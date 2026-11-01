using HybridLauncher.Core;
using HybridLauncher.Core.Media;
using HybridLauncher.Core.Opti;
using HybridLauncher.Core.Steam;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// What the game page knows about a game beyond its folder, fetched once per visit and shared by the banner and the tabs:
/// the Steam store data, PCGamingWiki, the OptiScaler wiki, HowLongToBeat and Metacritic (the last two only when asked).
/// </summary>
public sealed class GameData
{
    public required Game Game { get; init; }
    public required CancellationToken Token { get; init; }
    public required Task<GameArt> Art { get; init; }
    public required Task<StoreDetails?> Store { get; init; }
    public required Task<PcgwInfo?> Pcgw { get; init; }
    public required Task<OptiWikiEntry?> OptiWiki { get; init; }

    /// <summary>The name sites know the game by (no trademark signs, store tags or edition words; or the one the user picked).</summary>
    public string SearchName => GameNames.PlainOrClean(GameNames.LookupName(Game));

    Task<HltbTimes?>? _hltb;
    public Task<HltbTimes?> Hltb => _hltb ??= HowLongToBeat.GetAsync(SearchName, Token);

    Task<MetacriticScores?>? _reviews;
    /// <summary>Metacritic's scores (the Steam store's Metacritic link, when it has one, says which page).</summary>
    public Task<MetacriticScores?> Reviews => _reviews ??= ReviewsAsync();

    async Task<MetacriticScores?> ReviewsAsync()
    {
        StoreDetails? store = null;
        try { store = await Store; } catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Metacritic store link: {ex.Message}"); }
        return await Core.Media.Metacritic.GetAsync(SearchName, store?.MetacriticUrl, Token);
    }

    /// <summary>Opens another tab of the game page ("optiwiki", "pcgw", "ach").</summary>
    public required Action<string> ShowTab { get; init; }
}

/// <summary>Shared pieces for the info tabs: cards, rich text, status pills.</summary>
static class Ui
{
    public static Style Style(string key) => (Style)HybridLauncher.Helpers.ThemeRes.Current[key];
    public static Brush Brush(string key) => (Brush)HybridLauncher.Helpers.ThemeRes.Current[key];

    public static Border Card(UIElement child, double padding = 16) =>
        new() { Style = Style("CardStyle"), Padding = new Thickness(padding, padding - 2, padding, padding), Child = child };

    // Colours of the card badges: one per kind of content, so each card is recognisable at a glance.
    public static readonly Color Gold = Color.FromArgb(0xFF, 0xF2, 0xB8, 0x2E), Blue = Color.FromArgb(0xFF, 0x3D, 0x9B, 0xFF),
        Green = Color.FromArgb(0xFF, 0x3F, 0xC2, 0x6A), Teal = Color.FromArgb(0xFF, 0x22, 0xB8, 0xB0), Orange = Color.FromArgb(0xFF, 0xF0, 0x7F, 0x2A),
        Red = Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D), Pink = Color.FromArgb(0xFF, 0xE0, 0x5C, 0xB8), Slate = Color.FromArgb(0xFF, 0x8B, 0x94, 0xA8),
        Yellow = Color.FromArgb(0xFF, 0xF5, 0xC5, 0x18);

    /// <summary>A rounded tinted square with a glyph in it: the icon of a card heading.</summary>
    public static Border Badge(string glyph, Color? tint = null, double size = 34)
    {
        var c = tint ?? ToneColor(Tone.Accent);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 4),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x55, c.R, c.G, c.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x22, c.R, c.G, c.B), Offset = 1 },
                },
            },
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Tinted(Icons.Make(glyph, size * 0.47), c),
        };
    }

    /// <summary>
    /// The separator between the sections of a tab: a hairline that starts in the section's colour and fades to the page's
    /// grey, so a long scroll reads as chapters. No words on it: the section's own heading, right under it, names it.
    /// Shared by every tab (OptiScaler, ReShade &amp; Mods, the game overview).
    /// </summary>
    public static Grid SectionRule(Color tint)
    {
        var grey = (Brush("ControlStrongStrokeColorDefaultBrush") as SolidColorBrush)?.Color ?? Color.FromArgb(0x80, 0x80, 0x80, 0x80);
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 4), Height = 2, IsHitTestVisible = false };
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xC0, tint.R, tint.G, tint.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x70, grey.R, grey.G, grey.B), Offset = 0.18 },
                    new GradientStop { Color = Color.FromArgb(0x50, grey.R, grey.G, grey.B), Offset = 1 },
                },
            },
        });
        return grid;
    }

    /// <summary>An icon in a colour, centred in its slot (a font glyph or the trophy path).</summary>
    static IconElement Tinted(IconElement icon, Color c)
    {
        icon.Foreground = new SolidColorBrush(c);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    /// <summary>A card heading: icon badge, title, and optionally something on the right (a link button).</summary>
    public static Grid Heading(string glyph, string title, FrameworkElement? right = null, string? subtitle = null, Color? tint = null)
    {
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Settings → Appearance → Icons → Card headings: the title alone.
        if (Icons.Show(IconSpots.Headings)) grid.Children.Add(Badge(glyph, tint));
        else grid.ColumnSpacing = 0;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = Style("BodyStrongTextBlockStyle"), FontSize = 17, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle is not null) text.Children.Add(new TextBlock { Text = subtitle, Style = Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (right is not null)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }
        return grid;
    }

    /// <summary>A section heading without a box: a tinted glyph, the title, optionally a subtitle and something on the right.</summary>
    public static Grid FlatHeading(string glyph, string title, FrameworkElement? right = null, string? subtitle = null, Color? tint = null)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var c = tint ?? ToneColor(Tone.Accent);
        // The trophy is the shaded one, as on the covers and the play bar.
        if (Icons.Show(IconSpots.Headings)) grid.Children.Add(glyph == Icons.TrophyGlyph ? Icons.Trophy(22) : Tinted(Icons.Make(glyph, 18), c));
        else grid.ColumnSpacing = 0;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle is not null) text.Children.Add(new TextBlock { Text = subtitle, Style = Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (right is not null)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }
        return grid;
    }

    /// <summary>
    /// Long content (a store description) shown up to about <paramref name="height"/>, cut at the bottom of a whole paragraph or
    /// picture (never through a line or an image), with Read more / Show less under it. A cut that would leave too little shows
    /// the next block whole instead.
    /// </summary>
    public static FrameworkElement ReadMore(Panel content, double height)
    {
        var clip = new Grid { MaxHeight = height };
        clip.Children.Add(content);
        bool open = false;
        var label = new TextBlock { Text = L.T("Read more"), FontWeight = FontWeights.SemiBold };
        var chevron = new FontIcon { Glyph = "", FontSize = 12 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(label);
        row.Children.Add(chevron);
        var more = new Button { Content = row, Padding = new Thickness(14, 6, 14, 7), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var rule = new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        double Cut()
        {
            var bottoms = new List<double>();
            foreach (var child in content.Children)
            {
                if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible || fe.ActualHeight <= 0) continue;
                bottoms.Add(fe.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, fe.ActualHeight)).Y);
            }
            if (bottoms.Count == 0) return height;
            double fit = bottoms.Where(b => b <= height + 0.5).DefaultIfEmpty(0).Max();
            if (fit >= height * 0.6) return Math.Ceiling(fit);
            // A tall picture right at the edge: show it whole rather than stop well short.
            double next = bottoms.Where(b => b > height).DefaultIfEmpty(height).Min();
            return Math.Ceiling(next <= height * 1.6 ? next : Math.Max(fit, height));
        }
        void Update()
        {
            bool longer = content.ActualHeight > height + 24;
            more.Visibility = rule.Visibility = longer ? Visibility.Visible : Visibility.Collapsed;
            if (!open) clip.MaxHeight = longer ? Cut() : double.PositiveInfinity;
        }
        more.Click += (_, _) =>
        {
            open = !open;
            clip.MaxHeight = open ? double.PositiveInfinity : Cut();
            label.Text = open ? L.T("Show less") : L.T("Read more");
            chevron.Glyph = open ? "" : "";
            // Folding a long text again brings its start back into view.
            if (!open) clip.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.1 });
        };
        content.SizeChanged += (_, _) => Update();
        var panel = new StackPanel();
        panel.Children.Add(clip);
        panel.Children.Add(rule);
        panel.Children.Add(more);
        return panel;
    }

    public static TextBlock Muted(string text) => new() { Text = text, Style = Style("RowDescription") };

    public static Button LinkButton(string text, string glyph, Action click)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = panel, Padding = new Thickness(10, 4, 10, 5) };
        b.Click += (_, _) => Safe(click);
        return b;
    }

    public static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Warn($"Action failed: {ex}");
            App.Window?.ShowError(L.T("Action failed"), ex.Message);
        }
    }

    public static void Open(string? uri) => Safe(() => { if (!string.IsNullOrWhiteSpace(uri)) GameLauncher.OpenUri(uri); });

    public enum Tone { Good, Partial, Bad, Unknown, Accent }

    public static Color ToneColor(Tone tone) => tone switch
    {
        Tone.Good => Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43),
        Tone.Partial => Color.FromArgb(0xFF, 0xD2, 0x99, 0x22),
        Tone.Bad => Color.FromArgb(0xFF, 0xD1, 0x3B, 0x3B),
        Tone.Accent => (HybridLauncher.Helpers.ThemeRes.Current["AccentFillColorDefaultBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(0xFF, 0x9A, 0x5C, 0xFF),
        _ => Color.FromArgb(0xFF, 0x8B, 0x94, 0x9E),
    };

    /// <summary>A small tinted pill ("Yes", "Limited"...), readable on light and dark.</summary>
    public static Border Pill(string text, Tone tone)
    {
        var c = ToneColor(tone);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 4, 9, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = TechTags.Text(text, 12, Brush("TextFillColorPrimaryBrush"), spacing: 0),
        };
    }

    /// <summary>A neutral chip ("DLSS 4", "Action").</summary>
    public static Border Chip(string text, Tone tone = Tone.Unknown)
    {
        var c = ToneColor(tone);
        return new Border
        {
            Background = tone == Tone.Unknown ? Brush("ControlFillColorSecondaryBrush") : new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 4),
            Child = TechTags.Text(text, 12, Brush("TextFillColorPrimaryBrush"), spacing: 0),
        };
    }

    /// <summary>Chips that wrap onto more lines (a VariableSizedWrapGrid would size every cell alike).</summary>
    public static FrameworkElement Wrap(IEnumerable<FrameworkElement> items, double spacing = 6) => new WrapPanel(spacing, items);

    /// <summary>A label on the left, a value (pill, text...) on the right.</summary>
    public static Grid Row(string label, FrameworkElement value, string? tooltip = null)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        if (tooltip is { Length: > 0 }) ToolTipService.SetToolTip(grid, new ToolTip { Content = new TextBlock { Text = tooltip, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 } });
        return grid;
    }

    /// <summary>
    /// Text lines from a web page or wiki as TextBlocks: headings, bullets with indent, bold / struck / code runs, links.
    /// <paramref name="large"/> (store descriptions): bigger body text and real headings, set apart from the paragraphs.
    /// </summary>
    public static StackPanel Rich(IEnumerable<RichLine> lines, double spacing = 4, bool large = false, Color? headingTint = null)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var line in lines)
        {
            if (line.Image is not null || line.Video is not null)
            {
                if (Picture(line, large) is { } picture) panel.Children.Add(picture);
                continue;
            }
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = Style(line.Heading ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle") };
            if (line.Heading && headingTint is { } tint)
            {
                // Contrasted headings (release notes): the section's name in the tint, on a faint band with a bar on its left.
                tb.FontSize = large ? 17 : 15;
                tb.FontWeight = FontWeights.Bold;
                tb.Foreground = new SolidColorBrush(tint);
                foreach (var run in line.Runs) tb.Inlines.Add(ToInline(run));
                panel.Children.Add(new Border
                {
                    Child = tb,
                    Margin = new Thickness(0, panel.Children.Count > 0 ? 12 : 2, 0, 2),
                    Padding = new Thickness(10, 4, 10, 5),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = new SolidColorBrush(tint),
                    Background = new SolidColorBrush(Color.FromArgb(0x1E, tint.R, tint.G, tint.B)),
                    CornerRadius = new CornerRadius(0, 6, 6, 0),
                });
                continue;
            }
            if (line.Heading)
            {
                tb.Margin = new Thickness(0, large ? 14 : 8, 0, large ? 2 : 0);
                tb.FontSize = large ? 20 : 15;
                tb.FontWeight = FontWeights.Bold;
            }
            else if (large)
            {
                tb.FontSize = 15;
                tb.LineHeight = 23;
            }
            foreach (var run in line.Runs) tb.Inlines.Add(ToInline(run));
            if (line.Level <= 0)
            {
                panel.Children.Add(tb);
                continue;
            }
            var row = new Grid { ColumnSpacing = 8, Margin = new Thickness((line.Level - 1) * 18, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = line.Level == 1 ? "•" : "◦", Foreground = Brush("AccentTextFillColorPrimaryBrush"), FontWeight = FontWeights.Bold });
            Grid.SetColumn(tb, 1);
            row.Children.Add(tb);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>One line of rich text (a table cell) as a TextBlock: its words, styles and links.</summary>
    public static TextBlock LineBlock(RichLine line)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        foreach (var run in line.Runs) tb.Inlines.Add(ToInline(run));
        return tb;
    }

    public static Uri? WebUri(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u : null;

    /// <summary>
    /// A picture or clip of a store description, as on the Steam page: at its own size, never wider than the column, its room
    /// kept from the size the page gives (no jump when it arrives). AVIF pictures (all of Steam's now) go through
    /// <see cref="AvifDecoder"/>; GIFs, which Steam serves as short videos, loop muted while on screen.
    /// </summary>
    static FrameworkElement? Picture(RichLine line, bool large)
    {
        var image = WebUri(line.Image);
        var video = WebUri(line.Video);
        if (image is null && video is null) return null;
        var box = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, large ? 8 : 4, 0, large ? 8 : 4), CornerRadius = new CornerRadius(4) };
        if (line.Width > 0) box.MaxWidth = line.Width;
        if (line.Width > 0 && line.Height > 0)
        {
            double ratio = (double)line.Height / line.Width;
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            box.SizeChanged += (_, e) => { if (Math.Abs(box.Height - e.NewSize.Width * ratio) > 0.5) box.Height = e.NewSize.Width * ratio; };
        }
        Image? poster = null;
        if (image is not null)
        {
            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            poster = new Image { Source = bmp, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            bmp.ImageOpened += (_, _) => { if (line.Width <= 0) box.MaxWidth = bmp.PixelWidth; };
            bmp.ImageFailed += (_, _) => { if (video is null) box.Visibility = Visibility.Collapsed; };
            if (AvifDecoder.IsAvif(image.ToString()))
                _ = LoadAvifAsync(image.ToString(), bmp, box, video is null);
            else bmp.UriSource = image;
        }
        if (video is not null) box.Children.Add(new Controls.LoopVideo(new[] { video, WebUri(line.VideoAlt) }.OfType<Uri>(), poster));
        else box.Children.Add(poster!);
        return box;
    }

    static async Task LoadAvifAsync(string url, BitmapImage bmp, FrameworkElement box, bool hideIfMissing)
    {
        var file = await AvifDecoder.ToPngAsync(url);
        if (file is not null) bmp.UriSource = new Uri(file);
        else if (hideIfMissing) box.Visibility = Visibility.Collapsed;
    }

    static Inline ToInline(RichRun run)
    {
        var r = new Run { Text = run.Text };
        if (run.Code) r.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        if (run.Strike) r.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        if (run.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            var h = new Hyperlink();
            h.Inlines.Add(r);
            h.Click += (_, _) => Open(uri.ToString());
            ToolTipService.SetToolTip(h, uri.ToString());
            return Apply(h, run);
        }
        return Apply(r, run);

        static Inline Apply(Inline inline, RichRun run)
        {
            if (run.Bold) inline.FontWeight = FontWeights.Bold;
            if (run.Italic) inline.FontStyle = Windows.UI.Text.FontStyle.Italic;
            return inline;
        }
    }

    /// <summary>
    /// Long content cut to about <paramref name="maxHeight"/> with a "Show more" link that opens it all. The cut falls on the
    /// bottom of the last whole child that fits (a table row, a paragraph), never through the middle of a line.
    /// </summary>
    public static FrameworkElement Collapsible(FrameworkElement content, double maxHeight)
    {
        var clip = new Grid { MaxHeight = maxHeight };
        clip.Children.Add(content);
        bool open = false;
        var more = new HyperlinkButton { Content = L.T("Show more"), Padding = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        double Cut()
        {
            if (content is not Panel panel) return maxHeight;
            double best = 0;
            foreach (var child in panel.Children)
            {
                if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible || fe.ActualHeight <= 0) continue;
                double bottom = fe.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, fe.ActualHeight)).Y;
                if (bottom <= maxHeight + 0.5) best = Math.Max(best, bottom);
            }
            return best > maxHeight / 3 ? Math.Ceiling(best) : maxHeight;
        }
        more.Click += (_, _) =>
        {
            open = !open;
            clip.MaxHeight = open ? double.PositiveInfinity : Cut();
            more.Content = open ? L.T("Show less") : L.T("Show more");
        };
        content.SizeChanged += (_, e) =>
        {
            if (open) return;
            bool longer = e.NewSize.Height > maxHeight + 4;
            more.Visibility = longer ? Visibility.Visible : Visibility.Collapsed;
            clip.MaxHeight = longer ? Cut() : maxHeight;
        };
        var panel = new StackPanel();
        panel.Children.Add(clip);
        panel.Children.Add(more);
        return panel;
    }

    /// <summary>A text shown on a few lines (cut with "…") and a "Show more" link that shows it whole, only when it is cut.</summary>
    public static FrameworkElement CollapsibleText(TextBlock text, int lines)
    {
        text.MaxLines = lines;
        text.TextTrimming = TextTrimming.WordEllipsis;
        text.TextWrapping = TextWrapping.Wrap;
        var more = new HyperlinkButton { Content = L.T("Show more"), Padding = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        text.IsTextTrimmedChanged += (s, _) => { if (s.MaxLines > 0) more.Visibility = s.IsTextTrimmed ? Visibility.Visible : Visibility.Collapsed; };
        more.Click += (_, _) =>
        {
            bool open = text.MaxLines == 0;
            text.MaxLines = open ? lines : 0;
            more.Content = open ? L.T("Show more") : L.T("Show less");
        };
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(more);
        return panel;
    }

    public static (string Text, Tone Tone) State(PcgwFeature? f) => f is null ? (L.T("Unknown"), Tone.Unknown) : f.State switch
    {
        "true" or "always on" => (f.State == "always on" ? L.T("Always on") : L.T("Yes"), Tone.Good),
        "false" => (L.T("No"), Tone.Bad),
        "limited" => (L.T("Limited"), Tone.Partial),
        "hackable" => (L.T("With a fix"), Tone.Partial),
        "n/a" => ("N/A", Tone.Unknown),
        _ => (L.T("Unknown"), Tone.Unknown),
    };

    public static ProgressRing Loading() => new() { IsActive = true, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) };

    public static string Hours(double h) => h <= 0 ? "—" : h < 1 ? $"{Math.Round(h * 60)} min" : $"{(h % 1 >= 0.25 && h % 1 < 0.75 ? Math.Floor(h) + "½" : Math.Round(h).ToString())} h";

    /// <summary>A highlighted line with an icon and a coloured bar on the left (a wiki's first issue or tip).</summary>
    public static Border Callout(string glyph, string text, Tone tone)
    {
        var c = ToneColor(tone);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B)),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };
    }

    /// <summary>A small upper-case label over a big value, centred on a tile (times, scores).</summary>
    public static Border ValueTile(string label, string value, Color? tint = null, string? note = null)
    {
        var tile = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };
        tile.Children.Add(new TextBlock { Text = label.ToUpper(), Style = Style("GroupHeader"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var v = new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        if (tint is { } t) v.Foreground = new SolidColorBrush(t);
        tile.Children.Add(v);
        if (note is { Length: > 0 }) tile.Children.Add(new TextBlock { Text = note, Style = Style("RowDescription"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
        return new Border { Style = Style("RowStyle"), MinHeight = 0, Padding = new Thickness(10, 8, 10, 10), Child = tile };
    }

    /// <summary>Two equal columns of tiles (as many rows as needed).</summary>
    public static Grid TwoByN(IReadOnlyList<FrameworkElement> tiles)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < tiles.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(tiles[i], i % 2);
            Grid.SetRow(tiles[i], i / 2);
            grid.Children.Add(tiles[i]);
        }
        return grid;
    }
}

/// <summary>A panel that lays its children out in rows, wrapping to the next row when one is full.</summary>
sealed partial class WrapPanel : Panel
{
    readonly double _spacing;

    public WrapPanel(double spacing, IEnumerable<FrameworkElement> items)
    {
        _spacing = spacing;
        foreach (var item in items) Children.Add(item);
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > available.Width) { y += row + _spacing; x = 0; row = 0; }
            x += s.Width + _spacing;
            row = Math.Max(row, s.Height);
            width = Math.Max(width, x - _spacing);
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), y + row);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children)
        {
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { y += row + _spacing; x = 0; row = 0; }
            child.Arrange(new Windows.Foundation.Rect(x, y, s.Width, s.Height));
            x += s.Width + _spacing;
            row = Math.Max(row, s.Height);
        }
        return final;
    }
}

