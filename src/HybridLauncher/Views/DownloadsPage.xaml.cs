using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>The launcher libraries, loaded once per session and shared by the Downloads tab and the library's "not installed" filter.</summary>
public static class OwnedLibrary
{
    static readonly Dictionary<GameStore, OwnedList> Lists = [];
    static Task? _loading;

    public static event Action? Loaded;

    public static IReadOnlyDictionary<GameStore, OwnedList> All => Lists;

    public static bool IsLoaded => Lists.Count > 0;

    public static readonly GameStore[] Stores = [GameStore.Steam, GameStore.Epic, GameStore.Gog, GameStore.Xbox];

    public static bool Linked(GameStore store) => store switch
    {
        GameStore.Steam => AppState.Settings.Accounts.Steam,
        GameStore.Epic => AppState.Settings.Accounts.Epic,
        GameStore.Gog => AppState.Settings.Accounts.Gog,
        _ => AppState.Settings.Accounts.Xbox,
    };

    /// <summary>Loads every linked launcher's list (once; again with <paramref name="force"/>).</summary>
    public static Task LoadAsync(bool force = false)
    {
        if (!force && (_loading is { IsCompleted: false } || IsLoaded)) return _loading ?? Task.CompletedTask;
        _loading = LoadCore();
        return _loading;
    }

    static async Task LoadCore()
    {
        var accounts = AppState.Settings.Accounts;
        var tasks = Stores.Where(Linked).Select(s => OwnedGames.LoadAsync(s, accounts, CancellationToken.None)).ToList();
        var results = await Task.WhenAll(tasks);
        Lists.Clear();
        foreach (var r in results) Lists[r.Store] = r;
        Loaded?.Invoke();
    }

    /// <summary>Every owned game of the linked launchers (hidden ones left out).</summary>
    public static IEnumerable<OwnedGame> Owned() =>
        Lists.Values.Where(l => Linked(l.Store)).SelectMany(l => l.Games).Where(g => !AppState.Settings.HiddenGames.Contains(g.Id));

    public static OwnedGame? Find(string id) => Lists.Values.SelectMany(l => l.Games).FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Owned games of linked launchers that are not installed (nor hidden), name order.</summary>
    public static List<OwnedGame> NotInstalled()
    {
        var installed = InstalledIds();
        return Owned().Where(g => !installed.Contains(g.Id)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static HashSet<string> InstalledIds() =>
        AppState.Games.Where(g => !g.Installing).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The page-only game of an owned one: Download in place of Play, store art and overview, no folder.</summary>
    public static Game AsGame(OwnedGame g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Store = g.Store,
        InstallDir = "",
        SteamAppId = g.Store == GameStore.Steam && g.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) ? g.Id[6..] : null,
        NotInstalled = true,
    };

    /// <summary>Opens the game's page: the installed game when it is on this PC, its Download page otherwise.</summary>
    public static void Open(OwnedGame g)
    {
        if (AppState.Find(g.Id) is null) AppState.AddOwned(AsGame(g));
        App.Window?.OpenGame(g.Id);
    }

    /// <summary>
    /// Installs a game: Hybrid Launcher downloads it itself when its account is signed in (into the install folder of Downloads →
    /// Accounts), else the launcher's own install page opens and Hybrid Launcher follows it.
    /// </summary>
    public static async void Install(OwnedGame game, Action? started = null)
    {
        try
        {
            if (OwnedGames.Direct(game.Store))
            {
                // Steam's install window first: where it goes and the space it needs. A folder in Program Files asks Windows once
                // for the right to write; the progress then shows on the game's page and in Downloads (no message on top).
                if (App.Window?.Content?.XamlRoot is not { } xamlRoot) return;
                var theme = (App.Window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
                if (await InstallDialog.ShowAsync(game, xamlRoot, theme)) started?.Invoke();
                return;
            }
            Downloads.Start(game);
            App.Window?.ShowInfo(L.F("{0} opened the install of {1}. Confirm there; the progress shows in Downloads.", StoreLogos.Label(game.Store), game.Name));
            started?.Invoke();
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not open the launcher"), ex.Message); }
    }

    /// <summary>
    /// A cover for a game you own, in the library's style: the art, the launcher's tile in a corner, and on hover a dark veil
    /// with Install (Open for installed games). A download in progress shows its bar over the bottom of the cover.
    /// </summary>
    public static FrameworkElement Tile(OwnedGame game, double width, Action? started = null)
    {
        var installedGame = AppState.Find(game.Id) is { Installing: false } g ? g : null;
        var art = new Grid { Width = width, Height = Math.Round(width * 1.5), CornerRadius = new CornerRadius(10), Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["ControlFillColorSecondaryBrush"] };
        art.Children.Add(new TextBlock { Text = game.Name, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12), Opacity = 0.7, FontWeight = FontWeights.SemiBold });
        if (game.Cover is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var bmp = new BitmapImage { DecodePixelWidth = (int)Math.Min(440, width * 1.6), DecodePixelType = DecodePixelType.Logical };
            // A missing vertical cover (old Steam games) falls back to the wide header.
            bmp.ImageFailed += (_, _) => { if (game.Wide is { } w && Uri.TryCreate(w, UriKind.Absolute, out var wu) && bmp.UriSource != wu) bmp.UriSource = wu; };
            bmp.UriSource = uri;
            art.Children.Add(Smooth.Art(bmp, art.CornerRadius, fadeMs: 200));
        }
        var logo = StoreLogos.Tile(game.Store, size: 22);
        logo.HorizontalAlignment = HorizontalAlignment.Left;
        logo.VerticalAlignment = VerticalAlignment.Top;
        logo.Margin = new Thickness(8);
        art.Children.Add(logo);

        var job = Downloads.Find(game.Id);
        bool active = job is { State: DownloadState.Downloading or DownloadState.Waiting or DownloadState.Paused };
        if (installedGame is not null)
        {
            var check = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x14, 0x18)),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(8, 3, 9, 4),
                Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = Inline("", L.T("Installed"), Ui.ToneColor(Ui.Tone.Good)),
            };
            art.Children.Add(check);
        }
        else if (active)
        {
            // The download's bar along the bottom of the cover.
            var shade = new Border { Height = 70, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Background = BottomShade(), CornerRadius = new CornerRadius(0, 0, 10, 10) };
            var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 10), Spacing = 6 };
            var label = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis };
            var bar = new ProgressBar { Maximum = 1 };
            bottom.Children.Add(label);
            bottom.Children.Add(bar);
            art.Children.Add(shade);
            art.Children.Add(bottom);
            // The bar follows the download while the cover is on screen (the cover itself is not made again).
            void Follow()
            {
                if (Downloads.Find(game.Id) is not { } j) return;
                label.Text = StateText(j) + (j.Total > 0 && j.State != DownloadState.Done ? $"  {j.Fraction:P0}" : "");
                bar.Value = j.Fraction;
                bar.IsIndeterminate = j.State == DownloadState.Waiting && !j.Queued || j.State == DownloadState.Downloading && j.Total <= 0;
                bar.ShowPaused = j.State == DownloadState.Paused || j.Queued;
                bar.ShowError = j.Error is not null && j.State == DownloadState.Paused;
            }
            Follow();
            art.Loaded += (_, _) => { Downloads.Changed -= Follow; Downloads.Changed += Follow; };
            art.Unloaded += (_, _) => Downloads.Changed -= Follow;
        }

        // Hover: a veil and one clear action.
        if (!active)
        {
            var action = new Button
            {
                Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["AccentButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(18, 8, 20, 9),
                CornerRadius = new CornerRadius(18),
                Content = Inline(installedGame is null ? "" : "", installedGame is null ? L.T("Install") : L.T("Open"), null, 14),
            };
            action.Click += (_, _) =>
            {
                if (installedGame is not null) App.Window?.OpenGame(installedGame.Id);
                else Install(game, started);
            };
            var veil = new Grid
            {
                CornerRadius = art.CornerRadius,
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0x08, 0x08, 0x0C)),
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
            };
            veil.Children.Add(action);
            art.Children.Add(veil);
            art.PointerEntered += (_, _) => veil.Opacity = 1;
            art.PointerExited += (_, _) => veil.Opacity = 0;
            art.PointerCanceled += (_, _) => veil.Opacity = 0;
            // Keyboard: the button stays reachable with Tab and shows while it has focus.
            action.GotFocus += (_, _) => veil.Opacity = 1;
            action.LostFocus += (_, _) => veil.Opacity = 0;
        }

        var root = new StackPanel { Width = width, Tag = game.Id };
        root.Children.Add(art);
        // A click on the cover (not on its button) opens the game's page, like the library.
        art.Tapped += (_, e) =>
        {
            for (var el = e.OriginalSource as DependencyObject; el is not null && el != art; el = VisualTreeHelper.GetParent(el))
                if (el is ButtonBase) return;
            Open(game);
        };
        root.Children.Add(new TextBlock { Text = game.Name, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(2, 8, 2, 0), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["BodyStrongTextBlockStyle"] });
        root.Children.Add(new TextBlock { Text = StoreLogos.Label(game.Store), Margin = new Thickness(2, 0, 2, 2), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"] });

        // The menu is made on the first right-click: hundreds of covers do not each carry one they may never show.
        root.ContextRequested += (_, e) =>
        {
            var menu = new MenuFlyout();
            if (installedGame is null && !active)
            {
                var install = new MenuFlyoutItem { Text = L.T("Install"), Icon = new FontIcon { Glyph = "" } };
                install.Click += (_, _) => Install(game, started);
                menu.Items.Add(install);
            }
            if (game.PageUri is { } page)
            {
                var store = new MenuFlyoutItem { Text = L.T("Store page"), Icon = new FontIcon { Glyph = "" } };
                store.Click += (_, _) => Ui.Open(page);
                menu.Items.Add(store);
            }
            var hide = new MenuFlyoutItem { Text = L.T("Hide this game"), Icon = new FontIcon { Glyph = "" } };
            hide.Click += (_, _) =>
            {
                AppState.Settings.HiddenGames.Add(game.Id);
                SettingsStore.Save();
                Loaded?.Invoke();
            };
            menu.Items.Add(hide);
            if (e.TryGetPosition(root, out var at)) menu.ShowAt(root, at); else menu.ShowAt(root);
            e.Handled = true;
        };
        return root;
    }

    /// <summary>A download's state in a few words, the same on the covers, the Downloads tab and the game's page.</summary>
    public static string StateText(DownloadJob j) => j.CancelRequested && j.State != DownloadState.Failed ? L.T("Cancelling…") : j.State switch
    {
        DownloadState.Downloading when j.IsUpdate => L.T("Updating"),
        DownloadState.Downloading => L.T("Downloading"),
        DownloadState.Waiting when j.Queued => L.T("Queued"),
        DownloadState.Waiting when j.Direct && j.Error is not null => L.T("Reconnecting"),
        DownloadState.Waiting when j.Direct && j.IsUpdate => L.T("Preparing the update"),
        DownloadState.Waiting when j.Direct => L.T("Preparing"),
        DownloadState.Waiting => L.T("Waiting for the launcher"),
        DownloadState.Paused when j.Direct => j.Error is null ? L.T("Paused") : L.T("Stopped by an error"),
        DownloadState.Paused => L.T("Paused or queued"),
        DownloadState.Done when j.IsUpdate => L.T("Updated"),
        DownloadState.Done => L.T("Installed"),
        _ => j.CancelRequested ? L.T("Cancelled") : L.T("Stopped"),
    };

    static StackPanel Inline(string glyph, string text, Color? tint, double size = 12)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        p.Children.Add(new FontIcon { Glyph = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Foreground = tint is { } c ? new SolidColorBrush(c) : null });
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (tint is not null) t.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        p.Children.Add(t);
        return p;
    }

    static LinearGradientBrush BottomShade() => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(0x00, 0, 0, 0), Offset = 0 },
            new GradientStop { Color = Color.FromArgb(0xD0, 0, 0, 0), Offset = 1 },
        },
    };
}

