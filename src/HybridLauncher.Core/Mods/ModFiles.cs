using System.Diagnostics;
using HybridLauncher.Core.Opti;

namespace HybridLauncher.Core.Mods;

/// <summary>How a file in the game folder gets loaded, which decides where it shows.</summary>
public enum ModFileRole
{
    /// <summary>A dll Windows loads in place of its own (dxgi.dll, d3d11.dll, winmm.dll…): the way mods get into a game.</summary>
    Proxy,
    /// <summary>Loaded by another mod: ReShade64.dll by OptiScaler, .addon files by ReShade, .asi plugins by OptiScaler.</summary>
    Loaded,
    /// <summary>An upscaler or frame generation library (the game's own, or one a mod brought).</summary>
    Upscaler,
    /// <summary>A mod's settings file.</summary>
    Config,
}

/// <summary>
/// One file of the game folder and what it is: <paramref name="File"/> relative to the exe folder, <paramref name="What"/> the mod
/// ("OptiScaler", "ReShade with full add-on support"…), its version, <paramref name="How"/> it is loaded in words, whether
/// Hybrid Launcher put it there, and the game page section that manages it.
/// </summary>
public sealed record ModFile(string File, string What, string Version, string How, ModFileRole Role, bool ByHybrid, string? Tab, bool Problem = false);

/// <summary>
/// What is installed as what in a game folder: every proxy dll and who it belongs to, what loads the files that are not proxies
/// (ReShade64.dll through OptiScaler, add-ons through ReShade, plugins through OptiScaler), the upscaler libraries with their
/// versions and the mods' settings files. Reads the folder only; call it off the UI thread.
/// </summary>
public static class ModFiles
{
    // Every name Windows looks up beside the exe that mods use to get in.
    static readonly string[] ProxyNames =
    [
        "dxgi.dll", "d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "d3d12.dll", "d3d12core.dll", "opengl32.dll",
        "winmm.dll", "version.dll", "dbghelp.dll", "dinput8.dll", "wininet.dll", "winhttp.dll", "xinput1_3.dll", "xinput1_4.dll",
    ];

    static readonly (string File, string What, string Tab)[] Configs =
    [
        ("OptiScaler.ini", "OptiScaler", "opti/settings"), ("ReShade.ini", "ReShade", "mods/reshade"), ("dxvk.conf", "DXVK", "mods/dxvk"),
        ("UltraPlusConfig.ini", "Ultra+", "ultra/settings"),
    ];

    public static List<ModFile> Map(GameSettings s, string dir)
    {
        var list = new List<ModFile>();
        if (!Directory.Exists(dir)) return list;
        var opti = s.Install is { } m && SameDir(m.TargetDir, dir) ? m : null;
        bool optiLoadsReShade = ReShade.OptiLoadsReShade(dir) == true;
        bool optiHere = OptiDetector.Detect(dir) is not null;

        foreach (var name in ProxyNames)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try { list.Add(Proxy(s, opti, path, name)); }
            catch (Exception ex) { Log.Warn($"Mod files ({name}): {ex.Message}"); }
        }

