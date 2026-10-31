using System.Diagnostics;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Core;

public static class GameLauncher
{
    /// <summary>Whether custom launch arguments can be passed for this game.</summary>
    public static bool SupportsArgs(Game game) => game.Store != GameStore.Xbox;

    /// <summary>
    /// Launches through the store when possible (keeps overlays, cloud saves, DRM happy).
    /// Steam passes arguments with -applaunch; for Epic/GOG/custom, arguments mean starting the exe directly.
    /// </summary>
    public static async Task LaunchAsync(Game game, GameSettings settings)
    {
        // As on Steam, a game does not start halfway through its update.
        if (Accounts.Downloads.Updating(game.Id)) throw new InvalidOperationException(L.F("{0} is updating. It can start once the update in Downloads is done.", game.Name));
        var args = settings.LaunchArgs?.Trim() ?? "";
        var exe = GameScanner.EffectiveExe(game, settings);

        // Downloaded by Hybrid Launcher: no launcher knows it, so it starts from its own exe with the store's launch arguments. A Steam game
        // Hybrid Launcher downloaded into a Steam library does the same while Steam is closed: Steam stays closed (no start, no update check);
        // while Steam is open it goes through Steam below, which is restarted first if it has not seen the game yet.
        var direct = SettingsStore.Current.DirectInstalls.FirstOrDefault(d => d.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase));
        if (direct is not null && (game.Direct || game.Store == GameStore.Steam && !SteamScanner.ClientRunning()))
        {
            string? ownExe = null;
            try { ownExe = direct.Exe is { Length: > 0 } e ? Accounts.DirectDownloader.Full(direct.InstallDir, e) : null; } catch { }
            var target = settings.ExeOverride is not null ? exe
                : game.StoreExe ?? (ownExe is not null && File.Exists(ownExe) ? ownExe : exe);
            if (target is null || !File.Exists(target)) throw new FileNotFoundException("No executable found for this game. Pick one with \"Change exe\".");
            var all = string.Join(' ', new[] { direct.Args, args }.Where(a => !string.IsNullOrWhiteSpace(a)));
            if (game.Store == GameStore.Epic)
            {
                // Epic games that check the account at start read a one-time code from their arguments. Awaited, never waited
                // on: the window stays responsive while Epic answers (up to 8 s, then the game starts without it).
                try
                {
                    var code = await Accounts.EpicAccount.LaunchArgsAsync(game.Id["epic:".Length..], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                    all = (code + " " + all).Trim();
                }
                catch (Exception ex) { Log.Warn($"Epic launch code: {ex.Message}"); }
            }
            string? workDir = null;
            try { workDir = direct.WorkingDir is { Length: > 0 } w ? Accounts.DirectDownloader.Full(direct.InstallDir, w) : null; } catch { }
            Start(target, all, workDir ?? Path.GetDirectoryName(target));
            return;
        }

        switch (game.Store)
        {
            case GameStore.Steam when game.SteamAppId is not null:
                // Downloaded by Hybrid Launcher while Steam was open: Steam has not read its manifest yet and would not start it.
                if (SteamScanner.ClientRunning() && !SteamScanner.ClientKnows(game.SteamAppId)
                    && SteamScanner.Libraries().Any(lib => File.Exists(Path.Combine(lib, "steamapps", $"appmanifest_{game.SteamAppId}.acf"))))
                {
                    _ = SteamScanner.RestartClientAsync($"-applaunch {game.SteamAppId} {args}".TrimEnd());
                    return;
                }
                if (args.Length > 0 && SteamScanner.SteamExe() is { } steam)
                    Start(steam, $"-applaunch {game.SteamAppId} {args}");
                else
                    OpenUri($"steam://rungameid/{game.SteamAppId}");
                return;

            case GameStore.Epic when game.EpicLaunchUri is not null && args.Length == 0:
                OpenUri(game.EpicLaunchUri);
                return;

            case GameStore.Xbox when game.XboxAumid is not null:
                Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"shell:AppsFolder\\{game.XboxAumid}");
                return;
        }

        // Launch the store's own entry exe if it has one (GOG/Epic bootstrap), otherwise the detected game exe.
        var launchExe = game.StoreExe is not null && File.Exists(game.StoreExe) && settings.ExeOverride is null ? game.StoreExe : exe;
        if (launchExe is null || !File.Exists(launchExe)) throw new FileNotFoundException("No executable found for this game. Pick one with \"Change exe\".");
        Start(launchExe, args, Path.GetDirectoryName(launchExe));
    }

    public static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    /// <summary>True when an app is registered for the URL scheme (discord:, steam:...).</summary>
    public static bool HasProtocol(string scheme)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch { return false; }
    }

    /// <summary>Shows Windows' "How do you want to open this file?" picker for the file; the choice is used once.</summary>
    public static void OpenWith(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException(L.T("File not found."), file);
        var openWith = Path.Combine(Environment.SystemDirectory, "OpenWith.exe");
        if (File.Exists(openWith))
            Process.Start(new ProcessStartInfo(openWith) { ArgumentList = { file }, UseShellExecute = false })?.Dispose();
        else
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), $"shell32.dll,OpenAs_RunDLL {file}") { UseShellExecute = false })?.Dispose();
    }

    public static void OpenFolder(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false })?.Dispose();
    }

    static void Start(string exe, string args, string? workDir = null)
    {
        Log.Info($"Launch: \"{exe}\" {args}");
        Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = true,
            WorkingDirectory = workDir ?? Path.GetDirectoryName(exe) ?? "",
        })?.Dispose();
    }
}
