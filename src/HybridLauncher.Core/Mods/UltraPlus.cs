using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;
using HybridLauncher.Core.Opti;

namespace HybridLauncher.Core.Mods;

/// <summary>One game Ultra+ supports, from its gamedata.json.</summary>
public sealed class UltraGame
{
    public required string Key { get; init; }
    public string FullName { get; init; } = "";
    /// <summary>The Unreal project folder under the install folder ("Stalker2").</summary>
    public string UeGamePath { get; init; } = "";
    /// <summary>The real game exe, relative to the install folder ("Stalker2/Binaries/Win64/Stalker2-Win64-Shipping.exe").</summary>
    public string ExePath { get; init; } = "";
    /// <summary>The usual name of the install folder ("S.T.A.L.K.E.R. 2 Heart of Chornobyl").</summary>
    public string InstallFolder { get; init; } = "";
    public string NexusUrl { get; init; } = "";
    public string WikiUrl { get; init; } = "";
    public bool GamePass { get; init; }
    public bool NoPak { get; init; }
    public bool NoUe4ss { get; init; }
    /// <summary>Not an Unreal game (Cyberpunk 2077, The Witcher 3): the archive goes into the install folder as it is.</summary>
    public bool InstallRootOnly { get; init; }
    public List<string> Prefixes { get; init; } = [];
    /// <summary>Ultra+'s note for the game (Markdown links allowed): a mod enabler it needs, a known limit.</summary>
    public string Note { get; init; } = "";
    public List<UltraAddon> Addons { get; init; } = [];
    public List<string> SearchTerms { get; init; } = [];
    public string Name => FullName.Length > 0 ? FullName : Key;
}

public sealed record UltraAddon(string Name, string FileName, string Description);

/// <summary>An archive on Ultra+'s server: a mod build or an add-on build.</summary>
public sealed record UltraFile(string FileName, string Url, long Size, string Sha256, DateTime Published)
{
    /// <summary>"6.5.1", "13.3.0_beta4": what follows the last " v" of the name.</summary>
    public string Version
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(FileName);
            int v = name.LastIndexOf(" v", StringComparison.OrdinalIgnoreCase);
            return v < 0 ? "" : Regex.Replace(name[(v + 2)..], "[-_]gamepass$", "", RegexOptions.IgnoreCase);
        }
    }

    public bool Beta => Regex.IsMatch(Version, @"(alpha|beta|test|rc|pre|dev)", RegexOptions.IgnoreCase);
    public bool IsGamePass => FileName.Contains("gamepass", StringComparison.OrdinalIgnoreCase);
}

public sealed record UltraMatch(UltraGame Game, string Reason, int Score);

/// <summary>Where Ultra+'s files go in one game: the install folder, the exe's folder (UE4SS) and the Unreal project (paks).</summary>
public sealed record UltraLayout(string Root, string? ExeDir, string? ProjectDir)
{
    public string? Ue4ssDir => ExeDir is null ? null : Path.Combine(ExeDir, "ue4ss");
    public string? ConfigDir => ExeDir is null ? null : Path.Combine(ExeDir, "ue4ss", "Mods", "UltraPlusExtensions", "scripts", "config");
    public string? PakModsDir => ProjectDir is null ? null : Path.Combine(ProjectDir, "Content", "Paks", "~mods");
}

/// <summary>What is in a game folder now, whoever put it there.</summary>
public sealed record UltraFolderState(bool Installed, string? Version, bool Ue4ss, List<string> OtherUe4ssMods);

/// <summary>One setting of UltraPlusConfig.ini, described by the parser_friendly_settings.ini the mod ships.</summary>
public sealed class UltraSetting
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Numeric { get; set; }
    public bool IsFloat { get; set; }
    public List<string> Values { get; set; } = [];
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    public double Step { get; set; } = 0.1;
    public string Default { get; set; } = "";
    /// <summary>The key bound to it in game, from the comment above it ("CTRL + F10").</summary>
    public string? Hotkey { get; set; }
}

/// <summary>
/// Ultra+ (theultraplace.com, by Lazorr, SammiLucia and friends): free graphics mods for Unreal Engine games that hook into the
/// running game through UE4SS and set the engine's rendering at run time, with presets and a settings file read live (F12).
/// The list of games, the builds of each mod and of its add-ons come from the same public files the Ultra+ Manager reads
/// (gamedata.json, mods_manifest.json and addons_manifest.json on Ultra+'s server, each archive with its SHA-256). The install
/// places the archive the way the Ultra+ Manager does: UE4SS and UltraPlusExtensions beside the game exe, paks in
/// Content\Paks\~mods, and keeps the user's UltraPlusConfig.ini and keybinds.ini values across updates.
/// </summary>
public static partial class UltraPlus
{
    public const string Server = "https://d25cpafae92g0h.cloudfront.net/";
    public const string Site = "https://theultraplace.com/";
    public const string Discord = "https://discord.gg/UltraPlace";
    public const string ManagerSource = "https://gitlab.com/ultra-plus/ultraplus-manager";
    public const string ConfigFile = "UltraPlusConfig.ini";
    public const string CustomFile = "CustomSettings.ini";

    /// <summary>The files a user edits: an update keeps their values (the shipped file's comments and new settings come in).</summary>
    static readonly string[] UserOwned = [ConfigFile, "keybinds.ini"];

    /// <summary>Proxy dlls UE4SS loads through, placed beside the exe.</summary>
    static readonly HashSet<string> RootInjection = new(StringComparer.OrdinalIgnoreCase) { "dwmapi.dll", "version.dll", "winmm.dll", "xinput1_3.dll", "xinput1_4.dll" };

    /// <summary>UE4SS mods that come with the Ultra+ archive (not the user's own).</summary>
    static readonly HashSet<string> ShippedUe4ssMods = new(StringComparer.OrdinalIgnoreCase)
        { "UltraPlusExtensions", "UObjectCacheMod", "ConsoleEnablerMod", "ConsoleCommandsMod", "BPML_GenericFunctions", "BPModLoaderMod", "Keybinds", "shared", "LineTraceMod", "CheatManagerEnablerMod", "SplitScreenMod", "jsbLuaProfilerMod" };

    public static string Dir => Directory.CreateDirectory(Path.Combine(AppInfo.DataRoot, "UltraPlus")).FullName;
    static string DownloadsDir => Directory.CreateDirectory(Path.Combine(Dir, "downloads")).FullName;
    /// <summary>The user's settings of a game, kept when Ultra+ is uninstalled so a later install gets them back.</summary>
    static string SavedSettingsDir(string key) => Path.Combine(Dir, "settings", key);

    // ---------- the lists ----------

    static List<UltraGame>? _games;
    static List<UltraFile>? _mods, _addons;
    static Dictionary<string, string>? _names, _descriptions;
    static Dictionary<string, List<string>>? _categories;

