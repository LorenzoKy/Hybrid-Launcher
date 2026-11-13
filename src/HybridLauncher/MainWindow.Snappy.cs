using System.Diagnostics;
using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher;

/// <summary>
/// Self-test "snappy" (2.0.0-rc.4): how quick the app feels, measured the same way on every version and with no input of any
/// kind (no mouse, no keys: it can run while the PC is in use, and gives the same numbers run after run). It opens games from
/// the list in code (first visit, then a return to each), every tab of a game, the library and Settings, and scrolls the long
/// pages. Each action is timed from the call to the first frame that shows it, then watched for a second and a half: every
/// frame the UI thread missed (a gap of more than 1.5 refreshes) is counted, and the time lost in them is the "jank".
/// Each kind of action has a budget; the log ends with "Snappy verdict: PASS" or "FAIL (n over budget)", and a failure
/// counts as a self-test failure. Run it with Tools\progetti\mod-manager\run-perf.ps1 -Only snappy on a copy of real data.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>What one action cost: first frame after the call, the UI thread's own time in it, frames missed after it.</summary>
    internal sealed record SnapResult(string Kind, string What, double FirstMs, double BusyMs, int Missed, double WorstMs, double JankMs, double SettledMs)
    {
        public override string ToString() =>
            $"{What}: first frame {FirstMs:F0} ms (busy {BusyMs:F0}), {Missed} missed, worst gap {WorstMs:F0} ms, jank {JankMs:F0} ms, settled {SettledMs:F0} ms";
    }

    /// <summary>Budgets per kind of action: first frame and jank (ms). Kept tight: every version must meet them.</summary>
    static readonly Dictionary<string, (double First, double Jank)> SnapBudgets = new()
    {
        ["return"] = (50, 80),     // back to a game shown before: shown as it was left
        ["prepared"] = (50, 80),   // a game whose page was prepared while the window was idle
        ["first"] = (300, 450),    // a game never shown in this session
        ["tab"] = (150, 200),      // a tab of the open game
        ["page"] = (150, 200),     // the library, Settings
        ["scroll"] = (34, 60),     // an animated scroll down a long page (first = worst gap)
    };

    internal async Task<int> SelfTestSnappyAsync()
    {
        UiPerf.EnableDetailed();
        // Pages are prepared in the quiet moments between actions whatever the PC's own mouse and keys do, and never while one
        // is measured (SnapAsync pauses them).
        PrepareIgnoresInput = true;
        // The start's own work (scan, art, stats, first warm-up) is not what is measured.
        await Task.Delay(5000);
        double hz = RefreshHz();
        double period = 1000 / Math.Max(30, hz);
        var results = new List<SnapResult>();
        var games = AppState.VisibleGames.Where(g => !g.NotInstalled).Take(6).ToList();
        Log.Info($"Snappy: {games.Count} games, display {hz:F0} Hz");
        try
        {
            ContentFrame.Navigate(typeof(LibraryPage), "");
            await Task.Delay(2500);
            // First visits, then a return to each (the order a hand goes back and forth in).
            foreach (var g in games) results.Add(await SnapAsync("first", "open " + g.Name, () => OpenGame(g.Id), period, 1500));
            foreach (var g in games) results.Add(await SnapAsync("return", "back to " + g.Name, () => OpenGame(g.Id), period, 1500));
            // Games never opened, whose pages were prepared while nobody used the window (MainWindow.GameSlots).
            PreparingPaused = false;
            StartPreparingPages();
            await Task.Delay(16000);
            var visited = games.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ReadyGamePages.Where(id => !visited.Contains(id)).Take(3).ToList())
                results.Add(await SnapAsync("prepared", "open (prepared) " + (AppState.Find(id)?.Name ?? id), () => OpenGame(id), period, 1500));
            // Every tab of the game now open, as a click opens it.
            if (Shown is GamePage page)
            {
                await Task.Delay(2000);
                foreach (var tab in page.SelfTestTabIds())
                    results.Add(await SnapAsync("tab", "tab " + tab, () => page.SelfTestShowTab(tab), period, 1200));
                page.SelfTestShowTab("overview");
                await Task.Delay(800);
                results.Add(await SnapScrollAsync("scroll game page", page.SelfTestScroller, period));
            }
            // The library (the logo's way), its scroll, Settings, and back to a game.
            results.Add(await SnapAsync("page", "library", () => { ContentFrame.Navigate(typeof(LibraryPage), _filter); SyncSelection(); }, period, 1500));
            if (Shown is LibraryPage lib) results.Add(await SnapScrollAsync("scroll library", lib.SelfTestScroller, period));
            results.Add(await SnapAsync("return", "game from the library", () => OpenGame(games[0].Id), period, 1500));
            results.Add(await SnapAsync("page", "library after a game", () => { ContentFrame.Navigate(typeof(LibraryPage), _filter); SyncSelection(); }, period, 1500));
            results.Add(await SnapAsync("page", "settings", () => OpenSettings("appearance"), period, 1500));
            if (Shown is SettingsPage sp) results.Add(await SnapScrollAsync("scroll settings", sp.SelfTestScroller, period));
        }
        catch (Exception ex)
        {
            Log.Error("Self-test FAILED: snappy", ex);
            return 1;
        }

        PreparingPaused = false;
        PrepareIgnoresInput = false;
        int over = 0;
        foreach (var r in results)
        {
            var (first, jank) = SnapBudgets[r.Kind];
            bool ok = r.FirstMs <= first && r.JankMs <= jank;
            if (!ok) over++;
            Log.Info($"Snappy {(ok ? "ok  " : "SLOW")} [{r.Kind}] {r}  (budget {first:F0} / {jank:F0})");
        }
        foreach (var kind in results.Select(r => r.Kind).Distinct())
        {
            var of = results.Where(r => r.Kind == kind).ToList();
            Log.Info($"Snappy summary [{kind}]: first frame median {Median(of.Select(r => r.FirstMs)):F0} ms, worst {of.Max(r => r.FirstMs):F0} ms; jank median {Median(of.Select(r => r.JankMs)):F0} ms, worst {of.Max(r => r.JankMs):F0} ms");
        }
        using (var me = Process.GetCurrentProcess())
            Log.Info($"Snappy memory: {me.PrivateMemorySize64 / (1024 * 1024)} MB private, {me.WorkingSet64 / (1024 * 1024)} MB working set, {GameHost.Children.Count} game pages alive");
        Log.Info(over == 0 ? $"Snappy verdict: PASS ({results.Count} actions)" : $"Snappy verdict: FAIL ({over} of {results.Count} over budget)");
        return over == 0 ? 0 : 1;
    }

    static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>Runs <paramref name="action"/> and watches the frames for <paramref name="watchMs"/>.</summary>
    async Task<SnapResult> SnapAsync(string kind, string what, Action action, double period, int watchMs)
    {
        // Quiet before: what is left of the last action does not land on this one, and no page is being prepared meanwhile.
        PreparingPaused = true;
        await Task.Delay(400);
        var ticks = new List<long>(512);
        EventHandler<object> onFrame = (_, _) => ticks.Add(Stopwatch.GetTimestamp());
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;
        // The frame clock kept at the display's rate, as a hand on the mouse keeps it (FrameWarmth): without input it slows to
        // about 93 ms a frame after half a second, which would read as missed frames that no user ever sees.
        var warm = KeepClockAwake();
        try
        {
            await Task.Delay(100);
            long t0 = Stopwatch.GetTimestamp();
            action();
            long t1 = Stopwatch.GetTimestamp();
            await Task.Delay(watchMs);
            return Analyse(kind, what, ticks, t0, t1, period);
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame;
            warm.Stop();
        }
    }

    /// <summary>An animated scroll to the bottom of <paramref name="sv"/> and back: frames missed while it moves.</summary>
    async Task<SnapResult> SnapScrollAsync(string what, ScrollView? sv, double period)
    {
        if (sv is null || sv.ScrollableHeight < 300) return new SnapResult("scroll", what + " (nothing to scroll)", 0, 0, 0, 0, 0, 0);
        await Task.Delay(400);
        var ticks = new List<long>(1024);
        EventHandler<object> onFrame = (_, _) => ticks.Add(Stopwatch.GetTimestamp());
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;
        // The frame clock kept at the display's rate, as a hand on the mouse keeps it (FrameWarmth): without input it slows to
        // about 93 ms a frame after half a second, which would read as missed frames that no user ever sees.
        var warm = KeepClockAwake();
        try
        {
            await Task.Delay(100);
            long t0 = Stopwatch.GetTimestamp();
            sv.ScrollTo(0, Math.Min(sv.ScrollableHeight, 4000), new ScrollingScrollOptions(ScrollingAnimationMode.Enabled));
            await Task.Delay(1400);
            sv.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Enabled));
            await Task.Delay(1400);
            var r = Analyse("scroll", what, ticks, t0, t0, period);
            // For a scroll the worst gap is what is budgeted as "first".
            return r with { FirstMs = r.WorstMs };
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame;
            warm.Stop();
        }
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer KeepClockAwake()
    {
        FrameWarmth.Poke();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.Tick += (_, _) => FrameWarmth.Poke();
        timer.Start();
        return timer;
    }

    static SnapResult Analyse(string kind, string what, List<long> ticks, long t0, long t1, double period)
    {
        double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
        double busy = Ms(t0, t1);
        var after = ticks.Where(t => t >= t1).ToList();
        double first = after.Count > 0 ? Ms(t0, after[0]) : -1;
        int missed = 0;
        double worst = 0, jank = 0, settled = first;
        for (int i = 1; i < after.Count; i++)
        {
            double gap = Ms(after[i - 1], after[i]);
            worst = Math.Max(worst, gap);
            if (gap > period * 1.5)
            {
                missed += (int)Math.Round(gap / period) - 1;
                jank += gap - period;
                settled = Ms(t0, after[i]);
            }
        }
        // The wait for the first frame (the call itself and the layout after it) is jank too.
        if (first > period * 1.5) jank += first - period;
        return new SnapResult(kind, what, first, busy, missed, worst, jank, settled);
    }
}
