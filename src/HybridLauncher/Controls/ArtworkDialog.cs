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
/// "Edit artwork" (2.1), after Hydra Launcher's artwork picker: one tab per picture of the game (cover, banner, logo, icon),
/// each with SteamGridDB's community pictures in a grid. A click on one makes it the game's at once (a copy in the data folder);
/// a picture of the user's own works too, and "Use the store's" goes back. The game is matched on SteamGridDB by its Steam id
/// or its name; "Not this game?" searches another. SteamGridDB needs the user's own free API key: the dialog asks for it once.
/// </summary>
public static class ArtworkDialog
{
    sealed record KindInfo(ArtKind Kind, string Name, string Hint, double TileW, double TileH, Stretch Stretch);

    static readonly KindInfo[] Kinds =
    [
        new(ArtKind.Cover, "Cover", "The library tile (portrait, 2:3)", 132, 198, Stretch.UniformToFill),
        new(ArtKind.Banner, "Banner", "The wide picture at the top of the game's page", 300, 97, Stretch.UniformToFill),
        new(ArtKind.Logo, "Logo", "The title drawn on the banner", 196, 92, Stretch.Uniform),
        new(ArtKind.Icon, "Icon", "The small picture in the game list", 76, 76, Stretch.Uniform),
    ];

    public static async Task ShowAsync(XamlRoot root, ElementTheme theme, Game game, ArtKind start = ArtKind.Cover)
    {
        double width = Math.Clamp(root.Size.Width - 120, 420, 1000);
        double height = Math.Max(300, root.Size.Height - 300);
        var cts = new CancellationTokenSource();
        var kind = Kinds.First(k => k.Kind == start);
        SgdbGame? match = null;
        int page = 0;
        string? steamAppId = game.SteamAppId;

        var panel = new StackPanel { Spacing = 16, Width = width };
        var accent = Ui.Brush("AccentFillColorDefaultBrush");
        var muted = Ui.Brush("TextFillColorSecondaryBrush");
        var floor = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x16, 0x16, 0x1B));
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // 2.1-rc.9, after Hydra's: the four pictures as pills, a large preview of the one in use with where it comes from, then
        // SteamGridDB's pictures in a grid that fills the width, the one picked ringed in the accent.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var tabButtons = new List<Button>();
        foreach (var k in Kinds)
        {
            var b = new Button
            {
                Content = L.T(k.Name), Tag = k.Kind, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 6, 16, 7),
                BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            };
            tabButtons.Add(b);
            tabs.Children.Add(b);
        }
        panel.Children.Add(tabs);

        // The picture in use: a large preview on a dark floor, its name, where it comes from, and the buttons.
        var nowPic = new Border { CornerRadius = new CornerRadius(8), Background = floor, VerticalAlignment = VerticalAlignment.Center };
        var nowTitle = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold };
        var nowHint = new TextBlock { Foreground = muted, TextWrapping = TextWrapping.Wrap };
        var nowSourceText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        var nowSource = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), HorizontalAlignment = HorizontalAlignment.Left, Child = nowSourceText };
        var own = new Button { Content = L.T("Choose a file…"), Style = Ui.Style("AccentButtonStyle") };
        var reset = new Button { Content = L.T("Use the store's") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(own);
        buttons.Children.Add(reset);
        var nowText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
        nowText.Children.Add(nowTitle);
        nowText.Children.Add(nowSource);
        nowText.Children.Add(nowHint);
        nowText.Children.Add(buttons);
        var nowRow = new Grid { ColumnSpacing = 20 };
        nowRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nowRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nowRow.Children.Add(nowPic);
        Grid.SetColumn(nowText, 1);
        nowRow.Children.Add(nowText);
        panel.Children.Add(new Border
        {
            Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Child = nowRow,
        });

        // SteamGridDB: the game it was matched to, and a search for another.
        var sgdbHead = new Grid { ColumnSpacing = 12 };
        sgdbHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sgdbHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Min(320, width * 0.4)) });
        var sgdbTitle = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        sgdbTitle.Children.Add(new TextBlock { Text = L.T("From SteamGridDB"), FontSize = 15, FontWeight = FontWeights.SemiBold });
        var matchText = new TextBlock { Foreground = muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        sgdbTitle.Children.Add(matchText);
        sgdbHead.Children.Add(sgdbTitle);
        var search = new AutoSuggestBox { PlaceholderText = L.T("Search another game"), QueryIcon = new SymbolIcon(Symbol.Find), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(search, L.T("Not this game? Search SteamGridDB"));
        Grid.SetColumn(search, 1);
        sgdbHead.Children.Add(search);
        panel.Children.Add(sgdbHead);

        // The key, asked for once.
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
        panel.Children.Add(keyPanel);

        // The pictures: as many columns as fit, each tile as wide as the row allows (no ragged gap on the right).
        const double Gap = 12;
        double gridWidth = width - 16;
        var status = new TextBlock { Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        panel.Children.Add(status);
        var wrap = new WrapPanel(Gap, []);
        var more = new Button { Content = L.T("More"), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var ring = new ProgressRing { IsActive = false, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        var gridPanel = new StackPanel { Spacing = 6 };
        gridPanel.Children.Add(wrap);
        gridPanel.Children.Add(ring);
        gridPanel.Children.Add(more);
        var scroller = new ScrollViewer { Content = gridPanel, Height = height, Padding = new Thickness(0, 4, 16, 0) };
        panel.Children.Add(scroller);

        (double W, double H) TileSize()
        {
            int cols = Math.Max(1, (int)((gridWidth + Gap) / (kind.TileW + Gap)));
            double w = Math.Floor((gridWidth - Gap * (cols - 1)) / cols);
            return (w, Math.Round(w * kind.TileH / kind.TileW));
        }

        void Status(string? text)
        {
            status.Text = text ?? "";
            status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void ShowTabs()
        {
            foreach (var b in tabButtons)
            {
                bool on = b.Tag is ArtKind k && k == kind.Kind;
                b.Background = on ? accent : Ui.Brush("SubtleFillColorSecondaryBrush");
                b.Foreground = on ? Ui.Brush("TextOnAccentFillColorPrimaryBrush") : Ui.Brush("TextFillColorPrimaryBrush");
            }
        }

        void ShowNow()
        {
            var path = ArtOverrides.Get(game.Id, kind.Kind);
            nowTitle.Text = L.T(kind.Name);
            nowHint.Text = L.T(kind.Hint) + ".";
            bool mine = path is not null;
            nowSourceText.Text = mine ? L.T("Your pick") : L.T("From the store");
            nowSourceText.Foreground = mine ? Ui.Brush("TextOnAccentFillColorPrimaryBrush") : Ui.Brush("TextFillColorPrimaryBrush");
            nowSource.Background = mine ? accent : Ui.Brush("SubtleFillColorSecondaryBrush");
            reset.IsEnabled = mine;
            // Large enough to judge it: the banner wide, the cover tall, the logo and icon on their floor.
            double h = kind.Kind switch { ArtKind.Banner => 120, ArtKind.Cover => 150, ArtKind.Logo => 100, _ => 96 };
            double w = Math.Min(Math.Round(h * kind.TileW / kind.TileH), width * 0.5);
            h = Math.Round(w * kind.TileH / kind.TileW);
            nowPic.Width = w;
            nowPic.Height = h;
            nowPic.Child = path is null ? new FontIcon { Glyph = "", FontSize = 22, Opacity = 0.45 }
                : new Image { Source = new BitmapImage { UriSource = new Uri(path), DecodePixelWidth = (int)(w * 2) }, Stretch = kind.Stretch, Margin = kind.Stretch == Stretch.Uniform ? new Thickness(8) : new Thickness(0) };
        }

        FrameworkElement Tile(SgdbAsset asset)
        {
            var (tw, th) = TileSize();
            var image = new Image { Stretch = kind.Stretch, Opacity = 0, Margin = kind.Stretch == Stretch.Uniform ? new Thickness(10) : new Thickness(0) };
            image.Source = new BitmapImage { UriSource = new Uri(asset.Thumb), DecodePixelWidth = (int)(tw * 1.5) };
            image.ImageOpened += (_, _) => Motion.FadeIn(image, 200);
            var busy = new ProgressRing { IsActive = false, Width = 24, Height = 24 };
            var radius = new CornerRadius(kind.Kind == ArtKind.Icon ? 14 : 10);
            var face = new Grid { Width = tw, Height = th, CornerRadius = radius, Background = floor };
            face.Children.Add(image);
            face.Children.Add(busy);
            // A lighter wash under the pointer, and the accent ring of the picture picked, both over the picture.
            var wash = new Border { CornerRadius = radius, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Opacity = 0, IsHitTestVisible = false, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) } };
            var picked = new Border { CornerRadius = radius, BorderBrush = accent, BorderThickness = new Thickness(3), Visibility = Visibility.Collapsed, IsHitTestVisible = false, Tag = "picked" };
            face.Children.Add(wash);
            face.Children.Add(picked);
            var button = new Button { Content = face, Padding = new Thickness(0), CornerRadius = radius, BorderThickness = new Thickness(0), Background = clear };
            ToolTipService.SetToolTip(button, asset.Author is { } by ? L.F("{0} × {1}, by {2}", asset.Width, asset.Height, by) : $"{asset.Width} × {asset.Height}");
            button.PointerEntered += (_, _) => wash.Opacity = 1;
            button.PointerExited += (_, _) => wash.Opacity = 0;
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
            return button;
        }

        void ClearPicked()
        {
            foreach (var b in wrap.Children.OfType<Button>())
                if (b.Content is Grid g)
                    foreach (var r in g.Children.OfType<Border>().Where(x => x.Tag as string == "picked")) r.Visibility = Visibility.Collapsed;
        }

        int run = 0;
        async Task LoadAsync(bool append)
        {
            int mine = ++run;
            if (!append) { wrap.Children.Clear(); page = 0; scroller.ChangeView(null, 0, null, true); }
            // "More" asks for the next page; the page count moves only once it came (a failed try is not skipped).
            int wanted = append ? page + 1 : 0;
            more.Visibility = Visibility.Collapsed;
            Status(null);
            if (!SteamGridDb.HasKey)
            {
                keyPanel.Visibility = Visibility.Visible;
                matchText.Text = "";
                search.IsEnabled = false;
                return;
            }
            keyPanel.Visibility = Visibility.Collapsed;
            search.IsEnabled = true;
            ring.IsActive = true;
            ring.Visibility = Visibility.Visible;
            try
            {
                if (match is null)
                {
                    // A game matched by hand before needs no Steam app id (that look-up can take a while).
                    bool picked = AppState.Settings.Games.TryGetValue(game.Id, out var pgs) && pgs.SgdbGameId is not null;
                    if (!picked) steamAppId ??= await GameInfo.SteamAppIdAsync(game, cts.Token);
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
                foreach (var a in assets) wrap.Children.Add(Tile(a));
                more.Visibility = assets.Count >= 50 ? Visibility.Visible : Visibility.Collapsed;
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
                if (mine == run) { ring.IsActive = false; ring.Visibility = Visibility.Collapsed; }
            }
        }

        foreach (var b in tabButtons)
            b.Click += (_, _) =>
            {
                if (b.Tag is not ArtKind k || k == kind.Kind) return;
                kind = Kinds.First(x => x.Kind == k);
                ShowTabs();
                ShowNow();
                _ = LoadAsync(false);
            };
        more.Click += (_, _) => _ = LoadAsync(true);
        own.Click += async (_, _) =>
        {
            if (App.Window is not { } window) return;
            // The tab the picker was opened for (2.1 audit): another tab chosen while the picker was open must not get the file.
            var pickedKind = kind.Kind;
            var picked = await Pickers.PickFileAsync(window, ".png", ".jpg", ".jpeg", ".webp", ".bmp");
            if (picked is null) return;
            try
            {
                ArtOverrides.Import(game.Id, pickedKind, picked);
                Log.Info($"Artwork for {game.Name}: own {pickedKind} {Path.GetFileName(picked)}");
                if (kind.Kind == pickedKind) ShowNow();
            }
            catch (Exception ex) { Status(ex.Message); }
        };
        reset.Click += (_, _) =>
        {
            ArtOverrides.Set(game.Id, kind.Kind, null);
            ClearPicked();
            ShowNow();
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

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = L.F("Artwork of {0}", game.Title),
            CornerRadius = new CornerRadius(14),
            Content = panel,
            CloseButtonText = L.T("Done"),
            DefaultButton = ContentDialogButton.Close,
        };
        // The dialog's own padding (24 a side) and its scroll bar's lane on top of the content's width: with only 48 the right
        // edge of the content was cut (2.1-rc.3: "Use the store's" and the search box).
        dialog.Resources["ContentDialogMaxWidth"] = width + 96;
        dialog.Resources["ContentDialogMaxHeight"] = root.Size.Height;
        // The open tab takes the focus as a click would: no keyboard ring on a pill the moment the window opens.
        dialog.Opened += (_, _) => tabButtons.FirstOrDefault(b => b.Tag is ArtKind k && k == kind.Kind)?.Focus(FocusState.Pointer);
        ShowTabs();
        ShowNow();
        _ = LoadAsync(false);
        try { await dialog.ShowOnceAsync(); }
        finally { cts.Cancel(); }
    }
}
