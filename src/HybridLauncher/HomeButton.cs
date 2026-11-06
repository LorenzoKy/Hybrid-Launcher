using System.Runtime.InteropServices;
using HybridLauncher.Core;
using Microsoft.UI.Dispatching;
using Microsoft.Win32.SafeHandles;

namespace HybridLauncher;

/// <summary>
/// The controller's Home button (Settings → Controller → Home button), read in the background whatever window is in front, so
/// it can bring Hybrid Launcher forward from a game. Xbox / Guide comes from XInput's extended state (what Game Bar reads too):
/// Xbox controllers, handhelds that show up as one, and PlayStation controllers through DS4Windows, DualSenseX or Steam Input.
/// PlayStation controllers connected directly are read through their HID reports (shared, read only: the game keeps reading
/// them as before). Nothing runs while the option is off.
/// </summary>
sealed class HomeButton : IDisposable
{
    readonly DispatcherQueue _ui;
    readonly Action _pressed;
    readonly Thread _xinput;
    readonly Thread? _hidScan;
    volatile bool _stop;
    readonly object _lock = new();
    readonly Dictionary<string, SafeFileHandle> _open = new(StringComparer.OrdinalIgnoreCase);

    // Press / hold tracking, shared by every source: one press of any of them counts once.
    readonly bool[] _down = new bool[5]; // 0-3: XInput slots, 4: any PlayStation controller
    long _downSince;
    bool _fired;

    public HomeButton(DispatcherQueue ui, Action pressed, bool playStation)
    {
        _ui = ui;
        _pressed = pressed;
        _xinput = new Thread(PollXInput) { IsBackground = true, Name = "Home button (XInput)", Priority = ThreadPriority.BelowNormal };
        _xinput.Start();
        if (playStation)
        {
            _hidScan = new Thread(ScanHid) { IsBackground = true, Name = "Home button (HID)", Priority = ThreadPriority.BelowNormal };
            _hidScan.Start();
        }
        Log.Info($"Home button: listening (XInput{(playStation ? ", PlayStation HID" : "")})");
    }

    public void Dispose()
    {
        _stop = true;
        lock (_lock)
        {
            // Ends the readers' pending reads, then closes the controllers.
            foreach (var h in _open.Values) try { _ = CancelIoEx(h, 0); h.Dispose(); } catch { }
            _open.Clear();
        }
    }

    /// <summary>One source's button went down or up; fires on the press, or once it has been held long enough.</summary>
    void Report(int source, bool down)
    {
        lock (_down)
        {
            bool before = _down.Any(d => d);
            _down[source] = down;
            bool now = _down.Any(d => d);
            if (now && !before) { _downSince = Environment.TickCount64; _fired = false; }
            if (!now) { _fired = false; return; }
            bool hold = AppState.Settings.Controller.HomeTrigger == "Hold";
            if (_fired || hold && Environment.TickCount64 - _downSince < 500) return;
            _fired = true;
        }
        _ui.TryEnqueue(() => _pressed());
    }

    // ---------- XInput (Guide) ----------

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    /// <summary>XInputGetStateEx: the state with the Guide button (0x0400), which the documented call leaves out.</summary>
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    static extern int XInputGetStateEx(uint index, out XInputState state);

    void PollXInput()
    {
        // A slot with nothing connected is slow to ask about: those are asked every two seconds, connected ones 30 times a second.
        var nextTry = new long[4];
        try
        {
            while (!_stop)
            {
                long now = Environment.TickCount64;
                for (uint i = 0; i < 4; i++)
                {
                    if (now < nextTry[i]) continue;
                    if (XInputGetStateEx(i, out var st) == 0)
                        Report((int)i, (st.Buttons & 0x0400) != 0);
                    else
                    {
                        nextTry[i] = now + 2000;
                        Report((int)i, false);
                    }
                }
                Thread.Sleep(33);
            }
        }
        catch (Exception ex) { Log.Warn($"Home button (XInput): {ex.Message}"); }
    }

