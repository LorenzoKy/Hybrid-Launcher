using System.Runtime.InteropServices;
using HybridLauncher.Core;
using HybridLauncher.Core.Achievements;
using Windows.Graphics.Imaging;

namespace HybridLauncher.Helpers;

/// <summary>
/// What else happens with an unlock (Settings → Achievements → On unlock), each one on or off: a screenshot of the screen the game
/// is on (with the pop-up on it, or just before it), saved as PNG under the game's name; a short rumble on a connected controller;
/// the entry in the unlock history (<see cref="AchievementHistory"/>).
/// </summary>
public static class UnlockExtras
{
    /// <summary>Hooks the pop-up's opening (called once at start).</summary>
    public static void Start() => AchievementPopup.Opening += OnOpening;

    /// <summary>Where screenshots go: the folder picked, else Pictures\Hybrid Launcher\Achievements.</summary>
    public static string ScreenshotRoot =>
        AppState.Settings.AchScreenshotDir is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppInfo.Name, "Achievements");

    static void OnOpening(Toast t)
    {
        // Samples and previews (no game id) and progress pop-ups take no picture and write no history.
        if (t.AchievementKey is null || t.Progress is not null) return;
        var s = AppState.Settings;
        if (s.AchRumble) Rumble();
        if (!s.AchScreenshot) return;
        var folder = Path.Combine(ScreenshotRoot, Safe(t.Game ?? "Game"));
        var file = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {Safe(t.Name)}.png");
        if (!s.AchScreenshotWithPopup) _ = SaveAsync(Grab(), file, t);
        else _ = Task.Delay(750).ContinueWith(_ => App.Window?.DispatcherQueue.TryEnqueue(() => _ = SaveAsync(Grab(), file, t)), TaskScheduler.Default);
    }

    static string Safe(string name)
    {
        var s = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        return s.Length > 80 ? s[..80] : s.Length == 0 ? "_" : s;
    }

    /// <summary>The screen the game is on (the window in front), as BGRA pixels.</summary>
    static (byte[] Pixels, int W, int H)? Grab()
    {
        try
        {
            var mon = MonitorFromWindow(GetForegroundWindow(), 2 /* nearest */);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref info)) return null;
            int x = info.rcMonitor.Left, y = info.rcMonitor.Top, w = info.rcMonitor.Right - x, h = info.rcMonitor.Bottom - y;
            var screen = GetDC(0);
            var mem = CreateCompatibleDC(screen);
            var bmp = CreateCompatibleBitmap(screen, w, h);
            var old = SelectObject(mem, bmp);
            try
            {
                BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000 /* SRCCOPY | CAPTUREBLT */);
                var bi = new BITMAPINFOHEADER { biSize = 40,biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                var pixels = new byte[w * h * 4];
                GetDIBits(mem, bmp, 0, (uint)h, pixels, ref bi, 0);
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return (pixels, w, h);
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(bmp);
                DeleteDC(mem);
                ReleaseDC(0, screen);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Unlock screenshot: {ex.Message}");
            return null;
        }
    }

    static async Task SaveAsync((byte[] Pixels, int W, int H)? shot, string file, Toast t)
    {
        if (shot is not { } s) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)s.W, (uint)s.H, 96, 96, s.Pixels);
            await enc.FlushAsync();
            var bytes = new byte[stream.Size];
            stream.Seek(0);
            using (var reader = new Windows.Storage.Streams.DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }
            await File.WriteAllBytesAsync(file, bytes);
            Log.Info($"Unlock screenshot: {file}");
            AchievementHistory.AttachScreenshot(t.GameId, t.AchievementKey!, file);
        }
        catch (Exception ex) { Log.Warn($"Unlock screenshot save: {ex.Message}"); }
    }

    /// <summary>A short double pulse on every connected XInput controller.</summary>
    public static void Rumble()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (int pulse = 0; pulse < 2; pulse++)
                {
                    for (uint i = 0; i < 4; i++) XInputSetState(i, new XINPUT_VIBRATION { Left = 30000, Right = 42000 });
                    await Task.Delay(140);
                    for (uint i = 0; i < 4; i++) XInputSetState(i, default);
                    await Task.Delay(90);
                }
            }
            catch (Exception ex) { Log.Warn($"Unlock rumble: {ex.Message}"); }
        });
    }

    [StructLayout(LayoutKind.Sequential)] struct XINPUT_VIBRATION { public ushort Left, Right; }
    [DllImport("xinput1_4.dll")] static extern int XInputSetState(uint index, XINPUT_VIBRATION v);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int pad0, pad1, pad2;
    }
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint mon, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
}
