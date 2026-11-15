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

    /// <summary>A separate data folder (HL_DATA): a test instance, which must leave the user's own setup (start with Windows) alone.</summary>
    public static bool SeparateData { get { _ = DataRoot; return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HL_DATA")); } }

    /// <summary>
    /// A test instance that nobody gave a data folder (2.1.3): a build started from inside a source checkout (bin or dist next
    /// to a .git folder), or one started for a test (--selftest, HL_SELFTEST_ONLY, HL_PERF, HL_DEMO, HL_SHOTS). It gets
    /// %TEMP%\HybridLauncher-test through HL_DATA instead of the user's real settings. HL_REAL_DATA=1 opts out.
    /// </summary>
    static void GuardTestInstance()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HL_DATA")) || Environment.GetEnvironmentVariable("HL_REAL_DATA") == "1") return;
        bool test = Environment.GetCommandLineArgs().Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            || new[] { "HL_SELFTEST_ONLY", "HL_PERF", "HL_DEMO", "HL_SHOTS" }.Any(v => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)));
        if (!test)
            try
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git")) && Directory.Exists(Path.Combine(dir.FullName, "src", "HybridLauncher"))) { test = true; break; }
            }
            catch { }
        if (!test) return;
        var scratch = Path.Combine(Path.GetTempPath(), FileName + "-test");
        try
        {
            // The GitHub sign-in only, so update checks do not run into the anonymous hourly limit.
            var gh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FileName, "accounts", "github.bin");
            var dst = Path.Combine(scratch, "accounts", "github.bin");
            if (File.Exists(gh) && !File.Exists(dst)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(gh, dst); }
            // Test instances run in English, maximized.
            var settings = Path.Combine(scratch, "settings.json");
            if (!File.Exists(settings)) { Directory.CreateDirectory(scratch); File.WriteAllText(settings, "{ \"Language\": \"en\", \"WindowMaximized\": true }"); }
        }
        catch { }
        Environment.SetEnvironmentVariable("HL_DATA", scratch);
    }

    /// <summary>%LOCALAPPDATA%\HybridLauncher, or the HL_DATA environment variable (used by tests).</summary>
    public static string DataRoot
    {
        get
        {
            if (_dataRoot is null)
            {
                GuardTestInstance();
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
    /// <summary>Covers picked by the user for a game (Game settings → Library → Cover).</summary>
    public static string CoversDir => Ensure(Path.Combine(DataRoot, "covers"));
    public static string ProfilesDir => Ensure(Path.Combine(DataRoot, "profiles"));
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
