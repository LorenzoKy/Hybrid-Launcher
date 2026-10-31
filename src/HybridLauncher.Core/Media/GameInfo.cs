using System.Text.Json;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

public sealed class GameArt
{
    public string? HeroImage { get; init; }
    public string? HeaderImage { get; init; }

    /// <summary>The game's title logo (transparent PNG), drawn on the banner like Steam's library does.</summary>
    public string? LogoImage { get; init; }
    public string? Description { get; init; }
    public string? Developer { get; init; }
    public string? ReleaseDate { get; init; }
    public string? SteamAppId { get; init; }
}

/// <summary>Banner and overview from the public Steam store API (also used to find art for non-Steam games by name).</summary>
public static class GameInfo
{
    const string AssetBase = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/";

    // The library grid asks for many covers at once: a few downloads at a time.
    static readonly SemaphoreSlim CoverGate = new(4, 4);

    /// <summary>
    /// The portrait cover (Steam's 600x900 library capsule) for the library grid, or the landscape header when the game has
    /// none. Non-Steam games are looked up by name. Null when nothing is found.
    /// </summary>
    public static async Task<string?> GetCoverAsync(Game game, CancellationToken ct = default)
    {
        await CoverGate.WaitAsync(ct);
        try
        {
            var appId = await SteamAppIdAsync(game, ct);
            if (appId is null) return null;
            var assets = await AssetsAsync(appId, ct);
            var cover = assets is { Format: { } f, Cover: { } c } ? AssetRoot + f.Replace("${FILENAME}", c) : $"{AssetBase}{appId}/library_600x900.jpg";
            return await CacheImageAsync(cover, $"{appId}-cover.jpg", ct)
                ?? await CacheImageAsync($"{AssetBase}{appId}/header.jpg", $"{appId}-header.jpg", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Cover for {game.Name} failed: {ex.Message}");
            return null;
        }
        finally
        {
            CoverGate.Release();
        }
    }

    public static async Task<GameArt> GetAsync(Game game, CancellationToken ct = default)
    {
        try
        {
            var appId = await SteamAppIdAsync(game, ct);
            if (appId is null) return new GameArt();

            var json = await Http.GetCachedAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english",
                Path.Combine("store", $"app-{appId}.json"), TimeSpan.FromDays(14), ct: ct);

            string? desc = null, dev = null, date = null, header = null, shot = null;
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(appId, out var app) && app.TryGetProperty("data", out var data))
                {
                    desc = data.TryGetProperty("short_description", out var d) ? System.Net.WebUtility.HtmlDecode(d.GetString()) : null;
                    header = data.TryGetProperty("header_image", out var h) ? h.GetString() : null;
                    if (data.TryGetProperty("developers", out var devs) && devs.ValueKind == JsonValueKind.Array)
                        dev = string.Join(", ", devs.EnumerateArray().Select(x => x.GetString()));
                    if (data.TryGetProperty("release_date", out var rd) && rd.TryGetProperty("date", out var dd)) date = dd.GetString();
                    // The first screenshot (1920 px): the banner of a game with no library hero, instead of the 460 px header.
                    if (data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
                        shot = shots.EnumerateArray().Select(x => x.TryGetProperty("path_full", out var f) ? f.GetString() : null).FirstOrDefault(x => x is not null);
                }
            }

            // Newer games keep their art under a hashed folder the fixed address does not reach: the store's asset list says where.
            var assets = await AssetsAsync(appId, ct);
            string Asset(string file) => assets?.Format is { } f ? AssetRoot + f.Replace("${FILENAME}", file) : $"{AssetBase}{appId}/{file}";
            // Listed by the store: its own file; not listed (or no list): the old fixed name, which most older games still have.
            var logoTask = CacheImageAsync(Asset(assets?.Logo ?? "logo.png"), $"{appId}-logo.png", ct);
            var hero = await CacheImageAsync(Asset(assets?.Hero ?? "library_hero.jpg"), $"{appId}-hero.jpg", ct)
                    ?? (shot is not null ? await CacheImageAsync(shot, $"{appId}-shot.jpg", ct) : null)
                    ?? await CacheImageAsync(header ?? $"{AssetBase}{appId}/header.jpg", $"{appId}-header.jpg", ct);
            var logo = await logoTask;

            return new GameArt { HeroImage = hero, HeaderImage = header, LogoImage = logo, Description = desc, Developer = dev, ReleaseDate = date, SteamAppId = appId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Art for {game.Name} failed: {ex.Message}");
            return new GameArt();
        }
    }

    const string AssetRoot = "https://shared.fastly.steamstatic.com/store_item_assets/";

    sealed record StoreAssets(string? Format, string? Hero, string? Logo, string? Cover);

