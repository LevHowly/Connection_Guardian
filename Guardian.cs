using System.Diagnostics;
using System.Globalization;

namespace ConnectionGuardian;

/// <summary>
/// Background monitor for the game's server connections. Every poll it reads the kernel's view of
/// each connection (TCP_INFO), detects outages and both directions of recovery, writes everything
/// to the session log, and keeps the sockets tuned so the client retransmits at least every
/// <see cref="Configuration.RtoMaxMs"/> instead of backing off for up to two minutes.
/// </summary>
public sealed class Guardian : IDisposable
{
    private const int PollIntervalMs = 250;
    private const int RescanIntervalMs = 2000;
    private const int NetworkIntervalMs = 1000;
    private const int HealthySampleSeconds = 10;
    private const int DetailAfterOutageSeconds = 30;
    private const int RxSilenceMs = 5000;
    private const int MaxEvents = 300;
    private const int IpProtoTcp = 6;

    private readonly Configuration configuration;
    private readonly SessionLog log;
    private readonly IReadOnlyList<(string Name, string Value)> environment;
    private readonly Action<string, bool> notify;
    private readonly object sync = new();
    private readonly Dictionary<string, Connection> connections = [];
    private readonly HashSet<string> otherRemotes = [];
    private readonly LinkedList<GuardianEvent> events = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;
    private readonly Stopwatch sinceRescan = Stopwatch.StartNew();
    private readonly Stopwatch sinceNetwork = Stopwatch.StartNew();
    private readonly NetworkWatch network = new();
    private long scriptLogPosition = -1;
    private DateTime? scriptCut;
    private DateTime? scriptRestored;
    private bool forceRescan = true;

    public Guardian(Configuration configuration, SessionLog log, IReadOnlyList<(string Name, string Value)> environment, Action<string, bool> notify)
    {
        this.configuration = configuration;
        this.log = log;
        this.environment = environment;
        this.notify = notify;
        thread = new Thread(Run) { IsBackground = true, Name = "Connection Guardian" };
        thread.Start();
    }

    public string Status { get; private set; } = "Starting...";
    public bool Available { get; private set; }
    public string NetworkSummary => network.Summary;

    public List<ConnectionSnapshot> GetConnections()
    {
        lock (sync)
            return connections.Values.Select(c => c.Snapshot()).OrderBy(c => c.Remote).ToList();
    }

    public List<GuardianEvent> GetEvents()
    {
        lock (sync)
            return events.ToList();
    }

    public void Rescan() => forceRescan = true;

    /// <summary>Re-applies (or removes) the tuning on the next poll after a settings change.</summary>
    public void SettingsChanged()
    {
        log.Write("CONFIG", SettingsLine());
        lock (sync)
        {
            foreach (var connection in connections.Values)
                connection.Dirty = true;
        }

        forceRescan = true;
    }

    /// <summary>Game-side events (login, logout, zone changes) to correlate with the network.</summary>
    public void GameEvent(string message, bool important)
    {
        AddEvent("GAME", message, important);
        if (!important)
            return;

        lock (sync)
        {
            foreach (var connection in connections.Values)
                SampleLocked(connection, "at game event");
        }
    }

    public void AddNote(string note)
    {
        if (!string.IsNullOrWhiteSpace(note))
            AddEvent("NOTE", note.Trim(), false);
    }

    public void Dispose()
    {
        stop.Cancel();
        thread.Join(2000);
        lock (sync)
        {
            foreach (var connection in connections.Values)
            {
                SampleLocked(connection, "plugin unloading");
                Untune(connection);
            }
        }

        log.Write("SESSION", "plugin unloaded, log closed");
        stop.Dispose();
    }

    private void Run()
    {
        WriteHeader();
        if (!LinuxTcp.Initialize(out var error))
        {
            Status = $"Disabled: {error}";
            AddEvent("ERROR", $"Connection Guardian cannot run: {error}", true);
            return;
        }

        Available = true;
        Status = $"Active (Linux pid {LinuxTcp.LinuxPid}, {LinuxTcp.SyscallPath})";
        AddEvent("SESSION", $"started. Wine: {LinuxTcp.WineVersion}. {Status}", false);
        AddEvent("SESSION", $"all TCP connections of the game right now: {DescribeAll(LinuxTcp.ListTcpSockets())}", false);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                AddEvent("ERROR", $"monitor error: {ex}", false);
            }

