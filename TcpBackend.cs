using System.Runtime.InteropServices;

namespace ConnectionGuardian;

/// <summary>
/// How the guardian reaches the game's sockets: Linux system calls under Wine, or Winsock on Windows.
/// <see cref="TcpInfo"/> is always expressed with Linux semantics (state numbers, "ms since") so the
/// outage logic is shared.
/// </summary>
public interface ITcpBackend
{
    /// <summary>Short platform name for the log and the window, e.g. "Linux (Wine)".</summary>
    string Name { get; }

    /// <summary>Status line shown when active.</summary>
    string Description { get; }

    /// <summary>True when the retry interval itself can be capped (Linux only).</summary>
    bool CanCapRetryInterval { get; }

    bool Initialize(out string error);

    IEnumerable<(string Name, string Value)> HeaderLines();

    IEnumerable<string> LegendLines();

    List<TcpSocket> ListTcpSockets();

    /// <summary>Re-reads the endpoints of a socket; false if it is no longer that TCP socket.</summary>
    bool TryDescribe(int handle, out TcpSocket socket);

    bool TryGetTcpInfo(TcpSocket socket, out TcpInfo info);

    TuneResult Tune(TcpSocket socket, Configuration configuration, TuneMemory memory);

    void Untune(TcpSocket socket, TuneMemory memory);

    /// <summary>Called once a connection is gone, to drop any per-socket state.</summary>
    void Forget(TcpSocket socket);

    string FormatSample(TcpInfo info);

    /// <summary>What the tuning does, for event messages, e.g. "retrying every &lt;= 1000 ms".</summary>
    string TunedBehaviour(Configuration configuration);

    /// <summary>What happens without tuning, for event messages.</summary>
    string StockBehaviour { get; }

    /// <summary>Releases OS resources; called once the monitor thread has stopped.</summary>
    void Shutdown();

    /// <summary>Receives one-off diagnostics (written to the session log).</summary>
    Action<string>? Diagnostic { get; set; }
}

public sealed record TuneResult(bool Ok, string LogLine, string? Error);

/// <summary>Original socket option values, so they can be restored when the plugin unloads.</summary>
public sealed class TuneMemory
{
    public bool Saved;
    public long First = -1;
    public long Second = -1;
}

public static unsafe class Platform
{
    private static bool? isWine;

    /// <summary>True under Wine (Linux or macOS).</summary>
    public static bool IsWine => isWine ??= DetectWine();

    private static bool DetectWine()
    {
        // Some Wine builds (including XIVLauncher's) hide wine_get_version, so also check for the
        // registry key every Wine prefix has and for the Linux root mapped as Z:.
        var ntdll = GetModuleHandleW("ntdll.dll");
        if (ntdll != 0 && GetProcAddress(ntdll, "wine_get_version") != 0)
            return true;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Wine");
            if (key != null)
                return true;
        }
        catch (Exception)
        {
        }

        return Directory.Exists(@"Z:\proc\1");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern nint GetProcAddress(nint module, string name);
}
