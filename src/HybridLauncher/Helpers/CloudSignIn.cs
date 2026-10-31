using HybridLauncher.Core;
using HybridLauncher.Core.Cloud;
using HybridLauncher.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Helpers;

/// <summary>
/// Signing in to MEGA happens in the user's own browser, for the widest compatibility (password managers, passkeys,
/// two-factor apps, no web view): MEGA, which has no OAuth, gets its sign-in page served by Hybrid Launcher on 127.0.0.1. A small window above Hybrid Launcher waits for it and can cancel.
/// </summary>
static class CloudSignIn
{
    /// <summary>Signs in to MEGA; returns the account's e-mail address, or null when cancelled.</summary>
    public static Task<string?> MegaAsync(ElementTheme theme) => BrowserAsync(theme, "MEGA", "",
        L.T("MEGA's sign-in page is open in your browser. Enter your e-mail and password there (and your two-factor code, if you use one); this window closes by itself."),
        L.T("Your e-mail and password go straight to MEGA, once. Hybrid Launcher keeps only the session MEGA opens, encrypted for your Windows user, never the password; signing out closes that session."),
        MegaAuth.SignInInBrowserAsync);

    static async Task<string?> BrowserAsync(ElementTheme theme, string name, string glyph, string waiting, string note, Func<Action<string>, CancellationToken, Task<string>> signIn)
    {
        var cts = new CancellationTokenSource();
        Window? window = null;
        var status = new TextBlock { Text = waiting, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        var again = new HyperlinkButton { Content = L.T("Open the page again"), Padding = new Thickness(0) };
        string? url = null;
        again.Click += (_, _) => { if (url is not null) Ui.Open(url); };
        var cancel = new Button { Content = L.T("Cancel"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96 };
        cancel.Click += (_, _) => window?.Close();
        var page = Page(name, glyph, status, again, Note(note), cancel);
        window = AccountSignIn.PopUp(L.F("Sign in to {0}", name), page, theme, 560, 380, () => cts.Cancel());
        try
        {
            var email = await signIn(u => { url = u; Ui.Open(u); }, cts.Token);
            window.Close();
            App.Window?.Activate();
            return email;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn($"{name} sign-in: {ex.Message}");
            window.Close();
            App.Window?.ShowError(L.F("Could not sign in to {0}", name), ex.Message);
            return null;
        }
    }

    static StackPanel Page(string title, string glyph, params UIElement[] body)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(new FontIcon { Glyph = glyph, FontSize = 24, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var page = new StackPanel { Spacing = 14, Padding = new Thickness(32, 26, 32, 26) };
        page.Children.Add(head);
        foreach (var b in body) page.Children.Add(b);
        return page;
    }

    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12 };
}
