using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace HybridLauncher.Core.Detection;

public static partial class SteamScanner
{
    static readonly HashSet<string> ToolAppIds = ["228980", "1070560", "1391110", "1628350", "1493710", "250820", "1826330", "2180100", "961940", "1054830", "1113280", "1245040", "1420170", "1580130", "1887720", "2348590", "2805730"];

    [GeneratedRegex(@"^(Steam Linux Runtime|Proton|Steamworks Common|SteamVR)|Redistributable", RegexOptions.IgnoreCase)]
    private static partial Regex ToolName();

    public static string? SteamRoot()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return path is null ? null : Path.GetFullPath(path.Replace('/', '\\'));
    }

    /// <summary>
    /// The small icon Steam keeps for a game in its library cache (appcache\librarycache\&lt;id&gt;\&lt;hash&gt;.jpg, or the older
    /// &lt;id&gt;_icon.jpg). It is there as soon as the game is in the library, before any file is downloaded.
    /// </summary>
    public static string? ClientIcon(string appId)
    {
        var root = SteamRoot();
        if (root is null) return null;
        try
        {
            var cache = Path.Combine(root, "appcache", "librarycache");
            var old = Path.Combine(cache, $"{appId}_icon.jpg");
            if (File.Exists(old)) return old;
            var dir = Path.Combine(cache, appId);
            // The icon is the one picture at the top of the game's folder (the art sits in sub-folders).
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg").Select(f => new FileInfo(f)).Where(f => f.Length < 64 * 1024).OrderBy(f => f.Length).Select(f => f.FullName).FirstOrDefault() : null;
        }
        catch { return null; }
    }

    public static string? SteamExe()
    {
        var root = SteamRoot();
        var exe = root is null ? null : Path.Combine(root, "steam.exe");
        return exe is not null && File.Exists(exe) ? exe : null;
    }

    /// <summary>The Steam client is open (it reads the app manifests only when it starts).</summary>
    public static bool ClientRunning()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("steam");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>The running Steam client counts the game as installed (it keeps HKCU\…\Steam\Apps\&lt;id&gt;\Installed).</summary>
    public static bool ClientKnows(string appId) =>
        Registry.GetValue($@"HKEY_CURRENT_USER\Software\Valve\Steam\Apps\{appId}", "Installed", 0) is int i && i == 1;

    /// <summary>Steam is busy: a game of its own is running or it is updating one, so it should not be restarted now.</summary>
    public static bool ClientBusy()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0) is int run && run != 0) return true;
        try
        {
            using var apps = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps");
            foreach (var id in apps?.GetSubKeyNames() ?? [])
            {
                using var k = apps!.OpenSubKey(id);
                if (k?.GetValue("Updating") is int u && u != 0 || k?.GetValue("Running") is int r && r != 0) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Closes the Steam client and opens it again with <paramref name="args"/>, so it reads the app manifests Hybrid Launcher wrote
    /// while it was open (a game downloaded by Hybrid Launcher is unknown to a Steam that was already running).
    /// </summary>
    public static async Task RestartClientAsync(string args)
    {
        var exe = SteamExe();
        if (exe is null) return;
        Log.Info($"Restarting Steam so it sees new installs ({args})");
        if (ClientRunning())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })?.Dispose();
            for (int i = 0; i < 120 && ClientRunning(); i++) await Task.Delay(250);
            await Task.Delay(1500);
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
    }

    /// <summary>Every Steam library folder on this PC (the install folder first), from libraryfolders.vdf.</summary>
    public static List<string> Libraries()
    {
        var root = SteamRoot();
        if (root is null || !Directory.Exists(root)) return [];
        var libraries = new List<string> { root };
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf) && VdfNode.Parse(File.ReadAllText(vdf)).Child("libraryfolders") is { } folders)
                foreach (var lib in folders.Children.Values)
                    if (lib["path"] is { } p) libraries.Add(Path.GetFullPath(p));
        }
        catch (Exception ex) { Log.Warn($"libraryfolders.vdf: {ex.Message}"); }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<Game> Scan()
    {
        var root = SteamRoot();
        if (root is null || !Directory.Exists(root)) yield break;

        var libraries = new List<string> { root };
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            VdfNode? folders = null;
            try { folders = VdfNode.Parse(File.ReadAllText(vdf)).Child("libraryfolders"); }
            catch (Exception ex) { Log.Error("libraryfolders.vdf parse failed", ex); }
            if (folders is not null)
            {
                foreach (var lib in folders.Children.Values)
                    if (lib["path"] is { } p) libraries.Add(Path.GetFullPath(p));
            }
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            string[] manifests;
            try { manifests = Directory.GetFiles(apps, "appmanifest_*.acf"); } catch { continue; }

            foreach (var acf in manifests)
            {
                Game? game = null;
                try
                {
                    var state = VdfNode.Parse(File.ReadAllText(acf)).Child("AppState");
                    var appId = state?["appid"];
                    var name = state?["name"];
                    var installDir = state?["installdir"];
                    if (appId is null || name is null || installDir is null) continue;
                    if (ToolAppIds.Contains(appId) || ToolName().IsMatch(name)) continue;

                    var dir = Path.Combine(apps, "common", installDir);
                    if (!Directory.Exists(dir)) continue;

                    game = new Game
                    {
                        Id = $"steam:{appId}",
                        Name = name,
                        Store = GameStore.Steam,
                        InstallDir = dir,
                        SteamAppId = appId,
                        // StateFlags bit 4 = fully installed; without it Steam is still downloading the game.
                        Installing = int.TryParse(state?["StateFlags"], out var flags) && (flags & 4) == 0,
                    };
                }
                catch (Exception ex)
                {
                    Log.Error($"Steam manifest {acf} failed", ex);
                }
                if (game is not null) yield return game;
            }
        }
    }
}

