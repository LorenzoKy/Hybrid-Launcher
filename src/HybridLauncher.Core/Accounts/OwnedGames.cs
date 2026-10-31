using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Accounts;

/// <summary>A game in a launcher account, installed or not. <see cref="Id"/> matches the installed game's id ("steam:1091500").</summary>
public sealed record OwnedGame(GameStore Store, string Id, string Name, string? Cover, string? Wide, string InstallUri, string? PageUri)
{
    public string Key => Id;

    /// <summary>Epic only: where the game is in the catalog (needed to download it).</summary>
    public string? Namespace { get; init; }
    public string? CatalogItemId { get; init; }
}

/// <summary>Where the list of one launcher came from, for the Downloads tab ("your account", "Steam on this PC"...), and what went wrong.</summary>
public sealed record OwnedList(GameStore Store, List<OwnedGame> Games, string Source, string? Problem = null, string? Account = null);

/// <summary>
/// The games each launcher account owns:
///  • signed in to Hybrid Launcher (Downloads → Accounts): the account's full library, from the store itself (Steam's licences, Epic's
///    library service, GOG's account API); Hybrid Launcher can then download and uninstall them itself;
///  • not signed in: what the launcher keeps on this PC (Steam's app cache, the Epic Games Launcher's catalog cache, GOG
///    Galaxy's database), and installing opens the launcher's own install page.
///  • Xbox: Microsoft keeps the library online only; installs go through the Xbox app.
/// </summary>
public static class OwnedGames
{
    public static Task<OwnedList> LoadAsync(GameStore store, LinkedAccounts accounts, CancellationToken ct) => store switch
    {
        GameStore.Steam => SteamAsync(accounts, ct),
        GameStore.Epic => OnlineOr(GameStore.Epic, EpicAccount.SignedIn, EpicAccount.LibraryAsync, Epic, accounts.EpicName, ct),
        GameStore.Gog => OnlineOr(GameStore.Gog, GogAccount.SignedIn, GogAccount.LibraryAsync, Gog, accounts.GogName, ct),
        _ => Task.FromResult(new OwnedList(store, [], "Xbox app", L.T("Microsoft keeps the Xbox / Game Pass library online only: install games from the Xbox app. Hybrid Launcher lists them once installed."))),
    };

    /// <summary>Whether Hybrid Launcher downloads this store's games itself (its account is signed in).</summary>
    public static bool Direct(GameStore store) => store switch
    {
        GameStore.Steam => SteamAccount.SignedIn,
        GameStore.Epic => EpicAccount.SignedIn,
        GameStore.Gog => GogAccount.SignedIn,
        _ => false,
    };

    /// <summary>The signed-in account's library; the launcher's copy on this PC when not signed in, or when the store does not answer.</summary>
    static async Task<OwnedList> OnlineOr(GameStore store, bool signedIn, Func<CancellationToken, Task<List<OwnedGame>>> online, Func<OwnedList> local, string? who, CancellationToken ct)
    {
        if (signedIn)
        {
            try
            {
                var games = await online(ct);
                if (games.Count > 0) return new OwnedList(store, games, L.T("your account"), null, who);
                // An empty account library while the launcher on this PC knows games means the store's answer went wrong
                // somewhere (a service refusing the request): show the launcher's games rather than none.
                var copy = await Task.Run(local, ct);
                if (copy.Games.Count == 0) return new OwnedList(store, games, L.T("your account"), null, who);
                Log.Warn($"{store} library: the account listed no games, the launcher on this PC lists {copy.Games.Count}");
                return copy with { Problem = L.T("The account's library came back empty: showing what the launcher keeps on this PC."), Account = who };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Warn($"{store} library: {ex.Message}");
                var fallback = await Task.Run(local, ct);
                return fallback with { Problem = L.F("The account's library did not load ({0}): showing what the launcher keeps on this PC.", ex.Message), Account = who };
            }
        }
        return await Task.Run(local, ct);
    }

    // ---------- Steam ----------

    /// <summary>The account signed in to Steam on this PC (loginusers.vdf, "MostRecent"): SteamID64 and name.</summary>
    public static (string Id, string Name)? SteamLogin()
    {
        try
        {
            var root = SteamScanner.SteamRoot();
            var file = root is null ? null : Path.Combine(root, "config", "loginusers.vdf");
            if (file is null || !File.Exists(file)) return null;
            var users = VdfNode.Parse(File.ReadAllText(file)).Child("users");
            if (users is null) return null;
            var pick = users.Children.FirstOrDefault(u => u.Value["MostRecent"] == "1");
            if (pick.Key is null) pick = users.Children.FirstOrDefault();
            return pick.Key is null ? null : (pick.Key, pick.Value["PersonaName"] ?? pick.Value["AccountName"] ?? pick.Key);
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam login: {ex.Message}");
            return null;
        }
    }

