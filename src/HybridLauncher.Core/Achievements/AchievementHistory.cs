using System.Globalization;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Steam;

namespace HybridLauncher.Core.Achievements;

/// <summary>One unlock seen by Hybrid Launcher.</summary>
public sealed record UnlockEntry(DateTime At, string? GameId, string Game, string Store, string Key, string Name, string Description,
    string? Icon, double? Percent, int? Points, string? Screenshot);

/// <summary>
/// Every unlock Hybrid Launcher announced, kept in its data folder (one JSON line per unlock, achievements\history.jsonl), for the
/// unlock history in Settings → Achievements and its export. Settings → Achievements → "Keep an unlock history" turns it off.
/// Also the export of one game's whole list (CSV or JSON).
/// </summary>
public static class AchievementHistory
{
    static string Dir => AppInfo.Ensure(Path.Combine(AppInfo.DataRoot, "achievements"));
    public static string File_ => Path.Combine(Dir, "history.jsonl");
    static readonly object Gate = new();

    public static event Action? Changed;

    public static void Add(UnlockEntry e)
    {
        if (!SettingsStore.Current.AchHistory) return;
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("at", e.At.ToUniversalTime().ToString("O"));
                if (e.GameId is not null) w.WriteString("gameId", e.GameId);
                w.WriteString("game", e.Game);
                w.WriteString("store", e.Store);
                w.WriteString("key", e.Key);
                w.WriteString("name", e.Name);
                w.WriteString("description", e.Description);
                if (e.Icon is not null) w.WriteString("icon", e.Icon);
                if (e.Percent is { } p) w.WriteNumber("percent", Math.Round(p, 2));
                if (e.Points is { } pt) w.WriteNumber("points", pt);
                if (e.Screenshot is not null) w.WriteString("screenshot", e.Screenshot);
                w.WriteEndObject();
            }
            lock (Gate) File.AppendAllText(File_, Encoding.UTF8.GetString(ms.ToArray()) + "\n");
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Warn($"Unlock history: {ex.Message}"); }
    }

    /// <summary>A screenshot taken a moment after its unlock was written: the newest entry with that game and key gets it.</summary>
    public static void AttachScreenshot(string? gameId, string key, string path)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(File_)) return;
                var lines = File.ReadAllLines(File_);
                for (int i = lines.Length - 1; i >= Math.Max(0, lines.Length - 50); i--)
                {
                    var e = Parse(lines[i]);
                    if (e is null || e.Key != key || e.GameId != gameId || e.Screenshot is not null) continue;
                    using var doc = JsonDocument.Parse(lines[i]);
                    using var ms = new MemoryStream();
                    using (var w = new Utf8JsonWriter(ms))
                    {
                        w.WriteStartObject();
                        foreach (var p in doc.RootElement.EnumerateObject()) p.WriteTo(w);
                        w.WriteString("screenshot", path);
                        w.WriteEndObject();
                    }
                    lines[i] = Encoding.UTF8.GetString(ms.ToArray());
                    File.WriteAllLines(File_, lines);
                    break;
                }
            }
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Warn($"Unlock history screenshot: {ex.Message}"); }
    }

    /// <summary>Every unlock, newest first.</summary>
    public static List<UnlockEntry> All()
    {
        var list = new List<UnlockEntry>();
        try
        {
            string[] lines;
            lock (Gate) lines = File.Exists(File_) ? File.ReadAllLines(File_) : [];
            foreach (var line in lines) if (Parse(line) is { } e) list.Add(e);
        }
        catch (Exception ex) { Log.Warn($"Unlock history read: {ex.Message}"); }
        list.Reverse();
        return list;
    }

    public static void Clear()
    {
        lock (Gate) try { File.Delete(File_); } catch { }
        Changed?.Invoke();
    }

    static UnlockEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var at = DateTime.TryParse(S("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime() : DateTime.MinValue;
            return new UnlockEntry(at, S("gameId"), S("game") ?? "", S("store") ?? "", S("key") ?? "", S("name") ?? "", S("description") ?? "", S("icon"),
                r.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null,
                r.TryGetProperty("points", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : null, S("screenshot"));
        }
        catch { return null; }
    }

    // ---------- export ----------

    static string Csv(string? s) => s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>The history as CSV (.csv) or JSON (any other extension).</summary>
    public static void Export(IEnumerable<UnlockEntry> entries, string path)
    {
        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder("Unlocked,Game,Store,Achievement,Description,Rarity %,Points,Screenshot\r\n");
            foreach (var e in entries)
                sb.Append(Csv(e.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',').Append(Csv(e.Game)).Append(',').Append(Csv(e.Store)).Append(',')
                  .Append(Csv(e.Name)).Append(',').Append(Csv(e.Description)).Append(',').Append(e.Percent?.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.Points?.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Csv(e.Screenshot)).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return;
        }
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartArray();
        foreach (var e in entries)
        {
            w.WriteStartObject();
            w.WriteString("unlocked", e.At.ToString("O"));
            w.WriteString("game", e.Game);
            w.WriteString("store", e.Store);
            w.WriteString("achievement", e.Name);
            w.WriteString("description", e.Description);
            if (e.Percent is { } p) w.WriteNumber("rarity", Math.Round(p, 2));
            if (e.Points is { } pt) w.WriteNumber("points", pt);
            if (e.Screenshot is not null) w.WriteString("screenshot", e.Screenshot);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>One game's whole list (unlocked or not) as CSV (.csv) or JSON.</summary>
    public static void ExportSet(AchievementSet set, string game, string path)
    {
        var items = set.Items.OrderByDescending(a => a.Unlocked).ThenBy(a => a.UnlockedAt ?? DateTime.MaxValue).ToList();
        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder("Game,Achievement,Description,Unlocked,Unlocked at,Rarity %,Points,Progress,Id\r\n");
            foreach (var a in items)
                sb.Append(Csv(game)).Append(',').Append(Csv(a.Name)).Append(',').Append(Csv(a.Description)).Append(',').Append(a.Unlocked ? "yes" : "no").Append(',')
                  .Append(a.UnlockedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                  .Append(a.GlobalPercent?.ToString("0.##", CultureInfo.InvariantCulture)).Append(',').Append(a.Points?.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(a.ProgressMax is { } m ? $"{a.ProgressNow:0.##}/{m:0.##}".Replace(',', '.') : "").Append(',').Append(Csv(a.ApiName)).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return;
        }
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("game", game);
        w.WriteNumber("unlocked", set.UnlockedCount);
        w.WriteNumber("total", set.Total);
        w.WriteStartArray("achievements");
        foreach (var a in items)
        {
            w.WriteStartObject();
            w.WriteString("id", a.ApiName);
            w.WriteString("name", a.Name);
            w.WriteString("description", a.Description);
            w.WriteBoolean("unlocked", a.Unlocked);
            if (a.UnlockedAt is { } t) w.WriteString("unlockedAt", t.ToString("O"));
            if (a.GlobalPercent is { } p) w.WriteNumber("rarity", Math.Round(p, 2));
            if (a.Points is { } pt) w.WriteNumber("points", pt);
            if (a.ProgressMax is { } max) { w.WriteNumber("progress", a.ProgressNow ?? 0); w.WriteNumber("progressMax", max); }
            w.WriteBoolean("hidden", a.Hidden);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
