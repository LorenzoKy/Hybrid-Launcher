using System.Runtime.InteropServices;
using Microsoft.Win32;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// Hybrid Launcher's icon in the notification area (by the clock), drawn with Shell_NotifyIcon on a hidden message window of the UI
/// thread: a click opens the window, the right-click menu opens it or quits Hybrid Launcher. WinUI 3 has no tray control of its own.
/// </summary>
public sealed class Tray : IDisposable
{
    const int WM_APP_TRAY = 0x8000 + 1, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203, WM_COMMAND = 0x0111;
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const int CmdOpen = 1, CmdQuit = 2;

    readonly WndProc _proc; // Kept alive: Windows calls it for as long as the window exists.
    readonly nint _hwnd;
    readonly nint _icon;
    readonly Action _open, _quit;
    readonly int _taskbarCreated;
    bool _added;

    public Tray(string tooltip, Action open, Action quit)
    {
        _open = open;
        _quit = quit;
        _proc = Proc;
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "HybridTrayWindow" + Environment.ProcessId,
        };
        RegisterClassEx(ref cls);
        _hwnd = CreateWindowEx(0, cls.lpszClassName!, "Hybrid Launcher tray", 0, 0, 0, 0, 0, 0, 0, cls.hInstance, 0);
        // Explorer restarting drops every tray icon: it sends this message so they can be added again.
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        int size = GetSystemMetrics(49 /* SM_CXSMICON */);
        _icon = File.Exists(ico) ? LoadImage(0, ico, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */) : 0;
        Tooltip = tooltip;
        Add();
    }

    public string Tooltip { get; set; }

    void Add()
    {
        var data = Data();
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_added) Log.Warn("Tray icon could not be added");
    }

    NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = Tooltip.Length > 120 ? Tooltip[..120] : Tooltip,
    };

    public void Update(string tooltip)
    {
        Tooltip = tooltip;
        if (!_added) return;
        var data = Data();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    nint Proc(nint hwnd, int msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_APP_TRAY)
            {
                int ev = (int)(lParam & 0xFFFF);
                if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) _open();
                else if (ev == WM_RBUTTONUP) ShowMenu();
                return 0;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                Add();
                return 0;
            }
        }
        catch (Exception ex) { Log.Error("Tray", ex); }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, CmdOpen, L.T("Open Hybrid Launcher"));
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, CmdQuit, L.T("Quit Hybrid Launcher"));
        SetMenuDefaultItem(menu, CmdOpen, 0);
        GetCursorPos(out var pt);
        // The menu closes on a click elsewhere only if the window is in front first (documented TrackPopupMenu quirk).
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(menu, 0x100 /* TPM_RETURNCMD */ | 0x20 /* TPM_RIGHTALIGN */, pt.X, pt.Y, 0, _hwnd, 0);
        DestroyMenu(menu);
        if (cmd == CmdOpen) _open();
        else if (cmd == CmdQuit) _quit();
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != 0) DestroyIcon(_icon);
        if (_hwnd != 0) DestroyWindow(_hwnd);
    }

    delegate nint WndProc(nint hwnd, int msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern nint DefWindowProc(nint hwnd, int msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint LoadImage(nint inst, string name, int type, int cx, int cy, int load);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(nint menu, int flags, int id, string? text);
    [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(nint menu, int item, int byPos);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(nint menu, int flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
}

/// <summary>Start with Windows: a value under the user's Run key, starting Hybrid Launcher in the notification area.</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = AppInfo.Name;
    /// <summary>The value under the app's earlier name: it pointed at the old exe, so it is written again under the new one.</summary>
    const string PreviousValueName = AppInfo.PreviousName;

    public static bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is string) return true;
            if (key?.GetValue(PreviousValueName) is not string) return false;
            Set(true);
            return true;
        }
    }

    /// <summary>At start: an entry under the earlier name is moved to the new one (reading <see cref="IsOn"/> does it).</summary>
    public static void CarryOver()
    {
        try { _ = IsOn; } catch (Exception ex) { Log.Warn($"Start with Windows: {ex.Message}"); }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        key.DeleteValue(PreviousValueName, false);
        if (on) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(ValueName, false);
    }
}
