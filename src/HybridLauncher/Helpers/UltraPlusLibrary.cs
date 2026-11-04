using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Mods;
using HybridLauncher.Core.Opti;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Helpers;

/// <summary>One library game Ultra+ has a mod for, and where it stands.</summary>
public sealed record UltraEntry(Game Game, UltraGame Ultra, string Reason, UltraLayout Layout, UltraPlusState? State, UltraFolderState Folder,
    List<UltraFile> Versions, UltraFile? Update)
{
    public bool Installed => State is not null || Folder.Installed;
}

/// <summary>
/// Ultra+ across the library: which games it supports, which have it installed, which have a newer build. Also the install,
/// update and uninstall flows the game tab and the Ultra+ page share (checks, dialogs, saving the state).
/// </summary>
public static class UltraPlusLibrary
{
    /// <summary>Something changed (lists read again, a mod installed or removed): pages redraw, the side pane's badge updates.</summary>
    public static event Action? Changed;

    static List<UltraEntry> _entries = [];
    public static IReadOnlyList<UltraEntry> Entries => _entries;
    public static int UpdateCount => _entries.Count(e => e.Update is not null);
    public static bool Loaded { get; private set; }

    /// <summary>Self-test only: uninstalls go ahead without asking (with the default answers).</summary>
    public static bool Unattended { get; set; }

    static bool GamePass(Game game) => game.Store == GameStore.Xbox;

    /// <summary>
    /// The Ultra+ game a library game is: the one picked by hand, else the best automatic match. A game Ultra+ has no mod of its
    /// own for gets nothing here (the game tab offers the generic builds).
    /// </summary>
    public static UltraMatch? Resolve(Game game, GameSettings? settings, IEnumerable<UltraGame> games)
    {
        var list = games.Concat(UltraPlus.Generic).ToList();
        if (settings?.UltraPlusKey is { } key && list.FirstOrDefault(g => g.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } picked)
            return new UltraMatch(picked, L.T("Picked by you"), 200);
        if (settings?.UltraPlus is { } st && list.FirstOrDefault(g => g.Key.Equals(st.GameKey, StringComparison.OrdinalIgnoreCase)) is { } installed)
            return new UltraMatch(installed, L.T("Installed in this game"), 150);
        var exe = settings is null ? game.MainExe : GameScanner.EffectiveExe(game, settings);
        var hit = UltraPlus.Match(games, game.Name, game.InstallDir, exe).FirstOrDefault();
        return hit is null ? null : hit with { Reason = L.T(hit.Reason) };
    }

    public static UltraEntry Describe(Game game, GameSettings? settings, UltraMatch match, List<UltraFile> files)
    {
        var exe = settings is null ? game.MainExe : GameScanner.EffectiveExe(game, settings);
        var layout = UltraPlus.Layout(match.Game, game.InstallDir, exe);
        var state = settings?.UltraPlus;
        var folder = UltraPlus.Inspect(layout, state);
        var versions = UltraPlus.Versions(match.Game, files, GamePass(game), AppState.Settings.UltraPlusBetas);
        UltraFile? update = state is not null ? UltraPlus.UpdateFor(state, versions, AppState.Settings.UltraPlusBetas)
            // Put there by the Ultra+ Manager or by hand: the version file tells which build it is.
            : folder.Installed && folder.Version is { Length: > 0 } v && versions.FirstOrDefault(f => !f.Beta) is { } newest && UltraPlus.Compare(newest.Version, v) > 0 ? newest
            : null;
        return new UltraEntry(game, match.Game, match.Reason, layout, state, folder, versions, update);
    }

