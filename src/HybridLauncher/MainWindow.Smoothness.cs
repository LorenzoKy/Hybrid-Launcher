using System.Diagnostics;
using System.Runtime.InteropServices;
using HybridLauncher.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher;

/// <summary>
/// Self-test: how smooth scrolling with the mouse wheel really is. Real wheel notches are sent over a scroller (the cursor is
/// put back afterwards) and these are recorded:
/// - the app's frame clock (CompositionTarget.Rendering): a gap of more than 1.5 refreshes is a frame the app's UI thread missed
///   (it was busy: lag, a hitch);
/// - how long after the first notch the view first moves (the wheel's answer time);
/// - the scroll offset per frame: frames that do not move between frames that do (stalls), or one far larger than its
///   neighbours (a jump), are the "steps" one sees.
/// Tearing cannot happen inside a window DWM composes (it presents whole frames at the refresh); what looks like it comes from
/// either of these, which the numbers tell apart.
/// </summary>
public sealed partial class MainWindow
{
    internal sealed record ScrollReport(string Where, double Hz, int Frames, int UiHitches, double WorstUiMs, double AnswerMs, int Stalls, double WorstJump, double Distance)
    {
        public override string ToString() =>
            $"{Where}: {Hz:0} Hz, {Frames} frames, UI hitches {UiHitches} (worst {WorstUiMs:0.0} ms), first move {AnswerMs:0} ms after the first notch, " +
            $"offset stalls {Stalls}, worst step {WorstJump:0.0}x the median, scrolled {Distance:0} px";
    }

