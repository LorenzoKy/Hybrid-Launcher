using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridLauncher.Core;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Saves are atomic (temp file + replace) and keep one .bak.</summary>
public static class SettingsStore
{
    static readonly object Gate = new();

    /// <summary>Source-generated metadata (see <see cref="HybridJson"/>): no reflection, so trimmed release builds stay correct.</summary>
    public static JsonSerializerOptions Json => HybridJson.Default.Options;

    public static AppSettings Current { get; private set; } = new();

    public static AppSettings Load()
    {
        lock (Gate)
        {
            var file = AppInfo.SettingsFile;
            Current = TryRead(file) ?? TryRead(file + ".bak") ?? Fresh(file);
            Normalize(Current);
            return Current;
        }
    }

    /// <summary>Goes up on every save: a page kept in memory compares it to know whether what it shows may be stale.</summary>
    public static int Version { get; private set; }

    /// <summary>
    /// Saves the settings. The JSON is made here (a millisecond: it is a snapshot of this moment), the disk is written on a
    /// background thread: a save never waits for the disk (writing and replacing the file took 5-30 ms on the UI thread at every
    /// click that changed a setting, more while an antivirus scans). Saves made while one is being written are merged into one
    /// write of the newest state. <see cref="Flush"/> waits for the write (at exit).
    /// </summary>
    public static void Save()
    {
        string json;
        lock (Gate)
        {
            Version++;
            try { json = JsonSerializer.Serialize(Current, HybridJson.Default.AppSettings); }
            catch (Exception ex)
            {
                Log.Error("Saving settings failed", ex);
                return;
            }
        }
        lock (WriteGate)
        {
            _pending = json;
            _writer ??= Task.Run(WriteLoop);
        }
    }

    static readonly object WriteGate = new();
    static string? _pending;
    static Task? _writer;

    static void WriteLoop()
    {
        while (true)
        {
            string? json;
            lock (WriteGate)
            {
                json = _pending;
                _pending = null;
                if (json is null)
                {
                    _writer = null;
                    return;
                }
            }
            Write(json);
        }
    }

    static readonly object FileGate = new();

    static void Write(string json)
    {
        lock (FileGate)
        {
            try
            {
                var file = AppInfo.SettingsFile;
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(file))
                    File.Replace(tmp, file, file + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(tmp, file);
            }
            catch (Exception ex)
            {
                Log.Error("Saving settings failed", ex);
            }
        }
    }

    /// <summary>Waits (up to 5 s) until every save made so far is on disk: before the app ends or restarts.</summary>
    public static void Flush()
    {
        Task? writer;
        lock (WriteGate) writer = _writer;
        try { writer?.Wait(5000); }
        catch (Exception ex) { Log.Warn($"Settings flush: {ex.Message}"); }
        // A save that came in while the writer was ending is written here.
        string? json;
        lock (WriteGate) { json = _pending; _pending = null; }
        if (json is not null) Write(json);
    }