// ======================================================================================================================

/// <summary>Short previews of the two wikis (the game overview, and the OptiScaler wiki under the OptiScaler tab), each with the way to its tab.</summary>
static class WikiPreviews
{
    /// <summary>A preview: as a card (under the OptiScaler tab), or <paramref name="flat"/> without a box (the overview).</summary>
    static Border Card(GameData data, string glyph, string title, string tab, Color tint, bool flat, out StackPanel body)
    {
        var panel = new StackPanel { Spacing = flat ? 12 : 8 };
        var open = Ui.LinkButton(L.T("Open tab"), "", () => data.ShowTab(tab));
        panel.Children.Add(flat ? Ui.FlatHeading(glyph, title, open, tint: tint) : Ui.Heading(glyph, title, open, tint: tint));
        body = new StackPanel { Spacing = 8 };
        body.Children.Add(Ui.Loading());
        panel.Children.Add(body);
        return flat ? new Border { Child = panel } : Ui.Card(panel);
    }

    public static Border OptiCard(GameData data, bool flat = false)
    {
        var card = Card(data, "", L.T("OptiScaler wiki"), "optiwiki", Ui.Teal, flat, out var body);
        _ = FillOptiAsync(data, body);
        return card;
    }

    public static Border PcgwCard(GameData data, bool flat = false)
    {
        var card = Card(data, "", "PCGamingWiki", "pcgw", Ui.Blue, flat, out var body);
        _ = FillPcgwAsync(data, body);
        return card;
    }

    static async Task FillOptiAsync(GameData data, StackPanel body)
    {
        OptiWikiEntry? opti = null;
        try { opti = await data.OptiWiki; } catch (Exception ex) { Log.Warn($"OptiScaler wiki preview: {ex.Message}"); }
        if (data.Token.IsCancellationRequested) return;
        body.Children.Clear();
        if (opti is null)
        {
            body.Children.Add(Ui.Muted(L.T("This game has no page on the OptiScaler wiki yet. The tab links to the Compatibility List.")));
            return;
        }
        var facts = new List<string>();
        if (opti.Get("Last Tested Version") is { IsEmpty: false } v) facts.Add(L.F("Tested with {0}", v.Text));
        if (opti.Get("Filename") is { IsEmpty: false } fn) facts.Add(fn.Text);
        if (opti.Get("GPU") is { IsEmpty: false } gpu) facts.Add(gpu.Text);
        if (facts.Count > 0) body.Children.Add(Ui.Muted(string.Join("  ·  ", facts)));
        var chips = new List<FrameworkElement>();
        if (opti.Get("Upscaler Inputs") is { IsEmpty: false } ui) chips.AddRange(ui.Text.Split(',').Select(s => (FrameworkElement)TechTags.Create(s.Trim(), fontSize: 12)));
        if (opti.Get("FG Inputs") is { IsEmpty: false } fg) chips.Add(TechTags.Create("FG: " + fg.Text.TrimStart('-', ' '), frameGen: true, fontSize: 12));
        if (chips.Count > 0) body.Children.Add(Ui.Wrap(chips));
        if (opti.Get("Known Issues") is { IsEmpty: false } issues)
            body.Children.Add(Ui.Callout("", issues.Lines.FirstOrDefault()?.Text ?? issues.Text, Ui.Tone.Partial));
        else if (opti.Get("Settings") is { IsEmpty: false } settings)
            body.Children.Add(Ui.Callout("", settings.Lines.FirstOrDefault()?.Text ?? settings.Text, Ui.Tone.Accent));
    }

    static async Task FillPcgwAsync(GameData data, StackPanel body)
    {
        PcgwInfo? wiki = null;
        try { wiki = await data.Pcgw; } catch (Exception ex) { Log.Warn($"PCGamingWiki preview: {ex.Message}"); }
        if (data.Token.IsCancellationRequested) return;
        body.Children.Clear();
        if (wiki is null)
        {
            body.Children.Add(Ui.Muted(L.T("No PCGamingWiki page was found for this game.")));
            return;
        }
        var chips = Pcgw.UpscalerTags(string.Join(", ", wiki.UpscalingTech), wiki.Apis).Select(t => (FrameworkElement)TechTags.Create(t, fontSize: 12))
            .Concat(Pcgw.FrameGenTags(wiki.FrameGenTech).Select(t => (FrameworkElement)TechTags.Create(t, frameGen: true, fontSize: 12))).ToList();
        if (chips.Count > 0) body.Children.Add(Ui.Wrap(chips));
        var counts = new List<string>();
        if (wiki.Improvements.Count > 0) counts.Add(L.N(wiki.Improvements.Count, "{0} essential improvement", "{0} essential improvements"));
        counts.Add(wiki.IssuesUnresolved.Count == 0 ? L.T("no open issues") : L.N(wiki.IssuesUnresolved.Count, "{0} open issue", "{0} open issues"));
        if (wiki.IssuesFixed.Count > 0) counts.Add(L.F("{0} fixed", wiki.IssuesFixed.Count));
        body.Children.Add(Ui.Muted(string.Join("  ·  ", counts)));
        if (wiki.IssuesUnresolved.FirstOrDefault() is { } issue) body.Children.Add(Ui.Callout("", issue.Title, Ui.Tone.Bad));
        else if (wiki.Improvements.FirstOrDefault() is { } fix) body.Children.Add(Ui.Callout("", fix.Title, Ui.Tone.Good));
    }
}

// ======================================================================================================================

/// <summary>
/// The "Game" tab, Hydra / Steam store style and without boxes: one centred block, the media (trailer player and
/// screenshots) over the full "About this game" of the Steam store (pictures included, folded with Read more), then the
/// requirements and what the wikis say; on the side, flat sections: details, Steam reviews, achievements, HowLongToBeat,
/// Metacritic, controllers (with Steam Input) and languages. Narrow pages put the side under the main column.
/// </summary>
public sealed partial class OverviewView : UserControl
{
    readonly GameData _data;
    readonly Grid _root = new() { ColumnSpacing = 44, RowSpacing = 32, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 1840 };
    readonly StackPanel _main = new() { Spacing = 34 };
    readonly StackPanel _side = new() { Spacing = 12 };

    // The media: the player (picture or trailer) and the thumbnails under it.
    readonly StackPanel _media = new() { Spacing = 10, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Controls.TrailerPlayer _viewer = new();
    readonly Grid _thumbRow = new() { ColumnSpacing = ThumbGap };
    readonly Grid _thumbHost = new();
    readonly Button _thumbPrev, _thumbNext;
    readonly List<(Border Wrapper, Grid Box)> _thumbs = [];
    int _thumbOffset, _thumbsShown = 1;
    Border? _selectedThumb;

    readonly StackPanel _about = new() { Spacing = 14 };
    readonly StackPanel _details = new() { Spacing = 10 };
    readonly StackPanel _steamReviews = new() { Spacing = 12 };
    FrameworkElement? _steamReviewsSection;
    readonly StackPanel _achBody = new() { Spacing = 10 };
    FrameworkElement? _achCard;
    Grid? _achHead;
    readonly Grid _wikiRow = new() { ColumnSpacing = 32, RowSpacing = 28 };
    readonly StackPanel _controllers = new() { Spacing = 4 };
    readonly StackPanel _hltb = new() { Spacing = 8 };
    readonly StackPanel _reviews = new() { Spacing = 8 };
    readonly StackPanel _languages = new() { Spacing = 2 };
    readonly TextBlock _languagesSummary = new() { Style = Ui.Style("RowDescription"), Margin = new Thickness(0, -4, 0, 4) };
    FrameworkElement? _languagesCard, _reviewsCard;
    readonly Grid _requirements = new() { ColumnSpacing = 32, RowSpacing = 12 };
    FrameworkElement? _requirementsCard;
    bool _wide = true;
    double _mainWidth = -1;

    const double SideWidth = 400, MediaMaxWidth = 1240, ThumbMinWidth = 132, ThumbGap = 8;

    public OverviewView(GameData data)
    {
        _data = data;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SideWidth) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(_main);
        Grid.SetColumn(_side, 1);
        _root.Children.Add(_side);
        _root.Padding = new Thickness(4, 6, 4, 0);
        Content = _root;
        _thumbPrev = PageButton("", -1);
        _thumbNext = PageButton("", 1);
        _viewer.Failed += reason =>
        {
            Log.Warn($"Trailer: {reason}");
            if (_userPicked)
            {
                App.Window?.ShowInfo(L.T("This trailer could not be played here. Opening it on Steam instead."));
                if (_data.Store.IsCompletedSuccessfully && _data.Store.Result is { } s) Ui.Open(s.StoreUrl);
            }
        };
        Build();
        _main.SizeChanged += (_, e) => LayoutTop(e.NewSize.Width);
        Unloaded += (_, _) => StopTrailer();
    }

    bool _userPicked;

    /// <summary>Side column beside the main one on wide pages, below it otherwise.</summary>
    public void ApplyLayout(double width)
    {
        bool wide = width >= 1180;
        LayoutPair(_wikiRow, width >= 900);
        if (wide == _wide) return;
        _wide = wide;
        _root.ColumnDefinitions[1].Width = wide ? new GridLength(SideWidth) : new GridLength(0);
        _root.ColumnSpacing = wide ? 44 : 0;
        Grid.SetColumn(_side, wide ? 1 : 0);
        Grid.SetRow(_side, wide ? 0 : 1);
    }

    static void LayoutPair(Grid grid, bool sideBySide)
    {
        if (grid.ColumnDefinitions.Count < 2)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.ColumnDefinitions[1].Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        grid.ColumnSpacing = sideBySide ? 32 : 0;
        if (grid.Children.Count == 2)
        {
            Grid.SetColumn((FrameworkElement)grid.Children[1], sideBySide ? 1 : 0);
            Grid.SetRow((FrameworkElement)grid.Children[1], sideBySide ? 0 : 1);
        }
    }

    /// <summary>The media as wide as the main column allows (up to 1240 px), 16:9, the thumbnails filling the same width.</summary>
    void LayoutTop(double width)
    {
        if (width <= 0 || Math.Abs(width - _mainWidth) < 0.5) return;
        _mainWidth = width;
        double media = Math.Floor(Math.Min(MediaMaxWidth, width));
        _media.Width = media;
        _viewer.Width = media;
        _viewer.Height = Math.Round(media * 9 / 16);
        LayoutThumbs(media);
    }

