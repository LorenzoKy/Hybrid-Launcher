using System.Diagnostics;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Core;

/// <summary>
/// Desktop shortcuts that start a game through Hybrid Launcher (Hydra launcher style): the .lnk runs <c>HybridLauncher.exe --launch &lt;game id&gt;</c>,
/// so Smart HDR, play time and Stop work as if Play had been pressed in the app. The icon is the game's own exe.
/// </summary>
public static class Shortcuts
{
    public const string LaunchSwitch = "--launch";

    public static string DesktopPath(Game game) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SafeName(game.Name) + ".lnk");

    public static bool Exists(Game game) => File.Exists(DesktopPath(game));

    /// <summary>Writes (or rewrites) the desktop shortcut. Uses the Windows Script Host shell object, which every Windows has.</summary>
    public static async Task<string> CreateDesktopAsync(Game game, GameSettings settings)
    {
        var path = DesktopPath(game);
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Hybrid Launcher's own path is unknown.");
        var exe = GameScanner.EffectiveExe(game, settings);
        var icon = exe is not null && File.Exists(exe) ? exe : self;
        // Values travel in environment variables, so names with quotes or $ cannot break the script.
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($env:HL_LNK);" +
                "$s.TargetPath=$env:HL_EXE;$s.Arguments=$env:HL_ARGS;$s.WorkingDirectory=$env:HL_DIR;" +
                "$s.IconLocation=$env:HL_ICON;$s.Description=$env:HL_DESC;$s.Save()",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.Environment["HL_LNK"] = path;
        psi.Environment["HL_EXE"] = self;
        psi.Environment["HL_ARGS"] = $"{LaunchSwitch} \"{game.Id}\"";
        psi.Environment["HL_DIR"] = Path.GetDirectoryName(self)!;
        psi.Environment["HL_ICON"] = icon + ",0";
        psi.Environment["HL_DESC"] = L.F("Play {0} with Hybrid Launcher", game.Name);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell did not start.");
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 || !File.Exists(path)) throw new IOException(string.IsNullOrWhiteSpace(err) ? "The shortcut was not written." : err.Trim());
        Log.Info($"Desktop shortcut for {game.Name}: {path}");
        return path;
    }

    /// <summary>The game id after --launch on a command line, if any.</summary>
    public static string? LaunchRequest(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i].Equals(LaunchSwitch, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Game" : clean;
    }
}
