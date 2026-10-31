using System.Runtime.InteropServices;
using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// Signing in to a launcher account from Hybrid Launcher, in a pop-up window of its own above Hybrid Launcher (as the launchers do), never a
/// dialog inside the page. Steam: the window of Steam's own sign-in, a QR code scanned with the Steam mobile app, or name
/// and password sent straight to Steam's authentication (Steam Guard asked there); only Steam's refresh token is kept.
/// Epic and GOG: their own sign-in page fills the window and hands back a code; Hybrid Launcher never sees the password. Tokens are kept
/// encrypted for this Windows user.
/// </summary>
static class AccountSignIn
{
    /// <summary>Signs in; returns the account's name, or null when cancelled.</summary>
    public static async Task<string?> RunAsync(GameStore store, XamlRoot root, ElementTheme theme)
    {
        try
        {
            var name = store switch
            {
                GameStore.Steam => await SteamAsync(theme),
                GameStore.Epic => await WebAsync(theme, "Epic Games", EpicAccount.LoginUrl, async web =>
                {
                    if (!web.Source.StartsWith(EpicAccount.RedirectPrefix, StringComparison.OrdinalIgnoreCase)) return null;
                    var text = await web.ExecuteScriptAsync("document.body.innerText");
                    return EpicAccount.CodeFromPage(text);
                }, EpicAccount.SignInAsync),
                GameStore.Gog => await WebAsync(theme, "GOG", GogAccount.LoginUrl,
                    web => Task.FromResult(GogAccount.CodeFromUrl(web.Source)), GogAccount.SignInAsync),
                _ => null,
            };
            if (name is null) return null;
            var a = AppState.Settings.Accounts;
            switch (store)
            {
                case GameStore.Steam: a.SteamName = name; a.Steam = true; break;
                case GameStore.Epic: a.EpicName = name; a.Epic = true; break;
                case GameStore.Gog: a.GogName = name; a.Gog = true; break;
            }
            SettingsStore.Save();
            Log.Info($"Signed in to {store}");
            return name;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn($"{store} sign-in: {ex.Message}");
            App.Window?.ShowError(L.F("Could not sign in to {0}", StoreLogos.Label(store)), ex.Message);
            return null;
        }
    }

    public static void SignOut(GameStore store)
    {
        switch (store)
        {
            case GameStore.Steam: SteamAccount.SignOut(); AppState.Settings.Accounts.SteamName = null; break;
            case GameStore.Epic: EpicAccount.SignOut(); AppState.Settings.Accounts.EpicName = null; break;
            case GameStore.Gog: GogAccount.SignOut(); AppState.Settings.Accounts.GogName = null; break;
        }
        SettingsStore.Save();
        Log.Info($"Signed out of {store}");
    }

    // ---------- the pop-up window ----------

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>
    /// A window of its own, owned by Hybrid Launcher's (it stays above it and closes with it), centred on it, in Hybrid Launcher's theme.
    /// <paramref name="closed"/> runs once it is closed, however that happens.
    /// </summary>
    internal static Window PopUp(string title, UIElement content, ElementTheme theme, int width, int height, Action closed)
    {
        var host = new Grid { RequestedTheme = theme, Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["SolidBackgroundFillColorBaseBrush"] };
        host.Children.Add(content);
        var window = new Window { Title = title, Content = host, SystemBackdrop = new MicaBackdrop() };
        host.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var app = window.AppWindow;
        app.IsShownInSwitchers = false;
        if (app.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        try { app.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); } catch { }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // The new window has no screen yet: its size follows Hybrid Launcher's (the screen it will open on).
        var ownerHwnd = App.Window is { } ow ? WinRT.Interop.WindowNative.GetWindowHandle(ow) : hwnd;
        double scale = Math.Max(1, Native.GetDpiForWindow(ownerHwnd) / 96.0);
        int w = (int)(width * scale), h = (int)(height * scale);
        if (App.Window is { } owner)
        {
            _ = SetWindowLongPtr(hwnd, -8 /* GWLP_HWNDPARENT */, ownerHwnd);
            var o = owner.AppWindow;
            app.MoveAndResize(new Windows.Graphics.RectInt32(o.Position.X + (o.Size.Width - w) / 2, o.Position.Y + (o.Size.Height - h) / 2, w, h));
        }
        else app.Resize(new Windows.Graphics.SizeInt32(w, h));
        window.Closed += (_, _) => closed();
        window.Activate();
        return window;
    }

    // ---------- Steam: its own sign-in window ----------

