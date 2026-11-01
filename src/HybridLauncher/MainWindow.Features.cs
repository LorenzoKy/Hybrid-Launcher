using HybridLauncher.Controls;
using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace HybridLauncher;

/// <summary>
/// The feature picker: at the first start, before the welcome tour, a sheet over the window asks which tools to keep
/// (Everything, No achievements, OptiScaler + ReShade, Only OptiScaler, Just a launcher, or a mix of the switches below).
/// Each choice has a card with its icons, and a live preview of a game's page shows the tabs and launch bar items that stay.
/// Turning a feature off only hides it (see <see cref="Features"/>); Settings → Features opens the picker again.
/// </summary>
public sealed partial class MainWindow
{
    Grid? _picker;
    HashSet<string> _pickOn = [];
    bool _pickSync;
    Action? _pickDone;
    readonly List<(Features.Preset Preset, Border Face, Border Check)> _pickCards = [];
    readonly Dictionary<string, (ToggleSwitch Switch, Border Face)> _pickSwitches = [];
    readonly Dictionary<string, FrameworkElement> _pickChips = [], _pickTabs = [];
    TextBlock? _pickMix;

    static readonly Color CardFill = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), CardHover = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);

    static Color Tint(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color AccentColor() =>
        ThemeRes.Current.TryGetValue("SystemAccentColor", out var v) && v is Color c ? c
        : (ThemeRes.Current["AccentFillColorDefaultBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(0xFF, 0x9A, 0x5C, 0xFF);

    public bool FeaturePickerOpen => _picker is not null;

    /// <summary>
    /// Opens the picker. <paramref name="firstStart"/>: the welcome tour follows, so there is no Cancel. <paramref name="done"/>
    /// runs once it has closed (applied or cancelled).
    /// </summary>
    public void ShowFeaturePicker(bool firstStart, Action? done = null)
    {
        if (_picker is not null) return;
        _pickDone = done;
        _pickOn = AppState.Settings.FeaturesChosen || !firstStart ? Features.Current(AppState.Settings) : [.. Features.Presets[0].On];
        _pickCards.Clear();
        _pickSwitches.Clear();
        _pickChips.Clear();
        _pickTabs.Clear();

        var card = new StackPanel { Spacing = 28, MaxWidth = 1240, Padding = new Thickness(40, 36, 40, 36), HorizontalAlignment = HorizontalAlignment.Stretch };
        card.Children.Add(PickerHeader(firstStart));

        var presets = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        foreach (var p in Features.Presets) presets.Children.Add(PresetCard(p));
        card.Children.Add(presets);

        card.Children.Add(Section(L.T("Preview"), L.T("A game's page with this choice: the launch bar and the tabs that stay.")));
        card.Children.Add(PickerPreview());

        card.Children.Add(Section(L.T("Fine-tune"), L.T("Turn single features on or off. A mix of your own works too.")));
        var tiles = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        foreach (var f in Features.All) tiles.Children.Add(FeatureTile(f));
        card.Children.Add(tiles);


        // Cards in as many columns as fit: five on a wide window, fewer on a narrow one.
        card.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width - 80;
            Reflow(presets, w >= 1000 ? 5 : w >= 620 ? 3 : w >= 400 ? 2 : 1);
            Reflow(tiles, w >= 900 ? 3 : w >= 540 ? 2 : 1);
        };

        var scroll = new ScrollViewer
        {
            Content = card,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // The buttons stay in a bar at the bottom, in reach whatever the window's height.
        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0x06, 0x07, 0x09)),
            Child = new Border { MaxWidth = 1240, Padding = new Thickness(40, 14, 40, 14), Child = PickerFooter(firstStart) },
        };
        Grid.SetRow(footer, 1);
        var overlay = new Grid
        {
            // Hybrid Launcher's own glass: Windows transparency effects can be off, and the window behind should stay a hint.
            Background = new GlassBrush(Color.FromArgb(0xD6, 0x09, 0x0A, 0x0D), 34),
            Opacity = 0,
            RowDefinitions = { new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, new RowDefinition { Height = GridLength.Auto } },
            Children = { scroll, footer },
        };
        Grid.SetRow(overlay, 1);
        // Clicks and keys stay on the sheet: nothing behind it reacts while it is open.
        overlay.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && !firstStart) { e.Handled = true; ClosePicker(apply: false); }
        };
        RootGrid.Children.Add(overlay);
        _picker = overlay;
        SyncPicker();

        // In: the glass fades up, the sheet rises a little. Opacity and transforms only, so it stays smooth at any refresh rate.
        var sb = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, overlay);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        var move = new TranslateTransform { Y = 24 };
        card.RenderTransform = move;
        var rise = new DoubleAnimation { From = 24, To = 0, Duration = TimeSpan.FromMilliseconds(380), EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 } };
        Storyboard.SetTarget(rise, move);
        Storyboard.SetTargetProperty(rise, "Y");
        sb.Children.Add(rise);
        sb.Completed += (_, _) => overlay.Opacity = 1;
        sb.Begin();
        Log.Info($"Feature picker open (first start: {firstStart})");
    }

    void ClosePicker(bool apply)
    {
        if (_picker is not { } overlay) return;
        _picker = null;
        if (apply)
        {
            Features.Apply(AppState.Settings, _pickOn);
            SettingsStore.Save();
            Log.Info($"Features: {(Features.Matching(_pickOn)?.Id ?? "custom")} ({string.Join(", ", _pickOn.Order())})");
        }
        // The page behind shows the new set of tabs at once.
        if (apply && ContentFrame.Content is GamePage page && page.GameId is { } id) ContentFrame.Navigate(typeof(GamePage), id);
        Motion.Close(overlay, () =>
        {
            RootGrid.Children.Remove(overlay);
            var done = _pickDone;
            _pickDone = null;
            done?.Invoke();
        });
    }

    FrameworkElement PickerHeader(bool firstStart)
    {
        var head = new StackPanel { Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = firstStart ? L.T("WELCOME · STEP 1 OF 2") : L.T("FEATURES"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 120,
            Foreground = new SolidColorBrush(AccentColor()),
        });
        head.Children.Add(new TextBlock { Text = L.T("Choose what you need"), FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        head.Children.Add(new TextBlock
        {
            Text = L.T("Pick a starting point. What you leave out is only hidden: no tabs, buttons or pop-ups for it. Nothing is installed or removed, and Settings → Features brings it back any time."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        return head;
    }

    static FrameworkElement Section(string title, string text)
    {
        var s = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, -14) };
        s.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        s.Children.Add(new TextBlock { Text = text, Opacity = 0.65, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        return s;
    }

    /// <summary>Lays the children out row by row in <paramref name="columns"/> equal columns.</summary>
    static void Reflow(Grid grid, int columns)
    {
        if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count > 0) return;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (grid.Children.Count + columns - 1) / columns;
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < grid.Children.Count; i++)
        {
            Grid.SetColumn((FrameworkElement)grid.Children[i], i % columns);
            Grid.SetRow((FrameworkElement)grid.Children[i], i / columns);
        }
    }

    /// <summary>A plain face that lights up under the pointer: no outline, no WinUI button chrome.</summary>
    static Button Pressable(Border face, string name, Action click)
    {
        var b = new Button
        {
            Content = face,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
        };
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
            b.Resources[key] = new SolidColorBrush(Colors.Transparent);
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        var veil = new Border { Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), Opacity = 0, IsHitTestVisible = false, CornerRadius = new CornerRadius(10) };
        // The face holds a Grid: the veil goes on top of it.
        if (face.Child is Grid g)
        {
            Grid.SetRowSpan(veil, 8);
            Grid.SetColumnSpan(veil, 8);
            g.Children.Add(veil);
        }
        b.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) => veil.Opacity = 1), true);
        b.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => veil.Opacity = 0), true);
        return b;
    }

    /// <summary>A round badge with a feature's icon in its colour.</summary>
    static FrameworkElement Badge(string glyph, Color tint, double size, bool crossed = false)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x17, 0x18, 0x1C)) });
        g.Children.Add(new Ellipse { Fill = new SolidColorBrush(WithAlpha(tint, 0x30)) });
        var icon = glyph == Icons.TrophyGlyph ? Icons.Trophy(size * 0.5) : Icons.Colored(glyph, size * 0.44, tint);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        if (crossed) icon.Opacity = 0.55;
        g.Children.Add(icon);
        if (crossed)
            g.Children.Add(new Rectangle
            {
                Width = size * 1.05,
                Height = Math.Max(3, size / 16),
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D)),
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = -40 },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        return g;
    }

    FrameworkElement PresetCard(Features.Preset p)
    {
        var lead = p.Hero.Select(Features.Find).FirstOrDefault(f => f is not null);
        var leadTint = lead is null ? Color.FromArgb(0xFF, 0x8A, 0x90, 0x9C) : Tint(lead.Tint);

        // The picture: the preset's icons, overlapping, on a wash of its main colour.
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = -12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var h in p.Hero)
        {
            var f = Features.Find(h);
            icons.Children.Add(Badge(f?.Glyph ?? h, f is null ? Color.FromArgb(0xFF, 0xE8, 0xE8, 0xEC) : Tint(f.Tint), 54, p.CrossedOut));
        }
        if (p.Id == "all")
            icons.Children.Add(new Border
            {
                Width = 54, Height = 54, CornerRadius = new CornerRadius(27),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0x23, 0x28)),
                Child = new TextBlock { Text = L.F("+{0}", Features.All.Length - 3), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 },
            });
        var hero = new Border
        {
            Height = 104,
            CornerRadius = new CornerRadius(8),
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0),
                EndPoint = new(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = WithAlpha(leadTint, 0x40), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x00, 0, 0, 0), Offset = 0.9 },
                },
            },
            Child = icons,
        };

        // What stays on a game page: one small icon per tab and launch bar item.
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var f in Features.All)
        {
            bool on = p.On.Contains(f.Id);
            var mini = f.Glyph == Icons.TrophyGlyph ? Icons.Trophy(13) : Icons.Colored(f.Glyph, 12, Tint(f.Tint));
            mini.Opacity = on ? 1 : 0.18;
            ToolTipService.SetToolTip(mini, L.T(f.Name));
            strip.Children.Add(mini);
        }

        var check = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(AccentColor()),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Child = new FontIcon { Glyph = "", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) },
        };

        var body = new Grid { Padding = new Thickness(10) , RowSpacing = 6 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(hero);
        body.Children.Add(check);
        var title = new TextBlock { Text = L.T(p.Name), FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(4, 6, 4, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(title, 1);
        body.Children.Add(title);
        var text = new TextBlock { Text = L.T(p.Description), FontSize = 12.5, Opacity = 0.68, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0) };
        Grid.SetRow(text, 2);
        body.Children.Add(text);
        strip.Margin = new Thickness(4, 6, 4, 2);
        Grid.SetRow(strip, 3);
        body.Children.Add(strip);

        var face = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(CardFill), Child = body };
        _pickCards.Add((p, face, check));
        return Pressable(face, L.T(p.Name), () => { _pickOn = [.. p.On]; SyncPicker(); });
    }

    FrameworkElement FeatureTile(Features.Feature f)
    {
        var tint = Tint(f.Tint);
        var sw = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        sw.Toggled += (_, _) =>
        {
            if (_pickSync) return;
            if (sw.IsOn) _pickOn.Add(f.Id); else _pickOn.Remove(f.Id);
            SyncPicker();
        };
        var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(14, 12, 10, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = Badge(f.Glyph, tint, 40);
        badge.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(badge);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = L.T(f.Name), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = L.T(f.Description), FontSize = 12.5, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(sw, 2);
        row.Children.Add(sw);
        var face = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(CardFill), Child = row };
        _pickSwitches[f.Id] = (sw, face);
        // The whole tile is the switch's target, not just the knob.
        face.Tapped += (_, e) => { if (e.OriginalSource is not DependencyObject d || !d.IsDescendantOf(sw)) sw.IsOn = !sw.IsOn; };
        return face;
    }

    /// <summary>A small model of a game's page: banner, launch bar, tab strip, a hint of content. It follows the switches.</summary>
    FrameworkElement PickerPreview()
    {
        var accent = AccentColor();
        var banner = new Grid
        {
            Height = 168,
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0),
                EndPoint = new(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x3A, 0x0B, 0x16), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x16, 0x1B, 0x2E), Offset = 0.55 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x0B, 0x2A, 0x33), Offset = 1 },
                },
            },
        };
        // A soft horizon line, so the banner reads as a picture rather than a flat block.
        banner.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0),
                EndPoint = new(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x00, 0, 0, 0), Offset = 0.35 },
                    new GradientStop { Color = Color.FromArgb(0xC8, 0x0B, 0x0C, 0x0F), Offset = 1 },
                },
            },
        });
        var bottom = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(22, 0, 22, 16) };
        bottom.Children.Add(new TextBlock { Text = L.T("Your game"), FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var play = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(22, 9, 26, 9),
            Background = new SolidColorBrush(accent),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Colors.White) },
                    new TextBlock { Text = L.T("PLAY"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 80, Foreground = new SolidColorBrush(Colors.White) },
                },
            },
        };
        bar.Children.Add(play);
        bar.Children.Add(Chip(null, "", L.T("2 h 14 min"), Color.FromArgb(0xFF, 0xC8, 0xC8, 0xD0)));
        bar.Children.Add(Chip(Features.Hdr, "", L.T("HDR · Smart"), Tint(Features.Find(Features.Hdr)!.Tint)));
        bar.Children.Add(Chip(Features.Cloud, "", L.T("Synced"), Tint(Features.Find(Features.Cloud)!.Tint)));
        bar.Children.Add(Chip(Features.Achievements, Icons.TrophyGlyph, L.T("12 / 40"), Icons.Gold));
        bottom.Children.Add(bar);
        banner.Children.Add(bottom);

        // The tab strip, in the user's tab order, each tab tied to the feature that shows it.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bool first = true;
        foreach (var t in GameTabs.Ordered())
        {
            var owner = Features.All.FirstOrDefault(f => f.Tabs.Contains(t.Id));
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            var icon = Icons.Colored(t.Glyph, 13, t.Tint);
            label.Children.Add(icon);
            label.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var tab = new Grid { Padding = new Thickness(12, 9, 12, 9) };
            tab.Children.Add(label);
            if (first)
            {
                tab.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
                tab.CornerRadius = new CornerRadius(6);
                tab.Children.Add(new Border { Height = 2, Width = 18, CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -6) });
            }
            first = false;
            tabs.Children.Add(tab);
            if (owner is not null) _pickTabs[t.Id] = tab;
            else if (AppState.Settings.HiddenTabs.Contains(t.Id)) tab.Visibility = Visibility.Collapsed;
        }
        var tabRow = new ScrollViewer
        {
            Content = tabs,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 10, 14, 6),
        };

        // A hint of the page under the tabs: grey bars where the overview would be.
        var skeleton = new Grid { ColumnSpacing = 16, Padding = new Thickness(22, 10, 22, 20) };
        skeleton.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        skeleton.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lines = new StackPanel { Spacing = 8 };
        foreach (var w in new[] { 0.92, 0.85, 0.6 })
            lines.Children.Add(new Border { Height = 9, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Left, Width = 520 * w });
        skeleton.Children.Add(lines);
        var side = new Border { Height = 44, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
        Grid.SetColumn(side, 1);
        skeleton.Children.Add(side);

        _pickMix = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 12, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var page = new StackPanel { Children = { banner, tabRow, skeleton } };
        var frame = new Grid
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x11, 0x14)),
            Children = { page, _pickMix },
        };
        return frame;
    }

    /// <summary>One item of the preview's launch bar; <paramref name="feature"/> null = always there.</summary>
    FrameworkElement Chip(string? feature, string glyph, string text, Color tint)
    {
        var icon = glyph == Icons.TrophyGlyph ? Icons.Trophy(15) : Icons.Colored(glyph, 14, tint);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Background = new GlassBrush(Color.FromArgb(0x7A, 0x0A, 0x0B, 0x0E), 20),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { icon, new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        if (feature is not null) _pickChips[feature] = chip;
        return chip;
    }

    FrameworkElement PickerFooter(bool firstStart)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        note.Children.Add(new FontIcon { Glyph = "", FontSize = 13, Opacity = 0.6 });
        note.Children.Add(new TextBlock { Text = L.T("Change it any time in Settings → Features."), Opacity = 0.65, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(note);
        if (!firstStart)
        {
            var cancel = new Button { Content = L.T("Cancel"), MinWidth = 110, Height = 40 };
            cancel.Click += (_, _) => ClosePicker(apply: false);
            Grid.SetColumn(cancel, 1);
            row.Children.Add(cancel);
        }
        var go = new Button
        {
            Content = firstStart ? L.T("Continue to the tour") : L.T("Apply"),
            MinWidth = 180,
            Height = 40,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        go.Click += (_, _) => ClosePicker(apply: true);
        go.Loaded += (_, _) => go.Focus(FocusState.Programmatic);
        Grid.SetColumn(go, 2);
        row.Children.Add(go);
        return row;
    }

    /// <summary>Cards, switches and the preview follow <see cref="_pickOn"/>.</summary>
    void SyncPicker()
    {
        var match = Features.Matching(_pickOn);
        var accent = AccentColor();
        foreach (var (preset, face, check) in _pickCards)
        {
            bool on = ReferenceEquals(preset, match);
            face.Background = new SolidColorBrush(on ? WithAlpha(accent, 0x38) : CardFill);
            check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        _pickSync = true;
        foreach (var (id, (sw, face)) in _pickSwitches)
        {
            bool on = _pickOn.Contains(id);
            sw.IsOn = on;
            face.Background = new SolidColorBrush(on ? CardHover : CardFill);
        }
        _pickSync = false;
        foreach (var (id, chip) in _pickChips) Show(chip, _pickOn.Contains(id));
        foreach (var (tabId, tab) in _pickTabs) Show(tab, Features.All.First(f => f.Tabs.Contains(tabId)) is var f && _pickOn.Contains(f.Id));
        if (_pickMix is not null)
            _pickMix.Text = match is null ? L.T("Your own mix") : L.F("{0} · {1} of {2} features", L.T(match.Name), _pickOn.Count, Features.All.Length);

        static void Show(FrameworkElement e, bool show)
        {
            bool shown = e.Visibility == Visibility.Visible && e.Opacity > 0.5;
            if (show == shown) return;
            if (show) { e.Opacity = 1; Motion.Open(e); }
            else Motion.Close(e);
        }
    }

    /// <summary>Self-test: opens the picker, clicks through every preset and a custom mix, applies one, then puts everything back.</summary>
    internal void SelfTestFeaturePicker(int step)
    {
        switch (step)
        {
            case 0:
                ShowFeaturePicker(firstStart: true);
                if (_picker is null) throw new InvalidOperationException("the feature picker did not open");
                break;
            case <= 5:
                _pickOn = [.. Features.Presets[step - 1].On];
                SyncPicker();
                if (Features.Matching(_pickOn) != Features.Presets[step - 1]) throw new InvalidOperationException("the preset did not select");
                break;
            case 6:
                _pickOn = [Features.Opti, Features.Hdr];
                SyncPicker();
                ClosePicker(apply: true);
                var s = AppState.Settings;
                if (!s.HiddenTabs.Contains("mods") || s.HiddenTabs.Contains("opti") || !s.HiddenPageParts.Contains(PageParts.Cloud) || s.HiddenPageParts.Contains(PageParts.Hdr) || s.AchievementPopups)
                    throw new InvalidOperationException("the chosen features were not applied");
                break;
            default:
                Features.Apply(AppState.Settings, Features.Presets[0].On.ToHashSet());
                SettingsStore.Save();
                if (Features.Current(AppState.Settings).Count != Features.All.Length) throw new InvalidOperationException("Everything did not bring every feature back");
                break;
        }
    }
}