    internal async Task<ScrollReport> MeasureWheelScrollAsync(Microsoft.UI.Xaml.FrameworkElement sv, string where, int notches = 10)
    {
        Func<double> offset = sv switch { ScrollView v => () => v.VerticalOffset, ScrollViewer v => () => v.VerticalOffset, _ => () => 0 };
        // The display's refresh, from DWM's own timing.
        double hz = RefreshHz();
        Views.GamePage.ScrollWorkMs = Views.GamePage.ScrollWorkMax = 0;
        Helpers.SmoothWheel.WorstStepMs = 0;
        var times = new List<long>(2048);
        var offsets = new List<double>(2048);
        EventHandler<object> onFrame = (_, _) => { times.Add(Stopwatch.GetTimestamp()); offsets.Add(offset()); };
        GetCursorPos(out var oldCursor);
        var at = sv.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(sv.ActualWidth / 2, Math.Min(sv.ActualHeight / 2, 400)));
        double scale = GetDpiScale();
        var origin = new POINT();
        _ = ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin);
        _ = SetCursorPos(origin.X + (int)(at.X * scale), origin.Y + (int)(at.Y * scale));
        // A real move of the mouse, as a hand makes before turning the wheel (SetCursorPos alone is not seen as one).
        MouseNudge(1); await Task.Delay(30); MouseNudge(-1);
        await Task.Delay(500);
        // A probe of the UI thread: another thread asks it for a tiny job every 4 ms; how late those jobs run says whether the
        // thread itself was busy (lag in the app's code) or free while frames did not come (the frame clock waiting).
        double probeWorst = 0, probeAt = 0;
        var probeStop = new CancellationTokenSource();
        long probeStart = Stopwatch.GetTimestamp();
        var queue = DispatcherQueue;
        var probe = Task.Run(async () =>
        {
            while (!probeStop.IsCancellationRequested)
            {
                long sent = Stopwatch.GetTimestamp();
                var done = new TaskCompletionSource();
                queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => done.TrySetResult());
                await Task.WhenAny(done.Task, Task.Delay(1000));
                double late = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                if (late > probeWorst) { probeWorst = late; probeAt = (sent - probeStart) * 1000.0 / Stopwatch.Frequency; }
                await Task.Delay(4);
            }
        });
        CompositionTarget.Rendering += onFrame;
        double start = offset();
        long firstInput = 0;
        try
        {
            // Down, as a hand turns the wheel (a notch every 45 ms), let it settle, then back up.
            firstInput = Stopwatch.GetTimestamp();
            for (int i = 0; i < notches; i++) { Wheel(-120); await Task.Delay(45); }
            await Task.Delay(700);
            for (int i = 0; i < notches; i++) { Wheel(120); await Task.Delay(45); }
            await Task.Delay(700);
        }
        finally
        {
            CompositionTarget.Rendering -= onFrame;
            _ = SetCursorPos(oldCursor.X, oldCursor.Y);
            probeStop.Cancel();
            try { await probe; } catch { }
        }
        double period = 1000 / hz;
        int hitches = 0;
        double worst = 0;
        var gaps = new List<string>();
        for (int i = 1; i < times.Count; i++)
        {
            double ms = (times[i] - times[i - 1]) * 1000.0 / Stopwatch.Frequency;
            worst = Math.Max(worst, ms);
            if (ms > period * 1.5) { hitches++; if (gaps.Count < 12) gaps.Add($"{(times[i] - times[0]) * 1000.0 / Stopwatch.Frequency:0}ms:{ms:0.0}"); }
        }
        Log.Info($"Scroll smoothness {where}: UI thread at worst {probeWorst:0.0} ms late (at {probeAt:0} ms)");
        if (gaps.Count > 0) Log.Info($"Scroll smoothness {where}: missed frames at {string.Join(", ", gaps)}; page scroll handler {Views.GamePage.ScrollWorkMs:0.0} ms in total, worst {Views.GamePage.ScrollWorkMax:0.00} ms; slowest wheel step {Helpers.SmoothWheel.WorstStepMs:0.00} ms");
        // Steps between frames while the view moves: stalls (no move between two moving frames) and jumps (a step far larger
        // than the usual one).
        var steps = new List<double>();
        for (int i = 1; i < offsets.Count; i++) steps.Add(Math.Abs(offsets[i] - offsets[i - 1]));
        int stalls = 0;
        for (int i = 1; i + 1 < steps.Count; i++)
            if (steps[i] == 0 && steps[i - 1] > 0.5 && steps[i + 1] > 0.5) stalls++;
        var moving = steps.Where(s => s > 0.5).OrderBy(s => s).ToList();
        double median = moving.Count > 0 ? moving[moving.Count / 2] : 0;
        double jump = median > 0 ? moving[^1] / median : 0;
        double distance = offsets.Count > 0 ? offsets.Max() - start : 0;
        // From the first notch to the first frame that shows the view moved: how long the wheel takes to answer.
        double answer = -1;
        for (int i = 0; i < offsets.Count; i++)
            if (times[i] > firstInput && Math.Abs(offsets[i] - start) > 0.5) { answer = (times[i] - firstInput) * 1000.0 / Stopwatch.Frequency; break; }
        var report = new ScrollReport(where, hz, times.Count, hitches, worst, answer, stalls, jump, distance);
        Log.Info("Scroll smoothness " + report);
        return report;
    }

    static double RefreshHz()
    {
        var info = new byte[292];
        BitConverter.TryWriteBytes(info, 292);
        long period = DwmGetCompositionTimingInfo(0, info) == 0 ? BitConverter.ToInt64(info, 12) : 0;
        return period > 0 ? Stopwatch.Frequency / (double)period : 60;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmGetCompositionTimingInfo(nint hwnd, byte[] info);

    static void MouseNudge(int dx)
    {
        var input = new MOUSEINPUT_WRAP { type = 0, mi = new MOUSEINPUT { dx = dx, dwFlags = 0x0001 /* MOVE */ } };
        _ = SendInputMouse(1, [input], Marshal.SizeOf<MOUSEINPUT_WRAP>());
    }

    static void Wheel(int delta)
    {
        var input = new MOUSEINPUT_WRAP { type = 0, mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = 0x0800 /* WHEEL */ } };
        _ = SendInputMouse(1, [input], Marshal.SizeOf<MOUSEINPUT_WRAP>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct MOUSEINPUT_WRAP { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }

    [DllImport("user32.dll", EntryPoint = "SendInput")]
    static extern uint SendInputMouse(uint count, MOUSEINPUT_WRAP[] inputs, int size);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(nint hwnd, ref POINT p);
}
