using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Cloud;

namespace HybridLauncher.Core;

/// <summary>Cloud saves: on or off, the providers, how many older copies each game keeps.</summary>
public sealed class CloudSaveSettings
{
    public bool Enabled { get; set; }
    /// <summary>
    /// The providers in use, any of "onedrive" (the OneDrive app's folder), "mega" (MEGA, signed in from Hybrid Launcher)
    /// and "folder" (any folder, the fallback): with more than one, every upload goes to each.
    /// </summary>
    public List<string> Providers { get; set; } = [];
    /// <summary>The one provider of settings saved before 1.0; moved into <see cref="Providers"/> on load.</summary>
    public string Provider { get; set; } = "";
    /// <summary>The folder of the "folder" provider.</summary>
    public string? Folder { get; set; }
    public int KeepVersions { get; set; } = 5;
}

/// <summary>What sits next to a game's save.zip in the cloud: whose saves, which content, when and from which PC.</summary>
public sealed class CloudManifest
{
    public string GameId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Bytes { get; set; }
    public int Files { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string Machine { get; set; } = "";
    /// <summary>What the zip holds besides the saves ("saves", "config", "goldberg"); null: the saves alone, at the zip's root.</summary>
    public List<string>? Parts { get; set; }
    /// <summary>With several clouds: true when one that answered does not hold this copy yet (it was added later, or missed an upload).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Partial { get; set; }

    /// <summary>The name of this copy once it is an older one: when and where it was sent.</summary>
    public string HistoryName => $"{UpdatedUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss.fff} {CloudSaves.Safe(Machine)}";
}

/// <summary>An older copy of a game's saves in the cloud (its id is the provider's own: a path, a file id, a node id).</summary>
public sealed record CloudCopy(string Id, string Name, long Bytes);

public enum CloudState { Off, NoFolder, NotUploaded, Synced, ChangedHere, ChangedInCloud, Conflict }

/// <summary>
/// One folder of a game that goes to the cloud: "saves" (the saves), "config" (the game's settings folder, when it is another
/// one) or "goldberg" (the Steam emulator's achievements, stats and remote saves).
/// </summary>
public sealed record CloudPart(string Name, string Dir);

/// <summary>One game's sync state, for the game page and Settings.</summary>
public sealed record CloudStatus(CloudState State, string? SaveDir, CloudManifest? Cloud, DateTime? SyncedAt);

/// <summary>
/// Cloud saves, as Steam Cloud does them, to the user's own cloud: OneDrive (through Windows' OneDrive app, no sign-in here)
/// or MEGA (signed in from Hybrid Launcher, no app to install), or any folder as a fallback (a NAS, a USB drive, a folder another sync app keeps). Each game's saves go up as
/// one zip with a small manifest; older copies stay in the game's history.
///
/// The rule: before a game starts, saves that changed in the cloud and not here come down (the ones on this PC are zipped
/// into Hybrid Launcher's backups first); after it closes, saves that changed here go up (the cloud's previous copy moves to the
/// history). Changed on both sides since the last sync is a conflict: the user picks which one stays, and the other is kept
/// as a copy too. "Changed" is a hash of the files' contents, so copies and dates never fool it.
/// </summary>
public static class CloudSaves
{
    /// <summary>Saves bigger than this are not synced (a wrong folder, a whole game): the user is told instead.</summary>
    public const long MaxBytes = 2L << 30;

    static readonly SemaphoreSlim Gate = new(1);

    public static CloudSaveSettings Settings => SettingsStore.Current.Cloud;

    public static readonly string[] ProviderIds = ["onedrive", "mega", "folder"];

    /// <summary>The provider, ready to use (signed in, or its folder there); null while it is not.</summary>
    static ICloudStore? Ready(string id) => id switch
    {
        "onedrive" when OneDrive.Folder() is { } od => new FolderStore(od, "OneDrive"),
        "mega" when MegaAuth.SignedIn => MegaStore.Instance,
        "folder" when Settings.Folder is { Length: > 0 } f && Directory.Exists(f) => new FolderStore(f),
        _ => null,
    };

