using System.Runtime.InteropServices;
using HybridLauncher.Core;
using HybridLauncher.Core.Steam;
using HybridLauncher.Helpers;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace HybridLauncher;

/// <summary>
/// Watches Steam's appcache\stats folder while Hybrid Launcher runs. Steam rewrites UserGameStats_&lt;account&gt;_&lt;appid&gt;.bin when
/// stats change; each rewrite is compared with the last known unlocked set and new unlocks become pop-ups.
/// </summary>
public static class AchievementWatcher
{
    /// <summary>An app id whose stats file changed. Always raised on the UI thread.</summary>
    public static event Action<string>? Changed;

    static FileSystemWatcher? _watcher;
    static DispatcherQueue? _ui;
    static readonly Dictionary<string, HashSet<string>> Known = new();
    static readonly Dictionary<string, DispatcherQueueTimer> Pending = new();

    public static void Start(DispatcherQueue ui)
    {
        try
        {
            var dir = SteamAchievements.StatsDir;
            if (dir is null || _watcher is not null) return;
            _ui = ui;

            // What is already unlocked is not news: remember it before listening.
            var account = SteamAchievements.CurrentAccountId();
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, account is null ? "UserGameStats_*.bin" : $"UserGameStats_{account}_*.bin"))
                    {
                        if (SteamAchievements.AppIdFromStatsFile(file) is not { } appId) continue;
                        var keys = SteamAchievements.ReadUnlocked(appId).Keys.ToHashSet();
                        lock (Known) Known.TryAdd(appId, keys);
                    }
                }
                catch (Exception ex) { Log.Warn($"Achievement snapshot: {ex.Message}"); }
            });

            _watcher = new FileSystemWatcher(dir, "UserGameStats_*.bin")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
            _watcher.Created += (_, e) => OnFileEvent(e.FullPath);
            _watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);
            _watcher.Error += (_, e) => Log.Warn($"Steam stats watcher: {e.GetException().Message}");
            _watcher.EnableRaisingEvents = true;
            Log.Info($"Watching Steam achievements in {dir}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not watch Steam achievements: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public static void Stop()
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
    }

    // Watcher thread: hop to the UI thread at once, everything else happens there.
    static void OnFileEvent(string path)
    {
        if (SteamAchievements.AppIdFromStatsFile(path) is not { } appId) return;
        _ui?.TryEnqueue(() => Debounce(appId));
    }

    // Steam writes the file in a few steps: act once it has been quiet for a moment.
    static void Debounce(string appId)
    {
        if (_ui is null) return;
        if (!Pending.TryGetValue(appId, out var timer))
        {
            timer = _ui.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1200);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => _ = CheckAsync(appId);
            Pending[appId] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    static async Task CheckAsync(string appId)
    {
        try
        {
            var (unlocked, schema) = await Task.Run(() => (SteamAchievements.ReadUnlocked(appId), SteamAchievements.Schema(appId)));
            var now = unlocked.Keys.ToHashSet();
            HashSet<string>? before;
            lock (Known)
            {
                Known.TryGetValue(appId, out before);
                Known[appId] = now;
            }
            Changed?.Invoke(appId);

            if (before is null || schema is null || !AppState.Settings.AchievementPopups) return;
            var fresh = schema.Where(a => now.Contains(a.Key) && !before.Contains(a.Key)).ToList();
            if (fresh.Count == 0) return;
            var game = AppState.Find($"steam:{appId}")?.Name;
            Log.Info($"New achievements in {game ?? appId}: {string.Join(", ", fresh.Select(a => a.ApiName))}");
            // A burst (another account signing in, a restored backup) is summed up rather than shown one by one.
            if (fresh.Count > 3)
            {
                AchievementPopup.Show(L.F("{0} achievements unlocked", fresh.Count), string.Join(", ", fresh.Take(4).Select(a => a.Name)) + "…", fresh[0].IconUrl, game);
                return;
            }
            // Rarity comes from Steam's public stats (cached): the pop-up never waits more than a moment for it.
            Dictionary<string, double> percents = [];
            if (AppState.Settings.Popup.ShowRarity)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    percents = await SteamAchievements.GlobalPercentagesAsync(appId, cts.Token);
                }
                catch (Exception ex) { Log.Warn($"Rarity for {appId}: {ex.Message}"); }
            }
            foreach (var a in fresh)
                AchievementPopup.Show(a.Name, a.Description, a.IconUrl, game, percents.TryGetValue(a.ApiName, out var p) ? p : null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Achievement check for {appId}: {ex.Message}");
        }
    }
}

