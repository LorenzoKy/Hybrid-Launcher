using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HybridLauncher.Views;

public sealed partial class SettingsPage : Page
{
    bool _loading;

    /// <summary>The widest the open tab is drawn (2.0: a reading width; at 1600 a row's name and its switch were a screen apart).</summary>
    const double ColumnMax = 1320;

    /// <summary>
    /// The side column stands at the window's left edge; the open tab, at most <see cref="ColumnMax"/> wide, is centred in the
    /// window (2.0-rc.3), or as near to that as the side column lets it. Exact widths, never MaxWidth with Stretch (issue #5);
    /// everything on whole device pixels, so text stays sharp.
    /// </summary>
    void FitColumn(double room)
    {
        var (side, offset, width) = Columns(room);
        if (width <= 0) return;
        if (Math.Abs(SideColumn.Width - side) > 0.01) SideColumn.Width = side;
        if (SideColumn.Margin.Left != 0) SideColumn.Margin = new Thickness(0);
        if (Math.Abs(PageColumn.Margin.Left - offset) > 0.01) PageColumn.Margin = new Thickness(offset, 0, 0, 0);
        // (Width starts as NaN, and NaN compares false with everything: the test must be the one that fails for it.)
        if (!(Math.Abs(PageColumn.Width - width) < 0.01)) PageColumn.Width = width;
    }

    /// <summary>The side column's width, the open tab's distance from the side column, and its width, for the page as it is now.</summary>
    (double Side, double Offset, double Width) Columns(double room)
    {
        double total = ActualWidth;
        if (total <= 0 || room <= 0) return (296, 0, 0);
        double scale = (XamlRoot?.RasterizationScale is > 0 and var r ? r : 1) * Helpers.UiScale.Factor;
        double Whole(double v) => Math.Floor(v * scale) / scale;
        // A small window: the side column gives the open tab some of its room.
        double side = total < 1000 ? 232 : 296;
        // The room is the viewport's, not the ScrollView's: a scroll bar with room of its own cut the column on the right.
        double bar = Math.Max(0, PageScroller.ActualWidth - room);
        double free = total - side - bar;
        double width = Whole(Math.Min(ColumnMax, free));
        // Centred in the whole window; pushed right of the side column when there is not that much room.
        double offset = Whole(Math.Clamp((total - width) / 2 - side, 0, Math.Max(0, free - width)));
        return (side, offset, width);
    }

