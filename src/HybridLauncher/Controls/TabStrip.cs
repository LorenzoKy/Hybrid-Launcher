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
public sealed record GameTab(string Id, string Title, string Glyph, Color BaseTint, string Description)
{
    /// <summary>The icon's colour as the palette wants it (Cozy softens it).</summary>
    public Color Tint => HybridLauncher.Helpers.Palettes.Adapt(BaseTint);

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
        // The game's home (2.0.0-rc.10): a large house icon, no name, at the head of its own group.
        new("overview", "Dashboard", "\uE80F", C(0xFFE05CB8), "The game on one page: updates and news, your play, achievements, mods, trailer, details, requirements and reviews"),
        new("auto", "Auto", "\uE9F5", C(0xFF2EC7F0), "Experimental: OptiScaler, ReShade, RenoDX and Luma picked for this game and installed together with one click, with recommended settings"),
        new("quick", "Quick access", "\uECA5", C(0xFF00C2D8), "Your own folders, programs and files for this game"),
        new("notes", "Notes", "\uE70B", C(0xFFB9A3FF), "Free notes for this game"),
        new("opti", "OptiScaler", "\uE740", C(0xFF9A7CFF), "Install, update and remove OptiScaler, its settings, ASI plugins and the wiki entry"),
        new("mods", "Mods", "\uEA86", C(0xFFF07F2A), "The mods installed in this game and their conflicts; ReShade, RenoDX, Luma, DLSS 5, DXVK and VKD3D-Proton"),
        new("ultra", "Ultra+", "\uE8B9", C(0xFF4FD6B0), "Ultra+ graphics mods for Unreal Engine games: builds, updates, add-ons and settings"),
        new("pcgw", "PCGamingWiki", "\uE774", C(0xFF3D9BFF), "Fixes, issues and settings from PCGamingWiki"),
        new("ach", "Achievements", HybridLauncher.Helpers.Icons.TrophyGlyph, C(0xFFF2B82E), "Steam achievements (only for games that have them)"),
        new("links", "Links", "\uE71B", C(0xFF3FC26A), "Every useful page about the game"),
    ];

    /// <summary>The sections stacked inside the tabs that hold several ("opti", "mods"), in page order: they are the sub-tabs.</summary>
    static readonly Dictionary<string, GameTab[]> SectionsOf = new(StringComparer.OrdinalIgnoreCase)
    {
        // The Dashboard's main column (1.4.0-rc.9): the ids are its sections' own (DashboardView.All).
        ["overview"] =
        [
            new("media", "Trailer", "\uE714", C(0xFFE5484D), "Trailers and screenshots"),
            new("spotlight", "Latest update", "\uE735", C(0xFFF07F2A), "The post Steam features, or the newest major update, and what it changes"),
            new("news", "News", "\uE789", C(0xFF3D9BFF), "The game's posts on Steam, and its version history"),
            new("about", "About", "\uE946", C(0xFFE05CB8), "The store's description of the game"),
            new("wikis", "Wikis", "\uE736", C(0xFF22B8B0), "What the OptiScaler wiki and PCGamingWiki say about the game"),
            new("requirements", "Requirements", "\uE7F4", C(0xFFF07F2A), "Minimum and recommended PC"),
            new("reviews", "Reviews", "\uE90A", C(0xFF3D9BFF), "Steam's ratings and reviews"),
        ],
        ["opti"] =
        [
            new("install", "Install", "\uE896", C(0xFF9A7CFF), "Install, update and remove OptiScaler; its saved versions"),
            new("plugins", "Plugins & DLLs", "\uEA86", C(0xFFE05CB8), "ASI plugins and custom upscaler or frame generation DLLs"),
            new("quickpanel", "Quick Panel", "\uE945", C(0xFFF2B82E), "Frame rate cap, frame generation and ASI plugins, the settings changed most"),
            new("settings", "Settings", "\uE9E9", C(0xFF5FA8FF), "OptiScaler.ini as switches, sliders and choices"),
            new("wiki", "Wiki", "\uE736", C(0xFF22B8B0), "The game's entry on the OptiScaler compatibility list"),
            new("notes", "Release notes", "\uE8F2", C(0xFF3FC2A0), "What each stable and nightly OptiScaler release changed"),
        ],
        ["mods"] =
        [
            // First what is in the folder (1.4.0-rc.18): each mod, its files, what loads them, conflicts; then each mod's own section.
            new("installed", "Installed", "\uE8FD", C(0xFFF07F2A), "Every mod in the game folder, its files and what loads them, and conflicts between them"),
            new("reshade", "ReShade", "\uE790", C(0xFFF07F2A), "ReShade installers, and loading ReShade through OptiScaler"),
            new("renodx", "RenoDX", "\uE706", C(0xFFF2B82E), "Mainly HDR mods for this game (ReShade add-ons)"),
            new("luma", "Luma", "\uE793", C(0xFFFF8A3D), "Luma Framework mods for DirectX 11 games: reworked rendering, such as upscaling, HDR and fixes"),
            new("dlss5", "DLSS 5", "\uE945", C(0xFF76B900), "DLSS 5 neural rendering: the PC's card, the model file, DLSSNR-AMD and F5 Multipass"),
            new("dxvk", "DXVK", "\uE7F4", C(0xFFE84A5F), "DXVK and DXVK-gplasync: Direct3D 8 to 11 through Vulkan"),
            new("vkd3d", "VKD3D-Proton", "\uE7F4", C(0xFFB84AE8), "VKD3D-Proton: Direct3D 12 through Vulkan"),
        ],
        ["ultra"] =
        [
            new("install", "Install & versions", "\uE896", C(0xFF4FD6B0), "Install, update, go back to an older build or remove Ultra+"),
            new("addons", "Add-ons", "\uE71D", C(0xFFE05CB8), "Optional add-ons of this game's Ultra+ mod"),
            new("settings", "Settings", "\uE9E9", C(0xFF5FA8FF), "UltraPlusConfig.ini as switches, sliders and choices, with presets"),
        ],
    };

    public static GameTab[] Sections(string tab) => SectionsOf.TryGetValue(tab, out var list) ? list : [];

    /// <summary>
    /// Where an id lands: the tab it opens and the section to scroll to. Tabs that used to stand alone ("settings", "optiwiki",
    /// "reshade", "renodx", "luma", "dxvk", "dlss5") resolve to the tab that holds them now; "tab/section" names both; every other id is its own tab, with no section.
    /// </summary>
    public static (string Tab, string? Section) Resolve(string id) => id.Contains('/')
        ? (id[..id.IndexOf('/')].ToLowerInvariant(), id[(id.IndexOf('/') + 1)..].ToLowerInvariant())
        : id.ToLowerInvariant() switch
    {
        "settings" => ("opti", "settings"),
        "optiwiki" => ("opti", "wiki"),
        "releasenotes" => ("opti", "notes"),
        "reshade" => ("mods", "reshade"),
        "renodx" => ("mods", "renodx"),
        "luma" => ("mods", "luma"),
        "dxvk" => ("mods", "dxvk"),
        "vkd3d" => ("mods", "vkd3d"),
        "dlss5" => ("mods", "dlss5"),
        "modfiles" or "installedmods" => ("mods", "installed"),
        "ultraplus" => ("ultra", null),
        _ => (id, null),
    };

    /// <summary>
    /// The game's own group, left of the bar's hairline (2.0.0-rc.10): its home (the Dashboard), the one-click setup and the
    /// player's own things. The tabs after the hairline work on the game's mods and read about it.
    /// </summary>
    public static readonly HashSet<string> HomeGroup = new(StringComparer.OrdinalIgnoreCase) { "overview", "auto", "quick", "notes" };

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
    // The Dashboard is the game's home (rc.9): first, on its own, a hairline between it and the other tabs; it does not move.
    readonly StackPanel _home = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    // The home group (Home, Auto, Quick access, Notes, 2.0.0-rc.10); since rc.11 without a plate of its own (see Restyle).
    readonly Border _homePlate = new();
    readonly Border _homeRule = new() { Width = 1, Height = 22, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    readonly Button _gear;
    readonly Dictionary<string, Button> _buttons = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Self-test (perf): the tabs' buttons, in order.</summary>
    internal IEnumerable<Button> SelfTestButtons => _buttons.Values;

    /// <summary>Demo walk: a tab's button (sub-tabs too), for the drawn pointer.</summary>
    internal Button? DemoButton(string id) => _buttons.GetValueOrDefault(id) ?? _subButtons.GetValueOrDefault(id);
    string _selected = "";

    // Sub-tabs: the sections of the open tab when it holds several (OptiScaler, Mods), under the row of tabs.
    readonly StackPanel _subRow = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    // A recessed band of its own under the row, led by the open tab's name: the sections read as part of that tab, not as more tabs.
    readonly Border _subHost = new() { Margin = new Thickness(0, 5, 0, 0), Padding = new Thickness(4), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
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
    // The Dashboard is the first tab (1.4.0-rc.7): while it is open the gear customizes its cards instead of the tabs.
    bool _dashboard;

    // A window too narrow for every tab's name (1.4.0-rc.9): the tabs other than the open one show their icon alone
    // (their name is in the tooltip), instead of the last ones being cut off at the edge.
    bool _compact;
    double _fullWidth;
    Grid? _tabGrid;

    /// <summary>Whether the open tab is the Dashboard (the gear then customizes its cards).</summary>
    public bool Dashboard => _dashboard;

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
        // The tabs sit in the middle of the bar: the view switch on the left and the gear on the right get cells of the same
        // width (the wider of the two), so the tabs stay centred.
        var left = new Grid();
        var right = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        // An empty cell on the left as wide as the gear's on the right keeps the tabs centred.
        _gear.SizeChanged += (_, _) => { if (Math.Abs(left.MinWidth - _gear.ActualWidth) > 0.5) left.MinWidth = right.MinWidth = _gear.ActualWidth; };
        var scroller = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = _strip,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(scroller, 1);
        grid.Children.Add(scroller);
        _tabGrid = grid;
        grid.SizeChanged += (_, _) => UpdateCompact();
        _homePlate.Child = _home;
        _strip.Children.Add(_homePlate);
        _strip.Children.Add(_homeRule);
        _strip.Children.Add(_row);
        _strip.SizeChanged += (_, _) => UpdateCompact();
        _gear.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(_gear);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

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
        // No icon here: the open tab, right above, already shows it.
        lead.Children.Add(_subTitle);
        lead.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        var subGrid = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
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

    /// <summary>The open tab is the Dashboard (true): the gear customizes its cards; on the other tabs, the tabs.</summary>
    public void SetView(bool dashboard)
    {
        _dashboard = dashboard;
        ToolTipService.SetToolTip(_gear, dashboard
            ? L.T("Customize the dashboard: which cards show, and their order")
            : L.T("Customize tabs: order, and which ones show (everywhere or for this game)"));
    }

    /// <summary>Shows these tabs (already filtered and ordered), the <paramref name="selected"/> one marked.</summary>
    public void SetTabs(IEnumerable<GameTab> tabs, string selected)
    {
        _row.Children.Clear();
        _home.Children.Clear();
        _buttons.Clear();
        foreach (var t in tabs)
        {
            var b = CreateButton(t);
            _buttons[t.Id] = b;
            if (!GameTabs.HomeGroup.Contains(t.Id)) _row.Children.Add(b);
            // The home always first in its group.
            else if (t.Id == "overview") _home.Children.Insert(0, b);
            else _home.Children.Add(b);
        }
        _homePlate.Visibility = _home.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _homeRule.Visibility = _home.Children.Count > 0 && _row.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Other tabs, another width with names: measured again from the full row.
        _compact = false;
        _fullWidth = 0;
        Select(selected);
    }

    /// <summary>
    /// Whether the tabs fit with their names: the row's own width while every name shows (remembered while compact), against
    /// the room between the bar's two side cells.
    /// </summary>
    void UpdateCompact()
    {
        if (_tabGrid is null || _tabGrid.ActualWidth <= 0 || _buttons.Count == 0) return;
        if (!_compact && _strip.ActualWidth > 0) _fullWidth = _strip.ActualWidth;
        if (_fullWidth <= 0) return;
        double room = _tabGrid.ActualWidth - 2 * (_gear.ActualWidth + _tabGrid.ColumnSpacing);
        // A little slack before going back to names, so a width right at the edge does not flip back and forth.
        bool compact = !GameTabs.NoIcons && (_compact ? _fullWidth > room - 12 : _fullWidth > room + 0.5);
        if (compact == _compact) return;
        _compact = compact;
        Restyle();
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
        content.Children.Add(new TextBlock { Text = t.Name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
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

    static SolidColorBrush? _clear, _subDark, _subLight, _onDark, _onLight;
    static SolidColorBrush Clear => _clear ??= new(Microsoft.UI.Colors.Transparent);
    static SolidColorBrush SubDark => _subDark ??= new(Color.FromArgb(0x59, 0, 0, 0));
    static SolidColorBrush SubLight => _subLight ??= new(Color.FromArgb(0x0F, 0, 0, 0));
    // The open tab: a pane of light, brighter at the top edge, as if lit from above (no outline).
    static Brush? _onDarkLit;
    static Brush OnDarkLit => _onDarkLit ??= new LinearGradientBrush
    {
        StartPoint = new Windows.Foundation.Point(0.5, 0),
        EndPoint = new Windows.Foundation.Point(0.5, 1),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Offset = 0 },
            new GradientStop { Color = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Offset = 0.6 },
            new GradientStop { Color = Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), Offset = 1 },
        },
    };
    static SolidColorBrush OnDark => _onDark ??= new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    static SolidColorBrush OnLight => _onLight ??= new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));

    void RestyleSub()
    {
        if (_subButtons.Count == 0) return;
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        var accent = (res["AccentFillColorDefaultBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(255, 0x9A, 0x5C, 0xFF);
        bool dark = ActualTheme == ElementTheme.Dark;
        // The band: darker than the tab bar (a step down into the open tab) a plain fill with no edge.
        _subHost.Background = dark ? SubDark : SubLight;
        _subHost.BorderBrush = Clear;
        _subTitle.Foreground = (Brush)res["AccentTextFillColorPrimaryBrush"];
        foreach (var (id, b) in _subButtons)
        {
            bool on = id.Equals(_subSelected, StringComparison.OrdinalIgnoreCase);
            var tab = (GameTab)b.Tag;
            b.Background = on ? new SolidColorBrush(Color.FromArgb(dark ? (byte)0x3A : (byte)0x2C, accent.R, accent.G, accent.B)) : Clear;
            // No outline on the open tab (a plain tinted fill, as in Steam): its fill, bold word and underline say which it is.
            b.BorderBrush = Clear;
            var content = (StackPanel)b.Content;
            var icon = (IconElement)content.Children[0];
            icon.Visibility = GameTabs.NoSubIcons ? Visibility.Collapsed : Visibility.Visible;
            content.Spacing = GameTabs.NoSubIcons ? 0 : 7;
            if (on) icon.Foreground = (Brush)res["AccentTextFillColorPrimaryBrush"];
            else if (GameTabs.Colored) icon.Foreground = new SolidColorBrush(tab.Tint);
            else icon.Foreground = (Brush)res["TextFillColorSecondaryBrush"];
            var text = (TextBlock)content.Children[1];
            text.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
            text.Opacity = on ? 1 : 0.88;
        }
    }

    readonly Border _bar;

    void Restyle()
    {
        RestyleSub();
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        _homeRule.Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        // No fill of its own (rc.11): a lighter plate made one group look lit and the other switched off. The hairline alone
        // parts the two groups; both read the same.
        _homePlate.Background = Clear;
        // Dark see-through glass like the launch bar above it (the art, blurred, under a smoky tint), as Steam's tab band.
        // One glass brush for the bar's life: a new one at every restyle compiled its blur effect again.
        _bar.Background ??= new HybridLauncher.Helpers.GlassBrush(Color.FromArgb(0x70, 0x0C, 0x0D, 0x11), 32);
        _bar.BorderBrush = Clear;
        var accent = (res["AccentFillColorDefaultBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(255, 0x9A, 0x5C, 0xFF);
        bool dark = ActualTheme == ElementTheme.Dark;
        foreach (var (id, b) in _buttons)
        {
            bool on = id.Equals(_selected, StringComparison.OrdinalIgnoreCase);
            var tab = (GameTab)b.Tag;
            // Minimal, as Steam and Hydra: no tinted pill behind the open tab, only a faint lift; its bold white word, lit icon
            // and underline say which it is, and the others step back.
            b.Background = on ? (dark ? (AppState.Settings.Shading ? OnDarkLit : OnDark) : OnLight) : Clear;
            b.BorderBrush = Clear;
            if (b.Content is Grid g)
            {
                // The glyph alone: the accent on the open tab, the tab's own colour on the others (one quiet grey in "mono").
                var square = (Border)g.Children[0];
                var icon = (IconElement)square.Child;
                square.Visibility = GameTabs.NoIcons ? Visibility.Collapsed : Visibility.Visible;
                // The home is its icon alone, larger (its name in the tooltip); with icons turned off it keeps its name.
                bool home = id == "overview" && !GameTabs.NoIcons;
                bool iconOnly = home || (_compact && !on && !GameTabs.NoIcons);
                g.ColumnSpacing = GameTabs.NoIcons || iconOnly ? 0 : 8;
                g.Children[1].Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
                b.Padding = home ? new Thickness(13, 6, 13, 6) : iconOnly ? new Thickness(12, 8, 12, 8) : new Thickness(14, 8, 16, 8);
                if (on) icon.Foreground = (Brush)res["AccentTextFillColorPrimaryBrush"];
                else if (GameTabs.Colored) icon.Foreground = new SolidColorBrush(tab.Tint);
                else icon.Foreground = (Brush)res["TextFillColorSecondaryBrush"];
                var text = (TextBlock)g.Children[1];
                text.FontWeight = on ? FontWeights.Bold : FontWeights.SemiBold;
                // The others a step back, still clearly readable on the art behind the glass.
                text.Opacity = on ? 1 : 0.85;
                // The open tab's mark: a short accent bar under its name.
                ((Border)g.Children[2]).Opacity = on ? 1 : 0;
                if (g.Children.Count > 3 && g.Children[3] is Border glow)
                {
                    bool lit = on && dark && AppState.Settings.Shading;
                    glow.Opacity = lit ? 1 : 0;
                    if (lit) glow.Background = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0.5, 0),
                        EndPoint = new Windows.Foundation.Point(0.5, 1),
                        GradientStops =
                        {
                            new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 0 },
                            new GradientStop { Color = Color.FromArgb(0x48, accent.R, accent.G, accent.B), Offset = 1 },
                        },
                    };
                }
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
        // The glyph alone, in the tab's colour (1.4.0-rc.8: no square behind it, so it reads as part of the tab's name).
        bool home = t.Id == "overview";
        content.Children.Add(new Border
        {
            Width = home ? 26 : 20,
            Height = home ? 26 : 22,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Centered(HybridLauncher.Helpers.Icons.Make(t.Glyph, home ? 22 : 16)),
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
        // Light rising from the underline into the open tab: a glow rather than a shadow (coloured in Restyle).
        var glow = new Border
        {
            Height = 14,
            Margin = new Thickness(-6, 0, -6, -4),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        Grid.SetRowSpan(glow, 2);
        Grid.SetColumnSpan(glow, 2);
        content.Children.Add(glow);

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
        // Its name first: on a narrow window the tabs other than the open one show their icon alone.
        ToolTipService.SetToolTip(b, t.Name + "\n" + t.Info + "\n" + L.T("Drag to move it, right-click to hide it."));
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
        // Its name for screen readers and the self-test (the button shows an icon and the name as separate parts).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, t.Name);
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
    // The group the dragged tab moves in (2.0.0-rc.11: the game's group too, not only the tabs after the hairline); a tab
    // never leaves its group, and the Home stays first in its own.
    StackPanel _dragRow = null!;
    double _startX, _grab;
    bool _dragging, _dragged;
    List<string>? _orderBefore;

    /// <summary>The first place a tab of this group may take: after the Home in the game's group.</summary>
    int FirstFree(StackPanel row) => ReferenceEquals(row, _home) && _home.Children.Count > 0 && _home.Children[0] is Button { Tag: GameTab { Id: "overview" } } ? 1 : 0;

    void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button b || !e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
        if (b.Parent is not StackPanel row || (!ReferenceEquals(row, _home) && !ReferenceEquals(row, _row))) return;
        // The Home stays first.
        if (b.Tag is GameTab { Id: "overview" }) return;
        _drag = b;
        _dragRow = row;
        _dragging = false;
        _dragged = false;
        _startX = e.GetCurrentPoint(row).Position.X;
        _grab = _startX - Left(b);
    }

    void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null || !ReferenceEquals(sender, _drag)) return;
        var _row = _dragRow;
        int first = FirstFree(_row);
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
            else if (index > first && _row.Children[index - 1] is FrameworkElement prev && mid < Left(prev) + prev.ActualWidth / 2)
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

    List<string> Order() => _home.Children.Concat(_row.Children).OfType<Button>().Select(b => ((GameTab)b.Tag).Id).ToList();

    double Left(UIElement e) => e.TransformToVisual(_dragRow ?? _row).TransformPoint(new Windows.Foundation.Point(0, 0)).X - (e.RenderTransform is TranslateTransform t ? t.X : 0);
}
