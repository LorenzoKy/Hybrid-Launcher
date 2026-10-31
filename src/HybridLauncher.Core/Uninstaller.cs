using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Core;

/// <summary>How a game is uninstalled. Hybrid Launcher does it itself, with no launcher window, whenever it can.</summary>
public enum UninstallRoute
{
    /// <summary>Steam game: Hybrid Launcher closes Steam if it runs, deletes the game folder and Steam's record of it, then starts Steam again.</summary>
    Steam,
    /// <summary>GOG game: its own uninstaller (unins000.exe) runs silently.</summary>
    Uninstaller,
    /// <summary>Epic game: Hybrid Launcher closes the Epic Games Launcher if it runs, deletes the game folder and the launcher's records of it.</summary>
    Epic,
    /// <summary>Downloaded by Hybrid Launcher: its folder is deleted and it leaves Hybrid Launcher's list.</summary>
    Direct,
    /// <summary>Xbox / Game Pass (and installs a launcher keeps no readable record of): the store's own app opens on it.</summary>
    Launcher,
    /// <summary>A game added by hand: its folder goes to the Recycle Bin.</summary>
    RecycleBin,
}

/// <summary>
/// Uninstalls a game, installed files only: saves and settings usually live outside the install folder (Documents, AppData,
/// Steam Cloud) and are left alone.
/// </summary>
public static class Uninstaller
{
    public static UninstallRoute Route(Game game) => game switch
    {
        { Direct: true } => UninstallRoute.Direct,
        { Store: GameStore.Steam, SteamAppId: not null } => UninstallRoute.Steam,
        { Store: GameStore.Gog } when GogUninstaller(game) is not null => UninstallRoute.Uninstaller,
        { Store: GameStore.Epic } when EpicItem(game) is not null => UninstallRoute.Epic,
        { Store: GameStore.Epic or GameStore.Gog or GameStore.Xbox } => UninstallRoute.Launcher,
        _ => UninstallRoute.RecycleBin,
    };

    static string? GogUninstaller(Game game)
    {
        try
        {
            return Directory.Exists(game.InstallDir)
                ? Directory.GetFiles(game.InstallDir, "unins*.exe").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }
        catch { return null; }
    }

    static string EpicData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic");

