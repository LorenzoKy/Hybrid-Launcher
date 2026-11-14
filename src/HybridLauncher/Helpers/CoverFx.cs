using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Helpers;

/// <summary>
/// The feel of a library cover (2.1). At rest: a soft contact shadow under it and a quiet light-from-above shading on the art
/// (shared brushes, so a library of hundreds costs nothing more). Under the pointer, all on the compositor: the cover springs
/// up a little past its size and settles, a band of light sweeps across it once, a soft highlight follows the pointer, and
/// the theme's accent glows under it. A press dips it in; the release springs it back. The effects that need composition
/// visuals are made on the first hover only, so covers never hovered carry none of them.
/// </summary>
public sealed class CoverFx
{
    readonly FrameworkElement _art;
    // What grows: the picture layer only (2.1-rc.4). The tags, the time and Play over it are never scaled: a scale animated on
    // the compositor stretches what XAML drew at the old size, and text and glass looked blurred until the spring settled.
    // They move out with the picture's edges instead, by whole pixels at rest (see Spread).
    readonly FrameworkElement _scale;
    readonly (FrameworkElement El, int Sx, int Sy)[] _corners;
    readonly Border _fxHost;
    readonly Border _glowHost;
    float _w, _h, _lift = 1.06f;
    bool _hover, _pressed, _built;

    ContainerVisual? _fx;
    CompositionGeometricClip? _clip;
    CompositionRoundedRectangleGeometry? _clipShape;
    SpriteVisual? _sweep, _spot, _glow;
    CompositionRadialGradientBrush? _spotBrush;
    DropShadow? _shadow;

    /// <summary>The corner radius of the cover, for the clip of the light effects.</summary>
    public float Corner { get; init; } = 10;

