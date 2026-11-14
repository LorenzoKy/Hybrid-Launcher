using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HybridLauncher.Core.Cloud;

/// <summary>
/// "Hybrid Config and Stats" (2.1-rc.4): Hybrid Launcher's own settings and numbers in the cloud the cloud saves use, so
/// another PC (or this one after a reinstall) gets the same preferences, play time, sessions, achievement history, profile
/// pictures and hand-picked artwork. On by default with the cloud saves; Settings → Cloud saves turns it off.
///
/// One zip in the cloud ("Hybrid Config and Stats" beside the games' saves) holds the files and a small header (the bundle's
/// format, the app's version, the PC, the time). It is merged, never simply copied over:
///  • play time, sessions and the achievement history are unions: nothing either PC counted is ever lost (the bigger count
///    of a game wins, sessions and unlocks are merged by their time);
///  • profile pictures and artwork files: the ones missing here are added;
///  • the settings, three ways, key by key, against the copy both PCs last agreed on: a key changed on one side only takes
///    that side's value, one changed on both takes the newer side's; a game's settings are merged the same way, game by game.
///    What belongs to one PC (window size, folders, accounts, installed mods, the cloud's own setup) never travels.
///    A key this version does not know (written by a newer Hybrid Launcher) is kept as it is and sent back, so an older
///    version never strips what a newer one added; if the merged settings cannot be read, this PC keeps its own.
/// Paths inside the data folder travel as "%HL_DATA%\…" and come back as this PC's data folder.
/// It is pulled once at start (a few seconds at most, before the window) and sent every fifteen minutes when something
/// changed, and when the app closes.
/// </summary>
public static class ConfigSync
{
    /// <summary>The bundle's name in the cloud (beside the games' saves).</summary>
    public const string Key = "Hybrid Config and Stats";

    /// <summary>The bundle's format. A newer one is still read: what it holds that this version does not know is kept.</summary>
    public const int Schema = 1;

    const string HeaderFile = "hl-config.json", SettingsName = "settings.json", Token = "%HL_DATA%";

    /// <summary>Top-level settings that describe this PC, not the user: never sent, never taken.</summary>
    static readonly HashSet<string> MachineKeys = new(StringComparer.Ordinal)
    {
        "Version", "SidebarWidth", "WindowWidth", "WindowHeight", "WindowMaximized", "LastGameId", "CacheWarmed", "HdrRestore", "GameTab",
        "SettingsTab", "Accounts", "SteamGridDbKey", "GameLibraries", "CustomGames", "DirectInstalls", "InstallRoot", "InstallRoots", "Cloud",
        "CustomBuildsDir", "ModBackups", "BackupGameFiles", "LocalAchievementFolders", "AchScreenshotDir", "StartWithWindows", "HardwareAcceleration",
        "ModsPathDefaultApplied", "TourSeen", "LastVersionSeen", "FeaturesChosen", "OptiSeen", "UltraPlusSeen", "SkippedAppUpdate",
        "AppUpdatePopupShown", "CustomBackground", "ScanSteam", "ScanEpic", "ScanGog", "ScanXbox", "ScanUbisoft", "ScanEa", "ScanRockstar",
    };

    /// <summary>A game's settings that describe its files on this PC (its exe, installed mods, sizes, the cloud's own marks).</summary>
    static readonly HashSet<string> MachineGameKeys = new(StringComparer.Ordinal)
    {
        "ExeOverride", "Install", "Setup", "AsiPlugins", "CustomDlls", "SizeBytes", "SizeMeasuredAt", "CloudSaveDir", "CloudSaveDirPicked",
        "CloudConfigDir", "CloudConfigChecked", "CloudSyncedHash", "CloudSyncedAt", "Luma", "UltraPlus", "ReShade", "RenoDxFiles", "Dxvk",
        "DlssNrAmdReShade", "DismissedModProblems",
    };

