using System.Diagnostics;
using HybridLauncher.Core.Hardware;
using HybridLauncher.Core.Opti;

namespace HybridLauncher.Core;

public enum GameRunState { Stopped, Starting, Running, Stopping }

/// <summary>
/// Which games are running: the ones started from Hybrid Launcher (watched from the click on Play) and any other game found running from
/// its folder. Every 2 seconds the game's folder is checked for a running exe, so a game closed in any way (its own menu, Alt+F4,
/// a crash, Task Manager, Stop in Hybrid Launcher) is seen as closed. On top of that:
///  • time played for non-Steam games (Steam counts its own);
///  • Smart HDR: Windows HDR is turned on when a game with Smart HDR starts (only if it was off) and off again when it closes.
/// Events are raised on the thread that started the first session (the UI thread).
/// </summary>
public static class GameSessions
{
    sealed class Session
    {
        public required Game Game;
        public required GameSettings Settings;
        public required string Dir;
        public GameRunState State;
        public DateTime StartedAt;
        public DateTime? RunningSince;
        public double PendingMinutes;
        public int Misses;
        public string? Process;
        public bool Launched;
    }

    static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
    static SynchronizationContext? _ui;
    static Timer? _timer;
    static int _ticking;

    /// <summary>A game's state changed (game id).</summary>
    public static event Action<string>? Changed;

    /// <summary>A game's time played changed (game id).</summary>
    public static event Action<string>? PlaytimeChanged;

    /// <summary>A game closed, however it was started or closed (cloud saves go up then).</summary>
    public static event Action<Game>? Ended;

    /// <summary>Smart HDR could not switch HDR (message for the user).</summary>
    public static event Action<string>? HdrProblem;

    static void OnUi(Action a)
    {
        if (_ui is null) a();
        else _ui.Post(_ => { try { a(); } catch (Exception ex) { Log.Error("Game session event", ex); } }, null);
    }

    public static GameRunState State(string gameId)
    {
        lock (Sessions) return Sessions.TryGetValue(gameId, out var s) ? s.State : GameRunState.Stopped;
    }

    public static bool IsActive(string gameId) => State(gameId) is GameRunState.Starting or GameRunState.Running or GameRunState.Stopping;

    /// <summary>Ids of the games running or starting now.</summary>
    public static List<string> Active()
    {
        lock (Sessions) return Sessions.Values.Where(s => s.State != GameRunState.Stopped).Select(s => s.Game.Id).ToList();
    }

    /// <summary>Called right after the game was started from Hybrid Launcher: turns HDR on when the game asks for it, then watches it.</summary>
    public static void Launched(Game game, GameSettings settings, string dir)
    {
        _ui ??= SynchronizationContext.Current;
        if (settings.SmartHdr) HdrOn(game);
        lock (Sessions)
        {
            Sessions[game.Id] = new Session { Game = game, Settings = settings, Dir = dir, State = GameRunState.Starting, StartedAt = DateTime.UtcNow, Launched = true };
        }
        Raise(game.Id);
        EnsureTimer();
    }

    /// <summary>
    /// Looks once whether a game is running although Hybrid Launcher did not start it (opened from its launcher); then it is watched too.
    /// Cheap enough for the game page to call when it opens.
    /// </summary>
    public static void Observe(Game game, GameSettings settings, string? dir)
    {
        _ui ??= SynchronizationContext.Current;
        if (dir is null || IsActive(game.Id)) return;
        _ = Task.Run(() =>
        {
            var running = OptiInstaller.RunningGameProcess(dir);
            if (running is null) return;
            lock (Sessions)
            {
                if (Sessions.TryGetValue(game.Id, out var s) && s.State != GameRunState.Stopped) return;
                Sessions[game.Id] = new Session
                {
                    Game = game, Settings = settings, Dir = dir, State = GameRunState.Running, StartedAt = DateTime.UtcNow,
                    RunningSince = DateTime.UtcNow, Process = running,
                };
            }
            Log.Info($"Session: {game.Name} found running ({running})");
            OnUi(() => Raise(game.Id));
            EnsureTimer();
        });
    }

    // ---------- every game, however it was started ----------

    static Func<IEnumerable<(Game Game, GameSettings Settings)>>? _library;
    static Timer? _scanTimer;
    static int _scanning;
    // The exe path of each process seen (asked once per process id; a reused id with another start time is asked again).
    static readonly Dictionary<int, (DateTime Start, string? Path)> Seen = [];