    /// <summary>When the lists were last read from Ultra+'s server (null: never on this PC).</summary>
    public static DateTime? CheckedAt
    {
        get
        {
            var f = Path.Combine(AppInfo.CacheDir, "ultraplus-mods.json");
            return File.Exists(f) ? File.GetLastWriteTime(f) : null;
        }
    }

    /// <summary>Raised after the lists were read again (the Ultra+ page and the game tab redraw their versions).</summary>
    public static event Action? CatalogChanged;

    /// <summary>Ultra+'s list of games when it was read already (no wait), else null.</summary>
    public static List<UltraGame>? Cached => _games;

    /// <summary>The file is one Ultra+ put in the game (the UE4SS proxy dll beside the exe, for one).</summary>
    public static bool Owns(UltraPlusState state, string path)
    {
        if (state.Root.Length == 0) return false;
        var rel = Path.GetRelativePath(state.Root, path);
        return state.Files.Concat(state.Addons.Values.SelectMany(a => a.Files)).Contains(rel, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every game Ultra+ supports (cached for 3 hours, as long as the background check waits). Nothing about the games is built
    /// in: a game Ultra+ adds to its list shows up here, and gets its tab, without a Hybrid Launcher update.
    /// </summary>
    public static async Task<List<UltraGame>> GetGamesAsync(bool force = false, CancellationToken ct = default)
    {
        if (_games is not null && !force) return _games;
        var json = await Http.GetCachedAsync(Server + "gamedata.json", "ultraplus-gamedata.json", TimeSpan.FromHours(3), force, ct: ct);
        return _games = ParseOrLastGood(json, "ultraplus-gamedata.json", ParseGames);
    }

    /// <summary>Every mod build (cached for an hour: new builds show up quickly).</summary>
    public static async Task<List<UltraFile>> GetModFilesAsync(bool force = false, CancellationToken ct = default)
    {
        if (_mods is not null && !force) return _mods;
        var json = await Http.GetCachedAsync(Server + "mods_manifest.json", "ultraplus-mods.json", TimeSpan.FromHours(1), force, ct: ct);
        return _mods = ParseOrLastGood(json, "ultraplus-mods.json", ParseFiles);
    }

    public static async Task<List<UltraFile>> GetAddonFilesAsync(bool force = false, CancellationToken ct = default)
    {
        if (_addons is not null && !force) return _addons;
        var json = await Http.GetCachedAsync(Server + "addons_manifest.json", "ultraplus-addons.json", TimeSpan.FromHours(1), force, ct: ct);
        return _addons = ParseOrLastGood(json, "ultraplus-addons.json", ParseFiles);
    }

    /// <summary>
    /// A list read from Ultra+'s server, or the last one that read well: a half-written file or a change of its format on their
    /// side keeps the games and builds known so far (and is logged) instead of emptying the tab and the page.
    /// </summary>
    static List<T> ParseOrLastGood<T>(string? json, string cacheName, Func<string, List<T>> parse)
    {
        var good = Path.Combine(AppInfo.CacheDir, Path.GetFileNameWithoutExtension(cacheName) + ".good.json");
        if (json is not null)
            try
            {
                if (parse(json) is { Count: > 0 } list)
                {
                    try { if (!File.Exists(good) || new FileInfo(good).Length != json.Length) File.WriteAllText(good, json); } catch { }
                    return list;
                }
                Log.Warn($"Ultra+: {cacheName} lists nothing, the last good copy is used");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                Log.Warn($"Ultra+: {cacheName} could not be read ({ex.Message}), the last good copy is used");
            }
        try { return File.Exists(good) ? parse(File.ReadAllText(good)) : []; }
        catch { return []; }
    }

    /// <summary>Reads every list again from the server (Check now), then tells the pages.</summary>
    public static async Task RefreshAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(GetGamesAsync(true, ct), GetModFilesAsync(true, ct), GetAddonFilesAsync(true, ct));
        CatalogChanged?.Invoke();
    }

    public static List<UltraGame> ParseGames(string json)
    {
        var list = new List<UltraGame>();
        using var doc = JsonDocument.Parse(json);
        var terms = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("GameSearchTerms", out var st) && st.ValueKind == JsonValueKind.Object)
            foreach (var p in st.EnumerateObject())
                terms[p.Name] = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToList() : [];
        if (!doc.RootElement.TryGetProperty("SupportedGames", out var games)) return list;
        foreach (var p in games.EnumerateObject())
        {
            // One entry Ultra+ wrote differently (a new kind of value) is skipped, not the whole list.
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            var g = p.Value;
            string S(string name) => g.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string name) => g.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            var addons = new List<UltraAddon>();
            if (g.TryGetProperty("addons", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                {
                    string A(string name) => x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    if (A("FileName") is { Length: > 0 } file) addons.Add(new UltraAddon(A("Name") is { Length: > 0 } n ? n : file, file, A("Description")));
                }
            list.Add(new UltraGame
            {
                Key = p.Name,
                FullName = S("full_name"),
                UeGamePath = S("ue_game_path"),
                ExePath = S("exe_path").Replace('/', '\\'),
                InstallFolder = S("install_folder"),
                NexusUrl = S("nexus_url"),
                WikiUrl = S("wiki_url"),
                GamePass = B("gamepass"),
                NoPak = B("no_pak"),
                NoUe4ss = B("no_ue4ss"),
                InstallRootOnly = B("install_root_only"),
                Prefixes = g.TryGetProperty("mod_filename_prefixes", out var pre) && pre.ValueKind == JsonValueKind.Array
                    ? pre.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToList() : [],
                Note = S("note"),
                Addons = addons,
                SearchTerms = terms.GetValueOrDefault(p.Name) ?? [],
            });
        }
        return list.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<UltraFile> ParseFiles(string json)
    {
        var list = new List<UltraFile>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("files", out var files)) return list;
        foreach (var f in files.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            string S(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = S("filename");
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || S("url") is not { Length: > 0 } url) continue;
            DateTime.TryParse(S("updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published);
            list.Add(new UltraFile(name, url, f.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var n) ? n : 0, S("sha256"), published.ToLocalTime()));
        }
        return list;
    }

    // ---------- names and descriptions of the settings ----------

    /// <summary>The settings' friendly names, descriptions and groups, as the Ultra+ Manager shows them (cached for a day).</summary>
    public static async Task LoadSettingTextsAsync(CancellationToken ct = default)
    {
        if (_names is not null) return;
        var names = Http.GetCachedAsync(Server + "name_overrides.json", "ultraplus-names.json", TimeSpan.FromDays(1), ct: ct);
        var descriptions = Http.GetCachedAsync(Server + "description_overrides.json", "ultraplus-descriptions.json", TimeSpan.FromDays(1), ct: ct);
        var categories = Http.GetCachedAsync(Server + "ui_categories.json", "ultraplus-categories.json", TimeSpan.FromDays(1), ct: ct);
        // Read whole first, then published together: a reader never sees names without their descriptions. Nothing read
        // (offline) keeps nothing, so the next call asks again instead of staying without names for the session.
        var nameText = await names;
        var map = Map(nameText);
        var descriptionMap = Map(await descriptions);
        var categoryMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (await categories is { } json)
                foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        categoryMap[p.Name] = p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToList();
        }
        catch (JsonException) { }
        if (nameText is null) return;
        _descriptions = descriptionMap;
        _categories = categoryMap;
        _names = map;

        static Dictionary<string, string> Map(string? json)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (json is null) return map;
            try
            {
                foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString() ?? "";
            }
            catch (JsonException) { }
            return map;
        }
    }