        // ReShade loaded by OptiScaler rather than by Windows.
        foreach (var name in new[] { ReShade.OptiDllName, "ReShade32.dll" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) || !ReShade.IsReShade(path)) continue;
            bool loaded = optiHere && optiLoadsReShade && name == ReShade.OptiDllName;
            list.Add(new ModFile(name, ReShadeName(path), Version(path),
                loaded ? L.T("OptiScaler loads it (LoadReshade on in OptiScaler.ini)")
                       : optiHere ? L.T("Not loaded: LoadReshade is off in OptiScaler.ini") : L.T("Not loaded: OptiScaler, which would load it, is not here"),
                ModFileRole.Loaded, s.Luma?.Files.Contains(name, StringComparer.OrdinalIgnoreCase) == true || s.ReShade.RenamedFrom is not null
                    || name.Equals(s.ReShade.Placed, StringComparison.OrdinalIgnoreCase), "mods/reshade", Problem: !loaded));
        }

        // ReShade add-ons (RenoDX, Luma…): ReShade loads every .addon beside its dll.
        var reshade = ReShade.Loaded(dir);
        foreach (var path in Files(dir, "*.addon64").Concat(Files(dir, "*.addon32")).Concat(Files(dir, "*.addon")))
        {
            var name = Path.GetFileName(path);
            bool renodx = name.StartsWith("renodx", StringComparison.OrdinalIgnoreCase);
            bool luma = name.StartsWith("Luma", StringComparison.OrdinalIgnoreCase);
            bool works = reshade is not null && !ModCheck.LimitedAddons(reshade.Path);
            list.Add(new ModFile(name, renodx ? "RenoDX" : luma ? "Luma" : L.T("ReShade add-on"), Version(path),
                reshade is null ? L.T("Not loaded: there is no ReShade here")
                : works ? L.F("ReShade loads it ({0})", reshade.FileName)
                : L.F("Not loaded: {0} is the normal ReShade build, which skips add-ons", reshade.FileName),
                ModFileRole.Loaded, s.RenoDxFiles.ContainsKey(name) || s.Luma?.Files.Contains(name, StringComparer.OrdinalIgnoreCase) == true,
                renodx ? "mods/renodx" : luma ? "mods/luma" : "mods/reshade", Problem: !works));
        }

        // OptiScaler's ASI plugins.
        if (optiHere)
        {
            var plugins = Path.Combine(dir, opti?.PluginsDir ?? "plugins");
            foreach (var path in Files(plugins, "*.asi"))
            {
                var rel = Path.GetRelativePath(dir, path);
                list.Add(new ModFile(rel, Path.GetFileNameWithoutExtension(path), Version(path), L.T("OptiScaler loads it (ASI plugin)"),
                    ModFileRole.Loaded, opti?.AsiPlugins.ContainsKey(Path.GetFileName(path)) == true || opti?.Files.ContainsKey(rel) == true
                    || (opti?.DlssNrAmd is not null && Path.GetFileName(path).Equals(DlssNrAmd.PluginName, StringComparison.OrdinalIgnoreCase)), "opti/plugins"));
            }
        }

        // Upscaler and frame generation libraries next to the exe, with the version they really are.
        foreach (var path in Files(dir, "*.dll"))
        {
            var name = Path.GetFileName(path);
            if (Media.UpscalerFiles.Read(path, name) is not { } up) continue;
            bool optiOwn = opti?.Files.ContainsKey(name) == true;
            bool backedUp = opti?.Backups.ContainsKey(name) == true;
            list.Add(new ModFile(name, up.Tag, up.Version,
                optiOwn && backedUp ? L.T("OptiScaler's copy, in place of the game's own (kept as a backup)")
                : optiOwn ? L.T("Brought by OptiScaler")
                : L.T("The game's own"),
                ModFileRole.Upscaler, optiOwn, optiOwn ? "opti/install" : null));
        }

        foreach (var (file, what, tab) in Configs)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            bool mine = file switch
            {
                "OptiScaler.ini" => opti is not null,
                "dxvk.conf" => s.Dxvk is not null,
                "UltraPlusConfig.ini" => s.UltraPlus is not null,
                _ => false,
            };
            list.Add(new ModFile(file, L.F("{0} settings", what), "", L.F("Read by {0} when the game starts", what), ModFileRole.Config, mine, tab));
        }
        return list;
    }

    static ModFile Proxy(GameSettings s, InstallManifest? opti, string path, string name)
    {
        var how = L.F("Windows loads it in place of its own {0}", name);
        if (OptiDetector.IsOptiScaler(path))
        {
            bool mine = opti is not null && opti.DllName.Equals(name, StringComparison.OrdinalIgnoreCase);
            return new ModFile(name, "OptiScaler", CustomBuilds.Brief(OptiDetector.Version(path)), how, ModFileRole.Proxy, mine, "opti/install");
        }
        if (ReShade.IsReShade(path))
            return new ModFile(name, ReShadeName(path), Version(path), how, ModFileRole.Proxy,
                s.Luma?.Files.Contains(name, StringComparer.OrdinalIgnoreCase) == true || name.Equals(s.ReShade.Placed, StringComparison.OrdinalIgnoreCase), "mods/reshade");
        if (s.UltraPlus is { } ultra && UltraPlus.Owns(ultra, path))
            return new ModFile(name, "Ultra+", ultra.Version, how, ModFileRole.Proxy, true, "ultra");
        if (Dxvk.LayerOf(path) is { } layer)
        {
            bool mine = s.Dxvk?.Files.ContainsKey(name) == true;
            var version = mine ? (layer == "DXVK" && s.Dxvk!.Flavor == DxvkFlavor.Vkd3d ? s.Dxvk.DxgiVersion ?? "" : s.Dxvk!.Version) : "";
            var what = mine && s.Dxvk!.Flavor == DxvkFlavor.GplAsync && layer == "DXVK" ? "DXVK-gplasync" : layer;
            bool brokenDxgi = name is "d3d11.dll" or "d3d10core.dll" or "d3d12.dll" or "d3d12core.dll"
                              && Dxvk.LayerOf(Path.Combine(Path.GetDirectoryName(path)!, "dxgi.dll")) is null;
            return new ModFile(name, what, version, how + (brokenDxgi ? " · " + L.T("without DXVK's dxgi.dll it cannot start") : ""), ModFileRole.Proxy, mine, Dxvk.Section(layer == "VKD3D-Proton" || (mine && name == "dxgi.dll" && s.Dxvk!.Flavor == DxvkFlavor.Vkd3d)), brokenDxgi);
        }
        if (ModCheck.IsSpecialK(path))
            return new ModFile(name, "Special K", Version(path), how, ModFileRole.Proxy, false, null);
        var info = Describe(path);
        return new ModFile(name, info.Length > 0 ? info : L.T("Unknown"), Version(path), how, ModFileRole.Proxy, false, null);
    }

    static string ReShadeName(string path) => ModCheck.LimitedAddons(path) ? "ReShade" : L.T("ReShade with full add-on support");

    static IEnumerable<string> Files(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch { return []; }
    }

    static string Version(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            if (v.FileMajorPart + v.FileMinorPart + v.FileBuildPart > 0) return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
            return (v.ProductVersion ?? "").Trim();
        }
        catch { return ""; }
    }

    static string Describe(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return string.Join(" · ", new[] { v.ProductName, v.CompanyName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
        }
        catch { return ""; }
    }

    static bool SameDir(string a, string b)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
