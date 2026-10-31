using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace HybridLauncher.Core.Accounts;

/// <summary>
/// A Steam account signed in to Hybrid Launcher through Steam's own network (SteamKit2), as the Steam client does: the user name and
/// password go to Steam's authentication once (Steam Guard answered in Hybrid Launcher, or approved in the Steam mobile app) and only
/// the refresh token Steam hands back is kept. With the session Hybrid Launcher lists every licence the account holds and downloads
/// a game's depots from Steam's CDN, decrypted with the depot keys Steam gives the account.
/// Games with Steam DRM still need the Steam client running to start: Hybrid Launcher installs them, Steam has to be open to play.
/// </summary>
public static class SteamAccount
{
    public static bool SignedIn => AccountTokens.Has(GameStore.Steam);

    /// <summary>What Steam Guard asks during the sign-in; the UI answers (a code typed, or "approve in the app").</summary>
    public interface IGuard
    {
        Task<string> CodeAsync(bool email, string? hint, bool retry);
        /// <summary>Steam waits for an approval in the Steam mobile app; true to wait for it, false to type a code instead.</summary>
        Task<bool> ApproveInAppAsync();
    }

    sealed class Authenticator(IGuard guard) : IAuthenticator
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => guard.CodeAsync(false, null, previousCodeWasIncorrect);
        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => guard.CodeAsync(true, email, previousCodeWasIncorrect);
        public Task<bool> AcceptDeviceConfirmationAsync() => guard.ApproveInAppAsync();
    }

    /// <summary>One connection to Steam for the whole session, its callbacks pumped in the background.</summary>
    sealed class Session
    {
        // Plain TCP to Steam's servers, as the Steam client: SteamKit2's WebSocket transport could drop the connection right after
        // connecting (its socket failing to dispose), which cancelled the sign-in with "A task was canceled".
        public readonly SteamClient Client = new(SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.Tcp).WithConnectionTimeout(TimeSpan.FromSeconds(15))));
        public readonly CallbackManager Manager;
        public readonly SteamUser User;
        public readonly SteamApps Apps;
        public readonly SteamContent Content;
        public readonly SteamFriends Friends;
        // The friends list Steam sends after the sign-in, and what each friend is doing (name, state, game), as it changes.
        public readonly TaskCompletionSource FriendsListed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly HashSet<ulong> FriendIds = [];
        public readonly Dictionary<ulong, SteamFriend> Personas = [];
        public readonly CancellationTokenSource Pump = new();
        public TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SteamUser.LoggedOnCallback> LoggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<List<SteamApps.LicenseListCallback.License>> Licenses = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Alive;
        public uint CellId;

        public Session()
        {
            Manager = new CallbackManager(Client);
            // A drop before the sign-in got that far fails these with nobody waiting: observed, not logged as unobserved errors.
            _ = Connected.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = LoggedOn.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            User = Client.GetHandler<SteamUser>()!;
            Apps = Client.GetHandler<SteamApps>()!;
            Content = Client.GetHandler<SteamContent>()!;
            Friends = Client.GetHandler<SteamFriends>()!;
            Manager.Subscribe<SteamClient.ConnectedCallback>(_ => { Alive = true; Connected.TrySetResult(); });
            Manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            {
                Alive = false;
                Connected.TrySetException(new InvalidOperationException(L.T("Could not reach Steam.")));
                LoggedOn.TrySetException(new InvalidOperationException(L.T("Steam closed the connection.")));
            });
            Manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
            {
                CellId = cb.CellID;
                LoggedOn.TrySetResult(cb);
            });
            // Steam ending the session (signed in elsewhere with the same LoginID, token revoked): the next call signs in again.
            Manager.Subscribe<SteamUser.LoggedOffCallback>(cb =>
            {
                Alive = false;
                Log.Warn($"Steam signed Hybrid Launcher's session off: {cb.Result}");
                LoggedOn.TrySetException(new InvalidOperationException(L.F("Steam ended the session ({0}).", cb.Result)));
            });
            Manager.Subscribe<SteamApps.LicenseListCallback>(cb => Licenses.TrySetResult(cb.LicenseList.ToList()));
            Manager.Subscribe<SteamFriends.FriendsListCallback>(cb =>
            {
                lock (FriendIds)
                {
                    if (!cb.Incremental) FriendIds.Clear();
                    foreach (var f in cb.FriendList.Where(f => f.SteamID.IsIndividualAccount))
                        if (f.Relationship == EFriendRelationship.Friend) FriendIds.Add(f.SteamID.ConvertToUInt64());
                        else FriendIds.Remove(f.SteamID.ConvertToUInt64());
                }
                FriendsListed.TrySetResult();
                FriendsChanged?.Invoke();
            });
            Manager.Subscribe<SteamFriends.PersonaStateCallback>(cb =>
            {
                if (!cb.FriendID.IsIndividualAccount) return;
                var id = cb.FriendID.ConvertToUInt64();
                var hash = cb.AvatarHash is { Length: > 0 } h && h.Any(b => b != 0) ? Convert.ToHexString(h).ToLowerInvariant() : null;
                lock (FriendIds)
                {
                    Personas.TryGetValue(id, out var before);
                    Personas[id] = new SteamFriend(id, cb.Name ?? before?.Name ?? "", hash is null ? before?.AvatarUrl : $"https://avatars.steamstatic.com/{hash}_medium.jpg",
                        cb.State, cb.GameAppID, cb.GameName is { Length: > 0 } g ? g : null, cb.LastLogOff == DateTime.MinValue ? before?.LastOnline : cb.LastLogOff);
                }
                FriendsChanged?.Invoke();
            });
            _ = Task.Run(async () =>
            {
                while (!Pump.IsCancellationRequested)
                {
                    try { await Manager.RunWaitCallbackAsync(Pump.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Log.Warn($"Steam callbacks: {ex.Message}"); }
                }
            });
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            Client.Connect();
            await Connected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }

        public void Close()
        {
            try { Client.Disconnect(); } catch { }
            Pump.Cancel();
        }
    }

    /// <summary>"VNM": the LoginID Hybrid Launcher's sessions use, apart from the Steam client's (see <see cref="LogOnAsync"/>).</summary>
    const uint AppLoginId = 0x48474C;

    static Session? _session;
    static readonly SemaphoreSlim SessionLock = new(1);

    /// <summary>Signs in with the account's name and password (never stored); returns the name shown for the account.</summary>
    public static async Task<string> SignInAsync(string user, string password, IGuard guard, CancellationToken ct)
    {
        await SessionLock.WaitAsync(ct);
        try
        {
            _session?.Close();
            var s = _session = new Session();
            await s.ConnectAsync(ct);
            var auth = await s.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = user,
                Password = password,
                IsPersistentSession = true,
                DeviceFriendlyName = "Hybrid Launcher",
                GuardData = AccountTokens.Read(GameStore.Steam)?.GetValueOrDefault("guard"),
                Authenticator = new Authenticator(guard),
            });
            var result = await auth.PollingWaitForResultAsync(ct);
            var tokens = new Dictionary<string, string> { ["account"] = result.AccountName, ["refresh"] = result.RefreshToken };
            if (result.NewGuardData is { } g) tokens["guard"] = g;
            await LogOnAsync(s, result.AccountName, result.RefreshToken, ct);
            AccountTokens.Write(GameStore.Steam, tokens);
            return result.AccountName;
        }
        catch
        {
            _session?.Close();
            _session = null;
            throw;
        }
        finally { SessionLock.Release(); }
    }

    /// <summary>
    /// Signs in the way the Steam client's own sign-in window offers first: a QR code scanned with the Steam mobile app, no
    /// name or password typed anywhere. <paramref name="showCode"/> gets the address to draw as a QR code (again each time Steam
    /// renews it); returns the account's name once the sign-in is approved in the app.
    /// </summary>
    public static async Task<string> SignInWithQrAsync(Action<string> showCode, CancellationToken ct)
    {
        await SessionLock.WaitAsync(ct);
        try
        {
            _session?.Close();
            var s = _session = new Session();
            await s.ConnectAsync(ct);
            var auth = await s.Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                IsPersistentSession = true,
                DeviceFriendlyName = "Hybrid Launcher",
            });
            auth.ChallengeURLChanged = () => showCode(auth.ChallengeURL);
            showCode(auth.ChallengeURL);
            var result = await auth.PollingWaitForResultAsync(ct);
            var tokens = new Dictionary<string, string> { ["account"] = result.AccountName, ["refresh"] = result.RefreshToken };
            if (result.NewGuardData is { } g) tokens["guard"] = g;
            await LogOnAsync(s, result.AccountName, result.RefreshToken, ct);
            AccountTokens.Write(GameStore.Steam, tokens);
            return result.AccountName;
        }
        catch
        {
            _session?.Close();
            _session = null;
            throw;
        }
        finally { SessionLock.Release(); }
    }

    /// <summary>Connects to Steam and signs in anonymously, then disconnects: checks Steam can be reached (smoke test).</summary>
    public static async Task<string> ProbeAsync(CancellationToken ct)
    {
        var s = new Session();
        try
        {
            await s.ConnectAsync(ct);
            s.User.LogOnAnonymous();
            var logged = await s.LoggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return logged.Result.ToString();
        }
        finally { s.Close(); }
    }

    /// <summary>Connects and asks Steam for a sign-in QR code, then disconnects: what the sign-in window does first (smoke test).</summary>
    public static async Task<string> QrProbeAsync(CancellationToken ct)
    {
        var s = new Session();
        try
        {
            await s.ConnectAsync(ct);
            var auth = await s.Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails { IsPersistentSession = true, DeviceFriendlyName = "Hybrid Launcher" });
            return auth.ChallengeURL;
        }
        finally { s.Close(); }
    }

    public static void SignOut()
    {
        _session?.Close();
        _session = null;
        AccountTokens.Delete(GameStore.Steam);
    }

    static async Task LogOnAsync(Session s, string account, string refresh, CancellationToken ct)
    {
        // A LoginID of Hybrid Launcher's own: SteamKit2's default is drawn from this PC, the same number the Steam client uses, and Steam
        // allows one session per LoginID and account from one address: it signed the Steam client out (offline) as Hybrid Launcher signed in.
        s.User.LogOn(new SteamUser.LogOnDetails { Username = account, AccessToken = refresh, ShouldRememberPassword = true, LoginID = AppLoginId });
        var logged = await s.LoggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (logged.Result != EResult.OK)
        {
            if (logged.Result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked) AccountTokens.Delete(GameStore.Steam);
            throw new InvalidOperationException(L.F("Steam refused the sign-in ({0}). Sign in again from Downloads → Accounts.", logged.Result));
        }
    }

    /// <summary>A connected, signed-in session (made from the saved refresh token when there is none).</summary>
    static async Task<Session> SessionAsync(CancellationToken ct)
    {
        await SessionLock.WaitAsync(ct);
        try
        {
            if (_session is { Alive: true } live && live.LoggedOn.Task.IsCompletedSuccessfully) return live;
            var t = AccountTokens.Read(GameStore.Steam) ?? throw new InvalidOperationException(L.T("Sign in to Steam first (Downloads → Accounts)."));
            _session?.Close();
            var s = _session = new Session();
            await s.ConnectAsync(ct);
            await LogOnAsync(s, t["account"], t["refresh"], ct);
            return s;
        }
        finally { SessionLock.Release(); }
    }

    // ---------- friends ----------

    /// <summary>A friend's list or state changed (raised on SteamKit2's thread).</summary>
    public static event Action? FriendsChanged;

    static readonly Dictionary<uint, string> AppNames = [];

    /// <summary>
    /// The account's friends with what they are doing, online first, then by name. Hybrid Launcher's own session stays as it signs in
    /// (it never sets a state of its own, so you do not show up "online" twice): their states are asked of Steam directly.
    /// </summary>
    public static async Task<List<SteamFriend>> FriendsAsync(CancellationToken ct)
    {
        var s = await SessionAsync(ct);
        await s.FriendsListed.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        List<ulong> ids;
        lock (s.FriendIds) ids = [.. s.FriendIds];
        if (ids.Count > 0)
        {
            s.Friends.RequestFriendInfo(ids.Select(i => new SteamID(i)), EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.Presence
                | EClientPersonaStateFlag.LastSeen | EClientPersonaStateFlag.GameExtraInfo | EClientPersonaStateFlag.Status | EClientPersonaStateFlag.SourceID);
            // The answers come as separate messages: a short wait collects the first wave (later ones raise FriendsChanged).
            for (int i = 0; i < 20; i++)
            {
                int known;
                lock (s.FriendIds) known = ids.Count(s.Personas.ContainsKey);
                if (known >= ids.Count) break;
                await Task.Delay(150, ct);
            }
        }
        var list = Snapshot(s);
        // Steam games: the name from the app's info (Steam sends only the id for them), once per app.
        var missing = list.Where(f => f.PlayingAppId != 0 && f.PlayingName is null).Select(f => f.PlayingAppId).Distinct().Where(a => !AppNames.ContainsKey(a)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                foreach (var (id, kv) in await AppInfoAsync(s, missing, ct))
                    if (kv["common"]["name"].Value is { Length: > 0 } n) lock (AppNames) AppNames[id] = n;
            }
            catch (Exception ex) { Log.Warn($"Steam friends' games: {ex.Message}"); }
        }
        return Snapshot(s);
    }

    static List<SteamFriend> Snapshot(Session s)
    {
        lock (s.FriendIds)
        {
            return s.FriendIds.Select(id => s.Personas.TryGetValue(id, out var p) ? p : new SteamFriend(id, id.ToString(), null, EPersonaState.Offline, 0, null, null))
                .Select(f => f.PlayingName is null && f.PlayingAppId != 0 && AppNames.TryGetValue(f.PlayingAppId, out var n) ? f with { PlayingName = n } : f)
                .OrderBy(f => f.Rank).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    // ---------- library ----------

    /// <summary>Every game the account has a licence for: packages → their apps → the apps whose type is "game".</summary>
    public static async Task<List<OwnedGame>> LibraryAsync(CancellationToken ct)
    {
        var s = await SessionAsync(ct);
        var licenses = await s.Licenses.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var packages = licenses.Select(l => new SteamApps.PICSRequest(l.PackageID, l.AccessToken)).ToList();
        var appIds = new HashSet<uint>();
        foreach (var batch in packages.Chunk(200))
        {
            var info = await s.Apps.PICSGetProductInfo([], batch);
            foreach (var r in info.Results ?? [])
                foreach (var p in r.Packages.Values)
                    foreach (var a in p.KeyValues["appids"].Children)
                        if (uint.TryParse(a.Value, out var id)) appIds.Add(id);
        }
        var list = new List<OwnedGame>();
        foreach (var (id, kv) in await AppInfoAsync(s, appIds, ct))
        {
            var common = kv["common"];
            if (!string.Equals(common["type"].Value, "game", StringComparison.OrdinalIgnoreCase) || common["name"].Value is not { Length: > 0 } name) continue;
            var app = id.ToString();
            list.Add(new OwnedGame(GameStore.Steam, $"steam:{app}", name,
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{app}/library_600x900.jpg",
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{app}/header.jpg",
                $"steam://install/{app}", $"https://store.steampowered.com/app/{app}"));
        }
        return list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static async Task<Dictionary<uint, KeyValue>> AppInfoAsync(Session s, IEnumerable<uint> apps, CancellationToken ct)
    {
        var result = new Dictionary<uint, KeyValue>();
        foreach (var batch in apps.Chunk(200))
        {
            var tokens = await s.Apps.PICSGetAccessTokens(batch, []);
            var requests = batch.Select(a => new SteamApps.PICSRequest(a, tokens.AppTokens.TryGetValue(a, out var t) ? t : 0)).ToList();
            var info = await s.Apps.PICSGetProductInfo(requests, []);
            foreach (var r in info.Results ?? [])
                foreach (var (id, app) in r.Apps) result[id] = app.KeyValues;
            ct.ThrowIfCancellationRequested();
        }
        return result;
    }

    /// <summary>The game's current public build id on Steam (what an up-to-date install has in its app manifest).</summary>
    public static async Task<string?> LatestBuildAsync(string appId, CancellationToken ct)
    {
        var s = await SessionAsync(ct);
        var id = uint.Parse(appId);
        var kv = (await AppInfoAsync(s, [id], ct)).GetValueOrDefault(id);
        return kv?["depots"]["branches"]["public"]["buildid"].Value;
    }

    // ---------- download ----------

    /// <summary>
    /// The game's public build for Windows: its depots in English (or for every language), 64-bit when there is a choice,
    /// each as the files of its manifest, chunk by chunk from Steam's CDN.
    /// </summary>
    public static async Task<DownloadPlan> PlanAsync(OwnedGame game, string root, CancellationToken ct)
    {
        var s = await SessionAsync(ct);
        var appId = uint.Parse(game.Id["steam:".Length..]);
        var kv = (await AppInfoAsync(s, [appId], ct)).GetValueOrDefault(appId) ?? throw new InvalidOperationException(L.F("Steam has no data for {0}.", game.Name));
        var config = kv["config"];
        var folder = DirectDownloader.FolderName(config["installdir"].Value is { Length: > 0 } dir ? dir : game.Name);

        var plan = new DownloadPlan { InstallDir = Path.Combine(root, folder), Version = kv["depots"]["branches"]["public"]["buildid"].Value };
        // The launch entry for Windows (the first one without a type, or the "default" one).
        foreach (var l in config["launch"].Children)
        {
            var os = l["config"]["oslist"].Value;
            if (os is { Length: > 0 } && !os.Contains("windows", StringComparison.OrdinalIgnoreCase)) continue;
            if (l["type"].Value is { Length: > 0 } type && type != "default") continue;
            plan.Exe = l["executable"].Value?.Replace('/', '\\');
            plan.Args = l["arguments"].Value;
            plan.WorkingDir = l["workingdir"].Value?.Replace('/', '\\');
            break;
        }

        var depots = new List<(uint Id, ulong Manifest)>();
        bool any64 = kv["depots"].Children.Any(d => d["config"]["osarch"].Value == "64");
        foreach (var d in kv["depots"].Children)
        {
            if (!uint.TryParse(d.Name, out var depotId)) continue;
            var c = d["config"];
            if (c["oslist"].Value is { Length: > 0 } os && !os.Contains("windows", StringComparison.OrdinalIgnoreCase)) continue;
            if (c["language"].Value is { Length: > 0 } lang && lang != "english") continue;
            if (any64 && c["osarch"].Value == "32") continue;
            if (d["depotfromapp"].Value is { Length: > 0 } || d["sharedinstall"].Value == "1") continue;
            if (d["dlcappid"].Value is { Length: > 0 }) continue;
            var gid = d["manifests"]["public"]["gid"].Value ?? d["manifests"]["public"].Value;
            if (!ulong.TryParse(gid, out var manifestId)) continue;
            depots.Add((depotId, manifestId));
        }
        if (depots.Count == 0) throw new InvalidOperationException(L.F("Steam lists no Windows files for {0} that this account can download.", game.Name));

        var servers = (await s.Content.GetServersForSteamPipe(s.CellId)).Where(x => x.Type is "SteamCache" or "CDN").ToList();
        if (servers.Count == 0) throw new InvalidOperationException(L.T("Steam gave no download servers."));
        var cdn = new Client(s.Client);
        int next = 0;
        Server PickServer() => servers[(int)((uint)Interlocked.Increment(ref next) % (uint)servers.Count)];

        foreach (var (depotId, manifestId) in depots)
        {
            var key = await s.Apps.GetDepotDecryptionKey(depotId, appId);
            if (key.Result != EResult.OK) { Log.Warn($"Steam depot {depotId}: no key ({key.Result})"); continue; }
            var code = await s.Content.GetManifestRequestCode(depotId, appId, manifestId, "public");
            DepotManifest? manifest = null;
            for (int i = 0; i < 4 && manifest is null; i++)
            {
                try { manifest = await cdn.DownloadManifestAsync(depotId, manifestId, code, PickServer(), key.DepotKey); }
                catch (Exception ex) when (i < 3) { Log.Warn($"Steam manifest {depotId}: {ex.Message}"); }
            }
            if (manifest!.FilenamesEncrypted) manifest.DecryptFilenames(key.DepotKey);
            var depotKey = key.DepotKey;
            foreach (var f in manifest.Files ?? [])
            {
                var path = f.FileName.Replace('/', '\\');
                if (f.Flags.HasFlag(EDepotFileFlag.Directory)) { plan.Files.Add(new PlanFile { Path = path, Directory = true }); continue; }
                var chunks = f.Chunks.OrderBy(c => c.Offset).Select(c => new PlanChunk((long)c.Offset, (int)c.UncompressedLength, async _ =>
                {
                    var buffer = new byte[c.UncompressedLength];
                    int written = await cdn.DownloadDepotChunkAsync(depotId, c, PickServer(), buffer, depotKey);
                    return written == buffer.Length ? buffer : buffer[..written];
                })).ToList();
                plan.Files.Add(new PlanFile { Path = path, Size = (long)f.TotalSize, Chunks = chunks, Sha1 = f.FileHash is { Length: 20 } h ? Convert.ToHexString(h) : null });
            }
        }
        // Steamworks games look for their app id next to the exe when they are not started by Steam itself.
        var appText = appId.ToString();
        plan.Files.RemoveAll(f => f.Path.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase) && f.Chunks.Count == 0);
        var finishExe = plan.Exe;
        var name = kv["common"]["name"].Value ?? game.Name;
        return new DownloadPlan
        {
            InstallDir = plan.InstallDir, Files = plan.Files, Exe = plan.Exe, Args = plan.Args, WorkingDir = plan.WorkingDir, Version = plan.Version,
            Finish = p =>
            {
                try
                {
                    var dirOfExe = finishExe is null ? p.InstallDir : Path.GetDirectoryName(DirectDownloader.Full(p.InstallDir, finishExe))!;
                    var file = Path.Combine(dirOfExe, "steam_appid.txt");
                    if (!File.Exists(file)) File.WriteAllText(file, appText);
                }
                catch (Exception ex) { Log.Warn($"steam_appid.txt: {ex.Message}"); }
                if (WriteAppManifest(p.InstallDir, appText, name, p.Version, depots, p.Files.Sum(f => f.Size))
                    && Detection.SteamScanner.ClientRunning() && !Detection.SteamScanner.ClientBusy())
                    // Steam reads app manifests only when it starts: reopen it (minimized) so the game shows as installed.
                    _ = Detection.SteamScanner.RestartClientAsync("-silent");
            },
        };
    }

    /// <summary>
    /// In a Steam library (…\steamapps\common\&lt;game&gt;), the app manifest Steam writes for its own installs, so the Steam client
    /// sees the game as installed, up to date, and keeps it from then on (updates, verify, uninstall), like one it installed.
    /// </summary>
    static bool WriteAppManifest(string installDir, string appId, string name, string? buildId, List<(uint Id, ulong Manifest)> depots, long size)
    {
        try
        {
            var common = Path.GetDirectoryName(installDir);
            if (common is null || !Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase)) return false;
            var steamapps = Path.GetDirectoryName(common)!;
            if (!Path.GetFileName(steamapps).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return false;
            var file = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
            if (File.Exists(file)) return false;
            static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\"AppState\"").AppendLine("{");
            void Line(string k, string v) => sb.Append('\t').Append(Q(k)).Append("\t\t").AppendLine(Q(v));
            Line("appid", appId);
            Line("universe", "1");
            Line("name", name);
            Line("StateFlags", "4");
            Line("installdir", Path.GetFileName(installDir));
            Line("LastUpdated", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            Line("SizeOnDisk", size.ToString());
            Line("buildid", buildId ?? "0");
            Line("LastOwner", "0");
            Line("AutoUpdateBehavior", "0");
            Line("AllowOtherDownloadsWhileRunning", "0");
            Line("ScheduledAutoUpdate", "0");
            sb.AppendLine("\t\"InstalledDepots\"").AppendLine("\t{");
            foreach (var (id, manifest) in depots)
                sb.Append("\t\t").AppendLine(Q(id.ToString())).AppendLine("\t\t{").Append("\t\t\t").Append(Q("manifest")).Append("\t\t").AppendLine(Q(manifest.ToString())).AppendLine("\t\t}");
            sb.AppendLine("\t}").AppendLine("}");
            File.WriteAllText(file, sb.ToString());
            Log.Info($"Steam app manifest written: {file}");
            return true;
        }
        catch (Exception ex) { Log.Warn($"Steam app manifest for {appId}: {ex.Message}"); return false; }
    }
}

/// <summary>One Steam friend as Hybrid Launcher shows it: name, avatar, online state and the game they are in.</summary>
public sealed record SteamFriend(ulong SteamId, string Name, string? AvatarUrl, EPersonaState State, uint PlayingAppId, string? PlayingName, DateTime? LastOnline)
{
    public bool InGame => PlayingAppId != 0 || PlayingName is not null;
    public bool Online => State != EPersonaState.Offline;
    /// <summary>In a game first, then online, away, offline.</summary>
    public int Rank => InGame && Online ? 0 : State == EPersonaState.Online || State == EPersonaState.LookingToPlay || State == EPersonaState.LookingToTrade ? 1 : Online ? 2 : 3;
    public string ProfileUrl => $"https://steamcommunity.com/profiles/{SteamId}";
}
