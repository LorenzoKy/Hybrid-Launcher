using System.Text.Json.Nodes;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// A hash of the settings as a page sees them, for pages kept in memory (2.0.0-rc.4): a page shows what it built as long as
/// the settings it reads are the same. The settings that change while the app is merely used (the game opened last, the open
/// tab, the window's size, the cover size) never count, nor the parts the caller names. Worked out again only after a save.
/// </summary>
public static class SettingsKeys
{
    // What the app writes by itself while merely used: the window, the tab and game opened last, and the "already seen / already done"
    // marks of update checks, notifications and first-start steps (no page shows them).
    static readonly string[] Volatile = ["LastGameId", "GameTab", "SettingsTab", "WindowWidth", "WindowHeight", "WindowMaximized", "SidebarWidth", "LastVersionSeen",
        "OptiSeen", "UltraPlusSeen", "CacheWarmed", "AppUpdatePopupShown", "SkippedAppUpdate", "TourSeen", "FeaturesChosen", "ModsPathDefaultApplied", "HdrRestore",
        "Profiles", "ActiveProfile", "SteamGridDbKey",
        // Folds are applied in place where they are shown (LibraryPage.ApplyFold, the game page's cards): a fold must not
        // rebuild a whole page (2.1-rc.3 audit). The side list's own filters only redraw the list (2.1-rc.4).
        "CollapsedGroups", "CollapsedCards", "ListFilter", "HiddenFromStats"];
    static readonly string[] GameVolatile = ["SizeBytes", "SizeMeasuredAt", "CloudSyncedHash", "CloudSyncedAt", "Notes", "NoteSections", "QuickLinks", "DismissedModProblems", "SgdbGameId", "SgdbGameName"];
    static int _partsVersion = -1;
    static Dictionary<string, int> _parts = [];
    static readonly Dictionary<string, (int Version, int Key)> Cache = [];

    /// <summary>
    /// The settings in parts (2.0.0-rc.9): one hash per top-level setting, and one per game under "Games/&lt;id&gt;", with the
    /// volatile ones left out. A kept page compares the parts it was built with to these and learns WHICH settings changed,
    /// not only that something did: a save of a setting it never reads (an update check's "seen" mark, a notification) no
    /// longer throws away a page that was ready (rc.8: going back to a game rebuilt it whenever anything had been saved).
    /// Worked out once per save.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Parts(AppSettings settings)
    {
        if (_partsVersion == SettingsStore.Version) return _parts;
        var parts = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (System.Text.Json.JsonSerializer.SerializeToNode(settings, HybridJson.Default.AppSettings) is JsonObject node)
            {
                foreach (var name in Volatile) node.Remove(name);
                if (node["Library"] is JsonObject lib) lib.Remove("TileSize");
                foreach (var (name, value) in node)
                {
                    if (name == "Games" && value is JsonObject games)
                    {
                        // Per game, what a visit writes by itself (the size measured, the last cloud sync) and what no other page reads.
                        foreach (var (id, game) in games)
                        {
                            if (game is JsonObject g) foreach (var v in GameVolatile) g.Remove(v);
                            parts["Games/" + id] = game?.ToJsonString().GetHashCode() ?? 0;
                        }
                        continue;
                    }
                    parts[name] = value?.ToJsonString().GetHashCode() ?? 0;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Settings key: {ex.Message}"); }
        _parts = parts;
        _partsVersion = SettingsStore.Version;
        return parts;
    }

    /// <summary>The settings that differ between two <see cref="Parts"/> (added, removed or changed), in name order.</summary>
    public static List<string> Changed(IReadOnlyDictionary<string, int>? before, IReadOnlyDictionary<string, int> now)
    {
        if (before is null) return ["(none before)"];
        if (ReferenceEquals(before, now)) return [];
        var changed = new List<string>();
        foreach (var (k, v) in now) if (!before.TryGetValue(k, out var old) || old != v) changed.Add(k);
        foreach (var k in before.Keys) if (!now.ContainsKey(k)) changed.Add(k);
        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    /// <param name="leaveOut">Top-level settings this page does not read (e.g. "Games", the per-game settings).</param>
    public static int Of(AppSettings settings, params string[] leaveOut)
    {
        string id = string.Join(",", leaveOut);
        if (Cache.TryGetValue(id, out var c) && c.Version == SettingsStore.Version) return c.Key;
        var hash = new HashCode();
        foreach (var (k, v) in Parts(settings).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (leaveOut.Any(l => k == l || k.StartsWith(l + "/", StringComparison.Ordinal))) continue;
            hash.Add(k);
            hash.Add(v);
        }
        int key = hash.ToHashCode();
        Cache[id] = (SettingsStore.Version, key);
        return key;
    }
}
