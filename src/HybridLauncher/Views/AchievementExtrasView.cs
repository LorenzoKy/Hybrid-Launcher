using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Achievements;
using HybridLauncher.Core.Steam;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Views;

/// <summary>
/// Settings → Achievements → Sources: every source with its switch (Steam, local achievement files and the folders they are read
/// from, Xbox from the Xbox app on this PC and live with a Microsoft account, GOG), what else happens with an unlock (a screenshot, progress pop-ups,
/// a rumble) and the unlock history with its export. Every feature has its own switch. Built in code, made again on changes.
/// </summary>
public static class AchievementExtrasView
{
    /// <summary>A source was turned on or off: the page's other parts that depend on it (preview games, features) follow.</summary>
    public static event Action? SourcesChanged;

    static bool _foldersOpen;
    public static void Fill(Panel host)
    {
        host.Children.Clear();
        var s = AppState.Settings;
        void Again() { SettingsStore.Save(); Fill(host); }

        // ---- sources: one tile per source, two or more to a line, then the folders local files are read from ----
        host.Children.Add(Header("SOURCES"));
        void Changed() { SettingsStore.Save(); SourcesChanged?.Invoke(); Fill(host); }
        var tiles = new List<FrameworkElement>
        {
            Tile(GameStore.Steam, L.T("Steam"), SteamAchievements.StatsDir is null
                    ? L.T("Steam is not installed on this PC.")
                    : L.T("Read from Steam's own files on this PC: no sign-in."),
                s.ReadSteamAchievements, on => { s.ReadSteamAchievements = on; Changed(); }),
            Tile(null, L.T("Local achievements"), L.F("Games that keep achievements in save files on this PC (Steam emulators). {0} folders read.", AchievementWatcher.LocalFolderCount),
                s.ReadSteamEmulator, on => { s.ReadSteamEmulator = on; AchievementWatcher.RestartLocal(); Changed(); }),
            Tile(GameStore.Xbox, L.T("Xbox achievements"), XboxAchievements.CacheFile is null
                    ? L.T("From the Xbox app on this PC: open it once and sign in there first.")
                    : XboxAccount.SignedIn ? L.F("Live from your Microsoft account ({0}).", XboxAccount.Gamertag ?? "Xbox")
                    : L.T("From the Xbox app on this PC, as fresh as its last look at the game."),
                s.ReadXboxAchievements, on => { s.ReadXboxAchievements = on; Changed(); }),
            Tile(GameStore.Gog, L.T("GOG"), GogAccount.SignedIn
                    ? L.T("From your GOG account, checked while a GOG game runs.")
                    : L.T("Needs your GOG account: sign in under Downloads → Accounts."),
                s.ReadGogAchievements, on => { s.ReadGogAchievements = on; Changed(); }),
            Tile(GameStore.Epic, L.T("Epic Games"), HybridLauncher.Core.Accounts.EpicAccount.SignedIn
                    ? EpicStats.Refused
                        ? L.T("Epic asks you to confirm on its store page first: Confirm on Epic, below.")
                        : L.T("From your Epic Games account, also for games not installed.")
                    : L.T("Needs your Epic Games account: sign in under Downloads → Accounts."),
                s.ReadEpicAchievements, on =>
                {
                    s.ReadEpicAchievements = on;
                    Changed();
                    // Turned on: the counts come in now, not at the next start.
                    if (on && EpicAccount.SignedIn) Library.RefreshAfterEpicChange(host.DispatcherQueue);
                }),
        };
        host.Children.Add(TileGrid(tiles));
        // Epic's check (2.1-rc.11): its store page in a window, where the user answers it; then the reads start again.
        if (s.ReadEpicAchievements && EpicAccount.SignedIn)
        {
            var confirm = new Button { Content = L.T("Confirm on Epic"), VerticalAlignment = VerticalAlignment.Center };
            confirm.Click += async (_, _) =>
            {
                confirm.IsEnabled = false;
                try { await EpicWeb.ShowCheckAsync(host.ActualTheme); }
                finally
                {
                    // Closed with the app: nothing to save or draw again.
                    if (!App.ShuttingDown) { confirm.IsEnabled = true; Changed(); }
                }
            };
            host.Children.Add(Row(Text(L.T("Epic Games check"), EpicStats.Refused
                ? L.T("Epic stopped answering until you confirm on its store page. Open it, answer what Epic asks, then close the window.")
                : L.T("If Epic achievements stop showing, Epic may want you to confirm on its store page: open it here and answer what it asks.")), confirm));
        }

        // The folders local achievement files are read from: the known places on this PC and the user's own, folded to one line.
        var roots = LocalAchievements.Roots();
        var own = s.LocalAchievementFolders;
        int missing = LocalAchievements.KnownPlaces().Select(k => k.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count(r => !Directory.Exists(r));
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0), Visibility = _foldersOpen ? Visibility.Visible : Visibility.Collapsed };
        foreach (var dir in roots.Concat(own.Where(d => !Directory.Exists(d))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool mine = own.Contains(dir, StringComparer.OrdinalIgnoreCase);
            bool there = Directory.Exists(dir);
            var line = new Grid { ColumnSpacing = 6 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var path = new TextBlock
            {
                Text = dir + (mine ? "  ·  " + L.T("added by you") : "") + (there ? "" : "  ·  " + L.T("not found")),
                Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Opacity = there ? 1 : 0.6, IsTextSelectionEnabled = true,
            };
            ToolTipService.SetToolTip(path, dir);
            line.Children.Add(path);
            var open = Small("\uE838", L.T("Open"), there);
            open.Click += (_, _) => { try { Shell.Open(dir); } catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); } };
            Grid.SetColumn(open, 1);
            line.Children.Add(open);
            if (mine)
            {
                var remove = Small("\uE711", L.T("Remove"), true);
                remove.Click += (_, _) => { s.LocalAchievementFolders.Remove(dir); AchievementWatcher.RestartLocal(); Again(); };
                Grid.SetColumn(remove, 2);
                line.Children.Add(remove);
            }
            list.Children.Add(line);
        }
        if (missing > 0)
            list.Children.Add(new TextBlock { Text = L.N(missing, "{0} more known place is read as soon as it appears on this PC.", "{0} more known places are read as soon as they appear on this PC."), Style = Ui.Style("RowDescription"), Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var showFolders = new Button { Content = _foldersOpen ? L.T("Hide") : L.F("Show all ({0})", list.Children.OfType<Grid>().Count()) };
        showFolders.Click += (_, _) =>
        {
            _foldersOpen = !_foldersOpen;
            list.Visibility = _foldersOpen ? Visibility.Visible : Visibility.Collapsed;
            showFolders.Content = _foldersOpen ? L.T("Hide") : L.F("Show all ({0})", list.Children.OfType<Grid>().Count());
        };
        var add = new Button { Content = L.T("Add folder…") };
        add.Click += async (_, _) =>
        {
            if (await Pickers.PickFolderAsync(App.Window!) is not { } dir || s.LocalAchievementFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
            s.LocalAchievementFolders.Add(dir);
            _foldersOpen = true;
            AchievementWatcher.RestartLocal();
            Again();
        };
        actions.Children.Add(showFolders);
        actions.Children.Add(add);
        var foldersText = Text(L.T("Local achievement folders"), L.F("{0} folders on this PC are read now. Add one that holds a folder per game (named by its Steam app id), or a single game's folder with its achievement file.", roots.Count));
        foldersText.Children.Add(list);
        var folderRow = Row(foldersText, actions);
        folderRow.Opacity = s.ReadSteamEmulator ? 1 : 0.5;
        host.Children.Add(folderRow);

        // The Microsoft account for live Xbox achievements: only with Xbox on.
        if (s.ReadXboxAchievements)
        {
            var account = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (XboxAccount.SignedIn)
            {
                var popups = Toggle(s.XboxLivePopups, on => { s.XboxLivePopups = on; SettingsStore.Save(); });
                ToolTipService.SetToolTip(popups, L.T("Show Xbox unlocks with Hybrid Launcher's pop-up (the Xbox Game Bar may show its own too)."));
                var signOut = new Button { Content = L.T("Sign out") };
                signOut.Click += (_, _) => { XboxAccount.SignOut(); Log.Info("Signed out of Xbox"); Again(); };
                account.Children.Add(signOut);
                host.Children.Add(Row(Text(L.T("Xbox pop-ups while playing"), L.T("Show Xbox unlocks with Hybrid Launcher's pop-up (the Xbox Game Bar may show its own too).")), popups));
            }
            else
            {
                var signIn = new Button { Content = L.T("Sign in…"), Style = Ui.Style("AccentButtonStyle") };
                signIn.Click += async (_, _) =>
                {
                    if (await AccountSignIn.XboxAsync(host.ActualTheme) is { } tag) App.Window?.ShowSuccess(L.F("Signed in to Xbox as {0}.", tag));
                    Fill(host);
                };
                account.Children.Add(signIn);
            }
            var who = XboxAccount.SignedIn ? L.F("Signed in as {0}. While an Xbox game runs its achievements are checked every 40 seconds and new unlocks pop up.", XboxAccount.Gamertag ?? "Xbox")
                : L.T("Optional. With your Microsoft account, Xbox achievements are always current and their unlocks pop up while you play. Sign in on Microsoft's own page: Hybrid Launcher never sees your password.");
            host.Children.Add(Row(Text(L.T("Microsoft account for Xbox"), who), account));
        }

        // ---- on unlock ----
        host.Children.Add(Header("ON UNLOCK"));
        host.Children.Add(Row(Text(L.T("Screenshot"), L.F("A picture of the screen the game is on with each unlock, saved in {0} under the game's name.", UnlockExtras.ScreenshotRoot)),
            Toggle(s.AchScreenshot, on => { s.AchScreenshot = on; Again(); })));
        if (s.AchScreenshot)
        {
            host.Children.Add(Row(Text(L.T("With the pop-up"), L.T("The picture shows the pop-up too. Off: it is taken just before the pop-up appears.")),
                Toggle(s.AchScreenshotWithPopup, on => { s.AchScreenshotWithPopup = on; SettingsStore.Save(); })));
            var where = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var open = new Button { Content = L.T("Open") };
            open.Click += (_, _) =>
            {
                try { Directory.CreateDirectory(UnlockExtras.ScreenshotRoot); Shell.Open(UnlockExtras.ScreenshotRoot); }
                catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
            };
            var change = new Button { Content = L.T("Change…") };
            change.Click += async (_, _) =>
            {
                if (await Pickers.PickFolderAsync(App.Window!) is { } dir) { s.AchScreenshotDir = dir; Again(); }
            };
            var reset = new Button { Content = L.T("Default"), IsEnabled = s.AchScreenshotDir.Length > 0 };
            reset.Click += (_, _) => { s.AchScreenshotDir = ""; Again(); };
            where.Children.Add(open);
            where.Children.Add(change);
            where.Children.Add(reset);
            host.Children.Add(Row(Text(L.T("Screenshot folder"), UnlockExtras.ScreenshotRoot), where));
        }

        var step = new ComboBox { MinWidth = 120, IsEnabled = s.AchProgressPopupsOn };
        foreach (var n in new[] { 10, 25, 50 }) step.Items.Add(L.F("Every {0}%", n));
        step.SelectedIndex = s.AchProgressStep switch { 10 => 0, 50 => 2, _ => 1 };
        step.SelectionChanged += (_, _) => { s.AchProgressStep = step.SelectedIndex switch { 0 => 10, 2 => 50, _ => 25 }; SettingsStore.Save(); };
        var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        progress.Children.Add(step);
        progress.Children.Add(Toggle(s.AchProgressPopupsOn, on => { s.AchProgressPopupsOn = on; step.IsEnabled = on; SettingsStore.Save(); }));
        host.Children.Add(Row(Text(L.T("Progress pop-ups"), L.T("An achievement with steps (kill 100 enemies, collect 50 items) shows how far along it is each time it passes the next mark. Local achievements and Xbox give the steps.")), progress));

        var rumble = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var tryRumble = new Button { Content = L.T("Try") };
        tryRumble.Click += (_, _) => UnlockExtras.Rumble();
        rumble.Children.Add(tryRumble);
        rumble.Children.Add(Toggle(s.AchRumble, on => { s.AchRumble = on; SettingsStore.Save(); }));
        host.Children.Add(Row(Text(L.T("Controller vibration"), L.T("A short double rumble on a connected Xbox-compatible controller with each unlock.")), rumble));

        // ---- history ----
        host.Children.Add(Header("UNLOCK HISTORY"));
        var entries = AchievementHistory.All();
        var historyActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var show = new Button { Content = L.T("Show"), IsEnabled = entries.Count > 0 };
        show.Click += async (_, _) => await ShowHistoryAsync(host.XamlRoot, host.ActualTheme);
        var export = new Button { Content = L.T("Export…"), IsEnabled = entries.Count > 0 };
        export.Click += async (_, _) =>
        {
            try
            {
                if (await Pickers.PickSaveAsync(App.Window!, "Unlock history", ("CSV", ".csv"), ("JSON", ".json")) is not { } path) return;
                AchievementHistory.Export(AchievementHistory.All(), path);
                App.Window?.ShowSuccess(L.F("Unlock history saved to {0}.", Path.GetFileName(path)));
            }
            catch (Exception ex) { App.Window?.ShowError(L.T("Export failed"), ex.Message, ex); }
        };
        var clear = new Button { Content = L.T("Clear"), IsEnabled = entries.Count > 0 };
        clear.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = host.XamlRoot, RequestedTheme = host.ActualTheme,
                Title = L.T("Clear the unlock history?"),
                Content = L.F("{0} unlocks are removed from Hybrid Launcher's history. Your achievements and screenshots stay.", entries.Count),
                PrimaryButtonText = L.T("Clear"), CloseButtonText = L.T("Cancel"), DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
            AchievementHistory.Clear();
            Fill(host);
        };
        historyActions.Children.Add(show);
        historyActions.Children.Add(export);
        historyActions.Children.Add(clear);
        historyActions.Children.Add(Toggle(s.AchHistory, on => { s.AchHistory = on; SettingsStore.Save(); }));
        host.Children.Add(Row(Text(L.T("Keep an unlock history"), entries.Count == 0 ? L.T("Every unlock Hybrid Launcher shows is written down with its date, game and screenshot. Nothing yet.")
            : L.F("{0} unlocks so far, the latest {1:d MMM yyyy}. Export it as CSV (a spreadsheet) or JSON.", entries.Count, entries[0].At)), historyActions));
    }

    /// <summary>The history in a dialog: newest first, with the screenshot one click away.</summary>
    static async Task ShowHistoryAsync(XamlRoot root, ElementTheme theme)
    {
        var list = new StackPanel { Spacing = 4 };
        foreach (var e in AchievementHistory.All().Take(400))
        {
            var line = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = e.At.ToString("d MMM yyyy\nHH:mm"), Style = Ui.Style("RowDescription") });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = e.Name, Style = Ui.Style("RowHeader"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = e.Game + (e.Points is > 0 ? $"  ·  {e.Points} G" : "") + (e.Percent is { } p ? "  ·  " + L.F("{0:0.#}% of players", p) : ""), Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            if (e.Screenshot is { } shot && File.Exists(shot))
            {
                var pic = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Padding = new Thickness(8, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(pic, L.T("Open the screenshot"));
                pic.Click += (_, _) => Shell.Open(shot);
                Grid.SetColumn(pic, 2);
                line.Children.Add(pic);
            }
            list.Children.Add(line);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = theme,
            Title = L.T("Unlock history"),
            Content = new ScrollViewer { Content = list, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0) },
            CloseButtonText = L.T("Close"),
        };
        await dialog.ShowOnceAsync();
    }

