namespace HybridLauncher.Helpers;

/// <summary>
/// Waits for background checks measured on the wall clock (2.3). A plain Task.Delay of hours does not count the time the PC
/// sleeps, so on a PC that sleeps between sessions the OptiScaler and Ultra+ checks of "every three hours" in practice ran only
/// when Hybrid Launcher started. Here the wait looks at the clock every half minute: a check is due after <c>every</c> on the
/// clock, and sooner (after <c>onReturn</c>) once the user comes back to the window.
/// </summary>
public static class WallClock
{
    static long _returned;

    /// <summary>The window came to the front: checks waiting long enough run now.</summary>
    public static void Returned() => Interlocked.Exchange(ref _returned, DateTime.UtcNow.Ticks);

    public static async Task WaitAsync(DateTime last, TimeSpan every, TimeSpan onReturn, CancellationToken ct)
    {
        while (true)
        {
            var since = DateTime.UtcNow - last;
            if (since >= every || since < TimeSpan.Zero) return;
            if (since >= onReturn && Interlocked.Read(ref _returned) > last.Ticks) return;
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }
}
