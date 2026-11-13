using System.Runtime.InteropServices;
using System.Text;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// A game in front must keep the focus. Windows tells this class each time the foreground window changes (a WinEvent hook on the
/// UI thread). When one of Hybrid Launcher's own windows (the main window, an achievement pop-up) becomes the foreground while a
/// game was in front, and nobody touched the keyboard or the mouse just before (alt-tab, a click on the tray icon), Hybrid
/// Launcher took it by itself: a game in exclusive full screen then minimizes or loses its input. The foreground is given back to
/// the game at once, and the log says which window took it and what the app was doing, so the cause can be found and fixed.
/// A press of the controller's Home button, which brings Hybrid Launcher forward on purpose, is let through (<see cref="Allow"/>).
/// </summary>
public static class FocusGuard
{
    static nint _hook;
    static WinEventProc? _proc; // kept alive while the hook exists
    static nint _lastOther;     // the last foreground window of another process
    static long _allowUntil;
    static string? _context;    // what the app was doing lately (set around pop-ups)
    static long _contextUntil;

    public static void Start()
    {
        if (_hook != 0) return;
        try
        {
            _proc = OnEvent;
            _hook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, 0, _proc, 0, 0, 0 /* OUTOFCONTEXT */);
            if (_hook == 0) Log.Warn("Focus guard: hook refused");
            var fg = GetForegroundWindow();
            if (fg != 0 && !Ours(fg)) _lastOther = fg;
        }
        catch (Exception ex) { Log.Warn($"Focus guard: {ex.Message}"); }
    }

    public static void Stop()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
        _hook = 0;
    }

    /// <summary>Hybrid Launcher comes forward on purpose for a while (the Home button, a notification clicked).</summary>
    public static void Allow(int ms = 2500) => _allowUntil = Environment.TickCount64 + ms;

    /// <summary>
    /// Names what the app is doing (e.g. "achievement pop-up opening"), for the log line if the focus is taken within the next
    /// moment (Windows reports the change a little later, on the UI thread's queue).
    /// </summary>
    public static void Note(string what)
    {
        _context = what;
        _contextUntil = Environment.TickCount64 + 1500;
    }

    static void OnEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == 0) return;
            if (!Ours(hwnd))
            {
                _lastOther = hwnd;
                return;
            }
            var from = _lastOther;
            if (from == 0 || !IsWindow(from) || Environment.TickCount64 < _allowUntil) return;
            // A hand on the keyboard or mouse in the last half second: alt-tab, a click on the window or the tray icon.
            if (MsSinceInput() < 500) return;
            // Only when the window left was a game's: a game being watched, or anything in full screen.
            bool game = GameSessions.Active().Count > 0 || FullScreen(from);
            if (!game) return;
            Log.Warn($"Focus guard: \"{Title(hwnd)}\" ({Class(hwnd)}) took the foreground from {ProcessName(from)} without any input" +
                     $"{(_context is null || Environment.TickCount64 > _contextUntil ? "" : $" right after {_context}")}; given back");
            ToFront(from);
        }
        catch (Exception ex) { Log.Warn($"Focus guard: {ex.Message}"); }
    }

    /// <summary>The foreground window covers its whole monitor (a game in full screen, exclusive or borderless).</summary>
    public static bool FullScreen(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(mon, ref info)) return false;
        return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }

    /// <summary>The window in front is another app in full screen (a game): Hybrid Launcher must not come forward by itself.</summary>
    public static bool GameInFront()
    {
        var fg = GetForegroundWindow();
        return fg != 0 && !Ours(fg) && (FullScreen(fg) || GameSessions.Active().Count > 0) && Class(fg) is not "Progman" and not "WorkerW";
    }

    static bool Ours(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    static double MsSinceInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) : double.MaxValue;
    }

    static void ToFront(nint hwnd)
    {
        // This process has the foreground now, so Windows lets it hand it on.
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
    }

    static string Title(nint hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string Class(nint hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string ProcessName(nint hwnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch { return "another app"; }
    }

    delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)] struct LastInputInfo { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RectI rect);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, StringBuilder text, int max);
}
