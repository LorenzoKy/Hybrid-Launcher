using HybridLauncher.Core;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;

namespace HybridLauncher;

/// <summary>
/// One live page per recent game (2.0.0-rc.4). Until rc.3 a single game page was emptied and filled again on every switch, so
/// each game showed its banner, tabs and cards loading in (600-1200 ms of missed frames even going back to the game just left).
/// Now each of the last <see cref="GamePagesMax"/> games has a page of its own in <c>GameHost</c>, beside the Frame, never taken
/// out of the window: a switch hides one and shows the other, with nothing built, loaded or even laid out again (a page the
/// Frame takes out and puts back is laid out from scratch: 110-650 ms). The Frame shows an empty <see cref="GameShellPage"/>
/// meanwhile, so Back, Forward and every page change work as before. The game used longest ago gives its page up first.
/// </summary>
public sealed partial class MainWindow
{
    const int GamePagesMax = 8;

    readonly Dictionary<string, (GamePage Page, long Used)> _gamePages = new(StringComparer.OrdinalIgnoreCase);
    GamePage? _shownGame;
    /// <summary>The game page on screen (null when another page is shown).</summary>
    internal GamePage? ShownGamePage => Shown as GamePage;

    /// <summary>The page on screen: the game page while the Frame shows the game shell, else the Frame's page.</summary>
    object? Shown => ContentFrame.Content is GameShellPage ? _shownGame : ContentFrame.Content;

    /// <summary>Shows <paramref name="id"/>'s game page (as it was left, if it was shown before).</summary>
    public void NavigateGame(string id) => ContentFrame.Navigate(typeof(GameShellPage), id);

    /// <summary>The page on screen built again as it is now (a change of features, of the game's page settings).</summary>
    public void ReloadGame(string id)
    {
        GamePage.RequestFresh(id);
        NavigateGame(id);
    }