    /// <summary>Reads the lists (from the cache unless <paramref name="force"/>) and works out every library game's state.</summary>
    public static async Task ScanAsync(bool force = false, CancellationToken ct = default)
    {
        if (force) await UltraPlus.RefreshAsync(ct);
        var games = await UltraPlus.GetGamesAsync(ct: ct);
        var files = await UltraPlus.GetModFilesAsync(ct: ct);
        // Each game's settings looked up here, on the UI thread: the worker below must not read the dictionary while the UI adds to it.
        var library = AppState.VisibleGames.Where(g => !g.NotInstalled).Select(g => (Game: g, Settings: AppState.Settings.Games.GetValueOrDefault(g.Id))).ToList();
        var entries = await Task.Run(() =>
        {
            var list = new List<UltraEntry>();
            foreach (var (g, s) in library)
            {
                try
                {
                    if (Resolve(g, s, games) is { } m && (!UltraPlus.IsGeneric(m.Game) || s?.UltraPlus is not null)) list.Add(Describe(g, s, m, files));
                }
                catch (Exception ex) { Log.Warn($"Ultra+ scan of {g.Name}: {ex.Message}"); }
            }
            return list.OrderByDescending(e => e.Update is not null).ThenByDescending(e => e.Installed).ThenBy(e => e.Game.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }, ct);
        _entries = entries;
        Loaded = true;
        RaiseChanged();
    }

    public static void RaiseChanged()
    {
        if (App.Window?.DispatcherQueue is { HasThreadAccess: false } q) q.TryEnqueue(() => Changed?.Invoke());
        else Changed?.Invoke();
    }

    // ---------- flows ----------

    static async Task<bool> ConfirmAsync(string title, UIElement content, string primary, string? secondary = null)
    {
        if (App.Window?.Content?.XamlRoot is not { } root) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (App.Window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    static TextBlock Text(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap };

    static string? Blocked(Game game, UltraLayout layout)
    {
        if (game.NotInstalled) return L.F("{0} is not installed.", game.Title);
        if (layout.ExeDir is null) return L.T("Pick the game exe first (the gear beside Play → Change game exe).");
        if (OptiInstaller.RunningGameProcess(layout.ExeDir) is { } running) return L.F("Close the game first: {0} is running from its folder.", running);
        return null;
    }

    /// <summary>
    /// Installs (or updates to, or goes back to) one build: download with its SHA-256 checked, the OptiScaler proxy check, the
    /// backup choice on a first install, then the files. Returns true when it went in.
    /// </summary>
    public static async Task<bool> InstallAsync(Game game, UltraGame ultra, UltraFile file, bool ownUe4ss, IProgress<double>? progress, CancellationToken ct, bool quiet = false)
    {
        // One change at a time per game (GameJobs): an automatic update, a click on the tab, an OptiScaler or Luma install must
        // not write the same folder together. A click while something runs there is said, not silently ignored.
        var job = GameJobs.TryStart(game.Id, game.Title, "ultra", "Ultra+", quiet);
        if (job is null) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Token);
        // The game page shows the job's progress (also a page opened again while it runs), and the caller's own if it has one.
        var both = new Progress<double>(p =>
        {
            progress?.Report(p);
            GameJobs.Report(job, p, p < 0.85 ? L.F("Downloading Ultra+ {0}… {1:0}%", file.Version, p / 0.85 * 100) : L.F("Putting Ultra+ {0} in the game folder…", file.Version));
        });
        try { return await InstallCoreAsync(game, ultra, file, ownUe4ss, both, linked.Token, quiet); }
        finally { GameJobs.End(job); }
    }

    /// <summary>Something is being installed or removed in this game now (Ultra+ or another mod: see GameJobs).</summary>
    public static bool Working(Game game) => GameJobs.Busy(game.Id);

    static async Task<bool> InstallCoreAsync(Game game, UltraGame ultra, UltraFile file, bool ownUe4ss, IProgress<double>? progress, CancellationToken ct, bool quiet)
    {
        var settings = AppState.Settings.For(game.Id);
        var exe = GameScanner.EffectiveExe(game, settings);
        var layout = UltraPlus.Layout(ultra, game.InstallDir, exe);
        if (Blocked(game, layout) is { } why)
        {
            App.Window?.ShowError(L.T("Could not install Ultra+"), why);
            return false;
        }
        var previous = settings.UltraPlus is { } p && p.GameKey.Equals(ultra.Key, StringComparison.OrdinalIgnoreCase) ? p : null;
        bool backup;
        if (previous is not null) backup = AppState.Settings.ModBackups != "replace";
        else if (await ModBackups.DecideAsync("Ultra+", game.Title) is { } b) backup = b;
        else return false;
        try
        {
            // The download is most of the wait: it fills the bar to 85%, putting the files in the rest.
            var zip = await UltraPlus.DownloadAsync(file, progress is null ? null : new Progress<double>(p => progress.Report(p * 0.85)), ct);
            if (UltraPlus.ProxyConflict(zip, layout, ownUe4ss) is { } clash)
            {
                App.Window?.ShowError(L.T("OptiScaler is in the way"), L.F("OptiScaler is installed as {0}, the file Ultra+ loads UE4SS through: one of the two would stop working. Reinstall OptiScaler with another file name (OptiScaler tab → Setup options → OptiScaler file name, e.g. dxgi.dll), then install Ultra+.", clash));
                return false;
            }
            // Another Ultra+ mod installed here before (picked by hand, a generic build): out first, laid out as its own game
            // says, so none of its files stay.
            if (settings.UltraPlus is { } other && previous is null)
            {
                var otherGame = (await UltraPlus.GetGamesAsync(ct: ct)).Concat(UltraPlus.Generic).FirstOrDefault(g => g.Key.Equals(other.GameKey, StringComparison.OrdinalIgnoreCase))
                    ?? new UltraGame { Key = other.GameKey };
                var problems = UltraPlus.Uninstall(other, UltraPlus.Layout(otherGame, other.Root.Length > 0 ? other.Root : game.InstallDir, exe), keepUe4ss: false);
                if (problems.Count > 0) Log.Warn($"Ultra+: removing {other.File} before {file.FileName}: {string.Join("; ", problems)}");
                settings.UltraPlus = null;
                SettingsStore.Save();
            }
            var state = await UltraPlus.InstallAsync(ultra, file, zip, layout, ownUe4ss, previous, backup, ct,
                progress is null ? null : new Progress<double>(p => progress.Report(0.85 + p * 0.15)));
            settings.UltraPlus = state;
            SettingsStore.Save();
            if (!quiet) App.Window?.ShowSuccess(previous is null
                ? L.F("Ultra+ {0} installed in {1}. Start the game: F12 applies changed settings in game.", file.Version, game.Title)
                : L.F("{0}: Ultra+ is now {1}. Your settings were kept.", game.Title, file.Version));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Log.Warn($"Ultra+ install for {game.Name}: {ex}");
            App.Window?.ShowError(L.T("Could not install Ultra+"), ex.Message);
            return false;
        }
        finally
        {
            await RescanOneAsync(game);
        }
    }

    public static async Task<bool> UninstallAsync(Game game)
    {
        var settings = AppState.Settings.For(game.Id);
        if (settings.UltraPlus is not { } state)
        {
            Log.Warn($"Ultra+ uninstall for {game.Name}: not installed by Hybrid Launcher");
            return false;
        }
        var job = GameJobs.TryStart(game.Id, game.Title, "ultra", "Ultra+");
        if (job is null)
        {
            Log.Warn($"Ultra+ uninstall for {game.Name}: another change is still running in its folder");
            return false;
        }
        try { return await UninstallCoreAsync(game, settings, state); }
        finally { GameJobs.End(job); }
    }

    static async Task<bool> UninstallCoreAsync(Game game, GameSettings settings, UltraPlusState state)
    {
        var games = await UltraPlus.GetGamesAsync();
        var ultra = games.Concat(UltraPlus.Generic).FirstOrDefault(g => g.Key.Equals(state.GameKey, StringComparison.OrdinalIgnoreCase))
            ?? new UltraGame { Key = state.GameKey };
        var layout = UltraPlus.Layout(ultra, state.Root.Length > 0 ? state.Root : game.InstallDir, GameScanner.EffectiveExe(game, settings));
        if (Blocked(game, layout) is { } why)
        {
            Log.Warn($"Ultra+ uninstall for {game.Name}: {why}");
            App.Window?.ShowError(L.T("Could not uninstall Ultra+"), why);
            return false;
        }
        var others = UltraPlus.Inspect(layout, state).OtherUe4ssMods;
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Text(L.F("Removes the {0} files Ultra+ added", state.Files.Count + state.Addons.Values.Sum(a => a.Files.Count))
            + (state.Addons.Count > 0 ? " " + L.F("(its {0} add-on(s) included)", state.Addons.Count) : "")
            + (state.Backups.Count > 0 ? ", " + L.F("and puts back the {0} game file(s) it replaced.", state.Backups.Count) : ".")
            + " " + L.T("Your Ultra+ settings stay with Hybrid Launcher and come back if you install it again.")));
        var keep = new CheckBox
        {
            Content = others.Count > 0 ? L.F("Keep UE4SS: other UE4SS mods use it ({0})", string.Join(", ", others)) : L.T("Keep UE4SS (for other UE4SS mods)"),
            IsChecked = others.Count > 0 || state.OwnUe4ss,
        };
        if (!state.OwnUe4ss) body.Children.Add(keep);
        if (!Unattended && !await ConfirmAsync(L.T("Uninstall Ultra+?"), body, L.T("Uninstall"))) return false;
        try
        {
            // Read on the UI thread: the check box cannot be touched from the worker.
            bool keepUe4ss = state.OwnUe4ss || keep.IsChecked == true;
            var problems = await Task.Run(() => UltraPlus.Uninstall(state, layout, keepUe4ss));
            settings.UltraPlus = null;
            SettingsStore.Save();
            if (problems.Count > 0) App.Window?.ShowError(L.T("Ultra+ removed, with problems"), string.Join("\n", problems.Take(8)));
            else App.Window?.ShowSuccess(L.F("Ultra+ removed from {0}; the folder is as it was.", game.Title));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Ultra+ uninstall for {game.Name}: {ex}");
            App.Window?.ShowError(L.T("Could not uninstall Ultra+"), ex.Message);
            return false;
        }
        finally
        {
            await RescanOneAsync(game);
        }
    }

    public static async Task<bool> InstallAddonAsync(Game game, UltraGame ultra, UltraFile file, IProgress<double>? progress, CancellationToken ct)
    {
        var settings = AppState.Settings.For(game.Id);
        if (settings.UltraPlus is not { } state) return false;
        var layout = UltraPlus.Layout(ultra, state.Root, GameScanner.EffectiveExe(game, settings));
        if (Blocked(game, layout) is { } why)
        {
            App.Window?.ShowError(L.T("Could not install the add-on"), why);
            return false;
        }
        var job = GameJobs.TryStart(game.Id, game.Title, "ultra", "Ultra+");
        if (job is null) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Token);
        ct = linked.Token;
        try
        {
            var name = UltraPlus.AddonName(ultra.Key, file.FileName);
            var zip = await UltraPlus.DownloadAsync(file, progress, ct);
            // Variants of one add-on ("EnhancedRayTracing", "EnhancedRayTracing_LessVRAM") are alternatives: the other one goes.
            var replaced = state.Addons.Keys.Where(k => !k.Equals(name, StringComparison.OrdinalIgnoreCase) && UltraPlus.AddonFamily(k).Equals(UltraPlus.AddonFamily(name), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in replaced) UltraPlus.UninstallAddon(state, k);
            var addon = await UltraPlus.InstallAddonAsync(ultra, file, zip, layout, state, ct);
            state.Addons[name] = addon;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("Add-on {0} installed.", UltraPlus.Spaced(name))
                + (replaced.Count > 0 ? " " + L.F("It replaces {0}.", string.Join(", ", replaced.Select(UltraPlus.Spaced))) : "")
                + " " + L.T("Start the game again to load it."));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not install the add-on"), ex.Message);
            return false;
        }
        finally
        {
            GameJobs.End(job);
            RaiseChanged();
        }
    }