    /// <summary>The store's own list of a game's art (IStoreBrowseService): file names and the folder they are in. Null when unknown.</summary>
    static async Task<StoreAssets?> AssetsAsync(string appId, CancellationToken ct)
    {
        try
        {
            var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}";
            var json = await Http.GetCachedAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input),
                Path.Combine("store", $"assets-{appId}.json"), TimeSpan.FromDays(14), ct: ct);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("store_items", out var items) || items.GetArrayLength() == 0
                || !items[0].TryGetProperty("assets", out var a)) return null;
            string? Get(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new StoreAssets(Get("asset_url_format"), Get("library_hero_2x") ?? Get("library_hero"), Get("library_logo_2x") ?? Get("library_logo"),
                Get("library_capsule_2x") ?? Get("library_capsule"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Store assets of {appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The Steam app id behind a game's page: the page the user picked (⋯ → Choose the game's page), the store's own id,
    /// else a search by its name.
    /// </summary>
    public static async Task<string?> SteamAppIdAsync(Game game, CancellationToken ct)
    {
        if (GameNames.PickedAppId(game) is { } picked) return picked.Length > 0 ? picked : null;
        return game.SteamAppId ?? game.EmulatorAppId ?? await FindSteamAppIdAsync(GameNames.LookupName(game), ct);
    }

    /// <summary>The Steam app id of a game by its name; then by its plain name ("… Complete Edition - Amazon Prime" left out).</summary>
    public static async Task<string?> FindSteamAppIdAsync(string name, CancellationToken ct) =>
        await FindSteamAppIdCoreAsync(name, ct) ?? (GameNames.Plain(name) is { } plain ? await FindSteamAppIdCoreAsync(plain, ct) : null);

    /// <summary>Steam store search: the games it lists for <paramref name="term"/> (id, name, small capsule picture).</summary>
    public static async Task<List<(string Id, string Name, string? Image)>> SearchStoreAsync(string term, CancellationToken ct)
    {
        var list = new List<(string, string, string?)>();
        var json = await StoreSearchAsync(GameNames.Clean(term), ct);
        if (json is null) return list;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return list;
        foreach (var item in items.EnumerateArray())
            list.Add((item.GetProperty("id").ToString(), item.GetProperty("name").GetString() ?? "", item.TryGetProperty("tiny_image", out var img) ? img.GetString() : null));
        return list;
    }

    static Task<string?> StoreSearchAsync(string name, CancellationToken ct) =>
        Http.GetCachedAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US",
            Path.Combine("store", $"search-{OptiNameHash(ExeLocator.Normalize(name))}.json"), TimeSpan.FromDays(14), ct: ct);

    /// <summary>
    /// The store search's hit with exactly this name, else one that is the same game in another edition ("Control" →
    /// "CONTROL Ultimate Edition"). Never a game whose name only starts the same ("CONTROL Resonant").
    /// </summary>
    static async Task<string?> FindSteamAppIdCoreAsync(string name, CancellationToken ct)
    {
        name = GameNames.Clean(name);
        var key = ExeLocator.Normalize(name);
        if (key.Length < 2) return null;
        var json = await StoreSearchAsync(name, ct);
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return null;
        string? edition = null, remaster = null;
        foreach (var item in items.EnumerateArray())
        {
            var itemName = item.GetProperty("name").GetString() ?? "";
            var id = item.GetProperty("id").ToString();
            if (ExeLocator.Normalize(itemName) == key) return id;
            if (edition is null && GameNames.SameGame(name, itemName)) edition = id;
            if (remaster is null && GameNames.IsRemasterOf(itemName, name)) remaster = id;
        }
        // A folder named "Yakuza 5" holding "Yakuza 5 Remastered": the remaster is a game of its own, with its own pages, and
        // the one on this PC when the store has no other.
        return edition ?? remaster;
    }

    /// <summary>Downloads an image once into the image cache. Returns the local path, or null if it does not exist.</summary>
    public static async Task<string?> CacheImageAsync(string url, string fileName, CancellationToken ct)
    {
        var path = Path.Combine(AppInfo.ImageCacheDir, fileName);
        var missing = path + ".missing";
        if (File.Exists(path)) { CacheJanitor.Touch(path); return path; }
        if (File.Exists(missing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missing) < TimeSpan.FromDays(7)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await Http.Client.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true)
            {
                await File.WriteAllTextAsync(missing, "", ct);
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            // The banner and the library grid can fetch the same image at once: each writes its own temp file.
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Image {url} failed: {ex.Message}");
            return null;
        }
    }

    public static string PcGamingWikiUrl(Game game, string? steamAppId) => steamAppId is not null
        ? $"https://www.pcgamingwiki.com/api/appid.php?appid={steamAppId}"
        : $"https://www.pcgamingwiki.com/w/index.php?search={Uri.EscapeDataString(game.Name)}";

    static string OptiNameHash(string s) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];
}
