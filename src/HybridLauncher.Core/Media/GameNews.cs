using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

/// <summary>One news post: a game's (from Steam's news, where the developers post their announcements and patch notes) or
/// Hybrid Launcher's own (a release on GitHub).</summary>
/// <param name="Official">Posted by the game's developers on Steam (not a press article Steam relays).</param>
/// <param name="PatchNotes">An update's notes: Steam's "patchnotes" tag, or an announcement titled as an update.</param>
public sealed record NewsItem(string? GameId, string Source, string Title, string Url, DateTime Date, string Excerpt, string? Image,
    bool Official, bool PatchNotes, string? AppId = null);

/// <summary>
/// News for the News page. Games: Steam's public news service (ISteamNews/GetNewsForApp, no key, no sign-in) by the game's
/// Steam app id, which is also found for games from other stores by their name. Its posts tagged "patchnotes" are the
/// developers' own notes for each update, so they double as the game's version history; SteamDB keeps the full list of
/// builds and is linked (it has no public API). Hybrid Launcher: its releases on GitHub. Everything goes through the disk
/// cache, so the page opens at once with what it read last and works offline.
/// </summary>
public static class GameNews
{
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);

    /// <summary>The newest posts of the game with Steam app id <paramref name="appId"/> (official and relayed press), newest first.</summary>
    public static async Task<List<NewsItem>> ForAppAsync(string appId, string? gameId, int count, CancellationToken ct, bool force = false)
    {
        if (!Regex.IsMatch(appId, @"^\d+$")) return [];
        string url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count={Math.Clamp(count, 1, 100)}&maxlength=600&format=json";
        var json = await Http.GetCachedAsync(url, $"news-{appId}-{count}.json", MaxAge, force, ct: ct);
        return json is null ? [] : Parse(json, appId, gameId);
    }

    /// <summary>A game's update notes only (its version history), newest first: the developers' own posts tagged as patch notes.</summary>
    public static async Task<List<NewsItem>> PatchNotesAsync(string appId, string? gameId, CancellationToken ct, bool force = false) =>
        (await ForAppAsync(appId, gameId, 100, ct, force)).Where(n => n.PatchNotes && n.Official).ToList();

    /// <summary>SteamDB's page of every build and patch of a game (it keeps the history Steam itself does not show).</summary>
    public static string SteamDbPatches(string appId) => $"https://steamdb.info/app/{appId}/patchnotes/";

    /// <summary>A game's wide header picture on Steam's CDN, for a post with no picture of its own.</summary>
    public static string SteamHeader(string appId) => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";

    internal static List<NewsItem> Parse(string json, string appId, string? gameId)
    {
        var list = new List<NewsItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("appnews", out var news) || !news.TryGetProperty("newsitems", out var items)) return list;
            foreach (var e in items.EnumerateArray())
            {
                string title = Str(e, "title");
                string link = Str(e, "url");
                if (title.Length == 0 || !link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                var date = DateTimeOffset.FromUnixTimeSeconds(e.TryGetProperty("date", out var d) && d.TryGetInt64(out var s) ? s : 0).UtcDateTime;
                string feed = Str(e, "feedname");
                bool official = feed == "steam_community_announcements";
                var tags = e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                bool patch = tags.Contains("patchnotes") || official && IsUpdateTitle(title);
                string contents = Str(e, "contents");
                string source = official ? "Steam" : Str(e, "feedlabel") is { Length: > 0 } label ? label : "Steam";
                list.Add(new NewsItem(gameId, source, Clean(title), link, date, Excerpt(contents), Picture(contents), official, patch, appId));
            }
        }
        catch (JsonException ex) { Log.Warn($"Game news {appId}: {ex.Message}"); }
        return list.OrderByDescending(n => n.Date).ToList();
    }

    /// <summary>"Patch 1.2", "Update 4.0", "Hotfix", "v1.0.3": an announcement that is an update's notes.</summary>
    internal static bool IsUpdateTitle(string title) =>
        Regex.IsMatch(title, @"\b(patch|update|hotfix|hot-fix|changelog|release notes|patch notes)\b|\bv?\d+\.\d+(\.\d+)?\b", RegexOptions.IgnoreCase);

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The post's first picture: Steam's own clan images, a [img] tag, or an HTML img.</summary>
    public static string? Picture(string contents)
    {
        var clan = Regex.Match(contents, @"\{STEAM_CLAN_IMAGE\}/([^\s\[\]""<>]+\.(?:png|jpe?g|gif|webp))", RegexOptions.IgnoreCase);
        if (clan.Success) return "https://clan.cloudflare.steamstatic.com/images/" + clan.Groups[1].Value;
        var bb = Regex.Match(contents, @"\[img\](https?://[^\[\]\s]+)\[/img\]|\[img\b[^\]]*\bsrc=""(https?://[^""]+)""", RegexOptions.IgnoreCase);
        if (bb.Success) return bb.Groups[1].Success ? bb.Groups[1].Value : bb.Groups[2].Value;
        var html = Regex.Match(contents, @"<img[^>]+src=""(https?://[^""]+)""", RegexOptions.IgnoreCase);
        return html.Success ? html.Groups[1].Value : null;
    }

    /// <summary>The post as plain text: BBCode, HTML and Steam's placeholders taken out, spaces folded, about 300 characters.</summary>
    public static string Excerpt(string contents)
    {
        // Pictures and videos out whole, the tag's attributes included: Steam's posts now write [img src="..."][/img] (2.0.0-rc.10;
        // before, "{STEAM_CLAN_IMAGE}\S*" ate the address and the closing tag and left '[img src="' at the start of the text).
        string s = Regex.Replace(contents, @"\[img\b[^\]]*\][^\[]*(\[/img\])?|\[previewyoutube\b[^\]]*\][^\[]*(\[/previewyoutube\])?|\[video\b[^\]]*\][^\[]*(\[/video\])?", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\{STEAM_CLAN_IMAGE\}[^\s\[\]""<>]*", " ");
        s = Regex.Replace(s, @"\[/?[a-z0-9*]+([= ][^\]]*)?\]", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = Clean(s);
        return s.Length > 300 ? s[..300].TrimEnd() + "…" : s;
    }

    static string Clean(string s) => Regex.Replace(System.Net.WebUtility.HtmlDecode(s), @"\s+", " ").Trim();

    /// <summary>Hybrid Launcher's own releases on GitHub, newest first (test builds left out): its version history.</summary>
    public static async Task<List<NewsItem>> AppReleasesAsync(CancellationToken ct, bool force = false)
    {
        var json = await Http.GetCachedAsync($"https://api.github.com/repos/{AppInfo.Repository}/releases?per_page=30", "news-app-releases.json",
            TimeSpan.FromHours(6), force, "application/vnd.github+json", ct);
        var list = new List<NewsItem>();
        if (json is null) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var dr) && dr.GetBoolean()) continue;
                if (r.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) continue;
                string tag = Str(r, "tag_name"), name = Str(r, "name"), url = Str(r, "html_url"), body = Str(r, "body");
                DateTime.TryParse(Str(r, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date);
                // Markdown to plain text: headings, emphasis, links and list marks out.
                string text = Regex.Replace(body, @"!\[[^\]]*\]\([^)]*\)", " ");
                text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
                text = Regex.Replace(text, @"(?m)^\s*(#+|[-*]|\d+\.)\s*", "");
                text = Regex.Replace(text.Replace("**", "").Replace("__", "").Replace("`", ""), @"(?<!\w)\*|\*(?!\w)", "");
                // Emoji used as section marks ("🐛 Fixes") read as noise in a two-line excerpt.
                text = Regex.Replace(text, "\\p{Cs}|[\u2600-\u27BF]\uFE0F?", "");
                list.Add(new NewsItem(null, "Hybrid Launcher", name.Length > 0 ? name : tag, url, date, Clean(text) is var c && c.Length > 400 ? c[..400].TrimEnd() + "…" : Clean(text),
                    null, true, true));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { Log.Warn($"Hybrid Launcher releases: {ex.Message}"); }
        return list.OrderByDescending(n => n.Date).ToList();
    }
}
