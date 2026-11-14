using System.Globalization;
using System.Text.Json;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Steam;

public sealed class Achievement
{
    /// <summary>"statId:bit", the key Steam's user stats file uses.</summary>
    public required string Key { get; init; }
    public required string ApiName { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public bool Hidden { get; init; }
    public string? IconUrl { get; init; }
    public string? IconGrayUrl { get; init; }
    public bool Unlocked { get; set; }
    public DateTime? UnlockedAt { get; set; }

    /// <summary>Share of all Steam players who have it (0..100), when Steam's public stats are reachable.</summary>
    public double? GlobalPercent { get; set; }

    /// <summary>Points the store gives for it (Xbox gamerscore).</summary>
    public int? Points { get; init; }

    /// <summary>How far along a locked achievement with steps is (both set, or neither): 3 of 10, 40 of 100 percent.</summary>
    public double? ProgressNow { get; set; }
    public double? ProgressMax { get; set; }
}

public sealed record AchievementSet(string AppId, IReadOnlyList<Achievement> Items)
{
    public int UnlockedCount => Items.Count(a => a.Unlocked);
    public int Total => Items.Count;
    /// <summary>The game's own platinum (the achievement for unlocking all the others), or null when it has none.</summary>
    public Achievement? Platinum => PlatinumAchievement.Find(Items);
}

/// <summary>
/// The platinum is the game's own achievement for unlocking every other one (most games have one: "Tireless — Unlock all
/// achievements"). Found by what it says, in the store's English text or in the main languages GOG may answer in; nothing is
/// invented for a game without one.
/// </summary>
public static class PlatinumAchievement
{
    static readonly System.Text.RegularExpressions.Regex Says = new(
        @"\b(all|every)\b(\s+(of|the|other|others|remaining|previous|base|main|game|steam|available|\w+'s)){0,4}\s+(achievements?|trophies|trophy)\b" +
        @"|\b(achievements?|trophies)\b.{0,12}\b100\s?%|\b100\s?%\s+(of\s+(the\s+)?)?(achievements|trophies)\b" +
        @"|\bplatinum\s+trophy\b" +
        // Italian, French, Spanish, Portuguese, German, Polish, Russian.
        @"|\btutti\s+gli\s+(altri\s+)?(achievement|obiettivi|trofei)|\btous\s+les\s+(autres\s+)?(succ[eè]s|troph[ée]es)" +
        @"|\btodos\s+los\s+(dem[aá]s\s+)?(logros|trofeos)|\btodas\s+as\s+(outras\s+)?conquistas|\btodos\s+os\s+(outros\s+)?trof[eé]us" +
        @"|\balle\s+(anderen\s+)?(erfolge|errungenschaften|troph[äa]en)|\bwszystkie\s+(pozosta[lł]e\s+)?osi[aą]gni[eę]cia|\bвсе\s+(остальные\s+)?(достижения|трофеи)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="a"/> reads as the achievement for unlocking all the others.</summary>
    public static bool Is(Achievement a) => Says.IsMatch(a.Description) || Says.IsMatch(a.Name);

    /// <summary>
    /// The platinum of a game's list: the one whose text says it asks for every achievement (its description first, then its
    /// name). With more than one match (DLC sets), the rarest; null when none says so.
    /// </summary>
    public static Achievement? Find(IReadOnlyList<Achievement> items)
    {
        if (items.Count < 3) return null;
        var byText = items.Where(a => Says.IsMatch(a.Description)).ToList();
        if (byText.Count == 0) byText = items.Where(a => Says.IsMatch(a.Name)).ToList();
        if (byText.Count == 0) return null;
        return byText.OrderBy(a => a.GlobalPercent ?? 100).First();
    }
}

/// <summary>
/// Steam achievements read from the Steam client's own cache (no login, no API key):
/// appcache\stats\UserGameStatsSchema_&lt;appid&gt;.bin has the names, descriptions and icons,
/// UserGameStats_&lt;account&gt;_&lt;appid&gt;.bin which ones the signed-in account has unlocked, and when.
/// </summary>
public static class SteamAchievements
{
    const long SteamId64Base = 76561197960265728;
    const string IconBase = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/";

