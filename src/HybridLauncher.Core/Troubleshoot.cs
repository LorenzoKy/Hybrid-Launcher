using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;

namespace HybridLauncher.Core;

/// <summary>
/// What an error most likely means, for the line under an error message ("Possible causes: …"), and the section of
/// docs/troubleshooting.md that explains it further. Read from the exception's type, its HRESULT and its (English) text.
/// </summary>
public static class Troubleshoot
{
    public const string Guide = $"https://github.com/{AppInfo.Repository}/blob/main/docs/troubleshooting.md";

    /// <summary>The likely cause of <paramref name="ex"/> (or of one inside it), or null when nothing useful can be said.</summary>
    public static string? Cause(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (CauseOf(e) is { } cause) return cause;
        return Cause(ex.Message);
    }

    static string? CauseOf(Exception e) => e switch
    {
        TimeoutException or TaskCanceledException or HttpRequestException or SocketException => Offline,
        UnauthorizedAccessException => Denied,
        DirectoryNotFoundException or DriveNotFoundException => Missing,
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => InUse,
        IOException io when (io.HResult & 0xFFFF) is 112 or 39 => DiskFull,
        Win32Exception { NativeErrorCode: 740 } => Elevation,
        Win32Exception { NativeErrorCode: 1223 } => L.T("Possible causes: Windows asked for permission and the request was turned down."),
        Win32Exception { NativeErrorCode: 2 or 3 } => Missing,
        InvalidDataException => L.T("Possible causes: a damaged download or archive. Try again; if it keeps failing, clear the cache in Settings → Storage."),
        _ => null,
    };

    /// <summary>The likely cause read from an error's text alone (for messages that come without their exception).</summary>
    public static string? Cause(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        bool Has(params string[] words) => words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Has("being used by another process")) return InUse;
        if (Has("access to the path", "access is denied", "denied", "read-only")) return Denied;
        if (Has("not enough space", "disk is full", "disk full")) return DiskFull;
        if (Has("requires elevation")) return Elevation;
        if (Has("timed out", "timeout", "did not answer", "no such host", "network", "connection", "ssl", "unreachable")) return Offline;
        if (Has("quota", "over storage", "storage is full")) return L.T("Possible causes: the cloud is full. Free some space there or turn off cloud saves for the games you do not need.");
        if (Has("could not find a part of the path", "could not find file", "does not exist")) return Missing;
        if (Has("rate limit", "too many requests")) return L.T("Possible causes: the service limits how often it can be asked (GitHub allows 60 checks an hour). Wait a while and try again.");
        return null;
    }

    /// <summary>The section of the troubleshooting guide for an error with this title and text.</summary>
    public static string Link(string title, string message)
    {
        var text = title + " " + message;
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        var anchor =
            Has("cloud", "OneDrive", "MEGA", "Google Drive", "saves") ? "cloud-saves" :
            Has("start with Windows") ? "start-with-windows" :
            Has("did not start", "game exe", "launcher", "is running", "still running") ? "a-game-does-not-start" :
            Has("OptiScaler", "ReShade", "Luma", "RenoDX", "dll", "mod", "preset", "plugin", "build", "install") ? "mods-optiscaler-and-reshade" :
            Has("sign in", "download", "update") ? "downloads-sign-in-and-updates" :
            Has("HDR", "brightness", "display", "colour", "color") ? "hdr-and-display" :
            "";
        return anchor.Length == 0 ? Guide : Guide + "#" + anchor;
    }

    static string Offline => L.T("Possible causes: no internet connection, or the service is slow or down. Check your connection and try again in a few minutes.");
    static string Denied => L.T("Possible causes: the file is in use or protected. Close the game and its launcher, let OneDrive or your antivirus finish with the folder, and check Controlled folder access in Windows Security.");
    static string InUse => L.T("Possible causes: the game, its launcher or a cloud app is still using the file. Close them and try again.");
    static string Missing => L.T("Possible causes: the folder or file is gone. A drive may be disconnected, a cloud app (OneDrive, Google Drive) closed, or the game moved or uninstalled.");
    static string DiskFull => L.T("Possible causes: the disk is full. Free some space and try again.");
    static string Elevation => L.T("Possible causes: the folder needs administrator rights (Program Files, say). Move the game or Hybrid Launcher to a folder of yours.");
}
