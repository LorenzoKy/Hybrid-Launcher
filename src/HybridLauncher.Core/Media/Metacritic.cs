using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

/// <summary>
/// A game's scores on Metacritic: the Metascore (critics, 0-100) of the PC version when it has one (else of the main platform),
/// and the user score (0-10). Counts and Metacritic's own verdict ("Universal acclaim"...) come with them.
/// </summary>
public sealed record MetacriticScores(
    string Slug, string Title, int? Metascore, int CriticReviews, string CriticVerdict, string? MetascorePlatform,
    double? UserScore, int UserRatings, string UserVerdict, bool MustPlay)
{
    public string Url => $"https://www.metacritic.com/game/{Slug}/";
}

/// <summary>
/// Reads the JSON Metacritic's own website loads (no key needed). The game is found by its page name (the "slug" of the Steam
/// store link when there is one, otherwise made from the title), then checked by title so another game is never shown.
/// Cached a week; a miss is remembered for three days. A change on their side only hides the card.
/// </summary>
public static class Metacritic
{
    const string Backend = "https://backend.metacritic.com";

    public static async Task<MetacriticScores?> GetAsync(string gameName, string? steamMetacriticUrl, CancellationToken ct = default)
    {
        var key = ExeLocator.Normalize(gameName);
        if (key.Length < 2) return null;
        var dir = Path.Combine(AppInfo.CacheDir, "metacritic");
        var file = Path.Combine(dir, $"{Pcgw.Hash(key)}.json");
        var none = file + ".none";
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7))
                return JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, ct), MetacriticJson.Default.MetacriticScores);
            if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(3)) return null;

            MetacriticScores? found = null;
            foreach (var slug in Slugs(gameName, steamMetacriticUrl))
            {
                found = await ReadAsync(slug, key, ct);
                if (found is not null) break;
            }
            Directory.CreateDirectory(dir);
            if (found is null) await File.WriteAllTextAsync(none, "", ct);
            else await File.WriteAllTextAsync(file, JsonSerializer.Serialize(found, MetacriticJson.Default.MetacriticScores), ct);
            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Metacritic for {gameName}: {ex.Message}");
            try { return File.Exists(file) ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, ct), MetacriticJson.Default.MetacriticScores) : null; }
            catch { return null; }
        }
    }

    /// <summary>Page names to try: the one in Steam's link, then the title as Metacritic writes it, then the title before a subtitle.</summary>
    static IEnumerable<string> Slugs(string name, string? steamUrl)
    {
        var seen = new HashSet<string>();
        if (steamUrl is not null && Regex.Match(steamUrl, @"metacritic\.com/game/(?:[a-z0-9-]+/)?([a-z0-9-]+)", RegexOptions.IgnoreCase) is { Success: true } m)
        {
            var s = m.Groups[1].Value.ToLowerInvariant();
            // Old links carry the platform first ("/game/pc/name"): the name is the last part.
            if (seen.Add(s)) yield return s;
        }
        var full = Slug(name);
        if (full.Length > 0 && seen.Add(full)) yield return full;
        var cut = name.Split([':', '–', '—'], 2)[0];
        var shortSlug = Slug(cut);
        if (shortSlug.Length > 2 && seen.Add(shortSlug)) yield return shortSlug;
    }

    /// <summary>"Marvel's Spider-Man 2" → "marvels-spider-man-2": lower case, apostrophes dropped, anything else a single dash.</summary>
    public static string Slug(string name)
    {
        var s = new string(name.Where(c => c is not ('™' or '®' or '©')).ToArray()).ToLowerInvariant()
            .Replace("'", "").Replace("’", "").Replace(".", "").Replace("&", "and");
        s = Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
        return s;
    }

    static async Task<MetacriticScores?> ReadAsync(string slug, string key, CancellationToken ct)
    {
        string ProductUrl(string s) => $"{Backend}/games/metacritic/{s}/web?componentName=product&componentDisplayName=Product&componentType=Product";
        string? product;
        bool moved = false;
        try { product = await GetJsonAsync(ProductUrl(slug), ct); }
        catch (SlugMovedException m) when (m.Slug != slug)
        {
            // Metacritic renamed the page ("metal-gear-solid-v-ground-zeroes" is now "metal-gear-solid-ground-zeroes") and says where.
            slug = m.Slug;
            moved = true;
            product = await GetJsonAsync(ProductUrl(slug), ct);
        }
        if (product is null) return null;
        using var doc = JsonDocument.Parse(product);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("item", out var item)) return null;
        var title = Str(item, "title") ?? "";
        // The page must be this game: same name, or one name starting with the other ("Cyberpunk 2077" / "Cyberpunk 2077: Ultimate").
        var tk = ExeLocator.Normalize(title);
        // A page Metacritic itself sent us to is the game even when its title is written another way.
        if (tk.Length == 0 || !moved && !(tk == key || tk.StartsWith(key) || key.StartsWith(tk))) return null;

        int? score = null;
        int reviews = 0;
        string verdict = "", platform = "";
        // The PC version's Metascore when it has reviews; the lead platform's otherwise.
        if (item.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Array)
        {
            int best = int.MaxValue;
            foreach (var p in platforms.EnumerateArray())
            {
                if (!p.TryGetProperty("criticScoreSummary", out var cs) || Int(cs, "score") is not { } sc || Int(cs, "reviewCount") is not > 0) continue;
                bool pc = string.Equals(Str(p, "slug"), "pc", StringComparison.OrdinalIgnoreCase);
                bool lead = p.TryGetProperty("isLeadPlatform", out var l) && l.ValueKind == JsonValueKind.True;
                int rank = pc ? 0 : lead ? 1 : 2;
                if (rank >= best) continue;
                best = rank;
                score = sc;
                reviews = Int(cs, "reviewCount") ?? 0;
                verdict = Str(cs, "sentiment") ?? "";
                platform = Str(p, "name") ?? "";
            }
        }
        if (score is null && item.TryGetProperty("criticScoreSummary", out var main) && Int(main, "score") is { } ms)
        {
            score = ms;
            reviews = Int(main, "reviewCount") ?? 0;
            verdict = Str(main, "sentiment") ?? "";
            platform = Str(item, "platform") ?? "";
        }

        double? user = null;
        int ratings = 0;
        string userVerdict = "";
        var stats = await GetJsonAsync($"{Backend}/reviews/metacritic/user/games/{slug}/stats/web?componentName=user-score-summary&componentDisplayName=User+Score+Summary&componentType=ScoreSummary", ct);
        if (stats is not null)
        {
            using var u = JsonDocument.Parse(stats);
            if (u.RootElement.TryGetProperty("data", out var ud) && ud.TryGetProperty("item", out var ui)
                && ui.TryGetProperty("score", out var us) && us.ValueKind == JsonValueKind.Number && (Int(ui, "reviewCount") ?? 0) > 0)
            {
                user = Math.Round(us.GetDouble(), 1);
                ratings = Int(ui, "reviewCount") ?? 0;
                userVerdict = Str(ui, "sentiment") ?? "";
            }
        }
        if (score is null && user is null) return null;
        bool mustPlay = item.TryGetProperty("mustPlay", out var mp) && mp.ValueKind == JsonValueKind.True;
        return new MetacriticScores(slug, title, score, reviews, verdict, platform.Length > 0 ? platform : null, user, ratings, userVerdict, mustPlay);
    }

    /// <summary>The body of a 200 answer, null for "not found"; other failures throw (and keep the cache).</summary>
    static async Task<string?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await Http.Client.SendAsync(req, cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (resp.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
        {
            // The answer names the page's new name: errors[0].context.availableOn[0].slug.
            string? to = null;
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                to = Str(doc.RootElement.GetProperty("errors")[0].GetProperty("context").GetProperty("availableOn")[0], "slug");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { }
            if (to is { Length: > 0 }) throw new SlugMovedException(to);
            return null;
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}

/// <summary>A Metacritic page that moved to another name (<see cref="Slug"/>).</summary>
sealed class SlugMovedException(string slug) : Exception(slug)
{
    public string Slug { get; } = slug;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(MetacriticScores))]
internal partial class MetacriticJson : System.Text.Json.Serialization.JsonSerializerContext;
