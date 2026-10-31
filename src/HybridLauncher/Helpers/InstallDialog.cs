using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Detection;
using HybridLauncher.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace HybridLauncher.Helpers;

/// <summary>
/// Steam's install window, before a direct download: the game's art, where it goes (the launcher's folder, the one chosen
/// in Downloads → Accounts, the other Steam libraries, or any folder), the space it needs next to the space free there,
/// read from the store while the window is open. Install starts the download with the file list already read.
/// </summary>
static class InstallDialog
{
    sealed record Place(string Root, string Label);

    /// <summary>Shows the window; true when the download started (or queued).</summary>
    public static async Task<bool> ShowAsync(OwnedGame game, XamlRoot root, ElementTheme theme)
    {
        var places = Places(game.Store);
        using var cts = new CancellationTokenSource();
        DownloadPlan? plan = null;
        string? planError = null;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = L.F("Install {0}", game.Name),
            PrimaryButtonText = L.T("Install"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;

        var body = new StackPanel { Spacing = 16, Width = 560 };
        body.Children.Add(Banner(game));

        // Where it goes.
        var location = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in places) location.Items.Add(PlaceItem(p));
        location.Items.Add(new ComboBoxItem { Content = Labelled("", L.T("Choose another folder…")), Tag = "pick" });
        location.SelectedIndex = 0;
        var folderText = new TextBlock { Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis };
        var where = new StackPanel { Spacing = 6 };
        where.Children.Add(Caption(L.T("INSTALL LOCATION")));
        where.Children.Add(location);
        where.Children.Add(folderText);
        body.Children.Add(where);

        // The space: needed (read from the store) and free on that drive.
        var needed = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Text = "—" };
        var free = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Text = "—" };
        var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
        var neededRow = new StackPanel { Orientation = Orientation.Horizontal };
        neededRow.Children.Add(ring);
        neededRow.Children.Add(needed);
        var space = new Grid { ColumnSpacing = 24 };
        space.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        space.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        space.Children.Add(Figure(L.T("DISK SPACE REQUIRED"), neededRow));
        var freeFigure = Figure(L.T("DISK SPACE AVAILABLE"), free);
        Grid.SetColumn(freeFigure, 1);
        space.Children.Add(freeFigure);
        body.Children.Add(space);
        var meter = new ProgressBar { Maximum = 1, MinHeight = 6, Visibility = Visibility.Collapsed };
        body.Children.Add(meter);
        var status = new InfoBar { IsOpen = false, IsClosable = false };
        body.Children.Add(status);
        dialog.Content = body;

        Place Current() => location.SelectedItem is ComboBoxItem { Tag: Place p } ? p : places[0];

        void Refresh()
        {
            var place = Current();
            var folder = plan is null ? null : Path.Combine(place.Root, Path.GetFileName(plan.InstallDir));
            folderText.Text = folder ?? place.Root;
            ToolTipService.SetToolTip(folderText, folderText.Text);
            long? avail = FreeBytes(place.Root);
            free.Text = avail is { } a ? DiskUsage.Format(a) : "—";
            if (plan is not null)
            {
                ring.Visibility = Visibility.Collapsed;
                long size = plan.TotalBytes;
                needed.Text = DiskUsage.Format(size);
                bool fits = avail is null || avail >= size;
                meter.Visibility = avail is { } total && total > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (avail is { } all && all > 0) meter.Value = Math.Clamp((double)size / all, 0, 1);
                meter.ShowError = !fits;
                status.IsOpen = !fits;
                if (!fits)
                {
                    status.Severity = InfoBarSeverity.Error;
                    status.Title = L.T("Not enough space");
                    status.Message = L.F("{0} needs {1} more. Free some space or pick another drive.", game.Name, DiskUsage.Format(size - avail!.Value));
                }
                dialog.IsPrimaryButtonEnabled = fits;
            }
            else if (planError is not null)
            {
                ring.Visibility = Visibility.Collapsed;
                needed.Text = "—";
                status.IsOpen = true;
                status.Severity = InfoBarSeverity.Warning;
                status.Title = L.F("{0} did not send the file list", StoreLogos.Label(game.Store));
                status.Message = planError + " " + L.T("Install tries again.");
                dialog.IsPrimaryButtonEnabled = true;
            }
            else needed.Text = L.T("Reading…");
        }

        location.SelectionChanged += async (_, _) =>
        {
            if (location.SelectedItem is ComboBoxItem { Tag: "pick" })
            {
                // The picker is a window of its own: the dialog stays open behind it.
                var dir = App.Window is null ? null : await Pickers.PickFolderAsync(App.Window);
                if (dir is null) { location.SelectedIndex = 0; return; }
                var picked = new Place(dir, Path.GetFileName(dir.TrimEnd('\\')) is { Length: > 0 } n ? n : dir);
                places.Add(picked);
                var item = PlaceItem(picked);
                location.Items.Insert(location.Items.Count - 1, item);
                location.SelectedItem = item;
                return;
            }
            Refresh();
        };

        // The file list, read while the window is open (a big game takes some seconds: every depot's manifest).
        _ = Task.Run(async () =>
        {
            try { plan = await Downloads.PlanAsync(game, places[0].Root, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn($"Install dialog, file list of {game.Name}: {ex.Message}");
                planError = ex.Message;
            }
            dialog.DispatcherQueue.TryEnqueue(Refresh);
        });
        Refresh();

        var result = await dialog.ShowAsync();
        cts.Cancel();
        if (result != ContentDialogResult.Primary) return false;
        var target = Current().Root;
        if (!Downloads.EnsureWritable(target))
        {
            App.Window?.ShowError(L.T("Could not install"), L.F("Hybrid Launcher may not write in {0}. Allow it when Windows asks, or choose another folder in Downloads → Accounts.", target));
            return false;
        }
        Downloads.StartDirect(game, target, plan);
        return true;
    }

    /// <summary>The folders offered: the store's own (or the one chosen in Accounts) first, then the launcher's, then the other Steam libraries.</summary>
    static List<Place> Places(GameStore store)
    {
        var list = new List<Place>();
        void Add(string root, string label)
        {
            if (string.IsNullOrWhiteSpace(root) || list.Any(p => p.Root.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new Place(root, label));
        }
        var launcher = Downloads.LauncherRoot(store);
        var chosen = Downloads.InstallRootFor(store);
        Add(chosen, chosen.Equals(launcher, StringComparison.OrdinalIgnoreCase) ? L.F("{0}'s folder", StoreLogos.Label(store)) : L.T("Your install folder"));
        Add(launcher, L.F("{0}'s folder", StoreLogos.Label(store)));
        if (store == GameStore.Steam)
        {
            try
            {
                foreach (var lib in SteamScanner.Libraries()) Add(Path.Combine(lib, "steamapps", "common"), L.T("Steam library"));
            }
            catch (Exception ex) { Log.Warn($"Steam libraries: {ex.Message}"); }
        }
        // Epic and GOG keep no library list: the folders their installed games sit in are their libraries.
        else if (store is GameStore.Epic or GameStore.Gog)
        {
            var libs = store == GameStore.Epic ? EpicScanner.Libraries() : GogScanner.Libraries();
            foreach (var lib in libs.Take(4)) Add(lib, L.F("{0} library", StoreLogos.Label(store)));
        }
        return list;
    }

    static long? FreeBytes(string root)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(root)) is { Length: > 0 } drive ? new DriveInfo(drive).AvailableFreeSpace : null; }
        catch { return null; }
    }

    static ComboBoxItem PlaceItem(Place p)
    {
        var text = new StackPanel { Spacing = 0 };
        var drive = Path.GetPathRoot(p.Root)?.TrimEnd('\\');
        text.Children.Add(new TextBlock { Text = p.Label + (drive is { Length: > 0 } ? $"  ·  {drive}" : "") + (FreeBytes(p.Root) is { } f ? "  ·  " + L.F("{0} free", DiskUsage.Format(f)) : ""), FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = p.Root, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 500 });
        return new ComboBoxItem { Content = text, Tag = p };
    }

    static StackPanel Labelled(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    static TextBlock Caption(string text) => new() { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, CharacterSpacing = 60, Opacity = 0.65 };

    static StackPanel Figure(string label, FrameworkElement value)
    {
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(Caption(label));
        p.Children.Add(value);
        return p;
    }

    /// <summary>The game's wide art with its name and launcher over the bottom, like the top of Steam's install window.</summary>
    static FrameworkElement Banner(OwnedGame game)
    {
        var banner = new Grid { Height = 150, CornerRadius = new CornerRadius(10), Background = Ui.Brush("ControlFillColorSecondaryBrush"), RequestedTheme = ElementTheme.Dark };
        var art = game.Wide ?? (game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) ? $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{game.Id[6..]}/library_hero.jpg" : game.Cover);
        if (art is not null && Uri.TryCreate(art, UriKind.Absolute, out var uri))
        {
            var bmp = new BitmapImage { DecodePixelWidth = 1120 };
            bmp.UriSource = uri;
            banner.Children.Add(Smooth.Art(bmp, banner.CornerRadius, fadeMs: 220));
        }
        banner.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x00, 0x08, 0x08, 0x0C), Offset = 0.3 },
                    new GradientStop { Color = Color.FromArgb(0xE0, 0x08, 0x08, 0x0C), Offset = 1 },
                },
            },
        });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 12), Spacing = 4 };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(StoreLogos.Tile(game.Store, size: 18));
        line.Children.Add(new TextBlock { Text = L.F("Downloaded by Hybrid Launcher from your {0} account", StoreLogos.Label(game.Store)), FontSize = 12, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new TextBlock { Text = game.Name, FontSize = 22, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        name.Children.Add(line);
        banner.Children.Add(name);
        return banner;
    }
}
