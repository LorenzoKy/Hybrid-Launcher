// End-to-end checks for HybridLauncher.Core that need a real Windows box and network:
//   dotnet run -- scan              list detected games and their exe folders
//   dotnet run -- ini <file>        parse an OptiScaler.ini and print the option kinds
//   dotnet run -- decide            prompt classification table for the setup bat
//   dotnet run -- wiki              OptiScaler wiki page matching for a few known game names
//   dotnet run -- info [appid] [name]  game overview data: Steam store details, PCGamingWiki, HowLongToBeat, OptiScaler wiki
//   dotnet run -- hw                hardware overview (cpu, gpu, memory, displays, system, drives)
//   dotnet run -- api               graphics API per detected game (PCGamingWiki, exe imports) and Xbox package logos
//   dotnet run -- ach [appid]       Steam achievements read from appcache\stats (read-only)
//   dotnet run -- nexus             Nexus Mods page matching for a few known game names
//   dotnet run -- versions          list saved OptiScaler versions, delete one downloaded release
//   dotnet run -- renodx            RenoDX mod list parsing and game matching
//   dotnet run -- reshade           ReShade versions, and Load-through-OptiScaler on a fake folder
//   dotnet run -- modinstall        real Luma (alone and with OptiScaler) and RenoDX installs into fake folders, then removed
//   dotnet run -- steamsession [appid|list] [seconds] [download]
//                                   signs in with HL_DATA's Steam token, reads the library and a game's plan; "download" also
//                                   downloads it into a temp folder, stopped and resumed (the Steam app must stay online)
//   dotnet run -- install <dir> [stable|nightly|custom] [nvidia]
//                                   install + uninstall into a fake game folder and verify it is restored byte for byte
//   dotnet run -- cloud             cloud saves on temp folders: upload, change in the cloud, restore, conflict, history, guards
//   dotnet run -- report            the bug report: log scrubbed of personal details, the form's address fits, the saved log
//   dotnet run -- cloud-mirror      two clouds at once (two temp folders): both get each upload, one that missed it catches up
//   dotnet run -- cloud-live mega   the same against the account signed in under HL_DATA (mega), on a test game it removes
//   dotnet run -- art [appid] [name]  banner, logo and cover a game page uses (store asset list, screenshot fallback)
//   dotnet run -- signin-page [hold] MEGA's browser sign-in page served on 127.0.0.1: form, token guard, empty-form check, cancel
//   dotnet run -- friends           signs in with HL_DATA's Steam token and lists the account's friends (read-only)
//   dotnet run -- updates           the app's own update: release JSON parsing, the check against a local feed, download + unpack
//   dotnet run -- customdlls        custom dlls on a fake install: copied in, replaced, a foreign file kept, turned off, uninstalled
//   dotnet run -- techversions      My PC's technology versions read from AMD's, Intel's and NVIDIA's SDK releases on GitHub
// Set HL_DATA to keep test data out of %LOCALAPPDATA%\HybridLauncher.
using System.Security.Cryptography;
using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Ini;
using HybridLauncher.Core.Opti;
using HybridLauncher.Core.Mods;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) failures++;
}

