using HybridLauncher.Core;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher;

/// <summary>
/// Self-test "pad in Downloads" (2.3-rc.6): the controller walks into the Downloads page and through it, as a person would
/// with the D-pad, and every focus it lands on is logged. It fails when the focus never leaves the side pane, or when Down
/// never reaches the covers of your games.
/// </summary>
public sealed partial class MainWindow
{
    async Task<int> SelfTestPadInDownloadsAsync()
    {
        int failures = 0;
        try
        {
            ContentFrame.Navigate(typeof(DownloadsPage));
            await Task.Delay(2000);
            (Shown as DownloadsPage)?.SelfTestGames(true);
            await Task.Delay(2500);
            var item = Nav.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == DownloadsTag);
            item.Focus(FocusState.Keyboard);
            await Task.Delay(300);

            string Where()
            {
                var f = Focused(RootGrid.XamlRoot);
                if (f is null) return "none";
                var name = f is FrameworkElement fe ? (fe.Tag as string ?? Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(fe)) : "";
                if (f is ContentControl { Content: string s }) name = s;
                bool inPage = f is DependencyObject d && IsInside(d, Shown as DependencyObject);
                string at = "";
                if (f is FrameworkElement el)
                {
                    var p = el.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                    at = $" at {p.X:0},{p.Y:0} {el.ActualWidth:0}x{el.ActualHeight:0}";
                }
                return $"{f.GetType().Name} '{name}'{(inPage ? " [page]" : "")}{at}";
            }

            Pad(PadKey.Right);
            await Task.Delay(250);
            Log.Info($"PADTEST in Downloads, Right: {Where()}");
            bool reachedPage = Focused(RootGrid.XamlRoot) is DependencyObject first && IsInside(first, Shown as DependencyObject);
            bool reachedCover = false;
            var seen = new List<string>();
            for (int i = 0; i < 24 && !reachedCover; i++)
            {
                Pad(PadKey.Down);
                await Task.Delay(220);
                var w = Where();
                seen.Add(w);
                Log.Info($"PADTEST in Downloads, Down {i + 1}: {w}");
                reachedPage |= w.Contains("[page]");
                reachedCover |= (Shown as DownloadsPage)?.SelfTestFocusOnCover() == true;
                // The Install pill must never take the controller's focus: A on it would start a real install.
                if (w.StartsWith("Button 'Install") || w.StartsWith("LaunchChip")) throw new InvalidOperationException($"the focus landed on a cover's pill ({w})");
            }
            if (reachedCover)
            {
                // Along the covers and down a row, then A opens the game's page.
                var firstCover = Focused(RootGrid.XamlRoot);
                Pad(PadKey.Right);
                await Task.Delay(220);
                Log.Info($"PADTEST in Downloads, cover Right: {Where()}");
                if ((Shown as DownloadsPage)?.SelfTestFocusOnCover() != true || ReferenceEquals(firstCover, Focused(RootGrid.XamlRoot))) throw new InvalidOperationException("Right from a cover did not reach the next cover");
                Pad(PadKey.Left);
                await Task.Delay(220);
                Log.Info($"PADTEST in Downloads, cover Left: {Where()}");
                if (!ReferenceEquals(firstCover, Focused(RootGrid.XamlRoot))) throw new InvalidOperationException("Left did not come back to the first cover");
                for (int i = 0; i < 30; i++) Pad(PadKey.Right);
                await Task.Delay(300);
                Log.Info($"PADTEST in Downloads, cover Right at the row's end: {Where()}");
                if ((Shown as DownloadsPage)?.SelfTestFocusOnCover() != true) throw new InvalidOperationException("Right at a row's end left the covers");
                for (int i = 0; i < 4; i++)
                {
                    Pad(PadKey.Down);
                    await Task.Delay(300);
                    Log.Info($"PADTEST in Downloads, cover Down {i + 1}: {Where()}");
                }
                if ((Shown as DownloadsPage)?.SelfTestFocusOnCover() != true) throw new InvalidOperationException("Down from a cover left the covers");
                Pad(PadKey.Up);
                await Task.Delay(300);
                Log.Info($"PADTEST in Downloads, cover Up: {Where()}");
                if ((Shown as DownloadsPage)?.SelfTestFocusOnCover() != true) throw new InvalidOperationException("Up from a cover left the covers");
                // Checked again right before A: only ever on a cover's cell (it opens the page), never on a pill.
                if ((Shown as DownloadsPage)?.SelfTestFocusOnCover() != true) throw new InvalidOperationException("not on a cover before A");
                Pad(PadKey.A);
                await Task.Delay(2000);
                Log.Info($"PADTEST in Downloads, A on a cover opened {Shown?.GetType().Name}");
                if (Shown is not GameShellPage and not GamePage) throw new InvalidOperationException($"A on a cover opened {Shown?.GetType().Name}");
                Pad(PadKey.B);
                await Task.Delay(1500);
            }
            if (!reachedPage) throw new InvalidOperationException("the controller never got into the Downloads page");
            if (!reachedCover) throw new InvalidOperationException($"Down never reached the covers ({string.Join(" > ", seen.Distinct())})");
            Log.Info("Self-test: pad in Downloads");
        }
        catch (Exception ex)
        {
            failures++;
            Log.Error("Self-test FAILED: pad in Downloads", ex);
        }
        return failures;
    }

    static bool IsInside(DependencyObject d, DependencyObject? root)
    {
        if (root is null) return false;
        for (DependencyObject? p = d; p is not null; p = VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, root)) return true;
        return false;
    }
}
