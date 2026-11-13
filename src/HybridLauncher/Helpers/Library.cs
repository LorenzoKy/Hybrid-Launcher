using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Media;
using HybridLauncher.Core.Opti;
using HybridLauncher.Core.Steam;

namespace HybridLauncher.Helpers;

/// <summary>What the library sorts and filters on, per game, gathered in the background once per session.</summary>
public sealed record GameStats(long Minutes, DateTime? LastPlayed, int AchUnlocked, int AchTotal, List<string>? Tags);

/// <summary>One unlocked achievement of a game in the library (the Library's "Latest achievements" panel).</summary>
public sealed record RecentUnlock(Game Game, Achievement Item, DateTime At);

/// <summary>
/// The library's groups (launchers, games folders) in the user's order, folded or not, and its sort and filters
/// (shared by the grid page and the list in the side pane).
/// </summary>
public static class Library
{
    static readonly Dictionary<string, GameStats> Stats = new(StringComparer.OrdinalIgnoreCase);
    static Task? _loading;

    /// <summary>Raised on the UI thread when the background stats (play time, achievements, tech tags) are all in.</summary>
    public static event Action? StatsReady;

    public static GameStats? StatsOf(string id) => Stats.GetValueOrDefault(id);

    static List<RecentUnlock> _unlocks = [];

    /// <summary>The achievements unlocked last, over every game in the library, newest first (filled with the stats).</summary>
    public static IReadOnlyList<RecentUnlock> Unlocks => _unlocks;

    /// <summary>Raised on the UI thread when <see cref="Unlocks"/> was read again (an unlock while the app runs).</summary>
    public static event Action? UnlocksChanged;

    const int UnlocksKept = 40;

    static int _unlocksWeek;

    /// <summary>How many achievements were unlocked over the last seven days, every game counted (not only those kept).</summary>
    public static int UnlocksThisWeek => _unlocksWeek;

    static int CountWeek(IEnumerable<RecentUnlock> all) => all.Count(u => (DateTime.Now - u.At).TotalDays < 7);

    /// <summary>A game's unlocks that carry a time, newest first.</summary>
    static IEnumerable<RecentUnlock> UnlocksOf(Game game, AchievementSet set) => set.Items
        .Where(a => a.Unlocked && a.UnlockedAt is not null)
        .Select(a => new RecentUnlock(game, a, a.UnlockedAt!.Value.ToLocalTime()))
        .OrderByDescending(u => u.At);

    static int _unlockRun;

