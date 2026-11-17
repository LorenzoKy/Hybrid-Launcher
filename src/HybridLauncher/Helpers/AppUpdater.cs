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
            App.Window?.ShowSuccess(L.F("Updated to {0} {1}.", AppInfo.Name, AppUpdates.FriendlyVersion));
            _ = Task.Run(async () => { await Task.Delay(5000); AppUpdates.CleanUp(); });
        }
        await Task.Delay(TimeSpan.FromSeconds(8));
        // 2.3: not only at start. A Hybrid Launcher left open for days (in the tray) looks again every six hours on the clock,
        // and when the window comes back after an hour; each version is offered once.
        Version? offered = null;
        while (true)
        {
            if (AppState.Settings.CheckAppUpdates && !_busy)
            {
                var release = await CheckAsync(quiet: true);
                Log.Info($"App update check: {(release is null ? "up to date" : $"{release.Version} available")} (this is {AppUpdates.DisplayVersion})");
                if (release is not null && release.Version != offered && AppState.Settings.SkippedAppUpdate != release.Version.ToString() && !DialogGuard.Busy)
                {
                    offered = release.Version;
                    await OfferAsync(root, release);
                }
            }
            try { await WallClock.WaitAsync(DateTime.UtcNow, TimeSpan.FromHours(6), TimeSpan.FromHours(1), CancellationToken.None); }
            catch (OperationCanceledException) { return; }
        }
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
            if (!quiet) App.Window?.ShowError(L.T("Could not check for updates"), ex.Message, ex);
            return null;
        }
        Changed?.Invoke();
        return Available;
    }

    /// <summary>The one question: update now (download, close, start the new version) or later.</summary>
    public static async Task OfferAsync(XamlRoot root, AppRelease release)
    {
        if (_busy) return;
        // 2.3: the notes as GitHub shows them, easy to read: the version in large type with the one installed beside it, a
        // count of each section's changes, then the changelog itself with its sections set apart, its lists and links, in a
        // pane that scrolls on its own. The download shows its megabytes.
        double width = Math.Clamp(root.Size.Width - 160, 420, 680);
        var accent = (Ui.Brush("AccentFillColorDefaultBrush") as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color ?? Microsoft.UI.Colors.White;
        var muted = Ui.Brush("TextFillColorSecondaryBrush");
        var body = new StackPanel { Spacing = 14, Width = width };

        var head = new Grid { ColumnSpacing = 14 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x30, accent.R, accent.G, accent.B)),
            Child = new FontIcon { Glyph = "", FontSize = 24, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush") },
        });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = $"{AppInfo.Name} {release.Version.ToString(3)}", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var sub = L.F("You have {0}", AppUpdates.FriendlyVersion);
        if (release.Published is { } when) sub += " · " + L.F("released {0}", when.LocalDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
        titles.Children.Add(new TextBlock { Text = sub, Foreground = muted });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        body.Children.Add(head);

        var lines = release.Notes.Length > 0 ? Core.Media.MarkdownText.ToLines(release.Notes) : [];
        // A first heading that only repeats the name and number is left out (the head says it).
        if (lines.FirstOrDefault() is { Heading: true } first && first.Text.Contains(release.Version.ToString(3), StringComparison.Ordinal)) lines.RemoveAt(0);

        // What each section holds, at a glance: "New 6", "Fixed 4".
        var counts = new List<(string Name, int Count)>();
        foreach (var line in lines)
        {
            if (line.Heading) counts.Add((line.Text, 0));
            else if (line.Level == 1 && counts.Count > 0) counts[^1] = (counts[^1].Name, counts[^1].Count + 1);
        }
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (name, count) in counts.Where(c => c.Count > 0).Take(5))
        {
            var pill = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            pill.Children.Add(new TextBlock { Text = name, FontSize = 12 });
            pill.Children.Add(new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush") });
            pills.Children.Add(new Border { Child = pill, Padding = new Thickness(10, 3, 10, 4), CornerRadius = new CornerRadius(11), Background = Ui.Brush("SubtleFillColorSecondaryBrush") });
        }
        if (pills.Children.Count > 0) body.Children.Add(pills);

        FrameworkElement notes = lines.Count > 0
            ? Ui.Rich(lines, spacing: 6, headingTint: accent)
            : new TextBlock { Text = L.T("No notes for this version."), Foreground = muted };
        var pane = new ScrollViewer
        {
            Content = new Border { Child = notes, Padding = new Thickness(16, 12, 20, 16) },
            MaxHeight = Math.Clamp(root.Size.Height - 560, 160, 400),
        };
        SmoothWheel.Attach(pane);
        body.Children.Add(new Border { Child = pane, CornerRadius = new CornerRadius(12), Background = Ui.Brush("CardBackgroundFillColorDefaultBrush") });

        var foot = new Grid { ColumnSpacing = 12 };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(new TextBlock
        {
            Text = L.T("The app closes for a moment and opens again by itself; your games, settings and accounts stay as they are."),
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = muted, VerticalAlignment = VerticalAlignment.Center,
        });
        var page = new HyperlinkButton { Content = L.T("Open on GitHub"), NavigateUri = Uri.TryCreate(release.PageUrl, UriKind.Absolute, out var pageUri) ? pageUri : null, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(page, 1);
        foot.Children.Add(page);
        body.Children.Add(foot);

        var status = new TextBlock { Foreground = muted, Visibility = Visibility.Collapsed };
        var bar = new ProgressBar { Maximum = 1, Visibility = Visibility.Collapsed };
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
        dialog.Resources["ContentDialogMaxWidth"] = width + 96;
        dialog.Resources["ContentDialogMaxHeight"] = Math.Max(500, root.Size.Height - 80);
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
                double total = release.ZipSize / 1048576.0;
                var exe = await AppUpdates.DownloadAsync(release, new Progress<double>(p =>
                {
                    bar.Value = p;
                    if (total > 0) status.Text = L.F("Downloading the update… {0:0.0} of {1:0.0} MB", p * total, total);
                }), CancellationToken.None);
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
        if (await dialog.ShowOnceAsync() == ContentDialogResult.None && !_busy)
        {
            AppState.Settings.SkippedAppUpdate = release.Version.ToString();
            SettingsStore.Save();
        }
    }
}
