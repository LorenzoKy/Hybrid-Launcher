using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core;

/// <summary>A newer version of the app on GitHub: its version, notes, and the zip for this PC's processor.</summary>
public sealed record AppRelease(Version Version, string Tag, string Notes, string ZipUrl, long ZipSize, string PageUrl);

/// <summary>
/// The app's own updates, from the GitHub releases of <see cref="AppInfo.Repository"/>. The app runs from a plain folder (no
/// installer), so an update is: download the release's zip for this processor, unpack it next to the cache, start the new
/// exe from there with <see cref="ApplySwitch"/>; that copy waits for this one to close, copies itself over the app's
/// folder (only the files the release ships: nothing else in the folder is touched) and starts the updated app. The only
/// thing the user does is confirm. HL_UPDATE_FEED (a releases/latest JSON file or URL) replaces GitHub for tests.
/// </summary>
public static class AppUpdates
{
    public const string ApplySwitch = "--apply-update";
    public const string UpdatedSwitch = "--updated";

    /// <summary>The running version (major.minor.build).</summary>
    public static Version Current
    {
        get
        {
            var v = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AppUpdates).Assembly).GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    static string Feed => Environment.GetEnvironmentVariable("HL_UPDATE_FEED") is { Length: > 0 } f
        ? f
        : $"https://api.github.com/repos/{AppInfo.Repository}/releases/latest";

    /// <summary>The newest release when it is newer than this app; null when this is the newest (or none can be read).</summary>
    public static async Task<AppRelease?> CheckAsync(CancellationToken ct)
    {
        string json;
        if (File.Exists(Feed)) json = await File.ReadAllTextAsync(Feed, ct);
        else
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(HttpMethod.Get, Feed);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.Client.SendAsync(req, cts.Token);
            // No release yet (or the repository is still private): nothing to update to.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        var release = Parse(json);
        if (release is null || release.Version <= Current) return null;
        Log.Info($"Update: {release.Version} is out (this is {Current})");
        return release;
    }

    /// <summary>Reads a GitHub "release" object: the tag ("v0.26.0") and the zip asset for this processor.</summary>
    public static AppRelease? Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject o) return null;
        if (o["draft"]?.GetValue<bool>() == true || o["prerelease"]?.GetValue<bool>() == true) return null;
        var tag = o["tag_name"]?.GetValue<string>() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        foreach (var a in o["assets"] as JsonArray ?? [])
        {
            var name = a?["name"]?.GetValue<string>() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !name.Contains(Arch, StringComparison.OrdinalIgnoreCase)) continue;
            var url = a!["browser_download_url"]?.GetValue<string>();
            if (url is null) continue;
            return new AppRelease(version, tag, o["body"]?.GetValue<string>() ?? "", url, a["size"]?.GetValue<long>() ?? 0,
                o["html_url"]?.GetValue<string>() ?? $"https://github.com/{AppInfo.Repository}/releases");
        }
        return null;
    }

    static string UpdatesDir => AppInfo.Ensure(Path.Combine(AppInfo.CacheDir, "updates"));

    /// <summary>
    /// Downloads and unpacks the release; returns the new exe, ready for <see cref="Start"/>. Progress goes 0..1 over the
    /// download; the unpacking after it is quick.
    /// </summary>
    public static async Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken ct)
    {
        var zip = Path.Combine(UpdatesDir, $"{AppInfo.FileName}-{release.Version}-{Arch}.zip");
        var dir = Path.Combine(UpdatesDir, release.Version.ToString());
        if (File.Exists(release.ZipUrl)) File.Copy(release.ZipUrl, zip, overwrite: true);
        else await Http.DownloadFileAsync(release.ZipUrl, zip, release.ZipSize, progress, ct);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, dir), ct);
        File.Delete(zip);
        // The exe sits at the zip's root, or in one folder inside it.
        var exe = Directory.EnumerateFiles(dir, AppInfo.FileName + ".exe", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault()
            ?? throw new InvalidOperationException(L.T("The update has no app inside it."));
        Log.Info($"Update: {release.Version} unpacked in {Path.GetDirectoryName(exe)}");
        return exe;
    }

    /// <summary>Starts the unpacked new version, which updates this app's folder once this process has closed.</summary>
    public static void Start(string newExe)
    {
        var target = AppContext.BaseDirectory.TrimEnd('\\');
        var psi = new ProcessStartInfo(newExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(newExe)! };
        psi.ArgumentList.Add(ApplySwitch);
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// Run by the new version's copy (args: <see cref="ApplySwitch"/> target pid): waits for the old app to close, copies its
    /// own folder over the target and starts the updated app. Asks Windows for administrator rights only when the folder is not
    /// writable (an app placed in Program Files). Returns false when the copy failed; the old app is started again then.
    /// </summary>
    public static bool Apply(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals(ApplySwitch, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return false;
        var target = args[i + 1];
        if (i + 2 < args.Length && int.TryParse(args[i + 2], out var pid))
        {
            // Already gone, or not ours to wait for: the copy below retries files that are still held.
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(30_000); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        var source = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = Path.Combine(target, AppInfo.FileName + ".exe");
        try
        {
            CopyOver(source, target);
            Log.Info($"Update: copied into {target}");
            Process.Start(new ProcessStartInfo(exe) { ArgumentList = { UpdatedSwitch }, UseShellExecute = false, WorkingDirectory = target })?.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) when (!Elevated())
        {
            // A protected folder: the same copy once more, as administrator (Windows asks).
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                psi.ArgumentList.Add(ApplySwitch);
                psi.ArgumentList.Add(target);
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (Exception ex) { Log.Warn($"Update: elevation refused: {ex.Message}"); }
        }
        catch (Exception ex) { Log.Error("Update: copy failed", ex); }
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = target })?.Dispose(); } catch { }
        return false;
    }

    static bool Elevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Copies every file of <paramref name="from"/> into <paramref name="to"/>, retrying a file still held for a moment.</summary>
    static void CopyOver(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(file, dest, overwrite: true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
            }
        }
    }

    /// <summary>After an update: the unpacked copies and zips in the cache are not needed any more.</summary>
    public static void CleanUp()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(UpdatesDir)) Directory.Delete(d, recursive: true);
            foreach (var f in Directory.EnumerateFiles(UpdatesDir)) File.Delete(f);
        }
        catch (Exception ex) { Log.Warn($"Update clean-up: {ex.Message}"); }
    }
}
