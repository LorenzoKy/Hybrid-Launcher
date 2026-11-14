using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

/// <summary>The kinds of art a game can have (SteamGridDB's names in <see cref="SteamGridDb.Path"/>).</summary>
public enum ArtKind { Cover, Banner, Logo, Icon }

/// <summary>One picture on SteamGridDB: the full file, a small preview, its size and who made it.</summary>
public sealed record SgdbAsset(long Id, string Url, string Thumb, int Width, int Height, string? Author, bool Animated);

/// <summary>A game on SteamGridDB, as its search lists it.</summary>
public sealed record SgdbGame(long Id, string Name, int? Year);

/// <summary>
/// Art from SteamGridDB (2.1), the community library of covers, banners, logos and icons, like Hydra Launcher's artwork
/// picker. SteamGridDB's API needs a key of the user's own (free, from their SteamGridDB profile → Preferences → API): Hydra
/// goes through its own paid server instead, which we do not have. Answers are kept for the session and on disk for a day.
/// </summary>
public static class SteamGridDb
{
    const string Api = "https://www.steamgriddb.com/api/v2/";

    /// <summary>Where users get a key.</summary>
    public const string KeyPage = "https://www.steamgriddb.com/profile/preferences/api";

    public static string? Key => SettingsStore.Current.SteamGridDbKey is { Length: > 0 } k ? k.Trim() : null;

    /// <summary>A key SteamGridDB refused this session: the picker asks for one again until another is pasted (2.1-rc.3 audit).</summary>
    static string? _refused;

    public static bool HasKey => Key is { } k && k != _refused;

    /// <summary>The API's folder for a kind of art.</summary>
    public static string Path(ArtKind kind) => kind switch
    {
        ArtKind.Cover => "grids",
        ArtKind.Banner => "heroes",
        ArtKind.Logo => "logos",
        _ => "icons",
    };

    /// <summary>The key was refused (401/403): the picker asks for it again.</summary>
    public sealed class KeyRefusedException() : Exception("SteamGridDB refused the API key.");