/// <summary>
/// The Downloads tab, laid out like the library: the download in progress on top (art, progress, speed graph like Steam's),
/// the queue and finished installs under it, then every game you own on the linked launchers as covers, filtered by
/// launcher, search, sort and what to show. Linked accounts sit in a menu in the header. Installs run in the launchers.
/// </summary>
public sealed partial class DownloadsPage : Page
{
    const double MinTile = 150, MaxTile = 230, Gap = 16;

    readonly StackPanel _root = new() { Spacing = 22, Padding = new Thickness(0, 8, 0, 40), HorizontalAlignment = HorizontalAlignment.Center };
    readonly ScrollViewer _scroller;
    readonly TextBlock _count = Ui.Muted("");
    readonly StackPanel _active = new() { Spacing = 10 };
    // The covers: virtualized, so only the ones on screen (and a little around) exist; hundreds of games stay smooth.
    // The repeater gets plain game ids: a list of strings crosses into WinUI in a trimmed build, a collection of Hybrid Launcher's own
    // records does not (0.17: the page could not open at all). Each id's game is looked up in _games.
    List<string> _items = [];
    readonly Dictionary<string, OwnedGame> _games = new(StringComparer.OrdinalIgnoreCase);
    readonly UniformGridLayout _layout = new() { MinColumnSpacing = Gap, MinRowSpacing = Gap, ItemsJustification = UniformGridLayoutItemsJustification.Start };
    readonly ItemsRepeater _grid;
    // Typing in the search waits for a short pause before the covers are filtered again.
    readonly DispatcherQueueTimer _searchDelay;
    readonly StackPanel _storeChips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly AutoSuggestBox _search = new() { PlaceholderText = L.T("Search your games"), QueryIcon = new SymbolIcon(Symbol.Find), Width = 260 };
    readonly ComboBox _sort = new() { MinWidth = 150 };
    // What to show: a segmented switch (one click, always visible) instead of a drop-down.
    readonly StackPanel _show = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    int _showIndex;
    readonly TextBlock _gamesCount = new() { FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    // Covers already made, kept for when they scroll back into view (made again only when their game, state or size changes):
    // making a cover is what used to stutter while scrolling. Capped, oldest first, so a huge library does not hold them all.
    readonly Dictionary<string, (string Key, FrameworkElement Tile)> _tileCache = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _tileOrder = new();
    const int TileCacheSize = 160;
    readonly TextBlock _empty = new() { Style = Ui.Style("BodyTextBlockStyle"), Foreground = Ui.Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly ProgressRing _ring = new() { IsActive = true, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel _accountLogos = new() { Orientation = Orientation.Horizontal, Spacing = -4, VerticalAlignment = VerticalAlignment.Center };
    // 420: a flyout is at most 456 wide with its padding; wider content was cut off on the right.
    readonly StackPanel _accountsPanel = new() { Spacing = 8, Width = 420 };
    readonly Border _installRootRow = new();
    Flyout? _accountsFlyout;
    readonly Dictionary<string, JobView> _views = [];
    string _jobsKey = "";
    string _store = "all";
    double _tile = 180;

    public DownloadsPage()
    {
        InitializeComponent();
        // Kept in memory between visits: the covers are there at once when the page comes back.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        // ---- header: like the library's (title, a count line, the actions on the right) ----
        var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = L.T("Downloads"), Style = Ui.Style("TitleTextBlockStyle") });
        title.Children.Add(_count);
        var accountsContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        accountsContent.Children.Add(_accountLogos);
        accountsContent.Children.Add(new TextBlock { Text = L.T("Accounts"), VerticalAlignment = VerticalAlignment.Center });
        var accounts = new DropDownButton { Content = accountsContent, Padding = new Thickness(12, 6, 12, 7), VerticalAlignment = VerticalAlignment.Center };
        var flyoutBody = new StackPanel { Spacing = 12 };
        flyoutBody.Children.Add(new TextBlock { Text = L.T("Linked accounts"), Style = Ui.Style("BodyStrongTextBlockStyle"), FontSize = 16 });
        flyoutBody.Children.Add(new TextBlock { Text = L.T("Not signed in, the lists come from the launchers on this PC. Sign in to an account to list everything you own and let Hybrid Launcher download and uninstall games itself, with no launcher."), Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        flyoutBody.Children.Add(_accountsPanel);
        _installRootRow.Width = 420;
        flyoutBody.Children.Add(_installRootRow);
        accounts.Flyout = _accountsFlyout = new Flyout { Content = flyoutBody, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        ToolTipService.SetToolTip(accounts, L.T("Linked accounts"));
        var refresh = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(refresh, L.T("Read the launcher libraries again"));
        refresh.Click += (_, _) => _ = ReloadAsync(true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(accounts);
        actions.Children.Add(refresh);
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(title);
        Grid.SetColumn(actions, 1);
        head.Children.Add(actions);

        // ---- your games: launcher chips, search, sort, what to show ----
        foreach (var s in new[] { L.T("Name"), L.T("Launcher") }) _sort.Items.Add(s);
        _sort.SelectedIndex = 0;
        _sort.SelectionChanged += (_, _) => Restart();
        var showBox = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(3), Background = Ui.Brush("ControlFillColorSecondaryBrush"), Child = _show };
        foreach (var (i, s) in new[] { L.T("Not installed"), L.T("Installed"), L.T("All games") }.Index())
        {
            var seg = new ToggleButton { Content = s, IsChecked = i == 0, CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 5, 14, 6), BorderThickness = new Thickness(0), Tag = i };
            if (i != 0) seg.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            seg.Click += (_, _) =>
            {
                _showIndex = (int)seg.Tag;
                foreach (var t in _show.Children.OfType<ToggleButton>())
                {
                    t.IsChecked = (int)t.Tag == _showIndex;
                    t.ClearValue(Control.BackgroundProperty);
                    if (t.IsChecked != true) t.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
                Restart();
            };
            _show.Children.Add(seg);
        }
        _searchDelay = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _searchDelay.Interval = TimeSpan.FromMilliseconds(220);
        _searchDelay.IsRepeating = false;
        _searchDelay.Tick += (_, _) => Restart();
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        // Covers are made a few screens ahead (and kept a few behind), so scrolling meets covers that already exist.
        _grid = new ItemsRepeater { Layout = _layout, ItemTemplate = new TileFactory(this), VerticalCacheLength = 4 };
        _layout.MinItemWidth = _tile;
        _layout.MinItemHeight = Math.Round(_tile * 1.5) + 48;

        // One toolbar: what to show and the launchers on the left, search and order on the right (wrapping on narrow windows).
        _search.Width = 240;
        var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sortRow.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        sortRow.Children.Add(_sort);
        var filters = new WrapPanel(10, [showBox, _storeChips, _search, sortRow]);
        foreach (var f in filters.Children.OfType<FrameworkElement>()) f.VerticalAlignment = VerticalAlignment.Center;
        var toolbar = new Border { Style = Ui.Style("RowStyle"), MinHeight = 0, Padding = new Thickness(12, 10, 12, 10), Child = filters };

        var library = new StackPanel { Spacing = 14 };
        var gamesHead = SectionLabel("", L.T("YOUR GAMES"));
        gamesHead.Children.Add(_gamesCount);
        library.Children.Add(gamesHead);
        library.Children.Add(toolbar);
        library.Children.Add(_ring);
        library.Children.Add(_empty);
        library.Children.Add(_grid);

        _root.Children.Add(head);
        _root.Children.Add(_active);
        _root.Children.Add(library);
        _scroller = new ScrollViewer { Content = _root, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _scroller.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        Content = _scroller;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Downloads.Changed += OnJobsChanged;
        OwnedLibrary.Loaded += OnLoaded;
        AppState.GamesChanged += OnLoaded;
        Downloads.Watch();
        FillJobs();
        _ = ReloadAsync(false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Downloads.Changed -= OnJobsChanged;
        OwnedLibrary.Loaded -= OnLoaded;
        AppState.GamesChanged -= OnLoaded;
    }

    /// <summary>Whole columns of covers, the block centred, like the library.</summary>
    void Layout(double width)
    {
        if (width <= 0) return;
        double avail = Math.Max(MinTile, width - 56);
        int cols = Math.Max(1, (int)((avail + Gap) / (MinTile + Gap)));
        double tile = Math.Floor(Math.Min(MaxTile, (avail - Gap * (cols - 1)) / cols));
        _root.Width = cols * (tile + Gap) - Gap;
        if (Math.Abs(tile - _tile) < 0.5) return;
        _tile = tile;
        SizeTiles();
    }

    void OnJobsChanged()
    {
        FillJobs();
        App.Window?.UpdateDownloadsBadge();
    }

    void OnLoaded()
    {
        FillAccounts();
        // A finished download's game is in the list now: its line gets its Play button.
        FillJobs();
        FillChips();
        Fill();
        UpdateCount();
    }

    void Restart() => Fill();

    async Task ReloadAsync(bool force)
    {
        _ring.Visibility = Visibility.Visible;
        FillAccounts();
        // A finished download's game is in the list now: its line gets its Play button.
        FillJobs();
        try { await OwnedLibrary.LoadAsync(force); }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not read the launcher libraries"), ex.Message); }
        _ring.Visibility = Visibility.Collapsed;
        OnLoaded();
    }

    static StackPanel SectionLabel(string glyph, string text, string? count = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = text, Style = Ui.Style("GroupHeader"), VerticalAlignment = VerticalAlignment.Center });
        if (count is not null) head.Children.Add(new TextBlock { Text = count, FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        return head;
    }

    void UpdateCount()
    {
        var owned = OwnedLibrary.Owned().ToList();
        var installedIds = OwnedLibrary.InstalledIds();
        int installed = owned.Count(g => installedIds.Contains(g.Id));
        int launchers = OwnedLibrary.All.Keys.Count(s => OwnedLibrary.Linked(s) && s != GameStore.Xbox);
        int active = Downloads.ActiveCount;
        _count.Text = !OwnedLibrary.IsLoaded ? L.T("Reading your launchers…")
            : L.F("{0} games on {1} launchers", owned.Count, launchers) + "  ·  " + L.F("{0} installed", installed)
              + (active > 0 ? "  ·  " + L.N(active, "{0} download", "{0} downloads") : "");
    }

    // ---------- accounts (the header's menu) ----------

    void FillAccounts()
    {
        _accountsPanel.Children.Clear();
        _accountLogos.Children.Clear();
        var a = AppState.Settings.Accounts;
        foreach (var store in OwnedLibrary.Stores)
        {
            OwnedLibrary.All.TryGetValue(store, out var list);
            bool linked = OwnedLibrary.Linked(store);
            if (linked)
            {
                var mini = StoreLogos.Tile(store, size: 20);
                mini.BorderBrush = Ui.Brush("SolidBackgroundFillColorBaseBrush");
                mini.BorderThickness = new Thickness(1.5);
                _accountLogos.Children.Add(mini);
            }
            int installed = AppState.Games.Count(g => g.Store == store && !g.Installing);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            text.Children.Add(new TextBlock { Text = StoreLogos.Label(store) + (list?.Account is { } who ? $"  ·  {who}" : ""), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            string detail = !linked ? L.T("Not linked: its games are not listed here.")
                : list is null ? L.T("Reading…")
                : store == GameStore.Xbox ? L.F("{0} installed", installed) + (list.Problem is { } xp ? $". {xp}" : "")
                : L.F("{0} owned, {1} installed", list.Games.Count, installed) + $"  ·  {list.Source}" + (list.Problem is { } p ? $"  ·  {p}" : "");
            text.Children.Add(new TextBlock { Text = detail, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-8, 2, 0, 0) };
            if (store != GameStore.Xbox)
            {
                // Signed in, Hybrid Launcher lists the whole library and downloads and uninstalls games itself, with no launcher.
                bool signedIn = OwnedGames.Direct(store);
                var sign = new HyperlinkButton { Content = signedIn ? L.T("Sign out") : L.T("Sign in to download directly"), Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
                ToolTipService.SetToolTip(sign, signedIn
                    ? L.T("Hybrid Launcher forgets this account's sign-in. Games it installed stay.")
                    : L.F("Sign in to {0} in Hybrid Launcher: every game you own is listed, and Hybrid Launcher downloads and uninstalls them itself, without the launcher.", StoreLogos.Label(store)));
                sign.Click += async (_, _) =>
                {
                    _accountsFlyout?.Hide();
                    if (signedIn) AccountSignIn.SignOut(store);
                    else if (await AccountSignIn.RunAsync(store, XamlRoot, ActualTheme) is null) return;
                    await ReloadAsync(true);
                };
                links.Children.Add(sign);
                if (signedIn) text.Children.Insert(1, new TextBlock { Text = L.T("Signed in: Hybrid Launcher downloads and uninstalls these games itself."), Style = Ui.Style("RowDescription"), Foreground = Ui.Brush("SystemFillColorSuccessBrush") });
            }
            if (store == GameStore.Xbox && linked)
            {
                var app = new HyperlinkButton { Content = L.T("Open the Xbox app"), Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
                app.Click += (_, _) => Ui.Open("msxbox://");
                links.Children.Add(app);
            }
            if (links.Children.Count > 0) text.Children.Add(links);
            var toggle = new ToggleSwitch { IsOn = linked, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(toggle, linked ? L.T("Linked") : L.T("Off"));
            toggle.Toggled += (_, _) =>
            {
                switch (store)
                {
                    case GameStore.Steam: a.Steam = toggle.IsOn; break;
                    case GameStore.Epic: a.Epic = toggle.IsOn; break;
                    case GameStore.Gog: a.Gog = toggle.IsOn; break;
                    default: a.Xbox = toggle.IsOn; break;
                }
                SettingsStore.Save();
                _ = ReloadAsync(true);
            };
            var row = new Grid { ColumnSpacing = 12, Opacity = linked ? 1 : 0.7 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = StoreLogos.Tile(store, size: 32);
            tile.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(tile);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);
            _accountsPanel.Children.Add(row);
        }
        _installRootRow.Child = InstallRootRow();
        if (_accountLogos.Children.Count == 0) _accountLogos.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
    }

    /// <summary>
    /// Where Hybrid Launcher puts the games it downloads itself, one line per store: the launcher's own folder by default (so the game
    /// sits where that launcher would install it), or one chosen here; "Launcher's folder" goes back to it.
    /// </summary>
    FrameworkElement InstallRootRow()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = L.T("Install folders"), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L.T("Games go where their launcher installs them, under the launcher's own folder name, so the launcher sees them too."), Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        foreach (var store in new[] { GameStore.Steam, GameStore.Epic, GameStore.Gog })
        {
            bool chosen = AppState.Settings.InstallRoots.ContainsKey(store.ToString());
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            name.Children.Add(StoreLogos.Create(store, 14));
            name.Children.Add(new TextBlock { Text = StoreLogos.Label(store) + (chosen ? "" : "  ·  " + L.T("launcher's folder")), FontSize = 13 });
            text.Children.Add(name);
            var path = Downloads.InstallRootFor(store);
            var pathText = new TextBlock { Text = path, Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(pathText, path);
            text.Children.Add(pathText);
            var change = new Button { Content = L.T("Change…"), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(change, L.F("The folder {0} games downloaded by Hybrid Launcher go in (one folder per game)", StoreLogos.Label(store)));
            change.Click += async (_, _) =>
            {
                if (App.Window is null || await Pickers.PickFolderAsync(App.Window) is not { } dir) return;
                AppState.Settings.InstallRoots[store.ToString()] = dir;
                SettingsStore.Save();
                _installRootRow.Child = InstallRootRow();
            };
            var reset = new Button { Content = L.T("Launcher's folder"), VerticalAlignment = VerticalAlignment.Center, IsEnabled = chosen };
            ToolTipService.SetToolTip(reset, Downloads.LauncherRoot(store));
            reset.Click += (_, _) =>
            {
                AppState.Settings.InstallRoots.Remove(store.ToString());
                SettingsStore.Save();
                _installRootRow.Child = InstallRootRow();
            };
            // The path takes the whole width (it is long); the buttons sit under it.
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(change);
            buttons.Children.Add(reset);
            var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(text);
            row.Children.Add(buttons);
            panel.Children.Add(row);
        }
        return new Border { Style = Ui.Style("RowStyle"), MinHeight = 0, Padding = new Thickness(12, 8, 12, 10), Child = panel };
    }

    // ---------- in progress, queue, finished ----------

    /// <summary>
    /// The first active download gets the big card; the other active ones are the queue; finished ones a short list.
    /// The views are kept and updated in place every 2 s (no flicker of the art); rebuilt only when the set changes.
    /// </summary>
    void FillJobs()
    {
        var jobs = Downloads.All;
        // Steam's order: the one downloading, one getting ready (or reconnecting), the queue in its order, then the paused ones.
        var live = jobs.Where(j => j.State is DownloadState.Downloading or DownloadState.Waiting or DownloadState.Paused)
            .OrderBy(j => j.State == DownloadState.Downloading ? 0 : j.State == DownloadState.Waiting && !j.Queued ? 1 : j.Queued ? 2 : 3).ToList();
        var done = jobs.Where(j => j.State is DownloadState.Done or DownloadState.Failed).ToList();
        // Rebuilt only when downloads come, go or change places; their state, figures and buttons update in place.
        var key = string.Join("|", live.Select(j => j.Id)) + "#" + string.Join("|", done.Select(j => j.Id + j.State));
        RefreshChangedTiles(jobs);
        if (key != _jobsKey)
        {
            _jobsKey = key;
            _views.Clear();
            _active.Children.Clear();
            if (live.Count > 0)
            {
                _active.Children.Add(SectionLabel("", L.T("NOW DOWNLOADING")));
                var hero = new JobView(live[0], hero: true);
                _views[live[0].Id] = hero;
                _active.Children.Add(hero.Root);
            }
            if (live.Count > 1)
            {
                _active.Children.Add(SectionLabel("", L.T("UP NEXT"), (live.Count - 1).ToString()));
                foreach (var j in live.Skip(1))
                {
                    var v = new JobView(j, hero: false);
                    _views[j.Id] = v;
                    _active.Children.Add(v.Root);
                }
            }
            if (done.Count > 0)
            {
                var row = new Grid { Margin = new Thickness(0, live.Count > 0 ? 10 : 0, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(SectionLabel("", L.T("COMPLETED"), done.Count.ToString()));
                var clear = new HyperlinkButton { Content = L.T("Clear all"), Padding = new Thickness(8, 2, 8, 2) };
                clear.Click += (_, _) => Downloads.ClearFinished();
                Grid.SetColumn(clear, 1);
                row.Children.Add(clear);
                _active.Children.Add(row);
                foreach (var j in done)
                {
                    var v = new JobView(j, hero: false);
                    _views[j.Id] = v;
                    _active.Children.Add(v.Root);
                }
            }
            _active.Visibility = _active.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var v in _views.Values) v.Update();
        UpdateCount();
    }

    // What each game's cover last showed: nothing, a download's bar, or installed. Only the covers that changed are made again
    // (not the whole grid, whose hundreds of covers would load their art again at every step of a download).
    readonly Dictionary<string, string> _tileStates = new(StringComparer.OrdinalIgnoreCase);

    static string TileState(string id) =>
        AppState.Find(id) is { Installing: false } ? "installed" : Downloads.Active(id) is not null ? "downloading" : "";

    void RefreshChangedTiles(IReadOnlyList<DownloadJob> jobs)
    {
        foreach (var id in jobs.Select(j => j.Id).Concat(_tileStates.Keys.ToList()).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var now = TileState(id);
            if (_tileStates.TryGetValue(id, out var before) && before == now) continue;
            _tileStates[id] = now;
            // Only the covers on screen exist: that one is made again in its cell, the others get the new state as they scroll in.
            int i = _items.FindIndex(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
            _tileCache.Remove(id);
            if (i >= 0 && _grid.TryGetElement(i) is Border { Tag: string } cell && _games.TryGetValue(id, out var game))
            {
                cell.Child = null;
                cell.Child = CachedTile(game);
            }
        }
    }

    /// <summary>One followed install: a big card (art, progress, figures, speed graph) or a compact row.</summary>
    sealed class JobView
    {
        public readonly FrameworkElement Root;
        readonly DownloadJob _job;
        readonly bool _hero;
        readonly Meter _bar = new();
        readonly TextBlock _percent = new();
        readonly TextBlock _bytes = Ui.Muted("");
        readonly TextBlock _speed = new() { FontSize = 17, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _left = new() { FontSize = 17, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _peak = new() { FontSize = 17, FontWeight = FontWeights.SemiBold };
        readonly Border _pill = new() { VerticalAlignment = VerticalAlignment.Center };
        readonly Canvas _graph = new() { Height = 96, HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly Polyline _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        readonly Polygon _area = new();
        // The buttons, made again only when the state calls for other ones (pause ↔ resume, Start now, Retry).
        readonly Border _actions = new() { VerticalAlignment = VerticalAlignment.Center };
        string _actionsKey = "";

        public JobView(DownloadJob job, bool hero)
        {
            _job = job;
            _hero = hero;
            Root = hero ? BuildHero() : BuildRow();
        }

        static Border Cover(DownloadJob j, double w, double h, double radius)
        {
            var cover = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(radius), Background = Ui.Brush("ControlFillColorSecondaryBrush") };
            if (j.Cover is { } c && Uri.TryCreate(c, UriKind.Absolute, out var u))
                cover.Child = Smooth.Art(new BitmapImage { UriSource = u, DecodePixelWidth = (int)(w * 2) }, cover.CornerRadius, fadeMs: 180);
            return cover;
        }

        static string? Wide(DownloadJob j) =>
            OwnedLibrary.Find(j.Id)?.Wide ?? (j.Id.StartsWith("steam:") ? $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{j.Id[6..]}/library_hero.jpg" : null);

        /// <summary>A figure as a small glass tile: an icon, its label and the value.</summary>
        static Border Figure(string glyph, string label, TextBlock value)
        {
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            var p = new StackPanel { Spacing = 0 };
            p.Children.Add(new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, CharacterSpacing = 60, Opacity = 0.65 });
            p.Children.Add(value);
            Grid.SetColumn(p, 1);
            grid.Children.Add(p);
            return new Border
            {
                Child = grid, MinWidth = 132, Padding = new Thickness(12, 7, 14, 8), CornerRadius = new CornerRadius(10),
                Background = new HybridLauncher.Helpers.GlassBrush(Color.FromArgb(0x8C, 0x2C, 0x2E, 0x35), 18), BorderThickness = new Thickness(0),
            };
        }

        bool Live => _job.State is DownloadState.Downloading or DownloadState.Paused or DownloadState.Waiting;

        /// <summary>
        /// Steam's way: one button per download. In progress, ✕ cancels it (deleting what was downloaded); finished, Play opens the
        /// game and ✕ only clears the line. The rest (the launcher's own page, stop following) sits in the ⋯ menu.
        /// </summary>
        FrameworkElement Actions()
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (_job.State == DownloadState.Done && AppState.Find(_job.Id) is { Installing: false } game)
            {
                var play = new Button { Style = Ui.Style("AccentButtonStyle"), Padding = new Thickness(14, 5, 16, 6), Margin = new Thickness(0, 0, 4, 0), Content = Labelled("", L.T("Play")) };
                play.Click += async (_, _) =>
                {
                    try { await AppState.LaunchAsync(game); }
                    catch (Exception ex) { App.Window?.ShowError(L.T("The game did not start"), ex.Message); }
                };
                buttons.Children.Add(play);
            }
            if (Live && _job.Direct)
            {
                // Hybrid Launcher's own download, Steam's buttons: Start now in the queue, Retry after an error, else pause / resume
                // (what is done stays on disk).
                if (_job.Queued)
                {
                    var now = new Button { Padding = new Thickness(12, 5, 14, 6), Content = Labelled("", L.T("Start now")), IsEnabled = !_job.CancelRequested };
                    ToolTipService.SetToolTip(now, L.T("Downloads this one first; the one downloading now goes on after it."));
                    now.Click += (_, _) => Downloads.StartNow(_job);
                    buttons.Children.Add(now);
                }
                else if (_job.State == DownloadState.Paused && _job.Error is not null)
                {
                    var retry = new Button { Style = Ui.Style("AccentButtonStyle"), Padding = new Thickness(12, 5, 14, 6), Content = Labelled("", L.T("Retry")), IsEnabled = !_job.CancelRequested };
                    retry.Click += (_, _) => Downloads.Resume(_job);
                    buttons.Children.Add(retry);
                }
                else
                {
                    bool paused = _job.State == DownloadState.Paused;
                    var toggle = new Button { Style = Ui.Style("IconButton"), Content = new FontIcon { Glyph = paused ? "" : "", FontSize = 14 }, IsEnabled = !_job.CancelRequested };
                    ToolTipService.SetToolTip(toggle, paused ? L.T("Resume") : L.T("Pause"));
                    toggle.Click += (_, _) => { if (paused) Downloads.Resume(_job); else Downloads.Pause(_job); };
                    buttons.Children.Add(toggle);
                }
            }
            else if (Live)
            {
                var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
                var launcher = new MenuFlyoutItem { Text = L.F("Open in {0}", StoreLogos.Label(_job.Store)), Icon = new FontIcon { Glyph = "" } };
                launcher.Click += (_, _) => Ui.Open(_job.Store switch
                {
                    GameStore.Steam => "steam://open/downloads",
                    GameStore.Epic => "com.epicgames.launcher://library",
                    GameStore.Gog => "goggalaxy://",
                    _ => "msxbox://",
                });
                menu.Items.Add(launcher);
                var forget = new MenuFlyoutItem { Text = L.T("Stop following it here"), Icon = new FontIcon { Glyph = "" } };
                ToolTipService.SetToolTip(forget, L.T("The download goes on in the launcher; Hybrid Launcher just stops showing it."));
                forget.Click += (_, _) => Downloads.Remove(_job);
                menu.Items.Add(forget);
                var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = Ui.Style("IconButton"), Flyout = menu };
                ToolTipService.SetToolTip(more, L.T("More"));
                buttons.Children.Add(more);
            }
            var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = Ui.Style("IconButton"), IsEnabled = !_job.CancelRequested };
            var tip = Live ? L.T("Cancel download") : L.T("Remove from the list");
            ToolTipService.SetToolTip(close, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, tip);
            if (Live) close.Click += (_, _) => _ = CancelAsync(close);
            else close.Click += (_, _) => Downloads.Remove(_job);
            buttons.Children.Add(close);
            // The big card: round glass buttons, large enough to hit without aiming.
            if (_hero)
            {
                buttons.Spacing = 8;
                foreach (var b in buttons.Children.OfType<Button>().Where(b => b.Content is FontIcon))
                {
                    b.Width = b.Height = 40;
                    b.CornerRadius = new CornerRadius(20);
                    b.Background = new HybridLauncher.Helpers.GlassBrush(Color.FromArgb(0x8C, 0x2C, 0x2E, 0x35), 18);
                    b.BorderThickness = new Thickness(0);
                    if (b.Content is FontIcon icon) icon.FontSize = 15;
                }
            }
            return buttons;
        }

        static StackPanel Labelled(string glyph, string text)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            p.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return p;
        }

        /// <summary>Asks first (a download that has data on disk), then cancels; what went wrong shows as a message.</summary>
        async Task CancelAsync(FrameworkElement anchor)
        {
            var job = _job;
            try
            {
                if (job.State != DownloadState.Waiting || job.Store == GameStore.Steam)
                {
                    var root = anchor.XamlRoot ?? App.Window?.Content?.XamlRoot;
                    if (root is null) return;
                    string text = job.Direct
                        ? L.T("The download stops and its folder is deleted.")
                        : job.Store == GameStore.Steam
                        ? L.T("Steam closes for a moment while Hybrid Launcher deletes what was downloaded, then it opens again in the background. Other downloads pick up where they were.")
                        : L.F("{0} keeps its downloads to itself: it opens, and you cancel the install there.", StoreLogos.Label(job.Store));
                    if (job.Done > 0) text += "\n\n" + L.F("{0} downloaded so far will be deleted.", DiskUsage.Format(job.Done));
                    var dialog = new ContentDialog
                    {
                        XamlRoot = root,
                        RequestedTheme = anchor.ActualTheme,
                        Title = L.F("Cancel the download of {0}?", job.Name),
                        Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = text },
                        PrimaryButtonText = L.T("Cancel download"),
                        CloseButtonText = L.T("Keep downloading"),
                        DefaultButton = ContentDialogButton.Close,
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                }
                var message = await Downloads.CancelAsync(job);
                if (message is null) App.Window?.ShowInfo(L.F("The download of {0} was cancelled.", job.Name));
                else if (job.Store == GameStore.Steam) App.Window?.ShowError(L.T("Could not cancel the download"), message);
                else App.Window?.ShowInfo(message);
            }
            catch (Exception ex)
            {
                Log.Error("Cancel download", ex);
                App.Window?.ShowError(L.T("Could not cancel the download"), ex.Message);
            }
        }

        /// <summary>The cover and the name open the game's page (Download and its progress there too).</summary>
        void OpensPage(UIElement element)
        {
            element.Tapped += (_, _) =>
            {
                if (AppState.Find(_job.Id) is null && OwnedLibrary.Find(_job.Id) is { } owned) OwnedLibrary.Open(owned);
                else App.Window?.OpenGame(_job.Id);
            };
        }

        FrameworkElement BuildHero()
        {
            // Steam's downloads page, cleaner: the game's art behind everything, the network graph along the whole bottom of the
            // card, the cover, name and a thick bar on the left, the figures as glass tiles, the buttons top right.
            var card = new Grid { Height = 262, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x12, 0x18)), RequestedTheme = ElementTheme.Dark };
            if (Wide(_job) is { } wide && Uri.TryCreate(wide, UriKind.Absolute, out var wu))
            {
                var art = Smooth.Art(new BitmapImage { DecodePixelWidth = 1600, UriSource = wu }, Smooth.Inner(new Border { CornerRadius = card.CornerRadius, BorderThickness = card.BorderThickness }), fadeMs: 300);
                art.Loaded += (_, _) => art.Opacity = Math.Min(art.Opacity, 0.5);
                card.Children.Add(new Border { Child = art, Opacity = 0.5 });
            }
            var inner = Smooth.Inner(new Border { CornerRadius = card.CornerRadius, BorderThickness = card.BorderThickness });
            card.Children.Add(new Border { Background = Shade(0xF4, 0xD8, 0x70, horizontal: true), CornerRadius = inner });
            card.Children.Add(new Border { Background = Shade(0x00, 0x30, 0xC8, horizontal: false), CornerRadius = inner });

            // The speed graph: the last five minutes, filled under the line, the full width of the card.
            var accent = Ui.ToneColor(Ui.Tone.Accent);
            _line.Stroke = new SolidColorBrush(Color.FromArgb(0xE0, accent.R, accent.G, accent.B));
            _area.Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x58, accent.R, accent.G, accent.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x00, accent.R, accent.G, accent.B), Offset = 1 },
                },
            };
            _graph.Children.Add(_area);
            _graph.Children.Add(_line);
            _graph.Height = 92;
            _graph.VerticalAlignment = VerticalAlignment.Bottom;
            _graph.IsHitTestVisible = false;
            _graph.SizeChanged += (_, _) => DrawGraph();
            card.Children.Add(_graph);

            var layout = new Grid { ColumnSpacing = 24, Padding = new Thickness(22) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // The text column stops at 760 (a bar across a 4K screen reads badly); the space after it stays empty.
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MaxWidth = 760 });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heroCover = Cover(_job, 136, 204, 10);
            OpensPage(heroCover);
            layout.Children.Add(heroCover);

            var info = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(StoreLogos.Tile(_job.Store, size: 18));
            line.Children.Add(new TextBlock { Text = StoreLogos.Label(_job.Store).ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(_pill);
            info.Children.Add(line);
            var heroName = new TextBlock { Text = _job.Name, FontSize = 28, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, -4, 0, 0) };
            OpensPage(heroName);
            info.Children.Add(heroName);
            var barRow = new Grid { ColumnSpacing = 14 };
            barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _bar.Height = 10;
            _bar.VerticalAlignment = VerticalAlignment.Center;
            barRow.Children.Add(_bar);
            _percent.FontSize = 22;
            _percent.FontWeight = FontWeights.Bold;
            _percent.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_percent, 1);
            barRow.Children.Add(_percent);
            info.Children.Add(barRow);
            _bytes.TextWrapping = TextWrapping.NoWrap;
            _bytes.TextTrimming = TextTrimming.CharacterEllipsis;
            _bytes.Margin = new Thickness(0, -6, 0, 0);
            info.Children.Add(_bytes);
            var figures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            figures.Children.Add(Figure("", L.T("SPEED"), _speed));
            figures.Children.Add(Figure("", L.T("TIME LEFT"), _left));
            figures.Children.Add(Figure("", L.T("PEAK"), _peak));
            info.Children.Add(figures);
            Grid.SetColumn(info, 1);
            layout.Children.Add(info);

            _actions.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(_actions, 3);
            layout.Children.Add(_actions);
            card.Children.Add(layout);
            return card;
        }

        /// <summary>A dark veil from one side to the other (three stops: start, middle, end) so the text reads on any art.</summary>
        static LinearGradientBrush Shade(byte a0, byte a1, byte a2, bool horizontal) => new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = horizontal ? new Windows.Foundation.Point(1, 0) : new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(a0, 0x0C, 0x0C, 0x12), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(a1, 0x0C, 0x0C, 0x12), Offset = 0.5 },
                new GradientStop { Color = Color.FromArgb(a2, 0x0C, 0x0C, 0x12), Offset = 1 },
            },
        };

        FrameworkElement BuildRow()
        {
            // One card per game: the cover, the name and its state, a bar only as wide as the text column, the figures on the right.
            bool finished = _job.State is DownloadState.Done or DownloadState.Failed;
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 760 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cover = Cover(_job, finished ? 36 : 48, finished ? 54 : 72, 6);
            OpensPage(cover);
            row.Children.Add(cover);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var name = new TextBlock { Text = _job.Name, FontWeight = FontWeights.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            OpensPage(name);
            title.Children.Add(name);
            title.Children.Add(_pill);
            text.Children.Add(title);
            if (!finished) { _bar.Height = 6; text.Children.Add(_bar); }
            text.Children.Add(_bytes);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            _percent.FontSize = 15;
            _percent.FontWeight = FontWeights.SemiBold;
            _percent.Opacity = 0.85;
            _percent.Margin = new Thickness(4, 0, 0, 0);
            _percent.VerticalAlignment = VerticalAlignment.Center;
            _percent.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(_percent, 2);
            row.Children.Add(_percent);
            Grid.SetColumn(_actions, 4);
            row.Children.Add(_actions);
            return new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"),
                Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"),
                Child = row,
            };
        }

        public void Update()
        {
            var j = _job;
            var state = OwnedLibrary.StateText(j);
            var tone = j.CancelRequested && j.State != DownloadState.Failed ? Ui.Tone.Bad : j.State switch
            {
                DownloadState.Downloading => Ui.Tone.Accent,
                DownloadState.Paused when j.Direct && j.Error is not null => Ui.Tone.Bad,
                DownloadState.Paused => Ui.Tone.Partial,
                DownloadState.Waiting when j.Error is not null => Ui.Tone.Partial,
                DownloadState.Done => Ui.Tone.Good,
                DownloadState.Failed => Ui.Tone.Bad,
                _ => Ui.Tone.Unknown,
            };
            // The game showing up as installed (the scan after the download) brings the Play button.
            var key = $"{j.State}|{j.Queued}|{j.CancelRequested}|{j.Error is null}|{AppState.Find(j.Id) is { Installing: false }}";
            if (key != _actionsKey)
            {
                _actionsKey = key;
                _pill.Child = Ui.Pill(state, tone);
                _actions.Child = Actions();
            }

            _bar.Set(j.Fraction,
                indeterminate: j.State == DownloadState.Waiting && !j.Queued || (j.State == DownloadState.Downloading && j.Total <= 0),
                tone: j.State == DownloadState.Paused && j.Error is not null ? Ui.Tone.Bad : j.State == DownloadState.Paused || j.Queued ? Ui.Tone.Partial : Ui.Tone.Accent);
            var percent = j.Total > 0 ? $"{j.Fraction * 100:0}%" : "";
            if (_percent.Text != percent) _percent.Text = percent;

            var parts = new List<string>();
            if (j.Total > 0) parts.Add(L.F("{0} of {1}", DiskUsage.Format(j.Done), DiskUsage.Format(j.Total)));
            else if (j.Done > 0) parts.Add(DiskUsage.Format(j.Done));
            if (!_hero && j.State == DownloadState.Downloading && j.Speed > 0) parts.Add($"{DiskUsage.Format((long)j.Speed)}/s");
            if (!_hero && j.Remaining is { } r) parts.Add(L.F("{0} left", Time(r)));
            if (j.Direct)
            {
                // One line that says what is going on, in Steam's words.
                if (j.Queued) parts.Add(L.T("starts when the download before it finishes"));
                else if (j.Preparing) parts.Add(L.F("reading the file list from {0}", StoreLogos.Label(j.Store)));
                else if (j.Error is not null) parts.Add(j.Error);
                else if (j.State == DownloadState.Paused) parts.Add(L.T("Resume goes on where it stopped"));
                else if (j.Folder is not null && j.State != DownloadState.Done) parts.Add(j.Folder);
            }
            else
            {
                if (j.State == DownloadState.Waiting) parts.Add(L.T("confirm the install in the launcher"));
                if (j.State == DownloadState.Paused) parts.Add(L.T("paused or waiting its turn in the launcher"));
            }
            if (!_hero) parts.Insert(0, StoreLogos.Label(j.Store));
            var bytes = string.Join("  ·  ", parts);
            if (bytes != _bytes.Text)
            {
                _bytes.Text = bytes;
                ToolTipService.SetToolTip(_bytes, bytes.Length > 0 ? bytes : null);
            }
            if (!_hero) return;

            _speed.Text = j.State == DownloadState.Downloading && j.Speed > 0 ? $"{DiskUsage.Format((long)j.Speed)}/s" : "—";
            _left.Text = j.Remaining is { } left ? Time(left) : "—";
            _peak.Text = j.Peak > 0 ? $"{DiskUsage.Format((long)j.Peak)}/s" : "—";
            DrawGraph();
        }

        static string Time(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min" : $"{t.Seconds} s";

        void DrawGraph()
        {
            double w = _graph.ActualWidth, h = _graph.Height;
            if (w <= 0) return;
            double[] samples;
            lock (_job.History) samples = [.. _job.History];
            const int span = Downloads.HistorySpan;
            double max = Math.Max(Math.Max(_job.Peak, samples.DefaultIfEmpty(0).Max()), 1) * 1.15;
            var pts = new PointCollection();
            var fill = new PointCollection();
            double step = w / (span - 1);
            double x0 = w - (samples.Length - 1) * step;
            fill.Add(new Windows.Foundation.Point(Math.Max(0, x0), h));
            for (int i = 0; i < samples.Length; i++)
            {
                var p = new Windows.Foundation.Point(x0 + i * step, h - samples[i] / max * (h - 4));
                pts.Add(p);
                fill.Add(p);
            }
            fill.Add(new Windows.Foundation.Point(w, h));
            _line.Points = pts;
            _area.Points = samples.Length > 1 ? fill : new PointCollection();
        }
    }

    // ---------- your games ----------

    /// <summary>One chip per linked launcher (with its count), plus All.</summary>
    void FillChips()
    {
        _storeChips.Children.Clear();
        var owned = OwnedLibrary.Owned().ToList();
        var stores = OwnedLibrary.All.Keys.Where(s => OwnedLibrary.Linked(s) && s != GameStore.Xbox).OrderBy(s => s).ToList();
        if (_store != "all" && !stores.Any(s => s.ToString() == _store)) _store = "all";
        _storeChips.Children.Add(Chip("all", null, L.T("All"), owned.Count));
        foreach (var s in stores) _storeChips.Children.Add(Chip(s.ToString(), s, StoreLogos.Label(s), owned.Count(g => g.Store == s)));
    }

    ToggleButton Chip(string key, GameStore? store, string text, int count)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (store is { } s)
        {
            var logo = StoreLogos.Create(s, 14);
            logo.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(logo);
        }
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        var chip = new ToggleButton { Content = content, IsChecked = _store == key, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 5, 14, 6), Tag = key };
        chip.Click += (_, _) =>
        {
            _store = key;
            foreach (var c in _storeChips.Children.OfType<ToggleButton>()) c.IsChecked = (string)c.Tag == key;
            Restart();
        };
        return chip;
    }

    /// <summary>A new cover width: the layout's cells follow and the covers on screen are made again at the new size.</summary>
    void SizeTiles()
    {
        _tileCache.Clear();
        _tileOrder.Clear();
        _layout.MinItemWidth = _tile;
        _layout.MinItemHeight = Math.Round(_tile * 1.5) + 48;
        _items = [.. _items];
        _grid.ItemsSource = _items;
    }

    /// <summary>Makes the covers of the repeater as they come into view (and drops them as they leave it).</summary>
    sealed partial class TileFactory(DownloadsPage page) : IElementFactory
    {
        // Each cover sits in a cell tagged with its id, so one cover can be made again in place when its game changes.
        public UIElement GetElement(ElementFactoryGetArgs args) =>
            args.Data is string id && page._games.TryGetValue(id, out var g)
                ? new Border { Tag = id, Child = page.CachedTile(g) } : new Border();

        // The cell leaves the screen: its cover is let go (kept in the cache) so it can go into the next cell that shows it.
        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is Border cell) cell.Child = null;
        }
    }

