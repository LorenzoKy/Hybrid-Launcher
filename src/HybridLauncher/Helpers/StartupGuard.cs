using System.Diagnostics;
using System.Runtime.InteropServices;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// What happens when Hybrid Launcher is started while another copy holds the single-instance lock, and what the user is told
/// when it cannot start at all (2.3-rc.2).
///
/// A double-click on the exe sometimes did nothing the first time and worked the second: the copy already running (in the
/// notification area, started with Windows) was asked to show itself, but Windows lets only the app the user is working with
/// bring a window to the front, and a program running in the background is not that one: the window stayed hidden or came up
/// behind. The new copy now hands its right to the front to the running one first (AllowSetForegroundWindow) and waits for it
/// to say its window is up. A copy that does not answer is either ending (closed a moment ago) - the new copy then waits for
/// its lock and starts normally - or stuck, and the user is told so and may end it.
/// </summary>
public static class StartupGuard
{
    const int AsfwAny = -1;
    const uint MbOk = 0x0, MbYesNo = 0x4, MbIconError = 0x10, MbIconWarning = 0x30, MbSetForeground = 0x10000, MbTopMost = 0x40000;
    const int IdYes = 6;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    /// <summary>
    /// Asks the running copy to show its window and waits until it says it has. False when it did not answer in time (a copy
    /// that predates the answer is trusted, as before).
    /// </summary>
    public static bool HandOver(string lockName)
    {
        // This copy was started by the user (Explorer, a shortcut): it may give its right to the front to the running one.
        try { AllowSetForegroundWindow(AsfwAny); } catch { }
        EventWaitHandle.TryOpenExisting(lockName + "-shown", out var shown);
        using (shown)
        {
            if (!EventWaitHandle.TryOpenExisting(lockName + "-show", out var show))
            {
                // Still starting (its signals are made right after the lock): it opens its window by itself.
                if (shown is not null) return true;
                SingleInstance.FocusOther();
                return true;
            }
            using (show) show.Set();
            if (shown is null) { SingleInstance.FocusOther(); return true; }
            return shown.WaitOne(TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>Waits for the lock of a copy that is ending. True once this copy holds it.</summary>
    public static bool TakeOver(Mutex lockHandle, TimeSpan wait)
    {
        try { return lockHandle.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>
    /// The running copy neither answers nor ends: the user is told, and may end it (copies of this same exe only). True when
    /// it was ended and this copy now holds the lock.
    /// </summary>
    public static bool AskToEndStuckCopy(Mutex lockHandle)
    {
        Log.Warn("Start: another copy holds the lock and does not answer");
        int answer = MessageBoxW(0,
            L.T("Hybrid Launcher is already running but does not answer, so its window cannot be shown.") + "\n\n" +
            L.T("End the copy that does not answer and start again?"),
            "Hybrid Launcher", MbYesNo | MbIconWarning | MbSetForeground | MbTopMost);
        if (answer != IdYes) return false;
        int ended = 0;
        try
        {
            using var self = Process.GetCurrentProcess();
            var path = Environment.ProcessPath;
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
                using (p)
                {
                    if (p.Id == self.Id) continue;
                    string? other = null;
                    try { other = p.MainModule?.FileName; } catch { }
                    if (path is null || other is null || !string.Equals(other, path, StringComparison.OrdinalIgnoreCase)) continue;
                    try { p.Kill(); p.WaitForExit(5000); ended++; } catch (Exception ex) { Log.Warn($"Start: could not end copy {p.Id}: {ex.Message}"); }
                }
        }
        catch (Exception ex) { Log.Warn($"Start: ending the stuck copy: {ex.Message}"); }
        Log.Warn($"Start: ended {ended} copy(ies) that did not answer");
        if (TakeOver(lockHandle, TimeSpan.FromSeconds(5))) return true;
        Problem(L.T("Hybrid Launcher is already running but does not answer, and it could not be ended."),
            L.T("End \"HybridLauncher\" in Task Manager (Ctrl+Shift+Esc), then start Hybrid Launcher again."));
        return false;
    }

    /// <summary>Hybrid Launcher could not start: why, what to try, and where the log is. Works before any window exists.</summary>
    public static void Failed(Exception ex)
    {
        Log.Error("Start failed", ex);
        Log.Flush();
        Problem(L.T("Hybrid Launcher could not start."),
            L.F("Reason: {0}", ex.Message) + "\n\n" +
            L.T("Try starting it again. If it keeps happening, restart Windows or extract the whole download again, and send the log with a bug report.") +
            "\n\n" + L.F("Log: {0}", Log.CurrentFile));
    }

    static void Problem(string what, string detail)
    {
        try { MessageBoxW(0, what + "\n\n" + detail, "Hybrid Launcher", MbOk | MbIconError | MbSetForeground | MbTopMost); }
        catch { }
    }
}
