namespace HybridLauncher.Core.Opti;

/// <summary>An OptiScaler build that did not come from GitHub releases (e.g. a test build shared on Discord).</summary>
public sealed record CustomBuild(string Name, string Folder, string PayloadRoot, string Version, DateTime Date, IReadOnlyList<string> Missing)
{
    /// <summary>Files of <see cref="CustomBuilds.BorrowedFiles"/> this build lacks; the installer takes them from the latest nightly.</summary>
    public bool IsComplete => Missing.Count == 0;

    /// <summary>"10.0.0-dev (fa44a4a8) (20261022_091208)" → "10.0.0-dev · fa44a4a8 · 2026-10-22 09:12".</summary>
    public string PrettyVersion => CustomBuilds.PrettyVersion(Version);

    public string Display => $"{Name}  ·  {CustomBuilds.ShortVersion(Version)}";

    public OptiRelease ToRelease() => new(OptiChannel.Custom, Version, Name, Date, Name, PayloadRoot, 0, null);
}

/// <summary>
/// The custom builds folder: every sub-folder holding an OptiScaler dll is one build. Archives dropped in it
/// are extracted into a sub-folder of the same name on the next scan. The folder is %LOCALAPPDATA%\Hybrid Launcher\CustomBuilds
/// unless the user picked another one in Settings.
/// </summary>
public static class CustomBuilds
{
    static readonly string[] ArchiveExts = [".zip", ".7z", ".rar"];

    /// <summary>Files a build needs besides the dll; when missing, the installer borrows them from the latest nightly.</summary>
    public static readonly string[] BorrowedFiles = ["setup_windows.bat", "OptiScaler.ini"];

    public static string DefaultDir => Path.Combine(AppInfo.DataRoot, "CustomBuilds");

    public static string Dir(AppSettings s)
    {
        var dir = string.IsNullOrWhiteSpace(s.CustomBuildsDir) ? DefaultDir : s.CustomBuildsDir!;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task<List<CustomBuild>> ScanAsync(AppSettings s, CancellationToken ct = default)
    {
        var dir = Dir(s);
        // Loose archives become folders first, so a build downloaded straight into the folder just shows up.
        foreach (var archive in Directory.EnumerateFiles(dir).Where(IsArchive).ToList())
        {
            var dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(archive));
            if (Directory.Exists(dest)) continue;
            try { await ExtractBuildAsync(archive, dest, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"Custom build {archive}: {ex.Message}"); }
        }

        var list = new List<CustomBuild>();
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (Read(sub) is { } b) list.Add(b);
        }
        return list.OrderByDescending(b => b.Date).ToList();
    }

    /// <summary>Copies a build folder, or extracts a .zip/.7z/.rar, into the custom builds folder. Returns the new build.</summary>
    public static async Task<CustomBuild> ImportAsync(AppSettings s, string source, CancellationToken ct = default)
    {
        var dir = Dir(s);
        bool archive = File.Exists(source) && IsArchive(source);
        if (!archive && !Directory.Exists(source)) throw new InvalidOperationException(L.F("{0} was not found.", source));

        var name = archive ? Path.GetFileNameWithoutExtension(source) : Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var dest = Unique(Path.Combine(dir, Safe(name)));

        if (archive)
        {
            await ExtractBuildAsync(source, dest, ct);
        }
        else
        {
            if (FindPayloadRoot(source) is null) throw new InvalidOperationException(NoDllMessage(source));
            if (Path.GetFullPath(source).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Read(source) ?? throw new InvalidOperationException(NoDllMessage(source));
            await Task.Run(() => CopyDir(source, dest, ct), ct);
        }

        var build = Read(dest);
        if (build is null)
        {
            TryDelete(dest);
            throw new InvalidOperationException(NoDllMessage(source));
        }
        Log.Info($"Imported custom build {build.Name} ({build.Version}) from {source}");
        return build;
    }

    public static void Remove(CustomBuild b) => Directory.Delete(b.Folder, recursive: true);

    /// <summary>Reads one build folder; null if it has no OptiScaler dll.</summary>
    public static CustomBuild? Read(string folder)
    {
        var root = FindPayloadRoot(folder);
        if (root is null) return null;
        var dll = Path.Combine(root, "OptiScaler.dll");
        if (!File.Exists(dll)) dll = Directory.EnumerateFiles(root, "*.dll").First(OptiDetector.IsOptiScaler);
        return new CustomBuild(Path.GetFileName(folder), folder, root, OptiDetector.Version(dll),
            File.GetLastWriteTime(dll), BorrowedFiles.Where(f => !File.Exists(Path.Combine(root, f))).ToList());
    }

    /// <summary>The folder (the build itself or a sub-folder up to 3 levels down) that holds the OptiScaler dll.</summary>
    public static string? FindPayloadRoot(string folder, int depth = 3)
    {
        if (File.Exists(Path.Combine(folder, "OptiScaler.dll"))) return folder;
        // Some builds ship the dll already renamed (dxgi.dll, winmm.dll, ...): recognise it by its version resource.
        if (Directory.EnumerateFiles(folder, "*.dll").Any(OptiDetector.IsOptiScaler)) return folder;
        if (depth == 0) return null;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if (FindPayloadRoot(sub, depth - 1) is { } r) return r;
        }
        return null;
    }

    public static string ShortVersion(string v)
    {
        var first = v.Split(' ', 2)[0];
        return first.Length > 0 ? first : v;
    }

    public static string PrettyVersion(string v)
    {
        // OptiScaler's ProductVersion: "<version> (<commit>) (<yyyyMMdd_HHmmss>)"; anything else is shown as is.
        var parts = v.Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return v;
        var pretty = new List<string> { parts[0] };
        foreach (var p in parts.Skip(1))
        {
            if (DateTime.TryParseExact(p, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var d))
                pretty.Add($"built {d:yyyy-MM-dd HH:mm}");
            else pretty.Add(p);
        }
        return string.Join("  ·  ", pretty);
    }

    static async Task ExtractBuildAsync(string archive, string dest, CancellationToken ct)
    {
        await ArchiveExtractor.ExtractAsync(archive, dest, ct);
        if (FindPayloadRoot(dest) is null)
        {
            TryDelete(dest);
            throw new InvalidOperationException(NoDllMessage(archive));
        }
    }

    static string NoDllMessage(string source) =>
        L.F("{0} does not contain an OptiScaler dll (OptiScaler.dll, or a dll whose file details say OptiScaler).", Path.GetFileName(source));

    static bool IsArchive(string f) => ArchiveExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);

    static string Unique(string path)
    {
        var p = path;
        for (int i = 2; Directory.Exists(p) || File.Exists(p); i++) p = $"{path} ({i})";
        return p;
    }

    internal static void CopyDir(string src, string dst, CancellationToken ct)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, overwrite: true);
        }
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
