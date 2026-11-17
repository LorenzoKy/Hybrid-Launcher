using HybridLauncher.Core;
using Microsoft.UI.Dispatching;

namespace HybridLauncher;

/// <summary>
/// Self-test helpers (2.3-rc.7): what a test step may touch, and waiting for the UI to settle instead of a fixed time.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// A fake game made by the test scripts inside the scratch data folder (HL_DATA): the only kind a step may install into,
    /// uninstall from or swap files in. A real game (any launcher's, or a custom game elsewhere) never is.
    /// </summary>
    static bool IsScratchGame(Game g)
    {
        if (g.Store != GameStore.Custom || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HL_DATA"))) return false;
        try
        {
            var root = Path.GetFullPath(AppInfo.DataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(g.InstallDir).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// The sandbox holds (HL_SELFTEST_SANDBOX): a file written into a real game's folder and into the real data folder must both
    /// be refused by Windows. A write that gets through is removed again, logged as a failure, and the caller ends the run.
    /// </summary>
    static bool SelfTestSandboxHolds()
    {
        var targets = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HybridLauncher") };
        if (AppState.Games.FirstOrDefault(g => !g.NotInstalled && !IsScratchGame(g) && Directory.Exists(g.InstallDir)) is { } real) targets.Add(real.InstallDir);
        bool holds = true;
        foreach (var dir in targets.Where(Directory.Exists))
        {
            var probe = Path.Combine(dir, $"hl-sandbox-probe-{Environment.ProcessId}.tmp");
            try
            {
                File.WriteAllText(probe, "Hybrid Launcher self-test: this file must never be written.");
                try { File.Delete(probe); } catch { }
                Log.Error($"Self-test FAILED: sandbox: a write into {dir} got through (removed again); the run stops");
                holds = false;
            }
            catch (UnauthorizedAccessException) { Log.Info($"Self-test: sandbox refuses writes into {dir}"); }
            catch (Exception ex) { Log.Info($"Self-test: sandbox, {dir} not writable ({ex.GetType().Name})"); }
        }
        return holds;
    }

    /// <summary>
    /// Waits until the UI thread has been idle for a moment (three turns of its queue in a row answered within a frame),
    /// at least 120 ms and at most <paramref name="maxMs"/>.
    /// </summary>
    async Task SettleAsync(int maxMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(Math.Min(120, maxMs));
        int quiet = 0;
        while (watch.ElapsedMilliseconds < maxMs && quiet < 3)
        {
            var turn = System.Diagnostics.Stopwatch.StartNew();
            var done = new TaskCompletionSource();
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => done.TrySetResult())) return;
            await done.Task;
            quiet = turn.ElapsedMilliseconds <= 12 ? quiet + 1 : 0;
            await Task.Delay(40);
        }
    }
}
