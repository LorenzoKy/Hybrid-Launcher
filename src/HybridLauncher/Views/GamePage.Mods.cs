using HybridLauncher.Core;
using HybridLauncher.Core.Mods;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The Mods tab's first section (1.4.0-rc.18): every mod in the game folder in one compact table (its version, how it is loaded,
/// who put it there, whether it works), each with a way to its section and to uninstall it; a row opens to list its files. Under
/// the table the mod check (conflicts, things a game update undid) with its fixes, and the files beside the exe that are no mod
/// (the game's own upscalers, dlls nobody knows). Replaces the banner's "What is installed as what" and mod check windows.
/// </summary>
public partial class GamePage
{
    /// <summary>One mod of the table: what it is, what it shows, and what its buttons do.</summary>
    sealed record ModRow(string Key, string Name, Color Tint, string Section, string Version, string Loaded, bool ByHybrid,
        List<ModFile> Files, List<ModProblem> Problems, Func<Task>? Uninstall, string? NoUninstall);

    /// <summary>What a read of the folder found (off the UI thread), drawn by <see cref="DrawModsOverview"/>.</summary>
    sealed record ModsRead(string Dir, List<ModFile> Files, List<ModProblem> Problems, DetectedOpti? Opti, DetectedReShade? ReShade,
        List<string> RenoDx, List<string> Luma, List<DetectedLayer> Layers);

    int _modsOverviewRun;
    readonly HashSet<string> _modsOpen = new(StringComparer.OrdinalIgnoreCase);
    ModsRead? _modsRead;
    DateTime _modsChecked;

    FileSystemWatcher? _modsWatcher;
    DispatcherTimer? _modsWatchDelay;
    static readonly HashSet<string> WatchedKinds = new(StringComparer.OrdinalIgnoreCase) { ".dll", ".asi", ".ini", ".addon", ".addon32", ".addon64", "" };