    /// <summary>The Frame arrived on the game shell for <paramref name="id"/>: that game's page is shown, made if it has none.</summary>
    internal void ShowGamePage(string? id)
    {
        if (id is null) return;
        using var __perf = Helpers.UiPerf.Scope("MainWindow.ShowGamePage");
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool made = false;
        if (!_gamePages.TryGetValue(id, out var entry))
        {
            entry = (new GamePage { Visibility = Visibility.Collapsed }, now);
            GameHost.Children.Add(entry.Page);
            made = true;
            DropOldestPages(GamePagesMax - 1);
        }
        _gamePages[id] = (entry.Page, now);
        var page = entry.Page;
        // Being prepared unseen (PrepareStep): it is simply shown now.
        if (ReferenceEquals(page, _preparing)) EndPreparing(show: true);
        if (_shownGame is not null && !ReferenceEquals(_shownGame, page)) _shownGame.Visibility = Visibility.Collapsed;
        _shownGame = page;
        page.Enter(id);
        page.Visibility = Visibility.Visible;
        GameHost.Visibility = Visibility.Visible;
        FadeInPage(page);
        if (made && Helpers.ScrollBars.Hidden)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Helpers.ScrollBars.Apply(page));
        // The games around this one are prepared next, while the user looks at it.
        StartPreparingPages();
        WatchMemory();
    }

    Microsoft.UI.Xaml.Controls.Grid? _propsLayer;
    // The page whose Properties are open over the window (null when none).
    GamePage? _propsOwner;

    /// <summary>
    /// A game's Properties, over the page on screen (2.1-rc.9): from the library or the list it opens right there, without going
    /// to the game's page. The game's page (kept, or made unseen) holds the window; it is left again once the window closes.
    /// </summary>
    public void ShowProperties(string id)
    {
        if (AppState.FindAny(id) is null) return;
        if (Shown is GamePage { GameId: var shown } here && string.Equals(shown, id, StringComparison.OrdinalIgnoreCase))
        {
            here.ShowProperties();
            return;
        }
        using var __perf = Helpers.UiPerf.Scope("MainWindow.ShowProperties");
        // Another game's Properties still open over the window: closed first, so its page lets go of the layer and is left.
        _propsOwner?.CloseProperties();
        bool prepared = _gamePages.TryGetValue(id, out var entry) && ReferenceEquals(entry.Page, _preparing);
        if (prepared)
        {
            _prepTimer?.Stop();
            EndPreparing(show: false);
        }
        GamePage page;
        if (entry.Page is { } kept) page = kept;
        else
        {
            // Made unseen, like a page prepared ahead (see PrepareStep): it stays in the window's pages for a later visit.
            page = new GamePage { Visibility = Visibility.Collapsed };
            GameHost.Children.Add(page);
            DropOldestPages(GamePagesMax - 1);
        }
        _gamePages[id] = (page, System.Diagnostics.Stopwatch.GetTimestamp());
        page.Enter(id);
        if (_propsLayer is null)
        {
            _propsLayer = new Microsoft.UI.Xaml.Controls.Grid
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)),
                IsTabStop = true, Visibility = Visibility.Collapsed,
            };
            // Under the title bar: its buttons stay usable.
            Microsoft.UI.Xaml.Controls.Grid.SetRow(_propsLayer, 1);
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_propsLayer, 50);
            RootGrid.Children.Add(_propsLayer);
        }
        _propsOwner = page;
        page.ShowPropertiesIn(_propsLayer, () =>
        {
            _propsOwner = null;
            // Not on screen: the page stops its work again (unless it became the page shown meanwhile, or is being prepared).
            if (!ReferenceEquals(page, _shownGame) && !ReferenceEquals(page, _preparing)) page.Leave();
        });
    }

    /// <summary>Self-test (snappy): the games whose page is ready (prepared or shown before).</summary>
    internal IEnumerable<string> ReadyGamePages => _gamePages.Where(kv => !ReferenceEquals(kv.Value.Page, _preparing)).Select(kv => kv.Key);

    /// <summary>
    /// Self-test "pages": every installed game opened in turn, three rounds, quick switches and idle pauses mixed (the pages
    /// prepared and the neighbours warmed meanwhile), with more games than <see cref="GamePagesMax"/> so pages are given up and
    /// their kept views taken by new ones. Fails on any UI error, an empty page, or more pages alive than the limit.
    /// </summary>
    internal async Task<int> SelfTestPageChurnAsync()
    {
        var ids = AppState.VisibleGames.Where(g => !g.NotInstalled).Select(g => g.Id).ToList();
        int errors = App.UiErrors, problems = 0;
        bool ignore = PrepareIgnoresInput;
        PrepareIgnoresInput = true;
        static long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true) / (1024 * 1024); }
        static long Private() { using var me = System.Diagnostics.Process.GetCurrentProcess(); return me.PrivateMemorySize64 / (1024 * 1024); }
        var memory = new List<string> { $"start {Managed()} MB managed / {Private()} MB private" };
        try
        {
            int rounds = int.TryParse(Environment.GetEnvironmentVariable("HL_SELFTEST_ROUNDS"), out var r) ? r : 3;
            for (int round = 0; round < rounds; round++, memory.Add($"round {round}: {Managed()} MB managed / {Private()} MB private"))
                for (int i = 0; i < ids.Count; i++)
                {
                    try { OpenGame(ids[i]); }
                    catch (Exception ex) { problems++; Log.Warn($"Pages: opening {AppState.Find(ids[i])?.Name} threw {ex.Message}"); }
                    // Mostly quick switches; every fourth game a pause long enough for pages and warm-ups to be made.
                    await Task.Delay(i % 4 == 3 ? 2500 : 350);
                    if (Shown is not GamePage { GameId: var shown } || !string.Equals(shown, ids[i], StringComparison.OrdinalIgnoreCase))
                    {
                        problems++;
                        Log.Warn($"Pages: {AppState.Find(ids[i])?.Name} not shown (round {round + 1})");
                    }
                }
        }
        finally { PrepareIgnoresInput = ignore; }
        int alive = GameHost.Children.Count;
        if (alive > GamePagesMax) { problems++; Log.Warn($"Pages: {alive} pages alive, limit {GamePagesMax}"); }
        // Leaks (2.0.0-rc.12): every page retired during the churn must be collectable. WinUI lets go of a removed tree's
        // managed wrappers over a few collections and frames, so they get some time first.
        int retired, leaked = 0;
        for (int tries = 0; tries < 6; tries++)
        {
            await Task.Delay(500);
            Managed();
            lock (Views.GamePage.Retired) { retired = Views.GamePage.Retired.Count; leaked = Views.GamePage.Retired.Count(w => w.TryGetTarget(out _)); }
            if (leaked == 0) break;
        }
        lock (Views.GamePage.Retired) retired = Views.GamePage.Retired.Count;
        // HL_SELFTEST_LEAKWAIT=<seconds>: time to take a heap dump from outside (dotnet-gcdump) when pages stay alive.
        if (leaked > 0 && int.TryParse(Environment.GetEnvironmentVariable("HL_SELFTEST_LEAKWAIT"), out var wait))
        {
            Log.Info($"Pages: waiting {wait} s for a heap dump");
            await Task.Delay(TimeSpan.FromSeconds(wait));
        }
        Log.Info($"Pages memory: {string.Join("; ", memory)}; {retired} pages retired, {leaked} still alive after a full collection");
        if (leaked > 1) { problems++; Log.Warn($"Pages: {leaked} of {retired} retired pages still alive (leak)"); }
        int newErrors = App.UiErrors - errors;
        bool ok = newErrors == 0 && problems == 0;
        Log.Info($"Self-test {(ok ? "ok" : "FAILED")}: pages ({ids.Count} games x 3, {newErrors} UI errors, {problems} problems, {alive} pages alive)");
        return ok ? 0 : 1;
    }

    // ---------- memory: nothing kept while the window is in the notification area ----------

    bool _memoryWatched;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _releaseTimer;

    /// <summary>
    /// The game pages, their kept views and the decoded banner pictures take memory (about 100 MB a page with its pictures):
    /// a minute after the window went to the notification area they are let go, all but the page on screen; they are made
    /// again in the quiet moments once the window is back.
    /// </summary>
    void WatchMemory()
    {
        if (_memoryWatched) return;
        _memoryWatched = true;
        _releaseTimer = DispatcherQueue.CreateTimer();
        _releaseTimer.Interval = TimeSpan.FromSeconds(60);
        _releaseTimer.IsRepeating = false;
        _releaseTimer.Tick += (_, _) => { if (Helpers.MediaPolicy.WindowHidden) ReleasePages(); };
        Helpers.MediaPolicy.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (Helpers.MediaPolicy.WindowHidden) { if (!_releaseTimer.IsRunning) _releaseTimer.Start(); }
            else
            {
                _releaseTimer.Stop();
                StartPreparingPages();
            }
        });
    }

    void ReleasePages()
    {
        int before = _gamePages.Count;
        EndPreparing(show: false);
        DropOldestPages(_shownGame is null ? 0 : 1);
        GamePage.DropKept();
        Helpers.ArtCache.Clear();
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
        Log.Info($"Game pages let go while in the notification area: {before - _gamePages.Count}");
    }

    /// <summary>The oldest pages go until <paramref name="keep"/> are left (never the one shown): their views are kept a while longer.</summary>
    void DropOldestPages(int keep)
    {
        while (_gamePages.Count > keep)
        {
            // Never the page shown, the one being prepared, or the one holding Properties over the window.
            var oldest = _gamePages.Where(kv => !ReferenceEquals(kv.Value.Page, _shownGame) && !ReferenceEquals(kv.Value.Page, _preparing) && !ReferenceEquals(kv.Value.Page, _propsOwner)).MinBy(kv => kv.Value.Used);
            if (oldest.Value.Page is null) return;
            _gamePages.Remove(oldest.Key);
            oldest.Value.Page.Retire();
            GameHost.Children.Remove(oldest.Value.Page);
        }
    }

    // ---------- pages prepared ahead of time ----------

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _prepTimer;
    GamePage? _preparing;
    string? _preparingId;
    int _prepStage;
    bool _hostShownForPrep;

    /// <summary>Self-test (snappy): no page is prepared while an action is being measured.</summary>
    internal static bool PreparingPaused;

    /// <summary>Self-test: pages are prepared even while the PC's mouse and keys are in use (a measured run must not depend on them).</summary>
    internal static bool PrepareIgnoresInput;

    /// <summary>
    /// While nobody uses the window, the pages of the games a hand is likely to open next (the ones around the open game in the
    /// list, or the games played last) are made unseen, one at a time, with their heavy tabs (Mods, the Dashboard) built and
    /// laid out: opening one of them later only shows it. Never more than <see cref="GamePagesMax"/> pages in all, and the ones
    /// the user opened are not pushed out for them.
    /// </summary>
    internal void StartPreparingPages()
    {
        if (_prepTimer is null)
        {
            _prepTimer = DispatcherQueue.CreateTimer();
            _prepTimer.Interval = TimeSpan.FromMilliseconds(500);
            _prepTimer.Tick += (_, _) => { using (Helpers.UiPerf.Scope("MainWindow.PrepareStep")) PrepareStep(); };
        }
        _prepTimer.Start();
    }

    void PrepareStep()
    {
        if (App.ShuttingDown) { _prepTimer?.Stop(); return; }
        if (PreparingPaused) return;
        bool idle = PrepareIgnoresInput || (!Helpers.FrameWarmth.Awake && GamePage.UserIdleMs() > 1500);
        if (!idle || Helpers.MediaPolicy.Quiet) return;
        if (_preparing is { } page)
        {
            // One stage per idle tick: the page itself, then its Mods tab, then back to the tab it opens on.
            _prepStage++;
            if (_prepStage == 3) page.ShowTabQuietly("mods");
            else if (_prepStage == 8) page.ShowTabQuietly(AppState.Settings.GameTab);
            else if (_prepStage >= 10) EndPreparing(show: false);
            return;
        }
        if (_gamePages.Count >= GamePagesMax - 1) { _prepTimer?.Stop(); return; }
        string? around = (Shown as GamePage)?.GameId ?? AppState.Settings.LastGameId;
        var next = Candidates(around).FirstOrDefault(id => !_gamePages.ContainsKey(id));
        if (next is null) { _prepTimer?.Stop(); return; }
        // Unseen and untouchable, behind the page on screen; laid out at the window's size as if shown.
        // Hidden by its composition visual, not by the element's Opacity (2.0.0-rc.9): XAML does not draw an element whose Opacity
        // is 0, so a page prepared that way was laid out but never drawn, and its first drawing (text, pictures) was 120-280 ms
        // of missed frames on the click that showed it. A visual at 0 is drawn by XAML and only left out when composed; showing
        // the page fades that same visual in (FadeInPage).
        var made = new GamePage { IsHitTestVisible = false, Unseen = true };
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(made).Opacity = 0;
        GameHost.Children.Insert(0, made);
        _gamePages[next] = (made, 0);
        _preparing = made;
        _preparingId = next;
        _prepStage = 0;
        if (GameHost.Visibility != Visibility.Visible)
        {
            GameHost.Visibility = Visibility.Visible;
            _hostShownForPrep = true;
        }
        made.Enter(next);
        Log.Info($"Game page prepared ahead: {AppState.Find(next)?.Name ?? next}");
    }

    /// <summary>The games to prepare, likeliest first: around the open game in the list, then the ones played last.</summary>
    List<string> Candidates(string? around)
    {
        var list = new List<string>();
        if (around is not null && AppState.Find(around) is not null)
        {
            list.Add(around);
            list.AddRange(GamesNear(around, 4));
        }
        list.AddRange(AppState.VisibleGames.Where(g => !g.NotInstalled && AppState.Settings.For(g.Id).LastPlayed is not null)
            .OrderByDescending(g => AppState.Settings.For(g.Id).LastPlayed).Select(g => g.Id).Take(4));
        return list.Where(id => AppState.Find(id) is { NotInstalled: false }).Distinct(StringComparer.OrdinalIgnoreCase).Take(GamePagesMax - 1).ToList();
    }

    void EndPreparing(bool show)
    {
        if (_preparing is not { } page) return;
        _preparing = null;
        page.Unseen = false;
        page.IsHitTestVisible = true;
        if (!show)
        {
            page.Leave();
            page.Visibility = Visibility.Collapsed;
            if (_hostShownForPrep && _shownGame is null) GameHost.Visibility = Visibility.Collapsed;
        }
        _hostShownForPrep = false;
    }

    /// <summary>The Frame left the game shell: the game page stops its work; it is hidden unless another game's page follows.</summary>
    internal void LeaveGamePage(bool toAnotherGame)
    {
        if (_shownGame is null) return;
        _shownGame.Leave();
        if (toAnotherGame) return;
        _shownGame.Visibility = Visibility.Collapsed;
        // A page being prepared keeps the host shown (unseen) until it is done.
        if (_preparing is null) GameHost.Visibility = Visibility.Collapsed;
        else _hostShownForPrep = true;
        _shownGame = null;
    }

    /// <summary>The last entry of Back goes (a page opened again in place of itself must not be gone back to).</summary>
    internal void DropLastBack()
    {
        if (ContentFrame.BackStack.Count > 0) ContentFrame.BackStack.RemoveAt(ContentFrame.BackStack.Count - 1);
    }

    /// <summary>A game's page is dropped (the game is gone from the list, or the app's look changed): made again on its next visit.</summary>
    internal void ForgetGamePage(string id)
    {
        if (!_gamePages.TryGetValue(id, out var entry)) return;
        // The page on screen (or holding Properties over the window) stays where it is (else it stayed in GameHost for good beside its replacement): built again next time.
        if (ReferenceEquals(entry.Page, _shownGame) || ReferenceEquals(entry.Page, _propsOwner)) { GamePage.RequestFresh(id); return; }
        _gamePages.Remove(id);
        if (ReferenceEquals(entry.Page, _preparing)) EndPreparing(show: false);
        entry.Page.Retire();
        GameHost.Children.Remove(entry.Page);
    }
}
