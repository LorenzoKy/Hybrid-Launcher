using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Hardware;

/// <summary>
/// The newest version of each upscaling, frame generation, ray reconstruction and latency technology, kept right by itself.
/// Three layers: the highest version of the first two wins, and the third has the last word:
/// <list type="number">
/// <item>The built-in table below (what was newest when this version of the app was made).</item>
/// <item>The vendors' own SDK releases on GitHub, read at most once a day: AMD's FSR SDK release notes name every component
///   with its version ("AMD FSR Upscaling 4.1.2", "FidelityFX Super Resolution 3.1.6"...), Intel's XeSS SDK tags its
///   version (and its notes the Xe Low Latency one), NVIDIA's DLSS SDK tags the DLL version (310.x).</item>
/// <item>A correction file in the app's own repository (<see cref="CorrectionsUrl"/>), for what the vendors do not publish in
///   a readable way (NVIDIA's marketing numbers such as DLSS 4.5 or 5.0) or a mistake to fix without a new release.</item>
/// </list>
/// The merged result is cached, so My PC shows it at once and without network; a failed read keeps the last good values.
/// </summary>
public static class TechVersions
{
    /// <summary>Component ids and their built-in versions (October 2026).</summary>
    static readonly Dictionary<string, string> BuiltIn = new()
    {
        ["fsr-upscaling-ml"] = "4.1.2",      // FSR SDK 2.4
        ["fsr-framegen-ml"] = "4.0.2",
        ["fsr-ray-regeneration"] = "1.2.1",
        ["fsr-upscaling"] = "3.1.6",         // FidelityFX Super Resolution 3.1
        ["fsr-framegen"] = "3.1.6",
        ["xess"] = "3.0.2",                  // XeSS SDK: Super Resolution and Frame Generation
        ["xell"] = "1.3.2",                  // Xe Low Latency
        ["dlss-sdk"] = "310.9.16",           // DLSS SDK / DLL version
        ["dlss"] = "4.5",                    // NVIDIA's own numbering of Super Resolution, Multi Frame Generation, Ray Reconstruction
        ["dlss-neural"] = "5.0",
        ["dlss-framegen-rtx40"] = "4.0",
        ["reflex"] = "2.0",
        ["afmf"] = "2.1",
        ["anti-lag"] = "2.0",
    };

    public const string CorrectionsUrl = "https://raw.githubusercontent.com/" + AppInfo.Repository + "/main/data/tech-versions.json";

    static Dictionary<string, string> _current = Load();
    static string CacheFile => Path.Combine(AppInfo.CacheDir, "tech-versions.json");

    /// <summary>The newest known version of a component (built-in, then whatever the last refresh found).</summary>
    public static string Get(string id) => _current.TryGetValue(id, out var v) ? v : BuiltIn.GetValueOrDefault(id, "");

    /// <summary>When the versions were last read from the vendors (null: never, built-in values only).</summary>
    public static DateTime? CheckedUtc => File.Exists(CacheFile) ? File.GetLastWriteTimeUtc(CacheFile) : null;