    static readonly Dictionary<string, (DateTime Stamp, List<Achievement> Items)> SchemaCache = new();
    static readonly object Gate = new();

    public static string? StatsDir
    {
        get
        {
            var root = SteamScanner.SteamRoot();
            var dir = root is null ? null : Path.Combine(root, "appcache", "stats");
            return dir is not null && Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>The 32-bit account id of the most recent Steam login (loginusers.vdf), or null.</summary>
    public static string? CurrentAccountId()
    {
        try
        {
            var root = SteamScanner.SteamRoot();
            var file = root is null ? null : Path.Combine(root, "config", "loginusers.vdf");
            if (file is null || !File.Exists(file)) return null;
            var users = VdfNode.Parse(File.ReadAllText(file)).Child("users");
            if (users is null) return null;
            // Newer Steam builds dropped "MostRecent": then the auto-login account, then the latest sign-in.
            var pick = users.Children.FirstOrDefault(u => u.Value["MostRecent"] == "1");
            if (pick.Key is null) pick = users.Children.FirstOrDefault(u => u.Value["AutoLogin"] == "1");
            if (pick.Key is null) pick = users.Children.OrderByDescending(u => long.TryParse(u.Value["timestamp"], out var t) ? t : 0).FirstOrDefault();
            return pick.Key is not null && long.TryParse(pick.Key, out var id64) && id64 > SteamId64Base
                ? (id64 - SteamId64Base).ToString(CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"loginusers.vdf: {ex.Message}");
            return null;
        }
    }

    /// <summary>The signed-in account's stats file for the game; the newest one of any account when the login is unknown.</summary>
    public static string? UserStatsFile(string appId)
    {
        var dir = StatsDir;
        if (dir is null) return null;
        if (CurrentAccountId() is { } account)
        {
            var own = Path.Combine(dir, $"UserGameStats_{account}_{appId}.bin");
            if (File.Exists(own)) return own;
        }
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles($"UserGameStats_*_{appId}.bin")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>The app id in "UserGameStats_&lt;account&gt;_&lt;appid&gt;.bin", or null for any other file name.</summary>
    public static string? AppIdFromStatsFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("UserGameStats_", StringComparison.OrdinalIgnoreCase)) return null;
        var last = name.LastIndexOf('_');
        var id = last > 0 ? name[(last + 1)..] : "";
        return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
    }

    public static bool HasSchema(string appId) => StatsDir is { } dir && File.Exists(Path.Combine(dir, $"UserGameStatsSchema_{appId}.bin"));

    /// <summary>
    /// The achievements of any game: Steam's for Steam games, the Steam emulator's (Goldberg / GSE) for games run through one
    /// when Settings → Features allows it. No network.
    /// </summary>
    public static AchievementSet? ReadFor(Game game) => game switch
    {
        { Store: GameStore.Steam, SteamAppId: { } id } => SettingsStore.Current.ReadSteamAchievements ? Read(id) : null,
        { EmulatorAppId: { } emu } when SettingsStore.Current.ReadSteamEmulator =>
            Achievements.LocalAchievements.Read(emu, game.EmulatorSettingsDir) is { Total: > 0 } set ? set : null,
        _ when Achievements.XboxAchievements.Applies(game) => Achievements.XboxAchievements.ReadCache(game)?.Set,
        { Store: GameStore.Epic } => Accounts.EpicStats.ReadCached(game),
        _ => null,
    };

    /// <summary><see cref="ReadFor"/>, plus Steam's global percentages when online.</summary>
    public static async Task<AchievementSet?> LoadForAsync(Game game, CancellationToken ct = default)
    {
        if (game is { Store: GameStore.Steam, SteamAppId: { } id }) return SettingsStore.Current.ReadSteamAchievements ? await LoadAsync(id, ct) : null;
        if (Achievements.XboxAchievements.Applies(game)) return await Achievements.XboxAchievements.LoadAsync(game, ct);
        if (Accounts.GogAchievements.ProductId(game) is { } gogId && await Accounts.GogAchievements.ReadAsync(gogId, ct) is { } gogSet) return gogSet;
        if (game.Store == GameStore.Epic) return await Accounts.EpicStats.ReadAsync(game, ct);
        var set = game is { EmulatorAppId: { } emu } && SettingsStore.Current.ReadSteamEmulator
            ? await Achievements.LocalAchievements.LoadAsync(emu, game.EmulatorSettingsDir, ct)
            : await Task.Run(() => ReadFor(game), ct);
        if (set is null) return null;
        try
        {
            var percents = await GlobalPercentagesAsync(set.AppId, ct);
            foreach (var a in set.Items)
                if (percents.TryGetValue(a.ApiName, out var p)) a.GlobalPercent = p;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Global achievement stats {set.AppId}: {ex.Message}"); }
        return set;
    }

    /// <summary>The game's achievements, unlocked state from the user stats file, plus global percentages when online.</summary>
    public static async Task<AchievementSet?> LoadAsync(string appId, CancellationToken ct = default)
    {
        // Steam caches a game's achievements on this PC only once its own client has loaded them (2.0.0-rc.14: Cyberpunk 2077
        // and others showed none): without that file, the account's public achievements page is read instead.
        // All of it on a worker: the page's download continues where it was awaited, and its parse must not land on the UI thread.
        var set = await Task.Run(async () => HasSchema(appId) ? Read(appId) : await CommunityAsync(appId, ct).ConfigureAwait(false) ?? Read(appId), ct);
        if (set is null || set.Items.Count == 0) return set;
        try
        {
            var percents = await GlobalPercentagesAsync(appId, ct);
            foreach (var a in set.Items)
                if (percents.TryGetValue(a.ApiName, out var p)) a.GlobalPercent = p;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Global achievement stats {appId}: {ex.Message}"); }
        return set;
    }

    /// <summary>
    /// Schema + unlocked state, no network. Without Steam's schema on this PC, the achievements last read from the account's
    /// community page (<see cref="CommunityAsync"/>), if any; null otherwise.
    /// </summary>
    public static AchievementSet? Read(string appId)
    {
        var schema = Schema(appId);
        if (schema is null) return ReadCommunity(appId);
        var unlocked = ReadUnlocked(appId);
        var items = schema.Select(s => new Achievement
        {
            Key = s.Key, ApiName = s.ApiName, Name = s.Name, Description = s.Description, Hidden = s.Hidden,
            IconUrl = s.IconUrl, IconGrayUrl = s.IconGrayUrl,
            Unlocked = unlocked.ContainsKey(s.Key),
            UnlockedAt = unlocked.TryGetValue(s.Key, out var t) ? t : null,
        }).ToList();
        return new AchievementSet(appId, items);
    }

    /// <summary>The unlocked achievements as "statId:bit" keys with their unlock time (null when Steam has none).</summary>
    public static Dictionary<string, DateTime?> ReadUnlocked(string appId)
    {
        var result = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        var file = UserStatsFile(appId);
        var bytes = file is null ? null : ReadShared(file);
        if (bytes is null) return result;
        KvNode root;
        try { root = BinaryKv.Parse(bytes); }
        catch (InvalidDataException ex)
        {
            Log.Warn($"{Path.GetFileName(file)}: {ex.Message}");
            return result;
        }

        var cache = root["cache"] ?? root;
        foreach (var stat in cache.Children)
        {
            if (stat.Children.Count == 0 || !int.TryParse(stat.Name, out _)) continue;
            var times = stat["AchievementTimes"];
            long data = stat.Int("data") ?? 0;
            for (int bit = 0; bit < 32; bit++)
            {
                long? time = times?.Int(bit.ToString(CultureInfo.InvariantCulture));
                if (((data >> bit) & 1) == 0 && time is null) continue;
                result[$"{stat.Name}:{bit}"] = time is > 0 ? DateTimeOffset.FromUnixTimeSeconds(time.Value).LocalDateTime : null;
            }
        }
        return result;
    }

    /// <summary>The achievement definitions, cached until Steam rewrites the schema file.</summary>
    public static List<Achievement>? Schema(string appId)
    {
        var dir = StatsDir;
        var file = dir is null ? null : Path.Combine(dir, $"UserGameStatsSchema_{appId}.bin");
        if (file is null || !File.Exists(file)) return null;
        var stamp = File.GetLastWriteTimeUtc(file);
        lock (Gate)
        {
            if (SchemaCache.TryGetValue(appId, out var cached) && cached.Stamp == stamp) return cached.Items;
        }

        var bytes = ReadShared(file);
        if (bytes is null) return null;
        var list = new List<Achievement>();
        try
        {
            var root = BinaryKv.Parse(bytes);
            var app = root[appId] ?? root.Children.FirstOrDefault();
            foreach (var stat in app?["stats"]?.Children ?? [])
            {
                var bits = stat["bits"];
                if (bits is null) continue;
                foreach (var b in bits.Children)
                {
                    var api = b.Str("name");
                    var display = b["display"];
                    if (api is null || display is null) continue;
                    int bit = (int)(b.Int("bit") ?? (int.TryParse(b.Name, out var n) ? n : 0));
                    list.Add(new Achievement
                    {
                        Key = $"{stat.Name}:{bit}",
                        ApiName = api,
                        Name = Localized(display["name"]) ?? api,
                        Description = Localized(display["desc"]) ?? "",
                        Hidden = display.Str("hidden") is "1" or "true",
                        IconUrl = Icon(appId, display.Str("icon")),
                        IconGrayUrl = Icon(appId, display.Str("icon_gray")),
                    });
                }
            }
        }
        catch (InvalidDataException ex)
        {
            Log.Warn($"Achievement schema {appId}: {ex.Message}");
            return null;
        }
        lock (Gate) SchemaCache[appId] = (stamp, list);
        return list;
    }

    // Names come per language ("english", "italian", ... and a "token"); a plain string in older schemas.
    static string? Localized(KvNode? node)
    {
        if (node is null) return null;
        if (node.Value is string s) return s;
        return node.Str("english") ?? node.Children.FirstOrDefault(c => c.Value is string && c.Name != "token")?.Value as string;
    }

    // ---------- the account's community page (games Steam has not cached here) ----------

    static string CommunityCacheName(string appId) => Path.Combine("steam", $"achievements-{appId}.xml");

    /// <summary>
    /// The signed-in Steam account's achievements for the game from its community page
    /// (steamcommunity.com/profiles/&lt;id&gt;/stats/&lt;app&gt;/achievements/?xml=1): names, descriptions, icons, unlocks and their
    /// dates. Works when the profile's game details are public; a private profile gives null (logged once). Cached ten minutes.
    /// </summary>
    public static async Task<AchievementSet?> CommunityAsync(string appId, CancellationToken ct)
    {
        if (CurrentAccountId() is not { } account || !long.TryParse(account, out var id32)) return null;
        try
        {
            var url = $"https://steamcommunity.com/profiles/{SteamId64Base + id32}/stats/{appId}/achievements/?xml=1&l=english";
            var xml = await Http.GetCachedAsync(url, CommunityCacheName(appId), TimeSpan.FromMinutes(10), ct: ct);
            return xml is null ? null : ParseCommunity(appId, xml);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam achievements of {appId} from the community page: {ex.Message}");
            return null;
        }
    }

    /// <summary>The community page read last for the game, no network (null when never read or not usable).</summary>
    public static AchievementSet? ReadCommunity(string appId)
    {
        var file = Path.Combine(AppInfo.CacheDir, CommunityCacheName(appId));
        try { return File.Exists(file) ? ParseCommunity(appId, File.ReadAllText(file), quiet: true) : null; }
        catch (Exception ex) { Log.Warn($"{file}: {ex.Message}"); return null; }
    }

    // Read from several workers at once (one per game): added to under a lock.
    static readonly HashSet<string> CommunityWarned = [];
    static bool FirstWarning(string appId) { lock (CommunityWarned) return CommunityWarned.Add(appId); }

    public static AchievementSet? ParseCommunity(string appId, string xml, bool quiet = false)
    {
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Parse(xml); }
        catch (System.Xml.XmlException)
        {
            // An HTML page instead of the XML (Steam's error or sign-in page).
            if (!quiet && FirstWarning(appId)) Log.Warn($"Steam achievements of {appId}: the community page is not readable");
            return null;
        }
        var root = doc.Root;
        if (root is null) return null;
        if (root.Element("error")?.Value is { Length: > 0 } error)
        {
            if (!quiet && FirstWarning(appId)) Log.Info($"Steam achievements of {appId}: {error.Trim()} (the profile's game details must be public)");
            return null;
        }
        var items = new List<Achievement>();
        foreach (var a in root.Element("achievements")?.Elements("achievement") ?? [])
        {
            var api = a.Element("apiname")?.Value?.Trim();
            if (string.IsNullOrEmpty(api)) continue;
            bool unlocked = a.Attribute("closed")?.Value == "1";
            DateTime? at = long.TryParse(a.Element("unlockTimestamp")?.Value, out var unix) && unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : null;
            items.Add(new Achievement
            {
                Key = api,
                ApiName = api,
                Name = a.Element("name")?.Value?.Trim() is { Length: > 0 } name ? name : api,
                Description = a.Element("description")?.Value?.Trim() ?? "",
                // The page's "closed" icon is the one of an unlocked achievement, "open" the grey one.
                IconUrl = a.Element("iconClosed")?.Value?.Trim(),
                IconGrayUrl = a.Element("iconOpen")?.Value?.Trim(),
                Unlocked = unlocked,
                UnlockedAt = unlocked ? at : null,
            });
        }
        return items.Count == 0 ? null : new AchievementSet(appId, items);
    }

    static string? Icon(string appId, string? file) => string.IsNullOrWhiteSpace(file) ? null : $"{IconBase}{appId}/{file}";

    /// <summary>API name -> share of players (0..100), from Steam's public stats (cached for a day).</summary>
    public static async Task<Dictionary<string, double>> GlobalPercentagesAsync(string appId, CancellationToken ct)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var json = await Http.GetCachedAsync($"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}",
            Path.Combine("steam", $"achievement-pct-{appId}.json"), TimeSpan.FromDays(1), ct: ct);
        if (json is null) return result;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("achievementpercentages", out var root) || !root.TryGetProperty("achievements", out var list)
            || list.ValueKind != JsonValueKind.Array) return result;
        foreach (var a in list.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var name) || name.GetString() is not { } api || !a.TryGetProperty("percent", out var pct)) continue;
            // The percentage arrives as a string ("91.2") or a number, depending on the day.
            double? value = pct.ValueKind switch
            {
                JsonValueKind.Number => pct.GetDouble(),
                JsonValueKind.String when double.TryParse(pct.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            };
            if (value is { } v) result[api] = v;
        }
        return result;
    }

    /// <summary>Reads a file Steam may be rewriting right now; null when it stays locked or vanished.</summary>
    static byte[]? ReadShared(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[fs.Length];
                fs.ReadExactly(buffer);
                return buffer;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(80 * (attempt + 1));
            }
        }
        return null;
    }
}
