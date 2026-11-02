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
    private const int HealthySampleSeconds = 5;
    private const int DetailSampleMs = 1000;
    private const int DetailAfterOutageSeconds = 30;
    private const int RxSilenceMs = 5000;
    private const int MaxEvents = 300;

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
    private DateTime? scriptReachable;
    private bool forceRescan = true;
    private ITcpBackend? backend;

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
    public string PlatformName => backend?.Name ?? (Platform.IsWine ? "Linux (Wine)" : "Windows");
    public bool CanCapRetryInterval => backend?.CanCapRetryInterval ?? Platform.IsWine;
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
        // A socket scan can take a few seconds; the thread must be gone before the backend is shut down.
        var stopped = thread.Join(10000);
        lock (sync)
        {
            foreach (var connection in connections.Values)
            {
                SampleLocked(connection, "plugin unloading");
                Untune(connection);
            }
        }

        if (stopped)
            backend?.Shutdown();
        else
            log.Write("SESSION", "monitor thread did not stop in time");
        log.Write("SESSION", "plugin unloaded, log closed");
    }

    private void Run()
    {
        try
        {
            RunCore();
        }
        catch (Exception ex)
        {
            // An exception escaping a thread would take the game down with it.
            Status = $"Stopped: {ex.Message}";
            log.Write("ERROR", $"monitor stopped: {ex}");
        }
    }

    private void RunCore()
    {
        // Under Wine the Linux kernel is reachable and allows the full fix; the Windows API is the
        // fallback (e.g. Wine on macOS) and the only option on Windows.
        ITcpBackend[] candidates = Platform.IsWine ? [new LinuxBackend(), new WindowsBackend()] : [new WindowsBackend()];
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.Initialize(out var candidateError))
                {
                    backend = candidate;
                    break;
                }

                errors.Add($"{candidate.Name}: {candidateError}");
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate.Name}: {ex.Message}");
            }
        }

        WriteHeader();
        foreach (var error in errors)
            log.Write("SESSION", $"backend not usable: {error}");

        if (backend == null)
        {
            Status = $"Disabled: {string.Join(" / ", errors)}";
            AddEvent("ERROR", $"Connection Guardian cannot run: {string.Join(" / ", errors)}", true);
            return;
        }

        backend.Diagnostic = message => log.Write("DIAG", message);
        Available = true;
        Status = $"Active ({backend.Description})";
        AddEvent("SESSION", $"started. {Status}", false);
        AddEvent("SESSION", $"all TCP connections of the game right now: {DescribeAll(backend.ListTcpSockets())}", false);

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

            if (stop.Token.WaitHandle.WaitOne(PollIntervalMs))
                break;
        }
    }

    private void WriteHeader()
    {
        log.WriteRaw("==== Connection Guardian session log ====");
        log.WriteRaw($"Started:      {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        foreach (var (name, value) in environment)
            log.WriteRaw($"{name + ":",-13} {value}");
        log.WriteRaw($"Platform:     {(backend != null ? backend.Name : "none usable")} (Wine: {(Platform.IsWine ? "yes" : "no")})");
        if (backend != null)
        {
            foreach (var (name, value) in backend.HeaderLines())
                log.WriteRaw($"{name + ":",-13} {value}");
        }

        log.WriteRaw($"Settings:     {SettingsLine()}");
        log.WriteRaw($"Session file: {log.FilePath}");
        log.WriteRaw($"Script log:   {SessionLog.ToLinuxPath(log.ScriptLogPath)} (written by scripts/drop_internet.sh, copied below as [SCRIPT])");
        foreach (var line in backend?.LegendLines() ?? [])
            log.WriteRaw(line);
        log.WriteRaw("  Samples every 5 s when healthy, every 1 s during an outage and 30 s after it (events are timed to 250 ms).");
        log.WriteRaw("==========================================");
    }

    private void Poll()
    {
        if (forceRescan || sinceRescan.ElapsedMilliseconds >= RescanIntervalMs)
        {
            forceRescan = false;
            sinceRescan.Restart();
            Rescan(backend!.ListTcpSockets());
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
                if (!backend!.TryDescribe(connection.Socket.Fd, out var current))
                {
                    // A reset or timed-out socket has no peer any more, but its final state is still readable.
                    if (backend.TryGetTcpInfo(connection.Socket, out var final) && final.State != 1)
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

                if (backend.TryGetTcpInfo(connection.Socket, out var info))
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
                if (backend!.TryGetTcpInfo(socket, out var info))
                    Update(connection, info);
            }
        }
    }

    private void ApplyTuning(Connection connection)
    {
        connection.Dirty = false;
        if (!configuration.TuneSockets)
        {
            Untune(connection);
            log.Write("TUNE", $"{connection.Name}: tuning disabled in settings, stock {(backend!.CanCapRetryInterval ? "kernel" : "Windows")} behaviour");
            return;
        }

        var result = backend!.Tune(connection.Socket, configuration, connection.Memory);
        connection.Tuned = result.Ok;
        connection.TuneError = result.Error;
        log.Write("TUNE", $"{connection.Name}: {result.LogLine}");
        if (connection.TuneError != null)
            AddEvent("TUNE", $"{connection.Name}: {connection.TuneError}", true);
    }

    private void Untune(Connection connection)
    {
        if (!connection.Tuned)
            return;

        backend?.Untune(connection.Socket, connection.Memory);
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

        // We are waiting on the server when data is unacknowledged, or when the kernel is backing off
        // with nothing in flight (it cannot transmit at all, e.g. the route is gone). The wait is timed
        // from when that started, not from the last ACK: a quiet connection that sends a heartbeat
        // every 10 s would otherwise look stalled for a moment on every heartbeat.
        var waiting = info.Unacked > 0 || info.Backoff > 0;
        if (!waiting)
            connection.WaitingSince = null;
        else
            connection.WaitingSince ??= now.AddMilliseconds(-Math.Min(info.LastAckRecvMs, PollIntervalMs));
        var waitedMs = waiting ? Math.Min(info.LastAckRecvMs, (long)(now - connection.WaitingSince!.Value).TotalMilliseconds) : 0;
        var stalled = waiting && waitedMs >= configuration.StallAckMs;
        if (stalled && connection.Outage == null)
        {
            connection.Outage = new Outage(now.AddMilliseconds(-Math.Min(waitedMs, 60000)), info);
            connection.Outages++;
            AddEvent("OUTAGE", $"{connection.Name}: OUTAGE #{connection.Outages} - no ACK from server for {waitedMs} ms " +
                               $"(last ACK {info.LastAckRecvMs} ms ago), {info.Unacked} packets unacknowledged, backoff {info.Backoff}, " +
                               $"last data from server {info.LastDataRecvMs} ms ago. " +
                               (connection.Tuned ? $"Tuned: {backend!.TunedBehaviour(configuration)}." : backend!.StockBehaviour) +
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
                                    $"({info.TotalRetrans - outage.StartInfo.TotalRetrans} {RetransUnit(info)}, max backoff {outage.MaxBackoff})", true);
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
                                       $"server->PC {(outage.DataBack != null ? "ok" : "waiting")}, {(info.Windows ? string.Empty : $"rto {info.RtoUs / 1000} ms, ")}backoff {info.Backoff}, " +
                                       $"last ACK {info.LastAckRecvMs} ms ago, last data {info.LastDataRecvMs} ms ago). Network: {network.Summary}", false);
                    outage.NextReport = since + 10;
                }
            }
        }

        // Some game connections only hear from the server every ~10 s, so a silence is only reported when
        // it is clearly longer than the gaps this connection normally has.
        if (hadInfo && info.LastDataRecvMs < previous.LastDataRecvMs && connection.Outage == null)
            connection.RecordGap(previous.LastDataRecvMs);
        var rxSilent = info.LastDataRecvMs >= connection.SilenceThresholdMs(RxSilenceMs) && connection.Outage == null && info.State == 1;
        if (rxSilent && !connection.RxSilent)
        {
            connection.DetailUntil = now.AddSeconds(DetailAfterOutageSeconds);
            AddEvent("SILENCE", $"{connection.Name}: no data from server for {info.LastDataRecvMs} ms (usual longest gap {connection.UsualGapMs} ms; nothing unacknowledged, so we may simply have nothing to send). Network: {network.Summary}", false);
        }
        else if (!rxSilent && connection.RxSilent && connection.Outage == null)
        {
            log.Write("SILENCE", $"{connection.Name}: server data flowing again");
        }

        connection.RxSilent = rxSilent;

        // Connections are polled every 250 ms so events are timed precisely, but samples are written less often.
        var sinceSample = (now - connection.LastSample).TotalMilliseconds;
        if (sinceSample >= (connection.InDetail(now) ? DetailSampleMs : HealthySampleSeconds * 1000))
            SampleLocked(connection, null);
    }

    private void Summarize(Connection connection, Outage outage, string end)
    {
        var cutText = scriptCut is { } cut && Math.Abs((cut - outage.Start).TotalSeconds) < 120
            ? $"; script cut the network at {cut:HH:mm:ss.fff}"
            : string.Empty;
        var restoreText = scriptRestored is { } restore && restore > outage.Start
            ? $"{cutText}; network restored by script at {restore:HH:mm:ss.fff}" +
              (scriptCut is { } c && c < restore ? $" (cut for {Seconds(restore - c)} s)" : string.Empty)
            : string.Empty;
        if (restoreText.Length > 0 && scriptReachable is { } reachable && reachable > scriptRestored)
        {
            restoreText += $"; internet reachable at {reachable:HH:mm:ss.fff}";
            if (outage.AckBack is { } ack)
                restoreText += $", PC->server back {Seconds(ack - reachable)} s after that";
            if (outage.DataBack is { } data)
                restoreText += $", server->PC back {Seconds(data - reachable)} s after that";
        }
        AddEvent("SUMMARY", $"{connection.Name}: outage {end}. Started {outage.Start:HH:mm:ss.fff}{restoreText}; " +
                            $"PC->server back {(outage.AckBack is { } a ? $"+{Seconds(a - outage.Start)} s" : "never")}, " +
                            $"server->PC back {(outage.DataBack is { } d ? $"+{Seconds(d - outage.Start)} s" : "never")}; " +
                            $"{RetransUnit(connection.Info)} {connection.Info.TotalRetrans - outage.StartInfo.TotalRetrans}, max backoff {outage.MaxBackoff}, tuned {(connection.Tuned ? "yes" : "no")}", true);
    }

    private void SampleLocked(Connection connection, string? reason)
    {
        if (!connection.HasInfo)
            return;

        var i = connection.Info;
        connection.LastSample = DateTime.Now;
        log.Write("SAMPLE", $"{connection.Name} {backend!.FormatSample(i)}" + (reason != null ? $" ({reason})" : string.Empty));
    }

    private void Closed(Connection connection)
    {
        connections.Remove(Key(connection.Socket));
        backend?.Forget(connection.Socket);
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
                    else if (line.Contains(" internet reachable again", StringComparison.Ordinal))
                        scriptReachable = time;
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

    private static string RetransUnit(TcpInfo info) => info.Windows ? "bytes retransmitted" : "retransmissions";

    private string TuningSummary(Connection connection) => connection.Tuned
        ? $", tuned ({backend!.TunedBehaviour(configuration)})"
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

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Key(TcpSocket socket) => $"{socket.Fd}|{socket.Local}|{socket.Remote}";

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
        public TuneMemory Memory { get; } = new();
        public Outage? Outage;
        public bool RxSilent;
        public DateTime? WaitingSince;
        private readonly Queue<long> gaps = new();

        public long UsualGapMs => gaps.Count == 0 ? 0 : gaps.Max();

        public void RecordGap(long ms)
        {
            gaps.Enqueue(ms);
            if (gaps.Count > 20)
                gaps.Dequeue();
        }

        public long SilenceThresholdMs(long minimum) => Math.Min(Math.Max(minimum, UsualGapMs * 2), 60000);
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