    /// <summary>The providers in use that are ready, as one store (several mirror each other); null while none is.</summary>
    public static ICloudStore? Store()
    {
        var ready = ProviderIds.Where(Uses).Select(Ready).OfType<ICloudStore>().ToList();
        return ready.Count switch { 0 => null, 1 => ready[0], _ => new MirrorStore(ready) };
    }

    public static bool Uses(string id) => Settings.Providers.Contains(id);

    /// <summary>Adds or removes a provider (in <see cref="ProviderIds"/> order); the caller saves the settings.</summary>
    public static void Use(string id, bool on)
    {
        var set = Settings.Providers.Where(p => p != id).ToList();
        if (on) set.Add(id);
        Settings.Providers = ProviderIds.Where(set.Contains).ToList();
    }

    /// <summary>The names of the providers in use, for messages ("OneDrive + MEGA").</summary>
    public static string StoreName => Store()?.Name ?? string.Join(" + ", Settings.Providers.Select(ProviderName));

    public static bool On => Settings.Enabled && Store() is not null;

    public static string ProviderName(string id) => id switch
    {
        "onedrive" => "OneDrive",
        "mega" => "MEGA",
        _ => L.T("your folder"),
    };

    // ---------- one game ----------

    /// <summary>The game's name in the cloud: its name and its id, the same on every PC.</summary>
    public static string Key(Game game) => Safe(game.Name) + " [" + Safe(game.Id.Replace(':', '-')) + "]";

    /// <summary>The game's folder, for the folder provider only (the others have no folder on this PC).</summary>
    public static string? GameDir(Game game) => Store() switch
    {
        FolderStore f => f.GameDir(Key(game)),
        MirrorStore m => m.Stores.OfType<FolderStore>().FirstOrDefault()?.GameDir(Key(game)),
        _ => null,
    };

    /// <summary>The marker in a zip with more than the saves: the names of the folders at its root, one per line.</summary>
    const string PartsFile = "hl-parts.txt";
    /// <summary>The same marker in zips sent before the app's renames (1.0, 0.25).</summary>
    static readonly string[] LegacyPartsFiles = [AppInfo.PreviousShort + "-parts.txt", AppInfo.LegacyName.ToLowerInvariant() + "-parts.txt"];

    /// <summary>
    /// The folders of the game that sync, the ones on this PC: its saves, its settings folder when it is another one (never
    /// the install folder, never one inside the saves or holding them) and the Steam emulator's folder of the game.
    /// </summary>
    public static List<CloudPart> Parts(Game game, GameSettings settings)
    {
        var parts = new List<CloudPart>();
        if (settings.CloudSaveDir is { } saves && !TooBroad(saves)) parts.Add(new("saves", saves));
        if (settings.CloudConfigDir is { } config && Directory.Exists(config) && !TooBroad(config) && !Inside(config, game.InstallDir)
            && !(settings.CloudSaveDir is { } s && (Inside(config, s) || Inside(s, config))))
            parts.Add(new("config", config));
        if (GoldbergDir(game, create: false) is { } emu) parts.Add(new("goldberg", emu));
        return parts;
    }

    /// <summary>The Steam emulator's folder of the game (achievements.json, stats, remote saves); null when it runs without one.</summary>
    public static string? GoldbergDir(Game game, bool create)
    {
        if (game.EmulatorAppId is not { Length: > 0 } appId) return null;
        if (Steam.SteamEmulator.SaveDir(appId) is { } found) return found;
        // Brought from the cloud to a PC that never ran the game: gbe_fork's folder, the one it reads first.
        return create ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves", appId) : null;
    }

