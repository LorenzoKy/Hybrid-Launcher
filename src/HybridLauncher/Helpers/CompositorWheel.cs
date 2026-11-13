using HybridLauncher.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HybridLauncher.Helpers;

/// <summary>
/// Smooth mouse-wheel scrolling for a page's <see cref="ScrollView"/>, moved by the compositor instead of the UI thread.
///
/// The old way (SmoothWheel on a ScrollViewer) stepped the view from the UI thread once per frame, so any busy moment of that
/// thread (a layout, an image decoded, a tab built) showed as a hitch, and Windows still started its own scroll for each notch
/// for one frame before the step put the view back (a capture of the screen showed a jump forward and back at every notch).
/// Here the ScrollView's own wheel handling is off. The first notch starts one animated scroll whose animation is swapped for
/// one that follows a value of the compositor (Y); every notch then only gives Y a new curve towards the new target. The curve
/// starts at the speed the view already has and comes to rest at the target, so notches blend into one movement, and it runs on
/// the compositor at the display's refresh whatever the UI thread is doing. When the glide is over the view is set where it
/// ended, which hands the ScrollView back its own state.
///
/// Two things measured with an outside capture of the screen (Tools\media\measure-scroll-smoothness.py) shaped this:
/// - giving the ScrollView a new animation or speed for each notch held the view still for one frame at every notch (its
///   tracker changes state), hence one animation for the whole glide;
/// - a new animation on Y shows its start value on its first frame (again a frame without motion), hence each curve starts a
///   little way in.
///
/// Only the mouse wheel, vertical, without Ctrl or Shift, is taken: a precision touchpad, touch, pen, the scroll bar and the
/// keyboard keep the ScrollView's own handling. A wheel turned over an inner list that scrolls by itself stays with it (the
/// event arrives already handled), unless that list cannot move up or down that way (see InnerScrollerStuck).
/// </summary>
public static class CompositorWheel
{
    /// <summary>Pixels per wheel line (Windows' setting gives the lines per notch, 3 by default: 150 px a notch).</summary>
    const double LinePixels = 50;
    /// <summary>How long a glide lasts after the last notch.</summary>
    const double GlideMs = 220;
    /// <summary>How far into its curve a new piece starts, in frames (0.3 measured smoothest at 180 Hz; HL_WHEEL_LEAD to try others).</summary>
    static readonly double LeadFrames = double.TryParse(Environment.GetEnvironmentVariable("HL_WHEEL_LEAD"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lead) ? lead : 0.3;
    /// <summary>A piece starting from rest leaves at least this many times the average speed (an ease-out's start).</summary>
    const double Launch = 1.5;

    /// <summary>Tests only: blocks the UI thread 60 ms every 6 notches, to show the glide goes on without it.</summary>
    static readonly bool StallTest = Environment.GetEnvironmentVariable("HL_WHEEL_STALLTEST") == "1";
    static int _stallN;

    public static void Attach(ScrollView sv)
    {
        // Tests only: the ScrollView's own wheel scrolling, to compare.
        if (Environment.GetEnvironmentVariable("HL_NATIVE_WHEEL") == "1") return;
        try
        {
            sv.IgnoredInputKinds |= ScrollingInputKinds.MouseWheel;
            // Handled events too: a scroller inside the page that cannot move this way (a wide table or a row of chips that only
            // scrolls sideways, a list already at its end) still marks the wheel handled, and the page stood still for that notch.
            sv.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) => OnWheel(sv, e)), true);
        }
        catch (Exception ex) { Log.Warn($"Compositor wheel: {ex.Message}"); }
    }

    static void OnWheel(ScrollView sv, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        if ((e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)) != 0) return;
        var props = e.GetCurrentPoint(sv).Properties;
        if (props.IsHorizontalMouseWheel || props.MouseWheelDelta == 0) return;
        // Taken by something inside the page: left to it, unless it was a scroller that could not move up or down this way.
        if (e.Handled && !InnerScrollerStuck(sv, e.OriginalSource as DependencyObject, props.MouseWheelDelta > 0)) return;
        if (sv.VerticalScrollMode == ScrollingScrollMode.Disabled || sv.ScrollableHeight <= 0) return;
        double step = Math.Min(LinePixels * WheelLines(), sv.ViewportHeight * 0.4) * (-props.MouseWheelDelta / 120.0);
        e.Handled = true;
        try { Glide.For(sv).Notch(step); }
        catch (Exception ex) { Log.Warn($"Compositor wheel: {ex.Message}"); }
        if (StallTest && ++_stallN % 6 == 0) Thread.Sleep(60);
    }

    /// <summary>
    /// Whether the wheel event was handled by a scroller inside <paramref name="page"/> that cannot scroll vertically towards
    /// <paramref name="up"/> (sideways only, or at that end): the page then takes the notch. Anything else that handled it (a
    /// list that scrolled, a slider, a combo box) keeps it.
    /// </summary>
    static bool InnerScrollerStuck(ScrollView page, DependencyObject? from, bool up)
    {
        for (var o = from; o is not null && !ReferenceEquals(o, page); o = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(o))
        {
            switch (o)
            {
                case ScrollViewer inner:
                    if (inner.VerticalScrollMode == ScrollMode.Disabled || inner.ScrollableHeight <= 0.5) return true;
                    return up ? inner.VerticalOffset <= 0.5 : inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
                case ScrollView inner2:
                    if (inner2.VerticalScrollMode == ScrollingScrollMode.Disabled || inner2.ScrollableHeight <= 0.5) return true;
                    return up ? inner2.VerticalOffset <= 0.5 : inner2.VerticalOffset >= inner2.ScrollableHeight - 0.5;
                case Microsoft.UI.Xaml.Controls.Primitives.RangeBase or ComboBox or TextBox or NumberBox:
                    return false;
            }
        }
        return false;
    }

    /// <summary>The wheel's glide on one ScrollView.</summary>
    sealed class Glide
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollView, Glide> All = new();
        readonly ScrollView _sv;
        double _target;
        int _lastId = -1;
        /// <summary>A glide is on (from the first notch until the view is set at its end or something else moves it).</summary>
        bool _running;
        /// <summary>The ScrollView follows Y (its animation was swapped): notches only move Y.</summary>
        bool _following;
        CompositionPropertySet? _props;
        CompositionEasingFunction? _linear;
        ExpressionAnimation? _curve;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? _end;

        // The current piece of the glide as the UI thread models it (the compositor runs the same curve): used only for the
        // speed the next piece starts with.
        long _segStart;
        double _segFrom, _segTo, _segM, _segMs;

        Glide(ScrollView sv)
        {
            _sv = sv;
            sv.ScrollAnimationStarting += (_, e) =>
            {
                if (e.CorrelationId != _lastId || !_running || e.Animation is not CompositionAnimation old) return;
                var c = old.Compositor;
                _props ??= c.CreatePropertySet();
                _props.InsertScalar("O", e.StartPosition.Y);
                _props.InsertScalar("Y", e.StartPosition.Y);
                var follow = c.CreateExpressionAnimation("Vector3(this.StartingValue.X, this.StartingValue.Y + (p.Y - p.O), this.StartingValue.Z)");
                follow.SetReferenceParameter("p", _props);
                e.Animation = follow;
                _following = true;
                _segMs = 0;
                MoveTo(_target, e.StartPosition.Y);
            };
            // Ended, or interrupted by anything else moving the view (scroll bar, keys, a jump to a section).
            sv.ScrollCompleted += (_, e) => { if (e.CorrelationId == _lastId) _running = _following = false; };
        }

        public static Glide For(ScrollView sv) => All.GetValue(sv, s => new Glide(s));

        public void Notch(double step)
        {
            if (!_running) _target = _sv.VerticalOffset;
            _target = Snap(_sv, Math.Clamp(_target + step, 0, _sv.ScrollableHeight));
            if (_end is null)
            {
                // Weak (2.0.0-rc.12): a handler holding the glide held its ScrollView, and through it the whole page, for good.
                _end = WeakTimer.Create(_sv.DispatcherQueue, this, TimeSpan.FromMilliseconds(GlideMs + 60), static g => g.End(), repeating: false);
            }
            _end.Stop();
            _end.Interval = TimeSpan.FromMilliseconds(GlideMs + 60);
            _end.Start();
            if (_following) { MoveTo(_target, _target); return; }
            // The follow is being set up: ScrollAnimationStarting starts towards the latest target.
            if (_running) return;
            _running = true;
            _lastId = _sv.ScrollTo(_sv.HorizontalOffset, _target, new ScrollingScrollOptions(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore));
        }

        void End()
        {
            if (!_running) return;
            _running = _following = false;
            try { _lastId = _sv.ScrollTo(_sv.HorizontalOffset, _target, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)); }
            catch (Exception ex) { Log.Warn($"Compositor wheel: {ex.Message}"); }
        }

        // Cubic Hermite basis: position from start to end (H01) and the share of the starting speed (H10).
        static double H01(double s) => s * s * (3 - 2 * s);
        static double H10(double s) => s * (1 - s) * (1 - s);

        /// <summary>The model's position and speed (px per ms) now.</summary>
        (double p, double v) Model()
        {
            double s = System.Diagnostics.Stopwatch.GetElapsedTime(_segStart).TotalMilliseconds / _segMs;
            if (s >= 1) return (_segTo, 0);
            s = Math.Max(s, 0);
            double d = _segTo - _segFrom;
            return (_segFrom + d * H01(s) + _segM * H10(s), (d * (6 * s - 6 * s * s) + _segM * (3 * s * s - 4 * s + 1)) / _segMs);
        }

        void MoveTo(double target, double from)
        {
            if (_props is null) return;
            var c = _props.Compositor;
            _linear ??= c.CreateLinearEasingFunction();
            var (pNow, vNow) = _segMs > 0 ? Model() : (from, 0.0);
            double d = target - pNow, m = vNow * GlideMs;
            // Never slower at the start than an ease-out (a wheel turned from rest answers at once), never so fast that it
            // overshoots the target; a notch the other way brakes the motion first, briefly.
            if (Math.Sign(m) == Math.Sign(d) || m == 0) m = Math.Sign(d) * Math.Clamp(Math.Abs(m), Launch * Math.Abs(d), 2.5 * Math.Abs(d));
            else m = Math.Sign(m) * Math.Min(Math.Abs(m), 0.5 * Math.Abs(d) + 40);
            double s0 = Math.Clamp(LeadFrames * Math.Clamp(FrameSync.PeriodMs, 2, 34) / GlideMs, 0, 0.4);
            // The curve itself runs on the compositor, exact at every frame: a time T goes from s0 to 1 at a steady pace and Y is
            // the Hermite curve of T. (Before, 16 straight keyframes approximated it: the speed changed in small steps every
            // ~14 ms, and a tiny speed written as "-7.2E-07" broke the expression parser, which reads no exponent.)
            var time = c.CreateScalarKeyFrameAnimation();
            time.InsertKeyFrame(0f, (float)s0, _linear);
            time.InsertKeyFrame(1f, 1f, _linear);
            time.Duration = TimeSpan.FromMilliseconds(Math.Max(1, GlideMs * (1 - s0)));
            _props.InsertScalar("T", (float)s0);
            _props.StartAnimation("T", time);
            _curve ??= c.CreateExpressionAnimation(
                "this.StartingValue + (q.Target - this.StartingValue) * (q.T * q.T * (3 - 2 * q.T)) + q.M * (q.T * (1 - q.T) * (1 - q.T))");
            _curve.SetReferenceParameter("q", _props);
            _props.InsertScalar("Target", (float)target);
            _props.InsertScalar("M", (float)m);
            // From the value the view really has on screen when this piece starts (StartingValue), not the model's.
            _props.StartAnimation("Y", _curve);
            _segStart = System.Diagnostics.Stopwatch.GetTimestamp() - (long)(s0 * GlideMs * System.Diagnostics.Stopwatch.Frequency / 1000);
            _segFrom = pNow; _segTo = target; _segM = m; _segMs = GlideMs;
        }
    }

    /// <summary>
    /// An offset on a whole device pixel. A glide that came to rest between two pixels (a fractional step, or a display scale
    /// above 100%) left the content drawn at a sub-pixel offset: every text on the page looked blurred until the next scroll.
    /// </summary>
    internal static double Snap(ScrollView sv, double offset)
    {
        double scale = (sv.XamlRoot?.RasterizationScale is > 0 and var r ? r : 1) * UiScale.Factor;
        double snapped = Math.Round(offset * scale) / scale;
        return Math.Clamp(snapped, 0, Math.Max(0, Math.Floor(sv.ScrollableHeight * scale) / scale));
    }

    /// <summary>
    /// The width for a column of at most <paramref name="max"/> DIPs centred in a scroller <paramref name="room"/> DIPs wide, so
    /// that it lands on a whole device pixel: the free room left and right is an even number of pixels, and the ScrollView's own
    /// centring (half of it) is then a whole pixel too. Half a pixel off blurs every text in the column.
    /// </summary>
    public static double SharpWidth(FrameworkElement scroller, double room, double max)
    {
        double scale = (scroller.XamlRoot?.RasterizationScale is > 0 and var r ? r : 1) * UiScale.Factor;
        double roomPx = Math.Round(room * scale);
        double px = Math.Floor(Math.Min(max * scale, roomPx));
        if ((roomPx - px) % 2 != 0) px -= 1;
        return Math.Max(0, px / scale);
    }

    /// <summary>
    /// Calls <paramref name="fit"/> with the width the content really has, whenever it changes: the viewport, not the ScrollView.
    /// They differ when a scroll bar takes room of its own (Windows' "Always show scroll bars", or a tab long enough to need one
    /// once the bar no longer overlays): a column as wide as the ScrollView then ran a scroll bar's width past the right edge,
    /// only on some tabs and only some of the time.
    /// </summary>
    public static void OnViewportWidth(ScrollView sv, Action<double> fit)
    {
        FrameworkElement? presenter = null;
        double last = -1;
        void Run()
        {
            double w = sv.ActualWidth;
            if (presenter is { ActualWidth: > 0 }) w = Math.Min(w, presenter.ActualWidth);
            if (sv.ViewportWidth > 0) w = Math.Min(w, sv.ViewportWidth);
            if (w <= 0 || Math.Abs(w - last) < 0.01) return;
            last = w;
            fit(w);
        }
        void Hook()
        {
            if (presenter is null && sv.Content is FrameworkElement c && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(c) is FrameworkElement p && p != sv)
            {
                presenter = p;
                p.SizeChanged += (_, _) => Run();
            }
        }
        sv.SizeChanged += (_, _) => { Hook(); last = -1; Run(); };
        sv.Loaded += (_, _) => { Hook(); last = -1; Run(); };
        sv.ExtentChanged += (_, _) => Run();
    }

    /// <summary>
    /// A column centred in a ScrollView, placed on a whole device pixel. ScrollView centres a narrower content by itself, by
    /// half the free room: an odd number of pixels left it half a pixel off, and every text in it looked blurred.
    /// </summary>
    public static void CentreOnPixels(ScrollView sv, FrameworkElement column)
    {
        column.HorizontalAlignment = HorizontalAlignment.Left;
        void Place()
        {
            double scale = (sv.XamlRoot?.RasterizationScale is > 0 and var r ? r : 1) * UiScale.Factor;
            double free = sv.ActualWidth - column.ActualWidth;
            double left = free > 0 ? Math.Floor(free / 2 * scale) / scale : 0;
            var m = column.Margin;
            if (Math.Abs(m.Left - left) > 0.001) column.Margin = new Thickness(left, m.Top, 0, m.Bottom);
        }
        sv.SizeChanged += (_, _) => Place();
        column.SizeChanged += (_, _) => Place();
    }

    static int _lines;

    /// <summary>Windows' "lines to scroll per notch" (Settings → Bluetooth &amp; devices → Mouse), 3 by default.</summary>
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

/// <summary>ScrollViewer's ChangeView on a ScrollView, so pages moved to ScrollView keep their calls.</summary>
public static class ScrollViewExtensions
{
    public static bool ChangeView(this ScrollView sv, double? horizontalOffset, double? verticalOffset, float? zoomFactor, bool disableAnimation = false)
    {
        sv.ScrollTo(horizontalOffset ?? sv.HorizontalOffset, verticalOffset is { } v ? CompositorWheel.Snap(sv, v) : sv.VerticalOffset,
            new ScrollingScrollOptions(disableAnimation ? ScrollingAnimationMode.Disabled : ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore));
        return true;
    }
}
