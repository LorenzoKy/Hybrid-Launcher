using Microsoft.UI.Dispatching;

namespace HybridLauncher.Helpers;

/// <summary>
/// A DispatcherQueueTimer that reaches its owner through a weak reference (2.0.0-rc.12).
/// <para>
/// A DispatcherQueueTimer is a native object outside WinUI's reference tracking: the Tick handler it holds is a GC root for as
/// long as the timer lives. A handler that reaches its owner (a lambda using <c>this</c>, or a local holding the timer itself)
/// closes a cycle owner → timer → handler → owner that the GC can never break, and the owner stays in memory with everything
/// it holds: a retired game page with its whole visual tree and pictures (about 5 MB each), a kept Dashboard, a ScrollView.
/// </para>
/// Pass a <c>static</c> lambda as <paramref name="tick"/>: it receives the owner, so it needs to capture nothing.
/// </summary>
public static class WeakTimer
{
    public static DispatcherQueueTimer Create<T>(DispatcherQueue queue, T owner, TimeSpan interval, Action<T> tick, bool repeating = true) where T : class
    {
        var weak = new WeakReference<T>(owner);
        var timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += (t, _) =>
        {
            if (weak.TryGetTarget(out var o)) tick(o);
            else t.Stop();
        };
        return timer;
    }
}
