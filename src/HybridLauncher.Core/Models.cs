using System.Text.Json.Serialization;

namespace HybridLauncher.Core;

public enum GameStore { Steam, Epic, Gog, Xbox, Custom }

/// <summary>A game found on disk. Detection output only; user choices live in <see cref="GameSettings"/>.</summary>
public sealed class Game
{
    /// <summary>Stable key, e.g. "steam:1091500", "epic:Ginger", "custom:&lt;exe path&gt;".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required GameStore Store { get; init; }
    public required string InstallDir { get; init; }

    /// <summary>Executable reported by the store, when it tells us (Epic, GOG, Xbox, custom).</summary>
    public string? StoreExe { get; init; }

    /// <summary>The exe OptiScaler goes next to, found by <see cref="Detection.ExeLocator"/>. A user override wins (see GameSettings).</summary>
    public string? MainExe { get; set; }

    public string? SteamAppId { get; init; }

    /// <summary>
    /// A game run through a Steam emulator (Goldberg / GSE): its steam_settings folder and the Steam app id it was set up with,
    /// found by the scan. The id finds the game's pages; achievements and last session are read from the emulator's saves.
    /// </summary>
    public string? EmulatorSettingsDir { get; set; }
    public string? EmulatorAppId { get; set; }
    public string? EpicLaunchUri { get; init; }
    public string? XboxAumid { get; init; }

    /// <summary>Downloaded by Hybrid Launcher from a launcher account: started and uninstalled by Hybrid Launcher, no launcher involved.</summary>
    public bool Direct { get; init; }

    /// <summary>An image to use as the list icon instead of the exe's icon (Game Pass exes usually have none: the package logo is used).</summary>
    public string? IconPath { get; init; }

    /// <summary>Still being downloaded by its launcher (Steam): listed, but not playable yet.</summary>
    public bool Installing { get; init; }

    /// <summary>Owned on a linked launcher but not on this PC: its page shows Download instead of Play (no folder, no mods).</summary>
    public bool NotInstalled { get; init; }

    /// <summary>For games found in a "games folder": that folder (the library groups them under its name).</summary>
    public string? LibraryRoot { get; init; }

    public string StoreLabel => Store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic Games",
        GameStore.Gog => "GOG",
        GameStore.Xbox => "Xbox / Game Pass",
        _ => L.T("Custom"),
    };

    public override string ToString() => $"{Name} [{Id}] {InstallDir}";
}

/// <summary>The answers given to OptiScaler's setup_windows.bat, saved per game and replayed on every install.</summary>
public sealed class OptiSetupOptions
{
    public static readonly string[] DllNames =
        ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll", "OptiScaler.asi"];

    public string DllName { get; set; } = "dxgi.dll";

    /// <summary>null = use what the bat detects (nvapi64.dll present = Nvidia).</summary>
    public bool? Nvidia { get; set; }

    /// <summary>"Will you try to use DLSS inputs ... (enables Nvidia spoofing)". Only asked for AMD/Intel.</summary>
    public bool Spoofing { get; set; } = true;

    /// <summary>Download OptiPatcher.asi when the bat says the game is supported. Only asked for AMD/Intel.</summary>
    public bool OptiPatcher { get; set; } = true;

    /// <summary>Let the bat delete pre-0.9 leftovers (they are backed up first).</summary>
    public bool DeleteOldFiles { get; set; } = true;

    /// <summary>
    /// "Bat install": open setup_windows.bat in its own window and let the user answer it; the answers above are not used.
    /// Chosen per install with the button next to Install, so it is not saved.
    /// </summary>
    [JsonIgnore]
    public bool Manual { get; set; }

    public OptiSetupOptions Clone() => (OptiSetupOptions)MemberwiseClone();
}

/// <summary>Custom = a build imported by the user (see <see cref="Opti.CustomBuilds"/>), not a GitHub release.</summary>
public enum OptiChannel { Stable, Nightly, Custom }

