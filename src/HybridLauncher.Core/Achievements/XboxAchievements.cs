using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Net;
using HybridLauncher.Core.Steam;

namespace HybridLauncher.Core.Achievements;

/// <summary>
/// Xbox (PC Game Pass / Microsoft Store) achievements, two ways:
///  • with no sign-in, from the Xbox app's own cache on this PC
///    (%LOCALAPPDATA%\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\LocalState\AsyncCache.db, a SQLite file): the Xbox app stores a
///    game's list there when it shows the game, so it is as fresh as the last time the Xbox app opened that game's page;
///  • with a Microsoft account signed in (<see cref="XboxAccount"/>), live from Xbox's achievements service: always current, and
///    read every half minute while an Xbox game runs, so its unlocks pop up like Steam's.
/// The game is matched by its package family name (from its AUMID), then by name. Anything unexpected in the cache means "no
/// achievements" rather than an error.
/// </summary>
public static class XboxAchievements
{
    const string Db = "AsyncCache.db";

    /// <summary>The Xbox app's cache file, or null when the Xbox app is not installed or never ran.</summary>
    public static string? CacheFile
    {
        get
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.GamingApp_8wekyb3d8bbwe", "LocalState", Db);
            return File.Exists(f) ? f : null;
        }
    }

    /// <summary>"Publisher.Game_hash!App" → "Publisher.Game_hash".</summary>
    public static string? Pfn(Game game)
    {
        var aumid = game.XboxAumid;
        if (string.IsNullOrWhiteSpace(aumid)) return null;
        int bang = aumid.IndexOf('!');
        var pfn = (bang >= 0 ? aumid[..bang] : aumid).Trim();
        return pfn.Length > 0 ? pfn : null;
    }

    /// <summary>The key achievements of this game go by in the watcher and the game page ("xbox:" + the PFN or the game id).</summary>
    public static string Key(Game game) => "xbox:" + (Pfn(game) ?? game.Id);

    public static bool Applies(Game game) => game.Store == GameStore.Xbox && SettingsStore.Current.ReadXboxAchievements;

    static readonly Dictionary<string, (DateTime Stamp, AchievementSet? Set, string? TitleId)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's list: live when signed in (falls back to the cache), else from the Xbox app's cache. Null when none.</summary>
    public static async Task<AchievementSet?> LoadAsync(Game game, CancellationToken ct)
    {
        if (!Applies(game)) return null;
        var local = await Task.Run(() => ReadCache(game), ct);
        if (!XboxAccount.SignedIn) return local?.Set;
        try
        {
            var titleId = local?.TitleId ?? await XboxAccount.TitleIdAsync(Pfn(game), game.Name, ct);
            if (titleId is null) return local?.Set;
            return await XboxAccount.AchievementsAsync(titleId, ct) ?? local?.Set;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Xbox achievements for {game.Name}: {ex.Message}");
            return local?.Set;
        }
    }

    /// <summary>The Xbox app's cached list for the game and its title id (no network). Re-read only when the file changed.</summary>
    public static (AchievementSet? Set, string? TitleId)? ReadCache(Game game)
    {
        var file = CacheFile;
        if (file is null) return null;
        var pfn = Pfn(game);
        var key = (pfn ?? "") + "|" + game.Name;
        try
        {
            var wal = file + "-wal";
            var stamp = new[] { File.GetLastWriteTimeUtc(file), File.Exists(wal) ? File.GetLastWriteTimeUtc(wal) : DateTime.MinValue }.Max();
            lock (Cache) if (Cache.TryGetValue(key, out var c) && c.Stamp == stamp) return (c.Set, c.TitleId);
            var (set, titleId) = WithDb(file, db => Read(db, pfn, game.Name));
            lock (Cache) Cache[key] = (stamp, set, titleId);
            return (set, titleId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"Xbox app cache ({game.Name}): {ex.Message}");
            return null;
        }
    }

    // The live file first; when the Xbox app holds it mid-write, a private copy (with its journal).
    static T WithDb<T>(string file, Func<WinSqlite, T> read)
    {
        try
        {
            using var db = WinSqlite.Open(file);
            return read(db);
        }
        catch (IOException ex) { Log.Info($"Xbox app cache busy ({ex.Message}), reading a copy"); }
        var dir = Path.Combine(Path.GetTempPath(), "HybridLauncher-xbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Db);
            File.Copy(file, copy);
            if (File.Exists(file + "-wal")) File.Copy(file + "-wal", copy + "-wal");
            using var db = WinSqlite.Open(copy);
            return read(db);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    const string AchQuery = "SELECT value FROM AsyncCache WHERE scope = 'achievements' AND key = ?1 ORDER BY scopeVersion DESC LIMIT 1";

    static (AchievementSet?, string?) Read(WinSqlite db, string? pfn, string name)
    {
        var xuid = OwnerXuid(db);
        if (xuid is null) return (null, null);
        string? firstTitle = null;
        foreach (var titleId in TitleIds(db, xuid, pfn, name))
        {
            firstTitle ??= titleId;
            if (db.Scalar(AchQuery, $"achievements_{xuid}_{titleId}") is { } json && Parse(titleId, json) is { } set) return (set, titleId);
        }
        return (null, firstTitle);
    }

    // The cache also keeps a few friends' lists (to compare): the player is the id with the most lists.
    static string? OwnerXuid(WinSqlite db)
    {
        var counts = new Dictionary<string, int>();
        foreach (var row in db.Query("SELECT key FROM AsyncCache WHERE scope = 'achievements' AND key LIKE 'achievements!_%' ESCAPE '!'", 1))
        {
            var parts = row[0]?.Split('_');
            if (parts is not { Length: 3 } || !parts[1].All(char.IsAsciiDigit)) continue;
            counts[parts[1]] = counts.GetValueOrDefault(parts[1]) + 1;
        }
        return counts.Count == 0 ? null : counts.MaxBy(kv => kv.Value).Key;
    }

    static IEnumerable<string> TitleIds(WinSqlite db, string xuid, string? pfn, string name)
    {
        var seen = new HashSet<string>();
        if (pfn is not null)
        {
            var product = DataString(db.Scalar("SELECT value FROM AsyncCache WHERE scope = 'product_altid_pfn' AND key = ?1 COLLATE NOCASE LIMIT 1", pfn));
            if (product is { Length: 12 })
            {
                foreach (var id in TitleIdsOf(db.Scalar("SELECT value FROM AsyncCache WHERE scope = 'product_summary' AND key = ?1 LIMIT 1", product)))
                    if (seen.Add(id)) yield return id;
                foreach (var row in db.Query("SELECT key FROM AsyncCache WHERE scope = 'product_altid_titleid' AND value LIKE ?1", 1, $"%\"data\":\"{product}\"%"))
                    if (row[0] is { } id && id.All(char.IsAsciiDigit) && seen.Add(id)) yield return id;
            }
        }
        var want = Plain(name);
        if (want.Length == 0) yield break;
        foreach (var row in db.Query("SELECT key, value FROM AsyncCache WHERE scope = 'achievements' AND key LIKE ?1 ESCAPE '!'", 2, $"titleInfo!_{xuid}!_%"))
        {
            var id = row[0]?.Split('_').LastOrDefault();
            if (id is null || !id.All(char.IsAsciiDigit) || seen.Contains(id)) continue;
            if (Plain(NameOf(row[1])) == want && seen.Add(id)) yield return id;
        }
    }

    static string? DataString(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    static List<string> TitleIdsOf(string? json)
    {
        var ids = new List<string>();
        if (json is null) return ids;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("alternateIds", out var alt) && alt.ValueKind == JsonValueKind.Array)
                foreach (var a in alt.EnumerateArray())
                    if (Str(a, "idType") is { } t && t.Equals("XBOXTITLEID", StringComparison.OrdinalIgnoreCase) && Str(a, "id") is { } id && id.All(char.IsAsciiDigit))
                        ids.Add(id);
        }
        catch (JsonException) { }
        return ids;
    }

    static string? NameOf(string? json)
    {
        if (json is null) return null;
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "name") : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Letters and digits only, lower case ("Avowed™" = "avowed").</summary>
    public static string Plain(string? s) => s is null ? "" : new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// A list in the Xbox format (the cache and the live service give the same shape): name, description locked/unlocked or
    /// lockedDescription, gamerscore (or rewards), rarity, progress, unlock time. Null for an empty list.
    /// </summary>
    public static AchievementSet? Parse(string titleId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("achievements", out var inner)) root = inner;
        if (root.ValueKind != JsonValueKind.Array) return null;
        var items = new List<Achievement>();
        foreach (var e in root.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || Str(e, "name") is not { Length: > 0 } name) continue;
            var id = Str(e, "id") ?? (items.Count + 1).ToString(CultureInfo.InvariantCulture);
            bool unlocked = (e.TryGetProperty("isUnlocked", out var u) && u.ValueKind == JsonValueKind.True)
                || Str(e, "progressState") is "Achieved";
            if (e.TryGetProperty("isRevoked", out var rv) && rv.ValueKind == JsonValueKind.True) unlocked = false;
            bool secret = e.TryGetProperty("isSecret", out var s) && s.ValueKind == JsonValueKind.True;
            string desc = "";
            if (e.TryGetProperty("description", out var d))
                desc = d.ValueKind == JsonValueKind.Object ? (unlocked ? Str(d, "unlocked") ?? Str(d, "locked") : Str(d, "locked") ?? Str(d, "unlocked")) ?? ""
                    : d.ValueKind == JsonValueKind.String ? (unlocked ? d.GetString() : Str(e, "lockedDescription") ?? d.GetString()) ?? "" : "";
            DateTime? at = null;
            var ts = Str(e, "timeUnlocked") ?? (e.TryGetProperty("progression", out var pg) ? Str(pg, "timeUnlocked") : null);
            if (unlocked && ts is not null && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) && t.Year >= 2000) at = t.ToLocalTime();
            double? pct = e.TryGetProperty("rarity", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("currentPercentage", out var rp) && rp.ValueKind == JsonValueKind.Number ? rp.GetDouble() : null;
            int? score = e.TryGetProperty("gamerscore", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : null;
            if (score is null && e.TryGetProperty("rewards", out var rw) && rw.ValueKind == JsonValueKind.Array)
                foreach (var x in rw.EnumerateArray())
                    if (Str(x, "type") is "Gamerscore" && int.TryParse(Str(x, "value"), out var gs)) score = gs;
            // Progress: the first requirement's current / target (a percentage on most games).
            double? now = null, max = null;
            JsonElement reqs = default;
            bool hasReqs = e.TryGetProperty("requirements", out reqs) || (e.TryGetProperty("progression", out var pr) && pr.TryGetProperty("requirements", out reqs));
            if (!unlocked && hasReqs && reqs.ValueKind == JsonValueKind.Array && reqs.GetArrayLength() > 0)
            {
                var q = reqs[0];
                double? Num(string n) => q.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
                    : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? dv : null : null;
                if (Num("target") is > 0 and var tg && Num("current") is { } cur && cur > 0) { now = cur; max = tg; }
            }
            string? icon = Str(e, "imageUrl");
            if (icon is null && e.TryGetProperty("mediaAssets", out var ma) && ma.ValueKind == JsonValueKind.Array)
                foreach (var m in ma.EnumerateArray()) if (Str(m, "url") is { } mu) { icon = mu; break; }
            if (icon is not null && Uri.TryCreate(icon, UriKind.Absolute, out var iu) && iu.Host.EndsWith(".xboxlive.com", StringComparison.OrdinalIgnoreCase))
                icon += (icon.Contains('?') ? "&" : "?") + "h=128&format=png";
            items.Add(new Achievement
            {
                Key = id, ApiName = id, Name = name.Trim(), Description = desc.Trim(), Hidden = secret, IconUrl = icon,
                Unlocked = unlocked, UnlockedAt = at, GlobalPercent = pct, Points = score, ProgressNow = now, ProgressMax = max,
            });
        }
        return items.Count > 0 ? new AchievementSet(titleId, items) : null;
    }
}

