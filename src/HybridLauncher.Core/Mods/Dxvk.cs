using System.Globalization;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Net;
using HybridLauncher.Core.Opti;

namespace HybridLauncher.Core.Mods;

/// <summary>
/// The Vulkan translation layers: DXVK (Direct3D 8, 9, 10 and 11), its gplasync build (shaders compiled in the background,
/// less stutter) and VKD3D-Proton (Direct3D 12, with DXVK's dxgi.dll beside it).
/// </summary>
public enum DxvkFlavor { Dxvk, GplAsync, Vkd3d }

/// <summary>The Direct3D version a game renders with, which decides the dlls that go in.</summary>
public enum D3dVersion { D3D8, D3D9, D3D10, D3D11, D3D12 }

public sealed record DxvkRelease(DxvkFlavor Flavor, string Version, string Url, long Size, DateTime Published)
{
    public string Display => $"{Version}  ·  {Published:d MMM yyyy}";
    public string ArchiveName => Url.Split('?')[0].Split('/')[^1];
}

/// <summary>A dll of a translation layer found in a game folder, whoever put it there.</summary>
public sealed record DetectedLayer(string File, string Layer);

/// <summary>
/// DXVK, DXVK-gplasync and VKD3D-Proton for Windows games. The releases come from their own pages (GitHub for DXVK and
/// VKD3D-Proton, GitLab for gplasync); an archive is unpacked once under Hybrid Launcher's data, then the dlls the game's Direct3D
/// version needs, for its bitness, are copied next to the game exe. Every file they replace is backed up first and put back on
/// uninstall. A dll that belongs to OptiScaler or ReShade is never replaced: <see cref="Blockers"/> says which and why.
/// </summary>
public static class Dxvk
{
    public const string DxvkPage = "https://github.com/doitsujin/dxvk";
    public const string GplAsyncPage = "https://gitlab.com/Ph42oN/dxvk-gplasync";
    public const string Vkd3dPage = "https://github.com/HansKristian-Work/vkd3d-proton";
    public const string ConfName = "dxvk.conf";

    public static string Dir => Directory.CreateDirectory(Path.Combine(AppInfo.DataRoot, "Dxvk")).FullName;

    public static string Name(DxvkFlavor f) => f switch
    {
        DxvkFlavor.GplAsync => "DXVK-gplasync",
        DxvkFlavor.Vkd3d => "VKD3D-Proton",
        _ => "DXVK",
    };

    public static string Page(DxvkFlavor f) => f switch { DxvkFlavor.GplAsync => GplAsyncPage, DxvkFlavor.Vkd3d => Vkd3dPage, _ => DxvkPage };

    public static string Name(D3dVersion v) => v switch
    {
        D3dVersion.D3D8 => "Direct3D 8",
        D3dVersion.D3D9 => "Direct3D 9",
        D3dVersion.D3D10 => "Direct3D 10",
        D3dVersion.D3D11 => "Direct3D 11",
        _ => "Direct3D 12",
    };

    /// <summary>The layers that can run a Direct3D version: DXVK and gplasync up to 11, VKD3D-Proton for 12.</summary>
    public static bool Handles(DxvkFlavor f, D3dVersion v) => f == DxvkFlavor.Vkd3d ? v == D3dVersion.D3D12 : v != D3dVersion.D3D12;

    /// <summary>The dlls a game of that Direct3D version needs from the layer (VKD3D-Proton also needs DXVK's dxgi.dll, see <see cref="NeedsDxgi"/>).</summary>
    public static string[] Files(DxvkFlavor f, D3dVersion v) => (f, v) switch
    {
        (DxvkFlavor.Vkd3d, _) => ["d3d12.dll", "d3d12core.dll"],
        (_, D3dVersion.D3D8) => ["d3d8.dll", "d3d9.dll"],
        (_, D3dVersion.D3D9) => ["d3d9.dll"],
        (_, D3dVersion.D3D10) => ["d3d10core.dll", "d3d11.dll", "dxgi.dll"],
        _ => ["d3d11.dll", "dxgi.dll"],
    };

    /// <summary>VKD3D-Proton has no DXGI of its own: it shares DXVK's (its README says so).</summary>
    public static bool NeedsDxgi(DxvkFlavor f) => f == DxvkFlavor.Vkd3d;

