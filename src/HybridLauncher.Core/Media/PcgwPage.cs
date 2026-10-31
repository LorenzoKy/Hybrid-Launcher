using System.Text.RegularExpressions;

namespace HybridLauncher.Core.Media;

/// <summary>One setting or fact of a PCGamingWiki table: a state ("true", "limited"...) or a plain value, with the wiki's notes.</summary>
public sealed record PcgwField(string Label, string State, string Value, string Notes)
{
    public bool HasState => State.Length > 0;

    /// <summary>The value and the notes as the wiki writes them, links kept (the plain words above are for search and summaries).</summary>
    public List<RichLine> ValueLines { get; init; } = [];
    public List<RichLine> NoteLines { get; init; } = [];
}

public enum PcgwBlockKind { Text, Heading, Fields, Table, Fix }

/// <summary>A piece of a page section: text, a sub-heading, a table of features, a table of rows, or a fix box.</summary>
public sealed class PcgwBlock
{
    public required PcgwBlockKind Kind { get; init; }
    public string Title { get; init; } = "";
    public List<RichLine> Lines { get; init; } = [];
    public List<PcgwField> Fields { get; init; } = [];
    public List<string> Columns { get; init; } = [];
    public List<List<string>> Rows { get; init; } = [];

    /// <summary>The same cells as <see cref="Rows"/> with their links (a store page, a mod, a wiki article), one line each.</summary>
    public List<List<RichLine>> RichRows { get; init; } = [];

    /// <summary>All the words of the block, for the tab's search.</summary>
    public string SearchText => string.Join(" ", new[] { Title }
        .Concat(Lines.Select(l => l.Text))
        .Concat(Fields.Select(f => $"{f.Label} {f.State} {f.Value} {f.Notes}"))
        .Concat(Rows.SelectMany(r => r)));
}

/// <summary>A level-2 section of the page ("Video", "Availability", "Issues fixed"...) with everything it holds.</summary>
public sealed record PcgwSection(string Title, List<PcgwBlock> Blocks);

/// <summary>
/// The whole PCGamingWiki page read into sections, so the tab can show all of it (availability, DLC, game data, every video,
/// input, audio, network and VR setting, issues, APIs, middleware, system requirements...) and not only a summary. The wiki's
/// templates become tables: feature templates ({{Video|hdr = true|hdr notes = ...}}) a list of states with their notes,
/// row templates ({{Availability/row|...}}, {{L10n/switch|...}}) a table, {{Fixbox}} a fix.
/// </summary>
public static partial class PcgwPage
{
    static readonly HashSet<string> States = new(StringComparer.OrdinalIgnoreCase)
        { "true", "false", "limited", "hackable", "unknown", "n/a", "always on", "partial", "complete", "deprecated" };

    // Templates written inside a sentence ({{ii}}, {{P|game}}...): part of the text, never a block.
    static readonly HashSet<string> Inline = new(StringComparer.OrdinalIgnoreCase)
        { "++", "--", "ii", "mm", "+", "-", "p", "file", "folder", "code", "key", "term", "glossary", "note", "cn", "citation needed",
          "refcheck", "refurl", "refsnippet", "dead link", "update", "store link", "steam", "gog.com", "hover" };

    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { "image", "screenshot", "state", "series", "disambig", "stub", "cleanup", "dirtcheap", "infobox game/row/reception", "infobox game" };

    [GeneratedRegex(@"^==\s*([^=].*?)\s*==\s*$", RegexOptions.Multiline)]
    private static partial Regex Level2();

