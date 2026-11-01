using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Ini;
using HybridLauncher.Core.Media;
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

public sealed partial class GamePage : Page
{
    Game? _game;
    GameSettings _settings = new();
    CancellationTokenSource _pageCts = new();
    CancellationTokenSource? _installCts;
    List<OptiRelease> _stable = [], _nightly = [];
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
    OptiWikiView? _optiWikiView;
    PcgwView? _pcgwView;
    LinksView? _linksView;
    string? _configDir, _saveDir;

    AchievementSet? _ach;
    readonly List<FrameworkElement> _achTiles = [];
    int _achColumns;
    int _achLaidOutColumns = -1;
    bool _loadingAch;

    public string? GameId => _game?.Id;

    // The welcome tour points at these.
    internal FrameworkElement TourPlay => PlayButton;
    internal FrameworkElement TourHdr => HdrToggle;
    internal FrameworkElement TourTabs => Tabs;
    internal FrameworkElement TourMore => MoreButton;

    public GamePage()
    {
        InitializeComponent();
        Loc.Apply(this);
        // The GPU choice is read by index: its labels can be translated.
        GpuCombo.Items.Clear();
        foreach (var label in new[] { L.T("Auto-detect"), "AMD / Intel", "Nvidia" }) GpuCombo.Items.Add(label);
        foreach (var n in OptiSetupOptions.DllNames) DllCombo.Items.Add(n);
        Root.SizeChanged += (_, e) =>
        {
            _sectionsDirty = true;
            ApplyLayout(e.NewSize.Width);
            if (_tab == "pcgw") DispatcherQueue.TryEnqueue(PlaceToc);
        };
        // A section changing height moves the ones below it: the scroll-spy measures them again.
        foreach (var section in new FrameworkElement[] { OptiTab, QuickPanelCard, IniCard, OptiWikiHost, OptiNotesHost, ReShadeHost, RenoDxHost, LumaHost, OfflineHost, PcgwHost })
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
            if (_tab == "opti") _ = UpdateFootprintAsync();
            _ = ReadWindowsHdrAsync();
            // Steam writes the session's time when the game closes: pick it up within a minute.
            if (++_ticks % 15 == 0) _ = UpdatePlayStatsAsync();
        };
        VersionsList.Changed += () => _ = LoadCustomBuildsAsync(null, _pageCts.Token);
        PlayButton.Clicked += PlayOrStop;
        // Laid out again once the page is in the window: the sections' places are known only then.
        Loaded += (_, _) => QueueSubTabs();
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _game = AppState.FindAny(e.Parameter as string);
        if (_game is null) return;
        _settings = AppState.Settings.For(_game.Id);
        _channel = _settings.Install?.Channel ?? OptiChannel.Stable;
        ChannelCard(_channel).IsChecked = true;
        AppState.PinsChanged += UpdatePin;
        AchievementWatcher.Changed += OnAchievementsChanged;
        GameSessions.PlaytimeChanged += OnPlaytimeChanged;
        GameSessions.Changed += OnSessionChanged;
        CloudSync.Changed += OnCloudChanged;
        Downloads.Changed += OnDownloadsChanged;
        AppState.GamesChanged += OnDownloadsChanged;
        MediaPolicy.Changed += UpdateFloor;
        var ct = _pageCts.Token;
        var game = _game;
        var art = GameInfo.GetAsync(game, ct);
        _data = new GameData
        {
            Game = game,
            Token = ct,
            Art = art,
            Store = StoreAsync(art, ct),
            Pcgw = PcgwAsync(game, art, ct),
            OptiWiki = CompatWiki.GetEntryAsync(GameNames.PlainOrClean(GameNames.LookupName(game)), ct),
            ShowTab = OpenTab,
        };
        Prefetch(_data);
        Refresh();
        UpdateLinks();
        RebuildTabs();
        ShowTab(AppState.Settings.GameTab, remember: false);
        OptiWikiPreviewHost.Child = WikiPreviews.OptiCard(_data);
        OptiNotesPreviewHost.Child = OptiNotesView.Preview(() => OpenTab("releasenotes"));
        // Always open a game at the top, with the banner visible.
        DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, 0, null, disableAnimation: true));
        _ = LoadArtAsync(_pageCts.Token);
        _ = LoadWikiExtrasAsync(_pageCts.Token);
        _ = LoadReleasesAsync(false, _pageCts.Token);
        _ = LoadCustomBuildsAsync(null, _pageCts.Token);
        if (!_game.NotInstalled) _ = MeasureGameSizeAsync(_pageCts.Token);
        _filesTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _pageCts.Cancel();
        _filesTimer.Stop();
        _overview?.StopTrailer();
        AppState.PinsChanged -= UpdatePin;
        AchievementWatcher.Changed -= OnAchievementsChanged;
        GameSessions.PlaytimeChanged -= OnPlaytimeChanged;
        GameSessions.Changed -= OnSessionChanged;
        CloudSync.Changed -= OnCloudChanged;
        Downloads.Changed -= OnDownloadsChanged;
        AppState.GamesChanged -= OnDownloadsChanged;
        MediaPolicy.Changed -= UpdateFloor;
        SaveArgs();
    }

    /// <summary>Stops a playing trailer (the window went to the notification area).</summary>
    public void StopMedia() => _overview?.StopTrailer();

    /// <summary>Re-reads the game folder and redraws everything that depends on it.</summary>
    public void Refresh()
    {
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

            LoadSetupOptions();
            UpdateOptiStatus();
            LoadAsiList();
            LoadCustomDllList();
            _iniStale = true;
            if (IniOpen) LoadIni();
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

    string? TargetDir => _game is null ? null : GameScanner.TargetDir(_game, _settings);

    // ---------- tabs ----------

    ReShadeView? _reshadeView;
    RenoDxView? _renoDxView;
    LumaView? _lumaView;
    QuickAccessView? _quickView;
    NotesView? _notesView;

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
            try
            {
                if (!await TabsDialog.ShowAsync(XamlRoot, ActualTheme, _settings, _game.Title)) return;
                RebuildTabs();
                ShowTab(_tab, remember: false);
            }
            catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the tabs"), ex.Message); }
        };
    }

    /// <summary>
    /// The achievements tab needs Steam achievements; every other tab is always there unless hidden. On the page of a game not on
    /// this PC, the tabs that work on its files say what they do and offer Download (see <see cref="NeedsFiles"/>).
    /// </summary>
    bool Available(string id) => id != "ach" || _ach is not null;

    /// <summary>
    /// Tabs with sections about the game's own files: on a game not installed yet the OptiScaler tab shows <see cref="OfflineCard"/>
    /// in place of its install and settings, and the ReShade section of ReShade &amp; Mods shows it (the wiki, RenoDX and Luma still work).
    /// </summary>
    bool NeedsFiles(string id) => _game?.NotInstalled == true && id is "opti" or "mods";

    /// <summary>The OptiScaler.ini editor is on screen (its tab is open, the game is installed).</summary>
    bool IniOpen => IniCard.Visibility == Visibility.Visible;

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

    /// <summary>Shows one tab; the others are collapsed, so they cost nothing to lay out or draw.</summary>
    void ShowTab(string tab, bool remember = true, string? section = null)
    {
        tab = GameTabs.Resolve(tab).Tab;
        var visible = VisibleTabs();
        if (!visible.Any(t => t.Id == tab))
        {
            remember = false;
            tab = visible.FirstOrDefault(t => t.Id == "overview")?.Id ?? visible.FirstOrDefault()?.Id ?? "overview";
        }
        if (tab != "overview") _overview?.StopTrailer();
        _tab = tab;
        Tabs.Select(tab);
        bool offline = NeedsFiles(tab);
        bool opti = tab == "opti", mods = tab == "mods";
        OfflineHost.Visibility = offline && opti ? Visibility.Visible : Visibility.Collapsed;
        if (offline && opti) OfflineHost.Child = OfflineCard(GameTabs.Find("opti")!);
        OptiTab.Visibility = opti && !offline ? Visibility.Visible : Visibility.Collapsed;
        IniCard.Visibility = opti && !offline ? Visibility.Visible : Visibility.Collapsed;
        QuickPanelCard.Visibility = IniCard.Visibility;
        OptiWikiHost.Visibility = opti ? Visibility.Visible : Visibility.Collapsed;
        OptiNotesHost.Visibility = OptiWikiHost.Visibility;
        AchCard.Visibility = tab == "ach" ? Visibility.Visible : Visibility.Collapsed;
        OverviewHost.Visibility = tab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PcgwHost.Visibility = tab == "pcgw" ? Visibility.Visible : Visibility.Collapsed;
        LinksHost.Visibility = tab == "links" ? Visibility.Visible : Visibility.Collapsed;
        ReShadeHost.Visibility = mods ? Visibility.Visible : Visibility.Collapsed;
        RenoDxHost.Visibility = mods ? Visibility.Visible : Visibility.Collapsed;
        LumaHost.Visibility = mods ? Visibility.Visible : Visibility.Collapsed;
        QuickHost.Visibility = tab == "quick" ? Visibility.Visible : Visibility.Collapsed;
        NotesHost.Visibility = tab == "notes" ? Visibility.Visible : Visibility.Collapsed;
        // ReShade works on the game's files: without them (not installed) its section is the offline card, the others stay.
        if (mods && offline) ReShadeHost.Child = OfflineCard(GameTabs.Sections("mods")[0]);
        if (_data is not null)
        {
            if (tab == "overview" && _overview is null)
            {
                OverviewHost.Child = _overview = new OverviewView(_data);
                _overview.ApplyLayout(Root.ActualWidth);
                _overview.SetAchievements(_ach);
            }
            if (opti && _optiWikiView is null) OptiWikiHost.Child = _optiWikiView = new OptiWikiView(_data);
            if (opti && OptiNotesHost.Child is null) OptiNotesHost.Child = new OptiNotesView(() => _settings.Install);
            if (tab == "pcgw" && _pcgwView is null)
            {
                PcgwHost.Child = _pcgwView = new PcgwView(_data);
                _pcgwView.ScrollRequested += el => ScrollToElement(el);
                _pcgwView.TocLayoutChanged += () => DispatcherQueue.TryEnqueue(PlaceToc);
            }
            if (tab == "links" && _linksView is null) LinksHost.Child = _linksView = new LinksView(_data);
            if (mods)
            {
                // The three sections are built together, the first time the tab opens.
                if (!offline)
                {
                    _reshadeView ??= new ReShadeView(ModContext());
                    if (!ReferenceEquals(ReShadeHost.Child, _reshadeView)) ReShadeHost.Child = _reshadeView;
                }
                if (_renoDxView is null) RenoDxHost.Child = _renoDxView = new RenoDxView(ModContext());
                if (_lumaView is null) LumaHost.Child = _lumaView = new LumaView(ModContext());
            }
            if (tab == "quick" && _quickView is null) QuickHost.Child = _quickView = new QuickAccessView(ModContext());
            if (tab == "notes" && _notesView is null) NotesHost.Child = _notesView = new NotesView(ModContext());
        }
        if (mods)
        {
            // Each on its own: one failing must not leave the others showing an old state.
            if (!offline && _reshadeView is { } r) Ui.Safe(r.Refresh);
            if (_renoDxView is { } x) Ui.Safe(x.Refresh);
            if (_lumaView is { } l) Ui.Safe(l.Refresh);
        }
        if (opti && _iniStale && !offline) LoadIni();
        if (tab == "ach" && _achStale) BuildAchievementTiles();
        if (opti && !offline) _ = UpdateFootprintAsync();
        ShowSubTabs(section);
        PlaceToc();
        if (remember && AppState.Settings.GameTab != tab)
        {
            AppState.Settings.GameTab = tab;
            SettingsStore.Save();
        }
    }

    // ---------- sub-tabs: the sections of OptiScaler and of ReShade & Mods ----------

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
        ("opti", "wiki") => OptiWikiHost,
        ("opti", "notes") => OptiNotesHost,
        ("mods", "reshade") => ReShadeHost,
        ("mods", "renodx") => RenoDxHost,
        ("mods", "luma") => LumaHost,
        _ => null,
    };

    readonly Dictionary<string, FrameworkElement> _separators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The separator above a section: a hairline with the section's icon and name on it, so the vertical scroll through a tab's
    /// sections reads as chapters. Made once, placed right before the section in its panel, shown while that section is.
    /// </summary>
    FrameworkElement? Separator(GameTab section)
    {
        if (SectionElement(section.Id) is not { } el) return null;
        // Before the page is loaded the sections in the XAML have no Parent yet (the tab shown on opening a game): the page's
        // own panel holds them, so the separators are there from the start, not only after another tab was opened.
        var panel = el.Parent as Panel ?? VisualTreeHelper.GetParent(el) as Panel ?? (Root.Children.Contains(el) ? Root : null);
        if (panel is null) return null;
        string key = $"{_tab}/{section.Id}";
        if (!_separators.TryGetValue(key, out var sep)) _separators[key] = sep = Ui.SectionRule(section.Tint);
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
        _sectionEls = shown.Select((s, i) => i > 0 && Separator(s) is { } sep ? sep : SectionElement(s.Id)!).ToArray();
        _sectionTops = new double[shown.Count];
        _sectionsDirty = true;
        _forcedSection = null;
        Tabs.SetSubTabs(shown, selected is not null && _sectionIds.Contains(selected) ? selected : shown.Count > 0 ? shown[0].Id : "");
        // The row's height changed with the sub bar: keep the pinned row's place in the page as tall as the row now is.
        if (_stuck) DispatcherQueue.TryEnqueue(() => TabHost.Height = Tabs.ActualHeight);
    }

    bool _subTabsQueued, _spyQueued;

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
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28), HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, color.R, color.G, color.B)),
            Child = new FontIcon { Glyph = info.Glyph, FontSize = 24, Foreground = new SolidColorBrush(color) },
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

    ModContext ModContext() => new()
    {
        Game = _game!,
        Settings = _settings,
        Data = _data!,
        TargetDir = () => TargetDir,
        Token = _pageCts.Token,
        OptiChanged = () =>
        {
            _iniStale = true;
            Refresh();
            // RenoDX and Luma show how ReShade is loaded too: redrawn with the ReShade section, never left stale.
            if (_renoDxView is { } x) Ui.Safe(x.Refresh);
            if (_lumaView is { } l) Ui.Safe(l.Refresh);
        },
        ShowTab = OpenTab,
        ReinstallOpti = () => _ = InstallAsync(SelectedRelease, manual: false),
    };

    // ---------- the tab row stays on top while scrolling ----------

    bool _stuck;

    /// <summary>Where the tab row sits in the page (its top, from the top of the scrolled content).</summary>
    double TabTop() => TabHost.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

    void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateSticky();
        UpdateSectionSpy(e.IsIntermediate);
        if (_tab == "pcgw") _pcgwView?.UpdateSpy(Root, Scroller.VerticalOffset + StickyHeight + 32);
    }

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
    public void SelfTestShow(string tab, string? iniView = null)
    {
        var parts = tab.Split('/');
        ShowTab(parts[0], remember: false, section: parts.Length > 1 ? parts[1] : null);
        if (parts.Length > 1 && parts[0] == "pcgw") { if (_pcgwView?.SectionElement(parts[1]) is { } el) ScrollToElement(el, animated: false); }
        else if (parts.Length > 1) ScrollToSection(parts[1], animated: false);
        if (iniView is not null) SelfTestIni(iniView);
        foreach (var card in new[] { CustomCard, NightlyCard, StableCard }) card.IsChecked = true;
    }

    /// <summary>Self-test hook: scrolls past the tab row (it moves to the top bar) or back to the top.</summary>
    public void SelfTestScroll(bool down) => Scroller.ChangeView(null, down ? TabTop() + 400 : 0, null, disableAnimation: true);

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
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message); }
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
        _overview?.ApplyLayout(width);

        // Launch bar: arguments get their own full row when narrow; the play stats go first when space runs out.
        _narrowBar = narrow;
        UpdateDownloadPanel(_game is null ? null : Downloads.Active(_game.Id));
        LayoutArgsBox();

        // OptiScaler beside the ASI plugins on wide windows, the plugins below it otherwise.
        // The side column takes about a quarter of the page, so the install card does not stretch into a long thin strip.
        bool stackTop = width < 1100;
        double side = Math.Clamp(Math.Round(width * 0.27), 360, 600);
        OptiTab.ColumnDefinitions[1].Width = stackTop ? new GridLength(0) : new GridLength(side);
        OptiTab.ColumnSpacing = stackTop ? 0 : 16;
        Grid.SetRow(SideColumn, stackTop ? 1 : 0);
        Grid.SetColumn(SideColumn, stackTop ? 0 : 1);

        // Version step: channel switch above the version list when narrow.
        // Install card: the channel cards stack and the version tools go under the list when the card is narrow;
        // the ini / uninstall buttons drop to a second row.
        double card = (stackTop ? width : width - side - 16) - 40;
        bool narrowCard = card < 640;
        for (int i = 0; i < 3; i++) ChannelCards.ColumnDefinitions[i].Width = narrowCard && i > 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ChannelCards.ColumnSpacing = narrowCard ? 0 : 8;
        if (ChannelCards.RowDefinitions.Count < 3)
            for (int i = ChannelCards.RowDefinitions.Count; i < 3; i++) ChannelCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RadioButton[] cards = [StableCard, NightlyCard, CustomCard];
        for (int i = 0; i < 3; i++)
        {
            Grid.SetRow(cards[i], narrowCard ? i : 0);
            Grid.SetColumn(cards[i], narrowCard ? 0 : i);
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
                HeroImage.Source = new BitmapImage { DecodePixelWidth = HeroDecodeWidth(), DecodePixelType = DecodePixelType.Physical, UriSource = new Uri(art.HeroImage) };
                FadeIn(HeroImage);
                _heroUri = art.HeroImage;
                UpdateFloor();
            }
            catch (Exception ex) { Log.Warn($"Hero image failed: {ex.Message}"); }
        }

        // The title logo replaces the plain title once it has loaded; a broken file keeps the text.
        if (art.LogoImage is not null)
        {
            try
            {
                var logo = new BitmapImage();
                logo.ImageOpened += (_, _) =>
                {
                    if (game != _game) return;
                    LogoImage.Visibility = Visibility.Visible;
                    TitleText.Visibility = Visibility.Collapsed;
                    ApplyLayout(Root.ActualWidth);
                    FadeIn(LogoImage);
                };
                logo.ImageFailed += (_, e) => Log.Warn($"Logo for {game.Name} failed: {e.ErrorMessage}");
                logo.UriSource = new Uri(art.LogoImage);
                LogoImage.Source = logo;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LogoImage, game.Title);
            }
            catch (Exception ex) { Log.Warn($"Logo image failed: {ex.Message}"); }
        }
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
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not open the wiki"), ex.Message); }
    }

    async void Nexus_Click(object sender, RoutedEventArgs e)
    {
        try { GameLauncher.OpenUri(await NexusMods.PageForAsync(GameNames.PlainOrClean(GameNames.LookupName(_game!)))); }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not open Nexus Mods"), ex.Message); }
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
    /// "Open game folder" (only the ones that exist on this PC).
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
            foreach (var t in tags) TechBadges.Children.Add(TechTags.Create(t, frameGen: TechTags.IsFrameGen(t), fontSize: 12));
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
            var bitmap = await FloorMirror.CreateAsync(uri, aspect, fraction, ct);
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
            Frame.Navigate(typeof(GamePage), real.Id);
            if (Frame.BackStack.Count > 0) Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
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
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message); }
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
        // Three looks, so "Smart" stands apart: Smart (this game turns HDR on by itself) is the sun on a lit amber-to-pink plate
        // with "Smart" in the same sweep; otherwise the tile says what Windows HDR is now: "On" on a plain amber plate, or a grey
        // sun on a faint plate and a quiet "Off".
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
        HdrIcon.Background = smart ? sweep
            : windowsOn ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC8, 0x3D))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        HdrGlyph.Foreground = new SolidColorBrush(smart || windowsOn ? Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x14, 0x10) : Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
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
                : L.T("Counted by Hybrid Launcher while the game runs, when you start it with Play here.") + (stats.LastPlayed is { } l ? "\n" + L.F("Last played {0:d MMM yyyy, HH:mm}", l) : ""));
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
        catch (Exception ex) { App.Window?.ShowError(L.T("The shortcut was not created"), ex.Message); }
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
            timer.Tick += (_, _) => _ = SearchAsync();
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
            cts.Cancel();
            if (result != ContentDialogResult.Primary || _game != game) return;

            settings.PageAppId = choice == "auto" ? null : choice;
            var name = nameBox.Text.Trim();
            settings.PageName = name.Length == 0 ? null : name;
            SettingsStore.Save();
            LibraryPage.ForgetCover(game.Id);
            // Open the page again as it is now: the old one is cancelled by leaving it, and does not stay in Back.
            Frame.Navigate(typeof(GamePage), game.Id);
            if (Frame.BackStack.Count > 0) Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the game's page"), ex.Message); }
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

        // Settings → Appearance → Icons: the launch bar and the banner without their icons.
        bool bar = Icons.Show(IconSpots.LaunchBar), banner = Icons.Show(IconSpots.Banner);
        foreach (var icon in new FrameworkElement[] { HdrIcon, FolderIcon, SaveIcon, ConfigIcon, AchIcon }) icon.Visibility = Shown(bar);
        foreach (var icon in new FrameworkElement[] { NexusIcon, PcgwIcon, SteamDbIcon, ApiIcon }) icon.Visibility = Shown(banner);
        if (StoreBadgePanel.Children.Count > 0 && StoreBadgePanel.Children[0] is FrameworkElement logo and not TextBlock) logo.Visibility = Shown(banner);
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
        var target = TargetDir;
        var m = _settings.Install;
        var detected = target is null ? null : OptiDetector.Detect(target);

        string pill, headline, detail, glyph;
        Windows.UI.Color tint;
        if (m is not null && detected is not null)
        {
            pill = L.T("Installed");
            headline = L.F("OptiScaler {0} is installed", CustomBuilds.PrettyVersion(detected.Version));
            detail = L.F("{0} as {1}  ·  installed {2:yyyy-MM-dd HH:mm}", m.DisplayVersion, m.DllName, m.InstalledAt);
            glyph = "";
            tint = Windows.UI.Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43);
        }
        else if (m is not null)
        {
            pill = L.T("Missing");
            headline = L.T("OptiScaler's dll is missing");
            detail = L.F("Hybrid Launcher installed {0}, but its dll is gone from the folder. Uninstall to clean up, or install again.", m.DisplayVersion);
            glyph = "";
            tint = Windows.UI.Color.FromArgb(0xFF, 0xD2, 0x99, 0x22);
        }
        else if (detected is not null)
        {
            pill = L.T("Found");
            headline = L.F("OptiScaler {0} found (not installed by Hybrid Launcher)", CustomBuilds.PrettyVersion(detected.Version));
            detail = L.F("{0}. Installing here replaces it; the old files are backed up.", detected.FileName);
            glyph = "";
            tint = Windows.UI.Color.FromArgb(0xFF, 0x38, 0x8B, 0xFD);
        }
        else
        {
            pill = L.T("Not installed");
            headline = L.T("OptiScaler is not installed");
            detail = target is null ? L.T("Pick the game exe first (the gear beside Play → Change game exe).") : L.F("Pick a version and press Install. It goes in {0}", target);
            glyph = "";
            tint = Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x94, 0x9E);
        }
        StatusPillText.Text = pill;
        StatusPill.Background = Pill(tint.R, tint.G, tint.B);
        OptiHeadline.Text = headline;
        OptiStatusDetail.Text = detail;
        StatusIcon.Glyph = glyph;
        StatusIconCircle.Background = new SolidColorBrush(tint);
        StatusIcon.Foreground = Contrast.BrushOn(tint);
        // A soft wash of the state's colour behind the header, fading out to the right.
        OptiHeader.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x30, tint.R, tint.G, tint.B), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, tint.R, tint.G, tint.B), Offset = 0.7 },
            },
        };

        UninstallButton.Visibility = m is not null ? Visibility.Visible : Visibility.Collapsed;
        OpenIniButton.Visibility = target is not null && File.Exists(Path.Combine(target, "OptiScaler.ini")) ? Visibility.Visible : Visibility.Collapsed;
        Step3Title.Text = m is null ? L.T("Install") : L.T("Update, reinstall or remove");
        FillShortcutKeys();
        SetBusy(_busy);
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
            var text = L.F("OptiScaler files: {0}", DiskUsage.Format(fp.TotalBytes)) + " " + L.N(fp.FileCount, "in {0} file", "in {0} files")
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
        await Task.WhenAll(Fetch(OptiChannel.Stable), Fetch(OptiChannel.Nightly));
        if (ct.IsCancellationRequested) return;
        _releasesLoaded = true;
        if (_channel != OptiChannel.Custom) FillVersions();

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

    bool _releasesLoaded;
    bool _fillingVersions;
    string? _selectCustom;

    List<OptiRelease> Releases => _channel == OptiChannel.Nightly ? _nightly : _stable;

    // Stable / nightly: item 0 of the list is "Latest". Custom: one item per build.
    OptiRelease? SelectedRelease => _channel == OptiChannel.Custom
        ? SelectedCustom?.ToRelease()
        : Releases.Count == 0 ? null : VersionCombo.SelectedIndex <= 0 ? Releases[0] : Releases[Math.Min(VersionCombo.SelectedIndex - 1, Releases.Count - 1)];

    CustomBuild? SelectedCustom => _channel == OptiChannel.Custom && VersionCombo.SelectedIndex >= 0 && VersionCombo.SelectedIndex < _custom.Count
        ? _custom[VersionCombo.SelectedIndex] : null;

    RadioButton ChannelCard(OptiChannel channel) => channel switch { OptiChannel.Nightly => NightlyCard, OptiChannel.Custom => CustomCard, _ => StableCard };

    void Channel_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<OptiChannel>(tag, out var channel)) return;
        _channel = channel;
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
                VersionCombo.PlaceholderText = _releasesFailed ? L.T("Offline: can't list versions") : _releasesLoaded ? L.T("No releases found") : L.T("Loading versions…");
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

    void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingVersions) UpdateVersionDetail();
    }

    /// <summary>A plain-words hint for the chosen channel / build, then the install button's label.</summary>
    void UpdateVersionDetail()
    {
        var b = SelectedCustom;
        RemoveBuildItem.IsEnabled = b is not null;
        ChannelHint.Text = _channel switch
        {
            OptiChannel.Stable => L.T("Tested releases, published less often than nightly builds."),
            OptiChannel.Nightly => L.T("The newest features and fixes, built every day. If a game misbehaves, go back to Stable."),
            _ when b is not null => $"OptiScaler {b.PrettyVersion}"
                + (b.IsComplete ? "" : ". " + L.F("No {0} in this build: taken from the latest nightly at install.", string.Join(L.T(" or "), b.Missing))),
            _ => L.T("Builds you imported, e.g. test builds shared on the OptiScaler Discord. Import a folder or a .zip / .7z / .rar."),
        };
        if (b is not null) ToolTipService.SetToolTip(VersionCombo, $"{b.Name}\n{b.Version}\n{b.PayloadRoot}");
        SetBusy(_busy);
    }

    void VersionsRefresh_Click(object sender, RoutedEventArgs e) => _ = VersionsList.ReloadAsync();

    void Releases_Click(object sender, RoutedEventArgs e) =>
        Try(() => GameLauncher.OpenUri(OptiReleases.ReleasesPage(_channel == OptiChannel.Nightly ? OptiChannel.Nightly : OptiChannel.Stable)));

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
            var build = await Task.Run(() => CustomBuilds.ImportAsync(AppState.Settings, source));
            App.Window?.ShowSuccess(L.F("Imported \"{0}\": OptiScaler {1}.", build.Name, build.PrettyVersion));
            CustomCard.IsChecked = true;
            await LoadCustomBuildsAsync(build.Name, _pageCts.Token);
            _ = VersionsList.ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Import of {source} failed: {ex.Message}");
            App.Window?.ShowError(L.T("Could not import the build"), ex.Message);
            VersionCombo.PlaceholderText = L.T("No builds yet: use Import");
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
            App.Window?.ShowError(L.T("Could not delete the build"), ex.Message);
        }
        await LoadCustomBuildsAsync(null, _pageCts.Token);
        _ = VersionsList.ReloadAsync();
    }

    // ---------- OptiScaler: setup answers ----------

    void LoadSetupOptions()
    {
        var o = _settings.Setup;
        DllCombo.SelectedItem = OptiSetupOptions.DllNames.FirstOrDefault(n => n.Equals(o.DllName, StringComparison.OrdinalIgnoreCase)) ?? "dxgi.dll";
        GpuCombo.SelectedIndex = o.Nvidia switch { null => 0, false => 1, true => 2 };
        SpoofToggle.IsOn = o.Spoofing;
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
        o.DllName = DllCombo.SelectedItem as string ?? "dxgi.dll";
        o.Nvidia = GpuCombo.SelectedIndex switch { 1 => false, 2 => true, _ => null };
        o.Spoofing = SpoofToggle.IsOn;
        o.OptiPatcher = OptiPatcherToggle.IsOn;
        o.DeleteOldFiles = OldFilesToggle.IsOn;
        SettingsStore.Save();
        UpdateSetupState();
        SetBusy(_busy);
    }

    void UpdateSetupState()
    {
        var o = _settings.Setup;
        bool nvidia = o.Nvidia ?? File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
        SpoofToggle.IsEnabled = !nvidia;
        OptiPatcherToggle.IsEnabled = !nvidia;
        var parts = new List<string> { o.DllName, nvidia ? "Nvidia" : "AMD / Intel" };
        if (!nvidia)
        {
            parts.Add(o.Spoofing ? L.T("spoofing on") : L.T("spoofing off"));
            parts.Add(o.OptiPatcher ? L.T("OptiPatcher if supported") : L.T("no OptiPatcher"));
        }
        SetupSummary.Text = string.Join("  ·  ", parts);
    }

    async Task UpdateOptiPatcherHintAsync(CancellationToken ct)
    {
        var target = TargetDir;
        if (target is null) return;
        var supported = await OptiPatcherSupport.IsSupportedAsync(target, ct);
        if (ct.IsCancellationRequested) return;
        OptiPatcherHint.Text = supported switch
        {
            true => L.T("✔ This game is supported: OptiPatcher unlocks DLSS / DLSS-FG inputs without spoofing. Asked only on AMD / Intel."),
            false => L.T("This game is not in OptiPatcher's list, so the setup will skip it. Asked only on AMD / Intel."),
            _ => L.T("Unlocks DLSS / DLSS-FG inputs without spoofing in supported games (could not check the list now)."),
        };
    }

    // ---------- OptiScaler: install / uninstall ----------

    void Install_Click(object sender, RoutedEventArgs e) => StartInstall(manual: false);

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

        if (await ModBackups.DecideAsync("OptiScaler", game.Title) is not { } backup) return;

        // With the bat installer the file name is picked in the script, which warns about existing files itself.
        var options = settings.Setup.Clone();
        options.Manual = manual;
        var conflict = manual ? null : OptiDetector.DescribeConflict(target, settings.Setup.DllName);
        if (conflict is not null && !await ConfirmAsync(
                L.T("Another file has that name"),
                conflict + "\n\n" + (backup
                    ? L.T("OptiScaler would replace it, so that mod would stop working while OptiScaler is installed. Hybrid Launcher keeps a backup and puts it back when you uninstall.")
                    : L.T("OptiScaler would replace it without a backup (as chosen in Settings → Features): uninstalling OptiScaler will not bring it back.")) +
                "\n\n" + L.T("Tip: pick a different OptiScaler file name in Setup options (e.g. winmm.dll) to keep both."),
                backup ? L.T("Replace (keep a backup)") : L.T("Replace")))
            return;

        _installCts = new CancellationTokenSource();
        SetBusy(true);
        LogText.Text = "";
        LogExpander.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        InstallProgressBar.IsIndeterminate = false;

        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressStage.Text = p.Stage;
            if (p.Fraction > 0) InstallProgressBar.Value = p.Fraction;
            if (p.Line is not null) AppendLog(p.Line);
            else AppendLog($"[{p.Fraction:P0}] {p.Stage}");
        });

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
            }, progress, _installCts.Token));

            settings.Install = manifest;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("OptiScaler {0} installed in {1} as {2}.", manifest.DisplayVersion, game.Title, manifest.DllName));
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
            if (ex.Transcript is not null) AppendLog("\n----- " + L.T("setup_windows.bat output") + " -----\n" + ex.Transcript);
            LogExpander.IsExpanded = true;
            if (settings.Install is not null && !File.Exists(Path.Combine(settings.Install.TargetDir, settings.Install.DllName))) settings.Install = null;
            SettingsStore.Save();
            App.Window?.ShowError(L.T("Install failed"), ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            LogExpander.IsExpanded = true;
            AppendLog(ex.ToString());
            if (settings.Install is not null && !File.Exists(Path.Combine(settings.Install.TargetDir, settings.Install.DllName))) settings.Install = null;
            SettingsStore.Save();
            App.Window?.ShowError(L.T("Install failed"), ex.Message);
        }
        finally
        {
            _installCts.Dispose();
            _installCts = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            SetBusy(false);
            if (game == _game)
            {
                Refresh();
                _ = VersionsList.ReloadAsync();
            }
        }
    }

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var m = _settings.Install;
        if (m is null || _busy) return;
        if (!await ConfirmAsync(L.T("Uninstall OptiScaler?"),
                L.F("Removes the {0} files Hybrid Launcher installed in\n{1}\nand restores the {2} original file(s) it backed up. Your setup answers and ini choices stay saved.", m.Files.Count, m.TargetDir, m.Backups.Count),
                L.T("Uninstall")))
            return;

        SetBusy(true);
        try
        {
            var warnings = await Task.Run(() => OptiInstaller.Uninstall(m));
            _settings.Install = null;
            SettingsStore.Save();
            if (warnings.Count > 0) App.Window?.ShowInfo(L.T("OptiScaler removed.") + " " + string.Join(" ", warnings));
            else App.Window?.ShowSuccess(L.T("OptiScaler removed and the original files restored."));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Uninstall failed"), ex.Message);
        }
        finally
        {
            SetBusy(false);
            Refresh();
            _ = VersionsList.ReloadAsync();
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _installCts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = BatInstallButton.IsEnabled = !busy && SelectedRelease is not null;
        StableCard.IsEnabled = NightlyCard.IsEnabled = CustomCard.IsEnabled = VersionCombo.IsEnabled = !busy;
        ImportBuildButton.IsEnabled = CustomMoreButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        SetupExpander.IsEnabled = !busy;
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
            _ => $"OptiScaler {r.Tag}",
        };
        if (m is null)
            return (L.T("Install OptiScaler"), L.F("Installs {0}. Install runs OptiScaler's own setup script silently with your setup options; Standard .bat install opens it so you answer it yourself. Every file it replaces is backed up, and uninstall puts the folder back as it was.", version));
        bool same = m.Channel == r.Channel && (r.Channel == OptiChannel.Custom
            ? m.AssetName.Equals(r.AssetName, StringComparison.OrdinalIgnoreCase)
            : m.Tag.Equals(r.Tag, StringComparison.OrdinalIgnoreCase));
        if (same) return (L.T("Reinstall"), L.F("{0} is installed. Reinstalling keeps your settings.", m.DisplayVersion));
        if (m.Channel == r.Channel) return (L.T("Update"), L.F("Replaces {0} with {1}. Your settings are applied again.", m.DisplayVersion, version));
        var name = r.Channel switch { OptiChannel.Nightly => "Nightly", OptiChannel.Custom => L.T("custom build"), _ => "Stable" };
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
            App.Window?.ShowError(L.T("Could not delete the plugin"), ex.Message);
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
            App.Window?.ShowError(L.T("Could not add the plugin"), ex.Message);
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
            App.Window?.ShowError(L.T("Could not save the profile"), ex.Message);
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

    /// <summary>The app id the achievements belong to: the Steam game's, or the one a Steam emulator (Goldberg / GSE) runs it as.</summary>
    string? AchAppId => _game is { Store: GameStore.Steam, SteamAppId: { } id } ? id
        : _game?.EmulatorAppId is { } emu && AppState.Settings.ReadSteamEmulator ? emu : null;

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
        _overview?.SetAchievements(_ach);
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
        ToolTipService.SetToolTip(AchButton, unlocked == _ach.Total ? L.T("Platinum: every Steam achievement unlocked") : L.T("Steam achievements"));
        var last = _ach.Items.Where(a => a.UnlockedAt is not null).MaxBy(a => a.UnlockedAt);
        AchSummary.Text = L.F("{0} of {1} unlocked ({2:P0})", unlocked, _ach.Total, share) + (last is not null ? "  ·  " + L.F("latest: {0}, {1:d MMM yyyy}", last.Name, last.UnlockedAt) : "");

        _achStale = true;
        if (_tab == "ach") BuildAchievementTiles();
        else if (AppState.Settings.GameTab == "ach" && _tab == "overview") ShowTab("ach", remember: false);
    }

    /// <summary>Builds the tiles (icons load from Steam's CDN), only when the tab is shown.</summary>
    void BuildAchievementTiles()
    {
        _achStale = false;
        _achTiles.Clear();
        _achLaidOutColumns = -1;
        if (_ach is null) return;
        // Unlocked first, newest on top; then locked ones from the most common to the rarest, hidden ones last.
        var ordered = _ach.Items.Where(a => a.Unlocked).OrderByDescending(a => a.UnlockedAt ?? DateTime.MinValue)
            .Concat(_ach.Items.Where(a => !a.Unlocked).OrderBy(a => a.Hidden).ThenByDescending(a => a.GlobalPercent ?? -1));
        foreach (var a in ordered) _achTiles.Add(AchievementTile(a));
        LayoutAchievements();
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
        text.Children.Add(new TextBlock
        {
            Text = foot,
            FontSize = 11,
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
        if (!secret) ToolTipService.SetToolTip(tile, $"{a.Name}\n{a.Description}\n\n" + L.F("Steam name: {0}", a.ApiName));
        return tile;
    }

    /// <summary>Places the tiles that pass the filter in as many columns as fit.</summary>
    void LayoutAchievements()
    {
        if (_achTiles.Count == 0) return;
        _achLaidOutColumns = _achColumns;
        var filter = (AchFilterBar.SelectedItem as SelectorBarItem)?.Tag as string ?? "all";
        var tiles = _achTiles.Where(t => filter == "all" || (t.Tag is true) == (filter == "unlocked")).ToList();
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
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message); }
    }
}
