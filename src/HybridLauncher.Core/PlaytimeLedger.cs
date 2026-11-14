using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridLauncher.Core;

/// <summary>
/// Hybrid Launcher's own record of time played, kept apart from the games and the library (playtime.json in the data folder,
/// with a .bak). A game's time survives uninstalling it, moving it, reinstalling it from another store or adding it again by
/// hand: each record is found by every name the game goes by (its Steam app id, its id here, its name without marks and
/// punctuation), so the same game finds its hours again however it comes back. Always on, nothing to set.
/// </summary>
public static class PlaytimeLedger
{
    public sealed class Entry
    {
        /// <summary>"steam:1817070", "id:custom-…", "name:marvels spider man 2".</summary>
        public List<string> Keys { get; set; } = [];
        public string Name { get; set; } = "";
        public long Minutes { get; set; }
        public DateTime? LastPlayed { get; set; }
        public DateTime? FirstPlayed { get; set; }
    }

    public sealed class Book
    {
        public int Version { get; set; } = 1;
        public List<Entry> Games { get; set; } = [];
    }

    static readonly object Gate = new();
    static Book? _book;

    public static string File => Path.Combine(AppInfo.DataRoot, "playtime.json");

    /// <summary>"Marvel's Spider-Man™ 2" → "marvels spider man 2".</summary>
    public static string NormalName(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool space = false;
        foreach (var c in name.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { if (space && sb.Length > 0) sb.Append(' '); sb.Append(c); space = false; }
            else if (c is '\'' or '’' || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) { }
            else space = true;
        }
        return sb.ToString();
    }

    /// <summary>Every name <paramref name="game"/> goes by in the record.</summary>
    public static List<string> KeysOf(Game game)
    {
        var keys = new List<string>();
        if (game.SteamAppId is { Length: > 0 } steam) keys.Add("steam:" + steam);
        if (game.EmulatorAppId is { Length: > 0 } emu && emu != game.SteamAppId) keys.Add("steam:" + emu);
        keys.Add("id:" + game.Id);
        var name = NormalName(game.Name);
        if (name.Length >= 2) keys.Add("name:" + name);
        return keys;
    }

    /// <summary>What the record holds for <paramref name="game"/>, or null.</summary>
    public static (long Minutes, DateTime? LastPlayed)? Get(Game game)
    {
        lock (Gate)
        {
            var e = Find(KeysOf(game));
            return e is null ? null : (e.Minutes, e.LastPlayed);
        }
    }

    /// <summary>Adds <paramref name="minutes"/> played (and the moment) to the game's record. Saved at once.</summary>
    public static void Add(Game game, long minutes, DateTime when)
    {
        lock (Gate)
        {
            var e = FindOrNew(game);
            e.Minutes += Math.Max(0, minutes);
            e.FirstPlayed ??= when;
            if (e.LastPlayed is null || when > e.LastPlayed) e.LastPlayed = when;
            SaveLocked();
        }
    }

    /// <summary>A session started now (counted later by <see cref="Add"/>).</summary>
    public static void Started(Game game, DateTime when) => Add(game, 0, when);

    /// <summary>
    /// Makes sure the record has at least what another count knows (the time kept in the game's settings before this record
    /// existed): only ever raises it. Writes only when something changed.
    /// </summary>
    public static void Keep(Game game, long minutes, DateTime? last)
    {
        if (minutes <= 0 && last is null) return;
        lock (Gate)
        {
            var keys = KeysOf(game);
            var e = Find(keys);
            if (e is not null && e.Minutes >= minutes && (last is null || (e.LastPlayed is { } l && l >= last))
                && keys.All(e.Keys.Contains)) return;
            e ??= FindOrNew(game);
            foreach (var k in keys) if (!e.Keys.Contains(k)) e.Keys.Add(k);
            e.Minutes = Math.Max(e.Minutes, minutes);
            if (last is { } t && (e.LastPlayed is null || t > e.LastPlayed)) e.LastPlayed = t;
            e.FirstPlayed ??= e.LastPlayed;
            SaveLocked();
        }
    }

    // ---------- the file ----------

    static Entry? Find(List<string> keys)
    {
        var book = Load();
        // The strongest name first: an app id or this game's own id before a name two games could share.
        foreach (var k in keys)
            if (book.Games.FirstOrDefault(e => e.Keys.Contains(k)) is { } e) return e;
        return null;
    }

    static Entry FindOrNew(Game game)
    {
        var keys = KeysOf(game);
        var e = Find(keys);
        if (e is null)
        {
            e = new Entry { Name = game.Name };
            Load().Games.Add(e);
        }
        foreach (var k in keys) if (!e.Keys.Contains(k)) e.Keys.Add(k);
        if (game.Name.Length > 0) e.Name = game.Name;
        return e;
    }

    static Book Load()
    {
        if (_book is not null) return _book;
        foreach (var f in new[] { File, File + ".bak" })
        {
            try
            {
                if (!System.IO.File.Exists(f)) continue;
                _book = JsonSerializer.Deserialize(System.IO.File.ReadAllText(f), PlaytimeJson.Default.Book);
                if (_book is not null) return _book;
            }
            catch (Exception ex) { Log.Warn($"Play time record {Path.GetFileName(f)}: {ex.Message}"); }
        }
        return _book = new Book();
    }

    static void SaveLocked()
    {
        try
        {
            var file = File;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(Load(), PlaytimeJson.Default.Book));
            if (System.IO.File.Exists(file)) System.IO.File.Replace(tmp, file, file + ".bak", ignoreMetadataErrors: true);
            else System.IO.File.Move(tmp, file);
        }
        catch (Exception ex) { Log.Warn($"Saving the play time record: {ex.Message}"); }
    }

    /// <summary>
    /// Joins another PC's record (its playtime.json, "Hybrid Config and Stats", 2.1-rc.4): a game found by any of its names
    /// keeps the bigger count, the newer last session, the older first one and every name; a game only there is added.
    /// Saved when something changed.
    /// </summary>
    public static void MergeFrom(string json)
    {
        Book? other;
        try { other = JsonSerializer.Deserialize(json, PlaytimeJson.Default.Book); }
        catch (Exception ex) { Log.Warn($"Play time record from the cloud: {ex.Message}"); return; }
        if (other is null) return;
        lock (Gate)
        {
            bool changed = false;
            foreach (var e in other.Games)
            {
                var mine = Find(e.Keys);
                if (mine is null)
                {
                    Load().Games.Add(new Entry { Keys = [.. e.Keys], Name = e.Name, Minutes = e.Minutes, LastPlayed = e.LastPlayed, FirstPlayed = e.FirstPlayed });
                    changed = true;
                    continue;
                }
                foreach (var k in e.Keys) if (!mine.Keys.Contains(k)) { mine.Keys.Add(k); changed = true; }
                if (e.Minutes > mine.Minutes) { mine.Minutes = e.Minutes; changed = true; }
                if (e.LastPlayed is { } l && (mine.LastPlayed is null || l > mine.LastPlayed)) { mine.LastPlayed = l; changed = true; }
                if (e.FirstPlayed is { } f && (mine.FirstPlayed is null || f < mine.FirstPlayed)) { mine.FirstPlayed = f; changed = true; }
            }
            if (changed) SaveLocked();
        }
    }

    /// <summary>Forgets what was read (self-test, after the data folder changed).</summary>
    public static void Reload() { lock (Gate) _book = null; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PlaytimeLedger.Book))]
internal partial class PlaytimeJson : JsonSerializerContext;
