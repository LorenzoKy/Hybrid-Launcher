using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace HybridLauncher.Core.Detection;

/// <summary>
/// One entry of Windows' list of installed programs (Apps → Installed apps): the launchers that keep no readable game list of
/// their own (EA app, Rockstar Games Launcher) are found through it, and every one of them gives the command that uninstalls.
/// </summary>
public sealed record UninstallEntry(string Key, string? DisplayName, string? InstallLocation, string? UninstallString, string? Publisher);

public static class UninstallEntries
{
    static readonly (RegistryHive Hive, RegistryView View)[] Places =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    /// <summary>Every entry, from the 64-bit and 32-bit machine lists and the user's own (keys seen twice are read once).</summary>
    public static List<UninstallEntry> Read()
    {
        var list = new List<UninstallEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in Places)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    if (!seen.Add(hive + "\\" + name)) continue;
                    try
                    {
                        using var k = key.OpenSubKey(name);
                        if (k is null) continue;
                        list.Add(new UninstallEntry(name, k.GetValue("DisplayName") as string, k.GetValue("InstallLocation") as string,
                            k.GetValue("UninstallString") as string, k.GetValue("Publisher") as string));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Warn($"Installed programs list ({hive}, {view}): {ex.Message}"); }
        }
        return list;
    }

    /// <summary>The entry whose install folder is <paramref name="dir"/>, if any.</summary>
    public static UninstallEntry? ForFolder(IEnumerable<UninstallEntry> entries, string dir)
    {
        var target = Trim(dir);
        return entries.FirstOrDefault(e => e.InstallLocation is { Length: > 0 } l && Trim(l).Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    internal static string Trim(string dir)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Trim().Trim('"').Replace('/', '\\'))); }
        catch { return dir; }
    }
}

/// <summary>
/// Ubisoft Connect: every game it installed has HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\&lt;id&gt; with its folder
/// (InstallDir), and an "Uplay Install &lt;id&gt;" entry in the installed programs with its name. Games are started with
/// uplay://launch/&lt;id&gt;/0, as the launcher's own shortcuts do. Ubisoft games bought on Steam or Epic are listed there too:
/// those are found by Steam's and Epic's scans first (same folder), so they keep their own launcher.
/// </summary>
public static class UbisoftScanner
{
    public sealed record Install(string Id, string Dir);

    public static IEnumerable<Game> Scan()
    {
        var installs = new List<Install>();
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
            foreach (var id in key?.GetSubKeyNames() ?? [])
            {
                using var k = key!.OpenSubKey(id);
                if (k?.GetValue("InstallDir") is string dir && dir.Length > 0) installs.Add(new Install(id, dir));
            }
        }
        catch (Exception ex) { Log.Warn($"Ubisoft Connect installs: {ex.Message}"); }
        if (installs.Count == 0) return [];
        return From(installs, UninstallEntries.Read());
    }

    /// <summary>The games of the launcher's install records (the registry's, or a test's), named from the installed programs.</summary>
    public static List<Game> From(IEnumerable<Install> installs, IReadOnlyList<UninstallEntry> programs)
    {
        var games = new List<Game>();
        foreach (var i in installs)
        {
            try
            {
                var dir = UninstallEntries.Trim(i.Dir);
                if (!Directory.Exists(dir)) continue;
                var entry = programs.FirstOrDefault(p => p.Key.Equals("Uplay Install " + i.Id, StringComparison.OrdinalIgnoreCase))
                            ?? UninstallEntries.ForFolder(programs, dir);
                games.Add(new Game
                {
                    Id = $"ubisoft:{i.Id}",
                    Name = Clean(entry?.DisplayName) ?? Path.GetFileName(dir),
                    Store = GameStore.Ubisoft,
                    InstallDir = dir,
                    LaunchUri = $"uplay://launch/{i.Id}/0",
                    UninstallCommand = entry?.UninstallString,
                });
            }
            catch (Exception ex) { Log.Error($"Ubisoft game {i.Id} failed", ex); }
        }
        return games;
    }

    static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}

/// <summary>
/// EA app: it keeps its own game list encrypted, so its games are found the way Windows lists them. Each game the EA app
/// installs gets an installed-programs entry whose uninstaller is EA's (…\EAInstaller\&lt;game&gt;\Cleanup.exe), and a
/// __Installer\installerdata.xml in its folder that names the exe to start. Started from that exe: EA games open the EA app
/// themselves for the sign-in, as from their desktop shortcut.
/// </summary>
public static partial class EaScanner
{
    public static IEnumerable<Game> Scan() => From(UninstallEntries.Read());

