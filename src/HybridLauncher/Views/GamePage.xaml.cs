using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Ini;
using HybridLauncher.Core.Media;
using HybridLauncher.Core.Net;
using HybridLauncher.Core.Opti;
using HybridLauncher.Core.Steam;
using HybridLauncher.Controls;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace HybridLauncher.Views;

public partial class GamePage : Page, IAutoHost
{
    Game? _game;
    GameSettings _settings = new();
    CancellationTokenSource _pageCts = new();
    // The OptiScaler job this page shows (GameJobs): when it ends the page shows what is installed now.
    GameJobs.Job? _shownJob;
    List<OptiRelease> _stable = [], _nightly = [], _fork = [];
    string? _forkLoaded;
    List<CustomBuild> _custom = [];
    bool _releasesFailed;
    OptiChannel _channel = OptiChannel.Stable;
    string? _steamAppId;
    bool _loading;
    bool _busy;

    // The settings and achievements tabs are built the first time they are shown (and again when their data changes).
    string _tab = "opti";
    bool _iniStale = true;
    bool _achStale = true;

    // OptiScaler's files (logs grow while the game runs) are re-measured on this timer while the OptiScaler tab is open.
    readonly DispatcherTimer _filesTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    bool _measuringFiles;
    bool _measuringSize;
    int _ticks;

    // Store, wiki and HowLongToBeat data for this visit, shared by the banner and the info tabs (built when first shown).
    GameData? _data;
    OverviewView? _overview;
    // The Dashboard view (1.4.0) of the game shown, and whether the page shows it instead of the tabs.
    DashboardView? _dashboard;
    OptiWikiView? _optiWikiView;
    PcgwView? _pcgwView;
    LinksView? _linksView;
    string? _configDir, _saveDir;

    AchievementSet? _ach;
    readonly List<FrameworkElement> _achTiles = [];
    /// <summary>Each achievement's tile by its key (the Home button opens one).</summary>
    readonly Dictionary<string, Border> _achTileByKey = [];
    /// <summary>An achievement to show once the set is read (the Home button pressed on its pop-up).</summary>
    string? _achToShow;
    int _achColumns;
    int _achLaidOutColumns = -1;
    bool _loadingAch;

    public string? GameId => _game?.Id;

    /// <summary>
    /// How wide the tool tabs' content is drawn (OptiScaler, Mods, Ultra+), centred: most of the page, so little room is lost at
    /// its sides, up to a limit past which a row's name and its control drift too far apart. The Dashboard and the wikis use the
    /// whole page. Set by <see cref="FitContent"/>.
    /// </summary>
    double _contentMax = 1320;
    const double ContentShare = 0.92, ContentLimit = 2000;

    FrameworkElement[] ToolSections => [OptiTab, QuickPanelCard, IniCard, OptiPluginsGrid, OptiWikiHost, OptiNotesHost, ModsOverviewHost, ReShadeHost, RenoDxHost, LumaHost, Dlss5Host, DxvkHost, Vkd3dHost, UltraInstallHost, UltraAddonsHost, UltraSettingsHost, OfflineHost];

    /// <summary>The tool tabs' width for a page <paramref name="width"/> wide, set on their sections and separators.</summary>
    void FitContent(double width)
    {
        // A page given up or hidden meanwhile (its art loaded late) has no real width: NaN here threw "Value does not fall
        // within the expected range" from MaxWidth (2.0.0-rc.5).
        if (double.IsNaN(width) || width <= 0) return;
        double max = Math.Round(Math.Clamp(width * ContentShare, Math.Min(width, 1100), ContentLimit));
        if (Math.Abs(max - _contentMax) < 1) return;
        _contentMax = max;
        foreach (var section in ToolSections) section.MaxWidth = max;
        foreach (var sep in _separators.Values) if (!double.IsPositiveInfinity(sep.MaxWidth)) sep.MaxWidth = max;
    }

    // The welcome tour points at these.
    internal FrameworkElement TourPlay => PlayButton;
    /// <summary>Self-test: the page's scroller (scroll smoothness).</summary>
    internal ScrollView SelfTestScroller => Scroller;
    /// <summary>Self-test (perf): the tab row's buttons, to click.</summary>
    internal IEnumerable<Button> SelfTestTabButtons => Tabs.SelfTestButtons;
    internal FrameworkElement TourHdr => HdrToggle;
    internal FrameworkElement TourTabs => Tabs;
    internal FrameworkElement TourMore => MoreButton;

    // When the page was asked for: the log says how long a game switch took to show (see OnNavigatedTo).
    long _created = System.Diagnostics.Stopwatch.GetTimestamp();
    /// <summary>The page has shown a game before: the next one is filled into it after <see cref="ResetForNextGame"/>.</summary>
    bool _shownOnce;
    // True while the page is first shown: the open tab's views are built after the first frame, not before it.
    bool _deferViews;

