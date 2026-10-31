using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

public sealed record StoreScreenshot(string Thumbnail, string Full);

/// <summary>A Steam trailer: Steam streams them (HLS / DASH), there is no plain file any more.</summary>
public sealed record StoreMovie(string Name, string Thumbnail, string? Hls, string? Dash);

public sealed record StoreLanguage(string Name, bool FullAudio);

/// <summary>One line of the system requirements ("Graphics" → "GeForce GTX 1060"); Name is empty for free text.</summary>
public sealed record Requirement(string Name, string Value);

/// <summary>Everything the Steam store page shows about a game, from the public appdetails API.</summary>
public sealed class StoreDetails
{
    public required string AppId { get; init; }
    public string Name { get; init; } = "";
    public string ShortDescription { get; init; } = "";
    public List<RichLine> About { get; init; } = [];
    public List<string> Developers { get; init; } = [];
    public List<string> Publishers { get; init; } = [];
    public List<string> Genres { get; init; } = [];
    public string? ReleaseDate { get; init; }
    public int? Metacritic { get; init; }
    public string? MetacriticUrl { get; init; }
    public string? Website { get; init; }
    public int? Recommendations { get; init; }
    public List<StoreScreenshot> Screenshots { get; init; } = [];
    public List<StoreMovie> Movies { get; init; } = [];
    public List<Requirement> Minimum { get; init; } = [];
    public List<Requirement> Recommended { get; init; } = [];
    public List<StoreLanguage> Languages { get; init; } = [];

    /// <summary>Steam's "controller_support": "full", "partial" or null.</summary>
    public string? ControllerSupport { get; init; }
    public HashSet<int> Categories { get; init; } = [];

    // Steam store category ids (store search "category2").
    public bool DualShock => Categories.Contains(55);
    public bool DualSense => Categories.Contains(57);
    public bool DualSenseBluetooth => Categories.Contains(58);
    public bool XboxFull => Categories.Contains(28) || ControllerSupport == "full";
    public bool XboxPartial => !XboxFull && (Categories.Contains(18) || ControllerSupport == "partial");
    public bool Hdr => Categories.Contains(61);

    public string StoreUrl => $"https://store.steampowered.com/app/{AppId}";

