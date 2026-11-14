namespace HybridLauncher.Core;

/// <summary>
/// "Verify game files" (2.1): the launcher that installed a game checks its files against its own list and downloads again
/// what is missing or changed. Steam and Epic start it from a link; the other launchers have no link for it, so Hybrid
/// Launcher opens them on the game and says where the button is. Mods that replaced a game's own file (a swapped DLSS dll, a
/// patched exe) are put back to the original by a verify: the plan says so, and the game page's mods are checked after it.
/// </summary>
public static class GameVerify
{
    /// <summary>
    /// What a verify of the game takes: <see cref="Uri"/> starts it at once when it is set; otherwise <see cref="OpenUri"/>
    /// (or <see cref="OpenExe"/>) opens the launcher and <see cref="Steps"/> says where to click. <see cref="Launcher"/> null:
    /// no launcher can verify this game (a game added by hand).
    /// </summary>
    public sealed record Plan(string? Launcher, string? Uri, string? OpenUri, string? Steps, string? OpenExe = null)
    {
        public bool Direct => Uri is not null;
        public bool Possible => Launcher is not null;
    }

    public static Plan For(Game game)
    {
        string Suffix(string prefix) => game.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? game.Id[prefix.Length..] : "";
        if (game.Direct)
            return new Plan(null, null, null, null);
        switch (game.Store)
        {
            case GameStore.Steam when game.SteamAppId is { } app:
                return new Plan("Steam", $"steam://validate/{app}", null, null);
            case GameStore.Epic when game.EpicLaunchUri is { } launch:
                // The launch link with the verify action instead: Epic opens and checks the game.
                var q = launch.IndexOf('?');
                return new Plan("Epic Games Launcher", (q < 0 ? launch : launch[..q]) + "?action=verify", null, null);
            case GameStore.Epic:
                return new Plan("Epic Games Launcher", null, "com.epicgames.launcher://store/library",
                    "In the Library, click ⋯ on the game → Manage → Verify.");
            case GameStore.Gog:
                return new Plan("GOG Galaxy", null, $"goggalaxy://openGameView/{Suffix("gog:")}",
                    "On the game's page in GOG Galaxy, click the settings icon beside Play → Manage installation → Verify / Repair.");
            case GameStore.Ubisoft:
                return new Plan("Ubisoft Connect", null, "uplay://",
                    "In Ubisoft Connect open Games, pick the game → Properties → Verify files.");
            case GameStore.Ea:
                return new Plan("EA app", null, "origin2://library/open",
                    "In the EA app open Library, click ⋯ on the game → Repair.");
            case GameStore.Xbox:
                return new Plan("Xbox app", null, "xbox://",
                    "In the Xbox app open the game's page, click ⋯ → Manage → Files → Verify and repair.");
            case GameStore.Rockstar:
                return new Plan("Rockstar Games Launcher", null, null,
                    "In the Rockstar Games Launcher open Settings → My installed games → the game → Verify integrity.",
                    OpenExe: game.LauncherCommand?.Exe);
            default:
                return new Plan(null, null, null, null);
        }
    }

    /// <summary>Starts the verify, or opens the launcher on the game. False when nothing could be opened.</summary>
    public static bool Start(Plan plan)
    {
        try
        {
            if (plan.Uri is { } uri) { GameLauncher.OpenUri(uri); return true; }
            if (plan.OpenUri is { } open && GameLauncher.HasProtocol(open.Split(':')[0])) { GameLauncher.OpenUri(open); return true; }
            if (plan.OpenExe is { } exe && File.Exists(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
                return true;
            }
        }
        catch (Exception ex) { Log.Warn($"Verify: {ex.Message}"); }
        return false;
    }
}
