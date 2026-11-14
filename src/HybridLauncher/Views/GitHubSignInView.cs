using HybridLauncher.Core;
using HybridLauncher.Core.Net;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HybridLauncher.Views;

/// <summary>
/// Settings → Features → GitHub: the optional GitHub sign-in that lifts GitHub's limit on listing releases (OptiScaler, forks,
/// ReShade, mods), and the sign-in dialog the app also opens by itself when that limit runs out.
/// </summary>
public static class GitHubSignInView
{
    public static void Fill(Panel host)
    {
        App.Window?.KeepFocusOffSearch();
        host.Children.Clear();
        host.Children.Add(new TextBlock { Text = L.T("GITHUB"), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, 20, 0, 4) });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = L.T("GitHub sign-in"), Style = Ui.Style("RowHeader") });
        if (GitHubAuth.SignedIn) title.Children.Add(Ui.Pill(L.T("Signed in"), Ui.Tone.Good));
        else if (Http.GitHubLimitedUntil is not null) title.Children.Add(Ui.Pill(L.T("Limit reached"), Ui.Tone.Partial));
        text.Children.Add(title);
        string about;
        if (GitHubAuth.SignedIn)
            about = L.F("Signed in as {0}", GitHubAuth.Login is { Length: > 0 } l ? l : "GitHub")
                + (GitHubAuth.Source == "gh" ? "  ·  " + L.T("from the GitHub CLI") : "")
                + "  ·  " + L.T("up to 5000 version lists an hour.");
        else
            about = L.T("Versions of OptiScaler, its forks, ReShade and mods are read from GitHub, which allows 60 requests an hour without sign-in: past that, lists show as offline. Signing in raises the limit to 5000.");
        if (!GitHubAuth.SignedIn && Http.GitHubLimitedUntil is { } until)
            about += "\n" + L.F("The limit is used up until {0:HH:mm}.", until.ToLocalTime());
        text.Children.Add(new TextBlock { Text = about, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (GitHubAuth.SignedIn)
        {
            var outButton = new Button { Content = L.T("Sign out"), MinWidth = 96 };
            ToolTipService.SetToolTip(outButton, L.T("Forgets the sign-in on this PC. To revoke it on GitHub too: Settings → Applications there."));
            outButton.Click += (_, _) => { GitHubAuth.SignOut(); Fill(host); };
            actions.Children.Add(outButton);
        }
        else
        {
            var cli = new Button { Content = L.T("Use GitHub CLI"), MinWidth = 96, Visibility = Visibility.Collapsed };
            ToolTipService.SetToolTip(cli, L.T("The GitHub CLI (gh) is signed in on this PC: use that account."));
            cli.Click += async (_, _) =>
            {
                cli.IsEnabled = false;
                if (await GitHubAuth.UseCliAsync()) App.Window?.ShowSuccess(L.F("Signed in to GitHub as {0}.", GitHubAuth.Login ?? "GitHub"));
                else App.Window?.ShowError(L.T("GitHub sign-in"), L.T("The GitHub CLI is not signed in any more."));
                Fill(host);
            };
            actions.Children.Add(cli);
            _ = ShowCliIfSignedInAsync(cli);
            var b = new Button { Content = L.T("Sign in"), Style = Ui.Style("AccentButtonStyle"), MinWidth = 96, IsEnabled = GitHubAuth.CanSignIn };
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                await SignInAsync(host.XamlRoot);
                Fill(host);
            };
            actions.Children.Add(b);
        }
        host.Children.Add(Row(text, actions));
    }

    static async Task ShowCliIfSignedInAsync(Button cli)
    {
        if (await GitHubAuth.FindCliAsync() is not { } found) return;
        cli.DispatcherQueue.TryEnqueue(() =>
        {
            cli.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(cli, L.F("The GitHub CLI (gh) is signed in on this PC as {0}: use that account.", found.Login ?? "?"));
        });
    }

    static bool _open;

    /// <summary>
    /// The sign-in dialog: shows the code, opens github.com/login/device, and waits there until the user confirms (or cancels).
    /// Returns true when signed in.
    /// </summary>
    public static async Task<bool> SignInAsync(XamlRoot? root)
    {
        if (_open || root is null || !GitHubAuth.CanSignIn) return false;
        _open = true;
        using var cts = new CancellationTokenSource();
        var code = new TextBlock { Text = "…", FontSize = 30, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = true, CharacterSpacing = 120, HorizontalAlignment = HorizontalAlignment.Center };
        var status = new TextBlock { Text = L.T("Asking GitHub for a code…"), Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        var copy = new Button { Content = L.T("Copy code and open GitHub"), Style = Ui.Style("AccentButtonStyle"), HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = false };
        string? uri = null;
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(code.Text);
            Clipboard.SetContent(package);
            if (uri is not null) Ui.Open(uri);
        };
        var panel = new StackPanel { Spacing = 14, MaxWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Type this code on GitHub's page and confirm. Hybrid Launcher asks for no permissions: it can only read what anyone can see, to list versions."),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(code);
        panel.Children.Add(copy);
        panel.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.T("Sign in to GitHub"),
            Content = panel,
            CloseButtonText = L.T("Cancel"),
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        bool ok = false;
        var flow = GitHubAuth.SignInAsync(d => dialog.DispatcherQueue.TryEnqueue(() =>
        {
            code.Text = d.UserCode;
            uri = d.VerificationUri;
            copy.IsEnabled = true;
            status.Text = L.F("Waiting for you to confirm on {0}…", d.VerificationUri.Replace("https://", ""));
        }), cts.Token);
        _ = flow.ContinueWith(t => dialog.DispatcherQueue.TryEnqueue(() =>
        {
            if (t.IsCompletedSuccessfully) { ok = true; dialog.Hide(); }
            else if (t.Exception?.GetBaseException() is { } ex) status.Text = ex.Message;
        }), TaskScheduler.Default);
        try
        {
            await dialog.ShowOnceAsync();
        }
        finally
        {
            cts.Cancel();
            _open = false;
        }
        try { await flow; } catch { /* cancelled, or the error is already on the dialog */ }
        if (ok) App.Window?.ShowSuccess(L.F("Signed in to GitHub as {0}. Version lists load again.", GitHubAuth.Login ?? "GitHub"));
        return ok;
    }

    static Border Row(FrameworkElement text, FrameworkElement action)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return new Border { Style = Ui.Style("RowStyle"), Child = grid };
    }
}