    public static List<PcgwSection> Parse(string wikitext, string? gameDir)
    {
        var clean = Regex.Replace(wikitext, @"<ref[^>]*/>|<ref[^>]*>.*?</ref>|<!--.*?-->|<gallery[^>]*>.*?</gallery>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Replace("\r", "");
        var sections = new List<PcgwSection>();
        var heads = Level2().Matches(clean);
        // The lead (before the first section): the infobox and the introduction.
        var lead = Blocks(heads.Count > 0 ? clean[..heads[0].Index] : clean, gameDir);
        if (lead.Count > 0) sections.Add(new PcgwSection(L.T("Introduction"), lead));
        for (int i = 0; i < heads.Count; i++)
        {
            int start = heads[i].Index + heads[i].Length;
            int end = i + 1 < heads.Count ? heads[i + 1].Index : clean.Length;
            var blocks = Blocks(clean[start..end], gameDir);
            if (blocks.Count > 0) sections.Add(new PcgwSection(WikiText.Plain(heads[i].Groups[1].Value), blocks));
        }
        return sections;
    }

    static List<PcgwBlock> Blocks(string body, string? gameDir)
    {
        var blocks = new List<PcgwBlock>();
        var text = new System.Text.StringBuilder();
        void FlushText()
        {
            if (text.Length == 0) return;
            AddText(blocks, WikiText.ToLines(text.ToString(), gameDir));
            text.Clear();
        }

        int at = 0;
        while (at < body.Length)
        {
            int open = body.IndexOf("{{", at, StringComparison.Ordinal);
            if (open < 0) { text.Append(body, at, body.Length - at); break; }
            int close = Matching(body, open);
            if (close < 0) { text.Append(body, at, body.Length - at); break; }
            var inner = body[(open + 2)..close];
            var (name, args) = WikiText.Split(inner);
            bool lineStart = open == 0 || body[open - 1] == '\n';
            if (!lineStart || Inline.Contains(name) || name.Length == 0)
            {
                text.Append(body, at, close + 2 - at);
                at = close + 2;
                continue;
            }
            text.Append(body, at, open - at);
            at = close + 2;
            if (Skip.Contains(name)) continue;
            FlushText();
            if (Block(name, args, gameDir) is { } block) blocks.Add(block);
        }
        FlushText();
        return blocks;
    }

    /// <summary>Lines of text as text blocks, with the sub-headings (=== ... ===) as blocks of their own.</summary>
    static void AddText(List<PcgwBlock> blocks, List<RichLine> lines)
    {
        var run = new List<RichLine>();
        foreach (var line in lines)
        {
            if (line.Heading)
            {
                if (run.Count > 0) blocks.Add(new PcgwBlock { Kind = PcgwBlockKind.Text, Lines = run });
                run = [];
                blocks.Add(new PcgwBlock { Kind = PcgwBlockKind.Heading, Title = line.Text });
            }
            else run.Add(line);
        }
        if (run.Count > 0) blocks.Add(new PcgwBlock { Kind = PcgwBlockKind.Text, Lines = run });
    }

    /// <summary>The index of the "}}" closing the template that opens at <paramref name="open"/>, or -1.</summary>
    static int Matching(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length - 1; i++)
        {
            if (s[i] == '{' && s[i + 1] == '{') { depth++; i++; }
            else if (s[i] == '}' && s[i + 1] == '}')
            {
                depth--;
                if (depth == 0) return i;
                i++;
            }
        }
        return -1;
    }