    /// <summary>As many whole thumbnails as fit the media's width (never one cut by the edge); the arrows page through the rest.</summary>
    void LayoutThumbs(double width)
    {
        if (_thumbs.Count == 0 || width <= 0) return;
        int fit = Math.Max(1, (int)((width + ThumbGap) / (ThumbMinWidth + ThumbGap)));
        _thumbsShown = Math.Min(fit, _thumbs.Count);
        double w = Math.Floor((width - ThumbGap * (_thumbsShown - 1)) / _thumbsShown);
        if (_thumbsShown < fit) w = Math.Floor((width - ThumbGap * (fit - 1)) / fit);
        double h = Math.Round(w * 9 / 16);
        foreach (var (_, box) in _thumbs)
        {
            box.Width = w;
            box.Height = h;
        }
        _thumbOffset = Math.Clamp(_thumbOffset, 0, Math.Max(0, _thumbs.Count - _thumbsShown));
        ShowThumbPage();
    }

    void ShowThumbPage()
    {
        _thumbRow.Children.Clear();
        _thumbRow.ColumnDefinitions.Clear();
        for (int i = 0; i < _thumbsShown; i++)
        {
            _thumbRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var wrapper = _thumbs[_thumbOffset + i].Wrapper;
            Grid.SetColumn(wrapper, i);
            _thumbRow.Children.Add(wrapper);
        }
        _thumbPrev.Visibility = _thumbOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
        _thumbNext.Visibility = _thumbOffset + _thumbsShown < _thumbs.Count ? Visibility.Visible : Visibility.Collapsed;
    }

