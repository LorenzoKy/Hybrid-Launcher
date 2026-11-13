using HybridLauncher.Core;
using HybridLauncher.Core.Opti;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Helpers;

/// <summary>
/// Adding OptiScaler builds (2.0.0-rc.18): a build imported from a folder or an archive (one shared on Discord, a test build),
/// or a fork added by its GitHub address. Shared by the OptiScaler tab and the Auto tab, so both import the same way, with the
/// same checks and the same messages.
/// </summary>
public static class OptiSourceImport
{
    /// <summary>A build folder picked by the user, imported into the custom builds folder; null when cancelled or it failed (said).</summary>
    public static async Task<CustomBuild?> ImportFolderAsync() => await ImportAsync(await Pickers.PickFolderAsync(App.Window!));

    /// <summary>A .zip / .7z / .rar picked by the user, extracted into the custom builds folder; null when cancelled or it failed (said).</summary>
    public static async Task<CustomBuild?> ImportArchiveAsync() => await ImportAsync(await Pickers.PickFileAsync(App.Window!, ".zip", ".7z", ".rar"));

    /// <summary>Imports a build folder or archive; the result is said in a toast. Null when <paramref name="source"/> is null or it failed.</summary>
    public static async Task<CustomBuild?> ImportAsync(string? source)
    {
        if (source is null) return null;
        try
        {
            var build = await Task.Run(() => CustomBuilds.ImportAsync(AppState.Settings, source));
            App.Window?.ShowSuccess(L.F("Imported \"{0}\": OptiScaler {1}.", build.Name, build.PrettyVersion));
            return build;
        }
        catch (Exception ex)
        {
            Log.Warn($"Import of {source} failed: {ex.Message}");
            App.Window?.ShowError(L.T("Could not import the build"), ex.Message, ex);
            return null;
        }
    }

    /// <summary>
    /// Asks for the GitHub address of a fork, checks it publishes releases with an archive, and adds it to the fork list (saved).
    /// Returns the repository ("owner/name") and its releases, or null when cancelled or it failed (said).
    /// </summary>
    public static async Task<(string Repo, List<OptiRelease> Releases)?> AddForkAsync(XamlRoot root, ElementTheme theme, CancellationToken ct)
    {
        var box = new TextBox { PlaceholderText = "https://github.com/owner/OptiScaler", MinWidth = 380 };
        var note = new TextBlock { Text = L.T("Paste the GitHub address of a fork that publishes releases with OptiScaler.dll and setup_windows.bat."), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(note);
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.T("Add an OptiScaler fork"),
            Content = panel,
            PrimaryButtonText = L.T("Add"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = theme,
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var repo = OptiForks.ParseRepo(box.Text);
        if (repo is null)
        {
            App.Window?.ShowError(L.T("Could not add the fork"), L.T("That is not a GitHub address. Use https://github.com/owner/name or owner/name."));
            return null;
        }
        try
        {
            var releases = await OptiReleases.GetForkAsync(repo, force: true, ct: ct);
            if (releases.Count == 0)
            {
                App.Window?.ShowError(L.T("Could not add the fork"), L.F("{0} has no releases with a downloadable archive (.zip or .7z).", repo));
                return null;
            }
            if (!OptiForks.All(AppState.Settings).Any(f => f.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)))
            {
                AppState.Settings.OptiForks.Add(new OptiFork { Name = repo, Repo = repo });
                SettingsStore.Save();
            }
            return (repo, releases);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn($"Adding fork {repo}: {ex.Message}");
            App.Window?.ShowError(L.T("Could not add the fork"), L.F("Could not read the releases of {0}: {1}", repo, ex.Message), ex);
            return null;
        }
    }
}
