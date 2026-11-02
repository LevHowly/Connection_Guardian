using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace ConnectionGuardian;

/// <summary>
/// Talks to the Linux kernel directly from inside Wine. Wine runs Windows code as a normal Linux
/// process, so a raw <c>syscall</c> instruction reaches the real kernel. This lets us tune the game's
/// TCP sockets with options Winsock does not expose (TCP_RTO_MAX_MS, TCP_USER_TIMEOUT, TCP_INFO).
/// </summary>
public static unsafe class LinuxTcp
{
    private const long SysOpenAt = 257;
    private const long SysClose = 3;
    private const long SysGetDents64 = 217;
    private const long SysGetPid = 39;
    private const long SysGetSockName = 51;
    private const long SysGetPeerName = 52;
    private const long SysSetSockOpt = 54;
    private const long SysGetSockOpt = 55;

    private const int AtFdCwd = -100;
    private const int ODirectory = 0x10000;
    private const int SolSocket = 1;
    private const int SoType = 3;
    private const int SoProtocol = 38;
    private const int SoDomain = 39;
    private const int IpProtoTcp = 6;
    private const int SockStream = 1;
    private const int AfInet = 2;
    private const int AfInet6 = 10;

    public const int TcpInfoOpt = 11;
    public const int TcpUserTimeout = 18;
    public const int TcpRtoMaxMs = 44;

    // Windows x64 ABI in (rcx=nr, rdx, r8, r9, [rsp+0x28], [rsp+0x30]) -> Linux syscall ABI.
    private static readonly byte[] StubPrologue =
    [
        0x57,                         // push rdi
        0x56,                         // push rsi
        0x48, 0x89, 0xC8,             // mov rax, rcx
        0x48, 0x89, 0xD7,             // mov rdi, rdx
        0x4C, 0x89, 0xC6,             // mov rsi, r8
        0x4C, 0x89, 0xCA,             // mov rdx, r9
        0x4C, 0x8B, 0x54, 0x24, 0x38, // mov r10, [rsp+0x38]
        0x4C, 0x8B, 0x44, 0x24, 0x40, // mov r8,  [rsp+0x40]
    ];

    private static readonly byte[] StubEpilogue =
    [
        0x5E, // pop rsi
        0x5F, // pop rdi
        0xC3, // ret
    ];

    private static delegate* unmanaged<long, long, long, long, long, long, long> stub;

    public static string? WineVersion { get; private set; }
    public static string SyscallPath { get; private set; } = "unavailable";
    public static long LinuxPid { get; private set; }
    public static bool IsAvailable => stub != null;

    /// <summary>Prepares the syscall stub. Only succeeds when running under Wine on x86-64 Linux.</summary>
    public static bool Initialize(out string error)
    {
        if (stub != null)
        {
            error = string.Empty;
            return true;
        }

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            error = "Only x86-64 is supported.";
            return false;
        }

        var ntdll = GetModuleHandleW("ntdll.dll");
        var wineGetVersion = ntdll == 0 ? 0 : GetProcAddress(ntdll, "wine_get_version");
        WineVersion = wineGetVersion == 0 ? "unknown" : Marshal.PtrToStringAnsi(((delegate* unmanaged<nint>)wineGetVersion)());

        // Wine maps the Linux root to Z:. Only Linux has /proc.
        if (!Directory.Exists(@"Z:\proc\1"))
        {
            error = "Not running under Wine on Linux (Z:\\proc not found).";
            return false;
        }

        if (!TryFindOwnLinuxProcess(out var linuxPid, out var maps, out error))
            return false;

