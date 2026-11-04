using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridLauncher.Core;

/// <summary>One notification kept in the bell's list: what happened, when, and where a click takes you.</summary>
public sealed class InboxEntry
{
    public DateTime Time { get; set; }
    /// <summary>"opti", "ultra" or "app": picks the icon and tint.</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>The game a click opens (null: just the library, or the app's update dialog for "app").</summary>
    public string? GameId { get; set; }
    /// <summary>The game page tab a click opens ("opti", "ultra").</summary>
    public string? Tab { get; set; }
    public bool Read { get; set; }
    /// <summary>What it is about ("app:1.4.0"), so the same news is not kept twice.</summary>
    public string? Key { get; set; }
}

/// <summary>
/// Every notification the app sent (or, for silent auto-updates, would have sent), newest first, kept in notifications.json so a
/// toast you missed can still be read from the bell in the title bar. Bounded to <see cref="Max"/> entries.
/// </summary>
public static class Inbox
{
    public const int Max = 60;
    public const string Opti = "opti", Ultra = "ultra", App = "app";

    static readonly object Gate = new();
    static List<InboxEntry>? _entries;

    /// <summary>Raised (on any thread) after an entry was added, read or cleared.</summary>
    public static event Action? Changed;

    public static string File => Path.Combine(AppInfo.DataRoot, "notifications.json");

    public static IReadOnlyList<InboxEntry> Entries
    {
        get { lock (Gate) return [.. Load()]; }
    }

    public static int Unread
    {
        get { lock (Gate) return Load().Count(e => !e.Read); }
    }

    /// <summary>Keeps a notification; null when one with the same <paramref name="key"/> is already kept.</summary>
    /// <remarks>Kinds turned off in Settings → Notifications are not kept.</remarks>
    public static InboxEntry? Add(string kind, string title, string text, string? gameId = null, string? tab = null, bool read = false, string? key = null)
    {
        if (SettingsStore.Current.InboxOff.Contains(kind)) return null;
        var entry = new InboxEntry { Time = DateTime.UtcNow, Kind = kind, Title = title, Text = text, GameId = gameId, Tab = tab, Read = read, Key = key };
        lock (Gate)
        {
            var list = Load();
            if (key is not null && list.Any(e => e.Key == key)) return null;
            list.Insert(0, entry);
            if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
            Save(list);
        }
        Changed?.Invoke();
        return entry;
    }

    public static void MarkAllRead()
    {
        lock (Gate)
        {
            var list = Load();
            if (list.All(e => e.Read)) return;
            foreach (var e in list) e.Read = true;
            Save(list);
        }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            var list = Load();
            if (list.Count == 0) return;
            list.Clear();
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>Forgets what is in memory (tests that swap HL_DATA, or a file changed on disk).</summary>
    public static void Reload()
    {
        lock (Gate) _entries = null;
        Changed?.Invoke();
    }

    static List<InboxEntry> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            if (System.IO.File.Exists(File))
                _entries = JsonSerializer.Deserialize(System.IO.File.ReadAllText(File), InboxJson.Default.ListInboxEntry);
        }
        catch (Exception ex) { Log.Warn($"Notifications: {ex.Message}"); }
        _entries ??= [];
        _entries.RemoveAll(e => e is null || string.IsNullOrWhiteSpace(e.Title));
        // Settings → Notifications → Keep them for: older ones go when the list is first read.
        int days = SettingsStore.Current.InboxKeepDays;
        if (days > 0) _entries.RemoveAll(e => e.Time < DateTime.UtcNow.AddDays(-days));
        return _entries;
    }

    static void Save(List<InboxEntry> list)
    {
        try
        {
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(list, InboxJson.Default.ListInboxEntry));
            System.IO.File.Move(tmp, File, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"Notifications: could not save ({ex.Message})"); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<InboxEntry>))]
partial class InboxJson : JsonSerializerContext;