var cmd = args.Length > 0 ? args[0] : "scan";
switch (cmd)
{
    case "sessions":
    {
        // A fake game shaped like an Xbox / Unreal one: a launcher exe at the top hands over to the game exe in a sub-folder and
        // exits. The session must stay one session across the handover, end only when the game exe is gone, and Stop must reach
        // the sub-folder too. Smart HDR stays off here (the real display is never touched); HL_DATA goes to a temp folder.
        Environment.SetEnvironmentVariable("HL_DATA", Path.Combine(Path.GetTempPath(), "hl-sessions-data-" + Guid.NewGuid().ToString("N")[..6]));
        var root = Path.Combine(Path.GetTempPath(), "hl-sessions-" + Guid.NewGuid().ToString("N")[..6], "Content");
        var bin = Path.Combine(root, "Game", "Binaries", "Win64");
        Directory.CreateDirectory(bin);
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var launcher = Path.Combine(root, "gamelaunchhelper.exe");
        var gameExe = Path.Combine(bin, "Game-Win64-Shipping.exe");
        File.Copy(ping, launcher);
        File.Copy(ping, gameExe);
        System.Diagnostics.Process Run(string exe, int seconds) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"-n {seconds + 1} 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var game = new Game { Id = "smoke:sessions", Name = "Smoke Sessions", Store = GameStore.Xbox, InstallDir = root, MainExe = launcher };
        var settings = new GameSettings();
        async Task<bool> WaitFor(Func<bool> cond, int seconds)
        {
            for (int i = 0; i < seconds * 4; i++) { if (cond()) return true; await Task.Delay(250); }
            return cond();
        }

        // 1. Started "from Play": launcher 3 s, then the game in the sub-folder for 12 s.
        var l = Run(launcher, 3);
        GameSessions.Launched(game, settings, root);
        await Task.Delay(1500);
        var g = Run(gameExe, 12);
        Check(await WaitFor(() => GameSessions.State(game.Id) == GameRunState.Running, 6), "running once the launcher is up");
        l.WaitForExit();
        await Task.Delay(7000); // well past the 2 misses (4 s) that used to end the session
        Check(!g.HasExited && GameSessions.State(game.Id) == GameRunState.Running, "still running after the launcher exited (game in a sub-folder)");
        g.WaitForExit();
        Check(await WaitFor(() => GameSessions.State(game.Id) == GameRunState.Stopped, 8), "stopped once the game exe in the sub-folder is gone");

        // 2. Started outside Hybrid Launcher, game exe only: the library watch finds it, Stop ends the sub-folder exe.
        GameSessions.WatchAll(() => [(game, settings)]);
        var g2 = Run(gameExe, 60);
        Check(await WaitFor(() => GameSessions.State(game.Id) == GameRunState.Running, 8), "game exe in a sub-folder found running when started outside");
        Check(await GameSessions.StopAsync(game.Id) && await WaitFor(() => g2.HasExited, 3), "Stop ends the game exe in the sub-folder");
        Check(await WaitFor(() => GameSessions.State(game.Id) == GameRunState.Stopped, 6), "stopped after Stop");
        foreach (var p in new[] { l, g, g2 }) { try { if (!p.HasExited) p.Kill(); } catch { } p.Dispose(); }
        try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch { }
        break;
    }

    case "customdlls":
    {
        // Needs HL_DATA (the shared folder is written). A fake install with OptiScaler in its own "OptiScaler" folder, as the bat
        // makes it: custom dlls go to OptiScaler/custom/<name>/<kind>.
        var game = Path.Combine(Path.GetTempPath(), "hl-customdlls-" + Guid.NewGuid().ToString("N")[..6]);
        var opti = Path.Combine(game, "OptiScaler");
        Directory.CreateDirectory(opti);
        File.WriteAllText(Path.Combine(game, "OptiScaler.ini"), "[Upscalers]\nDx12Upscaler=Smoke FSR\nVulkanUpscaler=auto\n[FrameGen]\nFGNvngxReplacement=auto\n[Libraries]\nOptiDllPath=auto\n");
        var picked = Path.Combine(Path.GetTempPath(), Path.GetFileName(game) + "-src");
        Directory.CreateDirectory(picked);
        File.WriteAllText(Path.Combine(picked, "a.dll"), "ffx dx12");
        File.WriteAllText(Path.Combine(picked, "b.dll"), "ffx vk");
        File.WriteAllText(Path.Combine(picked, "helper.dll"), "helper");
        CustomDlls.AddFile("Smoke FSR", CustomDllKind.Ffx, Path.Combine(picked, "a.dll"), "amd_fidelityfx_upscaler_dx12.dll");
        CustomDlls.AddFile("Smoke FSR", CustomDllKind.Ffx, Path.Combine(picked, "helper.dll"), "helper.dll");
        CustomDlls.AddFile("Smoke FSR", CustomDllKind.FfxVk, Path.Combine(picked, "b.dll"), "my_vk.dll");
        var entry = CustomDlls.Entries().Single(e => e.Name == "Smoke FSR");
        Check(entry.Kinds.SequenceEqual([CustomDllKind.Ffx, CustomDllKind.FfxVk]), "one entry groups the DX12 and the Vulkan FFX dll");
        Check(entry.Files.Single(f => f.Loaded && f.Kind == CustomDllKind.Ffx).FileName == "amd_fidelityfx_upscaler_dx12.dll", "the standard name is the one OptiScaler loads");
        Check(CustomDlls.Guess("amd_fidelityfx_upscaler_vk.dll") == CustomDllKind.FfxVk && CustomDlls.Guess("nvngx_dlssg.dll") == CustomDllKind.Dlssg
              && CustomDlls.Guess("nvngx.dll") == CustomDllKind.Dlss && CustomDlls.Guess("amd_fidelityfx_upscaler_dx12.dll") == CustomDllKind.Ffx, "kinds guessed from file names");

        var m = new InstallManifest { TargetDir = game };
        CustomDlls.Sync(m, ["Smoke FSR"]);
        var ffx = Path.Combine(opti, "custom", "Smoke FSR", "ffx", "amd_fidelityfx_upscaler_dx12.dll");
        Check(File.ReadAllText(ffx) == "ffx dx12" && File.Exists(Path.Combine(opti, "custom", "Smoke FSR", "ffx_vk", "my_vk.dll")), "copied next to OptiScaler.dll: OptiScaler/custom/<name>/ffx and ffx_vk");
        Check(!Directory.Exists(Path.Combine(game, "custom")), "nothing in the game folder's own custom folder");

        // A dll moved to another kind and renamed follows in the game; the old one leaves.
        CustomDlls.MoveFile("Smoke FSR", CustomDllKind.FfxVk, "my_vk.dll", "Smoke FSR", CustomDllKind.FfxVk, "amd_fidelityfx_upscaler_vk.dll");
        CustomDlls.Sync(m, ["Smoke FSR"]);
        Check(!File.Exists(Path.Combine(opti, "custom", "Smoke FSR", "ffx_vk", "my_vk.dll")) && File.Exists(Path.Combine(opti, "custom", "Smoke FSR", "ffx_vk", "amd_fidelityfx_upscaler_vk.dll")), "renamed dll follows in the game");

        // Renaming the entry renames it in the game and in OptiScaler.ini.
        CustomDlls.RenameEntry("Smoke FSR", "Smoke FSR 4.0.3");
        CustomDlls.Sync(m, ["Smoke FSR 4.0.3"]);
        CustomDlls.RenameInIni(game, "Smoke FSR", "Smoke FSR 4.0.3");
        Check(!Directory.Exists(Path.Combine(opti, "custom", "Smoke FSR")) && File.Exists(Path.Combine(opti, "custom", "Smoke FSR 4.0.3", "ffx", "amd_fidelityfx_upscaler_dx12.dll")), "renamed entry renamed in the game");
        Check(HybridLauncher.Core.Ini.IniDocument.Load(Path.Combine(game, "OptiScaler.ini")).Get("Upscalers", "Dx12Upscaler") == "Smoke FSR 4.0.3", "Dx12Upscaler follows the new name");

        // A rename that only changes case reaches the game's folder too, so it matches OptiScaler.ini exactly.
        CustomDlls.RenameEntry("Smoke FSR 4.0.3", "SMOKE FSR 4.0.3");
        CustomDlls.Sync(m, ["SMOKE FSR 4.0.3"]);
        Check(Directory.GetDirectories(Path.Combine(opti, "custom")).Select(Path.GetFileName).Contains("SMOKE FSR 4.0.3") && m.CustomDlls.Keys.Single() == "SMOKE FSR 4.0.3", "case-only rename follows in the game");
        CustomDlls.RenameEntry("SMOKE FSR 4.0.3", "Smoke FSR 4.0.3");
        CustomDlls.Sync(m, ["Smoke FSR 4.0.3"]);

        // A custom folder of the user's own stays; turning the entry off and uninstalling leave nothing of ours.
        var foreign = Path.Combine(opti, "custom", "Mine", "dlss", "nvngx.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "mine");
        CustomDlls.Sync(m, []);
        Check(!Directory.Exists(Path.Combine(opti, "custom", "Smoke FSR 4.0.3")) && m.CustomDlls.Count == 0, "turned off: entry removed from the install");
        Check(File.ReadAllText(foreign) == "mine", "a custom folder of the user's own stays");
        Directory.Delete(Path.Combine(opti, "custom"), recursive: true);
        CustomDlls.Sync(m, ["Smoke FSR 4.0.3"]);
        OptiInstaller.Uninstall(m);
        Check(!Directory.Exists(Path.Combine(opti, "custom")), "uninstall removes the custom folder it made");

        CustomDlls.DeleteFile("Smoke FSR 4.0.3", CustomDllKind.Ffx, "helper.dll");
        Check(CustomDlls.Entries().Single(e => e.Name == "Smoke FSR 4.0.3").Files.Count == 2, "a single dll deleted");
        CustomDlls.Delete("Smoke FSR 4.0.3");
        Check(CustomDlls.Entries().All(e => !e.Name.StartsWith("Smoke")), "entry deleted from the shared folder");
        Directory.Delete(game, recursive: true);
        Directory.Delete(picked, recursive: true);
        break;
    }
    case "techversions":
    {
        var sample = "<h3>AMD FSR\u2122 Upscaling 4.1.2</h3> <h3>AMD FidelityFX\u2122 Super Resolution 3.1.6</h3> <h3>AMD FSR\u2122 Frame Generation (Swapchain) 4.0.0</h3> <h3>AMD FSR\u2122 Frame Generation 4.0.2</h3>";
        var parsed = HybridLauncher.Core.Hardware.TechVersions.ParseAmd(sample).ToList();
        Check(parsed.Contains(("fsr-upscaling-ml", "4.1.2")) && parsed.Contains(("fsr-upscaling", "3.1.6")) && parsed.Contains(("fsr-framegen-ml", "4.0.2")) && parsed.Count == 3,
            "AMD notes parsed, Swapchain left out: " + string.Join(", ", parsed));
        var live = await HybridLauncher.Core.Hardware.TechVersions.FromVendorsAsync(CancellationToken.None);
        foreach (var g in live.GroupBy(x => x.Id)) Console.WriteLine($"  {g.Key}: {string.Join(" ", g.Select(x => x.Version).Distinct())}");
        Check(live.Any(x => x.Id == "fsr-upscaling-ml") && live.Any(x => x.Id == "xess") && live.Any(x => x.Id == "dlss-sdk"), "AMD, Intel and NVIDIA releases read");
        await HybridLauncher.Core.Hardware.TechVersions.RefreshAsync(force: true);
        foreach (var id in new[] { "fsr-upscaling-ml", "fsr-framegen-ml", "fsr-ray-regeneration", "fsr-upscaling", "fsr-framegen", "xess", "xell", "dlss-sdk", "dlss" })
            Console.WriteLine($"  now {id} = {HybridLauncher.Core.Hardware.TechVersions.Get(id)}");
        break;
    }
    case "updates":
    {
        // A local release feed (HL_UPDATE_FEED) with a zip holding a stand-in exe: nothing is installed or started.
        var dir = Path.Combine(Path.GetTempPath(), "hl-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, "HybridLauncher-99.0.0-win-x64.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            using (var w = new StreamWriter(archive.CreateEntry(AppInfo.FileName + ".exe").Open())) w.Write("stand-in");
            string Release(string tag, bool draft = false) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = false, ["body"] = "Notes", ["html_url"] = "https://example.invalid",
                ["assets"] = new[] { new Dictionary<string, object> { ["name"] = Path.GetFileName(zip), ["browser_download_url"] = zip, ["size"] = new FileInfo(zip).Length } },
            });
            var parsed = AppUpdates.Parse(Release("v99.0.0"));
            Check(parsed?.Version == new Version(99, 0, 0) && parsed.ZipUrl == zip, "release parsed (tag, zip for this processor)");
            Check(AppUpdates.Parse(Release("v99.0.0", draft: true)) is null, "drafts are never offered");
            var feed = Path.Combine(dir, "latest.json");
            File.WriteAllText(feed, Release("v0.0.1"));
            Environment.SetEnvironmentVariable("HL_UPDATE_FEED", feed);
            Check(await AppUpdates.CheckAsync(CancellationToken.None) is null, "an older release is not an update");
            File.WriteAllText(feed, Release("v99.0.0"));
            var found = await AppUpdates.CheckAsync(CancellationToken.None);
            Check(found is not null, "a newer release is found");
            var exe = await AppUpdates.DownloadAsync(found!, null, CancellationToken.None);
            Check(File.Exists(exe) && File.ReadAllText(exe) == "stand-in", "the update is downloaded and unpacked: " + exe);
            AppUpdates.CleanUp();
            Check(!File.Exists(exe), "clean-up removes it");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HL_UPDATE_FEED", null);
            Directory.Delete(dir, recursive: true);
        }
        break;
    }
    case "steamprobe":
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { Console.WriteLine($"Steam anonymous logon: {await HybridLauncher.Core.Accounts.SteamAccount.ProbeAsync(CancellationToken.None)} in {sw.ElapsedMilliseconds} ms"); }
        catch (Exception ex) { Console.WriteLine($"FAILED after {sw.ElapsedMilliseconds} ms: {ex}"); failures++; }
        break;
    }
    case "luma":
    {
        var mods = await Luma.GetModsAsync(force: true);
        var builds = await Luma.GetBuildsAsync();
        Console.WriteLine($"{mods.Count} mods ({mods.Count(m => m.Status == LumaStatus.Working)} working), {builds.Count} builds, unreal tested {mods.FirstOrDefault(m => m.Engine == "Unreal")?.Tested.Count}");
        Check(mods.Count > 40 && builds.Count > 5, "list and builds read");
        foreach (var (name, engine) in new[] { ("Prey", (string?)null), ("Mafia III", null), ("Hollow Knight: Silksong", null), ("Ace Combat 7: Skies Unknown", "Unreal"), ("BioShock 2 Remastered", null), ("Cyberpunk 2077", null) })
        {
            var hits = Luma.Match(mods, name, engine);
            var best = hits.FirstOrDefault();
            var versions = best is null ? [] : Luma.Versions(best.Mod, builds, name.Contains("BioShock"));
            Console.WriteLine($"{name}: {best?.Mod.Name} [{best?.Mod.Status}] {best?.Reason} · {best?.Mod.Features.Count} features · links {string.Join(",", best?.Mod.Links.Select(l => l.Kind) ?? [])}");
            foreach (var v in versions.Take(3)) Console.WriteLine($"     {v.Display}  {v.Asset}  {v.Size / 1048576} MB");
        }
        Check(Luma.Match(mods, "Prey", null).FirstOrDefault()?.Mod.Features.Count > 0, "Prey has features");
        Check(Luma.Versions(Luma.Match(mods, "Prey", null)[0].Mod, builds, false).Count > 0, "Prey has versions");
        Check(Luma.Match(mods, "Cyberpunk 2077", null).Count == 0, "no Luma mod for Cyberpunk");
        break;
    }
    case "friends":
    {
        // Read-only: the friends list and their states, through Hybrid Launcher's own session (never sets a state of its own).
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var friends = await HybridLauncher.Core.Accounts.SteamAccount.FriendsAsync(limit.Token);
        Console.WriteLine($"Friends: {friends.Count} in {sw.ElapsedMilliseconds} ms, {friends.Count(f => f.Online)} online, {friends.Count(f => f.InGame)} in game");
        foreach (var f in friends.Take(8)) Console.WriteLine($"  rank {f.Rank}  {f.State,-8} avatar={(f.AvatarUrl is null ? "no" : "yes")} game={(f.InGame ? f.PlayingName ?? f.PlayingAppId.ToString() : "-")} last online={(f.LastOnline is { } t ? $"{(DateTime.UtcNow - t.ToUniversalTime()).TotalHours:F0} h ago" : "?")}");
        Check(friends.All(f => f.Name.Length > 0), "every friend has a name");
        Check(friends.Count == 0 || friends.Any(f => f.AvatarUrl is not null), "avatars known");
        break;
    }
    case "steamsession":
    {
        // Signs in with the saved Steam token (HL_DATA\accounts\steam.bin), reads the library, plans a game's download and stays
        // signed in a while: the Steam client running on this PC must stay online (see Steam\logs\connection_log.txt).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var library = await HybridLauncher.Core.Accounts.SteamAccount.LibraryAsync(limit.Token);
        Console.WriteLine($"Library: {library.Count} games in {sw.ElapsedMilliseconds} ms");
        Check(library.Count > 0, "library read");
        var pick = args.Length > 1 ? library.FirstOrDefault(g => g.Id == "steam:" + args[1]) : null;
        if (pick is not null)
        {
            var plan = await HybridLauncher.Core.Accounts.SteamAccount.PlanAsync(pick, Path.Combine(Path.GetTempPath(), "hl-steam-plan"), CancellationToken.None);
            Console.WriteLine($"Plan for {pick.Name}: {plan.Files.Count} files, {plan.TotalBytes / 1048576} MB, exe {plan.Exe}");
            Check(plan.Files.Count > 0, "plan read");
            if (args.Contains("download"))
            {
                // A real download into a temp folder: stopped after 10 s, then resumed (it must go on, not start over), then checked.
                if (Directory.Exists(plan.InstallDir)) Directory.Delete(plan.InstallDir, true);
                long done = 0;
                using (var first = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    try { await HybridLauncher.Core.Accounts.DirectDownloader.RunAsync(plan, b => done = Math.Max(done, b), first.Token); }
                    catch (OperationCanceledException) { }
                }
                var paused = done;
                Console.WriteLine($"Stopped at {paused / 1048576} MB");
                var again = await HybridLauncher.Core.Accounts.SteamAccount.PlanAsync(pick, Path.Combine(Path.GetTempPath(), "hl-steam-plan"), CancellationToken.None);
                long resumedFrom = -1;
                sw.Restart();
                await HybridLauncher.Core.Accounts.DirectDownloader.RunAsync(again, b => { if (resumedFrom < 0) resumedFrom = b; done = b; }, CancellationToken.None);
                Console.WriteLine($"Resumed from {resumedFrom / 1048576} MB, finished in {sw.Elapsed.TotalSeconds:0} s ({again.TotalBytes / 1048576.0 / Math.Max(1, sw.Elapsed.TotalSeconds):0.0} MB/s)");
                Check(resumedFrom >= paused * 0.95, $"resume goes on where it stopped ({paused / 1048576} MB before, {resumedFrom / 1048576} MB after)");
                var missing = again.Files.Where(f => !f.Directory && !f.Temporary && f.Slice is null && (!File.Exists(Path.Combine(again.InstallDir, f.Path)) || new FileInfo(Path.Combine(again.InstallDir, f.Path)).Length != f.Size)).ToList();
                Check(missing.Count == 0, $"every file there with its size ({missing.Count} wrong)");
                Check(again.Exe is null || File.Exists(Path.Combine(again.InstallDir, again.Exe)), "exe there");

                // An update, as Hybrid Launcher runs one: one file changed on disk and a mod file the build does not list. Only the changed
                // file is downloaded again, the rest is kept by checksum, the mod stays.
                var hashed = again.Files.Where(f => !f.Directory && f.Slice is null && f.Size > 0 && f.Sha1 is not null).OrderBy(f => f.Size).ToList();
                Check(hashed.Count > 0, $"the plan has checksums ({hashed.Count} files)");
                var changed = Path.Combine(again.InstallDir, hashed[0].Path);
                var original = File.ReadAllBytes(changed);
                var broken = (byte[])original.Clone();
                broken[0] ^= 0xFF;
                File.WriteAllBytes(changed, broken);
                var mod = Path.Combine(again.InstallDir, "hl-test-mod.dll");
                File.WriteAllText(mod, "mod");
                var update = await HybridLauncher.Core.Accounts.SteamAccount.PlanAsync(pick, Path.Combine(Path.GetTempPath(), "hl-steam-plan"), CancellationToken.None);
                update.Update = true;
                sw.Restart();
                await HybridLauncher.Core.Accounts.DirectDownloader.RunAsync(update, _ => { }, CancellationToken.None);
                Console.WriteLine($"Update finished in {sw.Elapsed.TotalSeconds:0} s");
                Check(File.ReadAllBytes(changed).AsSpan().SequenceEqual(original), "the changed file is downloaded again");
                Check(File.Exists(mod), "a file the build does not list (a mod) stays");
                Directory.Delete(again.InstallDir, true);
            }
        }
        else if (args.Length > 1 && args[1] == "list")
            foreach (var g in library) Console.WriteLine($"  {g.Id}  {g.Name}");
        await Task.Delay(TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 20));
        break;
    }
    case "modinstall":
    {
        // Real installs into fake game folders: a Luma package (alone and with OptiScaler there) and a RenoDX snapshot, then removed.
        var root = Path.Combine(Path.GetTempPath(), "hl-modinstall-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var mods = await Luma.GetModsAsync();
        var builds = await Luma.GetBuildsAsync();
        var prey = Luma.Match(mods, "Prey", null)[0].Mod;
        var version = Luma.Versions(prey, builds, false)[0];
        foreach (var opti in new[] { false, true })
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, opti ? "luma-opti" : "luma")).FullName;
            File.WriteAllText(Path.Combine(dir, "Prey.exe"), "fake");
            File.WriteAllText(Path.Combine(dir, "dxgi.dll"), "original");
            if (opti) File.WriteAllText(Path.Combine(dir, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n");
            var state = await Luma.InstallAsync(prey, version, dir, opti, null, null, CancellationToken.None);
            var top = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToList();
            Console.WriteLine($"Luma {(opti ? "with OptiScaler" : "alone")}: {state.Files.Count} files, top level: {string.Join(", ", top)}");
            Check(Luma.Installed(dir).Count > 0, "Luma found in the folder after install");
            Check(state.Files.All(f => File.Exists(Path.Combine(dir, f))), "every file listed is on disk");
            if (opti)
            {
                Check(File.Exists(Path.Combine(dir, ReShade.OptiDllName)) && File.ReadAllText(Path.Combine(dir, "dxgi.dll")) == "original", "ReShade as ReShade64.dll, dxgi.dll untouched");
                Check(ReShade.OptiLoadsReShade(dir) == true, "LoadReshade = true");
            }
            var problems = Luma.Uninstall(state, dir);
            Check(problems.Count == 0, "uninstalled without problems " + string.Join("; ", problems));
            Check(File.ReadAllText(Path.Combine(dir, "dxgi.dll")) == "original", "dxgi.dll restored");
            Check(Luma.Installed(dir).Count == 0, "no Luma left");
            if (opti) Check(ReShade.OptiLoadsReShade(dir) == false, "LoadReshade back off");
        }
        var reno = await RenoDx.GetModsAsync();
        var cp = RenoDx.Match(reno, "Cyberpunk 2077", null)[0].Mod;
        var renoDir = Directory.CreateDirectory(Path.Combine(root, "renodx")).FullName;
        var link = cp.Snapshot(true);
        Check(link is not null, "Cyberpunk has a 64-bit snapshot");
        if (link is not null)
        {
            var file = await RenoDx.InstallSnapshotAsync(link, renoDir, null, CancellationToken.None);
            Check(File.Exists(file) && RenoDx.Installed(renoDir).Count == 1, $"snapshot installed: {Path.GetFileName(file)} {new FileInfo(file).Length / 1024} KB");
        }
        Directory.Delete(root, true);
        break;
    }
    case "renodxf":
    {
        var mods = await RenoDx.GetModsAsync();
        Console.WriteLine($"{mods.Count(m => m.SourceDir is not null)} of {mods.Count} mods have a source folder");
        foreach (var name in new[] { "Cyberpunk 2077", "Absolum", "Elden Ring" })
        {
            var mod = RenoDx.Match(mods, name, null).FirstOrDefault()?.Mod;
            var f = mod is null ? null : await RenoDx.GetFeaturesAsync(mod);
            Console.WriteLine($"{name}: {mod?.SourceOwner}/{mod?.SourceDir} -> {f?.Count ?? -1} settings, updated {f?.Updated:d}");
            if (f is not null) foreach (var (sec, list) in f.Sections) Console.WriteLine($"   [{sec}] " + string.Join(", ", list.Select(x => x.Label + (x.Choices.Count > 0 ? "(" + string.Join("/", x.Choices) + ")" : ""))));
            Check(f is { Count: > 0 }, $"{name} features");
        }
        break;
    }
    case "reviews":
    {
        var set = await HybridLauncher.Core.Media.SteamReviews.GetAsync(args.Length > 1 ? args[1] : "1091500");
        Check(set is not null, "reviews read");
        if (set is null) break;
        Console.WriteLine($"{set.Summary} ({set.Share:P0} of {set.Total}), recent: {set.RecentSummary}");
        foreach (var r in set.Top) Console.WriteLine($"  {(r.Recommended ? "+" : "-")} {r.Author} · {r.Hours} · {r.Posted} · {r.Helpful}\n    {r.Text[..Math.Min(120, r.Text.Length)].Replace('\n', ' ')}");
        Check(set.Total > 1000 && set.Top.Count > 0, "summary and top reviews");
        break;
    }
    case "owned":
    {
        // Launcher libraries as the Downloads tab reads them (read-only).
        Console.WriteLine($"Steam login: {HybridLauncher.Core.Accounts.OwnedGames.SteamLogin()?.Name ?? "none"}");
        foreach (var store in new[] { GameStore.Steam, GameStore.Epic, GameStore.Gog, GameStore.Xbox })
        {
            var list = await HybridLauncher.Core.Accounts.OwnedGames.LoadAsync(store, new LinkedAccounts(), CancellationToken.None);
            Console.WriteLine($"{store}: {list.Games.Count} games from {list.Source}{(list.Problem is null ? "" : "  (" + list.Problem + ")")}");
            foreach (var g in list.Games.Take(4)) Console.WriteLine($"   {g.Id}  {g.Name}  {g.InstallUri}");
            if (store != GameStore.Xbox) Check(list.Games.Count > 0 || list.Problem is not null, $"{store} listed or explained");
        }
        break;
    }
    case "renodx":
    {
        var mods = await RenoDx.GetModsAsync();
        Console.WriteLine($"{mods.Count} mods, {mods.Count(m => m.Engine is not null)} generic");
        Check(mods.Count > 100, "list parsed");
        foreach (var m in mods.Where(m => m.Engine is not null))
            Console.WriteLine($"  generic {m.Engine}: {m.Name} ({m.Links.Count} links, {m.Tested.Count} tested)");
        foreach (var (name, engine) in new[] { ("Cyberpunk 2077", (string?)null), ("Black Myth: Wukong", "Unreal"), ("A Plague Tale: Innocence", null), ("Hollow Knight", "Unity"), ("Some Unknown Game", null) })
        {
            var hits = RenoDx.Match(mods, name, engine);
            Console.WriteLine($"{name}: " + string.Join(" | ", hits.Take(3).Select(h => $"{h.Mod.Name} [{h.Score}] {h.Reason} {h.Mod.Snapshot(true)?.Url ?? h.Mod.Nexus?.Url}")));
        }
        Check(RenoDx.Match(mods, "Cyberpunk 2077", null).Count > 0, "Cyberpunk matched");
        Check(RenoDx.Match(mods, "Some Unknown Game", null).Count == 0, "unknown game has no match");
        break;
    }
    case "reshade":
    {
        var versions = await ReShade.GetVersionsAsync();
        Console.WriteLine("ReShade versions: " + string.Join(", ", versions.Take(6)));
        Check(versions.Count > 0, "versions listed");
        var dir = Path.Combine(Path.GetTempPath(), "hl-reshade-test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var installer = await ReShade.DownloadAsync(new ReShadeRelease(versions[0], true), null, CancellationToken.None);
        Check(File.Exists(installer), $"installer downloaded {installer}");
        File.WriteAllText(Path.Combine(dir, "OptiScaler.ini"), "[Plugins]\n; Load ReShade64.dll\n; true or false - Default (auto) is false\nLoadReshade=auto\n");
        // A stand-in "ReShade dll": the installer itself carries ReShade's version resource.
        File.Copy(installer, Path.Combine(dir, "d3d12.dll"));
        var gs = new GameSettings();
        var res = ReShade.LoadThroughOpti(dir, gs, "dxgi.dll");
        Console.WriteLine(res.Message);
        Check(File.Exists(Path.Combine(dir, "ReShade64.dll")) && !File.Exists(Path.Combine(dir, "d3d12.dll")), "renamed to ReShade64.dll");
        Check(ReShade.OptiLoadsReShade(dir) == true, "LoadReshade = true");
        Console.WriteLine(ReShade.StopLoadingThroughOpti(dir, gs));
        Check(File.Exists(Path.Combine(dir, "d3d12.dll")) && ReShade.OptiLoadsReShade(dir) == false, "undone");
        // ReShade's installer wrote over OptiScaler's dxgi.dll: loading it through OptiScaler reports it, so OptiScaler is reinstalled.
        File.Move(Path.Combine(dir, "d3d12.dll"), Path.Combine(dir, "dxgi.dll"));
        Check(ReShade.LoadThroughOpti(dir, gs, "dxgi.dll").OptiDllReplaced, "over OptiScaler's dll: reported");
        // Removed by Hybrid Launcher: ReShade64.dll included (ReShade's own setup does not see it), LoadReshade off, nothing left detected.
        File.WriteAllText(Path.Combine(dir, "ReShade.log"), "log");
        Console.WriteLine(ReShade.Uninstall(dir, gs));
        Check(ReShade.Detect(dir) is null && !File.Exists(Path.Combine(dir, "ReShade64.dll")) && !File.Exists(Path.Combine(dir, "ReShade.log")), "removed: not detected any more");
        Check(ReShade.OptiLoadsReShade(dir) == false && !gs.IniOverrides.ContainsKey(ReShade.IniKey), "removed: LoadReshade off");
        break;
    }
    case "scan":
    {
        var games = await GameScanner.ScanAsync(new AppSettings());
        foreach (var g in games)
            Console.WriteLine($"{g.Store,-6} {g.Name,-42} {g.MainExe ?? "(no exe)"}");
        break;
    }
    case "ini":
    {
        var doc = IniDocument.Load(args[1]);
        Console.WriteLine($"{doc.Entries.Count} entries");
        foreach (var grp in doc.Entries.GroupBy(e => e.Kind))
            Console.WriteLine($"  {grp.Key}: {grp.Count()}");
        foreach (var e in doc.Entries.Where(e => e.Kind == IniValueKind.Choice))
            Console.WriteLine($"  {e.Path} = {e.Value} [{string.Join(", ", e.Options)}] default={e.DefaultText}");
        var copy = Path.Combine(Path.GetTempPath(), "hl-ini-test.ini");
        File.Copy(args[1], copy, true);
        var d2 = IniDocument.Load(copy);
        d2.Save();
        Check(File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(args[1])), "load+save is byte identical");
        Check(d2.Set("Upscalers", "Dx12Upscaler", "fsr31"), "set Dx12Upscaler");
        d2.Save();
        Check(IniDocument.Load(copy).Get("Upscalers", "Dx12Upscaler") == "fsr31", "value persisted");
        Check(!d2.Set("Nope", "Missing", "1"), "unknown key is not added");
        break;
    }
    case "hw":
    {
        var hw = HybridLauncher.Core.Hardware.HardwareInfo.Read();
        Console.WriteLine($"CPU  {hw.Cpu}");
        foreach (var g in hw.Gpus) Console.WriteLine($"GPU  {g}\n     {string.Join(", ", g.Techs.Select(t => $"{t.Name} {t.Version}"))}");
        Console.WriteLine($"RAM  {HybridLauncher.Core.Hardware.HardwareInfo.FormatBytes(hw.Memory.TotalBytes)}, {hw.Memory.Modules.Count} modules");
        foreach (var m in hw.Memory.Modules) Console.WriteLine($"     {m}");
        foreach (var d in hw.Displays) Console.WriteLine($"DISP {d}");
        Console.WriteLine($"SYS  {hw.System}");
        foreach (var d in hw.Drives) Console.WriteLine($"DRV  {d}");
        Check(hw.Cpu.Threads > 0 && hw.Cpu.Name != "Unknown CPU", "cpu read");
        Check(hw.Gpus.Count > 0, "at least one gpu");
        Check(hw.Memory.TotalBytes > 0, "memory read");
        Check(hw.Displays.Count > 0 && hw.Displays.All(d => d.Width > 0 && d.RefreshHz > 0), "displays read");
        Check(HybridLauncher.Core.Hardware.HardwareInfo.NvidiaVersion("32.0.15.6094") == "560.94", "nvidia driver version");
        break;
    }
    case "api":
    {
        var sample = "{{API\n|direct3d versions      = 11, 12\n|direct3d notes         = \n|vulkan versions        = 1.3<ref>x</ref>\n|opengl versions        = \n}}";
        Check(HybridLauncher.Core.Media.GraphicsApi.ParseWikitext(sample).SequenceEqual(["DX11", "DX12", "Vulkan"]), "wikitext parse");
        var games = await GameScanner.ScanAsync(new AppSettings());
        foreach (var g in games)
        {
            var info = await HybridLauncher.Core.Media.GraphicsApi.GetAsync(g, g.MainExe, g.SteamAppId);
            var local = g.MainExe is null ? HybridLauncher.Core.Media.GraphicsApiInfo.Unknown : HybridLauncher.Core.Media.GraphicsApi.FromExe(g.MainExe);
            Console.WriteLine($"  {g.Name,-40} {info.Display,-14} ({info.Source})   exe: {local.Display}");
        }
        foreach (var g in games.Where(g => g.Store == GameStore.Xbox))
            Check(g.IconPath is not null && File.Exists(g.IconPath), $"xbox logo {g.Name}: {g.IconPath}");
        break;
    }
    case "versions":
    {
        // Saved OptiScaler versions in HL_DATA (run after "install ... nightly" so there is one).
        var list = await SavedVersions.ListAsync(new AppSettings());
        foreach (var v in list) Console.WriteLine($"  {v.ChannelLabel,-8} {v.Name,-28} {DiskUsage.Format(v.Bytes),10}  {v.Folder}");
        Check(list.Count > 0, "at least one saved version");
        if (list.FirstOrDefault(v => v.Channel != OptiChannel.Custom) is { } d)
        {
            SavedVersions.Delete(d);
            Check(!Directory.Exists(d.Folder), $"deleted {d.Name}");
        }
        break;
    }
    case "nexus":
    {
        foreach (var (name, domain) in new[] {
            ("Marvel's Spider-Man 2", "marvelsspiderman2"),
            ("Cyberpunk 2077", "cyberpunk2077"),
            ("Some Game That Does Not Exist 9", null) })
        {
            var found = await HybridLauncher.Core.Media.NexusMods.DomainForAsync(name);
            Check(found == domain, $"{name} -> {found ?? "(none)"}");
        }
        break;
    }
    case "info":
    {
        // Overview data for one game: Steam store details, PCGamingWiki, HowLongToBeat, the OptiScaler wiki page.
        var appId = args.Length > 1 ? args[1] : "1091500";
        var name = args.Length > 2 ? args[2] : "Cyberpunk 2077";
        var game = new Game { Id = $"steam:{appId}", Name = name, Store = GameStore.Steam, InstallDir = Path.GetTempPath(), SteamAppId = appId };

        var store = await HybridLauncher.Core.Media.StoreDetails.GetAsync(appId);
        Check(store is not null, "store details");
        if (store is not null)
        {
            Console.WriteLine($"  {store.Name}: {store.Screenshots.Count} screenshots, {store.Movies.Count} trailers, {store.Languages.Count} languages, {store.About.Count} about lines");
            Console.WriteLine($"  controllers: xbox full {store.XboxFull}, dualshock {store.DualShock}, dualsense {store.DualSense}");
            foreach (var r in store.Minimum.Take(4)) Console.WriteLine($"  min {r.Name}: {r.Value}");
            Check(store.Screenshots.Count > 0 && store.Minimum.Count > 0 && store.Languages.Count > 0, "store media, requirements, languages");
        }

        var wiki = await HybridLauncher.Core.Media.Pcgw.GetAsync(game, appId);
        Check(wiki is not null, "PCGamingWiki page");
        if (wiki is not null)
        {
            Console.WriteLine($"  {wiki.Title}: upscaling [{string.Join(", ", wiki.UpscalingTech)}], fg [{string.Join(", ", wiki.FrameGenTech)}]");
            Console.WriteLine($"  tags: {string.Join(" | ", HybridLauncher.Core.Media.Pcgw.UpscalerTags(string.Join(", ", wiki.UpscalingTech)))}");
            Console.WriteLine($"  dualsense: triggers {wiki.AdaptiveTriggers?.State}, haptics {wiki.Haptics?.State}; video {wiki.Video.Count}, input {wiki.Input.Count}");
            Console.WriteLine($"  improvements {wiki.Improvements.Count}, unresolved {wiki.IssuesUnresolved.Count}, fixed {wiki.IssuesFixed.Count}");
            foreach (var t in wiki.IssuesUnresolved.Take(2))
            {
                Console.WriteLine($"  ISSUE {t.Title}");
                foreach (var l in t.Body.Take(4)) Console.WriteLine($"     {l}");
            }
            foreach (var p in wiki.ConfigPaths.Concat(wiki.SavePaths)) Console.WriteLine($"  path {p.Platform}: {p.Path}");
            Console.WriteLine($"  config folder here: {HybridLauncher.Core.Media.Pcgw.ExistingFolder(wiki.ConfigPaths, GameStore.Steam) ?? "(none)"}");
            Check(wiki.UpscalingTech.Count > 0 && wiki.ConfigPaths.Count > 0, "PCGamingWiki fields and paths");
        }

        var hltb = await HybridLauncher.Core.Media.HowLongToBeat.GetAsync(name);
        Check(hltb is not null, $"HowLongToBeat {hltb?.Name}: {hltb?.Main} / {hltb?.MainExtra} / {hltb?.Completionist} h");

        var opti = await CompatWiki.GetEntryAsync(name);
        Check(opti is not null, $"OptiScaler wiki {opti?.Title}");
        if (opti is not null)
            foreach (var f in opti.Fields) Console.WriteLine($"  {f.Name}: {string.Join(" / ", f.Lines.Select(l => l.ToString()))}");
        break;
    }
    case "ach":
    {
        // Read-only: Steam's own appcache\stats files for the given app id (default: every game with a schema).
        Console.WriteLine($"Stats dir {HybridLauncher.Core.Steam.SteamAchievements.StatsDir}, account {HybridLauncher.Core.Steam.SteamAchievements.CurrentAccountId()}");
        var ids = args.Length > 1 ? [args[1]]
            : Directory.GetFiles(HybridLauncher.Core.Steam.SteamAchievements.StatsDir ?? ".", "UserGameStatsSchema_*.bin")
                .Select(f => Path.GetFileNameWithoutExtension(f)["UserGameStatsSchema_".Length..]).ToArray();
        foreach (var id in ids)
        {
            var set = await HybridLauncher.Core.Steam.SteamAchievements.LoadAsync(id);
            if (set is null) { Console.WriteLine($"{id,-8} no schema"); continue; }
            Console.WriteLine($"{id,-8} {set.UnlockedCount,3} / {set.Total,-3} unlocked");
            if (args.Length > 1)
                foreach (var a in set.Items.OrderByDescending(a => a.Unlocked).Take(12))
                    Console.WriteLine($"    {(a.Unlocked ? "✔" : " ")} {a.Name} — {a.Description} {(a.UnlockedAt is { } t ? t.ToString("yyyy-MM-dd") : "")} {a.GlobalPercent:0.#}% {(a.Hidden ? "(hidden)" : "")}");
        }
        break;
    }
    case "wiki":
    {
        foreach (var (name, slug) in new[] {
            ("Marvel's Spider-Man 2", "Marvels-Spider%E2%80%90Man-2"),
            ("Cyberpunk 2077", "Cyberpunk-2077"),
            ("Some Game That Does Not Exist 9", "Compatibility-List") })
        {
            var url = await CompatWiki.PageForAsync(name);
            Check(url.EndsWith("/" + slug), $"{name} -> {url}");
        }
        break;
    }
    case "art":
    {
        // The banner, logo and cover a game page uses: the store's own asset list first, a screenshot before the small header.
        var id = args.ElementAtOrDefault(1) ?? "311340";
        var game = new Game { Id = "steam:" + id, Name = args.ElementAtOrDefault(2) ?? id, Store = GameStore.Steam, SteamAppId = id, InstallDir = "" };
        var art = await HybridLauncher.Core.Media.GameInfo.GetAsync(game);
        var cover = await HybridLauncher.Core.Media.GameInfo.GetCoverAsync(game);
        Console.WriteLine($"  hero  {art.HeroImage}");
        Console.WriteLine($"  logo  {art.LogoImage}");
        Console.WriteLine($"  cover {cover}");
        Check(art.HeroImage is not null && !art.HeroImage.EndsWith("-header.jpg"), "banner is a large picture, not the 460 px header");
        break;
    }
    case "signin-page":
    {
        // Only the local page is exercised: nothing is sent to MEGA (an empty form never leaves Hybrid Launcher).
        string? url = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(args.ElementAtOrDefault(1) == "hold" ? 150 : 30));
        var signIn = HybridLauncher.Core.Cloud.MegaAuth.SignInInBrowserAsync(u => url = u, cts.Token);
        using var http = new HttpClient();
        // "signin-page hold": two minutes to look at the page in a browser before the checks.
        if (args.ElementAtOrDefault(1) == "hold") { Console.WriteLine(url); await Task.Delay(TimeSpan.FromMinutes(2)); }
        Check(url is not null && url.StartsWith("http://127.0.0.1:"), $"page on loopback ({url})");
        var form = await http.GetStringAsync(url!);
        Check(form.Contains("name=\"email\"") && form.Contains("type=\"password\"") && form.Contains("autocomplete=\"current-password\""), "form with e-mail and password fields");
        var origin = url![..url!.LastIndexOf('/')];
        Check((await http.GetAsync(origin + "/favicon.ico")).StatusCode == System.Net.HttpStatusCode.NotFound, "other paths answer 404");
        Check((await http.GetAsync(origin + "/wrong-token")).StatusCode == System.Net.HttpStatusCode.NotFound, "a wrong token answers 404");
        var empty = await (await http.PostAsync(url, new FormUrlEncodedContent([new("email", "")]))).Content.ReadAsStringAsync();
        Check(empty.Contains("class=\"err\""), "empty form shown again with an error");
        Check(!signIn.IsCompleted, "still waiting for a sign-in");
        cts.Cancel();
        try { await signIn; Check(false, "cancel ends the sign-in"); }
        catch (OperationCanceledException) { Check(true, "cancel ends the sign-in"); }
        break;
    }
    case "cloud":
    case "cloud-live":
    {
        // Two "PCs" share one cloud: each has its own settings for the game and its own saves folder. "cloud" uses a temp
        // folder; "cloud-live mega" the account signed in under HL_DATA (a scratch copy of the token), on a test game.
        var root = Path.Combine(Path.GetTempPath(), "hl-cloud-" + Guid.NewGuid().ToString("N")[..8]);
        var cloudDir = Directory.CreateDirectory(Path.Combine(root, "cloud")).FullName;
        string savesA = Directory.CreateDirectory(Path.Combine(root, "pcA", "saves")).FullName, savesB = Path.Combine(root, "pcB", "saves");
        var provider = cmd == "cloud" ? "folder" : args.ElementAtOrDefault(1) ?? "mega";
        SettingsStore.Current.Cloud = new CloudSaveSettings { Enabled = true, Providers = [provider], Folder = cloudDir, KeepVersions = 2 };
        Check(CloudSaves.On, $"provider {provider} ready");
        var game = new Game { Id = "steam:smoke-" + Guid.NewGuid().ToString("N")[..6], Name = "Hybrid Launcher smoke: Game", Store = GameStore.Steam, InstallDir = root };
        var a = new GameSettings { CloudSaveDir = savesA };
        var b = new GameSettings { CloudSaveDir = savesB };
        Func<CloudStatus, Task<CloudSaves.Choice>> keepHere = _ => Task.FromResult(CloudSaves.Choice.KeepHere);
        Func<CloudStatus, Task<CloudSaves.Choice>> useCloud = _ => Task.FromResult(CloudSaves.Choice.UseCloud);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            File.WriteAllText(Path.Combine(savesA, "slot1.sav"), "A1");
            Directory.CreateDirectory(Path.Combine(savesA, "sub"));
            File.WriteAllText(Path.Combine(savesA, "sub", "profile.dat"), "P");
            Check((await CloudSaves.StatusAsync(game, a, fresh: true)).State == CloudState.NotUploaded, "new saves: not uploaded");
            Check(await CloudSaves.AfterSessionAsync(game, a), "after session: sent");
            Check(await CloudSaves.ReadManifestAsync(game, fresh: true) is { Files: 2 }, "manifest lists 2 files");
            Check((await CloudSaves.StatusAsync(game, a, fresh: true)).State == CloudState.Synced, "A synced");
            Check((await CloudSaves.StatusAsync(game, b, fresh: true)).State == CloudState.ChangedInCloud, "B (empty) sees newer in cloud");
            await CloudSaves.BeforeLaunchAsync(game, b, keepHere);
            Check(File.ReadAllText(Path.Combine(savesB, "slot1.sav")) == "A1" && File.Exists(Path.Combine(savesB, "sub", "profile.dat")), "B restored before launch");
            File.WriteAllText(Path.Combine(savesB, "slot1.sav"), "B2");
            await CloudSaves.AfterSessionAsync(game, b);
            Check((await CloudSaves.HistoryAsync(game)).Count == 1, "older cloud copy kept in history");
            Check((await CloudSaves.StatusAsync(game, a, fresh: true)).State == CloudState.ChangedInCloud, "A sees B's newer saves");
            await CloudSaves.BeforeLaunchAsync(game, a, keepHere);
            Check(File.ReadAllText(Path.Combine(savesA, "slot1.sav")) == "B2", "A got B2");
            Check(Directory.GetFiles(CloudSaves.LocalBackupsDir(game), "*.zip").Length >= 1, "A's saves backed up before restore");
            // Both change: conflict, resolved both ways.
            File.WriteAllText(Path.Combine(savesA, "slot1.sav"), "A3");
            File.WriteAllText(Path.Combine(savesB, "slot1.sav"), "B3");
            await CloudSaves.AfterSessionAsync(game, b);
            Check((await CloudSaves.StatusAsync(game, a, fresh: true)).State == CloudState.Conflict, "both changed: conflict");
            await CloudSaves.BeforeLaunchAsync(game, a, useCloud);
            Check(File.ReadAllText(Path.Combine(savesA, "slot1.sav")) == "B3", "conflict -> use cloud");
            File.WriteAllText(Path.Combine(savesA, "slot1.sav"), "A4");
            File.WriteAllText(Path.Combine(savesB, "slot1.sav"), "B4");
            await CloudSaves.AfterSessionAsync(game, b);
            await CloudSaves.BeforeLaunchAsync(game, a, keepHere);
            Check((await CloudSaves.ReadManifestAsync(game, fresh: true))?.Hash == CloudSaves.Fingerprint(savesA).Hash, "conflict -> keep here uploads A4");
            var history = await CloudSaves.HistoryAsync(game);
            Check(history.Count == 2, $"history trimmed to KeepVersions (2): {history.Count}");
            await CloudSaves.RestoreAsync(game, a, history.Last());
            Check((await CloudSaves.StatusAsync(game, a, fresh: true)).State == CloudState.ChangedHere, "restored history copy goes up next");
            Check(CloudSaves.TooBroad(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) && CloudSaves.TooBroad(@"C:\") && !CloudSaves.TooBroad(savesA), "broad folders refused");
            var cancelled = new GameSettings { CloudSaveDir = Path.Combine(root, "pcC", "saves") };
            try { await CloudSaves.BeforeLaunchAsync(game, cancelled, keepHere, new CancellationToken(true)); Check(false, "cancelled restore throws"); }
            catch (OperationCanceledException) { Check(!Directory.Exists(cancelled.CloudSaveDir), "cancelled restore touched nothing"); }

            // Saves, the game's settings folder and the Steam emulator's achievements go up together and come back each to its place.
            var emuGame = new Game { Id = game.Id + "-emu", Name = "Hybrid Launcher smoke: Emulated", Store = GameStore.Custom, InstallDir = Path.Combine(root, "install"), EmulatorAppId = "999999901" };
            var gseRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves");
            bool gseExisted = Directory.Exists(gseRoot);
            var gse = Directory.CreateDirectory(Path.Combine(gseRoot, emuGame.EmulatorAppId)).FullName;
            try
            {
                var a2 = new GameSettings { CloudSaveDir = Directory.CreateDirectory(Path.Combine(root, "pcA", "saves2")).FullName, CloudConfigDir = Directory.CreateDirectory(Path.Combine(root, "pcA", "config2")).FullName };
                var b2 = new GameSettings { CloudSaveDir = Path.Combine(root, "pcB", "saves2"), CloudConfigDir = Directory.CreateDirectory(Path.Combine(root, "pcB", "config2")).FullName };
                File.WriteAllText(Path.Combine(a2.CloudSaveDir, "slot.sav"), "S");
                File.WriteAllText(Path.Combine(a2.CloudConfigDir, "graphics.ini"), "[gfx]");
                File.WriteAllText(Path.Combine(gse, "achievements.json"), "{\"ACH_1\":{\"earned\":true}}");
                Check(CloudSaves.Parts(emuGame, a2).Select(p => p.Name).SequenceEqual(["saves", "config", "goldberg"]), "parts: saves, config, goldberg");
                Check(CloudSaves.Parts(emuGame, new GameSettings { CloudSaveDir = a2.CloudSaveDir, CloudConfigDir = Directory.CreateDirectory(Path.Combine(emuGame.InstallDir, "cfg")).FullName })
                    .All(p => p.Name != "config"), "a settings folder inside the install folder is left out");
                Check(await CloudSaves.AfterSessionAsync(emuGame, a2), "three parts sent");
                Check(await CloudSaves.ReadManifestAsync(emuGame, fresh: true) is { Files: 3, Parts.Count: 3 }, "manifest lists 3 files in 3 parts");
                Directory.Delete(gse, true);
                await CloudSaves.BeforeLaunchAsync(emuGame, b2, keepHere);
                Check(File.ReadAllText(Path.Combine(b2.CloudSaveDir, "slot.sav")) == "S" && File.ReadAllText(Path.Combine(b2.CloudConfigDir, "graphics.ini")) == "[gfx]"
                    && File.Exists(Path.Combine(gse, "achievements.json")), "each part restored to its folder (emulator's recreated)");
                Check((await CloudSaves.StatusAsync(emuGame, b2, fresh: true)).State == CloudState.Synced, "B synced after the three parts came down");
                await CloudSaves.DeleteFromCloudAsync(emuGame);
            }
            finally
            {
                try { Directory.Delete(gse, true); if (!gseExisted) Directory.Delete(gseRoot, true); Directory.Delete(CloudSaves.LocalBackupsDir(emuGame), true); } catch { }
            }
            Console.WriteLine($"  {provider}: {watch.Elapsed.TotalSeconds:0.0} s");
        }
        finally
        {
            try { await CloudSaves.DeleteFromCloudAsync(game); Check(await CloudSaves.ReadManifestAsync(game, fresh: true) is null, "test game removed from the cloud"); }
            catch (Exception ex) { Console.WriteLine($"  cleanup: {ex.Message}"); }
            try { Directory.Delete(root, true); Directory.Delete(CloudSaves.LocalBackupsDir(game), true); } catch { }
        }
        break;
    }

    case "report":
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Log.Info($"Report test: saves in {home}\\Saved Games, signed in as someone.test@example.com on {Environment.MachineName}");
        for (int i = 0; i < 400; i++) Log.Info($"Report test filler line {i} with a path {home}\\AppData\\Local\\x");
        var scrubbed = BugReport.Scrub($"{home}\\a  {home.Replace('\\', '/')}/b  me@mail.com  {Environment.MachineName}");
        Check(!scrubbed.Contains(home, StringComparison.OrdinalIgnoreCase) && !scrubbed.Contains("me@mail.com") && !scrubbed.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), "user folder, e-mail and PC name taken out: " + scrubbed);
        Check(BugReport.Scrub("LorenzoKy/Hybrid-Launcher") == "LorenzoKy/Hybrid-Launcher", "names inside longer words stay");
        var file = BugReport.SaveLog();
        var saved = File.ReadAllText(file);
        Check(saved.Contains("Report test filler line 399") && !saved.Contains(home, StringComparison.OrdinalIgnoreCase) && !saved.Contains("someone.test@example.com"), "saved log is whole and scrubbed");
        var url = BugReport.FormUrl("1.0.0", BugReport.SystemLine(), Path.GetFileName(file));
        Console.WriteLine($"  url {url.Length} chars, system: {BugReport.SystemLine()}");
        Check(url.Length <= 7800 && url.Contains("template=bug_report.yml") && url.Contains("&version=1.0.0") && url.Contains("filler%20line%20399"), "form address fits and ends with the newest lines");
        Check(Path.GetFileName(Log.CurrentFile).StartsWith("hl-") && Path.GetFileName(Log.CurrentFile).Length > "hl-2026-10-30.log".Length, "one log per session: " + Path.GetFileName(Log.CurrentFile));
        break;
    }

    case "cloud-mirror":
    {
        // Two clouds mirror each other: an upload reaches both, the newest copy wins, a cloud that missed one catches up,
        // older copies keep the cloud they came from, and a removal empties both.
        var root = Path.Combine(Path.GetTempPath(), "hl-mirror-" + Guid.NewGuid().ToString("N")[..8]);
        var a = new HybridLauncher.Core.Cloud.FolderStore(Directory.CreateDirectory(Path.Combine(root, "a")).FullName, "A");
        var b = new HybridLauncher.Core.Cloud.FolderStore(Directory.CreateDirectory(Path.Combine(root, "b")).FullName, "B");
        var mirror = new HybridLauncher.Core.Cloud.MirrorStore([a, b]);
        const string key = "Mirror test [smoke]";
        string Zip(string text)
        {
            var z = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
            using (var archive = System.IO.Compression.ZipFile.Open(z, System.IO.Compression.ZipArchiveMode.Create))
            using (var w = new StreamWriter(archive.CreateEntry("slot.sav").Open())) w.Write(text);
            return z;
        }
        CloudManifest M(string hash, int minutes) => new() { GameId = "smoke", Name = "Mirror", Hash = hash, Files = 1, UpdatedUtc = DateTime.UtcNow.AddMinutes(minutes), Machine = "PC" };
        try
        {
            Check(mirror.Name == "A + B", "name lists both clouds");
            await mirror.PutAsync(key, Zip("one"), M("h1", 0), null, 3, CancellationToken.None);
            Check((await a.ManifestAsync(key, default))?.Hash == "h1" && (await b.ManifestAsync(key, default))?.Hash == "h1", "an upload reaches both");
            // Another PC sent newer saves to A only.
            await a.PutAsync(key, Zip("two"), M("h2", 5), await a.ManifestAsync(key, default), 3, CancellationToken.None);
            Check((await mirror.ManifestAsync(key, default)) is { Hash: "h2", Partial: true }, "the newest copy wins, and B is seen as behind");
            var got = Path.Combine(root, "got.zip");
            await mirror.GetAsync(key, null, got, default);
            using (var z = System.IO.Compression.ZipFile.OpenRead(got))
            using (var r = new StreamReader(z.GetEntry("slot.sav")!.Open())) Check(r.ReadToEnd() == "two", "saves come from the newest cloud");
            int historyA = (await a.HistoryAsync(key, default)).Count;
            await mirror.PutAsync(key, Zip("two"), M("h2", 6), null, 3, CancellationToken.None);
            Check((await b.ManifestAsync(key, default))?.Hash == "h2", "B caught up");
            Check((await a.HistoryAsync(key, default)).Count == historyA, "A, already up to date, was left alone");
            Check((await mirror.ManifestAsync(key, default)) is { Hash: "h2", Partial: false }, "both hold the same saves");
            var history = await mirror.HistoryAsync(key, default);
            Check(history.Count > 0 && history.All(c => c.Id.Contains('|')), "older copies carry their cloud");
            await mirror.GetAsync(key, history[0].Id, got, default);
            Check(new FileInfo(got).Length > 0, "an older copy comes back");
            await mirror.DeleteAsync(key, default);
            Check(await a.ManifestAsync(key, default) is null && await b.ManifestAsync(key, default) is null, "removed from both");
            // A cloud turned on later (OneDrive beside MEGA) holds nothing yet: the saves are not everywhere, so they go up to it.
            await a.PutAsync(key, Zip("three"), M("h3", 7), null, 3, CancellationToken.None);
            Check((await mirror.ManifestAsync(key, default)) is { Hash: "h3", Partial: true }, "a cloud added later is seen as missing the saves");
            await mirror.PutAsync(key, Zip("three"), M("h3", 8), null, 3, CancellationToken.None);
            Check((await b.ManifestAsync(key, default))?.Hash == "h3", "the cloud added later receives them");
            await mirror.DeleteAsync(key, default);
            // The folder takes the new name over an earlier one.
            var old = Directory.CreateDirectory(Path.Combine(root, "c", "Hybrid Game Launcher Saves", key)).FullName;
            File.WriteAllText(Path.Combine(old, "manifest.json"), "{}");
            var c = new HybridLauncher.Core.Cloud.FolderStore(Path.Combine(root, "c"));
            Check(c.GameDir(key).Contains(Path.Combine("Hybrid Cloud Saves", key)) && Directory.Exists(c.GameDir(key)), "the old cloud folder is renamed to Hybrid Cloud Saves");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        break;
    }

    case "decide":
    {
        var o = new OptiSetupOptions { DllName = "winmm.dll", Nvidia = null, Spoofing = false, OptiPatcher = true };
        Check(BatDriver_Decide("...\nEnter 1-8 (or press Enter for default): ", o) == "2", "dll prompt -> winmm = 2");
        Check(BatDriver_Decide("Enter 1 or 2 (Detected AMD/Intel): ", o) == "1", "gpu auto on AMD -> 1");
        Check(BatDriver_Decide("Enter 1 or 2 (Detected Nvidia): ", o) == "2", "gpu auto on Nvidia -> 2");
        Check(BatDriver_Decide("Enter 1 or 2 (Detected Nvidia): ", new OptiSetupOptions { Nvidia = false }) == "1", "forced AMD on Nvidia box -> 1");
        Check(BatDriver_Decide("Will you try...\nEnter 1 or 2 (or press Enter for Yes): ", o) == "2", "spoofing off -> 2");
        Check(BatDriver_Decide("Download OptiPatcher.asi?\n\n[1] Yes\n[2] No\n\nWaiting - ", o) == "1", "optipatcher -> 1");
        Check(BatDriver_Decide("Do you want to overwrite dxgi.dll?\n[1] Yes\nWaiting - ", o) == "1", "overwrite -> 1");
        Check(BatDriver_Decide("Premere un tasto per continuare . . . ", o) == "", "localized pause -> Enter");
        Check(BatDriver_Decide("Something new?\nWaiting - ", o, expectUnknown: true) is null, "unknown question is refused");
        Check(BatDriver_Decide("Choose a filename for OptiScaler", o) is null, "partial output waits");
        // Issue #1: regional formats Windows has only in Unicode (Hindi, Georgian, ...) report OEM code page 1, which .NET cannot open.
        foreach (var name in new[] { "hi-IN", "ka-GE", "hy-AM" })
        {
            var cp = System.Globalization.CultureInfo.GetCultureInfo(name).TextInfo.OEMCodePage;
            Check(BatDriver.OutputEncoding(cp).CodePage == 65001, $"{name} (OEM code page {cp}) -> UTF-8, no exception");
        }
        Check(BatDriver.OutputEncoding(0).CodePage == 65001, "code page 0 (CP_ACP) -> UTF-8");
        Check(BatDriver.OutputEncoding(437).CodePage == 437 && BatDriver.OutputEncoding(850).CodePage == 850, "OEM 437 and 850 kept");
        Check(BatDriver.OutputEncoding(65001).CodePage == 65001, "UTF-8 system code page kept");
        Check(BatDriver.OutputEncoding(99999).CodePage == 65001, "unknown code page -> UTF-8");
        {
            // The bat itself under a Unicode-only format: it runs and its pause is answered.
            var saved = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("hi-IN");
            var batDir = Path.Combine(Path.GetTempPath(), "hl-codepage-bat");
            ArchiveExtractor.ResetDir(batDir);
            var bat = Path.Combine(batDir, "setup_windows.bat");
            File.WriteAllText(bat, "@echo off\r\necho Setup started\r\npause\r\necho Setup done\r\n");
            try
            {
                var r = await BatDriver.RunAsync(bat, o, null, CancellationToken.None);
                Check(r.Completed && r.Transcript.Contains("Setup done"), $"bat runs under hi-IN ({r.Failure ?? "ok"})");
            }
            catch (Exception ex) { Check(false, $"bat runs under hi-IN: {ex.GetType().Name}: {ex.Message}"); }
            finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
        }
        {
            // Whatever Windows' format, the app runs in one it is tested in, without the user's own separators.
            var saved = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture);
            var odd = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo("en-US").Clone();
            odd.NumberFormat.NumberDecimalSeparator = ",";
            foreach (var (system, setting) in new (System.Globalization.CultureInfo, string)[]
            {
                (System.Globalization.CultureInfo.GetCultureInfo("hi-IN"), "auto"), (System.Globalization.CultureInfo.GetCultureInfo("ka-GE"), "en"),
                (System.Globalization.CultureInfo.GetCultureInfo("th-TH"), "en"), (System.Globalization.CultureInfo.GetCultureInfo("fa-IR"), "it"),
                (System.Globalization.CultureInfo.GetCultureInfo("ar-SA"), "en"), (System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), "en"),
                (System.Globalization.CultureInfo.GetCultureInfo("en-ZA"), "en"), (odd, "en"),
            })
            {
                System.Globalization.CultureInfo.CurrentCulture = system;
                L.Init(setting);
                var now = System.Globalization.CultureInfo.CurrentCulture;
                Check(L.Formats.Contains(now.Name) && now.IsReadOnly && now.NumberFormat.NumberDecimalSeparator == (now.Name == "it-IT" ? "," : ".")
                    && now.Calendar is System.Globalization.GregorianCalendar, $"format {system.Name}{(system == odd ? " with ',' decimals" : "")}, language {setting} -> {now.Name}");
            }
            (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture) = saved;
            L.Init("en");
        }
        Check(XboxScanner.PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US") == "8wekyb3d8bbwe", "Xbox publisher id");
        // Names: trademark signs, store tags and editions do not stop a match; a longer name is another game.
        Check(HybridLauncher.Core.Media.GameNames.Clean("Batman™: Arkham Knight") == "Batman: Arkham Knight", "clean drops ™");
        Check(HybridLauncher.Core.Media.GameNames.Plain("BioShock Infinite Complete Edition - Amazon Prime") == "BioShock Infinite", "plain drops store tag + edition");
        Check(HybridLauncher.Core.Media.GameNames.Plain("Halo Infinite (PC)") == "Halo Infinite", "plain drops (PC)");
        Check(HybridLauncher.Core.Media.GameNames.Plain("SteamWorld Dig") is null, "plain keeps SteamWorld");
        Check(HybridLauncher.Core.Media.GameNames.SameGame("Control", "CONTROL Ultimate Edition"), "Control = CONTROL Ultimate Edition");
        Check(!HybridLauncher.Core.Media.GameNames.SameGame("Control", "CONTROL Resonant"), "Control != CONTROL Resonant");
        Check(HybridLauncher.Core.Media.GameNames.SameGame("Batman™: Arkham Knight", "Batman: Arkham Knight"), "Batman™ = Batman");
        Check(!HybridLauncher.Core.Media.GameNames.SameGame("Mass Effect", "Mass Effect Legendary Edition"), "Mass Effect != Legendary Edition");
        break;
    }
    case "pcgwpage":
    {
        // dotnet run -- pcgwpage [appid] [name]: the whole PCGamingWiki page as Hybrid Launcher reads it, section by section.
        var appId = args.Length > 1 ? args[1] : "1091500";
        var page = await HybridLauncher.Core.Media.Pcgw.GetPageAsync(args.Length > 2 ? args[2] : "Cyberpunk 2077", appId);
        Check(page is not null, "page found");
        if (page is null) break;
        var sections = HybridLauncher.Core.Media.PcgwPage.Parse(page.Value.Wikitext, null);
        foreach (var s in sections)
        {
            Console.WriteLine($"== {s.Title}");
            foreach (var b in s.Blocks)
                Console.WriteLine($"   {b.Kind,-8} {b.Title,-28} lines {b.Lines.Count,3}  fields {b.Fields.Count,3}  rows {b.Rows.Count,3}  [{string.Join(" | ", b.Columns)}]  {string.Join("; ", b.Fields.Take(3).Select(f => $"{f.Label}={f.State}{f.Value}"))}{(b.Rows.Count > 0 ? "  first: " + string.Join(" | ", b.Rows[0]) : "")}");
        }
        Check(sections.Count >= 8, "at least 8 sections");
        break;
    }
    case "pcgwall":
    {
        // dotnet run -- pcgwall <folder>: reads every cached wiki-*.txt (first line the title, then the wikitext) and reports any page that throws.
        foreach (var file in Directory.GetFiles(args[1], "wiki-*.txt"))
        {
            var text = File.ReadAllText(file);
            int nl = text.IndexOf('\n');
            try
            {
                var sections = HybridLauncher.Core.Media.PcgwPage.Parse(text[(nl + 1)..], null);
                Check(sections.Count > 0, $"{Path.GetFileName(file)}: {sections.Count} sections");
            }
            catch (Exception ex)
            {
                Check(false, $"{Path.GetFileName(file)}: {ex.GetType().Name} {ex.Message}");
            }
        }
        break;
    }
    case "install":
    {
        var dir = Path.GetFullPath(args[1]);
        var channel = args.Length > 2 ? args[2] switch { "nightly" => OptiChannel.Nightly, "custom" => OptiChannel.Custom, _ => OptiChannel.Stable } : OptiChannel.Stable;
        bool nvidia = args.Contains("nvidia");

        ArchiveExtractor.ResetDir(dir);
        File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(dir, "FakeGame.exe"));
        File.WriteAllText(Path.Combine(dir, "dxgi.dll"), "not optiscaler - pretend this is ReShade");
        File.WriteAllText(Path.Combine(dir, "nvngx.dll"), "pre-0.9 leftover");
        var before = Snapshot(dir);

        OptiRelease release;
        if (channel == OptiChannel.Custom)
        {
            // A "Discord-style" build: only the dll, already renamed, zipped, no setup_windows.bat.
            var nightly = (await OptiReleases.GetAsync(OptiChannel.Nightly))[0];
            var src = Path.Combine(Path.GetTempPath(), "hl-custom-src");
            await ArchiveExtractor.ExtractAsync(await OptiReleases.DownloadAsync(nightly, null, CancellationToken.None), src, CancellationToken.None);
            var pack = Path.Combine(Path.GetTempPath(), "hl-custom-pack");
            ArchiveExtractor.ResetDir(pack);
            Directory.CreateDirectory(Path.Combine(pack, "inner"));
            File.Copy(Path.Combine(CustomBuilds.FindPayloadRoot(src)!, "OptiScaler.dll"), Path.Combine(pack, "inner", "dxgi.dll"));
            var zip = Path.Combine(Path.GetTempPath(), "Discord test build.zip");
            File.Delete(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(pack, zip);
            var build = await CustomBuilds.ImportAsync(new AppSettings(), zip);
            Console.WriteLine($"Imported {build.Name}: {build.PrettyVersion}, missing: {string.Join(", ", build.Missing)}");
            Check(!build.IsComplete && build.PayloadRoot.EndsWith("inner"), "custom build found in its sub-folder, no bat");
            Check((await CustomBuilds.ScanAsync(new AppSettings())).Any(b => b.Name == build.Name), "custom build listed by scan");
            release = build.ToRelease();
        }
        else
        {
            var releases = await OptiReleases.GetAsync(channel);
            Console.WriteLine($"{releases.Count} {channel} releases, latest {releases[0].Tag}");
            release = releases[0];
        }
        var options = new OptiSetupOptions { DllName = "dxgi.dll", Nvidia = nvidia, Spoofing = false, OptiPatcher = true };
        Console.WriteLine($"Conflict check: {OptiDetector.DescribeConflict(dir, "dxgi.dll")}");

        var progress = new Progress<InstallProgress>(p => { if (p.Line is not null) Console.WriteLine($"    | {p.Line}"); else Console.WriteLine($"  {p.Fraction:P0} {p.Stage}"); });
        File.WriteAllText(Path.Combine(AppInfo.AsiPluginsDir, "TestPlugin.asi"), "fake asi");
        var m = await OptiInstaller.InstallAsync(new InstallRequest
        {
            GameId = "test:fake",
            TargetDir = dir,
            Release = release,
            Options = options,
            IniOverrides = new Dictionary<string, string> { ["Upscalers/Dx12Upscaler"] = "fsr31" },
            AsiPlugins = ["TestPlugin.asi"],
        }, progress, CancellationToken.None);

        await Task.Delay(300); // let Progress callbacks drain
        Console.WriteLine($"Manifest: {m.Files.Count} files, {m.Backups.Count} backups, created dirs [{string.Join(", ", m.CreatedDirs)}], plugins dir {m.PluginsDir}");
        Check(OptiDetector.IsOptiScaler(Path.Combine(dir, "dxgi.dll")), "dxgi.dll is OptiScaler");
        Check(!File.Exists(Path.Combine(dir, "OptiScaler.dll")), "OptiScaler.dll renamed");
        Check(!File.Exists(Path.Combine(dir, "setup_windows.bat")), "setup bat removed itself");
        Check(File.Exists(Path.Combine(dir, "Remove_OptiScaler.bat")), "uninstaller bat created");
        Check(!File.Exists(Path.Combine(dir, "nvngx.dll")), "old nvngx.dll removed by bat (backed up)");
        var ini = IniDocument.Load(Path.Combine(dir, "OptiScaler.ini"));
        Check(ini.Get("Upscalers", "Dx12Upscaler") == "fsr31", "ini override applied");
        Check(nvidia || ini.Get("Spoofing", "Dxgi") == "false", "spoofing answer applied (Dxgi=false)");
        Check(ini.Get("Plugins", "LoadAsiPlugins") == "true", "ASI loading enabled");
        Check(File.Exists(Path.Combine(dir, m.PluginsDir, "TestPlugin.asi")), "universal ASI plugin copied");
        Check(m.Backups.ContainsKey("dxgi.dll") && m.Backups.ContainsKey("nvngx.dll"), "originals backed up");

        // Persist and reload the manifest like the app does.
        var json = System.Text.Json.JsonSerializer.Serialize(m, HybridJson.Default.InstallManifest);
        m = System.Text.Json.JsonSerializer.Deserialize(json, HybridJson.Default.InstallManifest)!;
        m.Files = new(m.Files, StringComparer.OrdinalIgnoreCase);

        File.WriteAllText(Path.Combine(dir, "OptiScaler.log"), "runtime log");
        var warnings = OptiInstaller.Uninstall(m);
        foreach (var w in warnings) Console.WriteLine($"  warning: {w}");
        var after = Snapshot(dir);
        var diff = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b).ToList();
        foreach (var d in diff) Console.WriteLine($"  differs: {d}");
        Check(diff.Count == 0, "folder restored byte for byte after uninstall");
        break;
    }
}

Console.WriteLine(failures == 0 ? "OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static string? BatDriver_Decide(string text, OptiSetupOptions o, bool expectUnknown = false)
{
    var m = typeof(BatDriver).GetMethod("Decide", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var a = new object?[] { text, o, false };
    var r = (string?)m.Invoke(null, a);
    if (expectUnknown && !(bool)a[2]!) return "not-unknown";
    return r;
}

static Dictionary<string, string> Snapshot(string dir) =>
    Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(dir, p), p => File.Exists(p) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) : "<dir>",
            StringComparer.OrdinalIgnoreCase);
