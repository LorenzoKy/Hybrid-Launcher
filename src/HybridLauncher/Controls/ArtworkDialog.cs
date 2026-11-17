using HybridLauncher.Core;
using HybridLauncher.Core.Media;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HybridLauncher.Controls;

/// <summary>
/// "Edit artwork". 2.3: a window almost as large as the app's. On the left the game's four pictures (cover, banner, logo,
/// icon) with the one being edited shown large, where it comes from and the buttons; on the right SteamGridDB's community
/// pictures in a grid that fills the rest and scrolls on its own to the end, the next page asked for well before the end.
/// A click on a picture makes it the game's at once (a copy in the data folder); a picture of the user's own works too, and
/// "Use the store's" goes back. The game is matched on SteamGridDB by its Steam id or its name; the search finds another.
/// SteamGridDB needs the user's own free API key: the window asks for it once.
///
/// Pictures load fast (2.3): only the rows in view and the next ones ask for their thumbnail, through the app's own
/// downloader (<see cref="SteamGridDb.ThumbAsync"/>: a few at a time, kept on disk for a month), decoded at the tile's size
/// and kept in memory for the session, so scrolling back or opening the window again shows them at once.
/// </summary>
public static class ArtworkDialog
{
    /// <summary>Self-test: whether the open dialog's picture grid ends inside what the dialog shows (null: no dialog yet).</summary>
    public static Func<bool>? GridFits { get; private set; }
    /// <summary>Self-test: the numbers behind the last <see cref="GridFits"/>.</summary>
    public static string GridFitsDetail { get; private set; } = "";
    /// <summary>Self-test: scrolls the open dialog's grid to this share of its length (the soft edges show in the picture).</summary>
    public static Action<double>? ScrollForTest { get; private set; }

    sealed record KindInfo(ArtKind Kind, string Name, string Hint, string Glyph, double TileW, double TileH, Stretch Stretch);

    static readonly KindInfo[] Kinds =
    [
        new(ArtKind.Cover, "Cover", "The library tile (portrait, 2:3)", "", 140, 210, Stretch.UniformToFill),
        new(ArtKind.Banner, "Banner", "The wide picture at the top of the game's page", "", 300, 97, Stretch.UniformToFill),
        new(ArtKind.Logo, "Logo", "The title drawn on the banner", "", 196, 98, Stretch.Uniform),
        new(ArtKind.Icon, "Icon", "The small picture in the game list", "", 84, 84, Stretch.Uniform),
    ];

    /// <summary>One picture of the grid: its image, filled only once its row comes near the view.</summary>
    sealed class Slot(Image image, string thumb)
    {
        public Image Image { get; } = image;
        public string Thumb { get; } = thumb;
        public bool Requested;
    }

    // Thumbnails decoded this session, by address and size: a tab or the window opened again shows them in the first frame.
    static readonly Dictionary<string, BitmapImage> Decoded = new(StringComparer.Ordinal);
    static readonly Queue<string> DecodedOrder = new();
    const int DecodedMax = 600;

    static void Remember(string key, BitmapImage image)
    {
        if (Decoded.ContainsKey(key)) return;
        Decoded[key] = image;
        DecodedOrder.Enqueue(key);
        while (DecodedOrder.Count > DecodedMax) Decoded.Remove(DecodedOrder.Dequeue());
    }

    const double Gap = 12, PageSize = 50, PreviewHeight = 128, FadeTop = 36, FadeBottom = 52;

