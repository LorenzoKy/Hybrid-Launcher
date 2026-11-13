using HybridLauncher.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HybridLauncher.Helpers;

/// <summary>
/// Smooth mouse-wheel scrolling for a page's scroller, as browsers and Chromium-based launchers do it. Windows' own scroller
/// moves each wheel notch with a short animation of its own that shoots off and brakes hard, so turning the wheel moves the page
/// in bursts with stops between them (the self-test measured about 20 stops and steps up to 11 times the usual one in ten
/// notches). Here the notches only move a target, and the view glides towards it once per frame the window draws (XAML's own
/// frame clock, CompositionTarget.Rendering, which follows the display's refresh: a step taken there is in that very frame),
/// eased by time, so successive notches blend into one movement at any refresh rate.
///
/// Only the mouse wheel is taken, and only vertical, without Ctrl or Shift: a precision touchpad, touch, pen, the scroll bar,
/// the keyboard and code keep Windows' own scrolling. A wheel turned over an inner list that scrolls by itself stays with it
/// (the event arrives already handled). Anything else moving the view (a drag of the scroll bar, a key) stops the glide.
/// </summary>
public sealed class SmoothWheel
{
    /// <summary>Pixels per wheel line (Windows' setting gives the lines per notch, 3 by default: 150 px a notch).</summary>
    const double LinePixels = 50;
    /// <summary>The glide's time constant: about 95% of the way in 3 of them (150 ms).</summary>
    const double TauMs = 50;

    readonly ScrollViewer _sv;
    UIElement? _hooked;
    double _target, _current;
    TimeSpan _lastFrame;
    bool _active;

    SmoothWheel(ScrollViewer sv)
    {
        _sv = sv;
        sv.Loaded += (_, _) => Hook();
        sv.Unloaded += (_, _) => Stop();
        // Someone else moved the view (scroll bar, keyboard, a jump to a section): the glide gives way.
        sv.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Stop()), true);
        sv.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => Stop()), true);
        if (sv.IsLoaded) Hook();
    }

    public static void Attach(ScrollViewer sv)
    {
        // Tests only: Windows' own wheel scrolling, to compare.
        if (Environment.GetEnvironmentVariable("HL_NATIVE_WHEEL") == "1") return;
        try { _ = new SmoothWheel(sv); }
        catch (Exception ex) { Log.Warn($"Smooth wheel: {ex.Message}"); }
    }

    /// <summary>The wheel is taken on the scroller's content: it reaches it before the scroller would scroll by itself.</summary>
    void Hook()
    {
        if (_sv.Content is not UIElement content || content == _hooked) return;
        _hooked?.RemoveHandler(UIElement.PointerWheelChangedEvent, (PointerEventHandler)OnWheel);
        content.AddHandler(UIElement.PointerWheelChangedEvent, (PointerEventHandler)OnWheel, false);
        _hooked = content;
    }

    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        if ((e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)) != 0) return;
        var props = e.GetCurrentPoint(_sv).Properties;
        if (props.IsHorizontalMouseWheel || props.MouseWheelDelta == 0) return;
        if (_sv.VerticalScrollMode == ScrollMode.Disabled || _sv.ScrollableHeight <= 0) return;
        if (!_active) _current = _target = _sv.VerticalOffset;
        double step = Math.Min(LinePixels * WheelLines(), _sv.ViewportHeight * 0.4);
        _target = Math.Clamp(_target - props.MouseWheelDelta / 120.0 * step, 0, _sv.ScrollableHeight);
        e.Handled = true;
        if (_active) return;
        _active = true;
        _lastFrame = TimeSpan.Zero;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
    }

    void OnRendering(object? sender, object e)
    {
        var now = e is Microsoft.UI.Xaml.Media.RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromTicks(System.Diagnostics.Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
        double ms = _lastFrame == TimeSpan.Zero ? FrameSync.PeriodMs : (now - _lastFrame).TotalMilliseconds;
        _lastFrame = now;
        // The same frame reported twice (Rendering can fire more than once per frame): nothing to move.
        if (ms <= 0) return;
        try { Tick(ms); }
        catch (Exception ex) { Log.Warn($"Smooth wheel: {ex.Message}"); Stop(); }
    }

    /// <summary>One frame: the view moves part of the way left, by the time since the last frame.</summary>
    void Tick(double ms)
    {
        if (!_active) return;
        _target = Math.Clamp(_target, 0, _sv.ScrollableHeight);
        _current += (_target - _current) * (1 - Math.Exp(-Math.Clamp(ms, 0, 100) / TauMs));
        bool done = Math.Abs(_target - _current) < 0.5;
        if (done) _current = _target;
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        _sv.ChangeView(null, _current, null, disableAnimation: true);
        double cost = System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        if (cost > WorstStepMs) WorstStepMs = cost;
        if (done) Stop();
    }

    /// <summary>Self-test: the slowest single step (a ChangeView) since it was reset.</summary>
    internal static double WorstStepMs;

    void Stop()
    {
        if (!_active) return;
        _active = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
    }

    static int _lines;

    /// <summary>Windows' "lines to scroll per notch" (Settings → Bluetooth & devices → Mouse), 3 by default.</summary>
    static int WheelLines()
    {
        if (_lines > 0) return _lines;
        uint lines = 3;
        try { if (!SystemParametersInfo(0x0068 /* SPI_GETWHEELSCROLLLINES */, 0, ref lines, 0) || lines == 0 || lines > 20) lines = 3; }
        catch { lines = 3; }
        return _lines = (int)lines;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);
}