    /// <summary>A source's tile: its launcher's logo (the trophy for local files), name, one line of where it reads from, its switch.</summary>
    static Border Tile(GameStore? store, string name, string description, bool on, Action<bool> changed)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement logo = store is { } st ? StoreLogos.Tile(st, size: 28) : new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = Ui.Brush("SubtleFillColorSecondaryBrush"),
            Child = new ContentPresenter { Content = Icons.Make(Icons.TrophyGlyph, 15), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        logo.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(logo);
        var text = Text(name, description);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);
        return new Border { Style = Ui.Style("RowStyle"), Child = grid, Opacity = on ? 1 : 0.75 };
    }

    /// <summary>Tiles side by side: two to a line, four on a wide page, one on a narrow one.</summary>
    static FrameworkElement TileGrid(List<FrameworkElement> tiles)
    {
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        void Lay(double width)
        {
            int cols = width >= 1400 ? 4 : width >= 640 ? 2 : 1;
            if (grid.ColumnDefinitions.Count == cols && grid.Children.Count == tiles.Count) return;
            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < (tiles.Count + cols - 1) / cols; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < tiles.Count; i++)
            {
                Grid.SetColumn(tiles[i], i % cols);
                Grid.SetRow(tiles[i], i / cols);
                grid.Children.Add(tiles[i]);
            }
        }
        Lay(1000);
        grid.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) Lay(e.NewSize.Width); };
        return grid;
    }

    static Button Small(string glyph, string tip, bool enabled)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 11 }, Padding = new Thickness(8, 5, 8, 5), IsEnabled = enabled, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    static ToggleSwitch Toggle(bool on, Action<bool> changed)
    {
        var t = new ToggleSwitch { IsOn = on, OnContent = L.T("On"), OffContent = L.T("Off"), MinWidth = 0 };
        t.Toggled += (_, _) => changed(t.IsOn);
        return t;
    }

    static TextBlock Header(string text) =>
        new() { Text = L.T(text), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, 16, 0, 4) };

    static StackPanel Text(string header, string description)
    {
        var p = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new TextBlock { Text = header, Style = Ui.Style("RowHeader") });
        p.Children.Add(new TextBlock { Text = description, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        return p;
    }

    static Border Row(FrameworkElement text, FrameworkElement action)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return new Border { Style = Ui.Style("RowStyle"), Child = grid };
    }
}
