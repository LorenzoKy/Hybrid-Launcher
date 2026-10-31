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

    public Task PutAsync(string key, string zip, CloudManifest manifest, CloudManifest? old, int keep, CancellationToken ct) => Task.Run(() =>
    {
        var dir = Directory.CreateDirectory(GameDir(key)).FullName;
        var target = Path.Combine(dir, "save.zip");
        var tmp = target + ".partial";
        File.Copy(zip, tmp, overwrite: true);
        if (old is not null && File.Exists(target))
        {
            var history = Directory.CreateDirectory(Path.Combine(dir, "history")).FullName;
            File.Move(target, Path.Combine(history, old.HistoryName + ".zip"), overwrite: true);
            foreach (var f in new DirectoryInfo(history).GetFiles("*.zip").OrderByDescending(f => f.Name).Skip(keep))
                try { f.Delete(); } catch { }
        }
        File.Move(tmp, target, overwrite: true);
        var mtmp = Path.Combine(dir, "manifest.json.partial");
        File.WriteAllText(mtmp, JsonSerializer.Serialize(manifest, HybridJson.Default.CloudManifest));
        File.Move(mtmp, Path.Combine(dir, "manifest.json"), overwrite: true);
    }, ct);

    public Task GetAsync(string key, string? copyId, string toFile, CancellationToken ct) => Task.Run(() =>
    {
        var source = copyId ?? Path.Combine(GameDir(key), "save.zip");
        if (!File.Exists(source)) throw new FileNotFoundException(L.T("The cloud has no saves for this game yet."), source);
        // Copied whole first: a file the sync app keeps online-only is fetched here, before anything is unpacked.
        File.Copy(source, toFile, overwrite: true);
    }, ct);

    public Task<List<CloudCopy>> HistoryAsync(string key, CancellationToken ct) => Task.Run(() =>
    {
        var dir = Path.Combine(GameDir(key), "history");
        if (!Directory.Exists(dir)) return new List<CloudCopy>();
        return new DirectoryInfo(dir).GetFiles("*.zip").Select(f => new CloudCopy(f.FullName, Path.GetFileNameWithoutExtension(f.Name), f.Length)).ToList();
    }, ct);

    public Task DeleteAsync(string key, CancellationToken ct) => Task.Run(() => { if (Directory.Exists(GameDir(key))) Directory.Delete(GameDir(key), true); }, ct);
}
