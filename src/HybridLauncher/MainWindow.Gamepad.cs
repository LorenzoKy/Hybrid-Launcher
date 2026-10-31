using System.Runtime.InteropServices;
using HybridLauncher.Core;
using HybridLauncher.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Gaming.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace HybridLauncher;

/// <summary>What a controller press means in Hybrid Launcher, whatever the controller (Xbox layout names).</summary>
public enum PadKey { Up, Down, Left, Right, A, B, X, Y, LB, RB, LT, RT, Menu, View }

/// <summary>
/// The controller in Hybrid Launcher's window: the D-pad or the left stick moves the focus from control to
/// control (as the arrow keys and Tab do), A presses what has the focus, B goes back or closes a menu, LB / RB change tab,
/// LT / RT and the right stick scroll, Y plays the game of the open page, X opens the right-click menu of what has the focus,
/// Menu or View jumps to the game list. Read only while Hybrid Launcher is the window in front, so a
/// game running on top never steers it; the timer runs only while a controller is connected.
/// </summary>
public sealed partial class MainWindow
{
    DispatcherQueueTimer? _padTimer;
    readonly HashSet<PadKey> _padHeld = [];
    PadKey? _padDir;
    DateTime _padRepeat;

    public static bool PadConnected { get; private set; }

    void StartGamepad()
    {
        _padTimer = DispatcherQueue.CreateTimer();
        _padTimer.Interval = TimeSpan.FromMilliseconds(16);
        _padTimer.Tick += (_, _) => PollPad();
        void Changed()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PadConnected = Gamepad.Gamepads.Count > 0;
                if (PadConnected) _padTimer.Start();
                else { _padTimer.Stop(); _padHeld.Clear(); _padDir = null; }
            });
        }
        Gamepad.GamepadAdded += (_, _) => Changed();
        Gamepad.GamepadRemoved += (_, _) => Changed();
        Changed();
        Closed += (_, _) => _padTimer.Stop();
    }

    void PollPad()
    {
        var pads = Gamepad.Gamepads;
        if (pads.Count == 0 || _closed || _hidden || !IsForeground()) { _padHeld.Clear(); _padDir = null; return; }
        GamepadReading r;
        try { r = pads[0].GetCurrentReading(); }
        catch { return; }
        var b = r.Buttons;

        var now = new HashSet<PadKey>();
        void On(bool down, PadKey k) { if (down) now.Add(k); }
        On((b & GamepadButtons.DPadUp) != 0 || r.LeftThumbstickY > 0.55, PadKey.Up);
        On((b & GamepadButtons.DPadDown) != 0 || r.LeftThumbstickY < -0.55, PadKey.Down);
        On((b & GamepadButtons.DPadLeft) != 0 || r.LeftThumbstickX < -0.55, PadKey.Left);
        On((b & GamepadButtons.DPadRight) != 0 || r.LeftThumbstickX > 0.55, PadKey.Right);
        On((b & GamepadButtons.A) != 0, PadKey.A);
        On((b & GamepadButtons.B) != 0, PadKey.B);
        On((b & GamepadButtons.X) != 0, PadKey.X);
        On((b & GamepadButtons.Y) != 0, PadKey.Y);
        On((b & GamepadButtons.LeftShoulder) != 0, PadKey.LB);
        On((b & GamepadButtons.RightShoulder) != 0, PadKey.RB);
        On(r.LeftTrigger > 0.6, PadKey.LT);
        On(r.RightTrigger > 0.6, PadKey.RT);
        On((b & GamepadButtons.Menu) != 0, PadKey.Menu);
        On((b & GamepadButtons.View) != 0, PadKey.View);

        // Buttons act once per press.
        foreach (var k in now)
            if (k > PadKey.Right && !_padHeld.Contains(k)) Pad(k);

        // A direction acts on press, then repeats after a short pause, faster when the stick is pushed all the way (as on consoles).
        PadKey? dir = now.Contains(PadKey.Up) ? PadKey.Up : now.Contains(PadKey.Down) ? PadKey.Down : now.Contains(PadKey.Left) ? PadKey.Left : now.Contains(PadKey.Right) ? PadKey.Right : null;
        var t = DateTime.UtcNow;
        if (dir is null) _padDir = null;
        else if (dir != _padDir) { _padDir = dir; _padRepeat = t.AddMilliseconds(380); Pad(dir.Value); }
        else if (t >= _padRepeat)
        {
            _padRepeat = t.AddMilliseconds(Math.Max(Math.Abs(r.LeftThumbstickX), Math.Abs(r.LeftThumbstickY)) > 0.95 ? 70 : 110);
            Pad(dir.Value);
        }
        _padHeld.Clear();
        _padHeld.UnionWith(now);

        // The right stick scrolls what is under the focus, smoothly, a little each frame.
        if (Math.Abs(r.RightThumbstickY) > 0.2 || Math.Abs(r.RightThumbstickX) > 0.2)
            Scroll(-r.RightThumbstickY * 26, r.RightThumbstickX * 26, animate: false);
    }

    /// <summary>One controller press (also the self-test's way in).</summary>
    public void Pad(PadKey key)
    {
        try
        {
            switch (key)
            {
                case PadKey.Up: Move(FocusNavigationDirection.Up, VirtualKey.Up); break;
                case PadKey.Down: Move(FocusNavigationDirection.Down, VirtualKey.Down); break;
                case PadKey.Left: Move(FocusNavigationDirection.Left, VirtualKey.Left); break;
                case PadKey.Right: Move(FocusNavigationDirection.Right, VirtualKey.Right); break;
                case PadKey.A: Press(); break;
                case PadKey.B: Back(); break;
                case PadKey.X: ContextMenu(); break;
                case PadKey.Y: (ContentFrame.Content as GamePage)?.PlayFromPad(); break;
                case PadKey.LB or PadKey.RB:
                    int by = key == PadKey.LB ? -1 : 1;
                    if (ContentFrame.Content is GamePage g) g.StepTab(by);
                    else if (ContentFrame.Content is SettingsPage s) s.StepTab(by);
                    break;
                case PadKey.LT: Scroll(-0.85, 0, animate: true, pages: true); break;
                case PadKey.RT: Scroll(0.85, 0, animate: true, pages: true); break;
                case PadKey.Menu or PadKey.View:
                    if ((Nav.SelectedItem as Control ?? Nav.MenuItems.OfType<Control>().FirstOrDefault()) is { } item) item.Focus(FocusState.Keyboard);
                    break;
            }
        }
        catch (Exception ex) { Log.Warn($"Controller {key}: {ex.Message}"); }
    }

    int OpenPopups() => RootGrid.XamlRoot is { } root ? VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count(p => p.Child is not null && p.IsOpen && p.Child is not ToolTip) : 0;

    static object? Focused(XamlRoot root) => FocusManager.GetFocusedElement(root);

    /// <summary>
    /// Moves the focus the way the stick points. In an open menu, list or drop-down the arrow key itself goes to it (they move
    /// their own selection); a slider takes left / right as its value. Nothing focused yet: the page's first control.
    /// </summary>
    void Move(FocusNavigationDirection direction, VirtualKey arrow)
    {
        if (RootGrid.XamlRoot is not { } root) return;
        var focused = Focused(root);
        if (OpenPopups() > 0 || focused is Slider && arrow is VirtualKey.Left or VirtualKey.Right || focused is TextBox && arrow is VirtualKey.Left or VirtualKey.Right)
        {
            SendKey(arrow);
            return;
        }
        if (focused is null or MainWindow || focused is FrameworkElement { Visibility: Visibility.Collapsed } || ReferenceEquals(focused, RootGrid))
        {
            FocusFirst();
            return;
        }
        var options = new FindNextElementOptions { SearchRoot = RootGrid };
        if (!FocusManager.TryMoveFocus(direction, options)) SendKey(arrow);
    }

    /// <summary>The first control of what is on screen: the open page's, else the game list.</summary>
    public void FocusFirst()
    {
        if ((ContentFrame.Content is DependencyObject page ? FocusManager.FindFirstFocusableElement(page) : null) is Control c) c.Focus(FocusState.Keyboard);
        else (Nav.SelectedItem as Control)?.Focus(FocusState.Keyboard);
    }

    /// <summary>A presses what has the focus: Space for what toggles, Enter for the rest (buttons, list items, menus, drop-downs).</summary>
    void Press()
    {
        if (RootGrid.XamlRoot is not { } root) return;
        var focused = Focused(root);
        if (focused is null) { FocusFirst(); return; }
        SendKey(focused is ToggleSwitch or CheckBox or RadioButton or ToggleButton and not AppBarToggleButton ? VirtualKey.Space : VirtualKey.Enter);
    }

    /// <summary>B: closes an open menu or dialog first; else back a page.</summary>
    void Back()
    {
        if (OpenPopups() > 0) { SendKey(VirtualKey.Escape); return; }
        GoBack();
    }

    /// <summary>X: the right-click menu of what has the focus (a library tile's, a list item's), when it has one.</summary>
    void ContextMenu()
    {
        if (RootGrid.XamlRoot is not { } root || Focused(root) is not FrameworkElement el) return;
        for (DependencyObject? d = el; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement { ContextFlyout: { } menu } owner)
            {
                menu.ShowAt(owner, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
                return;
            }
        SendKey(VirtualKey.Application);
    }

    /// <summary>Scrolls the list or page around the focus (or the open page's own): by pixels, or by a share of its height.</summary>
    void Scroll(double down, double right, bool animate, bool pages = false)
    {
        if (RootGrid.XamlRoot is not { } root) return;
        ScrollViewer? sv = null;
        for (DependencyObject? d = Focused(root) as DependencyObject; d is not null && sv is null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollViewer s && (s.ScrollableHeight > 0 || s.ScrollableWidth > 0)) sv = s;
        sv ??= FindScroller(ContentFrame.Content as DependencyObject);
        if (sv is null) return;
        double dy = pages ? down * sv.ViewportHeight : down, dx = pages ? right * sv.ViewportWidth : right;
        sv.ChangeView(sv.HorizontalOffset + dx, sv.VerticalOffset + dy, null, !animate);
    }

    static ScrollViewer? FindScroller(DependencyObject? root)
    {
        if (root is null) return null;
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is ScrollViewer { ScrollableHeight: > 0 } s) return s;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    bool IsForeground()
    {
        try { return GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this); }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    static extern nint GetForegroundWindow();

    // ---------- key presses on behalf of the controller ----------

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>Presses and releases a key, as the keyboard would: menus, lists and drop-downs answer it in their own way.</summary>
    static void SendKey(VirtualKey key)
    {
        const uint KeyUp = 2, Extended = 1;
        uint ext = key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right or VirtualKey.Application ? Extended : 0;
        var inputs = new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = (ushort)key, dwFlags = ext } },
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = (ushort)key, dwFlags = ext | KeyUp } },
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }
}
