using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HybridLauncher.Core.Hardware;

public sealed record CpuInfo(string Name, int Cores, int Threads, int MaxMhz);

public enum GpuVendor { Nvidia, Amd, Intel, Other }

public sealed record GpuInfo(string Name, GpuVendor Vendor, ulong VramBytes, string DriverVersion, DateTime? DriverDate)
{
    /// <summary>The upscalers, frame generation, ray reconstruction and latency technologies this card runs, with their versions.</summary>
    public IReadOnlyList<GpuTech> Techs => UpscalerAdvice.Techs(this);
}

public sealed record MemoryModule(ulong SizeBytes, int SpeedMts, string Type, string Manufacturer, string PartNumber);

public sealed record MemoryInfo(ulong TotalBytes, ulong AvailableBytes, IReadOnlyList<MemoryModule> Modules);

/// <summary>
/// An active display. <see cref="GdiName"/> ("\\.\DISPLAY1"), the adapter LUID and the source / target ids are what the
/// display settings (mode, HDR, brightness, colour profile) need; <see cref="Internal"/> is a laptop's built-in panel.
/// </summary>
public sealed record DisplayInfo(string Name, int Width, int Height, double RefreshHz, bool HdrSupported, bool HdrOn, bool Primary,
    string GdiName = "", uint AdapterLow = 0, int AdapterHigh = 0, uint SourceId = 0, uint TargetId = 0, bool Internal = false);

public sealed record SystemInfo(string Os, string Build, string Board, string Bios, bool? HardwareScheduling, bool GameMode, bool WindowedOptimizations);

public sealed record DriveSummary(string Name, string Label, ulong TotalBytes, ulong FreeBytes, string Format);

public sealed record HardwareSnapshot(CpuInfo Cpu, IReadOnlyList<GpuInfo> Gpus, MemoryInfo Memory, IReadOnlyList<DisplayInfo> Displays,
    SystemInfo System, IReadOnlyList<DriveSummary> Drives);

/// <summary>Reads the PC's hardware from the registry and Win32 (no WMI). Each part fails on its own: a missing value never hides the rest.</summary>
public static class HardwareInfo
{
    public static HardwareSnapshot Read() => new(
        Safe(ReadCpu, new CpuInfo("Unknown CPU", 0, Environment.ProcessorCount, 0)),
        Safe(ReadGpus, []),
        Safe(ReadMemory, new MemoryInfo(0, 0, [])),
        Safe(Displays.Read, []),
        Safe(ReadSystem, new SystemInfo("Windows", "", "", "", null, true, false)),
        Safe(ReadDrives, []));