    Button PageButton(string glyph, int direction)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Background = new HybridLauncher.Helpers.GlassBrush(Color.FromArgb(0x99, 0x16, 0x17, 0x1B), 16),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = direction < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(direction < 0 ? -10 : 0, 0, direction > 0 ? -10 : 0, 0),
            Visibility = Visibility.Collapsed,
            RequestedTheme = ElementTheme.Dark,
        };
        ToolTipService.SetToolTip(b, direction < 0 ? L.T("Previous") : L.T("More"));
        b.Click += (_, _) =>
        {
            _thumbOffset = Math.Clamp(_thumbOffset + direction * _thumbsShown, 0, Math.Max(0, _thumbs.Count - _thumbsShown));
            ShowThumbPage();
        };
        return b;
    }

    /// <summary>Stops a playing trailer (another tab, another page): its sound must not keep going.</summary>
    public void StopTrailer() => _viewer.Stop();

    /// <summary>
    /// Adds a section to the main column; every section after the media starts with the same separator as the OptiScaler and
    /// ReShade tabs, shown and hidden with the section itself.
    /// </summary>
    void AddMain(FrameworkElement section, Color tint)
    {
        var rule = Ui.SectionRule(tint);
        // The column's spacing already parts the sections: the rule sits close above its section.
        rule.Margin = new Thickness(0, 0, 0, -20);
        rule.Visibility = section.Visibility;
        section.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => rule.Visibility = section.Visibility);
        _main.Children.Add(rule);
        _main.Children.Add(section);
    }

    /// <summary>A main-column section: the heading over the body, no box (like About this game).</summary>
    static FrameworkElement MainSection(string glyph, string title, FrameworkElement body, FrameworkElement? right = null, string? subtitle = null, Color? tint = null)
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Ui.FlatHeading(glyph, title, right, subtitle, tint));
        panel.Children.Add(body);
        return panel;
    }

    /// <summary>A side section in its own card (rounded, a subtle fill and border), the heading over the body.</summary>
    FrameworkElement SideSection(string glyph, string title, FrameworkElement body, FrameworkElement? right = null, string? subtitle = null, Color? tint = null)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Ui.FlatHeading(glyph, title, right, subtitle, tint));
        panel.Children.Add(body);
        return Ui.Card(panel);
    }

    void Build()
    {
        // ---- main column: media, about, the wikis, requirements, then Steam's reviews last (as on the store) ----
        _thumbHost.Children.Add(_thumbRow);
        _thumbHost.Children.Add(_thumbPrev);
        _thumbHost.Children.Add(_thumbNext);
        _media.Children.Add(_viewer);
        _media.Children.Add(_thumbHost);
        _main.Children.Add(_media);
        _about.Children.Add(Ui.Loading());
        AddMain(_about, Ui.Pink);

        // What the wikis say: a short preview each, with the way to their tab.
        _wikiRow.Children.Add(WikiPreviews.OptiCard(_data, flat: true));
        _wikiRow.Children.Add(WikiPreviews.PcgwCard(_data, flat: true));
        LayoutPair(_wikiRow, true);
        AddMain(_wikiRow, Ui.Teal);

        var req = new StackPanel { Spacing = 12 };
        req.Children.Add(Ui.FlatHeading("", L.T("System requirements"), tint: Ui.Orange));
        req.Children.Add(_requirements);
        _requirementsCard = req;
        req.Visibility = Visibility.Collapsed;
        AddMain(req, Ui.Orange);

        _steamReviews.Children.Add(Ui.Loading());
        var write = Ui.LinkButton(L.T("Write a review"), "", WriteReview);
        ToolTipService.SetToolTip(write, L.T("Opens the game's store page in Steam, at its reviews: write yours there with your Steam account."));
        _steamReviewsSection = MainSection("", L.T("Steam reviews"), _steamReviews, write, tint: Ui.Blue);
        _steamReviewsSection.Visibility = Visibility.Collapsed;
        AddMain(_steamReviewsSection, Ui.Blue);

        // ---- side column: controllers first, then achievements, details, play time, scores, languages ----
        // Steam Input: the game's controller layout in Steam (community configurations, remapping), for games Steam knows.
        var steamInput = Ui.LinkButton("Steam Input", "", OpenSteamInput);
        ToolTipService.SetToolTip(steamInput, L.T("Opens this game's controller layout in Steam (Steam Input: remapping and community configurations)"));
        steamInput.Visibility = SteamAppId is null ? Visibility.Collapsed : Visibility.Visible;
        _controllers.Children.Add(Ui.Loading());
        _side.Children.Add(SideSection("", L.T("Controllers"), _controllers, steamInput, subtitle: L.T("Official support (Steam, PCGamingWiki)"), tint: Ui.Blue));
        _ = ShowSteamInputWhenKnownAsync(steamInput);

        _achHead = Ui.FlatHeading(Icons.TrophyGlyph, L.T("Achievements"), Ui.LinkButton(L.T("View all"), "", () => _data.ShowTab("ach")), tint: Ui.Gold);
        var achPanel = new StackPanel { Spacing = 12 };
        achPanel.Children.Add(_achHead);
        achPanel.Children.Add(_achBody);
        _achCard = Ui.Card(achPanel);
        _achCard.Visibility = Visibility.Collapsed;
        _side.Children.Add(_achCard);

        _details.Children.Add(Ui.Loading());
        _side.Children.Add(SideSection("", L.T("Details"), _details, tint: Ui.Pink));


        if (AppState.Settings.ShowHltb)
        {
            _hltb.Children.Add(Ui.Loading());
            _side.Children.Add(SideSection("", L.T("How long to beat"), _hltb, Ui.LinkButton("HLTB", "", OpenHltb), tint: Ui.Green));
            _ = FillHltbAsync();
        }

        if (AppState.Settings.ShowMetacritic)
        {
            _reviews.Children.Add(Ui.Loading());
            _reviewsCard = SideSection("", "Metacritic", _reviews, Ui.LinkButton("Metacritic", "", OpenMetacritic), subtitle: L.T("Critic and user scores"), tint: Ui.Yellow);
            _side.Children.Add(_reviewsCard);
            _ = FillReviewsAsync();
        }

        var lang = new StackPanel { Spacing = 8 };
        lang.Children.Add(_languagesSummary);
        lang.Children.Add(_languages);
        _languagesCard = SideSection("", L.T("Languages"), lang, tint: Ui.Teal);
        _languagesCard.Visibility = Visibility.Collapsed;
        _side.Children.Add(_languagesCard);

        _ = FillStoreAsync();
    }

    bool Gone => _data.Token.IsCancellationRequested;

    string? SteamAppId => (_data.Store.IsCompletedSuccessfully ? _data.Store.Result?.AppId : null)
        ?? (_data.Art.IsCompletedSuccessfully ? _data.Art.Result.SteamAppId : null) ?? _data.Game.SteamAppId;

    async Task ShowSteamInputWhenKnownAsync(Button button)
    {
        try { await _data.Art; } catch { }
        if (!Gone) button.Visibility = SteamAppId is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void OpenSteamInput()
    {
        if (SteamAppId is not { } id) return;
        // steam://currentcontrollerconfig opens the game's "Controller layout" window in the Steam client; without Steam, its store page.
        Ui.Open(GameLauncher.HasProtocol("steam") ? $"steam://currentcontrollerconfig/{id}/" : $"https://store.steampowered.com/app/{id}");
    }

    void WriteReview()
    {
        if (SteamAppId is not { } id) return;
        var page = $"https://store.steampowered.com/app/{id}/#app_reviews_hash";
        Ui.Open(GameLauncher.HasProtocol("steam") ? $"steam://openurl/{page}" : page);
    }

    // ---------- Steam store ----------

    async Task FillStoreAsync()
    {
        StoreDetails? store = null;
        try { store = await _data.Store; }
        catch (Exception ex) { Log.Warn($"Overview store: {ex.Message}"); }
        if (Gone) return;

        _about.Children.Clear();
        _details.Children.Clear();
        if (store is null)
        {
            _about.Children.Add(Ui.Muted(L.T("Steam has no store page for this game, so there is no description, media or requirements to show.")));
            _details.Children.Add(Ui.Muted(L.T("No store details.")));
        }
        else
        {
            FillMedia(store);
            FillAbout(store);
            FillDetails(store);
            FillRequirements(store);
            _ = FillSteamReviewsAsync(store.AppId);
        }
        _mainWidth = -1;
        LayoutTop(_main.ActualWidth);
        _ = FillLanguagesAsync(store);
        await FillControllersAsync(store);
    }

    void FillMedia(StoreDetails store)
    {
        if (store.Screenshots.Count == 0 && store.Movies.Count == 0) return;
        _media.Visibility = Visibility.Visible;
        var movies = store.Movies.Take(8).ToList();
        foreach (var movie in movies) _thumbs.Add(Thumb(movie.Thumbnail, true, () => { _userPicked = true; PlayTrailer(movie); }));
        foreach (var shot in store.Screenshots) _thumbs.Add(Thumb(shot.Thumbnail, false, () => ShowPicture(shot.Full)));
        // First look: the first trailer, playing muted (Steam / Hydra style); the first screenshot when there is none.
        if (movies.FirstOrDefault(m => m.Hls is not null) is { } first)
        {
            if (store.Screenshots.Count > 0) ShowPicture(store.Screenshots[0].Full);
            Select(_thumbs[movies.IndexOf(first)].Wrapper);
            PlayTrailer(first);
        }
        else if (store.Screenshots.Count > 0)
        {
            ShowPicture(store.Screenshots[0].Full);
            Select(_thumbs[movies.Count].Wrapper);
        }
        else if (store.Movies.Count > 0) ShowPicture(store.Movies[0].Thumbnail);
    }

    (Border Wrapper, Grid Box) Thumb(string url, bool video, Action click)
    {
        var grid = new Grid { Width = ThumbMinWidth, Height = ThumbMinWidth * 9 / 16, CornerRadius = new CornerRadius(6), Background = Ui.Brush("ControlFillColorSecondaryBrush") };
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            grid.Children.Add(Smooth.Art(new BitmapImage { UriSource = uri, DecodePixelWidth = 320, DecodePixelType = DecodePixelType.Physical }, grid.CornerRadius));
        if (video)
        {
            grid.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon { Glyph = "", FontSize = 13, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(2, 0, 0, 0) },
            });
        }
        var frame = new Border { Child = grid, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(1) };
        var button = new Button { Content = frame, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        button.Click += (_, _) => { Select(frame); Ui.Safe(click); };
        // The frame's border and padding (3 px a side) sit outside the picture: the wrapper is what the row lays out.
        var wrapper = new Border { Child = button, Margin = new Thickness(-3) };
        return (wrapper, grid);
    }

    void Select(Border wrapper)
    {
        var frame = (wrapper.Child as Button)?.Content as Border ?? wrapper;
        if (_selectedThumb is not null) _selectedThumb.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _selectedThumb = frame;
        frame.BorderBrush = Ui.Brush("AccentFillColorDefaultBrush");
    }

    void ShowPicture(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        // Decoded at the viewer's size: a 1920 px screenshot is not needed in full on most windows.
        double scale = XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Clamp((_viewer.ActualWidth > 0 ? _viewer.ActualWidth : 1280) * scale, 640, 1920);
        _viewer.ShowPicture(uri, width);
    }

    async void PlayTrailer(StoreMovie movie)
    {
        if (movie.Hls is null || !Uri.TryCreate(movie.Hls, UriKind.Absolute, out var uri)) return;
        try { await _viewer.PlayAsync(uri, movie.Hls.Contains(".m3u8", StringComparison.OrdinalIgnoreCase), user: _userPicked); }
        catch (Exception ex)
        {
            Log.Warn($"Trailer {movie.Name}: {ex.Message}");
            if (_userPicked) App.Window?.ShowError(L.T("Could not play the trailer"), ex.Message);
            _viewer.Stop();
        }
    }

    /// <summary>
    /// "About this game" as on the Steam store: the short summary as a lead, then the whole description with its pictures,
    /// folded at a paragraph or picture edge with Read more.
    /// </summary>
    void FillAbout(StoreDetails store)
    {
        // No heading: the description speaks for itself. The store link keeps its place, on the right.
        var storeLink = Ui.LinkButton(L.T("Steam store page"), "", () => Ui.Open(store.StoreUrl));
        storeLink.HorizontalAlignment = HorizontalAlignment.Right;
        _about.Children.Add(storeLink);
        if (store.ShortDescription.Length > 0)
            _about.Children.Add(new TextBlock { Text = store.ShortDescription, Style = Ui.Style("BodyTextBlockStyle"), FontSize = 17, LineHeight = 26, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = 0.92 });
        if (store.About.Count > 0)
        {
            var body = Ui.Rich(store.About, 8, large: true);
            _about.Children.Add(Ui.ReadMore(body, 560));
        }
    }

    /// <summary>The side column's facts: developer, publisher, release date, genres, scores and website.</summary>
    void FillDetails(StoreDetails store)
    {
        var facts = new Grid { ColumnSpacing = 16, RowSpacing = 10 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Fact(string name, FrameworkElement value)
        {
            int r = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = name.ToUpper(), Style = Ui.Style("GroupHeader"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            Grid.SetRow(label, r);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            facts.Children.Add(label);
            facts.Children.Add(value);
        }
        TextBlock T(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (store.Developers.Count > 0) Fact(L.T("Developer"), T(string.Join(", ", store.Developers)));
        if (store.Publishers.Count > 0) Fact(L.T("Publisher"), T(string.Join(", ", store.Publishers)));
        if (store.ReleaseDate is { Length: > 0 } date) Fact(L.T("Release date"), T(date));
        if (store.Genres.Count > 0) Fact(L.T("Genres"), Ui.Wrap(store.Genres.Select(g => (FrameworkElement)Ui.Chip(g))));
        if (store.Website is { Length: > 0 } site)
        {
            var link = new HyperlinkButton { Content = new TextBlock { Text = site.Replace("https://", "").Replace("http://", "").TrimEnd('/'), TextTrimming = TextTrimming.CharacterEllipsis }, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(link, site);
            link.Click += (_, _) => Ui.Open(site);
            Fact(L.T("Website"), link);
        }
        if (facts.Children.Count > 0) _details.Children.Add(facts);
        else _details.Children.Add(Ui.Muted(L.T("No store details.")));
    }

    void FillRequirements(StoreDetails store)
    {
        if (store.Minimum.Count == 0 && store.Recommended.Count == 0) return;
        _requirementsCard!.Visibility = Visibility.Visible;
        var columns = new[] { (L.T("MINIMUM"), store.Minimum), (L.T("RECOMMENDED"), store.Recommended) }.Where(c => c.Item2.Count > 0).ToList();
        for (int i = 0; i < columns.Count; i++)
        {
            _requirements.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = columns[i].Item1, Style = Ui.Style("GroupHeader") });
            foreach (var r in columns[i].Item2)
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                if (r.Name.Length > 0) tb.Inlines.Add(new Run { Text = r.Name + ":  ", Foreground = Ui.Brush("TextFillColorSecondaryBrush") });
                tb.Inlines.Add(new Run { Text = r.Value });
                panel.Children.Add(tb);
            }
            Grid.SetColumn(panel, i);
            _requirements.Children.Add(panel);
        }
    }

    // ---------- Steam reviews ----------

    /// <summary>
    /// Steam's reviews as on the store page, at the end of the main column: the overall and the recent rating side by side, then
    /// "Most helpful" (wide cards: the reviewer on the left, the verdict and the text on the right) beside "Recently posted"
    /// (compact cards). One column on narrow pages.
    /// </summary>
    async Task FillSteamReviewsAsync(string appId)
    {
        SteamReviewSet? set = null;
        try { set = await SteamReviews.GetAsync(appId, _data.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Overview Steam reviews: {ex.Message}"); }
        catch (OperationCanceledException) { return; }
        if (Gone || _steamReviewsSection is null) return;
        _steamReviews.Children.Clear();
        _steamReviewsSection.Visibility = Visibility.Visible;
        if (set is null)
        {
            _steamReviews.Children.Add(Ui.Muted(L.T("Steam's reviews could not be read now.")));
            return;
        }

        // ---- the two ratings ----
        var ratings = new Grid { ColumnSpacing = 32, RowSpacing = 14 };
        ratings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ratings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ratings.Children.Add(Rating(L.T("OVERALL REVIEWS"), set.Summary, set.Share, L.N(set.Total, "{0:N0} review", "{0:N0} reviews")));
        long recentTotal = set.RecentUp + set.RecentDown;
        if (recentTotal >= 10)
        {
            double recent = (double)set.RecentUp / recentTotal;
            var r = Rating(L.T("RECENT REVIEWS"), set.RecentSummary, recent, L.F("{0:P0} of {1:N0} in the last 30 days", recent, recentTotal));
            Grid.SetColumn(r, 1);
            ratings.Children.Add(r);
        }
        _steamReviews.Children.Add(ratings);

        // ---- most helpful | recently posted ----
        var columns = new Grid { ColumnSpacing = 24, RowSpacing = 24 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var helpful = new StackPanel { Spacing = 10 };
        helpful.Children.Add(new TextBlock { Text = L.T("MOST HELPFUL REVIEWS"), Style = Ui.Style("GroupHeader") });
        foreach (var r in set.Top) helpful.Children.Add(ReviewCard(r, wide: true));
        columns.Children.Add(helpful);
        if (set.Recent.Count > 0)
        {
            var recentList = new StackPanel { Spacing = 10 };
            recentList.Children.Add(new TextBlock { Text = L.T("RECENTLY POSTED"), Style = Ui.Style("GroupHeader") });
            foreach (var r in set.Recent) recentList.Children.Add(ReviewCard(r, wide: false));
            Grid.SetColumn(recentList, 1);
            columns.Children.Add(recentList);
            // Narrow main column: one under the other.
            columns.SizeChanged += (_, e) =>
            {
                bool two = e.NewSize.Width >= 820;
                columns.ColumnDefinitions[1].Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                columns.ColumnSpacing = two ? 24 : 0;
                Grid.SetColumn(recentList, two ? 1 : 0);
                Grid.SetRow(recentList, two ? 0 : 1);
            };
        }
        else columns.ColumnDefinitions[1].Width = new GridLength(0);
        _steamReviews.Children.Add(columns);
        _steamReviews.Children.Add(Ui.LinkButton(L.T("All reviews on Steam"), "", () => Ui.Open(set.StorePage)));

        static Color Tint(double share) => share >= 0.7 ? Color.FromArgb(0xFF, 0x66, 0xC0, 0xF4) : share >= 0.4 ? Color.FromArgb(0xFF, 0xB9, 0xA0, 0x74) : Color.FromArgb(0xFF, 0xD9, 0x6A, 0x54);

        // "OVERALL REVIEWS: Very Positive (960,503 reviews)" with the share as a thin bar under it, Steam's colours.
        static StackPanel Rating(string label, string summary, double share, string count)
        {
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(new TextBlock { Text = label, Style = Ui.Style("GroupHeader") });
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new Run { Text = L.T(summary), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Tint(share)) });
            line.Inlines.Add(new Run { Text = "   " + count, FontSize = 13, Foreground = Ui.Brush("TextFillColorSecondaryBrush") });
            p.Children.Add(line);
            var bar = new Grid { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 0, 0), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, share), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1 - share), GridUnitType.Star) });
            bar.Children.Add(new Border { Background = new SolidColorBrush(Tint(share)) });
            var rest = new Border { Background = Ui.Brush("ControlFillColorSecondaryBrush") };
            Grid.SetColumn(rest, 1);
            bar.Children.Add(rest);
            p.Children.Add(bar);
            return p;
        }
    }

    /// <summary>
    /// One review, Steam's card: a flat tinted block; the verdict band (thumb, Recommended, hours on record), the date, the
    /// text folded to a few lines, how many found it helpful. Wide cards show the reviewer (avatar, name) on the left.
    /// </summary>
    static FrameworkElement ReviewCard(SteamReview r, bool wide)
    {
        var good = r.Recommended;
        var verdictColor = good ? Color.FromArgb(0xFF, 0x66, 0xC0, 0xF4) : Color.FromArgb(0xFF, 0xD9, 0x6A, 0x54);
        var band = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 7, 12, 7), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x26, verdictColor.R, verdictColor.G, verdictColor.B)) };
        band.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        band.Children.Add(new FontIcon { Glyph = good ? "" : "", FontSize = wide ? 22 : 18, Foreground = new SolidColorBrush(verdictColor), VerticalAlignment = VerticalAlignment.Center });
        var verdict = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        verdict.Children.Add(new TextBlock { Text = good ? L.T("Recommended") : L.T("Not Recommended"), FontWeight = FontWeights.SemiBold, FontSize = wide ? 16 : 14 });
        if (r.Hours.Length > 0) verdict.Children.Add(new TextBlock { Text = r.Hours, Style = Ui.Style("RowDescription") });
        Grid.SetColumn(verdict, 1);
        band.Children.Add(verdict);

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(band);
        if (!wide) body.Children.Add(new TextBlock { Text = r.Author, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = L.F("Posted: {0}", r.Posted).ToUpperInvariant(), Style = Ui.Style("GroupHeader"), Opacity = 0.75 });
        var text = new TextBlock { Text = r.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14, LineHeight = 21 };
        body.Children.Add(Ui.CollapsibleText(text, wide ? 6 : 4));
        if (r.Helpful.Length > 0) body.Children.Add(Ui.Muted(r.Helpful));

        FrameworkElement content = body;
        if (wide)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
            var avatar = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(4), Background = Ui.Brush("ControlFillColorSecondaryBrush") };
            if (Ui.WebUri(r.Avatar) is { } a) avatar.Child = Smooth.Art(new BitmapImage { UriSource = a, DecodePixelWidth = 68 }, Smooth.Inner(avatar), Stretch.UniformToFill);
            who.Children.Add(avatar);
            who.Children.Add(new TextBlock { Text = r.Author, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 106, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(who);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            content = row;
        }
        return new Border { Child = content, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(6), Background = Ui.Brush("SubtleFillColorSecondaryBrush") };
    }

    /// <summary>
    /// Languages as a small table (interface, full audio, subtitles), from PCGamingWiki when it has the game's table, from the
    /// Steam store otherwise. The Windows display language comes first and is marked; a summary line counts them.
    /// </summary>
    async Task FillLanguagesAsync(StoreDetails? store)
    {
        PcgwInfo? wiki = null;
        try { wiki = await _data.Pcgw; }
        catch (Exception ex) { Log.Warn($"Overview languages: {ex.Message}"); }
        if (Gone) return;

        // (name, interface, audio, subtitles, notes): "true" / "false" / "limited" / "" (not known).
        var rows = wiki is { Languages.Count: > 0 }
            ? wiki.Languages.Where(l => !l.Fan || l.Interface == "true").Select(l => (l.Name, l.Interface, l.Audio, l.Subtitles, Notes: l.Notes + (l.Fan ? " " + L.T("(fan translation)") : ""))).ToList()
            : store?.Languages.Select(l => (l.Name, Interface: "true", Audio: l.FullAudio ? "true" : "", Subtitles: "", Notes: "")).ToList() ?? [];
        if (rows.Count == 0) return;
        bool fromWiki = wiki is { Languages.Count: > 0 };

        // The user's language first (Windows display language, by its English name).
        var mine = L.SystemUICulture;
        bool IsMine(string name) =>
            name.Equals(mine.EnglishName, StringComparison.OrdinalIgnoreCase) || name.Equals(mine.Parent.EnglishName, StringComparison.OrdinalIgnoreCase)
            || (mine.Parent.EnglishName.Length > 2 && name.StartsWith(mine.Parent.EnglishName, StringComparison.OrdinalIgnoreCase));
        rows = rows.OrderByDescending(r => IsMine(r.Name)).ToList();

        _languagesCard!.Visibility = Visibility.Visible;
        int audio = rows.Count(r => r.Audio == "true");
        _languagesSummary.Text = L.N(rows.Count, "{0} language", "{0} languages") + (audio > 0 ? "  ·  " + L.F("{0} with full audio", audio) : "")
            + (fromWiki ? "  ·  PCGamingWiki" : "  ·  " + L.T("Steam store"));

        var table = new Grid { ColumnSpacing = 6, RowSpacing = 0 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        void Cell(FrameworkElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            table.Children.Add(e);
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heads = new[] { "", L.T("UI"), L.T("AUDIO"), L.T("SUBS") };
        for (int c = 0; c < 4; c++)
        {
            var h = new TextBlock { Text = heads[c], Style = Ui.Style("GroupHeader"), HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            Cell(h, 0, c);
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int row = i + 1;
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            if (IsMine(r.Name))
            {
                var a = Ui.ToneColor(Ui.Tone.Accent);
                var band = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x26, a.R, a.G, a.B)), Margin = new Thickness(-6, 1, -6, 1) };
                Grid.SetColumnSpan(band, 4);
                Cell(band, row, 0);
            }
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = L.T(r.Name), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = IsMine(r.Name) ? FontWeights.SemiBold : FontWeights.Normal });
            if (IsMine(r.Name)) name.Children.Add(new TextBlock { Text = L.T("YOURS"), FontSize = 10, FontWeight = FontWeights.Bold, CharacterSpacing = 60, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            if (r.Notes.Trim().Length > 0) ToolTipService.SetToolTip(name, r.Notes.Trim());
            Cell(name, row, 0);
            Cell(Mark(r.Interface), row, 1);
            Cell(Mark(r.Audio), row, 2);
            Cell(Mark(r.Subtitles), row, 3);
        }
        _languages.Children.Clear();
        _languages.Children.Add(rows.Count > 10 ? Ui.Collapsible(table, 10 * 28 + 24) : table);

        static FrameworkElement Mark(string state)
        {
            var (glyph, tone, tip) = state switch
            {
                "true" => ("", Ui.Tone.Good, L.T("Yes")),
                "false" => ("", Ui.Tone.Unknown, L.T("No")),
                "limited" => ("", Ui.Tone.Partial, L.T("Partly")),
                _ => ("", Ui.Tone.Unknown, L.T("Not known")),
            };
            if (glyph.Length == 0) return new TextBlock { Text = "·", Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = new SolidColorBrush(Ui.ToneColor(tone)), Opacity = state == "false" ? 0.5 : 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(icon, tip);
            return icon;
        }
    }

    async Task FillControllersAsync(StoreDetails? store)
    {
        PcgwInfo? wiki = null;
        try { wiki = await _data.Pcgw; }
        catch (Exception ex) { Log.Warn($"Overview controllers: {ex.Message}"); }
        if (Gone) return;
        _controllers.Children.Clear();

        Ui.Tone T(bool? yes) => yes switch { true => Ui.Tone.Good, false => Ui.Tone.Bad, _ => Ui.Tone.Unknown };
        string Y(bool? yes) => yes switch { true => L.T("Yes"), false => L.T("No"), _ => L.T("Unknown") };
        PcgwFeature? Input(string label) => wiki?.Input.FirstOrDefault(f => f.Label == label);

        // Xbox: Steam's own flags first, the wiki otherwise.
        var (xbox, xboxTone) = store is { XboxFull: true } ? (L.T("Full"), Ui.Tone.Good)
            : store is { XboxPartial: true } ? (L.T("Partial"), Ui.Tone.Partial)
            : Input("Xbox controllers") is { } x ? Ui.State(x)
            : store is null ? (L.T("Unknown"), Ui.Tone.Unknown) : (L.T("No"), Ui.Tone.Bad);
        _controllers.Children.Add(Ui.Row(L.T("Xbox controllers"), Ui.Pill(xbox, xboxTone)));

        var models = wiki?.PlayStationModels.Select(m => m.Trim().ToUpperInvariant()).ToHashSet() ?? [];
        bool? ps = Input("PlayStation controllers") is { } p ? p.Yes ? true : p.No ? false : null : null;
        bool? ds4 = store?.DualShock == true || models.Contains("DS4") ? true : store is not null || ps == false ? false : null;
        bool? ds5 = store?.DualSense == true || models.Contains("DS") || models.Contains("DS5") ? true : store is not null || ps == false ? false : null;
        _controllers.Children.Add(Ui.Row("DualShock 4", Ui.Pill(Y(ds4), T(ds4))));
        _controllers.Children.Add(Ui.Row("DualSense", Ui.Pill(ds5 == true && store?.DualSenseBluetooth == true ? L.T("Yes, also Bluetooth") : Y(ds5), T(ds5))));
        if (models.Contains("DSE")) _controllers.Children.Add(Ui.Row("DualSense Edge", Ui.Pill(L.T("Yes"), Ui.Tone.Good)));

        // DualSense features: only the wiki tracks them.
        _controllers.Children.Add(new TextBlock { Text = L.T("DUALSENSE FEATURES"), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, 8, 0, 0) });
        foreach (var (f, label) in new[] { (wiki?.AdaptiveTriggers, "Adaptive triggers"), (wiki?.Haptics, "Haptic feedback") })
        {
            var (text, tone) = Ui.State(f);
            _controllers.Children.Add(Ui.Row(L.T(label), Ui.Pill(text, tone), f?.Notes));
        }
        if (Input("PlayStation button prompts") is { } prompts)
        {
            var (text, tone) = Ui.State(prompts);
            _controllers.Children.Add(Ui.Row(L.T("PlayStation button prompts"), Ui.Pill(text, tone), prompts.Notes));
        }
        if (wiki is null) _controllers.Children.Add(Ui.Muted(L.T("PCGamingWiki has no page for this game: DualSense features are unknown.")));
    }

    // ---------- HowLongToBeat ----------

    HltbTimes? _times;

    async Task FillHltbAsync()
    {
        try { _times = await _data.Hltb; }
        catch (Exception ex) { Log.Warn($"Overview HLTB: {ex.Message}"); }
        if (Gone) return;
        _hltb.Children.Clear();
        if (_times is null)
        {
            _hltb.Children.Add(Ui.Muted(L.T("No times found on HowLongToBeat for this game.")));
            return;
        }
        var tiles = new[] { (L.T("Main story"), _times.Main), (L.T("Main + extras"), _times.MainExtra), (L.T("Completionist"), _times.Completionist), (L.T("All styles"), _times.AllStyles) }
            .Select(t => (FrameworkElement)Ui.ValueTile(t.Item1, Ui.Hours(t.Item2))).ToList();
        _hltb.Children.Add(Ui.TwoByN(tiles));
        if (!SameName(_times.Name)) _hltb.Children.Add(Ui.Muted(L.F("As \"{0}\" on HowLongToBeat", _times.Name)));
    }

    bool SameName(string name) => GameNames.SameGame(_data.Game.Name, name);

    void OpenHltb() => Ui.Open(_times?.Url ?? $"https://howlongtobeat.com/?q={Uri.EscapeDataString(_data.SearchName)}");

    // ---------- Metacritic ----------

    MetacriticScores? _scores;

    async Task FillReviewsAsync()
    {
        try { _scores = await _data.Reviews; }
        catch (Exception ex) { Log.Warn($"Overview Metacritic: {ex.Message}"); }
        if (Gone) return;
        _reviews.Children.Clear();
        if (_scores is null)
        {
            _reviews.Children.Add(Ui.Muted(L.T("No scores found on Metacritic for this game.")));
            return;
        }
        var s = _scores;
        var tiles = new List<FrameworkElement>
        {
            Ui.ValueTile(L.T("Metascore"), s.Metascore?.ToString() ?? "—", s.Metascore is { } m ? ScoreColor(m) : null,
                s.Metascore is null ? L.T("No critic reviews") : L.N(s.CriticReviews, "{0} critic review", "{0} critic reviews")),
            Ui.ValueTile(L.T("User score"), s.UserScore?.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) ?? "—",
                s.UserScore is { } u ? ScoreColor((int)Math.Round(u * 10)) : null,
                s.UserScore is null ? L.T("No user ratings") : L.N(s.UserRatings, "{0} rating", "{0} ratings")),
        };
        _reviews.Children.Add(Ui.TwoByN(tiles));
        var notes = new List<string>();
        if (s.CriticVerdict.Length > 0) notes.Add(L.T(s.CriticVerdict));
        if (s.MetascorePlatform is { } platform) notes.Add(L.F("Metascore of the {0} version", platform));
        if (s.MustPlay) notes.Add(L.T("Must-play"));
        if (notes.Count > 0) _reviews.Children.Add(Ui.Muted(string.Join("  ·  ", notes)));
        if (!SameName(s.Title)) _reviews.Children.Add(Ui.Muted(L.F("As \"{0}\" on Metacritic", s.Title)));
    }

    /// <summary>Metacritic's own colours: green from 75, yellow from 50, red below.</summary>
    static Color ScoreColor(int score) => score >= 75 ? Color.FromArgb(0xFF, 0x3F, 0xC2, 0x6A) : score >= 50 ? Color.FromArgb(0xFF, 0xF5, 0xC5, 0x18) : Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D);

    void OpenMetacritic() => Ui.Open(_scores?.Url ?? $"https://www.metacritic.com/search/{Uri.EscapeDataString(_data.SearchName)}/");

    // ---------- achievements ----------

    /// <summary>Called by the game page whenever Steam's achievement data changes (null: none for this game).</summary>
    public void SetAchievements(AchievementSet? set)
    {
        if (_achCard is null) return;
        _achBody.Children.Clear();
        _achCard.Visibility = set is null ? Visibility.Collapsed : Visibility.Visible;
        if (set is null) return;

        double share = (double)set.UnlockedCount / set.Total;
        // Every achievement unlocked: the heading shows the platinum trophy, as the play bar and the cover do.
        bool platinum = set.Total > 0 && set.UnlockedCount == set.Total;
        if (_achHead is { } h && h.Children.Count > 0 && h.Children[0] is Viewbox)
        {
            h.Children[0] = Icons.Trophy(22, platinum);
            ToolTipService.SetToolTip(h.Children[0], platinum ? L.T("Platinum: every Steam achievement unlocked") : null);
        }
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = L.F("{0} of {1} unlocked", set.UnlockedCount, set.Total), VerticalAlignment = VerticalAlignment.Center });
        var pct = new TextBlock { Text = $"{share:P0}", FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush") };
        Grid.SetColumn(pct, 1);
        head.Children.Add(pct);
        _achBody.Children.Add(head);
        _achBody.Children.Add(new ProgressBar { Maximum = 1, Value = share });

        // Newest unlocks first, then the rarest locked ones: each opens the Achievements tab.
        var picks = set.Items.Where(a => a.Unlocked).OrderByDescending(a => a.UnlockedAt ?? DateTime.MinValue).Take(4)
            .Concat(set.Items.Where(a => !a.Unlocked && !a.Hidden).OrderBy(a => a.GlobalPercent ?? 100).Take(4)).Take(8).ToList();
        var icons = new List<FrameworkElement>();
        foreach (var a in picks)
        {
            var url = a.Unlocked ? a.IconUrl : a.IconGrayUrl ?? a.IconUrl;
            var box = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(6), Background = Ui.Brush("ControlFillColorSecondaryBrush"), Opacity = a.Unlocked ? 1 : 0.55 };
            if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                box.Child = Smooth.Art(new BitmapImage { UriSource = uri, DecodePixelWidth = 64 }, Smooth.Inner(box), Stretch.UniformToFill);
            var button = new Button { Content = box, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            ToolTipService.SetToolTip(button, $"{a.Name}\n{a.Description}\n\n" + (a.Unlocked ? (a.UnlockedAt is { } t ? L.F("Unlocked {0:d MMM yyyy}", t) : L.T("Unlocked")) : L.T("Locked"))
                + (a.GlobalPercent is { } p ? "  ·  " + L.F("{0:0.#}% of players", p) : ""));
            button.Click += (_, _) => _data.ShowTab("ach");
            icons.Add(button);
        }
        if (icons.Count > 0) _achBody.Children.Add(Ui.Wrap(icons, 6));
    }
}

