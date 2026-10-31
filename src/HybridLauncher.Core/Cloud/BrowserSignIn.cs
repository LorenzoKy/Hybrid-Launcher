using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HybridLauncher.Core.Cloud;

/// <summary>
/// The sign-ins of the cloud saves run in the user's own browser, for every provider: the browser's password manager, its
/// passkeys and its extensions work there, and nothing about Windows' web views or the app's text boxes can get in the way.
/// Hybrid Launcher listens on a one-time port of 127.0.0.1 only; the page's path holds a random token, so nothing else (another site,
/// another tab) can talk to it. MEGA (which has no OAuth) gets a page served from here whose form
/// posts back to here.
/// </summary>
public sealed class BrowserSignIn : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public BrowserSignIn()
    {
        _listener.Start();
        Token = Base64Url(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Random, in the page's path and in OAuth's state: a request without it is not Hybrid Launcher's.</summary>
    public string Token { get; }

    public string Origin => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>The page this sign-in serves.</summary>
    public string Url => $"{Origin}/{Token}";

    public sealed record Request(string Method, Dictionary<string, string> Query, Dictionary<string, string> Form, NetworkStream Stream);

    /// <summary>
    /// Waits for the browser's next request to this sign-in and hands it to <paramref name="answer"/>, which replies with
    /// <see cref="RespondAsync"/> and returns a result to finish, or null to wait for the next one (a form shown again).
    /// Requests to any other path (the favicon, a stranger) get a 404.
    /// </summary>
    public async Task<T> ServeAsync<T>(Func<Request, Task<T?>> answer, CancellationToken ct) where T : class
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            Request? request;
            try { request = await ReadAsync(stream, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { continue; }
            if (request is null) { await RespondAsync(stream, "", ct, 404); continue; }
            if (await answer(request) is { } done) return done;
        }
    }

    async Task<Request?> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var buffer = new byte[16384];
        int read = 0, end;
        while ((end = IndexOf(buffer, read, "\r\n\r\n"u8)) < 0)
        {
            if (read == buffer.Length) return null;
            int n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
            if (n == 0) return null;
            read += n;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var line = head[0].Split(' ');
        if (line.Length < 2) return null;
        var target = line[1];
        var q = target.IndexOf('?');
        var query = q < 0 ? [] : Parse(target[(q + 1)..]);
        // An OAuth provider comes back to the bare origin (its redirect is the origin): there the token is in the state instead.
        var path = q < 0 ? target : target[..q];
        if (path != "/" + Token && !(path == "/" && query.GetValueOrDefault("state") == Token)) return null;
        var form = new Dictionary<string, string>();
        if (line[0] == "POST")
        {
            int length = head.Skip(1).Select(h => h.Split(':', 2)).Where(h => h.Length == 2 && h[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(h => int.TryParse(h[1].Trim(), out var l) ? l : 0).FirstOrDefault();
            if (length is < 0 or > 8192) return null;
            var body = new byte[length];
            int have = Math.Min(length, read - end - 4);
            Array.Copy(buffer, end + 4, body, 0, have);
            while (have < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(have), cts.Token);
                if (n == 0) return null;
                have += n;
            }
            form = Parse(Encoding.UTF8.GetString(body));
        }
        return new Request(line[0], query, form, stream);
    }

    static int IndexOf(byte[] buffer, int count, ReadOnlySpan<byte> what) => buffer.AsSpan(0, count).IndexOf(what);

    /// <summary>Replies with a page in Hybrid Launcher's look: <paramref name="body"/> is HTML already escaped where it needs to be.</summary>
    public static async Task RespondAsync(NetworkStream stream, string body, CancellationToken ct, int status = 200)
    {
        var html = Page(body);
        var bytes = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(bytes, ct);
    }

    /// <summary>A short message page (the sign-in finished, or went wrong).</summary>
    public static Task MessageAsync(NetworkStream stream, string title, string text, CancellationToken ct) =>
        RespondAsync(stream, $"<h1>{Html(title)}</h1><p>{Html(text)}</p>", ct);

    public static string Html(string? text) => WebUtility.HtmlEncode(text ?? "");

    static string Page(string body) => $$"""
        <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="color-scheme" content="dark"><title>Hybrid Launcher</title>
        <style>
        :root{--bg:#0d0b12;--card:rgba(255,255,255,.055);--text:#eee8f6;--dim:#a79fb6;--accent:#b14dff;--bad:#ff6b8a}
        *{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(1200px 600px at 20% -10%,#2a1240,transparent),var(--bg);color:var(--text);font:15px/1.5 "Segoe UI Variable","Segoe UI",system-ui,sans-serif;padding:16px}
        main{width:min(420px,100%);background:var(--card);backdrop-filter:blur(24px);border-radius:16px;padding:28px 28px 24px;box-shadow:0 20px 60px rgba(0,0,0,.45)}
        .brand{font-weight:700;letter-spacing:.24em;font-size:12px;color:var(--accent);margin-bottom:6px}
        h1{font-size:22px;margin:0 0 8px;font-weight:600}p{margin:0 0 14px;color:var(--dim)}
        label{display:block;font-size:13px;color:var(--dim);margin:12px 0 4px}
        input{width:100%;padding:10px 12px;border-radius:8px;border:0;background:rgba(255,255,255,.08);color:var(--text);font:inherit;outline:none}
        input:focus{background:rgba(255,255,255,.12);box-shadow:0 0 0 2px var(--accent)}
        button{width:100%;margin-top:18px;padding:10px;border:0;border-radius:8px;background:var(--accent);color:#fff;font:inherit;font-weight:600;cursor:pointer}
        button:disabled{opacity:.6;cursor:default}.err{color:var(--bad);margin:12px 0 0}.note{font-size:12px;margin:16px 0 0}
        </style><main><div class="brand">HYBRID</div>{{body}}</main></html>
        """;

    public static Dictionary<string, string> Parse(string query) => query.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).GroupBy(p => Unescape(p[0]))
        .ToDictionary(g => g.Key, g => Unescape(g.First().ElementAtOrDefault(1) ?? ""));

    static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _listener.Stop();
}
