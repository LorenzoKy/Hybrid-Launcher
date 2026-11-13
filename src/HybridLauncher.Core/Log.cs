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

    // Lines are written by a thread of their own: a log line never waits for the disk on the thread that writes it (the UI
    // thread logs too, and a busy disk held it for 10-40 ms now and then). Warnings and errors are on disk before Write returns.
    static readonly System.Collections.Concurrent.ConcurrentQueue<string> Pending = new();
    static readonly AutoResetEvent Wake = new(false);
    static Thread? _writer;

    static void Write(string level, string message)
    {
        try
        {
            string line;
            lock (Gate)
            {
                if (_bytes > MaxBytes && level == "INFO")
                {
                    if (_capped) return;
                    _capped = true;
                    level = "WARN";
                    message = $"The log of this session passed {MaxBytes >> 20} MB: from here on only warnings and errors are written.";
                }
                line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                _bytes += Encoding.UTF8.GetByteCount(line);
                Pending.Enqueue(line);
                if (_writer is null)
                {
                    _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Log writer", Priority = ThreadPriority.BelowNormal };
                    _writer.Start();
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
                }
            }
            if (level == "INFO") Wake.Set();
            else Flush();
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    static void WriteLoop()
    {
        while (true)
        {
            Wake.WaitOne();
            try { Flush(); } catch { }
        }
    }

    static readonly object FileGate = new();

    /// <summary>Writes every line logged so far (at exit, before a bug report reads the file).</summary>
    public static void Flush()
    {
        lock (FileGate)
        {
            if (Pending.IsEmpty) return;
            try
            {
                if (!_pruned) Prune();
                var sb = new StringBuilder();
                while (Pending.TryDequeue(out var line)) sb.Append(line);
                using var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                // The file's first bytes are the UTF-8 mark, as File.AppendAllText wrote them.
                if (stream.Length == 0) stream.Write(Encoding.UTF8.Preamble);
                stream.Write(bytes);
            }
            catch { }
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
