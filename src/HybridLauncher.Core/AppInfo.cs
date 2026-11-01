namespace HybridLauncher.Core;

/// <summary>App identity and the folders it owns. Everything the app writes outside game folders lives under <see cref="DataRoot"/>.</summary>
public static class AppInfo
{
    public const string Name = "Hybrid Launcher";
    /// <summary>The exe's name and the data folder's: no spaces.</summary>
    public const string FileName = "HybridLauncher";
    public const string UserAgent = "HybridLauncher/1.0";
    /// <summary>The GitHub repository: source, documentation and the releases the app updates itself from.</summary>
    public const string Repository = "LorenzoKy/Hybrid-Launcher";

    /// <summary>
    /// The app's name before 0.25. Read only to carry a user's data over: the old data folder, sign-ins encrypted under the
    /// old name, and cloud saves kept in the old folder name.
    /// </summary>
    public const string LegacyName = "Venom";

    /// <summary>
    /// The app's name from 0.25 to 1.0, before it became Hybrid Launcher: its data folder, the name its sign-ins were encrypted
    /// under, and the "hgl" prefix of its files. Read only to carry a user's data over, as with <see cref="LegacyName"/>.
    /// </summary>
    public const string PreviousName = "Hybrid Game Launcher";
    public const string PreviousFileName = "HybridGameLauncher";
    public const string PreviousShort = "hgl";

    static string? _dataRoot;

    /// <summary>%LOCALAPPDATA%\HybridLauncher, or the HL_DATA environment variable (used by tests).</summary>
    public static string DataRoot
    {
        get
        {
            if (_dataRoot is null)
            {
                var overridePath = Environment.GetEnvironmentVariable("HL_DATA");
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _dataRoot = string.IsNullOrWhiteSpace(overridePath) ? Path.Combine(local, FileName) : overridePath;
                if (string.IsNullOrWhiteSpace(overridePath))
                    foreach (var old in new[] { PreviousFileName, LegacyName }) MoveLegacyData(Path.Combine(local, old), _dataRoot);
                Directory.CreateDirectory(_dataRoot);
            }
            return _dataRoot;
        }
    }

    /// <summary>
    /// The first start after a rename: the old data folder (settings, sign-ins, backups, caches) becomes the new one, so
    /// nothing has to be set up again. A move on the same disk is instant; when the old folder is held open it is copied.
    /// </summary>
    static void MoveLegacyData(string legacy, string root)
    {
        if (Directory.Exists(root) || !File.Exists(Path.Combine(legacy, "settings.json"))) return;
        try { Directory.Move(legacy, root); }
        catch (IOException)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(root, Path.GetRelativePath(legacy, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: false);
                }
            }
            catch { /* best effort: the app starts fresh rather than not at all */ }
        }
        catch (UnauthorizedAccessException) { }
    }

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string CacheDir => Ensure(Path.Combine(DataRoot, "cache"));
    public static string ImageCacheDir => Ensure(Path.Combine(CacheDir, "images"));
    public static string DownloadsDir => Ensure(Path.Combine(CacheDir, "downloads"));
    public static string BackupsDir => Ensure(Path.Combine(DataRoot, "backups"));
    public static string LogsDir => Ensure(Path.Combine(DataRoot, "logs"));

    /// <summary>The universal ASI plugin folder: every enabled .asi in here is copied into each OptiScaler install.</summary>
    public static string AsiPluginsDir => Ensure(Path.Combine(DataRoot, "AsiPlugins"));

    /// <summary>The shared custom dll folder: one folder per entry (ffx, dlss, dlssg inside), copied into the OptiScaler installs that turn it on.</summary>
    public static string CustomDllsDir => Ensure(Path.Combine(DataRoot, "CustomDlls"));

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
