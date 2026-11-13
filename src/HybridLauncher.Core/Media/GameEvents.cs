using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

/// <summary>What kind of post a Steam event is (Steam's own event types, as its store and library label them).</summary>
public enum GameEventKind { News, MajorUpdate, Update, SmallUpdate, Event }

/// <summary>A section of an update's notes: its heading, how many changes it lists and the first of them.</summary>
public sealed record UpdateSection(string Heading, int Changes, string First);

/// <summary>What a block of a post is: a heading, a paragraph, a list item, a picture (Text = its address) or a YouTube video (Text = its id).</summary>
public enum NoteBlockKind { Heading, Paragraph, Item, Picture, Video }

/// <summary>One block of a post's text, read from Steam's BBCode (or the HTML some posts use).</summary>
/// <param name="Level">For an item: how deep it sits in nested lists (0 = a list of its own).</param>
public sealed record NoteBlock(NoteBlockKind Kind, string Text, int Level = 0);

/// <summary>
/// One post of a game's event feed on Steam (the feed of the store's and the library's news): its kind (major, regular or
/// small update, news, event), its one-line summary as the developers wrote it, its capsule picture, the build it shipped
/// with when Steam knows it, and for updates an outline of the notes (their sections and how many changes each lists).
/// </summary>
public sealed record GameEvent(string Gid, string AppId, GameEventKind Kind, string Title, string Summary, string Excerpt, DateTime Date,
    string? Image, string? BuildId, List<UpdateSection> Outline, List<string> Points, string Body = "", bool Featured = false, DateTime? Ends = null)
{
    /// <summary>The whole post as blocks to read in the app (headings, paragraphs, list items, pictures, videos).</summary>
    public List<NoteBlock> Blocks() => GameEvents.Blocks(Body);

    public bool IsUpdate => Kind is GameEventKind.MajorUpdate or GameEventKind.Update or GameEventKind.SmallUpdate;

    /// <summary>The post on the Steam store's news page.</summary>
    public string Url => $"https://store.steampowered.com/news/app/{AppId}/view/{Gid}";

    /// <summary>SteamDB's page of the build this update shipped with (only when Steam tells which build).</summary>
    public string? BuildUrl => BuildId is { Length: > 0 } b ? $"https://steamdb.info/patchnotes/{b}/" : null;

    /// <summary>A version number in the title ("2.5", "1.63", "v1.0.3"), or null.</summary>
    public string? Version => Regex.Match(Title, @"\bv?(\d+\.\d+(?:\.\d+){0,2}[a-z]?)\b", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>How many changes the outline counts in all.</summary>
    public int Changes => Outline.Sum(s => s.Changes) + Points.Count;
}

/// <summary>
/// A game's event feed on Steam (store.steampowered.com/events, public, no key): the same posts as the news, plus what Steam
/// knows about them that the news service does not say: which are major, regular or small updates, the developers' one-line
/// summary in the user's language, the capsule picture and the build. Its updates are the game's version history; a hundred
/// posts a page, two pages read (years of updates for most games). Through the disk cache, like the news.
/// </summary>
public static class GameEvents
{
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);

    /// <summary>The newest posts of a game, newest first (up to <paramref name="pages"/> × 100).</summary>
    public static async Task<List<GameEvent>> ForAppAsync(string appId, CancellationToken ct, int pages = 2, bool force = false)
    {
        if (!Regex.IsMatch(appId, @"^\d+$")) return [];
        var list = new List<GameEvent>();
        for (int page = 0; page < Math.Clamp(pages, 1, 5); page++)
        {
            string url = $"https://store.steampowered.com/events/ajaxgetpartnereventspageable/?clan_accountid=0&appid={appId}&offset={page * 100}&count=100&l=english";
            string? json;
            // Parsed off the UI thread (2.0.0-rc.9): two hundred posts held it 40-60 ms when a Dashboard filled its news.
            try { json = await Http.GetCachedAsync(url, $"events-{appId}-{page}.json", MaxAge, force, ct: ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"Steam events {appId} page {page}: {ex.Message}"); break; }
            if (json is null) break;
            var got = Parse(json, appId);
            list.AddRange(got);
            if (got.Count < 90) break;
        }
        return [.. list.GroupBy(e => e.Gid).Select(g => g.First()).OrderByDescending(e => e.Date)];
    }

    /// <summary>
    /// The post to put first, as Steam's library features it: the newest of the post the developers chose for the library's
    /// spotlight (Steam's "Featured", last 60 days; an event or sale that has ended is left out) and the newest major update
    /// (last 120 days); without either, the newest update of the last 45 days (2.0.0-rc.6: before, major updates only, so a
    /// game's featured patch notes posted as news never showed). When an update's own post is short (a trailer and a link),
    /// the outline comes from the notes posted with it (within ten days).
    /// </summary>
    public static GameEvent? Spotlight(IReadOnlyList<GameEvent> events, DateTime? now = null)
    {
        var today = now ?? DateTime.UtcNow;
        var featured = events.Where(e => e.Featured && today - e.Date < TimeSpan.FromDays(60) && !(e.Kind == GameEventKind.Event && e.Ends is { } end && end < today))
            .MaxBy(e => e.Date);
        var major = events.Where(e => e.Kind == GameEventKind.MajorUpdate && today - e.Date < TimeSpan.FromDays(120)).MaxBy(e => e.Date);
        var pick = featured is null ? major : major is null || featured.Date >= major.Date ? featured : major;
        pick ??= events.Where(e => e.IsUpdate && today - e.Date < TimeSpan.FromDays(45)).MaxBy(e => e.Date);
        if (pick is null || pick.Changes >= 3 || !pick.IsUpdate) return pick;
        var notes = events.Where(e => e.IsUpdate && e.Gid != pick.Gid && Math.Abs((e.Date - pick.Date).TotalDays) <= 10 && e.Changes > pick.Changes)
            .OrderByDescending(e => e.Changes).FirstOrDefault();
        // The notes' own text too, so reading the Spotlight in the app shows the notes, not only the announcement.
        return notes is null ? pick : pick with { Outline = notes.Outline, Points = notes.Points, Body = notes.Body.Length > 0 ? notes.Body : pick.Body };
    }

    /// <summary>
    /// The build of a Steam game installed on this PC and when Steam last updated it, from its appmanifest_&lt;id&gt;.acf (next to
    /// the "common" folder that holds the game), or null for other stores or when the manifest is not there.
    /// </summary>
    public static (string BuildId, DateTime? Updated)? InstalledBuild(Game game)
    {
        if (game.Store != GameStore.Steam || game.SteamAppId is not { Length: > 0 } app || game.InstallDir.Length == 0) return null;
        try
        {
            var common = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(game.InstallDir));
            var apps = common is null ? null : Path.GetDirectoryName(common);
            if (apps is null) return null;
            var acf = Path.Combine(apps, $"appmanifest_{app}.acf");
            if (!File.Exists(acf)) return null;
            var state = Detection.VdfNode.Parse(File.ReadAllText(acf)).Child("AppState");
            if (state?["buildid"] is not { Length: > 0 } build || build == "0") return null;
            DateTime? updated = long.TryParse(state["LastUpdated"], out var t) && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : null;
            return (build, updated);
        }
        catch (Exception ex) { Log.Warn($"Installed build of {game.Name}: {ex.Message}"); return null; }
    }

    /// <summary>Steam's language slot for the app's language (the localized_* arrays of an event): 0 English, 3 Italian.</summary>
    static int LangSlot => L.Italian ? 3 : 0;

    internal static List<GameEvent> Parse(string json, string appId)
    {
        var list = new List<GameEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in events.EnumerateArray())
            {
                // One odd post is skipped, never the whole feed (2.0.0-rc.6: HITMAN's news and Spotlight were empty).
                try { if (ParseOne(e, appId) is { } one) list.Add(one); }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException or ArgumentException)
                {
                    Log.Warn($"Steam events {appId}: a post skipped: {ex.Message}");
                }
            }
        }
        catch (JsonException ex) { Log.Warn($"Steam events {appId}: {ex.Message}"); }
        return list;
    }

    static GameEvent? ParseOne(JsonElement e, string appId)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() != 0) return null;
        string gid = Str(e, "gid");
        string title = Clean(Str(e, "event_name"));
        if (gid.Length == 0 || title.Length == 0) return null;
        int type = e.TryGetProperty("event_type", out var t) && t.TryGetInt32(out var ti) ? ti : 28;
        long start = e.TryGetProperty("rtime32_start_time", out var st) && st.TryGetInt64(out var sv) ? sv : 0;
        string body = "", clan = "";
        if (e.TryGetProperty("announcement_body", out var ab) && ab.ValueKind == JsonValueKind.Object)
        {
            body = Str(ab, "body");
            clan = ab.TryGetProperty("clanid", out var c) ? c.ToString() : "";
            if (start == 0 && ab.TryGetProperty("posttime", out var pt) && pt.TryGetInt64(out var pv)) start = pv;
        }
        string summary = "", image = "";
        bool featured = false;
        string data = Str(e, "jsondata");
        if (data.Length > 0)
        {
            try
            {
                using var jd = JsonDocument.Parse(data);
                // Some posts carry a list here (HITMAN's hotfix of 27 January 2026): nothing to read from it.
                if (jd.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("jsondata is not an object");
                summary = Localized(jd.RootElement, "localized_summary");
                image = Localized(jd.RootElement, "localized_capsule_image");
                // Chosen by the developers for the Steam library's spotlight ("Featured" there).
                featured = jd.RootElement.TryGetProperty("library_spotlight", out var ls) && ls.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { }
        }
        string? picture = image.Length > 0 && Regex.IsMatch(clan, @"^\d+$") ? $"https://clan.akamai.steamstatic.com/images/{clan}/{image}" : GameNews.Picture(body);
        string? build = e.TryGetProperty("build_id", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var bv) && bv > 0
            ? bv.ToString(CultureInfo.InvariantCulture) : null;
        var kind = type switch
        {
            14 => GameEventKind.MajorUpdate,
            13 => GameEventKind.Update,
            12 => GameEventKind.SmallUpdate,
            28 => GameEventKind.News,
            _ => GameEventKind.Event,
        };
        long endTime = e.TryGetProperty("rtime32_end_time", out var et) && et.TryGetInt64(out var ev) ? ev : 0;
        // The notes' outline is only worth reading for updates, and for a featured post (patch notes posted as news).
        var (outline, points) = kind is GameEventKind.Event || (kind == GameEventKind.News && !featured) ? ([], []) : Outline(body);
        return new GameEvent(gid, appId, kind, title, Clean(summary), GameNews.Excerpt(body),
            DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime, picture, build, outline, points, body, featured,
            endTime > start ? DateTimeOffset.FromUnixTimeSeconds(endTime).UtcDateTime : null);
    }

    /// <summary>The value for the app's language in a localized_* array, else the English one.</summary>
    static string Localized(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        string At(int i) => i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.String ? arr[i].GetString() ?? "" : "";
        return At(LangSlot) is { Length: > 0 } s ? s : At(0);
    }

    /// <summary>
    /// The outline of an update's notes: each heading ([h1]-[h3], or a paragraph in bold alone) with the number of changes listed
    /// under it and the first of them; and, when the notes have no headings, their first changes as plain points.
    /// </summary>
    internal static (List<UpdateSection> Outline, List<string> Points) Outline(string body)
    {
        var outline = new List<UpdateSection>();
        var points = new List<string>();
        if (body.Length == 0) return (outline, points);
        var heads = Regex.Matches(body, @"\[h[1-3]\](?<t>.*?)\[/h[1-3]\]|\[p\]\s*\[b\](?<t>[^\[]{3,80})\[/b\]\s*\[/p\]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        for (int i = 0; i < heads.Count; i++)
        {
            string heading = Plain(heads[i].Groups["t"].Value).TrimEnd(':');
            if (heading.Length is < 2 or > 90) continue;
            int from = heads[i].Index + heads[i].Length, to = i + 1 < heads.Count ? heads[i + 1].Index : body.Length;
            var items = Items(body[from..to]);
            if (items.Count == 0) continue;
            outline.Add(new UpdateSection(heading, items.Count, items[0]));
        }
        if (outline.Count == 0) points.AddRange(Items(body).Take(5));
        return (outline.Take(8).ToList(), points);
    }

    /// <summary>
    /// A post's body as blocks, in order: BBCode ([h1]-[h3], [list]/[*], [img], [p], [previewyoutube]) and the HTML tags some
    /// posts use instead. A paragraph that is bold alone counts as a heading; links keep their text only.
    /// </summary>
    public static List<NoteBlock> Blocks(string body)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(body)) return blocks;
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        string s = body.Replace("{STEAM_CLAN_IMAGE}", "https://clan.akamai.steamstatic.com/images");
        // Each block goes on its own line, tagged with a marker character and a letter (I picture, V video, H heading, L item).
        const string M = "\n\u0001";
        // [img]address[/img], and Steam's newer [img src="address"][/img] (2.0.0-rc.10), whose closing tag may be missing.
        s = Regex.Replace(s, @"\[img\b[^\]]*?\bsrc=""([^""]+)""[^\]]*\]([^\[]*\[/img\])?|\[img\b[^\]]*\](.*?)\[/img\]",
            m => M + "I" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value).Trim() + "\n", O);
        s = Regex.Replace(s, @"<img[^>]*src=""([^""]+)""[^>]*>", m => M + "I" + m.Groups[1].Value + "\n", O);
        s = Regex.Replace(s, @"\[previewyoutube=([A-Za-z0-9_-]{6,})[^\]]*\](\[/previewyoutube\])?", m => M + "V" + m.Groups[1].Value + "\n", O);
        s = Regex.Replace(s, @"\[h[1-6]\](.*?)\[/h[1-6]\]|<h[1-6][^>]*>(.*?)</h[1-6]>", m => M + "H" + m.Groups[1].Value + m.Groups[2].Value + "\n", O);
        s = Regex.Replace(s, @"\[p\]\s*\[b\]([^\[]{2,100})\[/b\]\s*\[/p\]", m => M + "H" + m.Groups[1].Value + "\n", O);
        // A list opens (+) and closes (-): an item's depth is how many are open around it. An item's own [p] is its text.
        s = Regex.Replace(s, @"\[(list|olist)[^\]]*\]|<(ul|ol)[^>]*>", M + "+\n", O);
        s = Regex.Replace(s, @"\[/(list|olist)\]|</(ul|ol)>", M + "-\n", O);
        s = Regex.Replace(s, @"(\[\*\]|<li[^>]*>)\s*(\[p\]|<p[^>]*>)?\s*", M + "L", O);
        s = Regex.Replace(s, @"\[/?(p|\*)[^\]]*\]|\[br\]|</?(p|div)[^>]*>|</li>|<br\s*/?>", "\n", O);
        int depth = 0;
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '\u0001')
            {
                if (line.Length >= 2 && line[1] == '+') { depth++; continue; }
                if (line.Length >= 2 && line[1] == '-') { depth = Math.Max(0, depth - 1); continue; }
                if (line.Length < 3) continue;
                string rest = line[2..].Trim();
                switch (line[1])
                {
                    case 'I': if (Uri.IsWellFormedUriString(rest, UriKind.Absolute)) blocks.Add(new(NoteBlockKind.Picture, rest)); break;
                    case 'V': blocks.Add(new(NoteBlockKind.Video, rest)); break;
                    case 'H': if (Plain(rest).TrimEnd(':') is { Length: > 1 } h) blocks.Add(new(NoteBlockKind.Heading, h)); break;
                    case 'L': if (Plain(rest) is { Length: > 1 } li) blocks.Add(new(NoteBlockKind.Item, li, Math.Max(0, depth - 1))); break;
                }
                continue;
            }
            if (Plain(line) is not { Length: > 0 } text) continue;
            // A line written as a list item without the list tags ("- Fixed...", "• Fixed...").
            var dash = Regex.Match(text, @"^(?:[-–—•·▪■◦*]|\d{1,2}[.)])\s+(?<t>\S.{2,})$");
            if (dash.Success) blocks.Add(new(NoteBlockKind.Item, dash.Groups["t"].Value));
            else blocks.Add(new(NoteBlockKind.Paragraph, text));
        }
        return blocks;
    }

    /// <summary>The list items ([*]) of a stretch of BBCode as plain text, empty ones left out.</summary>
    static List<string> Items(string bb) =>
        Regex.Matches(bb, @"\[\*\](.*?)(?=\[\*\]|\[/\*\]|\[/?list\]|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(m => Plain(m.Groups[1].Value)).Where(s => s.Length > 2)
            .Select(s => s.Length > 160 ? s[..160].TrimEnd() + "…" : s).ToList();

    static string Plain(string bb)
    {
        string s = Regex.Replace(bb, @"\[/?[a-z0-9*]+([= ][^\]]*)?\]", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        return Clean(s);
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string Clean(string s) => Regex.Replace(System.Net.WebUtility.HtmlDecode(s), @"\s+", " ").Trim();
}
