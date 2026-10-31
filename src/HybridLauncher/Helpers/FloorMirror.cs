using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using HybridLauncher.Core;
using Windows.Graphics.Imaging;

namespace HybridLauncher.Helpers;

/// <summary>
/// The reflection under a game's banner, Steam style: the bottom of the art flipped, blurred (a real gaussian, not a tiny bitmap
/// stretched, which looks blocky) and faded out downwards in its own alpha. Built once per game off the UI thread at 480 px wide,
/// then drawn as a plain image: nothing is recomputed while the page scrolls.
/// </summary>
public static class FloorMirror
{
    const int Width = 480, Radius = 7;

    /// <summary>
    /// The mirror for art shown <c>UniformToFill</c> in a box of <paramref name="boxAspect"/> (width / height), covering the
    /// <paramref name="fraction"/> of the box height under it. Null when the picture cannot be read.
    /// </summary>
    public static async Task<WriteableBitmap?> CreateAsync(string uri, double boxAspect, double fraction, CancellationToken ct)
    {
        var made = await Task.Run(() => Build(uri, boxAspect, fraction), ct);
        if (made is not { } m || ct.IsCancellationRequested) return null;
        var bitmap = new WriteableBitmap(m.W, m.H);
        using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(m.Pixels, ct);
        bitmap.Invalidate();
        return bitmap;
    }

    static async Task<(byte[] Pixels, int W, int H)?> Build(string uri, double boxAspect, double fraction)
    {
        try
        {
            var path = Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : uri;
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
            int sw = Width, sh = Math.Max(1, (int)Math.Round(Width * (double)decoder.PixelHeight / decoder.PixelWidth));
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = (uint)sw, ScaledHeight = (uint)sh, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var src = data.DetachPixelData();

            // The part of the picture the banner shows (centred crop to the box's shape), and its bottom rows.
            int cw = sw, ch = sh;
            if ((double)sw / sh > boxAspect) cw = Math.Max(1, (int)(sh * boxAspect));
            else ch = Math.Max(1, (int)(sw / boxAspect));
            int x0 = (sw - cw) / 2, bottom = (sh + ch) / 2 - 1;
            int rows = Math.Clamp((int)Math.Ceiling(ch * fraction), 2, ch);

            // Flipped: row 0 is the art's bottom row.
            var px = new float[cw * rows * 4];
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < cw; x++)
            {
                int s = ((bottom - y) * sw + x0 + x) * 4, d = (y * cw + x) * 4;
                for (int c = 0; c < 4; c++) px[d + c] = src[s + c];
            }

            // Three box blurs each way: close to a gaussian, and cheap.
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(px, cw, rows, Radius, horizontal: true);
                BoxBlur(px, cw, rows, Radius, horizontal: false);
            }

            // Faded out downwards: full at the art's edge, about half way at 45 %, gone at the bottom (premultiplied: every channel).
            var outPx = new byte[cw * rows * 4];
            for (int y = 0; y < rows; y++)
            {
                double t = (double)y / (rows - 1);
                double fade = t < 0.45 ? 1 - t / 0.45 * 0.44 : 0.56 * (1 - (t - 0.45) / 0.55);
                for (int i = y * cw * 4; i < (y + 1) * cw * 4; i++) outPx[i] = (byte)Math.Clamp(px[i] * fade, 0, 255);
            }
            return (outPx, cw, rows);
        }
        catch (Exception ex)
        {
            Log.Warn($"Floor mirror: {ex.Message}");
            return null;
        }
    }

    /// <summary>A running-sum box blur along rows or columns, edges clamped.</summary>
    static void BoxBlur(float[] px, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        var line = new float[length * 4];
        float scale = 1f / (2 * r + 1);
        for (int l = 0; l < lines; l++)
        {
            int At(int i) => (horizontal ? l * w + i : i * w + l) * 4;
            for (int i = 0; i < length; i++)
                for (int c = 0; c < 4; c++) line[i * 4 + c] = px[At(i) + c];
            for (int c = 0; c < 4; c++)
            {
                float sum = 0;
                for (int k = -r; k <= r; k++) sum += line[Math.Clamp(k, 0, length - 1) * 4 + c];
                for (int i = 0; i < length; i++)
                {
                    px[At(i) + c] = sum * scale;
                    sum += line[Math.Min(i + r + 1, length - 1) * 4 + c] - line[Math.Max(i - r, 0) * 4 + c];
                }
            }
        }
    }
}
