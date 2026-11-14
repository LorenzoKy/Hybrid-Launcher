using HybridLauncher.Core;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;

namespace HybridLauncher;

/// <summary>
/// OptiScaler update notifications: the sources the user turned on in a game's OptiScaler tab (Get notified on updates: Stable,
/// Nightly or one fork, each apart) are looked at half a minute after start and then every three hours; a new release raises one
/// Windows notification from the icon by the clock (shown as a toast and kept in the notification centre).
/// A click on it opens a game that uses that source on its OptiScaler tab.
/// </summary>
public sealed partial class MainWindow
{
    static readonly TimeSpan OptiCheckEvery = TimeSpan.FromHours(3);
    CancellationTokenSource? _optiChecks;

    /// <summary>Starts (or stops, with nothing followed) the background look at the followed sources.</summary>
    public void OptiFollowChanged()
    {
        if (AppState.Settings.OptiFollow.Count == 0)
        {
            _optiChecks?.Cancel();
            _optiChecks = null;
            return;
        }
        if (_optiChecks is not null) return;
        var cts = _optiChecks = new CancellationTokenSource();
        _ = OptiChecksAsync(cts.Token);
    }

    async Task OptiChecksAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            while (!ct.IsCancellationRequested)
            {
                // One check that fails (a file it could not save, say) must not end the checks for the rest of the session.
                try
                {
                    var found = await OptiUpdates.CheckAsync(AppState.Settings, ct);
                    SettingsStore.Save();
                    NotifyOptiUpdates(found);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn($"OptiScaler update check: {ex.Message}"); }
                await Task.Delay(OptiCheckEvery, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"OptiScaler update checks: {ex.Message}"); }
    }

    /// <summary>
    /// The new releases found in one check, as one notification: Windows shows one at a time from the icon, so a second one would
    /// replace the first and its click would open the wrong game.
    /// </summary>
    void NotifyOptiUpdates(List<(string Key, OptiRelease Release)> found)
    {
        if (_closed || found.Count == 0) return;
        foreach (var (key, release) in found) Log.Info($"OptiScaler update: {OptiUpdates.Name(key, AppState.Settings)} {release.Tag}");
        // A game installed from the (first) source, to open on its OptiScaler tab (else the window just opens).
        var first = found[0].Key;
        var game = AppState.Games.FirstOrDefault(g => AppState.Settings.For(g.Id).Install is { } m && OptiUpdates.Key(m).Equals(first, StringComparison.OrdinalIgnoreCase));
        string title, text;
        if (found.Count == 1)
        {
            var name = OptiUpdates.Name(first, AppState.Settings);
            // "OptiScaler Stable: v0.9.5 is out"; a fork already has OptiScaler in its name ("OptiScaler Venom (LorenzoKy): ...").
            // Short and plain (2.1-rc.9): what is out in the title, one line on what to do in the text.
            title = L.F("{0} {1} is out", OptiName(name), found[0].Release.Tag);
            text = game is null ? L.T("A new version of the build you follow.") : L.F("Click to update it in {0}.", game.Title);
        }
        else
        {
            title = L.F("{0} OptiScaler updates", found.Count);
            text = string.Join("  ·  ", found.Select(f => $"{OptiUpdates.Name(f.Key, AppState.Settings)} {f.Release.Tag}"));
        }
        // The bell keeps one line per release, each opening a game that uses its source; Windows gets the one notification.
        foreach (var (key, release) in found)
        {
            var name = OptiUpdates.Name(key, AppState.Settings);
            var user = AppState.Games.FirstOrDefault(g => AppState.Settings.For(g.Id).Install is { } m && OptiUpdates.Key(m).Equals(key, StringComparison.OrdinalIgnoreCase));
            Inbox.Add(Inbox.Opti, L.F("{0} {1} is out", OptiName(name), release.Tag),
                user is null ? L.T("A new version of the build you follow.") : L.F("Update it from {0}'s OptiScaler tab.", user.Title),
                user?.Id, "opti", key: $"opti:{key}:{release.Tag}");
        }
        if (!Popup(Inbox.Opti)) return;
        EnsureTray();
        _tray?.Notify(title, text, () => OpenFromNotification(game?.Id));
    }

    /// <summary>A source's name as a notification shows it: forks already carry OptiScaler in their name.</summary>
    static string OptiName(string name) => name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) ? name : "OptiScaler " + name;

    /// <summary>A click on an update notification: the window opens, on the game's OptiScaler tab when there is one.</summary>
    void OpenFromNotification(string? gameId)
    {
        ShowFromTray();
        if (gameId is not null && AppState.Find(gameId) is not null)
        {
            AppState.Settings.GameTab = "opti";
            OpenGame(gameId);
        }
    }
}
