using System.Text.Json;

namespace HybridLauncher.Core.Cloud;

/// <summary>Where cloud saves are kept: OneDrive, MEGA or a folder. One game is one key (its name and id).</summary>
public interface ICloudStore
{
    /// <summary>The provider's name, for messages and the log.</summary>
    string Name { get; }

    /// <summary>The manifest of the game's current saves in the cloud; null while it has none.</summary>
    Task<CloudManifest?> ManifestAsync(string key, CancellationToken ct);

    /// <summary>
    /// Sends <paramref name="zip"/> as the game's saves: the current copy (<paramref name="old"/>) becomes an older one first,
    /// and the history keeps the newest <paramref name="keep"/> copies.
    /// </summary>
    Task PutAsync(string key, string zip, CloudManifest manifest, CloudManifest? old, int keep, CancellationToken ct);

    /// <summary>Downloads the current saves (<paramref name="copyId"/> null) or an older copy into <paramref name="toFile"/>.</summary>
    Task GetAsync(string key, string? copyId, string toFile, CancellationToken ct);

    Task<List<CloudCopy>> HistoryAsync(string key, CancellationToken ct);

    /// <summary>Removes the game's saves and every older copy from the cloud.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// The fallback: any folder (a NAS, a USB drive, a folder another sync app keeps). "Hybrid Cloud Saves\&lt;game&gt;" holds
/// save.zip, manifest.json and history\. Files are written beside their target and swapped in, so a sync app watching the
/// folder never picks up a half-written one.
/// </summary>
public sealed class FolderStore(string folder, string? name = null) : ICloudStore
{
    public const string RootName = "Hybrid Cloud Saves";

    public string Name => name ?? folder;

    /// <summary>The saves folder's earlier names (before 0.25, then before 1.0): renamed to <see cref="RootName"/> when found.</summary>
    public static readonly string[] LegacyRootNames = [AppInfo.PreviousName + " Saves", AppInfo.LegacyName + " Cloud Saves"];

    /// <summary>"My Drive" in Google Drive for desktop's languages: its drive's root is read-only, saves go in there.</summary>
    static readonly string[] GoogleMyDrive = ["My Drive", "Il mio Drive", "Meine Ablage", "Mi unidad", "Mon Drive", "Meu Drive", "Mijn Drive", "Mój dysk"];

    /// <summary>
    /// The folder saves really go to: the one picked, or, when it is the root of Google Drive for desktop's drive (G:\, whose
    /// root takes no files), its "My Drive" folder.
    /// </summary>
    public static string Resolve(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            if (Path.GetPathRoot(full) is not { } root || !Path.TrimEndingDirectorySeparator(full).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(Path.Combine(root, ".shortcut-targets-by-id")))
                return dir;
            return GoogleMyDrive.Select(n => Path.Combine(root, n)).FirstOrDefault(Directory.Exists) ?? dir;
        }
        catch { return dir; }
    }

    /// <summary>Whether a file can be written in <paramref name="dir"/> (a test file, removed at once).</summary>
    public static bool Writable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".hl-write-test-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public string GameDir(string key)
    {
        var root = Path.Combine(folder, RootName);
        if (!Directory.Exists(root) && LegacyRootNames.Select(n => Path.Combine(folder, n)).FirstOrDefault(Directory.Exists) is { } legacy)
            try { Directory.Move(legacy, root); } catch (Exception ex) { Log.Warn($"Cloud folder rename: {ex.Message}"); }
        return Path.Combine(root, key);
    }

    public Task<CloudManifest?> ManifestAsync(string key, CancellationToken ct) => Task.Run(() =>
    {
        var dir = GameDir(key);
        var file = Path.Combine(dir, "manifest.json");
        if (!File.Exists(file) || !File.Exists(Path.Combine(dir, "save.zip"))) return null;
        return JsonSerializer.Deserialize(File.ReadAllText(file), HybridJson.Default.CloudManifest);
    }, ct);

    /// <summary>
    /// One file operation, done again when the folder's sync app is holding the file: OneDrive and Google Drive keep a file
    /// they are sending for a moment ("access denied"), most often when saves go up twice within seconds (an upload before Play,
    /// then one after a short session). A file or folder that is not there is not waited for.
    /// </summary>
    static void Retry(Action io, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { io(); return; }
            catch (Exception ex) when (attempt < 6 && ex is UnauthorizedAccessException or IOException && ex is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Log.Info($"Cloud folder busy ({ex.Message}), again in a moment");
                ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500 << attempt));
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    public Task PutAsync(string key, string zip, CloudManifest manifest, CloudManifest? old, int keep, CancellationToken ct) => Task.Run(() =>
    {
        var dir = Directory.CreateDirectory(GameDir(key)).FullName;
        var target = Path.Combine(dir, "save.zip");
        var tmp = target + ".partial";
        Retry(() => File.Copy(zip, tmp, overwrite: true), ct);
        if (old is not null && File.Exists(target))
        {
            var history = Directory.CreateDirectory(Path.Combine(dir, "history")).FullName;
            Retry(() => File.Move(target, Path.Combine(history, old.HistoryName + ".zip"), overwrite: true), ct);
            foreach (var f in new DirectoryInfo(history).GetFiles("*.zip").OrderByDescending(f => f.Name).Skip(keep))
                try { f.Delete(); } catch { }
        }
        Retry(() => File.Move(tmp, target, overwrite: true), ct);
        var mtmp = Path.Combine(dir, "manifest.json.partial");
        var json = JsonSerializer.Serialize(manifest, HybridJson.Default.CloudManifest);
        Retry(() => File.WriteAllText(mtmp, json), ct);
        Retry(() => File.Move(mtmp, Path.Combine(dir, "manifest.json"), overwrite: true), ct);
    }, ct);

    public Task GetAsync(string key, string? copyId, string toFile, CancellationToken ct) => Task.Run(() =>
    {
        var source = copyId ?? Path.Combine(GameDir(key), "save.zip");
        if (!File.Exists(source)) throw new FileNotFoundException(L.T("The cloud has no saves for this game yet."), source);
        // Copied whole first: a file the sync app keeps online-only is fetched here, before anything is unpacked.
        Retry(() => File.Copy(source, toFile, overwrite: true), ct);
    }, ct);

    public Task<List<CloudCopy>> HistoryAsync(string key, CancellationToken ct) => Task.Run(() =>
    {
        var dir = Path.Combine(GameDir(key), "history");
        if (!Directory.Exists(dir)) return new List<CloudCopy>();
        return new DirectoryInfo(dir).GetFiles("*.zip").Select(f => new CloudCopy(f.FullName, Path.GetFileNameWithoutExtension(f.Name), f.Length)).ToList();
    }, ct);

    /// <summary>
    /// Removes the game's folder, one entry at a time from the deepest: OneDrive's folders are reparse points, which a recursive
    /// delete does not go into ("the directory is not empty"), and OneDrive marks them read-only, which Windows does not delete
    /// (the mark only ever means "synced by OneDrive" there, so it is taken off).
    /// </summary>
    public Task DeleteAsync(string key, CancellationToken ct) => Task.Run(() =>
    {
        var dir = GameDir(key);
        if (!Directory.Exists(dir)) return;
        static void Writable(FileSystemInfo entry)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        var root = new DirectoryInfo(dir);
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories).ToList())
            Retry(() => { Writable(file); file.Delete(); }, ct);
        foreach (var folder in root.EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length).Append(root).ToList())
            Retry(() => { Writable(folder); folder.Delete(); }, ct);
    }, ct);
}
