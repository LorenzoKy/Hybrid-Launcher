using HybridLauncher.Controls;
using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HybridLauncher;

/// <summary>
/// The welcome tour at the first start: a tip beside each real part of the app, one at a time. The tour walks through the
/// window itself (it opens a game's page, Library, Downloads, My PC) so every tip points at the control it talks about, never
/// at a guess; a tip never covers what it explains. Next and Back move through it, "Skip tour" or Esc ends it at any step,
/// and Settings → Welcome tour starts it again. Steps about a game page are left out while no game is installed.
/// </summary>
public sealed partial class MainWindow
{
    // A fresh tip for every step: a TeachingTip reopened on a new target keeps the old one's place, and closing it to move on
    // must never count as the user closing the tour.
    TeachingTip? _tour;
    bool _touring;
    int _tourStep;
    TourStep[] _steps = [];
    object? _tourStart;

    /// <param name="Prepare">Opens the page the tip is about, before the tip shows.</param>
    sealed record TourStep(string Title, string Text, Func<FrameworkElement?> Target, Action? Prepare = null);

    GamePage? TourGame => ContentFrame.Content as GamePage;

    TourStep[] BuildTourSteps()
    {
        var game = AppState.Games.FirstOrDefault(g => !g.NotInstalled && !g.Installing);
        var list = new List<TourStep>
        {
            new(L.T("Welcome to Hybrid Launcher"),
                L.T("Your Steam, Epic, GOG and Xbox games in one place, with the tools PC players add to them: OptiScaler, ReShade, RenoDX, Luma, cloud saves and Smart HDR. This tour shows where each one is, on the real window; it takes about a minute."),
                () => null),
            new(L.T("Your games"),
                L.T("Every installed game, grouped by launcher. Type here to search; the button beside the box sorts and filters. Right-click a game to pin it on top."),
                () => SearchBox, () => Nav.SelectedItem = null),
        };
        if (game is not null)
        {
            list.Add(new(L.T("A game's page"),
                L.F("This is {0}'s page. Play starts the game; while it runs the same button stops it. Last played and play time are beside it.", game.Title),
                () => TourGame?.TourPlay, () => OpenGame(game.Id)));
            if (!PageParts.HiddenEverywhere(AppState.Settings, PageParts.Hdr))
            list.Add(new(L.T("Smart HDR"),
                L.T("Turn it on and Windows HDR switches on when this game starts and off when it closes, even after a crash. When it is off, the tile shows what Windows HDR is now: On or Off."),
                () => TourGame?.TourHdr));
            // The tools the feature picker left out are not talked about.
            bool opti = !GameTabs.HiddenEverywhere("opti"), mods = !GameTabs.HiddenEverywhere("mods");
            list.Add(new(L.T("Tools for this game"),
                opti && mods ? L.T("Game has the overview, trailer and controller support. OptiScaler installs in one click and swaps DLSS, FSR and XeSS; ReShade & Mods adds ReShade, RenoDX and Luma; PCGamingWiki lists the game's known fixes. Drag a tab to reorder it.")
                : opti ? L.T("Game has the overview, trailer and controller support. OptiScaler installs in one click and swaps DLSS, FSR and XeSS. Drag a tab to reorder it.")
                : mods ? L.T("Game has the overview, trailer and controller support. ReShade & Mods adds ReShade, RenoDX and Luma. Drag a tab to reorder it.")
                : L.T("Game has the overview, trailer and controller support. Drag a tab to reorder it; Settings → Features turns the mod tools on."),
                () => TourGame?.TourTabs));
            list.Add(new(L.T("Game settings"),
                L.T("The gear opens the game's properties: launch options, which exe to start, desktop shortcut, files and uninstall."),
                () => TourGame?.TourMore));
        }
        list.Add(new(L.T("Library"),
            L.T("Every game you own as covers, installed or not, with search and filters. A game that is not installed has Download on its page."),
            () => NavItem(LibraryTag), () => Nav.SelectedItem = NavItem(LibraryTag)));
        list.Add(new(L.T("Downloads"),
            L.T("Sign in to Steam, Epic or GOG with Accounts at the top right of this page to install the games you own. Downloads and game updates show here, with pause and resume."),
            () => _downloadsItem, () => Nav.SelectedItem = _downloadsItem));
        list.Add(new(L.T("My PC"),
            L.T("Your graphics card, processor, memory and displays, with the exact version of every upscaler and frame generation your card runs, and Windows' own graphics switches."),
            () => NavItem(HardwareTag), () => Nav.SelectedItem = NavItem(HardwareTag)));
        list.Add(new(L.T("Missing a game?"),
            L.T("Add its .exe, or a whole folder of games: each sub-folder becomes a game."),
            () => AddGameItem));
        list.Add(new(L.T("Settings"),
            L.T("Colour themes, cloud saves (OneDrive, MEGA or a folder), achievement pop-ups, game updates and the app's own updates. This tour is in Settings → Features → Welcome tour."),
            () => Nav.SettingsItem as FrameworkElement));
        list.Add(new(L.T("You're set"),
            L.T("Open any game from the list to start. Have fun!"),
            () => null));
        return list.ToArray();
    }