    /// <summary>The Direct3D versions a game's API list ("DX11", "DX12"…) names, newest first; empty when none is Direct3D.</summary>
    public static List<D3dVersion> FromApis(IEnumerable<string> apis)
    {
        var list = new List<D3dVersion>();
        foreach (var a in apis)
        {
            switch (a.ToUpperInvariant())
            {
                case "DX12": list.Add(D3dVersion.D3D12); break;
                case "DX11": list.Add(D3dVersion.D3D11); break;
                case "DX10": list.Add(D3dVersion.D3D10); break;
                case "DX9": list.Add(D3dVersion.D3D9); break;
                case "DX8": list.Add(D3dVersion.D3D8); break;
            }
        }
        return list.Distinct().OrderByDescending(v => v).ToList();
    }

    // ---------- releases ----------

    /// <summary>The newest releases of a layer (cached for 6 hours), newest first.</summary>
    public static async Task<List<DxvkRelease>> GetReleasesAsync(DxvkFlavor flavor, bool force = false, CancellationToken ct = default)
    {
        try
        {
            if (flavor == DxvkFlavor.GplAsync)
            {
                var json = await Http.GetCachedAsync("https://gitlab.com/api/v4/projects/Ph42oN%2Fdxvk-gplasync/releases?per_page=20", "dxvk-gplasync-releases.json", TimeSpan.FromHours(6), force, ct: ct);
                return json is null ? [] : ParseGitLab(json);
            }
            var repo = flavor == DxvkFlavor.Vkd3d ? "HansKristian-Work/vkd3d-proton" : "doitsujin/dxvk";
            var gh = await Http.GetCachedAsync($"https://api.github.com/repos/{repo}/releases?per_page=20", $"{(flavor == DxvkFlavor.Vkd3d ? "vkd3d" : "dxvk")}-releases.json",
                TimeSpan.FromHours(6), force, accept: "application/vnd.github+json", ct: ct);
            return gh is null ? [] : ParseGitHub(gh, flavor);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"{Name(flavor)} releases: {ex.Message}");
            return [];
        }
    }

    /// <summary>GitHub releases: the Windows archive is "dxvk-&lt;v&gt;.tar.gz" or "vkd3d-proton-&lt;v&gt;.tar.zst" (not the Linux "native" one).</summary>
    public static List<DxvkRelease> ParseGitHub(string json, DxvkFlavor flavor)
    {
        var list = new List<DxvkRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        var prefix = flavor == DxvkFlavor.Vkd3d ? "vkd3d-proton-" : "dxvk-";
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            DateTime.TryParse(r.TryGetProperty("published_at", out var pub) ? pub.GetString() : null, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when);
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Contains("native", StringComparison.OrdinalIgnoreCase)) continue;
                if (!(name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new DxvkRelease(flavor, tag.TrimStart('v'), a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), when.ToLocalTime()));
                break;
            }
        }
        return list.OrderByDescending(r => r.Published).ToList();
    }

    /// <summary>GitLab releases of gplasync: the archive is a release link named "dxvk-gplasync-v&lt;v&gt;.tar.gz" (the file in the repository).</summary>
    public static List<DxvkRelease> ParseGitLab(string json)
    {
        var list = new List<DxvkRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("upcoming_release", out var up) && up.ValueKind == JsonValueKind.True) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            DateTime.TryParse(r.TryGetProperty("released_at", out var rel) ? rel.GetString() : null, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when);
            if (!r.TryGetProperty("assets", out var assets) || !assets.TryGetProperty("links", out var links)) continue;
            foreach (var l in links.EnumerateArray())
            {
                var name = l.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) continue;
                var url = l.TryGetProperty("direct_asset_url", out var du) && du.GetString() is { Length: > 0 } direct ? direct : l.GetProperty("url").GetString()!;
                list.Add(new DxvkRelease(DxvkFlavor.GplAsync, tag.TrimStart('v'), url, 0, when.ToLocalTime()));
                break;
            }
        }
        return list.OrderByDescending(r => r.Published).ToList();
    }

    // ---------- the unpacked release ----------

    /// <summary>
    /// The release unpacked under Hybrid Launcher's data (downloaded and unpacked the first time): the folder holding its
    /// x64 and x32 (or x86) folders.
    /// </summary>
    public static async Task<string> PrepareAsync(DxvkRelease release, IProgress<double>? progress, CancellationToken ct)
    {
        var root = Path.Combine(Dir, release.Flavor.ToString().ToLowerInvariant(), Safe(release.Version));
        var done = Path.Combine(root, ".unpacked");
        if (File.Exists(done) && LayerRoot(root) is { } ready) return ready;
        var archive = Path.Combine(Dir, "downloads", release.ArchiveName);
        if (!File.Exists(archive) || release.Size > 0 && new FileInfo(archive).Length != release.Size)
            await Http.DownloadFileAsync(release.Url, archive, release.Size, progress, ct);
        var temp = root + ".tmp";
        try
        {
            await ArchiveExtractor.ExtractAsync(archive, temp, ct);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.Move(temp, root);
        }
        catch
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            // A broken download is fetched again next time.
            try { File.Delete(archive); } catch { }
            throw;
        }
        var layer = LayerRoot(root) ?? throw new InvalidDataException(L.F("{0} {1}: the archive has no x64 folder.", Name(release.Flavor), release.Version));
        File.WriteAllText(done, release.Url);
        try { File.Delete(archive); } catch { }
        return layer;
    }

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>The folder (the unpacked one, or the single folder inside it) that holds x64.</summary>
    static string? LayerRoot(string root)
    {
        if (!Directory.Exists(root)) return null;
        if (Directory.Exists(Path.Combine(root, "x64"))) return root;
        foreach (var sub in Directory.GetDirectories(root))
            if (Directory.Exists(Path.Combine(sub, "x64"))) return sub;
        return null;
    }

    static string Arch(string layerRoot, bool is64) =>
        is64 ? Path.Combine(layerRoot, "x64") : Directory.Exists(Path.Combine(layerRoot, "x32")) ? Path.Combine(layerRoot, "x32") : Path.Combine(layerRoot, "x86");

    // ---------- what is in the folder ----------

    static readonly string[] LayerDlls = ["d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll", "d3d12.dll", "d3d12core.dll"];
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, long Length, string? Layer)> Seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The translation layer dlls in a folder (DXVK's or VKD3D-Proton's), whoever put them there.</summary>
    public static List<DetectedLayer> Detect(string dir)
    {
        var list = new List<DetectedLayer>();
        foreach (var name in LayerDlls)
            if (LayerOf(Path.Combine(dir, name)) is { } layer) list.Add(new DetectedLayer(name, layer));
        return list;
    }

    /// <summary>The game page section of a layer: DXVK (and gplasync) and VKD3D-Proton each have their own in the Mods tab.</summary>
    public static string Section(bool vkd3d) => vkd3d ? "mods/vkd3d" : "mods/dxvk";

    /// <summary>"DXVK" or "VKD3D-Proton" when the dll is one of theirs (they carry their own setting names), else null.</summary>
    public static string? LayerOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 96 << 20) return null;
            if (Seen.TryGetValue(path, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length) return known.Layer;
            var bytes = File.ReadAllBytes(path);
            string? layer = Contains(bytes, "VKD3D_CONFIG") || Contains(bytes, "VKD3D_DEBUG") ? "VKD3D-Proton" : Contains(bytes, "DXVK_CONFIG_FILE") || Contains(bytes, "DXVK_HUD") ? "DXVK" : null;
            Seen[path] = (info.LastWriteTimeUtc, info.Length, layer);
            return layer;
        }
        catch { return null; }
    }

    static bool Contains(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    /// <summary>The dlls Hybrid Launcher installed that are gone or not the same file any more (a game update or a file check).</summary>
    public static List<string> Changed(DxvkState state, string dir)
    {
        var list = new List<string>();
        foreach (var (rel, hash) in state.Files)
        {
            var path = Path.Combine(dir, rel);
            try
            {
                if (!File.Exists(path) || !string.Equals(OptiInstaller.Hash(path), hash, StringComparison.OrdinalIgnoreCase)) list.Add(rel);
            }
            catch { list.Add(rel); }
        }
        return list;
    }

    /// <summary>A file that must not be replaced and why: who it belongs to (OptiScaler, ReShade).</summary>
    public sealed record Blocker(string File, string Owner);

    /// <summary>
    /// The files the install would replace that belong to another mod Hybrid Launcher knows (OptiScaler, ReShade, Special K).
    /// Replacing them would break that mod, so the install stops and says which.
    /// </summary>
    public static List<Blocker> Blockers(string dir, IEnumerable<string> files, DxvkState? previous)
    {
        var list = new List<Blocker>();
        foreach (var f in files)
        {
            if (previous?.Files.ContainsKey(f) == true) continue;
            var path = Path.Combine(dir, f);
            if (!File.Exists(path)) continue;
            if (OptiDetector.IsOptiScaler(path)) list.Add(new Blocker(f, "OptiScaler"));
            else if (ReShade.IsReShade(path)) list.Add(new Blocker(f, "ReShade"));
            else if (ModCheck.IsSpecialK(path)) list.Add(new Blocker(f, "Special K"));
        }
        return list;
    }

    // ---------- install ----------

    /// <summary>
    /// Copies the layer's dlls for <paramref name="version"/> next to the game exe (an earlier install taken out first), backing up
    /// every file it replaces. VKD3D-Proton gets DXVK's dxgi.dll from <paramref name="dxgiFrom"/>. gplasync turns async on in
    /// dxvk.conf (made when there is none).
    /// </summary>
    public static async Task<DxvkState> InstallAsync(DxvkRelease release, D3dVersion version, string dir, bool is64, DxvkState? previous,
        DxvkRelease? dxgiFrom, IProgress<double>? progress, CancellationToken ct, bool backup = true)
    {
        if (!Handles(release.Flavor, version)) throw new InvalidOperationException(L.F("{0} does not run {1} games.", Name(release.Flavor), Name(version)));
        var layer = await PrepareAsync(release, progress, ct);
        string? dxgiLayer = null;
        if (NeedsDxgi(release.Flavor))
        {
            if (dxgiFrom is null) throw new InvalidOperationException(L.T("VKD3D-Proton needs DXVK's dxgi.dll, and no DXVK release could be found (offline?)."));
            dxgiLayer = await PrepareAsync(dxgiFrom, null, ct);
        }

        var copies = Files(release.Flavor, version).Select(f => (Rel: f, Source: Path.Combine(Arch(layer, is64), f))).ToList();
        if (dxgiLayer is not null) copies.Add(("dxgi.dll", Path.Combine(Arch(dxgiLayer, is64), "dxgi.dll")));
        foreach (var (rel, source) in copies)
            if (!File.Exists(source)) throw new InvalidDataException(L.F("{0} {1} has no {2} for {3}-bit games.", Name(release.Flavor), release.Version, rel, is64 ? 64 : 32));
        var blockers = Blockers(dir, copies.Select(c => c.Rel), previous);
        if (blockers.Count > 0)
            throw new InvalidOperationException(string.Join("\n", blockers.Select(b => L.F("{0} is {1}'s file here.", b.File, b.Owner))));

        bool confExisted = File.Exists(Path.Combine(dir, ConfName));
        if (previous is not null) Uninstall(previous, dir, keepConf: true);
        var state = new DxvkState
        {
            Flavor = release.Flavor,
            Version = release.Version,
            Api = version,
            Is64 = is64,
            DxgiVersion = dxgiFrom is not null && dxgiLayer is not null ? dxgiFrom.Version : null,
            InstalledAt = DateTime.Now,
            ConfCreated = previous?.ConfCreated == true || !confExisted,
        };
        var backupRoot = Path.Combine(AppInfo.DataRoot, "backups", "dxvk", Path.GetFileName(dir) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        await Task.Run(() =>
        {
            foreach (var (rel, source) in copies)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(dir, rel);
                if (backup && File.Exists(dest))
                {
                    var copy = Path.Combine(backupRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(dest, copy, overwrite: true);
                    state.Backups[rel] = copy;
                }
                File.Copy(source, dest, overwrite: true);
                state.Files[rel] = OptiInstaller.Hash(dest);
            }
            // gplasync does nothing different until async is on; the other layers leave dxvk.conf alone.
            if (release.Flavor == DxvkFlavor.GplAsync) SetConf(dir, "dxvk.enableAsync", "True");
            else if (previous?.Flavor == DxvkFlavor.GplAsync) SetConf(dir, "dxvk.enableAsync", null);
            if (state.ConfCreated && File.Exists(Path.Combine(dir, ConfName)) && new FileInfo(Path.Combine(dir, ConfName)).Length == 0) File.Delete(Path.Combine(dir, ConfName));
        }, ct);
        Log.Info($"{Name(release.Flavor)} {release.Version} ({Name(version)}, {(is64 ? 64 : 32)}-bit) installed in {dir}: {string.Join(", ", state.Files.Keys)}");
        return state;
    }

    /// <summary>
    /// Removes the dlls the install added and puts back every file it replaced. A dxvk.conf Hybrid Launcher made goes too (unless
    /// <paramref name="keepConf"/>, for a reinstall); one that was there before keeps everything but the async line it added.
    /// </summary>
    public static List<string> Uninstall(DxvkState state, string dir, bool keepConf = false)
    {
        var problems = new List<string>();
        foreach (var rel in state.Files.Keys)
        {
            var path = Path.Combine(dir, rel);
            try
            {
                // A file that is not ours any more (a game update put the original back) is left alone.
                if (File.Exists(path) && string.Equals(OptiInstaller.Hash(path), state.Files[rel], StringComparison.OrdinalIgnoreCase)) File.Delete(path);
            }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        foreach (var (rel, backup) in state.Backups)
        {
            var path = Path.Combine(dir, rel);
            try { if (File.Exists(backup) && !File.Exists(path)) File.Copy(backup, path); }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        if (!keepConf)
        {
            var conf = Path.Combine(dir, ConfName);
            try
            {
                if (state.ConfCreated) { if (File.Exists(conf)) File.Delete(conf); }
                else if (state.Flavor == DxvkFlavor.GplAsync) SetConf(dir, "dxvk.enableAsync", null);
            }
            catch (Exception ex) { problems.Add($"{ConfName}: {ex.Message}"); }
        }
        Log.Info($"{Name(state.Flavor)} removed from {dir}{(problems.Count > 0 ? ", problems: " + string.Join("; ", problems) : "")}");
        return problems;
    }

    // ---------- dxvk.conf ----------

    /// <summary>A value of dxvk.conf in the folder ("key = value" lines; # starts a comment), or null when it is not set.</summary>
    public static string? GetConf(string dir, string key)
    {
        var file = Path.Combine(dir, ConfName);
        if (!File.Exists(file)) return null;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Split('#')[0];
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(eq + 1)..].Trim().Trim('"');
        }
        return null;
    }

    /// <summary>
    /// Sets a value in dxvk.conf (made when missing), or removes it with null; every other line stays as it was, line endings
    /// included, so taking a value out again leaves the file byte for byte as it was.
    /// </summary>
    public static void SetConf(string dir, string key, string? value)
    {
        var file = Path.Combine(dir, ConfName);
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var newline = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
        bool endsWithNewline = text.Length == 0 || text.EndsWith('\n');
        List<string> lines = text.Length == 0 ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        bool done = false;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i].Split('#')[0];
            int eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (value is null || done) lines.RemoveAt(i);
            else { lines[i] = $"{key} = {value}"; done = true; }
        }
        if (value is not null && !done) lines.Add($"{key} = {value}");
        if (lines.Count == 0 && text.Length == 0) return;
        File.WriteAllText(file, string.Join(newline, lines) + (endsWithNewline && lines.Count > 0 ? newline : ""));
    }
}

/// <summary>A translation layer installed by Hybrid Launcher: which, for which Direct3D version, and each dll with its SHA-256.</summary>
public sealed class DxvkState
{
    public DxvkFlavor Flavor { get; set; }
    public string Version { get; set; } = "";
    public D3dVersion Api { get; set; }
    public bool Is64 { get; set; } = true;
    /// <summary>VKD3D-Proton: the DXVK release its dxgi.dll came from.</summary>
    public string? DxgiVersion { get; set; }
    public DateTime InstalledAt { get; set; }
    /// <summary>Files added, relative to the exe folder, with their SHA-256.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files that were there before and were replaced: relative path -> backup path.</summary>
    public Dictionary<string, string> Backups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>There was no dxvk.conf before: the one there now is Hybrid Launcher's and goes on uninstall.</summary>
    public bool ConfCreated { get; set; }
}
