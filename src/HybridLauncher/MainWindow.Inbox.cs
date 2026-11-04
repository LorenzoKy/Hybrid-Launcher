using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher;

/// <summary>
/// The bell in the title bar, beside Minimize: every notification the app sent (OptiScaler and Ultra+ updates, a new Hybrid
/// Launcher) and the Ultra+ updates it installed by itself, kept in <see cref="Inbox"/> so one you missed can still be read.
/// A badge counts the unread ones; opening the list reads them, a click on one opens where it points.
/// </summary>
public sealed partial class MainWindow
{
    void InitInbox()
    {
        Inbox.Changed += () => DispatcherQueue.TryEnqueue(UpdateBell);
        UpdateBell();
    }

    /// <summary>
    /// Whether a Windows pop-up goes out for this kind now: not when turned off in Settings → Notifications, nor while a game
    /// runs with "Quiet while playing" (the bell keeps it either way).
    /// </summary>
    static bool Popup(string kind)
    {
        var s = AppState.Settings;
        if (s.PopupOff.Contains(kind)) return false;
        if (s.QuietWhilePlaying && GameSessions.Active().Count > 0) { Log.Info($"Notification ({kind}) kept in the bell only: a game is running"); return false; }
        return true;
    }

    /// <summary>Settings → Notifications changed: the bell shows or hides, the list is read again (kept days, kinds).</summary>
    public void InboxSettingsChanged()
    {
        Inbox.Reload();
        UpdateBell();
    }

    void UpdateBell()
    {
        BellButton.Visibility = AppState.Settings.ShowBell ? Visibility.Visible : Visibility.Collapsed;
        int unread = Inbox.Unread;
        BellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellCount.Text = unread > 9 ? "9+" : unread.ToString();
        var tip = unread == 0 ? L.T("Notifications") : unread == 1 ? L.T("Notifications (1 new)") : L.F("Notifications ({0} new)", unread);
        ToolTipService.SetToolTip(BellButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BellButton, tip);
        if (BellFlyout.IsOpen)
        {
            BellFlyout.Content = InboxPanel();
            DispatcherQueue.TryEnqueue(UpdateTitleBarRegions);
        }
    }

    void BellFlyout_Opening(object sender, object e) => BellFlyout.Content = InboxPanel();

    void BellFlyout_Opened(object sender, object e) => UpdateTitleBarRegions();

    /// <summary>Closing the list reads what was in it (the new ones stay marked while it is open).</summary>
    void BellFlyout_Closed(object sender, object e)
    {
        Inbox.MarkAllRead();
        UpdateTitleBarRegions();
    }

    void OpenInboxTarget(string kind, string? gameId, string? tab)
    {
        ShowFromTray();
        if (kind == Inbox.App)
        {
            if (Helpers.AppUpdater.Available is { } release && Content?.XamlRoot is { } root) _ = Helpers.AppUpdater.OfferAsync(root, release);
            return;
        }
        if (gameId is null || AppState.Find(gameId) is null) return;
        if (tab is not null) AppState.Settings.GameTab = tab;
        OpenGame(gameId);
    }

    FrameworkElement InboxPanel()
    {
        var entries = Inbox.Entries;
        var root = new StackPanel { Width = 380, Spacing = 8 };

        var head = new Grid { ColumnSpacing = 4, Margin = new Thickness(2, 0, 0, 2) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Settings → Notifications: what goes in here and what pops up.
        var gear = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Width = 30, Height = 28, Padding = new Thickness(0), MinWidth = 0,
                                Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(gear, L.T("Notification settings"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gear, L.T("Notification settings"));
        gear.Click += (_, _) => { BellFlyout.Hide(); OpenSettings("notifications"); };
        Grid.SetColumn(gear, 3);
        head.Children.Add(gear);
        head.Children.Add(new TextBlock { Text = L.T("Notifications"), FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (entries.Count > 0)
        {
            var read = new HyperlinkButton { Content = L.T("Mark all read"), Padding = new Thickness(6, 2, 6, 2), FontSize = 12, IsEnabled = entries.Any(x => !x.Read) };
            read.Click += (_, _) => Inbox.MarkAllRead();
            Grid.SetColumn(read, 1);
            head.Children.Add(read);
            var clear = new HyperlinkButton { Content = L.T("Clear"), Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };
            clear.Click += (_, _) => Inbox.Clear();
            Grid.SetColumn(clear, 2);
            head.Children.Add(clear);
        }
        root.Children.Add(head);

        if (entries.Count == 0)
        {
            var empty = new StackPanel { Spacing = 8, Padding = new Thickness(8, 22, 8, 26), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new FontIcon { Glyph = "", FontSize = 28, Opacity = 0.45 });
            empty.Children.Add(new TextBlock { Text = L.T("Nothing yet"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock
            {
                Text = L.T("New versions of the OptiScaler builds you follow, Ultra+ updates and Hybrid Launcher updates are kept here, so you can read them even after the pop-up is gone."),
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12, Opacity = 0.7,
            });
            root.Children.Add(empty);
            return root;
        }

        var list = new StackPanel { Spacing = 4 };
        foreach (var entry in entries) list.Children.Add(InboxRow(entry));
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0), Margin = new Thickness(0, 0, -12, 0) });
        return root;
    }

    Button InboxRow(InboxEntry entry)
    {
        var (glyph, tint) = entry.Kind switch
        {
            Inbox.Opti => (Features.Find(Features.Opti)!.Glyph, Features.Find(Features.Opti)!.Tint),
            Inbox.Ultra => (Features.Find(Features.UltraPlus)!.Glyph, Features.Find(Features.UltraPlus)!.Tint),
            _ => ("", 0xFF5FA8FFu),
        };
        var color = Palettes.Adapt(Color.FromArgb((byte)(tint >> 24), (byte)(tint >> 16), (byte)(tint >> 8), (byte)tint));

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
            Child = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        grid.Children.Add(tile);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = entry.Title, FontWeight = entry.Read ? FontWeights.Normal : FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(entry.Text))
            text.Children.Add(new TextBlock { Text = entry.Text, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = When(entry.Time), FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (!entry.Read)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top, Fill = Ui.Brush("AccentFillColorDefaultBrush") };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }

        var button = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 9, 10, 9), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            Background = entry.Read ? new SolidColorBrush(Colors.Transparent) : new SolidColorBrush(Color.FromArgb(0x10, color.R, color.G, color.B)),
        };
        bool goes = entry.Kind == Inbox.App ? Helpers.AppUpdater.Available is not null : entry.GameId is not null && AppState.Find(entry.GameId) is not null;
        if (goes)
        {
            ToolTipService.SetToolTip(button, entry.Kind == Inbox.App ? L.T("Open the update") : L.F("Open {0}", AppState.Find(entry.GameId!)!.Title));
            button.Click += (_, _) =>
            {
                BellFlyout.Hide();
                OpenInboxTarget(entry.Kind, entry.GameId, entry.Tab);
            };
        }
        else button.IsHitTestVisible = false;
        return button;
    }

    static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var ago = DateTime.Now - local;
        if (ago.TotalMinutes < 1) return L.T("Just now");
        if (ago.TotalMinutes < 60) return L.F("{0} min ago", (int)ago.TotalMinutes);
        if (local.Date == DateTime.Today) return L.F("Today, {0:HH:mm}", local);
        if (local.Date == DateTime.Today.AddDays(-1)) return L.F("Yesterday, {0:HH:mm}", local);
        return local.ToString("d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    }
}
