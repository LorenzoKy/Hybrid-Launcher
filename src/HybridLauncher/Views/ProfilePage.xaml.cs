using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Media;
using HybridLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The user's dashboard (2.1), opened from their chip under the title: who they are (picture, name, since when) over the art
/// of the game they played most, then the whole library summed up: games, hours, achievements, perfect games, the last two
/// weeks of play, the games played most, the progress of every game's achievements, the latest unlocks and the launchers.
/// Built in turns (the header with the click, each section on its own turn of the UI thread); the sessions file is read off it.
/// </summary>
public sealed partial class ProfilePage : Page
{
    readonly StackPanel _root = new() { Spacing = 22, Padding = new Thickness(0, 8, 0, 40), MaxWidth = 1280, HorizontalAlignment = HorizontalAlignment.Stretch };
    int _run;

    public ProfilePage()
    {
        InitializeComponent();
        Outer.Children.Add(_root);
        Helpers.CompositorWheel.Attach(Scroller);
        // Static events hold whoever subscribes: only while the page is on screen (a page the Frame let go must not stay alive).
        Loaded += (_, _) =>
        {
            Library.StatsReady -= Refill; Library.StatsReady += Refill;
            Library.OwnedStatsReady -= Refill; Library.OwnedStatsReady += Refill;
            Profiles.Changed -= Refill; Profiles.Changed += Refill;
        };
        Unloaded += (_, _) => { Library.StatsReady -= Refill; Library.OwnedStatsReady -= Refill; Profiles.Changed -= Refill; };
    }

    // Drawn again only when what it shows changed (a stats read that brought nothing new leaves it alone).
    void Refill() { if (IsLoaded && Key() != _shown) Fill(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        using var __perf = UiPerf.Scope("ProfilePage.NavigatedTo");
        base.OnNavigatedTo(e);
        // A return shows the page as it was left (snappy rule 1) unless what it sums up changed.
        if (Key() != _shown) Fill();
        _ = Library.LoadStatsAsync(DispatcherQueue);
        _ = LoadOwnedAsync();
    }

    /// <summary>The games owned on the linked launchers (2.1-rc.3): their list, then their hours and achievements, in the background.</summary>
    async Task LoadOwnedAsync()
    {
        try
        {
            var dq = DispatcherQueue;
            await OwnedLibrary.LoadAsync();
            await Library.LoadOwnedStatsAsync(dq);
        }
        catch (Exception ex) { Log.Warn($"Profile owned games: {ex.Message}"); }
    }

    /// <summary>Every game of the user: the installed ones and those owned on a linked launcher but not installed.</summary>
    static (List<Game> All, int Installed) AllGames()
    {
        var installed = AppState.VisibleGames.Where(g => !g.NotInstalled).ToList();
        var all = new List<Game>(installed);
        if (OwnedLibrary.IsLoaded)
        {
            var hidden = AppState.Settings.HiddenGames;
            foreach (var o in OwnedLibrary.NotInstalled())
                if (!hidden.Contains(o.Id)) all.Add(AppState.FindAny(o.Id) ?? OwnedAsGame(o));
        }
        return (all, installed.Count);
    }

    static readonly Dictionary<string, Game> OwnedGames = new(StringComparer.OrdinalIgnoreCase);
    static Game OwnedAsGame(OwnedGame o) => OwnedGames.TryGetValue(o.Id, out var g) ? g : OwnedGames[o.Id] = OwnedLibrary.AsGame(o);

    /// <summary>Opens a game's page: an owned game not installed opens its Download page.</summary>
    static void Open(Game g)
    {
        if (g.NotInstalled && OwnedLibrary.Find(g.Id) is { } owned) OwnedLibrary.Open(owned);
        else App.Window?.OpenGame(g.Id);
    }

    static Color Accent => Palettes.Current?.Accent ?? Microsoft.UI.Colors.White;

    sealed record Row(Game Game, GameStats Stats);

    string? _shown;

    /// <summary>Goes up when a game's own cover changed (Edit artwork): the page draws its covers again at its next look.</summary>
    public static int ArtVersion;

    /// <summary>What the page shows, in one string: the profile, the games, their hours and achievements, the latest unlock.</summary>
    static string Key()
    {
        var p = Profiles.Active;
        var (games, _) = AllGames();
        long minutes = 0; int ach = 0, stats = 0;
        foreach (var g in games)
            if (Library.StatsOf(g.Id) is { } st) { minutes += st.Minutes; ach += st.AchUnlocked; stats++; }
        return $"{p?.Id}|{p?.Name}|{p?.Picture}|{AppState.GamesVersion}|{games.Count}|{stats}|{minutes}|{ach}|{Library.Unlocks.FirstOrDefault()?.At:O}|{Palettes.Current?.Key}|{DateTime.Today:yyyyMMdd}|{Accounts()}|{ArtVersion}|{AppState.Settings.HiddenFromStats.Count}|{Names()}";
    }

    /// <summary>The games' own names: a rename shows on the dashboard at once (2.1-rc.10 audit).</summary>
    static int Names()
    {
        var hash = new HashCode();
        foreach (var (id, name) in AppState.Settings.GameNames.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) { hash.Add(id, StringComparer.OrdinalIgnoreCase); hash.Add(name); }
        return hash.ToHashCode();
    }

    /// <summary>Which launchers are signed in: a sign-in or out redraws the Launchers section.</summary>
    static string Accounts() => $"{SteamAccount.SignedIn}{EpicAccount.SignedIn}{GogAccount.SignedIn}{Core.Achievements.XboxAccount.SignedIn}";

    void Fill()
    {
        int run = ++_run;
        _shown = Key();
        _root.Children.Clear();
        var profile = Profiles.Active;
        var (games, installedCount) = AllGames();
        var rows = games.Select(g => (g, s: Library.StatsOf(g.Id))).Where(x => x.s is not null).Select(x => new Row(x.g, x.s!)).ToList();
        _rows = rows;
        _root.Children.Add(Header(profile, games, installedCount, rows, run));
        // The rest in turns, one section per turn of the UI thread.
        var steps = new Queue<(string Name, Func<FrameworkElement?> Make)>();
        steps.Enqueue(("stats", () => Stats(games, installedCount, rows)));
        steps.Enqueue(("activity", () => Activity(run)));
        steps.Enqueue(("played", () => MostPlayed(rows)));
        steps.Enqueue(("progress", () => Progress(rows)));
        steps.Enqueue(("unlocks", LatestUnlocks));
        steps.Enqueue(("launchers", () => Launchers(games, run)));
        void Next()
        {
            if (run != _run || steps.Count == 0) return;
            var (name, make) = steps.Dequeue();
            try
            {
                using var __perf = UiPerf.Scope("ProfilePage." + name);
                if (make() is { } part)
                {
                    _root.Children.Add(part);
                    UiPerf.Layout(part, "ProfilePage." + name);
                    Motion.FadeIn(part, 260);
                }
            }
            catch (Exception ex) { Log.Warn($"Profile page: {ex.Message}"); }
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Next);
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Next);
    }

