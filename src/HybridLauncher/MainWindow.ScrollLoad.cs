using System.Diagnostics;
using HybridLauncher.Core;
using HybridLauncher.Views;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher;

/// <summary>
/// Self-test "scrollload" (2.1.4): how heavy each page is to scroll for the GPU, no mouse or keys. Every page is scrolled
/// back and forth without pause for a few seconds (animated, as the wheel glides), with a quiet stretch before it; the log gets
/// a "ScrollLoad begin/end" line with the wall-clock time of each stretch, so an outside sampler of the GPU counters (DWM and
/// the app) can tell the pages apart. The app's own frame clock is recorded too (missed frames while it moves).
/// </summary>
public sealed partial class MainWindow
{
    async Task<int> SelfTestScrollLoadAsync()
    {
        PrepareIgnoresInput = true;
        await Task.Delay(5000);
        double period = 1000 / Math.Max(30, RefreshHz());
        var game = AppState.VisibleGames.FirstOrDefault(g => !g.NotInstalled);
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("HL_SCROLLLOAD_SECONDS"), out var s) ? s : 6;
        try
        {
            ContentFrame.Navigate(typeof(LibraryPage), "");
            await Task.Delay(3000);
            await LoadStretch("idle library", null, seconds, period);
            if (Shown is LibraryPage lib) await LoadStretch("library", lib.SelfTestScroller, seconds, period);
            if (game is not null)
            {
                OpenGame(game.Id);
                await Task.Delay(3000);
                if (Shown is GamePage page)
                {
                    page.SelfTestShowTab("overview");
                    await Task.Delay(1500);
                    await LoadStretch("idle game home", null, seconds, period);
                    await LoadStretch("game home", page.SelfTestScroller, seconds, period);
                    foreach (var tab in page.SelfTestTabIds().Where(t => t is "mods" or "opti"))
                    {
                        page.SelfTestShowTab(tab);
                        await Task.Delay(1500);
                        await LoadStretch("game " + tab, page.SelfTestScroller, seconds, period);
                    }
                }
            }
            OpenSettings("appearance");
            await Task.Delay(2500);
            if (Shown is SettingsPage sp) await LoadStretch("settings", sp.SelfTestScroller, seconds, period);
        }
        catch (Exception ex)
        {
            Log.Error("Self-test FAILED: scrollload", ex);
            return 1;
        }
        PrepareIgnoresInput = false;
        return 0;
    }

    /// <summary>Scrolls <paramref name="sv"/> down and up for <paramref name="seconds"/> (nothing moves when null: a quiet stretch).</summary>
    async Task LoadStretch(string what, ScrollView? sv, int seconds, double period)
    {
        // HL_SCROLLLOAD_ONLY=<name>: only that stretch (the others are skipped), for an outside capture of the screen.
        if (Environment.GetEnvironmentVariable("HL_SCROLLLOAD_ONLY") is { Length: > 0 } only && !what.Equals(only, StringComparison.OrdinalIgnoreCase)) return;
        PreparingPaused = true;
        await Task.Delay(1500);
        var ticks = new List<long>(4096);
        EventHandler<object> onFrame = (_, _) => ticks.Add(Stopwatch.GetTimestamp());
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;
        var warm = KeepClockAwake();
        double span = sv is null ? 0 : Math.Min(sv.ScrollableHeight, double.TryParse(Environment.GetEnvironmentVariable("HL_SCROLLLOAD_SPAN"), out var max) ? max : 2400);
        Log.Info($"ScrollLoad begin {what} {DateTime.Now:HH:mm:ss.fff} (span {span:0} px)");
        var end = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        bool down = true;
        // HL_SCROLLLOAD_FROM=<px>: the top of the back and forth (a part of the page only).
        double from = double.TryParse(Environment.GetEnvironmentVariable("HL_SCROLLLOAD_FROM"), out var f) ? f : 0;
        if (sv is not null && from > 0) { sv.ScrollTo(0, from, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled)); await Task.Delay(500); }
        try
        {
            while (Stopwatch.GetTimestamp() < end)
            {
                if (sv is not null && span > 200) sv.ScrollTo(0, down ? span : from, new ScrollingScrollOptions(ScrollingAnimationMode.Enabled));
                down = !down;
                await Task.Delay(900);
            }
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame;
            warm.Stop();
        }
        Log.Info($"ScrollLoad end {what} {DateTime.Now:HH:mm:ss.fff}");
        int missed = 0;
        for (int i = 1; i < ticks.Count; i++)
        {
            double gap = (ticks[i] - ticks[i - 1]) * 1000.0 / Stopwatch.Frequency;
            if (gap > period * 1.5) missed += (int)Math.Round(gap / period) - 1;
        }
        Log.Info($"ScrollLoad frames {what}: {ticks.Count} UI frames in {seconds} s, {missed} missed");
        if (sv is not null) sv.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }
}