    public static async Task ShowAsync(XamlRoot root, ElementTheme theme, Game game, ArtKind start = ArtKind.Cover)
    {
        // 2.3, after Hydra's: one column, as wide as a comfortable read and no wider: the pictures' tabs, the picture in use
        // (click it to choose a file of your own, or drop one on it; your own one clicked goes back to the store's), then
        // SteamGridDB's pictures filling the rest of the height.
        // 2.3-rc.2: larger (it was at most 1040 x 900 with wide margins round it): up to 1560 x 1280.
        double width = Math.Clamp(root.Size.Width - 96, 600, 1560);
        double height = Math.Clamp(root.Size.Height - 48, 480, 1280);
        double scale = root.RasterizationScale > 0 ? root.RasterizationScale : 1;
        var cts = new CancellationTokenSource();
        var kind = Kinds.First(k => k.Kind == start);
        SgdbGame? match = null;
        int page = 0;
        bool hasMore = false;
        string? steamAppId = game.SteamAppId;
        ContentDialog? dialog = null;

        var accent = Ui.Brush("AccentFillColorDefaultBrush");
        var accentColor = (accent as SolidColorBrush)?.Color ?? Windows.UI.Color.FromArgb(0xFF, 0xF0, 0x5A, 0x64);
        var accentSoft = new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, accentColor.R, accentColor.G, accentColor.B));
        var onAccent = Ui.Brush("TextOnAccentFillColorPrimaryBrush");
        var muted = Ui.Brush("TextFillColorSecondaryBrush");
        var subtle = Ui.Brush("SubtleFillColorSecondaryBrush");
        var card = Ui.Brush("CardBackgroundFillColorDefaultBrush");
        var floor = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x16, 0x16, 0x1B));
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // No padding at the foot (2.3-rc.2): the grid runs to the window's bottom edge and fades out there, no empty band under it.
        var rootGrid = new Grid { Width = width, Height = height, Padding = new Thickness(24, 20, 8, 0), RowSpacing = 14 };
        for (int i = 0; i < 7; i++) rootGrid.RowDefinitions.Add(new RowDefinition { Height = i == 6 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Place(FrameworkElement e, int row, Thickness? margin = null)
        {
            Grid.SetRow(e, row);
            e.Margin = margin ?? new Thickness(0, 0, 16, 0);
            rootGrid.Children.Add(e);
        }

        // Row 0: the title and Done.
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = L.T("Artwork").ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80, Foreground = muted });
        titles.Children.Add(new TextBlock { Text = game.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(titles);
        var done = new Button { Content = L.T("Done"), Style = Ui.Style("AccentButtonStyle"), MinWidth = 96, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(done, 1);
        head.Children.Add(done);
        Place(head, 0);

        // Row 1: the four pictures as tabs, each saying where its picture comes from.
        var tabs = new Grid { ColumnSpacing = 8 };
        var kindRows = new List<(KindInfo Info, Button Button, Border GlyphBox, FontIcon Glyph, TextBlock Source)>();
        foreach (var k in Kinds)
        {
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var glyph = new FontIcon { Glyph = k.Glyph, FontSize = 14 };
            var glyphBox = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = subtle, Child = glyph };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = L.T(k.Name), FontWeight = FontWeights.SemiBold });
            var source = new TextBlock { FontSize = 11, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Children.Add(source);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(glyphBox);
            row.Children.Add(text);
            var button = new Button
            {
                Content = row, Tag = k.Kind, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 7, 10, 7), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(0), Background = card,
            };
            Grid.SetColumn(button, tabs.Children.Count);
            tabs.Children.Add(button);
            kindRows.Add((k, button, glyphBox, glyph, source));
        }
        Place(tabs, 1);

        // Row 2: the picture in use. A click on it chooses a file (or, for your own picture, goes back to the store's); a picture
        // dropped on it is used as it is.
        var nowPic = new Border { CornerRadius = new CornerRadius(10), Background = floor, Height = PreviewHeight };
        var overlayIcon = new FontIcon { Glyph = "", FontSize = 18, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        var overlayText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var overlayStack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        overlayStack.Children.Add(overlayIcon);
        overlayStack.Children.Add(overlayText);
        var overlay = new Border
        {
            CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xA8, 0x0C, 0x0C, 0x10)), Opacity = 0, IsHitTestVisible = false,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) }, Child = overlayStack,
        };
        var dropZone = new Grid();
        dropZone.Children.Add(nowPic);
        dropZone.Children.Add(overlay);
        var pickButton = new Button { Content = dropZone, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = clear, CornerRadius = new CornerRadius(10), AllowDrop = true, VerticalAlignment = VerticalAlignment.Top };
        pickButton.PointerEntered += (_, _) => overlay.Opacity = 1;
        pickButton.PointerExited += (_, _) => overlay.Opacity = 0;
        pickButton.GotFocus += (_, _) => { if (pickButton.FocusState == FocusState.Keyboard) overlay.Opacity = 1; };
        pickButton.LostFocus += (_, _) => overlay.Opacity = 0;

        var nowSourceText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        var nowSource = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), HorizontalAlignment = HorizontalAlignment.Left, Child = nowSourceText };
        var nowHint = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var own = new Button { Content = L.T("Choose a file…"), Style = Ui.Style("AccentButtonStyle") };
        var reset = new Button { Content = L.T("Use the store's") };
        var nowButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        nowButtons.Children.Add(own);
        nowButtons.Children.Add(reset);
        var nowText = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        nowText.Children.Add(nowSource);
        nowText.Children.Add(nowHint);
        nowText.Children.Add(nowButtons);
        nowText.Children.Add(new TextBlock { Text = L.T("Or drop a picture on the preview."), FontSize = 12, Foreground = muted });
        var nowRow = new Grid { ColumnSpacing = 20, Padding = new Thickness(14), CornerRadius = new CornerRadius(12), Background = card };
        nowRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nowRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nowRow.Children.Add(pickButton);
        Grid.SetColumn(nowText, 1);
        nowRow.Children.Add(nowText);
        Place(nowRow, 2);

        // Row 3: SteamGridDB, the game it was matched to, the credit, the key and a search for another (2.3-rc.2: the credit and
        // the key had a row of their own under the grid, an empty band at the foot of the window).
        var sgdbHead = new Grid { ColumnSpacing = 8 };
        sgdbHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sgdbHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sgdbHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Min(300, width * 0.36)) });
        var title = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = L.T("From SteamGridDB"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        var matchText = new TextBlock { Foreground = muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Children.Add(matchText);
        title.Children.Add(new TextBlock { Text = L.T("Pictures by the SteamGridDB community."), FontSize = 11, Foreground = muted, Opacity = 0.8 });
        sgdbHead.Children.Add(title);
        var changeKey = new Button
        {
            Content = new FontIcon { Glyph = "\uE8D7", FontSize = 14 }, Padding = new Thickness(9, 8, 9, 8), Background = clear, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(changeKey, L.T("Change the SteamGridDB key"));
        Grid.SetColumn(changeKey, 1);
        sgdbHead.Children.Add(changeKey);
        var search = new AutoSuggestBox { PlaceholderText = L.T("Search another game"), QueryIcon = new SymbolIcon(Symbol.Find), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(search, L.T("Not this game? Search SteamGridDB"));
        Grid.SetColumn(search, 2);
        sgdbHead.Children.Add(search);
        Place(sgdbHead, 3, new Thickness(0, 6, 16, 0));

        // Row 4: the key, asked for once.
        var keyBox = new PasswordBox { PlaceholderText = L.T("Paste your SteamGridDB API key"), Width = Math.Min(340, width * 0.45) };
        var keySave = new Button { Content = L.T("Save key"), Style = Ui.Style("AccentButtonStyle") };
        var keyLink = new HyperlinkButton { Content = L.T("Get a free key"), NavigateUri = new Uri(SteamGridDb.KeyPage) };
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keyRow.Children.Add(keyBox);
        keyRow.Children.Add(keySave);
        keyRow.Children.Add(keyLink);
        var keyPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        keyPanel.Children.Add(new TextBlock
        {
            Text = L.T("SteamGridDB shares thousands of covers, banners, logos and icons made by its community. It asks for a free API key of your own: sign in on steamgriddb.com, open Preferences → API, and paste the key here. It stays on this PC."),
            TextWrapping = TextWrapping.Wrap, Style = Ui.Style("RowDescription"),
        });
        keyPanel.Children.Add(keyRow);
        Place(keyPanel, 4);

        // Row 5: a message (no key, nothing found, an error).
        var status = new TextBlock { Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Place(status, 5);

        // Row 6: the grid, as many columns as fit, each tile as wide as the row allows. Below it, while SteamGridDB has more, a row
        // that stays the same height whether the next page is coming or not: the grid only ever grows under the view (2.3: the
        // last rows were cut and scrolling jumped as placeholders came and went at the end).
        double gridWidth = width - 24 - 8 - 16 - 2;
        var wrap = new WrapPanel(Gap, []);
        var loader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Height = 64, Visibility = Visibility.Collapsed, Padding = new Thickness(0, 20, 0, 0) };
        loader.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
        loader.Children.Add(new TextBlock { Text = L.T("Loading more pictures…"), Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
        // Room at the top and the foot of the content (2.3-rc.2): at rest the first row starts clear of the top fade and the last
        // one ends clear of the bottom fade; in between, rows pass softly under both instead of being cut by a hard edge.
        var gridPanel = new StackPanel { Margin = new Thickness(0, FadeTop * 0.5, 16, FadeBottom) };
        gridPanel.Children.Add(wrap);
        gridPanel.Children.Add(loader);
        // A ScrollView moved by the compositor (2.3-rc.2), as the pages are: the wheel glides at the display's refresh even
        // while tiles are added or pictures decode on the UI thread (the ScrollViewer stepped from the UI thread and hitched).
        var scroller = new ScrollView
        {
            Content = gridPanel, HorizontalScrollMode = ScrollingScrollMode.Disabled, ContentOrientation = ScrollingContentOrientation.Vertical,
            VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto,
        };
        CompositorWheel.Attach(scroller);
        // The soft edges: the window's own colour fading into the grid, shown only where there is more to scroll to.
        var fadeTop = new Border { Height = FadeTop, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Opacity = 0, Margin = new Thickness(0, 0, 16, 0), OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) } };
        var fadeBottom = new Border { Height = FadeBottom, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Opacity = 0, Margin = new Thickness(0, 0, 16, 0), OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) } };
        var gridCell = new Grid();
        gridCell.Children.Add(scroller);
        gridCell.Children.Add(fadeTop);
        gridCell.Children.Add(fadeBottom);
        Place(gridCell, 6, new Thickness(0, -10, 0, 0));
        void PaintFades(Windows.UI.Color c)
        {
            LinearGradientBrush Fade(bool down)
            {
                var b = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
                // Eased (more stops than two): a straight ramp shows a faint line where it ends.
                for (int i = 0; i <= 6; i++)
                {
                    double t = i / 6.0, a = Math.Pow(1 - t, 2.2);
                    b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)Math.Round(c.A * a), c.R, c.G, c.B), Offset = down ? t : 1 - t });
                }
                return b;
            }
            fadeTop.Background = Fade(true);
            fadeBottom.Background = Fade(false);
        }
        void UpdateFades()
        {
            fadeTop.Opacity = scroller.VerticalOffset > 2 ? 1 : 0;
            fadeBottom.Opacity = scroller.ScrollableHeight - scroller.VerticalOffset > 2 ? 1 : 0;
        }

        bool loading = false;
        int run = 0;
        var slots = new List<Slot>();
        var skeletons = new List<FrameworkElement>();

        (double W, double H) TileSize()
        {
            int cols = Math.Max(1, (int)((gridWidth + Gap) / (kind.TileW + Gap)));
            double w = Math.Floor((gridWidth - Gap * (cols - 1)) / cols);
            return (w, Math.Round(w * kind.TileH / kind.TileW));
        }
        int Columns() => Math.Max(1, (int)((gridWidth + Gap) / (TileSize().W + Gap)));

        void Status(string? text)
        {
            status.Text = text ?? "";
            status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // A thumbnail for a tile: from this session's memory at once, else the disk copy or a download, then decoded at the
        // tile's size off the UI thread (BitmapImage decodes on its own).
        async void Fill(Slot slot, double tileWidth)
        {
            slot.Requested = true;
            int px = (int)Math.Ceiling(tileWidth * scale);
            var key = slot.Thumb + "|" + px;
            if (Decoded.TryGetValue(key, out var ready))
            {
                slot.Image.Source = ready;
                slot.Image.Opacity = 1;
                return;
            }
            string? file = null;
            try { file = await Task.Run(() => SteamGridDb.ThumbAsync(slot.Thumb, cts.Token)); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Info($"SteamGridDB thumbnail: {ex.Message}"); }
            if (cts.IsCancellationRequested) return;
            var bitmap = new BitmapImage { DecodePixelWidth = px };
            bitmap.ImageOpened += (_, _) =>
            {
                Remember(key, bitmap);
                Motion.FadeIn(slot.Image, 160);
            };
            try { bitmap.UriSource = new Uri(file ?? slot.Thumb); }
            catch (UriFormatException) { return; }
            slot.Image.Source = bitmap;
        }

        // The rows in view, one above and two screens below ask for their pictures (in that order); the rest wait.
        void Realize()
        {
            if (slots.Count == 0) return;
            var (tw, th) = TileSize();
            int cols = Columns();
            double rowH = th + Gap;
            double top = scroller.VerticalOffset - rowH;
            double bottom = scroller.VerticalOffset + Math.Max(scroller.ViewportHeight, 400) * 2;
            int first = Math.Max(0, (int)Math.Floor(top / rowH)) * cols;
            int last = Math.Min(slots.Count - 1, ((int)Math.Ceiling(bottom / rowH) + 1) * cols - 1);
            for (int i = first; i <= last; i++) if (!slots[i].Requested) Fill(slots[i], tw);
        }

        scroller.ViewChanged += (_, _) =>
        {
            Realize();
            UpdateFades();
            // The next page is asked for a screen and a half before the end, so it is there when the view gets there.
            if (hasMore && !loading && scroller.VerticalOffset > scroller.ScrollableHeight - scroller.ViewportHeight * 1.5) _ = LoadAsync(true);
        };
        scroller.SizeChanged += (_, _) => { Realize(); UpdateFades(); };
        gridPanel.SizeChanged += (_, _) => UpdateFades();

        (FrameworkElement Tile, Slot Slot) Tile(SgdbAsset asset)
        {
            var (tw, th) = TileSize();
            var image = new Image { Stretch = kind.Stretch, Opacity = 0, Margin = kind.Stretch == Stretch.Uniform ? new Thickness(12) : new Thickness(0) };
            var busy = new ProgressRing { IsActive = false, Width = 24, Height = 24 };
            var radius = new CornerRadius(kind.Kind == ArtKind.Icon ? 16 : 10);
            var face = new Grid { Width = tw, Height = th, CornerRadius = radius, Background = floor };
            face.Children.Add(image);
            face.Children.Add(busy);
            var wash = new Border { CornerRadius = radius, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Opacity = 0, IsHitTestVisible = false, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) } };
            var picked = new Grid { Visibility = Visibility.Collapsed, IsHitTestVisible = false, Tag = "picked" };
            picked.Children.Add(new Border { CornerRadius = radius, BorderBrush = accent, BorderThickness = new Thickness(3) });
            var check = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = accent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
            check.Child = new FontIcon { Glyph = "", FontSize = 13, Foreground = onAccent };
            picked.Children.Add(check);
            face.Children.Add(wash);
            // Who made it and its size, over the foot of the picture while the pointer is on it.
            UIElement? caption = null;
            if (th >= 80)
            {
                var shade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
                shade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 0 });
                shade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xCC, 0, 0, 0), Offset = 1 });
                var text = new StackPanel { Spacing = 0 };
                if (asset.Author is { Length: > 0 } author)
                    text.Children.Add(new TextBlock { Text = L.F("by {0}", author), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = $"{asset.Width} × {asset.Height}", FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)) });
                var footBar = new Border
                {
                    VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(10, 18, 10, 8), Background = shade, CornerRadius = new CornerRadius(0, 0, radius.BottomRight, radius.BottomLeft),
                    Opacity = 0, IsHitTestVisible = false, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) }, Child = text,
                };
                face.Children.Add(footBar);
                caption = footBar;
            }
            face.Children.Add(picked);
            face.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
            var button = new Button { Content = face, Padding = new Thickness(0), CornerRadius = radius, BorderThickness = new Thickness(0), Background = clear };
            ToolTipService.SetToolTip(button, asset.Author is { } by ? L.F("{0} × {1}, by {2}", asset.Width, asset.Height, by) : $"{asset.Width} × {asset.Height}");
            void Hover(bool on)
            {
                wash.Opacity = on ? 1 : 0;
                if (caption is not null) caption.Opacity = on ? 1 : 0;
                face.CenterPoint = new((float)(tw / 2), (float)(th / 2), 0);
                face.Scale = on ? new(1.03f, 1.03f, 1) : new(1, 1, 1);
            }
            button.PointerEntered += (_, _) => Hover(true);
            button.PointerExited += (_, _) => Hover(false);
            // A controller or the keyboard on a tile shows it the same way.
            button.GotFocus += (_, _) => { if (button.FocusState == FocusState.Keyboard) Hover(true); };
            button.LostFocus += (_, _) => Hover(false);
            // The tile's own kind: a tab changed while its picture downloads must not file it under the new tab's kind (2.1-rc.3 audit).
            var tileKind = kind;
            button.Click += async (_, _) =>
            {
                busy.IsActive = true;
                button.IsEnabled = false;
                try
                {
                    var file = await SteamGridDb.DownloadAsync(game.Id, tileKind.Kind, asset.Url, cts.Token);
                    ArtOverrides.Set(game.Id, tileKind.Kind, file);
                    Log.Info($"Artwork for {game.Name}: SteamGridDB {tileKind.Kind} {asset.Id}");
                    if (kind != tileKind) return;
                    ClearPicked();
                    picked.Visibility = Visibility.Visible;
                    ShowNow();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Warn($"Artwork for {game.Name}: {ex.Message}");
                    Status(L.F("The picture could not be downloaded: {0}", ex.Message));
                }
                finally
                {
                    busy.IsActive = false;
                    button.IsEnabled = true;
                }
            };
            return (button, new Slot(image, asset.Thumb));
        }

        void ClearPicked()
        {
            foreach (var b in wrap.Children.OfType<Button>())
                if (b.Content is Grid g)
                    foreach (var r in g.Children.OfType<Grid>().Where(x => x.Tag as string == "picked")) r.Visibility = Visibility.Collapsed;
        }

        // Placeholders the size of the tiles while SteamGridDB answers the first page, gently pulsing.
        void ShowSkeletons(int count)
        {
            var (tw, th) = TileSize();
            var radius = new CornerRadius(kind.Kind == ArtKind.Icon ? 16 : 10);
            for (int i = 0; i < count; i++)
            {
                var sk = new Border { Width = tw, Height = th, CornerRadius = radius, Background = subtle, IsHitTestVisible = false };
                Motion.Pulse(sk);
                skeletons.Add(sk);
                wrap.Children.Add(sk);
            }
        }
        void HideSkeletons()
        {
            foreach (var sk in skeletons) wrap.Children.Remove(sk);
            skeletons.Clear();
        }

        async Task LoadAsync(bool append)
        {
            int mine = ++run;
            loading = true;
            try
            {
                HideSkeletons();
                if (!append)
                {
                    wrap.Children.Clear();
                    slots.Clear();
                    page = 0;
                    hasMore = false;
                    scroller.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                }
                // The next page is asked for; the page count moves only once it came (a failed try is not skipped).
                int wanted = append ? page + 1 : 0;
                Status(null);
                if (!SteamGridDb.HasKey)
                {
                    keyPanel.Visibility = Visibility.Visible;
                    matchText.Text = "";
                    search.IsEnabled = false;
                    hasMore = false;
                    return;
                }
                search.IsEnabled = true;
                if (!append)
                {
                    keyPanel.Visibility = Visibility.Collapsed;
                    ShowSkeletons(Columns() * 4);
                }
                if (match is null)
                {
                    // A game matched by hand before needs no Steam app id (that look-up can take a while).
                    bool chosen = AppState.Settings.Games.TryGetValue(game.Id, out var pgs) && pgs.SgdbGameId is not null;
                    if (!chosen) steamAppId ??= await GameInfo.SteamAppIdAsync(game, cts.Token);
                    match = await SteamGridDb.FindAsync(game, steamAppId, cts.Token);
                    // The other tabs' pictures are asked meanwhile: switching shows them at once.
                    if (match is not null) SteamGridDb.Prefetch(match.Id);
                }
                if (mine != run) return;
                if (match is null)
                {
                    matchText.Text = "";
                    Status(L.T("SteamGridDB has no game by this name. Search for it above."));
                    return;
                }
                matchText.Text = L.F("Pictures for {0}", match.Year is { } y ? $"{match.Name} ({y})" : match.Name);
                var assets = await SteamGridDb.AssetsAsync(match.Id, kind.Kind, wanted, cts.Token);
                if (mine != run) return;
                page = wanted;
                HideSkeletons();
                // Added in turns of a couple of rows (docs/snappy.md: big content in turns), each turn on its own frame; the
                // rows in view ask for their pictures as soon as they are there.
                int turn = Math.Max(6, Columns() * 2), added = 0;
                foreach (var a in assets)
                {
                    var (tile, slot) = Tile(a);
                    wrap.Children.Add(tile);
                    slots.Add(slot);
                    if (++added % turn == 0)
                    {
                        Realize();
                        await Task.Delay(16, cts.Token);
                        if (mine != run) return;
                    }
                }
                hasMore = assets.Count >= PageSize;
                Realize();
                if (wrap.Children.Count == 0) Status(L.F("No {0} for this game on SteamGridDB yet.", L.T(kind.Name).ToLowerInvariant()));
            }
            catch (SteamGridDb.KeyRefusedException)
            {
                Status(L.T("SteamGridDB refused the key. Paste it again."));
                keyPanel.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn($"SteamGridDB for {game.Name}: {ex.Message}");
                Status(L.F("SteamGridDB could not be reached: {0}", ex.Message));
            }
            finally
            {
                if (mine == run)
                {
                    HideSkeletons();
                    loading = false;
                    loader.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        // The store's own picture of a kind, shown while the user has not picked one.
        async Task<string?> StoreArtAsync(ArtKind k)
        {
            try
            {
                switch (k)
                {
                    case ArtKind.Cover: return await GameInfo.GetCoverAsync(game, cts.Token);
                    case ArtKind.Banner: return (await GameInfo.GetAsync(game, cts.Token)).HeroImage;
                    case ArtKind.Logo: return (await GameInfo.GetAsync(game, cts.Token)).LogoImage;
                    default:
                        return game.IconPath is { } icon && Path.GetExtension(icon).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".ico" or ".webp" ? icon : null;
                }
            }
            catch (Exception) { return null; }
        }

        void ShowKinds()
        {
            foreach (var (info, button, glyphBox, glyph, source) in kindRows)
            {
                bool on = info.Kind == kind.Kind;
                button.Background = on ? accentSoft : card;
                glyphBox.Background = on ? accent : subtle;
                glyph.Foreground = on ? onAccent : Ui.Brush("TextFillColorPrimaryBrush");
                source.Text = ArtOverrides.Get(game.Id, info.Kind) is not null ? L.T("Your pick") : L.T("From the store");
            }
        }

        async void ShowNow()
        {
            var k = kind;
            ShowKinds();
            var path = ArtOverrides.Get(game.Id, k.Kind);
            bool mine = path is not null;
            nowHint.Text = L.T(k.Hint) + ".";
            nowSourceText.Text = mine ? L.T("Your pick") : L.T("From the store");
            nowSourceText.Foreground = mine ? onAccent : Ui.Brush("TextFillColorPrimaryBrush");
            nowSource.Background = mine ? accent : subtle;
            reset.IsEnabled = mine;
            // As tall as the row, as wide as the picture's shape: the cover stands, the banner lies wide.
            double h = PreviewHeight, w = Math.Min(Math.Round(h * k.TileW / k.TileH), 420);
            overlayIcon.Glyph = mine ? "" : "";
            overlayText.Text = mine ? L.T("Use the store's") : L.T("Choose a file…");
            nowPic.Width = w;
            nowPic.Height = h;
            nowPic.Child = new FontIcon { Glyph = k.Glyph, FontSize = 26, Opacity = 0.4 };
            var shown = path ?? await StoreArtAsync(k.Kind);
            if (kind != k || shown is null) return;
            try
            {
                var image = new Image
                {
                    Source = new BitmapImage { UriSource = new Uri(shown), DecodePixelWidth = (int)Math.Ceiling(w * scale) },
                    Stretch = k.Stretch, Margin = k.Stretch == Stretch.Uniform ? new Thickness(12) : new Thickness(0), Opacity = 0,
                };
                image.ImageOpened += (_, _) => Motion.FadeIn(image, 160);
                nowPic.Child = image;
            }
            catch (UriFormatException) { }
        }

        foreach (var row in kindRows)
            row.Button.Click += (_, _) =>
            {
                if (row.Info.Kind == kind.Kind) return;
                kind = row.Info;
                ShowNow();
                _ = LoadAsync(false);
            };
        void UseFile(ArtKind pickedKind, string picked)
        {
            try
            {
                ArtOverrides.Import(game.Id, pickedKind, picked);
                Log.Info($"Artwork for {game.Name}: own {pickedKind} {Path.GetFileName(picked)}");
                ClearPicked();
                if (kind.Kind == pickedKind) ShowNow(); else ShowKinds();
            }
            catch (Exception ex) { Status(ex.Message); }
        }
        pickButton.Click += (_, _) =>
        {
            if (ArtOverrides.Get(game.Id, kind.Kind) is null) { _ = PickAsync(); return; }
            ArtOverrides.Set(game.Id, kind.Kind, null);
            ClearPicked();
            ShowNow();
        };
        pickButton.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = L.T("Use this picture");
            overlayIcon.Glyph = "";
            overlayText.Text = L.T("Use this picture");
            overlay.Opacity = 1;
        };
        pickButton.DragLeave += (_, _) =>
        {
            overlay.Opacity = 0;
            bool mineNow = ArtOverrides.Get(game.Id, kind.Kind) is not null;
            overlayIcon.Glyph = mineNow ? "" : "";
            overlayText.Text = mineNow ? L.T("Use the store's") : L.T("Choose a file…");
        };
        pickButton.Drop += async (_, e) =>
        {
            overlay.Opacity = 0;
            var pickedKind = kind.Kind;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.OfType<Windows.Storage.StorageFile>().FirstOrDefault(f => Path.GetExtension(f.Path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp");
                if (file is null) { Status(L.T("Drop a PNG, JPG, WebP or BMP picture.")); return; }
                UseFile(pickedKind, file.Path);
            }
            catch (Exception ex) { Status(ex.Message); }
        };
        async Task PickAsync()
        {
            if (App.Window is not { } window) return;
            // The tab the picker was opened for (2.1 audit): another tab chosen while the picker was open must not get the file.
            var pickedKind = kind.Kind;
            var picked = await Pickers.PickFileAsync(window, ".png", ".jpg", ".jpeg", ".webp", ".bmp");
            if (picked is null) return;
            UseFile(pickedKind, picked);
        }
        own.Click += (_, _) => _ = PickAsync();
        reset.Click += (_, _) =>
        {
            ArtOverrides.Set(game.Id, kind.Kind, null);
            ClearPicked();
            ShowNow();
        };
        changeKey.Click += (_, _) =>
        {
            keyPanel.Visibility = keyPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (keyPanel.Visibility == Visibility.Visible) keyBox.Focus(FocusState.Programmatic);
        };
        keySave.Click += async (_, _) =>
        {
            var key = keyBox.Password.Trim();
            if (key.Length == 0) return;
            keySave.IsEnabled = false;
            Status(L.T("Checking the key…"));
            bool ok;
            try { ok = await SteamGridDb.CheckKeyAsync(key, cts.Token); }
            catch (Exception ex) { Status(L.F("SteamGridDB could not be reached: {0}", ex.Message)); keySave.IsEnabled = true; return; }
            keySave.IsEnabled = true;
            if (!ok) { Status(L.T("SteamGridDB refused the key. Check it and paste it again.")); return; }
            AppState.Settings.SteamGridDbKey = key;
            SettingsStore.Save();
            SteamGridDb.ForgetAnswers();
            keyBox.Password = "";
            keyPanel.Visibility = Visibility.Collapsed;
            match = null;
            await LoadAsync(false);
        };
        search.TextChanged += async (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput || s.Text.Trim().Length < 2) return;
            var text = s.Text;
            // Asked once typing pauses, not for every letter.
            try { await Task.Delay(250, cts.Token); } catch (OperationCanceledException) { return; }
            if (s.Text != text) return;
            try
            {
                var found = await SteamGridDb.SearchAsync(text, cts.Token);
                if (s.Text == text) s.ItemsSource = found.Take(12).Select(g => g.Year is { } y ? $"{g.Name} ({y})" : g.Name).ToList();
                s.Tag = found;
            }
            catch (Exception ex) { Log.Warn($"SteamGridDB search: {ex.Message}"); }
        };
        search.QuerySubmitted += async (s, e) =>
        {
            var found = s.Tag as List<SgdbGame> ?? [];
            if (e.ChosenSuggestion is null)
            {
                try { found = await SteamGridDb.SearchAsync(s.Text, cts.Token); }
                catch (Exception ex) { Status(ex.Message); return; }
            }
            var pick = e.ChosenSuggestion is string chosen ? found.FirstOrDefault(g => (g.Year is { } y ? $"{g.Name} ({y})" : g.Name) == chosen) : found.FirstOrDefault();
            if (pick is null) { Status(L.T("SteamGridDB has no game by this name.")); return; }
            match = pick;
            SteamGridDb.Prefetch(pick.Id);
            if (!AppState.Settings.Games.TryGetValue(game.Id, out var gs)) AppState.Settings.Games[game.Id] = gs = new GameSettings();
            gs.SgdbGameId = pick.Id;
            gs.SgdbGameName = pick.Name;
            SettingsStore.Save();
            s.Text = "";
            await LoadAsync(false);
        };

        // The dialog's own scroller never scrolls: the window is exactly as tall as the content, the grid scrolls by itself.
        ScrollViewer? Outer()
        {
            for (DependencyObject? d = VisualTreeHelper.GetParent(rootGrid); d is not null; d = VisualTreeHelper.GetParent(d))
                if (d is ScrollViewer sv) return sv;
            return null;
        }
        rootGrid.Loaded += (_, _) =>
        {
            // The fades are the window's own colour thinning out, never a darker or lighter band: the colour is read from what
            // the dialog really draws under the content (its template's surface), once it is on screen.
            var windowColor = Windows.UI.Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B);
            for (DependencyObject? d = VisualTreeHelper.GetParent(rootGrid); d is not null; d = VisualTreeHelper.GetParent(d))
            {
                var fill = d switch { Border b => b.Background, Panel p => p.Background, Control c => c.Background, _ => null };
                if (fill is SolidColorBrush { Color.A: 0xFF, Opacity: >= 1 } solid) { windowColor = solid.Color; break; }
            }
            rootGrid.Background = new SolidColorBrush(windowColor);
            rootGrid.CornerRadius = new CornerRadius(16);
            PaintFades(windowColor);
            if (Outer() is { } outer)
            {
                outer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                outer.VerticalScrollMode = ScrollMode.Disabled;
            }
            Realize();
        };
        ScrollForTest = share =>
        {
            scroller.ScrollTo(0, scroller.ScrollableHeight * share, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
            Log.Info($"Artwork test: offset {scroller.VerticalOffset:0} of {scroller.ScrollableHeight:0}, fades {fadeTop.Opacity}/{fadeBottom.Opacity} {fadeTop.ActualWidth:0}x{fadeTop.ActualHeight:0}, colour {(fadeTop.Background as LinearGradientBrush)?.GradientStops[0].Color}");
        };
        GridFits = () =>
        {
            if (!scroller.IsLoaded) return false;
            var bottom = scroller.TransformToVisual(rootGrid).TransformPoint(new Windows.Foundation.Point(0, scroller.ActualHeight)).Y;
            var outer = Outer();
            double shown = outer?.ViewportHeight ?? rootGrid.ActualHeight;
            GridFitsDetail = $"grid bottom {bottom:0}, window {rootGrid.ActualHeight:0}, shown {shown:0}, outer scrollable {outer?.ScrollableHeight ?? 0:0}";
            return bottom <= rootGrid.ActualHeight + 1 && shown >= rootGrid.ActualHeight - 1 && (outer is null || outer.ScrollableHeight < 1);
        };

        dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(0),
            Content = rootGrid,
        };
        // No title or buttons of the dialog's own and no padding: the content is the whole window (its own "Done").
        dialog.Resources["ContentDialogPadding"] = new Thickness(0);
        dialog.Resources["ContentDialogMaxWidth"] = width + 4;
        dialog.Resources["ContentDialogMinWidth"] = width;
        dialog.Resources["ContentDialogMaxHeight"] = height + 4;
        dialog.Resources["ContentDialogMinHeight"] = height;
        done.Click += (_, _) => dialog.Hide();
        // The picture being edited takes the focus as a click would: no keyboard ring the moment the window opens.
        dialog.Opened += (_, _) => kindRows.FirstOrDefault(r => r.Info.Kind == kind.Kind).Button?.Focus(FocusState.Pointer);
        ShowNow();
        _ = LoadAsync(false);
        try { await dialog.ShowOnceAsync(); }
        finally { cts.Cancel(); }
    }
}
