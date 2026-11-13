using System.Text.Json.Serialization;

namespace HybridLauncher.Core;

/// <summary>Where a game comes from. Saved by name (settings), so new launchers go before Custom: its place in the list orders the groups.</summary>
public enum GameStore { Steam, Epic, Gog, Xbox, Ubisoft, Ea, Rockstar, Custom }

/// <summary>A launcher's own exe and the arguments that make it start one game (Rockstar Games Launcher).</summary>
public sealed record LauncherCommand(string Exe, string Args);

/// <summary>A game found on disk. Detection output only; user choices live in <see cref="GameSettings"/>.</summary>
public sealed class Game
{
    /// <summary>Stable key, e.g. "steam:1091500", "epic:Ginger", "custom:&lt;exe path&gt;".</summary>
    public required string Id { get; init; }
    /// <summary>The name the store or the scan gave the game: wikis, store pages and searches look it up by this one.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The name shown in Hybrid Launcher: the user's own (right-click → Rename, kept in <see cref="AppSettings.GameNames"/>) or
    /// <see cref="Name"/>. Only for showing: lookups keep using <see cref="Name"/>.
    /// </summary>
    [JsonIgnore]
    public string Title => SettingsStore.Current.GameNames.TryGetValue(Id, out var own) && own.Length > 0 ? own : Name;

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

    /// <summary>The launcher's link that starts the game (Ubisoft Connect: uplay://launch/&lt;id&gt;/0).</summary>
    public string? LaunchUri { get; init; }
    /// <summary>The launcher exe that starts the game (Rockstar Games Launcher), when it has no link for it.</summary>
    public LauncherCommand? LauncherCommand { get; init; }
    /// <summary>The command Windows runs to uninstall the game (its installed-programs entry), for launchers found through it.</summary>
    public string? UninstallCommand { get; init; }

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
        GameStore.Ubisoft => "Ubisoft Connect",
        GameStore.Ea => "EA app",
        GameStore.Rockstar => "Rockstar Games Launcher",
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

    /// <summary>
    /// The user set <see cref="Spoofing"/> themselves (2.0.0-rc.11). Until then it follows the game: off when OptiPatcher
    /// supports it (OptiPatcher unlocks the DLSS inputs without spoofing), on otherwise.
    /// </summary>
    public bool SpoofingSet { get; set; }

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

/// <summary>Custom = a build imported by the user (see <see cref="Opti.CustomBuilds"/>), not a GitHub release. Fork = a release of another OptiScaler repository (see <see cref="OptiFork"/>).</summary>
public enum OptiChannel { Stable, Nightly, Custom, Fork }

/// <summary>DLSSNR-AMD in a game (<see cref="Opti.DlssNrAmd"/>): the release, the network it starts with, what it changed.</summary>
public sealed class DlssNrAmdInstall
{
    public string Tag { get; set; } = "";
    public string Network { get; set; } = "";
    public bool RenoDx { get; set; } = true;
    public string PluginHash { get; set; } = "";
    /// <summary>OptiScaler.ini keys changed for it ("Section|Key") and their values before.</summary>
    public Dictionary<string, string> OptiIniBefore { get; set; } = [];
    /// <summary>A dlssnr-amd folder the game had before, kept aside.</summary>
    public string? OwnFolderBackup { get; set; }
}

/// <summary>
/// DLSSNR-AMD through ReShade (1.4.0-rc.20), with no OptiScaler: the package's reshade-feeder routes. What went into the game
/// folder (files and folders, by their path in it) and what was there before under the same names, so it comes out exactly.
/// </summary>
public sealed class DlssNrAmdReShadeInstall
{
    public string Tag { get; set; } = "";
    /// <summary>"reshade-feeder" (DirectX 10/11/12), "reshade-feeder-dx9" (DirectX 9 through DXVK) or "reshade-feeder-vulkan".</summary>
    public string Route { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string Network { get; set; } = "";
    public bool RenoDx { get; set; } = true;
    public DateTime InstalledAt { get; set; }
    public List<string> Files { get; set; } = [];
    public List<string> Dirs { get; set; } = [];
    /// <summary>Where the game's own files of the same names went (same paths below it).</summary>
    public string? BackupDir { get; set; }
    public List<string> BackedUp { get; set; } = [];
}

/// <summary>What Hybrid Launcher put into a game folder, so it can be removed exactly and originals restored.</summary>
public sealed class InstallManifest
{
    public OptiChannel Channel { get; set; }
    /// <summary>For a fork: the repository ("owner/name") the release came from.</summary>
    public string? Source { get; set; }
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

