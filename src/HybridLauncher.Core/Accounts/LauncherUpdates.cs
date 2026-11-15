using System.Text.Json;
using System.Text.Json.Nodes;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Core.Accounts;

/// <summary>
/// A game a launcher installed (Steam, the Epic Games Launcher, GOG Galaxy), as that launcher records it: its folder, the version
/// installed, the file or key holding that record, and for Steam the depots it put in with their manifest (2.1.4).
/// </summary>
public sealed record LauncherInstall(GameStore Store, string Id, string Name, string Folder, string? Version, string Record, IReadOnlyDictionary<uint, ulong> Depots, bool Busy);

/// <summary>A game a launcher installed whose store has a newer build (2.1.4).</summary>
public sealed record LauncherUpdate(LauncherInstall Install, OwnedGame Game, string Latest);

/// <summary>
/// Updates for the games Steam, the Epic Games Launcher and GOG Galaxy installed, made by Hybrid Launcher itself (2.1.4), as
/// Legendary and Heroic do for Epic and GOG: the store's newest build is compared with the one the launcher recorded, only the
/// files that changed are downloaded into the game's folder, then the launcher's record is brought up to the new build (Steam's
/// app manifest, Epic's .item) so the launcher counts the game up to date. A store whose account is not signed in is left out,
/// and so is a game its launcher is running or updating right now.
/// </summary>
public static class LauncherUpdates
{
    // Steam's StateFlags: 4 fully installed; 64 running, 256 updating, 512 update paused, 1024 update started.
    const int SteamInstalled = 4, SteamBusy = 64 | 256 | 512 | 1024;

    /// <summary>Every game a launcher installed on this PC, but the ones Hybrid Launcher downloaded itself.</summary>
    public static List<LauncherInstall> Scan()
    {
        HashSet<string> mine;
        lock (SettingsStore.Current) mine = SettingsStore.Current.DirectInstalls.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<LauncherInstall>();
        try { list.AddRange(ScanSteam()); } catch (Exception ex) { Log.Warn($"Launcher updates, Steam: {ex.Message}"); }
        try { list.AddRange(ScanEpic(EpicScanner.ManifestsDir())); } catch (Exception ex) { Log.Warn($"Launcher updates, Epic: {ex.Message}"); }
        try { list.AddRange(ScanGog()); } catch (Exception ex) { Log.Warn($"Launcher updates, GOG: {ex.Message}"); }
        return list.Where(i => !mine.Contains(i.Id)).ToList();
    }

    static List<LauncherInstall> ScanSteam()
    {
        var list = new List<LauncherInstall>();
        foreach (var lib in SteamScanner.Libraries())
        {
            var steamapps = Path.Combine(lib, "steamapps");
            string[] files;
            try { files = Directory.GetFiles(steamapps, "appmanifest_*.acf"); } catch { continue; }
            foreach (var acf in files)
            {
                VdfNode? state;
                try { state = VdfNode.Parse(File.ReadAllText(acf)).Child("AppState"); } catch { continue; }
                if (state?["appid"] is not { Length: > 0 } appId || state["installdir"] is not { Length: > 0 } dir) continue;
                var folder = Path.Combine(steamapps, "common", dir);
                if (!Directory.Exists(folder)) continue;
                int.TryParse(state["StateFlags"], out var flags);
                var depots = new Dictionary<uint, ulong>();
                foreach (var (k, node) in state.Child("InstalledDepots")?.Children ?? [])
                    if (uint.TryParse(k, out var d)) depots[d] = ulong.TryParse(node["manifest"], out var m) ? m : 0;
                bool busy = (flags & SteamInstalled) == 0 || (flags & SteamBusy) != 0
                    || RegistryValue.Read($@"HKEY_CURRENT_USER\Software\Valve\Steam\Apps\{appId}", "Updating") is int u && u != 0;
                list.Add(new LauncherInstall(GameStore.Steam, "steam:" + appId, state["name"] ?? dir, folder, state["buildid"], acf, depots, busy));
            }
        }
        return list;
    }

    /// <summary>The games of a folder of Epic's .item manifests (the launcher's, or a test's).</summary>
    public static List<LauncherInstall> ScanEpic(string manifestsDir)
    {
        var list = new List<LauncherInstall>();
        if (!Directory.Exists(manifestsDir)) return list;
        foreach (var item in Directory.GetFiles(manifestsDir, "*.item"))
        {
            JsonNode? r;
            try { r = JsonNode.Parse(File.ReadAllText(item)); } catch { continue; }
            if (r is null || r["bIsIncompleteInstall"]?.GetValueKind() == JsonValueKind.True) continue;
            var app = Str(r, "AppName");
            var main = Str(r, "MainGameAppName");
            if (app is null || !string.IsNullOrWhiteSpace(main) && !main.Equals(app, StringComparison.OrdinalIgnoreCase)) continue;
            if (Str(r, "InstallLocation") is not { Length: > 0 } folder || !Directory.Exists(folder)) continue;
            list.Add(new LauncherInstall(GameStore.Epic, "epic:" + app, Str(r, "DisplayName") ?? app, folder, Str(r, "AppVersionString"), item, new Dictionary<uint, ulong>(), false));
        }
        return list;
    }

