using HybridLauncher.Core;
using HybridLauncher.Core.Opti;
using HybridLauncher.Controls;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Views;

/// <summary>
/// The OptiScaler tab's Custom DLLs card: the entries of the shared custom dll folder (see <see cref="CustomDlls"/>), each a group
/// of dlls under one name (the name OptiScaler's menu shows), switched on per game. Dlls can be added to any entry, renamed,
/// given another kind or moved to another entry; entries can be renamed (the folder itself) and deleted, for every game at once.
/// </summary>
public sealed partial class GamePage
{
    static Style Res(string key) => (Style)HybridLauncher.Helpers.ThemeRes.Current[key];

    static Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 13 }, Style = Res("IconButton") };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    void LoadCustomDllList()
    {
        CustomDllList.Children.Clear();
        var entries = CustomDlls.Entries();
        CustomDllEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCustomDllSummary(entries);
        foreach (var entry in entries) CustomDllList.Children.Add(CustomDllEntryView(entry, entries));
    }

    /// <summary>One entry: its name, kinds and switch on top, then each of its dlls with what OptiScaler does with it.</summary>
    Border CustomDllEntryView(CustomDllEntry entry, List<CustomDllEntry> all)
    {
        var name = entry.Name;
        var toggle = new ToggleSwitch { IsOn = _settings.CustomDlls.Contains(name), OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(toggle, L.T("On for this game: copied into its OptiScaler folder"));
        toggle.Toggled += async (_, _) =>
        {
            await CustomDllToggledAsync(name, toggle.IsOn);
            UpdateCustomDllSummary(CustomDlls.Entries());
        };

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(toggle);
        controls.Children.Add(IconButton("", L.T("Add dlls to this entry"), () => _ = AddCustomDllFilesAsync(name)));
        controls.Children.Add(IconButton("", L.T("Rename the entry (the name OptiScaler's menu shows)"), () => _ = RenameCustomDllEntryAsync(name)));
        controls.Children.Add(IconButton("", L.T("Delete the entry (from the shared folder and from every game that uses it)"), () => _ = DeleteCustomDllAsync(name)));

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = name, Style = Res("RowHeader"), TextTrimming = TextTrimming.CharacterEllipsis });
        title.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", entry.Kinds.Select(CustomDlls.MenuPlace).Distinct()) + "  ·  " + DiskUsage.Format(entry.Bytes),
            Style = Res("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        ToolTipService.SetToolTip(title, L.F("OptiScaler lists it as \"{0}\" in: {1}", name, string.Join(", ", entry.Kinds.Select(CustomDlls.MenuPlace).Distinct())));

        // An entry is a folder in OptiScaler's custom folder: the same yellow folder as the launch bar's game folder button.
        var folder = new FontIcon { Glyph = "", FontSize = 18, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC8, 0x57)), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(folder, L.T("An entry is a folder in OptiScaler's custom folder"));

        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(folder);
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        Grid.SetColumn(controls, 2);
        head.Children.Add(controls);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(head);
        foreach (var group in entry.Files.GroupBy(f => f.Kind))
        {
            body.Children.Add(new TextBlock
            {
                Text = CustomDlls.Label(group.Key) + "  ·  " + CustomDlls.Folder(group.Key),
                Style = Res("RowDescription"), Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0),
            });
            foreach (var file in group) body.Children.Add(CustomDllFileView(entry, file, all));
        }
        return new Border { Style = Res("RowStyle"), Child = body };
    }

    /// <summary>A dll of an entry: its name (the one OptiScaler loads is marked) and a menu to rename, change kind, move or delete it.</summary>
    Grid CustomDllFileView(CustomDllEntry owner, CustomDllFile file, List<CustomDllEntry> all)
    {
        var entry = owner.Name;
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new FontIcon { Glyph = file.Loaded ? "" : "", FontSize = 12, Opacity = file.Loaded ? 1 : 0.55 });
        text.Children.Add(new TextBlock { Text = file.FileName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = DiskUsage.Format(file.Bytes), Style = Res("RowDescription"), VerticalAlignment = VerticalAlignment.Center });
        bool isDll = file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var loaded = CustomDlls.LoadedName(file.Kind, owner.Files.Where(f => f.Kind == file.Kind).Select(f => f.FileName));
        ToolTipService.SetToolTip(text, file.Loaded ? L.T("OptiScaler loads this dll")
            : isDll ? L.F("OptiScaler loads {0} from this folder; this one is there for it to load", loaded)
            : L.T("Not a dll: copied along with the others"));

        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var rename = new MenuFlyoutItem { Text = L.T("Rename the file…"), Icon = new FontIcon { Glyph = "" } };
        rename.Click += (_, _) => _ = RenameCustomDllFileAsync(entry, file);
        menu.Items.Add(rename);

        var kinds = new MenuFlyoutSubItem { Text = L.T("Kind"), Icon = new FontIcon { Glyph = "" } };
        foreach (var kind in CustomDlls.Kinds)
        {
            var item = new ToggleMenuFlyoutItem { Text = CustomDlls.Label(kind), IsChecked = kind == file.Kind };
            var k = kind;
            item.Click += (_, _) => _ = MoveCustomDllFileAsync(entry, file, entry, k, file.FileName);
            kinds.Items.Add(item);
        }
        menu.Items.Add(kinds);

        var move = new MenuFlyoutSubItem { Text = L.T("Move to"), Icon = new FontIcon { Glyph = "" } };
        foreach (var other in all.Where(e => e.Name != entry))
        {
            var item = new MenuFlyoutItem { Text = other.Name };
            var target = other.Name;
            item.Click += (_, _) => _ = MoveCustomDllFileAsync(entry, file, target, file.Kind, file.FileName);
            move.Items.Add(item);
        }
        if (move.Items.Count > 0) move.Items.Add(new MenuFlyoutSeparator());
        var fresh = new MenuFlyoutItem { Text = L.T("A new entry…"), Icon = new FontIcon { Glyph = "" } };
        fresh.Click += async (_, _) =>
        {
            if (await AskCustomDllNameAsync(L.T("New entry"), "", isFile: false) is { } target)
                await MoveCustomDllFileAsync(entry, file, target, file.Kind, file.FileName);
        };
        move.Items.Add(fresh);
        menu.Items.Add(move);

        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = L.T("Delete the file"), Icon = new FontIcon { Glyph = "" } };
        delete.Click += (_, _) => _ = DeleteCustomDllFileAsync(entry, file);
        menu.Items.Add(delete);

        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = Res("IconButton"), Flyout = menu };
        ToolTipService.SetToolTip(more, L.T("Rename, change kind, move to another entry or delete"));

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(more, 1);
        grid.Children.Add(more);
        return grid;
    }

    void UpdateCustomDllSummary(List<CustomDllEntry> entries) =>
        CustomDllSummary.Text = entries.Count == 0 ? L.T("None in the folder") : L.F("{0} of {1} on", entries.Count(e => _settings.CustomDlls.Contains(e.Name)), entries.Count);

    /// <summary>Turns an entry on or off for this game only, and brings its OptiScaler install in line when there is one.</summary>
    async Task CustomDllToggledAsync(string name, bool on)
    {
        if (on) _settings.CustomDlls.Add(name);
        else _settings.CustomDlls.Remove(name);
        SettingsStore.Save();
        var errors = await SyncCustomDllsAsync([_settings]);
        if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
        else if (_settings.Install is null) App.Window?.ShowInfo(on ? L.F("{0} will be copied in when OptiScaler is installed in {1}.", name, _game?.Title) : L.F("{0} is off for {1}.", name, _game?.Title));
        else App.Window?.ShowSuccess(on ? L.F("{0} added to the OptiScaler install of {1}.", name, _game?.Title) : L.F("{0} removed from the OptiScaler install of {1}.", name, _game?.Title));
    }

    /// <summary>Copies the custom dlls of the games using any of <paramref name="names"/> again (all their entries are brought in line).</summary>
    static Task<List<string>> SyncCustomDllUsersAsync(params string[] names) =>
        SyncCustomDllsAsync(AppState.Settings.Games.Values.Where(g => names.Any(g.CustomDlls.Contains)).ToList());

    /// <summary>Copies each game's custom dlls into its OptiScaler install and takes out the ones turned off. Returns the failures.</summary>
    static async Task<List<string>> SyncCustomDllsAsync(IEnumerable<GameSettings> games)
    {
        var work = games.Where(g => g.Install is not null).Select(g => (g.Install!, AppState.EnabledCustomDlls(g))).ToList();
        if (work.Count == 0) return [];
        var errors = await Task.Run(() =>
        {
            var errs = new List<string>();
            foreach (var (m, enabled) in work)
            {
                try
                {
                    OptiInstaller.EnsureNotRunning(m.TargetDir);
                    CustomDlls.Sync(m, enabled);
                }
                catch (Exception ex) { errs.Add($"{Path.GetFileName(m.TargetDir)}: {ex.Message}"); }
            }
            return errs;
        });
        SettingsStore.Save();
        return errors;
    }

    /// <summary>Runs a change to the shared folder, then brings the games using the touched entries in line and redraws the card.</summary>
    async Task ChangeCustomDllsAsync(Action change, string done, params string[] touched)
    {
        try
        {
            await Task.Run(change);
            var errors = await SyncCustomDllUsersAsync(touched);
            if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
            else App.Window?.ShowSuccess(done);
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not change the custom dlls"), ex.Message);
        }
        LoadCustomDllList();
    }

    /// <summary>Removes an entry from the shared folder; it first leaves the OptiScaler installs of the games that had it on.</summary>
    async Task DeleteCustomDllAsync(string name)
    {
        if (!await ConfirmAsync(L.F("Delete {0}?", name),
                L.T("Deletes the entry from Hybrid Launcher's shared custom dlls folder. Every game that has it on loses it from its OptiScaler install."),
                L.T("Delete")))
            return;
        try
        {
            var users = AppState.Settings.Games.Values.Where(g => g.CustomDlls.Contains(name)).ToList();
            foreach (var g in users) g.CustomDlls.Remove(name);
            var errors = await SyncCustomDllsAsync(users);
            await Task.Run(() => CustomDlls.Delete(name));
            SettingsStore.Save();
            if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
            else App.Window?.ShowSuccess(L.F("{0} deleted.", name));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not delete the entry"), ex.Message);
        }
        LoadCustomDllList();
    }

    /// <summary>
    /// Renames an entry's folder, so OptiScaler's menu shows the new name. Games that have it on follow: their copy is renamed,
    /// and OptiScaler.ini settings that picked it by the old name (and Hybrid Launcher's saved ones) take the new one.
    /// </summary>
    async Task RenameCustomDllEntryAsync(string name)
    {
        if (await AskCustomDllNameAsync(L.T("Rename the entry"), name, isFile: false) is not { } to || to == name) return;
        if (!to.Equals(name, StringComparison.OrdinalIgnoreCase) && CustomDlls.Exists(to))
        {
            App.Window?.ShowError(L.T("That name is taken"), L.F("There is already an entry called {0}. Move the dlls into it instead, from each dll's menu.", to));
            return;
        }
        try
        {
            await Task.Run(() => CustomDlls.RenameEntry(name, to));
            var users = AppState.Settings.Games.Values.Where(g => g.CustomDlls.Contains(name)).ToList();
            foreach (var g in users)
            {
                g.CustomDlls.Remove(name);
                g.CustomDlls.Add(to);
                foreach (var key in CustomDlls.IniKeys)
                    if (g.IniOverrides.TryGetValue(key, out var v) && v.Equals(name, StringComparison.OrdinalIgnoreCase)) g.IniOverrides[key] = to;
            }
            var errors = await SyncCustomDllsAsync(users);
            await Task.Run(() =>
            {
                foreach (var g in users.Where(g => g.Install is not null))
                {
                    try { CustomDlls.RenameInIni(g.Install!.TargetDir, name, to); }
                    catch (Exception ex) { errors.Add($"{Path.GetFileName(g.Install!.TargetDir)}: {ex.Message}"); }
                }
            });
            SettingsStore.Save();
            if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
            else App.Window?.ShowSuccess(L.F("{0} is now {1}.", name, to));
            _iniStale = true;
            if (IniOpen) LoadIni();
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not rename the entry"), ex.Message);
        }
        LoadCustomDllList();
    }

    async Task RenameCustomDllFileAsync(string entry, CustomDllFile file)
    {
        if (await AskCustomDllNameAsync(L.T("Rename the file"), file.FileName, isFile: true) is not { } to || to == file.FileName) return;
        if (!to.Equals(file.FileName, StringComparison.OrdinalIgnoreCase) && CustomDlls.FileExists(entry, file.Kind, to)
            && !await ConfirmAsync(L.F("Replace {0}?", to), L.T("A file with this name is already in that folder. Replace it?"), L.T("Replace")))
            return;
        await ChangeCustomDllsAsync(() => CustomDlls.MoveFile(entry, file.Kind, file.FileName, entry, file.Kind, to), L.F("{0} is now {1}.", file.FileName, to), entry);
    }

    async Task MoveCustomDllFileAsync(string entry, CustomDllFile file, string toEntry, CustomDllKind toKind, string toName)
    {
        if (entry == toEntry && toKind == file.Kind && toName == file.FileName) return;
        if (CustomDlls.FileExists(toEntry, toKind, toName)
            && !await ConfirmAsync(L.F("Replace {0}?", toName), L.T("A file with this name is already in that folder. Replace it?"), L.T("Replace")))
            return;
        await ChangeCustomDllsAsync(() => CustomDlls.MoveFile(entry, file.Kind, file.FileName, toEntry, toKind, toName),
            L.F("{0} moved to {1} ({2}).", file.FileName, toEntry, CustomDlls.Label(toKind)), entry, toEntry);
    }

    async Task DeleteCustomDllFileAsync(string entry, CustomDllFile file)
    {
        if (!await ConfirmAsync(L.F("Delete {0}?", file.FileName),
                L.F("Deletes it from {0} in the shared folder and from every game that has {0} on.", entry), L.T("Delete")))
            return;
        await ChangeCustomDllsAsync(() => CustomDlls.DeleteFile(entry, file.Kind, file.FileName), L.F("{0} deleted.", file.FileName), entry);
    }

    void CustomDllOpen_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenFolder(AppInfo.CustomDllsDir));

    void CustomDllRefresh_Click(object sender, RoutedEventArgs e) => LoadCustomDllList();

    void CustomDllAddFiles_Click(object sender, RoutedEventArgs e) => _ = AddCustomDllFilesAsync(null);

    /// <summary>Picked dlls go into an entry (a new one or <paramref name="entry"/>), each with the name and kind the user sets.</summary>
    async Task AddCustomDllFilesAsync(string? entry)
    {
        try
        {
            var files = await Pickers.PickFilesAsync(App.Window!, ".dll");
            if (files.Count == 0) return;
            await AddCustomDllsAsync(files, entry ?? "");
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not add the custom dlls"), ex.Message);
        }
    }

    /// <summary>
    /// A folder already laid out as OptiScaler wants it (ffx, ffx_vk, dlss or dlssg inside) becomes an entry as it is; a folder of
    /// loose dlls (like an old "Extra dlls\4.0.3" folder) is added as picked files are, named after the folder.
    /// </summary>
    async void CustomDllAddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await Pickers.PickFolderAsync(App.Window!) is not { } folder) return;
            if (CustomDlls.IsLaidOut(folder))
            {
                if (await AskCustomDllNameAsync(L.T("Name the entry"), Path.GetFileName(folder), isFile: false) is not { } name) return;
                if (CustomDlls.Exists(name)
                    && !await ConfirmAsync(L.F("Add to {0}?", name), L.T("An entry with this name is already in the shared folder. Its dlls are added to it, replacing files with the same name."), L.T("Add")))
                    return;
                await ChangeCustomDllsAsync(() => CustomDlls.AddFolder(name, folder), L.F("Added {0}.", name) + " " + L.T("Turn it on for each game that should load it."), name);
                return;
            }
            var dlls = Directory.GetFiles(folder, "*.dll");
            if (dlls.Length == 0)
            {
                App.Window?.ShowError(L.T("No dlls in this folder"), L.T("Pick a folder with dlls in it, or one with ffx, ffx_vk, dlss or dlssg folders inside as OptiScaler's custom folder has."));
                return;
            }
            await AddCustomDllsAsync(dlls, Path.GetFileName(folder));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not add the custom dlls"), ex.Message);
        }
    }

    /// <summary>The add dialog: the entry (new or existing) and, for each dll, its file name and kind. Then copies them in.</summary>
    async Task AddCustomDllsAsync(IReadOnlyList<string> files, string suggested)
    {
        var existing = CustomDlls.Entries().Select(e => e.Name).ToList();
        var entryBox = new ComboBox
        {
            Header = L.T("Entry (OptiScaler's menu shows this name)"),
            IsEditable = true,
            ItemsSource = existing,
            Text = suggested,
            PlaceholderText = L.T("A new name, or pick an entry to add to"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var rows = new List<(string Source, TextBox Name, ComboBox Kind)>();
        var list = new StackPanel { Spacing = 8 };
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var nameBox = new TextBox { Text = fileName, MinWidth = 200 };
            ToolTipService.SetToolTip(nameBox, L.F("Picked: {0}", file));
            var kindBox = new ComboBox { ItemsSource = CustomDlls.Kinds.Select(CustomDlls.Label).ToList(), SelectedIndex = Array.IndexOf(CustomDlls.Kinds, CustomDlls.Guess(fileName)), MinWidth = 200 };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(nameBox);
            Grid.SetColumn(kindBox, 1);
            row.Children.Add(kindBox);
            list.Children.Add(row);
            rows.Add((file, nameBox, kindBox));
        }
        var error = new TextBlock { Foreground = Ui.Brush("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, MinWidth = 460, MaxWidth = 560 };
        panel.Children.Add(entryBox);
        panel.Children.Add(new TextBlock { Text = L.T("Dlls: the name each gets and what it is"), Style = Res("RowDescription") });
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock
        {
            Text = L.T("One entry can hold an FFX upscaler for DX12 and Vulkan, a DLSS runtime and a DLSSG replacement: OptiScaler lists each under the entry's name. A dll can have any name; when a folder holds more than one, OptiScaler loads the standard name (amd_fidelityfx_upscaler_dx12.dll, amd_fidelityfx_upscaler_vk.dll, nvngx.dll, nvngx_dlssg.dll) and the others are there for it to load."),
            Style = Res("RowDescription"), TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.T("Add custom dlls"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = L.T("Add"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ActualTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        string EntryText() => (entryBox.SelectedItem as string ?? entryBox.Text ?? "").Trim();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? problem = !CustomDlls.IsValidName(EntryText()) ? L.T("Give the entry a name Windows accepts for a folder.")
                : rows.Where(r => !CustomDlls.IsValidDllName(r.Name.Text.Trim())).Select(r => r.Name.Text).FirstOrDefault() is { } bad ? L.F("\"{0}\" is not a dll file name.", bad)
                : rows.GroupBy(r => (r.Kind.SelectedIndex, r.Name.Text.Trim().ToLowerInvariant())).Any(g => g.Count() > 1) ? L.T("Two dlls of the same kind have the same name.")
                : null;
            if (problem is null) return;
            args.Cancel = true;
            error.Text = problem;
            error.Visibility = Visibility.Visible;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var entry = existing.FirstOrDefault(e => e.Equals(EntryText(), StringComparison.OrdinalIgnoreCase)) ?? EntryText();
        var picked = rows.Select(r => (r.Source, Name: r.Name.Text.Trim(), Kind: CustomDlls.Kinds[Math.Max(0, r.Kind.SelectedIndex)])).ToList();
        var clashes = picked.Where(p => CustomDlls.FileExists(entry, p.Kind, p.Name)).Select(p => p.Name).ToList();
        if (clashes.Count > 0 && !await ConfirmAsync(L.T("Replace files?"), L.F("{0} already has: {1}. Replace them?", entry, string.Join(", ", clashes)), L.T("Replace")))
            return;
        await ChangeCustomDllsAsync(() => { foreach (var p in picked) CustomDlls.AddFile(entry, p.Kind, p.Source, p.Name); },
            L.F("Added to {0}: {1}.", entry, string.Join(", ", picked.Select(p => p.Name))) + " " + L.T("Turn it on for each game that should load it."), entry);
    }

    /// <summary>Asks a name for an entry (a folder name) or a dll file; null when cancelled or emptied.</summary>
    async Task<string?> AskCustomDllNameAsync(string title, string current, bool isFile)
    {
        var suggested = current;
        while (true)
        {
            var picked = await NameDialog.AskAsync(XamlRoot, ActualTheme, title, current, suggested);
            if (picked is null || picked.Length == 0) return null;
            if (isFile ? CustomDlls.IsValidDllName(picked) : CustomDlls.IsValidName(picked)) return picked;
            App.Window?.ShowError(L.T("Not a valid name"), isFile ? L.T("A dll's name must end in .dll and be one Windows accepts for a file.") : L.T("Use a name Windows accepts for a folder."));
            suggested = picked;
        }
    }
}
