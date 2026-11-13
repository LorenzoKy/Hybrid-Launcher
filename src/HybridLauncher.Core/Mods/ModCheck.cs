using System.Diagnostics;
using System.Text;
using HybridLauncher.Core.Opti;

namespace HybridLauncher.Core.Mods;

/// <summary>What the button of a finding does.</summary>
public enum ModFix
{
    None,
    /// <summary>Opens the tab or section in <see cref="ModProblem.Tab"/>.</summary>
    OpenTab,
    /// <summary>Installs OptiScaler again with the game's saved choices.</summary>
    ReinstallOpti,
    /// <summary>Turns off [Plugins] LoadReshade in OptiScaler.ini (and in the game's saved ini choices).</summary>
    StopOptiLoadingReShade,
}

/// <summary>
/// One thing in a game folder that stops a mod from working. <paramref name="Id"/> is stable, so a finding the user set aside
/// stays aside while it lasts.
/// </summary>
public sealed record ModProblem(string Id, string Title, string Detail, ModFix Fix = ModFix.None, string? FixLabel = null, string? Tab = null);

/// <summary>
/// Checks a game folder for mods that get in each other's way or that a game update undid. Only things that keep a mod from
/// loading are reported, each with what to do about it: a mod Hybrid Launcher installed whose files are gone or replaced,
/// OptiScaler or ReShade loaded twice, add-ons (RenoDX, Luma) beside a ReShade that skips add-ons or with no ReShade at all,
/// and DXVK whose dxgi.dll belongs to another mod. It reads the folder only; fixes are the buttons' job.
/// </summary>
public static class ModCheck
{
    public static List<ModProblem> Run(GameSettings s, string dir)
    {
        var list = new List<ModProblem>();
        if (!Directory.Exists(dir)) return list;
        try { Opti(s, dir, list); } catch (Exception ex) { Log.Warn($"Mod check (OptiScaler): {ex.Message}"); }
        try { ReShadeChecks(s, dir, list); } catch (Exception ex) { Log.Warn($"Mod check (ReShade): {ex.Message}"); }
        try { Layers(s, dir, list); } catch (Exception ex) { Log.Warn($"Mod check (DXVK): {ex.Message}"); }
        try { Others(s, dir, list); } catch (Exception ex) { Log.Warn($"Mod check (add-ons): {ex.Message}"); }
        return list;
    }

    static void Opti(GameSettings s, string dir, List<ModProblem> list)
    {
        if (s.Install is { } m && SameDir(m.TargetDir, dir))
        {
            var dll = Path.Combine(m.TargetDir, m.DllName);
            if (!File.Exists(dll))
                list.Add(new ModProblem("opti-gone", L.T("OptiScaler is gone from the game folder"),
                    L.F("Hybrid Launcher installed OptiScaler {0} as {1}, and the file is not there any more. A game update or a file check (Verify integrity) removes files the game does not know.", m.DisplayVersion, m.DllName),
                    ModFix.ReinstallOpti, L.T("Install OptiScaler again")));
            else if (!OptiDetector.IsOptiScaler(dll))
                list.Add(new ModProblem("opti-replaced", L.F("{0} is not OptiScaler any more", m.DllName),
                    L.F("Something replaced OptiScaler's {0} since Hybrid Launcher installed it: {1}. OptiScaler does not load.", m.DllName, Describe(dll)),
                    ModFix.ReinstallOpti, L.T("Install OptiScaler again")));
        }
        var copies = OptiSetupOptions.DllNames.Where(n => File.Exists(Path.Combine(dir, n)) && OptiDetector.IsOptiScaler(Path.Combine(dir, n))).ToList();
        if (copies.Count > 1)
            list.Add(new ModProblem("opti-twice", L.T("OptiScaler is in this folder twice"),
                L.F("{0} are both OptiScaler: the game loads both copies, which usually ends in a crash at start. Keep one: uninstall OptiScaler, then install it again.", string.Join(" " + L.T("and") + " ", copies)),
                ModFix.OpenTab, L.T("Open OptiScaler"), "opti/install"));
    }