/// <summary>
/// A Steam-like unlock toast: a small borderless window in a corner of the screen the game runs on, always on top,
/// shown without taking focus (so a game keeps it). One at a time; the rest wait in a queue.
/// Corner (or an exact spot), size, colours, opacity, duration, sound and rarity line come from Settings → Achievements.
/// </summary>
public static class AchievementPopup
{
    sealed record Item(string Name, string Description, string? IconUrl, string? Game, double? Percent);

    static readonly Queue<Item> Waiting = new();
    static Window? _current;

    public static void Show(string name, string description, string? iconUrl, string? game, double? percent = null)
    {
        Waiting.Enqueue(new Item(name, description, iconUrl, game, percent));
        if (_current is null) ShowNext();
    }

    static string SampleIcon => new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AchievementSample.png")).AbsoluteUri;

    static Item Sample => new("Perfect Blend", L.T("Saw what an unlock looks like. This one is only a test."), SampleIcon, "Game: The game", 12.4);

    /// <summary>A sample with the current settings; replaces one that is showing, so changes can be compared quickly.</summary>
    public static void ShowTest()
    {
        if (_current is { } shown) Finish(shown, next: false);
        Waiting.Clear();
        Show(Sample.Name, Sample.Description, Sample.IconUrl, Sample.Game, Sample.Percent);
    }

    public static void CloseAll()
    {
        Waiting.Clear();
        if (_current is { } shown) Finish(shown, next: false);
        if (_placing is { } placing) { try { placing.Close(); } catch { } _placing = null; }
        _current = null;
    }

    // ---------- placing it by hand ----------

    static Window? _placing;
    static Action<double, double>? _placed;

    /// <summary>The window of the pop-up on screen, or null (self-test pictures and checks).</summary>
    internal static nint? CurrentHandle => _current is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : null;
    internal static nint? PlacingHandle => _placing is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : null;

    /// <summary>
    /// A sample to drag to the exact spot (Settings → Achievements → Position → Exactly where I put it); every time it is let go
    /// the spot is saved and <paramref name="placed"/> hears it (0..1 across and down the screen). Its Done button closes it.
    /// </summary>
    public static void Place(Action<double, double> placed)
    {
        _placed = placed;
        if (_placing is not null) { MovePlacement(); return; }
        try { _placing = Open(Sample with { Description = L.T("Drag me to where unlocks should appear, then press Done."), Percent = null }, placing: true); }
        catch (Exception ex) { Log.Warn($"Pop-up placing failed: {ex.Message}"); _placing = null; }
    }

