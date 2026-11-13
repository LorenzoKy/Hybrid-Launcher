using System.Globalization;
using System.Text.Json;
using HybridLauncher.Core.Steam;

namespace HybridLauncher.Core.Accounts;

/// <summary>
/// GOG achievements of the signed-in account, from GOG's gameplay service (the one GOG Galaxy itself reads):
/// gameplay.gog.com/clients/&lt;game id&gt;/users/&lt;user id&gt;/achievements, with the same sign-in Hybrid Launcher uses for downloads.
/// Only games that have GOG Galaxy achievements answer; any other answer (no achievements, not signed in, offline) is "none".
/// </summary>
public static class GogAchievements
{
    /// <summary>The GOG product id in a game id like "gog:1207658924", or null.</summary>
    public static string? ProductId(Game game) =>
        game is { Store: GameStore.Gog } && game.Id.StartsWith("gog:", StringComparison.Ordinal) && game.Id[4..] is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? id : null;

    public static async Task<AchievementSet?> ReadAsync(string productId, CancellationToken ct)
    {
        if (!SettingsStore.Current.ReadGogAchievements || !GogAccount.SignedIn || GogAccount.UserId is not { } user) return null;
        try
        {
            var json = await GogAccount.GetAuthorizedAsync($"https://gameplay.gog.com/clients/{productId}/users/{user}/achievements", ct);
            return Parse(productId, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"GOG achievements {productId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The service's answer: {"items":[{"achievement_key","name","description","image_url_unlocked","image_url_locked","visible","rarity","date_unlocked"}]}.</summary>
    internal static AchievementSet? Parse(string productId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var list = new List<Achievement>();
        foreach (var a in items.EnumerateArray())
        {
            var key = Str(a, "achievement_key");
            if (key.Length == 0) key = Str(a, "achievement_id");
            if (key.Length == 0) continue;
            DateTime? at = null;
            if (Str(a, "date_unlocked") is { Length: > 0 } stamp && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                at = when.LocalDateTime;
            double? rarity = a.TryGetProperty("rarity", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null;
            list.Add(new Achievement
            {
                Key = key,
                ApiName = key,
                Name = Str(a, "name") is { Length: > 0 } n ? n : key,
                Description = Str(a, "description"),
                Hidden = a.TryGetProperty("visible", out var vis) && vis.ValueKind == JsonValueKind.False,
                IconUrl = Str(a, "image_url_unlocked") is { Length: > 0 } iu ? iu : null,
                IconGrayUrl = Str(a, "image_url_locked") is { Length: > 0 } il ? il : null,
                Unlocked = at is not null,
                UnlockedAt = at,
                GlobalPercent = rarity,
            });
        }
        return list.Count > 0 ? new AchievementSet(productId, list) : null;
    }
}
