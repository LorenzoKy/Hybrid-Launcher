using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Media;
using HybridLauncher.Core.Steam;
using HybridLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace HybridLauncher.Views;

/// <summary>
/// The grid view of the games: cover art tiles, pinned games first, then one block per launcher (in the user's order, each
/// foldable and draggable by its heading), or one block of every game in the chosen order. The tiles grow to fill the page:
/// few games get big covers, many get more columns. Each cover carries Hydra-style tags that shorten on small covers.
/// </summary>
public sealed partial class LibraryPage : Page
{
    const double MinTile = 180, MaxTile = 330, Gap = 16;
    // The zoom range (Ctrl + wheel, the slider) and the wheel's step: 8% of the cover width per notch.
    const double MinZoom = 120, MaxZoom = 480, ZoomStep = 1.08;

    // Covers decoded once per session and shared by every tile that shows the game.
    static readonly Dictionary<string, ImageSource> Covers = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Landscape = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops a game's decoded cover, so the next time the grid is drawn it is read again (its page was changed).</summary>
    public static void ForgetCover(string gameId)
    {
        Covers.Remove(gameId);
        Landscape.Remove(gameId);
    }

    CancellationTokenSource _cts = new();
    // The library's order the covers were last built in (Library.OrderKey).
    string? _order;
    string _filter = "";

    /// <summary>The pane's search text the covers were last built for.</summary>
    public string Filter => _filter;
    int _widestRow = 1;
    double _tileWidth = 204;
    readonly List<CoverTile> _tiles = [];

    /// <summary>Self-test: the second cover as under the pointer (it pops, Play shows), or back to rest.</summary>
    internal void SelfTestHover(bool on) { if (_tiles.Count > 0) _tiles[Math.Min(1, _tiles.Count - 1)].SelfTestHover(on); }
    readonly List<GridView> _grids = [];
    bool _ready, _syncing;
    ulong _lastWheel;
    DispatcherQueueTimer? _saveTimer, _dragTimer;
    bool _dragging;
    double _pendingZoom = -1;

    /// <summary>Self-test: the page's scroller (scroll smoothness).</summary>
    internal ScrollView SelfTestScroller => Scroller;
    /// <summary>Self-test: the placeholder fill of a cover (Collapsed once its picture loaded), null when it has none.</summary>
    internal static Visibility? CoverFill(FrameworkElement tileRoot) => CoverTile.BackdropOf(tileRoot);

    /// <summary>Self-test (perf): the first cover shown, to click.</summary>
    internal FrameworkElement? SelfTestFirstTile() =>
        _grids.Append(PinnedGrid).Where(g => g.Items.Count > 0).Select(g => g.ContainerFromIndex(0) as FrameworkElement).FirstOrDefault(c => c is not null);

    // A game played while the page was away: the Last played section is drawn again when it comes back.
    bool _recentStale;

