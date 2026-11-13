using HybridLauncher.Core;
using HybridLauncher.Core.Opti;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// Settings → Notifications: the bell (shown or not, how long it keeps things, clear it), and per source (OptiScaler builds you
/// follow, Ultra+, Hybrid Launcher) whether it goes in the bell and whether Windows shows a pop-up; quiet while a game runs.
/// Built in code and made again when something changes.
/// </summary>
public static class NotificationSettingsView
{
    static readonly int[] KeepDays = [7, 30, 90, 0];

    public static void Fill(Panel host)
    {
        App.Window?.KeepFocusOffSearch();
        host.Children.Clear();
        var s = AppState.Settings;
        void Changed()
        {
            SettingsStore.Save();
            App.Window?.InboxSettingsChanged();
        }

        // ---- the bell ----
        host.Children.Add(Header("NOTIFICATION LIST", top: false));
        host.Children.Add(Row(Text(L.T("Notifications button in the title bar"), L.T("Beside Minimize: every notification is kept in its list, so one you missed can still be read.")),
            Toggle(s.ShowBell, on => { s.ShowBell = on; Changed(); })));

        var keep = new ComboBox { MinWidth = 150 };
        foreach (var d in KeepDays) keep.Items.Add(d == 0 ? L.T("Until I clear them") : L.F("{0} days", d));
        keep.SelectedIndex = Math.Max(0, Array.IndexOf(KeepDays, s.InboxKeepDays));
        keep.SelectionChanged += (_, _) => { if (keep.SelectedIndex >= 0) { s.InboxKeepDays = KeepDays[keep.SelectedIndex]; Changed(); } };
        host.Children.Add(Row(Text(L.T("Keep them for"), L.T("Older notifications leave the list by themselves.")), keep));

        int count = Inbox.Entries.Count;
        var clear = new Button { Content = L.T("Clear the list"), IsEnabled = count > 0 };
        clear.Click += (_, _) => { Inbox.Clear(); Fill(host); };
        host.Children.Add(Row(Text(L.T("Notifications kept"), count == 0 ? L.T("None right now.") : count == 1 ? L.T("1 in the list.") : L.F("{0} in the list.", count)), clear));

        // ---- per source ----
        host.Children.Add(Header("WHAT YOU HEAR ABOUT", top: true));
        host.Children.Add(new TextBlock
        {
            Text = L.T("Each source can be kept in the app's list, show a Windows pop-up, both, or neither."),
            Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // OptiScaler: only the sources followed from a game's OptiScaler tab are checked at all.
        var follow = s.OptiFollow.Select(k => OptiUpdates.Name(k, s)).ToList();
        var optiText = Text(L.T("OptiScaler updates"),
            follow.Count == 0 ? L.T("You follow no OptiScaler build. Turn on Get notified on updates in a game's OptiScaler tab, for Stable, Nightly or a fork.")
                              : L.F("New versions of the builds you follow: {0}. Following is chosen in a game's OptiScaler tab.", string.Join(", ", follow)));
        var optiActions = Checks(Inbox.Opti, popupGet: () => !s.PopupOff.Contains(Inbox.Opti), popupSet: on => Set(s.PopupOff, Inbox.Opti, !on), Changed);
        if (follow.Count > 0)
        {
            var stop = new HyperlinkButton { Content = L.T("Stop following all"), Padding = new Thickness(4, 2, 4, 2) };
            stop.Click += (_, _) =>
            {
                s.OptiFollow.Clear();
                SettingsStore.Save();
                App.Window?.OptiFollowChanged();
                Fill(host);
            };
            optiActions.Children.Insert(0, stop);
        }
        host.Children.Add(Row(Source(Features.Opti, optiText), optiActions));

        host.Children.Add(Row(Source(Features.UltraPlus, Text(L.T("Ultra+ updates"),
                L.T("New Ultra+ versions for your games, and the ones installed by themselves. The pop-up is the same switch as Notify me of new versions on a game's Ultra+ tab."))),
            Checks(Inbox.Ultra, popupGet: () => s.UltraPlusNotify, popupSet: on =>
            {
                s.UltraPlusNotify = on;
                if (on) MainWindow.MarkUltraUpdatesSeen();
                s.PopupOff.Remove(Inbox.Ultra);
            }, Changed)));

        host.Children.Add(Row(Source(null, Text(L.T("Hybrid Launcher updates"),
                L.T("A newer version of the app. The blue Update button in the title bar shows it too; checking at start is in About."))),
            Checks(Inbox.App, popupGet: () => !s.PopupOff.Contains(Inbox.App), popupSet: on => Set(s.PopupOff, Inbox.App, !on), Changed)));

        // ---- quiet ----
        host.Children.Add(Header("WHILE YOU PLAY", top: true));
        host.Children.Add(Row(Text(L.T("No pop-ups while a game runs"), L.T("They wait in the app's list instead, so nothing jumps over the game.")),
            Toggle(s.QuietWhilePlaying, on => { s.QuietWhilePlaying = on; Changed(); })));
    }

    static void Set(HashSet<string> set, string kind, bool add)
    {
        if (add) set.Add(kind); else set.Remove(kind);
    }

    /// <summary>"In the bell" and (when the source has one) "Windows pop-up", side by side.</summary>
    static StackPanel Checks(string kind, Func<bool>? popupGet, Action<bool>? popupSet, Action changed)
    {
        var s = AppState.Settings;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var bell = new CheckBox { Content = L.T("In the app"), IsChecked = !s.InboxOff.Contains(kind), MinWidth = 0 };
        bell.Click += (_, _) => { Set(s.InboxOff, kind, bell.IsChecked != true); changed(); };
        panel.Children.Add(bell);
        if (popupGet is not null && popupSet is not null)
        {
            var popup = new CheckBox { Content = L.T("Windows pop-up"), IsChecked = popupGet(), MinWidth = 0 };
            popup.Click += (_, _) => { popupSet(popup.IsChecked == true); changed(); };
            panel.Children.Add(popup);
        }
        return panel;
    }

    static ToggleSwitch Toggle(bool on, Action<bool> set)
    {
        var t = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        t.Toggled += (_, _) => set(t.IsOn);
        return t;
    }

    static TextBlock Header(string text, bool top) =>
        new() { Text = L.T(text), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, top ? 16 : 0, 0, 4) };

    static StackPanel Text(string header, string description)
    {
        var p = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new TextBlock { Text = header, Style = Ui.Style("RowHeader") });
        p.Children.Add(new TextBlock { Text = description, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        return p;
    }

    /// <summary>The source's icon in its feature colour (the same tile as in the bell's list), then its text.</summary>
    static Grid Source(string? feature, FrameworkElement text)
    {
        var f = feature is null ? null : Features.Find(feature);
        uint tint = f?.Tint ?? 0xFF5FA8FF;
        var c = Helpers.Palettes.Adapt(Color.FromArgb((byte)(tint >> 24), (byte)(tint >> 16), (byte)(tint >> 8), (byte)tint));
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Controls.IconTile(f?.Glyph ?? "\uE896", c, 32));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
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