    static string? Str(JsonNode r, string key) => r[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static List<LauncherInstall> ScanGog()
    {
        var list = new List<LauncherInstall>();
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (key is null) return list;
        foreach (var id in key.GetSubKeyNames())
        {
            using var g = key.OpenSubKey(id);
            if (g is null || g.GetValue("dependsOn") is string dep && dep.Length > 0) continue;
            if (g.GetValue("path") is not string folder || !Directory.Exists(folder)) continue;
            var gameId = g.GetValue("gameID") as string ?? id;
            list.Add(new LauncherInstall(GameStore.Gog, "gog:" + gameId, g.GetValue("gameName") as string ?? Path.GetFileName(folder), folder, g.GetValue("ver") as string, g.Name, new Dictionary<uint, ulong>(), false));
        }
        return list;
    }

    /// <summary>
    /// The version installed: the launcher's record, or the build Hybrid Launcher put in while that record still shows what it
    /// showed before (GOG Galaxy's, which Hybrid Launcher cannot write). A record that changed since means the launcher updated it.
    /// </summary>
    public static string? Installed(LauncherInstall install)
    {
        var s = SettingsStore.Current;
        lock (s)
        {
            if (!s.LauncherUpdates.TryGetValue(install.Id, out var mark)) return install.Version;
            if (string.Equals(mark.Before, install.Version, StringComparison.OrdinalIgnoreCase)) return mark.Applied;
            s.LauncherUpdates.Remove(install.Id);
            return install.Version;
        }
    }

    /// <summary>The store's newest build of a game (null when the store says nothing).</summary>
    public static async Task<string?> LatestAsync(LauncherInstall install, OwnedGame game, CancellationToken ct) => install.Store switch
    {
        GameStore.Steam => await SteamAccount.LatestBuildAsync(install.Id["steam:".Length..], ct),
        GameStore.Epic => await EpicAccount.LatestBuildAsync(game, ct),
        GameStore.Gog => await GogAccount.LatestBuildAsync(install.Id["gog:".Length..], ct),
        _ => null,
    };

    /// <summary>The launcher installs with a newer build in their store; a store not answering leaves only its games out.</summary>
    public static async Task<List<LauncherUpdate>> CheckAsync(Func<string, OwnedGame?> owned, CancellationToken ct, IEnumerable<LauncherInstall>? installs = null)
    {
        var found = new List<LauncherUpdate>();
        int count = 0;
        foreach (var install in installs ?? Scan())
        {
            ct.ThrowIfCancellationRequested();
            if (!OwnedGames.Direct(install.Store) || owned(install.Id) is not { } game) continue;
            if (install.Busy) { Log.Info($"Update check: {install.Name} is running or updating in {install.Store}"); continue; }
            count++;
            try
            {
                var latest = await LatestAsync(install, game, ct);
                if (latest is { Length: > 0 } && !latest.Equals(Installed(install) ?? "", StringComparison.OrdinalIgnoreCase))
                    found.Add(new LauncherUpdate(install, game, latest));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Update check of {install.Name}: {ex.Message}"); }
        }
        Log.Info(found.Count == 0 ? $"Update check: the {count} game(s) launchers installed are up to date"
            : "Update check: " + string.Join(", ", found.Select(u => $"{u.Install.Name} ({u.Install.Store}) {Installed(u.Install) ?? "?"} → {u.Latest}")));
        return found;
    }

    /// <summary>
    /// After Hybrid Launcher updated a launcher's game: the launcher's record says the new build where Hybrid Launcher can write it
    /// (Steam's app manifest is written by the download itself, Epic's .item here), and the build is remembered.
    /// </summary>
    public static void Applied(LauncherInstall install, string? version)
    {
        if (version is not { Length: > 0 }) return;
        if (install.Store == GameStore.Epic) PatchEpicItem(install.Record, version);
        lock (SettingsStore.Current) SettingsStore.Current.LauncherUpdates[install.Id] = new LauncherUpdateMark { Before = install.Version, Applied = version };
        SettingsStore.Save();
    }

    /// <summary>Epic's .item of the game says the new build (its other fields are kept).</summary>
    public static bool PatchEpicItem(string item, string version)
    {
        try
        {
            var r = JsonNode.Parse(File.ReadAllText(item))!;
            r["AppVersionString"] = version;
            var temp = item + ".hl-tmp";
            File.WriteAllText(temp, r.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, item, overwrite: true);
            Log.Info($"Epic manifest brought to {version}: {item}");
            return true;
        }
        catch (Exception ex) { Log.Warn($"Epic manifest update {item}: {ex.Message}"); return false; }
    }
}