/// <summary>
/// A Microsoft account signed in for Xbox achievements: the Microsoft sign-in page hands back a code (Hybrid Launcher never sees the
/// password), exchanged with the public client id the Xbox sign-in of community tools uses, then for an Xbox Live user token and an
/// XSTS token. Tokens are kept encrypted (<see cref="AccountTokens"/>, "xbox").
/// </summary>
public static class XboxAccount
{
    const string ClientId = "000000004C12AE6F";
    const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";
    public const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";
    const string Store = "xbox";

    public static string LoginUrl =>
        $"https://login.live.com/oauth20_authorize.srf?client_id={ClientId}&response_type=code&scope={Uri.EscapeDataString(Scope)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&display=touch&locale=en";

    public static bool SignedIn => AccountTokens.Has(Store);

    public static string? Gamertag => AccountTokens.Read(Store)?.GetValueOrDefault("gamertag");

    /// <summary>The code once the sign-in page went on to its "desktop" address (or the error it gave).</summary>
    public static string? CodeFromUrl(string url)
    {
        if (!url.StartsWith(RedirectUri, StringComparison.OrdinalIgnoreCase)) return null;
        var query = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        if (query.TryGetValue("error", out var err)) throw new InvalidOperationException(query.GetValueOrDefault("error_description") ?? err);
        return query.GetValueOrDefault("code");
    }

