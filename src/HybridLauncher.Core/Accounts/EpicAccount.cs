using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Accounts;

/// <summary>
/// An Epic Games account signed in to Hybrid Launcher: Epic's own sign-in page hands back an authorization code, exchanged for tokens
/// (the launcher's public client). With them Hybrid Launcher lists the account's library and downloads games from Epic's CDN by their
/// build manifest, as the Epic Games Launcher does; games that check the account at start get a one-time exchange code.
/// </summary>
public static class EpicAccount
{
    const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    const string ClientSecret = "daafbccc737745039dffe53d94fc76cf";
    const string Oauth = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth";

    /// <summary>The sign-in page; once signed in it goes to <see cref="RedirectPrefix"/>, a page showing the code as JSON.</summary>
    public static string LoginUrl => "https://www.epicgames.com/id/login?redirectUrl=" +
        Uri.EscapeDataString($"https://www.epicgames.com/id/api/redirect?clientId={ClientId}&responseType=code");

    public const string RedirectPrefix = "https://www.epicgames.com/id/api/redirect";

    public static bool SignedIn => AccountTokens.Has(GameStore.Epic);

    /// <summary>The code in the redirect page's JSON ({"authorizationCode": "…"}).</summary>
    public static string? CodeFromPage(string text)
    {
        try
        {
            // ExecuteScript hands the page text back as a JSON string: unwrap it first.
            if (text.StartsWith('"')) text = JsonSerializer.Deserialize(text, AccountJson.Default.String) ?? "";
            // The browser shows JSON in its own viewer ("Pretty-print" and the like around it): the page text is not JSON
            // itself any more, so the code is picked out of it.
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"authorizationCode\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    public static async Task<string> SignInAsync(string code, CancellationToken ct)
    {
        await TokenRequestAsync($"grant_type=authorization_code&code={Uri.EscapeDataString(code)}&token_type=eg1", ct);
        return AccountTokens.Read(GameStore.Epic)?.GetValueOrDefault("name") ?? "Epic Games";
    }

    public static void SignOut() => AccountTokens.Delete(GameStore.Epic);

    static async Task TokenRequestAsync(string form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Oauth}/token")
        {
            Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}")));
        using var resp = await Http.Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(L.F("Epic refused the sign-in ({0}).", (int)resp.StatusCode));
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        AccountTokens.Write(GameStore.Epic, new Dictionary<string, string>
        {
            ["access"] = r.GetProperty("access_token").GetString()!,
            ["refresh"] = r.GetProperty("refresh_token").GetString()!,
            ["expires"] = DateTime.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var e) ? e.GetInt32() - 120 : 3000).ToString("O"),
            ["account"] = r.TryGetProperty("account_id", out var a) ? a.GetString() ?? "" : "",
            ["name"] = r.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
        });
    }

    static readonly SemaphoreSlim TokenLock = new(1);

    static async Task<Dictionary<string, string>> TokensAsync(CancellationToken ct)
    {
        await TokenLock.WaitAsync(ct);
        try
        {
            var t = AccountTokens.Read(GameStore.Epic) ?? throw new InvalidOperationException(L.T("Sign in to Epic Games first (Downloads → Accounts)."));
            if (DateTime.TryParse(t.GetValueOrDefault("expires"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var exp) && exp > DateTime.UtcNow) return t;
            await TokenRequestAsync($"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(t["refresh"])}&token_type=eg1", ct);
            return AccountTokens.Read(GameStore.Epic)!;
        }
        finally { TokenLock.Release(); }
    }

    static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await TokensAsync(ct))["access"]);
        using var resp = await Http.Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// The arguments Epic games expect from the launcher: a one-time exchange code (the game signs in to Epic Online Services
    /// with it), the app and the account. Games that do not check simply ignore them.
    /// </summary>
    public static async Task<string> LaunchArgsAsync(string appName, CancellationToken ct)
    {
        var t = await TokensAsync(ct);
        using var doc = JsonDocument.Parse(await GetStringAsync($"{Oauth}/exchange", ct));
        var code = doc.RootElement.GetProperty("code").GetString();
        return $"-AUTH_LOGIN=unused -AUTH_PASSWORD={code} -AUTH_TYPE=exchangecode -epicapp={appName} -epicenv=Prod -EpicPortal " +
               $"-epicusername=\"{t.GetValueOrDefault("name")}\" -epicuserid={t.GetValueOrDefault("account")} -epiclocale=en-US";
    }

    // ---------- library ----------

    sealed record CatalogItem(string Title, string? Tall, string? Wide, bool Game);

    public static async Task<List<OwnedGame>> LibraryAsync(CancellationToken ct)
    {
        var records = new List<(string Ns, string Cat, string App)>();
        string? cursor = null;
        for (int i = 0; i < 50; i++)
        {
            var url = "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var doc = JsonDocument.Parse(await GetStringAsync(url, ct));
            foreach (var r in doc.RootElement.GetProperty("records").EnumerateArray())
            {
                string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (S("namespace") is { } ns && ns != "ue" && S("catalogItemId") is { } cat && S("appName") is { } app && S("sandboxType") != "PRIVATE")
                    records.Add((ns, cat, app));
            }
            cursor = doc.RootElement.TryGetProperty("responseMetadata", out var meta) && meta.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
            if (cursor is null) break;
        }

        // The names and art come from the catalog, one item at a time (cached for a month: they hardly change). When the catalog
        // does not answer for an item, the Epic Games Launcher's own copy on this PC names it, so an owned game is never left out.
        var local = OwnedGames.EpicLocal().Games.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var list = new List<OwnedGame>();
        using var gate = new SemaphoreSlim(8);
        var tasks = records.DistinctBy(r => r.App).Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var item = await CatalogAsync(r.Ns, r.Cat, ct);
                if (item is null && local.TryGetValue($"epic:{r.App}", out var known))
                    item = new CatalogItem(known.Name, known.Cover, known.Wide, true);
                if (item is not { Game: true }) return;
                lock (list) list.Add(new OwnedGame(GameStore.Epic, $"epic:{r.App}", item.Title, item.Tall, item.Wide,
                    $"com.epicgames.launcher://apps/{r.Ns}%3A{r.Cat}%3A{r.App}?action=install", $"https://store.epicgames.com/browse?q={Uri.EscapeDataString(item.Title)}")
                    { Namespace = r.Ns, CatalogItemId = r.Cat });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Epic catalog {r.App}: {ex.Message}"); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// A catalog item's JSON, cached for a month. Epic's catalog answers only signed-in requests (401 without the account's
    /// token), so it goes through <see cref="GetStringAsync"/>; a failure falls back to the cached copy, or null.
    /// </summary>
    static async Task<string?> CatalogJsonAsync(string ns, string cat, CancellationToken ct)
    {
        var cacheFile = Path.Combine(AppInfo.CacheDir, "epic", $"catalog-{cat}.json");
        if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromDays(30))
            return await File.ReadAllTextAsync(cacheFile, ct);
        try
        {
            var json = await GetStringAsync(
                $"https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/{ns}/bulk/items?id={cat}&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en-US", ct);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var tmp = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, cacheFile, overwrite: true);
            return json;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Epic catalog {cat}: {ex.Message}");
            return File.Exists(cacheFile) ? await File.ReadAllTextAsync(cacheFile, ct) : null;
        }
    }

    static async Task<CatalogItem?> CatalogAsync(string ns, string cat, CancellationToken ct)
    {
        var json = await CatalogJsonAsync(ns, cat, ct);
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(cat, out var item)) return null;
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool dlc = item.TryGetProperty("mainGameItem", out var main) && main.ValueKind == JsonValueKind.Object;
        bool game = !dlc && (!item.TryGetProperty("categories", out var cats) || cats.EnumerateArray().Any(c => S(c, "path") is "games" or "applications"));
        string? Image(params string[] types) => item.TryGetProperty("keyImages", out var imgs)
            ? types.Select(t => imgs.EnumerateArray().Where(i => S(i, "type") == t).Select(i => S(i, "url")).FirstOrDefault()).FirstOrDefault(u => u is not null) : null;
        var title = (S(item, "title") ?? cat).Replace("™", "").Replace("®", "").Trim();
        return new CatalogItem(title, Image("DieselGameBoxTall", "OfferImageTall"), Image("DieselGameBox", "OfferImageWide"), game);
    }

    /// <summary>The folder name the Epic Games Launcher installs the game in ("customAttributes" → "FolderName" of its catalog item).</summary>
    static async Task<string?> FolderNameAsync(string ns, string cat, CancellationToken ct)
    {
        try
        {
            var json = await CatalogJsonAsync(ns, cat, ct);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(cat, out var item) && item.TryGetProperty("customAttributes", out var attrs)
                && attrs.TryGetProperty("FolderName", out var folder) && folder.TryGetProperty("value", out var v) && v.GetString() is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Epic folder name: {ex.Message}"); return null; }
    }

    /// <summary>The game's current live build version on Epic (what Hybrid Launcher stored when it downloaded the game).</summary>
    public static async Task<string?> LatestBuildAsync(OwnedGame game, CancellationToken ct)
    {
        if (game.Namespace is null || game.CatalogItemId is null) return null;
        var app = game.Id["epic:".Length..];
        using var assets = JsonDocument.Parse(await GetStringAsync(
            $"https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/v2/platform/Windows/namespace/{game.Namespace}/catalogItem/{game.CatalogItemId}/app/{app}/label/Live", ct));
        var element = assets.RootElement.GetProperty("elements")[0];
        return element.TryGetProperty("buildVersion", out var bv) ? bv.GetString() : null;
    }

    // ---------- download ----------

    public static async Task<DownloadPlan> PlanAsync(OwnedGame game, string root, CancellationToken ct)
    {
        var app = game.Id["epic:".Length..];
        if (game.Namespace is null || game.CatalogItemId is null) throw new InvalidOperationException(L.T("Refresh the Epic library first (sign in to Epic Games)."));
        using var assets = JsonDocument.Parse(await GetStringAsync(
            $"https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/v2/platform/Windows/namespace/{game.Namespace}/catalogItem/{game.CatalogItemId}/app/{app}/label/Live", ct));
        var element = assets.RootElement.GetProperty("elements")[0];
        var version = element.TryGetProperty("buildVersion", out var bv) ? bv.GetString() : null;
        byte[]? data = null;
        string? baseUrl = null;
        foreach (var m in element.GetProperty("manifests").EnumerateArray())
        {
            var uri = m.GetProperty("uri").GetString()!;
            var query = m.TryGetProperty("queryParams", out var q) && q.GetArrayLength() > 0
                ? "?" + string.Join("&", q.EnumerateArray().Select(p => $"{p.GetProperty("name").GetString()}={p.GetProperty("value").GetString()}")) : "";
            try
            {
                data = await Http.Client.GetByteArrayAsync(uri + query, ct);
                baseUrl = uri[..uri.LastIndexOf('/')];
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Epic manifest {uri}: {ex.Message}"); }
        }
        if (data is null || baseUrl is null) throw new InvalidOperationException(L.F("Epic did not send the manifest of {0}.", game.Name));
        var manifest = EpicManifest.Parse(data);
        var plan = new DownloadPlan
        {
            // The folder the Epic Games Launcher would make: the catalog's FolderName, else the app's name.
            InstallDir = Path.Combine(root, DirectDownloader.FolderName(await FolderNameAsync(game.Namespace, game.CatalogItemId, ct) ?? app)),
            Version = version ?? manifest.BuildVersion,
            Exe = manifest.LaunchExe.Replace('/', '\\'),
            Args = manifest.LaunchCommand,
        };
        var chunkDir = manifest.FeatureLevel >= 15 ? "ChunksV4" : manifest.FeatureLevel >= 6 ? "ChunksV3" : manifest.FeatureLevel >= 3 ? "ChunksV2" : "Chunks";
        foreach (var f in manifest.Files)
        {
            var chunks = new List<PlanChunk>();
            long offset = 0;
            foreach (var part in f.Parts)
            {
                var info = manifest.Chunks[part.Guid];
                var url = $"{baseUrl}/{chunkDir}/{info.Group:D2}/{info.Hash:X16}_{part.Guid}.chunk";
                var (start, size) = (part.Offset, part.Size);
                chunks.Add(new PlanChunk(offset, size, async ct2 =>
                {
                    var whole = EpicManifest.ReadChunk(await Http.Client.GetByteArrayAsync(url, ct2));
                    return whole.AsSpan(start, size).ToArray();
                }));
                offset += size;
            }
            plan.Files.Add(new PlanFile { Path = f.Name.Replace('/', '\\'), Size = offset, Chunks = chunks, Sha1 = f.Sha1 });
        }
        return plan;
    }
}

/// <summary>
/// Epic's binary build manifest: the build's files, each as parts of chunks on the CDN (the format the Epic Games Launcher and
/// its open-source re-implementations read). JSON manifests of very old builds are not supported.
/// </summary>
public sealed class EpicManifest
{
    public sealed record Chunk(string Guid, ulong Hash, byte Group);
    public sealed record Part(string Guid, int Offset, int Size);
    public sealed record FileEntry(string Name, List<Part> Parts, string? Sha1 = null);

    public int FeatureLevel;
    public string BuildVersion = "", LaunchExe = "", LaunchCommand = "";
    public Dictionary<string, Chunk> Chunks = [];
    public List<FileEntry> Files = [];

    const uint Magic = 0x44BEC00C, ChunkMagic = 0xB1FE3AA2;

    public static EpicManifest Parse(byte[] raw)
    {
        if (raw.Length > 0 && raw[0] == '{') throw new NotSupportedException("This game's Epic build uses an old JSON manifest that Hybrid Launcher cannot read: install it from the Epic Games Launcher.");
        var r = new BinaryReader(new MemoryStream(raw));
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not an Epic manifest.");
        int headerSize = r.ReadInt32();
        r.ReadInt32(); // size uncompressed
        int compressedSize = r.ReadInt32();
        r.ReadBytes(20); // sha1
        byte storedAs = r.ReadByte();
        var body = raw.AsSpan(headerSize).ToArray();
        if ((storedAs & 1) != 0) body = DirectDownloader.Inflate(body[..Math.Min(compressedSize, body.Length)]);
        if ((storedAs & 2) != 0) throw new NotSupportedException("Encrypted Epic manifest.");

        var m = new EpicManifest();
        var b = new BinaryReader(new MemoryStream(body));

        // Meta
        long start = b.BaseStream.Position;
        uint size = b.ReadUInt32();
        byte dataVersion = b.ReadByte();
        m.FeatureLevel = b.ReadInt32();
        b.ReadByte(); // is file data
        b.ReadUInt32(); // app id
        ReadString(b); // app name
        m.BuildVersion = ReadString(b);
        m.LaunchExe = ReadString(b);
        m.LaunchCommand = ReadString(b);
        b.BaseStream.Position = start + size;

        // Chunk data list
        start = b.BaseStream.Position;
        size = b.ReadUInt32();
        b.ReadByte(); // version
        int count = b.ReadInt32();
        var guids = new string[count];
        for (int i = 0; i < count; i++) guids[i] = Guid(b);
        var hashes = new ulong[count];
        for (int i = 0; i < count; i++) hashes[i] = b.ReadUInt64();
        var sha1s = new string[count];
        for (int i = 0; i < count; i++) sha1s[i] = Convert.ToHexString(b.ReadBytes(20));
        var groups = new byte[count];
        for (int i = 0; i < count; i++) groups[i] = b.ReadByte();
        for (int i = 0; i < count; i++) m.Chunks[guids[i]] = new Chunk(guids[i], hashes[i], groups[i]);
        b.BaseStream.Position = start + size;

        // File manifest list
        start = b.BaseStream.Position;
        size = b.ReadUInt32();
        b.ReadByte(); // version
        count = b.ReadInt32();
        var names = new string[count];
        for (int i = 0; i < count; i++) names[i] = ReadString(b);
        for (int i = 0; i < count; i++) ReadString(b); // symlink targets
        b.BaseStream.Position += 20L * count; // sha1s
        b.BaseStream.Position += count; // flags
        for (int i = 0; i < count; i++)
        {
            int tags = b.ReadInt32();
            for (int t = 0; t < tags; t++) ReadString(b);
        }
        for (int i = 0; i < count; i++)
        {
            int parts = b.ReadInt32();
            var list = new List<Part>(parts);
            for (int p = 0; p < parts; p++)
            {
                long partStart = b.BaseStream.Position;
                uint partSize = b.ReadUInt32();
                var guid = Guid(b);
                int offset = b.ReadInt32();
                int length = b.ReadInt32();
                list.Add(new Part(guid, offset, length));
                b.BaseStream.Position = partStart + partSize;
            }
            m.Files.Add(new FileEntry(names[i], list, sha1s[i]));
        }
        b.BaseStream.Position = start + size;
        _ = dataVersion;
        return m;
    }

    static string Guid(BinaryReader b) => $"{b.ReadUInt32():X8}{b.ReadUInt32():X8}{b.ReadUInt32():X8}{b.ReadUInt32():X8}";

    /// <summary>An FString: a length, then ASCII (positive) or UTF-16 (negative), ending in a null.</summary>
    static string ReadString(BinaryReader b)
    {
        int length = b.ReadInt32();
        if (length == 0) return "";
        if (length < 0)
        {
            var bytes = b.ReadBytes(-length * 2);
            return Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2);
        }
        var ascii = b.ReadBytes(length);
        return Encoding.ASCII.GetString(ascii, 0, ascii.Length - 1);
    }

    /// <summary>A chunk file from the CDN: its header, then the data (zlib-compressed when flagged).</summary>
    public static byte[] ReadChunk(byte[] raw)
    {
        var r = new BinaryReader(new MemoryStream(raw));
        if (r.ReadUInt32() != ChunkMagic) throw new InvalidDataException("Not an Epic chunk.");
        r.ReadUInt32(); // header version
        int headerSize = r.ReadInt32();
        int compressedSize = r.ReadInt32();
        r.BaseStream.Position += 16 + 8; // guid, hash
        byte storedAs = r.ReadByte();
        var data = raw.AsSpan(headerSize, Math.Min(compressedSize, raw.Length - headerSize)).ToArray();
        return (storedAs & 1) != 0 ? DirectDownloader.Inflate(data) : data;
    }
}
