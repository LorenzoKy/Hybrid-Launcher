using System.Diagnostics;
using System.Text.Json;
using HybridLauncher.Core.Accounts;

namespace HybridLauncher.Core.Net;

/// <summary>
/// An optional GitHub sign-in, only to lift GitHub's limit on reading public releases (60 requests an hour per network without
/// sign-in, 5000 with). It asks for no permission at all: the token can read what anyone can read and nothing else.
/// Signed in with GitHub's device flow (a code typed on github.com, never a password here), or taken from the GitHub CLI
/// (gh) when it is signed in on this PC. The token is kept encrypted for this Windows user (<see cref="AccountTokens"/>) and only
/// ever sent to api.github.com.
/// </summary>
public static class GitHubAuth
{
    /// <summary>Hybrid Launcher's OAuth app on GitHub (public by design: device flow needs no secret). Empty: no sign-in button.</summary>
    public const string ClientId = "Ov23licTxmIGEmJzQwBi";

    const string Store = "github";

    public static bool CanSignIn => ClientId.Length > 0;

    static string? _token;
    static string? _login;
    static bool _read;

    /// <summary>The token for api.github.com, or null when not signed in.</summary>
    public static string? Token { get { Load(); return _token; } }

    /// <summary>The account's name ("octocat") when signed in.</summary>
    public static string? Login { get { Load(); return _login; } }

    /// <summary>Where the sign-in came from: "device" (this app) or "gh" (the GitHub CLI).</summary>
    public static string? Source { get; private set; }

    public static bool SignedIn => Token is not null;

    /// <summary>Raised on the thread that changed it: signed in, signed out, or the token stopped working.</summary>
    public static event Action? Changed;

    static void Load()
    {
        if (_read) return;
        _read = true;
        if (AccountTokens.Read(Store) is { } v && v.TryGetValue("token", out var t) && t.Length > 0)
        {
            _token = t;
            _login = v.GetValueOrDefault("login");
            Source = v.GetValueOrDefault("source") ?? "device";
        }
    }

    static void Save(string token, string? login, string source)
    {
        AccountTokens.Write(Store, new Dictionary<string, string> { ["token"] = token, ["login"] = login ?? "", ["source"] = source });
        _token = token;
        _login = login;
        Source = source;
        _read = true;
        Log.Info($"GitHub: signed in as {login ?? "?"} ({source}).");
        Http.ResetGitHubLimit();
        Changed?.Invoke();
    }

    public static void SignOut()
    {
        AccountTokens.Delete(Store);
        _token = _login = Source = null;
        _read = true;
        Log.Info("GitHub: signed out.");
        Http.ResetGitHubLimit();
        Changed?.Invoke();
    }

    /// <summary>GitHub answered 401 to the token (revoked, expired): it is dropped, requests go without it.</summary>
    internal static void TokenRejected(string token)
    {
        if (_token != token) return;
        Log.Warn("GitHub: the saved sign-in was refused (revoked or expired); signed out.");
        SignOut();
    }

    // ---- GitHub CLI ----

    /// <summary>The GitHub CLI's account on this PC (its token and name), or null when gh is missing or signed out.</summary>
    public static async Task<(string Token, string? Login)?> FindCliAsync(CancellationToken ct = default)
    {
        var token = (await RunGhAsync("auth token", ct))?.Trim();
        if (string.IsNullOrEmpty(token)) return null;
        var login = await UserAsync(token, ct);
        return login is null ? null : (token, login);
    }

    /// <summary>Uses the GitHub CLI's sign-in (a copy of its token, kept like this app's own).</summary>
    public static async Task<bool> UseCliAsync(CancellationToken ct = default)
    {
        if (await FindCliAsync(ct) is not { } cli) return false;
        Save(cli.Token, cli.Login, "gh");
        return true;
    }

    static async Task<string?> RunGhAsync(string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("gh", args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return null; // gh not installed, or it hung
        }
    }

    // ---- Device flow ----

    /// <summary>What to show while the sign-in waits: the code to type and the page to type it on.</summary>
    public sealed record DeviceCode(string UserCode, string VerificationUri, DateTime Expires);

    /// <summary>
    /// Signs in with GitHub's device flow: <paramref name="show"/> gets the code to type on github.com/login/device, then this waits
    /// until the user confirms there (or cancels, or the code expires). Returns the account's name.
    /// </summary>
    public static async Task<string> SignInAsync(Action<DeviceCode> show, CancellationToken ct)
    {
        if (!CanSignIn) throw new InvalidOperationException(L.T("GitHub sign-in is not available in this build."));
        // No scope: the token reads public data only.
        using var start = await PostAsync("https://github.com/login/device/code", new() { ["client_id"] = ClientId, ["scope"] = "" }, ct);
        var deviceCode = start.RootElement.GetProperty("device_code").GetString()!;
        var interval = start.RootElement.TryGetProperty("interval", out var i) ? i.GetInt32() : 5;
        var expires = DateTime.UtcNow.AddSeconds(start.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 900);
        show(new DeviceCode(start.RootElement.GetProperty("user_code").GetString()!,
            start.RootElement.TryGetProperty("verification_uri", out var u) ? u.GetString()! : "https://github.com/login/device", expires));

        while (DateTime.UtcNow < expires)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            using var poll = await PostAsync("https://github.com/login/oauth/access_token", new()
            {
                ["client_id"] = ClientId, ["device_code"] = deviceCode, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, ct);
            var root = poll.RootElement;
            if (root.TryGetProperty("access_token", out var at) && at.GetString() is { Length: > 0 } token)
            {
                var login = await UserAsync(token, ct);
                Save(token, login, "device");
                return login ?? "";
            }
            switch (root.TryGetProperty("error", out var err) ? err.GetString() : null)
            {
                case "authorization_pending": break;
                case "slow_down": interval += 5; break;
                case "expired_token": throw new InvalidOperationException(L.T("The code expired before it was confirmed on GitHub. Try again."));
                case "access_denied": throw new InvalidOperationException(L.T("The sign-in was cancelled on GitHub."));
                case var other: throw new InvalidOperationException(L.F("GitHub refused the sign-in ({0}).", other ?? "?"));
            }
        }
        throw new InvalidOperationException(L.T("The code expired before it was confirmed on GitHub. Try again."));
    }

    static async Task<JsonDocument> PostAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await Http.Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The account a token belongs to, or null when GitHub does not take it.</summary>
    static async Task<string?> UserAsync(string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.Client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"GitHub account check: {ex.Message}");
            return null;
        }
    }
}
