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
public enum PadKey { Up, Down, Left, Right, A, B, X, Y, LB, RB, LT, RT, Menu, View, L3, R3 }

/// <summary>
/// The controller in Hybrid Launcher's window: the D-pad or the left stick moves the focus from control to
/// control (as the arrow keys and Tab do) and the right stick scrolls; the buttons do what Settings → Controller gives them
/// (<see cref="PadActions"/>; by default A presses, B goes back, X opens the right-click menu, Y plays, LB / RB change tab, LT / RT
/// scroll a page, Menu jumps to the game list, View to Search). Read only while Hybrid Launcher is the window in front, so a
/// game running on top never steers it; it is read once per display refresh, only while a controller is connected.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The controller is read once per display refresh (Helpers.FrameSync), while this is set.</summary>
    IDisposable? _padClock;
    readonly HashSet<PadKey> _padHeld = [];
    readonly HashSet<PadKey> _padNow = [];
    PadKey? _padDir;
    DateTime _padRepeat;
    /// <summary>When a button that scrolls a page (LT / RT by default) went down: held, it keeps scrolling.</summary>
    DateTime _padScrollSince;
    PadKey? _padScrollKey;
    /// <summary>How long a page button is held before it scrolls on by itself (a tap stays one page).</summary>
    const double HoldScrollAfterMs = 150;
    /// <summary>The ms since the last reading: the right stick scrolls by time, the same speed at any refresh rate.</summary>
    double _padMs = 1000.0 / 60;

    public static bool PadConnected { get; private set; }

    /// <summary>Settings → Features → Controller navigation was switched: the controller is read again, or left alone.</summary>
    public void GamepadSettingChanged() => _padApply?.Invoke();

    Action? _padApply;

    /// <summary>The controller is being read (the self-test checks it stops when Controller navigation is off).</summary>
    bool PadReading => _padClock is not null;

    void StartGamepad()
    {
        // The controller is read at each refresh of the display (vsync), so right-stick scrolling moves once per frame shown, but
        // only while one is connected and the window is in front: behind another app (a game) it is never read anyway, so the
        // clock stops instead of waking the process for nothing.
        bool front = true;
        void Apply()
        {
            PadConnected = Gamepad.Gamepads.Count > 0;
            // Turned off in Settings: never read, so a tool that maps the controller to the mouse is the only one acting on it.
            if (PadConnected && front && !_hidden && AppState.Settings.GamepadInput)
                _padClock ??= Helpers.FrameSync.Subscribe(DispatcherQueue, ms => { _padMs = Math.Clamp(ms, 1, 50); PollPad(); });
            else { StopPad(); _padHeld.Clear(); _padDir = null; }
        }
        void Changed() => DispatcherQueue.TryEnqueue(Apply);
        _padApply = Apply;
        Gamepad.GamepadAdded += (_, _) => Changed();
        Gamepad.GamepadRemoved += (_, _) => Changed();
        Activated += (_, e) =>
        {
            front = e.WindowActivationState != WindowActivationState.Deactivated;
            Apply();
        };
        Changed();
        Closed += (_, _) => StopPad();
    }

    void StopPad()
    {
        _padClock?.Dispose();
        _padClock = null;
    }

    void PollPad()
    {
        var pads = Gamepad.Gamepads;
        if (pads.Count == 0 || _closed || _hidden || !AppState.Settings.GamepadInput || !IsForeground()) { _padHeld.Clear(); _padDir = null; return; }
        GamepadReading r;
        try { r = pads[0].GetCurrentReading(); }
        catch { return; }
        var b = r.Buttons;

        // One set for every reading (read once per display refresh: a new one each time was garbage at 180 a second).
        var now = _padNow;
        now.Clear();
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
        On((b & GamepadButtons.LeftThumbstick) != 0, PadKey.L3);
        On((b & GamepadButtons.RightThumbstick) != 0, PadKey.R3);

        // Buttons act once per press. On a copy: a press can run a poll again inside Pad (2.3-rc.6, seen as "Collection was
        // modified" when A was pressed on the pop-up settings), which refills the shared list mid-loop.
        foreach (var k in now.ToArray())
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

        // 2.3-rc.2: a page button (LT / RT by default) held down keeps scrolling, once per frame, until it is let go: a tap is
        // one page, holding it glides on, faster the longer it is held (and, for a trigger, the deeper it is pressed).
        PadKey? scrollKey = null;
        int scrollSign = 0;
        double trigRate = 0;
        foreach (var k in now)
        {
            var action = PadActions.ActionOf(k);
            if (action is PadAction.PageUp or PadAction.PageDown) { scrollKey = k; scrollSign = action == PadAction.PageDown ? 1 : -1; break; }
        }
        if (scrollKey != _padScrollKey) { _padScrollKey = scrollKey; _padScrollSince = t; }
        else if (scrollKey is { } held && (t - _padScrollSince).TotalMilliseconds >= HoldScrollAfterMs)
        {
            double heldMs = (t - _padScrollSince).TotalMilliseconds - HoldScrollAfterMs;
            double depth = held switch
            {
                PadKey.LT => Math.Clamp((r.LeftTrigger - 0.6) / 0.4, 0, 1),
                PadKey.RT => Math.Clamp((r.RightTrigger - 0.6) / 0.4, 0, 1),
                _ => 1,
            };
            // From 1400 px/s up to 3600 px/s after a second and a half (eased in, no step); a trigger only just past its
            // click point goes at 60%.
            double ramp = Math.Clamp(heldMs / 1500, 0, 1);
            double speed = (1400 + 2200 * ramp * ramp * (3 - 2 * ramp)) * (0.75 + 0.25 * depth);
            trigRate = scrollSign * speed;
        }

        // A hand on the controller: the frame clock stays at the display's rate, as for the mouse (Helpers.FrameWarmth).
        bool stick = Math.Abs(r.RightThumbstickY) > StickDead || Math.Abs(r.RightThumbstickX) > StickDead;
        if (now.Count > 0 || stick) Helpers.FrameWarmth.Poke();
        // The right stick scrolls what is under the focus, a little each frame, through the same glide as the mouse wheel. The
        // speed grows smoothly from the dead zone (no jump when the stick leaves it) up to 2400 px/s at full tilt.
        // 2.3-rc.4: what is held is a speed (px/s) for the page's glide, not a few pixels pushed each poll: the page moves at a
        // steady pace on the compositor and eases out when let go (Glide.Drive).
        double rate = trigRate + (stick ? -StickSpeed(r.RightThumbstickY) : 0);
        if (rate != 0 || _padDriving) DriveVertical(rate);
        if (stick && Math.Abs(r.RightThumbstickX) > StickDead) Scroll(0, StickSpeed(r.RightThumbstickX) * _padMs / 1000);
    }

    bool _padDriving;
    WeakReference<DependencyObject>? _padDriven;

    /// <summary>Held vertical scroll at <paramref name="rate"/> px/s (0 lets go, easing out).</summary>
    void DriveVertical(double rate)
    {
        DependencyObject? target = null;
        if (rate == 0) { _padDriven?.TryGetTarget(out target); _padDriving = false; _padDriven = null; }
        else
        {
            if (RootGrid.XamlRoot is not { } root) return;
            target = PadTarget(root, vertical: true);
            // The scroller changed under a held scroll (focus moved): the old one is let go.
            if (_padDriven is not null && _padDriven.TryGetTarget(out var old) && !ReferenceEquals(old, target)) DriveOn(old, 0);
            _padDriving = target is not null;
            _padDriven = target is null ? null : new WeakReference<DependencyObject>(target);
        }
        if (target is not null) DriveOn(target, rate);
    }

    static void DriveOn(DependencyObject target, double rate)
    {
        switch (target)
        {
            case ScrollViewer sv: Helpers.SmoothWheel.Drive(sv, rate); break;
            case ScrollView view: Helpers.CompositorWheel.Drive(view, rate); break;
        }
    }

    const double StickDead = 0.18;

    /// <summary>Pixels per second for a stick axis: 0 at the edge of the dead zone, 2400 at full tilt, finer in between.</summary>
    static double StickSpeed(double axis)
    {
        double a = (Math.Abs(axis) - StickDead) / (1 - StickDead);
        if (a <= 0) return 0;
        return Math.Sign(axis) * 2400 * Math.Pow(Math.Min(a, 1), 1.7);
    }

    /// <summary>One controller press (also the self-test's way in): a direction moves, a button does its action.</summary>
    public void Pad(PadKey key)
    {
        NotePadPress();
        Helpers.UiPerf.Controller(key.ToString());
        try
        {
            switch (key)
            {
                case PadKey.Up: Move(FocusNavigationDirection.Up, VirtualKey.Up); break;
                case PadKey.Down: Move(FocusNavigationDirection.Down, VirtualKey.Down); break;
                case PadKey.Left: Move(FocusNavigationDirection.Left, VirtualKey.Left); break;
                case PadKey.Right: Move(FocusNavigationDirection.Right, VirtualKey.Right); break;
                default: Do(PadActions.ActionOf(key)); break;
            }
        }
        catch (Exception ex) { Log.Warn($"Controller {key}: {ex.Message}"); }
    }

    /// <summary>What a button does (Settings → Controller).</summary>
    public void Do(PadAction action)
    {
        switch (action)
        {
            case PadAction.Press: Press(); break;
            case PadAction.Back: Back(); break;
            case PadAction.Menu: ContextMenu(); break;
            case PadAction.Play: (Shown as GamePage)?.PlayFromPad(); break;
            case PadAction.PrevTab or PadAction.NextTab:
                int by = action == PadAction.PrevTab ? -1 : 1;
                if (Shown is GamePage g) g.StepTab(by);
                else if (Shown is SettingsPage s) s.StepTab(by);
                else StepSection(by);
                break;
            case PadAction.PageUp: Scroll(-0.85, 0, pages: true); break;
            case PadAction.PageDown: Scroll(0.85, 0, pages: true); break;
            case PadAction.GameList:
                if ((Nav.SelectedItem as Control ?? Nav.MenuItems.OfType<Control>().FirstOrDefault()) is { } item) item.Focus(FocusState.Keyboard);
                break;
            case PadAction.Search: SearchBox.Focus(FocusState.Keyboard); break;
            case PadAction.PrevSection: StepSection(-1); break;
            case PadAction.NextSection: StepSection(1); break;
        }
    }

    /// <summary>My PC, Library, Downloads, Settings: the one before or after the open page (a game page counts as the Library).</summary>
    public void StepSection(int by)
    {
        var items = new List<object>();
        foreach (var tag in new[] { HardwareTag, LibraryTag, DownloadsTag })
            if (Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag && i.Visibility == Visibility.Visible) is { } it)
                items.Add(it);
        if (Nav.SettingsItem is NavigationViewItem settings) items.Add(settings);
        if (items.Count == 0) return;
        int at = Shown switch
        {
            HardwarePage => items.FindIndex(i => (i as NavigationViewItem)?.Tag as string == HardwareTag),
            DownloadsPage => items.FindIndex(i => (i as NavigationViewItem)?.Tag as string == DownloadsTag),
            SettingsPage => items.IndexOf(Nav.SettingsItem),
            _ => items.FindIndex(i => (i as NavigationViewItem)?.Tag as string == LibraryTag),
        };
        var next = items[((Math.Max(0, at) + by) % items.Count + items.Count) % items.Count];
        Nav.SelectedItem = next;
        (next as Control)?.Focus(FocusState.Keyboard);
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
        if (focused is null or MainWindow || focused is FrameworkElement { Visibility: Visibility.Collapsed } || ReferenceEquals(focused, RootGrid) || IsPageHolder(focused))
        {
            FocusFirst();
            return;
        }
        // Downloads' covers move as a grid (2.3-rc.6).
        if (focused is CoverCell cover && Shown is DownloadsPage downloads && downloads.PadMove(cover, direction)) return;
        var options = new FindNextElementOptions { SearchRoot = RootGrid };
        if (!FocusManager.TryMoveFocus(direction, options)) SendKey(arrow);
        // 2.3-rc.6: the frame that holds the pages takes the focus (it keeps it off the search box) and was the nearest control
        // right of the pane: the stick stopped on it and never got into Downloads. It hands the focus on to the page's first control.
        else if (IsPageHolder(Focused(root))) FocusFirst();
    }

    /// <summary>The frame that holds the pages, or the page itself: focusable, but nothing to press, and arrows from it go nowhere.</summary>
    bool IsPageHolder(object? focused) => ReferenceEquals(focused, ContentFrame) || focused is Page && ReferenceEquals(focused, Shown);

    /// <summary>The first control of what is on screen: the open page's, else the game list.</summary>
    public void FocusFirst()
    {
        if ((Shown is DependencyObject page ? FocusManager.FindFirstFocusableElement(page) : null) is Control c) c.Focus(FocusState.Keyboard);
        else (Nav.SelectedItem as Control)?.Focus(FocusState.Keyboard);
    }

    /// <summary>A presses what has the focus: Space for what toggles, Enter for the rest (buttons, list items, menus, drop-downs).</summary>
    void Press()
    {
        if (RootGrid.XamlRoot is not { } root) return;
        var focused = Focused(root);
        if (focused is null) { FocusFirst(); return; }
        // 2.3-rc.4: the three sections of the pane open by selecting them, not by an Enter sent to whatever window is in front
        // (when that key went elsewhere the controller's A did nothing on Downloads).
        if (focused is NavigationViewItem { Tag: string sec } nav && (sec == HardwareTag || sec == LibraryTag || sec == DownloadsTag))
        {
            Nav.SelectedItem = nav;
            return;
        }
        // 2.3-rc.6: a Downloads cover opens its game directly, for the same reason.
        if (focused is CoverCell cover && cover.Open()) return;
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

    /// <summary>
    /// Scrolls the list or page around the focus (or the open page's own): by pixels, or by a share of its height. Pages scroll
    /// in a ScrollView since 2.0 (CompositorWheel), lists in a ScrollViewer: both are looked for (2.3: LT / RT and the right
    /// stick looked for ScrollViewers only and did nothing on the pages). A scroller that cannot move the asked way (a sideways
    /// strip of covers for an up / down scroll) is passed over for the one around it.
    /// </summary>
    // The scroller the controller moved last, with the focus it was found from: looked for again only when the focus changes
    // or now and then (walking the page's tree every frame of a held stick cost frames of its own).
    WeakReference<DependencyObject>? _padScroller;
    object? _padScrollerFocus;
    bool _padScrollerVertical;
    long _padScrollerAt;

    /// <summary>
    /// Scrolls what is under the focus (else the page) by down / right px, or by that share of a page with pages. Vertical
    /// moves go through the mouse wheel's glide (2.3-rc.3), so the controller scrolls as smoothly as the wheel.
    /// </summary>
    void Scroll(double down, double right, bool pages = false)
    {
        if (RootGrid.XamlRoot is not { } root) return;
        bool vertical = Math.Abs(down) >= Math.Abs(right);
        var target = PadTarget(root, vertical);
        switch (target)
        {
            case ScrollViewer sv:
            {
                double dy = pages ? down * sv.ViewportHeight : down, dx = pages ? right * sv.ViewportWidth : right;
                if (vertical) Helpers.SmoothWheel.Push(sv, dy);
                else sv.ChangeView(sv.HorizontalOffset + dx, null, null, !pages);
                break;
            }
            case ScrollView view:
            {
                double dy = pages ? down * view.ViewportHeight : down, dx = pages ? right * view.ViewportWidth : right;
                if (vertical) Helpers.CompositorWheel.Push(view, dy);
                else view.ScrollTo(Math.Clamp(view.HorizontalOffset + dx, 0, view.ScrollableWidth), view.VerticalOffset,
                    new ScrollingScrollOptions(pages ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                break;
            }
        }
    }

    /// <summary>The scroller the controller moves now: around the focus, else the open page's (remembered a moment).</summary>
    DependencyObject? PadTarget(XamlRoot root, bool vertical)
    {
        bool Fits(DependencyObject d) => d switch
        {
            ScrollViewer s => vertical ? s.ScrollableHeight > 0 && s.VerticalScrollMode != ScrollMode.Disabled : s.ScrollableWidth > 0,
            ScrollView v => vertical ? v.ScrollableHeight > 0 : v.ScrollableWidth > 0,
            _ => false,
        };
        var focused = Focused(root);
        long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
        DependencyObject? target = null;
        if (_padScroller is not null && _padScroller.TryGetTarget(out var known) && ReferenceEquals(focused, _padScrollerFocus)
            && _padScrollerVertical == vertical && System.Diagnostics.Stopwatch.GetElapsedTime(_padScrollerAt, nowTs).TotalMilliseconds < 400
            && known is FrameworkElement { IsLoaded: true } && Fits(known))
            target = known;
        if (target is null)
        {
            for (DependencyObject? d = focused as DependencyObject; d is not null && target is null; d = VisualTreeHelper.GetParent(d))
                if (Fits(d)) target = d;
            target ??= FindScroller(Shown as DependencyObject, Fits);
            _padScroller = target is null ? null : new WeakReference<DependencyObject>(target);
            _padScrollerFocus = focused;
            _padScrollerVertical = vertical;
            _padScrollerAt = nowTs;
        }
        return target;
    }

    static DependencyObject? FindScroller(DependencyObject? root, Func<DependencyObject, bool> fits)
    {
        if (root is null) return null;
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (fits(d)) return d;
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
