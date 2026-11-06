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
        Loc.Apply(RootGrid);
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
        GuardSearchFocus();
        Helpers.GameUpdater.Start(DispatcherQueue);
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
        if (st.WindowMaximized && AppWindow.Presenter is OverlappedPresenter op) op.Maximize();

        ApplyTheme(AppState.Settings.Theme);
        RootGrid.ActualThemeChanged += (_, _) => { UpdateCaptionButtons(); UpdateTint(); };
        Http.GitHubLimitReached += OfferGitHubSignIn;

        // The built-in item follows the Windows language; keep the UI in one language.
        Nav.Loaded += (_, _) => { if (Nav.SettingsItem is NavigationViewItem s) s.Content = L.T("Settings"); };

        // In the narrow (minimal) mode the menu button floats over the content: leave it room.
        Nav.DisplayModeChanged += (_, e) =>
        {
            ContentFrame.Margin = e.DisplayMode == NavigationViewDisplayMode.Minimal ? new Thickness(0, 40, 0, 0) : new Thickness(0);
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
                if (ContentFrame.Content is not LibraryPage) ContentFrame.Navigate(typeof(LibraryPage), _filter);
                SyncSelection();
            }
            catch (Exception ex) { ShowError(L.T("Could not open the library"), ex.Message, ex); }
        };
        Nav.Loaded += (_, _) => UpdateTitleBrand();
        // The page reads its game in OnNavigatedTo, after this event: look once it has.
        ContentFrame.Navigated += (_, _) => DispatcherQueue.TryEnqueue(UpdatePageTitle);
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        PageTitle.SizeChanged += (_, _) => UpdateTitleBarRegions();
        // Mouse back / forward buttons (and Alt+Left / Alt+Right) walk the page history, like a browser.
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnMouseNavigate), true);
        AddHistoryKeys();

        AppState.GamesChanged += RebuildNav;
        AppState.PinsChanged += RebuildNav;
        // The list sorts and filters like the library: an order or filter that needs play time, achievements or tags
        // is applied again once those are read (in the background, after the games are found).
        AppState.GamesChanged += () => _ = Library.LoadStatsAsync(DispatcherQueue);
        Library.StatsReady += () =>
        {
            var f = AppState.Settings.Library;
            if (f.Sort is not "name" and not "added" || f.Tech.Count > 0) RebuildNav();
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
            SettingsStore.Save();
            _tray?.Dispose();
            _tray = null;
            App.ExitForSure();
        };
        GameSessions.HdrProblem += message => ShowError(L.T("Smart HDR"), message);
        GameSessions.Changed += _ => UpdateTrayTip();
        GameSessions.Changed += MarkRunning;
        GameSessions.Changed += HideWhilePlaying;
        if (AppState.Settings.CloseToTray) EnsureTray();
        ShowLoading();
        _ = StartAsync();
        AchievementWatcher.Start(DispatcherQueue);
    }

    // ---------- start, notification area ----------

    Tray? _tray;
    bool _quitting;
    bool _hidden;
    // The window went to the notification area by itself for a game (Settings → General): it comes back when the last game closes,
    // unless the user opened it meanwhile.
    bool _hiddenForGame;

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
                if (!AppState.Settings.HideWhilePlaying || _hidden || _hiddenForGame) return;
                Log.Info($"Window to the notification area while {id} runs");
                HideToTray();
                _hiddenForGame = true;
            }
            else if (_hiddenForGame && GameSessions.Active().Count == 0)
            {
                Log.Info("Every game closed: window back from the notification area");
                ShowFromTray();
            }
        }
        catch (Exception ex) { Log.Warn($"Hide while playing: {ex.Message}"); }
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
        // Every game is watched, however it is started (Steam, a shortcut, its own exe, before Hybrid Launcher opened): the list is a
        // snapshot made here on the UI thread after each scan, so the watcher's thread never touches the settings.
        List<(Game, GameSettings)> watched = [];
        void Snapshot() => watched = AppState.Games.Select(g => (g, AppState.Settings.For(g.Id))).ToList();
        Snapshot();
        AppState.GamesChanged += Snapshot;
        GameSessions.WatchAll(() => watched);
        // Cloud saves: a game that closes sends its changed saves (Play brings newer ones down first, in AppState.LaunchAsync).
        CloudSync.Start();
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
        (ContentFrame.Content as GamePage)?.StopMedia();
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
        (ContentFrame.Content as GamePage)?.StopMedia();
        AppWindow.Hide();
        _hidden = true;
        MediaPolicy.SetWindowHidden(true);
    }

    /// <summary>Brings the window back (tray icon, a second start of Hybrid Launcher).</summary>
    public void ShowFromTray()
    {
        _hiddenForGame = false;
        try
        {
            if (_hidden)
            {
                AppWindow.Show();
                _hidden = false;
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
            if (ContentFrame.Content is SettingsPage settings) { settings.FocusSearch(); return; }
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
        if (ContentFrame.Content is GamePage { GameId: { } id }) AppState.Settings.LastGameId = id;
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
        // On narrow windows the pane floats over the page and the stock menu button closes it: only the search stays.
        PaneButton.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = PaneFilterButton.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PaneBar.ColumnSpacing = open ? 6 : 0;
        PaneBar.Margin = open ? new Thickness(12, 2, 12, 8) : new Thickness(7, 2, 0, 8);
        PaneBar.HorizontalAlignment = open ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

    // ---------- navigation list ----------

    void ShowLoading()
    {
        Nav.MenuItems.Clear();
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
        var f = AppState.Settings.Library;
        if (!f.ShowNotInstalled || f.OptiOnly) return [];
        if (!OwnedLibrary.IsLoaded)
        {
            if (!_ownedLoading)
            {
                _ownedLoading = true;
                _ = OwnedLibrary.LoadAsync().ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    _ownedLoading = false;
                    if (OwnedLibrary.IsLoaded) RebuildNav();
                }), TaskScheduler.Default);
            }
            return [];
        }
        return OwnedLibrary.NotInstalled()
            .Where(g => _filter.Length == 0 || g.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase))
            .Where(g => f.Launchers.Count == 0 || f.Launchers.Contains("store:" + g.Store))
            .ToList();
    }

    /// <summary>The games under one group heading, in the list's order: what folding a group takes out or puts back.</summary>
    List<NavigationViewItem> GroupItems(string key)
    {
        if (key == NotInstalledKey) return NotInstalledShown().Select(CreateOwnedItem).ToList();
        return Library.Apply(AppState.VisibleGames.Where(Matches))
            .Where(g => !AppState.IsPinned(g.Id) && StoreLogos.GroupOf(g).Key == key)
            .Select(CreateItem).ToList();
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
            while (at < Nav.MenuItems.Count && Nav.MenuItems[at] is NavigationViewItem) Nav.MenuItems.RemoveAt(at);
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
        if (Content.XamlRoot is null) return false;
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
    public void KeepFocusOffSearch() => KeepFocusOffSearch(SearchHasFocus());

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
            if (!SearchHasFocus()) return;
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
    void RebuildNav()
    {
        KeepFocusOffSearch(SearchHasFocus());
        _rebuilding = true;
        try
        {
            Nav.MenuItems.Clear();

            // The PC overview sits on top, above the game list.
            var hardware = new NavigationViewItem { Content = L.T("My PC"), Tag = HardwareTag, Icon = PaneIcon("", 0xFF5AB8FF, 0xFF7C6CFF) };
            BigIcon(hardware);
            ToolTipService.SetToolTip(hardware, L.T("Your PC: hardware, display settings and the upscalers your card supports"));
            Nav.MenuItems.Add(hardware);

            // The library (every game as covers, with filters) and the downloads are always there, under My PC.
            var library = new NavigationViewItem { Content = L.T("Library"), Tag = LibraryTag, Icon = PaneIcon("\uE8F1", 0xFFFF6B8A, 0xFFE0142B) };
            BigIcon(library);
            ToolTipService.SetToolTip(library, L.T("All your games as a grid of covers"));
            Nav.MenuItems.Add(library);
            var downloads = new NavigationViewItem { Content = L.T("Downloads"), Tag = DownloadsTag, Icon = PaneIcon("\uE896", 0xFF5EE0A0, 0xFF1FA3A3) };
            BigIcon(downloads);
            ToolTipService.SetToolTip(downloads, L.T("Games you own on your launchers: install them and follow the downloads"));
            _downloadsItem = downloads;
            UpdateDownloadsBadge();
            Nav.MenuItems.Add(downloads);

            var items = new Dictionary<string, NavigationViewItem>(StringComparer.OrdinalIgnoreCase);

            // The library's sort and filters (one set, shared with the grid page and set from either Filters button).
            var shown = Library.Apply(AppState.VisibleGames.Where(Matches));
            var shownIds = shown.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            UpdatePaneFilterBadge();

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
                if (games.Count == 0 && pinned.Count == 0)
                    Nav.MenuItems.Add(new NavigationViewItemHeader
                    {
                        Content = _filter.Length > 0 ? L.T("No game matches the search")
                            : AppState.Settings.Library.IsFiltering && AppState.VisibleGames.Any() ? L.T("No game passes the filters")
                            : L.T("No games found"),
                    });

                // Same order as the library (sort setting), in the user's group order; a folded group shows only its heading.
                foreach (var (key, group) in Library.Groups(games))
                {
                    Nav.MenuItems.Add(new NavigationViewItemSeparator());
                    Nav.MenuItems.Add(GroupHeaderItem(key, StoreLogos.GroupHeader(group[0], group.Count, (Style)HybridLauncher.Helpers.ThemeRes.Current["PaneGroupLabel"])));
                    if (IsFolded(key)) continue;
                    foreach (var game in group) Nav.MenuItems.Add(items[game.Id] = CreateItem(game));
                }
            }

            // Filters → "Also games you own but have not installed": the grid's last section, after the groups here too.
            if (NotInstalledShown() is { Count: > 0 } owned)
            {
                Nav.MenuItems.Add(new NavigationViewItemSeparator());
                Nav.MenuItems.Add(GroupHeaderItem(NotInstalledKey, StoreLogos.NotInstalledHeader(owned.Count, (Style)HybridLauncher.Helpers.ThemeRes.Current["PaneGroupLabel"])));
                if (!IsFolded(NotInstalledKey))
                    foreach (var g in owned) Nav.MenuItems.Add(items[g.Id] = CreateOwnedItem(g));
            }

            switch (ContentFrame.Content)
            {
                case HardwarePage:
                    Nav.SelectedItem = hardware;
                    break;
                case SettingsPage:
                    Nav.SelectedItem = Nav.SettingsItem;
                    break;
                case LibraryPage page:
                    Nav.SelectedItem = library;
                    page.Reload(_filter);
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
                    else if (ContentFrame.Content is LibraryPage || lastGame is null)
                    {
                        // Hybrid Launcher opens on the library, like a launcher's home, unless a game was open last time.
                        Nav.SelectedItem = library;
                        ContentFrame.Navigate(typeof(LibraryPage), _filter);
                    }
                    else if (lastGame is not null)
                    {
                        Nav.SelectedItem = items.GetValueOrDefault(lastGame.Id);
                        ContentFrame.Navigate(typeof(GamePage), lastGame.Id);
                    }
                    else
                    {
                        Nav.SelectedItem = null;
                        if (ContentFrame.Content is not HomePage) ContentFrame.Navigate(typeof(HomePage));
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

    /// <summary>The pane's Filters button: the library's own panel; a dot on the button while any filter is on.</summary>
    void PaneFilters_Opening(object sender, object e) => PaneFiltersFlyout.Content = LibraryPage.FiltersPanel(PaneFiltersFlyout.Hide);

    void UpdatePaneFilterBadge()
    {
        int active = Library.ActiveFilters();
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
                (ContentFrame.Content as LibraryPage)?.ApplyFold(key);
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
        panel.Children.Add(new FontIcon { Glyph = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = L.T("PINNED"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, CharacterSpacing = 40, VerticalAlignment = VerticalAlignment.Center });
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

    /// <summary>Opens a game page (from the library grid, a pop-up...), selecting it in the pane when it is listed there.</summary>
    public void OpenGame(string id)
    {
        if (AppState.FindAny(id) is null) return;
        AppState.Settings.LastGameId = id;
        KeepFocusOffSearch(SearchHasFocus());
        _rebuilding = true;
        try
        {
            if ((ContentFrame.Content as GamePage)?.GameId != id) ContentFrame.Navigate(typeof(GamePage), id);
            Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is string t && t.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _rebuilding = false;
        }
    }

    const string HardwareTag = "hw";

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
        ToolTipService.SetToolTip(item, $"{game.Title}\n{game.StoreLabel} · {game.InstallDir}");

        item.ContextFlyout = GameMenu(game);
        // Resting on a game warms its art (after a short pause, so a pointer sweeping over the list asks for nothing).
        Microsoft.UI.Dispatching.DispatcherQueueTimer? warm = null;
        item.PointerEntered += (_, _) =>
        {
            warm ??= DispatcherQueue.CreateTimer();
            warm.Interval = TimeSpan.FromMilliseconds(120);
            warm.IsRepeating = false;
            warm.Tick -= OnWarm;
            warm.Tick += OnWarm;
            warm.Start();
        };
        item.PointerExited += (_, _) => warm?.Stop();
        void OnWarm(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object _) => Core.Media.GameInfo.Warm(game);
        if (GameSessions.IsActive(game.Id)) DispatcherQueue.TryEnqueue(() => MarkRunning(game.Id));

        _ = LoadIconAsync(item, game);
        return item;
    }

    /// <summary>A game you own but have not installed: dimmed, its launcher's icon until Steam's own is found; opens its Download page.</summary>
    NavigationViewItem CreateOwnedItem(OwnedGame game)
    {
        var item = new NavigationViewItem { Content = ItemName(game.Name), Tag = game.Id, Opacity = 0.62, Icon = new FontIcon { Glyph = "" } };
        BigIcon(item);
        ToolTipService.SetToolTip(item, $"{game.Name}\n{StoreLogos.Label(game.Store)} · {L.T("not installed")}");
        if (game.Store == GameStore.Steam && game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            _ = LoadIconAsync(item, OwnedLibrary.AsGame(game));
        return item;
    }

    /// <summary>The right-click menu of a game, in the list and in the library grid.</summary>
    public MenuFlyout GameMenu(Game game)
    {
        var menu = new MenuFlyout();
        bool pinned = AppState.IsPinned(game.Id);
        menu.Items.Add(MenuItem(pinned ? L.T("Unpin from quick access") : L.T("Pin to quick access"), pinned ? "" : "",
            () => AppState.SetPinned(game.Id, !pinned)));
        menu.Items.Add(MenuItem(L.T("Play"), "\uF5B0", () => _ = PlayFromMenuAsync(game)));
        menu.Items.Add(MenuItem(L.T("Open game folder"), "", () => GameLauncher.OpenFolder(game.InstallDir)));
        menu.Items.Add(MenuItem(L.T("Rename…"), "", () => _ = RenameGameAsync(game)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(L.T("Hide from the list"), "", () =>
        {
            AppState.Settings.HiddenGames.Add(game.Id);
            SettingsStore.Save();
            RebuildNav();
            ShowInfo(L.F("{0} is hidden. Show it again from Settings.", game.Title));
        }));
        if (!game.Installing)
        {
            var uninstall = MenuItem(L.T("Uninstall…"), "", () => { if (Content.XamlRoot is { } root) _ = UninstallFlow.RunAsync(game, root, ((FrameworkElement)Content).ActualTheme); });
            menu.Items.Add(uninstall);
        }
        if (game.Store == GameStore.Custom && game.Id.StartsWith("custom:", StringComparison.Ordinal))
        {
            menu.Items.Add(MenuItem(L.T("Remove from Hybrid Launcher"), "", () =>
            {
                AppState.Settings.CustomGames.RemoveAll(c => GameScanner.CustomId(c.ExePath) == game.Id);
                AppState.SetPinned(game.Id, false);
                SettingsStore.Save();
                _ = AppState.RescanAsync();
            }));
        }
        return menu;
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
        switch (ContentFrame.Content)
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
        foreach (var source in new[] { game.IconPath, steamIcon, game.MainExe, game.StoreExe }.Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase))
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
        if (_rebuilding) return;
        KeepFocusOffSearch(SearchHasFocus());
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: HardwareTag })
        {
            if (ContentFrame.Content is not HardwarePage) ContentFrame.Navigate(typeof(HardwarePage));
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: LibraryTag })
        {
            AppState.Settings.LastGameId = LibraryTag;
            if (ContentFrame.Content is not LibraryPage) ContentFrame.Navigate(typeof(LibraryPage), _filter);
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: DownloadsTag })
        {
            if (ContentFrame.Content is not DownloadsPage) ContentFrame.Navigate(typeof(DownloadsPage));
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: string id } && id != "add" && id != DownloadsTag)
        {
            // A game listed as not installed opens on its Download page.
            if (AppState.Find(id) is null && OwnedLibrary.Find(id) is { } owned) AppState.AddOwned(OwnedLibrary.AsGame(owned));
            AppState.Settings.LastGameId = id;
            if ((ContentFrame.Content as GamePage)?.GameId != id)
                ContentFrame.Navigate(typeof(GamePage), id);
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

    // ---------- self-test ----------

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
                if (SearchHasFocus()) throw new InvalidOperationException($"the game list's search box took the focus by itself (page shown: {ContentFrame.Content?.GetType().Name})");
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
        await Step("library", () => ContentFrame.Navigate(typeof(LibraryPage), ""), 2500);
        await Step("library search", () => SearchBox.Text = "a");
        await Step("library search cleared", () => SearchBox.Text = "");
        var savedFilter = AppState.Settings.Library;
        foreach (var sort in new[] { "recent", "added", "playtime", "achievements", "tech", "name" })
            await Step($"library sort {sort}", () => { AppState.Settings.Library = new LibraryFilter { Sort = sort, GroupByLauncher = sort != "tech" }; (ContentFrame.Content as LibraryPage)?.Reload(""); }, 400);
        await Step("library filters", () => { AppState.Settings.Library = new LibraryFilter { OptiOnly = true, Tech = { "DLSS" } }; (ContentFrame.Content as LibraryPage)?.Reload(""); }, 400);
        await Step("library filters off", () => { AppState.Settings.Library = savedFilter; (ContentFrame.Content as LibraryPage)?.Reload(""); }, 400);
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
                if (ContentFrame.Content is SettingsPage sp && sp.SelfTestColumn() is var (w, expected, off) && (Math.Abs(w - expected) > 1 || off > 2))
                    throw new InvalidOperationException($"column {w:0} wide (should be {expected:0}), {off:0} px off centre");
            }, 50);
        }
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
            foreach (var tab in (ContentFrame.Content as GamePage)?.SelfTestTabIds() ?? [])
            {
                await Step($"page game tab {tab} fits", () => (ContentFrame.Content as GamePage)?.SelfTestShow(tab), 1500);
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
            if (_padTimer?.IsRunning == true) throw new InvalidOperationException("the controller is still read with Controller navigation off");
        }, 200);
        await Step("controller on", () => { AppState.Settings.GamepadInput = true; GamepadSettingChanged(); }, 200);
        await Step("pane folded", () => { Nav.IsPaneOpen = false; }, 500);
        await Step("pane open", () => { Nav.IsPaneOpen = true; }, 500);
        foreach (var game in AppState.VisibleGames.ToList())
        {
            await Step($"open {game.Name}", () => OpenGame(game.Id), 2500);
            await Step($"{game.Name}: pin", () => AppState.SetPinned(game.Id, true));
            foreach (var (tab, view) in new[] { ("overview", (string?)null), ("opti", null), ("opti/quickpanel", null), ("opti/settings", "essentials"), ("opti/settings", "restart"), ("opti/settings", "changed"), ("opti/settings", "all"), ("opti/wiki", null), ("opti/notes", null), ("opti/install", null), ("mods", null), ("mods/renodx", null), ("mods/luma", null), ("mods/reshade", null), ("ultra", null), ("ultra/addons", null), ("ultra/settings", null), ("pcgw", null), ("pcgw/issues", null), ("pcgw/page2", null), ("ach", null), ("links", null), ("quick", null), ("notes", null), ("overview", null) })
                await Step($"{game.Name}: {tab} {view}", () => (ContentFrame.Content as GamePage)?.SelfTestShow(tab, view), tab is "overview" or "opti/wiki" or "opti/notes" or "pcgw" or "links" or "mods" or "mods/renodx" or "mods/luma" or "ultra" ? 2500 : 600);
            await Step($"{game.Name}: scroll (tabs pinned on top)", () => (ContentFrame.Content as GamePage)?.SelfTestScroll(true), 600);
            // A second picture of the same place a little later: what moves on its own (the clips of a description) differs between the two.
            await Step($"{game.Name}: scroll (still there)", () => { }, 1500);
            await Step($"{game.Name}: scroll back", () => (ContentFrame.Content as GamePage)?.SelfTestScroll(false), 600);
            await Step($"{game.Name}: unpin", () => AppState.SetPinned(game.Id, false));
        }
        // An install keeps showing when its game is left and opened again (the page is made again): busy, its progress and
        // log, no second install in the same folder, and free again when it ends. A stand-in job: nothing is installed.
        if (AppState.VisibleGames.FirstOrDefault() is { } jobGame)
        {
            Helpers.GameJobs.Job? fake = null;
            GamePage Page() => ContentFrame.Content as GamePage ?? throw new InvalidOperationException("the game page is not open");
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
        await Step("settings tabs", () => (ContentFrame.Content as SettingsPage)?.SelfTestTabs(), 800);
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
                if (ContentFrame.Content is SettingsPage sp)
                    await sp.SelfTestSearch(string.IsNullOrWhiteSpace(shots) ? null : () => SaveShotAsync(Path.Combine(shots, $"{++shot:000}-settings search results.png")));
                Log.Info("Self-test: settings search");
            }
            catch (Exception ex) { failures++; Log.Error("Self-test FAILED: settings search", ex); }
        }
        // Settings → Controller, and a button given to another action: the one that had it gets the old button back, then the defaults.
        await Step("settings controller", () => (ContentFrame.Content as SettingsPage)?.SelfTestController(), 900);
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
                if ((ContentFrame.Content as GamePage)?.GameId != achGame.Id) throw new InvalidOperationException("the game of the pop-up did not open");
                if (AchievementPopup.TakeRecent() is not null) throw new InvalidOperationException("the pop-up's achievement was not taken");
            }, 2500);
            if (achKey != "selftest-home")
                await Step("home button shows that achievement", () =>
                {
                    if ((ContentFrame.Content as GamePage)?.LastShownAchievement != achKey) throw new InvalidOperationException($"{achKey} was not shown in the Achievements tab");
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
        await Step("pop-up settings sounds", () => (ContentFrame.Content as SettingsPage)?.SelfTestSounds(), 900);
        await SelfTestSoundsAsync(Step);
        await Step("pop-up settings try it", () => (ContentFrame.Content as SettingsPage ?? throw new InvalidOperationException("Settings is not open")).SelfTestTryPopup(), 400);
        await Step("pop-up settings try it again", () => (ContentFrame.Content as SettingsPage)?.SelfTestTryPopup(), 1500);
        await Step("pop-up settings try it see-through", AchievementPopup.CheckSeeThrough, 50);
        await Step("pop-up settings looks in a row", () => (ContentFrame.Content as SettingsPage)?.SelfTestLooks(), 1500);
        await Step("pop-up settings looks see-through", AchievementPopup.CheckSeeThrough, 50);
        // A real achievement of a game in the library (when one has readable achievements), shown as its unlock would be.
        bool previewed = false;
        if (only.Length == 0 || only.Any(o => "pop-up settings real game".StartsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                previewed = await ((ContentFrame.Content as SettingsPage)?.SelfTestPreviewGameAsync() ?? Task.FromResult(false));
                await Task.Delay(2000);
                Log.Info($"Self-test: pop-up settings real game ({(previewed ? "shown" : "no game with readable achievements")})");
            }
            catch (Exception ex) { failures++; Log.Error("Self-test FAILED: pop-up settings real game", ex); }
        }
        if (previewed) await Step("pop-up settings real game see-through", AchievementPopup.CheckSeeThrough, 50);
        if (previewed && !string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } real and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up real game.png"), real);
        // The game's platinum from the same row (the picked game, or the sample when none can be read).
        await Step("pop-up settings real game platinum", () => _ = (ContentFrame.Content as SettingsPage)?.SelfTestPreviewPlatinum(), 2500);
        if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } platPreview and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up real game platinum.png"), platPreview);
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
            await Step("game page dark, material on", () => { Look("Hybrid Launcher", "Dark", true); OpenGame(first.Id); }, 2500);
            await Step("game page dark, material off", () => Look("Hybrid Launcher", "Dark", false), 800);
        }
        await Step("downloads material on", () => { Look("Hybrid Launcher", "Dark", true); ContentFrame.Navigate(typeof(DownloadsPage)); }, 3000);
        await Step("downloads material off", () => Look("Hybrid Launcher", "Dark", false), 800);
        await Step("library material off", () => ContentFrame.Navigate(typeof(LibraryPage), ""), 1500);
        await Step("library material on", () => Look("Hybrid Launcher", "Dark", true), 800);
        await Step("settings material on", () => ContentFrame.Navigate(typeof(SettingsPage)), 1500);
        await Step("settings material off", () => Look("Hybrid Launcher", "Dark", false), 800);
        Look(savedLook.Palette, savedLook.Theme, savedLook.Material);
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
        // Hybrid Launcher is dark only (0.20): one look to get right, as Steam's.
        var palette = Palettes.Get(AppState.Settings.Palette);
        var requested = ElementTheme.Dark;
        Palettes.ApplyResources(palette);
        // Brushes read in code follow the theme the window ends up in (set first: the flip below restyles everything twice).
        ThemeRes.Theme = requested;
        // Theme resources are only looked up again when the theme changes: flip it once so the new accent shows everywhere.
        RootGrid.RequestedTheme = RootGrid.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        RootGrid.RequestedTheme = requested;
        UpdateTint();
        UpdateCaptionButtons();
        ThemeRes.RaiseApplied();
    }

    void UpdateTint() =>
        TintLayer.Background = Palettes.TintBrush(Palettes.Get(AppState.Settings.Palette), RootGrid.ActualTheme == ElementTheme.Dark);

    void UpdateTitleBrand() => UpdatePageTitle();

    // ---------- page title in the title bar ----------

    /// <summary>On a game page the title bar shows "← GAME NAME", starting where the page content starts.</summary>
    void UpdatePageTitle()
    {
        var game = ContentFrame.Content is GamePage { GameId: { } id } ? AppState.Find(id) : null;
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
            var title = L.F("{0} {1} is available", AppInfo.Name, version);
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
            if (ContentFrame.Content is not GamePage { GameId: { } open } || !open.Equals(id, StringComparison.OrdinalIgnoreCase) || AppState.Find(id) is not null) return;
            if (OwnedLibrary.Find(id) is { } owned)
            {
                AppState.AddOwned(OwnedLibrary.AsGame(owned));
                ContentFrame.Navigate(typeof(GamePage), id);
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
            if (ContentFrame.Content is not DownloadsPage) ContentFrame.Navigate(typeof(DownloadsPage));
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
            if (ContentFrame.Content is SettingsPage open) open.SelectTab(tab);
            else ContentFrame.Navigate(typeof(SettingsPage));
            SyncSelection();
        }
        catch (Exception ex) { ShowError(L.T("Action failed"), ex.Message, ex); }
    }

    void SyncSelection()
    {
        _rebuilding = true;
        try
        {
            object? tag = ContentFrame.Content switch
            {
                LibraryPage => LibraryTag,
                DownloadsPage => DownloadsTag,
                HardwarePage => HardwareTag,
                GamePage { GameId: { } id } => id,
                _ => null,
            };
            if (ContentFrame.Content is SettingsPage) Nav.SelectedItem = Nav.SettingsItem;
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
        var cause = error is null ? Troubleshoot.Cause(message) : Troubleshoot.Cause(error);
        var text = cause is null || message.Contains(cause, StringComparison.Ordinal) ? message : message + "\n" + cause;
        Toast(InfoBarSeverity.Error, title, text, TimeSpan.FromSeconds(cause is null ? 12 : 16), Troubleshoot.Link(title, message));
    }

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