    static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log.Warn($"Hardware: {read.Method.Name} failed: {ex.Message}");
            return fallback;
        }
    }

    // ---------- CPU ----------

    static CpuInfo ReadCpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown CPU";
        var mhz = key?.GetValue("~MHz") is int m ? m : 0;
        return new CpuInfo(Collapse(name), PhysicalCores(), Environment.ProcessorCount, mhz);
    }

    static int PhysicalCores()
    {
        uint len = 0;
        Native.GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, 0, ref len);
        if (len == 0) return 0;
        var buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(0, buffer, ref len)) return 0;
            int cores = 0;
            for (uint offset = 0; offset < len;)
            {
                var size = (uint)Marshal.ReadInt32(buffer, (int)offset + 4);
                if (size == 0) break;
                cores++;
                offset += size;
            }
            return cores;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ---------- GPU ----------

    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    static List<GpuInfo> ReadGpus()
    {
        var list = new List<GpuInfo>();
        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (cls is null) return list;
        var active = ActiveAdapterDescriptions();
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(sub);
            var name = k?.GetValue("DriverDesc") as string;
            if (k is null || string.IsNullOrWhiteSpace(name)) continue;
            var device = (k.GetValue("MatchingDeviceId") as string ?? "").ToUpperInvariant();
            if (!device.Contains("VEN_")) continue; // Basic Render Driver, remote display adapters
            // Drivers of cards no longer plugged in keep their key: only keep adapters Windows shows.
            if (active.Count > 0 && !active.Contains(name)) continue;

            var vendor = device.Contains("VEN_10DE") ? GpuVendor.Nvidia
                : device.Contains("VEN_1002") || device.Contains("VEN_1022") ? GpuVendor.Amd
                : device.Contains("VEN_8086") ? GpuVendor.Intel : GpuVendor.Other;

            ulong vram = k.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long l => (ulong)l,
                byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                _ => k.GetValue("HardwareInformation.MemorySize") switch
                {
                    int i => (uint)i,
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => 0,
                },
            };

            var driver = k.GetValue("DriverVersion") as string ?? "";
            driver = vendor switch
            {
                GpuVendor.Nvidia => NvidiaVersion(driver) ?? driver,
                GpuVendor.Amd => k.GetValue("RadeonSoftwareVersion") is string rs && rs.Length > 0 ? $"Adrenalin {rs}" : driver,
                _ => driver,
            };
            DateTime? date = DateTime.TryParse(k.GetValue("DriverDate") as string, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;

            if (list.Any(g => g.Name == name && g.DriverVersion == driver)) continue;
            list.Add(new GpuInfo(Collapse(name), vendor, vram, driver, date));
        }
        // Discrete cards first.
        return list.OrderByDescending(g => g.VramBytes).ToList();
    }

    /// <summary>"32.0.15.6094" → "560.94": Nvidia's version is the last five digits of the Windows driver version.</summary>
    public static string? NvidiaVersion(string windowsVersion)
    {
        var digits = new string(windowsVersion.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return null;
        var last = digits[^5..];
        return $"{int.Parse(last[..3])}.{last[3..]}";
    }

    static HashSet<string> ActiveAdapterDescriptions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (!string.IsNullOrWhiteSpace(dd.DeviceString)) set.Add(dd.DeviceString.Trim());
            dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>();
        }
        return set;
    }

    // ---------- memory ----------

    static MemoryInfo ReadMemory()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref status);
        List<MemoryModule> modules = [];
        try { modules = Smbios.MemoryModules(); }
        catch (Exception ex) { Log.Warn($"SMBIOS memory read failed: {ex.Message}"); }
        return new MemoryInfo(status.ullTotalPhys, status.ullAvailPhys, modules);
    }

    // ---------- system ----------

    static SystemInfo ReadSystem()
    {
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = nt?.GetValue("ProductName") as string ?? "Windows";
        var build = nt?.GetValue("CurrentBuild") as string ?? "";
        var ubr = nt?.GetValue("UBR") is int u ? u : 0;
        var display = nt?.GetValue("DisplayVersion") as string ?? "";
        // ProductName still says "Windows 10" on Windows 11.
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        var os = string.IsNullOrEmpty(display) ? product : $"{product} {display}";

        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var board = Collapse($"{bios?.GetValue("BaseBoardManufacturer")} {bios?.GetValue("BaseBoardProduct")}");
        var biosText = Collapse($"{bios?.GetValue("BIOSVendor")} {bios?.GetValue("BIOSVersion")} {bios?.GetValue("BIOSReleaseDate")}");

        using var gfx = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        bool? hags = gfx?.GetValue("HwSchMode") is int h ? h == 2 : null;

        using var gameBar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        bool gameMode = gameBar?.GetValue("AutoGameModeEnabled") is not int gm || gm != 0; // missing = on

        using var dx = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        bool windowed = (dx?.GetValue("DirectXUserGlobalSettings") as string ?? "").Contains("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase);

        return new SystemInfo(os, ubr > 0 ? $"{build}.{ubr}" : build, board, biosText, hags, gameMode, windowed);
    }

    // ---------- drives ----------

    static List<DriveSummary> ReadDrives() => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        .Select(d => new DriveSummary(d.Name.TrimEnd('\\'), d.VolumeLabel, (ulong)d.TotalSize, (ulong)d.AvailableFreeSpace, d.DriveFormat))
        .ToList();

    static string Collapse(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1UL << 40 => $"{bytes / (double)(1UL << 40):0.#} TB",
        >= 1UL << 30 => $"{bytes / (double)(1UL << 30):0.#} GB",
        >= 1UL << 20 => $"{bytes / (double)(1UL << 20):0} MB",
        _ => $"{bytes} B",
    };
}

/// <summary>The kind of graphics technology a <see cref="GpuTech"/> is, in the order the My PC page lists them.</summary>
public enum GpuTechKind { Upscaling, FrameGeneration, RayReconstruction, Latency }

