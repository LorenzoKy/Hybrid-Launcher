using System.Diagnostics;
using System.Runtime.InteropServices;
using HybridLauncher.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Helpers;

/// <summary>
/// How responsive the window really is, measured from inside: what the user feels as "lag" is one of three things, and each is
/// recorded separately so a report says which one it was.
///
/// 1. The UI thread busy (a stall): a watchdog thread asks the UI thread for a tiny job every few ms; a job that runs late means
///    the thread was doing something else. While a stall lasts the watchdog reads which <see cref="Scope"/> the UI thread is in,
///    so the log names the code (a page being built, a list redrawn) instead of only saying "something was slow".
/// 2. An interaction answered late: for every click, key or controller press, the time from the input to the first frame drawn
///    after the UI thread was free again (what the eye sees), and the frames missed in the half second after it (a page that
///    appears, then hitches while it fills).
/// 3. Frames missed while nothing was asked (an animation stuttering): the frame clock's gaps while the frame clock is kept awake.
///
/// Always on at a coarse level (stalls of 120 ms and more, interactions slower than 200 ms go to the log, a line each, so a user's
/// log shows where it hurt). HL_PERF=1 (the self-test sets it) logs every interaction and stalls from 24 ms, and keeps the numbers
/// for <see cref="Summary"/>. The cost is one wake-up of a background thread every 8 ms and a few handlers on the root.
/// </summary>
public static class UiPerf
{
    /// <summary>Every interaction and smaller stalls are logged (HL_PERF=1 or the self-test).</summary>
    public static bool Detailed { get; private set; } = Environment.GetEnvironmentVariable("HL_PERF") == "1";

    const double CoarseStallMs = 120, DetailedStallMs = 24;
    const double CoarseSlowMs = 200;

    static DispatcherQueue? _ui;
    static UIElement? _root;
    static Func<string>? _where;
    static int _uiThread;

    // The UI thread's innermost scope, read by the watchdog thread during a stall.
    static volatile string? _scope;

    // Numbers kept for the summary (detailed mode).
    static readonly List<(string Kind, string What, double FrameMs, double BusyMs, int Dropped)> Interactions = [];
    static readonly List<(double Ms, string Scope)> Stalls = [];
    static readonly Dictionary<string, (int Count, double Total, double Worst)> ScopeTimes = new(StringComparer.Ordinal);

    public static void EnableDetailed() => Detailed = true;