public static class EpicScanner
{
    /// <summary>
    /// The folders that hold this launcher's installed games (the parent of each game folder, most used first): the install
    /// window offers them as places, as it offers Steam's libraries.
    /// </summary>
    public static List<string> Libraries() => LibrariesOf(Scan());

    internal static List<string> LibrariesOf(IEnumerable<Game> games)
    {
        try
        {
            return games.Select(g => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(g.InstallDir)))
                .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        }
        catch (Exception ex) { Log.Warn($"Launcher libraries: {ex.Message}"); return []; }
    }

    public static IEnumerable<Game> Scan()
    {
        var dataPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath", null) as string;
        var manifestsDir = dataPath is not null
            ? Path.Combine(dataPath, "Manifests")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(manifestsDir)) yield break;

        string[] items;
        try { items = Directory.GetFiles(manifestsDir, "*.item"); } catch { yield break; }

        foreach (var item in items)
        {
            Game? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                var appName = S("AppName");
                var mainApp = S("MainGameAppName");
                if (appName is null || (mainApp is not null && !mainApp.Equals(appName, StringComparison.OrdinalIgnoreCase))) continue; // DLC

                if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    && !cats.EnumerateArray().Any(c => c.GetString() is "games")) continue;

                var dir = S("InstallLocation");
                if (dir is null || !Directory.Exists(dir)) continue;

                var exe = S("LaunchExecutable");
                var ns = S("CatalogNamespace");
                var catalogId = S("CatalogItemId");

                game = new Game
                {
                    Id = $"epic:{appName}",
                    Name = S("DisplayName") ?? appName,
                    Store = GameStore.Epic,
                    InstallDir = dir,
                    StoreExe = string.IsNullOrEmpty(exe) ? null : Path.GetFullPath(Path.Combine(dir, exe)),
                    EpicLaunchUri = ns is null || catalogId is null ? null
                        : $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(catalogId)}%3A{Uri.EscapeDataString(appName)}?action=launch&silent=true",
                };
            }
            catch (Exception ex)
            {
                Log.Error($"Epic manifest {item} failed", ex);
            }
            if (game is not null) yield return game;
        }
    }
}

public static class GogScanner
{
    /// <summary>The folders that hold GOG Galaxy's installed games, most used first (see <see cref="EpicScanner.Libraries"/>).</summary>
    public static List<string> Libraries() => EpicScanner.LibrariesOf(Scan());

    public static IEnumerable<Game> Scan()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (key is null) yield break;

        foreach (var id in key.GetSubKeyNames())
        {
            Game? game = null;
            try
            {
                using var g = key.OpenSubKey(id);
                if (g is null) continue;
                if (g.GetValue("dependsOn") is string dep && dep.Length > 0) continue; // DLC
                var dir = g.GetValue("path") as string;
                if (dir is null || !Directory.Exists(dir)) continue;
                var exe = g.GetValue("exe") as string;

                game = new Game
                {
                    Id = $"gog:{g.GetValue("gameID") as string ?? id}",
                    Name = g.GetValue("gameName") as string ?? Path.GetFileName(dir),
                    Store = GameStore.Gog,
                    InstallDir = dir,
                    StoreExe = exe is not null && File.Exists(exe) ? exe : null,
                };
            }
            catch (Exception ex)
            {
                Log.Error($"GOG entry {id} failed", ex);
            }
            if (game is not null) yield return game;
        }
    }
}

