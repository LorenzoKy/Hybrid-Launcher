using HybridLauncher.Core;
using Microsoft.UI.Xaml;

namespace HybridLauncher;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();

        // Crash-free policy: log everything, keep the app alive, tell the user once.
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
            Window?.ShowError(L.T("Something went wrong"), e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal exception", e.ExceptionObject as Exception);
    }

    static Mutex? _singleInstance;
    static EventWaitHandle _showSignal = null!;
    static EventWaitHandle _launchSignal = null!;
    static string LaunchFile => Path.Combine(AppInfo.DataRoot, "launch-request.txt");

    /// <summary>Starts Hybrid Launcher again (a new language takes effect at start) and closes this instance.</summary>
    public static void Restart()
    {
        SettingsStore.Save();
        // The new instance must not find this one's lock and hand over to it.
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        _singleInstance = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true })?.Dispose();
        Current.Exit();
    }

    static int _exiting;

    /// <summary>
    /// Ends the app once the main window has closed for real. Exit() asks politely; a background thread then ends the process
    /// anyway if a stuck thread or a pending call still holds it (the window is already gone, settings are already saved).
    /// </summary>
    public static void ExitForSure()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
        new Thread(() =>
        {
            Thread.Sleep(3000);
            Log.Warn("Exit: still running 3 s after the window closed, ending the process");
            Environment.Exit(0);
        }) { IsBackground = true, Name = "Hybrid Launcher exit guard" }.Start();
        try { Current.Exit(); }
        catch (Exception ex) { Log.Warn($"Exit: {ex.Message}"); }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The copy of a new version that updates the app's folder: it does only that, then starts the updated app.
        if (Environment.GetCommandLineArgs().Contains(AppUpdates.ApplySwitch, StringComparer.OrdinalIgnoreCase))
        {
            try { AppUpdates.Apply(Environment.GetCommandLineArgs()); }
            catch (Exception ex) { Log.Error("Update: apply failed", ex); }
            Environment.Exit(0);
            return;
        }
        // One Hybrid Launcher at a time: two instances would overwrite each other's settings and could install into the same folder.
        // A separate data folder (HL_DATA, used for tests) is a separate Hybrid Launcher: it may run beside the normal one.
        var name = @"Local\HybridLauncher-SingleInstance";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HL_DATA")))
            name += "-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(AppInfo.DataRoot.ToLowerInvariant())))[..12];
        _singleInstance = new Mutex(true, name, out bool first);
        var launch = Shortcuts.LaunchRequest(Environment.GetCommandLineArgs());
        if (!first)
        {
            // A desktop shortcut: the running Hybrid Launcher starts the game (it reads the id from a file next to its settings).
            if (launch is not null && EventWaitHandle.TryOpenExisting(name + "-launch", out var play))
            {
                try { File.WriteAllText(LaunchFile, launch); } catch { }
                using (play) play.Set();
                Exit();
                return;
            }
            // The other Hybrid Launcher may be hidden in the notification area (no window to find): ask it to show itself.
            if (EventWaitHandle.TryOpenExisting(name + "-show", out var show)) using (show) show.Set();
            else Helpers.SingleInstance.FocusOther();
            Exit();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
        _launchSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-launch");

        Log.Info($"{AppInfo.Name} {AppUpdates.Current.ToString(3)} starting, data in {AppInfo.DataRoot}");
        Log.Info($"Windows {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        SettingsStore.Load();
        L.Init(AppState.Settings.Language);
        Helpers.Startup.CarryOver();
        Window = new MainWindow();
        // Started with Windows: only the icon in the notification area, the window opens from there.
        // From a desktop shortcut: the game starts, Hybrid Launcher stays in the notification area to watch it.
        bool tray = launch is not null || Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (tray) Window.StartInTray();
        else Window.Activate();
        Helpers.MediaPolicy.Attach(Window, hidden: tray);
        var ui = Window.DispatcherQueue;
        var signal = _showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne()) ui.TryEnqueue(() => Window?.ShowFromTray());
        }) { IsBackground = true, Name = "Hybrid Launcher show signal" }.Start();
        var launchSignal = _launchSignal;
        new Thread(() =>
        {
            while (launchSignal.WaitOne())
            {
                string? id = null;
                try { id = File.ReadAllText(LaunchFile).Trim(); File.Delete(LaunchFile); } catch { }
                if (!string.IsNullOrEmpty(id)) ui.TryEnqueue(() => Window?.LaunchFromShortcut(id));
            }
        }) { IsBackground = true, Name = "Hybrid Launcher launch signal" }.Start();
        if (launch is not null) Window.LaunchFromShortcut(launch);
        if (Environment.GetCommandLineArgs().Contains("--selftest", StringComparer.OrdinalIgnoreCase)) _ = Window.RunSelfTestAsync();
        // Steam's sign-in from inside the shipped (trimmed) app, where it can fail although the smoke test passes: logged.
        if (Environment.GetCommandLineArgs().Contains("--steamprobe", StringComparer.OrdinalIgnoreCase))
        {
            SteamKit2.DebugLog.AddListener((category, message) => Log.Info($"SteamKit2 {category}: {message}"));
            SteamKit2.DebugLog.Enabled = true;
            _ = Task.Run(async () =>
            {
                try { Log.Info($"Steam probe: anonymous {await HybridLauncher.Core.Accounts.SteamAccount.ProbeAsync(CancellationToken.None)}, QR {(await HybridLauncher.Core.Accounts.SteamAccount.QrProbeAsync(CancellationToken.None)).Length > 0}"); }
                catch (Exception ex) { Log.Warn($"Steam probe FAILED: {ex}"); }
            });
        }
    }
}
