using HybridLauncher.Core;
using HybridLauncher.Core.Media;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HybridLauncher.Views;

/// <summary>
/// The Library's top section: the game played last on its own banner (its art, title logo, when it was played, the hours,
/// the achievements, Play; a click anywhere else on it opens the game), and beside it the achievements unlocked last over
/// every game (2.0). Read from the library stats (Library.StatsOf, Library.Unlocks), so it fills in once they are read;
/// hidden when no game has been played or while searching. The line under the page's title sums the library up.
/// </summary>
public sealed partial class LibraryPage
{
    const double HeroHeight = 264;
    int _recentRun;

    /// <summary>The game played last (games without a last-played date are left out).</summary>
    static (Game Game, GameStats Stats)? LastPlayed() => AppState.VisibleGames
        .Select(g => (Game: g, Stats: Library.StatsOf(g.Id)))
        .Where(x => x.Stats?.LastPlayed is not null && !x.Game.NotInstalled)
        .OrderByDescending(x => x.Stats!.LastPlayed)
        .Select(x => ((Game, GameStats)?)(x.Game, x.Stats!))
        .FirstOrDefault();

    /// <summary>"331 h played · 65 of 520 achievements": the whole library in one line, under the title. The number of games
    /// is only on the covers' bar (CountText), not here as well.</summary>
    void FillSummary()
    {
        var games = AppState.VisibleGames.Where(g => !g.NotInstalled).ToList();
        var stats = games.Select(g => Library.StatsOf(g.Id)).Where(s => s is not null).Select(s => s!).ToList();
        var parts = new List<string>();
        long minutes = stats.Sum(s => s.Minutes);
        if (minutes >= 60) parts.Add(L.F("{0:#,0} h played", Math.Round(minutes / 60.0)));
        else if (minutes > 0) parts.Add(L.N((int)minutes, "{0} minute played", "{0} minutes played"));
        int total = stats.Sum(s => s.AchTotal);
        if (total > 0) parts.Add(L.F("{0:#,0} of {1:#,0} achievements", stats.Sum(s => s.AchUnlocked), total));
        SummaryText.Text = string.Join("  ·  ", parts);
        SummaryText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void FillRecent()
    {
        int run = ++_recentRun;
        FillSummary();
        RecentSection.Children.Clear();
        var last = _filter.Length > 0 ? null : LastPlayed();
        if (last is not { } played) { RecentSection.Visibility = Visibility.Collapsed; return; }
        RecentSection.Visibility = Visibility.Visible;

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300, MaxWidth = 520 });
        grid.Children.Add(Hero(played.Game, played.Stats, run));
        var unlocks = UnlocksCard();
        Grid.SetColumn(unlocks, 1);
        grid.Children.Add(unlocks);
        // Narrow: the banner alone.
        void Fit(double w)
        {
            bool narrow = w < 860;
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].MinWidth = narrow ? 0 : 300;
            grid.ColumnSpacing = narrow ? 0 : 16;
            unlocks.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        }
        grid.SizeChanged += (_, e) => Fit(e.NewSize.Width);
        RecentSection.Children.Add(grid);
    }

    static ControlTemplate? _bare;

    /// <summary>A button that draws only its content (the banner, an achievement's row): the content is the whole surface.</summary>
    static ControlTemplate BareButton => _bare ??= (ControlTemplate)XamlReader.Load(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<ContentPresenter Content='{TemplateBinding Content}' Background='{TemplateBinding Background}' CornerRadius='{TemplateBinding CornerRadius}' " +
        "Padding='{TemplateBinding Padding}' HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch' />" +
        "</ControlTemplate>");

    /// <summary>The last played game's banner: its wide art, the logo (or the title), the facts and Play. A click on it opens the game.</summary>
    FrameworkElement Hero(Game game, GameStats stats, int run)
    {
        var root = new Grid { Height = HeroHeight, CornerRadius = new CornerRadius(12), Background = Ui.Brush("ControlFillColorSecondaryBrush") };
        var art = new Border { CornerRadius = new CornerRadius(12) };
        root.Children.Add(art);
        // Dark from the left, where the text sits; the right shows the picture.
        var veil = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        veil.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xE6, 0x0C, 0x0C, 0x10), Offset = 0 });
        veil.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x99, 0x0C, 0x0C, 0x10), Offset = 0.45 });
        veil.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0x0C, 0x0C, 0x10), Offset = 0.85 });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = veil, IsHitTestVisible = false });
        // 2.1: the foot deeper too, and the theme's accent glowing in from the lower left, so the text sits on depth and colour.
        var foot = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        foot.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0, 0, 0), Offset = 0.45 });
        foot.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x9A, 0, 0, 0), Offset = 1 });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = foot, IsHitTestVisible = false });
        var tone = Palettes.Current?.Accent ?? Microsoft.UI.Colors.White;
        var wash = new RadialGradientBrush { Center = new Windows.Foundation.Point(0.05, 1.1), GradientOrigin = new Windows.Foundation.Point(0.05, 1.1), RadiusX = 0.6, RadiusY = 1.1 };
        wash.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x55, tone.R, tone.G, tone.B), Offset = 0 });
        wash.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, tone.R, tone.G, tone.B), Offset = 1 });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = wash, IsHitTestVisible = false });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = Helpers.CoverFx.Gloss, IsHitTestVisible = false });

        // What the banner is, in its corner: no heading above it takes a line of the page.
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(28, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        label.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)) });
        label.Children.Add(new TextBlock { Text = L.T("LAST PLAYED"), Style = Ui.Style("GroupHeader"), Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(label);

        var text = new StackPanel { Spacing = 8, Margin = new Thickness(28, 52, 28, 22), VerticalAlignment = VerticalAlignment.Bottom, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, RequestedTheme = ElementTheme.Dark };
        var logoHost = new Grid { MinHeight = 0 };
        var title = new TextBlock { Text = game.Title, FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        logoHost.Children.Add(title);
        text.Children.Add(logoHost);

        var facts = new List<string> { L.F("Played {0}", Ago(stats.LastPlayed!.Value)) };
        if (stats.Minutes > 0) facts.Add(Hours(stats.Minutes));
        facts.Add(StoreLogos.GroupOf(game).Label);
        text.Children.Add(new TextBlock { Text = string.Join("  ·  ", facts), Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)), FontSize = 13 });
        if (stats.AchTotal > 0)
        {
            var ach = new Grid { ColumnSpacing = 10, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left };
            ach.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ach.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ach.Children.Add(new TextBlock { Text = L.F("{0} / {1} achievements", stats.AchUnlocked, stats.AchTotal), FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
            var bar = new ProgressBar { Maximum = stats.AchTotal, Value = stats.AchUnlocked, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(bar, 1);
            ach.Children.Add(bar);
            text.Children.Add(ach);
        }
        // Play, Stop while the game runs: the game page's button in small (Controls.LaunchChip). Its click is its own: the
        // banner under it does not open the page.
        var play = new Controls.LaunchChip(game, withLabel: true) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        text.Children.Add(play);
        root.Children.Add(text);

        // The whole banner opens the game's page (the Game page button it had is gone). Under the pointer the picture
        // comes a little closer, inside the banner's own shape.
        var card = new Button
        {
            // Transparent (2.0.0): the Button style's own fill sat under the banner and showed as a light rim on its rounded edge.
            Content = root, Template = BareButton, Padding = new Thickness(0), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            UseSystemFocusVisuals = true, FocusVisualMargin = new Thickness(-3),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, L.F("Open {0}", game.Title));
        card.Click += (_, _) => App.Window?.OpenGame(game.Id);
        art.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(420) };
        // One timer for the card (2.1 audit, as CoverFx.Sharp): every pointer-out used to make a new one.
        Microsoft.UI.Dispatching.DispatcherQueueTimer? rest = null;
        void Near(bool on)
        {
            art.CenterPoint = new System.Numerics.Vector3((float)art.ActualWidth * 0.7f, (float)art.ActualHeight / 2, 0);
            float s = on ? 1.03f : 1f;
            rest?.Stop();
            // Drawn at the near size from the start, so the picture is never stretched blurred while it moves (2.1-rc.4);
            // back at its own size once it has settled.
            if (on && art.XamlRoot is { } r) art.RasterizationScale = r.RasterizationScale * 1.03;
            else if (!on)
            {
                rest ??= WeakTimer.Create(art.DispatcherQueue, art, TimeSpan.FromMilliseconds(480), static a =>
                {
                    if (a.Scale.X <= 1.0001f && a.XamlRoot is { } root) a.RasterizationScale = root.RasterizationScale;
                }, repeating: false);
                rest.Start();
            }
            art.Scale = new System.Numerics.Vector3(s, s, 1);
        }
        card.PointerEntered += (_, _) => Near(true);
        card.PointerExited += (_, _) => Near(false);
        card.PointerCanceled += (_, _) => Near(false);
        card.PointerCaptureLost += (_, _) => Near(false);

        _ = LoadHeroArtAsync(game, art, logoHost, title, run);
        return card;
    }

    async Task LoadHeroArtAsync(Game game, Border art, Grid logoHost, TextBlock title, int run)
    {
        try
        {
            var a = await GameInfo.GetAsync(game);
            if (run != _recentRun) return;
            var picture = a.HeroImage ?? a.HeaderImage;
            if (picture is not null && File.Exists(picture))
            {
                var bmp = new BitmapImage { DecodePixelWidth = 1600, UriSource = new Uri(picture) };
                // The grey fill under the banner goes once the picture is in (2.0.0): two rounded fills on one edge left a light
                // rim around the corners.
                art.Child = Smooth.Art(bmp, art.CornerRadius, fadeMs: 220, opened: () => { if (art.Parent is Panel host) host.Background = null; });
            }
            if (a.LogoImage is { } logo && File.Exists(logo))
            {
                // The logo replaces the title at once (2.1-rc.3: a logo decoded before raised no ImageOpened, and the title
                // stayed written over it); the title comes back only if the picture cannot be read.
                var img = new Image { Source = new BitmapImage { UriSource = new Uri(logo), DecodePixelHeight = 160 }, MaxHeight = 80, MaxWidth = 340, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                img.ImageFailed += (_, _) => { title.Visibility = Visibility.Visible; img.Visibility = Visibility.Collapsed; };
                title.Visibility = Visibility.Collapsed;
                logoHost.Children.Add(img);
            }
        }
        catch (Exception ex) { Log.Warn($"Library last played art for {game.Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Beside the banner: the achievements unlocked last, whatever the game (its picture, its name, the game and when, how
    /// rare it is). A row opens that achievement on its game's page.
    /// </summary>
    FrameworkElement UnlocksCard()
    {
        var root = new Grid { RowSpacing = 6 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { ColumnSpacing = 8, Margin = new Thickness(6, 0, 6, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Icons.Trophy(14));
        var headText = new TextBlock { Text = L.T("LATEST ACHIEVEMENTS"), Style = Ui.Style("GroupHeader"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(headText, 1);
        head.Children.Add(headText);
        root.Children.Add(head);

        var unlocks = Library.Unlocks;
        if (unlocks.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = L.T("The achievements you unlock show here, from every game."),
                Style = Ui.Style("RowDescription"), Margin = new Thickness(6, 2, 6, 0), TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(empty, 1);
            root.Children.Add(empty);
        }
        else
        {
            // How many this week over every game, on the heading's right (counted from all of them, not only those shown).
            // The owned games' unlocks are in the list above: the week's count takes them too (as the profile does).
            int week = Library.UnlocksThisWeek + Library.OwnedUnlocksThisWeek;
            if (week > 0)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center,
                    Background = Ui.Brush("SubtleFillColorSecondaryBrush"),
                    Child = new TextBlock { Text = L.F("{0} this week", week), FontSize = 12, Opacity = 0.85 },
                };
                Grid.SetColumn(pill, 2);
                head.Children.Add(pill);
            }
            // Grouped by game, newest first: the game's cover and name once, its unlocks under it. As many as the card holds;
            // a game's heading is never left alone at the foot.
            var list = new StackPanel();
            Game? last = null;
            foreach (var u in unlocks.Take(10))
            {
                if (!ReferenceEquals(u.Game, last)) { list.Children.Add(UnlockGameHead(u.Game)); last = u.Game; }
                list.Children.Add(UnlockRow(u));
            }
            var host = new Grid();
            host.Children.Add(list);
            void Fit(double height)
            {
                double used = 0;
                bool full = false;
                foreach (FrameworkElement el in list.Children)
                {
                    bool isHead = el.Tag is "head";
                    double h = isHead ? GameHeadHeight : UnlockRowHeight;
                    bool fits = !full && used + h + (isHead ? UnlockRowHeight : 0) <= height + 0.5;
                    if (!fits) full = true;
                    el.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
                    if (fits) used += h;
                }
            }
            host.SizeChanged += (_, e) => Fit(e.NewSize.Height);
            Grid.SetRow(host, 1);
            root.Children.Add(host);
        }
        return new Border
        {
            Height = HeroHeight, CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 14, 8, 8),
            Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), Child = root,
        };
    }

    const double GameHeadHeight = 26, UnlockRowHeight = 46;

    /// <summary>A game's line over its unlocks in the Latest achievements card: its small cover and its name.</summary>
    static FrameworkElement UnlockGameHead(Game game)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = GameHeadHeight, Padding = new Thickness(6, 4, 6, 2), Tag = "head" };
        var thumb = new Border { Width = 12, Height = 18, CornerRadius = new CornerRadius(2), Background = Ui.Brush("ControlFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        if (Covers.TryGetValue(game.Id, out var known)) thumb.Child = Smooth.Art(known, thumb.CornerRadius);
        // Not decoded yet (its tile not drawn, the library scrolled or filtered): read on its own, or the line kept an empty
        // grey box (2.0.0-rc.10). A game without a cover shows its exe's icon.
        else _ = FillThumbAsync(thumb, game);
        line.Children.Add(thumb);
        line.Children.Add(new TextBlock
        {
            Text = game.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380,
        });
        return line;
    }

    static async Task FillThumbAsync(Border thumb, Game game)
    {
        try
        {
            var own = AppState.Settings.Games.TryGetValue(game.Id, out var gs) && gs.CustomCover is { } c && File.Exists(c) ? c : null;
            var path = own ?? await GameInfo.GetCoverAsync(game, CancellationToken.None);
            if (thumb.Child is not null) return;
            if (path is not null)
            {
                thumb.Child = Smooth.Art(new BitmapImage { DecodePixelWidth = 36, UriSource = new Uri(path) }, thumb.CornerRadius);
                return;
            }
            foreach (var source in new[] { ArtOverrides.Get(game.Id, ArtKind.Icon), game.IconPath, game.MainExe, game.StoreExe }.Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase))
                if (await IconCache.GetAsync(source!) is { } icon)
                {
                    if (thumb.Child is null) thumb.Child = new Image { Source = icon, Stretch = Stretch.Uniform };
                    return;
                }
        }
        catch (Exception ex) { Log.Warn($"Latest achievements: cover of {game.Name}: {ex.Message}"); }
    }

    /// <summary>The colour of an achievement's rarity tier.</summary>
    static Windows.UI.Color TierColor(string tier) => tier switch
    {
        "Gold" => Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xB8, 0x4A),
        "Silver" => Windows.UI.Color.FromArgb(0xFF, 0xB8, 0xC2, 0xD0),
        _ => Windows.UI.Color.FromArgb(0xFF, 0xC9, 0x8A, 0x5A),
    };

    FrameworkElement UnlockRow(RecentUnlock u)
    {
        var a = u.Item;
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string tier = a.GlobalPercent switch { null or >= 30 => "Bronze", >= 10 => "Silver", _ => "Gold" };
        var tint = TierColor(tier);
        var icon = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center,
            Background = Ui.Brush("ControlFillColorSecondaryBrush"),
            // A thin ring of its tier's colour.
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, tint.R, tint.G, tint.B)), BorderThickness = new Thickness(1.5),
        };
        FrameworkElement trophy = Icons.TierTrophy(20, tier);
        trophy.HorizontalAlignment = HorizontalAlignment.Center;
        trophy.VerticalAlignment = VerticalAlignment.Center;
        // The tier's trophy under the achievement's picture: a picture that never arrives (or never says so) leaves the
        // trophy, not an empty box (2.1-rc.3).
        var layers = new Grid();
        layers.Children.Add(trophy);
        icon.Child = layers;
        if (a.IconUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            try
            {
                var pic = new Image { Source = new BitmapImage { DecodePixelWidth = 72, UriSource = uri }, Stretch = Stretch.UniformToFill };
                pic.ImageOpened += (_, _) => trophy.Visibility = Visibility.Collapsed;
                pic.ImageFailed += (_, _) => pic.Visibility = Visibility.Collapsed;
                layers.Children.Add(new Border { CornerRadius = new CornerRadius(5), Child = pic });
            }
            catch (Exception ex) { Log.Warn($"Library unlock picture: {ex.Message}"); }
        }
        grid.Children.Add(icon);

        // Its name, and what it asked for (its description) under it.
        string when = Ago(u.At);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = a.Name, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        if (a.Description.Length > 0)
            text.Children.Add(new TextBlock { Text = a.Description, Style = Ui.Style("RowDescription"), FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // On the right: when, and how rare as a pill of its tier's colour.
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
        side.Children.Add(new TextBlock { Text = when, FontSize = 11.5, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.NoWrap });
        if (a.GlobalPercent is { } share)
        {
            side.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 0, 6, 1), HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, tint.R, tint.G, tint.B)),
                Child = new TextBlock { Text = $"{share:0.#}%", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(tint) },
            });
        }
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);

        var row = new Button
        {
            Content = grid, Template = BareButton, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = UnlockRowHeight, Padding = new Thickness(6, 4, 8, 4), CornerRadius = new CornerRadius(9), UseSystemFocusVisuals = true,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{a.Name}: {u.Game.Title}");
        void Over(bool on) => row.Background = on ? Ui.Brush("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.PointerEntered += (_, _) => Over(true);
        row.PointerExited += (_, _) => Over(false);
        row.PointerCanceled += (_, _) => Over(false);
        row.PointerCaptureLost += (_, _) => Over(false);
        row.Click += (_, _) => App.Window?.OpenAchievement(u.Game.Id, a.Key);
        return row;
    }

    /// <summary>Starts a game from the Library (its cover's Play, the Last played banner), as Play on its page does.</summary>
    internal static async void Launch(Game game)
    {
        try
        {
            if (GameSessions.IsActive(game.Id)) { App.Window?.ShowInfo(L.F("{0} is already running.", game.Title)); return; }
            await AppState.LaunchAsync(game);
        }
        catch (Exception ex) { App.Window?.ShowError(L.F("Could not start {0}", game.Title), ex.Message, ex); }
    }

    static string Hours(long minutes) => minutes < 60 ? L.N((int)minutes, "{0} minute", "{0} minutes") : L.F("{0:0.#} h played", minutes / 60.0);

    /// <summary>"today", "yesterday", "3 days ago", "on 4 Oct".</summary>
    static string Ago(DateTime when)
    {
        var days = (DateTime.Now.Date - when.Date).Days;
        return days <= 0 ? L.T("today") : days == 1 ? L.T("yesterday") : days < 14 ? L.F("{0} days ago", days)
            : when.Year == DateTime.Now.Year ? L.F("on {0:d MMM}", when) : L.F("on {0:d MMM yyyy}", when);
    }
}
