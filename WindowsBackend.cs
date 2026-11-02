using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ConnectionGuardian;

/// <summary>
/// Windows: finds the game's socket handles in its own handle table, reads SIO_TCP_INFO, and sets
/// TCP_MAXRT so Windows keeps retransmitting through an outage instead of dropping the connection
/// after its default ~5 retransmissions. Windows offers no way to cap the retry interval itself.
/// </summary>
public sealed unsafe class WindowsBackend : ITcpBackend
{
    private const int SolSocket = 0xFFFF;
    private const int SoType = 0x1008;
    private const int SockStream = 1;
    private const int IpProtoTcp = 6;
    private const int TcpMaxRt = 5;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint SioTcpInfo = 0xD8000027;
    private const int ProcessHandleInformation = 51;
    private const int SystemExtendedHandleInformation = 64;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int TcpTableOwnerPidConnections = 4;
    private const int ProbeTimeoutMs = 3000;
    private const int MaxStuckProbes = 3;

    private readonly object probeSync = new();
    private readonly Dictionary<TcpSocket, Tracker> trackers = [];
    private readonly Dictionary<string, int> resolved = [];
    private readonly HashSet<long> skipHandles = [];
    private string lastScanKey = string.Empty;
    private readonly List<Thread> stuckThreads = [];
    private bool wsaStarted;
    private long probing;
    private int fileTypeIndex = -1;
    private uint socketAccess;
    private uint tcpInfoVersion = 1;
    private string handleMethod = "none";

    public string Name => Platform.IsWine ? "Windows API under Wine (fallback)" : "Windows";

    public string Description => $"{Name}, SIO_TCP_INFO v{tcpInfoVersion}, sockets found via {handleMethod}";

    public bool CanCapRetryInterval => false;

    public Action<string>? Diagnostic { get; set; }

    public string StockBehaviour => "Socket NOT tuned: Windows drops the connection after about 5 unanswered retransmissions (TcpMaxDataRetransmissions).";

    public string TunedBehaviour(Configuration configuration) =>
        $"Windows keeps retrying for up to {TimeoutSeconds(configuration)} s instead of giving up after ~5 retries; the wait between retries still doubles, up to 60 s";

