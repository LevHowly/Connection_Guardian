using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ConnectionGuardian;

/// <summary>
/// Watches the network state (default route, interface state, traffic counters, Wi-Fi signal) so the
/// log shows when the internet itself went away and came back, separately from when the game
/// connection recovered. Under Wine it reads Linux's /proc through the Z: drive; on Windows it uses
/// the adapter list and the WLAN API.
/// </summary>
public sealed unsafe class NetworkWatch
{
    private readonly bool linux = Directory.Exists(@"Z:\proc\net");
    private string? lastSummary;
    private string? interfaceName;
    private ulong lastRx;
    private ulong lastTx;
    private DateTime lastCounters;

    public string Summary { get; private set; } = "unknown";

    /// <summary>Returns a line describing a change (or null), and fills a per-second traffic line.</summary>
    public string? Poll(out string traffic)
    {
        return linux ? PollLinux(out traffic) : PollWindows(out traffic);
    }

    private string? PollWindows(out string traffic)
    {
        traffic = string.Empty;
        NetworkInterface? best = null;
        NetworkInterface? previous = null;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                if (adapter.Id == interfaceName)
                    previous = adapter;
                if (best == null && adapter.OperationalStatus == OperationalStatus.Up && adapter.GetIPProperties().GatewayAddresses
                        .Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                    best = adapter;
            }
        }
        catch (Exception ex)
        {
            Summary = $"unknown ({ex.Message})";
            return null;
        }

        var shown = best ?? previous;
        var wifi = shown?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? WindowsWifi() : null;
        Summary = best == null
            ? $"NO default route{(previous != null ? $" ({previous.Name} {previous.OperationalStatus})" : string.Empty)}"
            : $"default route via {best.Name} ({best.NetworkInterfaceType}, {best.Description}, {best.Speed / 1_000_000} Mbps{(wifi != null ? $", wifi {wifi}" : string.Empty)})";

        if (shown != null)
        {
            try
            {
                var statistics = shown.GetIPStatistics();
                UpdateTraffic(shown.Id, shown.Name, (ulong)statistics.BytesReceived, (ulong)statistics.BytesSent, ref traffic);
            }
            catch (Exception)
            {
            }
        }

        var key = best == null ? $"none|{previous?.OperationalStatus}" : $"{best.Id}|{best.OperationalStatus}";
        return Changed(key);
    }

    private string? PollLinux(out string traffic)
    {
        traffic = string.Empty;
        var route = DefaultRoute();
        var name = route ?? interfaceName;
        var state = name == null ? "none" : ReadTrimmed($@"Z:\sys\class\net\{name}\operstate") ?? "?";
        var wifi = name == null ? null : WifiQuality(name);
        Summary = route == null
            ? $"NO default route{(name != null ? $" ({name} {state})" : string.Empty)}"
            : $"default route via {route} ({state}{(wifi != null ? $", wifi link {wifi}" : string.Empty)})";

        if (name != null && TryCounters(name, out var rx, out var tx))
            UpdateTraffic(name, name, rx, tx, ref traffic);

        var key = route == null ? $"none|{state}" : $"{route}|{state}";
        return Changed(key);
    }

    private void UpdateTraffic(string id, string name, ulong rx, ulong tx, ref string traffic)
    {
        var now = DateTime.UtcNow;
        if (id == interfaceName && lastCounters != default && rx >= lastRx && tx >= lastTx)
        {
            var seconds = Math.Max(0.001, (now - lastCounters).TotalSeconds);
            traffic = $"{name}: rx {(rx - lastRx) / seconds / 1024:F1} KiB/s, tx {(tx - lastTx) / seconds / 1024:F1} KiB/s";
        }

        interfaceName = id;
        lastRx = rx;
        lastTx = tx;
        lastCounters = now;
    }

    // The wifi quality changes constantly; only report route and link state changes.
    private string? Changed(string key)
    {
        if (key == lastSummary)
            return null;

        var first = lastSummary == null;
        lastSummary = key;
        return first ? $"network: {Summary}" : $"network CHANGED: {Summary}";
    }

    private static string? DefaultRoute()
    {
        try
        {
            string? best = null;
            var bestMetric = int.MaxValue;
            foreach (var line in File.ReadLines(@"Z:\proc\net\route").Skip(1))
            {
                var f = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (f.Length < 8 || f[1] != "00000000" || f[7] != "00000000")
                    continue;

                var flags = int.Parse(f[3], NumberStyles.HexNumber);
                var metric = int.Parse(f[6]);
                if ((flags & 1) != 0 && metric < bestMetric)
                {
                    best = f[0];
                    bestMetric = metric;
                }
            }

            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryCounters(string name, out ulong rx, out ulong tx)
    {
        rx = tx = 0;
        try
        {
            foreach (var line in File.ReadLines(@"Z:\proc\net\dev"))
            {
                var colon = line.IndexOf(':');
                if (colon < 0 || line[..colon].Trim() != name)
                    continue;

                var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                rx = ulong.Parse(f[0]);
                tx = ulong.Parse(f[8]);
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static string? WifiQuality(string name)
    {
        try
        {
            foreach (var line in File.ReadLines(@"Z:\proc\net\wireless"))
            {
                var colon = line.IndexOf(':');
                if (colon < 0 || line[..colon].Trim() != name)
                    continue;

                var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return $"{f[1].TrimEnd('.')}/70, signal {f[2].TrimEnd('.')} dBm";
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    /// <summary>Signal quality and link rate of the connected Wi-Fi interface, via wlanapi.dll.</summary>
    private static string? WindowsWifi()
    {
        nint client = 0;
        void* list = null;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0)
                return null;
            if (WlanEnumInterfaces(client, 0, out list) != 0)
                return null;

            // WLAN_INTERFACE_INFO_LIST: count, index, then 532-byte entries (GUID, description[256], state).
            var bytes = (byte*)list;
            var count = *(uint*)bytes;
            for (var i = 0; i < count; i++)
            {
                var entry = bytes + 8 + i * 532;
                if (*(uint*)(entry + 528) != 1) // wlan_interface_state_connected
                    continue;

                var guid = *(Guid*)entry;
                if (WlanQueryInterface(client, &guid, 7, 0, out var size, out var data, out _) != 0)
                    return "connected (details unavailable)";

                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES: state, mode, profile[256 WCHAR], then association
                    // attributes: SSID (36), BSS type, BSSID[6]+pad, PHY type, PHY index, quality, rx, tx rate (kbps).
                    var attributes = (byte*)data;
                    if (size < 592)
                        return null;
                    var quality = *(uint*)(attributes + 576);
                    var rx = *(uint*)(attributes + 580) / 1000;
                    var tx = *(uint*)(attributes + 584) / 1000;
                    return $"signal {quality}%, rx {rx} Mbps, tx {tx} Mbps";
                }
                finally
                {
                    WlanFreeMemory(data);
                }
            }

            return "not connected";
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (list != null)
                WlanFreeMemory(list);
            if (client != 0)
                WlanCloseHandle(client, 0);
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint client);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint client, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint client, nint reserved, out void* list);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(nint client, Guid* interfaceGuid, int opCode, nint reserved, out uint size, out void* data, out int valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(void* memory);

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
}
