using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Hardware;

namespace HybridLauncher.Core.Opti;

/// <summary>What the PC's graphics card can do for DLSS 5 neural rendering (the Setup DLSS 5 window, 1.4.0-rc.13).</summary>
public enum Dlss5Gpu
{
    /// <summary>An NVIDIA RTX card: janblade's F5 with NVIDIA's NGX.</summary>
    NvidiaRtx,
    /// <summary>Radeon RX 9000 (RDNA4): DLSSNR-AMD with every network, or F5 with the vendor-neutral port.</summary>
    AmdRdna4,
    /// <summary>Radeon RX 7000 and Radeon 700M / 800M / 8000S (RDNA3, RDNA3.5): DLSSNR-AMD (experimental, two networks) or F5's port.</summary>
    AmdRdna3,
    /// <summary>An older Radeon: only F5's vendor-neutral port (DirectX 12 compute).</summary>
    AmdOlder,
    /// <summary>Intel Arc or Intel graphics: only F5's vendor-neutral port.</summary>
    Intel,
    /// <summary>No card Hybrid Launcher recognises.</summary>
    Unknown,
}

/// <summary>The ways DLSS 5 can run in a game: <see cref="F5"/> (janblade's OptiScaler fork) or <see cref="Amd"/> (mochizuki0323's DLSSNR-AMD).</summary>
public static class Dlss5
{
    public const string F5 = "f5", Amd = "amd";

    public const string F5Repo = "janblade/OptiScaler-F5-DLSSNR-Multipass";

