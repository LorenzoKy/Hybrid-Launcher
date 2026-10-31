using System.Text;
using System.Text.Json;
using CG.Web.MegaApiClient;
using HybridLauncher.Core.Accounts;

namespace HybridLauncher.Core.Cloud;

/// <summary>
/// MEGA sign-in for cloud saves. MEGA has no OAuth: as rclone and MEGA's own tools do, the e-mail address and password (and
/// the two-factor code when the account has one) go once to MEGA's login, and only the session it opens is kept: its id and
/// the account's master key, encrypted for this Windows user. The password is never stored; signing out closes the session
/// at MEGA too.
/// </summary>
public static class MegaAuth
{
    const string Account = "mega";

    public static bool SignedIn => AccountTokens.Has(Account);

    public static string? Email => AccountTokens.Read(Account)?.GetValueOrDefault("email");

    public sealed class TwoFactorNeededException() : Exception(L.T("Enter the six-digit code from your authenticator app."));

    /// <summary>Opens a session; <see cref="TwoFactorNeededException"/> when the account asks for its code and none was given.</summary>
    public static Task SignInAsync(string email, string password, string? code, CancellationToken ct) => Task.Run(() =>
    {
        var client = MegaStore.NewClient();
        MegaApiClient.LogonSessionToken token;
        try { token = string.IsNullOrWhiteSpace(code) ? client.Login(email.Trim(), password) : client.Login(email.Trim(), password, code.Trim()); }
        catch (ApiException ex) when (ex.ApiResultCode == ApiResultCode.TwoFactorAuthenticationError && string.IsNullOrWhiteSpace(code)) { throw new TwoFactorNeededException(); }
        catch (ApiException ex) { throw new InvalidOperationException(MegaStore.Explain(ex)); }
        AccountTokens.Write(Account, new()
        {
            ["email"] = email.Trim(),
            ["session"] = token.SessionId,
            ["key"] = Convert.ToBase64String(token.MasterKey),
        });
        MegaStore.Adopt(client);
        Log.Info("Cloud: signed in to MEGA");
    }, ct);

    /// <summary>
    /// Signs in from the user's browser: Hybrid Launcher serves MEGA's sign-in form on a one-time port of 127.0.0.1 (MEGA has no OAuth
    /// to redirect through), the form posts back to Hybrid Launcher, and the two-factor code is asked on the same page when the account
    /// has one. The password stays in memory only for that page's life. Returns the account's e-mail address.
    /// </summary>
    public static async Task<string> SignInInBrowserAsync(Action<string> openBrowser, CancellationToken ct)
    {
        using var browser = new BrowserSignIn();
        (string Email, string Password)? pending = null;
        openBrowser(browser.Url);
        return await browser.ServeAsync<string>(async r =>
        {
            if (r.Method != "POST") { await BrowserSignIn.RespondAsync(r.Stream, Form(browser.Url, pending?.Email, null, pending is not null), ct); return null; }
            var email = (r.Form.GetValueOrDefault("email") ?? pending?.Email ?? "").Trim();
            var password = r.Form.GetValueOrDefault("password") is { Length: > 0 } p ? p : pending?.Password ?? "";
            var code = r.Form.GetValueOrDefault("code")?.Trim();
            if (email.Length == 0 || password.Length == 0)
            {
                pending = null;
                await BrowserSignIn.RespondAsync(r.Stream, Form(browser.Url, email, L.T("Enter your e-mail address and password."), false), ct);
                return null;
            }
            try
            {
                await SignInAsync(email, password, code, ct);
                await BrowserSignIn.MessageAsync(r.Stream, L.T("Signed in"), L.T("Hybrid Launcher is signed in to MEGA. You can close this tab."), ct);
                return email;
            }
            catch (TwoFactorNeededException ex)
            {
                pending = (email, password);
                await BrowserSignIn.RespondAsync(r.Stream, Form(browser.Url, email, string.IsNullOrEmpty(code) ? null : ex.Message, true), ct);
            }
            catch (InvalidOperationException ex)
            {
                // A wrong two-factor code asks for the code again; anything else starts over from the e-mail and password.
                bool again = pending is not null && !string.IsNullOrEmpty(code);
                if (!again) pending = null;
                await BrowserSignIn.RespondAsync(r.Stream, Form(browser.Url, email, ex.Message, again), ct);
            }
            return null;
        }, ct);
    }