            stop.Token.WaitHandle.WaitOne(PollIntervalMs);
        }
    }

    private void WriteHeader()
    {
        log.WriteRaw("==== Connection Guardian session log ====");
        log.WriteRaw($"Started:      {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        foreach (var (name, value) in environment)
            log.WriteRaw($"{name + ":",-13} {value}");
        log.WriteRaw($"OS:           {OsDescription()}");
        log.WriteRaw($"Kernel:       {ReadTrimmed(@"Z:\proc\sys\kernel\osrelease") ?? "unknown (not Linux?)"}");
        log.WriteRaw($"TCP sysctls:  {Sysctls()}");
        log.WriteRaw($"Settings:     {SettingsLine()}");
        log.WriteRaw($"Session file: {log.FilePath}");
        log.WriteRaw($"Script log:   {SessionLog.ToLinuxPath(log.ScriptLogPath)} (written by scripts/drop_internet.sh, copied below as [SCRIPT])");
        log.WriteRaw("Legend: SAMPLE lines are the kernel's TCP_INFO for one game connection. Times are in ms:");
        log.WriteRaw("  rto=current retransmit timeout, bo=backoff count, rtx=retries of the current packet, unacked/lost=packets,");
        log.WriteRaw("  snd/rcv/ack=ms since we last sent data / received data / received an ACK, rtt=round trip,");
        log.WriteRaw("  retx=total retransmissions, nrto=total retransmit timeouts, rxB/txB=total bytes received/sent.");
        log.WriteRaw("  Samples every 10 s when healthy, every 250 ms during an outage and 30 s after it.");
        log.WriteRaw("==========================================");
    }

    private void Poll()
    {
        if (forceRescan || sinceRescan.ElapsedMilliseconds >= RescanIntervalMs)
        {
            forceRescan = false;
            sinceRescan.Restart();
            Rescan(LinuxTcp.ListTcpSockets());
        }

        PollScriptLog();
        var networkTick = sinceNetwork.ElapsedMilliseconds >= NetworkIntervalMs;
        if (networkTick)
        {
            sinceNetwork.Restart();
            var change = network.Poll(out var traffic);
            if (change != null)
                AddEvent("NET", change, change.Contains("CHANGED", StringComparison.Ordinal));

            bool detail;
            lock (sync)
                detail = connections.Values.Any(c => c.InDetail(DateTime.Now));
            if (detail && traffic.Length > 0)
                log.Write("NET", $"{network.Summary}; {traffic}");
        }

        lock (sync)
        {
            foreach (var connection in connections.Values.ToList())
            {
                if (!LinuxTcp.TryDescribe(connection.Socket.Fd, out var current))
                {
                    // A reset or timed-out socket has no peer any more, but its final state is still readable.
                    if (LinuxTcp.TryGetTcpInfo(connection.Socket.Fd, out var final) && final.State != 1)
                        Update(connection, final);
                    Closed(connection);
                    continue;
                }

                if (current != connection.Socket)
                {
                    Closed(connection);
                    continue;
                }

                if (connection.Dirty)
                    ApplyTuning(connection);

                if (LinuxTcp.TryGetTcpInfo(connection.Socket.Fd, out var info))
                    Update(connection, info);
            }
        }
    }

    private void Rescan(List<TcpSocket> sockets)
    {
        lock (sync)
        {
            foreach (var socket in sockets)
            {
                if (socket.Remote.Port < configuration.PortMin || socket.Remote.Port > configuration.PortMax)
                {
                    if (otherRemotes.Add(socket.Remote.ToString()))
                        log.Write("OTHER", $"TCP connection outside the game port range (not tuned): {socket.Local} -> {socket.Remote}");
                    continue;
                }

                var key = Key(socket);
                if (connections.ContainsKey(key))
                    continue;

                var connection = new Connection(socket);
                connections[key] = connection;
                ApplyTuning(connection);
                AddEvent("CONN", $"{connection.Name}: game connection found ({socket.Local} -> {socket.Remote}){TuningSummary(connection)}", false);
                if (LinuxTcp.TryGetTcpInfo(socket.Fd, out var info))
                    Update(connection, info);
            }
        }
    }

    private void ApplyTuning(Connection connection)
    {
        connection.Dirty = false;
        var fd = connection.Socket.Fd;
        if (!configuration.TuneSockets)
        {
            Untune(connection);
            log.Write("TUNE", $"{connection.Name}: tuning disabled in settings, stock kernel behaviour");
            return;
        }

        if (connection.OriginalRtoMax < 0)
        {
            connection.OriginalRtoMax = (int)LinuxTcp.GetIntOption(fd, IpProtoTcp, LinuxTcp.TcpRtoMaxMs);
            connection.OriginalUserTimeout = (int)LinuxTcp.GetIntOption(fd, IpProtoTcp, LinuxTcp.TcpUserTimeout);
        }

        var rtoMax = Math.Clamp(configuration.RtoMaxMs, 1000, 120000);
        var userTimeout = Math.Clamp(configuration.UserTimeoutSeconds, 30, 3600) * 1000;
        var rc1 = LinuxTcp.SetIntOption(fd, IpProtoTcp, LinuxTcp.TcpRtoMaxMs, rtoMax);
        var rc2 = LinuxTcp.SetIntOption(fd, IpProtoTcp, LinuxTcp.TcpUserTimeout, userTimeout);
        var readRto = LinuxTcp.GetIntOption(fd, IpProtoTcp, LinuxTcp.TcpRtoMaxMs);
        var readUser = LinuxTcp.GetIntOption(fd, IpProtoTcp, LinuxTcp.TcpUserTimeout);
        connection.Tuned = rc1 == 0 && rc2 == 0;
        connection.TuneError = connection.Tuned
            ? null
            : $"setsockopt failed (TCP_RTO_MAX_MS: {Errno(rc1)}, TCP_USER_TIMEOUT: {Errno(rc2)}). TCP_RTO_MAX_MS needs Linux 6.15+.";
        log.Write("TUNE", $"{connection.Name}: original rto_max={connection.OriginalRtoMax} user_timeout={connection.OriginalUserTimeout}; " +
                          $"set rto_max={rtoMax} ({Errno(rc1)}) user_timeout={userTimeout} ({Errno(rc2)}); read back rto_max={readRto} user_timeout={readUser}");
        if (connection.TuneError != null)
            AddEvent("TUNE", $"{connection.Name}: {connection.TuneError}", true);
    }

    private static void Untune(Connection connection)
    {
        if (!connection.Tuned || connection.OriginalRtoMax < 0)
            return;

        var fd = connection.Socket.Fd;
        if (LinuxTcp.TryDescribe(fd, out var current) && current == connection.Socket)
        {
            LinuxTcp.SetIntOption(fd, IpProtoTcp, LinuxTcp.TcpRtoMaxMs, connection.OriginalRtoMax);
            LinuxTcp.SetIntOption(fd, IpProtoTcp, LinuxTcp.TcpUserTimeout, Math.Max(0, connection.OriginalUserTimeout));
        }

        connection.Tuned = false;
    }

    private void Update(Connection connection, TcpInfo info)
    {
        var previous = connection.Info;
        var hadInfo = connection.HasInfo;
        connection.Info = info;
        connection.HasInfo = true;
        var now = DateTime.Now;

        if (hadInfo && previous.State != info.State)
        {
            connection.DetailUntil = now.AddSeconds(DetailAfterOutageSeconds);
            AddEvent("CONN", $"{connection.Name}: TCP state {StateName(previous.State)} -> {StateName(info.State)}{StateMeaning(info.State)}", info.State != 1);
        }

        // Data is outstanding and the server has not acknowledged anything for a while. A single lost
        // packet also causes backoff, so backoff alone is not treated as an outage.
        var stalled = info.Unacked > 0 && info.LastAckRecvMs >= configuration.StallAckMs;
        if (stalled && connection.Outage == null)
        {
            connection.Outage = new Outage(now.AddMilliseconds(-Math.Min(info.LastAckRecvMs, 60000)), info);
            connection.Outages++;
            AddEvent("OUTAGE", $"{connection.Name}: OUTAGE #{connection.Outages} - no ACK from server for {info.LastAckRecvMs} ms, " +
                               $"{info.Unacked} packets unacknowledged, last data from server {info.LastDataRecvMs} ms ago. " +
                               (connection.Tuned ? $"Retrying every <= {configuration.RtoMaxMs} ms." : "Socket NOT tuned: kernel backoff can grow up to 120 s.") +
                               $" Network: {network.Summary}", true);
        }

        if (connection.Outage is { } outage)
        {
            connection.DetailUntil = now.AddSeconds(DetailAfterOutageSeconds);
            outage.MaxBackoff = Math.Max(outage.MaxBackoff, info.Backoff);
            if (!stalled && outage.AckBack == null)
            {
                outage.AckBack = now;
                AddEvent("RECOVER", $"{connection.Name}: PC -> server works again: server ACKs our data after {Seconds(now - outage.Start)} s{SinceRestore(now)} " +
                                    $"({info.TotalRetrans - outage.StartInfo.TotalRetrans} retransmissions, max backoff {outage.MaxBackoff})", true);
            }

            if (info.BytesReceived > outage.StartInfo.BytesReceived && outage.DataBack == null)
            {
                outage.DataBack = now;
                AddEvent("RECOVER", $"{connection.Name}: server -> PC works again: game data received after {Seconds(now - outage.Start)} s{SinceRestore(now)}", true);
            }

            if (outage.AckBack != null && outage.DataBack != null)
            {
                Summarize(connection, outage, "recovered");
                connection.Outage = null;
            }
            else
            {
                var since = (now - outage.Start).TotalSeconds;
                if (since >= outage.NextReport)
                {
                    AddEvent("OUTAGE", $"{connection.Name}: still out after {since:F0} s (PC->server {(outage.AckBack != null ? "ok" : "waiting")}, " +
                                       $"server->PC {(outage.DataBack != null ? "ok" : "waiting")}, rto {info.RtoUs / 1000} ms, backoff {info.Backoff}, " +
                                       $"last ACK {info.LastAckRecvMs} ms ago, last data {info.LastDataRecvMs} ms ago). Network: {network.Summary}", false);
                    outage.NextReport = since + 10;
                }
            }
        }

        // The game receives data continuously; a long silence without unacked data is also worth noting.
        var rxSilent = info.LastDataRecvMs >= RxSilenceMs && connection.Outage == null && info.State == 1;
        if (rxSilent && !connection.RxSilent)
        {
            connection.DetailUntil = now.AddSeconds(DetailAfterOutageSeconds);
            AddEvent("SILENCE", $"{connection.Name}: no data from server for {info.LastDataRecvMs} ms (nothing unacknowledged, so we may simply have nothing to send). Network: {network.Summary}", false);
        }
        else if (!rxSilent && connection.RxSilent && connection.Outage == null)
        {
            log.Write("SILENCE", $"{connection.Name}: server data flowing again");
        }

        connection.RxSilent = rxSilent;

        if (connection.InDetail(now) || (now - connection.LastSample).TotalSeconds >= HealthySampleSeconds)
            SampleLocked(connection, null);
    }

    private void Summarize(Connection connection, Outage outage, string end)
    {
        var cutText = scriptCut is { } cut && Math.Abs((cut - outage.Start).TotalSeconds) < 120
            ? $"; script cut the network at {cut:HH:mm:ss.fff}"
            : string.Empty;
        var restoreText = scriptRestored is { } restore && restore > outage.Start
            ? $"{cutText}; network restored by script at {restore:HH:mm:ss.fff} (network was down {Seconds(restore - outage.Start)} s)"
            : string.Empty;
        AddEvent("SUMMARY", $"{connection.Name}: outage {end}. Started {outage.Start:HH:mm:ss.fff}{restoreText}; " +
                            $"PC->server back {(outage.AckBack is { } a ? $"+{Seconds(a - outage.Start)} s" : "never")}, " +
                            $"server->PC back {(outage.DataBack is { } d ? $"+{Seconds(d - outage.Start)} s" : "never")}; " +
                            $"retransmissions {connection.Info.TotalRetrans - outage.StartInfo.TotalRetrans}, max backoff {outage.MaxBackoff}, tuned {(connection.Tuned ? "yes" : "no")}", true);
    }

    private void SampleLocked(Connection connection, string? reason)
    {
        if (!connection.HasInfo)
            return;

        var i = connection.Info;
        connection.LastSample = DateTime.Now;
        log.Write("SAMPLE", $"{connection.Name} {StateName(i.State)}/{i.CaStateName} rto={i.RtoUs / 1000} bo={i.Backoff} rtx={i.Retransmits} " +
                            $"unacked={i.Unacked} lost={i.Lost} snd={i.LastDataSentMs} rcv={i.LastDataRecvMs} ack={i.LastAckRecvMs} " +
                            $"rtt={i.RttUs / 1000.0:F1} retx={i.TotalRetrans} nrto={i.TotalRto} rxB={i.BytesReceived} txB={i.BytesSent}" +
                            (reason != null ? $" ({reason})" : string.Empty));
    }

    private void Closed(Connection connection)
    {
        connections.Remove(Key(connection.Socket));
        SampleLocked(connection, "last sample before close");
        var last = connection.HasInfo ? connection.Info.State : (byte)0;
        var why = last switch
        {
            8 => "the SERVER closed it (it sent FIN)",
            7 => "the connection was RESET or timed out by the kernel",
            1 => "the GAME closed it while it still looked healthy to the kernel",
            _ => $"last TCP state {StateName(last)}",
        };
        var during = connection.Outage != null
            ? $" during an outage of {Seconds(DateTime.Now - connection.Outage.Start)} s"
            : string.Empty;
        AddEvent("CLOSE", $"{connection.Name}: connection CLOSED{during}: {why}. Network: {network.Summary}", connection.Outage != null || last != 1);
        if (connection.Outage != null)
            Summarize(connection, connection.Outage, "ended with the connection closing");
    }

    /// <summary>Copies new lines written by scripts/drop_internet.sh into this session log.</summary>
    private void PollScriptLog()
    {
        try
        {
            var path = log.ScriptLogPath;
            if (!File.Exists(path))
            {
                scriptLogPosition = 0;
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (scriptLogPosition < 0 || scriptLogPosition > stream.Length)
                scriptLogPosition = stream.Length; // only lines written during this session
            stream.Position = scriptLogPosition;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                    continue;

                var important = line.Contains(" CUT ", StringComparison.Ordinal) || line.Contains(" RESTORED ", StringComparison.Ordinal);
                AddEvent("SCRIPT", line, important);
                if (line.Length >= 23 && DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                {
                    if (line.Contains(" CUT ", StringComparison.Ordinal))
                        scriptCut = time;
                    else if (line.Contains(" RESTORED ", StringComparison.Ordinal))
                        scriptRestored = time;
                }
            }

            scriptLogPosition = stream.Length;
        }
        catch (Exception)
        {
        }
    }

    private string SinceRestore(DateTime now) =>
        scriptRestored is { } restored && (now - restored).TotalSeconds is >= 0 and < 600
            ? $" ({Seconds(now - restored)} s after the script restored the network)"
            : string.Empty;

    private string TuningSummary(Connection connection) => connection.Tuned
        ? $", tuned (retry every <= {configuration.RtoMaxMs} ms, give up after {configuration.UserTimeoutSeconds} s)"
        : configuration.TuneSockets ? ", tuning FAILED" : ", tuning disabled";

    private string SettingsLine() =>
        $"TuneSockets={configuration.TuneSockets} RtoMaxMs={configuration.RtoMaxMs} UserTimeoutSeconds={configuration.UserTimeoutSeconds} " +
        $"Ports={configuration.PortMin}-{configuration.PortMax} StallAckMs={configuration.StallAckMs} Chat={configuration.ChatNotifications}";

    private void AddEvent(string category, string message, bool important)
    {
        log.Write(category, message);
        var entry = new GuardianEvent(DateTime.Now, category, message, important);
        lock (sync)
        {
            events.AddFirst(entry);
            while (events.Count > MaxEvents)
                events.RemoveLast();
        }

        notify(message, important);
    }

    private static string DescribeAll(List<TcpSocket> sockets) =>
        sockets.Count == 0 ? "none" : string.Join(", ", sockets.Select(s => $"{s.Local} -> {s.Remote}"));

    private static string OsDescription()
    {
        try
        {
            var line = File.ReadLines(@"Z:\etc\os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
            return line?["PRETTY_NAME=".Length..].Trim('"') ?? "unknown";
        }
        catch (Exception)
        {
            return $"unknown ({Environment.OSVersion})";
        }
    }

    private static string Sysctls()
    {
        string[] names = ["tcp_retries2", "tcp_rto_max_ms", "tcp_rto_min_us", "tcp_frto", "tcp_early_retrans", "tcp_keepalive_time", "tcp_congestion_control"];
        return string.Join(" ", names.Select(n => $"{n}={ReadTrimmed($@"Z:\proc\sys\net\ipv4\{n}") ?? "n/a"}"));
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Key(TcpSocket socket) => $"{socket.Fd}|{socket.Local}|{socket.Remote}";

    private static string Errno(long rc) => rc == 0 ? "ok" : $"errno {-rc}";

    public static string StateName(byte state) => state switch
    {
        1 => "ESTABLISHED",
        2 => "SYN_SENT",
        3 => "SYN_RECV",
        4 => "FIN_WAIT1",
        5 => "FIN_WAIT2",
        6 => "TIME_WAIT",
        7 => "CLOSE",
        8 => "CLOSE_WAIT",
        9 => "LAST_ACK",
        10 => "LISTEN",
        11 => "CLOSING",
        _ => state.ToString(),
    };

    private static string StateMeaning(byte state) => state switch
    {
        8 => " (server closed the connection)",
        7 => " (connection reset or timed out)",
        4 => " (game is closing the connection)",
        _ => string.Empty,
    };

    private sealed class Outage(DateTime start, TcpInfo startInfo)
    {
        public DateTime Start { get; } = start;
        public TcpInfo StartInfo { get; } = startInfo;
        public DateTime? AckBack;
        public DateTime? DataBack;
        public int MaxBackoff;
        public double NextReport = 10;
    }

    private sealed class Connection(TcpSocket socket)
    {
        private static int counter;

        public TcpSocket Socket { get; } = socket;
        public string Name { get; } = $"#{Interlocked.Increment(ref counter)} {socket.Remote}";
        public DateTime FirstSeen { get; } = DateTime.Now;
        public TcpInfo Info;
        public bool HasInfo;
        public bool Dirty;
        public bool Tuned;
        public string? TuneError;
        public int OriginalRtoMax = -1;
        public int OriginalUserTimeout = -1;
        public Outage? Outage;
        public bool RxSilent;
        public DateTime DetailUntil;
        public DateTime LastSample;
        public int Outages;

        public bool InDetail(DateTime now) => Outage != null || now < DetailUntil || (HasInfo && Info.State != 1);

        public ConnectionSnapshot Snapshot() => new(
            Name,
            Socket.Remote.ToString(),
            Socket.Fd,
            Tuned,
            TuneError,
            HasInfo ? Info : null,
            Outage?.Start,
            Outages,
            FirstSeen);
    }
}

public sealed record GuardianEvent(DateTime Time, string Category, string Message, bool Important);

public sealed record ConnectionSnapshot(
    string Name,
    string Remote,
    int Fd,
    bool Tuned,
    string? TuneError,
    TcpInfo? Info,
    DateTime? StallStart,
    int Outages,
    DateTime FirstSeen);