    static bool Inside(string dir, string? parent)
    {
        if (string.IsNullOrEmpty(parent)) return false;
        try
        {
            var d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + "\\";
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + "\\";
            return d.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The saves alone keep the first layout (the saves at the zip's root, the same hash): clouds filled before stay in sync.</summary>
    static bool SavesOnly(List<CloudPart> parts) => parts is [{ Name: "saves" }];

    public static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 80 ? clean[..80] : clean.Length == 0 ? "game" : clean;
    }

    // The manifest asked for by every game page, every Play and every close: a few seconds of memory spare the network.
    static readonly Dictionary<string, (CloudManifest? Manifest, DateTime At)> Recent = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<CloudManifest?> ReadManifestAsync(Game game, bool fresh = false, CancellationToken ct = default)
    {
        var store = Store() ?? throw new InvalidOperationException(L.T("The cloud is not available."));
        var key = store.Name + "|" + Key(game);
        lock (Recent)
            if (!fresh && Recent.TryGetValue(key, out var r) && DateTime.UtcNow - r.At < TimeSpan.FromSeconds(20)) return r.Manifest;
        var manifest = await store.ManifestAsync(Key(game), ct);
        lock (Recent) Recent[key] = (manifest, DateTime.UtcNow);
        return manifest;
    }

    static void Forget(Game game)
    {
        lock (Recent) foreach (var k in Recent.Keys.Where(k => k.EndsWith("|" + Key(game), StringComparison.OrdinalIgnoreCase)).ToList()) Recent.Remove(k);
    }

    /// <summary>A hash of every file's path and content (sorted), plus the total size and count. Empty folder: "".</summary>
    public static (string Hash, long Bytes, int Files) Fingerprint(string dir) => Fingerprint([new CloudPart("saves", dir)]);

    /// <summary>Every file of the parts, as its path in the zip ("saves/…" and so on; the saves alone: at the root).</summary>
    static List<(string Full, string Rel)> FilesOf(List<CloudPart> parts) => parts
        .Where(p => Directory.Exists(p.Dir))
        .SelectMany(p => Directory.EnumerateFiles(p.Dir, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: (SavesOnly(parts) ? "" : p.Name + "/") + Path.GetRelativePath(p.Dir, f).Replace('\\', '/'))))
        .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A hash of every part's files (paths and contents, sorted), plus the total size and count. Nothing there: "".</summary>
    public static (string Hash, long Bytes, int Files) Fingerprint(List<CloudPart> parts)
    {
        var files = FilesOf(parts);
        if (files.Count == 0) return ("", 0, 0);
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        foreach (var (full, rel) in files)
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            bytes += fs.Length;
            if (bytes > MaxBytes) throw new InvalidOperationException(L.F("The saves folder is larger than {0}: it is not synced. Check the folder in the game's cloud menu.", DiskUsage.Format(MaxBytes)));
            all.AppendData(Encoding.UTF8.GetBytes(rel.ToLowerInvariant() + "\n"));
            all.AppendData(SHA256.HashData(fs));
        }
        return (Convert.ToHexString(all.GetHashAndReset())[..32], bytes, files.Count);
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// A folder that holds far more than one game's saves (a drive, the user folder, Documents, Saved Games, AppData itself):
    /// never synced, whatever the wiki or a wrong pick says.
    /// </summary>
    public static bool TooBroad(string dir)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            if (Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root).Equals(full, StringComparison.OrdinalIgnoreCase)) return true;
            var broad = new[]
            {
                Home, Path.Combine(Home, "Saved Games"), Path.Combine(Home, "Documents"), Path.Combine(Home, "Documents", "My Games"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? Home, "LocalLow"),
                Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? Home,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            return broad.Any(b => b.Length > 0 && Path.TrimEndingDirectorySeparator(b).Equals(full, StringComparison.OrdinalIgnoreCase));
        }
        catch { return true; }
    }

