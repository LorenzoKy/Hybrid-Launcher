using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace HybridLauncher.Views;

/// <summary>
/// The covers of the games owned but not installed (Filters → "Also games you own but have not installed"), 2.1-rc.3: an
/// ItemsRepeater that makes only the covers on screen (and a few around), so a library of hundreds of owned games costs what
/// a screenful does. Before, up to 300 covers were all made, each loading its picture, on every change of a filter, and all
/// made again after a zoom: 1-3.7 s with the window frozen in the user's log. A zoom now resizes the covers made, in place.
/// </summary>
public sealed partial class LibraryPage
{
    readonly List<OwnedCovers> _ownedBlocks = [];

    sealed class OwnedCovers
    {
        static DataTemplate? _template;
        public readonly ItemsRepeater Repeater;
        readonly UniformGridLayout _layout;
        readonly HashSet<ContentControl> _made = [];
        readonly List<OwnedGame> _games;
        double _width;

        public int Count => _games.Count;

        public OwnedCovers(List<OwnedGame> games, double width)
        {
            _games = games;
            _template ??= (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "<ContentControl IsTabStop='False' HorizontalContentAlignment='Left' VerticalContentAlignment='Top' /></DataTemplate>");
            _layout = new UniformGridLayout
            {
                MinColumnSpacing = Gap, MinRowSpacing = Gap, ItemsStretch = UniformGridLayoutItemsStretch.None,
                ItemsJustification = UniformGridLayoutItemsJustification.Start,
            };
            Repeater = new ItemsRepeater { Layout = _layout, ItemTemplate = _template, ItemsSource = games.Cast<object>().ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
            Repeater.ElementPrepared += (_, e) =>
            {
                if (e.Element is not ContentControl host || e.Index < 0 || e.Index >= _games.Count) return;
                var owned = _games[e.Index];
                // The library's own menu (Install, Collection, Hide) only: the cover's built-in one is left off.
                var tile = OwnedLibrary.Tile(owned, _width, withMenu: false);
                tile.ContextRequested += (sender, args) =>
                {
                    if (App.Window is not { } w || sender is not FrameworkElement root) return;
                    var menu = w.OwnedMenu(owned);
                    if (args.TryGetPosition(root, out var at)) menu.ShowAt(root, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at });
                    else menu.ShowAt(root);
                    args.Handled = true;
                };
                host.Content = tile;
                _made.Add(host);
            };
            Repeater.ElementClearing += (_, e) =>
            {
                if (e.Element is not ContentControl host) return;
                host.Content = null;
                _made.Remove(host);
            };
            Resize(width);
        }

        /// <summary>The covers' width (zoom, page width): the slots follow, and the covers on screen are resized in place.</summary>
        public void Resize(double width)
        {
            if (width <= 0 || Math.Abs(width - _width) < 0.5) return;
            _width = width;
            _layout.MinItemWidth = width;
            // The art (2:3) and the two lines of text under it.
            _layout.MinItemHeight = Math.Round(width * 1.5) + 52;
            foreach (var host in _made)
                if (host.Content is FrameworkElement tile) OwnedLibrary.ResizeTile(tile, width);
        }
    }

    /// <summary>A block of not-installed covers for <paramref name="games"/>, at the page's cover width.</summary>
    FrameworkElement OwnedBlock(List<OwnedGame> games)
    {
        var block = new OwnedCovers(games, _tileWidth > 0 ? _tileWidth : 204);
        _ownedBlocks.Add(block);
        return block.Repeater;
    }

    void ResizeOwned(double width)
    {
        foreach (var b in _ownedBlocks) b.Resize(width);
    }

    /// <summary>The "NOT INSTALLED" heading: a download glyph, the label and how many.</summary>
    static FrameworkElement OwnedHeading(int count, bool small)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, small ? 2 : 6, 0, 0) };
        head.Children.Add(new FontIcon { Glyph = "", FontSize = small ? 11 : 13, Foreground = (Microsoft.UI.Xaml.Media.Brush)ThemeRes.Current["AccentTextFillColorPrimaryBrush"] });
        head.Children.Add(new TextBlock
        {
            Text = L.T("NOT INSTALLED"), Style = (Style)ThemeRes.Current[small ? "RowDescription" : "GroupHeader"], VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        head.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        return head;
    }

    /// <summary>
    /// The owned games that are not installed, as the filters ask (the side list uses the same): the search, the launchers
    /// ticked, the order (name, or last played / time played / achievements once their stats are in) and Reverse. Never with
    /// "only games with OptiScaler" (a game not on this PC has none); the tech filter needs tags these games do not have yet,
    /// so they pass it.
    /// </summary>
    internal static List<OwnedGame> OwnedShown(string filter, LibraryFilter? settings = null)
    {
        var f = settings ?? AppState.Settings.Library;
        if (!f.ShowNotInstalled || f.OptiOnly || !OwnedLibrary.IsLoaded) return [];
        var hidden = AppState.Settings.HiddenGames;
        var list = OwnedLibrary.NotInstalled()
            .Where(g => !hidden.Contains(g.Id))
            .Where(g => filter.Length == 0 || g.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .Where(g => f.Launchers.Count == 0 || f.Launchers.Contains("store:" + g.Store))
            .Where(g => f.Collection is not { } c || GameCollections.Contains(c, g.Id));
        Func<OwnedGame, IComparable> key = f.Sort switch
        {
            "recent" => g => Library.StatsOf(g.Id)?.LastPlayed ?? DateTime.MinValue,
            "playtime" => g => Library.StatsOf(g.Id)?.Minutes ?? 0,
            "achievements" => g => Library.StatsOf(g.Id) is { AchTotal: > 0 } s ? s.AchUnlocked * 1000.0 + (double)s.AchUnlocked / s.AchTotal : -1.0,
            _ => g => g.Name.ToLowerInvariant(),
        };
        bool byName = f.Sort is "name" or "added" or "tech";
        bool descending = !byName;
        if (f.Descending) descending = !descending;
        var ordered = descending ? list.OrderByDescending(key) : list.OrderBy(key);
        return ordered.ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