    // ---------- which Ultra+ game a library game is ----------

    /// <summary>
    /// The Ultra+ games that fit a library game, best first: the exe's path ends with the one Ultra+ lists (sure), the exe's
    /// name is the same, the install folder has Ultra+'s name for it, or the title matches the name or a search term.
    /// </summary>
    public static List<UltraMatch> Match(IEnumerable<UltraGame> games, string title, string installDir, string? exe)
    {
        var hits = new List<UltraMatch>();
        var exeNorm = exe is null ? null : NormalizeWin(exe);
        var exeName = exe is null ? null : Path.GetFileName(exe);
        var folder = Path.GetFileName(installDir.TrimEnd('\\', '/'));
        var key = RenoDx.Key(title);
        foreach (var g in games)
        {
            if (g.ExePath.Length > 0 && exeNorm is not null && exeNorm.EndsWith("\\" + NormalizeWin(g.ExePath), StringComparison.OrdinalIgnoreCase))
                hits.Add(new UltraMatch(g, "Matched by the game's exe", 100));
            else if (g.ExePath.Length > 0 && exeName is not null && Path.GetFileName(g.ExePath).Equals(exeName, StringComparison.OrdinalIgnoreCase)
                     && !exeName.StartsWith("UE4-", StringComparison.OrdinalIgnoreCase) && !exeName.StartsWith("UnrealGame", StringComparison.OrdinalIgnoreCase))
                hits.Add(new UltraMatch(g, "Matched by the game's exe name", 90));
            else if (g.InstallFolder.Length > 0 && folder.Equals(g.InstallFolder, StringComparison.OrdinalIgnoreCase))
                hits.Add(new UltraMatch(g, "Matched by the install folder", 80));
            else if (key.Length > 1 && (RenoDx.Key(g.Name) == key || RenoDx.Key(g.Key) == key || g.SearchTerms.Any(t => RenoDx.Key(t) == key)))
                hits.Add(new UltraMatch(g, "Matched by name", 70));
        }
        return hits.OrderByDescending(h => h.Score).ToList();
    }

    /// <summary>
    /// Ultra+'s generic builds for Unreal games it has no mod of their own for (UE5_Generic, UE4_Generic): the same framework
    /// with safe settings only, placed from the game's exe.
    /// </summary>
    public static readonly UltraGame[] Generic =
    [
        new() { Key = "UE5_Generic", FullName = "Any Unreal Engine 5 game (generic build)", Prefixes = ["UE5_Generic"], WikiUrl = Site },
        new() { Key = "UE4_Generic", FullName = "Any Unreal Engine 4 game (generic build)", Prefixes = ["UE4_Generic"], WikiUrl = Site },
    ];

    public static bool IsGeneric(UltraGame game) => Generic.Any(g => g.Key == game.Key);

