namespace HybridLauncher.Core.Accounts;

/// <summary>A game Hybrid Launcher downloaded whose store has a newer build than the one installed.</summary>
public sealed record GameUpdate(DirectInstall Install, OwnedGame Game, string Latest);

/// <summary>
/// Keeps the games Hybrid Launcher downloaded up to date, as Steam does: it asks each store for the game's current build and compares it
/// with the one installed. Games a launcher installed are kept up to date by that launcher (Steam, the Epic Games Launcher, GOG
/// Galaxy, the Microsoft Store), as is a game Hybrid Launcher put in a Steam library while Steam is open and knows it.
/// </summary>
public static class GameUpdates
{
    /// <summary>The games with a newer build; one store not answering leaves only its games out.</summary>
    public static async Task<List<GameUpdate>> CheckAsync(Func<string, OwnedGame?> owned, CancellationToken ct)
    {
        var found = new List<GameUpdate>();
        foreach (var install in SettingsStore.Current.DirectInstalls.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(install.InstallDir) || owned(install.Id) is not { } game) continue;
            if (!OwnedGames.Direct(install.Store)) continue; // signed out: Hybrid Launcher cannot download it
            // Open Steam that knows the game updates it itself.
            if (install.Store == GameStore.Steam && Detection.SteamScanner.ClientRunning() && Detection.SteamScanner.ClientKnows(install.Id[6..])) continue;
            try
            {
                var latest = install.Store switch
                {
                    GameStore.Steam => await SteamAccount.LatestBuildAsync(install.Id[6..], ct),
                    GameStore.Epic => await EpicAccount.LatestBuildAsync(game, ct),
                    GameStore.Gog => await GogAccount.LatestBuildAsync(install.Id[4..], ct),
                    _ => null,
                };
                if (latest is { Length: > 0 } && !latest.Equals(install.Version ?? "", StringComparison.OrdinalIgnoreCase))
                    found.Add(new GameUpdate(install, game, latest));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Update check of {install.Name}: {ex.Message}"); }
        }
        Log.Info(found.Count == 0 ? "Update check: every game Hybrid Launcher downloaded is up to date"
            : "Update check: " + string.Join(", ", found.Select(u => $"{u.Install.Name} {u.Install.Version ?? "?"} → {u.Latest}")));
        return found;
    }
}
