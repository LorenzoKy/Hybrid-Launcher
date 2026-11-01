namespace HybridLauncher.Core;

/// <summary>
/// The app's big features as the first-start picker shows them (OptiScaler, ReShade &amp; Mods, achievements...), each one the
/// game page tabs and page parts it stands for. Turning a feature off only hides it (tabs, launch bar items, pop-ups): nothing
/// is uninstalled, and Settings turns it back on. The presets are ready-made sets of them.
/// </summary>
public static class Features
{
    public const string Opti = "opti", Mods = "mods", Achievements = "ach", Hdr = "hdr", Cloud = "cloud", Pcgw = "pcgw", Extras = "extras";

    /// <param name="Tabs">Game page tabs it shows (see GameTabs in the app).</param>
    /// <param name="Parts">Game page parts it shows (see <see cref="PageParts"/>).</param>
    /// <param name="Links">Links on the game banner that belong to it ("optiwiki", "pcgw").</param>
    public sealed record Feature(string Id, string Name, string Description, string Glyph, uint Tint, string[] Tabs, string[] Parts, string[]? Links = null);

    public static readonly Feature[] All =
    [
        new(Opti, "OptiScaler", "Swap DLSS, FSR and XeSS in one click, with frame generation and a quick panel.", "", 0xFF9A7CFF, ["opti"], [], ["optiwiki"]),
        new(Mods, "ReShade & Mods", "ReShade effects, plus RenoDX and Luma for HDR.", "", 0xFFF07F2A, ["mods"], []),
        new(Achievements, "Achievements", "Trophies on the game page, and pop-ups while you play.", "trophy", 0xFFF2B82E, ["ach"], [PageParts.Achievements]),
        new(Hdr, "Smart HDR", "Windows HDR turns on with the game and off when it closes.", "", 0xFFFFC857, [], [PageParts.Hdr]),
        new(Cloud, "Cloud saves", "Saves backed up to OneDrive, MEGA or a folder, with a history.", "", 0xFF5FA8FF, [], [PageParts.Cloud]),
        new(Pcgw, "PCGamingWiki", "Known fixes, issues and settings for each game.", "", 0xFF3D9BFF, ["pcgw"], [], ["pcgw"]),
        new(Extras, "Notes & shortcuts", "Your own notes, quick-access folders and programs, and useful links per game.", "", 0xFF00C2D8, ["quick", "notes", "links"], []),
    ];

    /// <param name="Hero">Glyphs drawn big on its card (a feature id stands for that feature's icon).</param>
    public sealed record Preset(string Id, string Name, string Description, string[] On, string[] Hero, bool CrossedOut = false);

    public static readonly Preset[] Presets =
    [
        new("all", "Everything", "Every tool Hybrid Launcher has. Recommended if you are not sure.",
            [.. All.Select(f => f.Id)], [Opti, Mods, Achievements]),
        new("no-ach", "No achievements", "Everything except trophies and achievement pop-ups.",
            [.. All.Select(f => f.Id).Where(id => id != Achievements)], [Achievements], CrossedOut: true),
        new("opti-reshade", "OptiScaler + ReShade", "The two big mod tools, nothing else on the game page.",
            [Opti, Mods], [Opti, Mods]),
        new("opti", "Only OptiScaler", "Upscaling and frame generation, the rest stays out of the way.",
            [Opti], [Opti]),
        new("launcher", "Just a launcher", "Your games and Play. No mod tools, no extras.",
            [], [""]),
    ];

    public static Feature? Find(string id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>The features that are on now: none of their tabs or parts hidden for every game (and pop-ups on, for achievements).</summary>
    public static HashSet<string> Current(AppSettings s) =>
        All.Where(f => f.Tabs.All(t => !s.HiddenTabs.Contains(t)) && f.Parts.All(p => !s.HiddenPageParts.Contains(p))
                       && (f.Id != Achievements || s.AchievementPopups))
           .Select(f => f.Id).ToHashSet();

    /// <summary>The preset these features match exactly, or null (a custom set).</summary>
    public static Preset? Matching(IReadOnlySet<string> on) => Presets.FirstOrDefault(p => p.On.Length == on.Count && p.On.All(on.Contains));

    /// <summary>Shows the features in <paramref name="on"/> and hides the others, for every game. Per-game choices are left alone.</summary>
    public static void Apply(AppSettings s, IReadOnlySet<string> on)
    {
        foreach (var f in All)
        {
            bool show = on.Contains(f.Id);
            foreach (var t in f.Tabs) { if (show) s.HiddenTabs.Remove(t); else s.HiddenTabs.Add(t); }
            foreach (var p in f.Parts) { if (show) s.HiddenPageParts.Remove(p); else s.HiddenPageParts.Add(p); }
            foreach (var l in f.Links ?? []) { if (show) s.HiddenBannerLinks.Remove(l); else s.HiddenBannerLinks.Add(l); }
            if (f.Id == Achievements) s.AchievementPopups = show;
        }
        // The Game tab is never hidden by a feature: a page always has one tab.
        s.HiddenTabs.Remove("overview");
        s.FeaturesChosen = true;
    }
}