    static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(BuiltIn);
        try
        {
            var file = Path.Combine(AppInfo.CacheDir, "tech-versions.json");
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject o)
                foreach (var (k, v) in o)
                    if (v?.GetValue<string>() is { } s) Raise(map, k, s);
        }
        catch (Exception ex) { Log.Warn($"Tech versions cache: {ex.Message}"); }
        return map;
    }

    /// <summary>Keeps the higher of the two versions (a refresh never lowers a value).</summary>
    static bool Raise(Dictionary<string, string> map, string id, string version)
    {
        if (!TryParse(version, out var v)) return false;
        if (map.TryGetValue(id, out var old) && TryParse(old, out var o) && o >= v) return false;
        map[id] = version;
        return true;
    }

    static bool TryParse(string s, out Version v)
    {
        var m = Regex.Match(s, @"^\d+(\.\d+){1,3}");
        v = new Version(0, 0);
        return m.Success && Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out v!);
    }

    static int _refreshing;

    /// <summary>
    /// Reads the vendors' releases (at most once a day unless <paramref name="force"/>) and the correction file; returns true
    /// when a version changed, so the page can redraw. Never throws: an offline PC keeps the values it has.
    /// </summary>
    public static async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && CheckedUtc is { } at && DateTime.UtcNow - at < TimeSpan.FromHours(24)) return false;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return false;
        try
        {
            var map = new Dictionary<string, string>(_current);
            bool changed = false;
            foreach (var (id, version) in await FromVendorsAsync(ct)) changed |= Raise(map, id, version);
            // The correction file has the last word, so it can also fix a version that is too high.
            foreach (var (id, version) in await CorrectionsAsync(ct))
                if (TryParse(version, out _) && map.GetValueOrDefault(id) != version) { map[id] = version; changed = true; }
            _current = map;
            var json = new JsonObject();
            foreach (var (k, v) in map) json[k] = v;
            File.WriteAllText(CacheFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (changed) Log.Info("Tech versions updated: " + string.Join(", ", map.Select(p => $"{p.Key} {p.Value}")));
            return changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Tech versions: {ex.Message}");
            return false;
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    /// <summary>The versions the vendors' latest SDK releases name. Public for the smoke test.</summary>
    public static async Task<List<(string Id, string Version)>> FromVendorsAsync(CancellationToken ct)
    {
        var found = new List<(string, string)>();
        foreach (var r in await ReleasesAsync("GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", ct)) found.AddRange(ParseAmd(r.Body));
        foreach (var r in await ReleasesAsync("intel/xess", ct))
        {
            if (Regex.Match(r.Tag, @"\d+\.\d+\.\d+") is { Success: true } x) found.Add(("xess", x.Value));
            if (Regex.Match(r.Body, @"XeLL\s+(?:to\s+|version\s+)?v?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase) is { Success: true } l) found.Add(("xell", l.Groups[1].Value));
        }
        foreach (var r in await ReleasesAsync("NVIDIA/DLSS", ct))
            if (Regex.Match(r.Tag, @"\d+\.\d+\.\d+") is { Success: true } d) found.Add(("dlss-sdk", d.Value));
        return found;
    }

    /// <summary>
    /// AMD's release notes: one heading per component with its version. Trademark signs are dropped first; the Swapchain
    /// variants are separate components and left out.
    /// </summary>
    public static IEnumerable<(string Id, string Version)> ParseAmd(string body)
    {
        var text = Regex.Replace(body, "[™®©]|<[^>]+>", " ");
        text = Regex.Replace(text, @"[ \t]+", " ");
        foreach (Match m in Regex.Matches(text, @"(FSR|FidelityFX) (Super Resolution Frame Generation|Super Resolution|Upscaling|Frame Generation|Ray Regeneration)( \(Swapchain\))? (\d+\.\d+\.\d+)", RegexOptions.IgnoreCase))
        {
            if (m.Groups[3].Success) continue;
            var kind = m.Groups[2].Value.ToLowerInvariant();
            var version = m.Groups[4].Value;
            int major = int.Parse(version.Split('.')[0]);
            string? id = kind switch
            {
                "ray regeneration" => "fsr-ray-regeneration",
                "super resolution frame generation" => "fsr-framegen",
                "frame generation" => major >= 4 ? "fsr-framegen-ml" : "fsr-framegen",
                "upscaling" or "super resolution" => major >= 4 ? "fsr-upscaling-ml" : "fsr-upscaling",
                _ => null,
            };
            if (id is not null) yield return (id, version);
        }
    }

    sealed record Release(string Tag, string Body);

    static async Task<List<Release>> ReleasesAsync(string repo, CancellationToken ct)
    {
        var json = await Http.GetCachedAsync($"https://api.github.com/repos/{repo}/releases?per_page=10",
            "tech-" + repo.Replace('/', '-') + ".json", TimeSpan.FromHours(20), accept: "application/vnd.github+json", ct: ct);
        var list = new List<Release>();
        if (json is null || JsonNode.Parse(json) is not JsonArray a) return list;
        foreach (var r in a)
        {
            if (r?["draft"]?.GetValue<bool>() == true || r?["prerelease"]?.GetValue<bool>() == true) continue;
            list.Add(new(r?["tag_name"]?.GetValue<string>() ?? "", r?["body"]?.GetValue<string>() ?? ""));
        }
        return list;
    }

    /// <summary>The correction file: {"dlss": "4.5", ...}. Missing (or the repository private) is normal.</summary>
    static async Task<List<(string, string)>> CorrectionsAsync(CancellationToken ct)
    {
        var list = new List<(string, string)>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.Client.GetAsync(CorrectionsUrl, cts.Token);
            if (!resp.IsSuccessStatusCode) return list;
            if (JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token)) is JsonObject o)
                foreach (var (k, v) in o)
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) list.Add((k, s));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { Log.Warn($"Tech version corrections: {ex.Message}"); }
        return list;
    }
}
