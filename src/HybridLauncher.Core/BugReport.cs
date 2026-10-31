using System.Text;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Hardware;

namespace HybridLauncher.Core;

/// <summary>
/// Settings → About → Report a problem: GitHub's bug form, opened with the version, the system and the end of this session's
/// log already filled in, and the whole log saved beside it to attach. Personal details are taken out of both first: the
/// Windows user's folder and name, the PC's name, e-mail addresses.
/// </summary>
public static partial class BugReport
{
    /// <summary>GitHub refuses longer addresses; the log in the form is cut from its start to fit.</summary>
    const int MaxUrl = 7800;

    public static string IssuesUrl => $"https://github.com/{AppInfo.Repository}/issues";

    /// <summary>Windows, then each graphics card with its driver: one line for the form's "system" field.</summary>
    public static string SystemLine()
    {
        var hw = HardwareInfo.Read();
        var gpus = hw.Gpus.Select(g => g.DriverVersion.Length > 0 ? $"{g.Name}, {g.DriverVersion}" : g.Name);
        return string.Join("; ", new[] { $"{hw.System.Os} (build {hw.System.Build})", hw.Cpu.Name }.Concat(gpus).Where(s => s.Length > 0));
    }

    /// <summary>The text with the user's folder, user name, PC name and e-mail addresses replaced by placeholders.</summary>
    public static string Scrub(string text)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 3)
        {
            text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                       .Replace(home.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                       .Replace(home.Replace("\\", "\\\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        text = Email().Replace(text, "[e-mail]");
        foreach (var (name, mark) in new[] { (Environment.UserName, "[user]"), (Environment.MachineName, "[pc]") })
            if (name.Length >= 3)
                text = Regex.Replace(text, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])", mark, RegexOptions.IgnoreCase);
        return text;
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex Email();

    /// <summary>
    /// This session's log, scrubbed, saved as a file to drag into the form (with the previous session's when there is one, where
    /// a crash would be). Returns its path.
    /// </summary>
    public static string SaveLog()
    {
        var dir = AppInfo.Ensure(Path.Combine(AppInfo.LogsDir, "report"));
        foreach (var f in Directory.EnumerateFiles(dir, "*.log")) try { File.Delete(f); } catch { }
        var text = new StringBuilder();
        foreach (var (title, file) in new[] { ("Previous session", Log.PreviousFile), ("This session", Log.CurrentFile) })
        {
            if (file is null || !File.Exists(file)) continue;
            text.AppendLine($"===== {title}: {Path.GetFileName(file)} =====");
            text.AppendLine(Read(file));
        }
        var path = Path.Combine(dir, $"hl-report-{DateTime.Now:yyyy-MM-dd HH-mm-ss}.log");
        File.WriteAllText(path, Scrub(text.ToString()), Encoding.UTF8);
        return path;
    }

    /// <summary>GitHub's bug form with the version, the system and as much of the end of this session's log as fits.</summary>
    public static string FormUrl(string version, string system, string attachedName)
    {
        string Url(string log) =>
            $"{IssuesUrl}/new?template=bug_report.yml&version={Uri.EscapeDataString(version)}&system={Uri.EscapeDataString(system)}&log={Uri.EscapeDataString(log)}";
        var lines = Scrub(Read(Log.CurrentFile)).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var note = $"(End of this session's log. The whole log is in {attachedName}: drag it here too.)";
        for (int skip = 0; ; skip += Math.Max(1, lines.Count / 20))
        {
            var url = Url(note + "\n```\n" + string.Join("\n", lines.Skip(skip)) + "\n```");
            if (url.Length <= MaxUrl || skip >= lines.Count) return url.Length <= MaxUrl ? url : Url(note);
        }
    }

    /// <summary>A log the app is still writing: opened alongside it.</summary>
    static string Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) { return $"(log not readable: {ex.Message})"; }
    }
}
