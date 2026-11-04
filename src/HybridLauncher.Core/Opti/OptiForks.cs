namespace HybridLauncher.Core.Opti;

/// <summary>
/// OptiScaler forks Hybrid Launcher offers in the OptiScaler tab's Forks/Unofficial Builds channel: community repositories that publish their own
/// builds (new features ahead of the official ones, builds for one graphics card or one game family). The official stable and
/// nightly channels stay as they are; the user can add any other GitHub repository that publishes OptiScaler releases.
/// Every fork installs through its own setup_windows.bat, like the official builds.
/// </summary>
public static class OptiForks
{
    /// <summary>Forks known to publish release archives (name, "owner/name").</summary>
    public static readonly OptiFork[] Known =
    [
        new() { Name = "OptiScaler Venom (LorenzoKy)", Repo = "LorenzoKy/Optiscaler-Venom" },
        new() { Name = "F5 DLSS NR Multipass (janblade)", Repo = "janblade/OptiScaler-F5-DLSSNR-Multipass" },
        new() { Name = "DLSS NR Multipass MFG (y4my4my4m)", Repo = "y4my4my4m/OptiScaler_DLSSNR_Multipass_MFG" },
        new() { Name = "NR PreSR MFG (ShyVortex)", Repo = "ShyVortex/OptiScaler-NR-PreSR-MFG" },
        new() { Name = "FSR Ray Regeneration (ZachHembree)", Repo = "ZachHembree/OptiScaler" },
    ];

    public static IEnumerable<OptiFork> All(AppSettings s) =>
        Known.Concat(s.OptiForks.Where(u => !Known.Any(k => k.Repo.Equals(u.Repo, StringComparison.OrdinalIgnoreCase))));

    /// <summary>"https://github.com/owner/name/releases", "github.com/owner/name" or "owner/name" to "owner/name"; null if it is none of them.</summary>
    public static string? ParseRepo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        foreach (var prefix in new[] { "https://", "http://" })
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) t = t[prefix.Length..];
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = t[4..];
        if (t.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) t = t["github.com/".Length..];
        var parts = t.Split(['/', '?', '#'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        string name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        static bool Valid(string s) => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
        return Valid(parts[0]) && Valid(name) ? $"{parts[0]}/{name}" : null;
    }
}
