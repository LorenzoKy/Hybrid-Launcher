using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

/// <summary>One feature of a PCGamingWiki table (HDR, ultrawide...): "true", "false", "limited", "hackable", "unknown"...</summary>
public sealed record PcgwFeature(string Label, string State, string Notes)
{
    public bool Yes => State is "true" or "always on";
    public bool No => State is "false";
    public bool Partial => State is "limited" or "hackable";
}

/// <summary>A subsection of the page ("Bad performance with DLSS"), its text as lines.</summary>
public sealed record PcgwTopic(string Title, List<RichLine> Body);

/// <summary>A file location from the "Game data" section, already turned into a Windows path (may hold * for unknown ids).</summary>
public sealed record PcgwPath(string Platform, string Path);

/// <summary>One language from the wiki's localizations table: "true", "false", "limited" or "" (unknown) for each part.</summary>
public sealed record PcgwLanguage(string Name, string Interface, string Audio, string Subtitles, string Notes, bool Fan);

/// <summary>What PCGamingWiki says about a game, read from its page's wikitext.</summary>
public sealed class PcgwInfo
{
    public required string Title { get; init; }
    public string Url => "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(Title.Replace(' ', '_'));

    public PcgwFeature? Upscaling { get; init; }
    /// <summary>"DLSS 4", "FSR 3.1", "XeSS 2.0"... as written on the wiki.</summary>
    public List<string> UpscalingTech { get; init; } = [];
    public PcgwFeature? FrameGen { get; init; }
    public List<string> FrameGenTech { get; init; } = [];
    public List<PcgwFeature> Video { get; init; } = [];
    public List<PcgwFeature> Input { get; init; } = [];
    public List<string> PlayStationModels { get; init; } = [];
    public PcgwFeature? AdaptiveTriggers { get; init; }
    public PcgwFeature? Haptics { get; init; }
    public List<string> Apis { get; init; } = [];

    public List<PcgwTopic> Improvements { get; init; } = [];
    public List<PcgwTopic> IssuesUnresolved { get; init; } = [];
    public List<PcgwTopic> IssuesFixed { get; init; } = [];

    public List<PcgwPath> ConfigPaths { get; init; } = [];
    public List<PcgwPath> SavePaths { get; init; } = [];

    /// <summary>Interface, full audio and subtitles per language (the "Localizations" table).</summary>
    public List<PcgwLanguage> Languages { get; init; } = [];

    /// <summary>The whole page, section by section (see <see cref="PcgwPage"/>).</summary>
    public List<PcgwSection> Page { get; init; } = [];

    /// <summary>The page's infobox (developers, engine, release dates, scores, taxonomy, links), shown beside the page.</summary>
    public PcgwInfobox? Infobox { get; init; }
}

/// <summary>
/// PCGamingWiki pages: found by Steam app id (or by exact name), cached as wikitext for two weeks, read into
/// <see cref="PcgwInfo"/>. Also gives the short upscaler tags drawn on the library covers.
/// </summary>
public static partial class Pcgw
{
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    // The library asks for many games at once: a couple of wiki requests at a time.
    static readonly SemaphoreSlim Gate = new(2, 2);

    /// <summary>When the wiki last refused us (403, rate limit): for a while only cached pages are used, asking again only makes it worse.</summary>
    static DateTime _refusedAt = DateTime.MinValue;
    static readonly TimeSpan RefusedPause = TimeSpan.FromMinutes(10);

    static string Key(string name, string? steamAppId) => steamAppId is not null ? $"app-{steamAppId}" : $"name-{Hash(name)}";
    static string Dir => Path.Combine(AppInfo.CacheDir, "pcgw");

