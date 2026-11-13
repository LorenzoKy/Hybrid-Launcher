using System.Diagnostics;
using System.Runtime.InteropServices;
using HybridLauncher.Core;
using Microsoft.UI.Dispatching;

namespace HybridLauncher.Helpers;

/// <summary>
/// Vsync for everything Hybrid Launcher moves by itself, always on (there is no setting for it). Windows already composes the
/// app's windows once per refresh; what could still tear or judder was the app's own timing: motion stepped by 16 ms timers
/// (controller scrolling, the pop-up's fallback clock) lands between the display's refreshes, so on a 120 or 144 Hz screen a
/// step is shown twice or skipped, and a step taken mid-refresh shows half old, half new.
///
/// Here one thread waits for the display's own refresh (the next vertical blank, worked out from DWM's timing) and hands each subscriber one tick per refresh on its own thread, with the real time since
/// its last tick so motion is measured in time, not in ticks. A subscriber still busy with the last tick is not handed another:
/// ticks never pile up. The thread runs only while something is subscribed. At start DWM's composition is also given multimedia
/// scheduling (MMCSS), as video players do, so a busy CPU does not make it miss a refresh.
/// </summary>
public static class FrameSync
{
    sealed class Sub(DispatcherQueue queue, Action<double> tick)
    {
        public readonly DispatcherQueue Queue = queue;
        public readonly Action<double> Tick = tick;
        public long Last = Stopwatch.GetTimestamp();
        public int Pending;
        public volatile bool Gone;
    }

    static readonly List<Sub> Subs = [];
    static readonly AutoResetEvent Wake = new(false);
    static Thread? _thread;

    /// <summary>Ticks counted since start (the self-test checks the clock runs).</summary>
    public static long Ticks;

    /// <summary>The display's refresh period in ms as last measured (16.7 at 60 Hz).</summary>
    public static double PeriodMs { get; private set; } = 1000.0 / 60;

    /// <summary>Once at start: DWM composes with multimedia priority.</summary>
    public static void Init()
    {
        try { _ = DwmEnableMMCSS(true); }
        catch (Exception ex) { Log.Warn($"Frame sync: {ex.Message}"); }
    }

    /// <summary>
    /// <paramref name="tick"/> runs on <paramref name="queue"/> once per display refresh with the ms since its last run, until
    /// the returned handle is disposed.
    /// </summary>
    public static IDisposable Subscribe(DispatcherQueue queue, Action<double> tick)
    {
        var sub = new Sub(queue, tick);
        lock (Subs)
        {
            Subs.Add(sub);
            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "Frame sync", Priority = ThreadPriority.Highest };
                _thread.Start();
            }
        }
        Wake.Set();
        return new Handle(sub);
    }

    sealed class Handle(Sub sub) : IDisposable
    {
        public void Dispose()
        {
            sub.Gone = true;
            lock (Subs) Subs.Remove(sub);
        }
    }

    static void Loop()
    {
        using var timer = CreateWaitableTimerExW(0, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003 /* TIMER_ALL_ACCESS */);
        while (true)
        {
            Sub[] now;
            lock (Subs) now = [.. Subs];
            if (now.Length == 0)
            {
                // Nothing moving: the thread sleeps until something subscribes.
                Wake.WaitOne();
                continue;
            }
            WaitForRefresh(timer);
            Interlocked.Increment(ref Ticks);
            foreach (var s in now)
            {
                if (s.Gone || Interlocked.CompareExchange(ref s.Pending, 1, 0) != 0) continue;
                if (!s.Queue.TryEnqueue(DispatcherQueuePriority.High, () => Run(s))) s.Pending = 0;
            }
        }
    }

    static void Run(Sub s)
    {
        long t = Stopwatch.GetTimestamp();
        double ms = (t - s.Last) * 1000.0 / Stopwatch.Frequency;
        s.Last = t;
        s.Pending = 0;
        if (s.Gone) return;
        try { s.Tick(ms); }
        catch (Exception ex) { Log.Warn($"Frame sync tick: {ex.Message}"); }
    }

    /// <summary>
    /// Returns at the display's next refresh: DWM says when the last vertical blank was and how long a refresh lasts, and a
    /// high-resolution timer sleeps until the next one. (The compositor clock of Windows 11 was tried first: it stops while
    /// nothing on screen changes, so a waiter got about 9 ticks a second on a 180 Hz display.) The phase is kept even while
    /// DWM is idle, since the next blank is the last one plus whole periods.
    /// </summary>
    static void WaitForRefresh(SafeTimer timer)
    {
        var info = new byte[292];
        BitConverter.TryWriteBytes(info, 292);
        if (DwmGetCompositionTimingInfo(0, info) == 0)
        {
            long period = BitConverter.ToInt64(info, 12), vblank = BitConverter.ToInt64(info, 28);
            if (period > 0 && vblank > 0)
            {
                QueryPerformanceCounter(out long qpc);
                long next = vblank + period;
                if (next <= qpc) next += ((qpc - next) / period + 1) * period;
                // Never twice for the same blank: at least half a period after the last tick.
                if (_lastTick != 0 && next - _lastTick < period / 2) next += period;
                PeriodMs = period * 1000.0 / Stopwatch.Frequency;
                SleepFor((next - qpc) * 1000.0 / Stopwatch.Frequency, timer);
                _lastTick = next;
                return;
            }
        }
        SleepFor(PeriodMs, timer);
        _lastTick = 0;
    }

    static long _lastTick;

    static void SleepFor(double ms, SafeTimer timer)
    {
        if (ms <= 0) return;
        if (!timer.IsInvalid)
        {
            long due = -(long)(ms * 10_000); // relative, in 100 ns
            if (SetWaitableTimer(timer, ref due, 0, 0, 0, false))
            {
                _ = WaitForSingleObject(timer, 100);
                return;
            }
        }
        Thread.Sleep(Math.Max(1, (int)ms));
    }

    sealed class SafeTimer() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmEnableMMCSS(bool enable);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetCompositionTimingInfo(nint hwnd, byte[] info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern SafeTimer CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    static extern bool SetWaitableTimer(SafeTimer timer, ref long due, int period, nint completion, nint arg, bool resume);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(SafeTimer handle, uint ms);

    [DllImport("kernel32.dll")]
    static extern bool QueryPerformanceCounter(out long count);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);
}
