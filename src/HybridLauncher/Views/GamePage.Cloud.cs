using HybridLauncher.Core;
using HybridLauncher.Core.Cloud;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>The launch bar's Cloud button: the game's cloud saves state, and its menu (sync now, older copies, saves folder).</summary>
public sealed partial class GamePage
{
    CloudStatus? _cloud;
    int _cloudAsk;

    void OnCloudChanged(string id)
    {
        if (_game is not null && id.Equals(_game.Id, StringComparison.OrdinalIgnoreCase)) _ = UpdateCloudButtonAsync();
    }

    static readonly Color CloudBlue = Color.FromArgb(0xFF, 0x4F, 0xC3, 0xF7);
    static readonly Color SyncedMint = Color.FromArgb(0xFF, 0x7D, 0xE8, 0xC8);

    async Task UpdateCloudButtonAsync()
    {
        if (_game is null || _game.NotInstalled) return;
        var game = _game;
        var settings = _settings;
        int ask = ++_cloudAsk;
        bool busy = CloudSync.IsBusy(game.Id);
        CloudRing.IsActive = busy;
        CloudRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CloudGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (busy)
        {
            CloudStateText.Text = L.T("Syncing…");
            CloudStateText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            CloudStateText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            return;
        }
        CloudStatus status;
        try
        {
            if (CloudSaves.On && !settings.CloudOff && (settings.CloudSaveDir is null || !settings.CloudConfigChecked)) await CloudSync.PartsAsync(game, settings, _pageCts.Token);
            status = await CloudSaves.StatusAsync(game, settings);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cloud status of {game.Name}: {ex.Message}");
            status = new CloudStatus(CloudState.NoFolder, settings.CloudSaveDir, null, settings.CloudSyncedAt);
        }
        if (ask != _cloudAsk || game != _game) return;
        _cloud = status;
        bool lit = status.State is CloudState.Synced or CloudState.ChangedHere or CloudState.ChangedInCloud or CloudState.NotUploaded;
        CloudGlyph.Glyph = status.State switch
        {
            CloudState.Conflict => "",
            CloudState.Synced => "",
            CloudState.ChangedHere or CloudState.NotUploaded => "",
            CloudState.ChangedInCloud => "",
            _ => "",
        };
        CloudGlyph.Foreground = new SolidColorBrush(status.State == CloudState.Conflict ? Ui.ToneColor(Ui.Tone.Partial) : lit ? CloudBlue : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        CloudStateText.Text = status.State switch
        {
            CloudState.Off when !CloudSaves.On => L.T("Set up"),
            CloudState.Off => L.T("Off"),
            CloudState.NoFolder => L.T("No saves folder"),
            CloudState.Synced => status.Cloud is null ? L.T("Nothing to sync") : L.T("Synced"),
            CloudState.ChangedHere or CloudState.NotUploaded => L.T("Goes up after play"),
            CloudState.ChangedInCloud => L.T("Newer in the cloud"),
            _ => L.T("Choose a copy"),
        };
        CloudStateText.Opacity = lit ? 1 : 0.75;
        // In sync with the cloud: a calm mint "Synced", one solid colour in semibold. Noticeable at a glance next to Smart HDR's
        // sweep without competing with it; every other state stays plain white.
        bool synced = status.State == CloudState.Synced && status.Cloud is not null;
        CloudStateText.Foreground = new SolidColorBrush(synced ? SyncedMint : Microsoft.UI.Colors.White);
        CloudStateText.FontWeight = synced ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        ToolTipService.SetToolTip(CloudButton, !CloudSaves.On
            ? L.T("Cloud saves: sync this game's saves with OneDrive, MEGA or a folder. Set it up in Settings → Cloud saves.")
            : status.Cloud is { } c
            ? L.F("Cloud saves with {0}. Last upload: {1}, from {2}.", CloudSaves.StoreName, c.UpdatedUtc.ToLocalTime().ToString("g"), c.Machine)
            : L.F("Cloud saves with {0}.", CloudSaves.StoreName));
    }

    async void CloudButton_Click(object sender, RoutedEventArgs e)
    {
        if (_game is null) return;
        var game = _game;
        var settings = _settings;
        if (!CloudSaves.On)
        {
            App.Window?.OpenSettings("cloud");
            return;
        }
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var head = new MenuFlyoutItem
        {
            Text = _cloud?.Cloud is { } c ? L.F("Last upload {0} · {1}", c.UpdatedUtc.ToLocalTime().ToString("g"), DiskUsage.Format(c.Bytes)) : L.T("Not in the cloud yet"),
            IsEnabled = false,
        };
        menu.Items.Add(head);
        // What goes to the cloud for this game: the saves, the game's settings, the Steam emulator's achievements.
        var parts = CloudSaves.Parts(game, settings);
        if (parts.Count > 0)
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = L.F("Syncs: {0}", string.Join(", ", parts.Select(p => p.Name switch { "saves" => L.T("saves"), "config" => L.T("game settings"), _ => L.T("emulator achievements") }))),
                IsEnabled = false,
            });
        menu.Items.Add(new MenuFlyoutSeparator());
        if (!settings.CloudOff)
        {
            menu.Items.Add(Item(L.T("Sync now"), "", () => _ = CloudSync.SyncNowAsync(game), parts.Count > 0 &&!GameSessions.IsActive(game.Id)));
            menu.Items.Add(Item(L.T("Use the cloud's saves now"), "", async () =>
            {
                if (!await ConfirmAsync(L.T("Use the cloud's saves?"), L.T("The saves on this PC are replaced by the cloud's copy. They are zipped into Hybrid Launcher's backups first."), L.T("Use the cloud's"))) return;
                await RunCloud(() => CloudSaves.RestoreAsync(game, settings), L.T("The cloud's saves are on this PC now."));
            }, _cloud?.Cloud is not null && !GameSessions.IsActive(game.Id)));
            // The older copies, asked of the provider when the menu opens (a slow answer leaves the list empty, not the menu waiting).
            List<CloudCopy> history = [];
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                history = await CloudSaves.HistoryAsync(game, cts.Token);
            }
            catch (Exception ex) { Log.Warn($"Cloud history of {game.Name}: {ex.Message}"); }
            if (game != _game) return;
            var older = new MenuFlyoutSubItem { Text = L.T("Restore an older copy"), Icon = new FontIcon { Glyph = "" }, IsEnabled = history.Count > 0 && !GameSessions.IsActive(game.Id) };
            foreach (var f in history)
                older.Items.Add(Item($"{f.Name}  ·  {DiskUsage.Format(f.Bytes)}", "", async () =>
                {
                    if (!await ConfirmAsync(L.T("Restore this copy?"), L.F("The saves on this PC are replaced by the copy of {0}. They are zipped into Hybrid Launcher's backups first, and the restored copy goes up after you play.", f.Name), L.T("Restore"))) return;
                    await RunCloud(() => CloudSaves.RestoreAsync(game, settings, f), L.T("The older copy is on this PC now."));
                }));
            menu.Items.Add(older);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(settings.CloudSaveDir is null ? L.T("Choose the saves folder…") : L.T("Change the saves folder…"), "", async () =>
            {
                if (App.Window is null || await Pickers.PickFolderAsync(App.Window) is not { } dir) return;
                if (CloudSaves.TooBroad(dir)) { App.Window.ShowError(L.T("Cloud saves"), L.T("That folder holds much more than one game's saves: pick the game's own saves folder.")); return; }
                settings.CloudSaveDir = dir;
                settings.CloudSaveDirPicked = true;
                settings.CloudSyncedHash = null;
                SettingsStore.Save();
                _ = UpdateCloudButtonAsync();
            }));
            if (settings.CloudSaveDir is { } saves) menu.Items.Add(Item(L.T("Open the saves folder"), "", () => Ui.Safe(() => GameLauncher.OpenFolder(saves)), Directory.Exists(saves)));
            if (parts.FirstOrDefault(p => p.Name == "config") is { } config) menu.Items.Add(Item(L.T("Open the game's settings folder"), "", () => Ui.Safe(() => GameLauncher.OpenFolder(config.Dir))));
            if (parts.FirstOrDefault(p => p.Name == "goldberg") is { } emu) menu.Items.Add(Item(L.T("Open the emulator's achievements folder"), "", () => Ui.Safe(() => GameLauncher.OpenFolder(emu.Dir))));
            if (CloudSaves.GameDir(game) is { } inCloud) menu.Items.Add(Item(L.T("Open the game's cloud folder"), "", () => Ui.Safe(() => GameLauncher.OpenFolder(inCloud)), Directory.Exists(inCloud)));
            var backups = CloudSaves.LocalBackupsDir(game);
            menu.Items.Add(Item(L.T("Open Hybrid Launcher's backups of these saves"), "", () => Ui.Safe(() => GameLauncher.OpenFolder(backups)), Directory.Exists(backups)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(Item(settings.CloudOff ? L.T("Sync this game") : L.T("Leave this game out"), settings.CloudOff ? "" : "", () =>
        {
            settings.CloudOff = !settings.CloudOff;
            SettingsStore.Save();
            _ = UpdateCloudButtonAsync();
        }));
        menu.Items.Add(Item(L.T("Cloud saves settings"), "", () => App.Window?.OpenSettings("cloud")));
        menu.ShowAt(CloudButton);
    }

    static MenuFlyoutItem Item(string text, string glyph, Action click, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    async Task RunCloud(Func<Task> work, string done)
    {
        if (_game is null) return;
        var id = _game.Id;
        CloudSync.Raise(id);
        try
        {
            await work();
            SettingsStore.Save();
            App.Window?.ShowSuccess(done);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Cloud saves"), ex.Message); }
        CloudSync.Raise(id);
    }
}
