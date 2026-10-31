using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Mods;

public enum LumaStatus { Working, InProgress, Planned }

/// <summary>A download of a Luma mod: the GitHub build ("Luma-Prey.zip", the asset name), Nexus Mods or GameBanana.</summary>
public sealed record LumaLink(string Kind, string Url)
{
    /// <summary>For GitHub builds: the asset name without ".zip" and without the bitness ("Luma-Prey", "Luma-Unity_Engine").</summary>
    public string? Asset => Kind == "GitHub" && Regex.Match(Url, @"/download/(Luma-[^/]+?)(?:-x32)?\.zip$") is { Success: true } m ? m.Groups[1].Value : null;
    public bool Is32Bit => Url.EndsWith("-x32.zip", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One mod of the Luma list: a game, or (Unreal, Unity) a generic mod with the games it was tested with.</summary>
public sealed class LumaMod
{
    public required string Name { get; init; }
    public string Author { get; init; } = "";
    public List<LumaLink> Links { get; init; } = [];
    public LumaStatus Status { get; init; }
    public bool MoreComing { get; init; }
    public string? Notes { get; init; }
    public List<string> Features { get; set; } = [];
    /// <summary>"Unreal" for the generic Unreal Engine mod; its tested games with their notes.</summary>
    public string? Engine { get; init; }
    public List<(string Game, string Upscaling, string Hdr, string Notes)> Tested { get; } = [];

    public IEnumerable<string> Assets => Links.Select(l => l.Asset).Where(a => a is not null).Distinct().Select(a => a!);
    public LumaLink? Nexus => Links.FirstOrDefault(l => l.Kind == "Nexus Mods");
    public LumaLink? Other => Links.FirstOrDefault(l => l.Kind is "GameBanana" or "Link");
}

/// <summary>A build of the Luma releases: "latest-761" with the zip of every mod.</summary>
public sealed record LumaBuild(string Tag, string Name, DateTime Published, Dictionary<string, (string Url, long Size)> Assets);

/// <summary>One version of a mod offered for install: which build, which asset file, stable or test.</summary>
public sealed record LumaVersion(LumaBuild Build, string Asset, string Url, long Size, bool Test, bool Is32Bit)
{
    public string Display => $"{Build.Tag.Replace("latest-", "Build ")}  ·  {Build.Published:d MMM yyyy}" + (Test ? "  ·  Test" : "") + (Is32Bit ? "  ·  32-bit" : "");
}

public sealed record LumaMatch(LumaMod Mod, string Reason, int Score);

/// <summary>
/// Luma Framework (Filoppi / Pumbo and contributors): per-game ReShade add-on mods for DirectX 11 games that rewrite the
/// post-processing for HDR, and often add DLSS / FSR. The list and each mod's features come from the project's GitHub wiki;
/// the builds from its GitHub releases (one build has a zip per mod, plus a "Test" and a 32-bit one when there is).
/// A Luma zip carries ReShade (dxgi.dll), the mod (.addon) and its shaders: Hybrid Launcher copies them next to the game exe, keeping a
/// backup of every file it replaces. With OptiScaler in the folder, ReShade goes in as ReShade64.dll and OptiScaler loads it
/// (LoadReshade = true), as the OptiScaler wiki describes for ReShade, so both work together.
/// </summary>
public static partial class Luma
{
    const string WikiUrl = "https://raw.githubusercontent.com/wiki/Filoppi/Luma-Framework/Home.md";
    public const string WikiPage = "https://github.com/Filoppi/Luma-Framework/wiki";
    public const string ReleasesPage = "https://github.com/Filoppi/Luma-Framework/releases";

    public static string Dir => Directory.CreateDirectory(Path.Combine(AppInfo.DataRoot, "Luma")).FullName;

    public static async Task<List<LumaMod>> GetModsAsync(bool force = false, CancellationToken ct = default)
    {
        var md = await Http.GetCachedAsync(WikiUrl, "luma-home.md", TimeSpan.FromHours(12), force, ct: ct);
        return md is null ? [] : Parse(md);
    }

    [GeneratedRegex(@"\[!\[([^\]]*)\]\[?[^\]\)]*\]?\)?\]\(([^)\s]+)\)|\[\*\*([^*\]]+)\*\*\]\(([^)\s]+)\)|\[([^\]!]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex LinkRx();

    public static List<LumaMod> Parse(string md)
    {
        var mods = new List<LumaMod>();
        string? section = null, sub = null;
        LumaMod? unreal = null;
        var features = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# "))
            {
                section = line[2..].Trim();
                sub = null;
                if (section.StartsWith("Unreal Engine", StringComparison.OrdinalIgnoreCase))
                {
                    unreal = new LumaMod { Name = "Unreal Engine (generic)", Author = "Luma", Status = LumaStatus.Working, Engine = "Unreal",
                        Links = [new LumaLink("GitHub", "https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Unreal_Engine.zip")],
                        Notes = "Replaces the default UE4 TAA with DLSS (DLAA) or FSR 3, and adds native HDR; needs DirectX 11 (-dx11 for games that default to DX12).",
                        Features = ["DLSS (DLAA) or FSR 3 in place of the engine's TAA", "Native HDR output", "Works in many Unreal Engine 4 games (see the tested list)"] };
                    mods.Add(unreal);
                }
                continue;
            }
            if (line.StartsWith("## "))
            {
                sub = Plain(line[3..]).Trim();
                if (section?.StartsWith("Mod Features", StringComparison.OrdinalIgnoreCase) == true) features[sub] = [];
                continue;
            }
            if (section?.StartsWith("Mod Features", StringComparison.OrdinalIgnoreCase) == true && sub is not null && features.TryGetValue(sub, out var list))
            {
                var t = line.Trim();
                if (t.StartsWith("* ") || t.StartsWith("- ")) list.Add(Plain(t[2..]).Trim());
                continue;
            }
            if (!line.StartsWith('|') || section is null) continue;
            var cells = line.Trim().Trim('|').Split('|');
            var name = Plain(cells[0]).Trim();
            if (name.Length == 0 || name == "Name" || name.StartsWith("---") || name.StartsWith(":-")) continue;

            if (section.StartsWith("Unreal Engine", StringComparison.OrdinalIgnoreCase) && unreal is not null)
            {
                if (cells.Length >= 3) unreal.Tested.Add((name, cells[1].Trim(), cells[2].Trim(), cells.Length > 3 ? Plain(Regex.Replace(cells[3], "<details>.*?</details>", "")).Trim() : ""));
                continue;
            }
            bool completed = section.StartsWith("Completed", StringComparison.OrdinalIgnoreCase);
            bool wip = section.StartsWith("WIP", StringComparison.OrdinalIgnoreCase);
            if (!completed && !wip) continue;
            string statusCell = completed ? (cells.Length > 3 ? cells[3] : "") : (cells.Length > 2 ? cells[2] : "");
            var status = statusCell.Contains("✅") ? LumaStatus.Working : statusCell.Contains("💡") ? LumaStatus.Planned : LumaStatus.InProgress;
            var links = completed && cells.Length > 2 ? Links(cells[2]) : [];
            string? notes = completed && cells.Length > 4 ? Plain(Regex.Replace(cells[4], "<details>(.*?)</details>", m => " " + Regex.Replace(m.Groups[1].Value, "<summary>(.*?)</summary>", "$1:"))).Trim() : null;
            mods.Add(new LumaMod
            {
                Name = name,
                Author = cells.Length > 1 ? Plain(cells[1]).Trim() : "",
                Links = links,
                Status = status,
                MoreComing = statusCell.Contains("➕"),
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
            });
        }
        foreach (var m in mods)
            if (m.Features.Count == 0 && features.TryGetValue(m.Name, out var f)) m.Features = f;
        return mods;
    }

    static List<LumaLink> Links(string cell)
    {
        var list = new List<LumaLink>();
        foreach (Match m in LinkRx().Matches(cell))
        {
            var url = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? m.Groups[4].Value : m.Groups[6].Value;
            if (url.Length == 0 || list.Any(l => l.Url == url)) continue;
            string kind = url.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase) ? "Nexus Mods"
                : url.Contains("gamebanana.com", StringComparison.OrdinalIgnoreCase) ? "GameBanana"
                : url.Contains("github.com/Filoppi/Luma-Framework/releases", StringComparison.OrdinalIgnoreCase) && url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "GitHub"
                : "Link";
            list.Add(new LumaLink(kind, url));
        }
        return list;
    }

    static string Plain(string s) => System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(Regex.Replace(s, @"\[!\[[^\]]*\][^\)]*\)\]\([^)]*\)", ""),
        @"\[([^\]]*)\]\([^)]*\)", "$1"), @"<br\s*/?>|<[^>]+>|\*\*|`|📌|✅|➕|🚧|💡|⛔", " ")).Replace("  ", " ").Trim();