    /// <summary>
    /// As the Steam client's sign-in window: name and password on the left (Steam Guard asked under them, or approved in the
    /// Steam mobile app), a QR code to scan with the Steam mobile app on the right. Whichever finishes first signs in.
    /// </summary>
    /// <summary>What went wrong with a Steam sign-in, in words: a refusal from Steam, a dropped connection, or the error itself.</summary>
    static string SteamProblem(Exception ex) => ex switch
    {
        SteamKit2.Authentication.AuthenticationException ae => L.F("Steam said no: {0}", ae.Result),
        OperationCanceledException or TimeoutException => L.T("Steam closed the connection before the sign-in finished. Try again."),
        _ => ex.Message,
    };

    static async Task<string?> SteamAsync(ElementTheme theme)
    {
        var done = new TaskCompletionSource<string?>();
        var qrCts = new CancellationTokenSource();
        Window? window = null;

        var user = new TextBox { Header = L.T("Sign in with account name"), PlaceholderText = L.T("The name you sign in with, not your profile name") };
        var pass = new PasswordBox { Header = L.T("Password") };
        var code = new TextBox { Header = "Steam Guard", PlaceholderText = "ABC12", MaxLength = 8, CharacterCasing = CharacterCasing.Upper, Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Visibility = Visibility.Collapsed };
        var signIn = new Button { Content = L.T("Sign in"), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8, 12, 9) };
        var left = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(user);
        left.Children.Add(pass);
        left.Children.Add(code);
        left.Children.Add(signIn);
        left.Children.Add(status);

