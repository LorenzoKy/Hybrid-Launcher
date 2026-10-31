using System.Text.Json;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Core.Accounts;

public enum DownloadState { Waiting, Downloading, Paused, Done, Failed }

/// <summary>One install followed by the Downloads tab.</summary>
public sealed class DownloadJob
{
    public required GameStore Store { get; init; }
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? Cover { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DownloadState State { get; set; }
    public long Done { get; set; }
    public long Total { get; set; }
    /// <summary>Bytes per second, smoothed over the last few seconds.</summary>
    public double Speed { get; set; }
    /// <summary>The fastest speed seen, for the graph's scale and the "peak" figure.</summary>
    public double Peak { get; set; }
    /// <summary>Speed samples, one a second, newest last; the Downloads tab draws them as a graph like Steam's.</summary>
    public List<double> History { get; } = [];
    public string? Folder { get; set; }
    /// <summary>Started from Hybrid Launcher (else seen running in the launcher).</summary>
    public bool FromApp { get; init; }

    /// <summary>Downloaded by Hybrid Launcher itself from a signed-in account (no launcher): Hybrid Launcher pauses, resumes and cancels it.</summary>
    public bool Direct { get; init; }
    /// <summary>Why a direct download stopped (or, while it waits to try again, what went wrong), shown under it.</summary>
    public string? Error { get; set; }
    /// <summary>An update of a game Hybrid Launcher downloaded: only the files that changed are fetched, into the game's own folder.</summary>
    public bool IsUpdate { get; set; }
    /// <summary>A direct download waiting its turn: Hybrid Launcher downloads one game at a time, like Steam, the others queue.</summary>
    public bool Queued { get; set; }
    /// <summary>A direct download reading the store's file list (its size is not known yet).</summary>
    public bool Preparing => Direct && State == DownloadState.Waiting && !Queued && Error is null;
    internal OwnedGame? Source;
    internal string? Root;
    /// <summary>The file list the install dialog already read: used for the first try instead of asking the store again.</summary>
    internal DownloadPlan? Plan;
    internal CancellationTokenSource? Cts;
    internal long LastBytes = -1;
    internal DateTime LastAt;
    internal DateTime LastMoved;
    internal DateTime LastWalk;

    /// <summary>Cancel was asked (the launcher deletes what it downloaded): when its files go away it reads "Cancelled".</summary>
    public bool CancelRequested { get; set; }

    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;

    public TimeSpan? Remaining => Speed > 1024 && Total > Done ? TimeSpan.FromSeconds((Total - Done) / Speed) : null;

    /// <summary>A pretend download for the self-test's pictures (HL_SAMPLE_DOWNLOADS): moves by itself, touches no disk.</summary>
    public bool IsSample { get; init; }
}

/// <summary>
/// The installs the Downloads tab follows. Hybrid Launcher only asks the launcher to install (its own install page opens); the launcher
/// downloads. Progress is read from what each launcher writes on disk:
///  • Steam: appmanifest_&lt;id&gt;.acf (bytes to download / downloaded, the "fully installed" flag) — also for downloads
///    started in Steam itself;
///  • Epic: the pending manifest of the install (its folder and size), then the folder growing on disk;
///  • GOG: the game showing up as installed (Galaxy keeps no readable progress).
/// A job is done when the game is listed as installed. Checks every second on a timer (the launchers' files every 2 s), events on the UI thread.
/// </summary>
public static class Downloads
{
    static readonly List<DownloadJob> Jobs = [];
    // Downloads the user removed from the list: Steam's manifest of a half-done install would otherwise bring them back at the next check.
    static readonly HashSet<string> Dismissed = new(StringComparer.OrdinalIgnoreCase);
    static SynchronizationContext? _ui;
    static Timer? _timer;
    static int _busy;

    public static event Action? Changed;

    /// <summary>Answers whether a game id is installed now (the app's game list).</summary>
    public static Func<string, bool> IsInstalled { get; set; } = _ => false;

    /// <summary>Asked to rescan the installed games when a download finished.</summary>
    public static Action? Finished { get; set; }

    /// <summary>
    /// A game the app itself downloaded finished installing (not an update, not one a launcher started on its own): the id,
    /// on the UI thread, before <see cref="Finished"/>'s rescan lists it. Used for the desktop shortcut made after a download.
    /// </summary>
    public static event Action<string>? Installed;

    static void RaiseInstalled(DownloadJob job)
    {
        if (!job.FromApp || job.IsUpdate || job.IsSample) return;
        var installed = Installed;
        if (installed is not null) _ui?.Post(_ => installed(job.Id), null);
    }

    public static IReadOnlyList<DownloadJob> All { get { lock (Jobs) return Jobs.ToList(); } }

    public static int ActiveCount { get { lock (Jobs) return Jobs.Count(j => j.State is DownloadState.Waiting or DownloadState.Downloading or DownloadState.Paused); } }

