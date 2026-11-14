using HybridLauncher.Core;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The dashboard's "See all" lists (2.1-rc.4): Most played, the last two weeks and the achievements' progress each show a few
/// games on the page and the whole list, in order, in a panel over it. The list is an ItemsRepeater: only the rows on screen
/// are made (and load their cover), so a library of hundreds of games opens as fast as a screenful.
/// </summary>
public sealed partial class ProfilePage
{
    /// <summary>One line of a full list: the game, its rank, the bar's share and colour, the value on the right, a note under the name.</summary>
    sealed record ListRow(Game Game, int Rank, double Share, Color Tint, string Value, string? Note, bool Hidden = false, bool Platinum = false);

    Grid? _overlay;
    ScrollView? _overlayScroll;

    /// <summary>Opens the full list over the page. Esc, the close button or a click beside the panel closes it.</summary>
    void ShowList(string title, string? subtitle, List<ListRow> rows, bool hideMenu, double? keepOffset = null)
    {
        CloseList();
        var scrim = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x99, 0x05, 0x05, 0x08)) };
        scrim.PointerPressed += (_, e) => { if (ReferenceEquals(e.OriginalSource, scrim)) CloseList(); };

        var card = new Grid
        {
            MaxWidth = 760, Margin = new Thickness(28, 36, 28, 36), CornerRadius = new CornerRadius(18),
            Background = Ui.Brush("SolidBackgroundFillColorSecondaryBrush"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Shadow = new ThemeShadow(), Translation = new System.Numerics.Vector3(0, 0, 32),
        };
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid { Padding = new Thickness(22, 18, 14, 12), ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        if (subtitle is not null) titles.Children.Add(new TextBlock { Text = subtitle, Style = Ui.Style("RowDescription") });
        head.Children.Add(titles);
        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10), VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(close, L.T("Close"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, L.T("Close"));
        close.Click += (_, _) => CloseList();
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        card.Children.Add(head);

        var template = (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<ContentControl IsTabStop='False' HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch' /></DataTemplate>");
        var repeater = new ItemsRepeater
        {
            Layout = new StackLayout { Spacing = 4 }, ItemTemplate = template, ItemsSource = rows.Cast<object>().ToList(),
            Margin = new Thickness(12, 0, 12, 16),
        };
        repeater.ElementPrepared += (_, e) =>
        {
            if (e.Element is ContentControl host && e.Index >= 0 && e.Index < rows.Count)
                host.Content = ListLine(rows[e.Index], hideMenu);
        };
        repeater.ElementClearing += (_, e) => { if (e.Element is ContentControl host) host.Content = null; };
        var scroll = new ScrollView { Content = repeater, HorizontalScrollMode = ScrollingScrollMode.Disabled, ContentOrientation = ScrollingContentOrientation.Vertical };
        Grid.SetRow(scroll, 1);
        card.Children.Add(scroll);
        scrim.Children.Add(card);

        scrim.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { CloseList(); e.Handled = true; } };
        _overlay = scrim;
        _overlayScroll = scroll;
        PageRoot.Children.Add(scrim);
        // Drawn again in place (a game hidden or shown from the list): where it was, with no fade.
        if (keepOffset is { } offset) scroll.Loaded += (_, _) => scroll.ScrollTo(0, offset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
        else Motion.FadeIn(scrim, 160);
        close.Focus(FocusState.Programmatic);
    }

    void CloseList()
    {
        _listRun++;
        if (_overlay is null) return;
        PageRoot.Children.Remove(_overlay);
        _overlay = null;
    }

    FrameworkElement ListLine(ListRow r, bool hideMenu)
    {
        var line = new Grid { ColumnSpacing = 14, Padding = new Thickness(10, 6, 12, 6) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 200 });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });

        var rank = new TextBlock
        {
            Text = r.Hidden ? "–" : $"#{r.Rank}", FontWeight = FontWeights.Bold, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        line.Children.Add(rank);
        var cover = new Border { Width = 40, Height = 60, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1C, 0x26)) };
        Grid.SetColumn(cover, 1);
        line.Children.Add(cover);
        _ = LoadCoverAsync(r.Game, cover, 120);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (r.Platinum) name.Children.Add(Icons.PlatinumTrophy(14));
        name.Children.Add(new TextBlock { Text = r.Game.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(name);
        if (r.Note is not null) text.Children.Add(new TextBlock { Text = r.Note, Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 2);
        line.Children.Add(text);

        var bar = Bar(r.Share, r.Tint);
        Grid.SetColumn(bar, 3);
        line.Children.Add(bar);
        var value = new TextBlock { Text = r.Value, Style = Ui.Style("RowDescription"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 4);
        line.Children.Add(value);
        if (r.Hidden) line.Opacity = 0.55;

        var button = new Button
        {
            Content = line, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var g = r.Game;
        button.Click += (_, _) => { CloseList(); Open(g); };
        if (hideMenu) AttachHideMenu(button, g, reopen: true);
        return button;
    }

    // ---------- hidden from the dashboard ----------

    /// <summary>
    /// Whether the user took the game out of the dashboard's lists (right-click → Hide from Most played, 2.1-rc.4): some
    /// entries are tools or test apps (Steam's Spacewar), not games. Its hours still count in the totals.
    /// </summary>
    static bool IsHidden(Game g) => AppState.Settings.HiddenFromStats.Contains(g.Id);

    /// <summary>The right-click menu of a game in Most played: hide it from the lists, or show it again.</summary>
    void AttachHideMenu(FrameworkElement target, Game game, bool reopen = false)
    {
        target.ContextRequested += (sender, e) =>
        {
            bool hidden = IsHidden(game);
            var menu = new MenuFlyout();
            var item = new MenuFlyoutItem
            {
                Text = hidden ? L.T("Show in Most played again") : L.T("Hide from Most played"),
                Icon = new FontIcon { Glyph = hidden ? "" : "" },
            };
            ToolTipService.SetToolTip(item, L.T("For apps and tools that are not games. Their hours still count in the totals."));
            item.Click += (_, _) =>
            {
                if (hidden) AppState.Settings.HiddenFromStats.Remove(game.Id);
                else AppState.Settings.HiddenFromStats.Add(game.Id);
                SettingsStore.Save();
                bool wasOpen = _overlay is not null;
                double? offset = _overlayScroll?.VerticalOffset;
                Fill();
                // The list stays where it was (2.1-rc.10 audit: it came back at the top, faded in again).
                if (reopen && wasOpen) ShowMostPlayed(offset);
            };
            menu.Items.Add(item);
            var open = new MenuFlyoutItem { Text = L.T("Open"), Icon = new FontIcon { Glyph = "" } };
            open.Click += (_, _) => { CloseList(); Open(game); };
            menu.Items.Insert(0, open);
            menu.Items.Insert(1, new MenuFlyoutSeparator());
            if (sender is FrameworkElement el && e.TryGetPosition(el, out var at)) menu.ShowAt(el, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at });
            else menu.ShowAt(target);
            e.Handled = true;
        };
    }

    // ---------- the three full lists ----------

    List<Row> _rows = [];

    /// <summary>Every game played, most hours first; the hidden ones at the end, greyed, to show again.</summary>
    void ShowMostPlayed() => ShowMostPlayed(null);

    void ShowMostPlayed(double? keepOffset)
    {
        var played = _rows.Where(r => r.Stats.Minutes > 0).OrderByDescending(r => r.Stats.Minutes).ToList();
        var shown = played.Where(r => !IsHidden(r.Game)).ToList();
        var hidden = played.Where(r => IsHidden(r.Game)).ToList();
        long most = Math.Max(1, played.FirstOrDefault()?.Stats.Minutes ?? 1);
        var list = shown.Select((r, i) => new ListRow(r.Game, i + 1, r.Stats.Minutes / (double)most, Accent, Library.ShortTime(r.Stats.Minutes), LastPlayedNote(r.Stats)))
            .Concat(hidden.Select(r => new ListRow(r.Game, 0, r.Stats.Minutes / (double)most, Accent, Library.ShortTime(r.Stats.Minutes), L.T("Hidden from Most played"), Hidden: true)))
            .ToList();
        long total = played.Sum(r => r.Stats.Minutes);
        ShowList(L.T("Most played"), L.F("{0} played, {1} in all", L.N(shown.Count, "{0} game", "{0} games"), Library.ShortTime(total)), list, hideMenu: true, keepOffset);
    }

    static string? LastPlayedNote(GameStats s) => s.LastPlayed is { } at ? L.F("Last played {0}", Playtime.FormatLast(at)) : null;

    /// <summary>The games played over the last fourteen days (Hybrid Launcher's sessions), most time first.</summary>
    // Counts the lists opened and closed (2.1 audit): a list still reading its sessions when the user closed it, opened
    // another or left the page does not open late over what is on screen now.
    int _listRun;

    async void ShowTwoWeeks()
    {
        int run = ++_listRun;
        try
        {
            var since = DateTime.Now.Date.AddDays(-13);
            var sessions = await Task.Run(SessionLog.Everything);
            if (run != _listRun || !IsLoaded) return;
            var byGame = sessions.Where(s => s.Start.ToLocalTime() >= since)
                .GroupBy(s => s.GameId, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Id: g.Key, Name: g.Last().Name, Minutes: g.Sum(s => s.Minutes), Count: g.Count(), Last: g.Max(s => s.Start.ToLocalTime())))
                .OrderByDescending(x => x.Minutes).ToList();
            double most = Math.Max(1, byGame.FirstOrDefault().Minutes);
            var list = new List<ListRow>();
            double total = 0;
            foreach (var x in byGame)
            {
                var game = AppState.FindAny(x.Id) ?? AllGames().All.FirstOrDefault(g => g.Id.Equals(x.Id, StringComparison.OrdinalIgnoreCase));
                if (game is null || IsHidden(game)) continue;
                // The total of the games listed (2.1-rc.10 audit: hidden ones were counted in it).
                total += x.Minutes;
                list.Add(new ListRow(game, list.Count + 1, x.Minutes / most, Accent, Library.ShortTime((long)Math.Round(x.Minutes)),
                    L.N(x.Count, "{0} session", "{0} sessions") + "  ·  " + L.F("last {0}", Playtime.FormatLast(x.Last))));
            }
            ShowList(L.T("The last two weeks"), list.Count == 0 ? L.T("No game played in the last 14 days.")
                : L.F("{0} in 14 days", Library.ShortTime((long)Math.Round(total))), list, hideMenu: false);
        }
        catch (Exception ex) { Log.Warn($"Profile two weeks: {ex.Message}"); }
    }

    /// <summary>Every game with achievements, the most complete first.</summary>
    void ShowProgress()
    {
        var platinum = Color.FromArgb(0xFF, 0xB8, 0xE4, 0xF5);
        var list = _rows.Where(r => r.Stats.AchTotal > 0 && !IsHidden(r.Game))
            .OrderByDescending(r => (double)r.Stats.AchUnlocked / r.Stats.AchTotal).ThenByDescending(r => r.Stats.AchUnlocked)
            .Select((r, i) =>
            {
                double share = (double)r.Stats.AchUnlocked / r.Stats.AchTotal;
                return new ListRow(r.Game, i + 1, share, share >= 1 ? platinum : Ui.Gold, $"{r.Stats.AchUnlocked} / {r.Stats.AchTotal}  ·  {share * 100:0}%",
                    StoreLogos.Label(r.Game.Store), Platinum: share >= 1);
            }).ToList();
        int got = list.Sum(r => _rows.First(x => x.Game == r.Game).Stats.AchUnlocked);
        ShowList(L.T("Achievement progress"), L.F("{0} achievements over {1}", got.ToString("#,0"), L.N(list.Count, "{0} game", "{0} games")), list, hideMenu: false);
    }

    /// <summary>Self-test: opens one of the full lists ("played", "weeks", "progress"), hides / shows a game, or closes the list.</summary>
    internal void SelfTestList(string which)
    {
        switch (which)
        {
            case "played": ShowMostPlayed(); break;
            case "weeks": ShowTwoWeeks(); break;
            case "progress": ShowProgress(); break;
            case "hide" or "show" when _rows.FirstOrDefault(r => r.Stats.Minutes > 0) is { } first:
                if (which == "hide") AppState.Settings.HiddenFromStats.Add(first.Game.Id); else AppState.Settings.HiddenFromStats.Remove(first.Game.Id);
                SettingsStore.Save();
                Fill();
                break;
            default: CloseList(); break;
        }
    }

    /// <summary>The "See all" link at the right of a section's heading.</summary>
    static HyperlinkButton SeeAll(Action open)
    {
        var link = new HyperlinkButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new TextBlock { Text = L.T("See all") }, new FontIcon { Glyph = "", FontSize = 10 } },
            },
            Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center,
        };
        link.Click += (_, _) => open();
        return link;
    }
}