/// <summary>What Hybrid Launcher put into a game folder, so it can be removed exactly and originals restored.</summary>
public sealed class InstallManifest
{
    public OptiChannel Channel { get; set; }
    public string Tag { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string DllName { get; set; } = "";
    public string PluginsDir { get; set; } = "plugins";
    public DateTime InstalledAt { get; set; }

    /// <summary>Files created by the install, relative to <see cref="TargetDir"/>, with their SHA-256 at install time.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories created by the install, relative, deepest first when removed.</summary>
    public List<string> CreatedDirs { get; set; } = [];

    /// <summary>Folder holding this install's backups; deleted after a clean uninstall.</summary>
    public string? BackupRoot { get; set; }

    /// <summary>Pre-existing files that were replaced or deleted: relative path -> backup file path.</summary>
    public Dictionary<string, string> Backups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ASI plugins copied from the universal folder: file name -> SHA-256.</summary>
    public Dictionary<string, string> AsiPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A copy of OptiScaler.ini as the install left it, before Hybrid Launcher's saved choices (OptiScaler settings → Restore defaults).</summary>
    public string? DefaultIni { get; set; }

    [JsonIgnore]
    public string DisplayVersion => Channel switch
    {
        OptiChannel.Nightly => L.F("Nightly {0}", Tag),
        OptiChannel.Custom => L.F("Custom build \"{0}\"", AssetName),
        _ => Tag,
    };
}

public sealed class GameSettings
{
    public string? ExeOverride { get; set; }
    public string LaunchArgs { get; set; } = "";

    /// <summary>Show the launch arguments box in the play bar (off: they are set in ⋯ → Launch options only).</summary>
    public bool LaunchArgsInBar { get; set; }

    /// <summary>
    /// The Steam store page the user picked for this game when Hybrid Launcher matched the wrong one (⋯ → Choose the game's page):
    /// art, description, wikis and covers follow it. "none" = no store page. Null = Hybrid Launcher's own match.
    /// </summary>
    public string? PageAppId { get; set; }

    /// <summary>The name to look the game up by on wikis and sites, set with <see cref="PageAppId"/>; null = the store name made plain.</summary>
    public string? PageName { get; set; }

    public OptiSetupOptions Setup { get; set; } = new();

    /// <summary>"Section/Key" -> value, re-applied to OptiScaler.ini after every (re)install.</summary>
    public Dictionary<string, string> IniOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public InstallManifest? Install { get; set; }

    /// <summary>
    /// Plugins from the shared ASI folder turned on for this game only (file names), copied into its OptiScaler install.
    /// Null until set: before 0.11 one switch served every game, and the plugins an install already holds carry over.
    /// </summary>
    public HashSet<string>? AsiPlugins { get; set; }

    /// <summary>Size of the install folder at the last measurement (shown under "Open game folder" before the new one is done).</summary>
    public long? SizeBytes { get; set; }
    public DateTime? SizeMeasuredAt { get; set; }

    /// <summary>Time played and last session, counted by Hybrid Launcher for games it starts (Steam keeps its own, see <see cref="Playtime"/>).</summary>
    public long PlayMinutes { get; set; }
    public DateTime? LastPlayed { get; set; }