    public bool Initialize(out string error)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            error = "Only x86-64 is supported.";
            return false;
        }

        var data = stackalloc byte[512];
        if (WSAStartup(0x0202, data) != 0)
        {
            error = "WSAStartup failed.";
            return false;
        }

        wsaStarted = true;

        // Our own throwaway socket tells us which handle type and access rights sockets have, so only
        // matching handles need to be probed.
        var probe = socket(AfInet, SockStream, IpProtoTcp);
        try
        {
            var handles = OwnHandles();
            if (handles == null)
            {
                error = "Cannot list the process's handles (NtQueryInformationProcess and NtQuerySystemInformation failed).";
                return false;
            }

            foreach (var (handle, type, access) in handles)
            {
                if (handle == probe)
                {
                    fileTypeIndex = type;
                    socketAccess = access;
                }
            }
        }
        finally
        {
            if (probe != -1)
                closesocket(probe);
        }

        error = string.Empty;
        return true;
    }

    public IEnumerable<(string Name, string Value)> HeaderLines()
    {
        yield return ("OS", WindowsVersion());
        yield return ("TCP registry", TcpRegistry());
        yield return ("Socket lookup", $"{handleMethod}, socket handle type {fileTypeIndex}, access 0x{socketAccess:X}");
    }

    public IEnumerable<string> LegendLines()
    {
        yield return "Legend: SAMPLE lines are Windows' SIO_TCP_INFO for one game connection, read every 250 ms. Times are in ms:";
        yield return "  inflight=bytes sent but not yet acknowledged, snd/rcv=ms since we last sent / received data, ack=ms since";
        yield return "  data was last acknowledged (while data is outstanding: how long we have been waiting for the ACK),";
        yield return "  rtt=round trip, to=retransmit timeouts since the last ACK (each one doubles the";
        yield return "  wait, up to 60 s), nrto=total timeouts, retxB=bytes retransmitted, rxB/txB=total bytes received/sent.";
    }

    public List<TcpSocket> ListTcpSockets()
    {
        var result = new List<TcpSocket>();
        var missing = new HashSet<string>();
        var table = OwnTcpTable();
        foreach (var (local, remote) in table)
        {
            var key = PairKey(local, remote);
            if (resolved.TryGetValue(key, out var handle) && TryDescribe(handle, out var socket) && PairKey(socket.Local, socket.Remote) == key)
                result.Add(socket);
            else
                missing.Add(key);
        }

        foreach (var stale in resolved.Keys.Where(k => !table.Any(p => PairKey(p.Local, p.Remote) == k)).ToList())
            resolved.Remove(stale);

        if (missing.Count == 0)
            return result;

        // Only scan the handle table when the set of unknown connections changes.
        var scanKey = string.Join(",", missing.Order());
        if (scanKey == lastScanKey)
            return result;

        lastScanKey = scanKey;
        foreach (var socket in Scan(missing))
        {
            resolved[PairKey(socket.Local, socket.Remote)] = socket.Fd;
            missing.Remove(PairKey(socket.Local, socket.Remote));
            result.Add(socket);
        }

        if (missing.Count > 0)
            Diagnostic?.Invoke($"socket handle not found for {missing.Count} connection(s) of the game: {string.Join(", ", missing)}");
        return result;
    }

    public bool TryDescribe(int handle, out TcpSocket socket) => Probe(handle, out socket);

    public bool TryGetTcpInfo(TcpSocket socket, out TcpInfo info)
    {
        info = default;
        var buffer = stackalloc byte[256];
        uint returned = 0;
        var version = tcpInfoVersion;
        var rc = WSAIoctl(socket.Fd, SioTcpInfo, &version, 4, buffer, 256, &returned, 0, 0);
        if (rc != 0 && tcpInfoVersion == 1)
        {
            version = 0;
            rc = WSAIoctl(socket.Fd, SioTcpInfo, &version, 4, buffer, 256, &returned, 0, 0);
            if (rc == 0)
                tcpInfoVersion = 0;
        }

        if (!trackers.TryGetValue(socket, out var tracker))
            trackers[socket] = tracker = new Tracker();

        if (rc != 0 || returned < 88)
        {
            if (!tracker.ReportedError)
            {
                tracker.ReportedError = true;
                Diagnostic?.Invoke($"SIO_TCP_INFO failed for {socket.Remote}: {(rc != 0 ? $"WSA error {WSAGetLastError()}" : $"{returned} bytes returned")}");
            }

            return false;
        }

        info = Interpret(tracker, new ReadOnlySpan<byte>(buffer, (int)returned), Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>Converts TCP_INFO_v0 into Linux-style values, deriving the "ms since" fields from counter changes.</summary>
    internal static TcpInfo Interpret(Tracker tracker, ReadOnlySpan<byte> span, long now)
    {
        var state = BinaryPrimitives.ReadInt32LittleEndian(span);
        var rtt = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        var inFlight = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
        var bytesOut = BinaryPrimitives.ReadUInt64LittleEndian(span[48..]);
        var bytesIn = BinaryPrimitives.ReadUInt64LittleEndian(span[56..]);
        var bytesRetrans = BinaryPrimitives.ReadUInt32LittleEndian(span[68..]);
        var timeouts = BinaryPrimitives.ReadUInt32LittleEndian(span[80..]);

        // Windows does not report "time since last ACK", so it is derived from the counters. It is not
        // documented whether BytesOut includes retransmissions, so the server counts as answering when:
        // it sent us data (every segment carries an ACK), (sent - in flight - retransmitted) reaches a new
        // high, or (sent - in flight) reaches a new high during a poll without retransmissions or timeouts.
        var ackedExclRetrans = (long)bytesOut - inFlight - bytesRetrans;
        var ackedInclRetrans = (long)bytesOut - inFlight;
        if (!tracker.Started)
        {
            tracker.Started = true;
            tracker.LastAck = tracker.LastRecv = tracker.LastSent = now;
            tracker.MaxAckedExcl = ackedExclRetrans;
            tracker.MaxAckedIncl = ackedInclRetrans;
            tracker.TimeoutsAtAck = timeouts;
        }
        else
        {
            var received = bytesIn > tracker.BytesIn;
            var quietPoll = bytesRetrans == tracker.BytesRetrans && timeouts == tracker.Timeouts;
            if (received)
                tracker.LastRecv = now;
            if (bytesOut > tracker.BytesOut)
                tracker.LastSent = now;
            if (received || ackedExclRetrans > tracker.MaxAckedExcl || (quietPoll && ackedInclRetrans > tracker.MaxAckedIncl))
            {
                tracker.LastAck = now;
                tracker.TimeoutsAtAck = timeouts;
            }

            tracker.MaxAckedExcl = Math.Max(tracker.MaxAckedExcl, ackedExclRetrans);
            tracker.MaxAckedIncl = Math.Max(tracker.MaxAckedIncl, ackedInclRetrans);
        }

        tracker.BytesIn = bytesIn;
        tracker.BytesOut = bytesOut;
        tracker.BytesRetrans = bytesRetrans;
        tracker.Timeouts = timeouts;
        var timeoutsSinceAck = timeouts - Math.Min(timeouts, tracker.TimeoutsAtAck);

        // Windows may stop counting bytes in flight after a timeout; unanswered timeouts still mean unacked data.
        var unacked = inFlight > 0 ? inFlight : timeoutsSinceAck > 0 ? 1u : 0u;

        // While data is outstanding, "ack" is how long we have been waiting, so data sent after an idle
        // period is not mistaken for an outage before its ACK had time to arrive.
        if (unacked == 0)
            tracker.OutstandingSince = 0;
        else if (tracker.OutstandingSince == 0)
            tracker.OutstandingSince = now;
        var ackReference = unacked > 0 ? Math.Max(tracker.LastAck, tracker.OutstandingSince) : tracker.LastAck;
        return new TcpInfo
        {
            Windows = true,
            State = MapState(state),
            RttUs = rtt,
            InFlightBytes = inFlight,
            Unacked = unacked,
            Backoff = (byte)Math.Min(timeoutsSinceAck, 255),
            Retransmits = (byte)Math.Min(timeoutsSinceAck, 255),
            LastAckRecvMs = Elapsed(ackReference, now),
            LastDataRecvMs = Elapsed(tracker.LastRecv, now),
            LastDataSentMs = Elapsed(tracker.LastSent, now),
            TotalRetrans = bytesRetrans,
            TotalRto = (ushort)Math.Min(timeouts, ushort.MaxValue),
            BytesReceived = bytesIn,
            BytesSent = bytesOut,
        };
    }

    public TuneResult Tune(TcpSocket socket, Configuration configuration, TuneMemory memory)
    {
        if (!memory.Saved)
        {
            var (readOk, value, _) = GetIntOption(socket.Fd, IpProtoTcp, TcpMaxRt);
            memory.First = value;
            memory.Second = readOk ? 1 : 0;
            memory.Saved = true;
        }

        var seconds = TimeoutSeconds(configuration);
        var setOk = setsockopt(socket.Fd, IpProtoTcp, TcpMaxRt, &seconds, sizeof(int)) == 0;
        var setError = setOk ? 0 : WSAGetLastError();
        var (backOk, back, backError) = GetIntOption(socket.Fd, IpProtoTcp, TcpMaxRt);
        var line = $"original TCP_MAXRT={(memory.Second == 1 ? memory.First.ToString() : "unreadable")}; " +
                   $"set TCP_MAXRT={seconds} s ({(setOk ? "ok" : $"WSA error {setError}")}); read back {(backOk ? back.ToString() : $"WSA error {backError}")}";
        return new TuneResult(setOk, line, setOk ? null : $"setsockopt(TCP_MAXRT) failed with WSA error {setError}.");
    }

    public void Untune(TcpSocket socket, TuneMemory memory)
    {
        // 0 is not "default" for TCP_MAXRT, so only restore values that are safe to set again.
        if (!memory.Saved || memory.Second != 1 || (memory.First <= 0 && memory.First != -1))
            return;

        if (Probe(socket.Fd, out var current) && current == socket)
        {
            var value = (int)memory.First;
            setsockopt(socket.Fd, IpProtoTcp, TcpMaxRt, &value, sizeof(int));
        }
    }

    public void Forget(TcpSocket socket) => trackers.Remove(socket);

    public void Shutdown()
    {
        // A probe still stuck inside Winsock must not have Winsock torn down underneath it.
        stuckThreads.RemoveAll(t => !t.IsAlive);
        if (wsaStarted && stuckThreads.Count == 0)
        {
            wsaStarted = false;
            WSACleanup();
        }
    }

    public string FormatSample(TcpInfo i) =>
        $"{Guardian.StateName(i.State)} rtt={i.RttUs / 1000.0:F1} inflight={i.InFlightBytes} snd={i.LastDataSentMs} rcv={i.LastDataRecvMs} " +
        $"ack={i.LastAckRecvMs} to={i.Backoff} nrto={i.TotalRto} retxB={i.TotalRetrans} rxB={i.BytesReceived} txB={i.BytesSent}";

    private static int TimeoutSeconds(Configuration configuration) => Math.Clamp(configuration.UserTimeoutSeconds, 30, 3600);

    private static uint Elapsed(long from, long now) =>
        (uint)Math.Min(uint.MaxValue, (now - from) * 1000 / Stopwatch.Frequency);

    private static string PairKey(IPEndPoint local, IPEndPoint remote) => $"{local}->{remote}";

    private static (bool Ok, int Value, int Error) GetIntOption(int handle, int level, int option)
    {
        int value = 0;
        int length = sizeof(int);
        return getsockopt(handle, level, option, &value, &length) == 0 ? (true, value, 0) : (false, 0, WSAGetLastError());
    }

    private static byte MapState(int windowsState) => windowsState switch
    {
        0 => 7,   // CLOSED
        1 => 10,  // LISTEN
        2 => 2,   // SYN_SENT
        3 => 3,   // SYN_RCVD
        4 => 1,   // ESTABLISHED
        5 => 4,   // FIN_WAIT_1
        6 => 5,   // FIN_WAIT_2
        7 => 8,   // CLOSE_WAIT
        8 => 11,  // CLOSING
        9 => 9,   // LAST_ACK
        10 => 6,  // TIME_WAIT
        _ => 0,
    };

    private static bool Probe(nint handle, out TcpSocket socket)
    {
        socket = default;
        int type = 0;
        int length = sizeof(int);
        if (getsockopt(handle, SolSocket, SoType, &type, &length) != 0 || type != SockStream)
            return false;

        if (!TryEndpoint(handle, true, out var remote) || !TryEndpoint(handle, false, out var local))
            return false;

        socket = new TcpSocket((int)handle, local, remote);
        return true;
    }

    private static bool TryEndpoint(nint handle, bool peer, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        var address = stackalloc byte[128];
        int length = 128;
        if ((peer ? getpeername(handle, address, &length) : getsockname(handle, address, &length)) != 0)
            return false;

        var family = *(ushort*)address;
        var port = (address[2] << 8) | address[3];
        if (family == AfInet)
            endpoint = new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(address + 4, 4)), port);
        else if (family == AfInet6)
            endpoint = new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(address + 8, 16)), port);
        else
            return false;

        if (endpoint.Address.IsIPv4MappedToIPv6)
            endpoint = new IPEndPoint(endpoint.Address.MapToIPv4(), port);
        return true;
    }

    /// <summary>
    /// Probes candidate handles on a separate thread: Winsock calls on a non-socket handle with a
    /// blocked synchronous read could hang, and a hung probe must not stop the monitor.
    /// </summary>
    private List<TcpSocket> Scan(HashSet<string> wanted)
    {
        var found = new List<TcpSocket>();
        stuckThreads.RemoveAll(t => !t.IsAlive);
        if (stuckThreads.Count >= MaxStuckProbes)
            return found;

        var handles = OwnHandles();
        if (handles == null)
            return found;

        var known = resolved.Values.Select(v => (long)v).ToHashSet();
        var candidates = handles
            // Only handles that look exactly like our own socket are probed: Winsock calls on other
            // handles (e.g. a pipe with a blocked synchronous read) could hang.
            .Where(h => (fileTypeIndex < 0 || (h.Type == fileTypeIndex && h.Access == socketAccess)) && !skipHandles.Contains(h.Handle) && !known.Contains(h.Handle))
            .Select(h => h.Handle)
            .ToList();
        var remaining = new HashSet<string>(wanted);
        var thread = new Thread(() =>
        {
            foreach (var handle in candidates)
            {
                Interlocked.Exchange(ref probing, handle);
                if (!Probe((nint)handle, out var socket) || !remaining.Contains(PairKey(socket.Local, socket.Remote)))
                    continue;

                lock (probeSync)
                {
                    found.Add(socket);
                    remaining.Remove(PairKey(socket.Local, socket.Remote));
                    if (remaining.Count == 0)
                        return;
                }
            }
        }) { IsBackground = true, Name = "Connection Guardian socket scan" };
        thread.Start();
        if (!thread.Join(ProbeTimeoutMs))
        {
            stuckThreads.Add(thread);
            var stuck = Interlocked.Read(ref probing);
            skipHandles.Add(stuck);
            Diagnostic?.Invoke($"socket scan stuck on handle 0x{stuck:X}; that handle will be skipped from now on" +
                (stuckThreads.Count >= MaxStuckProbes ? "; no further scans this session" : string.Empty));
        }

        lock (probeSync)
            return [.. found];
    }

    private List<(long Handle, int Type, uint Access)>? OwnHandles()
    {
        var list = QueryProcessHandles();
        if (list != null)
        {
            handleMethod = "process handle snapshot";
            return list;
        }

        list = QuerySystemHandles();
        if (list != null)
            handleMethod = "system handle table";
        return list;
    }

    private static List<(long, int, uint)>? QueryProcessHandles()
    {
        uint size = 64 * 1024;
        while (size <= 64 * 1024 * 1024)
        {
            var buffer = NativeMemory.Alloc(size);
            try
            {
                uint needed = 0;
                var status = NtQueryInformationProcess(-1, ProcessHandleInformation, buffer, size, &needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 4096);
                    continue;
                }

                if (status < 0)
                    return null;

                // PROCESS_HANDLE_SNAPSHOT_INFORMATION: count, reserved, then 40-byte entries:
                // HandleValue, HandleCount, PointerCount, GrantedAccess, ObjectTypeIndex, attributes, reserved.
                var bytes = (byte*)buffer;
                var count = (long)*(ulong*)bytes;
                var result = new List<(long, int, uint)>((int)Math.Min(count, 1 << 20));
                for (long i = 0; i < count && 16 + (i + 1) * 40 <= size; i++)
                {
                    var entry = bytes + 16 + i * 40;
                    result.Add(((long)*(ulong*)entry, (int)*(uint*)(entry + 28), *(uint*)(entry + 24)));
                }

                return result;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    }

    private static List<(long, int, uint)>? QuerySystemHandles()
    {
        var pid = (ulong)Environment.ProcessId;
        uint size = 4 * 1024 * 1024;
        while (size <= 512 * 1024 * 1024)
        {
            var buffer = NativeMemory.Alloc(size);
            try
            {
                uint needed = 0;
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, &needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 65536);
                    continue;
                }

                if (status < 0)
                    return null;

                // SYSTEM_HANDLE_INFORMATION_EX: count, reserved, then 40-byte entries:
                // Object, UniqueProcessId, HandleValue, GrantedAccess, CreatorBackTraceIndex, ObjectTypeIndex, ...
                var bytes = (byte*)buffer;
                var count = (long)*(ulong*)bytes;
                var result = new List<(long, int, uint)>();
                for (long i = 0; i < count && 16 + (i + 1) * 40 <= size; i++)
                {
                    var entry = bytes + 16 + i * 40;
                    if (*(ulong*)(entry + 8) == pid)
                        result.Add(((long)*(ulong*)(entry + 16), *(ushort*)(entry + 30), *(uint*)(entry + 24)));
                }

                return result;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    }

    private static List<(IPEndPoint Local, IPEndPoint Remote)> OwnTcpTable()
    {
        var result = new List<(IPEndPoint, IPEndPoint)>();
        var pid = (uint)Environment.ProcessId;
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            uint size = 0;
            GetExtendedTcpTable(null, ref size, false, (uint)family, TcpTableOwnerPidConnections, 0);
            if (size == 0)
                continue;

            size += 4096;
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, (uint)family, TcpTableOwnerPidConnections, 0) != 0)
                    continue;

                var count = *(uint*)buffer;
                var rowSize = family == AfInet ? 24 : 56;
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * rowSize;
                    if (family == AfInet)
                    {
                        if (*(uint*)(row + 20) != pid)
                            continue;
                        result.Add((
                            new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(row + 4, 4)), Port(*(uint*)(row + 8))),
                            new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(row + 12, 4)), Port(*(uint*)(row + 16)))));
                    }
                    else
                    {
                        // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote addr[16], scope, port, state, pid
                        if (*(uint*)(row + 52) != pid)
                            continue;
                        var local = new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(row, 16)), Port(*(uint*)(row + 20)));
                        var remote = new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(row + 24, 16)), Port(*(uint*)(row + 44)));
                        if (local.Address.IsIPv4MappedToIPv6)
                            local = new IPEndPoint(local.Address.MapToIPv4(), local.Port);
                        if (remote.Address.IsIPv4MappedToIPv6)
                            remote = new IPEndPoint(remote.Address.MapToIPv4(), remote.Port);
                        result.Add((local, remote));
                    }
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return result;
    }

    private static int Port(uint networkOrder) => (int)(((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF));

    private static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var display = key?.GetValue("DisplayVersion") as string ?? string.Empty;
            var build = key?.GetValue("CurrentBuild") as string ?? string.Empty;
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : string.Empty;
            if (int.TryParse(build, out var number) && number >= 22000)
                product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            return $"{product} {display} (build {build}{ubr})".Replace("  ", " ", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return Environment.OSVersion.ToString();
        }
    }

    private static string TcpRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
            string[] names = ["TcpMaxDataRetransmissions", "TcpMaxConnectRetransmissions", "KeepAliveTime", "KeepAliveInterval", "TcpTimedWaitDelay"];
            return string.Join(" ", names.Select(n => $"{n}={key?.GetValue(n)?.ToString() ?? "default"}"));
        }
        catch (Exception ex)
        {
            return $"unreadable ({ex.Message})";
        }
    }

    internal sealed class Tracker
    {
        public bool Started;
        public bool ReportedError;
        public long LastAck;
        public long LastRecv;
        public long LastSent;
        public long MaxAckedExcl;
        public long MaxAckedIncl;
        public uint BytesRetrans;
        public uint Timeouts;
        public long OutstandingSince;
        public uint TimeoutsAtAck;
        public ulong BytesIn;
        public ulong BytesOut;
    }

    [DllImport("ws2_32.dll")]
    private static extern int WSAStartup(ushort version, byte* data);

    [DllImport("ws2_32.dll")]
    private static extern int WSACleanup();

    [DllImport("ws2_32.dll")]
    private static extern int WSAGetLastError();

    [DllImport("ws2_32.dll")]
    private static extern nint socket(int family, int type, int protocol);

    [DllImport("ws2_32.dll")]
    private static extern int closesocket(nint socket);

    [DllImport("ws2_32.dll")]
    private static extern int getsockopt(nint socket, int level, int option, void* value, int* length);

    [DllImport("ws2_32.dll")]
    private static extern int setsockopt(nint socket, int level, int option, void* value, int length);

    [DllImport("ws2_32.dll")]
    private static extern int getpeername(nint socket, byte* address, int* length);

    [DllImport("ws2_32.dll")]
    private static extern int getsockname(nint socket, byte* address, int* length);

    [DllImport("ws2_32.dll")]
    private static extern int WSAIoctl(nint socket, uint code, void* input, uint inputLength, void* output, uint outputLength, uint* returned, nint overlapped, nint completion);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int infoClass, void* buffer, uint length, uint* returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, void* buffer, uint length, uint* returned);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(void* table, ref uint size, bool order, uint family, int tableClass, uint reserved);
}
