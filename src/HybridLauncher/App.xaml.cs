using HybridLauncher.Core;
using Microsoft.UI.Xaml;

namespace HybridLauncher;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    /// <summary>UI exceptions caught so far (self-test: a step that shows "Something went wrong" fails).</summary>
    internal static int UiErrors;

    public App()
    {
        InitializeComponent();

        // Crash-free policy: log everything, keep the app alive, tell the user once.
        UnhandledException += (_, e) =>
        {
            // WinUI's own text (e.Message) often names the element or call that failed when the exception has no stack.
            Log.Error(string.IsNullOrWhiteSpace(e.Message) || e.Message == e.Exception.Message ? "Unhandled UI exception"
                : $"Unhandled UI exception (WinUI: {e.Message.Trim()}; HRESULT 0x{e.Exception.HResult:X8})", e.Exception);
            UiErrors++;
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
    static EventWaitHandle _shownSignal = null!;
    // Set once the window exists: a "show yourself" from another copy that comes while this one is still starting is kept here.
    static volatile Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    static volatile bool _showAsked;
    static string LaunchFile => Path.Combine(AppInfo.DataRoot, "launch-request.txt");

    /// <summary>Starts Hybrid Launcher again (a new language takes effect at start) and closes this instance.</summary>
    public static void Restart()
    {
        SettingsStore.Save();
        SettingsStore.Flush();
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
    /// <summary>
    /// From the window's close on, every exception thrown (even one caught) goes to the log with where it came from: a crash
    /// while the app ends leaves nothing else behind (no dialog, nothing in the event log).
    /// </summary>
    public static void WatchShutdown()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            // Cancelled work (an upload the close stops) is the close doing its job, not a crash.
            if (e.Exception is OperationCanceledException) return;
            try { Log.Warn($"Shutdown: {e.Exception.GetType().Name} on thread {Environment.CurrentManagedThreadId}: {e.Exception.Message}\n{new System.Diagnostics.StackTrace(1, false)}"); }
            catch { }
        };
    }

    static int _watching;

    /// <summary>The window has closed (or the app is ending): delayed UI work must not start any more.</summary>
    /// <summary>Started with --selftest: work the page would spread over several frames is done at once, as the steps expect.</summary>
    public static bool SelfTesting { get; } = Environment.GetCommandLineArgs().Contains("--selftest", StringComparer.OrdinalIgnoreCase);

    public static bool ShuttingDown => Volatile.Read(ref _watching) == 1 || Volatile.Read(ref _exiting) == 1;

    public static void ExitForSure()
    {
        WatchShutdown();
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
        SettingsStore.Flush();
        new Thread(() =>
        {
            Thread.Sleep(3000);
            Log.Warn("Exit: still running 3 s after the window closed, ending the process");
            Log.Flush();
            Environment.Exit(0);
        }) { IsBackground = true, Name = "Hybrid Launcher exit guard" }.Start();
        try { Current.Exit(); }
        catch (Exception ex) { Log.Warn($"Exit: {ex.Message}"); }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 2.3-rc.2: whatever stops the start is said in a message (with the log's place), not a window that never comes.
        try { Launch(); }
        catch (Exception ex)
        {
            Helpers.StartupGuard.Failed(ex);
            Environment.Exit(3);
        }
    }

    void Launch()
    {
        // The copy of a new version that updates the app's folder: it does only that, then starts the updated app.
        if (Environment.GetCommandLineArgs().Contains(AppUpdates.ApplySwitch, StringComparer.OrdinalIgnoreCase))
        {
            try { AppUpdates.Apply(Environment.GetCommandLineArgs()); }
            catch (Exception ex) { Log.Error("Update: apply failed", ex); }
            Environment.Exit(0);
            return;
        }
        // Started again by a copy whose UI thread stopped (UiRecovery): that copy ends first, so its lock is gone.
        Helpers.UiRecovery.WaitForStoppedCopy(Environment.GetCommandLineArgs());
        // One Hybrid Launcher at a time: two instances would overwrite each other's settings and could install into the same folder.
        // A separate data folder (HL_DATA, used for tests) is a separate Hybrid Launcher: it may run beside the normal one.
        var name = @"Local\HybridLauncher-SingleInstance";
        if (AppInfo.SeparateData)
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
            // Started by Windows at sign-in a second time (two start-up entries, a scheduled task): nothing to show.
            if (Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase)) { Exit(); return; }
            // The other Hybrid Launcher may be hidden in the notification area (no window to find): ask it to show itself, and
            // wait for it to say it did (2.3-rc.2: it could not come to the front, so the first double-click did nothing).
            if (Helpers.StartupGuard.HandOver(name)) { Exit(); return; }
            // No answer: a copy closed a moment ago is still ending (its lock goes with it), or a copy is stuck.
            Log.Warn("Start: the running copy did not answer, waiting for its lock");
            if (!Helpers.StartupGuard.TakeOver(_singleInstance, TimeSpan.FromSeconds(8)) && !Helpers.StartupGuard.AskToEndStuckCopy(_singleInstance))
            {
                Exit();
                return;
            }
            Log.Info("Start: took over from a copy that had ended");
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
        _launchSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-launch");
        _shownSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-shown");
        {
            // Listened to from now on, before the slow part of the start: another copy asking now is answered at once (the window
            // will open), and a start meant for the notification area opens the window instead.
            var signal = _showSignal;
            var shown = _shownSignal;
            new Thread(() =>
            {
                while (signal.WaitOne())
                {
                    if (_ui is not { } q) { _showAsked = true; shown.Set(); continue; }
                    // Answered from the UI thread once the window is up: a stuck UI thread gives no answer.
                    if (!q.TryEnqueue(() => { Window?.ShowFromTray(); shown.Set(); })) shown.Set();
                }
            }) { IsBackground = true, Name = "Hybrid Launcher show signal" }.Start();
        }

        Log.Info($"{AppInfo.Name} {AppUpdates.DisplayVersion} starting, data in {AppInfo.DataRoot}");
        if (Helpers.UiRecovery.IsRecovery(Environment.GetCommandLineArgs())) Log.Warn("Started again: the previous copy's UI thread had stopped");
        Log.Info($"Windows {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        SettingsStore.Load();
        // "Hybrid Config and Stats": what another PC sent (settings, play time, history) is merged in before anything reads
        // the settings; a few seconds at most, then the next start tries again.
        if (!SelfTesting) HybridLauncher.Core.Cloud.ConfigSync.PullAtStart(TimeSpan.FromSeconds(4));
        // Vsync for the app's own motion: always on, no setting (Helpers.FrameSync).
        Helpers.FrameSync.Init();
        L.Init(AppState.Settings.Language);
        Helpers.Startup.CarryOver(AppState.Settings);
        AchievementPopup.MoveOldSettings();
        Window = new MainWindow();
        // Started with Windows: only the icon in the notification area, the window opens from there.
        // From a desktop shortcut: the game starts, Hybrid Launcher stays in the notification area to watch it.
        bool tray = launch is not null || Helpers.UiRecovery.StartedHidden(Environment.GetCommandLineArgs()) || (Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase) && !AppState.Settings.StartOpensWindow);
        if (_showAsked && launch is null) tray = false;
        // Tests only (a separate data folder): HL_POPUP_TEST=<seconds> starts in the notification area and shows one unlock after
        // that many seconds, to try the pop-up over a game being played without any window coming up over it.
        int popupTest = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HL_DATA")) ? 0
            : int.TryParse(Environment.GetEnvironmentVariable("HL_POPUP_TEST"), out int pt) ? pt : 0;
        if (popupTest > 0) tray = true;
        if (tray) Window.StartInTray();
        else Window.ShowAtStart();
        Helpers.MediaPolicy.Attach(Window, hidden: tray);
        var ui = Window.DispatcherQueue;
        _ui = ui;
        // Asked while the window was being made: shown now.
        if (_showAsked && tray) ui.TryEnqueue(() => Window?.ShowFromTray());
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
        // Tests only (a separate data folder): the UI thread stops, idle, 8 s after start, as when WinUI stops serving it.
        if (Environment.GetEnvironmentVariable("HL_TEST_UI_STOP") == "1" && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HL_DATA")) && !Helpers.UiRecovery.IsRecovery(Environment.GetCommandLineArgs()))
            Helpers.Later.Run(ui, TimeSpan.FromSeconds(8), () => { Log.Warn("Test: stopping the UI thread"); Log.Flush(); Thread.Sleep(Timeout.Infinite); });
        if (Environment.GetCommandLineArgs().Contains("--selftest", StringComparer.OrdinalIgnoreCase)) _ = Window.RunSelfTestAsync();
        if (popupTest > 0)
            Helpers.Later.Run(ui, TimeSpan.FromSeconds(popupTest), () =>
            {
                Log.Info("Pop-up test: unlock now");
                AchievementPopup.Show(new Toast("Test unlock", "A pop-up over the game being played.", null, "Game: Pop-up test", 10));
            });
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
