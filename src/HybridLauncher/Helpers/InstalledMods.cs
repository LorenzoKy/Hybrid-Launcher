using HybridLauncher.Core;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Helpers;

/// <summary>One mod found in a game's folder: its short tag, colour and what the tooltip says.</summary>
public sealed record ModTag(string Short, Windows.UI.Color Color, string Detail, string? Version = null)
{
    /// <summary>The setting key of this kind of tag (AppSettings.CoverTagKinds).</summary>
    public string Key => Short switch { "Opti" => "opti", "RS" => "rs", "RDX" => "rdx", "DXVK" or "VKD3D" => "dxvk", _ => "uplus" };

    /// <summary>The mod's full name, for the game Dashboard.</summary>
    public string Name => Short switch { "Opti" => "OptiScaler", "RS" => "ReShade", "RDX" => "RenoDX", "DXVK" => "DXVK", "VKD3D" => "VKD3D-Proton", _ => "Ultra+" };

    /// <summary>The game page tab that manages it.</summary>
    public string Tab => Short switch { "Opti" => "opti", "U+" => "ultra", _ => "mods" };
}

/// <summary>
/// The mods in a game's folder now: OptiScaler, ReShade, RenoDX, DXVK / VKD3D-Proton and Ultra+, found by the same checks as the game page's tabs (a
/// mod put there by hand or by another tool shows too). Reads files: call it off the UI thread. Used by the library's cover tags
/// and the game Dashboard.
/// </summary>
public static class InstalledMods
{
    public static List<ModTag> Find(Game game, GameSettings? settings)
    {
        var list = new List<ModTag>();
        if (game.NotInstalled) return list;
        if (Library.OptiVersion(game) is { } opti)
            list.Add(new("Opti", Windows.UI.Color.FromArgb(0xFF, 0x5F, 0xD0, 0x7A), L.F("OptiScaler {0} is installed", opti), opti.Split("  ·  ")[0].Trim()));
        var dir = GameScanner.TargetDir(game, settings ?? new GameSettings());
        if (dir is not null)
        {
            if (HybridLauncher.Core.Mods.ReShade.Detect(dir) is { } rs)
                list.Add(new("RS", Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xA1, 0x3C),
                    rs.LoadedByOpti ? L.F("ReShade {0} is installed, loaded by OptiScaler", rs.Version) : L.F("ReShade {0} is installed", rs.Version),
                    rs.LoadedByOpti ? L.F("{0} · loaded by OptiScaler", rs.Version) : rs.Version));
            if (HybridLauncher.Core.Mods.RenoDx.Installed(dir) is { Count: > 0 } reno)
                list.Add(new("RDX", Windows.UI.Color.FromArgb(0xFF, 0x4F, 0xD1, 0xC5), L.F("RenoDX is installed: {0}", string.Join(", ", reno.Select(Path.GetFileName))),
                    string.Join(", ", reno.Select(Path.GetFileNameWithoutExtension))));
            // DXVK (gplasync too) or VKD3D-Proton: the one Hybrid Launcher installed with its version, or the files of one put in by hand.
            if (settings?.Dxvk is { } dx)
                list.Add(new(dx.Flavor == HybridLauncher.Core.Mods.DxvkFlavor.Vkd3d ? "VKD3D" : "DXVK", Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x4A, 0x5F),
                    L.F("{0} {1} is installed", HybridLauncher.Core.Mods.Dxvk.Name(dx.Flavor), dx.Version), dx.Version));
            else if (HybridLauncher.Core.Mods.Dxvk.Detect(dir) is { Count: > 0 } layers)
            {
                bool vkd3d = layers.Any(l => l.Layer == "VKD3D-Proton");
                list.Add(new(vkd3d ? "VKD3D" : "DXVK", Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x4A, 0x5F), L.F("{0} is in the game folder", vkd3d ? "VKD3D-Proton" : "DXVK")));
            }
        }
        if (settings?.UltraPlus is { } ultra)
            list.Add(new("U+", Windows.UI.Color.FromArgb(0xFF, 0x7F, 0xB2, 0xFF),
                ultra.Version.Length > 0 ? L.F("Ultra+ {0} is installed", ultra.Version) : L.T("Ultra+ is installed"), ultra.Version.Length > 0 ? "v" + ultra.Version : null));
        return list;
    }
}