    /// <summary>
    /// Backups that are an OptiScaler the user had put in the folder before Hybrid Launcher installed one there (2.0.0-rc.17):
    /// unlike an older OptiScaler of Hybrid Launcher's own, they go back at uninstall, so the folder is as it was.
    /// </summary>
    public List<string> UserOptiBackups { get; set; } = [];

    /// <summary>ASI plugins copied from the universal folder: file name -> SHA-256.</summary>
    public Dictionary<string, string> AsiPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The fork's optional downloads put in with the build (their archive names), see <see cref="Opti.OptiAddon"/>.</summary>
    public List<string> Addons { get; set; } = [];

    /// <summary>
    /// Custom dlls copied from the shared folder into OptiScaler's custom\&lt;name&gt; (see <see cref="Opti.CustomDlls"/>):
    /// entry name -> (file relative to <see cref="TargetDir"/> -> SHA-256).
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> CustomDlls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A copy of OptiScaler.ini as the install left it, before Hybrid Launcher's saved choices (OptiScaler settings → Restore defaults).</summary>
    public string? DefaultIni { get; set; }

    /// <summary>DLSSNR-AMD laid over this install (<see cref="Opti.DlssNrAmd"/>), or null.</summary>
    public DlssNrAmdInstall? DlssNrAmd { get; set; }

    [JsonIgnore]
    public string DisplayVersion => Channel switch
    {
        OptiChannel.Nightly => L.F("Nightly {0}", Tag),
        OptiChannel.Custom => L.F("Custom build \"{0}\"", AssetName),
        OptiChannel.Fork => $"{Source} {Tag}",
        _ => Tag,
    };
}

public sealed class GameSettings
{
    public string? ExeOverride { get; set; }

    /// <summary>A cover picked by the user (a copy under the data folder's covers\), shown instead of the store's.</summary>
    public string? CustomCover { get; set; }
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
    /// This game's own choice for the stored DLSS NR model file (<see cref="Opti.DlssNr"/>): null follows the global switch
    /// (<see cref="AppSettings.UseDlssNr"/>), true or false is this game's exception to it.
    /// </summary>
    public bool? DlssNrChoice { get; set; }

    /// <summary>How DLSS 5 runs in this game (Setup DLSS 5): "f5" (janblade's fork), "amd" (DLSSNR-AMD), or null for none chosen.</summary>
    public string? Dlss5Method { get; set; }

    /// <summary>How DLSSNR-AMD goes into this game: "optiscaler" (default, a plugin of OptiScaler) or "reshade" (with ReShade, no OptiScaler).</summary>
    public string? Dlss5AmdWay { get; set; }

    /// <summary>The ReShade route picked for this game ("reshade-feeder", "reshade-feeder-dx9", "reshade-feeder-vulkan"); null = by its graphics API.</summary>
    public string? Dlss5AmdRoute { get; set; }

    /// <summary>DLSSNR-AMD put in through ReShade (no OptiScaler), or null.</summary>
    public DlssNrAmdReShadeInstall? DlssNrAmdReShade { get; set; }

    /// <summary>Settings from 1.3.0-rc.4/rc.5 had a per-game switch, on by default: a game turned off there stays off.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DlssNr { get => null; set { if (value == false) DlssNrChoice ??= false; } }

    /// <summary>
    /// Plugins from the shared ASI folder turned on for this game only (file names), copied into its OptiScaler install.
    /// Null until set: before 0.11 one switch served every game, and the plugins an install already holds carry over.
    /// </summary>
    public HashSet<string>? AsiPlugins { get; set; }

