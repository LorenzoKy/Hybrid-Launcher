using HybridLauncher.Core;
using HybridLauncher.Core.Cloud;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Views;

/// <summary>
/// Settings → Cloud saves: on or off, OneDrive (the OneDrive app's folder), MEGA (signed in from here) and a folder of your choice, one or
/// several at once, how many older copies to keep, and Sync all now. Built in code and made again when something changes.
/// </summary>
public static class CloudSettingsView
{
    public static void Fill(Panel host)
    {
        // The button just clicked leaves with the old rows: the focus must not land in the game list's search box.
        App.Window?.KeepFocusOffSearch();
        host.Children.Clear();
        var s = CloudSaves.Settings;

        host.Children.Add(new TextBlock { Text = L.T("CLOUD SAVES"), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, 0, 0, 4) });
        var intro = new StackPanel { Spacing = 2 };
        intro.Children.Add(new TextBlock { Text = L.T("Sync your saves"), Style = Ui.Style("RowHeader") });
        intro.Children.Add(new TextBlock
        {
            Text = L.T("Before a game starts, newer saves from the cloud come to this PC; after it closes, the saves that changed go to the cloud. Pick OneDrive (one click, through Windows' OneDrive app), sign in to MEGA, or use both: every upload then goes to each. Each game sends its saves, its own settings folder when it has one, and its local achievements when it keeps them. Older copies are kept, and what is here is backed up before anything replaces it."),
            Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap,
        });
        var toggle = new ToggleSwitch { IsOn = s.Enabled, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += (_, _) =>
        {
            s.Enabled = toggle.IsOn;
            SettingsStore.Save();
            Fill(host);
        };
        host.Children.Add(Row(intro, toggle));

        if (!s.Enabled) return;

        host.Children.Add(new TextBlock { Text = L.T("WHERE THE SAVES GO"), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, 16, 0, 4) });
        var list = new StackPanel { Spacing = 4 };
        var theme = (host as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;

        // Use adds the provider to the ones in use, Stop using takes it out: several can be on at once.
        Button Switch(string id, string name)
        {
            bool on = CloudSaves.Uses(id);
            var b = new Button { Content = on ? L.T("Stop using") : L.T("Use"), MinWidth = 96 };
            if (!on) b.Style = Ui.Style("AccentButtonStyle");
            b.Click += (_, _) =>
            {
                CloudSaves.Use(id, !on);
                SettingsStore.Save();
                Fill(host);
                if (!on) App.Window?.ShowSuccess(L.F("Cloud saves go to {0} now.", CloudSaves.StoreName));
            };
            return b;
        }

        // OneDrive: Windows' OneDrive app keeps the folder in sync, so one click picks it; nothing to sign in to here.
        {
            var od = OneDrive.Folder();
            bool chosen = CloudSaves.Uses("onedrive");
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new FontIcon { Glyph = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = "OneDrive", Style = Ui.Style("RowHeader") });
            if (chosen && od is not null) title.Children.Add(Ui.Pill(L.T("In use"), Ui.Tone.Good));
            text.Children.Add(title);
            text.Children.Add(new TextBlock
            {
                Text = od is null
                    ? L.T("The OneDrive app is not set up on this PC: sign in to it with your Microsoft account, then come back here.")
                    : (OneDrive.Email() is { } mail ? L.F("Signed in as {0}", mail) + "  ·  " : "")
                      + L.F("Saves go to \"{0}\" in your OneDrive; Windows' OneDrive app keeps them in sync.", FolderStore.RootName),
                Style = Ui.Style("RowDescription"),
                TextWrapping = TextWrapping.Wrap,
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (od is null)
            {
                var setup = new Button { Content = L.T("Set up OneDrive"), MinWidth = 96 };
                setup.Click += (_, _) =>
                {
                    if (OneDrive.AppPath() is { } exe) Ui.Open(exe);
                    else Ui.Open("https://www.microsoft.com/microsoft-365/onedrive/download");
                };
                actions.Children.Add(setup);
                var again = new Button { Content = L.T("Check again"), MinWidth = 96 };
                again.Click += (_, _) => Fill(host);
                actions.Children.Add(again);
            }
            else
            {
                if (chosen)
                {
                    var open = new Button { Content = L.T("Open folder"), MinWidth = 96 };
                    open.Click += (_, _) => GameLauncher.OpenFolder(Directory.CreateDirectory(Path.Combine(od, FolderStore.RootName)).FullName);
                    actions.Children.Add(open);
                }
                actions.Children.Add(Switch("onedrive", "OneDrive"));
            }
            list.Children.Add(Row(text, actions, compact: true));
        }

        // MEGA: signed in from here, as Steam Cloud is (no app to install). Signing in also picks it.
        void Account(string id, string name, string glyph, bool available, bool signedIn, string? email, string about, Func<Task<string?>> signIn, Func<Task> signOut)
        {
            bool chosen = CloudSaves.Uses(id);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = name, Style = Ui.Style("RowHeader") });
            if (chosen && signedIn) title.Children.Add(Ui.Pill(L.T("In use"), Ui.Tone.Good));
            text.Children.Add(title);
            text.Children.Add(new TextBlock { Text = signedIn ? L.F("Signed in as {0}", email ?? name) : about, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (!signedIn)
            {
                var b = new Button { Content = L.T("Sign in"), Style = Ui.Style("AccentButtonStyle"), MinWidth = 96, IsEnabled = available };
                b.Click += async (_, _) =>
                {
                    b.IsEnabled = false;
                    if (await signIn() is { } who)
                    {
                        CloudSaves.Use(id, true);
                        SettingsStore.Save();
                        App.Window?.ShowSuccess(L.F("Cloud saves go to {0} ({1}) now.", CloudSaves.StoreName, who));
                    }
                    Fill(host);
                };
                actions.Children.Add(b);
            }
            else
            {
                actions.Children.Add(Switch(id, name));
                var outButton = new Button { Content = L.T("Sign out"), MinWidth = 96 };
                outButton.Click += async (_, _) =>
                {
                    outButton.IsEnabled = false;
                    await signOut();
                    Fill(host);
                };
                actions.Children.Add(outButton);
            }
            list.Children.Add(Row(text, actions, compact: true));
        }

        Account("mega", "MEGA", "", true, MegaAuth.SignedIn, MegaAuth.Email,
            L.F("Sign in with your MEGA account in your browser. Saves go to \"{0}\" in your Cloud Drive, end-to-end encrypted.", FolderStore.RootName),
            () => CloudSignIn.MegaAsync(theme), MegaAuth.SignOutAsync);

        // The fallback: any folder (a NAS, a USB drive, a folder another sync app keeps).
        {
            bool chosen = CloudSaves.Uses("folder");
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new FontIcon { Glyph = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = L.T("A folder of your choice"), Style = Ui.Style("RowHeader") });
            if (chosen) title.Children.Add(Ui.Pill(L.T("In use"), Ui.Tone.Good));
            text.Children.Add(title);
            text.Children.Add(new TextBlock { Text = s.Folder is { } f ? f : L.T("A NAS, a USB drive, or a folder another sync app keeps."), Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (s.Folder is { } folder && Directory.Exists(folder)) actions.Children.Add(Switch("folder", L.T("your folder")));
            var pick = new Button { Content = L.T("Choose…"), MinWidth = 96 };
            pick.Click += async (_, _) =>
            {
                if (App.Window is null || await Pickers.PickFolderAsync(App.Window) is not { } dir) return;
                // Google Drive's G:\ takes no files at its root: its "My Drive" folder is used instead.
                dir = FolderStore.Resolve(dir);
                if (!FolderStore.Writable(dir))
                {
                    App.Window.ShowError(L.T("Cloud saves"), L.F("Hybrid Launcher cannot write in \"{0}\": pick a folder inside it.", dir));
                    return;
                }
                CloudSaves.Use("folder", true);
                s.Folder = dir;
                SettingsStore.Save();
                Fill(host);
            };
            actions.Children.Add(pick);
            // Remove: the folder leaves the sync and is forgotten; what is already in it stays there.
            if (s.Folder is not null)
            {
                var remove = new Button { Content = L.T("Remove"), MinWidth = 96 };
                ToolTipService.SetToolTip(remove, L.T("Stop using this folder and forget it. The saves already in it stay there."));
                remove.Click += (_, _) =>
                {
                    CloudSaves.Use("folder", false);
                    s.Folder = null;
                    SettingsStore.Save();
                    Fill(host);
                    App.Window?.ShowSuccess(CloudSaves.Settings.Providers.Count > 0
                        ? L.F("Cloud saves go to {0} now.", CloudSaves.StoreName)
                        : L.T("The folder is no longer used for cloud saves."));
                };
                actions.Children.Add(remove);
            }
            list.Children.Add(Row(text, actions, compact: true));
        }
        host.Children.Add(list);

        host.Children.Add(new TextBlock { Text = L.T("COPIES AND SYNC"), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, 16, 0, 4) });
        var keepText = new StackPanel { Spacing = 1 };
        keepText.Children.Add(new TextBlock { Text = L.T("Older copies per game"), Style = Ui.Style("RowHeader") });
        keepText.Children.Add(new TextBlock { Text = L.T("Each upload keeps the cloud's copy before it; restore one from the game's cloud menu."), Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        var keep = new NumberBox { Value = s.KeepVersions, Minimum = 1, Maximum = 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        keep.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) return;
            s.KeepVersions = (int)Math.Clamp(e.NewValue, 1, 30);
            SettingsStore.Save();
        };
        host.Children.Add(Row(keepText, keep));

        var allText = new StackPanel { Spacing = 1 };
        allText.Children.Add(new TextBlock { Text = L.T("Sync every game now"), Style = Ui.Style("RowHeader") });
        var allState = new TextBlock { Text = L.T("Games that changed on both sides are left for you to decide from their page."), Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap };
        allText.Children.Add(allState);
        var all = new Button { Content = L.T("Sync all now"), MinWidth = 96, IsEnabled = CloudSaves.On };
        all.Click += async (_, _) =>
        {
            all.IsEnabled = false;
            try
            {
                var (up, down, skipped) = await CloudSync.SyncAllAsync(new Progress<string>(n => allState.Text = L.F("Checking {0}…", n)));
                allState.Text = L.F("{0} sent, {1} brought here, {2} to decide.", up, down, skipped);
            }
            catch (Exception ex) { allState.Text = ex.Message; }
            finally { all.IsEnabled = true; }
        };
        host.Children.Add(Row(allText, all));

        // Hybrid Launcher's own settings and numbers, beside the games' saves (2.1-rc.4).
        var configText = new StackPanel { Spacing = 1 };
        configText.Children.Add(new TextBlock { Text = L.T("Hybrid Launcher's settings and stats"), Style = Ui.Style("RowHeader") });
        var configState = new TextBlock
        {
            Text = L.F("Your preferences, play time, sessions, achievement history, profile and the covers you picked go to the cloud as \"{0}\", merged with what your other PCs send: no hours or unlocks are ever lost, and a setting changed on two PCs keeps the newer one. Folders, accounts and installed mods stay per PC.", HybridLauncher.Core.Cloud.ConfigSync.Key)
                + (HybridLauncher.Core.Cloud.ConfigSync.LastSync is { } at ? "  " + L.F("Last synced {0:g}.", at.ToLocalTime()) : ""),
            Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap,
        };
        configText.Children.Add(configState);
        var config = new ToggleSwitch { IsOn = s.SyncConfig, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        config.Toggled += (_, _) =>
        {
            s.SyncConfig = config.IsOn;
            SettingsStore.Save();
            if (config.IsOn && CloudSaves.On) _ = Task.Run(() => HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None));
        };
        host.Children.Add(Row(configText, config));
    }

    static Border Row(FrameworkElement text, FrameworkElement action, bool compact = false)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        var row = new Border { Style = Ui.Style("RowStyle"), Child = grid };
        if (compact) row.MinHeight = 0;
        return row;
    }
}
