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
    string _filter = "";

    /// <summary>The pane's search text the covers were last built for.</summary>
    public string Filter => _filter;
    int _widestRow = 1;
    double _tileWidth = 204;
    readonly List<CoverTile> _tiles = [];
    readonly List<GridView> _grids = [];
    bool _ready, _syncing;
    ulong _lastWheel;
    DispatcherQueueTimer? _saveTimer, _ownedTimer, _dragTimer;
    bool _dragging;
    double _pendingZoom = -1;

    public LibraryPage()
    {
        InitializeComponent();
        Loc.Apply(this);
        Scroller.SizeChanged += (_, e) => Center(e.NewSize.Width);
        // The scroller would scroll on the wheel itself: the Ctrl case is taken before it (on the content) and, for the margins around it, on the scroller.
        var wheel = new PointerEventHandler(OnWheel);
        Root.AddHandler(UIElement.PointerWheelChangedEvent, wheel, true);
        Scroller.AddHandler(UIElement.PointerWheelChangedEvent, wheel, true);
        // Dragging the slider: see ZoomDrag.
        ZoomSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => ZoomDrag(true)), true);
        ZoomSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => ZoomDrag(false)), true);
        ZoomSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => ZoomDrag(false)), true);
        _ready = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // The page stays in memory: coming back with nothing changed (same search, no settings saved, no new scan) keeps the
        // covers already made, and the scroll position, instead of building them all again.
        var filter = e.Parameter as string ?? "";
        if (_shown != (filter, SettingsStore.Version, AppState.GamesVersion, OwnedLibrary.IsLoaded)) Reload(filter);
        _ = Library.LoadStatsAsync(DispatcherQueue);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // The covers still loading go on: the page is kept in memory and shows them when it comes back (Reload cancels them).
        // A zoom still waiting for its save is written now: the page may not come back.
        if (_saveTimer is { IsRunning: true })
        {
            _saveTimer.Stop();
            SettingsStore.Save();
        }
        _ownedTimer?.Stop();
    }

    /// <summary>
    /// Automatic: as many columns as fit with tiles at least <see cref="MinTile"/> wide; with fewer games than that, fewer and
    /// bigger tiles (up to <see cref="MaxTile"/>). Zoomed (<c>TileSize</c> above 0): the column count nearest to that cover width,
    /// and the tiles share the row exactly. The block is centred and the titles line up with the first column.
    /// </summary>
    void Center(double width)
    {
        if (width <= 0) return;
        double avail = Math.Max(MinTile, width - 48);
        double zoom = AppState.Settings.Library.TileSize;
        int cols = zoom > 0 ? ColsFor(avail, zoom) : Math.Max(1, (int)((avail + Gap) / (MinTile + Gap)));
        // Automatic, with fewer games than columns: one column per game, the covers as big as they may be. Zoomed, the grid keeps the
        // full width whatever the count (rows just end early): a narrower, re-centred block made the header jump while zooming.
        bool fewGames = zoom <= 0 && _widestRow < cols;
        if (fewGames) cols = Math.Max(1, _widestRow);
        double tile = Math.Floor((avail - Gap * (cols - 1)) / cols);
        if (zoom <= 0) tile = Math.Min(MaxTile, tile);
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
            // The not-installed covers are built at a fixed width: rebuilt once the zoom settles.
            if (_ownedWrap is not null && Math.Abs(_ownedWidth - tile) >= 0.5) ScheduleOwnedRefill();
        }
        SyncZoom();
    }

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

    /// <summary>The not-installed covers are plain elements built at one width: after a zoom they are drawn again, once, when it pauses.</summary>
    void ScheduleOwnedRefill()
    {
        if (_ownedTimer is null)
        {
            _ownedTimer = DispatcherQueue.CreateTimer();
            _ownedTimer.Interval = TimeSpan.FromMilliseconds(180);
            _ownedTimer.IsRepeating = false;
            _ownedTimer.Tick += (_, _) => FillOwned();
        }
        _ownedTimer.Stop();
        _ownedTimer.Start();
    }

    void FillOwned()
    {
        if (_ownedWrap is null || _owned is null || _tileWidth <= 0) return;
        _ownedWidth = _tileWidth;
        _ownedWrap.Children.Clear();
        foreach (var g in _owned) _ownedWrap.Children.Add(OwnedLibrary.Tile(g, _tileWidth));
    }

    /// <summary>The grids' panels keep the first item's size: give them the new slot size so the tiles reflow.</summary>
    void SizeSlots()
    {
        if (_tiles.Count == 0 || _tileWidth <= 0) return;
        var first = _tiles[0].Root;
        first.Measure(new Windows.Foundation.Size(_tileWidth, double.PositiveInfinity));
        double height = first.DesiredSize.Height;
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

    public void Reload(string filter)
    {
        _filter = filter;
        _shown = (filter, SettingsStore.Version, AppState.GamesVersion, OwnedLibrary.IsLoaded);
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _tiles.Clear();
        _grids.Clear();
        _ownedWrap = null;
        _owned = null;
        var f = AppState.Settings.Library;

        bool Matches(Game g) => _filter.Length == 0 || g.Title.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) || g.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase);
        var visible = Library.Apply(AppState.VisibleGames.Where(Matches));
        var pinned = AppState.PinnedGames().Where(g => visible.Contains(g)).ToList();
        var rest = visible.Where(g => !AppState.IsPinned(g.Id)).ToList();

        PinnedGrid.Items.Clear();
        foreach (var game in pinned) PinnedGrid.Items.Add(Tile(game, ct));
        PinnedSection.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Groups.Children.Clear();
        _folds.Clear();
        if (f.GroupByLauncher)
        {
            foreach (var (key, games) in Library.Groups(rest)) Groups.Children.Add(Section(key, games, ct));
            _widestRow = Math.Max(pinned.Count, Library.Groups(rest).Select(g => g.Games.Count).DefaultIfEmpty(0).Max());
        }
        else
        {
            var grid = NewGrid();
            foreach (var game in rest) grid.Items.Add(Tile(game, ct));
            Groups.Children.Add(grid);
            _widestRow = Math.Max(pinned.Count, rest.Count);
        }
        _tileWidth = -1;
        Center(Scroller.ActualWidth);
        // The grids create their panels on first layout.
        DispatcherQueue.TryEnqueue(SizeSlots);

        // Games you own on a linked launcher but have not installed (Filters → Not installed), with Install on their cover.
        // A game not on this PC has no OptiScaler: "Only games with OptiScaler installed" leaves them out.
        if (f.ShowNotInstalled && !f.OptiOnly)
        {
            if (!OwnedLibrary.IsLoaded) _ = LoadOwnedAsync();
            var owned = OwnedLibrary.NotInstalled().Where(g => _filter.Length == 0 || g.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (owned.Count > 0)
            {
                var section = new StackPanel { Spacing = 8 };
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 6, 0, 0) };
                head.Children.Add(new FontIcon { Glyph = "\uE896", FontSize = 13, Foreground = (Brush)HybridLauncher.Helpers.ThemeRes.Current["AccentTextFillColorPrimaryBrush"] });
                head.Children.Add(new TextBlock { Text = L.T("NOT INSTALLED"), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["GroupHeader"], VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new TextBlock { Text = owned.Count.ToString(), FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
                section.Children.Add(head);
                var wrap = new WrapPanel(Gap, []);
                foreach (var g in owned.Take(300)) wrap.Children.Add(OwnedLibrary.Tile(g, _tileWidth > 0 ? _tileWidth : 204));
                section.Children.Add(wrap);
                Groups.Children.Add(section);
                _ownedWrap = wrap;
                _owned = owned.Take(300).ToList();
                _ownedWidth = _tileWidth > 0 ? _tileWidth : 204;
                // The block is as wide as its widest row: the not-installed covers count too.
                if (owned.Count > _widestRow)
                {
                    _widestRow = owned.Count;
                    Center(Scroller.ActualWidth);
                }
            }
        }

        int total = pinned.Count + rest.Count;
        int all = AppState.VisibleGames.Count();
        CountText.Text = (_filter.Length > 0 ? L.F(total == 1 ? "{0} game matching \"{1}\"" : "{0} games matching \"{1}\"", total, _filter)
                : f.IsFiltering ? L.F("{0} of {1} games (filtered)", total, all) : L.N(total, "{0} game", "{0} games"))
            + "  ·  " + L.F("sorted by {0}", SortName(f.Sort).ToLower()) + (total > 0 ? "  ·  " + L.T("right-click a cover for more") : "");
        int active = Library.ActiveFilters();
        FilterBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterBadgeText.Text = active.ToString();
        EmptyText.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _filter.Length > 0 ? L.T("No game matches the search.")
            : f.IsFiltering ? L.T("No game passes the filters. Open Filters and reset them.")
            : L.T("No games found. Use “Add a game or games folder” at the bottom of the pane.");
    }

    WrapPanel? _ownedWrap;
    List<OwnedGame>? _owned;
    double _ownedWidth;

    async Task LoadOwnedAsync()
    {
        await OwnedLibrary.LoadAsync();
        if (AppState.Settings.Library.ShowNotInstalled) Reload(_filter);
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
        Motion.Turn(f.Chevron, !fold);
        if (fold) Motion.Close(f.Grid);
        else
        {
            Motion.Open(f.Grid);
            DispatcherQueue.TryEnqueue(SizeSlots);
        }
    }

    StackPanel Section(string key, List<Game> games, CancellationToken ct)
    {
        bool collapsed = Library.IsCollapsed(key);
        var chevron = Motion.Chevron(!collapsed, 11);
        var head = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 10, 6), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(chevron);
        var label = StoreLogos.GroupHeader(games[0], games.Count, (Style)HybridLauncher.Helpers.ThemeRes.Current["GroupHeader"]);
        Grid.SetColumn(label, 1);
        head.Children.Add(label);
        ToolTipService.SetToolTip(head, L.T("Click to fold or open. Drag to move this group."));

        var grid = NewGrid();
        foreach (var game in games) grid.Items.Add(Tile(game, ct));
        grid.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        var section = new StackPanel { Spacing = 6, Tag = key, RenderTransform = new TranslateTransform() };
        section.Children.Add(head);
        section.Children.Add(grid);

        head.PointerEntered += (_, _) => head.Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["SubtleFillColorSecondaryBrush"];
        head.PointerExited += (_, _) => head.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _folds[key] = (chevron, grid, !collapsed);
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
            Padding = new Thickness(0),
            // The slots' right gap sits past the block's edge, so the covers line up with the titles on both sides.
            Margin = new Thickness(0, 0, -Gap, 0),
            ItemContainerStyle = (Style)Resources["TileItem"],
        };
        ScrollViewer.SetVerticalScrollMode(grid, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Hidden);
        grid.ItemClick += Tile_Click;
        grid.Loaded += (_, _) => SizeSlots();
        _grids.Add(grid);
        return grid;
    }

    void Tile_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement { Tag: string id }) App.Window?.OpenGame(id);
    }

    // ---------- sort and filters ----------

    static readonly (string Key, string Name)[] Sorts =
        [("name", "Name"), ("recent", "Last played"), ("added", "Date added"), ("playtime", "Time played"), ("achievements", "Achievements unlocked"), ("tech", "Most tech tags")];

    static string SortName(string key) => L.T(Sorts.FirstOrDefault(s => s.Key == key).Name ?? "Name");

    void FiltersFlyout_Opening(object sender, object e) => FiltersFlyout.Content = FiltersPanel(FiltersFlyout.Hide);

    /// <summary>
    /// The Filters flyout, in three plain steps: how the games are ordered, which games show, and how the covers look. Every
    /// change applies at once, to the grid and to the game list in the pane alike; Reset puts the order and the filters back
    /// (the cover size and the look stay). The pane's filter button opens the same panel.
    /// </summary>
    internal static FrameworkElement FiltersPanel(Action hide)
    {
        var f = AppState.Settings.Library;
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        void Changed()
        {
            SettingsStore.Save();
            // The pane redraws the grid too, when the library is the page shown.
            App.Window?.RefreshList();
        }
        TextBlock Head(string text, string hint)
        {
            var t = new TextBlock { Style = (Style)res["GroupHeader"], Margin = new Thickness(0, 10, 0, 0), Text = text };
            ToolTipService.SetToolTip(t, hint);
            return t;
        }
        CheckBox Check(string text, bool on, Action<bool> set, string? tip = null)
        {
            var cb = new CheckBox { Content = text, IsChecked = on, MinWidth = 0 };
            if (tip is not null) ToolTipService.SetToolTip(cb, tip);
            cb.Click += (_, _) => { set(cb.IsChecked == true); Changed(); };
            return cb;
        }

        var panel = new StackPanel { Spacing = 4, Width = 300 };

        // 1. Order.
        panel.Children.Add(Head(L.T("ORDER"), L.T("How the games are sorted")));
        var sort = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (key, name) in Sorts) sort.Items.Add(new ComboBoxItem { Content = L.T(name), Tag = key });
        sort.SelectedIndex = Math.Max(0, Array.FindIndex(Sorts, s => s.Key == f.Sort));
        sort.SelectionChanged += (_, _) =>
        {
            if (sort.SelectedItem is ComboBoxItem { Tag: string key } && key != f.Sort) { f.Sort = key; Changed(); }
        };
        panel.Children.Add(sort);
        panel.Children.Add(Check(L.T("Reverse the order"), f.Descending, v => f.Descending = v));

        // 2. Which games.
        panel.Children.Add(Head(L.T("SHOW"), L.T("Which games are listed")));
        foreach (var (key, games) in Library.Groups(AppState.VisibleGames))
        {
            var g = StoreLogos.GroupOf(games[0]);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(StoreLogos.Tile(games[0].Store, g.Folder is not null, 20));
            content.Children.Add(new TextBlock { Text = $"{g.Label}  ({games.Count})", VerticalAlignment = VerticalAlignment.Center });
            var cb = new CheckBox { Content = content, IsChecked = f.Launchers.Count == 0 || f.Launchers.Contains(key), MinWidth = 0 };
            cb.Click += (_, _) =>
            {
                // No launcher ticked off = every launcher; ticking one off keeps the others.
                var all = Library.Groups(AppState.VisibleGames).Select(x => x.Key).ToList();
                if (f.Launchers.Count == 0) foreach (var k in all) f.Launchers.Add(k);
                if (cb.IsChecked == true) f.Launchers.Add(key); else f.Launchers.Remove(key);
                if (all.All(f.Launchers.Contains)) f.Launchers.Clear();
                Changed();
            };
            panel.Children.Add(cb);
        }
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
        panel.Children.Add(Check(L.T("Upscaler tags on the covers (DLSS, FSR, XeSS)"), AppState.Settings.ShowCoverTags, v => AppState.Settings.ShowCoverTags = v));

        var reset = new Button { Content = L.T("Reset order and filters"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTipService.SetToolTip(reset, L.T("The cover size and the look stay as they are."));
        reset.Click += (_, _) =>
        {
            // The cover size and the grouping are not filters: they stay.
            AppState.Settings.Library = new LibraryFilter { TileSize = f.TileSize, GroupByLauncher = f.GroupByLauncher };
            hide();
            Changed();
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
        readonly TextBlock? _optiText;
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
            _art = new Grid { CornerRadius = new CornerRadius(10), Background = PlaceholderBrush() };
            _art.Children.Add(placeholder);
            _art.Children.Add(cover);

            var topShade = new Border { Height = 64, VerticalAlignment = VerticalAlignment.Top, Background = Shade(true), Visibility = Visibility.Collapsed, IsHitTestVisible = false, CornerRadius = new CornerRadius(10, 10, 0, 0) };
            var bottomShade = new Border { Height = 110, VerticalAlignment = VerticalAlignment.Bottom, Background = Shade(false), Visibility = Visibility.Collapsed, IsHitTestVisible = false, CornerRadius = new CornerRadius(0, 0, 10, 10) };
            _art.Children.Add(topShade);
            _art.Children.Add(bottomShade);

            // Top row as a grid: time played on the left, OptiScaler and the pin on the right; the left one gives way, never overlaps.
            var top = new Grid { Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, ColumnSpacing = 4 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var topLeft = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var topRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            top.Children.Add(topLeft);
            Grid.SetColumn(topRight, 1);
            top.Children.Add(topRight);
            _art.Children.Add(top);

            AppState.Settings.Games.TryGetValue(game.Id, out var settings);
            if (Library.OptiVersion(game) is { } optiVersion)
            {
                var opti = Chip("", "OptiScaler", new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x5F, 0xD0, 0x7A)), out _optiText);
                ToolTipService.SetToolTip(opti, L.F("OptiScaler {0} is installed", optiVersion));
                topRight.Children.Add(opti);
                topShade.Visibility = Visibility.Visible;
            }
            if (AppState.IsPinned(game.Id))
            {
                topRight.Children.Add(Chip("", "", null, out _));
                topShade.Visibility = Visibility.Visible;
            }

            var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(8, 0, 8, 8), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
            _tags = new WrapPanel(3, []);
            var ach = new StackPanel { Spacing = 5, Visibility = Visibility.Collapsed };
            bottom.Children.Add(_tags);
            bottom.Children.Add(ach);
            _art.Children.Add(bottom);

            var name = new TextBlock
            {
                Text = game.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(2, 8, 2, 0),
                Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["BodyStrongTextBlockStyle"],
            };
            var store = new TextBlock { Text = StoreLogos.GroupOf(game).Label, Margin = new Thickness(2, 0, 2, 2), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], TextTrimming = TextTrimming.CharacterEllipsis };

            Root = new StackPanel { Tag = game.Id };
            Root.Children.Add(_art);
            Root.Children.Add(name);
            Root.Children.Add(store);
            ToolTipService.SetToolTip(Root, $"{game.Title}\n{game.StoreLabel} · {game.InstallDir}");
            Resize(width);

            _ = LoadIconAsync(icon, game);
            _ = LoadCoverAsync(cover, placeholder, game, ct);
            _ = LoadTagsAsync(bottomShade, ct);
            _ = LoadPlayTimeAsync(topLeft, topShade, settings, ct);
            _ = LoadAchievementsAsync(ach, bottomShade, game, ct);
        }

        public void Resize(double width)
        {
            _width = width;
            Root.Width = width;
            _art.Width = width;
            _art.Height = Math.Round(width * 1.5);
            Shorten();
        }

        /// <summary>Full words on big covers; "Opti" and "12 h" on small ones, then the icon alone. Never hidden, never overlapping.</summary>
        void Shorten()
        {
            if (_optiText is not null)
            {
                _optiText.Text = _width >= 230 ? "OptiScaler" : _width >= 196 ? "Opti" : "";
                _optiText.Visibility = _optiText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            if (_timeText is not null) _timeText.Text = _width >= 230 ? Playtime.FormatTime(_minutes) : Library.ShortTime(_minutes);
            // Tech tags: every one shows. One per tag when they fit on a row; else each maker's tags in one chip
            // ("DLSS 4 · MFG"); and when even those do not fit, the chips wrap onto more rows.
            _tags.Children.Clear();
            if (_tagNames.Count == 0 || !AppState.Settings.ShowCoverTags) return;
            double room = _width - 16;
            double font = _width >= 240 ? 11 : 10;
            static double Width(IEnumerable<FrameworkElement> tags) => tags.Sum(e =>
            {
                e.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                return e.DesiredSize.Width + 3;
            }) - 3;
            var single = _tagNames.Select(t => (FrameworkElement)TechTags.Create(t, fontSize: font)).ToList();
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
                var chip = TechTags.Create(text, frameGen: fg.Count > 0 && upscaler is null, fontSize: font);
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
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Foreground = glyphBrush ?? White, VerticalAlignment = VerticalAlignment.Center });
            label = null;
            if (text.Length > 0)
            {
                label = new TextBlock { Text = text, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                panel.Children.Add(label);
            }
            return new Border
            {
                Background = ChipFill,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 3, 7, 4),
                Child = panel,
            };
        }

        static readonly SolidColorBrush ChipFill = new(Windows.UI.Color.FromArgb(0xC4, 0x12, 0x12, 0x17));

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
                foreach (var source in new[] { game.IconPath, game.MainExe, game.StoreExe }.Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase))
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
                    var path = await GameInfo.GetCoverAsync(game, ct);
                    if (path is null || ct.IsCancellationRequested) return;
                    // Games without a portrait capsule get their landscape header: shown whole, centred on the tile.
                    bool portrait = path.EndsWith("-cover.jpg", StringComparison.OrdinalIgnoreCase);
                    // Decoded once for the biggest zoom (480 px, and 1.5x that on high-DPI screens; Steam's capsule is 600 px wide anyway).
                    source = new BitmapImage { DecodePixelWidth = (int)(MaxZoom * (portrait ? 1.5 : 2)), UriSource = new Uri(path) };
                    Covers[game.Id] = source;
                    if (!portrait) Landscape.Add(game.Id);
                }
                var brush = new ImageBrush { Stretch = Landscape.Contains(game.Id) ? Stretch.Uniform : Stretch.UniformToFill };
                // An unreadable file brings the placeholder back (and is tried again next time).
                brush.ImageFailed += (_, _) =>
                {
                    placeholder.Visibility = Visibility.Visible;
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
                var chip = Chip("", "", null, out _);
                _timeText = new TextBlock { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ((StackPanel)chip.Child).Children.Add(_timeText);
                ToolTipService.SetToolTip(chip, L.F("Played {0:0.#} hours", stats.Minutes / 60.0) + (stats.LastPlayed is { } t ? ", " + L.F("last on {0:d MMM yyyy}", t) : ""));
                target.Children.Add(chip);
                shade.Visibility = Visibility.Visible;
                Shorten();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Play time for {_game.Name}: {ex.Message}"); }
        }

        /// <summary>Steam achievements unlocked / total with a thin bar, along the bottom of the cover (read from Steam's files, no network).</summary>
        static async Task LoadAchievementsAsync(StackPanel target, UIElement shade, Game game, CancellationToken ct)
        {
            if (game is not { Store: GameStore.Steam, SteamAppId: not null } and not { EmulatorAppId: not null }) return;
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
                left.Children.Add(Icons.Trophy(17, platinum: c.Unlocked == c.Total));
                left.Children.Add(new TextBlock { Text = $"{c.Unlocked} / {c.Total}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White });
                row.Children.Add(left);
                var pct = new TextBlock { Text = $"{share:P0}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White, Opacity = 0.85 };
                Grid.SetColumn(pct, 1);
                row.Children.Add(pct);
                target.Children.Add(row);

                // A plain two-part bar: cheaper than a ProgressBar per tile, and it stays thin.
                var bar = new Grid { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) };
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