    /// <summary>The game's cover from the cache when its state and size are the same, else made (and kept) now.</summary>
    FrameworkElement CachedTile(OwnedGame g)
    {
        var key = $"{_tile:0}|{TileState(g.Id)}|{Downloads.Find(g.Id)?.State}";
        if (_tileCache.TryGetValue(g.Id, out var hit) && hit.Key == key && hit.Tile.Parent is null)
        {
            _tileOrder.Remove(g.Id);
            _tileOrder.AddLast(g.Id);
            return hit.Tile;
        }
        var tile = OwnedLibrary.Tile(g, _tile, FillJobs);
        _tileCache[g.Id] = (key, tile);
        _tileOrder.Remove(g.Id);
        _tileOrder.AddLast(g.Id);
        while (_tileOrder.Count > TileCacheSize && _tileOrder.First is { } oldest)
        {
            _tileOrder.RemoveFirst();
            _tileCache.Remove(oldest.Value);
        }
        return tile;
    }

    void Fill()
    {
        var installed = OwnedLibrary.InstalledIds();
        var q = _search.Text.Trim();
        var games = OwnedLibrary.Owned()
            .Where(g => _store == "all" || g.Store.ToString() == _store)
            .Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .Where(g => _showIndex switch { 0 => !installed.Contains(g.Id), 1 => installed.Contains(g.Id), _ => true });
        games = _sort.SelectedIndex == 1 ? games.OrderBy(g => g.Store).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            : games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);
        var list = games.ToList();
        _gamesCount.Text = OwnedLibrary.IsLoaded ? list.Count.ToString() : "";
        // Unchanged list (a launcher re-read, a finished download elsewhere): the covers stay as they are.
        foreach (var g in list) _games[g.Id] = g;
        if (!list.Select(g => g.Id).SequenceEqual(_items, StringComparer.OrdinalIgnoreCase))
        {
            _items = list.Select(g => g.Id).ToList();
            _grid.ItemsSource = _items;
            _scroller.ChangeView(null, Math.Min(_scroller.VerticalOffset, _grid.ActualOffset.Y), null, true);
        }