    /// <summary>The mods for a game: its own entry (by name), then the generic Unreal mod when the game is in its tested list.</summary>
    public static List<LumaMatch> Match(IEnumerable<LumaMod> mods, string gameName, string? engine)
    {
        var key = RenoDx.Key(gameName);
        var hits = new List<LumaMatch>();
        foreach (var m in mods)
        {
            if (m.Engine is not null)
            {
                var tested = m.Tested.FirstOrDefault(t => RenoDx.Key(t.Game) == key || RenoDx.Key(t.Game).StartsWith(key + " ") || key.StartsWith(RenoDx.Key(t.Game) + " "));
                if (tested.Game is not null)
                    hits.Add(new LumaMatch(m, L.F("Tested with {0}: upscaling {1}, HDR {2}", tested.Game, Mark(tested.Upscaling), Mark(tested.Hdr)) + (tested.Notes.Length > 0 ? ". " + tested.Notes : ""), 80));
                else if (engine == m.Engine)
                    hits.Add(new LumaMatch(m, L.F("Generic mod for {0} games: not tested with this one yet", engine), 30));
                continue;
            }
            var mk = RenoDx.Key(m.Name);
            if (mk == key) hits.Add(new LumaMatch(m, L.T("Made for this game"), 100));
            else if (mk.Length > 3 && (key.StartsWith(mk + " ") || mk.StartsWith(key + " ") || mk.Contains(" " + key) || mk.Split(" + ").Any(p => p.Trim() == key)))
                hits.Add(new LumaMatch(m, L.T("Same game or series"), 70));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Mod.Status).ToList();

        static string Mark(string s) => s.Trim().Length == 0 ? L.T("not tested") : s.Contains("⛔") ? L.T("no") : s.Contains("🚧") ? L.T("partly") : L.T("yes");
    }

