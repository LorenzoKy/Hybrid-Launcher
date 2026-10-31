namespace HybridLauncher.Core;

/// <summary>
/// Keeps Hybrid Launcher's cache small. Game pages load from it (store data, wiki pages, reviews, art), so it is kept, but bounded:
///  • leftovers of interrupted downloads and unpacks (.tmp, .part, work folders) go after a day;
///  • text data no game page has needed for 60 days goes (it is fetched again when needed);
///  • pictures past the budget go oldest first, until the whole cache is back under it.
/// Runs once in the background at start; the Downloads folder (installers the user may reuse) is left alone.
/// </summary>
public static class CacheJanitor
{
    /// <summary>The cache's budget on disk. A game page needs a few MB at most (its art, its data): this holds a few hundred games.</summary>
    public const long Budget = 300L * 1024 * 1024;

    /// <summary>Trimmed down to this, so it does not run again at every start.</summary>
    const long Target = 240L * 1024 * 1024;

    static readonly string[] Kept = ["downloads", "webview"];

    public static Task RunAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try { Trim(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Cache trim: {ex.Message}"); }
    }, ct);

    /// <summary>The cache's size on disk (the Downloads folder left out).</summary>
    public static long Size() => Files().Sum(f => f.Length);

    static IEnumerable<FileInfo> Files()
    {
        var root = new DirectoryInfo(AppInfo.CacheDir);
        foreach (var f in root.EnumerateFiles()) yield return f;
        foreach (var d in root.EnumerateDirectories())
        {
            if (Kept.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
            IEnumerable<FileInfo> files;
            try { files = d.EnumerateFiles("*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    static void Trim(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        long freed = 0;
        bool Delete(FileInfo f)
        {
            try
            {
                long size = f.Length;
                f.Delete();
                freed += size;
                return true;
            }
            catch { return false; }
        }

        // Work folders of finished installs and unpacks.
        foreach (var name in new[] { "work", "renodx-unpack" })
        {
            var dir = Path.Combine(AppInfo.CacheDir, name);
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in Directory.GetDirectories(dir))
                try { if (now - Directory.GetLastWriteTimeUtc(sub) > TimeSpan.FromDays(1)) Directory.Delete(sub, recursive: true); } catch { }
        }

        var files = Files().ToList();
        var keep = new List<FileInfo>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var age = now - f.LastWriteTimeUtc;
            bool leftover = f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
            bool image = IsImage(f);
            if ((leftover && age > TimeSpan.FromDays(1)) || (!image && age > TimeSpan.FromDays(60) && Expires(f)))
            {
                if (Delete(f)) continue;
            }
            keep.Add(f);
        }

        long total = keep.Sum(f => f.Length);
        if (total > Budget)
        {
            // Pictures, least recently used first (a picture is touched each time a page shows it).
            foreach (var f in keep.Where(IsImage).OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= Target) break;
                ct.ThrowIfCancellationRequested();
                long size = f.Length;
                if (Delete(f)) total -= size;
            }
        }
        if (freed > 0) Log.Info($"Cache trimmed: {DiskUsage.Format(freed)} freed, {DiskUsage.Format(total)} kept");
    }

    static bool IsImage(FileInfo f) => f.Extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".avif" or ".gif";

    // Lists Hybrid Launcher reads as a whole (OptiScaler releases, the RenoDX and Luma lists, the GOG database...) sit at the top of the
    // cache and are refreshed by their own code: only per-game files in sub-folders expire here.
    static bool Expires(FileInfo f) => !string.Equals(f.DirectoryName?.TrimEnd('\\'), AppInfo.CacheDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks a cached picture as just used, so the trim keeps the ones pages still show.</summary>
    public static void Touch(string path)
    {
        try
        {
            var f = new FileInfo(path);
            if (f.Exists && DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(1)) f.LastWriteTimeUtc = DateTime.UtcNow;
        }
        catch { }
    }
}
