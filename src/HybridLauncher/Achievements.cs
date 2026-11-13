using System.Runtime.InteropServices;
using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Core.Achievements;
using HybridLauncher.Core.Steam;
using HybridLauncher.Helpers;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace HybridLauncher;

/// <summary>
/// Watches Steam's appcache\stats folder while Hybrid Launcher runs. Steam rewrites UserGameStats_&lt;account&gt;_&lt;appid&gt;.bin when
/// stats change; each rewrite is compared with the last known unlocked set and new unlocks become pop-ups.
/// </summary>
public static class AchievementWatcher
{
    /// <summary>An app id whose stats file changed. Always raised on the UI thread.</summary>
    public static event Action<string>? Changed;

    static FileSystemWatcher? _watcher;
    static DispatcherQueue? _ui;
    static readonly Dictionary<string, HashSet<string>> Known = new();
    static readonly Dictionary<string, DispatcherQueueTimer> Pending = new();

    public static void Start(DispatcherQueue ui)
    {
        try
        {
            _ui = ui;
            StartLocal();
            StartGog();
            StartFeed();
            var dir = SteamAchievements.StatsDir;
            if (dir is null || _watcher is not null) return;

            // What is already unlocked is not news: remember it before listening.
            var account = SteamAchievements.CurrentAccountId();
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, account is null ? "UserGameStats_*.bin" : $"UserGameStats_{account}_*.bin"))
                    {
                        if (SteamAchievements.AppIdFromStatsFile(file) is not { } appId) continue;
                        var keys = SteamAchievements.ReadUnlocked(appId).Keys.ToHashSet();
                        lock (Known) Known.TryAdd(appId, keys);
                    }
                }
                catch (Exception ex) { Log.Warn($"Achievement snapshot: {ex.Message}"); }
            });

            _watcher = new FileSystemWatcher(dir, "UserGameStats_*.bin")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
            _watcher.Created += (_, e) => OnFileEvent(e.FullPath);
            _watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);
            _watcher.Error += (_, e) => Log.Warn($"Steam stats watcher: {e.GetException().Message}");
            _watcher.EnableRaisingEvents = true;
            Log.Info($"Watching Steam achievements in {dir}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not watch Steam achievements: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public static void Stop()
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        StopLocal();
        try { _feed?.Dispose(); } catch { }
        _feed = null;
        _gogTimer?.Stop();
    }

    /// <summary>Raises <see cref="Changed"/> a couple of seconds from now (after a pop-up's entrance).</summary>
    static void ChangedLater(string key)
    {
        if (_ui is null) { Changed?.Invoke(key); return; }
        var t = _ui.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2.5);
        t.IsRepeating = false;
        t.Tick += (_, _) => { t.Stop(); Changed?.Invoke(key); };
        t.Start();
    }

    // Watcher thread: hop to the UI thread at once, everything else happens there.
    static void OnFileEvent(string path)
    {
        if (SteamAchievements.AppIdFromStatsFile(path) is not { } appId) return;
        _ui?.TryEnqueue(() => Debounce(appId));
    }

    // Steam writes the file in a few steps: act once it has been quiet for a moment.
    static void Debounce(string appId)
    {
        if (_ui is null) return;
        if (!Pending.TryGetValue(appId, out var timer))
        {
            timer = _ui.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(700);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => _ = appId.StartsWith("emu:", StringComparison.Ordinal) ? CheckEmulatorAsync(appId[4..]) : CheckAsync(appId);
            Pending[appId] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    static async Task CheckAsync(string appId)
    {
        // Settings → Achievements → Sources → Steam off: no Steam pop-ups.
        if (!SettingsStore.Current.ReadSteamAchievements) return;
        try
        {
            var (unlocked, schema) = await Task.Run(() => (SteamAchievements.ReadUnlocked(appId), SteamAchievements.Schema(appId)));
            var now = unlocked.Keys.ToHashSet();
            HashSet<string>? before;
            lock (Known)
            {
                Known.TryGetValue(appId, out before);
                Known[appId] = now;
            }
            // First read (the game has just started): the pictures of what is still locked, and the rarities, are fetched now, so
            // a pop-up shows at once instead of downloading them while the player waits.
            if (before is null && schema is not null && AppState.Settings.AchievementPopups)
            {
                AchievementPopup.Prefetch(schema.Where(a => !now.Contains(a.Key)).Select(a => a.IconUrl));
                if (AppState.Settings.Popup.ShowRarity)
                    _ = Task.Run(async () => { try { await SteamAchievements.GlobalPercentagesAsync(appId, CancellationToken.None); } catch { } });
            }

            var fresh = before is null || schema is null || !AppState.Settings.AchievementPopups ? [] : schema.Where(a => now.Contains(a.Key) && !before.Contains(a.Key)).ToList();
            if (fresh.Count == 0) { Changed?.Invoke(appId); return; }
            // The pages that show the game's achievements read them again after the pop-up has come in, not while it does: their
            // work on the UI thread (pictures, lists) made the toast's first frames late and its slide halting.
            ChangedLater(appId);
            var found = AppState.Find($"steam:{appId}");
            var game = found?.Name;
            Log.Info($"New achievements in {game ?? appId}: {string.Join(", ", fresh.Select(a => a.ApiName))}");
            await AnnounceAsync(fresh, game, GameStore.Steam, appId, PlatinumAchievement.Find(schema!)?.Key, found?.Id);
        }
        catch (Exception ex)
        {
            Log.Warn($"Achievement check for {appId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns new unlocks into pop-ups. A burst (another account signing in, a restored backup) is summed up rather than shown one
    /// by one. Rarity comes from the source (GOG) or from Steam's public stats (cached): the pop-up never waits more than a moment.
    /// The game's own platinum (<paramref name="platinumKey"/>: its achievement for unlocking all the others) shows as itself in
    /// the platinum tier, always on its own and last, as on a console.
    /// </summary>
    static async Task AnnounceAsync(List<Achievement> fresh, string? game, GameStore store, string? steamAppId, string? platinumKey = null, string? gameId = null)
    {
        var platinum = platinumKey is null ? null : fresh.FirstOrDefault(a => a.Key == platinumKey);
        if (platinum is not null)
        {
            Log.Info($"{game}: platinum {platinum.ApiName}");
            fresh = [.. fresh.Where(a => a != platinum), platinum];
        }
        foreach (var a in fresh)
            AchievementHistory.Add(new UnlockEntry(a.UnlockedAt ?? DateTime.Now, gameId, game ?? "", store.ToString(), a.Key, a.Name, a.Description, a.IconUrl, a.GlobalPercent, a.Points, null));
        if (fresh.Count > 3)
        {
            var rest = fresh.Where(a => a != platinum).ToList();
            AchievementPopup.Show(new Toast(L.F("{0} achievements unlocked", rest.Count), string.Join(", ", rest.Take(4).Select(a => a.Name)) + "…", rest[0].IconUrl, game, null, store, GameId: gameId, AchievementKey: rest[0].Key));
            fresh = platinum is null ? [] : [platinum];
        }
        Dictionary<string, double> percents = [];
        if (AppState.Settings.Popup.ShowRarity && steamAppId is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.2));
                percents = await SteamAchievements.GlobalPercentagesAsync(steamAppId, cts.Token);
            }
            catch (Exception ex) { Log.Warn($"Rarity for {steamAppId}: {ex.Message}"); }
        }
        foreach (var a in fresh)
            AchievementPopup.Show(new Toast(a.Name, a.Description, a.IconUrl, game, a.GlobalPercent ?? (percents.TryGetValue(a.ApiName, out var p) ? p : null), store, a.Points, Completion: a == platinum, GameId: gameId, AchievementKey: a.Key));
    }

    /// <summary>
    /// Compares a set read from any store with what was known (first read: only what was unlocked in the last half minute counts as
    /// new, so a game's older unlocks never pop up) and announces what is new.
    /// </summary>
    /// <param name="firstWindow">On the first read, how far back an unlock still counts as new (at least the time between two reads).</param>
    static Task DiffAsync(string key, AchievementSet set, Game game, GameStore store, string? steamAppId = null, TimeSpan? firstWindow = null) =>
        DiffAsync(key, set, game.Id, game.Name, store, steamAppId, firstWindow);

    // Progress last seen per set and achievement (0..1), for the progress pop-ups.
    static readonly Dictionary<string, Dictionary<string, double>> KnownProgress = new();

    static async Task DiffAsync(string key, AchievementSet set, string? gameId, string gameName, GameStore store, string? steamAppId = null, TimeSpan? firstWindow = null)
    {
        var now = set.Items.Where(a => a.Unlocked).Select(a => a.Key).ToHashSet();
        HashSet<string>? before;
        lock (Known)
        {
            Known.TryGetValue(key, out before);
            Known[key] = now;
        }
        if (!AppState.Settings.AchievementPopups) return;
        if (before is not null) ShowProgress(key, set, gameId, gameName, store);
        else lock (KnownProgress) KnownProgress[key] = Shares(set);
        if (before is null) AchievementPopup.Prefetch(set.Items.Where(a => !a.Unlocked).Select(a => a.IconUrl));
        List<Achievement> fresh;
        if (before is not null) fresh = set.Items.Where(a => a.Unlocked && !before.Contains(a.Key)).ToList();
        else
        {
            var cutoff = DateTime.Now - (firstWindow ?? TimeSpan.FromSeconds(30));
            fresh = set.Items.Where(a => a.Unlocked && a.UnlockedAt is { } t && t > cutoff).ToList();
        }
        if (fresh.Count == 0) return;
        Log.Info($"New achievements in {gameName}: {string.Join(", ", fresh.Select(a => a.ApiName))}");
        await AnnounceAsync(fresh, gameName, store, steamAppId, set.Platinum?.Key, gameId);
    }

    static Dictionary<string, double> Shares(AchievementSet set) =>
        set.Items.Where(a => !a.Unlocked && a.ProgressMax is > 0).ToDictionary(a => a.Key, a => Math.Clamp((a.ProgressNow ?? 0) / a.ProgressMax!.Value, 0, 1));

    /// <summary>
    /// Settings → Achievements → Progress pop-ups: a locked achievement with steps that passed the next mark (every 10, 25 or 50% of
    /// the way) since the last read shows how far along it is. Never for the last step: that one is the unlock.
    /// </summary>
    static void ShowProgress(string key, AchievementSet set, string? gameId, string gameName, GameStore store)
    {
        var shares = Shares(set);
        Dictionary<string, double>? old;
        lock (KnownProgress)
        {
            KnownProgress.TryGetValue(key, out old);
            KnownProgress[key] = shares;
        }
        if (old is null || !AppState.Settings.AchProgressPopupsOn) return;
        double step = Math.Clamp(AppState.Settings.AchProgressStep, 5, 100) / 100.0;
        foreach (var a in set.Items)
        {
            if (!shares.TryGetValue(a.Key, out var share) || share >= 1) continue;
            double was = old.GetValueOrDefault(a.Key);
            if (Math.Floor(share / step + 1e-9) <= Math.Floor(was / step + 1e-9)) continue;
            var max = a.ProgressMax!.Value;
            string progress = max == 100 && a.ProgressNow is { } pc && pc <= 100 ? $"{pc:0}%" : $"{a.ProgressNow:0.##} / {max:0.##}";
            Log.Info($"Progress in {gameName}: {a.ApiName} {progress}");
            AchievementPopup.Show(new Toast(a.Name, a.Description, a.IconGrayUrl ?? a.IconUrl, gameName, a.GlobalPercent, store, a.Points, GameId: gameId, AchievementKey: a.Key, Progress: progress));
        }
    }

    // ---------- preview with a real game ----------

    /// <summary>Games of the library whose achievements can be read here: Steam (schema in Steam's cache), Steam emulators, GOG when signed in.</summary>
    public static List<Game> PreviewGames() => AppState.Games
        .Where(g => g switch
        {
            { Store: GameStore.Steam, SteamAppId: { } id } => SettingsStore.Current.ReadSteamAchievements && SteamAchievements.HasSchema(id),
            { EmulatorAppId: not null } => SettingsStore.Current.ReadSteamEmulator,
            _ when XboxAchievements.Applies(g) => XboxAchievements.CacheFile is not null || XboxAccount.SignedIn,
            _ => SettingsStore.Current.ReadGogAchievements && GogAccount.SignedIn && GogAchievements.ProductId(g) is not null,
        })
        .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    static readonly Random Pick = new();

    /// <summary>
    /// Shows one real achievement of <paramref name="game"/> exactly as an unlock of it would be announced: the same reading
    /// (names, pictures, rarity from the store's public stats), the same store for "Match the game's store", the same sound and
    /// place. One still locked is picked when there is one, like a real new unlock. It only reads: nothing is unlocked or written,
    /// and the watcher's record of what is unlocked is left alone, so the real unlock still pops up later. False when the game has
    /// no achievements that can be read.
    /// </summary>
    public static async Task<bool> PreviewAsync(Game game)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var set = await SteamAchievements.LoadForAsync(game, cts.Token);
        if (set is not { Items.Count: > 0 }) return false;
        var locked = set.Items.Where(a => !a.Unlocked).ToList();
        var pool = locked.Count > 0 ? locked : set.Items.ToList();
        var a = pool[Pick.Next(pool.Count)];
        var store = game.Store is GameStore.Custom && game.EmulatorAppId is not null ? GameStore.Steam : game.Store;
        double? percent = AppState.Settings.Popup.ShowRarity ? a.GlobalPercent : null;
        Log.Info($"Achievement preview: {game.Name}, {a.ApiName}");
        AchievementPopup.ShowReplacing(new Toast(a.Name, a.Description, a.IconUrl, game.Name, percent, store, Completion: a == set.Platinum));
        return true;
    }

    /// <summary>
    /// A progress pop-up of <paramref name="game"/>, as a step towards one of its achievements would show it: a locked achievement
    /// that counts progress (its real count, or the next step of it), else a locked one with a sample count. Nothing changes.
    /// False when the game's achievements cannot be read.
    /// </summary>
    public static async Task<bool> PreviewProgressAsync(Game game)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var set = await SteamAchievements.LoadForAsync(game, cts.Token);
        if (set is not { Items.Count: > 0 }) return false;
        var counted = set.Items.Where(a => !a.Unlocked && a.ProgressMax is > 0).ToList();
        var locked = set.Items.Where(a => !a.Unlocked && a != set.Platinum).ToList();
        var pool = counted.Count > 0 ? counted : locked.Count > 0 ? locked : set.Items.ToList();
        var a = pool[Pick.Next(pool.Count)];
        string progress;
        if (a.ProgressMax is { } max and > 0)
        {
            // The real count, one step on (a preview of the next pop-up this achievement would give).
            double now = Math.Min(max - 1, Math.Max(1, (a.ProgressNow ?? 0) + Math.Max(1, Math.Round(max / 10))));
            progress = max == 100 ? $"{now:0}%" : $"{now:0.##} / {max:0.##}";
        }
        else progress = "3 / 10";
        var store = game.Store is GameStore.Custom && game.EmulatorAppId is not null ? GameStore.Steam : game.Store;
        Log.Info($"Progress preview: {game.Name}, {a.ApiName} {progress}");
        AchievementPopup.ShowReplacing(new Toast(a.Name, a.Description, a.IconGrayUrl ?? a.IconUrl, game.Name,
            AppState.Settings.Popup.ShowRarity ? a.GlobalPercent : null, store, a.Points, Progress: progress));
        return true;
    }

    /// <summary>
    /// The platinum of <paramref name="game"/>: its own achievement for unlocking all the others, shown as its unlock would be
    /// (name, picture, rarity, the platinum tier), in its store's look. A preview: nothing changes. False when the game has no
    /// such achievement or its achievements cannot be read.
    /// </summary>
    public static async Task<bool> PreviewPlatinumAsync(Game game)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var set = await SteamAchievements.LoadForAsync(game, cts.Token);
        if (set?.Platinum is not { } a) return false;
        var store = game.Store is GameStore.Custom && game.EmulatorAppId is not null ? GameStore.Steam : game.Store;
        Log.Info($"Platinum preview: {game.Name}, {a.ApiName}");
        AchievementPopup.ShowReplacing(new Toast(a.Name, a.Description, a.IconUrl, game.Name, AppState.Settings.Popup.ShowRarity ? a.GlobalPercent : null, store, Completion: true));
        return true;
    }

    // ---------- local achievement files ----------

    static readonly List<FileSystemWatcher> EmuWatchers = [];

    /// <summary>Watches every folder local achievement files live in (the known ones and the user's own).</summary>
    static void StartLocal()
    {
        try
        {
            foreach (var root in LocalAchievements.Roots())
            {
                var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                void OnEvent(string path)
                {
                    if (!LocalAchievements.IsStateFile(path)) return;
                    if (LocalAchievements.AppIdOf(path) is { } id) _ui?.TryEnqueue(() => Debounce("emu:" + id));
                }
                w.Changed += (_, e) => OnEvent(e.FullPath);
                w.Created += (_, e) => OnEvent(e.FullPath);
                w.Renamed += (_, e) => OnEvent(e.FullPath);
                w.Error += (_, e) => Log.Warn($"Local achievement watcher: {e.GetException().Message}");
                w.EnableRaisingEvents = true;
                EmuWatchers.Add(w);
            }
            if (EmuWatchers.Count > 0) Log.Info($"Watching local achievements in {EmuWatchers.Count} folders");
        }
        catch (Exception ex) { Log.Warn($"Could not watch local achievements: {ex.Message}"); }
    }

    static void StopLocal()
    {
        foreach (var w in EmuWatchers) try { w.Dispose(); } catch { }
        EmuWatchers.Clear();
    }

    /// <summary>The folders changed (Settings → Achievements → Sources): watched again.</summary>
    public static void RestartLocal()
    {
        StopLocal();
        StartLocal();
    }

    /// <summary>How many folders with local achievement files are watched.</summary>
    public static int LocalFolderCount => EmuWatchers.Count;

    static async Task CheckEmulatorAsync(string appId)
    {
        try
        {
            if (!SettingsStore.Current.ReadSteamEmulator) return;
            var game = AppState.Games.FirstOrDefault(g => g.EmulatorAppId == appId && g.Store != GameStore.Steam);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var set = await LocalAchievements.LoadAsync(appId, game?.EmulatorSettingsDir, cts.Token);
            if (set is not null)
            {
                // A game outside the library still gets its pop-up, under its Steam name.
                var name = game?.Name ?? await SteamAppNameAsync(appId) ?? $"Steam app {appId}";
                await DiffAsync("emu:" + appId, set, game?.Id, name, GameStore.Steam, appId);
            }
            Changed?.Invoke(appId);
        }
        catch (Exception ex) { Log.Warn($"Local achievement check {appId}: {ex.Message}"); }
    }

    static async Task<string?> SteamAppNameAsync(string appId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var json = await HybridLauncher.Core.Net.Http.GetCachedAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic",
                Path.Combine("steam", $"app-name-{appId}.json"), TimeSpan.FromDays(60), ct: cts.Token);
            if (json is null) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(appId, out var app) && app.TryGetProperty("data", out var data) && data.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    // ---------- GOG ----------

    static DispatcherQueueTimer? _gogTimer;
    static bool _gogBusy;

    // GOG has no file to watch: while a GOG game runs, its achievements are asked for every 40 seconds (only then, only when signed in).
    static void StartGog()
    {
        if (_ui is null) return;
        _gogTimer = _ui.CreateTimer();
        _gogTimer.Interval = TimeSpan.FromSeconds(40);
        _gogTimer.Tick += (_, _) => { _ = PollGogAsync(); _ = PollXboxAsync(); PollLocal(); };
        _gogTimer.Start();
    }

    static async Task PollGogAsync()
    {
        if (_gogBusy || !AppState.Settings.AchievementPopups || !GogAccount.SignedIn) return;
        _gogBusy = true;
        try
        {
            foreach (var game in AppState.Games.Where(g => GogAchievements.ProductId(g) is not null && GameSessions.IsActive(g.Id)).ToList())
            {
                var id = GogAchievements.ProductId(game)!;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // The first read can come a whole poll after the game started (and GOG's clock may differ a little from this PC's):
                // an unlock from the last two minutes is still news.
                if (await GogAchievements.ReadAsync(id, cts.Token) is { } set)
                {
                    await DiffAsync("gog:" + id, set, game, GameStore.Gog, null, TimeSpan.FromMinutes(2));
                    Changed?.Invoke("gog:" + id);
                }
            }
        }
        catch (Exception ex) { Log.Warn($"GOG achievement poll: {ex.Message}"); }
        finally { _gogBusy = false; }
    }

    // Running games with local achievements are read again now and then too: a change the watcher missed, or one kept in the
    // registry (no file to watch).
    static void PollLocal()
    {
        if (!AppState.Settings.AchievementPopups || !SettingsStore.Current.ReadSteamEmulator) return;
        foreach (var game in AppState.Games.Where(g => g.EmulatorAppId is not null && g.Store != GameStore.Steam && GameSessions.IsActive(g.Id)).ToList())
            Debounce("emu:" + game.EmulatorAppId);
    }

    // ---------- Xbox ----------

    static bool _xboxBusy;

    // Signed in to Xbox: while an Xbox game runs, its achievements are asked for with the GOG poll (every 40 s).
    static async Task PollXboxAsync()
    {
        if (_xboxBusy || !AppState.Settings.AchievementPopups || !AppState.Settings.XboxLivePopups || !XboxAccount.SignedIn) return;
        _xboxBusy = true;
        try
        {
            foreach (var game in AppState.Games.Where(g => XboxAchievements.Applies(g) && GameSessions.IsActive(g.Id)).ToList())
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (await XboxAchievements.LoadAsync(game, cts.Token) is { } set)
                {
                    var key = XboxAchievements.Key(game);
                    await DiffAsync(key, set, game, GameStore.Xbox, null, TimeSpan.FromMinutes(2));
                    Changed?.Invoke(key);
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Xbox achievement poll: {ex.Message}"); }
        finally { _xboxBusy = false; }
    }

    // ---------- the feed: any tool can announce an unlock ----------

    static FileSystemWatcher? _feed;

    /// <summary>The folder other tools drop a small .json file in to show an unlock (Epic, Xbox or anything else).</summary>
    public static string FeedDir => Path.Combine(AppInfo.DataRoot, "achievement-feed");

    /// <summary>
    /// Epic Games and Xbox publish no achievements that can be read from this PC, so they come in through a feed: a tool (a script,
    /// a plugin, an overlay bridge) writes {"store":"epic","game":"Name","name":"Title","description":"...","icon":"C:\path.png",
    /// "percent":4.2,"points":50,"platinum":false} to a .json file in <see cref="FeedDir"/>; it is shown with the look of that store, then the file
    /// is deleted. Only "name" is required.
    /// </summary>
    static void StartFeed()
    {
        try
        {
            Directory.CreateDirectory(FeedDir);
            // Files left from a time Hybrid Launcher was closed are not news.
            foreach (var old in Directory.EnumerateFiles(FeedDir, "*.json")) try { File.Delete(old); } catch { }
            _feed = new FileSystemWatcher(FeedDir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void OnEvent(string path) => _ui?.TryEnqueue(() => Later.Run(_ui, TimeSpan.FromMilliseconds(350), () => ReadFeedFile(path)));
            _feed.Created += (_, e) => OnEvent(e.FullPath);
            _feed.Renamed += (_, e) => OnEvent(e.FullPath);
            _feed.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Warn($"Could not watch the achievement feed: {ex.Message}"); }
    }

    static void ReadFeedFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var rd = new StreamReader(fs)) text = rd.ReadToEnd();
            try { File.Delete(path); } catch { }
            if (!AppState.Settings.AchievementPopups) return;
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var e = doc.RootElement;
            string? Str(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            if (Str("name") is not { Length: > 0 } name) return;
            GameStore? store = Str("store")?.ToLowerInvariant() switch { "steam" => GameStore.Steam, "epic" => GameStore.Epic, "gog" => GameStore.Gog, "xbox" => GameStore.Xbox, _ => null };
            var icon = Str("icon");
            if (icon is not null && Path.IsPathRooted(icon)) icon = new Uri(icon).AbsoluteUri;
            else if (icon is not null && !Uri.TryCreate(icon, UriKind.Absolute, out _)) icon = null;
            double? percent = e.TryGetProperty("percent", out var pc) && pc.ValueKind == System.Text.Json.JsonValueKind.Number ? pc.GetDouble() : null;
            // A tool may write 50.0 or a huge number: the points are rounded and kept in range, never a reason to lose the unlock.
            int? points = e.TryGetProperty("points", out var pt) && pt.ValueKind == System.Text.Json.JsonValueKind.Number ? (int)Math.Clamp(Math.Round(pt.GetDouble()), 0, 1_000_000) : null;
            // "platinum": true (or the older "completed": true) — this is the game's achievement for unlocking all the others.
            bool Flag(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
            AchievementPopup.Show(new Toast(name, Str("description") ?? "", icon, Str("game"), percent, store, points, Completion: Flag("platinum") || Flag("completed")));
        }
        catch (Exception ex) { Log.Warn($"Achievement feed file {Path.GetFileName(path)}: {ex.Message}"); }
    }
}