// ======================================================================================================================

/// <summary>The "OptiScaler wiki" tab: the game's entry on the OptiScaler wiki, field by field.</summary>
public sealed partial class OptiWikiView : UserControl
{
    readonly GameData _data;
    readonly StackPanel _root = new() { Spacing = 12 };

    public OptiWikiView(GameData data)
    {
        _data = data;
        Content = _root;
        _root.Children.Add(Ui.Card(Ui.Loading()));
        _ = FillAsync();
    }

    async Task FillAsync()
    {
        OptiWikiEntry? entry = null;
        try { entry = await _data.OptiWiki; } catch (Exception ex) { Log.Warn($"OptiScaler wiki tab: {ex.Message}"); }
        if (_data.Token.IsCancellationRequested) return;
        _root.Children.Clear();

        if (entry is null)
        {
            var empty = new StackPanel { Spacing = 10 };
            empty.Children.Add(Ui.Heading("", L.T("OptiScaler wiki"), Ui.LinkButton(L.T("Compatibility List"), "", () => Ui.Open(CompatWiki.ListUrl))));
            empty.Children.Add(new TextBlock { Text = L.F("{0} has no page on the OptiScaler wiki yet (or Hybrid Launcher could not reach GitHub).", _data.Game.Title), TextWrapping = TextWrapping.Wrap });
            empty.Children.Add(Ui.Muted(L.T("The Compatibility List shows every tested game. If you try OptiScaler here, the team welcomes a report on their Discord or GitHub.")));
            _root.Children.Add(Ui.Card(empty));
            return;
        }

        // Header: the page and the facts of the test (version, file name, hardware, who reported it).
        var head = new StackPanel { Spacing = 10 };
        head.Children.Add(Ui.Heading("", entry.Title, Ui.LinkButton(L.T("Open on GitHub"), "", () => Ui.Open(entry.Url)), L.T("What the OptiScaler wiki says about this game")));
        var facts = new List<FrameworkElement>();
        foreach (var name in new[] { "Last Tested Version", "Filename", "GPU", "OS", "Reported By" })
            if (entry.Get(name) is { IsEmpty: false } f) facts.Add(Fact(L.T(name), f.Text));
        if (facts.Count > 0) head.Children.Add(Ui.Wrap(facts, 8));
        _root.Children.Add(Ui.Card(head));

        string[] summary = ["Last Tested Version", "Filename", "GPU", "OS", "Reported By"];
        var cards = new List<Border>();
        foreach (var field in entry.Fields.Where(f => !summary.Contains(f.Name, StringComparer.OrdinalIgnoreCase)))
        {
            if (field.IsEmpty) continue;
            var (glyph, tone) = field.Name.ToLowerInvariant() switch
            {
                var n when n.Contains("issue") => ("", Ui.Tone.Partial),
                var n when n.Contains("setting") => ("", Ui.Tone.Accent),
                var n when n.Contains("fg") || n.Contains("frame") => ("", Ui.Tone.Accent),
                var n when n.Contains("input") => ("", Ui.Tone.Accent),
                _ => ("", Ui.Tone.Unknown),
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(Ui.Heading(glyph, L.T(field.Name)));
            panel.Children.Add(Ui.Rich(field.Lines));
            var card = Ui.Card(panel);
            if (tone == Ui.Tone.Partial)
            {
                var c = Ui.ToneColor(tone);
                card.Background = new SolidColorBrush(Color.FromArgb(0x1A, c.R, c.G, c.B));
            }
            cards.Add(card);
        }
        _root.Children.Add(Columns(cards));
        _root.Children.Add(Ui.Muted(L.T("From the OptiScaler wiki on GitHub, written by the community. Settings named here are keys of OptiScaler.ini (the OptiScaler settings tab).")));
    }

    static FrameworkElement Fact(string name, string value)
    {
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(new TextBlock { Text = name.ToUpper(), Style = Ui.Style("GroupHeader"), FontSize = 11 });
        panel.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = true });
        return new Border { Style = Ui.Style("RowStyle"), MinHeight = 0, Padding = new Thickness(12, 6, 12, 8), Child = panel };
    }

    /// <summary>Cards in two columns on wide pages (each into the shorter column by text length), one otherwise.</summary>
    public static FrameworkElement Columns(List<Border> cards, Func<Border, int>? weight = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        var left = new StackPanel { Spacing = 12 };
        var right = new StackPanel { Spacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        int lw = 0, rw = 0;
        foreach (var card in cards)
        {
            int w = weight?.Invoke(card) ?? 1 + Words(card);
            if (lw <= rw) { left.Children.Add(card); lw += w; }
            else { right.Children.Add(card); rw += w; }
        }
        grid.SizeChanged += (_, e) =>
        {
            bool two = e.NewSize.Width >= 820;
            grid.ColumnDefinitions[1].Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            grid.ColumnSpacing = two ? 12 : 0;
            Grid.SetColumn(right, two ? 1 : 0);
            Grid.SetRow(right, two ? 0 : 1);
            right.Margin = new Thickness(0, two ? 0 : 12, 0, 0);
        };
        return grid;

        static int Words(FrameworkElement e) => e switch
        {
            TextBlock t => 1 + (t.Text?.Length ?? 0) / 60,
            Panel p => p.Children.OfType<FrameworkElement>().Sum(Words),
            Border b when b.Child is FrameworkElement c => Words(c),
            _ => 0,
        };
    }
}

// ======================================================================================================================

/// <summary>
/// The "PCGamingWiki" tab, laid out like the wiki's own page: on the left the page's contents (numbered sections and their
/// sub-headings, the one being read marked, pinned under the tab bar while the page scrolls); in the middle every section in
/// the wiki's own order, each feature a row of a table (name, state, the wiki's notes in full); on the right, as on the
/// site, the infobox and a search. Every link of the page (mods, fixes, stores, other wiki articles) opens.
/// </summary>
public sealed partial class PcgwView : UserControl
{
    const double TocWidth = 250, SideWidth = 340, TocFrom = 1000, SideFrom = 1500;
    readonly GameData _data;
    readonly Grid _layout = new() { RowSpacing = 12 };
    readonly StackPanel _main = new() { Spacing = 14 };
    readonly StackPanel _side = new() { Spacing = 12 };
    // The page's sections, in order; a search shows its results here instead.
    readonly StackPanel _stack = new() { Spacing = 14 };
    readonly Border _tocSlot = new() { VerticalAlignment = VerticalAlignment.Top };
    readonly StackPanel _tocList = new() { Spacing = 1 };
    readonly Border _toc;
    readonly List<Entry> _entries = [];
    List<Entry> _pageEntries = [];
    double[] _tops = [];
    bool _topsDirty = true;
    int _active = -1;
    int _layoutMode = -1;
    // No wiki page for the game: no contents column, whatever the width (it stood empty beside the message).
    bool _noPage;
    UIElement? _spyRoot;
    double _spyLine;
    double _contentWidth = 900;

    /// <summary>One line of the contents: a section or a sub-heading, and where it is on the page (made when the section is).</summary>
    sealed class Entry
    {
        public required string Id;
        public required string Number;
        public required string Title;
        public int Level;
        public Color Tint;
        public required Border Host;
        public Func<FrameworkElement>? Build;
        public FrameworkElement? Target;
        public Button? Row;
        public Entry? Parent;
    }

    /// <summary>A line of the contents was clicked: the game page scrolls to this element (its top under the pinned bar).</summary>
    public event Action<FrameworkElement>? ScrollRequested;

    /// <summary>The contents column: the game page moves it to a pinned spot of its own while the tab bar is pinned.</summary>
    public FrameworkElement Toc => _toc;
    public Border TocSlot => _tocSlot;
    public bool TocShown => _layoutMode >= 1;

