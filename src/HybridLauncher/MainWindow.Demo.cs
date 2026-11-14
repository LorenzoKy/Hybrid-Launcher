using System.Text.Json;
using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher;

/// <summary>
/// The demo walk (2.0.0-rc.13, a tutorial since rc.15), for the video and the site's pictures: with <c>--selftest</c> and
/// HL_DEMO=1 the app walks through itself on the games it found, slowly and smoothly, while the screen is recorded from
/// outside. Each step is written to the log for the video tool:
/// <list type="bullet">
/// <item>"Demo scene &lt;name&gt; at &lt;s&gt; s": a step starts (the narration's cue).</item>
/// <item>"Demo look &lt;name&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; at &lt;s&gt; s": the part of the screen the step is about, in
/// screen pixels (the camera moves there); "Demo look wide" sends it back to the whole screen.</item>
/// <item>"Demo click &lt;name&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; at &lt;s&gt; s": the moment a control is used (the drawn pointer
/// clicks there). The walk waits a moment before each use, so the pointer has time to travel.</item>
/// </list>
/// It installs only when HL_DEMO_INSTALL names one of the games (OptiScaler, then uninstalled with its backup in the same
/// walk); otherwise it only looks. HL_DEMO_GAMES="Show|Mod" picks the game shown most and the game the mods go on.
/// HL_DEMO_PACE=&lt;json&gt; ({"scene": seconds}) makes a scene last at least that long (the narration's length).
/// HL_SHOTS=&lt;folder&gt; also saves a picture at each settled moment.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Demo walk only: confirmations on game pages are answered yes after this many seconds (null: never).</summary>
    internal static double? DemoAnswerAfter;

    readonly System.Diagnostics.Stopwatch _demoClock = new();

    void DemoLog(string kind, string name, FrameworkElement? el = null)
    {
        string where = "";
        if (el is { XamlRoot: not null, ActualWidth: > 0 })
        {
            try
            {
                var r = el.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, el.ActualWidth, el.ActualHeight));
                double scale = GetDpiScale();
                var origin = new POINT();
                _ = ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin);
                where = FormattableString.Invariant($" {origin.X + r.X * scale:F0} {origin.Y + r.Y * scale:F0} {r.Width * scale:F0} {r.Height * scale:F0}");
            }
            catch (Exception ex) { Log.Warn($"Demo {kind} {name}: {ex.Message}"); }
        }
        Log.Info(FormattableString.Invariant($"Demo {kind} {name}{where} at {_demoClock.Elapsed.TotalSeconds:F2} s"));
    }

    /// <summary>The demo walk answers a game page's confirmation: it stays readable a moment, then the pointer presses yes.</summary>
    internal async Task DemoAnswerAsync(ContentDialog dialog, double after, Action yes)
    {
        await Task.Delay(TimeSpan.FromSeconds(after));
        FrameworkElement? button = null;
        if (dialog.XamlRoot is { } root)
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.Child is { } child && (button = FindNamed(child, "PrimaryButton")) is not null) break;
        DemoLog("click", "dialog-yes", button);
        yes();
        dialog.Hide();
    }

    /// <summary>The first button showing one of these texts (a demo walk looks for controls made in code by their label).</summary>
    static Button? FindButton(DependencyObject root, params string[] texts)
    {
        if (root is Button { Content: string c } b && texts.Contains(c)) return b;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i), texts) is { } found) return found;
        return null;
    }

    static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        if (root is FrameworkElement { Name: var n } fe && n == name) return fe;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindNamed(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }

    internal async Task<int> RunDemoAsync()
    {
        var shots = Environment.GetEnvironmentVariable("HL_SHOTS");
        int shotNo = 0;
        _demoClock.Restart();
        var pace = new Dictionary<string, double>();
        if (Environment.GetEnvironmentVariable("HL_DEMO_PACE") is { Length: > 0 } paceFile && File.Exists(paceFile))
        {
            // JsonDocument, not a typed Deserialize: the published app is trimmed.
            using var doc = JsonDocument.Parse(File.ReadAllText(paceFile));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) pace[p.Name] = p.Value.GetDouble();
        }
        string? scene = null;
        double sceneAt = 0;

        async Task Scene(string name)
        {
            // The step before lasts at least as long as its narration.
            if (scene is not null && pace.TryGetValue(scene, out var need))
            {
                double left = need - (_demoClock.Elapsed.TotalSeconds - sceneAt);
                if (left > 0) await Wait(left);
            }
            scene = name;
            sceneAt = _demoClock.Elapsed.TotalSeconds;
            DemoLog("scene", name);
        }
        async Task Shot(string name)
        {
            if (!string.IsNullOrWhiteSpace(shots)) await SaveShotAsync(Path.Combine(shots, $"{++shotNo:00}-{name}.png"));
        }
        void Look(string name, FrameworkElement? el) => DemoLog("look", el is null ? "wide" : name, el);
        void Wide() => DemoLog("look", "wide");
        // The pointer travels for a moment, then the control is used.
        async Task Click(string name, FrameworkElement? el, Action act, double travel = 1.1)
        {
            await Wait(travel);
            DemoLog("click", name, el);
            act();
        }
        static Task Wait(double s) => Task.Delay(TimeSpan.FromSeconds(s));
        async Task Until(Func<bool> done, double max)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && watch.Elapsed.TotalSeconds < max) await Task.Delay(100);
        }

        var wanted = (Environment.GetEnvironmentVariable("HL_DEMO_GAMES") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var games = wanted.Select(n => AppState.VisibleGames.FirstOrDefault(g => g.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Where(g => g is not null).Select(g => g!).ToList();
        foreach (var g in AppState.VisibleGames.Where(g => !g.NotInstalled))
            if (games.Count < 2 && !games.Contains(g)) games.Add(g);
        if (games.Count == 0) { Log.Warn("Demo: no games"); return 1; }
        var show = games[0];
        var mod = games.Count > 1 ? games[1] : games[0];
        bool install = Environment.GetEnvironmentVariable("HL_DEMO_INSTALL") is { Length: > 0 } installName
                       && installName.Equals(mod.Name, StringComparison.OrdinalIgnoreCase);
        GamePage Page() => Shown as GamePage ?? throw new InvalidOperationException("no game page");
        SettingsPage Settings() => Shown as SettingsPage ?? throw new InvalidOperationException("no Settings page");
        async Task GlideTo(ScrollView sv, FrameworkElement? el, double seconds, double margin = 110)
        {
            if (el is null) return;
            var y = el.TransformToVisual(sv).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            await Glide(sv, sv.VerticalOffset + y - margin, seconds);
        }
        static void Pick(ComboBox combo, string tag)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == tag);
            if (item is not null) combo.SelectedItem = item;
        }

        // No hand on the mouse during a recording: the frame clock is kept awake as if there were one, else still moments and
        // pop-ups would be drawn at the idle rate on camera.
        var awake = DispatcherQueue.CreateTimer();
        awake.Interval = TimeSpan.FromMilliseconds(500);
        awake.Tick += (_, _) => FrameWarmth.Poke();
        awake.Start();
        Log.Info($"Demo start {DateTime.Now:HH:mm:ss.fff}: {show.Name} shown, {mod.Name} for mods{(install ? " (installs OptiScaler)" : "")}");
        var look0 = (AppState.Settings.Palette, AppState.Settings.Theme);
        try
        {
            // Let the first pictures and store data arrive while the library is still.
            ContentFrame.Navigate(typeof(LibraryPage), "");
            await Wait(8);

            // 1. The library
            await Scene("library");
            Wide();
            await Shot("library");
            if (Shown is LibraryPage lib)
            {
                await Wait(2.5);
                await Glide(lib.SelfTestScroller, 1100, 7);
                await Wait(1.5);
                await Glide(lib.SelfTestScroller, 0, 4);
            }
            await Wait(1);

            // 2. A game's page
            await Scene("dashboard");
            OpenGame(show.Id);
            await Wait(3.5);
            await Shot("dashboard");
            Look("hero", Page().DemoPart("LaunchBar"));
            await Wait(3.5);
            Wide();
            await Glide(Page().SelfTestScroller, Page().DemoTabTop() + 1500, 9);
            await Wait(1.5);
            await Glide(Page().SelfTestScroller, 0, 3.5);

            // 3. Achievements
            await Scene("achievements");
            await Click("tab-achievements", Page().DemoTabButton("ach"), () => Page().DemoShow("ach"), 0.6);
            await Wait(2.5);
            await Shot("achievements");
            Look("ach-summary", Page().DemoPart("AchSummary"));
            await Wait(4);
            Wide();
            await Glide(Page().SelfTestScroller, Page().DemoTabTop() + 1100, 7);
            await Wait(1);

            // 4. The pop-ups, with this game's rarest unlock
            await Scene("popups");
            foreach (var look in new[] { "PlayStation", "Xbox", "Steam", "PlayStation4Black", "Hybrid" })
            {
                AchievementPopup.ShowReplacing(Page().DemoToast(look) ?? new Toast("Perfect Blend", "An unlock, as it shows over a game.", null, show.Name, 4.2, Look: look));
                if (look == "PlayStation") { await Wait(2.2); await Shot("popup"); await Wait(2.3); }
                else await Wait(4.3);
            }
            await Wait(1.5);

            // 5. Pop-up settings
            await Scene("popup-settings");
            OpenSettings("achievements");
            await Wait(3);
            await Shot("popup-settings");
            var sp = Settings();
            if (sp.FindName("LookCombo") is ComboBox lookCombo)
            {
                await GlideTo(sp.SelfTestScroller, lookCombo, 2.5, 260);
                Look("look-combo", lookCombo);
                await Click("look-combo", lookCombo, () => lookCombo.IsDropDownOpen = true);
                await Wait(2);
                lookCombo.IsDropDownOpen = false;
                Pick(lookCombo, "PlayStation");
                await Wait(3.5);
            }
            if (sp.FindName("AchievementSoundsPanel") is FrameworkElement sounds)
            {
                Wide();
                await GlideTo(sp.SelfTestScroller, sounds, 4, 200);
                Look("sounds", sounds);
                await Wait(4);
            }
            if (sp.FindName("TryPopupButton") is Button tryIt)
            {
                Wide();
                await GlideTo(sp.SelfTestScroller, tryIt, 3, 300);
                await Click("try-popup", tryIt, () => { try { sp.SelfTestTryPopup(); } catch (Exception ex) { Log.Warn($"Demo: {ex.Message}"); } });
                await Wait(4.5);
            }
            Wide();

            // 6. Colours
            await Scene("themes");
            OpenSettings("appearance");
            await Wait(3);
            await Shot("appearance");
            if (Settings().FindName("PaletteCombo") is ComboBox palettes)
            {
                Look("palette", palettes);
                foreach (var palette in new[] { "TotalDark", "Gray", "Dark" })
                {
                    await Click("palette", palettes, () => palettes.IsDropDownOpen = true, 0.9);
                    await Wait(1.2);
                    palettes.IsDropDownOpen = false;
                    Pick(palettes, palette);
                    await Wait(2.4);
                }
                Wide();
                await Wait(1);
                OpenGame(show.Id);
                await Wait(4);
                await Shot("themed-game");
                AppState.Settings.Palette = look0.Palette;
                ApplyTheme(look0.Theme);
                await Wait(1.5);
            }

            // 7. OptiScaler on the mod game
            await Scene("optiscaler");
            OpenGame(mod.Id);
            await Wait(3);
            await Click("tab-opti", Page().DemoTabButton("opti"), () => Page().DemoShow("opti"), 0.6);
            await Wait(3);
            await Shot("optiscaler");
            Look("sources", Page().DemoPart("ChannelCards"));
            await Click("nightly", Page().DemoPart("NightlyCard"), () => Page().DemoChannel(OptiChannel.Nightly));
            await Wait(2.2);
            await Click("fork", Page().DemoPart("ForkCard"), () => Page().DemoChannel(OptiChannel.Fork));
            await Wait(2.2);
            await Click("stable", Page().DemoPart("StableCard"), () => Page().DemoChannel(OptiChannel.Stable));
            await Wait(1.5);

            await Scene("setup");
            Wide();
            await GlideTo(Page().SelfTestScroller, Page().DemoPart("SetupExpander"), 2.5, 160);
            await Click("setup-options", Page().DemoPart("SetupExpander"), () => Page().DemoSetup(true));
            await Wait(1.2);
            Look("answers", Page().DemoPart("SetupExpander"));
            await Wait(5);

            if (install)
            {
                await Scene("install");
                Wide();
                await GlideTo(Page().SelfTestScroller, Page().DemoPart("ActionRow"), 2, 320);
                DemoAnswerAfter = 3.2;
                await Click("install", Page().DemoPart("InstallButton"), () => Page().DemoInstallOpti());
                await Wait(1.5);
                Look("progress", Page().DemoPart("OptiCard"));
                await Until(() => !Page().DemoBusy, 180);
                await Wait(1.5);
                // To the header itself (2.0.0-rc.18): the tab's top left it below the screen's edge (look at y ~1100 of 1392).
                await GlideTo(Page().SelfTestScroller, Page().DemoPart("OptiHeader"), 2, 260);
                Look("installed", Page().DemoPart("OptiHeader"));
                await Wait(3);
                await Shot("installed");
                Wide();
                await GlideTo(Page().SelfTestScroller, Page().DemoPart("QuickPanelCard"), 4);
                Look("quick-panel", Page().DemoPart("QuickPanelCard"));
                await Wait(4.5);
                Wide();
                await GlideTo(Page().SelfTestScroller, Page().DemoPart("IniCard"), 3.5);
                Look("ini", Page().DemoPart("IniCard"));
                await Wait(4.5);
                Wide();
            }

            // 8. The Mods tab
            await Scene("mods");
            await Glide(Page().SelfTestScroller, 0, 2);
            await Click("tab-mods", Page().DemoTabButton("mods"), () => Page().DemoShow("mods"), 0.6);
            await Wait(3.5);
            await Shot("mods");
            Look("mods-table", Page().DemoPart("ModsOverviewHost"));
            await Wait(4);
            Wide();

            // 9. DLSS 5
            await Scene("dlss5");
            Page().DemoShow("mods/dlss5");
            await Wait(3.5);
            await Shot("dlss5");
            await Glide(Page().SelfTestScroller, Page().SelfTestScroller.VerticalOffset + 900, 7);
            await Wait(2);

            // 10. Back as it was
            if (install && Page().DemoOptiInstalled)
            {
                await Scene("uninstall");
                await Glide(Page().SelfTestScroller, 0, 2);
                Page().DemoShow("opti");
                await Wait(2);
                await GlideTo(Page().SelfTestScroller, Page().DemoPart("ActionRow"), 2, 320);
                DemoAnswerAfter = 2.8;
                await Click("uninstall", Page().DemoPart("UninstallButton"), () => Page().DemoUninstallOpti());
                await Wait(1);
                await Until(() => !Page().DemoBusy && !Page().DemoOptiInstalled, 60);
                await GlideTo(Page().SelfTestScroller, Page().DemoPart("OptiHeader"), 2, 260);
                Look("removed", Page().DemoPart("OptiHeader"));
                await Wait(3.5);
                Wide();
            }

            // 11. Auto (experimental)
            await Scene("auto");
            await Glide(Page().SelfTestScroller, 0, 1.5);
            await Click("tab-auto", Page().DemoTabButton("auto"), () => Page().DemoShow("auto"), 0.6);
            await Wait(4);
            await Shot("auto");
            // The DLSS 5 card's model file, chosen right from the Auto tab (2.0.0-rc.15).
            if (FindButton(Page(), L.T("Choose file…"), L.T("Change…")) is { } modelButton)
            {
                Wide();
                await GlideTo(Page().SelfTestScroller, modelButton, 4, 420);
                Look("auto-dlss5", (modelButton.Parent as FrameworkElement)?.Parent as FrameworkElement ?? modelButton);
                await Wait(1.2);
                DemoLog("click", "hover-model", modelButton);
                await Wait(4);
                Wide();
            }
            else
            {
                await Glide(Page().SelfTestScroller, Page().DemoTabTop() + 450, 4);
                await Wait(2);
            }

            await Scene("end");
            ContentFrame.Navigate(typeof(LibraryPage), "");
            await Wait(5);
            await Scene("done");
            Log.Info("Demo finished");
            return 0;
        }
        finally
        {
            DemoAnswerAfter = null;
            AppState.Settings.Palette = look0.Palette;
            awake.Stop();
        }
    }

    /// <summary>A slow, eased scroll for the recording: one step per frame of the display, nothing jumps.</summary>
    static Task Glide(ScrollView sv, double to, double seconds)
    {
        var done = new TaskCompletionSource();
        double from = sv.VerticalOffset;
        to = Math.Clamp(to, 0, Math.Max(0, sv.ScrollableHeight));
        if (Math.Abs(to - from) < 1) { done.SetResult(); return done.Task; }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        EventHandler<object>? step = null;
        step = (_, _) =>
        {
            double t = Math.Min(1, watch.Elapsed.TotalSeconds / seconds);
            // Ease in and out (smootherstep): the movement starts and stops without a jolt.
            double e = t * t * t * (t * (t * 6 - 15) + 10);
            try { sv.ScrollTo(sv.HorizontalOffset, from + (to - from) * e, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)); }
            catch (Exception ex) { Log.Warn($"Demo glide: {ex.Message}"); t = 1; }
            if (t >= 1)
            {
                CompositionTarget.Rendering -= step;
                done.TrySetResult();
            }
        };
        CompositionTarget.Rendering += step;
        return done.Task;
    }
}
