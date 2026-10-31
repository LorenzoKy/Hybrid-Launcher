using Microsoft.UI.Dispatching;
using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Views;

namespace HybridLauncher.Helpers;

/// <summary>
/// Steam-style automatic updates for the games Hybrid Launcher downloaded: a check a minute after Hybrid Launcher starts and then every six hours;
/// each game with a newer build updates in Downloads (only the files that changed), unless it is running or already downloading.
/// Off in Settings → Features: nothing is checked.
/// </summary>
public static class GameUpdater
{
    static readonly TimeSpan Every = TimeSpan.FromHours(6);
    static DispatcherQueueTimer? _timer;
    static bool _checking;

    public static void Start(DispatcherQueue ui)
    {
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += async (t, _) =>
        {
            t.Interval = Every;
            await CheckNowAsync();
        };
        _timer.Start();
    }

    /// <summary>Checks now and starts the updates found; the number started.</summary>
    public static async Task<int> CheckNowAsync(bool evenIfOff = false)
    {
        var settings = AppState.Settings;
        if (_checking || !evenIfOff && !settings.AutoUpdateGames || settings.DirectInstalls.Count == 0) return 0;
        _checking = true;
        try
        {
            await OwnedLibrary.LoadAsync();
            var updates = await Task.Run(() => GameUpdates.CheckAsync(OwnedLibrary.Find, CancellationToken.None));
            int started = 0;
            foreach (var u in updates)
            {
                if (GameSessions.IsActive(u.Install.Id)) { Log.Info($"Update of {u.Install.Name} waits: the game is running"); continue; }
                if (Downloads.Find(u.Install.Id) is { State: DownloadState.Waiting or DownloadState.Downloading or DownloadState.Paused }) continue;
                Downloads.StartUpdate(u.Game, u.Install);
                started++;
            }
            return started;
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check: {ex.Message}");
            return 0;
        }
        finally { _checking = false; }
    }
}