    public static DownloadJob? Find(string id) { lock (Jobs) return Jobs.FirstOrDefault(j => j.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); }

    /// <summary>Opens the launcher's install page for the game and follows the install.</summary>
    public static DownloadJob Start(OwnedGame game)
    {
        _ui ??= SynchronizationContext.Current;
        GameLauncher.OpenUri(game.InstallUri);
        DownloadJob job;
        lock (Jobs)
        {
            Dismissed.Remove(game.Id);
            Jobs.RemoveAll(j => j.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase) && j.State is DownloadState.Done or DownloadState.Failed);
            job = Jobs.FirstOrDefault(j => j.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase)) ?? new DownloadJob
            {
                Store = game.Store, Id = game.Id, Name = game.Name, Cover = game.Cover, FromApp = true, State = DownloadState.Waiting,
            };
            if (!Jobs.Contains(job)) Jobs.Insert(0, job);
        }
        Log.Info($"Download: {game.Name} ({game.InstallUri})");
        Raise();
        EnsureTimer();
        return job;
    }

    // ---------- direct downloads (signed-in accounts, no launcher) ----------

    /// <summary>
    /// The folder a store's games go in when Hybrid Launcher downloads them: the one chosen in Downloads → Accounts, else the folder that
    /// store's own launcher installs in, so a game sits where the launcher would put it, under the launcher's own folder name
    /// (Steam: its library's steamapps\common, with the app manifest that lets Steam see it; Epic: the launcher's default install
    /// location; GOG: GOG Galaxy's library folder).
    /// </summary>
    public static string InstallRootFor(GameStore store) =>
        SettingsStore.Current.InstallRoots.TryGetValue(store.ToString(), out var chosen) && chosen.Length > 0 ? Normalize(chosen) : LauncherRoot(store);

    /// <summary>A folder written by hand in the settings ("D:/Games/"): Windows' own form, or the paths made from it trip Windows APIs.</summary>
    static string Normalize(string dir)
    {
        try { return Path.GetFullPath(dir).TrimEnd('\\', '/') is { Length: > 3 } full ? full : Path.GetFullPath(dir); }
        catch { return dir; }
    }

    /// <summary>The launcher's own install folder for its games, read from its settings; its out-of-the-box default otherwise.</summary>
    public static string LauncherRoot(GameStore store)
    {
        try
        {
            switch (store)
            {
                case GameStore.Steam:
                    var lib = Detection.SteamScanner.Libraries().FirstOrDefault();
                    return Path.Combine(lib ?? Path.Combine(ProgramFilesX86, "Steam"), "steamapps", "common");
                case GameStore.Epic:
                    var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini");
                    if (File.Exists(ini))
                        foreach (var line in File.ReadLines(ini))
                            if (line.StartsWith("DefaultAppInstallLocation=", StringComparison.OrdinalIgnoreCase) && line[(line.IndexOf('=') + 1)..].Trim() is { Length: > 0 } epic)
                                return epic;
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
                case GameStore.Gog:
                    var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GOG.com", "Galaxy", "config.json");
                    if (File.Exists(config))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
                        if (doc.RootElement.TryGetProperty("libraryPath", out var p) && p.GetString() is { Length: > 0 } gog) return gog;
                    }
                    return Path.Combine(ProgramFilesX86, "GOG Galaxy", "Games");
            }
        }
        catch (Exception ex) { Log.Warn($"{store} install folder: {ex.Message}"); }
        return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Games");
    }

    static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <summary>
    /// Makes sure Hybrid Launcher can write in <paramref name="root"/>. The launchers' folders often sit in Program Files, which needs
    /// an administrator: Windows asks once (its own prompt) to make the folder and let this Windows user write in it, as the
    /// launchers' own installers do. False when that was refused.
    /// </summary>
    public static bool EnsureWritable(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".hl-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            try
            {
                var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mkdir \"{root}\" 2>nul & icacls \"{root}\" /grant \"{user}\":(OI)(CI)M /T /Q",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });
                p?.WaitForExit(60_000);
                return p?.ExitCode == 0;
            }
            catch (Exception e)
            {
                Log.Warn($"Install folder {root}: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>What a direct download of the game would fetch (its files and size), read from the store; the install dialog shows it.</summary>
    public static Task<DownloadPlan> PlanAsync(OwnedGame game, string root, CancellationToken ct) => game.Store switch
    {
        GameStore.Steam => SteamAccount.PlanAsync(game, root, ct),
        GameStore.Epic => EpicAccount.PlanAsync(game, root, ct),
        GameStore.Gog => GogAccount.PlanAsync(game, root, ct),
        _ => throw new NotSupportedException(L.T("This store's games install from its own app.")),
    };

    /// <summary>
    /// Downloads the game from the store into <paramref name="root"/> (one folder per game) and follows it here. With
    /// <paramref name="plan"/> (read by the install dialog) the download starts at once. Another direct download running,
    /// this one queues and starts when that one ends.
    /// </summary>
    public static DownloadJob StartDirect(OwnedGame game, string root, DownloadPlan? plan = null, string? updateFolder = null)
    {
        if (plan is not null) plan.InstallDir = Path.Combine(root, Path.GetFileName(plan.InstallDir));
        _ui ??= SynchronizationContext.Current;
        DownloadJob job;
        lock (Jobs)
        {
            Dismissed.Remove(game.Id);
            Jobs.RemoveAll(j => j.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase) && j.State is DownloadState.Done or DownloadState.Failed);
            job = Jobs.FirstOrDefault(j => j.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase) && j.Direct) ?? new DownloadJob
            {
                Store = game.Store, Id = game.Id, Name = game.Name, Cover = game.Cover, FromApp = true, Direct = true, State = DownloadState.Waiting,
            };
            job.Source = game;
            job.Root = root;
            job.IsUpdate = updateFolder is not null;
            if (updateFolder is not null) job.Folder = updateFolder;
            job.Plan = plan;
            if (plan is not null)
            {
                job.Folder = plan.InstallDir;
                job.Total = plan.TotalBytes;
            }
            if (!Jobs.Contains(job)) Jobs.Add(job);
        }
        Log.Info($"Direct download: {game.Name} into {root}");
        Enqueue(job);
        EnsureTimer();
        return job;
    }

    /// <summary>
    /// Updates a game Hybrid Launcher downloaded to the store's newest build, as Steam does: it queues like a download, keeps every file
    /// that is already as the new build has it and fetches only the rest, in the game's own folder.
    /// </summary>
    public static DownloadJob StartUpdate(OwnedGame game, DirectInstall install)
    {
        Log.Info($"Update: {game.Name} ({install.Version ?? "?"} → newest)");
        return StartDirect(game, Path.GetDirectoryName(install.InstallDir) ?? install.InstallDir, updateFolder: install.InstallDir);
    }

    /// <summary>An update of the game is downloading or waiting: the game should not start until it is done.</summary>
    public static bool Updating(string id)
    {
        lock (Jobs) return Jobs.Any(j => j.IsUpdate && j.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && j.State is DownloadState.Waiting or DownloadState.Downloading);
    }

    // The direct download running now (one at a time, as Steam does); the others wait with Queued set.
    static DownloadJob? _running;

    /// <summary>Runs the job now when no other direct download runs, else queues it.</summary>
    static void Enqueue(DownloadJob job)
    {
        bool queue;
        lock (Jobs)
        {
            queue = _running is { } other && other != job;
            if (queue)
            {
                job.Queued = true;
                job.State = DownloadState.Waiting;
                job.Speed = 0;
                job.Error = null;
            }
            else _running = job;
        }
        if (queue) Raise();
        else Run(job);
    }

    /// <summary>The first queued download starts, once none runs.</summary>
    static void StartNext()
    {
        DownloadJob? next;
        lock (Jobs)
        {
            if (_running is not null) return;
            next = Jobs.FirstOrDefault(j => j.Direct && j.Queued && j.State == DownloadState.Waiting);
            if (next is null) return;
            _running = next;
        }
        Run(next);
    }

    /// <summary>Pauses a direct download (or takes it out of the queue): what is finished stays on disk, Resume goes on from there.</summary>
    public static void Pause(DownloadJob job)
    {
        if (!job.Direct || job.State is DownloadState.Done or DownloadState.Failed) return;
        job.Queued = false;
        job.Cts?.Cancel();
        job.State = DownloadState.Paused;
        job.Speed = 0;
        Raise();
    }

    /// <summary>Resumes a paused download: at once when nothing else downloads, else at the end of the queue.</summary>
    public static void Resume(DownloadJob job)
    {
        if (!job.Direct || job.Source is null || job.State is DownloadState.Downloading || job.State == DownloadState.Waiting && !job.Queued && job.Error is null) return;
        if (job.Queued) { StartNow(job); return; }
        Enqueue(job);
    }

    /// <summary>A queued download goes first: the one running now goes back to the head of the queue and continues after it.</summary>
    public static void StartNow(DownloadJob job)
    {
        if (!job.Direct || job.Source is null) return;
        lock (Jobs)
        {
            if (_running is { } other && other != job)
            {
                other.Cts?.Cancel();
                other.Queued = true;
                other.State = DownloadState.Waiting;
                other.Speed = 0;
                // Next in line after this one.
                Jobs.Remove(other);
                Jobs.Insert(Math.Min(Jobs.IndexOf(job) + 1, Jobs.Count), other);
            }
            _running = job;
        }
        Run(job);
    }

    /// <summary>Errors that a new try can get past (the network, a store server); the others stop the download at once.</summary>
    static bool Transient(Exception ex) => ex is HttpRequestException or TimeoutException or IOException or TaskCanceledException
        || ex.GetType().Name.Contains("AsyncJob", StringComparison.Ordinal) || ex.InnerException is { } inner && Transient(inner);

    static void Run(DownloadJob job)
    {
        job.Cts?.Cancel();
        var cts = job.Cts = new CancellationTokenSource();
        job.State = DownloadState.Waiting;
        job.Queued = false;
        job.Error = null;
        job.LastBytes = -1;
        Raise();
        var game = job.Source!;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        // A plan read again at a new try: its servers and keys may have gone stale.
                        var plan = job.Plan ?? await PlanAsync(game, job.Root!, cts.Token);
                        job.Plan = null;
                        if (job.IsUpdate && job.Folder is { } own)
                        {
                            // The game's own folder, wherever it was installed; files already up to date stay.
                            plan.InstallDir = own;
                            plan.Update = true;
                        }
                        job.Folder = plan.InstallDir;
                        job.Total = plan.TotalBytes;
                        job.Done = 0;
                        job.Error = null;
                        job.State = DownloadState.Downloading;
                        Raise();
                        // Chunks finish in any order on several threads: the count only goes up.
                        await DirectDownloader.RunAsync(plan, bytes => { lock (job) if (bytes > job.Done) job.Done = bytes; }, cts.Token);
                        Register(game, plan);
                        break;
                    }
                    catch (Exception ex) when (!cts.IsCancellationRequested && attempt < 4 && Transient(ex))
                    {
                        // The connection dropped (Wi-Fi, a store server): wait a little and go on where it stopped, like Steam.
                        int wait = attempt * 10;
                        Log.Warn($"Direct download of {game.Name}, try {attempt}: {ex.Message}; again in {wait} s");
                        job.State = DownloadState.Waiting;
                        job.Speed = 0;
                        job.Error = L.F("The connection dropped. Trying again in {0} s…", wait);
                        Raise();
                        await Task.Delay(TimeSpan.FromSeconds(wait), cts.Token);
                    }
                }
                job.State = DownloadState.Done;
                job.Done = job.Total;
                job.Speed = 0;
                job.Error = null;
                Log.Info($"Direct download done: {game.Name} ({job.Folder})");
                Raise();
                RaiseInstalled(job);
                var finished = Finished;
                if (finished is not null) _ui?.Post(_ => finished(), null);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Paused, cancelled or moved back in the queue: the state was set by Pause / Cancel / StartNow.
            }
            catch (Exception ex)
            {
                Log.Error($"Direct download of {game.Name} failed", ex);
                job.Error = ex.Message;
                job.State = DownloadState.Paused;
                job.Speed = 0;
                Raise();
            }
            finally
            {
                bool freed;
                lock (Jobs)
                {
                    // Only this run's end frees the slot: a Resume right after a Pause already runs again with a new token.
                    freed = _running == job && job.Cts == cts;
                    if (freed) _running = null;
                }
                if (freed) StartNext();
            }
        });
    }

    /// <summary>The finished game joins the list as a Hybrid Launcher install (what to start, from the store's own launch data).</summary>
    static void Register(OwnedGame game, DownloadPlan plan)
    {
        var s = SettingsStore.Current;
        lock (s)
        {
            s.DirectInstalls.RemoveAll(d => d.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase));
            s.DirectInstalls.Add(new DirectInstall
            {
                Id = game.Id, Store = game.Store, Name = game.Name, InstallDir = plan.InstallDir,
                Exe = plan.Exe, Args = plan.Args, WorkingDir = plan.WorkingDir, Version = plan.Version, Size = plan.TotalBytes,
            });
        }
        SettingsStore.Save();
    }

    /// <summary>Cancels a direct download: it stops and its folder goes (only the folder Hybrid Launcher made for it).</summary>
    static async Task<string?> CancelDirectAsync(DownloadJob job)
    {
        job.CancelRequested = true;
        job.Cts?.Cancel();
        Raise();
        var folder = job.Folder;
        if (job.IsUpdate)
        {
            // An update runs in the installed game's folder: nothing is deleted. The files already updated stay (they are checked
            // again by checksum next time), only the download's own list goes; the game stays on its old build until it updates.
            await Task.Delay(800);
            if (folder is not null) DirectDownloader.DeleteState(folder);
            Forget(job);
            return null;
        }
        if (folder is not null && Directory.Exists(folder) && DirectDownloader.HasState(folder))
        {
            // The writers stop at their next chunk: give them a moment before deleting.
            await Task.Delay(800);
            try { await Task.Run(() => Directory.Delete(folder, recursive: true)); }
            catch (Exception ex) { Log.Warn($"Cancel {job.Name}: {ex.Message}"); return L.F("The download stopped, but its folder could not be deleted: {0}", ex.Message); }
        }
        Forget(job);
        return null;
    }

    /// <summary>Stops following a job (the install itself goes on or stops in the launcher); it does not come back by itself.</summary>
    public static void Remove(DownloadJob job)
    {
        lock (Jobs)
        {
            if (job.Direct) job.Cts?.Cancel();
            Jobs.Remove(job);
            if (job.State is not DownloadState.Done and not DownloadState.Failed) Dismissed.Add(job.Id);
        }
        Raise();
    }

    /// <summary>
    /// Cancels a download, like Steam's "Uninstall" on a game that never finished installing. Steam: Hybrid Launcher closes Steam (it keeps
    /// the download in memory and would write it back), deletes the download (its manifest, the downloading and temp folders and
    /// the partial game folder), then starts Steam again in the background. A job still waiting for the launcher just leaves the
    /// list. Epic and GOG keep their downloads in their own databases: their window opens to cancel it there.
    /// Returns null when done, else what to tell the user.
    /// </summary>
    public static async Task<string?> CancelAsync(DownloadJob job)
    {
        if (job.Direct) return await CancelDirectAsync(job);
        if (job.State == DownloadState.Waiting && (job.Store != GameStore.Steam || FindSteamManifest(job.Id) is null))
        {
            Forget(job);
            return null;
        }
        job.CancelRequested = true;
        Raise();
        Log.Info($"Download cancel asked: {job.Name}");
        switch (job.Store)
        {
            case GameStore.Steam:
                var error = await Task.Run(() => CancelSteam(job));
                if (error is not null)
                {
                    job.CancelRequested = false;
                    Raise();
                    return error;
                }
                Forget(job);
                var finished = Finished;
                if (finished is not null) _ui?.Post(_ => finished(), null);
                return null;
            case GameStore.Epic:
                GameLauncher.OpenUri("com.epicgames.launcher://library");
                return L.T("Epic Games opened: cancel the install from its library. Hybrid Launcher stops following it once it is gone.");
            default:
                GameLauncher.OpenUri("goggalaxy://");
                return L.T("GOG Galaxy opened: cancel the install from its downloads. Hybrid Launcher stops following it once it is gone.");
        }
    }

    /// <summary>Drops a job for good: Steam's manifest of a half-done install must not bring it back.</summary>
    static void Forget(DownloadJob job)
    {
        lock (Jobs)
        {
            Jobs.Remove(job);
            Dismissed.Add(job.Id);
        }
        Raise();
    }

    /// <summary>The library's steamapps folder and the manifest of a Steam app, if there is one.</summary>
    static (string Apps, string Acf)? FindSteamManifest(string id)
    {
        if (!id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return null;
        var appId = id["steam:".Length..];
        foreach (var lib in SteamScanner.Libraries())
        {
            var apps = Path.Combine(lib, "steamapps");
            var acf = Path.Combine(apps, $"appmanifest_{appId}.acf");
            if (File.Exists(acf)) return (apps, acf);
        }
        return null;
    }

    static string? CancelSteam(DownloadJob job)
    {
        var appId = job.Id["steam:".Length..];
        if (FindSteamManifest(job.Id) is not { } found) return null; // already gone
        VdfNode? state;
        try { state = VdfNode.Parse(File.ReadAllText(found.Acf)).Child("AppState"); }
        catch (Exception ex) { return ex.Message; }
        int.TryParse(state?["StateFlags"], out var flags);
        // An update of a game that is installed: cancelling would mean deleting the game. Steam only pauses those.
        if ((flags & 4) != 0) return L.F("{0} is installed and this is an update: pause it in Steam instead.", job.Name);
        var installDir = state?["installdir"];

        var steamExe = SteamScanner.SteamExe();
        bool running = SteamRunning();
        if (running)
        {
            if (steamExe is null) return L.T("Steam is running but steam.exe was not found: close Steam and try again.");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(steamExe, "-shutdown") { UseShellExecute = false }); }
            catch (Exception ex) { return ex.Message; }
            var until = DateTime.UtcNow.AddSeconds(60);
            while (SteamRunning() && DateTime.UtcNow < until) Thread.Sleep(500);
            if (SteamRunning()) return L.T("Steam did not close in time. Close it from its menu (Steam → Exit) and press Cancel again.");
            Thread.Sleep(800); // let it release the last files
        }

        string? failed = null;
        void Delete(string path, bool folder)
        {
            try
            {
                if (folder && Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (!folder && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) { failed ??= ex.Message; Log.Warn($"Cancel {job.Name}: {path}: {ex.Message}"); }
        }
        Delete(found.Acf, folder: false);
        Delete(Path.Combine(found.Apps, "downloading", appId), folder: true);
        Delete(Path.Combine(found.Apps, "temp", appId), folder: true);
        try
        {
            foreach (var patch in Directory.GetFiles(Path.Combine(found.Apps, "downloading"), $"state_{appId}_*.patch")) Delete(patch, folder: false);
        }
        catch { }
        // The partial game folder, only when it is really this game's (a plain name under steamapps\common).
        if (!string.IsNullOrWhiteSpace(installDir) && installDir.IndexOfAny(['\\', '/', ':']) < 0 && installDir is not "." and not "..")
        {
            var common = Path.GetFullPath(Path.Combine(found.Apps, "common"));
            var dir = Path.GetFullPath(Path.Combine(common, installDir));
            if (dir.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Delete(dir, folder: true);
        }

        if (running && steamExe is not null)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(steamExe, "-silent") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn($"Steam restart: {ex.Message}"); }
        }
        Log.Info($"Download cancelled: {job.Name}" + (failed is null ? "" : $" (some files left: {failed})"));
        return File.Exists(found.Acf) ? failed ?? L.T("Steam's record of the download could not be deleted.") : null;
    }

    static bool SteamRunning()
    {
        var list = System.Diagnostics.Process.GetProcessesByName("steam");
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    public static void ClearFinished()
    {
        lock (Jobs) Jobs.RemoveAll(j => j.State is DownloadState.Done or DownloadState.Failed);
        Raise();
    }

    /// <summary>The job of a game still being installed (waiting, downloading or paused), if any.</summary>
    public static DownloadJob? Active(string id)
    {
        var j = Find(id);
        return j is { State: DownloadState.Waiting or DownloadState.Downloading or DownloadState.Paused } ? j : null;
    }

    /// <summary>Starts watching (Steam downloads begun in Steam itself show up too).</summary>
    public static void Watch()
    {
        _ui ??= SynchronizationContext.Current;
        EnsureTimer();
    }

    static void Raise()
    {
        if (_ui is null) Changed?.Invoke();
        else _ui.Post(_ => { try { Changed?.Invoke(); } catch (Exception ex) { Log.Error("Downloads event", ex); } }, null);
    }

    static void EnsureTimer()
    {
        // Every second, as Steam's downloads page: the bars and figures move smoothly. The launchers' files are read every other tick.
        lock (Jobs) _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1));
    }

    static int _ticks;

    static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            bool launchers = _ticks++ % 2 == 0;
            bool changed = launchers && SteamTick();
            List<DownloadJob> jobs;
            lock (Jobs) jobs = Jobs.Where(j => j.State is DownloadState.Waiting or DownloadState.Downloading or DownloadState.Paused).ToList();
            foreach (var j in jobs)
            {
                if (j.IsSample) { SampleTick(j); continue; }
                if (j.Direct)
                {
                    // Followed by its own download task; the speed is measured here, once a second, from the bytes written.
                    if (j.State == DownloadState.Downloading) MeasureDirect(j);
                    continue;
                }
                if (!launchers) continue;
                if (j.Store == GameStore.Epic) changed |= EpicTick(j);
                else if (j.Store == GameStore.Gog && j.Folder is not null) changed |= FolderTick(j);
                // Listed as installed (and not still downloading): done.
                if (j.Store != GameStore.Steam && IsInstalled(j.Id))
                {
                    j.State = DownloadState.Done;
                    j.Speed = 0;
                    if (j.Total > 0) j.Done = j.Total;
                    changed = true;
                    Log.Info($"Download done: {j.Name}");
                    RaiseInstalled(j);
                    var done = Finished;
                    if (done is not null) _ui?.Post(_ => done(), null);
                }
                // Nothing seen for 30 minutes after asking: the launcher's dialog was probably closed.
                else if (j.State == DownloadState.Waiting && DateTime.Now - j.StartedAt > TimeSpan.FromMinutes(30))
                {
                    j.State = DownloadState.Failed;
                    changed = true;
                }
            }
            List<DownloadJob> live;
            lock (Jobs) live = Jobs.Where(j => j.State is DownloadState.Downloading or DownloadState.Paused or DownloadState.Waiting).ToList();
            foreach (var j in live) Sample(j);
            if (changed || live.Count > 0) Raise();
        }
        catch (Exception ex) { Log.Warn($"Downloads: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>
    /// Speed from the bytes the launcher reports. Launchers write their numbers every few seconds, not at every check: the speed is
    /// measured between two changes (not between two checks, which would read 0 in between) and only falls once nothing moved for 20 s.
    /// </summary>
    static void Measure(DownloadJob j, long bytes)
    {
        var now = DateTime.UtcNow;
        // First reading, a restarted download, or one that sat still (paused): a new starting point, not a slow rate.
        if (j.LastBytes < 0 || bytes < j.LastBytes || (j.Speed == 0 && bytes > j.LastBytes && now - j.LastMoved > TimeSpan.FromSeconds(30)))
        {
            j.LastBytes = bytes;
            j.LastAt = j.LastMoved = now;
            return;
        }
        if (bytes > j.LastBytes && now > j.LastAt)
        {
            double rate = (bytes - j.LastBytes) / (now - j.LastAt).TotalSeconds;
            // Smoothed, so the number does not jump at every write.
            j.Speed = j.Speed <= 0 ? rate : j.Speed * 0.55 + rate * 0.45;
            j.LastBytes = bytes;
            j.LastAt = j.LastMoved = now;
        }
        else if (now - j.LastMoved > TimeSpan.FromSeconds(20)) j.Speed *= 0.5;
        if (j.Speed < 1024) j.Speed = j.Speed < 1 ? 0 : j.Speed;
        j.Peak = Math.Max(j.Peak, j.Speed);
    }

    /// <summary>A direct download's speed: the bytes written since the last tick, smoothed over a few seconds.</summary>
    static void MeasureDirect(DownloadJob j)
    {
        var now = DateTime.UtcNow;
        long bytes = j.Done;
        if (j.LastBytes < 0 || bytes < j.LastBytes)
        {
            j.LastBytes = bytes;
            j.LastAt = now;
            return;
        }
        double seconds = (now - j.LastAt).TotalSeconds;
        if (seconds < 0.5) return;
        double rate = (bytes - j.LastBytes) / seconds;
        j.Speed = j.Speed <= 0 ? rate : j.Speed * 0.6 + rate * 0.4;
        if (j.Speed < 1024) j.Speed = 0;
        j.LastBytes = bytes;
        j.LastAt = now;
        j.Peak = Math.Max(j.Peak, j.Speed);
    }

    /// <summary>The samples the speed graph draws: one a second, the last five minutes.</summary>
    public const int HistorySpan = 300;

    /// <summary>
    /// Self-test only (HL_SAMPLE_DOWNLOADS=1): one download in progress, one queued, one paused and one finished, so the
    /// Downloads tab can be looked at and its graph watched moving without downloading anything.
    /// </summary>
    public static void AddSamples()
    {
        static string Art(string app) => $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{app}/library_600x900.jpg";
        var now = new DownloadJob { Store = GameStore.Steam, Id = "steam:1174180", Name = "Red Dead Redemption 2", Cover = Art("1174180"), State = DownloadState.Downloading, Total = 121L << 30, Done = 40L << 30, Direct = true, IsSample = true, Folder = @"D:\Games\Red Dead Redemption 2" };
        for (int i = 0; i < 120; i++) now.History.Add((38 + 12 * Math.Sin(i / 7.0) + 6 * Math.Sin(i / 2.3)) * (1 << 20));
        now.Speed = now.History[^1];
        now.Peak = now.History.Max();
        lock (Jobs)
        {
            Jobs.Add(now);
            Jobs.Add(new DownloadJob { Store = GameStore.Steam, Id = "steam:1245620", Name = "ELDEN RING", Cover = Art("1245620"), State = DownloadState.Waiting, Queued = true, Total = 49L << 30, Direct = true, IsSample = true });
            Jobs.Add(new DownloadJob { Store = GameStore.Steam, Id = "steam:2215430", Name = "Ghost of Tsushima", Cover = Art("2215430"), State = DownloadState.Paused, Total = 75L << 30, Done = 26L << 30, Direct = true, IsSample = true });
            Jobs.Add(new DownloadJob { Store = GameStore.Steam, Id = "steam:1145350", Name = "Hades II", Cover = Art("1145350"), State = DownloadState.Done, Total = 10L << 30, Done = 10L << 30, Direct = true, IsSample = true });
        }
        Watch();
        Raise();
    }

    static void SampleTick(DownloadJob j)
    {
        if (j.State != DownloadState.Downloading) return;
        double t = Environment.TickCount64 / 1000.0;
        j.Speed = (40 + 12 * Math.Sin(t / 7.0) + 6 * Math.Sin(t / 2.3)) * (1 << 20);
        j.Peak = Math.Max(j.Peak, j.Speed);
        j.Done = Math.Min(j.Total, j.Done + (long)j.Speed);
    }

    static void Sample(DownloadJob j)
    {
        lock (j.History)
        {
            j.History.Add(j.State == DownloadState.Downloading ? j.Speed : 0);
            if (j.History.Count > HistorySpan) j.History.RemoveAt(0);
        }
    }

    // ---------- Steam ----------

    /// <summary>Every appmanifest that is not fully installed is a download (started in Hybrid Launcher or in Steam).</summary>
    static bool SteamTick()
    {
        bool changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in SteamScanner.Libraries())
        {
            var apps = Path.Combine(lib, "steamapps");
            string[] files;
            try { files = Directory.GetFiles(apps, "appmanifest_*.acf"); } catch { continue; }
            foreach (var acf in files)
            {
                VdfNode? state;
                try { state = VdfNode.Parse(File.ReadAllText(acf)).Child("AppState"); } catch { continue; }
                if (state?["appid"] is not { } appId) continue;
                int.TryParse(state["StateFlags"], out var flags);
                long.TryParse(state["BytesToDownload"], out var total);
                long.TryParse(state["BytesDownloaded"], out var done);
                var id = $"steam:{appId}";
                bool complete = (flags & 4) != 0 && (flags & 1024) == 0 && (total == 0 || done >= total);
                var job = Find(id);
                if (job is { Direct: true }) continue;
                if (complete)
                {
                    if (job is { State: not DownloadState.Done })
                    {
                        job.State = DownloadState.Done;
                        job.Speed = 0;
                        job.Done = job.Total = Math.Max(job.Total, total);
                        changed = true;
                        Log.Info($"Download done: {job.Name}");
                        RaiseInstalled(job);
                        var finished = Finished;
                        if (finished is not null) _ui?.Post(_ => finished(), null);
                    }
                    continue;
                }
                // An update of an installed game is not a "download" unless Hybrid Launcher asked for it; one removed from the list stays away.
                if (job is null && ((flags & 4) != 0 || Dismissed.Contains(id))) continue;
                if (job is null)
                {
                    job = new DownloadJob { Store = GameStore.Steam, Id = id, Name = state["name"] ?? id, State = DownloadState.Waiting,
                        Cover = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg" };
                    lock (Jobs) Jobs.Add(job);
                }
                seen.Add(id);
                if (state["installdir"] is { } dir) job.Folder = Path.Combine(apps, "common", dir);
                Measure(job, done);
                var before = (job.State, job.Done, job.Total);
                job.Total = total;
                job.Done = done;
                // UpdateRunning (1024) = downloading now; otherwise queued or paused in Steam.
                job.State = (flags & 1024) != 0 ? DownloadState.Downloading : total > 0 ? DownloadState.Paused : DownloadState.Waiting;
                if (job.State != DownloadState.Downloading) job.Speed = 0;
                changed |= before != (job.State, job.Done, job.Total) || job.Speed > 0;
            }
        }
        // A Steam download whose manifest went away was cancelled in Steam.
        List<DownloadJob> gone;
        lock (Jobs) gone = Jobs.Where(j => j.Store == GameStore.Steam && !j.Direct && j.State is DownloadState.Downloading or DownloadState.Paused or DownloadState.Waiting
            && !seen.Contains(j.Id) && (j.State != DownloadState.Waiting || j.CancelRequested)).ToList();
        foreach (var j in gone)
        {
            j.State = DownloadState.Failed;
            j.Speed = 0;
            changed = true;
        }
        return changed;
    }

    // ---------- Epic ----------

    /// <summary>Epic writes the install's manifest under Manifests\Pending while it downloads: its folder and size give the progress.</summary>
    static bool EpicTick(DownloadJob j)
    {
        var appName = j.Id["epic:".Length..];
        if (j.Folder is null)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests", "Pending");
            try
            {
                if (Directory.Exists(dir))
                    foreach (var item in Directory.GetFiles(dir, "*.item"))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(item));
                        var r = doc.RootElement;
                        if (!(r.TryGetProperty("AppName", out var a) && a.GetString() == appName)) continue;
                        if (r.TryGetProperty("InstallLocation", out var loc)) j.Folder = loc.GetString();
                        if (r.TryGetProperty("InstallSize", out var size) && size.TryGetInt64(out var bytes)) j.Total = bytes;
                    }
            }
            catch (Exception ex) { Log.Warn($"Epic pending manifest: {ex.Message}"); }
        }
        return j.Folder is not null && FolderTick(j);
    }

    /// <summary>The install folder's size on disk as the progress (launchers that keep no readable numbers).</summary>
    static bool FolderTick(DownloadJob j)
    {
        if (j.Folder is null || !Directory.Exists(j.Folder)) return false;
        // Walking a big folder costs: every 6 s is enough for a progress bar.
        if (DateTime.UtcNow - j.LastWalk < TimeSpan.FromSeconds(6)) return false;
        j.LastWalk = DateTime.UtcNow;
        long bytes = DiskUsage.FolderSize(j.Folder, CancellationToken.None);
        Measure(j, bytes);
        bool moved = bytes != j.Done;
        j.Done = bytes;
        j.State = moved || j.Speed > 1024 ? DownloadState.Downloading : j.State == DownloadState.Waiting ? DownloadState.Waiting : DownloadState.Paused;
        if (j.State != DownloadState.Downloading) j.Speed = 0;
        return true;
    }
}
