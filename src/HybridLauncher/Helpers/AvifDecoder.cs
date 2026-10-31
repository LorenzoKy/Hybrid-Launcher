using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using HybridLauncher.Core;

namespace HybridLauncher.Helpers;

/// <summary>
/// Steam serves the pictures of store descriptions (and the posters of their GIF clips) only as AVIF, which Windows decodes
/// only with the AV1 extension installed. This turns them into PNG files once, with the browser engine every Windows already
/// has (a WebView2 control, 1 px and invisible, in the window), and keeps them in Hybrid Launcher's image cache. The engine starts on
/// the first AVIF and goes away after 30 s without work, so it costs nothing while it is not needed. Runs on the UI thread.
/// </summary>
public static class AvifDecoder
{
    static Task<CoreWebView2?>? _starting;
    static WebView2? _view;
    static readonly Dictionary<string, TaskCompletionSource<string?>> Waiting = [];
    static readonly Dictionary<string, Task<string?>> InFlight = new(StringComparer.Ordinal);
    static DispatcherTimer? _idle;
    static bool _unavailable;
    static int _next;

    static string Dir => AppInfo.Ensure(Path.Combine(AppInfo.ImageCacheDir, "avif"));

    /// <summary>Whether a URL points to an AVIF picture.</summary>
    public static bool IsAvif(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.EndsWith(".avif", StringComparison.OrdinalIgnoreCase);

    /// <summary>A local PNG of the AVIF at <paramref name="url"/> (from the cache when converted before), or null if it cannot be made.</summary>
    public static Task<string?> ToPngAsync(string url)
    {
        // The ?t= part only marks Steam's upload time: the same picture keeps the same file.
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url.Split('?')[0].ToLowerInvariant())))[..20];
        var file = Path.Combine(Dir, key + ".png");
        if (File.Exists(file)) return Task.FromResult<string?>(file);
        if (_unavailable) return Task.FromResult<string?>(null);
        if (InFlight.TryGetValue(key, out var running)) return running;
        var task = ConvertAsync(url, file);
        InFlight[key] = task;
        _ = task.ContinueWith(_ => App.Window?.DispatcherQueue.TryEnqueue(() => InFlight.Remove(key)), TaskScheduler.Default);
        return task;
    }

    static async Task<string?> ConvertAsync(string url, string file)
    {
        try
        {
            var web = await StartAsync();
            if (web is null) return null;
            _idle?.Stop();
            var id = (++_next).ToString();
            var done = new TaskCompletionSource<string?>();
            Waiting[id] = done;
            // Fetched by the engine (Steam's image servers allow any page to), drawn on a canvas, written back as PNG.
            var script =
                "(async()=>{const id=\"" + id + "\";try{" +
                "const r=await fetch(\"" + JsonEncodedText.Encode(url) + "\");if(!r.ok)throw new Error('HTTP '+r.status);" +
                "const bm=await createImageBitmap(await r.blob());const c=new OffscreenCanvas(bm.width,bm.height);" +
                "c.getContext('2d').drawImage(bm,0,0);const o=new Uint8Array(await (await c.convertToBlob({type:'image/png'})).arrayBuffer());" +
                "let s='';for(let i=0;i<o.length;i+=32768)s+=String.fromCharCode.apply(null,o.subarray(i,i+32768));" +
                "chrome.webview.postMessage(id+'|'+btoa(s));}catch(e){chrome.webview.postMessage(id+'|!'+e);}})();";
            await web.ExecuteScriptAsync(script);
            string? base64;
            try { base64 = await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { Waiting.Remove(id); }
            if (base64 is null) return null;
            var bytes = Convert.FromBase64String(base64);
            var tmp = file + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        catch (Exception ex)
        {
            Log.Warn($"AVIF {url}: {ex.Message}");
            return null;
        }
        finally
        {
            if (Waiting.Count == 0) StartIdle();
        }
    }

    static Task<CoreWebView2?> StartAsync()
    {
        if (_view?.CoreWebView2 is { } ready) return Task.FromResult<CoreWebView2?>(ready);
        return _starting ??= StartCore();
    }

    static async Task<CoreWebView2?> StartCore()
    {
        try
        {
            var window = App.Window ?? throw new InvalidOperationException("No window yet.");
            // The XAML control is the supported way to host the engine in a WinUI window: 1 px, see-through, not clickable.
            var view = new WebView2 { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false, IsTabStop = false };
            window.HostHidden(view);
            // Hardware acceleration off (Settings → Graphics): the browser engine decodes on the CPU too, like Steam's own switch.
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = AppState.Settings.HardwareAcceleration ? "" : "--disable-gpu" };
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(AppInfo.CacheDir, "webview"), options);
            await view.EnsureCoreWebView2Async(env);
            var web = view.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.WebMessageReceived += (_, e) =>
            {
                string msg;
                try { msg = e.TryGetWebMessageAsString(); } catch { return; }
                int bar = msg.IndexOf('|');
                if (bar < 0 || !Waiting.TryGetValue(msg[..bar], out var done)) return;
                var body = msg[(bar + 1)..];
                if (body.StartsWith('!'))
                {
                    Log.Warn($"AVIF decode: {body[1..]}");
                    done.TrySetResult(null);
                }
                else done.TrySetResult(body);
            };
            var loaded = new TaskCompletionSource();
            web.NavigationCompleted += (_, _) => loaded.TrySetResult();
            web.NavigateToString("<!doctype html><html><body></body></html>");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            _view = view;
            return web;
        }
        catch (Exception ex)
        {
            // No WebView2 runtime (rare): pictures that need it stay hidden, as before.
            Log.Warn($"AVIF decoder unavailable: {ex.Message}");
            _unavailable = true;
            return null;
        }
        finally
        {
            _starting = null;
        }
    }

    static void StartIdle()
    {
        if (_view is null) return;
        if (_idle is null)
        {
            _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _idle.Tick += (_, _) =>
            {
                _idle.Stop();
                if (Waiting.Count > 0 || InFlight.Count > 0 || _view is null) return;
                var view = _view;
                _view = null;
                try
                {
                    view.Close();
                    App.Window?.HostHidden(view, remove: true);
                }
                catch (Exception ex) { Log.Warn($"AVIF decoder close: {ex.Message}"); }
            };
        }
        _idle.Stop();
        _idle.Start();
    }
}
