using HybridLauncher.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace HybridLauncher.Controls;

/// <summary>A tab of the game page: its id (saved in settings), name, icon and the icon's colour.</summary>
public sealed record GameTab(string Id, string Title, string Glyph, Color Tint, string Description)
{
    /// <summary>The title and description in the UI language (the English ones are the keys).</summary>
    public string Name => L.T(Title);
    public string Info => L.T(Description);
}

/// <summary>
/// The game page tabs: the order the user arranged, the ones hidden everywhere and the ones hidden for one game.
/// Tabs missing from the saved order (new in an update) keep their default place.
/// </summary>
public static class GameTabs
{
    static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static readonly GameTab[] All =
    [
        new("overview", "Game", "\uE7FC", C(0xFFE05CB8), "Trailer, screenshots, description, requirements, languages"),
        new("opti", "OptiScaler", "\uE740", C(0xFF9A7CFF), "Install, update and remove OptiScaler, its settings, ASI plugins and the wiki entry"),
        new("mods", "ReShade & Mods", "\uE790", C(0xFFF07F2A), "ReShade, RenoDX and Luma: effects and HDR mods for this game"),
        new("pcgw", "PCGamingWiki", "\uE774", C(0xFF3D9BFF), "Fixes, issues and settings from PCGamingWiki"),
        new("ach", "Achievements", HybridLauncher.Helpers.Icons.TrophyGlyph, C(0xFFF2B82E), "Steam achievements (only for games that have them)"),
        new("quick", "Quick access", "\uECA5", C(0xFF00C2D8), "Your own folders, programs and files for this game"),
        new("notes", "Notes", "\uE70B", C(0xFFB9A3FF), "Free notes for this game"),
        new("links", "Links", "\uE71B", C(0xFF3FC26A), "Every useful page about the game"),
    ];

    /// <summary>The sections stacked inside the tabs that hold several ("opti", "mods"), in page order: they are the sub-tabs.</summary>
    static readonly Dictionary<string, GameTab[]> SectionsOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opti"] =
        [
            new("install", "Install & plugins", "\uE896", C(0xFF9A7CFF), "Install, update and remove OptiScaler, ASI plugins"),
            new("quickpanel", "Quick Panel", "\uE945", C(0xFFF2B82E), "Frame rate cap, frame generation and ASI plugins in one place"),
            new("settings", "Settings", "\uE9E9", C(0xFF5FA8FF), "OptiScaler.ini as switches, sliders and choices"),
            new("wiki", "Wiki", "\uE736", C(0xFF22B8B0), "The game's entry on the OptiScaler compatibility list"),
            new("notes", "Release notes", "\uE8F2", C(0xFF3FC2A0), "What each stable and nightly OptiScaler release changed"),
        ],
        ["mods"] =
        [
            new("reshade", "ReShade", "\uE790", C(0xFFF07F2A), "ReShade installers, and loading ReShade through OptiScaler"),
            new("renodx", "RenoDX", "\uE706", C(0xFFF2B82E), "HDR mods for this game (ReShade add-ons)"),
            new("luma", "Luma", "\uE793", C(0xFFFF8A3D), "Luma Framework mods: HDR and DLSS for DirectX 11 games"),
        ],
    };

    public static GameTab[] Sections(string tab) => SectionsOf.TryGetValue(tab, out var list) ? list : [];

    /// <summary>
    /// Where an id lands: the tab it opens and the section to scroll to. Tabs that used to stand alone ("settings", "optiwiki",
    /// "reshade", "renodx", "luma") resolve to the tab that holds them now; every other id is its own tab, with no section.
    /// </summary>
    public static (string Tab, string? Section) Resolve(string id) => id.ToLowerInvariant() switch
    {
        "settings" => ("opti", "settings"),
        "optiwiki" => ("opti", "wiki"),
        "releasenotes" => ("opti", "notes"),
        "reshade" => ("mods", "reshade"),
        "renodx" => ("mods", "renodx"),
        "luma" => ("mods", "luma"),
        _ => (id, null),
    };

    public static GameTab? Find(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every tab in the user's order.</summary>
    public static List<GameTab> Ordered()
    {
        var saved = AppState.Settings.TabOrder.Select(Find).Where(t => t is not null).Select(t => t!).ToList();
        for (int i = 0; i < All.Length; i++)
        {
            if (saved.Contains(All[i])) continue;
            // A new tab goes after the tab that comes before it by default.
            int after = i == 0 ? -1 : saved.IndexOf(All[i - 1]);
            saved.Insert(after < 0 ? Math.Min(i, saved.Count) : after + 1, All[i]);
        }
        return saved;
    }

    /// <summary>Settings → Appearance: tab icons each in their own colour (the default for new installs; otherwise one quiet colour).</summary>
    public static bool Colored => AppState.Settings.TabIcons == "color";

    /// <summary>Settings → Appearance → Icons: tabs as plain names, without icons.</summary>
    public static bool NoIcons => !IconSpots.Shown(AppState.Settings, IconSpots.Tabs);

    /// <summary>Settings → Appearance → Icons: the sub-tabs (sections of a tab) as plain names.</summary>
    public static bool NoSubIcons => !IconSpots.Shown(AppState.Settings, IconSpots.SubTabs);

    public static bool HiddenEverywhere(string id) => AppState.Settings.HiddenTabs.Contains(id);

    public static bool Hidden(string id, GameSettings? game) => HiddenEverywhere(id) || game?.HiddenTabs.Contains(id) == true;

    public static void SaveOrder(IEnumerable<string> ids)
    {
        AppState.Settings.TabOrder = ids.ToList();
        SettingsStore.Save();
    }
}

