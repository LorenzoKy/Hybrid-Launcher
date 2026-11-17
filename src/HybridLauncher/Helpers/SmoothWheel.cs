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

    /// <summary>One glide per scroller, shared by the wheel and the controller (so the two blend instead of fighting).</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, SmoothWheel> All = new();

    SmoothWheel(ScrollViewer sv, bool wheel)
    {
        _sv = sv;
        if (wheel) sv.Loaded += (_, _) => Hook();
        sv.Unloaded += (_, _) => Stop();
        // Someone else moved the view (scroll bar, keyboard, a jump to a section): the glide gives way. Not a controller key
        // (should Windows report one): the controller scrolls through this same glide.
        sv.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Stop()), true);
        sv.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { if (e.Key is < VirtualKey.GamepadA or > VirtualKey.GamepadRightThumbstickLeft) Stop(); }), true);
        if (wheel && sv.IsLoaded) Hook();
    }

    public static void Attach(ScrollViewer sv)
    {
        // Tests only: Windows' own wheel scrolling, to compare.
        if (Environment.GetEnvironmentVariable("HL_NATIVE_WHEEL") == "1") return;
        try
        {
            if (All.TryGetValue(sv, out var known)) { known.Hook(); return; }
            All.AddOrUpdate(sv, new SmoothWheel(sv, wheel: true));
        }
        catch (Exception ex) { Log.Warn($"Smooth wheel: {ex.Message}"); }
    }

    /// <summary>
    /// The controller (2.3-rc.3): moves the view's target by <paramref name="dy"/> px and lets it glide there exactly as a wheel
    /// notch does, in XAML's own frame. A stick or a held trigger pushes a little every frame, a tap of a page button a page at
    /// once. Before, the controller set the offset itself on its own clock, from an offset Windows had not updated yet, and a
    /// page tap's own animation fought the held scroll that followed: it stuttered where the wheel was smooth.
    /// </summary>
    public static void Push(ScrollViewer sv, double dy)
    {
        try
        {
            if (!All.TryGetValue(sv, out var glide))
            {
                glide = new SmoothWheel(sv, wheel: false);
                All.AddOrUpdate(sv, glide);
            }
            glide.Add(dy);
        }
        catch (Exception ex) { Log.Warn($"Smooth scroll: {ex.Message}"); }
    }

    /// <summary>A held trigger or stick (2.3-rc.4): the target moves at <paramref name="pxPerSecond"/> by the real time of each frame.</summary>
    public static void Drive(ScrollViewer sv, double pxPerSecond)
    {
        try
        {
            if (!All.TryGetValue(sv, out var glide))
            {
                glide = new SmoothWheel(sv, wheel: false);
                All.AddOrUpdate(sv, glide);
            }
            glide.SetVelocity(pxPerSecond);
        }
        catch (Exception ex) { Log.Warn($"Smooth scroll: {ex.Message}"); }
    }

    double _vel;
    long _velAt;

    void SetVelocity(double v)
    {
        if (v == 0) { _vel = 0; return; }
        if (_sv.VerticalScrollMode == ScrollMode.Disabled || _sv.ScrollableHeight <= 0) return;
        if (!_active) _current = _target = _sv.VerticalOffset;
        _vel = v;
        _velAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Start();
    }

    void Add(double dy)
    {
        if (_sv.VerticalScrollMode == ScrollMode.Disabled || _sv.ScrollableHeight <= 0) return;
        if (!_active) _current = _target = _sv.VerticalOffset;
        // Never far ahead of the view: let go, it stops within a few frames.
        _target = Math.Clamp(_target + dy, 0, _sv.ScrollableHeight);
        Start();
    }

    void Start()
    {
        if (_active) return;
        _active = true;
        _lastFrame = TimeSpan.Zero;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
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
        Start();
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
        if (_vel != 0)
        {
            // Stops by itself if the controller stopped reporting (no frame of it for 80 ms).
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_velAt).TotalMilliseconds > 80) _vel = 0;
            else _target += _vel * Math.Clamp(ms, 0, 100) / 1000;
        }
        _target = Math.Clamp(_target, 0, _sv.ScrollableHeight);
        _current += (_target - _current) * (1 - Math.Exp(-Math.Clamp(ms, 0, 100) / TauMs));
        bool done = _vel == 0 && Math.Abs(_target - _current) < 0.5;
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