    static PcgwBlock? Block(string name, List<string> args, string? gameDir)
    {
        var key = name.ToLowerInvariant();
        if (key == "fixbox")
        {
            var lines = WikiText.ToLines(Named(args, "fix"), gameDir);
            return new PcgwBlock { Kind = PcgwBlockKind.Fix, Title = WikiText.Plain(Named(args, "description"), gameDir), Lines = lines };
        }
        if (key == "system requirements") return Requirements(args, gameDir);
        if (key == "introduction")
        {
            var lines = new List<RichLine>();
            foreach (var part in new[] { "introduction", "release history", "current state" })
                lines.AddRange(WikiText.ToLines(Named(args, part), gameDir));
            return lines.Count > 0 ? new PcgwBlock { Kind = PcgwBlockKind.Text, Lines = lines } : null;
        }

        // A container of row templates ({{Availability| {{Availability/row|...}} }}): one table row each.
        var rows = Nested(args, name);
        if (rows.Count > 0) return Table(name, rows, gameDir);

        var named = args.Select(a => (Eq: a.IndexOf('='), Arg: a)).Where(a => a.Eq > 0).ToList();
        if (named.Count == 0)
        {
            var plain = WikiText.Plain("{{" + string.Join("|", new[] { name }.Concat(args)) + "}}", gameDir);
            return plain.Length > 0 ? new PcgwBlock { Kind = PcgwBlockKind.Text, Lines = WikiText.ToLines(plain, gameDir) } : null;
        }

        // A template can name a field twice (two "date" rows): the first one wins.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in named) values.TryAdd(a.Arg[..a.Eq].Trim().ToLowerInvariant(), a.Arg[(a.Eq + 1)..].Trim());
        var fields = new List<PcgwField>();
        foreach (var (k, raw) in values)
        {
            if (k.EndsWith(" notes", StringComparison.Ordinal) || raw.Length == 0) continue;
            var value = WikiText.Plain(raw, gameDir);
            if (value.Length == 0) continue;
            var rawNotes = values.TryGetValue(k + " notes", out var n) ? n : "";
            var notes = WikiText.Plain(rawNotes, gameDir);
            bool state = States.Contains(value);
            fields.Add(new PcgwField(Label(k), state ? value.ToLowerInvariant() : "", state ? "" : value, notes)
            {
                ValueLines = state ? [] : WikiText.ToLines(raw, gameDir),
                NoteLines = rawNotes.Length > 0 ? WikiText.ToLines(rawNotes, gameDir) : [],
            });
        }
        return fields.Count > 0 ? new PcgwBlock { Kind = PcgwBlockKind.Fields, Title = Label(name), Fields = fields } : null;
    }

    static string Named(List<string> args, string name)
    {
        foreach (var a in args)
        {
            int eq = a.IndexOf('=');
            if (eq > 0 && a[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return a[(eq + 1)..].Trim();
        }
        return "";
    }

    /// <summary>The row templates inside a container's arguments: those named after it ("Availability/row", "L10n/switch").</summary>
    static List<(string Name, List<string> Args)> Nested(List<string> args, string container)
    {
        var rows = new List<(string, List<string>)>();
        var prefix = container.Split('/')[0] + "/";
        foreach (var arg in args)
        {
            int at = 0;
            while ((at = arg.IndexOf("{{", at, StringComparison.Ordinal)) >= 0)
            {
                int close = Matching(arg, at);
                if (close < 0) break;
                var (n, a) = WikiText.Split(arg[(at + 2)..close]);
                if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !Skip.Contains(n)) rows.Add((n, a));
                at = close + 2;
            }
        }
        return rows;
    }

    // Column names of the row templates whose cells are positional.
    static readonly Dictionary<string, string[]> RowColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["availability/row"] = ["Store", "", "DRM", "Notes", "Keys", "OS"],
        ["dlc/row"] = ["DLC", "Notes", "OS"],
        ["game data/config"] = ["Platform", "Location"],
        ["game data/saves"] = ["Platform", "Location"],
    };

