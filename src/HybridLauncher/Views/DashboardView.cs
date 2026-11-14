using HybridLauncher.Core;
using HybridLauncher.Core.Achievements;
using HybridLauncher.Core.Media;
using HybridLauncher.Core.Steam;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The Dashboard, the game page's first tab (1.4.0; since rc.7 it holds the old Game tab too, since rc.8 in that tab's own
/// layout): a main column read from the top (the trailer and screenshots, the Spotlight for the featured post or newest major update, the news,
/// the description, the wikis' previews, requirements, Steam's reviews), each section under a hairline and its heading, and a
/// side column of cards (your play, achievements, mods, scores and graphics, how long to beat, controllers, details, activity,
/// languages). On a narrow window the side cards go under the main column, in as many columns as fit. A post opens in the app
/// (<see cref="ShowPost"/>). Every section folds, moves up or down within its column, or hides (the gear on the tab bar
/// brings hidden ones back).
/// </summary>
public sealed partial class DashboardView : UserControl
{
    /// <summary>A part of the Dashboard: its id (settings key), title, glyph, and whether it is a section of the main column (else a side card).</summary>
    public sealed record DashCard(string Id, string Title, string Glyph, bool Wide)
    {
        public string Name => L.T(Title);
    }

    /// <summary>Every part, in its default order: the main column's sections first, then the side column's cards.</summary>
    public static readonly DashCard[] All =
    [
        new("media", "Trailers and screenshots", "\uE714", true),
        new("spotlight", "Spotlight", "\uE735", true),
        new("news", "News and updates", "\uE789", true),
        new("about", "About this game", "\uE946", true),
        new("wikis", "Wikis", "", true),
        new("requirements", "System requirements", "\uE7F4", true),
        new("reviews", "Steam reviews", "\uE90A", true),
        new("achievements", "Achievements", Icons.TrophyGlyph, false),
        // Mods under Activity (2.0.0-rc.11).
        new("activity", "Activity", "\uE81C", false),
        new("mods", "Mods", "\uE74C", false),
        new("controllers", "Controllers", "\uE7FC", false),
        new("time", "How long to beat", "\uE916", false),
        // Scores above Details (1.4.0-rc.20).
        new("game", "Scores", "", false),
        new("details", "Details", "", false),
        new("languages", "Languages", "", false),
    ];

    /// <summary>Marks an order saved for this layout (rc.8); an older one (the masonry's, the first Dashboard's) is dropped.</summary>
    const string OrderMark = "#columns";

    /// <summary>The parts in the user's order (parts missing from the saved order keep their default place).</summary>
    public static List<DashCard> Ordered()
    {
        var order = AppState.Settings.DashboardOrder;
        if (!order.Contains(OrderMark)) order = [];
        return [.. All.OrderBy(c => order.IndexOf(c.Id) is var i and >= 0 ? i : 100 + Array.IndexOf(All, c))];
    }

    /// <summary>
    /// A card the user unticked in the customize panel. Hidden cards saved without that panel's order mark come from an older
    /// layout (the cards' own hide menu, gone since 2.0.0-rc.6) and are dropped with that layout's order: an Achievements card
    /// hidden that way stayed away in rc.10 with no way to see why.
    /// </summary>
    static bool Hidden(string id) => AppState.Settings.DashboardOrder.Contains(OrderMark) && AppState.Settings.DashboardHidden.Contains(id);

    readonly Game _game;
    readonly GameSettings _settings;
    readonly GameData _data;
    readonly OverviewView _overview;
    readonly Action<string> _openTab;
    readonly CancellationToken _ct;
    const double SideWidth = 400, WideFrom = 1180;
    readonly Grid _root = new() { ColumnSpacing = 40, RowSpacing = 30, MaxWidth = 1840, Padding = new Thickness(4, 6, 4, 0) };
    readonly StackPanel _main = new() { Spacing = 30 };
    // The side cards: one column beside the main one; under it (a narrow window) as many columns as fit.
    readonly Controls.MasonryPanel _side = new() { MinColumn = 330, MaxColumns = 3, Gap = 12 };
    bool _wide = true;
    readonly Dictionary<string, StackPanel> _bodies = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Border> _cards = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Grid> _headers = new(StringComparer.OrdinalIgnoreCase);
    // Cards that keep themselves hidden while they have nothing to show (ShowCard), apart from the ones the user hid.
    readonly HashSet<string> _empty = new(StringComparer.OrdinalIgnoreCase);

    AchievementSet? _ach;
    List<GameEvent> _events = [];
    bool _newsLoaded;
    string? _appId;
    // Five rows at first (rc.11, was 8): about as tall as the other side cards; Show more opens it further.
    const int ActivityFirst = 5;
    int _activityShown = ActivityFirst;
    /// <summary>The news list shows its three newest posts as an overview; Show more opens it five at a time (2.0).</summary>
    const int NewsFirst = 3;
    int _newsShown = NewsFirst;
    string _newsFilter = "all";
    bool _spotWide = true;
    Button? _history;