/// <summary>
/// One technology a graphics card runs: its name, the exact newest version the card supports (as the vendor numbers it, e.g.
/// "4.1.1"), and how it runs there (<see cref="Native"/>: on the card's own hardware or driver; otherwise only through
/// OptiScaler or a cross-vendor path).
/// </summary>
public sealed record GpuTech(GpuTechKind Kind, string Name, string Version, bool Native, string Note = "");

/// <summary>
/// What each GPU family runs: upscaling, frame generation, ray reconstruction and latency reduction, with the exact newest
/// version of each from <see cref="TechVersions"/> (built in, then corrected by itself from the vendors' SDK releases and the
/// app's correction file). The card's name and driver decide what runs natively; a renamed or unknown card falls back to the
/// cross-vendor ones.
/// </summary>
public static class UpscalerAdvice
{
    static bool Has(string name, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(name, pattern);

    static string V(string id) => TechVersions.Get(id);

    /// <summary>Whether an AMD driver is Adrenalin <paramref name="year"/>.<paramref name="month"/> or newer.</summary>
    static bool AdrenalinAtLeast(GpuInfo gpu, int year, int month)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gpu.DriverVersion, @"Adrenalin\s+(\d+)\.(\d+)");
        if (!m.Success) return false;
        int y = int.Parse(m.Groups[1].Value), mo = int.Parse(m.Groups[2].Value);
        return y > year || y == year && mo >= month;
    }

    public static IReadOnlyList<GpuTech> Techs(GpuInfo gpu)
    {
        var n = gpu.Name.ToUpperInvariant();
        var list = new List<GpuTech>();
        void Add(GpuTechKind kind, string name, string version, bool native, string note = "") => list.Add(new(kind, name, version, native, note));
        // FSR 3.1 and XeSS (DP4a) run on any recent card; OptiScaler can put them in place of a game's DLSS.
        void CrossVendor(bool xessNative = false)
        {
            Add(GpuTechKind.Upscaling, "FSR Upscaling", V("fsr-upscaling"), true, "Any recent card");
            if (!xessNative) Add(GpuTechKind.Upscaling, "XeSS Super Resolution", V("xess"), true, "DP4a path, lighter than on Intel Arc");
            Add(GpuTechKind.FrameGeneration, "FSR Frame Generation", V("fsr-framegen"), true, "Any recent card");
            if (!xessNative) Add(GpuTechKind.FrameGeneration, "XeSS Frame Generation", V("xess"), true, "Any card with Shader Model 6.4");
        }
        switch (gpu.Vendor)
        {
            case GpuVendor.Nvidia when n.Contains("RTX"):
                bool rtx50 = Has(n, @"RTX\s*(PRO\s*)?50\d\d"), rtx40 = Has(n, @"RTX\s*40\d\d");
                Add(GpuTechKind.Upscaling, "DLSS Super Resolution", V("dlss"), true, "Second-generation transformer model · SDK " + V("dlss-sdk"));
                Add(GpuTechKind.Upscaling, "DLSS 5 neural rendering", V("dlss-neural"), rtx50, rtx50 ? "In games that ship it" : "RTX 50 first; RTX 40 support announced for later");
                if (rtx50) Add(GpuTechKind.FrameGeneration, "DLSS Multi Frame Generation", V("dlss"), true, "Dynamic, up to 6x");
                else if (rtx40) Add(GpuTechKind.FrameGeneration, "DLSS Frame Generation", V("dlss-framegen-rtx40"), true, "2x");
                else Add(GpuTechKind.FrameGeneration, "DLSS Frame Generation", "", false, "Needs an RTX 40 or 50: use FSR or XeSS frame generation through OptiScaler");
                if (rtx50 || rtx40) Add(GpuTechKind.FrameGeneration, "Smooth Motion", "", true, "Driver frame generation for any DX11 / DX12 / Vulkan game");
                Add(GpuTechKind.RayReconstruction, "DLSS Ray Reconstruction", V("dlss"), true, "Second-generation transformer model");
                Add(GpuTechKind.Latency, "Reflex", rtx50 ? V("reflex") : "1.0", true, rtx50 ? "Frame Warp where the game supports it" : "");
                CrossVendor();
                break;
            case GpuVendor.Nvidia:
                Add(GpuTechKind.Upscaling, "DLSS Super Resolution", "", false, "Needs an RTX card: FSR or XeSS take its place through OptiScaler");
                CrossVendor();
                Add(GpuTechKind.Latency, "Reflex", "1.0", true, "GTX 900 and newer");
                break;
            case GpuVendor.Amd:
                bool rdna4 = Has(n, @"RX\s*9\d\d\d"), rdna3 = Has(n, @"RX\s*7\d\d\d"), rdna2 = Has(n, @"RX\s*6\d\d\d");
                if (rdna4)
                {
                    Add(GpuTechKind.Upscaling, "FSR Upscaling (ML)", V("fsr-upscaling-ml"), true, "FSR Redstone, AI upscaling");
                    Add(GpuTechKind.FrameGeneration, "FSR Frame Generation (ML)", V("fsr-framegen-ml"), true, "FSR Redstone, driver-assisted frame pacing");
                    Add(GpuTechKind.RayReconstruction, "FSR Ray Regeneration", V("fsr-ray-regeneration"), true, "FSR Redstone, DirectX 12");
                }
                else if (rdna3)
                    Add(GpuTechKind.Upscaling, "FSR Upscaling (ML)", V("fsr-upscaling-ml"), true, "AI upscaling on RX 7000");
                else if (rdna2)
                    Add(GpuTechKind.Upscaling, "FSR Upscaling (ML)", V("fsr-upscaling-ml"), AdrenalinAtLeast(gpu, 26, 10), AdrenalinAtLeast(gpu, 26, 10)
                        ? "INT8 model for RX 6000 (Adrenalin 26.10.1 and newer)"
                        : "Needs Adrenalin 26.10.1 or newer (INT8 model for RX 6000)");
                CrossVendor();
                if (rdna4 || rdna3 || rdna2) Add(GpuTechKind.FrameGeneration, "AMD Fluid Motion Frames", V("afmf"), true, "Driver frame generation for any DX11 / DX12 / Vulkan game");
                Add(GpuTechKind.Latency, "Anti-Lag", rdna4 || rdna3 ? V("anti-lag") : "1.0", true, rdna4 || rdna3 ? "In games that ship it; FSR frame generation pairs with it" : "Driver-wide");
                Add(GpuTechKind.Upscaling, "DLSS inputs", "", false, "Games with only DLSS need OptiScaler's spoofing");
                break;
            case GpuVendor.Intel when n.Contains("ARC"):
                Add(GpuTechKind.Upscaling, "XeSS Super Resolution", V("xess"), true, "XMX cores");
                Add(GpuTechKind.FrameGeneration, "XeSS Multi Frame Generation", V("xess"), true, "Up to 4x");
                CrossVendor(xessNative: true);
                Add(GpuTechKind.Latency, "Xe Low Latency", V("xell"), true);
                break;
            default:
                CrossVendor();
                break;
        }
        return list.OrderBy(t => t.Kind).ThenByDescending(t => t.Native).ToList();
    }
}