    /// <summary>The store data (the same cached file the banner uses), or null when Steam has nothing for the id.</summary>
    public static async Task<StoreDetails?> GetAsync(string appId, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetCachedAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english",
                Path.Combine("store", $"app-{appId}.json"), TimeSpan.FromDays(14), ct: ct);
            return json is null ? null : await Task.Run(() => Parse(appId, json), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Store details for {appId}: {ex.Message}");
            return null;
        }
    }

    public static StoreDetails? Parse(string appId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(appId, out var app) || !app.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;

        string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        List<string> Strings(string name) => d.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList()
            : [];

        var screenshots = new List<StoreScreenshot>();
        if (d.TryGetProperty("screenshots", out var ss) && ss.ValueKind == JsonValueKind.Array)
            foreach (var s in ss.EnumerateArray())
                if (S(s, "path_thumbnail") is { } t && S(s, "path_full") is { } f) screenshots.Add(new StoreScreenshot(t, f));

        var movies = new List<StoreMovie>();
        if (d.TryGetProperty("movies", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
            {
                // Older entries still carry plain mp4 files; newer ones only stream.
                var hls = S(m, "hls_h264");
                string? mp4 = m.TryGetProperty("mp4", out var mp) && mp.ValueKind == JsonValueKind.Object ? S(mp, "max") ?? S(mp, "480") : null;
                if (S(m, "thumbnail") is { } thumb && (hls ?? mp4) is { } src)
                    movies.Add(new StoreMovie(System.Net.WebUtility.HtmlDecode(S(m, "name") ?? "Trailer"), thumb, src, S(m, "dash_h264")));
            }

        var genres = d.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
            ? gs.EnumerateArray().Select(g => S(g, "description")).Where(g => g is not null).Select(g => g!).ToList() : [];
        var categories = d.TryGetProperty("categories", out var cs) && cs.ValueKind == JsonValueKind.Array
            ? cs.EnumerateArray().Where(c => c.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number).Select(c => c.GetProperty("id").GetInt32()).ToHashSet() : [];

        int? metacritic = null;
        string? metaUrl = null;
        if (d.TryGetProperty("metacritic", out var mc) && mc.ValueKind == JsonValueKind.Object)
        {
            if (mc.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number) metacritic = sc.GetInt32();
            metaUrl = S(mc, "url");
        }
        int? recs = d.TryGetProperty("recommendations", out var rc) && rc.ValueKind == JsonValueKind.Object && rc.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number ? tot.GetInt32() : null;

        List<Requirement> min = [], rec = [];
        if (d.TryGetProperty("pc_requirements", out var pr) && pr.ValueKind == JsonValueKind.Object)
        {
            min = Requirements(S(pr, "minimum"));
            rec = Requirements(S(pr, "recommended"));
        }

        return new StoreDetails
        {
            AppId = appId,
            Name = S(d, "name") ?? "",
            ShortDescription = System.Net.WebUtility.HtmlDecode(S(d, "short_description") ?? ""),
            // The whole description as the store page shows it (editions, extras, then About This Game); "about" alone when
            // that is all there is.
            About = new[] { S(d, "detailed_description"), S(d, "about_the_game") }.Select(HtmlText.ToLines).FirstOrDefault(l => l.Count > 0) ?? [],
            Developers = Strings("developers"),
            Publishers = Strings("publishers"),
            Genres = genres,
            ReleaseDate = d.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.Object ? S(rd, "date") : null,
            Metacritic = metacritic,
            MetacriticUrl = metaUrl,
            Website = S(d, "website"),
            Recommendations = recs,
            Screenshots = screenshots,
            Movies = movies,
            Minimum = min,
            Recommended = rec,
            Languages = ParseLanguages(S(d, "supported_languages")),
            ControllerSupport = S(d, "controller_support"),
            Categories = categories,
        };
    }

    /// <summary>"&lt;strong&gt;OS:&lt;/strong&gt; Windows 10&lt;br&gt;" items → ("OS", "Windows 10"); the "Minimum:" title is dropped.</summary>
    static List<Requirement> Requirements(string? html)
    {
        var list = new List<Requirement>();
        if (string.IsNullOrWhiteSpace(html)) return list;
        var items = Regex.Matches(html, @"<li>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToList();
        if (items.Count == 0) items = Regex.Split(html, @"<br\s*/?>", RegexOptions.IgnoreCase).ToList();
        foreach (var item in items)
        {
            var m = Regex.Match(item, @"^\s*<strong>(.*?)</strong>(.*)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var name = HtmlText.Plain(m.Groups[1].Value).TrimEnd(':').Trim();
                var value = HtmlText.Plain(m.Groups[2].Value);
                if (name.Equals("Minimum", StringComparison.OrdinalIgnoreCase) || name.Equals("Recommended", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Length > 0) list.Add(new Requirement("", value));
                    continue;
                }
                list.Add(new Requirement(name, value));
            }
            else if (HtmlText.Plain(item) is { Length: > 0 } text && !text.EndsWith(':')) list.Add(new Requirement("", text));
        }
        return list;
    }

    /// <summary>"English&lt;strong&gt;*&lt;/strong&gt;, French, ... &lt;br&gt;&lt;strong&gt;*&lt;/strong&gt;languages with full audio support".</summary>
    static List<StoreLanguage> ParseLanguages(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        var body = Regex.Split(html, @"<br\s*/?>", RegexOptions.IgnoreCase)[0];
        var list = new List<StoreLanguage>();
        foreach (var part in body.Split(','))
        {
            bool audio = part.Contains('*');
            var name = HtmlText.Plain(part.Replace("*", "")).Trim();
            if (name.Length > 0) list.Add(new StoreLanguage(name, audio));
        }
        return list;
    }
}
