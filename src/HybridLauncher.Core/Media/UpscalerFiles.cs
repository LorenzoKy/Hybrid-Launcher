using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HybridLauncher.Core.Media;

/// <summary>One upscaler or frame generation library found in a game's folder ("DLSS 4.5" from nvngx_dlss.dll 310.5.0).</summary>
public sealed record UpscalerFile(string Family, string Tag, string RelativePath, string Version, bool FrameGen, double Number);

/// <summary>
/// The upscaler versions a game really ships, read from its libraries' version numbers: the wiki says "DLSS 4" or "FSR 4",
/// the game's own nvngx_dlss.dll says 3.7 or 310.5 (DLSS 4.5), amd_fidelityfx_upscaler_dx12.dll 4.1. The tags on the covers
/// and the game page use these where the game has them (see <see cref="Merge"/>). OptiScaler's own copies are left out.
/// </summary>
public static class UpscalerFiles
{
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    static readonly Dictionary<string, (DateTime At, List<UpscalerFile> Files)> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Folders that hold OptiScaler's (or a mod's) libraries, not the game's.
    static readonly string[] SkipDirs = ["OptiScaler", "D3D12_Optiscaler", "streamline_opti", "Hybrid Launcher backup", "Hybrid Game Launcher backup", "_backup", "backup"];

    // Files OptiScaler drops next to its dll: in a folder with OptiScaler.ini they are its own, not the game's.
    static readonly HashSet<string> OptiPayload = new(StringComparer.OrdinalIgnoreCase)
    {
        "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll",
        "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_loader_dx12.dll",
    };

    /// <summary>The libraries in <paramref name="gameDir"/> (six folders deep), cached for a day. Empty when the folder is missing.</summary>
    public static List<UpscalerFile> Scan(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return [];
        lock (Cache)
            if (Cache.TryGetValue(gameDir, out var hit) && DateTime.UtcNow - hit.At < MaxAge) return hit.Files;
        var found = new List<UpscalerFile>();
        try { Walk(gameDir, gameDir, 0, found); }
        catch (Exception ex) { Log.Warn($"Upscaler files in {gameDir}: {ex.Message}"); }
        lock (Cache) Cache[gameDir] = (DateTime.UtcNow, found);
        return found;
    }

    /// <summary>Forgets what was read for a folder (after an install, an update or OptiScaler changes it).</summary>
    public static void Forget(string? gameDir)
    {
        if (gameDir is null) return;
        lock (Cache) Cache.Remove(gameDir);
    }

    static readonly EnumerationOptions Shallow = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    static void Walk(string root, string dir, int depth, List<UpscalerFile> found)
    {
        bool opti = File.Exists(Path.Combine(dir, "OptiScaler.ini"));
        foreach (var file in Directory.EnumerateFiles(dir, "*.dll", Shallow))
        {
            var name = Path.GetFileName(file);
            if (opti && OptiPayload.Contains(name)) continue;
            if (Read(file, name) is { } f) found.Add(f with { RelativePath = Path.GetRelativePath(root, file) });
        }
        if (depth >= 6) return;
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", Shallow))
        {
            var leaf = Path.GetFileName(sub);
            if (SkipDirs.Any(s => leaf.Equals(s, StringComparison.OrdinalIgnoreCase)) || leaf.StartsWith('.')) continue;
            Walk(root, sub, depth + 1, found);
        }
    }

    /// <summary>What one library is, from its name and version resource; null for anything else.</summary>
    internal static UpscalerFile? Read(string path, string name)
    {
        var lower = name.ToLowerInvariant();
        bool known = lower is "nvngx_dlss.dll" or "nvngx_dlssg.dll" or "libxess.dll" or "libxess_fg.dll" or "amd_fidelityfx_upscaler_dx12.dll"
            or "amd_fidelityfx_framegeneration_dx12.dll" or "amd_fidelityfx_dx12.dll" || lower.StartsWith("ffx_fsr2_api") || lower.StartsWith("ffx_fsr3");
        if (!known) return null;
        FileVersionInfo info;
        try { info = FileVersionInfo.GetVersionInfo(path); }
        catch { return null; }
        int major = info.FileMajorPart, minor = info.FileMinorPart, build = info.FileBuildPart;
        string version = $"{major}.{minor}.{build}";
        string desc = info.FileDescription ?? "";
        return lower switch
        {
            // 2.x / 3.x carry the version as is; 310.x is DLSS 4, and 310.5 onwards DLSS 4.5 (the transformer model's second generation).
            "nvngx_dlss.dll" when major >= 310 => Make("DLSS", minor >= 5 ? "DLSS 4.5" : "DLSS 4", minor >= 5 ? 4.5 : 4),
            "nvngx_dlss.dll" when major is >= 1 and < 10 => Make("DLSS", Short("DLSS", major, minor), major + minor / 10.0),
            "nvngx_dlssg.dll" => Make("DLSS", desc.Contains("MFG", StringComparison.OrdinalIgnoreCase) ? "DLSS MFG" : "DLSS FG", major, true),
            "libxess.dll" when major >= 1 => Make("XeSS", Short("XeSS", major, minor), major + minor / 10.0),
            "libxess_fg.dll" => Make("XeSS", "XeSS FG", major, true),
            // FidelityFX SDK 2: one library per effect, its version is the effect's (4.0.3, 4.1.1).
            "amd_fidelityfx_upscaler_dx12.dll" when major >= 3 => Make("FSR", Short("FSR", major, minor), major + minor / 10.0),
            "amd_fidelityfx_framegeneration_dx12.dll" => Make("FSR", "FSR FG", major, true),
            // FidelityFX SDK 1.x: FSR 3.1 in one library (SDK 2's loader shares the name: its version says nothing about FSR).
            "amd_fidelityfx_dx12.dll" when !desc.Contains("Loader", StringComparison.OrdinalIgnoreCase) && major <= 1 => Make("FSR", "FSR 3.1", 3.1),
            _ when lower.StartsWith("ffx_fsr2_api") => Make("FSR", "FSR 2", 2),
            _ when lower.StartsWith("ffx_fsr3") => Make("FSR", "FSR 3", 3),
            _ => null,
        };

        UpscalerFile Make(string family, string tag, double number, bool frameGen = false) => new(family, tag, name, version, frameGen, number);
    }

    /// <summary>"DLSS" 3 7 → "DLSS 3.7"; "XeSS" 2 0 → "XeSS 2".</summary>
    static string Short(string family, int major, int minor) => minor == 0 ? $"{family} {major}" : $"{family} {major}.{minor}";

    /// <summary>
    /// The wiki's tags with the game's own versions in their place: one upscaler tag per family, the newest library the game
    /// ships winning over what the wiki writes (a game listed as "DLSS 4" that ships 3.7 reads "DLSS 3.7"). The wiki's "FSR 4" for
    /// a DX12 game with a swappable FSR 3.1 stays (AMD's driver runs FSR 4 in its place). Frame generation found in the files
    /// joins the wiki's. Upscalers first, then frame generation, in the usual order.
    /// </summary>
    public static List<string> Merge(IEnumerable<string> wikiTags, IReadOnlyList<UpscalerFile> files)
    {
        var tags = wikiTags.ToList();
        if (files.Count == 0) return tags;
        var upscalers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frameGen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags)
        {
            if (IsFrameGen(t)) frameGen.Add(t);
            else upscalers[t.Split(' ')[0]] = t;
        }
        foreach (var family in files.Where(f => !f.FrameGen).GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase))
        {
            var best = family.MaxBy(f => f.Number)!;
            if (best.Family == "FSR" && best.Number < 4 && upscalers.TryGetValue("FSR", out var wikiFsr) && wikiFsr == "FSR 4" && best.Number >= 3.1) continue;
            upscalers[best.Family] = best.Tag;
        }
        foreach (var f in files.Where(f => f.FrameGen)) frameGen.Add(f.Tag);
        if (frameGen.Contains("DLSS MFG")) frameGen.Remove("DLSS FG");
        string[] order = ["DLSS", "FSR", "XeSS", "TSR", "PSSR"];
        string[] fgOrder = ["DLSS MFG", "DLSS FG", "FSR FG", "XeSS FG"];
        return order.Where(upscalers.ContainsKey).Select(f => upscalers[f])
            .Concat(upscalers.Keys.Where(k => !order.Contains(k, StringComparer.OrdinalIgnoreCase)).Select(k => upscalers[k]))
            .Concat(fgOrder.Where(frameGen.Contains)).Concat(frameGen.Where(f => !fgOrder.Contains(f))).ToList();
    }

    static bool IsFrameGen(string t) => Regex.IsMatch(t, @"\b(M?FG)\b", RegexOptions.IgnoreCase);

    /// <summary>"nvngx_dlss.dll 310.5.0, libxess.dll 2.0.2": what the tags were read from, for a tooltip.</summary>
    public static string Describe(IReadOnlyList<UpscalerFile> files) =>
        string.Join("\n", files.GroupBy(f => f.Tag).Select(g => $"{g.Key}: {Path.GetFileName(g.First().RelativePath)} {g.MaxBy(f => f.Number)!.Version}"));
}
