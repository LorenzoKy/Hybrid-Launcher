using System.Security.Cryptography;
using System.Text.Json;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Opti;

public sealed record OptiRelease(OptiChannel Channel, string Tag, string Title, DateTime Published, string AssetName, string AssetUrl, long Size, string? Sha256)
{
    /// <summary>The release notes as GitHub has them (Markdown), and the release's page.</summary>
    public string Notes { get; init; } = "";
    public string PageUrl { get; init; } = "";
    /// <summary>For a fork's release: its repository ("owner/name").</summary>
    public string? Repo { get; init; }

    public string Display => $"{Tag}  ·  {Published.ToLocalTime():yyyy-MM-dd}";
}

/// <summary>Reads OptiScaler releases from GitHub (stable: optiscaler/OptiScaler, nightly: optiscaler/OptiScaler-nightly).</summary>
public static class OptiReleases
{
    public static string Repo(OptiChannel channel) => channel == OptiChannel.Stable ? "optiscaler/OptiScaler" : "optiscaler/OptiScaler-nightly";
    public static string ReleasesPage(OptiChannel channel) => $"https://github.com/{Repo(channel)}/releases";

    public static Task<List<OptiRelease>> GetAsync(OptiChannel channel, bool force = false, CancellationToken ct = default) =>
        GetAsync(channel, Repo(channel), force, ct);

    /// <summary>The releases of a fork's repository ("owner/name").</summary>
    public static Task<List<OptiRelease>> GetForkAsync(string repo, bool force = false, CancellationToken ct = default) =>
        GetAsync(OptiChannel.Fork, repo, force, ct);

    public static string ForkPage(string repo) => $"https://github.com/{repo}/releases";

    /// <summary>
    /// A source's list came back with a newer release than the last one read in this session (the background update check found
    /// it, say): pages showing that source's versions take the new list as it is, with no request of their own. Raised on the
    /// thread that read it; the repository is set for a fork.
    /// </summary>
    public static event Action<OptiChannel, string?, List<OptiRelease>>? Updated;

    static readonly Dictionary<string, string> NewestRead = new(StringComparer.OrdinalIgnoreCase);

    static async Task<List<OptiRelease>> GetAsync(OptiChannel channel, string repo, bool force, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{repo.ToLowerInvariant()}/releases?per_page=30";
        // GitHub names are case-insensitive: one cache (and one request) per fork, however its name was written. The update
        // checks ask with the lower-case key, the OptiScaler tab with the name as listed: both used to fetch it apart.
        var cache = channel == OptiChannel.Fork ? $"releases-fork-{Safe(repo).ToLowerInvariant()}.json" : $"releases-{channel.ToString().ToLowerInvariant()}.json";
        var json = await Http.GetCachedAsync(url, cache, TimeSpan.FromMinutes(30), force, "application/vnd.github+json", ct);
        if (json is null) throw new InvalidOperationException(Http.GitHubLimitedUntil is { } until
            ? L.F("GitHub's limit of requests without sign-in is used up until {0:HH:mm}, and these releases are not cached yet.", until.ToLocalTime())
            : L.T("Could not reach GitHub to list OptiScaler releases (and nothing is cached yet)."));

        var list = new List<OptiRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var title = r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
            var published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : DateTime.MinValue;

            // The archive of the build: forks put extras beside it (an optional forwarder, a troubleshooting zip), so one named
            // after OptiScaler comes first, the others only when none is.
            var archives = r.GetProperty("assets").EnumerateArray()
                .Where(e => e.GetProperty("name").GetString() is { } file && (file.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            static int Rank(string asset) =>
                Extra.Any(x => asset.Contains(x, StringComparison.OrdinalIgnoreCase)) ? 2 : asset.Contains("optiscaler", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            var pick = archives.OrderBy(e => Rank(e.GetProperty("name").GetString()!)).Cast<JsonElement?>().FirstOrDefault();
            if (pick is not { } a) continue;
            var name = a.GetProperty("name").GetString() ?? "";
            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = digest[7..].ToLowerInvariant();
            list.Add(new OptiRelease(channel, tag, title, published, name,
                a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha)
            {
                Notes = r.TryGetProperty("body", out var body) ? (body.GetString() ?? "").Replace("﻿", "") : "",
                PageUrl = r.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                Repo = channel == OptiChannel.Fork ? repo : null,
            });
        }
        var sorted = list.OrderByDescending(r => r.Published).ToList();
        if (sorted.Count > 0)
        {
            bool newer;
            lock (NewestRead)
            {
                newer = NewestRead.TryGetValue(cache, out var before) && !before.Equals(sorted[0].Tag, StringComparison.OrdinalIgnoreCase);
                NewestRead[cache] = sorted[0].Tag;
            }
            if (newer)
            {
                try { Updated?.Invoke(channel, channel == OptiChannel.Fork ? repo : null, sorted); }
                catch (Exception ex) { Log.Warn($"Releases updated, {repo}: {ex.Message}"); }
            }
        }
        return sorted;
    }

    /// <summary>Downloads (or reuses a verified cached copy of) a release archive.</summary>
    public static async Task<string> DownloadAsync(OptiRelease release, IProgress<double>? progress, CancellationToken ct)
    {
        var dest = Path.Combine(AppInfo.DownloadsDir, release.Channel.ToString().ToLowerInvariant(), FolderName(release), release.AssetName);
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

    /// <summary>The folder a release is kept in: its tag, or for a fork its repository and tag (two forks can share a tag).</summary>
    public static string FolderName(OptiRelease r) => Safe(r.Repo is null ? r.Tag : $"{r.Repo.Replace('/', '_')}_{r.Tag}");

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

    // Archives beside a build that are not the build.
    static readonly string[] Extra = ["optional", "forwarder", "troubleshoot", "source", "symbols", "pdb"];

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
