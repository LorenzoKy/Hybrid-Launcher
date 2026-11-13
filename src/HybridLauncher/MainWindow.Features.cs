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
/// (Everything, No achievements, OptiScaler + Mods, Only OptiScaler, Just a launcher, or a mix of the switches below).
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

    static Color Tint(uint argb) => Palettes.Adapt(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

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

        // 1.4.0-rc.20 layout: the choices on the left (starting points as rows, then every feature in one list), the live
        // preview of a game page on the right; one column on a narrow window, the preview after the choices.
        var card = new StackPanel { Spacing = 26, MaxWidth = 1320, Padding = new Thickness(40, 36, 40, 36), HorizontalAlignment = HorizontalAlignment.Stretch };
        card.Children.Add(PickerHeader(firstStart));

        var presets = new StackPanel { Spacing = 8 };
        foreach (var p in Features.Presets) presets.Children.Add(PresetCard(p));
        var tiles = new StackPanel();
        foreach (var f in Features.All)
        {
            if (tiles.Children.Count > 0) tiles.Children.Add(new Border { Height = 1, Margin = new Thickness(16, 0, 16, 0), Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)) });
            tiles.Children.Add(FeatureTile(f));
        }
        var list = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(CardFill), Child = tiles };

        var choices = new StackPanel { Spacing = 12 };
        choices.Children.Add(Section(L.T("Starting point")));
        choices.Children.Add(presets);
        var fine = new StackPanel { Spacing = 12, Margin = new Thickness(0, 14, 0, 0) };
        fine.Children.Add(Section(L.T("Each feature")));
        fine.Children.Add(list);
        choices.Children.Add(fine);

        var preview = new StackPanel { Spacing = 12 };
        preview.Children.Add(Section(L.T("Preview")));
        preview.Children.Add(PickerPreview());
        preview.Children.Add(new TextBlock { Text = L.T("A game's page with this choice: the launch bar and the tabs that stay."), FontSize = 12.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        var columns = new Grid { ColumnSpacing = 32, RowSpacing = 26 };
        columns.Children.Add(choices);
        columns.Children.Add(preview);
        card.Children.Add(columns);

        // Wide: two columns, the preview beside the choices. Narrow: one column, the preview after them.
        card.SizeChanged += (_, e) =>
        {
            bool wide = e.NewSize.Width - 80 >= 1000;
            if (wide == (columns.ColumnDefinitions.Count == 2)) return;
            columns.ColumnDefinitions.Clear();
            columns.RowDefinitions.Clear();
            if (wide)
            {
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
                Grid.SetColumn(preview, 1); Grid.SetRow(preview, 0);
            }
            else
            {
                columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(preview, 0); Grid.SetRow(preview, 1);
            }
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
            Child = new Border { MaxWidth = 1320, Padding = new Thickness(40, 14, 40, 14), Child = PickerFooter(firstStart) },
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
        if (apply && Shown is GamePage page && page.GameId is { } id) ReloadGame(id);
        Motion.Close(overlay, () =>
        {
            // The picker held the focus: gone from the tree, WinUI hands it to the search box without a GettingFocus event.
            bool searchHadFocus = SearchHasFocus();
            RootGrid.Children.Remove(overlay);
            KeepFocusOffSearch(searchHadFocus);
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

    /// <summary>A group label in the app's own style (small capitals, as on Settings and the Dashboard).</summary>
    static FrameworkElement Section(string title) =>
        new TextBlock { Text = title.ToUpper(System.Globalization.CultureInfo.CurrentCulture), Style = Ui.Style("GroupHeader") };

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
        // The glyph alone in its colour (1.4.0-rc.9: no disc behind it, as everywhere else in the app).
        var icon = glyph == Icons.TrophyGlyph ? Icons.Trophy(size * 0.62) : Icons.Colored(glyph, size * 0.6, tint);
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

    /// <summary>
    /// One starting point as a row: its icons on a wash of its main colour, its name and what it keeps, a small strip of the
    /// features it keeps (lit) and leaves out (faint), and a round mark on the right that fills when it is the one chosen.
    /// </summary>
    FrameworkElement PresetCard(Features.Preset p)
    {
        var lead = p.Hero.Select(Features.Find).FirstOrDefault(f => f is not null);
        var leadTint = lead is null ? Color.FromArgb(0xFF, 0x8A, 0x90, 0x9C) : Tint(lead.Tint);

        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = -4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var h in p.Hero.Take(2))
        {
            var f = Features.Find(h);
            icons.Children.Add(Badge(f?.Glyph ?? h, f is null ? Color.FromArgb(0xFF, 0xE8, 0xE8, 0xEC) : Tint(f.Tint), 34, p.CrossedOut));
        }
        var hero = new Border
        {
            Width = 80, Height = 58,
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0),
                EndPoint = new(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = WithAlpha(leadTint, 0x4A), Offset = 0 },
                    new GradientStop { Color = WithAlpha(leadTint, 0x10), Offset = 1 },
                },
            },
            Child = icons,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var f in Features.All)
        {
            bool on = p.On.Contains(f.Id);
            var mini = f.Glyph == Icons.TrophyGlyph ? Icons.Trophy(13) : Icons.Colored(f.Glyph, 12, Tint(f.Tint));
            mini.Opacity = on ? 1 : 0.16;
            ToolTipService.SetToolTip(mini, L.T(f.Name));
            strip.Children.Add(mini);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var name = new TextBlock { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        name.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = L.T(p.Name) });
        name.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "   " + L.F("{0} of {1}", p.On.Length, Features.All.Length), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Normal, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) });
        text.Children.Add(name);
        text.Children.Add(new TextBlock { Text = L.T(p.Description), FontSize = 12.5, Opacity = 0.66, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(strip);

        // The mark: a faint ring, filled with the accent and a tick when chosen.
        var dot = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(AccentColor()),
            Child = new FontIcon { Glyph = "\uE73E", FontSize = 11, Foreground = new SolidColorBrush(Colors.White) },
        };
        var ring = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)) });
        ring.Children.Add(dot);

        var body = new Grid { ColumnSpacing = 16, Padding = new Thickness(12, 12, 18, 12) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(hero);
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        Grid.SetColumn(ring, 2);
        body.Children.Add(ring);

        var face = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(CardFill), Child = body };
        _pickCards.Add((p, face, dot));
        return Pressable(face, L.T(p.Name), () => { _pickOn = [.. p.On]; SyncPicker(); });
    }

    /// <summary>One feature as a row of the list: its icon, name and what it does, and its switch. The whole row toggles it.</summary>
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
        var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(16, 11, 8, 11) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = Badge(f.Glyph, tint, 30);
        badge.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(badge);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = L.T(f.Name), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = L.T(f.Description), FontSize = 12.5, Opacity = 0.62, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(sw, 2);
        row.Children.Add(sw);
        var face = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Colors.Transparent), Child = row };
        _pickSwitches[f.Id] = (sw, face);
        // The whole row is the switch's target, not just the knob.
        face.Tapped += (_, e) => { if (e.OriginalSource is not DependencyObject d || !d.IsDescendantOf(sw)) sw.IsOn = !sw.IsOn; };
        return face;
    }

    /// <summary>
    /// A small model of a game's page as it looks now: the banner with the title, the launch bar on its glass (Play drawn like
    /// the real button, then the facts that stay), the tab bar, and the Dashboard's two columns. It follows the switches.
    /// </summary>
    FrameworkElement PickerPreview()
    {
        var accent = PlayButton.Accent();
        var banner = new Grid
        {
            Height = 214,
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
        // A glow of the accent from the top right, and the foot darkened for the launch bar, as the real banner.
        banner.Children.Add(new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new(0.85, 0.1), GradientOrigin = new(0.85, 0.1), RadiusX = 0.6, RadiusY = 0.9,
                GradientStops =
                {
                    new GradientStop { Color = WithAlpha(accent, 0x55), Offset = 0 },
                    new GradientStop { Color = WithAlpha(accent, 0x00), Offset = 1 },
                },
            },
        });
        banner.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0),
                EndPoint = new(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x00, 0, 0, 0), Offset = 0.3 },
                    new GradientStop { Color = Color.FromArgb(0xD0, 0x0B, 0x0C, 0x0F), Offset = 1 },
                },
            },
        });
        var bottom = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 14) };
        bottom.Children.Add(new TextBlock { Text = L.T("Your game"), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0) });

        // Play: the game page's face in small (its colour in two tones, its solid triangle, the word in capitals).
        var face = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        face.GradientStops.Add(new GradientStop { Color = PlayButton.Mix(accent, Colors.White, 0.08), Offset = 0 });
        face.GradientStops.Add(new GradientStop { Color = PlayButton.Deepen(accent), Offset = 1 });
        var ink = Contrast.BrushOn(accent);
        var playIcon = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), PlayButton.PlayPath),
            Fill = ink, Width = 14, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        };
        var play = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 0, 22, 0),
            Height = 40,
            Background = face,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    playIcon,
                    new TextBlock { Text = L.T("Play").ToUpper(System.Globalization.CultureInfo.CurrentCulture), FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 60, Foreground = ink, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(play);
        bar.Children.Add(Chip(null, "\uE916", L.T("Play time"), L.T("2 h 14 min"), Color.FromArgb(0xFF, 0xC8, 0xC8, 0xD0)));
        bar.Children.Add(Chip(Features.Hdr, "\uE706", L.T("HDR"), L.T("Smart"), Tint(Features.Find(Features.Hdr)!.Tint)));
        bar.Children.Add(Chip(Features.Cloud, "\uE753", L.T("Cloud"), L.T("Synced"), Tint(Features.Find(Features.Cloud)!.Tint)));
        bar.Children.Add(Chip(Features.Achievements, Icons.TrophyGlyph, L.T("Achievements"), L.T("12 / 40"), Icons.Gold));
        var launch = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Background = new GlassBrush(Color.FromArgb(0x8C, 0x0A, 0x0B, 0x0E), 20),
            Child = new ScrollViewer
            {
                Content = bar,
                HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        bottom.Children.Add(launch);
        banner.Children.Add(bottom);

        // The tab bar, in the user's tab order, each tab tied to the feature that shows it; the first one open.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        bool first = true;
        foreach (var t in GameTabs.Ordered())
        {
            var owner = Features.All.FirstOrDefault(f => f.Tabs.Contains(t.Id));
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            label.Children.Add(Icons.Colored(t.Glyph, 13, t.Tint));
            label.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, FontWeight = first ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });
            var tab = new Grid { Padding = new Thickness(11, 8, 11, 8), CornerRadius = new CornerRadius(8) };
            tab.Children.Add(label);
            if (first)
            {
                tab.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                tab.Children.Add(new Border { Height = 2, Width = 22, CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -5) });
            }
            first = false;
            tabs.Children.Add(tab);
            if (owner is not null) _pickTabs[t.Id] = tab;
            else if (AppState.Settings.HiddenTabs.Contains(t.Id)) tab.Visibility = Visibility.Collapsed;
        }
        var tabRow = new Border
        {
            Margin = new Thickness(14, 12, 14, 0),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
            Child = new ScrollViewer
            {
                Content = tabs,
                HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };

        // A hint of the Dashboard under the tabs: the main column's picture and lines, the side cards with their colour wash.
        var dash = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 12, 14, 16) };
        dash.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        dash.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var main = new StackPanel { Spacing = 8 };
        main.Children.Add(new Border { Height = 92, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)) });
        foreach (var w in new[] { 0.9, 0.7 })
            main.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Left, Width = 300 * w });
        dash.Children.Add(main);
        var sideCards = new StackPanel { Spacing = 8 };
        foreach (var (tint, h) in new[] { (Icons.Gold, 62.0), (Tint(0xFF7C6CFF), 44.0) })
            sideCards.Children.Add(new Border
            {
                Height = h,
                CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush
                {
                    StartPoint = new(0, 0), EndPoint = new(1, 1),
                    GradientStops = { new GradientStop { Color = WithAlpha(tint, 0x30), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Offset = 0.6 } },
                },
            });
        Grid.SetColumn(sideCards, 1);
        dash.Children.Add(sideCards);

        _pickMix = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 12, 14, 0),
            Padding = new Thickness(10, 3, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var mix = new Border
        {
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(0, 12, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new GlassBrush(Color.FromArgb(0x8C, 0x0A, 0x0B, 0x0E), 16),
            Child = _pickMix,
        };
        _pickMix.Margin = new Thickness(0);

        var page = new StackPanel { Children = { banner, tabRow, dash } };
        return new Grid
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x11, 0x14)),
            Children = { page, mix },
        };
    }

    /// <summary>One item of the preview's launch bar, as the real one: its icon, a small label over its value. <paramref name="feature"/> null = always there.</summary>
    FrameworkElement Chip(string? feature, string glyph, string label, string value, Color tint)
    {
        var icon = glyph == Icons.TrophyGlyph ? Icons.Trophy(16) : Icons.Colored(glyph, 15, tint);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label.ToUpper(System.Globalization.CultureInfo.CurrentCulture), FontSize = 10, CharacterSpacing = 60, Opacity = 0.6 });
        text.Children.Add(new TextBlock { Text = value, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { icon, text } };
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
            face.Background = new SolidColorBrush(on ? WithAlpha(accent, 0x2E) : CardFill);
            check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        _pickSync = true;
        foreach (var (id, (sw, face)) in _pickSwitches)
        {
            bool on = _pickOn.Contains(id);
            sw.IsOn = on;
            face.Opacity = on ? 1 : 0.62;
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