        bool anyLinked = OwnedLibrary.Stores.Any(s => s != GameStore.Xbox && OwnedLibrary.Linked(s));
        _empty.Visibility = OwnedLibrary.IsLoaded && list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = !anyLinked ? L.T("No launcher is linked: open Accounts at the top right and turn on Steam, Epic Games or GOG.")
            : q.Length > 0 ? L.T("No game matches the search.")
            : _showIndex == 0 ? L.T("Every game you own is installed.")
            : L.T("No games to show.");
    }
}

/// <summary>
/// A thick, rounded progress bar: a soft track and a fill in the state's colour with a light sheen on top. Never thinner than
/// it is tall, so a few percent still reads as a pill. Indeterminate shows WinUI's moving bar instead.
/// </summary>
sealed partial class Meter : Grid
{
    readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    readonly ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    double _value;
    Ui.Tone? _tone;

    public Meter()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        Children.Add(_fill);
        Children.Add(_busy);
        SizeChanged += (_, e) =>
        {
            CornerRadius = _fill.CornerRadius = new CornerRadius(e.NewSize.Height / 2);
            Place();
        };
    }

    public void Set(double value, bool indeterminate, Ui.Tone tone)
    {
        _busy.Visibility = indeterminate ? Visibility.Visible : Visibility.Collapsed;
        _fill.Visibility = indeterminate ? Visibility.Collapsed : Visibility.Visible;
        if (tone != _tone)
        {
            _tone = tone;
            var c = Ui.ToneColor(tone);
            _fill.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Light(c, 0.35), Offset = 0 },
                    new GradientStop { Color = c, Offset = 0.55 },
                    new GradientStop { Color = Light(c, -0.15), Offset = 1 },
                },
            };
        }
        _value = Math.Clamp(value, 0, 1);
        Place();
    }

    void Place()
    {
        if (ActualWidth <= 0) return;
        double w = _value <= 0 ? 0 : Math.Max(ActualHeight, Math.Round(ActualWidth * _value));
        if (Math.Abs(_fill.Width - w) >= 0.5 || double.IsNaN(_fill.Width)) _fill.Width = w;
    }

    static Color Light(Color c, double amount)
    {
        byte F(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromArgb(c.A, F(c.R), F(c.G), F(c.B));
    }
}