    static PcgwBlock Table(string container, List<(string Name, List<string> Args)> rows, string? gameDir)
    {
        bool idColumns = false;
        var columns = new List<string>();
        var table = new List<List<string>>();
        if (rows.All(r => r.Args.Count > 0 && r.Args.All(a => a.IndexOf('=') > 0)))
        {
            // {{L10n/switch |language = ... |interface = ...}}: a column per field, in the order they first appear.
            foreach (var r in rows)
                foreach (var a in r.Args)
                {
                    var k = a[..a.IndexOf('=')].Trim().ToLowerInvariant();
                    if (!columns.Contains(k)) columns.Add(k);
                }
            foreach (var r in rows) table.Add(columns.Select(c => Named(r.Args, c)).ToList());
            columns = columns.Select(Label).ToList();
        }
        else
        {
            // Positional rows; mixed row kinds (the infobox: developer, engine, release date...) show their kind first.
            bool mixed = rows.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            int width = rows.Max(r => r.Args.Count) + (mixed ? 1 : 0);
            RowColumns.TryGetValue(rows[0].Name, out var known);
            // The known row kinds name every column worth reading; their unnamed ones are store ids, never shown.
            idColumns = !mixed && known is not null;
            for (int i = 0; i < width; i++) columns.Add(!mixed && known is not null && i < known.Length ? known[i] : "");
            foreach (var r in rows)
            {
                var cells = new List<string>();
                if (mixed) cells.Add(Label(r.Name.Split('/').Last()));
                cells.AddRange(r.Args.Select(a => a.IndexOf('=') is > 0 and var eq && !a[..eq].Contains("{{") ? a[(eq + 1)..] : a));
                while (cells.Count < width) cells.Add("");
                table.Add(cells);
            }
        }
        // The cells hold wikitext: read as plain words (search, sizes) and as lines that keep their links.
        var plain = table.Select(r => r.Select(c => Cell(c, gameDir)).ToList()).ToList();
        // Columns empty in every row (unused fields, ids) are left out.
        var keep = Enumerable.Range(0, columns.Count).Where(i => plain.Any(r => i < r.Count && r[i].Length > 0) && !(idColumns && columns[i].Length == 0)).ToList();
        var shown = Enumerable.Range(0, table.Count).Where(n => keep.Any(i => i < plain[n].Count && plain[n][i].Length > 0)).ToList();
        return new PcgwBlock
        {
            Kind = PcgwBlockKind.Table,
            Title = Label(container),
            Columns = keep.Select(i => columns[i]).ToList(),
            Rows = shown.Select(n => keep.Select(i => i < plain[n].Count ? plain[n][i] : "").ToList()).ToList(),
            RichRows = shown.Select(n => keep.Select(i => i < table[n].Count ? CellLine(table[n][i], plain[n][i], gameDir) : new RichLine()).ToList()).ToList(),
        };
    }

    /// <summary>A cell as one line with its links; a state word stays its sign (✔, ✘...).</summary>
    static RichLine CellLine(string raw, string plain, string? gameDir)
    {
        var line = new RichLine();
        var lines = WikiText.ToLines(raw, gameDir);
        if (plain != string.Join(" ", lines.Select(l => l.Text)).Trim())
        {
            if (plain.Length > 0) line.Runs.Add(new RichRun(plain));
            return line;
        }
        foreach (var l in lines)
        {
            if (line.Runs.Count > 0) line.Runs.Add(new RichRun(" "));
            line.Runs.AddRange(l.Runs);
        }
        return line;
    }

    /// <summary>A table cell: plain text, a state word as a sign ("true" → ✔).</summary>
    static string Cell(string raw, string? gameDir)
    {
        var s = WikiText.Plain(raw, gameDir);
        return s.ToLowerInvariant() switch
        {
            "true" => "✔",
            "false" => "✘",
            "limited" or "partial" => "◐",
            "hackable" => "🔧",
            "unknown" => "?",
            _ => s,
        };
    }

    /// <summary>{{System requirements |minOS = ... |recOS = ...}}: one row per part, minimum and recommended side by side.</summary>
    static PcgwBlock? Requirements(List<string> args, string? gameDir)
    {
        var parts = new List<(string Part, string Min, string Rec)>();
        foreach (var (suffix, label) in new[] { ("OS", "OS"), ("CPU", "CPU"), ("RAM", "Memory"), ("HD", "Storage"), ("GPU", "GPU"), ("VRAM", "VRAM"), ("DX", "DirectX"), ("OGL", "OpenGL"), ("audio", "Audio"), ("other", "Other") })
        {
            string Both(string tier) => string.Join(" / ", new[] { Named(args, tier + suffix), Named(args, tier + suffix + "2"), Named(args, tier + suffix + "3") }
                .Select(v => WikiText.Plain(v, gameDir)).Where(v => v.Length > 0));
            var (min, rec) = (Both("min"), Both("rec"));
            if (min.Length + rec.Length > 0) parts.Add((label, min, rec));
        }
        if (parts.Count == 0) return null;
        var family = WikiText.Plain(Named(args, "OSfamily"), gameDir);
        return new PcgwBlock
        {
            Kind = PcgwBlockKind.Table,
            Title = family.Length > 0 ? family : L.T("System requirements"),
            Columns = ["", L.T("Minimum"), L.T("Recommended")],
            Rows = parts.Select(p => new List<string> { p.Part, p.Min, p.Rec }).ToList(),
        };
    }

