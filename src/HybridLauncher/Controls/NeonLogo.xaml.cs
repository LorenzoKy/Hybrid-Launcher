using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace HybridLauncher.Controls;

/// <summary>
/// The wordmark in the title bar: "HYBRID" in the logo's ivory, widely spaced, over a short crimson bar (the H's crossbar),
/// with a faint crimson glow behind the letters (a composition drop shadow shaped like the text). Under the mouse the bar
/// stretches and the glow warms up, both on the compositor; a click raises <see cref="Clicked"/>.
/// </summary>
public sealed partial class NeonLogo : UserControl
{
    static readonly Windows.UI.Color Glow = Windows.UI.Color.FromArgb(255, 0xD7, 0x14, 0x3A);
    const float RestOpacity = 0.22f, HoverOpacity = 0.5f, HoverBar = 2.6f;

    SpriteVisual? _glow;
    DropShadow? _shadow;

    public event Action? Clicked;

    public NeonLogo()
    {
        InitializeComponent();
        Word.SizeChanged += (_, _) => { UpdateGlow(); Trim(); };
        Bar.SizeChanged += (_, _) =>
        {
            var v = ElementCompositionPreview.GetElementVisual(Bar);
            v.CenterPoint = new Vector3(0, (float)Bar.ActualHeight / 2, 0);
        };
        Unloaded += (_, _) =>
        {
            ElementCompositionPreview.SetElementChildVisual(GlowHost, null);
            _glow?.Dispose();
            _glow = null;
            _shadow = null;
        };
        // A transparent background so the whole wordmark (gaps between letters too) takes the mouse.
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerEntered += (_, _) => Hover(true);
        PointerExited += (_, _) => Hover(false);
        PointerCanceled += (_, _) => Hover(false);
        Tapped += (_, _) => Clicked?.Invoke();
    }

    /// <summary>The wordmark's size; the bar under it follows.</summary>
    public double WordSize
    {
        get => Word.FontSize;
        set
        {
            Word.FontSize = value;
            Bar.Width = Math.Round(value * 1.1);
            Bar.Margin = new Thickness(0, 0, 0, -Math.Round(value * 0.5));
            Trim();
        }
    }

    /// <summary>Letter spacing also follows the last letter: that trailing gap is taken off, so the word sits centred.</summary>
    void Trim() => Word.Margin = new Thickness(0, 0, -Word.CharacterSpacing * Word.FontSize / 1000, 0);

    float BaseBlur => (float)(Word.FontSize * 0.6);

    void UpdateGlow()
    {
        if (Word.ActualWidth <= 0) return;
        if (_glow is null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(Word).Compositor;
            _shadow = compositor.CreateDropShadow();
            _shadow.Color = Glow;
            _shadow.Opacity = RestOpacity;
            _glow = compositor.CreateSpriteVisual();
            _glow.Shadow = _shadow;
            ElementCompositionPreview.SetElementChildVisual(GlowHost, _glow);
        }
        // The text's mask changes with its size: take it again.
        _shadow!.Mask = Word.GetAlphaMask();
        _shadow.BlurRadius = BaseBlur;
        _glow.Size = new Vector2((float)Word.ActualWidth, (float)Word.ActualHeight);
        // GlowHost spans the whole wordmark: put the glow where the text is.
        _glow.Offset = Word.ActualOffset - GlowHost.ActualOffset;
    }

    /// <summary>Mouse over: the bar stretches, the glow grows and brightens, on the compositor (smooth at any refresh rate); out: back.</summary>
    void Hover(bool on)
    {
        ProtectedCursor = on ? InputSystemCursor.Create(InputSystemCursorShape.Hand) : null;
        var bar = ElementCompositionPreview.GetElementVisual(Bar);
        var c = bar.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));
        var duration = TimeSpan.FromMilliseconds(on ? 260 : 340);
        var stretch = c.CreateVector3KeyFrameAnimation();
        stretch.InsertKeyFrame(1f, on ? new Vector3(HoverBar, 1, 1) : Vector3.One, ease);
        stretch.Duration = duration;
        bar.StartAnimation("Scale", stretch);
        if (_shadow is null) return;
        var blur = c.CreateScalarKeyFrameAnimation();
        blur.InsertKeyFrame(1f, on ? BaseBlur * 1.5f : BaseBlur, ease);
        blur.Duration = duration;
        _shadow.StartAnimation("BlurRadius", blur);
        var opacity = c.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1f, on ? HoverOpacity : RestOpacity, ease);
        opacity.Duration = duration;
        _shadow.StartAnimation("Opacity", opacity);
    }
}
