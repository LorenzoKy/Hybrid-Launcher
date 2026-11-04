using System.Net;
using System.Net.Http.Headers;

namespace HybridLauncher.Core.Net;

public static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return client;
    }

    /// <summary>When GitHub's API limit (60 requests an hour without sign-in) ran out: the time it resets, else null.
    /// Until then requests to api.github.com are not sent: each would fail and push the reset no further.</summary>
    public static DateTime? GitHubLimitedUntil
    {
        get { var t = _gitHubLimitedUntil; return t is { } u && u > DateTime.UtcNow ? u : null; }
    }

    static DateTime? _gitHubLimitedUntil;

    /// <summary>Raised (off the UI thread) when GitHub's limit runs out: the app offers to sign in to GitHub, which lifts it.</summary>
    public static event Action<DateTime>? GitHubLimitReached;

    /// <summary>A new sign-in (or sign-out) comes with its own limit: requests may go again.</summary>
    public static void ResetGitHubLimit() => _gitHubLimitedUntil = null;

    // One request per address at a time: pages opened together (or a refresh while a load runs) share it.
    static readonly Dictionary<string, Task<string?>> InFlight = [];

    /// <summary>GET a string with a timeout, served from a disk cache while it is younger than <paramref name="maxAge"/>.
    /// When the network fails, a stale cache entry is returned instead of an error.</summary>
    public static async Task<string?> GetCachedAsync(string url, string cacheName, TimeSpan maxAge, bool force = false,
        string? accept = null, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(AppInfo.CacheDir, cacheName);
        if (!force && File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < maxAge)
            return await File.ReadAllTextAsync(cacheFile, ct);
        if (IsGitHubApi(url) && GitHubLimitedUntil is not null)
            return File.Exists(cacheFile) ? await File.ReadAllTextAsync(cacheFile, ct) : null;

        Task<string?> task;
        lock (InFlight)
        {
            if (!InFlight.TryGetValue(url, out task!))
            {
                // Not tied to this caller's token: another caller may still want the answer when this one leaves.
                task = FetchAsync(url, cacheFile, accept);
                InFlight[url] = task;
                _ = task.ContinueWith(_ => { lock (InFlight) InFlight.Remove(url); }, TaskScheduler.Default);
            }
        }
        var text = await task.WaitAsync(ct);
        if (text is not null) return text;
        // Another caller's request may have filled a different cache name for the same address.
        return File.Exists(cacheFile) ? await File.ReadAllTextAsync(cacheFile, ct) : null;
    }

    static bool IsGitHubApi(string url) => url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase);

    static async Task<string?> FetchAsync(string url, string cacheFile, string? accept)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (accept is not null) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            // Signed in to GitHub: 5000 requests an hour instead of 60. The token goes to api.github.com only.
            var token = IsGitHubApi(url) ? GitHubAuth.Token : null;
            if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await Client.SendAsync(req, cts.Token);
            if (token is not null && resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Revoked or expired: dropped, and the request goes again without it.
                resp.Dispose();
                GitHubAuth.TokenRejected(token);
                using var again = new HttpRequestMessage(HttpMethod.Get, url);
                if (accept is not null) again.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                resp = await Client.SendAsync(again, cts.Token);
            }
            using var _ = resp;
            if (IsGitHubApi(url)) NoteGitHubLimit(resp);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var tmp = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, text);
            File.Move(tmp, cacheFile, overwrite: true);
            return text;
        }
        catch (Exception ex)
        {
            Log.Warn($"GET {url} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>A 403 or 429 with no requests left: GitHub says in x-ratelimit-reset (Unix seconds) when it lets more through.</summary>
    static void NoteGitHubLimit(HttpResponseMessage resp)
    {
        if (resp.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return;
        string? Header(string name) => resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        DateTime? until = null;
        if (Header("x-ratelimit-remaining") == "0" && long.TryParse(Header("x-ratelimit-reset"), out var reset))
            until = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime;
        else if (resp.Headers.RetryAfter?.Delta is { } wait)
            until = DateTime.UtcNow + wait;
        if (until is not { } u || u <= DateTime.UtcNow) return;
        bool fresh = _gitHubLimitedUntil is null || _gitHubLimitedUntil < DateTime.UtcNow;
        _gitHubLimitedUntil = u;
        if (!fresh) return;
        Log.Warn($"GitHub API limit reached ({(GitHubAuth.SignedIn ? "signed in" : "not signed in")}): no more requests to it until {u.ToLocalTime():HH:mm:ss}.");
        GitHubLimitReached?.Invoke(u);
    }

    /// <summary>Download a file to <paramref name="destination"/> through a .part file, reporting 0..1 progress.</summary>
    public static async Task DownloadFileAsync(string url, string destination, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        using (var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? expectedSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            var lastReport = 0L;
            while (true)
            {
                // A stalled connection must not hang forever.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                read = await src.ReadAsync(buffer, stall.Token);
                if (read == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0 && done - lastReport > total / 200)
                {
                    lastReport = done;
                    progress?.Report((double)done / total);
                }
            }
        }
        File.Move(part, destination, overwrite: true);
        progress?.Report(1);
    }
}

/// <summary>An IProgress that reports inline, in order. <see cref="Progress{T}"/> posts each report to the thread pool
/// when created off the UI thread, so nested reports can arrive after later stages.</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
