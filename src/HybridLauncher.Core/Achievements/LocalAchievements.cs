using System.Globalization;
using System.Text.Json;
using HybridLauncher.Core.Steam;
using Microsoft.Win32;

namespace HybridLauncher.Core.Achievements;

/// <summary>One achievement's state as a local file has it (keyed by its Steam API name).</summary>
public readonly record struct LocalState(bool Unlocked, DateTime? At, double? Now, double? Max);

/// <summary>
/// Achievements that games keep in files on this PC instead of a store's servers (Settings → Achievements → Local achievements).
/// Every known place is read (the per-app folders under %APPDATA%, %LOCALAPPDATA%, %ProgramData% and the public Documents, and
/// the user's own extra folders), in the formats those files come in: JSON (a map of names to "earned"/"earned_time", or a list),
/// INI with a section per achievement ("Achieved", "State", "UnlockTime", "CurProgress"/"MaxProgress"...) or one flat section of
/// names, and a registry key per app. Names, descriptions, pictures and rarity come from the game's own list beside it when there
/// is one, else from Steam (the client's cache on this PC, or Steam's public list of the app's achievements, cached on disk).
/// </summary>
public static class LocalAchievements
{
    /// <summary>A known place: <paramref name="Root"/>\&lt;appid&gt;\<paramref name="Relative"/> (the file, or a folder of them).</summary>
    public sealed record Place(string Root, string Relative);

    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static readonly string PublicDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
    static readonly string Docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>Every known place, whether it exists or not.</summary>
    public static IEnumerable<Place> KnownPlaces()
    {
        yield return new(Path.Combine(AppData, "GSE Saves"), "achievements.json");
        yield return new(Path.Combine(AppData, "Goldberg SteamEmu Saves"), "achievements.json");
        yield return new(Path.Combine(AppData, "EMPRESS"), @"remote\{id}\achievements.json");
        yield return new(Path.Combine(PublicDocs, "EMPRESS"), @"remote\{id}\achievements.json");
        yield return new(Path.Combine(PublicDocs, "Steam", "CODEX"), "achievements.ini");
        yield return new(Path.Combine(PublicDocs, "Steam", "RUNE"), "achievements.ini");
        yield return new(Path.Combine(AppData, "Steam", "CODEX"), "achievements.ini");
        yield return new(Path.Combine(PublicDocs, "OnlineFix"), @"Stats\Achievements.ini");
        yield return new(Path.Combine(ProgramData, "RLD!"), "achievements.ini");
        yield return new(Path.Combine(ProgramData, "Steam", "RLD!"), @"stats\achievements.ini");
        yield return new(Path.Combine(ProgramData, "Steam", "Player"), @"stats\achievements.ini");
        yield return new(Path.Combine(ProgramData, "Steam", "dodi"), @"stats\achievements.ini");
        yield return new(Path.Combine(AppData, "SmartSteamEmu"), @"User\Achievements.ini");
        yield return new(Path.Combine(Local, "SKIDROW"), @"SteamEmu\UserStats\achiev.ini");
        yield return new(Path.Combine(Docs, "SKIDROW"), @"SteamEmu\UserStats\achiev.ini");
        yield return new(Path.Combine(AppData, "CreamAPI"), @"stats\CreamAPI.Achievements.cfg");
        yield return new(Path.Combine(AppData, "TENOKE"), "user_stats.ini");
        yield return new(Path.Combine(Docs, "Steam", "CODEX"), "achievements.ini");
    }

