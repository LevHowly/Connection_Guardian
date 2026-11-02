using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ConnectionGuardian;

/// <summary>
/// One log file per game session in &lt;plugin config&gt;/logs, written so that someone else can
/// analyze a disconnect without asking follow-up questions: environment header, every connection
/// event, a periodic TCP sample, and fine-grained samples around every outage.
/// </summary>
public sealed partial class SessionLog : IDisposable
{
    public const string ScriptLogName = "drop_internet.log";
    private const int KeepSessions = 30;

    private readonly object sync = new();
    private readonly StreamWriter writer;

    public SessionLog(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        LogDirectory = Path.Combine(configDirectory, "logs");
        Directory.CreateDirectory(LogDirectory);
        Prune();

        FilePath = Path.Combine(LogDirectory, $"session-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string ConfigDirectory { get; }
    public string LogDirectory { get; }
    public string FilePath { get; }
    public string ScriptLogPath => Path.Combine(LogDirectory, ScriptLogName);

    public void Write(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{category,-7}] {Redact(message)}";
        lock (sync)
        {
            try
            {
                writer.WriteLine(line);
            }
            catch (Exception)
            {
                // Logging must never break monitoring.
            }
        }
    }

    public void WriteRaw(string text)
    {
        lock (sync)
        {
            try
            {
                writer.WriteLine(Redact(text));
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Zips every session log plus the outage script log into one file that can be sent as is.</summary>
    public string CreateShareZip()
    {
        var zipPath = Path.Combine(ConfigDirectory, $"ConnectionGuardian-logs-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
        lock (sync)
        {
            writer.Flush();
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(LogDirectory).OrderBy(f => f))
            {
                var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }

        return zipPath;
    }

    /// <summary>Opens a file or folder with the desktop's default application (xdg-open under Wine).</summary>
    public static void Open(string path)
    {
        var underWine = Directory.Exists(@"Z:\proc\1");
        var startInfo = underWine
            ? new ProcessStartInfo("winebrowser.exe") { UseShellExecute = false }
            : new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>"Z:\home\me\x" -> "/home/me/x", so Linux users can find the file.</summary>
    public static string ToLinuxPath(string path) =>
        path.StartsWith(@"Z:\", StringComparison.OrdinalIgnoreCase) ? path[2..].Replace('\\', '/') : path;

    /// <summary>Hides the user name in home directory paths.</summary>
    public static string Redact(string text) => HomeRegex().Replace(text, "~");

    public void Dispose()
    {
        lock (sync)
            writer.Dispose();
    }

    private void Prune()
    {
        try
        {
            foreach (var old in Directory.GetFiles(LogDirectory, "session-*.log").OrderByDescending(f => f).Skip(KeepSessions - 1))
                File.Delete(old);
        }
        catch (Exception)
        {
        }
    }

    [GeneratedRegex(@"(?i)(?:Z:)?[\\/]home[\\/][^\\/\s]+")]
    private static partial Regex HomeRegex();
}
