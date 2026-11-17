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

    /// <summary>
    /// The linked launchers' lists were read (true also when none is linked, or none answered). "Some list in" was the test
    /// before: with every launcher unlinked it never became true and the Library asked again on each redraw, in a loop
    /// (2.1-rc.3 audit). Linking or unlinking a launcher makes it false again, so the next look reads the lists anew.
    /// </summary>
    public static bool IsLoaded => _loaded;

    static bool _loaded;

    // Only the launchers Hybrid Launcher downloads from itself (1.4.0-rc.20): Xbox, Ubisoft Connect, EA app and Rockstar can only
    // install through their own app, and listed here they showed just the games already installed.
    public static readonly GameStore[] Stores = [GameStore.Steam, GameStore.Epic, GameStore.Gog];

    public static bool Linked(GameStore store) => store switch
    {
        GameStore.Steam => AppState.Settings.Accounts.Steam,
        GameStore.Epic => AppState.Settings.Accounts.Epic,
        GameStore.Gog => AppState.Settings.Accounts.Gog,
        GameStore.Ubisoft => AppState.Settings.Accounts.Ubisoft,
        GameStore.Ea => AppState.Settings.Accounts.Ea,
        GameStore.Rockstar => AppState.Settings.Accounts.Rockstar,
        _ => AppState.Settings.Accounts.Xbox,
    };

    public static void SetLinked(GameStore store, bool on)
    {
        var a = AppState.Settings.Accounts;
        if (Linked(store) != on) _loaded = false;
        switch (store)
        {
            case GameStore.Steam: a.Steam = on; break;
            case GameStore.Epic: a.Epic = on; break;
            case GameStore.Gog: a.Gog = on; break;
            case GameStore.Ubisoft: a.Ubisoft = on; break;
            case GameStore.Ea: a.Ea = on; break;
            case GameStore.Rockstar: a.Rockstar = on; break;
            default: a.Xbox = on; break;
        }
    }

    /// <summary>
    /// A launcher whose library cannot be read (Xbox, Ubisoft Connect, EA app, Rockstar): its installed games, as owned games
    /// with the cover the Library already found, so they show under its chip like any other launcher's.
    /// </summary>
    static async Task<OwnedList> InstalledOnlyAsync(OwnedList list)
    {
        var games = new List<OwnedGame>();
        foreach (var g in AppState.Games.Where(g => g.Store == list.Store && !g.Installing && !g.NotInstalled).ToList())
        {
            string? cover = null;
            try { cover = AppState.Settings.Games.TryGetValue(g.Id, out var gs) && gs.CustomCover is { } c && File.Exists(c) ? c : await HybridLauncher.Core.Media.GameInfo.GetCoverAsync(g); }
            catch { }
            games.Add(new OwnedGame(g.Store, g.Id, g.Title, cover, null, "", null));
        }
        return list with { Games = games };
    }

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
        for (int i = 0; i < results.Length; i++)
            if (OwnedGames.InstalledOnly(results[i].Store)) results[i] = await InstalledOnlyAsync(results[i]);
        if (TestOwnedCount() is int fake and > 0)
        {
            // Self-test only: a Steam library of that many games (issue 9 had 15,000), names and ids that match no real game.
            var games = Enumerable.Range(0, fake).Select(i => new OwnedGame(GameStore.Steam, $"steam:{90_000_000 + i}", $"Test game {i:00000}", null, null, "", null)).ToList();
            results = [.. results.Where(r => r.Store != GameStore.Steam), new OwnedList(GameStore.Steam, games, "test")];
        }
        Lists.Clear();
        foreach (var r in results) Lists[r.Store] = r;
        var byId = new Dictionary<string, OwnedGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
            foreach (var g in r.Games) byId.TryAdd(g.Id, g);
        _byId = byId;
        _version++;
        _loaded = true;
        Loaded?.Invoke();
    }

    /// <summary>HL_TEST_OWNED=&lt;count&gt;, honoured only with a scratch data folder (HL_DATA): the self-test's big library.</summary>
    static int? TestOwnedCount() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HL_DATA")) && int.TryParse(Environment.GetEnvironmentVariable("HL_TEST_OWNED"), out var n) ? Math.Clamp(n, 0, 50_000) : null;

    /// <summary>Every owned game of the linked launchers (hidden ones left out).</summary>
    public static IEnumerable<OwnedGame> Owned() =>
        Lists.Values.Where(l => Linked(l.Store)).SelectMany(l => l.Games).Where(g => !AppState.Settings.HiddenGames.Contains(g.Id));

    // Every owned game by id, and the lists' version (2.1.2: a Steam library of 15,000 games was searched one by one and sorted
    // by name again on every call, many times per redraw).
    static Dictionary<string, OwnedGame> _byId = new(StringComparer.OrdinalIgnoreCase);
    static int _version;
    static (int Version, string Key, List<OwnedGame> Games)? _sorted;

    public static OwnedGame? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Owned games of linked launchers that are not installed (nor hidden), name order.</summary>
    public static List<OwnedGame> NotInstalled()
    {
        // The name order is kept while the lists, the launchers linked and the hidden games stay the same.
        var hidden = AppState.Settings.HiddenGames;
        var hash = new HashCode();
        foreach (var id in hidden.Order(StringComparer.OrdinalIgnoreCase)) hash.Add(id, StringComparer.OrdinalIgnoreCase);
        var key = $"{string.Join(",", Lists.Keys.Where(Linked).Order())}|{hidden.Count}|{hash.ToHashCode()}|{System.Globalization.CultureInfo.CurrentCulture.Name}";
        if (_sorted is not { } s || s.Version != _version || s.Key != key)
        {
            s = (_version, key, Owned().OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
            _sorted = s;
        }
        var installed = InstalledIds();
        return s.Games.Where(g => !installed.Contains(g.Id)).ToList();
    }

    public static HashSet<string> InstalledIds() =>
        AppState.Games.Where(g => !g.Installing && !g.NotInstalled).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The page-only game of an owned one: Download in place of Play, store art and overview, no folder.</summary>
    public static Game AsGame(OwnedGame g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Store = g.Store,
        InstallDir = "",
        SteamAppId = g.Store == GameStore.Steam && g.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) ? g.Id[6..] : null,
        // The Epic link carries the game's namespace, where its achievements live (EpicStats.NamespaceOf).
        EpicLaunchUri = g is { Store: GameStore.Epic, Namespace: { } ns, CatalogItemId: { } cat } && g.Id.StartsWith("epic:", StringComparison.Ordinal)
            ? $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(cat)}%3A{Uri.EscapeDataString(g.Id[5..])}?action=launch&silent=true" : null,
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
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not open the launcher"), ex.Message, ex); }
    }

    /// <summary>
    /// A cover for a game you own, in the library's style: the art, the launcher's tile in a corner, and on hover a dark veil
    /// with Install (Open for installed games). A download in progress shows its bar over the bottom of the cover.
    /// </summary>
    // The covers whose picture is on screen (or that have none to load). A cover that left the screen before its picture came
    // in never got it (the image does not finish without being in the tree), stayed at its fade's start, and the Downloads
    // page's cache kept giving that empty tile back: every cover blank but its name (1.4.0-rc.20).
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> Drawn = new();
    static readonly object Done = new();

    /// <summary>Whether the tile made by <see cref="Tile"/> shows its picture (or has none): only then is it worth keeping.</summary>
    public static bool IsDrawn(FrameworkElement tile) =>
        tile is Panel { Children.Count: > 0 } p && p.Children[0] is FrameworkElement art && Drawn.TryGetValue(art, out _);

    // The parts of a cover that follow its width (the library's zoom).
    sealed class TileParts(Grid stage, Grid art)
    {
        /// <summary>Self-test: the cover as under the pointer (or back).</summary>
        public Action<bool>? Hover;
        /// <summary>The cover's right-click menu (2.3-rc.6: also from the controller's X and the menu key on a focused cell).</summary>
        public Action? Menu;
        public Grid Stage { get; } = stage;
        public Grid Art { get; } = art;
        // Install / Play at the cover's size: made again on a zoom (never scaled, so it stays sharp), as the library's.
        public Grid? Launch;
        public FrameworkElement? Chip;
        public double ChipHeight;
        public Func<double, FrameworkElement>? MakeChip;
        // The picture, decoded for the width it was made at: a zoom to a larger cover decodes it again (2.3-rc.2: it was
        // stretched from the small decode and looked squeezed and soft).
        public BitmapImage? Bitmap;
    }

    /// <summary>How wide a cover's picture is decoded for a cover <paramref name="width"/> wide (logical px; room for the pop).</summary>
    static int DecodeWidth(double width) => (int)Math.Min(720, Math.Ceiling(width * 1.6 / 80) * 80);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, TileParts> Parts = new();

    /// <summary>Self-test: a cover as under the pointer (or back).</summary>
    internal static void SelfTestHover(FrameworkElement tile, bool on) => Hover(tile, on);

    /// <summary>A cover as under the pointer (or back): its lift, outline and Install / Play pill. Also the focus's look.</summary>
    public static void Hover(FrameworkElement tile, bool on)
    {
        if (tile is StackPanel root && Parts.TryGetValue(root, out var parts)) parts.Hover?.Invoke(on);
    }

    /// <summary>Opens a cover's right-click menu, when it has one.</summary>
    public static bool ShowMenu(FrameworkElement tile)
    {
        if (tile is not StackPanel root || !Parts.TryGetValue(root, out var parts) || parts.Menu is not { } menu) return false;
        menu();
        return true;
    }

    /// <summary>A cover made by <see cref="Tile"/> at another width (the library's zoom): resized in place.</summary>
    public static void ResizeTile(FrameworkElement tile, double width)
    {
        if (tile is not StackPanel root || !Parts.TryGetValue(root, out var parts)) return;
        root.Width = width;
        parts.Art.Width = width;
        parts.Art.Height = Math.Round(width * 1.5);
        if (parts.Bitmap is { } bmp && DecodeWidth(width) is var want && want > bmp.DecodePixelWidth) bmp.DecodePixelWidth = want;
        if (parts is { Launch: { } launch, Chip: { } old, MakeChip: { } make } && Math.Clamp(Math.Round(width * 0.19), 36, 46) is var h && h != parts.ChipHeight)
        {
            parts.ChipHeight = h;
            launch.Children.Remove(old);
            parts.Chip = make(h);
            launch.Children.Add(parts.Chip);
            if (launch.Children[0] is Ellipse halo) halo.Margin = new Thickness(-h * 1.1, -h * 0.9, -h * 1.1, -h * 0.9);
        }
    }

    static SolidColorBrush? _chipFill;
    // The library covers' chip (LibraryPage.CoverTile): near-black, no glass, soft corners.
    static SolidColorBrush ChipFill => _chipFill ??= new(Color.FromArgb(0xE0, 0x0E, 0x0E, 0x12));

    /// <summary>A chip on the cover as the library draws its tags: a small white mark (and a word) on the dark fill.</summary>
    static Border CoverChip(FrameworkElement mark, string? text = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(mark);
        if (text is not null)
            row.Children.Add(new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Center });
        return new Border { Background = ChipFill, CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 3, 5, 3), Child = row, IsHitTestVisible = false };
    }

    /// <param name="withMenu">False when the caller gives the cover its own right-click menu (the library's): one menu, not two.</param>
    /// <param name="cellTakesFocus">The cover's cell takes the keyboard and controller focus (Downloads, 2.3-rc.6): the pill in
    /// the middle is then left out of Tab and the D-pad, so the focus moves from cover to cover and A opens the game's page.</param>
    public static FrameworkElement Tile(OwnedGame game, double width, Action? started = null, bool withMenu = true, bool cellTakesFocus = false)
    {
        var installedGame = AppState.Find(game.Id) is { Installing: false, NotInstalled: false } g ? g : null;
        // 2.1: the library's cover (LibraryPage.CoverTile) for games owned but not installed too: the picture with its light
        // from above and deeper foot, the launcher's mark and Installed as the library's dark chips, the pop under the pointer
        // with Install (or Play) as the library's pill in the middle, in place of the grey veil and the coloured launcher tile.
        var art = new Grid { Width = width, Height = Math.Round(width * 1.5), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var pic = new Grid();
        art.Children.Add(pic);
        pic.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["ControlFillColorSecondaryBrush"], IsHitTestVisible = false });
        pic.Children.Add(new TextBlock { Text = game.Name, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12), Opacity = 0.7, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false });
        var stage = new Grid();
        // The user's own cover first (Edit artwork, 2.1-rc.10 audit: the cache knew it, the cover did not), else the launcher's
        // cover from the disk (2.1: OwnedCovers downloads it once, with fallbacks; straight from the web, one failed request
        // left the cover empty).
        var own = HybridLauncher.Core.Media.ArtOverrides.Get(game.Id, HybridLauncher.Core.Media.ArtKind.Cover)
            ?? HybridLauncher.Core.Media.OwnedCovers.KnownPath(game.Id);
        var bmp = new BitmapImage { DecodePixelWidth = DecodeWidth(width), DecodePixelType = DecodePixelType.Logical };
        if (own is not null) bmp.UriSource = new Uri(own);
        else
        {
            var ui = art.DispatcherQueue;
            _ = HybridLauncher.Core.Media.OwnedCovers.GetAsync(game).ContinueWith(t =>
            {
                // No picture anywhere: the name stays on the cover, and the cover is kept as it is (not made again and again).
                var file = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                ui.TryEnqueue(() =>
                {
                    // The window may be gone (shutdown): a late cover is dropped, never a crash.
                    try { if (file is not null) bmp.UriSource = new Uri(file); else Drawn.AddOrUpdate(stage, Done); }
                    catch (Exception ex) { Log.Warn($"Cover late: {ex.Message}"); }
                });
            }, TaskScheduler.Default);
        }
        pic.Children.Add(Smooth.Art(bmp, new CornerRadius(10), fadeMs: 200,
            opened: () => Drawn.AddOrUpdate(stage, Done),
            failed: () => Drawn.AddOrUpdate(stage, Done)));
        // 2.3-rc.3 (the user): no light or shade laid over the picture here, and no zoom: the cover as it is.
        // 2.3-rc.4: the cell's height holds the cover AND its two text lines (58 px, see MinItemHeight): too short, the tile's root was clipped to
        // its slot and clipped the lifted cover and the outline with it (top corners and sides cut).

        // The launcher's mark top left and Installed top right, as the library's tags.
        var top = new Grid { Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        var mark = StoreLogos.Create(game.Store, 12);
        if (mark is Viewbox { Child: PathIcon pi }) pi.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        var storeChip = CoverChip(mark);
        storeChip.HorizontalAlignment = HorizontalAlignment.Left;
        top.Children.Add(storeChip);
        if (installedGame is not null)
        {
            var check = CoverChip(new FontIcon { Glyph = "\uE73E", FontSize = 10, Foreground = new SolidColorBrush(Ui.ToneColor(Ui.Tone.Good)) }, L.T("Installed"));
            check.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(check);
        }
        art.Children.Add(top);

        var job = Downloads.Find(game.Id);
        bool active = job is { State: DownloadState.Downloading or DownloadState.Waiting or DownloadState.Paused };
        if (active)
        {
            // The download's bar along the bottom of the cover.
            var shade = new Border { Height = 70, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Background = BottomShade(), CornerRadius = new CornerRadius(0, 0, 10, 10) };
            var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 10), Spacing = 6, IsHitTestVisible = false };
            var label = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis };
            var bar = new ProgressBar { Maximum = 1 };
            bottom.Children.Add(label);
            bottom.Children.Add(bar);
            pic.Children.Add(shade);
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

        // Under the pointer (2.3-rc.3, the user): only a thin blue outline round the cover and a very light bump up, in place of
        // the library's pop (zoom, sweep of light, accent glow, contact shadow).
        var outline = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Margin = new Thickness(-2),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0x9A, 0xFF)),
            Opacity = 0, IsHitTestVisible = false,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) },
        };
        art.Children.Add(outline);
        stage.Children.Add(art);
        void Bump(bool on)
        {
            var c = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
            var spring = c.CreateSpringVector3Animation();
            spring.Target = "Translation";
            // As the library's lift (CoverFx.Lift): the whole cover, with its tags, bar and outline, moves up as one.
            spring.FinalValue = new System.Numerics.Vector3(0, on ? -5 : 0, 0);
            spring.DampingRatio = 0.6f;
            spring.Period = TimeSpan.FromMilliseconds(55);
            art.StartAnimation(spring);
        }

        // Install (or Play, installed) in the middle as the library's pill, on its dark halo; it comes up under the pointer.
        Grid? launch = null;
        FrameworkElement? chip = null;
        double h = Math.Clamp(Math.Round(width * 0.19), 36, 46);
        FrameworkElement MakeChip(double size)
        {
            var c = installedGame is not null
                ? new Controls.LaunchChip(installedGame, withLabel: true, size: size)
                : new Controls.LaunchChip(AsGame(game), withLabel: true, size: size, install: () => Install(game, started));
            c.HorizontalAlignment = HorizontalAlignment.Center;
            c.VerticalAlignment = VerticalAlignment.Center;
            if (cellTakesFocus) c.IsTabStop = false;
            // Keyboard: the pill stays reachable with Tab and shows while it has focus.
            c.GotFocus += (_, _) => Show(true);
            c.LostFocus += (_, _) => Show(false);
            return c;
        }
        if (!active)
        {
            chip = MakeChip(h);
            launch = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0, IsHitTestVisible = false,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
            };
            launch.Children.Add(chip);
            art.Children.Add(launch);
        }
        bool over = false;
        void Show(bool on)
        {
            if (launch is null) return;
            launch.Opacity = on ? 1 : 0;
            // Hidden, it takes no click (2.1-rc.10 audit: a hidden button caught clicks on the middle of the cover).
            launch.IsHitTestVisible = on;
        }
        void Pop(bool on)
        {
            if (on == over) return;
            over = on;
            Show(on);
            try
            {
                // Over the next covers while it is grown: the repeater's element is the one the layout orders.
                DependencyObject? up = stage;
                while (up is not null && VisualTreeHelper.GetParent(up) is { } parent && parent is not ItemsRepeater) up = parent;
                if (up is UIElement item && VisualTreeHelper.GetParent(up) is ItemsRepeater) Canvas.SetZIndex(item, on ? 10 : 0);
                outline.Opacity = on ? 1 : 0;
                Bump(on);
            }
            catch (Exception ex) { Log.Warn($"Cover pop: {ex.Message}"); }
        }
        art.PointerEntered += (_, _) => Pop(true);
        art.PointerExited += (_, _) => Pop(false);
        art.PointerCanceled += (_, _) => Pop(false);
        art.PointerCaptureLost += (_, _) => Pop(false);
        stage.Unloaded += (_, _) => { if (over) Pop(false); };

        var root = new StackPanel { Width = width, Tag = game.Id };
        root.Children.Add(stage);
        Parts.AddOrUpdate(root, new TileParts(stage, art) { Hover = Pop, Launch = launch, Chip = chip, ChipHeight = h, MakeChip = launch is null ? null : MakeChip, Bitmap = bmp });
        // A click on the cover (not on its button) opens the game's page, like the library.
        art.Tapped += (_, e) =>
        {
            for (var el = e.OriginalSource as DependencyObject; el is not null && el != art; el = VisualTreeHelper.GetParent(el))
                if (el is ButtonBase) return;
            if (over) Pop(false);
            Open(game);
        };
        root.Children.Add(new TextBlock { Text = game.Name, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(2, 8, 2, 0), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["BodyStrongTextBlockStyle"] });
        root.Children.Add(new TextBlock { Text = StoreLogos.Label(game.Store), Margin = new Thickness(2, 0, 2, 2), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"] });

        // The menu is made on the first right-click: hundreds of covers do not each carry one they may never show.
        void ShowMenu(Microsoft.UI.Xaml.Input.ContextRequestedEventArgs? e)
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
                // Gone from the side list and the library too, not only from here.
                App.Window?.RefreshList();
                App.Window?.RefreshLibrary();
            };
            menu.Items.Add(hide);
            if (e is not null && e.TryGetPosition(root, out var at)) menu.ShowAt(root, at);
            else menu.ShowAt(root, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
            if (e is not null) e.Handled = true;
        }
        if (withMenu)
        {
            root.ContextRequested += (_, e) => ShowMenu(e);
            if (Parts.TryGetValue(root, out var tp)) tp.Menu = () => ShowMenu(null);
        }
        return root;
    }

    /// <summary>A download's state in a few words, the same on the covers, the Downloads tab and the game's page.</summary>
    public static string StateText(DownloadJob j) => j.CancelRequested && j.State != DownloadState.Failed ? L.T("Cancelling…") : j.State switch
    {
        DownloadState.Downloading when j.Checking => L.F("Checking files ({0} of {1})", j.Checked, j.CheckTotal),
        DownloadState.Downloading when j.IsVerify => L.T("Repairing"),
        DownloadState.Downloading when j.IsUpdate => L.T("Updating"),
        DownloadState.Downloading => L.T("Downloading"),
        DownloadState.Waiting when j.Queued => L.T("Queued"),
        DownloadState.Waiting when j.Direct && j.Error is not null => L.T("Reconnecting"),
        DownloadState.Waiting when j.Direct && j.IsVerify => L.T("Reading the file list"),
        DownloadState.Waiting when j.Direct && j.IsUpdate => L.T("Preparing the update"),
        DownloadState.Waiting when j.Direct => L.T("Preparing"),
        DownloadState.Waiting => L.T("Waiting for the launcher"),
        DownloadState.Paused when j.Direct => j.Error is null ? L.T("Paused") : L.T("Stopped by an error"),
        DownloadState.Paused => L.T("Paused or queued"),
        DownloadState.Done when j.IsVerify => j.Repaired > 0 ? L.N(j.Repaired, "Verified: {0} file repaired", "Verified: {0} files repaired") : L.T("Verified: every file is fine"),
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
    // A sign-in or sign-out of a launcher is running (one at a time).
    bool _signingIn;

    const double MinTile = 150, MaxTile = 230, Gap = 16;
    // Room around each cover for its pop under the pointer (2.3): the cell was the cover's size, so the grown cover was cut by
    // its cell (a zoom inside the frame instead of the library's pop). The gap between covers is now this room.
    const double PopX = Gap / 2, PopTop = 14, PopBottom = 2;

    // Left-aligned with a margin worked out in Layout (2.2): centred, the ScrollView placed the column at half a pixel on some
    // window widths, and every text, icon and cover on the page was drawn soft.
    readonly StackPanel _root = new() { Spacing = 22, Padding = new Thickness(0, 8, 0, 40), HorizontalAlignment = HorizontalAlignment.Left };
    readonly ScrollView _scroller;
    readonly TextBlock _count = Ui.Muted("");
    readonly StackPanel _active = new() { Spacing = 10 };
    // The covers: virtualized, so only the ones on screen (and a little around) exist; hundreds of games stay smooth.
    // The repeater gets plain game ids: a list of strings crosses into WinUI in a trimmed build, a collection of Hybrid Launcher's own
    // records does not (0.17: the page could not open at all). Each id's game is looked up in _games.
    List<string> _items = [];
    readonly Dictionary<string, OwnedGame> _games = new(StringComparer.OrdinalIgnoreCase);
    readonly UniformGridLayout _layout = new() { MinColumnSpacing = 0, MinRowSpacing = Gap - PopTop - PopBottom, ItemsJustification = UniformGridLayoutItemsJustification.Start };
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

    // 2.2: your games are closed by default. CollapsedCards holds the cards people closed; this key in it means the opposite,
    // that the games were opened (a new key, so the 2.1.4 "downloads.games" of people who never closed them reads as closed).
    const string GamesOpenKey = "downloads.games.open";
    bool _gamesOpen;
    bool _gamesFilled;
    readonly StackPanel _gamesBody = new() { Spacing = 14 };
    readonly TextBlock _gamesSummary = new() { FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel _gamesLogos = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _gamesToggle = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    FrameworkElement? _gamesChevron;
    Border? _gamesPill;
    // The library's figures, counted when the lists change (not at every step of a download: 15,000 games each time).
    (int Owned, int Installed, int Launchers) _stats;
    bool _accountsDirty = true;
    // The line of the "nothing downloading" card that says when updates were last checked (made again with the card).
    TextBlock? _idleStatus;
    // Completed: the last few, all of them after Show all.
    bool _showAllDone;
    const int DoneShown = 3;
    // Check for updates: its label and the ring that takes the icon's place while a check runs.
    readonly Button _check = new() { Padding = new Thickness(12, 6, 14, 7), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _checkText = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly FontIcon _checkIcon = new() { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly ProgressRing _checkRing = new() { IsActive = false, Width = 14, Height = 14, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };

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
        // 2.2: the accounts' rows are made when the menu opens, not on every visit and every launcher re-read.
        _accountsFlyout.Opening += (_, _) => { if (_accountsDirty) FillAccountsPanel(); };
        // 2.2: Check for updates in the header, a ring in its place while a check runs (also the automatic ones).
        var checkContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        checkContent.Children.Add(_checkIcon);
        checkContent.Children.Add(_checkRing);
        checkContent.Children.Add(_checkText);
        _check.Content = checkContent;
        _check.Click += async (_, _) =>
        {
            await GameUpdater.CheckNowAsync(evenIfOff: true, start: AppState.Settings.AutoUpdateGames);
            UpdateCheck();
        };
        ToolTipService.SetToolTip(_check, L.T("Look for new versions of your installed games"));
        actions.Children.Add(_check);
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
        _grid = new ItemsRepeater { Layout = _layout, ItemTemplate = new TileFactory(this), VerticalCacheLength = 4, Margin = new Thickness(-PopX, -PopTop, -PopX, 0) };
        _layout.MinItemWidth = _tile + 2 * PopX;
        _layout.MinItemHeight = Math.Round(_tile * 1.5) + 58 + PopTop + PopBottom;

        // One toolbar: what to show and the launchers on the left, search and order on the right (wrapping on narrow windows).
        _search.Width = 240;
        var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sortRow.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        sortRow.Children.Add(_sort);
        var filters = new WrapPanel(10, [showBox, _storeChips, _search, sortRow]);
        foreach (var f in filters.Children.OfType<FrameworkElement>()) f.VerticalAlignment = VerticalAlignment.Center;
        var toolbar = new Border { Style = Ui.Style("RowStyle"), MinHeight = 0, Padding = new Thickness(12, 10, 12, 10), Child = filters };

        // 2.2: your games sit behind one wide bar, closed by default: Downloads opens on what is downloading, and the covers
        // (thousands for a big Steam library) are listed and made only once the bar is opened. Opened, it stays open.
        var library = new StackPanel { Spacing = 14 };
        _gamesOpen = AppState.Settings.CollapsedCards.Contains(GamesOpenKey);
        _gamesBody.Children.Add(toolbar);
        _gamesBody.Children.Add(_ring);
        _gamesBody.Children.Add(_empty);
        _gamesBody.Children.Add(_grid);
        _gamesBody.Visibility = _gamesOpen ? Visibility.Visible : Visibility.Collapsed;
        library.Children.Add(GamesBar());
        library.Children.Add(_gamesBody);

        _root.Children.Add(head);
        _root.Children.Add(_active);
        _root.Children.Add(library);
        _scroller = new ScrollView { Content = _root, HorizontalScrollMode = ScrollingScrollMode.Disabled, VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto, ContentOrientation = ScrollingContentOrientation.Vertical };
        _scroller.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        CompositorWheel.Attach(_scroller);
        Content = _scroller;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        using var __perf = UiPerf.Scope("DownloadsPage.NavigatedTo");
        base.OnNavigatedTo(e);
        Downloads.Changed += OnJobsChanged;
        GameUpdater.Changed += OnJobsChanged;
        OwnedLibrary.Loaded += OnLoaded;
        AppState.GamesChanged += OnLoaded;
        Downloads.Watch();
        FillJobs();
        _ = ReloadAsync(false);
        // 2.1.4: updates checked when Downloads opens (if the last check is a few minutes old), in the background.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = GameUpdater.CheckIfStaleAsync());
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Downloads.Changed -= OnJobsChanged;
        GameUpdater.Changed -= OnJobsChanged;
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
        // Centred by hand, on a whole screen pixel (display scale and Interface size included).
        double scale = (XamlRoot?.RasterizationScale is > 0 and var r ? r : 1) * UiScale.Factor;
        double left = Math.Floor(Math.Max(0, width - _root.Width) / 2 * scale) / scale;
        if (Math.Abs(_root.Margin.Left - left) > 0.01) _root.Margin = new Thickness(left, 0, 0, 0);
        if (Math.Abs(tile - _tile) < 0.5) return;
        _tile = tile;
        SizeTiles();
    }

    void OnJobsChanged()
    {
        FillJobs();
        UpdateCheck();
        App.Window?.UpdateDownloadsBadge();
    }

    void OnLoaded()
    {
        FillAccounts();
        CountLibrary();
        // A finished download's game is in the list now: its line gets its Play button.
        FillJobs();
        // Closed, the games are not listed at all (2.2): opening the bar lists them.
        if (_gamesOpen)
        {
            FillChips();
            Fill();
            _gamesFilled = true;
        }
        else _gamesFilled = false;
        UpdateGamesBar();
        UpdateCount();
        UpdateCheck();
    }

    void Restart() { if (_gamesOpen) Fill(); }

    async Task ReloadAsync(bool force)
    {
        _ring.Visibility = Visibility.Visible;
        FillAccounts();
        // A finished download's game is in the list now: its line gets its Play button.
        FillJobs();
        UpdateCheck();
        try { await OwnedLibrary.LoadAsync(force); }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not read the launcher libraries"), ex.Message, ex); }
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

    /// <summary>The library's figures, counted once per change of the lists or of the installed games.</summary>
    void CountLibrary()
    {
        if (!OwnedLibrary.IsLoaded) { _stats = default; return; }
        var installedIds = OwnedLibrary.InstalledIds();
        int owned = 0, installed = 0;
        foreach (var g in OwnedLibrary.Owned())
        {
            owned++;
            if (installedIds.Contains(g.Id)) installed++;
        }
        int launchers = OwnedLibrary.All.Keys.Count(s => OwnedLibrary.Linked(s) && (!OwnedGames.InstalledOnly(s) || OwnedLibrary.All[s].Games.Count > 0));
        _stats = (owned, installed, launchers);
    }

    void UpdateCount()
    {
        // 2.2: what is going on first (the games' own figures are on the Your games bar).
        int active = Downloads.ActiveCount, updates = GameUpdater.Available.Count;
        var parts = new List<string>();
        if (active > 0) parts.Add(L.N(active, "{0} download", "{0} downloads"));
        if (updates > 0) parts.Add(L.N(updates, "{0} update available", "{0} updates available"));
        if (parts.Count == 0) parts.Add(!OwnedLibrary.IsLoaded ? L.T("Reading your launchers…") : L.F("{0} games on {1} launchers", _stats.Owned, _stats.Launchers));
        var text = string.Join("  ·  ", parts);
        if (_count.Text != text) _count.Text = text;
    }

    /// <summary>Check for updates in the header: shown when something can be checked, a ring while a check runs.</summary>
    void UpdateCheck()
    {
        _check.Visibility = GameUpdater.CanCheck ? Visibility.Visible : Visibility.Collapsed;
        bool busy = GameUpdater.Checking;
        _check.IsEnabled = !busy;
        _checkRing.IsActive = busy;
        _checkRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _checkIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _checkText.Text = busy ? L.T("Checking…") : L.T("Check for updates");
        if (_idleStatus is not null) _idleStatus.Text = IdleText();
    }

    static string IdleText()
    {
        if (GameUpdater.Checking) return L.T("Checking your games for updates…");
        if (!GameUpdater.CanCheck) return L.T("Games you install show up here while they download.");
        if (GameUpdater.LastChecked is { } at) return L.F("Your games are up to date. Last checked at {0}.", at.ToLocalTime().ToString("t"));
        return L.T("Games you install or update show up here while they download.");
    }

    // ---------- your games: the bar that opens them ----------

    /// <summary>
    /// The wide bar in front of your games: an icon, the title, how many there are and on which launchers, and a clear
    /// Show / Hide with its arrow. The whole bar is the button.
    /// </summary>
    FrameworkElement GamesBar()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var accent = Ui.ToneColor(Ui.Tone.Accent);
        var icon = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)),
            Child = new FontIcon { Glyph = "", FontSize = 20, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush") },
        };
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = L.T("Your games"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        text.Children.Add(_gamesSummary);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(_gamesLogos, 2);
        grid.Children.Add(_gamesLogos);
        var chevron = new FontIcon { Glyph = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new RotateTransform() };
        _gamesChevron = chevron;
        var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toggleRow.Children.Add(_gamesToggle);
        toggleRow.Children.Add(chevron);
        var pill = new Border { CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 6, 12, 7), Child = toggleRow, VerticalAlignment = VerticalAlignment.Center };
        _gamesPill = pill;
        Grid.SetColumn(pill, 3);
        grid.Children.Add(pill);
        var bar = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(12),
            Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, L.T("Your games"));
        // 2.3-rc.6: down from the bar is the toolbar under it (the D-pad went straight to the right-hand cover past it).
        if (_show.Children.FirstOrDefault() is UIElement firstShow) bar.XYFocusDown = firstShow;
        bar.Click += (_, _) =>
        {
            SetGamesOpen(!_gamesOpen);
            // Opened, the bar goes to the top so the covers fill the screen under it.
            // On a whole screen pixel: a fractional offset drew the page soft.
            if (_gamesOpen && XamlRoot is { } xr)
            {
                double scale = xr.RasterizationScale * UiScale.Factor;
                double y = bar.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y - 12;
                y = Math.Round(Math.Max(0, y) * scale) / scale;
                _scroller.ScrollTo(0, y, new ScrollingScrollOptions(ScrollingAnimationMode.Enabled));
            }
        };
        _gamesBody.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        UpdateGamesBar();
        return bar;
    }

    /// <summary>Self-test: opens or closes your games as the bar's click does, and says whether covers are listed when open.</summary>
    internal bool SelfTestHover(bool on)
    {
        if (_grid.TryGetElement(0) is not CoverCell { Child: FrameworkElement tile }) return false;
        OwnedLibrary.SelfTestHover(tile, on);
        return true;
    }

    /// <summary>Self-test: the focus is on one of the covers of your games.</summary>
    /// <summary>
    /// The controller (or an arrow key) on a cover (2.3-rc.6): the covers are walked as a grid, cover by cover and row by row,
    /// scrolling the next one into view; WinUI's own move lost the focus at a row's end and could not reach covers not made yet.
    /// Up from the first row and Left from the first column are left to WinUI (the toolbar, the pane). True when handled.
    /// </summary>
    internal bool PadMove(CoverCell cell, Microsoft.UI.Xaml.Input.FocusNavigationDirection direction)
    {
        int i = _grid.GetElementIndex(cell);
        if (i < 0 || _items.Count == 0) return false;
        double itemWidth = _layout.MinItemWidth + _layout.MinColumnSpacing;
        int cols = Math.Max(1, (int)Math.Floor((_grid.ActualWidth + _layout.MinColumnSpacing + 0.5) / itemWidth));
        int col = i % cols;
        int next = direction switch
        {
            Microsoft.UI.Xaml.Input.FocusNavigationDirection.Left => col == 0 ? -1 : i - 1,
            Microsoft.UI.Xaml.Input.FocusNavigationDirection.Right => col == cols - 1 || i + 1 >= _items.Count ? i : i + 1,
            Microsoft.UI.Xaml.Input.FocusNavigationDirection.Up => i - cols,
            // Down from a row with no full row under it lands on the last cover.
            _ => i + cols < _items.Count ? i + cols : (i / cols < (_items.Count - 1) / cols ? _items.Count - 1 : i),
        };
        if (next < 0) return false;
        if (next == i) return true;
        if (_grid.GetOrCreateElement(next) is not CoverCell target) return false;
        target.UpdateLayout();
        target.Focus(FocusState.Keyboard);
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.5 });
        return true;
    }

    /// The cell itself, never the Install pill inside it: A there would start a real install.
    internal bool SelfTestFocusOnCover() =>
        XamlRoot is not null && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is CoverCell cell && VisualTreeHelper.GetParent(cell) == _grid;

    internal bool SelfTestGames(bool open)
    {
        if (_gamesOpen != open) SetGamesOpen(open);
        // The column sits on whole screen pixels (2.2: half a pixel drew the whole page soft).
        double scale = (XamlRoot?.RasterizationScale ?? 1) * UiScale.Factor;
        var at = _root.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        if (Math.Abs(at.X * scale - Math.Round(at.X * scale)) > 0.02)
            throw new InvalidOperationException($"Downloads column at x {at.X * scale:0.00} px, not on a whole pixel");
        return open ? _gamesBody.Visibility == Visibility.Visible : _gamesBody.Visibility == Visibility.Collapsed;
    }

    void SetGamesOpen(bool open)
    {
        using var __perf = UiPerf.Scope("DownloadsPage.Games");
        _gamesOpen = open;
        if (open) AppState.Settings.CollapsedCards.Add(GamesOpenKey); else AppState.Settings.CollapsedCards.Remove(GamesOpenKey);
        SettingsStore.Save();
        if (open && !_gamesFilled && OwnedLibrary.IsLoaded)
        {
            FillChips();
            Fill();
            _gamesFilled = true;
        }
        if (open)
        {
            _gamesBody.Opacity = 0;
            _gamesBody.Visibility = Visibility.Visible;
            DispatcherQueue.TryEnqueue(() => _gamesBody.Opacity = 1);
        }
        else _gamesBody.Visibility = Visibility.Collapsed;
        UpdateGamesBar();
    }

    void UpdateGamesBar()
    {
        // Closed, the pill is in the accent colour and says what it does; open, it goes quiet.
        _gamesToggle.Text = _gamesOpen ? L.T("Hide games") : L.T("Show games");
        if (_gamesPill is not null)
        {
            var accent = Ui.ToneColor(Ui.Tone.Accent);
            _gamesPill.Background = _gamesOpen ? Ui.Brush("ControlFillColorSecondaryBrush") : new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
        }
        if (_gamesChevron?.RenderTransform is RotateTransform r) r.Angle = _gamesOpen ? 180 : 0;
        string summary;
        if (!OwnedLibrary.IsLoaded) summary = L.T("Reading your launchers…");
        else if (!OwnedLibrary.Stores.Any(OwnedLibrary.Linked)) summary = L.T("No launcher is linked yet: open Accounts at the top right.");
        else
        {
            int notInstalled = _stats.Owned - _stats.Installed;
            summary = L.N(_stats.Owned, "{0} game you own", "{0} games you own") + "  ·  " + L.F("{0} installed", _stats.Installed)
                + "  ·  " + L.F("{0} ready to install", notInstalled);
        }
        if (_gamesSummary.Text != summary) _gamesSummary.Text = summary;
        _gamesLogos.Children.Clear();
        foreach (var s in OwnedLibrary.All.Keys.Where(OwnedLibrary.Linked).OrderBy(s => s))
        {
            var logo = StoreLogos.Create(s, 16);
            logo.Opacity = 0.8;
            ToolTipService.SetToolTip(logo, StoreLogos.Label(s));
            _gamesLogos.Children.Add(logo);
        }
    }

    // ---------- accounts (the header's menu) ----------

    /// <summary>The header's launcher marks now; the menu's rows when it opens (or now, if it is open).</summary>
    void FillAccounts()
    {
        if (_accountsFlyout?.IsOpen == true) { FillAccountsPanel(); return; }
        _accountsDirty = true;
        var key = string.Join(",", OwnedLibrary.Stores.Where(OwnedLibrary.Linked));
        if (key == _logosKey && _accountLogos.Children.Count > 0) return;
        _logosKey = key;
        _accountLogos.Children.Clear();
        foreach (var store in OwnedLibrary.Stores.Where(OwnedLibrary.Linked))
        {
            var mini = StoreLogos.Tile(store, size: 20);
            mini.BorderBrush = Ui.Brush("SolidBackgroundFillColorBaseBrush");
            mini.BorderThickness = new Thickness(1.5);
            _accountLogos.Children.Add(mini);
        }
        if (_accountLogos.Children.Count == 0) _accountLogos.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
    }

    string _logosKey = "";

    void FillAccountsPanel()
    {
        _accountsDirty = false;
        _logosKey = string.Join(",", OwnedLibrary.Stores.Where(OwnedLibrary.Linked));
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
                : OwnedGames.InstalledOnly(store) ? L.F("{0} installed", installed) + (list.Problem is { } xp ? $". {xp}" : "")
                : L.F("{0} owned, {1} installed", list.Games.Count, installed) + $"  ·  {list.Source}" + (list.Problem is { } p ? $"  ·  {p}" : "");
            text.Children.Add(new TextBlock { Text = detail, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-8, 2, 0, 0) };
            if (!OwnedGames.InstalledOnly(store))
            {
                // Signed in, Hybrid Launcher lists the whole library and downloads and uninstalls games itself, with no launcher.
                bool signedIn = OwnedGames.Direct(store);
                var sign = new HyperlinkButton { Content = signedIn ? L.T("Sign out") : L.T("Sign in to download directly"), Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
                ToolTipService.SetToolTip(sign, signedIn
                    ? L.T("Hybrid Launcher forgets this account's sign-in. Games it installed stay.")
                    : L.F("Sign in to {0} in Hybrid Launcher: every game you own is listed, and Hybrid Launcher downloads and uninstalls them itself, without the launcher.", StoreLogos.Label(store)));
                sign.Click += async (_, _) =>
                {
                    // One sign-in at a time (2.1-rc.10 audit: a second click opened a second dialog).
                    if (_signingIn) return;
                    _signingIn = true;
                    sign.IsEnabled = false;
                    try
                    {
                        _accountsFlyout?.Hide();
                        if (signedIn)
                        {
                            AccountSignIn.SignOut(store);
                            App.Window?.RefreshList();
                            App.Window?.RefreshLibrary();
                        }
                        else if (await AccountSignIn.RunAsync(store, XamlRoot, ActualTheme) is null) return;
                        await ReloadAsync(true);
                    }
                    finally
                    {
                        _signingIn = false;
                        sign.IsEnabled = true;
                    }
                };
                links.Children.Add(sign);
                if (signedIn) text.Children.Insert(1, new TextBlock { Text = L.T("Signed in: Hybrid Launcher downloads and uninstalls these games itself."), Style = Ui.Style("RowDescription"), Foreground = Ui.Brush("SystemFillColorSuccessBrush") });
            }
            var (appLabel, appUri) = store switch
            {
                GameStore.Xbox => (L.T("Open the Xbox app"), "msxbox://"),
                GameStore.Ubisoft => (L.T("Open Ubisoft Connect"), "uplay://"),
                GameStore.Ea => (L.T("Open the EA app"), "link2ea://"),
                _ => ("", ""),
            };
            if (linked && appUri.Length > 0)
            {
                var app = new HyperlinkButton { Content = appLabel, Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
                app.Click += (_, _) => Ui.Open(appUri);
                links.Children.Add(app);
            }
            if (links.Children.Count > 0) text.Children.Add(links);
            var toggle = new ToggleSwitch { IsOn = linked, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(toggle, linked ? L.T("Linked") : L.T("Off"));
            toggle.Toggled += (_, _) =>
            {
                OwnedLibrary.SetLinked(store, toggle.IsOn);
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
        var pending = GameUpdater.Available;
        var key = string.Join("|", live.Select(j => j.Id)) + "#" + string.Join("|", done.Select(j => j.Id + j.State)) + "#" + string.Join("|", pending.Select(p => p.Id + p.Latest)) + (_showAllDone ? "#all" : "");
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
            // 2.1.4: updates found and not started (automatic updates off, or the game was running).
            if (pending.Count > 0)
            {
                var head = new Grid { Margin = new Thickness(0, live.Count > 0 ? 10 : 0, 0, 0) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(SectionLabel("", L.T("UPDATES AVAILABLE"), pending.Count.ToString()));
                if (pending.Count > 1)
                {
                    var all = new HyperlinkButton { Content = L.T("Update all"), Padding = new Thickness(8, 2, 8, 2) };
                    all.Click += (_, _) => GameUpdater.StartAll();
                    Grid.SetColumn(all, 1);
                    head.Children.Add(all);
                }
                _active.Children.Add(head);
                foreach (var p in pending) _active.Children.Add(PendingRow(p));
            }
            if (done.Count > 0)
            {
                var row = new Grid { Margin = new Thickness(0, live.Count > 0 || pending.Count > 0 ? 10 : 0, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(SectionLabel("", L.T("COMPLETED"), done.Count.ToString()));
                var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                // 2.2: the newest few; the rest behind Show all (a long list pushed your games far down).
                if (done.Count > DoneShown)
                {
                    var more = new HyperlinkButton { Content = _showAllDone ? L.T("Show fewer") : L.F("Show all {0}", done.Count), Padding = new Thickness(8, 2, 8, 2) };
                    more.Click += (_, _) => { _showAllDone = !_showAllDone; FillJobs(); };
                    links.Children.Add(more);
                }
                var clear = new HyperlinkButton { Content = L.T("Clear all"), Padding = new Thickness(8, 2, 8, 2) };
                clear.Click += (_, _) => Downloads.ClearFinished();
                links.Children.Add(clear);
                Grid.SetColumn(links, 1);
                row.Children.Add(links);
                _active.Children.Add(row);
                var newest = Enumerable.Reverse(done);
                foreach (var j in _showAllDone ? newest : newest.Take(DoneShown))
                {
                    var v = new JobView(j, hero: false);
                    _views[j.Id] = v;
                    _active.Children.Add(v.Root);
                }
            }
            // 2.2: nothing downloading, queued, to update or finished: one calm card in place of an empty page top, saying
            // when your games were last checked (Check for updates is in the header).
            _idleStatus = null;
            if (_active.Children.Count == 0) _active.Children.Add(IdleCard());
            _active.Visibility = Visibility.Visible;
        }
        foreach (var v in _views.Values) v.Update();
        UpdateCount();
    }

    /// <summary>Nothing to show above your games: a quiet card with the download mark, a title and the last update check.</summary>
    FrameworkElement IdleCard()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var accent = Ui.ToneColor(Ui.Tone.Accent);
        grid.Children.Add(new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)),
            Child = new FontIcon { Glyph = "", FontSize = 18, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush") },
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = L.T("Nothing is downloading"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        _idleStatus = new TextBlock { Text = IdleText(), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        text.Children.Add(_idleStatus);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border
        {
            Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(12), Child = grid,
            Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        };
    }

    /// <summary>A game with an update found and not started: its icon, its name, and Update (the store's build numbers mean nothing to people).</summary>
    static FrameworkElement PendingRow(PendingUpdate p)
    {
        var grid = new Grid { ColumnSpacing = 16, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10), Background = Ui.Brush("CardBackgroundFillColorDefaultBrush") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(12), Background = Ui.Brush("SubtleFillColorSecondaryBrush") };
        if (AppState.Find(p.Id) is { } game)
        {
            async void Load() { if (await MainWindow.GameIconAsync(game) is { } src) icon.Child = new Image { Source = src, Stretch = Stretch.UniformToFill }; }
            Load();
        }
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        text.Children.Add(new TextBlock { Text = p.Name, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        line.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2F, 0x8C, 0xF0)) });
        line.Children.Add(new TextBlock { Text = L.T("A new version is ready to download"), FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(line);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var update = new Button { Content = L.T("Update"), Style = Ui.Style("AccentButtonStyle"), VerticalAlignment = VerticalAlignment.Center };
        update.Click += (_, _) =>
        {
            if (GameSessions.IsActive(p.Id)) { App.Window?.ShowError(L.F("{0} is running", p.Name), L.T("Close the game first: it updates once it is closed.")); return; }
            GameUpdater.StartAvailable(p.Id);
        };
        Grid.SetColumn(update, 2);
        grid.Children.Add(update);
        return grid;
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
            if (i >= 0 && _grid.TryGetElement(i) is CoverCell { Tag: string } cell && _games.TryGetValue(id, out var game))
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

        // 2.1.4: Steam's own library hero (3840 x 1240, what Steam's downloads page shows) before the account's wide picture,
        // which for Steam is the 460 px store header: stretched over the card it was blurry and grainy.
        static string? Wide(DownloadJob j) =>
            j.Id.StartsWith("steam:") ? $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{j.Id[6..]}/library_hero.jpg" : OwnedLibrary.Find(j.Id)?.Wide;

        static string? WideFallback(DownloadJob j) => j.Id.StartsWith("steam:") ? OwnedLibrary.Find(j.Id)?.Wide : null;

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
                    catch (Exception ex) { App.Window?.ShowError(L.T("The game did not start"), ex.Message, ex); }
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
                    if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
                }
                var message = await Downloads.CancelAsync(job);
                if (message is null) App.Window?.ShowInfo(L.F("The download of {0} was cancelled.", job.Name));
                else if (job.Store == GameStore.Steam) App.Window?.ShowError(L.T("Could not cancel the download"), message);
                else App.Window?.ShowInfo(message);
            }
            catch (Exception ex)
            {
                Log.Error("Cancel download", ex);
                App.Window?.ShowError(L.T("Could not cancel the download"), ex.Message, ex);
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
                // Decoded at the card's width on a large display (it was 1600 px, enlarged past 2000: soft), the store's own
                // wide picture if the game has no library hero.
                var bitmap = new BitmapImage { DecodePixelWidth = 2560, DecodePixelType = DecodePixelType.Logical, UriSource = wu };
                if (WideFallback(_job) is { } other && Uri.TryCreate(other, UriKind.Absolute, out var ou))
                {
                    bool tried = false;
                    bitmap.ImageFailed += (_, _) => { if (tried) return; tried = true; bitmap.DecodePixelWidth = 0; bitmap.UriSource = ou; };
                }
                var art = Smooth.Art(bitmap, Smooth.Inner(new Border { CornerRadius = card.CornerRadius, BorderThickness = card.BorderThickness }), fadeMs: 300);
                // 2.1.4: shown as Steam shows it (it was at a quarter of its strength behind two shades: nearly black).
                card.Children.Add(new Border { Child = art, Opacity = 0.9 });
            }
            var inner = Smooth.Inner(new Border { CornerRadius = card.CornerRadius, BorderThickness = card.BorderThickness });
            card.Children.Add(new Border { Background = Shade(0xEC, 0x9C, 0x18, horizontal: true), CornerRadius = inner });
            card.Children.Add(new Border { Background = Shade(0x00, 0x18, 0x90, horizontal: false), CornerRadius = inner });

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
        // Every linked launcher has its chip; one whose library cannot be read lists its installed games and says why.
        var stores = OwnedLibrary.All.Keys.Where(OwnedLibrary.Linked).OrderBy(s => s).ToList();
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
        _layout.MinItemWidth = _tile + 2 * PopX;
        _layout.MinItemHeight = Math.Round(_tile * 1.5) + 58 + PopTop + PopBottom;
        _items = [.. _items];
        _grid.ItemsSource = _items;
    }

    /// <summary>Makes the covers of the repeater as they come into view (and drops them as they leave it).</summary>
    sealed partial class TileFactory(DownloadsPage page) : IElementFactory
    {
        // Each cover sits in a cell tagged with its id, so one cover can be made again in place when its game changes.
        public UIElement GetElement(ElementFactoryGetArgs args) =>
            args.Data is string id && page._games.TryGetValue(id, out var g)
                ? new CoverCell { Tag = id, Child = page.CachedTile(g), Padding = new Thickness(PopX, PopTop, PopX, PopBottom) } : new CoverCell();

        // The cell leaves the screen: its cover is let go (kept in the cache) so it can go into the next cell that shows it.
        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is CoverCell cell) cell.Child = null;
        }
    }

    /// <summary>The game's cover from the cache when its state and size are the same, else made (and kept) now.</summary>
    FrameworkElement CachedTile(OwnedGame g)
    {
        // The game's own cover is part of the key: a cover changed with Edit artwork shows (2.1-rc.3 audit).
        var key = $"{_tile:0}|{TileState(g.Id)}|{Downloads.Find(g.Id)?.State}|{HybridLauncher.Core.Media.ArtOverrides.Get(g.Id, HybridLauncher.Core.Media.ArtKind.Cover)}";
        if (_tileCache.TryGetValue(g.Id, out var hit) && hit.Key == key && hit.Tile.Parent is null && OwnedLibrary.IsDrawn(hit.Tile))
        {
            _tileOrder.Remove(g.Id);
            _tileOrder.AddLast(g.Id);
            return hit.Tile;
        }
        var tile = OwnedLibrary.Tile(g, _tile, FillJobs, cellTakesFocus: true);
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
            : Enum.TryParse<GameStore>(_store, out var picked) && OwnedGames.InstalledOnly(picked) && OwnedLibrary.All.TryGetValue(picked, out var only) && only.Problem is { } why
                ? (_showIndex == 0 ? why : L.F("No {0} game is installed. {1}", StoreLogos.Label(picked), why))
            : _showIndex == 0 ? L.T("Every game you own is installed.")
            : L.T("No games to show.");
    }
}

/// <summary>
/// One cover's cell in Downloads (2.3-rc.6): the control the keyboard and the controller focus, so the D-pad moves from cover to
/// cover (the covers were panels, and only the hidden Install pill in their middle could take the focus). Focused, the cover
/// lifts as under the pointer; Enter (the controller's A) opens the game's page, the menu key (X) its right-click menu.
/// </summary>
sealed partial class CoverCell : ContentControl
{
    readonly Border _frame = new();

    public CoverCell()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = _frame;
        GotFocus += (_, e) => { if (ReferenceEquals(e.OriginalSource, this) && Child is FrameworkElement t) OwnedLibrary.Hover(t, true); };
        LostFocus += (_, e) => { if (ReferenceEquals(e.OriginalSource, this) && Child is FrameworkElement t) OwnedLibrary.Hover(t, false); };
        KeyDown += (_, e) =>
        {
            // The arrow keys walk the covers as the controller does (DownloadsPage.PadMove).
            if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
                && ReferenceEquals(e.OriginalSource, this))
            {
                var dir = e.Key switch
                {
                    Windows.System.VirtualKey.Left => Microsoft.UI.Xaml.Input.FocusNavigationDirection.Left,
                    Windows.System.VirtualKey.Right => Microsoft.UI.Xaml.Input.FocusNavigationDirection.Right,
                    Windows.System.VirtualKey.Up => Microsoft.UI.Xaml.Input.FocusNavigationDirection.Up,
                    _ => Microsoft.UI.Xaml.Input.FocusNavigationDirection.Down,
                };
                for (DependencyObject? p = this; p is not null; p = VisualTreeHelper.GetParent(p))
                    if (p is DownloadsPage page) { e.Handled = page.PadMove(this, dir); break; }
                return;
            }
            if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space or Windows.System.VirtualKey.GamepadA)) return;
            if (!ReferenceEquals(e.OriginalSource, this)) return;
            e.Handled = Open();
        };
        ContextRequested += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this) && Child is FrameworkElement t && OwnedLibrary.ShowMenu(t)) e.Handled = true;
        };
    }

    /// <summary>
    /// Opens the game's page. Also called straight from the controller's A (2.3-rc.6): an Enter sent through Windows goes to
    /// whatever window is in front, which was the game being played when Hybrid Launcher was behind it.
    /// </summary>
    public bool Open()
    {
        if (Tag is not string id || OwnedLibrary.Find(id) is not { } game) return false;
        if (Child is FrameworkElement t) OwnedLibrary.Hover(t, false);
        OwnedLibrary.Open(game);
        return true;
    }

    /// <summary>The cover (made by <see cref="OwnedLibrary.Tile"/>), with the room for its lift as padding.</summary>
    public UIElement? Child
    {
        get => _frame.Child;
        set => _frame.Child = value;
    }

    public new Thickness Padding
    {
        get => _frame.Padding;
        set => _frame.Padding = value;
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