    /// <summary>Folders of the data folder whose files travel (added where missing, never deleted).</summary>
    static readonly string[] FileFolders = ["profiles", "covers"];

    static string StateDir => AppInfo.Ensure(Path.Combine(AppInfo.CacheDir, "config-sync"));
    static string BaseFile => Path.Combine(StateDir, "base-settings.json");
    static string PendingFile => Path.Combine(StateDir, "pending-settings.json");
    // This PC's settings as they were when the pending ones were merged from them: what changed here since then is kept over them.
    static string PendingBaseFile => Path.Combine(StateDir, "pending-base.json");
    static string StateFile => Path.Combine(StateDir, "state.json");
    // The fingerprint of the settings that travel and when it last changed (see LocalAt).
    static string EditFile => Path.Combine(StateDir, "local-edit.json");

    static readonly SemaphoreSlim Gate = new(1);

    /// <summary>Smoke tests only: runs just before the bundle is sent, to make the send fail.</summary>
    internal static Func<Task>? PutFault;

    /// <summary>Smoke tests: when this PC's settings that travel last changed (see LocalAt).</summary>
    internal static DateTime LocalEditAt() => LocalAt();

    /// <summary>Whether the sync runs: cloud saves on (a provider ready) and the setting on.</summary>
    public static bool On => CloudSaves.On && SettingsStore.Current.Cloud.SyncConfig;

    /// <summary>When the bundle was last sent or taken (Settings shows it); null before the first time.</summary>
    public static DateTime? LastSync => ReadState().At;

    // ---------- start ----------