    /// <summary>
    /// Wires the effects on <paramref name="art"/> (the cover, its size set from code). <paramref name="fxHost"/> is an empty
    /// element laid over the art (it takes no pointer), <paramref name="glowHost"/> one behind it, the art's size.
    /// </summary>
    public CoverFx(FrameworkElement art, FrameworkElement scale, Border fxHost, Border glowHost, params (FrameworkElement El, int Sx, int Sy)[] corners)
    {
        _art = art;
        _scale = scale;
        _corners = corners;
        _fxHost = fxHost;
        _glowHost = glowHost;
        fxHost.IsHitTestVisible = false;
        glowHost.IsHitTestVisible = false;
        art.PointerMoved += (_, e) => { if (_hover) Follow(e); };
        // The press is seen even when the grid's item takes it (it turns it into a click).
        art.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(_art).Properties.IsLeftButtonPressed) Press(true);
        }), true);
        art.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Press(false)), true);
        art.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Press(false)), true);
    }

    /// <summary>The cover's size changed (zoom, page width).</summary>
    public void Resize(double width, double height)
    {
        _w = (float)width;
        _h = (float)height;
        // The same few pixels on each side whatever the cover's size.
        _lift = width > 0 ? (float)((width + 18) / width) : 1.06f;
        _scale.CenterPoint = new Vector3(_w / 2, _h / 2, 0);
        if (!_built) return;
        Size();
    }

    /// <summary>Under the pointer (or focused from the keyboard or a controller) or not.</summary>
    public void Hover(bool on)
    {
        if (on == _hover) return;
        _hover = on;
        if (!on) _pressed = false;
        try
        {
            if (on) Build();
            Sharp(on);
            Scale(on ? _lift : 1f, on ? 0.52f : 0.75f, on ? 55 : 60);
            Lift(on);
            if (_built) Light(on);
        }
        catch (Exception ex) { HybridLauncher.Core.Log.Warn($"Cover effects: {ex.Message}"); }
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _restTimer;

    /// <summary>
    /// Keeps the cover sharp while it grows (2.1-rc.4). A scale animated on the compositor stretches the picture XAML drew at
    /// the old size, and XAML draws it again at the new size only once the animation ends: the cover, its tags and its text
    /// were blurred for the whole spring, then snapped sharp. Drawn at the hover size from the start, the growing cover is
    /// only ever scaled down (sharp); back at rest, once the spring has settled, it is drawn at its own size again.
    /// </summary>
    void Sharp(bool on)
    {
        _restTimer?.Stop();
        if (on) { Raster(_lift); return; }
        _restTimer ??= WeakTimer.Create(_art.DispatcherQueue, this, TimeSpan.FromMilliseconds(450), static fx =>
        {
            if (!fx._hover) fx.Raster(1);
        }, repeating: false);
        _restTimer.Start();
    }

    void Raster(double factor)
    {
        if (_art.XamlRoot is not { } root) return;
        double scale = root.RasterizationScale * factor;
        if (Math.Abs(_scale.RasterizationScale - scale) > 0.001) _scale.RasterizationScale = scale;
    }

    void Press(bool down)
    {
        if (down == _pressed || !_hover && down) return;
        _pressed = down;
        try
        {
            // In fast and a little smaller; back out with a bounce.
            Scale(down ? _lift * 0.95f : _hover ? _lift : 1f, down ? 0.8f : 0.42f, down ? 30 : 50);
            if (_spot is not null) Fade(_spot, down ? 1.3f : _hover ? 1 : 0, 90);
        }
        catch { }
    }

    void Scale(float s, float damping, int periodMs)
    {
        var c = Compositor;
        var spring = c.CreateSpringVector3Animation();
        spring.Target = "Scale";
        spring.FinalValue = new Vector3(s, s, 1);
        spring.DampingRatio = damping;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);
        _scale.StartAnimation(spring);
        Spread(s, damping, periodMs);
    }

    /// <summary>
    /// The overlays follow the picture's edges on the same spring: each corner group moves out by the pixels the picture grew
    /// on its side, rounded, so at rest the text sits on whole pixels and stays crisp.
    /// </summary>
    void Spread(float s, float damping, int periodMs)
    {
        float dx = MathF.Round(_w * (s - 1) / 2), dy = MathF.Round(_h * (s - 1) / 2);
        foreach (var (el, sx, sy) in _corners)
        {
            var spring = Compositor.CreateSpringVector3Animation();
            spring.Target = "Translation";
            spring.FinalValue = new Vector3(sx * dx, sy * dy, 0);
            spring.DampingRatio = damping;
            spring.Period = TimeSpan.FromMilliseconds(periodMs);
            el.StartAnimation(spring);
        }
    }

    /// <summary>A few pixels up while hovered, so the cover floats over its shadow.</summary>
    void Lift(bool on)
    {
        var spring = Compositor.CreateSpringVector3Animation();
        spring.Target = "Translation";
        spring.FinalValue = new Vector3(0, on ? -5 : 0, 0);
        spring.DampingRatio = 0.6f;
        spring.Period = TimeSpan.FromMilliseconds(55);
        _art.StartAnimation(spring);
        // The glow behind follows the cover up.
        _glowHost.StartAnimation(spring);
    }

    static Compositor Compositor => CompositionTarget.GetCompositorForCurrentThread();

    /// <summary>The light visuals: made once, on the first hover.</summary>
    void Build()
    {
        if (_built) return;
        _built = true;
        var c = Compositor;
        _fx = c.CreateContainerVisual();
        _clipShape = c.CreateRoundedRectangleGeometry();
        _clipShape.CornerRadius = new Vector2(Corner, Corner);
        _clip = c.CreateGeometricClip(_clipShape);
        _fx.Clip = _clip;

        // The sweep: a slanted band of light, wider than it looks, crossing once from left to right. All the hover light is
        // kept faint (2.1-rc.10, the user: "more subtle and minimal"): a hint on the art, never a white wash over it.
        var band = c.CreateLinearGradientBrush();
        band.StartPoint = new Vector2(0, 0.5f);
        band.EndPoint = new Vector2(1, 0.5f);
        band.ColorStops.Add(c.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255)));
        band.ColorStops.Add(c.CreateColorGradientStop(0.5f, Color.FromArgb(0x1A, 255, 255, 255)));
        band.ColorStops.Add(c.CreateColorGradientStop(1, Color.FromArgb(0, 255, 255, 255)));
        _sweep = c.CreateSpriteVisual();
        _sweep.Brush = band;
        _sweep.RotationAngleInDegrees = 18;
        _sweep.Opacity = 0;
        _fx.Children.InsertAtTop(_sweep);

        // The highlight that follows the pointer: a wide, faint radial light.
        _spotBrush = c.CreateRadialGradientBrush();
        _spotBrush.MappingMode = CompositionMappingMode.Absolute;
        _spotBrush.ColorStops.Add(c.CreateColorGradientStop(0, Color.FromArgb(0x14, 255, 255, 255)));
        _spotBrush.ColorStops.Add(c.CreateColorGradientStop(0.45f, Color.FromArgb(0x06, 255, 255, 255)));
        _spotBrush.ColorStops.Add(c.CreateColorGradientStop(1, Color.FromArgb(0, 255, 255, 255)));
        _spot = c.CreateSpriteVisual();
        _spot.Brush = _spotBrush;
        _spot.Opacity = 0;
        _fx.Children.InsertAtTop(_spot);
        ElementCompositionPreview.SetElementChildVisual(_fxHost, _fx);

        // The glow under the cover: a block in the theme's accent hidden behind the art (inset past its rounded corners),
        // casting a wide, soft coloured shadow.
        _glow = c.CreateSpriteVisual();
        var accent = Accent();
        _glow.Brush = c.CreateColorBrush(accent);
        _shadow = c.CreateDropShadow();
        _shadow.Color = accent;
        // Tight and low (2.1-rc.3): a wider glow tinted the covers next to it.
        _shadow.BlurRadius = 32;
        _shadow.Offset = new Vector3(0, 14, 0);
        _shadow.Opacity = 0;
        _glow.Shadow = _shadow;
        _glow.Opacity = 1;
        ElementCompositionPreview.SetElementChildVisual(_glowHost, _glow);
        Size();
    }

    static Color Accent()
    {
        var a = Palettes.Current?.Accent ?? Colors.White;
        return Color.FromArgb(255, a.R, a.G, a.B);
    }

    void Size()
    {
        if (_fx is null || _sweep is null || _spot is null || _glow is null || _clipShape is null) return;
        _fx.Size = new Vector2(_w, _h);
        _clipShape.Size = new Vector2(_w, _h);
        _spot.Size = new Vector2(_w, _h);
        _spotBrush!.EllipseRadius = new Vector2(_w * 0.85f, _w * 0.85f);
        float band = Math.Max(60, _w * 0.55f);
        _sweep.Size = new Vector2(band, _h * 1.6f);
        _sweep.CenterPoint = new Vector3(band / 2, _h * 0.8f, 0);
        _sweep.Offset = new Vector3(-band * 1.5f, -_h * 0.3f, 0);
        float inset = Corner + 10;
        _glow.Size = new Vector2(Math.Max(0, _w - inset * 2), Math.Max(0, _h - inset * 2));
        _glow.Offset = new Vector3(inset, inset, 0);
    }

    void Light(bool on)
    {
        if (_shadow is null || _sweep is null || _spot is null || _glow is null) return;
        var c = Compositor;
        // The accent may have changed with the theme since the glow was made.
        var accent = Accent();
        _shadow.Color = accent;
        if (_glow.Brush is CompositionColorBrush fill) fill.Color = accent;
        var glow = c.CreateScalarKeyFrameAnimation();
        glow.InsertKeyFrame(1, on ? 0.32f : 0f, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1f)));
        glow.Duration = TimeSpan.FromMilliseconds(on ? 260 : 200);
        _shadow.StartAnimation("Opacity", glow);
        Fade(_spot, on ? 1 : 0, on ? 160 : 220);
        if (!on) return;
        // One pass of light across the cover.
        float band = _sweep.Size.X;
        var sweep = c.CreateVector3KeyFrameAnimation();
        sweep.InsertKeyFrame(0, new Vector3(-band * 1.5f, -_h * 0.3f, 0));
        sweep.InsertKeyFrame(1, new Vector3(_w + band * 0.5f, -_h * 0.3f, 0), c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.2f, 1f)));
        sweep.Duration = TimeSpan.FromMilliseconds(720);
        _sweep.Opacity = 1;
        _sweep.StartAnimation("Offset", sweep);
    }

    static void Fade(Visual v, float to, int ms)
    {
        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1, to);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Opacity", a);
    }

    void Follow(PointerRoutedEventArgs e)
    {
        if (_spotBrush is null) return;
        var p = e.GetCurrentPoint(_art).Position;
        _spotBrush.EllipseCenter = new Vector2((float)p.X, (float)p.Y);
    }

    // ---------- the shared rest-state shading ----------

    static Brush? _contact, _gloss, _floor;

    /// <summary>The soft shadow under a cover where it meets the page (an ellipse of black fading out).</summary>
    public static Brush ContactShadow
    {
        get
        {
            if (_contact is not null) return _contact;
            var b = new RadialGradientBrush();
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xB0, 0, 0, 0), Offset = 0 });
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x50, 0, 0, 0), Offset = 0.5 });
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1 });
            return _contact = b;
        }
    }

    /// <summary>Light from above on the art: a faint sheen at the top that fades out by a third of the way down.</summary>
    public static Brush Gloss
    {
        get
        {
            if (_gloss is not null) return _gloss;
            var b = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0.35, 1) };
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x1E, 255, 255, 255), Offset = 0 });
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x06, 255, 255, 255), Offset = 0.22 });
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0.4 });
            return _gloss = b;
        }
    }

    /// <summary>The lower part of the art a little deeper, so the cover reads as a solid object with weight at its foot.</summary>
    public static Brush Floor
    {
        get
        {
            if (_floor is not null) return _floor;
            var b = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.55 });
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x48, 0, 0, 0), Offset = 1 });
            return _floor = b;
        }
    }
}