        if (WineVersion == "unknown")
        {
            // e.g. ".../wine-xiv-staging-fsync-git-10.8.r0.a2ca9e4-nolsc/lib/wine/x86_64-unix/ntdll.so"
            var ntdllPath = maps.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty)
                .FirstOrDefault(path => path.EndsWith("/ntdll.so", StringComparison.Ordinal));
            var root = ntdllPath != null && ntdllPath.LastIndexOf("/wine/", StringComparison.Ordinal) is var cut and > 0 ? ntdllPath[..cut] : string.Empty;
            if (root.EndsWith("/lib64", StringComparison.Ordinal) || root.EndsWith("/lib", StringComparison.Ordinal))
                root = root[..root.LastIndexOf('/')];
            if (root.Length > 0)
                WineVersion = $"{Path.GetFileName(root)} ({root})";
        }

        // Wine builds with syscall emulation (Proton / wine-xiv) install a seccomp filter that traps
        // syscall instructions executed from Windows code. Syscalls issued from inside the native
        // libc are allowed, so we jump to a "syscall; ret" sequence located there.
        var gadget = FindLibcSyscallGadget(maps);
        if (gadget == 0)
        {
            error = "Could not locate a syscall instruction in the native libc.";
            return false;
        }

        byte[] callGadget = [0x49, 0xBB, .. BitConverter.GetBytes((long)gadget), 0x41, 0xFF, 0xD3]; // mov r11, imm64; call r11
        var memory = BuildStub([.. StubPrologue, .. callGadget, .. StubEpilogue]);
        if (!TryActivate(memory, linuxPid, out var pid))
        {
            error = $"Raw syscalls are not reaching Linux (getpid returned {pid}, expected {linuxPid}).";
            return false;
        }

        LinuxPid = linuxPid;
        SyscallPath = $"libc gadget at 0x{gadget:X}";
        error = string.Empty;
        return true;
    }

    private static nint BuildStub(byte[] code)
    {
        const uint memCommitReserve = 0x3000;
        const uint memTopDown = 0x100000;
        var memory = VirtualAlloc(0, (nuint)code.Length, memCommitReserve | memTopDown, 0x04);
        if (memory == 0)
            return 0;

        Marshal.Copy(code, 0, memory, code.Length);
        if (!VirtualProtect(memory, (nuint)code.Length, 0x20, out _))
            return 0;

        FlushInstructionCache(GetCurrentProcess(), memory, (nuint)code.Length);
        return memory;
    }

    private static bool TryActivate(nint memory, long expectedPid, out long pid)
    {
        pid = 0;
        if (memory == 0)
            return false;

        stub = (delegate* unmanaged<long, long, long, long, long, long, long>)memory;
        pid = Syscall(SysGetPid);
        if (pid == expectedPid)
            return true;

        stub = null;
        return false;
    }

    /// <summary>
    /// Finds our own Linux pid and memory map. Wine opens files inside wineserver, so /proc/self
    /// would describe wineserver; instead we map a uniquely named file and look for it in /proc/*/maps.
    /// </summary>
    private static bool TryFindOwnLinuxProcess(out long pid, out string[] maps, out string error)
    {
        pid = 0;
        maps = [];
        var marker = Path.Combine(Path.GetTempPath(), $"connection-guardian-{Guid.NewGuid():N}.marker");
        var markerName = Path.GetFileName(marker);
        var exeName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        try
        {
            File.WriteAllBytes(marker, new byte[4096]);
            using var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(marker, FileMode.Open, null, 4096);
            using var view = mapping.CreateViewAccessor(0, 4096);

            // The cmdline filter is only a speed-up; fall back to checking every process.
            foreach (var filter in new[] { true, false })
            foreach (var directory in Directory.EnumerateDirectories(@"Z:\proc"))
            {
                if (!long.TryParse(Path.GetFileName(directory), out var candidate))
                    continue;

                try
                {
                    if (filter && (exeName.Length == 0 || !File.ReadAllText(Path.Combine(directory, "cmdline")).Contains(exeName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var lines = File.ReadAllLines(Path.Combine(directory, "maps"));
                    if (!lines.Any(line => line.EndsWith(markerName, StringComparison.Ordinal)))
                        continue;

                    pid = candidate;
                    maps = lines;
                    error = string.Empty;
                    return true;
                }
                catch (Exception)
                {
                    // Process exited or is not ours.
                }
            }

            error = "Could not identify this process in /proc.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Could not identify this process in /proc: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                File.Delete(marker);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }

    private static nint FindLibcSyscallGadget(string[] maps)
    {
        foreach (var line in maps)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || parts[1] != "r-xp" || !Path.GetFileName(parts[5]).StartsWith("libc.so", StringComparison.Ordinal))
                continue;

            var range = parts[0].Split('-');
            var start = Convert.ToInt64(range[0], 16);
            var end = Convert.ToInt64(range[1], 16);
            var fileOffset = Convert.ToInt64(parts[2], 16);
            byte[] image;
            try
            {
                image = File.ReadAllBytes("Z:" + parts[5].Replace('/', '\\'));
            }
            catch (Exception)
            {
                continue;
            }

            // Read the code from disk: Wine does not let Windows code read native mappings.
            var limit = Math.Min(image.Length, fileOffset + (end - start));
            for (var i = fileOffset; i + 2 < limit; i++)
            {
                if (image[i] == 0x0F && image[i + 1] == 0x05 && image[i + 2] == 0xC3)
                    return (nint)(start + (i - fileOffset));
            }
        }

        return 0;
    }

    public static long Syscall(long nr, long a1 = 0, long a2 = 0, long a3 = 0, long a4 = 0, long a5 = 0)
        => stub(nr, a1, a2, a3, a4, a5);

    /// <summary>Lists the IPv4/IPv6 TCP sockets currently open in this process.</summary>
    public static List<TcpSocket> ListTcpSockets()
    {
        var result = new List<TcpSocket>();
        var dirFd = OpenProcSelfFd();
        if (dirFd < 0)
            return result;

        try
        {
            var buffer = stackalloc byte[8192];
            while (true)
            {
                var read = Syscall(SysGetDents64, dirFd, (long)buffer, 8192);
                if (read <= 0)
                    break;

                for (long offset = 0; offset < read;)
                {
                    var entry = buffer + offset;
                    var recordLength = *(ushort*)(entry + 16);
                    var name = Marshal.PtrToStringAnsi((nint)(entry + 19));
                    offset += recordLength;
                    if (!int.TryParse(name, out var fd) || fd == dirFd)
                        continue;
                    if (TryDescribe(fd, out var socket))
                        result.Add(socket);
                }
            }
        }
        finally
        {
            Syscall(SysClose, dirFd);
        }

        return result;
    }

    /// <summary>Re-reads the endpoints of an fd; returns false if it is no longer a TCP socket.</summary>
    public static bool TryDescribe(int fd, out TcpSocket socket)
    {
        socket = default;
        if (GetIntOption(fd, SolSocket, SoType) != SockStream || GetIntOption(fd, SolSocket, SoProtocol) != IpProtoTcp)
            return false;

        var domain = GetIntOption(fd, SolSocket, SoDomain);
        if (domain != AfInet && domain != AfInet6)
            return false;

        if (!TryGetEndpoint(fd, SysGetPeerName, out var remote) || !TryGetEndpoint(fd, SysGetSockName, out var local))
            return false;

        socket = new TcpSocket(fd, local, remote);
        return true;
    }

    public static long GetIntOption(int fd, int level, int option)
    {
        int value = 0;
        int length = sizeof(int);
        var rc = Syscall(SysGetSockOpt, fd, level, option, (long)&value, (long)&length);
        return rc < 0 ? rc : value;
    }

    /// <summary>Returns 0 on success or a negative Linux errno.</summary>
    public static long SetIntOption(int fd, int level, int option, int value)
        => Syscall(SysSetSockOpt, fd, level, option, (long)&value, sizeof(int));

    public static bool TryGetTcpInfo(int fd, out TcpInfo info)
    {
        var buffer = stackalloc byte[512];
        int length = 512;
        info = default;
        if (Syscall(SysGetSockOpt, fd, IpProtoTcp, TcpInfoOpt, (long)buffer, (long)&length) < 0 || length < 104)
            return false;

        var span = new ReadOnlySpan<byte>(buffer, length);
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(buffer + offset, 4));
        ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(buffer + offset, 8));
        info = new TcpInfo
        {
            State = span[0],
            CaState = span[1],
            Retransmits = span[2],
            Backoff = span[4],
            RtoUs = U32(8),
            Unacked = U32(24),
            Lost = U32(32),
            LastDataSentMs = U32(44),
            LastDataRecvMs = U32(52),
            LastAckRecvMs = U32(56),
            RttUs = U32(68),
            TotalRetrans = U32(100),
            BytesReceived = length >= 136 ? U64(128) : 0,
            BytesSent = length >= 208 ? U64(200) : 0,
            TotalRto = length >= 242 ? BinaryPrimitives.ReadUInt16LittleEndian(span[240..]) : (ushort)0,
            TotalRtoTimeMs = length >= 248 ? U32(244) : 0,
        };
        return true;
    }

    private static bool TryGetEndpoint(int fd, long nr, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        var address = stackalloc byte[128];
        int length = 128;
        if (Syscall(nr, fd, (long)address, (long)&length) < 0)
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

    private static long OpenProcSelfFd()
    {
        var path = Encoding.ASCII.GetBytes("/proc/self/fd\0");
        fixed (byte* p = path)
            return Syscall(SysOpenAt, AtFdCwd, (long)p, ODirectory);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}

public readonly record struct TcpSocket(int Fd, IPEndPoint Local, IPEndPoint Remote);

public struct TcpInfo
{
    public byte State;
    public byte CaState;
    public byte Retransmits;
    public byte Backoff;
    public uint RtoUs;
    public uint Unacked;
    public uint Lost;
    public uint LastDataSentMs;
    public uint LastDataRecvMs;
    public uint LastAckRecvMs;
    public uint RttUs;
    public uint TotalRetrans;
    public ulong BytesReceived;
    public ulong BytesSent;
    public ushort TotalRto;
    public uint TotalRtoTimeMs;

    public readonly string CaStateName => CaState switch
    {
        0 => "Open",
        1 => "Disorder",
        2 => "CWR",
        3 => "Recovery",
        4 => "Loss",
        _ => CaState.ToString(),
    };
}
