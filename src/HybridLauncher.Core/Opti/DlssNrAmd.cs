using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Opti;

/// <summary>A DLSSNR-AMD release on GitLab: its tag, day and the Windows package.</summary>
public sealed record DlssNrAmdRelease(string Tag, DateTime Date, string AssetName, string Url);

/// <summary>What Hybrid Launcher prepared from a DLSSNR-AMD package: the release and the networks whose weights were made.</summary>
public sealed class DlssNrAmdStage
{
    public string Tag { get; set; } = "";
    public DateTime PreparedAt { get; set; }
    /// <summary>The nvngx_dlssnr.dll version the weights were read from (310.9.16 or 310.8.0).</summary>
    public string ModelVersion { get; set; } = "";
    public List<string> Networks { get; set; } = [];
}

/// <summary>
/// mochizuki0323's DLSSNR-AMD (gitlab.com/mochizuki0323/DLSSNR-AMD): DLSS 5 neural rendering on Radeon RX 9000 (and RX 7000,
/// experimental) through Vulkan, as an .asi plugin OptiScaler loads. Its weights come from the user's nvngx_dlssnr.dll, read
/// (never run) by the package's own tools.
///
/// Hybrid Launcher runs the package's install.ps1 once, unattended, into a scratch folder (a stand-in exe, the OptiScaler
/// route): it makes and checks every network's weights exactly as the author's installer does. What a game needs from that
/// (the dlssnr-amd folder, the plugin, the ini templates) is kept as the "stage"; installing into a game lays the stage over
/// Hybrid Launcher's own OptiScaler install (big files hard-linked when on the same drive), the plugin into OptiScaler's
/// plugins folder, dlssnr-amd.ini, and two keys of OptiScaler.ini. Uninstalling OptiScaler takes it all out.
/// </summary>
public static class DlssNrAmd
{
    const string Api = "https://gitlab.com/api/v4/projects/mochizuki0323%2FDLSSNR-AMD/releases?per_page=5";
    public const string Page = "https://gitlab.com/mochizuki0323/DLSSNR-AMD";
    public const string Folder = "dlssnr-amd", IniName = "dlssnr-amd.ini", PluginName = "dlssnr-amd.asi";

    /// <summary>The four networks, in the order the package names them.</summary>
    public static readonly string[] AllNetworks = ["CC_SMALL_FP8", "CC_SMALL_FP8_EXPERIMENTAL", "CC_SILVER_AARDWOLD", "CC_SILVER_AARDWOLD_OPTIMIZED"];

    static string Root => AppInfo.Ensure(Path.Combine(AppInfo.DataRoot, "DlssNrAmd"));
    static string StageDir => Path.Combine(Root, "stage");
    static string StageFile => Path.Combine(StageDir, "hl-stage.json");
    static string SavedIniDir => AppInfo.Ensure(Path.Combine(Root, "ini"));

    // ---------- the release ----------

    static DlssNrAmdRelease? _latest;

    /// <summary>The newest release with a Windows package (asked once a session).</summary>
    public static async Task<DlssNrAmdRelease?> LatestAsync(CancellationToken ct, bool fresh = false)
    {
        if (_latest is not null && !fresh) return _latest;
        var json = await Http.Client.GetStringAsync(Api, ct);
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            DateTime.TryParse(r.TryGetProperty("released_at", out var d) ? d.GetString() : null, out var date);
            if (!r.TryGetProperty("assets", out var assets) || !assets.TryGetProperty("links", out var links)) continue;
            foreach (var l in links.EnumerateArray())
            {
                var name = l.GetProperty("name").GetString() ?? "";
                if (!name.Contains("Windows", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var url = (l.TryGetProperty("direct_asset_url", out var u) ? u.GetString() : null) ?? l.GetProperty("url").GetString() ?? "";
                return _latest = new DlssNrAmdRelease(tag, date, name, url);
            }
        }
        return null;
    }

    // ---------- the stage ----------

    /// <summary>What is prepared, or null when nothing is (or it is incomplete).</summary>
    public static DlssNrAmdStage? Stage()
    {
        try
        {
            if (!File.Exists(StageFile) || !File.Exists(Path.Combine(StageDir, PluginName))) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(StageFile), HybridJson.Default.DlssNrAmdStage);
        }
        catch { return null; }
    }

