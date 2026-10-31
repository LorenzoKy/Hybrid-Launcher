using System.Security.Cryptography;
using System.Text.Json;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Opti;

public sealed record OptiRelease(OptiChannel Channel, string Tag, string Title, DateTime Published, string AssetName, string AssetUrl, long Size, string? Sha256)
{
    /// <summary>The release notes as GitHub has them (Markdown), and the release's page.</summary>
    public string Notes { get; init; } = "";
    public string PageUrl { get; init; } = "";

    public string Display => $"{Tag}  ·  {Published.ToLocalTime():yyyy-MM-dd}";
}

/// <summary>Reads OptiScaler releases from GitHub (stable: optiscaler/OptiScaler, nightly: optiscaler/OptiScaler-nightly).</summary>
public static class OptiReleases
{
    public static string Repo(OptiChannel channel) => channel == OptiChannel.Stable ? "optiscaler/OptiScaler" : "optiscaler/OptiScaler-nightly";
    public static string ReleasesPage(OptiChannel channel) => $"https://github.com/{Repo(channel)}/releases";

    public static async Task<List<OptiRelease>> GetAsync(OptiChannel channel, bool force = false, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{Repo(channel)}/releases?per_page=30";
        var json = await Http.GetCachedAsync(url, $"releases-{channel.ToString().ToLowerInvariant()}.json",
            TimeSpan.FromMinutes(30), force, "application/vnd.github+json", ct);
        if (json is null) throw new InvalidOperationException(L.T("Could not reach GitHub to list OptiScaler releases (and nothing is cached yet)."));

        var list = new List<OptiRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var title = r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
            var published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : DateTime.MinValue;

            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = null;
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..].ToLowerInvariant();
                list.Add(new OptiRelease(channel, tag, title, published, name,
                    a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha)
                {
                    Notes = r.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    PageUrl = r.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                });
                break;
            }
        }
        return list.OrderByDescending(r => r.Published).ToList();
    }

    /// <summary>Downloads (or reuses a verified cached copy of) a release archive.</summary>
    public static async Task<string> DownloadAsync(OptiRelease release, IProgress<double>? progress, CancellationToken ct)
    {
        var dest = Path.Combine(AppInfo.DownloadsDir, release.Channel.ToString().ToLowerInvariant(), Safe(release.Tag), release.AssetName);
        if (File.Exists(dest) && await VerifyAsync(dest, release, ct))
        {
            Log.Info($"Using cached {dest}");
            progress?.Report(1);
            return dest;
        }

        Log.Info($"Downloading {release.AssetUrl}");
        await Http.DownloadFileAsync(release.AssetUrl, dest, release.Size, progress, ct);
        if (!await VerifyAsync(dest, release, ct))
        {
            File.Delete(dest);
            throw new InvalidOperationException(L.F("The download of {0} is corrupted (size or SHA-256 mismatch). Try again.", release.AssetName));
        }
        PruneOldDownloads(release.Channel);
        return dest;
    }

    static async Task<bool> VerifyAsync(string file, OptiRelease release, CancellationToken ct)
    {
        var info = new FileInfo(file);
        if (release.Size > 0 && info.Length != release.Size) return false;
        if (release.Sha256 is null) return true;
        await using var s = File.OpenRead(file);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
        return hash == release.Sha256;
    }

    /// <summary>Keep only the 3 newest archives per channel (nightlies are ~55 MB each).</summary>
    static void PruneOldDownloads(OptiChannel channel)
    {
        try
        {
            var dir = Path.Combine(AppInfo.DownloadsDir, channel.ToString().ToLowerInvariant());
            foreach (var old in new DirectoryInfo(dir).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(3))
                old.Delete(recursive: true);
        }
        catch (Exception ex) { Log.Warn($"Pruning downloads failed: {ex.Message}"); }
    }

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