    NavigationViewItem? NavItem(string tag) => Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag);

    /// <summary>Starts the tour from its first tip (first start, or Settings → Take the tour again).</summary>
    public void StartTour()
    {
        EndTour(markSeen: false);
        _steps = BuildTourSteps();
        _tourStep = 0;
        _tourStart = Nav.SelectedItem;
        _touring = true;
        ShowTourStep();
    }

    TeachingTip NewTourTip()
    {
        var tip = new TeachingTip
        {
            IsLightDismissEnabled = false,
            PlacementMargin = new Thickness(12),
            CloseButtonContent = L.T("Skip tour"),
            ShouldConstrainToRootBounds = true,
        };
        tip.ActionButtonClick += (_, _) => MoveTour(+1);
        tip.CloseButtonClick += (_, _) => EndTour(markSeen: true);
        // Esc ends the tour too; a tip closed by the tour itself (to move on) does not.
        tip.Closed += (s, e) =>
        {
            // A tip leaves the window only once it has fully closed: removed while open, its popup stays on screen, dead.
            RootGrid.Children.Remove(tip);
            if (ReferenceEquals(s, _tour))
            {
                _tour = null;
                if (e.Reason != TeachingTipCloseReason.Programmatic) EndTour(markSeen: true);
            }
            if (_afterTourTipClosed is { } next) { _afterTourTipClosed = null; next(); }
        };
        return tip;
    }

    Action? _afterTourTipClosed;

    /// <summary>Closes the tip on screen without ending the tour, then runs <paramref name="then"/> once it is gone.</summary>
    void RetireTourTip(Action? then = null)
    {
        if (_tour is not { } tip) { then?.Invoke(); return; }
        _tour = null;
        _afterTourTipClosed = then;
        tip.IsOpen = false;
        EnsureTipClosed(tip, 0);
    }

    /// <summary>
    /// A tip told to close while it was still opening (Next pressed quickly, or its control rebuilt) could stay on screen and
    /// never send Closed: it then sat behind the next tips and the tour skipped a step. It is told again until it is gone, and
    /// the tour moves on regardless.
    /// </summary>
    void EnsureTipClosed(TeachingTip tip, int tries)
    {
        TourLater(400, () =>
        {
            if (!RootGrid.Children.Contains(tip)) return;
            if (tries < 4)
            {
                // Open again (if WinUI dropped the close) and close once the opening is over.
                if (!tip.IsOpen) tip.IsOpen = true;
                TourLater(250, () => tip.IsOpen = false);
                EnsureTipClosed(tip, tries + 1);
                return;
            }
            Log.Warn("Tour: a tip did not close");
            if (_afterTourTipClosed is { } next) { _afterTourTipClosed = null; next(); }
        });
    }

    void MoveTour(int by)
    {
        if (!_touring) return;
        _tourStep += by;
        if (_tourStep >= _steps.Length) { EndTour(markSeen: true); return; }
        _tourStep = Math.Max(0, _tourStep);
        RetireTourTip(() => DispatcherQueue.TryEnqueue(ShowTourStep));
    }

    void ShowTourStep()
    {
        if (!_touring) return;
        int at = _tourStep;
        var step = _steps[at];
        if (step.Prepare is { } prepare)
        {
            try { prepare(); } catch (Exception ex) { Log.Warn($"Tour: {ex.Message}"); }
            // The page it opened lays out first: the tip then finds its control where it really is.
            // Only if the tour is still on this step (Next or Back pressed twice quickly moves on without it).
            TourLater(450, () => { if (_touring && _tourStep == at) OpenTourTip(step); });
        }
        else OpenTourTip(step);
    }

    // Through Later, which holds the timer: a timer nothing held was collected while Downloads loaded, the next tip never
    // opened and the tour looked closed after Next.
    void TourLater(int ms, Action action) => Later.Run(DispatcherQueue, TimeSpan.FromMilliseconds(ms), action);

    void OpenTourTip(TourStep step, int retries = 0)
    {
        if (!_touring || _tour is not null || _closed) return;
        var target = step.Target();
        // A target that is not on screen (folded pane, narrow window) gets the tip in the middle instead.
        if (target is { XamlRoot: null } or { ActualWidth: <= 0 } or { Visibility: not Visibility.Visible }) target = null;
        // A tip's size limits are read from the app's resources when its template loads (its own Resources are not looked at):
        // the welcome is wider and taller, the other steps keep WinUI's sizes.
        SizeTourTips(welcome: _tourStep == 0);
        var tip = NewTourTip();
        tip.Target = target;
        tip.PreferredPlacement = target is null ? TeachingTipPlacementMode.Center
            : target.IsDescendantOf(Nav) && !target.IsDescendantOf(ContentFrame) ? TeachingTipPlacementMode.Right
            : TeachingTipPlacementMode.Bottom;
        bool last = _tourStep == _steps.Length - 1;
        tip.ActionButtonContent = _tourStep == 0 ? L.T("Start") : last ? L.T("Let's go") : L.T("Next");
        if (_tourStep == 0)
        {
            // Welcome: wider, larger text, and the support note with Ko-fi (seen whether the tour is then started or skipped).
            tip.HeroContent = TourHero();
            tip.Content = WelcomeContent(step);
        }
        else
        {
            tip.Title = step.Title;
            tip.Subtitle = step.Text;
            tip.Content = TourFooter(last);
        }
        RootGrid.Children.Add(tip);
        _tour = tip;
        tip.IsOpen = true;
        // The page a step opens can rebuild the control the tip points at (Downloads refreshes the game list): the tip would
        // vanish with it, leaving the tour stuck. It moves to the new control instead, a few times at most.
        if (target is not null && retries < 3)
            target.Unloaded += (_, _) =>
            {
                if (!ReferenceEquals(_tour, tip)) return;
                _tour = null;
                tip.IsOpen = false;
                EnsureTipClosed(tip, 0);
                TourLater(300, () => { if (_touring && _tour is null && _steps[_tourStep] == step) OpenTourTip(step, retries + 1); });
            };
    }

    /// <summary>Under each tip: where you are in the tour, and Back.</summary>
    FrameworkElement TourFooter(bool last)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!last)
            row.Children.Add(new TextBlock { Text = L.F("{0} of {1}", _tourStep, _steps.Length - 2), Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var back = new HyperlinkButton { Content = L.T("Back"), Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };
        back.Click += (_, _) => MoveTour(-1);
        Grid.SetColumn(back, 1);
        row.Children.Add(back);
        return row;
    }

    static readonly string[] TipSizeKeys = ["TeachingTipMinWidth", "TeachingTipMaxWidth", "TeachingTipMaxHeight"];
    static Dictionary<string, object>? _tipSizes;

    static void SizeTourTips(bool welcome)
    {
        var res = Application.Current.Resources;
        _tipSizes ??= TipSizeKeys.Where(res.ContainsKey).ToDictionary(k => k, k => res[k]);
        if (welcome)
        {
            res["TeachingTipMinWidth"] = 540.0;
            res["TeachingTipMaxWidth"] = 540.0;
            res["TeachingTipMaxHeight"] = 900.0;
        }
        else foreach (var k in TipSizeKeys)
        {
            if (_tipSizes.TryGetValue(k, out var v)) res[k] = v; else res.Remove(k);
        }
    }

    public const string KofiUrl = "https://ko-fi.com/lorenzoky";

    /// <summary>The welcome step's body: its own big title and text, then a card asking for support, with the Ko-fi button.</summary>
    FrameworkElement WelcomeContent(TourStep step)
    {
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(0, 4, 0, 2) };
        body.Children.Add(new TextBlock { Text = step.Title, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = step.Text, FontSize = 15, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Opacity = 0.9 });

        var tint = Palettes.Adapt(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x5E, 0x5B));
        var card = new Grid { ColumnSpacing = 14, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 6, 0, 0), CornerRadius = new CornerRadius(10),
                              Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1E, tint.R, tint.G, tint.B)) };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, tint.R, tint.G, tint.B)),
            Child = new FontIcon { Glyph = "", FontSize = 18, Foreground = new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(new TextBlock { Text = L.T("Developed by LorenzoKy"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = L.F("If {0} has earned a place on your PC, you can buy me a slice of pizza.", AppInfo.Name),
            FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
        });
        var kofi = new Button { Style = Ui.Style("AccentButtonStyle"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 4, 0, 0) };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        label.Children.Add(new TextBlock { Text = L.T("Support on Ko-fi") });
        kofi.Content = label;
        ToolTipService.SetToolTip(kofi, "ko-fi.com/lorenzoky");
        kofi.Click += (_, _) =>
        {
            Log.Info("Welcome: Ko-fi opened");
            try { GameLauncher.OpenUri(KofiUrl); }
            catch (Exception ex) { ShowError(L.T("Action failed"), ex.Message, ex); }
        };
        text.Children.Add(kofi);
        Grid.SetColumn(text, 1);
        card.Children.Add(text);
        body.Children.Add(card);
        return body;
    }

    static FrameworkElement TourHero() => new Border
    {
        Height = 150,
        Background = new LinearGradientBrush
        {
            StartPoint = new(0, 0),
            EndPoint = new(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x34, 0x08, 0x12), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x0C, 0x0C, 0x0E), Offset = 0.6 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x1C, 0x1A, 0x17), Offset = 1 },
            },
        },
        Child = new Image
        {
            Source = new BitmapImage { UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.png")), DecodePixelHeight = 160 },
            Height = 100,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    void EndTour(bool markSeen)
    {
        if (_tipSizes is not null) SizeTourTips(welcome: false);
        if (_touring)
        {
            _touring = false;
            RetireTourTip();
            // Back where the tour started.
            if (markSeen && !ReferenceEquals(Nav.SelectedItem, _tourStart))
                try { Nav.SelectedItem = _tourStart; } catch { }
        }
        if (markSeen && !AppState.Settings.TourSeen)
        {
            AppState.Settings.TourSeen = true;
            SettingsStore.Save();
        }
    }

    /// <summary>At the first start, once the window has settled: the feature picker, then the tour (not in the self-test, not from the tray).</summary>
    void MaybeStartTour()
    {
        if (AppState.Settings.TourSeen || _hidden || Environment.GetCommandLineArgs().Contains("--selftest", StringComparer.OrdinalIgnoreCase)) return;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.5);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (_closed || _hidden || AppState.Settings.TourSeen) return;
            // First the features to keep, then the tour shows only those.
            if (!AppState.Settings.FeaturesChosen) ShowFeaturePicker(firstStart: true, StartTour);
            else StartTour();
        };
        _tourTimer = timer;
        timer.Start();
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _tourTimer;

    /// <summary>Self-test: the tour's steps one by one.</summary>
    internal bool SelfTestTourStep(int step)
    {
        // The step before: one tip on screen, and a step that opens a page did open it (a tip that never closed blocked both).
        if (RootGrid.Children.OfType<TeachingTip>().Count(t => t.IsOpen) > 1)
            throw new InvalidOperationException("more than one tour tip is open");
        if (step > 0 && _touring && _steps[_tourStep].Title == L.T("A game's page") && TourGame is null)
            throw new InvalidOperationException("the tour did not open the game's page");
        if (step == 0) StartTour();
        else if (step >= _steps.Length) { EndTour(markSeen: false); return false; }
        else MoveTour(+1);
        return true;
    }
}

static class TreeExtensions
{
    /// <summary>Whether <paramref name="element"/> sits inside <paramref name="ancestor"/> in the visual tree.</summary>
    public static bool IsDescendantOf(this DependencyObject element, DependencyObject ancestor)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (ReferenceEquals(e, ancestor)) return true;
        return false;
    }
}
