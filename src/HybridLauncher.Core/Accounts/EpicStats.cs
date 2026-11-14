using System.Globalization;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Steam;

namespace HybridLauncher.Core.Accounts;

/// <summary>
/// Play time and achievements of the signed-in Epic Games account (2.1-rc.4): the Epic Games Launcher keeps neither on this
/// PC, so Epic games showed no hours and no trophies, installed or not. Both come from the services the launcher itself reads:
/// the library service's play time (seconds per app, every game ever played on the account) and the launcher's GraphQL
/// achievements (definitions by the game's namespace, the account's unlocks by namespace). Every answer is kept in the cache
/// folder, so the library and the profile read them without the network and a later failure still shows the last numbers.
/// </summary>
public static class EpicStats
{
    static string Dir => AppInfo.Ensure(Path.Combine(AppInfo.CacheDir, "epic"));
    static string PlayFile => Path.Combine(Dir, "playtime.json");
    static string NamespaceFile => Path.Combine(Dir, "namespaces.json");

    static readonly object Gate = new();
    static Dictionary<string, long>? _play;
    static Dictionary<string, string>? _namespaces;
    static DateTime _playAsked;

    /// <summary>
    /// Forgets everything that belongs to the signed-in account (play time, unlocks, a refused service) when the account
    /// signs in or out, so another account never shows the last one's hours or trophies. Definitions are public and stay.
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _play = null;
            _playAsked = default;
            _refused = false;
            try
            {
                var dir = Path.Combine(AppInfo.CacheDir, "epic");
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "ach-player-*.json")) File.Delete(f);
                var play = Path.Combine(dir, "playtime.json");
                if (File.Exists(play)) File.Delete(play);
            }
            catch (Exception ex) { Log.Warn($"Epic stats reset: {ex.Message}"); }
        }
    }

    // ---------- play time ----------

    /// <summary>Minutes Epic counted for the app (the part after "epic:" in the game id), from the last answer kept; null when unknown.</summary>
    public static long? Minutes(string appName)
    {
        lock (Gate)
        {
            _play ??= ReadPlayFile();
            return _play.TryGetValue(appName, out var seconds) && seconds > 0 ? seconds / 60 : null;
        }
    }

    /// <summary>
    /// Asks Epic for the account's play time again when the copy kept is older than <paramref name="maxAge"/> (ten minutes by
    /// default). True when new numbers came in. Never throws: offline or signed out keeps the last copy.
    /// </summary>
    public static async Task<bool> RefreshPlaytimeAsync(CancellationToken ct, TimeSpan? maxAge = null)
    {
        if (!EpicAccount.SignedIn || EpicAccount.AccountId is not { } account) return false;
        var age = maxAge ?? TimeSpan.FromMinutes(10);
        lock (Gate)
        {
            if (DateTime.UtcNow - _playAsked < age) return false;
            if (File.Exists(PlayFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(PlayFile) < age)
            {
                _playAsked = File.GetLastWriteTimeUtc(PlayFile);
                return false;
            }
            _playAsked = DateTime.UtcNow;
        }
        try
        {
            var json = await EpicAccount.GetAuthorizedAsync($"https://library-service.live.use1a.on.epicgames.com/library/api/public/playtime/account/{account}/all", ct);
            var map = ParsePlaytime(json);
            Write(PlayFile, json);
            lock (Gate) _play = map;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Epic play time: {ex.Message}");
            return false;
        }
    }

    /// <summary>The service's answer: [{"accountId","artifactId": app name,"totalTime": seconds}].</summary>
    public static Dictionary<string, long> ParsePlaytime(string json)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.TryGetProperty("artifactId", out var a) && a.GetString() is { Length: > 0 } app
                && e.TryGetProperty("totalTime", out var t) && t.TryGetInt64(out var seconds))
                map[app] = Math.Max(map.GetValueOrDefault(app), seconds);
        return map;
    }

    static Dictionary<string, long> ReadPlayFile()
    {
        try { return File.Exists(PlayFile) ? ParsePlaytime(File.ReadAllText(PlayFile)) : new(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) { Log.Warn($"Epic play time cache: {ex.Message}"); return new(StringComparer.OrdinalIgnoreCase); }
    }

    // ---------- namespaces ----------

    /// <summary>
    /// The game's Epic namespace (the "sandbox" its achievements live in): from the launch link the scan found, else from the
    /// account's library as last listed. Null when neither knows it.
    /// </summary>
    public static string? NamespaceOf(Game game)
    {
        if (game.Store != GameStore.Epic || !game.Id.StartsWith("epic:", StringComparison.Ordinal)) return null;
        // com.epicgames.launcher://apps/<ns>%3A<catalog>%3A<app>?action=launch
        if (game.EpicLaunchUri is { } uri && uri.IndexOf("/apps/", StringComparison.OrdinalIgnoreCase) is var at and >= 0)
        {
            var rest = Uri.UnescapeDataString(uri[(at + 6)..]);
            if (rest.IndexOf(':') is var colon and > 0) return rest[..colon];
        }
        lock (Gate)
        {
            _namespaces ??= ReadNamespaces();
            return _namespaces.GetValueOrDefault(game.Id["epic:".Length..]);
        }
    }

    /// <summary>Keeps the app → namespace pairs of the account's library (written when the library is listed).</summary>
    public static void KeepNamespaces(IEnumerable<(string App, string Namespace)> pairs)
    {
        lock (Gate)
        {
            _namespaces ??= ReadNamespaces();
            bool changed = false;
            foreach (var (app, ns) in pairs)
                if (!string.Equals(_namespaces.GetValueOrDefault(app), ns, StringComparison.Ordinal)) { _namespaces[app] = ns; changed = true; }
            if (!changed) return;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var (app, ns) in _namespaces) w.WriteString(app, ns);
                w.WriteEndObject();
            }
            Write(NamespaceFile, Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    static Dictionary<string, string> ReadNamespaces()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(NamespaceFile)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(NamespaceFile));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.GetString() is { Length: > 0 } ns) map[p.Name] = ns;
        }
        catch (Exception ex) { Log.Warn($"Epic namespaces cache: {ex.Message}"); }
        return map;
    }

    // ---------- achievements ----------

    const string DefinitionsQuery = "query($sandboxId: String!, $locale: String!) { Achievement { productAchievementsRecordBySandbox(sandboxId: $sandboxId, locale: $locale) { productId totalAchievements achievements { achievement { name hidden unlockedDisplayName lockedDisplayName unlockedDescription lockedDescription unlockedIconLink lockedIconLink rarity { percent } } } } } }";
    const string PlayerQuery = "query($epicAccountId: String!, $sandboxId: String!) { PlayerAchievement { playerAchievementGameRecordsBySandbox(epicAccountId: $epicAccountId, sandboxId: $sandboxId) { records { totalUnlocked playerAchievements { playerAchievement { achievementName unlocked unlockDate } } } } } }";

    // The account's unlocks by product (Playnite's SuccessStory reads them this way): asked when the query by sandbox above gives
    // no records. The product id comes with the definitions.
    const string ProfileQuery = "query($epicAccountId: String!, $productId: String!) { PlayerProfile { playerProfile(epicAccountId: $epicAccountId) { productAchievements(productId: $productId) { ... on PlayerProductAchievementsResponseSuccess { data { totalUnlocked playerAchievements { playerAchievement { achievementName unlocked unlockDate } } } } } } } }";

    static string DefsFile(string ns) => Path.Combine(Dir, $"ach-defs-{ns}.json");
    static string PlayerFile(string ns) => Path.Combine(Dir, $"ach-player-{ns}.json");

    /// <summary>Whether Epic achievements are read at all: the setting is on and the account is signed in.</summary>
    public static bool CanRead => SettingsStore.Current.ReadEpicAchievements && EpicAccount.SignedIn;

    /// <summary>The game's achievements as last read (no network); null when never read or the game has none.</summary>
    public static AchievementSet? ReadCached(Game game)
    {
        if (!SettingsStore.Current.ReadEpicAchievements || NamespaceOf(game) is not { } ns) return null;
        try
        {
            if (!File.Exists(DefsFile(ns))) return null;
            var defs = File.ReadAllText(DefsFile(ns));
            var player = File.Exists(PlayerFile(ns)) ? File.ReadAllText(PlayerFile(ns)) : null;
            return Parse(ns, defs, player);
        }
        catch (Exception ex) { Log.Warn($"Epic achievements cache {ns}: {ex.Message}"); return null; }
    }

    /// <summary>
    /// The game's achievements from Epic: the definitions (kept a week: they hardly change) and the account's unlocks (asked
    /// again when the copy kept is older than <paramref name="maxAge"/>, every time by default). Falls back to the copy kept.
    /// </summary>
    public static async Task<AchievementSet?> ReadAsync(Game game, CancellationToken ct, TimeSpan? maxAge = null)
    {
        if (!CanRead || _refused || NamespaceOf(game) is not { } ns || EpicAccount.AccountId is not { } account) return ReadCached(game);
        // Signed out, another account, or the source turned off while Epic answered: nothing of this read is kept.
        bool Still() => CanRead && EpicAccount.AccountId == account;
        try
        {
            string defs;
            var defsFile = DefsFile(ns);
            string? kept = File.Exists(defsFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(defsFile) < TimeSpan.FromDays(7)
                ? await File.ReadAllTextAsync(defsFile, ct) : null;
            // Copies kept before rc.12 have no product id (the second way to the unlocks needs it): asked again once. A game
            // without achievements on Epic answers a null record: that copy is good for the week too.
            if (kept is not null && DefsUsable(kept)) defs = kept;
            else
            {
                defs = await QueryAsync(Body(DefinitionsQuery, ("sandboxId", ns), ("locale", "en-US")), signed: false, ct);
                // Kept a week only when it is a real answer: an error payload would otherwise stop the retries for days.
                if (!IsAnswer(defs)) { Log.Warn($"Epic achievements {ns}: no definitions in the answer"); return ReadCached(game); }
                Write(defsFile, defs);
            }
            // A game without achievements answers with nulls: nothing more to ask.
            if (Parse(ns, defs, null) is null) return null;

            string player;
            var playerFile = PlayerFile(ns);
            if (maxAge is { } age && File.Exists(playerFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(playerFile) < age)
                player = await File.ReadAllTextAsync(playerFile, ct);
            else
            {
                player = await QueryAsync(Body(PlayerQuery, ("epicAccountId", account), ("sandboxId", ns)), signed: true, ct);
                // No records that way: the same unlocks by product, as Playnite's SuccessStory asks them.
                if ((!IsAnswer(player) || !HasRecords(player)) && ProductId(defs) is { } product)
                {
                    var byProduct = await QueryAsync(Body(ProfileQuery, ("epicAccountId", account), ("productId", product)), signed: true, ct);
                    if (IsAnswer(byProduct)) player = byProduct;
                }
                if (!Still()) return null;
                if (!IsAnswer(player)) { Log.Warn($"Epic achievements {ns}: no unlocks in the answer"); return Parse(ns, defs, File.Exists(playerFile) ? await File.ReadAllTextAsync(playerFile, ct) : null); }
                lock (Gate) { if (!Still()) return null; Write(playerFile, player); }
            }
            return Parse(ns, defs, player);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Refused (403): the service did not take this app's requests; not asked again this session, so a library of
            // Epic games does not send one refused request each.
            if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }) _refused = true;
            Log.Warn($"Epic achievements {ns}: {ex.Message}");
            if (!Still()) return null;
            return ReadCached(game);
        }
    }

    // Read by the library's four reads at a time and by the browser's queue: always the latest value.
    static volatile bool _refused;

    /// <summary>Self-test: a public query (a game's definitions, no account) sent straight to Epic; the answer's start, or the error.</summary>
    public static async Task<string> ProbeDirectAsync(string sandbox)
    {
        try
        {
            var text = await EpicAccount.GraphQlAsync(Body(DefinitionsQuery, ("sandboxId", sandbox), ("locale", "en-US")), signed: false, CancellationToken.None);
            return text.Length > 160 ? text[..160] : text;
        }
        catch (Exception ex) { return "failed: " + ex.Message; }
    }

    /// <summary>
    /// A kept copy of the definitions still good: a real answer with the product id (rc.12 on), or Epic's null record for a
    /// game without achievements there (then there is no product id to ask for).
    /// </summary>
    public static bool DefsUsable(string defsJson)
    {
        if (!IsAnswer(defsJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(defsJson);
            if (!doc.RootElement.TryGetProperty("data", out var d) || !d.TryGetProperty("Achievement", out var a)) return false;
            if (a.ValueKind == JsonValueKind.Null) return true;
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("productAchievementsRecordBySandbox", out var r)) return false;
            return r.ValueKind == JsonValueKind.Null || (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("productId", out _));
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The product id the definitions came with (null in copies kept before rc.12).</summary>
    static string? ProductId(string defsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(defsJson);
            return doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("Achievement", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("productAchievementsRecordBySandbox", out var r) && r.ValueKind == JsonValueKind.Object
                ? Str(r, "productId") : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Whether an answer to the query by sandbox holds any record of the account.</summary>
    static bool HasRecords(string playerJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(playerJson);
            return Dig(doc.RootElement, "data", "PlayerAchievement", "playerAchievementGameRecordsBySandbox", "records", out var r)
                && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// The app's own route to Epic's achievements (2.1-rc.11): the query sent from an Epic store page open in the app's
    /// browser, with the session the user signed in with there. Since 2.1-rc.8 Epic's GraphQL refuses direct requests behind a
    /// bot check (403, hCaptcha); a real browser page passes it, or shows the check to the user, who answers it. Hands back
    /// the answer's text, or null when the page could not ask (Epic wants the user to confirm first). Null when the app has
    /// no browser (tests): then the direct request is tried as before.
    /// </summary>
    public static Func<string, bool, CancellationToken, Task<BrowserAnswer>>? Browser { get; set; }

    /// <summary>
    /// What the browser route got: the answer's text; or none, <see cref="Refused"/> when Epic said no (403, its check) and
    /// not when the page could not ask at all (offline, no browser, no reply in time), which is tried again later.
    /// </summary>
    public readonly record struct BrowserAnswer(string? Text, bool Refused)
    {
        public static BrowserAnswer No => new(null, true);
        public static BrowserAnswer Unavailable => new(null, false);
    }

    /// <summary>Raised once when Epic stopped answering until the user confirms on its page (see <see cref="Browser"/>).</summary>
    public static event Action? NeedsUser;

    /// <summary>The user answered Epic's check: achievements are asked again.</summary>
    public static void Retry() { lock (Gate) _refused = false; }

    /// <summary>Whether Epic refused this session (until <see cref="Retry"/>).</summary>
    public static bool Refused => _refused;

    /// <summary>
    /// One query: straight to Epic as its launcher sends it (rc.12, the way Legendary, Heroic and Playnite go). Only when Epic
    /// refuses that (403, its bot check) the store page in the app's browser asks instead (rc.11's way), and only when that
    /// cannot either the reads stop and the user is asked to confirm on Epic's page. Nothing tries to get past Epic's check.
    /// </summary>
    static async Task<string> QueryAsync(string body, bool signed, CancellationToken ct)
    {
        try { return await EpicAccount.GraphQlAsync(body, signed, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden && Browser is not null)
        {
            Log.Warn("Epic achievements: Epic refused the direct request (403); asking from its store page");
        }
        if (_refused) throw new HttpRequestException("Epic asks to confirm on its page first", null, System.Net.HttpStatusCode.Forbidden);
        var answer = await Browser!(body, signed, ct);
        if (answer.Text is { } text) return text;
        // The page could not ask (offline, the browser closed, no reply in time): not Epic's no, so nothing stops; the copy
        // kept is shown and the next read tries again.
        if (!answer.Refused) throw new HttpRequestException("Epic's store page could not ask (offline or no browser)");
        bool first;
        lock (Gate) { first = !_refused; _refused = true; }
        if (first) NeedsUser?.Invoke();
        throw new HttpRequestException("Epic asks to confirm on its page first", null, System.Net.HttpStatusCode.Forbidden);
    }

    /// <summary>A GraphQL answer worth keeping: JSON with a "data" object and no "errors".</summary>
    public static bool IsAnswer(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                && !(r.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0);
        }
        catch (JsonException) { return false; }
    }

    static string Body(string query, params (string Name, string Value)[] vars)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("query", query);
            w.WriteStartObject("variables");
            foreach (var (n, v) in vars) w.WriteString(n, v);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// The definitions and (when given) the account's unlocks, as one set keyed "epic:&lt;namespace&gt;". Null when the game
    /// has no achievements on Epic.
    /// </summary>
    public static AchievementSet? Parse(string ns, string defsJson, string? playerJson)
    {
        using var defs = JsonDocument.Parse(defsJson);
        if (!Dig(defs.RootElement, "data", "Achievement", "productAchievementsRecordBySandbox", "achievements", out var list)
            || list.ValueKind != JsonValueKind.Array) return null;

        var unlocked = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        if (playerJson is not null)
        {
            using var player = JsonDocument.Parse(playerJson);
            // Two shapes: by sandbox (records, each with playerAchievements) and by product (rc.12, one list).
            var lists = new List<JsonElement>();
            if (Dig(player.RootElement, "data", "PlayerAchievement", "playerAchievementGameRecordsBySandbox", "records", out var records)
                && records.ValueKind == JsonValueKind.Array)
                foreach (var rec in records.EnumerateArray())
                    if (rec.TryGetProperty("playerAchievements", out var pas) && pas.ValueKind == JsonValueKind.Array) lists.Add(pas);
            if (Dig(player.RootElement, "data", "PlayerProfile", "playerProfile", "productAchievements", out var prod)
                && prod.ValueKind == JsonValueKind.Object && prod.TryGetProperty("data", out var pd) && pd.ValueKind == JsonValueKind.Object
                && pd.TryGetProperty("playerAchievements", out var ppas) && ppas.ValueKind == JsonValueKind.Array) lists.Add(ppas);
            foreach (var pas in lists)
                        foreach (var pa in pas.EnumerateArray())
                        {
                            if (!pa.TryGetProperty("playerAchievement", out var a) || a.ValueKind != JsonValueKind.Object) continue;
                            if (!(a.TryGetProperty("unlocked", out var u) && u.ValueKind == JsonValueKind.True)) continue;
                            if (Str(a, "achievementName") is not { Length: > 0 } name) continue;
                            DateTime? at = Str(a, "unlockDate") is { Length: > 0 } d
                                && DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.LocalDateTime : null;
                            unlocked[name] = at;
                        }
        }

        var items = new List<Achievement>();
        foreach (var wrap in list.EnumerateArray())
        {
            if (!wrap.TryGetProperty("achievement", out var a) || a.ValueKind != JsonValueKind.Object) continue;
            if (Str(a, "name") is not { Length: > 0 } key) continue;
            bool got = unlocked.TryGetValue(key, out var at);
            bool hidden = a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True;
            double? rarity = a.TryGetProperty("rarity", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("percent", out var pc)
                && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : null;
            items.Add(new Achievement
            {
                Key = key,
                ApiName = key,
                Name = Str(a, "unlockedDisplayName") ?? Str(a, "lockedDisplayName") ?? key,
                // A hidden one keeps its secret until unlocked, as in the Epic Games Launcher.
                Description = (hidden && !got ? Str(a, "lockedDescription") : Str(a, "unlockedDescription")) ?? "",
                Hidden = hidden,
                IconUrl = Str(a, "unlockedIconLink"),
                IconGrayUrl = Str(a, "lockedIconLink"),
                Unlocked = got,
                UnlockedAt = at,
                GlobalPercent = rarity,
            });
        }
        return items.Count > 0 ? new AchievementSet("epic:" + ns, items) : null;
    }

    static bool Dig(JsonElement e, string a, string b, string c, string d, out JsonElement found)
    {
        found = default;
        foreach (var name in new[] { a, b, c, d })
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return false;
        }
        found = e;
        return e.ValueKind != JsonValueKind.Null;
    }

    static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    static void Write(string file, string text)
    {
        try
        {
            var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"Epic stats cache {Path.GetFileName(file)}: {ex.Message}"); }
    }
}
