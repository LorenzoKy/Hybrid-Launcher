namespace HybridLauncher.Core;

/// <summary>Folder and file sizes for the UI.</summary>
public static class DiskUsage
{
    /// <summary>Total size of the files under <paramref name="dir"/>; unreadable folders and junctions are skipped.</summary>
    public static long FolderSize(string dir, CancellationToken ct = default)
    {
        if (!Directory.Exists(dir)) return 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        long total = 0;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            try { total += file.Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    public static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>"84.3 GB", "12.6 MB", "740 KB".</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
