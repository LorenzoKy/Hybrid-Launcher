using System.Text;
using System.Text.RegularExpressions;

namespace HybridLauncher.Core.Ini;

public enum IniValueKind { Bool, Choice, Text }

public sealed class IniEntry
{
    public required string Section { get; init; }
    public required string Key { get; init; }
    public required string Value { get; set; }
    public int LineIndex { get; set; }

    /// <summary>The comment block written above the key in OptiScaler.ini, without the leading "; ".</summary>
    public string Comment { get; init; } = "";
    public IniValueKind Kind { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public string? DefaultText { get; init; }

    /// <summary>
    /// The note above a run of keys written with no blank line between them (forks often describe a group once, above its
    /// first key): what the later keys of the run show when they have no comment of their own. Never used to tell the kind.
    /// </summary>
    public string SharedComment { get; init; } = "";

    /// <summary>The key is in the official OptiScaler.ini (see <see cref="OptiScalerKeys"/>); false for options a fork or test build adds.</summary>
    public bool Official => OptiScalerKeys.IsOfficial(Section, Key);

    public string Path => $"{Section}/{Key}";
    public string Summary => Comment.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
}

/// <summary>
/// Line-preserving INI editor: only the value part of the edited lines changes, so OptiScaler's
/// comments, ordering, encoding and line endings stay exactly as shipped.
/// </summary>
public sealed partial class IniDocument
{
    readonly List<string> _lines;
    readonly string _newLine;
    readonly Encoding _encoding;

    public string FilePath { get; }
    public List<IniEntry> Entries { get; } = [];

    IniDocument(string path, List<string> lines, string newLine, Encoding encoding)
    {
        FilePath = path;
        _lines = lines;
        _newLine = newLine;
        _encoding = encoding;
        Index();
    }

    public static IniDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var encoding = new UTF8Encoding(bom);
        var text = encoding.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return new IniDocument(path, lines, newLine, encoding);
    }