    static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hdr"] = "HDR", ["fov"] = "FOV", ["fps"] = "FPS", ["vsync"] = "V-Sync", ["4k"] = "4K", ["api"] = "API", ["gpu"] = "GPU",
        ["cpu"] = "CPU", ["ram"] = "RAM", ["vram"] = "VRAM", ["os"] = "OS", ["drm"] = "DRM", ["dlc"] = "DLC", ["vr"] = "VR",
        ["eax"] = "EAX", ["xinput"] = "XInput", ["tcp"] = "TCP", ["udp"] = "UDP", ["upnp"] = "UPnP", ["lan"] = "LAN", ["ui"] = "UI",
        ["dualsense"] = "DualSense", ["playstation"] = "PlayStation", ["l10n"] = "Languages", ["steamvr"] = "SteamVR", ["dx"] = "DirectX",
        ["ipv6"] = "IPv6", ["id"] = "ID", ["openxr"] = "OpenXR", ["opengl"] = "OpenGL", ["directx"] = "DirectX", ["tobii"] = "Tobii",
        ["multiplayer"] = "Multiplayer", ["dualshock"] = "DualShock", ["xbox"] = "Xbox", ["gyro"] = "Gyro", ["ultrawidescreen"] = "Ultra-widescreen",
    };

    /// <summary>A field or template name as a label: "120 fps" → "120 FPS", "Network/Multiplayer" → "Multiplayer".</summary>
    public static string Label(string key)
    {
        var last = key.Split('/').Last().Trim();
        var words = last.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => Words.TryGetValue(w, out var known) ? known : w).ToList();
        if (words.Count == 0) return "";
        var s = string.Join(" ", words);
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}

/// <summary>A score on the wiki's infobox: the site, its number and the game's page there.</summary>
public sealed record PcgwScore(string Site, string Score, string? Url);

/// <summary>A link of the infobox: a site's name and the game's page there (from the ids the infobox holds).</summary>
public sealed record PcgwLink(string Site, string Url);

/// <summary>
/// The box at the top right of every PCGamingWiki page: who made the game, its engine, release dates, scores, taxonomy and
/// its pages on other sites. Ids (Steam, HowLongToBeat, MobyGames...) become links, never raw numbers.
/// </summary>
public sealed class PcgwInfobox
{
    public List<string> Developers { get; } = [];
    public List<string> Publishers { get; } = [];
    public List<string> Engines { get; } = [];
    public List<(string Platform, string Date)> Releases { get; } = [];
    public List<PcgwScore> Scores { get; } = [];
    public List<(string Label, string Value)> Taxonomy { get; } = [];
    public List<PcgwLink> Links { get; } = [];

    public bool IsEmpty => Developers.Count + Publishers.Count + Engines.Count + Releases.Count + Scores.Count + Taxonomy.Count + Links.Count == 0;

