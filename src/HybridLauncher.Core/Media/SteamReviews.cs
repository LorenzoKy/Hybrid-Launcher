using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

public sealed record SteamReview(string Author, bool Recommended, string Hours, string Posted, string Text, string Helpful, string Url, string? Avatar = null);

/// <summary>Steam's user reviews of a game: the overall rating (all time and the last 30 days) and a few of the most helpful.</summary>
public sealed class SteamReviewSet
{
    public long Up { get; init; }
    public long Down { get; init; }
    public long RecentUp { get; init; }
    public long RecentDown { get; init; }
    public List<SteamReview> Top { get; init; } = [];
    /// <summary>The newest reviews (Steam's "Recently posted" column).</summary>
    public List<SteamReview> Recent { get; init; } = [];
    public required string AppId { get; init; }

    public long Total => Up + Down;
    public double Share => Total > 0 ? (double)Up / Total : 0;

    /// <summary>Steam's own words for a score ("Very Positive"...): share of positive reviews and how many there are.</summary>
    public static string Label(long up, long down)
    {
        long n = up + down;
        if (n < 10) return n == 0 ? "No user reviews" : "Too few reviews";
        double p = (double)up / n * 100;
        return p switch
        {
            >= 95 when n >= 500 => "Overwhelmingly Positive",
            >= 80 when n >= 50 => "Very Positive",
            >= 80 => "Positive",
            >= 70 => "Mostly Positive",
            >= 40 => "Mixed",
            >= 20 => "Mostly Negative",
            _ when n >= 500 => "Overwhelmingly Negative",
            _ when n >= 50 => "Very Negative",
            _ => "Negative",
        };
    }

    public string Summary => Label(Up, Down);
    public string RecentSummary => Label(RecentUp, RecentDown);

    public string StorePage => $"https://store.steampowered.com/app/{AppId}/#app_reviews_hash";
}

/// <summary>
/// Read from Steam's public pages, no account needed: the review histogram of the store (positive / negative counts by month)
/// and the community hub's most helpful reviews. Cached for a day.
/// </summary>
public static partial class SteamReviews
{
    public static async Task<SteamReviewSet?> GetAsync(string appId, CancellationToken ct = default)
    {
        try
        {
            var histogram = await Http.GetCachedAsync($"https://store.steampowered.com/appreviewhistogram/{appId}?l=english&review_score_preference=0",
                Path.Combine("reviews", $"hist-{appId}.json"), TimeSpan.FromDays(1), ct: ct);
            long up = 0, down = 0, rUp = 0, rDown = 0;
            if (histogram is not null)
            {
                using var doc = JsonDocument.Parse(histogram);
                if (doc.RootElement.TryGetProperty("results", out var r))
                {
                    if (r.TryGetProperty("rollups", out var rollups))
                        foreach (var x in rollups.EnumerateArray()) { up += L(x, "recommendations_up"); down += L(x, "recommendations_down"); }
                    if (r.TryGetProperty("recent", out var recent))
                        foreach (var x in recent.EnumerateArray()) { rUp += L(x, "recommendations_up"); rDown += L(x, "recommendations_down"); }
                }
            }
            var html = await Http.GetCachedAsync(
                $"https://steamcommunity.com/app/{appId}/homecontent/?userreviewsoffset=0&p=1&numperpage=6&browsefilter=toprated&appHubSubSection=10&l=english&filterLanguage=default&searchText=&maxInappropriateScore=50",
                Path.Combine("reviews", $"top-{appId}.html"), TimeSpan.FromDays(1), ct: ct);
            var top = html is null ? [] : ParseCards(html, 5);
            // Steam's "Recently posted" column.
            var newest = await Http.GetCachedAsync(
                $"https://steamcommunity.com/app/{appId}/homecontent/?userreviewsoffset=0&p=1&numperpage=8&browsefilter=mostrecent&appHubSubSection=10&l=english&filterLanguage=default&searchText=&maxInappropriateScore=50",
                Path.Combine("reviews", $"new-{appId}.html"), TimeSpan.FromHours(6), ct: ct);
            var recentCards = newest is null ? [] : ParseCards(newest, 5);
            if (up + down == 0 && top.Count == 0) return null;
            return new SteamReviewSet { AppId = appId, Up = up, Down = down, RecentUp = rUp, RecentDown = rDown, Top = top, Recent = recentCards };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam reviews for {appId}: {ex.Message}");
            return null;
        }

        static long L(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
    }

    [GeneratedRegex(@"class=""apphub_Card[^""]*""[^>]*data-modal-content-url=""([^""]*)""(.*?)(?=class=""apphub_Card |\z)", RegexOptions.Singleline)]
    private static partial Regex Card();

    [GeneratedRegex(@"apphub_CardContentAuthorName[^>]*>\s*<a[^>]*>([^<]*)</a>", RegexOptions.Singleline)]
    private static partial Regex Author();

    [GeneratedRegex(@"<div class=""title"">([^<]*)</div>")]
    private static partial Regex Title();

    [GeneratedRegex(@"<div class=""hours"">([^<]*)</div>")]
    private static partial Regex Hours();

    [GeneratedRegex(@"<div class=""date_posted"">([^<]*)</div>(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex Body();

    [GeneratedRegex(@"<div class=""found_helpful"">\s*([^<]*)", RegexOptions.Singleline)]
    private static partial Regex Helpful();

    [GeneratedRegex(@"appHubIconHolder[^""]*""><img src=""([^""]+)""")]
    private static partial Regex Avatar();

    /// <summary>The review cards of the community hub page: author, avatar, thumb, hours, date and the text (line breaks kept).</summary>
    public static List<SteamReview> ParseCards(string html, int max = 4)
    {
        var list = new List<SteamReview>();
        foreach (Match m in Card().Matches(html))
        {
            var block = m.Groups[2].Value;
            var body = Body().Match(block);
            if (!body.Success) continue;
            var text = Regex.Replace(body.Groups[2].Value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", "")).Trim();
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            if (text.Length == 0) continue;
            list.Add(new SteamReview(
                WebUtility.HtmlDecode(Author().Match(block).Groups[1].Value.Trim()),
                Title().Match(block).Groups[1].Value.Trim().StartsWith("Recommended", StringComparison.OrdinalIgnoreCase),
                Hours().Match(block).Groups[1].Value.Trim(),
                body.Groups[1].Value.Replace("Posted:", "").Trim(),
                text,
                WebUtility.HtmlDecode(Helpful().Match(block).Groups[1].Value.Trim()),
                m.Groups[1].Value,
                Avatar().Match(block) is { Success: true } a ? a.Groups[1].Value : null));
            if (list.Count >= max) break;
        }
        return list;
    }
}
