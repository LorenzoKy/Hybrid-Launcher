using HybridLauncher.Core;
using HybridLauncher.Views;

namespace HybridLauncher;

/// <summary>
/// Self-test "real library" (2.3-rc.7, run-selftest -RealLibrary, HL_SELFTEST_REAL=1): pages with the real covers, achievements,
/// news and sizes of your own games, which fake games do not have. Read only, by construction: on a COPY of the data folder
/// without the store and cloud sign-ins (nothing synced, updated or downloaded), only pages and read-only tabs are opened, and
/// none of the steps that install, remove or swap files runs (those stay on fake games, <see cref="IsScratchGame"/>).
/// </summary>
public sealed partial class MainWindow
{
    async Task<int> SelfTestRealLibraryAsync(Func<string, Action, int, Task> step, Func<string, Action, int, Task> settled)
    {
        int failures = 0;
        void Fail(string what, string why) { failures++; Log.Error($"Self-test FAILED: {what}: {why}"); }
        var games = AppState.VisibleGames.Where(g => !g.NotInstalled && !IsScratchGame(g)).ToList();
        int count = int.TryParse(Environment.GetEnvironmentVariable("HL_SELFTEST_REAL_GAMES"), out int n) && n > 0 ? n : 5;
        Log.Info($"Self-test real library: {games.Count} installed games, {AppState.VisibleGames.Count(g => g.NotInstalled)} owned not installed; touring {Math.Min(count, games.Count)}");
        if (games.Count == 0) { Fail("real library", "no installed game found (are the launchers' scans on in the copied settings?)"); return failures; }
        // The start may still be opening its first page (the list redrawn after the scan): the walk begins once it is done.
        await Task.Delay(1500);
        await SettleAsync(3000);

        await settled("real library", () => ContentFrame.Navigate(typeof(LibraryPage), ""), 2500);
        if (Shown is not LibraryPage) Fail("real library", $"opened {Shown?.GetType().Name}");
        await settled("real downloads", () => Nav.SelectedItem = Nav.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>().FirstOrDefault(i => i.Tag as string == DownloadsTag), 2500);
        if (Shown is not DownloadsPage) Fail("real downloads", $"opened {Shown?.GetType().Name}");

        // Read-only tabs only: what the game is, its achievements, news and reviews, PCGamingWiki, links.
        string[] tabs = ["overview", "ach", "overview/news", "overview/reviews", "pcgw", "links", "overview"];
        foreach (var game in games.Take(count))
        {
            await settled($"real {game.Name}: open", () => OpenGame(game.Id), 3000);
            if (Shown is not GamePage and not GameShellPage) { Fail($"real {game.Name}: open", $"opened {Shown?.GetType().Name}"); continue; }
            foreach (var tab in tabs)
            {
                await settled($"real {game.Name}: {tab}", () => (Shown as GamePage)?.SelfTestShow(tab), 1500);
                var cut = CutOnTheRight(ContentFrame);
                if (cut.Count > 0) Fail($"real {game.Name}: {tab}", $"past the right edge: {string.Join("; ", cut.Take(4))}");
            }
        }
        await settled("real library again", () => ContentFrame.Navigate(typeof(LibraryPage), ""), 1500);
        return failures;
    }
}