    /// <summary>
    /// At start, after the settings were read: merges the cloud's bundle into this PC's files (at most <paramref name="limit"/>,
    /// the window waits for it) and reads the settings again when they changed. True when they did.
    /// </summary>
    public static bool PullAtStart(TimeSpan limit)
    {
        if (!On) return false;
        using var cts = new CancellationTokenSource(limit);
        try
        {
            var task = Task.Run(() => PullAsync(cts.Token));
            if (!task.Wait(limit + TimeSpan.FromMilliseconds(500))) { Log.Warn("Config sync: the cloud did not answer in time, the next start tries again"); return false; }
            if (!task.Result) return false;
            SettingsStore.Load();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Config sync at start: {(ex as AggregateException)?.GetBaseException().Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>Takes the cloud's bundle when it changed since the last sync and merges it here. True when settings.json was rewritten.</summary>
    public static async Task<bool> PullAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            bool rewrote = ApplyPending(ct);
            var store = CloudSaves.Store();
            if (store is not null)
            {
                var manifest = await store.ManifestAsync(Key, ct);
                var state = ReadState();
                if (manifest is not null && manifest.Hash != state.CloudHash)
                {
                    var dir = await DownloadAsync(store, ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        rewrote |= Merge(dir, manifest.UpdatedUtc, writeSettings: true, ct);
                        WriteState(state with { CloudHash = manifest.Hash, At = DateTime.UtcNow });
                        Log.Info($"Config sync: merged the bundle from {manifest.Machine} ({manifest.UpdatedUtc.ToLocalTime():g})");
                    }
                    finally { TryDelete(dir); }
                }
            }
            if (rewrote) SettingsStore.Load();
            return rewrote;
        }
        finally { Gate.Release(); }
    }

    // ---------- send ----------

    /// <summary>
    /// Sends this PC's settings and numbers when they changed since the last send. The cloud's bundle is merged in first when
    /// another PC sent one meanwhile: its numbers join this PC's at once, its settings at the next start (the running app keeps
    /// its own until then).
    /// </summary>
    public static async Task PushAsync(CancellationToken ct)
    {
        if (!On) return;
        await Gate.WaitAsync(ct);
        string? dir = null, zip = null;
        try
        {
            var store = CloudSaves.Store();
            if (store is null) return;
            // What the app saved a moment ago is on disk before it is read (2.1 audit: the save is written in the background).
            SettingsStore.Flush();
            var state = ReadState();
            var cloud = await store.ManifestAsync(Key, ct);
            // The copy both PCs last agreed on, read before a merge below moves it to the cloud's (2.1 audit: merged against
            // the cloud's own copy, every key looked changed here only, and the send undid the other PC's changes).
            var baseBefore = ReadBase();
            var raw = LocalSettings();
            if (cloud is not null && cloud.Hash != state.CloudHash)
            {
                dir = await DownloadAsync(store, ct);
                Merge(dir, cloud.UpdatedUtc, writeSettings: false, ct);
            }
            var settings = MergedSettingsForCloud(dir, cloud?.UpdatedUtc ?? DateTime.MinValue, baseBefore);
            var hash = Fingerprint(settings);
            if (cloud is not null && hash == state.SentHash && cloud.Hash == state.CloudHash) return;

            zip = Path.Combine(StateDir, Guid.NewGuid().ToString("N") + ".zip");
            await Task.Run(() => Pack(settings, zip), ct);
            var manifest = new CloudManifest
            {
                GameId = "hybrid-config", Name = Key, Hash = hash, Bytes = new FileInfo(zip).Length, Files = 1,
                UpdatedUtc = DateTime.UtcNow, Machine = Environment.MachineName,
            };
            if (PutFault is { } fault) await fault();
            await store.PutAsync(Key, zip, manifest, cloud, 3, ct);
            File.WriteAllText(BaseFile, settings.ToJsonString());
            // The running app keeps its settings until the next start: what was sent becomes the pending settings, taken
            // then over the copy of this PC's they were merged from (a pending older one is folded in already).
            if (dir is not null || File.Exists(PendingFile)) KeepPending((JsonObject)Expand(settings)!, raw);
            WriteState(new SyncState(hash, hash, DateTime.UtcNow));
            Log.Info($"Config sync: sent ({DiskUsage.Format(manifest.Bytes)})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Config sync: {ex.Message}"); }
        finally
        {
            Gate.Release();
            if (zip is not null) TryDelete(zip);
            if (dir is not null) TryDelete(dir);
        }
    }

    /// <summary>At exit: sends what changed, waiting at most <paramref name="limit"/>.</summary>
    public static void PushAtExit(TimeSpan limit)
    {
        if (!On) return;
        try
        {
            using var cts = new CancellationTokenSource(limit);
            Task.Run(() => PushAsync(cts.Token)).Wait(limit + TimeSpan.FromMilliseconds(300));
        }
        catch (Exception ex) { Log.Warn($"Config sync at exit: {ex.GetBaseException().Message}"); }
    }

    // ---------- the bundle ----------

    static async Task<string> DownloadAsync(ICloudStore store, CancellationToken ct)
    {
        var zip = Path.Combine(StateDir, Guid.NewGuid().ToString("N") + ".zip");
        var dir = zip[..^4];
        try
        {
            await store.GetAsync(Key, null, zip, ct);
            ZipFile.ExtractToDirectory(zip, dir);
            return dir;
        }
        finally { TryDelete(zip); }
    }

    /// <summary>
    /// The settings as they go to the cloud: this PC's (with settings merged while it ran) merged with the cloud's (or the
    /// last agreed copy) against <paramref name="baseCopy"/>, paths tokenised.
    /// </summary>
    static JsonObject MergedSettingsForCloud(string? cloudDir, DateTime cloudAt, JsonObject? baseCopy)
    {
        var local = EffectiveLocal();
        var cloud = cloudDir is not null && File.Exists(Path.Combine(cloudDir, SettingsName))
            ? Expand(ReadObject(File.ReadAllText(Path.Combine(cloudDir, SettingsName)))) as JsonObject : baseCopy;
        var merged = MergeSettings(local, cloud, baseCopy, localNewer: LocalAt() > cloudAt);
        foreach (var k in MachineKeys) merged.Remove(k);
        if (merged["Games"] is JsonObject games)
            foreach (var (_, g) in games) if (g is JsonObject o) foreach (var k in MachineGameKeys) o.Remove(k);
        return (JsonObject)Tokenise(merged)!;
    }

    static void Pack(JsonObject settings, string zip)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        void Text(string name, string text)
        {
            using var w = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        void Add(string full, string name)
        {
            if (!File.Exists(full)) return;
            using var from = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var to = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
            from.CopyTo(to);
        }
        var header = new JsonObject
        {
            ["schema"] = Schema, ["app"] = AppUpdates.DisplayVersion, ["machine"] = Environment.MachineName, ["at"] = DateTime.UtcNow.ToString("O"),
        };
        Text(HeaderFile, header.ToJsonString());
        Text(SettingsName, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Add(PlaytimeLedger.File, "playtime.json");
        Add(SessionLog.File_, "sessions.jsonl");
        Add(Achievements.AchievementHistory.File_, "achievements/history.jsonl");
        foreach (var folder in FileFolders)
        {
            var root = Path.Combine(AppInfo.DataRoot, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
                if (new FileInfo(f).Length <= 25L << 20) Add(f, folder + "/" + Path.GetFileName(f));
        }
    }

    /// <summary>Merges an unpacked bundle into this PC's files. True when settings.json was rewritten (<paramref name="writeSettings"/>).</summary>
    static bool Merge(string dir, DateTime cloudAt, bool writeSettings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        MergeLedger(Path.Combine(dir, "playtime.json"));
        lock (SessionLog.Gate) MergeLines(Path.Combine(dir, "sessions.jsonl"), SessionLog.File_, SessionKey);
        bool unlocks;
        lock (Achievements.AchievementHistory.Gate) unlocks = MergeLines(Path.Combine(dir, "achievements", "history.jsonl"), Achievements.AchievementHistory.File_, HistoryKey);
        SessionLog.Reload();
        // What shows the unlocks reads them again (2.1-rc.10 audit: a merge while the app ran left them stale).
        if (unlocks) Achievements.AchievementHistory.NotifyChanged();
        foreach (var folder in FileFolders)
        {
            var from = Path.Combine(dir, folder);
            if (!Directory.Exists(from)) continue;
            var to = AppInfo.Ensure(Path.Combine(AppInfo.DataRoot, folder));
            foreach (var f in Directory.EnumerateFiles(from))
            {
                var target = Path.Combine(to, Path.GetFileName(f));
                if (!File.Exists(target)) File.Copy(f, target);
            }
        }

        var cloudFile = Path.Combine(dir, SettingsName);
        if (!File.Exists(cloudFile)) return false;
        var cloud = (JsonObject?)Expand(ReadObject(File.ReadAllText(cloudFile))) ?? new JsonObject();
        var raw = LocalSettings();
        var local = EffectiveLocal(raw);
        var merged = MergeSettings(local, cloud, ReadBase(), localNewer: LocalAt() > cloudAt);
        var json = merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        // The merged settings must still read as settings: a bundle a future version changed beyond reading leaves this PC's own.
        // A value only a newer version knows (a new channel, say) is left out as SettingsStore.Load does, not the whole merge.
        try { if (JsonSerializer.Deserialize(json, HybridJson.Default.AppSettings) is null) return false; }
        catch (JsonException ex) when (SettingsStore.TryReadLenient(json, ex, "the cloud's settings") is not null) { }
        catch (Exception ex) { Log.Warn($"Config sync: the cloud's settings cannot be read by this version, this PC keeps its own ({ex.Message})"); return false; }
        // A pull agrees on the cloud's copy now; a push (writeSettings false) only once the cloud took what it sends (2.1-rc.10
        // audit: a send that failed after the merge had moved the base already, and the next merge missed this PC's changes).
        if (writeSettings) File.WriteAllText(BaseFile, Tokenise(cloud)!.ToJsonString());
        if (JsonNode.DeepEquals(merged, raw)) { DropPending(); return false; }
        ct.ThrowIfCancellationRequested();
        if (!writeSettings)
        {
            // The app is running with its settings in memory: they are taken at the next start.
            KeepPending(merged, raw);
            return false;
        }
        WriteSettings(json);
        return true;
    }

    /// <summary>Settings merged while the app ran (another PC's changes): written now, before the settings are read again.</summary>
    static bool ApplyPending(CancellationToken ct)
    {
        if (!File.Exists(PendingFile)) return false;
        try
        {
            var local = LocalSettings();
            var merged = EffectiveLocal(local);
            ct.ThrowIfCancellationRequested();
            DropPending();
            if (JsonNode.DeepEquals(merged, local)) return false;
            WriteSettings(merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Config sync pending settings: {ex.Message}"); DropPending(); return false; }
    }

    /// <summary>
    /// This PC's settings with the pending ones (merged while the app ran) taken in: a key changed here since they were merged
    /// keeps this PC's value, any other the pending one. Without pending settings, this PC's as they are.
    /// </summary>
    static JsonObject EffectiveLocal(JsonObject? local = null)
    {
        local ??= LocalSettings();
        try
        {
            if (!File.Exists(PendingFile)) return local;
            var pending = ReadObject(File.ReadAllText(PendingFile));
            var at = File.Exists(PendingBaseFile) ? ReadObject(File.ReadAllText(PendingBaseFile)) : ReadBase();
            return MergeSettings(local, pending, at, localNewer: true);
        }
        catch (Exception ex) { Log.Warn($"Config sync pending settings: {ex.Message}"); return local; }
    }

    /// <summary>Keeps <paramref name="pending"/> for the next start, with this PC's settings it was merged from.</summary>
    static void KeepPending(JsonObject pending, JsonObject mergedFrom)
    {
        // Only the keys that travel: a machine key missing from what was sent must not look removed.
        var copy = (JsonObject)pending.DeepClone();
        foreach (var k in MachineKeys) copy.Remove(k);
        if (JsonNode.DeepEquals(MergeSettings(mergedFrom, copy, mergedFrom, localNewer: true), mergedFrom)) { DropPending(); return; }
        File.WriteAllText(PendingFile, copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(PendingBaseFile, mergedFrom.ToJsonString());
    }

    static void DropPending()
    {
        TryDelete(PendingFile);
        TryDelete(PendingBaseFile);
    }

    static void WriteSettings(string json)
    {
        SettingsStore.Flush();
        var file = AppInfo.SettingsFile;
        var tmp = file + ".sync.tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(file)) File.Replace(tmp, file, file + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, file);
    }

    // ---------- settings: three-way merge ----------

    /// <summary>
    /// Merges <paramref name="local"/> and <paramref name="cloud"/> against <paramref name="baseCopy"/> (what both last
    /// agreed on, null before the first sync). Machine keys stay local. A key changed on one side only takes that side; on
    /// both (or with no base), <paramref name="localNewer"/> decides. Keys only the cloud has that this version does not know
    /// are kept. A game's play time is the bigger of the two.
    /// </summary>
    public static JsonObject MergeSettings(JsonObject local, JsonObject? cloud, JsonObject? baseCopy, bool localNewer)
    {
        var merged = (JsonObject)local.DeepClone();
        if (cloud is null) return merged;
        foreach (var (key, cv) in cloud)
        {
            if (MachineKeys.Contains(key)) continue;
            if (key == "Games") { merged["Games"] = MergeGames(local["Games"] as JsonObject, cv as JsonObject, baseCopy?["Games"] as JsonObject, localNewer); continue; }
            if (!local.ContainsKey(key))
            {
                // Removed here since the base and not changed in the cloud: it stays removed (2.1-rc.10 audit: it came back).
                if (baseCopy is not null && baseCopy.ContainsKey(key) && JsonNode.DeepEquals(baseCopy[key], cv)) continue;
                // Not known here (a newer version's setting): kept as it is. Known but missing here: the cloud's.
                merged[key] = cv?.DeepClone();
                continue;
            }
            merged[key] = Pick(local[key], cv, baseCopy?[key], localNewer)?.DeepClone();
        }
        // Removed in the cloud since the base and not changed here: removed here too (machine keys never travel).
        if (baseCopy is not null)
            foreach (var key in local.Select(p => p.Key).ToList())
                if (!MachineKeys.Contains(key) && key != "Games" && !cloud.ContainsKey(key) && baseCopy.ContainsKey(key) && JsonNode.DeepEquals(local[key], baseCopy[key]))
                    merged.Remove(key);
        return merged;
    }

    static JsonObject MergeGames(JsonObject? local, JsonObject? cloud, JsonObject? baseGames, bool localNewer)
    {
        var merged = (JsonObject?)local?.DeepClone() ?? new JsonObject();
        if (cloud is null) return merged;
        foreach (var (id, cg) in cloud)
        {
            if (cg is not JsonObject c) continue;
            if (merged[id] is not JsonObject l)
            {
                // A game's settings removed here since the base, the cloud's unchanged: they stay removed.
                if (baseGames?[id] is JsonObject gone && JsonNode.DeepEquals(Travelling(gone), Travelling(c))) continue;
                var copy = (JsonObject)c.DeepClone();
                foreach (var k in MachineGameKeys) copy.Remove(k);
                merged[id] = copy;
                continue;
            }
            var b = baseGames?[id] as JsonObject;
            foreach (var (key, cv) in c)
            {
                if (MachineGameKeys.Contains(key)) continue;
                if (key == "PlayMinutes")
                {
                    l[key] = Math.Max(Number(l[key]), Number(cv));
                    continue;
                }
                if (key == "LastPlayed")
                {
                    if (Date(cv) is { } z && (Date(l[key]) is not { } a || z > a)) l[key] = cv!.DeepClone();
                    continue;
                }
                l[key] = Pick(l[key], cv, b?[key], localNewer)?.DeepClone();
            }
        }
        // A game's settings removed in the cloud since the base, unchanged here: what travels goes; what describes this PC's
        // files (installed mods, its exe) stays, so a mod installed here is never forgotten.
        if (baseGames is not null && local is not null)
            foreach (var (id, lg) in local)
            {
                if (cloud.ContainsKey(id) || lg is not JsonObject l || baseGames[id] is not JsonObject b || !JsonNode.DeepEquals(Travelling(l), Travelling(b))) continue;
                if (merged[id] is not JsonObject m) continue;
                foreach (var k in m.Select(p => p.Key).Where(k => !MachineGameKeys.Contains(k)).ToList()) m.Remove(k);
                if (m.Count == 0) merged.Remove(id);
            }
        return merged;
    }

    /// <summary>A game's settings without the keys that describe this PC (the part that travels).</summary>
    static JsonObject Travelling(JsonObject game)
    {
        var copy = (JsonObject)game.DeepClone();
        foreach (var k in MachineGameKeys) copy.Remove(k);
        return copy;
    }

    /// <summary>One key: the side that changed it since the base; both changed (or no base): the newer side.</summary>
    static JsonNode? Pick(JsonNode? local, JsonNode? cloud, JsonNode? baseValue, bool localNewer)
    {
        if (JsonNode.DeepEquals(local, cloud)) return local;
        bool localChanged = !JsonNode.DeepEquals(local, baseValue), cloudChanged = !JsonNode.DeepEquals(cloud, baseValue);
        if (baseValue is not null || local is null || cloud is null)
        {
            if (cloudChanged && !localChanged) return cloud;
            if (localChanged && !cloudChanged) return local;
        }
        return localNewer ? local : cloud;
    }

    static long Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
    static DateTime? Date(JsonNode? n) => n is JsonValue v && v.TryGetValue<DateTime>(out var d) ? d : null;

    /// <summary>
    /// When this PC's settings that travel were last changed: on a key changed on both sides, the newer side wins. Not the
    /// file's own time (2.1-rc.10 audit): the window's size or the last game opened rewrite it too, and an idle PC won over a
    /// real change made on the other one. The travelling part's fingerprint is kept with the time it was first seen.
    /// </summary>
    static DateTime LocalAt()
    {
        if (!File.Exists(AppInfo.SettingsFile)) return DateTime.MinValue;
        var written = File.GetLastWriteTimeUtc(AppInfo.SettingsFile);
        try
        {
            var travelling = LocalSettings();
            foreach (var k in MachineKeys) travelling.Remove(k);
            if (travelling["Games"] is JsonObject games)
                foreach (var id in games.Select(p => p.Key).ToList()) if (games[id] is JsonObject g) games[id] = Travelling(g);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(travelling.ToJsonString())))[..32];
            if (File.Exists(EditFile) && JsonNode.Parse(File.ReadAllText(EditFile)) is JsonObject seen
                && seen["hash"]?.GetValue<string>() == hash && seen["at"] is JsonValue at && at.TryGetValue<DateTime>(out var when))
                return when;
            File.WriteAllText(EditFile, new JsonObject { ["hash"] = hash, ["at"] = written.ToString("O") }.ToJsonString());
        }
        catch (Exception ex) { Log.Warn($"Config sync edit time: {ex.Message}"); }
        return written;
    }

    static JsonObject LocalSettings()
    {
        try
        {
            if (File.Exists(AppInfo.SettingsFile)) return ReadObject(File.ReadAllText(AppInfo.SettingsFile));
        }
        catch (Exception ex) { Log.Warn($"Config sync settings: {ex.Message}"); }
        return JsonSerializer.SerializeToNode(SettingsStore.Current, HybridJson.Default.AppSettings) as JsonObject ?? new JsonObject();
    }

    /// <summary>The settings both sides last agreed on (kept with tokenised paths), expanded for this PC; null before the first sync.</summary>
    static JsonObject? ReadBase()
    {
        try { return File.Exists(BaseFile) ? Expand(ReadObject(File.ReadAllText(BaseFile))) as JsonObject : null; }
        catch { return null; }
    }

    static JsonObject ReadObject(string json) => JsonNode.Parse(json) as JsonObject ?? new JsonObject();

    /// <summary>Paths in the data folder become "%HL_DATA%\…" (and back), so they point into the data folder of each PC.</summary>
    static JsonNode? Tokenise(JsonNode? node) => Map(node, s => InDataRoot(s) ? Token + s[AppInfo.DataRoot.TrimEnd('\\', '/').Length..] : s);

    /// <summary>The data folder itself or a path inside it ("C:\Data\x", not "C:\DataBackup\x", 2.1-rc.10 audit).</summary>
    internal static bool InDataRoot(string s)
    {
        var root = AppInfo.DataRoot.TrimEnd('\\', '/');
        return root.Length > 0 && s.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (s.Length == root.Length || s[root.Length] is '\\' or '/');
    }

    static JsonNode? Expand(JsonNode? node) => Map(node, s => s.StartsWith(Token, StringComparison.Ordinal) ? AppInfo.DataRoot + s[Token.Length..] : s);

    /// <summary>A copy of <paramref name="node"/> with every string passed through <paramref name="f"/>.</summary>
    static JsonNode? Map(JsonNode? node, Func<string, string> f) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Map(p.Value, f)))),
        JsonArray a => new JsonArray(a.Select(x => Map(x, f)).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s) => JsonValue.Create(f(s)),
        _ => node?.DeepClone(),
    };