    /// <summary>Where the game stands: compared with the last sync on this PC and with the cloud (hashes the saves, off the UI thread).</summary>
    public static async Task<CloudStatus> StatusAsync(Game game, GameSettings settings, bool fresh = false, CancellationToken ct = default)
    {
        if (!On || settings.CloudOff) return new(CloudState.Off, settings.CloudSaveDir, null, settings.CloudSyncedAt);
        var parts = Parts(game, settings);
        var dir = parts.FirstOrDefault(p => p.Name == "saves")?.Dir;
        if (parts.Count == 0) return new(CloudState.NoFolder, null, null, settings.CloudSyncedAt);
        var cloudTask = ReadManifestAsync(game, fresh, ct);
        var local = (await Task.Run(() => Fingerprint(parts), ct)).Hash;
        var cloud = await cloudTask;
        return new CloudStatus(Decide(local, cloud, settings.CloudSyncedHash), dir, cloud, settings.CloudSyncedAt);
    }

    static CloudState Decide(string local, CloudManifest? manifest, string? synced)
    {
        var cloud = manifest?.Hash;
        if (cloud is null) return local.Length == 0 ? CloudState.Synced : CloudState.NotUploaded;
        // The same saves, but not in every cloud yet (OneDrive turned on beside MEGA, say): they go up to the ones missing them.
        if (cloud == local) return manifest!.Partial ? CloudState.NotUploaded : CloudState.Synced;
        bool hereChanged = local.Length > 0 && local != synced;
        bool cloudChanged = cloud != synced;
        // Never synced on this PC: saves only in the cloud come down; saves on both sides that differ are a conflict.
        if (synced is null) return local.Length == 0 ? CloudState.ChangedInCloud : CloudState.Conflict;
        return hereChanged && cloudChanged ? CloudState.Conflict : hereChanged ? CloudState.ChangedHere : CloudState.ChangedInCloud;
    }

    /// <summary>Sends this PC's saves to the cloud; the cloud's copy before them goes to the game's history.</summary>
    public static async Task UploadAsync(Game game, GameSettings settings, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { await Upload(game, settings, ct); }
        finally { Gate.Release(); }
    }

    static async Task Upload(Game game, GameSettings settings, CancellationToken ct)
    {
        var parts = Parts(game, settings);
        if (parts.Count == 0) throw new InvalidOperationException(L.T("No saves folder for this game."));
        var store = Store() ?? throw new InvalidOperationException(L.T("The cloud is not available."));
        var (hash, bytes, files) = await Task.Run(() => Fingerprint(parts), ct);
        if (files == 0) { Log.Info($"Cloud: {game.Name} has no saves to send"); return; }
        var old = await ReadManifestAsync(game, fresh: true, ct);
        if (old?.Hash == hash && !old.Partial) { Remember(settings, hash); return; }

        var zip = Path.Combine(AppInfo.Ensure(Path.Combine(AppInfo.CacheDir, "cloud-upload")), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await Task.Run(() => Zip(parts, zip, CompressionLevel.Optimal), ct);
            var manifest = new CloudManifest
            {
                GameId = game.Id, Name = game.Name, Hash = hash, Bytes = bytes, Files = files, UpdatedUtc = DateTime.UtcNow, Machine = Environment.MachineName,
                Parts = SavesOnly(parts) ? null : parts.Select(p => p.Name).ToList(),
            };
            await store.PutAsync(Key(game), zip, manifest, old, Settings.KeepVersions, ct);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            Forget(game);
        }
        Remember(settings, hash);
        Log.Info($"Cloud: {game.Name} sent to {store.Name} ({string.Join(", ", parts.Select(p => p.Name))}: {files} files, {DiskUsage.Format(bytes)})");
    }

