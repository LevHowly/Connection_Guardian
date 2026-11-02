using System.Globalization;

namespace ConnectionGuardian;

/// <summary>
/// Watches the Linux network state (default route, interface state, traffic counters) through
/// Wine's Z: drive, so the log shows when the internet itself went away and came back, separately
/// from when the game connection recovered.
/// </summary>
public sealed class NetworkWatch
{
    private string? lastSummary;
    private string? interfaceName;
    private ulong lastRx;
    private ulong lastTx;
    private DateTime lastCounters;

    public string Summary { get; private set; } = "unknown";

    /// <summary>Returns a line describing a change (or null), and fills a per-second traffic line.</summary>
    public string? Poll(out string traffic)
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
        {
            var now = DateTime.UtcNow;
            if (name == interfaceName && lastCounters != default)
            {
                var seconds = Math.Max(0.001, (now - lastCounters).TotalSeconds);
                traffic = $"{name}: rx {(rx - lastRx) / seconds / 1024:F1} KiB/s, tx {(tx - lastTx) / seconds / 1024:F1} KiB/s";
            }

            interfaceName = name;
            lastRx = rx;
            lastTx = tx;
            lastCounters = now;
        }

        // The wifi quality changes constantly; only report route and link state changes.
        var key = route == null ? $"none|{state}" : $"{route}|{state}";
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
