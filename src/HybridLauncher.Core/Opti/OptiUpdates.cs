namespace HybridLauncher.Core.Opti;

/// <summary>
/// New OptiScaler versions, per source: the official stable and nightly channels and each fork are followed apart ("stable",
/// "nightly", "fork:owner/name"). A game whose install is older than its source's newest release shows Update available; the
/// sources the user chose to be notified about are looked at in the background and a new release raises one notification.
/// </summary>
public static class OptiUpdates
{
    public static string Key(OptiChannel channel, string? repo) => channel switch
    {
        OptiChannel.Nightly => "nightly",
        OptiChannel.Fork when repo is not null => "fork:" + repo.ToLowerInvariant(),
        OptiChannel.Fork => "",
        OptiChannel.Custom => "",
        _ => "stable",
    };

    /// <summary>The source of an installed build (empty for a custom build: it has nowhere to come from).</summary>
    public static string Key(InstallManifest m) => Key(m.Channel, m.Source);

    /// <summary>"Stable", "Nightly" or the fork's name, for a notification.</summary>
    public static string Name(string key, AppSettings settings)
    {
        if (key == "stable") return "Stable";
        if (key == "nightly") return "Nightly";
        var repo = key.StartsWith("fork:", StringComparison.Ordinal) ? key[5..] : key;
        return OptiForks.All(settings).FirstOrDefault(f => f.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)) is { } f && f.Name.Length > 0 ? f.Name : repo;
    }

    /// <summary>The releases of a source, newest first.</summary>
    public static Task<List<OptiRelease>> ReleasesAsync(string key, bool force, CancellationToken ct) => key switch
    {
        "stable" => OptiReleases.GetAsync(OptiChannel.Stable, force, ct),
        "nightly" => OptiReleases.GetAsync(OptiChannel.Nightly, force, ct),
        _ when key.StartsWith("fork:", StringComparison.Ordinal) => OptiReleases.GetForkAsync(key[5..], force, ct),
        _ => Task.FromResult(new List<OptiRelease>()),
    };

    /// <summary>
    /// The release an install could update to, or null when it is the newest one. An install whose tag is no longer listed counts
    /// as older only when the newest release came out after it was installed (a build newer than the list is left alone).
    /// </summary>
    public static OptiRelease? NewerThan(InstallManifest m, IReadOnlyList<OptiRelease> releases)
    {
        if (releases.Count == 0 || Key(m).Length == 0) return null;
        var latest = releases[0];
        int at = -1;
        for (int i = 0; i < releases.Count; i++)
            if (releases[i].Tag.Equals(m.Tag, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
        if (at == 0) return null;
        if (at > 0) return latest;
        return latest.Published.ToUniversalTime() > m.InstalledAt.ToUniversalTime() ? latest : null;
    }

    public static bool IsFollowed(AppSettings s, string key) => key.Length > 0 && s.OptiFollow.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns notifications for a source on or off. Turned on, its newest release now counts as seen: only a later one notifies.
    /// </summary>
    public static async Task SetFollowedAsync(AppSettings s, string key, bool on, CancellationToken ct)
    {
        if (key.Length == 0) return;
        s.OptiFollow.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (!on) { s.OptiSeen.Remove(key); return; }
        s.OptiFollow.Add(key);
        try
        {
            var list = await ReleasesAsync(key, false, ct);
            if (list.Count > 0) s.OptiSeen[key] = list[0].Tag;
        }
        catch (Exception ex) { Log.Warn($"OptiScaler updates, {key}: {ex.Message}"); }
    }

    /// <summary>
    /// Looks at every followed source once and returns the new releases not notified yet (each is then marked as seen). A source
    /// seen for the first time is only remembered, never notified.
    /// </summary>
    public static async Task<List<(string Key, OptiRelease Release)>> CheckAsync(AppSettings s, CancellationToken ct)
    {
        var found = new List<(string, OptiRelease)>();
        foreach (var key in s.OptiFollow.ToList())
        {
            try
            {
                var list = await ReleasesAsync(key, false, ct);
                if (list.Count == 0) continue;
                var latest = list[0];
                if (s.OptiSeen.TryGetValue(key, out var seen) && !seen.Equals(latest.Tag, StringComparison.OrdinalIgnoreCase))
                    found.Add((key, latest));
                s.OptiSeen[key] = latest.Tag;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"OptiScaler updates, {key}: {ex.Message}"); }
        }
        return found;
    }
}