    /// <summary>The package folders the ReShade routes need, kept in the stage.</summary>
    static readonly string[] RoutePackageParts = ["reshade", "vulkan", "dxvk"];

    /// <summary>The ReShade routes: DirectX 10/11/12 (ReShade as dxgi.dll), DirectX 9 (DXVK, ReShade as a Vulkan layer), Vulkan.</summary>
    public static readonly string[] ReShadeRoutes = ["reshade-feeder", "reshade-feeder-dx9", "reshade-feeder-vulkan"];

    /// <summary>The stage has the ReShade routes' files (prepared by 1.4.0-rc.20 or later).</summary>
    public static bool StageHasReShade() => Stage() is not null && File.Exists(Path.Combine(StageDir, "reshade", "dlssnr_amd.addon64"))
        && File.Exists(Path.Combine(StageDir, "reshade", "ReShade64.dll"));

    /// <summary>The route that fits a game's graphics APIs ("DX12", "DX9", "Vulkan"…): Vulkan alone, DirectX 9 alone, else DirectX 10-12.</summary>
    public static string RouteFor(IEnumerable<string> apis)
    {
        var list = apis.Select(a => a.ToUpperInvariant()).ToList();
        if (list.Any(a => a is "DX10" or "DX11" or "DX12")) return "reshade-feeder";
        if (list.Contains("VULKAN")) return "reshade-feeder-vulkan";
        if (list.Contains("DX9")) return "reshade-feeder-dx9";
        return "reshade-feeder";
    }

    public static long StageBytes()
    {
        try { return Directory.Exists(StageDir) ? new DirectoryInfo(StageDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch { return 0; }
    }

    /// <summary>Deletes what was prepared (games that have it keep their copy).</summary>
    public static void ForgetStage()
    {
        try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, recursive: true); } catch (Exception ex) { Log.Warn($"DLSSNR-AMD: {ex.Message}"); }
    }

    /// <summary>
    /// Downloads <paramref name="release"/>, runs its installer unattended into a scratch folder with <paramref name="dll"/>
    /// (only its weights are read), and keeps what games need. Throws with the installer's own last lines when it fails.
    /// </summary>
    public static async Task<DlssNrAmdStage> PrepareAsync(DlssNrAmdRelease release, string dll, IProgress<(string Step, double? Share)>? progress, CancellationToken ct)
    {
        if (!Dlss5.AmdCanRead(dll))
            throw new InstallException(L.F("DLSSNR-AMD reads only {0} 310.9.16 or 310.8.0; this one is {1}.", DlssNr.FileName, DlssNr.Version(dll)));
        var zip = Path.Combine(Root, "download", Path.GetFileName(release.AssetName));
        var pkg = Path.Combine(Root, "package");
        var work = Path.Combine(Root, "work");
        var next = Path.Combine(Root, "stage.new");
        try
        {
            if (!File.Exists(zip))
            {
                Log.Info($"DLSSNR-AMD: downloading {release.Url}");
                await Http.DownloadFileAsync(release.Url, zip, 0, new Progress<double>(p => progress?.Report((L.T("Downloading DLSSNR-AMD…"), p))), ct);
            }
            progress?.Report((L.T("Unpacking…"), null));
            await Task.Run(() =>
            {
                foreach (var d in new[] { pkg, work, next }) if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
                ZipFile.ExtractToDirectory(zip, pkg);
            }, ct);
            var script = Path.Combine(pkg, "install.ps1");
            var stand = Path.Combine(pkg, "model-tools", "dlssnr_extract_model.exe");
            if (!File.Exists(script) || !File.Exists(stand)) throw new InstallException(L.F("{0} is not a DLSSNR-AMD package Hybrid Launcher knows (no install.ps1).", release.AssetName));

            // A stand-in game: a 64-bit exe of the package itself, in a folder of its own. The installer puts the OptiScaler
            // route there, making every network's weights from the dll and checking them against the author's checksums.
            var game = Path.Combine(work, "game");
            Directory.CreateDirectory(game);
            File.Copy(stand, Path.Combine(game, "game.exe"));
            progress?.Report((L.T("Making the networks' weights from your model file (a minute or two)…"), null));
            var (code, output) = await RunAsync(script, Path.Combine(game, "game.exe"), dll, ct);
            Log.Info($"DLSSNR-AMD install.ps1 exited {code}:\n{output}");
            var made = Path.Combine(game, Folder);
            var plugin = Path.Combine(game, "OptiScaler", "plugins", PluginName);
            if (code != 0 || !Directory.Exists(made) || !File.Exists(plugin))
            {
                var tail = string.Join("\n", output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(4));
                throw new InstallException(L.F("DLSSNR-AMD's installer did not finish: {0}", tail.Length > 0 ? tail : code.ToString()));
            }

            progress?.Report((L.T("Keeping what games need…"), null));
            await Task.Run(() =>
            {
                Directory.CreateDirectory(next);
                Directory.Move(made, Path.Combine(next, Folder));
                TryDeleteFile(Path.Combine(next, Folder, "dlssnr-amd.default.ini"));
                File.Copy(plugin, Path.Combine(next, PluginName));
                var ini = Path.Combine(next, "ini");
                Directory.CreateDirectory(ini);
                foreach (var f in Directory.GetFiles(Path.Combine(pkg, "ini"), "*.ini")) File.Copy(f, Path.Combine(ini, Path.GetFileName(f)));
                // The ReShade routes' own files (1.4.0-rc.20): ReShade with the NR add-on and its shaders, the Vulkan loader,
                // DXVK for DirectX 9 games.
                foreach (var part in RoutePackageParts)
                {
                    var src = Path.Combine(pkg, part);
                    if (Directory.Exists(src)) CopyTree(src, Path.Combine(next, part));
                }
                var stage = new DlssNrAmdStage
                {
                    Tag = release.Tag,
                    PreparedAt = DateTime.Now,
                    ModelVersion = DlssNr.Version(dll),
                    Networks = [.. AllNetworks.Where(n => File.Exists(Path.Combine(next, Folder, n + ".bin")))],
                };
                File.WriteAllText(Path.Combine(next, "hl-stage.json"), JsonSerializer.Serialize(stage, HybridJson.Default.DlssNrAmdStage));
                if (Directory.Exists(StageDir)) Directory.Delete(StageDir, recursive: true);
                Directory.Move(next, StageDir);
            }, ct);
            var done = Stage() ?? throw new InstallException(L.T("DLSSNR-AMD was prepared but could not be read back."));
            Log.Info($"DLSSNR-AMD {done.Tag} prepared: {string.Join(", ", done.Networks)}");
            return done;
        }
        finally
        {
            foreach (var d in new[] { pkg, work, next })
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
            TryDeleteFile(zip);
        }
    }