    // ---------- header ----------

    FrameworkElement Header(UserProfile? profile, List<Game> games, int installedCount, List<Row> rows, int run)
    {
        var root = new Grid { Height = 248, CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x12, 0x16)) };
        var art = new Border { CornerRadius = new CornerRadius(18) };
        root.Children.Add(art);
        // Dark from the left where the name sits, deeper at the foot, the theme's accent glowing in behind the picture.
        var veil = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        veil.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xF0, 0x0A, 0x0A, 0x0E), Offset = 0 });
        veil.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xA0, 0x0A, 0x0A, 0x0E), Offset = 0.5 });
        veil.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x40, 0x0A, 0x0A, 0x0E), Offset = 1 });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(18), Background = veil });
        var a = Accent;
        var wash = new RadialGradientBrush { Center = new(0.12, 0.5), GradientOrigin = new(0.12, 0.5), RadiusX = 0.45, RadiusY = 1.2 };
        wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x66, a.R, a.G, a.B), Offset = 0 });
        wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, a.R, a.G, a.B), Offset = 1 });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(18), Background = wash });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(18), Background = CoverFx.Gloss });

        var row = new Grid { ColumnSpacing = 26, Margin = new Thickness(36, 0, 36, 0), VerticalAlignment = VerticalAlignment.Center, RequestedTheme = ElementTheme.Dark };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The picture on a soft glow of the accent.
        var halo = new RadialGradientBrush();
        halo.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x90, a.R, a.G, a.B), Offset = 0 });
        halo.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, a.R, a.G, a.B), Offset = 1 });
        var avatarHost = new Grid();
        avatarHost.Children.Add(new Border { Width = 200, Height = 200, Margin = new Thickness(-38), Background = halo, IsHitTestVisible = false });
        var avatar = Profiles.Avatar(profile, 124, ring: true);
        avatarHost.Children.Add(avatar);
        row.Children.Add(avatarHost);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
        text.Children.Add(new TextBlock { Text = L.T("PROFILE"), Style = Ui.Style("GroupHeader"), Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)) });
        text.Children.Add(new TextBlock
        {
            Text = profile?.Name ?? L.T("Your profile"), FontSize = 40, FontWeight = FontWeights.Bold, CharacterSpacing = -10,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        long minutes = rows.Sum(r => r.Stats.Minutes);
        var facts = new List<string>();
        if (profile is not null) facts.Add(L.F("Member since {0}", profile.Created.ToLocalTime().ToString("MMMM yyyy")));
        facts.Add(L.N(games.Count, "{0} game", "{0} games"));
        if (games.Count > installedCount) facts.Add(L.F("{0} installed", installedCount));
        if (minutes >= 60) facts.Add(L.F("{0:#,0} h played", Math.Round(minutes / 60.0)));
        text.Children.Add(new TextBlock { Text = string.Join("  ·  ", facts), FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)) });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        // The profile is managed here (2.1-rc.3): edit it, switch to another one when there are several, remove it.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 30) };
        var edit = new Button { Content = profile is null ? L.T("Create your profile") : L.T("Edit profile") };
        if (profile is null) edit.Style = Ui.Style("AccentButtonStyle");
        edit.Click += async (_, _) => { if (XamlRoot is { } r) await Profiles.EditAsync(r, ActualTheme, profile); };
        actions.Children.Add(edit);
        if (profile is not null)
        {
            var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8) };
            ToolTipService.SetToolTip(more, L.T("More"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, L.T("More"));
            var menu = new MenuFlyout();
            menu.Opening += (_, _) =>
            {
                menu.Items.Clear();
                foreach (var other in AppState.Settings.Profiles.Where(x => x != profile).ToList())
                {
                    var item = new MenuFlyoutItem { Text = L.F("Switch to {0}", other.Name), Icon = new FontIcon { Glyph = "" } };
                    item.Click += (_, _) => Profiles.Switch(other);
                    menu.Items.Add(item);
                }
                if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
                var remove = new MenuFlyoutItem { Text = L.F("Remove {0}", profile.Name), Icon = new FontIcon { Glyph = "" } };
                remove.Click += (_, _) => _ = RemoveAsync(profile);
                menu.Items.Add(remove);
            };
            more.Flyout = menu;
            actions.Children.Add(more);
        }
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        root.Children.Add(row);

        // Behind it all: the banner of the game played most (not one hidden from Most played: an app is no banner).
        if (rows.Where(r => !IsHidden(r.Game)).OrderByDescending(r => r.Stats.Minutes).FirstOrDefault() is { Stats.Minutes: > 0 } top)
            _ = LoadArtAsync(top.Game, art, run);
        return root;
    }

    async Task RemoveAsync(UserProfile p)
    {
        if (XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = ActualTheme,
            Title = L.F("Remove {0}?", p.Name),
            Content = L.T("The profile's name and picture are removed. Your games, play time and achievements stay."),
            PrimaryButtonText = L.T("Remove"), CloseButtonText = L.T("Cancel"),
        };
        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
        Profiles.Remove(p);
    }

    async Task LoadArtAsync(Game game, Border target, int run)
    {
        try
        {
            var art = await GameInfo.GetAsync(game);
            if (run != _run || (art.HeroImage ?? art.HeaderImage) is not { } pic || !File.Exists(pic)) return;
            target.Child = Smooth.Art(new BitmapImage { UriSource = new Uri(pic), DecodePixelWidth = 1600 }, target.CornerRadius, fadeMs: 320);
        }
        catch (Exception ex) { Log.Warn($"Profile header art: {ex.Message}"); }
    }

    // ---------- the numbers ----------

    FrameworkElement Stats(List<Game> games, int installedCount, List<Row> rows)
    {
        long minutes = rows.Sum(r => r.Stats.Minutes);
        int got = rows.Sum(r => r.Stats.AchUnlocked), all = rows.Sum(r => r.Stats.AchTotal);
        var withAch = rows.Where(r => r.Stats.AchTotal > 0).ToList();
        int perfect = withAch.Count(r => r.Stats.AchUnlocked >= r.Stats.AchTotal);
        double avg = withAch.Count > 0 ? withAch.Average(r => 100.0 * r.Stats.AchUnlocked / r.Stats.AchTotal) : 0;
        int played = rows.Count(r => r.Stats.Minutes > 0);

        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        var tiles = new List<FrameworkElement>
        {
            StatTile("", L.N(games.Count, "game", "games"), games.Count.ToString("#,0"), games.Count > installedCount ? L.F("{0} installed", installedCount) + "  ·  " + L.N(played, "{0} played", "{0} played") : L.N(played, "{0} played", "{0} played"), Ui.Blue),
            StatTile("", L.T("hours played"), (minutes / 60.0).ToString("#,0"), rows.Count(r => r.Stats.LastPlayed is { } lp && (DateTime.Now - lp).TotalDays < 7) is var week and > 0 ? L.N(week, "{0} game this week", "{0} games this week") : null, Ui.Green),
            StatTile(Icons.TrophyGlyph, L.T("achievements"), got.ToString("#,0"), all > 0 ? L.F("of {0:#,0}", all) : null, Ui.Gold),
            StatTile("", L.T("average completion"), $"{avg:0}%", withAch.Count > 0 ? L.N(withAch.Count, "over {0} game", "over {0} games") : null, Ui.Violet),
            StatTile("platinum", L.T("perfect games"), perfect.ToString(), L.T("every achievement unlocked"), Color.FromArgb(0xFF, 0xB8, 0xE4, 0xF5)),
        };
        void Lay(double w)
        {
            int cols = w >= 1100 ? 5 : w >= 760 ? 3 : 2;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (int i = 0; i < cols; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < (tiles.Count + cols - 1) / cols; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < tiles.Count; i++) { Grid.SetColumn(tiles[i], i % cols); Grid.SetRow(tiles[i], i / cols); }
        }
        foreach (var t in tiles) grid.Children.Add(t);
        // Laid out again only for a real change of width (a scroll bar coming and going must not swing it back and forth).
        double laidFor = 0;
        grid.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - laidFor) > 40) { laidFor = e.NewSize.Width; Lay(laidFor); } };
        Lay(1200);
        return grid;
    }

    static FrameworkElement StatTile(string glyph, string label, string value, string? note, Color tint)
    {
        var wash = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x2E, tint.R, tint.G, tint.B), Offset = 0 });
        wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x08, tint.R, tint.G, tint.B), Offset = 1 });
        var panel = new StackPanel { Spacing = 4 };
        FrameworkElement icon = glyph switch
        {
            Icons.TrophyGlyph => Icons.Trophy(22),
            "platinum" => Icons.PlatinumTrophy(22),
            _ => new FontIcon { Glyph = glyph, FontSize = 20, Foreground = new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Left },
        };
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = value, FontSize = 32, FontWeight = FontWeights.Bold, CharacterSpacing = -10, TextLineBounds = TextLineBounds.TrimToCapHeight });
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        if (note is not null) panel.Children.Add(new TextBlock { Text = note, Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis });
        var tile = new Border { Padding = new Thickness(18, 16, 18, 16), CornerRadius = new CornerRadius(14), Background = wash, Child = panel };
        // A small lift under the pointer.
        tile.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) };
        tile.PointerEntered += (_, _) => tile.Translation = new System.Numerics.Vector3(0, -3, 0);
        tile.PointerExited += (_, _) => tile.Translation = default;
        return tile;
    }

    static FrameworkElement Section(string title, FrameworkElement body, string? right = null, FrameworkElement? action = null)
    {
        var panel = new StackPanel { Spacing = 12 };
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = title, Style = Ui.Style("GroupHeader"), VerticalAlignment = VerticalAlignment.Center });
        var rule = LibraryPage.Divider();
        Grid.SetColumn(rule, 1);
        head.Children.Add(rule);
        if (right is not null)
        {
            var r = new TextBlock { Text = right, Style = Ui.Style("RowDescription"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(r, 2);
            head.Children.Add(r);
        }
        if (action is not null)
        {
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(action, 3);
            head.Children.Add(action);
        }
        panel.Children.Add(head);
        panel.Children.Add(body);
        return panel;
    }

    static Border Card(UIElement child, double padding = 18) => new()
    {
        Padding = new Thickness(padding), CornerRadius = new CornerRadius(14), Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), Child = child,
    };

    // ---------- activity ----------

    FrameworkElement Activity(int run)
    {
        var bars = new Grid { Height = 150, ColumnSpacing = 6 };
        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(bars);
        var labels = new Grid { ColumnSpacing = 6 };
        host.Children.Add(labels);
        var section = Section(L.T("THE LAST TWO WEEKS"), Card(host), action: SeeAll(ShowTwoWeeks));
        _ = FillActivityAsync(bars, labels, section, run);
        return section;
    }

    async Task FillActivityAsync(Grid bars, Grid labels, FrameworkElement section, int run)
    {
        const int Days = 14;
        var today = DateTime.Now.Date;
        double[] minutes = new double[Days];
        try
        {
            var sessions = await Task.Run(SessionLog.Everything);
            foreach (var s in sessions)
            {
                // Calendar days, not hours / 24 (2.1-rc.10 audit: the 23-hour day of the clock change merged two days).
                int i = DateOnly.FromDateTime(today).DayNumber - DateOnly.FromDateTime(s.Start.ToLocalTime()).DayNumber;
                if (i is >= 0 and < Days) minutes[Days - 1 - i] += s.Minutes;
            }
        }
        catch (Exception ex) { Log.Warn($"Profile activity: {ex.Message}"); }
        if (run != _run) return;
        double max = Math.Max(30, minutes.Max());
        var a = Accent;
        var fill = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        fill.GradientStops.Add(new GradientStop { Color = a, Offset = 0 });
        fill.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x60, a.R, a.G, a.B), Offset = 1 });
        for (int i = 0; i < Days; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var day = today.AddDays(i - Days + 1);
            var track = new Grid { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
            var bar = new Border { VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(6), Background = fill, Height = 0 };
            double m = minutes[i], share = m / max;
            track.Children.Add(bar);
            track.SizeChanged += (_, e) => bar.Height = m <= 0 ? 0 : Math.Max(6, e.NewSize.Height * share);
            ToolTipService.SetToolTip(track, $"{day:dddd d MMM}\n{(m < 1 ? L.T("Not played") : Library.ShortTime((long)Math.Round(m)))}");
            Grid.SetColumn(track, i);
            bars.Children.Add(track);
            var label = new TextBlock { Text = day.ToString("ddd")[..1].ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Center, Style = Ui.Style("RowDescription"), Opacity = day == today ? 1 : 0.7 };
            if (day == today) label.FontWeight = FontWeights.Bold;
            Grid.SetColumn(label, i);
            labels.Children.Add(label);
        }
        double total = minutes.Sum();
        if (section is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is Grid head && total >= 1)
        {
            var r = new TextBlock { Text = L.F("{0} in 14 days", Library.ShortTime((long)Math.Round(total))), Style = Ui.Style("RowDescription"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(r, 2);
            head.Children.Add(r);
        }
    }

    // ---------- most played ----------

    /// <summary>
    /// The games played most (2.1-rc.4): one row that fills the page's width, up to eight covers (fewer on a narrow window,
    /// each as wide as the row allows), "See all" for the whole list. Right-click hides an app that is not a game.
    /// </summary>
    FrameworkElement? MostPlayed(List<Row> rows)
    {
        var top = rows.Where(r => r.Stats.Minutes > 0 && !IsHidden(r.Game)).OrderByDescending(r => r.Stats.Minutes).Take(8).ToList();
        if (top.Count == 0) return null;
        long most = top[0].Stats.Minutes;
        const double Gap = 18, MinW = 128;
        var grid = new Grid { ColumnSpacing = Gap };
        var arts = new List<(FrameworkElement Tile, Grid Art)>();
        for (int i = 0; i < top.Count; i++)
        {
            var r = top[i];
            var cover = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1C, 0x26)) };
            var art = new Grid();
            art.Children.Add(cover);
            art.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = CoverFx.Floor, IsHitTestVisible = false });
            var rank = new Border
            {
                Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(8), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x14)),
                Child = new TextBlock { Text = $"#{i + 1}", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            };
            art.Children.Add(rank);
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(art);
            panel.Children.Add(new TextBlock { Text = r.Game.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(Bar(r.Stats.Minutes / (double)most, Accent));
            var hours = new TextBlock { Text = Library.ShortTime(r.Stats.Minutes), Style = Ui.Style("RowDescription") };
            Grid.SetColumn(hours, 1);
            line.Children.Add(hours);
            panel.Children.Add(line);
            var button = new Button
            {
                Content = panel, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var g = r.Game;
            button.Click += (_, _) => Open(g);
            AttachHideMenu(button, g);
            // A lift instead of a zoom: a scaled cover is drawn from its bitmap at the old size while it grows (blurred).
            art.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) };
            button.PointerEntered += (_, _) => art.Translation = new System.Numerics.Vector3(0, -4, 0);
            button.PointerExited += (_, _) => art.Translation = default;
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
            arts.Add((button, art));
            _ = LoadCoverAsync(r.Game, cover);
        }
        // As many columns as fit at a readable size, up to eight, the covers sharing the row; laid out again only for a real
        // change of width (a height worked out from the width must not swing with the scroll bar).
        double laidFor = 0;
        void Lay(double w)
        {
            int cols = Math.Clamp((int)((w + Gap) / (MinW + Gap)), 3, 8);
            double tw = Math.Floor((w - (cols - 1) * Gap) / cols);
            grid.ColumnDefinitions.Clear();
            for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < arts.Count; i++)
            {
                arts[i].Tile.Visibility = i < cols ? Visibility.Visible : Visibility.Collapsed;
                arts[i].Art.Height = Math.Round(tw * 1.5);
            }
        }
        // A width that only swings back to the one before (a scroll bar coming and going as the row's height changes) is
        // not followed: that swing never settled once (crashes.md, "Profile page froze the window").
        double before = -1;
        grid.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width;
            if (Math.Abs(w - laidFor) < 1 || Math.Abs(w - before) < 1 && Math.Abs(w - laidFor) < 24) return;
            before = laidFor;
            laidFor = w;
            Lay(w);
        };
        Lay(1100);
        int played = rows.Count(r => r.Stats.Minutes > 0 && !IsHidden(r.Game));
        return Section(L.T("MOST PLAYED"), grid, action: played > top.Count || rows.Any(r => IsHidden(r.Game)) ? SeeAll(ShowMostPlayed) : null);
    }

    static async Task LoadCoverAsync(Game game, Border target, int decode = 400)
    {
        try
        {
            var path = ArtOverrides.Get(game.Id, ArtKind.Cover) ?? await GameInfo.GetCoverAsync(game);
            // No XamlRoot check here (2.1-rc.11): a cover already in the cache answers before its row is on the page, and
            // rc.10's "no longer on the page" check dropped exactly those (Most played showed empty tiles). A Border that left
            // the page meanwhile never draws, so its picture is never decoded: setting it costs nothing.
            if (path is null) return;
            bool portrait = ArtOverrides.Get(game.Id, ArtKind.Cover) is not null || path.EndsWith("-cover.jpg", StringComparison.OrdinalIgnoreCase);
            var source = new BitmapImage { UriSource = new Uri(path), DecodePixelWidth = decode };
            if (portrait) { target.Child = Smooth.Art(source, target.CornerRadius, Stretch.UniformToFill, fadeMs: 240); return; }
            // A landscape header (no portrait capsule): whole and centred, over a dim, filled copy of itself, so the cover has
            // no empty band (2.1-rc.3: it sat at the top of a black tile).
            var layers = new Grid();
            var back = Smooth.Art(source, target.CornerRadius, Stretch.UniformToFill, fadeMs: 240);
            layers.Children.Add(back);
            layers.Children.Add(new Border { CornerRadius = target.CornerRadius, Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x0C, 0x0C, 0x10)) });
            // A picture of its own for the second layer: one BitmapImage shown by two elements misbehaves in WinUI.
            var front = new BitmapImage { UriSource = new Uri(path), DecodePixelWidth = decode };
            layers.Children.Add(Smooth.Art(front, new CornerRadius(0), Stretch.Uniform, fadeMs: 240));
            target.Child = layers;
        }
        catch (Exception ex) { Log.Warn($"Profile cover of {game.Name}: {ex.Message}"); }
    }

    static FrameworkElement Bar(double share, Color tint)
    {
        var track = new Grid { Height = 5, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x90, tint.R, tint.G, tint.B), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = tint, Offset = 1 });
        var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2.5), Background = brush };
        track.Children.Add(fill);
        track.SizeChanged += (_, e) => fill.Width = Math.Max(share > 0 ? 5 : 0, e.NewSize.Width * Math.Clamp(share, 0, 1));
        return track;
    }

    // ---------- achievements ----------

    FrameworkElement? Progress(List<Row> rows)
    {
        var all = rows.Where(r => r.Stats.AchTotal > 0 && !IsHidden(r.Game)).ToList();
        var list = all.OrderByDescending(r => (double)r.Stats.AchUnlocked / r.Stats.AchTotal).ThenByDescending(r => r.Stats.AchUnlocked).Take(10).ToList();
        if (list.Count == 0) return null;
        var gold = Ui.Gold;
        var panel = new StackPanel { Spacing = 12 };
        foreach (var r in list)
        {
            double share = (double)r.Stats.AchUnlocked / r.Stats.AchTotal;
            var line = new Grid { ColumnSpacing = 14 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (share >= 1) name.Children.Add(Icons.PlatinumTrophy(16));
            name.Children.Add(new TextBlock { Text = r.Game.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 196 });
            line.Children.Add(name);
            var bar = Bar(share, share >= 1 ? Color.FromArgb(0xFF, 0xB8, 0xE4, 0xF5) : gold);
            Grid.SetColumn(bar, 1);
            line.Children.Add(bar);
            var count = new TextBlock { Text = $"{r.Stats.AchUnlocked} / {r.Stats.AchTotal}  ·  {share * 100:0}%", Style = Ui.Style("RowDescription"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(count, 2);
            line.Children.Add(count);
            var button = new Button { Content = line, Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var g = r.Game;
            button.Click += (_, _) => Open(g);
            panel.Children.Add(button);
        }
        int perfect = rows.Count(r => r.Stats.AchTotal > 0 && r.Stats.AchUnlocked >= r.Stats.AchTotal);
        return Section(L.T("ACHIEVEMENT PROGRESS"), Card(panel, 12), perfect > 0 ? L.N(perfect, "{0} perfect game", "{0} perfect games") : null,
            all.Count > list.Count ? SeeAll(ShowProgress) : null);
    }

    FrameworkElement? LatestUnlocks()
    {
        var unlocks = Library.Unlocks.Take(8).ToList();
        if (unlocks.Count == 0) return null;
        var gold = Ui.Gold;
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < (unlocks.Count + 1) / 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < unlocks.Count; i++)
        {
            var u = unlocks[i];
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement pic = Icons.Trophy(26);
            if (u.Item.IconUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                pic = Smooth.Art(new BitmapImage { UriSource = uri, DecodePixelWidth = 96 }, new CornerRadius(8));
            line.Children.Add(new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(8), Child = pic });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            text.Children.Add(new TextBlock { Text = u.Item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{u.Game.Title}  ·  {u.At:d MMM}", Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            var wash = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x26, gold.R, gold.G, gold.B), Offset = 0 });
            wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x06, gold.R, gold.G, gold.B), Offset = 1 });
            var tile = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(12), Background = wash, Child = line };
            ToolTipService.SetToolTip(tile, u.Item.Description.Length > 0 ? $"{u.Item.Name}\n{u.Item.Description}" : u.Item.Name);
            Grid.SetColumn(tile, i % 2);
            Grid.SetRow(tile, i / 2);
            grid.Children.Add(tile);
        }
        int week = Library.UnlocksThisWeek + Library.OwnedUnlocksThisWeek;
        return Section(L.T("LATEST ACHIEVEMENTS"), grid, week > 0 ? L.N(week, "{0} this week", "{0} this week") : null);
    }

    // ---------- launchers ----------

    /// <summary>
    /// Every launcher (2.1-rc.3): its games here (owned and installed), and whether Hybrid Launcher is signed in to it. One
    /// it can sign in to but is not invites to sign in (a click opens its sign-in), so the whole library and its hours and
    /// achievements show; the launchers whose library stays online (Ubisoft, EA, Rockstar) say they list installed games only.
    /// </summary>
    FrameworkElement? Launchers(List<Game> games, int run)
    {
        var byStore = games.GroupBy(g => StoreLogos.GroupOf(g).Key).ToDictionary(g => g.Key, g => g.ToList());
        var stores = new[] { GameStore.Steam, GameStore.Epic, GameStore.Gog, GameStore.Xbox, GameStore.Ubisoft, GameStore.Ea, GameStore.Rockstar };
        var wrap = new WrapPanel(12, []);
        foreach (var store in stores)
        {
            var list = byStore.GetValueOrDefault("store:" + store) ?? [];
            bool signable = store is GameStore.Steam or GameStore.Epic or GameStore.Gog or GameStore.Xbox;
            bool signedIn = store switch
            {
                GameStore.Steam => SteamAccount.SignedIn,
                GameStore.Epic => EpicAccount.SignedIn,
                GameStore.Gog => GogAccount.SignedIn,
                GameStore.Xbox => Core.Achievements.XboxAccount.SignedIn,
                _ => false,
            };
            // A launcher with nothing here and nothing to sign in to is left out.
            if (list.Count == 0 && !signable) continue;
            int installed = list.Count(g => !g.NotInstalled);
            string who = store switch
            {
                GameStore.Steam => AppState.Settings.Accounts.SteamName ?? "",
                GameStore.Epic => AppState.Settings.Accounts.EpicName ?? "",
                GameStore.Gog => AppState.Settings.Accounts.GogName ?? "",
                GameStore.Xbox => Core.Achievements.XboxAccount.Gamertag ?? "",
                _ => "",
            };
            string count = list.Count == 0 ? L.T("No games yet")
                : list.Count > installed ? L.F("{0} games, {1} installed", list.Count, installed) : L.N(list.Count, "{0} game", "{0} games");
            string note = signedIn ? (who.Length > 0 ? L.F("Signed in as {0}", who) : L.T("Signed in"))
                : signable ? store == GameStore.Xbox ? L.T("Sign in to see your Xbox achievements") : L.T("Sign in to see your whole library")
                : L.T("Installed games only");
            wrap.Children.Add(LauncherTile(store, StoreLogos.Label(store), count, note, signedIn, signable && !signedIn ? store : null));
        }
        // Games folders and games added by hand.
        foreach (var (key, list) in byStore.Where(k => !k.Key.StartsWith("store:", StringComparison.Ordinal) || k.Key == "store:" + GameStore.Custom))
        {
            var g = StoreLogos.GroupOf(list[0]);
            wrap.Children.Add(LauncherTile(list[0].Store, g.Label, L.N(list.Count, "{0} game", "{0} games"), null, false, null, g.Folder is not null));
        }
        return wrap.Children.Count == 0 ? null : Section(L.T("LAUNCHERS"), wrap);
    }

    bool _signingIn;

    FrameworkElement LauncherTile(GameStore store, string label, string count, string? note, bool signedIn, GameStore? signIn, bool folder = false)
    {
        var row = new Grid { ColumnSpacing = 12, MinWidth = 230 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var logo = StoreLogos.Tile(store, folder, 34);
        logo.VerticalAlignment = VerticalAlignment.Center;
        if (signIn is not null) logo.Opacity = 0.55;
        row.Children.Add(logo);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = count, Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (note is not null)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 2, 0, 0) };
            if (signedIn) line.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Foreground = Ui.Brush("SystemFillColorSuccessBrush"), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock
            {
                Text = note, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = signedIn ? Ui.Brush("SystemFillColorSuccessBrush") : signIn is not null ? Ui.Brush("AccentTextFillColorPrimaryBrush") : Ui.Brush("TextFillColorTertiaryBrush"),
            });
            text.Children.Add(line);
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (signIn is not { } target) return Card(row, 12);
        // Not signed in: the whole tile is the invitation, a click opens the launcher's sign-in.
        var button = new Button
        {
            Content = row, Padding = new Thickness(12), CornerRadius = new CornerRadius(14), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, Accent.R, Accent.G, Accent.B)),
            Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"),
        };
        ToolTipService.SetToolTip(button, L.F("Sign in to {0}", label));
        button.Click += async (_, _) =>
        {
            // One sign-in at a time (a double click opened two dialogs and ended the app, 2.1-rc.3 audit).
            if (XamlRoot is not { } root || _signingIn) return;
            _signingIn = true;
            try
            {
                string? done = target == GameStore.Xbox ? await AccountSignIn.XboxAsync(ActualTheme) : await AccountSignIn.RunAsync(target, root, ActualTheme);
                if (done is null) return;
                await OwnedLibrary.LoadAsync(force: true);
                App.Window?.RefreshList();
                Fill();
                _ = Library.LoadOwnedStatsAsync(DispatcherQueue);
            }
            catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
            finally { _signingIn = false; }
        };
        return button;
    }
}