    /// <summary>MEGA's sign-in page: e-mail and password, or (<paramref name="twoFactor"/>) the authenticator's code.</summary>
    static string Form(string action, string? email, string? error, bool twoFactor)
    {
        var e = BrowserSignIn.Html(email);
        var fields = twoFactor
            ? $"""<p>{BrowserSignIn.Html(L.F("Enter the six-digit code from your authenticator app for {0}.", email ?? "MEGA"))}</p><label for="code">{BrowserSignIn.Html(L.T("Two-factor code"))}</label><input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="6" required autofocus>"""
            : $"""<p>{BrowserSignIn.Html(L.T("Sign in to keep your saves in your MEGA Cloud Drive."))}</p><label for="email">{BrowserSignIn.Html(L.T("E-mail"))}</label><input id="email" name="email" type="email" autocomplete="username" value="{e}" required {(e.Length == 0 ? "autofocus" : "")}><label for="password">{BrowserSignIn.Html(L.T("Password"))}</label><input id="password" name="password" type="password" autocomplete="current-password" required {(e.Length > 0 ? "autofocus" : "")}>""";
        return $"""
            <h1>{BrowserSignIn.Html(L.T("Sign in to MEGA"))}</h1>
            <form method="post" action="{BrowserSignIn.Html(action)}" onsubmit="this.querySelector('button').disabled=true">
            {fields}
            {(error is null ? "" : $"""<p class="err">{BrowserSignIn.Html(error)}</p>""")}
            <button type="submit">{BrowserSignIn.Html(L.T("Sign in"))}</button>
            </form>
            <p class="note">{BrowserSignIn.Html(L.T("This page is served by Hybrid Launcher on your PC. Your e-mail and password go straight to MEGA, once: Hybrid Launcher keeps only the session MEGA opens, encrypted for your Windows user, never the password."))}</p>
            """;
    }

    internal static MegaApiClient.LogonSessionToken? Token()
    {
        var values = AccountTokens.Read(Account);
        if (values?.GetValueOrDefault("session") is not { } session || values.GetValueOrDefault("key") is not { } key) return null;
        return new MegaApiClient.LogonSessionToken(session, Convert.FromBase64String(key));
    }

    /// <summary>Closes the session at MEGA (when the network allows) and forgets it here.</summary>
    public static async Task SignOutAsync()
    {
        await MegaStore.Instance.LogoutAsync();
        AccountTokens.Delete(Account);
    }
}

/// <summary>
/// Cloud saves in MEGA: "Hybrid Cloud Saves\&lt;game&gt;" in the account's Cloud Drive holds save.zip, manifest.json and
/// history\, as the folder provider does (MEGA has no hidden app folder). Files are end-to-end encrypted by MEGA's client
/// library before they leave the PC.
/// </summary>
public sealed class MegaStore : ICloudStore
{
    public static readonly MegaStore Instance = new();

    const string RootName = FolderStore.RootName;

    public string Name => "MEGA";

    static MegaApiClient? _client;
    static readonly SemaphoreSlim Lock = new(1);

    internal static MegaApiClient NewClient() => new(new Options());

    internal static void Adopt(MegaApiClient client) => _client = client;

