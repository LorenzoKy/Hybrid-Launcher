using System.Diagnostics;
using System.Runtime.InteropServices;
using HybridLauncher.Core;
using HybridLauncher.Helpers;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Storage.Streams;
using Color = Windows.UI.Color;

namespace HybridLauncher;

/// <summary>One unlock to announce. <see cref="Store"/> picks the look when the look is "Match the game's store".</summary>
/// <param name="Completion">The game's platinum: its own achievement for unlocking all the others, shown in the platinum tier.</param>
/// <param name="GameId">The library game it belongs to, and <paramref name="AchievementKey"/> the achievement: the controller's
/// Home button pressed while it shows (or just after) opens that achievement in the game's Achievements tab.</param>
public sealed record Toast(string Name, string Description, string? IconUrl, string? Game, double? Percent = null, GameStore? Store = null, int? Points = null, string? Look = null, bool Completion = false, string? GameId = null, string? AchievementKey = null);

/// <summary>
/// The unlock toast: a small borderless window in a corner of the screen the game runs on, always on top, shown without taking
/// focus (so a game keeps it). One at a time; the rest wait in a queue.
///
/// How it avoids the dark trail the first version left: the window itself is fully transparent (per-pixel alpha), so nothing but
/// the toast is ever drawn, and one animator moves the WINDOW and fades its content in the same frame, driven by the display's
/// own frame clock (CompositionTarget.Rendering) with time-based easing, so it is smooth at any refresh rate. The content never
/// slides inside a fixed window any more (that exposed the window's backdrop as a dark smear).
///
/// Looks (Settings → Achievements → Look): Hybrid, PlayStation, PlayStation 4 (white or black), Xbox, Steam, GOG, Epic, Minimal, or the game's own store.
/// Corner (or the original's own, "Native"), size, colours (Hybrid and Minimal), opacity, duration, sound and rarity line come from Settings → Achievements.
/// </summary>
public static class AchievementPopup
{
    static readonly Queue<Toast> Waiting = new();
    static Window? _current;
    static Run? _run;

    public static void Show(string name, string description, string? iconUrl, string? game, double? percent = null, GameStore? store = null) =>
        Show(new Toast(name, description, iconUrl, game, percent, store));

    public static void Show(Toast toast)
    {
        int generation = _generation;
        _preparing = PrepareThenShow(_preparing, toast, generation);
    }

    /// <summary>Bumped when what is waiting is thrown away (a sample replacing it, all closed): pop-ups still being prepared are dropped.</summary>
    static int _generation;
    static Task _preparing = Task.CompletedTask;

    /// <summary>
    /// The picture is fetched (or taken from <see cref="Prefetch"/>) and decoded before the pop-up opens, in the order the unlocks
    /// came, so it is there from the first frame. It never waits long: without the picture in a moment it shows without it.
    /// </summary>
    static async Task PrepareThenShow(Task previous, Toast toast, int generation)
    {
        try { await previous; } catch { }
        if (generation != _generation) return;
        try
        {
            if (toast.IconUrl is { } url && !ReadyIcons.ContainsKey(url) && await DecodeAsync(url) is { } picture)
            {
                if (ReadyIcons.Count > 24) ReadyIcons.Clear();
                ReadyIcons[url] = picture;
            }
        }
        catch (Exception ex) { Log.Warn($"Pop-up picture: {ex.Message}"); }
        if (generation != _generation) return;
        Waiting.Enqueue(toast);
        if (_current is null) ShowNext();
    }

    /// <summary>Decoded pictures ready for the pop-ups, by URL.</summary>
    static readonly Dictionary<string, BitmapImage> ReadyIcons = [];
    /// <summary>Picture files fetched this session, by URL (<see cref="Prefetch"/>).</summary>
    static readonly Dictionary<string, byte[]> IconBytes = [];
    static readonly SemaphoreSlim Fetching = new(4);