    public static void UninstallAddon(Game game, string name)
    {
        var settings = AppState.Settings.For(game.Id);
        if (settings.UltraPlus is not { } state) return;
        // A running game holds its paks open: removing them now would half fail.
        if (Blocked(game, UltraPlus.Layout(new UltraGame { Key = state.GameKey }, state.Root, GameScanner.EffectiveExe(game, settings))) is { } why)
        {
            App.Window?.ShowError(L.T("Could not remove the add-on"), why);
            return;
        }
        UltraPlus.UninstallAddon(state, name);
        SettingsStore.Save();
        App.Window?.ShowSuccess(L.F("Add-on {0} removed.", UltraPlus.Spaced(name)));
        RaiseChanged();
    }

    /// <summary>
    /// Automatic updates (the Ultra+ page's switch): every installed mod with a newer build gets it, unless its game is running
    /// (it is tried again at the next check). Returns what was updated, as (game, version).
    /// </summary>
    public static async Task<List<(Game Game, string Version)>> AutoUpdateAsync(CancellationToken ct)
    {
        var done = new List<(Game, string)>();
        foreach (var e in _entries.Where(e => e.Update is not null && e.State is not null).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (e.Layout.ExeDir is not { } dir || OptiInstaller.RunningGameProcess(dir) is not null || Working(e.Game)) continue;
            if (await InstallAsync(e.Game, e.Ultra, e.Update!, e.State!.OwnUe4ss, null, ct, quiet: true))
            {
                Log.Info($"Ultra+: {e.Game.Name} updated automatically to {e.Update!.Version}");
                done.Add((e.Game, e.Update!.Version));
            }
        }
        return done;
    }