    // ---------- PlayStation controllers over HID ----------

    /// <summary>Sony's DualShock 4 (two models and the wireless adapter), DualSense and DualSense Edge.</summary>
    static readonly int[] DualShock4 = [0x05C4, 0x09CC, 0x0BA0], DualSense = [0x0CE6, 0x0DF2];

    void ScanHid()
    {
        try
        {
            while (!_stop)
            {
                foreach (var path in HidPaths())
                {
                    lock (_lock) if (_open.ContainsKey(path)) continue;
                    if (!path.Contains("vid_054c", StringComparison.OrdinalIgnoreCase)) continue;
                    int pid = Pid(path);
                    bool ds4 = DualShock4.Contains(pid), ds = DualSense.Contains(pid);
                    if (!ds4 && !ds) continue;
                    var h = CreateFile(path, 0x80000000 /* GENERIC_READ */, 3 /* SHARE_READ | SHARE_WRITE */, 0, 3 /* OPEN_EXISTING */, 0, 0);
                    if (h.IsInvalid) { h.Dispose(); continue; } // held exclusively (DS4Windows' exclusive mode, HidHide): its Xbox twin is read instead
                    lock (_lock) _open[path] = h;
                    new Thread(() => ReadSony(path, h, ds)) { IsBackground = true, Name = "Home button (PlayStation)", Priority = ThreadPriority.BelowNormal }.Start();
                }
                for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100);
            }
        }
        catch (Exception ex) { Log.Warn($"Home button (HID scan): {ex.Message}"); }
    }

    void ReadSony(string path, SafeFileHandle handle, bool dualSense)
    {
        Log.Info($"Home button: reading a {(dualSense ? "DualSense" : "DualShock 4")}");
        var buf = new byte[600];
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, 0, false);
            while (!_stop)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                if (PsButton(buf, n, dualSense) is { } down) Report(4, down);
            }
        }
        catch (Exception) when (_stop) { }
        catch (Exception ex) { Log.Info($"Home button: a PlayStation controller went away ({ex.Message})"); }
        finally
        {
            Report(4, false);
            lock (_lock) _open.Remove(path);
            try { handle.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// The PS button in an input report (byte 0 is the report id), or null for a report without it. DualShock 4: report 1 (USB, and
    /// Bluetooth before it is set up) byte 7, report 0x11 (Bluetooth) byte 9. DualSense: report 1 byte 10 over USB (64 bytes),
    /// byte 7 in the short Bluetooth one; report 0x31 (Bluetooth) byte 11. Bit 0 each time.
    /// </summary>
    internal static bool? PsButton(byte[] r, int n, bool dualSense)
    {
        int at = (dualSense, r[0]) switch
        {
            (false, 0x01) => 7,
            (false, 0x11) => 9,
            (true, 0x01) => n >= 64 ? 10 : 7,
            (true, 0x31) => 11,
            _ => -1,
        };
        return at < 0 || at >= n ? null : (r[at] & 1) != 0;
    }

    static int Pid(string path)
    {
        int i = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 8 <= path.Length && int.TryParse(path.AsSpan(i + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var pid) ? pid : -1;
    }

    /// <summary>The device paths of every HID device present.</summary>
    static List<string> HidPaths()
    {
        var list = new List<string>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, 0, 0, 0x2 | 0x10 /* PRESENT | DEVICEINTERFACE */);
        if (set == -1 || set == 0) return list;
        try
        {
            var data = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
            var detail = Marshal.AllocHGlobal(2048);
            try
            {
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref data); i++)
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, 2048, out _, 0) && Marshal.PtrToStringUni(detail + 4) is { Length: > 0 } p)
                        list.Add(p);
                }
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceInterfaceData { public int Size; public Guid InterfaceClassGuid; public int Flags; public nint Reserved; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern nint SetupDiGetClassDevs(ref Guid guid, nint enumerator, nint parent, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterfaceData data, nint detail, int size, out int required, nint info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle handle, nint overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