    /// <summary>Reads every game's unlocks again, off the UI thread (local files only).</summary>
    static void RefreshUnlocks(Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        int run = ++_unlockRun;
        var games = AppState.VisibleGames.ToList();
        _ = Task.Run(async () =>
        {
            // A game writes its stats file several times around an unlock: one read once it has settled, not one per write.
            await Task.Delay(3000);
            if (run != Volatile.Read(ref _unlockRun) || App.ShuttingDown) return;
            var all = new List<RecentUnlock>();
            var counts = new Dictionary<string, (int Unlocked, int Total)>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                try
                {
                    if (SteamAchievements.ReadFor(g) is not { Total: > 0 } set) continue;
                    all.AddRange(UnlocksOf(g, set));
                    counts[g.Id] = (set.UnlockedCount, set.Total);
                }
                catch (Exception ex) { Log.Warn($"Library unlocks for {g.Name}: {ex.Message}"); }
            }
            var newest = all.OrderByDescending(u => u.At).Take(UnlocksKept).ToList();
            int week = CountWeek(all);
            ui.TryEnqueue(() =>
            {
                if (run != _unlockRun) return;
                _unlocks = newest;
                _unlocksWeek = week;
                foreach (var (id, c) in counts)
                    if (Stats.TryGetValue(id, out var old)) Stats[id] = old with { AchUnlocked = c.Unlocked, AchTotal = c.Total };
                UnlocksChanged?.Invoke();
            });
        });
    }

    static bool _watching;

    /// <summary>
    /// Keeps one game's play time and last played current while the app runs: read again when the game starts, when it closes
    /// and when its time is counted. The stats were read once per session, so the Library's Last played section and its "last
    /// played" order stayed as they were at start (1.4.0-rc.20).
    /// </summary>
    public static void WatchSessions(Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        if (_watching) return;
        _watching = true;
        GameSessions.PlaytimeChanged += id => RefreshPlay(id, ui);
        GameSessions.Changed += id => { if (GameSessions.State(id) is GameRunState.Running or GameRunState.Stopped) RefreshPlay(id, ui); };
        // An unlock while the app runs (raised on the UI thread): the Latest achievements panel follows.
        AchievementWatcher.Changed += _ => RefreshUnlocks(ui);
    }

    static void RefreshPlay(string id, Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        if (AppState.FindAny(id) is not { } game) return;
        var settings = AppState.Settings.Games.GetValueOrDefault(id) ?? new GameSettings();
        _ = Task.Run(() =>
        {
            try
            {
                var play = Playtime.Get(game, settings);
                ui.TryEnqueue(() =>
                {
                    var old = Stats.GetValueOrDefault(id);
                    if (old is not null && old.Minutes == play.Minutes && old.LastPlayed == play.LastPlayed) return;
                    Stats[id] = old is null ? new GameStats(play.Minutes, play.LastPlayed, 0, 0, null) : old with { Minutes = play.Minutes, LastPlayed = play.LastPlayed };
                    StatsReady?.Invoke();
                });
            }
            catch (Exception ex) { Log.Warn($"Library stats for {game.Name}: {ex.Message}"); }
        });
    }

    /// <summary>Upscaler / frame generation tags found for a game (the grid tiles fill this as they load).</summary>
    public static void SetTags(string id, List<string> tags)
    {
        if (Stats.TryGetValue(id, out var s)) Stats[id] = s with { Tags = tags };
        else Stats[id] = new GameStats(0, null, 0, 0, tags);
    }

    /// <summary>Reads play time, achievements and tags for every game, off the UI thread; once per session (and after a rescan).</summary>
    public static Task LoadStatsAsync(Microsoft.UI.Dispatching.DispatcherQueue ui, bool force = false)
    {
        if (_loading is { IsCompleted: false } || (!force && _loading is not null)) return _loading;
        var games = AppState.VisibleGames.ToList();
        var settings = games.ToDictionary(g => g.Id, g => AppState.Settings.Games.GetValueOrDefault(g.Id) ?? new GameSettings(), StringComparer.OrdinalIgnoreCase);
        _loading = Task.Run(async () =>
        {
            var results = new Dictionary<string, GameStats>(StringComparer.OrdinalIgnoreCase);
            var unlocks = new List<RecentUnlock>();
            foreach (var g in games)
            {
                try
                {
                    var play = Playtime.Get(g, settings[g.Id]);
                    int unlocked = 0, total = 0;
                    if (SteamAchievements.ReadFor(g) is { Total: > 0 } set)
                    {
                        unlocked = set.UnlockedCount;
                        total = set.Total;
                        unlocks.AddRange(UnlocksOf(g, set));
                    }
                    List<string>? tags = null;
                    try
                    {
                        var appIdForWiki = await GameInfo.SteamAppIdAsync(g, CancellationToken.None);
                        tags = await Pcgw.GetUpscalerTagsAsync(g, appIdForWiki);
                    }
                    catch (Exception ex) { Log.Warn($"Library tags for {g.Name}: {ex.Message}"); }
                    results[g.Id] = new GameStats(play.Minutes, play.LastPlayed, unlocked, total, tags);
                }
                catch (Exception ex) { Log.Warn($"Library stats for {g.Name}: {ex.Message}"); }
            }
            var newest = unlocks.OrderByDescending(u => u.At).Take(UnlocksKept).ToList();
            int week = CountWeek(unlocks);
            ui.TryEnqueue(() =>
            {
                foreach (var (id, s) in results) Stats[id] = s;
                _unlocks = newest;
                _unlocksWeek = week;
                _unlockRun++;
                StatsReady?.Invoke();
            });
        });
        return _loading;
    }

    /// <summary>How many kinds of filter are on (the badge on the Filters buttons).</summary>
    public static int ActiveFilters()
    {
        var f = AppState.Settings.Library;
        return (f.OptiOnly ? 1 : 0) + (f.Launchers.Count > 0 ? 1 : 0) + (f.Tech.Count > 0 ? 1 : 0) + (f.ShowNotInstalled ? 1 : 0);
    }

    /// <summary>Filters and sorts with the library settings. Games whose stats are not in yet pass the filters that need them.</summary>
    public static List<Game> Apply(IEnumerable<Game> games)
    {
        var f = AppState.Settings.Library;
        IEnumerable<Game> list = games;
        if (f.OptiOnly) list = list.Where(g => OptiVersion(g) is not null);
        if (f.Launchers.Count > 0) list = list.Where(g => f.Launchers.Contains(StoreLogos.GroupOf(g).Key));
        if (f.Tech.Count > 0)
            list = list.Where(g => StatsOf(g.Id)?.Tags is not { } tags || f.Tech.Any(t => HasTech(tags, t)));

        Func<Game, IComparable> key = f.Sort switch
        {
            "recent" => g => StatsOf(g.Id)?.LastPlayed ?? DateTime.MinValue,
            "added" => g => AppState.Settings.Games.GetValueOrDefault(g.Id)?.AddedAt ?? DateTime.MinValue,
            "playtime" => g => StatsOf(g.Id)?.Minutes ?? 0,
            "achievements" => g => StatsOf(g.Id) is { AchTotal: > 0 } s ? s.AchUnlocked * 1000.0 + (double)s.AchUnlocked / s.AchTotal : -1.0,
            "tech" => g => StatsOf(g.Id)?.Tags?.Count ?? 0,
            _ => g => g.Title.ToLowerInvariant(),
        };
        // Name sorts A→Z by default; the numbers and dates biggest / newest first. "Reverse" flips either.
        bool descending = f.Sort != "name";
        if (f.Descending) descending = !descending;
        var ordered = descending ? list.OrderByDescending(key) : list.OrderBy(key);
        return ordered.ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The OptiScaler version in the game's folder now, or null: Hybrid Launcher's own install while its dll is still there, or one put
    /// there by hand or by another tool (found by its version resource). A record left over after the dll was deleted is not one.
    /// </summary>
    public static string? OptiVersion(Game game)
    {
        if (game.NotInstalled) return null;
        var settings = AppState.Settings.Games.GetValueOrDefault(game.Id);
        try
        {
            // The record's version, unless the dll was replaced since (a nightly or a test build dropped over it): then the dll's own.
            if (settings?.Install is { } m && File.Exists(Path.Combine(m.TargetDir, m.DllName)) && !OptiDetector.ReplacedSince(m)) return m.DisplayVersion;
            return GameScanner.TargetDir(game, settings ?? new GameSettings()) is { } dir && OptiDetector.Detect(dir) is { } found
                ? CustomBuilds.Readable(found.Version) : null;
        }
        catch { return null; }
    }

    public static bool HasTech(List<string> tags, string tech) => tech switch
    {
        "FG" => tags.Any(t => t.Contains("FG", StringComparison.OrdinalIgnoreCase) || t.Contains("MFG", StringComparison.OrdinalIgnoreCase)),
        _ => tags.Any(t => t.StartsWith(tech, StringComparison.OrdinalIgnoreCase)),
    };

    /// <summary>The games grouped by launcher / games folder, groups in the user's order (new groups in their default place).</summary>
    public static List<(string Key, List<Game> Games)> Groups(IEnumerable<Game> games)
    {
        var groups = games.GroupBy(g => StoreLogos.GroupOf(g).Key)
            .OrderBy(g => StoreLogos.GroupOf(g.First()).Order).ThenBy(g => StoreLogos.GroupOf(g.First()).Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (g.Key, g.ToList())).ToList();
        var order = AppState.Settings.GroupOrder;
        return groups.Select((g, i) => (g, i))
            .OrderBy(x => order.IndexOf(x.g.Key) is var o and >= 0 ? o : order.Count + x.i)
            .Select(x => x.g).ToList();
    }

    /// <summary>Saves a new order of the groups shown (groups not shown keep their place after them).</summary>
    public static void SaveGroupOrder(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        foreach (var k in AppState.Settings.GroupOrder) if (!list.Contains(k, StringComparer.OrdinalIgnoreCase)) list.Add(k);
        AppState.Settings.GroupOrder = list;
        SettingsStore.Save();
    }

    public static bool IsCollapsed(string key) => AppState.Settings.CollapsedGroups.Contains(key);

    public static void SetCollapsed(string key, bool collapsed)
    {
        if (collapsed) AppState.Settings.CollapsedGroups.Add(key);
        else AppState.Settings.CollapsedGroups.Remove(key);
        SettingsStore.Save();
    }

    /// <summary>"12 h", "1,234 h", "45 min": short enough for a cover chip.</summary>
    public static string ShortTime(long minutes) => minutes < 60 ? $"{minutes} min" : $"{minutes / 60.0:#,0.#} h".Replace(".0 h", " h");
}