    /// <summary>
    /// Watches every game of the library, not only those opened in Hybrid Launcher: every few seconds the running processes are matched
    /// against the games' folders, so a game started from Steam, a shortcut or its own exe, before or after Hybrid Launcher, shows as
    /// running (Play becomes Stop, time played counts, the tray says so) and as closed once it is gone — as Steam and other
    /// launchers do. Cheap: each process's path is asked of Windows once, with the lightest access right.
    /// </summary>
    public static void WatchAll(Func<IEnumerable<(Game Game, GameSettings Settings)>> library)
    {
        _ui ??= SynchronizationContext.Current;
        _library = library;
        _scanTimer ??= new Timer(_ => Scan(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    static void Scan()
    {
        if (_library is null || Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            // The game folders, longest first (a game inside another game's folder is that game, not the outer one). A folder at
            // the top of a drive ("D:\Games") is never a single game.
            var games = _library()
                .Select(g => (g.Game, g.Settings, Dir: g.Game.InstallDir?.TrimEnd('\\', '/') ?? ""))
                .Where(g => g.Dir.Length > 3 && g.Dir.Count(c => c == '\\') >= 2 && !g.Game.NotInstalled && !g.Game.Installing)
                .OrderByDescending(g => g.Dir.Length).ToList();
            if (games.Count == 0) return;
            var alive = new HashSet<int>();
            var found = new Dictionary<string, (Game Game, GameSettings Settings, string Exe)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    int pid = p.Id;
                    if (pid <= 4) continue;
                    alive.Add(pid);
                    DateTime start;
                    try { start = p.StartTime; } catch { start = default; }
                    if (!Seen.TryGetValue(pid, out var seen) || seen.Start != start)
                        Seen[pid] = seen = (start, ImagePath(pid));
                    if (seen.Path is not { } path) continue;
                    foreach (var g in games)
                    {
                        if (!path.StartsWith(g.Dir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                        if (IsHelperExe(path)) break;
                        found.TryAdd(g.Game.Id, (g.Game, g.Settings, path));
                        break;
                    }
                }
            }
            foreach (var pid in Seen.Keys.Where(k => !alive.Contains(k)).ToList()) Seen.Remove(pid);

            foreach (var (id, (game, settings, exe)) in found)
            {
                bool added = false;
                lock (Sessions)
                {
                    if (!Sessions.TryGetValue(id, out var s) || s.State == GameRunState.Stopped)
                    {
                        Sessions[id] = new Session
                        {
                            Game = game, Settings = settings, Dir = Path.GetDirectoryName(exe)!, State = GameRunState.Running,
                            StartedAt = DateTime.UtcNow, RunningSince = DateTime.UtcNow, Process = Path.GetFileName(exe),
                        };
                        added = true;
                    }
                    else if (s.State == GameRunState.Starting)
                    {
                        // Started from Hybrid Launcher through a launcher: the game's own exe may sit in another folder than the one watched.
                        s.Dir = Path.GetDirectoryName(exe)!;
                    }
                }
                if (!added) continue;
                Log.Info($"Session: {game.Name} found running ({Path.GetFileName(exe)}), started outside Hybrid Launcher");
                if (game.Store != GameStore.Steam) OnUi(() =>
                {
                    settings.LastPlayed = DateTime.Now;
                    SettingsStore.Save();
                    PlaytimeChanged?.Invoke(game.Id);
                });
                Raise(id);
                EnsureTimer();
            }
        }
        catch (Exception ex) { Log.Warn($"Game watch: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _scanning, 0); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(nint process, int flags, System.Text.StringBuilder name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);

    /// <summary>
    /// Crash reporters, uninstallers and redistributables shipped in a game folder are not the game. Crashpad's handler is also
    /// run by many other apps (browsers, Discord, launchers) under the same name.
    /// </summary>
    internal static bool IsHelperExe(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        return name.Contains("crashreport", StringComparison.OrdinalIgnoreCase) || name.Contains("crashhandler", StringComparison.OrdinalIgnoreCase)
            || name.Contains("crashpad", StringComparison.OrdinalIgnoreCase) || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || name.Contains("redist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The processes whose exe sits right in this folder, matched by their real path (never by name alone).</summary>
    internal static List<Process> InFolder(string dir, bool helpers)
    {
        var list = new List<Process>();
        string[] names;
        try { names = Directory.GetFiles(dir, "*.exe").Select(Path.GetFileNameWithoutExtension).ToArray()!; }
        catch { return list; }
        foreach (var name in names)
        {
            if (!helpers && IsHelperExe(name)) continue;
            foreach (var p in Process.GetProcessesByName(name))
            {
                // A path Windows will not give is another program's process with the same name: never assume it is the game.
                var path = ImagePath(p.Id);
                if (path is not null && string.Equals(Path.GetDirectoryName(path), dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) list.Add(p);
                else p.Dispose();
            }
        }
        return list;
    }

    /// <summary>A process's exe path, asked with PROCESS_QUERY_LIMITED_INFORMATION (works for most processes, even elevated ones).</summary>
    internal static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Closes the game: asks its windows to close (the game can save), then ends every process still running from its folder
    /// after a few seconds. True when it is gone.
    /// </summary>
    public static async Task<bool> StopAsync(string gameId)
    {
        Session? s;
        lock (Sessions)
        {
            if (!Sessions.TryGetValue(gameId, out s) || s.State == GameRunState.Stopped) return true;
            s.State = GameRunState.Stopping;
        }
        Raise(gameId);
        var dir = s.Dir;
        bool gone = await Task.Run(async () =>
        {
            foreach (var p in Processes(dir)) using (p) { try { p.CloseMainWindow(); } catch { } }
            for (int i = 0; i < 16; i++)
            {
                await Task.Delay(500);
                if (OptiInstaller.RunningGameProcess(dir) is null) return true;
            }
            foreach (var p in Processes(dir))
                using (p)
                {
                    try { p.Kill(entireProcessTree: true); }
                    catch (Exception ex) { Log.Warn($"Stop {p.ProcessName}: {ex.Message}"); }
                }
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(300);
                if (OptiInstaller.RunningGameProcess(dir) is null) return true;
            }
            return false;
        });
        Log.Info($"Stop {s.Game.Name}: {(gone ? "closed" : "still running")}");
        if (gone) Tick();
        else lock (Sessions) if (s.State == GameRunState.Stopping) s.State = GameRunState.Running;
        Raise(gameId);
        return gone;
    }

    /// <summary>Every process whose exe sits in the game's folder (its crash handler included, so nothing is left behind).</summary>
    static List<Process> Processes(string dir) => InFolder(dir, helpers: true);

    static void Raise(string id) => OnUi(() => Changed?.Invoke(id));

    static void EnsureTimer()
    {
        lock (Sessions) _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>One look at every watched game: started, still running, or gone.</summary>
    static void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            List<Session> sessions;
            lock (Sessions) sessions = Sessions.Values.Where(s => s.State != GameRunState.Stopped).ToList();
            foreach (var s in sessions)
            {
                var running = OptiInstaller.RunningGameProcess(s.Dir);
                var now = DateTime.UtcNow;
                switch (s.State)
                {
                    case GameRunState.Starting when running is not null:
                        s.State = GameRunState.Running;
                        s.RunningSince = now;
                        s.Process = running;
                        Log.Info($"Session: {s.Game.Name} is running ({running})");
                        if (s.Game.Store != GameStore.Steam) OnUi(() =>
                        {
                            s.Settings.LastPlayed = DateTime.Now;
                            SettingsStore.Save();
                            PlaytimeChanged?.Invoke(s.Game.Id);
                        });
                        Raise(s.Game.Id);
                        break;

                    case GameRunState.Starting:
                        // Launchers (Steam, Epic, GOG Galaxy) can take a while to start the game itself; past 5 minutes it did not start.
                        if (now - s.StartedAt > TimeSpan.FromMinutes(5)) End(s, "never started");
                        break;

                    case GameRunState.Running or GameRunState.Stopping:
                        // Two misses in a row (4 s): some games restart their exe once (launcher → game).
                        s.Misses = running is null ? s.Misses + 1 : 0;
                        if (s.Game.Store != GameStore.Steam && s.RunningSince is { } since)
                        {
                            s.PendingMinutes += (now - since).TotalMinutes;
                            s.RunningSince = now;
                            if (s.PendingMinutes >= 1 || s.Misses >= 2) SavePlaytime(s);
                        }
                        if (s.Misses >= 2) End(s, "closed");
                        break;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Game sessions: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    static void SavePlaytime(Session s)
    {
        long whole = (long)Math.Floor(s.PendingMinutes);
        if (whole <= 0 && s.Misses < 2) return;
        s.PendingMinutes -= whole;
        OnUi(() =>
        {
            s.Settings.PlayMinutes += Math.Max(0, whole);
            s.Settings.LastPlayed = DateTime.Now;
            SettingsStore.Save();
            PlaytimeChanged?.Invoke(s.Game.Id);
        });
    }

    static void End(Session s, string why)
    {
        s.State = GameRunState.Stopped;
        Log.Info($"Session: {s.Game.Name} {why}");
        OnUi(() => HdrRestoreFor(s.Game.Id));
        Raise(s.Game.Id);
        OnUi(() => Ended?.Invoke(s.Game));
    }

    // ---------- Smart HDR ----------

    /// <summary>Whether any display of this PC can show HDR (null when Windows could not say).</summary>
    public static bool? HdrAvailable()
    {
        try { return Displays.Read().Any(d => d.HdrSupported); }
        catch { return null; }
    }

    /// <summary>Whether Windows HDR is on now on the display a game opens on (null when no display can show HDR).</summary>
    public static bool? HdrIsOn() => HdrDisplay()?.HdrOn;

    /// <summary>The display a game opens on: the main one when it can do HDR, else the first that can.</summary>
    static DisplayInfo? HdrDisplay()
    {
        try
        {
            var displays = Displays.Read();
            return displays.FirstOrDefault(d => d.Primary && d.HdrSupported) ?? displays.FirstOrDefault(d => d.HdrSupported);
        }
        catch (Exception ex)
        {
            Log.Warn($"Smart HDR displays: {ex.Message}");
            return null;
        }
    }

    static void HdrOn(Game game)
    {
        var d = HdrDisplay();
        if (d is null)
        {
            HdrProblem?.Invoke(L.T("Smart HDR: no display on this PC can show HDR."));
            return;
        }
        if (d.HdrOn) return; // Already on: it was the user's choice, so it stays on after the game too.
        try
        {
            DisplaySettings.SetHdr(d, true);
            var list = SettingsStore.Current.HdrRestore;
            list.RemoveAll(r => r.TargetId == d.TargetId && r.AdapterLow == d.AdapterLow && r.AdapterHigh == d.AdapterHigh);
            list.Add(new HdrRestore { GameId = game.Id, AdapterLow = d.AdapterLow, AdapterHigh = d.AdapterHigh, TargetId = d.TargetId });
            SettingsStore.Save();
            Log.Info($"Smart HDR: on for {game.Name} ({d.Name})");
        }
        catch (Exception ex)
        {
            Log.Warn($"Smart HDR on: {ex.Message}");
            HdrProblem?.Invoke(L.F("Smart HDR could not turn HDR on: {0}", ex.Message));
        }
    }

    /// <summary>Turns HDR back off on the displays Hybrid Launcher turned it on for this game, unless another running game still wants it.</summary>
    static void HdrRestoreFor(string gameId)
    {
        var list = SettingsStore.Current.HdrRestore;
        var mine = list.Where(r => r.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return;
        var others = Active().Where(id => !id.Equals(gameId, StringComparison.OrdinalIgnoreCase) && SettingsStore.Current.For(id).SmartHdr).ToList();
        foreach (var r in mine)
        {
            list.Remove(r);
            if (others.Count > 0)
            {
                // Handed over to the game still running: it turns HDR off when it closes.
                r.GameId = others[0];
                list.Add(r);
                continue;
            }
            Off(r);
        }
        SettingsStore.Save();
    }

    static void Off(HdrRestore r)
    {
        try
        {
            var d = Displays.Read().FirstOrDefault(x => x.TargetId == r.TargetId && x.AdapterLow == r.AdapterLow && x.AdapterHigh == r.AdapterHigh);
            if (d is { HdrOn: true })
            {
                DisplaySettings.SetHdr(d, false);
                Log.Info($"Smart HDR: off again ({d.Name})");
            }
        }
        catch (Exception ex) { Log.Warn($"Smart HDR off: {ex.Message}"); }
    }

    /// <summary>
    /// At start: HDR that Hybrid Launcher turned on for a game that is no longer running (Hybrid Launcher was closed or the PC slept meanwhile)
    /// goes back off.
    /// </summary>
    public static void RestoreLeftovers(Func<string, Game?> find)
    {
        _ui ??= SynchronizationContext.Current;
        var list = SettingsStore.Current.HdrRestore;
        if (list.Count == 0) return;
        foreach (var r in list.ToList())
        {
            var game = find(r.GameId);
            var settings = game is null ? null : SettingsStore.Current.For(game.Id);
            var exe = game is null ? null : Detection.GameScanner.EffectiveExe(game, settings!);
            var dir = exe is null ? null : Path.GetDirectoryName(exe);
            if (game is not null && dir is not null && OptiInstaller.RunningGameProcess(dir) is { } running)
            {
                // Still running: watch it, and turn HDR off when it closes.
                lock (Sessions)
                    Sessions[game.Id] = new Session { Game = game, Settings = settings!, Dir = dir, State = GameRunState.Running, StartedAt = DateTime.UtcNow, RunningSince = DateTime.UtcNow, Process = running };
                EnsureTimer();
                continue;
            }
            list.Remove(r);
            Off(r);
        }
        SettingsStore.Save();
    }
}