    public PcgwView(GameData data)
    {
        _data = data;
        var tocPanel = new StackPanel { Spacing = 6 };
        var head = new Button
        {
            Content = new TextBlock { Text = L.T("Contents"), Style = Ui.Style("BodyStrongTextBlockStyle"), FontSize = 15 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        ToolTipService.SetToolTip(head, L.T("Back to the top of the page"));
        head.Click += (_, _) => ScrollRequested?.Invoke(this);
        tocPanel.Children.Add(head);
        tocPanel.Children.Add(new ScrollViewer { Content = _tocList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 6, 0) });
        _toc = new Border { Style = Ui.Style("CardStyle"), Padding = new Thickness(6, 10, 4, 10), Child = tocPanel, Width = TocWidth };
        _tocSlot.Child = _toc;

        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TocWidth) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SideWidth) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.Children.Add(_tocSlot);
        _layout.Children.Add(_main);
        _layout.Children.Add(_side);
        Content = _layout;
        _main.Children.Add(Ui.Card(Ui.Loading()));
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        // Sections built later (one per frame) move the ones under them: measured again, and the marked line follows at once.
        _stack.SizeChanged += (_, _) =>
        {
            _topsDirty = true;
            if (_spyRoot is { } root) DispatcherQueue.TryEnqueue(() => UpdateSpy(root, _spyLine));
        };
        Arrange(SideFrom);
        _ = FillAsync();
    }

    /// <summary>
    /// Wide: contents | page | infobox, as on the wiki. Medium: contents | page, the infobox above the page. Narrow: the page
    /// alone with the infobox above it.
    /// </summary>
    void Arrange(double width)
    {
        int mode = _noPage ? 0 : width >= SideFrom ? 2 : width >= TocFrom ? 1 : 0;
        _contentWidth = width - (mode >= 1 ? TocWidth + 16 : 0) - (mode == 2 ? SideWidth + 16 : 0);
        if (mode == _layoutMode) return;
        _layoutMode = mode;
        _layout.ColumnDefinitions[0].Width = mode >= 1 ? new GridLength(TocWidth) : new GridLength(0);
        _layout.ColumnDefinitions[2].Width = mode == 2 ? new GridLength(SideWidth) : new GridLength(0);
        _layout.ColumnSpacing = mode >= 1 ? 16 : 0;
        _tocSlot.Visibility = mode >= 1 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRowSpan(_tocSlot, 2);
        Grid.SetColumn(_main, 1);
        Grid.SetColumn(_side, mode == 2 ? 2 : 1);
        Grid.SetRow(_side, 0);
        Grid.SetRow(_main, mode == 2 ? 0 : 1);
        Grid.SetRowSpan(_main, mode == 2 ? 2 : 1);
        _side.VerticalAlignment = VerticalAlignment.Top;
        TocLayoutChanged?.Invoke();
    }

    /// <summary>The contents column appeared or went away (the window was resized).</summary>
    public event Action? TocLayoutChanged;

    /// <summary>The element of a section by id ("page2", "issues"): built now if it was still waiting for its frame.</summary>
    public FrameworkElement? SectionElement(string id)
    {
        var e = id == "issues"
            ? _entries.FirstOrDefault(x => x.Level == 1 && x.Title.Contains("issue", StringComparison.OrdinalIgnoreCase))
            : _entries.FirstOrDefault(x => x.Id == id);
        return e is null ? null : Target(e);
    }

    FrameworkElement Target(Entry e)
    {
        var section = e.Parent ?? e;
        if (section.Host.Child is null && section.Build is { } build) section.Host.Child = build();
        return e.Target ?? section.Host;
    }

    async Task FillAsync()
    {
        PcgwInfo? wiki = null;
        try { wiki = await _data.Pcgw; } catch (Exception ex) { Log.Warn($"PCGamingWiki tab: {ex.Message}"); }
        if (_data.Token.IsCancellationRequested) return;
        _main.Children.Clear();

        if (wiki is null)
        {
            var empty = new StackPanel { Spacing = 10 };
            empty.Children.Add(Ui.Heading("\uE774", "PCGamingWiki", Ui.LinkButton(L.T("Search the wiki"), "\uE721",
                () => Ui.Open($"https://www.pcgamingwiki.com/w/index.php?search={Uri.EscapeDataString(_data.SearchName)}"))));
            empty.Children.Add(new TextBlock { Text = L.F("No PCGamingWiki page was found for {0} (or the wiki could not be reached).", _data.Game.Title), TextWrapping = TextWrapping.Wrap });
            _main.Children.Add(Ui.Card(empty));
            _noPage = true;
            _layoutMode = -1;
            Arrange(ActualWidth > 0 ? ActualWidth : SideFrom);
            return;
        }

        var search = new AutoSuggestBox { PlaceholderText = L.T("Search the whole page"), QueryIcon = new SymbolIcon(Symbol.Find), HorizontalAlignment = HorizontalAlignment.Stretch };
        _side.Children.Add(Summary(wiki, search));
        if (wiki.Infobox is { } box) _side.Children.Add(InfoboxCard(box));
        _main.Children.Add(_stack);

        // Every section in the wiki's order, numbered as the wiki's contents number them (the lead has no number).
        int n = 0;
        foreach (var page in wiki.Page)
        {
            var p = page;
            var blocks = Blocks(p, skipGameDataTables: false);
            // The lead, once the infobox has its own card, may hold nothing but a heading: no empty section.
            if (blocks.Count == 0) continue;
            bool lead = _entries.Count == 0 && p.Title == L.T("Introduction");
            var number = lead ? "" : (++n).ToString();
            var (glyph, tint) = SectionLook(p.Title);
            var entry = new Entry { Id = $"page{_entries.Count(e => e.Level == 1)}", Number = number, Title = p.Title, Level = 1, Tint = tint, Host = new Border() };
            var subs = new List<Entry>();
            int k = 0;
            foreach (var b in blocks.Where(b => b.Kind == PcgwBlockKind.Heading))
                subs.Add(new Entry { Id = $"{entry.Id}h{k}", Number = lead ? "" : $"{number}.{++k}", Title = b.Title, Level = 2, Tint = tint, Host = entry.Host, Parent = entry });
            entry.Build = p.Title.ToLowerInvariant() switch
            {
                "game data" => () => GameDataCard(wiki, p, entry, subs),
                "video" => () => PageSection(p, entry, subs, Upscaling(wiki)),
                _ => () => PageSection(p, entry, subs),
            };
            _entries.Add(entry);
            _entries.AddRange(subs);
        }

        // The first sections are built at once; the rest one per frame after, so the tab opens without a stall and the
        // scroll stays smooth while the long ones (tables, system requirements) are made.
        int built = 0;
        foreach (var e in _entries.Where(e => e.Level == 1))
        {
            _stack.Children.Add(e.Host);
            var entry = e;
            if (built++ < 3) entry.Host.Child = entry.Build!();
            else DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_data.Token.IsCancellationRequested && entry.Host.Child is null) entry.Host.Child = entry.Build!();
            });
        }
        _pageEntries = [.. _entries];
        FillToc();

        var searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        searchTimer.Tick += (_, _) =>
        {
            searchTimer.Stop();
            ShowSearch(wiki, search.Text.Trim());
        };
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
    }

    /// <summary>The contents column: a line per section (bold) and per sub-heading (indented), numbered as on the wiki.</summary>
    void FillToc()
    {
        _tocList.Children.Clear();
        _active = -1;
        _topsDirty = true;
        foreach (var e in _entries)
        {
            var text = new Grid { ColumnSpacing = 8 };
            text.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            text.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            text.Children.Add(new TextBlock { Text = e.Number, FontSize = e.Level == 1 ? 13 : 12, Opacity = 0.55, MinWidth = e.Level == 1 ? 18 : 26 });
            var title = new TextBlock
            {
                Text = e.Title, FontSize = e.Level == 1 ? 13.5 : 12.5, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = e.Level == 1 ? FontWeights.SemiBold : FontWeights.Normal, Opacity = e.Level == 1 ? 1 : 0.85,
            };
            Grid.SetColumn(title, 1);
            text.Children.Add(title);
            var row = new Button
            {
                Content = text,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(e.Level == 1 ? 8 : 20, e.Level == 1 ? 6 : 3, 6, e.Level == 1 ? 6 : 3),
                Margin = new Thickness(0, e.Level == 1 && _tocList.Children.Count > 0 ? 4 : 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var entry = e;
            row.Click += (_, _) => ScrollRequested?.Invoke(Target(entry));
            e.Row = row;
            _tocList.Children.Add(row);
        }
    }

    /// <summary>
    /// Scroll-spy, called by the game page on every scroll step: the line of the section (or sub-heading) at the top of the
    /// page is marked. Cheap: the places are measured again only after the page changed size, rows are touched only on a change.
    /// </summary>
    public void UpdateSpy(UIElement root, double line)
    {
        _spyRoot = root;
        _spyLine = line;
        if (_entries.Count == 0 || _layoutMode < 1) return;
        if (_topsDirty || _tops.Length != _entries.Count)
        {
            _tops = _entries.Select(e =>
            {
                var t = e.Target ?? (e.Parent is null ? e.Host : null);
                return t is { } el && el.XamlRoot is not null && el.ActualHeight > 0 ? el.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y : double.NaN;
            }).ToArray();
            _topsDirty = false;
        }
        int active = 0;
        for (int i = 0; i < _tops.Length; i++)
            if (!double.IsNaN(_tops[i]) && _tops[i] <= line) active = i;
        if (active == _active) return;
        if (_active >= 0 && _active < _entries.Count) Mark(_entries[_active], false);
        _active = active;
        Mark(_entries[active], true);
        // The marked line stays in sight in a long contents list.
        _entries[active].Row?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
    }

    static void Mark(Entry e, bool on)
    {
        if (e.Row is not { } row) return;
        var accent = Ui.ToneColor(Ui.Tone.Accent);
        row.BorderBrush = new SolidColorBrush(on ? accent : Microsoft.UI.Colors.Transparent);
        row.Background = on ? new SolidColorBrush(Color.FromArgb(0x24, accent.R, accent.G, accent.B)) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>The side column's first card: the page, its tags, what matters at a glance (as plain lines), and the search.</summary>
    static Border Summary(PcgwInfo wiki, AutoSuggestBox search)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Ui.Heading("\uE774", wiki.Title, null, "PCGamingWiki", Ui.Blue));
        var chips = Pcgw.UpscalerTags(string.Join(", ", wiki.UpscalingTech), wiki.Apis).Select(t => (FrameworkElement)TechTags.Create(t, fontSize: 12))
            .Concat(Pcgw.FrameGenTags(wiki.FrameGenTech).Select(t => (FrameworkElement)TechTags.Create(t, frameGen: true, fontSize: 12)))
            .Concat(wiki.Apis.Select(a => (FrameworkElement)Ui.Chip(a))).ToList();
        if (chips.Count > 0) panel.Children.Add(Ui.Wrap(chips));
        var glance = new StackPanel { Spacing = 0 };
        glance.Children.Add(Ui.Row(L.T("Open issues"), Ui.Pill(wiki.IssuesUnresolved.Count.ToString(), wiki.IssuesUnresolved.Count > 0 ? Ui.Tone.Bad : Ui.Tone.Good)));
        glance.Children.Add(Ui.Row(L.T("Essential improvements"), Ui.Pill(wiki.Improvements.Count.ToString(), wiki.Improvements.Count > 0 ? Ui.Tone.Good : Ui.Tone.Unknown)));
        glance.Children.Add(Ui.Row(L.T("Fixed issues"), Ui.Pill(wiki.IssuesFixed.Count.ToString(), Ui.Tone.Unknown)));
        var (up, upTone) = Ui.State(wiki.Upscaling);
        glance.Children.Add(Ui.Row(L.T("Upscaling"), Ui.Pill(up, upTone)));
        var (fg, fgTone) = Ui.State(wiki.FrameGen);
        glance.Children.Add(Ui.Row(L.T("Frame generation"), Ui.Pill(fg, fgTone)));
        panel.Children.Add(glance);
        panel.Children.Add(search);
        var open = Ui.LinkButton(L.T("Open on PCGamingWiki"), "\uE8A7", () => Ui.Open(wiki.Url));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(open);
        return Ui.Card(panel);
    }

    /// <summary>The wiki's infobox: who made it, the engine, release dates, scores, taxonomy, and its pages on other sites.</summary>
    static Border InfoboxCard(PcgwInfobox box)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Ui.Heading("\uE946", L.T("Game info"), tint: Ui.Slate));
        void Group(string title, FrameworkElement body)
        {
            var g = new StackPanel { Spacing = 4 };
            g.Children.Add(new TextBlock { Text = title.ToUpper(), Style = Ui.Style("GroupHeader"), FontSize = 11 });
            g.Children.Add(body);
            panel.Children.Add(g);
        }
        TextBlock Text(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (box.Developers.Count > 0) Group(L.T("Developers"), Text(string.Join(", ", box.Developers)));
        if (box.Publishers.Count > 0) Group(L.T("Publishers"), Text(string.Join(", ", box.Publishers)));
        if (box.Engines.Count > 0) Group(L.T("Engines"), Text(string.Join(", ", box.Engines)));
        if (box.Releases.Count > 0)
        {
            var dates = new StackPanel();
            foreach (var (platform, date) in box.Releases) dates.Children.Add(Ui.Row(platform, new TextBlock { Text = date, Opacity = 0.85 }));
            Group(L.T("Release dates"), dates);
        }
        if (box.Scores.Count > 0)
        {
            var scores = box.Scores.Select(s =>
            {
                int.TryParse(s.Score, out var v);
                var tone = v >= 75 ? Ui.Tone.Good : v >= 50 ? Ui.Tone.Partial : v > 0 ? Ui.Tone.Bad : Ui.Tone.Unknown;
                var chip = new Button { Content = $"{s.Site}  {s.Score}", Padding = new Thickness(10, 4, 10, 5), MinWidth = 0 };
                var c = Ui.ToneColor(tone);
                chip.Background = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B));
                chip.BorderThickness = new Thickness(0);
                if (s.Url is { } url) chip.Click += (_, _) => Ui.Open(url);
                else chip.IsEnabled = false;
                return (FrameworkElement)chip;
            });
            Group(L.T("Reception"), Ui.Wrap(scores));
        }
        if (box.Taxonomy.Count > 0)
        {
            var rows = new StackPanel();
            foreach (var (label, value) in box.Taxonomy)
                rows.Children.Add(Ui.Row(L.T(label), new TextBlock { Text = value, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, MaxWidth = 200, TextAlignment = TextAlignment.Right }));
            Group(L.T("Taxonomy"), rows);
        }
        if (box.Links.Count > 0)
            Group(L.T("Elsewhere"), Ui.Wrap(box.Links.Select(l =>
            {
                var b = Ui.LinkButton(l.Site == "Official site" ? L.T("Official site") : l.Site, "\uE8A7", () => Ui.Open(l.Url));
                ToolTipService.SetToolTip(b, l.Url);
                return (FrameworkElement)b;
            })));
        return Ui.Card(panel);
    }

    /// <summary>A query of two letters or more shows only the blocks that mention it, a section each; clearing it brings the page back.</summary>
    void ShowSearch(PcgwInfo wiki, string query)
    {
        _stack.Children.Clear();
        _entries.Clear();
        if (query.Length < 2)
        {
            _entries.AddRange(_pageEntries);
            foreach (var e in _entries.Where(e => e.Level == 1)) _stack.Children.Add(e.Host);
            FillToc();
            return;
        }
        int hits = 0, n = 0;
        foreach (var section in wiki.Page)
        {
            var found = section.Blocks.Where(b => b.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0) continue;
            hits += found.Count;
            var (_, tint) = SectionLook(section.Title);
            var entry = new Entry { Id = $"hit{n}", Number = (++n).ToString(), Title = section.Title, Level = 1, Tint = tint, Host = new Border() };
            entry.Host.Child = PageSection(new PcgwSection(section.Title, found), entry, []);
            _entries.Add(entry);
            _stack.Children.Add(entry.Host);
        }
        _stack.Children.Insert(0, Ui.Card(Ui.Muted(hits == 0
            ? L.F("Nothing on this page mentions \"{0}\".", query)
            : L.F("{0} parts of the page mention \"{1}\". Clear the search to see the whole page.", hits, query))));
        FillToc();
    }

    static (string Glyph, Color Tint) SectionLook(string title) => title.ToLowerInvariant() switch
    {
        "game info" or "introduction" => ("\uE946", Ui.Slate),
        "availability" => ("\uE719", Ui.Teal),
        "monetization" or "microtransactions" => ("\uE8C7", Ui.Gold),
        "dlc and expansion packs" or "dlc" => ("\uE7B8", Ui.Gold),
        "essential improvements" => ("\uE73E", Ui.Green),
        "issues unresolved" => ("\uE7BA", Ui.Red),
        "issues fixed" => ("\uE90F", Ui.Green),
        "game data" => ("\uE8B7", Ui.Orange),
        "video" => ("\uE7F4", Ui.Pink),
        "input" => ("\uE7FC", Ui.Blue),
        "audio" => ("\uE767", Ui.Teal),
        "localizations" => ("\uE775", Ui.Green),
        "network" => ("\uE968", Ui.Blue),
        "vr support" => ("\uE7B3", Ui.Pink),
        "mods" => ("\uEA86", Ui.Yellow),
        "other information" => ("\uE9CE", Ui.Slate),
        "system requirements" => ("\uE7F8", Ui.Yellow),
        _ => ("\uE8A5", Ui.Slate),
    };

    /// <summary>A section's heading as on the wiki: its number and name, large, over a rule in the section's colour.</summary>
    static FrameworkElement SectionHeading(Entry entry)
    {
        var (glyph, tint) = SectionLook(entry.Title);
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (Icons.Show(IconSpots.Headings)) line.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, Foreground = new SolidColorBrush(tint), VerticalAlignment = VerticalAlignment.Center });
        if (entry.Number.Length > 0) line.Children.Add(new TextBlock { Text = entry.Number, FontSize = 22, Opacity = 0.45, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = entry.Title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(line);
        head.Children.Add(Ui.SectionRule(tint));
        ((FrameworkElement)head.Children[1]).Margin = new Thickness(0);
        return head;
    }

    /// <summary>One section of the page as a card: its text, sub-headings, feature tables, tables and fixes, all of it.</summary>
    Border PageSection(PcgwSection section, Entry entry, List<Entry> subs, FrameworkElement? top = null)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SectionHeading(entry));
        if (top is not null) panel.Children.Add(top);
        AddBlocks(panel, Blocks(section, skipGameDataTables: false), subs);
        var card = Ui.Card(panel, 20);
        entry.Target = card;
        return card;
    }

    /// <summary>The blocks of a section; each sub-heading becomes the place its contents line scrolls to.</summary>
    void AddBlocks(Panel panel, List<PcgwBlock> blocks, List<Entry> subs)
    {
        int k = 0;
        foreach (var block in blocks)
        {
            var el = PageBlock(block, block.Kind == PcgwBlockKind.Heading && k < subs.Count ? subs[k].Number : null);
            if (block.Kind == PcgwBlockKind.Heading && k < subs.Count) subs[k++].Target = el;
            panel.Children.Add(el);
        }
        _topsDirty = true;
    }

    /// <summary>
    /// A section's blocks to show. Without the game data tables (shown as paths with Open buttons instead), and without a
    /// sub-heading left with nothing under it.
    /// </summary>
    static List<PcgwBlock> Blocks(PcgwSection section, bool skipGameDataTables)
    {
        string[] gameData = [PcgwPage.Label("Game data/config"), PcgwPage.Label("Game data/saves")];
        var kept = section.Blocks.Where(b => !(skipGameDataTables && b.Kind == PcgwBlockKind.Table && gameData.Contains(b.Title))).ToList();
        return kept.Where((b, i) => b.Kind != PcgwBlockKind.Heading || (i + 1 < kept.Count && kept[i + 1].Kind != PcgwBlockKind.Heading)).ToList();
    }

    FrameworkElement PageBlock(PcgwBlock block, string? number = null)
    {
        switch (block.Kind)
        {
            case PcgwBlockKind.Heading:
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
                if (number is { Length: > 0 }) head.Children.Add(new TextBlock { Text = number, FontSize = 17, Opacity = 0.45, FontWeight = FontWeights.SemiBold });
                head.Children.Add(new TextBlock { Text = block.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                return head;
            }
            case PcgwBlockKind.Text:
                return Ui.Rich(block.Lines, spacing: 4, large: true);
            case PcgwBlockKind.Fix:
            {
                var fix = new StackPanel { Spacing = 6 };
                fix.Children.Add(new TextBlock { Text = L.T("FIX"), Style = Ui.Style("GroupHeader"), FontSize = 11, Foreground = new SolidColorBrush(Ui.Green) });
                if (block.Title.Length > 0) fix.Children.Add(new TextBlock { Text = block.Title, Style = Ui.Style("BodyStrongTextBlockStyle"), FontSize = 15, TextWrapping = TextWrapping.Wrap });
                fix.Children.Add(Ui.Rich(block.Lines, spacing: 5, large: true));
                var g = Ui.Green;
                return new Border
                {
                    Child = fix, Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromArgb(0x14, g.R, g.G, g.B)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, g.R, g.G, g.B)), BorderThickness = new Thickness(3, 0, 0, 0),
                };
            }
            case PcgwBlockKind.Fields:
                return Fields(block);
            default:
                return Table(block);
        }
    }

    /// <summary>
    /// A feature table as on the wiki: one row per feature with its name, its state (a coloured mark and the word) and the
    /// wiki's notes in full beside it, links included; rows on alternating shades. On a narrow page the notes go under the name.
    /// </summary>
    FrameworkElement Fields(PcgwBlock block)
    {
        bool wide = _contentWidth >= 760;
        // As on the wiki: the name column as wide as the longest name (no fixed 220 px gap), the state a mark alone
        // (its word in the tooltip, and beside it only when there are no notes), then the notes at once.
        double labelWidth = Math.Clamp(block.Fields.Select(f => L.T(f.Label).Length).DefaultIfEmpty(10).Max() * 8.2 + 8, 100, wide ? 210 : 150);
        var table = new StackPanel { CornerRadius = new CornerRadius(6) };
        int i = 0;
        foreach (var f in block.Fields)
        {
            var line = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 4, 10, 4) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = wide ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            if (wide) line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i++ % 2 == 0) line.Background = Ui.Brush("SubtleFillColorSecondaryBrush");
            line.Children.Add(new TextBlock { Text = L.T(f.Label), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top });

            FrameworkElement value;
            if (f.HasState)
            {
                var (state, tone) = Ui.State(new PcgwFeature(f.Label, f.State, f.Notes));
                var mark = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
                mark.Children.Add(StateMark(f.State, tone, state));
                if (f.NoteLines.Count == 0 && f.Notes.Length == 0)
                    mark.Children.Add(new TextBlock { Text = state, FontSize = 13, Foreground = new SolidColorBrush(Ui.ToneColor(tone)), VerticalAlignment = VerticalAlignment.Center });
                value = mark;
            }
            else value = f.ValueLines.Count > 0 ? Ui.Rich(f.ValueLines, spacing: 2) : new TextBlock { Text = f.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(value, 1);
            // A value in words (not a state) takes the state and notes columns together; its notes go under it.
            if (!f.HasState && wide) Grid.SetColumnSpan(value, 2);
            line.Children.Add(value);

            if (f.NoteLines.Count > 0 || f.Notes.Length > 0)
            {
                FrameworkElement notes = f.NoteLines.Count > 0 ? Ui.Rich(f.NoteLines, spacing: 2) : new TextBlock { Text = f.Notes, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                notes.Opacity = 0.9;
                if (wide && !f.HasState)
                {
                    line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(notes, 1);
                    Grid.SetColumn(notes, 1);
                    Grid.SetColumnSpan(notes, 2);
                    notes.Margin = new Thickness(0, 4, 0, 0);
                }
                else if (wide) Grid.SetColumn(notes, 2);
                else
                {
                    line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(notes, 1);
                    Grid.SetColumnSpan(notes, 2);
                    notes.Margin = new Thickness(0, 4, 0, 0);
                }
                line.Children.Add(notes);
            }
            table.Children.Add(line);
        }
        var panel = new StackPanel { Spacing = 6 };
        if (block.Title.Length > 0 && block.Fields.Count > 1) panel.Children.Add(new TextBlock { Text = L.T(block.Title).ToUpper(), Style = Ui.Style("GroupHeader"), FontSize = 11 });
        panel.Children.Add(table);
        return panel;
    }

    /// <summary>A table: a header row when the columns have names, then the rows on alternating shades; cells wrap and keep their links.</summary>
    static FrameworkElement Table(PcgwBlock block)
    {
        int cols = Math.Max(block.Columns.Count, block.Rows.Count > 0 ? block.Rows.Max(r => r.Count) : 0);
        var grid = new Grid { ColumnSpacing = 14, MinWidth = cols * 70 };
        // Short columns (an OS, a date, a yes / no) as wide as their text; the long ones share the rest by how long they run.
        // Every short column stretching made the wide gaps between a name and its value.
        var longest = Enumerable.Range(0, cols).Select(c => Math.Max(c < block.Columns.Count ? block.Columns[c].Length : 0,
            block.Rows.Select(r => c < r.Count ? r[c].Length : 0).DefaultIfEmpty(0).Max())).ToArray();
        int widest = Array.IndexOf(longest, longest.DefaultIfEmpty(0).Max());
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = longest[c] > 40 || c == widest ? new GridLength(Math.Max(longest[c], 1), GridUnitType.Star) : GridLength.Auto,
                MaxWidth = longest[c] > 40 || c == widest ? double.PositiveInfinity : 260,
            });
        int row = 0;
        void Add(FrameworkElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); grid.Children.Add(e); }
        if (block.Columns.Any(c => c.Length > 0))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < block.Columns.Count; c++)
                Add(new TextBlock { Text = L.T(block.Columns[c]).ToUpper(), Style = Ui.Style("GroupHeader"), FontSize = 11, Margin = new Thickness(0, 0, 0, 4) }, row, c);
            row++;
        }
        for (int n = 0; n < block.Rows.Count; n++)
        {
            var cells = block.Rows[n];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (n % 2 == 0)
            {
                var shade = new Border { Background = Ui.Brush("SubtleFillColorSecondaryBrush"), CornerRadius = new CornerRadius(4), Margin = new Thickness(-8, 0, -8, 0) };
                Grid.SetColumnSpan(shade, cols);
                Add(shade, row, 0);
            }
            for (int c = 0; c < cells.Count; c++)
            {
                var tb = n < block.RichRows.Count && c < block.RichRows[n].Count && block.RichRows[n][c].Runs.Any(r => r.Link is not null)
                    ? Ui.LineBlock(block.RichRows[n][c])
                    : new TextBlock { Text = cells[c], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                tb.Padding = new Thickness(0, 4, 0, 4);
                if (c == 0) tb.FontWeight = FontWeights.SemiBold;
                Add(tb, row, c);
            }
            row++;
        }
        var panel = new StackPanel { Spacing = 6 };
        if (block.Title.Length > 0) panel.Children.Add(new TextBlock { Text = L.T(block.Title).ToUpper(), Style = Ui.Style("GroupHeader"), FontSize = 11 });
        // Wide tables scroll sideways on a narrow window instead of squeezing every column.
        panel.Children.Add(new ScrollViewer
        {
            Content = grid, Padding = new Thickness(8, 0, 8, 0),
            HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        return panel;
    }

    /// <summary>
    /// A feature's state as the wiki draws it: a small coloured disc with a tick (yes), a cross (no), a half (limited), a wrench
    /// (needs a fix) or a question mark; the word is in its tooltip and beside it.
    /// </summary>
    static FrameworkElement StateMark(string state, Ui.Tone tone, string word)
    {
        var c = Ui.ToneColor(tone);
        string glyph = state switch
        {
            "true" or "always on" or "complete" => "\uE73E",
            "false" => "\uE711",
            "limited" or "partial" => "\uE7BA",
            "hackable" => "\uE90F",
            "n/a" => "\uE738",
            _ => "\uE9CE",
        };
        var mark = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)),
            BorderThickness = new Thickness(0),
            Child = new FontIcon { Glyph = glyph, FontSize = 10, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTipService.SetToolTip(mark, word);
        return mark;
    }

    static Border Upscaling(PcgwInfo wiki)
    {
        var tiles = new TileGrid(320, 10);
        foreach (var (feature, tech, label) in new[] { (wiki.Upscaling, wiki.UpscalingTech, "Upscaling"), (wiki.FrameGen, wiki.FrameGenTech, "Frame generation") })
        {
            var (text, tone) = Ui.State(feature);
            var box = new StackPanel { Spacing = 8 };
            box.Children.Add(Ui.Row(L.T(label), Ui.Pill(text, tone)));
            // The same names as the banner's tags ("FSR FG", not the wiki's "fsr fg").
            var tags = label == "Upscaling" ? Pcgw.UpscalerTags(string.Join(", ", tech), wiki.Apis) : Pcgw.FrameGenTags(tech);
            if (tags.Count > 0) box.Children.Add(Ui.Wrap(tags.Select(t => (FrameworkElement)TechTags.Create(t, fontSize: 12))));
            if (feature is null && tech.Count == 0) box.Children.Add(Ui.Muted(L.T("Not filled in on the wiki yet.")));
            tiles.Children.Add(new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(14, 10, 14, 12), Child = box });
        }
        return new Border { Child = tiles, Margin = new Thickness(0, 0, 0, 4) };
    }

    Border GameDataCard(PcgwInfo wiki, PcgwSection section, Entry entry, List<Entry> subs)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(SectionHeading(entry));
        panel.Children.Add(Ui.Muted(L.T("Where settings and saves are kept")));
        foreach (var (kind, paths) in new[] { (L.T("Settings"), wiki.ConfigPaths), (L.T("Saves"), wiki.SavePaths) })
        {
            if (paths.Count == 0) continue;
            panel.Children.Add(new TextBlock { Text = kind.ToUpper(), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, 4, 0, 0) });
            foreach (var p in paths.DistinctBy(p => p.Path.ToLowerInvariant()))
            {
                var tb = new TextBlock { Text = p.Path, Style = Ui.Style("RowDescription"), IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                var platform = new TextBlock { Text = p.Platform, FontSize = 11, Opacity = 0.7 };
                var text = new StackPanel();
                text.Children.Add(platform);
                text.Children.Add(tb);
                var open = new Button { Content = L.T("Open"), Padding = new Thickness(10, 3, 10, 4), IsEnabled = false };
                var path = p;
                open.Click += (_, _) => Ui.Safe(() =>
                {
                    if (Pcgw.ExistingFolder([path], _data.Game.Store) is { } dir) GameLauncher.OpenFolder(dir);
                    else App.Window?.ShowInfo(L.T("That folder is not on this PC (yet)."));
                });
                // Checked off the UI thread: some paths walk a few folders.
                _ = Task.Run(() => Pcgw.ExistingFolder([path], _data.Game.Store)).ContinueWith(t =>
                    DispatcherQueue.TryEnqueue(() => open.IsEnabled = t.IsCompletedSuccessfully && t.Result is not null));
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(text);
                Grid.SetColumn(open, 1);
                open.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(open);
                panel.Children.Add(new Border { Style = Ui.Style("RowStyle"), MinHeight = 0, Padding = new Thickness(12, 8, 12, 8), Child = row });
            }
        }
        // The rest of the wiki's section (cloud sync and the like) in the same card: the paths above already list its tables.
        var blocks = Blocks(section, skipGameDataTables: true);
        // The sub-headings of the tables shown as paths above have nothing left under them: their contents lines go to the card.
        var shownSubs = subs.Where(s => blocks.Any(b => b.Kind == PcgwBlockKind.Heading && b.Title == s.Title)).ToList();
        AddBlocks(panel, blocks, shownSubs);
        var card = Ui.Card(panel, 20);
        entry.Target = card;
        foreach (var s in subs.Except(shownSubs)) s.Target = card;
        return card;
    }
}

