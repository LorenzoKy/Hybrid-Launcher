using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Accounts;

/// <summary>
/// A GOG account signed in to Hybrid Launcher: GOG's own sign-in page (the one GOG Galaxy shows) hands back a code, exchanged for
/// tokens. With them Hybrid Launcher lists the account's games and downloads them from GOG's content system (the same files Galaxy
/// installs, generation 2 builds), with no Galaxy involved.
/// </summary>
public static class GogAccount
{
    // GOG Galaxy's public client, the one every third-party GOG launcher uses.
    const string ClientId = "46899977096215655";
    const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    public const string RedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    public static string LoginUrl =>
        $"https://auth.gog.com/auth?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=code&layout=galaxy";

    public static bool SignedIn => AccountTokens.Has(GameStore.Gog);

    /// <summary>The sign-in code, once the sign-in page went on to its "success" address.</summary>
    public static string? CodeFromUrl(string url)
    {
        if (!url.StartsWith("https://embed.gog.com/on_login_success", StringComparison.OrdinalIgnoreCase)) return null;
        var query = new Uri(url).Query.TrimStart('?').Split('&');
        return query.Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "code" && p.Length == 2) is { } kv ? Uri.UnescapeDataString(kv[1]) : null;
    }

    /// <summary>Exchanges the sign-in code for tokens (kept encrypted) and returns the account's name.</summary>
    public static async Task<string> SignInAsync(string code, CancellationToken ct)
    {
        await TokenRequestAsync($"grant_type=authorization_code&code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}", ct);
        var name = "GOG";
        try
        {
            using var doc = JsonDocument.Parse(await GetStringAsync("https://embed.gog.com/userData.json", ct));
            name = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() ?? name : name;
        }
        catch (Exception ex) { Log.Warn($"GOG user data: {ex.Message}"); }
        return name;
    }

    public static void SignOut() => AccountTokens.Delete(GameStore.Gog);

    static async Task TokenRequestAsync(string grant, CancellationToken ct)
    {
        var url = $"https://auth.gog.com/token?client_id={ClientId}&client_secret={ClientSecret}&{grant}";
        using var resp = await Http.Client.GetAsync(url, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(L.F("GOG refused the sign-in ({0}).", (int)resp.StatusCode));
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        AccountTokens.Write(GameStore.Gog, new Dictionary<string, string>
        {
            ["access"] = r.GetProperty("access_token").GetString()!,
            ["refresh"] = r.GetProperty("refresh_token").GetString()!,
            ["expires"] = DateTime.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var e) ? e.GetInt32() - 120 : 3000).ToString("O"),
            ["user"] = r.TryGetProperty("user_id", out var id) ? id.GetString() ?? "" : "",
        });
    }

    static readonly SemaphoreSlim TokenLock = new(1);

    /// <summary>A valid access token, refreshed when it expired (they last an hour).</summary>
    static async Task<string> TokenAsync(CancellationToken ct)
    {
        await TokenLock.WaitAsync(ct);
        try
        {
            var t = AccountTokens.Read(GameStore.Gog) ?? throw new InvalidOperationException(L.T("Sign in to GOG first (Downloads → Accounts)."));
            if (DateTime.TryParse(t.GetValueOrDefault("expires"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var exp) && exp > DateTime.UtcNow)
                return t["access"];
            await TokenRequestAsync($"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(t["refresh"])}", ct);
            return AccountTokens.Read(GameStore.Gog)!["access"];
        }
        finally { TokenLock.Release(); }
    }

    static async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var resp = await Http.Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(url, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct, bool auth = true)
    {
        if (!auth) return await Http.Client.GetByteArrayAsync(url, ct);
        using var resp = await SendAsync(url, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // ---------- library ----------

    /// <summary>Every game of the account that runs on Windows, page by page from GOG's account API.</summary>
    public static async Task<List<OwnedGame>> LibraryAsync(CancellationToken ct)
    {
        var list = new List<OwnedGame>();
        for (int page = 1, pages = 1; page <= pages && page <= 100; page++)
        {
            using var doc = JsonDocument.Parse(await GetStringAsync($"https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={page}", ct));
            var r = doc.RootElement;
            pages = r.TryGetProperty("totalPages", out var tp) ? tp.GetInt32() : 1;
            if (!r.TryGetProperty("products", out var products)) break;
            foreach (var p in products.EnumerateArray())
            {
                if (p.TryGetProperty("worksOn", out var on) && on.TryGetProperty("Windows", out var win) && win.ValueKind == JsonValueKind.False) continue;
                var id = p.GetProperty("id").ToString();
                var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var image = p.TryGetProperty("image", out var img) ? img.GetString() : null;
                var art = image is { Length: > 0 } ? (image.StartsWith("//") ? "https:" + image : image) : null;
                list.Add(new OwnedGame(GameStore.Gog, $"gog:{id}", title.Replace("™", "").Replace("®", "").Trim(),
                    art is null ? null : art + "_glx_vertical_cover.webp", art is null ? null : art + "_392.jpg",
                    $"goggalaxy://openGameView/{id}", p.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } path ? "https://www.gog.com" + path : null));
            }
        }
        return list.DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------- download ----------

    static string GalaxyPath(string hash) => $"{hash[..2]}/{hash[2..4]}/{hash}";

    static async Task<JsonDocument> ZippedJsonAsync(string url, CancellationToken ct, bool auth)
    {
        var bytes = DirectDownloader.Inflate(await GetBytesAsync(url, ct, auth));
        return JsonDocument.Parse(bytes);
    }

    /// <summary>The newest Windows build's version name on GOG (generation 2, main branch), as a download stores it.</summary>
    public static async Task<string?> LatestBuildAsync(string productId, CancellationToken ct)
    {
        using var builds = JsonDocument.Parse(await GetStringAsync($"https://content-system.gog.com/products/{productId}/os/windows/builds?generation=2", ct));
        foreach (var b in builds.RootElement.GetProperty("items").EnumerateArray())
        {
            bool gen2 = b.TryGetProperty("generation", out var g) && g.GetInt32() == 2;
            bool main = !b.TryGetProperty("branch", out var br) || br.ValueKind == JsonValueKind.Null || br.GetString() is null or "";
            if (gen2 && main) return b.TryGetProperty("version_name", out var vn) ? vn.GetString() : null;
        }
        return null;
    }

    /// <summary>
    /// The files of the game's newest Windows build (generation 2): the base game's depots in English or for every language,
    /// each file as its chunks on GOG's CDN; small files packed in a container are cut out of it after.
    /// </summary>
    public static async Task<DownloadPlan> PlanAsync(OwnedGame game, string root, CancellationToken ct)
    {
        var id = game.Id["gog:".Length..];
        using var builds = JsonDocument.Parse(await GetStringAsync($"https://content-system.gog.com/products/{id}/os/windows/builds?generation=2", ct));
        JsonElement? build = null;
        foreach (var b in builds.RootElement.GetProperty("items").EnumerateArray())
        {
            bool gen2 = b.TryGetProperty("generation", out var g) && g.GetInt32() == 2;
            bool main = !b.TryGetProperty("branch", out var br) || br.ValueKind == JsonValueKind.Null || br.GetString() is null or "";
            if (gen2 && main) { build = b; break; }
        }
        if (build is null) throw new InvalidOperationException(L.F("GOG has only an older kind of installer for {0}: download it from gog.com or GOG Galaxy.", game.Name));
        var version = build.Value.TryGetProperty("version_name", out var vn) ? vn.GetString() : null;

        using var manifest = await ZippedJsonAsync(build.Value.GetProperty("link").GetString()!, ct, auth: false);
        var m = manifest.RootElement;
        var baseId = m.TryGetProperty("baseProductId", out var bp) ? bp.ToString() : id;
        var folder = m.TryGetProperty("installDirectory", out var dir) && dir.GetString() is { Length: > 0 } d ? DirectDownloader.FolderName(d) : DirectDownloader.FolderName(game.Name);
        var plan = new DownloadPlan { InstallDir = Path.Combine(root, folder), Version = version, Finish = p => ReadPlayTask(p, baseId) };

        var links = new Dictionary<string, Task<Func<string, string>>>();
        int container = 0;
        foreach (var depot in m.GetProperty("depots").EnumerateArray())
        {
            var product = depot.GetProperty("productId").ToString();
            if (product != baseId) continue; // DLC depots: only the base game
            var langs = depot.TryGetProperty("languages", out var l) ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : ["*"];
            if (!langs.Any(x => x == "*" || x.StartsWith("en", StringComparison.OrdinalIgnoreCase))) continue;
            if (!links.TryGetValue(product, out var link)) links[product] = link = SecureLinkAsync(product, ct);
            var hash = depot.GetProperty("manifest").GetString()!;
            using var dm = await ZippedJsonAsync($"https://cdn.gog.com/content-system/v2/meta/{GalaxyPath(hash)}", ct, auth: false);
            var depotEl = dm.RootElement.GetProperty("depot");
            string? sfc = null;
            if (depotEl.TryGetProperty("smallFilesContainer", out var small) && small.ValueKind == JsonValueKind.Object)
            {
                sfc = $".hl-gog-sfc-{container++}";
                plan.Files.Add(FileOf(sfc, small, link, temporary: true));
            }
            foreach (var item in depotEl.GetProperty("items").EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                var path = item.GetProperty("path").GetString()!.Replace('/', '\\');
                if (type == "DepotDirectory") plan.Files.Add(new PlanFile { Path = path, Directory = true });
                else if (type == "DepotFile")
                {
                    if (sfc is not null && item.TryGetProperty("sfcRef", out var refEl))
                        plan.Files.Add(new PlanFile { Path = path, Size = refEl.GetProperty("size").GetInt64(), Slice = (sfc, refEl.GetProperty("offset").GetInt64()) });
                    else plan.Files.Add(FileOf(path, item, link));
                }
            }
        }
        if (plan.Files.Count == 0) throw new InvalidOperationException(L.F("GOG lists no Windows files in English for {0}.", game.Name));
        return plan;
    }

    static PlanFile FileOf(string path, JsonElement item, Task<Func<string, string>> link, bool temporary = false)
    {
        var chunks = new List<PlanChunk>();
        long offset = 0;
        foreach (var c in item.GetProperty("chunks").EnumerateArray())
        {
            var size = c.GetProperty("size").GetInt32();
            var compressed = c.GetProperty("compressedMd5").GetString()!;
            var md5 = c.GetProperty("md5").GetString()!;
            chunks.Add(new PlanChunk(offset, size, async ct =>
            {
                var url = (await link)(GalaxyPath(compressed));
                var data = DirectDownloader.Inflate(await Http.Client.GetByteArrayAsync(url, ct));
                if (!Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data)).Equals(md5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("GOG chunk checksum mismatch");
                return data;
            }));
            offset += size;
        }
        // The file's own md5 when GOG lists it; a file in one chunk has that chunk's.
        var fileMd5 = item.TryGetProperty("md5", out var fm) && fm.GetString() is { Length: 32 } whole ? whole
            : item.GetProperty("chunks").GetArrayLength() == 1 ? item.GetProperty("chunks")[0].GetProperty("md5").GetString() : null;
        return new PlanFile { Path = path, Size = offset, Chunks = chunks, Temporary = temporary, Md5 = fileMd5 };
    }

    /// <summary>The CDN address maker for a product's files: GOG signs a base address per product (it expires after hours).</summary>
    static async Task<Func<string, string>> SecureLinkAsync(string product, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync($"https://content-system.gog.com/products/{product}/secure_link?generation=2&_version=2&path=/", ct));
        var urls = doc.RootElement.GetProperty("urls").EnumerateArray()
            .OrderByDescending(u => u.TryGetProperty("priority", out var p) ? p.GetInt32() : 0).ToList();
        if (urls.Count == 0) throw new InvalidOperationException("GOG gave no download address.");
        var format = urls[0].GetProperty("url_format").GetString()!;
        var parameters = urls[0].GetProperty("parameters").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
        return chunkPath =>
        {
            var sb = new StringBuilder(format);
            foreach (var (key, value) in parameters)
                sb.Replace("{" + key + "}", key == "path" ? value.TrimEnd('/') + "/" + chunkPath : value);
            return sb.ToString();
        };
    }

    /// <summary>The game's own launch entry, from the goggame-&lt;id&gt;.info file that comes with every GOG game.</summary>
    static void ReadPlayTask(DownloadPlan plan, string productId)
    {
        try
        {
            var info = Path.Combine(plan.InstallDir, $"goggame-{productId}.info");
            if (!File.Exists(info)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(info));
            if (!doc.RootElement.TryGetProperty("playTasks", out var tasks)) return;
            var list = tasks.EnumerateArray().Where(t => t.TryGetProperty("type", out var ty) && ty.GetString() == "FileTask").ToList();
            var task = list.FirstOrDefault(t => t.TryGetProperty("isPrimary", out var p) && p.GetBoolean());
            if (task.ValueKind != JsonValueKind.Object) task = list.FirstOrDefault();
            if (task.ValueKind != JsonValueKind.Object) return;
            plan.Exe = task.GetProperty("path").GetString()?.Replace('/', '\\');
            plan.Args = task.TryGetProperty("arguments", out var a) ? a.GetString() : null;
            plan.WorkingDir = task.TryGetProperty("workingDir", out var w) ? w.GetString()?.Replace('/', '\\') : null;
        }
        catch (Exception ex) { Log.Warn($"GOG play task: {ex.Message}"); }
    }
}