    /// <summary>Self-test: the column's width and how far it is from where it should stand (both should be right on every tab).</summary>
    internal (double Width, double Expected, double OffCentre, double SubPixel) SelfTestColumn()
    {
        UpdateLayout();
        double room = PageScroller.ViewportWidth > 0 ? Math.Min(PageScroller.ActualWidth, PageScroller.ViewportWidth) : PageScroller.ActualWidth;
        var (_, offset, expected) = Columns(room);
        double left = PageColumn.TransformToVisual(PageScroller).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        // How far the column's left edge is from a whole device pixel in the window (0 = sharp text; 0.5 = blurred).
        double scale = (XamlRoot?.RasterizationScale is > 0 and var r ? r : 1) * Helpers.UiScale.Factor;
        double x = PageColumn.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).X * scale;
        double sub = Math.Abs(x - Math.Round(x));
        // The column must be shown whole: a scroller laid out narrower than its content clips the right side.
        if (left + PageColumn.ActualWidth > room + 1) sub = Math.Max(sub, 1 + left + PageColumn.ActualWidth - room);
        return (PageColumn.ActualWidth, expected, Math.Abs(SideColumn.Margin.Left) + Math.Abs(left - offset), sub);
    }

    /// <summary>Self-test: the page's scroller (scroll smoothness).</summary>
    internal ScrollView SelfTestScroller => PageScroller;
    /// <summary>Self-test (perf): the tabs, to click.</summary>
    internal IEnumerable<FrameworkElement> SelfTestTabItems => SettingsTabs.Items;

    public SettingsPage()
    {
        using var __perf = UiPerf.Scope("SettingsPage.ctor");
        InitializeComponent();
        Loc.Apply(this);
        CompositorWheel.Attach(PageScroller);
        CompositorWheel.OnViewportWidth(PageScroller, FitColumn);
        // The app's own trophy on the Achievements tab, in the tab's amber.
        var trophy = Icons.Make(Icons.TrophyGlyph, 15);
        if (Application.Current.Resources.TryGetValue("TintAmberBrush", out var amber)) trophy.Foreground = amber as Microsoft.UI.Xaml.Media.Brush;
        AchievementsTabItem.Icon = trophy;
        BuildNav();
        GuardSearchFocus();
        SizeChanged += (_, _) => FitColumn(PageScroller.ViewportWidth > 0 ? Math.Min(PageScroller.ActualWidth, PageScroller.ViewportWidth) : PageScroller.ActualWidth);
        AchievementExtrasView.SourcesChanged += OnAchievementSourcesChanged;
        Loaded += (_, _) => { HybridLauncher.Helpers.ThemeRes.Applied -= OnThemeApplied; HybridLauncher.Helpers.ThemeRes.Applied += OnThemeApplied; };
        Unloaded += (_, _) => HybridLauncher.Helpers.ThemeRes.Applied -= OnThemeApplied;
        // Read by index: the labels follow the UI language.
        foreach (var v in TabIconChoices) TabIconsCombo.Items.Add(L.T(v.Label));
        foreach (var (_, name) in L.Choices) LanguageCombo.Items.Add(name == "Automatic (Windows)" ? L.T(name) : name);
    }

    static readonly (string Key, string Label)[] TabIconChoices = [("mono", "One colour"), ("color", "Each in its own colour")];

    static AppSettings S => AppState.Settings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        using var __perf = UiPerf.Scope("SettingsPage.NavigatedTo");
        base.OnNavigatedTo(e);
        LoadControls();

        DataText.Text = AppInfo.DataRoot;
        AboutText.Text = $"{AppInfo.Name} {AppUpdates.FriendlyVersion}" + (Elevation.IsAdmin ? "  " + L.T("(running as administrator)") : "");
        Fill();
        FillTabs();
        ShowFeatures();
        if (_searching) { SettingsSearch.Text = ""; ShowSearch(""); }
        var tab = SettingsTabs.Items.FirstOrDefault(i => i.Tag as string == S.SettingsTab) ?? SettingsTabs.Items[0];
        SettingsTabs.SelectedItem = tab;
        ShowSettingsTab(tab.Tag as string ?? "appearance");
    }

    /// <summary>Puts the saved settings into the controls (their change handlers stay quiet meanwhile).</summary>
    void LoadControls()
    {
        _loading = true;
        LanguageCombo.SelectedIndex = Math.Max(0, Array.FindIndex(L.Choices, c => c.Key == S.Language));
        MaterialToggle.IsOn = S.Material;
        ShadingToggle.IsOn = S.Shading;
        if (InterfaceSizeCombo.Items.Count == 0)
            foreach (var step in Helpers.UiScale.Steps) InterfaceSizeCombo.Items.Add(new ComboBoxItem { Content = $"{step}%", Tag = step });
        InterfaceSizeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Helpers.UiScale.Steps, Helpers.UiScale.Steps.MinBy(v => Math.Abs(v - S.InterfaceSize))));
        ScrollBarsToggle.IsOn = !S.HideScrollBars;
        AutoUpdateToggle.IsOn = S.AutoUpdateGames;
        AppUpdateToggle.IsOn = S.CheckAppUpdates;
        ShortcutToggle.IsOn = S.ShortcutOnInstall;
        ShowUpdateState();
        HltbToggle.IsOn = S.ShowHltb;
        // Filled once: the page is cached (NavigationCacheMode Required), so every visit would add the modes again.
        if (ModBackupsCombo.Items.Count != ModBackups.Modes.Length)
        {
            ModBackupsCombo.Items.Clear();
            foreach (var mode in ModBackups.Modes) ModBackupsCombo.Items.Add(ModBackups.ModeName(mode));
        }
        ModBackupsCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ModBackups.Modes, S.ModBackups));
        BackupGameFilesToggle.IsOn = S.BackupGameFiles;
        ShowBackupMode();
        CoverOverlaysToggle.IsOn = S.ShowCoverOverlays;
        FillCoverTagKinds();
        MetacriticToggle.IsOn = S.ShowMetacritic;
        FillPalettes();
        SteamCheck.IsChecked = S.ScanSteam;
        EpicCheck.IsChecked = S.ScanEpic;
        GogCheck.IsChecked = S.ScanGog;
        XboxCheck.IsChecked = S.ScanXbox;
        UbisoftCheck.IsChecked = S.ScanUbisoft;
        EaCheck.IsChecked = S.ScanEa;
        RockstarCheck.IsChecked = S.ScanRockstar;
        TabIconsCombo.SelectedIndex = Math.Max(0, Array.FindIndex(TabIconChoices, c => c.Key == S.TabIcons));
        FillIconsAndParts();
        HardwareToggle.IsOn = S.HardwareAcceleration;
        HardwarePauseToggle.IsOn = S.PauseEffectsInGame;
        BackgroundSaverToggle.IsOn = S.BackgroundSaver;
        TrayToggle.IsOn = S.CloseToTray;
        HideWhilePlayingToggle.IsOn = S.HideWhilePlaying;
        // Turned off in Windows' Startup apps (or removed) since: the toggle says what Windows will do.
        try { if (S.StartWithWindows && !Startup.IsOn) S.StartWithWindows = false; } catch { }
        StartupToggle.IsOn = S.StartWithWindows;
        StartOpenToggle.IsOn = S.StartOpensWindow;
        PopupToggle.IsOn = S.AchievementPopups;
        var o = S.Popup;
        Select(CornerCombo, o.Corner);
        Select(ScreenCombo, o.Screen);
        Select(SizeCombo, o.Size);
        Select(StyleCombo, o.Style);
        Select(LookCombo, o.Look);
        if (AchievementPopup.TryParseHex(o.CustomColor, out var custom)) CustomColorPicker.Color = custom;
        if (ColoursLookCombo.SelectedIndex < 0) ColoursLookCombo.SelectedIndex = 0;
        LoadLookColours();
        PlaceX.Value = Math.Round(Math.Clamp(o.X, 0, 1) * 100, 1);
        PlaceY.Value = Math.Round(Math.Clamp(o.Y, 0, 1) * 100, 1);
        OpacitySlider.Value = Math.Round(Math.Clamp(o.Opacity, 0.6, 1) * 100);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        SecondsSlider.Value = o.Seconds;
        SecondsText.Text = $"{o.Seconds:0.#} s";
        RarityToggle.IsOn = o.ShowRarity;
        UpdatePopupOptions();
        _loading = false;
    }

    string _openTab = "appearance";

    /// <summary>
    /// Restore defaults for the open tab only. Appearance: language, theme, colours, material, tab icons. Features: the window
    /// (notification area, start with Windows), the overview's extra sections, achievement pop-ups, the launchers scanned and the
    /// tabs' order and visibility. Storage: where custom builds are kept. Games, folders and hidden games are never touched.
    /// </summary>
    async void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var name = _openTab switch { "features" => L.T("Features"), "controller" => L.T("Controller"), "achievements" => L.T("Achievements"), "storage" => L.T("Storage"), _ => L.T("Appearance") };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.F("Restore the {0} settings?", name),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = _openTab switch
                {
                    "features" => L.T("Every option of this tab goes back to how Hybrid Launcher ships. Your games, game folders, hidden games and linked accounts stay as they are."),
                    "controller" => L.T("Controller navigation, the buttons and the Home button go back to how Hybrid Launcher ships."),
                    "achievements" => L.T("The unlock pop-up goes back to how Hybrid Launcher ships: on, with the look, place, colours and sound it comes with."),
                    "storage" => L.T("Custom builds go back to their default folder (the builds already there are not moved or deleted)."),
                    _ => L.T("Language, theme, colours and tab icons go back to how Hybrid Launcher ships."),
                },
            },
            PrimaryButtonText = L.T("Restore defaults"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
        try
        {
            var d = new AppSettings();
            switch (_openTab)
            {
                case "features":
                    S.HardwareAcceleration = d.HardwareAcceleration;
                    Helpers.MediaPolicy.NotifyEffectsChanged();
                    S.CloseToTray = d.CloseToTray;
                    if (S.StartWithWindows != d.StartWithWindows)
                    {
                        Startup.Set(d.StartWithWindows);
                        S.StartWithWindows = d.StartWithWindows;
                    }
                    S.ShowHltb = d.ShowHltb;
                    S.ShowMetacritic = d.ShowMetacritic;
                    bool rescan = S.ScanSteam != d.ScanSteam || S.ScanEpic != d.ScanEpic || S.ScanGog != d.ScanGog || S.ScanXbox != d.ScanXbox
                                  || S.ScanUbisoft != d.ScanUbisoft || S.ScanEa != d.ScanEa || S.ScanRockstar != d.ScanRockstar;
                    (S.ScanSteam, S.ScanEpic, S.ScanGog, S.ScanXbox) = (d.ScanSteam, d.ScanEpic, d.ScanGog, d.ScanXbox);
                    (S.ScanUbisoft, S.ScanEa, S.ScanRockstar) = (d.ScanUbisoft, d.ScanEa, d.ScanRockstar);
                    S.TabOrder.Clear();
                    S.HiddenTabs.Clear();
                    foreach (var g in S.Games.Values) { g.HiddenTabs.Clear(); g.View = null; }
                    S.GameView = d.GameView;
                    S.DashboardOrder.Clear();
                    S.DashboardHidden.Clear();
                    if (rescan) _ = AppState.RescanAsync();
                    break;
                case "controller":
                    S.GamepadInput = d.GamepadInput;
                    S.Controller = d.Controller;
                    App.Window?.GamepadSettingChanged();
                    App.Window?.HomeButtonSettingChanged();
                    break;
                case "achievements":
                    S.AchievementPopups = d.AchievementPopups;
                    S.Popup = d.Popup;
                    break;
                case "storage":
                    S.CustomBuildsDir = d.CustomBuildsDir;
                    break;
                default:
                    S.Language = d.Language;
                    S.Palette = d.Palette;
                    S.Material = d.Material;
                    S.Shading = d.Shading;
                    S.TabIcons = d.TabIcons;
                    S.CustomAccent = d.CustomAccent;
                    S.CustomTint = d.CustomTint;
                    S.CustomBackground = d.CustomBackground;
                    S.CustomBackdrop = d.CustomBackdrop;
                    S.InterfaceSize = d.InterfaceSize;
                    S.HideScrollBars = d.HideScrollBars;
                    Helpers.UiScale.Set(S.InterfaceSize);
                    Helpers.ScrollBars.UpdateStyles();
                    Helpers.ScrollBars.Apply(App.Window?.Content);
                    App.Window?.ApplyTheme(S.Theme);
                    var wanted = L.AutoLanguage;
                    RestartBar.IsOpen = wanted != L.Language;
                    break;
            }
            SettingsStore.Save();
            LoadControls();
            if (_openTab == "achievements") { AchievementSoundsView.Fill(AchievementSoundsPanel); AchievementExtrasView.Fill(AchievementExtrasPanel); }
            if (_openTab == "controller") ControllerSettingsView.Fill(ControllerPanel);
            Fill();
            FillTabs();
            App.Window?.ShowSuccess(L.F("{0}: defaults restored.", name));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
    }

    void SettingsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tab) return;
        // A tab picked while searching (the controller's LB / RB): the search gives way to it.
        if (_searching) { SettingsSearch.Text = ""; ShowSearch(""); }
        ShowSettingsTab(tab);
        UpdateNav();
        if (S.SettingsTab != tab)
        {
            S.SettingsTab = tab;
            SettingsStore.Save();
        }
    }

    /// <summary>
    /// The tabs as a vertical list on the left: one row per item of <c>SettingsTabs</c> (its icon on a tile of its colour,
    /// its name), the open one on a pill with a bar at its left edge and its sections listed under it (see
    /// <see cref="FillNavSections"/>). A click selects the item, so everything that follows a tab change still runs from
    /// <see cref="SettingsTabs_SelectionChanged"/>.
    /// </summary>
    void BuildNav()
    {
        SettingsNav.Children.Clear();
        foreach (var item in SettingsTabs.Items)
        {
            string tab = item.Tag as string ?? "";
            TabInfo.TryGetValue(tab, out var info);
            var tint = info.Tint is not null && Application.Current.Resources.TryGetValue($"Tint{info.Tint}Brush", out var t) ? t as Microsoft.UI.Xaml.Media.Brush : null;
            var wash = info.Tint is not null && Application.Current.Resources.TryGetValue($"Tint{info.Tint}WashBrush", out var w) ? w as Microsoft.UI.Xaml.Media.Brush : null;
            IconElement icon = Icons.Make(info.Glyph ?? "\uE713", 16);
            if (tint is not null) icon.Foreground = tint;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = wash, Child = icon, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(2), Background = Ui.Brush("AccentFillColorDefaultBrush"), VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(bar);
            Grid.SetColumn(tile, 1);
            row.Children.Add(tile);
            var text = new TextBlock { Text = item.Text, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            var button = new Button
            {
                Content = row,
                Tag = item,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4, 7, 12, 7),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, item.Text);
            var chosen = item;
            button.Click += (_, _) =>
            {
                // The open tab clicked while searching: the search gives way to it (SelectionChanged does not fire).
                if (SettingsTabs.SelectedItem == chosen && _searching) { SettingsSearch.Text = ""; ShowSearch(""); }
                else if (SettingsTabs.SelectedItem == chosen) PageScroller.ScrollTo(0, 0);
                SettingsTabs.SelectedItem = chosen;
            };
            SettingsNav.Children.Add(button);
        }
        UpdateNav();
    }

    void UpdateNav()
    {
        foreach (var b in SettingsNav.Children.OfType<Button>())
        {
            bool on = !_searching && b.Tag == (object?)SettingsTabs.SelectedItem;
            b.Background = on ? Ui.Brush("SubtleFillColorSecondaryBrush") : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (b.Content is Grid g)
            {
                if (g.Children[0] is Border bar) bar.Opacity = on ? 1 : 0;
                if (g.Children[2] is TextBlock t) t.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
        if (_navSections is not null) _navSections.Visibility = _searching ? Visibility.Collapsed : Visibility.Visible;
    }

    StackPanel? _navSections;

    /// <summary>
    /// The open tab's sections (its group headings) as a short list under it in the side column: a press scrolls the tab to
    /// that section. In place of the row of pills over the tab (2.0): the way around a long tab is where the tabs are.
    /// </summary>
    void FillNavSections(Panel panel)
    {
        if (_navSections is not null) SettingsNav.Children.Remove(_navSections);
        _navSections = null;
        var headers = Headers(panel).Select(h => h.Text).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        var open = SettingsNav.Children.OfType<Button>().FirstOrDefault(b => b.Tag == (object?)SettingsTabs.SelectedItem);
        if (headers.Count <= 1 || open is null) return;
        var list = new StackPanel { Margin = new Thickness(49, 0, 0, 6) };
        foreach (var text in headers)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = Sentence(text), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("TextFillColorSecondaryBrush") },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 8, 5),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, Sentence(text));
            // Found again on click: a tab built in code makes its headings anew when something changes.
            b.Click += (_, _) => Headers(panel).FirstOrDefault(h => h.Text == text)?.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, VerticalOffset = -12, AnimationDesired = true });
            list.Children.Add(b);
        }
        _navSections = list;
        SettingsNav.Children.Insert(SettingsNav.Children.IndexOf(open) + 1, list);
        list.Visibility = _searching ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- the search box takes the focus only from the user ----------

    long _searchPressTick = -10_000, _searchKeyTick = -10_000;
    bool _searchWanted;

    bool SearchHasFocus()
    {
        if (XamlRoot is null) return false;
        for (var node = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, SettingsSearch)) return true;
        return false;
    }

    /// <summary>
    /// The first control of the page is its search box, so WinUI hands it the focus whenever the focused control goes away (a
    /// row rebuilt, a tab's panel hidden, the game list redrawn): it then looked clicked, with its caret in it (2.0, the same
    /// guard as the game list's search box). It keeps the focus only when the user put it there: a press on it, Tab or the
    /// controller moving onto it, Ctrl+F. Anything else goes to the open tab's row in the list.
    /// </summary>
    void GuardSearchFocus()
    {
        AddHandler(PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, _) => _searchKeyTick = Environment.TickCount64), true);
        SettingsSearch.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _searchPressTick = Environment.TickCount64), true);
        SettingsSearch.GettingFocus += (_, e) =>
        {
            long now = Environment.TickCount64;
            bool byUser = e.InputDevice is Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Mouse or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Touch or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Pen
                    && now - _searchPressTick < 800
                || (e.InputDevice is Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Keyboard or Microsoft.UI.Xaml.Input.FocusInputDeviceKind.GameController
                    && e.Direction != Microsoft.UI.Xaml.Input.FocusNavigationDirection.None && now - _searchKeyTick < 800);
            if (byUser || _searchWanted || SearchHasFocus()) return;
            Log.Info($"Settings search box focus: {e.InputDevice}, {e.Direction}, moved away");
            // Sent to the open tab's row before the box ever has it (no caret blinking in it for a frame); when WinUI does not
            // allow the redirect, moved right after, not inside this event (see MainWindow.GuardSearchFocus).
            if (OpenTabButton() is { } open && e.TrySetNewFocusedElement(open)) return;
            DispatcherQueue.TryEnqueue(() => KeepFocusOffSearch(force: true));
        };
    }

    /// <summary>
    /// The focus leaves the search box unless the user put it there. Called after the page rebuilt something under the focus;
    /// <paramref name="force"/> when the caller already knows the move was not the user's.
    /// </summary>
    internal void KeepFocusOffSearch(bool force = false)
    {
        if (!SearchHasFocus()) return;
        long now = Environment.TickCount64;
        if (!force && (SettingsSearch.Text.Length > 0 || now - _searchPressTick < 800 || _searchWanted)) return;
        OpenTabButton()?.Focus(FocusState.Programmatic);
    }

    Button? OpenTabButton() => SettingsNav.Children.OfType<Button>().FirstOrDefault(b => b.Tag == (object?)SettingsTabs.SelectedItem) ?? SettingsNav.Children.OfType<Button>().FirstOrDefault();

    /// <summary>Restore defaults shown or not; hidden it keeps its room, so the header (and the search box) never moves.</summary>
    void ShowRestoreDefaults(bool on)
    {
        RestoreDefaultsButton.Visibility = Visibility.Visible;
        RestoreDefaultsButton.Opacity = on ? 1 : 0;
        RestoreDefaultsButton.IsHitTestVisible = on;
        RestoreDefaultsButton.IsTabStop = on;
    }

    void ShowSettingsTab(string tab)
    {
        using var __perf = UiPerf.Scope("SettingsPage.ShowTab");
        _openTab = tab;
        ShowRestoreDefaults(tab is not ("about" or "cloud" or "notifications"));
        AppearancePanel.Visibility = tab == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        FeaturesPanel.Visibility = tab == "features" ? Visibility.Visible : Visibility.Collapsed;
        AchievementsPanel.Visibility = tab == "achievements" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "achievements")
        {
            // The platinum trophy beside Platinum, drawn when the tab opens.
            PreviewPlatinumIcon.Content = Icons.Trophy(16, platinum: true);
            FillPreviewGames();
            AchievementSoundsView.Fill(AchievementSoundsPanel);
            AchievementExtrasView.Fill(AchievementExtrasPanel);
        }
        ControllerPanel.Visibility = tab == "controller" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "controller") ControllerSettingsView.Fill(ControllerPanel);
        StoragePanel.Visibility = tab == "storage" ? Visibility.Visible : Visibility.Collapsed;
        CloudPanel.Visibility = tab == "cloud" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "cloud") CloudSettingsView.Fill(CloudPanel);
        NotificationsPanel.Visibility = tab == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "notifications") NotificationSettingsView.Fill(NotificationsPanel);
        if (tab == "features") GitHubSignInView.Fill(GitHubPanel);
        AboutPanel.Visibility = tab == "about" ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabIntro(tab);
        // A control of the tab just hidden had the focus: WinUI drops it on the search box, with no event.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => KeepFocusOffSearch());
    }

    /// <summary>Each tab: its icon, colour and what it holds (the first line under the tabs).</summary>
    static readonly Dictionary<string, (string Glyph, string Tint, string Text)> TabInfo = new()
    {
        ["appearance"] = ("\uE790", "Pink", "Language, colour theme, material and the icons of the app."),
        ["features"] = ("\uE9E9", "Blue", "What Hybrid Launcher does: graphics, the window, game page tabs, the library, mods and finding games."),
        ["controller"] = ("\uE7FC", "Violet", "Moving around with a controller, what each button does, and the Home button that brings Hybrid Launcher back from a game."),
        ["achievements"] = (Icons.TrophyGlyph, "Amber", "The pop-up shown when a game unlocks an achievement: its look, where it shows, and its sounds."),
        ["notifications"] = ("\uEA8F", "Gold", "The notification list in the title bar, and what you hear about."),
        ["cloud"] = ("\uE753", "Cyan", "Copies of your saves in the cloud services you link."),
        ["storage"] = ("\uEDA2", "Orange", "Game updates, game folders, and where Hybrid Launcher keeps what it downloads."),
        ["about"] = ("\uE946", "Green", "The version, updates, and the projects Hybrid Launcher works with."),
    };

    /// <summary>
    /// The open tab's heading: its icon on its colour, its name and what it holds. Its sections (its group headings) are
    /// listed under the tab in the side column, each a press away with a mouse or a controller.
    /// </summary>
    void UpdateTabIntro(string tab)
    {
        if (!TabInfo.TryGetValue(tab, out var info)) { TabIntro.Visibility = Visibility.Collapsed; return; }
        TabIntro.Visibility = Visibility.Visible;
        var tint = Application.Current.Resources.TryGetValue($"Tint{info.Tint}Brush", out var at) ? at as Microsoft.UI.Xaml.Media.Brush : null;
        var wash = Application.Current.Resources.TryGetValue($"Tint{info.Tint}WashBrush", out var w) ? w as Microsoft.UI.Xaml.Media.Brush : null;
        var icon = Icons.Make(info.Glyph, 22);
        if (tint is not null) icon.Foreground = tint;
        TabIntroIcon.Content = icon;
        TabIntroTile.Background = wash;
        TabIntroTitle.Text = TabName(tab);
        TabIntroText.Text = L.T(info.Text);
        FillNavSections(PanelOf(tab));
    }

    /// <summary>The group headings of a tab, in order, wherever they sit in it.</summary>
    static IEnumerable<TextBlock> Headers(Panel panel)
    {
        var style = Ui.Style("GroupHeader");
        foreach (var child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            if (child is TextBlock tb && (tb.Style == style || tb.CharacterSpacing == 60)) yield return tb;
            else if (child is Panel inner) foreach (var h in Headers(inner)) yield return h;
        }
    }

    /// <summary>"GAME UPDATES" → "Game updates".</summary>
    static string Sentence(string caps) => caps.Length < 2 ? caps : caps[..1] + caps[1..].ToLower(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Opens one tab ("achievements", "cloud"...) on the page already on screen.</summary>
    public void SelectTab(string tab)
    {
        if (SettingsTabs.Items.FirstOrDefault(i => i.Tag as string == tab) is { } item) SettingsTabs.SelectedItem = item;
    }

    /// <summary>The controller's LB / RB: the tab before or after the open one (round).</summary>
    public void StepTab(int by)
    {
        int n = SettingsTabs.Items.Count;
        if (n == 0) return;
        int i = Math.Max(0, SettingsTabs.Items.IndexOf(SettingsTabs.SelectedItem));
        SettingsTabs.SelectedItem = SettingsTabs.Items[((i + by) % n + n) % n];
    }

    /// <summary>Self-test hook: shows each settings tab once.</summary>
    public void SelfTestTabs()
    {
        foreach (var item in SettingsTabs.Items)
        {
            SettingsTabs.SelectedItem = item;
        }
        SettingsTabs.SelectedItem = SettingsTabs.Items[0];
    }

    /// <summary>Self-test hook: the Sounds section under the pop-ups, scrolled to, with its rows built.</summary>
    public void SelfTestSounds()
    {
        if (AchievementSoundsPanel.Children.Count < 8) throw new InvalidOperationException($"the Sounds section has {AchievementSoundsPanel.Children.Count} rows");
        AchievementSoundsPanel.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
    }

    /// <summary>Self-test hook: the Controller tab, with its button rows built.</summary>
    public void SelfTestController()
    {
        SettingsTabs.SelectedItem = SettingsTabs.Items.First(i => i.Tag as string == "controller");
        if (ControllerPanel.Children.Count < 15) throw new InvalidOperationException($"the Controller tab has {ControllerPanel.Children.Count} rows");
    }

    /// <summary>Self-test hook: presses Achievements → "Try it" the way a click does (through its automation peer).</summary>
    public void SelfTestTryPopup()
    {
        if (new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(TryPopupButton).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)
            is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke) invoke.Invoke();
        else throw new InvalidOperationException("the Try it button cannot be pressed");
    }

    /// <summary>Self-test hook: picks each look in the Look list quickly, one after another (each shows a sample), and puts back the first.</summary>
    public void SelfTestLooks()
    {
        var start = LookCombo.SelectedItem;
        foreach (var item in LookCombo.Items) LookCombo.SelectedItem = item;
        LookCombo.SelectedItem = start;
    }

    /// <summary>The game page tabs in their order, each with a switch: shown on every game page or hidden everywhere.</summary>
    void FillTabs()
    {
        TabsList.Children.Clear();
        foreach (var t in Controls.GameTabs.Ordered())
        {
            var toggle = new ToggleSwitch { IsOn = !S.HiddenTabs.Contains(t.Id), OnContent = L.T("Shown"), OffContent = L.T("Hidden"), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            var id = t.Id;
            toggle.Toggled += (_, _) =>
            {
                if (toggle.IsOn) S.HiddenTabs.Remove(id); else S.HiddenTabs.Add(id);
                if (Controls.GameTabs.All.All(x => S.HiddenTabs.Contains(x.Id))) { S.HiddenTabs.Remove("overview"); FillTabs(); }
                SettingsStore.Save();
            };
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(HybridLauncher.Helpers.Icons.Colored(t.Glyph, 16, t.Tint));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = t.Name, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Muted(t.Info));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(toggle);
            TabsList.Children.Add(grid);
        }
    }

    void ResetTabs_Click(object sender, RoutedEventArgs e)
    {
        S.TabOrder.Clear();
        S.HiddenTabs.Clear();
        foreach (var g in S.Games.Values) g.HiddenTabs.Clear();
        SettingsStore.Save();
        FillTabs();
        App.Window?.ShowSuccess(L.T("Every tab is back, in the default order, on every game."));
    }

    void Fill()
    {
        LibrariesList.Children.Clear();
        foreach (var lib in S.GameLibraries.ToList())
            LibrariesList.Children.Add(ItemRow(lib, L.T("Remove"), () => { S.GameLibraries.Remove(lib); Changed(); }));

        CustomList.Children.Clear();
        foreach (var c in S.CustomGames.ToList())
            CustomList.Children.Add(ItemRow(string.IsNullOrWhiteSpace(c.Name) ? c.ExePath : $"{c.Name}  ·  {c.ExePath}", L.T("Remove"), () => { S.CustomGames.Remove(c); Changed(); }));
        if (S.CustomGames.Count == 0) CustomList.Children.Add(Muted(L.T("None")));

        HiddenList.Children.Clear();
        foreach (var id in S.HiddenGames.ToList())
        {
            var name = AppState.Find(id)?.Name ?? id;
            HiddenList.Children.Add(ItemRow(name, L.T("Show"), () => { S.HiddenGames.Remove(id); Changed(); }));
        }
        if (S.HiddenGames.Count == 0) HiddenList.Children.Add(Muted(L.T("None")));

        FillPageCache();
        CacheText.Text = L.F("Downloaded releases (the newest 3 per channel are kept, in {0}) and the custom builds you imported.", AppInfo.DownloadsDir);

        CustomBuildsText.Text = CustomBuilds.Dir(S) + "\n" + L.T("Each sub-folder (or .zip / .7z) holding an OptiScaler DLL is a build you can install from a game's Custom box.");
        ResetBuildsButton.Visibility = S.CustomBuildsDir is null ? Visibility.Collapsed : Visibility.Visible;
    }

    async void ChangeBuildsDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = await Pickers.PickFolderAsync(App.Window!);
        if (dir is null) return;
        S.CustomBuildsDir = dir;
        SettingsStore.Save();
        Fill();
    }

    void ResetBuildsDir_Click(object sender, RoutedEventArgs e)
    {
        S.CustomBuildsDir = null;
        SettingsStore.Save();
        Fill();
    }

    void OpenBuildsDir_Click(object sender, RoutedEventArgs e) => GameLauncher.OpenFolder(CustomBuilds.Dir(S));

    void Changed()
    {
        SettingsStore.Save();
        Fill();
        _ = AppState.RescanAsync();
    }

    void Palette_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PaletteCombo.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        S.Palette = key;
        SettingsStore.Save();
        App.Window?.ApplyTheme(S.Theme);
    }

    // The Windows colours the theme list was last drawn with: drawn again only when Windows' accent changed since.
    Windows.UI.Color? _paletteStamp;
    bool _paletteFilled;

    /// <summary>
    /// The theme list, built once and again only when Windows' colours changed (the Windows theme's swatch shows them); the
    /// saved theme selected. Quiet: the change handler ignores it.
    /// </summary>
    void FillPalettes()
    {
        bool was = _loading;
        _loading = true;
        var stamp = Palettes.Get("Windows").Accent;
        if (!_paletteFilled || stamp != _paletteStamp)
        {
            PaletteCombo.Items.Clear();
            foreach (var p in Palettes.All) PaletteCombo.Items.Add(PaletteItem(p));
            _paletteStamp = stamp;
            _paletteFilled = true;
        }
        PaletteCombo.SelectedIndex = Array.FindIndex(Palettes.All, p => p.Key == Palettes.Get(S.Palette).Key);
        _loading = was;
    }

    /// <summary>Windows' colours changed while the page is open (MainWindow.WatchWindowsColours applied them): the swatch follows.</summary>
    void OnThemeApplied()
    {
        if (!IsLoaded) return;
        FillPalettes();
    }

    /// <summary>A palette entry: its accent as a swatch, the name and a short description.</summary>
    static ComboBoxItem PaletteItem(Palette p)
    {
        // The Windows theme's colours come from Windows: its swatch shows them, on a dark shade of the accent.
        p = Palettes.Get(p.Key);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        // The theme in small: its background with its accent as a dot in the middle.
        var swatch = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(p.Backdrop?.Top
                ?? (p.Tint is { } t ? Windows.UI.Color.FromArgb(0xFF, (byte)(0x14 + t.R / 10), (byte)(0x14 + t.G / 10), (byte)(0x18 + t.B / 10)) : Microsoft.UI.Colors.Black)),
            BorderThickness = new Thickness(1),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Child = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(p.Accent ?? Microsoft.UI.Colors.White),
            },
        };
        panel.Children.Add(swatch);
        panel.Children.Add(new TextBlock { Text = L.T(p.Name), VerticalAlignment = VerticalAlignment.Center });
        var item = new ComboBoxItem { Content = panel, Tag = p.Key };
        ToolTipService.SetToolTip(item, L.T(p.Description));
        return item;
    }

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedIndex < 0) return;
        S.Language = L.Choices[LanguageCombo.SelectedIndex].Key;
        SettingsStore.Save();
        // Shown until a restart: the language picked differs from the one this session runs in.
        var wanted = S.Language == "auto" ? L.AutoLanguage : S.Language;
        RestartBar.IsOpen = wanted != L.Language;
    }

    void Restart_Click(object sender, RoutedEventArgs e) => App.Restart();

    void Shortcut_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ShortcutOnInstall = ShortcutToggle.IsOn;
        SettingsStore.Save();
    }

    void AppUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CheckAppUpdates = AppUpdateToggle.IsOn;
        SettingsStore.Save();
    }

    /// <summary>The About tab's update row: the newer version when one was found, else when the app is up to date.</summary>
    void ShowUpdateState()
    {
        if (AppUpdater.Available is { } r)
        {
            UpdateStatusText.Text = L.F("{0} is out: update now, the app opens again by itself.", r.Version.ToString(3));
            UpdateButton.Content = L.F("Update to {0}", r.Version.ToString(3));
            UpdateButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }
        else
        {
            UpdateStatusText.Text = L.F("You have {0}. New versions come from GitHub and install with one click.", AppUpdates.FriendlyVersion);
            UpdateButton.Content = L.T("Check for updates");
            UpdateButton.ClearValue(StyleProperty);
        }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (AppUpdater.Available is { } ready)
        {
            await AppUpdater.OfferAsync(XamlRoot, ready);
            return;
        }
        UpdateButton.IsEnabled = false;
        UpdateStatusText.Text = L.T("Checking GitHub…");
        try
        {
            var found = await AppUpdater.CheckAsync(quiet: false);
            ShowUpdateState();
            if (found is not null) await AppUpdater.OfferAsync(XamlRoot, found);
            else UpdateStatusText.Text = L.F("You have the newest version ({0}).", AppUpdates.FriendlyVersion);
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AutoUpdateGames = AutoUpdateToggle.IsOn;
        SettingsStore.Save();
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateCheckText.Visibility = Visibility.Visible;
        UpdateCheckText.Text = L.T("Checking…");
        int started = await Helpers.GameUpdater.CheckNowAsync(evenIfOff: true);
        UpdateCheckText.Text = started == 0 ? L.T("Every game Hybrid Launcher downloaded is up to date.") : L.F("{0} update(s) started in Downloads.", started);
        CheckUpdatesButton.IsEnabled = true;
    }

    void StoreUpdates_Click(object sender, RoutedEventArgs e) => GameLauncher.OpenUri("ms-windows-store://downloadsandupdates");

    void Material_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Material = MaterialToggle.IsOn;
        SettingsStore.Save();
        App.Window?.ApplyTheme(S.Theme);
    }

    void Shading_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || S.Shading == ShadingToggle.IsOn) return;
        S.Shading = ShadingToggle.IsOn;
        SettingsStore.Save();
        App.Window?.ApplyTheme(S.Theme);
    }

    void InterfaceSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || InterfaceSizeCombo.SelectedItem is not ComboBoxItem { Tag: int size } || size == S.InterfaceSize) return;
        S.InterfaceSize = size;
        SettingsStore.Save();
        Helpers.UiScale.Set(size);
    }

    void ScrollBars_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.HideScrollBars = !ScrollBarsToggle.IsOn;
        SettingsStore.Save();
        Helpers.ScrollBars.UpdateStyles();
        Helpers.ScrollBars.Apply(App.Window?.Content);
    }

    void CoverOverlays_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ShowCoverOverlays = CoverOverlaysToggle.IsOn;
        SettingsStore.Save();
        foreach (var c in CoverTagKindsPanel.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<CheckBox>())) c.IsEnabled = S.ShowCoverOverlays;
    }

    /// <summary>One check box per kind of cover tag (pinned, play time, each mod, upscalers, achievements).</summary>
    void FillCoverTagKinds()
    {
        CoverTagKindsPanel.Children.Clear();
        var wrap = new WrapPanel(20, []);
        CoverTagKindsPanel.Children.Add(wrap);
        foreach (var (key, label) in AppSettings.CoverTagKinds)
        {
            var cb = new CheckBox
            {
                Content = L.T(label),
                MinWidth = 0,
                IsChecked = key == "tech" ? S.ShowCoverTags : !S.HiddenCoverTags.Contains(key),
                IsEnabled = S.ShowCoverOverlays,
            };
            cb.Click += (_, _) => { S.SetCoverTag(key, cb.IsChecked == true); SettingsStore.Save(); };
            wrap.Children.Add(cb);
        }
    }

    void OverviewCards_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ShowHltb = HltbToggle.IsOn;
        S.ShowMetacritic = MetacriticToggle.IsOn;
        SettingsStore.Save();
    }

    void Kofi_Click(object sender, RoutedEventArgs e) => Open("https://ko-fi.com/lorenzoky");

    /// <summary>
    /// Report a problem: says what happens, then opens GitHub's bug form filled in and shows the saved log in Explorer, ready
    /// to drag into the form next to the screenshots.
    /// </summary>
    async void Report_Click(object sender, RoutedEventArgs e)
    {
        var steps = new StackPanel { Spacing = 8 };
        foreach (var line in new[]
        {
            L.T("1. GitHub opens the bug form (you need a GitHub account). The version, your system and the end of this session's log are already filled in."),
            L.T("2. Explorer shows the whole log of this session and the one before: drag that file into the Log field."),
            L.T("3. Write what happened and the steps that make it happen again, and add screenshots. Reports without them are closed."),
            L.T("Your Windows user name, the PC's name and e-mail addresses are taken out of the log first. Nothing is sent until you submit the form yourself."),
        })
            steps.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.T("Report a problem"),
            Content = steps,
            PrimaryButtonText = L.T("Open GitHub"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
        try
        {
            Log.Info("Report: opening GitHub's bug form");
            var (file, url) = await Task.Run(() =>
            {
                var saved = BugReport.SaveLog();
                return (saved, BugReport.FormUrl(AppUpdates.Current.ToString(3), BugReport.SystemLine(), Path.GetFileName(saved)));
            });
            Open(url);
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"")?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Report", ex);
            Open(BugReport.IssuesUrl + "/new/choose");
        }
    }
    void Github_Click(object sender, RoutedEventArgs e) => Open("https://github.com/LorenzoKy");

    static void Open(string url)
    {
        try { GameLauncher.OpenUri(url); }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
    }

    /// <summary>
    /// Appearance → Icons (one switch per place: tabs, sub-tabs, separators, card headings, launch bar, banner) and
    /// Game page (one switch per part, for every game). Applied when the page is next drawn.
    /// </summary>
    void FillIconsAndParts()
    {
        IconSpotsPanel.Children.Clear();
        foreach (var (key, label, description) in IconSpots.All)
            IconSpotsPanel.Children.Add(SwitchRow(L.T(label), L.T(description), !S.HiddenIcons.Contains(key), on =>
            {
                if (on) S.HiddenIcons.Remove(key); else S.HiddenIcons.Add(key);
            }));
        PagePartsPanel.Children.Clear();
        foreach (var (key, label) in PageParts.All)
            PagePartsPanel.Children.Add(SwitchRow(L.T(label), null, !S.HiddenPageParts.Contains(key), on =>
            {
                if (on) S.HiddenPageParts.Remove(key); else S.HiddenPageParts.Add(key);
            }));
    }

    /// <summary>A settings row: name and description on the left, a Shown / Hidden switch on the right; every change is saved.</summary>
    Border SwitchRow(string header, string? description, bool on, Action<bool> set)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = header, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowHeader"] });
        if (description is not null) text.Children.Add(new TextBlock { Text = description, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"] });
        grid.Children.Add(text);
        var toggle = new ToggleSwitch { OnContent = L.T("Shown"), OffContent = L.T("Hidden"), IsOn = on, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            set(toggle.IsOn);
            SettingsStore.Save();
        };
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return new Border { Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowStyle"], Child = grid };
    }

    void BackupGameFiles_Toggled(object sender, RoutedEventArgs e)
    {
        ShowBackupMode();
        if (_loading || S.BackupGameFiles == BackupGameFilesToggle.IsOn) return;
        S.BackupGameFiles = BackupGameFilesToggle.IsOn;
        SettingsStore.Save();
    }

    /// <summary>"When to back up" means something only with backups on.</summary>
    void ShowBackupMode()
    {
        bool on = BackupGameFilesToggle.IsOn;
        ModBackupsCombo.IsEnabled = on;
        ModBackupsRow.Opacity = on ? 1 : 0.5;
    }

    void ModBackups_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModBackupsCombo.SelectedIndex < 0) return;
        S.ModBackups = ModBackups.Modes[ModBackupsCombo.SelectedIndex];
        SettingsStore.Save();
    }

    void TabIcons_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TabIconsCombo.SelectedIndex < 0) return;
        S.TabIcons = TabIconChoices[TabIconsCombo.SelectedIndex].Key;
        SettingsStore.Save();
    }

    /// <summary>Effects and videos that start by themselves follow this at once (the game page listens to the policy).</summary>
    void Hardware_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.HardwareAcceleration = HardwareToggle.IsOn;
        S.PauseEffectsInGame = HardwarePauseToggle.IsOn;
        S.BackgroundSaver = BackgroundSaverToggle.IsOn;
        SettingsStore.Save();
        Helpers.MediaPolicy.NotifyEffectsChanged();
    }

    void Window_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CloseToTray = TrayToggle.IsOn;
        S.HideWhilePlaying = HideWhilePlayingToggle.IsOn;
        S.StartOpensWindow = StartOpenToggle.IsOn;
        if (S.StartWithWindows != StartupToggle.IsOn)
        {
            try
            {
                Startup.Set(StartupToggle.IsOn);
                S.StartWithWindows = StartupToggle.IsOn;
            }
            catch (Exception ex)
            {
                App.Window?.ShowError(L.T("Could not change the start with Windows setting"), ex.Message, ex);
                _loading = true;
                StartupToggle.IsOn = S.StartWithWindows;
                _loading = false;
            }
        }
        SettingsStore.Save();
    }

    void Popup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AchievementPopups = PopupToggle.IsOn;
        SettingsStore.Save();
        UpdatePopupOptions();
    }

    void TestPopup_Click(object sender, RoutedEventArgs e) => AchievementPopup.ShowTest();

    /// <summary>Achievements → Sources: a source turned on or off (AchievementExtrasView): what depends on it follows.</summary>
    void OnAchievementSourcesChanged()
    {
        ShowFeatures();
        FillPreviewGames();
    }

    /// <summary>The games of the library that have achievements this PC can read (Steam, Steam emulators, GOG when signed in).</summary>
    void FillPreviewGames()
    {
        var keep = (PreviewGameCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? S.LastGameId;
        PreviewGameCombo.Items.Clear();
        foreach (var g in AchievementWatcher.PreviewGames())
            PreviewGameCombo.Items.Add(new ComboBoxItem { Content = g.Title, Tag = g.Id });
        PreviewGameCombo.SelectedItem = PreviewGameCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == keep)
            ?? PreviewGameCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        bool any = PreviewGameCombo.Items.Count > 0;
        PreviewGameCombo.IsEnabled = PreviewGameButton.IsEnabled = any;
        PreviewGameCombo.PlaceholderText = any ? L.T("Pick a game") : L.T("No game with achievements found");
    }

    /// <summary>One real achievement of the chosen game, announced as an unlock would be (nothing is unlocked).</summary>
    async void PreviewGame_Click(object sender, RoutedEventArgs e)
    {
        if ((PreviewGameCombo.SelectedItem as ComboBoxItem)?.Tag is not string id || AppState.Find(id) is not { } game) return;
        PreviewGameButton.IsEnabled = false;
        try
        {
            if (!await AchievementWatcher.PreviewAsync(game))
                App.Window?.ShowError(L.T("No achievements"), L.F("{0} has no achievements that can be read on this PC.", game.Title));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
        finally { PreviewGameButton.IsEnabled = true; }
    }

    /// <summary>
    /// The picked game's own platinum (its achievement for unlocking all the others) as its unlock would show it; with no game
    /// picked, a sample platinum. Nothing is unlocked.
    /// </summary>
    async void PreviewPlatinum_Click(object sender, RoutedEventArgs e) => await PreviewPlatinumAsync();

    async Task PreviewPlatinumAsync()
    {
        if ((PreviewGameCombo.SelectedItem as ComboBoxItem)?.Tag is not string id || AppState.Find(id) is not { } game)
        {
            AchievementPopup.ShowTestPlatinum();
            return;
        }
        PreviewPlatinumButton.IsEnabled = false;
        try
        {
            if (!await AchievementWatcher.PreviewPlatinumAsync(game))
                App.Window?.ShowError(L.T("No platinum"), L.F("{0} has no achievement for unlocking all the others, so it has no platinum.", game.Title));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
        finally { PreviewPlatinumButton.IsEnabled = true; }
    }

    /// <summary>A progress pop-up of the picked game (a sample progress pop-up with no game picked). Nothing changes.</summary>
    async void PreviewProgress_Click(object sender, RoutedEventArgs e) => await PreviewProgressAsync(true);

    async Task PreviewProgressAsync(bool tell)
    {
        if ((PreviewGameCombo.SelectedItem as ComboBoxItem)?.Tag is not string id || AppState.Find(id) is not { } game)
        {
            AchievementPopup.ShowReplacing(new Toast(L.T("Sample achievement"), L.T("A step towards an achievement"), null, "Hybrid Launcher", 12.5, Progress: "3 / 10"));
            return;
        }
        PreviewProgressButton.IsEnabled = false;
        try
        {
            if (!await AchievementWatcher.PreviewProgressAsync(game) && tell)
                App.Window?.ShowError(L.T("No achievements"), L.F("{0} has no achievements that can be read on this PC.", game.Title));
        }
        catch (Exception ex) { if (tell) App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
        finally { PreviewProgressButton.IsEnabled = true; }
    }

    /// <summary>Self-test hook: Progress in the real-game preview row (no dialog).</summary>
    public Task SelfTestPreviewProgress() => PreviewProgressAsync(false);

    /// <summary>Self-test hook: Platinum in the real-game preview row (the sample when the picked game has no platinum: no dialog).</summary>
    public async Task SelfTestPreviewPlatinum()
    {
        if ((PreviewGameCombo.SelectedItem as ComboBoxItem)?.Tag is string id && AppState.Find(id) is { } game && await AchievementWatcher.PreviewPlatinumAsync(game)) return;
        AchievementPopup.ShowTestPlatinum();
    }

    /// <summary>Self-test hook: the real-game preview with the first game listed; false when no game has readable achievements.</summary>
    public Task<bool> SelfTestPreviewGameAsync()
    {
        FillPreviewGames();
        if ((PreviewGameCombo.SelectedItem as ComboBoxItem)?.Tag is not string id || AppState.Find(id) is not { } game) return Task.FromResult(false);
        return AchievementWatcher.PreviewAsync(game);
    }

    void Tour_Click(object sender, RoutedEventArgs e) => App.Window?.StartTour();

    /// <summary>The picker of the first start, again: what it changes shows here at once (tabs, page parts, pop-ups).</summary>
    void Features_Click(object sender, RoutedEventArgs e) => App.Window?.ShowFeaturePicker(firstStart: false, () =>
    {
        _loading = true;
        PopupToggle.IsOn = S.AchievementPopups;
        _loading = false;
        AchievementExtrasView.Fill(AchievementExtrasPanel);
        FillIconsAndParts();
        FillTabs();
        ShowFeatures();
    });

    void ShowFeatures()
    {
        var on = Features.Current(S);
        FeaturesText.Text = (Features.Matching(on) is { } p ? L.T(p.Name) : L.T("Your own mix")) + " · "
            + (on.Count == 0 ? L.T("only the launcher") : string.Join(", ", Features.All.Where(f => on.Contains(f.Id)).Select(f => L.T(f.Name))))
            + ". " + L.T("What is left out is only hidden, nothing is uninstalled.");
    }

    static void Select(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[0];

    static string TagOf(ComboBox combo, string fallback) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    /// <summary>A new look: kept, and shown at once on a sample so it can be judged.</summary>
    void PopupLook_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.Popup.Look = TagOf(LookCombo, "Hybrid");
        SettingsStore.Save();
        UpdatePopupOptions();
        // The two colour pickers now hold the new look's colours, not the previous look's.
        LoadLookColours();
        AchievementPopup.ShowTest();
    }

    void PopupOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var o = S.Popup;
        o.Corner = TagOf(CornerCombo, "BottomRight");
        o.Screen = TagOf(ScreenCombo, "Game");
        o.Size = TagOf(SizeCombo, "Normal");
        o.Style = TagOf(StyleCombo, "Accent");
        o.ShowRarity = RarityToggle.IsOn;
        SettingsStore.Save();
        UpdatePopupOptions();
    }

    static string Hex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    void PopupColor_Changed(ColorPicker sender, ColorChangedEventArgs e)
    {
        if (_loading) return;
        S.Popup.CustomColor = Hex(e.NewColor);
        SettingsStore.Save();
        UpdatePopupOptions();
    }

    /// <summary>The look whose background and text colours the rows show: the look picked, or for "Match the game's store" the one picked beside them.</summary>
    string ColoursLook => S.Popup.Look == "Auto" ? TagOf(ColoursLookCombo, "Steam") : S.Popup.Look;

    static readonly Windows.UI.Color NoColour = Windows.UI.Color.FromArgb(255, 0x14, 0x12, 0x1A);

    /// <summary>Puts the colours of <see cref="ColoursLook"/> into the two pickers (their change handlers stay quiet).</summary>
    void LoadLookColours()
    {
        bool was = _loading;
        _loading = true;
        var c = S.Popup.LookColours.TryGetValue(ColoursLook, out var lc) ? lc : new PopupLookColours();
        PopupBackPicker.Color = AchievementPopup.TryParseHex(c.Background, out var back) ? back : NoColour;
        PopupTextPicker.Color = AchievementPopup.TryParseHex(c.Text, out var ink) ? ink : Microsoft.UI.Colors.White;
        _loading = was;
    }

    void ColoursLook_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LoadLookColours();
        UpdatePopupOptions();
    }

    void BackgroundColor_Changed(ColorPicker sender, ColorChangedEventArgs e)
    {
        if (_loading) return;
        S.Popup.ColoursOf(ColoursLook).Background = Hex(e.NewColor);
        SettingsStore.Save();
        UpdatePopupOptions();
    }

    void BackgroundAuto_Click(object sender, RoutedEventArgs e)
    {
        S.Popup.ColoursOf(ColoursLook).Background = "";
        SettingsStore.Save();
        LoadLookColours();
        UpdatePopupOptions();
    }

    void TextColor_Changed(ColorPicker sender, ColorChangedEventArgs e)
    {
        if (_loading) return;
        S.Popup.ColoursOf(ColoursLook).Text = Hex(e.NewColor);
        SettingsStore.Save();
        UpdatePopupOptions();
    }

    void TextAuto_Click(object sender, RoutedEventArgs e)
    {
        S.Popup.ColoursOf(ColoursLook).Text = "";
        SettingsStore.Save();
        LoadLookColours();
        UpdatePopupOptions();
    }

    /// <summary>A row a look does not use: greyed and not clickable, so it is clear which options shape the chosen look.</summary>
    static void Usable(bool on, params UIElement[] parts)
    {
        foreach (var p in parts)
        {
            p.Opacity = on ? 1 : 0.4;
            if (p is Control c) c.IsEnabled = on;
            else if (p is Panel panel) foreach (var child in panel.Children.OfType<Control>()) child.IsEnabled = on;
        }
    }

    void Opacity_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (OpacityText is null) return;
        OpacityText.Text = $"{e.NewValue:0}%";
        if (_loading) return;
        S.Popup.Opacity = e.NewValue / 100;
        SettingsStore.Save();
    }

    void PlaceSlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (PlaceXText is null || PlaceYText is null) return;
        PlaceXText.Text = $"{PlaceX.Value:0.#}%";
        PlaceYText.Text = $"{PlaceY.Value:0.#}%";
        if (_loading) return;
        S.Popup.X = PlaceX.Value / 100;
        S.Popup.Y = PlaceY.Value / 100;
        SettingsStore.Save();
        AchievementPopup.MovePlacement();
    }

    /// <summary>A sample pop-up to drag where it should appear; its spot is kept when it is let go.</summary>
    void PlacePopup_Click(object sender, RoutedEventArgs e) => AchievementPopup.Place((x, y) =>
    {
        _loading = true;
        PlaceX.Value = Math.Round(x * 100, 1);
        PlaceY.Value = Math.Round(y * 100, 1);
        _loading = false;
        PlaceXText.Text = $"{PlaceX.Value:0.#}%";
        PlaceYText.Text = $"{PlaceY.Value:0.#}%";
    });

    void Seconds_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (SecondsText is null) return;
        SecondsText.Text = $"{e.NewValue:0.#} s";
        if (_loading) return;
        S.Popup.Seconds = e.NewValue;
        SettingsStore.Save();
    }

    void UpdatePopupOptions()
    {
        var o = S.Popup;
        CustomColorButton.Visibility = o.Style == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        PlacePanel.Visibility = o.Corner == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        if (AchievementPopup.TryParseHex(o.CustomColor, out var custom)) CustomColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(custom);
        var look = o.Look;
        var colours = o.LookColours.TryGetValue(ColoursLook, out var lc) ? lc : new PopupLookColours();
        bool ownBack = AchievementPopup.TryParseHex(colours.Background, out var back);
        bool ownText = AchievementPopup.TryParseHex(colours.Text, out var ink);
        PopupBackSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ownBack ? back : NoColour);
        PopupTextSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ownText ? ink : Microsoft.UI.Colors.White);
        PopupBackSwatch.Opacity = ownBack ? 1 : 0.35;
        PopupTextSwatch.Opacity = ownText ? 1 : 0.35;
        BackgroundAutoButton.IsEnabled = ownBack;
        TextAutoButton.IsEnabled = ownText;
        ColoursLookCombo.Visibility = look == "Auto" ? Visibility.Visible : Visibility.Collapsed;
        string lookName = (LookCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == ColoursLook)?.Content as string) ?? ColoursLook;
        ColoursForText.Text = L.F("Of the {0} look", lookName);
        // Colour (the accent) shapes only Hybrid Launcher and Minimal; the rarity line shows only on the card looks.
        bool accentUsed = look is "Hybrid" or "Minimal" or "Auto";
        Usable(accentUsed, StyleHeader, StyleControls);
        bool rarityUsed = look is "Hybrid" or "Steam" or "Gog" or "Epic" or "Auto";
        Usable(rarityUsed, RarityHeader, RarityToggle);
        PopupOptions.Opacity = PopupLookRow.Opacity = S.AchievementPopups ? 1 : 0.5;
    }

    void Sources_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ScanSteam = SteamCheck.IsChecked == true;
        S.ScanEpic = EpicCheck.IsChecked == true;
        S.ScanGog = GogCheck.IsChecked == true;
        S.ScanXbox = XboxCheck.IsChecked == true;
        S.ScanUbisoft = UbisoftCheck.IsChecked == true;
        S.ScanEa = EaCheck.IsChecked == true;
        S.ScanRockstar = RockstarCheck.IsChecked == true;
        SettingsStore.Save();
        _ = AppState.RescanAsync();
    }

    async void AddLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dir = await Pickers.PickFolderAsync(App.Window!);
        if (dir is null || S.GameLibraries.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
        S.GameLibraries.Add(dir);
        Changed();
    }

    bool _fillingCacheBudget;

    /// <summary>Settings → Storage → Game page cache: the budget choices and the size now (measured in the background).</summary>
    void FillPageCache()
    {
        _fillingCacheBudget = true;
        CacheBudgetCombo.Items.Clear();
        foreach (var gb in CacheJanitor.BudgetChoices) CacheBudgetCombo.Items.Add(L.F("{0} GB", gb));
        int at = Array.IndexOf(CacheJanitor.BudgetChoices, S.CacheBudgetGb);
        CacheBudgetCombo.SelectedIndex = at >= 0 ? at : 1;
        _fillingCacheBudget = false;
        _ = ShowPageCacheSizeAsync();
    }

    async Task ShowPageCacheSizeAsync()
    {
        PageCacheText.Text = L.T("Measuring…");
        try
        {
            long size = await Task.Run(CacheJanitor.Size);
            PageCacheText.Text = L.F("{0} in use now.", DiskUsage.Format(size));
        }
        catch (Exception ex) { PageCacheText.Text = ""; Log.Warn($"Cache size: {ex.Message}"); }
    }

    void CacheBudget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCacheBudget || CacheBudgetCombo.SelectedIndex < 0) return;
        int gb = CacheJanitor.BudgetChoices[CacheBudgetCombo.SelectedIndex];
        if (gb == S.CacheBudgetGb) return;
        S.CacheBudgetGb = gb;
        SettingsStore.Save();
        // A smaller budget takes effect now, not at the next start.
        _ = CacheJanitor.RunAsync().ContinueWith(_ => DispatcherQueue.TryEnqueue(() => _ = ShowPageCacheSizeAsync()), TaskScheduler.Default);
    }

    async void EmptyPageCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            long freed = await CacheJanitor.ClearAsync();
            App.Window?.ShowInfo(L.F("Game page cache emptied: {0} freed. Pages fetch what they show again.", DiskUsage.Format(freed)));
            await ShowPageCacheSizeAsync();
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not empty the cache"), ex.Message, ex); }
    }

    void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(AppInfo.DownloadsDir)) Directory.Delete(d, recursive: true);
            var work = Path.Combine(AppInfo.CacheDir, "work");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not clear the cache"), ex.Message, ex); }
        Fill();
        _ = VersionsList.ReloadAsync();
    }

    void OpenLogs_Click(object sender, RoutedEventArgs e) => GameLauncher.OpenFolder(AppInfo.LogsDir);
    void OpenData_Click(object sender, RoutedEventArgs e) => GameLauncher.OpenFolder(AppInfo.DataRoot);

    static FrameworkElement ItemRow(string text, string action, Action onClick)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button { Content = action };
        button.Click += (_, _) => onClick();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    static TextBlock Muted(string text) => new() { Text = text, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"] };
}
