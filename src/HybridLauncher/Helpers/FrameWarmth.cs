using HybridLauncher.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace HybridLauncher.Helpers;

/// <summary>
/// Keeps the window's frame clock at the display's full rate while the mouse is in use over it. With nothing moving on screen,
/// the clock drops to one tick every ~93 ms, and the first frames after a pause come at that pace (measured by the self-test's
/// scroll check: two such gaps before the first frame that moves, with the UI thread free all the while), so a wheel turned
/// after a pause answered only after ~185 ms, with Windows' own scrolling too (~95 ms). A listener on the frame clock does not
/// keep it fast (it ticks slowly too); an animation running does. So while the pointer moves, a wheel turns or a button is
/// pressed over the window, and for a short while after, an invisible 1 x 1 visual runs an animation; the wheel then answers at
/// the next refresh. With the mouse still, outside the window, the window hidden or effects paused for a game, it stops and
/// the clock sleeps again.
/// </summary>
public static class FrameWarmth
{
    /// <summary>
    /// How long the clock stays fast after the last pointer activity. With hardware acceleration on (Settings → Features), longer:
    /// a hand that pauses on the mouse to read still gets the next click or notch at the next refresh, for a tiny animation's
    /// cost on the GPU. Off, or while a game runs with effects paused, it sleeps sooner and spends less.
    /// </summary>
    static long LingerMs => MediaPolicy.Effects ? 4000 : AppState.Settings.HardwareAcceleration ? 1500 : 1200;

    static long _until;
    static bool _awake;
    static SpriteVisual? _dot;
    static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    /// <param name="host">An element the invisible dot is attached to (its own child visual: nothing else is drawn there).</param>
    public static void Attach(UIElement root, UIElement host)
    {
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            _dot = compositor.CreateSpriteVisual();
            _dot.Size = new System.Numerics.Vector2(1, 1);
            _dot.Opacity = 0;
            ElementCompositionPreview.SetElementChildVisual(host, _dot);
            _timer = root.DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += (_, _) => { if (Environment.TickCount64 >= _until) Sleep(); };
        }
        catch (Exception ex) { Log.Warn($"Frame warmth: {ex.Message}"); return; }
        var poke = new PointerEventHandler((_, _) => Poke());
        root.AddHandler(UIElement.PointerMovedEvent, poke, true);
        root.AddHandler(UIElement.PointerWheelChangedEvent, poke, true);
        root.AddHandler(UIElement.PointerPressedEvent, poke, true);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => Poke()), true);
        // The pointer coming back into the window: awake before the first move lands on a button (hardware acceleration on).
        root.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) => { if (MediaPolicy.Effects) Poke(); }), true);
        // PointerExited bubbles up from every element the pointer leaves (a button, a card, a line of text), also while the
        // page scrolls under a still pointer: only leaving the window itself lets the clock sleep at once. (Reacting to every
        // one put the clock to sleep between two wheel notches: the next one waited ~93 ms for a frame.)
        root.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, e) =>
        {
            var at = e.GetCurrentPoint(root).Position;
            if (root is FrameworkElement fe && (at.X <= 0 || at.Y <= 0 || at.X >= fe.ActualWidth - 1 || at.Y >= fe.ActualHeight - 1)) _until = 0;
        }), true);
    }

    /// <summary>
    /// The window came to the front (a click on the taskbar, Alt+Tab, the tray): with hardware acceleration on, the clock wakes
    /// at once, so the first frames after the switch come at the display's rate.
    /// </summary>
    public static void WindowActivated()
    {
        if (MediaPolicy.Effects) Poke();
    }

    /// <summary>Self-test: whether the clock is being kept fast now.</summary>
    internal static bool Awake => _awake;

    /// <summary>The pointer is in use (also the self-test's way to stand for a hand on the mouse).</summary>
    internal static void Poke()
    {
        _until = Environment.TickCount64 + LingerMs;
        // Not held back by paused effects (a game running): the user is using the window now, and it must answer at once.
        if (_awake || _dot is null) return;
        _awake = true;
        // A slow, endless animation of a property nobody sees: the compositor ticks at the display's rate while it runs.
        var spin = _dot.Compositor.CreateScalarKeyFrameAnimation();
        spin.InsertKeyFrame(0f, 0f);
        spin.InsertKeyFrame(1f, 360f, _dot.Compositor.CreateLinearEasingFunction());
        spin.Duration = TimeSpan.FromSeconds(10);
        spin.IterationBehavior = AnimationIterationBehavior.Forever;
        _dot.StartAnimation("RotationAngleInDegrees", spin);
        _timer?.Start();
    }

    static void Sleep()
    {
        if (!_awake) return;
        _awake = false;
        _timer?.Stop();
        // At exit the timer can tick once more after the compositor is closed: stopping the animation then throws
        // (ObjectDisposedException), which ended the process with a WinUI crash (0xC000027B) right after the self-test.
        try { _dot?.StopAnimation("RotationAngleInDegrees"); }
        catch (ObjectDisposedException) { _dot = null; }
    }
}
