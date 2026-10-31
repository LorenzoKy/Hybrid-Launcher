using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HybridLauncher.Core.Accounts;

/// <summary>A piece of a file: where it goes and how to get its bytes (already checked and unpacked by the store's client).</summary>
public sealed record PlanChunk(long Offset, int Size, Func<CancellationToken, Task<byte[]>> Fetch);

/// <summary>A file of the game: its chunks, or a slice of another file (GOG packs small files into one container).</summary>
public sealed class PlanFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public bool Directory { get; init; }
    public List<PlanChunk> Chunks { get; init; } = [];
    /// <summary>Copied out of this other file of the plan (downloaded first), at this offset; the container is deleted after.</summary>
    public (string Container, long Offset)? Slice { get; init; }
    /// <summary>A helper file (a container): downloaded, used, then deleted.</summary>
    public bool Temporary { get; init; }
    /// <summary>The whole file's checksum from the store's manifest (hex), when it gives one: an update keeps a file that matches.</summary>
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
}

/// <summary>Everything to download for one game, and how it starts once it is there.</summary>
public sealed class DownloadPlan
{
    /// <summary>The game's folder; the install dialog may move it to another install folder before the download starts.</summary>
    public required string InstallDir { get; set; }
    public List<PlanFile> Files { get; init; } = [];
    public string? Exe { get; set; }
    public string? Args { get; set; }
    public string? WorkingDir { get; set; }
    public string? Version { get; init; }
    /// <summary>An update of a game already in <see cref="InstallDir"/>: files already as the new build has them are not downloaded again.</summary>
    public bool Update { get; set; }
    /// <summary>Run after the files are in place (reads the store's launch info that came with the files, writes extra files).</summary>
    public Action<DownloadPlan>? Finish { get; init; }
    public long TotalBytes => Files.Where(f => !f.Directory && f.Slice is null).Sum(f => f.Size);
}

/// <summary>
/// Downloads a plan into its folder: chunks in parallel, each written at its place in the file (no temp copies), several
/// tries per chunk. Files already completed, and the chunks written so far of the ones still going ("path|offset"), are listed
/// in ".hl-download.json" in the folder, so a paused or broken download goes on where it stopped, even inside a big file;
/// the list is deleted once the game is complete.
/// </summary>
public static class DirectDownloader
{
    const int Parallel = 8;
    public const string StateFile = ".hl-download.json";
    /// <summary>The same list, from a download paused before one of the app's renames: picked up and renamed.</summary>
    public static readonly string[] LegacyStateFiles =
        ["." + AppInfo.PreviousShort + "-download.json", "." + AppInfo.LegacyName.ToLowerInvariant() + "-download.json"];

    /// <summary>Whether a download of this app is unfinished in <paramref name="folder"/> (its list of done files is there).</summary>
    public static bool HasState(string folder) => LegacyStateFiles.Prepend(StateFile).Any(f => File.Exists(Path.Combine(folder, f)));

    public static void DeleteState(string folder)
    {
        foreach (var f in LegacyStateFiles.Prepend(StateFile))
            try { File.Delete(Path.Combine(folder, f)); } catch { }
    }

    public static async Task RunAsync(DownloadPlan plan, Action<long> progress, CancellationToken ct)
    {
        var root = plan.InstallDir;
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, StateFile);
        if (!File.Exists(statePath) && LegacyStateFiles.Select(f => Path.Combine(root, f)).FirstOrDefault(File.Exists) is { } legacyState)
            File.Move(legacyState, statePath);
        var done = ReadState(statePath);
        // The build the list was written for: after a game update the old pieces must not be mixed with the new ones.
        var build = "#build:" + (plan.Version ?? "");
        if (!done.Contains(build))
        {
            // A list from before the marker (Hybrid Launcher 0.14) is kept as it is.
            if (done.Any(d => d.StartsWith("#build:", StringComparison.Ordinal)))
            {
                Log.Info($"Download state of {root}: written for another build, starting over");
                done.Clear();
            }
            done.Add(build);
        }
        long bytes = 0;
        var gate = new SemaphoreSlim(Parallel);
        var lastSave = DateTime.UtcNow;
        var stateLock = new object();

        static string ChunkKey(PlanFile f, PlanChunk c) => $"{f.Path}|{c.Offset}";

        // A finished file replaces its chunks in the list (a big game has tens of thousands of chunks).
        void Saved(string path, PlanFile? whole = null)
        {
            lock (stateLock)
            {
                if (whole is not null) foreach (var c in whole.Chunks) done.Remove(ChunkKey(whole, c));
                done.Add(path);
                if (DateTime.UtcNow - lastSave < TimeSpan.FromSeconds(3)) return;
                lastSave = DateTime.UtcNow;
                WriteState(statePath, done);
            }
        }

        foreach (var dir in plan.Files.Where(f => f.Directory)) Directory.CreateDirectory(Full(root, dir.Path));

