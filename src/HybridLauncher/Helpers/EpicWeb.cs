using System.Collections.Concurrent;
using System.Text.Json;
using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// Epic achievements through Epic's own store page (2.1-rc.11), the fallback since rc.12: the queries go straight to Epic with its
/// launcher's user agent (EpicStats.QueryAsync); only when Epic refuses that (a bot check: 403 and an hCaptcha page) are they sent by an Epic store page open in a hidden browser of the
/// app, the way the store itself asks them, with the cookies of that browser (the folder the Epic sign-in uses) and the
/// account's token. Nothing gets around Epic's check: when Epic wants a confirmation, the hidden page cannot ask, the reads
/// stop for the session and the user is told; <see cref="ShowCheckAsync"/> opens the store page in a window where the user
/// answers Epic's check, and the reads start again. The hidden browser closes after a minute without queries.
/// </summary>
static class EpicWeb
{
    const string StoreUrl = "https://store.epicgames.com/en-US/";

    // The store's own endpoint first (same origin as the page), then the launcher's (Epic lets the store page call it).
    static readonly string[] Endpoints = ["https://store.epicgames.com/graphql", "https://launcher.store.epicgames.com/graphql"];

    static WebView2? _view;
    static Task<CoreWebView2?>? _starting;
    static DispatcherTimer? _idle;
    static readonly ConcurrentDictionary<string, TaskCompletionSource<(int Status, string Text)>> Waiting = new();
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Hooks the route into <see cref="EpicStats"/>; called once at start on the UI thread.</summary>
    public static void Start(Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        EpicStats.Browser = async (body, signed, ct) =>
        {
            string? token = signed ? await EpicAccount.AccessTokenAsync(ct) : null;
            var done = new TaskCompletionSource<EpicStats.BrowserAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_shut || !ui.TryEnqueue(async () =>
            {
                try { done.TrySetResult(await QueryAsync(body, token, ct)); }
                catch (Exception ex) { done.TrySetException(ex); }
            })) return EpicStats.BrowserAnswer.Unavailable;
            return await done.Task;
        };
        EpicStats.NeedsUser += () => ui.TryEnqueue(() =>
        {
            Log.Warn("Epic achievements: Epic asks to confirm on its page; reads stopped until then");
            Inbox.Add(Inbox.App, L.T("Epic Games wants a confirmation"),
                L.T("Epic asks you to confirm on its store page before Hybrid Launcher can read your Epic achievements. Settings → Achievements → Epic Games → Confirm on Epic."),
                key: "epic-check-" + DateTime.UtcNow.ToString("yyyyMMdd"));
        });
    }

    /// <summary>
    /// Self-test: a public query (a game's achievement definitions, no account) sent from the store page; the answer's start,
    /// or why there is none. Tells whether Epic answers the route at all.
    /// </summary>
    public static async Task<string> ProbeAsync(string sandbox)
    {
        var body = "{\"query\":" + Js("query($sandboxId: String!, $locale: String!) { Achievement { productAchievementsRecordBySandbox(sandboxId: $sandboxId, locale: $locale) { totalAchievements } } }")
            + ",\"variables\":{\"sandboxId\":" + Js(sandbox) + ",\"locale\":\"en-US\"}}";
        var answer = await QueryAsync(body, null, CancellationToken.None);
        return answer.Text is not { } text ? (answer.Refused ? "no answer (Epic's check)" : "no answer (no browser)") : text.Length > 160 ? text[..160] : text;
    }

    /// <summary>
    /// Sends one query from the store page: the answer, refused when Epic said no (403 or its check), unavailable when the page
    /// could not ask at all (no browser, the page did not open, no reply in time: worth another try later).
    /// </summary>
    static async Task<EpicStats.BrowserAnswer> QueryAsync(string body, string? token, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // Another query was refused while this one waited its turn: not asked again.
            if (EpicStats.Refused) return EpicStats.BrowserAnswer.No;
            if (_shut || await StartAsync() is not { } web) return EpicStats.BrowserAnswer.Unavailable;
            _idle?.Stop();
            bool refused = false;
            foreach (var endpoint in Endpoints)
            {
                if (_shut || _view?.CoreWebView2 is not { } currentWeb) return EpicStats.BrowserAnswer.Unavailable;
                var id = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<(int Status, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
                Waiting[id] = tcs;
                try
                {
                    var headers = token is null ? "{'Content-Type':'application/json'}" : "{'Content-Type':'application/json','Authorization':" + Js("Bearer " + token) + "}";
                    var script = "(async()=>{const id=" + Js(id) + ";try{const r=await fetch(" + Js(endpoint)
                        + ",{method:'POST',credentials:'include',headers:" + headers + ",body:" + Js(body)
                        + "});const t=await r.text();chrome.webview.postMessage(id+'|'+r.status+'|'+t);}catch(e){chrome.webview.postMessage(id+'|0|'+e);}})();";
                    await currentWeb.ExecuteScriptAsync(script);
                    var (status, text) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                    if (status == 200 && EpicStats.IsAnswer(text)) return new(text, false);
                    if (status is 401 or 403 || text.Contains("captcha", StringComparison.OrdinalIgnoreCase)) refused = true;
                }
                catch (TimeoutException) { Log.Warn($"Epic achievements: {endpoint} did not answer"); }
                finally { Waiting.TryRemove(id, out _); }
            }
            return refused ? EpicStats.BrowserAnswer.No : EpicStats.BrowserAnswer.Unavailable;
        }
        finally
        {
            Gate.Release();
            StartIdle();
        }
    }

    /// <summary>A string as a JSON / JavaScript literal (quoted, escaped). No reflection: the published build is trimmed.</summary>
    static string Js(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

    static Task<CoreWebView2?> StartAsync()
    {
        if (_view?.CoreWebView2 is { } ready) return Task.FromResult<CoreWebView2?>(ready);
        return _starting ??= StartCore();
    }

    static async Task<CoreWebView2?> StartCore()
    {
        WebView2? view = null;
        try
        {
            if (_shut) return null;
            var window = App.Window ?? throw new InvalidOperationException("No window yet.");
            view = new WebView2 { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false, IsTabStop = false };
            window.HostHidden(view);
            var web = await EnsureAsync(view);
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            // Muted and never a window of its own: the store page is only the place the queries are sent from.
            web.IsMuted = true;
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.WebMessageReceived += (_, e) =>
            {
                string msg;
                try { msg = e.TryGetWebMessageAsString(); } catch { return; }
                var parts = msg.Split('|', 3);
                if (parts.Length < 3 || !Waiting.TryGetValue(parts[0], out var done)) return;
                if (parts[1] != "200") Log.Warn($"Epic achievements: the store page got {parts[1]}{(parts[2].Contains("captcha", StringComparison.OrdinalIgnoreCase) ? " (Epic's check)" : "")}");
                done.TrySetResult((int.TryParse(parts[1], out var status) ? status : 0, parts[2]));
            };
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.Navigate(StoreUrl);
            bool ok = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // The page did not open (offline, Epic down) or the app closed meanwhile: the browser goes, the next query tries afresh.
            if (!ok || _shut)
            {
                if (!ok) Log.Warn("Epic achievements: the store page did not open");
                Dispose(view);
                return null;
            }
            _view = view;
            return web;
        }
        catch (Exception ex)
        {
            Log.Warn($"Epic achievements: no store page ({ex.Message})");
            if (view is not null) Dispose(view);
            return null;
        }
        finally
        {
            _starting = null;
        }
    }

    static void Dispose(WebView2 view)
    {
        try
        {
            view.Close();
            App.Window?.HostHidden(view, remove: true);
        }
        catch (Exception ex) { Log.Warn($"Epic store page close: {ex.Message}"); }
    }

    static bool _shut;

    /// <summary>The window closes: the hidden page and its timer go with it, and no new one starts.</summary>
    public static void Shutdown()
    {
        _shut = true;
        _idle?.Stop();
        foreach (var w in Waiting.Values) w.TrySetResult((0, ""));
        Close();
    }

    /// <summary>The browser data the Epic sign-in uses (its cookies are the store page's).</summary>
    static async Task<CoreWebView2> EnsureAsync(WebView2 view)
    {
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(AppInfo.DataRoot, "WebView2"), new CoreWebView2EnvironmentOptions());
        await view.EnsureCoreWebView2Async(env);
        return view.CoreWebView2;
    }

    static void StartIdle()
    {
        if (_view is null) return;
        if (_idle is null)
        {
            _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _idle.Tick += (_, _) =>
            {
                _idle.Stop();
                if (!Waiting.IsEmpty || _view is null) return;
                Close();
            };
        }
        _idle.Stop();
        _idle.Start();
    }

    static void Close()
    {
        var view = _view;
        _view = null;
        if (view is not null) Dispose(view);
    }

    /// <summary>
    /// Epic's store page in a window of its own, where the user answers Epic's check (or signs in to the store) by hand.
    /// When the window closes the hidden page starts fresh and achievements are asked again.
    /// </summary>
    public static async Task ShowCheckAsync(ElementTheme theme)
    {
        // One window at a time: a second click while it is open does nothing.
        if (_checkOpen) return;
        _checkOpen = true;
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var web = new WebView2();
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(16, 10, 16, 10),
                Text = L.T("Epic's store page. If Epic asks you to confirm, answer it here, then close this window: Hybrid Launcher reads your achievements again."),
            });
            Grid.SetRow(web, 1);
            grid.Children.Add(web);
            AccountSignIn.PopUp(L.T("Confirm on Epic"), grid, theme, 980, 760, () =>
            {
                try { web.Close(); } catch { }
                done.TrySetResult();
            });
            try
            {
                if (!web.IsLoaded)
                {
                    var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    web.Loaded += (_, _) => loaded.TrySetResult();
                    // Closed before it ever showed: nothing to load.
                    await Task.WhenAny(loaded.Task, done.Task);
                }
                if (!done.Task.IsCompleted)
                {
                    var core = await EnsureAsync(web);
                    // Closed while the browser started: it is already gone.
                    if (!done.Task.IsCompleted)
                    {
                        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
                        core.Navigate(StoreUrl);
                    }
                }
            }
            catch (Exception ex) when (done.Task.IsCompleted) { Log.Info($"Epic store window closed while opening ({ex.Message})"); }
            catch (Exception ex) { Log.Warn($"Epic store window: {ex.Message}"); }
            await done.Task;
            if (App.ShuttingDown) return;
            await Gate.WaitAsync();
            try { Close(); }
            finally { Gate.Release(); }
            EpicStats.Retry();
            Log.Info("Epic achievements: asked again after the store window");
            if (App.Window?.DispatcherQueue is { } ui) Library.RefreshAfterEpicChange(ui);
        }
        finally { _checkOpen = false; }
    }

    static bool _checkOpen;
}
