using System.Diagnostics;

namespace ConnectionGuardian;

/// <summary>
/// Background monitor for the game's server connections. Every poll it reads the kernel's view of
/// each connection (TCP_INFO), detects outages and recoveries, and keeps the sockets tuned so the
/// client retransmits at least every <see cref="Configuration.RtoMaxMs"/> instead of backing off
/// for up to two minutes.
/// </summary>
public sealed class Guardian : IDisposable
{
    private const int PollIntervalMs = 250;
    private const int RescanIntervalMs = 2000;
    private const int MaxEvents = 300;
    private const int IpProtoTcp = 6;

    private readonly Configuration configuration;
    private readonly string logPath;
    private readonly Action<string, bool> notify;
    private readonly object sync = new();
    private readonly Dictionary<string, Connection> connections = [];
    private readonly LinkedList<GuardianEvent> events = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;
    private readonly Stopwatch sinceRescan = Stopwatch.StartNew();
    private bool forceRescan = true;

    public Guardian(Configuration configuration, string logDirectory, Action<string, bool> notify)
    {
        this.configuration = configuration;
        this.notify = notify;
        Directory.CreateDirectory(logDirectory);
        logPath = Path.Combine(logDirectory, "events.log");
        thread = new Thread(Run) { IsBackground = true, Name = "Connection Guardian" };
        thread.Start();
    }

    public string Status { get; private set; } = "Starting...";
    public bool Available { get; private set; }
    public string LogPath => logPath;

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
        lock (sync)
        {
            foreach (var connection in connections.Values)
                connection.Dirty = true;
        }