    /// <summary>The parts as one zip: the saves alone at its root (the first layout), else one folder per part and the marker.</summary>
    static void Zip(List<CloudPart> parts, string zip, CompressionLevel level)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (full, rel) in FilesOf(parts))
        {
            var entry = archive.CreateEntry(rel, level);
            entry.LastWriteTime = File.GetLastWriteTime(full);
            using var from = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var to = entry.Open();
            from.CopyTo(to);
        }
        if (SavesOnly(parts)) return;
        using var marker = new StreamWriter(archive.CreateEntry(PartsFile).Open());
        marker.Write(string.Join("\n", parts.Select(p => p.Name)));
    }

    /// <summary>Brings the cloud's saves (or an older copy) to this PC; the saves here are zipped into Hybrid Launcher's backups first.</summary>
    public static async Task RestoreAsync(Game game, GameSettings settings, CloudCopy? copy = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { await Restore(game, settings, copy, ct); }
        finally { Gate.Release(); }
    }

    static async Task Restore(Game game, GameSettings settings, CloudCopy? copy, CancellationToken ct)
    {
        var store = Store() ?? throw new InvalidOperationException(L.T("The cloud is not available."));
        var manifest = copy is null ? await ReadManifestAsync(game, fresh: true, ct) ?? throw new FileNotFoundException(L.T("The cloud has no saves for this game yet.")) : null;

        // Downloaded whole and unpacked away from the saves first: a slow or broken download never leaves half a save here.
        var staging = Path.Combine(AppInfo.CacheDir, "cloud-restore", Guid.NewGuid().ToString("N"));
        var zip = staging + ".zip";
        try
        {
            Directory.CreateDirectory(staging);
            await store.GetAsync(Key(game), copy?.Id, zip, ct);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging), ct);
            // Too late (the game is starting with the saves here): nothing on this PC was touched yet.
            ct.ThrowIfCancellationRequested();
            // Where each folder of the zip goes on this PC. The first layout is the saves alone; a part this PC has no folder
            // for (a settings folder PCGamingWiki does not name here) is left out, said in the log.
            var marker = Path.Combine(staging, PartsFile);
            if (!File.Exists(marker) && LegacyPartsFiles.Select(f => Path.Combine(staging, f)).FirstOrDefault(File.Exists) is { } oldMarker) File.Move(oldMarker, marker);
            bool layered = File.Exists(marker);
            var targets = new List<CloudPart>();
            if (!layered)
                targets.Add(new("saves", settings.CloudSaveDir ?? throw new InvalidOperationException(L.T("No saves folder for this game."))));
            else
            {
                foreach (var name in File.ReadAllLines(marker).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct())
                {
                    var to = name switch { "saves" => settings.CloudSaveDir, "config" => settings.CloudConfigDir, "goldberg" => GoldbergDir(game, create: true), _ => null };
                    if (to is null || TooBroad(to)) { Log.Warn($"Cloud: {game.Name} has no folder here for its \"{name}\", left out"); continue; }
                    if (Directory.Exists(Path.Combine(staging, name))) targets.Add(new(name, to));
                }
                if (targets.Count == 0) throw new InvalidOperationException(L.T("No saves folder for this game."));
            }
            await Task.Run(() =>
            {
                var here = targets.Where(t => Directory.Exists(t.Dir) && Directory.EnumerateFileSystemEntries(t.Dir).Any()).ToList();
                if (here.Count > 0) BackupLocal(game, here);
                foreach (var t in targets)
                {
                    var from = layered ? Path.Combine(staging, t.Name) : staging;
                    Directory.CreateDirectory(t.Dir);
                    // Written over, nothing deleted: a file only this PC has stays (and goes up after the next session).
                    foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    {
                        var target = Path.Combine(t.Dir, Path.GetRelativePath(from, f));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(f, target, overwrite: true);
                    }
                }
            }, CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
            try { File.Delete(zip); } catch { }
        }
        // A restored older copy is now what this PC has: it is a local change, sent up after the next session (or now).
        Remember(settings, manifest?.Hash ?? settings.CloudSyncedHash ?? "");
        Log.Info($"Cloud: {game.Name} restored from {store.Name} ({copy?.Name ?? "latest"})");
    }

    /// <summary>The saves on this PC as a zip in Hybrid Launcher's backups (the last few per game), before anything replaces them.</summary>
    static void BackupLocal(Game game, List<CloudPart> parts)
    {
        var backups = AppInfo.Ensure(LocalBackupsDir(game));
        Zip(parts, Path.Combine(backups, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss.fff}.zip"), CompressionLevel.Fastest);
        foreach (var f in new DirectoryInfo(backups).GetFiles("*.zip").OrderByDescending(f => f.Name).Skip(Math.Max(3, Settings.KeepVersions)))
            try { f.Delete(); } catch { }
    }

    public static string LocalBackupsDir(Game game) => Path.Combine(AppInfo.BackupsDir, "cloud saves", Safe(game.Name));

    /// <summary>The game's older cloud copies, newest first.</summary>
    public static async Task<List<CloudCopy>> HistoryAsync(Game game, CancellationToken ct = default)
    {
        if (Store() is not { } store) return [];
        return (await store.HistoryAsync(Key(game), ct)).OrderByDescending(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Removes the game's saves from the cloud (every copy); the saves on this PC stay.</summary>
    public static async Task DeleteFromCloudAsync(Game game, CancellationToken ct = default)
    {
        if (Store() is not { } store) return;
        await store.DeleteAsync(Key(game), ct);
        Forget(game);
    }

    static void Remember(GameSettings settings, string hash)
    {
        settings.CloudSyncedHash = hash;
        settings.CloudSyncedAt = DateTime.Now;
    }

    // ---------- the automatic rule ----------

    /// <summary>What to do with a conflict, asked of the user.</summary>
    public enum Choice { UseCloud, KeepHere, Skip }

    /// <summary>
    /// Before the game starts: saves changed in the cloud (and not here) come down; changed here only go up; changed on both
    /// sides asks <paramref name="conflict"/>. Returns a short line for the user when something was done, else null.
    /// </summary>
    public static async Task<string?> BeforeLaunchAsync(Game game, GameSettings settings, Func<CloudStatus, Task<Choice>> conflict, CancellationToken ct = default)
    {
        var status = await StatusAsync(game, settings, fresh: true, ct);
        switch (status.State)
        {
            case CloudState.ChangedInCloud:
                await RestoreAsync(game, settings, null, ct);
                return L.F("Saves from {0} ({1}) brought to this PC.", status.Cloud?.Machine, status.Cloud?.UpdatedUtc.ToLocalTime().ToString("g"));
            case CloudState.ChangedHere or CloudState.NotUploaded:
                await UploadAsync(game, settings, ct);
                return null;
            case CloudState.Conflict:
                switch (await conflict(status))
                {
                    // The user answered: no time limit on what they chose (the question may have waited longer than it).
                    case Choice.UseCloud: await RestoreAsync(game, settings, null, CancellationToken.None); return L.T("The cloud's saves are on this PC now; the ones here are in Hybrid Launcher's backups.");
                    case Choice.KeepHere: await UploadAsync(game, settings, CancellationToken.None); return L.T("This PC's saves are in the cloud now; the cloud's are kept in its history.");
                }
                return null;
            default:
                if (status.State == CloudState.Synced && status.Cloud is { } c && settings.CloudSyncedHash != c.Hash) Remember(settings, c.Hash);
                return null;
        }
    }

    /// <summary>After the game closed: what changed here goes up (the cloud's copy before it goes to the history).</summary>
    public static async Task<bool> AfterSessionAsync(Game game, GameSettings settings, CancellationToken ct = default)
    {
        var status = await StatusAsync(game, settings, fresh: true, ct);
        if (status.State is not (CloudState.ChangedHere or CloudState.NotUploaded or CloudState.Conflict)) return false;
        await UploadAsync(game, settings, ct);
        return true;
    }
}
