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

    /// <summary>GET a string with a timeout, served from a disk cache while it is younger than <paramref name="maxAge"/>.
    /// When the network fails, a stale cache entry is returned instead of an error.</summary>
    public static async Task<string?> GetCachedAsync(string url, string cacheName, TimeSpan maxAge, bool force = false,
        string? accept = null, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(AppInfo.CacheDir, cacheName);
        if (!force && File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < maxAge)
            return await File.ReadAllTextAsync(cacheFile, ct);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (accept is not null) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            using var resp = await Client.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var tmp = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, text, ct);
            File.Move(tmp, cacheFile, overwrite: true);
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"GET {url} failed: {ex.Message}");
            return File.Exists(cacheFile) ? await File.ReadAllTextAsync(cacheFile, ct) : null;
        }
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
