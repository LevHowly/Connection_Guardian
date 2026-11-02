namespace ConnectionGuardian;

/// <summary>Linux kernel sockets reached through raw syscalls from inside Wine (see <see cref="LinuxTcp"/>).</summary>
public sealed class LinuxBackend : ITcpBackend
{
    private const int IpProtoTcp = 6;

    public string Name => "Linux (Wine)";

    public string Description => $"Linux pid {LinuxTcp.LinuxPid}, {LinuxTcp.SyscallPath}, Wine {LinuxTcp.WineVersion}";

    public bool CanCapRetryInterval => true;

    public Action<string>? Diagnostic { get; set; }

    public string StockBehaviour => "Socket NOT tuned: kernel backoff can grow up to 120 s.";

    public bool Initialize(out string error) => LinuxTcp.Initialize(out error);

    public IEnumerable<(string Name, string Value)> HeaderLines()
    {
        yield return ("OS", OsDescription());
        yield return ("Kernel", ReadTrimmed(@"Z:\proc\sys\kernel\osrelease") ?? "unknown");
        yield return ("Wine", LinuxTcp.WineVersion ?? "unknown");
        yield return ("TCP sysctls", Sysctls());
    }

    public IEnumerable<string> LegendLines()
    {
        yield return "Legend: SAMPLE lines are the Linux kernel's TCP_INFO for one game connection. Times are in ms:";
        yield return "  rto=current retransmit timeout, bo=backoff count, rtx=retries of the current packet, unacked/lost=packets,";
        yield return "  snd/rcv/ack=ms since we last sent data / received data / received an ACK, rtt=round trip,";
        yield return "  retx=total retransmissions, nrto=total retransmit timeouts, rxB/txB=total bytes received/sent.";
    }

    public List<TcpSocket> ListTcpSockets() => LinuxTcp.ListTcpSockets();

    public bool TryDescribe(int handle, out TcpSocket socket) => LinuxTcp.TryDescribe(handle, out socket);

    public bool TryGetTcpInfo(TcpSocket socket, out TcpInfo info) => LinuxTcp.TryGetTcpInfo(socket.Fd, out info);

    public TuneResult Tune(TcpSocket socket, Configuration configuration, TuneMemory memory)
    {
        var fd = socket.Fd;
        if (!memory.Saved)
        {
            memory.First = LinuxTcp.GetIntOption(fd, IpProtoTcp, LinuxTcp.TcpRtoMaxMs);
            memory.Second = LinuxTcp.GetIntOption(fd, IpProtoTcp, LinuxTcp.TcpUserTimeout);
            memory.Saved = true;
        }

        var rtoMax = Math.Clamp(configuration.RtoMaxMs, 1000, 120000);
        var userTimeout = Math.Clamp(configuration.UserTimeoutSeconds, 30, 3600) * 1000;
        var rc1 = LinuxTcp.SetIntOption(fd, IpProtoTcp, LinuxTcp.TcpRtoMaxMs, rtoMax);
        var rc2 = LinuxTcp.SetIntOption(fd, IpProtoTcp, LinuxTcp.TcpUserTimeout, userTimeout);
        var readRto = LinuxTcp.GetIntOption(fd, IpProtoTcp, LinuxTcp.TcpRtoMaxMs);
        var readUser = LinuxTcp.GetIntOption(fd, IpProtoTcp, LinuxTcp.TcpUserTimeout);
        var ok = rc1 == 0 && rc2 == 0;
        var line = $"original rto_max={memory.First} user_timeout={memory.Second}; " +
                   $"set rto_max={rtoMax} ({Errno(rc1)}) user_timeout={userTimeout} ({Errno(rc2)}); read back rto_max={readRto} user_timeout={readUser}";
        var error = ok ? null : $"setsockopt failed (TCP_RTO_MAX_MS: {Errno(rc1)}, TCP_USER_TIMEOUT: {Errno(rc2)}). TCP_RTO_MAX_MS needs Linux 6.15+.";
        return new TuneResult(ok, line, error);
    }

    public void Untune(TcpSocket socket, TuneMemory memory)
    {
        if (!memory.Saved || memory.First < 0)
            return;

        if (LinuxTcp.TryDescribe(socket.Fd, out var current) && current == socket)
        {
            LinuxTcp.SetIntOption(socket.Fd, IpProtoTcp, LinuxTcp.TcpRtoMaxMs, (int)memory.First);
            LinuxTcp.SetIntOption(socket.Fd, IpProtoTcp, LinuxTcp.TcpUserTimeout, (int)Math.Max(0, memory.Second));
        }
    }

    public void Forget(TcpSocket socket)
    {
    }

    public void Shutdown()
    {
    }

    public string FormatSample(TcpInfo i) =>
        $"{Guardian.StateName(i.State)}/{i.CaStateName} rto={i.RtoUs / 1000} bo={i.Backoff} rtx={i.Retransmits} " +
        $"unacked={i.Unacked} lost={i.Lost} snd={i.LastDataSentMs} rcv={i.LastDataRecvMs} ack={i.LastAckRecvMs} " +
        $"rtt={i.RttUs / 1000.0:F1} retx={i.TotalRetrans} nrto={i.TotalRto} rxB={i.BytesReceived} txB={i.BytesSent}";

    public string TunedBehaviour(Configuration configuration) =>
        $"retry every <= {configuration.RtoMaxMs} ms, give up after {configuration.UserTimeoutSeconds} s";

    private static string Errno(long rc) => rc == 0 ? "ok" : $"errno {-rc}";

    private static string OsDescription()
    {
        try
        {
            var line = File.ReadLines(@"Z:\etc\os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
            return line?["PRETTY_NAME=".Length..].Trim('"') ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string Sysctls()
    {
        string[] names = ["tcp_retries2", "tcp_rto_max_ms", "tcp_rto_min_us", "tcp_frto", "tcp_early_retrans", "tcp_keepalive_time", "tcp_congestion_control"];
        return string.Join(" ", names.Select(n => $"{n}={ReadTrimmed($@"Z:\proc\sys\net\ipv4\{n}") ?? "n/a"}"));
    }

    internal static string? ReadTrimmed(string path)
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
}
