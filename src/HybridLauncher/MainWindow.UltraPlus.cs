using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;

namespace HybridLauncher;

/// <summary>
/// Ultra+ in the background: the installed mods are looked at once the library is read, then every three hours (Ultra+'s lists
/// read again from its server). With notifications on (a game's Ultra+ tab), each new build raises one Windows notification from the icon by the clock, and a click opens the game's Ultra+ tab.
/// </summary>
public sealed partial class MainWindow
{
    static readonly TimeSpan UltraCheckEvery = TimeSpan.FromHours(3);
    CancellationTokenSource? _ultraChecks;

    /// <summary>Starts (or, with the check turned off, stops) the background look at the installed mods.</summary>
    public void UltraChecksChanged()
    {
        if (!AppState.Settings.UltraPlusAutoCheck)
        {
            _ultraChecks?.Cancel();
            _ultraChecks = null;
            return;
        }
        if (_ultraChecks is not null) return;
        var cts = _ultraChecks = new CancellationTokenSource();
        _ = UltraChecksAsync(cts.Token);
    }

    async Task UltraChecksAsync(CancellationToken ct)
    {
        try
        {
            // The first look uses the cached lists (no wait at start); the later ones read Ultra+'s server again.
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            bool force = false;
            while (!ct.IsCancellationRequested)
            {
                // One check that fails must not end the checks for the rest of the session.
                try
                {
                    await UltraPlusLibrary.ScanAsync(force, ct);
                    if (UltraPlusLibrary.UpdateCount > 0) Log.Info($"Ultra+: {UltraPlusLibrary.UpdateCount} update(s) available");
                    if (AppState.Settings.UltraPlusAutoUpdate && UltraPlusLibrary.UpdateCount > 0)
                        NotifyUltraUpdated(await UltraPlusLibrary.AutoUpdateAsync(ct));
                    NotifyUltraUpdates();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn($"Ultra+ update check: {ex.Message}"); }
                force = true;
                await Task.Delay(UltraCheckEvery, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Ultra+ update checks: {ex.Message}"); }
    }

    /// <summary>The mods the background check updated by itself: one notification for all of them.</summary>
    void NotifyUltraUpdated(List<(Game Game, string Version)> updated)
    {
        if (updated.Count == 0 || _closed) return;
        // What was installed by itself is always kept in the bell's list (read there later), with or without a pop-up.
        foreach (var (game, version) in updated)
            Inbox.Add(Inbox.Ultra, L.F("Ultra+ {0} installed in {1}", version, game.Title), L.T("Updated by itself. Your settings were kept."), game.Id, "ultra");
        if (!AppState.Settings.UltraPlusNotify || !Popup(Inbox.Ultra)) return;
        var id = updated[0].Game.Id;
        EnsureTray();
        _tray?.Notify(updated.Count == 1 ? L.F("Ultra+ {0} installed in {1}", updated[0].Version, updated[0].Game.Title) : L.F("Ultra+ updated in {0} games", updated.Count),
            updated.Count == 1 ? L.T("Updated by itself. Your settings were kept.") : string.Join("  ·  ", updated.Select(u => u.Game.Title)), () =>
            {
                ShowFromTray();
                if (AppState.Find(id) is null) return;
                AppState.Settings.GameTab = "ultra";
                OpenGame(id);
            });
    }

    /// <summary>
    /// The new builds of installed mods (each build notifies once, then counts as seen), as one notification: Windows shows one at a
    /// time from the icon, so a second one would replace the first and its click would open the wrong game.
    /// </summary>
    void NotifyUltraUpdates()
    {
        // Kept in the bell even without pop-ups (unless the bell is off for Ultra+ too); each build counts once.
        if (_closed || (!AppState.Settings.UltraPlusNotify && AppState.Settings.InboxOff.Contains(Inbox.Ultra))) return;
        var fresh = new List<UltraEntry>();
        foreach (var e in UltraPlusLibrary.Entries.Where(e => e.Update is not null && e.State is not null))
        {
            var update = e.Update!;
            if (AppState.Settings.UltraPlusSeen.TryGetValue(e.Ultra.Key, out var seen) && seen.Equals(update.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            AppState.Settings.UltraPlusSeen[e.Ultra.Key] = update.FileName;
            Log.Info($"Ultra+ update notified: {e.Game.Name} {update.Version}");
            fresh.Add(e);
        }
        if (fresh.Count == 0) return;
        SettingsStore.Save();
        foreach (var e in fresh)
            Inbox.Add(Inbox.Ultra, L.F("Ultra+ {0} is out for {1}", e.Update!.Version, e.Game.Title), L.T("Update it from the game's Ultra+ tab. Your settings stay."),
                e.Game.Id, "ultra", key: $"ultra:{e.Ultra.Key}:{e.Update.FileName}");
        if (!AppState.Settings.UltraPlusNotify || !Popup(Inbox.Ultra)) return;
        var id = fresh[0].Game.Id;
        EnsureTray();
        _tray?.Notify(fresh.Count == 1 ? L.F("Ultra+ {0} is out for {1}", fresh[0].Update!.Version, fresh[0].Game.Title) : L.F("New Ultra+ versions for {0} games", fresh.Count),
            fresh.Count == 1 ? L.T("Click to update it. Your settings stay.") : string.Join("  ·  ", fresh.Select(e => $"{e.Game.Title} {e.Update!.Version}")), () =>
            {
                ShowFromTray();
                if (AppState.Find(id) is null) return;
                AppState.Settings.GameTab = "ultra";
                OpenGame(id);
            });
    }

    /// <summary>
    /// Self-test (HL_SELFTEST_ULTRA=1, fake game folders only): up to two library games get an older Ultra+ version, their tab
    /// and the page show the update, Update all brings them up to date, then Ultra+ comes out again. Returns the failures.
    /// </summary>
    async Task<int> SelfTestUltraAsync(Func<string, Action, int, Task> step)
    {
        int failures = 0;
        async Task Check(string what, Func<Task> work)
        {
            try
            {
                await work();
                Log.Info($"Self-test: {what}");
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error($"Self-test FAILED: {what}", ex);
            }
        }
        // The confirmations an uninstall asks for are answered here: a self-test has nobody at the keyboard.
        UltraPlusLibrary.Unattended = true;
        // The older versions put in on purpose must stay until Update all: no automatic update in between.
        bool autoUpdate = AppState.Settings.UltraPlusAutoUpdate;
        AppState.Settings.UltraPlusAutoUpdate = false;
        try
        {
            List<UltraEntry> mine = [];
            await Check("ultra installs (older versions)", async () =>
            {
                await UltraPlusLibrary.ScanAsync();
                mine = UltraPlusLibrary.Entries.Where(e => e.Versions.Count(v => !v.Beta) >= 2 && e.State is null).Take(2).ToList();
                if (mine.Count == 0) throw new InvalidOperationException("no library game has two Ultra+ versions (fake Unreal games needed)");
                foreach (var e in mine)
                {
                    var older = e.Versions.Where(v => !v.Beta).Skip(1).First();
                    if (!await UltraPlusLibrary.InstallAsync(e.Game, e.Ultra, older, false, null, CancellationToken.None, quiet: true))
                        throw new InvalidOperationException($"Ultra+ {older.FileName} did not install in {e.Game.Name}");
                }
                if (UltraPlusLibrary.UpdateCount < mine.Count) throw new InvalidOperationException($"{UltraPlusLibrary.UpdateCount} update(s) shown for {mine.Count} older install(s)");
            });
            if (UltraPlusLibrary.Entries.FirstOrDefault(e => e.State is not null) is { } installed)
            {
                await step($"ultra {installed.Game.Name}: install", () => { AppState.Settings.GameTab = "ultra"; OpenGame(installed.Game.Id); }, 4000);
                await step($"ultra {installed.Game.Name}: add-ons", () => (Shown as GamePage)?.SelfTestShow("ultra/addons"), 1500);
                await step($"ultra {installed.Game.Name}: settings", () => (Shown as GamePage)?.SelfTestShow("ultra/settings"), 1500);
                await step($"ultra {installed.Game.Name}: scroll", () => (Shown as GamePage)?.SelfTestScroll(true), 800);
            }
            await Check("ultra update all", async () =>
            {
                int updated = await UltraPlusLibrary.UpdateAllAsync(null, CancellationToken.None);
                if (updated < mine.Count || UltraPlusLibrary.UpdateCount > 0) throw new InvalidOperationException($"Update all: {updated} updated, {UltraPlusLibrary.UpdateCount} left");
            });
            if (mine.Count > 0) await step($"ultra {mine[0].Game.Name}: up to date", () => { AppState.Settings.GameTab = "ultra"; OpenGame(mine[0].Game.Id); }, 3000);
            await Check("ultra uninstalls", async () =>
            {
                foreach (var e in mine)
                    if (!await UltraPlusLibrary.UninstallAsync(e.Game)) throw new InvalidOperationException($"Ultra+ did not come out of {e.Game.Name}");
                if (UltraPlusLibrary.Entries.Any(e => e.Installed)) throw new InvalidOperationException("Ultra+ is still seen in a game folder");
            });
        }
        finally
        {
            UltraPlusLibrary.Unattended = false;
            AppState.Settings.UltraPlusAutoUpdate = autoUpdate;
        }
        return failures;
    }

    /// <summary>Notifications turned on: the builds already out count as seen, so only later ones notify.</summary>
    public static void MarkUltraUpdatesSeen()
    {
        foreach (var e in UltraPlusLibrary.Entries.Where(e => e.Update is not null))
            AppState.Settings.UltraPlusSeen[e.Ultra.Key] = e.Update!.FileName;
    }
}