    /// <summary>
    /// Watches the game folder while its page is open: whatever installs, updates or removes a mod there (this page, a mod's
    /// own installer, the user in Explorer) redraws the banner's tags, the Mods table and the Dashboard's Mods card within a second.
    /// </summary>
    void WatchModsFolder(string? dir)
    {
        if (dir is not null && _modsWatcher?.Path.Equals(dir, StringComparison.OrdinalIgnoreCase) == true) return;
        StopWatchingModsFolder();
        if (dir is null || !Directory.Exists(dir)) return;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            void Changed(string path)
            {
                // Logs and dumps games and mods write while they run are not a change of mods.
                if (!WatchedKinds.Contains(Path.GetExtension(path))) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_modsWatcher != w) return;
                    _modsWatchDelay ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                    _modsWatchDelay.Stop();
                    _modsWatchDelay.Tick -= OnModsFolderSettled;
                    _modsWatchDelay.Tick += OnModsFolderSettled;
                    _modsWatchDelay.Start();
                });
            }
            w.Created += (_, e) => Changed(e.FullPath);
            w.Deleted += (_, e) => Changed(e.FullPath);
            w.Changed += (_, e) => Changed(e.FullPath);
            w.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
            w.Error += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_modsWatcher == w) OnModsFolderSettled(null, null); });
            w.EnableRaisingEvents = true;
            _modsWatcher = w;
        }
        catch (Exception ex) { Log.Warn($"Mods: cannot watch {dir}: {ex.Message}"); }
    }

    void OnModsFolderSettled(object? sender, object? e)
    {
        _modsWatchDelay?.Stop();
        if (_game is null) return;
        // The banner's tags and the mod check, which also redraws the table.
        _ = UpdateModBadgesAsync();
        // OptiScaler's status and the DLSS 5 steps name the build from the dll there: a dll dropped over the install shows at
        // once instead of the build the install record names (1.4.0-rc.20).
        if (!_busy)
        {
            try { UpdateOptiStatus(); RefreshDlss5(); }
            catch (Exception ex) { Log.Warn($"Mods: OptiScaler status after a folder change: {ex.Message}"); }
        }
        // The Auto tab's plan: what is in now (2.0.0-rc.10; it was read only when the tab was shown).
        _autoView?.Refresh();
    }

    /// <summary>
    /// The Auto tab's bin (2.0.0-rc.10): the same uninstall as the Mods table's line for that mod, with its own question. The
    /// Mods tab's sections are made first when it was never opened (their uninstall lives there). False with the reason when
    /// Hybrid Launcher cannot take it out (not installed by it).
    /// </summary>
    public async Task<(bool Done, string? Why)> AutoUninstallAsync(string id)
    {
        if (_game is not { } game || TargetDir is not { } dir) return (false, null);
        if (GameJobs.InTheWay(game.Id, game.Title)) return (false, null);
        MakeModSections(game);
        var read = await ReadModsAsync(dir);
        if (_game != game) return (false, null);
        var row = ModRows(read).FirstOrDefault(r => r.Key.Equals(id, StringComparison.OrdinalIgnoreCase) || (id == "dxvk" && r.Key == "vkd3d"));
        if (row is null) return (false, L.T("It is not in the game folder any more."));
        if (row.Uninstall is null) return (false, row.NoUninstall ?? L.T("Uninstall it from its section"));
        await row.Uninstall();
        _ = UpdateModBadgesAsync();
        _ = RefreshModsOverviewAsync();
        return (true, null);
    }

    /// <summary>The Mods tab's sections, made when the tab was never opened: each mod's uninstall lives in its section.</summary>
    void MakeModSections(Game game)
    {
        if (game.NotInstalled) return;
        if (_reshadeView is null) ReShadeHost.Child = _reshadeView = new ReShadeView(ModContext());
        if (_renoDxView is null) { RenoDxHost.Child = _renoDxView = new RenoDxView(ModContext()); Ui.Safe(_renoDxView.Refresh); }
        if (_lumaView is null) { LumaHost.Child = _lumaView = new LumaView(ModContext()); Ui.Safe(_lumaView.Refresh); }
        if (_dxvkView is null) { DxvkHost.Child = _dxvkView = new DxvkView(ModContext()); Ui.Safe(_dxvkView.Refresh); }
        if (_vkd3dView is null) { Vkd3dHost.Child = _vkd3dView = new DxvkView(ModContext(), vkd3d: true); Ui.Safe(_vkd3dView.Refresh); }
    }

    Task<ModsRead> ReadModsAsync(string dir)
    {
        var settings = _settings;
        return Task.Run(() => new ModsRead(dir, ModFiles.Map(settings, dir), ModCheck.Run(settings, dir), OptiDetector.Detect(dir),
            Core.Mods.ReShade.Detect(dir), RenoDx.Installed(dir), Core.Mods.Luma.Installed(dir), Core.Mods.Dxvk.Detect(dir)));
    }

    /// <summary>The order mods come out in: what loads through another mod first (plugins, add-ons), OptiScaler last.</summary>
    static readonly string[] RemovalOrder = ["dlss5", "renodx", "luma", "reshade", "ultra", "dxvk", "vkd3d", "opti"];

    /// <summary>
    /// "Uninstall all" (2.1-rc.6, the Auto tab's bar and the Mods table's heading): every mod Hybrid Launcher installed and
    /// manages in this game comes out, after one question, each as its own section's uninstall does (backed-up files back).
    /// Mods it did not install (put in by hand or by another tool) are left as they are, and the question says so.
    /// </summary>
    // Uninstall all is running (2.1 audit): a second click (the table's button and the Auto tab's) opens no second dialog.
    bool _uninstallingAll;

    public async Task UninstallAllModsAsync()
    {
        if (_uninstallingAll) return;
        if (_busy || _dlss5Busy is not null) { App.Window?.ShowInfo(L.T("Wait for the mod being installed or removed to finish.")); return; }
        _uninstallingAll = true;
        try { await UninstallAllCoreAsync(); }
        catch (Exception ex) { Log.Warn($"Uninstall all: {ex.Message}"); App.Window?.ShowError(L.T("Uninstall all"), ex.Message, ex); }
        finally { _uninstallingAll = false; }
    }

    async Task UninstallAllCoreAsync()
    {
        if (_game is not { } game || TargetDir is not { } dir) return;
        if (GameJobs.InTheWay(game.Id, game.Title)) return;
        if (GameSessions.IsActive(game.Id)) { App.Window?.ShowInfo(L.F("Close {0} first: its files are in use.", game.Title)); return; }
        MakeModSections(game);
        var read = await ReadModsAsync(dir);
        if (_game != game) return;
        var all = ModRows(read);
        var mine = all.Where(r => r.ByHybrid && r.Uninstall is not null)
            .OrderBy(r => Array.IndexOf(RemovalOrder, r.Key) is var i and >= 0 ? i : RemovalOrder.Length).ToList();
        var others = all.Where(r => !mine.Contains(r)).Select(r => r.Name).Distinct().ToList();
        if (mine.Count == 0)
        {
            App.Window?.ShowInfo(others.Count > 0
                ? L.T("No mod here was installed by Hybrid Launcher: the ones in the folder were put in another way, so they stay.")
                : L.T("No mods in this game: it runs as it shipped."));
            return;
        }
        var names = string.Join(", ", mine.Select(r => r.Name).Distinct());
        var message = L.F("Takes out {0} and puts back every game file they replaced.", names)
            + (others.Count > 0 ? "\n\n" + L.F("Left as they are (not installed by Hybrid Launcher): {0}.", string.Join(", ", others)) : "");
        if (!await ConfirmAsync(mine.Count == 1 ? L.T("Uninstall 1 mod?") : L.F("Uninstall {0} mods?", mine.Count), message, L.T("Uninstall all"))) return;

        var failed = new List<string>();
        global::HybridLauncher.Views.ModContext.UninstallingAll = true;
        try
        {
            foreach (var row in mine)
            {
                if (_game != game) break;
                try
                {
                    Log.Info($"Uninstall all in {game.Name}: {row.Name}");
                    await row.Uninstall!();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Uninstall all, {row.Name}: {ex.Message}");
                    failed.Add(row.Name);
                }
            }
        }
        finally { global::HybridLauncher.Views.ModContext.UninstallingAll = false; }
        if (_game != game) return;
        // What is still there: a mod whose own uninstall stopped (busy, a file in use) is named.
        var after = ModRows(await ReadModsAsync(dir)).Where(r => r.ByHybrid && r.Uninstall is not null).Select(r => r.Name).ToList();
        foreach (var n in after) if (!failed.Contains(n)) failed.Add(n);
        _ = UpdateModBadgesAsync();
        _ = RefreshModsOverviewAsync();
        _autoView?.Refresh();
        Refresh();
        if (failed.Count == 0) App.Window?.ShowSuccess(L.T("Every mod Hybrid Launcher installed is out, and the game's files are back."));
        else App.Window?.ShowError(L.T("Uninstall all"), L.F("Still in the game: {0}. Uninstall it from its section.", string.Join(", ", failed)));
    }

    void StopWatchingModsFolder()
    {
        _modsWatchDelay?.Stop();
        if (_modsWatcher is { } w)
        {
            _modsWatcher = null;
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
    }

    void ResetModsOverview()
    {
        StopWatchingModsFolder();
        _modsOverviewRun++;
        _modsRead = null;
        _modsOpen.Clear();
        ModsOverviewHost.Child = null;
    }

    /// <summary>Reads the folder again and redraws the table (when the Mods tab is open). <paramref name="told"/>: says what the check found.</summary>
    async Task RefreshModsOverviewAsync(bool told = false)
    {
        if (_game is not { } game || _tab != "mods") return;
        int run = ++_modsOverviewRun;
        var dir = TargetDir;
        var settings = _settings;
        if (game.NotInstalled || dir is null)
        {
            StopWatchingModsFolder();
            ModsOverviewHost.Child = Ui.Card(ModsHeading(null), 20);
            return;
        }
        WatchModsFolder(dir);
        if (ModsOverviewHost.Child is null) ModsOverviewHost.Child = Ui.Card(ModsHeading(null, reading: true), 20);
        ModsRead read;
        try
        {
            read = await Task.Run(() => new ModsRead(dir, ModFiles.Map(settings, dir), ModCheck.Run(settings, dir), OptiDetector.Detect(dir),
                Core.Mods.ReShade.Detect(dir), RenoDx.Installed(dir), Core.Mods.Luma.Installed(dir), Core.Mods.Dxvk.Detect(dir)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Mods overview: {ex.Message}");
            return;
        }
        // Another game, or a newer read started meanwhile: this one is stale.
        if (run != _modsOverviewRun || _game != game) return;
        _modsRead = read;
        _modsChecked = DateTime.Now;
        _lastModFiles = read.Files;
        DrawModsOverview();
        if (told)
        {
            var open = read.Problems.Where(p => !settings.DismissedModProblems.Contains(p.Id)).ToList();
            if (open.Count == 0) App.Window?.ShowSuccess(L.T("No conflicts: every mod in this folder can load."));
            else App.Window?.ShowInfo(open.Count == 1 ? L.T("1 problem found: see the list under the table.") : L.F("{0} problems found: see the list under the table.", open.Count));
        }
    }

    // ---------- the rows ----------

    List<ModRow> ModRows(ModsRead r)
    {
        var s = _settings;
        var rows = new List<ModRow>();
        List<ModFile> FilesOf(Func<ModFile, bool> pick) => r.Files.Where(pick).ToList();
        List<ModProblem> ProblemsOf(params string[] sections) => r.Problems
            .Where(p => !s.DismissedModProblems.Contains(p.Id) && p.Tab is { } t && sections.Any(x => t.StartsWith(x, StringComparison.OrdinalIgnoreCase))).ToList();
        static bool In(ModFile f, string section) => f.Tab?.StartsWith(section, StringComparison.OrdinalIgnoreCase) == true;

        if (r.Opti is { } opti)
        {
            var m = s.Install;
            bool mine = m is not null && !OptiDetector.ReplacedSince(m);
            string version = mine && TagNumber(m!) is { } tag ? "v" + tag : CustomBuilds.Brief(opti.Version);
            if (mine && m!.Channel == OptiChannel.Fork) version += "  ·  " + OptiUpdates.Name(OptiUpdates.Key(OptiChannel.Fork, m.Source), AppState.Settings);
            else if (mine && m!.Channel == OptiChannel.Nightly && !CustomBuilds.IsDev(opti.Version)) version += "  ·  " + L.T("nightly");
            rows.Add(new ModRow("opti", "OptiScaler", Color.FromArgb(0xFF, 0xB3, 0x8B, 0xFF), "opti/install", version,
                L.F("as {0}, loaded by Windows", opti.FileName), m is not null,
                FilesOf(f => In(f, "opti/")), ProblemsOf("opti/"),
                m is null ? null : UninstallOptiAsync,
                L.T("Not installed by Hybrid Launcher: its files have no record to be taken out by. Install OptiScaler here to manage it.")));
        }
        if (r.ReShade is { } rs)
        {
            var main = r.Files.FirstOrDefault(f => f.File.Equals(rs.FileName, StringComparison.OrdinalIgnoreCase));
            rows.Add(new ModRow("reshade", main?.What ?? "ReShade", Color.FromArgb(0xFF, 0xFF, 0xA0, 0x4D), "mods/reshade", rs.Version,
                rs.LoadedByOpti ? main?.How ?? L.T("OptiScaler loads it") : L.F("as {0}, loaded by Windows", rs.FileName),
                main?.ByHybrid == true || s.ReShade.RenamedFrom is not null,
                FilesOf(f => In(f, "mods/reshade")), ProblemsOf("mods/reshade"),
                _reshadeView is { } view ? view.RemoveAsync : null, null));
        }
        if (r.RenoDx.Count > 0)
        {
            var files = FilesOf(f => In(f, "mods/renodx"));
            // One add-on: its version; several: which they are.
            rows.Add(new ModRow("renodx", "RenoDX", Color.FromArgb(0xFF, 0x6C, 0xB8, 0xFF), "mods/renodx",
                files.Count == 1 && files[0].Version.Length > 0 ? files[0].Version : string.Join(", ", r.RenoDx.Select(Path.GetFileNameWithoutExtension)),
                files.FirstOrDefault()?.How ?? L.T("ReShade loads it"), r.RenoDx.Any(f => s.RenoDxFiles.ContainsKey(Path.GetFileName(f))),
                files, ProblemsOf("mods/renodx"), _renoDxView is { } view ? view.RemoveAllAsync : null, null));
        }
        if (s.Luma is { } luma || r.Luma.Count > 0)
        {
            var files = FilesOf(f => In(f, "mods/luma"));
            var state = s.Luma;
            rows.Add(new ModRow("luma", state?.Mod is { Length: > 0 } mod ? mod : "Luma", Color.FromArgb(0xFF, 0xFF, 0x6B, 0x8A), "mods/luma",
                state?.Release ?? "", files.FirstOrDefault()?.How ?? L.T("ReShade loads it"), state is not null,
                files, ProblemsOf("mods/luma"),
                state is not null && _lumaView is { } view ? view.UninstallAsync : null,
                L.T("Not installed by Hybrid Launcher: remove its files from the game folder by hand.")));
        }
        if (s.Install is { } im && (im.DlssNrAmd is not null || DlssNr.NeededBy(im)))
        {
            var (tier, _) = Dlss5.Detect();
            bool amd = im.DlssNrAmd is not null;
            rows.Add(new ModRow("dlss5", amd ? "DLSSNR-AMD" : "DLSS 5 (F5 Multipass)", Color.FromArgb(0xFF, 0x76, 0xB9, 0x00), "mods/dlss5",
                amd ? im.DlssNrAmd!.Tag : im.DisplayVersion,
                amd ? L.T("OptiScaler loads it (plugin)") : MethodName(Dlss5.F5, tier), true,
                [], ProblemsOf("mods/dlss5"),
                amd ? TakeOutAmdAsync : null,
                L.T("F5 Multipass is OptiScaler itself (a fork): uninstall OptiScaler, or install the official build, to take it out.")));
        }
        if (s.DlssNrAmdReShade is { } rsi)
            rows.Add(new ModRow("dlss5", "DLSSNR-AMD", Color.FromArgb(0xFF, 0x76, 0xB9, 0x00), "mods/dlss5", rsi.Tag,
                L.T("ReShade loads it (add-on)"), true, [], ProblemsOf("mods/dlss5"), TakeOutAmdReShadeAsync,
                L.T("Take it out from Mods → DLSS 5.")));
        bool vk = r.Layers.Any(l => l.Layer == "VKD3D-Proton");
        foreach (bool vkd3d in new[] { false, true })
        {
            var state = s.Dxvk is { } d && (d.Flavor == DxvkFlavor.Vkd3d) == vkd3d ? d : null;
            var section = Core.Mods.Dxvk.Section(vkd3d);
            var layers = vkd3d ? (vk ? r.Layers : []) : r.Layers.Where(l => l.Layer == "DXVK" && !(vk && l.File == "dxgi.dll")).ToList();
            if (state is null && layers.Count == 0) continue;
            string name = state is not null ? Core.Mods.Dxvk.Name(state.Flavor) : vkd3d ? "VKD3D-Proton" : "DXVK";
            rows.Add(new ModRow(vkd3d ? "vkd3d" : "dxvk", name, vkd3d ? Color.FromArgb(0xFF, 0xB8, 0x4A, 0xE8) : Color.FromArgb(0xFF, 0xE8, 0x4A, 0x5F), section,
                state is not null ? state.Version + "  ·  " + Core.Mods.Dxvk.Name(state.Api) : "",
                L.F("as {0}, loaded by Windows", string.Join(", ", layers.Select(l => l.File))), state is not null,
                FilesOf(f => In(f, section)), ProblemsOf(section),
                state is not null && (vkd3d ? _vkd3dView : _dxvkView) is { } view ? view.UninstallAsync : null,
                L.T("Not installed by Hybrid Launcher: nothing was backed up, so its files are left for you to remove by hand.")));
        }
        if (s.UltraPlus is { } ultra && Directory.Exists(ultra.Root))
        {
            var files = FilesOf(f => In(f, "ultra"));
            rows.Add(new ModRow("ultra", "Ultra+", Color.FromArgb(0xFF, 0x4F, 0xD6, 0xB0), "ultra",
                "v" + ultra.Version + (ultra.Beta ? "  ·  " + L.T("beta") : ""),
                files.FirstOrDefault(f => f.Role == ModFileRole.Proxy) is { } p ? L.F("as {0}, loaded by Windows", p.File) : L.T("Loaded by the game's engine"), true,
                files, ProblemsOf("ultra"), UninstallUltraAsync, null));
        }
        // Proxy dlls of mods Hybrid Launcher does not manage (Special K, an unknown one): listed so nothing in the folder is a mystery.
        // Windows' own dlls a game ships beside its exe (dbghelp.dll…) are no mod: they go with the other files.
        foreach (var f in r.Files.Where(IsOtherMod))
            rows.Add(new ModRow("other:" + f.File, f.What, Color.FromArgb(0xFF, 0x9A, 0x9A, 0xA8), "", f.Version, L.F("as {0}, loaded by Windows", f.File), false,
                [f], [], null, L.T("Not managed by Hybrid Launcher: remove the file by hand if you do not want it.")));
        return rows;
    }

    /// <summary>A proxy dll no section manages that is a mod (not one of Windows' own, which games ship beside the exe).</summary>
    static bool IsOtherMod(ModFile f) => f.Role == ModFileRole.Proxy && f.Tab is null && !f.What.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>Self-test: the table's lines ("name version [x]" with x when it can be uninstalled from there), or "not read".</summary>
    internal string SelfTestModsTable() => _modsRead is not { } read ? "not read"
        : string.Join(" | ", ModRows(read).Select(r => $"{r.Name} {r.Version} [{(r.Uninstall is null ? "-" : "x")}]").DefaultIfEmpty("no mods"))
          + $" || problems={read.Problems.Count(p => !_settings.DismissedModProblems.Contains(p.Id))}";

    async Task UninstallUltraAsync()
    {
        if (_game is not { } game) return;
        if (!global::HybridLauncher.Views.ModContext.UninstallingAll && !await ConfirmAsync(L.T("Uninstall Ultra+?"), L.T("Removes Ultra+ from this game and puts back the files it replaced."), L.T("Uninstall"))) return;
        UltraPlusLibrary.Unattended = global::HybridLauncher.Views.ModContext.UninstallingAll;
        try
        {
            if (await UltraPlusLibrary.UninstallAsync(game))
            {
                _ultra?.Refresh();
                _ = UpdateModBadgesAsync();
            }
        }
        finally { if (global::HybridLauncher.Views.ModContext.UninstallingAll) UltraPlusLibrary.Unattended = false; }
        _ = RefreshModsOverviewAsync();
    }

    // ---------- drawing ----------

    /// <summary>The section's heading: the count of mods and problems, and Check for conflicts / Open folder.</summary>
    Grid ModsHeading(ModsRead? read, List<ModRow>? rows = null, int problems = 0, bool reading = false)
    {
        string subtitle = read is null
            ? reading ? L.T("Reading the game folder…") : _game?.NotInstalled == true ? L.F("{0} is not installed: its mods show here once it is.", _game.Title) : L.T("Pick the game exe first (the gear beside Play → Change game exe).")
            : rows!.Count == 0 ? L.T("No mods in this game: it runs as it shipped.")
            : string.Join("  ·  ", new[]
            {
                rows.Count == 1 ? L.T("1 mod") : L.F("{0} mods", rows.Count),
                read.Files.Count == 1 ? L.T("1 file") : L.F("{0} files", read.Files.Count),
                problems == 0 ? L.T("no conflicts") : problems == 1 ? L.T("1 problem") : L.F("{0} problems", problems),
            });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (read is not null)
        {
            var check = ModUi.Action(L.T("Check for conflicts"), "\uE9D5", () => _ = RefreshModsOverviewAsync(told: true));
            ToolTipService.SetToolTip(check, L.T("Reads the game folder again: mods in each other's way, files a game update put back, add-ons nothing loads"));
            right.Children.Add(check);
            right.Children.Add(ModUi.Action(L.T("Open folder"), "\uE838", () => Shell.Open(read.Dir)));
            if (rows!.Any(r => r.ByHybrid && r.Uninstall is not null))
            {
                var all = ModUi.Action(L.T("Uninstall all"), "\uE74D", () => _ = UninstallAllModsAsync());
                ToolTipService.SetToolTip(all, L.T("Takes out every mod Hybrid Launcher installed here; mods put in another way stay"));
                right.Children.Add(all);
            }
        }
        return Ui.Heading("\uE8FD", L.T("Installed mods"), right, subtitle, Palettes.Adapt(Color.FromArgb(0xFF, 0xF0, 0x7F, 0x2A)));
    }

    void DrawModsOverview()
    {
        if (_modsRead is not { } read) return;
        var rows = ModRows(read);
        var open = read.Problems.Where(p => !_settings.DismissedModProblems.Contains(p.Id)).ToList();
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(ModsHeading(read, rows, open.Count));

        if (rows.Count > 0)
        {
            var table = new StackPanel { Spacing = 4 };
            table.Children.Add(ModsTableRow(null, header: true));
            foreach (var row in rows)
            {
                table.Children.Add(ModsTableRow(row));
                if (_modsOpen.Contains(row.Key))
                {
                    var files = new StackPanel { Spacing = 4, Margin = new Thickness(26, 0, 0, 6) };
                    if (row.Files.Count == 0) files.Children.Add(Ui.Muted(L.T("Its files are part of OptiScaler's install.")));
                    foreach (var f in row.Files) files.Children.Add(ModFileRow(f, compact: true));
                    table.Children.Add(files);
                }
            }
            panel.Children.Add(table);
        }

        // The mod check: what stops a mod from working here, with its fix.
        var check = new StackPanel { Spacing = 8 };
        check.Children.Add(new TextBlock { Text = L.T("Conflicts and problems").ToUpperInvariant(), Style = Ui.Style("GroupHeader") });
        if (open.Count == 0)
            check.Children.Add(ModUi.Check(true, L.T("No conflicts: every mod in this folder can load."), L.F("Checked at {0:HH:mm}", _modsChecked)));
        foreach (var p in open) check.Children.Add(ModProblemRow(p, read.Dir));
        int ignored = read.Problems.Count - open.Count;
        if (ignored > 0)
        {
            var again = new HyperlinkButton { Content = ignored == 1 ? L.T("Show 1 ignored problem") : L.F("Show {0} ignored problems", ignored), Padding = new Thickness(0) };
            again.Click += (_, _) =>
            {
                _settings.DismissedModProblems.Clear();
                SettingsStore.Save();
                DrawModsOverview();
                _ = UpdateModBadgesAsync();
            };
            check.Children.Add(again);
        }
        panel.Children.Add(check);

        // Files beside the exe that belong to no mod (the game's own upscalers and their versions, Windows' dlls): folded at first.
        var others = read.Files.Where(f => f.Tab is null && !IsOtherMod(f)).ToList();
        if (others.Count > 0)
        {
            bool shown = _modsOpen.Contains("*others");
            var toggle = new HyperlinkButton { Padding = new Thickness(0) };
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(new FontIcon { Glyph = shown ? "\uE70D" : "\uE76C", FontSize = 11 });
            label.Children.Add(new TextBlock { Text = L.F("Other files beside the exe ({0}): upscalers and Windows dlls the game ships", others.Count) });
            toggle.Content = label;
            toggle.Click += (_, _) =>
            {
                if (!_modsOpen.Remove("*others")) _modsOpen.Add("*others");
                DrawModsOverview();
            };
            panel.Children.Add(toggle);
            if (shown)
            {
                var body = new StackPanel { Spacing = 4, Margin = new Thickness(26, 0, 0, 0) };
                foreach (var f in others) body.Children.Add(ModFileRow(f, compact: true));
                panel.Children.Add(body);
            }
        }
        ModsOverviewHost.Child = Ui.Card(panel, 20);
    }

    static readonly GridLength[] ModColumns =
    [
        new(22), new(1.1, GridUnitType.Star), new(1, GridUnitType.Star), new(1.6, GridUnitType.Star), new(130), new(110), new(ButtonsWidth),
    ];

    /// <summary>
    /// The buttons' column has one width on every line (Open and the bin, or the bin alone): sized by its own content, each line
    /// gave its star columns a different width and the cells stood out of line with the headings (2.0.0-rc.10).
    /// </summary>
    const double ButtonsWidth = 110;

    /// <summary>A line of the table (or its header): chevron, mod, version, loaded by, installed by, state, buttons.</summary>
    FrameworkElement ModsTableRow(ModRow? row, bool header = false)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = header ? 0 : 34 };
        foreach (var w in ModColumns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Put(FrameworkElement el, int col)
        {
            el.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(el, col);
            grid.Children.Add(el);
        }
        TextBlock Cell(string text, bool muted = true) => new()
        {
            Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            Style = muted ? Ui.Style("RowDescription") : null, FontWeight = muted ? FontWeights.Normal : FontWeights.SemiBold,
        };

        if (header || row is null)
        {
            string[] heads = ["", L.T("Mod"), L.T("Version"), L.T("Loaded"), L.T("Installed by"), L.T("State"), ""];
            for (int i = 0; i < heads.Length; i++)
                if (heads[i].Length > 0) Put(new TextBlock { Text = heads[i].ToUpperInvariant(), Style = Ui.Style("GroupHeader") }, i);
            // The same side padding as a line, so the headings stand over their cells.
            return new Border { Padding = new Thickness(12, 0, 8, 2), Child = grid };
        }

        bool open = _modsOpen.Contains(row.Key);
        var chevron = new FontIcon { Glyph = open ? "\uE70D" : "\uE76C", FontSize = 11, Opacity = row.Files.Count > 0 ? 0.8 : 0.3 };
        Put(chevron, 0);

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Palettes.Adapt(row.Tint)), VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(Cell(row.Name, muted: false));
        ToolTipService.SetToolTip(name, row.Name);
        Put(name, 1);
        var version = Cell(row.Version.Length > 0 ? row.Version : "—");
        ToolTipService.SetToolTip(version, row.Version.Length > 0 ? row.Version : null);
        Put(version, 2);
        var loaded = Cell(row.Loaded);
        ToolTipService.SetToolTip(loaded, row.Loaded);
        Put(loaded, 3);
        Put(Cell(row.ByHybrid ? "Hybrid Launcher" : L.T("By hand")), 4);

        bool broken = row.Problems.Count > 0 || row.Files.Any(f => f.Problem);
        var state = Ui.Pill(row.Problems.Count > 1 ? L.F("{0} problems", row.Problems.Count) : broken ? L.T("Problem") : L.T("OK"), broken ? Ui.Tone.Bad : Ui.Tone.Good);
        state.HorizontalAlignment = HorizontalAlignment.Left;
        if (broken) ToolTipService.SetToolTip(state, string.Join("\n", row.Problems.Select(p => p.Title).Concat(row.Files.Where(f => f.Problem).Select(f => f.File + ": " + f.How))));
        Put(state, 5);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        if (row.Section.Length > 0)
        {
            var go = new Button { Content = L.T("Open"), Padding = new Thickness(10, 0, 10, 0), Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(go, L.F("{0}'s section: install, update, settings", row.Name));
            var section = row.Section;
            go.Click += (_, _) => Ui.Safe(() => OpenTab(section));
            buttons.Children.Add(go);
        }
        var bin = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 16 }, Width = 34, Height = 30, Padding = new Thickness(0), IsEnabled = row.Uninstall is not null && !GameJobs.Busy(_game?.Id) };
        ToolTipService.SetToolTip(bin, row.Uninstall is not null ? L.F("Uninstall {0}", row.Name) : row.NoUninstall ?? L.T("Uninstall it from its section"));
        // A disabled button shows no tooltip: the reason sits on a wrapper that stays hit-testable.
        var binHost = new Border { Child = bin, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        if (row.Uninstall is null) ToolTipService.SetToolTip(binHost, row.NoUninstall ?? L.T("Uninstall it from its section"));
        var uninstall = row.Uninstall;
        bin.Click += async (_, _) =>
        {
            if (uninstall is null) return;
            try { await uninstall(); }
            catch (Exception ex) { App.Window?.ShowError(L.F("Could not uninstall {0}", row.Name), ex.Message, ex); }
            _ = RefreshModsOverviewAsync();
        };
        buttons.Children.Add(binHost);
        Put(buttons, 6);

        var line = new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(12, 5, 8, 5), MinHeight = 0, Child = grid };
        // A click on the line (not on its buttons) opens or closes the list of its files.
        if (row.Files.Count > 0)
        {
            ToolTipService.SetToolTip(chevron, open ? L.T("Hide its files") : L.T("Show its files"));
            line.Tapped += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInside<ButtonBase>(d, line)) return;
                if (!_modsOpen.Remove(row.Key)) _modsOpen.Add(row.Key);
                DrawModsOverview();
            };
        }
        return line;
    }

    static bool IsInside<T>(DependencyObject d, DependencyObject stop) where T : DependencyObject
    {
        for (var at = d; at is not null && at != stop; at = VisualTreeHelper.GetParent(at))
            if (at is T) return true;
        return false;
    }

    /// <summary>One finding of the mod check: what is wrong, its fix, and Ignore (hidden until it changes).</summary>
    Border ModProblemRow(ModProblem p, string dir)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = "\uE7BA", FontSize = 15, Foreground = new SolidColorBrush(Ui.ToneColor(Ui.Tone.Partial)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = p.Detail, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (p.Fix != ModFix.None && p.FixLabel is { } label)
        {
            var fix = new Button { Content = label, Style = Ui.Style("AccentButtonStyle") };
            fix.Click += (_, _) => Ui.Safe(() => ApplyModFix(p, dir));
            buttons.Children.Add(fix);
        }
        var ignore = new Button { Content = L.T("Ignore") };
        ToolTipService.SetToolTip(ignore, L.T("Hide this until it changes"));
        ignore.Click += (_, _) =>
        {
            _settings.DismissedModProblems.Add(p.Id);
            SettingsStore.Save();
            DrawModsOverview();
            _ = UpdateModBadgesAsync();
        };
        buttons.Children.Add(ignore);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(12, 8, 10, 8), MinHeight = 0, Child = grid };
    }

    /// <summary>What a finding's fix button does: open the section that fixes it, reinstall OptiScaler, or a change made here.</summary>
    void ApplyModFix(ModProblem problem, string dir)
    {
        switch (problem.Fix)
        {
            case ModFix.OpenTab when problem.Tab is { } tab: OpenTab(tab); break;
            case ModFix.ReinstallOpti: OpenTab("opti/install"); _ = InstallAsync(SelectedRelease, manual: false); break;
            case ModFix.StopOptiLoadingReShade:
                ModCheck.StopOptiLoadingReShade(_settings, dir);
                SettingsStore.Save();
                App.Window?.ShowSuccess(L.T("OptiScaler no longer loads ReShade in this game."));
                _iniStale = true;
                Refresh();
                break;
        }
        _ = UpdateModBadgesAsync();
    }
}
