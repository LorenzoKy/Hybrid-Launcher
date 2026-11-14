// End-to-end checks for HybridLauncher.Core that need a real Windows box and network:
//   dotnet run -- scan              list detected games and their exe folders
//   dotnet run -- ini <file>        parse an OptiScaler.ini and print the option kinds
//   dotnet run -- decide            prompt classification table for the setup bat
//   dotnet run -- spotlight         the Dashboard Spotlight rule on made-up posts (featured, major, recent update)
//   dotnet run -- wiki              OptiScaler wiki page matching for a few known game names
//   dotnet run -- info [appid] [name]  game overview data: Steam store details, PCGamingWiki, HowLongToBeat, OptiScaler wiki
//   dotnet run -- hw                hardware overview (cpu, gpu, memory, displays, system, drives)
//   dotnet run -- api               graphics API per detected game (PCGamingWiki, exe imports) and Xbox package logos
//   dotnet run -- ach [appid]       Steam achievements read from appcache\stats (read-only)
//   dotnet run -- localach          local achievement files (INI / JSON / flat formats, temp folders), Steam's public list,
//                                   the Xbox app's cache on this PC (read-only), the unlock history and its export
//   dotnet run -- nexus             Nexus Mods page matching for a few known game names
//   dotnet run -- versions          list saved OptiScaler versions, delete one downloaded release
//   dotnet run -- renodx            RenoDX mod list parsing and game matching
//   dotnet run -- reshade           ReShade versions, and Load-through-OptiScaler on a fake folder
//   dotnet run -- dlssnramd <nvngx_dlssnr.dll>
//                                   needs HL_DATA: prepares DLSSNR-AMD's newest package from the dll, lays it over a fake
//                                   OptiScaler folder, changes a setting, takes it out (every file and key back as it was)
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
//   dotnet run -- cloud-live all [folder]  the same on OneDrive, MEGA and a folder at once (default: Google Drive's drive),
//                                   checking after each upload that every cloud holds the copy
//   dotnet run -- cloud-stall       MEGA hanging (its requests go to a local proxy that never answers) beside OneDrive, on a
//                                   test game: nothing waits for MEGA past the patience, nothing calls it in sync
//   dotnet run -- cloud-timing [n]  read-only: how long HL_DATA's clouds take for n games, alone and together; a 3 MB MEGA upload (removed)
//   dotnet run -- cloud-play <id>…  what Play does for real games of HL_DATA (back their saves up first), timed; uploads go on in the background
//   dotnet run -- art [appid] [name]  banner, logo and cover a game page uses (store asset list, screenshot fallback)
//   dotnet run -- signin-page [hold] MEGA's browser sign-in page served on 127.0.0.1: form, token guard, empty-form check, cancel
//   dotnet run -- friends           signs in with HL_DATA's Steam token and lists the account's friends (read-only)
//   dotnet run -- extract           the built-in extractor on a zip it makes (and HL_EXTRACT_SAMPLES' archives), no tar.exe
//   dotnet run -- settings          settings from a newer version: unknown values left out, the rest kept
//   dotnet run -- v21               2.1: old themes mapped to the new four, verify plans, a game's own pictures, profiles
//   dotnet run -- rc4               2.1-rc.4: Epic stats parsing, GOG containers in an update, SGDB pictures, the list's own
//                                   filters, "Hybrid Config and Stats" merged between two PCs through a folder cloud
//   dotnet run -- rc10              2.1-rc.10 (audit of rc.8/rc.9): Steam shortcuts, settings read case-insensitive, config sync
//                                   (failed send, removals, the window's size, time zones, the data folder's paths)
//   dotnet run -- updates           the app's own update: release JSON parsing, the check against a local feed, download + unpack
//   dotnet run -- customdlls        custom dlls on a fake install: copied in, replaced, a foreign file kept, turned off, uninstalled
//   dotnet run -- forks           OptiScaler forks: addresses, Update available, notifications once per release, the archive picked
//   dotnet run -- techversions      My PC's technology versions read from AMD's, Intel's and NVIDIA's SDK releases on GitHub
//   dotnet run -- names             OptiScaler dev build names, a post's BBCode read as blocks
//   dotnet run -- ultra             Ultra+ on a fake Unreal folder: lists, install, update keeping settings, add-on variants,
//                                   custom variables back after a reinstall, taking over an install made elsewhere, uninstall
//   dotnet run -- stores           Ubisoft Connect, EA app and Rockstar Games Launcher games from fake records and folders
//   dotnet run -- dxvk             DXVK, DXVK-gplasync and VKD3D-Proton: releases, unpack, install into fake games (64 and 32-bit),
//                                  dxvk.conf, a ReShade dll refused, a changed file found, uninstall back byte for byte
//   dotnet run -- modcheck         the mod check on fake folders: add-ons without ReShade or with the normal build, ReShade loaded
//                                  twice, DXVK without its dxgi.dll, mods a game update removed
//   dotnet run -- auto             the Auto tab's planner on made-up games (names, ReShade through OptiScaler, HDR mods one at a
//                                  time, 32-bit, DX9, Vulkan, DXVK), then ReShade put in directly (no installer) into a fake folder
//   dotnet run -- dllswap          OptiScaler's dll renamed in place (from a saved OptiScaler archive): record updated, the game's own
//                                  file under the old name put back, a taken name refused
// Set HL_DATA to keep test data out of %LOCALAPPDATA%\HybridLauncher.
using System.Security.Cryptography;
using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Ini;
using HybridLauncher.Core.Opti;
using HybridLauncher.Core.Mods;
using HybridLauncher.Core.Achievements;
using HybridLauncher.Core.Media;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) failures++;
}