public static class XboxScanner
{
    public static IEnumerable<Game> Scan()
    {
        foreach (var root in GamingRoots())
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { continue; }

            foreach (var dir in dirs)
            {
                Game? game = null;
                try { game = ReadGame(dir); }
                catch (Exception ex) { Log.Error($"Xbox game {dir} failed", ex); }
                if (game is not null) yield return game;
            }
        }
    }

    /// <summary>Each drive may hold a .GamingRoot file: "RGBX", uint32 count, then UTF-16 zero-terminated folder paths.</summary>
    static IEnumerable<string> GamingRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            string file = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
            byte[] bytes;
            try
            {
                if (!drive.IsReady || !File.Exists(file)) continue;
                bytes = File.ReadAllBytes(file);
            }
            catch { continue; }
            if (bytes.Length < 10 || Encoding.ASCII.GetString(bytes, 0, 4) != "RGBX") continue;

            var text = Encoding.Unicode.GetString(bytes, 8, (bytes.Length - 8) / 2 * 2);
            foreach (var part in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Path.IsPathRooted(part) ? part : Path.Combine(drive.RootDirectory.FullName, part.TrimStart('\\'));
                if (Directory.Exists(path)) yield return path;
            }
        }
    }

    static Game? ReadGame(string dir)
    {
        var content = Path.Combine(dir, "Content");
        var config = Path.Combine(content, "MicrosoftGame.config");
        var appx = Path.Combine(content, "appxmanifest.xml");
        if (!File.Exists(config) && !File.Exists(appx)) return null;

        string name = Path.GetFileName(dir);
        string? exe = null, identityName = null, publisher = null, appId = null;
        var logos = new List<string>();

        if (File.Exists(config))
        {
            var x = XDocument.Load(config).Root;
            var visuals = x?.Element("ShellVisuals");
            foreach (var attr in new[] { "Square44x44Logo", "StoreLogo", "Square150x150Logo" })
                if (visuals?.Attribute(attr)?.Value is { Length: > 0 } logo) logos.Add(logo);
            var display = visuals?.Attribute("DefaultDisplayName")?.Value;
            if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) name = display;
            var exeName = x?.Element("ExecutableList")?.Element("Executable")?.Attribute("Name")?.Value;
            if (exeName is not null) exe = Path.Combine(content, exeName);
            identityName = x?.Element("Identity")?.Attribute("Name")?.Value;
            publisher = x?.Element("Identity")?.Attribute("Publisher")?.Value;
        }
        if (File.Exists(appx))
        {
            var x = XDocument.Load(appx).Root;
            if (x is not null)
            {
                XNamespace ns = x.Name.Namespace;
                var identity = x.Element(ns + "Identity");
                identityName ??= identity?.Attribute("Name")?.Value;
                publisher ??= identity?.Attribute("Publisher")?.Value;
                var application = x.Element(ns + "Applications")?.Element(ns + "Application");
                appId = application?.Attribute("Id")?.Value;
                // uap:VisualElements carries the tile logos; its namespace differs between manifest versions.
                var visual = application?.Elements().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
                foreach (var attr in new[] { "Square44x44Logo", "Square150x150Logo" })
                    if (visual?.Attribute(attr)?.Value is { Length: > 0 } logo) logos.Add(logo);
                if (x.Element(ns + "Properties")?.Element(ns + "Logo")?.Value is { Length: > 0 } storeLogo) logos.Add(storeLogo);
            }
        }

        string? aumid = identityName is not null && publisher is not null && appId is not null
            ? $"{identityName}_{PublisherId(publisher)}!{appId}"
            : null;

        return new Game
        {
            Id = $"xbox:{identityName ?? Path.GetFileName(dir)}",
            Name = name,
            Store = GameStore.Xbox,
            InstallDir = content,
            StoreExe = exe is not null && File.Exists(exe) ? exe : null,
            XboxAumid = aumid,
            IconPath = logos.Select(l => ResolveLogo(content, l)).FirstOrDefault(p => p is not null),
        };
    }

    /// <summary>
    /// A package logo on disk. Manifests name the plain file ("SmallLogo.png"), but packages often ship only
    /// qualified variants ("SmallLogo.targetsize-48_altform-unplated.png", "SmallLogo.scale-200.png"): pick the best one.
    /// </summary>
    public static string? ResolveLogo(string contentDir, string relative)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(contentDir, relative.Replace('/', '\\')));
            if (!path.StartsWith(Path.GetFullPath(contentDir), StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) return null;
            var stem = Path.GetFileNameWithoutExtension(path);
            return Directory.GetFiles(dir, stem + ".*" + Path.GetExtension(path))
                .Where(f => !Path.GetFileName(f).Contains("contrast-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => LogoScore(Path.GetFileName(f)))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    // Unplated (transparent) first, then the size closest to a 32-48 px list icon.
    static int LogoScore(string file)
    {
        int score = 0;
        if (file.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase)) score += 1000;
        if (file.Contains("altform-lightunplated", StringComparison.OrdinalIgnoreCase)) score -= 500;
        var m = Regex.Match(file, @"(targetsize|scale)-(\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            int n = int.Parse(m.Groups[2].Value);
            int px = m.Groups[1].Value.Equals("scale", StringComparison.OrdinalIgnoreCase) ? 44 * n / 100 : n;
            score += 500 - Math.Abs(px - 48);
        }
        return score;
    }

    /// <summary>The 13-char publisher id of a package family name: Crockford base32 of the first 8 bytes of SHA-256(UTF-16 publisher).</summary>
    public static string PublisherId(string publisher)
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        var bits = new StringBuilder(65);
        for (int i = 0; i < 8; i++) bits.Append(Convert.ToString(hash[i], 2).PadLeft(8, '0'));
        bits.Append('0');
        var sb = new StringBuilder(13);
        for (int i = 0; i < 65; i += 5) sb.Append(alphabet[Convert.ToInt32(bits.ToString(i, 5), 2)]);
        return sb.ToString();
    }
}