    public GamePage()
    {
        using var __perf = UiPerf.Scope("GamePage.ctor");
        InitializeComponent();
        // The tabs that work on the game (OptiScaler, Mods, Ultra+) are centred on most of the page's width (FitContent).
        foreach (var section in ToolSections) section.MaxWidth = _contentMax;
        // Since 2.0.0-rc.4 one page per recent game, kept alive beside the Frame (MainWindow.GameSlots); a page can still be
        // filled again for its game (ResetForNextGame) when its settings or the app's look changed.
        Loc.Apply(this);
        // The GPU choice is read by index: its labels can be translated.
        GpuCombo.Items.Clear();
        foreach (var label in new[] { L.T("Auto-detect"), "AMD / Intel", "Nvidia" }) GpuCombo.Items.Add(label);
        foreach (var n in OptiSetupOptions.DllNames) DllCombo.Items.Add(n);
        // The file name chip of an install: a click offers the other names, swapped in place (no reinstall).
        OptiDllChip.Tapped += (_, e) => { e.Handled = true; ShowDllSwapMenu(); };
        ToolTipService.SetToolTip(OptiDllChip, L.T("The file name OptiScaler is loaded as. Click to change it without reinstalling."));
        Root.SizeChanged += (_, e) =>
        {
            _sectionsDirty = true;
            // The sections moved (the tab row pinned or let go, a width change): the marked sub-tab is checked again even
            // when no scroll step follows (a jump straight to a section left the pill on the section before it).
            QueueSpy();
            ApplyLayout(e.NewSize.Width);
            if (_tab == "pcgw") DispatcherQueue.TryEnqueue(PlaceToc);
        };
        // A section changing height moves the ones below it: the scroll-spy measures them again.
        foreach (var section in new FrameworkElement[] { OptiTab, QuickPanelCard, IniCard, OptiPluginsGrid, OptiWikiHost, OptiNotesHost, ModsOverviewHost, ReShadeHost, RenoDxHost, LumaHost, Dlss5Host, DxvkHost, Vkd3dHost, UltraInstallHost, UltraAddonsHost, UltraSettingsHost, OfflineHost, PcgwHost })
        {
            // A section changing height (built late, a card folded) moves the others: the marked sub-tab is checked again.
            section.SizeChanged += (_, _) =>
            {
                _sectionsDirty = true;
                if (_spyQueued) return;
                _spyQueued = true;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    _spyQueued = false;
                    UpdateSectionSpy(false);
                });
            };
            // A section shown or hidden after its tab opened (the game finished installing, a view hid itself) gets its
            // separator and pill back, or loses them, at once: they are never left to the next tab change.
            section.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueSubTabs());
        }
        _filesTimer.Tick += (_, _) =>
        {
            // Nobody works with the page (in the notification area, minimized, another app in front): nothing to read; the next
            // tick after the window is in front again catches up.
            if (MediaPolicy.Quiet) return;
            if (_tab == "opti") _ = UpdateFootprintAsync();
            _ = ReadWindowsHdrAsync();
            // Steam writes the session's time when the game closes: pick it up within a minute.
            if (++_ticks % 15 == 0) _ = UpdatePlayStatsAsync();
        };
        VersionsList.Changed += OnVersionsListChanged;
        PlayButton.Clicked += PlayOrStop;
        // The mouse wheel glides, once per display refresh (Helpers.SmoothWheel).
        CompositorWheel.Attach(Scroller);
        // Laid out again once the page is in the window: the sections' places are known only then.
        Loaded += (_, _) => QueueSubTabs();
        // Settings → Appearance → Shading: the launch bar's sheen from above.
        Sheen();
        Loaded += (_, _) => Sheen();
        ThemeRes.Applied += Sheen;
        AchIcon.Content = Icons.Make(Icons.TrophyGlyph, 17);
        InitFolds();
        InitTabs();
        InitLinks();
        // The floor lives in a layer behind the page: it follows the banner's place and width.
        Hero.SizeChanged += (_, _) => PlaceFloor();
        PageLayers.SizeChanged += (_, _) => PlaceFloor();
        // Reached with Tab it shows while it has focus; after a mouse click it goes back to showing on hover only.
        LinksToggle.GotFocus += (_, _) => { if (LinksToggle.FocusState == FocusState.Keyboard) LinksToggle.Opacity = 1; };
        LinksToggle.LostFocus += (_, _) => { if (!_overLinks) LinksToggle.Opacity = 0; };
    }

    /// <summary>Being prepared ahead of time, out of sight (MainWindow.PrepareStep): nothing is spread over frames for a viewer.</summary>
    internal bool Unseen { get; set; }

    /// <summary>The page is shown for <paramref name="id"/> (MainWindow.ShowGamePage; until 2.0.0-rc.4 the Frame's OnNavigatedTo).</summary>
    public void Enter(string? id)
    {
        using var __perf = UiPerf.Scope("GamePage.NavigatedTo");
        // Back to the game this page already shows (2.0.0-rc.4: one page per recent game, see MainWindow.NavigateGame): it is
        // shown as it was left, nothing built or loaded again; only what may have changed meanwhile is looked at, after the
        // first frame.
        if (CanResume(id))
        {
            Resume();
            return;
        }
        var built = System.Diagnostics.Stopwatch.GetElapsedTime(_created).TotalMilliseconds;
        if (_shownOnce)
        {
            _created = System.Diagnostics.Stopwatch.GetTimestamp();
            ResetForNextGame();
            built = 0;
        }
        _shownOnce = true;
        _game = AppState.FindAny(id);
        if (_game is null) return;
        _settings = AppState.Settings.For(_game.Id);
        _builtParts = SettingsKeys.Parts(AppState.Settings);
        _settingsStale = null;
        _builtLook = _lookVersion;
        _channel = _settings.Install?.Channel ?? OptiChannel.Stable;
        if (_channel == OptiChannel.Fork && _settings.Install?.Source is { } installedFork) AppState.Settings.SelectedFork = installedFork;
        ChannelCard(_channel).IsChecked = true;
        Subscribe();
        var ct = _pageCts.Token;
        var game = _game;
        if (TakeKept(game) is not { } kept)
        {
            (_data, _dataCts) = MakeData(game);
            Prefetch(_data);
        }
        // A switch to another game shows at once: the banner, the launch bar and the tab row come first, and the open tab's
        // content (the overview with its description, the mod views, the OptiScaler card read from the game folder...) is built
        // right after that first frame, while the page is already on screen. Building it before made every switch wait for it.
        _deferViews = true;
        Refresh();
        UpdateLinks();
        _ultraFits = UltraFitsNow();
        if (_ultraFits is null) _ = CheckUltraFitAsync(game, ct);
        RebuildTabs();
        ShowTab(AppState.Settings.GameTab, remember: false);
        _deferViews = false;
        var setup = System.Diagnostics.Stopwatch.GetElapsedTime(_created).TotalMilliseconds;
        // When the UI thread is next free (after the navigation's own work and anything queued before): the rest, up to the
        // first frame, is the page's first layout and drawing.
        double free = 0;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => free = System.Diagnostics.Stopwatch.GetElapsedTime(_created).TotalMilliseconds);
        var data = _data;
        AfterFirstFrame(() =>
        {
            if (ct.IsCancellationRequested || _game != game) return;
            Log.Info($"Game page {game.Name}: first frame {_firstFrameMs:F0} ms, settled {System.Diagnostics.Stopwatch.GetElapsedTime(_created).TotalMilliseconds:F0} ms (page built {built:F0}, filled {setup:F0}, UI free {free:F0})");
            ShowTab(_tab, remember: false);
            UiPerf.Layout(this, "first visit tab " + _tab);
            if (_optiPending) RefreshOpti();
            UiPerf.Layout(this, "first visit opti");
            if (data is not null) OptiWikiPreviewHost.Child = WikiPreviews.OptiCard(data);
            UiPerf.Layout(this, "first visit wiki preview");
            StartWarmUp();
        });
        // Always open a game at the top, with the banner visible.
        DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, 0, null, disableAnimation: true));
        _ = LoadArtAsync(_pageCts.Token);
        // The folder as read now: a later return reads it again only if it changes.
        var stampDir = TargetDir;
        _ = Task.Run(() => FolderStamp(stampDir)).ContinueWith(t => DispatcherQueue.TryEnqueue(() => { if (_game == game) _folderStamp = t.Result; }), TaskScheduler.Default);
        _ = LoadWikiExtrasAsync(_pageCts.Token);
        _ = LoadReleasesAsync(false, _pageCts.Token);
        _ = LoadCustomBuildsAsync(null, _pageCts.Token);
        if (!_game.NotInstalled) _ = MeasureGameSizeAsync(_pageCts.Token);
        _filesTimer.Start();
        // An install started before this page was made (the game was left and opened again while it ran) shows here.
        SyncJob();
    }

    /// <summary>The page follows the app's events while shown; never twice (a page prepared ahead of time may be shown mid-way).</summary>
    void Subscribe()
    {
        Unsubscribe();
        AppState.PinsChanged += UpdatePin;
        AchievementWatcher.Changed += OnAchievementsChanged;
        GameSessions.PlaytimeChanged += OnPlaytimeChanged;
        GameSessions.Changed += OnSessionChanged;
        CloudSync.Changed += OnCloudChanged;
        Downloads.Changed += OnDownloadsChanged;
        AppState.GamesChanged += OnDownloadsChanged;
        MediaPolicy.Changed += UpdateFloor;
        UltraPlusLibrary.Changed += OnUltraChanged;
        GameJobs.Changed += OnJobsChanged;
        GameJobs.Progress += OnJobProgress;
        OptiReleases.Updated += OnReleasesUpdated;
    }

    // ---------- one page per recent game (2.0.0-rc.4) ----------

    // What the page was built with: the app-wide settings it reads (a hash) and the look; either changing means building again.
    int _builtLook = -1;
    // The settings the page was last brought up to date with (Helpers.SettingsKeys.Parts), and the ones found changed on a
    // return (refreshed in place right after the page is back on screen).
    IReadOnlyDictionary<string, int>? _builtParts;
    List<string>? _settingsStale;
    // The game folder as it was when the page last read it (newest write, entry count): a return reads it again only if it moved.
    (DateTime, int) _folderStamp;
    // Ids whose page must be built again on the next visit (the page's own "open again as it is now").
    static readonly HashSet<string> FreshIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The next visit to <paramref name="id"/> builds its page again instead of showing it as it was left.</summary>
    public static void RequestFresh(string id) => FreshIds.Add(id);

    /// <summary>App-wide settings that changed since the page was brought up to date (not the per-game ones, the library's or the window's).</summary>
    List<string> SettingsChanged()
    {
        var now = SettingsKeys.Parts(AppState.Settings);
        var changed = SettingsKeys.Changed(_builtParts, now);
        changed.RemoveAll(k => k == "Library" || k.StartsWith("Games/", StringComparison.Ordinal));
        _builtParts = now;
        return changed;
    }

    bool CanResume(string? id)
    {
        if (!_shownOnce || _game is null || id is null || !string.Equals(_game.Id, id, StringComparison.OrdinalIgnoreCase)) return false;
        if (FreshIds.Remove(id)) return false;
        if (_builtLook != _lookVersion || AppState.FindAny(id) is not { } now) return false;
        // A rescan makes a new Game for every game: the page stays if it is the same game (folder, exe, state), and takes the
        // new one; a game that moved, changed its exe or finished installing is built again.
        if (!ReferenceEquals(now, _game))
        {
            if (!SameGame(now, _game)) return false;
            _game = now;
        }
        // A setting changed while the page was away (until rc.8 any save at all, an update check's included, built the page
        // again from nothing, 150-300 ms of loading on a return): it is shown as it was and brought up to date in place right
        // after its first frame. A new look (theme, palette) and the Features page still build it again (above).
        var changed = SettingsChanged();
        _settingsStale = changed.Count > 0 ? changed : null;
        return true;
    }

    static bool SameGame(Game a, Game b) =>
        a.Name == b.Name && a.Store == b.Store && string.Equals(a.InstallDir, b.InstallDir, StringComparison.OrdinalIgnoreCase)
        && a.StoreExe == b.StoreExe && a.MainExe == b.MainExe && a.SteamAppId == b.SteamAppId
        && a.NotInstalled == b.NotInstalled && a.Installing == b.Installing;

    void Resume()
    {
        using var __perf = UiPerf.Scope("GamePage.Resume");
        _created = System.Diagnostics.Stopwatch.GetTimestamp();
        _firstFrameMs = 0;
        _pageCts.Dispose();
        _pageCts = new CancellationTokenSource();
        _settings = AppState.Settings.For(_game!.Id);
        Subscribe();
        var game = _game;
        var ct = _pageCts.Token;
        // As a game is always opened: at the top, the banner in view.
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        AfterFirstFrame(() =>
        {
            if (ct.IsCancellationRequested || _game != game) return;
            Log.Info($"Game page {game.Name}: shown as left, first frame {_firstFrameMs:F0} ms" + (_settingsStale is { } st ? $", settings changed meanwhile: {string.Join(", ", st.Take(6))}" : ""));
            if (_settingsStale is not null)
            {
                _settingsStale = null;
                RefreshInPlace();
            }
            UpdatePin();
            UpdateRunState();
            SyncJob();
            _ = UpdatePlayStatsAsync();
            _ = UpdateCloudButtonAsync();
            // Pictures whose loading the last visit left unfinished.
            if (HeroImage.Source is null) _ = LoadArtAsync(ct);
            _ = ResumeFolderAsync(game, ct);
            _filesTimer.Start();
            StartWarmUp();
        });
    }

    /// <summary>
    /// The page brought up to date with the app's settings without being built again: what reads them (the launch bar, the
    /// tab row, the OptiScaler card, the links) is drawn again where it is. One part per turn of the UI thread, so the frames
    /// keep coming.
    /// </summary>
    void RefreshInPlace()
    {
        var game = _game;
        var steps = new List<Action> { Refresh, RebuildTabs, UpdateLinks, RefreshOpti };
        int i = 0;
        void Next()
        {
            if (i >= steps.Count || _game is null || !ReferenceEquals(_game, game)) return;
            using (UiPerf.Scope("GamePage.RefreshInPlace"))
            {
                try { steps[i++](); }
                catch (Exception ex) { Log.Warn($"Game page refresh: {ex.Message}"); }
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Next);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Next);
    }

    /// <summary>The game folder read again only when something in it changed while the page was away (a mod, an update).</summary>
    async Task ResumeFolderAsync(Game game, CancellationToken ct)
    {
        var dir = TargetDir;
        var stamp = await Task.Run(() => FolderStamp(dir));
        if (ct.IsCancellationRequested || _game != game) return;
        WatchModsFolder(dir);
        if (stamp == _folderStamp) return;
        _folderStamp = stamp;
        RefreshOpti();
        _ = UpdateModBadgesAsync();
        _ = UpdateFootprintAsync();
        if (_tab == "mods") _ = RefreshModsOverviewAsync();
        _dashboard?.RefreshMods();
        // The Auto tab's plan follows the folder too (2.0.0-rc.12).
        _autoView?.Refresh();
    }

    static (DateTime, int) FolderStamp(string? dir)
    {
        try
        {
            if (dir is null || !Directory.Exists(dir)) return default;
            var newest = DateTime.MinValue;
            int count = 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                count++;
                if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
            }
            return (newest, count);
        }
        catch { return default; }
    }

    /// <summary>
    /// Runs <paramref name="steps"/> one per turn of the UI thread (each after the frames queued before it), while the same game
    /// and the Mods tab stay open; a step that fails is logged and the next still runs.
    /// </summary>
    void BuildInTurns(List<Action> steps, Game? game)
    {
        int i = 0;
        void Next()
        {
            if (i >= steps.Count || _game is null || !ReferenceEquals(_game, game) || _tab != "mods") return;
            using (UiPerf.Scope("GamePage.ModsSection"))
            {
                try { steps[i++](); }
                catch (Exception ex) { Log.Warn($"Mods tab section: {ex.Message}"); }
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Next);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Next);
    }

    // ---------- info views: made once per game, ahead of time when the user is idle ----------

    /// <summary>Makes the info view of <paramref name="tab"/> for this game if it is not made yet (not placed anywhere).</summary>
    void MakeInfoView(string tab)
    {
        if (_data is null) return;
        using var __perf = UiPerf.Scope("GamePage.MakeView " + tab);
        switch (tab)
        {
            case "overview" when _dashboard is null && _game is not null:
                // The Dashboard tab: the old Game tab's parts (trailer, description, details...) are some of its cards.
                _overview ??= new OverviewView(_data);
                _dashboard = new DashboardView(_game, _settings, _data, _overview, _ach, _data.ShowTab, _data.Token);
                break;
            case "optiwiki" when _optiWikiView is null:
                _optiWikiView = new OptiWikiView(_data);
                break;
            case "pcgw" when _pcgwView is null:
                _pcgwView = new PcgwView(_data);
                BindPcgw(_pcgwView);
                break;
            case "links" when _linksView is null:
                _linksView = new LinksView(_data);
                break;
        }
    }

    /// <summary>The PCGamingWiki view's contents and table of contents act on this page (a kept view moves between pages).</summary>
    void BindPcgw(PcgwView view)
    {
        view.ScrollRequested = el => ScrollToElement(el);
        view.TocLayoutChanged = () => DispatcherQueue.TryEnqueue(PlaceToc);
    }

    /// <summary>Puts a view in its host, taking it from wherever it was (the warm-up layer, nowhere).</summary>
    static void Place(Border host, UIElement? view)
    {
        if (view is null || ReferenceEquals(host.Child, view)) return;
        Detach(view);
        host.Child = view;
    }

    /// <summary>
    /// Takes <paramref name="view"/> out of whatever holds it. Since 2.0.0-rc.4 a game's kept views can still sit in another
    /// game page (a retired page, another page's warm-up layer): putting them in a host while they had a parent threw
    /// "Value does not fall within the expected range" and left the game page empty (2.0.0-rc.5).
    /// </summary>
    static void Detach(UIElement view)
    {
        var parent = (view as FrameworkElement)?.Parent ?? Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(view);
        switch (parent)
        {
            case Border b when ReferenceEquals(b.Child, view): b.Child = null; break;
            case Panel p: p.Children.Remove(view); break;
            case ContentControl c when ReferenceEquals(c.Content, view): c.Content = null; break;
            case ContentPresenter cp when ReferenceEquals(cp.Content, view): cp.Content = null; break;
            case Microsoft.UI.Xaml.Controls.Primitives.Popup pop when ReferenceEquals(pop.Child, view): pop.Child = null; break;
        }
    }

    Canvas? _warmLayer;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _warmTimer;

    /// <summary>
    /// The info tabs not opened yet are made while nobody uses the app (no input for a moment, nothing scrolling), one per idle
    /// moment, and laid out unseen at the page's width in a layer of their own: opening one of them later only moves it into
    /// place, instead of building and laying out a large view on the click (200-450 ms).
    /// </summary>
    void StartWarmUp()
    {
        if (_warmLayer is null)
        {
            // Laid out but not drawn (off the window, Opacity 0). Tried in 2.0.0-rc.9: drawn too (composition visual at 0) made
            // the Dashboard tab open quicker, but every view held here was then drawn with the page, and showing pages and
            // the library got slower (snappy gate: prepared jank 69 -> 381 ms, library 81 -> 217 ms).
            _warmLayer = new Canvas { IsHitTestVisible = false, Opacity = 0 };
            Canvas.SetLeft(_warmLayer, -20000);
            PageLayers.Children.Insert(0, _warmLayer);
        }
        if (_warmTimer is null)
        {
            // Weak (2.0.0-rc.12): a timer holding this page through its handler kept every retired page alive.
            _warmTimer = WeakTimer.Create(DispatcherQueue, this, TimeSpan.FromMilliseconds(350), static p => p.WarmStep());
        }
        _warmTimer.Start();
    }

    void WarmStep()
    {
        if (_game is null || _data is null || App.ShuttingDown) { _warmTimer?.Stop(); return; }
        // Only while nobody is using the window: a view made during a scroll or a click would be the very hitch it saves.
        if (MediaPolicy.Quiet || FrameWarmth.Awake || UserIdleMs() < 900 || _loading) return;
        string? next = new[] { "overview", "optiwiki", "pcgw", "links" }.FirstOrDefault(t => t switch
        {
            "overview" => _dashboard is null,
            "optiwiki" => _optiWikiView is null,
            "pcgw" => _pcgwView is null,
            _ => _linksView is null,
        } && !GameTabs.Hidden(t == "optiwiki" ? "opti" : t, _settings));
        if (next is null)
        {
            if (!WarmNeighbour()) _warmTimer?.Stop();
            return;
        }
        MakeInfoView(next);
        UIElement? view = next switch { "overview" => _dashboard, "optiwiki" => _optiWikiView, "pcgw" => _pcgwView, _ => _linksView };
        if (view is FrameworkElement { Parent: null } fe && _warmLayer is not null)
        {
            double width = Math.Max(320, Root.ActualWidth - 40);
            _warmLayer.Children.Add(new Border { Width = width, Child = fe });
        }
    }

    /// <summary>Views left in the warm-up layer go with the game (they are kept with its other views, see Kept).</summary>
    void ClearWarmLayer()
    {
        _warmTimer?.Stop();
        if (_warmLayer is null) return;
        foreach (var b in _warmLayer.Children.OfType<Border>()) b.Child = null;
        _warmLayer.Children.Clear();
    }

    internal static double UserIdleMs()
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) : 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct LastInputInfo { public uint Size, Time; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>
    /// The store, wiki and reviews data of a game's page. It lives as long as the game's views are kept (see Kept), not only one
    /// visit: its own token, cancelled when they go.
    /// </summary>
    (GameData, CancellationTokenSource) MakeData(Game game)
    {
        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var art = GameInfo.GetAsync(game, ct);
        return (new GameData
        {
            Game = game,
            Token = ct,
            Art = art,
            Store = StoreAsync(art, ct),
            Pcgw = PcgwAsync(game, art, ct),
            OptiWiki = CompatWiki.GetEntryAsync(GameNames.PlainOrClean(GameNames.LookupName(game)), ct),
            // Through the page that shows the game when asked (2.0.0-rc.12): these views outlive the page that made them (Kept),
            // and the made page's OpenTab opened tabs on a page no longer shown, and held it in memory.
            ShowTab = tab => OpenTabOf(game.Id, tab),
        }, cts);
    }

    /// <summary>
    /// Idle warm-up for the games around this one in the list (the next a hand is likely to open): their overview is made and
    /// laid out unseen, and kept like a game shown before, so the first visit opens as fast as a return.
    /// </summary>
    bool WarmNeighbour()
    {
        if (_game is null || _warmLayer is null || App.Window is not { } window) return false;
        if (_kept.Count >= KeptMax - 2 || _kept.Values.Count(k => k.Prepared) >= 4) return false;
        foreach (var id in window.GamesNear(_game.Id, 4))
        {
            if (_kept.ContainsKey(id) || AppState.Find(id) is not { } game || GameTabs.Hidden("overview", AppState.Settings.For(id))) continue;
            using var __perf = UiPerf.Scope("GamePage.WarmNeighbour");
            var (data, cts) = MakeData(game);
            var overview = new OverviewView(data);
            var dashboard = new DashboardView(game, AppState.Settings.For(id), data, overview, null, data.ShowTab, data.Token);
            _warmLayer.Children.Add(new Border { Width = Math.Max(320, Root.ActualWidth - 40), Child = dashboard });
            // Older than anything shown: made ahead of time, it is the first to go when room is needed.
            _kept[id] = new Kept { Data = data, Cts = cts, Overview = overview, Dashboard = dashboard, Used = 0, Prepared = true };
            Prefetch(data);
            return true;
        }
        return false;
    }

    // ---------- the views of recently shown games ----------

    /// <summary>
    /// What a game's info tabs were built from and into, kept when another game is shown: going back to it shows its overview,
    /// wikis and links as they were, with no wait (building the overview and laying it out was 50-200 ms after each switch).
    /// The twelve games shown last are kept; the oldest goes first. A new look (theme, palette) drops them all.
    /// </summary>
    sealed class Kept
    {
        public required GameData Data;
        public required CancellationTokenSource Cts;
        public OverviewView? Overview;
        public DashboardView? Dashboard;
        public OptiWikiView? OptiWiki;
        public PcgwView? Pcgw;
        public LinksView? Links;
        public long Used;
        /// <summary>Made ahead of time by the idle warm-up, not shown yet.</summary>
        public bool Prepared;
    }

    const int KeptMax = 12;
    // Shared by every game page place (GameSlots): a game that lost its place keeps its views here.
    static readonly Dictionary<string, Kept> _kept = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _dataCts;
    static int _lookVersion;
    static int _keptLook;

    /// <summary>The kept views of games without a page go (the window went to the notification area: their memory is freed).</summary>
    internal static void DropKept()
    {
        foreach (var k in _kept.Values) k.Cts.Cancel();
        _kept.Clear();
    }

    /// <summary>The app's look changed: views built in the old one are not shown again.</summary>
    public static void ForgetViews() => _lookVersion++;

    void KeepViews()
    {
        // A kept Dashboard called this page back when its sections changed (2.0.0-rc.12): it held the page while it was kept.
        if (_dashboard is not null) _dashboard.SectionsChanged = null;
        if (_pcgwView is not null) { _pcgwView.ScrollRequested = null; _pcgwView.TocLayoutChanged = null; }
        if (_game is null || _data is null || _dataCts is null) return;
        _overview?.Hide();
        if (_kept.Remove(_game.Id, out var old) && old.Cts != _dataCts) old.Cts.Cancel();
        _kept[_game.Id] = new Kept
        {
            Data = _data, Cts = _dataCts, Overview = _overview, Dashboard = _dashboard, OptiWiki = _optiWikiView, Pcgw = _pcgwView, Links = _linksView,
            Used = System.Diagnostics.Stopwatch.GetTimestamp(),
        };
        _dataCts = null;
        while (_kept.Count > KeptMax)
        {
            var oldest = _kept.MinBy(k => k.Value.Used);
            oldest.Value.Cts.Cancel();
            _kept.Remove(oldest.Key);
        }
    }

    /// <summary>The kept views of <paramref name="game"/>, put back in their places (still hidden unless their tab is open).</summary>
    Kept? TakeKept(Game game)
    {
        if (_keptLook != _lookVersion)
        {
            foreach (var k in _kept.Values) k.Cts.Cancel();
            _kept.Clear();
            _keptLook = _lookVersion;
            return null;
        }
        if (!_kept.Remove(game.Id, out var kept)) return null;
        // A rescan made a new Game for it: its data may differ (folder, name), so it is read again.
        if (!ReferenceEquals(kept.Data.Game, game) || kept.Cts.IsCancellationRequested)
        {
            kept.Cts.Cancel();
            return null;
        }
        try
        {
            Place(OverviewHost, kept.Dashboard);
            Place(OptiWikiHost, kept.OptiWiki);
            Place(PcgwHost, kept.Pcgw);
            Place(LinksHost, kept.Links);
        }
        catch (Exception ex)
        {
            // Still held somewhere they cannot be taken from: the game's views are made again instead.
            Log.Info($"Kept views of {game.Name} not reusable, made again: {ex.Message}");
            OverviewHost.Child = OptiWikiHost.Child = PcgwHost.Child = LinksHost.Child = null;
            kept.Cts.Cancel();
            return null;
        }
        _data = kept.Data;
        _dataCts = kept.Cts;
        _overview = kept.Overview;
        _dashboard = kept.Dashboard;
        _optiWikiView = kept.OptiWiki;
        _pcgwView = kept.Pcgw;
        if (_pcgwView is not null) BindPcgw(_pcgwView);
        _linksView = kept.Links;
        return kept;
    }

    /// <summary>
    /// The page is kept for the next game (NavigationCacheMode): everything the last game left in it goes, so it starts as a
    /// new page would. The controls stay, already laid out; their values are set again by the game's own loading, and every
    /// load that was still running for the last game was cancelled (its token) or checks that its game is still the one shown.
    /// </summary>
    void ResetForNextGame()
    {
        using var __perf = UiPerf.Scope("GamePage.Reset");
        _pageCts.Dispose();
        _pageCts = new CancellationTokenSource();
        _floorCts?.Cancel();
        ClearWarmLayer();
        KeepViews();
        _dataCts?.Cancel();
        _dataCts = null;
        _game = null;
        _shownJob = null;
        _steamAppId = null;
        _busy = false;
        _iniStale = _achStale = true;
        _measuringFiles = _measuringSize = _loadingAch = false;
        _ticks = 0;
        _data = null;
        _configDir = _saveDir = null;
        _ach = null;
        _achToShow = null;
        _achTiles.Clear();
        _achTileByKey.Clear();
        _achText.Clear();
        if (_achSearch is not null && _achSearch.Text.Length > 0) _achSearch.Text = "";
        AchGrid.Children.Clear();
        _achLaidOutColumns = -1;
        _optiPending = false;
        _ultraFits = null;
        _firstFrameMs = 0;
        // The views of the last game's tabs: made again for this one when its tabs are opened.
        _overview?.Hide();
        _overview = null; OverviewHost.Child = null;
        _dashboard = null;
        _optiWikiView = null; OptiWikiHost.Child = null;
        OptiNotesHost.Child = null;
        _pcgwView = null; PcgwHost.Child = null;
        _linksView = null; LinksHost.Child = null;
        _reshadeView = null; ReShadeHost.Child = null;
        _renoDxView = null; RenoDxHost.Child = null;
        _lumaView = null; LumaHost.Child = null;
        _dxvkView = null; DxvkHost.Child = null;
        _vkd3dView = null; Vkd3dHost.Child = null;
        _dlss5View = null; _dlss5Body = null; _dlss5Stale = false; _dlss5Pills = null; _dlss5Page = "overview"; Dlss5Host.Child = null;
        ResetModsOverview();
        _ultra = null; UltraInstallHost.Child = null; UltraAddonsHost.Child = null; UltraSettingsHost.Child = null;
        _quickView = null; QuickHost.Child = null;
        _notesView = null; NotesHost.Child = null;
        _autoView = null; AutoHost.Child = null;
        OfflineHost.Child = null;
        OptiWikiPreviewHost.Child = null;
        TocPin.Child = null;
        TocPin.Visibility = Visibility.Collapsed;
        _sectionEls = []; _sectionIds = []; _sectionTops = [];
        _sectionsDirty = true;
        _forcedSection = null;
        if (_stuck)
        {
            _stuck = false;
            StickySlot.Child = null;
            StickyBar.Visibility = Visibility.Collapsed;
            TabHost.Child = Tabs;
            TabHost.Height = double.NaN;
        }
        // The banner of the last game.
        _heroUri = _floorKey = null;
        HeroImage.Source = null;
        HeroImage.Opacity = 0;
        FloorImage.Source = null;
        LogoImage.Source = null;
        LogoImage.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        MetaText.Text = "";
        TechBadges.Children.Clear();
        TechBadges.Visibility = Visibility.Collapsed;
        ApiBadge.Visibility = Visibility.Collapsed;
        ConfigSizeText.Text = SaveSizeText.Text = L.T("Measuring…");
        LastPlayedText.Text = PlayTimeText.Text = "—";
        _argsExpanded = false;
        _selectCustom = null;
        _update = null;
        _updateCheck++;
        _cloud = null;
        _cloudAsk++;
        _modBadgesRun++;
        _doc = null;
        _dupKeys.Clear();
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Retired pages, watched by the self-test's page churn: none should outlive a full collection (2.0.0-rc.12).</summary>
    internal static readonly List<WeakReference<GamePage>> Retired = [];
    /// <summary>
    /// A game page taken out of the window for good (MainWindow keeps the last eight games' pages): its views go to the kept
    /// ones, so the game opens quickly again later.
    /// </summary>

    internal void Retire()
    {
        if (App.SelfTesting) lock (Retired) Retired.Add(new WeakReference<GamePage>(this));
        // The app-wide events let go of the page (2.0.0-rc.12): the two added when it was made held every retired page, with its
        // whole visual tree and pictures, for the rest of the session.
        Unsubscribe();
        VersionsList.Changed -= OnVersionsListChanged;
        ThemeRes.Applied -= Sheen;
        _warmTimer?.Stop();
        _filesTimer.Stop();
        _pageCts.Cancel();
        _overview?.StopTrailer();
        StopWatchingModsFolder();
        ClearWarmLayer();
        KeepViews();
        // The kept views leave this page with it: the game's next page takes them free.
        OverviewHost.Child = OptiWikiHost.Child = PcgwHost.Child = LinksHost.Child = null;
        _overview = null; _dashboard = null; _optiWikiView = null; _pcgwView = null; _linksView = null;
        _game = null;
    }

    /// <summary>The page is hidden (another page or game is shown): its work stops until it is shown again.</summary>
    public void Leave()
    {
        // The warm-up layer stays as it is: the views laid out there ahead of time are what makes this page's tabs open at
        // once (emptying it here cost 300+ ms on the Opti tab). A neighbour's views held here are taken out by Detach.
        _warmTimer?.Stop();
        _pageCts.Cancel();
        _filesTimer.Stop();
        StopWatchingModsFolder();
        _overview?.StopTrailer();
        Unsubscribe();
        SaveArgs();
    }

    void OnVersionsListChanged() => _ = LoadCustomBuildsAsync(null, _pageCts.Token);

    /// <summary>Settings → Appearance → Shading: the launch bar's sheen from above.</summary>
    void Sheen() => LaunchSheen.Visibility = AppState.Settings.Shading ? Visibility.Visible : Visibility.Collapsed;

    void Unsubscribe()
    {
        AppState.PinsChanged -= UpdatePin;
        AchievementWatcher.Changed -= OnAchievementsChanged;
        GameSessions.PlaytimeChanged -= OnPlaytimeChanged;
        GameSessions.Changed -= OnSessionChanged;
        CloudSync.Changed -= OnCloudChanged;
        Downloads.Changed -= OnDownloadsChanged;
        AppState.GamesChanged -= OnDownloadsChanged;
        MediaPolicy.Changed -= UpdateFloor;
        UltraPlusLibrary.Changed -= OnUltraChanged;
        GameJobs.Changed -= OnJobsChanged;
        GameJobs.Progress -= OnJobProgress;
        OptiReleases.Updated -= OnReleasesUpdated;
    }

    /// <summary>When the page's first frame was drawn, from when it was asked for (for the log's game-switch timing).</summary>
    double _firstFrameMs;

    /// <summary>Runs <paramref name="action"/> once the next frame has been drawn (then at low priority, after input and layout).</summary>
    void AfterFirstFrame(Action action)
    {
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            if (_firstFrameMs == 0) _firstFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(_created).TotalMilliseconds;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Safe(action));
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
    }

    /// <summary>Stops a playing trailer (the window went to the notification area).</summary>
    public void StopMedia() => _overview?.StopTrailer();

    /// <summary>Re-reads the game folder and redraws everything that depends on it.</summary>
    public void Refresh()
    {
        using var __perf = UiPerf.Scope("GamePage.Refresh");
        if (_game is null) return;
        _loading = true;
        try
        {
            TitleText.Text = _game.Title;
            StoreText.Text = _game.StoreLabel.ToUpperInvariant();
            if (StoreBadgePanel.Children.Count > 2) StoreBadgePanel.Children.RemoveAt(0);
            var logo = StoreLogos.Create(_game.Store, 13);
            logo.VerticalAlignment = VerticalAlignment.Center;
            StoreBadgePanel.Children.Insert(0, logo);
            ToolTipService.SetToolTip(StoreButton, StoreLinks.Describe(_game, _steamAppId));
            ArgsBox.Text = _settings.LaunchArgs;
            ArgsBox.IsEnabled = GameLauncher.SupportsArgs(_game);
            if (!ArgsBox.IsEnabled) ArgsBox.PlaceholderText = L.T("Xbox / Game Pass games cannot receive launch arguments");
            // Off the bar by default: the arguments live in More menu, Launch options (which can bring the box back).
            ArgsBox.Visibility = _settings.LaunchArgsInBar && !_game.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
            LayoutArgsBox();

            // Not on this PC: no folder, exe, mods or pin; Download and its progress in place of Play.
            bool offline = _game.NotInstalled;
            FolderGroup.Visibility = PinButton.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
            if (offline)
            {
                _exeLineAlert = true;
                ExeLine.Text = L.F("Not installed. Download installs it through {0}; Hybrid Launcher follows the download here.", _game.StoreLabel);
                UpdateHdrToggle();
                UpdateRunState();
                return;
            }

            var exe = GameScanner.EffectiveExe(_game, _settings);
            _exeLineAlert = _game.Installing || exe is null;
            ExeLine.Text = _game.Installing
                ? L.F("Downloading through {0}: Play is ready when it finishes. Cancel it from Downloads.", _game.StoreLabel)
                : exe is null
                ? L.T("No game exe found. Use the gear beside Play → Change game exe to pick it.")
                : L.F("Mods go next to {0}  ·  {1}", Path.GetFileName(exe), Path.GetDirectoryName(exe))
                  + (_settings.ExeOverride is not null ? "  " + L.T("(chosen by you)") : "");

            // The OptiScaler card reads the game folder (the dll, OptiScaler.ini, plugins): on a switch to another game it is
            // filled right after the first frame, so the page shows without waiting for the disk.
            if (_deferViews) _optiPending = true;
            else RefreshOpti();
            UpdatePin();
            ShowGameSize();
            UpdateHdrToggle();
            _ = UpdateCloudButtonAsync();
            UpdateRunState();
        }
        finally
        {
            _loading = false;
        }
        ApplyPageParts();
        if (_game.NotInstalled)
        {
            _ = LoadAchievementsAsync(_pageCts.Token);
            return;
        }
        // A game already running (started from its launcher) gets Stop too.
        if (GameScanner.EffectiveExe(_game, _settings) is { } runExe) GameSessions.Observe(_game, _settings, Path.GetDirectoryName(runExe));
        _ = UpdateOptiPatcherHintAsync(_pageCts.Token);
        _ = UpdateFootprintAsync();
        _ = UpdatePlayStatsAsync();
        _ = LoadAchievementsAsync(_pageCts.Token);
    }

    bool _optiPending;

    /// <summary>The OptiScaler card and its side cards: setup answers, status, ASI plugins, custom dlls and the ini view.</summary>
    void RefreshOpti()
    {
        using var __perf = UiPerf.Scope("GamePage.RefreshOpti");
        _optiPending = false;
        bool was = _loading;
        _loading = true;
        try
        {
            LoadSetupOptions();
            UpdateOptiStatus();
            LoadAsiList();
            UpdateDlssNrRow();
            LoadCustomDllList();
            _iniStale = true;
            if (IniOpen) LoadIni();
        }
        finally { _loading = was; }
    }

    string? TargetDir => _game is null ? null : GameScanner.TargetDir(_game, _settings);

    // ---------- tabs ----------

    ReShadeView? _reshadeView;
    RenoDxView? _renoDxView;
    LumaView? _lumaView;
    DxvkView? _dxvkView;
    DxvkView? _vkd3dView;
    UltraPlusTab? _ultra;
    QuickAccessView? _quickView;
    NotesView? _notesView;
    AutoView? _autoView;

    void InitTabs()
    {
        Tabs.TabInvoked += id =>
        {
            // Picked from the bar pinned on top: the new tab opens right under it, not wherever the old one was scrolled to.
            bool pinned = _stuck;
            ShowTab(id);
            if (pinned) DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, TabTop(), null, disableAnimation: true));
        };
        Tabs.Reordered += order => GameTabs.SaveOrder(MergeOrder(order));
        Tabs.HideRequested += HideTab;
        Tabs.SubTabInvoked += id => ScrollToSection(id);
        Tabs.CustomizeRequested += async () =>
        {
            if (_game is null) return;
            if (_tab == "overview")
            {
                try { if (await DashboardView.CustomizeAsync(XamlRoot, ActualTheme)) _dashboard?.Rebuild(); }
                catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the dashboard"), ex.Message, ex); }
                return;
            }
            try
            {
                if (!await TabsDialog.ShowAsync(XamlRoot, ActualTheme, _settings, _game.Title)) return;
                RebuildTabs();
                ShowTab(_tab, remember: false);
            }
            catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the tabs"), ex.Message, ex); }
        };
    }

    /// <summary>
    /// The achievements tab needs Steam achievements; every other tab is always there unless hidden. On the page of a game not on
    /// this PC, the tabs that work on its files say what they do and offer Download (see <see cref="NeedsFiles"/>).
    /// </summary>
    bool Available(string id) => id switch
    {
        "ach" => _ach is not null,
        // Until Ultra+'s list is read the tab shows only when asked for (a notification, the Ultra+ page's Open).
        "ultra" => _ultraFits ?? AppState.Settings.GameTab == "ultra",
        _ => true,
    };

    /// <summary>Ultra+ can do something for this game: null while Ultra+'s list of games is not read yet.</summary>
    bool? _ultraFits;

    /// <summary>
    /// The Ultra+ tab is only for games on Ultra+'s own list (read from its server, so a game Ultra+ adds later gets the tab
    /// without a Hybrid Launcher update) and games it is already in or was picked for; other games do not get it.
    /// </summary>
    bool? UltraFitsNow()
    {
        if (_game is null) return false;
        if (_settings.UltraPlus is not null || _settings.UltraPlusKey is not null) return true;
        if (Core.Mods.UltraPlus.Cached is not { } games) return null;
        return Core.Mods.UltraPlus.Match(games, _game.Name, _game.InstallDir, GameScanner.EffectiveExe(_game, _settings)).Count > 0;
    }

    /// <summary>Ultra+'s list read again or a mod picked, installed or removed: the tab comes or goes at once.</summary>
    void OnUltraChanged()
    {
        if (_game is null || UltraFitsNow() is not { } fits || fits == _ultraFits) return;
        _ultraFits = fits;
        RebuildTabs();
        if (!fits && _tab == "ultra") ShowTab("overview", remember: false);
    }

    async Task CheckUltraFitAsync(Game game, CancellationToken ct)
    {
        try
        {
            if (GameTabs.HiddenEverywhere("ultra")) return;
            await Core.Mods.UltraPlus.GetGamesAsync(ct: ct);
            if (ct.IsCancellationRequested || _game != game) return;
            var fits = UltraFitsNow() ?? false;
            if (fits == _ultraFits) return;
            _ultraFits = fits;
            RebuildTabs();
            if (!fits && _tab == "ultra") ShowTab("overview", remember: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Ultra+ list for {game.Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Tabs with sections about the game's own files: on a game not installed yet the OptiScaler tab shows <see cref="OfflineCard"/>
    /// in place of its install and settings, and the ReShade section of ReShade &amp; Mods shows it (the wiki, RenoDX and Luma still work).
    /// </summary>
    bool NeedsFiles(string id) => _game?.NotInstalled == true && id is "opti" or "mods" or "auto";

    /// <summary>The OptiScaler.ini editor is on screen (its tab is open, the game is installed).</summary>
    bool IniOpen => IniCard.Visibility == Visibility.Visible;

    /// <summary>The ids of the tabs this game shows (self-test).</summary>
    internal List<string> SelfTestTabIds() => VisibleTabs().Select(t => t.Id).ToList();

    /// <summary>The tab open now.</summary>
    internal string OpenTabId => _tab;

    /// <summary>Opens a tab without remembering it as the one to open next time (pages prepared ahead of time, MainWindow.GameSlots).</summary>
    internal void ShowTabQuietly(string tab) => ShowTab(tab, remember: false);

    /// <summary>Self-test (snappy): opens a tab as its button does.</summary>
    internal void SelfTestShowTab(string tab) => ShowTab(tab);

    List<GameTab> VisibleTabs() => GameTabs.Ordered().Where(t => Available(t.Id) && !GameTabs.Hidden(t.Id, _settings)).ToList();

    void RebuildTabs() => Tabs.SetTabs(VisibleTabs(), _tab);

    /// <summary>The strip holds only the visible tabs: their new order replaces theirs in the full list, hidden tabs keep their slots.</summary>
    static List<string> MergeOrder(List<string> visible)
    {
        var all = GameTabs.Ordered().Select(t => t.Id).ToList();
        var queue = new Queue<string>(visible);
        var set = visible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Select(id => set.Contains(id) && queue.Count > 0 ? queue.Dequeue() : id).ToList();
    }

    void HideTab(string id, bool everywhere)
    {
        var name = GameTabs.Find(id)?.Name ?? id;
        if (VisibleTabs().Count <= 1)
        {
            App.Window?.ShowInfo(L.T("That is the only tab left: keep at least one."));
            return;
        }
        if (everywhere) AppState.Settings.HiddenTabs.Add(id);
        else _settings.HiddenTabs.Add(id);
        SettingsStore.Save();
        RebuildTabs();
        if (id == _tab) ShowTab("overview", remember: false);
        App.Window?.ShowInfo(everywhere
            ? L.F("\"{0}\" is hidden in every game. The gear at the end of the tabs brings it back.", name)
            : L.F("\"{0}\" is hidden for {1}. The gear at the end of the tabs brings it back.", name, _game?.Title));
    }

    /// <summary>A tab of the game's page on screen, when the game shown is <paramref name="gameId"/>.</summary>
    static void OpenTabOf(string gameId, string tab)
    {
        if (App.Window?.ShownGamePage is { } page && string.Equals(page.GameId, gameId, StringComparison.OrdinalIgnoreCase)) page.OpenTab(tab);
    }

    /// <summary>
    /// Opens a tab from a link on the page (any id, old ones included: see <see cref="GameTabs.Resolve"/>), and brings the part
    /// wanted into view: the section of a tab that holds several, or the tab row.
    /// </summary>
    void OpenTab(string id)
    {
        var (tab, section) = GameTabs.Resolve(id);
        ShowTab(tab, section: section);
        if (section is null) ScrollToTabs();
        else DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ScrollToSection(section));
    }

    // ---------- Dashboard (1.4.0; the first tab since rc.7) ----------

    /// <summary>Self-test: opens the Dashboard tab and reports what it shows.</summary>
    internal (int Cards, bool NewsLoaded, int Activity)? SelfTestDashboard()
    {
        ShowTab("overview", remember: false);
        return _dashboard?.SelfTestState();
    }

    /// <summary>Self-test: the Dashboard's cards reordered and hidden, then back (DashboardView.SelfTestReorder).</summary>
    internal string SelfTestDashboardReorder() => _dashboard?.SelfTestReorder() ?? throw new InvalidOperationException("no dashboard");

    /// <summary>Self-test: opens the newest post of the Dashboard in the app's reader.</summary>
    internal bool SelfTestReadPost() => _dashboard?.SelfTestReadPost() ?? false;

    /// <summary>
    /// Self-test: gives the Dashboard a made-up set of achievements (13 of 43 unlocked, rarities from 2% to 80%, no icons so
    /// the tier trophies stand in) and scrolls to its card, for a picture of the card without a signed-in store.
    /// </summary>
    internal bool SelfTestSampleAchievements()
    {
        if (_dashboard is null) return false;
        var items = new List<Achievement>();
        for (int i = 0; i < 43; i++)
            items.Add(new Achievement
            {
                Key = $"s:{i}", ApiName = $"SAMPLE_{i}", Name = i == 7 ? "Hang Ten" : $"Sample achievement {i + 1}",
                Description = i == 7 ? "Surf a wave for ten seconds without falling" : "Do the thing this achievement asks for",
                Unlocked = i < 13, UnlockedAt = i < 13 ? DateTime.UtcNow.AddHours(-3 - i * 20) : null, GlobalPercent = i == 7 ? 2.4 : 8 + i * 1.7,
            });
        _dashboard.SetAchievements(new AchievementSet("sample", items));
        return true;
    }

    /// <summary>Self-test: opens or closes the Dashboard's version history.</summary>
    internal bool SelfTestHistory(bool open) => _dashboard?.SelfTestHistory(open) ?? false;

    /// <summary>Shows one tab; the others are collapsed, so they cost nothing to lay out or draw.</summary>
    int _optiReveal, _modsReveal;

    void ShowTab(string tab, bool remember = true, string? section = null)
    {
        using var __perf = UiPerf.Scope("GamePage.ShowTab");
        tab = GameTabs.Resolve(tab).Tab;
        var visible = VisibleTabs();
        if (!visible.Any(t => t.Id == tab))
        {
            remember = false;
            tab = visible.FirstOrDefault(t => t.Id == "overview")?.Id ?? visible.FirstOrDefault()?.Id ?? "overview";
        }
        if (tab != "overview") _dashboard?.Hide();
        _tab = tab;
        Tabs.Select(tab);
        Tabs.SetView(tab == "overview");
        bool offline = NeedsFiles(tab);
        bool opti = tab == "opti", mods = tab == "mods", auto = tab == "auto";
        OfflineHost.Visibility = offline && (opti || auto) ? Visibility.Visible : Visibility.Collapsed;
        if (offline && (opti || auto)) OfflineHost.Child = OfflineCard(GameTabs.Find(tab)!);
        AutoHost.Visibility = auto && !offline ? Visibility.Visible : Visibility.Collapsed;
        // A switch to another game: the OptiScaler card (the largest part of the page) is laid out on the frame after the first,
        // so the banner, the launch bar and the tabs show at once.
        bool optiFiles = opti && !offline && !_deferViews;
        // The OptiScaler tab's sections come back from Collapsed one per frame (2.0.0-rc.6): all at once, WinUI laid out the
        // install block, plugins, Quick Panel, every setting, the wiki and the notes before drawing anything (340-460 ms
        // after the click). The first shows with the click; asked for a section by name, all of them show at once.
        bool optiInTurns = opti && section is null && OptiTab.Visibility != Visibility.Visible;
        var optiRest = new List<(FrameworkElement El, bool Show)>
        {
            (OptiPluginsGrid, optiFiles), (QuickPanelCard, optiFiles), (IniCard, optiFiles), (OptiWikiHost, opti), (OptiNotesHost, opti),
        };
        OptiTab.Visibility = optiFiles ? Visibility.Visible : Visibility.Collapsed;
        int optiTurn = ++_optiReveal;
        foreach (var (el, show) in optiRest)
            if (!show) el.Visibility = Visibility.Collapsed;
            else if (!optiInTurns) el.Visibility = Visibility.Visible;
        if (optiInTurns)
        {
            var queue = new Queue<FrameworkElement>(optiRest.Where(x => x.Show).Select(x => x.El));
            void Reveal()
            {
                if (optiTurn != _optiReveal || _tab != "opti" || queue.Count == 0) return;
                using (UiPerf.Scope("GamePage.OptiSection"))
                {
                    var el = queue.Dequeue();
                    el.Visibility = Visibility.Visible;
                    UiPerf.Layout(this, "opti section " + el.Name);
                }
                if (queue.Count > 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Reveal);
                else _sectionsDirty = true;
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Reveal);
        }
        AchCard.Visibility = tab == "ach" ? Visibility.Visible : Visibility.Collapsed;
        OverviewHost.Visibility = tab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PcgwHost.Visibility = tab == "pcgw" ? Visibility.Visible : Visibility.Collapsed;
        LinksHost.Visibility = tab == "links" ? Visibility.Visible : Visibility.Collapsed;
        // The Mods tab the same way: the table first, then ReShade and the others, one per frame (2.0.0-rc.6).
        bool modsInTurns = mods && section is null && ModsOverviewHost.Visibility != Visibility.Visible;
        int modsTurn = ++_modsReveal;
        var modsHosts = new FrameworkElement[] { ModsOverviewHost, ReShadeHost, RenoDxHost, LumaHost, Dlss5Host, DxvkHost, Vkd3dHost };
        foreach (var host in modsHosts)
            host.Visibility = mods && (!modsInTurns || host == ModsOverviewHost) ? Visibility.Visible : Visibility.Collapsed;
        if (modsInTurns)
        {
            var queue = new Queue<FrameworkElement>(modsHosts.Skip(1));
            void Reveal()
            {
                if (modsTurn != _modsReveal || _tab != "mods" || queue.Count == 0) return;
                using (UiPerf.Scope("GamePage.ModsReveal"))
                {
                    var el = queue.Dequeue();
                    el.Visibility = Visibility.Visible;
                    UiPerf.Layout(this, "mods section " + el.Name);
                }
                if (queue.Count > 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Reveal);
                else _sectionsDirty = true;
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Reveal);
        }
        ShowUltra(tab == "ultra");
        QuickHost.Visibility = tab == "quick" ? Visibility.Visible : Visibility.Collapsed;
        NotesHost.Visibility = tab == "notes" ? Visibility.Visible : Visibility.Collapsed;
        // ReShade works on the game's files: without them (not installed) its section is the offline card, the others stay.
        if (mods && offline) ReShadeHost.Child = OfflineCard(GameTabs.Sections("mods")[0]);
        if (_data is not null && !_deferViews)
        {
            if (tab == "overview")
            {
                bool madeNow = _dashboard is null;
                MakeInfoView("overview");
                Place(OverviewHost, _dashboard);
                // Made with the click and on screen (not prepared unseen): its cards come in one per frame. One made ahead of
                // time was already laid out and drawn in the warm-up layer: it is only moved.
                if (madeNow && !Unseen) _dashboard?.RevealInTurns();
                if (_dashboard is not null) _dashboard.SectionsChanged = OnDashboardSections;
                _dashboard?.Show();
            }
            if (opti)
            {
                MakeInfoView("optiwiki");
                Place(OptiWikiHost, _optiWikiView);
            }
            if (opti && OptiNotesHost.Child is null) OptiNotesHost.Child = new OptiNotesView(() => _settings.Install);
            if (tab == "pcgw")
            {
                MakeInfoView("pcgw");
                Place(PcgwHost, _pcgwView);
            }
            if (tab == "links")
            {
                MakeInfoView("links");
                Place(LinksHost, _linksView);
            }
            if (mods)
            {
                // The sections are built together, the first time the tab opens; the installed mods are read again each time.
                if (!offline)
                {
                    _reshadeView ??= new ReShadeView(ModContext());
                    if (!ReferenceEquals(ReShadeHost.Child, _reshadeView)) ReShadeHost.Child = _reshadeView;
                }
                // The sections under ReShade, one per turn of the UI thread (2.0): built together they held it 150-550 ms on
                // the click. Asked for one of them by name (a link, a search hit), all at once so the jump finds it.
                // Built at once, each is refreshed below with the others; built later, by its own step.
                bool now = section is not null || App.SelfTesting;
                var steps = new List<Action>();
                if (_renoDxView is null) steps.Add(() => { RenoDxHost.Child = _renoDxView = new RenoDxView(ModContext()); if (!now) Ui.Safe(_renoDxView.Refresh); });
                if (_lumaView is null) steps.Add(() => { LumaHost.Child = _lumaView = new LumaView(ModContext()); if (!now) Ui.Safe(_lumaView.Refresh); });
                if (_dlss5View is null) steps.Add(() => Dlss5Host.Child = Dlss5Section());
                if (_dxvkView is null) steps.Add(() => { DxvkHost.Child = _dxvkView = new DxvkView(ModContext()); if (!now) Ui.Safe(_dxvkView.Refresh); });
                if (_vkd3dView is null) steps.Add(() => { Vkd3dHost.Child = _vkd3dView = new DxvkView(ModContext(), vkd3d: true); if (!now) Ui.Safe(_vkd3dView.Refresh); });
                if (now) foreach (var step in steps) step();
                else BuildInTurns(steps, _game);
            }
            if (tab == "ultra" && _ultra is null)
            {
                _ultra = new UltraPlusTab(ModContext());
                UltraInstallHost.Child = _ultra.Install;
                UltraAddonsHost.Child = _ultra.Addons;
                UltraSettingsHost.Child = _ultra.Settings;
                _ultra.SectionsChanged += () => ShowUltra(_tab == "ultra");
                ShowUltra(true);
            }
            if (tab == "quick" && _quickView is null) QuickHost.Child = _quickView = new QuickAccessView(ModContext());
            if (tab == "notes" && _notesView is null) NotesHost.Child = _notesView = new NotesView(ModContext());
            if (auto && !offline && _autoView is null) AutoHost.Child = _autoView = new AutoView(ModContext(), this);
            else if (auto && !offline) _autoView?.Refresh();
            // The Auto tab follows the folder as the Mods table does: a mod taken out anywhere (its tab, Explorer) shows at once.
            if (auto && !offline) WatchModsFolder(TargetDir);
        }
        if (mods)
        {
            // Each on its own: one failing must not leave the others showing an old state.
            if (!offline && _reshadeView is { } r) Ui.Safe(r.Refresh);
            if (_renoDxView is { } x) Ui.Safe(x.Refresh);
            if (_lumaView is { } l) Ui.Safe(l.Refresh);
            if (_dxvkView is { } d) Ui.Safe(d.Refresh);
            if (_vkd3dView is { } v) Ui.Safe(v.Refresh);
            // DLSS 5 reads OptiScaler and the game folder: what changed while another tab was open shows now.
            if (_dlss5Stale) RefreshDlss5();
            _ = RefreshModsOverviewAsync();
        }
        if (tab == "ultra") _ultra?.Refresh();
        // Not with the first frame of a game switch (_deferViews): reading and laying out OptiScaler.ini's settings held a game
        // with OptiScaler 150-300 ms before anything showed; the page's after-first-frame ShowTab loads it.
        if (opti && _iniStale && !offline && !_deferViews) using (UiPerf.Scope("GamePage.LoadIni")) LoadIni();
        if (tab == "ach" && _achStale) BuildAchievementTiles();
        if (opti && !offline && !_deferViews) _ = UpdateFootprintAsync();
        ShowSubTabs(section);
        PlaceToc();
        if (remember && AppState.Settings.GameTab != tab)
        {
            AppState.Settings.GameTab = tab;
            SettingsStore.Save();
        }
    }

    // ---------- sub-tabs: the sections of OptiScaler and of Mods ----------

    FrameworkElement[] _sectionEls = [];
    string[] _sectionIds = [];
    double[] _sectionTops = [];
    bool _sectionsDirty = true;
    string? _forcedSection;

    /// <summary>The element a section of the open tab is (its top is where the page scrolls to).</summary>
    FrameworkElement? SectionElement(string id) => (_tab, id) switch
    {
        ("opti", "install") => NeedsFiles("opti") ? OfflineHost : OptiTab,
        ("opti", "quickpanel") => NeedsFiles("opti") ? null : QuickPanelCard,
        ("opti", "settings") => IniCard,
        ("opti", "plugins") => NeedsFiles("opti") ? null : OptiPluginsGrid,
        ("opti", "wiki") => OptiWikiHost,
        ("opti", "notes") => OptiNotesHost,
        ("mods", "installed") => ModsOverviewHost,
        ("mods", "reshade") => ReShadeHost,
        ("mods", "renodx") => RenoDxHost,
        ("mods", "luma") => LumaHost,
        ("mods", "dxvk") => DxvkHost,
        ("mods", "vkd3d") => Vkd3dHost,
        ("mods", "dlss5") => Dlss5Host,
        ("ultra", "install") => UltraInstallHost,
        ("ultra", "addons") => UltraAddonsHost,
        ("ultra", "settings") => UltraSettingsHost,
        // The Dashboard's main sections (the trailer, the latest update, news...), inside its own view.
        ("overview", _) => _dashboard?.Section(id),
        _ => null,
    };

    /// <summary>The Ultra+ tab's sections: shown with their tab, the add-ons and settings only when they have something.</summary>
    void ShowUltra(bool on)
    {
        UltraInstallHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UltraAddonsHost.Visibility = on && _ultra?.AddonsShown == true ? Visibility.Visible : Visibility.Collapsed;
        UltraSettingsHost.Visibility = on && _ultra?.SettingsShown == true ? Visibility.Visible : Visibility.Collapsed;
    }

    readonly Dictionary<string, FrameworkElement> _separators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The separator above a section: a hairline with the section's icon and name on it, so the vertical scroll through a tab's
    /// sections reads as chapters. Made once, placed right before the section in its panel, shown while that section is.
    /// </summary>
    FrameworkElement? Separator(GameTab section)
    {
        // OptiScaler's Plugins & DLLs read as part of its install (2.0.0-rc.4): no line between them.
        if (_tab == "opti" && section.Id == "plugins") return null;
        if (SectionElement(section.Id) is not { } el) return null;
        // Before the page is loaded the sections in the XAML have no Parent yet (the tab shown on opening a game): the page's
        // own panel holds them, so the separators are there from the start, not only after another tab was opened.
        var panel = el.Parent as Panel ?? VisualTreeHelper.GetParent(el) as Panel ?? (Root.Children.Contains(el) ? Root : null);
        if (panel is null) return null;
        string key = $"{_tab}/{section.Id}";
        if (!_separators.TryGetValue(key, out var sep))
        {
            _separators[key] = sep = Ui.SectionRule(section.Tint);
            // As wide as the section it stands over (FitContent follows the window).
            sep.MaxWidth = el.MaxWidth;
        }
        // Asked of the panel, not of Parent (null too before the page is loaded): a separator is never added twice.
        int at = panel.Children.IndexOf(sep);
        if (at < 0) panel.Children.Insert(panel.Children.IndexOf(el), sep);
        else if (at != panel.Children.IndexOf(el) - 1)
        {
            panel.Children.Remove(sep);
            panel.Children.Insert(panel.Children.IndexOf(el), sep);
        }
        sep.Visibility = Visibility.Visible;
        return sep;
    }

    /// <summary>The sections of a tab (OptiScaler, ReShade &amp; Mods); PCGamingWiki has its own contents column instead.</summary>
    IEnumerable<GameTab> SectionsOf(string tab) => tab == "pcgw" ? [] : GameTabs.Sections(tab);

    /// <summary>Scrolls (animated) so the element's top sits just under the pinned tab bar (PCGamingWiki's contents).</summary>
    void ScrollToElement(FrameworkElement el, bool animated = true)
    {
        Root.UpdateLayout();
        double top = el.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        double target = Math.Max(TabTop(), top - StickyHeight - 12);
        Scroller.ChangeView(null, target, null, disableAnimation: !animated);
    }

    /// <summary>
    /// PCGamingWiki's contents column: in its place in the page while the tab row is too, then pinned under the tab bar (the
    /// same column, moved, as the tab row moves into the bar) so it stays in sight while the page is read.
    /// </summary>
    void PlaceToc()
    {
        if (_pcgwView is not { } view) return;
        bool pin = _stuck && _tab == "pcgw" && view.TocShown && PcgwHost.XamlRoot is not null;
        var toc = view.Toc;
        if (pin)
        {
            if (!ReferenceEquals(TocPin.Child, toc))
            {
                view.TocSlot.Child = null;
                TocPin.Child = toc;
            }
            var at = view.TocSlot.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
            double top = StickyBar.ActualHeight > 0 ? StickyBar.ActualHeight + 12 : StickyHeight + 12;
            TocPin.Margin = new Thickness(at.X, top, 0, 0);
            toc.MaxHeight = Math.Max(160, Scroller.ViewportHeight - top - 16);
            TocPin.Visibility = Visibility.Visible;
        }
        else
        {
            TocPin.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(TocPin.Child, toc))
            {
                TocPin.Child = null;
                view.TocSlot.Child = toc;
            }
            toc.MaxHeight = Math.Max(160, Scroller.ViewportHeight - StickyHeight - 28);
        }
    }

    /// <summary>The sub-tab bar of the open tab: one pill per section that is on the page (a hidden section has no pill).</summary>
    void ShowSubTabs(string? selected)
    {
        var shown = SectionsOf(_tab).Where(s => SectionElement(s.Id) is { Visibility: Visibility.Visible }).ToList();
        _sectionIds = shown.Select(s => s.Id).ToArray();
        // Every section after the first starts with a slim separator naming it; it is also where a sub-tab scrolls to.
        foreach (var sep in _separators.Values) sep.Visibility = Visibility.Collapsed;
        // PCGamingWiki's sections are cards with their own headings: no separator over them.
        // The Dashboard draws its own hairline over each section and has no sub-tab bar (rc.11: the user found it noise).
        bool own = _tab == "overview";
        if (own) shown = [];
        _sectionIds = shown.Select(s => s.Id).ToArray();
        _sectionEls = shown.Select((s, i) => i > 0 && !own && Separator(s) is { } sep ? sep : SectionElement(s.Id)!).ToArray();
        _sectionTops = new double[shown.Count];
        _sectionsDirty = true;
        _forcedSection = null;
        Tabs.SetSubTabs(shown, selected is not null && _sectionIds.Contains(selected) ? selected : shown.Count > 0 ? shown[0].Id : "");
        // The row's height changed with the sub bar: keep the pinned row's place in the page as tall as the row now is.
        if (_stuck) DispatcherQueue.TryEnqueue(() => TabHost.Height = Tabs.ActualHeight);
    }

    bool _subTabsQueued, _spyQueued;

    /// <summary>The Dashboard's main sections changed: its sub-tabs are laid out again (one came or went) or only measured again.</summary>
    void OnDashboardSections(DashboardView view, bool shown)
    {
        if (!ReferenceEquals(view, _dashboard) || _tab != "overview") return;
        _sectionsDirty = true;
        if (shown) QueueSubTabs(); else QueueSpy();
    }

    /// <summary>Checks the marked sub-tab again once the current layout pass is done.</summary>
    void QueueSpy()
    {
        if (_spyQueued) return;
        _spyQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _spyQueued = false;
            // As a scroll step would: a pill held for an animated jump stays held.
            UpdateSectionSpy(true);
        });
    }

    /// <summary>Lays the sub-tabs and separators out again once the current change is done (several sections change together).</summary>
    void QueueSubTabs()
    {
        if (_subTabsQueued) return;
        _subTabsQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _subTabsQueued = false;
            if (_game is not null) ShowSubTabs(Tabs.SelectedSub);
        });
    }

    /// <summary>The pinned bar's height: the tab row (with its sub-tabs) and the strip around it.</summary>
    double StickyHeight => Tabs.ActualHeight + 17;

    void MeasureSections()
    {
        for (int i = 0; i < _sectionEls.Length; i++)
            _sectionTops[i] = _sectionEls[i].TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        _sectionsDirty = false;
    }

    /// <summary>Scrolls (animated) so the section's top sits just under the pinned tab bar; the first section goes to the tab row.</summary>
    void ScrollToSection(string id, bool animated = true)
    {
        int i = Array.IndexOf(_sectionIds, id);
        if (i < 0)
        {
            ScrollToTabs();
            return;
        }
        Root.UpdateLayout();
        MeasureSections();
        double target = Math.Max(TabTop(), _sectionTops[i] - StickyHeight - 8);
        Tabs.SelectSub(id);
        // The pill stays on the section asked for while the page scrolls (a short last section may never reach the top).
        // Only for an animated scroll that actually moves: a jump (or no move at all) may never send the final ViewChanged that
        // releases it, and the spy would then ignore the user's first scroll.
        _forcedSection = animated && Math.Abs(Scroller.VerticalOffset - target) > 1 ? id : null;
        if (!Scroller.ChangeView(null, target, null, disableAnimation: !animated)) _forcedSection = null;
    }

    /// <summary>
    /// Scroll-spy: the sub-tab of the section at the top of the page is marked. Cheap enough for every scroll step: the sections'
    /// offsets are measured again only after a size change, and the pills are only touched when the section changes.
    /// </summary>
    void UpdateSectionSpy(bool intermediate)
    {
        if (_sectionIds.Length < 2) return;
        if (_forcedSection is not null)
        {
            if (!intermediate) _forcedSection = null;
            return;
        }
        if (_sectionsDirty) MeasureSections();
        double line = Scroller.VerticalOffset + StickyHeight + 24;
        int active = 0;
        for (int i = 1; i < _sectionTops.Length; i++)
            if (_sectionTops[i] <= line) active = i;
        // At the end of the page a short section never reaches the top: the one picked (a later one, on screen) stays marked
        // instead of going back to the one above it.
        int picked = Array.IndexOf(_sectionIds, Tabs.SelectedSub);
        if (picked > active && Scroller.VerticalOffset >= Scroller.ScrollableHeight - 2
            && _sectionTops[picked] <= Scroller.VerticalOffset + Scroller.ViewportHeight) return;
        Tabs.SelectSub(_sectionIds[active]);
    }

    /// <summary>A tab about the game's files, on a game not installed yet: what it will do, and Download (or the download's page).</summary>
    FrameworkElement OfflineCard(GameTab info)
    {
        var color = info.Tint;
        var body = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 560, Padding = new Thickness(24, 36, 24, 40) };
        body.Children.Add(new Border
        {
            Width = 56, Height = 48, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new FontIcon { Glyph = info.Glyph, FontSize = 36, Foreground = new SolidColorBrush(color) },
        });
        body.Children.Add(new TextBlock { Text = info.Name, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        body.Children.Add(new TextBlock
        {
            Text = (info is null ? "" : info.Info + ".\n") + L.F("It works on the game's files: ready as soon as {0} is installed.", _game?.Title),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.75,
        });
        bool downloading = _game is not null && Downloads.Active(_game.Id) is not null;
        var button = new Button
        {
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(18, 7, 18, 8),
            Content = downloading ? L.T("Show the download") : L.T("Download"),
        };
        button.Click += (_, _) => Download();
        body.Children.Add(button);
        return new Border { Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["CardStyle"], Child = body };
    }

    /// <summary>
    /// Everything the info tabs show is fetched as soon as the page opens, in the background (store data and both wikis already
    /// start with the page; here the reviews, HowLongToBeat, the RenoDX and Luma lists): switching tabs then shows them at once,
    /// from memory or from the disk cache.
    /// </summary>
    static void Prefetch(GameData data)
    {
        var ct = data.Token;
        _ = data.Hltb;
        _ = data.Reviews;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await data.Store is { } store) await SteamReviews.GetAsync(store.AppId, ct);
                await Core.Mods.RenoDx.GetModsAsync(ct: ct);
                await Core.Mods.Luma.GetModsAsync(ct: ct);
                if (!GameTabs.HiddenEverywhere("ultra"))
                {
                    await Core.Mods.UltraPlus.GetGamesAsync(ct: ct);
                    await Core.Mods.UltraPlus.GetModFilesAsync(ct: ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Prefetch for {data.Game.Name}: {ex.Message}"); }
        }, ct);
    }

    void UninstallGame_Click(object sender, RoutedEventArgs e)
    {
        if (_game is not null) _ = UninstallFlow.RunAsync(_game, XamlRoot, ActualTheme);
    }

    // ---------- banner links: the eye hides them all, a right click hides one ----------

    static readonly string[] LinkIds = ["nexus", "pcgw", "steamdb", "optiwiki"];
    bool _overLinks;

    Button LinkButton(string id) => id switch { "nexus" => NexusButton, "pcgw" => PcgwButton, "steamdb" => SteamDbButton, _ => OptiWikiButton };

    static string LinkName(string id) => id switch { "nexus" => "Nexus Mods", "pcgw" => "PCGamingWiki", "steamdb" => "SteamDB", _ => L.T("OptiScaler wiki") };

    void InitLinks()
    {
        foreach (var id in LinkIds)
        {
            var hide = new MenuFlyoutItem { Text = L.F("Hide {0}", LinkName(id)), Icon = new FontIcon { Glyph = "" } };
            hide.Click += (_, _) =>
            {
                AppState.Settings.HiddenBannerLinks.Add(id);
                SettingsStore.Save();
                UpdateLinks();
            };
            var menu = new MenuFlyout();
            menu.Items.Add(hide);
            LinkButton(id).ContextFlyout = menu;
        }
        // A right click on the eye picks the links one by one.
        var pick = new MenuFlyout();
        pick.Opening += (_, _) =>
        {
            pick.Items.Clear();
            foreach (var id in LinkIds)
            {
                if (id == "steamdb" && _steamAppId is null) continue;
                var item = new ToggleMenuFlyoutItem { Text = LinkName(id), IsChecked = !AppState.Settings.HideBannerLinks && !AppState.Settings.HiddenBannerLinks.Contains(id) };
                item.Click += (_, _) =>
                {
                    var s = AppState.Settings;
                    if (s.HideBannerLinks)
                    {
                        // Coming back from "all hidden": only the one picked shows.
                        s.HideBannerLinks = false;
                        foreach (var other in LinkIds) s.HiddenBannerLinks.Add(other);
                    }
                    if (item.IsChecked) s.HiddenBannerLinks.Remove(id);
                    else s.HiddenBannerLinks.Add(id);
                    SettingsStore.Save();
                    UpdateLinks();
                };
                pick.Items.Add(item);
            }
        };
        LinksToggle.ContextFlyout = pick;
    }

    bool AnyLinkShown() => !AppState.Settings.HideBannerLinks
        && LinkIds.Any(id => !AppState.Settings.HiddenBannerLinks.Contains(id) && (id != "steamdb" || _steamAppId is not null));

    void UpdateLinks()
    {
        var s = AppState.Settings;
        foreach (var id in LinkIds)
            LinkButton(id).Visibility = !s.HiddenBannerLinks.Contains(id) && (id != "steamdb" || _steamAppId is not null) ? Visibility.Visible : Visibility.Collapsed;
        bool shown = AnyLinkShown();
        LinkPills.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        // Hidden as a part of the page (for this game or every game): the corner and its eye go too.
        LinkArea.Visibility = Part(PageParts.Links) ? Visibility.Visible : Visibility.Collapsed;
        LinksToggleIcon.Glyph = shown ? "" : "";
        var tip = shown ? L.T("Hide the links (right click: choose which ones show)") : L.T("Show the links");
        ToolTipService.SetToolTip(LinksToggle, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LinksToggle, tip);
    }

    void LinksToggle_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        if (AnyLinkShown()) s.HideBannerLinks = true;
        else
        {
            s.HideBannerLinks = false;
            s.HiddenBannerLinks.Clear();
        }
        SettingsStore.Save();
        UpdateLinks();
    }

    void LinkArea_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _overLinks = true;
        LinksToggle.Opacity = 1;
    }

    void LinkArea_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _overLinks = false;
        if (LinksToggle.FocusState != FocusState.Keyboard) LinksToggle.Opacity = 0;
    }

    ModContext ModContext()
    {
        var game = _game!;
        return new()
    {
        Game = game,
        Settings = _settings,
        Data = _data!,
        TargetDir = () => TargetDir,
        // The page's token at the time of each read: Resume makes a new one (2.0.0-rc.12). Another game's: cancelled.
        TokenOf = () => _game == game ? _pageCts.Token : new CancellationToken(true),
        OptiChanged = () =>
        {
            _iniStale = true;
            Refresh();
            // RenoDX and Luma show how ReShade is loaded too: redrawn with the ReShade section, never left stale.
            if (_renoDxView is { } x) Ui.Safe(x.Refresh);
            if (_lumaView is { } l) Ui.Safe(l.Refresh);
            if (_dxvkView is { } d) Ui.Safe(d.Refresh);
            if (_vkd3dView is { } v) Ui.Safe(v.Refresh);
            // The banner's mod tags and the mod check follow every change to the folder.
            _ = UpdateModBadgesAsync();
            // Ultra+ reads OptiScaler's file name (both load through a proxy dll beside the exe).
            _ultra?.Refresh();
        },
        ShowTab = OpenTab,
        ReinstallOpti = () => _ = InstallAsync(SelectedRelease, manual: false),
        SteamAppId = () => _steamAppId,
    };
    }

    // ---------- the tab row stays on top while scrolling ----------

    bool _stuck;

    /// <summary>Where the tab row sits in the page (its top, from the top of the scrolled content).</summary>
    double TabTop() => TabHost.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

    void Scroller_ViewChanged(ScrollView sender, object e)
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateSticky();
        UpdateSectionSpy(intermediate: true);
        if (_tab == "pcgw") _pcgwView?.UpdateSpy(Root, Scroller.VerticalOffset + StickyHeight + 32);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        ScrollWorkMs += ms;
        ScrollWorkMax = Math.Max(ScrollWorkMax, ms);
    }

    /// <summary>The arrow on the pinned tab bar: back to the top of the page, the banner, with a glide.</summary>
    void TopButton_Click(object sender, RoutedEventArgs e) => Scroller.ChangeView(null, 0, null, disableAnimation: false);

    /// <summary>A scroll (a jump to a section, a glide) has ended: the section marker follows the page again.</summary>
    void Scroller_ScrollCompleted(ScrollView sender, ScrollingScrollCompletedEventArgs e) => UpdateSectionSpy(intermediate: false);

    /// <summary>Self-test (scroll smoothness): the time spent in the page's own scroll handler, in total and at worst.</summary>
    internal static double ScrollWorkMs, ScrollWorkMax;

    /// <summary>
    /// Past the tab row, the same row moves into the bar pinned to the top of the page (its place keeps its height, so nothing
    /// jumps); scrolled back, it returns. Moved only when crossing the line, never redrawn while scrolling.
    /// </summary>
    void UpdateSticky()
    {
        if (TabHost.XamlRoot is null) return;
        bool stick = Scroller.VerticalOffset > TabTop() + 4;
        if (stick == _stuck) return;
        _stuck = stick;
        if (stick)
        {
            TabHost.Height = TabHost.ActualHeight;
            TabHost.Child = null;
            StickySlot.Child = Tabs;
            StickyBar.Visibility = Visibility.Visible;
        }
        else
        {
            StickySlot.Child = null;
            StickyBar.Visibility = Visibility.Collapsed;
            TabHost.Child = Tabs;
            TabHost.Height = double.NaN;
        }
        if (_tab == "pcgw") PlaceToc();
    }

    /// <summary>
    /// The bar has its real height only after the layout that follows pinning it: until then the contents column was placed
    /// with no height at all and slid under the bar. Placed again once the bar is measured.
    /// </summary>
    void StickyBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_tab == "pcgw") PlaceToc();
    }

    /// <summary>Brings the tab row to the top of the page (after opening a tab from elsewhere on the page).</summary>
    void ScrollToTabs()
    {
        double top = TabTop();
        if (Math.Abs(Scroller.VerticalOffset - top) > 4) Scroller.ChangeView(null, top, null);
    }

    /// <summary>
    /// Self-test hook (HybridLauncher.exe --selftest): shows a tab ("opti"), or a section of one ("opti/settings", scrolled to without
    /// animation), and for the settings section one of its views.
    /// </summary>
    /// <summary>Self-test: what the DXVK and VKD3D-Proton sections show ("loaded …" once they have their releases, "folded: …"
    /// for a game of the other Direct3D), or why not.</summary>
    /// <summary>Self-test: the Auto tab's plan (see AutoView.SelfTestDescribe), or "no Auto tab".</summary>
    internal string SelfTestAuto() => _autoView?.SelfTestDescribe() ?? "no Auto tab";
    internal int SelfTestAutoAddBuildMenu(bool open) => _autoView?.SelfTestAddBuildMenu(open) ?? -1;

    /// <summary>Self-test: the Auto tab's one click with ReShade ticked (fake games only: it installs for real).</summary>
    internal string SelfTestAutoDlss5(string way) => _autoView?.SelfTestDlss5(way) ?? "no Auto tab";

    internal string SelfTestAutoDlss5Tick() => _autoView?.SelfTestDlss5Tick() ?? "no Auto tab";

    internal string SelfTestAutoReShadeBuild(bool? addon) => _autoView?.SelfTestReShadeBuild(addon) ?? "no Auto tab";

    internal Task SelfTestAutoRunAsync() => _autoView?.SelfTestRunAsync() ?? Task.CompletedTask;

    /// <summary>Self-test: the game folder (where the Auto tab installed).</summary>
    internal string? SelfTestTargetDir => TargetDir;

    internal string? SelfTestDxvk() => $"DXVK {_dxvkView?.SelfTestDescribe() ?? "none"} || VKD3D-Proton {_vkd3dView?.SelfTestDescribe() ?? "none"}";

    /// <summary>
    /// The game folder a self-test step may write in: only a fake game's, inside the test's own data folder (HL_DATA). A real
    /// game found by a launcher scan is never written to (rc.18: a run without -FakeGame put a sample OptiScaler over the
    /// user's own install); the step fails instead.
    /// </summary>
    string SelfTestFolder()
    {
        if (TargetDir is not { } dir) throw new InvalidOperationException("no game folder");
        string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        if (!AppInfo.SeparateData || !Full(dir).StartsWith(Full(AppInfo.DataRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refused: {dir} is not a fake game inside the test data folder (run the self-test with -FakeGame)");
        return dir;
    }

    /// <summary>Self-test: puts a RenoDX add-on with no ReShade in the game folder (or takes it out) and checks the banner again.</summary>
    internal void SelfTestModProblem(bool on)
    {
        var dir = SelfTestFolder();
        var file = Path.Combine(dir, "renodx-selftest.addon64");
        if (on) File.WriteAllText(file, "self-test");
        else if (File.Exists(file)) File.Delete(file);
        _ = UpdateModBadgesAsync();
    }

    /// <summary>
    /// Self-test (HL_SELFTEST_OPTI = an OptiScaler.dll): puts it in the game folder as dxgi.dll with an install record, as an
    /// install would, then shows the file name menu of the status chip.
    /// </summary>
    internal void SelfTestDllSwapMenu(string optiDll)
    {
        var dir = SelfTestFolder();
        var dll = Path.Combine(dir, "dxgi.dll");
        File.Copy(optiDll, dll, true);
        _settings.Install = new InstallManifest { TargetDir = dir, DllName = "dxgi.dll", Tag = "self-test", Channel = OptiChannel.Stable, InstalledAt = DateTime.Now };
        _settings.Install.Files["dxgi.dll"] = OptiInstaller.Hash(dll);
        _settings.Setup.DllName = "dxgi.dll";
        SettingsStore.Save();
        ShowTab("opti", remember: false, section: "install");
        UpdateOptiStatus();
        ShowDllSwapMenu();
    }

    /// <summary>Self-test: swaps to <paramref name="name"/> and says what the folder and the chip show then.</summary>
    internal async Task<string> SelfTestDllSwapAsync(string name)
    {
        SelfTestFolder();
        await SwapDllAsync(name);
        return $"{_settings.Install?.DllName} chip={OptiDllText.Text} found={(TargetDir is { } d ? OptiDetector.Detect(d)?.FileName : null)} setup={_settings.Setup.DllName}";
    }

    /// <summary>Self-test: the texts of the banner's mod tags and warnings, in order.</summary>
    internal string SelfTestBadges() => string.Join(" | ", ModBadges.Children.OfType<Border>()
        .Select(b => string.Join(" ", (b.Child as Panel)?.Children.OfType<TextBlock>().Select(t => t.Text) ?? []) + (b.Tag is string all ? $" ({all})" : "")));

    /// <summary>Demo walk (MainWindow.Demo): a tab or a tab's section, scrolled to smoothly; nothing else changes.</summary>
    internal void DemoShow(string tab)
    {
        var parts = tab.Split('/');
        ShowTab(parts[0], remember: false, section: parts.Length > 1 ? parts[1] : null);
        if (parts.Length > 1) ScrollToSection(parts[1], animated: true);
    }

    /// <summary>Demo walk: one of the page's named parts (its x:Name), for the recording's camera and pointer.</summary>
    internal FrameworkElement? DemoPart(string name) => FindName(name) as FrameworkElement;

    /// <summary>Demo walk: a tab's button in the tab bar (the whole bar when it has none by that id).</summary>
    internal FrameworkElement? DemoTabButton(string id) => Tabs.DemoButton(id) ?? (FrameworkElement)Tabs;

    /// <summary>Demo walk: picks an OptiScaler source card, as a click on it would.</summary>
    internal void DemoChannel(OptiChannel channel) => ChannelCard(channel).IsChecked = true;

    /// <summary>Demo walk: opens or folds Setup options.</summary>
    internal void DemoSetup(bool open) => SetupExpander.IsExpanded = open;

    /// <summary>Demo walk: Install and Uninstall, as their buttons do (the confirmations are answered by MainWindow.DemoAnswerAfter).</summary>
    internal void DemoInstallOpti() => StartInstall(manual: false);
    internal void DemoUninstallOpti() => Uninstall_Click(UninstallButton, new RoutedEventArgs());

    /// <summary>Demo walk: a change runs in this game's folder.</summary>
    internal bool DemoBusy => _busy || GameJobs.Busy(_game?.Id);

    /// <summary>Demo walk: OptiScaler is in through Hybrid Launcher.</summary>
    internal bool DemoOptiInstalled => _settings.Install is not null;

    /// <summary>Demo walk: where the open tab starts in the page's scroll.</summary>
    internal double DemoTabTop() => Scroller.VerticalOffset + TabTop();

    /// <summary>Demo walk: the game's rarest unlocked achievement as a pop-up in <paramref name="look"/> (null: none read).</summary>
    internal Toast? DemoToast(string look)
    {
        if (_ach is not { } set || _game is not { } game) return null;
        var a = set.Items.Where(i => i.Unlocked).OrderBy(i => i.GlobalPercent ?? 100).FirstOrDefault() ?? set.Items.FirstOrDefault();
        return a is null ? null : new Toast(a.Name, a.Description, a.IconUrl, game.Name, a.GlobalPercent, game.Store, a.Points, look, GameId: game.Id, AchievementKey: a.Key);
    }

    public void SelfTestShow(string tab, string? iniView = null)
    {
        var parts = tab.Split('/');
        ShowTab(parts[0], remember: false, section: parts.Length > 1 ? parts[1] : null);
        if (parts.Length > 1 && parts[0] == "pcgw") { if (_pcgwView?.SectionElement(parts[1]) is { } el) ScrollToElement(el, animated: false); }
        else if (parts.Length > 1) ScrollToSection(parts[1], animated: false);
        if (iniView is not null) SelfTestIni(iniView);
        // Every source once, then back to the installed one (as the page opens on it).
        foreach (var card in new[] { CustomCard, ForkCard, NightlyCard, StableCard }) card.IsChecked = true;
        ChannelCard(_settings.Install?.Channel ?? OptiChannel.Stable).IsChecked = true;
    }

    /// <summary>Self-test hook: scrolls past the tab row (it moves to the top bar) or back to the top.</summary>
    /// <summary>Self-test: scrolls the Dashboard to its side cards (under the main column on a narrow window).</summary>
    internal void SelfTestScrollToDashboardSide() { if (_dashboard?.SelfTestSide is { } side) ScrollToElement(side, animated: false); }

    /// <summary>Self-test: scrolls the open tab to a place below its top (pictures of a long tab, screen by screen).</summary>
    internal void SelfTestScrollBelow(double below) => Scroller.ChangeView(null, TabTop() + below, null, disableAnimation: true);

    public void SelfTestScroll(bool down) => Scroller.ChangeView(null, down ? TabTop() + (double.TryParse(Environment.GetEnvironmentVariable("HL_SELFTEST_SCROLL"), out var by) ? by : 400) : 0, null, disableAnimation: true);

    async void Store_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        var game = _game;
        try
        {
            if (await StoreLinks.OpenAsync(game, _steamAppId, _pageCts.Token) is null)
                App.Window?.ShowInfo(L.F("{0} was added by hand and no store page was found for it.", game.Title));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
    }

    // ---------- layout ----------

    double _laidOutWidth = -1;

    void ApplyLayout(double width)
    {
        // Height changes (tabs, expanders) also raise SizeChanged: only a new width needs a new layout.
        if (Math.Abs(width - _laidOutWidth) < 0.5) return;
        _laidOutWidth = width;

        bool narrow = width < 760;
        // Steam's library art is about 3.1:1. The launch bar sits on top of the art, so the banner keeps the art's
        // shape instead of being cut to a strip; it stops growing on very wide windows.
        Hero.Height = Math.Clamp(width / 3.1, narrow ? 380 : 340, 640);
        LogoImage.MaxHeight = Math.Clamp(Hero.Height * 0.42, 100, 250);
        LogoImage.MaxWidth = Math.Clamp(width * 0.5, 280, 760);
        PcgwLabel.Visibility = OptiWikiLabel.Visibility = NexusLabel.Visibility = SteamDbLabel.Visibility = width < 900 ? Visibility.Collapsed : Visibility.Visible;
        FolderLabel.Visibility = SizeText.Visibility = width < 560 ? Visibility.Collapsed : Visibility.Visible;
        AchButtonText.Visibility = width < 560 ? Visibility.Collapsed : Visibility.Visible;
        SaveFolderLabel.Visibility = width < 1400 ? Visibility.Collapsed : Visibility.Visible;
        ConfigFolderLabel.Visibility = width < 1600 ? Visibility.Collapsed : Visibility.Visible;

        // Launch bar: arguments get their own full row when narrow; the play stats go first when space runs out.
        _narrowBar = narrow;
        UpdateDownloadPanel(_game is null ? null : Downloads.Active(_game.Id));
        LayoutArgsBox();

        // OptiScaler beside the ASI plugins on wide windows, the plugins below it otherwise.
        // The side column takes about a quarter of the page, so the install card does not stretch into a long thin strip.
        // The tool tabs take most of the page, centred: their own layout follows that width, not the page's.
        double page = width;
        FitContent(width - 40);
        width = Math.Min(width, _contentMax + 40);
        bool stackTop = width < 1100;
        double side = Math.Clamp(Math.Round(width * 0.27), 360, 600);
        OptiTab.ColumnDefinitions[1].Width = stackTop ? new GridLength(0) : new GridLength(side);
        OptiTab.ColumnSpacing = stackTop ? 0 : 16;
        Grid.SetRow(SideColumn, stackTop ? 1 : 0);
        Grid.SetColumn(SideColumn, stackTop ? 0 : 1);
        // Plugins &amp; DLLs: side by side, one above the other on narrow windows.
        bool stackPlugins = width < 1000;
        OptiPluginsGrid.ColumnDefinitions[1].Width = stackPlugins ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        OptiPluginsGrid.ColumnSpacing = stackPlugins ? 0 : 12;
        Grid.SetRow(CustomDllCard, stackPlugins ? 1 : 0);
        Grid.SetColumn(CustomDllCard, stackPlugins ? 0 : 1);

        // Version step: channel switch above the version list when narrow.
        // Install card: the channel cards stack and the version tools go under the list when the card is narrow;
        // the ini / uninstall buttons drop to a second row.
        double card = (stackTop ? width : width - side - 16) - 40;
        bool narrowCard = card < 640;
        // In one row the forks' card gets more of it: its name is the longest and was cut on a card of reading width.
        for (int i = 0; i < 4; i++) ChannelCards.ColumnDefinitions[i].Width = new GridLength(i == 2 && !narrowCard ? 1.45 : 1, GridUnitType.Star);
        ChannelCards.ColumnSpacing = narrowCard ? 0 : 8;
        if (ChannelCards.RowDefinitions.Count < 2)
            for (int i = ChannelCards.RowDefinitions.Count; i < 2; i++) ChannelCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Two by two when the card is narrow, one row of four otherwise.
        RadioButton[] cards = [StableCard, NightlyCard, ForkCard, CustomCard];
        for (int i = 0; i < 4; i++)
        {
            Grid.SetRow(cards[i], narrowCard ? i / 2 : 0);
            Grid.SetColumn(cards[i], narrowCard ? i % 2 : i);
        }
        Grid.SetRow(ChannelTools, narrowCard ? 1 : 0);
        Grid.SetColumn(ChannelTools, narrowCard ? 0 : 1);
        ChannelTools.HorizontalAlignment = narrowCard ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Grid.SetColumnSpan(VersionCombo, narrowCard ? 2 : 1);
        Grid.SetRow(ChannelHint, narrowCard ? 2 : 1);
        bool narrowActions = card < 760;
        Grid.SetRow(OpenIniButton, narrowActions ? 1 : 0);
        Grid.SetRow(UninstallButton, narrowActions ? 1 : 0);
        Grid.SetColumn(OpenIniButton, narrowActions ? 0 : 3);
        Grid.SetColumn(UninstallButton, narrowActions ? 1 : 4);

        LayoutIni(width);

        width = page;
        _achColumns = Math.Max(1, (int)((width - 40) / 340));
        if (_achColumns != _achLaidOutColumns) LayoutAchievements();
    }

    // ---------- banner ----------

    async Task LoadArtAsync(CancellationToken ct)
    {
        var game = _game!;
        _steamAppId = game.SteamAppId;
        var art = await _data!.Art;
        if (ct.IsCancellationRequested || game != _game) return;

        _steamAppId = art.SteamAppId ?? _steamAppId;
        UpdateLinks();
        _ = LoadApiAsync(ct);
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(art.Developer)) meta.Add(art.Developer!.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(art.ReleaseDate)) meta.Add(art.ReleaseDate!.ToUpperInvariant());
        MetaText.Text = string.Join("  ·  ", meta);
        ToolTipService.SetToolTip(StoreButton, StoreLinks.Describe(game, _steamAppId));

        if (art.HeroImage is not null)
        {
            try
            {
                // Steam heroes are 3840 px wide. Decoded once at the widest the banner can ever be drawn on this PC (the widest
                // screen, or its tallest crop), never below it: the page's width when the art arrives can be far smaller than the
                // window will be, and an upscaled bitmap looks blurry.
                // Shown before in this session: the decoded picture is still in memory, on screen at once with no fade.
                if (ArtCache.Get(art.HeroImage) is BitmapImage known)
                {
                    HeroImage.Source = known;
                    HeroImage.Opacity = 1;
                }
                else
                {
                    var hero = new BitmapImage { DecodePixelWidth = HeroDecodeWidth(), DecodePixelType = DecodePixelType.Physical, UriSource = new Uri(art.HeroImage) };
                    ArtCache.Put(art.HeroImage, hero);
                    HeroImage.Source = hero;
                    FadeIn(HeroImage);
                }
                _heroUri = art.HeroImage;
                UpdateFloor();
            }
            catch (Exception ex) { Log.Warn($"Hero image failed: {ex.Message}"); }
        }

        // The title logo replaces the plain title once it has loaded; a broken file keeps the text.
        if (art.LogoImage is not null && ArtCache.Get(art.LogoImage) is BitmapImage knownLogo && ArtCache.Opened(art.LogoImage))
        {
            // A logo shown before: in place at once, no fade.
            LogoImage.Source = knownLogo;
            LogoImage.Opacity = 1;
            LogoImage.Visibility = Visibility.Visible;
            TitleText.Visibility = Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LogoImage, game.Title);
            ApplyLayout(Root.ActualWidth);
        }
        else if (art.LogoImage is not null)
        {
            try
            {
                var logo = new BitmapImage();
                var logoUri = art.LogoImage;
                ArtCache.Put(logoUri, logo);
                // The picture is shared (ArtCache) and outlives this page: its handlers reach the page weakly and go once it has
                // loaded (2.0.0-rc.12; a handler using the page held every page that ever loaded a logo).
                var page = new WeakReference<GamePage>(this);
                RoutedEventHandler? opened = null;
                ExceptionRoutedEventHandler? failed = null;
                opened = (_, _) =>
                {
                    logo.ImageOpened -= opened;
                    logo.ImageFailed -= failed;
                    ArtCache.MarkOpened(logoUri);
                    if (page.TryGetTarget(out var p)) p.ShowLogo(game);
                };
                failed = (_, e) =>
                {
                    logo.ImageOpened -= opened;
                    logo.ImageFailed -= failed;
                    Log.Warn($"Logo for {game.Name} failed: {e.ErrorMessage}");
                };
                logo.ImageOpened += opened;
                logo.ImageFailed += failed;
                logo.UriSource = new Uri(art.LogoImage);
                LogoImage.Source = logo;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LogoImage, game.Title);
            }
            catch (Exception ex) { Log.Warn($"Logo image failed: {ex.Message}"); }
        }
    }

    void ShowLogo(Game game)
    {
        if (game != _game) return;
        LogoImage.Visibility = Visibility.Visible;
        TitleText.Visibility = Visibility.Collapsed;
        ApplyLayout(Root.ActualWidth);
        FadeIn(LogoImage);
    }

    /// <summary>Physical pixels: the widest display, or the banner's tallest height (640) at the art's 3.1:1, whichever is more.</summary>
    int HeroDecodeWidth()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        double widest = 0;
        try
        {
            // Indexed, not foreach: enumerating this list directly is known to throw in some C#/WinRT versions.
            var areas = Microsoft.UI.Windowing.DisplayArea.FindAll();
            for (int i = 0; i < areas.Count; i++) widest = Math.Max(widest, areas[i].OuterBounds.Width);
        }
        catch (Exception ex) { Log.Warn($"Display sizes: {ex.Message}"); }
        if (widest <= 0) widest = 2560;
        return (int)Math.Clamp(Math.Max(widest, 640 * 3.1 * scale), 1280, 3840);
    }

    static void FadeIn(UIElement element)
    {
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(350) };
        var sb = new Storyboard();
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Begin();
    }

    /// <summary>The graphics API badge next to the store: PCGamingWiki first, the exe's imports when the wiki has nothing.</summary>
    async Task LoadApiAsync(CancellationToken ct)
    {
        var game = _game!;
        ApiBadge.Visibility = Visibility.Collapsed;
        try
        {
            var info = await GraphicsApi.GetAsync(game, GameScanner.EffectiveExe(game, _settings), _steamAppId, ct);
            if (ct.IsCancellationRequested || game != _game || !info.IsKnown) return;
            ApiText.Text = info.Display.ToUpperInvariant();
            ToolTipService.SetToolTip(ApiBadge, L.F("Graphics API: {0}", info.Display) + "\n" + L.F("Source: {0}", L.T(info.Source)));
            ApiBadge.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Graphics API for {game.Name}: {ex.Message}"); }
    }

    void Pcgw_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenUri(GameInfo.PcGamingWikiUrl(_game!, _steamAppId)));

    async void OptiWiki_Click(object sender, RoutedEventArgs e)
    {
        try { GameLauncher.OpenUri(await CompatWiki.PageForAsync(GameNames.PlainOrClean(GameNames.LookupName(_game!)))); }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not open the wiki"), ex.Message, ex); }
    }

    async void Nexus_Click(object sender, RoutedEventArgs e)
    {
        try { GameLauncher.OpenUri(await NexusMods.PageForAsync(GameNames.PlainOrClean(GameNames.LookupName(_game!)))); }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not open Nexus Mods"), ex.Message, ex); }
    }

    void SteamDb_Click(object sender, RoutedEventArgs e)
    {
        if (_steamAppId is { } id) Try(() => GameLauncher.OpenUri($"https://steamdb.info/app/{id}/"));
    }

    static async Task<StoreDetails?> StoreAsync(Task<GameArt> art, CancellationToken ct) =>
        (await art).SteamAppId is { } id ? await StoreDetails.GetAsync(id, ct) : null;

    // Wiki lookups need the Steam app id the art lookup finds for games from other stores.
    static async Task<PcgwInfo?> PcgwAsync(Game game, Task<GameArt> art, CancellationToken ct) =>
        await Pcgw.GetAsync(game, (await art).SteamAppId ?? game.SteamAppId, ct);

    /// <summary>
    /// From PCGamingWiki: the upscaler and frame generation tags on the banner, and the settings / save folders next to
    /// "Game folder" (only the ones that exist on this PC).
    /// </summary>
    async Task LoadWikiExtrasAsync(CancellationToken ct)
    {
        var game = _game;
        if (_data is null || game is null) return;
        try
        {
            var wiki = await _data.Pcgw;
            if (ct.IsCancellationRequested || game != _game || wiki is null) return;

            // The versions the game's own libraries carry win over the wiki's ("DLSS 3.7", "FSR 4.1").
            var installDir = game.InstallDir;
            var files = await Task.Run(() => UpscalerFiles.Scan(installDir), ct);
            if (ct.IsCancellationRequested || game != _game) return;
            TechBadges.Children.Clear();
            var tags = UpscalerFiles.Merge(Pcgw.UpscalerTags(string.Join(", ", wiki.UpscalingTech), wiki.Apis).Concat(Pcgw.FrameGenTags(wiki.FrameGenTech)), files);
            // A maker's upscaler and frame generation share one chip, with the exact versions of the game's libraries.
            foreach (var g in UpscalerFiles.Group(tags, files)) TechBadges.Children.Add(TechTags.CreateGroup(g, fontSize: 12));
            TechBadges.Visibility = TechBadges.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(TechBadges, (files.Count > 0 ? L.T("From the game's files") + "\n" + UpscalerFiles.Describe(files) + "\n\n" : "") + L.T("From PCGamingWiki") +
                (wiki.UpscalingTech.Count > 0 ? "\n" + L.F("Upscaling: {0}", string.Join(", ", wiki.UpscalingTech)) : "") +
                (wiki.FrameGenTech.Count > 0 ? "\n" + L.F("Frame generation: {0}", string.Join(", ", wiki.FrameGenTech)) : ""));

            var store = game.Store;
            var (config, saves) = await Task.Run(() => (Pcgw.ExistingFolder(wiki.ConfigPaths, store), Pcgw.ExistingFolder(wiki.SavePaths, store)), ct);
            if (ct.IsCancellationRequested || game != _game) return;
            _configDir = config;
            _saveDir = saves;
            ApplyPageParts();
            ToolTipService.SetToolTip(ConfigFolderButton, L.T("Open the game's configuration folder (from PCGamingWiki)") + "\n" + config);
            ToolTipService.SetToolTip(SaveFolderButton, L.T("Open the game's saves folder (from PCGamingWiki)") + "\n" + saves);

            // Their size on disk, like the game folder's (saves can grow large).
            foreach (var (dir, text) in new[] { (config, ConfigSizeText), (saves, SaveSizeText) })
            {
                if (dir is null) continue;
                text.Text = L.T("Measuring…");
                var bytes = await Task.Run(() => DiskUsage.FolderSize(dir, ct), ct);
                if (ct.IsCancellationRequested || game != _game) return;
                text.Text = DiskUsage.Format(bytes);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Wiki extras for {game.Name}: {ex.Message}"); }
    }

    void ConfigFolder_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenFolder(_configDir!));
    void SaveFolder_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenFolder(_saveDir!));

    void OptiGithub_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenUri("https://github.com/optiscaler/OptiScaler"));

    /// <summary>The test builds channel of the OptiScaler Discord: in the Discord app when it is installed, in the browser otherwise.</summary>
    void OptiDiscord_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        const string channel = "channels/995299945492008990/1371201835477499975";
        GameLauncher.OpenUri(GameLauncher.HasProtocol("discord") ? $"discord://-/{channel}" : $"https://discord.com/{channel}");
    });

    // ---------- the floor under the banner ----------

    string? _heroUri, _floorKey;
    CancellationTokenSource? _floorCts;

    /// <summary>
    /// The mirror under the art (see <see cref="FloorMirror"/>), made again when the art or the banner's shape changes. With
    /// hardware acceleration off only the dark fade stays.
    /// </summary>
    void UpdateFloor()
    {
        if (!MediaPolicy.Effects || _heroUri is null || Hero.ActualWidth <= 0)
        {
            _floorCts?.Cancel();
            FloorImage.Source = null;
            _floorKey = null;
            return;
        }
        // The banner's shape in steps of 0.05: a window being resized does not rebuild it for every pixel.
        double aspect = Math.Round(Hero.ActualWidth / Hero.Height / 0.05) * 0.05;
        string key = $"{_heroUri}|{aspect:0.00}";
        if (key == _floorKey) return;
        _floorKey = key;
        _floorCts?.Cancel();
        var cts = _floorCts = new CancellationTokenSource();
        _ = BuildFloorAsync(_heroUri, aspect, Floor.Height / Hero.Height, cts.Token);
    }

    async Task BuildFloorAsync(string uri, double aspect, double fraction, CancellationToken ct)
    {
        try
        {
            // The mirror is a blur worked out on the CPU: kept with the banner's other pictures for the next visit.
            string key = $"floor|{uri}|{aspect:0.00}|{fraction:0.000}";
            if (ArtCache.Get(key) is ImageSource known) { FloorImage.Source = known; return; }
            var bitmap = await FloorMirror.CreateAsync(uri, aspect, fraction, ct);
            if (bitmap is not null) ArtCache.Put(key, bitmap);
            if (!ct.IsCancellationRequested) FloorImage.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Floor mirror: {ex.Message}"); }
    }

    /// <summary>Puts the floor right under the banner's bottom edge, as wide as the banner.</summary>
    void PlaceFloor()
    {
        if (Hero.ActualWidth <= 0) return;
        try
        {
            var at = Hero.TransformToVisual(PageLayers).TransformPoint(new Windows.Foundation.Point(0, Hero.ActualHeight));
            Canvas.SetLeft(Floor, at.X);
            Canvas.SetTop(Floor, at.Y);
            Floor.Width = Hero.ActualWidth;
            UpdateFloor();
        }
        catch (Exception ex) { Log.Warn($"Floor place: {ex.Message}"); }
    }

    // ---------- launch bar ----------

    /// <summary>The controller's LB / RB: the tab before or after the open one (round).</summary>
    public void StepTab(int by)
    {
        var tabs = VisibleTabs();
        if (tabs.Count == 0) return;
        // From the Dashboard, LB / RB go to the tabs, on the one that was open.
        int i = Math.Max(0, tabs.FindIndex(t => t.Id == _tab));
        ShowTab(tabs[((i + by) % tabs.Count + tabs.Count) % tabs.Count].Id);
    }

    /// <summary>The controller's Y: Play (or Download, or Stop), as the launch bar's button.</summary>
    public void PlayFromPad() => PlayOrStop();

    /// <summary>Play starts the game (through its launcher); while it runs the same button closes it.</summary>
    void PlayOrStop()
    {
        if (_game is null) return;
        if (_game.NotInstalled || _game.Installing) Download();
        // Starting too: the button shows a stop square then, and a click closes the game before (or as) it comes up.
        else if (GameSessions.State(_game.Id) is GameRunState.Running or GameRunState.Starting) _ = StopGameAsync();
        else Play();
    }

    /// <summary>Not installed: asks the launcher to install it. Already downloading: shows it in Downloads.</summary>
    void Download()
    {
        if (_game is null) return;
        if (Downloads.Active(_game.Id) is not null)
        {
            App.Window?.OpenDownloads();
            return;
        }
        var owned = OwnedLibrary.Find(_game.Id) ?? (_game.SteamAppId is { } app
            ? new OwnedGame(GameStore.Steam, _game.Id, _game.Name, null, null, $"steam://install/{app}", $"https://store.steampowered.com/app/{app}")
            : null);
        if (owned is null)
        {
            App.Window?.ShowError(L.T("Could not open the launcher"), L.F("Install {0} from {1}.", _game.Title, _game.StoreLabel));
            return;
        }
        OwnedLibrary.Install(owned, UpdateRunState);
    }

    void DownloadPause_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null || Downloads.Active(_game.Id) is not { Direct: true } job) return;
        if (job.Queued) Downloads.StartNow(job);
        else if (job.State == DownloadState.Paused) Downloads.Resume(job);
        else Downloads.Pause(job);
    }

    void OnDownloadsChanged()
    {
        if (_game is null) return;
        // Installed meanwhile: the page becomes the installed game's (Play, mods, folders). The download ends before the rescan
        // that lists the game, so this runs again on GamesChanged; without it the page kept showing Download until clicked.
        if ((_game.NotInstalled || _game.Installing) && AppState.Find(_game.Id) is { Installing: false, NotInstalled: false } real)
        {
            RequestFresh(real.Id);
            App.Window?.NavigateGame(real.Id);
            App.Window?.DropLastBack();
            return;
        }
        UpdateRunState();
    }

    /// <summary>The download bar beside the button, like Steam's library: state, speed, bytes and time left.</summary>
    void UpdateDownloadPanel(DownloadJob? job)
    {
        bool show = job is not null && (_game?.NotInstalled == true || _game?.Installing == true);
        DownloadPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) PlayStatsPanel.Visibility = Visibility.Collapsed;
        else PlayStatsPanel.Visibility = _laidOutWidth < 980 && !_narrowBar || _laidOutWidth < 520 || !Part(PageParts.PlayStats) ? Visibility.Collapsed : Visibility.Visible;
        if (!show) return;
        var j = job!;
        DownloadStateText.Text = OwnedLibrary.StateText(j).ToUpper(System.Globalization.CultureInfo.CurrentCulture) + (j.Total > 0 ? $"  ·  {j.Fraction:P0}" : "");
        DownloadSpeedText.Text = j.State == DownloadState.Downloading && j.Speed > 0 ? $"{DiskUsage.Format((long)j.Speed)}/s" : "";
        DownloadBar.Value = j.Fraction;
        DownloadBar.IsIndeterminate = j.State == DownloadState.Waiting && !j.Queued || (j.State == DownloadState.Downloading && j.Total <= 0);
        DownloadBar.ShowPaused = j.State == DownloadState.Paused && j.Error is null || j.Queued;
        DownloadBar.ShowError = j.Direct && j.State == DownloadState.Paused && j.Error is not null;
        // Hybrid Launcher's own download: pause / resume (retry after an error) beside the bar. A launcher's download is paused in the launcher.
        DownloadPauseButton.Visibility = j.Direct && !j.CancelRequested ? Visibility.Visible : Visibility.Collapsed;
        bool resume = j.State == DownloadState.Paused || j.Queued;
        DownloadPauseGlyph.Glyph = j.Error is not null && j.State == DownloadState.Paused ? "" : resume ? "" : "";
        ToolTipService.SetToolTip(DownloadPauseButton, j.Error is not null && j.State == DownloadState.Paused ? L.T("Retry") : j.Queued ? L.T("Start now") : resume ? L.T("Resume") : L.T("Pause"));
        var parts = new List<string>();
        if (j.Total > 0) parts.Add(L.F("{0} of {1}", DiskUsage.Format(j.Done), DiskUsage.Format(j.Total)));
        if (j.Remaining is { } left) parts.Add(L.F("{0} left", left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : left.TotalMinutes >= 1 ? $"{left.Minutes} min" : $"{left.Seconds} s"));
        if (j.Direct)
        {
            if (j.Queued) parts.Add(L.T("starts when the download before it finishes"));
            else if (j.Preparing) parts.Add(L.F("reading the file list from {0}", StoreLogos.Label(j.Store)));
            else if (j.Error is not null) parts.Add(j.Error);
        }
        else if (j.State == DownloadState.Waiting) parts.Add(L.T("confirm the install in the launcher"));
        ToolTipService.SetToolTip(DownloadDetailText, j.Error);
        DownloadDetailText.Text = string.Join("  ·  ", parts);
    }

    async void Play()
    {
        if (_game is null || GameSessions.IsActive(_game.Id)) return;
        SaveArgs();
        // Watched from then on: Stop, time played (non-Steam) and Smart HDR follow the game's exe.
        try { await AppState.LaunchAsync(_game); }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
    }

    async Task StopGameAsync()
    {
        if (_game is null) return;
        var game = _game;
        // A game still starting closes at once: the click on the stop square is the decision. A running one asks first (unsaved progress).
        if (GameSessions.State(game.Id) != GameRunState.Starting && !await ConfirmAsync(L.F("Close {0}?", game.Title),
                L.T("Hybrid Launcher asks the game to close, as if you closed its window, and ends it after a few seconds if it does not. Progress that is not saved is lost."),
                L.T("Close the game")))
            return;
        if (!await GameSessions.StopAsync(game.Id))
            App.Window?.ShowError(L.F("{0} is still running", game.Title), L.T("Windows did not let Hybrid Launcher close it (it may run as administrator). Close it from the game or Task Manager."));
    }

    void OnSessionChanged(string gameId)
    {
        if (gameId == _game?.Id) UpdateRunState();
    }

    /// <summary>The launch button follows the game: Play, Starting, Stop while it runs, Closing.</summary>
    void UpdateRunState()
    {
        if (_game is null) return;
        var state = GameSessions.State(_game.Id);
        var job = Downloads.Active(_game.Id);
        UpdateDownloadPanel(job);
        if (_game.Installing || _game.NotInstalled)
        {
            // Not on this PC, or still downloading in its launcher: Download, then its progress; no Play yet.
            PlayButton.SetMode(job is not null || _game.Installing ? Controls.PlayButton.Mode.Downloading : Controls.PlayButton.Mode.Install);
            HdrToggle.IsEnabled = _hdrCapable != false;
            return;
        }
        PlayButton.SetMode(state switch
        {
            GameRunState.Starting => Controls.PlayButton.Mode.Starting,
            GameRunState.Running => Controls.PlayButton.Mode.Stop,
            GameRunState.Stopping => Controls.PlayButton.Mode.Stopping,
            _ => Controls.PlayButton.Mode.Play,
        });
        HdrToggle.IsEnabled = state == GameRunState.Stopped && _hdrCapable != false;
    }

    // ---------- Smart HDR ----------

    bool? _hdrCapable;

    void UpdateHdrToggle()
    {
        HdrToggle.IsChecked = _settings.SmartHdr;
        // Three looks, so "Smart" stands apart: Smart (this game turns HDR on by itself) is the sun and "Smart" in an amber-to-pink
        // sweep; otherwise the tile says what Windows HDR is now: "On" with a plain amber sun, or a grey sun and a quiet "Off".
        bool smart = _settings.SmartHdr, windowsOn = !smart && _windowsHdrOn == true;
        var sweep = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x8A, 0x3D), Offset = 0.5 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x4F, 0xA3), Offset = 1 },
            },
        };
        HdrGlyph.Foreground = smart ? sweep
            : new SolidColorBrush(windowsOn ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC8, 0x3D) : Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        HdrToggleState.Text = smart ? L.T("Smart") : windowsOn ? L.T("On") : L.T("Off");
        HdrToggleState.Opacity = smart || windowsOn ? 1 : 0.75;
        HdrToggleState.FontWeight = smart || windowsOn ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        HdrToggleState.Foreground = smart
            ? new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x8A, 0x3D), Offset = 0.5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x4F, 0xA3), Offset = 1 },
                },
            }
            : new SolidColorBrush(Microsoft.UI.Colors.White);
        if (_hdrCapable is null) _ = CheckHdrAsync();
        ToolTipService.SetToolTip(HdrToggle, _hdrCapable == false
            ? L.T("Smart HDR needs a display that can show HDR: none was found on this PC.")
            : (smart ? "" : (_windowsHdrOn == true ? L.T("Windows HDR is on now.") : L.T("Windows HDR is off now.")) + " ")
              + L.T("Smart HDR for this game: when it starts, Hybrid Launcher turns Windows HDR on (only if it is off), and turns it off again when the game closes, even if it crashes or is ended from Task Manager."));
    }

    bool? _windowsHdrOn;
    bool _hdrReading;

    /// <summary>Reads whether Windows HDR is on now (off the UI thread); the tile follows a change made in Windows within seconds.</summary>
    async Task ReadWindowsHdrAsync()
    {
        if (_hdrReading || _hdrCapable == false) return;
        _hdrReading = true;
        try
        {
            var on = await Task.Run(GameSessions.HdrIsOn);
            if (_game is null || on == _windowsHdrOn) return;
            _windowsHdrOn = on;
            UpdateHdrToggle();
        }
        finally { _hdrReading = false; }
    }

    async Task CheckHdrAsync()
    {
        _hdrCapable = await Task.Run(GameSessions.HdrAvailable);
        if (_game is null) return;
        if (_hdrCapable == false) HdrToggle.IsEnabled = false;
        UpdateHdrToggle();
        await ReadWindowsHdrAsync();
    }

    void HdrToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        _settings.SmartHdr = HdrToggle.IsChecked == true;
        SettingsStore.Save();
        UpdateHdrToggle();
        UpdateRunState();
        App.Window?.ShowInfo(_settings.SmartHdr
            ? L.F("Smart HDR is on for {0}: HDR turns on when you press Play and off when the game closes.", _game.Title)
            : L.F("Smart HDR is off for {0}.", _game.Title));
    }

    bool _narrowBar;
    bool _argsExpanded;

    void ArgsBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Only a click or Tab into the box opens it; focus Windows moves here by itself does not.
        if (ArgsBox.FocusState == FocusState.Programmatic) return;
        _argsExpanded = true;
        LayoutArgsBox();
    }

    void ArgsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _argsExpanded = false;
        LayoutArgsBox();
        SaveArgs();
    }

    /// <summary>
    /// Idle: as wide as its text (150-240 px). Focused: from its place to the right edge, over the buttons, wrapping long lines.
    /// Narrow windows: its own row under the buttons.
    /// </summary>
    void LayoutArgsBox()
    {
        bool wide = _narrowBar || _argsExpanded;
        // The second row is only there for the arguments: with them off the bar, it takes no room.
        LaunchGrid.RowSpacing = _narrowBar && ArgsBox.Visibility == Visibility.Visible ? 10 : 0;
        Grid.SetRow(ArgsBox, _narrowBar ? 1 : 0);
        Grid.SetColumn(ArgsBox, _narrowBar ? 0 : 3);
        Grid.SetColumnSpan(ArgsBox, _narrowBar ? 9 : _argsExpanded ? 6 : 1);
        ArgsBox.MaxWidth = wide ? double.PositiveInfinity : 240;
        ArgsBox.HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ArgsBox.TextWrapping = _argsExpanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
        // Opaque while it covers the buttons, so nothing shows through the text.
        ArgsBox.Background = new SolidColorBrush(_argsExpanded && !_narrowBar ? Windows.UI.Color.FromArgb(0xF2, 0x16, 0x14, 0x1E) : Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>Last played and time played: Steam's own numbers for Steam games, Hybrid Launcher's count for the others.</summary>
    async Task UpdatePlayStatsAsync()
    {
        if (_game is null) return;
        var game = _game;
        var settings = _settings;
        try
        {
            var stats = await Task.Run(() => Playtime.Get(game, settings));
            if (game != _game) return;
            LastPlayedText.Text = Playtime.FormatLast(stats.LastPlayed);
            PlayTimeText.Text = Playtime.FormatTime(stats.Minutes);
            ToolTipService.SetToolTip(PlayStatsPanel, stats.Source == "Steam"
                ? L.F("From Steam: {0:0.#} hours", stats.Minutes / 60.0) + (stats.LastPlayed is { } t ? ", " + L.F("last played {0:d MMM yyyy, HH:mm}", t) : "")
                : L.T("Counted by Hybrid Launcher while the game runs, when you start it with Play here.") + "\n" + L.T("Kept by Hybrid Launcher even if you uninstall the game, move it or add it again.") + (stats.LastPlayed is { } l ? "\n" + L.F("Last played {0:d MMM yyyy, HH:mm}", l) : ""));
        }
        catch (Exception ex) { Log.Warn($"Play stats for {game.Name}: {ex.Message}"); }
    }

    void OnPlaytimeChanged(string gameId)
    {
        if (gameId == _game?.Id) _ = UpdatePlayStatsAsync();
    }

    void ArgsBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Play();
    }

    void SaveArgs()
    {
        if (_game is null || _settings.LaunchArgs == ArgsBox.Text.Trim()) return;
        _settings.LaunchArgs = ArgsBox.Text.Trim();
        SettingsStore.Save();
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_game is not null) AppState.SetPinned(_game.Id, !AppState.IsPinned(_game.Id));
    }

    void UpdatePin()
    {
        if (_game is null) return;
        bool pinned = AppState.IsPinned(_game.Id);
        PinIcon.Glyph = pinned ? "" : "";
        ToolTipService.SetToolTip(PinButton, pinned ? L.T("Unpin from quick access") : L.T("Pin to quick access (top of the game list)"));
    }

    void ShowGameSize()
    {
        SizeText.Text = _settings.SizeBytes is { } bytes ? DiskUsage.Format(bytes) : L.T("Measuring…");
        ToolTipService.SetToolTip(FolderButton, L.T("Open the folder where mods go") + "\n" + L.F("Install folder: {0}", _game?.InstallDir)
            + (_settings.SizeMeasuredAt is { } at ? "\n" + L.F("Size measured {0:yyyy-MM-dd HH:mm}", at) : ""));
    }

    /// <summary>Sizes the install folder in the background; the last size shows meanwhile.</summary>
    async Task MeasureGameSizeAsync(CancellationToken ct)
    {
        if (_game is null || _measuringSize) return;
        var game = _game;
        var settings = _settings;
        // A fresh measurement is enough for a while: big folders take a few seconds to walk.
        if (settings.SizeMeasuredAt is { } at && DateTime.Now - at < TimeSpan.FromMinutes(10)) return;
        _measuringSize = true;
        try
        {
            var bytes = await Task.Run(() => DiskUsage.FolderSize(game.InstallDir, ct), ct);
            settings.SizeBytes = bytes;
            settings.SizeMeasuredAt = DateTime.Now;
            SettingsStore.Save();
            if (game == _game) ShowGameSize();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"Size of {game.InstallDir}: {ex.Message}");
            if (game == _game && settings.SizeBytes is null) SizeText.Text = L.T("Size unknown");
        }
        finally
        {
            _measuringSize = false;
        }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenFolder(TargetDir ?? _game!.InstallDir));
    void OpenInstallFolder_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenFolder(_game!.InstallDir));

    async void ChangeExe_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Install is not null)
        {
            App.Window?.ShowError(L.T("Uninstall OptiScaler first"), L.T("It is installed next to the current exe. Remove it, change the exe, then install again."));
            return;
        }
        var exe = await Pickers.PickExeAsync(App.Window!);
        if (exe is null) return;
        _settings.ExeOverride = exe;
        SettingsStore.Save();
        Refresh();
    }

    void ResetExe_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Install is not null)
        {
            App.Window?.ShowError(L.T("Uninstall OptiScaler first"), L.T("It is installed next to the current exe."));
            return;
        }
        _settings.ExeOverride = null;
        SettingsStore.Save();
        Refresh();
    }

    /// <summary>A shortcut on the desktop that starts the game through Hybrid Launcher (Smart HDR, play time and Stop included).</summary>
    async void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        var game = _game;
        try
        {
            bool existed = Shortcuts.Exists(game);
            await Shortcuts.CreateDesktopAsync(game, _settings);
            App.Window?.ShowInfo(existed
                ? L.F("The desktop shortcut for {0} was updated.", game.Title)
                : L.F("{0} is on the desktop: the shortcut starts it through Hybrid Launcher.", game.Title));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("The shortcut was not created"), ex.Message, ex); }
    }

    // ---------- launch options and the game's page (the gear) ----------

    /// <summary>Launch options live in the game's properties (the gear → General), typed straight in.</summary>
    void LaunchOptions_Click(object sender, RoutedEventArgs e) => OpenProperties("general");

    /// <summary>
    /// Pick the Steam store page whose information the game page shows, when Hybrid Launcher matched the wrong one (or none). Works for every
    /// game, installed or not, with or without a Steam id: the game itself still starts the way it always did.
    /// </summary>
    async void ChoosePage_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        var game = _game;
        var settings = _settings;
        using var cts = new CancellationTokenSource();
        try
        {
            // What the choice is: "auto", "none", or a Steam app id.
            string choice = string.IsNullOrWhiteSpace(settings.PageAppId) ? "auto" : settings.PageAppId!;
            string current = choice;
            string automaticName = GameNames.PlainOrClean(game.Name);

            var term = new TextBox { Text = automaticName, PlaceholderText = L.T("Search the Steam store") };
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 330 };
            var status = new TextBlock { Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], TextWrapping = TextWrapping.Wrap };
            var nameBox = new TextBox { Text = settings.PageName ?? "", PlaceholderText = automaticName, Header = L.T("Name for wikis and sites") };
            var panel = new StackPanel { Spacing = 10, Width = 520 };
            panel.Children.Add(new TextBlock { Text = L.T("The information on this page (trailer, description, reviews, wikis) follows the page you pick. The game still starts the same way."), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(term);
            panel.Children.Add(list);
            panel.Children.Add(status);
            panel.Children.Add(nameBox);

            bool filling = false;
            void Fill(List<(string Id, string Name, string? Image)> hits)
            {
                filling = true;
                list.Items.Clear();
                list.Items.Add(PageRow("auto", L.T("Automatic (Hybrid Launcher's match)"), L.T("Hybrid Launcher looks the game up by its name"), null, current));
                list.Items.Add(PageRow("none", L.T("No store page"), L.T("Show no store information for this game"), null, current));
                // The page chosen before stays in the list even when this search does not find it.
                if (current is not ("auto" or "none") && !hits.Any(h => h.Id == current))
                    list.Items.Add(PageRow(current, L.F("Steam app {0}", current), L.T("The page chosen before"), null, current));
                foreach (var h in hits) list.Items.Add(PageRow(h.Id, h.Name, L.F("Steam app {0}", h.Id), h.Image, current));
                list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == choice) ?? list.Items.FirstOrDefault();
                filling = false;
            }
            list.SelectionChanged += (_, _) =>
            {
                if (!filling && list.SelectedItem is ListViewItem { Tag: string tag }) choice = tag;
            };
            Fill([]);

            CancellationTokenSource? running = null;
            async Task SearchAsync()
            {
                running?.Cancel();
                running = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var token = running.Token;
                var text = term.Text.Trim();
                if (text.Length == 0)
                {
                    status.Text = "";
                    Fill([]);
                    return;
                }
                status.Text = L.T("Searching…");
                try
                {
                    var hits = await GameInfo.SearchStoreAsync(text, token);
                    if (token.IsCancellationRequested) return;
                    status.Text = hits.Count == 0 ? L.T("The Steam store lists nothing for this name.") : "";
                    Fill(hits);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) return;
                    Log.Warn($"Store search for {text}: {ex.Message}");
                    status.Text = L.T("The Steam store could not be reached. Automatic and No store page still work.");
                    Fill([]);
                }
            }
            // Typing waits a moment: one search when the typing pauses, not one per key.
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(400);
            timer.IsRepeating = false;
            Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object> tick = (_, _) => _ = SearchAsync();
            timer.Tick += tick;
            term.TextChanged += (_, _) =>
            {
                timer.Stop();
                timer.Start();
            };
            _ = SearchAsync();

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.T("Choose the game's page"),
                Content = panel,
                PrimaryButtonText = L.T("Save"),
                CloseButtonText = L.T("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ActualTheme,
            };
            var result = await dialog.ShowAsync();
            timer.Stop();
            // The timer holds its handler outside the GC's view, and the handler the timer: let go, or the dialog stays in memory.
            timer.Tick -= tick;
            cts.Cancel();
            if (result != ContentDialogResult.Primary || _game != game) return;

            settings.PageAppId = choice == "auto" ? null : choice;
            var name = nameBox.Text.Trim();
            settings.PageName = name.Length == 0 ? null : name;
            SettingsStore.Save();
            LibraryPage.ForgetCover(game.Id);
            // Open the page again as it is now: the old one is cancelled by leaving it, and does not stay in Back.
            RequestFresh(game.Id);
            App.Window?.NavigateGame(game.Id);
            App.Window?.DropLastBack();
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the game's page"), ex.Message, ex); }
    }

    /// <summary>One choice of the page list: a capsule picture (when there is one), the name, a muted line, and a mark on the current one.</summary>
    static ListViewItem PageRow(string tag, string name, string detail, string? image, string current)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var art = new Border { Width = 120, Height = 45, CornerRadius = new CornerRadius(4), Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["ControlFillColorSecondaryBrush"] };
        if (image is not null && Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            art.Child = Smooth.Art(new BitmapImage { UriSource = uri, DecodePixelWidth = 240 }, Smooth.Inner(art));
        else
            art.Child = new FontIcon { Glyph = tag == "none" ? "" : tag == "auto" ? "" : "", FontSize = 18, Opacity = 0.6 };
        grid.Children.Add(art);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = detail, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (tag == current)
        {
            var mark = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 1, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["AccentFillColorDefaultBrush"],
                Child = new TextBlock { Text = L.T("Current"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)HybridLauncher.Helpers.ThemeRes.Current["TextOnAccentFillColorPrimaryBrush"] },
            };
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }
        return new ListViewItem { Tag = tag, Content = grid };
    }

    // ---------- parts of the page the user hides (everywhere in Settings, for this game in the gear beside Play → Show on this page) ----------

    /// <summary>The part is shown on this game's page: hidden neither for every game nor for this one.</summary>
    bool Part(string key) => !PageParts.Hidden(AppState.Settings, _settings, key);

    // The line under the launch bar says something needs attention (no exe, downloading, not installed): shown even when hidden.
    bool _exeLineAlert;

    static Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows or hides every part of the banner and the launch bar the user can hide, and the icons in them.</summary>
    void ApplyPageParts()
    {
        if (_game is null) return;
        bool offline = _game.NotInstalled;
        BannerTags.Visibility = Shown(Part(PageParts.Tags));
        HdrToggle.Visibility = Shown(Part(PageParts.Hdr));
        CloudButton.Visibility = Shown(!offline && Part(PageParts.Cloud));
        FolderButton.Visibility = Shown(Part(PageParts.GameFolder));
        SaveFolderButton.Visibility = Shown(_saveDir is not null && !offline && Part(PageParts.SaveFolder));
        ConfigFolderButton.Visibility = Shown(_configDir is not null && !offline && Part(PageParts.ConfigFolder));
        AchButton.Visibility = Shown(_ach is not null && Part(PageParts.Achievements));
        // Off by default; a line that tells something is wrong or pending (no exe found, downloading, not installed) always shows.
        ExeLine.Visibility = Shown(Part(PageParts.ModsPath) || _exeLineAlert);
        UpdateDownloadPanel(Downloads.Active(_game.Id));
        UpdateLinks();
        _ = UpdateModBadgesAsync();
        UpdateDlssNrRow();

        // Settings → Appearance → Icons: the launch bar and the banner without their icons.
        bool bar = Icons.Show(IconSpots.LaunchBar), banner = Icons.Show(IconSpots.Banner);
        foreach (var icon in new FrameworkElement[] { HdrIcon, CloudIcon, FolderIcon, SaveIcon, ConfigIcon, AchTile }) icon.Visibility = Shown(bar);
        foreach (var icon in new FrameworkElement[] { NexusIcon, PcgwIcon, SteamDbIcon, ApiIcon }) icon.Visibility = Shown(banner);
        if (StoreBadgePanel.Children.Count > 0 && StoreBadgePanel.Children[0] is FrameworkElement logo and not TextBlock) logo.Visibility = Shown(banner);
    }

    // ---------- the banner's top-left corner: what is installed, at a glance ----------

    int _modBadgesRun;

    /// <summary>
    /// OptiScaler, ReShade, Ultra+, RenoDX and Luma in the game folder, each with its version, as small quiet lines under the title
    /// (Settings → Game page, or this game's gear, can hide them). The folder is read off the UI thread; a click opens the mod's tab.
    /// </summary>
    async Task UpdateModBadgesAsync()
    {
        int run = ++_modBadgesRun;
        // The mod check's warning shows even with the mod tags hidden: it tells something is broken.
        bool tags = Part(PageParts.InstalledMods);
        if (_game is null || _game.NotInstalled || TargetDir is not { } dir)
        {
            ModBadges.Visibility = Visibility.Collapsed;
            StopWatchingModsFolder();
            return;
        }
        var settings = _settings;
        var exeDir = GameScanner.EffectiveExe(_game, settings) is { } exe ? Path.GetDirectoryName(exe) : null;
        // Where the OptiScaler build came from (the fork's name is in the app's settings: read here, on the UI thread).
        var m = settings.Install;
        string source = m is not null && OptiDetector.ReplacedSince(m) ? "" : m?.Channel switch
        {
            OptiChannel.Nightly => L.T("nightly"),
            OptiChannel.Fork => OptiUpdates.Name(OptiUpdates.Key(OptiChannel.Fork, m.Source), AppState.Settings),
            OptiChannel.Custom => L.T("custom build"),
            _ => "",
        };
        List<(string Name, string Detail, string Tab, Windows.UI.Color Tint)> found = [];
        List<Core.Mods.ModProblem> problems = [];
        try
        {
            // The mod check reads the same folder: things a game update undid, or two mods in each other's way.
            problems = await Task.Run(() => Core.Mods.ModCheck.Run(settings, dir));
            if (tags) found = await Task.Run(() =>
            {
                var list = new List<(string, string, string, Windows.UI.Color)>();
                if (OptiDetector.Detect(dir) is { } opti)
                {
                                        // The version as read (a release number, or a dev build by its date and commit): the line stays short.
                    // A dev build: its major number and day alone ("v10 · 10 Nov"), no channel word; a fork or custom build says which.
                    bool dev = CustomBuilds.IsDev(opti.Version);
                    string from = dev && source == L.T("nightly") ? "" : source;
                    string named = m is not null && !OptiDetector.ReplacedSince(m) && TagNumber(m) is { } tagged ? "v" + tagged : CustomBuilds.Brief(opti.Version);
                    list.Add(("OptiScaler", named + (from.Length > 0 ? "  ·  " + from : ""), "opti", Windows.UI.Color.FromArgb(0xFF, 0xB3, 0x8B, 0xFF)));
                }
                if (Core.Mods.ReShade.Detect(dir) is { } reshade)
                    list.Add(("ReShade", reshade.Version, "mods/reshade", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xA0, 0x4D)));
                if (settings.UltraPlus is { } ultra && Directory.Exists(ultra.Root))
                    list.Add(("Ultra+", "v" + ultra.Version + (ultra.Beta ? "  ·  " + L.T("beta") : ""), "ultra", Windows.UI.Color.FromArgb(0xFF, 0x4F, 0xD6, 0xB0)));
                if (settings.RenoDxFiles.Count > 0 && exeDir is not null && settings.RenoDxFiles.Keys.Any(f => File.Exists(Path.Combine(exeDir, f))))
                    list.Add(("RenoDX", string.Join(", ", settings.RenoDxFiles.Keys.Select(Path.GetFileNameWithoutExtension)), "mods/renodx", Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xB8, 0xFF)));
                if (settings.Luma is { } luma)
                    list.Add(("Luma", luma.Mod + (luma.Release.Length > 0 ? "  ·  " + luma.Release : ""), "mods/luma", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x8A)));
                if (settings.Dxvk is { } dxvk)
                    list.Add((Core.Mods.Dxvk.Name(dxvk.Flavor), dxvk.Version + "  ·  " + Core.Mods.Dxvk.Name(dxvk.Api), Core.Mods.Dxvk.Section(dxvk.Flavor == Core.Mods.DxvkFlavor.Vkd3d), Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x4A, 0x5F)));
                else if (Core.Mods.Dxvk.Detect(dir).Select(d => d.Layer).Distinct().ToList() is { Count: > 0 } layers)
                    list.Add((string.Join(" + ", layers), L.T("in the folder"), Core.Mods.Dxvk.Section(layers.Contains("VKD3D-Proton")), Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x4A, 0x5F)));
                return list;
            });
        }
        catch (Exception ex) { Log.Warn($"Installed mods on the banner: {ex.Message}"); }
        // A newer look started meanwhile (another game, a mod installed): this one is stale.
        if (run != _modBadgesRun) return;
        ModBadges.Children.Clear();
        foreach (var (name, detail, tab, tint) in found)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(tint), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = name, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis });
            var badge = new Border
            {
                Child = line,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 9, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            ToolTipService.SetToolTip(badge, L.F("{0} {1}: click for its tab, or to hide these tags", name, detail));
            string open = tab;
            // Either button opens the same small menu: the mod's tab, or hide these tags (Settings → Game page brings them back).
            var menu = new MenuFlyout();
            var go = new MenuFlyoutItem { Text = L.F("Open {0}", name), Icon = new FontIcon { Glyph = "" } };
            go.Click += (_, _) => Ui.Safe(() => OpenTab(open));
            var hide = new MenuFlyoutItem { Text = L.T("Hide these tags"), Icon = new FontIcon { Glyph = "" } };
            hide.Click += (_, _) =>
            {
                AppState.Settings.HiddenPageParts.Add(PageParts.InstalledMods);
                SettingsStore.Save();
                ApplyPageParts();
                App.Window?.ShowInfo(L.T("Installed mods hidden on the banner of every game. Settings → Appearance → Game page shows them again."));
            };
            var filesItem = new MenuFlyoutItem { Text = L.T("All installed mods"), Icon = new FontIcon { Glyph = "\uE8FD" } };
            filesItem.Click += (_, _) => Ui.Safe(() => OpenTab("mods/installed"));
            menu.Items.Add(go);
            menu.Items.Add(filesItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(hide);
            badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            badge.Tapped += (_, e) => { e.Handled = true; menu.ShowAt(badge, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft }); };
            badge.RightTapped += (_, e) => { e.Handled = true; menu.ShowAt(badge, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(badge) }); };
            ModBadges.Children.Add(badge);
        }
        // Findings the user set aside stay aside while they last; one that went away and comes back shows again.
        settings.DismissedModProblems.RemoveWhere(id => !problems.Any(p => p.Id == id));
        var pending = problems.Where(p => !settings.DismissedModProblems.Contains(p.Id)).ToList();
        if (pending.Count > 0) ModBadges.Children.Insert(0, ProblemBadge(pending));
        ModBadges.Visibility = Shown(ModBadges.Children.Count > 0);
        // The Mods tab's table and the Dashboard's Mods card follow every change to the folder too.
        if (_tab == "mods") _ = RefreshModsOverviewAsync();
        _dashboard?.RefreshMods();
        WatchModsFolder(dir);
    }

    /// <summary>The banner's warning: how many things stop a mod from working here; a click opens the Mods tab's list of them.</summary>
    Border ProblemBadge(List<Core.Mods.ModProblem> problems)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC0, 0x4D)), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock
        {
            Text = problems.Count == 1 ? problems[0].Title : L.F("{0} problems with this game's mods", problems.Count),
            FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var badge = new Border
        {
            Child = line,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x8A, 0x1C, 0x1C)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        // Several problems: their titles in the tooltip, so the banner says what they are without a click (2.0.0-rc.12).
        var tip = L.T("Click for what is wrong and how to fix it");
        ToolTipService.SetToolTip(badge, problems.Count > 1 ? string.Join("\n", problems.Select(p => "• " + p.Title)) + "\n\n" + tip : tip);
        badge.Tag = string.Join(" | ", problems.Select(p => p.Title));
        badge.Tapped += (_, e) => { e.Handled = true; Ui.Safe(() => OpenTab("mods/installed")); };
        return badge;
    }

    /// <summary>The files the last read of the Mods tab found (the self-test reads them).</summary>
    List<Core.Mods.ModFile> _lastModFiles = [];

    /// <summary>
    /// One file: its name, an arrow, the mod and version, what loads it, who put it there, and (not <paramref name="compact"/>)
    /// a way to its section. Compact under a mod of the Mods tab's table, where the line above already leads there.
    /// </summary>
    Border ModFileRow(Core.Mods.ModFile f, bool compact = false)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = f.File, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
        ToolTipService.SetToolTip(name, f.File);
        grid.Children.Add(name);
        var arrow = new FontIcon
        {
            Glyph = f.Problem ? "\uE7BA" : "\uE72A", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(f.Problem ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xB0, 0x40) : Windows.UI.Color.FromArgb(0x99, 0x99, 0x99, 0x99)),
        };
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = f.What + (f.Version.Length > 0 ? "  ·  " + f.Version : ""), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var who = f.ByHybrid ? L.T("put in by Hybrid Launcher") : f.Role == Core.Mods.ModFileRole.Upscaler ? L.T("shipped with the game") : L.T("put in another way");
        text.Children.Add(new TextBlock { Text = f.How + "  ·  " + who, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        if (f.Tab is { } tab && !compact)
        {
            var open = new Button { Content = L.T("Open"), Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center };
            open.Click += (_, _) => OpenTab(tab);
            Grid.SetColumn(open, 3);
            grid.Children.Add(open);
        }
        return new Border { Style = Ui.Style("RowStyle"), Padding = compact ? new Thickness(12, 4, 12, 4) : new Thickness(12, 8, 12, 8), MinHeight = 0, Child = grid };
    }

    /// <summary>Self-test: opens the Mods tab on its table with every mod's files shown; the next call returns the files as text.</summary>
    internal void SelfTestModFilesOpen()
    {
        ShowTab("mods", remember: false, section: "installed");
        _modsOpen.UnionWith(["opti", "reshade", "renodx", "luma", "dxvk", "vkd3d", "ultra"]);
        _ = RefreshModsOverviewAsync();
    }

    internal string SelfTestModFilesClose()
    {
        return string.Join(" | ", _lastModFiles.Select(f => $"{f.File} = {f.What} {f.Version} ({f.How}){(f.ByHybrid ? " [HL]" : "")}{(f.Problem ? " [!]" : "")}"));
    }

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.HiddenGames.Add(_game!.Id);
        SettingsStore.Save();
        App.Window?.SelectGame("");
        App.Window?.ShowInfo(L.F("{0} is hidden. Show it again from Settings.", _game.Title));
    }

    // ---------- OptiScaler: status and versions ----------

    void UpdateOptiStatus()
    {
        SyncNotes();
        var target = TargetDir;
        var m = _settings.Install;
        var detected = target is null ? null : OptiDetector.Detect(target);

        string pill, headline, detail, glyph;
        string? build = null;
        Windows.UI.Color tint;
        bool installed = m is not null && detected is not null;
        // The dll was swapped since the install (a nightly or a test build dropped over it): the record's build no longer applies.
        bool replaced = installed && OptiDetector.ReplacedSince(m!);
        if (installed)
        {
            // Version (with the release tag as a badge), build, then the facts as chips; the commit is left to the tooltip.
            pill = L.T("Installed");
            // A fork whose release tag is a plain number is named by it (rc.10: janblade's v0.1.37.2 read "0.1.37 pre2", the
            // number its dll carries); a tag that is not a number (Venom's) leaves the dll's version.
            // An official nightly reads as its number with its tag as the badge, as in 1.3 ("10.0.0 dev" + nightly-20261111).
            bool officialNightly = !replaced && m!.Channel == OptiChannel.Nightly && !string.IsNullOrWhiteSpace(m.Tag);
            headline = "OptiScaler " + (!replaced && TagNumber(m!) is { } number ? number
                : officialNightly ? NumberOnly(detected!.Version) : ShortVersion(detected!.Version));
            build = replaced ? L.T("Replaced outside Hybrid Launcher") : BuildName(m!);
            detail = replaced ? L.F("The DLL in the folder is not the {0} Hybrid Launcher installed: the version above is the one there now. Uninstall still puts the game's own files back.", m!.DisplayVersion) : "";
            glyph = "";
            tint = Palettes.Adapt(Windows.UI.Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43));
        }
        else if (m is not null)
        {
            pill = L.T("Missing");
            headline = L.T("OptiScaler's DLL is missing");
            detail = L.F("Hybrid Launcher installed {0}, but its DLL is gone from the folder. Uninstall to clean up, or install again.", m.DisplayVersion);
            glyph = "";
            tint = Palettes.Adapt(Windows.UI.Color.FromArgb(0xFF, 0xD2, 0x99, 0x22));
        }
        else if (detected is not null)
        {
            pill = L.T("Found");
            headline = L.F("OptiScaler {0} found (not installed by Hybrid Launcher)", ShortVersion(detected.Version));
            detail = L.F("{0}. Installing here replaces it; the old files are backed up.", detected.FileName);
            glyph = "";
            tint = Palettes.Adapt(Windows.UI.Color.FromArgb(0xFF, 0x38, 0x8B, 0xFD));
        }
        else
        {
            pill = L.T("Not installed");
            headline = L.T("OptiScaler is not installed");
            detail = target is null ? L.T("Pick the game exe first (the gear beside Play → Change game exe).") : L.F("Pick a version and press Install. It goes in {0}", target);
            glyph = "";
            tint = Palettes.Adapt(Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x94, 0x9E));
        }
        StatusPillText.Text = pill;
        StatusPill.Background = Pill(tint.R, tint.G, tint.B);
        OptiHeadline.Text = headline;
        ToolTipService.SetToolTip(OptiHeadline, detected is not null ? $"{detected.FileName}  ·  {CustomBuilds.Readable(detected.Version)}" : null);
        OptiBuildName.Text = build ?? "";
        OptiBuildName.Visibility = Shown(build is not null);
        OptiStatusDetail.Text = detail;
        OptiStatusDetail.Visibility = Shown(detail.Length > 0);
        OptiDllChip.Visibility = OptiDateChip.Visibility = Shown(installed);
        OptiNrChip.Visibility = Shown(installed && m!.Files.ContainsKey(DlssNr.FileName));
        var tag = installed && !replaced ? ReleaseTag(m!, detected!.Version) : null;
        OptiVersionBadge.Visibility = Shown(tag is not null);
        OptiVersionText.Text = tag ?? "";
        var built = detected is not null ? BuiltOn(detected.Version) : null;
        // A dev build's day is already in its name (an official nightly is named by its number, so its day shows here).
        OptiBuiltChip.Visibility = Shown(installed && built is not null && (!CustomBuilds.IsDev(detected!.Version) || m!.Channel == OptiChannel.Nightly && !replaced));
        OptiBuiltText.Text = built is { } b ? L.F("Built {0}", b.ToString("d MMM yyyy")) : "";
        if (installed)
        {
            OptiDllText.Text = m!.DllName;
            OptiDateText.Text = m.InstalledAt.ToString("d MMM yyyy, HH:mm");
        }
        StatusIcon.Glyph = glyph;
        // The state's glyph in the state's colour, with no disc behind it (1.4.0-rc.9).
        StatusIconCircle.Background = null;
        StatusIcon.Foreground = new SolidColorBrush(tint);
        // A faint wash of the state's colour behind the header, fading out early so the text sits on the plain card.
        OptiHeader.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x1C, tint.R, tint.G, tint.B), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, tint.R, tint.G, tint.B), Offset = 0.55 },
            },
        };

        UninstallButton.Visibility = m is not null ? Visibility.Visible : Visibility.Collapsed;
        OpenIniButton.Visibility = target is not null && File.Exists(Path.Combine(target, "OptiScaler.ini")) ? Visibility.Visible : Visibility.Collapsed;
        Step3Title.Text = m is null ? L.T("Install") : L.T("Update, reinstall or remove");
        _ = RefreshUpdateAvailableAsync();
        FillShortcutKeys();
        SetBusy(_busy);
    }

    /// <summary>"10.0.0 dev" from OptiScaler's version ("10.0.0-dev (4420de92) (20261102_145100)"): the number only.</summary>
    static string ShortVersion(string v)
    {
        // A development build's placeholder number ("10.0.0-dev") is no release: its date and commit name it.
        if (CustomBuilds.IsDev(v)) return CustomBuilds.Readable(v);
        var number = CustomBuilds.PrettyVersion(v).Split(" · ")[0].Trim();
        int dash = number.IndexOf('-');
        return dash > 0 ? number[..dash] + " " + number[(dash + 1)..] : number;
    }

    /// <summary>"10.0.0 dev" from "10.0.0-dev (4420de92) (20261102_145100)": the number as OptiScaler gives it, dash as a space.</summary>
    static string NumberOnly(string v)
    {
        var first = v.Replace("(", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('v', 'V') ?? v;
        int dash = first.IndexOf('-');
        return dash > 0 ? first[..dash] + " " + first[(dash + 1)..] : first;
    }

    /// <summary>The installed build's own name: the custom build's folder, the fork's name, or the channel (its tag is the badge).</summary>
    static string BuildName(InstallManifest m) => m.Channel switch
    {
        OptiChannel.Custom => m.AssetName,
        OptiChannel.Fork => OptiUpdates.Name(OptiUpdates.Key(OptiChannel.Fork, m.Source), AppState.Settings),
        OptiChannel.Nightly => L.T("Official nightly build"),
        _ => L.T("Official stable release"),
    };

    /// <summary>A fork's release tag when it is a plain number ("v0.1.37.2" → "0.1.37.2"), else null.</summary>
    static string? TagNumber(InstallManifest m) =>
        m.Channel == OptiChannel.Fork && System.Text.RegularExpressions.Regex.IsMatch(m.Tag ?? "", @"^v?\d+(\.\d+)+$") ? m.Tag!.TrimStart('v', 'V') : null;

    /// <summary>
    /// The release tag of the installed build ("v0.1.36"), for the badge beside the version: none for custom builds (their tag
    /// is the raw version string, commit included) or when it only repeats the version number already in the title.
    /// </summary>
    static string? ReleaseTag(InstallManifest m, string dllVersion)
    {
        // The title already reads the fork's tag.
        if (TagNumber(m) is not null && !OptiDetector.ReplacedSince(m)) return null;
        // A dev build has no tag of its own, save the official nightly's ("nightly-20261111", brought back in 2.0.0-rc.4).
        if (m.Channel == OptiChannel.Custom || CustomBuilds.IsDev(dllVersion) && m.Channel != OptiChannel.Nightly || string.IsNullOrWhiteSpace(m.Tag) || m.Tag.Contains(' ') || m.Tag.Length > 32) return null;
        static string Plain(string v) => v.Trim().TrimStart('v', 'V').Replace('-', ' ').ToLowerInvariant();
        return Plain(m.Tag) == Plain(ShortVersion(dllVersion)) ? null : m.Tag;
    }

    /// <summary>The build date OptiScaler writes in its version ("... (20261102_145100)"), or null.</summary>
    static DateTime? BuiltOn(string v)
    {
        foreach (var part in v.Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (DateTime.TryParseExact(part, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var d)) return d;
        return null;
    }

    /// <summary>Re-measures OptiScaler's files (payload, plugins, ini, logs) off the UI thread and shows the total.</summary>
    async Task UpdateFootprintAsync()
    {
        if (_game is null || _measuringFiles || _busy) return;
        var game = _game;
        var target = TargetDir;
        var manifest = _settings.Install;
        if (target is null)
        {
            OptiFilesPanel.Visibility = Visibility.Collapsed;
            return;
        }
        _measuringFiles = true;
        try
        {
            var fp = await Task.Run(() => OptiFootprint.Measure(target, manifest, manifest is null ? OptiDetector.Detect(target) : null));
            if (game != _game) return;
            OptiFilesPanel.Visibility = fp.FileCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (fp.FileCount == 0) return;
            var text = DiskUsage.Format(fp.TotalBytes) + " " + L.N(fp.FileCount, "in {0} file", "in {0} files")
                       + (fp.LogBytes > 0 ? "  ·  " + L.F("logs {0}", DiskUsage.Format(fp.LogBytes)) : "");
            if (OptiFilesText.Text != text)
            {
                OptiFilesText.Text = text;
                var top = fp.Files.Take(14).Select(f => $"{DiskUsage.Format(f.Bytes),9}  {f.RelativePath}");
                ToolTipService.SetToolTip(OptiFilesPanel, new ToolTip
                {
                    Content = new TextBlock
                    {
                        Text = L.T("Largest files (updated every few seconds, logs grow while the game runs):") + "\n\n" + string.Join("\n", top)
                               + (fp.Files.Count > 14 ? "\n" + L.F("… and {0} more", fp.Files.Count - 14) : ""),
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12,
                    },
                });
            }
        }
        catch (Exception ex) { Log.Warn($"OptiScaler footprint for {game.Name}: {ex.Message}"); }
        finally
        {
            _measuringFiles = false;
        }
    }

    // Translucent tint: readable on both the light and the dark theme, whatever theme the app was started with.
    static SolidColorBrush Pill(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(0x55, r, g, b));

    async Task LoadReleasesAsync(bool force, CancellationToken ct)
    {
        _releasesFailed = false;
        await Task.WhenAll(Fetch(OptiChannel.Stable), Fetch(OptiChannel.Nightly), LoadForkAsync(force, ct));
        if (ct.IsCancellationRequested) return;
        _releasesLoaded = true;
        if (_channel != OptiChannel.Custom) FillVersions();
        _ = RefreshUpdateAvailableAsync();

        async Task Fetch(OptiChannel channel)
        {
            try
            {
                var list = await OptiReleases.GetAsync(channel, force, ct);
                if (ct.IsCancellationRequested) return;
                if (channel == OptiChannel.Stable) _stable = list; else _nightly = list;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _releasesFailed = true;
                Log.Warn($"Releases {channel}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A newer list of a source this page shows, read elsewhere (the background update check that just notified it): it replaces
    /// the one loaded when the page opened, so the version list matches the notification without a restart or a request.
    /// </summary>
    void OnReleasesUpdated(OptiChannel channel, string? repo, List<OptiRelease> list) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_pageCts.IsCancellationRequested) return;
        // The page's own load already took it (the event is raised while that load reads the list).
        var shown = channel switch { OptiChannel.Stable => _stable, OptiChannel.Nightly => _nightly, _ => _fork };
        if (shown.Count > 0 && shown[0].Tag.Equals(list[0].Tag, StringComparison.OrdinalIgnoreCase) && (channel != OptiChannel.Fork || _forkLoaded is not null && _forkLoaded.Equals(repo, StringComparison.OrdinalIgnoreCase))) return;
        switch (channel)
        {
            case OptiChannel.Stable: _stable = list; break;
            case OptiChannel.Nightly: _nightly = list; break;
            case OptiChannel.Fork when repo is not null && repo.Equals(AppState.Settings.SelectedFork, StringComparison.OrdinalIgnoreCase):
                _fork = list; _forkLoaded = AppState.Settings.SelectedFork; _forkFailed = false; break;
            default: return;
        }
        if (channel == _channel)
        {
            // The version picked stays picked ("Latest" moves on to the new one, like a fresh list).
            var picked = VersionCombo.SelectedIndex > 0 ? VersionCombo.SelectedItem as string : null;
            FillVersions();
            if (picked is not null && VersionCombo.Items.IndexOf(picked) is var at and > 0) VersionCombo.SelectedIndex = at;
        }
        _ = RefreshUpdateAvailableAsync();
    });

    bool _releasesLoaded;
    bool _fillingVersions;
    string? _selectCustom;

    // A fork's list counts only once it is the picked fork's (rc.10: while another fork's versions loaded, Install took the
    // previous fork's newest build, Venom instead of the fork just picked).
    List<OptiRelease> Releases => _channel switch
    {
        OptiChannel.Nightly => _nightly,
        OptiChannel.Fork => _forkLoaded is { } loaded && loaded.Equals(AppState.Settings.SelectedFork, StringComparison.OrdinalIgnoreCase) ? _fork : [],
        _ => _stable,
    };

    /// <summary>One entry of the fork list: a known fork or one the user added.</summary>
    sealed record ForkItem(string Name, string Repo, bool User)
    {
        public override string ToString() => Name;
    }

    /// <summary>Loads the releases of the chosen fork (nothing when none is chosen or it is the one already loaded).</summary>
    async Task LoadForkAsync(bool force, CancellationToken ct)
    {
        var repo = AppState.Settings.SelectedFork;
        if (repo is null) { _fork = []; _forkLoaded = null; _forkFailed = false; return; }
        // A fork that failed is asked again: the failure was likely GitHub's limit or a dropped connection.
        if (!force && _forkLoaded == repo && !_forkFailed) return;
        try
        {
            var list = await OptiReleases.GetForkAsync(repo, force, ct);
            // Another fork picked while this one loaded: its list must not end up under the other's name.
            if (ct.IsCancellationRequested || AppState.Settings.SelectedFork != repo) return;
            _fork = list;
            _forkLoaded = repo;
            _forkFailed = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (AppState.Settings.SelectedFork != repo) return;
            _fork = [];
            _forkLoaded = repo;
            _forkFailed = true;
            Log.Warn($"Releases of fork {repo}: {ex.Message}");
        }
    }

    bool _forkFailed;

    void FillForkList()
    {
        _fillingFork = true;
        try
        {
            ForkCombo.Items.Clear();
            var user = AppState.Settings.OptiForks.Select(f => f.Repo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in OptiForks.All(AppState.Settings))
                ForkCombo.Items.Add(new ForkItem(f.Name.Length > 0 ? f.Name : f.Repo, f.Repo, user.Contains(f.Repo) && !OptiForks.Known.Any(k => k.Repo.Equals(f.Repo, StringComparison.OrdinalIgnoreCase))));
            var current = AppState.Settings.SelectedFork;
            ForkCombo.SelectedItem = ForkCombo.Items.OfType<ForkItem>().FirstOrDefault(i => i.Repo.Equals(current, StringComparison.OrdinalIgnoreCase))
                ?? ForkCombo.Items.OfType<ForkItem>().FirstOrDefault();
            if (ForkCombo.SelectedItem is ForkItem chosen) AppState.Settings.SelectedFork = chosen.Repo;
        }
        finally { _fillingFork = false; }
    }

    bool _fillingFork;

    async void ForkCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingFork || ForkCombo.SelectedItem is not ForkItem item) return;
        AppState.Settings.SelectedFork = item.Repo;
        SettingsStore.Save();
        RemoveForkButton.IsEnabled = !_busy && item.User;
        // The previous fork's list goes at once, so nothing installs from it while this one loads.
        _fork = [];
        _forkLoaded = null;
        VersionCombo.Items.Clear();
        VersionCombo.PlaceholderText = L.T("Loading versions…");
        SetBusy(_busy);
        await LoadForkAsync(false, _pageCts.Token);
        if (_channel == OptiChannel.Fork) FillVersions();
    }

    /// <summary>Asks for the address of a GitHub repository that publishes OptiScaler builds, checks it has releases, and adds it.</summary>
    async void AddFork_Click(object sender, RoutedEventArgs e)
    {
        // The same dialog and checks as the Auto tab's "Add a build" (2.0.0-rc.18).
        if (await OptiSourceImport.AddForkAsync(XamlRoot, ActualTheme, _pageCts.Token) is not { } added) return;
        AppState.Settings.SelectedFork = added.Repo;
        SettingsStore.Save();
        FillForkList();
        ForkCard.IsChecked = true;
        _fork = added.Releases;
        _forkLoaded = added.Repo;
        FillVersions();
    }

    void RemoveFork_Click(object sender, RoutedEventArgs e)
    {
        if (ForkCombo.SelectedItem is not ForkItem { User: true } item) return;
        AppState.Settings.OptiForks.RemoveAll(f => f.Repo.Equals(item.Repo, StringComparison.OrdinalIgnoreCase));
        AppState.Settings.SelectedFork = null;
        SettingsStore.Save();
        FillForkList();
        _ = LoadForkAsync(false, _pageCts.Token).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => { if (_channel == OptiChannel.Fork) { FillVersions(); _ = SyncForwarderAsync(); } }));
    }

    // Stable / nightly: item 0 of the list is "Latest". Custom: one item per build.
    OptiRelease? SelectedRelease => _channel == OptiChannel.Custom
        ? SelectedCustom?.ToRelease()
        : Releases.Count == 0 ? null : VersionCombo.SelectedIndex <= 0 ? Releases[0] : Releases[Math.Min(VersionCombo.SelectedIndex - 1, Releases.Count - 1)];

    CustomBuild? SelectedCustom => _channel == OptiChannel.Custom && VersionCombo.SelectedIndex >= 0 && VersionCombo.SelectedIndex < _custom.Count
        ? _custom[VersionCombo.SelectedIndex] : null;

    RadioButton ChannelCard(OptiChannel channel) => channel switch { OptiChannel.Nightly => NightlyCard, OptiChannel.Fork => ForkCard, OptiChannel.Custom => CustomCard, _ => StableCard };

    void Channel_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<OptiChannel>(tag, out var channel)) return;
        _channel = channel;
        if (channel == OptiChannel.Fork)
        {
            if (ForkCombo.Items.Count == 0) FillForkList();
            if (_forkLoaded != AppState.Settings.SelectedFork || _forkFailed) _ = LoadForkAsync(false, _pageCts.Token).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => { if (_channel == OptiChannel.Fork) FillVersions(); }));
        }
        FillVersions();
    }

    /// <summary>Lists the versions of the chosen channel: the installed build stays selected for custom builds, "Latest" otherwise.</summary>
    void FillVersions()
    {
        _fillingVersions = true;
        try
        {
            VersionCombo.Items.Clear();
            bool custom = _channel == OptiChannel.Custom;
            ForkRow.Visibility = _channel == OptiChannel.Fork ? Visibility.Visible : Visibility.Collapsed;
            ImportBuildButton.Visibility = CustomMoreButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            ReleasesLink.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(VersionCombo, null);
            if (custom)
            {
                foreach (var b in _custom) VersionCombo.Items.Add(b.Display);
                VersionCombo.PlaceholderText = L.T("No builds yet: use Import");
                var installed = _settings.Install is { Channel: OptiChannel.Custom } m ? m.AssetName : null;
                int index = _custom.FindIndex(b => b.Name.Equals(_selectCustom ?? installed, StringComparison.OrdinalIgnoreCase));
                VersionCombo.SelectedIndex = index >= 0 ? index : _custom.Count > 0 ? 0 : -1;
            }
            else
            {
                var list = Releases;
                bool failed = _channel == OptiChannel.Fork ? _forkFailed : _releasesFailed;
                VersionCombo.PlaceholderText = failed && list.Count == 0 && Http.GitHubLimitedUntil is { } until
                    ? L.F("GitHub limit reached: versions after {0:HH:mm}", until.ToLocalTime())
                    : failed ? L.T("Offline: can't list versions") : _releasesLoaded && (_channel != OptiChannel.Fork || _forkLoaded is not null) ? L.T("No releases found") : L.T("Loading versions…");
                if (failed && list.Count == 0) RetryWhenGitHubResets();
                if (list.Count > 0)
                {
                    VersionCombo.Items.Add(L.F("Latest  ({0})", list[0].Tag));
                    foreach (var r in list) VersionCombo.Items.Add(r.Display);
                    VersionCombo.SelectedIndex = 0;
                }
            }
        }
        finally
        {
            _fillingVersions = false;
        }
        UpdateVersionDetail();
    }

    bool _retryScheduled;

    /// <summary>A list GitHub refused because its limit ran out loads again, once: as soon as a GitHub account is signed in, or a
    /// few seconds after the limit resets.</summary>
    async void RetryWhenGitHubResets()
    {
        if (_retryScheduled || Http.GitHubLimitedUntil is not { } until) return;
        _retryScheduled = true;
        var ct = _pageCts.Token;
        try
        {
            var again = until + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < again && Http.GitHubLimitedUntil is not null) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            _retryScheduled = false;
            if (_channel == OptiChannel.Fork) _forkLoaded = null;
            await LoadReleasesAsync(false, ct);
        }
        catch (OperationCanceledException) { }
        finally { _retryScheduled = false; }
    }

    void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingVersions) UpdateVersionDetail();
    }

    /// <summary>A plain-words hint for the chosen channel / build, then the install button's label.</summary>
    void UpdateVersionDetail()
    {
        var b = SelectedCustom;
        RemoveBuildItem.IsEnabled = b is not null;
        // A fork's hint is just its repository, as a link to it.
        if (_channel == OptiChannel.Fork)
        {
            ChannelHint.Inlines.Clear();
            if (AppState.Settings.SelectedFork is { } fork)
            {
                var link = new Microsoft.UI.Xaml.Documents.Hyperlink { NavigateUri = new Uri($"https://github.com/{fork}"), UnderlineStyle = Microsoft.UI.Xaml.Documents.UnderlineStyle.None };
                link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"github.com/{fork}" });
                ChannelHint.Inlines.Add(link);
            }
        }
        else ChannelHint.Text = _channel switch
        {
            OptiChannel.Stable => L.T("Tested releases, published less often than nightly builds."),
            OptiChannel.Nightly => L.T("The newest features and fixes, built every day. If a game misbehaves, go back to Stable."),
            _ when b is not null => $"OptiScaler {b.PrettyVersion}"
                + (b.IsComplete ? "" : ". " + L.F("No {0} in this build: taken from the latest nightly at install.", string.Join(L.T(" or "), b.Missing))),
            _ => L.T("Builds you imported, e.g. test builds shared on the OptiScaler Discord. Import a folder or a .zip / .7z / .rar."),
        };
        if (b is not null) ToolTipService.SetToolTip(VersionCombo, $"{b.Name}\n{b.Version}\n{b.PayloadRoot}");
        UpdateNotifyRow();
        UpdateDlssNrRow();
        SyncNotes();
        SetBusy(_busy);
    }

    /// <summary>The Release notes section follows the install and the fork picked (it was built once and kept the first fork).</summary>
    void SyncNotes() => (OptiNotesHost.Child as OptiNotesView)?.Sync();

    // ---------- OptiScaler: the DLSS NR model file (forks and custom builds) ----------

    bool _storingDlssNr;

    /// <summary>
    /// The DLSS 5 row (1.4.0-rc.13): one line saying what this game has (F5 with NGX or the port, DLSSNR-AMD and its network,
    /// or nothing yet) and Set up DLSS 5…, which opens the window with everything else. Shown on a game with OptiScaler, or
    /// while a fork or a custom build is picked.
    /// </summary>
    void UpdateDlssNrRow()
    {
        bool shown = (_channel is OptiChannel.Fork or OptiChannel.Custom || _settings.Install is not null) && Part(PageParts.DlssNr);
        DlssNrRow.Visibility = Shown(shown);
        Dlss5SetupButton.IsEnabled = !_busy;
        if (shown) DlssNrHint.Text = _storingDlssNr ? L.T("Storing the file…") : Dlss5Summary();
        RefreshDlss5();
    }

    void SetDlssNrBackend(string backend)
    {
        AppState.Settings.DlssNrBackend = backend;
        SettingsStore.Save();
        UpdateDlssNrRow();
        _ = SyncForwarderAsync();
    }

    /// <summary>The release this game's OptiScaler install came from, when the page has it listed (for its optional downloads).</summary>
    OptiRelease? InstalledRelease()
    {
        if (_settings.Install is not { } m) return null;
        bool Same(OptiRelease r) => r.Tag == m.Tag && (m.Source is null || string.Equals(r.Repo, m.Source, StringComparison.OrdinalIgnoreCase));
        if (SelectedRelease is { } sel && Same(sel)) return sel;
        try { return Releases.FirstOrDefault(Same); } catch { return null; }
    }

    bool _switchingForwarder;

    /// <summary>
    /// Puts the forwarder the setting asks for into this game's F5 install now (the vendor-neutral port on a card without RTX,
    /// or NGX), instead of waiting for a reinstall. Runs only while the game gets the model file: the port has nothing to run
    /// without it. 1.4.0-rc.11: an install made before the model was stored kept the NGX forwarder, so DLSS 5 never started
    /// on the user's AMD card.
    /// </summary>
    async Task SyncForwarderAsync()
    {
        if (_switchingForwarder || _busy || _game is not { } game || _settings.Install is not { } m || InstalledRelease() is not { } release) return;
        if (DlssNr.Backend(m) is not { } now || DlssNr.PortAddon(release) is null) return;
        bool want = DlssNr.PortWanted(AppState.Settings) && DlssNr.ActiveFor(_settings, AppState.Settings) is not null;
        if ((now == "port") == want) return;
        _switchingForwarder = true;
        try
        {
            bool changed = await Task.Run(() => OptiInstaller.SwitchForwarderAsync(m, release, want, null, _pageCts.Token));
            if (changed)
            {
                SettingsStore.Save();
                App.Window?.ShowSuccess(want
                    ? L.F("{0}: the vendor-neutral DLSS 5 forwarder is in, so the model runs on any DirectX 12 card.", game.Title)
                    : L.F("{0}: NVIDIA's NGX forwarder is back in.", game.Title));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not switch the DLSS 5 forwarder"), ex.Message, ex); }
        finally { _switchingForwarder = false; }
        UpdateOptiStatus();
        UpdateDlssNrRow();
    }

    void SetF5Low(bool on)
    {
        AppState.Settings.F5LowUpdate = on;
        SettingsStore.Save();
        UpdateDlssNrRow();
    }

    /// <summary>On globally: the default for every game. This game follows it (its own exception, if any, is dropped).</summary>
    async Task SetDlssNrGlobalAsync(bool on)
    {
        if (on && !File.Exists(DlssNr.StoredPath) && !await PickDlssNrAsync())
        {
            UpdateDlssNrRow();
            return;
        }
        AppState.Settings.UseDlssNr = on;
        _settings.DlssNrChoice = null;
        SettingsStore.Save();
        await SyncDlssNrAsync();
    }

    /// <summary>On for this game: an exception to On globally while it differs from it; back in line, it follows it again.</summary>
    async Task SetDlssNrGameAsync(bool on)
    {
        if (_game is null) return;
        if (on && !File.Exists(DlssNr.StoredPath) && !await PickDlssNrAsync())
        {
            UpdateDlssNrRow();
            return;
        }
        _settings.DlssNrChoice = on == AppState.Settings.UseDlssNr ? null : on;
        SettingsStore.Save();
        await SyncDlssNrAsync();
    }

    /// <summary>
    /// Hides the DLSS NR row in this game only; this game's gear (Game page parts) shows it again. Hiding it in every game is
    /// Settings → Appearance → Game page, like the other parts.
    /// </summary>
    void DlssNrHide_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.HiddenPageParts.Add(PageParts.DlssNr);
        SettingsStore.Save();
        UpdateDlssNrRow();
        App.Window?.ShowInfo(L.T("DLSS NR row hidden for this game. The game's gear shows it again, Settings → Appearance → Game page hides it in every game; files already set up keep working."));
    }

    async Task ChangeDlssNrAsync()
    {
        if (await PickDlssNrAsync()) await SyncDlssNrAsync();
        else UpdateDlssNrRow();
    }

    /// <summary>Puts the file in or takes it out of this game's install to match its switch now, and says so.</summary>
    async Task SyncDlssNrAsync()
    {
        if (_game is { } game && _settings.Install is { } m)
        {
            var stored = DlssNr.ActiveFor(_settings, AppState.Settings);
            bool had = m.Files.ContainsKey(DlssNr.FileName);
            try
            {
                // A new stored file replaces the copy in the install: take the old one out first.
                bool changed = await Task.Run(() =>
                {
                    OptiInstaller.EnsureNotRunning(m.TargetDir);
                    bool c = false;
                    if (had && stored is not null && m.Files.TryGetValue(DlssNr.FileName, out var hash) && hash != OptiInstaller.Hash(stored)) c = DlssNr.Sync(m, null);
                    return DlssNr.Sync(m, stored) || c;
                });
                SettingsStore.Save();
                bool has = m.Files.ContainsKey(DlssNr.FileName);
                if (changed && has != had) App.Window?.ShowSuccess(has ? L.F("{0} added to the OptiScaler install of {1}.", DlssNr.FileName, game.Title) : L.F("{0} removed from the OptiScaler install of {1}.", DlssNr.FileName, game.Title));
            }
            catch (Exception ex) { App.Window?.ShowError(L.T("Could not update the DLSS NR model file"), ex.Message, ex); }
            UpdateOptiStatus();
            _ = UpdateFootprintAsync();
        }
        UpdateDlssNrRow();
        await SyncForwarderAsync();
    }

    /// <summary>Asks for an nvngx_dlssnr.dll and stores a copy. False when the user cancelled or the file is not DLSS NR.</summary>
    async Task<bool> PickDlssNrAsync()
    {
        var file = await Pickers.PickFileAsync(App.Window!, ".dll");
        if (file is null) return false;
        _storingDlssNr = true;
        UpdateDlssNrRow();
        try
        {
            await DlssNr.StoreAsync(file, _pageCts.Token);
            App.Window?.ShowSuccess(L.F("{0} {1} stored: forks and custom builds that need it get it when installed.", DlssNr.FileName, DlssNr.Version(DlssNr.StoredPath)));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Window?.ShowError(L.T("Could not store the DLSS NR model file"), ex.Message);
            return false;
        }
        finally { _storingDlssNr = false; }
    }

    // ---------- OptiScaler: updates ----------

    bool _fillingNotify;

    /// <summary>The source shown now: "stable", "nightly", "fork:owner/name", or empty (custom builds, no fork chosen).</summary>
    string ShownSource => OptiUpdates.Key(_channel, AppState.Settings.SelectedFork);

    /// <summary>Get notified on updates: for the source shown (each one is turned on by itself, all are off at first).</summary>
    void UpdateNotifyRow()
    {
        var key = ShownSource;
        NotifyRow.Visibility = key.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (key.Length == 0) return;
        _fillingNotify = true;
        try { NotifyToggle.IsOn = OptiUpdates.IsFollowed(AppState.Settings, key); }
        finally { _fillingNotify = false; }
        NotifyHint.Text = L.F("A Windows notification when a new {0} version comes out. Only this one: the other channels and forks are turned on apart.", OptiUpdates.Name(key, AppState.Settings));
    }

    async void NotifyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_fillingNotify) return;
        var key = ShownSource;
        if (key.Length == 0) return;
        try
        {
            await OptiUpdates.SetFollowedAsync(AppState.Settings, key, NotifyToggle.IsOn, _pageCts.Token);
            SettingsStore.Save();
            App.Window?.OptiFollowChanged();
        }
        catch (Exception ex) { Log.Warn($"Notifications for {key}: {ex.Message}"); }
    }

    OptiRelease? _update;
    int _updateCheck;

    /// <summary>
    /// Update available: the installed build's own source (its channel, or its fork) has a newer release. Shown whatever the
    /// notifications say; it looks at the releases already read (cached), so it costs nothing.
    /// </summary>
    async Task RefreshUpdateAvailableAsync()
    {
        int run = ++_updateCheck;
        var m = _settings.Install;
        OptiRelease? newer = null;
        if (m is not null && OptiUpdates.Key(m) is { Length: > 0 } key)
        {
            try { newer = OptiUpdates.NewerThan(m, await OptiUpdates.ReleasesAsync(key, false, _pageCts.Token)); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn($"Update check for {key}: {ex.Message}"); }
        }
        if (run != _updateCheck || _settings.Install != m) return;
        _update = newer;
        UpdateAvailableButton.Visibility = newer is not null ? Visibility.Visible : Visibility.Collapsed;
        if (newer is not null && m is not null)
            ToolTipService.SetToolTip(UpdateAvailableButton, L.F("{0} is out ({1:yyyy-MM-dd}). Click to install it in place of {2}: your settings are applied again.", newer.Tag, newer.Published.ToLocalTime(), m.DisplayVersion));
    }

    /// <summary>Update available: the installed source's newest release is picked and installed, as Install would.</summary>
    async void UpdateAvailable_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _settings.Install is not { } m || _update is null) return;
        try
        {
            if (m.Channel == OptiChannel.Fork && m.Source is { } repo)
            {
                AppState.Settings.SelectedFork = repo;
                FillForkList();
                await LoadForkAsync(false, _pageCts.Token);
            }
            ChannelCard(m.Channel).IsChecked = true;
            _channel = m.Channel;
            FillVersions();
            // Item 0 is Latest: the newest release of the channel.
            if (SelectedRelease is not { } r || !r.Tag.Equals(_update.Tag, StringComparison.OrdinalIgnoreCase)) return;
            StartInstall(manual: false);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not update OptiScaler"), ex.Message, ex); }
    }

    void VersionsRefresh_Click(object sender, RoutedEventArgs e) => _ = VersionsList.ReloadAsync();

    void Releases_Click(object sender, RoutedEventArgs e) =>
        Try(() => GameLauncher.OpenUri(_channel == OptiChannel.Fork && AppState.Settings.SelectedFork is { } fork
            ? OptiReleases.ForkPage(fork)
            : OptiReleases.ReleasesPage(_channel == OptiChannel.Nightly ? OptiChannel.Nightly : OptiChannel.Stable)));

    // ---------- OptiScaler: custom builds ----------

    async Task LoadCustomBuildsAsync(string? select, CancellationToken ct)
    {
        try
        {
            var list = await Task.Run(() => CustomBuilds.ScanAsync(AppState.Settings, ct), ct);
            if (ct.IsCancellationRequested) return;
            _custom = list;
            _selectCustom = select;
            if (_channel == OptiChannel.Custom) FillVersions();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_channel == OptiChannel.Custom) VersionCombo.PlaceholderText = L.T("Can't read the custom builds folder");
            Log.Warn($"Custom builds: {ex.Message}");
        }
        SetBusy(_busy);
    }

    void CustomFolder_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenFolder(CustomBuilds.Dir(AppState.Settings)));
    void CustomRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadCustomBuildsAsync(SelectedCustom?.Name, _pageCts.Token);

    async void ImportFolder_Click(object sender, RoutedEventArgs e) => await ImportBuildAsync(await Pickers.PickFolderAsync(App.Window!));
    async void ImportArchive_Click(object sender, RoutedEventArgs e) => await ImportBuildAsync(await Pickers.PickFileAsync(App.Window!, ".zip", ".7z", ".rar"));

    async Task ImportBuildAsync(string? source)
    {
        if (source is null || _busy) return;
        SetBusy(true);
        VersionCombo.PlaceholderText = L.T("Importing…");
        try
        {
            // The same import as the Auto tab's "Add a build" (2.0.0-rc.18); it says what went wrong itself.
            if (await OptiSourceImport.ImportAsync(source) is { } build)
            {
                CustomCard.IsChecked = true;
                await LoadCustomBuildsAsync(build.Name, _pageCts.Token);
                _ = VersionsList.ReloadAsync();
            }
            else VersionCombo.PlaceholderText = L.T("No builds yet: use Import");
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void RemoveBuild_Click(object sender, RoutedEventArgs e)
    {
        var b = SelectedCustom;
        if (b is null || _busy) return;
        if (!await ConfirmAsync(L.F("Delete \"{0}\"?", b.Name),
                L.F("Deletes this build from the custom builds folder:\n{0}\n\nGames where it is installed keep working and can still be uninstalled.", b.Folder),
                L.T("Delete")))
            return;
        try
        {
            await Task.Run(() => CustomBuilds.Remove(b));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not delete the build"), ex.Message, ex);
        }
        await LoadCustomBuildsAsync(null, _pageCts.Token);
        _ = VersionsList.ReloadAsync();
    }

    // ---------- OptiScaler: setup answers ----------

    /// <summary>
    /// The smart spoofing default of the game shown (rc.11: off where OptiPatcher supports the game) until the user sets it. Kept
    /// on the page, never written by itself (2.0.0-rc.13): writing it into the game's settings on a mere visit rebuilt the
    /// library and every kept page (snappy gate: library 44 -> 120 ms). It goes into the settings with the user's next change
    /// of the setup or with an install.
    /// </summary>
    (string GameId, bool Value)? _smartSpoofing;

    bool EffectiveSpoofing => !_settings.Setup.SpoofingSet && _smartSpoofing is { } smart && smart.GameId == _game?.Id ? smart.Value : _settings.Setup.Spoofing;

    void LoadSetupOptions()
    {
        var o = _settings.Setup;
        DllCombo.SelectedItem = OptiSetupOptions.DllNames.FirstOrDefault(n => n.Equals(o.DllName, StringComparison.OrdinalIgnoreCase)) ?? "dxgi.dll";
        GpuCombo.SelectedIndex = o.Nvidia switch { null => 0, false => 1, true => 2 };
        SpoofToggle.IsOn = EffectiveSpoofing;
        OptiPatcherToggle.IsOn = o.OptiPatcher;
        OldFilesToggle.IsOn = o.DeleteOldFiles;
        bool nvidiaDetected = File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
        GpuHint.Text = L.F("The setup asks which GPU you use. Detected: {0}.", nvidiaDetected ? "Nvidia" : "AMD / Intel");
        UpdateSetupState();
    }

    void SetupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _game is null) return;
        var o = _settings.Setup;
        var dllBefore = o.DllName;
        o.DllName = DllCombo.SelectedItem as string ?? "dxgi.dll";
        // OptiScaler already in this game: a new file name is swapped in at once, the next install keeps it.
        if (!o.DllName.Equals(dllBefore, StringComparison.OrdinalIgnoreCase) && _settings.Install is { } installed
            && !o.DllName.Equals(installed.DllName, StringComparison.OrdinalIgnoreCase) && OptiDetector.Detect(installed.TargetDir) is not null)
        {
            if (OptiDllSwap.Names.Contains(o.DllName, StringComparer.OrdinalIgnoreCase)) _ = SwapDllAsync(o.DllName);
            else App.Window?.ShowInfo(L.F("{0} needs an ASI loader in the game: it is used at the next install.", o.DllName));
        }
        o.Nvidia = GpuCombo.SelectedIndex switch { 1 => false, 2 => true, _ => null };
        if (EffectiveSpoofing != SpoofToggle.IsOn) o.SpoofingSet = true;
        o.Spoofing = SpoofToggle.IsOn;
        o.OptiPatcher = OptiPatcherToggle.IsOn;
        o.DeleteOldFiles = OldFilesToggle.IsOn;
        SettingsStore.Save();
        UpdateSetupState();
        SetBusy(_busy);
    }

    /// <summary>The names OptiScaler can be renamed to here, each with what it suits and why a taken one cannot be used.</summary>
    void ShowDllSwapMenu()
    {
        if (_settings.Install is not { } m || _busy) return;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Load OptiScaler as…"), IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var name in OptiDllSwap.Names)
        {
            var why = OptiDllSwap.WhyNot(m, name);
            bool current = name.Equals(m.DllName, StringComparison.OrdinalIgnoreCase);
            var hint = OptiDllSwap.Hint(name);
            var item = new ToggleMenuFlyoutItem
            {
                Text = name + (why is not null && !current ? $"  ·  {why}" : hint.Length > 0 ? $"  ·  {hint}" : ""),
                IsChecked = current,
                IsEnabled = why is null,
            };
            var chosen = name;
            item.Click += (_, _) => _ = SwapDllAsync(chosen);
            menu.Items.Add(item);
        }
        menu.ShowAt(OptiDllChip, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    /// <summary>Renames OptiScaler's dll in place (no reinstall); the setup's choice follows, so the next install keeps the name.</summary>
    async Task SwapDllAsync(string name)
    {
        if (_game is not { } game || _settings.Install is not { } m) return;
        var settings = _settings;
        if (_busy || Helpers.GameJobs.Busy(game.Id))
        {
            App.Window?.ShowInfo(L.T("Wait for the running install or update to finish."));
            return;
        }
        if (OptiInstaller.RunningGameProcess(m.TargetDir) is { } running)
        {
            App.Window?.ShowError(L.T("Close the game first"), L.F("{0} is running from this folder.", running));
            ResetDllCombo(settings);
            return;
        }
        var old = m.DllName;
        try
        {
            await Task.Run(() => OptiDllSwap.Swap(m, name));
            settings.Setup.DllName = name;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("OptiScaler is now {0} in {1} (was {2}). Nothing was reinstalled.", name, game.Title, old));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.F("Could not rename OptiScaler to {0}", name), ex.Message, ex);
            settings.Setup.DllName = m.DllName;
            SettingsStore.Save();
        }
        if (_game != game) return;
        ResetDllCombo(settings);
        UpdateOptiStatus();
        _ = UpdateModBadgesAsync();
        if (_reshadeView is { } r) Ui.Safe(r.Refresh);
        if (_dxvkView is { } d) Ui.Safe(d.Refresh);
        if (_vkd3dView is { } v) Ui.Safe(v.Refresh);
    }

    void ResetDllCombo(GameSettings settings)
    {
        if (settings != _settings) return;
        bool was = _loading;
        _loading = true;
        DllCombo.SelectedItem = OptiSetupOptions.DllNames.FirstOrDefault(n => n.Equals(settings.Setup.DllName, StringComparison.OrdinalIgnoreCase)) ?? "dxgi.dll";
        _loading = was;
        UpdateSetupState();
    }

    void UpdateSetupState()
    {
        var o = _settings.Setup;
        bool nvidia = o.Nvidia ?? File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
        SpoofToggle.IsEnabled = !nvidia;
        OptiPatcherToggle.IsEnabled = !nvidia;
        // The summary in colour (2.0.0): the file name stands out, and a gold star beside OptiPatcher when it supports this
        // game, which is why spoofing starts off.
        var runs = new List<Microsoft.UI.Xaml.Documents.Inline>();
        void Part(string text, Windows.UI.Color? tint = null, bool strong = false)
        {
            if (runs.Count > 0) runs.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  ·  " });
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = text };
            if (tint is { } c) run.Foreground = new SolidColorBrush(c);
            if (strong) run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            runs.Add(run);
        }
        Part(o.DllName, strong: true);
        Part(nvidia ? "Nvidia" : "AMD / Intel");
        bool starred = false;
        if (!nvidia)
        {
            Part(EffectiveSpoofing ? L.T("spoofing on") : L.T("spoofing off"));
            if (o.OptiPatcher && _patcherSupported == true)
            {
                Part("★ ", Ui.Gold);
                runs.Add(new Microsoft.UI.Xaml.Documents.Run { Text = L.T("OptiPatcher supports this game"), Foreground = new SolidColorBrush(Ui.Gold) });
                starred = true;
            }
            else Part(o.OptiPatcher ? L.T("OptiPatcher if supported") : L.T("no OptiPatcher"));
        }
        SetupSummary.Inlines.Clear();
        foreach (var r in runs) SetupSummary.Inlines.Add(r);
        ToolTipService.SetToolTip(SetupSummary, starred && !EffectiveSpoofing
            ? L.T("Spoofing is off because OptiPatcher supports this game: it opens the upscaler options without spoofing.")
            : null);
    }

    /// <summary>Whether OptiPatcher supports the shown game (null: not known yet or the list could not be read).</summary>
    bool? _patcherSupported;

    async Task UpdateOptiPatcherHintAsync(CancellationToken ct)
    {
        var target = TargetDir;
        if (target is null) return;
        _patcherSupported = null;
        var supported = await OptiPatcherSupport.IsSupportedAsync(target, ct);
        if (ct.IsCancellationRequested) return;
        _patcherSupported = supported;
        // Smart default (rc.11): spoofing off for a game OptiPatcher supports, on for the others, until the user sets it.
        var o = _settings.Setup;
        if (supported is { } s && !o.SpoofingSet && o.OptiPatcher && _game is { } shown && EffectiveSpoofing == s)
        {
            _smartSpoofing = (shown.Id, !s);
            bool was = _loading;
            _loading = true;
            SpoofToggle.IsOn = !s;
            _loading = was;
            UpdateSetupState();
        }
        OptiPatcherHint.Text = supported switch
        {
            true => L.T("✔ This game is supported: OptiPatcher unlocks DLSS / DLSS-FG inputs without spoofing. Asked only on AMD / Intel."),
            false => L.T("This game is not in OptiPatcher's list, so the setup will skip it. Asked only on AMD / Intel."),
            _ => L.T("Unlocks DLSS / DLSS-FG inputs without spoofing in supported games (could not check the list now)."),
        };
        // The summary's star follows the answer.
        UpdateSetupState();
    }

    // ---------- OptiScaler: install / uninstall ----------

    void Install_Click(object sender, RoutedEventArgs e) => StartInstall(manual: false);

    /// <summary>
    /// The Auto tab's OptiScaler step: the build picked there, with the plan's file name and spoofing (saved as this game's
    /// setup options, so the OptiScaler tab shows them), through the same install as the Install button. True when it is in.
    /// </summary>
    async Task<bool> IAutoHost.AutoInstallOptiAsync(OptiRelease release, string dllName, bool spoofing)
    {
        if (_game is null) return false;
        var settings = _settings;
        settings.Setup.DllName = dllName;
        settings.Setup.Spoofing = spoofing;
        settings.Setup.OptiPatcher = true;
        SettingsStore.Save();
        ResetDllCombo(settings);
        await InstallAsync(release, manual: false);
        return settings.Install is { } m && File.Exists(Path.Combine(m.TargetDir, m.DllName));
    }

    async Task<bool> IAutoHost.AutoRenameOptiAsync(string name)
    {
        await SwapDllAsync(name);
        return _settings.Install?.DllName.Equals(name, StringComparison.OrdinalIgnoreCase) == true;
    }

    async Task<bool> IAutoHost.AutoPrepareAmdAsync()
    {
        if (DlssNrAmd.Stage() is null) await PrepareAmdAsync();
        return DlssNrAmd.Stage() is not null;
    }

    async Task<bool> IAutoHost.AutoPutAmdAsync()
    {
        await PutAmdAsync();
        return _settings.Install?.DlssNrAmd is not null;
    }

    async Task<bool> IAutoHost.AutoPutAmdReShadeAsync()
    {
        await PutAmdReShadeAsync();
        return _settings.DlssNrAmdReShade is not null;
    }

    Task IAutoHost.AutoFinishF5Async() => Dlss5SetUpAsync(Dlss5.F5);

    Task<bool> IAutoHost.AutoPickDlssNrAsync() => PickDlssNrAsync();

    /// <summary>Standard .bat install: same backups and uninstall, but setup_windows.bat opens in its own window for the user to answer.</summary>
    void BatInstall_Click(object sender, RoutedEventArgs e) => StartInstall(manual: true);

    void StartInstall(bool manual)
    {
        if (_channel == OptiChannel.Custom && SelectedCustom is null)
        {
            App.Window?.ShowInfo(L.T("Import a build first: Custom build → Import → from a folder or an archive."));
            return;
        }
        _ = InstallAsync(SelectedRelease, manual);
    }

    async Task InstallAsync(OptiRelease? release, bool manual)
    {
        if (_busy || _game is null) return;
        var game = _game;
        var settings = _settings;

        // DLSSNR-AMD's ReShade way has its own ReShade as dxgi.dll (1.4.0-rc.20): OptiScaler would go over it.
        if (settings.DlssNrAmdReShade is not null)
        {
            App.Window?.ShowError(L.T("DLSSNR-AMD is in through ReShade"), L.T("Take it out first (Mods → DLSS 5 → DLSSNR-AMD), then install OptiScaler. With OptiScaler, DLSSNR-AMD can go in as its plugin."));
            return;
        }
        if (release is null)
        {
            App.Window?.ShowError(L.T("No version available"), L.T("Hybrid Launcher could not list OptiScaler releases. Check the connection and try again."));
            _ = LoadReleasesAsync(true, _pageCts.Token);
            return;
        }
        var target = TargetDir;
        if (target is null)
        {
            App.Window?.ShowError(L.T("No game exe"), L.T("Pick the game exe first (the gear beside Play → Change game exe)."));
            return;
        }

        if (!OptiInstaller.CanWrite(target))
        {
            if (await AskElevateAsync(target)) Elevation.RestartElevated();
            return;
        }

        // A DLSS NR build (janblade's F5 and the like): the model file it needs, asked for now when it is missing or too old.
        var (go, nrDll) = await PrepareDlssNrAsync(release);
        if (!go) return;

        if (await ModBackups.DecideAsync("OptiScaler", game.Title) is not { } backup) return;

        // With the bat installer the file name is picked in the script, which warns about existing files itself.
        var options = settings.Setup.Clone();
        options.Manual = manual;
        // The smart default shown on the page goes in with the install, and is kept from now on.
        if (!settings.Setup.SpoofingSet && ReferenceEquals(settings, _settings) && settings.Setup.Spoofing != EffectiveSpoofing)
        {
            settings.Setup.Spoofing = options.Spoofing = EffectiveSpoofing;
            SettingsStore.Save();
        }
        // Ultra+ loads UE4SS through a proxy dll beside the exe: OptiScaler under that name would stop it, and removing Ultra+
        // later would take OptiScaler's file with it.
        if (!manual && settings.UltraPlus is { } ultra && Core.Mods.UltraPlus.Owns(ultra, Path.Combine(target, settings.Setup.DllName)))
        {
            App.Window?.ShowError(L.T("Ultra+ uses that file name"), L.F("Ultra+ loads through {0} in this game. Pick another OptiScaler file name in Setup options (dxgi.dll works for most games), then install.", settings.Setup.DllName));
            return;
        }
        var conflict = manual ? null : OptiDetector.DescribeConflict(target, settings.Setup.DllName);
        if (conflict is not null && !await ConfirmAsync(
                L.T("Another file has that name"),
                conflict + "\n\n" + (backup
                    ? L.T("OptiScaler would replace it, so that mod would stop working while OptiScaler is installed. Hybrid Launcher keeps a backup and puts it back when you uninstall.")
                    : L.T("OptiScaler would replace it without a backup (as chosen in Settings → Features): uninstalling OptiScaler will not bring it back.")) +
                "\n\n" + L.T("Tip: pick a different OptiScaler file name in Setup options (e.g. winmm.dll) to keep both."),
                backup ? L.T("Replace (keep a backup)") : L.T("Replace")))
            return;

        // Kept outside the page (GameJobs): leaving the game and coming back shows this install, not the one before it, and
        // nothing else can be installed in the folder until it ends.
        var job = GameJobs.TryStart(game.Id, game.Title, "opti", "OptiScaler");
        if (job is null) return;
        var progress = new Progress<InstallProgress>(p =>
            GameJobs.Report(job, p.Fraction > 0 ? p.Fraction : -1, p.Stage, p.Line ?? $"[{p.Fraction:P0}] {p.Stage}"));

        try
        {
            var manifest = await Task.Run(() => OptiInstaller.InstallAsync(new InstallRequest
            {
                GameId = game.Id,
                TargetDir = target,
                Release = release,
                Options = options,
                IniOverrides = new Dictionary<string, string>(settings.IniOverrides),
                AsiPlugins = AppState.EnabledAsiPlugins(settings),
                CustomDlls = AppState.EnabledCustomDlls(settings),
                Previous = settings.Install,
                Backup = backup,
                DlssNrDll = nrDll,
                Addons = DlssNr.AddonsFor(release, AppState.Settings, nrDll is not null),
            }, progress, job.Token));

            settings.Install = manifest;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("OptiScaler {0} installed in {1} as {2}.", manifest.DisplayVersion, game.Title, manifest.DllName));
            // DLSSNR-AMD chosen for this game: back over the new install (its dlssnr-amd.ini as it was).
            if (settings.Dlss5Method == Dlss5.Amd) await ReapplyDlssNrAmdAsync(game, settings, manifest);
        }
        catch (OperationCanceledException)
        {
            // The previous install (if any) was already removed when cancel arrives late; reflect what is on disk.
            if (settings.Install is not null && !File.Exists(Path.Combine(settings.Install.TargetDir, settings.Install.DllName))) settings.Install = null;
            SettingsStore.Save();
            App.Window?.ShowInfo(L.T("Install cancelled. The game folder was left as it was."));
        }
        catch (InstallException ex)
        {
            if (ex.Transcript is not null) GameJobs.Report(job, -1, null, "\n----- " + L.T("setup_windows.bat output") + " -----\n" + ex.Transcript);
            job.Failed = true;
            if (settings.Install is not null && !File.Exists(Path.Combine(settings.Install.TargetDir, settings.Install.DllName))) settings.Install = null;
            SettingsStore.Save();
            App.Window?.ShowError(L.T("Install failed"), ex.Message, ex);
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            job.Failed = true;
            GameJobs.Report(job, -1, null, ex.ToString());
            if (settings.Install is not null && !File.Exists(Path.Combine(settings.Install.TargetDir, settings.Install.DllName))) settings.Install = null;
            SettingsStore.Save();
            App.Window?.ShowError(L.T("Install failed"), ex.Message, ex);
        }
        finally
        {
            // Whichever page shows this game now (this one, or one made since) redraws from here: see SyncJob.
            GameJobs.End(job);
        }
    }

    void OnJobsChanged(string gameId)
    {
        if (_game is not null && gameId.Equals(_game.Id, StringComparison.OrdinalIgnoreCase)) Ui.Safe(SyncJob);
    }

    void OnJobProgress(GameJobs.Job job)
    {
        if (_game is null || !job.GameId.Equals(_game.Id, StringComparison.OrdinalIgnoreCase)) return;
        Ui.Safe(() =>
        {
            switch (job.Kind)
            {
                case "opti": ShowOptiProgress(job); break;
                case "reshade": _reshadeView?.ShowJob(job); break;
                case "renodx": _renoDxView?.ShowJob(job); break;
                case "luma": _lumaView?.ShowJob(job); break;
                case "dxvk": _dxvkView?.ShowJob(job); _vkd3dView?.ShowJob(job); break;
                case "ultra": _ultra?.ShowJob(job); break;
            }
        });
    }

    /// <summary>
    /// The page follows GameJobs: while something is installed or removed in this game every control that would start another
    /// change is off, OptiScaler's own job shows its progress and log, and when it ends the page shows what is installed now
    /// (its channel picked again, so a page opened mid-install never keeps showing the build that was there before).
    /// </summary>
    void SyncJob()
    {
        if (_game is null) return;
        var job = GameJobs.For(_game.Id);
        SetBusy(false);
        if (job is { Kind: "opti" })
        {
            if (!ReferenceEquals(_shownJob, job))
            {
                _shownJob = job;
                InstallProgressBar.IsIndeterminate = false;
                InstallProgressBar.Value = 0;
                LogExpander.Visibility = Visibility.Visible;
                ProgressPanel.Visibility = Visibility.Visible;
            }
            ShowOptiProgress(job);
        }
        else if (_shownJob is { } done)
        {
            _shownJob = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            LogText.Text = done.Text.ToString();
            if (done.Failed) LogExpander.IsExpanded = true;
            if (_settings.Install is { } m && !ReferenceEquals(ChannelCard(m.Channel), ChannelCard(_channel)))
            {
                if (m.Channel == OptiChannel.Fork && m.Source is { } repo) AppState.Settings.SelectedFork = repo;
                ChannelCard(m.Channel).IsChecked = true;
                _channel = m.Channel;
            }
            Refresh();
            _ = VersionsList.ReloadAsync();
        }
        // The mod views lock or unlock their buttons, and redraw what a finished job changed.
        if (_reshadeView is { } r) Ui.Safe(r.Refresh);
        if (_renoDxView is { } x) Ui.Safe(x.Refresh);
        if (_lumaView is { } l) Ui.Safe(l.Refresh);
        if (_ultra is { } u)
        {
            Ui.Safe(() => u.ShowJob(job));
            if (job is null) Ui.Safe(u.Refresh);
        }
    }

    /// <summary>Self-test: the Play button's width in every state (it must not stretch with a long word), then back to its own.</summary>
    internal string SelfTestPlayWidths()
    {
        var before = PlayButton.Current;
        var widths = new List<string>();
        foreach (var mode in Enum.GetValues<Controls.PlayButton.Mode>())
        {
            PlayButton.SetMode(mode);
            PlayButton.UpdateLayout();
            // Between the narrowest face and the one its word needs (a longer word widens the button, never shrinks the word).
            if (PlayButton.ActualWidth > PlayButton.MaxWidth + 1 || PlayButton.ActualWidth < 191) throw new InvalidOperationException($"{mode}: {PlayButton.ActualWidth:F0} px (max {PlayButton.MaxWidth:F0})");
            // Every word at Play's size, all of it showing: never smaller, squeezed or cut.
            var (size, whole, detail) = PlayButton.SelfTestLabel();
            if (size != 24) throw new InvalidOperationException($"{mode}: the word is {size} px, Play's is 24");
            if (!whole) throw new InvalidOperationException($"{mode}: the word does not fit in the button: {detail}");
            // Icon and word centred together on the face (2.0.0-rc.5), no blank beside them.
            if (PlayButton.SelfTestWordOffCentre() is var off && Math.Abs(off) > 1.5) throw new InvalidOperationException($"{mode}: the word is {off:F1} px off the button's centre");
            widths.Add($"{mode} {PlayButton.ActualWidth:F0}");
        }
        PlayButton.SetMode(before);
        return string.Join(", ", widths);
    }

    /// <summary>Self-test: what the page shows of a running job.</summary>
    internal (bool Busy, bool InstallEnabled, bool Progress, string Stage, string Log) SelfTestJob() =>
        (_busy, InstallButton.IsEnabled, ProgressPanel.Visibility == Visibility.Visible, ProgressStage.Text, LogText.Text);

    void ShowOptiProgress(GameJobs.Job job)
    {
        ProgressStage.Text = job.Stage;
        if (job.Fraction > 0) InstallProgressBar.Value = job.Fraction;
        LogText.Text = job.Text.ToString();
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight + 1000, null, disableAnimation: true);
    }

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var m = _settings.Install;
        if (m is null || _busy) return;
        if (!await ConfirmAsync(L.T("Uninstall OptiScaler?"),
                L.F("Removes the {0} files Hybrid Launcher installed in\n{1}\nand restores the {2} original file(s) it backed up. Your setup answers and ini choices stay saved.", m.Files.Count, m.TargetDir, m.Backups.Count),
                L.T("Uninstall")))
            return;

        var game = _game!;
        var job = GameJobs.TryStart(game.Id, game.Title, "opti", "OptiScaler");
        if (job is null) return;
        var settings = _settings;
        try
        {
            var warnings = await Task.Run(() => OptiInstaller.Uninstall(m));
            settings.Install = null;
            SettingsStore.Save();
            if (warnings.Count > 0) App.Window?.ShowInfo(L.T("OptiScaler removed.") + " " + string.Join(" ", warnings));
            else App.Window?.ShowSuccess(L.T("OptiScaler removed and the original files restored."));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Uninstall failed"), ex.Message, ex);
        }
        finally
        {
            GameJobs.End(job);
            Refresh();
            _ = VersionsList.ReloadAsync();
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        GameJobs.For(_game?.Id)?.Cts.Cancel();
        CancelButton.IsEnabled = false;
    }

    void SetBusy(bool busy)
    {
        // A change running in this game's folder (GameJobs) keeps the page busy, whatever else asked.
        busy |= GameJobs.Busy(_game?.Id);
        bool changed = _busy != busy;
        _busy = busy;
        // DLSS 5 reads OptiScaler from the folder and greys its buttons while the page is busy: drawn again once an install,
        // an update or an uninstall ends (or marked to be, when the Mods tab is not open).
        if (changed) RefreshDlss5();
        InstallButton.IsEnabled = BatInstallButton.IsEnabled = !busy && SelectedRelease is not null;
        StableCard.IsEnabled = NightlyCard.IsEnabled = ForkCard.IsEnabled = CustomCard.IsEnabled = VersionCombo.IsEnabled = ForkCombo.IsEnabled = !busy;
        AddForkButton.IsEnabled = UpdateAvailableButton.IsEnabled = !busy;
        RemoveForkButton.IsEnabled = !busy && ForkCombo.SelectedItem is ForkItem { User: true };
        ImportBuildButton.IsEnabled = CustomMoreButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        SetupExpander.IsEnabled = !busy;
        Dlss5SetupButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        var (label, hint) = InstallLabel();
        InstallButtonText.Text = label;
        InstallHint.Text = hint;
        ToolTipService.SetToolTip(InstallHint, hint.Length > 0 ? hint : null);
        InstallHintPanel.Visibility = hint.Length > 0 && !busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the install button does with the chosen version: install, reinstall, update or switch channel.</summary>
    (string Label, string Hint) InstallLabel()
    {
        var m = _settings.Install;
        var r = SelectedRelease;
        if (r is null) return (m is null ? L.T("Install OptiScaler") : L.T("Install"), "");
        string version = r.Channel switch
        {
            OptiChannel.Custom => L.F("the custom build \"{0}\"", r.AssetName),
            OptiChannel.Nightly => L.F("nightly {0}", r.Tag),
            OptiChannel.Fork => $"{r.Repo} {r.Tag}",
            _ => $"OptiScaler {r.Tag}",
        };
        if (m is null)
            return (L.T("Install OptiScaler"), L.F("Installs {0}. Install runs OptiScaler's own setup script silently with your setup options; Standard .bat install opens it so you answer it yourself. Every file it replaces is backed up, and uninstall puts the folder back as it was.", version));
        bool same = m.Channel == r.Channel && (r.Channel == OptiChannel.Custom
            ? m.AssetName.Equals(r.AssetName, StringComparison.OrdinalIgnoreCase)
            : m.Tag.Equals(r.Tag, StringComparison.OrdinalIgnoreCase) && (r.Channel != OptiChannel.Fork || string.Equals(m.Source, r.Repo, StringComparison.OrdinalIgnoreCase)));
        if (same) return (L.T("Reinstall"), L.F("{0} is installed. Reinstalling keeps your settings.", m.DisplayVersion));
        if (m.Channel == r.Channel && (r.Channel != OptiChannel.Fork || string.Equals(m.Source, r.Repo, StringComparison.OrdinalIgnoreCase))) return (L.T("Update"), L.F("Replaces {0} with {1}. Your settings are applied again.", m.DisplayVersion, version));
        var name = r.Channel switch { OptiChannel.Nightly => "Nightly", OptiChannel.Custom => L.T("custom build"), OptiChannel.Fork => L.T("a fork"), _ => "Stable" };
        return (L.F("Switch to {0}", name), L.F("Replaces {0} with {1}. Your settings are applied again.", m.DisplayVersion, version));
    }

    void AppendLog(string line)
    {
        LogText.Text += line + "\n";
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight + 1000, null, disableAnimation: true);
    }

    /// <summary>Asks which app to open it with every time (Windows' "Open with" picker), so no default app is set or changed.</summary>
    void OpenIni_Click(object sender, RoutedEventArgs e) =>
        Try(() => GameLauncher.OpenWith(Path.Combine(TargetDir!, "OptiScaler.ini")));

    async Task<bool> AskElevateAsync(string target)
    {
        if (Elevation.IsAdmin)
        {
            App.Window?.ShowError(L.T("Folder is read-only"), L.F("Even as administrator Hybrid Launcher cannot write to {0}.", target));
            return false;
        }
        return await ConfirmAsync(L.T("Administrator rights needed"),
            L.F("Windows does not let Hybrid Launcher write to\n{0}\n\nRestart Hybrid Launcher as administrator and try again?", target), L.T("Restart as administrator"));
    }

    /// <summary>
    /// Before installing a DLSS NR build: the model file goes in unless this game said no (rc.10: it only went in with "On
    /// globally" ticked, so a fork installed on its own never got it and DLSS 5 did not start). With no stored file, or one
    /// too old for the vendor-neutral port this PC needs (AMD and Intel: 310.9 or newer), the user picks one or goes on
    /// without. False: cancelled.
    /// </summary>
    async Task<(bool Go, string? NrDll)> PrepareDlssNrAsync(OptiRelease release)
    {
        bool nrBuild = $"{release.Repo} {release.AssetName}".Contains("dlssnr", StringComparison.OrdinalIgnoreCase) || DlssNr.PortAddon(release) is not null;
        if (!nrBuild) return (true, DlssNr.ActiveFor(_settings, AppState.Settings));
        if (_settings.DlssNrChoice == false) return (true, null);
        bool port = DlssNr.PortWanted(AppState.Settings) && DlssNr.PortAddon(release) is not null;
        var gpu = Core.Hardware.HardwareInfo.Gpus().FirstOrDefault(g => g.Vendor != Core.Hardware.GpuVendor.Other)?.Name ?? L.T("this PC's card");
        for (int round = 0; round < 3; round++)
        {
            var stored = File.Exists(DlssNr.StoredPath) ? DlssNr.StoredPath : null;
            string? ask = null;
            if (stored is null)
                ask = L.F("This build runs DLSS 5 neural rendering with NVIDIA's {0}, which no build may include: without it DLSS 5 does not start.", DlssNr.FileName)
                    + (port ? "\n\n" + L.F("On {0} the vendor-neutral port runs it, and it needs version {1} or newer.", gpu, DlssNr.PortMinimum) : "");
            else if (port && !DlssNr.PortCanRun(stored))
                ask = L.F("Your {0} is version {1}. The vendor-neutral port that runs DLSS 5 on {2} works only with {3} or newer, so DLSS 5 will not start with this one.",
                    DlssNr.FileName, DlssNr.Version(stored), gpu, DlssNr.PortMinimum);
            if (ask is null)
            {
                // Going in with this install: the game follows it from now on, whatever On globally says.
                if (!AppState.Settings.UseDlssNr && _settings.DlssNrChoice != true) { _settings.DlssNrChoice = true; SettingsStore.Save(); }
                return (true, stored);
            }
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.T("DLSS 5 model file"),
                Content = new TextBlock { Text = ask, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = L.F("Pick {0}…", DlssNr.FileName),
                SecondaryButtonText = stored is null ? L.T("Install without it") : L.T("Install anyway"),
                CloseButtonText = L.T("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ActualTheme,
            };
            var answer = await dialog.ShowAsync();
            if (answer == ContentDialogResult.None) return (false, null);
            if (answer == ContentDialogResult.Secondary)
            {
                if (stored is not null && !AppState.Settings.UseDlssNr && _settings.DlssNrChoice != true) { _settings.DlssNrChoice = true; SettingsStore.Save(); }
                return (true, stored);
            }
            await PickDlssNrAsync();
        }
        return (true, File.Exists(DlssNr.StoredPath) ? DlssNr.StoredPath : null);
    }

    async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme,
        };
        // Demo walk only (HL_DEMO): the question stays on screen a moment, then is answered yes.
        bool demoYes = false;
        if (MainWindow.DemoAnswerAfter is { } after && App.Window is { } window) _ = window.DemoAnswerAsync(dialog, after, () => demoYes = true);
        return await dialog.ShowAsync() == ContentDialogResult.Primary || demoYes;
    }

    // ---------- ASI plugins ----------

    void LoadAsiList()
    {
        AsiList.Children.Clear();
        var files = AppState.AsiPluginFiles();
        AsiEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var on = AppState.AsiOn(_settings);
        int enabledCount = files.Count(on.Contains);
        AsiSummary.Text = files.Count == 0 ? L.T("None in the folder") : L.F("{0} of {1} on", enabledCount, files.Count);
        foreach (var name in files)
        {
            long size = 0;
            try { size = new FileInfo(Path.Combine(AppInfo.AsiPluginsDir, name)).Length; } catch { }
            var toggle = new ToggleSwitch
            {
                IsOn = on.Contains(name),
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Toggled += async (_, _) =>
            {
                await AsiToggledAsync(name, toggle.IsOn);
                int count = AppState.EnabledAsiPlugins(_settings).Count;
                AsiSummary.Text = L.F("{0} of {1} on", count, AppState.AsiPluginFiles().Count);
            };
            var bin = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 }, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["IconButton"] };
            ToolTipService.SetToolTip(bin, L.T("Delete this plugin (from the shared folder and from every game that uses it)"));
            bin.Click += async (_, _) => await DeleteAsiAsync(name);
            // A name of the user's own, shown in Hybrid Launcher only: the file on disk keeps its name (shown muted under it).
            var file = name;
            bool renamed = AppState.Settings.AsiNames.TryGetValue(file, out var custom) && custom.Length > 0;
            var rename = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["IconButton"] };
            ToolTipService.SetToolTip(rename, L.T("Rename (only in Hybrid Launcher: the file keeps its name)"));
            rename.Click += async (_, _) =>
            {
                var picked = await NameDialog.AskAsync(XamlRoot, ActualTheme, L.T("Rename this plugin"), file, AppState.Settings.AsiNames.GetValueOrDefault(file, ""));
                if (picked is null) return;
                if (picked.Length == 0 || picked == file) AppState.Settings.AsiNames.Remove(file);
                else AppState.Settings.AsiNames[file] = picked;
                SettingsStore.Save();
                LoadAsiList();
            };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(toggle);
            controls.Children.Add(rename);
            controls.Children.Add(bin);
            AsiList.Children.Add(Row(renamed ? custom! : name, (renamed ? name + "  ·  " : "") + $"{size / 1024.0:0.#} KB", controls));
        }
    }

    /// <summary>Removes a plugin from the shared folder; it first leaves the OptiScaler installs of the games that had it on.</summary>
    async Task DeleteAsiAsync(string name)
    {
        var users = AppState.Settings.Games.Values.Where(g => AppState.AsiOn(g).Contains(name)).ToList();
        if (!await ConfirmAsync(L.F("Delete {0}?", name),
                L.T("Deletes the plugin from Hybrid Launcher's shared ASI folder. Every game that has it on loses it from its OptiScaler install."),
                L.T("Delete")))
            return;
        try
        {
            foreach (var g in users) AppState.AsiOn(g).Remove(name);
            var errors = await SyncAsiAsync(users);
            await Task.Run(() => File.Delete(Path.Combine(AppInfo.AsiPluginsDir, name)));
            AppState.Settings.AsiNames.Remove(name);
            SettingsStore.Save();
            if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
            else App.Window?.ShowSuccess(L.F("{0} deleted.", name));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not delete the plugin"), ex.Message, ex);
        }
        LoadAsiList();
    }

    /// <summary>Turns a plugin on or off for this game only, and brings its OptiScaler install in line when there is one.</summary>
    async Task AsiToggledAsync(string name, bool on)
    {
        if (on) AppState.AsiOn(_settings).Add(name);
        else AppState.AsiOn(_settings).Remove(name);
        SettingsStore.Save();
        var errors = await SyncAsiAsync([_settings]);
        if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
        else if (_settings.Install is null) App.Window?.ShowInfo(on ? L.F("{0} will be copied in when OptiScaler is installed in {1}.", name, _game?.Title) : L.F("{0} is off for {1}.", name, _game?.Title));
        else App.Window?.ShowSuccess(on ? L.F("{0} added to the OptiScaler install of {1}.", name, _game?.Title) : L.F("{0} removed from the OptiScaler install of {1}.", name, _game?.Title));
        _iniStale = true;
        if (IniOpen) LoadIni();
    }

    /// <summary>Copies each game's plugins into its OptiScaler install and takes out the ones turned off. Returns the failures.</summary>
    static async Task<List<string>> SyncAsiAsync(IEnumerable<GameSettings> games)
    {
        var work = games.Where(g => g.Install is not null).Select(g => (g.Install!, AppState.EnabledAsiPlugins(g))).ToList();
        if (work.Count == 0) return [];
        var errors = await Task.Run(() =>
        {
            var errs = new List<string>();
            foreach (var (m, enabled) in work)
            {
                try { OptiInstaller.SyncAsiPlugins(m, enabled); }
                catch (Exception ex) { errs.Add($"{Path.GetFileName(m.TargetDir)}: {ex.Message}"); }
            }
            return errs;
        });
        SettingsStore.Save();
        return errors;
    }

    void AsiOpen_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenFolder(AppInfo.AsiPluginsDir));

    /// <summary>Copies picked .asi files into the shared folder. They arrive turned off; a file with the same name is replaced after asking.</summary>
    async void AsiAdd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = await Pickers.PickFilesAsync(App.Window!, ".asi");
            if (files.Count == 0) return;
            Directory.CreateDirectory(AppInfo.AsiPluginsDir);
            var added = new List<string>();
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var dest = Path.Combine(AppInfo.AsiPluginsDir, name);
                if (Path.GetFullPath(file).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(dest) && !await ConfirmAsync(L.F("Replace {0}?", name), L.T("A plugin with this name is already in the shared folder. Replace it with the one you picked?"), L.T("Replace")))
                    continue;
                await Task.Run(() => File.Copy(file, dest, overwrite: true));
                added.Add(name);
            }
            LoadAsiList();
            if (added.Count == 0) return;
            // A replaced plugin goes again into the installs of the games that have it on.
            var users = AppState.Settings.Games.Values.Where(g => added.Any(AppState.AsiOn(g).Contains)).ToList();
            var errors = await SyncAsiAsync(users);
            if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
            else App.Window?.ShowSuccess(L.F("Added {0}.", string.Join(", ", added)) + " " + L.T(added.Count == 1 ? "Turn it on for each game that should load it." : "Turn them on for each game that should load them."));
            _iniStale = true;
            if (IniOpen) LoadIni();
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not add the plugin"), ex.Message, ex);
        }
    }
    void AsiRefresh_Click(object sender, RoutedEventArgs e) => LoadAsiList();

    // ---------- OptiScaler.ini profiles ----------

    void ProfilesFlyout_Opening(object sender, object e)
    {
        ProfilesFlyout.Items.Clear();
        var save = new MenuFlyoutItem { Text = L.T("Save current settings as a profile…"), Icon = new FontIcon { Glyph = "" } };
        save.Click += async (_, _) => await SaveProfileAsync(null);
        ProfilesFlyout.Items.Add(save);
        ProfilesFlyout.Items.Add(new MenuFlyoutSeparator());

        var profiles = AppState.Settings.IniProfiles;
        if (profiles.Count == 0)
            ProfilesFlyout.Items.Add(new MenuFlyoutItem { Text = L.T("No profiles yet"), IsEnabled = false });
        foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var sub = new MenuFlyoutSubItem { Text = $"{profile.Name}  ({profile.Values.Count})", Icon = new FontIcon { Glyph = "" } };
            var apply = new MenuFlyoutItem { Text = L.T("Apply to this game"), Icon = new FontIcon { Glyph = "" } };
            apply.Click += async (_, _) => await ApplyProfileAsync(profile);
            var update = new MenuFlyoutItem { Text = L.T("Replace with current settings"), Icon = new FontIcon { Glyph = "" } };
            update.Click += async (_, _) => await SaveProfileAsync(profile);
            var delete = new MenuFlyoutItem { Text = L.T("Delete"), Icon = new FontIcon { Glyph = "" } };
            delete.Click += async (_, _) =>
            {
                if (!await ConfirmAsync(L.F("Delete the profile \"{0}\"?", profile.Name), L.T("Games keep their current settings; only the saved profile goes."), L.T("Delete"))) return;
                AppState.Settings.IniProfiles.Remove(profile);
                SettingsStore.Save();
            };
            sub.Items.Add(apply);
            sub.Items.Add(update);
            sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(delete);
            ToolTipService.SetToolTip(sub, ProfileDescription(profile));
            ProfilesFlyout.Items.Add(sub);
        }
    }

    static string ProfileDescription(IniProfile p) =>
        L.F("Saved {0:d MMM yyyy, HH:mm}", p.CreatedAt) + "\n\n" + string.Join("\n", p.Values.OrderBy(v => v.Key).Take(24).Select(v => $"{v.Key} = {v.Value}"))
        + (p.Values.Count > 24 ? "\n" + L.F("… and {0} more", p.Values.Count - 24) : "");

    /// <summary>
    /// What a profile keeps: every key of this game's OptiScaler.ini that is not on Auto (also the ones changed in OptiScaler's
    /// own menu), plus Hybrid Launcher's saved choices for it when the ini is not there.
    /// </summary>
    Dictionary<string, string> CurrentIniValues()
    {
        var values = new Dictionary<string, string>(_settings.IniOverrides, StringComparer.OrdinalIgnoreCase);
        if (IniPath is { } path)
        {
            var doc = IniDocument.Load(path);
            foreach (var e in doc.Entries)
            {
                if (IsAuto(e.Value)) values.Remove(e.Path);
                else values[e.Path] = e.Value;
            }
        }
        return values;
    }

    /// <summary>Saves a new profile (asking its name), or replaces <paramref name="existing"/>' values with the current ones.</summary>
    async Task SaveProfileAsync(IniProfile? existing)
    {
        try
        {
            var values = CurrentIniValues();
            if (values.Count == 0)
            {
                App.Window?.ShowInfo(L.T("Every setting is on Auto: change something first, then save it as a profile."));
                return;
            }
            var profile = existing;
            if (profile is null)
            {
                var name = await AskNameAsync(L.T("Save as a profile"), L.F("{0} setting(s) that are not on Auto will be saved. Apply them to any game later from Profiles.", values.Count), _game?.Title ?? L.T("My profile"));
                if (name is null) return;
                profile = AppState.Settings.IniProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
                if (profile is not null && !await ConfirmAsync(L.F("Replace \"{0}\"?", profile.Name), L.T("A profile with this name already exists."), L.T("Replace"))) return;
                if (profile is null)
                {
                    profile = new IniProfile { Name = name };
                    AppState.Settings.IniProfiles.Add(profile);
                }
            }
            profile.Values = values;
            profile.CreatedAt = DateTime.Now;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("Profile \"{0}\" saved with {1} setting(s).", profile.Name, values.Count));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not save the profile"), ex.Message, ex);
        }
    }

    /// <summary>
    /// Makes the game's settings match the profile: its keys get its values, keys Hybrid Launcher set earlier that the profile does not have go
    /// back to Auto. Without OptiScaler installed the profile is kept and written at the next install.
    /// </summary>
    async Task ApplyProfileAsync(IniProfile profile)
    {
        if (_game is null) return;
        var game = _game;
        var settings = _settings;
        var path = IniPath;
        try
        {
            int written = 0;
            if (path is not null)
            {
                var stale = settings.IniOverrides.Keys.Where(k => !profile.Values.ContainsKey(k)).ToList();
                written = await Task.Run(() =>
                {
                    var doc = IniDocument.Load(path);
                    int n = 0;
                    foreach (var key in stale)
                        if (doc.Find(key) is { } e) doc.Set(e.Section, e.Key, "auto");
                    foreach (var (key, value) in profile.Values)
                        if (doc.Find(key) is { } e && doc.Set(e.Section, e.Key, value)) n++;
                    doc.Save();
                    return n;
                });
            }
            settings.IniOverrides = new Dictionary<string, string>(profile.Values, StringComparer.OrdinalIgnoreCase);
            SettingsStore.Save();
            if (game != _game) return;
            _iniStale = true;
            if (IniOpen) LoadIni();
            if (path is null)
                App.Window?.ShowSuccess(L.F("\"{0}\" will be applied when OptiScaler is installed in {1}.", profile.Name, game.Title));
            else
                App.Window?.ShowSuccess(L.F("\"{0}\" applied to {1}: {2} setting(s) written to OptiScaler.ini", profile.Name, game.Title, written)
                    + (written < profile.Values.Count ? " " + L.F("({0} not in this OptiScaler version).", profile.Values.Count - written) : "."));
        }
        catch (Exception ex)
        {
            var running = path is null ? null : OptiInstaller.RunningGameProcess(Path.GetDirectoryName(path)!);
            App.Window?.ShowError(L.T("Could not apply the profile"), ex.Message + (running is not null ? " " + L.F("({0} is running)", running) : ""));
        }
    }

    async Task<string?> AskNameAsync(string title, string message, string suggestion)
    {
        var box = new TextBox { Text = suggestion, PlaceholderText = L.T("Profile name"), MaxLength = 60 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = L.T("Save"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ActualTheme,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    // ---------- Steam achievements ----------

    /// <summary>
    /// The id the achievements belong to: the Steam game's, the one a Steam emulator (Goldberg / GSE) runs it as, or for a GOG game
    /// "gog:&lt;product id&gt;" while signed in to GOG (the same ids <see cref="AchievementWatcher.Changed"/> reports).
    /// </summary>
    string? AchAppId => _game is { Store: GameStore.Steam, SteamAppId: { } id } ? id
        : _game is not null && HybridLauncher.Core.Achievements.XboxAchievements.Applies(_game) ? HybridLauncher.Core.Achievements.XboxAchievements.Key(_game)
        : _game?.EmulatorAppId is { } emu && AppState.Settings.ReadSteamEmulator ? emu
        : _game is not null && AppState.Settings.ReadGogAchievements && HybridLauncher.Core.Accounts.GogAccount.SignedIn && HybridLauncher.Core.Accounts.GogAchievements.ProductId(_game) is { } gog ? "gog:" + gog : null;

    async Task LoadAchievementsAsync(CancellationToken ct)
    {
        var appId = AchAppId;
        if (appId is null || _loadingAch)
        {
            if (appId is null) ShowAchievements(null);
            return;
        }
        var game = _game;
        _loadingAch = true;
        try
        {
            var set = await SteamAchievements.LoadForAsync(game!, ct);
            if (ct.IsCancellationRequested || game != _game) return;
            ShowAchievements(set);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"Achievements for {game?.Name}: {ex.Message}");
            ShowAchievements(null);
        }
        finally
        {
            _loadingAch = false;
        }
    }

    // Raised from the Steam stats watcher on the UI thread.
    void OnAchievementsChanged(string appId)
    {
        if (appId == AchAppId) _ = LoadAchievementsAsync(_pageCts.Token);
    }

    void ShowAchievements(AchievementSet? set)
    {
        _ach = set is { Total: > 0 } ? set : null;
        ApplyPageParts();
        RebuildTabs();
        _dashboard?.SetAchievements(_ach);
        _achTiles.Clear();
        AchGrid.Children.Clear();
        _achLaidOutColumns = -1;
        if (_ach is null)
        {
            if (_tab == "ach") ShowTab("overview", remember: false);
            return;
        }

        int unlocked = _ach.UnlockedCount;
        double share = (double)unlocked / _ach.Total;
        AchProgress.Value = share;
        AchButtonText.Text = $"{unlocked} / {_ach.Total}";
        // Gold with depth; platinum once every achievement is unlocked.
        AchIcon.Content = Icons.Trophy(20, platinum: unlocked == _ach.Total);
        ToolTipService.SetToolTip(AchButton, unlocked == _ach.Total ? L.T("Platinum: every achievement unlocked") : L.T("Achievements"));
        var last = _ach.Items.Where(a => a.UnlockedAt is not null).MaxBy(a => a.UnlockedAt);
        AchSummary.Text = L.F("{0} of {1} unlocked ({2:P0})", unlocked, _ach.Total, share) + (last is not null ? "  ·  " + L.F("latest: {0}, {1:d MMM yyyy}", last.Name, last.UnlockedAt) : "");
        // Gamerscore (Xbox): earned of the total.
        if (_ach.Items.Any(a => a.Points is > 0))
            AchSummary.Text += "  ·  " + L.F("{0} of {1} G", _ach.Items.Where(a => a.Unlocked).Sum(a => a.Points ?? 0), _ach.Items.Sum(a => a.Points ?? 0));
        EnsureAchExport();

        _achStale = true;
        if (_tab == "ach") BuildAchievementTiles();
        else if (AppState.Settings.GameTab == "ach" && _tab == "overview") ShowTab("ach", remember: false);
        if (_achToShow is { } wanted) ShowAchievement(wanted);
    }

    /// <summary>Builds the tiles (icons load from Steam's CDN), only when the tab is shown.</summary>
    void BuildAchievementTiles()
    {
        _achStale = false;
        _achTiles.Clear();
        _achTileByKey.Clear();
        _achLaidOutColumns = -1;
        if (_ach is null) return;
        // Unlocked first, newest on top; then locked ones from the most common to the rarest, hidden ones last.
        var ordered = _ach.Items.Where(a => a.Unlocked).OrderByDescending(a => a.UnlockedAt ?? DateTime.MinValue)
            .Concat(_ach.Items.Where(a => !a.Unlocked).OrderBy(a => a.Hidden).ThenByDescending(a => a.GlobalPercent ?? -1));
        _achText.Clear();
        foreach (var a in ordered)
        {
            var tile = AchievementTile(a);
            _achTiles.Add(tile);
            // What the search box looks in: a hidden achievement still locked gives nothing away.
            _achText[tile] = a.Hidden && !a.Unlocked ? "" : a.Name + " " + a.Description;
            if (tile is Border b) _achTileByKey[a.Key] = b;
        }
        LayoutAchievements();
    }

    /// <summary>
    /// The controller's Home button on an achievement's pop-up (as a console opens the trophy just earned): the Achievements tab,
    /// every achievement listed, scrolled to that one and lit for a moment. Waits for the set when it is still being read.
    /// </summary>
    public void ShowAchievement(string key)
    {
        _achToShow = key;
        if (_ach is null || !_ach.Items.Any(a => a.Key == key)) return; // ShowAchievements calls back once the set is read
        _achToShow = null;
        ShowTab("ach", remember: false);
        if (AchFilterBar.Items.FirstOrDefault(i => i.Tag as string == "all") is { } all && AchFilterBar.SelectedItem != all) AchFilterBar.SelectedItem = all;
        if (_achStale || _achTiles.Count == 0) BuildAchievementTiles();
        if (!_achTileByKey.TryGetValue(key, out var tile)) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (tile.Parent is null) return;
            ScrollToElement(tile);
            LightTile(tile);
        });
        LastShownAchievement = key;
    }

    /// <summary>The achievement the Home button opened last (self-test).</summary>
    internal string? LastShownAchievement { get; private set; }

    /// <summary>The accent edge on a tile for three seconds, then the tile as it was.</summary>
    void LightTile(Border tile)
    {
        var brush = tile.ReadLocalValue(Border.BorderBrushProperty);
        var thickness = tile.ReadLocalValue(Border.BorderThicknessProperty);
        var opacity = tile.Opacity;
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accent)) tile.BorderBrush = accent as Brush;
        tile.BorderThickness = new Thickness(2);
        tile.Opacity = 1;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(3000);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            Restore(tile, Border.BorderBrushProperty, brush);
            Restore(tile, Border.BorderThicknessProperty, thickness);
            tile.Opacity = opacity;
        };
        timer.Start();
        static void Restore(DependencyObject o, DependencyProperty p, object local)
        {
            if (local == DependencyProperty.UnsetValue) o.ClearValue(p);
            else o.SetValue(p, local);
        }
    }

    FrameworkElement AchievementTile(Achievement a)
    {
        bool secret = a.Hidden && !a.Unlocked;
        var iconUrl = a.Unlocked ? a.IconUrl : a.IconGrayUrl ?? a.IconUrl;
        var icon = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["ControlFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (!secret && iconUrl is not null && Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri))
            icon.Child = Smooth.Art(new BitmapImage { UriSource = uri, DecodePixelWidth = 64 }, Smooth.Inner(icon), Stretch.UniformToFill);
        else
            icon.Child = new FontIcon { Glyph = secret ? "" : "", FontSize = 22, Opacity = 0.6 };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = secret ? L.T("Hidden achievement") : a.Name,
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = secret ? L.T("Details show up once you unlock it.") : a.Description,
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"],
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var foot = a.Unlocked ? (a.UnlockedAt is { } t ? L.F("Unlocked {0:d MMM yyyy, HH:mm}", t) : L.T("Unlocked")) : L.T("Locked");
        if (a.GlobalPercent is { } p) foot += "  ·  " + L.F("{0:0.#}% of players", p);
        if (a.Points is > 0) foot += $"  ·  {a.Points} G";
        // A locked achievement with steps: how far along it is, as a line and a thin bar.
        if (!a.Unlocked && !secret && a.ProgressMax is > 0 and var max)
        {
            var now = Math.Clamp(a.ProgressNow ?? 0, 0, max);
            text.Children.Add(new TextBlock
            {
                Text = max == 100 ? L.F("Progress: {0:0}%", now) : L.F("Progress: {0:0.##} of {1:0.##}", now, max),
                FontSize = 11,
                Foreground = (Brush)HybridLauncher.Helpers.ThemeRes.Current["TextFillColorSecondaryBrush"],
            });
            text.Children.Add(new ProgressBar { Maximum = max, Value = now, Margin = new Thickness(0, 2, 8, 2), MinHeight = 3 });
        }
        text.Children.Add(new TextBlock
        {
            Text = foot,
            FontSize = 11,
            // A narrow tile: the date and the rarity go on two lines instead of the rarity being cut mid-word.
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)HybridLauncher.Helpers.ThemeRes.Current[a.Unlocked ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush"],
        });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var tile = new Border
        {
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowStyle"],
            Padding = new Thickness(10),
            Child = grid,
            Opacity = a.Unlocked ? 1 : 0.62,
            Tag = a.Unlocked,
        };
        if (!secret) ToolTipService.SetToolTip(tile, _game?.Store == GameStore.Xbox ? $"{a.Name}\n{a.Description}" : $"{a.Name}\n{a.Description}\n\n" + L.F("Steam name: {0}", a.ApiName));
        return tile;
    }

    /// <summary>Places the tiles that pass the filter in as many columns as fit.</summary>
    void LayoutAchievements()
    {
        if (_achTiles.Count == 0) return;
        _achLaidOutColumns = _achColumns;
        var filter = (AchFilterBar.SelectedItem as SelectorBarItem)?.Tag as string ?? "all";
        var query = _achSearch?.Text.Trim() ?? "";
        var tiles = _achTiles.Where(t => (filter == "all" || (t.Tag is true) == (filter == "unlocked"))
            && (query.Length == 0 || (_achText.TryGetValue(t, out var text) && text.Contains(query, StringComparison.CurrentCultureIgnoreCase)))).ToList();
        int cols = Math.Max(1, _achColumns);
        AchGrid.Children.Clear();
        AchGrid.ColumnDefinitions.Clear();
        AchGrid.RowDefinitions.Clear();
        for (int c = 0; c < cols; c++) AchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < (tiles.Count + cols - 1) / cols; r++) AchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < tiles.Count; i++)
        {
            Grid.SetRow(tiles[i], i / cols);
            Grid.SetColumn(tiles[i], i % cols);
            AchGrid.Children.Add(tiles[i]);
        }
    }

    void AchFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => LayoutAchievements();

    Button? _achExport;
    TextBox? _achSearch;
    readonly Dictionary<FrameworkElement, string> _achText = [];

    /// <summary>Export beside the filter: the game's whole list (unlocked or not) as CSV or JSON.</summary>
    void EnsureAchExport()
    {
        if (_achExport is not null || AchFilterBar.Parent is not Grid head) return;
        if (head.ColumnDefinitions.Count < 3) head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _achExport = new Button
        {
            Content = new FontIcon { Glyph = "\uEDE1", FontSize = 14 },
            Padding = new Thickness(9, 6, 9, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["FlatGlassButton"],
        };
        ToolTipService.SetToolTip(_achExport, L.T("Export the list (CSV or JSON)"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_achExport, L.T("Export the list"));
        _achExport.Click += async (_, _) =>
        {
            if (_ach is not { } set || _game is not { } game || App.Window is not { } w) return;
            try
            {
                var path = await Pickers.PickSaveAsync(w, $"{game.Title} achievements", ("CSV", ".csv"), ("JSON", ".json"));
                if (path is null) return;
                HybridLauncher.Core.Achievements.AchievementHistory.ExportSet(set, game.Title, path);
                Log.Info($"Achievements of {game.Name} exported to {path}");
            }
            catch (Exception ex) { App.Window?.ShowError(L.T("Export failed"), ex.Message, ex); }
        };
        // Search by name or description, beside it.
        _achSearch = new TextBox { PlaceholderText = L.T("Search achievements"), Width = 220, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_achSearch, L.T("Search achievements"));
        _achSearch.TextChanged += (_, _) => LayoutAchievements();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(_achSearch);
        tools.Children.Add(_achExport);
        Grid.SetColumn(tools, 2);
        head.Children.Add(tools);
    }

    void AchButton_Click(object sender, RoutedEventArgs e)
    {
        ShowTab("ach");
        ScrollToTabs();
    }

    // ---------- shared ----------

    static Border Row(string header, string description, FrameworkElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = header, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowHeader"] });
        if (!string.IsNullOrWhiteSpace(description))
            text.Children.Add(new TextBlock { Text = description, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowStyle"], Child = grid };
    }

    static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
    }
}