    /// <summary>Fetches pictures ahead (a game's locked achievements when it starts), so its unlocks show their picture at once.</summary>
    public static void Prefetch(IEnumerable<string?> urls)
    {
        var list = urls.OfType<string>().Distinct().Take(400).ToList();
        if (list.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var url in list)
            {
                await Fetching.WaitAsync();
                _ = Task.Run(async () =>
                {
                    try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await FetchAsync(url, cts.Token); }
                    catch { }
                    finally { Fetching.Release(); }
                });
            }
        });
    }

    static async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        lock (IconBytes) if (IconBytes.TryGetValue(url, out var known)) return known;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        byte[]? data = uri.IsFile ? await File.ReadAllBytesAsync(uri.LocalPath, ct)
            : uri.Scheme is "http" or "https" ? await HybridLauncher.Core.Net.Http.Client.GetByteArrayAsync(uri, ct)
            : null;
        if (data is not { Length: > 0 }) return null;
        lock (IconBytes)
        {
            if (IconBytes.Count > 600) IconBytes.Clear();
            IconBytes[url] = data;
        }
        return data;
    }

    /// <summary>The picture decoded and ready to draw, or null when it is not there within a moment (the pop-up then shows without it).</summary>
    static async Task<BitmapImage?> DecodeAsync(string url)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        byte[]? data;
        try { data = await FetchAsync(url, cts.Token); }
        catch (Exception ex) { Log.Warn($"Pop-up picture {url}: {ex.Message}"); return null; }
        if (data is null) return null;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        var picture = new BitmapImage();
        await picture.SetSourceAsync(stream);
        return picture;
    }

    /// <summary>The ready picture for <paramref name="url"/>, or one that loads from it.</summary>
    static ImageSource? Picture(string? url, double side)
    {
        if (url is null) return null;
        if (ReadyIcons.TryGetValue(url, out var ready)) return ready;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new BitmapImage { UriSource = uri, DecodePixelWidth = (int)Math.Ceiling(side * 2) } : null;
    }

    static string SampleIcon => new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AchievementSample.png")).AbsoluteUri;

    static Toast Sample => new("Perfect Blend", L.T("Saw what an unlock looks like. This one is only a test."), SampleIcon, "Game: The game", 12.4, null, 50);

    /// <summary>
    /// A sample with the current settings; replaces one that is showing, so changes can be compared quickly. The one replaced
    /// leaves with a quick fade of its own while the new one comes in, instead of vanishing from one frame to the next.
    /// </summary>
    public static void ShowTest(string? look = null) => ShowReplacing(Sample with { Look = look });

    /// <summary>The card looks' first line: an unlock, or the game's platinum.</summary>
    static string Headline(Toast t) => L.T(t.Completion ? "PLATINUM UNLOCKED" : "ACHIEVEMENT UNLOCKED");

    /// <summary>
    /// A sample platinum (Settings with no game picked, self-test): a game's achievement for unlocking all the others, with the
    /// app's platinum emblem for its picture since there is no game to take one from.
    /// </summary>
    public static void ShowTestPlatinum(string? look = null) =>
        ShowReplacing(new Toast(L.T("Tireless"), L.T("Unlock every other achievement. This one is only a test."), null, "Game: The game", 4.1, null, 180, look, Completion: true));

    /// <summary>Shows <paramref name="toast"/> now, in place of what is on screen or waiting (samples and previews).</summary>
    public static void ShowReplacing(Toast toast)
    {
        Waiting.Clear();
        _generation++;
        _preparing = Task.CompletedTask;
        if (_current is { } shown)
        {
            _current = null;
            if (_run is { } r && r.Window == shown && r.Leave(160)) { Leaving.Add(r); _run = null; }
            else Finish(shown, next: false);
        }
        Show(toast);
    }

    /// <summary>Toasts that were replaced and are fading out on their own.</summary>
    static readonly List<Run> Leaving = [];

    public static void CloseAll()
    {
        Waiting.Clear();
        _generation++;
        _preparing = Task.CompletedTask;
        if (_current is { } shown) Finish(shown, next: false);
        foreach (var r in Leaving.ToArray()) Finish(r.Window, next: false);
        Leaving.Clear();
        if (_placing is { } placing) { try { placing.Close(); } catch { } _placing = null; }
        _current = null;
    }

    // ---------- placing it by hand ----------

    static Window? _placing;
    static Action<double, double>? _placed;

    /// <summary>The window of the pop-up on screen, or null (self-test pictures and checks).</summary>
    internal static nint? CurrentHandle => Handle(_current);
    internal static nint? PlacingHandle => Handle(_placing);
    /// <summary>Self-test: the pop-up on screen sits in <paramref name="look"/>'s corner (with "Native", where the original shows it).</summary>
    internal static void CheckNativeSpot(string look)
    {
        if (CurrentHandle is not { } hwnd || !GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("no pop-up on screen");
        var o = AppState.Settings.Popup;
        string corner = CornerOf(o, look);
        var a = TargetDisplay(o).WorkArea;
        double cx = (r.Left + r.Right) / 2.0 - a.X, cy = (r.Top + r.Bottom) / 2.0 - a.Y;
        string h = cx < a.Width * 0.3 ? "Left" : cx > a.Width * 0.7 ? "Right" : "Center";
        string v = cy < a.Height / 2.0 ? "Top" : "Bottom";
        if (v + h != corner) throw new InvalidOperationException($"{look} is at {v}{h}, expected {corner}");
    }

    /// <summary>Set when a pop-up window was found uncovered before its first frames were drawn (self-test).</summary>
    internal static bool UncoveredEarly;

    /// <summary>Self-test: the pop-up on screen is see-through (its transparent backdrop connected), so no dark box can show.</summary>
    internal static void CheckSeeThrough()
    {
        if (CurrentHandle is null) throw new InvalidOperationException("no pop-up on screen");
        if (!TransparentBackdrop.LastConnected) throw new InvalidOperationException("the pop-up window has no transparent backdrop: it shows a dark box");
        // Windows' own show / hide animation is off (it left a ghost of the toast on screen after it closed).
        if (DwmGetWindowAttribute(CurrentHandle.Value, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, out int noFx, sizeof(int)) == 0 && noFx == 0)
            throw new InvalidOperationException("the pop-up window still has Windows' show / hide animation");
        if (UncoveredEarly) throw new InvalidOperationException("a pop-up window was on screen before its first frames were drawn");
        // It opens cloaked and is uncovered once drawn: still cloaked here means it never showed.
        if (DwmGetWindowAttribute(CurrentHandle.Value, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            throw new InvalidOperationException($"the pop-up window is still cloaked ({cloaked})");
    }

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(nint hwnd);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(nint hwnd, out Rect rect);

    static nint? Handle(Window? w) { try { return w is null || Closed.Contains(w) ? null : WinRT.Interop.WindowNative.GetWindowHandle(w); } catch { return null; } }

    /// <summary>
    /// A sample to drag to the exact spot (Settings → Achievements → Position → Exactly where I put it); every time it is let go
    /// the spot is saved and <paramref name="placed"/> hears it (0..1 across and down the screen). Its Done button closes it.
    /// </summary>
    public static void Place(Action<double, double> placed)
    {
        _placed = placed;
        if (_placing is not null) { MovePlacement(); return; }
        try { _placing = Open(Sample with { Description = L.T("Drag me to where unlocks should appear, then press Done.") , Percent = null }, placing: true); }
        catch (Exception ex) { Log.Warn($"Pop-up placing failed: {ex.Message}"); _placing = null; }
    }

    /// <summary>The sliders moved: the sample follows them.</summary>
    public static void MovePlacement()
    {
        if (_placing is not { } w) return;
        var app = w.AppWindow;
        var o = AppState.Settings.Popup;
        var look = ResolveLook(o, null, null);
        var display = TargetDisplay(AppState.Settings.Popup);
        double scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(w)) / 96.0;
        var (x, y) = Spot(o, CornerOf(o, look), display.WorkArea, app.Size.Width, app.Size.Height, Margins(look, scale <= 0 ? 1 : scale));
        app.Move(new PointInt32(x, y));
    }

    /// <summary>Where the pop-up goes on <paramref name="area"/>: a corner with the look's margins, or the exact spot.</summary>
    static (int X, int Y) Spot(PopupSettings o, string corner, RectInt32 area, int w, int h, (int X, int Y) margin)
    {
        if (corner == "Custom")
            return (area.X + (int)Math.Round(Math.Clamp(o.X, 0, 1) * Math.Max(0, area.Width - w)),
                    area.Y + (int)Math.Round(Math.Clamp(o.Y, 0, 1) * Math.Max(0, area.Height - h)));
        bool top = corner.StartsWith("Top", StringComparison.Ordinal);
        int x = corner.EndsWith("Center", StringComparison.Ordinal) ? area.X + (area.Width - w) / 2
              : corner.EndsWith("Left", StringComparison.Ordinal) ? area.X + margin.X
              : area.X + area.Width - w - margin.X;
        return (x, top ? area.Y + margin.Y : area.Y + area.Height - h - margin.Y);
    }

    /// <summary>
    /// The corner to use: the one chosen, or for "Native" where the original shows it: PS5 top right, PS4 top left, Xbox bottom
    /// centre, Steam bottom right, GOG Galaxy and Epic top centre, Hybrid Launcher and Minimal bottom right.
    /// </summary>
    static string CornerOf(PopupSettings o, string look) => o.Corner != "Native" ? o.Corner : look switch
    {
        "PlayStation" => "TopRight",
        _ when IsPs4(look) => "TopLeft",
        "Xbox" => "BottomCenter",
        "Gog" or "Epic" => "TopCenter",
        _ => "BottomRight",
    };

    /// <summary>
    /// DIPs per pixel of the PlayStation templates (1080p frames). Those fill a TV seen from the couch: kept at the template's share
    /// of the screen they were twice the size of the other looks on a desk monitor (a 873 px plate on 1440p). The PS5
    /// plate at 0.85 is 557 DIPs wide (at 0.68 it read as too small next to the original), the PS4 bar at 0.68 is 492;
    /// everything in them keeps the original's proportions.
    /// </summary>
    static double PsUnit(string look) => look == "PlayStation" ? 0.85 : 0.68;

    /// <summary>The PS4 look, white or black ("PlayStation4Black", the console's dark notification theme).</summary>
    static bool IsPs4(string look) => look is "PlayStation4" or "PlayStation4Black";

    /// <summary>
    /// The distance from the screen edges, in pixels: the originals' own (PS5 81 from the side and 59 from the top, the PS4 bar
    /// flush with the side, 126 from the top, in template pixels at <see cref="PsUnit"/>), 20 DIPs for the other looks.
    /// </summary>
    static (int X, int Y) Margins(string look, double scale)
    {
        int Px(double templatePx) => (int)Math.Round(templatePx * PsUnit(look) * scale);
        return look switch
        {
            "PlayStation" => (Px(81), Px(59)),
            _ when IsPs4(look) => (0, Px(126)),
            _ => ((int)(20 * scale), (int)(20 * scale)),
        };
    }

    static double MonitorScale(DisplayArea area)
    {
        try
        {
            var mon = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
            if (GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch (Exception ex) { Log.Warn($"Pop-up monitor DPI: {ex.Message}"); }
        return 1;
    }

    /// <summary>The last unlock shown that belongs to a library game, and when it left the screen (null while it is on screen).</summary>
    static Toast? _lastShown;
    static DateTime? _lastLeft;

    /// <summary>
    /// The unlock the Home button should open, as a console's trophy notice does: the one on screen, or the last one if it left
    /// less than <c>10 s</c> ago. Taken once: the pop-up on screen goes away and a second press is a plain Home press again.
    /// </summary>
    public static Toast? TakeRecent()
    {
        if (_lastShown is not { GameId: not null } t) return null;
        if (_lastLeft is { } left && DateTime.Now - left > TimeSpan.FromSeconds(10)) return null;
        _lastShown = null;
        _run?.Dismiss();
        return t;
    }

    static void ShowNext()
    {
        while (Waiting.TryDequeue(out var item))
        {
            try
            {
                _current = Open(item, placing: false);
                if (item.GameId is not null) { _lastShown = item; _lastLeft = null; }
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Achievement pop-up failed: {ex.Message}");
                _current = null;
            }
        }
    }

    // ---------- looks ----------

    /// <summary>The look to draw: the one chosen, or for "Auto" the one of the game's store.</summary>
    static string ResolveLook(PopupSettings o, GameStore? store, string? toastLook)
    {
        var look = toastLook ?? o.Look;
        if (look == "Auto")
            return store switch { GameStore.Steam => "Steam", GameStore.Epic => "Epic", GameStore.Gog => "Gog", GameStore.Xbox => "Xbox", _ => "Hybrid" };
        return look is "Hybrid" or "PlayStation" or "PlayStation4" or "PlayStation4Black" or "Xbox" or "Steam" or "Gog" or "Epic" or "Minimal" ? look : "Hybrid";
    }

    /// <summary>Width in DIPs and where the toast slides in from (a unit vector towards the near screen edge; scaled by the look).</summary>
    /// <param name="OutSlide">How far back towards the edge it moves while leaving (0 = it only fades where it is, 1 = all the way back).</param>
    sealed record Motion(double Width, double MinHeight, double Dx, double Dy, int InMs, int OutMs, double OutSlide = 0.45, Timeline? Script = null);

    /// <summary>
    /// A motion taken from the original frame by frame (PlayStation): its own ease in and out, its own fades, and sizes in pixels of
    /// a 1080p screen. <paramref name="Inside"/>: the window stays put and the content slides inside it (the PS4 bar enters from
    /// beyond the screen edge, which a moving window would show on the next screen).
    /// </summary>
    sealed record Timeline(Bezier In, Bezier Out, int FadeInMs, int FadeOutDelayMs, int FadeOutMs, bool Inside);

    /// <summary>A CSS-style cubic-bezier ease (the curves After Effects keyframes make).</summary>
    sealed record Bezier(double X1, double Y1, double X2, double Y2)
    {
        public double At(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double lo = 0, hi = 1, s = t;
            for (int i = 0; i < 24; i++)
            {
                s = (lo + hi) / 2;
                double x = 3 * (1 - s) * (1 - s) * s * X1 + 3 * (1 - s) * s * s * X2 + s * s * s;
                if (x < t) lo = s; else hi = s;
            }
            return 3 * (1 - s) * (1 - s) * s * Y1 + 3 * (1 - s) * s * s * Y2 + s * s * s;
        }
    }

    // PS5 (from its After Effects project and a 60 fps capture): 39 px in from the side over 400 ms, fading in over the first
    // 200 ms; out 42 px over 367 ms, fading over 233 ms from 67 ms in.
    static readonly Timeline Ps5Script = new(new(0.25, 0.75, 0.2, 1), new(0.2, 0.05, 0, 1), 200, 67, 233, false);
    // PS4: the opaque bar slides in from fully beyond the screen edge in 542 ms, braking hard; it leaves the same way, speeding up, no fade.
    static readonly Timeline Ps4Script = new(new(0.05, 0.5, 0.3, 1), new(1, 0.05, 0.9, 0.8), 0, 0, 0, true);
    const double Ps5W = 655, Ps5H = 114, Ps4W = 724, Ps4H = 128, Ps4Pad = 34;

    static Motion MotionOf(string look, bool left, bool top, bool center)
    {
        var m = BaseMotion(look, left, top);
        // Centred: a sideways slide would cross the middle of the screen, so it comes from the near top or bottom edge instead.
        return center && m.Dx != 0 ? m with { Dx = 0, Dy = top ? -Math.Abs(m.Dx) : Math.Abs(m.Dx) } : m;
    }

    /// <summary>
    /// How each original moves. PS5: comes in from the side of its corner and goes back the same way. PS4: slides in from off the
    /// screen edge, then only fades where it is. Xbox: the picture pops in a little above the edge, the pill unrolls, then folds back
    /// and fades. Steam: rises from the bottom edge and sinks back. Epic and GOG: drop in from the top edge and fade.
    /// </summary>
    static Motion BaseMotion(string look, bool left, bool top) => look switch
    {
        // In 1080p pixels (Script): the PS5 plate, the PS4 bar with room for its shadow.
        "PlayStation" => new(Ps5W, Ps5H, left ? -39 : 39, 0, 400, 367, 42.0 / 39, Ps5Script),
        _ when IsPs4(look) => new(Ps4W + Ps4Pad, Ps4H + Ps4Pad, left ? -(Ps4W + 39) : Ps4W + 39, 0, 542, 542, (Ps4W + 11) / (Ps4W + 39), Ps4Script),
        "Xbox" => new(390, 80, 0, top ? -26 : 26, 1000, 520, 0.3),
        "Minimal" => new(330, 52, left ? -36 : 36, 0, 360, 260),
        "Epic" => new(400, 88, 0, top ? -40 : 40, 380, 280, 0.3),
        "Gog" => new(440, 96, 0, top ? -44 : 44, 420, 300, 0.3),
        "Steam" => new(440, 96, 0, top ? -70 : 70, 420, 320, 1),
        _ => new(440, 96, left ? -52 : 52, 0, 460, 320),
    };

    /// <param name="Slider">What slides inside a window that stays put (Timeline.Inside).</param>
    /// <param name="Pulse">Called every frame with the time on screen in ms (the PS4 ring's slow glow).</param>
    sealed record Built(Grid Root, FrameworkElement? Icon, ScaleTransform? Bar, Action<double>? Reveal = null, FrameworkElement? Slider = null, Action<double>? Pulse = null);

    static Built Build(string look, Toast t, double size, PopupSettings o, bool left)
    {
        var accent = AccentColor(o);
        var custom = BackgroundColor(o, accent);
        double opacity = Math.Clamp(o.Opacity, 0.6, 1);
        return look switch
        {
            "PlayStation" => BuildPlayStation(t, size, opacity),
            _ when IsPs4(look) => BuildPlayStation4(t, size, opacity, left, look == "PlayStation4Black"),
            "Xbox" => BuildXbox(t, size, opacity),
            "Minimal" => BuildMinimal(t, size, accent, custom, opacity),
            "Steam" => BuildCard(t, size, o.ShowRarity, Color.FromArgb(255, 0x66, 0xC0, 0xF4), Color.FromArgb(255, 0x1B, 0x28, 0x38), opacity, 8, Headline(t), true),
            "Gog" => BuildCard(t, size, o.ShowRarity, Color.FromArgb(255, 0xC0, 0x6C, 0xD0), Color.FromArgb(255, 0x24, 0x1A, 0x2B), opacity, 10, Headline(t), true),
            "Epic" => BuildCard(t, size, o.ShowRarity, Color.FromArgb(255, 0xFF, 0xFF, 0xFF), Color.FromArgb(255, 0x1D, 0x1E, 0x22), opacity, 4, Headline(t), false),
            _ => BuildCard(t, size, o.ShowRarity, t.Completion ? PlatinumColour : accent, custom, opacity, 8, Headline(t), true),
        };
    }

    // ---------- window ----------

    static Window Open(Toast item, bool placing)
    {
        var o = AppState.Settings.Popup;
        double size = o.Size == "Large" ? 1.3 : 1.0;
        string look = ResolveLook(o, item.Store, item.Look);
        double seconds = Math.Clamp(o.Seconds, 2, 15);
        string corner = CornerOf(o, look);
        bool left = corner == "Custom" ? o.X < 0.5 : corner.EndsWith("Left", StringComparison.Ordinal);
        bool top = corner == "Custom" ? o.Y < 0.5 : corner.StartsWith("Top", StringComparison.Ordinal);
        bool center = corner != "Custom" && corner.EndsWith("Center", StringComparison.Ordinal);
        var motion = MotionOf(look, left, top, center);
        var display = TargetDisplay(o);
        // The PlayStation looks are measured in pixels of their 1080p templates.
        double unit = motion.Script is null ? size : size * PsUnit(look);
        // The scale of the screen it will show on, not of the window: a new window starts on another screen (the main one, or
        // none while it waits off screen) and WinUI lays its content out again at the target's scale once it arrives there.
        double scale = MonitorScale(display);
        if (scale <= 0) scale = 1;
        _px = scale;

        var built = Build(look, item, unit, o, left);
        var root = built.Root;
        // The cards and Minimal take exactly their look's width. Without it a long description let the card grow into the room
        // the window keeps for the slide (52 px more on the right for a right-hand corner), past its own edge, cut off there.
        if (look is "Hybrid" or "Steam" or "Gog" or "Epic" or "Minimal") root.Width = motion.Width * unit;
        Button? done = null;
        if (placing)
        {
            // The sample being placed: a Done button over it, and it never leaves by itself.
            done = new Button
            {
                Content = L.T("Done"),
                Style = HybridLauncher.Views.Ui.Style("AccentButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0),
                Padding = new Thickness(14, 4, 14, 5),
            };
            root.Children.Add(done);
        }

        // The window never moves while the toast is on screen: it covers the whole slide and the toast moves inside it, on the
        // compositor's clock. Moving the window every frame (SetWindowPos) is not in step with what it draws, which showed as
        // tearing on the looks that slide up from the bottom edge. The PS4 bar already slides inside its own padded root.
        bool inside = !placing && motion.Script is not { Inside: true } && (motion.Dx != 0 || motion.Dy != 0);
        double reach = Math.Max(1, motion.OutSlide);
        int extraX = inside ? (int)Math.Ceiling(Math.Abs(motion.Dx) * unit * reach * scale) : 0;
        int extraY = inside ? (int)Math.Ceiling(Math.Abs(motion.Dy) * unit * reach * scale) : 0;
        FrameworkElement content = root;
        if (inside)
        {
            root.HorizontalAlignment = HorizontalAlignment.Left;
            root.VerticalAlignment = VerticalAlignment.Top;
            root.Margin = new Thickness(motion.Dx < 0 ? extraX / scale : 0, motion.Dy < 0 ? extraY / scale : 0, 0, 0);
            var host = new Grid { IsHitTestVisible = true, Background = null };
            host.Children.Add(root);
            content = host;
        }

        var window = new Window { Title = "Hybrid Launcher achievement", Content = content };
        // Fully transparent window: only what the toast draws is ever on screen. Without a backdrop of its own the window has
        // no dark rectangle to show while it moves, fades or closes.
        TransparentBackdrop.Reset();
        try { window.SystemBackdrop = new TransparentBackdrop(); }
        catch (Exception ex) { Log.Warn($"Transparent pop-up backdrop: {ex}"); }

        var app = window.AppWindow;
        app.IsShownInSwitchers = false;
        if (app.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // Cloaked from the start: nothing of this window may reach the screen before it is in place and drawn. (It used to be
        // made visible by the style change below, for a frame or two at Windows' default place and size with a thin frame:
        // the "ghost window" seen when a pop-up opened.)
        if (!placing) Cloak(hwnd, true);
        // A plain pop-up window: no caption or sizing frame left over from the normal window, a tool window (not in Alt+Tab)
        // that never takes the focus from the game.
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const long WS_POPUP = 0x80000000L, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        // Not WS_VISIBLE: the window shows when Show() is called, once it has its place.
        _ = SetWindowLongPtr(hwnd, GWL_STYLE, unchecked((nint)WS_POPUP));
        _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | (placing ? 0 : WS_EX_NOACTIVATE)));
        _ = SetWindowPos(hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020); // NOSIZE | NOMOVE | NOACTIVATE | FRAMECHANGED
        // Self-test guard: before Show() the window must be neither visible nor uncovered.
        if (!placing && IsWindowVisible(hwnd) && !(DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int hidden, sizeof(int)) == 0 && hidden != 0))
        {
            UncoveredEarly = true;
            Log.Warn("Pop-up window visible before it was placed");
        }
        // The toast draws its own rounded corners; Windows' round-off and its thin light frame are both switched off.
        int noRound = 1; // DWMWCP_DONOTROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref noRound, sizeof(int));
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int));
        // No show / hide animation of Windows' own: it fades a snapshot of the window in and out, and on closing that snapshot
        // (taken mid-fade, with whatever the surface last held) lingers for a few frames as a coloured ghost of a window that is
        // already gone. The toast animates itself.
        int noTransitions = 1; // DWMWA_TRANSITIONS_FORCEDISABLED
        _ = DwmSetWindowAttribute(hwnd, 3, ref noTransitions, sizeof(int));
        // Per-pixel alpha: an empty blur region tells DWM to compose the window by its own alpha, so what the toast does not paint
        // (the corners, and all of it while it fades) is what is behind the window, not a black rectangle.
        var blur = new DwmBlurBehind { Flags = 3 /* ENABLE | BLURREGION */, Enable = 1, Region = CreateRectRgn(0, 0, -1, -1) };
        _ = DwmEnableBlurBehindWindow(hwnd, ref blur);
        if (blur.Region != 0) _ = DeleteObject(blur.Region);

        var area = display.WorkArea;
        var margins = Margins(look, scale);
        double widthDip = motion.Width * unit;
        int w = (int)Math.Round(widthDip * scale);
        // The height follows the text (a long name or description wraps instead of being cut): measured before it shows.
        int Height()
        {
            root.Measure(new Windows.Foundation.Size(widthDip, double.PositiveInfinity));
            return (int)Math.Ceiling(Math.Max(motion.MinHeight * unit, root.DesiredSize.Height) * scale);
        }
        int h = Height();
        var (x, y) = Spot(o, corner, area, w, h, margins);
        // The window's own rectangle: the toast's resting place, grown towards the edge it slides in from.
        (int X, int Y, int W, int H) Frame(int fx, int fy, int fh) =>
            (motion.Dx < 0 ? fx - extraX : fx, motion.Dy < 0 ? fy - extraY : fy, w + extraX, fh + extraY);
        var frame = Frame(x, y, h);

        if (!placing)
        {
            // Opens invisible, already where it stays: the window never moves on screen. It is cloaked (Windows composes it but
            // shows nothing) until the animator has seen its first frames drawn, then uncovered in one DWM frame. Shown off screen and
            // moved in instead, the first frame on the game's display could show a surface not drawn there yet, or be rescaled
            // mid-slide when that display has another DPI: a one-frame tear.
            root.Opacity = 0;
            app.MoveAndResize(new RectInt32(frame.X, frame.Y, frame.W, frame.H));
        }
        else app.MoveAndResize(new RectInt32(x, y, w, h));
        app.Show(placing);
        if (!placing) PlaySound(o, Tier(item).Name);

        // Once laid out in the window (fonts loaded), fit the height again if it differs.
        root.Loaded += (_, _) =>
        {
            int h2 = Height();
            if (Math.Abs(h2 - h) <= 2) return;
            h = h2;
            var (x2, y2) = Spot(o, corner, area, w, h2, margins);
            if (placing) app.MoveAndResize(new RectInt32(x2, y2, w, h2));
            else if (_run is { } r && r.Window == window)
            {
                var f2 = Frame(x2, y2, h2);
                if (inside) app.Resize(new SizeInt32(f2.W, f2.H));
                r.Resize(f2.X, f2.Y, f2.W, f2.H);
            }
        };

        if (placing)
        {
            // Dragged by its body (Windows moves it, as by a title bar); each drop saves the spot.
            root.PointerPressed += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInside(d, done)) return;
                _ = ReleaseCapture();
                _ = SendMessageW(hwnd, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
                var pos = app.Position;
                var a = TargetDisplay(AppState.Settings.Popup).WorkArea;
                double fx = a.Width > app.Size.Width ? (pos.X - a.X) / (double)(a.Width - app.Size.Width) : 0;
                double fy = a.Height > app.Size.Height ? (pos.Y - a.Y) / (double)(a.Height - app.Size.Height) : 0;
                var s = AppState.Settings.Popup;
                s.Corner = "Custom";
                s.X = Math.Clamp(fx, 0, 1);
                s.Y = Math.Clamp(fy, 0, 1);
                SettingsStore.Save();
                _placed?.Invoke(s.X, s.Y);
            };
            done!.Click += (_, _) =>
            {
                _placing = null;
                try { window.Close(); } catch { }
            };
            window.Closed += (_, _) => { if (_placing == window) _placing = null; };
            return window;
        }

        var run = new Run(window, hwnd, root, built.Icon, built.Bar, built.Reveal, frame.X, frame.Y, frame.W, frame.H, motion.Dx * unit, motion.Dy * unit, scale, motion.InMs, motion.OutMs, motion.OutSlide, seconds, () => Finish(window), motion.Script, inside ? root : built.Slider, built.Pulse, inside);
        _run = run;
        run.Start();
        // The last guard: whatever happened to the frame clock, it is gone shortly after its time.
        var queue = window.DispatcherQueue;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds + 3)).ContinueWith(_ => queue.TryEnqueue(() => Finish(window)), TaskScheduler.Default);
        // A click sends it away at once.
        root.Tapped += (_, _) => run.Dismiss();
        return window;
    }

    // ---------- the animator ----------

    /// <summary>
    /// One toast's motion: wait for the first frames to be drawn (no hitch at the start), slide in with a fast-out ease while it
    /// fades, hold with the time line running, then slide a little back and fade. Everything is a function of the elapsed time,
    /// so a slow frame never slows the animation down, it only skips ahead.
    /// </summary>
    sealed class Run
    {
        public readonly Window Window;
        readonly nint _hwnd;
        readonly FrameworkElement _root;
        readonly FrameworkElement? _icon;
        readonly ScaleTransform? _bar, _pop;
        readonly Action<double>? _reveal;
        int _x, _y, _w, _h, _out;
        readonly int _dx, _dy, _in, _outNominal;
        readonly double _hold, _outSlide, _dxDip, _dyDip;
        readonly Action _finish;
        readonly Timeline? _script;
        readonly TranslateTransform? _slide;
        readonly Action<double>? _pulse;
        long _startTicks, _outTicks, _lastFrame;
        int _frames, _drawn, _lastX = int.MinValue, _lastY = int.MinValue;
        bool _started, _done, _awake;
        double _fromSlide = 1, _fromOpacity, _fromReveal;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdog;

        /// <param name="dxDip">The slide in DIPs (the window moves by it in pixels, or the slider inside it in DIPs).</param>
        /// <param name="inside">The window stays put and <paramref name="slider"/> (the toast) slides inside it.</param>
        public Run(Window window, nint hwnd, FrameworkElement root, FrameworkElement? icon, ScaleTransform? bar, Action<double>? reveal, int x, int y, int w, int h, double dxDip, double dyDip, double scale, int inMs, int outMs, double outSlide, double hold, Action finish, Timeline? script = null, FrameworkElement? slider = null, Action<double>? pulse = null, bool inside = false)
        {
            Window = window; _hwnd = hwnd; _root = root; _icon = icon; _bar = bar; _reveal = reveal;
            _reveal?.Invoke(0);
            _x = x; _y = y; _lastX = x; _lastY = y; _w = w; _h = h; _dxDip = dxDip; _dyDip = dyDip; _dx = (int)Math.Round(dxDip * scale); _dy = (int)Math.Round(dyDip * scale);
            _in = inMs; _out = _outNominal = outMs; _outSlide = outSlide; _hold = hold; _finish = finish;
            _script = script; _pulse = pulse;
            if ((inside || script is { Inside: true }) && slider is not null)
            {
                slider.RenderTransform = _slide = new TranslateTransform { X = dxDip, Y = dyDip };
            }
            if (icon is not null)
            {
                icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                icon.RenderTransform = _pop = new ScaleTransform { ScaleX = 0.7, ScaleY = 0.7 };
            }
        }

        public void Resize(int x, int y, int w, int h) { _x = x; _y = y; _w = w; _h = h; _lastX = int.MinValue; }

        public void Start()
        {
            // Out of Windows' efficiency mode while it is on screen (over a game it would stutter).
            _awake = true;
            MediaPolicy.KeepAwake(true);
            CompositionTarget.Rendering += OnFrame;
            // If the frame clock does not run (nothing is being drawn), a timer steps the animation instead.
            _watchdog = Window.DispatcherQueue.CreateTimer();
            _watchdog.Interval = TimeSpan.FromMilliseconds(16);
            _watchdog.Tick += (_, _) => { if (Stopwatch.GetTimestamp() - _lastFrame > Stopwatch.Frequency / 10) SafeStep(); };
            _watchdog.Start();
        }

        public void Dismiss()
        {
            if (_done || !_started || _outTicks != 0) return;
            _outTicks = Stopwatch.GetTimestamp();
        }

        /// <summary>Replaced by a new toast: leaves now, in <paramref name="ms"/>. False when it has not come on screen yet (close it instead).</summary>
        public bool Leave(int ms)
        {
            if (_done || !_started) return false;
            long now = Stopwatch.GetTimestamp();
            if (_outTicks == 0) { _out = ms; _outTicks = now; }
            else
            {
                // Already leaving: finish from where it is, no slower than asked.
                double gone = (now - _outTicks) * 1000.0 / Stopwatch.Frequency / _out;
                if ((1 - gone) * _out > ms) { _out = ms; _outTicks = now - (long)(gone * ms * Stopwatch.Frequency / 1000.0); }
            }
            return true;
        }

        void OnFrame(object? sender, object e)
        {
            _lastFrame = Stopwatch.GetTimestamp();
            if (_started) _drawn++;
            SafeStep();
        }

        /// <summary>A frame never throws back into the frame clock (that would take the app down): the toast just ends.</summary>
        void SafeStep()
        {
            try { Step(); }
            catch (Exception ex)
            {
                Log.Warn($"Pop-up animation: {ex.Message}");
                Stop();
                try { Window.DispatcherQueue.TryEnqueue(() => _finish()); } catch { }
            }
        }

        void Step()
        {
            if (_done) return;
            long now = Stopwatch.GetTimestamp();
            // Two frames drawn first: the window is on screen (invisible) and its content laid out.
            if (!_started)
            {
                // Still cloaked while its first frames are drawn: if something (WinUI) uncovered it already, it would show early.
                if (++_frames == 1 && DwmGetWindowAttribute(_hwnd, 14 /* DWMWA_CLOAKED */, out int c, sizeof(int)) == 0 && c == 0)
                {
                    UncoveredEarly = true;
                    Log.Warn("Pop-up window uncovered before its first frames were drawn");
                }
                if (_frames < 3) return;
                _started = true;
                _startTicks = now;
                Cloak(_hwnd, false);
            }
            double ms = (now - _startTicks) * 1000.0 / Stopwatch.Frequency;
            double holdMs = _hold * 1000.0;
            if (_outTicks == 0 && ms >= _in + holdMs) _outTicks = now;
            if (_script is { } script) { StepScript(script, now, ms); return; }

            double opacity, slide;
            if (_outTicks == 0)
            {
                double t = Math.Clamp(ms / _in, 0, 1);
                slide = 1 - EaseOutQuint(t);
                opacity = Math.Clamp(ms / (_in * 0.55), 0, 1);
                opacity = opacity * opacity * (3 - 2 * opacity); // smoothstep
                if (_pop is not null)
                {
                    double pt = Math.Clamp((ms - 70) / (_in + 160), 0, 1);
                    double s = 0.7 + 0.3 * EaseOutBack(pt);
                    _pop.ScaleX = _pop.ScaleY = s;
                }
                if (_bar is not null) _bar.ScaleX = ms <= _in ? 1 : Math.Clamp(1 - (ms - _in) / holdMs, 0, 1);
                _reveal?.Invoke(t);
                _fromSlide = slide; _fromOpacity = opacity; _fromReveal = t;
            }
            else
            {
                // It leaves from wherever it is: a toast sent away (or replaced) while still coming in does not jump to its
                // resting place first, it turns round from the spot and the opacity it has.
                double outMs = (now - _outTicks) * 1000.0 / Stopwatch.Frequency;
                double t = Math.Clamp(outMs / _out, 0, 1);
                double q = t * t * t;
                slide = _fromSlide + (Math.Max(_fromSlide, _outSlide) - _fromSlide) * q; // a little further towards the edge it came from
                opacity = _fromOpacity * (1 - t * t * (3 - 2 * t));
                _reveal?.Invoke(_fromReveal * (1 - Math.Min(1, t * 1.6))); // the pill folds back first, then the whole thing fades
                if (t >= 1) { End(); return; }
            }

            _root.Opacity = opacity;
            int px = _x, py = _y;
            if (_slide is not null)
            {
                _slide.X = _dxDip * slide;
                _slide.Y = _dyDip * slide;
            }
            else
            {
                px += (int)Math.Round(_dx * slide);
                py += (int)Math.Round(_dy * slide);
            }
            if (px != _lastX || py != _lastY)
            {
                _lastX = px; _lastY = py;
                _ = SetWindowPos(_hwnd, 0, px, py, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
            }
        }

        /// <summary>
        /// The original's own motion (PlayStation): its ease in, its fade in (or none), the hold, then its ease out towards the edge
        /// and its fade, which starts a little after the slide. A toast replaced while showing leaves at the shorter time asked,
        /// its fade squeezed the same way.
        /// </summary>
        void StepScript(Timeline s, long now, double ms)
        {
            double opacity, slide;
            if (_outTicks == 0)
            {
                slide = 1 - s.In.At(ms / _in);
                opacity = s.FadeInMs > 0 ? Math.Clamp(ms / s.FadeInMs, 0, 1) : 1;
                _fromSlide = slide; _fromOpacity = opacity;
            }
            else
            {
                double outMs = (now - _outTicks) * 1000.0 / Stopwatch.Frequency;
                double t = Math.Clamp(outMs / _out, 0, 1);
                slide = _fromSlide + (_outSlide - _fromSlide) * s.Out.At(t);
                double squeeze = _out / (double)_outNominal;
                opacity = s.FadeOutMs > 0 ? _fromOpacity * (1 - Math.Clamp((outMs - s.FadeOutDelayMs * squeeze) / (s.FadeOutMs * squeeze), 0, 1)) : _fromOpacity;
                if (t >= 1) { End(); return; }
            }
            _pulse?.Invoke(ms);
            _root.Opacity = opacity;
            int px = _x, py = _y;
            if (_slide is not null)
            {
                _slide.X = _dxDip * slide;
                _slide.Y = _dyDip * slide;
            }
            else
            {
                px += (int)Math.Round(_dx * slide);
                py += (int)Math.Round(_dy * slide);
            }
            if (px != _lastX || py != _lastY)
            {
                _lastX = px; _lastY = py;
                _ = SetWindowPos(_hwnd, 0, px, py, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
            }
        }

        void End()
        {
            if (_done) return;
            _done = true;
            Release();
            _root.Opacity = 0;
            // Gone from the screen in this DWM frame: closing a window can show one last frame of it.
            Cloak(_hwnd, true);
            // How smooth it was: the frames the display's clock gave it over its whole time on screen.
            double total = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
            if (total > 0) Log.Info($"Pop-up shown {total / 1000:0.0} s, {_drawn} frames ({_drawn * 1000 / total:0} fps)");
            // Not closed from inside the frame clock's own callback: the window goes on the next turn of the dispatcher.
            if (!Window.DispatcherQueue.TryEnqueue(() => _finish())) _finish();
        }

        public void Stop()
        {
            if (_done) return;
            _done = true;
            Release();
        }

        void Release()
        {
            CompositionTarget.Rendering -= OnFrame;
            try { _watchdog?.Stop(); } catch { }
            if (_awake) { _awake = false; MediaPolicy.KeepAwake(false); }
        }
    }

    static double EaseOutQuint(double t) => 1 - Math.Pow(1 - t, 5);

    static double EaseOutBack(double t)
    {
        const double c1 = 1.9, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    static bool IsInside(DependencyObject? d, DependencyObject? target)
    {
        for (; d is not null && target is not null; d = VisualTreeHelper.GetParent(d))
            if (d == target) return true;
        return false;
    }

    static readonly HashSet<Window> Closed = [];

    static void Finish(Window window, bool next = true)
    {
        if (!Closed.Add(window)) return;
        // The queue is taken before Close: a closed window refuses every property.
        Microsoft.UI.Dispatching.DispatcherQueue? queue = null;
        try { queue = window.DispatcherQueue; } catch { }
        if (_run is { } r && r.Window == window) { r.Stop(); _run = null; }
        foreach (var l in Leaving.ToArray())
            if (l.Window == window) { l.Stop(); Leaving.Remove(l); }
        try { window.Close(); } catch { }
        if (_current == window)
        {
            _current = null;
            if (_lastShown is not null) _lastLeft ??= DateTime.Now;
        }
        if (next && _current is null) ShowNext();
        // Forget it once its timers are surely done.
        _ = Task.Delay(20000).ContinueWith(_ => queue?.TryEnqueue(() => Closed.Remove(window)), TaskScheduler.Default);
    }

    /// <summary>The display of the window in front (the game), or the main display.</summary>
    static DisplayArea TargetDisplay(PopupSettings o)
    {
        try
        {
            if (o.Screen == "Game" && GetForegroundWindow() is var fg && fg != 0)
                return DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(fg), DisplayAreaFallback.Primary);
        }
        catch (Exception ex) { Log.Warn($"Pop-up display: {ex.Message}"); }
        return DisplayArea.Primary;
    }

    /// <summary>
    /// The pop-up's sound: with "File", your .wav for the achievement's rarity tier (bronze, silver, gold, platinum), else your .wav
    /// for every achievement; otherwise (or when the file is gone) the Windows notification sound. No sounds ship with the app.
    /// </summary>
    static void PlaySound(PopupSettings o, string tier)
    {
        try
        {
            LastSound = null;
            if (o.Sound == "None") return;
            string? file = o.Sound == "File" ? SoundFor(o, tier) : null;
            file ??= WindowsNotificationSound();
            LastSound = file ?? "Notification.Default";
            if (file is not null) PlayAt(file, Math.Clamp(o.Volume, 0, 1));
            else _ = PlaySoundW("Notification.Default", 0, 0x00010000 /* ALIAS */ | 0x0001 /* ASYNC */ | 0x0002 /* NODEFAULT */);
        }
        catch (Exception ex) { Log.Warn($"Pop-up sound: {ex.Message}"); }
    }

    /// <summary>What the last pop-up sound played: a file, "Notification.Default" (the system sound by name), or null (none) (self-test).</summary>
    internal static string? LastSound { get; set; }

    /// <summary>Your .wav for <paramref name="tier"/> (or for every achievement) when it is still there, else null.</summary>
    public static string? SoundFor(PopupSettings o, string tier) =>
        o.TierSounds.GetValueOrDefault(tier) is { Length: > 0 } t && File.Exists(t) ? t
        : o.SoundFile is { Length: > 0 } f && File.Exists(f) ? f : null;

    /// <summary>Settings → Achievements → Sounds: the sound an achievement of <paramref name="tier"/> plays, at the chosen volume.</summary>
    public static void PlaySample(string tier = "Gold") => PlaySound(AppState.Settings.Popup, tier);

    /// <summary>The file of Windows' notification sound (the current sound scheme), so it can be played at the chosen volume.</summary>
    static string? WindowsNotificationSound()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\Notification.Default\.Current");
            if (key?.GetValue(null) is string { Length: > 0 } v)
            {
                var path = Environment.ExpandEnvironmentVariables(v);
                if (!Path.IsPathRooted(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", path);
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception ex) { Log.Warn($"Pop-up sound scheme: {ex.Message}"); }
        return null;
    }

    /// <summary>The sound playing now, pinned while Windows reads it.</summary>
    static GCHandle _playing;

    /// <summary>
    /// Plays a .wav at <paramref name="volume"/>: 16-bit PCM (the app's own sounds, Windows' sounds, most .wav files) is scaled in
    /// memory and played from there; anything else plays as it is.
    /// </summary>
    static void PlayAt(string file, double volume)
    {
        const uint Async = 0x0001, NoDefault = 0x0002, Memory = 0x0004, FileName = 0x00020000;
        if (volume <= 0) { LastSound = null; return; }
        var wav = File.ReadAllBytes(file);
        if (volume < 0.999 && Pcm16Data(wav) is var (start, length) && length > 0)
        {
            for (int i = start; i + 1 < start + length; i += 2)
            {
                int sample = (short)(wav[i] | wav[i + 1] << 8);
                sample = (int)Math.Round(sample * volume);
                wav[i] = (byte)sample;
                wav[i + 1] = (byte)(sample >> 8);
            }
        }
        else if (volume < 0.999)
        {
            _ = PlaySoundW(file, 0, FileName | Async | NoDefault);
            return;
        }
        var handle = GCHandle.Alloc(wav, GCHandleType.Pinned);
        _ = PlaySoundPtr(handle.AddrOfPinnedObject(), 0, Memory | Async | NoDefault);
        // The sound before it has stopped (a new one replaces it): its buffer can go.
        if (_playing.IsAllocated) _playing.Free();
        _playing = handle;
    }

    /// <summary>The .wav is 16-bit PCM, so the volume scales it in memory (self-test).</summary>
    internal static bool ScalesVolume(string file) => Pcm16Data(File.ReadAllBytes(file)) is { Length: > 0 };

    /// <summary>Where the samples of a 16-bit PCM .wav are (offset and length in bytes), or null for any other format.</summary>
    static (int Start, int Length)? Pcm16Data(byte[] wav)
    {
        if (wav.Length < 44 || BitConverter.ToUInt32(wav, 0) != 0x46464952 /* RIFF */ || BitConverter.ToUInt32(wav, 8) != 0x45564157 /* WAVE */) return null;
        bool pcm16 = false;
        for (int at = 12; at + 8 <= wav.Length;)
        {
            uint id = BitConverter.ToUInt32(wav, at);
            int size = BitConverter.ToInt32(wav, at + 4);
            if (size < 0) return null;
            int body = at + 8;
            if (id == 0x20746D66 /* "fmt " */ && body + 16 <= wav.Length)
            {
                ushort format = BitConverter.ToUInt16(wav, body), bits = BitConverter.ToUInt16(wav, body + 14);
                pcm16 = bits == 16 && (format == 1 || format == 0xFFFE && body + 40 <= wav.Length && wav[body + 24] == 1);
            }
            else if (id == 0x61746164 /* "data" */)
                return pcm16 ? (body, Math.Min(size, wav.Length - body)) : null;
            at = body + size + (size & 1);
        }
        return null;
    }

    // ---------- shared pieces ----------

    static SolidColorBrush Solid(Color c) => new(c);
    static SolidColorBrush Solid(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    static UIElement IconPicture(Toast item, double side, Stretch stretch = Stretch.UniformToFill)
    {
        var host = new Border { Width = side, Height = side };
        if (item.Completion && item.IconUrl is null) return Icons.PlatinumEmblem(side);
        if (Picture(item.IconUrl, side) is { } picture)
            return Smooth.Art(picture, Smooth.Inner(host), stretch);
        return Icons.Trophy(side * 0.6, Solid(Colors.White));
    }

    /// <summary>
    /// The trophy tier: platinum only for the game's own platinum (its achievement for unlocking all the others); otherwise by rarity, the rarer the
    /// higher: bronze for 30% of players or more (or no percentage), silver from 10%, gold under 10%.
    /// </summary>
    /// <summary>Platinum with a diamond's blue: an icy blue white (the card accent, the tier line).</summary>
    internal static readonly Color PlatinumColour = Color.FromArgb(255, 0xC6, 0xE4, 0xFF);

    static (string Name, Color Colour) Tier(Toast item) => item.Completion ? ("Platinum", PlatinumColour) : item.Percent switch
    {
        null => ("Bronze", Color.FromArgb(255, 0xCD, 0x7F, 0x4A)),
        >= 30 => ("Bronze", Color.FromArgb(255, 0xCD, 0x7F, 0x4A)),
        >= 10 => ("Silver", Color.FromArgb(255, 0xC6, 0xCB, 0xD4)),
        _ => ("Gold", Color.FromArgb(255, 0xFF, 0xD3, 0x4D)),
    };

    static Grid Shell(double radius, Brush background, Color edge)
    {
        var root = new Grid { RequestedTheme = ElementTheme.Dark, CornerRadius = new CornerRadius(radius), Background = background };
        root.Children.Add(new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), BorderBrush = Solid(edge), IsHitTestVisible = false });
        return root;
    }

    static Brush Gradient(Color from, Color to) => new LinearGradientBrush
    {
        StartPoint = new(0, 0),
        EndPoint = new(1, 1),
        GradientStops = { new GradientStop { Color = from, Offset = 0 }, new GradientStop { Color = to, Offset = 1 } },
    };

    static Color WithAlpha(Color c, double opacity) => Color.FromArgb((byte)Math.Round(255 * opacity), c.R, c.G, c.B);

    // ---------- PlayStation (shared) ----------

    // Both PlayStation notices are set in Calibri Light, as the After Effects projects they were measured from.
    static readonly FontFamily PsFont = new("Calibri Light");

    /// <summary>
    /// A line of PlayStation text placed by the top of its capitals (the measurements are of capitals): the box is trimmed to the
    /// cap height, so Canvas.Top is where the capitals start whatever the font's line spacing.
    /// </summary>
    static TextBlock PsText(string text, double fontSize, Color colour, double maxWidth) => new()
    {
        Text = text,
        FontFamily = PsFont,
        FontSize = fontSize,
        Foreground = Solid(colour),
        MaxWidth = maxWidth,
        MaxLines = 1,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextLineBounds = TextLineBounds.TrimToCapHeight,
        OpticalMarginAlignment = OpticalMarginAlignment.TrimSideBearings,
        IsHitTestVisible = false,
    };

    /// <summary>Device pixels per DIP of the screen the pop-up being built goes to (set before each build).</summary>
    static double _px = 1;

    /// <summary>A length or position in DIPs on whole device pixels: edges that fall between two pixels are drawn soft.</summary>
    static double Snap(double dip) => Math.Round(dip * _px) / _px;

    /// <summary>Like <see cref="Snap"/>, an even number of pixels, so something centred on a whole pixel stays on whole pixels.</summary>
    static double SnapEven(double dip) => Math.Round(dip * _px / 2) * 2 / _px;

    static T At<T>(T e, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(e, Snap(left));
        Canvas.SetTop(e, Snap(top));
        return e;
    }

    static Geometry PathData(string d) => Icons.NewGeometry(d);

    /// <summary>
    /// The game's picture for the trophy, filling a <paramref name="side"/> square, or <paramref name="empty"/> with the trophy on
    /// it. <paramref name="free"/> (PlayStation 5): the picture stands on its own as the console shows it, whole and unframed (a
    /// round or see-through icon keeps its shape, no dark square behind it, nothing cropped or stretched).
    /// </summary>
    static FrameworkElement PsPicture(Toast item, double side, Color empty, string tier, bool free = false)
    {
        side = Snap(side);
        var host = new Border { Width = side, Height = side, Background = Solid(empty) };
        if (item.Completion && item.IconUrl is null)
        {
            host.Background = null;
            host.Child = Icons.PlatinumEmblem(side);
        }
        else if (Picture(item.IconUrl, side) is { } picture)
        {
            if (free) host.Background = null;
            host.Child = Smooth.Art(picture, Smooth.Inner(host), free ? Stretch.Uniform : Stretch.UniformToFill);
        }
        else
            host.Child = new Border { Child = Icons.TierTrophy(side * 0.6, tier), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return host;
    }

    // ---------- PlayStation 5 ----------

    /// <summary>
    /// The PlayStation 5 trophy notice, measured from its After Effects project and a capture (1080p pixels, scaled by
    /// <paramref name="u"/>): an opaque 655 x 114 plate with 19.4 px corners and no outline, a horizontal gradient from #403840
    /// to #242123 between 10% and 71% of its width; the picture 68 px square; the tier's trophy, the name in white and
    /// "Trophy earned!" in grey, all in Calibri Light.
    /// </summary>
    static Built BuildPlayStation(Toast item, double u, double opacity)
    {
        var (tier, _) = Tier(item);
        var plate = new Border
        {
            Width = Ps5W * u,
            Height = Ps5H * u,
            CornerRadius = new CornerRadius(19.4 * u),
            Background = new LinearGradientBrush
            {
                StartPoint = new(0.101, 0.5),
                EndPoint = new(0.711, 0.5),
                GradientStops =
                {
                    new GradientStop { Color = WithAlpha(Color.FromArgb(255, 0x40, 0x38, 0x3F), opacity), Offset = 0 },
                    new GradientStop { Color = WithAlpha(Color.FromArgb(255, 0x24, 0x21, 0x23), opacity), Offset = 1 },
                },
            },
        };
        var canvas = new Canvas { Width = Ps5W * u, Height = Ps5H * u, IsHitTestVisible = false };
        canvas.Children.Add(At(PsPicture(item, 68 * u, Color.FromArgb(255, 0x14, 0x13, 0x15), tier, free: true), 26 * u, 23 * u));
        // The app's trophy in the tier's metal, a square centred on the name's capitals, its left edge on the line below's.
        canvas.Children.Add(At(Icons.TierTrophy(Snap(34 * u), tier), 107 * u, 23 * u));
        canvas.Children.Add(At(PsText(item.Name, 28 * u, Colors.White, (Ps5W - 146 - 34) * u), 146 * u, 31 * u));
        canvas.Children.Add(At(PsText(L.T("Trophy earned!"), 23 * u, Color.FromArgb(255, 0x7D, 0x77, 0x7B), (Ps5W - 109 - 34) * u), 109 * u, 74.5 * u));
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Width = Ps5W * u, Height = Ps5H * u };
        root.Children.Add(plate);
        root.Children.Add(canvas);
        return new Built(root, null, null);
    }

    // ---------- Xbox ----------

    /// <summary>
    /// The Xbox achievement banner: the green-ringed picture pops in first, then the dark pill unrolls from it to the right and the text
    /// appears ("Achievement unlocked", the name, the gamerscore when the source gives one); on the way out it folds back.
    /// </summary>
    static Built BuildXbox(Toast item, double size, double opacity)
    {
        var green = Color.FromArgb(255, 0x10, 0x9C, 0x10);
        double ring = 60 * size, pad = 10 * size, full = 390 * size, closed = ring + 2 * pad;

        var circle = new Grid { Width = ring, Height = ring, VerticalAlignment = VerticalAlignment.Center };
        circle.Children.Add(new Border { CornerRadius = new CornerRadius(ring / 2), Background = Gradient(Color.FromArgb(255, 0x1B, 0xB1, 0x1B), Color.FromArgb(255, 0x0B, 0x6A, 0x0B)) });
        var pic = new Border { Width = ring - 8 * size, Height = ring - 8 * size, CornerRadius = new CornerRadius((ring - 8 * size) / 2), Background = Solid(0xFF, 0x10, 0x10, 0x10), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        pic.Child = IconPicture(item, ring - 8 * size);
        circle.Children.Add(pic);

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        text.Children.Add(new TextBlock { Text = (item.Points is { } g ? g + "G  ·  " : "") + L.T(item.Completion ? "Platinum unlocked" : "Achievement unlocked"), FontSize = 12.5 * size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Solid(0xFF, 0x7C, 0xE0, 0x7C), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        text.Children.Add(new TextBlock { Text = item.Name, FontSize = 17 * size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Solid(Colors.White), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
        if (item.Game is { Length: > 0 } game)
            text.Children.Add(new TextBlock { Text = game, FontSize = 11.5 * size, Foreground = Solid(0xAA, 0xFF, 0xFF, 0xFF), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });

        // The content is laid out at its full width; the pill around it is what grows, and clips it.
        var grid = new Grid { Width = full - 2, ColumnSpacing = 16 * size, Padding = new Thickness(pad, pad, 26 * size, pad) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(circle);
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        var pill = new Border
        {
            Width = full,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(40 * size),
            Background = Gradient(WithAlpha(Color.FromArgb(255, 0x2A, 0x2C, 0x2E), opacity), WithAlpha(Color.FromArgb(255, 0x1A, 0x1B, 0x1D), opacity)),
            BorderBrush = Solid(0x60, green.R, green.G, green.B),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.Children.Add(pill);
        void Reveal(double p)
        {
            double grow = Math.Clamp((p - 0.3) / 0.6, 0, 1);
            grow = 1 - Math.Pow(1 - grow, 3);
            pill.Width = closed + (full - closed) * grow;
            text.Opacity = Math.Clamp((p - 0.62) / 0.3, 0, 1);
        }
        return new Built(root, circle, null, Reveal);
    }

    // ---------- PlayStation 4 ----------

    /// <summary>
    /// The PlayStation 4 trophy notice, measured from its After Effects project and a capture (1080p pixels, scaled by
    /// <paramref name="u"/>): an opaque white 724 x 128 bar flush with the screen edge, square corners, a soft shadow down and to the
    /// right; the picture 96 px square with its own small shadow; the name in black Calibri Light and the tier's trophy under it;
    /// on the right the black PS disc in a light-blue ring that slowly glows and fades (a 4 s breath). The bar slides inside the
    /// window (<see cref="Built.Slider"/>), so it comes from beyond the screen edge without showing on a neighbouring screen.
    /// <paramref name="black"/>: the console's dark theme, the same bar in near-black with the name in white and a lighter disc.
    /// </summary>
    static Built BuildPlayStation4(Toast item, double u, double opacity, bool left, bool black = false)
    {
        var faceColour = black ? Color.FromArgb(255, 0x16, 0x16, 0x18) : Colors.White;
        var emptyColour = black ? Color.FromArgb(255, 0x33, 0x33, 0x36) : Color.FromArgb(255, 0xBC, 0xBC, 0xBC);
        var (tier, _) = Tier(item);
        double W = Ps4W * u, H = Ps4H * u;
        var bar = new Grid { Width = W, Height = H, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };

        // The white face is a composition sprite with a drop shadow (XAML has no soft shadow that shows in a see-through window).
        var face = new Border { Width = W, Height = H, Background = Solid(WithAlpha(faceColour, opacity)) };
        bar.Children.Add(face);
        Shadow(face, W, H, WithAlpha(faceColour, opacity), 26 * u, 5 * u, 7 * u, black ? 0.7f : 0.5f);

        var canvas = new Canvas { Width = W, Height = H, IsHitTestVisible = false };
        var picture = PsPicture(item, 96 * u, emptyColour, tier);
        // A child visual draws over its element's content, so each shadow has an empty element of its own behind the content.
        var pictureShadow = new Border { Width = Snap(96 * u), Height = Snap(96 * u) };
        canvas.Children.Add(At(pictureShadow, 130 * u, 16 * u));
        Shadow(pictureShadow, Snap(96 * u), Snap(96 * u), emptyColour, 5 * u, 3 * u, 3 * u, 0.55f);
        canvas.Children.Add(At(picture, 130 * u, 16 * u));
        canvas.Children.Add(At(PsText(item.Name, 35.5 * u, black ? Colors.White : Colors.Black, (591 - 247 - 14) * u), 247 * u, 27.8 * u));
        // The app's trophy in the tier's metal under the name, its left edge on the name's.
        // 6 px lower than the capture's place, so it never touches a name's descenders (p, g, y).
        canvas.Children.Add(At(Icons.TierTrophy(Snap(44 * u), tier), 243 * u, 64 * u));

        // The disc: glossy black, the mark in white; around it, past a white gap, the breathing blue ring.
        // Even pixel sizes round a whole-pixel centre: the ring, the disc and the mark have crisp edges.
        double ring = SnapEven(97 * u), disc = SnapEven(71 * u), cx = Snap(639 * u), cy = Snap(62.5 * u);
        var halo = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = ring, Height = ring, Stroke = Solid(0xFF, 0x7D, 0xCB, 0xFF), StrokeThickness = 5 * u, Opacity = 0 };
        canvas.Children.Add(At(halo, cx - ring / 2, cy - ring / 2));
        var mark = new Grid { Width = disc, Height = disc };
        mark.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Fill = new RadialGradientBrush
            {
                Center = new(0.5, 0.3), GradientOrigin = new(0.5, 0.18), RadiusX = 0.75, RadiusY = 0.75,
                GradientStops =
                {
                    new GradientStop { Color = black ? Color.FromArgb(255, 0x80, 0x80, 0x84) : Color.FromArgb(255, 0x5A, 0x5A, 0x5A), Offset = 0 },
                    new GradientStop { Color = black ? Color.FromArgb(255, 0x3A, 0x3A, 0x3E) : Color.FromArgb(255, 0x14, 0x14, 0x14), Offset = 0.55 },
                    new GradientStop { Color = black ? Color.FromArgb(255, 0x24, 0x24, 0x27) : Color.FromArgb(255, 0x00, 0x00, 0x00), Offset = 1 },
                },
            },
            // On the black bar the disc needs an edge to stand out.
            Stroke = black ? Solid(0x50, 0xFF, 0xFF, 0xFF) : null,
            StrokeThickness = black ? 1.5 * u : 0,
        });
        // Hybrid Launcher's own mark in white where the original has the console's: the controller H, its buttons cut through.
        mark.Children.Add(HlMark(SnapEven(disc * 0.8), Solid(Colors.White)));
        canvas.Children.Add(At(mark, cx - disc / 2, cy - disc / 2));
        bar.Children.Add(canvas);

        // Dark like every pop-up (a light root gives the see-through window a white background around the bar); the colours are its own.
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Width = (Ps4W + Ps4Pad) * u, Height = (Ps4H + Ps4Pad) * u };
        root.Children.Add(bar);
        // The ring breathes: faint as the bar arrives, brightest about 2 s in, gone again near 4 s, rising again as it leaves.
        void Pulse(double ms) => halo.Opacity = (1 - Math.Cos(2 * Math.PI * Math.Max(0, ms - 170) / 4000)) / 2;
        return new Built(root, null, null, null, bar, Pulse);
    }

    // Hybrid Launcher's mark (the controller H) on a 256 grid, as outlines with the D-pad and the four buttons cut through, so
    // whatever is under it shows in the holes (a disc's gloss, a picture): the legs, then the crossbar between them.
    const string HlLeft = "F0 M56.2,76.7 L42.2,184.7 A26,26 0 0 0 93.8,191.3 L107.8,83.3 A26,26 0 0 0 56.2,76.7 Z M75.2,71.5 H86.2 V84.5 H99.2 V95.5 H86.2 V108.5 H75.2 V95.5 H62.2 V84.5 H75.2 Z";
    const string HlRight = "F0 M148.2,83.3 L162.2,191.3 A26,26 0 0 0 213.8,184.7 L199.8,76.7 A26,26 0 0 0 148.2,83.3 Z M167.9,76.5 A7.4,7.4 0 1 0 182.7,76.5 A7.4,7.4 0 1 0 167.9,76.5 Z M181.4,90 A7.4,7.4 0 1 0 196.2,90 A7.4,7.4 0 1 0 181.4,90 Z M167.9,103.5 A7.4,7.4 0 1 0 182.7,103.5 A7.4,7.4 0 1 0 167.9,103.5 Z M154.4,90 A7.4,7.4 0 1 0 169.2,90 A7.4,7.4 0 1 0 154.4,90 Z";
    const string HlBar = "M98,99 H158 V137 H98 Z";

    /// <summary>Hybrid Launcher's mark in one colour, centred in a <paramref name="side"/> square (the mark fills about two thirds of it).</summary>
    static FrameworkElement HlMark(double side, Brush ink)
    {
        // Drawn at its own size (the geometry scaled, not a 256 px drawing shrunk by a Viewbox, which came out grainy), lifted
        // 6 units so it sits optically centred.
        double k = side / 256;
        var art = new Grid { Width = side, Height = side, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var d in new[] { HlLeft, HlRight, HlBar })
        {
            var g = PathData(d);
            g.Transform = new MatrixTransform { Matrix = new Matrix(k, 0, 0, k, 0, -6 * k) };
            art.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = g, Fill = ink });
        }
        return art;
    }

    /// <summary>A soft drop shadow under <paramref name="target"/> (a composition sprite of its size, painted <paramref name="fill"/>, behind its content).</summary>
    static void Shadow(FrameworkElement target, double w, double h, Color fill, double blur, double dx, double dy, float opacity)
    {
        try
        {
            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(target).Compositor;
            var shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = (float)blur;
            shadow.Offset = new System.Numerics.Vector3((float)dx, (float)dy, 0);
            shadow.Color = Color.FromArgb(255, 0, 0, 0);
            shadow.Opacity = opacity;
            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new System.Numerics.Vector2((float)w, (float)h);
            sprite.Brush = compositor.CreateColorBrush(fill);
            sprite.Shadow = shadow;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(target, sprite);
        }
        catch (Exception ex) { Log.Warn($"Pop-up shadow: {ex.Message}"); }
    }

    // ---------- Minimal ----------

    static Built BuildMinimal(Toast item, double size, Color accent, Color? background, double opacity)
    {
        var root = Shell(10 * size, Solid(WithAlpha(background is { } c ? Color.FromArgb(255, (byte)(c.R * 0.8), (byte)(c.G * 0.8), (byte)(c.B * 0.8)) : Color.FromArgb(255, 0x17, 0x16, 0x1C), opacity)), WithAlpha(accent, 0.5));
        var icon = new Border { Width = 34 * size, Height = 34 * size, CornerRadius = new CornerRadius(6 * size), Background = Solid(WithAlpha(accent, 0.25)), VerticalAlignment = VerticalAlignment.Center };
        icon.Child = IconPicture(item, 34 * size);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Name, FontSize = 14 * size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Solid(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        text.Children.Add(new TextBlock { Text = item.Game is { Length: > 0 } g ? g : L.T("Achievement unlocked"), FontSize = 11 * size, Foreground = Solid(0xAA, 0xFF, 0xFF, 0xFF), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var grid = new Grid { ColumnSpacing = 12 * size, Padding = new Thickness(10 * size, 9 * size, 16 * size, 9 * size) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        root.Children.Insert(0, grid);
        return new Built(root, icon, null);
    }

    // ---------- the card (Hybrid, Steam, GOG, Epic) ----------

    /// <summary>The card's fixed height (DIPs at size 1): the label, one line of name, two of description and the footer.</summary>
    const double CardH = 146;

    /// <summary>
    /// The card: the picture in a ring (with a check badge), the label, name, description and a small footer, a time line along the
    /// bottom. Hybrid, Steam, GOG and Epic are this with their own colours, corner and details.
    /// </summary>
    static Built BuildCard(Toast item, double size, bool showRarity, Color accent, Color? background, double opacity, double radius, string label, bool badgeAndBar)
    {
        var accentBrush = Solid(accent);
        var light = Color.FromArgb(0xFF, (byte)(accent.R + (255 - accent.R) / 2), (byte)(accent.G + (255 - accent.G) / 2), (byte)(accent.B + (255 - accent.B) / 2));

        var icon = new Border
        {
            Width = 64 * size,
            Height = 64 * size,
            CornerRadius = new CornerRadius(Math.Min(radius, 8) * size),
            Background = Solid(Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
            BorderBrush = Solid(Color.FromArgb(0xC0, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(badgeAndBar ? 1.5 : 0),
        };
        if (item.Completion && item.IconUrl is null)
        {
            icon.Background = null;
            icon.BorderThickness = new Thickness(0);
            icon.Child = Icons.PlatinumEmblem(64 * size);
        }
        else if (item.IconUrl is not null && Uri.TryCreate(item.IconUrl, UriKind.Absolute, out var uri))
            icon.Child = Smooth.Art(new BitmapImage { UriSource = uri, DecodePixelWidth = (int)(64 * size * 2) }, Smooth.Inner(icon), Stretch.UniformToFill);
        else
            icon.Child = new FontIcon { Glyph = "", FontSize = 28 * size, Foreground = Solid(light) };
        var iconBox = new Grid { VerticalAlignment = VerticalAlignment.Center };
        iconBox.Children.Add(icon);
        if (badgeAndBar)
            iconBox.Children.Add(new Border
            {
                Width = 22 * size,
                Height = 22 * size,
                CornerRadius = new CornerRadius(11 * size),
                Background = accentBrush,
                BorderBrush = Solid(0xFF, 0x12, 0x10, 0x18),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -6, -6),
                Child = new FontIcon { Glyph = "", FontSize = 10 * size, Foreground = Solid(Colors.White) },
            });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 10.5 * size, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 90, Foreground = Solid(light) });
        // A fixed size like every other look (it used to grow with the text, and a tall one could reach past the screen's edge):
        // the name on one line, the description on two at most, both ending in "…".
        text.Children.Add(new TextBlock { Text = item.Name, FontSize = 16 * size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Solid(Colors.White), TextWrapping = TextWrapping.NoWrap, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Description.Length > 0)
            text.Children.Add(new TextBlock { Text = item.Description, FontSize = 12.5 * size, Foreground = Solid(0xC8, 0xFF, 0xFF, 0xFF), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
        var foot = new List<string>();
        if (item.Game is { Length: > 0 } game) foot.Add(game);
        if (showRarity && item.Percent is { } pct) foot.Add(L.F("{0:0.#}% of players", pct) + (pct < 10 ? "  ·  " + L.T("rare") : ""));
        if (foot.Count > 0)
            text.Children.Add(new TextBlock { Text = string.Join("  ·  ", foot), FontSize = 11 * size, Margin = new Thickness(0, 2, 0, 0), Foreground = Solid(0x99, 0xFF, 0xFF, 0xFF), TextTrimming = TextTrimming.CharacterEllipsis });

        var grid = new Grid { ColumnSpacing = 16 * size, Padding = new Thickness(16 * size, 14 * size, 18 * size, 16 * size) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(iconBox);
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        var root = new Grid { RequestedTheme = ElementTheme.Dark, Height = CardH * size, CornerRadius = new CornerRadius(radius), Background = Back(accent, background, opacity) };
        root.Children.Add(grid);
        ScaleTransform? timeBar = null;
        if (badgeAndBar)
        {
            timeBar = new ScaleTransform { ScaleX = 1 };
            root.Children.Add(new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = accentBrush, RenderTransform = timeBar, Opacity = 0.9 });
        }
        // Its own outline: a thin accent edge, brighter at the top-left, fading round the corner.
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new(0, 0),
                EndPoint = new(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xB0, accent.R, accent.G, accent.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x30, accent.R, accent.G, accent.B), Offset = 0.6 },
                    new GradientStop { Color = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), Offset = 1 },
                },
            },
        });
        return new Built(root, iconBox, timeBar);
    }

    /// <summary>The background: the user's colour, or a dark tint of the accent; <paramref name="opacity"/> lets the game show through.</summary>
    static Brush Back(Color accent, Color? chosen, double opacity)
    {
        byte a = (byte)Math.Round(255 * opacity);
        if (chosen is { } c)
        {
            var darker = Color.FromArgb(a, (byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7));
            return new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(1, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(a, c.R, c.G, c.B), Offset = 0 }, new GradientStop { Color = darker, Offset = 1 } },
            };
        }
        return new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(a, (byte)(0x18 + accent.R / 12), (byte)(0x16 + accent.G / 12), (byte)(0x20 + accent.B / 12)), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(a, 0x0E, 0x0D, 0x12), Offset = 0.65 },
            },
        };
    }

    static Color? BackgroundColor(PopupSettings o, Color accent) => TryParseHex(o.Background, out var c) ? c : null;

    /// <summary>The pop-up's colour: the colour theme's accent (or the Windows one), gold, Steam blue, or the user's own.</summary>
    static Color AccentColor(PopupSettings o)
    {
        switch (o.Style)
        {
            case "Gold": return Color.FromArgb(255, 0xF2, 0xC2, 0x3A);
            case "Steam": return Color.FromArgb(255, 0x66, 0xC0, 0xF4);
            case "Custom" when TryParseHex(o.CustomColor, out var c): return c;
        }
        if (Palettes.Get(AppState.Settings.Palette).Accent is { } accent) return accent;
        try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2); }
        catch { return Color.FromArgb(255, 0x9A, 0x5C, 0xFF); }
    }

    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        hex = hex?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DwmBlurBehind { public uint Flags; public int Enable; public nint Region; public int Transition; }

    [DllImport("dwmapi.dll")]
    static extern int DwmEnableBlurBehindWindow(nint hwnd, ref DwmBlurBehind blur);

    [DllImport("gdi32.dll")]
    static extern nint CreateRectRgn(int l, int t, int r, int b);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(nint obj);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>DWMWA_CLOAK: the window is composed but not shown (or shown again), from the next DWM frame.</summary>
    static void Cloak(nint hwnd, bool on)
    {
        int value = on ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
    }

    [DllImport("user32.dll")]
    static extern nint GetForegroundWindow();

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    static extern long GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool PlaySoundW(string sound, nint module, uint flags);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    static extern bool PlaySoundPtr(nint sound, nint module, uint flags);
}

/// <summary>
/// A backdrop that is nothing: the window shows only what XAML draws on it (per-pixel alpha), so a toast has no rectangle behind it.
///
/// It must stay <c>partial</c>: the published app is trimmed, and CsWinRT only generates the native interface table of a C# class
/// derived from a WinRT type when the class is partial. Without it (1.3.1-rc.1 to rc.3) setting it threw "Specified cast is not
/// valid", the window kept its black backdrop (the dark box around and behind the toast), and WinUI crashed the app (0xC000027B).
/// Both callbacks come from native code: nothing may be thrown out of them, it would end the process the same way.
/// </summary>
sealed partial class TransparentBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
{
    static Windows.UI.Composition.Compositor? _compositor;

    /// <summary>Whether the last pop-up window really got the see-through backdrop (self-test).</summary>
    internal static bool LastConnected { get; private set; }

    internal static void Reset() => LastConnected = false;

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        try
        {
            _compositor ??= new Windows.UI.Composition.Compositor();
            target.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            LastConnected = true;
        }
        catch (Exception ex) { Log.Warn($"Transparent pop-up backdrop: {ex}"); }
    }

    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
    {
        try { target.SystemBackdrop = null; } catch { }
    }
}
