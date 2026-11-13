namespace HybridLauncher.Core.Opti;

/// <summary>An OptiScaler version kept on this PC: a downloaded release (stable / nightly) or an imported custom build.</summary>
public sealed record SavedVersion(OptiChannel Channel, string Name, string Folder, long Bytes, DateTime Date, CustomBuild? Build)
{
    public string ChannelLabel => Channel switch { OptiChannel.Nightly => "Nightly", OptiChannel.Custom => "Custom", OptiChannel.Fork => "Fork", _ => "Stable" };
}

/// <summary>Lists and deletes the saved versions. Games keep working when one is deleted: installs never point back at it.</summary>
public static class SavedVersions
{
    public static async Task<List<SavedVersion>> ListAsync(AppSettings settings, CancellationToken ct = default)
    {
        var list = new List<SavedVersion>();
        foreach (var channel in new[] { OptiChannel.Stable, OptiChannel.Nightly, OptiChannel.Fork })
        {
            var dir = Path.Combine(AppInfo.DownloadsDir, channel.ToString().ToLowerInvariant());
            if (!Directory.Exists(dir)) continue;
            foreach (var tagDir in new DirectoryInfo(dir).EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                long bytes = DiskUsage.FolderSize(tagDir.FullName, ct);
                if (bytes == 0) continue;
                list.Add(new SavedVersion(channel, tagDir.Name, tagDir.FullName, bytes, tagDir.LastWriteTime, null));
            }
        }
        try
        {
            foreach (var b in await CustomBuilds.ScanAsync(settings, ct))
                list.Add(new SavedVersion(OptiChannel.Custom, b.Name, b.Folder, DiskUsage.FolderSize(b.Folder, ct), b.Date, b));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Saved versions: custom builds: {ex.Message}"); }
        return list.OrderBy(v => v.Channel).ThenByDescending(v => v.Date).ToList();
    }

    public static void Delete(SavedVersion v)
    {
        if (v.Build is { } b) CustomBuilds.Remove(b);
        else if (Directory.Exists(v.Folder)) Directory.Delete(v.Folder, recursive: true);
        Log.Info($"Deleted saved OptiScaler version {v.ChannelLabel} {v.Name}");
    }

    /// <summary>
    /// Nightly builds pile up (one a day): the ones older than the newest <paramref name="keep"/> that no game's install
    /// came from. Deleting them never breaks a game (installs don't point back at the saved copy).
    /// </summary>
    public static List<SavedVersion> OldNightlies(IEnumerable<SavedVersion> list, AppSettings settings, int keep = 3) =>
        list.Where(v => v.Channel == OptiChannel.Nightly).OrderByDescending(v => v.Date).Skip(keep)
            .Where(v => UsedBy(v, settings, _ => null).Count == 0).ToList();

    /// <summary>The games (names) whose current OptiScaler install came from this version.</summary>
    public static List<string> UsedBy(SavedVersion v, AppSettings settings, Func<string, string?> gameName) =>
        settings.Games.Where(g => g.Value.Install is { } m && m.Channel == v.Channel
                                  && (v.Channel == OptiChannel.Custom ? m.AssetName.Equals(v.Name, StringComparison.OrdinalIgnoreCase)
                                      : v.Channel == OptiChannel.Fork ? SafeTag($"{m.Source?.Replace('/', '_')}_{m.Tag}").Equals(v.Name, StringComparison.OrdinalIgnoreCase) : SafeTag(m.Tag) == v.Name))
            .Select(g => gameName(g.Key) ?? g.Key).ToList();

    // Download folders are named after the tag with path-unsafe characters replaced (see OptiReleases).
    static string SafeTag(string tag) => string.Concat(tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
