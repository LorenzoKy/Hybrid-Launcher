using System.Runtime.ExceptionServices;

namespace HybridLauncher.Core.Cloud;

/// <summary>
/// Several clouds at once (OneDrive and MEGA, say): every upload goes to each of them, and the saves come from whichever holds
/// the newest copy. A cloud that could not be reached keeps its copy and catches up at the next sync; only when none answers
/// does a call fail. An older copy's id carries the index of the cloud it belongs to ("1|…").
/// </summary>
public sealed class MirrorStore(IReadOnlyList<ICloudStore> stores) : ICloudStore
{
    public IReadOnlyList<ICloudStore> Stores => stores;

    public string Name => string.Join(" + ", stores.Select(s => s.Name));

    sealed record Answer(int Index, ICloudStore Store, CloudManifest? Manifest, Exception? Error);

    /// <summary>How long the other clouds get once one has answered: a cloud that hangs does not hold the ones that work.</summary>
    public static TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Clouds found silent, by name, until when nobody waits for them again (Play asks the clouds two or three times in a row:
    /// each must not wait the patience again). A cloud that answers meanwhile is waited for again at once.
    /// </summary>
    static readonly Dictionary<string, DateTime> Silent = new(StringComparer.OrdinalIgnoreCase);

    static bool IsSilent(ICloudStore s)
    {
        lock (Silent) return Silent.TryGetValue(s.Name, out var until) && DateTime.UtcNow < until;
    }

    /// <summary>
    /// Every cloud's manifest of the game, asked at once; a cloud still silent <see cref="Patience"/> after another answered
    /// counts as not answering (its call goes on and ends by itself). Throws only when none of them answered.
    /// </summary>
    async Task<List<Answer>> AskAllAsync(string key, CancellationToken ct)
    {
        var asks = stores.Select(async (s, i) =>
        {
            try
            {
                var answer = new Answer(i, s, await s.ManifestAsync(key, ct), null);
                lock (Silent) Silent.Remove(s.Name);
                return answer;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Warn($"Cloud: {s.Name} did not answer: {ex.Message}");
                return new Answer(i, s, null, ex);
            }
        }).ToList();
        // Waited for: the clouds not found silent lately (all of them when every one is).
        var awaited = asks.Where((_, i) => !IsSilent(stores[i])).ToList();
        if (awaited.Count == 0) awaited = asks;
        var all = Task.WhenAll(awaited);
        await Task.WhenAny(awaited);
        if (!all.IsCompleted) await Task.WhenAny(all, Task.Delay(Patience, ct));
        ct.ThrowIfCancellationRequested();
        var answers = asks.Select((t, i) => t.IsCompletedSuccessfully ? t.Result : Late(i)).ToList();
        if (answers.All(a => a.Error is not null)) ExceptionDispatchInfo.Throw(answers[0].Error!);
        return answers;

        Answer Late(int i)
        {
            bool known = IsSilent(stores[i]);
            lock (Silent) Silent[stores[i].Name] = DateTime.UtcNow.AddSeconds(60);
            if (!known) Log.Warn($"Cloud: {stores[i].Name} did not answer within {Patience.TotalSeconds:0} s, left for the next sync");
            return new Answer(i, stores[i], null, new TimeoutException(L.F("{0} did not answer in time.", stores[i].Name)));
        }
    }

    static IEnumerable<Answer> NewestFirst(List<Answer> answers) =>
        answers.Where(a => a.Manifest is not null).OrderByDescending(a => a.Manifest!.UpdatedUtc);

    /// <summary>
    /// The newest copy, marked <see cref="CloudManifest.Partial"/> when a cloud lacks it, or did not answer: "Synced" only when
    /// every cloud was seen holding it (a cloud that is down is not in sync, its upload goes again at the next sync).
    /// </summary>
    public async Task<CloudManifest?> ManifestAsync(string key, CancellationToken ct)
    {
        var answers = await AskAllAsync(key, ct);
        if (NewestFirst(answers).FirstOrDefault()?.Manifest is not { } newest) return null;
        newest.Partial = answers.Any(a => a.Error is not null || a.Manifest?.Hash != newest.Hash);
        return newest;
    }

    public async Task PutAsync(string key, string zip, CloudManifest manifest, CloudManifest? old, int keep, CancellationToken ct)
    {
        var answers = await AskAllAsync(key, ct);
        var todo = answers.Where(a => a.Error is null && a.Manifest?.Hash != manifest.Hash).ToList();
        var missed = answers.Where(a => a.Error is not null).Select(a => (a.Store.Name, a.Error!)).ToList();
        if (missed.Count > 0)
            Log.Info($"Cloud: {key} not sent to {string.Join(", ", missed.Select(m => m.Name))} (did not answer), left for the next sync");
        // To every cloud at once: a slow one (MEGA, a big save) does not hold the others.
        var errors = (await Task.WhenAll(todo.Select(async a =>
        {
            try { await a.Store.PutAsync(key, zip, manifest, a.Manifest, keep, ct); return ((string, Exception)?)null; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Warn($"Cloud: sending to {a.Store.Name} failed: {ex.Message}");
                return (a.Store.Name, ex);
            }
        }))).OfType<(string Name, Exception Error)>().ToList();
        if (todo.Count > 0 && errors.Count == todo.Count && missed.Count == 0) ExceptionDispatchInfo.Throw(errors[0].Error);
        missed.AddRange(errors);
        // Some clouds have the saves and others not: said to the user, the missing ones get them at the next sync.
        if (missed.Count > 0) throw new CloudPartialException(stores.Select(s => s.Name).Except(missed.Select(m => m.Name)).ToList(), missed);
    }

    public async Task GetAsync(string key, string? copyId, string toFile, CancellationToken ct)
    {
        if (copyId is not null)
        {
            var bar = copyId.IndexOf('|');
            await stores[int.Parse(copyId[..bar])].GetAsync(key, copyId[(bar + 1)..], toFile, ct);
            return;
        }
        Exception? last = null;
        foreach (var a in NewestFirst(await AskAllAsync(key, ct)))
            try { await a.Store.GetAsync(key, null, toFile, ct); return; }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Cloud: {a.Store.Name}: {ex.Message}"); last = ex; }
        if (last is not null) ExceptionDispatchInfo.Throw(last);
        throw new FileNotFoundException(L.T("The cloud has no saves for this game yet."));
    }

    /// <summary>The history of the cloud with the newest copy (the others hold the same uploads).</summary>
    public async Task<List<CloudCopy>> HistoryAsync(string key, CancellationToken ct)
    {
        foreach (var a in NewestFirst(await AskAllAsync(key, ct)))
            try { return (await a.Store.HistoryAsync(key, ct)).Select(c => c with { Id = $"{a.Index}|{c.Id}" }).ToList(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Cloud: {a.Store.Name}: {ex.Message}"); }
        return [];
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var errors = new List<Exception>();
        foreach (var s in stores)
            try { await s.DeleteAsync(key, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Cloud: removing from {s.Name} failed: {ex.Message}"); errors.Add(ex); }
        if (errors.Count == stores.Count) ExceptionDispatchInfo.Throw(errors[0]);
    }
}

/// <summary>An upload that reached some of the clouds only: the others did not answer or failed, and catch up at the next sync.</summary>
public sealed class CloudPartialException(IReadOnlyList<string> sent, IReadOnlyList<(string Name, Exception Error)> missed)
    : Exception(L.F("not in {0} yet: {1}", string.Join(", ", missed.Select(m => m.Name)), missed[0].Error.Message), missed[0].Error)
{
    /// <summary>The clouds that have the saves (none, when every one missed them).</summary>
    public IReadOnlyList<string> Sent => sent;
    public IReadOnlyList<(string Name, Exception Error)> Missed => missed;
}