    static void ReShadeChecks(GameSettings s, string dir, List<ModProblem> list)
    {
        bool optiLoads = ReShade.OptiLoadsReShade(dir) == true;
        var through = Path.Combine(dir, ReShade.OptiDllName);
        var proxies = ReShade.DllNames.Where(n => !n.Equals(ReShade.OptiDllName, StringComparison.OrdinalIgnoreCase) && !n.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase))
            .Where(n => File.Exists(Path.Combine(dir, n)) && ReShade.IsReShade(Path.Combine(dir, n))).ToList();
        if (optiLoads && File.Exists(through) && proxies.Count > 0)
            list.Add(new ModProblem("reshade-twice", L.T("ReShade loads twice"),
                L.F("ReShade is {0}, and OptiScaler also loads ReShade64.dll (LoadReshade on). Two ReShades in one game draw their effects twice or crash. Let one of them go: stop OptiScaler loading it, or remove {0} in ReShade's section.", proxies[0]),
                ModFix.StopOptiLoadingReShade, L.T("Stop OptiScaler loading ReShade")));
        else if (optiLoads && !File.Exists(through) && File.Exists(Path.Combine(dir, "OptiScaler.ini")) && OptiDetector.Detect(dir) is not null)
            list.Add(new ModProblem("reshade-missing", L.T("OptiScaler is set to load ReShade, and ReShade is not here"),
                L.T("LoadReshade is on in OptiScaler.ini, but there is no ReShade64.dll beside it, so no ReShade starts. Install ReShade through OptiScaler again, or turn the setting off."),
                ModFix.StopOptiLoadingReShade, L.T("Turn LoadReshade off")));
    }

    static void Layers(GameSettings s, string dir, List<ModProblem> list)
    {
        if (s.Dxvk is { } state)
        {
            var changed = Dxvk.Changed(state, dir);
            if (changed.Count > 0)
                list.Add(new ModProblem("dxvk-changed", L.F("{0} is not complete any more", Dxvk.Name(state.Flavor)),
                    L.F("{0} changed since Hybrid Launcher installed {1} {2}: a game update or a file check put the game's own back, or removed them.", string.Join(", ", changed), Dxvk.Name(state.Flavor), state.Version),
                    ModFix.OpenTab, L.T("Install it again"), Dxvk.Section(state.Flavor == DxvkFlavor.Vkd3d)));
        }
        // DXVK's Direct3D 10/11 and VKD3D-Proton's Direct3D 12 both need DXVK's own dxgi.dll beside them.
        var layers = Dxvk.Detect(dir);
        bool needsDxgi = layers.Any(l => l.File is "d3d11.dll" or "d3d10core.dll" or "d3d12.dll" or "d3d12core.dll");
        var dxgi = Path.Combine(dir, "dxgi.dll");
        if (needsDxgi && !layers.Any(l => l.File == "dxgi.dll"))
        {
            var owner = File.Exists(dxgi) ? Owner(dxgi) : null;
            var name = layers.First(l => l.File is "d3d11.dll" or "d3d10core.dll" or "d3d12.dll" or "d3d12core.dll");
            list.Add(new ModProblem("dxvk-dxgi", L.F("{0} is missing its dxgi.dll", name.Layer),
                owner is not null
                    ? L.F("{0}'s {1} is here, but dxgi.dll is {2}. {0} only starts with DXVK's own dxgi.dll: the game runs on Windows' Direct3D, or does not start. Give the other mod another dll name, or take {0} out.", name.Layer, name.File, owner)
                    : L.F("{0}'s {1} is here without DXVK's dxgi.dll: {0} cannot start, and the game runs on Windows' Direct3D or does not start.", name.Layer, name.File),
                ModFix.OpenTab, name.Layer == "VKD3D-Proton" ? L.T("Open VKD3D-Proton") : L.T("Open DXVK"), Dxvk.Section(name.Layer == "VKD3D-Proton")));
        }
    }

    static void Others(GameSettings s, string dir, List<ModProblem> list)
    {
        // RenoDX and Luma are ReShade add-ons: they need ReShade with full add-on support beside them.
        var addons = new List<string>();
        foreach (var pattern in new[] { "*.addon64", "*.addon32", "*.addon" })
        {
            try { addons.AddRange(Directory.GetFiles(dir, pattern).Select(Path.GetFileName)!); } catch { }
        }
        addons = addons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addons.Count > 0)
        {
            var reshade = ReShade.Loaded(dir);
            var what = addons.Count == 1 ? addons[0] : L.F("{0} add-ons", addons.Count);
            if (reshade is null)
                list.Add(new ModProblem("addon-no-reshade", addons.Count == 1 ? L.F("{0} has no ReShade to load it", addons[0]) : L.F("{0} add-ons have no ReShade to load them", addons.Count),
                    L.T("RenoDX and Luma mods are ReShade add-ons: without ReShade (the build with full add-on support) in this folder they never start."),
                    ModFix.OpenTab, L.T("Open ReShade"), "mods/reshade"));
            else if (reshade is not null && LimitedAddons(reshade.Path))
                list.Add(new ModProblem("addon-limited", L.F("ReShade {0} skips {1}", reshade.Version, what),
                    L.F("{0} is the normal ReShade build, which loads only a few add-ons. RenoDX and Luma need the build with full add-on support: install it again from ReShade's section (the version marked \"with full add-on support\").", reshade.FileName),
                    ModFix.OpenTab, L.T("Open ReShade"), "mods/reshade"));
        }
        if (s.RenoDxFiles.Count > 0 && s.RenoDxFiles.Keys.All(f => !File.Exists(Path.Combine(dir, f))))
            list.Add(new ModProblem("renodx-gone", L.T("The RenoDX mod is gone from the game folder"),
                L.F("{0} is not there any more (a game update or a file check may have cleared the folder).", string.Join(", ", s.RenoDxFiles.Keys)),
                ModFix.OpenTab, L.T("Open RenoDX"), "mods/renodx"));
        if (s.Luma is { } luma && Luma.Installed(dir).Count == 0)
            list.Add(new ModProblem("luma-gone", L.F("{0} is gone from the game folder", luma.Mod),
                L.T("Its files are not there any more (a game update or a file check may have cleared the folder)."),
                ModFix.OpenTab, L.T("Open Luma"), "mods/luma"));
    }

    // ---------- fixes that need no page ----------

    /// <summary>Turns LoadReshade off in OptiScaler.ini and drops it from the game's saved ini choices, so a reinstall keeps it off.</summary>
    public static void StopOptiLoadingReShade(GameSettings s, string dir)
    {
        var ini = Path.Combine(dir, "OptiScaler.ini");
        if (File.Exists(ini))
        {
            var doc = Ini.IniDocument.Load(ini);
            if (doc.Set("Plugins", "LoadReshade", "false")) doc.Save();
        }
        s.IniOverrides.Remove(ReShade.IniKey);
    }

    // ---------- what a file is ----------

    /// <summary>The mod a proxy dll belongs to, in words ("OptiScaler", "ReShade 6.8.0", "Special K", a product name), or null for none known.</summary>
    public static string Owner(string path)
    {
        if (OptiDetector.IsOptiScaler(path)) return "OptiScaler";
        if (ReShade.IsReShade(path)) return "ReShade";
        if (IsSpecialK(path)) return "Special K";
        if (Dxvk.LayerOf(path) is { } layer) return layer;
        return Describe(path);
    }

    public static bool IsSpecialK(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return (v.ProductName ?? "").Contains("Special K", StringComparison.OrdinalIgnoreCase) || (v.FileDescription ?? "").Contains("Special K", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static string Describe(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var what = string.Join(" ", new[] { v.ProductName, v.ProductVersion }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return what.Length > 0 ? what : L.T("a file of unknown origin");
        }
        catch { return L.T("a file of unknown origin"); }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, long Length, bool Limited)> Limited = new(StringComparer.OrdinalIgnoreCase);
    static readonly byte[] LimitedText = Encoding.ASCII.GetBytes("only limited add-on functionality");

    /// <summary>
    /// The normal ReShade build (not the one "with full add-on support"): it carries the message it logs when it skips an add-on,
    /// which the add-on build does not have.
    /// </summary>
    public static bool LimitedAddons(string reshadeDll)
    {
        try
        {
            var info = new FileInfo(reshadeDll);
            if (!info.Exists) return false;
            if (Limited.TryGetValue(reshadeDll, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length) return known.Limited;
            bool limited = File.ReadAllBytes(reshadeDll).AsSpan().IndexOf(LimitedText) >= 0;
            Limited[reshadeDll] = (info.LastWriteTimeUtc, info.Length, limited);
            return limited;
        }
        catch { return false; }
    }

    static bool SameDir(string a, string b)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
