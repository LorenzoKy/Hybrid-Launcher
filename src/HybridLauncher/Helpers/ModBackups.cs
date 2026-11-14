using HybridLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Helpers;

/// <summary>
/// Before a mod goes into a game folder: whether the game's own files it replaces are backed up first (and put back on
/// uninstall) or just replaced. Settings → Features → Mods: backups on or off (2.1, the master switch), and when on, made at
/// once or asked at each install.
/// </summary>
public static class ModBackups
{
    public static readonly string[] Modes = ["backup", "ask"];

    public static string ModeName(string mode) => mode switch
    {
        "ask" => L.T("Ask at each install"),
        _ => L.T("Always"),
    };

    /// <summary>True: back up; false: replace without a backup; null: the user cancelled the install.</summary>
    public static async Task<bool?> DecideAsync(string mod, string game)
    {
        if (!AppState.Settings.BackupGameFiles) return false;
        switch (AppState.Settings.ModBackups)
        {
            case "replace": return false;
            case not "ask": return true;
        }
        if (App.Window?.Content?.XamlRoot is not { } root) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (App.Window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Title = L.F("Install {0} in {1}", mod, game),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = L.T("Files of the game that the mod replaces can be backed up first: uninstalling the mod then puts them back. Without a backup they are simply replaced, and uninstalling only removes the mod's files."),
            },
            PrimaryButtonText = L.T("Back up and install"),
            SecondaryButtonText = L.T("Just replace"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowOnceAsync() switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null,
        };
    }
}