    public LibraryPage()
    {
        using var __perf = UiPerf.Scope("LibraryPage.ctor");
        InitializeComponent();
        Loc.Apply(this);
        Scroller.SizeChanged += (_, e) => Center(e.NewSize.Width);
        // The mouse wheel glides on the compositor (Ctrl + wheel still zooms: CompositorWheel leaves it alone).
        Helpers.CompositorWheel.Attach(Scroller);
        // The scroller would scroll on the wheel itself: the Ctrl case is taken before it (on the content) and, for the margins around it, on the scroller.
        var wheel = new PointerEventHandler(OnWheel);
        Root.AddHandler(UIElement.PointerWheelChangedEvent, wheel, true);
        Scroller.AddHandler(UIElement.PointerWheelChangedEvent, wheel, true);
        // Dragging the slider: see ZoomDrag.
        ZoomSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => ZoomDrag(true)), true);
        ZoomSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => ZoomDrag(false)), true);
        ZoomSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => ZoomDrag(false)), true);
        var pinRule = Divider();
        Grid.SetColumn(pinRule, 2);
        PinnedHead.Children.Add(pinRule);
        PinnedGrid.GotFocus += (_, e) => FocusTile(e.OriginalSource, true);
        PinnedGrid.LostFocus += (_, e) => FocusTile(e.OriginalSource, false);
        _ready = true;
        // Play time and last played are read in the background: the Last played section and an order that needs them follow.
        Library.StatsReady += () =>
        {
            var f = AppState.Settings.Library;
            // Only an order the new stats really change redraws the covers (a session's minutes counted rarely does).
            bool order = (f.Sort is not "name" and not "added" || f.Tech.Count > 0) && Library.OrderKey() != _order;
            if (!IsLoaded) { if (order) _shown = default; else _recentStale = true; return; }
            if (order) Reload(_filter);
            else FillRecent();
        };
        // The owned games' hours and achievements came in: their covers take the order that needs them (once).
        Library.OwnedStatsReady += () =>
        {
            var f = AppState.Settings.Library;
            if (!f.ShowNotInstalled || f.Sort is "name" or "added" or "tech") return;
            if (IsLoaded) Reload(_filter); else _shown = default;
        };
        // An achievement unlocked while the app runs: the Latest achievements panel and the summary line follow.
        Library.UnlocksChanged += () =>
        {
            if (IsLoaded) FillRecent();
            else _recentStale = true;
        };
        // A game started or closed (from here or anywhere): its last played and hours follow, so does the Last played section.
        Library.WatchSessions(DispatcherQueue);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        using var __perf = UiPerf.Scope("LibraryPage.NavigatedTo");
        base.OnNavigatedTo(e);
        // The page stays in memory: coming back with nothing changed (same search, no settings saved, no new scan) keeps the
        // covers already made, and the scroll position, instead of building them all again.
        var filter = e.Parameter as string ?? "";
        if (_shown != (filter, SettingsKey(), AppState.GamesVersion, OwnedLibrary.IsLoaded))
        {
            // Why the covers are made again (the snappy pillar: a return must not rebuild them without a reason worth it).
            if (_shown is { } was)
                Log.Info($"Library rebuilt: {(was.Item1 != filter ? "search " : "")}{(was.Item3 != AppState.GamesVersion ? "games " : "")}{(was.Item4 != OwnedLibrary.IsLoaded ? "owned " : "")}"
                    + $"settings [{string.Join(", ", Helpers.SettingsKeys.Changed(_shownParts, Helpers.SettingsKeys.Parts(AppState.Settings)).Take(6))}]");
            Reload(filter);
        }
        else
        {
            if (_recentStale) FillRecent();
            // Groups folded or opened from the side list while this page was away: the folds are applied in place (a fold no
            // longer counts as a settings change that rebuilds every cover, 2.1-rc.3 audit).
            foreach (var key in _folds.Keys.ToList()) ApplyFold(key);
        }
        _recentStale = false;
        _ = Library.LoadStatsAsync(DispatcherQueue);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        foreach (var t in _tiles) t.Rest();
        // The covers still loading go on: the page is kept in memory and shows them when it comes back (Reload cancels them).
        // A zoom still waiting for its save is written now: the page may not come back.
        if (_saveTimer is { IsRunning: true })
        {
            _saveTimer.Stop();
            SettingsStore.Save();
        }
    }

    /// <summary>
    /// Automatic: as many columns as fit with tiles at least <see cref="MinTile"/> wide; with fewer games than that, fewer and
    /// bigger tiles (up to <see cref="MaxTile"/>). Zoomed (<c>TileSize</c> above 0): the column count nearest to that cover width,
    /// and the tiles share the row exactly. The block is centred and the titles line up with the first column.
    /// </summary>
    void Center(double width)
    {
        if (width <= 0) return;
        // On a whole device pixel once centred (an even number of free pixels each side): the covers' text stays sharp.
        double avail = Helpers.CompositorWheel.SharpWidth(Scroller, width, Math.Max(MinTile, width - 48));
        double zoom = AppState.Settings.Library.TileSize;
        int cols;
        double tile;
        if (zoom > 0)
        {
            // Zoomed, the grid keeps the full width whatever the count (rows just end early): a narrower, re-centred block made
            // the header jump while zooming.
            cols = ColsFor(avail, zoom);
            tile = Math.Floor((avail - Gap * (cols - 1)) / cols);
        }
        else (cols, tile) = AutoSize(avail, Scroller.ActualHeight);
        // The header always spans the page, so it (and the zoom slider under the pointer) never moves while zooming; only the
        // block of covers takes the width of the columns it fills, centred under it, in Auto and zoomed alike.
        Root.Width = avail;
        int used = Math.Clamp(_widestRow, 1, cols);
        Block.Width = Math.Min(avail, used * (tile + Gap) - Gap);
        if (Math.Abs(tile - _tileWidth) >= 0.5)
        {
            _tileWidth = tile;
            foreach (var t in _tiles) t.Resize(tile);
            SizeSlots();
            // The not-installed covers made so far are resized in place (the others are made at this width).
            ResizeOwned(tile);
        }
        SyncZoom();
    }

    /// <summary>
    /// Automatic size (2.0): the biggest covers with which every cover on the page fits in the window at once (the covers'
    /// bar scrolled to the top). For each column count the covers are as wide as the row allows and as tall as the viewport
    /// allows for that many rows; the count giving the widest covers wins. Never past <see cref="MaxZoom"/>. A library too big
    /// to fit with covers of <see cref="MinTile"/> keeps the old rule: as many columns as fit at that width.
    /// </summary>
    (int Cols, double Tile) AutoSize(double avail, double viewport)
    {
        int widest = Math.Max(1, _widestRow);
        double TileFor(int c) => Math.Floor((avail - Gap * (c - 1)) / c);
        if (viewport > 0 && _sections.Count > 0)
        {
            // The room under the covers' bar: the bar (about 40 px with its spacing) and the page's bottom padding.
            double room = viewport - 40 - 32;
            int bestCols = 0;
            double best = 0;
            for (int c = 1; c <= widest; c++)
            {
                double wide = Math.Min(MaxZoom, TileFor(c));
                if (wide < MinTile) break;
                var (fixedPart, rows) = CoversHeight(c);
                // rows x (1.5 t + extra + gap) + fixed <= room
                double tall = rows == 0 ? wide : Math.Floor(((room - fixedPart) / rows - _tileExtra - Gap) / 1.5);
                double t = Math.Min(wide, tall);
                if (t > best + 0.5) { best = t; bestCols = c; }
            }
            if (bestCols > 0 && best >= MinTile) return (bestCols, best);
        }
        int cols = Math.Max(1, (int)((avail + Gap) / (MinTile + Gap)));
        if (widest < cols) cols = widest;
        return (cols, Math.Min(MaxTile, TileFor(cols)));
    }

    /// <summary>The covers block with <paramref name="cols"/> columns: its headings' height and its rows of covers.</summary>
    (double Fixed, int Rows) CoversHeight(int cols)
    {
        double h = 0;
        int rows = 0, prev = -1;
        foreach (var (kind, key, count, open) in _sections)
        {
            // Pinned and the rest are 20 px apart (the block's spacing), the groups 14 (theirs).
            if (prev >= 0) h += prev == 0 ? 20 : 14;
            prev = kind;
            // Headings: Pinned (its line and spacing), a launcher group (padded, clickable), Not installed; the one grid of
            // every game (not grouped) has none.
            h += kind switch { 0 => 28, 1 => key.Length > 0 ? 38 : 0, _ => 34 };
            if (open && count > 0) rows += (int)Math.Ceiling(count / (double)cols);
        }
        return (h, rows);
    }

    // What the covers block holds, for the automatic size: (0 pinned / 1 group / 2 not installed, covers, open).
    readonly List<(int Kind, string Key, int Count, bool Open)> _sections = [];
    double _tileExtra;

    static int ColsFor(double avail, double size) => Math.Max(1, (int)Math.Round((avail + Gap) / (size + Gap), MidpointRounding.AwayFromZero));

    /// <summary>The slider shows the chosen size, or the automatic width while nothing is chosen; Auto is only live while zoomed.</summary>
    void SyncZoom()
    {
        double zoom = AppState.Settings.Library.TileSize;
        double shown = Math.Clamp(zoom > 0 ? zoom : _tileWidth, MinZoom, MaxZoom);
        _syncing = true;
        try
        {
            if (!_dragging && Math.Abs(ZoomSlider.Value - ToPercent(shown)) >= 0.5) ZoomSlider.Value = ToPercent(shown);
        }
        finally { _syncing = false; }
        ZoomAutoButton.IsEnabled = zoom > 0;
    }

    /// <summary>
    /// Sets the cover width (0 = automatic) and resizes the existing tiles in place, never rebuilding the grid. The scroll position
    /// follows: the point at <paramref name="anchor"/> (a y in the viewport) keeps its place in the content as the rows change height.
    /// </summary>
    void SetZoom(double size, double anchor)
    {
        var f = AppState.Settings.Library;
        size = size <= 0 ? 0 : Math.Round(Math.Clamp(size, MinZoom, MaxZoom), 1);
        if (size == f.TileSize) return;
        double extent = Scroller.ExtentHeight, offset = Scroller.VerticalOffset, before = _tileWidth;
        f.TileSize = size;
        Center(Scroller.ActualWidth);
        ScheduleSave();
        if (extent > 0 && Math.Abs(_tileWidth - before) >= 0.5)
        {
            Scroller.UpdateLayout();
            double target = (offset + anchor) * Scroller.ExtentHeight / extent - anchor;
            Scroller.ChangeView(null, Math.Max(0, target), null, true);
        }
    }

    /// <summary>Writes the settings once the zoom has been quiet for a moment: a wheel spin is dozens of changes.</summary>
    void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SettingsStore.Save();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>The cover width the zoom works from: the chosen one, or the one automatic sizing gives now.</summary>
    /// <summary>The slider reads 0 (smallest covers) to 100 (biggest), not pixels.</summary>
    static double ToPercent(double size) => Math.Round((size - MinZoom) / (MaxZoom - MinZoom) * 100);

    static double FromPercent(double percent) => MinZoom + (MaxZoom - MinZoom) * Math.Clamp(percent, 0, 100) / 100;

    double CurrentZoom() => AppState.Settings.Library.TileSize is > 0 and var z ? z : Math.Max(MinZoom, _tileWidth);

    /// <summary>Ctrl + wheel: bigger covers up, smaller down. The page does not scroll while Ctrl is held; without it the wheel is untouched.</summary>
    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        // The event's modifiers can miss a Ctrl held before the pointer came over the page: the key's own state is asked too.
        bool ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)
            || InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        e.Handled = true;
        var point = e.GetCurrentPoint(Scroller);
        // The content and the scroller both get the event: one zoom per wheel tick.
        if (point.Timestamp == _lastWheel) return;
        _lastWheel = point.Timestamp;
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || InZoomBar(e.OriginalSource as DependencyObject)) return;
        SetZoom(CurrentZoom() * Math.Pow(ZoomStep, delta / 120.0), point.Position.Y);
    }

    bool InZoomBar(DependencyObject? node)
    {
        for (int i = 0; node is not null && i < 64; i++, node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ZoomBar)) return true;
        return false;
    }

    void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _syncing || Math.Abs(e.NewValue - ToPercent(CurrentZoom())) < 0.5) return;
        if (!_dragging)
        {
            SetZoom(FromPercent(e.NewValue), 0);
            return;
        }
        // While the thumb is dragged the grid follows at most every 40 ms (the last value wins): resizing every tile for each
        // pixel of the drag made the thumb stutter and jump.
        _pendingZoom = FromPercent(e.NewValue);
        if (_dragTimer is null)
        {
            _dragTimer = DispatcherQueue.CreateTimer();
            _dragTimer.Interval = TimeSpan.FromMilliseconds(40);
            _dragTimer.IsRepeating = false;
            _dragTimer.Tick += (_, _) => ApplyPendingZoom();
        }
        if (!_dragTimer.IsRunning) _dragTimer.Start();
    }

    void ApplyPendingZoom()
    {
        if (_pendingZoom < 0) return;
        double size = _pendingZoom;
        _pendingZoom = -1;
        SetZoom(size, 0);
    }

    /// <summary>The slider's thumb is held: the slider is not set back from the grid meanwhile (that fought the pointer).</summary>
    void ZoomDrag(bool on)
    {
        _dragging = on;
        if (on) return;
        _dragTimer?.Stop();
        ApplyPendingZoom();
        SyncZoom();
    }

    void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(1);

    void ZoomAuto_Click(object sender, RoutedEventArgs e) => SetZoom(0, 0);

    /// <summary>A button press: on to the next size that changes the number of columns (a few percent would show nothing).</summary>
    void StepZoom(int direction)
    {
        double avail = Math.Max(MinTile, Scroller.ActualWidth - 48);
        double size = CurrentZoom();
        int cols = ColsFor(avail, size);
        for (int i = 0; i < 80; i++)
        {
            size = Math.Clamp(direction > 0 ? size * 1.03 : size / 1.03, MinZoom, MaxZoom);
            if (ColsFor(avail, size) != cols || size is MinZoom or MaxZoom) break;
        }
        SetZoom(size, 0);
    }

    /// <summary>The grids' panels keep the first item's size: give them the new slot size so the tiles reflow.</summary>
    void SizeSlots()
    {
        if (_tiles.Count == 0 || _tileWidth <= 0) return;
        var first = _tiles[0].Root;
        first.Measure(new Windows.Foundation.Size(_tileWidth, double.PositiveInfinity));
        double height = first.DesiredSize.Height;
        double extra = Math.Max(0, height - Math.Round(_tileWidth * 1.5));
        if (Math.Abs(extra - _tileExtra) >= 1)
        {
            // The automatic size counted on another height under the art: sized again with the real one.
            _tileExtra = extra;
            if (AppState.Settings.Library.TileSize <= 0) DispatcherQueue.TryEnqueue(() => Center(Scroller.ActualWidth));
        }
        foreach (var g in _grids.Append(PinnedGrid))
        {
            if (g.ItemsPanelRoot is ItemsWrapGrid panel)
            {
                panel.ItemWidth = _tileWidth + Gap;
                panel.ItemHeight = height + Gap;
            }
        }
    }

    /// <summary>Redraws the tiles for the games matching <paramref name="filter"/> (the pane's search box) and the library filters.</summary>
    // What the covers on the page were built from (see OnNavigatedTo).
    (string, int, int, bool)? _shown;
    IReadOnlyDictionary<string, int>? _shownParts;

    /// <summary>
    /// The settings as the covers see them (Helpers.SettingsKeys): before 2.0.0-rc.4 any save counted, and opening a game saves
    /// the game opened last, so every return to the library rebuilt every cover (200-260 ms, then the covers loading again).
    /// </summary>
    static int SettingsKey() => Helpers.SettingsKeys.Of(AppState.Settings);

    /// <summary>Builds the covers again only when the search, the games or the settings the grid reads changed.</summary>
    public void ReloadIfChanged(string filter)
    {
        if (_shown != (filter, SettingsKey(), AppState.GamesVersion, OwnedLibrary.IsLoaded)) Reload(filter);
    }

    public void Reload(string filter)
    {
        using var __perf = UiPerf.Scope("LibraryPage.Reload");
        _filter = filter;
        _shown = (filter, SettingsKey(), AppState.GamesVersion, OwnedLibrary.IsLoaded);
        _shownParts = Helpers.SettingsKeys.Parts(AppState.Settings);
        _order = Library.OrderKey();
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _tiles.Clear();
        _grids.Clear();
        _ownedBlocks.Clear();
        var f = AppState.Settings.Library;

        bool Matches(Game g) => _filter.Length == 0 || g.Title.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) || g.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase);
        // A game owned but not installed that was opened once is in the app's list as a page-only game: it shows with the
        // other not-installed games, never as an installed cover.
        var visible = Library.Apply(AppState.VisibleGames.Where(g => !g.NotInstalled).Where(Matches));
        var pinned = AppState.PinnedGames().Where(g => visible.Contains(g)).ToList();
        var rest = visible.Where(g => !AppState.IsPinned(g.Id)).ToList();

        PinnedGrid.Items.Clear();
        foreach (var game in pinned) PinnedGrid.Items.Add(Tile(game, ct));
        PinnedSection.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Groups.Children.Clear();
        _folds.Clear();
        _sections.Clear();
        if (pinned.Count > 0) _sections.Add((0, "", pinned.Count, true));
        // Games you own on a linked launcher but have not installed (Filters → Not installed), with Install on their cover: in
        // their launcher's group when the library is grouped (2.1-rc.3), else in a block of their own after the others.
        if (f.ShowNotInstalled && !f.OptiOnly && !OwnedLibrary.IsLoaded) _ = LoadOwnedAsync();
        var owned = OwnedShown(_filter);
        if (f.GroupByLauncher)
        {
            _widestRow = pinned.Count;
            var ownedBy = owned.GroupBy(g => "store:" + g.Store).ToDictionary(g => g.Key, g => g.ToList());
            var groups = Library.Groups(rest.Concat(ownedBy.Where(o => !rest.Any(g => StoreLogos.GroupOf(g).Key == o.Key)).Select(o => OwnedLibrary.AsGame(o.Value[0]))));
            foreach (var (key, list) in groups)
            {
                var games = list.Where(g => !g.NotInstalled).ToList();
                var mine = ownedBy.GetValueOrDefault(key) ?? [];
                Groups.Children.Add(Section(key, games, mine, list[0], ct));
                _sections.Add((1, key, games.Count + mine.Count, !Library.IsCollapsed(key)));
                _widestRow = Math.Max(_widestRow, Math.Max(games.Count, mine.Count));
            }
            _widestRow = Math.Max(_widestRow, pinned.Count);
        }
        else
        {
            var grid = NewGrid();
            foreach (var game in rest) grid.Items.Add(Tile(game, ct));
            Groups.Children.Add(grid);
            if (rest.Count > 0) _sections.Add((1, "", rest.Count, true));
            _widestRow = Math.Max(pinned.Count, rest.Count);
            if (owned.Count > 0)
            {
                var section = new StackPanel { Spacing = 8 };
                section.Children.Add(OwnedHeading(owned.Count, false));
                section.Children.Add(OwnedBlock(owned));
                Groups.Children.Add(section);
                _sections.Add((2, "", owned.Count, true));
                // The block is as wide as its widest row: the not-installed covers count too.
                _widestRow = Math.Max(_widestRow, owned.Count);
            }
        }
        _tileWidth = -1;
        Center(Scroller.ActualWidth);
        // The grids create their panels on first layout.
        DispatcherQueue.TryEnqueue(SizeSlots);

        int total = pinned.Count + rest.Count;
        int all = AppState.VisibleGames.Count(g => !g.NotInstalled);
        // The order is on its own pill now: the line says only how many.
        CountText.Text = (_filter.Length > 0 ? L.F(total == 1 ? "{0} game matching \"{1}\"" : "{0} games matching \"{1}\"", total, _filter)
                : f.IsFiltering && total != all ? L.F("{0} of {1} games (filtered)", total, all) : L.N(total, "{0} game", "{0} games"))
            + (owned.Count > 0 ? "  ·  " + L.F("{0} not installed", owned.Count) : "");
        FillToolbar();
        EmptyText.Visibility = total == 0 && owned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FillRecent();
        EmptyText.Text = _filter.Length > 0 ? L.T("No game matches the search.")
            : f.IsFiltering ? L.T("No game passes the filters. Open Filters and reset them.")
            : L.T("No games found. Use “Add a game or games folder” at the bottom of the pane.");
    }

    async Task LoadOwnedAsync()
    {
        await OwnedLibrary.LoadAsync();
        if (AppState.Settings.Library.ShowNotInstalled) Reload(_filter);
        // Their hours and achievements, for the orders that need them.
        _ = Library.LoadOwnedStatsAsync(DispatcherQueue);
    }

    // ---------- launcher groups: fold, drag to reorder ----------

    readonly Dictionary<string, (FontIcon Chevron, FrameworkElement Grid, bool Open)> _folds = [];

    /// <summary>Folds or opens a group's covers as the setting now says (from its heading here or in the side list).</summary>
    public void ApplyFold(string key)
    {
        if (!_folds.TryGetValue(key, out var f)) return;
        bool fold = Library.IsCollapsed(key);
        if (f.Open != fold) return;
        _folds[key] = f with { Open = !fold };
        // The automatic size counts only the open groups: it follows.
        int at = _sections.FindIndex(x => x.Kind == 1 && x.Key == key);
        if (at >= 0)
        {
            _sections[at] = _sections[at] with { Open = !fold };
            if (AppState.Settings.Library.TileSize <= 0) DispatcherQueue.TryEnqueue(() => Center(Scroller.ActualWidth));
        }
        Motion.Turn(f.Chevron, !fold);
        if (fold) Motion.Close(f.Grid);
        else
        {
            Motion.Open(f.Grid);
            DispatcherQueue.TryEnqueue(SizeSlots);
        }
    }

    StackPanel Section(string key, List<Game> games, List<OwnedGame> owned, Game sample, CancellationToken ct)
    {
        bool collapsed = Library.IsCollapsed(key);
        var chevron = Motion.Chevron(!collapsed, 11);
        var head = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 10, 6), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(chevron);
        var label = StoreLogos.GroupHeader(sample, games.Count, (Style)HybridLauncher.Helpers.ThemeRes.Current["GroupHeader"]);
        Grid.SetColumn(label, 1);
        head.Children.Add(label);
        // A hairline that fades out to the right: the groups read as chapters of the page (2.1).
        var rule = Divider();
        Grid.SetColumn(rule, 2);
        head.Children.Add(rule);
        ToolTipService.SetToolTip(head, L.T("Click to fold or open. Drag to move this group."));

        var grid = NewGrid();
        foreach (var game in games) grid.Items.Add(Tile(game, ct));
        // The group's games you own but have not installed, under its installed ones (2.1-rc.3); folded with them.
        FrameworkElement body = grid;
        if (owned.Count > 0)
        {
            var both = new StackPanel { Spacing = 8 };
            if (games.Count > 0) both.Children.Add(grid);
            else _grids.Remove(grid);
            both.Children.Add(OwnedHeading(owned.Count, true));
            both.Children.Add(OwnedBlock(owned));
            body = both;
        }
        body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        var section = new StackPanel { Spacing = 6, Tag = key, RenderTransform = new TranslateTransform() };
        // The grid's top room (see NewGrid) reaches under the heading: the heading takes the pointer there.
        Canvas.SetZIndex(head, 1);
        section.Children.Add(head);
        section.Children.Add(body);

        head.PointerEntered += (_, _) => head.Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["SubtleFillColorSecondaryBrush"];
        head.PointerExited += (_, _) => head.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _folds[key] = (chevron, body, !collapsed);
        DragToReorder(head, section, Groups, () =>
        {
            Library.SetCollapsed(key, !Library.IsCollapsed(key));
            ApplyFold(key);
            // Only that group of the side list follows: nothing else is redrawn.
            App.Window?.ApplyFold(key);
        }, keys =>
        {
            Library.SaveGroupOrder(keys);
            App.Window?.RefreshList();
        });
        return section;
    }

    static Brush? _divider;

    /// <summary>A thin line from the accent fading to nothing, beside a group's heading.</summary>
    internal static Border Divider()
    {
        if (_divider is null)
        {
            var accent = Palettes.Current?.Accent ?? Microsoft.UI.Colors.White;
            var b = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
            b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x70, accent.R, accent.G, accent.B), Offset = 0 });
            b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x18, 255, 255, 255), Offset = 0.35 });
            b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 255, 255, 255), Offset = 1 });
            _divider = b;
        }
        return new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0), Background = _divider, IsHitTestVisible = false };
    }

    /// <summary>A new theme: the shared divider takes its accent.</summary>
    internal static void ForgetLook() => _divider = null;

    /// <summary>
    /// A press on <paramref name="handle"/> that does not move is a click; moved more than a few pixels, <paramref name="item"/>
    /// follows the pointer and swaps with its neighbours in <paramref name="panel"/>; the new order of the items' tags is saved.
    /// </summary>
    internal static void DragToReorder(FrameworkElement handle, FrameworkElement item, Panel panel, Action click, Action<List<string>> reordered)
    {
        double startY = 0, grab = 0;
        bool pressed = false, dragging = false;
        List<string>? before = null;
        double Top(UIElement e) => e.TransformToVisual(panel).TransformPoint(new Windows.Foundation.Point(0, 0)).Y - (e.RenderTransform is TranslateTransform t ? t.Y : 0);

        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            pressed = true;
            dragging = false;
            startY = e.GetCurrentPoint(panel).Position.Y;
            grab = startY - Top(item);
            handle.CapturePointer(e.Pointer);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!pressed) return;
            double y = e.GetCurrentPoint(panel).Position.Y;
            if (!dragging)
            {
                if (Math.Abs(y - startY) < 8) return;
                dragging = true;
                before = panel.Children.OfType<FrameworkElement>().Select(c => c.Tag as string ?? "").ToList();
                Canvas.SetZIndex(item, 10);
                item.Opacity = 0.85;
            }
            double wanted = y - grab;
            // Compare the dragged heading's top with the neighbours' middles: tall open groups still swap easily.
            for (int guard = 0; guard < 32; guard++)
            {
                int index = panel.Children.IndexOf(item);
                if (index + 1 < panel.Children.Count && panel.Children[index + 1] is FrameworkElement next && wanted + 24 > Top(next) + Math.Min(next.ActualHeight, 120) / 2)
                    Swap(index + 1, index);
                else if (index > 0 && panel.Children[index - 1] is FrameworkElement prev && wanted < Top(prev) + Math.Min(prev.ActualHeight, 120) / 2)
                    Swap(index - 1, index + 1);
                else break;
            }
            ((TranslateTransform)item.RenderTransform).Y = wanted - Top(item);

            // The neighbour moves, never the dragged group: taking it out of the panel would drop its pointer capture.
            void Swap(int from, int to)
            {
                var other = panel.Children[from];
                panel.Children.RemoveAt(from);
                panel.Children.Insert(from < to ? to - 1 : to, other);
                panel.UpdateLayout();
            }
        };
        void End()
        {
            if (!pressed) return;
            pressed = false;
            handle.ReleasePointerCaptures();
            if (!dragging)
            {
                click();
                return;
            }
            dragging = false;
            item.Opacity = 1;
            Canvas.SetZIndex(item, 0);
            ((TranslateTransform)item.RenderTransform).Y = 0;
            var after = panel.Children.OfType<FrameworkElement>().Select(c => c.Tag as string ?? "").ToList();
            if (before is null || !after.SequenceEqual(before)) reordered(after.Where(k => k.Length > 0).ToList());
        }
        handle.PointerReleased += (_, _) => End();
        handle.PointerCaptureLost += (_, _) =>
        {
            // Lost without a release (a dialog, alt-tab): drop the drag where it is.
            if (!pressed) return;
            if (dragging) End();
            else pressed = false;
        };
    }

    GridView NewGrid()
    {
        var grid = new GridView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            // Room around the covers inside the grid's own bounds (taken back by the margin, so nothing moves): a cover
            // that grows under the pointer in the first row or column is not cut at the grid's edge.
            Padding = new Thickness(12, 18, 0, 0),
            // The slots' right gap sits past the block's edge, so the covers line up with the titles on both sides.
            Margin = new Thickness(-12, -18, -Gap, 0),
            ItemContainerStyle = (Style)Resources["TileItem"],
        };
        ScrollViewer.SetVerticalScrollMode(grid, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Hidden);
        grid.ItemClick += Tile_Click;
        grid.Loaded += (_, _) => SizeSlots();
        // Keyboard and controller focus pop the cover like the pointer (2.1).
        grid.GotFocus += (_, e) => FocusTile(e.OriginalSource, true);
        grid.LostFocus += (_, e) => FocusTile(e.OriginalSource, false);
        _grids.Add(grid);
        return grid;
    }

    static void FocusTile(object source, bool on)
    {
        if (source is not GridViewItem { Content: StackPanel root } item || !CoverTile.Tiles.TryGetValue(root, out var tile)) return;
        if (on && item.FocusState != FocusState.Keyboard) return;
        tile.Focus(on);
    }

    void Tile_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement { Tag: string id }) App.Window?.OpenGame(id);
    }

    // ---------- sort and filters ----------

    static readonly (string Key, string Name)[] Sorts =
        [("name", "Name"), ("recent", "Last played"), ("added", "Date added"), ("playtime", "Time played"), ("achievements", "Achievements unlocked"), ("tech", "Most tech tags")];

    static string SortName(string key) => L.T(Sorts.FirstOrDefault(s => s.Key == key).Name ?? "Name");


    /// <summary>
    /// The Filters flyout, in three plain steps: how the games are ordered, which games show, and how the covers look. Every
    /// change applies at once; Reset puts the order and the filters back (the cover size and the look stay). The pane's
    /// filter button opens the same panel for the list's own filters (<paramref name="list"/>).
    /// </summary>
    internal static FrameworkElement FiltersPanel(Action hide, bool full = true, bool list = false)
    {
        // The side list's panel sets the list's own filters; the library's bar sets the grid's (2.1-rc.4).
        var f = list ? Library.ListFilter : AppState.Settings.Library;
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        void Changed() => FiltersChanged(f);
        TextBlock Head(string text, string hint)
        {
            var t = new TextBlock { Style = (Style)res["GroupHeader"], Margin = new Thickness(0, 10, 0, 0), Text = text };
            ToolTipService.SetToolTip(t, hint);
            return t;
        }
        CheckBox Check(string text, bool on, Action<bool> set, string? tip = null)
        {
            var cb = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on, MinWidth = 0 };
            if (tip is not null) ToolTipService.SetToolTip(cb, tip);
            cb.Click += (_, _) => { set(cb.IsChecked == true); Changed(); };
            return cb;
        }

        // Wide enough for its longest line, which wraps rather than being cut (2.1-rc.3: the side list's panel cut its lines).
        var panel = new StackPanel { Spacing = 4, Width = 320 };

        if (full)
        {
            // 1. Order and 2. launchers: the same lists as the library's bar, each entry with its own icon.
            panel.Children.Add(Head(L.T("ORDER"), L.T("How the games are sorted")));
            panel.Children.Add(SortList(() => { }, f));
            panel.Children.Add(Head(L.T("LAUNCHERS"), L.T("Which launchers' games are listed")));
            panel.Children.Add(LaunchersList(null, f));
            // 3. The collection (the library has its own pill for it).
            panel.Children.Add(Head(L.T("COLLECTION"), L.T("Only the favorites or the games of one collection")));
            panel.Children.Add(CollectionList(hide, f));
        }
        panel.Children.Add(Head(L.T("SHOW"), L.T("Which games are listed")));
        panel.Children.Add(Check(L.T("Also games you own but have not installed"), f.ShowNotInstalled, v => f.ShowNotInstalled = v,
            L.T("Games you own on Steam, Epic or GOG but have not installed, with an Install button (accounts in Downloads).")));
        panel.Children.Add(Check(L.T("Only games with OptiScaler installed"), f.OptiOnly, v => f.OptiOnly = v));
        panel.Children.Add(new TextBlock { Text = L.T("Only games that support"), Style = (Style)res["RowDescription"], Margin = new Thickness(0, 6, 0, 0) });
        var techRow = new WrapPanel(6, []);
        foreach (var (key, name) in new[] { ("DLSS", "DLSS"), ("FSR", "FSR"), ("XeSS", "XeSS"), ("FG", "Frame generation") })
        {
            var b = new ToggleButton { Content = L.T(name), IsChecked = f.Tech.Contains(key), Padding = new Thickness(10, 4, 10, 5), MinWidth = 0 };
            b.Click += (_, _) =>
            {
                if (b.IsChecked == true) f.Tech.Add(key); else f.Tech.Remove(key);
                Changed();
            };
            techRow.Children.Add(b);
        }
        panel.Children.Add(techRow);

        // 3. How the covers look.
        panel.Children.Add(Head(L.T("LOOK"), L.T("How the list and the covers look")));
        panel.Children.Add(Check(L.T("Group by launcher"), f.GroupByLauncher, v => f.GroupByLauncher = v));
        // Tags on the covers: all of them, then each kind on its own (greyed while all are off). The list has no covers.
        if (!list)
        {
            var kinds = new WrapPanel(4, []) { Margin = new Thickness(26, 0, 0, 0) };
            void Kinds() { foreach (var c in kinds.Children.OfType<CheckBox>()) c.IsEnabled = AppState.Settings.ShowCoverOverlays; }
            panel.Children.Add(Check(L.T("Tags on the covers"), AppState.Settings.ShowCoverOverlays, v => { AppState.Settings.ShowCoverOverlays = v; Kinds(); }));
            foreach (var (key, label) in AppSettings.CoverTagKinds)
            {
                var cb = Check(L.T(label), AppState.Settings.ShowCoverTags && key == "tech" || key != "tech" && !AppState.Settings.HiddenCoverTags.Contains(key), v => AppState.Settings.SetCoverTag(key, v));
                cb.MinWidth = 130;
                kinds.Children.Add(cb);
            }
            Kinds();
            panel.Children.Add(kinds);
        }

        var reset = new Button { Content = L.T("Reset order and filters"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTipService.SetToolTip(reset, L.T("The cover size and the look stay as they are."));
        reset.Click += (_, _) =>
        {
            // The cover size and the grouping are not filters: they stay.
            if (list) AppState.Settings.ListFilter = new LibraryFilter { GroupByLauncher = f.GroupByLauncher, DefaultsVersion = f.DefaultsVersion };
            else AppState.Settings.Library = new LibraryFilter { TileSize = f.TileSize, GroupByLauncher = f.GroupByLauncher, DefaultsVersion = f.DefaultsVersion };
            hide();
            // The filter just made, not the one this panel was opened on (2.1-rc.10 audit: the old one no longer was the list's,
            // so a Reset in the side list redrew the library and left the list as it was).
            FiltersChanged(list ? AppState.Settings.ListFilter : AppState.Settings.Library);
        };
        panel.Children.Add(reset);
        // The flyout scrolls by itself: a scroller of our own inside it drew a second bar.
        panel.Padding = new Thickness(0, 0, 8, 0);
        return panel;
    }

    // ---------- tiles ----------

    FrameworkElement Tile(Game game, CancellationToken ct)
    {
        var tile = new CoverTile(game, _tileWidth > 0 ? _tileWidth : 204, ct);
        _tiles.Add(tile);
        // The menu is made on the first right-click: dozens of covers do not each build one they may never show.
        tile.Root.ContextRequested += (sender, e) =>
        {
            if (App.Window is not { } w || sender is not FrameworkElement root) return;
            var menu = w.GameMenu(game);
            if (e.TryGetPosition(root, out var at)) menu.ShowAt(root, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at });
            else menu.ShowAt(root);
            e.Handled = true;
        };
        return tile.Root;
    }

    /// <summary>One cover with its chips; resized in place when the page width changes, the chips shortening on small covers.</summary>
    sealed class CoverTile
    {
        public readonly StackPanel Root;
        readonly Grid _art;
        readonly Game _game;
        readonly StackPanel _mods = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
        readonly WrapPanel _tags;
        readonly List<string> _tagNames = [];
        TextBlock? _timeText;
        long _minutes;
        double _width;

        static SolidColorBrush White => new(Microsoft.UI.Colors.White);

        public CoverTile(Game game, double width, CancellationToken ct)
        {
            _game = game;
            var placeholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 10, Padding = new Thickness(12) };
            var icon = new Image { Width = 44, Height = 44 };
            placeholder.Children.Add(icon);
            placeholder.Children.Add(new TextBlock
            {
                Text = game.Title,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.WordEllipsis,
                Foreground = White,
                Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["BodyStrongTextBlockStyle"],
            });

            // The cover is painted as a rounded fill (Smooth.Art): an Image cut by the tile's corners would show jagged edges.
            var cover = new Border { CornerRadius = new CornerRadius(10), IsHitTestVisible = false };
            // Two layers (2.1-rc.4): the picture, which grows under the pointer, and over it the tags and Play, which never
            // scale (they move out with its edges), so they stay sharp all through the motion.
            // No rounded clip on the layer itself (2.1): every part of it is rounded on its own (antialiased), and a second,
            // clipped edge over them cut their soft rim into steps. The light effects keep their own rounded clip (CoverFx).
            var pic = new Grid();
            // A transparent fill takes the pointer (2.1-rc.9): every layer of the picture is IsHitTestVisible=false, and without
            // a fill of its own the cover let clicks, hover and right-clicks through to the page (rc.8: the library looked frozen).
            _art = new Grid { Background = HitFill };
            _art.Children.Add(pic);
            // The placeholder's fill, under the picture: hidden once a cover that fills the tile has loaded (LoadCoverAsync).
            pic.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = PlaceholderBrush(), IsHitTestVisible = false, Tag = BackdropTag });
            pic.Children.Add(placeholder);
            pic.Children.Add(cover);
            // Light from above and a deeper foot (2.1): the cover reads as a solid object, not a flat picture.
            pic.Children.Add(new Border { Background = Helpers.CoverFx.Gloss, CornerRadius = new CornerRadius(10), IsHitTestVisible = false });
            pic.Children.Add(new Border { Background = Helpers.CoverFx.Floor, CornerRadius = new CornerRadius(10), IsHitTestVisible = false });

            var topShade = new Border { Height = 44, VerticalAlignment = VerticalAlignment.Top, Background = Shade(true), Visibility = Visibility.Collapsed, IsHitTestVisible = false, CornerRadius = new CornerRadius(10, 10, 0, 0) };
            var bottomShade = new Border { Height = 84, VerticalAlignment = VerticalAlignment.Bottom, Background = Shade(false), Visibility = Visibility.Collapsed, IsHitTestVisible = false, CornerRadius = new CornerRadius(0, 0, 10, 10) };
            pic.Children.Add(topShade);
            pic.Children.Add(bottomShade);

            // Top row as a grid: time played on the left, OptiScaler and the pin on the right; the left one gives way, never overlaps.
            var top = new Grid { Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, ColumnSpacing = 3 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var topLeft = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var topRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            top.Children.Add(topLeft);
            Grid.SetColumn(topRight, 1);
            top.Children.Add(topRight);
            _art.Children.Add(top);

            AppState.Settings.Games.TryGetValue(game.Id, out var settings);
            bool overlays = AppState.Settings.ShowCoverOverlays;
            topRight.Children.Add(_mods);
            if (AppState.Settings.CoverTag("pin") && AppState.IsPinned(game.Id))
            {
                topRight.Children.Add(Chip("\uEB52", "", new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x5A, 0x6E)), out _));
                topShade.Visibility = Visibility.Visible;
            }

            var bottom = new StackPanel { Spacing = 4, Margin = new Thickness(6, 0, 6, 6), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
            _tags = new WrapPanel(3, []);
            var ach = new StackPanel { Spacing = 5, Visibility = Visibility.Collapsed };
            bottom.Children.Add(_tags);
            bottom.Children.Add(ach);
            _art.Children.Add(bottom);

            _name = new TextBlock
            {
                Text = game.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(2, 8, 2, 0),
                Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["BodyStrongTextBlockStyle"],
            };
            var store = new TextBlock { Text = StoreLogos.GroupOf(game).Label, Margin = new Thickness(2, 0, 2, 2), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], TextTrimming = TextTrimming.CharacterEllipsis };

            // 2.1: the cover stands on a soft contact shadow; under the pointer it springs up, a band of light crosses it, a
            // highlight follows the pointer and the theme's accent glows under it (Helpers.CoverFx, all on the compositor).
            var fxHost = new Border();
            var glowHost = new Border();
            _contact = new Border { Height = 26, VerticalAlignment = VerticalAlignment.Bottom, Background = Helpers.CoverFx.ContactShadow, IsHitTestVisible = false };
            var stage = new Grid();
            stage.Children.Add(_contact);
            stage.Children.Add(glowHost);
            stage.Children.Add(_art);
            pic.Children.Add(fxHost);
            _fx = new Helpers.CoverFx(_art, pic, fxHost, glowHost, (topLeft, -1, -1), (topRight, 1, -1), (bottom, -1, 1));
            Root = new StackPanel { Tag = game.Id };
            Root.Children.Add(stage);
            Root.Children.Add(_name);
            Root.Children.Add(store);
            Tiles.AddOrUpdate(Root, this);
            // Under the pointer the cover pops: it grows a little on a spring and stands over its neighbours (1.4.0-rc.20,
            // in place of the grey veil). Play sits on it, small and round; it turns into Stop while the game runs, and then
            // stays on the cover without the pointer too.
            _art.PointerEntered += (_, _) => Pop(true);
            _art.PointerExited += (_, _) => Pop(false);
            _art.PointerCanceled += (_, _) => Pop(false);
            _art.PointerCaptureLost += (_, _) => Pop(false);
            if (!game.NotInstalled && !game.Installing)
            {
                // Play in the middle of the cover (2.1-rc.3: a pill with its word, following the game's states, see LaunchChip), on
                // a soft dark halo so it reads on any art. It comes up from a little below and grows into place when the pointer
                // arrives; it stays while the game runs or downloads an update.
                _launchHeight = Math.Clamp(Math.Round(width * 0.19), 36, 46);
                _launchMadeFor = width;
                var halo = new Microsoft.UI.Xaml.Shapes.Ellipse { IsHitTestVisible = false, Fill = HaloBrush, Margin = new Thickness(-_launchHeight * 1.1, -_launchHeight * 0.9, -_launchHeight * 1.1, -_launchHeight * 0.9) };
                _launchChip = new Controls.LaunchChip(game, withLabel: true, size: _launchHeight) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                _launch = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0, IsHitTestVisible = false,
                    OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
                    TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(200) },
                };
                _launch.SizeChanged += (_, e) => _launch.CenterPoint = new System.Numerics.Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
                _launch.Children.Add(halo);
                _launch.Children.Add(_launchChip);
                _art.Children.Add(_launch);
                _art.Loaded += (_, _) =>
                {
                    GameSessions.Changed -= OnSession; GameSessions.Changed += OnSession;
                    Downloads.Changed -= OnDownload; Downloads.Changed += OnDownload;
                    ShowLaunch();
                };
                _art.Unloaded += (_, _) => { GameSessions.Changed -= OnSession; Downloads.Changed -= OnDownload; };
            }
            Resize(width);

            _ = LoadIconAsync(icon, game);
            _ = LoadCoverAsync(cover, placeholder, game, ct);
            // Settings → Library → "Tags on the covers" off: the bare art, and none of the reads behind the tags.
            if (!overlays) return;
            var s = AppState.Settings;
            if (s.CoverTag("opti") || s.CoverTag("rs") || s.CoverTag("rdx") || s.CoverTag("uplus") || s.CoverTag("dxvk")) _ = LoadModsAsync(topShade, settings, ct);
            if (s.CoverTag("tech")) _ = LoadTagsAsync(bottomShade, ct);
            if (s.CoverTag("time")) _ = LoadPlayTimeAsync(topLeft, topShade, settings, ct);
            if (s.CoverTag("ach")) _ = LoadAchievementsAsync(ach, bottomShade, game, ct);
        }

        double _launchHeight, _launchMadeFor;
        readonly Helpers.CoverFx _fx;
        readonly TextBlock _name;
        readonly Border _contact;

        /// <summary>The tile behind a cover's root element (keyboard and controller focus land on the grid's item).</summary>
        internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StackPanel, CoverTile> Tiles = new();

        bool _focusPop;

        /// <summary>Focus from the keyboard or a controller pops the cover like the pointer does (and only that focus lets it down).</summary>
        internal void Focus(bool on)
        {
            if (on) { _focusPop = true; Pop(true); }
            else if (_focusPop) { _focusPop = false; Pop(false); }
        }
        Grid? _launch;
        Controls.LaunchChip? _launchChip;
        bool _hover;

        static RadialGradientBrush? _halo;
        static RadialGradientBrush HaloBrush
        {
            get
            {
                if (_halo is not null) return _halo;
                _halo = new RadialGradientBrush();
                _halo.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x8C, 0, 0, 0), Offset = 0 });
                _halo.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x55, 0, 0, 0), Offset = 0.45 });
                _halo.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0, 0, 0), Offset = 1 });
                return _halo;
            }
        }

        void OnSession(string id)
        {
            if (Root.Tag is string mine && mine.Equals(id, StringComparison.OrdinalIgnoreCase)) ShowLaunch();
        }

        void OnDownload()
        {
            if (Root.Tag is string id && (_busy || Downloads.Active(id) is not null)) ShowLaunch();
        }

        bool _busy;

        void ShowLaunch()
        {
            if (_launch is null || Root.Tag is not string id) return;
            _busy = GameSessions.IsActive(id) || Downloads.Active(id) is { State: DownloadState.Downloading or DownloadState.Waiting or DownloadState.Paused };
            bool on = _hover || _busy;
            _launch.Opacity = on ? 1 : 0;
            _launch.IsHitTestVisible = on;
            // Made for the cover's width when it was built: a zoom scales it with the cover; away, it waits smaller and lower.
            // Never scaled (2.1-rc.4: it grew from 60% and was blurred until it settled): it fades in rising a few pixels,
            // and a zoom makes it again at the cover's size (Resize).
            _launch.Translation = new System.Numerics.Vector3(0, on ? 0 : 10, 0);
        }

        /// <summary>
        /// Back to rest: the page goes away (a click on a cover opens its game with the pointer still on it, and no
        /// PointerExited comes), so the cover does not come back popped with its glow spilling over its neighbours.
        /// </summary>
        internal void Rest()
        {
            _focusPop = false;
            if (_hover) Pop(false);
        }

        /// <summary>Self-test: the cover as under the pointer (popped, Play shown), or back.</summary>
        internal void SelfTestHover(bool on) => Pop(on);

        /// <summary>
        /// The cover grows by the same few pixels on each side whatever its size, over its neighbours and past its slot (the
        /// item's template and the grid's padding leave it the room, see TileItem and NewGrid). Set on the element itself
        /// with a transition, not as a composition animation: the cover is then really that size for everything that reads
        /// it, pictures of the window included.
        /// </summary>
        void Pop(bool on)
        {
            _hover = on;
            ShowLaunch();
            try
            {
                // Over the next covers while it is grown (the item's container is the one the grid orders).
                DependencyObject? up = _art;
                while (up is not null && up is not GridViewItem) up = VisualTreeHelper.GetParent(up);
                if (up is UIElement item) Canvas.SetZIndex(item, on ? 10 : 0);
                _fx.Hover(on);
                if (on) _name.Foreground = (Brush)HybridLauncher.Helpers.ThemeRes.Current["AccentTextFillColorPrimaryBrush"];
                else _name.ClearValue(TextBlock.ForegroundProperty);
            }
            catch (Exception ex) { Log.Warn($"Cover pop: {ex.Message}"); }
        }

        public void Resize(double width)
        {
            _width = width;
            Root.Width = width;
            _art.Width = width;
            _art.Height = Math.Round(width * 1.5);
            _fx.Resize(_art.Width, _art.Height);
            // Play at the cover's size: made again when a zoom changed it (it is never scaled, so it stays sharp).
            if (_launchChip is not null && _launch is not null && Math.Clamp(Math.Round(width * 0.19), 36, 46) is var h && h != _launchHeight)
            {
                _launchHeight = h;
                _launchMadeFor = width;
                _launch.Children.Remove(_launchChip);
                _launchChip = new Controls.LaunchChip(_game, withLabel: true, size: h) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                _launch.Children.Add(_launchChip);
            }
            _contact.Margin = new Thickness(Math.Round(width * 0.08), 0, Math.Round(width * 0.08), -14);
            ShowLaunch();
            Shorten();
        }

        /// <summary>"12 hours" on big covers, "12 h" on small ones; the tech tags fit the cover's width. Never overlapping.</summary>
        void Shorten()
        {
            if (_timeText is not null) _timeText.Text = _width >= 230 ? Playtime.FormatTime(_minutes) : Library.ShortTime(_minutes);
            // Tech tags: every one shows. One per tag when they fit on a row; else each maker's tags in one chip
            // ("DLSS 4 · MFG"); and when even those do not fit, the chips wrap onto more rows.
            _tags.Children.Clear();
            if (_tagNames.Count == 0 || !AppState.Settings.CoverTag("tech")) return;
            double room = _width - 16;
            double font = _width >= 240 ? 10 : 9;
            static double Width(IEnumerable<FrameworkElement> tags) => tags.Sum(e =>
            {
                e.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                return e.DesiredSize.Width + 3;
            }) - 3;
            var single = _tagNames.Select(t => (FrameworkElement)TechTags.Create(t, fontSize: font, padX: 5)).ToList();
            var chips = Width(single) <= room ? single : Grouped(font);
            foreach (var c in chips) _tags.Children.Add(c);
        }

        /// <summary>One chip per maker: its upscaler and its frame generation together ("DLSS 4 · MFG", "FSR 4 · FG").</summary>
        List<FrameworkElement> Grouped(double font)
        {
            var list = new List<FrameworkElement>();
            foreach (var g in _tagNames.GroupBy(t => t.Split(' ')[0], StringComparer.OrdinalIgnoreCase))
            {
                var parts = g.ToList();
                var upscaler = parts.FirstOrDefault(p => !TechTags.IsFrameGen(p));
                var fg = parts.Where(TechTags.IsFrameGen).Select(p => p[(p.IndexOf(' ') + 1)..]).ToList();
                var text = upscaler is null ? string.Join(" · ", parts) : fg.Count == 0 ? upscaler : $"{upscaler} · {string.Join(" · ", fg)}";
                var chip = TechTags.Create(text, frameGen: fg.Count > 0 && upscaler is null, fontSize: font, padX: 5);
                ToolTipService.SetToolTip(chip, string.Join(", ", parts));
                list.Add(chip);
            }
            return list;
        }

        /// <summary>
        /// A dark see-through chip on the cover: a glyph and a short text. A plain fill, not glass: a library holds hundreds of
        /// chips and each live blur is redrawn on every frame of a scroll, which made the grid stutter.
        /// </summary>
        static Border Chip(string glyph, string text, Brush? glyphBrush, out TextBlock? label)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 10, Foreground = glyphBrush ?? White, VerticalAlignment = VerticalAlignment.Center });
            label = null;
            if (text.Length > 0)
            {
                label = new TextBlock { Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                panel.Children.Add(label);
            }
            return new Border
            {
                Background = ChipFill,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(ChipCorner),
                Padding = new Thickness(5, 2, 5, 3),
                Child = panel,
            };
        }

        /// <summary>
        /// The time played, laid out as Hydra's library draws it (2.1-rc.3): 24 px tall, GitHub's clock icon at 11 px and the
        /// hours in 12 px. Since 2.1 on the same dark chip as every other tag of the cover (no glass, no hairline): the glass
        /// went pale over bright art and its clipped edge looked rough.
        /// </summary>
        static FrameworkElement TimeChip(out TextBlock text)
        {
            var ink = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), ClockPath),
                Width = 11, Height = 11, Stretch = Stretch.Uniform, Fill = ink, VerticalAlignment = VerticalAlignment.Center,
            });
            text = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(text);
            var tag = new Border
            {
                Height = 24, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(ChipCorner), Child = row,
                Background = ChipFill,
            };
            // No shadow box under it (2.1-rc.4): offset from the glass, its edge read as a glitch.
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            host.Children.Add(tag);
            return host;
        }


        // GitHub's Octicons "clock-16", as Hydra uses it.
        const string ClockPath = "M8 0a8 8 0 1 1 0 16A8 8 0 0 1 8 0ZM1.5 8a6.5 6.5 0 1 0 13 0 6.5 6.5 0 0 0-13 0Zm7-3.25v2.992l2.028.812a.75.75 0 0 1-.557 1.392l-2.5-1A.751.751 0 0 1 7 8.25v-3.5a.75.75 0 0 1 1.5 0Z";

        // Marks the placeholder's fill under the picture (see LoadCoverAsync).
        static readonly object BackdropTag = new();

        /// <summary>Self-test: the placeholder's fill of the first cover, hidden once its picture loaded.</summary>
        internal static Visibility? BackdropOf(FrameworkElement tileRoot) =>
            Tiles.TryGetValue(tileRoot as StackPanel ?? new StackPanel(), out var t) ? Find(t._art)?.Visibility : null;

        static Border? Find(Panel art) => art.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<Border>()).FirstOrDefault(b => ReferenceEquals(b.Tag, BackdropTag));

        static SolidColorBrush? _hitFill;
        static SolidColorBrush HitFill => _hitFill ??= new(Microsoft.UI.Colors.Transparent);

        static SolidColorBrush? _chipFill;
        // Every tag of a cover on one chip (2.1): the same near-black fill, dark enough to read over bright art, and the same corners.
        static SolidColorBrush ChipFill => _chipFill ??= new(Windows.UI.Color.FromArgb(0xE0, 0x0E, 0x0E, 0x12));
        const double ChipCorner = 5;

        static LinearGradientBrush Shade(bool top)
        {
            var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(top ? (byte)0x8C : (byte)0, 0, 0, 0), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(top ? (byte)0 : (byte)0xD8, 0, 0, 0), Offset = 1 });
            return brush;
        }

        static Brush PlaceholderBrush()
        {
            var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x1F, 0x3D), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0x10, 0x0E, 0x16), Offset = 1 });
            return brush;
        }

        static async Task LoadIconAsync(Image target, Game game)
        {
            try
            {
                foreach (var source in new[] { ArtOverrides.Get(game.Id, ArtKind.Icon), game.IconPath, game.MainExe, game.StoreExe }.Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (await IconCache.GetAsync(source!) is { } icon)
                    {
                        target.Source = icon;
                        return;
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"Icon for {game.Name}: {ex.Message}"); }
        }

        static async Task LoadCoverAsync(Border target, FrameworkElement placeholder, Game game, CancellationToken ct)
        {
            try
            {
                if (!Covers.TryGetValue(game.Id, out var source))
                {
                    // The user's own cover first (Game settings → Library → Cover): it fills the tile like a portrait capsule.
                    var own = AppState.Settings.Games.TryGetValue(game.Id, out var gs) && gs.CustomCover is { } c && File.Exists(c) ? c : null;
                    var path = own ?? await GameInfo.GetCoverAsync(game, ct);
                    if (path is null || ct.IsCancellationRequested) return;
                    // Games without a portrait capsule get their landscape header: shown whole, centred on the tile.
                    bool portrait = own is not null || path.EndsWith("-cover.jpg", StringComparison.OrdinalIgnoreCase);
                    // Decoded once for the biggest zoom (480 px, and 1.5x that on high-DPI screens; Steam's capsule is 600 px wide anyway).
                    source = new BitmapImage { DecodePixelWidth = (int)(MaxZoom * (portrait ? 1.5 : 2)), UriSource = new Uri(path) };
                    Covers[game.Id] = source;
                    if (!portrait) Landscape.Add(game.Id);
                }
                bool fills = !Landscape.Contains(game.Id);
                var brush = new ImageBrush { Stretch = fills ? Stretch.UniformToFill : Stretch.Uniform };
                // A cover that fills the tile takes the placeholder's fill away (2.0.0): the dark fill under it showed as a dark
                // rim on the rounded corners. A landscape header keeps it around the picture.
                // Since 2.1-rc.4 that fill is a layer of its own under the picture, not the tile's background (2.1-rc.10 audit:
                // clearing the background left the layer, a dark rim around every loaded cover).
                var backdrop = (target.Parent as Panel)?.Children.OfType<Border>().FirstOrDefault(b => ReferenceEquals(b.Tag, BackdropTag));
                if (fills && backdrop is not null)
                {
                    // A picture decoded before (kept in Covers) may not raise ImageOpened again.
                    if (source is BitmapImage { PixelWidth: > 0 }) backdrop.Visibility = Visibility.Collapsed;
                    else brush.ImageOpened += (_, _) => backdrop.Visibility = Visibility.Collapsed;
                }
                // An unreadable file brings the placeholder back (and is tried again next time).
                brush.ImageFailed += (_, _) =>
                {
                    placeholder.Visibility = Visibility.Visible;
                    if (backdrop is not null) backdrop.Visibility = Visibility.Visible;
                    Covers.Remove(game.Id);
                };
                target.Opacity = 0;
                brush.ImageSource = source;
                target.Background = brush;
                placeholder.Visibility = Visibility.Collapsed;
                var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(250) };
                var sb = new Storyboard();
                Storyboard.SetTarget(fade, target);
                Storyboard.SetTargetProperty(fade, "Opacity");
                sb.Children.Add(fade);
                sb.Begin();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Cover for {game.Name}: {ex.Message}"); }
        }

        /// <summary>
        /// The mods in the game's folder now, read off the UI thread (a library holds hundreds of covers): OptiScaler ("Opti"),
        /// ReShade ("RS"), RenoDX ("RDX") and Ultra+ ("U+"), each a small chip with a dot in its own colour and the version in
        /// its tooltip. Found by the same checks as the game page's tabs, so a mod put there by hand or by another tool shows too.
        /// </summary>
        async Task LoadModsAsync(UIElement shade, GameSettings? settings, CancellationToken ct)
        {
            try
            {
                var game = _game;
                var found = await Task.Run(() => FindMods(game, settings), ct);
                if (ct.IsCancellationRequested || found.Count == 0) return;
                found.RemoveAll(m => !AppState.Settings.CoverTag(m.Key));
                if (found.Count == 0) return;
                foreach (var m in found)
                {
                    var chip = ModChip(m);
                    ToolTipService.SetToolTip(chip, m.Detail);
                    _mods.Children.Add(chip);
                }
                shade.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Mod tags for {_game.Name}: {ex.Message}"); }
        }

        static List<ModTag> FindMods(Game game, GameSettings? settings) => InstalledMods.Find(game, settings);

        /// <summary>A mod's tag: a dot in its colour and the short name, on the covers' dark chip.</summary>
        static Border ModChip(ModTag m)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(m.Color), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = m.Short, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White, VerticalAlignment = VerticalAlignment.Center });
            return new Border { Background = ChipFill, CornerRadius = new CornerRadius(ChipCorner), Padding = new Thickness(5, 2, 6, 3), Child = panel };
        }

        async Task LoadTagsAsync(UIElement shade, CancellationToken ct)
        {
            try
            {
                var tags = Library.StatsOf(_game.Id)?.Tags;
                if (tags is null)
                {
                    var appId = await GameInfo.SteamAppIdAsync(_game, ct);
                    tags = await Pcgw.GetUpscalerTagsAsync(_game, appId, ct);
                    if (ct.IsCancellationRequested) return;
                    Library.SetTags(_game.Id, tags);
                }
                _tagNames.Clear();
                _tagNames.AddRange(tags);
                Shorten();
                if (tags.Count > 0) shade.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Upscaler tags for {_game.Name}: {ex.Message}"); }
        }

        /// <summary>Hours played (Steam's own count for Steam games, Hybrid Launcher's for the others), top left; nothing before the first session.</summary>
        async Task LoadPlayTimeAsync(Panel target, UIElement shade, GameSettings? settings, CancellationToken ct)
        {
            try
            {
                var s = settings ?? new GameSettings();
                var game = _game;
                var stats = await Task.Run(() => Playtime.Get(game, s), ct);
                if (ct.IsCancellationRequested || stats.Minutes <= 0) return;
                _minutes = stats.Minutes;
                var chip = TimeChip(out _timeText);
                ToolTipService.SetToolTip(chip, L.F("Played {0:0.#} hours", stats.Minutes / 60.0) + (stats.LastPlayed is { } t ? ", " + L.F("last on {0:d MMM yyyy}", t) : ""));
                // Glass needs no dark band behind it to read (as on Hydra's covers).
                target.Children.Add(chip);
                Shorten();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Play time for {_game.Name}: {ex.Message}"); }
        }

        /// <summary>Steam achievements unlocked / total with a thin bar, along the bottom of the cover (read from Steam's files, no network).</summary>
        static async Task LoadAchievementsAsync(StackPanel target, UIElement shade, Game game, CancellationToken ct)
        {
            if (game is not { Store: GameStore.Steam, SteamAppId: not null } and not { EmulatorAppId: not null } && !HybridLauncher.Core.Achievements.XboxAchievements.Applies(game)) return;
            try
            {
                if (!AchCounts.TryGetValue(game.Id, out var counts))
                {
                    var set = await Task.Run(() => SteamAchievements.ReadFor(game), ct);
                    if (ct.IsCancellationRequested) return;
                    counts = set is { Total: > 0 } ? (set.UnlockedCount, set.Total) : null;
                    AchCounts[game.Id] = counts;
                }
                if (counts is not { } c) return;

                double share = (double)c.Unlocked / c.Total;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                left.Children.Add(Icons.Trophy(14, platinum: c.Unlocked == c.Total));
                left.Children.Add(new TextBlock { Text = $"{c.Unlocked} / {c.Total}", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White });
                row.Children.Add(left);
                var pct = new TextBlock { Text = $"{share:P0}", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White, Opacity = 0.85 };
                Grid.SetColumn(pct, 1);
                row.Children.Add(pct);
                target.Children.Add(row);

                // A plain two-part bar: cheaper than a ProgressBar per tile, and it stays thin.
                var bar = new Grid { Height = 3, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.0001), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star) });
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["AccentFillColorDefaultBrush"] });
                target.Children.Add(bar);
                target.Visibility = Visibility.Visible;
                shade.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Achievements for {game.Name}: {ex.Message}"); }
        }
    }

    // Achievement counts per game for the session: the grid is rebuilt on every search keystroke and pin.
    static readonly Dictionary<string, (int Unlocked, int Total)?> AchCounts = new(StringComparer.OrdinalIgnoreCase);

    // A new unlock (raised on the UI thread) is counted again the next time the grid is drawn.
    static LibraryPage() => AchievementWatcher.Changed += _ => AchCounts.Clear();
}