    static string Fingerprint(JsonObject settings)
    {
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        all.AppendData(Encoding.UTF8.GetBytes(settings.ToJsonString()));
        foreach (var f in new[] { PlaytimeLedger.File, SessionLog.File_, Achievements.AchievementHistory.File_ })
            if (File.Exists(f)) all.AppendData(SHA256.HashData(File.ReadAllBytes(f)));
        foreach (var folder in FileFolders)
        {
            var root = Path.Combine(AppInfo.DataRoot, folder);
            if (Directory.Exists(root))
                foreach (var f in Directory.EnumerateFiles(root).Order(StringComparer.OrdinalIgnoreCase))
                    all.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(f) + ":" + new FileInfo(f).Length + "\n"));
        }
        return Convert.ToHexString(all.GetHashAndReset())[..32];
    }

    // ---------- stats: unions ----------

    /// <summary>The cloud's play time record joined to this PC's: a game in both keeps the bigger count, every name and the dates.</summary>
    static void MergeLedger(string cloudFile)
    {
        if (!File.Exists(cloudFile)) return;
        PlaytimeLedger.MergeFrom(File.ReadAllText(cloudFile));
    }

    static string? SessionKey(JsonElement r) =>
        r.TryGetProperty("id", out var id) && r.TryGetProperty("start", out var s) ? id.GetString() + "|" + s.GetString() : null;

    static string? HistoryKey(JsonElement r) =>
        r.TryGetProperty("at", out var at) && r.TryGetProperty("key", out var k)
            ? (r.TryGetProperty("gameId", out var g) ? g.GetString() : r.TryGetProperty("game", out var n) ? n.GetString() : "") + "|" + k.GetString() + "|" + at.GetString() : null;

    /// <summary>A JSON-lines file: the cloud's lines this PC lacks are added (same key = same line), all in time order.</summary>
    internal static bool MergeLines(string cloudFile, string localFile, Func<JsonElement, string?> key)
    {
        if (!File.Exists(cloudFile)) return false;
        var lines = new List<(string Line, string Key, string Sort)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool added = false;
        void Read(string file, bool cloud)
        {
            if (!File.Exists(file)) return;
            foreach (var line in File.ReadAllLines(file))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (key(doc.RootElement) is not { } k || !seen.Add(k)) continue;
                    string sort = doc.RootElement.TryGetProperty("start", out var s) ? s.GetString() ?? "" : doc.RootElement.TryGetProperty("at", out var a) ? a.GetString() ?? "" : "";
                    lines.Add((line, k, sort));
                    if (cloud) added = true;
                }
                catch { }
            }
        }
        Read(localFile, false);
        Read(cloudFile, true);
        if (!added) return false;
        AppInfo.Ensure(Path.GetDirectoryName(localFile)!);
        var tmp = localFile + ".sync.tmp";
        // By the moment itself (2.1-rc.10 audit: as text, "+02:00" and "-05:00" times of two PCs sorted out of order); a time
        // that does not read keeps the text order, after the ones that do.
        static DateTimeOffset? When(string t) => DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
        File.WriteAllLines(tmp, lines.OrderBy(l => When(l.Sort) is null).ThenBy(l => When(l.Sort)).ThenBy(l => l.Sort, StringComparer.Ordinal).Select(l => l.Line));
        File.Move(tmp, localFile, overwrite: true);
        return true;
    }

    // ---------- state ----------

    sealed record SyncState(string? CloudHash, string? SentHash, DateTime? At);

    static SyncState ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return new(null, null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new(S("cloud"), S("sent"), DateTime.TryParse(S("at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null);
        }
        catch { return new(null, null, null); }
    }

    static void WriteState(SyncState s)
    {
        var o = new JsonObject { ["cloud"] = s.CloudHash, ["sent"] = s.SentHash, ["at"] = s.At?.ToString("O") };
        File.WriteAllText(StateFile, o.ToJsonString());
    }

    static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
