using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// When Hybrid Launcher's videos (trailers, the looping clips of store descriptions) may play by themselves. A game launcher must not
/// take GPU time from the game it started, so nothing plays while a game runs or while the window is minimized or in the
/// notification area. A video the user paused keeps every video paused until they play one again (for the session, like
/// the trailer's sound). With hardware acceleration off nothing plays by itself and the GPU effects are left out.
/// </summary>
public static class MediaPolicy
{
    static DispatcherQueue? _ui;
    static bool _windowHidden;

    /// <summary>Raised on the UI thread whenever <see cref="MayAutoplay"/> or <see cref="Effects"/> may have changed.</summary>
    public static event Action? Changed;

    /// <summary>The user paused a video: the others wait until they play one again.</summary>
    public static bool UserPaused { get; private set; }

    /// <summary>A game started from Hybrid Launcher (or seen running) is on: no video plays.</summary>
    public static bool GameRunning { get; private set; }

    /// <summary>Hardware acceleration (Settings → Features, on by default): blur, reflections and videos that start by themselves.</summary>
    public static bool Effects => AppState.Settings.HardwareAcceleration;

    /// <summary>Whether a video may start or go on by itself now.</summary>
    public static bool MayAutoplay => Effects && !UserPaused && !GameRunning && !_windowHidden;

    /// <summary>For the log: what holds the videos back.</summary>
    public static string Why => $"effects {Effects}, user paused {UserPaused}, game running {GameRunning}, window hidden {_windowHidden}";

    /// <summary>Whether a video may play at all now (the user pressing Play still wins over the user pause, not over a game).</summary>
    public static bool Suspended => GameRunning || _windowHidden;

    /// <summary>Follows the main window (minimized, hidden in the notification area) and the running games.</summary>
    public static void Attach(Window window, bool hidden)
    {
        _ui = window.DispatcherQueue;
        _windowHidden = hidden;
        window.VisibilityChanged += (_, e) => SetHidden(!e.Visible || IsMinimized(window));
        window.AppWindow.Changed += (_, e) =>
        {
            if (e.DidPresenterChange || e.DidSizeChange || e.DidVisibilityChange) SetHidden(!window.AppWindow.IsVisible || IsMinimized(window));
        };
        GameSessions.Changed += _ => Post(() =>
        {
            bool running = GameSessions.Active().Count > 0;
            if (running == GameRunning) return;
            GameRunning = running;
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
    public static void NotifyEffectsChanged() => Raise();

    static bool IsMinimized(Window window) =>
        window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    static void SetHidden(bool hidden)
    {
        if (hidden == _windowHidden) return;
        _windowHidden = hidden;
        Raise();
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
