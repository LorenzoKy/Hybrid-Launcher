using HybridLauncher.Core;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Helpers;

/// <summary>
/// The app's own updates, on screen: checked a few seconds after start (when Settings → About allows it) and from the About
/// tab; a newer version asks once, in a dialog with its notes, and one click downloads it, closes the app and starts the
/// updated one. Nothing else is asked.
/// </summary>
static class AppUpdater
{
    /// <summary>The newer release found by the last check, for the About tab.</summary>
    public static AppRelease? Available { get; private set; }

    public static event Action? Changed;

    static bool _busy;

    /// <summary>At start: a quiet check; a version the user put off with Later is not asked again.</summary>
    public static async Task CheckAtStartAsync(XamlRoot root)
    {
        if (Environment.GetCommandLineArgs().Contains(AppUpdates.UpdatedSwitch, StringComparer.OrdinalIgnoreCase))
        {
            App.Window?.ShowSuccess(L.F("Updated to {0} {1}.", AppInfo.Name, AppUpdates.Current.ToString(3)));
            _ = Task.Run(async () => { await Task.Delay(5000); AppUpdates.CleanUp(); });
        }
        if (!AppState.Settings.CheckAppUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(8));
        var release = await CheckAsync(quiet: true);
        if (release is null || AppState.Settings.SkippedAppUpdate == release.Version.ToString()) return;
        await OfferAsync(root, release);
    }

    /// <summary>Asks GitHub for a newer version; null when this is the newest. Not quiet: a failure is shown.</summary>
    public static async Task<AppRelease?> CheckAsync(bool quiet)
    {
        try
        {
            Available = await AppUpdates.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check: {ex.Message}");
            if (!quiet) App.Window?.ShowError(L.T("Could not check for updates"), ex.Message);
            return null;
        }
        Changed?.Invoke();
        return Available;
    }

    /// <summary>The one question: update now (download, close, start the new version) or later.</summary>
    public static async Task OfferAsync(XamlRoot root, AppRelease release)
    {
        if (_busy) return;
        var notes = new TextBlock
        {
            Text = release.Notes.Length > 0 ? release.Notes.Trim() : L.T("No notes for this version."),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Style = Ui.Style("RowDescription"),
        };
        var status = new TextBlock { Style = Ui.Style("RowDescription"), Visibility = Visibility.Collapsed };
        var bar = new ProgressBar { Maximum = 1, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Spacing = 10, MaxWidth = 520 };
        body.Children.Add(new TextBlock
        {
            Text = L.F("{0} {1} is out (you have {2}). Update now? The app closes for a moment and opens again by itself; your games, settings and accounts stay as they are.",
                AppInfo.Name, release.Version.ToString(3), AppUpdates.Current.ToString(3)),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 260 });
        body.Children.Add(bar);
        body.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Title = L.T("Update available"),
            Content = body,
            PrimaryButtonText = L.T("Update now"),
            CloseButtonText = L.T("Later"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (d, e) =>
        {
            // The dialog stays open with the download's progress, then the app closes.
            var deferral = e.GetDeferral();
            e.Cancel = true;
            d.IsPrimaryButtonEnabled = false;
            _busy = true;
            bar.Visibility = status.Visibility = Visibility.Visible;
            status.Text = L.T("Downloading the update…");
            try
            {
                var exe = await AppUpdates.DownloadAsync(release, new Progress<double>(p => bar.Value = p), CancellationToken.None);
                status.Text = L.T("Restarting…");
                AppState.Settings.SkippedAppUpdate = null;
                SettingsStore.Save();
                AppUpdates.Start(exe);
                App.Window?.Quit();
            }
            catch (Exception ex)
            {
                Log.Error("Update download failed", ex);
                status.Text = L.F("The update could not be downloaded: {0}", ex.Message);
                d.IsPrimaryButtonEnabled = true;
                _busy = false;
            }
            finally { deferral.Complete(); }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.None && !_busy)
        {
            AppState.Settings.SkippedAppUpdate = release.Version.ToString();
            SettingsStore.Save();
        }
    }
}
