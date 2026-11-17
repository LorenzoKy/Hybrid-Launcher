using Microsoft.UI.Dispatching;
using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Views;

namespace HybridLauncher.Helpers;

/// <summary>A game update found and not started (automatic updates off, or the game was running): Update starts it.</summary>
public sealed record PendingUpdate(string Id, string Name, string? From, string Latest, Func<DownloadJob> Start);

/// <summary>
/// Steam-style automatic updates: a check a minute after Hybrid Launcher starts and then every six hours; each game with a newer
/// build updates in Downloads (only the files that changed), unless it is running or already downloading. The games Hybrid
/// Launcher downloaded, and since 2.1.4 the ones Steam, the Epic Games Launcher or GOG Galaxy installed (signed-in stores only,
/// <see cref="LauncherUpdates"/>). Since 2.1.4 also when Downloads opens (if the last check is a few minutes old) and when a
/// game's page opens (that game only), so an update shows at once. With automatic updates off (Settings → Features) an update
/// found is only listed (<see cref="Available"/>): Downloads and the game's page offer Update.
/// </summary>
public static class GameUpdater
{
    static readonly TimeSpan Every = TimeSpan.FromHours(6), PageAge = TimeSpan.FromMinutes(3), GameAge = TimeSpan.FromMinutes(10);
    static DispatcherQueueTimer? _timer;
    static DispatcherQueue? _ui;
    static bool _checking;
    static DateTime _lastFull = DateTime.MinValue;
    static readonly Dictionary<string, DateTime> LastGame = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, PendingUpdate> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list of updates found and not started changed (raised on the UI thread).</summary>
    public static event Action? Changed;

    /// <summary>Updates found and not started, by game id.</summary>
    public static IReadOnlyList<PendingUpdate> Available { get { lock (Pending) return Pending.Values.OrderBy(p => p.Name).ToList(); } }

    public static PendingUpdate? AvailableFor(string id) { lock (Pending) return Pending.GetValueOrDefault(id); }

    public static void Start(DispatcherQueue ui)
    {
        _ui = ui;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += async (t, _) =>
        {
            t.Interval = Every;
            await CheckNowAsync();
        };
        _timer.Start();
    }

    static bool AnyStore(AppSettings s) => s.DirectInstalls.Count > 0 || OwnedGames.Direct(GameStore.Steam) || OwnedGames.Direct(GameStore.Epic) || OwnedGames.Direct(GameStore.Gog);

    /// <summary>A check of every game is running (Downloads shows it).</summary>
    public static bool Checking => _checking;

    /// <summary>When every game was last checked (UTC), or null before the first check.</summary>
    public static DateTime? LastChecked => _lastFull == DateTime.MinValue ? null : _lastFull;

    /// <summary>Something can be checked: a signed-in account, or a game Hybrid Launcher installed itself.</summary>
    public static bool CanCheck => AnyStore(AppState.Settings);

