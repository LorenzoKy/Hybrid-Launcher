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
            Current = TryRead(AppInfo.SettingsFile) ?? TryRead(AppInfo.SettingsFile + ".bak") ?? new AppSettings();
            Normalize(Current);
            return Current;
        }
    }

    /// <summary>Goes up on every save: a page kept in memory compares it to know whether what it shows may be stale.</summary>
    public static int Version { get; private set; }

    public static void Save()
    {
        lock (Gate)
        {
            Version++;
            try
            {
                var file = AppInfo.SettingsFile;
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, HybridJson.Default.AppSettings));
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

    static AppSettings? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(file), HybridJson.Default.AppSettings);
        }
        catch (Exception ex)
        {
            Log.Error($"Reading {file} failed", ex);
            return null;
        }
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


    // Deserialization drops the case-insensitive comparers; put them back.
    static void Normalize(AppSettings s)
    {
        s.HiddenGames = new HashSet<string>(s.HiddenGames ?? [], StringComparer.OrdinalIgnoreCase);
        s.CollapsedCards = new HashSet<string>(s.CollapsedCards ?? [], StringComparer.OrdinalIgnoreCase);
        s.Palette ??= "Windows";
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
        s.Library.Launchers = new HashSet<string>(s.Library.Launchers ?? [], StringComparer.OrdinalIgnoreCase);
        s.Library.Tech = new HashSet<string>(s.Library.Tech ?? [], StringComparer.OrdinalIgnoreCase);
        s.SettingsTab = string.IsNullOrWhiteSpace(s.SettingsTab) ? "appearance" : s.SettingsTab;
        s.CustomAccent ??= "#9A5CFF";
        s.CustomTint ??= "#8A4DFF";
        s.CustomBackground ??= "#16121F";
        s.Popup ??= new PopupSettings();
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
            }
            // Per game since 0.11: a game starts with the plugins its install already holds (the old shared switches).
            g.CustomDlls = new HashSet<string>(g.CustomDlls ?? [], StringComparer.OrdinalIgnoreCase);
            g.AsiPlugins = new HashSet<string>(g.AsiPlugins ?? g.Install?.AsiPlugins.Keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }
        MigrateTabs(s);
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
public partial class HybridJson : JsonSerializerContext;
