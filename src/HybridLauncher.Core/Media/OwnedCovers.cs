using HybridLauncher.Core.Accounts;

namespace HybridLauncher.Core.Media;

/// <summary>
/// The covers of the games owned on the linked launchers (Downloads and the library's Not installed), kept on disk (2.1).
/// Each cover was the launcher's picture address handed straight to the image control: one failed request (a busy CDN while
/// hundreds of covers load, a moment offline) left the cover empty until it was made again, and Steam games whose art moved
/// to a new address had none at all. Now the picture is downloaded once into the image cache, a few at a time, trying the
/// tall cover, then the wide one, then (Steam) the address the store itself gives, and the control shows the file.
/// </summary>
public static class OwnedCovers
{
    static readonly SemaphoreSlim Gate = new(8, 8);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> Running = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Missed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cover's file when this session already has it (checks disk so trimmed files are not returned stale).</summary>
    public static string? KnownPath(string gameId) => Known.TryGetValue(gameId, out var p) && File.Exists(p) ? p : null;

    /// <summary>The cover's file, downloaded when needed; null when no picture could be had (asked again next time).</summary>
    public static Task<string?> GetAsync(OwnedGame game)
    {
        if (Known.TryGetValue(game.Id, out var p))
        {
            if (File.Exists(p)) { CacheJanitor.Touch(p); return Task.FromResult<string?>(p); }
            Known.TryRemove(game.Id, out _);
        }
        // Found nowhere a short while ago: not asked again for every scroll past it.
        if (Missed.TryGetValue(game.Id, out var when) && DateTime.UtcNow - when < TimeSpan.FromMinutes(10)) return Task.FromResult<string?>(null);
        var task = Running.GetOrAdd(game.Id, _ => Task.Run(() => FetchAsync(game)));
        _ = task.ContinueWith(_ => Running.TryRemove(new KeyValuePair<string, Task<string?>>(game.Id, task)), TaskScheduler.Default);
        return task;
    }

    /// <summary>Drops the in-memory index of cached owned covers (e.g. after clearing the disk cache).</summary>
    public static void ForgetAll()
    {
        Known.Clear();
        Running.Clear();
        Missed.Clear();
    }

    static async Task<string?> FetchAsync(OwnedGame game)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var safe = string.Concat(game.Id.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ':' ? '_' : ch));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string? path = null;
            for (int attempt = 0; attempt < 2 && path is null; attempt++)
            {
                if (attempt > 0) await Task.Delay(1500, cts.Token).ConfigureAwait(false);
                if (game.Cover is { Length: > 0 } tall)
                    path = await GameInfo.CacheImageAsync(Small(tall), $"owned-{safe}-cover{Ext(tall)}", cts.Token).ConfigureAwait(false);
                // Steam: the store's own address for the cover (art updated since 2024 lives under a new folder).
                if (path is null && game.Store == GameStore.Steam && game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
                    path = await GameInfo.GetCoverAsync(new Game { Id = game.Id, Name = game.Name, Store = GameStore.Steam, InstallDir = "", SteamAppId = game.Id[6..] }, cts.Token).ConfigureAwait(false);
                if (path is null && game.Wide is { Length: > 0 } wide)
                    path = await GameInfo.CacheImageAsync(wide, $"owned-{safe}-wide{Ext(wide)}", cts.Token).ConfigureAwait(false);
                // No picture from its launcher (GOG's "… - Amazon Prime" copies): the same game's Steam cover, found by name.
                if (path is null && game.Store != GameStore.Steam && attempt == 0)
                    path = await GameInfo.GetCoverAsync(new Game { Id = game.Id, Name = game.Name, Store = game.Store, InstallDir = "" }, cts.Token).ConfigureAwait(false);
            }
            if (path is not null) Known[game.Id] = path;
            else Missed[game.Id] = DateTime.UtcNow;
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"Owned cover {game.Name}: {ex.Message}");
            return null;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Epic's pictures come at full size (often 2-3 MB): its image server sends them at cover size when asked.</summary>
    static string Small(string url) =>
        url.Contains(".epicgames.com/", StringComparison.OrdinalIgnoreCase) && !url.Contains('?') ? url + "?h=660&w=440&resize=1&quality=high" : url;

    static string Ext(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" ? ext : ".jpg";
        }
        catch (UriFormatException) { return ".jpg"; }
    }
}
