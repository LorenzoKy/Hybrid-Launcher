using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Detection;
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
        AppWindow.Changed += (_, e) => { if (e.DidPresenterChange || e.DidSizeChange) UpdateMaximizeButton(); };
        CaptionButtons.SizeChanged += (_, _) => UpdateTitleBarRegions();
        StartGamepad();
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
        Nav.PaneOpening += (_, _) => { UpdatePaneButton(true); UpdatePageTitle(); UpdateGrip(true); };
        Nav.PaneClosing += (_, _) => { UpdatePaneButton(false); UpdatePageTitle(); UpdateGrip(false); };
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
            catch (Exception ex) { ShowError(L.T("Could not open the library"), ex.Message); }
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
            if (_quitting || !AppState.Settings.CloseToTray) return;
            e.Cancel = true;
            HideToTray();
        };
        Closed += (_, _) =>
        {
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
        if (AppState.Settings.CloseToTray) EnsureTray();
        ShowLoading();
        _ = StartAsync();
        AchievementWatcher.Start(DispatcherQueue);
    }

    // ---------- start, notification area ----------

    Tray? _tray;
    bool _quitting;
    bool _hidden;

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
        // The app's own updates: a quiet look at GitHub a few seconds after start; a newer version asks once.
        if (Content?.XamlRoot is { } root) _ = Helpers.AppUpdater.CheckAtStartAsync(root);
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
            ShowError(L.T("The game did not start"), ex.Message);
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

    void HideToTray()
    {
        SaveWindowState();
        SettingsStore.Save();
        EnsureTray();
        // A trailer must not keep playing in a window nobody sees.
        (ContentFrame.Content as GamePage)?.StopMedia();
        AppWindow.Hide();
        _hidden = true;
    }

    /// <summary>Brings the window back (tray icon, a second start of Hybrid Launcher).</summary>
    public void ShowFromTray()
    {
        try
        {
            if (_hidden)
            {
                AppWindow.Show();
                _hidden = false;
            }
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
            Activate();
            Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
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
        catch (Exception ex) { ShowError(L.T("Could not go back"), ex.Message); }
    }

    public void GoForward()
    {
        try
        {
            if (!ContentFrame.CanGoForward) return;
            ContentFrame.GoForward();
            AfterHistoryMove();
        }
        catch (Exception ex) { ShowError(L.T("Could not go forward"), ex.Message); }
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

    void PaneButton_Click(object sender, RoutedEventArgs e) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

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
        SearchBox.GettingFocus += (_, e) =>
        {
            bool byUser = e.InputDevice is Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Mouse or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Touch or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Pen
                || (e.InputDevice is Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Keyboard or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.GameController && e.Direction != Microsoft.UI.Xaml.Input.FocusNavigationDirection.None);
            if (byUser || _searchWanted || SearchHasFocus()) return;
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
    void KeepFocusOffSearch(bool searchHadFocus)
    {
        if (searchHadFocus) return;
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
            var hardware = new NavigationViewItem { Content = L.T("My PC"), Tag = HardwareTag, Icon = new FontIcon { Glyph = "" } };
            ToolTipService.SetToolTip(hardware, L.T("Your PC: hardware, display settings and the upscalers your card supports"));
            Nav.MenuItems.Add(hardware);

            // The library (every game as covers, with filters) and the downloads are always there, under My PC.
            var library = new NavigationViewItem { Content = L.T("Library"), Tag = LibraryTag, Icon = new FontIcon { Glyph = "\uE8F1" } };
            ToolTipService.SetToolTip(library, L.T("All your games as a grid of covers"));
            Nav.MenuItems.Add(library);
            var downloads = new NavigationViewItem { Content = L.T("Downloads"), Tag = DownloadsTag, Icon = new FontIcon { Glyph = "\uE896" } };
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
        catch (Exception ex) { App.Window?.ShowError(L.T("The game did not start"), ex.Message); }
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
            catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message); }
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
            ShowError(L.T("Could not add the game"), ex.Message);
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
            ShowError(L.T("Could not add the folder"), ex.Message);
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
                if (SearchHasFocus()) throw new InvalidOperationException("the game list's search box took the focus by itself");
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
        await Step("window narrow", () => AppWindow.Resize(new SizeInt32((int)(700 * GetDpiScale()), (int)(800 * GetDpiScale()))), 800);
        await Step("window wide", () => AppWindow.Resize(new SizeInt32((int)(1600 * GetDpiScale()), (int)(950 * GetDpiScale()))), 800);
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
        await Step("pane folded", () => Nav.IsPaneOpen = false, 500);
        await Step("pane open", () => Nav.IsPaneOpen = true, 500);
        foreach (var game in AppState.VisibleGames.ToList())
        {
            await Step($"open {game.Name}", () => OpenGame(game.Id), 2500);
            await Step($"{game.Name}: pin", () => AppState.SetPinned(game.Id, true));
            foreach (var (tab, view) in new[] { ("overview", (string?)null), ("opti", null), ("opti/quickpanel", null), ("opti/settings", "essentials"), ("opti/settings", "restart"), ("opti/settings", "changed"), ("opti/settings", "all"), ("opti/wiki", null), ("opti/install", null), ("mods", null), ("mods/renodx", null), ("mods/luma", null), ("mods/reshade", null), ("pcgw", null), ("pcgw/issues", null), ("pcgw/page2", null), ("ach", null), ("links", null), ("quick", null), ("notes", null), ("overview", null) })
                await Step($"{game.Name}: {tab} {view}", () => (ContentFrame.Content as GamePage)?.SelfTestShow(tab, view), tab is "overview" or "opti/wiki" or "pcgw" or "links" or "mods" or "mods/renodx" or "mods/luma" ? 2500 : 600);
            await Step($"{game.Name}: scroll (tabs pinned on top)", () => (ContentFrame.Content as GamePage)?.SelfTestScroll(true), 600);
            await Step($"{game.Name}: scroll back", () => (ContentFrame.Content as GamePage)?.SelfTestScroll(false), 600);
            await Step($"{game.Name}: unpin", () => AppState.SetPinned(game.Id, false));
        }
        await Step("my PC", () => ContentFrame.Navigate(typeof(HardwarePage)), 2000);
        await Step("settings", () => ContentFrame.Navigate(typeof(SettingsPage)), 1500);
        await Step("settings tabs", () => (ContentFrame.Content as SettingsPage)?.SelfTestTabs(), 800);
        await Step("pop-up", AchievementPopup.ShowTest, 1500);
        if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.CurrentHandle is { } popup and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up window.png"), popup);
        await Step("pop-up again", AchievementPopup.ShowTest, 7000);
        await Step("pop-up gone by itself", () => { if (AchievementPopup.CurrentHandle is not null) throw new InvalidOperationException("the pop-up is still on screen after its time"); }, 100);
        await Step("pop-up placing", () => AchievementPopup.Place((_, _) => { }), 1500);
        if (!string.IsNullOrWhiteSpace(shots) && AchievementPopup.PlacingHandle is { } placing and not 0)
            await SaveShotAsync(Path.Combine(shots, $"{++shot:000}-pop-up placing window.png"), placing);
        await Step("pop-up placing closed", AchievementPopup.CloseAll, 300);
        for (int f = 0; f <= 7; f++)
        {
            int step = f;
            await Step($"feature picker {step}", () => SelfTestFeaturePicker(step), step == 0 ? 1200 : 700);
        }
        for (int t = 0; t <= 7; t++)
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
        double brand = TitleBrand.ActualWidth;
        PageTitle.Margin = new Thickness(besidePane ? Math.Max(24, Nav.OpenPaneLength + 20 - 18 - 8 - brand) : 24, 0, 0, 0);
        DispatcherQueue.TryEnqueue(UpdateTitleBarRegions);
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
        catch (Exception ex) { ShowError(L.T("Action failed"), ex.Message); }
    }

    /// <summary>Opens Settings on one of its tabs ("appearance", "features"...): from a game page's menus.</summary>
    public void OpenSettings(string tab)
    {
        try
        {
            AppState.Settings.SettingsTab = tab;
            if (ContentFrame.Content is not SettingsPage) ContentFrame.Navigate(typeof(SettingsPage));
            SyncSelection();
        }
        catch (Exception ex) { ShowError(L.T("Action failed"), ex.Message); }
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

    public void ShowError(string title, string message) => Toast(InfoBarSeverity.Error, title, message, TimeSpan.FromSeconds(12));
    public void ShowInfo(string message) => Toast(InfoBarSeverity.Informational, "", message, TimeSpan.FromSeconds(5));
    public void ShowSuccess(string message) => Toast(InfoBarSeverity.Success, "", message, TimeSpan.FromSeconds(5));

    void Toast(InfoBarSeverity severity, string title, string message, TimeSpan duration)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ToastBar.Severity = severity;
            ToastBar.Title = title;
            ToastBar.Message = message;
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

    void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        int w = Math.Min(size.Width, area.Width), h = Math.Min(size.Height, area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }
}