    /// <summary>Reads {{Infobox game ...}} from the page's wikitext; null when the page has none.</summary>
    public static PcgwInfobox? Parse(string wikitext, string? gameDir = null)
    {
        int open = wikitext.IndexOf("{{Infobox game", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        int close = Matching(wikitext, open);
        if (close < 0) return null;
        var (_, args) = WikiText.Split(wikitext[(open + 2)..close]);
        var box = new PcgwInfobox();
        string P(string raw) => WikiText.Plain(raw, gameDir);
        foreach (var arg in args)
        {
            int eq = arg.IndexOf('=');
            if (eq <= 0) continue;
            var key = arg[..eq].Trim().ToLowerInvariant();
            var value = arg[(eq + 1)..].Trim();
            if (value.Length == 0) continue;
            var rows = Rows(value);
            switch (key)
            {
                case "developers":
                    box.Developers.AddRange(rows.Where(r => r.Args.Count > 0).Select(r => P(r.Args[0])).Where(s => s.Length > 0));
                    break;
                case "publishers":
                    box.Publishers.AddRange(rows.Where(r => r.Args.Count > 0).Select(r => P(r.Args[0])).Where(s => s.Length > 0));
                    break;
                case "engines":
                    box.Engines.AddRange(rows.Where(r => r.Args.Count > 0).Select(r => P(r.Args[0])).Where(s => s.Length > 0));
                    break;
                case "release dates":
                    foreach (var r in rows.Where(r => r.Args.Count >= 2)) box.Releases.Add((P(r.Args[0]), P(r.Args[1])));
                    break;
                case "reception":
                    foreach (var r in rows.Where(r => r.Args.Count >= 3 && P(r.Args[2]).Length > 0))
                    {
                        var site = P(r.Args[0]);
                        var slug = r.Args[1].Trim();
                        string? url = site.ToLowerInvariant() switch
                        {
                            "metacritic" => "https://www.metacritic.com/game/" + slug,
                            "opencritic" => "https://opencritic.com/game/" + slug,
                            "igdb" => "https://www.igdb.com/games/" + slug,
                            _ => null,
                        };
                        box.Scores.Add(new PcgwScore(site, P(r.Args[2]), slug.Length > 0 ? url : null));
                    }
                    break;
                case "taxonomy":
                    foreach (var r in rows)
                    {
                        var kind = r.Name.Split('/').Last().Trim();
                        var v = r.Args.Count > 0 ? P(r.Args[0]) : "";
                        if (v.Length > 0 && kind.Length > 0) box.Taxonomy.Add((PcgwPage.Label(kind), v));
                    }
                    break;
                default:
                    if (LinkFor(key, value) is { } link) box.Links.Add(link);
                    break;
            }
        }
        return box.IsEmpty ? null : box;
    }

    /// <summary>An infobox id as the game's page on that site; ids of side apps, licences and the cover are not links.</summary>
    static PcgwLink? LinkFor(string key, string value)
    {
        var id = value.Split(',')[0].Trim();
        var path = Uri.EscapeDataString(id.Replace(' ', '_'));
        return key switch
        {
            "official site" when id.StartsWith("http", StringComparison.OrdinalIgnoreCase) => new PcgwLink("Official site", id),
            "steam appid" => new PcgwLink("Steam", "https://store.steampowered.com/app/" + id),
            "hltb" => new PcgwLink("HowLongToBeat", "https://howlongtobeat.com/game/" + id),
            "lutris" => new PcgwLink("Lutris", "https://lutris.net/games/" + id),
            "mobygames" => new PcgwLink("MobyGames", "https://www.mobygames.com/game/" + id),
            "strategywiki" => new PcgwLink("StrategyWiki", "https://strategywiki.org/wiki/" + path),
            "wikipedia" => new PcgwLink("Wikipedia", "https://en.wikipedia.org/wiki/" + path),
            "winehq" => new PcgwLink("WineHQ", "https://appdb.winehq.org/objectManager.php?sClass=application&iId=" + id),
            _ => null,
        };
    }

    /// <summary>The row templates in a field's value ({{Infobox game/row/date|Windows|March 19, 2026}}...).</summary>
    static List<(string Name, List<string> Args)> Rows(string value)
    {
        var rows = new List<(string Name, List<string> Args)>();
        int at = 0;
        while ((at = value.IndexOf("{{", at, StringComparison.Ordinal)) >= 0)
        {
            int close = Matching(value, at);
            if (close < 0) break;
            var (n, a) = WikiText.Split(value[(at + 2)..close]);
            rows.Add((n, a.Select(x => x.Trim()).ToList()));
            at = close + 2;
        }
        // A plain value without row templates ("developers = Pearl Abyss").
        if (rows.Count == 0) rows.Add(("", [value]));
        return rows;
    }

    static int Matching(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length - 1; i++)
        {
            if (s[i] == '{' && s[i + 1] == '{') { depth++; i++; }
            else if (s[i] == '}' && s[i + 1] == '}')
            {
                depth--;
                if (depth == 0) return i;
                i++;
            }
        }
        return -1;
    }
}