var cmd = args.Length > 0 ? args[0] : "scan";
// Never the user's own data folder (2.0.0-rc.13): with no HL_DATA every test runs on a fresh scratch folder. Some tests delete
// and rewrite settings files; run by hand without HL_DATA, "settings" once deleted the real settings.json. A test that needs
// real sign-ins takes a copy through HL_DATA, as the comments above say.
if (!AppInfo.SeparateData)
{
    var scratch = Path.Combine(Path.GetTempPath(), "hl-smoke-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(scratch);
    Environment.SetEnvironmentVariable("HL_DATA", scratch);
    Console.WriteLine($"HL_DATA not set: using {scratch}");
}
switch (cmd)
{
    case "steamcommunity":
    {
        // Steam achievements of a game Steam has not cached on this PC (2.0.0-rc.14): the account's community page.
        var sample = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <playerstats><privacyState>public</privacyState><achievements>
            <achievement closed="1"><iconClosed><![CDATA[https://x/c.jpg]]></iconClosed><iconOpen><![CDATA[https://x/o.jpg]]></iconOpen>
            <name><![CDATA[The World]]></name><apiname><![CDATA[ACH_ONE]]></apiname><description><![CDATA[Do it.]]></description>
            <unlockTimestamp>1700000000</unlockTimestamp></achievement>
            <achievement closed="0"><iconClosed>https://x/c2.jpg</iconClosed><iconOpen>https://x/o2.jpg</iconOpen>
            <name>Two</name><apiname>ACH_TWO</apiname><description></description></achievement>
            </achievements></playerstats>
            """;
        var set = HybridLauncher.Core.Steam.SteamAchievements.ParseCommunity("1", sample);
        Check(set is { Total: 2, UnlockedCount: 1 } && set.Items[0].UnlockedAt?.Year == 2023 && set.Items[0].IconUrl == "https://x/c.jpg" && !set.Items[1].Unlocked,
            "community page: names, unlocks, dates and icons");
        Check(HybridLauncher.Core.Steam.SteamAchievements.ParseCommunity("1", "<response><error><![CDATA[This profile is private.]]></error></response>") is null, "a private profile gives none");
        Check(HybridLauncher.Core.Steam.SteamAchievements.ParseCommunity("1", "<!DOCTYPE html><html><body>Sign in</body>") is null, "an HTML page gives none");
        if (args.Length > 1)
        {
            var live = await HybridLauncher.Core.Steam.SteamAchievements.LoadAsync(args[1]);
            Console.WriteLine($"  {args[1]}: schema here {HybridLauncher.Core.Steam.SteamAchievements.HasSchema(args[1])}, {live?.Total ?? 0} achievements, {live?.UnlockedCount ?? 0} unlocked, " +
                $"rarest {live?.Items.Where(a => a.GlobalPercent is not null).MinBy(a => a.GlobalPercent)?.Name ?? "-"}");
            Check(live is { Total: > 0 }, $"app {args[1]} has its achievements");
        }
        break;
    }
    case "names":
    {
        // OptiScaler's dev builds named short (1.4.0-rc.7), and a post's BBCode read as blocks for the in-app reader.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        int year = DateTime.Now.Year;
        Check(CustomBuilds.Readable($"10.0.0-dev (07aa9da1) ({year}1110_091208)") == "v10 dev-nightly · 10 Nov", "dev build: v10 dev-nightly · 10 Nov (no commit)");
        Check(CustomBuilds.Readable("11.0.0-dev (fa44a4a8) (20251022_091208)") == "v11 dev-nightly · 22 Oct 2025", "dev build of another year keeps its year");
        Check(CustomBuilds.Readable("v0.9.4") == "0.9.4" && !CustomBuilds.IsDev("0.9.4") && CustomBuilds.IsDev("10.0.0-dev (x)"), "release numbers stay as they are");
        var blocks = HybridLauncher.Core.Media.GameEvents.Blocks("[h2]Fixes[/h2][list][*]Fixed a crash.[*]Fixed [b]sound[/b].[/list][p]See [url=https://x]notes[/url].[/p][img]{STEAM_CLAN_IMAGE}/1/a.png[/img][p][dynamiclink href=\"https://x\"][/p]");
        Check(blocks.Count == 5 && blocks[0] == new HybridLauncher.Core.Media.NoteBlock(HybridLauncher.Core.Media.NoteBlockKind.Heading, "Fixes")
            && blocks[1].Kind == HybridLauncher.Core.Media.NoteBlockKind.Item && blocks[2].Text == "Fixed sound ."
            && blocks[3].Text == "See notes ." && blocks[4].Text == "https://clan.akamai.steamstatic.com/images/1/a.png", "post blocks: heading, items, paragraph, picture, no dynamiclink");
        // Steam's newer picture tag (2.0.0-rc.10): [img src="..."][/img] left '[img src="' at the start of the news text.
        const string newer = "[p][img src=\"{STEAM_CLAN_IMAGE}/45/b.png\"][/img][/p][p]Fixed an exploit.[/p]";
        var ex = HybridLauncher.Core.Media.GameNews.Excerpt(newer);
        Check(ex == "Fixed an exploit.", "news text without the [img src] tag (got: " + ex + ")");
        var nb = HybridLauncher.Core.Media.GameEvents.Blocks(newer);
        Check(nb.Count == 2 && nb[0].Text == "https://clan.akamai.steamstatic.com/images/45/b.png" && nb[1].Text == "Fixed an exploit.", "post blocks read [img src] as a picture");
        Check(HybridLauncher.Core.Media.GameNews.Picture("[img src=\"https://x.test/c.jpg\"][/img]") == "https://x.test/c.jpg", "first picture from [img src]");
        Console.WriteLine("    " + CustomBuilds.Readable($"10.0.0-dev (07aa9da1) ({year}1110_091208)") + " / " + System.Globalization.CultureInfo.CurrentCulture.Name);
        break;
    }
    case "localach":
    {

        var ini = LocalAchievements.ParseIni("[SteamAchievements]\nCount=2\n[ACH_ONE]\nAchieved=1\nUnlockTime=1700000000\nCurProgress=0\nMaxProgress=0\n[ACH_TWO]\nAchieved=0\nCurProgress=3\nMaxProgress=10\n");
        Check(ini.Count == 2 && ini["ACH_ONE"].Unlocked && ini["ACH_ONE"].At?.Year == 2023 && !ini["ACH_TWO"].Unlocked && ini["ACH_TWO"].Now == 3 && ini["ACH_TWO"].Max == 10, "INI with a section per achievement (unlock, time, progress)");
        var rld = LocalAchievements.ParseIni("[Steam]\nAppID=1\n[ACH_A]\nState = 0101\nTime = 00F15365\n[ACH_B]\nState = 0000\n");
        Check(rld["ACH_A"].Unlocked && !rld["ACH_B"].Unlocked && rld["ACH_A"].At?.Year == 2023, "INI with State and a hex time");
        var flat = LocalAchievements.ParseIni("[Achievements]\nACH_X=1\nACH_Y=0\n[AchievementsUnlockTimes]\nACH_X=1650000000\n");
        Check(flat["ACH_X"].Unlocked && flat["ACH_X"].At?.Year == 2022 && !flat["ACH_Y"].Unlocked, "flat INI section with its times");
        var json = LocalAchievements.ParseJson("{\"ACH_J\":{\"earned\":true,\"earned_time\":1600000000},\"ACH_K\":{\"earned\":false,\"progress\":2,\"max_progress\":5}}");
        Check(json["ACH_J"].Unlocked && json["ACH_J"].At?.Year == 2020 && json["ACH_K"].Max == 5, "JSON map with progress");
        var list = LocalAchievements.ParseJson("[{\"name\":\"L1\",\"achieved\":1,\"unlock_time\":1600000000},{\"name\":\"L2\",\"achieved\":0}]");
        Check(list["L1"].Unlocked && !list["L2"].Unlocked, "JSON list");
        var cfg = LocalAchievements.ParseIni("[FIRST_BLOOD]\nachieved=true\nunlocktime=1600000000\n");
        Check(cfg["FIRST_BLOOD"].Unlocked, "lower-case keys (cfg)");

        // A game folder whose layer names the app id; a user folder with a per-app sub-folder.
        var tmp = Path.Combine(Path.GetTempPath(), "hl-localach-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tmp, "game", "bin"));
        File.WriteAllText(Path.Combine(tmp, "game", "bin", "steam_emu.ini"), "[Settings]\nAppId=1091500\n");
        File.WriteAllText(Path.Combine(tmp, "game", "bin", "game.exe"), "");
        Check(LocalAchievements.DetectAppId(Path.Combine(tmp, "game"), Path.Combine(tmp, "game", "bin", "game.exe")) == "1091500", "app id from the layer's settings file");
        Directory.CreateDirectory(Path.Combine(tmp, "saves", "1091500"));
        File.WriteAllText(Path.Combine(tmp, "saves", "1091500", "achievements.ini"), "[TheFool]\nAchieved=1\nUnlockTime=1700000000\n[TheLovers]\nAchieved=0\nCurProgress=1\nMaxProgress=4\n");
        SettingsStore.Current.LocalAchievementFolders.Add(Path.Combine(tmp, "saves"));
        Check(LocalAchievements.AppIdOf(Path.Combine(tmp, "saves", "1091500", "achievements.ini")) == "1091500", "app id of a changed file in a user folder");
        var set = await LocalAchievements.LoadAsync("1091500", null, CancellationToken.None);
        Console.WriteLine($"  set: {set?.UnlockedCount}/{set?.Total}");
        var fool = set?.Items.FirstOrDefault(a => a.ApiName == "TheFool");
        Check(set is { Total: > 10 } && fool is { Unlocked: true, Name: "The Fool" } && fool.IconUrl is not null && fool.GlobalPercent is > 0, "Steam's public list named the achievements, with pictures and rarity");
        Check(LocalAchievements.ParseFile(Path.Combine(tmp, "saves", "1091500", "achievements.ini"))["TheLovers"].Max == 4 && set!.Items.Any(a => a.ApiName == "TheLovers"), "progress read from the file, the achievement in the set");

        // History and export.
        var hist = new UnlockEntry(DateTime.Now, "lib:test", "Smoke game", "Steam", "TheFool", "The Fool", "Become, a \"mercenary\".", null, 88.5, null, null);
        var before = AchievementHistory.All().Count;
        AchievementHistory.Add(hist);
        AchievementHistory.AttachScreenshot("lib:test", "TheFool", @"C:\shot.png");
        var all = AchievementHistory.All();
        Check(all.Count == before + 1 && all[0].Screenshot == @"C:\shot.png", "history written and screenshot attached");
        var csv = Path.Combine(tmp, "h.csv");
        AchievementHistory.Export(all.Take(1), csv);
        Check(File.ReadAllText(csv).Contains("\"Become, a \"\"mercenary\"\".\""), "CSV export quotes commas and quotes");
        AchievementHistory.ExportSet(set!, "Smoke", Path.Combine(tmp, "s.json"));
        Check(System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp, "s.json"))).RootElement.GetProperty("achievements").GetArrayLength() == set!.Total, "set export as JSON");

        // The Xbox app's cache (read-only), when this PC has it.
        if (XboxAchievements.CacheFile is { } xc)
        {
            using var db = WinSqlite.Open(xc);
            var rows = db.Query("SELECT key, value FROM AsyncCache WHERE scope = 'achievements' AND key LIKE 'achievements!_%' ESCAPE '!'", 2);
            int parsed = 0, withUnlocks = 0;
            foreach (var r in rows)
            {
                var title = r[0]!.Split('_').Last();
                if (XboxAchievements.Parse(title, r[1]!) is { } x) { parsed++; if (x.UnlockedCount > 0) withUnlocks++; Console.WriteLine($"  xbox {title}: {x.UnlockedCount}/{x.Total}, {x.Items.Sum(a => a.Points ?? 0)} G"); }
            }
            Check(rows.Count > 0 && parsed > 0, $"Xbox app cache read with Windows' SQLite ({parsed} lists, {withUnlocks} with unlocks)");
            foreach (var g in (await GameScanner.ScanAsync(SettingsStore.Current)).Where(g => g.Store == GameStore.Xbox))
            {
                var c = XboxAchievements.ReadCache(g);
                Console.WriteLine($"  {g.Name} ({XboxAchievements.Pfn(g)}): title {c?.TitleId ?? "-"}, {c?.Set?.UnlockedCount}/{c?.Set?.Total}");
            }
        }
        else Console.WriteLine("  (no Xbox app cache on this PC)");
        try { Directory.Delete(tmp, true); } catch { }
        break;
    }
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

    case "dlssnramd":
    {
        if (!AppInfo.SeparateData) { Console.WriteLine("Set HL_DATA to a scratch folder first."); break; }
        var dll = args.Length > 1 ? args[1] : DlssNr.StoredPath;
        var (tier, gpu) = Dlss5.Detect();
        Console.WriteLine($"GPU: {gpu?.Name} ({tier}), driver {gpu?.DriverVersion}, ok={Dlss5.DriverOk(tier, gpu)}; model {Dlss5.Describe(dll)}");
        var release = await DlssNrAmd.LatestAsync(CancellationToken.None);
        Check(release is not null, $"newest release: {release?.Tag} {release?.AssetName}");
        var stage = await DlssNrAmd.PrepareAsync(release!, dll, new Progress<(string Step, double? Share)>(p => { if (p.Share is null) Console.WriteLine(p.Step); }), CancellationToken.None);
        Check(stage.Networks.Count > 0, $"prepared {stage.Tag}: {string.Join(", ", stage.Networks)}, {DlssNrAmd.StageBytes() / 1048576} MB");
        var game = Path.Combine(Path.GetTempPath(), "hl-dlssnramd-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(game, "OptiScaler", "plugins"));
        const string optiIni = "[Menu]\nFGShortcutKey=auto\n[Plugins]\nPath=auto\nLoadAsiPlugins=false\n";
        File.WriteAllText(Path.Combine(game, "OptiScaler.ini"), optiIni);
        var m = new InstallManifest { TargetDir = game, DllName = "dxgi.dll", PluginsDir = @"OptiScaler\plugins", Channel = OptiChannel.Nightly };
        Check(DlssNrAmd.CannotGoInto(m) is null, "can go into the fake install");
        DlssNrAmd.Apply(m, stage.Networks[0], renoDx: true);
        Check(File.Exists(Path.Combine(game, @"OptiScaler\plugins\dlssnr-amd.asi")), "plugin in OptiScaler\\plugins");
        Check(stage.Networks.All(n => File.Exists(Path.Combine(game, "dlssnr-amd", n + ".bin"))), "every network's weights in dlssnr-amd");
        Check(DlssNrAmd.Get(m, "Network", "Model") == stage.Networks[0], "dlssnr-amd.ini starts with the chosen network");
        Check(DlssNrAmd.IniGet(Path.Combine(game, "OptiScaler.ini"), "Plugins", "LoadAsiPlugins") == "true", "LoadAsiPlugins=true");
        Check(DlssNrAmd.IniGet(Path.Combine(game, "OptiScaler.ini"), "Menu", "FGShortcutKey") == "-1", "FGShortcutKey=-1");
        Check(DlssNrAmd.Set(m, "Network", "Memory Guard", "1") && DlssNrAmd.Get(m, "Network", "Memory Guard") == "1", "a setting changed");
        DlssNrAmd.Remove(m);
        Check(!Directory.Exists(Path.Combine(game, "dlssnr-amd")) && !File.Exists(Path.Combine(game, "dlssnr-amd.ini")), "taken out");
        Check(File.ReadAllText(Path.Combine(game, "OptiScaler.ini")) == optiIni, "OptiScaler.ini as it was");
        DlssNrAmd.Apply(m, stage.Networks[0], renoDx: true);
        Check(DlssNrAmd.Get(m, "Network", "Memory Guard") == "1", "its dlssnr-amd.ini comes back on the next install");
        DlssNrAmd.Remove(m);
        // A put-in that fails half way (the plugin's name taken by a folder) leaves nothing behind.
        var blocker = Path.Combine(game, @"OptiScaler\plugins\dlssnr-amd.asi");
        Directory.CreateDirectory(blocker);
        bool threw = false;
        try { DlssNrAmd.Apply(m, stage.Networks[0], renoDx: true); } catch { threw = true; }
        Check(threw && m.DlssNrAmd is null && !Directory.Exists(Path.Combine(game, "dlssnr-amd")) && File.ReadAllText(Path.Combine(game, "OptiScaler.ini")) == optiIni,
            "a failed put-in is taken out again");
        Directory.Delete(blocker);
        DlssNrAmd.ForgetSavedIni(game);
        Directory.Delete(game, recursive: true);
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
    case "forks":
    {
        // Addresses typed by hand become owner/name; anything else is refused.
        Check(OptiForks.ParseRepo("https://github.com/LorenzoKy/Optiscaler-Venom") == "LorenzoKy/Optiscaler-Venom", "full address");
        Check(OptiForks.ParseRepo("github.com/janblade/OptiScaler-F5-DLSSNR-Multipass/releases") == "janblade/OptiScaler-F5-DLSSNR-Multipass", "address with a path");
        Check(OptiForks.ParseRepo("Dagherbou/OptiScaler_DLSSNR.git") == "Dagherbou/OptiScaler_DLSSNR", "owner/name.git");
        Check(OptiForks.ParseRepo("not a repo") is null && OptiForks.ParseRepo("https://github.com/") is null, "not an address");
        // Update available: an older tag, the newest one, a tag no longer listed.
        var now = DateTime.UtcNow;
        List<OptiRelease> list =
        [
            new(OptiChannel.Fork, "v3", "v3", now, "a.7z", "u", 1, null) { Repo = "o/n" },
            new(OptiChannel.Fork, "v2", "v2", now.AddDays(-3), "a.7z", "u", 1, null) { Repo = "o/n" },
        ];
        var m = new InstallManifest { Channel = OptiChannel.Fork, Source = "o/n", Tag = "v2", InstalledAt = now.AddDays(-2) };
        Check(OptiUpdates.NewerThan(m, list)?.Tag == "v3", "older install: v3 offered");
        m.Tag = "v3";
        Check(OptiUpdates.NewerThan(m, list) is null, "newest install: nothing offered");
        m.Tag = "v9-private";
        m.InstalledAt = now.AddDays(1);
        Check(OptiUpdates.NewerThan(m, list) is null, "unlisted build installed after the newest: left alone");
        m.InstalledAt = now.AddDays(-10);
        Check(OptiUpdates.NewerThan(m, list)?.Tag == "v3", "unlisted build older than the newest: v3 offered");
        Check(OptiUpdates.Key(OptiChannel.Fork, "LorenzoKy/Optiscaler-Venom") == "fork:lorenzoky/optiscaler-venom" && OptiUpdates.Key(OptiChannel.Custom, null) == "", "source keys");
        // Notifications: a source turned on remembers its newest release, only a later one notifies.
        var s = new AppSettings();
        s.OptiFollow.Add("fork:lorenzoky/optiscaler-venom");
        s.OptiSeen["fork:lorenzoky/optiscaler-venom"] = "an-old-tag";
        var found = await OptiUpdates.CheckAsync(s, CancellationToken.None);
        Check(found.Count == 1 && s.OptiSeen["fork:lorenzoky/optiscaler-venom"] == found[0].Release.Tag, "a new release notifies once: " + string.Join(", ", found.Select(f => f.Release.Tag)));
        Check((await OptiUpdates.CheckAsync(s, CancellationToken.None)).Count == 0, "then stays quiet");
        // Each known fork lists releases, and the archive picked is the build (not an optional extra beside it).
        foreach (var f in OptiForks.Known.Take(3))
        {
            var releases = await OptiReleases.GetForkAsync(f.Repo);
            Check(releases.Count > 0 && releases.All(r => !r.AssetName.Contains("Optional", StringComparison.OrdinalIgnoreCase)),
                $"{f.Repo}: {releases.Count} releases, newest {releases.FirstOrDefault()?.AssetName}");
        }
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
    case "extract":
    {
        // The built-in extractor reads zips (entry by entry) and 7z (front to back) without falling back to tar.exe. Real archives
        // from HL_EXTRACT_SAMPLES (a folder, searched below) are checked too, file for file against .NET's own zip reader.
        var dir = Path.Combine(Path.GetTempPath(), "hl-extract-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var src = Path.Combine(dir, "src");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "OptiScaler.ini"), "[x]");
            File.WriteAllBytes(Path.Combine(src, "sub", "a.dll"), new byte[70000]);
            var zip = Path.Combine(dir, "t.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(src, zip);
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);
            HybridLauncher.Core.Opti.ArchiveExtractor.ExtractManaged(zip, outDir, CancellationToken.None);
            Check(File.Exists(Path.Combine(outDir, "OptiScaler.ini")) && new FileInfo(Path.Combine(outDir, "sub", "a.dll")).Length == 70000, "a zip is read by the built-in extractor");
            if (Environment.GetEnvironmentVariable("HL_EXTRACT_SAMPLES") is { Length: > 0 } samples && Directory.Exists(samples))
                foreach (var archive in Directory.EnumerateFiles(samples, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)).Take(8))
                {
                    var o = Path.Combine(dir, Guid.NewGuid().ToString("N")[..6]);
                    Directory.CreateDirectory(o);
                    HybridLauncher.Core.Opti.ArchiveExtractor.ExtractManaged(archive, o, CancellationToken.None);
                    int got = Directory.GetFiles(o, "*", SearchOption.AllDirectories).Length;
                    int want = archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        ? System.IO.Compression.ZipFile.OpenRead(archive).Entries.Count(e => !e.FullName.EndsWith('/'))
                        : got;
                    Check(got > 0 && got == want, $"{Path.GetFileName(archive)}: {got} files");
                }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        break;
    }
    case "settings":
    {
        // Settings written by a newer version: a value this one does not know is left out, the rest is kept.
        var json = """
            { "Theme": "Dark", "Games": {
                "steam:1": { "LaunchArgs": "-dx12", "Install": { "Channel": "SomethingNewer" } },
                "x.y['z']": { "LaunchArgs": "-safe", "Install": { "Channel": "AlsoNewer" } } },
              "TourSeen": "not a bool" }
            """;
        System.Text.Json.JsonException? first = null;
        try { System.Text.Json.JsonSerializer.Deserialize(json, HybridLauncher.Core.HybridJson.Default.AppSettings); }
        catch (System.Text.Json.JsonException ex) { first = ex; }
        Check(first is not null, "the strict read fails, as in an older version: " + first?.Path);
        var s = SettingsStore.TryReadLenient(json, first!, "test.json");
        Check(s is not null, "the lenient read succeeds");
        Check(s?.Theme == "Dark", "a setting it knows is kept (Theme)");
        Check(s?.Games.TryGetValue("steam:1", out var g1) == true && g1.LaunchArgs == "-dx12" && g1.Install is not null, "the game is kept, only its unknown channel dropped");
        Check(s?.Games.TryGetValue("x.y['z']", out var g2) == true && g2.LaunchArgs == "-safe", "a key with dots and quotes is found and kept");
        Check(SettingsStore.TryReadLenient("{ not json", first!, "test.json") is null, "a broken file is not guessed at");
        // Neither file readable: defaults, but both are kept aside first (the next save would replace them).
        var dataDir = Path.GetDirectoryName(AppInfo.SettingsFile)!;
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        File.WriteAllText(AppInfo.SettingsFile, "{ broken");
        File.WriteAllText(AppInfo.SettingsFile + ".bak", "{ broken too");
        var fresh = SettingsStore.Load();
        Check(fresh.Games.Count == 0, "unreadable settings start from defaults");
        Check(Directory.GetFiles(dataDir, "settings.unreadable-*.json").Length == 2, "both unreadable files are kept aside");
        // A real settings file, if one is given (HL_SETTINGS_SAMPLE), with every OptiScaler channel made unknown.
        if (Environment.GetEnvironmentVariable("HL_SETTINGS_SAMPLE") is { Length: > 0 } sample && File.Exists(sample))
        {
            var real = File.ReadAllText(sample);
            var games = System.Text.Json.Nodes.JsonNode.Parse(real)!["Games"]!.AsObject().Count;
            var changed = System.Text.RegularExpressions.Regex.Replace(real, "\"Channel\": \"\\w+\"","\"Channel\": \"FromTheFuture\"");
            File.WriteAllText(AppInfo.SettingsFile, changed);
            File.Delete(AppInfo.SettingsFile + ".bak");
            var read = SettingsStore.Load();
            Check(read.Games.Count == games, $"a real settings file with unknown channels keeps all {games} games");
        }
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        break;
    }
    case "verify":
    {
        // 2.1-rc.3: a verify is an update in place with every file checked by its checksum. A file as the store lists it is
        // kept, a damaged or missing one is downloaded again, a file the store does not list (a mod, a save) is not touched.
        var dir = Path.Combine(Path.GetTempPath(), "hl-smoke-verify");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        byte[] good = System.Text.Encoding.UTF8.GetBytes("the game's exe, as the store has it");
        byte[] data = System.Text.Encoding.UTF8.GetBytes("a data file of the game");
        string Sha(byte[] b) => Convert.ToHexString(SHA1.HashData(b));
        File.WriteAllBytes(Path.Combine(dir, "bin", "game.exe"), good);
        var damaged = (byte[])data.Clone(); damaged[3] ^= 0x55;
        File.WriteAllBytes(Path.Combine(dir, "data.pak"), damaged);
        File.WriteAllText(Path.Combine(dir, "mod.dll"), "a mod");
        int fetched = 0;
        HybridLauncher.Core.Accounts.PlanFile File1(string path, byte[] bytes) => new()
        {
            Path = path, Size = bytes.Length, Sha1 = Sha(bytes),
            Chunks = [new HybridLauncher.Core.Accounts.PlanChunk(0, bytes.Length, _ => { Interlocked.Increment(ref fetched); return Task.FromResult(bytes); })],
        };
        int lastChecked = -1, lastTotal = -1;
        var plan = new HybridLauncher.Core.Accounts.DownloadPlan
        {
            InstallDir = dir, Update = true, Version = "1",
            Files = [new() { Path = "bin", Directory = true }, File1(@"bin\game.exe", good), File1("data.pak", data), File1("missing.txt", data)],
            Checked = (n, t) => { lastChecked = n; lastTotal = t; },
        };
        HybridLauncher.Core.Accounts.DirectDownloader.RunAsync(plan, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        Check(plan.Fetched == 2, $"two files of three downloaded again (got {plan.Fetched})");
        Check(fetched == 2, "the file as the store has it was not downloaded");
        Check(File.ReadAllBytes(Path.Combine(dir, "data.pak")).SequenceEqual(data), "the damaged file is repaired");
        Check(File.ReadAllBytes(Path.Combine(dir, "missing.txt")).SequenceEqual(data), "the missing file is back");
        Check(File.ReadAllText(Path.Combine(dir, "mod.dll")) == "a mod", "a file the store does not list is left alone");
        Check(lastChecked == 3 && lastTotal == 3, "the check reports every file checked");
        Check(!HybridLauncher.Core.Accounts.DirectDownloader.HasState(dir), "the download's own list is gone once done");
        Directory.Delete(dir, true);
        break;
    }
    case "rc4":
    {
        // 2.1-rc.4: Epic's play time and achievements read from its answers, GOG Galaxy's keys, GOG's small-file containers in
        // an update (fetched only for a damaged file), empty files kept, SteamGridDB pictures checked, the list's own filters,
        // and "Hybrid Config and Stats" between two PCs (a folder as the cloud): numbers joined, settings merged key by key.
        var dataDir = Path.GetDirectoryName(AppInfo.SettingsFile)!;
        if (!dataDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) { Check(false, "rc4 needs a scratch HL_DATA under %TEMP%"); break; }

        // Epic: play time per app (seconds), achievements from the definitions and the account's unlocks.
        var play = HybridLauncher.Core.Accounts.EpicStats.ParsePlaytime("[{\"accountId\":\"a\",\"artifactId\":\"Sugar\",\"totalTime\":7260},{\"accountId\":\"a\",\"artifactId\":\"x\",\"totalTime\":30}]");
        Check(play["Sugar"] == 7260 && play["x"] == 30, "Epic play time read per app");
        const string defs = "{\"data\":{\"Achievement\":{\"productAchievementsRecordBySandbox\":{\"totalAchievements\":2,\"achievements\":[" +
            "{\"achievement\":{\"name\":\"ACH_1\",\"hidden\":false,\"unlockedDisplayName\":\"First\",\"unlockedDescription\":\"Do it\",\"lockedDescription\":\"?\",\"unlockedIconLink\":\"https://x/u.png\",\"lockedIconLink\":\"https://x/l.png\",\"rarity\":{\"percent\":12.5}}}," +
            "{\"achievement\":{\"name\":\"ACH_2\",\"hidden\":true,\"unlockedDisplayName\":\"Secret\",\"unlockedDescription\":\"The secret\",\"lockedDescription\":\"Hidden\",\"rarity\":{\"percent\":1}}}]}}}}";
        const string player = "{\"data\":{\"PlayerAchievement\":{\"playerAchievementGameRecordsBySandbox\":{\"records\":[{\"totalUnlocked\":1,\"playerAchievements\":[{\"playerAchievement\":{\"achievementName\":\"ACH_1\",\"unlocked\":true,\"unlockDate\":\"2023-04-12T21:46:21.166Z\"}}]}]}}}}";
        var set = HybridLauncher.Core.Accounts.EpicStats.Parse("ns1", defs, player);
        Check(set is { Total: 2, UnlockedCount: 1, AppId: "epic:ns1" }, "Epic achievements: two, one unlocked");
        Check(set!.Items[0] is { Name: "First", GlobalPercent: 12.5, UnlockedAt: not null }, "an unlock has its name, rarity and date");
        Check(set.Items[1].Description == "Hidden", "a hidden one keeps its secret until unlocked");
        // 2.1-rc.12: the unlocks by product (Playnite's SuccessStory's query) read the same way.
        const string byProduct = "{\"data\":{\"PlayerProfile\":{\"playerProfile\":{\"productAchievements\":{\"data\":{\"totalUnlocked\":2,\"playerAchievements\":[" +
            "{\"playerAchievement\":{\"achievementName\":\"ACH_1\",\"unlocked\":true,\"unlockDate\":\"2023-04-12T21:46:21.166Z\"}}," +
            "{\"playerAchievement\":{\"achievementName\":\"ACH_2\",\"unlocked\":true,\"unlockDate\":\"2024-01-02T10:00:00Z\"}}]}}}}}}";
        Check(HybridLauncher.Core.Accounts.EpicStats.Parse("ns1", defs, byProduct) is { Total: 2, UnlockedCount: 2 }, "Epic unlocks by product: both unlocked");
        Check(HybridLauncher.Core.Accounts.EpicStats.Parse("ns2", "{\"data\":{\"Achievement\":{\"productAchievementsRecordBySandbox\":{\"totalAchievements\":null,\"achievements\":null}}}}", null) is null,
            "a game without achievements on Epic has none");
        var epicGame = new Game { Id = "epic:Sugar", Name = "x", Store = GameStore.Epic, InstallDir = @"C:\x", EpicLaunchUri = "com.epicgames.launcher://apps/ns9%3Acat%3ASugar?action=launch" };
        Check(HybridLauncher.Core.Accounts.EpicStats.NamespaceOf(epicGame) == "ns9", "an installed Epic game's namespace comes from its launch link");
        Check(HybridLauncher.Core.Accounts.GalaxyPlaytime.KeyOf(epicGame) == "epic_Sugar"
            && HybridLauncher.Core.Accounts.GalaxyPlaytime.KeyOf(new Game { Id = "gog:12", Name = "x", Store = GameStore.Gog, InstallDir = "" }) == "gog_12", "GOG Galaxy's release keys");

        // GOG containers in an update: two small files packed in one container, both fine on disk: nothing is downloaded.
        var dir = Path.Combine(Path.GetTempPath(), "hl-smoke-rc4-gog");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        byte[] a = System.Text.Encoding.UTF8.GetBytes("small file one"), b = System.Text.Encoding.UTF8.GetBytes("second small file");
        byte[] container = [.. a, .. b];
        string Md5(byte[] x) => Convert.ToHexString(MD5.HashData(x)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(dir, "a.txt"), a);
        File.WriteAllBytes(Path.Combine(dir, "b.txt"), b);
        File.WriteAllBytes(Path.Combine(dir, "empty.cfg"), []);
        int fetches = 0;
        HybridLauncher.Core.Accounts.DownloadPlan GogPlan() => new()
        {
            InstallDir = dir, Update = true, Version = "2",
            Files =
            [
                new() { Path = ".hl-gog-sfc-0", Size = container.Length, Temporary = true, Md5 = Md5(container),
                    Chunks = [new(0, container.Length, _ => { Interlocked.Increment(ref fetches); return Task.FromResult(container); })] },
                new() { Path = "a.txt", Size = a.Length, Slice = (".hl-gog-sfc-0", 0), Md5 = Md5(a) },
                new() { Path = "b.txt", Size = b.Length, Slice = (".hl-gog-sfc-0", a.Length), Md5 = Md5(b) },
                new() { Path = "empty.cfg", Size = 0, Chunks = [] },
            ],
        };
        var p1 = GogPlan();
        HybridLauncher.Core.Accounts.DirectDownloader.RunAsync(p1, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        Check(fetches == 0 && p1.Fetched == 0, $"files packed in a container and an empty file, all fine: nothing downloaded (fetched {fetches}, repaired {p1.Fetched})");
        var bBefore = File.GetLastWriteTimeUtc(Path.Combine(dir, "b.txt"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "damaged!!");
        var p2 = GogPlan();
        HybridLauncher.Core.Accounts.DirectDownloader.RunAsync(p2, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        Check(fetches == 1 && p2.Fetched == 1, $"one damaged file: its container is downloaded once, one file repaired (fetched {fetches}, repaired {p2.Fetched})");
        Check(File.ReadAllBytes(Path.Combine(dir, "a.txt")).SequenceEqual(a), "the damaged file is repaired from the container");
        Check(File.GetLastWriteTimeUtc(Path.Combine(dir, "b.txt")) == bBefore, "the file that was fine is not written again");
        Check(!File.Exists(Path.Combine(dir, ".hl-gog-sfc-0")), "the container is gone afterwards");
        Directory.Delete(dir, true);

        // SteamGridDB: only real pictures are kept.
        Check(SteamGridDb.IsPicture(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0 }) && SteamGridDb.IsPicture(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })
            && SteamGridDb.IsPicture(System.Text.Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 ")), "PNG, JPEG and WebP are pictures");
        Check(!SteamGridDb.IsPicture(System.Text.Encoding.ASCII.GetBytes("<html><body>error</body></html>")), "an error page is not a picture");

        // The side list's own filters: a copy, changed on its own.
        var lib = new LibraryFilter { Sort = "playtime", Launchers = { "store:Steam" } };
        var list = lib.CopyFilters();
        list.Launchers.Add("store:Epic");
        list.Sort = "name";
        Check(lib.Launchers.Count == 1 && lib.Sort == "playtime", "the list's filters are its own copy");

        // Settings merged three ways.
        var baseCopy = System.Text.Json.Nodes.JsonNode.Parse("{\"Theme\":\"Dark\",\"Language\":\"en\",\"WindowWidth\":1000,\"Games\":{\"g\":{\"PlayMinutes\":10,\"LaunchArgs\":\"-a\"}}}")!.AsObject();
        var local = System.Text.Json.Nodes.JsonNode.Parse("{\"Theme\":\"Dark\",\"Language\":\"it\",\"WindowWidth\":1000,\"Games\":{\"g\":{\"PlayMinutes\":40,\"LaunchArgs\":\"-a\",\"ExeOverride\":\"C:\\\\here.exe\"}}}")!.AsObject();
        var cloud = System.Text.Json.Nodes.JsonNode.Parse("{\"Theme\":\"Light\",\"Language\":\"en\",\"WindowWidth\":3000,\"FutureThing\":42,\"Games\":{\"g\":{\"PlayMinutes\":25,\"LaunchArgs\":\"-b\",\"ExeOverride\":\"D:\\\\there.exe\"},\"h\":{\"PlayMinutes\":5}}}")!.AsObject();
        var merged = HybridLauncher.Core.Cloud.ConfigSync.MergeSettings(local, cloud, baseCopy, localNewer: true);
        Check(merged["Theme"]!.GetValue<string>() == "Light", "a setting changed on the other PC only comes here");
        Check(merged["Language"]!.GetValue<string>() == "it", "a setting changed here only stays");
        Check(merged["WindowWidth"]!.GetValue<int>() == 1000, "this PC's window size never travels");
        Check(merged["FutureThing"]!.GetValue<int>() == 42, "a newer version's setting is kept as it is");
        Check(merged["Games"]!["g"]!["PlayMinutes"]!.GetValue<long>() == 40 && merged["Games"]!["g"]!["LaunchArgs"]!.GetValue<string>() == "-b", "a game: the bigger play time, the other PC's new arguments");
        Check(merged["Games"]!["g"]!["ExeOverride"]!.GetValue<string>() == "C:\\here.exe", "a game's exe on this PC stays");
        Check(merged["Games"]!["h"] is not null, "a game only the other PC knows is added");

        // Two PCs through a folder: this one sends, the other changes things, this one takes them.
        var cloudDir = Path.Combine(Path.GetTempPath(), "hl-smoke-rc4-cloud");
        if (Directory.Exists(cloudDir)) Directory.Delete(cloudDir, true);
        Directory.CreateDirectory(cloudDir);
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        // A clean start (the test may run again on the same folder): no play time, sessions or sync state of a run before.
        foreach (var f in new[] { PlaytimeLedger.File, PlaytimeLedger.File + ".bak", SessionLog.File_ }) if (File.Exists(f)) File.Delete(f);
        if (Directory.Exists(Path.Combine(AppInfo.CacheDir, "config-sync"))) Directory.Delete(Path.Combine(AppInfo.CacheDir, "config-sync"), true);
        SessionLog.Reload();
        var s = SettingsStore.Load();
        s.Cloud.Enabled = true;
        s.Cloud.Providers = ["folder"];
        s.Cloud.Folder = cloudDir;
        s.Theme = "Dark";
        s.Language = "en";
        var pic = Path.Combine(AppInfo.ProfilesDir, "me.png");
        File.WriteAllBytes(pic, [0x89, 0x50, 0x4E, 0x47]);
        s.Profiles.Add(new UserProfile { Name = "Alex", Picture = pic });
        SettingsStore.Save();
        SettingsStore.Flush();
        var g1 = new Game { Id = "steam:10", Name = "Ten", Store = GameStore.Steam, InstallDir = "", SteamAppId = "10" };
        PlaytimeLedger.Reload();
        PlaytimeLedger.Add(g1, 10, DateTime.Now.AddDays(-1));
        SessionLog.Add(new PlaySession("steam:10", "Ten", DateTime.Now.AddDays(-1), 10));
        Check(HybridLauncher.Core.Cloud.ConfigSync.On, "the sync is on with cloud saves on (the default)");
        HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None).GetAwaiter().GetResult();
        var bundleDir = Path.Combine(cloudDir, HybridLauncher.Core.Cloud.FolderStore.RootName, HybridLauncher.Core.Cloud.ConfigSync.Key);
        var zip = Directory.Exists(bundleDir) ? Directory.GetFiles(bundleDir, "*.zip").FirstOrDefault() : null;
        Check(zip is not null, $"the bundle is in the cloud as \"{HybridLauncher.Core.Cloud.ConfigSync.Key}\"");
        if (zip is null) break;
        var other = Path.Combine(Path.GetTempPath(), "hl-smoke-rc4-other");
        if (Directory.Exists(other)) Directory.Delete(other, true);
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, other);
        var sentSettings = File.ReadAllText(Path.Combine(other, "settings.json"));
        Check(sentSettings.Contains("%HL_DATA%") && !sentSettings.Contains(dataDir.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase), "paths in the data folder travel as %HL_DATA%");
        Check(!sentSettings.Contains("\"Cloud\""), "the cloud's own setup is not sent");
        Check(File.Exists(Path.Combine(other, "profiles", "me.png")) && File.Exists(Path.Combine(other, "playtime.json")) && File.Exists(Path.Combine(other, "sessions.jsonl")), "pictures, play time and sessions are in it");

        // The other PC: more hours on the game, a new game, a session, another theme, a setting of a newer version.
        var node = System.Text.Json.Nodes.JsonNode.Parse(sentSettings)!.AsObject();
        node["Theme"] = "Light";
        node["FutureThing"] = 42;
        File.WriteAllText(Path.Combine(other, "settings.json"), node.ToJsonString());
        var book = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(other, "playtime.json")))!.AsObject();
        book["Games"]![0]!["Minutes"] = 50;
        book["Games"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("{\"Keys\":[\"steam:20\"],\"Name\":\"Twenty\",\"Minutes\":7}"));
        File.WriteAllText(Path.Combine(other, "playtime.json"), book.ToJsonString());
        File.AppendAllText(Path.Combine(other, "sessions.jsonl"), "{\"id\":\"steam:20\",\"name\":\"Twenty\",\"start\":\"" + DateTime.UtcNow.AddHours(-2).ToString("O") + "\",\"minutes\":7}\n");
        var otherZip = Path.Combine(Path.GetTempPath(), "hl-smoke-rc4-other.zip");
        if (File.Exists(otherZip)) File.Delete(otherZip);
        System.IO.Compression.ZipFile.CreateFromDirectory(other, otherZip);
        var store = new HybridLauncher.Core.Cloud.FolderStore(cloudDir);
        var oldManifest = store.ManifestAsync(HybridLauncher.Core.Cloud.ConfigSync.Key, CancellationToken.None).GetAwaiter().GetResult();
        store.PutAsync(HybridLauncher.Core.Cloud.ConfigSync.Key, otherZip, new CloudManifest { GameId = "hybrid-config", Name = "x", Hash = "OTHER", UpdatedUtc = DateTime.UtcNow.AddMinutes(1), Machine = "OTHER-PC" },
            oldManifest, 3, CancellationToken.None).GetAwaiter().GetResult();
        // Here meanwhile: the language changed.
        s = SettingsStore.Current;
        s.Language = "it";
        SettingsStore.Save();
        SettingsStore.Flush();
        File.SetLastWriteTimeUtc(AppInfo.SettingsFile, DateTime.UtcNow.AddMinutes(-5));
        bool rewrote = HybridLauncher.Core.Cloud.ConfigSync.PullAsync(CancellationToken.None).GetAwaiter().GetResult();
        s = SettingsStore.Load();
        PlaytimeLedger.Reload();
        Check(rewrote && s.Theme == "Light", "the other PC's theme came here");
        Check(s.Language == "it", "the language changed here stayed");
        Check(s.Cloud.Folder == cloudDir && s.Cloud.Enabled, "this PC's cloud setup stayed");
        Check(s.Profiles.FirstOrDefault()?.Picture == pic, "the profile picture points into this PC's data folder");
        Check(PlaytimeLedger.Get(g1)?.Minutes == 50, "the bigger count of a game's hours is kept");
        Check(PlaytimeLedger.Get(new Game { Id = "steam:20", Name = "Twenty", Store = GameStore.Steam, InstallDir = "", SteamAppId = "20" })?.Minutes == 7, "a game played only on the other PC is added");
        Check(SessionLog.Everything().Count == 2, "the sessions of both PCs are kept");
        Check(File.ReadAllText(AppInfo.SettingsFile).Contains("FutureThing"), "a newer version's setting is kept here");
        // And back: what goes up now still has the newer version's setting.
        HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None).GetAwaiter().GetResult();
        zip = Directory.GetFiles(bundleDir, "*.zip").First();
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
        using (var r = new StreamReader(z.GetEntry("settings.json")!.Open()))
        {
            var up = r.ReadToEnd();
            Check(up.Contains("FutureThing") && up.Contains("\"it\""), "the next bundle keeps the newer setting and has this PC's change");
        }

        // 2.1 audit (P0-1): the other PC sends a change while this one has a different change of its own, and this PC sends
        // straight away (no pull first, as the 15-minute timer does). Both changes must be in what goes up, and this PC takes
        // the other's at its next start.
        var current = store.ManifestAsync(HybridLauncher.Core.Cloud.ConfigSync.Key, CancellationToken.None).GetAwaiter().GetResult();
        var other2 = Path.Combine(Path.GetTempPath(), "hl-smoke-rc4-other2");
        if (Directory.Exists(other2)) Directory.Delete(other2, true);
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, other2);
        var node2 = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(other2, "settings.json")))!.AsObject();
        node2["Theme"] = "Dark";
        File.WriteAllText(Path.Combine(other2, "settings.json"), node2.ToJsonString());
        var otherZip2 = Path.Combine(Path.GetTempPath(), "hl-smoke-rc4-other2.zip");
        if (File.Exists(otherZip2)) File.Delete(otherZip2);
        System.IO.Compression.ZipFile.CreateFromDirectory(other2, otherZip2);
        store.PutAsync(HybridLauncher.Core.Cloud.ConfigSync.Key, otherZip2, new CloudManifest { GameId = "hybrid-config", Name = "x", Hash = "OTHER2", UpdatedUtc = DateTime.UtcNow.AddMinutes(2), Machine = "OTHER-PC" },
            current, 3, CancellationToken.None).GetAwaiter().GetResult();
        s = SettingsStore.Current;
        s.AchievementPopups = false;
        SettingsStore.Save();
        HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None).GetAwaiter().GetResult();
        zip = Directory.GetFiles(bundleDir, "*.zip").First();
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
        using (var r = new StreamReader(z.GetEntry("settings.json")!.Open()))
        {
            var up = System.Text.Json.Nodes.JsonNode.Parse(r.ReadToEnd())!.AsObject();
            Check(up["Theme"]?.GetValue<string>() == "Dark", "a push without a pull first keeps the other PC's change (theme)");
            Check(up["AchievementPopups"]?.GetValue<bool>() == false, "and this PC's own change (pop-ups off)");
        }
        Check(SettingsStore.Current.Theme == "Light", "the running app keeps its settings until the next start");
        HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None).GetAwaiter().GetResult();
        zip = Directory.GetFiles(bundleDir, "*.zip").First();
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
        using (var r = new StreamReader(z.GetEntry("settings.json")!.Open()))
            Check(r.ReadToEnd().Contains("\"Dark\""), "a second push before the restart does not undo the other PC's change");
        HybridLauncher.Core.Cloud.ConfigSync.PullAsync(CancellationToken.None).GetAwaiter().GetResult();
        s = SettingsStore.Load();
        Check(s.Theme == "Dark" && !s.AchievementPopups, "at the next start this PC has both changes");
        Directory.Delete(other2, true);
        File.Delete(otherZip2);
        Directory.Delete(cloudDir, true);
        Directory.Delete(other, true);
        File.Delete(otherZip);
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        break;
    }
    case "rc10":
    {
        // 2.1-rc.10, Gemini's audit of rc.8/rc.9: Steam shortcuts updated in place and a cut file refused, settings read back
        // case-insensitive, and "Hybrid Config and Stats": a failed send keeps the agreed copy, removals stay removed, the
        // window's size does not count as an edit, times of two time zones in order, the data folder's own paths only.
        var dataDir = Path.GetDirectoryName(AppInfo.SettingsFile)!;
        if (!dataDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) { Check(false, "rc10 needs a scratch HL_DATA under %TEMP%"); break; }
        var work = Directory.CreateDirectory(Path.Combine(dataDir, "rc10")).FullName;

        // Steam shortcuts.
        var vdf = Path.Combine(work, "shortcuts.vdf");
        var lo = "--launch \"steam:10\"";
        Check(HybridLauncher.Core.Steam.SteamShortcuts.Add(vdf, "Ten", @"C:\A\HybridLauncher.exe", lo, null), "a shortcut is added");
        var first = HybridLauncher.Core.Steam.SteamShortcuts.Read(File.ReadAllBytes(vdf));
        var firstId = ((HybridLauncher.Core.Steam.SteamShortcuts.KvMap)((HybridLauncher.Core.Steam.SteamShortcuts.KvMap)first.Get("shortcuts")!).Items[0].Value).Get("appid");
        Check(!HybridLauncher.Core.Steam.SteamShortcuts.Add(vdf, "Ten", @"C:\A\HybridLauncher.exe", lo, null), "the same shortcut again changes nothing");
        Check(HybridLauncher.Core.Steam.SteamShortcuts.Add(vdf, "Ten", @"D:\Moved\HybridLauncher.exe", lo, null), "Hybrid Launcher moved: the shortcut is updated");
        var list = (HybridLauncher.Core.Steam.SteamShortcuts.KvMap)HybridLauncher.Core.Steam.SteamShortcuts.Read(File.ReadAllBytes(vdf)).Get("shortcuts")!;
        var only = (HybridLauncher.Core.Steam.SteamShortcuts.KvMap)list.Items[0].Value;
        Check(list.Items.Count == 1 && only.Get("Exe") as string == "\"D:\\Moved\\HybridLauncher.exe\"", "updated in place, no second entry");
        Check(Equals(only.Get("appid"), firstId), "the shortcut keeps its Steam id (controller layouts, pictures)");
        // Keys with a gap (an entry deleted in Steam): the new one takes a free number.
        list.Items.Insert(0, ("1", new HybridLauncher.Core.Steam.SteamShortcuts.KvMap()));
        list.Items.RemoveAt(1);
        list.Items.Add(("2", only));
        var root = new HybridLauncher.Core.Steam.SteamShortcuts.KvMap();
        root.Set("shortcuts", list);
        File.WriteAllBytes(vdf, HybridLauncher.Core.Steam.SteamShortcuts.Write(root));
        HybridLauncher.Core.Steam.SteamShortcuts.Add(vdf, "Twenty", @"D:\Moved\HybridLauncher.exe", "--launch \"steam:20\"", null);
        var keys = ((HybridLauncher.Core.Steam.SteamShortcuts.KvMap)HybridLauncher.Core.Steam.SteamShortcuts.Read(File.ReadAllBytes(vdf)).Get("shortcuts")!).Items.Select(i => i.Key).ToList();
        Check(keys.Count == 3 && keys.Distinct().Count() == 3 && keys.Contains("0"), $"a new entry takes a free key ({string.Join(",", keys)})");
        var bytes = File.ReadAllBytes(vdf);
        bool refused(byte[] b) { try { HybridLauncher.Core.Steam.SteamShortcuts.Read(b); return false; } catch (InvalidDataException) { return true; } catch { return false; } }
        Check(refused(bytes[..(bytes.Length - 1)]) && refused(bytes[..(bytes.Length / 2)]) && refused(bytes[..7]), "a cut shortcuts.vdf is refused as invalid data");
        Check(!HybridLauncher.Core.Steam.SteamShortcuts.Has(Path.Combine(work, "none.vdf"), lo), "no file: no shortcut");

        // Settings read back case-insensitive.
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        File.WriteAllText(AppInfo.SettingsFile, """
            {"ListFilter":{"Launchers":["Steam"],"Tech":["DLSS"]},"HiddenFromStats":["steam:1"],"HiddenCoverTags":["Opti"],
             "DashboardHidden":["News"],"UltraPlusPresets":{"Game":{"Mine":{"Key":"1"}}},"OptiSeen":{"Stable":"v1"},
             "Popup":{"LookColours":{"Steam":{}}},
             "Games":{"steam:1":{"CustomDlls":["A.dll"],"UltraPlus":{"Backups":{"X.pak":"y"},"Addons":{"Fog":{}}},
               "Install":{"DlssNrAmd":{"OptiIniBefore":{"Sec|Key":"1"}}}}}}
            """);
        var st = SettingsStore.Load();
        Check(st.ListFilter!.Launchers.Contains("steam") && st.ListFilter.Tech.Contains("dlss"), "the side list's filters, any case");
        Check(st.HiddenFromStats.Contains("STEAM:1") && st.HiddenCoverTags.Contains("opti") && st.DashboardHidden.Contains("news"), "hidden from stats, cover tags, dashboard parts, any case");
        Check(st.UltraPlusPresets.TryGetValue("game", out var pr) && pr.TryGetValue("MINE", out var vals) && vals.ContainsKey("key"), "Ultra+ presets, all three levels");
        Check(st.OptiSeen.ContainsKey("stable") && st.Popup.LookColours.ContainsKey("steam"), "seen releases and pop-up colours, any case");
        var gs = st.Games["STEAM:1"];
        Check(gs.CustomDlls.Contains("a.dll") && gs.UltraPlus!.Backups.ContainsKey("x.PAK") && gs.UltraPlus.Addons.ContainsKey("fog"), "a game's dlls and Ultra+ files, any case");
        Check(gs.Install?.DlssNrAmd?.OptiIniBefore.ContainsKey("sec|key") == true && gs.Install.UserOptiBackups is not null, "DLSSNR-AMD's saved ini values, any case");
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);

        // Config sync: the merge itself.
        var baseCopy = System.Text.Json.Nodes.JsonNode.Parse("""{"Theme":"Dark","Old":1,"Games":{"gone":{"LaunchArgs":"-a"},"kept":{"LaunchArgs":"-k"},"drop":{"LaunchArgs":"-d"}}}""")!.AsObject();
        var local = System.Text.Json.Nodes.JsonNode.Parse("""{"Theme":"Dark","Games":{"kept":{"LaunchArgs":"-k"},"drop":{"LaunchArgs":"-d","ExeOverride":"C:\\x.exe"}}}""")!.AsObject();
        var cloud = System.Text.Json.Nodes.JsonNode.Parse("""{"Theme":"Dark","Old":1,"Games":{"gone":{"LaunchArgs":"-a"},"kept":{"LaunchArgs":"-k"}}}""")!.AsObject();
        var merged = HybridLauncher.Core.Cloud.ConfigSync.MergeSettings(local, cloud, baseCopy, localNewer: false);
        Check(merged["Games"]!["gone"] is null, "a game's settings removed here stay removed");
        Check(!merged.ContainsKey("Old"), "a setting removed here stays removed");
        Check(merged["Games"]!["drop"] is System.Text.Json.Nodes.JsonObject d && !d.ContainsKey("LaunchArgs") && d.ContainsKey("ExeOverride"), "removed on the other PC: removed here, this PC's exe kept");
        Check(merged["Games"]!["kept"]!["LaunchArgs"]!.GetValue<string>() == "-k", "the rest stays");
        var changed = System.Text.Json.Nodes.JsonNode.Parse("""{"Theme":"Dark","Old":1,"Games":{"gone":{"LaunchArgs":"-b"},"kept":{"LaunchArgs":"-k"}}}""")!.AsObject();
        Check(HybridLauncher.Core.Cloud.ConfigSync.MergeSettings(local, changed, baseCopy, localNewer: false)["Games"]!["gone"] is not null, "removed here but changed on the other PC: the change comes back");

        // Times of two PCs in different zones, in the moment's order.
        var cloudLines = Path.Combine(work, "cloud.jsonl");
        var localLines = Path.Combine(work, "local.jsonl");
        File.WriteAllText(localLines, "{\"id\":\"a\",\"start\":\"2026-11-14T10:00:00+02:00\"}\n");
        File.WriteAllText(cloudLines, "{\"id\":\"b\",\"start\":\"2026-11-14T03:30:00-05:00\"}\n{\"id\":\"c\",\"start\":\"2026-11-14T07:30:00Z\"}\n");
        HybridLauncher.Core.Cloud.ConfigSync.MergeLines(cloudLines, localLines, r => r.GetProperty("id").GetString());
        var order = File.ReadAllLines(localLines).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("id").GetString()).ToList();
        Check(string.Join("", order) == "cab", $"sessions of two time zones in time order ({string.Join("", order)})");

        // Only the data folder's own paths travel as %HL_DATA%.
        Check(HybridLauncher.Core.Cloud.ConfigSync.InDataRoot(Path.Combine(AppInfo.DataRoot, "covers", "x.png")) && HybridLauncher.Core.Cloud.ConfigSync.InDataRoot(AppInfo.DataRoot), "a path in the data folder is its own");
        Check(!HybridLauncher.Core.Cloud.ConfigSync.InDataRoot(AppInfo.DataRoot.TrimEnd('\\') + "Backup\\x.png"), "a folder next to it whose name starts the same is not");

        // The window's size is no edit; a real change is.
        var s = SettingsStore.Load();
        SettingsStore.Save();
        SettingsStore.Flush();
        File.SetLastWriteTimeUtc(AppInfo.SettingsFile, DateTime.UtcNow.AddHours(-3));
        var editAt = HybridLauncher.Core.Cloud.ConfigSync.LocalEditAt();
        s.WindowWidth = 1234;
        s.LastGameId = "steam:10";
        SettingsStore.Save();
        SettingsStore.Flush();
        Check(HybridLauncher.Core.Cloud.ConfigSync.LocalEditAt() == editAt, "the window's size and the last game opened are no edit");
        s.Theme = s.Theme == "Light" ? "Dark" : "Light";
        SettingsStore.Save();
        SettingsStore.Flush();
        Check(HybridLauncher.Core.Cloud.ConfigSync.LocalEditAt() > editAt, "a real change counts");

        // A send that fails keeps the copy both PCs agreed on, and the next one carries both PCs' changes.
        var cloudDir = Directory.CreateDirectory(Path.Combine(work, "cloud")).FullName;
        if (Directory.Exists(Path.Combine(AppInfo.CacheDir, "config-sync"))) Directory.Delete(Path.Combine(AppInfo.CacheDir, "config-sync"), true);
        s.Cloud.Enabled = true;
        s.Cloud.Providers = ["folder"];
        s.Cloud.Folder = cloudDir;
        s.Theme = "Dark";
        s.Language = "en";
        SettingsStore.Save();
        SettingsStore.Flush();
        HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None).GetAwaiter().GetResult();
        var baseFile = Path.Combine(AppInfo.CacheDir, "config-sync", "base-settings.json");
        var agreed = File.Exists(baseFile) ? File.ReadAllText(baseFile) : null;
        Check(agreed is not null, "the first send leaves the agreed copy");
        var store = new HybridLauncher.Core.Cloud.FolderStore(cloudDir);
        var bundleDir = Path.Combine(cloudDir, HybridLauncher.Core.Cloud.FolderStore.RootName, HybridLauncher.Core.Cloud.ConfigSync.Key);
        var zip = Directory.GetFiles(bundleDir, "*.zip").First();
        var other = Path.Combine(work, "other");
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, other);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(other, "settings.json")))!.AsObject();
        node["Theme"] = "Light";
        File.WriteAllText(Path.Combine(other, "settings.json"), node.ToJsonString());
        var otherZip = Path.Combine(work, "other.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(other, otherZip);
        var was = store.ManifestAsync(HybridLauncher.Core.Cloud.ConfigSync.Key, CancellationToken.None).GetAwaiter().GetResult();
        store.PutAsync(HybridLauncher.Core.Cloud.ConfigSync.Key, otherZip, new CloudManifest { GameId = "hybrid-config", Name = "x", Hash = "OTHER", UpdatedUtc = DateTime.UtcNow.AddMinutes(1), Machine = "OTHER-PC" },
            was, 3, CancellationToken.None).GetAwaiter().GetResult();
        s = SettingsStore.Current;
        s.Language = "it";
        SettingsStore.Save();
        HybridLauncher.Core.Cloud.ConfigSync.PutFault = () => throw new IOException("the cloud went away");
        HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None).GetAwaiter().GetResult();
        HybridLauncher.Core.Cloud.ConfigSync.PutFault = null;
        Check(File.ReadAllText(baseFile) == agreed, "a send that failed leaves the agreed copy as it was");
        HybridLauncher.Core.Cloud.ConfigSync.PushAsync(CancellationToken.None).GetAwaiter().GetResult();
        zip = Directory.GetFiles(bundleDir, "*.zip").First();
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
        using (var r = new StreamReader(z.GetEntry("settings.json")!.Open()))
        {
            var up = System.Text.Json.Nodes.JsonNode.Parse(r.ReadToEnd())!.AsObject();
            Check(up["Theme"]?.GetValue<string>() == "Light" && up["Language"]?.GetValue<string>() == "it", "the next send has both PCs' changes");
        }
        Check(File.ReadAllText(baseFile) != agreed, "a send that worked moves the agreed copy");
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        Directory.Delete(work, true);
        break;
    }
    case "collections":
    {
        // 2.1: favorites and collections (Hydra style), Steam's binary shortcuts file, Epic's GraphQL answers.
        var dataDir = Path.GetDirectoryName(AppInfo.SettingsFile)!;
        if (!dataDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) { Check(false, "collections needs a scratch HL_DATA under %TEMP%"); break; }
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        var s = SettingsStore.Load();
        var fav = GameCollections.Create("Shooters");
        GameCollections.Set(fav.Id, "steam:1", true);
        GameCollections.Set(fav.Id, "STEAM:1", true);
        GameCollections.Set(GameCollections.Favorites, "steam:2", true);
        Check(fav.Games.Count == 1 && GameCollections.Contains(fav.Id, "steam:1"), "a game is in a collection once, whatever the case of its id");
        Check(GameCollections.IsFavorite("steam:2") && s.PinnedGames.Contains("steam:2"), "favorites are the old pinned games");
        Check(GameCollections.Create(" shooters ").Id == fav.Id, "a collection with the same name is not made twice");
        s.Library.Collection = fav.Id;
        s.ListFilter = new LibraryFilter { Collection = GameCollections.Favorites };
        SettingsStore.Save();
        SettingsStore.Flush();
        s = SettingsStore.Load();
        Check(s.Collections.Count == 1 && s.Collections[0].Games.SequenceEqual(["steam:1"]) && s.Library.Collection == fav.Id, "collections and the library's filter are saved");
        Check(s.Library.CopyFilters().Collection == fav.Id && s.Library.IsFiltering, "the collection is a filter and is copied with them");
        GameCollections.Rename(fav.Id, "FPS");
        GameCollections.Delete(fav.Id);
        Check(s.Collections.Count == 0 && s.Library.Collection is null && s.ListFilter?.Collection == GameCollections.Favorites, "deleting a collection shows every game again; the favorites filter stays");
        s.Library.Collection = "gone";
        GameCollections.Sanitize(s);
        Check(s.Library.Collection is null, "a filter on a collection that is gone is dropped at load");

        // Steam's shortcuts.vdf: an entry is added after the ones there, which stay byte for byte.
        var vdf = Path.Combine(dataDir, "shortcuts.vdf");
        var first = new HybridLauncher.Core.Steam.SteamShortcuts.KvMap();
        var list = new HybridLauncher.Core.Steam.SteamShortcuts.KvMap();
        var entry = new HybridLauncher.Core.Steam.SteamShortcuts.KvMap();
        entry.Set("appid", -123);
        entry.Set("AppName", "Old");
        entry.Set("Exe", "\"C:\\Old\\old.exe\"");
        entry.Set("LastPlayTime", 1700000000);
        entry.Set("tags", new HybridLauncher.Core.Steam.SteamShortcuts.KvMap());
        list.Set("0", entry);
        first.Set("shortcuts", list);
        var before = HybridLauncher.Core.Steam.SteamShortcuts.Write(first);
        File.WriteAllBytes(vdf, before);
        Check(HybridLauncher.Core.Steam.SteamShortcuts.Write(HybridLauncher.Core.Steam.SteamShortcuts.Read(before)).SequenceEqual(before), "the binary format reads and writes back the same bytes");
        var game = new Game { Id = "custom:abc", Name = "Fake Game", Store = GameStore.Custom, InstallDir = dataDir };
        var written = Shortcuts.CreateSteam(game, new GameSettings(), vdf);
        var back = HybridLauncher.Core.Steam.SteamShortcuts.Read(File.ReadAllBytes(vdf));
        var entries = ((HybridLauncher.Core.Steam.SteamShortcuts.KvMap)back.Get("shortcuts")!).Items;
        Check(written == vdf && entries.Count == 2 && entries[1].Key == "1", "the game is added as the next entry");
        var added = (HybridLauncher.Core.Steam.SteamShortcuts.KvMap)entries[1].Value;
        Check(added.Get("AppName") as string == "Fake Game" && (added.Get("LaunchOptions") as string)!.Contains("--launch \"custom:abc\""), "it starts the game through Hybrid Launcher");
        Check(((HybridLauncher.Core.Steam.SteamShortcuts.KvMap)entries[0].Value).Get("AppName") as string == "Old", "the entry already there stays");
        Check(Shortcuts.CreateSteam(game, new GameSettings(), vdf) is null, "a second time adds nothing");
        Check(HybridLauncher.Core.Steam.SteamShortcuts.AppId("\"a\"", "b") >= 0x80000000, "the shortcut's id has its top bit set as Steam's");
        File.Delete(vdf);
        File.Delete(vdf + ".hl-backup");

        // Epic: an error answer is not a definitions answer (it must not be kept for a week).
        Check(!HybridLauncher.Core.Accounts.EpicStats.IsAnswer("{\"errors\":[{\"message\":\"x\"}],\"data\":{}}"), "an answer with errors is not kept");
        Check(!HybridLauncher.Core.Accounts.EpicStats.IsAnswer("<html>"), "a page that is not JSON is not kept");
        Check(HybridLauncher.Core.Accounts.EpicStats.IsAnswer("{\"data\":{\"Achievement\":null}}"), "a real answer is kept");
        var probe = new Game { Id = "epic:probe", Name = "probe", Store = GameStore.Epic, InstallDir = "", EpicLaunchUri = "com.epicgames.launcher://APPS/ns1%3Acat%3Aprobe?action=launch" };
        Check(HybridLauncher.Core.Accounts.EpicStats.NamespaceOf(probe) == "ns1", "the namespace is found whatever the case of the link");
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        break;
    }
    case "epicstats":
    {
        // Read-only, with HL_DATA's Epic sign-in (a copy): the account's play time and one game's achievements, live.
        if (!HybridLauncher.Core.Accounts.EpicAccount.SignedIn) { Check(false, "needs an Epic sign-in in HL_DATA"); break; }
        SettingsStore.Load();
        bool fresh = HybridLauncher.Core.Accounts.EpicStats.RefreshPlaytimeAsync(CancellationToken.None, TimeSpan.Zero).GetAwaiter().GetResult();
        var minutes = HybridLauncher.Core.Accounts.EpicStats.ParsePlaytime(File.ReadAllText(Path.Combine(AppInfo.CacheDir, "epic", "playtime.json")));
        Check(fresh && minutes.Count > 0, $"Epic play time: {minutes.Count} games, {minutes.Values.Sum() / 3600} h in all");
        var ns = args.Length > 1 ? args[1] : "2c42520d342a46d7a6e0cfa77b4715de";
        var game = new Game { Id = "epic:probe", Name = "probe", Store = GameStore.Epic, InstallDir = "", EpicLaunchUri = $"com.epicgames.launcher://apps/{ns}%3Acat%3Aprobe?action=launch" };
        var set = HybridLauncher.Core.Accounts.EpicStats.ReadAsync(game, CancellationToken.None).GetAwaiter().GetResult();
        Check(set is { Total: > 0 }, $"achievements of {ns}: {set?.UnlockedCount} of {set?.Total}");
        Check(HybridLauncher.Core.Accounts.EpicStats.ReadCached(game)?.Total == set?.Total, "kept for the library to read without the network");
        break;
    }
    case "v21":
    {
        // 2.1: old themes carry on as one of the five, launcher verify plans, a game's own pictures, profiles kept in settings.
        // Runs only on a scratch data folder (HL_DATA in %TEMP%): it deletes and rewrites settings files there.
        var dataDir = Path.GetDirectoryName(AppInfo.SettingsFile)!;
        if (!dataDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) { Check(false, "v21 needs a scratch HL_DATA under %TEMP%"); break; }
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
        foreach (var (from, to) in new[] { ("Cozy", "Dark"), ("Graphite", "Dark"), ("Cyberpunk", "Hybrid"), ("Windows", "Windows"), ("Custom", "Hybrid"), ("Gray", "Gray"), ("TotalDark", "TotalDark"), ("Dark", "Dark"), ("Hybrid", "Hybrid"), ("", "Windows"), (null, "Windows") })
        {
            File.WriteAllText(AppInfo.SettingsFile, from is null ? "{ }" : "{ \"Palette\": \"" + from + "\" }");
            File.Delete(AppInfo.SettingsFile + ".bak");
            Check(SettingsStore.Load().Palette == to, $"theme {from ?? "(none)"} carries on as {to}");
        }
        var steam = new Game { Id = "steam:1091500", Name = "Cyberpunk 2077", Store = GameStore.Steam, InstallDir = @"C:\x", SteamAppId = "1091500" };
        var epic = new Game { Id = "epic:Ginger", Name = "x", Store = GameStore.Epic, InstallDir = @"C:\x",
            EpicLaunchUri = "com.epicgames.launcher://apps/ns%3Acat%3AGinger?action=launch&silent=true" };
        var gog = new Game { Id = "gog:1207658924", Name = "x", Store = GameStore.Gog, InstallDir = @"C:\x" };
        var custom = new Game { Id = "custom:x", Name = "x", Store = GameStore.Custom, InstallDir = @"C:\x" };
        Check(GameVerify.For(steam).Uri == "steam://validate/1091500", "Steam verifies from steam://validate");
        Check(GameVerify.For(epic).Uri == "com.epicgames.launcher://apps/ns%3Acat%3AGinger?action=verify", "Epic verifies from its launch link with action=verify");
        Check(GameVerify.For(gog) is { Direct: false, OpenUri: "goggalaxy://openGameView/1207658924", Steps: not null }, "GOG opens Galaxy on the game with steps");
        Check(!GameVerify.For(custom).Possible, "a game added by hand cannot be verified");
        // Epic definitions kept a week (2.1 audit): a game without achievements (null record) is a good copy, an old one without the product id is not.
        Check(HybridLauncher.Core.Accounts.EpicStats.DefsUsable("{\"data\":{\"Achievement\":{\"productAchievementsRecordBySandbox\":null}}}"), "Epic: a game without achievements is kept");
        Check(HybridLauncher.Core.Accounts.EpicStats.DefsUsable("{\"data\":{\"Achievement\":{\"productAchievementsRecordBySandbox\":{\"productId\":\"p\",\"achievements\":[]}}}}"), "Epic: definitions with the product id are kept");
        Check(!HybridLauncher.Core.Accounts.EpicStats.DefsUsable("{\"data\":{\"Achievement\":{\"productAchievementsRecordBySandbox\":{\"achievements\":[]}}}}"), "Epic: an old copy without the product id is asked again");
        Check(!HybridLauncher.Core.Accounts.EpicStats.DefsUsable("{\"errors\":[{\"message\":\"x\"}]}"), "Epic: an error is never kept");
        // GTA V on Epic (2.1): the older version is named Legacy beside Enhanced, with its own art found by that name.
        Check(HybridLauncher.Core.Accounts.EpicTitles.Name("9d2d0eb64d5c44529cece33fe2a46482", "Grand Theft Auto V") == "Grand Theft Auto V Legacy", "Epic: GTA V's older version is Legacy");
        Check(HybridLauncher.Core.Accounts.EpicTitles.Name("8769e24080ea413b8ebca3f1b8c50951", "Grand Theft Auto V Enhanced") == "Grand Theft Auto V Enhanced", "Epic: GTA V Enhanced keeps its name");
        var pair = HybridLauncher.Core.Accounts.EpicTitles.Disambiguate([
            new HybridLauncher.Core.Accounts.OwnedGame(GameStore.Epic, "epic:a", "Some Game", "c", "w", "", null), new HybridLauncher.Core.Accounts.OwnedGame(GameStore.Epic, "epic:b", "Some Game Enhanced", "c", "w", "", null)]);
        Check(pair[0] is { Name: "Some Game Legacy", Cover: null } && pair[1].Name == "Some Game Enhanced", "Epic: a title beside its Enhanced version reads Legacy");
        // GTA V Legacy (2.1): Steam's search no longer lists it, so its page comes from the known id; its achievements live in
        // Rockstar's Social Club and the page says so.
        Check(HybridLauncher.Core.Media.GameInfo.FindSteamAppIdAsync("Grand Theft Auto V Legacy", CancellationToken.None).GetAwaiter().GetResult() == "271590", "GTA V Legacy: its Steam page is known without the store search");
        Check(HybridLauncher.Core.Accounts.EpicTitles.AchievementsElsewhere("epic:9d2d0eb64d5c44529cece33fe2a46482") is { Length: > 0 }
            && HybridLauncher.Core.Accounts.EpicTitles.AchievementsElsewhere("epic:8769e24080ea413b8ebca3f1b8c50951") is null, "GTA V Legacy: the page says where its achievements are, Enhanced does not");
        // An archive that gives no file is a failed read (the caller then tries tar.exe), not an empty success.
        var emptyZip = Path.Combine(dataDir, "empty.zip");
        using (System.IO.Compression.ZipFile.Open(emptyZip, System.IO.Compression.ZipArchiveMode.Create)) { }
        var emptyOut = Path.Combine(dataDir, "empty-out");
        Directory.CreateDirectory(emptyOut);
        bool emptyThrew = false;
        try { HybridLauncher.Core.Opti.ArchiveExtractor.ExtractManaged(emptyZip, emptyOut, CancellationToken.None); }
        catch (InvalidDataException) { emptyThrew = true; }
        Check(emptyThrew, "an archive with no files is not taken as extracted");
        // Backups (2.1): "Replace them, no backup" carries on as the master switch off.
        File.WriteAllText(AppInfo.SettingsFile, "{ \"ModBackups\": \"replace\" }");
        File.Delete(AppInfo.SettingsFile + ".bak");
        Check(SettingsStore.Load() is { BackupGameFiles: false, ModBackups: "backup" }, "backups: the old no-backup choice turns the master switch off");
        File.WriteAllText(AppInfo.SettingsFile, "{ }");
        Check(SettingsStore.Load() is { BackupGameFiles: true, ModBackups: "backup" }, "backups: on by default");
        // A game's own pictures.
        SettingsStore.Load();
        var png = Path.Combine(dataDir, "pic.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47]);
        string? changed = null;
        ArtOverrides.Changed += (id, kind) => changed = $"{id}:{kind}";
        var hero = ArtOverrides.Import("steam:1091500", ArtKind.Banner, png);
        Check(ArtOverrides.Get("steam:1091500", ArtKind.Banner) == hero && changed == "steam:1091500:Banner", "an own banner is kept and announced");
        var art = ArtOverrides.Apply(steam, new GameArt { HeroImage = "store.jpg", LogoImage = "logo.png", SteamAppId = "1091500" });
        Check(art.HeroImage == hero && art.LogoImage == "logo.png" && art.SteamAppId == "1091500", "the own banner replaces the store's, the rest stays");
        var hero2 = ArtOverrides.Import("steam:1091500", ArtKind.Banner, png);
        Check(!File.Exists(hero) && File.Exists(hero2), "a new pick deletes the picture it replaces");
        ArtOverrides.Set("steam:1091500", ArtKind.Banner, null);
        Check(ArtOverrides.Get("steam:1091500", ArtKind.Banner) is null && !File.Exists(hero2), "back to the store's removes the own picture");
        // Profiles survive a save and a read.
        SettingsStore.Current.Profiles.Add(new UserProfile { Name = "Alex" });
        SettingsStore.Current.ActiveProfile = "missing";
        SettingsStore.Save();
        SettingsStore.Flush();
        var back = SettingsStore.Load();
        Check(back.Profiles.Count == 1 && back.Profiles[0].Name == "Alex", "a profile is kept in settings");
        Check(back.ActiveProfile == back.Profiles[0].Id, "a profile in use that is gone falls back to the first one");
        foreach (var old in Directory.GetFiles(dataDir, "settings*")) File.Delete(old);
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
    case "stores":
    {
        // The three launchers read from fake records (what their registry entries and installed-programs entries hold).
        var root = Path.Combine(Path.GetTempPath(), "hl-stores-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string Folder(string rel) => Directory.CreateDirectory(Path.Combine(root, rel)).FullName;
        var ac = Folder(@"Ubisoft\games\Assassin's Creed Test");
        var ubi = UbisoftScanner.From([new UbisoftScanner.Install("4923", ac.Replace('\\', '/') + "/"), new UbisoftScanner.Install("99", Path.Combine(root, "gone"))],
            [new UninstallEntry("Uplay Install 4923", "Assassin's Creed Test", null, @"""C:\Ubisoft\upc.exe"" uplay://uninstall/4923", "Ubisoft")]);
        Check(ubi.Count == 1 && ubi[0].Id == "ubisoft:4923" && ubi[0].Name == "Assassin's Creed Test" && ubi[0].Store == GameStore.Ubisoft, "Ubisoft: named from its installed-programs entry, missing folder skipped");
        Check(ubi.Count == 1 && ubi[0].LaunchUri == "uplay://launch/4923/0" && ubi[0].InstallDir == ac, "Ubisoft: uplay://launch link, folder with forward slashes read as a Windows path");

        var ea = Folder(@"EA Games\Test Racer");
        Directory.CreateDirectory(Path.Combine(ea, "__Installer"));
        Directory.CreateDirectory(Path.Combine(ea, "bin"));
        File.WriteAllText(Path.Combine(ea, "bin", "TestRacer.exe"), "fake");
        File.WriteAllText(Path.Combine(ea, "__Installer", "installerdata.xml"), """
            <DiPManifest version="4.0">
              <contentIDs><contentID>1234567</contentID></contentIDs>
              <gameTitles><gameTitle locale="de_DE">Testrennen</gameTitle><gameTitle locale="en_US">Test Racer</gameTitle></gameTitles>
              <runtime>
                <launcher><filePath>[HKEY_LOCAL_MACHINE\SOFTWARE\EA Games\Test Racer\Install Dir]bin\Trial.exe</filePath><trial>1</trial></launcher>
                <launcher><filePath>[HKEY_LOCAL_MACHINE\SOFTWARE\EA Games\Test Racer\Install Dir]bin\TestRacer.exe</filePath><trial>0</trial></launcher>
              </runtime>
            </DiPManifest>
            """);
        var eaGames = EaScanner.From([
            new UninstallEntry("{EA-TEST}", null, ea + "\\", @"""C:\Program Files\Common Files\EAInstaller\Test Racer\Cleanup.exe"" uninstall_game -autologging", "Electronic Arts"),
            new UninstallEntry("EA app", "EA app", Folder("EA Desktop"), @"""C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EAUninstall.exe"" /uninstall", "Electronic Arts"),
            new UninstallEntry("Other", "Some App", Folder("Other"), @"C:\Other\uninstall.exe", "Someone")]);
        Check(eaGames.Count == 1 && eaGames[0].Id == "ea:1234567" && eaGames[0].Name == "Test Racer", "EA: only the EAInstaller entry, id from installerdata.xml, English title");
        Check(eaGames.Count == 1 && eaGames[0].StoreExe == Path.Combine(ea, "bin", "TestRacer.exe"), "EA: exe from the first launcher that is not a trial");

        var gta = Folder(@"Rockstar Games\Test Theft Auto");
        var rock = RockstarScanner.From([new RockstarScanner.Install("Test Theft Auto", gta)],
            [new UninstallEntry("Rockstar Games Launcher", "Rockstar Games Launcher", Folder("Launcher"), @"""C:\Rockstar\Launcher\uninstall.exe"" -uninstall=launcher", "Rockstar Games"),
             new UninstallEntry("Test Theft Auto", "Test Theft Auto V", gta, @"""C:\Rockstar\Launcher\uninstall.exe"" -enableFullMode -uninstall=tta5", "Rockstar Games")], @"C:\Rockstar\Launcher\Launcher.exe");
        Check(rock.Count == 1 && rock[0].Id == "rockstar:tta5" && rock[0].Name == "Test Theft Auto V", "Rockstar: title id and name from its installed-programs entry");
        Check(rock.Count == 1 && rock[0].LauncherCommand?.Args == $"-launchTitleInFolder \"{gta}\"", "Rockstar: started by the launcher with -launchTitleInFolder");
        Check(Uninstaller.SplitCommand("\"" + Path.Combine(ea, "bin", "TestRacer.exe") + "\" uninstall_game") is { Args: "uninstall_game" }, "an uninstall command split into exe and arguments");
        Check(Uninstaller.Route(rock[0]) == UninstallRoute.Launcher && Uninstaller.Route(eaGames[0]) == UninstallRoute.Launcher, "uninstall goes through the launcher's own entry");

        // This PC: what the real scans find (read-only).
        foreach (var g in UbisoftScanner.Scan().Concat(EaScanner.Scan()).Concat(RockstarScanner.Scan()))
            Console.WriteLine($"    this PC: {g.Store} {g.Name} [{g.Id}] {g.InstallDir}");
        Directory.Delete(root, true);
        break;
    }
    case "dxvk":
    {
        var root = Path.Combine(Path.GetTempPath(), "hl-dxvk-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var releases = new Dictionary<DxvkFlavor, List<DxvkRelease>>();
        foreach (var f in Enum.GetValues<DxvkFlavor>())
        {
            releases[f] = await Dxvk.GetReleasesAsync(f);
            Check(releases[f].Count > 0, $"{Dxvk.Name(f)}: {releases[f].Count} releases, newest {releases[f].FirstOrDefault()?.Display} ({releases[f].FirstOrDefault()?.ArchiveName})");
        }
        if (releases.Values.Any(r => r.Count == 0)) break;
        Check(Dxvk.FromApis(["DX11", "DX12", "Vulkan"]).SequenceEqual([D3dVersion.D3D12, D3dVersion.D3D11]), "APIs read as Direct3D versions, newest first");

        foreach (var (flavor, api, is64) in new[] { (DxvkFlavor.Dxvk, D3dVersion.D3D11, true), (DxvkFlavor.Dxvk, D3dVersion.D3D9, false), (DxvkFlavor.GplAsync, D3dVersion.D3D11, true), (DxvkFlavor.Vkd3d, D3dVersion.D3D12, true) })
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"{flavor}-{api}-{(is64 ? 64 : 32)}")).FullName;
            File.WriteAllText(Path.Combine(dir, "Game.exe"), "fake");
            File.WriteAllText(Path.Combine(dir, "d3d9.dll"), "the game's own d3d9");
            File.WriteAllText(Path.Combine(dir, "dxvk.conf"), "# the player's own\ndxgi.maxFrameRate = 60\n");
            var before = Snapshot(dir);
            var dxgi = flavor == DxvkFlavor.Vkd3d ? releases[DxvkFlavor.Dxvk][0] : null;
            var state = await Dxvk.InstallAsync(releases[flavor][0], api, dir, is64, null, dxgi, null, CancellationToken.None);
            var label = $"{Dxvk.Name(flavor)} {Dxvk.Name(api)} {(is64 ? 64 : 32)}-bit";
            var want = Dxvk.Files(flavor, api).Concat(flavor == DxvkFlavor.Vkd3d ? ["dxgi.dll"] : Array.Empty<string>()).ToList();
            Check(want.All(f => state.Files.ContainsKey(f)) && state.Files.Count == want.Count, $"{label}: {string.Join(", ", state.Files.Keys)}");
            var found = Dxvk.Detect(dir);
            Check(want.All(f => found.Any(d => d.File == f)), $"{label}: found again by their own setting names ({string.Join(", ", found.Select(d => d.File + "=" + d.Layer))})");
            Check(RenoDx.Is64Bit(Path.Combine(dir, want[0])) == is64, $"{label}: the {(is64 ? 64 : 32)}-bit dlls went in");
            Check((Dxvk.GetConf(dir, "dxvk.enableAsync") == "True") == (flavor == DxvkFlavor.GplAsync) && Dxvk.GetConf(dir, "dxgi.maxFrameRate") == "60", $"{label}: async {(flavor == DxvkFlavor.GplAsync ? "on" : "untouched")}, the player's line kept");
            Check(Dxvk.Changed(state, dir).Count == 0 && ModCheck.Run(new GameSettings { Dxvk = state }, dir).Count == 0, $"{label}: nothing changed, the mod check is quiet");
            if (api == D3dVersion.D3D9)
            {
                Check(state.Backups.ContainsKey("d3d9.dll"), $"{label}: the game's own d3d9.dll backed up");
                File.WriteAllText(Path.Combine(dir, "d3d9.dll"), "a game update put its own back");
                Check(Dxvk.Changed(state, dir).SequenceEqual(["d3d9.dll"]) && ModCheck.Run(new GameSettings { Dxvk = state }, dir).Any(p => p.Id == "dxvk-changed"), $"{label}: the replaced dll is found, the mod check says so");
                File.WriteAllText(Path.Combine(dir, "d3d9.dll"), "the game's own d3d9");
            }
            if (flavor != DxvkFlavor.Vkd3d && api == D3dVersion.D3D11)
            {
                File.Delete(Path.Combine(dir, "dxgi.dll"));
                Check(ModCheck.Run(new GameSettings(), dir).Any(p => p.Id == "dxvk-dxgi"), $"{label}: d3d11.dll without DXVK's dxgi.dll is reported");
                state = await Dxvk.InstallAsync(releases[flavor][0], api, dir, is64, state, null, null, CancellationToken.None);
                Check(File.Exists(Path.Combine(dir, "dxgi.dll")) && Dxvk.Changed(state, dir).Count == 0, $"{label}: reinstall over the old one puts dxgi.dll back");
            }
            var problems = Dxvk.Uninstall(state, dir);
            Check(problems.Count == 0, $"{label}: uninstalled without problems " + string.Join("; ", problems));
            var after = Snapshot(dir);
            var diff = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b).ToList();
            Check(diff.Count == 0, $"{label}: folder back byte for byte" + (diff.Count > 0 ? ": " + string.Join(", ", diff) : ""));
        }

        // A new dxvk.conf is Hybrid Launcher's own and goes with the layer; a ReShade dll is never replaced.
        var clean = Directory.CreateDirectory(Path.Combine(root, "clean")).FullName;
        var s1 = await Dxvk.InstallAsync(releases[DxvkFlavor.GplAsync][0], D3dVersion.D3D11, clean, true, null, null, null, CancellationToken.None);
        Check(s1.ConfCreated && File.Exists(Path.Combine(clean, "dxvk.conf")), "gplasync without a dxvk.conf: one is made");
        Dxvk.Uninstall(s1, clean);
        Check(Directory.GetFiles(clean).Length == 0, "uninstall leaves the folder empty, dxvk.conf included");
        var reshadeDll = ReShadeSample();
        if (reshadeDll is not null)
        {
            File.Copy(reshadeDll, Path.Combine(clean, "dxgi.dll"));
            string? refused = null;
            try { await Dxvk.InstallAsync(releases[DxvkFlavor.Dxvk][0], D3dVersion.D3D11, clean, true, null, null, null, CancellationToken.None); }
            catch (InvalidOperationException ex) { refused = ex.Message; }
            Check(refused?.Contains("ReShade") == true && ReShade.IsReShade(Path.Combine(clean, "dxgi.dll")) && !File.Exists(Path.Combine(clean, "d3d11.dll")), $"ReShade's dxgi.dll is not replaced: {refused}");
        }
        else Console.WriteLine("    (no saved ReShade installer: the ReShade refusal is not tried)");
        Directory.Delete(root, true);
        break;
    }
    case "spotlight":
    {
        // The Dashboard's Spotlight (GameEvents.Spotlight) on made-up posts: Steam's featured post as well as major updates.
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        GameEvent Post(string id, GameEventKind kind, int daysAgo, bool featured = false, int? endsDaysAgo = null, int changes = 0) =>
            new(id, "1", kind, id, "", "", now.AddDays(-daysAgo), null, null,
                [.. Enumerable.Range(0, changes).Select(i => new UpdateSection("Section " + i, 1, "change"))], [], "", featured,
                endsDaysAgo is { } e ? now.AddDays(-e) : null);
        GameEvent? Pick(params GameEvent[] posts) => GameEvents.Spotlight([.. posts.OrderByDescending(p => p.Date)], now);
        Check(Pick(Post("news", GameEventKind.News, 2, featured: true), Post("major", GameEventKind.MajorUpdate, 20, changes: 4))?.Gid == "news",
            "a featured post newer than the last major update comes first (Steam's Featured)");
        Check(Pick(Post("news", GameEventKind.News, 30, featured: true), Post("major", GameEventKind.MajorUpdate, 5, changes: 4))?.Gid == "major",
            "a major update newer than the featured post comes first");
        Check(Pick(Post("news", GameEventKind.News, 2), Post("small", GameEventKind.SmallUpdate, 10, changes: 4))?.Gid == "small",
            "with nothing featured and no major update: the newest recent update");
        Check(Pick(Post("old", GameEventKind.News, 90, featured: true)) is null, "a featured post older than 60 days is not shown");
        Check(Pick(Post("sale", GameEventKind.Event, 3, featured: true, endsDaysAgo: 1), Post("major", GameEventKind.MajorUpdate, 40, changes: 4))?.Gid == "major",
            "a featured sale that has ended is left out");
        var notes = Pick(Post("news", GameEventKind.News, 1, featured: true), Post("small", GameEventKind.SmallUpdate, 3, changes: 5));
        Check(notes?.Gid == "news" && notes.Outline.Count == 0, "a featured news post does not take another update's notes");
        break;
    }
    case "modcheck":
    {
        var root = Path.Combine(Path.GetTempPath(), "hl-modcheck-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string Fresh(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        List<string> Ids(GameSettings s, string dir) => ModCheck.Run(s, dir).Select(p => p.Id).ToList();

        var empty = Fresh("empty");
        Check(Ids(new GameSettings(), empty).Count == 0, "an empty folder has nothing to report");

        var addon = Fresh("addon");
        File.WriteAllText(Path.Combine(addon, "renodx-testgame.addon64"), "fake");
        Check(Ids(new GameSettings(), addon).SequenceEqual(["addon-no-reshade"]), "an add-on with no ReShade is reported");

        var normal = ReShadeSample(addonBuild: false);
        var full = ReShadeSample(addonBuild: true);
        if (normal is not null && full is not null)
        {
            File.Copy(normal, Path.Combine(addon, "dxgi.dll"));
            Check(ModCheck.LimitedAddons(Path.Combine(addon, "dxgi.dll")) && !ModCheck.LimitedAddons(full), "the normal ReShade build is told from the add-on one");
            Check(Ids(new GameSettings(), addon).SequenceEqual(["addon-limited"]), "an add-on beside the normal ReShade build is reported");
            File.Copy(full, Path.Combine(addon, "dxgi.dll"), true);
            Check(Ids(new GameSettings(), addon).Count == 0, "with the add-on build nothing is reported");

            var twice = Fresh("twice");
            File.Copy(full, Path.Combine(twice, "d3d11.dll"));
            File.Copy(full, Path.Combine(twice, ReShade.OptiDllName));
            File.WriteAllText(Path.Combine(twice, "OptiScaler.ini"), "[Plugins]\nLoadReshade=true\n");
            var s = new GameSettings();
            s.IniOverrides[ReShade.IniKey] = "true";
            Check(Ids(s, twice).SequenceEqual(["reshade-twice"]), "ReShade as d3d11.dll and through OptiScaler: loads twice");
            ModCheck.StopOptiLoadingReShade(s, twice);
            Check(ReShade.OptiLoadsReShade(twice) == false && !s.IniOverrides.ContainsKey(ReShade.IniKey) && Ids(s, twice).Count == 0, "the fix turns LoadReshade off in the ini and the saved choices");
        }
        else Console.WriteLine("    (no saved ReShade installers: the ReShade checks are not tried)");

        var gone = Fresh("gone");
        var settings = new GameSettings
        {
            Install = new InstallManifest { TargetDir = gone, DllName = "dxgi.dll", Tag = "v0.9.0" },
            Luma = new LumaState { Mod = "Test Luma" },
        };
        settings.RenoDxFiles["renodx-gone.addon64"] = "snapshot";
        Check(Ids(settings, gone).OrderBy(x => x).SequenceEqual(["luma-gone", "opti-gone", "renodx-gone"]), "mods a game update removed: OptiScaler, RenoDX, Luma");
        File.WriteAllText(Path.Combine(gone, "dxgi.dll"), "not OptiScaler");
        Check(Ids(settings, gone).Contains("opti-replaced"), "OptiScaler's dll replaced by another file is reported");
        Directory.Delete(root, true);
        break;
    }
    case "dlssnrreshade":
    {
        // DLSSNR-AMD's ReShade way (1.4.0-rc.20): a stage made from the package zip (HL_DLSSNR_ZIP) in a scratch HL_DATA, put
        // into a fake game that has a dxgi.dll, a ReShade.ini and another add-on of its own, by each route, then taken out.
        var zipPath = Environment.GetEnvironmentVariable("HL_DLSSNR_ZIP");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HL_DATA")) || zipPath is null || !File.Exists(zipPath)) { Console.WriteLine("    (needs HL_DATA and HL_DLSSNR_ZIP)"); break; }
        var stageDir = Path.Combine(AppInfo.DataRoot, "DlssNrAmd", "stage");
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        Directory.CreateDirectory(Path.Combine(stageDir, "dlssnr-amd"));
        File.WriteAllText(Path.Combine(stageDir, "dlssnr-amd", "CC_SILVER_AARDWOLD.bin"), "weights");
        File.WriteAllText(Path.Combine(stageDir, "dlssnr-amd.asi"), "plugin");
        using (var z = System.IO.Compression.ZipFile.OpenRead(zipPath))
            foreach (var e in z.Entries)
            {
                var top = e.FullName.Split('/')[0];
                if (top is not ("reshade" or "vulkan" or "dxvk" or "ini") || e.FullName.EndsWith('/')) continue;
                var to = Path.Combine(stageDir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                System.IO.Compression.ZipFileExtensions.ExtractToFile(e, to, true);
            }
        File.WriteAllText(Path.Combine(stageDir, "hl-stage.json"), "{\"Tag\":\"test\",\"ModelVersion\":\"310.8.0\",\"Networks\":[\"CC_SILVER_AARDWOLD\"]}");
        Check(DlssNrAmd.StageHasReShade(), "the stage has the ReShade way's files");
        var game = Path.Combine(Path.GetTempPath(), "hl-dlssnr-reshade-game");
        if (Directory.Exists(game)) Directory.Delete(game, true);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "game.exe"), "exe");
        File.WriteAllText(Path.Combine(game, "dxgi.dll"), "the game's own dxgi");
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), "the game's own ReShade.ini");
        File.WriteAllText(Path.Combine(game, "other.addon64"), "another add-on");
        string FolderState() => string.Join("|", Directory.EnumerateFileSystemEntries(game, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(game, f) + (File.Exists(f) ? ":" + File.ReadAllText(f).Length : "")).Order());
        var before = FolderState();
        var reshade64 = new FileInfo(Path.Combine(stageDir, "reshade", "ReShade64.dll")).Length;
        foreach (var route in DlssNrAmd.ReShadeRoutes)
        {
            var gs = new GameSettings();
            DlssNrAmd.ApplyReShade(gs, game, route, "CC_SILVER_AARDWOLD", renoDx: true);
            bool layer = route != "reshade-feeder";
            Check(gs.DlssNrAmdReShade is { } i && i.Route == route, $"{route}: recorded");
            Check(File.Exists(Path.Combine(game, "dlssnr_amd.addon64")) && Directory.Exists(Path.Combine(game, "reshade-shaders")) && File.Exists(Path.Combine(game, "dlssnr-amd", "CC_SILVER_AARDWOLD.bin")), $"{route}: add-on, shaders and networks in");
            Check(!File.Exists(Path.Combine(game, "other.addon64")), $"{route}: the other add-on kept aside");
            if (!layer) Check(new FileInfo(Path.Combine(game, "dxgi.dll")).Length == reshade64 && !File.Exists(Path.Combine(game, "ReShade64.dll")), $"{route}: ReShade is dxgi.dll");
            else Check(File.Exists(Path.Combine(game, "vulkan-1.dll")) && File.Exists(Path.Combine(game, "ReShade64.dll")) && Directory.Exists(Path.Combine(game, "vk-override")), $"{route}: Vulkan loader and ReShade's layer in");
            if (route == "reshade-feeder-dx9") Check(File.Exists(Path.Combine(game, "d3d9.dll")) && new FileInfo(Path.Combine(game, "dxgi.dll")).Length > 1000, $"{route}: DXVK's d3d9.dll and dxgi.dll in");
            Check(DlssNrAmd.GetAt(game, "Network", "Model") == "CC_SILVER_AARDWOLD" && DlssNrAmd.GetAt(game, "RenoDX", "Enabled") == "1", $"{route}: dlssnr-amd.ini with the network and the RenoDX style");
            DlssNrAmd.RemoveReShade(gs);
            Check(gs.DlssNrAmdReShade is null && FolderState() == before, $"{route}: taken out, the folder exactly as before");
            DlssNrAmd.ForgetSavedReShadeIni(game);
        }
        var withOpti = new GameSettings { Install = new InstallManifest { TargetDir = game } };
        Check(DlssNrAmd.CannotGoIntoReShade(withOpti, game) is not null, "refused over Hybrid Launcher's OptiScaler");
        Check(DlssNrAmd.RouteFor(["Vulkan"]) == "reshade-feeder-vulkan" && DlssNrAmd.RouteFor(["DX9"]) == "reshade-feeder-dx9" && DlssNrAmd.RouteFor(["DX11", "Vulkan"]) == "reshade-feeder", "route picked from the graphics APIs");
        Directory.Delete(game, true);
        break;
    }
    case "auto":
    {
        static Dictionary<string, string> F(params (string, string)[] x) => x.ToDictionary(t => t.Item1, t => t.Item2, StringComparer.OrdinalIgnoreCase);
        void Show(string title, AutoPlan p)
        {
            Console.WriteLine($"-- {title}: opti {p.OptiDll}, reshade {p.ReShadeDll}{(p.ReShadeThroughOpti ? " (through OptiScaler)" : "")}{(p.ReShadeAddon ? " addon" : "")}");
            foreach (var i in p.Items) Console.WriteLine($"   {i.Id,-8} {i.State,-11} {(i.Selected ? "[x]" : "[ ]")} {i.Why}");
            foreach (var r in p.Resolved) Console.WriteLine($"   resolved: {r}");
        }
        var renodx = new AutoCandidate("Some Game", true, true);
        var luma = new AutoCandidate("Luma-Some Game", true, true);

        // A plain DX12 game on AMD, no HDR: OptiScaler only, as dxgi.dll, spoofing on; ReShade offered, not ticked.
        var p1 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], RenoDx = renodx, Luma = luma });
        Show("DX12, AMD, SDR", p1);
        Check(p1["opti"]!.Acts && p1.OptiDll == "dxgi.dll" && p1.Spoofing, "SDR: OptiScaler as dxgi.dll, spoofing on");
        Check(!p1["reshade"]!.Selected && !p1["renodx"]!.Selected && !p1["luma"]!.Selected, "SDR: no ReShade, no HDR mod");

        // HDR on: RenoDX ticked, ReShade forced with full add-on support and loaded by OptiScaler; Luma left out.
        var p2 = AutoSetup.Plan(new AutoFacts { Apis = ["DX11", "DX12"], Hdr = true, RenoDx = renodx, Luma = luma });
        Show("DX11/12, HDR", p2);
        Check(p2["renodx"]!.Acts && !p2["luma"]!.Selected, "HDR: RenoDX, not Luma");
        Check(p2["reshade"]!.Acts && p2["reshade"]!.Locked && p2.ReShadeAddon && p2.ReShadeThroughOpti && p2.ReShadeDll == "ReShade64.dll", "HDR: ReShade add-on build as ReShade64.dll through OptiScaler");
        // ReShade's build (2.0.0-rc.13): RenoDX forces the add-on build even when standard is picked; alone it follows the pick.
        var p2s = AutoSetup.Plan(new AutoFacts { Apis = ["DX11", "DX12"], Hdr = true, RenoDx = renodx, Luma = luma }, new AutoChoice { ReShadeAddon = false });
        Check(p2s.ReShadeAddon && p2s.ReShadeAddonNeeded, "RenoDX keeps the add-on build when standard is picked");
        var pStd = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"] }, new AutoChoice { ReShade = true });
        var pAdd = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"] }, new AutoChoice { ReShade = true, ReShadeAddon = true });
        Check(pStd["reshade"]!.Acts && !pStd.ReShadeAddon && pAdd.ReShadeAddon && !pAdd.ReShadeAddonNeeded, "ReShade alone: standard by default, add-on when picked");

        // Luma picked by hand: RenoDX goes out, ReShade is covered by Luma.
        var p3 = AutoSetup.Plan(new AutoFacts { Apis = ["DX11"], Hdr = true, RenoDx = renodx, Luma = luma }, new AutoChoice { Luma = true, RenoDx = false });
        Show("Luma by hand", p3);
        Check(p3["luma"]!.Acts && !p3["renodx"]!.Selected && p3["reshade"]!.State == AutoState.Covered, "Luma: RenoDX out, ReShade covered");

        // Generic mods (2.0.0-rc.18): offered, never ticked by themselves even with HDR on, and their row says so.
        var genericReno = new AutoCandidate("Unreal Engine Extended", true, true, "Unreal");
        var genericLuma = new AutoCandidate("Unreal Engine (generic)", true, true, "Unreal");
        var pg = AutoSetup.Plan(new AutoFacts { Apis = ["DX11", "DX12"], Hdr = true, RenoDx = genericReno, Luma = genericLuma });
        Show("generic RenoDX and Luma, HDR", pg);
        Check(pg["renodx"]!.State == AutoState.Install && !pg["renodx"]!.Selected && !pg["renodx"]!.Recommended
            && !pg["luma"]!.Selected && !pg["luma"]!.Recommended, "generic mods offered, not ticked");
        Check(pg["renodx"]!.Why.StartsWith("Generic mod for Unreal Engine games") && pg["luma"]!.Why.StartsWith("Generic mod for Unreal Engine games"), "generic mods flagged in their rows");
        Check(!p2["luma"]!.Why.Contains("An HDR mod") && p2["renodx"]!.Why.StartsWith("Mainly an HDR mod"), "Luma not described as an HDR mod; RenoDX as mainly HDR");
        var pgPicked = AutoSetup.Plan(new AutoFacts { Apis = ["DX11"], Hdr = true, RenoDx = genericReno }, new AutoChoice { RenoDx = true });
        Check(pgPicked["renodx"]!.Acts, "a generic mod goes in when ticked by hand");

        // The wiki says winmm.dll and no spoofing; dxgi.dll holds ReShade already: OptiScaler as winmm.dll, ReShade stays.
        var rsDxgi = new DetectedReShade(@"C:\g\dxgi.dll", "6.3.0");
        var p4 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], WikiFilename = "winmm.dll", WikiSpoofing = false, ReShade = rsDxgi, Files = F(("dxgi.dll", "reshade")) });
        Show("wiki winmm, ReShade dxgi", p4);
        Check(p4.OptiDll == "winmm.dll" && !p4.Spoofing && p4.MoveReShadeFirst is null, "wiki name and no spoofing kept, ReShade left alone");

        // No wiki name, ReShade on dxgi.dll: OptiScaler takes dxgi.dll and ReShade moves to ReShade64.dll first.
        var p5 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], ReShade = rsDxgi, Files = F(("dxgi.dll", "reshade")) });
        Show("ReShade dxgi", p5);
        Check(p5.OptiDll == "dxgi.dll" && p5.MoveReShadeFirst == "dxgi.dll", "ReShade moved out of OptiScaler's way");

        // An unknown dxgi.dll and the wiki's name taken: the next free name.
        var p6 = AutoSetup.Plan(new AutoFacts { Apis = ["DX11"], WikiFilename = "winmm.dll", Files = F(("dxgi.dll", "other"), ("winmm.dll", "other")) });
        Show("names taken", p6);
        Check(p6.OptiDll == "version.dll" && p6.Resolved.Count > 0, "OptiScaler as version.dll, said why");

        // 32-bit DX9: no OptiScaler; ReShade as d3d9.dll when ticked.
        var p7 = AutoSetup.Plan(new AutoFacts { Is64 = false, Apis = ["DX9"] }, new AutoChoice { ReShade = true });
        Show("32-bit DX9", p7);
        Check(p7["opti"]!.State == AutoState.NotPossible && p7["reshade"]!.Acts && p7.ReShadeDll == "d3d9.dll", "DX9: ReShade as d3d9.dll, no OptiScaler");

        // Vulkan only: OptiScaler as winmm.dll, ReShade not possible here.
        var p8 = AutoSetup.Plan(new AutoFacts { Apis = ["Vulkan"] });
        Show("Vulkan", p8);
        Check(p8.OptiDll == "winmm.dll" && p8["reshade"]!.State == AutoState.NotPossible, "Vulkan: winmm.dll, no ReShade");

        // DXVK in (2.0.0-rc.16: the others are still offered, the user decides): OptiScaler under a name the layer does not hold.
        var p9 = AutoSetup.Plan(new AutoFacts { Apis = ["DX11"], Hdr = true, Layer = true, RenoDx = renodx, Files = F(("dxgi.dll", "layer"), ("d3d11.dll", "layer")) });
        Show("DXVK", p9);
        Check(p9["opti"]!.State == AutoState.Install && p9.OptiDll != "dxgi.dll" && p9.OptiDll != "d3d11.dll", "DXVK in: OptiScaler still offered, not as dxgi.dll or d3d11.dll");

        // Normal ReShade build already in, RenoDX ticked: ReShade replaced with the add-on build.
        var p10 = AutoSetup.Plan(new AutoFacts { Apis = ["DX11"], Hdr = true, OptiFile = "dxgi.dll", RenoDx = renodx,
            ReShade = new DetectedReShade(@"C:\g\ReShade64.dll", "6.3.0"), ReShadeLimited = true, Files = F(("dxgi.dll", "opti")) });
        Show("limited ReShade", p10);
        Check(p10["reshade"]!.State == AutoState.Update && p10["reshade"]!.Acts && p10.ReShadeThroughOpti, "normal build replaced for RenoDX");
        Check(AutoSetup.SpoofingFromWiki("Dxgi = false for Intel/AMD as Spoofing is unnecessary") == false, "wiki spoofing text read");

        // DLSS 5, F5 on an RTX card: OptiScaler is needed and fixed to janblade's F5 build; the official channels are not offered.
        var p11 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], Nvidia = true, Dlss5Tier = Dlss5Gpu.NvidiaRtx }, new AutoChoice { Dlss5 = AutoSetup.F5 });
        Show("DLSS 5 F5", p11);
        Check(p11["dlss5"]!.Acts && p11["opti"]!.Acts && p11["opti"]!.Locked && p11.OptiSource == AutoSetup.F5Source, "F5: OptiScaler locked to the F5 build");
        Check(!p11.AllowsSource(AutoSetup.Stable) && p11.AllowsSource("custom:my build"), "F5: stable refused, an imported build allowed");
        // The F5 way with an official OptiScaler in: it is replaced.
        var p12 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], Nvidia = true, Dlss5Tier = Dlss5Gpu.NvidiaRtx, OptiFile = "dxgi.dll", Files = F(("dxgi.dll", "opti")) }, new AutoChoice { Dlss5 = AutoSetup.F5 });
        Show("F5 over an official build", p12);
        Check(p12["opti"]!.State == AutoState.Update && p12["opti"]!.Locked && p12.OptiDll == "dxgi.dll", "F5: the official build in is replaced, same name");
        // DLSSNR-AMD on RDNA 4: the nightly first, NR forks refused; a hand-picked F5 source is overridden and said.
        var p13 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], Dlss5Tier = Dlss5Gpu.AmdRdna4 }, new AutoChoice { Dlss5 = AutoSetup.Amd, OptiSource = AutoSetup.F5Source });
        Show("DLSSNR-AMD", p13);
        Check(p13.OptiSource == AutoSetup.Nightly && !p13.AllowsSource(AutoSetup.F5Source) && p13.Resolved.Count > 0, "DLSSNR-AMD: nightly, not the F5 build, said why");
        // DLSSNR-AMD with a DLSS NR build in: replaced by an official one.
        var p14 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], Dlss5Tier = Dlss5Gpu.AmdRdna4, OptiFile = "dxgi.dll", OptiIsDlssNr = true, OptiSourceNow = AutoSetup.F5Source, Files = F(("dxgi.dll", "opti")) },
            new AutoChoice { Dlss5 = AutoSetup.Amd });
        Show("DLSSNR-AMD over F5", p14);
        Check(p14["opti"]!.State == AutoState.Update && p14.OptiSource == AutoSetup.Nightly, "DLSSNR-AMD: the NR build in is replaced by the nightly");
        // DLSSNR-AMD's ReShade way: no OptiScaler, ReShade comes with it, the HDR mods stay out.
        var p15 = AutoSetup.Plan(new AutoFacts { Apis = ["DX11"], Hdr = true, Dlss5Tier = Dlss5Gpu.AmdRdna3, RenoDx = renodx, Luma = luma }, new AutoChoice { Dlss5 = AutoSetup.AmdReShade });
        Show("DLSSNR-AMD through ReShade", p15);
        Check(!p15["opti"]!.Selected && p15["reshade"]!.State == AutoState.Covered && !p15["renodx"]!.Selected && !p15["luma"]!.Selected, "ReShade way: no OptiScaler, ReShade covered, no HDR mod");
        // A card that cannot run DLSSNR-AMD is not offered it.
        var p16 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], Dlss5Tier = Dlss5Gpu.Intel }, new AutoChoice { Dlss5 = AutoSetup.Amd });
        Check(p16.Dlss5 == AutoSetup.Off && !AutoSetup.Dlss5Ways(Dlss5Gpu.Intel).Contains(AutoSetup.Amd), "Intel: DLSSNR-AMD not offered");
        // A source picked by hand without DLSS 5 is kept.
        var p17 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"] }, new AutoChoice { OptiSource = "fork:LorenzoKy/Optiscaler-Venom" });
        Check(p17.OptiSource == "fork:LorenzoKy/Optiscaler-Venom" && p17.Resolved.Count == 0, "a fork picked by hand is kept");
        // Ultra+ ticked: OptiScaler never takes a name UE4SS loads through (the wiki's winmm.dll is skipped).
        var p18 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], UltraFits = true, WikiFilename = "winmm.dll" }, new AutoChoice { Ultra = true });
        Show("Ultra+", p18);
        Check(p18["ultra"]!.Acts && p18.OptiDll == "dxgi.dll", "Ultra+: OptiScaler as dxgi.dll, not winmm.dll");
        // OptiScaler already winmm.dll and Ultra+ ticked: renamed in place first.
        var p19 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], UltraFits = true, OptiFile = "winmm.dll", Files = F(("winmm.dll", "opti")) }, new AutoChoice { Ultra = true });
        Check(p19.OptiRename == "dxgi.dll" && p19.Resolved.Count > 0, "Ultra+: OptiScaler renamed from winmm.dll to dxgi.dll");
        // DXVK ticked with OptiScaler: both go in, OptiScaler under a name DXVK does not use.
        var p20 = AutoSetup.Plan(new AutoFacts { Apis = ["DX11"] }, new AutoChoice { Dxvk = true, Opti = true });
        Show("DXVK ticked", p20);
        Check(p20["dxvk"]!.Acts && p20["opti"]!.Acts && p20.OptiDll is not ("dxgi.dll" or "d3d11.dll"), "DXVK ticked with OptiScaler: both in, OptiScaler as " + p20.OptiDll);
        // A ReShade in, ticked: put in again with the version picked, under its own name.
        var p21 = AutoSetup.Plan(new AutoFacts { Apis = ["DX11"], Is64 = false, ReShade = new DetectedReShade(@"C:\g\d3d11.dll", "6.1.0"), Files = F(("d3d11.dll", "reshade")) }, new AutoChoice { ReShade = true });
        Check(p21["reshade"]!.State == AutoState.Update && p21.ReShadeDll == "d3d11.dll", "ReShade in: replaced under its own name");
        // OptiScaler in, ticked: put in again from the source it came from.
        var p22 = AutoSetup.Plan(new AutoFacts { Apis = ["DX12"], OptiFile = "version.dll", OptiSourceNow = AutoSetup.Nightly, Files = F(("version.dll", "opti")) }, new AutoChoice { Opti = true });
        Check(p22["opti"]!.State == AutoState.Update && p22.OptiSource == AutoSetup.Nightly && p22.OptiDll == "version.dll", "OptiScaler in: replaced from its own source, same name");

        // Direct install: the add-on build's ReShade64.dll out of the installer, ReShade.ini, standard effects.
        var dir = Path.Combine(Path.GetTempPath(), "hl-auto-test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ReShade32.dll"), "not reshade");
        var version = await ReShade.InstallDirectAsync(dir, "ReShade64.dll", addon: true, is64: true, null, CancellationToken.None);
        var placed = Path.Combine(dir, "ReShade64.dll");
        Check(File.Exists(placed) && ReShade.IsReShade(placed), $"ReShade {version} put in as ReShade64.dll");
        Check(!ModCheck.LimitedAddons(placed), "the build with full add-on support");
        Check(File.ReadAllText(Path.Combine(dir, "ReShade.ini")).Contains("PerformanceMode=1"), "recommended ReShade.ini written");
        Check(Directory.GetFiles(Path.Combine(dir, "reshade-shaders", "Shaders"), "*.fx*", SearchOption.AllDirectories).Length > 5, "standard effects in reshade-shaders");
        Check(File.Exists(Path.Combine(dir, "ReShade32.dll")), "a file that is not ReShade left alone");
        File.WriteAllText(Path.Combine(dir, "ReShade.ini"), "[GENERAL]\nmine=1\n");
        await ReShade.InstallDirectAsync(dir, "dxgi.dll", addon: false, is64: true, null, CancellationToken.None);
        Check(File.Exists(Path.Combine(dir, "dxgi.dll")) && !File.Exists(placed), "moved to dxgi.dll: one ReShade per folder");
        Check(ModCheck.LimitedAddons(Path.Combine(dir, "dxgi.dll")), "the normal build this time");
        Check(File.ReadAllText(Path.Combine(dir, "ReShade.ini")).Contains("mine=1"), "the user's own ReShade.ini kept");
        Directory.Delete(dir, true);
        break;
    }
    case "dllswap":
    {
        // A real OptiScaler.dll from an archive Hybrid Launcher saved, in a fake game as dxgi.dll over the game's own dxgi.dll.
        var archive = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HybridLauncher", "cache", "downloads"))
            ? Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HybridLauncher", "cache", "downloads"), "OptiScaler*.7z", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (archive is null) { Console.WriteLine("    (no saved OptiScaler archive: nothing to try)"); break; }
        var root = Path.Combine(Path.GetTempPath(), "hl-dllswap-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var unpacked = Path.Combine(root, "unpacked");
        await ArchiveExtractor.ExtractAsync(archive, unpacked, CancellationToken.None);
        var opti = Directory.GetFiles(unpacked, "OptiScaler.dll", SearchOption.AllDirectories)[0];
        var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
        File.WriteAllText(Path.Combine(backups, "dxgi.dll"), "the game's own dxgi");
        File.Copy(opti, Path.Combine(game, "dxgi.dll"));
        File.WriteAllText(Path.Combine(game, "OptiScaler.ini"), "[Plugins]\n");
        File.WriteAllText(Path.Combine(game, "version.dll"), "someone else's version.dll");
        var m = new InstallManifest { TargetDir = game, DllName = "dxgi.dll", Tag = "test" };
        m.Files["dxgi.dll"] = OptiInstaller.Hash(Path.Combine(game, "dxgi.dll"));
        m.Files["OptiScaler.ini"] = "x";
        m.Backups["dxgi.dll"] = Path.Combine(backups, "dxgi.dll");

        Check(OptiDllSwap.WhyNot(m, "version.dll") is { } taken && taken.Contains("taken"), $"a name another file holds is refused: {OptiDllSwap.WhyNot(m, "version.dll")}");
        Check(!OptiDllSwap.Names.Any(n => n.EndsWith(".asi")), "OptiScaler.asi is not offered for a quick swap");
        OptiDllSwap.Swap(m, "winmm.dll");
        Check(OptiDetector.IsOptiScaler(Path.Combine(game, "winmm.dll")) && OptiDetector.Detect(game)?.FileName == "winmm.dll", "OptiScaler is winmm.dll now");
        Check(File.ReadAllText(Path.Combine(game, "dxgi.dll")) == "the game's own dxgi" && !m.Backups.ContainsKey("dxgi.dll"), "the game's own dxgi.dll is back");
        Check(m.DllName == "winmm.dll" && m.Files.ContainsKey("winmm.dll") && !m.Files.ContainsKey("dxgi.dll") && !OptiDetector.ReplacedSince(m), "the record follows: name, files, hash");
        Check(ModCheck.Run(new GameSettings { Install = m }, game).Count == 0, "the mod check sees nothing wrong");
        string? refused = null;
        try { OptiDllSwap.Swap(m, "dxgi.dll"); } catch (InvalidOperationException ex) { refused = ex.Message; }
        Check(refused is not null && File.Exists(Path.Combine(game, "winmm.dll")), $"back to dxgi.dll is refused while the game's own is there: {refused}");
        File.Delete(Path.Combine(game, "dxgi.dll"));
        OptiDllSwap.Swap(m, "dxgi.dll");
        Check(m.DllName == "dxgi.dll" && OptiDetector.Detect(game)?.FileName == "dxgi.dll" && !File.Exists(Path.Combine(game, "winmm.dll")), "and once it is free, swapped back");

        // The user's case (rc.18): the install's "backup" of dxgi.dll was an older OptiScaler it had replaced. The swap must not
        // put it back, and a stray OptiScaler under another name is taken out.
        File.Copy(opti, Path.Combine(backups, "old-opti.dll"));
        m.Backups["dxgi.dll"] = Path.Combine(backups, "old-opti.dll");
        m.BackupRoot = Path.Combine(root, "backup-root");
        OptiDllSwap.Swap(m, "winmm.dll");
        Check(!File.Exists(Path.Combine(game, "dxgi.dll")) && !m.Backups.ContainsKey("dxgi.dll"), "an older OptiScaler backed up as dxgi.dll is not put back");
        File.Copy(opti, Path.Combine(game, "d3d12.dll"));
        File.Copy(opti, Path.Combine(game, "dxgi.dll"));
        var removed = OptiDllSwap.RemoveOtherCopies(m);
        Check(removed.Count == 2 && !File.Exists(Path.Combine(game, "d3d12.dll")) && !File.Exists(Path.Combine(game, "dxgi.dll"))
            && File.Exists(Path.Combine(game, "winmm.dll")) && File.Exists(Path.Combine(game, "version.dll")), $"other OptiScaler copies taken out, everything else kept: {string.Join(", ", removed)}");
        Directory.Delete(root, true);
        break;
    }
    case "ultra":
    {
        // Ultra+ on a fake Unreal game folder (Lies of P's layout): the lists, a real install, an update that keeps the user's
        // settings, an add-on, the settings file, an OptiScaler proxy clash, and an uninstall that leaves the folder as it was.
        var root = Path.Combine(Path.GetTempPath(), "hl-ultra-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var games = await UltraPlus.GetGamesAsync();
        var files = await UltraPlus.GetModFilesAsync();
        var addonFiles = await UltraPlus.GetAddonFilesAsync();
        await UltraPlus.LoadSettingTextsAsync();
        Console.WriteLine($"Ultra+: {games.Count} games, {files.Count} mod builds, {addonFiles.Count} add-on builds");
        Check(games.Count > 50 && files.Count > 100, "the lists load");
        var install = Directory.CreateDirectory(Path.Combine(root, "Lies of P")).FullName;
        var exe = Path.Combine(install, @"LiesofP\Binaries\Win64\LOP-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "fake");
        File.WriteAllText(Path.Combine(install, "LiesofP.exe"), "fake bootstrap");
        Directory.CreateDirectory(Path.Combine(install, @"LiesofP\Content\Paks"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "dwmapi.dll"), "someone else's dwmapi");
        var before = Directory.GetFiles(install, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

        var hits = UltraPlus.Match(games, "Lies of P", install, exe);
        Check(hits.Count > 0 && hits[0].Game.Key == "LiesOfP" && hits[0].Score == 100, $"matched by exe: {hits.FirstOrDefault()?.Game.Key} ({hits.FirstOrDefault()?.Reason})");
        Check(UltraPlus.Match(games, "Lies of P", install, Path.Combine(install, "LiesofP.exe")).FirstOrDefault()?.Game.Key == "LiesOfP", "matched by name from the bootstrap exe");
        var game = hits[0].Game;
        var layout = UltraPlus.Layout(game, install, exe);
        Check(layout.Root == install && layout.ProjectDir == Path.Combine(install, "LiesofP") && layout.ExeDir == Path.GetDirectoryName(exe), "layout: install, project and exe folders");
        var versions = UltraPlus.Versions(game, files, gamePass: false, betas: false);
        Console.WriteLine("  versions: " + string.Join(", ", versions.Select(v => v.Version)));
        Check(versions.Count >= 2 && versions.All(v => !v.IsGamePass), "Steam builds only, at least two");
        Check(UltraPlus.Versions(game, files, gamePass: true, betas: false).All(v => v.IsGamePass), "Game Pass builds for Game Pass");
        Check(UltraPlus.Compare("6.5.1", "6.4.13") > 0 && UltraPlus.Compare("13.3.0_beta4", "13.3.0") < 0 && UltraPlus.Compare("1.0.1", "1.0.1") == 0, "version order");
        Check(UltraPlus.AddonFamily("EnhancedRayTracing_LessVRAM") == "EnhancedRayTracing" && UltraPlus.AddonFamily("RT_PoolSize") == "RT_PoolSize" && UltraPlus.AddonFamily("FSR3-FG") == "FSR3-FG",
            "add-on variants: EnhancedRayTracing_* share a family, RT_PoolSize stands alone");

        // An older build first, then the newest as an update.
        var older = versions[1];
        var zipOld = await UltraPlus.DownloadAsync(older, null, CancellationToken.None);
        Check(File.Exists(zipOld), $"downloaded and SHA-256 checked: {older.FileName}");
        double lastStep = 0;
        var state = await UltraPlus.InstallAsync(game, older, zipOld, layout, false, null, true, CancellationToken.None, new Progress<double>(p => lastStep = p));
        Check(state.Files.All(f => File.Exists(Path.Combine(install, f))), $"{state.Files.Count} files on disk");
        Check(File.Exists(Path.Combine(layout.Ue4ssDir!, "UE4SS.dll")) && Directory.Exists(Path.Combine(layout.Ue4ssDir!, @"Mods\UltraPlusExtensions")), "UE4SS and UltraPlusExtensions beside the exe");
        Check(Directory.GetFiles(layout.PakModsDir!, "*.pak").Length > 0, "pak in Content\\Paks\\~mods");
        Check(state.Backups.ContainsKey(@"LiesofP\Binaries\Win64\dwmapi.dll"), "the dwmapi.dll that was there is backed up");
        Check(UltraPlus.Owns(state, Path.Combine(layout.ExeDir!, "dwmapi.dll")) && !UltraPlus.Owns(state, Path.Combine(layout.ExeDir!, "dxgi.dll")), "Ultra+ owns dwmapi.dll, not dxgi.dll (OptiScaler's install checks this)");
        var folder = UltraPlus.Inspect(layout, state);
        Check(folder.Installed && folder.Ue4ss && folder.Version is { Length: > 0 }, $"seen in the folder: version {folder.Version}");

        var settings = UltraPlus.ReadSettings(layout.ConfigDir!);
        var values = UltraPlus.ReadConfig(layout.ConfigDir!);
        Console.WriteLine($"  settings: {settings.Count}, groups: {string.Join(", ", settings.Select(s => s.Category).Distinct())}");
        Check(settings.Count > 10 && settings.Any(s => s.Numeric) && settings.Any(s => s.Values.Count > 1), "settings read with their kinds");
        Check(settings.All(s => values.ContainsKey(s.Key) || s.Default.Length > 0), "every setting has a value or a default");
        var pick = settings.First(s => !s.Numeric && s.Values.Count > 2);
        var newValue = pick.Values.First(v => !v.Equals(values.GetValueOrDefault(pick.Key), StringComparison.OrdinalIgnoreCase));
        UltraPlus.WriteConfig(layout.ConfigDir!, new Dictionary<string, string> { [pick.Key] = newValue });
        Check(UltraPlus.ReadConfig(layout.ConfigDir!)[pick.Key] == newValue, $"{pick.Key} set to {newValue}");
        UltraPlus.WriteCustom(layout.ConfigDir!, "r.Shadow.MaxResolution=4096");
        Check(UltraPlus.ReadCustom(layout.ConfigDir!) == "r.Shadow.MaxResolution=4096", "CustomSettings.ini written");
        var presets = UltraPlus.ShippedPresets(layout.ConfigDir!);
        Check(presets.Count >= 3, "presets: " + string.Join(", ", presets.Select(p => p.Name)));

        var newest = versions[0];
        Check(UltraPlus.UpdateFor(state, versions, false)?.FileName == newest.FileName, $"update found: {newest.Version}");
        var zipNew = await UltraPlus.DownloadAsync(newest, null, CancellationToken.None);
        state = await UltraPlus.InstallAsync(game, newest, zipNew, layout, false, state, true, CancellationToken.None);
        Check(UltraPlus.ReadConfig(layout.ConfigDir!).GetValueOrDefault(pick.Key) == newValue, "the user's value survives the update");
        Check(UltraPlus.ReadCustom(layout.ConfigDir!) == "r.Shadow.MaxResolution=4096", "CustomSettings.ini survives the update");
        Check(File.Exists(Path.Combine(layout.ConfigDir!, "UltraPlusConfig.default")), "shipped config kept as UltraPlusConfig.default");
        Check(UltraPlus.UpdateFor(state, versions, false) is null, "no update after updating");

        var addons = UltraPlus.Addons(game, addonFiles, false);
        Console.WriteLine("  add-ons: " + string.Join(", ", addons.Select(a => $"{a.Addon.Name} ({a.Files.Count})")));
        if (addons.Count > 0)
        {
            var (addon, builds) = addons[0];
            var zipAddon = await UltraPlus.DownloadAsync(builds[0], null, CancellationToken.None);
            var a = await UltraPlus.InstallAddonAsync(game, builds[0], zipAddon, layout, state, CancellationToken.None);
            state.Addons[UltraPlus.AddonName(game.Key, builds[0].FileName)] = a;
            Check(a.Files.All(f => File.Exists(Path.Combine(install, f))), $"add-on {addon.Name}: {string.Join(", ", a.Files)}");
        }

        var proxies = UltraPlus.ProxyNames(zipNew);
        Check(proxies.Count > 0, "UE4SS loads through " + string.Join(", ", proxies));
        Check(UltraPlus.ProxyConflict(zipNew, layout, false) is null, "no OptiScaler here: no proxy clash");

        var problems = UltraPlus.Uninstall(state, layout, keepUe4ss: false);
        Check(problems.Count == 0, "uninstalled without problems " + string.Join("; ", problems));
        var after = Directory.GetFiles(install, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
        Check(after.Count == before.Count && before.All(kv => after.TryGetValue(kv.Key, out var t) && t == kv.Value),
            "folder back as it was: " + string.Join(", ", after.Keys.Except(before.Keys).Select(f => Path.GetRelativePath(install, f))));
        Check(!Directory.Exists(layout.Ue4ssDir!), "no ue4ss folder left");

        // Installing again brings the user's settings back, their own console variables included.
        state = await UltraPlus.InstallAsync(game, newest, zipNew, layout, false, null, true, CancellationToken.None);
        Check(UltraPlus.ReadConfig(layout.ConfigDir!).GetValueOrDefault(pick.Key) == newValue, "a new install gets the saved settings back");
        Check(UltraPlus.ReadCustom(layout.ConfigDir!) == "r.Shadow.MaxResolution=4096", "a new install gets CustomSettings.ini back");
        UltraPlus.Uninstall(state, layout, keepUe4ss: false);

        // Ultra+ put in by someone else (the Ultra+ Manager): taken over, its files not mistaken for the game's, removed whole.
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zipOld))
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
                if (UltraPlus.Destination(entry.FullName, layout, false) is { } dest)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, dest, overwrite: true);
                }
        Check(UltraPlus.Inspect(layout, null).Installed, "an install made elsewhere is seen");
        state = await UltraPlus.InstallAsync(game, newest, zipNew, layout, false, null, true, CancellationToken.None);
        Check(!state.Backups.Keys.Any(k => k.Contains(@"\ue4ss\", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)),
            "taking over: Ultra+'s own files are not backed up as the game's (" + string.Join(", ", state.Backups.Keys.Take(4)) + ")");
        UltraPlus.Uninstall(state, layout, keepUe4ss: false);
        Check(!Directory.Exists(layout.Ue4ssDir!) && Directory.GetFiles(layout.ProjectDir!, "*.pak", SearchOption.AllDirectories).Length == 0, "taken over, then uninstalled: no UE4SS or pak left");
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
    case "epic":
    {
        // Epic manifests: a game with an empty MainGameAppName (1.3.0 skipped it as DLC), one naming itself, and a DLC.
        var root = Path.Combine(Path.GetTempPath(), "hl-smoke-epic");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var manifests = Directory.CreateDirectory(Path.Combine(root, "Manifests")).FullName;
        void Item(string file, string app, string main)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, app)).FullName;
            File.WriteAllText(Path.Combine(manifests, file + ".item"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["AppName"] = app, ["MainGameAppName"] = main, ["DisplayName"] = app, ["InstallLocation"] = dir,
                ["LaunchExecutable"] = "game.exe", ["AppCategories"] = new[] { "public", "games", "applications" },
            }));
        }
        Item("a", "EmptyMain", "");
        Item("b", "SelfMain", "SelfMain");
        Item("c", "SomeDlc", "SelfMain");
        var found = EpicScanner.ScanManifests(manifests).Select(g => g.Name).OrderBy(n => n).ToList();
        Console.WriteLine("Epic: " + string.Join(", ", found));
        Directory.Delete(root, true);
        Check(found.SequenceEqual(new[] { "EmptyMain", "SelfMain" }), "games with an empty or their own MainGameAppName found, the DLC left out");
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
        if (Environment.GetEnvironmentVariable("HL_INI_DUMP") is { Length: > 0 } dump)
            File.WriteAllLines(dump, doc.Entries.Select(e => $"{e.Path}	{e.Kind}	{string.Join("|", e.Options)}"));
        Console.WriteLine($"  not in the official file: {doc.Entries.Count(e => !e.Official)}, with a shared note only: {doc.Entries.Count(e => e.Comment.Length == 0 && e.SharedComment.Length > 0)}");
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
    case "cloud-timing":
    {
        // Read-only: how long each cloud of HL_DATA's settings takes to answer for the games of this PC, alone and together.
        var games = (await GameScanner.ScanAsync(SettingsStore.Current)).Take(args.Length > 1 ? int.Parse(args[1]) : 4).ToList();
        foreach (var round in new[] { "first", "again" })
            foreach (var id in CloudSaves.Settings.Providers.Append("both"))
            {
                SettingsStore.Current.Cloud.Providers = id == "both" ? ["onedrive", "mega"] : [id];
                var store = CloudSaves.Store();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                foreach (var g in games)
                {
                    var t = watch.Elapsed;
                    var m = await store!.ManifestAsync(CloudSaves.Key(g), default);
                    Console.WriteLine($"  {round,-5} {store.Name,-16} {g.Name,-34} {(watch.Elapsed - t).TotalSeconds,6:0.00} s  {(m is null ? "none" : m.Hash[..8] + (m.Partial ? " partial" : ""))}");
                }
                Console.WriteLine($"  {round,-5} {store!.Name,-16} total {watch.Elapsed.TotalSeconds:0.00} s");
            }
        // An upload of a save-sized zip (random bytes, so it does not compress) to MEGA, under a test key removed afterwards.
        var zip = Path.Combine(Path.GetTempPath(), "hl-timing.zip");
        File.WriteAllBytes(zip, RandomNumberGenerator.GetBytes(3 << 20));
        var key = "Hybrid Launcher smoke [timing-" + Guid.NewGuid().ToString("N")[..6] + "]";
        var mega = HybridLauncher.Core.Cloud.MegaStore.Instance;
        try
        {
            for (int i = 1; i <= 3; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var old = await mega.ManifestAsync(key, default);
                await mega.PutAsync(key, zip, new CloudManifest { GameId = key, Hash = "T" + i, UpdatedUtc = DateTime.UtcNow, Machine = "smoke" }, old, 2, default);
                Console.WriteLine($"  MEGA upload {i} (3 MB): {watch.Elapsed.TotalSeconds:0.00} s");
            }
        }
        finally
        {
            await mega.DeleteAsync(key, default);
            Check(await mega.ManifestAsync(key, default) is null, "timing key removed from MEGA");
            File.Delete(zip);
        }
        break;
    }

    case "cloud-play":
    {
        // What Play does for real games of HL_DATA's settings (back their saves up first): the check and the sync, timed, with the app's 45 s limit.
        var library = await GameScanner.ScanAsync(SettingsStore.Load());
        Console.WriteLine($"  cloud on: {CloudSaves.On} ({CloudSaves.Settings.Enabled}, {string.Join("+", CloudSaves.Settings.Providers)}, store {CloudSaves.Store()?.Name ?? "none"})");
        foreach (var id in args.Skip(1))
        {
            var g = library.First(x => x.Id == id);
            var settings = SettingsStore.Current.For(g.Id);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var status = await CloudSaves.StatusAsync(g, settings, fresh: true);
            Console.WriteLine($"  {g.Name}: {status.State}{(status.Cloud?.Partial == true ? " (partial)" : "")} in {watch.Elapsed.TotalSeconds:0.00} s");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                watch.Restart();
                Task? sending = null;
                var message = await CloudSaves.BeforeLaunchAsync(g, settings, _ => Task.FromResult(CloudSaves.Choice.Skip), cts.Token, s => sending = s);
                Console.WriteLine($"  {g.Name}: game could start after {watch.Elapsed.TotalSeconds:0.00} s {message}");
                if (sending is not null)
                {
                    await sending;
                    Console.WriteLine($"  {g.Name}: sent in the background, done after {watch.Elapsed.TotalSeconds:0.00} s");
                }
            }
            catch (OperationCanceledException) { Console.WriteLine($"  {g.Name}: TOO LONG after {watch.Elapsed.TotalSeconds:0.00} s"); }
            status = await CloudSaves.StatusAsync(g, settings, fresh: true);
            Console.WriteLine($"  {g.Name}: now {status.State}{(status.Cloud?.Partial == true ? " (partial)" : "")}");
        }
        break;
    }

    case "cloud-stall":
    {
        // MEGA hanging, as on a stalled network: its requests go through a local "proxy" that takes the connection and never
        // answers. OneDrive (the folder of HL_DATA's PC) must work as usual, nothing may wait for MEGA past the patience, and
        // a cloud that did not answer must not count as in sync. A test game is used; only OneDrive's copy needs removing.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var held = new List<System.Net.Sockets.TcpClient>();
        _ = Task.Run(async () => { while (true) held.Add(await listener.AcceptTcpClientAsync()); });
        Environment.SetEnvironmentVariable("HTTPS_PROXY", $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}");
        SettingsStore.Current.Cloud = new CloudSaveSettings { Enabled = true, Providers = ["onedrive", "mega"], KeepVersions = 2 };
        Check(CloudSaves.On, $"clouds ready ({CloudSaves.StoreName})");
        var root = Path.Combine(Path.GetTempPath(), "hl-stall-" + Guid.NewGuid().ToString("N")[..8]);
        var game = new Game { Id = "steam:smoke-stall-" + Guid.NewGuid().ToString("N")[..6], Name = "Hybrid Launcher smoke: Stall", Store = GameStore.Steam, InstallDir = root };
        var s = new GameSettings { CloudSaveDir = Directory.CreateDirectory(Path.Combine(root, "saves")).FullName };
        File.WriteAllText(Path.Combine(s.CloudSaveDir, "slot.sav"), "stall");
        var oneDrive = ((HybridLauncher.Core.Cloud.MirrorStore)CloudSaves.Store()!).Stores[0];
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var status = await CloudSaves.StatusAsync(game, s, fresh: true);
            Console.WriteLine($"  status: {status.State} in {watch.Elapsed.TotalSeconds:0.0} s");
            Check(watch.Elapsed < TimeSpan.FromSeconds(25), "the state comes without waiting for MEGA (under 25 s)");
            watch.Restart();
            Task? later = null;
            using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                await CloudSaves.BeforeLaunchAsync(game, s, _ => Task.FromResult(CloudSaves.Choice.Skip), limit.Token, t => later = t);
            Console.WriteLine($"  game could start after {watch.Elapsed.TotalSeconds:0.0} s");
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Play does not wait for MEGA again once it was found silent (under 5 s)");
            Check(later is not null, "the upload goes on in the background");
            try { if (later is not null) await later; Check(false, "an upload MEGA missed is reported"); }
            catch (HybridLauncher.Core.Cloud.CloudPartialException ex)
            {
                Console.WriteLine($"  reported: {ex.Message}");
                Check(ex.Sent.SequenceEqual([oneDrive.Name]) && ex.Missed.Count == 1, $"reported as in {string.Join(", ", ex.Sent)}, not in {string.Join(", ", ex.Missed.Select(m => m.Name))}");
                Check(Troubleshoot.Cause(ex.Missed[0].Error) is { } cause && cause.Contains("internet"), "the error comes with its likely cause");
            }
            Console.WriteLine($"  upload done after {watch.Elapsed.TotalSeconds:0.0} s");
            Check(await oneDrive.ManifestAsync(CloudSaves.Key(game), default) is { } od && od.Hash == CloudSaves.Fingerprint(s.CloudSaveDir).Hash, "OneDrive got the saves");
            status = await CloudSaves.StatusAsync(game, s, fresh: true);
            Check(status.State != CloudState.Synced, $"not \"Synced\" while MEGA does not answer ({status.State})");
        }
        catch (OperationCanceledException ex) { Check(false, $"a MEGA stall surfaced as a cancellation: {ex.Message}"); }
        finally
        {
            await oneDrive.DeleteAsync(CloudSaves.Key(game), default);
            Check(await oneDrive.ManifestAsync(CloudSaves.Key(game), default) is null, "test game removed from OneDrive");
            try { Directory.Delete(root, true); Directory.Delete(CloudSaves.LocalBackupsDir(game), true); } catch { }
            listener.Stop();
        }
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
        // "cloud-live all [folder]": OneDrive, MEGA and a folder at once (by default Google Drive's drive, as a person picks it).
        var gdrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && Directory.Exists(Path.Combine(d.RootDirectory.FullName, ".shortcut-targets-by-id")))?.RootDirectory.FullName;
        SettingsStore.Current.Cloud = provider == "all"
            ? new CloudSaveSettings { Enabled = true, Providers = ["onedrive", "mega", "folder"], Folder = args.ElementAtOrDefault(2) ?? gdrive ?? cloudDir, KeepVersions = 2 }
            : new CloudSaveSettings { Enabled = true, Providers = [provider], Folder = cloudDir, KeepVersions = 2 };
        Check(CloudSaves.On, $"provider {provider} ready ({CloudSaves.StoreName})");
        // With several clouds: each one, asked alone, holds the copy the sync sees.
        async Task EveryCloud(Game g, string what)
        {
            if (CloudSaves.Store() is not HybridLauncher.Core.Cloud.MirrorStore mirror) return;
            var key = CloudSaves.Key(g);
            var hashes = new List<string>();
            foreach (var s in mirror.Stores) hashes.Add((await s.ManifestAsync(key, default))?.Hash ?? "none");
            var seen = (await CloudSaves.ReadManifestAsync(g, fresh: true))?.Hash ?? "none";
            Check(hashes.All(h => h == seen), $"{what}: {string.Join(", ", mirror.Stores.Select((s, i) => $"{s.Name} {hashes[i][..Math.Min(8, hashes[i].Length)]}"))}");
        }
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
            await EveryCloud(game, "A's first upload is in every cloud");
            Check((await CloudSaves.StatusAsync(game, a, fresh: true)).State == CloudState.Synced, "A synced");
            Check((await CloudSaves.StatusAsync(game, b, fresh: true)).State == CloudState.ChangedInCloud, "B (empty) sees newer in cloud");
            await CloudSaves.BeforeLaunchAsync(game, b, keepHere);
            Check(File.ReadAllText(Path.Combine(savesB, "slot1.sav")) == "A1" && File.Exists(Path.Combine(savesB, "sub", "profile.dat")), "B restored before launch");
            File.WriteAllText(Path.Combine(savesB, "slot1.sav"), "B2");
            await CloudSaves.AfterSessionAsync(game, b);
            await EveryCloud(game, "B's upload replaced it in every cloud");
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
            await EveryCloud(game, "the conflict's choice is in every cloud");
            var history = await CloudSaves.HistoryAsync(game);
            Check(history.Count == 2, $"history trimmed to KeepVersions (2): {history.Count}");
            await CloudSaves.RestoreAsync(game, a, history.Last());
            Check((await CloudSaves.StatusAsync(game, a, fresh: true)).State == CloudState.ChangedHere, "restored history copy goes up next");
            Check(CloudSaves.TooBroad(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) && CloudSaves.TooBroad(@"C:\") && !CloudSaves.TooBroad(savesA), "broad folders refused");

            // Before Play an upload only zips the saves: the sending is handed back to go on while the game runs, it carries the
            // saves as they were (the game changes them meanwhile), and the next upload waits for it.
            var laterGame = new Game { Id = game.Id + "-later", Name = "Hybrid Launcher smoke: Later", Store = GameStore.Steam, InstallDir = root };
            var l = new GameSettings { CloudSaveDir = Directory.CreateDirectory(Path.Combine(root, "pcA", "later")).FullName };
            try
            {
                File.WriteAllText(Path.Combine(l.CloudSaveDir, "slot.sav"), "before play");
                var before = CloudSaves.Fingerprint(l.CloudSaveDir).Hash;
                Task? later = null;
                using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                    await CloudSaves.BeforeLaunchAsync(laterGame, l, keepHere, limit.Token, s => later = s);
                Check(later is not null, "before launch: the upload goes on in the background");
                File.WriteAllText(Path.Combine(l.CloudSaveDir, "slot.sav"), "while playing");
                var after = CloudSaves.AfterSessionAsync(laterGame, l);
                await later!;
                Check(await after, "the upload after the session waited for it and went up too");
                var hist = await CloudSaves.HistoryAsync(laterGame);
                Check(hist.Count == 1, $"the background copy is the history's ({hist.Count})");
                await EveryCloud(laterGame, "Play's background upload and the session's are in every cloud");
                var lb = new GameSettings { CloudSaveDir = Path.Combine(root, "pcB", "later") };
                await CloudSaves.RestoreAsync(laterGame, lb, hist[0]);
                Check(CloudSaves.Fingerprint(lb.CloudSaveDir).Hash == before, "the background copy holds the saves from before Play");
            }
            finally { await CloudSaves.DeleteFromCloudAsync(laterGame); try { Directory.Delete(CloudSaves.LocalBackupsDir(laterGame), true); } catch { } }

            // One saves folder per account (Spider-Man 2: Documents\Marvel's Spider-Man 2\<steam id>): the wiki's wildcard picks
            // the one the game wrote in last, not the first by name.
            var accounts = Directory.CreateDirectory(Path.Combine(root, "Documents", "Some Game")).FullName;
            foreach (var (id, age) in new[] { ("76561197960271872", 300), ("76561198833423527", 0), ("76561199685111768", 30) })
            {
                var f = Path.Combine(Directory.CreateDirectory(Path.Combine(accounts, id)).FullName, "slot1.save");
                File.WriteAllText(f, id);
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-age));
            }
            var picked = HybridLauncher.Core.Media.Pcgw.ExistingFolder([new HybridLauncher.Core.Media.PcgwPath("Steam", Path.Combine(accounts, "*"))], GameStore.Steam);
            Check(Path.GetFileName(picked) == "76561198833423527", $"several account folders: the one in use is synced ({Path.GetFileName(picked)})");

            // Google Drive for desktop's G:\ takes no files at its root: its "My Drive" folder is used; any other folder as it is.
            Check(HybridLauncher.Core.Cloud.FolderStore.Resolve(savesA) == savesA, "a plain folder is used as picked");
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && Directory.Exists(Path.Combine(d.RootDirectory.FullName, ".shortcut-targets-by-id"))))
            {
                var mine = HybridLauncher.Core.Cloud.FolderStore.Resolve(drive.RootDirectory.FullName);
                Check(mine != drive.RootDirectory.FullName && HybridLauncher.Core.Cloud.FolderStore.Writable(mine), $"Google Drive {drive.Name} goes to {Path.GetFileName(mine)}");
            }
            var cancelled = new GameSettings { CloudSaveDir = Path.Combine(root, "pcC", "saves") };
            try { await CloudSaves.BeforeLaunchAsync(game, cancelled, keepHere, new CancellationToken(true)); Check(false, "cancelled restore throws"); }
            catch (OperationCanceledException) { Check(!Directory.Exists(cancelled.CloudSaveDir), "cancelled restore touched nothing"); }

            // Saves, the game's settings folder and the Steam emulator's achievements go up together and come back each to its place.
            var emuGame = new Game { Id = game.Id + "-emu", Name = "Hybrid Launcher smoke: Emulated", Store = GameStore.Custom, InstallDir = Path.Combine(root, "install"), EmulatorAppId = "999999901" };
            // The emulator's %APPDATA% folder in the test's own scratch folder, never the real one (2.1-rc.10 audit).
            HybridLauncher.Core.Steam.SteamEmulator.AppData = Directory.CreateDirectory(Path.Combine(root, "appdata")).FullName;
            var gseRoot = Path.Combine(HybridLauncher.Core.Steam.SteamEmulator.AppData, "GSE Saves");
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
            // A cloud that fails (here a folder that cannot exist, under a file): the others get the saves, the failure is reported.
            File.WriteAllText(Path.Combine(root, "file"), "");
            var broken = new HybridLauncher.Core.Cloud.MirrorStore([a, new HybridLauncher.Core.Cloud.FolderStore(Path.Combine(root, "file", "x"), "Broken")]);
            try { await broken.PutAsync(key, Zip("four"), M("h4", 9), null, 3, CancellationToken.None); Check(false, "a failing cloud is reported"); }
            catch (HybridLauncher.Core.Cloud.CloudPartialException ex)
            {
                Check(ex.Sent.SequenceEqual(["A"]) && ex.Missed.Single().Name == "Broken", $"partial upload: in {string.Join(", ", ex.Sent)}, not in {string.Join(", ", ex.Missed.Select(m => m.Name))}");
                Check((await a.ManifestAsync(key, default))?.Hash == "h4", "the working cloud has the saves");
            }
            await a.DeleteAsync(key, default);
            // Every cloud failing is a plain error, not a partial one.
            var dead = new HybridLauncher.Core.Cloud.MirrorStore([new HybridLauncher.Core.Cloud.FolderStore(Path.Combine(root, "file", "y"), "Dead")]);
            try { await dead.PutAsync(key, Zip("five"), M("h5", 10), null, 3, CancellationToken.None); Check(false, "a dead cloud throws"); }
            catch (Exception ex) { Check(ex is not HybridLauncher.Core.Cloud.CloudPartialException, $"no cloud reached: a plain error ({ex.GetType().Name})"); }
            // The likely causes under error messages.
            Check(Troubleshoot.Cause(new TimeoutException()) is { } t && t.Contains("internet"), "a timeout points at the connection");
            Check(Troubleshoot.Cause(new IOException("x", unchecked((int)0x80070020))) is { } u && u.Contains("using the file"), "a sharing violation points at a program holding the file");
            Check(Troubleshoot.Cause("Access to the path 'saves' is denied.") is { } d && d.Contains("in use or protected"), "access denied, from the text alone");
            Check(Troubleshoot.Link("Cloud saves", "x").EndsWith("#cloud-saves") && Troubleshoot.Link("The game did not start", "x").EndsWith("#a-game-does-not-start"), "errors link to their troubleshooting section");
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
        // dlssnr: a DLSS NR build (its name says so) gets the stored model file; one already in the folder is backed up.
        bool dlssnr = args.Contains("dlssnr");

        ArchiveExtractor.ResetDir(dir);
        File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(dir, "FakeGame.exe"));
        File.WriteAllText(Path.Combine(dir, "dxgi.dll"), "not optiscaler - pretend this is ReShade");
        File.WriteAllText(Path.Combine(dir, "nvngx.dll"), "pre-0.9 leftover");
        if (dlssnr) File.WriteAllText(Path.Combine(dir, DlssNr.FileName), "the user's own older model file");
        // useropti (2.0.0-rc.17): an OptiScaler the user put in by hand, as dxgi.dll and a second copy as winmm.dll. Uninstalling
        // Hybrid Launcher's must bring both back (it deleted the user's dxgi.dll before).
        if (args.Contains("useropti"))
        {
            var stable = (await OptiReleases.GetAsync(OptiChannel.Stable))[0];
            var own = Path.Combine(Path.GetTempPath(), "hl-useropti-src");
            ArchiveExtractor.ResetDir(own);
            await ArchiveExtractor.ExtractAsync(await OptiReleases.DownloadAsync(stable, null, CancellationToken.None), own, CancellationToken.None);
            var ownDll = Directory.GetFiles(own, "OptiScaler.dll", SearchOption.AllDirectories)[0];
            File.Copy(ownDll, Path.Combine(dir, "dxgi.dll"), true);
            File.Copy(ownDll, Path.Combine(dir, "winmm.dll"), true);
        }
        var before = Snapshot(dir);
        var nrFile = Path.Combine(Path.GetTempPath(), "hl-fake-" + DlssNr.FileName);
        if (dlssnr) File.WriteAllText(nrFile, "stand-in for NVIDIA's DLSS NR runtime");

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
            var zip = Path.Combine(Path.GetTempPath(), dlssnr ? "Discord DLSSNR test build.zip" : "Discord test build.zip");
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
            DlssNrDll = dlssnr ? nrFile : null,
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
        if (dlssnr)
        {
            Check(File.ReadAllText(Path.Combine(dir, DlssNr.FileName)).StartsWith("stand-in"), "stored DLSS NR model file put in");
            Check(m.Files.ContainsKey(DlssNr.FileName) && m.Backups.ContainsKey(DlssNr.FileName), "model file recorded, the old one backed up");
            // The game's own switch: off takes it out and puts the game's file back, on puts it in again.
            Check(DlssNr.Sync(m, null) && File.ReadAllText(Path.Combine(dir, DlssNr.FileName)).StartsWith("the user's own"), "switched off: game's own file back");
            Check(DlssNr.NeededBy(m) && DlssNr.Sync(m, nrFile) && File.ReadAllText(Path.Combine(dir, DlssNr.FileName)).StartsWith("stand-in"), "switched on again: stored file back in");
            // On globally / On for this game: the game follows the global switch until it has its own choice.
            var app = new AppSettings { UseDlssNr = true };
            var g = new GameSettings();
            Check(DlssNr.OnFor(g, app), "a game follows On globally");
            app.UseDlssNr = false;
            Check(!DlssNr.OnFor(g, app), "and is off with it");
            g.DlssNrChoice = true;
            Check(DlssNr.OnFor(g, app), "this game on while global is off");
            var old = System.Text.Json.JsonSerializer.Deserialize("{\"DlssNr\":false}", HybridJson.Default.GameSettings)!;
            app.UseDlssNr = true;
            Check(old.DlssNrChoice == false && !DlssNr.OnFor(old, app), "an rc.4 game switched off stays off");
            var oldOn = System.Text.Json.JsonSerializer.Deserialize("{\"DlssNr\":true}", HybridJson.Default.GameSettings)!;
            Check(oldOn.DlssNrChoice is null, "an rc.4 game left on follows the global switch");
            Check(!System.Text.Json.JsonSerializer.Serialize(oldOn, HybridJson.Default.GameSettings).Contains("\"DlssNr\""), "the old key is not written back");
        }
        else Check(!m.Files.ContainsKey(DlssNr.FileName), "no DLSS NR model file for a build that doesn't need it");

        // Persist and reload the manifest like the app does.
        var json = System.Text.Json.JsonSerializer.Serialize(m, HybridJson.Default.InstallManifest);
        m = System.Text.Json.JsonSerializer.Deserialize(json, HybridJson.Default.InstallManifest)!;
        m.Files = new(m.Files, StringComparer.OrdinalIgnoreCase);

        // update (2.0.0-rc.18): the same build put in again over the first install (Previous set), as Update does. The user's own
        // OptiScaler, put back by the update's uninstall and backed up again, must still be the user's afterwards.
        if (args.Contains("update"))
        {
            m = await OptiInstaller.InstallAsync(new InstallRequest
            {
                GameId = "test:fake",
                TargetDir = dir,
                Release = release,
                Options = options,
                Previous = m,
                IniOverrides = new Dictionary<string, string> { ["Upscalers/Dx12Upscaler"] = "fsr31" },
                AsiPlugins = ["TestPlugin.asi"],
                DlssNrDll = dlssnr ? nrFile : null,
            }, progress, CancellationToken.None);
            await Task.Delay(300);
            Check(OptiDetector.IsOptiScaler(Path.Combine(dir, "dxgi.dll")), "update: dxgi.dll is OptiScaler");
            if (args.Contains("useropti")) Check(m.UserOptiBackups.Count > 0, "update: the user's own OptiScaler still recorded as theirs");
            json = System.Text.Json.JsonSerializer.Serialize(m, HybridJson.Default.InstallManifest);
            m = System.Text.Json.JsonSerializer.Deserialize(json, HybridJson.Default.InstallManifest)!;
            m.Files = new(m.Files, StringComparer.OrdinalIgnoreCase);
        }

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