        // Containers and plain files first; slices of the containers after.
        var files = plan.Files.Where(f => !f.Directory && f.Slice is null).ToList();
        // What is on disk already: whole files, and the chunks written of the files still going (only while the file is there).
        var partial = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (!File.Exists(Full(root, f.Path))) continue;
            if (done.Contains(f.Path)) { bytes += f.Size; continue; }
            foreach (var c in f.Chunks)
                if (done.Contains(ChunkKey(f, c)))
                {
                    partial.Add(ChunkKey(f, c));
                    bytes += c.Size;
                }
        }
        progress(bytes);

        // An update, like Steam's: a file already as the new build has it (same size, same checksum) is kept; only the files
        // that changed are downloaded. Files the build does not list (mods, settings, saves) are never touched.
        if (plan.Update)
        {
            int kept = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (done.Contains(f.Path) || f.Chunks.Any(c => partial.Contains(ChunkKey(f, c)))) continue;
                if (!Same(Full(root, f.Path), f)) continue;
                done.Add(f.Path);
                bytes += f.Size;
                kept++;
                progress(bytes);
            }
            lock (stateLock) WriteState(statePath, done);
            Log.Info($"Update of {root}: {kept} of {files.Count} files already up to date");
        }
        var tasks = new List<Task>();
        var handles = new List<SafeFileHandle>();
        try
        {
        foreach (var file in files)
        {
            if (done.Contains(file.Path) && File.Exists(Full(root, file.Path))) continue;
            ct.ThrowIfCancellationRequested();
            var path = Full(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (file.Chunks.Count == 0)
            {
                await File.WriteAllBytesAsync(path, [], ct);
                Saved(file.Path);
                continue;
            }
            var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
            handles.Add(handle);
            RandomAccess.SetLength(handle, file.Size);
            var missing = file.Chunks.Where(c => !partial.Contains(ChunkKey(file, c))).ToList();
            int left = missing.Count;
            if (left == 0)
            {
                // Every chunk was written before the pause; only the file's own line was missing.
                handle.Dispose();
                Saved(file.Path, file);
                continue;
            }
            foreach (var chunk in missing)
            {
                await gate.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var data = await FetchWithRetries(chunk, ct);
                        await RandomAccess.WriteAsync(handle, data, chunk.Offset, ct);
                        Interlocked.Add(ref bytes, data.Length);
                        progress(Interlocked.Read(ref bytes));
                        if (Interlocked.Decrement(ref left) == 0)
                        {
                            handle.Dispose();
                            Saved(file.Path, file);
                        }
                        else Saved(ChunkKey(file, chunk));
                    }
                    finally { gate.Release(); }
                }, ct));
            }
            // Finished tasks are dropped as we go: a game has tens of thousands of chunks.
            if (tasks.Count > 256)
            {
                await Task.WhenAll(tasks.Where(t => t.IsCompleted));
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }
        await Task.WhenAll(tasks);
        }
        catch
        {
            // Whatever finished is kept: the next try (Resume) goes on from there.
            try { await Task.WhenAll(tasks); } catch { }
            lock (stateLock) WriteState(statePath, done);
            throw;
        }
        finally
        {
            foreach (var h in handles) h.Dispose();
        }

        foreach (var slice in plan.Files.Where(f => f.Slice is not null))
        {
            ct.ThrowIfCancellationRequested();
            var (container, offset) = slice.Slice!.Value;
            var target = Full(root, slice.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var src = new FileStream(Full(root, container), FileMode.Open, FileAccess.Read, FileShare.Read);
            src.Position = offset;
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write);
            var buffer = new byte[slice.Size];
            await src.ReadExactlyAsync(buffer, ct);
            await dst.WriteAsync(buffer, ct);
        }
        foreach (var temp in plan.Files.Where(f => f.Temporary)) TryDelete(Full(root, temp.Path));

        plan.Finish?.Invoke(plan);
        TryDelete(statePath);
    }

    /// <summary>The file on disk is the one the build lists: same size and, when the store gives one, the same checksum.</summary>
    static bool Same(string path, PlanFile f)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != f.Size) return false;
            if (f.Sha1 is null && f.Md5 is null) return false; // nothing to compare with: download it
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            var hash = f.Sha1 is not null ? System.Security.Cryptography.SHA1.HashData(stream) : System.Security.Cryptography.MD5.HashData(stream);
            return Convert.ToHexString(hash).Equals(f.Sha1 ?? f.Md5, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static async Task<byte[]> FetchWithRetries(PlanChunk chunk, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(2));
                var data = await chunk.Fetch(cts.Token);
                if (data.Length != chunk.Size) throw new InvalidDataException($"chunk is {data.Length} bytes, {chunk.Size} expected");
                return data;
            }
            catch (Exception ex) when (attempt < 5 && !ct.IsCancellationRequested)
            {
                Log.Warn($"Chunk at {chunk.Offset}, try {attempt}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    /// <summary>A path of the plan inside the install folder; never outside it (a manifest is data from the network).</summary>
    public static string Full(string root, string relative)
    {
        var rel = relative.Replace('/', '\\').TrimStart('\\');
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var top = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (!full.StartsWith(top, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"A file of the download points outside its folder: {relative}");
        return full;
    }

    static HashSet<string> ReadState(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize(File.ReadAllText(path), DownloadStateJson.Default.HashSetString) ?? [];
        }
        catch (Exception ex) { Log.Warn($"Download state {path}: {ex.Message}"); }
        return [];
    }

    static void WriteState(string path, HashSet<string> done)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(done, DownloadStateJson.Default.HashSetString)); }
        catch (Exception ex) { Log.Warn($"Download state {path}: {ex.Message}"); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>zlib data (GOG's manifests and chunks, Epic's chunks), or the bytes as they are when they are not compressed.</summary>
    public static byte[] Inflate(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x78) return data;
        using var src = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var dst = new MemoryStream();
        src.CopyTo(dst);
        return dst.ToArray();
    }

    /// <summary>A folder name made from a game's title (no characters Windows refuses).</summary>
    public static string FolderName(string title)
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = new string(title.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        while (name.Contains("  ")) name = name.Replace("  ", " ");
        return name.Length == 0 ? "Game" : name;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(HashSet<string>))]
partial class DownloadStateJson : System.Text.Json.Serialization.JsonSerializerContext;
