using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;

namespace HybridLauncher.Helpers;

/// <summary>
/// When Hybrid Launcher's videos (trailers, the looping clips of store descriptions) may play by themselves. A game launcher must not
/// take GPU time from the game it started, so nothing plays while a game runs or while the window is minimized or in the
/// notification area. A trailer the user paused keeps the other trailers paused until they play one again (for the session, like
/// the trailer's sound); the looping clips of descriptions keep moving. With hardware acceleration off nothing plays by itself and the GPU effects are left out.
/// </summary>
public static class MediaPolicy
{
    static DispatcherQueue? _ui;
    static bool _windowHidden;
    static bool _background;

    /// <summary>Raised on the UI thread whenever <see cref="MayAutoplay"/> or <see cref="Effects"/> may have changed.</summary>
    public static event Action? Changed;

    /// <summary>The user paused a video: the others wait until they play one again.</summary>
    public static bool UserPaused { get; private set; }

    /// <summary>A game started from Hybrid Launcher (or seen running) is on: no video plays.</summary>
    public static bool GameRunning { get; private set; }

    /// <summary>
    /// Hardware acceleration (Settings → Graphics, on by default): blur, reflections and videos that start by themselves. It is also
    /// paused while a game runs (unless the user turned that off), so a launcher left open beside a game takes no GPU time from it,
    /// and it comes back by itself when the game closes.
    /// </summary>
    public static bool Effects => AppState.Settings.HardwareAcceleration && !(AppState.Settings.PauseEffectsInGame && GameRunning);

    /// <summary>Whether a video may start or go on by itself now.</summary>
    public static bool MayAutoplay => Effects && !UserPaused && !GameRunning && !_windowHidden;

    /// <summary>
    /// Whether the looping clips of a description (animated GIFs and their video versions) may play: like <see cref="MayAutoplay"/>
    /// but not held by the user pausing the trailer. They are pictures that move, not videos, and pausing a trailer leaves them on.
    /// </summary>
    public static bool MayLoop => Effects && !GameRunning && !_windowHidden && !InBackground;

    /// <summary>
    /// The window is on screen but another app is in front, and the background saver is on (Settings → Graphics): the moving clips
    /// and muted trailers pause and nothing polls, until it is in front again.
    /// </summary>
    public static bool InBackground => _background && !_windowHidden && AppState.Settings.BackgroundSaver;

    /// <summary>Nobody is working with the window (hidden, minimized or behind another app): timers that refresh what it shows can rest.</summary>
    public static bool Quiet => _windowHidden || InBackground;

    /// <summary>The window is minimized or in the notification area: nothing on it needs refreshing.</summary>
    public static bool WindowHidden => _windowHidden;

    /// <summary>For the log: what holds the videos back.</summary>
    public static string Why => $"effects {Effects}, user paused {UserPaused}, game running {GameRunning}, window hidden {_windowHidden}, background {InBackground}";

    /// <summary>Whether a video may play at all now (the user pressing Play still wins over the user pause, not over a game).</summary>
    public static bool Suspended => GameRunning || _windowHidden;

    /// <summary>Follows the main window (minimized, hidden in the notification area) and the running games.</summary>
    public static void Attach(Window window, bool hidden)
    {
        _ui = window.DispatcherQueue;
        _windowHidden = hidden;
        window.VisibilityChanged += (_, e) => SetHidden(!e.Visible || IsMinimized(window));
        // Activated also means shown: the first Activate of a window that started in the notification area raises no visibility event.
        window.Activated += (_, e) =>
        {
            bool front = e.WindowActivationState != WindowActivationState.Deactivated;
            if (front && window.AppWindow.IsVisible) SetHidden(IsMinimized(window));
            SetBackground(!front);
        };
        Downloads.Changed += () => Post(UpdateEfficiency);
        window.AppWindow.Changed += (_, e) =>
        {
            if (e.DidPresenterChange || e.DidSizeChange || e.DidVisibilityChange) SetHidden(!window.AppWindow.IsVisible || IsMinimized(window));
        };
        GameSessions.Changed += _ => Post(() =>
        {
            bool running = GameSessions.Active().Count > 0;
            if (running == GameRunning) return;
            GameRunning = running;
            UpdateEfficiency();
            Raise();
        });
    }

    /// <summary>The user pressed pause (true) or play (false) on a video.</summary>
    public static void SetUserPaused(bool paused)
    {
        if (paused == UserPaused) return;
        UserPaused = paused;
        Raise();
    }

    /// <summary>Settings changed hardware acceleration.</summary>
    public static void NotifyEffectsChanged()
    {
        UpdateEfficiency();
        Raise();
    }

    static bool IsMinimized(Window window) =>
        window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>The window went to the notification area (true) or came back from it (false): said by the code that does it, since
    /// a window that was never shown (a start with Windows) raises no visibility event when it first opens.</summary>
    public static void SetWindowHidden(bool hidden) => SetHidden(hidden);

    static void SetHidden(bool hidden)
    {
        if (hidden == _windowHidden) return;
        Log.Info($"Media policy: window {(hidden ? "hidden" : "shown")}");
        _windowHidden = hidden;
        UpdateEfficiency();
        Raise();
        // Hidden for a while (not a quick minimize and restore): give back the memory the window no longer needs.
        if (hidden && _ui is not null) Later.Run(_ui, TimeSpan.FromSeconds(20), () => { if (_windowHidden) Efficiency.Trim(); });
    }

    static void SetBackground(bool background)
    {
        if (background == _background) return;
        _background = background;
        if (AppState.Settings.BackgroundSaver) Log.Info($"Media policy: {(background ? "in the background, resting" : "in front again")}");
        UpdateEfficiency();
        Raise();
    }

    static bool _efficient;
    static int _awake;

    /// <summary>
    /// An achievement pop-up is on screen: full speed and normal priority until it is gone. In efficiency mode, over a running
    /// game, its frame clock and window moves would stutter.
    /// </summary>
    public static void KeepAwake(bool on) => Post(() =>
    {
        _awake = Math.Max(0, _awake + (on ? 1 : -1));
        UpdateEfficiency();
    });

    /// <summary>
    /// Windows' efficiency mode (EcoQoS and a lower priority, as Task Manager's leaf shows) while nobody works with the window or a
    /// game runs; off while anything downloads, so a download keeps its speed, and off at once when the window comes in front.
    /// </summary>
    static void UpdateEfficiency()
    {
        bool want = (_windowHidden || InBackground || (GameRunning && _background)) && Downloads.ActiveCount == 0 && _awake == 0;
        if (want == _efficient) return;
        _efficient = want;
        Efficiency.Set(want);
    }

    static void Post(Action a)
    {
        if (_ui is null || _ui.HasThreadAccess) a();
        else _ui.TryEnqueue(() => a());
    }

    static void Raise() => Post(() =>
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn($"Media policy: {ex.Message}"); }
    });
}