    /// <summary>Checks now; with automatic updates on, starts the updates found. The number started.</summary>
    /// <param name="start">Whether to start what is found; by default with automatic updates on or <paramref name="evenIfOff"/>.</param>
    public static async Task<int> CheckNowAsync(bool evenIfOff = false, bool? start = null)
    {
        var settings = AppState.Settings;
        if (_checking || !evenIfOff && !settings.AutoUpdateGames || !AnyStore(settings)) return 0;
        _checking = true;
        RaiseChanged();
        try
        {
            await OwnedLibrary.LoadAsync();
            var direct = await Task.Run(() => GameUpdates.CheckAsync(OwnedLibrary.Find, CancellationToken.None));
            var launcher = await Task.Run(() => LauncherUpdates.CheckAsync(OwnedLibrary.Find, CancellationToken.None));
            _lastFull = DateTime.UtcNow;
            _checking = false;
            // A manual "Check now" in Settings starts what it finds, like the timer with automatic updates on.
            return Take(direct, launcher, start: start ?? (settings.AutoUpdateGames || evenIfOff), replace: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check: {ex.Message}");
            return 0;
        }
        finally
        {
            if (_checking) { _checking = false; RaiseChanged(); }
        }
    }

    /// <summary>Downloads opened: a check when the last one is more than a few minutes old (updates start if automatic updates are on).</summary>
    public static async Task CheckIfStaleAsync()
    {
        if (_checking || DateTime.UtcNow - _lastFull < PageAge || !AnyStore(AppState.Settings)) return;
        var settings = AppState.Settings;
        _checking = true;
        RaiseChanged();
        try
        {
            await OwnedLibrary.LoadAsync();
            var direct = await Task.Run(() => GameUpdates.CheckAsync(OwnedLibrary.Find, CancellationToken.None));
            var launcher = await Task.Run(() => LauncherUpdates.CheckAsync(OwnedLibrary.Find, CancellationToken.None));
            _lastFull = DateTime.UtcNow;
            _checking = false;
            Take(direct, launcher, start: settings.AutoUpdateGames, replace: true);
        }
        catch (Exception ex) { Log.Warn($"Update check: {ex.Message}"); }
        finally
        {
            if (_checking) { _checking = false; RaiseChanged(); }
        }
    }

    /// <summary>A game's page opened: that game alone is checked (at most every few minutes per game).</summary>
    public static async Task CheckGameAsync(string id)
    {
        if (!id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) && !id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase) && !id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase)) return;
        var settings = AppState.Settings;
        if (!AnyStore(settings)) return;
        lock (LastGame)
        {
            if (LastGame.TryGetValue(id, out var at) && DateTime.UtcNow - at < GameAge) return;
            LastGame[id] = DateTime.UtcNow;
        }
        try
        {
            await OwnedLibrary.LoadAsync();
            var (direct, launcher) = await Task.Run(async () =>
            {
                var d = await GameUpdates.CheckAsync(OwnedLibrary.Find, CancellationToken.None, id);
                var installs = d.Count > 0 ? [] : LauncherUpdates.Scan().Where(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
                var l = installs.Count == 0 ? [] : await LauncherUpdates.CheckAsync(OwnedLibrary.Find, CancellationToken.None, installs);
                return (d, l);
            });
            Take(direct, launcher, start: settings.AutoUpdateGames, replace: false, only: id);
        }
        catch (Exception ex) { Log.Warn($"Update check of {id}: {ex.Message}"); }
    }

    /// <summary>Starts the update found for a game (Update in Downloads or on its page).</summary>
    public static DownloadJob? StartAvailable(string id)
    {
        PendingUpdate? p;
        lock (Pending) { if (!Pending.Remove(id, out p)) return null; }
        RaiseChanged();
        return p.Start();
    }

    /// <summary>Starts every update found (Update all in Downloads).</summary>
    public static int StartAll()
    {
        int n = 0;
        // A game that is running keeps its update waiting: its files are in use (as the single Update buttons do).
        foreach (var p in Available) if (!GameSessions.IsActive(p.Id) && StartAvailable(p.Id) is not null) n++;
        return n;
    }

    /// <summary>Self-test only: an update found for a game (no store asked), and its page's own check skipped for a while.</summary>
    internal static void TestInject(PendingUpdate p)
    {
        lock (LastGame) LastGame[p.Id] = DateTime.UtcNow;
        lock (Pending) Pending[p.Id] = p;
        RaiseChanged();
    }

    static int Take(List<GameUpdate> direct, List<LauncherUpdate> launcher, bool start, bool replace, string? only = null)
    {
        var found = direct.Select(u => new PendingUpdate(u.Install.Id, u.Install.Name, u.Install.Version, u.Latest, () => Downloads.StartUpdate(u.Game, u.Install)))
            .Concat(launcher.Select(u => new PendingUpdate(u.Install.Id, u.Install.Name, LauncherUpdates.Installed(u.Install), u.Latest, () => Downloads.StartLauncherUpdate(u.Game, u.Install, u.Latest))))
            .ToList();
        int started = 0;
        lock (Pending)
        {
            if (replace) Pending.Clear();
            else if (only is not null) Pending.Remove(only);
            foreach (var p in found)
            {
                // Already downloading or queued: Downloads shows it.
                if (Downloads.Find(p.Id) is { State: DownloadState.Waiting or DownloadState.Downloading or DownloadState.Paused }) continue;
                if (start && !GameSessions.IsActive(p.Id))
                {
                    p.Start();
                    started++;
                    continue;
                }
                if (start) Log.Info($"Update of {p.Name} waits: the game is running");
                Pending[p.Id] = p;
            }
        }
        RaiseChanged();
        return started;
    }

    static void RaiseChanged()
    {
        if (_ui is null || _ui.HasThreadAccess) Changed?.Invoke();
        else _ui.TryEnqueue(() => Changed?.Invoke());
    }
}
