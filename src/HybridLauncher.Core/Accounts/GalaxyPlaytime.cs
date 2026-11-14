using System.Globalization;
using HybridLauncher.Core.Achievements;

namespace HybridLauncher.Core.Accounts;

/// <summary>
/// GOG Galaxy's own count of play time, read from its database on this PC (galaxy-2.0.db: GameTimes and LastPlayedDates, keyed
/// by release keys like "gog_1207658924" or "epic_Sugar"). Galaxy counts GOG games and, through its integrations, games of
/// other launchers (Epic among them), installed or not, with no sign-in in Hybrid Launcher (2.1-rc.4). Read again only when
/// Galaxy rewrote the file; read-only, and through a copy when Galaxy holds it locked.
/// </summary>
public static class GalaxyPlaytime
{
    static readonly object Gate = new();
    static DateTime _stamp;
    static Dictionary<string, (long Minutes, DateTime? Last)> _times = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Galaxy's database, or null when GOG Galaxy is not installed.</summary>
    public static string? DatabaseFile
    {
        get
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
            return File.Exists(file) ? file : null;
        }
    }

    /// <summary>The Galaxy release key of a game ("gog_&lt;id&gt;", "epic_&lt;app&gt;"), or null for launchers Galaxy keys otherwise.</summary>
    public static string? KeyOf(Game game) => game.Store switch
    {
        GameStore.Gog when game.Id.StartsWith("gog:", StringComparison.Ordinal) => "gog_" + game.Id[4..],
        GameStore.Epic when game.Id.StartsWith("epic:", StringComparison.Ordinal) => "epic_" + game.Id[5..],
        _ => null,
    };

    /// <summary>Galaxy's minutes and last session for the game; null when Galaxy has none.</summary>
    public static (long Minutes, DateTime? Last)? For(Game game)
    {
        if (KeyOf(game) is not { } key) return null;
        var all = Read();
        return all.TryGetValue(key, out var t) && (t.Minutes > 0 || t.Last is not null) ? t : null;
    }

    static Dictionary<string, (long Minutes, DateTime? Last)> Read()
    {
        lock (Gate)
        {
            if (DatabaseFile is not { } file) return _times;
            DateTime stamp;
            try
            {
                // The write-ahead log changes first: either file newer means new numbers.
                stamp = File.GetLastWriteTimeUtc(file);
                var wal = file + "-wal";
                if (File.Exists(wal) && File.GetLastWriteTimeUtc(wal) > stamp) stamp = File.GetLastWriteTimeUtc(wal);
            }
            catch { return _times; }
            if (stamp == _stamp) return _times;
            // The stamp is kept only once the database was read (2.1 audit): a read that failed while Galaxy held the file
            // is tried again on the next call, not skipped until Galaxy writes it again.
            try { _times = Load(file); _stamp = stamp; }
            catch (Exception ex)
            {
                // Galaxy may hold the file: read a copy (with its log) instead.
                var dir = Path.Combine(Path.GetTempPath(), "hl-galaxy-" + Environment.ProcessId);
                try
                {
                    Directory.CreateDirectory(dir);
                    var copy = Path.Combine(dir, "galaxy-2.0.db");
                    File.Copy(file, copy, true);
                    foreach (var ext in new[] { "-wal", "-shm" })
                        if (File.Exists(file + ext)) File.Copy(file + ext, copy + ext, true);
                    _times = Load(copy);
                    _stamp = stamp;
                }
                catch (Exception ex2) { Log.Warn($"GOG Galaxy play time: {ex.Message}; copy: {ex2.Message}"); }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
            return _times;
        }
    }

    static Dictionary<string, (long Minutes, DateTime? Last)> Load(string file)
    {
        var map = new Dictionary<string, (long Minutes, DateTime? Last)>(StringComparer.OrdinalIgnoreCase);
        using var db = WinSqlite.Open(file);
        // One row per Galaxy user and game: the biggest count wins (a PC shared by two Galaxy accounts).
        foreach (var row in db.Query("SELECT releaseKey, minutesInGame FROM GameTimes", 2))
            if (row[0] is { Length: > 0 } key && long.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
                map[key] = (Math.Max(map.GetValueOrDefault(key).Minutes, m), map.GetValueOrDefault(key).Last);
        try
        {
            foreach (var row in db.Query("SELECT gameReleaseKey, lastPlayedDate FROM LastPlayedDates", 2))
                if (row[0] is { Length: > 0 } key && DateTime.TryParse(row[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                {
                    var local = at.ToLocalTime();
                    var old = map.GetValueOrDefault(key);
                    map[key] = (old.Minutes, old.Last is { } l && l > local ? l : local);
                }
        }
        catch (Exception ex) { Log.Warn($"GOG Galaxy last played: {ex.Message}"); }
        return map;
    }
}