    /// <summary>Game Pass keeps the Unreal binaries in WinGDK: compared as Win64.</summary>
    static string NormalizeWin(string path) => Regex.Replace(path.Replace('/', '\\'), @"\\Binaries\\Win[A-Za-z0-9]+\\", @"\Binaries\Win64\", RegexOptions.IgnoreCase);

    /// <summary>The folders Ultra+'s files go into, worked out from the game exe (the Unreal "-Shipping" one).</summary>
    public static UltraLayout Layout(UltraGame game, string installDir, string? exe)
    {
        string? exeDir = exe is null ? null : Path.GetDirectoryName(exe);
        // The install folder: the exe's path less the part Ultra+ lists; otherwise what the store says.
        string root = installDir;
        bool fromExe = false;
        if (exe is not null && game.ExePath.Length > 0)
        {
            var tail = NormalizeWin(game.ExePath);
            var norm = NormalizeWin(exe);
            if (norm.EndsWith("\\" + tail, StringComparison.OrdinalIgnoreCase))
            {
                root = exe[..(exe.Length - tail.Length - 1)];
                fromExe = true;
            }
        }
        if (game.InstallRootOnly) return new UltraLayout(root, exeDir, null);
        string? project = null;
        if (exeDir is not null && Path.GetFileName(Path.GetDirectoryName(exeDir) ?? "").Equals("Binaries", StringComparison.OrdinalIgnoreCase))
            project = Path.GetDirectoryName(Path.GetDirectoryName(exeDir));
        if (project is null && game.UeGamePath.Length > 0 && Directory.Exists(Path.Combine(root, game.UeGamePath))) project = Path.Combine(root, game.UeGamePath);
        // A game added by hand (its folder is the exe's) or a generic build: the install folder is the one above the Unreal project.
        if (!fromExe && project is not null && !project.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(project) ?? root;
        // An exe outside an Unreal layout (a bootstrap exe picked by hand): the real one is where Ultra+ says.
        if (project is not null && (exeDir is null || !exeDir.StartsWith(project, StringComparison.OrdinalIgnoreCase)) && game.ExePath.Length > 0
            && File.Exists(Path.Combine(root, game.ExePath)))
            exeDir = Path.GetDirectoryName(Path.Combine(root, game.ExePath));
        return new UltraLayout(root, exeDir, project);
    }

    // ---------- versions ----------

    /// <summary>
    /// The builds of a game's mod, newest first: the archive for the store (Game Pass has its own), stable ones only unless betas are
    /// wanted. The newest beta is always kept when it is newer than the newest stable build, so it can be offered.
    /// </summary>
    public static List<UltraFile> Versions(UltraGame game, IEnumerable<UltraFile> files, bool gamePass, bool betas)
    {
        bool Ours(UltraFile f) => game.Prefixes.Count > 0
            ? game.Prefixes.Any(p => f.FileName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            : f.FileName.StartsWith(game.Key + " Ultra", StringComparison.OrdinalIgnoreCase);
        var mine = files.Where(Ours).ToList();
        // Game Pass builds only when the game has them; the others for every store.
        bool wantGamePass = gamePass && mine.Any(f => f.IsGamePass);
        var list = mine.Where(f => f.IsGamePass == wantGamePass).OrderByDescending(f => f.Published).ToList();
        if (betas) return list;
        var stable = list.Where(f => !f.Beta).ToList();
        var newestBeta = list.FirstOrDefault(f => f.Beta);
        if (newestBeta is not null && (stable.Count == 0 || newestBeta.Published > stable[0].Published)) stable.Insert(stable.Count == 0 ? 0 : 1, newestBeta);
        return stable;
    }

    /// <summary>The newest build that is an update for what is installed (stable, unless a beta is installed or betas are wanted).</summary>
    public static UltraFile? UpdateFor(UltraPlusState state, List<UltraFile> versions, bool betas)
    {
        var newest = versions.Where(f => betas || state.Beta || !f.Beta).OrderByDescending(f => f.Published).FirstOrDefault();
        if (newest is null || newest.FileName.Equals(state.File, StringComparison.OrdinalIgnoreCase)) return null;
        return newest.Published > state.Published.AddMinutes(1) && Compare(newest.Version, state.Version) >= 0 ? newest : null;
    }

    /// <summary>Compares "6.5.1" with "6.4.13" or "13.3.0_beta4" by their numbers; a beta is older than the same release.</summary>
    public static int Compare(string a, string b)
    {
        var na = Regex.Matches(a, @"\d+").Select(m => int.TryParse(m.Value, out var n) ? n : 0).ToList();
        var nb = Regex.Matches(b, @"\d+").Select(m => int.TryParse(m.Value, out var n) ? n : 0).ToList();
        // Only the release part (before "_beta") counts first.
        int ra = Regex.Match(a, @"^[\d.]+").Value.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
        int rb = Regex.Match(b, @"^[\d.]+").Value.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
        for (int i = 0; i < Math.Max(ra, rb); i++)
        {
            int x = i < ra && i < na.Count ? na[i] : 0, y = i < rb && i < nb.Count ? nb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        bool ba = new UltraFile("x v" + a + ".zip", "", 0, "", default).Beta, bb = new UltraFile("x v" + b + ".zip", "", 0, "", default).Beta;
        if (ba != bb) return ba ? -1 : 1;
        for (int i = ra; i < Math.Max(na.Count, nb.Count); i++)
        {
            int x = i < na.Count ? na[i] : 0, y = i < nb.Count ? nb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>The add-on builds of a game, grouped by add-on: name -> builds, newest first.</summary>
    public static List<(UltraAddon Addon, List<UltraFile> Files)> Addons(UltraGame game, IEnumerable<UltraFile> files, bool gamePass)
    {
        var mine = files.Where(f => f.FileName.StartsWith(game.Key + " ", StringComparison.OrdinalIgnoreCase)).ToList();
        var result = new List<(UltraAddon, List<UltraFile>)>();
        var known = game.Addons.ToList();
        // Add-ons on the server that the game's entry does not list yet.
        foreach (var f in mine)
            if (AddonName(game.Key, f.FileName) is { Length: > 0 } n && !known.Any(a => a.FileName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                known.Add(new UltraAddon(Spaced(n), n, ""));
        foreach (var a in known)
        {
            var builds = mine.Where(f => AddonName(game.Key, f.FileName).Equals(a.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
            bool wantGamePass = gamePass && builds.Any(f => f.IsGamePass);
            builds = builds.Where(f => f.IsGamePass == wantGamePass).OrderByDescending(f => f.Published).ToList();
            if (builds.Count > 0) result.Add((a, builds));
        }
        return result;
    }

    /// <summary>"Stalker2 FSR3-FG Ultra Plus v5.4.0.zip" -> "FSR3-FG".</summary>
    public static string AddonName(string key, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!name.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase)) return "";
        name = name[(key.Length + 1)..];
        int v = name.LastIndexOf(" v", StringComparison.OrdinalIgnoreCase);
        if (v >= 0) name = name[..v];
        foreach (var tail in new[] { " Ultra Plus", " UltraPlus", " Ultra+" })
            if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) name = name[..^tail.Length];
        return name.Trim();
    }

    /// <summary>
    /// "EnhancedRayTracing_LessVRAM" -> "EnhancedRayTracing": variants of one add-on share it and go in one at a time. A short tag
    /// before the underscore ("RT_PoolSize", "FG_Fix") is not a family: those are separate add-ons.
    /// </summary>
    public static string AddonFamily(string name) => name.Split('_', 2) is [var family, _] && family.Length >= 6 ? family : name;

    /// <summary>"EnhancedRT" -> "Enhanced RT".</summary>
    public static string Spaced(string name) => Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");

    // ---------- what is in the folder ----------

    public static UltraFolderState Inspect(UltraLayout layout, UltraPlusState? state)
    {
        if (layout.ExeDir is null) return new UltraFolderState(state is not null, state?.Version, false, []);
        var ue4ss = layout.Ue4ssDir!;
        bool hasUe4ss = File.Exists(Path.Combine(ue4ss, "UE4SS.dll")) || File.Exists(Path.Combine(layout.ExeDir, "UE4SS.dll"));
        var extensions = Path.Combine(ue4ss, "Mods", "UltraPlusExtensions");
        bool installed = state is not null || Directory.Exists(extensions);
        string? version = null;
        try
        {
            var vf = Path.Combine(layout.ConfigDir!, "UltraPlus_version.txt");
            if (File.Exists(vf)) version = File.ReadAllText(vf).Trim().Trim('﻿');
        }
        catch { }
        version ??= state?.Version;
        var others = new List<string>();
        try
        {
            var mods = Path.Combine(ue4ss, "Mods");
            if (Directory.Exists(mods))
                foreach (var d in Directory.GetDirectories(mods))
                {
                    var n = Path.GetFileName(d);
                    if (ShippedUe4ssMods.Contains(n)) continue;
                    var rel = Path.GetRelativePath(layout.Root, d) + "\\";
                    if (state?.Files.Any(f => f.StartsWith(rel, StringComparison.OrdinalIgnoreCase)) == true) continue;
                    others.Add(n);
                }
        }
        catch { }
        return new UltraFolderState(installed, version, hasUe4ss, others);
    }

    // ---------- install ----------

    /// <summary>Where one archive entry goes (null: skipped), as the Ultra+ Manager places it.</summary>
    public static string? Destination(string entry, UltraLayout layout, bool rootOnly)
    {
        var segments = entry.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
        if (segments.Length == 0 || segments.Contains("..") || entry.Contains(':')) return null;
        string Combine(string root, IEnumerable<string> parts)
        {
            var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts.ToArray())));
            var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The archive entry {entry} points outside the game folder.");
            return full;
        }
        if (rootOnly) return Combine(layout.Root, segments);
        // <Project>/Binaries/Win64/... (any Win* target, not the engine's): beside the real exe.
        for (int i = 0; i + 1 < segments.Length; i++)
            if (segments[i].Equals("Binaries", StringComparison.OrdinalIgnoreCase) && segments[i + 1].StartsWith("Win", StringComparison.OrdinalIgnoreCase)
                && !(i > 0 && segments[i - 1].Equals("Engine", StringComparison.OrdinalIgnoreCase)))
                return layout.ExeDir is null ? null : Combine(layout.ExeDir, segments.Skip(i + 2));
        for (int i = 0; i + 1 < segments.Length; i++)
            if (segments[i].Equals("Content", StringComparison.OrdinalIgnoreCase) && segments[i + 1].Equals("Paks", StringComparison.OrdinalIgnoreCase))
                return layout.ProjectDir is null ? null : Combine(layout.ProjectDir, segments.Skip(i));
        if (segments[0].Equals("ue4ss", StringComparison.OrdinalIgnoreCase) || segments.Length == 1 && RootInjection.Contains(segments[0]))
            return layout.ExeDir is null ? null : Combine(layout.ExeDir, segments);
        return Combine(layout.Root, segments);
    }

    /// <summary>"I manage my own UE4SS": only Ultra+'s own UE4SS mods and its paks go in.</summary>
    static bool UltraOwned(string entry)
    {
        var n = entry.Replace('\\', '/');
        foreach (var dir in new[] { "UltraPlusExtensions", "UObjectCacheMod" })
            if (n.Contains($"/Mods/{dir}/", StringComparison.OrdinalIgnoreCase) || n.EndsWith($"/Mods/{dir}", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith($"Mods/{dir}/", StringComparison.OrdinalIgnoreCase))
                return true;
        bool package = Regex.IsMatch(n, @"\.(pak|ucas|utoc)$", RegexOptions.IgnoreCase);
        return package && (n.StartsWith("Content/", StringComparison.OrdinalIgnoreCase) || n.Contains("/Content/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The proxy dll the archive loads UE4SS through, when another mod already uses that name in the exe's folder: OptiScaler
    /// as dwmapi.dll / version.dll / winmm.dll would be overwritten, and one of the two would stop loading.
    /// </summary>
    public static string? ProxyConflict(string zip, UltraLayout layout, bool ownUe4ss)
    {
        if (ownUe4ss || layout.ExeDir is null) return null;
        var opti = OptiDetector.Detect(layout.ExeDir);
        if (opti is null) return null;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var e in archive.Entries)
        {
            if (!RootInjection.Contains(e.Name)) continue;
            if (Destination(e.FullName, layout, false) is { } dest && Path.GetDirectoryName(dest)!.Equals(layout.ExeDir, StringComparison.OrdinalIgnoreCase)
                && e.Name.Equals(opti.FileName, StringComparison.OrdinalIgnoreCase))
                return e.Name;
        }
        return null;
    }

    /// <summary>The proxy dll names an archive brings beside the exe (dwmapi.dll for most games).</summary>
    public static List<string> ProxyNames(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Where(e => RootInjection.Contains(e.Name)).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Downloads a build into Hybrid Launcher's data (kept, so a reinstall or a rollback needs no download) and checks its SHA-256.</summary>
    public static async Task<string> DownloadAsync(UltraFile file, IProgress<double>? progress, CancellationToken ct)
    {
        var zip = Path.Combine(DownloadsDir, file.FileName);
        if (File.Exists(zip) && (file.Size <= 0 || new FileInfo(zip).Length == file.Size) && await HashMatchesAsync(zip, file.Sha256, ct))
        {
            progress?.Report(1);
            return zip;
        }
        await Http.DownloadFileAsync(file.Url, zip, file.Size, progress, ct);
        if (!await HashMatchesAsync(zip, file.Sha256, ct))
        {
            try { File.Delete(zip); } catch { }
            throw new InvalidDataException($"{file.FileName} arrived damaged (its SHA-256 does not match Ultra+'s list). Try again.");
        }
        return zip;
    }

    static async Task<bool> HashMatchesAsync(string path, string sha256, CancellationToken ct)
    {
        if (sha256.Length == 0) return true;
        await using var s = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(s, ct));
        return hash.Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The builds kept on this PC (downloaded once).</summary>
    public static List<(string Path, long Size)> Saved()
    {
        try { return Directory.GetFiles(DownloadsDir, "*.zip").Select(p => (p, new FileInfo(p).Length)).OrderBy(p => p.p).ToList(); }
        catch { return []; }
    }

    /// <summary>
    /// Puts a downloaded build in the game. An update replaces the files of the build before it (the ones the new build no
    /// longer has go away) and keeps the user's UltraPlusConfig.ini / keybinds.ini values (merged into the new files, the new
    /// shipped config kept as UltraPlusConfig.default); every file of the game's own that it replaces is backed up first.
    /// </summary>
    public static async Task<UltraPlusState> InstallAsync(UltraGame game, UltraFile file, string zip, UltraLayout layout, bool ownUe4ss,
        UltraPlusState? previous, bool backup, CancellationToken ct, IProgress<double>? progress = null)
    {
        if (!game.InstallRootOnly && layout.ExeDir is null) throw new InvalidOperationException("The game's exe was not found: pick it first (the gear beside Play → Change game exe).");
        if (!game.InstallRootOnly && layout.ProjectDir is null) throw new InvalidOperationException("The game's Unreal project folder was not found next to its exe.");
        var root = layout.Root;
        var state = new UltraPlusState
        {
            GameKey = game.Key, File = file.FileName, Version = file.Version, Beta = file.Beta, Published = file.Published,
            InstalledAt = DateTime.Now, Root = root, OwnUe4ss = ownUe4ss,
            Addons = previous?.Addons ?? new(StringComparer.OrdinalIgnoreCase),
            Backups = previous?.Backups ?? new(StringComparer.OrdinalIgnoreCase),
        };
        var before = previous?.Files.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var backupRoot = Path.Combine(AppInfo.BackupsDir, "ultraplus", game.Key + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var savedSettings = SavedSettingsDir(game.Key);
        // Ultra+ already in the folder, put there by the Ultra+ Manager or by hand: its files are Ultra+'s, not the game's. They are
        // taken over (an uninstall removes them) rather than backed up as the game's own (an uninstall would put them back).
        bool adopt = previous is null && layout.Ue4ssDir is { } ue && Directory.Exists(Path.Combine(ue, "Mods", "UltraPlusExtensions"));
        bool UltraPlace(string dest) =>
            layout.Ue4ssDir is { } u && dest.StartsWith(u.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || layout.PakModsDir is { } p && dest.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || RootInjection.Contains(Path.GetFileName(dest)) && Path.GetDirectoryName(dest)!.Equals(layout.ExeDir, StringComparison.OrdinalIgnoreCase);
        await Task.Run(() =>
        {
            var written = new List<string>();
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                int done = 0, total = Math.Max(1, archive.Entries.Count);
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report((double)++done / total);
                    if (entry.FullName.EndsWith('/') || entry.Name.Length == 0) continue;
                    if (ownUe4ss && !UltraOwned(entry.FullName)) continue;
                    if (Destination(entry.FullName, layout, game.InstallRootOnly) is not { } dest) continue;
                    var rel = Path.GetRelativePath(root, dest);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    bool userOwned = UserOwned.Any(u => entry.Name.Equals(u, StringComparison.OrdinalIgnoreCase));
                    if (userOwned)
                    {
                        var shipped = dest + ".incoming";
                        entry.ExtractToFile(shipped, overwrite: true);
                        var mine = File.Exists(dest) ? dest : File.Exists(Path.Combine(savedSettings, entry.Name)) ? Path.Combine(savedSettings, entry.Name) : null;
                        if (entry.Name.Equals(ConfigFile, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(shipped, Path.ChangeExtension(dest, ".default"), overwrite: true);
                            AddOnce(state.Files, Path.GetRelativePath(root, Path.ChangeExtension(dest, ".default")));
                        }
                        if (mine is not null)
                        {
                            var merged = MergeConfig(File.ReadAllLines(shipped), File.ReadAllLines(mine));
                            File.WriteAllLines(dest + ".tmp", merged);
                            File.Move(dest + ".tmp", dest, overwrite: true);
                            File.Delete(shipped);
                        }
                        else File.Move(shipped, dest, overwrite: true);
                        AddOnce(state.Files, rel);
                        continue;
                    }
                    if (File.Exists(dest) && !before.Contains(rel) && !state.Backups.ContainsKey(rel) && !(adopt && UltraPlace(dest)))
                    {
                        if (backup)
                        {
                            var copy = Path.Combine(backupRoot, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                            File.Copy(dest, copy, overwrite: true);
                            state.Backups[rel] = copy;
                        }
                    }
                    // mods.txt lists every UE4SS mod: the user's own stay on, Ultra+'s are added.
                    if (entry.Name.Equals("mods.txt", StringComparison.OrdinalIgnoreCase) && File.Exists(dest))
                    {
                        using var reader = new StreamReader(entry.Open());
                        File.WriteAllLines(dest, MergeModsTxt(File.ReadAllLines(dest), reader.ReadToEnd().Split('\n')));
                    }
                    else entry.ExtractToFile(dest, overwrite: true);
                    written.Add(dest);
                    AddOnce(state.Files, rel);
                }
            }
            catch
            {
                // A first install that failed half way leaves nothing behind (the game's files it replaced come back from their
                // backups); an update keeps what was there.
                if (previous is null)
                {
                    foreach (var f in written) try { File.Delete(f); } catch { }
                    foreach (var (rel, copy) in state.Backups) try { File.Copy(copy, Path.Combine(root, rel), overwrite: true); } catch { }
                    RemoveEmptyFolders(root, written.Select(f => Path.GetRelativePath(root, f)));
                }
                throw;
            }
            // Files of the old build that the new one no longer has (renamed paks, UE4SS signatures).
            foreach (var rel in before.Where(r => !state.Files.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                if (previous!.Addons.Values.Any(a => a.Files.Contains(rel, StringComparer.OrdinalIgnoreCase))) continue;
                // Switched to "I manage my own UE4SS": the UE4SS that went in before stays (and stays Ultra+'s to remove).
                if (ownUe4ss && IsUe4ssRuntime(rel))
                {
                    AddOnce(state.Files, rel);
                    continue;
                }
                try { File.Delete(Path.Combine(root, rel)); } catch { }
            }
            // The user's own console variables, kept when Ultra+ was uninstalled (the archive never ships this file).
            var custom = Path.Combine(savedSettings, CustomFile);
            if (File.Exists(custom) && layout.ConfigDir is { } cfg && Directory.Exists(cfg) && !File.Exists(Path.Combine(cfg, CustomFile)))
                try { File.Copy(custom, Path.Combine(cfg, CustomFile)); } catch (Exception ex) { Log.Warn($"Ultra+: {CustomFile} not brought back: {ex.Message}"); }
            if (Directory.Exists(savedSettings)) try { Directory.Delete(savedSettings, true); } catch { }
        }, ct);
        Log.Info($"Ultra+: {file.FileName} installed for {game.Key} in {root}, {state.Files.Count} files{(ownUe4ss ? " (own UE4SS)" : "")}{(adopt ? " (taken over from an install made elsewhere)" : "")}{(previous is not null ? $", was {previous.File}" : "")}");
        return state;
    }

    static void AddOnce(List<string> list, string item)
    {
        if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<key>[\w.]+)=(?<value>.*)$")]
    private static partial Regex SettingRx();

    /// <summary>The shipped config (its comments, order and new settings) with the user's values put back, as the Ultra+ Manager does.</summary>
    public static List<string> MergeConfig(IReadOnlyList<string> shipped, IReadOnlyList<string> user)
    {
        var values = ReadValues(user);
        return shipped.Select(line =>
        {
            var m = SettingRx().Match(line.TrimEnd());
            if (!m.Success || !values.TryGetValue(m.Groups["key"].Value, out var v)) return line;
            int c = m.Groups["value"].Value.IndexOf("; ", StringComparison.Ordinal);
            return $"{m.Groups["indent"].Value}{m.Groups["key"].Value}={v}{(c > 0 ? " " + m.Groups["value"].Value[c..] : "")}";
        }).ToList();
    }

    public static Dictionary<string, string> ReadValues(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var m = SettingRx().Match(line.TrimEnd().TrimStart('﻿'));
            if (!m.Success) continue;
            var v = m.Groups["value"].Value;
            int c = v.IndexOf("; ", StringComparison.Ordinal);
            values[m.Groups["key"].Value] = (c > 0 ? v[..c] : v).Trim();
        }
        return values;
    }

    static List<string> MergeModsTxt(string[] existing, string[] shipped)
    {
        var result = existing.Select(l => l.TrimEnd('\r')).ToList();
        foreach (var raw in shipped)
        {
            var line = raw.Trim().TrimStart('﻿');
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            int at = result.FindIndex(l => l.Split(':')[0].Trim().TrimStart('﻿').Equals(name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) result[at] = line;
            else result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Takes Ultra+ out: its files and its add-ons' files go, the game's own files it replaced come back. The user's settings are
    /// kept in Hybrid Launcher's data first, so installing Ultra+ again later brings them back. With <paramref name="keepUe4ss"/>
    /// (the game has other UE4SS mods) UE4SS itself and its proxy dll stay.
    /// </summary>
    public static List<string> Uninstall(UltraPlusState state, UltraLayout layout, bool keepUe4ss)
    {
        var problems = new List<string>();
        var root = state.Root.Length > 0 ? state.Root : layout.Root;
        if (layout.ConfigDir is { } cfg && Directory.Exists(cfg))
        {
            var keep = Directory.CreateDirectory(SavedSettingsDir(state.GameKey)).FullName;
            foreach (var name in UserOwned.Append(CustomFile))
                try { if (File.Exists(Path.Combine(cfg, name))) File.Copy(Path.Combine(cfg, name), Path.Combine(keep, name), overwrite: true); }
                catch { }
        }
        var files = state.Files.Concat(state.Addons.Values.SelectMany(a => a.Files)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var rel in files)
        {
            if (keepUe4ss && IsUe4ssRuntime(rel)) continue;
            var path = Path.Combine(root, rel);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        // Left by the mod while it ran (logs, its own settings): the UltraPlusExtensions folder goes as a whole.
        if (layout.Ue4ssDir is { } ue && Directory.Exists(Path.Combine(ue, "Mods", "UltraPlusExtensions")))
            try { Directory.Delete(Path.Combine(ue, "Mods", "UltraPlusExtensions"), true); } catch (Exception ex) { problems.Add($"UltraPlusExtensions: {ex.Message}"); }
        // UE4SS writes its log next to itself: removed with it, when Ultra+ brought it.
        if (!keepUe4ss && layout.Ue4ssDir is { } u4 && files.Any(f => f.EndsWith("UE4SS.dll", StringComparison.OrdinalIgnoreCase)))
            foreach (var junk in new[] { "UE4SS.log", "UE4SS_ObjectDump.txt" }) try { File.Delete(Path.Combine(u4, junk)); } catch { }
        foreach (var (rel, copy) in state.Backups)
        {
            try { if (File.Exists(copy)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.Copy(copy, Path.Combine(root, rel), overwrite: true); } }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        RemoveEmptyFolders(root, files);
        Log.Info($"Ultra+: {state.File} removed from {root}{(keepUe4ss ? " (UE4SS kept)" : "")}{(problems.Count > 0 ? ", problems: " + string.Join("; ", problems) : "")}");
        return problems;
    }

    /// <summary>UE4SS itself (not a mod of it): its dll, settings, proxy, and the mods that are not Ultra+'s.</summary>
    static bool IsUe4ssRuntime(string rel)
    {
        var n = rel.Replace('/', '\\');
        if (n.Contains(@"\Mods\UltraPlusExtensions\", StringComparison.OrdinalIgnoreCase) || n.Contains(@"\Mods\UObjectCacheMod\", StringComparison.OrdinalIgnoreCase)) return false;
        if (Regex.IsMatch(n, @"\.(pak|ucas|utoc|sig)$", RegexOptions.IgnoreCase)) return false;
        return n.Contains(@"\ue4ss\", StringComparison.OrdinalIgnoreCase) || RootInjection.Contains(Path.GetFileName(n));
    }

    static void RemoveEmptyFolders(string root, IEnumerable<string> files)
    {
        foreach (var folder in files.Select(f => Path.GetDirectoryName(f)).Where(f => !string.IsNullOrEmpty(f)).Distinct()
                     .SelectMany(f => Enumerable.Range(1, f!.Count(c => c == '\\') + 1).Select(n => string.Join('\\', f!.Split('\\').Take(n)))).Distinct()
                     .OrderByDescending(f => f.Length))
        {
            var full = Path.Combine(root, folder);
            try { if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full); } catch { }
        }
    }

    // ---------- add-ons ----------

    /// <summary>An add-on's paks go in Content\Paks\~mods, its .asi beside the exe (games that are not Unreal: as the archive has them).</summary>
    public static async Task<UltraPlusAddonState> InstallAddonAsync(UltraGame game, UltraFile file, string zip, UltraLayout layout, UltraPlusState state, CancellationToken ct)
    {
        var name = AddonName(game.Key, file.FileName);
        var addon = new UltraPlusAddonState { File = file.FileName, Version = file.Version, Published = file.Published, InstalledAt = DateTime.Now };
        var root = state.Root.Length > 0 ? state.Root : layout.Root;
        if (state.Addons.TryGetValue(name, out var old)) RemoveFiles(root, old.Files.Where(f => !state.Files.Contains(f, StringComparer.OrdinalIgnoreCase)));
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.Name.Length == 0) continue;
                string? dest;
                if (game.InstallRootOnly)
                    dest = entry.Name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) && layout.ExeDir is { } d ? Path.Combine(d, entry.Name) : Destination(entry.FullName, layout, true);
                else if (Regex.IsMatch(entry.Name, @"\.(pak|ucas|utoc|sig)$", RegexOptions.IgnoreCase))
                    dest = layout.PakModsDir is { } p ? Path.Combine(p, entry.Name) : null;
                else if (entry.Name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
                    dest = layout.ExeDir is { } e ? Path.Combine(e, entry.Name) : null;
                else dest = null;
                if (dest is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                AddOnce(addon.Files, Path.GetRelativePath(root, dest));
            }
        }, ct);
        if (addon.Files.Count == 0) throw new InvalidDataException($"{file.FileName} holds no pak or asi file to install.");
        Log.Info($"Ultra+: add-on {name} ({file.FileName}) installed for {game.Key}, {addon.Files.Count} files");
        return addon;
    }

    public static void UninstallAddon(UltraPlusState state, string name)
    {
        if (!state.Addons.Remove(name, out var addon)) return;
        RemoveFiles(state.Root, addon.Files.Where(f => !state.Files.Contains(f, StringComparer.OrdinalIgnoreCase)));
        Log.Info($"Ultra+: add-on {name} removed");
    }

    static void RemoveFiles(string root, IEnumerable<string> files)
    {
        foreach (var rel in files) try { File.Delete(Path.Combine(root, rel)); } catch { }
    }

    // ---------- settings ----------

    /// <summary>
    /// The settings of the installed mod: what each one is from parser_friendly_settings.ini (shipped with it; without it, from
    /// the comments in UltraPlusConfig.ini), with Ultra+'s friendly names, descriptions and groups.
    /// </summary>
    public static List<UltraSetting> ReadSettings(string configDir)
    {
        var settings = new Dictionary<string, UltraSetting>(StringComparer.OrdinalIgnoreCase);
        var schema = Path.Combine(configDir, "parser_friendly_settings.ini");
        if (File.Exists(schema))
        {
            foreach (var raw in File.ReadAllLines(schema))
            {
                var line = raw.Trim().TrimStart('﻿');
                int eq = line.IndexOf('='), dot = line.IndexOf('.');
                if (eq <= 0 || dot <= 0 || dot > eq) continue;
                var key = line[..dot];
                var prop = line[(dot + 1)..eq];
                var value = line[(eq + 1)..].Trim().Trim('"');
                if (!settings.TryGetValue(key, out var s)) settings[key] = s = new UltraSetting { Key = key };
                switch (prop.ToLowerInvariant())
                {
                    case "comment": s.Description = value; break;
                    case "type": s.Numeric = value.Equals("numeric", StringComparison.OrdinalIgnoreCase); break;
                    case "default": s.Default = value; break;
                    case "values": s.Values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                    case "valuetype": s.IsFloat = value.Equals("float", StringComparison.OrdinalIgnoreCase); break;
                    case "min": s.Min = Num(value, 0); break;
                    case "max": s.Max = Num(value, 1); break;
                    case "step": s.Step = Num(value, 0.1); break;
                    case "category": s.Category = value; break;
                }
            }
        }
        // Settings the schema misses (older builds): read from the config's own comments ("game/off/on; what it does").
        var config = Path.Combine(configDir, ConfigFile);
        if (File.Exists(config))
        {
            var comments = new List<string>();
            foreach (var raw in File.ReadAllLines(config))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.StartsWith(';')) { comments.Add(line.TrimStart(';').Trim()); continue; }
                var m = SettingRx().Match(line);
                if (!m.Success) { comments.Clear(); continue; }
                var key = m.Groups["key"].Value;
                if (!settings.TryGetValue(key, out var s))
                {
                    settings[key] = s = new UltraSetting { Key = key };
                    var desc = comments.LastOrDefault() ?? "";
                    int semi = desc.IndexOf(';');
                    if (semi > 0 && desc[..semi].Contains('/') && !desc[..semi].Contains(' '))
                    {
                        s.Values = desc[..semi].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                        desc = desc[(semi + 1)..].Trim();
                    }
                    else
                    {
                        var range = Regex.Match(desc, @"\((?<min>-?[\d.]+)\s*-\s*(?<max>-?[\d.]+)");
                        if (range.Success)
                        {
                            s.Numeric = true;
                            s.IsFloat = true;
                            s.Min = Num(range.Groups["min"].Value, 0);
                            s.Max = Num(range.Groups["max"].Value, 1);
                        }
                    }
                    s.Description = desc;
                }
                // "CTRL + F10" above the description: the key that changes it in game.
                s.Hotkey ??= comments.FirstOrDefault(c => Regex.IsMatch(c, @"^((CTRL|ALT|SHIFT)\s*\+\s*)*F\d{1,2}$", RegexOptions.IgnoreCase));
                comments.Clear();
            }
        }
        foreach (var s in settings.Values)
        {
            s.Name = _names?.GetValueOrDefault(s.Key) is { Length: > 0 } n ? n : Spaced(s.Key);
            if (_descriptions?.GetValueOrDefault(s.Key) is { Length: > 0 } d) s.Description = d;
            else s.Description = Regex.Replace(s.Description, @"^[\w./]+;\s*", "");
            if (_categories?.FirstOrDefault(c => c.Value.Contains(s.Key, StringComparer.OrdinalIgnoreCase)).Key is { } cat) s.Category = cat;
            if (s.Category.Length == 0) s.Category = "Other";
            if (s.Numeric && s.Step <= 0) s.Step = s.IsFloat ? 0.1 : 1;
        }
        return settings.Values.ToList();

        static double Num(string s, double fallback) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>The groups' order on the page: Ultra+'s own order, then the rest by name.</summary>
    public static int CategoryOrder(string category)
    {
        if (_categories is null) return 0;
        int i = _categories.Keys.ToList().FindIndex(k => k.Equals(category, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 1000 : i;
    }

    public static Dictionary<string, string> ReadConfig(string configDir)
    {
        var path = Path.Combine(configDir, ConfigFile);
        return File.Exists(path) ? ReadValues(File.ReadAllLines(path)) : new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Writes values into UltraPlusConfig.ini line by line: comments and order stay, a setting it lacks is added at the end.</summary>
    public static void WriteConfig(string configDir, IReadOnlyDictionary<string, string> values)
    {
        var path = Path.Combine(configDir, ConfigFile);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var left = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var m = SettingRx().Match(lines[i].TrimEnd().TrimStart('﻿'));
            if (!m.Success || !left.Remove(m.Groups["key"].Value, out var v)) continue;
            int c = m.Groups["value"].Value.IndexOf("; ", StringComparison.Ordinal);
            lines[i] = $"{m.Groups["indent"].Value}{m.Groups["key"].Value}={v}{(c > 0 ? " " + m.Groups["value"].Value[c..] : "")}";
        }
        if (left.Count > 0)
        {
            lines.Add("");
            foreach (var (k, v) in left) lines.Add($"{k}={v}");
        }
        File.WriteAllLines(path + ".tmp", lines);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>The presets the mod ships (preset_low.ini … preset_epic.ini), lowest first.</summary>
    public static List<(string Name, Dictionary<string, string> Values)> ShippedPresets(string configDir)
    {
        string[] order = ["low", "medium", "high", "veryhigh", "epic", "ultra", "insane"];
        try
        {
            return Directory.GetFiles(configDir, "preset_*.ini")
                .Select(p => (Name: Path.GetFileNameWithoutExtension(p)["preset_".Length..], Values: ReadValues(File.ReadAllLines(p))))
                .Where(p => p.Values.Count > 0)
                .OrderBy(p => Array.IndexOf(order, p.Name.ToLowerInvariant()) is var i && i < 0 ? 99 : i).ThenBy(p => p.Name)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>The values the mod ships (UltraPlusConfig.default, saved at each install), or the schema's defaults.</summary>
    public static Dictionary<string, string> Defaults(string configDir, IEnumerable<UltraSetting> settings)
    {
        var shipped = Path.Combine(configDir, Path.ChangeExtension(ConfigFile, ".default"));
        if (File.Exists(shipped)) return ReadValues(File.ReadAllLines(shipped));
        return settings.Where(s => s.Default.Length > 0).ToDictionary(s => s.Key, s => s.Default, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>CustomSettings.ini: console variables of the user's own, applied last ([custom] section, one "r.X=value" a line).</summary>
    public static string ReadCustom(string configDir)
    {
        var path = Path.Combine(configDir, CustomFile);
        if (!File.Exists(path)) return "";
        var lines = File.ReadAllLines(path).Select(l => l.TrimStart('﻿')).Where(l => !l.Trim().Equals("[custom]", StringComparison.OrdinalIgnoreCase));
        return string.Join("\r\n", lines).Trim();
    }

    public static void WriteCustom(string configDir, string text)
    {
        var path = Path.Combine(configDir, CustomFile);
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Equals("[custom]", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lines.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        File.WriteAllLines(path, new[] { "[custom]" }.Concat(lines));
    }
}