/// <summary>
/// The tab row of the game page: one pill per tab with its coloured icon, the open one filled with the accent. Tabs can be
/// dragged to a new place, and right-clicked to hide them for this game or everywhere; the gear at the end opens the
/// customisation dialog. Raises events only: the page decides what to show.
/// </summary>
public sealed partial class TabStrip : UserControl
{
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly Button _gear;
    readonly Dictionary<string, Button> _buttons = new(StringComparer.OrdinalIgnoreCase);
    string _selected = "";

    // Sub-tabs: the sections of the open tab when it holds several (OptiScaler, ReShade & Mods), under the row of tabs.
    readonly StackPanel _subRow = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    // A recessed band of its own under the row, led by the open tab's name: the sections read as part of that tab, not as more tabs.
    readonly Border _subHost = new() { Margin = new Thickness(0, 5, 0, 0), Padding = new Thickness(4), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
    readonly FontIcon _subIcon = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _subTitle = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<string, Button> _subButtons = new(StringComparer.OrdinalIgnoreCase);
    string _subSelected = "";

    public event Action<string>? TabInvoked;
    /// <summary>A sub-tab was clicked: the page scrolls to that section.</summary>
    public event Action<string>? SubTabInvoked;
    public event Action<List<string>>? Reordered;
    /// <summary>(tab id, everywhere): hide it for this game, or for every game.</summary>
    public event Action<string, bool>? HideRequested;
    public event Action? CustomizeRequested;

    public TabStrip()
    {
        _gear = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 15 },
            Padding = new Thickness(11, 9, 11, 9),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        ToolTipService.SetToolTip(_gear, L.T("Customize tabs: order, and which ones show (everywhere or for this game)"));
        _gear.Click += (_, _) => CustomizeRequested?.Invoke();

        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var scroller = new ScrollViewer
        {
            Content = _row,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        grid.Children.Add(scroller);
        Grid.SetColumn(_gear, 1);
        grid.Children.Add(_gear);

        var subScroller = new ScrollViewer
        {
            Content = _subRow,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // The open tab's name, then a chevron, then its sections.
        var lead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Padding = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        lead.Children.Add(_subIcon);
        lead.Children.Add(_subTitle);
        lead.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        var subGrid = new Grid { ColumnSpacing = 6 };
        subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(subScroller, 1);
        subGrid.Children.Add(lead);
        subGrid.Children.Add(subScroller);
        _subHost.Child = subGrid;
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(_subHost);

        // One rounded bar holding every tab (and under them the sections of the open one), a step above the page.
        Content = _bar = new Border
        {
            Child = stack,
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
        };
        ActualThemeChanged += (_, _) => Restyle();
        // A palette or Material colours change that keeps light / dark raises no ActualThemeChanged: listen while on screen.
        Action applied = Restyle;
        Loaded += (_, _) => { HybridLauncher.Helpers.ThemeRes.Applied -= applied; HybridLauncher.Helpers.ThemeRes.Applied += applied; Restyle(); };
        Unloaded += (_, _) => HybridLauncher.Helpers.ThemeRes.Applied -= applied;
        Restyle();
    }

    /// <summary>Shows these tabs (already filtered and ordered), the <paramref name="selected"/> one marked.</summary>
    public void SetTabs(IEnumerable<GameTab> tabs, string selected)
    {
        _row.Children.Clear();
        _buttons.Clear();
        foreach (var t in tabs)
        {
            var b = CreateButton(t);
            _buttons[t.Id] = b;
            _row.Children.Add(b);
        }
        Select(selected);
    }

    public void Select(string id)
    {
        _selected = id;
        Restyle();
        if (_buttons.TryGetValue(id, out var b)) b.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = double.NaN });
    }