    // Answers of this session by path, shared by every caller (a tab asked twice, or while it is still coming, is asked once),
    // and on disk for a day (2.1: the picker opened again, or another tab, shows at once).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> Answers = new();
    static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);
    const string NotFound = "-none-";

    static string CacheFile(string path)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..20];
        return System.IO.Path.Combine(AppInfo.Ensure(System.IO.Path.Combine(AppInfo.CacheDir, "sgdb")), hash + ".json");
    }

    /// <summary>
    /// A cached GET: the answer of this session, else the copy on disk younger than a day, else asked (once, however many wait).
    /// A failure is not kept: the next call asks again.
    /// </summary>
    static async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        if (Key is null) throw new KeyRefusedException();
        var task = Answers.GetOrAdd(path, static p =>
        {
            var t = FetchTextAsync(p);
            _ = t.ContinueWith(failed => Answers.TryRemove(new KeyValuePair<string, Task<string?>>(p, failed)),
                CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            return t;
        });
        string? text;
        try { text = await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Answers.TryRemove(new KeyValuePair<string, Task<string?>>(path, task));
            throw;
        }
        if (text is null or NotFound) return null;
        var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) { doc.Dispose(); return null; }
        return doc;
    }

    static async Task<string?> FetchTextAsync(string path)
    {
        var file = CacheFile(path);
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < KeepFor)
                return await File.ReadAllTextAsync(file).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn($"SteamGridDB cache: {ex.Message}"); }
        // Not tied to one caller's token: others may wait for the same answer (each stops waiting on its own token).
        var text = await GetTextAsync(path, CancellationToken.None).ConfigureAwait(false) ?? NotFound;
        try
        {
            var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, text).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"SteamGridDB cache: {ex.Message}"); }
        return text;
    }

    /// <summary>The answers kept are forgotten (another key was saved: what the old one could not see may show now).</summary>
    public static void ForgetAnswers()
    {
        Answers.Clear();
        try
        {
            var dir = System.IO.Path.Combine(AppInfo.CacheDir, "sgdb");
            if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f);
        }
        catch (Exception ex) { Log.Warn($"SteamGridDB cache: {ex.Message}"); }
    }

    /// <summary>
    /// Asks for the first page of every kind of art of the game in the background, so the other tabs of the picker show at once.
    /// </summary>
    public static void Prefetch(long gameId)
    {
        foreach (var kind in new[] { ArtKind.Cover, ArtKind.Banner, ArtKind.Logo, ArtKind.Icon })
            _ = AssetsAsync(gameId, kind, 0, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>One request, its answer's text (null: not found). Not cached.</summary>
    static async Task<string?> GetTextAsync(string path, CancellationToken ct, string? key = null)
    {
        key ??= Key;
        if (key is null) throw new KeyRefusedException();
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.Client.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (key == Key) _refused = key;
            throw new KeyRefusedException();
        }
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>Checks a key with one small request. True: it works.</summary>
    public static async Task<bool> CheckKeyAsync(string key, CancellationToken ct = default)
    {
        try
        {
            // A real answer only: "not found" or "success": false is no proof the key works (2.1-rc.3 audit).
            var text = await GetTextAsync("search/autocomplete/" + Uri.EscapeDataString("portal"), ct, key.Trim()).ConfigureAwait(false);
            if (text is null) return false;
            using var doc = JsonDocument.Parse(text);
            return !(doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False);
        }
        catch (KeyRefusedException) { return false; }
    }

    /// <summary>The games whose name matches <paramref name="term"/>.</summary>
    public static async Task<List<SgdbGame>> SearchAsync(string term, CancellationToken ct = default)
    {
        var list = new List<SgdbGame>();
        if (string.IsNullOrWhiteSpace(term)) return list;
        using var doc = await GetAsync("search/autocomplete/" + Uri.EscapeDataString(term.Trim()), ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var g in data.EnumerateArray())
        {
            if (!g.TryGetProperty("id", out var id) || !id.TryGetInt64(out var gid)) continue;
            var name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            int? year = null;
            if (g.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.Number && rd.TryGetInt64(out var unix) && unix > 0)
                year = DateTimeOffset.FromUnixTimeSeconds(unix).Year;
            list.Add(new SgdbGame(gid, name, year));
        }
        return list;
    }

    /// <summary>
    /// The game's SteamGridDB id: the one the user picked before, else by its Steam app id, else the first match of its name.
    /// </summary>
    public static async Task<SgdbGame?> FindAsync(Game game, string? steamAppId, CancellationToken ct = default)
    {
        if (SettingsStore.Current.Games.TryGetValue(game.Id, out var gs) && gs.SgdbGameId is { } picked)
            return new SgdbGame(picked, gs.SgdbGameName ?? game.Title, null);
        if (steamAppId is not null)
        {
            using var doc = await GetAsync("games/steam/" + steamAppId, ct).ConfigureAwait(false);
            if (doc is not null && doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("id", out var id) && id.TryGetInt64(out var gid))
                return new SgdbGame(gid, d.TryGetProperty("name", out var n) ? n.GetString() ?? game.Title : game.Title, null);
        }
        return (await SearchAsync(game.Title, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    /// <summary>One page (50) of a game's art of a kind, the best rated first. Static pictures only (no animations).</summary>
    public static async Task<List<SgdbAsset>> AssetsAsync(long gameId, ArtKind kind, int page = 0, CancellationToken ct = default)
    {
        var query = new List<string> { "nsfw=false", "humor=false", "types=static", $"page={page}" };
        // Covers: the portrait library capsules (the library tiles are 2:3). Icons: PNG, which the list can draw.
        if (kind == ArtKind.Cover) query.Add("dimensions=600x900,342x482,660x930");
        if (kind == ArtKind.Icon) query.Add("mimes=image/png");
        if (kind == ArtKind.Logo) query.Add("mimes=image/png");
        using var doc = await GetAsync($"{Path(kind)}/game/{gameId}?{string.Join('&', query)}", ct).ConfigureAwait(false);
        var list = new List<SgdbAsset>();
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in data.EnumerateArray())
        {
            string? Str(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int Int(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
            if (Str("url") is not { } url || !a.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
            var mime = Str("mime") ?? "";
            bool animated = mime is "image/gif" or "video/webm" || url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
            if (animated) continue;
            string? author = a.TryGetProperty("author", out var au) && au.TryGetProperty("name", out var an) ? an.GetString() : null;
            list.Add(new SgdbAsset(id, url, Str("thumb") ?? url, Int("width"), Int("height"), author, animated));
        }
        return list;
    }

    /// <summary>
    /// Downloads a picture into the data folder's covers\ (a new name each time: the old picture may still be decoded in memory)
    /// and returns its path.
    /// </summary>
    public static async Task<string> DownloadAsync(string gameId, ArtKind kind, string url, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await Http.Client.GetAsync(url, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        // Only a real picture is kept (an error page or a cut download must not become the game's cover, 2.1-rc.3 audit).
        if (!IsPicture(bytes)) throw new InvalidDataException(L.T("The download is not a picture."));
        var ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp")) ext = resp.Content.Headers.ContentType?.MediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };
        var target = ArtOverrides.NewFile(gameId, kind, ext);
        await File.WriteAllBytesAsync(target, bytes, cts.Token).ConfigureAwait(false);
        return target;
    }

    /// <summary>PNG, JPEG or WebP, by the file's first bytes.</summary>
    public static bool IsPicture(ReadOnlySpan<byte> b) =>
        b.Length > 12 && (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            || b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF
            || b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P');
}

/// <summary>
/// The pictures a user chose for a game (cover, banner, logo, icon), kept as copies in the data folder's covers\ and named in
/// its <see cref="GameSettings"/>. Everything that shows a game's art asks here first.
/// </summary>
public static class ArtOverrides
{
    static GameSettings? Settings(string gameId) => SettingsStore.Current.Games.TryGetValue(gameId, out var gs) ? gs : null;

    /// <summary>The user's own picture of that kind for the game, when it is set and still on disk.</summary>
    public static string? Get(string gameId, ArtKind kind)
    {
        var gs = Settings(gameId);
        var path = kind switch
        {
            ArtKind.Cover => gs?.CustomCover,
            ArtKind.Banner => gs?.CustomHero,
            ArtKind.Logo => gs?.CustomLogo,
            _ => gs?.CustomIcon,
        };
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Sets (or with null clears) the game's own picture of that kind; the file it replaces is deleted.</summary>
    public static void Set(string gameId, ArtKind kind, string? path)
    {
        var all = SettingsStore.Current.Games;
        if (!all.TryGetValue(gameId, out var gs))
        {
            if (path is null) return;
            all[gameId] = gs = new GameSettings();
        }
        var old = Get(gameId, kind);
        switch (kind)
        {
            case ArtKind.Cover: gs.CustomCover = path; break;
            case ArtKind.Banner: gs.CustomHero = path; break;
            case ArtKind.Logo: gs.CustomLogo = path; break;
            default: gs.CustomIcon = path; break;
        }
        SettingsStore.Save();
        if (old is not null && !old.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(old); }
            catch (Exception ex) { Log.Warn($"Old {kind} picture of {gameId}: {ex.Message}"); }
        }
        Changed?.Invoke(gameId, kind);
    }

    /// <summary>A game's own picture changed (raised on the thread that set it).</summary>
    public static event Action<string, ArtKind>? Changed;

    /// <summary>A new file name in covers\ for a game's picture of that kind.</summary>
    public static string NewFile(string gameId, ArtKind kind, string ext)
    {
        var safe = string.Concat(gameId.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) || ch == ':' ? '_' : ch));
        var suffix = kind switch { ArtKind.Cover => "", ArtKind.Banner => "-hero", ArtKind.Logo => "-logo", _ => "-icon" };
        return System.IO.Path.Combine(AppInfo.CoversDir, $"{safe}{suffix}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}");
    }

    /// <summary>Copies a picture the user picked from disk in as the game's own picture of that kind.</summary>
    public static string Import(string gameId, ArtKind kind, string file)
    {
        var target = NewFile(gameId, kind, System.IO.Path.GetExtension(file).ToLowerInvariant());
        File.Copy(file, target, overwrite: true);
        Set(gameId, kind, target);
        return target;
    }

    /// <summary>The store's art with the user's own banner and logo laid over it.</summary>
    public static GameArt Apply(Game game, GameArt art)
    {
        var hero = Get(game.Id, ArtKind.Banner);
        var logo = Get(game.Id, ArtKind.Logo);
        if (hero is null && logo is null) return art;
        return new GameArt
        {
            HeroImage = hero ?? art.HeroImage,
            HeaderImage = art.HeaderImage,
            LogoImage = logo ?? art.LogoImage,
            Description = art.Description,
            Developer = art.Developer,
            ReleaseDate = art.ReleaseDate,
            SteamAppId = art.SteamAppId,
        };
    }
}