    static async Task<(int Code, string Output)> RunAsync(string script, string exe, string dll, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Target", exe, "-Route", "optiscaler",
                     "-Dll", dll, "-Model", "CC_SILVER_AARDWOLD", "-RenoDX", "on", "-Lumenite", "off" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InstallException("powershell.exe did not start.");
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            // Cancelled by the user: as is. Out of time: an error to show, not a silent cancel.
            if (ct.IsCancellationRequested) throw;
            throw new InstallException(L.T("DLSSNR-AMD's installer did not finish in 15 minutes and was stopped."));
        }
        return (p.ExitCode, (await outTask) + (await errTask));
    }

    // ---------- a game ----------

    /// <summary>Why DLSSNR-AMD cannot go into this install, or null when it can.</summary>
    public static string? CannotGoInto(InstallManifest m)
    {
        if (DlssNr.NeededBy(m)) return L.T("This game has a DLSS NR fork of OptiScaler (F5): DLSSNR-AMD goes with the official OptiScaler instead.");
        var ini = Path.Combine(m.TargetDir, "OptiScaler.ini");
        if (!File.Exists(ini)) return L.T("This OptiScaler install has no OptiScaler.ini.");
        if (IniGet(ini, "Plugins", "LoadAsiPlugins") is null) return L.T("This OptiScaler cannot load .asi plugins (no [Plugins] LoadAsiPlugins): install a recent nightly.");
        return null;
    }

    /// <summary>
    /// Lays the stage over the game's OptiScaler install. <paramref name="network"/> is the one the game starts with;
    /// <paramref name="renoDx"/> the RenoDX style of processing (else wilsjo2's). A dlssnr-amd.ini kept from an earlier install
    /// of this game comes back as it was.
    /// </summary>
    public static void Apply(InstallManifest m, string network, bool renoDx)
    {
        var stage = Stage() ?? throw new InstallException(L.T("DLSSNR-AMD is not prepared yet."));
        if (CannotGoInto(m) is { } why) throw new InstallException(why);
        OptiInstaller.EnsureNotRunning(m.TargetDir);
        if (m.DlssNrAmd is not null) Remove(m);
        var target = m.TargetDir;
        var info = new DlssNrAmdInstall { Tag = stage.Tag, Network = network, RenoDx = renoDx };
        try { ApplyFiles(m, stage, info, network, renoDx); }
        catch
        {
            // Half put in: taken out again, so nothing is left in the game that the manifest does not know about.
            m.DlssNrAmd = info;
            try { Remove(m); } catch (Exception ex) { Log.Warn($"DLSSNR-AMD: taking a half install out: {ex.Message}"); }
            throw;
        }
    }

    static void ApplyFiles(InstallManifest m, DlssNrAmdStage stage, DlssNrAmdInstall info, string network, bool renoDx)
    {
        var target = m.TargetDir;

        // A dlssnr-amd folder of the game's own (put in by hand earlier): kept aside, back on uninstall.
        var dst = Path.Combine(target, Folder);
        // Backups off (Settings → Mods): the game's own folder is replaced, not kept.
        if (Directory.Exists(dst) && !SettingsStore.Current.BackupGameFiles)
        {
            Directory.Delete(dst, recursive: true);
            Log.Info($"DLSS NR (AMD): the game's own {Folder} folder replaced (backups are off)");
        }
        if (Directory.Exists(dst))
        {
            var keep = Path.Combine(m.BackupRoot ?? Path.Combine(AppInfo.BackupsDir, OptiInstaller.SafeName(target)), "dlssnr-amd-own");
            if (Directory.Exists(keep)) Directory.Delete(keep, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            Directory.Move(dst, keep);
            info.OwnFolderBackup = keep;
        }
        bool sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(StageDir)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase);
        var from = Path.Combine(StageDir, Folder);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            // Hard links: the networks' weights are 600 MB, the same for every game. NR only reads them.
            if (!(sameDrive && CreateHardLink(to, file, IntPtr.Zero))) File.Copy(file, to, overwrite: true);
        }

        var plugins = Path.Combine(target, m.PluginsDir);
        Directory.CreateDirectory(plugins);
        var plugin = Path.Combine(plugins, PluginName);
        File.Copy(Path.Combine(StageDir, PluginName), plugin, overwrite: true);
        info.PluginHash = OptiInstaller.Hash(plugin);

        // dlssnr-amd.ini: the game's own from an earlier install, else the package's, starting with the chosen network.
        var saved = SavedIni(target);
        string text;
        if (File.Exists(saved)) text = File.ReadAllText(saved);
        else
        {
            text = File.ReadAllText(Path.Combine(StageDir, "ini", renoDx ? "dlssnr-amd-optiscaler-renodx.ini" : "dlssnr-amd-optiscaler.ini"));
            text = SetIn(text, "Network", "Model", network);
        }
        File.WriteAllText(Path.Combine(target, IniName), text, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dst, "dlssnr-amd.default.ini"), text, new UTF8Encoding(false));

        // OptiScaler loads .asi plugins only when asked; its frame generation key is End by default, NR Control's too.
        var opti = Path.Combine(target, "OptiScaler.ini");
        foreach (var (section, key, value) in new[] { ("Plugins", "LoadAsiPlugins", "true"), ("Menu", "FGShortcutKey", "-1") })
        {
            var before = IniGet(opti, section, key);
            if (before is null) continue;
            info.OptiIniBefore[section + "|" + key] = before;
            IniSet(opti, section, key, value);
        }
        m.DlssNrAmd = info;
        Log.Info($"DLSSNR-AMD {stage.Tag} put in {target} ({network}, {(renoDx ? "RenoDX" : "wilsjo2")} style, {(sameDrive ? "hard links" : "copies")})");
    }

    /// <summary>Takes DLSSNR-AMD out of the game (its ini kept for the next time), OptiScaler.ini's two keys as they were.</summary>
    public static void Remove(InstallManifest m)
    {
        if (m.DlssNrAmd is not { } info) return;
        var target = m.TargetDir;
        var ini = Path.Combine(target, IniName);
        try { if (File.Exists(ini)) File.Copy(ini, SavedIni(target), overwrite: true); } catch { }
        var dst = Path.Combine(target, Folder);
        try { if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true); }
        catch (Exception ex) { Log.Warn($"DLSSNR-AMD: could not delete {dst}: {ex.Message}"); }
        foreach (var f in new[] { IniName, "dlssnr-amd.log", "dlssnr-amd-crash.dmp" }) TryDeleteFile(Path.Combine(target, f));
        var plugin = Path.Combine(target, m.PluginsDir, PluginName);
        if (File.Exists(plugin) && OptiInstaller.Hash(plugin) == info.PluginHash) TryDeleteFile(plugin);
        var opti = Path.Combine(target, "OptiScaler.ini");
        if (File.Exists(opti))
            foreach (var (k, v) in info.OptiIniBefore)
            {
                var parts = k.Split('|');
                if (parts.Length == 2) IniSet(opti, parts[0], parts[1], v);
            }
        if (info.OwnFolderBackup is { } keep && Directory.Exists(keep) && !Directory.Exists(dst))
            try { Directory.Move(keep, dst); } catch (Exception ex) { Log.Warn($"DLSSNR-AMD: could not restore {dst}: {ex.Message}"); }
        m.DlssNrAmd = null;
        Log.Info($"DLSSNR-AMD taken out of {target}");
    }

    /// <summary>Forgets the dlssnr-amd.ini kept for a game: the next install starts from the package's.</summary>
    public static void ForgetSavedIni(string target) => TryDeleteFile(SavedIni(target));

    static string SavedIni(string target) => Path.Combine(SavedIniDir, OptiInstaller.SafeName(target) + ".ini");

    // ---------- a game, through ReShade (no OptiScaler) ----------

    /// <summary>Why DLSSNR-AMD cannot go into this game through ReShade, or null when it can.</summary>
    public static string? CannotGoIntoReShade(GameSettings gs, string target)
    {
        if (gs.Install is not null) return L.T("This game has OptiScaler from Hybrid Launcher: uninstall it first, or put DLSSNR-AMD in through OptiScaler.");
        // Only a ReShade dll really in the folder stops it (2.0.0-rc.11). The saved marks and ReShade.ini outlive an uninstall
        // (the user's ReShade.ini is kept on purpose), so a game with no ReShade left was refused.
        if (gs.DlssNrAmdReShade is null && (gs.ReShade.RenamedFrom is not null || gs.ReShade.InstallerVersion is not null) && Mods.ReShade.Detect(target) is not null)
            return L.T("This game has ReShade from Hybrid Launcher's ReShade section: uninstall it there first. The ReShade way brings its own ReShade.");
        if (!Directory.Exists(target)) return L.T("The game folder was not found.");
        return null;
    }

    /// <summary>
    /// Puts DLSSNR-AMD in through ReShade, as the package's installer does for <paramref name="route"/>: the networks and
    /// shaders, ReShade with the NR add-on and its shaders (as dxgi.dll for DirectX 10/11/12; as a Vulkan layer, with the
    /// package's Vulkan loader, for Vulkan and, through DXVK, DirectX 9), dlssnr-amd.ini. Files of the same names the game
    /// had (and other ReShade add-ons, which would load too) are kept aside and come back when it is taken out.
    /// </summary>
    public static void ApplyReShade(GameSettings gs, string target, string route, string network, bool renoDx)
    {
        var stage = Stage() ?? throw new InstallException(L.T("DLSSNR-AMD is not prepared yet."));
        if (!StageHasReShade()) throw new InstallException(L.T("The prepared DLSSNR-AMD has no ReShade files: prepare it again."));
        if (!ReShadeRoutes.Contains(route)) throw new InstallException($"Unknown route {route}");
        if (CannotGoIntoReShade(gs, target) is { } why) throw new InstallException(why);
        OptiInstaller.EnsureNotRunning(target);
        if (gs.DlssNrAmdReShade is not null) RemoveReShade(gs);
        var info = new DlssNrAmdReShadeInstall
        {
            Tag = stage.Tag, Route = route, TargetDir = target, Network = network, RenoDx = renoDx, InstalledAt = DateTime.Now,
            BackupDir = Path.Combine(AppInfo.BackupsDir, OptiInstaller.SafeName(target), "dlssnr-amd-reshade"),
        };
        gs.DlssNrAmdReShade = info;
        try { ApplyReShadeFiles(info, target, route, network, renoDx); }
        catch
        {
            try { RemoveReShade(gs); } catch (Exception ex) { Log.Warn($"DLSSNR-AMD: taking a half install out: {ex.Message}"); }
            throw;
        }
    }

    static void ApplyReShadeFiles(DlssNrAmdReShadeInstall info, string target, string route, string network, bool renoDx)
    {
        bool sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(StageDir)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(info.BackupDir!)) Directory.Delete(info.BackupDir!, recursive: true);

        void KeepAside(string rel)
        {
            if (!SettingsStore.Current.BackupGameFiles) return;
            var at = Path.Combine(target, rel);
            if (!File.Exists(at) && !Directory.Exists(at)) return;
            var to = Path.Combine(info.BackupDir!, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (Directory.Exists(at)) Directory.Move(at, to); else File.Move(at, to);
            info.BackedUp.Add(rel);
        }
        void PutFile(string src, string rel)
        {
            KeepAside(rel);
            var to = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(src, to, overwrite: true);
            info.Files.Add(rel);
        }
        void PutTree(string src, string rel, bool link)
        {
            KeepAside(rel);
            info.Dirs.Add(rel);
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(target, rel, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                // Hard links for the networks' weights (600 MB, the same for every game; NR only reads them).
                if (!(link && sameDrive && CreateHardLink(to, file, IntPtr.Zero))) File.Copy(file, to, overwrite: true);
            }
        }

        // Other ReShade add-ons in the folder would load too: aside, as the package's installer does.
        foreach (var addon in Directory.EnumerateFiles(target).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".addon" or ".addon64")
                     .Select(Path.GetFileName).Where(n => !n!.Equals("dlssnr_amd.addon64", StringComparison.OrdinalIgnoreCase)).ToList())
            KeepAside(addon!);

        PutTree(Path.Combine(StageDir, Folder), Folder, link: true);
        var reshade = Path.Combine(StageDir, "reshade");
        bool asLayer = route != "reshade-feeder";
        if (asLayer)
        {
            PutFile(Path.Combine(StageDir, "vulkan", "vulkan-1.dll"), "vulkan-1.dll");
            if (route == "reshade-feeder-dx9")
            {
                PutFile(Path.Combine(StageDir, "dxvk", "d3d9.dll"), "d3d9.dll");
                PutFile(Path.Combine(StageDir, "dxvk", "dxgi.dll"), "dxgi.dll");
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(reshade))
        {
            var name = Path.GetFileName(dir);
            if (!asLayer && name.Equals("vk-override", StringComparison.OrdinalIgnoreCase)) continue;
            PutTree(dir, name, link: false);
        }
        foreach (var file in Directory.EnumerateFiles(reshade))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("ReShadePreset-d3d9.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (!asLayer && name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase)) continue;
            PutFile(file, name);
        }
        if (!asLayer) PutFile(Path.Combine(reshade, "ReShade64.dll"), "dxgi.dll");
        if (route == "reshade-feeder-dx9") File.Copy(Path.Combine(reshade, "ReShadePreset-d3d9.ini"), Path.Combine(target, "ReShadePreset.ini"), overwrite: true);

        // dlssnr-amd.ini: the game's own from an earlier ReShade-way install, else the package's add-on file with the network
        // and the processing style chosen.
        var saved = SavedReShadeIni(target);
        string text;
        if (File.Exists(saved)) text = File.ReadAllText(saved);
        else
        {
            text = File.ReadAllText(Path.Combine(StageDir, "ini", "dlssnr-amd-addon.ini"));
            text = SetIn(text, "Network", "Model", network);
            if (renoDx) text = SetIn(text, "RenoDX", "Enabled", "1");
        }
        KeepAside(IniName);
        File.WriteAllText(Path.Combine(target, IniName), text, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(target, Folder, "dlssnr-amd.default.ini"), text, new UTF8Encoding(false));
        info.Files.Add(IniName);
        Log.Info($"DLSSNR-AMD {info.Tag} put in {target} through ReShade ({route}, {network}, {(renoDx ? "RenoDX" : "wilsjo2")} style, {info.BackedUp.Count} of the game's files kept aside)");
    }

    /// <summary>Takes the ReShade way out: every file and folder it put in, its logs, and the game's own files back.</summary>
    public static void RemoveReShade(GameSettings gs)
    {
        if (gs.DlssNrAmdReShade is not { } info) return;
        var target = info.TargetDir;
        var ini = Path.Combine(target, IniName);
        try { if (File.Exists(ini)) File.Copy(ini, SavedReShadeIni(target), overwrite: true); } catch { }
        foreach (var rel in info.Files.AsEnumerable().Reverse()) TryDeleteFile(Path.Combine(target, rel));
        foreach (var rel in info.Dirs.AsEnumerable().Reverse())
            try { var d = Path.Combine(target, rel); if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch (Exception ex) { Log.Warn($"DLSSNR-AMD: could not delete {rel}: {ex.Message}"); }
        foreach (var f in new[] { "dlssnr-amd.log", "dlssnr-amd-crash.dmp", "ReShade.log" }) TryDeleteFile(Path.Combine(target, f));
        if (info.BackupDir is { } backup)
        {
            foreach (var rel in info.BackedUp)
            {
                var from = Path.Combine(backup, rel);
                var to = Path.Combine(target, rel);
                try
                {
                    if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
                    else if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
                }
                catch (Exception ex) { Log.Warn($"DLSSNR-AMD: could not put {rel} back: {ex.Message}"); }
            }
            try { if (Directory.Exists(backup) && !Directory.EnumerateFileSystemEntries(backup, "*", SearchOption.AllDirectories).Any(e => File.Exists(e))) Directory.Delete(backup, recursive: true); } catch { }
        }
        gs.DlssNrAmdReShade = null;
        Log.Info($"DLSSNR-AMD (ReShade way) taken out of {target}");
    }

    static string SavedReShadeIni(string target) => Path.Combine(SavedIniDir, OptiInstaller.SafeName(target) + "-reshade.ini");

    /// <summary>Forgets the dlssnr-amd.ini kept for the ReShade way in a game.</summary>
    public static void ForgetSavedReShadeIni(string target) => TryDeleteFile(SavedReShadeIni(target));

    /// <summary>A setting of the dlssnr-amd.ini in a game folder.</summary>
    public static string? GetAt(string target, string section, string key) => IniGet(Path.Combine(target, IniName), section, key);

    /// <summary>Changes a setting of the dlssnr-amd.ini in a game folder (false when the file or the key is missing).</summary>
    public static bool SetAt(string target, string section, string key, string value)
    {
        var ini = Path.Combine(target, IniName);
        return File.Exists(ini) && IniSet(ini, section, key, value);
    }

    static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ---------- dlssnr-amd.ini ----------

    /// <summary>A setting of the game's dlssnr-amd.ini (null when the file or the key is missing).</summary>
    public static string? Get(InstallManifest m, string section, string key) => IniGet(Path.Combine(m.TargetDir, IniName), section, key);

    /// <summary>Changes a setting in the game's dlssnr-amd.ini (NR reads it again while the game runs).</summary>
    public static bool Set(InstallManifest m, string section, string key, string value)
    {
        var ini = Path.Combine(m.TargetDir, IniName);
        if (!File.Exists(ini)) return false;
        bool ok = IniSet(ini, section, key, value);
        if (key == "Model" && m.DlssNrAmd is { } info) info.Network = value;
        return ok;
    }

    public static string? IniGet(string file, string section, string key)
    {
        if (!File.Exists(file)) return null;
        string current = "";
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line[1..^1].Trim(); continue; }
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0 || !current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(eq + 1)..].Trim();
        }
        return null;
    }

    public static bool IniSet(string file, string section, string key, string value)
    {
        var text = File.ReadAllText(file);
        var changed = SetIn(text, section, key, value);
        if (changed == text) return IniGet(file, section, key) == value;
        File.WriteAllText(file, changed, new UTF8Encoding(false));
        return true;
    }

    /// <summary>The text with the first <paramref name="key"/> of <paramref name="section"/> set, its spacing ("Key = v" or "Key=v") kept.</summary>
    static string SetIn(string text, string section, string key, string value)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(nl);
        string current = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line[1..^1].Trim(); continue; }
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            int eq = lines[i].IndexOf('=');
            if (eq <= 0 || !current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (!lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            bool spaced = lines[i][..eq].EndsWith(' ');
            lines[i] = lines[i][..eq].TrimEnd() + (spaced ? " = " : "=") + value;
            return string.Join(nl, lines);
        }
        return text;
    }

    static void TryDeleteFile(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);
}
