using HybridLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Helpers;

/// <summary>
/// Uninstall from the game page or the list: says what goes (the installed files, never saves or settings), starts it the
/// launcher's way, then watches the disk and drops the game from the list once its files are gone.
/// </summary>
static class UninstallFlow
{
    static readonly HashSet<string> Watching = new(StringComparer.OrdinalIgnoreCase);

    public static async Task RunAsync(Game game, XamlRoot root, ElementTheme theme)
    {
        try
        {
            if (GameSessions.IsActive(game.Id))
            {
                App.Window?.ShowError(L.F("{0} is running", game.Name), L.T("Close the game first."));
                return;
            }
            var route = Uninstaller.Route(game);
            string how = route switch
            {
                UninstallRoute.Steam => L.T("Hybrid Launcher removes it directly: Steam is closed for a moment if it is running (it would write the game back), the game folder and Steam's record of it are deleted, then Steam starts again."),
                UninstallRoute.Uninstaller => L.T("Hybrid Launcher runs the game's own uninstaller silently: no GOG Galaxy needed."),
                UninstallRoute.Epic => L.T("Hybrid Launcher removes it directly: the Epic Games Launcher is closed if it is running, then the game folder and the launcher's records of it are deleted."),
                UninstallRoute.Direct => L.T("Hybrid Launcher downloaded it: its folder is deleted for good."),
                UninstallRoute.Launcher => L.F("{0} opens: remove the game there, Hybrid Launcher drops it from the list once it is gone.", game.StoreLabel),
                _ => L.T("Its folder goes to the Recycle Bin, so you can still bring it back."),
            };
            var text = new StackPanel { Spacing = 10 };
            text.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = L.T("Only the installed files are removed. Saves, settings and mods kept outside the game folder stay where they are.") });
            text.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = how });
            if (route is UninstallRoute.RecycleBin or UninstallRoute.Direct or UninstallRoute.Steam or UninstallRoute.Epic)
                text.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Text = game.InstallDir, IsTextSelectionEnabled = true });
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = theme,
                Title = L.F("Uninstall {0}?", game.Name),
                Content = text,
                PrimaryButtonText = L.T("Uninstall"),
                CloseButtonText = L.T("Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var error = await Task.Run(() => Uninstaller.Start(game));
            if (error is not null)
            {
                App.Window?.ShowError(L.T("Could not uninstall"), error);
                return;
            }
            if (route != UninstallRoute.Launcher && await Task.Run(() => Uninstaller.IsGone(game))) await GoneAsync(game);
            else _ = WatchAsync(game);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not uninstall"), ex.Message); }
    }

    /// <summary>Checks the disk every 2 s for 20 minutes (the launcher's own dialog may wait for the user).</summary>
    static async Task WatchAsync(Game game)
    {
        if (!Watching.Add(game.Id)) return;
        try
        {
            var until = DateTime.UtcNow.AddMinutes(20);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(2000);
                if (await Task.Run(() => Uninstaller.IsGone(game)))
                {
                    await GoneAsync(game);
                    return;
                }
            }
        }
        finally { Watching.Remove(game.Id); }
    }

    static async Task GoneAsync(Game game)
    {
        Log.Info($"Uninstalled: {game.Name}");
        await AppState.RescanAsync();
        App.Window?.ShowInfo(L.F("{0} was uninstalled.", game.Name));
        App.Window?.GameRemoved(game.Id);
    }
}
