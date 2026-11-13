using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HybridLauncher.Core;

/// <summary>One play session Hybrid Launcher saw: when the game was running and for how long.</summary>
public sealed record PlaySession(string GameId, string Name, DateTime Start, double Minutes);

/// <summary>
/// Every play session Hybrid Launcher saw (started from it or found running), one JSON line each in its data folder
/// (sessions.jsonl), for the game Dashboard: recent sessions, time played per day, the longest session. Steam's own hours stay
/// Steam's: this only lists what Hybrid Launcher watched. Written by hand (Utf8JsonWriter), so trimming keeps it working.
/// </summary>
public static class SessionLog
{
    public static string File_ => Path.Combine(AppInfo.DataRoot, "sessions.jsonl");
    static readonly object Gate = new();
    static List<PlaySession>? _cache;

    /// <summary>A session ended (shorter than half a minute: a launcher window that closed at once, left out).</summary>
    public static void Add(PlaySession s)
    {
        if (s.Minutes < 0.5) return;
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("id", s.GameId);
                w.WriteString("name", s.Name);
                w.WriteString("start", s.Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                w.WriteNumber("minutes", Math.Round(s.Minutes, 1));
                w.WriteEndObject();
            }
            lock (Gate)
            {
                AppInfo.Ensure(AppInfo.DataRoot);
                File.AppendAllText(File_, Encoding.UTF8.GetString(ms.ToArray()) + "\n");
                _cache?.Add(s);
            }
        }
        catch (Exception ex) { Log.Warn($"Session log: {ex.Message}"); }
    }

    /// <summary>The sessions of <paramref name="game"/> (by id, or by name for a game added again), newest first.</summary>
    public static List<PlaySession> For(Game game)
    {
        string name = PlaytimeLedger.NormalName(game.Name);
        return All().Where(s => s.GameId.Equals(game.Id, StringComparison.OrdinalIgnoreCase) || PlaytimeLedger.NormalName(s.Name) == name)
            .OrderByDescending(s => s.Start).ToList();
    }

    static List<PlaySession> All()
    {
        lock (Gate)
        {
            if (_cache is not null) return [.. _cache];
            var list = new List<PlaySession>();
            try
            {
                if (File.Exists(File_))
                    foreach (var line in File.ReadAllLines(File_))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var r = doc.RootElement;
                            if (!DateTime.TryParse(r.GetProperty("start").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
                            list.Add(new PlaySession(r.GetProperty("id").GetString() ?? "", r.GetProperty("name").GetString() ?? "", at.ToLocalTime(), r.GetProperty("minutes").GetDouble()));
                        }
                        catch (Exception) { /* a broken line is skipped */ }
                    }
            }
            catch (Exception ex) { Log.Warn($"Session log read: {ex.Message}"); }
            _cache = list;
            return [.. list];
        }
    }
}