// ======================================================================================================================

/// <summary>Tiles of one width in as many columns as fit (each at least <c>minWidth</c>), filling the row edge to edge.</summary>
sealed partial class TileGrid : Panel
{
    readonly double _minWidth, _spacing;

    public TileGrid(double minWidth, double spacing = 10)
    {
        _minWidth = minWidth;
        _spacing = spacing;
    }

    (int Columns, double Width) Columns(double available)
    {
        if (double.IsInfinity(available) || available <= 0) return (1, _minWidth);
        // Never more columns than tiles: a short row stretches across instead of leaving a gap on the right.
        int columns = Math.Clamp((int)((available + _spacing) / (_minWidth + _spacing)), 1, Math.Max(1, Children.Count));
        return (columns, (available - _spacing * (columns - 1)) / columns);
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        var (columns, width) = Columns(available.Width);
        double y = 0, row = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            row = Math.Max(row, Children[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == Children.Count - 1) { y += row + _spacing; row = 0; }
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? width : available.Width, Math.Max(0, y - _spacing));
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        var (columns, width) = Columns(final.Width);
        double y = 0;
        for (int start = 0; start < Children.Count; start += columns)
        {
            // Every tile of a row gets the row's height, so the boxes line up.
            double row = 0;
            int end = Math.Min(start + columns, Children.Count);
            for (int i = start; i < end; i++) row = Math.Max(row, Children[i].DesiredSize.Height);
            for (int i = start; i < end; i++)
                Children[i].Arrange(new Windows.Foundation.Rect((i - start) * (width + _spacing), y, width, row));
            y += row + _spacing;
        }
        return final;
    }
}

/// <summary>
/// The "Links" tab: every useful page for the game in one place, by purpose (store and community, mods and fixes,
/// reviews and guides, OptiScaler). Each tile shows the site's icon, the page and its address.
/// </summary>
public sealed partial class LinksView : UserControl
{
    readonly GameData _data;
    readonly StackPanel _root = new() { Spacing = 12 };

    sealed record Link(string Title, string Description, Func<Task<string?>> Url, string Host, string Glyph);

    public LinksView(GameData data)
    {
        _data = data;
        Content = _root;
        _root.Children.Add(Ui.Card(Ui.Loading()));
        _ = FillAsync();
    }

    static Link Fixed(string title, string description, string url, string glyph) =>
        new(title, description, () => Task.FromResult<string?>(url), Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url, glyph);