    /// <summary>Game page tabs hidden for this game only (the global ones are in <see cref="AppSettings.HiddenTabs"/>).</summary>
    public HashSet<string> HiddenTabs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parts of the game page hidden for this game only (see <see cref="PageParts"/>; the global ones are in <see cref="AppSettings.HiddenPageParts"/>).</summary>
    public HashSet<string> HiddenPageParts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>RenoDX / Luma mods taken out of "Mods for this game" with their X ("renodx:Name", "luma:Name"), for this game.</summary>
    public HashSet<string> HiddenModCards { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Quick access tab: folders, programs and files linked by the user, in their order.</summary>
    public List<QuickLink> QuickLinks { get; set; } = [];

    /// <summary>Free notes for this game (before 1.0: one text; now the first section of <see cref="NoteSections"/>).</summary>
    public string Notes { get; set; } = "";

    /// <summary>The Notes tab: named pages of notes, in the user's order.</summary>
    public List<NoteSection> NoteSections { get; set; } = [];

    /// <summary>
    /// Smart HDR: Hybrid Launcher turns Windows HDR on when this game starts (only if it was off) and back off when the game closes,
    /// however it closes.
    /// </summary>
    public bool SmartHdr { get; set; }

    /// <summary>Cloud saves: this game is left out of the sync (on by default for every game once cloud saves are on).</summary>
    public bool CloudOff { get; set; }
    /// <summary>The saves folder the cloud sync uses: found on PCGamingWiki, or picked by the user (<see cref="CloudSaveDirPicked"/>).</summary>
    public string? CloudSaveDir { get; set; }
    public bool CloudSaveDirPicked { get; set; }
    /// <summary>The game's settings folder (graphics, controls) PCGamingWiki names, synced beside the saves when it is another folder.</summary>
    public string? CloudConfigDir { get; set; }
    /// <summary>PCGamingWiki was asked for the settings folder once (found or not): not asked again at every Play.</summary>
    public bool CloudConfigChecked { get; set; }
    /// <summary>The saves' content hash at the last sync on this PC: what tells "changed here" from "changed in the cloud".</summary>
    public string? CloudSyncedHash { get; set; }
    public DateTime? CloudSyncedAt { get; set; }

    /// <summary>
    /// The Quick Panel profile: true is "This game only" (<see cref="QuickPanelExtras"/> and <see cref="QuickPanelHidden"/>
    /// below), false is "Global", the panel every game set to Global shares.
    /// </summary>
    public bool QuickPanelOwn { get; set; }
    /// <summary>The game's own profile exists (it was made once, as a copy of the Global one); an emptied one stays empty.</summary>
    public bool QuickPanelOwnMade { get; set; }
    public List<string> QuickPanelExtras { get; set; } = [];
    public HashSet<string> QuickPanelHidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Luma mod Hybrid Launcher put in the game folder, so it can be removed exactly.</summary>
    public LumaState? Luma { get; set; }

    /// <summary>ReShade in this game folder, as Hybrid Launcher set it up (loaded through OptiScaler or not).</summary>
    public ReShadeState ReShade { get; set; } = new();

    /// <summary>RenoDX add-on files Hybrid Launcher put next to the game exe: file name -> where it came from.</summary>
    public Dictionary<string, string> RenoDxFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When Hybrid Launcher first listed this game (the library's "Date added" order).</summary>
    public DateTime? AddedAt { get; set; }
}

/// <summary>One link of the Quick access tab.</summary>
public sealed class QuickLink
{
    /// <summary>"folder", "exe" or "file".</summary>
    public string Kind { get; set; } = "file";
    public string Path { get; set; } = "";
    public string? Label { get; set; }
}

/// <summary>One page of the Notes tab.</summary>
public sealed class NoteSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>A Luma mod installed by Hybrid Launcher: which one, which build, and every file it added (relative to the game exe folder).</summary>
public sealed class LumaState
{
    public string Mod { get; set; } = "";
    public string Asset { get; set; } = "";
    public string Release { get; set; } = "";
    public DateTime InstalledAt { get; set; }
    public List<string> Files { get; set; } = [];
    /// <summary>Files that were there before and were replaced: relative path -> backup path.</summary>
    public Dictionary<string, string> Backups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>ReShade from the Luma package went in as ReShade64.dll, loaded by OptiScaler.</summary>
    public bool ThroughOpti { get; set; }
}

/// <summary>A display whose HDR Hybrid Launcher turned on for a game (turned off again when the game closes, or at the next start).</summary>
public sealed class HdrRestore
{
    public string GameId { get; set; } = "";
    public uint AdapterLow { get; set; }
    public int AdapterHigh { get; set; }
    public uint TargetId { get; set; }
}

/// <summary>
/// Launcher accounts for the Downloads tab. Signing in (Downloads → Accounts) lets Hybrid Launcher list every game the account owns and
/// download and uninstall them itself; the sign-in tokens are kept apart, encrypted for this Windows user (see AccountTokens).
/// Not signed in, the lists come from what the launchers keep on this PC.
/// </summary>
public sealed class LinkedAccounts
{
    /// <summary>SteamID64 of the account (from the sign-in, or from Steam's own login on this PC).</summary>
    public string? SteamId { get; set; }
    public string? SteamName { get; set; }
    /// <summary>The names shown for the signed-in Epic and GOG accounts.</summary>
    public string? EpicName { get; set; }
    public string? GogName { get; set; }
    public bool Steam { get; set; } = true;
    public bool Epic { get; set; } = true;
    public bool Gog { get; set; } = true;
    public bool Xbox { get; set; } = true;
}

/// <summary>A game Hybrid Launcher downloaded from a launcher account: where it is and how it starts (from the store's own data).</summary>
public sealed class DirectInstall
{
    /// <summary>The same id the launcher's install would have ("steam:1091500", "epic:Ginger", "gog:1207658924").</summary>
    public string Id { get; set; } = "";
    public GameStore Store { get; set; }
    public string Name { get; set; } = "";
    public string InstallDir { get; set; } = "";
    /// <summary>The exe to start and its working folder, relative to <see cref="InstallDir"/>, and its arguments.</summary>
    public string? Exe { get; set; }
    public string? Args { get; set; }
    public string? WorkingDir { get; set; }
    public string? Version { get; set; }
    public long Size { get; set; }
    public DateTime InstalledAt { get; set; } = DateTime.Now;
}

/// <summary>What Hybrid Launcher did with ReShade in a game folder, so it can be undone.</summary>
public sealed class ReShadeState
{
    /// <summary>The name ReShade's dll had before Hybrid Launcher renamed it to ReShade64.dll (e.g. dxgi.dll); null = not renamed by Hybrid Launcher.</summary>
    public string? RenamedFrom { get; set; }

    /// <summary>ReShade64.dll was copied back from OptiScaler's backup (ReShade had been replaced when OptiScaler went in).</summary>
    public bool RestoredFromBackup { get; set; }

    /// <summary>The last installer started for this game had full add-on support (needed by RenoDX); null = unknown.</summary>
    public bool? Addon { get; set; }
    public string? InstallerVersion { get; set; }
}

/// <summary>A named set of OptiScaler.ini values ("Section/Key" -> value) that can be applied to any game.</summary>
public sealed class IniProfile
{
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Achievement pop-up options (Settings → Achievements).</summary>
public sealed class PopupSettings
{
    /// <summary>"BottomRight", "BottomLeft", "TopRight", "TopLeft", or "Custom" (<see cref="X"/>, <see cref="Y"/>).</summary>
    public string Corner { get; set; } = "BottomRight";

    /// <summary>Where a "Custom" pop-up sits: 0 = left / top edge of the screen's work area, 1 = right / bottom edge.</summary>
    public double X { get; set; } = 1;
    public double Y { get; set; } = 1;

    /// <summary>#RRGGBB of the pop-up's background; empty = a dark tint of its colour.</summary>
    public string Background { get; set; } = "";

    /// <summary>How solid the background is (0.6 to 1).</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Seconds on screen.</summary>
    public double Seconds { get; set; } = 5.5;

    /// <summary>"Normal" or "Large".</summary>
    public string Size { get; set; } = "Normal";

    /// <summary>"Accent" (colour theme), "Gold", "Steam" (blue), or "Custom" with <see cref="CustomColor"/>.</summary>
    public string Style { get; set; } = "Accent";

    /// <summary>#RRGGBB used when <see cref="Style"/> is "Custom".</summary>
    public string CustomColor { get; set; } = "#9A5CFF";

    /// <summary>"None", "Windows" (the system notification sound) or "File" (<see cref="SoundFile"/>, a .wav).</summary>
    public string Sound { get; set; } = "Windows";
    public string? SoundFile { get; set; }

    /// <summary>Show the share of players who have it (Steam's global stats).</summary>
    public bool ShowRarity { get; set; } = true;

    /// <summary>"Game" (the display the game runs on) or "Primary".</summary>
    public string Screen { get; set; } = "Game";
}

/// <summary>How the library is ordered and filtered.</summary>
public sealed class LibraryFilter
{
    /// <summary>"name", "recent" (last played), "added", "playtime", "achievements" or "tech" (most upscaler / FG tags).</summary>
    public string Sort { get; set; } = "name";
    public bool Descending { get; set; }

    /// <summary>Keep the launcher groups; off = one block of every game in the chosen order.</summary>
    public bool GroupByLauncher { get; set; } = true;

    public bool OptiOnly { get; set; }

    /// <summary>Launcher group keys to show; empty = all.</summary>
    public HashSet<string> Launchers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Only games with any of these tech tags ("DLSS", "FSR", "XeSS", "FG"); empty = all.</summary>
    public HashSet<string> Tech { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Also show games you own on a linked launcher but have not installed (with a Download button).</summary>
    public bool ShowNotInstalled { get; set; }

    /// <summary>Cover width the user zoomed the grid to (Ctrl+wheel or the zoom slider), in px; 0 = automatic.</summary>
    public double TileSize { get; set; }

    public bool IsFiltering => OptiOnly || Launchers.Count > 0 || Tech.Count > 0 || ShowNotInstalled;
}

public sealed class CustomGame
{
    public string ExePath { get; set; } = "";
    public string? Name { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string Theme { get; set; } = "System";

    /// <summary>UI language: "auto" (Windows' language), "en" or "it". Applied at the next start.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Material colours: cards, outlines and the window take a trace of the colour theme (and the Windows accent).
    /// Off (the default since 0.21) = neutral grey surfaces, only the controls in the accent colour.
    /// </summary>
    public bool Material { get; set; }

    /// <summary>Which one-time look changes were applied to these settings (2 = Material turned off once, 0.21).</summary>
    public int LookVersion { get; set; }

    /// <summary>The welcome tour was shown (finished or skipped); Settings can start it again.</summary>
    public bool TourSeen { get; set; }

    /// <summary>Icons of the game page tabs: "mono" (one colour, the default), "color" (each its own colour) or "none".</summary>
    public string TabIcons { get; set; } = "color";

    /// <summary>Closing the window keeps Hybrid Launcher running in the notification area (games keep being tracked, Smart HDR keeps working).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Hardware acceleration: videos decoded on the GPU, blur and reflection effects, animated pictures. Off = the lightest
    /// drawing (no effects, videos still), for weak or busy GPUs. Applied at once.
    /// </summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>The game list's width, dragged by its edge (240 to 480, and at most 45 % of the window).</summary>
    public double SidebarWidth { get; set; } = 312;

    /// <summary>Start Hybrid Launcher with Windows (in the notification area, without a window).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Displays whose HDR Hybrid Launcher turned on for a running game: turned back off when it closes, or at the next start.</summary>
    public List<HdrRestore> HdrRestore { get; set; } = [];

    /// <summary>Launcher accounts for the Downloads tab.</summary>
    public LinkedAccounts Accounts { get; set; } = new();

    /// <summary>Cards of the game overview's side column that can be turned off (Settings → Features).</summary>
    public bool ShowHltb { get; set; } = true;
    public bool ShowMetacritic { get; set; } = true;

    /// <summary>Colour palette on top of the light/dark theme: "Windows" keeps the Windows accent colour.</summary>
    public string Palette { get; set; } = "Windows";

    /// <summary>The "My colours" palette (#RRGGBB): accent, the glow behind the window, and an optional own dark background.</summary>
    public string CustomAccent { get; set; } = "#9A5CFF";
    public string CustomTint { get; set; } = "#8A4DFF";
    public bool CustomBackdrop { get; set; }
    public string CustomBackground { get; set; } = "#16121F";

    public string? LastGameId { get; set; }

    /// <summary>Games pinned to the quick-access row on top of the list, in pin order.</summary>
    public List<string> PinnedGames { get; set; } = [];

    /// <summary>How the games are shown: "list" (in the side pane, by store) or "grid" (cover art in the library page).</summary>
    public string LibraryView { get; set; } = "list";

    /// <summary>Show a pop-up when Steam records a newly unlocked achievement.</summary>
    public bool AchievementPopups { get; set; } = true;

    /// <summary>Cards the user folded on the game page (e.g. "opti", "asi", "ini"); shared by all games.</summary>
    public HashSet<string> CollapsedCards { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last topic of the OptiScaler settings tab: "topic:Upscaling", "restart", "changed", "sec:FrameGen"... ("essentials" = the first).</summary>
    public string IniView { get; set; } = "essentials";

    /// <summary>Last tab of the game page ("overview", "opti", "settings", "reshade", "renodx", "optiwiki", "pcgw", "ach", "links", "quick", "notes").</summary>
    public string GameTab { get; set; } = "overview";

    /// <summary>Order of the game page tabs, as the user arranged them; tabs missing here keep their default place.</summary>
    public List<string> TabOrder { get; set; } = [];

    /// <summary>Game page tabs hidden for every game.</summary>
    public HashSet<string> HiddenTabs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The links on a game's banner (Nexus Mods, PCGamingWiki...), hidden all at once with the eye.</summary>
    public bool HideBannerLinks { get; set; }

    /// <summary>Banner links hidden one by one ("nexus", "pcgw", "steamdb", "optiwiki").</summary>
    public HashSet<string> HiddenBannerLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parts of the game page hidden for every game (see <see cref="PageParts"/>): play stats, Smart HDR, folders, tags...</summary>
    public HashSet<string> HiddenPageParts { get; set; } = new(StringComparer.OrdinalIgnoreCase) { PageParts.ModsPath };

    /// <summary>Since 0.13 the "where mods go" line under the launch bar starts hidden; set once that default was applied to old settings.</summary>
    public bool ModsPathDefaultApplied { get; set; }

    /// <summary>
    /// Places where icons are left out, one switch each in Settings → Appearance (see <see cref="IconSpots"/>): the tabs, the
    /// sub-tabs, the section separators, the card headings, the launch bar, the banner and the game list.
    /// </summary>
    public HashSet<string> HiddenIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>OptiScaler.ini settings ("Section/Key") the user added to the Quick Panel, in their order, after its own ones.</summary>
    public List<string> QuickPanelExtras { get; set; } = [];

    /// <summary>The Quick Panel's own settings the user took out of it ("fps", "fg", "asi").</summary>
    public HashSet<string> QuickPanelHidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Library groups (launchers, games folders) folded in the list and the grid, by group key.</summary>
    public HashSet<string> CollapsedGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Order of the library groups, by group key; groups missing here keep their default place.</summary>
    public List<string> GroupOrder { get; set; } = [];

    /// <summary>The library's order and filters (the Filters button of the grid).</summary>
    public LibraryFilter Library { get; set; } = new();

    /// <summary>The main window at the last close (size in physical pixels); null = first start.</summary>
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>The Settings page's last tab ("appearance", "features", "storage", "about").</summary>
    public string SettingsTab { get; set; } = "appearance";

    /// <summary>How the achievement pop-up looks and behaves.</summary>
    public PopupSettings Popup { get; set; } = new();

    /// <summary>Folders whose sub-folders are each treated as one game (e.g. D:\Games).</summary>
    public List<string> GameLibraries { get; set; } = [];

    public List<CustomGame> CustomGames { get; set; } = [];

    /// <summary>Games Hybrid Launcher downloaded itself from a signed-in launcher account (no launcher installed them).</summary>
    public List<DirectInstall> DirectInstalls { get; set; } = [];

    /// <summary>Games Hybrid Launcher downloaded are checked for updates at start and every few hours, and updated by themselves.</summary>
    public bool AutoUpdateGames { get; set; } = true;

    /// <summary>A game the app downloads gets a desktop shortcut once it is installed.</summary>
    public bool ShortcutOnInstall { get; set; } = true;

    /// <summary>The app looks for a newer version of itself on GitHub a few seconds after it starts.</summary>
    public bool CheckAppUpdates { get; set; } = true;

    /// <summary>The app version the user answered Later to: the start-up check does not ask about it again.</summary>
    public string? SkippedAppUpdate { get; set; }

    /// <summary>Before 1.2: one folder for every store's downloads; moved into <see cref="InstallRoots"/> when the settings load.</summary>
    public string? InstallRoot { get; set; }

    /// <summary>Where Hybrid Launcher installs each store's games ("Steam", "Epic", "Gog" -> folder); missing = that launcher's own folder.</summary>
    public Dictionary<string, string> InstallRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cloud saves: where they go and whether the sync runs (Settings → Cloud saves).</summary>
    public CloudSaveSettings Cloud { get; set; } = new();

    /// <summary>Library covers show the upscaler and frame generation tags (DLSS, FSR, XeSS...). Settings → Library, or the library's Filters.</summary>
    public bool ShowCoverTags { get; set; } = true;

    public HashSet<string> HiddenGames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GameSettings> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names the user gave to ASI plugins (file name → shown name); the file keeps its own name.</summary>
    public Dictionary<string, string> AsiNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names the user gave to saved OptiScaler versions (version folder → shown name); the folder keeps its own name.</summary>
    public Dictionary<string, string> VersionNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Saved OptiScaler.ini profiles, shared by all games.</summary>
    public List<IniProfile> IniProfiles { get; set; } = [];

    /// <summary>Where imported OptiScaler builds live; null = %LOCALAPPDATA%\Hybrid Launcher\CustomBuilds.</summary>
    public string? CustomBuildsDir { get; set; }

    /// <summary>
    /// Settings → Features: what happens to the game's own files a mod replaces (OptiScaler, Luma): "backup" (copied to the
    /// backups first and put back on uninstall), "replace" (overwritten, nothing kept) or "ask" (asked at each install).
    /// </summary>
    public string ModBackups { get; set; } = "backup";

    /// <summary>Settings → Features: read achievements and the last session of games run through a Steam emulator (Goldberg / GSE).</summary>
    public bool ReadSteamEmulator { get; set; } = true;

    public bool ScanSteam { get; set; } = true;
    public bool ScanEpic { get; set; } = true;
    public bool ScanGog { get; set; } = true;
    public bool ScanXbox { get; set; } = true;

    public GameSettings For(string gameId)
    {
        if (!Games.TryGetValue(gameId, out var s))
        {
            s = new GameSettings();
            Games[gameId] = s;
        }
        return s;
    }
}

/// <summary>The places whose icons can be left out (Settings → Appearance → Icons), each with its label and what it covers.</summary>
public static class IconSpots
{
    public const string Tabs = "tabs", SubTabs = "subtabs", Headings = "headings",
        LaunchBar = "launchbar", Banner = "banner";

    public static readonly (string Key, string Label, string Description)[] All =
    [
        (Tabs, "Tabs", "The icons of the game page tabs"),
        (SubTabs, "Sub-tabs", "The icons of the sections under a tab (OptiScaler, ReShade & Mods...)"),
        (Headings, "Card headings", "The coloured badges of the cards inside the tabs"),
        (LaunchBar, "Launch bar", "The icons of HDR, the folders and the achievements beside Play"),
        (Banner, "Banner", "The logos of the links and tags on the game's banner"),
    ];

    public static bool Shown(AppSettings settings, string spot) => !settings.HiddenIcons.Contains(spot);
}

/// <summary>
/// The parts of the game page that can be hidden, for every game (Settings → Appearance) or for one game (⋯ → Show on this
/// page). The banner links keep their own switches too (the eye on the banner).
/// </summary>
public static class PageParts
{
    public const string Links = "links", Tags = "tags", PlayStats = "playstats", Hdr = "hdr", Cloud = "cloud", GameFolder = "gamefolder",
        SaveFolder = "savefolder", ConfigFolder = "configfolder", Achievements = "achievements", ModsPath = "modspath";

    public static readonly (string Key, string Label)[] All =
    [
        (Links, "Links on the banner"),
        (Tags, "Tags on the banner (store, graphics API, upscalers, developer and date)"),
        (PlayStats, "Last played and play time"),
        (Hdr, "Smart HDR"),
        (Cloud, "Cloud saves"),
        (GameFolder, "Open game folder"),
        (SaveFolder, "Open saves folder"),
        (ConfigFolder, "Open configuration folder"),
        (Achievements, "Achievements button"),
        (ModsPath, "Where mods go (the line under the bar)"),
    ];

    public static bool HiddenEverywhere(AppSettings settings, string part) => settings.HiddenPageParts.Contains(part);

    public static bool Hidden(AppSettings settings, GameSettings? game, string part) =>
        HiddenEverywhere(settings, part) || game?.HiddenPageParts.Contains(part) == true;
}