    /// <summary>Updates every game that has a newer build, one after another (the Ultra+ page's Update all).</summary>
    public static async Task<int> UpdateAllAsync(IProgress<(string Game, double Progress)>? progress, CancellationToken ct)
    {
        int done = 0;
        foreach (var e in _entries.Where(e => e.Update is not null && e.State is not null).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var title = e.Game.Title;
            if (await InstallAsync(e.Game, e.Ultra, e.Update!, e.State!.OwnUe4ss, new Progress<double>(p => progress?.Report((title, p))), ct, quiet: true)) done++;
        }
        return done;
    }

    /// <summary>One game's entry worked out again after a change (the rest stay as they are).</summary>
    static async Task RescanOneAsync(Game game)
    {
        try
        {
            var games = await UltraPlus.GetGamesAsync();
            var files = await UltraPlus.GetModFilesAsync();
            var s = AppState.Settings.Games.GetValueOrDefault(game.Id);
            var list = _entries.Where(e => e.Game.Id != game.Id).ToList();
            if (Resolve(game, s, games) is { } m && (!UltraPlus.IsGeneric(m.Game) || s?.UltraPlus is not null)) list.Add(Describe(game, s, m, files));
            _entries = list.OrderByDescending(e => e.Update is not null).ThenByDescending(e => e.Installed).ThenBy(e => e.Game.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) { Log.Warn($"Ultra+ rescan of {game.Name}: {ex.Message}"); }
        RaiseChanged();
    }
}
