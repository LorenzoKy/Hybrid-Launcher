using HybridLauncher.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Controls;

/// <summary>
/// "Customize tabs": every game page tab in the user's order (drag rows to reorder), each with two switches: shown in every
/// game, and shown in this game. Reset puts the default order back and shows every tab again.
/// </summary>
public static class TabsDialog
{
    const double CheckColumn = 92;

    /// <summary>Returns true when something changed.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, ElementTheme theme, GameSettings game, string gameName)
    {
        // As wide as the window allows (up to 820): the descriptions and both switches always fit, nothing is cut.
        double width = Math.Clamp(root.Size.Width - 96, 360, 820);
        var list = new ListView
        {
            CanReorderItems = true,
            AllowDrop = true,
            CanDragItems = true,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = Math.Max(240, root.Size.Height - 330),
        };
        var rows = new Dictionary<string, (CheckBox Everywhere, CheckBox Here)>(StringComparer.OrdinalIgnoreCase);

        static Grid Columns()
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CheckColumn) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CheckColumn) });
            return grid;
        }

        void Fill(bool defaults)
        {
            list.Items.Clear();
            rows.Clear();
            foreach (var t in defaults ? GameTabs.All.ToList() : GameTabs.Ordered())
            {
                CheckBox Box(bool on, string tip)
                {
                    var box = new CheckBox { MinWidth = 0, IsChecked = on, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
                    ToolTipService.SetToolTip(box, tip);
                    return box;
                }
                var everywhere = Box(defaults || !GameTabs.HiddenEverywhere(t.Id), L.T("Show this tab on every game page"));
                var here = Box(defaults || !game.HiddenTabs.Contains(t.Id), L.F("Show this tab for {0}", gameName));
                everywhere.Checked += (_, _) => here.IsEnabled = true;
                everywhere.Unchecked += (_, _) => here.IsEnabled = false;
                here.IsEnabled = everywhere.IsChecked == true;
                rows[t.Id] = (everywhere, here);

                var grid = Columns();
                grid.Padding = new Thickness(0, 6, 0, 6);
                grid.Tag = t.Id;
                FrameworkElement icon;
                if (GameTabs.Colored) icon = HybridLauncher.Helpers.Icons.Colored(t.Glyph, 18, t.Tint);
                else { icon = HybridLauncher.Helpers.Icons.Make(t.Glyph, 18); icon.VerticalAlignment = VerticalAlignment.Center; }
                Grid.SetColumn(icon, 1);
                grid.Children.Add(icon);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock { Text = t.Info, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(text, 2);
                grid.Children.Add(text);
                Grid.SetColumn(everywhere, 3);
                grid.Children.Add(everywhere);
                Grid.SetColumn(here, 4);
                grid.Children.Add(here);
                list.Items.Add(grid);
            }
        }
        Fill(false);

        // The two switch columns are named once, above the list.
        var head = Columns();
        head.Margin = new Thickness(12, 0, 12, 0);
        foreach (var (label, col) in new[] { (L.T("All games"), 3), (L.T("This game"), 4) })
        {
            var tb = new TextBlock { Text = label, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["GroupHeader"], HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(tb, col);
            head.Children.Add(tb);
        }

        var panel = new StackPanel { Spacing = 10, Width = width };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Drag the rows to change the order (you can also drag the tabs themselves). A tab turned off for all games is hidden on every game page; \"This game\" hides it only here. Right-click a tab for the same choices."),
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"],
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(head);
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = L.T("Customize tabs"),
            Content = panel,
            PrimaryButtonText = L.T("Save"),
            SecondaryButtonText = L.T("Reset to default"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        // WinUI caps dialogs at 548 px: room for the content's width plus the dialog's padding.
        dialog.Resources["ContentDialogMaxWidth"] = width + 48;
        // Reset fills the list with the defaults and keeps the dialog open, so it can still be adjusted or cancelled.
        dialog.SecondaryButtonClick += (_, e) =>
        {
            e.Cancel = true;
            Fill(true);
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        var order = list.Items.OfType<Grid>().Select(g => (string)g.Tag).ToList();
        GameTabs.SaveOrder(order);
        AppState.Settings.HiddenTabs.Clear();
        foreach (var (id, (everywhere, here)) in rows)
        {
            if (everywhere.IsChecked != true) AppState.Settings.HiddenTabs.Add(id);
            if (here.IsChecked != true) game.HiddenTabs.Add(id); else game.HiddenTabs.Remove(id);
        }
        // Never every tab: the game page needs one to show.
        if (GameTabs.All.All(t => GameTabs.Hidden(t.Id, game)))
        {
            AppState.Settings.HiddenTabs.Remove("overview");
            game.HiddenTabs.Remove("overview");
        }
        SettingsStore.Save();
        return true;
    }
}
