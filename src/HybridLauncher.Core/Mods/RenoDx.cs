using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Mods;

/// <summary>A download place of a RenoDX mod: a snapshot build (a direct .addon64 / .addon32), a Nexus Mods page or a Discord.</summary>
public sealed record RenoDxLink(string Kind, string Url)
{
    public bool IsFile => Url.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) || Url.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase);
    public bool Is32Bit => Url.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase);
    public string FileName => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? Path.GetFileName(u.LocalPath) : "";
}

/// <summary>One mod of the RenoDX list (a game, or a generic mod for an engine).</summary>
public sealed record RenoDxMod(string Name, string Maintainer, List<RenoDxLink> Links, bool Working, string? Note, string? Engine = null)
{
    /// <summary>Generic mods: the games the community tested it with (name, working, notes such as "Upgrade Path: On").</summary>
    public List<(string Game, bool Working, string Notes)> Tested { get; } = [];

    public RenoDxLink? Snapshot(bool want64) =>
        Links.Where(l => l.IsFile).OrderBy(l => l.Is32Bit == want64 ? 1 : 0).FirstOrDefault();
    public RenoDxLink? Nexus => Links.FirstOrDefault(l => l.Kind == "Nexus Mods");
    public RenoDxLink? Discord => Links.FirstOrDefault(l => l.Kind == "Discord");

    /// <summary>Where the mod's source lives (from its snapshot badge): GitHub owner and the folder under src/games.</summary>
    public string? SourceOwner { get; set; }
    public string? SourceDir { get; set; }
}

/// <summary>One setting a RenoDX mod offers in its ReShade add-on page (its label, the help text, the choices).</summary>
public sealed record RenoDxSetting(string Label, string? Tooltip, List<string> Choices);

/// <summary>What a RenoDX mod lets you change, by section, read from its source; and when the source last changed.</summary>
public sealed record RenoDxFeatures(List<(string Section, List<RenoDxSetting> Settings)> Sections, DateTime? Updated, string SourceUrl)
{
    public int Count => Sections.Sum(s => s.Settings.Count);
}

/// <summary>A RenoDX mod that fits a game, and why.</summary>
public sealed record RenoDxMatch(RenoDxMod Mod, string Reason, int Score);

/// <summary>
/// RenoDX (HDR mods built as ReShade add-ons, MIT licensed): the mod list comes from the project's wiki ("Mods" page), the
/// snapshot builds from the maintainers' GitHub releases. Mods only on Nexus Mods are opened in the browser and installed from
/// the downloaded file (Hybrid Launcher never logs in to Nexus). Every mod needs ReShade with full add-on support in the same folder.
/// </summary>
public static partial class RenoDx
{
    const string WikiUrl = "https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md";
    public const string WikiPage = "https://github.com/clshortfuse/renodx/wiki/Mods";

    public static async Task<List<RenoDxMod>> GetModsAsync(bool force = false, CancellationToken ct = default)
    {
        var md = await Http.GetCachedAsync(WikiUrl, "renodx-mods.md", TimeSpan.FromHours(12), force, ct: ct);
        return md is null ? [] : Parse(md);
    }