    /// <summary>The card DLSS 5 would run on: an RTX card first, then the first AMD or Intel one.</summary>
    public static (Dlss5Gpu Tier, GpuInfo? Gpu) Detect()
    {
        var gpus = HardwareInfo.Gpus();
        if (gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia && g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase)) is { } rtx)
            return (Dlss5Gpu.NvidiaRtx, rtx);
        if (gpus.Where(g => g.Vendor == GpuVendor.Amd).OrderByDescending(g => Rank(TierOf(g))).FirstOrDefault() is { } amd)
            return (TierOf(amd), amd);
        if (gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Intel) is { } intel) return (Dlss5Gpu.Intel, intel);
        return (Dlss5Gpu.Unknown, gpus.FirstOrDefault());
    }

    static int Rank(Dlss5Gpu t) => t switch { Dlss5Gpu.AmdRdna4 => 3, Dlss5Gpu.AmdRdna3 => 2, _ => 1 };

    /// <summary>An AMD card's generation from its name (RX 9xxx RDNA4; RX 7xxx, Radeon 7x0M / 8x0M / 80x0S RDNA3).</summary>
    public static Dlss5Gpu TierOf(GpuInfo g)
    {
        var n = g.Name.ToUpperInvariant();
        if (Regex.IsMatch(n, @"\bRX\s*9\d{3}")) return Dlss5Gpu.AmdRdna4;
        if (Regex.IsMatch(n, @"\bRX\s*7\d{3}") || Regex.IsMatch(n, @"\b(7[4-9]0M|8[4-9]0M|80[4-6]0S)\b")) return Dlss5Gpu.AmdRdna3;
        return Dlss5Gpu.AmdOlder;
    }

    /// <summary>The ways that can run on <paramref name="tier"/>, the recommended one first.</summary>
    public static IReadOnlyList<string> MethodsFor(Dlss5Gpu tier) => tier switch
    {
        Dlss5Gpu.AmdRdna4 or Dlss5Gpu.AmdRdna3 => [Amd, F5],
        _ => [F5],
    };

    public static string Recommended(Dlss5Gpu tier) => MethodsFor(tier)[0];

    public static string TierName(Dlss5Gpu tier) => tier switch
    {
        Dlss5Gpu.NvidiaRtx => "NVIDIA RTX",
        Dlss5Gpu.AmdRdna4 => "AMD RDNA 4",
        Dlss5Gpu.AmdRdna3 => "AMD RDNA 3",
        Dlss5Gpu.AmdOlder => L.T("Older AMD"),
        Dlss5Gpu.Intel => "Intel",
        _ => L.T("Unknown card"),
    };

    // ---------- drivers ----------

    /// <summary>NVIDIA's own driver number (616.56) from Windows' (32.0.16.1656); null when it cannot be read.</summary>
    public static double? NvidiaDriver(GpuInfo g)
    {
        var m = Regex.Match(g.DriverVersion, @"(\d+)\.(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return null;
        var digits = (int.Parse(m.Groups[3].Value) % 10).ToString() + m.Groups[4].Value.PadLeft(4, '0');
        return int.Parse(digits) / 100.0;
    }

    /// <summary>AMD Software's year and month (26.9) from the driver's description; null when it does not say.</summary>
    public static (int Year, int Month)? AdrenalinVersion(GpuInfo g)
    {
        var m = Regex.Match(g.DriverVersion, @"Adrenalin\s+(?:Edition\s+)?(\d+)\.(\d+)");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    public const double NvidiaMinimum = 616.56;
    public static readonly (int Year, int Month) AdrenalinMinimum = (26, 9);

    /// <summary>The driver against what DLSS 5 needs: true new enough, false too old, null unknown.</summary>
    public static bool? DriverOk(Dlss5Gpu tier, GpuInfo? g)
    {
        if (g is null) return null;
        if (tier == Dlss5Gpu.NvidiaRtx) return NvidiaDriver(g) is { } v ? v >= NvidiaMinimum : null;
        if (tier is Dlss5Gpu.AmdRdna4 or Dlss5Gpu.AmdRdna3)
            return AdrenalinVersion(g) is { } a ? a.Year > AdrenalinMinimum.Year || a.Year == AdrenalinMinimum.Year && a.Month >= AdrenalinMinimum.Month : null;
        return null;
    }

    // ---------- the model file ----------

    // The 310.8 builds janblade's guide lists (SHA-256): NVIDIA's own (RTX 50) and ShortFuse's modified one (RTX 20 / 30 / 40).
    const string Nvidia3108 = "E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E";
    const string ShortFuse3108 = "E67DEE209320CDAFE0E93E45675D7AA34323A53ACC57A72B2E40A181581C989A";

    static readonly Dictionary<string, (long Length, DateTime Written, string Hash)> HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's SHA-256, kept while its size and date stay the same (the file is 150 MB).</summary>
    public static string HashOf(string file)
    {
        var info = new FileInfo(file);
        lock (HashCache)
            if (HashCache.TryGetValue(file, out var c) && c.Length == info.Length && c.Written == info.LastWriteTimeUtc) return c.Hash;
        string hash;
        using (var s = File.OpenRead(file)) hash = Convert.ToHexString(SHA256.HashData(s));
        lock (HashCache) HashCache[file] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    /// <summary>Which build of nvngx_dlssnr.dll a file is: "310.8 (NVIDIA)", "310.8 (ShortFuse, RTX 20-40)" or its version.</summary>
    public static string Describe(string file)
    {
        string version = DlssNr.Version(file);
        try
        {
            var hash = HashOf(file);
            if (hash == Nvidia3108) return L.F("{0}, NVIDIA's signed build", version);
            if (hash == ShortFuse3108) return L.F("{0}, ShortFuse's build for RTX 20 / 30 / 40", version);
        }
        catch { }
        return version;
    }

    public static bool IsShortFuse(string file) { try { return HashOf(file) == ShortFuse3108; } catch { return false; } }

    /// <summary>DLSSNR-AMD takes 310.9.16 (both networks) or 310.8.0 (CC_SILVER_AARDWOLD only).</summary>
    public static bool AmdCanRead(string file)
    {
        var v = DlssNr.Version(file);
        return v.StartsWith("310.9.16", StringComparison.Ordinal) || v.StartsWith("310.8.0", StringComparison.Ordinal);
    }

    /// <summary>
    /// What is wrong with the stored file for <paramref name="method"/> on <paramref name="tier"/>, or null when it fits:
    /// F5 through NGX wants 310.8 (NVIDIA's on RTX 50, ShortFuse's on RTX 20-40), F5's port wants 310.9 or newer, DLSSNR-AMD
    /// wants 310.9.16 or 310.8.0.
    /// </summary>
    public static string? ModelProblem(string? file, string method, Dlss5Gpu tier, GpuInfo? gpu)
    {
        if (file is null) return L.F("No {0} yet.", DlssNr.FileName);
        if (method == Amd)
            return AmdCanRead(file) ? null : L.F("DLSSNR-AMD reads only {0} 310.9.16 or 310.8.0; this one is {1}.", DlssNr.FileName, DlssNr.Version(file));
        if (tier != Dlss5Gpu.NvidiaRtx)
            return DlssNr.PortCanRun(file) ? null : L.F("The vendor-neutral port needs {0} {1} or newer (this one is {2}), or it runs the large network slowly.", DlssNr.FileName, DlssNr.PortMinimum, DlssNr.Version(file));
        bool rtx50 = gpu is not null && Regex.IsMatch(gpu.Name, @"RTX\s*50\d\d", RegexOptions.IgnoreCase);
        if (!rtx50 && !IsShortFuse(file) && DlssNr.Version(file).StartsWith("310.8", StringComparison.Ordinal))
            return L.T("RTX 20, 30 and 40 cards need ShortFuse's modified 310.8 build (pinned in the RenoDX Discord); NVIDIA's own runs on RTX 50 only.");
        return null;
    }
}