        var qrImage = new Image { Width = 196, Height = 196 };
        var qrBox = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.White), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = qrImage };
        var qrStatus = new TextBlock { Text = L.T("Use the Steam mobile app to sign in via QR code"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.85, MaxWidth = 230 };
        var right = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = L.T("Or sign in with QR"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        right.Children.Add(qrBox);
        right.Children.Add(qrStatus);

        var columns = new Grid { ColumnSpacing = 36 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.Children.Add(left);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(StoreLogos.Create(GameStore.Steam, 30));
        head.Children.Add(new TextBlock { Text = L.T("Sign in to Steam"), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var page = new StackPanel { Spacing = 22, Padding = new Thickness(36, 28, 36, 28) };
        page.Children.Add(head);
        page.Children.Add(columns);
        page.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12,
            Text = L.T("Both ways go straight to Steam, which hands back a sign-in token; Hybrid Launcher keeps only that token, encrypted for your Windows user."),
        });

        void Finish(string? name)
        {
            if (!done.TrySetResult(name)) return;
            qrCts.Cancel();
            window?.Close();
        }

        // The QR code: Steam's own, renewed by Steam every so often; approved in the Steam mobile app, it signs in.
        async Task QrAsync(int attempt = 0)
        {
            try
            {
                var name = await SteamAccount.SignInWithQrAsync(url => window?.DispatcherQueue.TryEnqueue(() => qrImage.Source = Qr(url)), qrCts.Token);
                window?.DispatcherQueue.TryEnqueue(() => Finish(name));
            }
            catch (Exception ex)
            {
                if (qrCts.IsCancellationRequested) return;
                Log.Warn($"Steam QR sign-in: {ex.GetType().Name}: {ex.Message}");
                // Steam dropped the connection (not the user): a new code, once or twice, before giving up.
                if (ex is OperationCanceledException && attempt < 2)
                {
                    await QrAsync(attempt + 1);
                    return;
                }
                window?.DispatcherQueue.TryEnqueue(() => qrStatus.Text = SteamProblem(ex));
            }
        }

        TaskCompletionSource<string>? waitingCode = null;
        bool running = false;
        async void SignInClick()
        {
            if (waitingCode is { } wait)
            {
                if (code.Text.Trim().Length == 0) return;
                waitingCode = null;
                signIn.IsEnabled = false;
                status.Text = L.T("Signing in to Steam…");
                wait.TrySetResult(code.Text.Trim());
                return;
            }
            if (running || user.Text.Trim().Length == 0 || pass.Password.Length == 0) return;
            running = true;
            // One sign-in at a time with Steam: the QR code gives way to the name and password.
            qrCts.Cancel();
            qrStatus.Text = L.T("Signing in with the account name instead.");
            signIn.IsEnabled = user.IsEnabled = pass.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            status.Text = L.T("Signing in to Steam…");
            var guard = new Guard(window!.DispatcherQueue, (email, hint, retry) =>
            {
                code.Text = "";
                code.Visibility = Visibility.Visible;
                status.Text = (retry ? L.T("That code was not right. Try the new one.") + " " : "")
                    + (email ? L.F("Steam sent a code to {0}.", hint ?? L.T("your e-mail")) : L.T("Type the code shown in the Steam mobile app."));
                signIn.Content = L.T("Continue");
                signIn.IsEnabled = true;
                code.Focus(FocusState.Programmatic);
                waitingCode = new TaskCompletionSource<string>();
                return waitingCode.Task;
            }, () => status.Text = L.T("Approve the sign-in in the Steam mobile app…"));
            try
            {
                Finish(await SteamAccount.SignInAsync(user.Text.Trim(), pass.Password, guard, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam sign-in: {ex.GetType().Name}: {ex.Message}");
                status.Text = SteamProblem(ex);
                running = false;
                code.Visibility = Visibility.Collapsed;
                user.IsEnabled = pass.IsEnabled = signIn.IsEnabled = true;
                signIn.Content = L.T("Sign in");
            }
        }
        signIn.Click += (_, _) => SignInClick();
        pass.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) SignInClick(); };
        code.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) SignInClick(); };

        window = PopUp(L.T("Sign in to Steam"), page, theme, 780, 540, () =>
        {
            waitingCode?.TrySetCanceled();
            done.TrySetResult(null);
            qrCts.Cancel();
        });
        _ = QrAsync();
        return await done.Task;
    }

    /// <summary>The address as a QR code picture (black on white, crisp at any scale).</summary>
    static BitmapImage Qr(string url)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(10);
        var image = new BitmapImage();
        // The bytes are already in memory: wrapped as a stream, nothing to write first (no wait on the UI thread).
        using var stream = new MemoryStream(png).AsRandomAccessStream();
        image.SetSource(stream);
        return image;
    }

    /// <summary>Steam Guard's questions, asked from SteamKit2's threads and answered in the window on the UI thread.</summary>
    sealed class Guard(Microsoft.UI.Dispatching.DispatcherQueue ui, Func<bool, string?, bool, Task<string>> ask, Action waitForApp) : SteamAccount.IGuard
    {
        public Task<string> CodeAsync(bool email, string? hint, bool retry) => OnUi(() => ask(email, hint, retry));

        public Task<bool> ApproveInAppAsync() => OnUi(() =>
        {
            waitForApp();
            return Task.FromResult(true);
        });

        Task<T> OnUi<T>(Func<Task<T>> work)
        {
            var tcs = new TaskCompletionSource<T>();
            ui.TryEnqueue(async () =>
            {
                try { tcs.SetResult(await work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
    }

    // ---------- Epic and GOG: their own sign-in page, in its own window ----------

    static async Task<string?> WebAsync(ElementTheme theme, string label, string url,
        Func<CoreWebView2, Task<string?>> codeOf, Func<string, CancellationToken, Task<string>> signIn)
    {
        var done = new TaskCompletionSource<string?>();
        var web = new WebView2();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(16, 10, 16, 10), Text = L.F("Sign in on {0}'s own page: Hybrid Launcher never sees your password.", label) };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(status);
        Grid.SetRow(web, 1);
        grid.Children.Add(web);
        Window? window = null;
        bool busy = false;
        window = PopUp(L.F("Sign in to {0}", label), grid, theme, 620, 780, () =>
        {
            done.TrySetResult(null);
            try { web.Close(); } catch { }
        });
        try
        {
            // The page is made once the browser is in the new window (made earlier, it stayed blank there).
            if (!web.IsLoaded)
            {
                var loaded = new TaskCompletionSource();
                web.Loaded += (_, _) => loaded.TrySetResult();
                await loaded.Task;
            }
            // The browser's data (the sign-in cookies) stays in Hybrid Launcher's own folder.
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(AppInfo.DataRoot, "WebView2"), new CoreWebView2EnvironmentOptions());
            await web.EnsureCoreWebView2Async(env);
            // Pages that open a window of their own (a captcha, "sign in with...") stay in this one.
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                web.CoreWebView2.Navigate(e.Uri);
            };
            web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) Log.Warn($"{label} sign-in page: {e.WebErrorStatus} ({e.HttpStatusCode})");
                if (busy) return;
                string? code;
                try { code = await codeOf(web.CoreWebView2); } catch { code = null; }
                if (code is null) return;
                busy = true;
                status.Text = L.T("Signing in…");
                web.Visibility = Visibility.Collapsed;
                try
                {
                    var name = await signIn(code, CancellationToken.None);
                    done.TrySetResult(name);
                    window.Close();
                }
                catch (Exception ex)
                {
                    Log.Warn($"{label} sign-in: {ex.Message}");
                    status.Text = ex.Message;
                    busy = false;
                    web.Visibility = Visibility.Visible;
                    web.CoreWebView2.Navigate(url);
                }
            };
            web.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            status.Text = L.F("The sign-in page could not open (Microsoft Edge WebView2 is needed): {0}", ex.Message);
        }
        return await done.Task;
    }
}