    async Task FillAsync()
    {
        GameArt? art = null;
        StoreDetails? store = null;
        try { art = await _data.Art; } catch (Exception ex) { Log.Warn($"Links art: {ex.Message}"); }
        try { store = await _data.Store; } catch (Exception ex) { Log.Warn($"Links store: {ex.Message}"); }
        if (_data.Token.IsCancellationRequested) return;
        _root.Children.Clear();

        var game = _data.Game;
        var id = art?.SteamAppId ?? game.SteamAppId;
        var q = Uri.EscapeDataString(_data.SearchName);
        var ct = _data.Token;

        var storeLinks = new List<Link>();
        if (game.Store != GameStore.Steam && StoreLinks.For(game, id) is { } own && own.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            storeLinks.Add(Fixed(StoreLogos.Label(game.Store), L.T(own.What), own.Uri, ""));
        if (id is not null)
        {
            storeLinks.Add(Fixed(L.T("Steam store page"), L.T("Price, reviews, DLC and updates"), store?.StoreUrl ?? $"https://store.steampowered.com/app/{id}", ""));
            storeLinks.Add(Fixed(L.T("Steam community hub"), L.T("Screenshots, artwork, videos and news"), $"https://steamcommunity.com/app/{id}", ""));
            storeLinks.Add(Fixed(L.T("Steam discussions"), L.T("The game's forums: bugs, fixes, questions"), $"https://steamcommunity.com/app/{id}/discussions/", ""));
            storeLinks.Add(Fixed(L.T("Steam guides"), L.T("Walkthroughs, achievement and settings guides"), $"https://steamcommunity.com/app/{id}/guides/", ""));
            storeLinks.Add(Fixed("SteamDB", L.T("Depots, patch history, player counts and prices"), $"https://steamdb.info/app/{id}/", ""));
        }
        if (store?.Website is { Length: > 0 } site && Uri.TryCreate(site, UriKind.Absolute, out _))
            storeLinks.Add(Fixed(L.T("Official website"), L.T("The publisher's page for the game"), site, ""));

        var mods = new List<Link>
        {
            new("Nexus Mods", L.T("Mods for this game"), async () => await NexusMods.PageForAsync(_data.SearchName, ct), "nexusmods.com", ""),
            Fixed("PCGamingWiki", L.T("Fixes, settings, file locations"), GameInfo.PcGamingWikiUrl(game, id), ""),
            new(L.T("OptiScaler wiki"), L.T("Tested OptiScaler setup for this game"), async () => await CompatWiki.PageForAsync(_data.SearchName, ct), "github.com", ""),
            Fixed("ModDB", L.T("Mods, patches and add-ons"), $"https://www.moddb.com/games?filter=t&kw={q}", ""),
        };

        var info = new List<Link>
        {
            Fixed("HowLongToBeat", L.T("How long the story and 100% take"), $"https://howlongtobeat.com/?q={q}", ""),
            Fixed("Metacritic", L.T("Critic and user scores"), store?.MetacriticUrl ?? $"https://www.metacritic.com/search/{q}/", ""),
            Fixed(L.T("YouTube: PC performance"), L.T("Benchmarks and best settings videos"), $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(_data.SearchName + " PC performance")}", ""),
            Fixed("Reddit", L.T("Discussions, tips and news"), $"https://www.reddit.com/search/?q={q}", ""),
        };
        if (id is not null) info.Add(Fixed("ProtonDB", L.T("How it runs on Linux and Steam Deck"), $"https://www.protondb.com/app/{id}", ""));

        var opti = new List<Link>
        {
            Fixed(L.T("OptiScaler on GitHub"), L.T("Releases, source and issues"), "https://github.com/optiscaler/OptiScaler", ""),
            Fixed(L.T("OptiScaler releases"), L.T("Every stable and nightly build"), "https://github.com/optiscaler/OptiScaler/releases", ""),
            Fixed(L.T("Compatibility List"), L.T("Every game tested with OptiScaler"), CompatWiki.ListUrl, ""),
        };

        if (storeLinks.Count > 0) _root.Children.Add(Section("", L.T("Store and community"), L.T("The game's pages on its store and on Steam"), storeLinks, Ui.Blue));
        _root.Children.Add(Section("", L.T("Mods and fixes"), L.T("Mods, community fixes and setup notes"), mods, Ui.Orange));
        _root.Children.Add(Section("", L.T("Reviews, guides and performance"), L.T("What others say and how it runs"), info, Ui.Green));
        _root.Children.Add(Section("", "OptiScaler", L.T("The project behind the mod Hybrid Launcher installs"), opti, Ui.Pink));
    }

    Border Section(string glyph, string title, string subtitle, List<Link> links, Color tint)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Ui.Heading(glyph, title, subtitle: subtitle, tint: tint));
        var grid = new TileGrid(300);
        foreach (var l in links) grid.Children.Add(Tile(l, tint));
        panel.Children.Add(grid);
        return Ui.Card(panel);
    }

    static Button Tile(Link link, Color tint)
    {
        // The site's own icon (from its domain) when it loads, the glyph badge otherwise.
        var badge = Ui.Badge(link.Glyph, tint, 40);
        var icon = new Image { Width = 22, Height = 22, Visibility = Visibility.Collapsed };
        var bitmap = new BitmapImage { DecodePixelWidth = 44 };
        bitmap.ImageOpened += (_, _) =>
        {
            icon.Visibility = Visibility.Visible;
            if (badge.Child is FontIcon f) f.Visibility = Visibility.Collapsed;
        };
        bitmap.UriSource = new Uri($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(link.Host)}&sz=64");
        icon.Source = bitmap;
        var iconHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(badge);
        iconHost.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = link.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = link.Description, Style = Ui.Style("RowDescription"), MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
        text.Children.Add(new TextBlock { Text = link.Host, FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(iconHost);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var open = new FontIcon { Glyph = "", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
        };
        ToolTipService.SetToolTip(button, link.Host);
        button.Click += async (_, _) =>
        {
            try { Ui.Open(await link.Url()); }
            catch (Exception ex) { App.Window?.ShowError(L.F("Could not open {0}", link.Title), ex.Message); }
        };
        return button;
    }
}

// ======================================================================================================================

/// <summary>
/// The OptiScaler tab's Release notes: what each stable or nightly release changed, from GitHub, newest first. The channel of
/// the version installed in this game opens first and its release is marked; the newest notes are open, the older folded.
/// </summary>
public sealed partial class OptiNotesView : UserControl
{
    readonly Func<InstallManifest?> _installed;
    readonly StackPanel _list = new() { Spacing = 10 };
    readonly SelectorBar _bar = new();
    OptiChannel _channel;
    int _filling;

    public OptiNotesView(Func<InstallManifest?> installed)
    {
        _installed = installed;
        _channel = installed()?.Channel ?? OptiChannel.Stable;
        foreach (var c in new[] { OptiChannel.Stable, OptiChannel.Nightly })
            _bar.Items.Add(new SelectorBarItem { Text = L.T(c.ToString()), Tag = c, IsSelected = c == _channel });
        _bar.SelectionChanged += (_, _) =>
        {
            if (_bar.SelectedItem?.Tag is OptiChannel c && c != _channel) { _channel = c; _ = FillAsync(); }
        };
        var head = new StackPanel { Spacing = 10 };
        head.Children.Add(Ui.Heading("", L.T("Release notes"), Ui.LinkButton(L.T("All releases"), "", () => Ui.Open(OptiReleases.ReleasesPage(_channel))),
            L.T("What each OptiScaler version changed, from GitHub"), Ui.Teal));
        head.Children.Add(_bar);
        head.Children.Add(_list);
        Content = Ui.Card(head);
        _ = FillAsync();
    }

    async Task FillAsync()
    {
        int run = ++_filling;
        _list.Children.Clear();
        _list.Children.Add(Ui.Loading());
        List<OptiRelease> releases;
        try { releases = await OptiReleases.GetAsync(_channel); }
        catch (Exception ex)
        {
            if (run != _filling) return;
            _list.Children.Clear();
            _list.Children.Add(Ui.Muted(ex.Message));
            return;
        }
        if (run != _filling) return;
        _list.Children.Clear();
        var installed = _installed();
        int shown = 0;
        foreach (var r in releases.Take(12))
        {
            bool mine = installed is not null && installed.Channel == r.Channel && installed.Tag == r.Tag;
            _list.Children.Add(Release(r, mine, open: shown++ < 2 || mine));
        }
        if (releases.Count == 0) _list.Children.Add(Ui.Muted(L.T("GitHub lists no releases for this channel.")));
    }

    static FrameworkElement Release(OptiRelease r, bool installed, bool open)
    {
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4) };
        title.Children.Add(new TextBlock { Text = r.Title.Length > 0 ? r.Title : r.Tag, FontSize = 19, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        title.Children.Add(new TextBlock { Text = $"{r.Tag}  ·  {r.Published.ToLocalTime():d MMM yyyy}", Style = Ui.Style("RowDescription"), FontSize = 13 });
        head.Children.Add(title);
        if (installed)
        {
            var pill = Ui.Pill(L.T("Installed"), Ui.Tone.Good);
            pill.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(pill, 1);
            head.Children.Add(pill);
        }
        var body = new StackPanel { Spacing = 10, Padding = new Thickness(4, 2, 4, 4) };
        var (notes, commits) = Notes(r.Notes);
        if (commits > 0) title.Children.Add(new TextBlock { Text = L.N(commits, "{0} change", "{0} changes"), FontSize = 12, Foreground = new SolidColorBrush(Ui.Teal) });
        body.Children.Add(notes ?? Ui.Muted(L.T("No notes for this release.")));
        if (r.PageUrl.Length > 0)
        {
            var link = Ui.LinkButton(L.T("Open on GitHub"), "", () => Ui.Open(r.PageUrl));
            link.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(link);
        }
        return new Expander
        {
            Header = head,
            Content = body,
            IsExpanded = open,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[-*]\s+\[`?([0-9a-fA-F]{7,40})`?\]\((https?://[^)\s]+)\)\s+(.*?)\s*(?:\(([^()]+)\))?\s*$")]
    private static partial System.Text.RegularExpressions.Regex CommitLine();

    /// <summary>
    /// A release's notes as GitHub shows them, easier to read. Nightly notes are a list of commits ("- [b19cfd29](url) What
    /// changed (Author)", details quoted under it): each becomes a row with the short hash as a link in monospace, the change in
    /// larger text and its author as a coloured tag; the quoted details sit in a quote block under it, their hard line breaks
    /// joined back into sentences. Anything else (stable notes, headings) is the Markdown as before. Returns the view and the
    /// number of commits.
    /// </summary>
    static (FrameworkElement? View, int Commits) Notes(string markdown)
    {
        var panel = new StackPanel { Spacing = 6 };
        var plain = new System.Text.StringBuilder();
        int commits = 0;
        StackPanel? detailOf = null;
        var detail = new List<string>();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            var lines = MarkdownText.ToLines(plain.ToString());
            if (lines.Count > 0) panel.Children.Add(Ui.Rich(lines, spacing: 5, large: true, headingTint: Ui.Teal));
            plain.Clear();
        }
        void FlushDetail()
        {
            if (detailOf is null || detail.Count == 0) { detail.Clear(); return; }
            // Hard-wrapped quote lines are one sentence: joined, unless a line starts a list item.
            var joined = new List<string>();
            foreach (var d in detail)
            {
                bool item = System.Text.RegularExpressions.Regex.IsMatch(d, @"^\s*([-*+]|\d+[.)])\s");
                if (joined.Count == 0 || item || d.Trim().Length == 0) joined.Add(d);
                else joined[^1] = joined[^1].TrimEnd() + " " + d.Trim();
            }
            var lines = MarkdownText.ToLines(string.Join("\n", joined));
            if (lines.Count > 0)
            {
                var rich = Ui.Rich(lines, spacing: 4);
                rich.Opacity = 0.8;
                detailOf.Children.Add(new Border
                {
                    Child = rich, Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(84, 4, 0, 2),
                    BorderBrush = Ui.Brush("ControlStrongStrokeColorDefaultBrush"), BorderThickness = new Thickness(3, 0, 0, 0),
                });
            }
            detail.Clear();
            detailOf = null;
        }

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            if (CommitLine().Match(raw) is { Success: true } m)
            {
                FlushDetail();
                FlushPlain();
                commits++;
                panel.Children.Add(detailOf = CommitRow(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Success ? m.Groups[4].Value.Trim() : null, commits));
                continue;
            }
            if (detailOf is not null && System.Text.RegularExpressions.Regex.Match(raw, @"^\s*>\s?(.*)$") is { Success: true } q)
            {
                detail.Add(q.Groups[1].Value);
                continue;
            }
            if (detailOf is not null && raw.Trim().Length == 0) continue;
            FlushDetail();
            plain.AppendLine(raw);
        }
        FlushDetail();
        FlushPlain();
        return (panel.Children.Count > 0 ? panel : null, commits);
    }

    /// <summary>One commit of a nightly's notes: hash (a link to the commit), what changed, who changed it.</summary>
    static StackPanel CommitRow(string hash, string url, string message, string? author, int n)
    {
        var row = new StackPanel { Spacing = 0, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6) };
        if (n % 2 == 1) row.Background = Ui.Brush("SubtleFillColorSecondaryBrush");
        var line = new Grid { ColumnSpacing = 12 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var link = new HyperlinkButton
        {
            Content = new TextBlock { Text = hash.Length > 8 ? hash[..8] : hash, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 },
            Padding = new Thickness(6, 2, 6, 3), VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(link, L.T("Open the commit on GitHub"));
        link.Click += (_, _) => Ui.Open(url);
        line.Children.Add(link);
        var text = MarkdownText.ToLines(message).FirstOrDefault() is { } ml ? Ui.LineBlock(ml) : new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.FontSize = 15;
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        if (author is { Length: > 0 and < 40 })
        {
            // One colour per author, the same in every release: who did what reads at a glance.
            Color[] colors = [Ui.Teal, Ui.Pink, Ui.Gold, Ui.Blue, Ui.Green, Ui.Orange];
            var c = colors[(int)((uint)author.Aggregate(17, (h, ch) => h * 31 + char.ToLowerInvariant(ch)) % (uint)colors.Length)];
            var tag = new Border
            {
                Child = new TextBlock { Text = author, FontSize = 12, Foreground = new SolidColorBrush(c) },
                Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B)),
            };
            Grid.SetColumn(tag, 2);
            line.Children.Add(tag);
        }
        row.Children.Add(line);
        return row;
    }

    /// <summary>
    /// The preview in the OptiScaler tab's side column: the newest stable and nightly release with their date, the first lines of
    /// the newest notes, and the way to the Release notes section.
    /// </summary>
    public static FrameworkElement Preview(Action open)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Ui.Heading("", L.T("Release notes"), Ui.LinkButton(L.T("Open"), "", open), tint: Ui.Teal));
        var rows = new StackPanel { Spacing = 6 };
        rows.Children.Add(Ui.Loading());
        panel.Children.Add(rows);
        _ = FillPreviewAsync(rows);
        return Ui.Card(panel);
    }

    static async Task FillPreviewAsync(StackPanel rows)
    {
        var found = new List<OptiRelease>();
        foreach (var c in new[] { OptiChannel.Stable, OptiChannel.Nightly })
        {
            try { if ((await OptiReleases.GetAsync(c)).FirstOrDefault() is { } r) found.Add(r); }
            catch (Exception ex) { Log.Warn($"Release notes preview ({c}): {ex.Message}"); }
        }
        rows.Children.Clear();
        if (found.Count == 0)
        {
            rows.Children.Add(Ui.Muted(L.T("GitHub could not be reached.")));
            return;
        }
        foreach (var r in found)
        {
            var line = new StackPanel();
            line.Children.Add(new TextBlock { Text = $"{L.T(r.Channel.ToString())}  ·  {r.Tag}", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            line.Children.Add(new TextBlock { Text = r.Published.ToLocalTime().ToString("d MMM yyyy"), Style = Ui.Style("RowDescription"), FontSize = 12 });
            var first = MarkdownText.ToLines(r.Notes).Where(l => !l.Heading).Take(2).Select(l => l.Text).ToList();
            if (first.Count > 0)
                line.Children.Add(new TextBlock { Text = string.Join(" ", first), Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
            rows.Children.Add(line);
        }
    }
}
