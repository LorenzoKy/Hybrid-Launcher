using System.Runtime.InteropServices;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// Windows' efficiency mode for Hybrid Launcher's own process: EcoQoS (the CPU runs its threads at the most power-efficient
/// speed) and a below-normal priority, the same as the green leaf in Task Manager. Set while nobody works with the window
/// (see <see cref="MediaPolicy"/>), cleared the moment it is in front again. When the window goes away entirely the memory
/// it no longer needs is handed back to Windows too.
/// </summary>
static class Efficiency
{
    const int ProcessPowerThrottling = 4;
    const uint ThrottlingCurrentVersion = 1, ExecutionSpeed = 0x1;
    const uint NormalPriority = 0x20, BelowNormalPriority = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(nint process, int infoClass, ref PowerThrottlingState info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetPriorityClass(nint process, uint priority);

    [DllImport("kernel32.dll")]
    static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessWorkingSetSizeEx(nint process, nint min, nint max, uint flags);

    public static bool On { get; private set; }

    public static void Set(bool on)
    {
        On = on;
        try
        {
            var p = GetCurrentProcess();
            // ControlMask says the process manages execution speed itself; StateMask on = throttled (EcoQoS), off = full speed.
            var state = new PowerThrottlingState { Version = ThrottlingCurrentVersion, ControlMask = ExecutionSpeed, StateMask = on ? ExecutionSpeed : 0 };
            bool eco = SetProcessInformation(p, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
            bool prio = SetPriorityClass(p, on ? BelowNormalPriority : NormalPriority);
            Log.Info($"Efficiency mode {(on ? "on" : "off")}{(eco && prio ? "" : $" (EcoQoS {eco}, priority {prio})")}");
        }
        catch (Exception ex) { Log.Warn($"Efficiency mode: {ex.Message}"); }
    }

    /// <summary>
    /// The window went to the notification area or was minimized: what the garbage collector can free is freed and the pages
    /// nobody touches go back to Windows. They come back from memory as needed when the window opens again.
    /// </summary>
    public static void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);
        }
        catch (Exception ex) { Log.Warn($"Memory trim: {ex.Message}"); }
    }
}