/// <summary>Memory modules from the SMBIOS table (type 17), read with GetSystemFirmwareTable.</summary>
static class Smbios
{
    public static List<MemoryModule> MemoryModules()
    {
        const uint RSMB = 0x52534D42;
        uint size = Native.GetSystemFirmwareTable(RSMB, 0, 0, 0);
        if (size == 0) return [];
        var buf = new byte[size];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { Native.GetSystemFirmwareTable(RSMB, 0, h.AddrOfPinnedObject(), size); }
        finally { h.Free(); }

        var list = new List<MemoryModule>();
        int length = BitConverter.ToInt32(buf, 4);
        int p = 8, end = Math.Min(buf.Length, 8 + length);
        while (p + 4 <= end)
        {
            byte type = buf[p], len = buf[p + 1];
            if (len < 4 || p + len > end) break;
            // The unformed section (strings) follows the formatted part and ends with a double zero.
            var strings = new List<string>();
            int s = p + len;
            while (s < end && buf[s] != 0)
            {
                int e = Array.IndexOf(buf, (byte)0, s);
                if (e < 0) e = end;
                strings.Add(Encoding.ASCII.GetString(buf, s, e - s).Trim());
                s = e + 1;
            }
            int next = s + (strings.Count == 0 ? 2 : 1);

            if (type == 17 && len >= 0x1B)
            {
                ushort sizeField = BitConverter.ToUInt16(buf, p + 0x0C);
                ulong bytes = sizeField switch
                {
                    0 or 0xFFFF => 0,
                    0x7FFF when len >= 0x20 => (ulong)BitConverter.ToUInt32(buf, p + 0x1C) << 20,
                    _ => (sizeField & 0x8000) != 0 ? (ulong)(sizeField & 0x7FFF) << 10 : (ulong)sizeField << 20,
                };
                if (bytes > 0)
                {
                    int speed = BitConverter.ToUInt16(buf, p + 0x15);
                    if (len >= 0x22 && BitConverter.ToUInt16(buf, p + 0x20) is var configured and > 0 and < 0xFFFF) speed = configured;
                    string Str(int index)
                    {
                        var v = index > 0 && index <= strings.Count ? strings[index - 1] : "";
                        // Many boards fill unknown fields with placeholders.
                        return v is "Unknown" or "Not Specified" or "Undefined" || v.StartsWith("Manufacturer", StringComparison.OrdinalIgnoreCase) ? "" : v;
                    }
                    var memType = buf[p + 0x12] switch { 0x18 => "DDR3", 0x1A => "DDR4", 0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5", _ => "" };
                    list.Add(new MemoryModule(bytes, speed, memType, Str(buf[p + 0x17]), Str(buf[p + 0x1A])));
                }
            }
            if (type == 127) break;
            p = next;
        }
        return list;
    }
}

/// <summary>Active displays from the Windows display configuration: monitor name, mode, refresh rate and HDR state.</summary>
static class Displays
{
    public static List<DisplayInfo> Read()
    {
        var list = new List<DisplayInfo>();
        if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return list;
        var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
        if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, 0) != 0) return list;

        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            var name = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            name.header.type = 2; // GET_TARGET_NAME
            name.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            name.header.adapterId = path.targetInfo.adapterId;
            name.header.id = path.targetInfo.id;
            var friendly = Native.DisplayConfigGetDeviceInfo(ref name) == 0 && !string.IsNullOrWhiteSpace(name.monitorFriendlyDeviceName)
                ? name.monitorFriendlyDeviceName : "Display";