    /// <summary>Shows the sections of the open tab as sub-tabs under the row (none: the sub bar goes away).</summary>
    public void SetSubTabs(IEnumerable<GameTab> subs, string selected)
    {
        _subRow.Children.Clear();
        _subButtons.Clear();
        foreach (var s in subs)
        {
            var b = CreateSubButton(s);
            _subButtons[s.Id] = b;
            _subRow.Children.Add(b);
        }
        _subHost.Visibility = _subButtons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (GameTabs.Find(_selected) is { } parent)
        {
            _subIcon.Glyph = parent.Glyph;
            _subTitle.Text = parent.Name;
        }
        _subSelected = selected;
        RestyleSub();
    }

    /// <summary>The section marked in the sub bar ("" when the open tab has none).</summary>
    public string SelectedSub => _subSelected;

    /// <summary>Marks the sub-tab of the section at the top of the page; only restyles when it changes (called while scrolling).</summary>
    public void SelectSub(string id)
    {
        if (id.Equals(_subSelected, StringComparison.OrdinalIgnoreCase)) return;
        _subSelected = id;
        RestyleSub();
    }

    Button CreateSubButton(GameTab t)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var subIcon = HybridLauncher.Helpers.Icons.Make(t.Glyph, 13);
        subIcon.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(subIcon);
        content.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content,
            Tag = t,
            Padding = new Thickness(11, 5, 13, 6),
            MinWidth = 0,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
        };
        b.Click += (_, _) => SubTabInvoked?.Invoke(t.Id);
        return b;
    }

    void RestyleSub()
    {
        if (_subButtons.Count == 0) return;
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        var accent = (res["AccentFillColorDefaultBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(255, 0x9A, 0x5C, 0xFF);
        bool dark = ActualTheme == ElementTheme.Dark;
        // The band: darker than the tab bar (a step down into the open tab) a plain fill with no edge.
        _subHost.Background = new SolidColorBrush(dark ? Color.FromArgb(0x59, 0, 0, 0) : Color.FromArgb(0x0F, 0, 0, 0));
        _subHost.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _subIcon.Foreground = _subTitle.Foreground = (Brush)res["AccentTextFillColorPrimaryBrush"];
        _subIcon.Visibility = GameTabs.NoSubIcons ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (id, b) in _subButtons)
        {
            bool on = id.Equals(_subSelected, StringComparison.OrdinalIgnoreCase);
            var tab = (GameTab)b.Tag;
            b.Background = on ? new SolidColorBrush(Color.FromArgb(dark ? (byte)0x3A : (byte)0x2C, accent.R, accent.G, accent.B)) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            // No outline on the open tab (a plain tinted fill, as in Steam): its fill, bold word and underline say which it is.
            b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var content = (StackPanel)b.Content;
            var icon = (IconElement)content.Children[0];
            icon.Visibility = GameTabs.NoSubIcons ? Visibility.Collapsed : Visibility.Visible;
            content.Spacing = GameTabs.NoSubIcons ? 0 : 7;
            if (on) icon.Foreground = (Brush)res["AccentTextFillColorPrimaryBrush"];
            else if (GameTabs.Colored) icon.Foreground = new SolidColorBrush(tab.Tint);
            else icon.Foreground = (Brush)res["TextFillColorSecondaryBrush"];
            var text = (TextBlock)content.Children[1];
            text.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
            text.Opacity = on ? 1 : 0.82;
        }
    }

    readonly Border _bar;

    void Restyle()
    {
        RestyleSub();
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        // Dark see-through glass like the launch bar above it (the art, blurred, under a smoky tint), as Steam's tab band.
        _bar.Background = new HybridLauncher.Helpers.GlassBrush(Color.FromArgb(0x70, 0x0C, 0x0D, 0x11), 32);
        _bar.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var accent = (res["AccentFillColorDefaultBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(255, 0x9A, 0x5C, 0xFF);
        bool dark = ActualTheme == ElementTheme.Dark;
        foreach (var (id, b) in _buttons)
        {
            bool on = id.Equals(_selected, StringComparison.OrdinalIgnoreCase);
            var tab = (GameTab)b.Tag;
            // Minimal, as Steam and Hydra: no tinted pill behind the open tab, only a faint lift; its bold white word, lit icon
            // and underline say which it is, and the others step back.
            b.Background = on
                ? new SolidColorBrush(Color.FromArgb(dark ? (byte)0x14 : (byte)0x10, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (b.Content is Grid g)
            {
                // The icon sits in a small rounded square: the tab's colour at low alpha (one quiet grey in "mono"); the open tab's
                // square is filled with the accent and the glyph turns white, so it reads on the tinted pill.
                var square = (Border)g.Children[0];
                var icon = (IconElement)square.Child;
                square.Visibility = GameTabs.NoIcons ? Visibility.Collapsed : Visibility.Visible;
                g.ColumnSpacing = GameTabs.NoIcons ? 0 : 9;
                if (on)
                {
                    square.Background = new SolidColorBrush(accent);
                    icon.Foreground = HybridLauncher.Helpers.Contrast.BrushOn(accent);
                }
                else if (GameTabs.Colored)
                {
                    square.Background = new SolidColorBrush(Color.FromArgb(0x26, tab.Tint.R, tab.Tint.G, tab.Tint.B));
                    icon.Foreground = new SolidColorBrush(tab.Tint);
                }
                else
                {
                    square.Background = new SolidColorBrush(dark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0));
                    icon.Foreground = (Brush)res["TextFillColorSecondaryBrush"];
                }
                var text = (TextBlock)g.Children[1];
                text.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
                text.Opacity = on ? 1 : 0.7;
                // The open tab's mark: a short accent bar under its name.
                ((Border)g.Children[2]).Opacity = on ? 1 : 0;
            }
        }
    }

    static IconElement Centered(IconElement icon)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    Button CreateButton(GameTab t)
    {
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Centered(HybridLauncher.Helpers.Icons.Make(t.Glyph, 13)),
        });
        var text = new TextBlock { Text = t.Name, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        var bar = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 4, 0, -4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["AccentFillColorDefaultBrush"],
            Opacity = 0,
        };
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 2);
        content.Children.Add(bar);

        var b = new Button
        {
            Content = content,
            Tag = t,
            Padding = new Thickness(14, 8, 16, 8),
            MinWidth = 0,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransform = new TranslateTransform(),
        };
        ToolTipService.SetToolTip(b, t.Info + "\n" + L.T("Drag to move it, right-click to hide it."));
        b.Click += (_, _) =>
        {
            // Click comes before the release handler: a drag set _dragged while moving.
            if (_dragged) return;
            TabInvoked?.Invoke(t.Id);
        };
        b.ContextFlyout = Menu(t);
        b.AddHandler(PointerPressedEvent, new PointerEventHandler(Pressed), true);
        b.AddHandler(PointerMovedEvent, new PointerEventHandler(Moved), true);
        b.AddHandler(PointerReleasedEvent, new PointerEventHandler(Released), true);
        b.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Released), true);
        return b;
    }

    MenuFlyout Menu(GameTab t)
    {
        var menu = new MenuFlyout();
        var game = new MenuFlyoutItem { Text = L.F("Hide \"{0}\" for this game", t.Name), Icon = new FontIcon { Glyph = "" } };
        game.Click += (_, _) => HideRequested?.Invoke(t.Id, false);
        var all = new MenuFlyoutItem { Text = L.F("Hide \"{0}\" in every game", t.Name), Icon = new FontIcon { Glyph = "" } };
        all.Click += (_, _) => HideRequested?.Invoke(t.Id, true);
        var custom = new MenuFlyoutItem { Text = L.T("Customize tabs…"), Icon = new FontIcon { Glyph = "" } };
        custom.Click += (_, _) => CustomizeRequested?.Invoke();
        menu.Items.Add(game);
        menu.Items.Add(all);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(custom);
        return menu;
    }

    // ---------- drag to reorder ----------

    Button? _drag;
    double _startX, _grab;
    bool _dragging, _dragged;
    List<string>? _orderBefore;

    void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button b || !e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
        _drag = b;
        _dragging = false;
        _dragged = false;
        _startX = e.GetCurrentPoint(_row).Position.X;
        _grab = _startX - Left(b);
    }

    void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || !ReferenceEquals(sender, _drag)) return;
        double x = e.GetCurrentPoint(_row).Position.X;
        if (!_dragging)
        {
            if (Math.Abs(x - _startX) < 8) return;
            _dragging = true;
            _dragged = true;
            _orderBefore = Order();
            _drag.CapturePointer(e.Pointer);
            Canvas.SetZIndex(_drag, 10);
            _drag.Opacity = 0.9;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
        // Swap with a neighbour once the dragged tab's middle passes the neighbour's middle.
        double wanted = x - _grab;
        double mid = wanted + _drag.ActualWidth / 2;
        // A fast move can pass several tabs at once: keep swapping until the neighbours are on the right sides.
        for (int guard = 0; guard < 32; guard++)
        {
            int index = _row.Children.IndexOf(_drag);
            if (index + 1 < _row.Children.Count && _row.Children[index + 1] is FrameworkElement next && mid > Left(next) + next.ActualWidth / 2)
                Swap(index + 1, index);
            else if (index > 0 && _row.Children[index - 1] is FrameworkElement prev && mid < Left(prev) + prev.ActualWidth / 2)
                Swap(index - 1, index + 1);
            else break;
        }
        ((TranslateTransform)_drag.RenderTransform).X = wanted - Left(_drag);
        e.Handled = true;

        // The neighbour moves, never the dragged tab: taking it out of the row would drop its pointer capture.
        void Swap(int from, int to)
        {
            var other = _row.Children[from];
            _row.Children.RemoveAt(from);
            _row.Children.Insert(from < to ? to - 1 : to, other);
            _row.UpdateLayout();
        }
    }

    void Released(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || !ReferenceEquals(sender, _drag)) return;
        var b = _drag;
        _drag = null;
        if (!_dragging) return;
        _dragging = false;
        b.ReleasePointerCaptures();
        ProtectedCursor = null;
        b.Opacity = 1;
        Canvas.SetZIndex(b, 0);
        // Slide into its new place.
        var t = (TranslateTransform)b.RenderTransform;
        var anim = new DoubleAnimation { From = t.X, To = 0, Duration = TimeSpan.FromMilliseconds(140), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, t);
        Storyboard.SetTargetProperty(anim, "X");
        sb.Children.Add(anim);
        sb.Begin();
        var order = Order();
        if (_orderBefore is null || !order.SequenceEqual(_orderBefore)) Reordered?.Invoke(order);
        // The click that follows a drag is not a tab change; a release outside the button raises none, so clear it soon.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _dragged = false);
    }

    List<string> Order() => _row.Children.OfType<Button>().Select(b => ((GameTab)b.Tag).Id).ToList();

    double Left(UIElement e) => e.TransformToVisual(_row).TransformPoint(new Windows.Foundation.Point(0, 0)).X - (e.RenderTransform is TranslateTransform t ? t.X : 0);
}
