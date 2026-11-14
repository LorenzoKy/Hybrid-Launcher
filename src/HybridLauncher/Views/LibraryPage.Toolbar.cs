using HybridLauncher.Core;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The library's bar of choices (2.1-rc.3), drawn like Hydra's: a pill per choice with its name, the icon and the value of
/// what is chosen, and a chevron ("Launchers  [stack] All", "Sort by  [clock] Last played"). Each opens a short list where
/// every entry has its own icon (the launchers' logos, an icon per order) and a tick on the chosen ones; the launchers list
/// stays open so several can be ticked in one go. The same lists make the first two steps of the Filters panel (the side
/// list's filter button opens it).
/// </summary>
public sealed partial class LibraryPage
{
    // GitHub's Octicons (16 px), the set Hydra draws its library with.
    internal static class Octicons
    {
        public const string Typography = "M6.71 10H2.332l-.874 2.498a.75.75 0 0 1-1.415-.496l3.39-9.688a1.217 1.217 0 0 1 2.302.018l3.227 9.681a.75.75 0 0 1-1.423.474Zm3.13-4.358C10.53 4.374 11.87 4 13 4c1.5 0 3 .939 3 2.601v5.649a.75.75 0 0 1-1.448.275C13.995 12.82 13.3 13 12.5 13c-.77 0-1.514-.231-2.078-.709-.577-.488-.922-1.199-.922-2.041 0-.694.265-1.411.887-1.944C11 7.78 11.88 7.5 13 7.5h1.5v-.899c0-.54-.5-1.101-1.5-1.101-.869 0-1.528.282-1.840.858a.75.75 0 1 1-1.32-.716ZM6.21 8.5 4.574 3.594 2.857 8.5Zm8.29.5H13c-.881 0-1.375.22-1.637.444-.253.217-.363.5-.363.806 0 .408.155.697.39.896.249.21.63.354 1.11.354.732 0 1.26-.209 1.588-.449.35-.257.412-.495.412-.551Z";
        public const string Clock = "M8 0a8 8 0 1 1 0 16A8 8 0 0 1 8 0ZM1.5 8a6.5 6.5 0 1 0 13 0 6.5 6.5 0 0 0-13 0Zm7-3.25v2.992l2.028.812a.75.75 0 0 1-.557 1.392l-2.5-1A.751.751 0 0 1 7 8.25v-3.5a.75.75 0 0 1 1.5 0Z";
        public const string Calendar = "M4.75 0a.75.75 0 0 1 .75.75V2h5V.75a.75.75 0 0 1 1.5 0V2h1.25c.966 0 1.75.784 1.75 1.75v10.5A1.75 1.75 0 0 1 13.250 16H2.75A1.75 1.75 0 0 1 1 14.25V3.75C1 2.784 1.784 2 2.75 2H4V.75A.75.75 0 0 1 4.75 0ZM2.5 7.5v6.75c0 .138.112.25.25.25h10.5a.25.25 0 0 0 .25-.25V7.5Zm10.75-4H2.75a.25.25 0 0 0-.25.25V6h11V3.75a.25.25 0 0 0-.25-.25Z";
        public const string Stopwatch = "M5.75.75A.75.75 0 0 1 6.5 0h3a.75.75 0 0 1 0 1.5h-.75v1l-.001.041a6.724 6.724 0 0 1 3.464 1.435l.007-.006.75-.75a.749.749 0 0 1 1.275.326.749.749 0 0 1-.215.734l-.75.75-.006.007a6.75 6.75 0 1 1-10.548 0L2.72 5.03l-.75-.75a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018l.75.75.007.006A6.72 6.72 0 0 1 7.25 2.541V1.5H6.5a.75.75 0 0 1-.75-.75ZM8 14.5a5.25 5.25 0 1 0-.001-10.501A5.25 5.25 0 0 0 8 14.5Zm.389-6.7 1.33-1.33a.75.75 0 1 1 1.061 1.06L9.45 8.861A1.503 1.503 0 0 1 8 10.75a1.499 1.499 0 1 1 .389-2.95Z";
        public const string Trophy = "M3.217 6.962A3.75 3.75 0 0 1 0 3.25v-.5C0 1.784.784 1 1.75 1h1.356c.228-.585.796-1 1.462-1h6.864c.647 0 1.227.397 1.462 1h1.356c.966 0 1.75.784 1.75 1.75v.5a3.75 3.75 0 0 1-3.217 3.712 5.014 5.014 0 0 1-2.771 3.117l.144 1.446c.005.05.03.12.114.204.086.087.217.17.373.227.283.103.618.274.89.568.285.31.467.723.467 1.226v.75h1.25a.75.75 0 0 1 0 1.5H2.75a.75.75 0 0 1 0-1.5H4v-.75c0-.503.182-.916.468-1.226.27-.294.606-.465.889-.568.139-.048.266-.126.373-.227.084-.085.109-.153.114-.204l.144-1.446a5.015 5.015 0 0 1-2.77-3.117ZM4.5 1.568V5.5a3.5 3.5 0 1 0 7 0V1.568a.068.068 0 0 0-.068-.068H4.568a.068.068 0 0 0-.068.068Zm2.957 8.902-.12 1.204c-.093.925-.858 1.47-1.467 1.691a.766.766 0 0 0-.3.176c-.037.04-.07.093-.07.21v.75h5v-.75c0-.117-.033-.17-.07-.21a.766.766 0 0 0-.3-.176c-.609-.221-1.374-.766-1.466-1.69l-.12-1.204a5.064 5.064 0 0 1-1.087 0ZM13 2.5v2.872a2.25 2.25 0 0 0 1.5-2.122v-.5a.25.25 0 0 0-.25-.25H13Zm-10 0H1.75a.25.25 0 0 0-.25.25v.5c0 .98.626 1.813 1.5 2.122Z";
        public const string Cpu = "M6.5.75V2h3V.75a.75.75 0 0 1 1.5 0V2h1.25c.966 0 1.75.784 1.75 1.75V5h1.25a.75.75 0 0 1 0 1.5H14v3h1.25a.75.75 0 0 1 0 1.5H14v1.25A1.75 1.75 0 0 1 12.25 14H11v1.25a.75.75 0 0 1-1.5 0V14h-3v1.25a.75.75 0 0 1-1.5 0V14H3.75A1.75 1.75 0 0 1 2 12.250V11H.75a.75.75 0 0 1 0-1.5H2v-3H.75a.75.75 0 0 1 0-1.5H2V3.75C2 2.784 2.784 2 3.75 2H5V.75a.75.75 0 0 1 1.5 0Zm5.75 11.75a.25.25 0 0 0 .25-.25v-8.5a.25.25 0 0 0-.25-.25h-8.5a.25.25 0 0 0-.25.25v8.5c0 .138.112.25.25.25ZM5.75 5h4.5a.75.75 0 0 1 .75.75v4.5a.75.75 0 0 1-.75.75h-4.5a.75.75 0 0 1-.75-.75v-4.5A.75.75 0 0 1 5.75 5Zm.75 4.5h3v-3h-3Z";
        public const string Stack = "M7.122.392a1.75 1.75 0 0 1 1.756 0l5.003 2.902c.83.481.83 1.68 0 2.162L8.878 8.358a1.75 1.75 0 0 1-1.756 0L2.119 5.456a1.251 1.251 0 0 1 0-2.162ZM8.125 1.69a.248.248 0 0 0-.25 0l-4.63 2.685 4.63 2.685a.248.248 0 0 0 .25 0l4.63-2.685ZM1.601 7.789a.75.75 0 0 1 1.025-.273l5.249 3.044a.248.248 0 0 0 .25 0l5.249-3.044a.75.75 0 0 1 .752 1.298l-5.248 3.044a1.75 1.75 0 0 1-1.756 0L1.874 8.814A.75.75 0 0 1 1.6 7.789Zm0 3.5a.75.75 0 0 1 1.025-.273l5.249 3.044a.248.248 0 0 0 .25 0l5.249-3.044a.75.75 0 0 1 .752 1.298l-5.248 3.044a1.75 1.75 0 0 1-1.756 0l-5.248-3.044a.75.75 0 0 1-.273-1.025Z";
        public const string SortDesc = "M0 4.25a.75.75 0 0 1 .75-.75h7.5a.75.75 0 0 1 0 1.5H.75A.75.75 0 0 1 0 4.25Zm0 4a.75.75 0 0 1 .75-.75h4.5a.75.75 0 0 1 0 1.5H.75A.75.75 0 0 1 0 8.25Zm0 4a.75.75 0 0 1 .75-.75h2.5a.75.75 0 0 1 0 1.5H.75a.75.75 0 0 1-.75-.75ZM13.5 10h2.25a.25.25 0 0 1 .177.427l-3 3a.25.25 0 0 1-.354 0l-3-3A.25.25 0 0 1 9.75 10H12V3.75a.75.75 0 0 1 1.5 0V10Z";
        public const string ChevronDown = "M12.78 5.22a.749.749 0 0 1 0 1.06l-4.25 4.25a.749.749 0 0 1-1.06 0L3.22 6.28a.749.749 0 1 1 1.06-1.06L8 8.939l3.72-3.719a.749.749 0 0 1 1.06 0Z";
        public const string Check = "M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z";
        public const string Heart = "M7.655 14.916v-.001h-.002l-.006-.003-.018-.01a22.066 22.066 0 0 1-3.744-2.584C2.045 10.731 0 8.35 0 5.5 0 2.836 2.086 1 4.25 1 5.797 1 7.153 1.802 8 3.020 8.847 1.802 10.203 1 11.75 1 13.914 1 16 2.836 16 5.5c0 2.85-2.044 5.231-3.886 6.818a22.094 22.094 0 0 1-3.433 2.414 7.152 7.152 0 0 1-.31.17l-.018.01-.008.004a.75.75 0 0 1-.69 0Z";
        // file-directory-fill: a solid folder reads at 16 px (the outline one blurred into a blob).
        public const string Folder = "M1.75 1A1.75 1.75 0 0 0 0 2.75v10.5C0 14.216.784 15 1.75 15h12.5A1.75 1.75 0 0 0 16 13.25v-8.5A1.75 1.75 0 0 0 14.25 3H7.5a.25.25 0 0 1-.2-.1l-.9-1.2C6.07 1.26 5.55 1 5 1H1.75Z";
        public const string Plus = "M7.75 2a.75.75 0 0 1 .75.75V7h4.25a.75.75 0 0 1 0 1.5H8.5v4.25a.75.75 0 0 1-1.5 0V8.5H2.75a.75.75 0 0 1 0-1.5H7V2.75A.75.75 0 0 1 7.75 2Z";
        public const string Filter = "M.75 3h14.5a.75.75 0 0 1 0 1.5H.75a.75.75 0 0 1 0-1.5ZM3 7.75A.75.75 0 0 1 3.75 7h8.5a.75.75 0 0 1 0 1.5h-8.5A.75.75 0 0 1 3 7.75Zm3 4a.75.75 0 0 1 .75-.75h2.5a.75.75 0 0 1 0 1.5h-2.5a.75.75 0 0 1-.75-.75Z";

