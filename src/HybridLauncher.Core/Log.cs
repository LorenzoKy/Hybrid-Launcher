using System.Text;

namespace HybridLauncher.Core;

/// <summary>
/// Tiny thread-safe file logger. One file per session of the app ("hl-2026-10-30 13-41-09.log"), so a bug report carries the
/// session where the problem happened, whole and nothing else. The newest <see cref="KeepSessions"/> sessions are kept, none
/// older than 30 days; past <see cref="MaxBytes"/> a session keeps only its warnings and errors (a loop can never fill the disk).
/// </summary>
public static class Log
{
    public const int KeepSessions = 20;
    public const long MaxBytes = 20L << 20;

    static readonly object Gate = new();
    static readonly DateTime Started = DateTime.Now;
    static bool _pruned, _capped;
    static long _bytes;
    static string? _file;

    /// <summary>This session's file.</summary>
    public static string CurrentFile => _file ??= Path.Combine(AppInfo.LogsDir, $"hl-{Started:yyyy-MM-dd HH-mm-ss}.log");

    /// <summary>The session before this one (where a crash happened, when the app was started again to report it); null at the first.</summary>
    public static string? PreviousFile => Sessions().FirstOrDefault(f => !string.Equals(f, CurrentFile, StringComparison.OrdinalIgnoreCase));

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (!_pruned) Prune();
                if (_bytes > MaxBytes && level == "INFO")
                {
                    if (_capped) return;
                    _capped = true;
                    level = "WARN";
                    message = $"The log of this session passed {MaxBytes >> 20} MB: from here on only warnings and errors are written.";
                }
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
                _bytes += Encoding.UTF8.GetByteCount(line);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    /// <summary>The sessions' files, newest first.</summary>
    static IEnumerable<string> Sessions() =>
        Directory.EnumerateFiles(AppInfo.LogsDir, "hl-*.log").OrderByDescending(f => File.GetLastWriteTimeUtc(f));

    static void Prune()
    {
        _pruned = true;
        // The newest sessions stay (this one is not written yet, so one fewer); every other log goes after 30 days.
        var old = Sessions().Skip(KeepSessions - 1)
            .Concat(Directory.EnumerateFiles(AppInfo.LogsDir, "*.log", SearchOption.AllDirectories).Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)));
        foreach (var file in old.Distinct(StringComparer.OrdinalIgnoreCase))
            try { File.Delete(file); } catch { }
    }
}
