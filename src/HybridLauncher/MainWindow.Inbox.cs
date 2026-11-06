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

    int _bellUnread = -1;
    /// <summary>The kind the list shows (null: all of them); kept while the window lives.</summary>
    string? _inboxFilter;
    /// <summary>What the last Clear took away, offered back until the list closes.</summary>
    List<InboxEntry>? _inboxUndo;

    void UpdateBell()
    {
        BellButton.Visibility = AppState.Settings.ShowBell ? Visibility.Visible : Visibility.Collapsed;
        int unread = Inbox.Unread;
        BellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellCount.Text = unread > 9 ? "9+" : unread.ToString();
        // Something new: the bell takes the accent and swings once (a few frames, only then).
        BellIcon.Foreground = unread > 0 ? Ui.Brush("AccentTextFillColorPrimaryBrush") : Ui.Brush("TextFillColorPrimaryBrush");
        if (_bellUnread >= 0 && unread > _bellUnread && BellButton.Visibility == Visibility.Visible) RingBell();
        _bellUnread = unread;
        var tip = unread == 0 ? L.T("Notifications") : unread == 1 ? L.T("Notifications (1 new)") : L.F("Notifications ({0} new)", unread);
        ToolTipService.SetToolTip(BellButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BellButton, tip);
        if (BellFlyout.IsOpen)
        {
            BellFlyout.Content = InboxPanel();
            DispatcherQueue.TryEnqueue(UpdateTitleBarRegions);
        }
    }

    /// <summary>The bell swings from its top, a damped shake of under a second.</summary>
    void RingBell()
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(BellIcon);
            var size = BellIcon.ActualSize;
            visual.CenterPoint = new System.Numerics.Vector3(size.X / 2, 1, 0);
            var swing = visual.Compositor.CreateScalarKeyFrameAnimation();
            float[] angles = [0, 16, -13, 9, -6, 3, 0];
            for (int i = 0; i < angles.Length; i++) swing.InsertKeyFrame(i / (float)(angles.Length - 1), angles[i]);
            swing.Duration = TimeSpan.FromMilliseconds(850);
            visual.StartAnimation("RotationAngleInDegrees", swing);
        }
        catch (Exception ex) { Log.Warn($"Bell: {ex.Message}"); }
    }

    void BellFlyout_Opening(object sender, object e) => BellFlyout.Content = InboxPanel();

    void BellFlyout_Opened(object sender, object e) => UpdateTitleBarRegions();

    /// <summary>Closing the list reads what was in it (the new ones stay marked while it is open).</summary>
    void BellFlyout_Closed(object sender, object e)
    {
        _inboxUndo = null;
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

    static string KindName(string kind) => kind switch
    {
        Inbox.Opti => "OptiScaler",
        Inbox.Ultra => "Ultra+",
        _ => AppInfo.Name,
    };

    static (string Glyph, Color Color) KindLook(string kind)
    {
        var (glyph, tint) = kind switch
        {
            Inbox.Opti => (Features.Find(Features.Opti)!.Glyph, Features.Find(Features.Opti)!.Tint),
            Inbox.Ultra => (Features.Find(Features.UltraPlus)!.Glyph, Features.Find(Features.UltraPlus)!.Tint),
            _ => ("", 0xFF5FA8FFu),
        };
        return (glyph, Palettes.Adapt(Color.FromArgb((byte)(tint >> 24), (byte)(tint >> 16), (byte)(tint >> 8), (byte)tint)));
    }

    static Button IconButton(string glyph, string tip, double size = 13)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = size }, Width = 32, Height = 30, Padding = new Thickness(0), MinWidth = 0,
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6),
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    /// <summary>
    /// The list under the bell: a header with the count of new ones and its actions, filters by kind when there is more than one,
    /// then the notifications grouped by day (Today, Yesterday, This week, Earlier). Each one opens where it points; the cross on
    /// it takes it away. Clear empties the list and offers Undo until the list closes.
    /// </summary>
    FrameworkElement InboxPanel()
    {
        var entries = Inbox.Entries;
        int unread = entries.Count(x => !x.Read);
        var root = new StackPanel { Width = 424, Spacing = 10 };

        var head = new Grid { ColumnSpacing = 2, Margin = new Thickness(2, 0, 0, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = L.T("Notifications"), FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (unread > 0)
            title.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center,
                Background = Ui.Brush("AccentFillColorDefaultBrush"),
                Child = new TextBlock { Text = unread == 1 ? L.T("1 new") : L.F("{0} new", unread), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("TextOnAccentFillColorPrimaryBrush") },
            });
        head.Children.Add(title);
        if (entries.Count > 0)
        {
            var read = IconButton("", L.T("Mark all read"));
            read.IsEnabled = unread > 0;
            read.Click += (_, _) => Inbox.MarkAllRead();
            Grid.SetColumn(read, 1);
            head.Children.Add(read);
            var clear = IconButton("", L.T("Clear"));
            clear.Click += (_, _) => { _inboxUndo = Inbox.Clear(); };
            Grid.SetColumn(clear, 2);
            head.Children.Add(clear);
        }
        // Settings → Notifications: what goes in here and what pops up.
        var gear = IconButton("", L.T("Notification settings"));
        gear.Click += (_, _) => { BellFlyout.Hide(); OpenSettings("notifications"); };
        Grid.SetColumn(gear, 3);
        head.Children.Add(gear);
        root.Children.Add(head);

        if (entries.Count == 0)
        {
            var empty = new StackPanel { Spacing = 8, Padding = new Thickness(12, 20, 12, 22), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new Border
            {
                Width = 52, Height = 52, CornerRadius = new CornerRadius(26), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Child = new FontIcon { Glyph = "", FontSize = 22, Opacity = 0.6 },
            });
            empty.Children.Add(new TextBlock { Text = L.T("You're all caught up"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            empty.Children.Add(new TextBlock
            {
                Text = L.T("New versions of the OptiScaler builds you follow, Ultra+ updates and Hybrid Launcher updates are kept here, so you can read them even after the pop-up is gone."),
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12, Opacity = 0.7,
            });
            if (_inboxUndo is { Count: > 0 } undo)
            {
                var back = new HyperlinkButton { Content = undo.Count == 1 ? L.T("Undo: bring back 1 notification") : L.F("Undo: bring back {0} notifications", undo.Count), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
                back.Click += (_, _) => { _inboxUndo = null; Inbox.Restore(undo); };
                empty.Children.Add(back);
            }
            root.Children.Add(empty);
            return root;
        }

        // Filters, only when the list holds more than one kind.
        var kinds = entries.Select(x => x.Kind).Distinct().OrderBy(k => k == Inbox.App ? 2 : k == Inbox.Ultra ? 1 : 0).ToList();
        if (_inboxFilter is not null && !kinds.Contains(_inboxFilter)) _inboxFilter = null;
        if (kinds.Count > 1)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            void Chip(string? kind, string label, int count)
            {
                bool on = _inboxFilter == kind;
                var chip = new Button
                {
                    Content = new TextBlock { Text = $"{label}  {count}", FontSize = 12, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal },
                    Padding = new Thickness(10, 4, 10, 5), MinWidth = 0, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0),
                    Background = on ? Ui.Brush("AccentFillColorDefaultBrush") : new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
                    Foreground = on ? Ui.Brush("TextOnAccentFillColorPrimaryBrush") : Ui.Brush("TextFillColorPrimaryBrush"),
                };
                chip.Click += (_, _) => { _inboxFilter = kind; BellFlyout.Content = InboxPanel(); DispatcherQueue.TryEnqueue(UpdateTitleBarRegions); };
                chips.Children.Add(chip);
            }
            Chip(null, L.T("All"), entries.Count);
            foreach (var k in kinds) Chip(k, KindName(k), entries.Count(x => x.Kind == k));
            root.Children.Add(new ScrollViewer { Content = chips, HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }

        var list = new StackPanel { Spacing = 2 };
        string? group = null;
        foreach (var entry in entries.Where(x => _inboxFilter is null || x.Kind == _inboxFilter))
        {
            var day = DayGroup(entry.Time);
            if (day != group)
            {
                group = day;
                list.Children.Add(new TextBlock { Text = day, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.6, Margin = new Thickness(4, list.Children.Count == 0 ? 0 : 10, 0, 4) });
            }
            list.Children.Add(InboxRow(entry));
        }
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 520, Padding = new Thickness(0, 0, 12, 0), Margin = new Thickness(0, 0, -12, 0) });
        return root;
    }

    static string DayGroup(DateTime utc)
    {
        var day = utc.ToLocalTime().Date;
        if (day >= DateTime.Today) return L.T("Today");
        if (day == DateTime.Today.AddDays(-1)) return L.T("Yesterday");
        if (day > DateTime.Today.AddDays(-7)) return L.T("This week");
        return L.T("Earlier");
    }

    FrameworkElement InboxRow(InboxEntry entry)
    {
        var (glyph, color) = KindLook(entry.Kind);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        grid.Children.Add(new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x3A, color.R, color.G, color.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x16, color.R, color.G, color.B), Offset = 1 },
                },
            },
            Child = new FontIcon { Glyph = glyph, FontSize = 17, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });

        var game = entry.GameId is null ? null : AppState.Find(entry.GameId);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = entry.Title, FontWeight = entry.Read ? FontWeights.Normal : FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(entry.Text))
            text.Children.Add(new TextBlock { Text = entry.Text, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = KindName(entry.Kind) + "  ·  " + When(entry.Time), FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (!entry.Read)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Fill = Ui.Brush("AccentFillColorDefaultBrush") };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }

        var button = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10),
            Background = entry.Read ? new SolidColorBrush(Colors.Transparent) : new SolidColorBrush(Color.FromArgb(0x12, color.R, color.G, color.B)),
        };
        bool goes = entry.Kind == Inbox.App ? Helpers.AppUpdater.Available is not null : game is not null;
        if (goes)
        {
            ToolTipService.SetToolTip(button, entry.Kind == Inbox.App ? L.T("Open the update") : L.F("Open {0}", game!.Title));
            button.Click += (_, _) =>
            {
                Inbox.MarkRead(entry);
                BellFlyout.Hide();
                OpenInboxTarget(entry.Kind, entry.GameId, entry.Tab);
            };
        }
        // A row with nowhere to go is still a row: no click, but it keeps its cross.
        else button.IsHitTestVisible = false;

        // The cross that takes it away, over the row's bottom-right corner: shown while the pointer is on the row, or when it
        // has the keyboard focus (it is a stop of its own).
        var remove = IconButton("", L.T("Remove"), 10);
        remove.Width = 26; remove.Height = 26;
        remove.HorizontalAlignment = HorizontalAlignment.Right; remove.VerticalAlignment = VerticalAlignment.Bottom; remove.Margin = new Thickness(0, 0, 4, 4);
        remove.Click += (_, _) => Inbox.Remove(entry);
        // Hidden, it is not clickable either: a tap on the row's corner opens the row, never removes it unseen.
        void ShowCross(bool on) { remove.Opacity = on ? 1 : 0; remove.IsHitTestVisible = on; }
        ShowCross(false);
        remove.GotFocus += (_, _) => ShowCross(true);
        remove.LostFocus += (_, _) => ShowCross(false);
        var host = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        host.Children.Add(button);
        host.Children.Add(remove);
        host.PointerEntered += (_, _) => ShowCross(true);
        host.PointerExited += (_, _) => { if (remove.FocusState == FocusState.Unfocused) ShowCross(false); };
        return host;
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
