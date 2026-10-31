using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HybridLauncher.Core.Media;

/// <summary>A piece of styled text in a <see cref="RichLine"/>.</summary>
public sealed record RichRun(string Text, bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, string? Link = null);

/// <summary>
/// One line of text read from a web page or a wiki: a heading, a paragraph or a list item (<see cref="Level"/> &gt; 0),
/// made of styled runs. The UI turns it into a TextBlock.
/// </summary>
public sealed class RichLine
{
    public List<RichRun> Runs { get; } = [];
    public int Level { get; set; }
    public bool Heading { get; set; }

    /// <summary>A picture on its own line (store descriptions show banners and GIFs between the paragraphs).</summary>
    public string? Image { get; set; }

    /// <summary>
    /// An animation on its own line: Steam turns the GIFs of store descriptions into short muted looping videos (mp4 / webm);
    /// <see cref="Image"/> is then their poster frame.
    /// </summary>
    public string? Video { get; set; }

    /// <summary>The same clip in its other format, tried when <see cref="Video"/> cannot be played here.</summary>
    public string? VideoAlt { get; set; }

    /// <summary>The picture's size as the page gives it (0 = unknown): room is kept for it before it arrives.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    public string Text => string.Concat(Runs.Select(r => r.Text)).Trim();
    public bool IsEmpty => Text.Length == 0 && Image is null && Video is null;
    public override string ToString() => (Heading ? "# " : Level > 0 ? new string(' ', Level * 2) + "- " : "") + Text;
}

