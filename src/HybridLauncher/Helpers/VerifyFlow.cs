using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Detection;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Helpers;

/// <summary>
/// "Verify game files…" (2.1): says what will happen, warns about the mods a verify can undo, then has the game's own
/// launcher verify it: started at once (Steam, Epic) or opened with the steps (the others). See <see cref="GameVerify"/>.
/// 2.1-rc.3 verified inside Hybrid Launcher (<see cref="Downloads.StartVerify"/>); it did not work well enough and is off
/// since 2.1-rc.4 (the user's call), so the launcher always does it.
/// </summary>
public static class VerifyFlow
{
    public static async Task RunAsync(Game game, XamlRoot root, ElementTheme theme)
    {
        if (GameSessions.IsActive(game.Id)) { App.Window?.ShowInfo(L.F("Close {0} first: its files are in use.", game.Title)); return; }
        if (Downloads.Updating(game.Id)) { App.Window?.OpenDownloads(); return; }
        var plan = GameVerify.For(game);
        var panel = new StackPanel { Spacing = 10, MaxWidth = 520 };
        TextBlock Line(string text, bool muted = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            if (muted) t.Style = (Style)ThemeRes.Current["RowDescription"];
            return t;
        }

        if (!plan.Possible)
        {
            panel.Children.Add(Line(game.Direct
                ? L.T("Hybrid Launcher downloaded this game itself, so no launcher keeps a list of its files to check them against. Download it again to repair it.")
                : L.T("This game was not installed by a launcher Hybrid Launcher knows, so nothing can check its files. Reinstall it to repair it.")));
            await new ContentDialog { XamlRoot = root, RequestedTheme = theme, Title = L.T("Verify game files"), Content = panel, CloseButtonText = L.T("Close") }.ShowOnceAsync();
            return;
        }

        panel.Children.Add(Line(L.F("{0} checks every file of {1} against its own list and downloads again what is missing or damaged. It helps when the game crashes at start, after a failed update, or when a mod left it broken.", plan.Launcher!, game.Title)));
        // The mods a verify may undo: those that replace a file of the game's own.
        AppState.Settings.Games.TryGetValue(game.Id, out var gs);
        List<ModTag> mods;
        try { mods = await Task.Run(() => InstalledMods.Find(game, gs)); }
        catch { mods = []; }
        if (mods.Count > 0)
        {
            var bar = new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                Title = L.T("Mods in this game"),
                Message = L.F("Installed here: {0}. A verify puts back any game file a mod replaced (a swapped DLSS or FSR dll, a patched exe); files a mod added are usually left alone. Check the mods' tabs afterwards.",
                    string.Join(", ", mods.Select(m => m.Short switch { "Opti" => "OptiScaler", "RS" => "ReShade", "RDX" => "RenoDX", "U+" => "Ultra+", _ => m.Short }).Distinct())),
            };
            panel.Children.Add(bar);
        }
        if (!plan.Direct && plan.Steps is { } steps)
        {
            panel.Children.Add(Line(L.F("{0} has no direct link for this: Hybrid Launcher opens it, then:", plan.Launcher!), muted: true));
            panel.Children.Add(new Border
            {
                Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)ThemeRes.Current["CardBackgroundFillColorDefaultBrush"],
                Child = Line(L.T(steps)),
            });
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = theme,
            Title = L.F("Verify the files of {0}", game.Title),
            Content = panel,
            PrimaryButtonText = plan.Direct ? L.T("Verify") : L.F("Open {0}", plan.Launcher!),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
        Log.Info($"Verify {game.Name} through {plan.Launcher} ({(plan.Direct ? "direct" : "steps")})");
        if (!GameVerify.Start(plan))
            App.Window?.ShowInfo(L.F("{0} could not be opened. Is it installed?", plan.Launcher!));
        else if (plan.Direct)
            App.Window?.ShowInfo(L.F("{0} is checking the files of {1}.", plan.Launcher!, game.Title));
    }
}