            int w = 0, h = 0;
            bool primary = false;
            uint srcIdx = path.sourceInfo.modeInfoIdx;
            if (srcIdx < modes.Length && modes[srcIdx].infoType == 1) // SOURCE
            {
                var sm = modes[srcIdx].sourceMode;
                w = (int)sm.width; h = (int)sm.height;
                primary = sm.positionX == 0 && sm.positionY == 0;
            }
            double hz = path.targetInfo.refreshRate.Denominator == 0 ? 0 : (double)path.targetInfo.refreshRate.Numerator / path.targetInfo.refreshRate.Denominator;

            var color = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            color.header.type = 9; // GET_ADVANCED_COLOR_INFO
            color.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            color.header.adapterId = path.targetInfo.adapterId;
            color.header.id = path.targetInfo.id;
            bool hdrSupported = false, hdrOn = false;
            if (Native.DisplayConfigGetDeviceInfo(ref color) == 0)
            {
                hdrSupported = (color.value & 1) != 0;
                hdrOn = (color.value & 2) != 0;
            }
            var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = 1; // GET_SOURCE_NAME
            source.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            var gdi = Native.DisplayConfigGetDeviceInfo(ref source) == 0 ? source.viewGdiDeviceName ?? "" : "";
            // Built-in panels: internal, LVDS, embedded DisplayPort or embedded UDI.
            uint tech = path.targetInfo.outputTechnology;
            bool builtIn = tech is 0x80000000 or 6 or 11 or 13;
            list.Add(new DisplayInfo(friendly, w, h, Math.Round(hz, 2), hdrSupported, hdrOn, primary,
                gdi, path.targetInfo.adapterId.LowPart, path.targetInfo.adapterId.HighPart, path.sourceInfo.id, path.targetInfo.id, builtIn));
        }
        return list.OrderByDescending(d => d.Primary).ToList();
    }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    internal static extern uint GetSystemFirmwareTable(uint provider, uint tableId, nint buffer, uint size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    // ---- display configuration ----

    internal const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] internal struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_SOURCE_MODE { public uint width, height, pixelFormat; public int positionX, positionY; }

    // The mode union is 48 bytes (the target mode is the largest member); only the source mode is read.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value, colorEncoding, bitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out int numPaths, out int numModes);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(uint flags, ref int numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref int numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, nint topology);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO info);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
}