    /// <summary>The roots to read and watch: the known ones that exist, then the user's own folders.</summary>
    public static List<string> Roots()
    {
        var list = KnownPlaces().Select(p => p.Root).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var dir in SettingsStore.Current.LocalAchievementFolders)
            if (Directory.Exists(dir) && !list.Contains(dir, StringComparer.OrdinalIgnoreCase)) list.Add(dir);
        return list;
    }

    /// <summary>Files that can hold achievements (the watcher reacts to these).</summary>
    public static bool IsStateFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ini", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The app id a changed file belongs to: the first all-digits folder below the root it sits in (null if none).</summary>
    public static string? AppIdOf(string path)
    {
        foreach (var root in Roots())
        {
            if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var part in path[(root.TrimEnd('\\').Length + 1)..].Split('\\'))
                if (IsAppId(part)) return part;
            // A user folder that is one game's own: its app id from a steam_appid.txt in it.
            return AppIdInFolder(root);
        }
        return null;
    }

    static bool IsAppId(string s) => s.Length is > 0 and < 12 && s.All(char.IsAsciiDigit);

    // ---------- the game's app id ----------

    /// <summary>
    /// The Steam app id a game runs as when it keeps its achievements locally: from steam_appid.txt or the settings file of the
    /// layer next to its exe (AppId=, RealAppId=, appid=), looked for beside the exe, one folder up and in the install folder.
    /// </summary>
    public static string? DetectAppId(string installDir, string? exe)
    {
        var dirs = new List<string>();
        if (exe is not null && Path.GetDirectoryName(exe) is { } exeDir)
        {
            dirs.Add(exeDir);
            if (Path.GetDirectoryName(exeDir) is { } up) dirs.Add(up);
        }
        dirs.Add(installDir);
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            if (AppIdInFolder(dir) is { } id) return id;
        return null;
    }

    static readonly string[] IdFiles = ["steam_emu.ini", "cream_api.ini", "OnlineFix.ini", "SmartSteamEmu.ini", "ds.ini", "valve.ini", "steam_api.ini", "tenoke.ini", "CPY.ini", "ALI213.ini"];

    static string? AppIdInFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var txt = Path.Combine(dir, "steam_appid.txt");
            if (File.Exists(txt) && File.ReadAllText(txt).Trim() is var t && IsAppId(t)) return t;
            foreach (var name in IdFiles)
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadLines(file).Take(400))
                {
                    var kv = line.Split('=', 2);
                    if (kv.Length != 2) continue;
                    var key = kv[0].Trim();
                    if (!(key.Equals("AppId", StringComparison.OrdinalIgnoreCase) || key.Equals("RealAppId", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("GameId", StringComparison.OrdinalIgnoreCase))) continue;
                    var value = kv[1].Split(';', '#')[0].Trim().Trim('"');
                    if (IsAppId(value) && value != "480") return value;
                }
            }
        }
        catch { }
        return null;
    }

    // ---------- reading ----------

    /// <summary>Every local file that has this app's achievements (newest first).</summary>
    public static List<string> FilesFor(string appId)
    {
        var files = new List<string>();
        void Add(string path) { if (File.Exists(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path); }
        foreach (var p in KnownPlaces())
        {
            if (!Directory.Exists(p.Root)) continue;
            Add(Path.Combine(p.Root, appId, p.Relative.Replace("{id}", appId)));
        }
        foreach (var dir in SettingsStore.Current.LocalAchievementFolders)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var own = Path.GetFileName(dir.TrimEnd('\\')) == appId || AppIdInFolder(dir) == appId ? dir : Path.Combine(dir, appId);
                if (!Directory.Exists(own)) continue;
                foreach (var f in Directory.EnumerateFiles(own, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
                    if (IsStateFile(f) && LooksLikeAchievements(f)) Add(f);
            }
            catch { }
        }
        return files.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
    }

    static bool LooksLikeAchievements(string file)
    {
        var name = Path.GetFileName(file);
        return name.Contains("achiev", StringComparison.OrdinalIgnoreCase) || name.Contains("stats", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether this PC has any local record of the app's achievements (a file or the registry key).</summary>
    public static bool HasRecord(string appId) => FilesFor(appId).Count > 0 || RegistryState(appId).Count > 0;

    /// <summary>The app's achievement states from every local record, merged (an unlock anywhere counts, the earliest time wins).</summary>
    public static Dictionary<string, LocalState> ReadStates(string appId)
    {
        var all = new Dictionary<string, LocalState>(StringComparer.OrdinalIgnoreCase);
        void Merge(Dictionary<string, LocalState> one)
        {
            foreach (var (k, v) in one)
            {
                if (!all.TryGetValue(k, out var had)) { all[k] = v; continue; }
                bool unlocked = had.Unlocked || v.Unlocked;
                DateTime? at = (had.At, v.At) switch { ({ } a, { } b) => a < b ? a : b, ({ } a, null) => a, (null, var b) => b };
                all[k] = new LocalState(unlocked, unlocked ? at : null, Max(had.Now, v.Now), had.Max ?? v.Max);
            }
        }
        foreach (var file in FilesFor(appId))
        {
            try { Merge(ParseFile(file)); }
            catch (Exception ex) { Log.Warn($"Local achievements {Path.GetFileName(file)} ({appId}): {ex.Message}"); }
        }
        Merge(RegistryState(appId));
        return all;
    }

    static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>Reads one file in whichever format it is.</summary>
    public static Dictionary<string, LocalState> ParseFile(string file)
    {
        string text;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var rd = new StreamReader(fs, detectEncodingFromByteOrderMarks: true)) text = rd.ReadToEnd();
        var trimmed = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        // An INI starts with "[Section]" too: JSON only when the bracket opens a list ("[{" or "[]").
        bool json = trimmed.StartsWith('{') || (trimmed.StartsWith('[') && trimmed[1..].TrimStart() is var rest && (rest.StartsWith('{') || rest.StartsWith(']')));
        return json ? ParseJson(trimmed) : ParseIni(text);
    }

    /// <summary>
    /// JSON: {"NAME": {"earned": true, "earned_time": 1700000000, "progress": 3, "max_progress": 10}, ...} or a list of
    /// {"name": "NAME", "achieved": 1, "unlock_time": ...}.
    /// </summary>
    public static Dictionary<string, LocalState> ParseJson(string text)
    {
        var result = new Dictionary<string, LocalState>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object) result[p.Name] = FromJson(p.Value);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var name = JStr(e, "name") ?? JStr(e, "apiname") ?? JStr(e, "id");
                if (name is { Length: > 0 }) result[name] = FromJson(e);
            }
        }
        return result;
    }

    static LocalState FromJson(JsonElement e)
    {
        bool unlocked = JBool(e, "earned") ?? JBool(e, "achieved") ?? JBool(e, "unlocked") ?? JBool(e, "Achieved") ?? false;
        long time = JLong(e, "earned_time") ?? JLong(e, "unlock_time") ?? JLong(e, "UnlockTime") ?? JLong(e, "time") ?? 0;
        double? now = JDouble(e, "progress") ?? JDouble(e, "CurProgress");
        double? max = JDouble(e, "max_progress") ?? JDouble(e, "MaxProgress");
        return Make(unlocked, time, now, max);
    }

    static string? JStr(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? JBool(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => v.GetString() is "1" or "true" or "True",
            _ => null,
        };
    }

    static long? JLong(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l
            : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s : null : null;

    static double? JDouble(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    static readonly HashSet<string> FlatSections = new(StringComparer.OrdinalIgnoreCase) { "Achievements", "Achievement", "SteamAchievements", "AchievementsState" };
    static readonly HashSet<string> NotAchievements = new(StringComparer.OrdinalIgnoreCase)
        { "Steam", "SteamAchievements", "Stats", "SteamStats", "AchievementsUnlockTimes", "Settings", "Config", "General", "Player" };

    /// <summary>
    /// INI: a section per achievement ([NAME] Achieved=1 UnlockTime=... CurProgress=3 MaxProgress=10, or State=... Time=...), or a
    /// flat [Achievements] section of NAME=1 with the times in [AchievementsUnlockTimes].
    /// </summary>
    public static Dictionary<string, LocalState> ParseIni(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current)) sections[name] = current = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var kv = line.Split('=', 2);
            if (kv.Length == 2 && current is not null) current[kv[0].Trim()] = kv[1].Trim().Trim('"');
        }

        var result = new Dictionary<string, LocalState>(StringComparer.OrdinalIgnoreCase);
        sections.TryGetValue("AchievementsUnlockTimes", out var flatTimes);
        foreach (var (name, keys) in sections)
        {
            if (FlatSections.Contains(name) && !keys.Keys.Any(IsStateKey))
            {
                foreach (var (ach, value) in keys)
                {
                    if (ach.Equals("Count", StringComparison.OrdinalIgnoreCase)) continue;
                    long time = flatTimes is not null && flatTimes.TryGetValue(ach, out var tv) ? ParseTime(tv) : 0;
                    result[ach] = Make(Truthy(value), time, null, null);
                }
                continue;
            }
            if (NotAchievements.Contains(name)) continue;
            string? Get(params string[] names) { foreach (var n in names) if (keys.TryGetValue(n, out var v)) return v; return null; }
            var achieved = Get("Achieved", "achieved", "HaveAchieved", "unlocked", "earned");
            var state = Get("State");
            if (achieved is null && state is null) continue;
            bool unlocked = achieved is not null ? Truthy(achieved) : StateUnlocked(state!);
            long t = ParseTime(Get("UnlockTime", "unlocktime", "HaveAchievedTime", "Time", "timestamp", "earned_time") ?? "");
            double? now = Num(Get("CurProgress", "progress", "Progress"));
            double? max = Num(Get("MaxProgress", "max_progress"));
            result[name] = Make(unlocked, t, now, max);
        }
        return result;
    }

    static bool IsStateKey(string k) => k.Equals("Achieved", StringComparison.OrdinalIgnoreCase) || k.Equals("State", StringComparison.OrdinalIgnoreCase)
        || k.Equals("HaveAchieved", StringComparison.OrdinalIgnoreCase) || k.Equals("unlocked", StringComparison.OrdinalIgnoreCase);

    static bool Truthy(string v) => v.Trim() is "1" or "true" or "True" or "TRUE" or "yes";

    // "State" is a hex dump: any non-zero byte means achieved.
    static bool StateUnlocked(string v)
    {
        var s = v.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length > 0 && s.Any(c => c is not '0' and not ' ');
    }

    static double? Num(string? v) => v is not null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>Unix seconds, written in decimal, or as a little-endian hex dump of 4 or 8 bytes.</summary>
    static long ParseTime(string v)
    {
        v = v.Trim();
        if (v.Length == 0) return 0;
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && Plausible(dec)) return dec;
        var hex = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? v[2..] : v;
        if (hex.Length is 8 or 16 && hex.All(Uri.IsHexDigit))
        {
            var bytes = Convert.FromHexString(hex);
            long le = hex.Length == 8 ? BitConverter.ToUInt32(bytes) : BitConverter.ToInt64(bytes);
            if (Plausible(le)) return le;
            Array.Reverse(bytes);
            long be = hex.Length == 8 ? BitConverter.ToUInt32(bytes) : BitConverter.ToInt64(bytes);
            if (Plausible(be)) return be;
        }
        return 0;
    }

    static bool Plausible(long unix) => unix is > 946684800 and < 4102444800; // 2000..2100

    static LocalState Make(bool unlocked, long unix, double? now, double? max) =>
        new(unlocked, unlocked && Plausible(unix) ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : null,
            max is > 0 ? now : null, max is > 0 ? max : null);

    /// <summary>HKCU\Software\GLR\AppID\&lt;id&gt;\Achievements: NAME = 1, NAME_Time = unix seconds.</summary>
    static Dictionary<string, LocalState> RegistryState(string appId)
    {
        var result = new Dictionary<string, LocalState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\GLR\AppID\{appId}\Achievements");
            if (key is null) return result;
            foreach (var name in key.GetValueNames())
            {
                if (name.EndsWith("_Time", StringComparison.OrdinalIgnoreCase)) continue;
                var v = key.GetValue(name);
                bool unlocked = v is int i ? i != 0 : v is string s && Truthy(s);
                long t = key.GetValue(name + "_Time") is int ti ? ti : key.GetValue(name + "_Time") is long tl ? tl : 0;
                result[name] = Make(unlocked, t, null, null);
            }
        }
        catch { }
        return result;
    }

    // ---------- a whole set ----------

    /// <summary>
    /// The app's achievement list with the local state on it, no network: the game's own list (steam_settings) when there is one,
    /// else Steam's (client cache, or the public list fetched earlier). Without any list the names alone are shown. Null when this
    /// PC has neither a list nor a local record for the app.
    /// </summary>
    public static AchievementSet? Read(string appId, string? settingsDir)
    {
        var states = ReadStates(appId);
        var schema = (settingsDir is not null ? SteamEmulator.ReadAchievements(appId, settingsDir)?.Items.ToList() : null)
            ?? SteamSchema.Cached(appId);
        if (states.Count == 0 && (settingsDir is null || schema is null)) return null;
        var items = new List<Achievement>();
        if (schema is { Count: > 0 })
        {
            foreach (var s in schema)
            {
                states.TryGetValue(s.ApiName, out var st);
                items.Add(new Achievement
                {
                    Key = s.ApiName, ApiName = s.ApiName, Name = s.Name, Description = s.Description, Hidden = s.Hidden,
                    IconUrl = s.IconUrl, IconGrayUrl = s.IconGrayUrl, GlobalPercent = s.GlobalPercent,
                    Unlocked = st.Unlocked || s.Unlocked, UnlockedAt = st.At ?? s.UnlockedAt,
                    ProgressNow = st.Unlocked ? null : st.Now, ProgressMax = st.Unlocked ? null : st.Max,
                });
            }
        }
        else
        {
            foreach (var (api, st) in states)
                items.Add(new Achievement { Key = api, ApiName = api, Name = api, Unlocked = st.Unlocked, UnlockedAt = st.At, ProgressNow = st.Now, ProgressMax = st.Max });
        }
        return items.Count > 0 ? new AchievementSet(appId, items) : null;
    }

    /// <summary><see cref="Read"/>, after making sure Steam's public list of the app's achievements is on disk.</summary>
    public static async Task<AchievementSet?> LoadAsync(string appId, string? settingsDir, CancellationToken ct)
    {
        if (settingsDir is null || !File.Exists(Path.Combine(settingsDir, "achievements.json")))
            try { await SteamSchema.FetchAsync(appId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Steam achievement list {appId}: {ex.Message}"); }
        return await Task.Run(() => Read(appId, settingsDir), ct);
    }
}

/// <summary>
/// Steam's public list of an app's achievements (names, descriptions, pictures, hidden flag and the share of players), from the
/// Steam Web API's IPlayerService/GetGameAchievements (no key, no sign-in), or the Steam client's own cache on this PC. Kept on disk
/// for a week.
/// </summary>
public static class SteamSchema
{
    const string IconBase = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/";
    static string CacheName(string appId) => Path.Combine("steam", $"achievement-list-{appId}.json");
    static string Url(string appId) => $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?appid={appId}&language=english";

    /// <summary>The list fetched before (or Steam's client cache), no network; null when neither is here.</summary>
    public static List<Achievement>? Cached(string appId)
    {
        if (SteamAchievements.Schema(appId) is { Count: > 0 } client)
        {
            // The client's list is keyed by stat and bit; local files use the API name.
            return client.Select(a => new Achievement
            {
                Key = a.ApiName, ApiName = a.ApiName, Name = a.Name, Description = a.Description, Hidden = a.Hidden,
                IconUrl = a.IconUrl, IconGrayUrl = a.IconGrayUrl,
            }).ToList();
        }
        var file = Path.Combine(AppInfo.CacheDir, CacheName(appId));
        try { return File.Exists(file) ? Parse(appId, File.ReadAllText(file)) : null; }
        catch { return null; }
    }

    /// <summary>Fetches the public list into the disk cache (once a week); returns it.</summary>
    public static async Task<List<Achievement>?> FetchAsync(string appId, CancellationToken ct)
    {
        var json = await Net.Http.GetCachedAsync(Url(appId), CacheName(appId), TimeSpan.FromDays(7), ct: ct);
        return json is null ? null : Parse(appId, json);
    }

    static List<Achievement>? Parse(string appId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        var items = new List<Achievement>();
        foreach (var a in list.EnumerateArray())
        {
            if (!a.TryGetProperty("internal_name", out var n) || n.GetString() is not { Length: > 0 } api) continue;
            string S(string p) => a.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            double? pct = a.TryGetProperty("player_percent_unlocked", out var pv)
                ? pv.ValueKind == JsonValueKind.Number ? pv.GetDouble()
                : double.TryParse(pv.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null : null;
            items.Add(new Achievement
            {
                Key = api, ApiName = api,
                Name = S("localized_name") is { Length: > 0 } ln ? ln : api,
                Description = S("localized_desc"),
                Hidden = a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True,
                IconUrl = S("icon") is { Length: > 0 } i ? $"{IconBase}{appId}/{i}" : null,
                IconGrayUrl = S("icon_gray") is { Length: > 0 } g ? $"{IconBase}{appId}/{g}" : null,
                GlobalPercent = pct,
            });
        }
        return items.Count > 0 ? items : null;
    }
}
