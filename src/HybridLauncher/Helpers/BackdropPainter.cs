using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace HybridLauncher.Helpers;

/// <summary>
/// The window's background in the dark theme (1.4.0-rc.5), painted pixel by pixel instead of one linear gradient: a deep base
/// that darkens downwards, two soft glows of the palette (its tint from the top-right, its accent low on the left), a quiet
/// vignette at the edges, and a fine dither so the long, dark gradients show no bands (a plain gradient brush steps visibly
/// on 8-bit displays, the "artefacts" of the earlier look). Scene themes paint opaque over their own colours; the others keep
/// a trace of Mica (and so of the wallpaper) through a high but not full alpha. Painted off the UI thread at the window's
/// pixel size (capped), again only when the size or the palette changes.
/// </summary>
public static class BackdropPainter
{
    /// <summary>What to paint: the base top and bottom colours, the two glows, and how opaque it is.</summary>
    public sealed record Recipe(Color Top, Color Bottom, Color? Glow, Color? Glow2, byte Alpha);

    public static Recipe For(Palette palette)
    {
        if (palette.Backdrop is { } scene)
            return new Recipe(scene.Top, scene.Bottom, palette.Tint ?? palette.Accent, palette.Accent ?? palette.Tint, 0xFF);
        // 2.0.0-rc.4: halfway back to 1.3's lighter Mica for readability (was 0x13 → 0x08 at 0xD4): a lighter base and more
        // of Mica through it, still darker than 1.3.
        var top = Color.FromArgb(0xFF, 0x19, 0x19, 0x1E);
        var bottom = Color.FromArgb(0xFF, 0x0E, 0x0E, 0x11);
        if (palette.Key == "Windows" && palette.Tint is { } tint)
        {
            // The Windows theme (the default): the base takes a breath of the accent's hue and covers a little more of Mica,
            // so a bright wallpaper never washes the window out while its tone still shows through.
            top = Mix(top, tint, 0.07);
            bottom = Mix(bottom, tint, 0.04);
            return new Recipe(top, bottom, tint, palette.Accent, 0xB4);
        }
        return new Recipe(top, bottom, palette.Tint ?? palette.Accent, palette.Accent ?? palette.Tint, 0xA0);
    }

    /// <summary>Paints <paramref name="recipe"/> into a BGRA buffer (premultiplied) of the given size. Pure: runs on any thread.</summary>
    public static byte[] Paint(Recipe recipe, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        double a = recipe.Alpha / 255.0;
        // The glows: the tint from just past the top-right corner, wide; the accent low on the left, smaller and fainter.
        var g1 = recipe.Glow ?? recipe.Top;
        var g2 = recipe.Glow2 ?? g1;
        double s1 = recipe.Glow is null ? 0 : 0.20, s2 = recipe.Glow2 is null ? 0 : 0.09;
        double aspect = (double)width / Math.Max(1, height);
        double invR1Sq = 1.0 / ((0.95 * aspect) * (0.95 * aspect));
        double invR2Sq = 1.0 / ((0.7 * aspect) * (0.7 * aspect));
        uint seed = 0x9E3779B9;
        for (int y = 0; y < height; y++)
        {
            double v = (double)y / Math.Max(1, height - 1);
            // The base: top colour to bottom colour, eased so the darkening happens mostly in the lower half.
            double t = v * v * (3 - 2 * v);
            double br = recipe.Top.R + (recipe.Bottom.R - recipe.Top.R) * t;
            double bg = recipe.Top.G + (recipe.Bottom.G - recipe.Top.G) * t;
            double bb = recipe.Top.B + (recipe.Bottom.B - recipe.Top.B) * t;
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / Math.Max(1, width - 1);
                // Glow 1 centred past the top-right corner; glow 2 low on the left. Smooth falloff (no hard edge).
                double ax = (u - 1.05) * aspect, ay = v + 0.15, bx = (u + 0.05) * aspect, by = v - 1.05;
                double k1 = s1 * FalloffSq((ax * ax + ay * ay) * invR1Sq), k2 = s2 * FalloffSq((bx * bx + by * by) * invR2Sq);
                // A quiet vignette: the corners a little deeper.
                double vx = (u - 0.5) * 2, vy = (v - 0.45) * 2;
                double vig = 1 - 0.18 * Math.Clamp((vx * vx + vy * vy) / 2, 0, 1);
                double r = (br + (g1.R - br) * k1 + (g2.R - br) * k2) * vig;
                double g = (bg + (g1.G - bg) * k1 + (g2.G - bg) * k2) * vig;
                double b = (bb + (g1.B - bb) * k1 + (g2.B - bb) * k2) * vig;
                // Triangular dither of about one step: the gradient never shows bands.
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                double n1 = (seed & 0xFFFF) / 65535.0;
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                double n2 = (seed & 0xFFFF) / 65535.0;
                double dither = n1 - n2;
                int i = row + x * 4;
                pixels[i] = Byte(b * a + dither);
                pixels[i + 1] = Byte(g * a + dither);
                pixels[i + 2] = Byte(r * a + dither);
                pixels[i + 3] = recipe.Alpha;
            }
        }
        return pixels;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static double FalloffSq(double d2) { if (d2 >= 1) return 0; double f = 1 - d2; return f * f; }

    static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    static int _run;

    /// <summary>
    /// Paints the background for a window of <paramref name="dipWidth"/> × <paramref name="dipHeight"/> at <paramref name="scale"/>
    /// and hands back a brush (null when a newer paint started meanwhile). The bitmap is at most 2560 pixels wide; above that it
    /// is stretched, which the dither survives.
    /// </summary>
    public static async Task<Brush?> BrushAsync(Palette palette, double dipWidth, double dipHeight, double scale)
    {
        int run = ++_run;
        double k = Math.Min(scale, 2560 / Math.Max(1, dipWidth));
        int w = Math.Max(16, (int)Math.Round(dipWidth * k)), h = Math.Max(16, (int)Math.Round(dipHeight * k));
        var recipe = For(palette);
        var pixels = await Task.Run(() => Paint(recipe, w, h));
        if (run != _run) return null;
        var bitmap = new WriteableBitmap(w, h);
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
            await stream.WriteAsync(pixels);
        bitmap.Invalidate();
        return new ImageBrush { ImageSource = bitmap, Stretch = Stretch.Fill };
    }
}
