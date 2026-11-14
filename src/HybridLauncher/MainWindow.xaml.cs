using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Net;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;

namespace HybridLauncher;

public sealed partial class MainWindow : Window
{
    string _filter = "";
    bool _rebuilding;
    DispatcherTimer? _toastTimer;

    public MainWindow()
    {
        InitializeComponent();
        // Room for every main page and a page per recent game (MainWindow.GameSlots): none is dropped and built again.
        ContentFrame.CacheSize = 24;
        Loc.Apply(RootGrid);
        // Interface size (GitHub issue #7) and hidden scroll bars (#8), Settings → Appearance.
        Helpers.UiScale.Attach(this, RootGrid, AppState.Settings.InterfaceSize);
        Helpers.ScrollBars.UpdateStyles();
        RootGrid.Loaded += (_, _) => { if (Helpers.ScrollBars.Hidden) Helpers.ScrollBars.Apply(RootGrid); };
        RootGrid.SizeChanged += (_, e) => Helpers.ScrollBars.FitToWindow(e.NewSize.Width * Helpers.UiScale.Factor);
        Title = AppInfo.Name;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Windows' own caption buttons are hidden (they were 48 px in a 58 px bar, top-aligned, and hard to hit): Hybrid Launcher draws
        // its own, full height (CaptionButtons in the title bar).
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPresenterChange || e.DidSizeChange) UpdateMaximizeButton();
            if (e.DidSizeChange || e.DidPositionChange) QueueMaximizeCheck();
        };
        // A maximized window whose screen changes resolution (a 1440p screen set to 1080p, a game or a TV switching it) can stay
        // "maximized" at its old size: the page was cut off on the right. Checked again after every display change.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CaptionButtons.SizeChanged += (_, _) => UpdateTitleBarRegions();
        StartGamepad();
        StartHomeButton();
        // A game in front keeps the focus: a Hybrid Launcher window that takes it by itself gives it back (and the log says which).
        Helpers.FocusGuard.Start();
        Closed += (_, _) => Helpers.FocusGuard.Stop();
        GuardSearchFocus();
        Helpers.GameUpdater.Start(DispatcherQueue);
        Helpers.EpicWeb.Start(DispatcherQueue);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        var scale = (RootGrid.XamlRoot?.RasterizationScale) ?? GetDpiScale();
        AppWindow.Resize(new SizeInt32((int)(1320 * scale), (int)(880 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = (int)(560 * scale);
            p.PreferredMinimumHeight = (int)(480 * scale);
        }
        // The size and maximized state of the last session (clamped to this screen by CenterOnScreen).
        var st = AppState.Settings;
        if (st.WindowWidth is > 400 and var ww && st.WindowHeight is > 300 and var wh) AppWindow.Resize(new SizeInt32(ww, wh));
        CenterOnScreen();
        // Maximized again only when the window is first shown: Maximize() on a window not shown yet shows it, which opened the
        // window at sign-in although Hybrid Launcher was set to start in the notification area.
        _maximizeOnShow = st.WindowMaximized;

        ApplyTheme(AppState.Settings.Theme);
        // Not while ApplyTheme flips the theme twice: it paints once itself, after (2.1 audit: three paints per change).
        RootGrid.ActualThemeChanged += (_, _) => { if (_applyingTheme) return; UpdateCaptionButtons(); UpdateTint(); };
        RootGrid.SizeChanged += (_, _) => QueueBackdropRepaint();
        WatchWindowsColours();
        Http.GitHubLimitReached += OfferGitHubSignIn;
        HybridLauncher.Core.Media.ArtOverrides.Changed += OnArtChanged;

        // The built-in item follows the Windows language; keep the UI in one language.
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SettingsItem is NavigationViewItem s) { s.Content = L.T("Settings"); PaneTip(s, null); }
            // The footer's items say what they are beside their icon: no tip of their own, and none from NavigationView
            // over the page while the list is folded.
            PaneTip(FriendsItem, null);
            PaneTip(AddGameItem, null);
        };

        // In the narrow (minimal) mode the menu button floats over the content: leave it room.
        Nav.DisplayModeChanged += (_, e) =>
        {
            PageHost.Margin = e.DisplayMode == NavigationViewDisplayMode.Minimal ? new Thickness(0, 40, 0, 0) : new Thickness(0);
            // On narrow windows the pane floats over the page: then only the stock menu button can bring it back.
            Nav.IsPaneToggleButtonVisible = e.DisplayMode == NavigationViewDisplayMode.Minimal;
            UpdatePaneButton();
            UpdateGrip();
            UpdateTitleBrand();
        };
        // Applied right after the event, not inside it: these fire inside NavigationView's own IsPaneOpen setter, and collapsing
        // the search box there while it had the focus made the setter fail now and then (COMException E_FAIL, self-test
        // "pane folded" after "search by Ctrl+F").
        Nav.PaneOpening += (_, _) => DispatcherQueue.TryEnqueue(() => { UpdatePaneButton(true); UpdatePageTitle(); UpdateGrip(true); });
        Nav.PaneClosing += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (SearchHasFocus()) ContentFrame.Focus(FocusState.Programmatic);
                UpdatePaneButton(false);
                UpdatePageTitle();
                UpdateGrip(false);
            });
        };
        // Page changes: no slide (WinUI's 300 ms entrance started while the new page was still being built, and stuttered);
        // the new page fades in over 90 ms on the compositor instead, which keeps the display's full refresh rate. The main pages
        // stay in memory (NavigationCacheMode), so going back to them is instant.
        ContentFrame.ContentTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
        {
            new Microsoft.UI.Xaml.Media.Animation.NavigationThemeTransition { DefaultNavigationTransitionInfo = new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo() },
        };
        ContentFrame.Navigated += (_, e) => { if (e.Content is UIElement page) FadeInPage(page); };
        // From the first page on, the games likely to be opened next get their pages made in the quiet moments (GameSlots).
        ContentFrame.Navigated += (_, _) => StartPreparingPages();
        // The frame clock stays awake while the mouse is in use over the window: a wheel answers at the next refresh.
        var warmthHost = new Border { Width = 1, Height = 1, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        RootGrid.Children.Add(warmthHost);
        Helpers.FrameWarmth.Attach(RootGrid, warmthHost);
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) Helpers.FrameWarmth.WindowActivated(); };
        // Stalls of the UI thread and slow clicks go to the log (Helpers.UiPerf; every click with HL_PERF=1 or the self-test).
        Helpers.UiPerf.Attach(DispatcherQueue, RootGrid, () => Shown is GamePage { GameId: { } gid } ? "game page " + (AppState.Find(gid)?.Name ?? gid) : Shown?.GetType().Name ?? "no page");
        // The game list beside the pages glides on the mouse wheel too (its scroller is a part of the NavigationView's template).
        Nav.Loaded += (_, _) =>
        {
            var queue = new Queue<DependencyObject>([Nav]);
            while (queue.Count > 0)
            {
                var d = queue.Dequeue();
                if (d is ScrollViewer { Name: "MenuItemsScrollViewer" } menu) { Helpers.SmoothWheel.Attach(menu); break; }
                for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i));
            }
        };
        // The game list's width: the saved one, changed by dragging its edge.
        PaneGrip.Relative = Nav;
        PaneGrip.Dragged += x => SetPaneWidth(x);
        PaneGrip.Released += () => SettingsStore.Save();
        Nav.SizeChanged += (_, _) => SetPaneWidth(Nav.OpenPaneLength, save: false);
        SetPaneWidth(AppState.Settings.SidebarWidth, save: false);
        TitleBrand.SizeChanged += (_, _) => UpdatePageTitle();
        // The logo is the way home: the library as a grid of covers.
        TitleBrand.Clicked += () =>
        {
            try
            {
                if (Shown is not LibraryPage) ContentFrame.Navigate(typeof(LibraryPage), _filter);
                SyncSelection();
            }
            catch (Exception ex) { ShowError(L.T("Could not open the library"), ex.Message, ex); }
        };
        Nav.Loaded += (_, _) => UpdateTitleBrand();
        // The page reads its game in OnNavigatedTo, after this event: look once it has.
        ContentFrame.Navigated += (_, _) => DispatcherQueue.TryEnqueue(UpdatePageTitle);
        // Lists inside a new page take the scroll bar setting once they are on screen (their templates are built by then).
        ContentFrame.Navigated += (_, e) =>
        {
            if (Helpers.ScrollBars.Hidden && e.Content is FrameworkElement page)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Helpers.ScrollBars.Apply(page));
        };
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        PageTitle.SizeChanged += (_, _) => UpdateTitleBarRegions();
        // Mouse back / forward buttons (and Alt+Left / Alt+Right) walk the page history, like a browser.
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnMouseNavigate), true);
        AddHistoryKeys();

        AppState.GamesChanged += RebuildNav;
        AppState.PinsChanged += () => { RebuildNav(); RefreshLibrary(); };
        // The list sorts and filters like the library: an order or filter that needs play time, achievements or tags
        // is applied again once those are read (in the background, after the games are found).
        AppState.GamesChanged += () => _ = Library.LoadStatsAsync(DispatcherQueue);
        Library.StatsReady += () =>
        {
            var f = Library.ListFilter;
            if ((f.Sort is not "name" and not "added" || f.Tech.Count > 0) && Library.OrderKey(f) != _navOrder) RebuildNav();
        };
        // The owned games' stats change only their own order (not installed, shown, sorted by what the stats hold).
        Library.OwnedStatsReady += () =>
        {
            var f = Library.ListFilter;
            if (f.ShowNotInstalled && f.Sort is not "name" and not "added" and not "tech") RebuildNav();
        };
        // Closing keeps Hybrid Launcher in the notification area (Settings, Features); Quit there, or the setting off, really closes it.
        AppWindow.Closing += (_, e) =>
        {
            if (_quitting || !AppState.Settings.CloseToTray)
            {
                if (CloudSync.IsSending && !_cloudWaitStarted)
                {
                    e.Cancel = true;
                    QuitAfterCloudAsync().ContinueWith(t => Log.Warn($"Quit after cloud: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
                }
                return;
            }
            e.Cancel = true;
            HideToTray();
        };
        Closed += (_, _) =>
        {
            App.WatchShutdown();
            _closed = true;
            SaveWindowState();
            AchievementWatcher.Stop();
            AchievementPopup.CloseAll();
            Helpers.EpicWeb.Shutdown();
            SettingsStore.Save();
            SettingsStore.Flush();
            _tray?.Dispose();
            _tray = null;
            _configTimer?.Stop();
            if (!App.SelfTesting) HybridLauncher.Core.Cloud.ConfigSync.PushAtExit(TimeSpan.FromSeconds(2.5));
            App.ExitForSure();
        };
        GameSessions.HdrProblem += message => ShowError(L.T("Smart HDR"), message);
        GameSessions.Changed += _ => UpdateTrayTip();
        GameSessions.Changed += MarkRunning;
        GameSessions.Changed += HideWhilePlaying;
        GameSessions.Changed += id => { if (GameSessions.State(id) == GameRunState.Stopped) DispatcherQueue.TryEnqueue(Helpers.TaskbarJumpList.Update); };
        if (AppState.Settings.CloseToTray) EnsureTray();
        ShowLoading();
        _ = StartAsync();
        AchievementWatcher.Start(DispatcherQueue);
        UnlockExtras.Start();
    }

    // ---------- start, notification area ----------

    Tray? _tray;

    /// <summary>Takes the icon out of the notification area from any thread (UiRecovery, before a stopped copy ends).</summary>
    internal void DropTrayIcon() => _tray?.RemoveIcon();

    bool _quitting;
    bool _hidden;
    // The window went to the notification area by itself for a game (Settings → General): it comes back when the last game closes,
    // unless the user opened it meanwhile.
    bool _hiddenForGame;
    DateTime _hiddenForGameAt;

    /// <summary>
    /// A game started from Hybrid Launcher is up (its process found, not just asked to start): the window steps aside to the
    /// notification area, so it takes nothing from the game. When every game has closed it opens again on the page it was on.
    /// </summary>
    void HideWhilePlaying(string id)
    {
        try
        {
            if (_closed || _quitting) return;
            if (GameSessions.State(id) == GameRunState.Running && GameSessions.WasLaunched(id))
            {
                // The window's real state, not only the flag: a window shown some other way while the flag said hidden was left
                // on screen over the game.
                if (AppWindow.IsVisible) _hidden = _hiddenForGame = false;
                if (!AppState.Settings.HideWhilePlaying || _hidden || _hiddenForGame) return;
                Log.Info($"Window to the notification area while {id} runs");
                HideToTray();
                _hiddenForGame = true;
                _hiddenForGameAt = DateTime.UtcNow;
            }
            else if (_hiddenForGame && GameSessions.Active().Count == 0)
            {
                // A game gone within seconds of starting has often only restarted itself (a launcher exe, Steam's wrapper,
                // anti-cheat): the window waits a little, so it does not flash back between the two. Those restarts come in the
                // first seconds; a game played longer brings the window back at once (1.4.0-rc.20: was 60 s then 20 s, and
                // the window looked as if it never came back).
                if (DateTime.UtcNow - _hiddenForGameAt < TimeSpan.FromSeconds(12))
                {
                    var at = _hiddenForGameAt;
                    Log.Info("The game closed soon after starting: window back in 8 s unless it starts again");
                    Later.Run(DispatcherQueue, TimeSpan.FromSeconds(8), () =>
                    {
                        if (_closed || _quitting) return;
                        if (!_hiddenForGame || at != _hiddenForGameAt) { Log.Info("Window back after the game: already brought back or hidden again"); return; }
                        if (GameSessions.Active() is { Count: > 0 } active) { Log.Info($"Window back after the game: still waiting, {string.Join(", ", active)} running"); return; }
                        BackFromGame("The game closed soon after starting and did not come back");
                    });
                    return;
                }
                BackFromGame("Every game closed");
            }
        }
        catch (Exception ex) { Log.Warn($"Hide while playing: {ex.Message}"); }
    }

    /// <summary>
    /// The window back from the notification area after the last game closed, in front unless another app is (2.0: the focus
    /// guard, which hands the foreground back to a game the window takes it from without input, gave it back to the closing
    /// game's last window or to Steam, and the window stayed behind). Checked again a moment later and brought forward once more
    /// if something took the front meanwhile.
    /// </summary>
    void BackFromGame(string why)
    {
        bool activate = NothingElseInFront();
        Log.Info($"{why}: window back from the notification area{(activate ? ", in front" : ", behind the app in front")}");
        if (activate) Helpers.FocusGuard.Allow(4000);
        ShowFromTray(activate);
        if (!activate) return;
        Later.Run(DispatcherQueue, TimeSpan.FromMilliseconds(1200), () =>
        {
            if (_closed || _quitting || _hidden || GameSessions.Active().Count > 0) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (GetForegroundWindow() == hwnd || !NothingElseInFront()) return;
            Log.Info("Window back after the game: not in front, brought forward again");
            Helpers.FocusGuard.Allow(2500);
            ShowFromTray(activate: true);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    /// <summary>
    /// After a game closed: true when nothing the user is busy with is in front (no window, the desktop or taskbar, a game
    /// launcher, Hybrid Launcher itself), so the window comes back in front, as a console's home screen does. Another app in
    /// front (a browser, a chat) keeps it: the window then comes back behind it.
    /// </summary>
    static bool NothingElseInFront()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0) return true;
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = p.ProcessName.ToLowerInvariant();
            return name is "explorer" or "steam" or "steamwebhelper" or "epicgameslauncher" or "galaxyclient" or "galaxyclient helper"
                or "ubisoftconnect" or "upc" or "eadesktop" or "launcher" or "xboxpcapp" or "gamebar" or "shellexperiencehost"
                or "searchhost" or "startmenuexperiencehost" or "lockapp" or "applicationframehost";
        }
        catch { return true; }
    }

    async Task StartAsync()
    {
        // Downloads: a finished install shows up as an installed game; the pane's badge counts the running ones.
        HybridLauncher.Core.Accounts.Downloads.IsInstalled = id => AppState.Find(id) is { Installing: false };
        HybridLauncher.Core.Accounts.Downloads.Finished = () => _ = AppState.RescanAsync();
        HybridLauncher.Core.Accounts.Downloads.Installed += id => _ = ShortcutAfterInstallAsync(id);
        HybridLauncher.Core.Accounts.Downloads.Changed += UpdateDownloadsBadge;
        HybridLauncher.Core.Accounts.Downloads.Watch();
        await AppState.RescanAsync();
        // The taskbar button's right-click list: the games played last, each started through Hybrid Launcher.
        Helpers.TaskbarJumpList.Update();
        // Every game is watched, however it is started (Steam, a shortcut, its own exe, before Hybrid Launcher opened): the list is a
        // snapshot made here on the UI thread after each scan, so the watcher's thread never touches the settings.
        List<(Game, GameSettings)> watched = [];
        void Snapshot() => watched = AppState.Games.Select(g => (g, AppState.Settings.For(g.Id))).ToList();
        Snapshot();
        AppState.GamesChanged += Snapshot;
        GameSessions.WatchAll(() => watched);
        // Cloud saves: a game that closes sends its changed saves (Play brings newer ones down first, in AppState.LaunchAsync).
        CloudSync.Start();
        // "Hybrid Config and Stats": Hybrid Launcher's own settings and numbers go to the same cloud when they changed, every
        // fifteen minutes (the first a couple of minutes after start), off the UI thread, and once more when the app closes.
        if (!App.SelfTesting)
        {
            _configTimer = DispatcherQueue.CreateTimer();
            _configTimer.Interval = TimeSpan.FromMinutes(2);
            _configTimer.Tick += (t, _) =>
            {
                t.Interval = TimeSpan.FromMinutes(15);
                if (HybridLauncher.Core.Cloud.ConfigSync.On) _ = Task.Run(() => HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None));
            };
            _configTimer.Start();
        }
        // Steam friends: the footer item follows the sign-in, its badge the friends online (read once in the background).
        FriendsView.CountChanged += UpdateFriendsItem;
        OwnedLibrary.Loaded += UpdateFriendsItem;
        UpdateFriendsItem();
        MaybeStartTour();
        // The app's own updates: a quiet look at GitHub a few seconds after start; a newer version asks once, and the blue button
        // beside Minimize stays until it is installed.
        InitInbox();
        Helpers.AppUpdater.Changed += () => DispatcherQueue.TryEnqueue(UpdateUpdateButton);
        UpdateUpdateButton();
        if (Content?.XamlRoot is { } root) _ = Helpers.AppUpdater.CheckAtStartAsync(root);
        // New versions of the OptiScaler builds the user follows (each turned on in a game's OptiScaler tab).
        OptiFollowChanged();
        // Ultra+: the installed mods with a newer build (a notification when the user asked for one).
        UltraChecksChanged();
        if (SteamAccount.SignedIn) _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => _ = (_friends ??= new FriendsView()).LoadAsync(quiet: true)), TaskScheduler.Default);
        // Every game's page data fetched ahead of the click, in the background (Helpers.WarmUp).
        Helpers.WarmUp.Start(DispatcherQueue);
        // The cache stays within its budget (game pages load from it); in the background, once things have settled.
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => CacheJanitor.RunAsync(), TaskScheduler.Default);
        // HDR Hybrid Launcher turned on for a game that closed while Hybrid Launcher was not running goes back off.
        try { GameSessions.RestoreLeftovers(AppState.Find); }
        catch (Exception ex) { Log.Warn($"Smart HDR restore: {ex.Message}"); }
    }

    static void FadeInPage(UIElement page)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(page);
        var c = visual.Compositor;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f)));
        fade.Duration = TimeSpan.FromMilliseconds(90);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>A desktop shortcut (HybridLauncher.exe --launch id): starts the game once the library is known, the window stays as it is.</summary>
    public async void LaunchFromShortcut(string id)
    {
        try
        {
            if (AppState.Find(id) is null) await AppState.Scanned;
            var game = AppState.Find(id);
            if (game is null)
            {
                ShowFromTray();
                ShowError(L.T("Game not found"), L.T("The shortcut points to a game Hybrid Launcher no longer finds. Make a new shortcut from the game's page."));
                return;
            }
            if (GameSessions.IsActive(game.Id)) return;
            if (game.Installing)
            {
                ShowFromTray();
                OpenGame(game.Id);
                return;
            }
            await AppState.LaunchAsync(game);
        }
        catch (Exception ex)
        {
            ShowFromTray();
            ShowError(L.T("The game did not start"), ex.Message, ex);
        }
    }

    void EnsureTray()
    {
        if (_tray is not null) return;
        try { _tray = new Tray(AppInfo.Name, ShowFromTray, Quit); }
        catch (Exception ex) { Log.Error("Tray icon", ex); }
        UpdateTrayTip();
    }

    void UpdateTrayTip()
    {
        if (_tray is null) return;
        var running = GameSessions.Active().Select(AppState.Find).Where(g => g is not null).Select(g => g!.Title).ToList();
        _tray.Update(running.Count == 0 ? AppInfo.Name : $"{AppInfo.Name} · " + L.F("playing {0}", string.Join(", ", running)));
    }

    bool _maximizeOnShow;

    /// <summary>The first time the window shows: maximized again when it was maximized in the last session.</summary>
    void ApplyMaximizeOnShow()
    {
        if (!_maximizeOnShow) return;
        _maximizeOnShow = false;
        if (AppWindow.Presenter is OverlappedPresenter op) op.Maximize();
    }

    /// <summary>A normal start: the window opens (maximized when it was last time).</summary>
    public void ShowAtStart()
    {
        ApplyMaximizeOnShow();
        Activate();
    }

    /// <summary>Started with Windows: only the icon in the notification area; the window opens from there.</summary>
    public void StartInTray()
    {
        EnsureTray();
        _hidden = true;
    }

    bool _cloudWaitStarted;

    /// <summary>
    /// Quitting while saves are still on their way to the cloud (sent in the background since Play): the window goes away at
    /// once and Hybrid Launcher closes when they are there, three minutes at most. Quitting again meanwhile closes it at once.
    /// </summary>
    async Task QuitAfterCloudAsync()
    {
        _cloudWaitStarted = true;
        Log.Info("Quit: waiting for the saves still going to the cloud");
        (Shown as GamePage)?.StopMedia();
        AppWindow.Hide();
        _hidden = true;
        await CloudSync.WaitForSendingAsync(TimeSpan.FromMinutes(3));
        Quit();
    }

    void HideToTray()
    {
        SaveWindowState();
        SettingsStore.Save();
        EnsureTray();
        // A trailer must not keep playing in a window nobody sees.
        (Shown as GamePage)?.StopMedia();
        AppWindow.Hide();
        _hidden = true;
        MediaPolicy.SetWindowHidden(true);
    }

    /// <summary>Brings the window back (tray icon, a second start of Hybrid Launcher).</summary>
    public void ShowFromTray() => ShowFromTray(activate: true);

    /// <param name="activate">False when the app brings the window back by itself: it shows behind what is in front (a game in
    /// full screen keeps the focus) instead of taking the focus.</param>
    public void ShowFromTray(bool activate)
    {
        _hiddenForGame = false;
        try
        {
            if (!activate)
            {
                if (_hidden)
                {
                    AppWindow.Show(false);
                    _hidden = false;
                    if (_maximizeOnShow) { _maximizeOnShow = false; if (AppWindow.Presenter is OverlappedPresenter mp) mp.Maximize(); }
                }
                MediaPolicy.SetWindowHidden(AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized });
                return;
            }
            if (_hidden)
            {
                AppWindow.Show();
                _hidden = false;
                ApplyMaximizeOnShow();
                // Started in the notification area after an update: the new version's welcome when the window first opens.
                MaybeShowWhatsNew();
            }
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
            Activate();
            Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            // Out of the notification area the clips and the reflection start again (a window that never opened sends no event).
            MediaPolicy.SetWindowHidden(AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized });
        }
        catch (Exception ex) { Log.Warn($"Show window: {ex.Message}"); }
    }

    /// <summary>
    /// Quits for real (tray menu): the window closes, the icon goes, the app ends. Run after the tray's menu has returned (not
    /// from inside its window procedure), and the process is made to end even when something still holds it: a hidden
    /// window closed from the tray used to leave Hybrid Launcher frozen in Task Manager.
    /// </summary>
    public void Quit()
    {
        if (CloudSync.IsSending && !_cloudWaitStarted) { _ = QuitAfterCloudAsync(); return; }
        _quitting = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            try { Close(); }
            catch (Exception ex) { Log.Warn($"Quit: {ex.Message}"); }
            App.ExitForSure();
        });
    }

    // ---------- caption buttons ----------

    void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
    }

    void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter p) return;
        if (p.State == OverlappedPresenterState.Maximized) p.Restore();
        else p.Maximize();
    }

    /// <summary>The same path as Alt+F4 (so "close to the notification area" applies).</summary>
    void CloseWindow_Click(object sender, RoutedEventArgs e) =>
        Native.PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x0112 /* WM_SYSCOMMAND */, 0xF060 /* SC_CLOSE */, 0);

    void UpdateMaximizeButton()
    {
        if (_closed || AppWindow.Presenter is not OverlappedPresenter p) return;
        bool max = p.State == OverlappedPresenterState.Maximized;
        MaximizeIcon.Glyph = max ? "\uE923" : "\uE922";
        ToolTipService.SetToolTip(MaximizeButton, max ? L.T("Restore") : L.T("Maximize"));
    }

    void SaveWindowState()
    {
        if (_hidden || AppWindow.Presenter is not OverlappedPresenter wp) return;
        AppState.Settings.WindowMaximized = wp.State == OverlappedPresenterState.Maximized;
        if (wp.State == OverlappedPresenterState.Restored)
        {
            AppState.Settings.WindowWidth = AppWindow.Size.Width;
            AppState.Settings.WindowHeight = AppWindow.Size.Height;
        }
    }

    // ---------- history: back and forward ----------

    void OnMouseNavigate(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(RootGrid).Properties;
        if (props.IsXButton1Pressed) { GoBack(); e.Handled = true; }
        else if (props.IsXButton2Pressed) { GoForward(); e.Handled = true; }
    }

    void AddHistoryKeys()
    {
        var back = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Left, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
        back.Invoked += (_, a) => { a.Handled = true; GoBack(); };
        var forward = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Right, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
        forward.Invoked += (_, a) => { a.Handled = true; GoForward(); };
        // Ctrl+F: the game list's search (the list opens first if it is folded).
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += (_, a) =>
        {
            a.Handled = true;
            // On Settings, its own search through every tab.
            if (Shown is SettingsPage settings) { settings.FocusSearch(); return; }
            if (!Nav.IsPaneOpen) Nav.IsPaneOpen = true;
            _searchWanted = true;
            DispatcherQueue.TryEnqueue(() => { SearchBox.Focus(FocusState.Keyboard); _searchWanted = false; });
        };
        RootGrid.KeyboardAccelerators.Add(back);
        RootGrid.KeyboardAccelerators.Add(forward);
        RootGrid.KeyboardAccelerators.Add(find);
        RootGrid.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>The page before (the empty start page does not count).</summary>
    public void GoBack()
    {
        try
        {
            while (ContentFrame.BackStack.Count > 0 && ContentFrame.BackStack[^1].SourcePageType == typeof(HomePage))
                ContentFrame.BackStack.RemoveAt(ContentFrame.BackStack.Count - 1);
            if (!ContentFrame.CanGoBack) return;
            ContentFrame.GoBack();
            AfterHistoryMove();
        }
        catch (Exception ex) { ShowError(L.T("Could not go back"), ex.Message, ex); }
    }

    public void GoForward()
    {
        try
        {
            if (!ContentFrame.CanGoForward) return;
            ContentFrame.GoForward();
            AfterHistoryMove();
        }
        catch (Exception ex) { ShowError(L.T("Could not go forward"), ex.Message, ex); }
    }

    void AfterHistoryMove()
    {
        if (Shown is GamePage { GameId: { } id }) AppState.Settings.LastGameId = id;
        SyncSelection();
        UpdatePageTitle();
    }

    // ---------- the game list's width ----------

    const double MinPane = 240, MaxPane = 480;

    /// <summary>
    /// Sets the open game list's width: between <see cref="MinPane"/> and <see cref="MaxPane"/>, and never more than 45 % of the
    /// window so the page keeps room. <paramref name="save"/>: a drag, kept in the settings (written when the drag ends).
    /// </summary>
    void SetPaneWidth(double width, bool save = true)
    {
        double max = Nav.ActualWidth > 0 ? Math.Max(MinPane, Math.Min(MaxPane, Nav.ActualWidth * 0.45)) : MaxPane;
        width = Math.Round(Math.Clamp(width, MinPane, max));
        if (save) AppState.Settings.SidebarWidth = width;
        if (Math.Abs(Nav.OpenPaneLength - width) >= 0.5)
        {
            Nav.OpenPaneLength = width;
            UpdatePageTitle();
        }
        UpdateGrip();
    }

    /// <summary>The drag strip sits on the list's right edge, only while the list is open beside the page (not floating over it).</summary>
    void UpdateGrip(bool? opening = null)
    {
        bool show = Nav.DisplayMode == NavigationViewDisplayMode.Expanded && (opening ?? Nav.IsPaneOpen);
        PaneGrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PaneGrip.Margin = new Thickness(Nav.OpenPaneLength - PaneGrip.Width / 2, 0, 0, 0);
    }

    // ---------- the pane button ----------

    void PaneButton_Click(object sender, RoutedEventArgs e) => SetPaneOpen(!Nav.IsPaneOpen);

    /// <summary>
    /// Opens or folds the game list. The focus leaves the search box first (it is hidden in a folded pane); a refusal by
    /// NavigationView (E_FAIL while it is busy) is retried once on the next tick instead of ending the app.
    /// </summary>
    void SetPaneOpen(bool open, bool retry = true)
    {
        if (!open && SearchHasFocus()) ContentFrame.Focus(FocusState.Programmatic);
        try { Nav.IsPaneOpen = open; }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warn($"Game list {(open ? "open" : "fold")}: {ex.Message.Trim()}{(retry ? ", trying again" : "")}");
            if (retry) DispatcherQueue.TryEnqueue(() => SetPaneOpen(open, retry: false));
        }
    }

    /// <summary>Open pane: the button sits right of the search box. Folded pane (a 48 px strip): alone, centred in it.</summary>
    void UpdatePaneButton(bool? opening = null)
    {
        bool minimal = Nav.DisplayMode == NavigationViewDisplayMode.Minimal;
        bool open = minimal || (opening ?? Nav.IsPaneOpen);
        // The arrow turns round (left: hide, right: show) instead of swapping glyphs.
        double angle = open ? 0 : 180;
        if (PaneButtonArrowTurn.Angle != angle)
        {
            if (opening is null) PaneButtonArrowTurn.Angle = angle;
            else Motion.Turn(PaneButtonArrowTurn, angle, 220);
        }
        ToolTipService.SetToolTip(PaneButton, open ? L.T("Hide the game list") : L.T("Show the game list"));
        UpdatePaneTips(open);
        // On narrow windows the pane floats over the page and the stock menu button closes it: only the search stays.
        PaneButton.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = PaneFilterButton.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PaneBar.ColumnSpacing = open ? 6 : 0;
        PaneBar.Margin = open ? new Thickness(12, 2, 12, 8) : new Thickness(7, 2, 0, 8);
        PaneBar.HorizontalAlignment = open ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        UpdateProfileChip(open);
    }

    // ---------- navigation list ----------

    void ShowLoading()
    {
        Nav.MenuItems.Clear();
        _ownedInList.Clear();
        Nav.MenuItems.Add(new NavigationViewItemHeader { Content = L.T("Scanning Steam, Epic, GOG and Xbox…") });
        ContentFrame.Navigate(typeof(HomePage));
    }


    bool Matches(Game g) => _filter.Length == 0 || g.Title.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) || g.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase);

    const string NotInstalledKey = "notinstalled";

    /// <summary>A group shows only its heading while folded; a search opens every group.</summary>
    bool IsFolded(string key) => Library.IsCollapsed(key) && _filter.Length == 0;

    bool _ownedLoading;

    /// <summary>
    /// The owned games that are not on this PC, listed when the filters ask for them (the grid's rule: never with "only games
    /// with OptiScaler"; the launcher filter applies). The lists load in the background the first time, then the pane redraws.
    /// </summary>
    List<OwnedGame> NotInstalledShown()
    {
        var f = Library.ListFilter;
        if (!f.ShowNotInstalled || f.OptiOnly) return [];
        if (!OwnedLibrary.IsLoaded)
        {
            if (!_ownedLoading)
            {
                _ownedLoading = true;
                _ = OwnedLibrary.LoadAsync().ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    _ownedLoading = false;
                    if (OwnedLibrary.IsLoaded)
                    {
                        RebuildNav();
                        // Their hours and achievements, for the orders that need them.
                        _ = Library.LoadOwnedStatsAsync(DispatcherQueue);
                    }
                }), TaskScheduler.Default);
            }
            return [];
        }
        return LibraryPage.OwnedShown(_filter, f);
    }

    /// <summary>The games under one group heading, in the list's order: what folding a group takes out or puts back.</summary>
    List<NavigationViewItem> GroupItems(string key)
    {
        if (key == NotInstalledKey) return NotInstalledShown().Select(CreateOwnedItem).ToList();
        var list = Library.Apply(AppState.VisibleGames.Where(g => !g.NotInstalled).Where(Matches), Library.ListFilter)
            .Where(g => !AppState.IsPinned(g.Id) && StoreLogos.GroupOf(g).Key == key)
            .Select(CreateItem).ToList();
        // Grouped, a launcher's group holds its games not installed too, after the others.
        if (Library.ListFilter.GroupByLauncher)
            list.AddRange(NotInstalledShown().Where(o => "store:" + o.Store == key).Select(CreateOwnedItem));
        return list;
    }

    /// <summary>
    /// Folds or opens one group in place: only its games are taken out or put back; the rest of the list and the page shown stay
    /// as they are (redrawing everything made folding slow). The library grid's own fold calls this too.
    /// </summary>
    public void ApplyFold(string key)
    {
        var header = Nav.MenuItems.OfType<NavigationViewItemHeader>().FirstOrDefault(h => h.Tag is string t && t == key);
        if (header is null) return;
        bool fold = IsFolded(key);
        bool searchHadFocus = SearchHasFocus();
        _rebuilding = true;
        try
        {
            int at = Nav.MenuItems.IndexOf(header) + 1;
            while (at < Nav.MenuItems.Count && Nav.MenuItems[at] is NavigationViewItem gone)
            {
                if (gone.Tag is string id) _ownedInList.Remove(id);
                Nav.MenuItems.RemoveAt(at);
            }
            if (!fold)
                foreach (var item in GroupItems(key)) Nav.MenuItems.Insert(at++, item);
            if (header.Content is Grid { Children.Count: > 0 } grid)
            {
                if (grid.Children[0] is FontIcon chevron) Motion.Turn(chevron, !fold);
                ToolTipService.SetToolTip(grid, fold ? L.T("Click to show these games. Drag it up or down to move the group.") : L.T("Click to fold this group. Drag it up or down to move the group."));
            }
        }
        finally
        {
            _rebuilding = false;
        }
        SyncSelection();
        KeepFocusOffSearch(searchHadFocus);
    }

    // ---------- the search box keeps its focus only when clicked ----------

    bool SearchHasFocus()
    {
        // A check queued just before the window closed runs after it: Content then throws (0xC000027B at exit, rc.18 self-test).
        if (_closed || _quitting || Content?.XamlRoot is null) return false;
        for (var d = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (d == SearchBox) return true;
        return false;
    }

    /// <summary>
    /// The search box takes the focus only from the user: a click or touch on it, Tab or the gamepad moving onto it. Every other
    /// move (the focused item removed from a redrawn list, a page or dialog closing, the window coming back from the tray) is
    /// sent to the page instead, before the box can open its caret and look clicked.
    /// </summary>
    bool _searchWanted;

    void GuardSearchFocus()
    {
        ContentFrame.IsTabStop = true;
        // Every page change, not only a click in the pane: a page left with a focused control in it (a download's pause button)
        // otherwise handed the focus to the search box. Who had the focus is read when the change starts, but the focus is moved
        // only once the new page is in: moving it while the frame swapped pages crashed the app now and then.
        bool searchHadFocus = false;
        ContentFrame.Navigating += (_, _) => searchHadFocus = SearchHasFocus();
        ContentFrame.Navigated += (_, _) => KeepFocusOffSearch(searchHadFocus);
        // Out of the box (to anything else): the next time it gets the focus has to come from the user again.
        SearchBox.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!SearchHasFocus()) _searchByUser = false; });
        // GettingFocus names the last input device even for a move WinUI makes by itself (the focused item removed from a
        // rebuilt list falls back to the next control: "Keyboard, Next" with no key pressed). So a move counts as the user's
        // only right after a real press: on the box itself for a pointer, of a key or controller button for Tab and the pad.
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, _) => _lastKeyTick = Environment.TickCount64), true);
        SearchBox.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _lastSearchPressTick = Environment.TickCount64), true);
        SearchBox.GettingFocus += (_, e) =>
        {
            long now = Environment.TickCount64;
            bool byUser = e.InputDevice is Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Mouse or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Touch or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Pen
                    && now - _lastSearchPressTick < 800
                || (e.InputDevice is Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Keyboard or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.GameController
                    && e.Direction != Microsoft.UI.Xaml.Input.FocusNavigationDirection.None && now - _lastKeyTick < 800);
            if (!SearchHasFocus()) Log.Info($"Search box focus: {e.InputDevice}, {e.Direction}, {(byUser || _searchWanted ? "kept (user)" : "moved away")}");
            if (byUser || _searchWanted) { _searchByUser = true; return; }
            if (SearchHasFocus()) return;
            // Moved right after, not redirected here: changing the focus inside this event broke a closing pop-up (the welcome
            // tour's tip on the search box never finished closing, and the tour skipped steps).
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!SearchHasFocus()) return;
                ContentFrame.Focus(FocusState.Programmatic);
            });
        };
    }

    /// <summary>
    /// When the focused element leaves the tree (the item clicked in a list that is redrawn, the page that is replaced), WinUI hands
    /// the focus to the first control of the window: the search box, which then looked clicked. The page takes it back instead.
    /// </summary>
    /// <summary>For a page that rebuilds itself under the focused control (Settings → Cloud saves after one of its buttons).</summary>
    public void KeepFocusOffSearch()
    {
        KeepFocusOffSearch(SearchHasFocus());
        // Settings has a search box of its own, the first control of its page.
        if (Shown is SettingsPage settings) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => settings.KeepFocusOffSearch());
    }

    /// <summary>
    /// The user put the focus in the search box (click, touch, Tab, gamepad, Ctrl+F). Only then is a search box with the focus
    /// left alone: WinUI also drops the focus there with no event (an overlay or the selected pane item removed), and reading
    /// "it has the focus" as "the user wants it" then kept it there for good (the self-test's "took the focus by itself" from
    /// feature picker 6 on).
    /// </summary>
    bool _searchByUser;

    /// <summary>When a key or controller button, or a pointer on the search box, was last pressed (Environment.TickCount64).</summary>
    long _lastKeyTick = -10_000, _lastSearchPressTick = -10_000;

    /// <summary>A controller button counts as a key press for the search box's focus guard.</summary>
    internal void NotePadPress() => _lastKeyTick = Environment.TickCount64;

    void KeepFocusOffSearch(bool searchHadFocus)
    {
        if (searchHadFocus && _searchByUser) return;
        void Check()
        {
            if (_closed || _quitting || !SearchHasFocus()) return;
            ContentFrame.IsTabStop = true;
            ContentFrame.Focus(FocusState.Programmatic);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Check);
        // The page's own controls load a moment later and can move it again.
        Later.Run(DispatcherQueue, TimeSpan.FromMilliseconds(250), Check);
    }

    /// <summary>
    /// Rebuilds the pane: My PC, the library (grid view), the pinned games, then (list view) every other game by store.
    /// The page shown stays the same when it still makes sense; otherwise the last game, the library or the home page.
    /// </summary>
    /// <summary>The games listed around <paramref name="id"/> in the game list, nearest first (the next one a hand picks).</summary>
    internal List<string> GamesNear(string id, int count)
    {
        var ids = Nav.MenuItems.OfType<NavigationViewItem>().Select(i => i.Tag as string)
            .Where(t => t is not null && AppState.Find(t) is not null).Select(t => t!).ToList();
        int at = ids.FindIndex(t => t.Equals(id, StringComparison.OrdinalIgnoreCase));
        var near = new List<string>();
        for (int d = 1; near.Count < count && (at - d >= 0 || at + d < ids.Count); d++)
        {
            if (at + d < ids.Count) near.Add(ids[at + d]);
            if (near.Count < count && at - d >= 0) near.Add(ids[at - d]);
        }
        return near;
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _configTimer;

    // The library's order the list was last drawn in (Library.OrderKey).
    string? _navOrder;

    // A redraw of the list: the not-installed items still to add belong to the redraw that asked for them.
    int _navRun;
    const int OwnedAtOnce = 30;

    /// <summary>
    /// The not-installed games of a group: the first few at once, the rest in turns of the UI thread (2.1-rc.3: 370 items added
    /// in one go held the window 0.5-0.8 s). A newer redraw drops what an older one had left to add.
    /// </summary>
    void AddOwnedItems(List<OwnedGame> games, Dictionary<string, NavigationViewItem> items)
    {
        foreach (var g in games.Take(OwnedAtOnce)) Nav.MenuItems.Add(items[g.Id] = CreateOwnedItem(g));
        if (games.Count <= OwnedAtOnce || Nav.MenuItems.LastOrDefault() is not { } anchor) return;
        int run = _navRun;
        var rest = new Queue<OwnedGame>(games.Skip(OwnedAtOnce));
        void Next()
        {
            if (run != _navRun || _closed || rest.Count == 0) return;
            int at = Nav.MenuItems.IndexOf(anchor);
            if (at < 0) return;
            _rebuilding = true;
            try
            {
                for (int i = 0; i < 40 && rest.Count > 0; i++)
                {
                    var g = rest.Dequeue();
                    // Already in the list (its group was folded and opened again meanwhile): not twice.
                    if (_ownedInList.Contains(g.Id)) continue;
                    var item = CreateOwnedItem(g);
                    Nav.MenuItems.Insert(++at, item);
                    anchor = item;
                    // The page shown is this game's: its item is selected once it is in the list (2.1-rc.3 audit).
                    if (Shown is GamePage { GameId: { } shown } && shown.Equals(g.Id, StringComparison.OrdinalIgnoreCase)) Nav.SelectedItem = item;
                }
            }
            finally { _rebuilding = false; }
            if (rest.Count > 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Next);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Next);
    }

    void RebuildNav()
    {
        using var __perf = Helpers.UiPerf.Scope("MainWindow.RebuildNav");
        _navOrder = Library.OrderKey(Library.ListFilter);
        _navRun++;
        KeepFocusOffSearch(SearchHasFocus());
        _rebuilding = true;
        try
        {
            Nav.MenuItems.Clear();
            _ownedInList.Clear();

            // The PC overview sits on top, above the game list.
            var hardware = new NavigationViewItem { Content = L.T("My PC"), Tag = HardwareTag, Icon = PaneIcon("", 0xFF5AB8FF, 0xFF7C6CFF) };
            BigIcon(hardware);
            PaneTip(hardware, L.T("Your PC: hardware, display settings and the upscalers your card supports"));
            Nav.MenuItems.Add(hardware);

            // The library (every game as covers, with filters) and the downloads are always there, under My PC.
            var library = new NavigationViewItem { Content = L.T("Library"), Tag = LibraryTag, Icon = PaneIcon("\uE8F1", 0xFFFF6B8A, 0xFFE0142B) };
            BigIcon(library);
            PaneTip(library, L.T("All your games as a grid of covers"));
            Nav.MenuItems.Add(library);
            var downloads = new NavigationViewItem { Content = L.T("Downloads"), Tag = DownloadsTag, Icon = PaneIcon("\uE896", 0xFF5EE0A0, 0xFF1FA3A3) };
            BigIcon(downloads);
            PaneTip(downloads, L.T("Games you own on your launchers: install them and follow the downloads"));
            _downloadsItem = downloads;
            UpdateDownloadsBadge();
            Nav.MenuItems.Add(downloads);

            var items = new Dictionary<string, NavigationViewItem>(StringComparer.OrdinalIgnoreCase);

            // The library's sort and filters (one set, shared with the grid page and set from either Filters button). A game owned
            // but not installed that was opened once is a page-only game in the app's list: it shows with the not-installed ones.
            var shown = Library.Apply(AppState.VisibleGames.Where(g => !g.NotInstalled).Where(Matches), Library.ListFilter);
            var shownIds = shown.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            UpdatePaneFilterBadge();
            var owned = NotInstalledShown();
            bool grouped = Library.ListFilter.GroupByLauncher;
            var paneLabel = (Style)HybridLauncher.Helpers.ThemeRes.Current["PaneGroupLabel"];

            // Quick access: pinned games first, in pin order, in both views.
            var pinned = AppState.PinnedGames().Where(g => shownIds.Contains(g.Id)).ToList();
            if (pinned.Count > 0)
            {
                Nav.MenuItems.Add(new NavigationViewItemSeparator());
                Nav.MenuItems.Add(PinnedHeader(pinned.Count));
                foreach (var game in pinned) Nav.MenuItems.Add(items[game.Id] = CreateItem(game));
            }

            {
                var games = shown.Where(g => !AppState.IsPinned(g.Id)).ToList();
                if (games.Count == 0 && pinned.Count == 0 && owned.Count == 0)
                    Nav.MenuItems.Add(new NavigationViewItemHeader
                    {
                        Content = _filter.Length > 0 ? L.T("No game matches the search")
                            : Library.ListFilter.IsFiltering && AppState.VisibleGames.Any() ? L.T("No game passes the filters")
                            : L.T("No games found"),
                    });

                if (grouped)
                {
                    // Same order as the library (sort setting), in the user's group order; a folded group shows only its heading.
                    // A group holds its launcher's games not installed too (2.1-rc.3), after the installed ones.
                    var ownedBy = owned.GroupBy(o => "store:" + o.Store).ToDictionary(g => g.Key, g => g.ToList());
                    var onlyOwned = ownedBy.Where(o => !games.Any(g => StoreLogos.GroupOf(g).Key == o.Key)).Select(o => OwnedLibrary.AsGame(o.Value[0]));
                    foreach (var (key, group) in Library.Groups(games.Concat(onlyOwned)))
                    {
                        var mine = ownedBy.GetValueOrDefault(key) ?? [];
                        var installed = group.Where(g => !g.NotInstalled).ToList();
                        Nav.MenuItems.Add(new NavigationViewItemSeparator());
                        Nav.MenuItems.Add(GroupHeaderItem(key, StoreLogos.GroupHeader(group[0], installed.Count + mine.Count, paneLabel)));
                        if (IsFolded(key)) continue;
                        foreach (var game in installed) Nav.MenuItems.Add(items[game.Id] = CreateItem(game));
                        AddOwnedItems(mine, items);
                    }
                }
                else if (games.Count > 0)
                {
                    // Not grouped (Filters, Look): one block of every game in the chosen order, as in the library (2.1-rc.3).
                    Nav.MenuItems.Add(new NavigationViewItemSeparator());
                    foreach (var game in games) Nav.MenuItems.Add(items[game.Id] = CreateItem(game));
                }
            }

            // Not grouped, "Also games you own but have not installed": the grid's last section, after the games here too.
            if (!grouped && owned.Count > 0)
            {
                Nav.MenuItems.Add(new NavigationViewItemSeparator());
                Nav.MenuItems.Add(GroupHeaderItem(NotInstalledKey, StoreLogos.NotInstalledHeader(owned.Count, paneLabel)));
                if (!IsFolded(NotInstalledKey)) AddOwnedItems(owned, items);
            }

            switch (Shown)
            {
                case HardwarePage:
                    Nav.SelectedItem = hardware;
                    break;
                case SettingsPage:
                    Nav.SelectedItem = Nav.SettingsItem;
                    break;
                case LibraryPage page:
                    Nav.SelectedItem = library;
                    // Only when what the grid shows changed (the search, the games, its settings): a change of the list's own
                    // filters leaves the covers as they are (2.1-rc.4).
                    page.ReloadIfChanged(_filter);
                    break;
                case DownloadsPage:
                    Nav.SelectedItem = downloads;
                    break;
                case GamePage { GameId: { } id } page when AppState.VisibleGames.Any(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase)):
                    Nav.SelectedItem = items.GetValueOrDefault(id);
                    page.Refresh();
                    break;
                case GamePage { GameId: { } id } when AppState.FindAny(id) is { NotInstalled: true }:
                    // The Download page of a game not installed stays open (selected when the filters list it).
                    Nav.SelectedItem = items.GetValueOrDefault(id);
                    break;
                default:
                    // First load, or the page shown no longer fits (hidden game, library in list view).
                    var last = AppState.Settings.LastGameId;
                    var lastGame = AppState.VisibleGames.FirstOrDefault(g => g.Id.Equals(last, StringComparison.OrdinalIgnoreCase));
                    if (last == HardwareTag)
                    {
                        Nav.SelectedItem = hardware;
                        ContentFrame.Navigate(typeof(HardwarePage));
                    }
                    else if (Shown is LibraryPage || lastGame is null)
                    {
                        // Hybrid Launcher opens on the library, like a launcher's home, unless a game was open last time.
                        Nav.SelectedItem = library;
                        ContentFrame.Navigate(typeof(LibraryPage), _filter);
                    }
                    else
                    {
                        Nav.SelectedItem = items.GetValueOrDefault(lastGame.Id);
                        NavigateGame(lastGame.Id);
                    }
                    break;
            }
        }
        finally
        {
            _rebuilding = false;
        }
        UpdatePageTitle();
    }

    const string LibraryTag = "lib", DownloadsTag = "dl";

    NavigationViewItem? _downloadsItem;

    /// <summary>The Downloads item shows how many installs are running (an info badge).</summary>
    bool _closed;

    public void UpdateDownloadsBadge()
    {
        // Raised from the downloads' timer too: on any other thread (or while the window closes) it goes to the UI thread.
        if (_closed || DispatcherQueue is not { } ui) return;
        if (!ui.HasThreadAccess) { ui.TryEnqueue(UpdateDownloadsBadge); return; }
        if (_downloadsItem is null) return;
        int active = HybridLauncher.Core.Accounts.Downloads.ActiveCount;
        _downloadsItem.InfoBadge = active > 0 ? new InfoBadge { Value = active } : null;
    }

    /// <summary>Redraws the side pane (after the library's groups, order or filters changed).</summary>
    public void RefreshList() => RebuildNav();

    /// <summary>The library's filters changed: the grid follows when it is the page shown (else when it is opened).</summary>
    public void RefreshLibrary() => (Shown as LibraryPage)?.ReloadIfChanged(_filter);

    /// <summary>
    /// A game's own picture changed (Edit artwork, 2.1): the library drops its cover, the list redraws its icon, and the game's
    /// page shows the new banner or logo (built again; the one on screen at once).
    /// </summary>
    void OnArtChanged(string id, HybridLauncher.Core.Media.ArtKind kind)
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(() => OnArtChanged(id, kind)); return; }
        try
        {
            switch (kind)
            {
                case HybridLauncher.Core.Media.ArtKind.Cover:
                    LibraryPage.ForgetCover(id);
                    // The library on screen shows the new cover now; the profile and Downloads at their next look (2.1-rc.3 audit).
                    ProfilePage.ArtVersion++;
                    if (Shown is LibraryPage library) library.Reload(library.Filter);
                    break;
                case HybridLauncher.Core.Media.ArtKind.Icon:
                    // A not-installed game's list item is kept between redraws: made again with the new icon.
                    _ownedItems.Remove(id);
                    RebuildNav();
                    break;
                default:
                    // The page kept the game's data with the old banner (rc.8: a new banner showed only after a restart).
                    GamePage.ArtChanged(id);
                    if (Shown is GamePage { GameId: { } shown } && shown.Equals(id, StringComparison.OrdinalIgnoreCase)) ReloadGame(id);
                    else ForgetGamePage(id);
                    break;
            }
        }
        catch (Exception ex) { Log.Warn($"Artwork change of {id}: {ex.Message}"); }
    }

    /// <summary>The pane's Filters button: the list's own filters (2.1-rc.4), in the library's panel; a dot while any is on.</summary>
    void PaneFilters_Opening(object sender, object e) => PaneFiltersFlyout.Content = LibraryPage.FiltersPanel(PaneFiltersFlyout.Hide, list: true);

    void UpdatePaneFilterBadge()
    {
        int active = Library.ActiveFilters(Library.ListFilter);
        PaneFilterDot.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PaneFilterButton, active > 0 ? L.F("Sort and filters ({0} on)", active) : L.T("Sort and filters"));
    }

    /// <summary>
    /// A launcher / games folder heading in the list: click folds or opens the group, dragging it onto another heading moves the
    /// group there (the grid's groups follow the same order).
    /// </summary>
    NavigationViewItemHeader GroupHeaderItem(string key, FrameworkElement label)
    {
        bool collapsed = IsFolded(key);
        var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        var grid = new Grid { ColumnSpacing = 8, Background = clear, MinWidth = 240, Padding = new Thickness(6, 4, 4, 4), Margin = new Thickness(-6, 0, 0, 0), CornerRadius = new CornerRadius(6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chevron = Motion.Chevron(!collapsed, 10);
        chevron.Opacity = 0.75;
        grid.Children.Add(chevron);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        // No grip: the heading lifts as soon as it is dragged, and the tooltip says it moves.
        var move = new Microsoft.UI.Xaml.Media.CompositeTransform();
        grid.RenderTransform = move;
        grid.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        ToolTipService.SetToolTip(grid, collapsed ? L.T("Click to show these games. Drag it up or down to move the group.") : L.T("Click to fold this group. Drag it up or down to move the group."));
        var header = new NavigationViewItemHeader { Content = grid, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["StoreHeaderStyle"], IsEnabled = true, Tag = key };

        bool pressed = false, dragging = false;
        double startY = 0;
        NavigationViewItemHeader? target = null;
        bool below = false;
        // The games not installed always come last: their heading folds but does not move, and no group lands on it.
        bool movable = key != NotInstalledKey;
        IEnumerable<NavigationViewItemHeader> Headers() => Nav.MenuItems.OfType<NavigationViewItemHeader>().Where(h => h.Tag is string t && t != NotInstalledKey);
        // Where the group would land: a thick accent line on the top or bottom edge of the heading under the pointer.
        void Mark(NavigationViewItemHeader? h, bool after)
        {
            if (target?.Content is Grid old) { old.BorderThickness = new Thickness(0); old.Background = clear; }
            target = h;
            below = after;
            if (h?.Content is Grid g)
            {
                g.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)HybridLauncher.Helpers.ThemeRes.Current["AccentFillColorDefaultBrush"];
                g.BorderThickness = after ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0);
                g.Background = (Microsoft.UI.Xaml.Media.Brush)HybridLauncher.Helpers.ThemeRes.Current["SubtleFillColorSecondaryBrush"];
            }
        }
        void Lift(bool up)
        {
            Canvas.SetZIndex(header, up ? 10 : 0);
            grid.Background = up ? (Microsoft.UI.Xaml.Media.Brush)HybridLauncher.Helpers.ThemeRes.Current["AcrylicInAppFillColorDefaultBrush"] : clear;
            grid.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)HybridLauncher.Helpers.ThemeRes.Current["CardStrokeColorDefaultBrush"];
            grid.BorderThickness = new Thickness(up ? 1 : 0);
            Motion.To(move, "ScaleX", up ? 1.04 : 1, 140);
            Motion.To(move, "ScaleY", up ? 1.04 : 1, 140);
        }
        grid.PointerEntered += (_, _) => { if (!dragging) grid.Background = (Microsoft.UI.Xaml.Media.Brush)HybridLauncher.Helpers.ThemeRes.Current["SubtleFillColorSecondaryBrush"]; };
        grid.PointerExited += (_, _) => { if (!dragging) grid.Background = clear; };
        grid.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
            pressed = true;
            dragging = false;
            startY = e.GetCurrentPoint(null).Position.Y;
            grid.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        grid.PointerMoved += (_, e) =>
        {
            if (!pressed) return;
            double y = e.GetCurrentPoint(null).Position.Y;
            if (!movable || (!dragging && Math.Abs(y - startY) < 6)) return;
            if (!dragging)
            {
                dragging = true;
                Lift(true);
            }
            // The heading follows the pointer while it is dragged.
            move.TranslateY = y - startY;
            NavigationViewItemHeader? over = null;
            bool after = false;
            foreach (var h in Headers())
            {
                if (h == header || h.XamlRoot is null) continue;
                var b = h.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, h.ActualWidth, h.ActualHeight));
                if (y >= b.Top - 10 && y <= b.Bottom + 10)
                {
                    over = h;
                    after = y > b.Top + b.Height / 2;
                }
            }
            if (over != target || after != below) Mark(over, after);
        };
        void Released()
        {
            if (!pressed) return;
            pressed = false;
            grid.ReleasePointerCaptures();
            var drop = target;
            bool after = below;
            Mark(null, false);
            if (!dragging)
            {
                // A search shows every group open: folding waits until it is cleared.
                if (_filter.Length > 0) return;
                Library.SetCollapsed(key, !Library.IsCollapsed(key));
                ApplyFold(key);
                // The grid's group folds with it when the library is the page shown.
                (Shown as LibraryPage)?.ApplyFold(key);
                return;
            }
            dragging = false;
            Lift(false);
            if (drop?.Tag is not string to)
            {
                // Dropped nowhere: it glides back to its place.
                Motion.To(move, "TranslateY", 0, 200);
                return;
            }
            var keys = Headers().Select(h => (string)h.Tag).ToList();
            keys.Remove(key);
            int at = keys.IndexOf(to) + (after ? 1 : 0);
            keys.Insert(Math.Clamp(at, 0, keys.Count), key);
            Library.SaveGroupOrder(keys);
            DispatcherQueue.TryEnqueue(RebuildNav);
        }
        grid.PointerReleased += (_, e) => { Released(); e.Handled = true; };
        grid.PointerCaptureLost += (_, _) => { if (pressed && dragging) Released(); pressed = false; };
        return header;
    }

    static NavigationViewItemHeader PinnedHeader(int count)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = "\uEB52", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = L.T("FAVORITES"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, CharacterSpacing = 40, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        return new NavigationViewItemHeader { Content = panel, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["StoreHeaderStyle"] };
    }

    /// <summary>
    /// A game the app downloaded is installed: a desktop shortcut for it (Settings → Storage → Desktop shortcut for downloaded
    /// games). The rescan that lists the game runs first; a shortcut already there is left alone.
    /// </summary>
    async Task ShortcutAfterInstallAsync(string id)
    {
        if (!AppState.Settings.ShortcutOnInstall) return;
        try
        {
            await AppState.RescanAsync();
            if (AppState.Find(id) is not { NotInstalled: false, Installing: false } game || Shortcuts.Exists(game)) return;
            await Shortcuts.CreateDesktopAsync(game, AppState.Settings.For(game.Id));
            Log.Info($"Desktop shortcut made for {game.Name}");
            ShowSuccess(L.F("{0} is installed. Its shortcut is on the desktop.", game.Title));
        }
        catch (Exception ex) { Log.Warn($"Shortcut after install: {ex.Message}"); }
    }

    /// <summary>Opens a game's page on one of its achievements (the Library's Latest achievements panel).</summary>
    public void OpenAchievement(string gameId, string key)
    {
        OpenGame(gameId);
        (Shown as GamePage)?.ShowAchievement(key);
    }

    /// <summary>Opens a game page (from the library grid, a pop-up...), selecting it in the pane when it is listed there.</summary>
    public void OpenGame(string id)
    {
        using var __perf = Helpers.UiPerf.Scope("MainWindow.OpenGame");
        if (AppState.FindAny(id) is null)
        {
            // A game owned on a launcher but not installed (its achievements show in the Library and the profile): its page
            // opens through the launcher's library (2.1-rc.3 audit: the click did nothing).
            if (OwnedLibrary.Find(id) is { } owned) OwnedLibrary.Open(owned);
            return;
        }
        AppState.Settings.LastGameId = id;
        KeepFocusOffSearch(SearchHasFocus());
        _rebuilding = true;
        try
        {
            if ((Shown as GamePage)?.GameId != id) NavigateGame(id);
            Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is string t && t.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _rebuilding = false;
        }
    }

    const string HardwareTag = "hw";

    // The tips of the pane's items, to hide them while the pane is folded (see PaneTip).
    readonly List<WeakReference<ToolTip>> _paneTips = [];

    /// <summary>
    /// The tip of an item of the game list. Shown only while the list is open (a long name cut at the pane's edge, the
    /// game's folder): folded to its strip of icons, no tip comes up over the page beside it (2.0), neither ours nor the
    /// one NavigationView would put there by itself (an item with a tip of its own gets none from it).
    /// </summary>
    void PaneTip(DependencyObject item, string? text)
    {
        var tip = new ToolTip { Content = text ?? "", Visibility = text is not null && PaneShowsTips ? Visibility.Visible : Visibility.Collapsed, Tag = text };
        _paneTips.Add(new WeakReference<ToolTip>(tip));
        ToolTipService.SetToolTip(item, tip);
    }

    bool PaneShowsTips => Nav.DisplayMode == NavigationViewDisplayMode.Minimal || Nav.IsPaneOpen;

    void UpdatePaneTips(bool open)
    {
        _paneTips.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in _paneTips)
            if (w.TryGetTarget(out var tip)) tip.Visibility = open && tip.Tag is string ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A game's name in the list: a long one ends in "…" at the pane's edge, as names do everywhere else.</summary>
    static TextBlock ItemName(string name) => new() { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };

    NavigationViewItem CreateItem(Game game)
    {
        var item = new NavigationViewItem
        {
            Content = ItemName(game.Title),
            Tag = game.Id,
            Icon = new FontIcon { Glyph = "" },
        };
        BigIcon(item);
        PaneTip(item, $"{game.Title}\n{game.StoreLabel} · {game.InstallDir}");

        // The menu is made when it opens (and again each time, so it says what is true then): the list no longer carries a
        // dozen menu items per game it may never show.
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var made = GameMenu(game);
            var entries = made.Items.ToList();
            made.Items.Clear();
            foreach (var e in entries) menu.Items.Add(e);
        };
        item.ContextFlyout = menu;
        // Resting on a game warms its art (after a short pause, so a pointer sweeping over the list asks for nothing).
        Microsoft.UI.Dispatching.DispatcherQueueTimer? warm = null;
        item.PointerEntered += (_, _) =>
        {
            warm ??= Helpers.WeakTimer.Create(DispatcherQueue, game, TimeSpan.FromMilliseconds(120), static g => Core.Media.GameInfo.Warm(g), repeating: false);
            warm.Start();
        };
        item.PointerExited += (_, _) => warm?.Stop();
        if (GameSessions.IsActive(game.Id)) DispatcherQueue.TryEnqueue(() => MarkRunning(game.Id));

        _ = LoadIconAsync(item, game);
        return item;
    }

    /// <summary>A game you own but have not installed: dimmed, its launcher's icon until Steam's own is found; opens its Download page.</summary>
    // The not-installed games' items, kept across redraws (2.1-rc.3): hundreds of them were made again, each loading its
    // icon, on every change of a filter or the search (1-2 s with the window frozen in the user's log).
    readonly Dictionary<string, NavigationViewItem> _ownedItems = new(StringComparer.OrdinalIgnoreCase);
    // The ones in the list now (an item can be in it once only).
    readonly HashSet<string> _ownedInList = new(StringComparer.OrdinalIgnoreCase);

    NavigationViewItem CreateOwnedItem(OwnedGame game)
    {
        if (_ownedItems.TryGetValue(game.Id, out var kept) && _ownedInList.Add(game.Id)) return kept;
        var item = new NavigationViewItem { Content = ItemName(game.Name), Tag = game.Id, Opacity = 0.62, Icon = new FontIcon { Glyph = "" } };
        BigIcon(item);
        PaneTip(item, $"{game.Name}\n{StoreLogos.Label(game.Store)} · {L.T("not installed")}");
        // The icon only once the item is on screen (the list makes the items it shows): never hundreds at once.
        if (game.Store == GameStore.Steam && game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            bool asked = false;
            item.Loaded += (_, _) =>
            {
                if (asked) return;
                asked = true;
                _ = LoadIconAsync(item, OwnedLibrary.AsGame(game));
            };
        }
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var made = OwnedMenu(game);
            var entries = made.Items.ToList();
            made.Items.Clear();
            foreach (var e in entries) menu.Items.Add(e);
        };
        item.ContextFlyout = menu;
        _ownedItems[game.Id] = item;
        _ownedInList.Add(game.Id);
        return item;
    }

    /// <summary>
    /// The right-click menu of a game, in the list and in the library grid, laid out as Hydra's (2.1): Play (Stop while it
    /// runs), Collection (the favorites, the user's collections, a new one), Create shortcut (desktop, Steam), Options (the
    /// rest) and Properties.
    /// </summary>
    public MenuFlyout GameMenu(Game game)
    {
        var menu = new MenuFlyout();
        if (GameSessions.IsActive(game.Id))
            menu.Items.Add(MenuItem(L.T("Stop"), "", () => _ = GameSessions.StopAsync(game.Id)));
        else
        {
            var play = MenuItem(L.T("Play"), "", () => _ = PlayFromMenuAsync(game));
            play.IsEnabled = !game.Installing;
            menu.Items.Add(play);
        }
        menu.Items.Add(CollectionMenu(game.Id));

        if (!game.NotInstalled && !game.Installing)
        {
            var shortcuts = new MenuFlyoutSubItem { Text = L.T("Create shortcut"), Icon = new FontIcon { Glyph = "" } };
            shortcuts.Items.Add(MenuItem(L.T("Desktop shortcut"), "", () => _ = DesktopShortcutAsync(game)));
            var steam = MenuItem(L.T("Steam shortcut"), "", () => SteamShortcut(game));
            steam.Icon = StoreLogos.MenuIcon(GameStore.Steam);
            shortcuts.Items.Add(steam);
            menu.Items.Add(shortcuts);
        }

        var options = new MenuFlyoutSubItem { Text = L.T("Options"), Icon = new FontIcon { Glyph = "" } };
        options.Items.Add(MenuItem(L.T("Edit artwork…"), "", () => { if (Content.XamlRoot is { } root) _ = Controls.ArtworkDialog.ShowAsync(root, ((FrameworkElement)Content).ActualTheme, game); }));
        options.Items.Add(MenuItem(L.T("Rename…"), "", () => _ = RenameGameAsync(game)));
        options.Items.Add(MenuItem(L.T("Open game folder"), "", () => GameLauncher.OpenFolder(game.InstallDir)));
        if (!game.NotInstalled && !game.Installing)
            options.Items.Add(MenuItem(L.T("Verify game files…"), "", () => { if (Content.XamlRoot is { } root) _ = VerifyFlow.RunAsync(game, root, ((FrameworkElement)Content).ActualTheme); }));
        options.Items.Add(new MenuFlyoutSeparator());
        options.Items.Add(MenuItem(L.T("Hide from the list"), "", () =>
        {
            AppState.Settings.HiddenGames.Add(game.Id);
            SettingsStore.Save();
            RebuildNav();
            ShowInfo(L.F("{0} is hidden. Show it again from Settings.", game.Title));
        }));
        if (game.Store == GameStore.Custom && game.Id.StartsWith("custom:", StringComparison.Ordinal))
        {
            options.Items.Add(MenuItem(L.T("Remove from Hybrid Launcher"), "", () =>
            {
                AppState.Settings.CustomGames.RemoveAll(c => GameScanner.CustomId(c.ExePath) == game.Id);
                AppState.SetPinned(game.Id, false);
                SettingsStore.Save();
                _ = AppState.RescanAsync();
            }));
        }
        if (!game.Installing)
            options.Items.Add(MenuItem(L.T("Uninstall…"), "", () => { if (Content.XamlRoot is { } root) _ = UninstallFlow.RunAsync(game, root, ((FrameworkElement)Content).ActualTheme); }));
        menu.Items.Add(options);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(L.T("Properties"), "", () => ShowProperties(game.Id)));
        return menu;
    }

    /// <summary>The right-click menu of a game owned but not installed: Install (its page), its collections, hide it.</summary>
    public MenuFlyout OwnedMenu(OwnedGame game)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem(L.T("Install"), "", () => OwnedLibrary.Open(game)));
        menu.Items.Add(CollectionMenu(game.Id));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(L.T("Hide from the list"), "", () =>
        {
            AppState.Settings.HiddenGames.Add(game.Id);
            SettingsStore.Save();
            RebuildNav();
            RefreshLibrary();
            ShowInfo(L.F("{0} is hidden. Show it again from Settings.", game.Name));
        }));
        return menu;
    }

    /// <summary>Collection ▸ Favorites (the heart), each of the user's collections (a tick when the game is in it), New collection….</summary>
    MenuFlyoutSubItem CollectionMenu(string id)
    {
        var sub = new MenuFlyoutSubItem { Text = L.T("Collection"), Icon = new FontIcon { Glyph = "" } };
        bool fav = GameCollections.IsFavorite(id);
        sub.Items.Add(MenuItem(fav ? L.T("Remove from favorites") : L.T("Add to favorites"), fav ? "" : "",
            () => AppState.SetInCollection(GameCollections.Favorites, id, !fav)));
        if (GameCollections.All.Count > 0) sub.Items.Add(new MenuFlyoutSeparator());
        foreach (var c in GameCollections.All.ToList())
        {
            bool inIt = GameCollections.Contains(c.Id, id);
            var cid = c.Id;
            var item = new ToggleMenuFlyoutItem { Text = c.Name, IsChecked = inIt, Icon = new FontIcon { Glyph = "" } };
            item.Click += (_, _) =>
            {
                try { AppState.SetInCollection(cid, id, !inIt); }
                catch (Exception ex) { ShowError(L.T("Action failed"), ex.Message, ex); }
            };
            sub.Items.Add(item);
        }
        sub.Items.Add(new MenuFlyoutSeparator());
        sub.Items.Add(MenuItem(L.T("New collection…"), "", () => _ = NewCollectionAsync(id)));
        return sub;
    }

    // A naming or deleting dialog is open (only one ContentDialog can be).
    bool _collectionDialog;

    /// <summary>Asks for a name and makes a collection (with the game in it when one is given). Returns its id, or null.</summary>
    public async Task<string?> NewCollectionAsync(string? gameId = null)
    {
        if (Content.XamlRoot is not { } root || _collectionDialog) return null;
        _collectionDialog = true;
        try
        {
            var name = await HybridLauncher.Controls.NameDialog.AskAsync(root, ((FrameworkElement)Content).ActualTheme, L.T("New collection"), "", "");
            if (string.IsNullOrWhiteSpace(name)) return null;
            var c = GameCollections.Create(name);
            if (gameId is not null) AppState.SetInCollection(c.Id, gameId, true);
            else AppState.CollectionsEdited();
            return c.Id;
        }
        catch (Exception ex) { Log.Warn($"New collection: {ex.Message}"); return null; }
        finally { _collectionDialog = false; }
    }

    /// <summary>Asks for a new name for the collection.</summary>
    public async Task RenameCollectionAsync(string id)
    {
        if (Content.XamlRoot is not { } root || GameCollections.Find(id) is not { } c || _collectionDialog) return;
        _collectionDialog = true;
        try
        {
            var name = await HybridLauncher.Controls.NameDialog.AskAsync(root, ((FrameworkElement)Content).ActualTheme, L.T("Rename collection"), c.Name, c.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            GameCollections.Rename(id, name);
            AppState.CollectionsEdited();
        }
        catch (Exception ex) { Log.Warn($"Rename collection: {ex.Message}"); }
        finally { _collectionDialog = false; }
    }

    /// <summary>Deletes the collection after asking (its games stay in the library).</summary>
    public async Task DeleteCollectionAsync(string id)
    {
        if (Content.XamlRoot is not { } root || GameCollections.Find(id) is not { } c || _collectionDialog) return;
        _collectionDialog = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root, RequestedTheme = ((FrameworkElement)Content).ActualTheme, Title = L.F("Delete \"{0}\"?", c.Name),
                Content = L.T("The collection goes away; its games stay in your library."),
                PrimaryButtonText = L.T("Delete"), CloseButtonText = L.T("Cancel"), DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
            GameCollections.Delete(id);
            AppState.CollectionsEdited();
        }
        catch (Exception ex) { Log.Warn($"Delete collection: {ex.Message}"); }
        finally { _collectionDialog = false; }
    }

    async Task DesktopShortcutAsync(Game game)
    {
        try
        {
            await Shortcuts.CreateDesktopAsync(game, AppState.Settings.For(game.Id));
            ShowSuccess(L.F("The shortcut to {0} is on the desktop.", game.Title));
        }
        catch (Exception ex) { ShowError(L.T("The shortcut was not made"), ex.Message, ex); }
    }

    void SteamShortcut(Game game)
    {
        try
        {
            var file = Shortcuts.CreateSteam(game, AppState.Settings.For(game.Id));
            ShowSuccess(file is null ? L.F("{0} is in Steam already.", game.Title)
                : L.F("{0} is added to Steam as a non-Steam game. Restart Steam to see it.", game.Title));
        }
        catch (Exception ex) { ShowError(L.T("The Steam shortcut was not made"), ex.Message, ex); }
    }

    /// <summary>
    /// A name of the user's own for a game, shown everywhere in Hybrid Launcher. The game keeps its store name (Game.Name)
    /// for the OptiScaler wiki, PCGamingWiki and the store pages, so they still find it.
    /// </summary>
    async Task RenameGameAsync(Game game)
    {
        if (Content.XamlRoot is not { } root) return;
        var picked = await HybridLauncher.Controls.NameDialog.AskAsync(root, ((FrameworkElement)Content).ActualTheme, L.T("Rename this game"), game.Name,
            AppState.Settings.GameNames.GetValueOrDefault(game.Id, ""));
        if (picked is null) return;
        if (picked.Length == 0 || picked == game.Name) AppState.Settings.GameNames.Remove(game.Id);
        else AppState.Settings.GameNames[game.Id] = picked;
        SettingsStore.Save();
        RebuildNav();
        switch (Shown)
        {
            case LibraryPage library: library.Reload(library.Filter); break;
            case GamePage page when page.GameId == game.Id: page.Refresh(); break;
        }
        UpdatePageTitle();
    }

    /// <summary>Play from a right-click menu: started and watched like the Play button (Stop, time played, Smart HDR).</summary>
    static async Task PlayFromMenuAsync(Game game)
    {
        if (game.Installing || GameSessions.IsActive(game.Id)) return;
        try { await AppState.LaunchAsync(game); }
        catch (Exception ex) { App.Window?.ShowError(L.T("The game did not start"), ex.Message, ex); }
    }

    /// <summary>A running game's row in the list gets a green dot (as Steam marks it), however the game was started.</summary>
    void MarkRunning(string id)
    {
        if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is string t && t.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } item) return;
        var state = GameSessions.State(id);
        if (state == GameRunState.Stopped) { item.InfoBadge = null; return; }
        item.InfoBadge = new InfoBadge
        {
            Width = 10, Height = 10, MinWidth = 10,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(state == GameRunState.Running ? Windows.UI.Color.FromArgb(0xFF, 0x5B, 0xD1, 0x6E) : Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xC5, 0x18)),
        };
        ToolTipService.SetToolTip(item.InfoBadge, state == GameRunState.Running ? L.T("Running") : state == GameRunState.Starting ? L.T("Starting…") : L.T("Closing…"));
    }

    /// <summary>
    /// The pane's own places (My PC, Library, Downloads): a glyph as large as the games' icons, filled with a two-tone gradient from
    /// top left to bottom right, so the three read as one set and stand apart from the plain list below.
    /// </summary>
    static FontIcon PaneIcon(string glyph, uint from, uint to)
    {
        static Windows.UI.Color C(uint c) => Windows.UI.Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        return new FontIcon
        {
            Glyph = glyph,
            FontSize = 19,
            Foreground = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = C(from), Offset = 0 },
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = C(to), Offset = 1 },
                },
            },
        };
    }

    /// <summary>A game's row with a larger icon than the pane's own items (26 px, the row a little taller), so games are told apart at a glance.</summary>
    static void BigIcon(NavigationViewItem item)
    {
        item.Resources["NavigationViewItemOnLeftIconBoxHeight"] = 26.0;
        item.Resources["NavigationViewItemOnLeftMinHeight"] = 44.0;
    }

    static async Task LoadIconAsync(NavigationViewItem item, Game game)
    {
        // Game Pass exes carry no icon: their package logo comes first. Steam games then take Steam's own small icon from its
        // library cache, the one Steam's list shows (also there while the game downloads and has no exe); then the exes.
        var steamIcon = game.SteamAppId is { } app ? await Task.Run(() => SteamScanner.ClientIcon(app)) : null;
        foreach (var source in new[] { HybridLauncher.Core.Media.ArtOverrides.Get(game.Id, HybridLauncher.Core.Media.ArtKind.Icon), game.IconPath, steamIcon, game.MainExe, game.StoreExe }.Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var icon = await IconCache.GetAsync(source!);
            if (icon is null) continue;
            item.Icon = RoundedIcon(icon);
            return;
        }
    }

    /// <summary>The game's icon with softly rounded corners, like Steam's list (a clip on its visual; the icon itself stays square).</summary>
    static ImageIcon RoundedIcon(Microsoft.UI.Xaml.Media.ImageSource source)
    {
        var icon = new ImageIcon { Source = source };
        icon.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var c = visual.Compositor;
            var shape = c.CreateRoundedRectangleGeometry();
            shape.Size = new System.Numerics.Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            float r = (float)Math.Round(e.NewSize.Width * 0.18);
            shape.CornerRadius = new System.Numerics.Vector2(r, r);
            visual.Clip = c.CreateGeometricClip(shape);
        };
        return icon;
    }

    static MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        mi.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
        };
        return mi;
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        using var __perf = Helpers.UiPerf.Scope("MainWindow.NavSelection");
        if (_rebuilding) return;
        KeepFocusOffSearch(SearchHasFocus());
        if (args.IsSettingsSelected)
        {
            // Settings already open: the pane reports it selected again when the game list is redrawn (a game started or
            // closed, a folder change, a scan). Navigating again ran OnNavigatedTo on the cached page: back to the top, the
            // search box focused, the tab picked again (1.4.0-rc.20).
            if (Shown is SettingsPage) { Log.Info("Nav: Settings selected again, page kept"); return; }
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: HardwareTag })
        {
            if (Shown is not HardwarePage) ContentFrame.Navigate(typeof(HardwarePage));
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: LibraryTag })
        {
            AppState.Settings.LastGameId = LibraryTag;
            if (Shown is not LibraryPage) ContentFrame.Navigate(typeof(LibraryPage), _filter);
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: DownloadsTag })
        {
            if (Shown is not DownloadsPage) ContentFrame.Navigate(typeof(DownloadsPage));
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: string id } && id != "add" && id != DownloadsTag)
        {
            // A game listed as not installed opens on its Download page.
            if (AppState.Find(id) is null && OwnedLibrary.Find(id) is { } owned) AppState.AddOwned(OwnedLibrary.AsGame(owned));
            AppState.Settings.LastGameId = id;
            if ((Shown as GamePage)?.GameId != id)
                NavigateGame(id);
        }
    }

    void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == AddGameItem)
            FlyoutBase.ShowAttachedFlyout(AddGameItem);
        else if (args.InvokedItemContainer == FriendsItem)
            ShowFriends();
    }

    // ---------- Steam friends ----------

    FriendsView? _friends;
    Flyout? _friendsFlyout;

    /// <summary>The footer's Friends item shows while Steam is signed in in Hybrid Launcher, with the number of friends online.</summary>
    public void UpdateFriendsItem()
    {
        FriendsItem.Visibility = SteamAccount.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        FriendsItem.Content = L.T("Friends");
        FriendsItem.InfoBadge = SteamAccount.SignedIn && FriendsView.OnlineCount > 0
            ? new InfoBadge { Value = FriendsView.OnlineCount, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x57, 0xCB, 0xDE)) }
            : null;
    }

    void ShowFriends()
    {
        _friends ??= new FriendsView();
        _friendsFlyout ??= new Flyout { Content = _friends.Root, Placement = FlyoutPlacementMode.RightEdgeAlignedBottom };
        _friendsFlyout.ShowAt(FriendsItem);
        _ = _friends.LoadAsync();
    }

    void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _filter = sender.Text.Trim();
        RebuildNav();
    }

    public void SelectGame(string id)
    {
        AppState.Settings.LastGameId = id;
        _filter = "";
        SearchBox.Text = "";
        RebuildNav();
    }

    // ---------- add games ----------

    async void AddGameExe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = await Pickers.PickExeAsync(this);
            if (exe is null) return;
            var id = GameScanner.CustomId(exe);
            if (!AppState.Settings.CustomGames.Any(c => GameScanner.CustomId(c.ExePath) == id))
                AppState.Settings.CustomGames.Add(new CustomGame { ExePath = exe });
            AppState.Settings.HiddenGames.Remove(id);
            AppState.Settings.LastGameId = id;
            SettingsStore.Save();
            await AppState.RescanAsync();
        }
        catch (Exception ex)
        {
            ShowError(L.T("Could not add the game"), ex.Message, ex);
        }
    }

    async void AddLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = await Pickers.PickFolderAsync(this);
            if (dir is null) return;
            if (!AppState.Settings.GameLibraries.Contains(dir, StringComparer.OrdinalIgnoreCase))
                AppState.Settings.GameLibraries.Add(dir);
            SettingsStore.Save();
            await AppState.RescanAsync();
            ShowInfo(L.F("Added {0}. Each sub-folder with an .exe is listed as a game.", dir));
        }
        catch (Exception ex)
        {
            ShowError(L.T("Could not add the folder"), ex.Message, ex);
        }
    }

    void Rescan_Click(object sender, RoutedEventArgs e) => _ = AppState.RescanAsync();

    // Files dragged onto the window: a game's .exe is added as a game, a folder as a games folder.
    void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link;
        e.DragUIOverride.Caption = L.T("Add to Hybrid Launcher");
        e.DragUIOverride.IsGlyphVisible = false;
    }

    async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            int games = 0, folders = 0;
            string? last = null;
            foreach (var item in items)
            {
                var path = item.Path;
                if (string.IsNullOrEmpty(path)) continue;
                if (Directory.Exists(path))
                {
                    if (!AppState.Settings.GameLibraries.Contains(path, StringComparer.OrdinalIgnoreCase)) { AppState.Settings.GameLibraries.Add(path); folders++; }
                }
                else if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    var id = GameScanner.CustomId(path);
                    if (!AppState.Settings.CustomGames.Any(c => GameScanner.CustomId(c.ExePath) == id)) AppState.Settings.CustomGames.Add(new CustomGame { ExePath = path });
                    AppState.Settings.HiddenGames.Remove(id);
                    last = id;
                    games++;
                }
            }
            if (games == 0 && folders == 0)
            {
                ShowInfo(L.T("Drop a game's .exe to add it as a game, or a folder to add it as a games folder."));
                return;
            }
            if (last is not null) AppState.Settings.LastGameId = last;
            SettingsStore.Save();
            Log.Info($"Dropped on the window: {games} game exe, {folders} folders");
            await AppState.RescanAsync();
            if (folders > 0) ShowInfo(L.T("Folder added. Each sub-folder with an .exe is listed as a game."));
        }
        catch (Exception ex)
        {
            ShowError(L.T("Could not add the game"), ex.Message, ex);
        }
    }

    // ---------- self-test ----------

    /// <summary>Self-test "local unlock" (see HL_SELFTEST_LOCALACH): file → watcher → pop-up → history.</summary>
    async Task<int> SelfTestLocalUnlockAsync(string? shots)
    {
        try
        {
            var root = Path.Combine(AppInfo.DataRoot, "selftest-local-achievements");
            var file = Path.Combine(root, "480", "achievements.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            const string locked = "[ACH_WIN_ONE_GAME]\nAchieved=0\n[ACH_WIN_100_GAMES]\nAchieved=0\n";
            File.WriteAllText(file, locked);
            AppState.Settings.ReadSteamEmulator = true;
            AppState.Settings.AchievementPopups = true;
            if (!AppState.Settings.LocalAchievementFolders.Contains(root, StringComparer.OrdinalIgnoreCase)) AppState.Settings.LocalAchievementFolders.Add(root);
            AchievementWatcher.RestartLocal();
            await Task.Delay(1500);
            // A first change: the watcher reads the file and remembers what is unlocked (nothing).
            File.WriteAllText(file, locked + "\n");
            await Task.Delay(5000);
            AchievementPopup.CloseAll();
            int before = HybridLauncher.Core.Achievements.AchievementHistory.All().Count;
            File.WriteAllText(file, $"[ACH_WIN_ONE_GAME]\nAchieved=1\nUnlockTime={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n[ACH_WIN_100_GAMES]\nAchieved=0\n");
            bool popup = false, kept = false;
            for (int i = 0; i < 200 && !(popup && kept); i++)
            {
                await Task.Delay(100);
                if (!popup && AchievementPopup.CurrentHandle is { } h and not 0)
                {
                    popup = true;
                    if (!string.IsNullOrWhiteSpace(shots)) { await Task.Delay(900); await SaveShotAsync(Path.Combine(shots, "local unlock pop-up.png"), h); }
                }
                kept = HybridLauncher.Core.Achievements.AchievementHistory.All().Count > before;
            }
            if (!popup) throw new InvalidOperationException("no pop-up for the unlock written to the local file");
            if (!kept) throw new InvalidOperationException("the unlock is not in the history");
            Log.Info("Self-test: local unlock pop-up and history");
            AchievementPopup.CloseAll();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Self-test FAILED: local unlock", ex);
            return 1;
        }
    }

    /// <summary>Self-test "play restart" (see HL_SELFTEST_PLAY): hidden while the restarted game runs, back after it closes.</summary>
    async Task<int> SelfTestPlayRestartAsync(string exe)
    {
        try
        {
            var id = GameScanner.CustomId(exe);
            if (!AppState.Settings.CustomGames.Any(c => GameScanner.CustomId(c.ExePath) == id)) AppState.Settings.CustomGames.Add(new CustomGame { ExePath = exe });
            await AppState.RescanAsync();
            var game = AppState.Find(id) ?? throw new InvalidOperationException("the restarting game was not listed");
            bool savedHide = AppState.Settings.HideWhilePlaying;
            AppState.Settings.HideWhilePlaying = true;
            OpenGame(id);
            await Task.Delay(1500);
            await AppState.LaunchAsync(game);
            // Hidden once the game runs, and still hidden after it restarted itself.
            bool hiddenSeen = false;
            for (int i = 0; i < 150 && !hiddenSeen; i++) { await Task.Delay(100); hiddenSeen = !AppWindow.IsVisible; }
            if (!hiddenSeen) throw new InvalidOperationException("the window did not go to the notification area when the game started");
            for (int i = 0; i < 100 && !GameSessions.Active().Contains(id, StringComparer.OrdinalIgnoreCase); i++) await Task.Delay(100);
            await Task.Delay(6000);
            if (AppWindow.IsVisible) throw new InvalidOperationException("the window came back on screen while the restarted game runs");
            Log.Info("Self-test: play restart hidden while the restarted game runs");
            // Back once the game is gone (the restart wait is 20 s after a short session).
            bool back = false;
            for (int i = 0; i < 1200 && !back; i++) { await Task.Delay(100); back = AppWindow.IsVisible; }
            AppState.Settings.HideWhilePlaying = savedHide;
            if (!back) throw new InvalidOperationException("the window did not come back after the game closed");
            Log.Info("Self-test: play restart window back after the game closed");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Self-test FAILED: play restart", ex);
            if (!AppWindow.IsVisible) ShowFromTray(activate: false);
            return 1;
        }
    }

    /// <summary>
    /// HybridLauncher.exe --selftest: walks every page, game, tab and view, both game views and the pop-up, then closes.
    /// Errors land in the log (the app-wide handler catches them); the last line says how many there were.
    /// Meant for a separate data folder (HL_DATA): it changes the view setting while it runs.
    /// </summary>
    public async Task RunSelfTestAsync()
    {
        int failures = 0;
        // HL_SHOTS=<folder>: a picture of the window as on screen after each step, for checking the pages by eye (tests only).
        var shots = Environment.GetEnvironmentVariable("HL_SHOTS");
        int shot = 0;
        // HL_SELFTEST_ONLY=downloads,settings: only the steps whose name starts with one of these (a quick look at a few pages).
        var only = (Environment.GetEnvironmentVariable("HL_SELFTEST_ONLY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        async Task Step(string what, Action action, int waitMs = 700)
        {
            if (only.Length > 0 && !only.Any(o => what.StartsWith(o, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                action();
                await Task.Delay(waitMs);
                // No step clicks the search box: holding the focus after one means it grabbed it by itself (an old bug).
                if (SearchHasFocus()) throw new InvalidOperationException($"the game list's search box took the focus by itself (page shown: {Shown?.GetType().Name})");
                Log.Info($"Self-test: {what}");
                if (!string.IsNullOrWhiteSpace(shots)) await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-{string.Concat(what.Split(Path.GetInvalidFileNameChars()))}.png"));
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error($"Self-test FAILED: {what}", ex);
            }
        }

        for (int i = 0; i < 100 && AppState.Games.Count == 0; i++) await Task.Delay(200);
        // HL_DEMO=1: the demo walk for the video and the site's pictures instead of the tests (MainWindow.Demo).
        if (Environment.GetEnvironmentVariable("HL_DEMO") == "1")
        {
            int demo = await RunDemoAsync();
            Log.Info($"Self-test finished: {demo} failure(s)");
            Close();
            return;
        }
        if (only.Any(o => o.StartsWith("perf", StringComparison.OrdinalIgnoreCase)))
            failures += await SelfTestPerfAsync((what, action, wait) => Step(what, action, wait));
        else Helpers.UiPerf.EnableDetailed();
        // How quick the app feels, with budgets (MainWindow.Snappy): no input, the same numbers run after run.
        if (only.Any(o => o.StartsWith("snappy", StringComparison.OrdinalIgnoreCase)))
            failures += await SelfTestSnappyAsync();
        // Many game switches in a row, past the live pages' limit, with idle warm-ups between (2.0.0-rc.4 showed "Value does
        // not fall within the expected range" and an empty page after a while).
        if (only.Length == 0 || only.Any(o => "pages".StartsWith(o, StringComparison.OrdinalIgnoreCase)))
            failures += await SelfTestPageChurnAsync();
        await Step("library", () => ContentFrame.Navigate(typeof(LibraryPage), ""), 2500);
        // 2.1-rc.9: the middle of a cover takes the pointer (rc.8: every layer of the picture let it through, so clicks, hover
        // and right-clicks on the covers did nothing). XAML's own hit test, the one a real click goes through.
        await Step("library covers take the pointer", () =>
        {
            if ((Shown as LibraryPage)?.SelfTestFirstTile() is not GridViewItem tile || Content?.XamlRoot is not { } root) throw new InvalidOperationException("no cover shown");
            var at = tile.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(tile.ActualWidth / 2, tile.ActualHeight * 0.4));
            var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(at, root.Content).ToList();
            if (!hits.Contains(tile)) throw new InvalidOperationException($"a click at the cover's middle reaches {hits.FirstOrDefault()?.GetType().Name ?? "nothing"}, not the cover");
            Log.Info($"Self-test: a click on the first cover reaches it ({hits.Count} elements under the pointer)");
        });
        await Step("library search", () => SearchBox.Text = "a");
        await Step("library search cleared", () => SearchBox.Text = "");
        var savedFilter = AppState.Settings.Library;
        foreach (var sort in new[] { "recent", "added", "playtime", "achievements", "tech", "name" })
            await Step($"library sort {sort}", () => { AppState.Settings.Library = new LibraryFilter { Sort = sort, GroupByLauncher = sort != "tech" }; (Shown as LibraryPage)?.Reload(""); }, 400);
        await Step("library filters", () => { AppState.Settings.Library = new LibraryFilter { OptiOnly = true, Tech = { "DLSS" } }; (Shown as LibraryPage)?.Reload(""); }, 400);
        // 2.1: collections (Hydra style). A collection with one game shows that game alone, in the grid and in the list; the
        // favorites filter shows the favorites; the right-click menu has Collection, Create shortcut, Options and Properties.
        string? testCollection = null;
        await Step("library collections: one game", () =>
        {
            var first = AppState.VisibleGames.First(g => !g.NotInstalled);
            testCollection = GameCollections.Create("Self-test collection").Id;
            AppState.SetInCollection(testCollection, first.Id, true);
            AppState.Settings.Library = new LibraryFilter { Collection = testCollection };
            (Shown as LibraryPage)?.Reload("");
            var shown = Library.Apply(AppState.VisibleGames.Where(g => !g.NotInstalled));
            if (shown.Count != 1 || shown[0].Id != first.Id) throw new InvalidOperationException($"the collection shows {shown.Count} games");
            var menu = GameMenu(first);
            var subs = menu.Items.OfType<MenuFlyoutSubItem>().Select(i => i.Text).ToList();
            if (!subs.Contains(L.T("Collection")) || !subs.Contains(L.T("Options")) || !menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == L.T("Properties")))
                throw new InvalidOperationException($"the game's menu has {string.Join(", ", subs)}");
            Log.Info($"Self-test collections: {first.Name} alone in \"Self-test collection\"; menu: {string.Join(", ", subs)}");
            // 2.1-rc.9: the collection's coloured folder and the Steam logo of "Steam shortcut" are made from path data.
            _ = LibraryPage.CollectionIcon(testCollection);
            _ = StoreLogos.MenuIcon(GameStore.Steam);
        }, 900);
        await Step("library collections: favorites", () =>
        {
            AppState.Settings.Library = new LibraryFilter { Collection = GameCollections.Favorites };
            Library.ListFilter.Collection = GameCollections.Favorites;
            (Shown as LibraryPage)?.Reload("");
            RebuildNav();
            var shown = Library.Apply(AppState.VisibleGames.Where(g => !g.NotInstalled));
            if (shown.Any(g => !GameCollections.IsFavorite(g.Id))) throw new InvalidOperationException("a game that is not a favorite is shown");
        }, 900);
        await Step("library collections: delete", () =>
        {
            if (testCollection is not null) { GameCollections.Delete(testCollection); AppState.CollectionsEdited(); }
            Library.ListFilter.Collection = null;
            AppState.Settings.Library = new LibraryFilter();
            (Shown as LibraryPage)?.Reload("");
            RebuildNav();
            if (GameCollections.Find(testCollection) is not null) throw new InvalidOperationException("the collection is still there");
        }, 600);
        // 2.1-rc.3: the owned games not installed (this PC's launchers, read only), in their launchers' groups and in one block,
        // in the grid and in the side list; then a zoom with them on screen.
        await Step("library not installed: load", () => _ = OwnedLibrary.LoadAsync(), 3000);
        await Step("library not installed: grouped", () =>
        {
            AppState.Settings.Library = new LibraryFilter { ShowNotInstalled = true, GroupByLauncher = true, Sort = "name" };
            // The side list has its own filters since 2.1-rc.4: the same here, so both show the owned games.
            AppState.Settings.ListFilter = AppState.Settings.Library.CopyFilters();
            RebuildNav();
            Log.Info($"Self-test: {LibraryPage.OwnedShown("").Count} owned games not installed shown, list {Nav.MenuItems.Count} items");
        }, 1200);
        await Step("library not installed: one block", () =>
        {
            AppState.Settings.Library = new LibraryFilter { ShowNotInstalled = true, GroupByLauncher = false, Sort = "recent", Descending = true };
            AppState.Settings.ListFilter = AppState.Settings.Library.CopyFilters();
            RebuildNav();
        }, 1200);
        await Step("library not installed: zoom", () => { AppState.Settings.Library.TileSize = 150; (Shown as LibraryPage)?.Reload(""); }, 800);
        var savedList = AppState.Settings.ListFilter;
        await Step("library not installed: off", () => { AppState.Settings.Library = savedFilter; AppState.Settings.ListFilter = savedList; RebuildNav(); }, 800);
        await Step("library filters off", () => { AppState.Settings.Library = savedFilter; (Shown as LibraryPage)?.Reload(""); }, 400);
        await Step("library cover hover", () => (Shown as LibraryPage)?.SelfTestHover(true), 700);
        await Step("library cover hover off", () => (Shown as LibraryPage)?.SelfTestHover(false), 500);
        // 2.1-rc.9: Properties from the library opens over the library (the library stays the page shown), and the Artwork
        // window, restyled after Hydra's, opens and closes.
        await Step("library properties", () =>
        {
            var first = AppState.VisibleGames.First(g => !g.NotInstalled);
            ShowProperties(first.Id);
            if (Shown is not LibraryPage) throw new InvalidOperationException("Properties left the library");
            if (_propsLayer is not { Visibility: Visibility.Visible, Children.Count: > 0 }) throw new InvalidOperationException("Properties did not open over the library");
        }, 1200);
        await Step("library properties closed", () =>
        {
            _propsOwner?.CloseProperties();
            if (_propsLayer is { Visibility: Visibility.Visible }) throw new InvalidOperationException("Properties did not close");
        }, 500);
        await Step("library artwork", () =>
        {
            var first = AppState.VisibleGames.First(g => !g.NotInstalled);
            if (Content?.XamlRoot is { } root) _ = Controls.ArtworkDialog.ShowAsync(root, ((FrameworkElement)Content).ActualTheme, first, HybridLauncher.Core.Media.ArtKind.Banner);
        }, 1500);
        await Step("library artwork closed", () =>
        {
            if (Content?.XamlRoot is { } root)
                foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                    if (popup.Child is ContentDialog d) d.Hide();
        }, 600);
        // 2.1-rc.10 (Gemini's audit of rc.8/rc.9): two dialogs asked for at once, Properties of one game then another over the
        // library, the side list's Reset, a loaded cover's placeholder fill.
        int uiErrorsBefore = App.UiErrors;
        await Step("audit rc.10: two dialogs at once", () =>
        {
            var first = AppState.VisibleGames.First(g => !g.NotInstalled);
            if (Content?.XamlRoot is not { } root) return;
            var theme = ((FrameworkElement)Content).ActualTheme;
            _ = Controls.ArtworkDialog.ShowAsync(root, theme, first);
            _ = Controls.ArtworkDialog.ShowAsync(root, theme, first);
        }, 1200);
        await Step("audit rc.10: two dialogs at once, one shown", () =>
        {
            int open = 0;
            if (Content?.XamlRoot is { } root)
                foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                    if (popup.Child is ContentDialog d) { open++; d.Hide(); }
            if (App.UiErrors != uiErrorsBefore) throw new InvalidOperationException($"{App.UiErrors - uiErrorsBefore} UI errors");
            if (open != 1) throw new InvalidOperationException($"{open} dialogs open");
        }, 600);
        await Step("audit rc.10: Properties of two games in a row", () =>
        {
            var games = AppState.VisibleGames.Where(g => !g.NotInstalled).Take(2).ToList();
            if (games.Count < 2) { Log.Info("Self-test: one game only, Properties switch not tried"); return; }
            SearchBox.Text = "";
            ShowProperties(games[0].Id);
            var firstOwner = _propsOwner;
            ShowProperties(games[1].Id);
            if (_propsOwner is null || ReferenceEquals(_propsOwner, firstOwner) || _propsOwner.GameId != games[1].Id)
                throw new InvalidOperationException("the second game's Properties did not take the window");
            if (_propsLayer?.Children.Count != 1) throw new InvalidOperationException($"{_propsLayer?.Children.Count} windows in the layer");
            if (Shown is not LibraryPage) throw new InvalidOperationException("Properties left the library");
        }, 900);
        await Step("audit rc.10: Properties closed", () =>
        {
            _propsOwner?.CloseProperties();
            if (_propsOwner is not null || _propsLayer is { Visibility: Visibility.Visible }) throw new InvalidOperationException("Properties still open");
        }, 400);
        await Step("audit rc.10: side list Reset", () =>
        {
            var f = Library.ListFilter;
            f.OptiOnly = true;
            RefreshList();
            var panel = LibraryPage.FiltersPanel(() => { }, list: true);
            // The panel is not on screen: its buttons are found through its children, not the visual tree.
            static IEnumerable<Button> Buttons(object? o) => o switch
            {
                Button b => [b],
                Panel p => p.Children.SelectMany(c => Buttons(c)),
                Border b => Buttons(b.Child),
                ContentControl c => Buttons(c.Content),
                _ => [],
            };
            var reset = Buttons(panel).FirstOrDefault(b => b.Content as string == L.T("Reset order and filters"))
                ?? throw new InvalidOperationException("no Reset button");
            new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(reset).Invoke();
            if (Library.ListFilter.OptiOnly || ReferenceEquals(Library.ListFilter, f)) throw new InvalidOperationException("the side list's filters were not reset");
        }, 600);
        await Step("audit rc.10: loaded covers drop the placeholder fill", () =>
        {
            if ((Shown as LibraryPage)?.SelfTestFirstTile() is not GridViewItem { Content: FrameworkElement tile }) throw new InvalidOperationException("no cover shown");
            Log.Info($"Self-test: first cover's placeholder fill is {LibraryPage.CoverFill(tile)?.ToString() ?? "missing"}");
            if (LibraryPage.CoverFill(tile) is null) throw new InvalidOperationException("the cover has no placeholder fill layer");
        }, 400);
        // 2.1: the user's dashboard, empty then with a profile; a game's own cover and icon, then the store's again; the verify plan.
        // 2.1-rc.6: a game owned on Steam but not installed, whose achievements Steam keeps on this PC: its page shows its
        // hours and its achievements (they never showed: the page's load returned early for games not installed).
        if (OwnedLibrary.NotInstalled().FirstOrDefault(o => o.Store == GameStore.Steam && o.Id.StartsWith("steam:", StringComparison.Ordinal)
                && HybridLauncher.Core.Steam.SteamAchievements.HasSchema(o.Id[6..])) is { } ownedPick)
        {
            await Step("owned game page: open", () => OwnedLibrary.Open(ownedPick), 5000);
            await Step("owned game page: hours and achievements", () =>
            {
                if (Shown is not GamePage page) throw new InvalidOperationException("the game page is not shown");
                var (ach, hours) = page.SelfTestOwnedStats();
                Log.Info($"Self-test: {ownedPick.Name} (not installed): achievements {ach}, play time {hours}");
                if (ach is null) throw new InvalidOperationException("no achievements on the page of a game not installed");
            }, 300);
        }
        else Log.Info("Self-test: no Steam game owned but not installed with achievements on this PC: page check skipped");
        await Step("profile: none yet", () => OpenProfile(), 1500);
        await Step("profile: made", () =>
        {
            var pic = Path.Combine(AppInfo.ProfilesDir, "selftest.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"), pic, overwrite: true);
            Profiles.Add(new UserProfile { Name = "Alex Rivera", Picture = pic });
            if (Profiles.Active?.Name != "Alex Rivera") throw new InvalidOperationException("the profile is not in use");
        }, 2500);
        await Step("profile: chip under the title", () => { if (ProfileChip.Children.Count == 0) throw new InvalidOperationException("no profile chip"); }, 200);
        // 2.1-rc.4: the full lists over the dashboard, and a game hidden from Most played then shown again.
        foreach (var list in new[] { "played", "weeks", "progress", "close", "hide", "show" })
            await Step($"profile: list {list}", () => (Shown as ProfilePage)?.SelfTestList(list), 900);
        if (AppState.VisibleGames.FirstOrDefault() is { } artGame)
        {
            await Step("artwork: own cover and icon", () =>
            {
                var png = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
                HybridLauncher.Core.Media.ArtOverrides.Import(artGame.Id, HybridLauncher.Core.Media.ArtKind.Cover, png);
                HybridLauncher.Core.Media.ArtOverrides.Import(artGame.Id, HybridLauncher.Core.Media.ArtKind.Icon, png);
                if (HybridLauncher.Core.Media.ArtOverrides.Get(artGame.Id, HybridLauncher.Core.Media.ArtKind.Cover) is null) throw new InvalidOperationException("the cover was not kept");
                ContentFrame.Navigate(typeof(LibraryPage), "");
            }, 1500);
            await Step("artwork: the store's again", () =>
            {
                HybridLauncher.Core.Media.ArtOverrides.Set(artGame.Id, HybridLauncher.Core.Media.ArtKind.Cover, null);
                HybridLauncher.Core.Media.ArtOverrides.Set(artGame.Id, HybridLauncher.Core.Media.ArtKind.Icon, null);
                if (HybridLauncher.Core.Media.ArtOverrides.Get(artGame.Id, HybridLauncher.Core.Media.ArtKind.Cover) is not null) throw new InvalidOperationException("the cover stayed");
            }, 800);
            await Step("verify: plan", () =>
            {
                var plan = GameVerify.For(artGame);
                Log.Info($"Verify plan for {artGame.Name}: {plan.Launcher ?? "none"} {(plan.Direct ? "direct" : "steps")}");
                if (artGame.Store == GameStore.Steam && !plan.Direct) throw new InvalidOperationException("a Steam game should verify directly");
            }, 200);
        }
        // Mouse-wheel scrolling, measured frame by frame (MainWindow.Smoothness): the library, then a game page.
        // HL_SELFTEST_NOINPUT=1: no real wheel or mouse (the PC is in use); the "snappy" step measures scrolling without input.
        bool noInput = Environment.GetEnvironmentVariable("HL_SELFTEST_NOINPUT") == "1";
        if (!noInput && (only.Length == 0 || only.Any(o => "scroll".StartsWith(o, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var target in new[] { "library", "game page", "settings" })
            {
                try
                {
                    if (target == "game page" && AppState.VisibleGames.FirstOrDefault(g => !g.NotInstalled) is { } g) { OpenGame(g.Id); await Task.Delay(2500); }
                    if (target == "settings") { OpenSettings("achievements"); await Task.Delay(1500); }
                    Microsoft.UI.Xaml.FrameworkElement? sv = (Shown as LibraryPage)?.SelfTestScroller ?? (Microsoft.UI.Xaml.FrameworkElement?)(Shown as GamePage)?.SelfTestScroller ?? (Shown as SettingsPage)?.SelfTestScroller;
                    if (sv is null || (sv is ScrollViewer sva ? sva.ScrollableHeight : sv is ScrollView svb ? svb.ScrollableHeight : 0) < 200) { Log.Info($"Scroll smoothness {target}: nothing to scroll"); continue; }
                    Activate();
                    var r = await MeasureWheelScrollAsync(sv, target);
                    // A healthy scroll: the view answers the wheel within a few frames (the clock kept awake by the mouse,
                    // Helpers.FrameWarmth) and goes the whole way. On a ScrollView the motion runs on the compositor
                    // (Helpers.CompositorWheel): the UI thread's frames and the offset it reads lag behind the screen, so
                    // missed UI frames and offset stalls say nothing about what is shown there (measured from outside with
                    // Tools\media\measure-scroll-smoothness.py); they are checked on a ScrollViewer only.
                    bool onCompositor = sv is ScrollView;
                    if (!onCompositor && r.UiHitches > Math.Max(3, r.Frames / 20)) throw new InvalidOperationException("missed frames: " + r);
                    if (r.AnswerMs is < 0 or > 80) throw new InvalidOperationException("slow to answer the wheel: " + r);
                    if (!onCompositor && r.Stalls > 2) throw new InvalidOperationException("stops while scrolling: " + r);
                    if (r.Distance < 100) throw new InvalidOperationException("did not scroll: " + r);
                    Log.Info($"Self-test: scroll {target}");
                }
                catch (Exception ex) { failures++; Log.Error($"Self-test FAILED: scroll {target}", ex); }
            }
            ContentFrame.Navigate(typeof(LibraryPage), "");
            await Task.Delay(800);
        }
        // Game switches as a click makes them (the mouse in use, so the frame clock is awake): each one's first frame and
        // settled time are logged by the game page ("Game page …: first frame").
        if (only.Length == 0 || only.Any(o => "switch".StartsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            var installed = AppState.VisibleGames.Where(g => !g.NotInstalled).Take(4).ToList();
            for (int round = 0; round < 2 && installed.Count > 1; round++)
                foreach (var g in installed)
                {
                    Helpers.FrameWarmth.Poke();
                    await Task.Delay(120);
                    Helpers.FrameWarmth.Poke();
                    OpenGame(g.Id);
                    await Task.Delay(1200);
                }
            ContentFrame.Navigate(typeof(LibraryPage), "");
            await Task.Delay(800);
        }
        // Resizing a maximized window leaves it "maximized" at the new, small size (Restore shown, Maximize doing nothing):
        // restore it first, and maximize it again afterwards if it was.
        var wasMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (wasMaximized && AppWindow.Presenter is OverlappedPresenter rp) rp.Restore();
        await Step("window narrow", () => AppWindow.Resize(new SizeInt32((int)(700 * GetDpiScale()), (int)(800 * GetDpiScale()))), 800);
        await Step("window wide", () => AppWindow.Resize(new SizeInt32((int)(1600 * GetDpiScale()), (int)(950 * GetDpiScale()))), 800);
        if (wasMaximized && AppWindow.Presenter is OverlappedPresenter mp) mp.Maximize();
        await Step("window state back", () => { if (wasMaximized && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Maximized }) throw new InvalidOperationException("the window did not go back to maximized"); }, 500);
        // A maximized window that ends up smaller than its screen (as after a resolution change: 1440p to 1080p) is maximized again.
        if (wasMaximized)
        {
            await Step("maximized window shrunk", () => AppWindow.Resize(new SizeInt32((int)(900 * GetDpiScale()), (int)(700 * GetDpiScale()))), 1500);
            await Step("maximized window covers the screen again", () =>
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Maximized } || AppWindow.Size.Width < area.Width - 4)
                    throw new InvalidOperationException($"still {AppWindow.Size.Width} wide on a {area.Width} work area");
            }, 300);
        }
        // Issue #5: a narrow window (game list folded to its strip) maximized afterwards kept the page as wide as beside the
        // strip while the list opened beside it: Settings ran past the right edge.
        await Step("settings before the narrow window", () => ContentFrame.Navigate(typeof(SettingsPage)), 900);
        await Step("narrow window, list folded", () =>
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } np) np.Restore();
            AppWindow.Resize(new SizeInt32((int)(1000 * GetDpiScale()), (int)(760 * GetDpiScale())));
        }, 900);
        await Step("narrow window maximized", () => { if (AppWindow.Presenter is OverlappedPresenter xp) xp.Maximize(); }, 1500);
        await Step("page fits beside the list", () =>
        {
            var (overflow, state) = PageOverflow();
            Log.Info($"Page beside the list: {state}");
            if (overflow > 2) throw new InvalidOperationException($"the page runs {overflow:0} px past the window ({state})");
        }, 100);
        // Every Settings tab at this size: nothing visible may end past the window's right edge.
        foreach (var tab in new[] { "appearance", "features", "controller", "achievements", "notifications", "cloud", "storage", "about" })
        {
            await Step($"settings tab {tab} fits", () => OpenSettings(tab), 900);
            await Step($"settings tab {tab} nothing cut", () =>
            {
                var cut = CutOnTheRight(ContentFrame);
                if (cut.Count > 0) throw new InvalidOperationException($"past the right edge: {string.Join("; ", cut.Take(6))}");
                // Every tab the same column: as wide as the page up to 1600, centred.
                if (Shown is SettingsPage sp && sp.SelfTestColumn() is var (w, expected, off, sub) && (Math.Abs(w - expected) > 1 || off > 2 || sub > 0.05))
                    throw new InvalidOperationException($"column {w:0} wide (should be {expected:0}), {off:0} px off centre, {sub:0.00} px off a whole pixel");
            }, 50);
        }
        // Achievements → Sources (1.4.0): every source's tile and the folders local files are read from.
        await Step("settings sources", () =>
        {
            OpenSettings("achievements");
            if (Shown is SettingsPage sp && !sp.SelfTestGoTo("achievements", L.T("Local achievement folders")))
                throw new InvalidOperationException("the Sources section has no Local achievement folders row");
        }, 1500);
        // The other pages and every tab of a game page, at the same size.
        foreach (var (name, type) in new[] { ("library", typeof(LibraryPage)), ("downloads", typeof(DownloadsPage)), ("my pc", typeof(HardwarePage)) })
        {
            await Step($"page {name} fits", () => ContentFrame.Navigate(type), 1500);
            await Step($"page {name} nothing cut", () =>
            {
                var cut = CutOnTheRight(ContentFrame);
                if (cut.Count > 0) throw new InvalidOperationException($"past the right edge: {string.Join("; ", cut.Take(6))}");
            }, 50);
        }
        if (AppState.Games.FirstOrDefault() is { } fitGame)
        {
            await Step("page game fits", () => OpenGame(fitGame.Id), 2500);
            foreach (var tab in (Shown as GamePage)?.SelfTestTabIds() ?? [])
            {
                await Step($"page game tab {tab} fits", () => (Shown as GamePage)?.SelfTestShow(tab), 1500);
                await Step($"page game tab {tab} nothing cut", () =>
                {
                    var cut = CutOnTheRight(ContentFrame);
                    if (cut.Count > 0) throw new InvalidOperationException($"past the right edge: {string.Join("; ", cut.Take(6))}");
                }, 50);
            }
        }
        if (!wasMaximized && AppWindow.Presenter is OverlappedPresenter wp2) wp2.Restore();
        if (Environment.GetEnvironmentVariable("HL_SAMPLE_DOWNLOADS") == "1") Downloads.AddSamples();
        await Step("downloads", () => ContentFrame.Navigate(typeof(DownloadsPage)), 2500);
        // The focused list item thrown away by a redraw: WinUI used to hand the focus to the search box.
        await Step("list redrawn under the focus", () => { (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault())?.Focus(FocusState.Keyboard); RebuildNav(); }, 500);
        await Step("search by Ctrl+F", () =>
        {
            _searchWanted = true;
            SearchBox.Focus(FocusState.Keyboard);
            _searchWanted = false;
            if (!SearchHasFocus()) throw new InvalidOperationException("Ctrl+F could not put the focus in the search box");
            ContentFrame.Focus(FocusState.Programmatic);
        }, 300);
        // The blue Update button beside Minimize: shown for a newer version (a local feed stands in for GitHub), gone without one.
        var feed = Path.Combine(Path.GetTempPath(), "hl-selftest-feed.json");
        var savedFeed = Environment.GetEnvironmentVariable("HL_UPDATE_FEED");
        void Feed(string tag) => File.WriteAllText(feed, $$"""{"tag_name":"{{tag}}","body":"Self-test","html_url":"https://example.invalid","assets":[{"name":"HybridLauncher-{{tag}}-win-x64.zip","size":1,"browser_download_url":"https://example.invalid/a.zip"},{"name":"HybridLauncher-{{tag}}-win-arm64.zip","size":1,"browser_download_url":"https://example.invalid/b.zip"}]}""");
        Environment.SetEnvironmentVariable("HL_UPDATE_FEED", feed);
        await Step("update button: newer version out", () => { Feed("v99.0.0"); _ = Helpers.AppUpdater.CheckAsync(quiet: true); }, 1500);
        await Step("update button shown", () => { if (UpdateButton.Visibility != Visibility.Visible) throw new InvalidOperationException("the Update button did not appear for a newer version"); }, 300);
        await Step("update button: no newer version", () => { Feed("v0.0.1"); _ = Helpers.AppUpdater.CheckAsync(quiet: true); }, 1500);
        await Step("update button hidden", () => { if (UpdateButton.Visibility != Visibility.Collapsed) throw new InvalidOperationException("the Update button stayed without a newer version"); }, 300);
        Environment.SetEnvironmentVariable("HL_UPDATE_FEED", savedFeed);
        File.Delete(feed);
        // The bell: the newer version above was kept; more notifications raise the count, opening the list reads them, Clear empties it.
        await Step("bell: notifications kept", () =>
        {
            if (!Inbox.Entries.Any(x => x.Key == "app:99.0.0")) throw new InvalidOperationException("the newer version was not kept in the bell's list");
            var id = AppState.VisibleGames.FirstOrDefault()?.Id;
            Inbox.Add(Inbox.Opti, "OptiScaler Nightly: nightly-selftest is out", "Self-test", id, "opti");
            Inbox.Add(Inbox.Ultra, "Ultra+ updated to 9.9 for a self-test", "Self-test", id, "ultra");
        }, 500);
        await Step("bell: badge counts them", () => { if (BellBadge.Visibility != Visibility.Visible || Inbox.Unread < 3) throw new InvalidOperationException($"badge {BellBadge.Visibility}, {Inbox.Unread} unread"); }, 300);
        await Step("bell: list open", () => BellFlyout.ShowAt(BellButton), 900);
        await Step("bell: list closed reads them", () => BellFlyout.Hide(), 600);
        await Step("bell: badge gone", () => { if (BellBadge.Visibility != Visibility.Collapsed || Inbox.Unread != 0) throw new InvalidOperationException($"{Inbox.Unread} still unread"); }, 300);
        await Step("bell: clear", () => { Inbox.Clear(); BellFlyout.ShowAt(BellButton); }, 900);
        await Step("bell: empty list closed", () => { BellFlyout.Hide(); if (Inbox.Entries.Count != 0) throw new InvalidOperationException("Clear left entries"); }, 400);
        // Controller navigation off (Settings → Features): the controller is never read, whatever is connected; back on after.
        await Step("controller off", () =>
        {
            AppState.Settings.GamepadInput = false;
            GamepadSettingChanged();
            if (PadReading) throw new InvalidOperationException("the controller is still read with Controller navigation off");
        }, 200);
        await Step("controller on", () => { AppState.Settings.GamepadInput = true; GamepadSettingChanged(); }, 200);
        // Through the pane button's own path (SetPaneOpen: NavigationView refuses with E_FAIL while its last change is still
        // under way, and that is retried on the next tick), then checked.
        await Step("pane folded", () => SetPaneOpen(false), 500);
        await Step("pane folded: checked", () => { if (Nav.IsPaneOpen) throw new InvalidOperationException("the game list is still open"); }, 50);
        await Step("pane open", () => SetPaneOpen(true), 500);
        await Step("pane open: checked", () => { if (!Nav.IsPaneOpen) throw new InvalidOperationException("the game list did not open"); }, 50);
        foreach (var game in AppState.VisibleGames.ToList())
        {
            await Step($"open {game.Name}", () => OpenGame(game.Id), 2500);
            await Step($"{game.Name}: pin", () => AppState.SetPinned(game.Id, true));
            foreach (var (tab, view) in new[] { ("overview", (string?)null), ("opti", null), ("opti/quickpanel", null), ("opti/settings", "essentials"), ("opti/settings", "restart"), ("opti/settings", "changed"), ("opti/settings", "all"), ("opti/wiki", null), ("opti/notes", null), ("opti/install", null), ("mods", null), ("mods/renodx", null), ("mods/luma", null), ("mods/dlss5", null), ("mods/dxvk", null), ("mods/vkd3d", null), ("mods/reshade", null), ("mods/installed", null), ("auto", null), ("ultra", null), ("ultra/addons", null), ("ultra/settings", null), ("pcgw", null), ("pcgw/issues", null), ("pcgw/page2", null), ("ach", null), ("links", null), ("quick", null), ("notes", null), ("overview/news", null), ("overview/reviews", null), ("overview", null) })
                await Step($"{game.Name}: {tab} {view}", () => (Shown as GamePage)?.SelfTestShow(tab, view), tab is "overview" or "opti/wiki" or "opti/notes" or "pcgw" or "links" or "mods" or "mods/renodx" or "mods/luma" or "mods/dxvk" or "mods/vkd3d" or "mods/installed" or "auto" or "ultra" ? 2500 : 600);
            // The Auto tab (rc.7) read the game and made a plan; then, on a fake game, its one click with ReShade ticked puts
            // OptiScaler and ReShade in together: ReShade as ReShade64.dll, loaded by OptiScaler.
            await Step($"{game.Name}: auto plan", () =>
            {
                var plan = (Shown as GamePage)?.SelfTestAuto() ?? "no page";
                Log.Info($"Self-test Auto plan: {plan}");
                if (!plan.Contains("opti:") || !plan.Contains("reshade:")) throw new InvalidOperationException($"no Auto plan: {plan}");
                // 2.0.0-rc.18: a generic RenoDX or Luma mod is offered, never ticked by itself.
                if (plan.Contains("renodx-generic=True") && plan.Contains("renodx:Install[x]"))
                    throw new InvalidOperationException($"a generic RenoDX mod was ticked by itself: {plan}");
                if (plan.Contains("luma-generic=True") && plan.Contains("luma:Install[x]"))
                    throw new InvalidOperationException($"a generic Luma mod was ticked by itself: {plan}");
            }, 1500);
            // 2.0.0-rc.18: OptiScaler's builds can be added from the Auto tab as on the OptiScaler tab (folder, archive, GitHub fork).
            await Step($"{game.Name}: auto add build menu", () =>
            {
                int n = (Shown as GamePage)?.SelfTestAutoAddBuildMenu(true) ?? -1;
                if (n is >= 0 and not 3) throw new InvalidOperationException($"the Add a build menu has {n} entries, not 3");
            }, 900);
            await Step($"{game.Name}: auto add build menu closed", () => (Shown as GamePage)?.SelfTestAutoAddBuildMenu(false), 400);
            // DLSS 5's F5 way picked: OptiScaler's build follows to janblade's F5 fork, its releases read; then back to Off.
            await Step($"{game.Name}: auto dlss5 f5", () => (Shown as GamePage)?.SelfTestAutoDlss5("f5"), 6000);
            await Step($"{game.Name}: auto dlss5 f5 build", () =>
            {
                var plan = (Shown as GamePage)?.SelfTestAuto() ?? "no page";
                Log.Info($"Self-test Auto plan with F5: {plan}");
                if (!plan.Contains("dlss5=f5") || !plan.Contains("source=fork:janblade/") || plan.Contains("build=-")) throw new InvalidOperationException($"OptiScaler's build did not follow F5: {plan}");
            }, 1500);
            await Step($"{game.Name}: auto dlss5 off", () => (Shown as GamePage)?.SelfTestAutoDlss5("off"), 3000);
            // 2.0.0-rc.13: DLSS 5 ticked with no method: "Select one", and Install waits for a method.
            await Step($"{game.Name}: auto dlss5 ticked without a method", () =>
            {
                var plan = (Shown as GamePage)?.SelfTestAutoDlss5Tick() ?? "no page";
                Log.Info($"Self-test Auto with DLSS 5 ticked: {plan}");
                if (!plan.Contains("dlss5=pending") || !plan.Contains("install-enabled=False")) throw new InvalidOperationException("DLSS 5 ticked without a method did not stop Install: " + plan);
            }, 800);
            await Step($"{game.Name}: auto dlss5 untick", () => (Shown as GamePage)?.SelfTestAutoDlss5("off"), 1500);
            // 2.0.0-rc.13: ReShade's build picked by hand, then back to the recommendation.
            await Step($"{game.Name}: auto reshade build", () =>
            {
                var page = Shown as GamePage;
                var addon = page?.SelfTestAutoReShadeBuild(true) ?? "no page";
                var standard = page?.SelfTestAutoReShadeBuild(false) ?? "no page";
                Log.Info($"Self-test Auto ReShade builds: {addon} || {standard}");
                page?.SelfTestAutoReShadeBuild(null);
                if (addon.Contains("reshade:NotPossible")) return;
                if (!addon.Contains("reshade-build=addon") || !standard.Contains("reshade-build=standard")) throw new InvalidOperationException("ReShade's build did not follow the pick: " + addon + " || " + standard);
            }, 1500);
            if (Environment.GetEnvironmentVariable("HL_SELFTEST_AUTO") == "1")
            {
                await Step($"{game.Name}: auto install", () => _ = (Shown as GamePage)?.SelfTestAutoRunAsync(), 90_000);
                await Step($"{game.Name}: auto installed", () =>
                {
                    var page = Shown as GamePage;
                    var dir = page?.SelfTestTargetDir ?? throw new InvalidOperationException("no game folder");
                    var plan = page.SelfTestAuto();
                    Log.Info($"Self-test Auto after the one click: {plan}");
                    bool opti = Core.Opti.OptiDetector.Detect(dir) is not null;
                    var rs = Core.Mods.ReShade.Loaded(dir);
                    if (!opti || rs is null || !rs.LoadedByOpti) throw new InvalidOperationException($"OptiScaler {(opti ? "in" : "missing")}, ReShade {(rs is null ? "not loaded" : rs.FileName)}; plan {plan}");
                }, 3000);
                // 2.0.0-rc.12: away to the library and back, as a person does. The page comes back as it was left (Resume) with
                // a new token; the Auto tab kept the cancelled one and never read the folder again.
                await Step($"{game.Name}: auto away to the library", () => ContentFrame.Navigate(typeof(LibraryPage), ""), 1500);
                await Step($"{game.Name}: auto back", () => OpenGame(game.Id), 2500);
                await Step($"{game.Name}: auto back on the tab", () => (Shown as GamePage)?.SelfTestShow("auto", null), 2500);
                // 2.0.0-rc.10: the plan follows the folder by itself. ReShade's dll taken out by hand (as Explorer would) shows as
                // not installed without leaving the tab.
                await Step($"{game.Name}: auto follows the folder", () =>
                {
                    var page = Shown as GamePage;
                    var dir = page?.SelfTestTargetDir ?? throw new InvalidOperationException("no game folder");
                    if (Core.Mods.ReShade.Detect(dir) is { } rs) File.Delete(rs.Path);
                }, 3000);
                await Step($"{game.Name}: auto saw ReShade go", () =>
                {
                    var plan = (Shown as GamePage)?.SelfTestAuto() ?? "no page";
                    Log.Info($"Self-test Auto after ReShade was deleted: {plan}");
                    if (plan.Contains("reshade:Installed", StringComparison.Ordinal)) throw new InvalidOperationException("the Auto tab still shows ReShade installed: " + plan);
                }, 50);
            }
            // The DXVK and VKD3D-Proton sections filled themselves: the one for the game's Direct3D loaded (version picked, releases
            // listed, hint written), the other loaded too or folded to one line (both folded for a Vulkan game); none still
            // waiting or missing releases.
            await Step($"{game.Name}: dxvk section loaded", () =>
            {
                var state = (Shown as GamePage)?.SelfTestDxvk() ?? "no DXVK section";
                Log.Info($"Self-test DXVK sections: {state}");
                var parts = state.Split(" || ");
                // A game that draws with Vulkan needs neither: both fold to one line saying so (DOOM: The Dark Ages, Indiana Jones).
                bool vulkan = parts.Length == 2 && parts.All(p => p.Contains("draws with Vulkan", StringComparison.Ordinal));
                if (parts.Length != 2 || (!vulkan && !parts.Any(p => p.Contains(" loaded", StringComparison.Ordinal)))
                    || parts.Any(p => !p.Contains(" loaded", StringComparison.Ordinal) && !p.Contains(" folded:", StringComparison.Ordinal)))
                    throw new InvalidOperationException(state);
            }, 300);
            // The mod check: an add-on with no ReShade beside it puts a warning on the banner, and it goes once the add-on does.
            await Step($"{game.Name}: mod check warns", () => { var page = Shown as GamePage; page?.SelfTestScroll(false); page?.SelfTestModProblem(true); }, 1500);
            await Step($"{game.Name}: mod check warning shown", () =>
            {
                var badges = (Shown as GamePage)?.SelfTestBadges() ?? "";
                Log.Info($"Self-test banner tags: {badges}");
                if (!badges.Contains("no ReShade to load", StringComparison.Ordinal)) throw new InvalidOperationException($"no mod check warning on the banner: {badges}");
            }, 200);
            // The same finding in the Mods tab's table, with its fix and Ignore (rc.18).
            await Step($"{game.Name}: mod check in the Mods table", () => (Shown as GamePage)?.SelfTestShow("mods/installed"), 1800);
            await Step($"{game.Name}: mod check in the Mods table listed", () =>
            {
                var table = (Shown as GamePage)?.SelfTestModsTable() ?? "no page";
                Log.Info($"Self-test mods table with a finding: {table}");
                if (table.EndsWith("problems=0", StringComparison.Ordinal) || table is "no page" or "not read") throw new InvalidOperationException($"the finding is not in the Mods table: {table}");
            }, 50);
            await Step($"{game.Name}: mod check clears", () => (Shown as GamePage)?.SelfTestModProblem(false), 1500);
            await Step($"{game.Name}: mod check warning gone", () =>
            {
                var badges = (Shown as GamePage)?.SelfTestBadges() ?? "";
                if (badges.Contains("renodx-selftest")) throw new InvalidOperationException($"the warning stayed: {badges}");
            }, 200);
            // OptiScaler's file name swapped in place (needs HL_SELFTEST_OPTI = an OptiScaler.dll).
            if (Environment.GetEnvironmentVariable("HL_SELFTEST_OPTI") is { Length: > 0 } optiDll && File.Exists(optiDll))
            {
                await Step($"{game.Name}: dll swap menu", () => (Shown as GamePage)?.SelfTestDllSwapMenu(optiDll), 1200);
                string swapped = "";
                if (Shown is GamePage swapPage && (only.Length == 0 || only.Any(o => $"{game.Name}: dll swap".StartsWith(o, StringComparison.OrdinalIgnoreCase))))
                {
                    try { swapped = await swapPage.SelfTestDllSwapAsync("winmm.dll"); }
                    catch (Exception ex) { swapped = ex.Message; }
                    Log.Info($"Self-test dll swap: {swapped}");
                    if (!swapped.StartsWith("winmm.dll chip=winmm.dll found=winmm.dll setup=winmm.dll", StringComparison.Ordinal)) { failures++; Log.Error($"Self-test FAILED: dll swap: {swapped}"); }
                }
                await Step($"{game.Name}: dll swap done", () => { }, 1200);
                // What is installed as what: OptiScaler shows as winmm.dll now, put in by Hybrid Launcher.
                await Step($"{game.Name}: dll swap mod files", () => (Shown as GamePage)?.SelfTestModFilesOpen(), 1500);
                await Step($"{game.Name}: dll swap mod files listed", () =>
                {
                    var rows = (Shown as GamePage)?.SelfTestModFilesClose() ?? "";
                    Log.Info($"Self-test mod files: {rows}");
                    if (!rows.Contains("winmm.dll = OptiScaler", StringComparison.Ordinal) || !rows.Contains("[HL]", StringComparison.Ordinal))
                        throw new InvalidOperationException($"OptiScaler as winmm.dll is not listed: {rows}");
                }, 600);
            }
            await Step($"{game.Name}: scroll (tabs pinned on top)", () => (Shown as GamePage)?.SelfTestScroll(true), 600);
            // A second picture of the same place a little later: what moves on its own (the clips of a description) differs between the two.
            await Step($"{game.Name}: scroll (still there)", () => { }, 1500);
            await Step($"{game.Name}: scroll back", () => (Shown as GamePage)?.SelfTestScroll(false), 600);
            // The Dashboard tab (1.4.0): the cards shown, nothing past the right edge, a post read in the app.
            await Step($"{game.Name}: dashboard", () => (Shown as GamePage)?.SelfTestDashboard(), 3500);
            await Step($"{game.Name}: dashboard shown", () =>
            {
                if ((Shown as GamePage)?.SelfTestDashboard() is not { } d) throw new InvalidOperationException("the dashboard was not made");
                Log.Info($"Self-test dashboard: {d.Cards} cards, news {(d.NewsLoaded ? "read" : "still loading")}, {d.Activity} activity lines");
                if (d.Cards == 0 && AppState.Settings.DashboardHidden.Count < Views.DashboardView.All.Length) throw new InvalidOperationException("no card shown");
                var cut = CutOnTheRight(ContentFrame);
                if (cut.Count > 0) throw new InvalidOperationException($"past the right edge: {string.Join("; ", cut.Take(6))}");
            }, 50);
            // Cards reordered and hidden as the customize panel does it, then back (2.0.0-rc.6: building them all again lost their parts).
            await Step($"{game.Name}: dashboard reorder", () =>
            {
                int errors = App.UiErrors;
                Log.Info("Self-test dashboard reorder: " + ((Shown as GamePage)?.SelfTestDashboardReorder() ?? "no page"));
                if (App.UiErrors != errors) throw new InvalidOperationException("a UI error while reordering");
            }, 1200);
            await Step($"{game.Name}: dashboard scrolled", () => (Shown as GamePage)?.SelfTestScroll(true), 600);
            // The rest of the Dashboard, screen by screen (pictures; nothing is checked).
            for (int screen = 1; screen <= 4; screen++)
            {
                double below = screen * 1150;
                await Step($"{game.Name}: dashboard lower {screen}", () => (Shown as GamePage)?.SelfTestScrollBelow(below), 900);
            }
            // A window too narrow for two columns: the side cards go under the main column.
            {
                bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
                if (maximized && AppWindow.Presenter is OverlappedPresenter restore) restore.Restore();
                await Step($"{game.Name}: dashboard narrow", () => { AppWindow.Resize(new SizeInt32((int)(1320 * GetDpiScale()), (int)(940 * GetDpiScale()))); (Shown as GamePage)?.SelfTestScrollBelow(0); }, 1200);
                // A picture only: at this width the tab row itself scrolls sideways, which the right-edge check would report.
                await Step($"{game.Name}: dashboard narrow, side cards", () => (Shown as GamePage)?.SelfTestScrollToDashboardSide(), 900);
                if (maximized && AppWindow.Presenter is OverlappedPresenter again) again.Maximize();
                await Task.Delay(700);
            }
            await Step($"{game.Name}: dashboard post", () => { (Shown as GamePage)?.SelfTestScroll(false); (Shown as GamePage)?.SelfTestReadPost(); }, 1800);
            await Step($"{game.Name}: dashboard post closed", () => CloseDialogs(), 500);
            // The Achievements card on made-up achievements (rc.11: the latest and the rarest drawn alike, tier trophies).
            await Step($"{game.Name}: dashboard achievements", () => { (Shown as GamePage)?.SelfTestSampleAchievements(); (Shown as GamePage)?.SelfTestScrollBelow(0); }, 1500);
            // The version history (rc.11: tiles a year, a bar a month, the table at full width).
            await Step($"{game.Name}: version history", () => (Shown as GamePage)?.SelfTestHistory(true), 1500);
            await Step($"{game.Name}: version history closed", () => (Shown as GamePage)?.SelfTestHistory(false), 400);
            // DLSS 5 (rc.13 a window, rc.18 a section of the Mods tab): every page drawn, then back to its overview.
            foreach (var dlssPage in new[] { "overview", "model", "amd", "f5", "logs" })
                await Step($"{game.Name}: dlss5 {dlssPage}", () => (Shown as GamePage)?.SelfTestDlss5(dlssPage), 700);
            await Step($"{game.Name}: dlss5 back to overview", () => (Shown as GamePage)?.SelfTestDlss5(null), 300);
            // The Mods tab's table of installed mods (rc.18): read and drawn, one line per mod.
            await Step($"{game.Name}: mods table", () => (Shown as GamePage)?.SelfTestShow("mods/installed"), 2000);
            await Step($"{game.Name}: mods table drawn", () =>
            {
                var table = (Shown as GamePage)?.SelfTestModsTable() ?? "no page";
                Log.Info($"Self-test mods table: {table}");
                if (table is "no page" or "not read") throw new InvalidOperationException($"the Mods table was not drawn: {table}");
                var cut = CutOnTheRight(ContentFrame);
                if (cut.Count > 0) throw new InvalidOperationException($"past the right edge: {string.Join("; ", cut.Take(6))}");
            }, 50);
            await Step($"{game.Name}: unpin", () => AppState.SetPinned(game.Id, false));
        }
        // An install keeps showing when its game is left and opened again (the page is made again): busy, its progress and
        // log, no second install in the same folder, and free again when it ends. A stand-in job: nothing is installed.
        if (AppState.VisibleGames.FirstOrDefault() is { } jobGame)
        {
            Helpers.GameJobs.Job? fake = null;
            GamePage Page() => Shown as GamePage ?? throw new InvalidOperationException("the game page is not open");
            await Step("play button widths", () => Log.Info("Play button: " + Page().SelfTestPlayWidths()), 300);
            await Step("install kept: started", () =>
            {
                OpenGame(jobGame.Id);
                fake = Helpers.GameJobs.TryStart(jobGame.Id, jobGame.Title, "opti", "OptiScaler", quiet: true) ?? throw new InvalidOperationException("a job is already running");
                Helpers.GameJobs.Report(fake, 0.4, "Self-test install", "self-test line");
            }, 1500);
            await Step("install kept: page busy", () => { var j = Page().SelfTestJob(); if (!j.Busy || !j.Progress) throw new InvalidOperationException($"busy {j.Busy}, progress {j.Progress}"); }, 300);
            await Step("install kept: another page", () => ContentFrame.Navigate(typeof(HardwarePage)), 1200);
            await Step("install kept: back mid-install", () => OpenGame(jobGame.Id), 2000);
            await Step("install kept: still shown", () =>
            {
                var j = Page().SelfTestJob();
                if (!j.Busy || !j.Progress || j.InstallEnabled || j.Stage != "Self-test install" || !j.Log.Contains("self-test line"))
                    throw new InvalidOperationException($"busy {j.Busy}, progress {j.Progress}, install enabled {j.InstallEnabled}, stage \"{j.Stage}\"");
            }, 300);
            await Step("install kept: second change refused", () => { if (Helpers.GameJobs.TryStart(jobGame.Id, jobGame.Title, "luma", "Luma", quiet: true) is not null) throw new InvalidOperationException("a second job started in the same game"); }, 300);
            await Step("install kept: ended", () => Helpers.GameJobs.End(fake!), 800);
            await Step("install kept: page free again", () => { var j = Page().SelfTestJob(); if (j.Busy || j.Progress) throw new InvalidOperationException($"busy {j.Busy}, progress {j.Progress}"); }, 300);
        }
        await Step("my PC", () => ContentFrame.Navigate(typeof(HardwarePage)), 2000);
        await Step("settings", () => ContentFrame.Navigate(typeof(SettingsPage)), 1500);
        await Step("settings tabs", () => (Shown as SettingsPage)?.SelfTestTabs(), 800);
        // 1.4.0-rc.20: the game list redrawn (a game started or closed, a scan) sent Settings back to its top with the search
        // box focused. Scrolled down, the list redrawn twice: the page must stay where it was.
        double settingsAt = 0;
        await Step("settings scrolled down", () =>
            (Shown as SettingsPage)?.SelfTestScroller.ScrollTo(0, 700, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled)), 500);
        await Step("settings list redrawn", () =>
        {
            settingsAt = (Shown as SettingsPage)?.SelfTestScroller.VerticalOffset ?? 0;
            RebuildNav();
            RebuildNav();
        }, 900);
        await Step("settings kept its place", () =>
        {
            var now = (Shown as SettingsPage)?.SelfTestScroller.VerticalOffset ?? -1;
            if (settingsAt > 100 && Math.Abs(now - settingsAt) > 4) throw new InvalidOperationException($"Settings moved from {settingsAt:0} to {now:0} when the game list was redrawn");
        }, 300);
        // Hybrid Launcher's own play time record: hours added to a game come back for the same game added again under another
        // id or store (by its name or app id), older time kept in a game's settings is taken in, and other games are untouched.
        await Step("play time record", () =>
        {
            var first = new Game { Id = "selftest:ledger-a", Name = "Self-Test Quest™: Redux", Store = GameStore.Custom, InstallDir = @"C:\Games\A" };
            var again = new Game { Id = "selftest:ledger-b", Name = "Self-test Quest: Redux", Store = GameStore.Epic, InstallDir = @"D:\Other\B" };
            var other = new Game { Id = "selftest:ledger-c", Name = "Another Test Game", Store = GameStore.Custom, InstallDir = @"C:\Games\C" };
            long before = PlaytimeLedger.Get(first)?.Minutes ?? 0;
            PlaytimeLedger.Add(first, 42, DateTime.Now);
            if (Playtime.Get(again, new GameSettings()).Minutes != before + 42) throw new InvalidOperationException("the same game added again did not find its hours");
            if (Playtime.Get(other, new GameSettings()).Minutes != 0) throw new InvalidOperationException("another game took the hours");
            Playtime.Get(other, new GameSettings { PlayMinutes = 7 });
            PlaytimeLedger.Reload();
            if (PlaytimeLedger.Get(other)?.Minutes != 7) throw new InvalidOperationException("older time in the settings was not kept");
            if (!File.Exists(PlaytimeLedger.File)) throw new InvalidOperationException("the record was not saved");
        }, 100);
        // The search through every tab: results grouped by tab and section, and one opens its tab at the setting.
        if (only.Length == 0 || only.Any(o => "settings search".StartsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (Shown is SettingsPage sp)
                    await sp.SelfTestSearch(string.IsNullOrWhiteSpace(shots) ? null : () => SaveShotAsync(Path.Combine(shots, $"{++shot:000}-settings search results.png")));
                Log.Info("Self-test: settings search");
            }
            catch (Exception ex) { failures++; Log.Error("Self-test FAILED: settings search", ex); }
        }
        // Settings → Controller, and a button given to another action: the one that had it gets the old button back, then the defaults.
        await Step("settings controller", () => (Shown as SettingsPage)?.SelfTestController(), 900);
        await Step("controller buttons swap", () =>
        {
            PadActions.Set(PadAction.Press, PadKey.B);
            bool swapped = PadActions.ActionOf(PadKey.B) == PadAction.Press && PadActions.ActionOf(PadKey.A) == PadAction.Back;
            PadActions.Reset();
            if (!swapped) throw new InvalidOperationException("giving B to Press did not give A to Back");
            if (PadActions.ActionOf(PadKey.A) != PadAction.Press || PadActions.ActionOf(PadKey.View) != PadAction.Search) throw new InvalidOperationException("Reset did not bring the default buttons back");
        }, 100);
        await Step("controller sections", () => { StepSection(1); StepSection(-1); }, 900);
        await Step("settings again", () => ContentFrame.Navigate(typeof(SettingsPage)), 900);
        // The Home button listening (XInput, and PlayStation controllers over HID) starts and stops cleanly.
        await Step("home button listening", () =>
        {
            var c = AppState.Settings.Controller;
            bool was = c.HomeButton;
            c.HomeButton = true;
            HomeButtonSettingChanged();
            if (_home is null) throw new InvalidOperationException("the Home button is not listened to");
            c.HomeButton = was;
            HomeButtonSettingChanged();
        }, 300);
        // The Home button on an achievement's pop-up opens that game's Achievements tab (the achievement is shown once read).
        // A game whose achievements can be read when there is one (the real achievement is then scrolled to), else any game.
        var achGame = AchievementWatcher.PreviewGames().FirstOrDefault() ?? AppState.Games.FirstOrDefault();
        if (achGame is not null)
        {
            string achKey = "selftest-home";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (await HybridLauncher.Core.Steam.SteamAchievements.LoadForAsync(achGame, cts.Token) is { Items.Count: > 0 } set) achKey = set.Items[set.Items.Count / 2].Key;
            }
            catch (Exception ex) { Log.Warn($"Self-test achievements of {achGame.Name}: {ex.Message}"); }
            await Step("home button on a pop-up", () =>
            {
                AchievementPopup.ShowReplacing(new Toast("Home test", "The Home button opens this one.", null, achGame.Name, 20, GameId: achGame.Id, AchievementKey: achKey));
            }, 1200);
            await Step("home button opens the achievement", () =>
            {
                HomePressed();
                if ((Shown as GamePage)?.GameId != achGame.Id) throw new InvalidOperationException("the game of the pop-up did not open");
                if (AchievementPopup.TakeRecent() is not null) throw new InvalidOperationException("the pop-up's achievement was not taken");
            }, 2500);
            if (achKey != "selftest-home")
                await Step("home button shows that achievement", () =>
                {
                    if ((Shown as GamePage)?.LastShownAchievement != achKey) throw new InvalidOperationException($"{achKey} was not shown in the Achievements tab");
                }, 300);
            await Step("settings again", () => ContentFrame.Navigate(typeof(SettingsPage)), 900);
        }
        await Step("settings notifications", () => OpenSettings("notifications"), 1000);
        // The welcome tip (wide, with the Ko-fi card), then closed without marking the tour seen.
        await Step("welcome tip", StartTour, 1500);
        await Step("welcome tip closed", () => { EndTour(markSeen: false); if (_touring) throw new InvalidOperationException("the tour is still running"); }, 600);
        // Welcome to the new version: the highlights, the changelog link and the support card, then closed.
        await Step("what's new", () => { _ = ShowWhatsNewAsync(); }, 1200);
        await Step("what's new closed", () => { if (!SelfTestWhatsNew(close: true)) throw new InvalidOperationException("the new version's welcome did not open"); }, 600);
        // Vsync (Helpers.FrameSync, always on): the display's refresh ticks the app's own motion at its full rate.
        int vsyncTicks = 0;
        long vsyncFrom = 0;
        IDisposable? vsync = null;
        await Step("vsync clock", () => { vsyncFrom = System.Diagnostics.Stopwatch.GetTimestamp(); vsync = Helpers.FrameSync.Subscribe(DispatcherQueue, _ => vsyncTicks++); }, 1000);
        await Step("vsync clock follows the display", () =>
        {
            vsync?.Dispose();
            double secs = System.Diagnostics.Stopwatch.GetElapsedTime(vsyncFrom).TotalSeconds, hz = 1000 / Helpers.FrameSync.PeriodMs;
            Log.Info($"Vsync: {vsyncTicks / secs:0} ticks a second, display refresh {hz:0} Hz");
            if (vsyncTicks / secs < Math.Min(hz, 60) * 0.6) throw new InvalidOperationException($"only {vsyncTicks / secs:0} ticks a second at {hz:0} Hz");
            if (vsyncTicks / secs > hz * 1.3 + 5) throw new InvalidOperationException($"{vsyncTicks / secs:0} ticks a second, faster than the display's {hz:0} Hz");
        }, 50);
        await Step("pop-up", () => AchievementPopup.ShowTest(), 1500);
        await Step("pop-up see-through", AchievementPopup.CheckSeeThrough, 50);
        if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } popup and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up window.png"), popup);
        // Every look, one after another (each replaces the one on screen), a picture of each once it has settled.
        var savedCorner = AppState.Settings.Popup.Corner;
        AppState.Settings.Popup.Corner = "Native";
        foreach (var look in new[] { "PlayStation", "PlayStation4", "PlayStation4Black", "Xbox", "Steam", "Gog", "Epic", "Minimal" })
        {
            await Step($"pop-up look {look}", () => AchievementPopup.ShowTest(look), 1500);
            await Step($"pop-up look {look} where the original shows it", () => AchievementPopup.CheckNativeSpot(look), 50);
            await Step($"pop-up look {look} see-through", AchievementPopup.CheckSeeThrough, 50);
            if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } lookWindow and not 0)
                await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up {look}.png"), lookWindow);
        }
        // Every look on its own background and text colours (Settings → Achievements → Background, Text): light, dark, a colour,
        // a picked text colour, and back to the look's own.
        var savedColours = AppState.Settings.Popup.LookColours.ToDictionary(p => p.Key, p => new PopupLookColours { Background = p.Value.Background, Text = p.Value.Text }, StringComparer.OrdinalIgnoreCase);
        foreach (var (back, ink) in new[] { ("#F4F3F5", ""), ("#1C1B1D", ""), ("#B3121A", ""), ("#2D6CDF", "#FFD34D"), ("", "") })
            foreach (var look in new[] { "Hybrid", "PlayStation", "PlayStation4", "Xbox", "Steam", "Gog", "Epic", "Minimal" })
            {
                var c = AppState.Settings.Popup.ColoursOf(look);
                c.Background = back; c.Text = ink;
                string name = back.Length == 0 ? "own" : back.TrimStart('#') + (ink.Length > 0 ? " text " + ink.TrimStart('#') : "");
                await Step($"pop-up look {look} colours {name}", () => AchievementPopup.ShowTest(look), 1200);
                await Step($"pop-up look {look} colours {name} see-through", AchievementPopup.CheckSeeThrough, 50);
                if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } backWindow and not 0)
                    await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up {look} {name}.png"), backWindow);
            }
        AppState.Settings.Popup.LookColours = savedColours;
        // The platinum of a completed game, in every look: its own emblem and the platinum trophy.
        foreach (var look in new[] { "Hybrid", "PlayStation", "PlayStation4", "Xbox", "Steam", "Minimal" })
        {
            await Step($"pop-up platinum {look}", () => AchievementPopup.ShowTestPlatinum(look), 1500);
            if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } plat and not 0)
                await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up platinum {look}.png"), plat);
        }
        AppState.Settings.Popup.Corner = savedCorner;
        await Step("pop-up again", () => AchievementPopup.ShowTest(), 7000);
        await Step("pop-up gone by itself", () => { if (AchievementPopup.CurrentHandle is not null) throw new InvalidOperationException("the pop-up is still on screen after its time"); }, 100);
        // The way people try it: Settings → Achievements → "Try it", pressed again while one shows, then every look picked in a row.
        await Step("pop-up settings", () => OpenSettings("achievements"), 1200);
        // Achievements → Sounds, under the pop-ups: what plays, volume, a .wav per rarity.
        await Step("pop-up settings sounds", () => (Shown as SettingsPage)?.SelfTestSounds(), 900);
        await SelfTestSoundsAsync(Step);
        await Step("pop-up settings try it", () => (Shown as SettingsPage ?? throw new InvalidOperationException("Settings is not open")).SelfTestTryPopup(), 400);
        await Step("pop-up settings try it again", () => (Shown as SettingsPage)?.SelfTestTryPopup(), 1500);
        await Step("pop-up settings try it see-through", AchievementPopup.CheckSeeThrough, 50);
        await Step("pop-up settings looks in a row", () => (Shown as SettingsPage)?.SelfTestLooks(), 1500);
        await Step("pop-up settings looks see-through", AchievementPopup.CheckSeeThrough, 50);
        // A real achievement of a game in the library (when one has readable achievements), shown as its unlock would be.
        bool previewed = false;
        if (only.Length == 0 || only.Any(o => "pop-up settings real game".StartsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                previewed = await ((Shown as SettingsPage)?.SelfTestPreviewGameAsync() ?? Task.FromResult(false));
                await Task.Delay(2000);
                Log.Info($"Self-test: pop-up settings real game ({(previewed ? "shown" : "no game with readable achievements")})");
            }
            catch (Exception ex) { failures++; Log.Error("Self-test FAILED: pop-up settings real game", ex); }
        }
        if (previewed) await Step("pop-up settings real game see-through", AchievementPopup.CheckSeeThrough, 50);
        if (previewed && !string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } real and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up real game.png"), real);
        // The game's platinum from the same row (the picked game, or the sample when none can be read).
        await Step("pop-up settings real game platinum", () => _ = (Shown as SettingsPage)?.SelfTestPreviewPlatinum(), 2500);
        if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } platPreview and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up real game platinum.png"), platPreview);
        // A progress pop-up from the same row (a step towards one of the game's locked achievements).
        await Step("pop-up settings real game progress", () => _ = (Shown as SettingsPage)?.SelfTestPreviewProgress(), 2500);
        if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } progPreview and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up real game progress.png"), progPreview);
        await Step("pop-up placing", () => AchievementPopup.Place((_, _) => { }), 1500);
        if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.PlacingHandle is { } placing and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up placing window.png"), placing);
        await Step("pop-up placing closed", AchievementPopup.CloseAll, 300);
        // HL_SELFTEST_ULTRA=1 (fake game folders only): real Ultra+ installs, an older version first so the update shows, then
        // Update all, then each one removed again, so the folders end as they began.
        if (Environment.GetEnvironmentVariable("HL_SELFTEST_ULTRA") == "1" && (only.Length == 0 || only.Any(o => "ultra".StartsWith(o, StringComparison.OrdinalIgnoreCase))))
            failures += await SelfTestUltraAsync(Step);
        // HL_SELFTEST_TRAILER=<video file>: a trailer opened and stopped again and again at random moments, as when games are
        // switched while a trailer loads. Up to rc.2 this froze the window for good (see docs: a MediaPlayer made on the UI
        // thread and disposed off it); a freeze shows as the self-test never finishing.
        if (Environment.GetEnvironmentVariable("HL_SELFTEST_TRAILER") is { Length: > 0 } clip && File.Exists(clip)
            && (only.Length == 0 || only.Any(o => "trailer".StartsWith(o, StringComparison.OrdinalIgnoreCase))))
        {
            for (int w = 0; w < 50 && ContentFrame.XamlRoot is null; w++) await Task.Delay(100);
            var player = new Controls.TrailerPlayer { Width = 480, Height = 270 };
            var layer = new Microsoft.UI.Xaml.Controls.Primitives.Popup { XamlRoot = ContentFrame.XamlRoot ?? throw new InvalidOperationException("no XamlRoot for the trailer test") };
            layer.Child = player;
            layer.IsOpen = true;
            var rng = new Random(7);
            var uri = new Uri(clip);
            // The trailer must really play, as on a page the user is looking at.
            bool wasHidden = MediaPolicy.WindowHidden;
            MediaPolicy.SetWindowHidden(false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int rounds = int.TryParse(Environment.GetEnvironmentVariable("HL_SELFTEST_TRAILER_ROUNDS"), out var r) ? r : 300;
            for (int i = 0; i < rounds; i++)
            {
                var play = player.PlayAsync(uri, hls: false, user: true);
                await Task.Delay(rng.Next(0, 250));
                player.Stop();
                try { await play; } catch (Exception ex) { Log.Warn($"Self-test trailer round {i}: {ex.Message}"); }
            }
            player.Stop();
            MediaPolicy.SetWindowHidden(wasHidden);
            layer.IsOpen = false;
            layer.Child = null;
            await Step($"trailer churn ({rounds} rounds, {watch.Elapsed.TotalSeconds:0.0} s)", () => { }, 300);
        }
        // HL_SELFTEST_PLAY=<exe>: a game that closes a moment after Play and starts itself again (like a launcher exe or Steam's
        // DRM wrapper) is still the game launched here: the window must go to the notification area while it runs and come back
        // once it is gone for good (it used to stay on screen over the game).
        if (Environment.GetEnvironmentVariable("HL_SELFTEST_PLAY") is { Length: > 0 } playExe && File.Exists(playExe)
            && (only.Length == 0 || only.Any(o => "play restart".StartsWith(o, StringComparison.OrdinalIgnoreCase))))
            failures += await SelfTestPlayRestartAsync(playExe);
        // HL_SELFTEST_LOCALACH=1: an unlock written to a local achievement file (a folder of the user's own, Steam app 480) is
        // seen by the watcher, shown as a pop-up and kept in the unlock history.
        // Epic achievements through the store page (2.1-rc.11): a public query, no account, to see Epic answers the route.
        if (Environment.GetEnvironmentVariable("HL_SELFTEST_EPICWEB") is { Length: > 0 } sandbox)
            await Step("epic store page answers", async () =>
            {
                try
                {
                    Log.Info($"Self-test Epic direct: {await HybridLauncher.Core.Accounts.EpicStats.ProbeDirectAsync(sandbox)}");
                    Log.Info($"Self-test Epic store page: {await Helpers.EpicWeb.ProbeAsync(sandbox)}");
                }
                catch (Exception ex) { Log.Error("Self-test FAILED: Epic store page", ex); }
            }, 30000);
        if (Environment.GetEnvironmentVariable("HL_SELFTEST_LOCALACH") == "1" && (only.Length == 0 || only.Any(o => "local unlock".StartsWith(o, StringComparison.OrdinalIgnoreCase))))
            failures += await SelfTestLocalUnlockAsync(shots);
        for (int f = 0; f <= 7; f++)
        {
            int step = f;
            await Step($"feature picker {step}", () => SelfTestFeaturePicker(step), step == 0 ? 1200 : 700);
        }
        // Every step of the tour, then one more that ends it: a tip left open covered the pictures of the steps after it.
        for (int t = 0; t == 0 || t <= _steps.Length; t++)
        {
            int step = t;
            await Step($"tour {step}", () => SelfTestTourStep(step), 1600);
        }
        foreach (var palette in Palettes.All)
            await Step($"palette {palette.Key}", () => { AppState.Settings.Palette = palette.Key; ApplyTheme(AppState.Settings.Theme); }, 300);
        // Material colours on and off, on a game page (its tabs paint themselves), Downloads, the library and Settings.
        var savedLook = (AppState.Settings.Palette, AppState.Settings.Theme, AppState.Settings.Material);
        void Look(string palette, string theme, bool material)
        {
            AppState.Settings.Palette = palette; AppState.Settings.Theme = theme; AppState.Settings.Material = material;
            ApplyTheme(theme);
        }
        if (AppState.VisibleGames.FirstOrDefault() is { } first)
        {
            await Step("game page dark, material on", () => { Look("Hybrid", "Dark", true); OpenGame(first.Id); }, 2500);
            await Step("game page dark, material off", () => Look("Hybrid", "Dark", false), 800);
        }
        await Step("downloads material on", () => { Look("Hybrid", "Dark", true); ContentFrame.Navigate(typeof(DownloadsPage)); }, 3000);
        await Step("downloads material off", () => Look("Hybrid", "Dark", false), 800);
        await Step("library material off", () => ContentFrame.Navigate(typeof(LibraryPage), ""), 1500);
        await Step("library material on", () => Look("Hybrid", "Dark", true), 800);
        await Step("settings material on", () => ContentFrame.Navigate(typeof(SettingsPage)), 1500);
        await Step("settings material off", () => Look("Hybrid", "Dark", false), 800);
        Look(savedLook.Palette, savedLook.Theme, savedLook.Material);
        Helpers.UiPerf.Summary();
        Log.Info($"Self-test finished: {failures} failure(s)");
        Close();
    }

    /// <summary>
    /// Self-test: the window as it is on screen, as a PNG: Windows draws it into a bitmap (PrintWindow with full content), so
    /// blur, acrylic, Mica, shadows and video show as the user sees them. Falls back to XAML's own picture if that fails.
    /// </summary>
    async Task SaveShotAsync(string file, nint other = 0)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var hwnd = other != 0 ? other : WinRT.Interop.WindowNative.GetWindowHandle(this);
            byte[]? pixels = null;
            int w = 0, h = 0;
            if (Shot.GetClientRect(hwnd, out var rect) && (w = rect.Right - rect.Left) > 0 && (h = rect.Bottom - rect.Top) > 0)
            {
                var screen = Shot.GetDC(0);
                var dc = Shot.CreateCompatibleDC(screen);
                var bmp = Shot.CreateCompatibleBitmap(screen, w, h);
                var old = Shot.SelectObject(dc, bmp);
                // PW_CLIENTONLY | PW_RENDERFULLCONTENT: the DirectComposition content (acrylic, Mica, video) included.
                if (Shot.PrintWindow(hwnd, dc, 1 | 2))
                {
                    var info = new Shot.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                    pixels = new byte[w * h * 4];
                    if (Shot.GetDIBits(dc, bmp, 0, (uint)h, pixels, ref info, 0) == 0) pixels = null;
                    else for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    // With the display asleep (or the PC left alone) Windows hands back a black picture: XAML's own is used then.
                    if (pixels is not null && other == 0 && AllBlack(pixels)) pixels = null;
                }
                Shot.SelectObject(dc, old);
                Shot.DeleteObject(bmp);
                Shot.DeleteDC(dc);
                Shot.ReleaseDC(0, screen);
            }
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            double dpi = 96 * (RootGrid.XamlRoot?.RasterizationScale ?? 1);
            if (pixels is not null)
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, (uint)w, (uint)h, dpi, dpi, pixels);
            else
            {
                var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await rtb.RenderAsync(RootGrid);
                var xaml = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await rtb.GetPixelsAsync());
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, dpi, dpi, xaml);
            }
            await encoder.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream).ReadExactlyAsync(bytes);
            await File.WriteAllBytesAsync(file, bytes);
        }
        catch (Exception ex) { Log.Warn($"Self-test picture: {ex.Message}"); }
    }

    static bool AllBlack(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4 * 97)
            if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0) return false;
        return true;
    }

    /// <summary>The GDI calls of the self-test's window pictures.</summary>
    static class Shot
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out RECT rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool DeleteDC(nint dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    }

    // ---------- theme, toasts ----------

    public void ApplyTheme(string theme)
    {
        // Game views kept for a quick return were drawn in the old look.
        GamePage.ForgetViews();
        // Hybrid Launcher is dark only (0.20): one look to get right, as Steam's.
        var palette = Palettes.Get(AppState.Settings.Palette);
        var requested = ElementTheme.Dark;
        Palettes.ApplyResources(palette);
        Views.LibraryPage.ForgetLook();
        // Brushes read in code follow the theme the window ends up in (set first: the flip below restyles everything twice).
        ThemeRes.Theme = requested;
        // Theme resources are only looked up again when the theme changes: flip it once so the new accent shows everywhere.
        // The flip's own ActualThemeChanged events stay quiet (they may come a moment later): the paint below is the one.
        _applyingTheme = true;
        RootGrid.RequestedTheme = RootGrid.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        RootGrid.RequestedTheme = requested;
        UpdateTint(newLook: true);
        UpdateCaptionButtons();
        ThemeRes.RaiseApplied();
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _applyingTheme = false)) _applyingTheme = false;
    }

    bool _applyingTheme;

    // Kept alive: UISettings only raises ColorValuesChanged while the object lives.
    Windows.UI.ViewManagement.UISettings? _uiSettings;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _coloursTimer;

    /// <summary>
    /// The Windows theme follows Windows: when the accent changes there (Settings, Personalisation, Colours, or a new wallpaper
    /// with automatic accent), the theme is read again and applied. Windows raises the event several times per change, on a
    /// worker thread: it is gathered into one apply on the UI thread 400 ms after the last one.
    /// </summary>
    void WatchWindowsColours()
    {
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                if (_coloursTimer is null)
                {
                    _coloursTimer = DispatcherQueue.CreateTimer();
                    _coloursTimer.Interval = TimeSpan.FromMilliseconds(400);
                    _coloursTimer.IsRepeating = false;
                    _coloursTimer.Tick += (_, _) =>
                    {
                        if (_closed) return;
                        var before = Palettes.Get("Windows").Accent;
                        Palettes.ForgetWindowsColours();
                        if (Palettes.Get(AppState.Settings.Palette).Key != "Windows" || Palettes.Get("Windows").Accent == before) return;
                        Log.Info("Windows accent changed: Windows theme applied again");
                        ApplyTheme(AppState.Settings.Theme);
                    };
                }
                _coloursTimer.Stop();
                _coloursTimer.Start();
            });
        }
        catch (Exception ex) { Log.Warn($"Windows colours: {ex.Message}"); }
    }

    /// <summary>
    /// The window's background: the plain gradient at once, then (dark theme, rc.5) the painted one with glows, vignette and
    /// dither (Helpers/BackdropPainter) when it is ready. Painted again when the window's size settles or the palette changes.
    /// </summary>
    void UpdateTint(bool newLook = false)
    {
        var palette = Palettes.Get(AppState.Settings.Palette);
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        // A new theme shows its plain gradient at once (2.1): the painted one of the old theme stayed until the new one was ready.
        if (newLook || TintLayer.Background is not Microsoft.UI.Xaml.Media.ImageBrush || !dark) TintLayer.Background = Palettes.TintBrush(palette, dark);
        if (dark) _ = PaintBackdropAsync(palette);
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _backdropTimer;
    Windows.Foundation.Size _backdropSize;
    int _backdropGen;

    async Task PaintBackdropAsync(Palette palette)
    {
        double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;
        if (w < 16 || h < 16) return;
        _backdropSize = new Windows.Foundation.Size(w, h);
        // Only the newest paint lands (2.1-rc.10 audit: two quick theme changes could finish out of order), never after close.
        int gen = ++_backdropGen;
        try
        {
            var brush = await BackdropPainter.BrushAsync(palette, w, h, RootGrid.XamlRoot?.RasterizationScale ?? 1);
            if (_closed || gen != _backdropGen) return;
            if (brush is not null && RootGrid.ActualTheme == ElementTheme.Dark)
                TintLayer.Background = brush;
        }
        catch (Exception ex) { Log.Warn($"Window background: {ex.Message}"); }
    }

    /// <summary>A resize repaints the background once it has settled (a drag would otherwise paint dozens of times).</summary>
    void QueueBackdropRepaint()
    {
        if (RootGrid.ActualTheme != ElementTheme.Dark) return;
        if (Math.Abs(RootGrid.ActualWidth - _backdropSize.Width) < 24 && Math.Abs(RootGrid.ActualHeight - _backdropSize.Height) < 24) return;
        if (_backdropTimer is null)
        {
            _backdropTimer = DispatcherQueue.CreateTimer();
            _backdropTimer.Interval = TimeSpan.FromMilliseconds(220);
            _backdropTimer.IsRepeating = false;
            _backdropTimer.Tick += (_, _) => _ = PaintBackdropAsync(Palettes.Get(AppState.Settings.Palette));
        }
        _backdropTimer.Stop();
        _backdropTimer.Start();
    }

    void UpdateTitleBrand() => UpdatePageTitle();

    // ---------- page title in the title bar ----------

    /// <summary>On a game page the title bar shows "← GAME NAME", starting where the page content starts.</summary>
    void UpdatePageTitle()
    {
        var game = Shown is GamePage { GameId: { } id } ? AppState.Find(id) : null;
        PageTitle.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        if (game is null)
        {
            UpdateTitleBarRegions();
            return;
        }
        PageTitleText.Text = game.Title.ToUpperInvariant();
        BackButton.Visibility = CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        // Pane open beside the content: line up with the banner (pane width + page padding). Otherwise after the brand.
        bool besidePane = Nav.IsPaneOpen && Nav.DisplayMode == NavigationViewDisplayMode.Expanded;
        double brand = TitleBrand.ActualWidth + TitleLogo.ActualWidth + TitleLogo.Margin.Left;
        PageTitle.Margin = new Thickness(besidePane ? Math.Max(24, Nav.OpenPaneLength + 20 - 18 - 8 - brand) : 24, 0, 0, 0);
        DispatcherQueue.TryEnqueue(UpdateTitleBarRegions);
    }

    /// <summary>The blue Update button: shown only while a newer version is out, with its number in the tooltip.</summary>
    void UpdateUpdateButton()
    {
        var release = Helpers.AppUpdater.Available;
        UpdateButton.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtonText.Text = L.T("Update");
        if (release is not null)
        {
            ToolTipService.SetToolTip(UpdateButton, L.F("{0} {1} is available. Click to update.", AppInfo.Name, release.Version.ToString(3)));
            // Kept once per version in the notification list, beside the blue button; and, like the other sources, one Windows
            // pop-up per version unless turned off in Settings -> Notifications (or held back while a game runs).
            var version = release.Version.ToString(3);
            var title = L.F("{0} {1} is out", AppInfo.Name, version);
            Inbox.Add(Inbox.App, title, L.T("Click to see what is new and update."), null, null, key: "app:" + version);
            if (AppState.Settings.AppUpdatePopupShown != version && Popup(Inbox.App))
            {
                AppState.Settings.AppUpdatePopupShown = version;
                SettingsStore.Save();
                EnsureTray();
                _tray?.Notify(title, L.T("Click to see what is new and update."), () =>
                {
                    ShowFromTray();
                    if (Helpers.AppUpdater.Available is { } r && Content?.XamlRoot is { } root) _ = Helpers.AppUpdater.OfferAsync(root, r);
                });
            }
        }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Helpers.AppUpdater.Available is { } release && Content?.XamlRoot is { } root)
            await Helpers.AppUpdater.OfferAsync(root, release);
    }

    /// <summary>The title bar drags the window: the back button needs a pass-through area to be clickable.</summary>
    void UpdateTitleBarRegions()
    {
        try
        {
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            // The logo and the back button take clicks; the rest of the title bar drags the window.
            var rects = new List<RectInt32>();
            void Add(FrameworkElement e)
            {
                if (e.XamlRoot is null || e.ActualWidth <= 0 || e.Visibility != Visibility.Visible) return;
                double scale = e.XamlRoot.RasterizationScale;
                var b = e.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
                rects.Add(new RectInt32((int)Math.Round(b.X * scale), (int)Math.Round(b.Y * scale), (int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale)));
            }
            Add(TitleBrand);
            Add(CaptionButtons);
            if (PageTitle.Visibility == Visibility.Visible) Add(BackButton);
            // The bell's list hangs over the title bar: its header buttons (Mark all read, Clear, settings) must take clicks.
            if (BellFlyout.IsOpen && BellFlyout.Content is FrameworkElement bell) Add(bell);
            if (rects.Count == 0) source.ClearRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough);
            else source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch (Exception ex) { Log.Warn($"Title bar regions: {ex.Message}"); }
    }

    /// <summary>Back goes to the library in the grid view, else to the page before (the empty start page does not count).</summary>
    bool CanGoBack => ContentFrame.BackStack.Any(e => e.SourcePageType != typeof(HomePage));

    void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    /// <summary>Selects the pane item of the page shown, without navigating again.</summary>
    /// <summary>Puts an invisible helper (the image decoder's engine) in the window, or takes it out again.</summary>
    public void HostHidden(UIElement element, bool remove = false)
    {
        if (remove) RootGrid.Children.Remove(element);
        else if (!RootGrid.Children.Contains(element))
        {
            Grid.SetRowSpan((FrameworkElement)element, 2);
            if (element is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Left;
                fe.VerticalAlignment = VerticalAlignment.Top;
            }
            RootGrid.Children.Insert(0, element);
        }
    }

    /// <summary>
    /// A game was uninstalled: its open page becomes its Download page when you own it on a linked launcher, else the library opens.
    /// </summary>
    public void GameRemoved(string id)
    {
        try
        {
            if (Shown is not GamePage { GameId: { } open } || !open.Equals(id, StringComparison.OrdinalIgnoreCase) || AppState.Find(id) is not null) return;
            if (OwnedLibrary.Find(id) is { } owned)
            {
                AppState.AddOwned(OwnedLibrary.AsGame(owned));
                NavigateGame(id);
                if (ContentFrame.BackStack.Count > 0) ContentFrame.BackStack.RemoveAt(ContentFrame.BackStack.Count - 1);
            }
            else ContentFrame.Navigate(typeof(LibraryPage));
            SyncSelection();
        }
        catch (Exception ex) { Log.Warn($"Page after uninstall: {ex.Message}"); }
    }

    /// <summary>Opens the Downloads tab (from a game page's Download button while it downloads).</summary>
    public void OpenDownloads()
    {
        try
        {
            if (Shown is not DownloadsPage) ContentFrame.Navigate(typeof(DownloadsPage));
            SyncSelection();
        }
        catch (Exception ex) { ShowError(L.T("Action failed"), ex.Message, ex); }
    }

    /// <summary>Opens Settings on one of its tabs ("appearance", "features"...): from a game page's menus.</summary>
    public void OpenSettings(string tab)
    {
        try
        {
            AppState.Settings.SettingsTab = tab;
            if (Shown is SettingsPage open) open.SelectTab(tab);
            else ContentFrame.Navigate(typeof(SettingsPage));
            SyncSelection();
        }
        catch (Exception ex) { ShowError(L.T("Action failed"), ex.Message, ex); }
    }

    void SyncSelection()
    {
        using var __perf = Helpers.UiPerf.Scope("MainWindow.SyncSelection");
        _rebuilding = true;
        try
        {
            object? tag = Shown switch
            {
                LibraryPage => LibraryTag,
                DownloadsPage => DownloadsTag,
                HardwarePage => HardwareTag,
                GamePage { GameId: { } id } => id,
                _ => null,
            };
            if (Shown is SettingsPage) Nav.SelectedItem = Nav.SettingsItem;
            else Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is string t && tag is string s && t.Equals(s, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _rebuilding = false;
        }
    }

    void UpdateCaptionButtons()
    {
        // A theme change can still arrive while the window closes, when it has no title bar any more.
        if (AppWindow?.TitleBar is not { } tb) return;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x20, 0, 0, 0);
        tb.ButtonHoverForegroundColor = tb.ButtonForegroundColor;
        tb.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
    }

    /// <summary>
    /// An error in red, with the likely cause on a line of its own (read from <paramref name="error"/> when there is one, else from
    /// the text) and a Troubleshooting link to the guide's section for it.
    /// </summary>
    public void ShowError(string title, string message, Exception? error = null)
    {
        ErrorShown?.Invoke(title, message);
        var cause = error is null ? Troubleshoot.Cause(message) : Troubleshoot.Cause(error);
        var text = cause is null || message.Contains(cause, StringComparison.Ordinal) ? message : message + "\n" + cause;
        Toast(InfoBarSeverity.Error, title, text, TimeSpan.FromSeconds(cause is null ? 12 : 16), Troubleshoot.Link(title, message));
    }

    /// <summary>Every error shown (title, message): the Auto tab keeps the ones of a step as that step's reason (2.0.0-rc.10).</summary>
    public event Action<string, string>? ErrorShown;

    public void ShowInfo(string message) => Toast(InfoBarSeverity.Informational, "", message, TimeSpan.FromSeconds(5));
    public void ShowSuccess(string message) => Toast(InfoBarSeverity.Success, "", message, TimeSpan.FromSeconds(5));

    /// <summary>GitHub's limit ran out and no GitHub account is signed in: once per run, a note with a Sign in button.</summary>
    void OfferGitHubSignIn(DateTime until)
    {
        if (GitHubAuth.SignedIn || !GitHubAuth.CanSignIn || _offeredGitHub) return;
        _offeredGitHub = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            var button = new Button { Content = L.T("Sign in to GitHub"), Style = Ui.Style("AccentButtonStyle") };
            button.Click += async (_, _) =>
            {
                ToastHost.Visibility = Visibility.Collapsed;
                await GitHubSignInView.SignInAsync(RootGrid.XamlRoot);
            };
            Toast(InfoBarSeverity.Warning, L.T("GitHub limit reached"),
                L.F("GitHub allows 60 requests an hour without sign-in, so version lists can't load until {0:HH:mm}. Sign in to GitHub to lift the limit.", until.ToLocalTime()),
                TimeSpan.FromSeconds(30), action: button);
        });
    }

    bool _offeredGitHub;

    void Toast(InfoBarSeverity severity, string title, string message, TimeSpan duration, string? help = null, ButtonBase? action = null)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ToastBar.Severity = severity;
            ToastBar.Title = title;
            ToastBar.Message = message;
            ToastBar.ActionButton = action ?? (help is null ? null : new HyperlinkButton { Content = L.T("Troubleshooting"), NavigateUri = new Uri(help) });
            ToastBar.IsOpen = true;
            ToastHost.Visibility = Visibility.Visible;
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = duration };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer?.Stop();
                ToastHost.Visibility = Visibility.Collapsed;
            };
            _toastTimer.Start();
        });
    }

    void ToastBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ToastHost.Visibility = Visibility.Collapsed;
        sender.IsOpen = true; // keep it "open" inside the host for the next toast
    }

    double GetDpiScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return Native.GetDpiForWindow(hwnd) / 96.0;
    }

    /// <summary>
    /// How far the page reaches past the window's right edge (0 when it fits), and the game list's state for the log: the
    /// NavigationView's mode, the list's width, the inner SplitView's own mode, and the page's place and width.
    /// </summary>
    internal (double Overflow, string State) PageOverflow()
    {
        RootGrid.UpdateLayout();
        double left = ContentFrame.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        double right = left + ContentFrame.ActualWidth;
        var split = FindDescendant<SplitView>(Nav);
        string splitState = split is null ? "no SplitView" : $"split {split.DisplayMode} open={split.IsPaneOpen} pane={split.OpenPaneLength:0}/{split.CompactPaneLength:0}";
        string state = $"nav {Nav.DisplayMode} open={Nav.IsPaneOpen} pane={Nav.OpenPaneLength:0}, {splitState}, page x={left:0} w={ContentFrame.ActualWidth:0}, window {RootGrid.ActualWidth:0}";
        return (Math.Max(0, right - RootGrid.ActualWidth), state);
    }

    /// <summary>Self-test: closes every dialog open over the window.</summary>
    void CloseDialogs()
    {
        if (Content?.XamlRoot is not { } root) return;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            if (popup.Child is ContentDialog d) d.Hide();
    }

    /// <summary>
    /// Self-test: the visible elements under <paramref name="root"/> that end past the window's right edge (or past the right
    /// edge of a parent that clips them), described by type, name or text and how far they reach. Elements inside something that
    /// scrolls sideways are left out, and so are the ones off screen vertically.
    /// </summary>
    internal List<string> CutOnTheRight(FrameworkElement root)
    {
        RootGrid.UpdateLayout();
        var found = new List<string>();
        double windowRight = RootGrid.ActualWidth, windowBottom = RootGrid.ActualHeight;
        void Walk(DependencyObject o, double limit)
        {
            if (o is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: 0 }) return;
            if (o is ScrollViewer { HorizontalScrollMode: ScrollMode.Enabled, HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled }) return;
            if (o is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                var p = fe.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
                double right = p.X + fe.ActualWidth;
                bool onScreen = p.Y < windowBottom && p.Y + fe.ActualHeight > 0;
                if (onScreen && right > limit + 1.5 && fe is not Microsoft.UI.Xaml.Controls.Primitives.Popup)
                {
                    string what = fe switch
                    {
                        TextBlock t => $"text \"{(t.Text.Length > 30 ? t.Text[..30] + "…" : t.Text)}\"",
                        ContentControl { Content: string s } => $"{fe.GetType().Name} \"{s}\"",
                        _ => fe.GetType().Name + (string.IsNullOrEmpty(fe.Name) ? "" : " " + fe.Name),
                    };
                    found.Add($"{what} +{right - limit:0}px");
                    // The chain up to the page, each level's place, width and wanted width: where it got wider than its parent.
                    var chain = new List<string>();
                    for (DependencyObject? up = fe; up is FrameworkElement f2 && up != ContentFrame; up = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(up))
                    {
                        double x = f2.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                        chain.Add($"{f2.GetType().Name}{(string.IsNullOrEmpty(f2.Name) ? "" : ":" + f2.Name)} x={x:0} w={f2.ActualWidth:0} want={f2.DesiredSize.Width:0} max={f2.MaxWidth:0}");
                    }
                    Log.Info("Cut on the right: " + string.Join(" < ", chain));
                    // What inside it wants the most room.
                    void Widest(DependencyObject d, int depth)
                    {
                        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d) && depth < 6; i++)
                            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i) is FrameworkElement c && c.DesiredSize.Width > fe.ActualWidth - 60 && c.Visibility == Visibility.Visible)
                            {
                                Log.Info($"  {new string(' ', depth * 2)}{c.GetType().Name}{(string.IsNullOrEmpty(c.Name) ? "" : ":" + c.Name)} w={c.ActualWidth:0} want={c.DesiredSize.Width:0}{(c is TextBlock tb ? " \"" + tb.Text[..Math.Min(40, tb.Text.Length)] + "\"" : "")}");
                                Widest(c, depth + 1);
                            }
                    }
                    Widest(fe, 0);
                    return; // its children are cut with it
                }
                // A parent narrower than the window clips what is inside it at its own edge (when it clips: a Border with
                // rounded corners, a ScrollViewer, a Grid row).
                if (fe is ScrollViewer or ScrollContentPresenter) limit = Math.Min(limit, right);
            }
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(o); i++)
                Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(o, i), limit);
        }
        Walk(root, windowRight);
        return found;
    }

    static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => QueueMaximizeCheck(600));

    bool _maximizeCheckQueued;
    DateTime _lastRemaximize;

    /// <summary>
    /// After a size, place or display change: a window that says it is maximized but does not cover its screen's work area (it
    /// kept its size from another resolution, with the content laid out for it) is maximized again, which also lays the page out
    /// anew. Once per second at most, so it can never loop.
    /// </summary>
    void QueueMaximizeCheck(int delayMs = 250)
    {
        if (_maximizeCheckQueued || _closed) return;
        _maximizeCheckQueued = true;
        Later.Run(DispatcherQueue, TimeSpan.FromMilliseconds(delayMs), () =>
        {
            _maximizeCheckQueued = false;
            try
            {
                if (_closed || _hidden || AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Maximized } p) return;
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                var pos = AppWindow.Position;
                var size = AppWindow.Size;
                // A maximized window reaches a few pixels past the work area on each side (its hidden frame); more than that is wrong.
                int slack = (int)Math.Ceiling(16 * GetDpiScale());
                bool fits = Math.Abs(size.Width - area.Width) <= slack && Math.Abs(size.Height - area.Height) <= slack
                    && Math.Abs(pos.X - area.X) <= slack && Math.Abs(pos.Y - area.Y) <= slack;
                if (fits || DateTime.Now - _lastRemaximize < TimeSpan.FromSeconds(1)) return;
                _lastRemaximize = DateTime.Now;
                Log.Info($"Window maximized at {size.Width}x{size.Height} on a {area.Width}x{area.Height} work area: maximized again");
                p.Restore();
                CenterOnScreen();
                p.Maximize();
            }
            catch (Exception ex) { Log.Warn($"Maximize check: {ex.Message}"); }
        });
    }

    void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        int w = Math.Min(size.Width, area.Width), h = Math.Min(size.Height, area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    /// <summary>
    /// Self-test of the pop-up sounds, everywhere they come from: Windows' sound, None, and with HL_SELFTEST_SOUNDS (a folder with
    /// ps5-bronze/silver/gold/platinum.wav and ps4-trophy.wav) your own files per tier, the one for every achievement when a tier
    /// has none or its file is gone, volume 0, and the sound real pop-ups play for each tier and for the platinum. Played quietly;
    /// the settings are put back after.
    /// </summary>
    async Task SelfTestSoundsAsync(Func<string, Action, int, Task> step)
    {
        var o = AppState.Settings.Popup;
        var saved = (o.Sound, o.SoundFile, Tiers: new Dictionary<string, string>(o.TierSounds, StringComparer.OrdinalIgnoreCase), o.Volume);
        var savedPresets = o.SoundPresets;
        void Expect(string? want, string what)
        {
            var got = AchievementPopup.LastSound;
            if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{what}: played {got ?? "nothing"}, expected {want ?? "nothing"}");
        }
        try
        {
            o.Volume = 0.2;
            await step("sounds: Windows", () =>
            {
                o.Sound = "Windows";
                AchievementPopup.PlaySample("Gold");
                if (AchievementPopup.LastSound is null) throw new InvalidOperationException("the Windows sound did not play");
            }, 1200);
            await step("sounds: none", () => { o.Sound = "None"; AchievementPopup.PlaySample("Gold"); Expect(null, "None"); }, 200);
            var dir = Environment.GetEnvironmentVariable("HL_SELFTEST_SOUNDS");
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) { Log.Info("Self-test: no HL_SELFTEST_SOUNDS folder, own sounds not tried"); return; }
            string F(string name) => Path.Combine(dir, name);
            var tiers = new[] { "Bronze", "Silver", "Gold", "Platinum" };
            o.Sound = "File";
            o.SoundFile = F("ps4-trophy.wav");
            o.TierSounds.Clear();
            foreach (var t in tiers) o.TierSounds[t] = F($"ps5-{t.ToLowerInvariant()}.wav");
            foreach (var t in tiers)
                await step($"sounds: {t}", () =>
                {
                    var want = F($"ps5-{t.ToLowerInvariant()}.wav");
                    if (!File.Exists(want)) throw new InvalidOperationException($"{want} is missing");
                    if (!AchievementPopup.ScalesVolume(want)) throw new InvalidOperationException($"{Path.GetFileName(want)} is not 16-bit PCM: the volume cannot scale it");
                    AchievementPopup.PlaySample(t);
                    Expect(want, t);
                }, 1800);
            // Settings → Sounds → Every achievement → play: its own file even when gold has one (it played gold's up to 1.3.1).
            await step("sounds: every achievement preview", () =>
            {
                AchievementPopup.PlaySample(AchievementPopup.EveryTier);
                Expect(F("ps4-trophy.wav"), "the Every achievement preview");
            }, 1300);
            await step("sounds: every achievement", () =>
            {
                o.TierSounds.Remove("Silver");
                AchievementPopup.PlaySample("Silver");
                Expect(F("ps4-trophy.wav"), "Silver without its own");
            }, 1300);
            await step("sounds: file gone", () =>
            {
                o.TierSounds["Gold"] = F("not-there.wav");
                AchievementPopup.PlaySample("Gold");
                Expect(F("ps4-trophy.wav"), "Gold with its file gone");
                o.TierSounds["Gold"] = F("ps5-gold.wav");
                o.TierSounds["Silver"] = F("ps5-silver.wav");
            }, 1300);
            // A folder as a preset: the files go to their tiers by name, the other one to every achievement.
            await step("sounds: folder preset", () =>
            {
                var p = Views.AchievementSoundsView.FromFolder(dir);
                foreach (var t in tiers)
                    if (!string.Equals(p.GetValueOrDefault(t), F($"ps5-{t.ToLowerInvariant()}.wav"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"the folder preset put {p.GetValueOrDefault(t) ?? "nothing"} on {t}");
                if (!string.Equals(p.GetValueOrDefault("All"), F("ps4-trophy.wav"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("the folder preset did not put the other file on every achievement");
                o.TierSounds.Clear();
                o.SoundFile = null;
                Views.AchievementSoundsView.Apply(o, p);
                AchievementPopup.PlaySample("Platinum");
                Expect(F("ps5-platinum.wav"), "Platinum from the folder preset");
            }, 1800);
            await step("sounds: volume 0", () => { o.Volume = 0; AchievementPopup.PlaySample("Gold"); Expect(null, "volume 0"); o.Volume = 0.2; }, 200);
            // The sound real pop-ups play: the tier from the rarity, and the platinum of a completed game.
            foreach (var (percent, tier) in new (double?, string)[] { (55, "bronze"), (null, "bronze"), (20, "silver"), (6, "gold"), (0.5, "gold") })
            {
                string what = percent is { } p ? p + "%" : "no rarity";
                await step($"sounds: pop-up at {what}", () =>
                {
                    AchievementPopup.LastSound = "";
                    AchievementPopup.ShowReplacing(new Toast("Sound test", "A pop-up's own sound.", null, "Game: The game", percent));
                }, 1900);
                await step($"sounds: pop-up at {what} played {tier}", () => Expect(F($"ps5-{tier}.wav"), $"a pop-up at {what}"), 50);
            }
            await step("sounds: pop-up platinum", () => { AchievementPopup.LastSound = ""; AchievementPopup.ShowTestPlatinum(); }, 2200);
            await step("sounds: pop-up platinum played platinum", () => Expect(F("ps5-platinum.wav"), "the platinum pop-up"), 50);
        }
        finally
        {
            (o.Sound, o.SoundFile, o.Volume) = (saved.Sound, saved.SoundFile, saved.Volume);
            o.TierSounds = saved.Tiers;
            o.SoundPresets = savedPresets;
        }
    }
}