    /// <param name="where">Describes the page shown (for the log lines).</param>
    public static void Attach(DispatcherQueue ui, UIElement root, Func<string> where)
    {
        if (_ui is not null) return;
        _ui = ui;
        _root = root;
        _where = where;
        _uiThread = Environment.CurrentManagedThreadId;
        Log.Info($"UI thread: managed {_uiThread}, native {GetCurrentThreadId()} (for traces)");
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) => OnInput("click", e.OriginalSource, e.GetCurrentPoint(null).Timestamp)), true);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s, e) => OnInput("key " + e.Key, e.OriginalSource, 0)), true);
        new Thread(Watchdog) { IsBackground = true, Name = "UI watchdog", Priority = ThreadPriority.AboveNormal }.Start();
    }

    // ---------- scopes ----------

    /// <summary>
    /// Marks a piece of UI-thread work: a stall while it runs names it, and in detailed mode its time is added up per name.
    /// <c>using var _ = UiPerf.Scope("GamePage.Fill");</c>
    /// </summary>
    public static ScopeToken Scope(string name)
    {
        if (Environment.CurrentManagedThreadId != _uiThread) return default;
        var outer = _scope;
        _scope = outer is null ? name : outer + " > " + name;
        return new ScopeToken(name, outer, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// In detailed mode (the perf and snappy self-tests), lays <paramref name="root"/> out now, inside a scope named
    /// "Layout &lt;what&gt;": XAML's own layout otherwise shows up in the stall log only as "no scope", with nothing to say which
    /// change caused it. Outside detailed mode it does nothing (the frame lays the page out as usual).
    /// </summary>
    public static void Layout(UIElement? root, string what)
    {
        if (!Detailed || root is null) return;
        using var _ = Scope("Layout " + what);
        try { root.UpdateLayout(); } catch { }
    }

    public readonly struct ScopeToken(string? name, string? outer, long started) : IDisposable
    {
        public void Dispose()
        {
            if (name is null) return;
            _scope = outer;
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!Detailed) return;
            lock (ScopeTimes)
            {
                ScopeTimes.TryGetValue(name, out var t);
                ScopeTimes[name] = (t.Count + 1, t.Total + ms, Math.Max(t.Worst, ms));
            }
            if (ms >= 16) Log.Info($"Perf scope {name}: {ms:0.0} ms" + (outer is null ? "" : $" (in {outer})"));
        }
    }

    // ---------- watchdog ----------

    static void Watchdog()
    {
        using var timer = CreateWaitableTimerExW(0, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);
        var done = new ManualResetEventSlim(false);
        long reported = 0;
        while (true)
        {
            // Hidden (in the tray while a game runs): checked every two seconds and only for a long stall, so a UI thread that
            // stops while nobody looks is still caught (rc.10: one stopped during a game and was only seen afterwards).
            bool hidden = MediaPolicy.WindowHidden && !Detailed;
            if (hidden) Thread.Sleep(2000);
            done.Reset();
            long sent = Stopwatch.GetTimestamp();
            if (_ui is null || !_ui.TryEnqueue(DispatcherQueuePriority.High, () => done.Set())) return;
            // While the job waits, the scope the UI thread is in is sampled: the longest-held one names the stall.
            string? seen = null;
            int seenCount = 0;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            while (!done.Wait(4))
            {
                if (Stopwatch.GetElapsedTime(sent).TotalMilliseconds < 12) continue;
                var s = _scope ?? "(no scope: XAML layout, rendering or an unmarked handler)";
                counts[s] = counts.TryGetValue(s, out var c) ? c + 1 : 1;
                if (counts[s] > seenCount) { seenCount = counts[s]; seen = s; }
                if (Stopwatch.GetElapsedTime(sent).TotalSeconds > 20) break;
            }
            double ms = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
            double limit = Detailed ? DetailedStallMs : CoarseStallMs;
            if (hidden && ms < 5000) { SleepMs(timer, 8); continue; }
            // A UI thread stopped for good: a small dump of every thread's stack next to the log, once per run, to find
            // where it waits (open it with a debugger, or Tools\windows\minidump-stack-scan.py --all-threads).
            if (ms >= 19000 && !_dumped && !App.ShuttingDown)
            {
                _dumped = true;
                WriteHangDump();
            }
            if (ms >= limit && !App.ShuttingDown)
            {
                var all = counts.Count > 1 ? " [" + string.Join(", ", counts.OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Key} x{k.Value}")) + "]" : "";
                // Not more than a line a second outside detailed mode: a hung thread must not fill the log.
                if (Detailed || Environment.TickCount64 - reported > 1000)
                {
                    reported = Environment.TickCount64;
                    Log.Info($"UI stall {ms:0} ms in {seen ?? "(too short to sample)"}{all}");
                }
                if (Detailed) lock (Stalls) Stalls.Add((ms, seen ?? "?"));
            }
            SleepMs(timer, 8);
        }
    }

    // ---------- interactions ----------

    static long _lastInput;

    static void OnInput(string kind, object? source, ulong inputMicros)
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            // Two inputs within a frame (a key repeat, a release right after a press) count as one.
            if (Stopwatch.GetElapsedTime(_lastInput, now).TotalMilliseconds < 8) return;
            _lastInput = now;
            // The input's own time (microseconds since boot, like the performance counter): the handlers that ran before this
            // one (a button's Click) are part of the wait.
            double handledMs = 0;
            if (inputMicros > 0)
            {
                double nowMicros = now * 1_000_000.0 / Stopwatch.Frequency;
                double d = (nowMicros - inputMicros) / 1000.0;
                if (d is >= 0 and < 5000) handledMs = d;
            }
            long start = now - (long)(handledMs * Stopwatch.Frequency / 1000);
            string what = Describe(source as DependencyObject);
            string page = _where?.Invoke() ?? "";
            Track(kind, what, page, start);
        }
        catch (Exception ex) { Log.Warn($"UI perf: {ex.Message}"); }
    }

    /// <summary>Gamepad presses come from a poller, not the root: measured the same way.</summary>
    public static void Controller(string button)
    {
        if (_ui is null) return;
        Track("pad " + button, "", _where?.Invoke() ?? "", Stopwatch.GetTimestamp());
    }

    static void Track(string kind, string what, string page, long start)
    {
        if (_ui is null) return;
        FrameWarmth.Poke();
        double busy = -1, frame = -1;
        var gaps = new List<double>();
        long last = 0;
        int frames = 0;
        // The UI thread is free again when a low-priority job runs (after input, layout and anything the click queued).
        _ui.TryEnqueue(DispatcherQueuePriority.Low, () => busy = Ms(start));
        EventHandler<object>? onFrame = null;
        onFrame = (_, _) =>
        {
            long t = Stopwatch.GetTimestamp();
            if (busy >= 0 && frame < 0) frame = Ms(start);
            if (last != 0 && frame >= 0) gaps.Add((t - last) * 1000.0 / Stopwatch.Frequency);
            last = t;
            frames++;
            // Watched for half a second after the first frame that shows the answer.
            if (frame >= 0 && Ms(start) > frame + 500)
            {
                CompositionTarget.Rendering -= onFrame;
                Report(kind, what, page, busy, frame, gaps);
            }
            else if (Ms(start) > 5000)
            {
                CompositionTarget.Rendering -= onFrame;
                Report(kind, what, page, busy, frame, gaps);
            }
        };
        CompositionTarget.Rendering += onFrame;
    }

    static void Report(string kind, string what, string page, double busy, double frame, List<double> gaps)
    {
        double period = FrameSync.PeriodMs;
        int dropped = 0;
        double worst = 0;
        foreach (var g in gaps)
        {
            if (g > period * 1.6) dropped += (int)Math.Round(g / period) - 1;
            worst = Math.Max(worst, g);
        }
        bool slow = frame > CoarseSlowMs || busy > CoarseSlowMs;
        if (!Detailed && !slow) return;
        var line = $"{(slow ? "Slow " : "")}{kind} on {(what.Length > 0 ? what : "?")} ({page}): UI free after {busy:0} ms, shown at {frame:0} ms" +
                   (dropped > 0 ? $", then {dropped} frame(s) missed (worst gap {worst:0} ms)" : ", then smooth");
        Log.Info("Perf " + line);
        if (Detailed) lock (Interactions) Interactions.Add((kind.Split(' ')[0], what, frame, busy, dropped));
    }

    static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary>What was clicked, in words: the nearest named element (or a button's or list item's text) and its type.</summary>
    static string Describe(DependencyObject? d)
    {
        string type = d?.GetType().Name ?? "";
        for (int i = 0; d is not null && i < 12; i++, d = VisualTreeHelper.GetParent(d))
        {
            string name = d is FrameworkElement fe ? fe.Name : "";
            string auto = d is UIElement u ? AutomationProperties.GetName(u) : "";
            string text = d switch
            {
                ContentControl { Content: string s } => s,
                TextBlock tb when i < 3 => tb.Text,
                _ => "",
            };
            if (name.Length > 0 && !name.StartsWith("Part", StringComparison.Ordinal) && name != "ContentPresenter")
                return $"{d.GetType().Name} {name}" + (text.Length > 0 ? $" \"{Short(text)}\"" : "");
            if (auto.Length > 0) return $"{d.GetType().Name} \"{Short(auto)}\"";
            if (text.Length > 0 && d is not TextBlock) return $"{d.GetType().Name} \"{Short(text)}\"";
            if (d is NavigationViewItem or ListViewItem or GridViewItem or Button or ToggleSwitch or ComboBox or CheckBox)
                return d.GetType().Name;
        }
        return type;
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    // ---------- summary ----------

    /// <summary>Detailed mode: the numbers of the session in a few lines (the self-test writes them at the end).</summary>
    public static void Summary()
    {
        if (!Detailed) return;
        lock (Interactions)
        {
            foreach (var kind in Interactions.GroupBy(i => i.Kind))
            {
                var f = kind.Select(i => i.FrameMs).Where(x => x >= 0).OrderBy(x => x).ToList();
                if (f.Count == 0) continue;
                Log.Info($"Perf summary {kind.Key}: {f.Count} interactions, shown at median {f[f.Count / 2]:0} ms, p90 {f[(int)(f.Count * 0.9)]:0} ms, worst {f[^1]:0} ms; " +
                         $"{kind.Count(i => i.Dropped > 0)} followed by missed frames ({kind.Sum(i => i.Dropped)} in all)");
            }
            foreach (var slow in Interactions.OrderByDescending(i => i.FrameMs).Take(8))
                Log.Info($"Perf slowest: {slow.Kind} {slow.What} shown at {slow.FrameMs:0} ms (UI busy {slow.BusyMs:0} ms, {slow.Dropped} missed after)");
        }
        lock (Stalls)
        {
            Log.Info($"Perf summary stalls: {Stalls.Count} of {DetailedStallMs:0} ms or more, {Stalls.Sum(s => s.Ms):0} ms in all");
            foreach (var g in Stalls.GroupBy(s => s.Scope).OrderByDescending(g => g.Sum(s => s.Ms)).Take(10))
                Log.Info($"Perf stalls in {g.Key}: {g.Count()}, {g.Sum(s => s.Ms):0} ms in all, worst {g.Max(s => s.Ms):0} ms");
        }
        lock (ScopeTimes)
            foreach (var (name, t) in ScopeTimes.OrderByDescending(k => k.Value.Total).Take(15))
                Log.Info($"Perf scope total {name}: {t.Count} x, {t.Total:0} ms in all, worst {t.Worst:0.0} ms");
    }

    /// <summary>Clears the kept numbers (the self-test measures one part at a time).</summary>
    public static void Reset()
    {
        lock (Interactions) Interactions.Clear();
        lock (Stalls) Stalls.Clear();
        lock (ScopeTimes) ScopeTimes.Clear();
    }

    // ---------- native ----------

    static void SleepMs(SafeTimer timer, double ms)
    {
        if (!timer.IsInvalid)
        {
            long due = -(long)(ms * 10_000);
            if (SetWaitableTimer(timer, ref due, 0, 0, 0, false)) { _ = WaitForSingleObject(timer, 100); return; }
        }
        Thread.Sleep((int)ms);
    }

    sealed class SafeTimer() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static bool _dumped;

    static void WriteHangDump()
    {
        try
        {
            var file = Path.Combine(AppInfo.LogsDir, $"hang-{DateTime.Now:yyyy-MM-dd HH-mm-ss}.dmp");
            using var process = Process.GetCurrentProcess();
            using var fs = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            // MiniDumpWithDataSegs | MiniDumpWithHandleData | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules: the stacks
            // and what they wait on, a few MB.
            bool ok = MiniDumpWriteDump(process.Handle, (uint)process.Id, fs.SafeFileHandle, 0x1 | 0x4 | 0x1000 | 0x20, 0, 0, 0);
            Log.Warn(ok ? $"UI thread stopped: thread stacks written to {Path.GetFileName(file)}" : $"UI thread stopped: no dump ({Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex) { Log.Warn($"UI thread stopped, dump failed: {ex.Message}"); }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    static extern bool MiniDumpWriteDump(nint process, uint processId, Microsoft.Win32.SafeHandles.SafeFileHandle file, int type, nint exception, nint user, nint callback);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern SafeTimer CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(SafeTimer timer, ref long due, int period, nint completion, nint arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(SafeTimer handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}