    // ---------- builds ----------

    /// <summary>The last 30 builds on GitHub (cached for 6 hours), newest first.</summary>
    public static async Task<List<LumaBuild>> GetBuildsAsync(bool force = false, CancellationToken ct = default)
    {
        var json = await Http.GetCachedAsync("https://api.github.com/repos/Filoppi/Luma-Framework/releases?per_page=30", "luma-releases.json",
            TimeSpan.FromHours(6), force, accept: "application/vnd.github+json", ct: ct);
        var list = new List<LumaBuild>();
        if (json is null) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var assets = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in r.GetProperty("assets").EnumerateArray())
                assets[a.GetProperty("name").GetString()!] = (a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64());
            DateTime.TryParse(r.GetProperty("published_at").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var published);
            list.Add(new LumaBuild(r.GetProperty("tag_name").GetString()!, r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", published.ToLocalTime(), assets));
        }
        return list.OrderByDescending(b => b.Published).ToList();
    }

    /// <summary>
    /// The versions of a mod worth offering, best first: the newest stable build for the game's bitness, then its test build,
    /// then older stable builds (when a newer one breaks something). A 64-bit game never gets a 32-bit file and the other way round.
    /// </summary>
    public static List<LumaVersion> Versions(LumaMod mod, List<LumaBuild> builds, bool want32)
    {
        var list = new List<LumaVersion>();
        foreach (var asset in mod.Assets)
        {
            foreach (var b in builds)
            {
                foreach (bool test in new[] { false, true })
                {
                    var file = asset + (test ? "-Test" : "") + (want32 ? "-x32" : "") + ".zip";
                    if (b.Assets.TryGetValue(file, out var a)) list.Add(new LumaVersion(b, file, a.Url, a.Size, test, want32));
                }
            }
        }
        var newestStable = list.Where(v => !v.Test).OrderByDescending(v => v.Build.Published).FirstOrDefault();
        var newestTest = list.Where(v => v.Test).OrderByDescending(v => v.Build.Published).FirstOrDefault();
        var older = list.Where(v => !v.Test && v != newestStable).OrderByDescending(v => v.Build.Published)
            // Many builds change nothing for one game: keep one per week.
            .GroupBy(v => ISOWeek.GetYear(v.Build.Published) * 100 + ISOWeek.GetWeekOfYear(v.Build.Published)).Select(g => g.First()).Take(8);
        return new[] { newestStable, newestTest }.Where(v => v is not null).Select(v => v!).Concat(older).ToList();
    }

    // ---------- install ----------

