using HybridLauncher.Core;
using HybridLauncher.Core.Cloud;
using HybridLauncher.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Helpers;

/// <summary>
/// Cloud saves in the app: the saves folder found on PCGamingWiki (once per game, kept in its settings), the check before Play,
/// the upload after the game closes, and the question when both sides changed. Nothing here ever stops a game from starting:
/// a cloud that is slow or gone is logged and the game starts with the saves on this PC.
/// </summary>
public static class CloudSync
{
    /// <summary>A game's cloud state may have changed (game id): the game page's cloud button follows it.</summary>
    public static event Action<string>? Changed;

    // Changed from the UI thread and from sessions ending on other threads: a plain HashSet could be corrupted.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Busy = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsBusy(string id)
    {
        if (Busy.ContainsKey(id)) return true;
        lock (Sending) return Sending.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Hooked once at start: every game that closes, however it was started, sends its changed saves.</summary>
    public static void Start() => GameSessions.Ended += game => _ = AfterSessionAsync(game);

    /// <summary>
    /// The folders the sync uses (<see cref="CloudSaves.Parts"/>): the saves folder picked, else the one PCGamingWiki names that
    /// exists on this PC; the game's settings folder PCGamingWiki names (asked once); the Steam emulator's folder of the game.
    /// </summary>
    /// <summary>When each game's saves folder (found on the wiki, not picked) was last looked for again.</summary>
    static readonly Dictionary<string, DateTime> Rechecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A saves folder found on the wiki is looked for again (<paramref name="fresh"/>: now, before Play and after the game
    /// closes; otherwise every two minutes at most): a game that keeps one folder per account writes in another one when the
    /// account changes, and the sync must follow it. A folder the user picked is never changed.
    /// </summary>
    public static async Task<List<CloudPart>> PartsAsync(Game game, GameSettings settings, CancellationToken ct = default, bool fresh = false)
    {
        bool picked = settings.CloudSaveDirPicked && settings.CloudSaveDir is not null;
        bool known = settings.CloudSaveDir is { } d && Directory.Exists(d);
        bool due;
        lock (Rechecked) due = fresh || !Rechecked.TryGetValue(game.Id, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(2);
        bool needSaves = !picked && (!known || due);
        if (needSaves || !settings.CloudConfigChecked)
        {
            if (needSaves) lock (Rechecked) Rechecked[game.Id] = DateTime.UtcNow;
            try
            {
                var art = await GameInfo.GetAsync(game, ct);
                var wiki = await Pcgw.GetAsync(game, art.SteamAppId ?? game.SteamAppId, ct);
                if (wiki is not null)
                {
                    if (needSaves && await Task.Run(() => Pcgw.ExistingFolder(wiki.SavePaths, game.Store), ct) is { } dir && !CloudSaves.TooBroad(dir)
                        && !string.Equals(dir, settings.CloudSaveDir, StringComparison.OrdinalIgnoreCase))
                    {
                        if (settings.CloudSaveDir is not null) Log.Info($"Cloud: {game.Name} saves now in {dir} (was {settings.CloudSaveDir}): the game writes there");
                        settings.CloudSaveDir = dir;
                    }
                    if (!settings.CloudConfigChecked)
                    {
                        settings.CloudConfigDir = await Task.Run(() => Pcgw.ExistingFolder(wiki.ConfigPaths, game.Store), ct);
                        settings.CloudConfigChecked = true;
                    }
                    SettingsStore.Save();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Cloud saves folder of {game.Name}: {ex.Message}"); }
        }
        return CloudSaves.Parts(game, settings);
    }

    /// <summary>Before Play: newer saves from the cloud come down (asked only when both sides changed).</summary>
    public static async Task BeforeLaunchAsync(Game game)
    {
        var settings = AppState.Settings.For(game.Id);
        if (!Ready(game, settings) || game.NotInstalled) return;
        Busy.TryAdd(game.Id, 0);
        Changed?.Invoke(game.Id);
        try
        {
            if ((await PartsAsync(game, settings, fresh: true)).Count == 0) { Log.Info($"Cloud: {game.Name} has no saves folder, nothing to sync"); return; }
            // A cloud app still waking up (an online-only file being fetched) gets its time, but the game is not held for ever:
            // past it, the restore stops before touching anything here and the game starts with this PC's saves.
            // An upload only zips the saves here: the sending goes on while the game runs (minutes, for a big save to MEGA).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            // Done quietly: the cloud button says "Synced". Only an error is shown.
            if (await CloudSaves.BeforeLaunchAsync(game, settings, s => AskAsync(game, s), cts.Token, sending => Track(game, sending)) is { } message) Log.Info($"Cloud: {message}");
            SettingsStore.Save();
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Cloud: {game.Name} check took too long, starting with the saves on this PC");
            App.Window?.ShowInfo(L.F("{0} is taking long to answer: {1} starts with the saves on this PC.", CloudSaves.StoreName, game.Title));
        }
        catch (Exception ex)
        {
            Log.Error($"Cloud before {game.Name}", ex);
            if (ex is CloudPartialException) Failed(game, ex);
            else App.Window?.ShowError(L.T("Cloud saves"), L.F("The cloud check failed, {0} starts with the saves on this PC: {1}", game.Title, ex.Message), ex);
        }
        finally
        {
            Busy.TryRemove(game.Id, out _);
            Changed?.Invoke(game.Id);
        }
    }

    /// <summary>Uploads going on in the background (started before Play), with their game's id.</summary>
    static readonly List<(string Id, Task Task)> Sending = [];

    /// <summary>An upload is still going on in the background: quitting waits for it.</summary>
    public static bool IsSending
    {
        get { lock (Sending) return Sending.Count > 0; }
    }

    /// <summary>Waits for the background uploads, at most <paramref name="limit"/>.</summary>
    public static async Task WaitForSendingAsync(TimeSpan limit)
    {
        Task[] all;
        lock (Sending) all = Sending.Select(s => s.Task).ToArray();
        await Task.WhenAny(Task.WhenAll(all), Task.Delay(limit));
    }

    /// <summary>A sending that goes on after Play: the game's cloud button stays busy until it is done, a failure is said.</summary>
    static async void Track(Game game, Task sending)
    {
        lock (Sending) Sending.Add((game.Id, sending));
        Changed?.Invoke(game.Id);
        try
        {
            await sending;
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Error($"Cloud before {game.Name}, sending", ex);
            Failed(game, ex);
        }
        finally
        {
            lock (Sending) Sending.Remove((game.Id, sending));
            Changed?.Invoke(game.Id);
        }
    }

    /// <summary>Whether the game syncs now; when cloud saves are on but no cloud is ready, the log says why nothing happens.</summary>
    static bool Ready(Game game, GameSettings settings)
    {
        if (settings.CloudOff || !CloudSaves.Settings.Enabled) return false;
        if (CloudSaves.On) return true;
        Log.Warn($"Cloud: {game.Name} not synced, none of {string.Join(", ", CloudSaves.Settings.Providers)} is ready (OneDrive folder: {(OneDrive.Folder() is null ? "not found" : "found")}, MEGA: {(MegaAuth.SignedIn ? "signed in" : "signed out")})");
        return false;
    }

    static async Task AfterSessionAsync(Game game)
    {
        var settings = AppState.Settings.For(game.Id);
        if (!Ready(game, settings)) return;
        Busy.TryAdd(game.Id, 0);
        Changed?.Invoke(game.Id);
        try
        {
            // A few seconds for the game's last writes (some save while closing, or from a helper process).
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (GameSessions.IsActive(game.Id)) return;
            // Never busy for ever (rc.10: a check that got no answer left "Cloud syncing" on the game until a restart): the
            // folder lookup gets a minute, the check and the sending five; past that the next Play or Sync now tries again.
            using (var look = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
                if ((await PartsAsync(game, settings, look.Token, fresh: true)).Count == 0) { Log.Info($"Cloud: {game.Name} has no saves folder, nothing to send"); return; }
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await CloudSaves.AfterSessionAsync(game, settings, cts.Token);
            SettingsStore.Save();
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Cloud: {game.Name} after the session took too long, left for the next sync");
            App.Window?.ShowInfo(L.F("{0} is taking long to answer: the saves of {1} are sent at the next sync.", CloudSaves.StoreName, game.Title));
        }
        catch (Exception ex)
        {
            Log.Error($"Cloud after {game.Name}", ex);
            SettingsStore.Save();
            Failed(game, ex);
        }
        finally
        {
            Busy.TryRemove(game.Id, out _);
            Changed?.Invoke(game.Id);
        }
    }

    /// <summary>"Sync now": the same rule as Play and closing, asked by the user (conflicts asked too).</summary>
    public static async Task SyncNowAsync(Game game)
    {
        var settings = AppState.Settings.For(game.Id);
        if ((await PartsAsync(game, settings, fresh: true)).Count == 0)
        {
            App.Window?.ShowError(L.T("Cloud saves"), L.F("Hybrid Launcher did not find the saves folder of {0}. Pick it in the cloud menu.", game.Title));
            return;
        }
        Busy.TryAdd(game.Id, 0);
        Changed?.Invoke(game.Id);
        try
        {
            var message = await CloudSaves.BeforeLaunchAsync(game, settings, s => AskAsync(game, s));
            SettingsStore.Save();
            if (message is not null) Log.Info($"Cloud: {message}");
        }
        catch (Exception ex)
        {
            SettingsStore.Save();
            Failed(game, ex);
        }
        finally
        {
            Busy.TryRemove(game.Id, out _);
            Changed?.Invoke(game.Id);
        }
    }

    /// <summary>Every game with cloud saves on, one after the other (Settings → Cloud saves → Sync all now). Conflicts are skipped.</summary>
    public static async Task<(int Up, int Down, int Skipped)> SyncAllAsync(IProgress<string>? progress = null)
    {
        int up = 0, down = 0, skipped = 0;
        foreach (var game in AppState.Games.Where(g => !g.Installing && !g.NotInstalled).ToList())
        {
            var settings = AppState.Settings.For(game.Id);
            if (settings.CloudOff || GameSessions.IsActive(game.Id)) continue;
            progress?.Report(game.Title);
            try
            {
                if ((await PartsAsync(game, settings, fresh: true)).Count == 0) continue;
                var status = await CloudSaves.StatusAsync(game, settings);
                switch (status.State)
                {
                    case CloudState.ChangedHere or CloudState.NotUploaded: await CloudSaves.UploadAsync(game, settings); up++; break;
                    case CloudState.ChangedInCloud: await CloudSaves.RestoreAsync(game, settings); down++; break;
                    case CloudState.Conflict: skipped++; break;
                    case CloudState.Synced when status.Cloud is { } c: settings.CloudSyncedHash = c.Hash; break;
                }
            }
            catch (CloudPartialException ex) { Log.Warn($"Cloud sync all, {game.Name}: {ex.Message}"); up++; }
            catch (Exception ex) { Log.Warn($"Cloud sync all, {game.Name}: {ex.Message}"); skipped++; }
            Changed?.Invoke(game.Id);
        }
        SettingsStore.Save();
        return (up, down, skipped);
    }

    public static void Raise(string id) => Changed?.Invoke(id);

    /// <summary>
    /// The one cloud message the user sees on their own: what went wrong, in red. Saves that reached only some of the clouds say
    /// which ones missed them (they get them at the next sync); the line under it gives the likely cause.
    /// </summary>
    static void Failed(Game game, Exception ex)
    {
        if (ex is CloudPartialException p)
            App.Window?.ShowError(L.T("Cloud saves"), L.F("The saves of {0} are in {1} but not in {2} yet: {3} They go there at the next sync.", game.Title,
                string.Join(", ", p.Sent), string.Join(", ", p.Missed.Select(m => m.Name)), Sentence(p.Missed[0].Error.Message)), p.Missed[0].Error);
        else App.Window?.ShowError(L.T("Cloud saves"), L.F("The saves of {0} could not be sent: {1}", game.Title, ex.Message), ex);
    }

    static string Sentence(string text) => text.TrimEnd().EndsWith('.') ? text.TrimEnd() : text.TrimEnd() + ".";

    /// <summary>Both sides changed: the user picks which saves stay; the other copy is kept (history or Hybrid Launcher's backups).</summary>
    static async Task<CloudSaves.Choice> AskAsync(Game game, CloudStatus status)
    {
        if (App.Window?.Content?.XamlRoot is not { } root) return CloudSaves.Choice.Skip;
        var here = status.SaveDir is { } d && Directory.Exists(d) ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Select(f => File.GetLastWriteTime(f)).DefaultIfEmpty().Max() : default;
        var text = new StackPanel { Spacing = 10 };
        text.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = L.F("The saves of {0} changed on this PC and in the cloud since the last sync. Which ones should Hybrid Launcher keep? The other copy is not lost: it stays in the cloud's history or in Hybrid Launcher's backups.", game.Title) });
        text.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Text = L.F("This PC: last changed {0}", here == default ? "—" : here.ToString("g")) });
        if (status.Cloud is { } c)
            text.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Text = L.F("Cloud: sent from {0} on {1}", c.Machine, c.UpdatedUtc.ToLocalTime().ToString("g")) });
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (App.Window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Title = L.T("Which saves should stay?"),
            Content = text,
            PrimaryButtonText = L.T("Use the cloud's"),
            SecondaryButtonText = L.T("Keep this PC's"),
            CloseButtonText = L.T("Decide later"),
            DefaultButton = ContentDialogButton.Secondary,
        };
        return await dialog.ShowOnceAsync() switch
        {
            ContentDialogResult.Primary => CloudSaves.Choice.UseCloud,
            ContentDialogResult.Secondary => CloudSaves.Choice.KeepHere,
            _ => CloudSaves.Choice.Skip,
        };
    }
}
