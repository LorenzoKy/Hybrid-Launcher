using System.Text.Json;

namespace HybridLauncher.Core.Steam;

/// <summary>
/// Games run through a Steam emulator (Goldberg SteamEmu and its gbe_fork, "GSE"): the Steam app id it was set up with (its
/// steam_appid.txt), and, read only when Settings → Features allows it, the achievements it records and the time of the last
/// session. Play time is not kept by the emulator: Hybrid Launcher counts it for the games it starts.
/// </summary>
public static class SteamEmulator
{
    /// <summary>The emulator's per-game save folders under %APPDATA% (gbe_fork's first, then the original Goldberg's).</summary>
    static IEnumerable<string> SaveRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "GSE Saves");
        yield return Path.Combine(appData, "Goldberg SteamEmu Saves");
    }

    /// <summary>
    /// The emulator's settings folder for a game: "steam_settings" beside its steam_api dll, looked for next to the exe, in the
    /// install folder and one or two folders down (games keep the exe in "main", "bin\x64"...). Null when the game has none.
    /// </summary>
    public static string? SettingsDir(string installDir, string? exe)
    {
        var dirs = new List<string>();
        if (exe is not null && Path.GetDirectoryName(exe) is { } exeDir) { dirs.Add(exeDir); if (Path.GetDirectoryName(exeDir) is { } up) dirs.Add(up); }
        dirs.Add(installDir);
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(installDir))
            {
                dirs.Add(sub);
                foreach (var sub2 in Directory.EnumerateDirectories(sub).Take(20)) dirs.Add(sub2);
            }
        }
        catch { }
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var settings = Path.Combine(dir, "steam_settings");
            if (Directory.Exists(settings) && (File.Exists(Path.Combine(dir, "steam_api64.dll")) || File.Exists(Path.Combine(dir, "steam_api.dll"))
                || File.Exists(Path.Combine(settings, "steam_appid.txt"))))
                return settings;
        }
        return null;
    }

    /// <summary>The Steam app id the emulator was set up with (steam_settings\steam_appid.txt, or steam_appid.txt beside it).</summary>
    public static string? AppId(string settingsDir)
    {
        foreach (var file in new[] { Path.Combine(settingsDir, "steam_appid.txt"), Path.Combine(Path.GetDirectoryName(settingsDir)!, "steam_appid.txt") })
        {
            try
            {
                if (!File.Exists(file)) continue;
                var id = File.ReadAllText(file).Trim();
                if (id.Length is > 0 and < 12 && id.All(char.IsDigit)) return id;
            }
            catch { }
        }
        return null;
    }

    /// <summary>The folder the emulator saves this app's progress in (achievements.json, remote saves), or null.</summary>
    public static string? SaveDir(string appId) =>
        SaveRoots().Select(r => Path.Combine(r, appId)).FirstOrDefault(Directory.Exists);

    /// <summary>
    /// The achievements as the emulator has them: names, descriptions and icons from steam_settings\achievements.json, earned
    /// state and time from the save folder. Null when the game has no achievement list.
    /// </summary>
    public static AchievementSet? ReadAchievements(string appId, string settingsDir)
    {
        try
        {
            var schemaFile = Path.Combine(settingsDir, "achievements.json");
            if (!File.Exists(schemaFile)) return null;
            using var schema = JsonDocument.Parse(File.ReadAllText(schemaFile));
            if (schema.RootElement.ValueKind != JsonValueKind.Array) return null;

            var earned = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            if (SaveDir(appId) is { } save && File.Exists(Path.Combine(save, "achievements.json")))
            {
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(save, "achievements.json")));
                if (state.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in state.RootElement.EnumerateObject())
                    {
                        if (!p.Value.TryGetProperty("earned", out var e) || e.ValueKind != JsonValueKind.True) continue;
                        long t = p.Value.TryGetProperty("earned_time", out var et) && et.TryGetInt64(out var v) ? v : 0;
                        earned[p.Name] = t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : null;
                    }
            }

            string? Icon(JsonElement a, string prop) =>
                a.TryGetProperty(prop, out var i) && i.GetString() is { Length: > 0 } rel && File.Exists(Path.Combine(settingsDir, rel))
                    ? Path.GetFullPath(Path.Combine(settingsDir, rel)) : null;
            string Str(JsonElement a, string prop) =>
                a.TryGetProperty(prop, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? ""
                    // Some dumps keep every language: {"english": "...", ...}.
                    : v.ValueKind == JsonValueKind.Object && v.TryGetProperty("english", out var en) ? en.GetString() ?? "" : "" : "";

            var items = new List<Achievement>();
            foreach (var a in schema.RootElement.EnumerateArray())
            {
                var api = Str(a, "name");
                if (api.Length == 0) continue;
                bool hidden = a.TryGetProperty("hidden", out var h) && (h.ValueKind == JsonValueKind.Number ? h.GetInt32() != 0 : h.ValueKind == JsonValueKind.True || h.GetString() == "1");
                items.Add(new Achievement
                {
                    Key = api,
                    ApiName = api,
                    Name = Str(a, "displayName") is { Length: > 0 } n ? n : api,
                    Description = Str(a, "description"),
                    Hidden = hidden,
                    IconUrl = Icon(a, "icon"),
                    IconGrayUrl = Icon(a, "icongray") ?? Icon(a, "icon_gray"),
                    Unlocked = earned.ContainsKey(api),
                    UnlockedAt = earned.TryGetValue(api, out var at) ? at : null,
                });
            }
            return items.Count > 0 ? new AchievementSet(appId, items) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Emulator achievements {appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>When the emulator last wrote this app's progress (saves, achievements): the last session, near enough.</summary>
    public static DateTime? LastPlayed(string appId)
    {
        if (SaveDir(appId) is not { } dir) return null;
        try
        {
            var newest = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max();
            return newest > DateTime.MinValue ? newest : null;
        }
        catch { return null; }
    }
}