    /// <summary>The table rows of the game list, then the generic mods (Unreal Engine, Unity, Source) from their headings.</summary>
    public static List<RenoDxMod> Parse(string md)
    {
        var mods = new List<RenoDxMod>();
        string? section = null;
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                section = line.TrimStart('#').Trim();
                // The generic mods sit in headings ("### Unreal Engine Extended [badge](url)") and the line under them.
                var engine = EngineOf(section);
                if (engine is not null)
                {
                    var links = Links(line);
                    if (links.Count > 0) mods.Add(new RenoDxMod(Plain(BadgeRx().Replace(section, "")).Trim(), "", links, true, null, engine));
                }
                continue;
            }
            if (section is not null && EngineOf(section) is { } eng && !line.StartsWith('|'))
            {
                // "64-bit: [badge](url) | 32-bit: [badge](url)" under the Unity / Source headings.
                var links = Links(line).Where(l => l.IsFile).ToList();
                if (links.Count > 0 && !mods.Any(m => m.Engine == eng)) mods.Add(new RenoDxMod(section, "", links, true, null, eng));
                continue;
            }
            if (!line.StartsWith('|') || section is null) continue;
            if (section.StartsWith("Deprecated", StringComparison.OrdinalIgnoreCase)) continue;
            var cells = line.Trim().Trim('|').Split('|');
            if (EngineOf(section) is not null || (section.Length > 0 && mods.LastOrDefault()?.Engine is { } && !section.StartsWith("Related")))
            {
                // Under a generic mod: "| Game | status | notes |", the games it was tested with.
                var generic = mods.LastOrDefault(m => m.Engine is not null);
                var game = cells.Length > 0 ? Plain(cells[0]).Trim() : "";
                if (generic is not null && cells.Length >= 2 && game.Length > 0 && game != "Name" && !game.StartsWith(":-"))
                    generic.Tested.Add((game, cells[1].Contains("white_check_mark"), cells.Length > 2 ? Plain(cells[2].Replace("<br>", " ")) : ""));
                continue;
            }
            if (cells.Length < 4) continue;
            var name = Plain(cells[0]).Trim();
            if (name.Length == 0 || name == "Name" || name.StartsWith(":-") || name.StartsWith("---")) continue;
            var linkCell = string.Join("|", cells.Skip(2).Take(cells.Length - 3));
            var status = cells[^1];
            var links2 = Links(linkCell);
            if (links2.Count == 0) continue;
            var note = NoteRx().Match(status) is { Success: true } m ? m.Groups[1].Value : null;
            var mod = new RenoDxMod(name, Plain(cells[1]).Trim(), links2, status.Contains("white_check_mark"), note,
                section is not null && EngineOf(section) is { } e ? e : null);
            if (SourceRx().Match(linkCell) is { Success: true } src)
            {
                mod.SourceOwner = src.Groups[1].Value;
                mod.SourceDir = Uri.UnescapeDataString(src.Groups[2].Value);
            }
            mods.Add(mod);
        }
        return mods;
    }

    static string? EngineOf(string heading) =>
        heading.StartsWith("Unreal Engine", StringComparison.OrdinalIgnoreCase) ? "Unreal"
        : heading.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) ? "Unity"
        : heading.StartsWith("Source Engine", StringComparison.OrdinalIgnoreCase) ? "Source"
        : null;

    /// <summary>Every "[![Label](badge)](url)" in the text, named from the badge (Nexus Mods, Snapshot, Discord).</summary>
    static List<RenoDxLink> Links(string text)
    {
        var list = new List<RenoDxLink>();
        foreach (Match m in LinkRx().Matches(text))
        {
            var label = m.Groups[1].Value;
            var url = m.Groups[2].Value.Trim();
            string kind = url.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase) ? "Nexus Mods"
                : url.Contains("discord", StringComparison.OrdinalIgnoreCase) ? "Discord"
                : url.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ? "Snapshot"
                : label.Length > 0 ? label : "Link";
            if (!list.Any(l => l.Url == url)) list.Add(new RenoDxLink(kind, url));
        }
        return list;
    }

    // "[Hollow Knight](discussion link)" keeps its text.
    static string Plain(string s) => System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1"), @"<[^>]+>|\*\*|`", "")).Replace("&middot;", "").Trim();

    /// <summary>The mods that fit a game: its own entry first (by name), then the generic mod of its engine.</summary>
    public static List<RenoDxMatch> Match(IEnumerable<RenoDxMod> mods, string gameName, string? engine)
    {
        var key = Key(gameName);
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
        var matches = new List<RenoDxMatch>();
        foreach (var mod in mods.Where(m => m.Engine is null))
        {
            var mk = Key(mod.Name);
            int score = mk == key ? 100
                : mk.Length > 3 && (key.StartsWith(mk + " ") || mk.StartsWith(key + " ")) ? 80
                : 0;
            if (score == 0 && words.Count > 0)
            {
                var mw = mk.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
                int common = mw.Intersect(words).Count();
                if (mw.Count > 0 && common == mw.Count && common == words.Count) score = 90;
                else if (mw.Count > 1 && common >= Math.Max(2, Math.Min(mw.Count, words.Count) - 0) && common * 2 > Math.Max(mw.Count, words.Count)) score = 55;
            }
            if (score > 0) matches.Add(new RenoDxMatch(mod, score >= 90 ? L.T("Made for this game") : L.T("Name looks like this game"), score));
        }
        foreach (var mod in mods.Where(m => m.Engine is not null))
        {
            var tested = mod.Tested.FirstOrDefault(t => Key(t.Game) == key);
            if (tested.Game is not null)
                matches.Add(new RenoDxMatch(mod, (tested.Working ? L.T("Tested with this game") : L.T("Tested with this game (in progress)")) + (tested.Notes.Length > 0 ? $": {tested.Notes}" : ""), 70));
            else if (mod.Engine == engine)
                matches.Add(new RenoDxMatch(mod, L.F("Generic mod for {0} games (this game runs on {0})", engine), 30));
        }
        return matches.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>A name reduced for comparing: lower case, no accents, no ™ ® : - ' and no leading "The".</summary>
    public static string Key(string name)
    {
        var s = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var key = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        if (key.StartsWith("the ")) key = key[4..];
        // Roman numerals and digits read alike ("ii" = "2").
        key = Regex.Replace(key, @"\b(ii|iii|iv)\b", m => m.Value switch { "ii" => "2", "iii" => "3", _ => "4" });
        return key;
    }

    /// <summary>The engine of a game from its files: Unreal (Engine folder, *-Shipping.exe), Unity (UnityPlayer.dll), Source (bin\engine.dll).</summary>
    public static string? DetectEngine(string installDir, string? exe)
    {
        try
        {
            if (exe is not null)
            {
                var name = Path.GetFileName(exe);
                if (name.Contains("-Shipping", StringComparison.OrdinalIgnoreCase) || name.Contains("-WinGDK", StringComparison.OrdinalIgnoreCase)) return "Unreal";
                var exeDir = Path.GetDirectoryName(exe)!;
                if (File.Exists(Path.Combine(exeDir, "UnityPlayer.dll"))) return "Unity";
                if (exeDir.Replace('/', '\\').Contains(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase)) return "Unreal";
            }
            if (Directory.Exists(Path.Combine(installDir, "Engine", "Binaries"))) return "Unreal";
            if (File.Exists(Path.Combine(installDir, "UnityPlayer.dll"))) return "Unity";
            if (File.Exists(Path.Combine(installDir, "bin", "engine.dll"))) return "Source";
        }
        catch (Exception ex) { Log.Warn($"Engine of {installDir}: {ex.Message}"); }
        return null;
    }

    /// <summary>True for a 64-bit exe (PE32+), false for 32-bit, null when it can't be read.</summary>
    public static bool? Is64Bit(string exe)
    {
        try
        {
            using var f = File.OpenRead(exe);
            using var r = new BinaryReader(f);
            f.Position = 0x3C;
            int pe = r.ReadInt32();
            f.Position = pe + 4;
            ushort machine = r.ReadUInt16();
            return machine is 0x8664 or 0xAA64;
        }
        catch { return null; }
    }

    /// <summary>RenoDX add-on files in the folder (whoever put them there).</summary>
    public static List<string> Installed(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*.addon64").Concat(Directory.GetFiles(dir, "*.addon32"))
                .Where(f => Path.GetFileName(f).StartsWith("renodx", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("renodx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f).ToList();
        }
        catch { return []; }
    }

    /// <summary>Downloads a snapshot build straight into the game folder (replacing an older copy of the same file).</summary>
    public static async Task<string> InstallSnapshotAsync(RenoDxLink link, string dir, IProgress<double>? progress, CancellationToken ct)
    {
        if (!link.IsFile) throw new InvalidOperationException(L.T("This link is not a direct download."));
        var dest = Path.Combine(dir, link.FileName);
        var tmp = Path.Combine(AppInfo.DownloadsDir, "renodx", link.FileName);
        await Http.DownloadFileAsync(link.Url, tmp, 0, progress, ct);
        if (new FileInfo(tmp).Length < 10_000) throw new InvalidOperationException($"{link.FileName} looks broken (too small). Try again later.");
        File.Copy(tmp, dest, overwrite: true);
        return dest;
    }

    /// <summary>
    /// Installs a file downloaded by hand (from Nexus Mods): a .addon64 / .addon32 is copied in, an archive is unpacked and its
    /// add-on files copied. Returns the names put in the folder.
    /// </summary>
    public static async Task<List<string>> InstallFromFileAsync(string file, string dir, CancellationToken ct)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var added = new List<string>();
        if (ext is ".addon64" or ".addon32")
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            added.Add(Path.GetFileName(file));
            return added;
        }
        if (ext is not (".zip" or ".7z" or ".rar")) throw new InvalidOperationException(L.T("Pick a .addon64 / .addon32 file or the .zip / .7z / .rar it came in."));
        var temp = Path.Combine(AppInfo.CacheDir, "renodx-unpack", Guid.NewGuid().ToString("N"));
        try
        {
            await Opti.ArchiveExtractor.ExtractAsync(file, temp, ct);
            foreach (var f in Directory.GetFiles(temp, "*.addon*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)))
            {
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
                added.Add(Path.GetFileName(f));
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
        if (added.Count == 0) throw new InvalidOperationException(L.T("No .addon64 / .addon32 file in that archive."));
        return added;
    }

    [GeneratedRegex(@"\[!\[([^\]]*)\]\([^)]*\)\]\(([^)\s]+)\)")]
    private static partial Regex LinkRx();

    // The snapshot badge: "img.shields.io/github/last-commit/<owner>/renodx?path=src%2Fgames%2F<folder>".
    [GeneratedRegex(@"last-commit/([A-Za-z0-9_.-]+)/renodx\?path=src%2Fgames%2F([A-Za-z0-9_.%-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRx();

    [GeneratedRegex(@"^\s*\.(key|label|section|tooltip|labels|group)\s*=\s*(.*?),?\s*$")]
    private static partial Regex FieldRx();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex QuotedRx();

    /// <summary>
    /// The settings of a mod, read from its addon.cpp on GitHub (cached for a day): every non-commented setting block's label,
    /// section, tooltip and choices. Buttons (links to Discord, Ko-fi...) are left out. Null when the source cannot be read.
    /// </summary>
    public static async Task<RenoDxFeatures?> GetFeaturesAsync(RenoDxMod mod, CancellationToken ct = default)
    {
        if (mod.SourceOwner is null || mod.SourceDir is null) return null;
        var url = $"https://raw.githubusercontent.com/{mod.SourceOwner}/renodx/main/src/games/{mod.SourceDir}/addon.cpp";
        var code = await Http.GetCachedAsync(url, Path.Combine("renodx", $"{mod.SourceOwner}-{mod.SourceDir}.cpp"), TimeSpan.FromDays(1), ct: ct);
        if (code is null) return null;
        DateTime? updated = null;
        try
        {
            var json = await Http.GetCachedAsync($"https://api.github.com/repos/{mod.SourceOwner}/renodx/commits?path=src/games/{mod.SourceDir}&per_page=1",
                Path.Combine("renodx", $"{mod.SourceOwner}-{mod.SourceDir}-commit.json"), TimeSpan.FromDays(1), accept: "application/vnd.github+json", ct: ct);
            if (json is not null && Regex.Match(json, @"""date""\s*:\s*""([^""]+)""") is { Success: true } d && DateTime.TryParse(d.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when))
                updated = when.ToLocalTime();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"RenoDX last change of {mod.Name}: {ex.Message}"); }
        return new RenoDxFeatures(ParseSettings(code), updated, $"https://github.com/{mod.SourceOwner}/renodx/tree/main/src/games/{mod.SourceDir}");
    }

    /// <summary>Settings blocks of an addon.cpp ("new renodx::utils::settings::Setting{ .key = ..., .label = ..., .section = ... }").</summary>
    public static List<(string Section, List<RenoDxSetting> Settings)> ParseSettings(string code)
    {
        var result = new List<(string Section, List<RenoDxSetting> Settings)>();
        string? label = null, section = null, tooltip = null, type = null;
        List<string> choices = [];
        bool inBlock = false, blockComment = false;
        void Flush()
        {
            if (inBlock && label is { Length: > 0 } && type?.Contains("BUTTON", StringComparison.OrdinalIgnoreCase) != true && type?.Contains("TEXT", StringComparison.OrdinalIgnoreCase) != true)
            {
                var name = section is { Length: > 0 } ? section : "General";
                var group = result.FirstOrDefault(g => g.Section == name);
                if (group.Settings is null) { group = (name, []); result.Add(group); }
                if (!group.Settings.Any(x => x.Label == label)) group.Settings.Add(new RenoDxSetting(label, tooltip, choices));
            }
            label = tooltip = type = null;
            choices = [];
            inBlock = false;
        }
        foreach (var raw in code.Split('\n'))
        {
            var line = raw.Trim();
            if (blockComment) { if (line.Contains("*/")) blockComment = false; continue; }
            if (line.StartsWith("/*")) { if (!line.Contains("*/")) blockComment = true; continue; }
            if (line.StartsWith("//")) continue;
            if (line.Contains("new renodx::utils::settings::Setting", StringComparison.Ordinal) || line.StartsWith("new Setting", StringComparison.Ordinal))
            {
                Flush();
                inBlock = true;
                section = null;
                continue;
            }
            if (!inBlock) continue;
            if (line.StartsWith(".value_type", StringComparison.Ordinal)) { type = line; continue; }
            var m = FieldRx().Match(line);
            if (!m.Success) continue;
            var quoted = QuotedRx().Matches(m.Groups[2].Value).Select(q => q.Groups[1].Value.Replace("\\\"", "\"")).ToList();
            switch (m.Groups[1].Value)
            {
                case "label" when quoted.Count > 0: label = quoted[0]; break;
                case "section" when quoted.Count > 0: section = quoted[0]; break;
                case "tooltip" when quoted.Count > 0: tooltip = string.Concat(quoted); break;
                case "labels": choices = quoted; break;
            }
        }
        Flush();
        return result.Where(g => g.Settings.Count > 0).ToList();
    }

    [GeneratedRegex(@"\[!\[[^\]]*\]\([^)]*\)\]\([^)]*\)")]
    private static partial Regex BadgeRx();

    [GeneratedRegex(@"\(#\s+""([^""]+)""\)")]
    private static partial Regex NoteRx();
}