    public DashboardView(Game game, GameSettings settings, GameData data, OverviewView overview, AchievementSet? achievements, Action<string> openTab, CancellationToken ct)
    {
        _game = game;
        _settings = settings;
        _data = data;
        _overview = overview;
        _ach = achievements is { Total: > 0 } ? achievements : null;
        _openTab = openTab;
        _ct = ct;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SideWidth) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(_main);
        Grid.SetColumn(_side, 1);
        _root.Children.Add(_side);
        Content = _root;
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        Build();
        _ = LoadNewsAsync();
        _ = FillModsAsync();
        _ = FillTimeAsync();
        _ = FillGameAsync();
        Unloaded += (_, _) => { GameSessions.PlaytimeChanged -= OnPlaytime; AchievementHistory.Changed -= OnHistory; };
        Loaded += (_, _) =>
        {
            GameSessions.PlaytimeChanged -= OnPlaytime; GameSessions.PlaytimeChanged += OnPlaytime;
            AchievementHistory.Changed -= OnHistory; AchievementHistory.Changed += OnHistory;
        };
    }

    /// <summary>
    /// The page's hook for its sub-tabs: called with true when a main section came or went (the pills change), with false when
    /// one only changed height (the others moved). The first argument is this view (the page keeps several games' views).
    /// </summary>
    public Action<DashboardView, bool>? SectionsChanged { get; set; }

    /// <summary>A main section's element by its id (the sub-tabs scroll to it), or null when it is not on the page.</summary>
    public FrameworkElement? Section(string id) => _cards.TryGetValue(id, out var c) && _main.Children.Contains(c) ? c : null;

    void OnPlaytime(string id) { if (id == _game.Id) DispatcherQueue?.TryEnqueue(() => FillActivity()); }
    // An unlock: the activity is filled again once the pop-up has come in (not on the same frames).
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _historyTimer;
    void OnHistory() => DispatcherQueue?.TryEnqueue(() =>
    {
        if (_historyTimer is null)
        {
            // Weak (2.0.0-rc.12): a timer whose handler holds the view keeps a Dashboard no longer kept in memory for good.
            _historyTimer = WeakTimer.Create(DispatcherQueue, this, TimeSpan.FromSeconds(3), static d => d.FillActivity(), repeating: false);
        }
        _historyTimer.Stop();
        _historyTimer.Start();
    });

    /// <summary>The game page loaded (or reloaded) the game's achievements.</summary>
    public void SetAchievements(AchievementSet? set)
    {
        _ach = set is { Total: > 0 } ? set : null;
        FillAchievements();
        FillActivity();
    }

    readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    int _revealTurn;

    /// <summary>
    /// Shown on screen right after being made (a first visit, 2.0.0-rc.9): the first two sections of the main column come with
    /// the page's first frame, every other card one per turn of the UI thread after it, as the OptiScaler and Mods tabs do since
    /// rc.6. All at once, WinUI laid out every card, picture and text of the Dashboard before drawing anything (the 200-300 ms
    /// of missed frames after a game's first click). Cards already on screen are left as they are.
    /// </summary>
    public void RevealInTurns()
    {
        int turn = ++_revealTurn;
        var order = _main.Children.OfType<Border>().Skip(2).Concat(_side.Children.OfType<Border>())
            .Select(b => b.Tag as string).OfType<string>().ToList();
        foreach (var id in order)
        {
            if (!_cards.TryGetValue(id, out var card) || card.Visibility != Visibility.Visible) continue;
            _held.Add(id);
            card.Visibility = Visibility.Collapsed;
        }
        var queue = new Queue<string>(order.Where(_held.Contains));
        void Next()
        {
            if (turn != _revealTurn) return;
            if (queue.Count == 0) { SectionsChanged?.Invoke(this, true); return; }
            var id = queue.Dequeue();
            _held.Remove(id);
            using (UiPerf.Scope("Dashboard.Reveal"))
            {
                if (_cards.TryGetValue(id, out var card)) card.Visibility = _empty.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
                UiPerf.Layout(this, "dashboard card " + id);
            }
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Next);
        }
        if (queue.Count > 0) DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Next);
    }

    /// <summary>The trailer may start (the tab is on screen) or must stop (another tab, another game).</summary>
    public void Show() => _overview.Show();
    public void Hide() => _overview.Hide();
    public void StopTrailer() => _overview.StopTrailer();

    /// <summary>The Spotlight's own layout (picture beside the text, three tiles a row) wants about 760 DIPs.</summary>
    bool SpotlightWide() => !_cards.TryGetValue("spotlight", out var c) || c.ActualWidth <= 0 || c.ActualWidth >= 760;

    /// <summary>The side column beside the main one on a wide page, under it otherwise (as the Game tab did).</summary>
    void ApplyLayout(double width)
    {
        if (width <= 0) return;
        bool wide = width >= WideFrom && _main.Children.Count > 0;
        // On a very wide page the side column takes what the main one cannot use (the player stops at 1240), up to 560, so
        // the player, the sections under it and the side cards share the same edges.
        double side = Math.Clamp(Math.Min(width, _root.MaxWidth) - 8 - 40 - 1240, SideWidth, 560);
        if (wide == _wide && (!wide || Math.Abs(_root.ColumnDefinitions[1].Width.Value - side) < 0.5)) return;
        _wide = wide;
        _root.ColumnDefinitions[1].Width = wide ? new GridLength(side) : new GridLength(0);
        _root.ColumnSpacing = wide ? 40 : 0;
        Grid.SetColumn(_side, wide ? 1 : 0);
        Grid.SetRow(_side, wide ? 0 : 1);
    }

    // ---------- cards ----------

    /// <summary>
    /// Puts the cards in the user's order, the hidden ones out. A card is made once and kept (2.0.0-rc.6): the order or the
    /// cards shown changing only moves, takes out or adds cards. Building every card again moved the Game-overview parts
    /// (trailer, details, languages...) into new cards, which WinUI refused ("No installed components were detected"), and the
    /// cards came back empty.
    /// </summary>
    void Build()
    {
        var wanted = Ordered().Where(c => !Hidden(c.Id)).ToList();
        var ids = wanted.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Whatever is not a wanted card leaves its column (a hidden card is kept, with its part, for when it comes back).
        foreach (var panel in new Panel[] { _main, _side })
            for (int i = panel.Children.Count - 1; i >= 0; i--)
                if (panel.Children[i] is not Border { Tag: string id } || !ids.Contains(id) || (panel == _main) != wanted.First(c => c.Id == id).Wide)
                    panel.Children.RemoveAt(i);
        int mainAt = 0, sideAt = 0;
        foreach (var c in wanted)
        {
            var panel = c.Wide ? (Panel)_main : _side;
            int to = c.Wide ? mainAt++ : sideAt++;
            if (!_cards.TryGetValue(c.Id, out var card))
            {
                card = MakeCard(c, out var body);
                card.Tag = c.Id;
                _bodies[c.Id] = body;
                _cards[c.Id] = card;
                if (c.Wide) card.SizeChanged += (_, _) => SectionsChanged?.Invoke(this, false);
                // The Spotlight lays itself out again when its width crosses its wide layout's need (window resized, columns changed).
                if (c.Id == "spotlight") card.SizeChanged += (_, e) => { if ((e.NewSize.Width >= 760) != _spotWide) FillSpotlight(); };
                panel.Children.Insert(to, card);
                if (_overview.Part(c.Id) is { } part) Adopt(c.Id, card, body, part.Body, part.Shown, part.Extra);
                continue;
            }
            int at = panel.Children.IndexOf(card);
            if (at == to) continue;
            if (at >= 0) panel.Children.Move((uint)at, (uint)to);
            else panel.Children.Insert(to, card);
        }
        if (_main.Children.Count == 0 && _side.Children.Count == 0)
            _main.Children.Add(Ui.Card(Ui.Muted(L.T("Every card of the dashboard is hidden. Bring them back with the gear on the right of the bar above."))));
        // No main section left: the side cards take the whole width.
        _wide = !_wide;
        ApplyLayout(ActualWidth > 0 ? ActualWidth : WideFrom);
        FillSpotlight();
        FillNews();
        FillActivity();
        FillAchievements();
        SectionsChanged?.Invoke(this, true);
    }

    /// <summary>
    /// A part of the old Game tab (trailer, description, details...) as the body of a card: moved from wherever it was, the
    /// card shown only while the part has something (its own visibility, set as its data arrives), its control in the header.
    /// </summary>
    void Adopt(string id, Border card, StackPanel body, FrameworkElement part, FrameworkElement shown, FrameworkElement? extra)
    {
        // Taken out of wherever it is first: added while still in the old card (a card moved up or down rebuilds them all),
        // WinUI threw "No installed components were detected" (2.0.0-rc.2, moving a card).
        if (!Detach(part)) { Log.Warn($"Dashboard: {id} could not be moved into its card (in {Holder(part)})"); AdoptFailures++; return; }
        try { body.Children.Add(part); }
        catch (Exception ex)
        {
            // Never the error pop-up for one card: logged with what still held it, the card left empty.
            Log.Warn($"Dashboard: {id} not added to its card ({Holder(part)}): {ex.Message}");
            AdoptFailures++;
            return;
        }
        if (extra is not null)
        {
            if (Detach(extra)) SetHeaderExtra(id, extra);
            else Log.Warn($"Dashboard: {id}'s header control could not be moved (in {Holder(extra)})");
        }
        void Sync() => ShowCard(id, shown.Visibility == Visibility.Visible);
        shown.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (_cards.TryGetValue(id, out var c) && ReferenceEquals(c, card)) Sync(); });
        Sync();
    }

    /// <summary>
    /// Takes an element out of its parent, whatever holds it (a panel, a border, a content control or presenter, a viewbox).
    /// True when it has no parent left.
    /// </summary>
    static bool Detach(FrameworkElement e)
    {
        var parent = e.Parent ?? VisualTreeHelper.GetParent(e);
        switch (parent)
        {
            case null: return true;
            case Panel p: p.Children.Remove(e); break;
            case Border b: b.Child = null; break;
            case ContentControl cc: cc.Content = null; break;
            case ContentPresenter cp: cp.Content = null; break;
            case Viewbox vb: vb.Child = null; break;
            default: return false;
        }
        return e.Parent is null && VisualTreeHelper.GetParent(e) is null;
    }

    static string Holder(FrameworkElement e) => (e.Parent ?? VisualTreeHelper.GetParent(e))?.GetType().Name ?? "nothing";

    /// <summary>Re-reads the order and hidden cards (after the customize dialog) and builds the cards again.</summary>
    public void Rebuild()
    {
        Build();
        _ = FillModsAsync();
        _ = FillTimeAsync();
        _ = FillGameAsync();
    }

    static Color TintOf(string id) => id switch
    {
        "spotlight" => Ui.Orange,
        "news" => Ui.Blue,
        "activity" => Ui.Teal,
        "achievements" => Ui.Gold,
        "mods" => Ui.Pink,
        "media" => Ui.Red,
        "about" => Ui.Pink,
        "details" => Ui.Violet,
        "controllers" => Ui.Blue,
        "wikis" => Ui.Teal,
        "requirements" => Ui.Orange,
        "languages" => Ui.Teal,
        "reviews" => Ui.Blue,
        "game" => Ui.Slate,
        _ => Ui.Green,
    };

    /// <summary>
    /// A part of the page. A side card is a box: icon, title, an optional slot for its own controls, then fold. A main section has no box, as on the Game tab: a hairline in its colour, a larger heading with
    /// the same controls, the body under it. The media has no heading at all (it opens the page), and the Spotlight keeps a
    /// box of its own colour. Folding is remembered.
    /// </summary>
    Border MakeCard(DashCard c, out StackPanel body)
    {
        string key = "dash." + c.Id;
        bool flat = c.Wide && c.Id != "spotlight";
        // The trailer opens the page: no hairline over it and a quieter heading, but it folds like every other part (rc.9).
        bool media = c.Id == "media";
        bool open = !AppState.Settings.CollapsedCards.Contains(key);
        var stack = new StackPanel { Spacing = media ? 8 : flat ? 14 : 12 };
        if (flat && !media)
        {
            var rule = Ui.SectionRule(TintOf(c.Id));
            rule.Margin = new Thickness(0, 0, 0, 2);
            stack.Children.Add(rule);
        }
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // A part whose own content starts with icons (the two wikis, each with its own) has none on its heading.
        // The side cards have no icon (rc.9), save the Achievements' trophy: their larger title is enough to tell them apart.
        // The trailer, the description and the wikis speak for themselves (rc.12): no heading text, only their controls.
        bool untitled = c.Id is "media" or "about" or "wikis";
        bool icon = Icons.Show(IconSpots.Headings) && c.Glyph.Length > 0 && !untitled && (flat || c.Id == "achievements");
        if (icon) header.Children.Add(c.Id == "achievements" && !flat ? Icons.Trophy(24) : Ui.Badge(c.Glyph, TintOf(c.Id), flat ? 26 : 22));
        else header.ColumnSpacing = 0;
        var title = new TextBlock
        {
            Text = untitled ? "" : c.Name,
            FontSize = media ? 15 : flat ? 20 : 19,
            FontWeight = flat ? FontWeights.SemiBold : FontWeights.Bold,
            Opacity = media ? 0.8 : 1,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var chevron = Motion.Chevron(open, 11);
        var fold = new Button { Content = chevron, Padding = new Thickness(8, 6, 8, 6), MinWidth = 0, Background = Clear, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(fold, L.T("Fold or open this card"));
        Grid.SetColumn(fold, 3);
        header.Children.Add(fold);

        // No move or hide menu on the cards since 2.0.0-rc.6 (it rebuilt the page from a click and misbehaved); the order and
        // the cards shown are chosen in the Dashboard's own customize panel.
        stack.Children.Add(header);
        _headers[c.Id] = header;

        body = new StackPanel { Spacing = 10, Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        stack.Children.Add(body);
        var b = body;
        void Toggle()
        {
            open = !open;
            Motion.Turn(chevron, open);
            // Folding is immediate (the parts under it move up at once, no fade to wait for); opening fades the body in.
            Motion.Fold(b, open);
            if (!open && media) _overview.StopTrailer();
            if (open) AppState.Settings.CollapsedCards.Remove(key); else AppState.Settings.CollapsedCards.Add(key);
            // Saved off the click: writing the settings file must not hold up the fold.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SettingsStore.Save);
        }
        fold.Click += (_, _) => Toggle();
        // The whole heading folds (not only the chevron or the title's letters), on press, with a light hover; its own
        // controls (a filter) keep their clicks.
        header.Padding = new Thickness(6, 2, 0, 2);
        header.Margin = new Thickness(-6, -2, 0, -2);
        header.CornerRadius = new CornerRadius(6);
        Motion.FoldOnPress(header, Toggle);
        Border card;
        if (flat) card = new Border { Child = stack };
        else
        {
            // Each side card in its own colour (rc.9): a wash of it from the top-left corner fading into the card, so the cards
            // stand off the page's background and from each other. The Achievements card is gold (no trophy watermark since 2.1).
            bool ach = c.Id == "achievements";
            UIElement content = stack;
            card = Ui.Card(content, 18);
            card.Background = CardWash(TintOf(c.Id), ach ? (byte)0x40 : (byte)0x26);
            card.BorderThickness = new Thickness(0);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, c.Name);
        return card;
    }

    /// <summary>The fill of a side card: its colour strongest in the top-left corner, fading into the card's own fill.</summary>
    static LinearGradientBrush CardWash(Color tint, byte strength)
    {
        var fill = (ThemeRes.Current["CardBackgroundFillColorDefaultBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF);
        // A touch more body than the theme's card, so the box reads against the painted page background.
        var baseFill = Color.FromArgb((byte)Math.Min(255, fill.A + 0x0A), fill.R, fill.G, fill.B);
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0.85, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(strength, tint.R, tint.G, tint.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb((byte)(strength / 3), tint.R, tint.G, tint.B), Offset = 0.45 },
                new GradientStop { Color = baseFill, Offset = 1 },
            },
        };
    }

    /// <summary>Puts a card's own control (a filter, a drop-down) in its header, left of the fold button.</summary>
    void SetHeaderExtra(string id, FrameworkElement? extra)
    {
        if (!_headers.TryGetValue(id, out var header)) return;
        foreach (var old in header.Children.OfType<FrameworkElement>().Where(e => Grid.GetColumn(e) == 2).ToList()) header.Children.Remove(old);
        if (extra is null) return;
        extra.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(extra, 2);
        header.Children.Add(extra);
    }

    static SolidColorBrush? _clear;
    static SolidColorBrush Clear => _clear ??= new(Microsoft.UI.Colors.Transparent);

    StackPanel? Body(string id) => _bodies.TryGetValue(id, out var b) ? b : null;

    /// <summary>A card with nothing to show folds away entirely (its place is taken by the next one).</summary>
    void ShowCard(string id, bool show)
    {
        if (show) _empty.Remove(id); else _empty.Add(id);
        // Held back for RevealInTurns: shown (if it has something) on its turn.
        if (_held.Contains(id)) return;
        if (!_cards.TryGetValue(id, out var card)) return;
        var to = show ? Visibility.Visible : Visibility.Collapsed;
        if (card.Visibility == to) return;
        card.Visibility = to;
        if (_main.Children.Contains(card)) SectionsChanged?.Invoke(this, true);
    }

    // ---------- news: Steam's event feed ----------

    async Task LoadNewsAsync()
    {
        try
        {
            _appId = _game.SteamAppId ?? await GameInfo.SteamAppIdAsync(_game, _ct);
            if (_appId is { Length: > 0 } id)
            {
                _events = await GameEvents.ForAppAsync(id, _ct);
                // The event feed failed or is empty (offline with nothing cached): the plain news service, as before.
                if (_events.Count == 0)
                    _events = [.. (await GameNews.ForAppAsync(id, _game.Id, 30, _ct)).Where(n => n.Official).Select(FromNews)];
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"Dashboard news for {_game.Name}: {ex.Message}"); }
        _newsLoaded = true;
        if (_ct.IsCancellationRequested) return;
        var (events, updates, major, spotlight) = SelfTestNews();
        Log.Info($"Dashboard news for {_game.Name}: {events} posts, {updates} updates ({major} major), spotlight: {spotlight ?? "none"}");
        FillSpotlight();
        FillNews();
    }

    static GameEvent FromNews(NewsItem n) => new(n.Url, n.AppId ?? "", n.PatchNotes ? GameEventKind.Update : GameEventKind.News, n.Title, "", n.Excerpt,
        n.Date, n.Image, null, [], []);

    /// <summary>Steam's words for each kind of post, and its colour.</summary>
    static string KindName(GameEventKind k) => k switch
    {
        GameEventKind.MajorUpdate => L.T("Major update"),
        GameEventKind.Update => L.T("Regular update"),
        GameEventKind.SmallUpdate => L.T("Small update"),
        GameEventKind.Event => L.T("Event"),
        _ => L.T("News"),
    };

    static Color KindTint(GameEventKind k) => k switch
    {
        GameEventKind.MajorUpdate => Ui.Orange,
        GameEventKind.Update => Ui.Blue,
        GameEventKind.SmallUpdate => Ui.Teal,
        GameEventKind.Event => Ui.Pink,
        _ => Ui.Slate,
    };

    /// <summary>
    /// The Spotlight: the post Steam features or the newest major update (else a recent update, see GameEvents.Spotlight), always at the top and drawn apart from the other cards:
    /// a warm glow, its capsule, Steam's own one-line summary, and an outline of what it changes (the notes' sections, how many
    /// changes each lists, the first of them), with the full notes and its build on SteamDB one click away.
    /// </summary>
    void FillSpotlight()
    {
        if (Body("spotlight") is not { } body || !_cards.TryGetValue("spotlight", out var card)) return;
        body.Children.Clear();
        var spot = GameEvents.Spotlight(_events);
        if (spot is null)
        {
            if (!_newsLoaded) { body.Children.Add(Ui.Loading()); return; }
            ShowCard("spotlight", false);
            return;
        }
        ShowCard("spotlight", true);
        bool major = spot.Kind == GameEventKind.MajorUpdate;
        var tint = KindTint(spot.Kind);
        // Apart from the other cards: a wash of its colour from the top-left corner (no outline: the app draws none).
        card.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0x3A, tint.R, tint.G, tint.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x12, tint.R, tint.G, tint.B), Offset = 0.45 },
                new GradientStop { Color = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), Offset = 1 },
            },
        };

        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 14 };
        bool wide = _spotWide = SpotlightWide();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = wide ? new GridLength(0.85, GridUnitType.Star) : new GridLength(1, GridUnitType.Star), MaxWidth = 440 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = wide ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string? picture = spot.Image ?? (_appId is { } app ? GameNews.SteamHeader(app) : null);
        if (picture is not null && Picture(picture, wide ? 0 : 180, 720) is { } pic)
        {
            if (wide) { pic.Height = double.NaN; pic.MinHeight = 170; pic.MaxHeight = 250; pic.VerticalAlignment = VerticalAlignment.Top; }
            grid.Children.Add(pic);
        }

        var text = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        meta.Children.Add(SolidPill(KindName(spot.Kind), tint));
        meta.Children.Add(new TextBlock { Text = DayName(spot.Date.ToLocalTime()), FontSize = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(meta);
        text.Children.Add(new TextBlock { Text = spot.Title, FontSize = major ? 24 : 21, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis });
        string lead = spot.Summary.Length > 0 ? spot.Summary : spot.Excerpt;
        if (lead.Length > 0)
            text.Children.Add(new TextBlock { Text = lead, FontSize = 15, LineHeight = 23, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var read = new Button { Style = Ui.Style("AccentButtonStyle"), Padding = new Thickness(14, 6, 14, 7), Content = spot.IsUpdate ? L.T("Read the notes") : L.T("Read the post") };
        read.Click += (_, _) => ShowPost(spot);
        actions.Children.Add(read);
        if (spot.BuildUrl is { } build)
        {
            var b = Ui.LinkButton(L.F("Build {0} on SteamDB", spot.BuildId!), "", () => Ui.Open(build));
            actions.Children.Add(b);
        }
        text.Children.Add(actions);
        if (wide) Grid.SetColumn(text, 1); else Grid.SetRow(text, 1);
        grid.Children.Add(text);
        body.Children.Add(grid);

        // What it changes: the notes' sections as small tiles (or their first changes when the notes have no sections).
        if (spot.Outline.Count > 0)
        {
            int cols = wide ? 3 : 2;
            body.Children.Add(new TextBlock { Text = L.T("What it changes"), Style = Ui.Style("GroupHeader"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            var tiles = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (int i = 0; i < cols; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var sections = spot.Outline.Take(cols * 2).ToList();
            for (int i = 0; i < sections.Count; i++)
            {
                if (i % cols == 0) tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var s = sections[i];
                var t = new StackPanel { Spacing = 3 };
                t.Children.Add(new TextBlock { Text = s.Heading, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                t.Children.Add(new TextBlock { Text = s.First, FontSize = 13.5, LineHeight = 20, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
                // A tile opens the notes at its section.
                string heading = s.Heading;
                var tile = Tappable(new Border { Style = Ui.Style("RowStyle"), MinHeight = 0, Padding = new Thickness(12, 9, 12, 10), Child = t },
                    () => ShowPost(spot, heading), L.F("Read \"{0}\" in the notes", heading));
                tile.VerticalAlignment = VerticalAlignment.Stretch;
                Grid.SetColumn(tile, i % cols);
                Grid.SetRow(tile, i / cols);
                tiles.Children.Add(tile);
            }
            body.Children.Add(tiles);
        }
        else if (spot.Points.Count > 0)
        {
            var list = new StackPanel { Spacing = 5 };
            foreach (var p in spot.Points.Take(4)) list.Children.Add(Bullet(p, tint, 14.5, 22, 0.9, maxLines: 2));
            body.Children.Add(list);
        }
    }

    /// <summary>A solid pill in a colour with readable text on it ("MAJOR UPDATE").</summary>
    static Border SolidPill(string text, Color tint) => new()
    {
        Background = new SolidColorBrush(tint),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(8, 2, 8, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text.ToUpper(), FontSize = 11, FontWeight = FontWeights.Bold, CharacterSpacing = 50, Foreground = Contrast.BrushOn(tint) },
    };

    /// <summary>
    /// The News card: the game's posts as Steam's library lists them (picture, kind, date, title, the developers' summary),
    /// filtered by kind; its header holds the filter and the version history (every update, in a drop-down).
    /// </summary>
    void FillNews()
    {
        if (Body("news") is not { } body) return;
        body.Children.Clear();
        if (_events.Count == 0)
        {
            if (!_newsLoaded) { body.Children.Add(Ui.Loading()); return; }
            ShowCard("news", false);
            return;
        }
        ShowCard("news", true);
        SetHeaderExtra("news", NewsTools());
        IEnumerable<GameEvent> shown = _newsFilter switch
        {
            "updates" => _events.Where(e => e.IsUpdate),
            "news" => _events.Where(e => !e.IsUpdate),
            _ => _events,
        };
        // Every post in its place, the Spotlight's too (2.0): the list reads as the game's whole history, in date order.
        var list = shown.ToList();
        if (list.Count == 0)
        {
            body.Children.Add(Ui.Muted(L.T("No posts of this kind.")));
            return;
        }
        foreach (var e in list.Take(_newsShown)) body.Children.Add(NewsRow(e));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        if (list.Count > _newsShown)
        {
            var more = new HyperlinkButton { Content = L.F("Show {0} more", Math.Min(5, list.Count - _newsShown)), Padding = new Thickness(0, 4, 0, 0) };
            more.Click += (_, _) => { _newsShown += 5; FillNews(); };
            links.Children.Add(more);
        }
        if (_newsShown > NewsFirst && list.Count > NewsFirst)
        {
            var fewer = new HyperlinkButton { Content = L.T("Show fewer"), Padding = new Thickness(0, 4, 0, 0) };
            fewer.Click += (_, _) => { _newsShown = NewsFirst; FillNews(); };
            links.Children.Add(fewer);
        }
        if (links.Children.Count > 0) body.Children.Add(links);
    }

    /// <summary>The News card's header controls: All / Updates / News, and the version history.</summary>
    FrameworkElement NewsTools()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var bar = new SelectorBar();
        foreach (var (tag, name) in new[] { ("all", L.T("All")), ("updates", L.T("Updates")), ("news", L.T("News")) })
        {
            var item = new SelectorBarItem { Text = name, Tag = tag };
            bar.Items.Add(item);
            if (tag == _newsFilter) bar.SelectedItem = item;
        }
        bar.SelectionChanged += (s, _) =>
        {
            if (s.SelectedItem?.Tag is string tag && tag != _newsFilter) { _newsFilter = tag; _newsShown = NewsFirst; DispatcherQueue.TryEnqueue(FillNews); }
        };
        panel.Children.Add(bar);
        var updates = _events.Where(e => e.IsUpdate).ToList();
        if (updates.Count > 0)
        {
            // A button of its own (2.0), not a drop-down: it opens a panel, so it has no menu arrow. Tinted with the accent, the
            // number of updates in a pill beside its name.
            var accent = (Microsoft.UI.Xaml.Media.SolidColorBrush)Ui.Brush("AccentFillColorDefaultBrush");
            var a = accent.Color;
            var count = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, a.R, a.G, a.B)),
                Child = new TextBlock { Text = updates.Count.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold },
            };
            var history = new Button
            {
                Padding = new Thickness(12, 6, 10, 7),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x66, a.R, a.G, a.B)),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x26, a.R, a.G, a.B)),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon { Glyph = "", FontSize = 14, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush") },
                        new TextBlock { Text = L.T("Version history"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        count,
                    },
                },
            };
            ToolTipService.SetToolTip(history, L.F("Every update Steam lists for this game ({0})", updates.Count));
            var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
            // A flyout's presenter stops at 456 x 758 by default: the table was cut and scrolled sideways.
            var presenter = new Style(typeof(FlyoutPresenter));
            presenter.Setters.Add(new Setter(MaxWidthProperty, 4000.0));
            presenter.Setters.Add(new Setter(MaxHeightProperty, 4000.0));
            presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(20, 18, 20, 16)));
            presenter.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
            presenter.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
            flyout.FlyoutPresenterStyle = presenter;
            flyout.Opening += (_, _) => flyout.Content ??= VersionHistory(updates, flyout);
            history.Flyout = flyout;
            _history = history;
            panel.Children.Add(history);
        }
        return panel;
    }

    /// <summary>
    /// The version history: every update in Steam's event feed, newest first, the major ones marked; a click opens its notes.
    /// Steam's own data (the builds behind them are on SteamDB, linked at the bottom and on each update that names its build).
    /// </summary>
    FrameworkElement VersionHistory(List<GameEvent> updates, Flyout flyout)
    {
        var panel = new StackPanel { Spacing = 14, Width = Math.Clamp((XamlRoot?.Size.Width ?? 1100) - 160, 460, 980) };
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = L.T("Version history"), FontSize = 22, FontWeight = FontWeights.SemiBold });
        int majors = updates.Count(u => u.Kind == GameEventKind.MajorUpdate);
        string since = updates[^1].Date.ToLocalTime().ToString("MMMM yyyy");
        titles.Children.Add(Ui.Muted(majors > 0 ? L.F("{0} updates, {1} of them major, since {2}", updates.Count, majors, since) : L.F("{0} updates since {1}", updates.Count, since)));
        head.Children.Add(titles);
        var majorOnly = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = L.T("Major only"), Padding = new Thickness(10, 4, 10, 5), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(majorOnly, 1);
        head.Children.Add(majorOnly);
        panel.Children.Add(head);

        // The build on this PC (Steam's own manifest), as SteamDB names builds.
        var installed = GameEvents.InstalledBuild(_game);
        if (installed is { } mine)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new FontIcon { Glyph = "", FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = mine.Updated is { } at ? L.F("Installed: build {0}, updated by Steam {1}", mine.BuildId, at.ToLocalTime().ToString("d MMMM yyyy, HH:mm")) : L.F("Installed: build {0}", mine.BuildId), VerticalAlignment = VerticalAlignment.Center });
            var link = new HyperlinkButton { Content = "SteamDB", Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            string build = mine.BuildId;
            link.Click += (_, _) => { flyout.Hide(); Ui.Open($"https://steamdb.info/patchnotes/{build}/"); };
            line.Children.Add(link);
            panel.Children.Add(new Border { Style = Ui.Style("RowStyle"), MinHeight = 0, Padding = new Thickness(12, 7, 12, 7), Child = line });
        }

        // The overview, one tile a year (newest first): how many updates, how many major, and a bar a month under it, taller
        // the busier the month, orange where a major update came out. Readable at a glance, as the thin strip was not.
        var years = updates.GroupBy(u => u.Date.ToLocalTime().Year).OrderByDescending(g => g.Key).ToList();
        if (years.Count > 0)
        {
            int busiest = years.SelectMany(g => g.GroupBy(u => u.Date.ToLocalTime().Month)).Max(m => m.Count());
            var tiles = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
            int perRow = Math.Min(years.Count, panel.Width >= 760 ? 4 : 2);
            for (int c = 0; c < perRow; c++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < (years.Count + perRow - 1) / perRow; r++) tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < years.Count; i++)
            {
                var g = years[i];
                int n = g.Count(), m = g.Count(u => u.Kind == GameEventKind.MajorUpdate);
                var tile = new StackPanel { Spacing = 8 };
                var headLine = new Grid();
                headLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                headLine.Children.Add(new TextBlock { Text = g.Key.ToString(), FontSize = 15, FontWeight = FontWeights.SemiBold, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center });
                if (m > 0)
                {
                    var major = SolidPill(L.N(m, "{0} major update", "{0} major updates"), Ui.Orange);
                    Grid.SetColumn(major, 1);
                    headLine.Children.Add(major);
                }
                tile.Children.Add(headLine);
                var big = new TextBlock { FontSize = 30, FontWeight = FontWeights.Bold };
                big.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = n.ToString() });
                big.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + (n == 1 ? L.T("update") : L.T("updates")), FontSize = 14, FontWeight = FontWeights.Normal, Foreground = Ui.Brush("TextFillColorSecondaryBrush") });
                tile.Children.Add(big);
                // Twelve month bars, the month's initial under each.
                var months = new Grid { ColumnSpacing = 3, Height = 58 };
                months.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                months.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int mo = 1; mo <= 12; mo++)
                {
                    months.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    int month = mo;
                    var inMonth = g.Where(u => u.Date.ToLocalTime().Month == month).ToList();
                    bool hasMajor = inMonth.Any(u => u.Kind == GameEventKind.MajorUpdate);
                    var tint = hasMajor ? Ui.Orange : Ui.Teal;
                    var bar = new Border
                    {
                        CornerRadius = new CornerRadius(3),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Height = inMonth.Count == 0 ? 3 : 8 + 32.0 * inMonth.Count / Math.Max(1, busiest),
                        Background = new SolidColorBrush(inMonth.Count == 0 ? Color.FromArgb(0x30, 0x80, 0x80, 0x80) : Color.FromArgb(0xE0, tint.R, tint.G, tint.B)),
                    };
                    var monthName = new DateTime(g.Key, month, 1).ToString("MMMM");
                    ToolTipService.SetToolTip(bar, inMonth.Count == 0 ? L.F("{0}: no updates", monthName) : monthName + "\n" + string.Join("\n", inMonth.Select(u => u.Title)));
                    Grid.SetColumn(bar, month - 1);
                    months.Children.Add(bar);
                    var initial = new TextBlock { Text = monthName[..1].ToUpper(), FontSize = 11, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                    Grid.SetColumn(initial, month - 1);
                    Grid.SetRow(initial, 1);
                    months.Children.Add(initial);
                }
                tile.Children.Add(months);
                var box = new Border { Style = Ui.Style("RowStyle"), MinHeight = 0, Padding = new Thickness(14, 12, 14, 10), Child = tile };
                Grid.SetColumn(box, i % perRow);
                Grid.SetRow(box, i / perRow);
                tiles.Children.Add(box);
            }
            panel.Children.Add(tiles);
        }

        // The table: date, day, time, title (major ones marked), build.
        var rows = new StackPanel { Spacing = 0 };
        void Fill()
        {
            rows.Children.Clear();
            rows.Children.Add(HistoryLine(new TextBlock { Text = L.T("Date"), FontWeight = FontWeights.SemiBold }, new TextBlock { Text = L.T("Day"), FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = L.T("Time"), FontWeight = FontWeights.SemiBold }, new TextBlock { Text = L.T("Title"), FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = L.T("Build"), FontWeight = FontWeights.SemiBold }, header: true));
            foreach (var u in updates.Where(u => majorOnly.IsChecked != true || u.Kind == GameEventKind.MajorUpdate))
                rows.Children.Add(HistoryRow(u, flyout, installed?.BuildId));
        }
        majorOnly.Click += (_, _) => Fill();
        Fill();
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = Math.Max(260, (XamlRoot?.Size.Height ?? 800) - 420), Padding = new Thickness(0, 0, 12, 0) });
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foot.Children.Add(Ui.Muted(L.T("From Steam's update posts. Builds shipped without notes are listed on SteamDB only.")));
        if (_appId is { Length: > 0 } app)
        {
            var link = new HyperlinkButton { Content = L.T("Every build on SteamDB"), Padding = new Thickness(0) };
            link.Click += (_, _) => { flyout.Hide(); Ui.Open(GameNews.SteamDbPatches(app)); };
            foot.Children.Add(link);
        }
        panel.Children.Add(foot);
        return panel;
    }

    /// <summary>One line of the version history's table, in its fixed columns.</summary>
    static Grid HistoryLine(FrameworkElement date, FrameworkElement day, FrameworkElement time, FrameworkElement title, FrameworkElement build, bool header = false)
    {
        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(10, header ? 4 : 10, 10, header ? 8 : 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        FrameworkElement[] cells = [date, day, time, title, build];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].VerticalAlignment = VerticalAlignment.Center;
            if (cells[i] is TextBlock tb) tb.FontSize = header ? 13 : 14.5;
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        build.HorizontalAlignment = HorizontalAlignment.Right;
        grid.BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush");
        grid.BorderThickness = new Thickness(0, 0, 0, 1);
        return grid;
    }

    static FrameworkElement HistoryRow(GameEvent u, Flyout flyout, string? installedBuild)
    {
        var local = u.Date.ToLocalTime();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (u.Kind == GameEventKind.MajorUpdate) title.Children.Add(SolidPill(L.T("Major"), Ui.Red));
        title.Children.Add(new TextBlock { Text = u.Title, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        FrameworkElement build;
        if (u.BuildId is { } b)
        {
            var bt = new TextBlock { Text = b, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            if (b == installedBuild) { bt.Foreground = new SolidColorBrush(Ui.Green); ToolTipService.SetToolTip(bt, L.T("The build installed on this PC")); }
            build = bt;
        }
        else build = new TextBlock { Text = "—", Opacity = 0.4 };
        var line = HistoryLine(new TextBlock { Text = local.ToString("d MMMM yyyy") }, new TextBlock { Text = local.ToString("ddd"), Opacity = 0.75 },
            new TextBlock { Text = local.ToString("HH:mm"), Opacity = 0.75 }, title, build);
        var button = new Button
        {
            Content = line,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Clear,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
        };
        ToolTipService.SetToolTip(button, u.Summary.Length > 0 ? $"{u.Title}\n{u.Summary}" : u.Title);
        string url = u.Url;
        button.Click += (_, _) => { flyout.Hide(); Ui.Open(url); };
        return button;
    }

    UIElement NewsRow(GameEvent e)
    {
        var tint = KindTint(e.Kind);
        var grid = new Grid { ColumnSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The post's own picture; a post without one starts at the text (no stand-in icon beside the kind's name).
        if (e.Image is not null && Picture(e.Image, 104, 400) is { } pic)
        {
            pic.Width = 184;
            pic.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(pic);
        }
        else grid.ColumnSpacing = 0;
        var text = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        meta.Children.Add(new TextBlock { Text = KindName(e.Kind).ToUpper(), FontSize = 12, FontWeight = FontWeights.Bold, CharacterSpacing = 40, Foreground = new SolidColorBrush(tint), VerticalAlignment = VerticalAlignment.Center });
        meta.Children.Add(new TextBlock { Text = DayName(e.Date.ToLocalTime()), FontSize = 12.5, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(meta);
        text.Children.Add(new TextBlock { Text = e.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis, LineHeight = 23 });
        string lead2 = e.Summary.Length > 0 ? e.Summary : e.Excerpt;
        if (lead2.Length > 0)
            text.Children.Add(new TextBlock { Text = lead2, FontSize = 14.5, LineHeight = 22, Opacity = 0.82, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis });
        // What the notes cover, as a list: their sections' names (else their first changes).
        var points = e.IsUpdate ? e.Outline.Count > 0 ? e.Outline.Take(4).Select(o => o.Heading).ToList() : e.Points.Take(3).ToList() : [];
        if (points.Count > 0)
        {
            var list = new StackPanel { Spacing = 3, Margin = new Thickness(0, 2, 0, 0) };
            foreach (var point in points) list.Children.Add(Bullet(point, tint, 14, 21, 0.78, maxLines: 1));
            text.Children.Add(list);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var row = new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(14, 13, 16, 14), MinHeight = 0, Child = grid };
        return Tappable(row, () => ShowPost(e), L.T("Read it here"));
    }

    /// <summary>One line of a list: a dot in the post's colour, then the text hanging beside it (wrapped lines stay aligned).</summary>
    static Grid Bullet(string text, Color tint, double size, double lineHeight, double opacity = 1, int maxLines = 0, bool select = false)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 6, Height = 6, Fill = new SolidColorBrush(tint), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, Math.Round((lineHeight - 6) / 2) + 1, 0, 0),
        });
        var line = new TextBlock { Text = text, FontSize = size, LineHeight = lineHeight, Opacity = opacity, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = select };
        if (maxLines > 0) { line.MaxLines = maxLines; line.TextTrimming = TextTrimming.CharacterEllipsis; if (maxLines == 1) line.TextWrapping = TextWrapping.NoWrap; }
        Grid.SetColumn(line, 1);
        row.Children.Add(line);
        return row;
    }

    /// <summary>A picture that keeps its band only when it loads (a failed one takes no room).</summary>
    static Border? Picture(string url, double height, int decodeWidth)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var img = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0, 0, 0)),
            Child = img,
        };
        if (height > 0) border.Height = height;
        try { img.Source = new BitmapImage(uri) { DecodePixelWidth = decodeWidth }; }
        catch (Exception) { return null; }
        img.ImageFailed += (_, _) => border.Visibility = Visibility.Collapsed;
        return border;
    }

    /// <summary>Content that opens <paramref name="url"/> when clicked (a flat button, the whole surface).</summary>
    static Button Clickable(UIElement content, string url, string name)
    {
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Clear,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "web";
        ToolTipService.SetToolTip(b, L.F("Open on {0}", host));
        b.Click += (_, _) => Ui.Open(url);
        return b;
    }

    /// <summary>Content that runs <paramref name="click"/> when clicked (a flat button over the whole surface).</summary>
    static Button Tappable(FrameworkElement content, Action click, string tip)
    {
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = Clear,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => Ui.Safe(click);
        return b;
    }

    // ---------- a post, read in the app ----------

    bool _reading;

    /// <summary>
    /// A post read in the app instead of in the browser: its picture, kind, version, date and build (marked when it is the one
    /// installed), the developers' summary, the sections of its notes as chips that jump to them, then the whole text laid out
    /// (headings, the changes as a list, paragraphs, pictures, videos as links). Steam and SteamDB stay one click away.
    /// <paramref name="section"/> opens it scrolled to that heading.
    /// </summary>
    public async void ShowPost(GameEvent e, string? section = null)
    {
        if (_reading || XamlRoot is not { } root) return;
        _reading = true;
        var current = e;
        try
        {
            double width = Math.Clamp(root.Size.Width - 160, 420, 860);
            var installed = GameEvents.InstalledBuild(_game);
            var panel = new StackPanel { Spacing = 10, Width = width };
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = ActualTheme,
                Content = panel,
                PrimaryButtonText = L.T("Open on Steam"),
                CloseButtonText = L.T("Close"),
                DefaultButton = ContentDialogButton.Close,
            };
            dialog.Resources["ContentDialogMaxWidth"] = width + 48;
            dialog.Resources["ContentDialogMaxHeight"] = root.Size.Height - 40;

            // The posts in the order of the News list, to step to the one before or after without closing the reader.
            var posts = _events;
            ScrollViewer? scroller = null;
            StackPanel? text = null;
            Dictionary<string, FrameworkElement> anchors = new(StringComparer.OrdinalIgnoreCase);

            void Compose(GameEvent post)
            {
                current = post;
                panel.Children.Clear();
                var tint = KindTint(post.Kind);
                int at = posts.FindIndex(p => p.Gid == post.Gid);

                var top = new Grid { ColumnSpacing = 18 };
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                string? picture = post.Image ?? (_appId is { } app ? GameNews.SteamHeader(app) : null);
                if (picture is not null && Picture(picture, 120, 520) is { } pic && width >= 560)
                {
                    pic.Width = 214;
                    pic.VerticalAlignment = VerticalAlignment.Top;
                    top.Children.Add(pic);
                }
                var head = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                var meta = new Grid { ColumnSpacing = 10 };
                meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                meta.Children.Add(SolidPill(KindName(post.Kind), tint));
                var local = post.Date.ToLocalTime();
                var when = new TextBlock { Text = local.ToString(local.Year == DateTime.Now.Year ? "d MMMM, HH:mm" : "d MMMM yyyy, HH:mm"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(when, 1);
                meta.Children.Add(when);
                // Newer / older: the post before and after this one in the game's feed.
                if (at >= 0 && posts.Count > 1)
                {
                    var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    nav.Children.Add(new TextBlock { Text = L.F("{0} of {1}", at + 1, posts.Count), FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                    var newer = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(9, 6, 9, 6), MinWidth = 0, IsEnabled = at > 0 };
                    var older = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(9, 6, 9, 6), MinWidth = 0, IsEnabled = at < posts.Count - 1 };
                    ToolTipService.SetToolTip(newer, at > 0 ? L.F("Newer: {0}", posts[at - 1].Title) : L.T("This is the newest post"));
                    ToolTipService.SetToolTip(older, at < posts.Count - 1 ? L.F("Older: {0}", posts[at + 1].Title) : L.T("This is the oldest post"));
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(newer, L.T("Newer post"));
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(older, L.T("Older post"));
                    newer.Click += (_, _) => { if (at > 0) Compose(posts[at - 1]); };
                    older.Click += (_, _) => { if (at < posts.Count - 1) Compose(posts[at + 1]); };
                    nav.Children.Add(newer);
                    nav.Children.Add(older);
                    Grid.SetColumn(nav, 2);
                    meta.Children.Add(nav);
                }
                head.Children.Add(meta);
                head.Children.Add(new TextBlock { Text = post.Title, FontSize = 24, LineHeight = 30, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                if (post.Summary.Length > 0)
                    head.Children.Add(new TextBlock { Text = post.Summary, FontSize = 15.5, LineHeight = 24, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                Grid.SetColumn(head, top.Children.Count);
                top.Children.Add(head);
                panel.Children.Add(top);

                // The facts of an update: its build (and whether this PC has it), how much the notes list, how long a read.
                var facts = new List<FrameworkElement>();
                if (post.BuildId is { } build)
                {
                    bool mine = installed?.BuildId == build;
                    facts.Add(Ui.Chip(mine ? L.F("Build {0} · installed", build) : L.F("Build {0}", build), mine ? Ui.Tone.Good : Ui.Tone.Unknown));
                }
                var blocks = post.Blocks();
                int items = blocks.Count(b => b.Kind == NoteBlockKind.Item);
                var headings = blocks.Where(b => b.Kind == NoteBlockKind.Heading).Select(b => b.Text).Distinct().ToList();
                if (post.IsUpdate && items > 0) facts.Add(Ui.Chip(L.N(items, "{0} change", "{0} changes")));
                int words = blocks.Where(b => b.Kind is NoteBlockKind.Item or NoteBlockKind.Paragraph or NoteBlockKind.Heading).Sum(b => b.Text.Count(c => c == ' ') + 1);
                if (words >= 300) facts.Add(Ui.Chip(L.F("{0} min read", Math.Max(1, (int)Math.Round(words / 220.0)))));
                if (installed is { } ins && post.BuildId is null && post.IsUpdate && ins.Updated is { } upd)
                    facts.Add(Ui.Chip(upd >= post.Date ? L.T("Your copy was updated after this post") : L.T("Your copy was last updated before this post"), upd >= post.Date ? Ui.Tone.Good : Ui.Tone.Partial));
                if (facts.Count > 0) panel.Children.Add(Ui.Wrap(facts));

                // The text: headings, the changes as a list, paragraphs, pictures; the sections as chips above it.
                var body = text = new StackPanel { Spacing = 10, Padding = new Thickness(2, 4, 22, 12) };
                var view = scroller = new ScrollViewer { Content = body, MaxHeight = Math.Max(260, root.Size.Height - 360), HorizontalScrollMode = ScrollMode.Disabled };
                var marks = anchors = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
                if (headings.Count > 1)
                {
                    var jumps = new List<FrameworkElement>();
                    foreach (var h in headings.Take(16))
                    {
                        var jump = new Button { Content = h, Padding = new Thickness(11, 4, 11, 5), MinWidth = 0, FontSize = 13 };
                        string target = h;
                        jump.Click += (_, _) => JumpTo(view, body, marks, target);
                        jumps.Add(jump);
                    }
                    panel.Children.Add(Ui.Wrap(jumps));
                }
                if (blocks.Count == 0)
                    body.Children.Add(new TextBlock { Text = post.Excerpt.Length > 0 ? post.Excerpt : L.T("This post has no text Hybrid Launcher can show. Open it on Steam."), FontSize = 16, LineHeight = 26, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = 0.9 });
                foreach (var b in blocks.Take(600))
                {
                    switch (b.Kind)
                    {
                        case NoteBlockKind.Heading:
                            // A heading opens a chapter: room above it, a hairline in the post's colour under it.
                            var heading = new TextBlock { Text = b.Text, FontSize = 21, LineHeight = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Lighten(tint, 0.25)) };
                            var chapter = new StackPanel { Spacing = 7, Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 20, 0, 2) };
                            chapter.Children.Add(heading);
                            chapter.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x48, tint.R, tint.G, tint.B)) });
                            marks.TryAdd(b.Text, chapter);
                            body.Children.Add(chapter);
                            break;
                        case NoteBlockKind.Item:
                            // A list inside a list steps in, with a quieter dot.
                            var item = Bullet(b.Text, b.Level > 0 ? Color.FromArgb(0x99, tint.R, tint.G, tint.B) : tint, 16, 25, b.Level > 0 ? 0.88 : 0.95, select: true);
                            item.Margin = new Thickness(6 + 24 * Math.Min(b.Level, 3), 0, 0, 0);
                            body.Children.Add(item);
                            break;
                        case NoteBlockKind.Paragraph:
                            body.Children.Add(new TextBlock { Text = b.Text, FontSize = 16, LineHeight = 26, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = 0.9, Margin = new Thickness(0, 2, 0, 2) });
                            break;
                        case NoteBlockKind.Picture:
                            if (Picture(b.Text, 0, 1400) is { } p)
                            {
                                p.MaxHeight = 460;
                                p.HorizontalAlignment = HorizontalAlignment.Left;
                                if (p.Child is Image img) img.Stretch = Stretch.Uniform;
                                p.Background = null;
                                p.Margin = new Thickness(0, 4, 0, 4);
                                body.Children.Add(p);
                            }
                            break;
                        case NoteBlockKind.Video:
                            body.Children.Add(VideoLink(b.Text));
                            break;
                    }
                }
                panel.Children.Add(view);
                dialog.SecondaryButtonText = post.BuildUrl is not null ? L.T("Build on SteamDB") : "";
            }

            Compose(e);
            if (section is not null)
                dialog.Opened += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (scroller is not null && text is not null) JumpTo(scroller, text, anchors, section); });
            var result = await dialog.ShowOnceAsync();
            if (result == ContentDialogResult.Primary) Ui.Open(current.Url);
            else if (result == ContentDialogResult.Secondary && current.BuildUrl is { } bu) Ui.Open(bu);
        }
        catch (Exception ex) { Log.Warn($"Post {current.Title}: {ex.Message}"); Ui.Open(current.Url); }
        finally { _reading = false; }
    }

    /// <summary>A video of a post: its YouTube thumbnail with a play mark; a click opens it on YouTube.</summary>
    static FrameworkElement VideoLink(string id)
    {
        var frame = new Grid { Width = 420, Height = 236, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        if (Picture($"https://i.ytimg.com/vi/{id}/hqdefault.jpg", 236, 640) is { } thumb) frame.Children.Add(thumb);
        frame.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)) });
        frame.Children.Add(new FontIcon { Glyph = "", FontSize = 44, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        frame.Children.Add(new TextBlock { Text = L.T("Watch on YouTube"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 10) });
        return Tappable(frame, () => Ui.Open($"https://www.youtube.com/watch?v={id}"), L.T("Watch the video on YouTube"));
    }

    /// <summary>Scrolls the post to a heading (a near match when the outline trimmed it).</summary>
    static void JumpTo(ScrollViewer scroller, FrameworkElement text, Dictionary<string, FrameworkElement> anchors, string heading)
    {
        var target = anchors.TryGetValue(heading, out var t) ? t
            : anchors.FirstOrDefault(a => a.Key.StartsWith(heading, StringComparison.OrdinalIgnoreCase) || heading.StartsWith(a.Key, StringComparison.OrdinalIgnoreCase)).Value;
        if (target is null) return;
        try
        {
            var at = target.TransformToVisual(text).TransformPoint(new Windows.Foundation.Point(0, 0));
            scroller.ChangeView(null, Math.Max(0, at.Y - 4), null, disableAnimation: false);
        }
        catch (ArgumentException) { }
    }

    static Color Lighten(Color c, double t) => Color.FromArgb(c.A, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    // ---------- activity: your sessions and unlocks ----------

    /// <summary>One line of the activity timeline.</summary>
    sealed record Entry(DateTime At, string Kind, string Title, string? Detail, string? Picture, Color Tint, string Glyph);

    List<Entry> ActivityEntries()
    {
        var list = new List<Entry>();
        foreach (var s in SessionLog.For(_game).Take(60))
            list.Add(new Entry(s.Start, L.T("Session"), L.F("Played {0}", Playtime.FormatTime((long)Math.Round(s.Minutes))),
                L.F("From {0:HH:mm} to {1:HH:mm}", s.Start, s.Start.AddMinutes(s.Minutes)), null, Ui.Green, ""));
        // Unlocks: the store's own dates (every unlock, also those made while Hybrid Launcher was closed), else its own history.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_ach is { } set)
            foreach (var a in set.Items.Where(a => a.Unlocked && a.UnlockedAt is not null).OrderByDescending(a => a.UnlockedAt).Take(60))
            {
                seen.Add(a.Name);
                list.Add(new Entry(a.UnlockedAt!.Value.ToLocalTime(), L.T("Achievement"), a.Name,
                    a.GlobalPercent is { } p ? L.F("{0} · {1:0.#}% of players have it", a.Description, p) : a.Description, a.IconUrl, Ui.Gold, Icons.TrophyGlyph));
            }
        string name = PlaytimeLedger.NormalName(_game.Name);
        foreach (var u in AchievementHistory.All().Where(u => (u.GameId == _game.Id || PlaytimeLedger.NormalName(u.Game) == name) && !seen.Contains(u.Name)).Take(60))
            list.Add(new Entry(u.At.ToLocalTime(), L.T("Achievement"), u.Name, u.Description, u.Icon, Ui.Gold, Icons.TrophyGlyph));
        return [.. list.OrderByDescending(e => e.At)];
    }

    void FillActivity()
    {
        if (Body("activity") is not { } body) return;
        body.Children.Clear();
        var entries = ActivityEntries();
        // A game whose achievements its publisher keeps (GTA V Legacy on Epic: Rockstar's Social Club): say where they are.
        if (HybridLauncher.Core.Accounts.EpicTitles.AchievementsElsewhere(_game.Id) is { } elsewhere)
            body.Children.Add(Ui.Muted(L.F("{0} has no achievements on Epic Games: they are kept in {1}, which Hybrid Launcher cannot read.", _game.Name, elsewhere)));
        if (entries.Count == 0)
        {
            body.Children.Add(Ui.Muted(L.T("Your sessions and the achievements you unlock show here, newest first. Sessions count from the first time you play with Hybrid Launcher running.")));
            return;
        }
        DateTime? day = null;
        foreach (var e in entries.Take(_activityShown))
        {
            if (day != e.At.Date)
            {
                day = e.At.Date;
                body.Children.Add(new TextBlock
                {
                    Text = DayName(e.At),
                    Style = Ui.Style("GroupHeader"),
                    FontSize = 12,
                    Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 6, 0, 0),
                });
            }
            body.Children.Add(ActivityRow(e));
        }
        if (entries.Count > _activityShown)
        {
            var more = new HyperlinkButton { Content = L.F("Show {0} more", Math.Min(10, entries.Count - _activityShown)), Padding = new Thickness(0, 4, 0, 0) };
            more.Click += (_, _) => { _activityShown += 10; FillActivity(); };
            body.Children.Add(more);
        }
    }

    static UIElement ActivityRow(Entry e)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement lead;
        if (e.Picture is not null && Picture(e.Picture, 36, 72) is { } pic) { pic.Width = 36; lead = pic; }
        else lead = Ui.Badge(e.Glyph, e.Tint, 36);
        lead.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(lead);
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = e.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (e.Detail is { Length: > 0 } d)
            text.Children.Add(new TextBlock { Text = d, Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var time = new TextBlock { Text = e.At.ToString("HH:mm"), FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        return new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(10, 7, 12, 7), MinHeight = 0, Child = grid };
    }

    /// <summary>"Today", "Yesterday", then the date ("26 October", with the year when it is not this year).</summary>
    static string DayName(DateTime local)
    {
        int days = (DateTime.Now.Date - local.Date).Days;
        return days switch
        {
            <= 0 => L.T("Today"),
            1 => L.T("Yesterday"),
            _ => local.ToString(local.Year == DateTime.Now.Year ? "d MMMM" : "d MMMM yyyy"),
        };
    }

    // ---------- play ----------

    /// <summary>A small upper-case label over a group of lines.</summary>
    static TextBlock Caption(string text) => new() { Text = text, Style = Ui.Style("GroupHeader"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    // ---------- achievements ----------

    void FillAchievements()
    {
        if (Body("achievements") is not { } body) return;
        body.Children.Clear();
        if (_ach is not { } set)
        {
            ShowCard("achievements", false);
            return;
        }
        ShowCard("achievements", true);
        int got = set.UnlockedCount, total = set.Total;
        double pct = total > 0 ? 100.0 * got / total : 0;
        var gold = Ui.Gold;

        // A large gold ring with the share in it, beside the counts and a bar of what is left.
        var top = new Grid { ColumnSpacing = 18 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ring = new Grid { Width = 86, Height = 86 };
        ring.Children.Add(new ProgressRing { IsIndeterminate = false, Value = pct, Maximum = 100, Width = 86, Height = 86, Foreground = new SolidColorBrush(gold), Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)) });
        // The share in the text colour (gold on the gold wash read poorly), a small raised "%".
        ring.Children.Add(ScoreNumber($"{pct:0}%", 24, Microsoft.UI.Colors.White));
        top.Children.Add(ring);
        var counts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var count = new TextBlock { FontSize = 26, FontWeight = FontWeights.Bold };
        count.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = got.ToString() });
        count.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " / " + total, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("TextFillColorSecondaryBrush") });
        counts.Children.Add(count);
        counts.Children.Add(Ui.Muted(got == total ? L.T("Every achievement unlocked") : L.N(total - got, "{0} left to unlock", "{0} left to unlock")));
        int points = set.Items.Where(a => a.Unlocked).Sum(a => a.Points ?? 0), allPoints = set.Items.Sum(a => a.Points ?? 0);
        if (allPoints > 0) counts.Children.Add(Ui.Muted(L.F("Gamerscore {0} of {1}", points, allPoints)));
        // The platinum only once it is earned (its name gives away what it asks for, and "not earned" is no news).
        if (set.Platinum is { Unlocked: true })
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(Icons.PlatinumTrophy(16));
            line.Children.Add(Ui.Muted(L.T("Platinum earned")));
            counts.Children.Add(line);
        }
        Grid.SetColumn(counts, 1);
        top.Children.Add(counts);
        body.Children.Add(top);

        // 2.1: a gold bar of the way done, and the unlocks counted by rarity tier (gold, silver, bronze trophies, as the
        // pop-ups give them), so the card says how hard the haul was, not only how big.
        body.Children.Add(GoldBar(pct / 100, gold));
        var tiers = set.Items.Where(a => a.Unlocked && a.GlobalPercent is not null).GroupBy(a => TierName(a.GlobalPercent)).ToDictionary(g => g.Key, g => g.Count());
        if (tiers.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var tier in new[] { "Gold", "Silver", "Bronze" })
            {
                if (!tiers.TryGetValue(tier, out var n)) continue;
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                chip.Children.Add(Icons.TierTrophy(16, tier));
                chip.Children.Add(new TextBlock { Text = n.ToString(), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                var pill = new Border { Padding = new Thickness(9, 4, 11, 4), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), Child = chip };
                ToolTipService.SetToolTip(pill, tier switch
                {
                    "Gold" => L.T("Rare: fewer than 10% of players have them"),
                    "Silver" => L.T("Uncommon: 10 to 30% of players have them"),
                    _ => L.T("Common: 30% of players or more have them"),
                });
                row.Children.Add(pill);
            }
            body.Children.Add(row);
        }

        // As Steam's achievements box: the latest unlock large (its icon, name, what it asks and when), then the ones before it
        // as a row of icons, the rest counted in a last tile.
        var unlocked = set.Items.Where(a => a.Unlocked).OrderByDescending(a => a.UnlockedAt ?? DateTime.MinValue).ToList();
        if (unlocked.Count > 0)
        {
            var last = unlocked[0];
            body.Children.Add(Caption(L.T("Latest unlocked")));
            body.Children.Add(AchievementPreview(last, last.UnlockedAt is { } at ? DayName(at.ToLocalTime()) + "  ·  " + at.ToLocalTime().ToString("HH:mm") : null, gold));
            const int Row = 6;
            var before = unlocked.Skip(1).ToList();
            if (before.Count > 0)
            {
                var icons = new Grid { ColumnSpacing = 6 };
                for (int i = 0; i < Row; i++) icons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int shown = before.Count > Row ? Row - 1 : before.Count;
                for (int i = 0; i < shown; i++)
                {
                    var a = before[i];
                    var tile = SquareTile(a.IconUrl is { } url && Picture(url, 0, 96) is { } p ? p : Icons.TierTrophy(26, TierName(a.GlobalPercent)));
                    ToolTipService.SetToolTip(tile, a.UnlockedAt is { } at2 ? $"{a.Name}\n{a.Description}\n{at2.ToLocalTime():d MMM yyyy, HH:mm}" : $"{a.Name}\n{a.Description}");
                    Grid.SetColumn(tile, i);
                    icons.Children.Add(tile);
                }
                if (before.Count > shown)
                {
                    var more = SquareTile(new TextBlock { Text = "+" + (before.Count - shown), FontSize = 16, FontWeight = FontWeights.SemiBold, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                    more.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80));
                    // A click opens the Achievements tab with every unlock (1.4.0-rc.20).
                    var moreButton = Tappable(more, () => _openTab("ach"), L.T("All achievements"));
                    moreButton.MaxWidth = 64;
                    moreButton.VerticalAlignment = VerticalAlignment.Top;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(moreButton, L.T("All achievements"));
                    Grid.SetColumn(moreButton, Row - 1);
                    icons.Children.Add(moreButton);
                }
                body.Children.Add(icons);
            }
        }
        // Your rarest unlock, drawn as the latest one is: its icon, its name and what it asks, the trophy of its rarity tier
        // (bronze, silver, gold, as the pop-ups give it) and the share of players who have it.
        if (set.Items.Where(a => a.Unlocked && a.GlobalPercent is not null).OrderBy(a => a.GlobalPercent).FirstOrDefault() is { } rare)
        {
            body.Children.Add(Caption(L.T("Your rarest")));
            body.Children.Add(AchievementPreview(rare, null, gold, rarity: true));
        }
        body.Children.Add(Ui.LinkButton(L.T("All achievements"), "", () => _openTab("ach")));
    }

    /// <summary>Bronze, Silver or Gold by the share of players who have it, as the achievement pop-ups pick their trophy.</summary>
    static string TierName(double? percent) => percent switch { null or >= 30 => "Bronze", >= 10 => "Silver", _ => "Gold" };

    /// <summary>A square tile for an achievement's icon (or what stands in for it), as tall as it is wide, at most 64.</summary>
    static Border SquareTile(FrameworkElement content)
    {
        var tile = new Border { CornerRadius = new CornerRadius(8), Child = content, MaxWidth = 64 };
        tile.SizeChanged += (_, e) => { if (Math.Abs(tile.Height - e.NewSize.Width) > 0.5) tile.Height = e.NewSize.Width; };
        return tile;
    }

    /// <summary>
    /// One achievement drawn large, as Steam's box draws the latest: its 64 px icon, its name, what it asks for and a small line
    /// (when it was unlocked). With <paramref name="rarity"/> the line is the share of players who have it, beside the trophy
    /// of its rarity tier, and the share stands on the right in large type.
    /// </summary>
    static FrameworkElement AchievementPreview(Achievement a, string? when, Color gold, bool rarity = false)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string tier = TierName(a.GlobalPercent);
        FrameworkElement pic = a.IconUrl is { } url && Picture(url, 64, 128) is { } p ? p : Icons.TierTrophy(40, tier);
        var icon = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), Child = pic, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = a.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (a.Description.Length > 0)
            text.Children.Add(new TextBlock { Text = a.Description, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
        if (rarity && a.GlobalPercent is { } share)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 3, 0, 0) };
            line.Children.Add(Icons.TierTrophy(16, tier));
            line.Children.Add(new TextBlock { Text = L.F("{0:0.#}% of players", share), FontSize = 12.5, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(line);
        }
        else if (when is not null)
            text.Children.Add(new TextBlock { Text = when, FontSize = 12.5, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        ToolTipService.SetToolTip(grid, a.Description.Length > 0 ? $"{a.Name}\n{a.Description}" : a.Name);
        // 2.1: a gold light behind the icon and a wash that fades across the row, so the unlock reads as a prize.
        var halo = new RadialGradientBrush();
        halo.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x70, gold.R, gold.G, gold.B), Offset = 0 });
        halo.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, gold.R, gold.G, gold.B), Offset = 1 });
        var glow = new Border { Width = 104, Height = 104, Margin = new Thickness(-20), Background = halo, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Insert(0, glow);
        var wash = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x34, gold.R, gold.G, gold.B), Offset = 0 });
        wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x0C, gold.R, gold.G, gold.B), Offset = 1 });
        return new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(12), Background = wash, Child = grid };
    }

    /// <summary>A slim bar filled in gold to <paramref name="share"/> (0 to 1), its light end a little brighter.</summary>
    static FrameworkElement GoldBar(double share, Color gold)
    {
        var track = new Grid { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 2, 0, 0) };
        var fillBrush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        fillBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, (byte)(gold.R * 0.75), (byte)(gold.G * 0.6), (byte)(gold.B * 0.4)), Offset = 0 });
        fillBrush.GradientStops.Add(new GradientStop { Color = gold, Offset = 0.8 });
        fillBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0xF1, 0xC4), Offset = 1 });
        var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Background = fillBrush };
        track.Children.Add(fill);
        track.SizeChanged += (_, e) => fill.Width = Math.Max(share > 0 ? 6 : 0, e.NewSize.Width * Math.Clamp(share, 0, 1));
        return track;
    }

    /// <summary>A small fact line: icon, label, value.</summary>
    static FrameworkElement Fact(string glyph, string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        var l = new TextBlock { Text = label, Style = Ui.Style("RowDescription"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(l, 1);
        grid.Children.Add(l);
        var v = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(v, value);
        Grid.SetColumn(v, 2);
        grid.Children.Add(v);
        return grid;
    }

    // ---------- mods ----------

    /// <summary>
    /// The Mods card as an overview at a glance: one line saying how many mods and whether anything stops them, then one compact
    /// row per mod (its colour, name, version, how it is loaded and what rides on it, its state); a row opens the mod's section.
    /// Under them "Add a mod" and the way to the Mods tab's full table, side by side.
    /// </summary>
    /// <summary>The game folder changed (a mod in or out): the Mods card is read again.</summary>
    public void RefreshMods() => _ = FillModsAsync();

    async Task FillModsAsync()
    {
        if (Body("mods") is not { } body) return;
        if (_game.NotInstalled) { ShowCard("mods", false); return; }
        List<ModTag> mods;
        List<HybridLauncher.Core.Mods.ModProblem> problems;
        var settings = _settings;
        var game = _game;
        try
        {
            (mods, problems) = await Task.Run(() =>
            {
                var found = InstalledMods.Find(game, settings);
                var dir = HybridLauncher.Core.Detection.GameScanner.TargetDir(game, settings);
                var check = dir is null ? [] : HybridLauncher.Core.Mods.ModCheck.Run(settings, dir);
                return (found, check.Where(p => !settings.DismissedModProblems.Contains(p.Id)).ToList());
            }, _ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"Dashboard mods for {_game.Name}: {ex.Message}"); return; }
        if (_ct.IsCancellationRequested) return;
        body.Children.Clear();

        // The summary: how many, and whether they all load.
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 0, 0, 2) };
        bool ok = problems.Count == 0;
        summary.Children.Add(new FontIcon
        {
            Glyph = ok ? "\uE73E" : "\uE7BA", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Ui.Brush(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"),
        });
        summary.Children.Add(new TextBlock
        {
            Text = mods.Count == 0 ? L.T("No mods in the game's folder yet.")
                : L.N(mods.Count, "{0} mod", "{0} mods") + "  ·  " + (ok ? L.T("all load") : L.N(problems.Count, "{0} problem", "{0} problems")),
            Style = Ui.Style("RowDescription"), VerticalAlignment = VerticalAlignment.Center,
        });
        if (mods.Count > 0 || !ok) body.Children.Add(summary);
        else body.Children.Add(Ui.Muted(L.T("No mods in the game's folder yet.")));

        foreach (var m in mods)
        {
            string tab = m.Key switch { "rs" => "mods/reshade", "rdx" => "mods/renodx", "dxvk" => m.Short == "VKD3D" ? "mods/vkd3d" : "mods/dxvk", _ => m.Tab };
            // What rides on it: OptiScaler's file name and plugins, ReShade's loader.
            var extras = new List<string>();
            if (m.Key == "opti" && settings.Install is { } inst)
            {
                extras.Add(L.F("as {0}", inst.DllName));
                if (inst.DlssNrAmd is not null) extras.Add("DLSSNR-AMD");
                if (inst.AsiPlugins.Count > 0) extras.Add(L.N(inst.AsiPlugins.Count, "{0} plugin", "{0} plugins"));
            }
            var version = m.Version ?? "";
            if (m.Key == "rs" && version.Contains(" · "))
            {
                var parts = version.Split(" · ", 2);
                version = parts[0];
                extras.Add(parts[1]);
            }
            int mine = problems.Count(p => ProblemTab(p) == m.Key);

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(m.Color), VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            var line1 = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            line1.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = m.Name, FontWeight = FontWeights.SemiBold });
            if (version.Length > 0) line1.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + ShortVersion(version), Foreground = Ui.Brush("TextFillColorSecondaryBrush"), FontSize = 12 });
            text.Children.Add(line1);
            if (extras.Count > 0) text.Children.Add(new TextBlock { Text = string.Join("  ·  ", extras), Style = Ui.Style("RowDescription"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var state = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), VerticalAlignment = VerticalAlignment.Center,
                Background = Ui.Brush(mine == 0 ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorCautionBackgroundBrush"),
                Child = new TextBlock { Text = mine == 0 ? L.T("OK") : L.N(mine, "{0} problem", "{0} problems"), FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = Ui.Brush(mine == 0 ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush") },
            };
            Grid.SetColumn(state, 2);
            // Only a mod with problems carries a tag: a green OK on every row said nothing (1.4.0-rc.20).
            if (mine > 0) grid.Children.Add(state);
            var row = new Button
            {
                Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0),
                Background = Ui.Brush("SubtleFillColorSecondaryBrush"),
            };
            ToolTipService.SetToolTip(row, m.Version is { Length: > 0 } full && full != version ? $"{m.Detail}\n{full}" : m.Detail);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, m.Name);
            row.Click += (_, _) => _openTab(tab);
            body.Children.Add(row);
        }

        // Add a mod and the full table, one line.
        var missing = new List<(string Name, string What, string Tab)>();
        if (!mods.Any(m => m.Key == "opti")) missing.Add(("OptiScaler", L.T("Upscaling and frame generation: DLSS, FSR, XeSS in any game that has one of them"), "opti/install"));
        if (!mods.Any(m => m.Key == "rs")) missing.Add(("ReShade", L.T("Post-processing effects and filters"), "mods/reshade"));
        if (!mods.Any(m => m.Key == "rdx")) missing.Add(("RenoDX", L.T("HDR mods, when there is one for this game"), "mods/renodx"));
        if (!mods.Any(m => m.Key == "dxvk")) missing.Add(("DXVK", L.T("Direct3D through Vulkan: less stutter in some older games"), "mods/dxvk"));
        var foot = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (missing.Count > 0)
        {
            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
            foreach (var (name, what, tab) in missing)
            {
                var item = new MenuFlyoutItem { Text = $"{name}  ·  {what}" };
                string target = tab;
                item.Click += (_, _) => _openTab(target);
                menu.Items.Add(item);
            }
            var add = new Button
            {
                Padding = new Thickness(8, 3, 10, 4),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE710", FontSize = 11 }, new TextBlock { Text = L.T("Add a mod"), FontSize = 13 } } },
                Flyout = menu,
            };
            ToolTipService.SetToolTip(add, L.T("What else can go in this game, and where to set it up"));
            foot.Children.Add(add);
        }
        // With mods listed each row already opens its place in the Mods tab: no second link (1.4.0-rc.20).
        if (mods.Count == 0)
        {
            var table = new HyperlinkButton { Content = L.T("Mods tab"), Padding = new Thickness(6, 2, 2, 2), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            table.Click += (_, _) => _openTab("mods/installed");
            Grid.SetColumn(table, 2);
            foot.Children.Add(table);
        }
        body.Children.Add(foot);
    }

    /// <summary>The mod a finding of the mod check is about, by the section its fix opens ("opti" for OptiScaler's, "rs" for ReShade's…).</summary>
    static string ProblemTab(HybridLauncher.Core.Mods.ModProblem p) => p.Tab switch
    {
        null => "",
        var t when t.StartsWith("opti", StringComparison.OrdinalIgnoreCase) => "opti",
        var t when t.Contains("reshade", StringComparison.OrdinalIgnoreCase) => "rs",
        var t when t.Contains("renodx", StringComparison.OrdinalIgnoreCase) => "rdx",
        var t when t.Contains("dxvk", StringComparison.OrdinalIgnoreCase) || t.Contains("vkd3d", StringComparison.OrdinalIgnoreCase) => "dxvk",
        var t when t.StartsWith("ultra", StringComparison.OrdinalIgnoreCase) => "uplus",
        _ => "",
    };

    /// <summary>A fork's "owner/Repo-Name v1.2.3" as "v1.2.3 · Repo-Name" kept short: the card is narrow.</summary>
    static string ShortVersion(string v)
    {
        int slash = v.IndexOf('/');
        int space = v.LastIndexOf(' ');
        if (slash > 0 && space > slash) return v[(space + 1)..] + "  ·  " + v[(slash + 1)..space];
        return v;
    }

    static string ModGlyph(string key) => key switch
    {
        "opti" => "",
        "rs" => "",
        "rdx" => "",
        "dxvk" => "",
        _ => "",
    };

    // ---------- about the game ----------

    /// <summary>
    /// The game's facts that help decide what to do with it: Steam's review score (all time and lately), Metacritic, the
    /// upscalers and frame generation it ships with, its graphics APIs and the problems PCGamingWiki still lists.
    /// </summary>
    async Task FillGameAsync()
    {
        if (Body("game") is not { } body) return;
        body.Children.Clear();
        body.Children.Add(Ui.Loading());
        StoreDetails? store = null;
        PcgwInfo? pcgw = null;
        SteamReviewSet? reviews = null;
        MetacriticScores? mc = null;
        try { store = await _data.Store; } catch (Exception ex) when (ex is not OperationCanceledException) { }
        catch (OperationCanceledException) { return; }
        try { pcgw = await _data.Pcgw; } catch (Exception ex) when (ex is not OperationCanceledException) { }
        catch (OperationCanceledException) { return; }
        string? app = _game.SteamAppId ?? store?.AppId;
        if (app is { Length: > 0 })
        {
            try { reviews = await SteamReviews.GetAsync(app, _ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn($"Dashboard reviews for {_game.Name}: {ex.Message}"); }
        }
        if (AppState.Settings.ShowMetacritic)
        {
            try { mc = await _data.Reviews; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn($"Dashboard Metacritic for {_game.Name}: {ex.Message}"); }
        }
        if (_ct.IsCancellationRequested) return;
        int facts = 0;
        body.Children.Clear();

        // The scores side by side, each a solid square in its verdict's colour with the number in it (as Metacritic draws
        // its own): Steam's share of positive reviews, the critics' Metascore, the users' score. Each opens its page.
        var scores = new List<FrameworkElement>();
        if (reviews is { Total: >= 10 } r)
        {
            double share = r.Share * 100;
            string recent = r.RecentUp + r.RecentDown >= 10 ? L.F("Lately: {0}", L.T(r.RecentSummary)) : L.F("{0} reviews", r.Total.ToString("N0"));
            scores.Add(Clickable(ScoreBlock($"{share:0}%", ScoreColor((int)Math.Round(share)), "Steam", L.T(r.Summary), recent, wide: true), r.StorePage, L.T("Steam reviews")));
        }
        if (mc is { } m && (m.Metascore is not null || m.UserScore is not null))
        {
            if (m.Metascore is { } meta)
                scores.Add(Clickable(ScoreBlock(meta.ToString(), ScoreColor(meta), L.T("Metascore"), m.CriticVerdict.Length > 0 ? L.T(m.CriticVerdict) : L.T("Critics"),
                    L.N(m.CriticReviews, "{0} critic review", "{0} critic reviews")), m.Url, "Metacritic"));
            if (m.UserScore is { } user)
                scores.Add(Clickable(ScoreBlock(user.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture), ScoreColor((int)Math.Round(user * 10)), L.T("User score"),
                    L.T("Players"), L.N(m.UserRatings, "{0} rating", "{0} ratings"), round: true), m.Url, "Metacritic"));
        }
        else if (AppState.Settings.ShowMetacritic && store?.Metacritic is { } storeMeta)
            scores.Add(Clickable(ScoreBlock(storeMeta.ToString(), ScoreColor(storeMeta), L.T("Metascore"), L.T("Critics"), "Metacritic"),
                $"https://www.metacritic.com/search/{Uri.EscapeDataString(_data.SearchName)}/", "Metacritic"));
        if (scores.Count > 0)
        {
            // Steam's on its own line (its words are long), the two Metacritic scores side by side under it.
            var list = new StackPanel { Spacing = 8 };
            var steam = scores.FirstOrDefault(x => reviews is { Total: >= 10 } && ReferenceEquals(x, scores[0]));
            if (steam is not null) list.Children.Add(steam);
            var rest = scores.Where(x => !ReferenceEquals(x, steam)).ToList();
            if (rest.Count == 1) list.Children.Add(rest[0]);
            else if (rest.Count > 1) list.Children.Add(Ui.TwoByN(rest));
            body.Children.Add(list);
            var notes = new List<string>();
            if (mc is { } n)
            {
                if (n.MetascorePlatform is { } platform) notes.Add(L.F("Metascore of the {0} version", platform));
                if (n.MustPlay) notes.Add(L.T("Must-play"));
                if (!GameNames.SameGame(_game.Name, n.Title)) notes.Add(L.F("As \"{0}\" on Metacritic", n.Title));
            }
            if (notes.Count > 0) body.Children.Add(Ui.Muted(string.Join("  ·  ", notes)));
            facts++;
        }

        ShowCard("game", facts > 0);
    }

    /// <summary>Metacritic's own colours: green from 75, yellow from 50, red below (Steam's share read the same way).</summary>
    static Color ScoreColor(int score) => score >= 75 ? Color.FromArgb(0xFF, 0x00, 0xCE, 0x7A) : score >= 50 ? Color.FromArgb(0xFF, 0xFF, 0xBD, 0x3F) : Color.FromArgb(0xFF, 0xFF, 0x68, 0x74);

    /// <summary>
    /// A number set tight on its glyphs (no line gap above or below), so it sits in the middle of a square or a ring; a trailing
    /// "%" is drawn smaller and raised beside it, as score sites do, instead of squeezing the whole value smaller.
    /// </summary>
    static TextBlock ScoreNumber(string value, double size, Color ink)
    {
        var text = new TextBlock
        {
            FontSize = size, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(ink), CharacterSpacing = -20,
            TextLineBounds = TextLineBounds.Tight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        if (value.EndsWith('%'))
        {
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value[..^1] });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "%", FontSize = Math.Round(size * 0.55), FontWeight = FontWeights.SemiBold });
        }
        else text.Text = value;
        return text;
    }

    /// <summary>
    /// A score: a solid square (a disc for a users' score) in the verdict's colour with the number in dark bold type on it,
    /// then the source, the verdict and a small line. The block itself has a faint wash of the same colour.
    /// </summary>
    static FrameworkElement ScoreBlock(string value, Color tint, string source, string verdict, string note, bool wide = false, bool round = false)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        double size = wide ? 58 : 50;
        grid.Children.Add(new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(round ? size / 2 : 8), Background = new SolidColorBrush(tint),
            VerticalAlignment = VerticalAlignment.Center,
            Child = ScoreNumber(value, wide ? 24 : round ? 19 : 22, Color.FromArgb(0xFF, 0x10, 0x12, 0x16)),
        });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        words.Children.Add(new TextBlock { Text = source.ToUpper(), Style = Ui.Style("GroupHeader"), FontSize = 10.5 });
        words.Children.Add(new TextBlock { Text = verdict, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(tint), TextTrimming = TextTrimming.CharacterEllipsis });
        if (note.Length > 0) words.Children.Add(new TextBlock { Text = note, Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        return new Border
        {
            Padding = new Thickness(8, 8, 10, 8), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, tint.R, tint.G, tint.B)), Child = grid,
        };
    }

    // ---------- how long to beat ----------

    async Task FillTimeAsync()
    {
        if (Body("time") is not { } body) return;
        if (!AppState.Settings.ShowHltb) { ShowCard("time", false); return; }
        body.Children.Clear();
        body.Children.Add(Ui.Loading());
        HltbTimes? times = null;
        try { times = await _data.Hltb; }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"Dashboard HLTB for {_game.Name}: {ex.Message}"); }
        if (_ct.IsCancellationRequested) return;
        body.Children.Clear();
        if (times is null || times.Main <= 0 && times.MainExtra <= 0 && times.Completionist <= 0) { ShowCard("time", false); return; }
        ShowCard("time", true);
        // One tile per length, as HowLongToBeat lists them: its name and the hours large in its colour (nothing about your own
        // progress: play time says nothing of how far into a game someone is).
        var tiles = new StackPanel { Spacing = 8 };
        foreach (var (label, h, tint) in new[] { (L.T("Main story"), times.Main, Ui.Teal), (L.T("Main + extras"), times.MainExtra, Ui.Blue), (L.T("Completionist"), times.Completionist, Ui.Gold) })
        {
            if (h <= 0) continue;
            var tile = new StackPanel { Spacing = 2 };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = label, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom });
            var value = new TextBlock { Text = Ui.Hours(h), FontSize = 24, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(tint), VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(value, 1);
            line.Children.Add(value);
            tile.Children.Add(line);
            tiles.Children.Add(new Border
            {
                Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x16, tint.R, tint.G, tint.B)), Child = tile,
            });
        }
        body.Children.Add(tiles);
        var foot = new List<string>();
        if (times.Submissions > 0) foot.Add(L.N(times.Submissions, "From {0} player's times", "From {0} players' times"));
        if (!GameNames.SameGame(_game.Name, times.Name)) foot.Add(L.F("As \"{0}\" on HowLongToBeat", times.Name));
        if (foot.Count > 0) body.Children.Add(Ui.Muted(string.Join("  ·  ", foot)));
        body.Children.Add(Ui.LinkButton(L.T("Open on HowLongToBeat"), "", () => Ui.Open(times.Url)));
    }

    // ---------- customize ----------

    /// <summary>What each card shows, for the customize dialog.</summary>
    static string Info(string id) => id switch
    {
        "spotlight" => L.T("The post Steam features, or the newest major update, always first: its summary and what it changes."),
        "news" => L.T("The game's posts on Steam by kind, read here, and every update in the version history."),
        "activity" => L.T("Your sessions and the achievements you unlock, by day."),
        "achievements" => L.T("Progress, the latest unlocked and your rarest."),
        "mods" => L.T("OptiScaler, ReShade, RenoDX and Ultra+ in the game's folder, each with its tab."),
        "game" => L.T("Steam reviews and Metacritic scores."),
        "media" => L.T("The store's trailers and screenshots."),
        "about" => L.T("The store's description of the game."),
        "details" => L.T("Developer, publisher, release date, genres and website."),
        "controllers" => L.T("Controller support, and the game's layout in Steam Input."),
        "wikis" => L.T("What the OptiScaler wiki and PCGamingWiki say about the game."),
        "requirements" => L.T("Minimum and recommended PC."),
        "languages" => L.T("Interface, audio and subtitles by language."),
        "reviews" => L.T("Steam's ratings and the most helpful and newest reviews."),
        _ => L.T("HowLongToBeat's times, and how far your own hours go."),
    };

    /// <summary>The gear in Dashboard view: which cards show, and their order (drag the rows). True when something changed.</summary>
    public static async Task<bool> CustomizeAsync(XamlRoot root, ElementTheme theme)
    {
        double width = Math.Clamp(root.Size.Width - 96, 360, 640);
        var list = new ListView { CanReorderItems = true, AllowDrop = true, CanDragItems = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = Math.Max(240, root.Size.Height - 330) };
        var boxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        void Fill(bool defaults)
        {
            list.Items.Clear();
            boxes.Clear();
            foreach (var c in defaults ? All.ToList() : Ordered())
            {
                var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = c.Id };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(Ui.Badge(c.Glyph.Length > 0 ? c.Glyph : "", TintOf(c.Id), 28));
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = c.Name, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = Info(c.Id), Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                var box = new CheckBox { MinWidth = 0, IsChecked = defaults || !Hidden(c.Id), VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(box, L.T("Show this card"));
                boxes[c.Id] = box;
                Grid.SetColumn(box, 2);
                grid.Children.Add(box);
                list.Items.Add(grid);
            }
        }
        Fill(false);
        var panel = new StackPanel { Spacing = 10, Width = width };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Drag the rows to change the order. Sections of the main column and cards of the side column each keep to their own column. Unticked ones are hidden on every game's dashboard. Each one's own menu can also move or hide it."),
            Style = Ui.Style("RowDescription"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(list);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = L.T("Customize the dashboard"),
            Content = panel,
            PrimaryButtonText = L.T("Save"),
            SecondaryButtonText = L.T("Reset to default"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = width + 48;
        dialog.SecondaryButtonClick += (_, e) => { e.Cancel = true; Fill(true); };
        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return false;
        AppState.Settings.DashboardOrder = [OrderMark, .. list.Items.OfType<Grid>().Select(g => (string)g.Tag)];
        AppState.Settings.DashboardHidden.Clear();
        foreach (var (id, box) in boxes) if (box.IsChecked != true) AppState.Settings.DashboardHidden.Add(id);
        SettingsStore.Save();
        return true;
    }

    // ---------- self-test ----------

    /// <summary>Parts that could not be put in their card (self-test: must stay 0).</summary>
    internal static int AdoptFailures;

    /// <summary>
    /// Self-test: the customize panel's changes without the panel: the order reversed with two cards hidden, then everything
    /// back, each time built again as Rebuild does. Fails when a part could not go into its card or a column holds a card it
    /// should not.
    /// </summary>
    internal string SelfTestReorder()
    {
        var settings = AppState.Settings;
        var order = settings.DashboardOrder.ToList();
        var hidden = settings.DashboardHidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int failures = AdoptFailures;
        string Check(string what)
        {
            var wanted = Ordered().Where(c => !Hidden(c.Id)).ToList();
            var main = _main.Children.OfType<Border>().Select(b => b.Tag as string).ToList();
            var side = _side.Children.OfType<Border>().Select(b => b.Tag as string).ToList();
            if (!main.SequenceEqual(wanted.Where(c => c.Wide).Select(c => c.Id)) || !side.SequenceEqual(wanted.Where(c => !c.Wide).Select(c => c.Id)))
                throw new InvalidOperationException($"{what}: columns are [{string.Join(",", main)}] and [{string.Join(",", side)}]");
            return $"{what} {main.Count}+{side.Count}";
        }
        try
        {
            settings.DashboardOrder = [OrderMark, .. Ordered().Select(c => c.Id).Reverse()];
            settings.DashboardHidden = new(StringComparer.OrdinalIgnoreCase) { "details", "reviews" };
            Rebuild();
            var first = Check("reversed, two hidden");
            settings.DashboardOrder = [];
            settings.DashboardHidden = new(StringComparer.OrdinalIgnoreCase);
            Rebuild();
            var second = Check("default, all shown");
            if (AdoptFailures != failures) throw new InvalidOperationException($"{AdoptFailures - failures} parts not put in their cards");
            return first + "; " + second;
        }
        finally
        {
            settings.DashboardOrder = order;
            settings.DashboardHidden = hidden;
            Rebuild();
        }
    }

    /// <summary>Self-test: the side cards' panel (a picture of it on a narrow window).</summary>
    internal FrameworkElement SelfTestSide => _side;

    /// <summary>Self-test: the cards shown, whether the news finished loading, the activity rows, and the posts read.</summary>
    internal (int Cards, bool NewsLoaded, int Activity) SelfTestState() =>
        (_cards.Values.Count(c => c.Visibility == Visibility.Visible && c.Parent is not null), _newsLoaded, Body("activity")?.Children.Count ?? 0);

    /// <summary>Self-test: opens the Spotlight's post (else the newest) in the reader; false when there is none.</summary>
    internal bool SelfTestReadPost()
    {
        var post = GameEvents.Spotlight(_events) ?? _events.FirstOrDefault();
        if (post is null) return false;
        Log.Info($"Self-test post: {post.Title}: {post.Blocks().Count} blocks, {post.Blocks().Count(b => b.Kind == NoteBlockKind.Heading)} headings");
        ShowPost(post, post.Outline.FirstOrDefault()?.Heading);
        return true;
    }

    /// <summary>Self-test: opens (or closes) the version history; false when the game has no update posts.</summary>
    internal bool SelfTestHistory(bool open)
    {
        if (_history?.Flyout is not Flyout f) return false;
        if (!open) { f.Hide(); return true; }
        _history.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.1, AnimationDesired = false });
        DispatcherQueue.TryEnqueue(() => f.ShowAt(_history));
        return true;
    }

    /// <summary>Self-test: posts read from Steam's event feed, of which updates and major updates, and the Spotlight's title.</summary>
    internal (int Events, int Updates, int Major, string? Spotlight) SelfTestNews() =>
        (_events.Count, _events.Count(e => e.IsUpdate), _events.Count(e => e.Kind == GameEventKind.MajorUpdate), GameEvents.Spotlight(_events)?.Title);
}
