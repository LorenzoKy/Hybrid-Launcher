using HybridLauncher.Core;
using HybridLauncher.Core.Opti;
using HybridLauncher.Controls;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Views;

/// <summary>
/// The OptiScaler tab's Custom DLLs card: entries of the shared custom dll folder (see <see cref="CustomDlls"/>), switched on per game,
/// renamed (in Hybrid Launcher only) and deleted for every game, as the ASI plugins are.
/// </summary>
public sealed partial class GamePage
{
    void LoadCustomDllList()
    {
        CustomDllList.Children.Clear();
        var entries = CustomDlls.Entries();
        CustomDllEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCustomDllSummary(entries);
        foreach (var entry in entries)
        {
            var name = entry.Name;
            var toggle = new ToggleSwitch
            {
                IsOn = _settings.CustomDlls.Contains(name),
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Toggled += async (_, _) =>
            {
                await CustomDllToggledAsync(name, toggle.IsOn);
                UpdateCustomDllSummary(CustomDlls.Entries());
            };
            var bin = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["IconButton"] };
            ToolTipService.SetToolTip(bin, L.T("Delete this entry (from the shared folder and from every game that uses it)"));
            bin.Click += async (_, _) => await DeleteCustomDllAsync(name);
            // A name of the user's own, shown in Hybrid Launcher only: the folder keeps its name, which is the one OptiScaler's menu shows.
            bool renamed = AppState.Settings.CustomDllNames.TryGetValue(name, out var custom) && custom.Length > 0;
            var rename = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["IconButton"] };
            ToolTipService.SetToolTip(rename, L.T("Rename (only in Hybrid Launcher: the folder keeps its name, which OptiScaler's menu shows)"));
            rename.Click += async (_, _) =>
            {
                var picked = await NameDialog.AskAsync(XamlRoot, ActualTheme, L.T("Rename this entry"), name, AppState.Settings.CustomDllNames.GetValueOrDefault(name, ""));
                if (picked is null) return;
                if (picked.Length == 0 || picked == name) AppState.Settings.CustomDllNames.Remove(name);
                else AppState.Settings.CustomDllNames[name] = picked;
                SettingsStore.Save();
                LoadCustomDllList();
            };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(toggle);
            controls.Children.Add(rename);
            controls.Children.Add(bin);
            var kinds = string.Join(", ", entry.Kinds.Select(CustomDlls.Label));
            CustomDllList.Children.Add(Row(renamed ? custom! : name, (renamed ? name + "  ·  " : "") + kinds + "  ·  " + DiskUsage.Format(entry.Bytes), controls));
        }
    }

    void UpdateCustomDllSummary(List<CustomDllEntry> entries) =>
        CustomDllSummary.Text = entries.Count == 0 ? L.T("None in the folder") : L.F("{0} of {1} on", entries.Count(e => _settings.CustomDlls.Contains(e.Name)), entries.Count);

    /// <summary>Turns an entry on or off for this game only, and brings its OptiScaler install in line when there is one.</summary>
    async Task CustomDllToggledAsync(string name, bool on)
    {
        if (on) _settings.CustomDlls.Add(name);
        else _settings.CustomDlls.Remove(name);
        SettingsStore.Save();
        var shown = CustomDllShownName(name);
        var errors = await SyncCustomDllsAsync([_settings]);
        if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
        else if (_settings.Install is null) App.Window?.ShowInfo(on ? L.F("{0} will be copied in when OptiScaler is installed in {1}.", shown, _game?.Title) : L.F("{0} is off for {1}.", shown, _game?.Title));
        else App.Window?.ShowSuccess(on ? L.F("{0} added to the OptiScaler install of {1}.", shown, _game?.Title) : L.F("{0} removed from the OptiScaler install of {1}.", shown, _game?.Title));
    }

    static string CustomDllShownName(string name) =>
        AppState.Settings.CustomDllNames.TryGetValue(name, out var own) && own.Length > 0 ? own : name;

    /// <summary>Removes an entry from the shared folder; it first leaves the OptiScaler installs of the games that had it on.</summary>
    async Task DeleteCustomDllAsync(string name)
    {
        if (!await ConfirmAsync(L.F("Delete {0}?", CustomDllShownName(name)),
                L.T("Deletes the entry from Hybrid Launcher's shared custom dlls folder. Every game that has it on loses it from its OptiScaler install."),
                L.T("Delete")))
            return;
        try
        {
            var users = AppState.Settings.Games.Values.Where(g => g.CustomDlls.Contains(name)).ToList();
            foreach (var g in users) g.CustomDlls.Remove(name);
            var errors = await SyncCustomDllsAsync(users);
            await Task.Run(() => CustomDlls.Delete(name));
            AppState.Settings.CustomDllNames.Remove(name);
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

    void CustomDllOpen_Click(object sender, RoutedEventArgs e) => Try(() => GameLauncher.OpenFolder(AppInfo.CustomDllsDir));

    void CustomDllRefresh_Click(object sender, RoutedEventArgs e) => LoadCustomDllList();

    /// <summary>Picked dlls become one entry: the user names it (the name OptiScaler's menu shows) and says what the dlls are.</summary>
    async void CustomDllAddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = await Pickers.PickFilesAsync(App.Window!, ".dll");
            if (files.Count == 0) return;
            if (await AskCustomDllAsync(files, null) is not { } picked) return;
            if (!await ConfirmReplaceCustomDllAsync(picked.Name, picked.Kind)) return;
            await Task.Run(() => CustomDlls.Add(picked.Name, picked.Kind, files));
            await CustomDllAddedAsync(picked.Name);
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not add the custom dlls"), ex.Message);
        }
    }

    /// <summary>
    /// A folder already laid out as OptiScaler wants it (ffx, dlss or dlssg inside) is copied whole under its own name;
    /// a folder of loose dlls is added as the picked files are.
    /// </summary>
    async void CustomDllAddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await Pickers.PickFolderAsync(App.Window!) is not { } folder) return;
            if (CustomDlls.IsLaidOut(folder))
            {
                var name = await NameCustomDllAsync(Path.GetFileName(folder));
                if (name is null) return;
                if (Directory.Exists(Path.Combine(AppInfo.CustomDllsDir, name))
                    && !await ConfirmAsync(L.F("Replace {0}?", name), L.T("An entry with this name is already in the shared folder. Replace it with the folder you picked?"), L.T("Replace")))
                    return;
                await Task.Run(() => CustomDlls.AddFolder(name, folder));
                await CustomDllAddedAsync(name);
                return;
            }
            var dlls = Directory.GetFiles(folder, "*.dll");
            if (dlls.Length == 0)
            {
                App.Window?.ShowError(L.T("No dlls in this folder"), L.T("Pick a folder with dlls in it, or one with ffx, dlss or dlssg folders inside as OptiScaler's custom folder has."));
                return;
            }
            if (await AskCustomDllAsync(dlls, Path.GetFileName(folder)) is not { } picked) return;
            if (!await ConfirmReplaceCustomDllAsync(picked.Name, picked.Kind)) return;
            await Task.Run(() => CustomDlls.Add(picked.Name, picked.Kind, dlls));
            await CustomDllAddedAsync(picked.Name);
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not add the custom dlls"), ex.Message);
        }
    }

    async Task<bool> ConfirmReplaceCustomDllAsync(string name, CustomDllKind kind) =>
        !Directory.Exists(Path.Combine(AppInfo.CustomDllsDir, name, CustomDlls.Folder(kind)))
        || await ConfirmAsync(L.F("Replace {0}?", name), L.F("{0} already has a {1}. Replace it with the dlls you picked?", name, CustomDlls.Label(kind)), L.T("Replace"));

    /// <summary>The entry is in the shared folder: shown, and copied again into the installs of the games that have it on.</summary>
    async Task CustomDllAddedAsync(string name)
    {
        LoadCustomDllList();
        var users = AppState.Settings.Games.Values.Where(g => g.CustomDlls.Contains(name)).ToList();
        var errors = await SyncCustomDllsAsync(users);
        if (errors.Count > 0) App.Window?.ShowError(L.T("Some games were not updated"), string.Join("\n", errors));
        else App.Window?.ShowSuccess(L.F("Added {0}.", name) + " " + L.T("Turn it on for each game that should load it."));
    }

    /// <summary>Asks the entry's name and what the dlls are (guessed from their names); null when cancelled.</summary>
    async Task<(string Name, CustomDllKind Kind)?> AskCustomDllAsync(IReadOnlyList<string> files, string? suggested)
    {
        var names = files.Select(Path.GetFileName).ToList();
        var guess = names.Any(n => n!.Contains("dlssg", StringComparison.OrdinalIgnoreCase)) ? CustomDllKind.Dlssg
            : names.Any(n => n!.StartsWith("nvngx", StringComparison.OrdinalIgnoreCase)) ? CustomDllKind.Dlss
            : CustomDllKind.Ffx;
        var box = new TextBox
        {
            Header = L.T("Name (OptiScaler's menu shows it)"),
            Text = suggested ?? Path.GetFileNameWithoutExtension(files[0]),
            MaxLength = 60,
        };
        var kinds = Enum.GetValues<CustomDllKind>().ToList();
        var combo = new ComboBox { Header = L.T("What the dlls are"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = kinds.Select(CustomDlls.Label).ToList(), SelectedIndex = kinds.IndexOf(guess) };
        var error = new TextBlock { Foreground = Ui.Brush("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, MinWidth = 340, MaxWidth = 420 };
        panel.Children.Add(new TextBlock { Text = string.Join(", ", names), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(combo);
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Upscalers show up in OptiScaler's DX12 upscaler list under this name; DLSSG frame generation as \"Nvngx Replacement (FG Nvngx)\". With more than one dll, OptiScaler loads amd_fidelityfx_upscaler_dx12.dll or nvngx.dll."),
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"],
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.T("Add custom dlls"),
            Content = panel,
            PrimaryButtonText = L.T("Add"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ActualTheme,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (CustomDlls.IsValidName(box.Text.Trim())) return;
            args.Cancel = true;
            error.Text = L.T("Use a name Windows accepts for a folder.");
            error.Visibility = Visibility.Visible;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return (box.Text.Trim(), kinds[Math.Max(0, combo.SelectedIndex)]);
    }

    /// <summary>Asks the name of an entry added from a laid-out folder; null when cancelled.</summary>
    async Task<string?> NameCustomDllAsync(string folderName)
    {
        var suggested = folderName;
        while (true)
        {
            var picked = await NameDialog.AskAsync(XamlRoot, ActualTheme, L.T("Name (OptiScaler's menu shows it)"), folderName, suggested);
            if (picked is null) return null;
            if (picked.Length == 0) return folderName;
            if (CustomDlls.IsValidName(picked)) return picked;
            App.Window?.ShowError(L.T("Not a folder name"), L.T("Use a name Windows accepts for a folder."));
            suggested = picked;
        }
    }
}
