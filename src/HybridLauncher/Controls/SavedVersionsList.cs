using HybridLauncher.Core;
using HybridLauncher.Core.Opti;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Controls;

/// <summary>
/// The OptiScaler versions kept on this PC (downloaded releases and imported builds), one row each:
/// channel, name, date, size, the games using it, and a bin button that deletes it after a one-click confirmation.
/// </summary>
public sealed partial class SavedVersionsList : UserControl
{
    readonly StackPanel _rows = new() { Spacing = 4 };
    readonly TextBlock _summary = new();
    CancellationTokenSource? _cts;

    /// <summary>Raised after a version was deleted.</summary>
    public event Action? Changed;

    public SavedVersionsList()
    {
        _summary.Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"];
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_summary);
        panel.Children.Add(_rows);
        Content = panel;
        Loaded += (_, _) => _ = ReloadAsync();
        Unloaded += (_, _) => _cts?.Cancel();
    }

    public async Task ReloadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var list = await Task.Run(() => SavedVersions.ListAsync(AppState.Settings, ct), ct);
            if (ct.IsCancellationRequested) return;
            _rows.Children.Clear();
            _summary.Text = list.Count == 0
                ? L.T("None yet. Versions are saved here when you install them, and custom builds when you import them.")
                : L.N(list.Count, "{0} version", "{0} versions") + "  ·  " + L.F("{0} in total. Deleting one never breaks a game: installs don't depend on it.", DiskUsage.Format(list.Sum(v => v.Bytes)));
            foreach (var v in list) _rows.Children.Add(Row(v));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"Saved versions: {ex.Message}");
            _summary.Text = L.T("Could not read the saved versions.");
        }
    }

    /// <summary>The key of a version's own name: its folder's name, so it survives moving the builds folder.</summary>
    static string NameKey(SavedVersion v) => Path.GetFileName(v.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>The name shown for a version: the one the user gave it, else the release name.</summary>
    static string Shown(SavedVersion v) => AppState.Settings.VersionNames.TryGetValue(NameKey(v), out var custom) && custom.Length > 0 ? custom : v.Name;

    FrameworkElement Row(SavedVersion v)
    {
        string shown = Shown(v);
        bool renamed = shown != v.Name;
        // Delete questions name the version both ways, so it is never unclear which one goes.
        string both = renamed ? $"{shown} ({v.Name})" : v.Name;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(v.Channel switch
            {
                OptiChannel.Nightly => Windows.UI.Color.FromArgb(0x55, 0xD2, 0x99, 0x22),
                OptiChannel.Custom => Windows.UI.Color.FromArgb(0x55, 0x9A, 0x5C, 0xFF),
                _ => Windows.UI.Color.FromArgb(0x55, 0x2E, 0xA0, 0x43),
            }),
            Child = new TextBlock { Text = L.T(v.ChannelLabel).ToUpper(), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 40 },
        };

        var usedBy = SavedVersions.UsedBy(v, AppState.Settings, id => AppState.Find(id)?.Name);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = shown, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowHeader"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        text.Children.Add(new TextBlock
        {
            Text = (renamed ? v.Name + "  ·  " : "") + $"{v.Date:yyyy-MM-dd}  ·  {DiskUsage.Format(v.Bytes)}" + (usedBy.Count > 0 ? "  ·  " + L.F("installed in {0}", string.Join(", ", usedBy)) : ""),
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        var delete = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Padding = new Thickness(8, 6, 8, 6),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(delete, L.F("Delete {0}", both));

        // One more click to confirm, right where the bin is.
        var confirm = new Button { Content = L.F("Delete ({0})", DiskUsage.Format(v.Bytes)), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["AccentButtonStyle"] };
        var flyoutText = new TextBlock
        {
            Text = usedBy.Count > 0 ? L.F("Delete {0}? {1}: the games keep working and can still be uninstalled.", both, string.Join(", ", usedBy)) : L.F("Delete {0}?", both),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
        };
        var flyoutPanel = new StackPanel { Spacing = 10 };
        flyoutPanel.Children.Add(flyoutText);
        flyoutPanel.Children.Add(confirm);
        var flyout = new Flyout { Content = flyoutPanel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.LeftEdgeAlignedTop };
        delete.Flyout = flyout;
        confirm.Click += async (_, _) =>
        {
            flyout.Hide();
            try
            {
                await Task.Run(() => SavedVersions.Delete(v));
                AppState.Settings.VersionNames.Remove(NameKey(v));
                SettingsStore.Save();
                App.Window?.ShowSuccess(L.F("Deleted {0} ({1} freed).", both, DiskUsage.Format(v.Bytes)));
            }
            catch (Exception ex)
            {
                App.Window?.ShowError(L.T("Could not delete the version"), ex.Message);
            }
            await ReloadAsync();
            Changed?.Invoke();
        };

        var rename = new Button
        {
            Content = new FontIcon { Glyph = "\uE70F", FontSize = 14 },
            Padding = new Thickness(8, 6, 8, 6),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(rename, L.T("Rename (only in Hybrid Launcher: the folder keeps its name)"));
        rename.Click += async (_, _) =>
        {
            var name = await NameDialog.AskAsync(XamlRoot, ActualTheme, L.T("Rename this version"), v.Name, renamed ? shown : "");
            if (name is null) return;
            if (name.Length == 0 || name == v.Name) AppState.Settings.VersionNames.Remove(NameKey(v));
            else AppState.Settings.VersionNames[NameKey(v)] = name;
            SettingsStore.Save();
            await ReloadAsync();
            Changed?.Invoke();
        };

        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(chip);
        chip.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(rename, 2);
        grid.Children.Add(rename);
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);
        ToolTipService.SetToolTip(text, v.Folder);

        var row = new Border { Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowStyle"], Child = grid };
        row.Padding = new Thickness(10, 6, 4, 6);
        row.MinHeight = 0;
        return row;
    }
}

/// <summary>A small dialog that asks for a display name (an ASI plugin, a saved OptiScaler version): only Hybrid Launcher shows it, files keep their names.</summary>
public static class NameDialog
{
    /// <summary>The new name; "" when it was emptied (back to <paramref name="original"/>); null when cancelled.</summary>
    public static async Task<string?> AskAsync(XamlRoot root, ElementTheme theme, string title, string original, string current)
    {
        var box = new TextBox { Text = current, PlaceholderText = original, MaxLength = 80, MinWidth = 320 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = L.F("Leave it empty to go back to {0}.", original),
            Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = panel,
            PrimaryButtonText = L.T("Save"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = theme,
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}
