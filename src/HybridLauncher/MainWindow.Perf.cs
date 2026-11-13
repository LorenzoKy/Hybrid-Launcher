using System.Runtime.InteropServices;
using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher;

/// <summary>
/// Self-test "perf": the app used as a hand uses it, with real mouse clicks (SendInput) on the game list, a game page's tabs,
/// the library and Settings' tabs, and the wheel on each page. Every click is measured by <see cref="UiPerf"/> (input to the first
/// frame that shows its answer, the frames missed after it, the UI thread's stalls and the code they were in); the log ends with
/// a summary. Read-only: nothing is installed, played or changed except which page and tab are shown.
/// HL_PERF_ROUNDS=n repeats the game switches n times (default 2).
/// </summary>
public sealed partial class MainWindow
{
    internal async Task<int> SelfTestPerfAsync(Func<string, Action, int, Task> step)
    {
        UiPerf.EnableDetailed();
        UiPerf.Reset();
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("HL_PERF_ROUNDS"), out var r) ? Math.Max(1, r) : 2;
        // Settle: the start's own work (scan, art, stats) is not what is measured.
        await Task.Delay(4000);
        var games = Nav.MenuItems.OfType<NavigationViewItem>().Where(i => i.Tag is string t && AppState.Find(t) is not null).Take(8).ToList();
        Log.Info($"Perf: {games.Count} games in the list, {rounds} round(s)");

        // Game switches from the list, the way a hand does it: one, a pause to look, the next.
        for (int round = 0; round < rounds; round++)
            foreach (var item in games)
            {
                await ClickAsync(item, $"game list: {item.Content}");
                await Task.Delay(1100);
            }

        // A game page's tabs, each clicked, then the wheel down the page.
        if (Shown is GamePage page)
        {
            // A hand looks at the page a few seconds before picking a tab (the idle warm-up uses that time).
            await Task.Delay(3500);
            foreach (var b in page.SelfTestTabButtons.ToList())
            {
                await ClickAsync(b, $"tab {AutomationName(b)}");
                await Task.Delay(900);
            }
            await MeasureWheelScrollAsync(page.SelfTestScroller, "perf game page", 8);
        }

        // The library (the logo), a cover clicked, back to the library.
        await ClickAsync(TitleBrand, "logo: library");
        await Task.Delay(1500);
        if (Shown is LibraryPage lib)
        {
            if (lib.SelfTestScroller is { } sv) await MeasureWheelScrollAsync(sv, "perf library", 8);
            if (lib.SelfTestFirstTile() is { } tile)
            {
                await ClickAsync(tile, "library cover");
                await Task.Delay(1500);
                await ClickAsync(BackButton, "back");
                await Task.Delay(1200);
            }
        }

        // Settings and each of its tabs.
        if (Nav.SettingsItem is NavigationViewItem settings)
        {
            await ClickAsync(settings, "settings");
            await Task.Delay(1500);
            if (Shown is SettingsPage sp)
            {
                foreach (var tab in sp.SelfTestTabItems.ToList())
                {
                    await ClickAsync(tab, $"settings tab {AutomationName(tab)}");
                    await Task.Delay(800);
                }
                await MeasureWheelScrollAsync(sp.SelfTestScroller, "perf settings", 8);
            }
        }

        // Back to games after Settings (the page is built again).
        foreach (var item in games.Take(3))
        {
            await ClickAsync(item, $"game list again: {item.Content}");
            await Task.Delay(1100);
        }
        await Task.Delay(800);
        UiPerf.Summary();
        await step("perf done", () => { }, 100);
        return 0;
    }

    static string AutomationName(FrameworkElement e)
    {
        var n = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(e);
        if (!string.IsNullOrEmpty(n)) return n;
        return e is ContentControl { Content: string s } ? s : e is SelectorBarItem si ? si.Text : e.Name;
    }

    /// <summary>A real left click in the middle of <paramref name="target"/> (brought into view first).</summary>
    async Task ClickAsync(FrameworkElement target, string what)
    {
        try
        {
            if (target.XamlRoot is null) { Log.Warn($"Perf click {what}: not in the window"); return; }
            target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await Task.Delay(250);
            var at = target.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(target.ActualWidth / 2, target.ActualHeight / 2));
            double scale = GetDpiScale();
            var origin = new POINT();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _ = ClientToScreen(hwnd, ref origin);
            _ = SetCursorPos(origin.X + (int)(at.X * scale), origin.Y + (int)(at.Y * scale));
            // A hand moves before it clicks (and the frame clock wakes on the move, as it does for a hand).
            MouseNudge(1); await Task.Delay(40); MouseNudge(-1); await Task.Delay(60);
            Log.Info($"Perf click: {what}");
            MouseButton(0x0002); // left down
            await Task.Delay(55);
            MouseButton(0x0004); // left up
        }
        catch (Exception ex) { Log.Warn($"Perf click {what}: {ex.Message}"); }
    }

    static void MouseButton(uint flags)
    {
        var input = new MOUSEINPUT_WRAP { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        _ = SendInputMouse(1, [input], Marshal.SizeOf<MOUSEINPUT_WRAP>());
    }
}
