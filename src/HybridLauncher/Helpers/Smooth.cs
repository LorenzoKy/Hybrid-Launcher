using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Helpers;

/// <summary>
/// Pictures with smooth rounded corners. WinUI cuts a child to a parent's CornerRadius without anti-aliasing, so an Image inside
/// a rounded Border or Grid shows stair-stepped corners (most visible on bright art and at 100-150% scaling). A Border painting
/// the picture as its own background is drawn as a filled rounded shape, which is anti-aliased like every other edge: that is
/// how Steam's covers and buttons stay perfectly round.
/// </summary>
public static class Smooth
{
    /// <summary>
    /// The picture as a rounded fill (put it where the Image went). <paramref name="fadeMs"/> fades it in once decoded;
    /// <paramref name="opened"/> and <paramref name="failed"/> follow the picture's loading.
    /// </summary>
    public static Border Art(ImageSource? source, CornerRadius radius, Stretch stretch = Stretch.UniformToFill, int fadeMs = 0, Action? opened = null, Action? failed = null)
    {
        var brush = new ImageBrush { Stretch = stretch };
        var art = new Border { CornerRadius = radius, Background = brush, IsHitTestVisible = false };
        if (fadeMs > 0) art.Opacity = 0;
        brush.ImageOpened += (_, _) =>
        {
            if (fadeMs > 0) { art.Opacity = 1; Motion.FadeIn(art, fadeMs); }
            opened?.Invoke();
        };
        brush.ImageFailed += (_, _) => failed?.Invoke();
        brush.ImageSource = source;
        return art;
    }

    /// <summary>The inner corners of a bordered element: its radius less the border, so a fill inside meets the border exactly.</summary>
    public static CornerRadius Inner(Border b) => new(
        Math.Max(0, b.CornerRadius.TopLeft - Math.Max(b.BorderThickness.Left, b.BorderThickness.Top)),
        Math.Max(0, b.CornerRadius.TopRight - Math.Max(b.BorderThickness.Right, b.BorderThickness.Top)),
        Math.Max(0, b.CornerRadius.BottomRight - Math.Max(b.BorderThickness.Right, b.BorderThickness.Bottom)),
        Math.Max(0, b.CornerRadius.BottomLeft - Math.Max(b.BorderThickness.Left, b.BorderThickness.Bottom)));
}