// A ReShade dll for the tests, taken from the installers Hybrid Launcher saved (the normal one or the one with full add-on support).
static string? ReShadeSample(bool addonBuild = true)
{
    try
    {
        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HybridLauncher", "ReShade");
        var installer = Directory.Exists(saved) ? Directory.GetFiles(saved, "ReShade_Setup_*.exe").Where(f => f.EndsWith("_Addon.exe", StringComparison.OrdinalIgnoreCase) == addonBuild).OrderDescending().FirstOrDefault() : null;
        if (installer is null) return null;
        var outDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hl-reshade-sample", addonBuild ? "addon" : "normal")).FullName;
        var dll = Path.Combine(outDir, "ReShade64.dll");
        if (!File.Exists(dll))
        {
            // The installer is an exe with the zip appended: where the zip starts is where its central directory says it is,
            // less the central directory's own offset (the end record holds both).
            var bytes = File.ReadAllBytes(installer);
            int end = bytes.AsSpan().LastIndexOf("PK"u8);
            uint cdSize = BitConverter.ToUInt32(bytes, end + 12), cdOffset = BitConverter.ToUInt32(bytes, end + 16);
            int start = (int)(end - cdSize - cdOffset);
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes, start, bytes.Length - start));
            System.IO.Compression.ZipFileExtensions.ExtractToFile(zip.GetEntry("ReShade64.dll")!, dll, true);
        }
        return dll;
    }
    catch (Exception ex) { Console.WriteLine($"    (ReShade sample: {ex.Message})"); return null; }
}

static Dictionary<string, string> Snapshot(string dir) =>
    Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(dir, p), p => File.Exists(p) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) : "<dir>",
            StringComparer.OrdinalIgnoreCase);
