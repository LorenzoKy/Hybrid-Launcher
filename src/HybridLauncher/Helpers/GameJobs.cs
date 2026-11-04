using System.Text;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// What is being installed or removed in each game folder right now (OptiScaler, ReShade, RenoDX, Luma, Ultra+), kept outside
/// the game page. A page is made again every time a game is opened, so a page that kept this itself forgot it as soon as you
/// looked at something else: coming back showed the old install, offered a second one into the same folder, and the mod views'
/// work was cancelled with the page. Here one change runs at a time per game, with its own cancel, and any page showing the
/// game reads its state and progress from here. Used on the UI thread only.
/// </summary>
public static class GameJobs
{
    public sealed class Job
    {
        public required string GameId { get; init; }
        /// <summary>"opti", "reshade", "renodx", "luma", "ultra".</summary>
        public required string Kind { get; init; }
        /// <summary>Shown in "… is being installed": "OptiScaler", "Luma"…</summary>
        public required string What { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public CancellationToken Token => Cts.Token;
        /// <summary>0..1, or below 0 while not known.</summary>
        public double Fraction { get; set; } = -1;
        public string Stage { get; set; } = "";
        /// <summary>The running log (OptiScaler's setup output), read by a page that opens mid-install.</summary>
        public StringBuilder Text { get; } = new();
        /// <summary>Set when it failed: the page then keeps the log open.</summary>
        public bool Failed { get; set; }
    }

    static readonly Dictionary<string, Job> Jobs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A job started or ended for this game id.</summary>
    public static event Action<string>? Changed;

    /// <summary>A running job moved on (fraction, stage or a log line).</summary>
    public static event Action<Job>? Progress;

    public static Job? For(string? gameId) => gameId is not null && Jobs.TryGetValue(gameId, out var j) ? j : null;

    public static bool Busy(string? gameId, string? kind = null) => For(gameId) is { } j && (kind is null || j.Kind == kind);

    /// <summary>
    /// Starts a job for the game, or says what is already running there and returns null (nothing is said when
    /// <paramref name="quiet"/>, e.g. a background update). End it with <see cref="End"/> in a finally.
    /// </summary>
    public static Job? TryStart(string gameId, string gameTitle, string kind, string what, bool quiet = false)
    {
        if (Jobs.TryGetValue(gameId, out var running))
        {
            if (!quiet) App.Window?.ShowInfo(L.F("{0} is being installed or removed in {1}. Try again when it is done.", running.What, gameTitle));
            return null;
        }
        var job = new Job { GameId = gameId, Kind = kind, What = what };
        Jobs[gameId] = job;
        Changed?.Invoke(gameId);
        return job;
    }

    /// <summary>For a quick change that needs no job of its own: true (and said) while another change runs in the game's folder.</summary>
    public static bool InTheWay(string gameId, string gameTitle)
    {
        if (For(gameId) is not { } running) return false;
        App.Window?.ShowInfo(L.F("{0} is being installed or removed in {1}. Try again when it is done.", running.What, gameTitle));
        return true;
    }

    public static void Report(Job job, double fraction, string? stage = null, string? line = null)
    {
        if (fraction >= 0) job.Fraction = fraction;
        if (stage is not null) job.Stage = stage;
        if (line is not null) job.Text.Append(line).Append('\n');
        Progress?.Invoke(job);
    }

    public static void End(Job job)
    {
        if (Jobs.TryGetValue(job.GameId, out var j) && ReferenceEquals(j, job)) Jobs.Remove(job.GameId);
        Changed?.Invoke(job.GameId);
    }
}
