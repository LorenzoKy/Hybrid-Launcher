using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The gear beside Play: the game's properties as a window over the page, laid out like Steam's: the game's name and the pages
/// on the left (General, Installed files, Library, Customization), the open page on the right as rows with their controls in
/// place (launch options typed straight in, switches, Open buttons with folder sizes). Esc, the X or a click outside closes it.
/// Built each time it opens, so it always matches the game's state.
/// </summary>
public sealed partial class GamePage
{
    string _propsPage = "general";
    StackPanel? _propsNav;
    ScrollViewer? _propsBody;

    void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        try { OpenProperties(); }
        catch (Exception ex) { Log.Warn($"Game properties: {ex.Message}"); }
    }

    void OpenProperties(string? page = null)
    {
        if (_game is null) return;
        if (page is not null) _propsPage = page;
        var game = _game;

        // The window: a dark rail with the game's name and the pages, the page on the right; a title bar with the X.
        _propsNav = new StackPanel { Spacing = 2, Padding = new Thickness(14, 26, 14, 14) };
        _propsNav.Children.Add(new TextBlock
        {
            Text = game.Title.ToUpperInvariant(), FontSize = 19, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush"), Margin = new Thickness(10, 0, 10, 18), MaxLines = 3,
        });
        foreach (var (id, title) in PropsPages(game)) _propsNav.Children.Add(NavItem(id, title));
        var rail = new Border { Background = Ui.Brush("SolidBackgroundFillColorTertiaryBrush"), Child = new ScrollViewer { Content = _propsNav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };

        _propsBody = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(30, 4, 30, 24) };
        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Style = Ui.Style("IconButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 8, 0) };
        ToolTipService.SetToolTip(close, L.T("Close (Esc)"));
        close.Click += (_, _) => CloseProperties();
        var right = new Grid { Background = Ui.Brush("SolidBackgroundFillColorBaseBrush") };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(close);
        Grid.SetRow(_propsBody, 1);
        right.Children.Add(_propsBody);

        var window = new Grid { CornerRadius = new CornerRadius(10), BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1) };
        window.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        window.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        window.Children.Add(rail);
        Grid.SetColumn(right, 1);
        window.Children.Add(right);
        double w = Math.Min(1000, Math.Max(640, ActualWidth - 120)), h = Math.Min(720, Math.Max(460, ActualHeight - 100));
        window.Width = w;
        window.Height = h;
        window.Tapped += (_, e) => e.Handled = true;
        window.Shadow = new ThemeShadow();
        window.Translation = new System.Numerics.Vector3(0, 0, 48);

        PropsOverlay.Children.Clear();
        PropsOverlay.Children.Add(window);
        PropsOverlay.Visibility = Visibility.Visible;
        ShowPropsPage(_propsPage);
        PropsOverlay.Focus(FocusState.Programmatic);
    }

    void CloseProperties()
    {
        PropsOverlay.Visibility = Visibility.Collapsed;
        PropsOverlay.Children.Clear();
        _propsNav = null;
        _propsBody = null;
    }

    void PropsOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => CloseProperties();

    void PropsOverlay_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        CloseProperties();
    }

    static IEnumerable<(string Id, string Title)> PropsPages(Game game)
    {
        yield return ("general", L.T("General"));
        if (!game.NotInstalled) yield return ("files", L.T("Installed files"));
        yield return ("library", L.T("Library"));
        yield return ("custom", L.T("Customization"));
    }

    Button NavItem(string id, string title)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = title, FontSize = 15 },
            Tag = id,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        b.Click += (_, _) => ShowPropsPage(id);
        return b;
    }

    void ShowPropsPage(string id)
    {
        if (_game is not { } game || _propsNav is null || _propsBody is null) return;
        if (!PropsPages(game).Any(p => p.Id == id)) id = "general";
        _propsPage = id;
        foreach (var b in _propsNav.Children.OfType<Button>())
        {
            bool on = (string)b.Tag == id;
            b.Background = on ? Ui.Brush("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ((TextBlock)b.Content).FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        var page = new StackPanel { Spacing = 8 };
        var title = PropsPages(game).First(p => p.Id == id).Title;
        page.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        switch (id)
        {
            case "general": GeneralPage(page, game); break;
            case "files": FilesPage(page, game); break;
            case "library": LibraryPropsPage(page, game); break;
            default: CustomPage(page); break;
        }
        _propsBody.Content = page;
        _propsBody.ChangeView(null, 0, null, disableAnimation: true);
    }

    // ---------- pages ----------

    void GeneralPage(StackPanel page, Game game)
    {
        if (!game.NotInstalled)
        {
            var hdr = new ToggleSwitch { IsOn = _settings.SmartHdr, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = _hdrCapable != false && !GameSessions.IsActive(game.Id) };
            hdr.Toggled += (_, _) =>
            {
                HdrToggle.IsChecked = hdr.IsOn;
                HdrToggle_Click(HdrToggle, new RoutedEventArgs());
            };
            page.Children.Add(PropRow(L.T("Smart HDR"), L.T("Windows HDR turns on when the game starts and off when it closes."), hdr));
        }

        if (!game.NotInstalled)
        {
            page.Children.Add(PropHeading(L.T("Launch options")));
            bool canPass = GameLauncher.SupportsArgs(game);
            page.Children.Add(PropNote(canPass
                ? L.T("Advanced users can type launch options here. Steam games start through Steam (-applaunch) with them; Epic, GOG and other games start their exe directly with them.")
                : L.T("Xbox / Game Pass games cannot receive launch arguments")));
            var args = new TextBox { Text = _settings.LaunchArgs, PlaceholderText = L.T("Launch arguments, e.g. -dx12 -skipintro"), IsEnabled = canPass, FontSize = 15, Padding = new Thickness(12, 9, 12, 9) };
            args.TextChanged += (_, _) =>
            {
                _settings.LaunchArgs = args.Text.Trim();
                SettingsStore.Save();
                ArgsBox.Text = _settings.LaunchArgs;
            };
            page.Children.Add(args);
            var inBar = new ToggleSwitch { IsOn = _settings.LaunchArgsInBar, OnContent = "", OffContent = "", MinWidth = 0 };
            inBar.Toggled += (_, _) =>
            {
                _settings.LaunchArgsInBar = inBar.IsOn;
                SettingsStore.Save();
                ArgsBox.Visibility = _settings.LaunchArgsInBar ? Visibility.Visible : Visibility.Collapsed;
                LayoutArgsBox();
            };
            page.Children.Add(PropRow(L.T("Show them on the launch bar"), L.T("A box beside Play to change them quickly."), inBar));
        }

        page.Children.Add(PropHeading(L.T("Store page")));
        page.Children.Add(PropRow(L.T("The game's page"), L.T("The store page its art, overview and wiki come from. Pick another when Hybrid Launcher matched the wrong game."),
            PropButton(L.T("Choose…"), () => { CloseProperties(); ChoosePage_Click(this, new RoutedEventArgs()); })));
    }

    void FilesPage(StackPanel page, Game game)
    {
        var exeDir = TargetDir ?? game.InstallDir;
        page.Children.Add(PropNote(L.F("Size of installation: {0}", string.IsNullOrWhiteSpace(SizeText.Text) ? "…" : SizeText.Text)));
        page.Children.Add(PropRow(L.T("Game folder"), exeDir, PropButton(L.T("Browse…"), () => OpenFolder_Click(this, new RoutedEventArgs()))));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(exeDir), Path.TrimEndingDirectorySeparator(game.InstallDir), StringComparison.OrdinalIgnoreCase))
            page.Children.Add(PropRow(L.T("Install folder"), game.InstallDir, PropButton(L.T("Browse…"), () => OpenInstallFolder_Click(this, new RoutedEventArgs()))));
        if (_saveDir is not null)
            page.Children.Add(PropRow(L.T("Saves"), Sized(_saveDir, SaveSizeText.Text), PropButton(L.T("Browse…"), () => SaveFolder_Click(this, new RoutedEventArgs()))));
        if (_configDir is not null)
            page.Children.Add(PropRow(L.T("Configuration"), Sized(_configDir, ConfigSizeText.Text), PropButton(L.T("Browse…"), () => ConfigFolder_Click(this, new RoutedEventArgs()))));
        if (_saveDir is null && _configDir is null)
            page.Children.Add(PropNote(L.T("PCGamingWiki lists no saves or configuration folder found on this PC.")));

        page.Children.Add(PropHeading(L.T("Game exe")));
        var exe = GameScanner.EffectiveExe(game, _settings);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(PropButton(L.T("Change…"), () => { CloseProperties(); ChangeExe_Click(this, new RoutedEventArgs()); }));
        if (_settings.ExeOverride is not null) buttons.Children.Add(PropButton(L.T("Use the detected exe"), () => { ResetExe_Click(this, new RoutedEventArgs()); ShowPropsPage("files"); }));
        page.Children.Add(PropRow(exe is null ? L.T("No exe found") : Path.GetFileName(exe), (exe ?? "") + "\n" + L.T("The exe that starts the game; mods go next to it."), buttons));

        if (!game.Installing)
        {
            page.Children.Add(PropHeading(L.T("Uninstall")));
            var red = Color.FromArgb(0xFF, 0xE8, 0x4A, 0x55);
            var uninstall = PropButton(L.T("Uninstall…"), () => { CloseProperties(); UninstallGame_Click(this, new RoutedEventArgs()); });
            uninstall.Foreground = new SolidColorBrush(red);
            uninstall.Background = new SolidColorBrush(Color.FromArgb(0x26, red.R, red.G, red.B));
            page.Children.Add(PropRow(L.T("Remove the game from this PC"), L.T("Saves outside its folder stay."), uninstall));
        }
    }

    void LibraryPropsPage(StackPanel page, Game game)
    {
        if (!game.NotInstalled)
        {
            var pin = new ToggleSwitch { IsOn = AppState.IsPinned(game.Id), OnContent = "", OffContent = "", MinWidth = 0 };
            pin.Toggled += (_, _) => { if (pin.IsOn != AppState.IsPinned(game.Id)) Pin_Click(this, new RoutedEventArgs()); };
            page.Children.Add(PropRow(L.T("Pinned to quick access"), L.T("Pinned games sit at the top of the list."), pin));
            page.Children.Add(PropRow(L.T("Desktop shortcut"), L.T("Starts the game through Hybrid Launcher (Smart HDR, play time, Stop)."),
                PropButton(L.T("Create"), () => Shortcut_Click(this, new RoutedEventArgs()))));
            page.Children.Add(PropRow(L.T("Hide from the list"), L.T("Show it again from Settings."),
                PropButton(L.T("Hide"), () => { CloseProperties(); Hide_Click(this, new RoutedEventArgs()); })));
        }
        page.Children.Add(PropRow(L.T("Store"), game.StoreLabel, new Border()));
    }

    /// <summary>"Customization": a switch per part of the banner and the launch bar, for this game.</summary>
    void CustomPage(StackPanel page)
    {
        page.Children.Add(PropNote(L.T("The parts of the banner and the launch bar shown for this game. Parts hidden for every game are changed in Settings.")));
        foreach (var (key, label) in PageParts.All)
        {
            bool everywhere = PageParts.HiddenEverywhere(AppState.Settings, key);
            var sw = new ToggleSwitch { IsOn = !everywhere && !_settings.HiddenPageParts.Contains(key), IsEnabled = !everywhere, OnContent = "", OffContent = "", MinWidth = 0 };
            var k = key;
            sw.Toggled += (_, _) =>
            {
                if (sw.IsOn) _settings.HiddenPageParts.Remove(k);
                else _settings.HiddenPageParts.Add(k);
                SettingsStore.Save();
                ApplyPageParts();
            };
            page.Children.Add(PropRow(L.T(label), everywhere ? L.T("(hidden in every game)") : "", sw));
        }
        var all = new HyperlinkButton { Content = L.T("Choose for every game…"), Padding = new Thickness(0, 8, 0, 4) };
        all.Click += (_, _) => { CloseProperties(); App.Window?.OpenSettings("appearance"); };
        page.Children.Add(all);
    }

    // ---------- pieces ----------

    /// <summary>The folder's path, with its size once measured.</summary>
    static string Sized(string path, string size) => string.IsNullOrWhiteSpace(size) || size == L.T("Measuring…") ? path : $"{size}  ·  {path}";

    static TextBlock PropHeading(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 0) };

    static TextBlock PropNote(string text) => new() { Text = text, Style = Ui.Style("RowDescription"), FontSize = 13, TextWrapping = TextWrapping.Wrap };

    static Button PropButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(16, 6, 16, 7), MinWidth = 96 };
        b.Click += (_, _) =>
        {
            try { click(); }
            catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message); }
        };
        return b;
    }

    /// <summary>A Steam-style row: a tinted block with the name (and a line under it) on the left, its control on the right.</summary>
    static Border PropRow(string title, string detail, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 12, 14, 12), Child = grid, MinHeight = 58 };
    }
}

