using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HybridLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace HybridLauncher.Helpers;

static class Native
{
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(nint hwnd);
}

public static class SingleInstance
{
    /// <summary>Brings the already running Hybrid Launcher window to the front.</summary>
    public static void FocusOther()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(self.ProcessName))
        {
            using (p)
            {
                if (p.Id == self.Id || p.MainWindowHandle == 0) continue;
                if (Native.IsIconic(p.MainWindowHandle)) Native.ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                Native.SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }
}

/// <summary>Exe icons for the game list, loaded once through the shell thumbnail API.</summary>
public static class IconCache
{
    const int E_PENDING = unchecked((int)0x8000000A);
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // The shell answers E_PENDING when asked for many thumbnails at once: one at a time, with a short retry.
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ImageSource?> GetAsync(string exe)
    {
        if (Cache.TryGetValue(exe, out var cached)) return cached;

        // Package logos (Game Pass) are plain images, not exes.
        if (Path.GetExtension(exe).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
        {
            ImageSource? image = null;
            try
            {
                if (File.Exists(exe))
                {
                    var bmp = new BitmapImage { DecodePixelWidth = 48 };
                    var file = await StorageFile.GetFileFromPathAsync(exe);
                    using var stream = await file.OpenReadAsync();
                    await bmp.SetSourceAsync(stream);
                    image = bmp;
                }
            }
            catch (Exception ex) { Log.Warn($"Logo {exe} failed: {ex.Message}"); }
            Cache[exe] = image;
            return image;
        }

        await Gate.WaitAsync();
        try
        {
            if (Cache.TryGetValue(exe, out cached)) return cached;
            ImageSource? result = null;
            for (int attempt = 0; attempt < 4 && File.Exists(exe); attempt++)
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(exe);
                    using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
                    if (thumb is not null && thumb.Type == ThumbnailType.Icon)
                    {
                        var bmp = new BitmapImage();
                        await bmp.SetSourceAsync(thumb);
                        result = bmp;
                    }
                    break;
                }
                catch (Exception ex) when (ex.HResult == E_PENDING)
                {
                    await Task.Delay(250 * (attempt + 1));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Icon for {exe} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                    break;
                }
            }
            Cache[exe] = result;
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>Windows App SDK pickers: they take a window id, so they also work when Hybrid Launcher runs elevated.</summary>
public static class Pickers
{
    public static async Task<string?> PickExeAsync(Window owner)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".exe");
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<string?> PickFileAsync(Window owner, params string[] extensions)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<List<string>> PickFilesAsync(Window owner, params string[] extensions)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        var results = await picker.PickMultipleFilesAsync();
        var paths = new List<string>();
        if (results is not null)
            for (int i = 0; i < results.Count; i++) paths.Add(results[i].Path);
        return paths;
    }

    public static async Task<string?> PickFolderAsync(Window owner)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(owner.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}

public static class Elevation
{
    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts a new elevated instance and closes this one. Returns false if the user declined UAC.</summary>
    public static bool RestartElevated()
    {
        try
        {
            SettingsStore.Save();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            Application.Current.Exit();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
