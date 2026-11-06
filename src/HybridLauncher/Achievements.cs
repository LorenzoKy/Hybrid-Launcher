using System.Runtime.InteropServices;
using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
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
            StartEmulators();
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
        foreach (var w in EmuWatchers) try { w.Dispose(); } catch { }
        EmuWatchers.Clear();
        try { _feed?.Dispose(); } catch { }
        _feed = null;
        _gogTimer?.Stop();
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
            timer.Interval = TimeSpan.FromMilliseconds(1200);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => _ = appId.StartsWith("emu:", StringComparison.Ordinal) ? CheckEmulatorAsync(appId[4..]) : CheckAsync(appId);
            Pending[appId] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    static async Task CheckAsync(string appId)
    {
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
            Changed?.Invoke(appId);
            // First read (the game has just started): the pictures of what is still locked are fetched now, so a pop-up shows its
            // picture at once instead of downloading it while it is on screen.
            if (before is null && schema is not null && AppState.Settings.AchievementPopups)
                AchievementPopup.Prefetch(schema.Where(a => !now.Contains(a.Key)).Select(a => a.IconUrl));

            if (before is null || schema is null || !AppState.Settings.AchievementPopups) return;
            var fresh = schema.Where(a => now.Contains(a.Key) && !before.Contains(a.Key)).ToList();
            if (fresh.Count == 0) return;
            var found = AppState.Find($"steam:{appId}");
            var game = found?.Name;
            Log.Info($"New achievements in {game ?? appId}: {string.Join(", ", fresh.Select(a => a.ApiName))}");
            await AnnounceAsync(fresh, game, GameStore.Steam, appId, PlatinumAchievement.Find(schema)?.Key, found?.Id);
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                percents = await SteamAchievements.GlobalPercentagesAsync(steamAppId, cts.Token);
            }
            catch (Exception ex) { Log.Warn($"Rarity for {steamAppId}: {ex.Message}"); }
        }
        foreach (var a in fresh)
            AchievementPopup.Show(new Toast(a.Name, a.Description, a.IconUrl, game, a.GlobalPercent ?? (percents.TryGetValue(a.ApiName, out var p) ? p : null), store, Completion: a == platinum, GameId: gameId, AchievementKey: a.Key));
    }

    /// <summary>
    /// Compares a set read from any store with what was known (first read: only what was unlocked in the last half minute counts as
    /// new, so a game's older unlocks never pop up) and announces what is new.
    /// </summary>
    /// <param name="firstWindow">On the first read, how far back an unlock still counts as new (at least the time between two reads).</param>
    static async Task DiffAsync(string key, AchievementSet set, Game game, GameStore store, string? steamAppId = null, TimeSpan? firstWindow = null)
    {
        var now = set.Items.Where(a => a.Unlocked).Select(a => a.Key).ToHashSet();
        HashSet<string>? before;
        lock (Known)
        {
            Known.TryGetValue(key, out before);
            Known[key] = now;
        }
        if (!AppState.Settings.AchievementPopups) return;
        if (before is null) AchievementPopup.Prefetch(set.Items.Where(a => !a.Unlocked).Select(a => a.IconUrl));
        List<Achievement> fresh;
        if (before is not null) fresh = set.Items.Where(a => a.Unlocked && !before.Contains(a.Key)).ToList();
        else
        {
            var cutoff = DateTime.Now - (firstWindow ?? TimeSpan.FromSeconds(30));
            fresh = set.Items.Where(a => a.Unlocked && a.UnlockedAt is { } t && t > cutoff).ToList();
        }
        if (fresh.Count == 0) return;
        Log.Info($"New achievements in {game.Name}: {string.Join(", ", fresh.Select(a => a.ApiName))}");
        await AnnounceAsync(fresh, game.Name, store, steamAppId, set.Platinum?.Key, game.Id);
    }

    // ---------- preview with a real game ----------

    /// <summary>Games of the library whose achievements can be read here: Steam (schema in Steam's cache), Steam emulators, GOG when signed in.</summary>
    public static List<Game> PreviewGames() => AppState.Games
        .Where(g => g switch
        {
            { Store: GameStore.Steam, SteamAppId: { } id } => SteamAchievements.HasSchema(id),
            { EmulatorAppId: not null, EmulatorSettingsDir: not null } => SettingsStore.Current.ReadSteamEmulator,
            _ => GogAccount.SignedIn && GogAchievements.ProductId(g) is not null,
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

    // ---------- Steam emulators (Goldberg / GSE) ----------

    static readonly List<FileSystemWatcher> EmuWatchers = [];

    static void StartEmulators()
    {
        try
        {
            foreach (var root in SteamEmulator.SaveRoots().Where(Directory.Exists))
            {
                var w = new FileSystemWatcher(root, "achievements.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                void OnEvent(string path)
                {
                    // <root>\<appid>\achievements.json
                    var id = Path.GetFileName(Path.GetDirectoryName(path));
                    if (id is { Length: > 0 } && id.All(char.IsAsciiDigit)) _ui?.TryEnqueue(() => Debounce("emu:" + id));
                }
                w.Changed += (_, e) => OnEvent(e.FullPath);
                w.Created += (_, e) => OnEvent(e.FullPath);
                w.Renamed += (_, e) => OnEvent(e.FullPath);
                w.EnableRaisingEvents = true;
                EmuWatchers.Add(w);
            }
            if (EmuWatchers.Count > 0) Log.Info("Watching Steam emulator achievements");
        }
        catch (Exception ex) { Log.Warn($"Could not watch emulator achievements: {ex.Message}"); }
    }

    static async Task CheckEmulatorAsync(string appId)
    {
        try
        {
            if (!SettingsStore.Current.ReadSteamEmulator) return;
            var game = AppState.Games.FirstOrDefault(g => g.EmulatorAppId == appId && g.EmulatorSettingsDir is not null);
            if (game is null) return;
            var set = await Task.Run(() => SteamEmulator.ReadAchievements(appId, game.EmulatorSettingsDir!));
            if (set is not null) await DiffAsync("emu:" + appId, set, game, GameStore.Steam, appId);
            Changed?.Invoke(appId);
        }
        catch (Exception ex) { Log.Warn($"Emulator achievement check {appId}: {ex.Message}"); }
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
        _gogTimer.Tick += (_, _) => _ = PollGogAsync();
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
