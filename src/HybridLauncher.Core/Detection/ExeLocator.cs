using System.Text.RegularExpressions;

namespace HybridLauncher.Core.Detection;

/// <summary>
/// Finds the executable that actually renders the game, which is where OptiScaler must go.
/// Unreal games keep it in &lt;Project&gt;\Binaries\Win64 (or WinGDK on Game Pass), behind a small bootstrap exe.
/// </summary>
public static partial class ExeLocator
{
    const int MaxDepth = 5;
    const int MaxDirs = 4000;

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "CommonRedist", "Redist", "Redistributables", "redist", "DirectX", "DirectX9", "dotNetFx", "vcredist",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "Support", "__Installer", "Installer", "Installers", "Prereqs",
        "PhysX", "CustomInstall", "_manifest", "Microsoft.VCLibs", "steamapps", "shadercache", "Logs", "Saved",
        "Content", "Movies", "Paks", "Data", "Mods", "plugins", "OptiScaler", "D3D12_Optiscaler", "Licenses",
    };

    [GeneratedRegex(@"(^unins|crash|redist|setup|install|^vc_|dxsetup|helper|report|updater|uploader|^cef|^ue4prereq|^dotnet|easyanticheat|^be(service|launcher)|start_protected_game|gamelaunchhelper|^quicksfv|touchup|cleanup|^dxwebsetup|^oalinst|^vcredist)", RegexOptions.IgnoreCase)]
    private static partial Regex JunkName();

    [GeneratedRegex(@"launcher|^REDprelauncher", RegexOptions.IgnoreCase)]
    private static partial Regex LauncherName();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlnum();

    public static string? Locate(string installDir, string? storeExe, string gameName)
    {
        try
        {
            if (!Directory.Exists(installDir)) return null;
            var candidates = Enumerate(installDir).ToList();
            if (candidates.Count == 0)
                return storeExe is not null && File.Exists(storeExe) ? storeExe : null;

            string normName = Normalize(gameName);
            string? normStore = storeExe is null ? null : Path.GetFullPath(storeExe);

            return candidates
                .Select(c => (Path: c, Score: Score(c, installDir, normStore, normName)))
                .OrderByDescending(x => x.Score)
                .First().Path;
        }
        catch (Exception ex)
        {
            Log.Error($"ExeLocator failed for {installDir}", ex);
            return null;
        }
    }

    static double Score(string exe, string root, string? storeExe, string normName)
    {
        var file = Path.GetFileNameWithoutExtension(exe);
        var dir = Path.GetDirectoryName(exe) ?? "";
        double score = 0;

        bool inUeBinaries = dir.EndsWith(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase)
                         || dir.EndsWith(@"\Binaries\WinGDK", StringComparison.OrdinalIgnoreCase);
        var rel = "\\" + Path.GetRelativePath(root, dir) + "\\";
        bool engineDir = rel.Contains(@"\Engine\", StringComparison.OrdinalIgnoreCase);

        if (inUeBinaries && !engineDir) score += 2000;
        if (file.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)) score += 500;
        if (engineDir) score -= 3000;
        if (storeExe is not null && exe.Equals(storeExe, StringComparison.OrdinalIgnoreCase)) score += 800;
        if (LauncherName().IsMatch(file)) score -= 600;

        var normFile = Normalize(file);
        if (normFile.Length >= 3 && normName.Length >= 3 && (normName.Contains(normFile) || normFile.Contains(normName)))
            score += 150;

        long size = 0;
        try { size = new FileInfo(exe).Length; } catch { }
        // Big exes are usually the game; small ones bootstraps. Log keeps a 50 MB exe from beating everything.
        score += Math.Log10(Math.Max(size, 1)) * 60;

        // Prefer shallower paths a little.
        score -= (exe.Length - root.Length) / 40.0;
        return score;
    }

    static IEnumerable<string> Enumerate(string root)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;

        while (queue.Count > 0 && visited < MaxDirs)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;

            string[] files = [], subdirs = [];
            try
            {
                files = Directory.GetFiles(dir, "*.exe");
                if (depth < MaxDepth) subdirs = Directory.GetDirectories(dir);
            }
            catch { continue; }

            foreach (var f in files)
                if (!JunkName().IsMatch(Path.GetFileNameWithoutExtension(f)))
                    yield return f;

            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (SkipDirs.Contains(name)) continue;
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                queue.Enqueue((d, depth + 1));
            }
        }
    }

    public static string Normalize(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");
}
