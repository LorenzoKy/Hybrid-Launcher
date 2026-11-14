using HybridLauncher.Core;
using HybridLauncher.Core.Media;
using HybridLauncher.Core.Opti;
using Microsoft.UI.Dispatching;

namespace HybridLauncher.Helpers;

/// <summary>
/// After start, in the background: everything a game page shows is fetched for every game in the list, ahead of the click, so
/// the first visit of a game opens from the disk instead of waiting for Steam, PCGamingWiki or the OptiScaler wiki (the art, the
/// store page, both wikis, HowLongToBeat, the reviews). The games the user opens most (pinned, the last one, recently played)
/// go first. Two games at a time, a short pause between them; it waits while a game runs (a launcher must not take the network
/// or the CPU from the game) and stops when the app closes. Everything lands in the same disk cache the pages read, bounded by
/// Settings → Storage → Game page cache. Once per start: each source refreshes its own data when it is due.
/// The first run on a PC (2.0) starts sooner and runs four games at a time, and also reads each game's achievements, news,
/// graphics API and cover, so after the first start every page opens from the cache.
/// </summary>
public static class WarmUp
{
    static int _started;

    public static void Start(DispatcherQueue ui)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        if (Environment.GetEnvironmentVariable("HL_NO_WARMUP") == "1") return;
        // The list, in the order a hand is most likely to open them, read on the UI thread.
        var s = AppState.Settings;
        var games = AppState.VisibleGames
            .OrderByDescending(g => g.Id.Equals(s.LastGameId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(g => AppState.IsPinned(g.Id))
            .ThenByDescending(g => s.For(g.Id).LastPlayed ?? DateTime.MinValue)
            .ToList();
        bool first = !s.CacheWarmed;
        var exes = games.ToDictionary(g => g.Id, g => Core.Detection.GameScanner.EffectiveExe(g, s.For(g.Id)), StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(async () =>
        {
            // The graphics cards are read once and kept; read here, the first Mods tab or DLSS 5 card does not read them on the
            // UI thread (2.0.1: real logs showed one 330-490 ms "GamePage.ModsSection" stall per session).
            Core.Hardware.HardwareInfo.Gpus();
            int done = await RunAsync(games, exes, first);
            if (first && done == games.Count && !App.ShuttingDown)
                ui.TryEnqueue(() => { AppState.Settings.CacheWarmed = true; SettingsStore.Save(); });
        });
    }

    static async Task<int> RunAsync(List<Game> games, Dictionary<string, string?> exes, bool first)
    {
        // The start's own work first (scan, the first page, the stats); on the very first start, sooner and wider.
        await Task.Delay(TimeSpan.FromSeconds(first ? 3 : 6));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int done = 0;
        using var gate = new SemaphoreSlim(first ? 4 : 2);
        var tasks = new List<Task>();
        foreach (var game in games)
        {
            if (App.ShuttingDown) return done;
            // Nothing while a game is being played.
            while (MediaPolicy.GameRunning && !App.ShuttingDown) await Task.Delay(TimeSpan.FromSeconds(10));
            await gate.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try { await OneAsync(game, exes.GetValueOrDefault(game.Id)); Interlocked.Increment(ref done); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn($"Warm-up of {game.Name}: {ex.Message}"); }
                finally { gate.Release(); }
            }));
            await Task.Delay(first ? 50 : 150);
        }
        await Task.WhenAll(tasks);
        Log.Info($"Warm-up{(first ? " (first start)" : "")}: {done} of {games.Count} game pages ready in the cache ({sw.Elapsed.TotalSeconds:0.0} s)");
        return done;
    }

    static async Task OneAsync(Game game, string? exe)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;
        var art = await GameInfo.GetAsync(game, ct);
        var appId = art.SteamAppId ?? game.SteamAppId;
        var name = GameNames.PlainOrClean(GameNames.LookupName(game));
        var work = new List<Task>
        {
            Pcgw.GetAsync(game, appId, ct),
            CompatWiki.GetEntryAsync(name, ct),
            HowLongToBeat.GetAsync(name, ct),
            // The library's cover and the Dashboard's achievements card.
            GameInfo.GetCoverAsync(game, ct),
            Core.Steam.SteamAchievements.LoadForAsync(game, ct),
        };
        if (!game.NotInstalled) work.Add(GraphicsApi.GetAsync(game, exe, appId, ct));
        if (appId is not null)
        {
            work.Add(StoreDetails.GetAsync(appId, ct));
            work.Add(SteamReviews.GetAsync(appId, ct));
            // The Dashboard's news and updates.
            work.Add(GameEvents.ForAppAsync(appId, ct));
        }
        try { await Task.WhenAll(work); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info($"Warm-up of {game.Name}: {ex.GetBaseException().Message}"); }
    }
}
