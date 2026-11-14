using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
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

    static List<RecentUnlock> _unlocks = [], _ownedUnlocks = [];
    static List<RecentUnlock>? _merged;

    /// <summary>
    /// The achievements unlocked last, over every game in the library (and, once read, the games owned on a signed-in
    /// launcher but not installed), newest first (filled with the stats).
    /// </summary>
    public static IReadOnlyList<RecentUnlock> Unlocks => _merged ??= _ownedUnlocks.Count == 0 ? _unlocks
        : _unlocks.Concat(_ownedUnlocks).OrderByDescending(u => u.At).Take(UnlocksKept).ToList();

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
                _merged = null;
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
            // Epic keeps play time and achievements on its servers only: the account's hours first, so Playtime.Get counts them.
            await RefreshEpicAsync();
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
            // Installed Epic games: their achievements from the account (the copy kept is used when younger than six hours).
            await ReadOnlineAsync(games.Where(g => g.Store == GameStore.Epic && EpicStats.CanRead).ToList(), results, unlocks);
            var newest = unlocks.OrderByDescending(u => u.At).Take(UnlocksKept).ToList();
            int week = CountWeek(unlocks);
            ui.TryEnqueue(() =>
            {
                foreach (var (id, s) in results) Stats[id] = s;
                _unlocks = newest;
                _merged = null;
                _unlocksWeek = week;
                _unlockRun++;
                StatsReady?.Invoke();
            });
        });
        return _loading;
    }

    /// <summary>
    /// Epic's achievements were turned on or Epic's check was answered: the library's and the owned games' stats are read
    /// again now, so the counts show without a restart. Waits for a read still running first (that one started without them).
    /// </summary>
    public static void RefreshAfterEpicChange(Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (_loading is { IsCompleted: false } running) await running;
                if (_ownedLoading is { IsCompleted: false } owned) await owned;
            }
            catch { }
            ui.TryEnqueue(() =>
            {
                if (App.ShuttingDown) return;
                _ = LoadStatsAsync(ui, force: true);
                _ownedKey = null;
                _ = LoadOwnedStatsAsync(ui);
            });
        });
    }

    static Task? _ownedLoading;
    // What the owned games' stats were read for: the list, the sources turned on and the accounts signed in (2.1 audit: the
    // count alone missed a source turned on or another account with as many games).
    static string? _ownedKey;

    /// <summary>Raised on the UI thread when the stats of the owned, not installed games are in (the profile dashboard).</summary>
    public static event Action? OwnedStatsReady;

    /// <summary>Whether the stats of the owned games were read for the list loaded now.</summary>
    public static bool OwnedStatsLoaded => _ownedLoading is { IsCompleted: true };

    /// <summary>
    /// Play time and achievements of the games owned on the linked launchers but not installed (2.1-rc.3, the profile
    /// dashboard): Steam's own count of hours (localconfig.vdf, also for games uninstalled long ago) and Hybrid Launcher's
    /// record; Steam's achievements from its cache on this PC, else (games played only) the account's public achievements
    /// page; GOG's from its service when signed in. Off the UI thread, a few games at a time; read again only when the list
    /// of owned games changes.
    /// </summary>
    public static Task LoadOwnedStatsAsync(Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        var owned = Views.OwnedLibrary.NotInstalled();
        bool steamAch = SettingsStore.Current.ReadSteamAchievements;
        // GOG and Epic are read only with their source on and the account signed in: a game that cannot be asked must not take
        // one of the sixty places below (2.1-rc.3 audit).
        bool gogAch = SettingsStore.Current.ReadGogAchievements && Core.Accounts.GogAccount.SignedIn;
        bool epicAch = EpicStats.CanRead;
        var key = $"{owned.Count}|{string.Join(",", owned.Select(o => o.Id).Order(StringComparer.OrdinalIgnoreCase)).GetHashCode()}|{steamAch}|{gogAch}|{epicAch}";
        if (_ownedLoading is not null && (_ownedLoading.IsCompleted is false || key == _ownedKey)) return _ownedLoading;
        _ownedKey = key;
        var games = owned.Select(Views.OwnedLibrary.AsGame).ToList();
        var settings = games.ToDictionary(g => g.Id, g => AppState.Settings.Games.GetValueOrDefault(g.Id) ?? new GameSettings(), StringComparer.OrdinalIgnoreCase);
        _ownedLoading = Task.Run(async () =>
        {
            var results = new Dictionary<string, GameStats>(StringComparer.OrdinalIgnoreCase);
            var unlocks = new List<RecentUnlock>();
            var online = new List<Game>();
            await RefreshEpicAsync();
            foreach (var g in games)
            {
                try
                {
                    var play = Playtime.Get(g, settings[g.Id]);
                    int got = 0, total = 0;
                    if (g is { Store: GameStore.Steam, SteamAppId: { } app } && steamAch && SteamAchievements.Read(app) is { Total: > 0 } set)
                    {
                        got = set.UnlockedCount;
                        total = set.Total;
                        unlocks.AddRange(UnlocksOf(g, set));
                    }
                    else if (play.Minutes > 0 && (g.Store == GameStore.Steam && steamAch || g.Store == GameStore.Gog && gogAch || g.Store == GameStore.Epic && epicAch))
                        online.Add(g);
                    if (play.Minutes > 0 || total > 0 || play.LastPlayed is not null)
                        results[g.Id] = new GameStats(play.Minutes, play.LastPlayed, got, total, null);
                }
                catch (Exception ex) { Log.Warn($"Owned stats for {g.Name}: {ex.Message}"); }
            }
            // The games played but with nothing on this PC: their achievements from the store, most played first, four at a time.
            var gate = new SemaphoreSlim(4);
            var work = online.OrderByDescending(g => results.GetValueOrDefault(g.Id)?.Minutes ?? 0).Take(60).Select(async g =>
            {
                await gate.WaitAsync();
                try
                {
                    AchievementSet? set = null;
                    if (g is { Store: GameStore.Steam, SteamAppId: { } app }) set = await SteamAchievements.CommunityAsync(app, CancellationToken.None);
                    else if (Core.Accounts.GogAchievements.ProductId(g) is { } gog) set = await Core.Accounts.GogAchievements.ReadAsync(gog, CancellationToken.None);
                    else if (g.Store == GameStore.Epic) set = await EpicStats.ReadAsync(g, CancellationToken.None, TimeSpan.FromHours(6));
                    if (set is not { Total: > 0 }) return;
                    lock (results)
                    {
                        var old = results.GetValueOrDefault(g.Id);
                        results[g.Id] = old is null ? new GameStats(0, null, set.UnlockedCount, set.Total, null) : old with { AchUnlocked = set.UnlockedCount, AchTotal = set.Total };
                        unlocks.AddRange(UnlocksOf(g, set));
                    }
                }
                catch (Exception ex) { Log.Warn($"Owned achievements for {g.Name}: {ex.Message}"); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(work);
            var newest = unlocks.OrderByDescending(u => u.At).Take(UnlocksKept).ToList();
            int week = CountWeek(unlocks);
            Log.Info($"Owned games' stats: {results.Count} of {games.Count} with play time or achievements ({online.Count} read online)");
            ui.TryEnqueue(() =>
            {
                foreach (var (id, st) in results)
                    if (AppState.Find(id) is not { NotInstalled: false }) Stats[id] = st;
                _ownedUnlocks = newest;
                _merged = null;
                _ownedWeek = week;
                OwnedStatsReady?.Invoke();
            });
        });
        return _ownedLoading;
    }

    /// <summary>The Epic account's play time, asked again when the copy kept is older than ten minutes (never throws).</summary>
    static async Task RefreshEpicAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await EpicStats.RefreshPlaytimeAsync(cts.Token);
        }
        catch (Exception ex) { Log.Warn($"Epic play time: {ex.Message}"); }
    }

    /// <summary>Achievements kept online only (Epic), four games at a time; what comes in goes into the stats and the unlocks.</summary>
    static async Task ReadOnlineAsync(List<Game> games, Dictionary<string, GameStats> results, List<RecentUnlock> unlocks)
    {
        if (games.Count == 0) return;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(games.Select(async g =>
        {
            await gate.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (await EpicStats.ReadAsync(g, cts.Token, TimeSpan.FromHours(6)) is not { Total: > 0 } set) return;
                lock (results)
                {
                    var old = results.GetValueOrDefault(g.Id);
                    results[g.Id] = old is null ? new GameStats(0, null, set.UnlockedCount, set.Total, null) : old with { AchUnlocked = set.UnlockedCount, AchTotal = set.Total };
                    // The copy kept may already have given this game's unlocks (ReadFor): the fresh ones replace them.
                    unlocks.RemoveAll(u => u.Game.Id == g.Id);
                    unlocks.AddRange(UnlocksOf(g, set));
                }
            }
            catch (Exception ex) { Log.Warn($"Epic achievements for {g.Name}: {ex.Message}"); }
            finally { gate.Release(); }
        }));
    }

    static int _ownedWeek;

    /// <summary>Unlocks over the last seven days of the owned games that are not installed (the dashboard adds them).</summary>
    public static int OwnedUnlocksThisWeek => _ownedWeek;

    /// <summary>
    /// The order the library shows the installed games in now, in one string: new stats that leave it as it is (a session's
    /// minutes counted, an unlock) redraw nothing (2.1-rc.3: every count of play time rebuilt the list and every cover).
    /// </summary>
    public static string OrderKey(LibraryFilter? filter = null) => string.Join("|", Apply(AppState.VisibleGames.Where(g => !g.NotInstalled), filter).Select(g => g.Id));

    /// <summary>The side list's order and filters (its own since 2.1-rc.4; a copy of the library's the first time).</summary>
    public static LibraryFilter ListFilter => AppState.Settings.ListFilter ??= AppState.Settings.Library.CopyFilters();

    /// <summary>How many kinds of filter are on (the badge on the Filters buttons), of the library or of <paramref name="filter"/>.</summary>
    public static int ActiveFilters(LibraryFilter? filter = null)
    {
        var f = filter ?? AppState.Settings.Library;
        return (f.OptiOnly ? 1 : 0) + (f.Launchers.Count > 0 ? 1 : 0) + (f.Tech.Count > 0 ? 1 : 0) + (f.ShowNotInstalled ? 1 : 0) + (f.Collection is not null ? 1 : 0);
    }

    /// <summary>Filters and sorts with the library settings. Games whose stats are not in yet pass the filters that need them.</summary>
    public static List<Game> Apply(IEnumerable<Game> games, LibraryFilter? filter = null)
    {
        var f = filter ?? AppState.Settings.Library;
        IEnumerable<Game> list = games;
        if (f.Collection is { } collection) list = list.Where(g => GameCollections.Contains(collection, g.Id));
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
