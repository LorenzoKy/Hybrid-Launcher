using HybridLauncher.Controls;
using HybridLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HybridLauncher;

/// <summary>
/// Self-test "profile picture" (2.3-rc.6): the profile picture's framing with pictures made here, in the scratch data folder.
/// A phone-style JPEG stored on its side with an EXIF turn and a '#' in its name (rc.5: shown blank, saved squeezed), framed
/// as it comes, zoomed about a point and dragged, each save's pixels checked; then a file Windows cannot read, which must be
/// said so and left for the caller to copy as it is.
/// </summary>
public sealed partial class MainWindow
{
    async Task<int> SelfTestProfilePictureAsync()
    {
        int failures = 0;
        var dir = Path.Combine(AppInfo.DataRoot, "selftest-avatar");
        Directory.CreateDirectory(dir);
        var host = new Border { Opacity = 0.01, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        RootGrid.Children.Add(host);
        try
        {
            // Stored 400 Ã— 200, red on the left and blue on the right, EXIF orientation 6 (turn 90Â° clockwise to stand up):
            // upright it is 200 Ã— 400, red on top and blue below.
            var photo = Path.Combine(dir, "phone #1 photo.jpg");
            await WriteSidewaysJpegAsync(photo);

            async Task<AvatarCropper> Open(string path)
            {
                var c = new AvatarCropper(path);
                host.Child = c;
                for (int i = 0; i < 50 && c.SelfTestSize.Width <= 0; i++) await Task.Delay(100);
                await Task.Delay(300);
                return c;
            }
            async Task Check(string what, AvatarCropper c, (int X, int Y, char Colour)[] expect)
            {
                var file = Path.Combine(dir, $"{what}.png");
                if (!await c.SaveAsync(file)) throw new InvalidOperationException($"{what}: nothing saved");
                var (w, h, px) = await ReadPixelsAsync(file);
                if (w != 512 || h != 512) throw new InvalidOperationException($"{what}: saved {w} Ã— {h}, not 512 Ã— 512");
                foreach (var (x, y, colour) in expect)
                {
                    int i = (y * w + x) * 4;
                    byte b = px[i], g = px[i + 1], r = px[i + 2];
                    char got = r > 180 && b < 80 ? 'R' : b > 180 && r < 80 ? 'B' : '?';
                    if (got != colour) throw new InvalidOperationException($"{what}: pixel {x},{y} is rgb({r},{g},{b}), wanted {(colour == 'R' ? "red" : "blue")}");
                }
                Log.Info($"Self-test: profile picture {what}");
            }

            var cropper = await Open(photo);
            var size = cropper.SelfTestSize;
            if (size != (200, 400)) throw new InvalidOperationException($"the sideways photo reads as {size.Width} Ã— {size.Height}, not 200 Ã— 400 upright");
            // As it comes: the middle square, red over blue.
            await Check("as it comes", cropper, [(256, 60, 'R'), (256, 452, 'B'), (40, 200, 'R'), (470, 320, 'B')]);
            // Dragged down as far as it goes: the top square, all red.
            cropper.SelfTestFrame(1, 0, 0, 0, 1000);
            await Check("dragged to the top", cropper, [(256, 20, 'R'), (256, 490, 'R')]);
            // Back to the bottom (all blue), then zoomed 2Ã— about the circle's top edge: that edge stays on the line where red
            // meets blue, so the circle still shows only blue (zoomed about the centre it would have moved up into the red).
            cropper.SelfTestFrame(1, 0, 0, 0, -2000);
            cropper.SelfTestFrame(2, 0, -110, 0, 0);
            await Check("zoomed about a point", cropper, [(256, 40, 'B'), (256, 470, 'B')]);

            // A file no decoder reads: said so, nothing written (the caller copies it as it is).
            var broken = Path.Combine(dir, "broken.webp");
            await File.WriteAllBytesAsync(broken, [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50, 9, 9, 9, 9]);
            var bad = await Open(broken);
            if (bad.Readable) throw new InvalidOperationException("a damaged picture was read");
            if (await bad.SaveAsync(Path.Combine(dir, "broken.png"))) throw new InvalidOperationException("a damaged picture was saved as a PNG");
            Log.Info("Self-test: profile picture unreadable file left as it is");
        }
        catch (Exception ex)
        {
            failures++;
            Log.Error("Self-test FAILED: profile picture", ex);
        }
        finally { RootGrid.Children.Remove(host); }
        return failures;
    }

    static async Task WriteSidewaysJpegAsync(string path)
    {
        const int w = 400, h = 200;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool left = x < w / 2;
                px[i] = (byte)(left ? 0 : 255);     // B
                px[i + 1] = 0;                       // G
                px[i + 2] = (byte)(left ? 255 : 0); // R
                px[i + 3] = 255;
            }
        using var ms = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, px);
        await enc.BitmapProperties.SetPropertiesAsync([new("System.Photo.Orientation", new BitmapTypedValue((ushort)6, Windows.Foundation.PropertyType.UInt16))]);
        await enc.FlushAsync();
        ms.Seek(0);
        await using var fs = File.Create(path);
        await ms.AsStreamForRead().CopyToAsync(fs);
    }

    static async Task<(int W, int H, byte[] Pixels)> ReadPixelsAsync(string path)
    {
        using var stream = await Helpers.LocalFile.OpenReadAsync(path);
        var dec = await BitmapDecoder.CreateAsync(stream);
        var data = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return ((int)dec.PixelWidth, (int)dec.PixelHeight, data.DetachPixelData());
    }
}