    public static bool IsEaEntry(UninstallEntry e) =>
        e.UninstallString is { } u && (u.Contains(@"\EAInstaller\", StringComparison.OrdinalIgnoreCase) || u.Contains("EADesktop", StringComparison.OrdinalIgnoreCase) && u.Contains("uninstall_game", StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(e.InstallLocation);

    public static List<Game> From(IEnumerable<UninstallEntry> programs)
    {
        var games = new List<Game>();
        foreach (var e in programs.Where(IsEaEntry))
        {
            try
            {
                var dir = UninstallEntries.Trim(e.InstallLocation!);
                if (!Directory.Exists(dir)) continue;
                var data = ReadInstallerData(dir);
                games.Add(new Game
                {
                    Id = $"ea:{data.ContentId ?? e.Key}",
                    Name = !string.IsNullOrWhiteSpace(e.DisplayName) ? e.DisplayName.Trim() : data.Title ?? Path.GetFileName(dir),
                    Store = GameStore.Ea,
                    InstallDir = dir,
                    StoreExe = data.Exe,
                    UninstallCommand = e.UninstallString,
                });
            }
            catch (Exception ex) { Log.Error($"EA game {e.Key} failed", ex); }
        }
        return games;
    }

    public sealed record InstallerData(string? ContentId, string? Title, string? Exe);

    /// <summary>
    /// __Installer\installerdata.xml: the first content id, the English title and the exe of the first launcher that is not a
    /// trial one. The exe path starts with a registry value in brackets ("[HKEY_…\Install Dir]bin\game.exe") that is the game's
    /// folder: the rest is taken relative to it.
    /// </summary>
    public static InstallerData ReadInstallerData(string dir)
    {
        var file = Path.Combine(dir, "__Installer", "installerdata.xml");
        if (!File.Exists(file)) return new InstallerData(null, null, null);
        try
        {
            var root = XDocument.Load(file).Root;
            if (root is null) return new InstallerData(null, null, null);
            string? Local(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
            var contentId = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "contentID")?.Value.Trim();
            var titles = root.Descendants().Where(x => x.Name.LocalName == "gameTitle").ToList();
            var title = (titles.FirstOrDefault(t => (string?)t.Attribute("locale") is "en_US" or "en_GB") ?? titles.FirstOrDefault())?.Value.Trim();
            string? exe = null;
            foreach (var launcher in root.Descendants().Where(x => x.Name.LocalName == "launcher"))
            {
                if (Local(launcher, "trial") is "1" or "true") continue;
                var path = Local(launcher, "filePath");
                if (string.IsNullOrWhiteSpace(path)) continue;
                path = Bracket().Replace(path, "").Replace('/', '\\').TrimStart('\\');
                var full = Path.GetFullPath(Path.Combine(dir, path));
                if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) { exe = full; break; }
            }
            return new InstallerData(string.IsNullOrWhiteSpace(contentId) ? null : contentId, string.IsNullOrWhiteSpace(title) ? null : title, exe);
        }
        catch (Exception ex)
        {
            Log.Warn($"EA installerdata.xml in {dir}: {ex.Message}");
            return new InstallerData(null, null, null);
        }
    }

    [GeneratedRegex(@"^\[[^\]]*\]")]
    private static partial Regex Bracket();
}

/// <summary>
/// Rockstar Games Launcher: each game it installed has HKLM\SOFTWARE\WOW6432Node\Rockstar Games\&lt;game&gt; with InstallFolder
/// (copies from Steam or Epic write InstallFolderSteam / InstallFolderEpic instead and are left to those scans). The name comes
/// from the game's installed-programs entry ("…\Launcher\uninstall.exe -uninstall=&lt;title&gt;"), and games are started by the
/// launcher with -launchTitleInFolder "&lt;folder&gt;", as its own shortcuts do.
/// </summary>
public static partial class RockstarScanner
{
    public sealed record Install(string Key, string Dir);

    static readonly string[] NotGames = ["Launcher", "Rockstar Games Social Club", "Social Club", "Steam", "Epic"];

    public static string? LauncherExe()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Rockstar Games\Launcher");
            var dir = key?.GetValue("InstallFolder") as string;
            var exe = dir is null ? null : Path.Combine(dir, "Launcher.exe");
            return exe is not null && File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    public static IEnumerable<Game> Scan()
    {
        var installs = new List<Install>();
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Rockstar Games");
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                if (NotGames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                using var k = key!.OpenSubKey(name);
                if (k?.GetValue("InstallFolder") is string dir && dir.Length > 0) installs.Add(new Install(name, dir));
            }
        }
        catch (Exception ex) { Log.Warn($"Rockstar Games Launcher installs: {ex.Message}"); }
        if (installs.Count == 0) return [];
        return From(installs, UninstallEntries.Read(), LauncherExe());
    }

    public static List<Game> From(IEnumerable<Install> installs, IReadOnlyList<UninstallEntry> programs, string? launcher)
    {
        var games = new List<Game>();
        foreach (var i in installs)
        {
            try
            {
                var dir = UninstallEntries.Trim(i.Dir);
                if (!Directory.Exists(dir)) continue;
                var entry = UninstallEntries.ForFolder(programs.Where(p => p.UninstallString?.Contains("-uninstall=", StringComparison.OrdinalIgnoreCase) == true).ToList(), dir);
                var title = entry?.UninstallString is { } u && TitleId().Match(u) is { Success: true } m ? m.Groups[1].Value : null;
                games.Add(new Game
                {
                    Id = $"rockstar:{(title ?? i.Key).ToLowerInvariant()}",
                    Name = !string.IsNullOrWhiteSpace(entry?.DisplayName) ? entry!.DisplayName!.Trim() : i.Key,
                    Store = GameStore.Rockstar,
                    InstallDir = dir,
                    LauncherCommand = launcher is null ? null : new LauncherCommand(launcher, $"-launchTitleInFolder \"{dir}\""),
                    UninstallCommand = entry?.UninstallString,
                });
            }
            catch (Exception ex) { Log.Error($"Rockstar game {i.Key} failed", ex); }
        }
        return games;
    }

    [GeneratedRegex(@"-uninstall=([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TitleId();
}