    /// <summary>Entries of the shared custom dll folder turned on for this game only (folder names), copied into its OptiScaler install.</summary>
    public HashSet<string> CustomDlls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Size of the install folder at the last measurement (shown under "Game folder" before the new one is done).</summary>
    public long? SizeBytes { get; set; }
    public DateTime? SizeMeasuredAt { get; set; }

    /// <summary>Time played and last session, counted by Hybrid Launcher for games it starts (Steam keeps its own, see <see cref="Playtime"/>).</summary>
    public long PlayMinutes { get; set; }
    public DateTime? LastPlayed { get; set; }

    /// <summary>Game page tabs hidden for this game only (the global ones are in <see cref="AppSettings.HiddenTabs"/>).</summary>
    public HashSet<string> HiddenTabs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How this game's page opens: "dashboard" or "tabs"; null follows <see cref="AppSettings.GameView"/>.</summary>
    public string? View { get; set; }

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

    /// <summary>DXVK, DXVK-gplasync or VKD3D-Proton installed by Hybrid Launcher in this game.</summary>
    public Mods.DxvkState? Dxvk { get; set; }

    /// <summary>Mod check findings the user dismissed for this game (their ids), until the folder changes again.</summary>
    public HashSet<string> DismissedModProblems { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Ultra+ mod Hybrid Launcher put in the game folder (with its add-ons), so it can be updated and removed exactly.</summary>
    public UltraPlusState? UltraPlus { get; set; }

    /// <summary>The Ultra+ game picked by hand for this game (its key), when the automatic match found none or the wrong one.</summary>
    public string? UltraPlusKey { get; set; }

    /// <summary>ReShade in this game folder, as Hybrid Launcher set it up (loaded through OptiScaler or not).</summary>
    public ReShadeState ReShade { get; set; } = new();

    /// <summary>RenoDX add-on files Hybrid Launcher put next to the game exe: file name -> where it came from.</summary>
    public Dictionary<string, string> RenoDxFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When Hybrid Launcher first listed this game (the library's "Date added" order).</summary>
    public DateTime? AddedAt { get; set; }
}

/// <summary>An OptiScaler fork the user added: a GitHub repository ("owner/name") that publishes releases.</summary>
public sealed class OptiFork
{
    public string Name { get; set; } = "";
    public string Repo { get; set; } = "";
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

/// <summary>
/// An Ultra+ mod installed by Hybrid Launcher: the Ultra+ game it was made for, the archive, and every file it added (relative to
/// <see cref="Root"/>, the game's install folder), so an update replaces exactly these and an uninstall leaves the folder as it was.
/// </summary>
public sealed class UltraPlusState
{
    /// <summary>The game's key in Ultra+'s list ("Stalker2").</summary>
    public string GameKey { get; set; } = "";
    /// <summary>The archive installed: "Stalker2 Ultra Plus v6.5.1.zip".</summary>
    public string File { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Beta { get; set; }
    /// <summary>When Ultra+ published that archive (newer ones are updates).</summary>
    public DateTime Published { get; set; }
    public DateTime InstalledAt { get; set; }
    /// <summary>The game's install folder the files are relative to.</summary>
    public string Root { get; set; } = "";
    /// <summary>"I manage my own UE4SS": only Ultra+'s own files went in, the game's UE4SS was left alone.</summary>
    public bool OwnUe4ss { get; set; }
    public List<string> Files { get; set; } = [];
    /// <summary>Files that were there before and were replaced: relative path -> backup path.</summary>
    public Dictionary<string, string> Backups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Add-ons installed on top: add-on name -> what went in.</summary>
    public Dictionary<string, UltraPlusAddonState> Addons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UltraPlusAddonState
{
    public string File { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime Published { get; set; }
    public DateTime InstalledAt { get; set; }
    /// <summary>Relative to the game's install folder, as <see cref="UltraPlusState.Files"/>.</summary>
    public List<string> Files { get; set; } = [];
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
    public bool Ubisoft { get; set; } = true;
    public bool Ea { get; set; } = true;
    public bool Rockstar { get; set; } = true;
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

    /// <summary>The dll Hybrid Launcher put in itself, without ReShade's installer (the Auto tab, 2.0.0-rc.7); null = none.</summary>
    public string? Placed { get; set; }

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
/// <summary>One pop-up look's own colours (#RRGGBB; empty = the look's own).</summary>
public sealed class PopupLookColours
{
    public string Background { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class PopupSettings
{
    /// <summary>"Native" (where the chosen look's original shows it), "BottomRight", "BottomLeft", "TopRight", "TopLeft",
    /// "TopCenter", "BottomCenter", or "Custom" (<see cref="X"/>, <see cref="Y"/>).</summary>
    public string Corner { get; set; } = "Native";

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

    /// <summary>
    /// How the pop-up is drawn: "Hybrid", "PlayStation", "PlayStation4", "PlayStation4Black", "Xbox", "Steam", "Gog", "Epic", "Minimal", or "Auto" (the look of the
    /// game's own store). <see cref="Style"/> and the background colours only apply to Hybrid and Minimal.
    /// </summary>
    public string Look { get; set; } = "Hybrid";

    /// <summary>
    /// The PlayStation looks' background: "Original" (the console's own: the PS5 plate's grey gradient, the PS4 bar white or
    /// black as the look says), "Light", "Dark", "Tier" (the trophy's metal) or "Colour" (the colour picked in Colour).
    /// </summary>
    public string PsBackground { get; set; } = "Original";

    /// <summary>
    /// 1.4: the PlayStation looks take the shared <see cref="Background"/> (any colour). A background chosen with the old list moves
    /// there once, when no background was picked; "Tier" (per trophy) has no single colour and goes back to the console's own.
    /// </summary>
    public void MovePsBackground(string accentHex)
    {
        if (PsBackground == "Original") return;
        if (Background.Length == 0)
            Background = PsBackground switch { "Light" => "#F4F3F5", "Dark" => "#1C1B1D", "Colour" => accentHex, _ => "" };
        PsBackground = "Original";
    }

    /// <summary>
    /// 1.4: each look's own background and text colours (look key as in <see cref="Look"/> → colours). A look with no entry, or
    /// an empty colour, keeps its own (the console's or store's, or the theme's tint for Hybrid and Minimal).
    /// </summary>
    public Dictionary<string, PopupLookColours> LookColours { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The colours of <paramref name="look"/> (a new, empty entry when it has none yet).</summary>
    public PopupLookColours ColoursOf(string look)
    {
        if (!LookColours.TryGetValue(look, out var c)) LookColours[look] = c = new PopupLookColours();
        return c;
    }

    /// <summary>The looks that took the one shared <see cref="Background"/> before 1.4.</summary>
    public static readonly string[] SharedBackgroundLooks = ["Hybrid", "Minimal", "PlayStation", "PlayStation4", "PlayStation4Black"];

    /// <summary>
    /// 1.4: the one shared <see cref="Background"/> becomes the background of each look that used it, so every look has its own
    /// from now on. Runs once (the shared value is emptied).
    /// </summary>
    public void MoveSharedBackground()
    {
        if (Background.Length == 0) return;
        foreach (var look in SharedBackgroundLooks)
            if (ColoursOf(look).Background.Length == 0) ColoursOf(look).Background = Background;
        Background = "";
    }

    /// <summary>"Accent" (colour theme), "Gold", "Steam" (blue), or "Custom" with <see cref="CustomColor"/>.</summary>
    public string Style { get; set; } = "Accent";

    /// <summary>#RRGGBB used when <see cref="Style"/> is "Custom".</summary>
    public string CustomColor { get; set; } = "#9A5CFF";

    /// <summary>
    /// "Windows" (the system notification sound), "File" (your own .wav files: <see cref="TierSounds"/> for a rarity, else
    /// <see cref="SoundFile"/>, else the system sound) or "None". "Look" is an older value (looks came with their own sounds,
    /// no longer shipped) and plays like "Windows".
    /// </summary>
    public string Sound { get; set; } = "Windows";

    /// <summary>How loud the pop-up's sound plays, 0..1 (1 = as recorded).</summary>
    public double Volume { get; set; } = 1;

    /// <summary>Your .wav for every achievement (when its rarity has none of its own).</summary>
    public string? SoundFile { get; set; }

    /// <summary>Your .wav per rarity tier: "Bronze", "Silver", "Gold" or "Platinum" → its path. A missing tier plays <see cref="SoundFile"/>.</summary>
    public Dictionary<string, string> TierSounds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Saved sets of your own sounds (Settings → Achievements → Sound preset): name → slot → .wav, the slots being "All" (every
    /// achievement) and the tiers. Picking one fills My sounds with it.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> SoundPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Show the share of players who have it (Steam's global stats).</summary>
    public bool ShowRarity { get; set; } = true;

    /// <summary>"Game" (the display the game runs on) or "Primary".</summary>
    public string Screen { get; set; } = "Game";
}

/// <summary>Settings → Controller: what each button does in the app, and the Home button that brings Hybrid Launcher to the front.</summary>
public sealed class ControllerSettings
{
    /// <summary>Action → button ("Press" → "A"…), only what differs from the defaults. Actions and buttons: see the app's PadActions.</summary>
    public Dictionary<string, string> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The controller's Home button (Xbox / Guide, PS) brings Hybrid Launcher to the front from a game or any app.</summary>
    public bool HomeButton { get; set; }

    /// <summary>"Press" or "Hold" (half a second, so a short press stays the console's or Game Bar's own).</summary>
    public string HomeTrigger { get; set; } = "Press";

    /// <summary>What opens: "Game" (the page of the game running, else as it was), "Library" or "Last" (as it was).</summary>
    public string HomeOpens { get; set; } = "Game";

    /// <summary>Pressed again while Hybrid Launcher is in front: back to the game (or app) it came from.</summary>
    public bool HomeGoesBack { get; set; } = true;

    /// <summary>Also read the PS button of PlayStation controllers connected directly (no Xbox emulation in between).</summary>
    public bool HomePlayStation { get; set; } = true;
}

/// <summary>How the library is ordered and filtered.</summary>
public sealed class LibraryFilter
{
    /// <summary>"name", "recent" (last played), "added", "playtime", "achievements" or "tech" (most upscaler / FG tags).</summary>
    public string Sort { get; set; } = "recent";
    public bool Descending { get; set; }

    /// <summary>Keep the launcher groups; off = one block of every game in the chosen order (the default since 1.4.0).</summary>
    public bool GroupByLauncher { get; set; }

    /// <summary>1 = the 1.4.0 defaults (last played, one block) applied once over older settings.</summary>
    public int DefaultsVersion { get; set; }

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

    /// <summary>
    /// Shading (1.4.0): cards and rows lit from above, a glow under the open tab, a sheen on the launch bar and covers lifted
    /// under the pointer. Off by default: flat surfaces as before.
    /// </summary>
    public bool Shading { get; set; }

    /// <summary>Interface size in percent (50-200), on top of Windows' own display scale. 100 = as Windows draws it.</summary>
    public int InterfaceSize { get; set; } = 100;

    /// <summary>Hide the vertical scroll bars everywhere (the wheel, touch, keys and the controller still scroll).</summary>
    public bool HideScrollBars { get; set; }

    /// <summary>Which one-time look changes were applied to these settings (2 = Material turned off once, 0.21).</summary>
    public int LookVersion { get; set; }

    /// <summary>The welcome tour was shown (finished or skipped); Settings can start it again.</summary>
    public bool TourSeen { get; set; }

    /// <summary>
    /// The version this window was last opened on (x.y.z). After an update it differs, and "Welcome to the new version" shows
    /// once; a first start sets it with the welcome tour instead.
    /// </summary>
    public string? LastVersionSeen { get; set; }

    /// <summary>The first-start feature picker was answered (see <see cref="Features"/>); Settings → Features opens it again.</summary>
    public bool FeaturesChosen { get; set; }

    /// <summary>Icons of the game page tabs: "mono" (one colour, the default), "color" (each its own colour) or "none".</summary>
    public string TabIcons { get; set; } = "color";

    /// <summary>Closing the window keeps Hybrid Launcher running in the notification area (games keep being tracked, Smart HDR keeps working).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Hardware acceleration: videos decoded on the GPU, blur and reflection effects, animated pictures. Off = the lightest
    /// drawing (no effects, videos still), for weak or busy GPUs. Applied at once.
    /// </summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>Hardware acceleration is paused while a game started from Hybrid Launcher runs, and resumes when it closes.</summary>
    public bool PauseEffectsInGame { get; set; } = true;

    /// <summary>
    /// Background saver: while the window is open but another app is in front, the moving clips and muted trailers pause, the
    /// polling stops and Windows runs Hybrid Launcher in efficiency mode; all of it comes back the moment it is in front again.
    /// </summary>
    public bool BackgroundSaver { get; set; } = true;

    /// <summary>
    /// How much the cache of the game pages (covers, banners, store, wiki and review data) may take on disk, in GB (Settings →
    /// Storage). Pages open from it at once, also offline; past it the pictures used least recently go first.
    /// </summary>
    public int CacheBudgetGb { get; set; } = 2;

    /// <summary>The game list's width, dragged by its edge (240 to 480, and at most 45 % of the window).</summary>
    public double SidebarWidth { get; set; } = 312;

    /// <summary>OptiScaler forks added by the user (the known ones are in <see cref="Opti.OptiForks"/>).</summary>
    public List<OptiFork> OptiForks { get; set; } = [];

    /// <summary>OptiScaler sources whose new releases raise a notification (see <see cref="Opti.OptiUpdates"/>: "stable", "nightly", "fork:owner/name").</summary>
    public List<string> OptiFollow { get; set; } = [];

    /// <summary>The newest release already seen per followed source (its tag): a different one notifies.</summary>
    public Dictionary<string, string> OptiSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Put the stored DLSS NR model file (<see cref="Opti.DlssNr"/>) in every OptiScaler install that needs it.</summary>
    public bool UseDlssNr { get; set; }

    /// <summary>
    /// What runs the DLSS 5 model in a DLSS NR build: "auto" (NVIDIA's NGX on an RTX card, the vendor-neutral port otherwise),
    /// "ngx" or "port" (janblade's vendor-neutral forwarder: DirectX 12 compute, any GPU). See <see cref="Opti.DlssNr"/>.
    /// </summary>
    public string DlssNrBackend { get; set; } = "auto";

    /// <summary>Put the newer F5Low a fork publishes beside its build (janblade's Optional-F5Low-Update) in with it, on RTX cards.</summary>
    public bool F5LowUpdate { get; set; } = true;

    /// <summary>The fork ("owner/name") the Fork channel of the OptiScaler tab shows.</summary>
    public string? SelectedFork { get; set; }

    /// <summary>Start Hybrid Launcher with Windows (in the notification area, without a window).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>A game started from Hybrid Launcher that is up and running sends the window to the notification area; it comes back when the game closes.</summary>
    public bool HideWhilePlaying { get; set; } = true;

    /// <summary>
    /// A controller steers Hybrid Launcher's window (focus, A to press, B back...). Off for people who move the mouse with a
    /// controller through another tool (JoyToKey, JoyXoff, Steam Input's desktop layout): both would act on the same press.
    /// </summary>
    public bool GamepadInput { get; set; } = true;

    /// <summary>Settings → Controller: the buttons' actions and the Home button.</summary>
    public ControllerSettings Controller { get; set; } = new();

    /// <summary>A start with Windows opens the window too (off, the default: only the icon in the notification area).</summary>
    public bool StartOpensWindow { get; set; }

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

    /// <summary>
    /// The game pages' warm-up went through the whole library once (2.0): until then it starts right after the first window
    /// and runs wider, so the first visit of every game opens from the cache (Helpers.WarmUp).
    /// </summary>
    public bool CacheWarmed { get; set; }

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

    /// <summary>
    /// How a game page opens (1.4.0): "tabs" (the classic page with every tab, the default) or "dashboard" (a summary of the
    /// game's news, achievements, sessions and mods). Each game can keep its own choice (<see cref="GameSettings.View"/>).
    /// </summary>
    public string GameView { get; set; } = "tabs";

    /// <summary>Order of the Dashboard's cards as the user arranged them; cards missing here keep their default place.</summary>
    public List<string> DashboardOrder { get; set; } = [];

    /// <summary>Dashboard cards the user hid ("news", "activity", "play", "achievements", "mods", "updates", "time").</summary>
    public HashSet<string> DashboardHidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Library covers show their tags at all (play time, mods, upscalers, achievements). Off: the bare cover art.</summary>
    public bool ShowCoverOverlays { get; set; } = true;

    /// <summary>Single cover tags turned off (keys in <see cref="CoverTagKinds"/>); the upscaler tags keep <see cref="ShowCoverTags"/>.</summary>
    public HashSet<string> HiddenCoverTags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every kind of tag a library cover can show, in the order of Settings → Library and the library's Filters.</summary>
    public static readonly (string Key, string Label)[] CoverTagKinds =
    [
        ("pin", "Pinned"), ("time", "Play time"), ("opti", "OptiScaler"), ("rs", "ReShade"), ("rdx", "RenoDX"), ("uplus", "Ultra+"), ("dxvk", "DXVK"),
        ("tech", "Upscalers (DLSS, FSR, XeSS)"), ("ach", "Achievements"),
    ];

    /// <summary>Whether a cover shows the tag <paramref name="key"/> (all of them off with <see cref="ShowCoverOverlays"/>).</summary>
    public bool CoverTag(string key) => ShowCoverOverlays && (key == "tech" ? ShowCoverTags : !HiddenCoverTags.Contains(key));

    public void SetCoverTag(string key, bool on)
    {
        if (key == "tech") ShowCoverTags = on;
        else if (on) HiddenCoverTags.Remove(key);
        else HiddenCoverTags.Add(key);
    }

    public HashSet<string> HiddenGames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GameSettings> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names the user gave to games (game id → shown name, see <see cref="Game.Title"/>); the game keeps its own for lookups.</summary>
    public Dictionary<string, string> GameNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Settings → Achievements → Sources: more folders with local achievement files (a folder per app id, or one game's).</summary>
    public List<string> LocalAchievementFolders { get; set; } = [];

    /// <summary>Settings → Achievements → Sources: Xbox games' achievements, read from the Xbox app's cache on this PC.</summary>
    public bool ReadXboxAchievements { get; set; } = true;

    /// <summary>Settings → Achievements → Sources: Steam games' achievements, from Steam's stats files on this PC.</summary>
    public bool ReadSteamAchievements { get; set; } = true;

    /// <summary>Settings → Achievements → Sources: GOG games' achievements, from the signed-in GOG account.</summary>
    public bool ReadGogAchievements { get; set; } = true;

    /// <summary>Settings → Achievements → Sources: with a Microsoft account signed in, Xbox unlocks pop up while the game runs.</summary>
    public bool XboxLivePopups { get; set; } = true;

    /// <summary>Settings → Achievements → On unlock: a screenshot of the game's screen with each unlock.</summary>
    public bool AchScreenshot { get; set; }

    /// <summary>The screenshot is taken with the pop-up on screen (false: just before it shows).</summary>
    public bool AchScreenshotWithPopup { get; set; } = true;

    /// <summary>Where the screenshots go; empty = Pictures\Hybrid Launcher\Achievements.</summary>
    public string AchScreenshotDir { get; set; } = "";

    /// <summary>Settings → Achievements → On unlock: a pop-up when an achievement with steps moves on (local files and Xbox give them).</summary>
    /// Off by default and saved under a new name in 2.0.0-rc.10, so a true written by an older build does not carry over.
    public bool AchProgressPopupsOn { get; set; }

    /// <summary>Progress pop-ups every N percent of the way (10, 25 or 50).</summary>
    public int AchProgressStep { get; set; } = 25;

    /// <summary>Settings → Achievements → On unlock: a short rumble on a connected controller.</summary>
    public bool AchRumble { get; set; }

    /// <summary>Settings → Achievements → On unlock: every unlock is written to the unlock history (kept by Hybrid Launcher).</summary>
    public bool AchHistory { get; set; } = true;

    /// <summary>Ultra+: look for new builds of the installed mods when Hybrid Launcher starts, then every few hours (the Ultra+ page's badge).</summary>
    public bool UltraPlusAutoCheck { get; set; } = true;

    /// <summary>Ultra+: a newer build found by the background check goes in by itself (settings kept), not only counted.</summary>
    public bool UltraPlusAutoUpdate { get; set; } = true;

    /// <summary>Ultra+: a Windows notification when a newer build of an installed Ultra+ mod comes out.</summary>
    public bool UltraPlusNotify { get; set; }

    /// <summary>The bell in the title bar (Settings → Notifications).</summary>
    public bool ShowBell { get; set; } = true;

    /// <summary>Days the bell keeps a notification (0 = until cleared).</summary>
    public int InboxKeepDays { get; set; } = 30;

    /// <summary>Kinds ("opti", "ultra", "app") the bell does not keep.</summary>
    public HashSet<string> InboxOff { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kinds ("opti", "ultra", "app") that get no Windows pop-up (still kept in the list unless off there too).</summary>
    public HashSet<string> PopupOff { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Hybrid Launcher version a Windows pop-up was last shown for: one pop-up per new version.</summary>
    public string? AppUpdatePopupShown { get; set; }

    /// <summary>No Windows pop-ups while a game runs: they wait in the bell.</summary>
    public bool QuietWhilePlaying { get; set; } = true;

    /// <summary>Ultra+: the newest build already notified per Ultra+ game key (its archive name), so each build notifies once.</summary>
    public Dictionary<string, string> UltraPlusSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ultra+: offer beta builds too (newer, less tested), not only the stable ones.</summary>
    public bool UltraPlusBetas { get; set; }

    /// <summary>Ultra+: the user's own saved presets, by Ultra+ game key, then preset name -> setting -> value.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> UltraPlusPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ScanSteam { get; set; } = true;
    public bool ScanEpic { get; set; } = true;
    public bool ScanGog { get; set; } = true;
    public bool ScanXbox { get; set; } = true;
    public bool ScanUbisoft { get; set; } = true;
    public bool ScanEa { get; set; } = true;
    public bool ScanRockstar { get; set; } = true;

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
        (SubTabs, "Sub-tabs", "The icons of the sections under a tab (OptiScaler, Mods...)"),
        (Headings, "Card headings", "The icons beside the headings of the cards inside the tabs"),
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
        SaveFolder = "savefolder", ConfigFolder = "configfolder", Achievements = "achievements", ModsPath = "modspath",
        InstalledMods = "installedmods", DlssNr = "dlssnr";

    public static readonly (string Key, string Label)[] All =
    [
        (Links, "Links on the banner"),
        (InstalledMods, "Installed mods and their versions (top left of the banner)"),
        (Tags, "Tags on the banner (store, graphics API, upscalers, developer and date)"),
        (PlayStats, "Last played and play time"),
        (Hdr, "Smart HDR"),
        (Cloud, "Cloud saves"),
        (GameFolder, "Game folder"),
        (SaveFolder, "Saves folder"),
        (ConfigFolder, "Configuration folder"),
        (Achievements, "Achievements button"),
        (ModsPath, "Where mods go (the line under the bar)"),
        (DlssNr, "DLSS NR model file picker (OptiScaler tab, forks and custom builds)"),
    ];

    public static bool HiddenEverywhere(AppSettings settings, string part) => settings.HiddenPageParts.Contains(part);

    public static bool Hidden(AppSettings settings, GameSettings? game, string part) =>
        HiddenEverywhere(settings, part) || game?.HiddenPageParts.Contains(part) == true;
}
