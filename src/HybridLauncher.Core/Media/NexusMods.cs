using System.Text.Json;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

/// <summary>Finds a game's page on Nexus Mods by matching its name against Nexus' public games list.</summary>
public static class NexusMods
{
    const string GamesList = "https://data.nexusmods.com/file/nexus-data/games.json";
    const string Site = "https://www.nexusmods.com/";

    static Dictionary<string, string>? _domains;

    /// <summary>The game's Nexus page (www.nexusmods.com/&lt;domain&gt;), or a Nexus search for its name when it has none.</summary>
    public static async Task<string> PageForAsync(string gameName, CancellationToken ct = default)
    {
        var domain = await DomainForAsync(gameName, ct);
        return domain is not null ? Site + domain : $"{Site}search?keyword={Uri.EscapeDataString(gameName)}";
    }

    public static async Task<string?> DomainForAsync(string gameName, CancellationToken ct = default)
    {
        if (_domains is null)
        {
            var json = await Http.GetCachedAsync(GamesList, "nexus-games.json", TimeSpan.FromDays(7), ct: ct);
            var map = new Dictionary<string, string>();
            if (json is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        // Most-downloaded first, so a name shared by two entries resolves to the main one.
                        var games = doc.RootElement.EnumerateArray()
                            .Select(g => (Name: Str(g, "name"), Domain: Str(g, "domain_name"), Downloads: g.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var n) ? n : 0))
                            .Where(g => g.Name is not null && g.Domain is not null)
                            .OrderByDescending(g => g.Downloads);
                        foreach (var g in games)
                        {
                            map.TryAdd(ExeLocator.Normalize(g.Name!), g.Domain!);
                            map.TryAdd(ExeLocator.Normalize(g.Domain!), g.Domain!);
                        }
                    }
                }
                catch (JsonException ex) { Log.Warn($"Nexus games list unreadable: {ex.Message}"); }
            }
            // Only keep an empty result for this session: the next start tries the download again.
            _domains = map;
        }

        var key = ExeLocator.Normalize(gameName);
        if (key.Length < 2) return null;
        if (_domains.TryGetValue(key, out var exact)) return exact;
        // "Cyberpunk 2077 Ultimate Edition" vs "Cyberpunk 2077": the longest name that one starts with the other, only when
        // the shorter covers most of the longer and the rest is not a number ("Spider-Man" never lands on "Spider-Man 2").
        var best = _domains.Where(p => p.Key.Length >= 5 && IsLooseMatch(key, p.Key)).OrderByDescending(p => p.Key.Length).FirstOrDefault();
        return best.Value;
    }

    static bool IsLooseMatch(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return longer.StartsWith(shorter, StringComparison.Ordinal) && longer.Length > shorter.Length
            && shorter.Length * 10 >= longer.Length * 6 && !char.IsDigit(longer[shorter.Length]);
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