    internal static string Explain(ApiException ex) => ex.ApiResultCode switch
    {
        ApiResultCode.ResourceNotExists or ApiResultCode.BadArguments => L.T("MEGA did not accept that e-mail address and password."),
        ApiResultCode.TwoFactorAuthenticationError => L.T("MEGA did not accept that two-factor code."),
        ApiResultCode.TooManyRequests or ApiResultCode.ToManyRequestsForThisResource => L.T("MEGA asks to wait a little: try again in a minute."),
        ApiResultCode.BadSessionId => L.T("MEGA ended Hybrid Launcher's sign-in: sign in to MEGA again in Settings → Cloud saves."),
        ApiResultCode.QuotaExceeded => L.T("Your MEGA storage or transfer quota is used up."),
        ApiResultCode.ResourceAdministrativelyBlocked => L.T("MEGA has blocked this account."),
        _ => L.F("MEGA answered: {0}", ex.ApiResultCode),
    };

    /// <summary>Runs <paramref name="work"/> with a signed-in client, one call at a time (the client is not thread-safe).</summary>
    static async Task<T> RunAsync<T>(Func<MegaApiClient, T> work, CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (_client is not { IsLoggedIn: true })
                    {
                        var token = MegaAuth.Token() ?? throw new InvalidOperationException(L.T("Sign in to MEGA again in Settings → Cloud saves."));
                        var client = NewClient();
                        Try(() => { client.Login(token); return true; }, ct);
                        _client = client;
                    }
                    return work(_client);
                }
                catch (ApiException ex)
                {
                    if (ex.ApiResultCode == ApiResultCode.BadSessionId) _client = null;
                    throw new InvalidOperationException(Explain(ex));
                }
            }, ct);
        }
        finally { Lock.Release(); }
    }

    /// <summary>
    /// One MEGA call, sent again when MEGA asks for it ("try again", "too many requests", a passing server error): MEGA answers
    /// that way now and then, most often right after a file was added. Each call is retried on its own, so an upload already
    /// done is never repeated.
    /// </summary>
    static T Try<T>(Func<T> call, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return call(); }
            catch (ApiException ex) when (attempt < 6 && ex.ApiResultCode is ApiResultCode.RequestFailedRetry or ApiResultCode.TooManyRequests
                or ApiResultCode.ToManyRequestsForThisResource or ApiResultCode.ResourceTemporarilyNotAvailable or ApiResultCode.InternalError
                or ApiResultCode.TooManyConnectionsOnThisResource)
            {
                Log.Info($"MEGA: {ex.ApiResultCode}, again in a moment");
                ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500 << attempt));
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    internal async Task LogoutAsync()
    {
        await Lock.WaitAsync();
        try
        {
            var client = _client;
            _client = null;
            if (client is null && MegaAuth.Token() is { } token)
            {
                client = NewClient();
                try { await Task.Run(() => client.Login(token)); } catch { client = null; }
            }
            if (client is { IsLoggedIn: true }) await Task.Run(client.Logout);
        }
        catch (Exception ex) { Log.Warn($"MEGA sign-out: {ex.Message}"); }
        finally { Lock.Release(); }
    }

    /// <summary>The game's folder and what it holds (made when <paramref name="create"/>); null while the game has none.</summary>
    sealed record GameNodes(INode Dir, INode? Save, INode? Manifest, INode? History, List<INode> Old, List<INode> Partial);

    static GameNodes? Find(MegaApiClient client, string key, bool create, CancellationToken ct)
    {
        var nodes = Try(() => client.GetNodes().ToList(), ct);
        var root = nodes.Single(n => n.Type == NodeType.Root);
        INode? Child(INode parent, string name, NodeType type) =>
            nodes.Where(n => n.ParentId == parent.Id && n.Type == type && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)).MaxBy(n => n.ModificationDate ?? n.CreationDate);
        var top = Child(root, RootName, NodeType.Directory);
        // Saves sent under an earlier folder name are in the old folder: it takes the new name.
        if (top is null && FolderStore.LegacyRootNames.Select(n => Child(root, n, NodeType.Directory)).FirstOrDefault(n => n is not null) is { } legacy)
            top = Try(() => client.Rename(legacy, RootName), ct);
        top ??= create ? Try(() => client.CreateFolder(RootName, root), ct) : null;
        if (top is null) return null;
        var dir = Child(top, key, NodeType.Directory) ?? (create ? Try(() => client.CreateFolder(key, top), ct) : null);
        if (dir is null) return null;
        var history = Child(dir, "history", NodeType.Directory);
        var old = history is null ? [] : nodes.Where(n => n.ParentId == history.Id && n.Type == NodeType.File).ToList();
        var partial = nodes.Where(n => n.ParentId == dir.Id && n.Type == NodeType.File && n.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)).ToList();
        return new GameNodes(dir, Child(dir, "save.zip", NodeType.File), Child(dir, "manifest.json", NodeType.File), history, old, partial);
    }

    public Task<CloudManifest?> ManifestAsync(string key, CancellationToken ct) => RunAsync(client =>
    {
        if (Find(client, key, create: false, ct) is not { Save: not null, Manifest: { } m }) return null;
        return Try(() =>
        {
            using var stream = client.Download(m, ct);
            return JsonSerializer.Deserialize(stream, HybridJson.Default.CloudManifest);
        }, ct);
    }, ct);

    public Task PutAsync(string key, string zip, CloudManifest manifest, CloudManifest? old, int keep, CancellationToken ct) => RunAsync(client =>
    {
        var game = Find(client, key, create: true, ct)!;
        // What an upload cut short left behind.
        foreach (var n in game.Partial) Try(() => { client.Delete(n, moveToTrash: false); return true; }, ct);
        // The new copy goes up under a temporary name first: the current one stays whole until it is safely there.
        var uploaded = Try(() =>
        {
            using var file = File.OpenRead(zip);
            return client.Upload(file, "save.zip.partial", game.Dir, DateTime.UtcNow, ct);
        }, ct);
        if (game.Save is { } current)
        {
            var history = game.History ?? Try(() => client.CreateFolder("history", game.Dir), ct);
            var moved = Try(() => client.Move(current, history), ct);
            Try(() => client.Rename(moved, (old?.HistoryName ?? $"{DateTime.Now:yyyy-MM-dd HH-mm-ss.fff}") + ".zip"), ct);
        }
        Try(() => client.Rename(uploaded, "save.zip"), ct);
        var text = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, HybridJson.Default.CloudManifest));
        Try(() =>
        {
            using var json = new MemoryStream(text);
            return client.Upload(json, "manifest.json", game.Dir, DateTime.UtcNow, ct);
        }, ct);
        if (game.Manifest is { } before) Try(() => { client.Delete(before, moveToTrash: false); return true; }, ct);
        // Older copies past the limit: the history after this upload holds the old ones plus the copy just moved there.
        foreach (var n in game.Old.OrderByDescending(n => n.Name, StringComparer.Ordinal).Skip(Math.Max(0, keep - (game.Save is null ? 0 : 1))))
            Try(() => { client.Delete(n, moveToTrash: false); return true; }, ct);
        return true;
    }, ct);

    public Task GetAsync(string key, string? copyId, string toFile, CancellationToken ct) => RunAsync(client =>
    {
        var game = Find(client, key, create: false, ct);
        var node = copyId is null ? game?.Save : game?.Old.FirstOrDefault(n => n.Id == copyId);
        if (node is null) throw new FileNotFoundException(L.T("The cloud has no saves for this game yet."));
        return Try(() =>
        {
            using var stream = client.Download(node, ct);
            using var file = File.Create(toFile);
            stream.CopyTo(file);
            return true;
        }, ct);
    }, ct);

    public Task<List<CloudCopy>> HistoryAsync(string key, CancellationToken ct) => RunAsync(client =>
        Find(client, key, create: false, ct)?.Old.Select(n => new CloudCopy(n.Id, Path.GetFileNameWithoutExtension(n.Name), n.Size)).ToList() ?? [], ct);

    public Task DeleteAsync(string key, CancellationToken ct) => RunAsync(client =>
    {
        if (Find(client, key, create: false, ct) is { } game) Try(() => { client.Delete(game.Dir, moveToTrash: false); return true; }, ct);
        return true;
    }, ct);
}
