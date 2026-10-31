using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HybridLauncher.Core.Hardware;

/// <summary>A display mode: resolution and refresh rate.</summary>
public sealed record DisplayMode(int Width, int Height, int Hz);

/// <summary>
/// The display and graphics switches of the My PC page: resolution and refresh rate, HDR, Auto HDR, brightness, night light,
/// colour profiles, hardware-accelerated GPU scheduling, Game Mode and the optimisations for windowed games. Every setter
/// changes only what Windows' own Settings app changes, and says so when a restart (or administrator rights) is needed.
/// </summary>
public static class DisplaySettings
{
    // ---------- resolution and refresh rate ----------

    /// <summary>Every mode the display offers (32-bit colour), biggest first, refresh rates highest first.</summary>
    public static List<DisplayMode> Modes(string gdiName)
    {
        var set = new HashSet<DisplayMode>();
        var dm = NewDevMode();
        for (int i = 0; Native.EnumDisplaySettings(gdiName, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel == 32 && dm.dmPelsWidth >= 640 && dm.dmDisplayFrequency > 1)
                set.Add(new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency));
            dm = NewDevMode();
        }
        return set.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Width).ThenByDescending(m => m.Hz).ToList();
    }

    public static DisplayMode? Current(string gdiName)
    {
        var dm = NewDevMode();
        return Native.EnumDisplaySettings(gdiName, -1 /* ENUM_CURRENT_SETTINGS */, ref dm)
            ? new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency) : null;
    }

    /// <summary>Switches the display to <paramref name="mode"/> (saved like the Settings app does). Throws with Windows' reason when refused.</summary>
    public static void SetMode(string gdiName, DisplayMode mode)
    {
        var dm = NewDevMode();
        if (!Native.EnumDisplaySettings(gdiName, -1, ref dm)) throw new InvalidOperationException(L.T("Windows could not read this display's current mode."));
        dm.dmPelsWidth = (uint)mode.Width;
        dm.dmPelsHeight = (uint)mode.Height;
        dm.dmDisplayFrequency = (uint)mode.Hz;
        dm.dmFields = 0x80000 | 0x100000 | 0x400000; // DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY
        int test = Native.ChangeDisplaySettingsEx(gdiName, ref dm, 0, 0x2 /* CDS_TEST */, 0);
        if (test != 0) throw new InvalidOperationException(L.F("Windows refused this mode (code {0}).", test));
        int result = Native.ChangeDisplaySettingsEx(gdiName, ref dm, 0, 0x1 /* CDS_UPDATEREGISTRY */, 0);
        if (result == 1) throw new InvalidOperationException(L.T("Windows needs a restart to use this mode."));
        if (result != 0) throw new InvalidOperationException(L.F("Windows refused this mode (code {0}).", result));
    }

    static Native.DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };

    // ---------- HDR ----------

    /// <summary>Turns HDR ("Use HDR" in Settings → Display) on or off for one display.</summary>
    public static void SetHdr(DisplayInfo display, bool on)
    {
        var state = new Native.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = 10, // DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE
                size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = new Native.LUID { LowPart = display.AdapterLow, HighPart = display.AdapterHigh },
                id = display.TargetId,
            },
            value = on ? 1u : 0u,
        };
        int r = Native.DisplayConfigSetDeviceInfo(ref state);
        if (r != 0) throw new InvalidOperationException(L.F("Windows did not change HDR (code {0}).", r));
    }

    // ---------- Auto HDR and windowed games: flags in one per-user DirectX string ----------

    const string DxKey = @"Software\Microsoft\DirectX\UserGpuPreferences", DxValue = "DirectXUserGlobalSettings";

    /// <summary>Auto HDR for DirectX 11 / 12 games (Settings → Display → HDR). Read from Windows' own setting.</summary>
    public static bool AutoHdr => Flag("AutoHDREnable") ?? false;

    public static void SetAutoHdr(bool on) => SetFlag("AutoHDREnable", on);

    /// <summary>"Optimizations for windowed games" (Settings → Display → Graphics).</summary>
    public static bool WindowedOptimizations => Flag("SwapEffectUpgradeEnable") ?? false;

    public static void SetWindowedOptimizations(bool on) => SetFlag("SwapEffectUpgradeEnable", on);

    static Dictionary<string, string> DxFlags()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DxKey);
        var text = key?.GetValue(DxValue) as string ?? "";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) map[kv[0].Trim()] = kv[1].Trim();
        }
        return map;
    }

    // Newer Windows builds keep more bits in the number (e.g. 2097): the lowest bit is on / off.
    static bool? Flag(string name) => DxFlags().TryGetValue(name, out var v) && long.TryParse(v, out var n) ? (n & 1) == 1 : null;

    static void SetFlag(string name, bool on)
    {
        var map = DxFlags();
        long value = map.TryGetValue(name, out var v) && long.TryParse(v, out var n) ? n : 0;
        value = on ? value | 1 : value & ~1L;
        map[name] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var key = Registry.CurrentUser.CreateSubKey(DxKey);
        key.SetValue(DxValue, string.Concat(map.Select(kv => $"{kv.Key}={kv.Value};")), RegistryValueKind.String);
    }

    // ---------- Game Mode ----------

    public static bool GameMode
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            return key?.GetValue("AutoGameModeEnabled") is not int v || v != 0; // missing = on
        }
    }

    public static void SetGameMode(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
        key.SetValue("AutoGameModeEnabled", on ? 1 : 0, RegistryValueKind.DWord);
    }

    // ---------- hardware-accelerated GPU scheduling ----------

    const string GfxKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    /// <summary>On / off as saved (it applies after a restart); null when Windows has no value (the GPU or driver does not offer it).</summary>
    public static bool? HardwareScheduling
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(GfxKey);
            return key?.GetValue("HwSchMode") is int v ? v == 2 : null;
        }
    }

    /// <summary>
    /// Saves the GPU scheduling choice (machine-wide: Windows asks for administrator rights). Takes effect after a restart.
    /// Returns false when the rights were not given.
    /// </summary>
    public static bool SetHardwareScheduling(bool on, bool isAdmin)
    {
        int value = on ? 2 : 1;
        if (isAdmin)
        {
            using var key = Registry.LocalMachine.CreateSubKey(GfxKey);
            key.SetValue("HwSchMode", value, RegistryValueKind.DWord);
            return true;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"),
                $"add \"HKLM\\{GfxKey}\" /v HwSchMode /t REG_DWORD /d {value} /f")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(15000);
            return p is { HasExited: true, ExitCode: 0 };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC declined
        }
    }

    // ---------- night light ----------

    const string NightKey = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    /// <summary>Night light on / off; null when Windows has no state yet (never opened in Settings) or an unknown format.</summary>
    public static bool? NightLight
    {
        get
        {
            var data = NightData();
            return data is { Length: 41 or 43 } ? data[18] == 0x15 : null;
        }
    }

    static byte[]? NightData()
    {
        using var key = Registry.CurrentUser.OpenSubKey(NightKey);
        return key?.GetValue("Data") as byte[];
    }

    /// <summary>
    /// Turns night light on or off by rewriting its state record the way Settings does (the record's known 41 / 43 byte layout).
    /// Returns false when the record has another layout: then nothing is written (Settings can still do it).
    /// </summary>
    public static bool SetNightLight(bool on)
    {
        var data = NightData();
        if (data is not { Length: 41 or 43 }) return false;
        bool isOn = data[18] == 0x15;
        if (isOn == on) return true;
        if ((isOn && data.Length != 43) || (!isOn && data.Length != 41)) return false;
        byte[] next;
        if (on)
        {
            next = new byte[43];
            Array.Copy(data, 0, next, 0, 22);
            Array.Copy(data, 23, next, 25, 18);
            next[18] = 0x15;
            next[23] = 0x10;
            next[24] = 0x00;
        }
        else
        {
            next = new byte[41];
            Array.Copy(data, 0, next, 0, 22);
            Array.Copy(data, 25, next, 23, 18);
            next[18] = 0x13;
        }
        // The record's change counter: Windows only picks up a record newer than the one it has.
        for (int i = 10; i < 15; i++)
        {
            if (next[i] == 0xFF) continue;
            next[i]++;
            break;
        }
        using var key = Registry.CurrentUser.OpenSubKey(NightKey, writable: true);
        if (key is null) return false;
        key.SetValue("Data", next, RegistryValueKind.Binary);
        return true;
    }

    // ---------- brightness ----------

    /// <summary>
    /// The brightness (0-100) of a display: over DDC/CI for monitors, through the laptop panel's own control for built-in screens.
    /// Null when the display does not let Windows set it (many monitors keep DDC/CI off). Slow: call off the UI thread.
    /// </summary>
    public static int? Brightness(DisplayInfo display)
    {
        if (display.Internal) return PanelBrightness();
        return WithPhysicalMonitor(display, h =>
            Native.GetMonitorBrightness(h, out var min, out var cur, out var max) && max > min ? (int?)Math.Round((cur - min) * 100.0 / (max - min)) : null);
    }

    public static bool SetBrightness(DisplayInfo display, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (display.Internal) return SetPanelBrightness(percent);
        return WithPhysicalMonitor(display, h =>
        {
            if (!Native.GetMonitorBrightness(h, out var min, out _, out var max) || max <= min) return false;
            return Native.SetMonitorBrightness(h, (uint)Math.Round(min + (max - min) * percent / 100.0));
        });
    }

    static T? WithPhysicalMonitor<T>(DisplayInfo display, Func<nint, T?> action)
    {
        nint monitor = 0;
        Native.MonitorEnumProc proc = (h, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref info) && string.Equals(info.szDevice, display.GdiName, StringComparison.OrdinalIgnoreCase)) monitor = h;
            return true;
        };
        Native.EnumDisplayMonitors(0, 0, proc, 0);
        GC.KeepAlive(proc);
        if (monitor == 0 || !Native.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) return default;
        var physical = new Native.PHYSICAL_MONITOR[count];
        if (!Native.GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return default;
        try { return action(physical[0].hPhysicalMonitor); }
        finally { Native.DestroyPhysicalMonitors(count, physical); }
    }

    // Built-in panels: Windows' WMI brightness classes, through PowerShell (no extra library in Hybrid Launcher).
    static int? PanelBrightness() =>
        int.TryParse(PowerShell("(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness -ErrorAction Stop | Select-Object -First 1).CurrentBrightness"), out var v) ? v : null;

    static bool SetPanelBrightness(int percent) =>
        PowerShell($"Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods -ErrorAction Stop | Select-Object -First 1 | Invoke-CimMethod -MethodName WmiSetBrightness -Arguments @{{Timeout=0;Brightness=[byte]{percent}}} | Out-Null; 'ok'") == "ok";

    static string? PowerShell(string command)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Panel brightness: {ex.Message}");
            return null;
        }
    }

    // ---------- colour profiles ----------

    /// <summary>
    /// The ICC profiles linked to a display for its current mode (SDR or HDR) and the default one, from Windows' colour
    /// management (Windows 11). Empty when the API is missing (older Windows) or nothing is linked.
    /// </summary>
    public static (List<string> Profiles, string? Default) ColorProfiles(DisplayInfo display)
    {
        var luid = new Native.LUID { LowPart = display.AdapterLow, HighPart = display.AdapterHigh };
        var list = new List<string>();
        string? current = null;
        try
        {
            foreach (var scope in new[] { 1u, 0u }) // the user's own links first, then the system-wide ones
            {
                if (Native.ColorProfileGetDisplayList(scope, luid, display.SourceId, out var buffer, out var count) == 0 && buffer != 0)
                {
                    try
                    {
                        for (int i = 0; i < count; i++)
                            if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)) is { Length: > 0 } name && !list.Contains(name, StringComparer.OrdinalIgnoreCase))
                                list.Add(name);
                    }
                    finally { Native.LocalFree(buffer); }
                }
                if (current is null && Native.ColorProfileGetDisplayDefault(scope, luid, display.SourceId, 0 /* CPT_ICC */, SubType(display), out var def) == 0 && def != 0)
                {
                    current = Marshal.PtrToStringUni(def);
                    Native.LocalFree(def);
                }
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Info($"Colour profiles need a newer Windows: {ex.Message}");
        }
        if (current is { Length: > 0 } && !list.Contains(current, StringComparer.OrdinalIgnoreCase)) list.Insert(0, current);
        return (list, current);
    }

    /// <summary>Makes <paramref name="profile"/> (already linked to the display) its default, for the current user.</summary>
    public static void SetColorProfile(DisplayInfo display, string profile)
    {
        var luid = new Native.LUID { LowPart = display.AdapterLow, HighPart = display.AdapterHigh };
        int r = Native.ColorProfileSetDisplayDefaultAssociation(1, profile, 0, SubType(display), luid, display.SourceId);
        if (r != 0) throw new InvalidOperationException(L.F("Windows did not change the colour profile (code 0x{0:X8}).", r));
    }

    // SDR displays use the standard colour mode profiles, HDR ones the extended (advanced colour) ones.
    static uint SubType(DisplayInfo display) => display.HdrOn ? 8u : 7u;

    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, nint hwnd, uint flags, nint lParam);

        [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint value; }

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE info);

        internal delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFOEX
        {
            public int cbSize;
            public int rcLeft, rcTop, rcRight, rcBottom;
            public int wkLeft, wkTop, wkRight, wkBottom;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PHYSICAL_MONITOR
        {
            public nint hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        [DllImport("dxva2.dll")]
        internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);

        [DllImport("dxva2.dll")]
        internal static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll")]
        internal static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll")]
        internal static extern bool GetMonitorBrightness(nint monitor, out uint min, out uint current, out uint max);

        [DllImport("dxva2.dll")]
        internal static extern bool SetMonitorBrightness(nint monitor, uint brightness);

        [DllImport("mscms.dll")]
        internal static extern int ColorProfileGetDisplayList(uint scope, LUID adapter, uint sourceId, out nint profileList, out uint count);

        [DllImport("mscms.dll")]
        internal static extern int ColorProfileGetDisplayDefault(uint scope, LUID adapter, uint sourceId, uint profileType, uint subType, out nint profileName);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        internal static extern int ColorProfileSetDisplayDefaultAssociation(uint scope, string profileName, uint profileType, uint subType, LUID adapter, uint sourceId);

        [DllImport("kernel32.dll")]
        internal static extern nint LocalFree(nint mem);
    }
}
