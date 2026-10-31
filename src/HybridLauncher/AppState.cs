using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Helpers;

namespace HybridLauncher;

/// <summary>The detected games, shared by the pages. Everything here is used from the UI thread.</summary>
public static class AppState
{
    public static AppSettings Settings => SettingsStore.Current;
    public static List<Game> Games { get; private set; } = [];
    public static event Action? GamesChanged;

    /// <summary>Goes up after every scan (see <see cref="SettingsStore.Version"/>).</summary>
    public static int GamesVersion { get; private set; }

    static Task? _scan;
    static bool _again;

    /// <summary>Rescans; a request made while a scan runs triggers one more scan right after, so new settings are never missed.</summary>
    public static Task RescanAsync()
    {
        if (_scan is { IsCompleted: false })
        {
            _again = true;
            return _scan;
        }
        _scan = ScanCore();
        return _scan;
    }

    static async Task ScanCore()
    {
        do
        {
            _again = false;
            try
            {
                Games = await GameScanner.ScanAsync(Settings);
                // The library's "Date added": when Hybrid Launcher first saw the game.
                bool added = false;
                foreach (var g in Games)
                {
                    var s = Settings.For(g.Id);
                    if (s.AddedAt is not null) continue;
                    s.AddedAt = DateTime.Now;
                    added = true;
                }
                if (added) SettingsStore.Save();
            }
            catch (Exception ex)
            {
                Log.Error("Scan failed", ex);
            }
        } while (_again);
        GamesVersion++;
        GamesChanged?.Invoke();
    }

    /// <summary>Completes when the scan in progress (or the last one) is done.</summary>
    public static Task Scanned => _scan ?? Task.CompletedTask;

    /// <summary>Starts a game the way Play does, and watches it from then on (Stop, time played, Smart HDR).</summary>
    /// <summary>
    /// The cloud copy of the saves comes down first when it is newer (see <see cref="CloudSaves"/>), then the game starts. Awaited
    /// from the click: nothing here blocks the window (Epic's launch code and the cloud are both asked asynchronously).
    /// </summary>
    public static async Task LaunchAsync(Game game)
    {
        var settings = Settings.For(game.Id);
        if (GameSessions.IsActive(game.Id) || !Launching.Add(game.Id)) return;
        try
        {
            await CloudSync.BeforeLaunchAsync(game);
            await GameLauncher.LaunchAsync(game, settings);
            if (GameScanner.EffectiveExe(game, settings) is { } exe) GameSessions.Launched(game, settings, Path.GetDirectoryName(exe)!);
        }
        finally { Launching.Remove(game.Id); }
    }

    // A second click on Play while the first one is still getting ready (cloud, Epic) does not start the game twice.
    static readonly HashSet<string> Launching = new(StringComparer.OrdinalIgnoreCase);

    // Pages of games you own but have not installed (opened from Downloads): made on demand, kept for the session.
    static readonly Dictionary<string, Game> Owned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A page-only game for an owned, not installed one (Download instead of Play).</summary>
    public static void AddOwned(Game game) => Owned[game.Id] = game;

    /// <summary>An installed (or installing) game, else an owned one opened from Downloads.</summary>
    public static Game? FindAny(string? id) => Find(id) ?? (id is not null && Owned.TryGetValue(id, out var g) ? g : null);

    public static Game? Find(string? id) => id is null ? null : Games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<Game> VisibleGames => Games.Where(g => !Settings.HiddenGames.Contains(g.Id));

    /// <summary>Raised after a game is pinned or unpinned (the list, the grid and the game page follow it).</summary>
    public static event Action? PinsChanged;

    public static bool IsPinned(string id) => Settings.PinnedGames.Contains(id, StringComparer.OrdinalIgnoreCase);

    public static void SetPinned(string id, bool pinned)
    {
        if (pinned == IsPinned(id)) return;
        if (pinned) Settings.PinnedGames.Add(id);
        else Settings.PinnedGames.RemoveAll(p => p.Equals(id, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save();
        PinsChanged?.Invoke();
    }

    /// <summary>Visible pinned games, in pin order.</summary>
    public static List<Game> PinnedGames() =>
        Settings.PinnedGames.Select(Find).Where(g => g is not null && !Settings.HiddenGames.Contains(g.Id)).Select(g => g!).ToList();

    /// <summary>Universal ASI plugins currently in the folder.</summary>
    public static List<string> AsiPluginFiles()
    {
        try
        {
            return Directory.GetFiles(AppInfo.AsiPluginsDir, "*.asi").Select(Path.GetFileName).OrderBy(n => n).ToList()!;
        }
        catch { return []; }
    }

    /// <summary>The plugins turned on for one game (they may no longer be in the folder).</summary>
    public static HashSet<string> AsiOn(GameSettings game) => game.AsiPlugins ??= new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugins in the folder that are turned on for the game.</summary>
    public static List<string> EnabledAsiPlugins(GameSettings game)
    {
        var on = AsiOn(game);
        return AsiPluginFiles().Where(on.Contains).ToList();
    }
}
