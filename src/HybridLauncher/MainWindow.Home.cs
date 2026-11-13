using System.Runtime.InteropServices;
using HybridLauncher.Core;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher;

/// <summary>
/// The controller's Home button as a console's: from a game or any app it brings Hybrid Launcher to the front (out of the
/// notification area too), on the page of the game being played; pressed again, back to where it was.
/// </summary>
public sealed partial class MainWindow
{
    HomeButton? _home;

    /// <summary>The window the Home button came from, to go back to.</summary>
    nint _homeBack;

    /// <summary>Settings → Controller → Home button changed: listening starts, stops or starts again with the new options.</summary>
    public void HomeButtonSettingChanged()
    {
        _home?.Dispose();
        _home = null;
        var c = AppState.Settings.Controller;
        if (c.HomeButton && !_closed) _home = new HomeButton(DispatcherQueue, HomePressed, c.HomePlayStation);
    }

    void StartHomeButton()
    {
        HomeButtonSettingChanged();
        Closed += (_, _) => { _home?.Dispose(); _home = null; };
    }

    /// <summary>The Home button (also the self-test's way in).</summary>
    public void HomePressed()
    {
        // On purpose: the focus guard lets Hybrid Launcher come forward.
        Helpers.FocusGuard.Allow();
        try
        {
            var c = AppState.Settings.Controller;
            nint me = WinRT.Interop.WindowNative.GetWindowHandle(this);
            nint front = GetForegroundWindow();
            // Pressed on an achievement's pop-up (or just after it), as a console opens the trophy just earned: that achievement
            // in its game's Achievements tab, from a game or from Hybrid Launcher itself.
            if (AchievementPopup.TakeRecent() is { GameId: { } gameId, AchievementKey: { } key } && AppState.FindAny(gameId) is not null)
            {
                if (front != me) _homeBack = front;
                ShowFromTray();
                ToFront(me);
                Log.Info($"Home button: achievement {key} of {gameId}");
                OpenGame(gameId);
                (Shown as Views.GamePage)?.ShowAchievement(key);
                NotePadPress();
                return;
            }
            if (front == me && !_hidden)
            {
                if (c.HomeGoesBack && _homeBack != 0 && IsWindow(_homeBack))
                {
                    Log.Info("Home button: back to the window it came from");
                    ToFront(_homeBack);
                }
                return;
            }
            _homeBack = front != me ? front : 0;
            ShowFromTray();
            ToFront(me);
            Log.Info("Home button: Hybrid Launcher brought to the front");
            switch (c.HomeOpens)
            {
                case "Game" when GameSessions.Active().FirstOrDefault(id => AppState.Find(id) is not null) is { } running:
                    OpenGame(running);
                    break;
                case "Library":
                    if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == LibraryTag) is { } lib) Nav.SelectedItem = lib;
                    break;
            }
            NotePadPress();
            FocusFirst();
        }
        catch (Exception ex) { Log.Warn($"Home button: {ex.Message}"); }
    }

    /// <summary>
    /// Puts a window in front even though the press did not reach this process (Windows only lets the process that got the last
    /// input take the foreground): for a moment the input of the window in front is shared with this one.
    /// </summary>
    static void ToFront(nint hwnd)
    {
        nint front = GetForegroundWindow();
        uint mine = GetCurrentThreadId(), theirs = front != 0 ? GetWindowThreadProcessId(front, 0) : 0;
        bool attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(mine, theirs, false);
        }
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, nint processId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