    /// <summary>The page's title and wikitext, or null when the wiki has no page for the game.</summary>
    public static async Task<(string Title, string Wikitext)?> GetPageAsync(string name, string? steamAppId, CancellationToken ct = default)
    {
        var key = Key(name, steamAppId);
        var file = Path.Combine(Dir, $"wiki-{key}.txt");
        var none = file + ".none";
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge) return Read(file);
        if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromHours(12)) return null;

        await Gate.WaitAsync(ct);
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge) return Read(file);
            if (DateTime.UtcNow - _refusedAt < RefusedPause) return File.Exists(file) ? Read(file) : null;
            try
            {
                Directory.CreateDirectory(Dir);
                if (steamAppId is not null && await TitleFromAppIdAsync(steamAppId, ct) is { } byAppId)
                    return await TryPageAsync(byAppId, trusted: true) ?? Nothing();

                // By name, each step only when the one before found nothing (the wiki rate-limits): the store name and its
                // plain form as titles, then the wiki's search with each (see GameNames).
                var plain = GameNames.Plain(name);
                var tried = new HashSet<string>(StringComparer.Ordinal);
                Func<Task<List<string>>>[] steps =
                [
                    async () => await TitleFromExactNamesAsync([GameNames.Clean(name), plain], ct) is { } exact ? [exact] : [],
                    () => TitlesFromSearchAsync(name, ct),
                    () => plain is not null ? TitlesFromSearchAsync(plain, ct) : Task.FromResult(new List<string>()),
                ];
                foreach (var step in steps)
                    foreach (var title in await step())
                        if (tried.Count < 5 && tried.Add(title) && await TryPageAsync(title, trusted: false) is { } page) return page;
                return Nothing();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests }) _refusedAt = DateTime.UtcNow;
                Log.Warn($"PCGamingWiki lookup for {name}: {ex.Message}");
                return File.Exists(file) ? Read(file) : null; // offline: a stale page beats nothing
            }

            (string, string)? Nothing()
            {
                File.WriteAllText(none, "");
                return null;
            }

            // A page found by name is the game only if it lists the game's Steam app id (when it has one and lists any):
            // "DOOM" by name is the 1993 page, the 2016 game is "Doom (2016)".
            async Task<(string, string)?> TryPageAsync(string title, bool trusted)
            {
                var json = await Http.GetCachedAsync(
                    $"https://www.pcgamingwiki.com/w/api.php?action=parse&page={Uri.EscapeDataString(title)}&prop=wikitext&format=json&formatversion=2&redirects=1",
                    Path.Combine("pcgw", $"page-{key}.json"), TimeSpan.Zero, ct: ct);
                try { File.Delete(Path.Combine(Dir, $"page-{key}.json")); } catch { }
                if (json is null) throw new HttpRequestException("PCGamingWiki page failed", null, System.Net.HttpStatusCode.Forbidden);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("parse", out var parse) || !parse.TryGetProperty("wikitext", out var wt)) return null;
                var text = wt.GetString() ?? "";
                if (!trusted)
                {
                    // A page without the game infobox lists the games by that name, it is not one.
                    if (!text.Contains("{{Infobox game", StringComparison.OrdinalIgnoreCase)) return null;
                    if (steamAppId is not null && SteamAppIds(text) is { Count: > 0 } ids && !ids.Contains(steamAppId))
                    {
                        Log.Info($"PCGamingWiki: \"{title}\" is another game than {name} (Steam {steamAppId})");
                        return null;
                    }
                }
                var finalTitle = parse.TryGetProperty("title", out var t) ? t.GetString() ?? title : title;
                var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(tmp, finalTitle + "\n" + text, ct);
                File.Move(tmp, file, overwrite: true);
                return (finalTitle, text);
            }
        }
        finally
        {
            Gate.Release();
        }

        static (string, string) Read(string file)
        {
            var text = File.ReadAllText(file);
            var nl = text.IndexOf('\n');
            return nl < 0 ? (text, "") : (text[..nl], text[(nl + 1)..]);
        }
    }

    /// <summary>The page read into <see cref="PcgwInfo"/> (paths resolved against <paramref name="gameDir"/>). Null when there is no page.</summary>
    public static async Task<PcgwInfo?> GetAsync(Game game, string? steamAppId, CancellationToken ct = default)
    {
        try
        {
            var page = await GetPageAsync(GameNames.LookupName(game), steamAppId, ct);
            if (page is null) return null;
            return await Task.Run(() => Parse(page.Value.Title, page.Value.Wikitext, game.InstallDir), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"PCGamingWiki for {game.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Short upscaler tags for a cover ("DLSS 4", "FSR 3", "XeSS 2"): the newest version of each family the wiki lists.
    /// Cached on their own so the library does not read whole pages each time.
    /// </summary>
    public static Task<List<string>> GetUpscalerTagsAsync(Game game, string? steamAppId, CancellationToken ct = default)
    {
        // The grid tiles and the library's sort ask for the same game at once: one read / download per game at a time.
        var key = Key(game.Name, steamAppId);
        lock (TagsInFlight)
        {
            if (TagsInFlight.TryGetValue(key, out var running) && !running.IsCompleted) return running;
            var task = WithFilesAsync(game, ReadTagsAsync(game, steamAppId, key, ct));
            TagsInFlight[key] = task;
            return task;
        }
    }

    static readonly Dictionary<string, Task<List<string>>> TagsInFlight = new();

    /// <summary>The wiki's tags with the versions the game's own libraries carry (see <see cref="UpscalerFiles.Merge"/>).</summary>
    static async Task<List<string>> WithFilesAsync(Game game, Task<List<string>> wiki)
    {
        var tags = await wiki;
        try { return UpscalerFiles.Merge(tags, await Task.Run(() => UpscalerFiles.Scan(game.InstallDir))); }
        catch (Exception ex) { Log.Warn($"Upscaler files for {game.Name}: {ex.Message}"); return tags; }
    }

    /// <summary>Upscalers then frame generation ("DLSS 4", "FSR 3", "DLSS MFG", "FSR FG").</summary>
    static async Task<List<string>> ReadTagsAsync(Game game, string? steamAppId, string key, CancellationToken ct)
    {
        var file = Path.Combine(Dir, $"tags3-{key}.txt");
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge)
                return (await File.ReadAllTextAsync(file, ct)).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            var page = await GetPageAsync(game.Name, steamAppId, ct);
            var tags = page is null ? [] : UpscalerTags(Field(page.Value.Wikitext, "upscaling tech"), GraphicsApi.ParseWikitext(page.Value.Wikitext))
                .Concat(FrameGenTags(SplitList(Field(page.Value.Wikitext, "framegen tech")))).Distinct().ToList();
            Directory.CreateDirectory(Dir);
            var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, string.Join(",", tags), ct);
            File.Move(tmp, file, overwrite: true);
            return tags;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Upscaler tags for {game.Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// "DLSS 4, FSR 2.1, FSR 3.1, XeSS 1.3, XeSS 2.0, MetalFX" → ["DLSS 4", "FSR 3.1", "XeSS 2"]. With <paramref name="apis"/> holding
    /// DX12, FSR 3.1 reads "FSR 4": its DLL is swappable, so AMD's driver (and OptiScaler) run FSR 4 in its place, and the wiki rarely says so.
    /// </summary>
    public static List<string> UpscalerTags(string tech, IReadOnlyCollection<string>? apis = null)
    {
        var best = new Dictionary<string, (double Version, string Text)>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in tech.Split([',', '/', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(part.Trim(), @"^(DLSS|FSR|XeSS|TSR|PSSR)\s*([0-9]+(?:\.[0-9]+)?)?", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var family = m.Groups[1].Value.ToUpperInvariant() switch { "XESS" => "XeSS", var f => f };
            double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v);
            var version = m.Groups[2].Value.EndsWith(".0") ? m.Groups[2].Value[..^2] : m.Groups[2].Value;
            if (!best.TryGetValue(family, out var cur) || v > cur.Version) best[family] = (v, version.Length > 0 ? $"{family} {version}" : family);
        }
        if (apis is not null && apis.Contains("DX12") && best.TryGetValue("FSR", out var fsr) && fsr.Version >= 3.1 && fsr.Version < 4) best["FSR"] = (4, "FSR 4");
        string[] order = ["DLSS", "FSR", "XeSS", "TSR", "PSSR"];
        return order.Where(best.ContainsKey).Select(f => best[f].Text).ToList();
    }

    /// <summary>"DLSS FG, DLSS MFG, fsr fg, xess fg, metalfx fg" → ["DLSS MFG", "FSR FG", "XeSS FG"] (multi frame gen wins over plain).</summary>
    public static List<string> FrameGenTags(IEnumerable<string> tech)
    {
        var found = new HashSet<string>();
        foreach (var raw in tech)
        {
            var t = raw.Trim().ToLowerInvariant();
            if (t.StartsWith("dlss")) found.Add(t.Contains("mfg") || t.Contains("multi") ? "DLSS MFG" : "DLSS FG");
            else if (t.StartsWith("fsr") || t.StartsWith("amd")) found.Add("FSR FG");
            else if (t.StartsWith("xess") || t.StartsWith("xefg")) found.Add("XeSS FG");
        }
        if (found.Contains("DLSS MFG")) found.Remove("DLSS FG");
        string[] order = ["DLSS MFG", "DLSS FG", "FSR FG", "XeSS FG"];
        return order.Where(found.Contains).ToList();
    }

    // ---------- parsing ----------

    [GeneratedRegex(@"^==\s*([^=].*?)\s*==\s*$", RegexOptions.Multiline)]
    private static partial Regex Level2();

    [GeneratedRegex(@"^===\s*([^=].*?)\s*===\s*$", RegexOptions.Multiline)]
    private static partial Regex Level3();

    public static PcgwInfo Parse(string title, string wikitext, string? gameDir)
    {
        var clean = Regex.Replace(wikitext, @"<ref[^>]*/>|<ref[^>]*>.*?</ref>|<!--.*?-->", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        PcgwFeature? F(string field, string label)
        {
            var value = Field(clean, field).Trim().ToLowerInvariant();
            if (value.Length == 0) return null;
            var notes = WikiText.Plain(Field(clean, field + " notes"), gameDir);
            return new PcgwFeature(label, value, notes);
        }
        List<PcgwFeature> All(params (string Field, string Label)[] fields) =>
            fields.Select(f => F(f.Field, f.Label)).Where(f => f is not null).Select(f => f!).ToList();

        var sections = Sections(clean);
        return new PcgwInfo
        {
            Title = title,
            Upscaling = F("upscaling", "Upscaling"),
            UpscalingTech = SplitList(Field(clean, "upscaling tech")),
            FrameGen = F("framegen", "Frame generation"),
            FrameGenTech = SplitList(Field(clean, "framegen tech")),
            Video = All(("hdr", "HDR"), ("ultrawidescreen", "Ultra-widescreen"), ("4k ultra hd", "4K Ultra HD"), ("120 fps", "High frame rate"),
                ("ray tracing", "Ray tracing"), ("borderless windowed", "Borderless windowed"), ("fov", "Field of view"),
                ("vsync", "V-Sync"), ("anisotropic", "Anisotropic filtering"), ("antialiasing", "Anti-aliasing"), ("color blind", "Color blind mode")),
            Input = All(("controller support", "Controller support"), ("full controller", "Full controller support"), ("xinput controllers", "Xbox controllers"),
                ("playstation controllers", "PlayStation controllers"), ("playstation prompts", "PlayStation button prompts"),
                ("playstation motion sensors", "Motion sensors (gyro)"), ("playstation speaker", "Controller speaker"),
                ("steam input api", "Steam Input"), ("controller remap", "Controller remapping"), ("key remap", "Keyboard remapping")),
            PlayStationModels = SplitList(Field(clean, "playstation controller models")),
            AdaptiveTriggers = F("dualsense adaptive trigger support", "Adaptive triggers"),
            Haptics = F("dualsense haptics support", "Haptic feedback"),
            Apis = GraphicsApi.ParseWikitext(wikitext),
            Improvements = Topics(sections, "Essential improvements", gameDir),
            IssuesUnresolved = Topics(sections, "Issues unresolved", gameDir),
            IssuesFixed = Topics(sections, "Issues fixed", gameDir),
            ConfigPaths = GameData(clean, "config", gameDir),
            SavePaths = GameData(clean, "saves", gameDir),
            Languages = Languages(clean),
            Page = WholePage(title, wikitext, gameDir),
            Infobox = SafeInfobox(clean, gameDir),
        };
    }

    static PcgwInfobox? SafeInfobox(string wikitext, string? gameDir)
    {
        try { return PcgwInfobox.Parse(wikitext, gameDir); }
        catch (Exception ex)
        {
            Log.Warn($"PCGamingWiki infobox: {ex.Message}");
            return null;
        }
    }

    /// <summary>The whole page for the PCGamingWiki tab. A page it cannot read leaves that tab empty, never the banner's tags or folders.</summary>
    static List<PcgwSection> WholePage(string title, string wikitext, string? gameDir)
    {
        try
        {
            return PcgwPage.Parse(wikitext, gameDir);
        }
        catch (Exception ex)
        {
            Log.Warn($"PCGamingWiki page {title}: {ex.Message}");
            return [];
        }
    }

    /// <summary>The {{L10n/switch |language = ... |interface = ... |audio = ... |subtitles = ...}} rows.</summary>
    static List<PcgwLanguage> Languages(string wikitext)
    {
        var list = new List<PcgwLanguage>();
        int at = 0;
        while ((at = wikitext.IndexOf("{{L10n/switch", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = wikitext.IndexOf("}}", at, StringComparison.Ordinal);
            if (end < 0) break;
            // Rows are written " |language = English": the bars sit after a space.
            var block = Regex.Replace(wikitext[at..end].Replace("\r", ""), @"(?m)^[ \t]+\|", "|");
            at = end;
            string F(string field) => Field(block, field).Trim().ToLowerInvariant();
            var name = WikiText.Plain(Field(block, "language")).Trim();
            if (name.Length == 0) continue;
            list.Add(new PcgwLanguage(name, F("interface"), F("audio"), F("subtitles"), WikiText.Plain(Field(block, "notes")), F("fan") == "true"));
        }
        return list;
    }

    /// <summary>A template field's raw value ("|hdr = limited"), empty when missing.</summary>
    public static string Field(string wikitext, string field)
    {
        var m = Regex.Match(wikitext, @"^\|\s*" + Regex.Escape(field) + @"\s*=([^\n]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    static List<string> SplitList(string value) =>
        WikiText.Plain(value).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0).ToList();

    /// <summary>Level-2 sections by plain title ("Issues fixed" → its body).</summary>
    static Dictionary<string, string> Sections(string wikitext)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = Level2().Matches(wikitext);
        for (int i = 0; i < matches.Count; i++)
        {
            var name = WikiText.Plain(matches[i].Groups[1].Value);
            int start = matches[i].Index + matches[i].Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : wikitext.Length;
            result.TryAdd(name, wikitext[start..end]);
        }
        return result;
    }

    static List<PcgwTopic> Topics(Dictionary<string, string> sections, string name, string? gameDir)
    {
        if (!sections.TryGetValue(name, out var body)) return [];
        var topics = new List<PcgwTopic>();
        var heads = Level3().Matches(body);
        // Text before the first subsection is a topic of its own (rare).
        var intro = heads.Count > 0 ? body[..heads[0].Index] : body;
        var introLines = WikiText.ToLines(intro, gameDir).Where(l => !l.Heading).ToList();
        if (introLines.Count > 0) topics.Add(new PcgwTopic(name, introLines));
        for (int i = 0; i < heads.Count; i++)
        {
            int start = heads[i].Index + heads[i].Length;
            int end = i + 1 < heads.Count ? heads[i + 1].Index : body.Length;
            var lines = WikiText.ToLines(body[start..end], gameDir);
            topics.Add(new PcgwTopic(WikiText.Plain(heads[i].Groups[1].Value), lines));
        }
        return topics;
    }

    /// <summary>{{Game data/config|Windows|path|path}} entries, as Windows paths.</summary>
    static List<PcgwPath> GameData(string wikitext, string kind, string? gameDir)
    {
        var result = new List<PcgwPath>();
        var marker = "{{Game data/" + kind + "|";
        int at = 0;
        while ((at = wikitext.IndexOf(marker, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            // The template's end: its braces balance again.
            int depth = 0, end = -1;
            for (int i = at; i < wikitext.Length - 1; i++)
            {
                if (wikitext[i] == '{' && wikitext[i + 1] == '{') { depth++; i++; }
                else if (wikitext[i] == '}' && wikitext[i + 1] == '}') { depth--; i++; if (depth == 0) { end = i + 1; break; } }
            }
            if (end < 0) break;
            var inner = wikitext[(at + 2)..(end - 2)];
            at = end;
            // Split at top-level bars only (the {{P|...}} placeholders have their own).
            var parts = new List<string>();
            int d = 0, s = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '{') d++;
                else if (inner[i] == '}') d--;
                else if (inner[i] == '|' && d == 0) { parts.Add(inner[s..i]); s = i + 1; }
            }
            parts.Add(inner[s..]);
            if (parts.Count < 3) continue;
            var platform = parts[1].Trim();
            if (platform is "OS X" or "Linux" or "macOS") continue;
            foreach (var raw in parts.Skip(2))
            {
                var path = WikiText.ExpandTemplates(raw, gameDir).Trim().Replace('/', '\\');
                // An unknown placeholder (<hkcu>, <osxhome>...) means a path this PC cannot have.
                if (path.Length > 3 && !path.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase) && !path.Contains("<"))
                    result.Add(new PcgwPath(platform, path));
            }
        }
        return result;
    }

    /// <summary>
    /// The first of <paramref name="paths"/> that exists on this PC (wildcards for unknown ids resolved), preferring the
    /// game's own store. Returns a folder; a file path gives its folder.
    /// </summary>
    public static string? ExistingFolder(IEnumerable<PcgwPath> paths, GameStore store)
    {
        string[] preferred = store switch
        {
            GameStore.Steam => ["Steam", "Windows"],
            GameStore.Xbox => ["Microsoft Store", "Xbox", "Windows"],
            GameStore.Epic => ["Epic Games Store", "Epic Games Launcher", "Windows"],
            GameStore.Gog => ["GOG.com", "Windows"],
            _ => ["Windows"],
        };
        var ordered = paths.OrderBy(p => Array.FindIndex(preferred, x => p.Platform.Equals(x, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : 99);
        foreach (var p in ordered)
        {
            try
            {
                if (Resolve(p.Path) is { } found) return found;
            }
            catch (Exception ex) { Log.Warn($"Game data path {p.Path}: {ex.Message}"); }
        }
        return null;
    }

    static string? Resolve(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].EndsWith(':') && !path.StartsWith(@"\\")) return null;
        var candidates = new List<string> { parts[0] + "\\" };
        for (int i = 1; i < parts.Length; i++)
        {
            var next = new List<string>();
            bool last = i == parts.Length - 1;
            foreach (var c in candidates)
            {
                if (!Directory.Exists(c)) continue;
                if (parts[i].Contains('*') || parts[i].Contains('?'))
                {
                    next.AddRange(Directory.EnumerateDirectories(c, parts[i]).Take(8));
                    if (last && next.Count == 0 && Directory.EnumerateFiles(c, parts[i]).Any()) return c;
                }
                else
                {
                    var p = Path.Combine(c, parts[i]);
                    if (Directory.Exists(p)) next.Add(p);
                    else if (last && File.Exists(p)) return c;
                }
            }
            candidates = next;
            if (candidates.Count == 0) return null;
        }
        return candidates.FirstOrDefault(Directory.Exists);
    }

    // ---------- page lookup ----------

    /// <summary>
    /// Cloudflare answers the wiki's app id redirect (and its /wiki/ pages) with a browser check that apps cannot pass, while
    /// the MediaWiki API stays open. Once refused, the app id is skipped for the session and the game is found by name.
    /// </summary>
    static bool _appIdBlocked;

    static async Task<string?> TitleFromAppIdAsync(string appId, CancellationToken ct)
    {
        if (_appIdBlocked) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.Client.GetAsync($"https://www.pcgamingwiki.com/api/appid.php?appid={appId}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var final = resp.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        const string prefix = "/wiki/";
        // The browser check is not the API refusing us: it must not pause the wiki, the name lookups still work.
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _appIdBlocked = true;
            Log.Info("PCGamingWiki app id lookup is behind a browser check: pages are found by name");
            return null;
        }
        // Refused or failing (rate limit, server error): not an answer, so it must not be remembered as "no page".
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 404) resp.EnsureSuccessStatusCode();
        if (!resp.IsSuccessStatusCode || !final.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var title = Uri.UnescapeDataString(final[prefix.Length..]);
        return title.StartsWith("Special:", StringComparison.OrdinalIgnoreCase) ? null : title;
    }

    /// <summary>The first of the names that is a wiki page (or redirects to one): "Batman: Arkham Knight" is its own title.</summary>
    static async Task<string?> TitleFromExactNamesAsync(IEnumerable<string?> names, CancellationToken ct)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return null;
        var joined = string.Join("|", list);
        var json = await Http.GetCachedAsync(
            $"https://www.pcgamingwiki.com/w/api.php?action=query&titles={Uri.EscapeDataString(joined)}&redirects=1&format=json&formatversion=2",
            Path.Combine("pcgw", $"titles-{Hash(joined)}.json"), TimeSpan.FromDays(30), ct: ct);
        if (json is null) throw new HttpRequestException("PCGamingWiki title lookup failed", null, System.Net.HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var q)) return null;
        // Each asked name is first normalized ("batman: …" → "Batman: …"), then followed through a redirect.
        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in new[] { "normalized", "redirects" })
            if (q.TryGetProperty(step, out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.GetProperty("from").GetString() is { } from && m.GetProperty("to").GetString() is { } to) moves[from] = to;
        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (q.TryGetProperty("pages", out var pages))
            foreach (var p in pages.EnumerateArray())
                if (!p.TryGetProperty("missing", out _) && !p.TryGetProperty("invalid", out _) && p.GetProperty("title").GetString() is { } t) existing.Add(t);
        foreach (var name in list)
        {
            var t = name;
            for (int i = 0; i < 3 && moves.TryGetValue(t, out var next); i++) t = next;
            if (existing.Contains(t)) return t;
        }
        return null;
    }

    /// <summary>The Steam app ids the page's infobox lists ("|steam appid = 208650", "|steam appid side = 1, 2").</summary>
    internal static HashSet<string> SteamAppIds(string wikitext)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in SteamAppIdField().Matches(wikitext))
            foreach (Match id in Regex.Matches(m.Groups[1].Value, @"\d+")) ids.Add(id.Value);
        return ids;
    }

    [GeneratedRegex(@"^\|\s*steam appid(?: side)?\s*=\s*([^\n|}]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SteamAppIdField();

    static async Task<List<string>> TitlesFromSearchAsync(string name, CancellationToken ct)
    {
        // Store names carry marks the wiki's titles do not ("Batman™: Arkham Knight"): the search finds nothing with them.
        name = GameNames.Clean(name);
        var found = new List<string>();
        // Titles starting with the name first: the text search ranks "Dr. Doom's Revenge!" above "Doom (2016)".
        foreach (var (list, query) in new[] { ("prefixsearch", $"pssearch={Uri.EscapeDataString(name)}&pslimit=10"), ("search", $"srsearch={Uri.EscapeDataString(name)}&srlimit=5") })
        {
            var json = await Http.GetCachedAsync(
                $"https://www.pcgamingwiki.com/w/api.php?action=query&list={list}&{query}&format=json&formatversion=2",
                Path.Combine("pcgw", $"{list}-{Hash(name)}.json"), TimeSpan.FromDays(30), ct: ct);
            // No answer (refused, offline) is not "no page": it must not be remembered as one.
            if (json is null) throw new HttpRequestException("PCGamingWiki search failed", null, System.Net.HttpStatusCode.Forbidden);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty(list, out var hits)) continue;
            foreach (var hit in hits.EnumerateArray())
            {
                // A loose match would show another game's page.
                var title = hit.GetProperty("title").GetString();
                if (title is not null && GameNames.SameGame(name, title)) found.Add(title);
            }
        }
        return found;
    }

    internal static string Hash(string s) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..12];
}