        forceRescan = true;
    }

    public void Dispose()
    {
        stop.Cancel();
        thread.Join(2000);
        lock (sync)
        {
            foreach (var connection in connections.Values)
                Untune(connection);
        }

        stop.Dispose();
    }

    private void Run()
    {
        if (!LinuxTcp.Initialize(out var error))
        {
            Status = $"Disabled: {error}";
            AddEvent($"Connection Guardian cannot run: {error}", true);
            return;
        }

        Available = true;
        Status = $"Active (Linux pid {LinuxTcp.LinuxPid}, {LinuxTcp.SyscallPath})";
        AddEvent($"Started. {Status}", false);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                AddEvent($"Monitor error: {ex.Message}", false);
            }

            stop.Token.WaitHandle.WaitOne(PollIntervalMs);
        }
    }

    private void Poll()
    {
        if (forceRescan || sinceRescan.ElapsedMilliseconds >= RescanIntervalMs)
        {
            forceRescan = false;
            sinceRescan.Restart();
            Rescan(LinuxTcp.ListTcpSockets());
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
                    continue;

                var key = Key(socket);
                if (connections.ContainsKey(key))
                    continue;

                var connection = new Connection(socket);
                connections[key] = connection;
                ApplyTuning(connection);
                AddEvent($"{connection.Name}: game connection found{TuningSummary(connection)}", false);
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
        connection.Tuned = rc1 == 0 && rc2 == 0;
        connection.TuneError = connection.Tuned
            ? null
            : $"setsockopt failed (TCP_RTO_MAX_MS: {Errno(rc1)}, TCP_USER_TIMEOUT: {Errno(rc2)}). TCP_RTO_MAX_MS needs Linux 6.15+.";
        if (connection.TuneError != null)
            AddEvent($"{connection.Name}: {connection.TuneError}", false);
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
        connection.Info = info;
        connection.HasInfo = true;

        if (previous.State != info.State && connection.HasPreviousInfo)
            AddEvent($"{connection.Name}: TCP state {StateName(previous.State)} -> {StateName(info.State)}{StateMeaning(info.State)}", info.State != 1);
        connection.HasPreviousInfo = true;

        // Data is outstanding and the server has not acknowledged anything for a while. A single lost
        // packet also causes backoff, so backoff alone is not treated as an outage.
        var stalled = info.Unacked > 0 && info.LastAckRecvMs >= configuration.StallAckMs;
        var now = DateTime.Now;
        if (stalled && connection.StallStart == null)
        {
            connection.StallStart = now.AddMilliseconds(-Math.Min(info.LastAckRecvMs, 60000));
            connection.StallStartRetrans = info.TotalRetrans;
            connection.Outages++;
            AddEvent($"{connection.Name}: OUTAGE - no ACK from server for {info.LastAckRecvMs} ms, {info.Unacked} packets unacknowledged. " +
                     (connection.Tuned ? $"Retrying every <= {configuration.RtoMaxMs} ms." : "Socket NOT tuned: kernel backoff can grow up to 120 s."), true);
        }
        else if (!stalled && connection.StallStart != null)
        {
            var duration = now - connection.StallStart.Value;
            AddEvent($"{connection.Name}: RECOVERED after {duration.TotalSeconds:F1} s " +
                     $"({info.TotalRetrans - connection.StallStartRetrans} retransmissions, " +
                     $"last data from server {info.LastDataRecvMs} ms ago).", true);
            connection.StallStart = null;
        }

        if (connection.StallStart != null)
        {
            var since = (now - connection.StallStart.Value).TotalSeconds;
            if (since >= connection.NextStallReport)
            {
                AddEvent($"{connection.Name}: still out after {since:F0} s (rto {info.RtoUs / 1000} ms, backoff {info.Backoff}, " +
                         $"retransmits {info.Retransmits}, last ACK {info.LastAckRecvMs} ms ago, last data {info.LastDataRecvMs} ms ago)", false);
                connection.NextStallReport = since + 10;
            }
        }
        else
        {
            connection.NextStallReport = 10;
        }
    }

    private void Closed(Connection connection)
    {
        connections.Remove(Key(connection.Socket));
        var last = connection.HasInfo ? connection.Info.State : (byte)0;
        var why = last switch
        {
            8 => "the server closed it (it sent FIN while we were waiting)",
            7 => "the connection was reset or timed out by the kernel",
            1 => "the game closed it while it still looked healthy to the kernel",
            _ => $"last TCP state {StateName(last)}",
        };
        var during = connection.StallStart != null
            ? $" during an outage of {(DateTime.Now - connection.StallStart.Value).TotalSeconds:F1} s"
            : string.Empty;
        AddEvent($"{connection.Name}: connection CLOSED{during}: {why}.", connection.StallStart != null || last != 1);
    }

    private string TuningSummary(Connection connection) => connection.Tuned
        ? $", tuned (retry every <= {configuration.RtoMaxMs} ms, give up after {configuration.UserTimeoutSeconds} s)"
        : configuration.TuneSockets ? ", tuning FAILED" : ", tuning disabled";

    private void AddEvent(string message, bool important)
    {
        var entry = new GuardianEvent(DateTime.Now, message, important);
        lock (sync)
        {
            events.AddFirst(entry);
            while (events.Count > MaxEvents)
                events.RemoveLast();
        }

        try
        {
            File.AppendAllText(logPath, $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never break monitoring.
        }

        notify(message, important);
    }

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

    private sealed class Connection(TcpSocket socket)
    {
        private static int counter;

        public TcpSocket Socket { get; } = socket;
        public string Name { get; } = $"#{Interlocked.Increment(ref counter)} {socket.Remote}";
        public DateTime FirstSeen { get; } = DateTime.Now;
        public TcpInfo Info;
        public bool HasInfo;
        public bool HasPreviousInfo;
        public bool Dirty;
        public bool Tuned;
        public string? TuneError;
        public int OriginalRtoMax = -1;
        public int OriginalUserTimeout = -1;
        public DateTime? StallStart;
        public uint StallStartRetrans;
        public double NextStallReport = 10;
        public int Outages;

        public ConnectionSnapshot Snapshot() => new(
            Name,
            Socket.Remote.ToString(),
            Socket.Fd,
            Tuned,
            TuneError,
            HasInfo ? Info : null,
            StallStart,
            Outages,
            FirstSeen);
    }
}

public sealed record GuardianEvent(DateTime Time, string Message, bool Important);

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