        public static Microsoft.UI.Xaml.Shapes.Path Icon(string data, double size = 16, Brush? fill = null) => new()
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
            Fill = fill ?? ThemeRes.Current["TextFillColorPrimaryBrush"] as Brush,
        };
    }

    /// <summary>The orders, each with its own icon.</summary>
    static readonly (string Key, string Name, string Icon)[] SortIcons =
    [
        ("recent", "Last played", Octicons.Clock), ("name", "Name", Octicons.Typography), ("added", "Date added", Octicons.Calendar),
        ("playtime", "Time played", Octicons.Stopwatch), ("achievements", "Achievements unlocked", Octicons.Trophy), ("tech", "Most tech tags", Octicons.Cpu),
    ];

    static Brush Muted => (Brush)ThemeRes.Current["TextFillColorSecondaryBrush"];

    /// <summary>A pill of the bar: the choice's name (muted), the chosen value's icon and name, and a chevron.</summary>
    static Button Pill(string name, FrameworkElement icon, string value, FlyoutBase flyout, int badge = 0)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (name.Length > 0) row.Children.Add(new TextBlock { Text = name, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 });
        if (badge > 0)
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9), MinWidth = 18, Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)ThemeRes.Current["AccentFillColorDefaultBrush"],
                Child = new TextBlock { Text = badge.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)ThemeRes.Current["TextOnAccentFillColorPrimaryBrush"] },
            });
        var chevron = Octicons.Icon(Octicons.ChevronDown, 12, Muted);
        chevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform();
        row.Children.Add(chevron);
        var button = new Button
        {
            Content = row, Height = 36, Padding = new Thickness(12, 0, 10, 0), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1), Flyout = flyout,
        };
        // The chevron turns while its list is open.
        flyout.Opened += (_, _) => ((RotateTransform)chevron.RenderTransform).Angle = 180;
        flyout.Closed += (_, _) => ((RotateTransform)chevron.RenderTransform).Angle = 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name.Length > 0 ? $"{name}: {value}" : value);
        return button;
    }

    static Flyout ListFlyout(FrameworkElement content)
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 260.0));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420.0));
        return new Flyout { Content = content, FlyoutPresenterStyle = style, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
    }

    /// <summary>One entry of a list: its icon, its name, a count on the right, and a tick when it is chosen.</summary>
    static Button Entry(FrameworkElement icon, string text, string? count, bool on, Action click)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        row.Children.Add(icon);
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (on) label.FontWeight = FontWeights.SemiBold;
        else label.Foreground = Muted;
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        if (count is not null)
        {
            var c = new TextBlock { Text = count, FontSize = 12, Foreground = (Brush)ThemeRes.Current["TextFillColorTertiaryBrush"], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(c, 2);
            row.Children.Add(c);
        }
        var tick = Octicons.Icon(Octicons.Check, 14);
        tick.Opacity = on ? 1 : 0;
        Grid.SetColumn(tick, 3);
        row.Children.Add(tick);
        var b = new Button
        {
            Content = row, Height = 36, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static TextBlock ListLabel(string text) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(10, 8, 10, 4),
    };

    /// <summary>
    /// Saves a change of filters and redraws what they belong to: the library's change the grid, the side list's change the
    /// list (2.1-rc.4: one set used to drive both, and a change of either redrew the two).
    /// </summary>
    static void FiltersChanged(LibraryFilter? f = null)
    {
        SettingsStore.Save();
        if (f is not null && f == AppState.Settings.ListFilter) App.Window?.RefreshList();
        else App.Window?.RefreshLibrary();
    }

    /// <summary>The launchers to show: All (a stack) or each launcher (its logo) with its games; several can be ticked.</summary>
    internal static StackPanel LaunchersList(Action? changed = null, LibraryFilter? filter = null)
    {
        var f = filter ?? AppState.Settings.Library;
        var panel = new StackPanel { Spacing = 1 };
        void Fill()
        {
            panel.Children.Clear();
            var groups = Library.Groups(AppState.VisibleGames.Where(g => !g.NotInstalled));
            // Launchers with games owned but none installed are listed too, when those games are shown.
            var ownedStores = f.ShowNotInstalled && OwnedLibrary.IsLoaded
                ? OwnedLibrary.NotInstalled().GroupBy(o => o.Store).Where(g => groups.All(x => x.Key != "store:" + g.Key)).ToList() : [];
            panel.Children.Add(Entry(Octicons.Icon(Octicons.Stack), L.T("All launchers"), null, f.Launchers.Count == 0, () =>
            {
                f.Launchers.Clear();
                FiltersChanged(f);
                changed?.Invoke();
                Fill();
            }));
            var keys = groups.Select(g => g.Key).Concat(ownedStores.Select(o => "store:" + o.Key)).ToList();
            void Toggle(string key)
            {
                // No launcher ticked = every launcher; ticking one from "all" shows only that one.
                if (f.Launchers.Count == 0) f.Launchers.Add(key);
                else if (!f.Launchers.Remove(key)) f.Launchers.Add(key);
                if (keys.All(f.Launchers.Contains)) f.Launchers.Clear();
                FiltersChanged(f);
                changed?.Invoke();
                Fill();
            }
            foreach (var (key, games) in groups)
            {
                var g = StoreLogos.GroupOf(games[0]);
                int owned = f.ShowNotInstalled && OwnedLibrary.IsLoaded && g.Folder is null
                    ? OwnedLibrary.NotInstalled().Count(o => "store:" + o.Store == key) : 0;
                var k = key;
                panel.Children.Add(Entry(StoreLogos.Tile(games[0].Store, g.Folder is not null, 18), g.Label,
                    owned > 0 ? $"{games.Count} + {owned}" : games.Count.ToString(), f.Launchers.Count > 0 && f.Launchers.Contains(key), () => Toggle(k)));
            }
            foreach (var o in ownedStores)
            {
                var k = "store:" + o.Key;
                panel.Children.Add(Entry(StoreLogos.Tile(o.Key, false, 18), StoreLogos.Label(o.Key), $"0 + {o.Count()}", f.Launchers.Count > 0 && f.Launchers.Contains(k), () => Toggle(k)));
            }
        }
        Fill();
        return panel;
    }

    /// <summary>The orders, each with its icon (picking one closes the list), and Reverse.</summary>
    internal static StackPanel SortList(Action picked, LibraryFilter? filter = null)
    {
        var f = filter ?? AppState.Settings.Library;
        var panel = new StackPanel { Spacing = 1 };
        void Fill()
        {
            panel.Children.Clear();
            foreach (var (key, name, icon) in SortIcons)
            {
                var k = key;
                panel.Children.Add(Entry(Octicons.Icon(icon), L.T(name), null, f.Sort == key, () =>
                {
                    if (f.Sort != k) { f.Sort = k; FiltersChanged(f); }
                    Fill();
                    picked();
                }));
            }
            panel.Children.Add(new Border { Height = 1, Margin = new Thickness(4), Background = (Brush)ThemeRes.Current["DividerStrokeColorDefaultBrush"] });
            panel.Children.Add(Entry(Octicons.Icon(Octicons.SortDesc), L.T("Reverse the order"), null, f.Descending, () =>
            {
                f.Descending = !f.Descending;
                FiltersChanged(f);
                Fill();
                picked();
            }));
        }
        Fill();
        return panel;
    }

    // One colour per collection (2.1-rc.9): each folder keeps its own, so a collection is told apart at a glance.
    static readonly Color[] FolderColors =
    [
        Color.FromArgb(0xFF, 0x4F, 0x9D, 0xFF), Color.FromArgb(0xFF, 0x3D, 0xC9, 0x8A), Color.FromArgb(0xFF, 0xF5, 0xB2, 0x3C),
        Color.FromArgb(0xFF, 0xA7, 0x7B, 0xFF), Color.FromArgb(0xFF, 0x2E, 0xC4, 0xD6), Color.FromArgb(0xFF, 0xFF, 0x8A, 0x4C),
    ];

    /// <summary>A solid folder in the collection's own colour (stable: picked from its id).</summary>
    internal static FrameworkElement CollectionIcon(string id)
    {
        int h = 0;
        foreach (var ch in id) h = h * 31 + ch;
        return Octicons.Icon(Octicons.Folder, 16, new SolidColorBrush(FolderColors[(int)((uint)h % FolderColors.Length)]));
    }

    static readonly Brush HeartRed = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x5A, 0x6E));

    /// <summary>
    /// The collections (2.1, Hydra style): every game, the favorites (the heart), each of the user's collections with its
    /// games, and New collection…. A right-click on a collection renames or deletes it. Picking one closes the list.
    /// </summary>
    internal static StackPanel CollectionList(Action picked, LibraryFilter? filter = null)
    {
        var f = filter ?? AppState.Settings.Library;
        var panel = new StackPanel { Spacing = 1 };
        void Pick(string? id)
        {
            if (f.Collection != id) { f.Collection = id; FiltersChanged(f); }
            picked();
        }
        panel.Children.Add(Entry(Octicons.Icon(Octicons.Stack), L.T("All games"), null, f.Collection is null, () => Pick(null)));
        panel.Children.Add(Entry(Octicons.Icon(Octicons.Heart, 16, HeartRed), L.T("Favorites"), GameCollections.Count(GameCollections.Favorites).ToString(),
            f.Collection == GameCollections.Favorites, () => Pick(GameCollections.Favorites)));
        foreach (var c in GameCollections.All.ToList())
        {
            var id = c.Id;
            var entry = Entry(CollectionIcon(id), c.Name, c.Games.Count.ToString(), f.Collection == id, () => Pick(id));
            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = L.T("Rename…"), Icon = new FontIcon { Glyph = "\uE8AC" } };
            rename.Click += (_, _) => { picked(); _ = App.Window?.RenameCollectionAsync(id); };
            var delete = new MenuFlyoutItem { Text = L.T("Delete…"), Icon = new FontIcon { Glyph = "\uE74D" } };
            delete.Click += (_, _) => { picked(); _ = App.Window?.DeleteCollectionAsync(id); };
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            entry.ContextFlyout = menu;
            ToolTipService.SetToolTip(entry, L.T("Right-click to rename or delete it"));
            panel.Children.Add(entry);
        }
        panel.Children.Add(new Border { Height = 1, Margin = new Thickness(4), Background = (Brush)ThemeRes.Current["DividerStrokeColorDefaultBrush"] });
        panel.Children.Add(Entry(Octicons.Icon(Octicons.Plus), L.T("New collection…"), null, false, () =>
        {
            picked();
            _ = NewAndShowAsync();
        }));
        async Task NewAndShowAsync()
        {
            if (App.Window is not { } w || await w.NewCollectionAsync() is not { } id) return;
            f.Collection = id;
            FiltersChanged(f);
        }
        return panel;
    }

    /// <summary>What the collection pill shows: All, Favorites or the collection's name.</summary>
    static (FrameworkElement Icon, string Value) CollectionValue()
    {
        var c = AppState.Settings.Library.Collection;
        if (c == GameCollections.Favorites) return (Octicons.Icon(Octicons.Heart, 16, HeartRed), L.T("Favorites"));
        if (GameCollections.Find(c) is { } col) return (CollectionIcon(col.Id), col.Name);
        return (Octicons.Icon(Octicons.Stack), L.T("All"));
    }

    /// <summary>What the launchers pill shows: All, the one launcher ticked, or how many.</summary>
    static (FrameworkElement Icon, string Value) LaunchersValue()
    {
        var f = AppState.Settings.Library;
        if (f.Launchers.Count == 0) return (Octicons.Icon(Octicons.Stack), L.T("All"));
        if (f.Launchers.Count == 1)
        {
            var key = f.Launchers.First();
            var sample = AppState.VisibleGames.FirstOrDefault(g => StoreLogos.GroupOf(g).Key == key);
            if (sample is not null)
            {
                var g = StoreLogos.GroupOf(sample);
                return (StoreLogos.Tile(sample.Store, g.Folder is not null, 18), g.Label);
            }
            if (key.StartsWith("store:", StringComparison.Ordinal) && Enum.TryParse<GameStore>(key[6..], out var store))
                return (StoreLogos.Tile(store, false, 18), StoreLogos.Label(store));
        }
        return (Octicons.Icon(Octicons.Stack), L.N(f.Launchers.Count, "{0} launcher", "{0} launchers"));
    }

    void Watch(Flyout flyout)
    {
        // Counted from Opening (it comes at once with the click, Opened a frame later): going straight from one pill's list
        // to another's, the bar is never drawn again under the list that is opening (2.1-rc.10 audit).
        flyout.Opening += (_, _) => _toolbarOpen++;
        flyout.Closed += (_, _) =>
        {
            _toolbarOpen = Math.Max(0, _toolbarOpen - 1);
            DispatcherQueue.TryEnqueue(FillToolbar);
        };
    }

    /// <summary>What the pills show now: the bar is drawn again only when it changed.</summary>
    static string ToolbarKey()
    {
        var f = AppState.Settings.Library;
        return string.Join("|", f.Collection, string.Join(",", f.Launchers.Order(StringComparer.OrdinalIgnoreCase)), f.Sort, f.Descending, f.OptiOnly, f.Tech.Count,
            f.ShowNotInstalled, string.Join(",", GameCollections.All.Select(c => c.Id + ":" + c.Name)), L.T("Collection"), AppState.Settings.Palette, AppState.Settings.Theme);
    }

    string? _toolbarKey;

    /// <summary>Draws the bar's pills for the settings as they are now (after every change).</summary>
    // Lists of the bar open: the bar is drawn again once they all closed (its own button must not go away under it).
    int _toolbarOpen;

    void FillToolbar()
    {
        if (_toolbarOpen > 0) return;
        var key = ToolbarKey();
        if (key == _toolbarKey && Choices.Children.Count > 0) return;
        _toolbarKey = key;
        Choices.Children.Clear();
        var f = AppState.Settings.Library;

        var (cIcon, cValue) = CollectionValue();
        Flyout? collections = null;
        collections = ListFlyout(new StackPanel());
        collections.Opening += (_, _) =>
        {
            var p = new StackPanel();
            p.Children.Add(ListLabel(L.T("COLLECTION")));
            p.Children.Add(CollectionList(() => collections!.Hide()));
            collections.Content = p;
        };
        Watch(collections);
        Choices.Children.Add(Pill(L.T("Collection"), cIcon, cValue, collections));

        var (lIcon, lValue) = LaunchersValue();
        Flyout? launchers = null;
        launchers = ListFlyout(new StackPanel());
        launchers.Opening += (_, _) =>
        {
            var p = new StackPanel();
            p.Children.Add(ListLabel(L.T("LAUNCHERS")));
            p.Children.Add(LaunchersList());
            launchers.Content = p;
        };
        Watch(launchers);
        Choices.Children.Add(Pill(L.T("Launchers"), lIcon, lValue, launchers));

        var sort = SortIcons.FirstOrDefault(s => s.Key == f.Sort);
        if (sort.Key is null) sort = SortIcons[0];
        Flyout? sorts = null;
        sorts = ListFlyout(new StackPanel());
        sorts.Opening += (_, _) =>
        {
            var p = new StackPanel();
            p.Children.Add(ListLabel(L.T("SORT BY")));
            p.Children.Add(SortList(() => sorts!.Hide()));
            sorts.Content = p;
        };
        Watch(sorts);
        var sortIcon = Octicons.Icon(f.Descending ? Octicons.SortDesc : sort.Icon);
        Choices.Children.Add(Pill(L.T("Sort by"), sortIcon, L.T(sort.Name) + (f.Descending ? " ↓" : ""), sorts));

        // The rest (games not installed, OptiScaler, tech, the covers' look) behind a Filters pill.
        int other = (f.OptiOnly ? 1 : 0) + (f.Tech.Count > 0 ? 1 : 0) + (f.ShowNotInstalled ? 1 : 0);
        var filters = ListFlyout(new StackPanel());
        filters.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        filters.Opening += (_, _) => filters.Content = FiltersPanel(filters.Hide, full: false);
        Watch(filters);
        Choices.Children.Add(Pill("", Octicons.Icon(Octicons.Filter), L.T("Filters"), filters, other));
    }
}