    /// <summary>Luma's files that are in a game folder now (its .addon files and the Luma folder), to tell whether it is installed.</summary>
    public static List<string> Installed(string dir)
    {
        var list = new List<string>();
        try { list.AddRange(Directory.GetFiles(dir, "Luma*.addon*")); } catch { }
        if (Directory.Exists(Path.Combine(dir, "Luma"))) list.Add(Path.Combine(dir, "Luma"));
        return list;
    }

    /// <summary>
    /// Downloads the zip (kept under Hybrid Launcher's data, so a reinstall needs no download), then copies it next to the game exe:
    /// every file it replaces is backed up first. With OptiScaler there (<paramref name="optiPresent"/>), the package's ReShade
    /// (dxgi.dll) goes in as ReShade64.dll and LoadReshade is turned on in OptiScaler.ini.
    /// </summary>
    public static async Task<LumaState> InstallAsync(LumaMod mod, LumaVersion version, string dir, bool optiPresent, LumaState? previous,
        IProgress<double>? progress, CancellationToken ct, bool backup = true)
    {
        var zip = Path.Combine(Directory.CreateDirectory(Path.Combine(Dir, version.Build.Tag)).FullName, version.Asset);
        if (!File.Exists(zip) || new FileInfo(zip).Length != version.Size)
            await Http.DownloadFileAsync(version.Url, zip, version.Size, progress, ct);
        if (previous is not null) Uninstall(previous, dir);

        var state = new LumaState { Mod = mod.Name, Asset = version.Asset, Release = version.Build.Tag, InstalledAt = DateTime.Now, ThroughOpti = optiPresent };
        var backupRoot = Path.Combine(AppInfo.DataRoot, "backups", "luma", Path.GetFileName(dir) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && entry.Name.Length == 0) continue;
                var rel = entry.FullName.Replace('/', '\\');
                // Two ReShades cannot both be the game's dxgi.dll: with OptiScaler, ReShade is loaded by it.
                if (optiPresent && rel.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)) rel = ReShade.OptiDllName;
                var dest = Path.GetFullPath(Path.Combine(dir, rel));
                if (!dest.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue; // never outside the folder
                if (backup && File.Exists(dest))
                {
                    var copy = Path.Combine(backupRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(dest, copy, overwrite: true);
                    state.Backups[rel] = copy;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                state.Files.Add(rel);
            }
            if (optiPresent)
            {
                var ini = Path.Combine(dir, "OptiScaler.ini");
                if (File.Exists(ini))
                {
                    var doc = Ini.IniDocument.Load(ini);
                    if (doc.Set("Plugins", "LoadReshade", "true")) doc.Save();
                }
            }
        }, ct);
        Log.Info($"Luma: {mod.Name} {version.Asset} ({version.Build.Tag}) installed in {dir}, {state.Files.Count} files{(optiPresent ? ", ReShade through OptiScaler" : "")}");
        return state;
    }

    /// <summary>Removes the files Luma added (and the folders it left empty), and puts back every file it replaced.</summary>
    public static List<string> Uninstall(LumaState state, string dir)
    {
        var problems = new List<string>();
        foreach (var rel in state.Files)
        {
            var path = Path.Combine(dir, rel);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        foreach (var (rel, backup) in state.Backups)
        {
            try { if (File.Exists(backup)) File.Copy(backup, Path.Combine(dir, rel), overwrite: true); }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        // Folders the package created and that are empty now (deepest first).
        foreach (var folder in state.Files.Select(f => Path.GetDirectoryName(f)).Where(f => !string.IsNullOrEmpty(f)).Distinct()
                     .SelectMany(f => Enumerable.Range(1, f!.Count(c => c == '\\') + 1).Select(n => string.Join('\\', f!.Split('\\').Take(n)))).Distinct()
                     .OrderByDescending(f => f.Length))
        {
            var full = Path.Combine(dir, folder);
            try { if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full); } catch { }
        }
        if (state.ThroughOpti && !File.Exists(Path.Combine(dir, ReShade.OptiDllName)))
        {
            var ini = Path.Combine(dir, "OptiScaler.ini");
            try
            {
                if (File.Exists(ini))
                {
                    var doc = Ini.IniDocument.Load(ini);
                    if (doc.Set("Plugins", "LoadReshade", "false")) doc.Save();
                }
            }
            catch (Exception ex) { problems.Add($"OptiScaler.ini: {ex.Message}"); }
        }
        Log.Info($"Luma: {state.Mod} removed from {dir}{(problems.Count > 0 ? ", problems: " + string.Join("; ", problems) : "")}");
        return problems;
    }
}
