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

    static readonly HashSet<string> Busy = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsBusy(string id) => Busy.Contains(id);

    /// <summary>Hooked once at start: every game that closes, however it was started, sends its changed saves.</summary>
    public static void Start() => GameSessions.Ended += game => _ = AfterSessionAsync(game);

    /// <summary>
    /// The folders the sync uses (<see cref="CloudSaves.Parts"/>): the saves folder picked, else the one PCGamingWiki names that
    /// exists on this PC; the game's settings folder PCGamingWiki names (asked once); the Steam emulator's folder of the game.
    /// </summary>
    public static async Task<List<CloudPart>> PartsAsync(Game game, GameSettings settings, CancellationToken ct = default)
    {
        bool needSaves = !(settings.CloudSaveDirPicked && settings.CloudSaveDir is not null) && !(settings.CloudSaveDir is { } known && Directory.Exists(known));
        if (needSaves || !settings.CloudConfigChecked)
        {
            try
            {
                var art = await GameInfo.GetAsync(game, ct);
                var wiki = await Pcgw.GetAsync(game, art.SteamAppId ?? game.SteamAppId, ct);
                if (wiki is not null)
                {
                    if (needSaves && await Task.Run(() => Pcgw.ExistingFolder(wiki.SavePaths, game.Store), ct) is { } dir && !CloudSaves.TooBroad(dir))
                        settings.CloudSaveDir = dir;
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
        if (!CloudSaves.On || settings.CloudOff || game.NotInstalled) return;
        Busy.Add(game.Id);
        Changed?.Invoke(game.Id);
        try
        {
            if ((await PartsAsync(game, settings)).Count == 0) return;
            // A cloud app still waking up (an online-only file being fetched) gets its time, but the game is not held for ever:
            // past it, the restore stops before touching anything here and the game starts with this PC's saves.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            if (await CloudSaves.BeforeLaunchAsync(game, settings, s => AskAsync(game, s), cts.Token) is { } message) App.Window?.ShowSuccess(message);
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
            App.Window?.ShowError(L.T("Cloud saves"), L.F("The cloud check failed, {0} starts with the saves on this PC: {1}", game.Title, ex.Message));
        }
        finally
        {
            Busy.Remove(game.Id);
            Changed?.Invoke(game.Id);
        }
    }

    static async Task AfterSessionAsync(Game game)
    {
        var settings = AppState.Settings.For(game.Id);
        if (!CloudSaves.On || settings.CloudOff) return;
        Busy.Add(game.Id);
        Changed?.Invoke(game.Id);
        try
        {
            // A few seconds for the game's last writes (some save while closing, or from a helper process).
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (GameSessions.IsActive(game.Id) || (await PartsAsync(game, settings)).Count == 0) return;
            if (await CloudSaves.AfterSessionAsync(game, settings))
                App.Window?.ShowSuccess(L.F("Saves of {0} sent to {1}.", game.Title, CloudSaves.StoreName));
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Error($"Cloud after {game.Name}", ex);
            App.Window?.ShowError(L.T("Cloud saves"), L.F("The saves of {0} could not be sent: {1}", game.Title, ex.Message));
        }
        finally
        {
            Busy.Remove(game.Id);
            Changed?.Invoke(game.Id);
        }
    }

    /// <summary>"Sync now": the same rule as Play and closing, asked by the user (conflicts asked too).</summary>
    public static async Task SyncNowAsync(Game game)
    {
        var settings = AppState.Settings.For(game.Id);
        if ((await PartsAsync(game, settings)).Count == 0)
        {
            App.Window?.ShowError(L.T("Cloud saves"), L.F("Hybrid Launcher did not find the saves folder of {0}. Pick it in the cloud menu.", game.Title));
            return;
        }
        Busy.Add(game.Id);
        Changed?.Invoke(game.Id);
        try
        {
            var message = await CloudSaves.BeforeLaunchAsync(game, settings, s => AskAsync(game, s));
            SettingsStore.Save();
            App.Window?.ShowSuccess(message ?? L.F("Saves of {0} are in sync.", game.Title));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Cloud saves"), ex.Message); }
        finally
        {
            Busy.Remove(game.Id);
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
                if ((await PartsAsync(game, settings)).Count == 0) continue;
                var status = await CloudSaves.StatusAsync(game, settings);
                switch (status.State)
                {
                    case CloudState.ChangedHere or CloudState.NotUploaded: await CloudSaves.UploadAsync(game, settings); up++; break;
                    case CloudState.ChangedInCloud: await CloudSaves.RestoreAsync(game, settings); down++; break;
                    case CloudState.Conflict: skipped++; break;
                    case CloudState.Synced when status.Cloud is { } c: settings.CloudSyncedHash = c.Hash; break;
                }
            }
            catch (Exception ex) { Log.Warn($"Cloud sync all, {game.Name}: {ex.Message}"); skipped++; }
            Changed?.Invoke(game.Id);
        }
        SettingsStore.Save();
        return (up, down, skipped);
    }

    public static void Raise(string id) => Changed?.Invoke(id);

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
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => CloudSaves.Choice.UseCloud,
            ContentDialogResult.Secondary => CloudSaves.Choice.KeepHere,
            _ => CloudSaves.Choice.Skip,
        };
    }
}
