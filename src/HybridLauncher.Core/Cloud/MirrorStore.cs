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

    /// <summary>Every cloud's manifest of the game, asked at once; throws only when none of them answered.</summary>
    async Task<List<Answer>> AskAllAsync(string key, CancellationToken ct)
    {
        var answers = await Task.WhenAll(stores.Select(async (s, i) =>
        {
            try { return new Answer(i, s, await s.ManifestAsync(key, ct), null); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Cloud: {s.Name} did not answer: {ex.Message}");
                return new Answer(i, s, null, ex);
            }
        }));
        if (answers.All(a => a.Error is not null)) ExceptionDispatchInfo.Throw(answers[0].Error!);
        return answers.ToList();
    }

    static IEnumerable<Answer> NewestFirst(List<Answer> answers) =>
        answers.Where(a => a.Manifest is not null).OrderByDescending(a => a.Manifest!.UpdatedUtc);

    /// <summary>The newest copy, marked <see cref="CloudManifest.Partial"/> when a cloud that answered lacks it.</summary>
    public async Task<CloudManifest?> ManifestAsync(string key, CancellationToken ct)
    {
        var answers = await AskAllAsync(key, ct);
        if (NewestFirst(answers).FirstOrDefault()?.Manifest is not { } newest) return null;
        newest.Partial = answers.Any(a => a.Error is null && a.Manifest?.Hash != newest.Hash);
        return newest;
    }

    public async Task PutAsync(string key, string zip, CloudManifest manifest, CloudManifest? old, int keep, CancellationToken ct)
    {
        var todo = (await AskAllAsync(key, ct)).Where(a => a.Error is null && a.Manifest?.Hash != manifest.Hash).ToList();
        var errors = new List<Exception>();
        foreach (var a in todo)
            try { await a.Store.PutAsync(key, zip, manifest, a.Manifest, keep, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Cloud: sending to {a.Store.Name} failed: {ex.Message}");
                errors.Add(ex);
            }
        if (todo.Count > 0 && errors.Count == todo.Count) ExceptionDispatchInfo.Throw(errors[0]);
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