    static OwnedGame SteamGame(string appId, string name) => new(GameStore.Steam, $"steam:{appId}", name,
        $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
        $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
        $"steam://install/{appId}", $"https://store.steampowered.com/app/{appId}");

    static async Task<OwnedList> SteamAsync(LinkedAccounts a, CancellationToken ct)
    {
        if (SteamAccount.SignedIn)
            return await OnlineOr(GameStore.Steam, true, SteamAccount.LibraryAsync, SteamLocal, a.SteamName, ct);
        var login = SteamLogin();
        var fromPc = await Task.Run(SteamLocal, ct);
        return login is null ? fromPc with { Problem = L.T("Nobody is signed in to Steam on this PC.") } : fromPc with { Account = login.Value.Name };
    }

    /// <summary>
    /// Not signed in: the games in the Steam client's app cache (appinfo.vdf) that also have library art cached
    /// (appcache\librarycache\&lt;appid&gt;), which Steam keeps for the games in your library.
    /// </summary>
    static OwnedList SteamLocal()
    {
        var root = SteamScanner.SteamRoot();
        if (root is null) return new OwnedList(GameStore.Steam, [], "Steam", L.T("Steam is not installed on this PC."));
        try
        {
            var library = Directory.Exists(Path.Combine(root, "appcache", "librarycache"))
                ? Directory.GetDirectories(Path.Combine(root, "appcache", "librarycache")).Select(Path.GetFileName).Where(n => n is not null && n.All(char.IsDigit)).ToHashSet()!
                : new HashSet<string?>();
            var apps = Steam.SteamAppCache.ReadGames(Path.Combine(root, "appcache", "appinfo.vdf"));
            var list = apps.Where(a => library.Contains(a.AppId)).Select(a => SteamGame(a.AppId, a.Name))
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new OwnedList(GameStore.Steam, list, L.T("Steam on this PC"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam local library: {ex.Message}");
            return new OwnedList(GameStore.Steam, [], L.T("Steam on this PC"), L.F("Steam's app cache could not be read: {0}", ex.Message));
        }
    }

    // ---------- Epic ----------

    /// <summary>The Epic Games Launcher's catalog cache on this PC (also names games the online catalog does not answer for).</summary>
    internal static OwnedList EpicLocal() => Epic();

    static OwnedList Epic()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
        if (!File.Exists(file)) return new OwnedList(GameStore.Epic, [], "Epic Games Launcher", L.T("The Epic Games Launcher has no library cache on this PC: sign in to it once, then refresh."));
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(file).Trim()));
            using var doc = JsonDocument.Parse(json);
            var list = new List<OwnedGame>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                bool game = item.TryGetProperty("categories", out var cats) && cats.EnumerateArray().Any(c => c.TryGetProperty("path", out var p) && p.GetString() == "games");
                bool isDlc = item.TryGetProperty("mainGameItem", out var main) && main.ValueKind == JsonValueKind.Object && main.TryGetProperty("id", out var mid) && mid.GetString() is { Length: > 0 };
                if (!game || isDlc) continue;
                string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var title = S(item, "title");
                var ns = S(item, "namespace");
                var catalogId = S(item, "id");
                string? appName = item.TryGetProperty("releaseInfo", out var rel) && rel.ValueKind == JsonValueKind.Array && rel.GetArrayLength() > 0 ? S(rel[0], "appId") : null;
                if (title is null || ns is null || catalogId is null || appName is null) continue;
                string? Image(string type) => item.TryGetProperty("keyImages", out var imgs)
                    ? imgs.EnumerateArray().Where(i => S(i, "type") == type).Select(i => S(i, "url")).FirstOrDefault() : null;
                list.Add(new OwnedGame(GameStore.Epic, $"epic:{appName}", title.Replace("™", "").Replace("®", "").Trim(),
                    Image("DieselGameBoxTall"), Image("DieselGameBox"),
                    $"com.epicgames.launcher://apps/{ns}%3A{catalogId}%3A{appName}?action=install",
                    $"https://store.epicgames.com/browse?q={Uri.EscapeDataString(title)}"));
            }
            return new OwnedList(GameStore.Epic, list.DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                L.F("Epic Games Launcher (updated {0:d MMM yyyy})", File.GetLastWriteTime(file)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Epic catalog cache: {ex.Message}");
            return new OwnedList(GameStore.Epic, [], "Epic Games Launcher", L.F("The Epic library cache could not be read: {0}", ex.Message));
        }
    }

    // ---------- GOG ----------

    static OwnedList Gog()
    {
        var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
        if (!File.Exists(db)) return new OwnedList(GameStore.Gog, [], "GOG Galaxy", L.T("GOG Galaxy is not installed on this PC (its library lives in Galaxy)."));
        // Galaxy keeps the database open: read a copy (with its write-ahead log), never the original.
        var copy = Path.Combine(AppInfo.CacheDir, "gog-library.db");
        try
        {
            File.Copy(db, copy, overwrite: true);
            foreach (var ext in new[] { "-wal", "-shm" })
                if (File.Exists(db + ext)) File.Copy(db + ext, copy + ext, overwrite: true);
                else if (File.Exists(copy + ext)) File.Delete(copy + ext);

            var rows = Sqlite.Query(copy,
                "SELECT lr.releaseKey, " +
                " (SELECT value FROM GamePieces gp JOIN GamePieceTypes t ON t.id = gp.gamePieceTypeId WHERE gp.releaseKey = lr.releaseKey AND t.type = 'title' LIMIT 1), " +
                " (SELECT value FROM GamePieces gp JOIN GamePieceTypes t ON t.id = gp.gamePieceTypeId WHERE gp.releaseKey = lr.releaseKey AND t.type = 'originalImages' LIMIT 1) " +
                "FROM LibraryReleases lr WHERE lr.releaseKey LIKE 'gog_%'", 3);
            var list = new List<OwnedGame>();
            foreach (var r in rows)
            {
                var id = r[0]?["gog_".Length..];
                if (id is null || r[1] is null) continue;
                string? title = null, cover = null, wide = null;
                try { using var t = JsonDocument.Parse(r[1]!); title = t.RootElement.TryGetProperty("title", out var v) ? v.GetString() : null; } catch { }
                try
                {
                    if (r[2] is { } imgs)
                    {
                        using var i = JsonDocument.Parse(imgs);
                        cover = i.RootElement.TryGetProperty("verticalCover", out var c) ? c.GetString() : null;
                        wide = i.RootElement.TryGetProperty("background", out var b) ? b.GetString() : null;
                    }
                }
                catch { }
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new OwnedGame(GameStore.Gog, $"gog:{id}", title!.Replace("™", "").Replace("®", "").Trim(), cover, wide,
                    $"goggalaxy://openGameView/{id}", $"https://www.gog.com/en/games?query={Uri.EscapeDataString(title)}"));
            }
            return new OwnedList(GameStore.Gog, list.DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), "GOG Galaxy");
        }
        catch (Exception ex)
        {
            Log.Warn($"GOG Galaxy library: {ex.Message}");
            return new OwnedList(GameStore.Gog, [], "GOG Galaxy", L.F("GOG Galaxy's library could not be read: {0}", ex.Message));
        }
    }
}

