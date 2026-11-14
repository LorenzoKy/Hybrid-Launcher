using System.Diagnostics;
using System.Runtime.InteropServices;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// The way out of a UI thread that WinUI stopped serving: the thread waits for messages, idle, while the work queued for it
/// (DispatcherQueue: drawing, input, every page update) never runs again. Window activation still reaches it, so Windows does
/// not call the window "Not responding", but nothing on it moves until the process ends. Seen in 2.1.0; the same stop
/// is reported against the Windows App SDK itself (microsoft/WindowsAppSDK#6797), with no
/// fix and no way to start the queue again from the app.
///
/// <see cref="UiPerf"/>'s watchdog decides when it has happened: the queue has run nothing for a minute and the UI thread has
/// used no CPU meanwhile (a thread that is busy with a long job uses CPU; this one only waits). Hybrid Launcher then saves what
/// it has, starts a new copy of itself (in the notification area when the window was hidden) and ends the stopped one, so the
/// window comes back in a few seconds instead of staying frozen until Task Manager ends it.
/// </summary>
static class UiRecovery
{
    /// <summary>Command line switch of the new copy: the process id of the stopped one, which it waits to end first.</summary>
    public const string Switch = "--recover";

    /// <summary>The new copy starts in the notification area: the stopped one was hidden there.</summary>
    public const string HiddenSwitch = "--recover-hidden";

    static int _started;

    public static void Restart(bool hidden, Action? dropTrayIcon)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            Log.Warn($"UI thread stopped for good (WinUI runs no queued work and the thread is idle): starting Hybrid Launcher again{(hidden ? " in the notification area" : "")}");
            SettingsStore.Flush();
            Log.Flush();
            var args = $"{Switch} {Environment.ProcessId}" + (hidden ? " " + HiddenSwitch : "");
            using var next = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
            if (next is null) { Log.Warn("Recovery: the new copy did not start, the stopped one is left as it is"); Log.Flush(); return; }
            // The icon by the clock belongs to this process: removed now, or a dead one stays there until the pointer passes over it.
            try { dropTrayIcon?.Invoke(); } catch { }
            Log.Flush();
        }
        catch (Exception ex)
        {
            try { Log.Warn($"Recovery failed: {ex.Message}"); Log.Flush(); } catch { }
            return;
        }
        // Not Environment.Exit: its shutdown waits for the UI thread, which is the one that stopped.
        using var self = Process.GetCurrentProcess();
        self.Kill();
    }

    /// <summary>
    /// The new copy: waits (up to 15 s) for the stopped one to end, so it does not find its single-instance lock and hand over
    /// to a window that cannot open.
    /// </summary>
    public static void WaitForStoppedCopy(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid) || pid == Environment.ProcessId) return;
        try
        {
            using var old = Process.GetProcessById(pid);
            if (!old.WaitForExit(15000)) Log.Warn($"Recovery: the stopped copy ({pid}) is still running after 15 s");
        }
        catch (ArgumentException) { } // already gone
        catch (Exception ex) { Log.Warn($"Recovery: waiting for the stopped copy: {ex.Message}"); }
    }

    public static bool StartedHidden(string[] args) => args.Contains(HiddenSwitch, StringComparer.OrdinalIgnoreCase);

    public static bool IsRecovery(string[] args) => args.Contains(Switch, StringComparer.OrdinalIgnoreCase);

    // ---------- the UI thread's CPU time ----------

    static nint _thread;

    /// <summary>Opens the UI thread (by its native id) so the watchdog can read how much CPU it used.</summary>
    public static void TrackThread(uint nativeId)
    {
        if (_thread == 0) _thread = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, nativeId);
    }

    /// <summary>CPU time (kernel and user) the UI thread has used so far, in milliseconds; -1 when it cannot be read.</summary>
    public static double ThreadCpuMs()
    {
        if (_thread == 0 || !GetThreadTimes(_thread, out _, out _, out long kernel, out long user)) return -1;
        return (kernel + user) / 10_000.0;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}
