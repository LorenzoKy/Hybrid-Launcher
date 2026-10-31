using System.Net;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Core.Media;

/// <summary>HowLongToBeat's times for a game, in hours (0 = no data for that style).</summary>
public sealed record HltbTimes(int GameId, string Name, double Main, double MainExtra, double Completionist, double AllStyles, int Submissions)
{
    public string Url => $"https://howlongtobeat.com/game/{GameId}";
}

/// <summary>
/// HowLongToBeat has no public API: Hybrid Launcher asks the same search endpoint its website uses (a short-lived token from
/// /api/search/site/init, then a POST). Results are cached for a month; any change on their side only hides the card.
/// </summary>
public static class HowLongToBeat
{
    const string Site = "https://howlongtobeat.com";

    // The search token is tied to the user agent, so both calls send the same browser-like one.
    const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        CookieContainer = new CookieContainer(),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    }) { Timeout = TimeSpan.FromSeconds(20) };

    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<HltbTimes?> GetAsync(string gameName, CancellationToken ct = default)
    {
        var key = ExeLocator.Normalize(gameName);
        if (key.Length < 2) return null;
        var dir = Path.Combine(AppInfo.CacheDir, "hltb");
        var file = Path.Combine(dir, $"{Pcgw.Hash(key)}.json");
        var none = file + ".none";
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(30))
                return JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, ct), HltbJson.Default.HltbTimes);
            if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(5)) return null;

            await Gate.WaitAsync(ct);
            try
            {
                var found = await SearchAsync(gameName, key, ct);
                Directory.CreateDirectory(dir);
                if (found is null) await File.WriteAllTextAsync(none, "", ct);
                else await File.WriteAllTextAsync(file, JsonSerializer.Serialize(found, HltbJson.Default.HltbTimes), ct);
                return found;
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"HowLongToBeat for {gameName}: {ex.Message}");
            try { return File.Exists(file) ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, ct), HltbJson.Default.HltbTimes) : null; }
            catch { return null; }
        }
    }

    static async Task<HltbTimes?> SearchAsync(string gameName, string key, CancellationToken ct)
    {
        using var init = new HttpRequestMessage(HttpMethod.Get, $"{Site}/api/search/site/init?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        Browser(init);
        using var initResp = await Client.SendAsync(init, ct);
        initResp.EnsureSuccessStatusCode();
        using var tokenDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync(ct));
        var token = tokenDoc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("no search token");

        // Trademark signs and edition words make the site's word search miss.
        var terms = new string(gameName.Where(c => c is not ('™' or '®' or '©')).ToArray())
            .Split([' ', ':', '-', '–'], StringSplitOptions.RemoveEmptyEntries);
        // Written by hand: the trimmed release build has no reflection-based JSON (not even for JsonNode values).
        var buffer = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("searchType", "games");
            w.WriteStartArray("searchTerms");
            foreach (var term in terms) w.WriteStringValue(term);
            w.WriteEndArray();
            w.WriteNumber("searchPage", 1);
            w.WriteNumber("size", 20);
            w.WriteStartObject("searchOptions");
            w.WriteStartObject("games");
            w.WriteNumber("userId", 0);
            w.WriteString("platform", "");
            w.WriteString("sortCategory", "popular");
            w.WriteString("rangeCategory", "main");
            w.WriteStartObject("rangeTime"); w.WriteNull("min"); w.WriteNull("max"); w.WriteEndObject();
            w.WriteStartObject("gameplay"); w.WriteString("perspective", ""); w.WriteString("flow", ""); w.WriteString("genre", ""); w.WriteString("difficulty", ""); w.WriteEndObject();
            w.WriteStartObject("rangeYear"); w.WriteString("min", ""); w.WriteString("max", ""); w.WriteEndObject();
            w.WriteString("modifier", "");
            w.WriteEndObject();
            w.WriteStartObject("users"); w.WriteString("sortCategory", "postcount"); w.WriteEndObject();
            w.WriteStartObject("lists"); w.WriteString("sortCategory", "follows"); w.WriteEndObject();
            w.WriteString("filter", "");
            w.WriteNumber("sort", 0);
            w.WriteNumber("randomizer", 0);
            w.WriteEndObject();
            w.WriteBoolean("useCache", true);
            w.WriteEndObject();
        }
        var body = Encoding.UTF8.GetString(buffer.ToArray());
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Site}/api/search/site") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        Browser(req);
        req.Headers.Add("Origin", Site);
        req.Headers.Add("x-auth-token", token);
        using var resp = await Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        JsonElement? exact = null, loose = null;
        foreach (var item in data.EnumerateArray())
        {
            var name = item.TryGetProperty("game_name", out var n) ? n.GetString() ?? "" : "";
            var alias = item.TryGetProperty("game_alias", out var a) ? a.GetString() ?? "" : "";
            var nk = ExeLocator.Normalize(name);
            if (nk == key || alias.Split(',').Any(x => ExeLocator.Normalize(x) == key)) { exact = item; break; }
            if (loose is null && nk.Length >= 4 && (nk.StartsWith(key) || key.StartsWith(nk))) loose = item;
        }
        var hit = exact ?? loose;
        if (hit is not { } h) return null;

        double Hours(string field) => h.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDouble() / 3600.0, 1) : 0;
        int Int(string field) => h.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new HltbTimes(Int("game_id"), h.GetProperty("game_name").GetString() ?? gameName,
            Hours("comp_main"), Hours("comp_plus"), Hours("comp_100"), Hours("comp_all"), Int("count_comp"));
    }

    static void Browser(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
        req.Headers.Referrer = new Uri(Site + "/");
        req.Headers.Accept.ParseAdd("application/json, text/plain, */*");
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(HltbTimes))]
internal partial class HltbJson : System.Text.Json.Serialization.JsonSerializerContext;