/// <summary>Read-only queries through Windows' own SQLite (winsqlite3.dll, part of Windows 10 and 11): no extra library.</summary>
static class Sqlite
{
    const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_OPEN_READONLY = 1;

    public static List<string?[]> Query(string file, string sql, int columns)
    {
        var rows = new List<string?[]>();
        if (sqlite3_open_v2(file, out var db, SQLITE_OPEN_READONLY, 0) != SQLITE_OK)
            throw new IOException("SQLite could not open the database.");
        try
        {
            if (sqlite3_prepare16_v2(db, sql, -1, out var stmt, 0) != SQLITE_OK)
                throw new IOException("SQLite: " + Marshal.PtrToStringUni(sqlite3_errmsg16(db)));
            try
            {
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var row = new string?[columns];
                    for (int c = 0; c < columns; c++)
                    {
                        var p = sqlite3_column_text16(stmt, c);
                        row[c] = p == 0 ? null : Marshal.PtrToStringUni(p);
                    }
                    rows.Add(row);
                }
            }
            finally { sqlite3_finalize(stmt); }
        }
        finally { sqlite3_close(db); }
        return rows;
    }

    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(nint db, string sql, int bytes, out nint stmt, nint tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(nint stmt);
    [DllImport("winsqlite3.dll")] static extern nint sqlite3_column_text16(nint stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(nint stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close(nint db);
    [DllImport("winsqlite3.dll")] static extern nint sqlite3_errmsg16(nint db);
}