    public static async Task<string> SignInAsync(string code, CancellationToken ct)
    {
        var live = await LiveTokenAsync($"grant_type=authorization_code&code={Uri.EscapeDataString(code)}", ct);
        var t = new Dictionary<string, string> { ["refresh"] = live.Refresh };
        await XstsAsync(t, live.Access, ct);
        return t.GetValueOrDefault("gamertag") is { Length: > 0 } tag ? tag : "Xbox";
    }

    public static void SignOut() => AccountTokens.Delete(Store);

    static async Task<(string Access, string Refresh)> LiveTokenAsync(string grant, CancellationToken ct)
    {
        using var content = new StringContent($"client_id={ClientId}&scope={Uri.EscapeDataString(Scope)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&{grant}",
            Encoding.UTF8, "application/x-www-form-urlencoded");
        using var resp = await Http.Client.PostAsync("https://login.live.com/oauth20_token.srf", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(L.F("Microsoft refused the sign-in ({0}).", (int)resp.StatusCode));
        using var doc = JsonDocument.Parse(text);
        return (doc.RootElement.GetProperty("access_token").GetString()!, doc.RootElement.GetProperty("refresh_token").GetString()!);
    }

    // Microsoft token → Xbox Live user token → XSTS token (with the user hash, XUID and gamertag); saved with the refresh token.
    static async Task XstsAsync(Dictionary<string, string> t, string liveToken, CancellationToken ct)
    {
        var user = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate",
            "{\"RelyingParty\":\"http://auth.xboxlive.com\",\"TokenType\":\"JWT\",\"Properties\":{\"AuthMethod\":\"RPS\",\"SiteName\":\"user.auth.xboxlive.com\",\"RpsTicket\":\"" + liveToken + "\"}}", ct);
        string userToken;
        using (var doc = JsonDocument.Parse(user)) userToken = doc.RootElement.GetProperty("Token").GetString()!;
        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize",
            "{\"RelyingParty\":\"http://xboxlive.com\",\"TokenType\":\"JWT\",\"Properties\":{\"UserTokens\":[\"" + userToken + "\"],\"SandboxId\":\"RETAIL\"}}", ct);
        using var x = JsonDocument.Parse(xsts);
        var claims = x.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0];
        t["xsts"] = x.RootElement.GetProperty("Token").GetString()!;
        t["uhs"] = claims.GetProperty("uhs").GetString()!;
        if (claims.TryGetProperty("xid", out var xid)) t["xuid"] = xid.GetString()!;
        if (claims.TryGetProperty("gtg", out var gtg)) t["gamertag"] = gtg.GetString()!;
        t["expires"] = (x.RootElement.TryGetProperty("NotAfter", out var na) && DateTime.TryParse(na.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var until)
            ? until.AddMinutes(-5) : DateTime.UtcNow.AddHours(12)).ToString("O");
        AccountTokens.Write(Store, t);
    }

    static async Task<string> PostJsonAsync(string url, string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-xbl-contract-version", "1");
        using var resp = await Http.Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(L.F("Xbox refused the sign-in ({0}).", (int)resp.StatusCode));
        return text;
    }

    static readonly SemaphoreSlim Lock = new(1);

    /// <summary>The saved tokens, renewed when the XSTS token ran out (they last about 16 hours; the refresh token months).</summary>
    static async Task<Dictionary<string, string>> TokensAsync(CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            var t = AccountTokens.Read(Store) ?? throw new InvalidOperationException(L.T("Sign in to Xbox first (Settings → Achievements)."));
            if (DateTime.TryParse(t.GetValueOrDefault("expires"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var exp) && exp > DateTime.UtcNow && t.ContainsKey("xsts"))
                return t;
            var live = await LiveTokenAsync($"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(t["refresh"])}", ct);
            t["refresh"] = live.Refresh;
            await XstsAsync(t, live.Access, ct);
            return t;
        }
        finally { Lock.Release(); }
    }

    static async Task<string> GetAsync(string url, string contract, CancellationToken ct)
    {
        var t = await TokensAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"XBL3.0 x={t["uhs"]};{t["xsts"]}");
        req.Headers.Add("x-xbl-contract-version", contract);
        req.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(CultureInfo.CurrentUICulture.Name));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await Http.Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Ask for new tokens next time.
            t["expires"] = DateTime.MinValue.ToString("O");
            AccountTokens.Write(Store, t);
        }
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Xbox {(int)resp.StatusCode}");
        return text;
    }

    static string Xuid(Dictionary<string, string> t) => t.GetValueOrDefault("xuid") ?? throw new InvalidOperationException("Xbox: no XUID");

    /// <summary>The game's title id from the account's title history, by package family name, then by name.</summary>
    public static async Task<string?> TitleIdAsync(string? pfn, string name, CancellationToken ct)
    {
        var t = await TokensAsync(ct);
        var json = await GetAsync($"https://titlehub.xboxlive.com/users/xuid({Xuid(t)})/titles/titlehistory/decoration/achievement?maxItems=1000", "2", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) return null;
        var want = XboxAchievements.Plain(name);
        string? byName = null;
        foreach (var title in titles.EnumerateArray())
        {
            var id = title.TryGetProperty("titleId", out var tid) ? tid.GetString() : null;
            if (id is null) continue;
            if (pfn is not null && title.TryGetProperty("pfn", out var p) && string.Equals(p.GetString(), pfn, StringComparison.OrdinalIgnoreCase)) return id;
            if (byName is null && title.TryGetProperty("name", out var n) && XboxAchievements.Plain(n.GetString()) == want) byName = id;
        }
        return byName;
    }

    /// <summary>The account's achievements for a title, live (null when the title has none).</summary>
    public static async Task<AchievementSet?> AchievementsAsync(string titleId, CancellationToken ct)
    {
        var t = await TokensAsync(ct);
        var json = await GetAsync($"https://achievements.xboxlive.com/users/xuid({Xuid(t)})/achievements?titleId={titleId}&maxItems=1000", "2", ct);
        return XboxAchievements.Parse(titleId, json);
    }
}
