using System.Diagnostics;
using System.Globalization;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Opti;
using HybridLauncher.Core.Steam;

namespace HybridLauncher.Core;

/// <summary>Time played and last session. <see cref="Source"/> says where the numbers come from ("Steam" or "Hybrid Launcher").</summary>
public readonly record struct PlayStats(long Minutes, DateTime? LastPlayed, string Source)
{
    public bool IsEmpty => Minutes <= 0 && LastPlayed is null;
}

/// <summary>
/// Steam games: read from the Steam client's own userdata\&lt;account&gt;\config\localconfig.vdf (no login, no API key).
/// Other games: counted by Hybrid Launcher itself (see <see cref="GameSessions"/>) and kept in its own record
/// (<see cref="PlaytimeLedger"/>), so the hours stay when the game is uninstalled, moved or added again.
/// </summary>
public static class Playtime
{
    static readonly object Gate = new();
    static string? _file;
    static DateTime _stamp;
    static Dictionary<string, (long Minutes, long LastPlayed)> _steam = new();

    public static PlayStats Get(Game game, GameSettings settings)
    {
        var own = Counted(game, settings);
        if (game is { Store: GameStore.Steam, SteamAppId: { } appId } && SteamApps().TryGetValue(appId, out var s))
        {
            var stats = new PlayStats(s.Minutes, s.LastPlayed > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.LastPlayed).LocalDateTime : null, "Steam");
            // Hybrid Launcher's record when it knows more (hours from another store's copy of the game, or Steam's count lost).
            return own.Minutes > stats.Minutes ? own : stats;
        }
        return own;
    }

    /// <summary>The time Hybrid Launcher counted: the game's settings and its own record (which keeps it across installs).</summary>
    static PlayStats Counted(Game game, GameSettings settings)
    {
        PlaytimeLedger.Keep(game, settings.PlayMinutes, settings.LastPlayed);
        long minutes = settings.PlayMinutes;
        DateTime? last = settings.LastPlayed;
        if (PlaytimeLedger.Get(game) is { } kept)
        {
            minutes = Math.Max(minutes, kept.Minutes);
            last = Max(last, kept.LastPlayed);
        }
        // A game run through a Steam emulator: the time Hybrid Launcher counted, and the last session from the emulator's saves when newer.
        if (game.EmulatorAppId is { } emu && SettingsStore.Current.ReadSteamEmulator && SteamEmulator.LastPlayed(emu) is { } emuLast
            && (last is null || emuLast > last))
            return new PlayStats(minutes, emuLast, "Hybrid Launcher");
        return new PlayStats(minutes, last, "Hybrid Launcher");
    }

    static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Re-reads localconfig.vdf only when Steam rewrote it (it does at the end of every session).</summary>
    static Dictionary<string, (long Minutes, long LastPlayed)> SteamApps()
    {
        lock (Gate)
        {
            try
            {
                var root = SteamScanner.SteamRoot();
                var account = SteamAchievements.CurrentAccountId();
                if (root is null || account is null) return _steam;
                var file = Path.Combine(root, "userdata", account, "config", "localconfig.vdf");
                if (!File.Exists(file)) return _steam;
                var stamp = File.GetLastWriteTimeUtc(file);
                if (file == _file && stamp == _stamp) return _steam;

                var apps = VdfNode.Parse(File.ReadAllText(file))
                    .Child("UserLocalConfigStore")?.Child("Software")?.Child("Valve")?.Child("Steam")?.Child("apps");
                var map = new Dictionary<string, (long, long)>();
                if (apps is not null)
                    foreach (var (id, node) in apps.Children)
                        map[id] = (Long(node["Playtime"]), Long(node["LastPlayed"]));
                _steam = map;
                _file = file;
                _stamp = stamp;
            }
            catch (Exception ex)
            {
                Log.Warn($"localconfig.vdf: {ex.Message}");
            }
            return _steam;
        }
    }

    static long Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>"425 h", "38 min", "1.5 h".</summary>
    public static string FormatTime(long minutes) => minutes switch
    {
        <= 0 => "—",
        < 60 => $"{minutes} min",
        < 600 => $"{minutes / 60.0:0.#} h",
        _ => $"{minutes / 60:N0} h",
    };

    /// <summary>"Today", "Yesterday", "3 days ago", "12 Mar 2025".</summary>
    public static string FormatLast(DateTime? at)
    {
        if (at is not { } t) return L.T("Never");
        var days = (DateTime.Today - t.Date).Days;
        return days switch
        {
            <= 0 => L.T("Today"),
            1 => L.T("Yesterday"),
            < 7 => L.F("{0} days ago", days),
            _ => t.ToString(t.Year == DateTime.Today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture),
        };
    }
}