    /// <summary>The sliders moved: the sample follows them.</summary>
    public static void MovePlacement()
    {
        if (_placing is not { } w) return;
        var app = w.AppWindow;
        var (x, y) = Spot(AppState.Settings.Popup, TargetDisplay(AppState.Settings.Popup).WorkArea, app.Size.Width, app.Size.Height, Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(w)) / 96.0);
        app.Move(new PointInt32(x, y));
    }

    /// <summary>Where the pop-up goes on <paramref name="area"/>: a corner with a margin, or the exact spot.</summary>
    static (int X, int Y) Spot(PopupSettings o, RectInt32 area, int w, int h, double scale)
    {
        int margin = (int)(20 * (scale <= 0 ? 1 : scale));
        if (o.Corner == "Custom")
            return (area.X + (int)Math.Round(Math.Clamp(o.X, 0, 1) * Math.Max(0, area.Width - w)),
                    area.Y + (int)Math.Round(Math.Clamp(o.Y, 0, 1) * Math.Max(0, area.Height - h)));
        bool left = o.Corner.EndsWith("Left", StringComparison.Ordinal), top = o.Corner.StartsWith("Top", StringComparison.Ordinal);
        return (left ? area.X + margin : area.X + area.Width - w - margin, top ? area.Y + margin : area.Y + area.Height - h - margin);
    }

    static void ShowNext()
    {
        while (Waiting.TryDequeue(out var item))
        {
            try
            {
                _current = Open(item, placing: false);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Achievement pop-up failed: {ex.Message}");
                _current = null;
            }
        }
    }

    static Window Open(Item item, bool placing)
    {
        var o = AppState.Settings.Popup;
        double size = o.Size == "Large" ? 1.3 : 1.0;
        var accent = AccentColor(o);
        double seconds = Math.Clamp(o.Seconds, 2, 15);
        var root = Content(item, accent, BackgroundColor(o, accent), Math.Clamp(o.Opacity, 0.6, 1), size, o.ShowRarity, out var slide, out var timeBar);
        Button? done = null;
        if (placing)
        {
            // The sample being placed: a hint and a Done button over it, and it never leaves by itself.
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
        var window = new Window { Title = "Hybrid Launcher achievement", Content = root };

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
        // A plain pop-up window: no caption or sizing frame left over from the normal window (WinUI keeps a thin light
        // line of it along the top), a tool window (not in Alt+Tab) that never takes the focus from the game.
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const long WS_POPUP = 0x80000000L, WS_VISIBLE = 0x10000000L, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        _ = SetWindowLongPtr(hwnd, GWL_STYLE, unchecked((nint)(WS_POPUP | WS_VISIBLE)));
        _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | (placing ? 0 : WS_EX_NOACTIVATE)));
        _ = SetWindowPos(hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020); // NOSIZE | NOMOVE | NOACTIVATE | FRAMECHANGED
        int corner = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        // Windows 11 draws a thin light frame round borderless windows: none here, the pop-up draws its own accent outline.
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int)); // DWMWA_BORDER_COLOR

        var area = TargetDisplay(o).WorkArea;
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        double widthDip = 440 * size;
        int w = (int)(widthDip * scale);
        bool left = o.Corner == "Custom" ? o.X < 0.5 : o.Corner.EndsWith("Left", StringComparison.Ordinal);
        // The height follows the text (a long name or description wraps instead of being cut): measured before it shows.
        int Height()
        {
            root.Measure(new Windows.Foundation.Size(widthDip, double.PositiveInfinity));
            return (int)Math.Ceiling(Math.Max(96 * size, root.DesiredSize.Height) * scale);
        }
        int h = Height();
        var (x, y) = Spot(o, area, w, h, scale);
        app.MoveAndResize(new RectInt32(x, y, w, h));
        app.Show(placing);
        if (!placing) PlaySound(o);

        // Once laid out in the window (fonts loaded), fit the height again if it differs.
        root.Loaded += (_, _) =>
        {
            int h2 = Height();
            if (Math.Abs(h2 - h) <= 2) return;
            var (x2, y2) = Spot(o, area, w, h2, scale);
            app.MoveAndResize(new RectInt32(x2, y2, w, h2));
        };

        // Slide in from the near edge while the time line starts running down; fade out, then the next one.
        var sb = new Storyboard();
        sb.Children.Add(Anim(slide, "X", left ? -48 : 48, 0, 280));
        sb.Children.Add(Anim(root, "Opacity", 0, 1, 220));
        if (!placing)
        {
            var run = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromSeconds(seconds) };
            Storyboard.SetTarget(run, timeBar);
            Storyboard.SetTargetProperty(run, "ScaleX");
            sb.Children.Add(run);
        }
        sb.Begin();

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

        // Time up: fade out, then close. The close never waits on the fade alone: a pop-up over a full-screen game may not
        // be drawn, and its storyboard then never ends, which used to leave it on screen.
        void FadeOut()
        {
            try
            {
                var fade = new Storyboard();
                fade.Children.Add(Anim(root, "Opacity", 1, 0, 300));
                fade.Completed += (_, _) => Finish(window);
                fade.Begin();
            }
            catch { Finish(window); }
            After(window, TimeSpan.FromMilliseconds(600), () => Finish(window));
        }
        After(window, TimeSpan.FromSeconds(seconds), FadeOut);
        // The last guard: whatever happened to the timers and the fade, it is gone shortly after its time.
        var queue = window.DispatcherQueue;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds + 2)).ContinueWith(_ => queue.TryEnqueue(() => Finish(window)), TaskScheduler.Default);
        // A click sends it away at once.
        root.Tapped += (_, _) => Finish(window);
        return window;
    }

    static bool IsInside(DependencyObject? d, DependencyObject? target)
    {
        for (; d is not null && target is not null; d = VisualTreeHelper.GetParent(d))
            if (d == target) return true;
        return false;
    }

    static void After(Window window, TimeSpan delay, Action action) => Later.Run(window.DispatcherQueue, delay, action);

    static readonly HashSet<Window> Closed = [];

    static void Finish(Window window, bool next = true)
    {
        if (!Closed.Add(window)) return;
        try { window.Close(); } catch { }
        if (_current == window) _current = null;
        if (next && _current is null) ShowNext();
        // Forget it once its timers are surely done.
        var queue = window.DispatcherQueue;
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

    static void PlaySound(PopupSettings o)
    {
        try
        {
            const uint Async = 0x0001, NoDefault = 0x0002, Alias = 0x00010000, FileName = 0x00020000;
            if (o.Sound == "Windows") _ = PlaySoundW("Notification.Default", 0, Alias | Async | NoDefault);
            else if (o.Sound == "File" && o.SoundFile is { } f && File.Exists(f)) _ = PlaySoundW(f, 0, FileName | Async | NoDefault);
        }
        catch (Exception ex) { Log.Warn($"Pop-up sound: {ex.Message}"); }
    }

    static Grid Content(Item item, Windows.UI.Color accent, Windows.UI.Color? background, double opacity, double size, bool showRarity, out TranslateTransform slide, out ScaleTransform timeBar)
    {
        var accentBrush = new SolidColorBrush(accent);
        var light = Windows.UI.Color.FromArgb(0xFF, (byte)(accent.R + (255 - accent.R) / 2), (byte)(accent.G + (255 - accent.G) / 2), (byte)(accent.B + (255 - accent.B) / 2));

        // The icon in an accent ring, a small check badge on its corner.
        var icon = new Border
        {
            Width = 64 * size,
            Height = 64 * size,
            CornerRadius = new CornerRadius(8 * size),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1.5),
        };
        if (item.IconUrl is not null && Uri.TryCreate(item.IconUrl, UriKind.Absolute, out var uri))
            icon.Child = Smooth.Art(new BitmapImage { UriSource = uri, DecodePixelWidth = (int)(64 * size * 2) }, Smooth.Inner(icon), Stretch.UniformToFill);
        else
            icon.Child = new FontIcon { Glyph = "\uE734", FontSize = 28 * size, Foreground = new SolidColorBrush(light) };
        var badge = new Border
        {
            Width = 22 * size,
            Height = 22 * size,
            CornerRadius = new CornerRadius(11 * size),
            Background = accentBrush,
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x10, 0x18)),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -6, -6),
            Child = new FontIcon { Glyph = "\uE73E", FontSize = 10 * size, Foreground = new SolidColorBrush(Colors.White) },
        };
        var iconBox = new Grid { VerticalAlignment = VerticalAlignment.Center };
        iconBox.Children.Add(icon);
        iconBox.Children.Add(badge);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = L.T("ACHIEVEMENT UNLOCKED"),
            FontSize = 10.5 * size,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 90,
            Foreground = new SolidColorBrush(light),
        });
        text.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 16 * size,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.WordEllipsis,
        });
        if (item.Description.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 12.5 * size,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.WordEllipsis,
            });
        var foot = new List<string>();
        if (item.Game is { Length: > 0 } game) foot.Add(game);
        if (showRarity && item.Percent is { } pct) foot.Add(L.F("{0:0.#}% of players", pct) + (pct < 10 ? "  ·  " + L.T("rare") : ""));
        if (foot.Count > 0)
            text.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", foot),
                FontSize = 11 * size,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

        var grid = new Grid { ColumnSpacing = 16 * size, Padding = new Thickness(16 * size, 14 * size, 18 * size, 16 * size) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(iconBox);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        slide = new TranslateTransform();
        timeBar = new ScaleTransform { ScaleX = 1 };
        // The time left, as a thin accent line along the bottom edge.
        var bar = new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = accentBrush, RenderTransform = timeBar, Opacity = 0.9 };
        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            CornerRadius = new CornerRadius(8),
            Background = Back(accent, background, opacity),
            RenderTransform = slide,
            Opacity = 0,
        };
        root.Children.Add(grid);
        root.Children.Add(bar);
        // Its own outline instead of Windows' light frame: a thin accent edge, brighter at the top-left, fading round the corner.
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new(0, 0),
                EndPoint = new(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0xB0, accent.R, accent.G, accent.B), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x30, accent.R, accent.G, accent.B), Offset = 0.6 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), Offset = 1 },
                },
            },
        });
        return root;
    }

    /// <summary>The background: the user's colour, or a dark tint of the accent; <paramref name="opacity"/> lets the game show through.</summary>
    static Brush Back(Windows.UI.Color accent, Windows.UI.Color? chosen, double opacity)
    {
        byte a = (byte)Math.Round(255 * opacity);
        if (chosen is { } c)
        {
            var darker = Windows.UI.Color.FromArgb(a, (byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7));
            return new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(1, 1),
                GradientStops = { new GradientStop { Color = Windows.UI.Color.FromArgb(a, c.R, c.G, c.B), Offset = 0 }, new GradientStop { Color = darker, Offset = 1 } },
            };
        }
        return new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(a, (byte)(0x18 + accent.R / 12), (byte)(0x16 + accent.G / 12), (byte)(0x20 + accent.B / 12)), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(a, 0x0E, 0x0D, 0x12), Offset = 0.65 },
            },
        };
    }

    static Windows.UI.Color? BackgroundColor(PopupSettings o, Windows.UI.Color accent) => TryParseHex(o.Background, out var c) ? c : null;

    /// <summary>The pop-up's colour: the colour theme's accent (or the Windows one), gold, Steam blue, or the user's own.</summary>
    static Windows.UI.Color AccentColor(PopupSettings o)
    {
        switch (o.Style)
        {
            case "Gold": return Windows.UI.Color.FromArgb(255, 0xF2, 0xC2, 0x3A);
            case "Steam": return Windows.UI.Color.FromArgb(255, 0x66, 0xC0, 0xF4);
            case "Custom" when TryParseHex(o.CustomColor, out var c): return c;
        }
        if (Palettes.Get(AppState.Settings.Palette).Accent is { } accent) return accent;
        try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2); }
        catch { return Windows.UI.Color.FromArgb(255, 0x9A, 0x5C, 0xFF); }
    }

    public static bool TryParseHex(string? hex, out Windows.UI.Color color)
    {
        color = default;
        hex = hex?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    static DoubleAnimation Anim(DependencyObject target, string property, double from, double to, int ms)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        return a;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern nint GetForegroundWindow();

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
}