    /// <summary>The Epic Games Launcher's install record of the game (Manifests\&lt;id&gt;.item), found by its folder.</summary>
    static string? EpicItem(Game game)
    {
        try
        {
            var dir = Path.Combine(EpicData, "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(dir)) return null;
            var target = Path.GetFullPath(game.InstallDir).TrimEnd('\\');
            foreach (var item in Directory.GetFiles(dir, "*.item"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { } l
                    && Path.GetFullPath(l).TrimEnd('\\').Equals(target, StringComparison.OrdinalIgnoreCase)) return item;
            }
        }
        catch (Exception ex) { Log.Warn($"Epic record of {game.Name}: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// Uninstalls (the caller confirms first). Returns null when done (or, for the store app route, when its window opened),
    /// else why it could not be.
    /// </summary>
    public static string? Start(Game game)
    {
        var route = Route(game);
        Log.Info($"Uninstall {game.Name} ({route})");
        switch (route)
        {
            case UninstallRoute.Steam:
                return RemoveSteam(game);
            case UninstallRoute.Uninstaller:
                var exe = GogUninstaller(game)!;
                using (var p = Process.Start(new ProcessStartInfo(exe, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true, WorkingDirectory = game.InstallDir }))
                    p?.WaitForExit(TimeSpan.FromMinutes(10));
                return null;
            case UninstallRoute.Epic:
                return RemoveEpic(game);
            case UninstallRoute.Direct:
                if (!SafeToDelete(game.InstallDir)) return L.F("Hybrid Launcher does not delete {0}: it is not a game's own folder.", game.InstallDir);
                var error = DeleteFolder(game.InstallDir);
                if (error is not null) return error;
                lock (SettingsStore.Current) SettingsStore.Current.DirectInstalls.RemoveAll(d => d.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase));
                SettingsStore.Save();
                return null;
            case UninstallRoute.Launcher:
                GameLauncher.OpenUri(game.Store switch
                {
                    GameStore.Epic => "com.epicgames.launcher://library",
                    GameStore.Gog => "goggalaxy://",
                    _ => "ms-settings:appsfeatures",
                });
                return null;
            default:
                if (!SafeToDelete(game.InstallDir)) return L.F("Hybrid Launcher does not delete {0}: it is not a game's own folder.", game.InstallDir);
                return RecycleFolder(game.InstallDir) ? null : L.T("Windows did not move the folder to the Recycle Bin (a file may be in use).");
        }
    }

    static string? DeleteFolder(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                // Read-only files (some games ship them) would stop the delete.
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    if ((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Delete {dir}: {ex.Message}");
            return L.F("Some files could not be deleted (one may be in use): {0}", ex.Message);
        }
    }

    static bool Running(string name)
    {
        var list = Process.GetProcessesByName(name);
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    /// <summary>Steam keeps its library in memory and writes it back: it is closed first, and started again (minimised) after.</summary>
    static string? RemoveSteam(Game game)
    {
        var id = game.SteamAppId!;
        var found = SteamScanner.Libraries().Select(lib => Path.Combine(lib, "steamapps"))
            .Select(apps => (Apps: apps, Acf: Path.Combine(apps, $"appmanifest_{id}.acf"))).FirstOrDefault(x => File.Exists(x.Acf));
        var steamExe = SteamScanner.SteamExe();
        bool running = Running("steam");
        if (running)
        {
            if (steamExe is null) return L.T("Steam is running but steam.exe was not found: close Steam and try again.");
            Process.Start(new ProcessStartInfo(steamExe, "-shutdown") { UseShellExecute = false })?.Dispose();
            var until = DateTime.UtcNow.AddSeconds(60);
            while (Running("steam") && DateTime.UtcNow < until) Thread.Sleep(500);
            if (Running("steam")) return L.T("Steam did not close in time. Close it from its menu (Steam → Exit) and try again.");
            Thread.Sleep(800);
        }
        string? error = null;
        if (SafeToDelete(game.InstallDir)) error = DeleteFolder(game.InstallDir);
        if (found.Acf is not null)
        {
            try { File.Delete(found.Acf); } catch (Exception ex) { error ??= ex.Message; }
            try
            {
                foreach (var extra in new[] { Path.Combine(found.Apps, "downloading", id), Path.Combine(found.Apps, "temp", id) })
                    if (Directory.Exists(extra)) Directory.Delete(extra, recursive: true);
            }
            catch { }
        }
        if (running && steamExe is not null)
        {
            try { Process.Start(new ProcessStartInfo(steamExe, "-silent") { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Log.Warn($"Steam restart: {ex.Message}"); }
        }
        return error;
    }

    /// <summary>The game folder, the launcher's .item record and its line in LauncherInstalled.dat; the launcher is closed first.</summary>
    static string? RemoveEpic(Game game)
    {
        var item = EpicItem(game);
        foreach (var name in new[] { "EpicGamesLauncher", "EpicWebHelper" })
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                {
                    try { p.CloseMainWindow(); if (!p.WaitForExit(5000)) p.Kill(); } catch { }
                }
        if (!SafeToDelete(game.InstallDir)) return L.F("Hybrid Launcher does not delete {0}: it is not a game's own folder.", game.InstallDir);
        var error = DeleteFolder(game.InstallDir);
        if (item is not null)
        {
            try { File.Delete(item); } catch (Exception ex) { error ??= ex.Message; }
        }
        var dat = Path.Combine(EpicData, "UnrealEngineLauncher", "LauncherInstalled.dat");
        try
        {
            if (File.Exists(dat) && JsonNode.Parse(File.ReadAllText(dat)) is JsonObject root && root["InstallationList"] is JsonArray list)
            {
                var target = Path.GetFullPath(game.InstallDir).TrimEnd('\\');
                foreach (var entry in list.OfType<JsonObject>().Where(e => e["InstallLocation"]?.GetValue<string>() is { } l
                             && Path.GetFullPath(l).TrimEnd('\\').Equals(target, StringComparison.OrdinalIgnoreCase)).ToList())
                    list.Remove(entry);
                File.WriteAllText(dat, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex) { Log.Warn($"LauncherInstalled.dat: {ex.Message}"); }
        return error;
    }

    /// <summary>Whether the game is gone from disk (its Steam manifest or its folder), so the list can drop it.</summary>
    public static bool IsGone(Game game)
    {
        if (game.Store == GameStore.Steam && game.SteamAppId is { } id)
            return !SteamScanner.Libraries().Any(lib => File.Exists(Path.Combine(lib, "steamapps", $"appmanifest_{id}.acf")));
        return !Directory.Exists(game.InstallDir);
    }

    /// <summary>A folder at least two levels deep that is none of Windows' own folders (a drive, Program Files, the user folder...).</summary>
    public static bool SafeToDelete(string dir)
    {
        string full;
        try { full = Path.GetFullPath(dir).TrimEnd('\\'); }
        catch { return false; }
        if (!Directory.Exists(full) || full.Count(c => c == '\\') < 2) return false;
        Environment.SpecialFolder[] guarded =
        [
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
        ];
        foreach (var f in guarded)
        {
            var p = Environment.GetFolderPath(f).TrimEnd('\\');
            if (p.Length == 0) continue;
            // The folder itself or one of its parents.
            if (p.Equals(full, StringComparison.OrdinalIgnoreCase) || p.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return !downloads.Equals(full, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Recycle Bin ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Moves a folder to the Recycle Bin (Windows warns first when it is too big for it and would be deleted for good).</summary>
    public static bool RecycleFolder(string dir)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(dir) + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
        };
        int result = SHFileOperation(ref op);
        if (result != 0) Log.Warn($"Recycle {dir}: shell error {result}");
        return result == 0 && op.fAnyOperationsAborted == 0 && !Directory.Exists(dir);
    }
}