    public IniEntry? Find(string section, string key) =>
        Entries.FirstOrDefault(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public IniEntry? Find(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? null : Find(path[..slash], path[(slash + 1)..]);
    }

    public string? Get(string section, string key) => Find(section, key)?.Value;

    /// <summary>Sets a value. Returns false when the key does not exist in this OptiScaler version (nothing is added).</summary>
    public bool Set(string section, string key, string value)
    {
        var e = Find(section, key);
        if (e is null) return false;
        value = value.Replace("\r", "").Replace("\n", "");
        var line = _lines[e.LineIndex];
        var eq = line.IndexOf('=');
        // A note after the value ("Key=1 ; 0 = off") stays where it was.
        var inline = InlineComment().Match(line[(eq + 1)..]);
        _lines[e.LineIndex] = line[..(eq + 1)] + value + (inline.Success ? inline.Groups["c"].Value : "");
        e.Value = value;
        return true;
    }

    public bool Set(string path, string value)
    {
        var slash = path.IndexOf('/');
        return slash >= 0 && Set(path[..slash], path[(slash + 1)..], value);
    }

    public void Save()
    {
        var tmp = FilePath + ".self.tmp";
        File.WriteAllText(tmp, string.Join(_newLine, _lines) + _newLine, _encoding);
        File.Move(tmp, FilePath, overwrite: true);
    }

    [GeneratedRegex(@"^\s*\[(?<s>[^\]]+)\]\s*$")]
    private static partial Regex SectionLine();

    // Forks write keys with dashes too, and some indent them.
    [GeneratedRegex(@"^\s*(?<k>[A-Za-z0-9_.\-]+)\s*=(?<v>.*)$")]
    private static partial Regex KeyLine();

    // "1 ; 0 = off, 1 = on": a note after the value, set off by a space.
    [GeneratedRegex(@"^(?<v>.*?)(?<c>\s+[;#].*)$")]
    private static partial Regex InlineComment();

    // "Game or OptiScaler", "Auto, PTX, or Cubin": the separators of a list of values.
    [GeneratedRegex(@"\s*,\s*(?:or\s+)?|\s+or\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ListSeparator();

    [GeneratedRegex(@"Default(?:\s*\(auto\))?\s*is\s*(?<d>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultPart();

    [GeneratedRegex(@"^(?<opts>.+?)\s+-\s+Default", RegexOptions.IgnoreCase)]
    private static partial Regex OptionsPart();

    [GeneratedRegex(@"^[A-Za-z0-9_.]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthetical();

    void Index()
    {
        Entries.Clear();
        string section = "";
        var comments = new List<string>();
        // The note of the run of keys the line belongs to (no blank line since its first key), for keys with none of their own.
        string shared = "";
        for (int i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) { comments.Clear(); shared = ""; continue; }
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                var c = trimmed.TrimStart(';', '#').Trim();
                if (!c.StartsWith("---", StringComparison.Ordinal)) comments.Add(c);
                continue;
            }
            var s = SectionLine().Match(line);
            if (s.Success) { section = s.Groups["s"].Value.Trim(); comments.Clear(); shared = ""; continue; }

            var k = KeyLine().Match(line);
            if (!k.Success) { comments.Clear(); shared = ""; continue; }

            var value = k.Groups["v"].Value;
            if (InlineComment().Match(value) is { Success: true } inline) value = inline.Groups["v"].Value;
            var entry = Describe(section, k.Groups["k"].Value, value.Trim(), i, comments, comments.Count == 0 ? shared : "");
            Entries.Add(entry);
            if (comments.Count > 0) shared = entry.Comment;
            comments.Clear();
        }
    }

    static IniEntry Describe(string section, string key, string value, int line, List<string> comments, string shared = "")
    {
        var comment = string.Join("\n", comments);
        var kind = IniValueKind.Text;
        var options = new List<string>();
        string? def = null;

        var last = comments.LastOrDefault(c => c.Contains("Default", StringComparison.OrdinalIgnoreCase)) ?? "";
        var dm = DefaultPart().Match(last);
        if (dm.Success) def = dm.Groups["d"].Value.Trim();

        if (comments.Any(c => c.Contains("true or false", StringComparison.OrdinalIgnoreCase)) && !LooksNumericDefault(def))
        {
            kind = IniValueKind.Bool;
        }
        else
        {
            // "xess (native DX11, Arc only), dlss - Default..." : notes in brackets are not options.
            var om = OptionsPart().Match(Parenthetical().Replace(last, ""));
            if (om.Success && !om.Groups["opts"].Value.Contains(',') && WordList(om.Groups["opts"].Value) is { } either && Fits(either, value, def))
            {
                // "Game or OptiScaler - Default is Game" (forks): single words joined by "or", the value one of them.
                options = either;
                kind = IniValueKind.Choice;
            }
            else if (om.Success && om.Groups["opts"].Value.Contains(','))
            {
                foreach (var part in om.Groups["opts"].Value.Split(','))
                {
                    var w = Word().Match(part.Trim());
                    if (w.Success && !options.Contains(w.Value, StringComparer.OrdinalIgnoreCase)) options.Add(w.Value);
                }
                if (options.Count >= 2 && options.All(o => o.Length <= 24)) kind = IniValueKind.Choice;
                else options.Clear();
            }
            // "xess, fsr21, fsr22, ffx (FSR 2.3; 3.1; 4.x), dlss" on its own line, the default on the next one.
            if (kind == IniValueKind.Text && OptionList(comments) is { } list)
            {
                options = list;
                kind = IniValueKind.Choice;
            }
            // "Kernel format: Auto (PTX on Linux), PTX, or Cubin" (forks): a label, then a list of single words.
            if (kind == IniValueKind.Text)
                foreach (var c in comments)
                {
                    var colon = c.IndexOf(':');
                    if (colon < 0 || WordList(c[(colon + 1)..]) is not { } labelled || !Fits(labelled, value, def)) continue;
                    options = labelled;
                    kind = IniValueKind.Choice;
                    break;
                }
            // No "true or false" line, but the key holds true or false, or its default is one ("ExtraPacing=false", forks).
            if (kind == IniValueKind.Text && (IsBoolWord(value) || IsAutoValue(value) && IsBoolWord(def?.Split(' ', '.', ',', ';')[0])))
                kind = IniValueKind.Bool;
        }

        return new IniEntry
        {
            Section = section,
            Key = key,
            Value = value,
            LineIndex = line,
            Comment = comment,
            Kind = kind,
            Options = options,
            DefaultText = def,
            SharedComment = shared,
        };
    }

    static bool IsBoolWord(string? s) => s is not null && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase));

    static bool IsAutoValue(string s) => s.Length == 0 || s.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A list read from a comment is trusted only when the value is auto or one of the options, and so is the default the file
    /// names ("300.0 or more - Default is 700.0" is a number; "Keys: intensity, style - Default is empty" names parts of a text).
    /// </summary>
    static bool Fits(List<string> options, string value, string? def)
    {
        bool In(string? v) => v is not null && (IsAutoValue(v) || options.Contains(v, StringComparer.OrdinalIgnoreCase));
        var first = def?.Split(' ', ',', ';', '(')[0].TrimEnd('.');
        return In(value) && (string.IsNullOrEmpty(first) || In(first));
    }

    /// <summary>"Game or OptiScaler", "Auto (detect), SM86, or SM75": every part a single word (notes in brackets aside); null otherwise.</summary>
    static List<string>? WordList(string text)
    {
        var parts = ListSeparator().Split(Parenthetical().Replace(text, "").Trim().TrimEnd('.')).Where(p => p.Length > 0).ToList();
        if (parts.Count < 2 || parts.Count > 12) return null;
        if (!parts.All(p => p.Length <= 24 && Word().Match(p) is { Success: true } w && w.Length == p.Length)) return null;
        // "true or false" is a switch, not a choice.
        if (parts.All(IsBoolWord)) return null;
        return parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A comment line that is only a list of single-word values ("nofg, dlssg, fsrfg"), notes in brackets aside.</summary>
    /// With <paramref name="needsDefaultAfter"/> the list must sit right above the "Default (auto) is ..." line, so lists of
    /// values written as text (overlay rows, dll names) stay text.
    public static List<string>? OptionList(IReadOnlyList<string> comments, bool needsDefaultAfter = true)
    {
        for (int i = 0; i < comments.Count; i++)
        {
            var c = comments[i];
            if (needsDefaultAfter && (i + 1 >= comments.Count || !comments[i + 1].TrimStart().StartsWith("Default", StringComparison.OrdinalIgnoreCase))) continue;
            var parts = Parenthetical().Replace(c, "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts.All(p => p.Length <= 24 && Word().Match(p) is { Success: true } w && w.Length == p.Length))
                return parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return null;
    }

    // "; true or false - Default (auto) is 1" (InterpolationCount) is really a number, not a bool.
    static bool LooksNumericDefault(string? def) => def is not null && def.Length > 0 && char.IsDigit(def[0]);
}
