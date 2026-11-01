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

    public SettingsPage()
    {
        InitializeComponent();
        Loc.Apply(this);
        // Read by index: the labels follow the UI language.
        foreach (var v in TabIconChoices) TabIconsCombo.Items.Add(L.T(v.Label));
        foreach (var (_, name) in L.Choices) LanguageCombo.Items.Add(name == "Automatic (Windows)" ? L.T(name) : name);
    }

    static readonly (string Key, string Label)[] TabIconChoices = [("mono", "One colour"), ("color", "Each in its own colour")];

    static AppSettings S => AppState.Settings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadControls();

        DataText.Text = AppInfo.DataRoot;
        AboutText.Text = $"{AppInfo.Name} {AppUpdates.DisplayVersion}" + (Elevation.IsAdmin ? "  " + L.T("(running as administrator)") : "");
        Fill();
        FillTabs();
        ShowFeatures();
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
        AutoUpdateToggle.IsOn = S.AutoUpdateGames;
        AppUpdateToggle.IsOn = S.CheckAppUpdates;
        ShortcutToggle.IsOn = S.ShortcutOnInstall;
        ShowUpdateState();
        HltbToggle.IsOn = S.ShowHltb;
        EmulatorToggle.IsOn = S.ReadSteamEmulator;
        foreach (var mode in ModBackups.Modes) ModBackupsCombo.Items.Add(ModBackups.ModeName(mode));
        ModBackupsCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ModBackups.Modes, S.ModBackups));
        CoverTagsToggle.IsOn = S.ShowCoverTags;
        MetacriticToggle.IsOn = S.ShowMetacritic;
        PaletteCombo.Items.Clear();
        foreach (var p in Palettes.All) PaletteCombo.Items.Add(PaletteItem(p));
        PaletteCombo.SelectedIndex = Array.FindIndex(Palettes.All, p => p.Key == Palettes.Get(S.Palette).Key);
        SteamCheck.IsChecked = S.ScanSteam;
        EpicCheck.IsChecked = S.ScanEpic;
        GogCheck.IsChecked = S.ScanGog;
        XboxCheck.IsChecked = S.ScanXbox;
        TabIconsCombo.SelectedIndex = Math.Max(0, Array.FindIndex(TabIconChoices, c => c.Key == S.TabIcons));
        FillIconsAndParts();
        HardwareToggle.IsOn = S.HardwareAcceleration;
        TrayToggle.IsOn = S.CloseToTray;
        StartupToggle.IsOn = S.StartWithWindows;
        PopupToggle.IsOn = S.AchievementPopups;
        var o = S.Popup;
        Select(CornerCombo, o.Corner);
        Select(ScreenCombo, o.Screen);
        Select(SizeCombo, o.Size);
        Select(StyleCombo, o.Style);
        Select(SoundCombo, o.Sound);
        if (AchievementPopup.TryParseHex(o.CustomColor, out var custom)) CustomColorPicker.Color = custom;
        if (AchievementPopup.TryParseHex(o.Background, out var back)) PopupBackPicker.Color = back;
        else PopupBackPicker.Color = Windows.UI.Color.FromArgb(255, 0x14, 0x12, 0x1A);
        PlaceX.Value = Math.Round(Math.Clamp(o.X, 0, 1) * 100, 1);
        PlaceY.Value = Math.Round(Math.Clamp(o.Y, 0, 1) * 100, 1);
        OpacitySlider.Value = Math.Round(Math.Clamp(o.Opacity, 0.6, 1) * 100);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        SecondsSlider.Value = o.Seconds;
        SecondsText.Text = $"{o.Seconds:0.#} s";
        RarityToggle.IsOn = o.ShowRarity;
        UpdatePopupOptions();
        LoadCustomColors();
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
        var name = _openTab switch { "features" => L.T("Features"), "storage" => L.T("Storage"), _ => L.T("Appearance") };
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
                    "storage" => L.T("Custom builds go back to their default folder (the builds already there are not moved or deleted)."),
                    _ => L.T("Language, theme, colours and tab icons go back to how Hybrid Launcher ships."),
                },
            },
            PrimaryButtonText = L.T("Restore defaults"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
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
                    S.AchievementPopups = d.AchievementPopups;
                    S.Popup = d.Popup;
                    bool rescan = S.ScanSteam != d.ScanSteam || S.ScanEpic != d.ScanEpic || S.ScanGog != d.ScanGog || S.ScanXbox != d.ScanXbox;
                    (S.ScanSteam, S.ScanEpic, S.ScanGog, S.ScanXbox) = (d.ScanSteam, d.ScanEpic, d.ScanGog, d.ScanXbox);
                    S.TabOrder.Clear();
                    S.HiddenTabs.Clear();
                    foreach (var g in S.Games.Values) g.HiddenTabs.Clear();
                    if (rescan) _ = AppState.RescanAsync();
                    break;
                case "storage":
                    S.CustomBuildsDir = d.CustomBuildsDir;
                    break;
                default:
                    S.Language = d.Language;
                    S.Palette = d.Palette;
                    S.Material = d.Material;
                    S.TabIcons = d.TabIcons;
                    S.CustomAccent = d.CustomAccent;
                    S.CustomTint = d.CustomTint;
                    S.CustomBackground = d.CustomBackground;
                    S.CustomBackdrop = d.CustomBackdrop;
                    App.Window?.ApplyTheme(S.Theme);
                    var wanted = L.AutoLanguage;
                    RestartBar.IsOpen = wanted != L.Language;
                    break;
            }
            SettingsStore.Save();
            LoadControls();
            Fill();
            FillTabs();
            App.Window?.ShowSuccess(L.F("{0}: defaults restored.", name));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message); }
    }

    void SettingsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tab) return;
        ShowSettingsTab(tab);
        if (S.SettingsTab != tab)
        {
            S.SettingsTab = tab;
            SettingsStore.Save();
        }
    }

    void ShowSettingsTab(string tab)
    {
        _openTab = tab;
        RestoreDefaultsButton.Visibility = tab is "about" or "cloud" ? Visibility.Collapsed : Visibility.Visible;
        AppearancePanel.Visibility = tab == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        FeaturesPanel.Visibility = tab == "features" ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = tab == "storage" ? Visibility.Visible : Visibility.Collapsed;
        CloudPanel.Visibility = tab == "cloud" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "cloud") CloudSettingsView.Fill(CloudPanel);
        AboutPanel.Visibility = tab == "about" ? Visibility.Visible : Visibility.Collapsed;
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
        foreach (var item in SettingsTabs.Items) SettingsTabs.SelectedItem = item;
        SettingsTabs.SelectedItem = SettingsTabs.Items[0];
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

        CacheText.Text = L.F("Downloaded releases (the newest 3 per channel are kept, in {0}) and the custom builds you imported.", AppInfo.DownloadsDir);

        CustomBuildsText.Text = CustomBuilds.Dir(S) + "\n" + L.T("Each sub-folder (or .zip / .7z) holding an OptiScaler dll is a build you can install from a game's Custom box.");
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

    // ---------- my colours ----------

    DispatcherTimer? _colorTimer;

    void LoadCustomColors()
    {
        AccentPicker.Color = Palettes.ParseHex(S.CustomAccent) ?? Microsoft.UI.Colors.MediumPurple;
        TintPicker.Color = Palettes.ParseHex(S.CustomTint) ?? AccentPicker.Color;
        BackgroundPicker.Color = Palettes.ParseHex(S.CustomBackground) ?? Windows.UI.Color.FromArgb(255, 0x16, 0x12, 0x1F);
        BackdropToggle.IsOn = S.CustomBackdrop;
        UpdateSwatches();
    }

    void UpdateSwatches()
    {
        AccentSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(AccentPicker.Color);
        TintSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(TintPicker.Color);
        BackgroundSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(BackgroundPicker.Color);
        BackgroundPickButton.Opacity = BackdropToggle.IsOn ? 1 : 0.6;
    }

    /// <summary>
    /// Any picker or the background switch: saved at once, switched to "My colours", and applied a moment after the last
    /// change (re-theming the whole window on every drag step would stutter).
    /// </summary>
    void CustomColor_Changed(object sender, object e)
    {
        if (_loading) return;
        S.CustomAccent = Palettes.ToHex(AccentPicker.Color);
        S.CustomTint = Palettes.ToHex(TintPicker.Color);
        S.CustomBackground = Palettes.ToHex(BackgroundPicker.Color);
        S.CustomBackdrop = BackdropToggle.IsOn;
        S.Palette = "Custom";
        UpdateSwatches();
        _colorTimer ??= CreateColorTimer();
        _colorTimer.Stop();
        _colorTimer.Start();
    }

    DispatcherTimer CreateColorTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SettingsStore.Save();
            App.Window?.ApplyTheme(S.Theme);
            _loading = true;
            try
            {
                // The "My colours" entry shows the new accent.
                int index = Array.FindIndex(Palettes.All, p => p.Key == "Custom");
                PaletteCombo.Items[index] = PaletteItem(Palettes.All[index]);
                PaletteCombo.SelectedIndex = index;
            }
            finally { _loading = false; }
        };
        return timer;
    }

    /// <summary>A palette entry: its accent as a swatch, the name and a short description.</summary>
    static ComboBoxItem PaletteItem(Palette p)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
        if (Palettes.Get(p.Key).Accent is { } c) swatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(c);
        else
        {
            swatch.BorderThickness = new Thickness(1.5);
            swatch.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)HybridLauncher.Helpers.ThemeRes.Current["TextFillColorSecondaryBrush"];
        }
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
            UpdateStatusText.Text = L.F("You have {0}. New versions come from GitHub and install with one click.", AppUpdates.Current.ToString(3));
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
            else UpdateStatusText.Text = L.F("You have the newest version ({0}).", AppUpdates.Current.ToString(3));
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

    void CoverTags_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ShowCoverTags = CoverTagsToggle.IsOn;
        SettingsStore.Save();
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
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
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message); }
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

    void ModBackups_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModBackupsCombo.SelectedIndex < 0) return;
        S.ModBackups = ModBackups.Modes[ModBackupsCombo.SelectedIndex];
        SettingsStore.Save();
    }

    void Emulator_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ReadSteamEmulator = EmulatorToggle.IsOn;
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
        SettingsStore.Save();
        Helpers.MediaPolicy.NotifyEffectsChanged();
    }

    void Window_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CloseToTray = TrayToggle.IsOn;
        if (S.StartWithWindows != StartupToggle.IsOn)
        {
            try
            {
                Startup.Set(StartupToggle.IsOn);
                S.StartWithWindows = StartupToggle.IsOn;
            }
            catch (Exception ex)
            {
                App.Window?.ShowError(L.T("Could not change the start with Windows setting"), ex.Message);
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

    void Tour_Click(object sender, RoutedEventArgs e) => App.Window?.StartTour();

    /// <summary>The picker of the first start, again: what it changes shows here at once (tabs, page parts, pop-ups).</summary>
    void Features_Click(object sender, RoutedEventArgs e) => App.Window?.ShowFeaturePicker(firstStart: false, () =>
    {
        _loading = true;
        PopupToggle.IsOn = S.AchievementPopups;
        _loading = false;
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

    void PopupOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var o = S.Popup;
        o.Corner = TagOf(CornerCombo, "BottomRight");
        o.Screen = TagOf(ScreenCombo, "Game");
        o.Size = TagOf(SizeCombo, "Normal");
        o.Style = TagOf(StyleCombo, "Accent");
        o.Sound = TagOf(SoundCombo, "Windows");
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

    void BackgroundColor_Changed(ColorPicker sender, ColorChangedEventArgs e)
    {
        if (_loading) return;
        S.Popup.Background = Hex(e.NewColor);
        SettingsStore.Save();
        UpdatePopupOptions();
    }

    void BackgroundAuto_Click(object sender, RoutedEventArgs e)
    {
        S.Popup.Background = "";
        SettingsStore.Save();
        UpdatePopupOptions();
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

    async void SoundFile_Click(object sender, RoutedEventArgs e)
    {
        var file = await Pickers.PickFileAsync(App.Window!, ".wav");
        if (file is null) return;
        S.Popup.SoundFile = file;
        SettingsStore.Save();
        UpdatePopupOptions();
    }

    void UpdatePopupOptions()
    {
        var o = S.Popup;
        CustomColorButton.Visibility = o.Style == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        PlacePanel.Visibility = o.Corner == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        if (AchievementPopup.TryParseHex(o.CustomColor, out var custom)) CustomColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(custom);
        PopupBackSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(AchievementPopup.TryParseHex(o.Background, out var back) ? back : Windows.UI.Color.FromArgb(255, 0x14, 0x12, 0x1A));
        BackgroundAutoButton.IsEnabled = o.Background.Length > 0;
        SoundFileButton.Visibility = o.Sound == "File" ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(SoundFileButton, o.SoundFile ?? L.T("No file picked yet"));
        PopupOptions.Opacity = S.AchievementPopups ? 1 : 0.5;
    }

    void Sources_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ScanSteam = SteamCheck.IsChecked == true;
        S.ScanEpic = EpicCheck.IsChecked == true;
        S.ScanGog = GogCheck.IsChecked == true;
        S.ScanXbox = XboxCheck.IsChecked == true;
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

    void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(AppInfo.DownloadsDir)) Directory.Delete(d, recursive: true);
            var work = Path.Combine(AppInfo.CacheDir, "work");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not clear the cache"), ex.Message); }
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