/// <summary>Turns the HTML of Steam's store text and GitHub's wiki pages into <see cref="RichLine"/>s (no images, no tables).</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(/?)([a-zA-Z0-9]+)([^>]*)>|([^<]+)", RegexOptions.Singleline)]
    private static partial Regex Token();

    [GeneratedRegex(@"href\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\bsrc\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Src();

    [GeneratedRegex(@"\bposter\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Poster();

    [GeneratedRegex(@"\btype\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex TypeAttr();

    [GeneratedRegex(@"\bwidth\s*=\s*""?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WidthAttr();

    [GeneratedRegex(@"\bheight\s*=\s*""?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeightAttr();

    static int Size(Regex re, string attrs) => re.Match(attrs) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;

    public static List<RichLine> ToLines(string? html)
    {
        var lines = new List<RichLine>();
        if (string.IsNullOrWhiteSpace(html)) return lines;
        html = Regex.Replace(html, "<!--.*?-->|<script.*?</script>|<style.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        RichLine? video = null;
        RichLine current = new();
        int listDepth = 0, bold = 0, italic = 0, strike = 0, code = 0;
        string? link = null;

        void Break(bool heading = false)
        {
            if (!current.IsEmpty) lines.Add(current);
            current = new RichLine { Level = 0, Heading = heading };
        }

        foreach (Match m in Token().Matches(html))
        {
            if (m.Groups[4].Success)
            {
                var text = Spaces().Replace(WebUtility.HtmlDecode(m.Groups[4].Value), " ");
                if (text.Length == 0 || (text == " " && current.Runs.Count == 0)) continue;
                if (current.Runs.Count == 0) text = text.TrimStart();
                current.Runs.Add(new RichRun(text, bold > 0, italic > 0, strike > 0, code > 0, link));
                continue;
            }
            bool close = m.Groups[1].Value == "/";
            var tag = m.Groups[2].Value.ToLowerInvariant();
            switch (tag)
            {
                case "br":
                    Break();
                    current.Level = listDepth > 0 ? listDepth : 0;
                    break;
                case "p" or "div" or "tr":
                    if (!close || current.Runs.Count > 0)
                    {
                        int level = current.Level;
                        Break();
                        current.Level = level;
                    }
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Break(heading: !close);
                    break;
                case "ul" or "ol":
                    Break();
                    listDepth = Math.Max(0, listDepth + (close ? -1 : 1));
                    break;
                case "li":
                    Break();
                    if (!close) current.Level = Math.Max(1, listDepth);
                    break;
                case "strong" or "b":
                    bold += close ? -1 : 1;
                    break;
                case "em" or "i":
                    italic += close ? -1 : 1;
                    break;
                case "s" or "del" or "strike":
                    strike += close ? -1 : 1;
                    break;
                case "code" or "kbd":
                    code += close ? -1 : 1;
                    break;
                case "img" when !close && Src().Match(m.Groups[3].Value) is { Success: true } src:
                    Break();
                    lines.Add(new RichLine { Image = WebUtility.HtmlDecode(src.Groups[1].Value), Width = Size(WidthAttr(), m.Groups[3].Value), Height = Size(HeightAttr(), m.Groups[3].Value) });
                    break;
                // Steam's GIFs: <video poster=... width=... height=...><source src=....webm><source src=....mp4></video>.
                case "video" when !close:
                    Break();
                    video = new RichLine
                    {
                        Image = Poster().Match(m.Groups[3].Value) is { Success: true } poster ? WebUtility.HtmlDecode(poster.Groups[1].Value) : null,
                        Width = Size(WidthAttr(), m.Groups[3].Value),
                        Height = Size(HeightAttr(), m.Groups[3].Value),
                    };
                    lines.Add(video);
                    break;
                case "video":
                    video = null;
                    break;
                case "source" when !close && video is not null && Src().Match(m.Groups[3].Value) is { Success: true } source:
                    // Steam's webm is VP9 (Windows ships its decoder), its mp4 is HEVC (a paid extension): webm first, mp4 kept as
                    // the second choice.
                    var url = WebUtility.HtmlDecode(source.Groups[1].Value);
                    bool webm = TypeAttr().Match(m.Groups[3].Value).Groups[1].Value.Contains("webm", StringComparison.OrdinalIgnoreCase) || url.Contains(".webm", StringComparison.OrdinalIgnoreCase);
                    if (video.Video is null) video.Video = url;
                    else if (webm) (video.Video, video.VideoAlt) = (url, video.Video);
                    else video.VideoAlt ??= url;
                    break;
                case "a":
                    link = close ? null : Href().Match(m.Groups[3].Value) is { Success: true } h ? WebUtility.HtmlDecode(h.Groups[1].Value) : null;
                    break;
            }
            bold = Math.Max(0, bold);
            italic = Math.Max(0, italic);
            strike = Math.Max(0, strike);
            code = Math.Max(0, code);
        }
        Break();
        foreach (var line in lines) TrimRuns(line);
        return lines.Where(l => !l.IsEmpty).ToList();
    }

    /// <summary>Plain text of an HTML fragment, on one line.</summary>
    public static string Plain(string? html) => string.Join(" ", ToLines(html).Where(l => l.Image is null && l.Video is null).Select(l => l.Text)).Trim();

    static void TrimRuns(RichLine line)
    {
        if (line.Runs.Count == 0) return;
        var first = line.Runs[0];
        line.Runs[0] = first with { Text = first.Text.TrimStart() };
        var last = line.Runs[^1];
        line.Runs[^1] = last with { Text = last.Text.TrimEnd() };
    }
}

/// <summary>
/// A small MediaWiki reader for PCGamingWiki pages: section bodies become <see cref="RichLine"/>s, the templates the wiki
/// uses for notes and fixes become plain words, references and citations are dropped.
/// </summary>
public static partial class WikiText
{
    [GeneratedRegex(@"<ref[^>]*/>|<ref[^>]*>.*?</ref>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Refs();

    /// <summary>Wikitext to lines: lists, bold / italic, links, &lt;code&gt;, the common PCGamingWiki templates.</summary>
    public static List<RichLine> ToLines(string wikitext, string? gameDir = null)
    {
        var text = ExpandTemplates(Refs().Replace(wikitext, ""), gameDir);
        var lines = new List<RichLine>();
        foreach (var raw0 in text.Replace("\r", "").Split('\n'))
        {
            foreach (var raw in Regex.Split(raw0, @"<br\s*/?>|<p>|</p>", RegexOptions.IgnoreCase))
            {
                var s = raw.TrimEnd();
                if (s.Trim().Length == 0 || s.Trim() == "__NOTOC__") continue;
                var line = new RichLine();
                var heading = Regex.Match(s, @"^(={2,6})\s*(.*?)\s*\1\s*$");
                if (heading.Success)
                {
                    line.Heading = true;
                    s = heading.Groups[2].Value;
                }
                else
                {
                    int level = 0;
                    while (level < s.Length && s[level] is '*' or '#' or ':') level++;
                    line.Level = level;
                    s = s[level..].TrimStart();
                }
                AddInline(line, s);
                if (!line.IsEmpty) lines.Add(line);
            }
        }
        return lines;
    }

    /// <summary>Plain text of a wikitext fragment (template field values, notes), on one line.</summary>
    public static string Plain(string wikitext, string? gameDir = null) =>
        string.Join(" ", ToLines(wikitext, gameDir).Select(l => l.Text)).Trim();

    // '''bold''', ''italic'', [[link|text]], [url text], <code>, <s>, html tags dropped.
    [GeneratedRegex(@"'''(.+?)'''|''(.+?)''|\[\[([^\]|]*)(?:\|([^\]]*))?\]\]|\[(https?://[^\s\]]+)(?:\s+([^\]]*))?\]|<code>(.*?)</code>|<s>(.*?)</s>|<[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex Inline();

    static void AddInline(RichLine line, string s)
    {
        int at = 0;
        foreach (Match m in Inline().Matches(s))
        {
            if (m.Index > at) Add(line, s[at..m.Index]);
            if (m.Groups[1].Success) Add(line, m.Groups[1].Value, bold: true);
            else if (m.Groups[2].Success) Add(line, m.Groups[2].Value, italic: true);
            else if (m.Groups[3].Success)
            {
                var target = m.Groups[3].Value;
                // Files and categories are not text.
                if (!target.StartsWith("File:", StringComparison.OrdinalIgnoreCase) && !target.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
                {
                    var label = m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? m.Groups[4].Value : target.Split(':').Last().TrimStart('#');
                    Add(line, label, link: WikiLink(target));
                }
            }
            else if (m.Groups[5].Success) Add(line, m.Groups[6].Success && m.Groups[6].Value.Length > 0 ? m.Groups[6].Value : m.Groups[5].Value, link: m.Groups[5].Value);
            else if (m.Groups[7].Success) Add(line, m.Groups[7].Value, code: true);
            else if (m.Groups[8].Success) Add(line, m.Groups[8].Value, strike: true);
            at = m.Index + m.Length;
        }
        if (at < s.Length) Add(line, s[at..]);
    }

    static void Add(RichLine line, string text, bool bold = false, bool italic = false, bool code = false, bool strike = false, string? link = null)
    {
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", "")).Replace("'''", "").Replace("''", "");
        text = Regex.Replace(text, @"\s+", " ");
        if (text.Length == 0) return;
        if ((bold || italic) && link is null)
        {
            // A link in bold ('''[[DLSS]]''', '''[https://... a mod]'''): still a link, in bold.
            var nested = Regex.Match(text, @"^\s*(?:\[\[([^\]|]*)(?:\|([^\]]*))?\]\]|\[(https?://[^\s\]]+)(?:\s+([^\]]*))?\])\s*$");
            if (nested.Success)
            {
                if (nested.Groups[3].Success) { text = nested.Groups[4].Success && nested.Groups[4].Value.Length > 0 ? nested.Groups[4].Value : nested.Groups[3].Value; link = nested.Groups[3].Value; }
                else { text = nested.Groups[2].Success && nested.Groups[2].Value.Length > 0 ? nested.Groups[2].Value : nested.Groups[1].Value; link = WikiLink(nested.Groups[1].Value); }
            }
            // Other markup inside bold text is flattened to its words.
            else text = Regex.Replace(text, @"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", "$1");
        }
        // Bare addresses in the text ("see https://...") are links too, as on the wiki.
        if (link is null && !code)
        {
            int at = 0;
            foreach (Match m in BareUrl().Matches(text))
            {
                if (m.Index > at) line.Runs.Add(new RichRun(text[at..m.Index], bold, italic, strike));
                line.Runs.Add(new RichRun(m.Value, bold, italic, strike, Link: m.Value));
                at = m.Index + m.Length;
            }
            if (at > 0)
            {
                if (at < text.Length) line.Runs.Add(new RichRun(text[at..], bold, italic, strike));
                return;
            }
        }
        line.Runs.Add(new RichRun(text, bold, italic, strike, code, link));
    }

    [GeneratedRegex(@"https?://[^\s<>""\]\)]+[^\s<>""\]\).,;:!?']")]
    private static partial Regex BareUrl();

    /// <summary>A wiki link's target as an address on PCGamingWiki ("#Section" is a part of the game's own page).</summary>
    static string? WikiLink(string target)
    {
        if (target.StartsWith('#')) return null;
        return "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(target.Replace(' ', '_')).Replace("%3A", ":").Replace("%2F", "/").Replace("%23", "#");
    }

    // ---------- templates ----------

    /// <summary>Replaces every {{template}} (innermost first) with its plain-word form.</summary>
    public static string ExpandTemplates(string text, string? gameDir = null)
    {
        for (int guard = 0; guard < 2000; guard++)
        {
            // The innermost template: an opening "{{" with no other "{{" before its closing "}}".
            int close = text.IndexOf("}}", StringComparison.Ordinal);
            if (close < 0) break;
            int open = text.LastIndexOf("{{", close, StringComparison.Ordinal);
            if (open < 0) { text = text.Remove(close, 2); continue; }
            var inner = text[(open + 2)..close];
            text = text[..open] + Template(inner, gameDir) + text[(close + 2)..];
        }
        return text;
    }

    /// <summary>Splits a template body into its name and arguments (named ones as "name=value").</summary>
    public static (string Name, List<string> Args) Split(string inner)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            // Links and nested templates keep their own bars: "|developers = {{Infobox game/row/developer|Pearl Abyss}}" is one argument.
            if (i + 1 < inner.Length && ((inner[i] == '[' && inner[i + 1] == '[') || (inner[i] == '{' && inner[i + 1] == '{'))) { depth++; i++; }
            else if (i + 1 < inner.Length && ((inner[i] == ']' && inner[i + 1] == ']') || (inner[i] == '}' && inner[i + 1] == '}'))) { depth--; i++; }
            else if (inner[i] == '|' && depth <= 0)
            {
                parts.Add(inner[start..i]);
                start = i + 1;
            }
        }
        parts.Add(inner[start..]);
        return (parts[0].Trim(), parts.Skip(1).ToList());
    }

    static string Arg(List<string> args, string name)
    {
        foreach (var a in args)
        {
            var eq = a.IndexOf('=');
            if (eq > 0 && a[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return a[(eq + 1)..].Trim();
        }
        return "";
    }

    static string Positional(List<string> args, int index)
    {
        var plain = args.Where(a => !Regex.IsMatch(a, @"^\s*[a-zA-Z ]+\s*=")).ToList();
        return index < plain.Count ? plain[index].Trim() : "";
    }

    static string Template(string inner, string? gameDir)
    {
        var (name, args) = Split(inner);
        var key = name.ToLowerInvariant();
        switch (key)
        {
            case "++": return "✔ ";
            case "--": return "✘ ";
            case "ii": return "ℹ ";
            case "mm" or "+" or "-": return "• ";
            case "p": return PathToken(Positional(args, 0), gameDir);
            case "file" or "folder" or "code" or "key":
                return key == "key" ? string.Join("+", args.Select(a => a.Trim())) : $"<code>{Positional(args, 0)}</code>";
            case "term" or "glossary": return Positional(args, args.Count > 1 ? 1 : 0);
            case "note": return Arg(args, "note") is { Length: > 0 } n ? n : Positional(args, 0);
            // {{store link|Steam|1418529|Deluxe Edition}}: the words are the last part (the store and the id are not text).
            case "store link" or "store link/row": return args.Count >= 3 ? Positional(args, 2) : "";
            case "store feature" or "drm": return Positional(args, 0);
            case "fixbox":
                var desc = Arg(args, "description");
                var fix = Arg(args, "fix");
                return $"\n'''Fix: {desc}'''\n{fix}\n";
            case "cn" or "citation needed" or "refcheck" or "refurl" or "refsnippet" or "dead link" or "update" or "stub" or "disambig" or "image" or "screenshot":
                return "";
        }
        if (key.StartsWith("game data/", StringComparison.Ordinal) || key.StartsWith("infobox", StringComparison.Ordinal)) return "";
        // An unknown template with a single short argument is usually just a label.
        var first = Positional(args, 0);
        return args.Count == 1 && first.Length < 80 ? first : "";
    }

    /// <summary>PCGamingWiki's path placeholders ({{P|localappdata}}...) as Windows folders; unknown ones stay as &lt;name&gt;.</summary>
    public static string PathToken(string token, string? gameDir)
    {
        var t = token.Trim().ToLowerInvariant().Replace('/', '\\');
        string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var user = Env(Environment.SpecialFolder.UserProfile);
        return t switch
        {
            "game" => gameDir ?? "<game folder>",
            "localappdata" => Env(Environment.SpecialFolder.LocalApplicationData),
            "appdata" => Env(Environment.SpecialFolder.ApplicationData),
            "userprofile" => user,
            "userprofile\\documents" => Env(Environment.SpecialFolder.MyDocuments),
            "userprofile\\appdata\\locallow" => Path.Combine(user, "AppData", "LocalLow"),
            "programdata" or "allusersprofile" => Env(Environment.SpecialFolder.CommonApplicationData),
            "public" => Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
            "windir" => Env(Environment.SpecialFolder.Windows),
            "username" => Environment.UserName,
            "steam" => Detection.SteamScanner.SteamRoot() ?? "<Steam folder>",
            "uid" => "*",
            _ => $"<{token.Trim()}>",
        };
    }
}

/// <summary>
/// GitHub-flavoured Markdown (release notes) to <see cref="RichLine"/>s: headings, lists, bold, italic, inline code, links and
/// bare addresses. Tables, images and HTML are kept as their plain words.
/// </summary>
public static partial class MarkdownText
{
    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__|`([^`]+)`|\[([^\]]+)\]\((https?://[^)\s]+)\)|(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?!\w)|(https?://[^\s<>()]+)|(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?![\w_])")]
    private static partial Regex Inline();

    public static List<RichLine> ToLines(string? markdown)
    {
        var lines = new List<RichLine>();
        if (string.IsNullOrWhiteSpace(markdown)) return lines;
        bool fence = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var s = raw.TrimEnd();
            if (s.TrimStart().StartsWith("```")) { fence = !fence; continue; }
            if (s.Trim().Length == 0 || Regex.IsMatch(s, @"^\s*([-*_]\s*){3,}$") || Regex.IsMatch(s, @"^\s*\|?\s*:?-{2,}")) continue;
            var line = new RichLine();
            if (fence)
            {
                line.Runs.Add(new RichRun(s, Code: true));
                lines.Add(line);
                continue;
            }
            s = Regex.Replace(s, @"<[^>]+>|!\[[^\]]*\]\([^)]*\)", "");
            // Quotes and GitHub's alerts ("> [!CAUTION]"): the words, the alert as a bold label.
            s = Regex.Replace(s, @"^\s*(>\s?)+", "");
            s = Regex.Replace(s, @"\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]", m => "**" + m.Groups[1].Value[0] + m.Groups[1].Value[1..].ToLowerInvariant() + "**", RegexOptions.IgnoreCase);
            if (Regex.Match(s, @"^\s*#{1,6}\s+(.*?)\s*#*$") is { Success: true } h)
            {
                line.Heading = true;
                s = h.Groups[1].Value;
            }
            else if (Regex.Match(s, @"^(\s*)(?:[-*+]|\d+[.)])\s+(.*)$") is { Success: true } item)
            {
                line.Level = 1 + item.Groups[1].Value.Replace("\t", "  ").Length / 2;
                s = item.Groups[2].Value;
            }
            s = s.Trim().Trim('|').Replace(" | ", "  ·  ");
            int at = 0;
            foreach (Match m in Inline().Matches(s))
            {
                if (m.Index > at) Add(line, s[at..m.Index]);
                if (m.Groups[1].Success || m.Groups[2].Success) Add(line, m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, bold: true);
                else if (m.Groups[3].Success) line.Runs.Add(new RichRun(m.Groups[3].Value, Code: true));
                else if (m.Groups[4].Success) Add(line, m.Groups[4].Value, link: m.Groups[5].Value);
                else if (m.Groups[6].Success) Add(line, m.Groups[6].Value, italic: true);
                else if (m.Groups[7].Success) Add(line, m.Groups[7].Value, link: m.Groups[7].Value);
                else Add(line, m.Groups[8].Value, italic: true);
                at = m.Index + m.Length;
            }
            if (at < s.Length) Add(line, s[at..]);
            if (!line.IsEmpty) lines.Add(line);
        }
        return lines;
    }

    static void Add(RichLine line, string text, bool bold = false, bool italic = false, string? link = null)
    {
        text = WebUtility.HtmlDecode(text).Replace("**", "");
        if (link is null)
        {
            // A link inside bold or italic words ("*Check the [Wiki](https://...) first*") stays a link, in that style.
            int at = 0;
            foreach (Match m in Regex.Matches(text, @"\[([^\]]+)\]\((https?://[^)\s]+)\)"))
            {
                if (m.Index > at) line.Runs.Add(new RichRun(text[at..m.Index], bold, italic));
                line.Runs.Add(new RichRun(m.Groups[1].Value, bold, italic, Link: m.Groups[2].Value));
                at = m.Index + m.Length;
            }
            if (at > 0)
            {
                if (at < text.Length) line.Runs.Add(new RichRun(text[at..], bold, italic));
                return;
            }
        }
        if (text.Length > 0) line.Runs.Add(new RichRun(text, bold, italic, Link: link));
    }
}