    /// <summary>
    /// Neither the settings nor their .bak could be read: start from defaults, but first keep both files aside
    /// (settings.unreadable-&lt;time&gt;.json and .bak.json), since the next save replaces them. Nothing is lost that way.
    /// </summary>
    static AppSettings Fresh(string file)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var (from, suffix) in new[] { (file, ".json"), (file + ".bak", ".bak.json") })
            try
            {
                if (!File.Exists(from)) continue;
                var keep = Path.Combine(Path.GetDirectoryName(from)!, $"settings.unreadable-{stamp}{suffix}");
                File.Copy(from, keep, overwrite: false);
                Log.Warn($"Settings could not be read: a copy is kept as {keep}");
            }
            catch (Exception ex) { Log.Error($"Keeping a copy of {from} failed", ex); }
        return new AppSettings();
    }

    static AppSettings? TryRead(string file)
    {
        string json;
        try
        {
            if (!File.Exists(file)) return null;
            json = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            Log.Error($"Reading {file} failed", ex);
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(json, HybridJson.Default.AppSettings);
        }
        catch (JsonException ex)
        {
            // Usually a value this version does not know (written by a newer one, e.g. an OptiScaler channel added later):
            // drop just that value and read the rest, rather than losing every setting.
            Log.Warn($"Reading {file}: {ex.Message} Trying again without the values it cannot read.");
            return TryReadLenient(json, ex, file);
        }
        catch (Exception ex)
        {
            Log.Error($"Reading {file} failed", ex);
            return null;
        }
    }

    /// <summary>Removes the value each failed read points at (its JSON path), up to 50 of them, until the rest reads.</summary>
    public static AppSettings? TryReadLenient(string json, JsonException first, string file)
    {
        System.Text.Json.Nodes.JsonNode? root;
        try { root = System.Text.Json.Nodes.JsonNode.Parse(json); }
        catch (Exception ex) { Log.Error($"Reading {file} failed: not valid JSON", ex); return null; }
        var error = first;
        for (int i = 0; i < 50 && root is not null; i++)
        {
            if (error.Path is not { Length: > 1 } path || !RemoveAt(root, path))
            {
                Log.Error($"Reading {file} failed", error);
                return null;
            }
            Log.Warn($"Settings: left out {path}, which this version cannot read");
            try { return root.Deserialize(HybridJson.Default.AppSettings); }
            catch (JsonException next) { error = next; }
            catch (Exception ex) { Log.Error($"Reading {file} failed", ex); return null; }
        }
        Log.Error($"Reading {file} failed: too many values this version cannot read", error);
        return null;
    }

    /// <summary>
    /// Removes the node a System.Text.Json error path points at ($.Games.steam:1.Install.Channel). The path is not parsed (keys are not
    /// escaped in it): the tree is walked and each node's own path compared. False when no node has that path.
    /// </summary>
    public static bool RemoveAt(System.Text.Json.Nodes.JsonNode root, string path)
    {
        var target = Find(root, path);
        switch (target?.Parent)
        {
            case System.Text.Json.Nodes.JsonObject o:
                var key = o.FirstOrDefault(p => ReferenceEquals(p.Value, target)).Key;
                return key is not null && o.Remove(key);
            case System.Text.Json.Nodes.JsonArray a:
                return a.Remove(target);
            default:
                return false;
        }
    }

    static System.Text.Json.Nodes.JsonNode? Find(System.Text.Json.Nodes.JsonNode? node, string path)
    {
        if (node is null) return null;
        var own = node.GetPath();
        if (string.Equals(own, path, StringComparison.OrdinalIgnoreCase)) return node;
        // Only go down where the path continues (keeps it quick on big settings files).
        if (!path.StartsWith(own, StringComparison.OrdinalIgnoreCase)) return null;
        IEnumerable<System.Text.Json.Nodes.JsonNode?> children = node switch
        {
            System.Text.Json.Nodes.JsonObject o => o.Select(p => p.Value),
            System.Text.Json.Nodes.JsonArray a => a,
            _ => [],
        };
        foreach (var c in children)
            if (Find(c, path) is { } hit) return hit;
        return null;
    }

    // Tabs that used to stand alone and now are sections of another tab (see GameTabs.Resolve in the app).
    static readonly string[] RetiredTabs = ["settings", "optiwiki", "reshade", "renodx", "luma"];

    /// <summary>
    /// The game page's tabs were merged (OptiScaler: install, settings, wiki; ReShade &amp; Mods: ReShade, RenoDX, Luma). A saved order
    /// that names a retired tab is dropped (the new default order applies); a merged tab stays hidden only when every part of it was
    /// hidden; the last open tab moves to the one that holds it. Settings without retired ids are left alone, so this runs once.
    /// </summary>
    static void MigrateTabs(AppSettings s)
    {
        try
        {
            bool Retired(string id) => RetiredTabs.Contains(id, StringComparer.OrdinalIgnoreCase);
            if (s.TabOrder.Any(Retired)) s.TabOrder.Clear();
            s.HiddenTabs = MigrateHiddenTabs(s.HiddenTabs);
            foreach (var g in s.Games.Values) g.HiddenTabs = MigrateHiddenTabs(g.HiddenTabs);
            s.GameTab = s.GameTab.ToLowerInvariant() switch
            {
                "settings" or "optiwiki" => "opti",
                "reshade" or "renodx" or "luma" => "mods",
                _ => s.GameTab,
            };
        }
        catch (Exception ex) { Log.Warn($"Migrating the tabs failed: {ex.Message}"); }
    }

    static HashSet<string> MigrateHiddenTabs(HashSet<string> hidden)
    {
        if (!hidden.Any(id => RetiredTabs.Contains(id, StringComparer.OrdinalIgnoreCase))) return hidden;
        var next = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        bool optiAll = hidden.Contains("opti") && hidden.Contains("settings") && hidden.Contains("optiwiki");
        bool modsAll = hidden.Contains("reshade") && hidden.Contains("renodx") && hidden.Contains("luma");
        next.ExceptWith(RetiredTabs);
        if (!optiAll) next.Remove("opti");
        if (modsAll) next.Add("mods");
        return next;
    }


    /// <summary>A set read from JSON, case-insensitive again (deserialization drops the comparer), without empty entries.</summary>
    static HashSet<string> CI(HashSet<string>? set) =>
        new((set ?? []).Where(static x => !string.IsNullOrEmpty(x)), StringComparer.OrdinalIgnoreCase);

    /// <summary>A map read from JSON, case-insensitive again; keys that now collide keep the first value (never throws).</summary>
    static Dictionary<string, T> CI<T>(Dictionary<string, T>? map)
    {
        var next = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in map ?? []) if (k is not null) next.TryAdd(k, v);
        return next;
    }

    /// <summary>The library's and the side list's filters, read the same way (2.1-rc.10 audit: the list's were left case-sensitive).</summary>
    static void NormalizeFilter(LibraryFilter f)
    {
        f.Launchers = CI(f.Launchers);
        f.Tech = CI(f.Tech);
    }

    // Deserialization drops the case-insensitive comparers; put them back.
    static void Normalize(AppSettings s)
    {
        s.HiddenGames = new HashSet<string>(s.HiddenGames ?? [], StringComparer.OrdinalIgnoreCase);
        s.Profiles = (s.Profiles ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id)).ToList();
        if (s.ActiveProfile is not null && !s.Profiles.Any(p => p.Id == s.ActiveProfile)) s.ActiveProfile = s.Profiles.FirstOrDefault()?.Id;
        s.CollapsedCards = new HashSet<string>(s.CollapsedCards ?? [], StringComparer.OrdinalIgnoreCase);
        s.InboxOff = new HashSet<string>(s.InboxOff ?? [], StringComparer.OrdinalIgnoreCase);
        s.PopupOff = new HashSet<string>(s.PopupOff ?? [], StringComparer.OrdinalIgnoreCase);
        // 2.1: five themes (Windows, Total Dark, Dark, Gray, Hybrid); every earlier one carries on as Dark (the plain dark
        // ones), Windows (2.0's own Windows accent, or none saved) or Hybrid.
        s.Palette = s.Palette switch
        {
            "Windows" or "TotalDark" or "Dark" or "Gray" or "Hybrid" => s.Palette,
            "Graphite" or "Cozy" => "Dark",
            null or "" => "Windows",
            _ => "Hybrid",
        };
        // 2.1: "Replace them, no backup" became the master switch of backups turned off.
        if (s.ModBackups == "replace") { s.BackupGameFiles = false; s.ModBackups = "backup"; }
        if (s.ModBackups is not ("backup" or "ask")) s.ModBackups = "backup";
        s.IniView = string.IsNullOrWhiteSpace(s.IniView) ? "essentials" : s.IniView;
        s.GameTab = string.IsNullOrWhiteSpace(s.GameTab) ? "overview" : s.GameTab;
        s.TabOrder = (s.TabOrder ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.HiddenTabs = new HashSet<string>(s.HiddenTabs ?? [], StringComparer.OrdinalIgnoreCase);
        s.HiddenBannerLinks = new HashSet<string>(s.HiddenBannerLinks ?? [], StringComparer.OrdinalIgnoreCase);
        s.HiddenPageParts = new HashSet<string>(s.HiddenPageParts ?? [], StringComparer.OrdinalIgnoreCase);
        if (!s.ModsPathDefaultApplied)
        {
            s.HiddenPageParts.Add(PageParts.ModsPath);
            s.ModsPathDefaultApplied = true;
        }
        s.HiddenIcons = new HashSet<string>(s.HiddenIcons ?? [], StringComparer.OrdinalIgnoreCase);
        // Before 1.2 "No icons" was a choice of the tab icon colour: it is the tabs' switch now.
        if (s.TabIcons == "none") { s.HiddenIcons.Add(IconSpots.Tabs); s.HiddenIcons.Add(IconSpots.SubTabs); s.TabIcons = "mono"; }
        s.QuickPanelExtras = (s.QuickPanelExtras ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.QuickPanelHidden = new HashSet<string>(s.QuickPanelHidden ?? [], StringComparer.OrdinalIgnoreCase);
        s.InstallRoots = new Dictionary<string, string>(s.InstallRoots ?? [], StringComparer.OrdinalIgnoreCase);
        // Before 1.2 one folder served every store; it stays the choice for each until changed.
        if (s.InstallRoot is { Length: > 0 } old)
        {
            foreach (var store in new[] { "Steam", "Epic", "Gog" }) s.InstallRoots.TryAdd(store, old);
            s.InstallRoot = null;
        }
        s.AsiNames = new Dictionary<string, string>(s.AsiNames ?? [], StringComparer.OrdinalIgnoreCase);
        s.GameNames = new Dictionary<string, string>(s.GameNames ?? [], StringComparer.OrdinalIgnoreCase);
        s.VersionNames = new Dictionary<string, string>(s.VersionNames ?? [], StringComparer.OrdinalIgnoreCase);
        s.CollapsedGroups = new HashSet<string>(s.CollapsedGroups ?? [], StringComparer.OrdinalIgnoreCase);
        s.GroupOrder = (s.GroupOrder ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.Library ??= new LibraryFilter();
        NormalizeFilter(s.Library);
        if (s.ListFilter is { } listFilter) NormalizeFilter(listFilter);
        s.HiddenFromStats = CI(s.HiddenFromStats);
        s.HiddenCoverTags = CI(s.HiddenCoverTags);
        s.DashboardHidden = CI(s.DashboardHidden);
        s.OptiSeen = CI(s.OptiSeen);
        s.UltraPlusSeen = CI(s.UltraPlusSeen);
        s.UltraPlusPresets = CI(s.UltraPlusPresets);
        foreach (var key in s.UltraPlusPresets.Keys.ToList())
        {
            var presets = CI(s.UltraPlusPresets[key]);
            foreach (var name in presets.Keys.ToList()) presets[name] = CI(presets[name]);
            s.UltraPlusPresets[key] = presets;
        }
        s.SettingsTab = string.IsNullOrWhiteSpace(s.SettingsTab) ? "appearance" : s.SettingsTab;
        s.CustomAccent ??= "#9A5CFF";
        s.CustomTint ??= "#8A4DFF";
        s.CustomBackground ??= "#16121F";
        s.Popup ??= new PopupSettings();
        s.Popup.LookColours = CI(s.Popup.LookColours);
        s.Controller ??= new ControllerSettings();
        s.Controller.Buttons = new Dictionary<string, string>(s.Controller.Buttons ?? [], StringComparer.OrdinalIgnoreCase);
        s.Popup.TierSounds = new Dictionary<string, string>(s.Popup.TierSounds ?? [], StringComparer.OrdinalIgnoreCase);
        s.Popup.SoundPresets = new Dictionary<string, Dictionary<string, string>>(
            (s.Popup.SoundPresets ?? []).Where(p => p.Value is not null).ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        // Before 1.3.1 the looks came with their own sounds (no longer shipped): the system sound takes their place.
        if (s.Popup.Sound is not ("Windows" or "File" or "None")) s.Popup.Sound = "Windows";
        s.Cloud ??= new CloudSaveSettings();
        s.Cloud.KeepVersions = Math.Clamp(s.Cloud.KeepVersions, 1, 30);
        // Before 0.20 the providers were desktop sync apps (OneDrive, Dropbox…), each a folder: they carry on as the folder provider.
        if (s.Cloud.Provider is { Length: > 0 } && Array.IndexOf(CloudSaves.ProviderIds, s.Cloud.Provider) < 0) s.Cloud.Provider = s.Cloud.Folder is { Length: > 0 } ? "folder" : "";
        // Before 1.0 one provider at a time: it joins the list.
        var providers = (s.Cloud.Providers ?? []).Append(s.Cloud.Provider).ToHashSet();
        s.Cloud.Providers = CloudSaves.ProviderIds.Where(providers.Contains).ToList();
        s.Cloud.Provider = "";
        s.Popup.Seconds = Math.Clamp(s.Popup.Seconds, 2, 15);
        s.LibraryView = s.LibraryView == "grid" ? "grid" : "list";
        s.PinnedGames = (s.PinnedGames ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        GameCollections.Sanitize(s);
        s.Games = new Dictionary<string, GameSettings>(s.Games ?? [], StringComparer.OrdinalIgnoreCase);
        s.GameLibraries ??= [];
        s.CustomGames ??= [];
        s.DirectInstalls = (s.DirectInstalls ?? []).Where(d => !string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(d.InstallDir)).ToList();
        s.IniProfiles = (s.IniProfiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        foreach (var p in s.IniProfiles) p.Values = new Dictionary<string, string>(p.Values ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var g in s.Games.Values)
        {
            g.Setup ??= new OptiSetupOptions();
            g.HiddenTabs = new HashSet<string>(g.HiddenTabs ?? [], StringComparer.OrdinalIgnoreCase);
            g.HiddenPageParts = new HashSet<string>(g.HiddenPageParts ?? [], StringComparer.OrdinalIgnoreCase);
            g.HiddenModCards = new HashSet<string>(g.HiddenModCards ?? [], StringComparer.OrdinalIgnoreCase);
            g.DismissedModProblems = new HashSet<string>(g.DismissedModProblems ?? [], StringComparer.OrdinalIgnoreCase);
            if (g.Dxvk is { } dx)
            {
                dx.Files = new Dictionary<string, string>(dx.Files ?? [], StringComparer.OrdinalIgnoreCase);
                dx.Backups = new Dictionary<string, string>(dx.Backups ?? [], StringComparer.OrdinalIgnoreCase);
            }
            g.QuickLinks = (g.QuickLinks ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Path)).ToList();
            g.Notes ??= "";
            g.QuickPanelExtras = (g.QuickPanelExtras ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            g.QuickPanelHidden = new HashSet<string>(g.QuickPanelHidden ?? [], StringComparer.OrdinalIgnoreCase);
            // Settings from before the flag: a game that had its own panel (or anything in it) keeps it.
            g.QuickPanelOwnMade |= g.QuickPanelOwn || g.QuickPanelExtras.Count > 0 || g.QuickPanelHidden.Count > 0;
            g.ReShade ??= new ReShadeState();
            g.RenoDxFiles = new Dictionary<string, string>(g.RenoDxFiles ?? [], StringComparer.OrdinalIgnoreCase);
            g.IniOverrides = new Dictionary<string, string>(g.IniOverrides ?? [], StringComparer.OrdinalIgnoreCase);
            if (g.Install is { } m)
            {
                m.Files = new Dictionary<string, string>(m.Files ?? [], StringComparer.OrdinalIgnoreCase);
                m.Backups = new Dictionary<string, string>(m.Backups ?? [], StringComparer.OrdinalIgnoreCase);
                m.AsiPlugins = new Dictionary<string, string>(m.AsiPlugins ?? [], StringComparer.OrdinalIgnoreCase);
                m.CustomDlls = new Dictionary<string, Dictionary<string, string>>(
                    (m.CustomDlls ?? []).ToDictionary(e => e.Key, e => new Dictionary<string, string>(e.Value ?? [], StringComparer.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase);
                m.CreatedDirs ??= [];
                m.UserOptiBackups ??= [];
                if (m.DlssNrAmd is { } amd) amd.OptiIniBefore = CI(amd.OptiIniBefore);
            }
            // Per game since 0.11: a game starts with the plugins its install already holds (the old shared switches).
            g.CustomDlls = CI(g.CustomDlls);
            if (g.Luma is { } luma) luma.Backups = CI(luma.Backups);
            if (g.UltraPlus is { } ultra)
            {
                ultra.Backups = CI(ultra.Backups);
                ultra.Addons = CI(ultra.Addons);
                ultra.Files ??= [];
            }
            g.AsiPlugins = new HashSet<string>(g.AsiPlugins ?? g.Install?.AsiPlugins.Keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }
        MigrateTabs(s);
        // 1.4.0: the Library opens on the games played last, every launcher in one block; once, the user can change it back.
        if (s.Library.DefaultsVersion < 1)
        {
            s.Library.Sort = "recent";
            s.Library.Descending = false;
            s.Library.GroupByLauncher = false;
            s.Library.DefaultsVersion = 1;
        }
        // 0.21: Material colours are off by default; turned off once in settings from before, the user can turn them back on.
        if (s.LookVersion < 2)
        {
            s.Material = false;
            s.LookVersion = 2;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(InstallManifest))]
[JsonSerializable(typeof(CloudManifest))]
[JsonSerializable(typeof(HybridLauncher.Core.Opti.DlssNrAmdStage))]
public partial class HybridJson : JsonSerializerContext;