/// <summary>
/// The OptiScaler tab's foldable parts: the note under Install, ASI plugins, Custom DLLs and Saved versions. Each remembers whether it was
/// folded (Settings: CollapsedCards), for every game, so it stays the way the user left it.
/// </summary>
public sealed partial class GamePage
{
    const string HintKey = "opti:installhint", AsiKey = "opti:asi", CustomDllKey = "opti:customdlls", VersionsKey = "opti:versions";

    void InitFolds()
    {
        Foldable(AsiKey, AsiHeader, AsiTitle, AsiBody);
        Foldable(CustomDllKey, CustomDllHeader, CustomDllTitle, CustomDllBody);
        Foldable(VersionsKey, VersionsHeader, VersionsTitle, VersionsList);
        ApplyHintFold();
    }

    static bool Folded(string key) => AppState.Settings.CollapsedCards.Contains(key);

    static void SaveFold(string key, bool folded)
    {
        if (folded) AppState.Settings.CollapsedCards.Add(key);
        else AppState.Settings.CollapsedCards.Remove(key);
        SettingsStore.Save();
    }

    /// <summary>A chevron before the card's title; a click on either folds or opens <paramref name="body"/> (the buttons stay).</summary>
    static void Foldable(string key, Grid header, FrameworkElement title, FrameworkElement body)
    {
        bool open = !Folded(key);
        var chevron = Motion.Chevron(open, 11);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Margin = new Thickness(0, 0, 6, 0);
        header.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
        foreach (var child in header.Children.OfType<FrameworkElement>()) Grid.SetColumn(child, Grid.GetColumn(child) + 1);
        header.Children.Add(chevron);
        body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(title, L.T("Click to fold or open"));
        void Toggle()
        {
            open = !open;
            Motion.Turn(chevron, open);
            if (open) Motion.Open(body); else Motion.Close(body);
            SaveFold(key, !open);
        }
        chevron.Tapped += (_, e) => { e.Handled = true; Toggle(); };
        title.Tapped += (_, e) => { e.Handled = true; Toggle(); };
    }

    void InstallHintFold_Click(object sender, RoutedEventArgs e)
    {
        SaveFold(HintKey, !Folded(HintKey));
        ApplyHintFold();
    }

    /// <summary>The note under Install: the whole text wrapped, or folded to one line ending in "…" (the tooltip still has it all).</summary>
    void ApplyHintFold()
    {
        bool folded = Folded(HintKey);
        InstallHint.TextWrapping = folded ? TextWrapping.NoWrap : TextWrapping.Wrap;
        InstallHint.TextTrimming = folded ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        InstallHintFold.Content = Motion.Chevron(!folded, 10);
        ToolTipService.SetToolTip(InstallHintFold, folded ? L.T("Show the whole note") : L.T("Fold the note to one line"));
    }
}
