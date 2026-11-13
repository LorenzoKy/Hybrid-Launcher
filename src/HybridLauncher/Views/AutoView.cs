using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Ini;
using HybridLauncher.Core.Mods;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>What the Auto tab needs the game page to do: the steps that go through the page's own installs and DLSS 5 section.</summary>
public interface IAutoHost
{
    /// <summary>OptiScaler from <paramref name="release"/> as <paramref name="dll"/>, through the page's install (backups, DLSS NR model, job). True when it is in.</summary>
    Task<bool> AutoInstallOptiAsync(OptiRelease release, string dll, bool spoofing);
    /// <summary>The installed OptiScaler renamed in place. True when done.</summary>
    Task<bool> AutoRenameOptiAsync(string name);
    /// <summary>DLSSNR-AMD's package ready (downloaded and prepared from the model file, asked for when missing). True when ready.</summary>
    Task<bool> AutoPrepareAmdAsync();
    /// <summary>DLSSNR-AMD laid over the game's OptiScaler. True when in.</summary>
    Task<bool> AutoPutAmdAsync();
    /// <summary>DLSSNR-AMD put in through its own ReShade. True when in.</summary>
    Task<bool> AutoPutAmdReShadeAsync();
    /// <summary>What F5 still needs after its build is in (the model beside it, the vendor-neutral port on other cards).</summary>
    Task AutoFinishF5Async();
    /// <summary>Asks for NVIDIA's nvngx_dlssnr.dll and keeps a copy, as the DLSS 5 section does. True when stored.</summary>
    Task<bool> AutoPickDlssNrAsync();
    /// <summary>A mod taken out as the Mods table's bin does (with its own question). Not done: why, when there is a reason to say.</summary>
    Task<(bool Done, string? Why)> AutoUninstallAsync(string id);
}

/// <summary>
/// The Auto tab (2.0.0-rc.7, modular since rc.8): every mod Hybrid Launcher installs (OptiScaler, DLSS 5, ReShade, RenoDX, Luma,
/// Ultra+, DXVK / VKD3D-Proton), each with a tick and the build to put in, planned by <see cref="AutoSetup"/> so that the ones
/// ticked work together (file names, the OptiScaler build DLSS 5 needs, ReShade through OptiScaler, one HDR mod, a Vulkan layer
/// alone), then installed in order with one click and the recommended settings. Every mod is managed afterwards from its own
/// section, as if installed there.
/// </summary>
public sealed partial class AutoView : UserControl
{
    readonly ModContext _ctx;
    readonly IAutoHost _host;
    readonly StackPanel _facts = new() { Spacing = 6 };
    // The mods side by side (2.0.0-rc.10): as many columns as the width holds, so the whole plan fits on screen without a scroll.
    // In rows (2.0.0-rc.11): the mods keep their places left to right whatever a tick opens; the masonry moved them between
    // columns as a card grew.
    readonly Controls.MasonryPanel _rows = new() { MinColumn = 430, MaxColumns = 3, Gap = 12, Rows = true };
    /// <summary>The mods this game cannot take, on one line under the cards (2.0.0-rc.13): a full card each only said "no".</summary>
    readonly StackPanel _notHere = new() { Spacing = 6 };
    /// <summary>What each mod is set up with, shown on demand (2.0.0-rc.13): the cards stay short.</summary>
    static bool _details;
    readonly Button _detailsButton;
    /// <summary>DLSS 5 ticked with no method picked yet (2.0.0-rc.13): its Method says "Select one" in red, Install waits.</summary>
    bool _dlss5Pending;

    /// <summary>The DLSS NR model file kept by Hybrid Launcher: its version, "" when there is none, null until read (off the UI thread).</summary>
    string? _model;
    readonly StackPanel _resolved = new() { Spacing = 6 };
    readonly Border _resolvedCard;
    readonly StackPanel _results = new() { Spacing = 6 };
    readonly Border _resultsCard;
    readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed };
    readonly TextBlock _status;
    readonly Button _go, _again;
    readonly Button _uninstall;
    /// <summary>Installed mods ticked by the user: what Uninstall takes out (rc.11).</summary>
    readonly HashSet<string> _remove = new(StringComparer.OrdinalIgnoreCase);

    static bool In(AutoItem i) => i.State is AutoState.Installed or AutoState.Update;

    /// <summary>The installed mods ticked now, in the plan's order.</summary>
    List<AutoItem> Ticked() => _plan?.Items.Where(i => In(i) && _remove.Contains(i.Id)).ToList() ?? [];
    readonly AutoChoice _choice = new();

    AutoFacts? _f;
    AutoPlan? _plan;
    List<RenoDxMod> _renoHits = [];
    /// <summary>Why each RenoDX mod fits (score 70 = a generic mod whose list names this game).</summary>
    List<RenoDxMatch> _renoMatches = [];
    RenoDxMod? _renoDx;
    /// <summary>The RenoDX and Luma mods picked by hand, by name: the lists are parsed again on every read (2.0.0-rc.18: a pick was lost).</summary>
    string? _renoPick, _lumaPick;
    LumaMod? _luma;
    List<LumaMatch> _lumaMatches = [];
    List<LumaVersion> _lumaVersions = [];
    LumaVersion? _lumaVersion;
    UltraGame? _ultraGame;
    List<UltraFile> _ultraVersions = [];
    UltraFile? _ultraFile;
    List<string> _reshadeVersions = [];
    string? _reshadeVersion;
    List<DxvkRelease> _dxvkReleases = [];
    DxvkRelease? _dxvkRelease;
    D3dVersion _d3d = D3dVersion.D3D11;
    // OptiScaler's sources (key, name) and the releases read for each; the build picked.
    readonly List<(string Key, string Name)> _sources = [];
    readonly Dictionary<string, List<OptiRelease>> _releases = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _sourceErrors = new(StringComparer.OrdinalIgnoreCase);
    OptiRelease? _optiRelease;
    bool _running;
    int _read;

    static Color MyTint => Palettes.Adapt(Color.FromArgb(0xFF, 0x2E, 0xC7, 0xF0));

    public AutoView(ModContext ctx, IAutoHost host)
    {
        _ctx = ctx;
        _host = host;
        _status = Ui.Muted("");
        _status.TextWrapping = TextWrapping.Wrap;
        // Always one line (2.0.0), empty or not: a message coming or going never moves the cards under it. The whole message
        // is in the tooltip when the window is too narrow for it.
        _status.MinHeight = 20;
        _status.MaxLines = 1;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.IsTextTrimmedChanged += (_, _) => ToolTipService.SetToolTip(_status, _status.IsTextTrimmed ? _status.Text : null);

        // One card (2.0.0-rc.13): what the tab does and what was read about the game, then the bar that acts on the mods right
        // above them, then the mods. It was two cards, with the buttons far from the description.
        var top = new StackPanel { Spacing = 12 };
        // Experimental (2.0.0-rc.12): a pill beside Read again, and a word on why the manual way is the recommended one.
        var headRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var pill = Ui.Pill(L.T("Experimental"), Ui.Tone.Partial);
        ToolTipService.SetToolTip(pill, L.T("The Auto tab is new: it may pick something wrong for some games."));
        headRight.Children.Add(pill);
        headRight.Children.Add(Ui.LinkButton(L.T("Read again"), "", () => _ = LoadAsync()));
        top.Children.Add(Ui.Heading("", L.T("Auto setup"), headRight,
            subtitle: L.T("Tick the mods you want: Hybrid Launcher downloads each one and puts it in this game's folder in the right order, with settings that work together. What a mod replaces is backed up."),
            tint: MyTint));
        top.Children.Add(Ui.Callout("", L.T("Experimental. Installing each mod from its own tab (OptiScaler, Mods, Ultra+) is recommended: you see what goes in, where and why, and you can undo one step at a time."), Ui.Tone.Partial));
        top.Children.Add(_facts);

        // The bar: the plan's state on the left; Install, Uninstall, back to the recommendation and Details on the right.
        _go = ModUi.Action(L.T("Install"), "", () => _ = RunAsync(), accent: true);
        // Uninstall takes out the ticked mods that are in the game (2.0.0-rc.11; it was a menu of every installed mod).
        _uninstall = ModUi.Action(L.T("Uninstall"), "\uE74D", () => _ = UninstallTickedAsync());
        _again = ModUi.Action(L.T("Use the recommendation"), "", () =>
        {
            _choice.Opti = _choice.ReShade = _choice.RenoDx = _choice.Luma = _choice.Ultra = _choice.Dxvk = _choice.ReShadeAddon = null;
            _choice.OptiSource = _choice.OptiDll = _choice.Dlss5 = null;
            _dlss5Pending = false;
            Replan();
        });
        _detailsButton = ModUi.Action(L.T("Show details"), "\uE8BD", () =>
        {
            _details = !_details;
            Replan();
        });
        ToolTipService.SetToolTip(_detailsButton, L.T("What each ticked mod goes in with: file name, build and settings"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_go);
        buttons.Children.Add(_uninstall);
        buttons.Children.Add(_again);
        buttons.Children.Add(_detailsButton);
        var bar = new Grid { ColumnSpacing = 12 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var progressRow = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        progressRow.Children.Add(new TextBlock { Text = L.T("Plan"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        progressRow.Children.Add(_status);
        progressRow.Children.Add(_progress);
        bar.Children.Add(progressRow);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        top.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 2, 0, 0) });
        top.Children.Add(bar);
        top.Children.Add(_rows);
        top.Children.Add(_notHere);

        var conflicts = new StackPanel { Spacing = 8 };
        conflicts.Children.Add(Ui.Heading("", L.T("Conflicts handled"), tint: Ui.Orange));
        conflicts.Children.Add(_resolved);
        _resolvedCard = Ui.Card(conflicts);
        _resolvedCard.Visibility = Visibility.Collapsed;

        var results = new StackPanel { Spacing = 8 };
        results.Children.Add(Ui.Heading("", L.T("Last run"), tint: Ui.Green));
        results.Children.Add(_results);
        _resultsCard = Ui.Card(results);
        _resultsCard.Visibility = Visibility.Collapsed;

        var tips = new StackPanel { Spacing = 10 };
        var tipList = new Controls.MasonryPanel { MinColumn = 380, MaxColumns = 3, Gap = 10, Visibility = Visibility.Collapsed };
        // Folded at first (2.0.0-rc.13): the plan above is what the tab is for; the tips are a click away.
        var tipsHead = new Grid();
        tipsHead.Children.Add(Ui.Heading("", L.T("Good to know"), tint: Ui.Slate));
        var chevron = new FontIcon { Glyph = "\uE70D", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        tipsHead.Children.Add(chevron);
        Motion.FoldOnPress(tipsHead, () =>
        {
            bool open = tipList.Visibility != Visibility.Visible;
            chevron.Glyph = open ? "\uE70E" : "\uE70D";
            Motion.Fold(tipList, open);
        });
        tips.Children.Add(tipsHead);
        foreach (var t in new[]
        {
            "• Close the game before installing. Each mod's build is picked in its row; the newest stable one is picked first.",
            "• What you tick changes what the others can be: DLSS 5's F5 method needs janblade's F5 build of OptiScaler, DLSSNR-AMD a build without DLSS NR. A Vulkan layer goes with the others, but some mods may not work through it.",
            "• With OptiScaler in the game, ReShade goes in as ReShade64.dll and OptiScaler loads it, so both work.",
            "• RenoDX is mainly for HDR; Luma reworks a game's rendering (upscaling, HDR, fixes, depending on the mod). Only one of them goes in. RenoDX needs ReShade's add-on build, which is picked for you; Luma brings its own ReShade. Without them the standard build is safer in online games.",
            "• RenoDX and Luma mods are matched to the game by name, so a match can be wrong, and a generic mod (one for every game of an engine) may not work or may not be needed: check the mod in Mods → RenoDX or Mods → Luma before installing.",
            "• Everything installed here is managed from its own place afterwards: OptiScaler in its tab, the other mods in Mods, Ultra+ in its tab.",
            "• In game: Insert opens OptiScaler's menu, Home opens ReShade (and the RenoDX or Luma settings on its Add-ons page).",
            "• Files a mod replaces are backed up as set in Settings → Features, and put back when it is removed.",
        })
        {
            var tip = Ui.Muted(L.T(t));
            tip.TextWrapping = TextWrapping.Wrap;
            tipList.Children.Add(tip);
        }
        tips.Children.Add(tipList);

        // Under the plan, side by side: the last run (what went in, what did not and why), the conflicts settled, the tips.
        var below = new Controls.MasonryPanel { MinColumn = 400, MaxColumns = 3, Gap = 12 };
        below.Children.Add(_resultsCard);
        below.Children.Add(_resolvedCard);
        // The tips take the whole width, in columns, so they stay a few lines tall.
        var tipsCard = Ui.Card(tips);
        Controls.MasonryPanel.SetSpan(tipsCard, 3);
        below.Children.Add(tipsCard);

        var page = new StackPanel { Spacing = 12, MaxWidth = 1880 };
        page.Children.Add(Ui.Card(top));
        page.Children.Add(below);
        Content = page;
        _facts.Children.Add(Ui.Loading());
        _ = LoadAsync();
    }

    /// <summary>The tab was shown again: what is in the folder may have changed since (another tab, Explorer).</summary>
    public void Refresh()
    {
        if (!_running && _f is not null) _ = LoadAsync();
    }

    // ---------- what the game is and holds ----------

    /// <summary>Whether OptiPatcher supports the game (null: the list could not be read).</summary>
    static async Task<bool?> SafePatcher(string dir, CancellationToken ct)
    {
        try { return await OptiPatcherSupport.IsSupportedAsync(dir, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    async Task LoadAsync()
    {
        int run = ++_read;
        _ = ReadModelAsync();
        var dir = _ctx.TargetDir();
        if (dir is null || !Directory.Exists(dir))
        {
            _facts.Children.Clear();
            _facts.Children.Add(Ui.Callout("", L.T("Pick the game exe first (the gear beside Play → Change game exe)."), Ui.Tone.Partial));
            _rows.Children.Clear();
            _go.IsEnabled = false;
            return;
        }
        var game = _ctx.Game;
        var settings = _ctx.Settings;
        var ct = _ctx.Token;
        try
        {
            var exe = GameScanner.EffectiveExe(game, settings);
            var apiTask = Core.Media.GraphicsApi.GetAsync(game, exe, _ctx.SteamAppId(), ct);
            var wikiTask = CompatWiki.GetEntryAsync(Core.Media.GameNames.LookupName(game), ct);
            var renoTask = SafeList(() => RenoDx.GetModsAsync(ct: ct));
            var lumaTask = SafeList(() => Luma.GetModsAsync(ct: ct));
            var buildsTask = SafeList(() => Luma.GetBuildsAsync(ct: ct));
            var reshadeTask = SafeList(() => ReShade.GetVersionsAsync(ct: ct));
            var ultraTask = SafeList(() => UltraPlus.GetGamesAsync(ct: ct));
            var ultraFilesTask = SafeList(() => UltraPlus.GetModFilesAsync(ct: ct));
            var patcherTask = SafePatcher(dir, ct);
            var local = await Task.Run(() =>
            {
                bool is64 = exe is null || RenoDx.Is64Bit(exe) != false;
                var rs = ReShade.Detect(dir);
                var opti = OptiDetector.Detect(dir);
                return (Is64: is64, Files: AutoSetup.ReadFolder(dir, settings), Opti: opti, ReShade: rs,
                    Limited: rs is not null && ModCheck.LimitedAddons(rs.Path), RenoDx: RenoDx.Installed(dir).Count > 0,
                    Luma: settings.Luma is not null || Luma.Installed(dir).Count > 0, Layer: Dxvk.Detect(dir).Count > 0,
                    Nr: opti is not null && settings.Install is { } m && DlssNr.NeededBy(m),
                    Hdr: Core.Hardware.HardwareInfo.AnyHdrOn(), Engine: RenoDx.DetectEngine(game.InstallDir, exe), Tier: Dlss5.Detect().Tier);
            }, ct);
            var apis = await apiTask;
            var name = Core.Media.GameNames.PlainOrClean(Core.Media.GameNames.LookupName(game));
            var d3d = Dxvk.FromApis(apis.Apis);
            _d3d = d3d.Count > 0 ? d3d[0] : D3dVersion.D3D11;
            static T? Done<T>(Task<T> t) => t.IsCompletedSuccessfully ? t.Result : default;

            // Made twice: at once with what is in (the folder, the API, lists already cached), then again with every list.
            void Make(bool reading)
            {
                var wiki = Done(wikiTask);
                var renoMods = Done(renoTask) ?? [];
                var lumaMods = Done(lumaTask) ?? [];
                var builds = Done(buildsTask) ?? [];
                _reshadeVersions = Done(reshadeTask) ?? [];
                var ultraGames = Done(ultraTask) ?? [];
                var ultraFiles = Done(ultraFilesTask) ?? [];

                _renoMatches = RenoDx.Match(renoMods, name, local.Engine).Take(5).ToList();
                _renoHits = _renoMatches.Select(h => h.Mod).ToList();
                _renoDx = _renoHits.FirstOrDefault(m => m.Name == _renoPick) ?? _renoHits.FirstOrDefault();
                var renoSnap = _renoDx?.Snapshot(local.Is64);
                bool renoFits = renoSnap is not null && renoSnap.Is32Bit != local.Is64;
                _lumaMatches = Luma.Match(lumaMods, name, local.Engine).Take(5).ToList();
                _luma = settings.Luma is { } lm ? lumaMods.FirstOrDefault(m => m.Name == lm.Mod) : null;
                _luma ??= _lumaMatches.FirstOrDefault(h => h.Mod.Name == _lumaPick)?.Mod ?? _lumaMatches.FirstOrDefault()?.Mod;
                _lumaVersions = _luma is null ? [] : Luma.Versions(_luma, builds, !local.Is64);
                // A build picked before a new read stays picked.
                _lumaVersion = _lumaVersion is { } lv && _lumaVersions.Contains(lv) ? lv : _lumaVersions.FirstOrDefault(v => !v.Test) ?? _lumaVersions.FirstOrDefault();
                if (_reshadeVersion is null || !_reshadeVersions.Contains(_reshadeVersion)) _reshadeVersion = _reshadeVersions.FirstOrDefault();
                _ultraGame = UltraPlus.Match(ultraGames, game.Name, game.InstallDir, exe).FirstOrDefault()?.Game;
                _ultraVersions = _ultraGame is null ? [] : UltraPlus.Versions(_ultraGame, ultraFiles, game.Store == GameStore.Xbox, betas: true);
                _ultraFile = _ultraFile is { } uf && _ultraVersions.Contains(uf) ? uf : _ultraVersions.FirstOrDefault(v => !v.Beta) ?? _ultraVersions.FirstOrDefault();

                bool nvidia = settings.Setup.Nvidia ?? File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
                string? dlss5Now = settings.DlssNrAmdReShade is not null ? AutoSetup.AmdReShade
                    : settings.Install?.DlssNrAmd is not null ? AutoSetup.Amd
                    : local.Opti is not null && local.Nr ? AutoSetup.F5 : null;
                _f = new AutoFacts
                {
                    Is64 = local.Is64,
                    Apis = apis.Apis,
                    Nvidia = nvidia,
                    Dlss5Tier = local.Tier,
                    Hdr = local.Hdr,
                    Files = local.Files,
                    OptiFile = local.Opti is { } o ? Path.GetFileName(o.DllPath) : null,
                    OptiIsDlssNr = local.Nr,
                    OptiSourceNow = settings.Install is { } im ? AutoSetup.SourceKey(im.Channel, im.Channel == OptiChannel.Custom ? im.AssetName : im.Source) : null,
                    ReShade = local.ReShade,
                    ReShadeLimited = local.Limited,
                    RenoDxInstalled = local.RenoDx,
                    LumaInstalled = local.Luma,
                    Layer = local.Layer,
                    Dlss5Now = dlss5Now,
                    WikiFilename = WikiName(wiki?.Get("Filename")?.Text),
                    WikiSpoofing = AutoSetup.SpoofingFromWiki(wiki?.Get("Settings")?.Text),
                    OptiPatcher = Done(patcherTask),
                    SpoofingSet = settings.Setup.SpoofingSet ? settings.Setup.Spoofing : null,
                    RenoDx = _renoDx is null ? null : new AutoCandidate(_renoDx.Name, _renoDx.Working, renoFits, _renoDx.Engine,
                        _renoMatches.FirstOrDefault(h => ReferenceEquals(h.Mod, _renoDx))?.Score == 70),
                    Luma = _luma is null ? null : new AutoCandidate(_luma.Name, _luma.Status == LumaStatus.Working, _lumaVersion is not null, _luma.Engine,
                        _lumaMatches.FirstOrDefault(h => ReferenceEquals(h.Mod, _luma))?.Score == 80),
                    UltraFits = _ultraGame is not null && _ultraVersions.Count > 0,
                    UltraInstalled = settings.UltraPlus is not null,
                    Reading = reading,
                };
                ShowFacts(_f, apis.Source, wiki is not null);
                Replan();
            }

            if (run != _read || ct.IsCancellationRequested) return;
            if (_sources.Count == 0) { _sources.Add((AutoSetup.Stable, L.T("Stable"))); _sources.Add((AutoSetup.Nightly, L.T("Nightly"))); }
            var all = Task.WhenAll(wikiTask, patcherTask, renoTask, lumaTask, buildsTask, reshadeTask, ultraTask, ultraFilesTask);
            if (!all.IsCompleted) Make(reading: true);
            await all;
            await LoadSourcesAsync(ct);
            if (run != _read || ct.IsCancellationRequested) return;
            Make(reading: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"Auto tab for {game.Name}: {ex.Message}");
            _facts.Children.Clear();
            _facts.Children.Add(Ui.Callout("", L.F("Could not read the game: {0}", ex.Message), Ui.Tone.Bad));
        }
    }

    static async Task<List<T>> SafeList<T>(Func<Task<List<T>>> read)
    {
        try { return await read(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Auto tab list: {ex.Message}"); return []; }
    }

    /// <summary>OptiScaler's sources: the official channels, every fork, the builds imported by hand.</summary>
    async Task LoadSourcesAsync(CancellationToken ct)
    {
        _sources.Clear();
        _sources.Add((AutoSetup.Stable, L.T("Stable")));
        _sources.Add((AutoSetup.Nightly, L.T("Nightly")));
        foreach (var fork in OptiForks.All(AppState.Settings)) _sources.Add(("fork:" + fork.Repo, fork.Name.Length > 0 ? fork.Name : fork.Repo));
        try
        {
            var custom = await CustomBuilds.ScanAsync(AppState.Settings, ct);
            foreach (var c in custom)
            {
                var key = "custom:" + c.Name;
                _sources.Add((key, L.F("Imported: {0}", c.Name)));
                _releases[key] = [c.ToRelease()];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Auto tab custom builds: {ex.Message}"); }
    }

    /// <summary>The releases of a source, read once (GitHub, cached by OptiReleases).</summary>
    async Task<List<OptiRelease>> ReleasesAsync(string key)
    {
        if (_releases.TryGetValue(key, out var list)) return list;
        try
        {
            list = key == AutoSetup.Stable ? await OptiReleases.GetAsync(OptiChannel.Stable, ct: _ctx.Token)
                : key == AutoSetup.Nightly ? await OptiReleases.GetAsync(OptiChannel.Nightly, ct: _ctx.Token)
                : key.StartsWith("fork:", StringComparison.OrdinalIgnoreCase) ? await OptiReleases.GetForkAsync(key[5..], ct: _ctx.Token)
                : [];
            _sourceErrors.Remove(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sourceErrors[key] = ex.Message;
            list = [];
        }
        _releases[key] = list;
        return list;
    }

    /// <summary>The first dll name in the wiki's Filename cell ("dxgi.dll", "winmm.dll / version.dll"…).</summary>
    static string? WikiName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"[A-Za-z0-9_]+\.(dll|asi)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    void ShowFacts(AutoFacts f, string apiSource, bool wiki)
    {
        _facts.Children.Clear();
        var chips = new List<FrameworkElement>
        {
            Ui.Chip(f.Is64 ? L.T("64-bit") : L.T("32-bit")),
            Ui.Chip(f.Apis.Count > 0 ? string.Join(" / ", f.Apis) : L.T("Graphics API not known")),
            Ui.Chip(f.Nvidia ? L.T("Nvidia card") : L.T("AMD / Intel card")),
            Ui.Chip(L.F("DLSS 5: {0}", Dlss5.TierName(f.Dlss5Tier))),
            Ui.Chip(f.Hdr ? L.T("HDR on") : L.T("HDR off"), f.Hdr ? Ui.Tone.Good : Ui.Tone.Unknown),
        };
        if (wiki) chips.Add(Ui.Chip(L.T("On the OptiScaler wiki"), Ui.Tone.Good));
        _facts.Children.Add(Ui.Wrap(chips));
        if (apiSource.Length > 0 && f.Apis.Count > 0) _facts.Children.Add(Ui.Muted(L.F("Graphics API from {0}.", apiSource)));
    }

    // ---------- the plan ----------

    void Replan() => Replan(false);

    void Replan(bool fromTick)
    {
        if (_f is null) return;
        _plan = AutoSetup.Plan(_f, _choice);
        if (_plan["dlss5"] is not { State: AutoState.Install }) _dlss5Pending = false;
        // A tick only redraws the cards it changed (2.0.0): the others, and the ticked box itself, stay as they are, so the box
        // animates and nothing else on the page is built again. Anything else (a new read, a list loaded) draws every card anew.
        if (!fromTick) _rowGen++;
        // Shown OptiScaler first (the plan keeps DLSS 5 ahead of it for the install order), then as the plan lists them. The mods
        // this game cannot take are one line under them (2.0.0-rc.13), each with its reason on hover.
        var shown = _plan.Items.Where(i => i.State != AutoState.NotPossible).OrderBy(i => i.Id == "opti" ? 0 : 1).Select(Row).ToList();
        if (shown.Count != _rows.Children.Count || shown.Where((r, i) => !ReferenceEquals(r, _rows.Children[i])).Any())
        {
            _rows.Children.Clear();
            foreach (var r in shown) _rows.Children.Add(r);
        }
        _notHere.Children.Clear();
        var notHere = _plan.Items.Where(i => i.State == AutoState.NotPossible).ToList();
        if (notHere.Count > 0)
        {
            var chips = new List<FrameworkElement> { Ui.Muted(L.T("Not for this game:")) };
            foreach (var i in notHere)
            {
                var chip = Ui.Chip(i.Name);
                ToolTipService.SetToolTip(chip, i.Why);
                chips.Add(chip);
            }
            _notHere.Children.Add(Ui.Wrap(chips));
        }
        ((TextBlock)((StackPanel)_detailsButton.Content).Children[1]).Text = _details ? L.T("Hide details") : L.T("Show details");
        _resolved.Children.Clear();
        foreach (var r in _plan.Resolved) _resolved.Children.Add(ModUi.Check(true, r));
        _resolvedCard.Visibility = _plan.Resolved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
        // The OptiScaler build list follows the source the plan settled on. Read as soon as OptiScaler can be ticked, so its
        // card shows the lists before the tick and does not grow with it.
        if (_plan["opti"] is { } opti && Tickable(opti)) _ = PickReleaseAsync(_plan.OptiSource);
    }

    void UpdateButtons()
    {
        int n = _plan?.Items.Count(i => i.Acts) ?? 0;
        bool busy = _running || GameJobs.Busy(_ctx.Game.Id);
        bool optiWaiting = _plan?["opti"] is { Acts: true } && _optiRelease is null;
        _go.IsEnabled = n > 0 && !busy && !optiWaiting && !_dlss5Pending;
        ((TextBlock)((StackPanel)_go.Content).Children[1]).Text = n == 0 ? L.T("Nothing to install") : n == 1 ? L.T("Install 1 mod") : L.F("Install {0} mods", n);
        _again.IsEnabled = !busy;
        // Uninstall takes out the ticked mods that are in the game.
        _remove.RemoveWhere(id => _plan?[id] is not { } i || !In(i));
        int m = Ticked().Count;
        _uninstall.IsEnabled = m > 0 && !busy;
        ((TextBlock)((StackPanel)_uninstall.Content).Children[1]).Text = m <= 1 ? L.T("Uninstall") : L.F("Uninstall {0} mods", m);
        ToolTipService.SetToolTip(_uninstall, m > 0
            ? L.T("Take the ticked mods out of this game, as their own tabs do (the files they replaced come back)")
            : L.T("Tick a mod that is in the game to uninstall it"));
        _again.Visibility = _choice.Custom ? Visibility.Visible : Visibility.Collapsed;
        if (!_running) _status.Text = _dlss5Pending ? L.T("DLSS 5 is ticked: select its method, or untick it.")
            : n == 0 && _plan is not null && Ticked().Count == 0 ? L.T("Nothing is ticked. Tick a mod to install it, or one that is in to put another build in its place.")
            : optiWaiting ? L.T("Reading OptiScaler's builds…") : "";
    }

    FrameworkElement Row(AutoItem item)
    {
        var (glyph, tint) = item.Id switch
        {
            "opti" => ("", Palettes.Adapt(Color.FromArgb(0xFF, 0x9A, 0x7C, 0xFF))),
            "dlss5" => ("", Palettes.Adapt(Color.FromArgb(0xFF, 0x76, 0xB9, 0x00))),
            "reshade" => ("", Ui.Orange),
            "renodx" => ("", Ui.Gold),
            "luma" => ("", Palettes.Adapt(Color.FromArgb(0xFF, 0xFF, 0x8A, 0x3D))),
            "dxvk" => ("", Palettes.Adapt(Color.FromArgb(0xFF, 0xE8, 0x4A, 0x5F))),
            _ => ("", Palettes.Adapt(Color.FromArgb(0xFF, 0x4F, 0xD6, 0xB0))),
        };
        // The card, its grid and its box are made once per mod and kept (2.0.0): the box is never rebuilt under the pointer, so
        // its check animates, and only the text part is drawn again when what it shows changed.
        if (!_cards.TryGetValue(item.Id, out var card))
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string id = item.Id;
            var box = new CheckBox
            {
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0),
            };
            box.Click += (_, _) => Ui.Safe(() => OnTick(id, box.IsChecked == true));
            grid.Children.Add(box);
            var badge = Ui.Badge(glyph, tint, 30);
            badge.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
            var border = new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(14, 12, 10, 12), Child = grid };
            // Cards pushed by another that grew or shrank glide to their new place instead of jumping.
            Motion.GlideOnLayout(border);
            card = new Card(border, grid, box);
            _cards[id] = card;
        }

        // DLSS 5 (2.0.0-rc.13): its tick enables the Method list, which says "Select one" in red until a method is picked.
        // A mod that is in the game is ticked only when the user ticks it (rc.11): then Uninstall takes it out, or Install puts
        // the build picked here in its place.
        bool inGame = In(item);
        var tick = card.Box;
        bool isChecked = inGame ? _remove.Contains(item.Id) : item.Selected || item.State is AutoState.Covered || (item.Id == "dlss5" && _dlss5Pending);
        if (tick.IsChecked != isChecked) tick.IsChecked = isChecked;
        tick.IsEnabled = (inGame || item.CanTick) && !_running;
        tick.Opacity = item.State is AutoState.NotPossible ? 0.35 : 1;
        ToolTipService.SetToolTip(tick, inGame
            ? item.Replaceable ? L.T("Tick it, then Uninstall to take it out, or Install to put the build picked here in its place") : L.T("Tick it, then Uninstall to take it out")
            : item.Locked ? L.T("Needed by another ticked mod") : item.CanTick ? L.T("Part of the one click") : null);

        var sig = Signature(item);
        if (card.Sig == sig && card.Text is not null) return card.Border;
        card.Sig = sig;
        if (card.Text is not null) card.Grid.Children.Remove(card.Text);

        var text = new StackPanel { Spacing = 3 };
        // The name and its state on the left; on the right the mod's own tab and, when it is in, the bin (2.0.0-rc.10).
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new WrapPanel(8, []) { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        // No pill for a mod that is only on offer: every mod is optional, saying so told nothing.
        (string Text, Ui.Tone Tone)? pill = item.State switch
        {
            AutoState.Installed => (L.T("Installed"), Ui.Tone.Good),
            AutoState.Update => (L.T("Will be replaced"), Ui.Tone.Partial),
            AutoState.Covered => (L.T("Comes with another pick"), Ui.Tone.Good),
            AutoState.NotPossible => (L.T("Not for this game"), Ui.Tone.Unknown),
            _ when item.Locked => (L.T("Needed"), Ui.Tone.Accent),
            _ when item.Recommended => (L.T("Recommended"), Ui.Tone.Accent),
            _ => null,
        };
        if (pill is { } p) title.Children.Add(Ui.Pill(p.Text, p.Tone));
        // A generic RenoDX or Luma mod (2.0.0-rc.18): flagged on the card, not only in the list of mods.
        if (item.State != AutoState.NotPossible && GenericOf(item.Id) is { } engine)
        {
            var generic = Ui.Pill(L.T("Generic"), Ui.Tone.Partial);
            ToolTipService.SetToolTip(generic, L.F("Made for every {0} game, not for this one: it may not work here or may not be needed.", AutoSetup.EngineName(engine)));
            title.Children.Add(generic);
        }
        titleRow.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        var (target, label) = TabOf(item.Id);
        var open = new Button { Content = IconText("", label), Padding = new Thickness(10, 0, 10, 0), Height = ActionHeight, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(open, L.F("{0}'s section: install, update, settings", item.Name));
        open.Click += (_, _) => Ui.Safe(() => _ctx.ShowTab(target));
        actions.Children.Add(open);
        if (item.State is AutoState.Installed or AutoState.Update)
        {
            var bin = new Button { Content = new FontIcon { Glyph = "", FontSize = 17 }, Width = ActionHeight + 4, Height = ActionHeight, Padding = new Thickness(0), IsEnabled = !_running && !GameJobs.Busy(_ctx.Game.Id) };
            ToolTipService.SetToolTip(bin, L.F("Uninstall {0}", item.Name));
            bin.Click += (_, _) => _ = UninstallAsync(item.Id, item.Name);
            actions.Children.Add(bin);
        }
        Grid.SetColumn(actions, 1);
        titleRow.Children.Add(actions);
        text.Children.Add(titleRow);
        var why = Ui.Muted(item.Why);
        why.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(why);
        if (Options(item) is { } options) text.Children.Add(options);
        // RenoDX and Luma (2.0.0-rc.18): the match comes from their lists by name and can be wrong; their own section says more.
        if (item.Id is "renodx" or "luma" && item.State != AutoState.NotPossible)
        {
            // A grid, not a horizontal stack: the sentence wraps under itself instead of running off the card.
            var note = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            note.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            note.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            note.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
            var noteText = Ui.Muted(L.F("Matched by name from the {0} list, so it may be wrong. Checking it in Mods → {0} first is recommended.", item.Name));
            noteText.FontSize = 12;
            noteText.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(noteText, 1);
            note.Children.Add(noteText);
            text.Children.Add(note);
        }
        if (_details && item.Acts && item.Settings.Count > 0)
        {
            var list = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var s in item.Settings)
                list.Children.Add(new TextBlock { Text = "· " + s, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(list);
        }
        Grid.SetColumn(text, 2);
        card.Grid.Children.Add(text);
        card.Text = text;
        return card.Border;
    }

    /// <summary>A mod's card, kept across plans: the border in the list, its grid, its box, and the text part drawn last.</summary>
    sealed class Card(Border border, Grid grid, CheckBox box)
    {
        public Border Border { get; } = border;
        public Grid Grid { get; } = grid;
        public CheckBox Box { get; } = box;
        public FrameworkElement? Text { get; set; }
        public string Sig { get; set; } = "";
    }

    readonly Dictionary<string, Card> _cards = new(StringComparer.Ordinal);
    int _rowGen;

    /// <summary>
    /// Everything a card's text part is drawn from that a tick can change. Anything a tick cannot change (lists read, a new
    /// read of the game) bumps <see cref="_rowGen"/> instead, which draws every card again.
    /// </summary>
    string Signature(AutoItem item)
    {
        var p = _plan!;
        return string.Join("|", _rowGen, item.State, item.Selected, item.Locked, item.Recommended, item.Replaceable, item.Why,
            string.Join("\u001f", item.Settings), GenericOf(item.Id), _details, _running, GameJobs.Busy(_ctx.Game.Id), _dlss5Pending,
            p.OptiSource, p.OptiDll, p.OptiSourceWhy, p.Dlss5, p.ReShadeAddon, p.ReShadeAddonNeeded, _optiRelease?.Tag,
            _choice.Ultra, _choice.OptiDll, _remove.Contains(item.Id));
    }

    /// <summary>A mod's box ticked or unticked: the choice changes and the plan is made again, redrawing only what changed.</summary>
    void OnTick(string id, bool on)
    {
        if (_plan?[id] is not { } item) return;
        if (In(item))
        {
            if (on) _remove.Add(id); else _remove.Remove(id);
            // One that cannot be replaced only counts for Uninstall.
            if (!item.Replaceable) { UpdateButtons(); return; }
        }
        switch (id)
        {
            case "dlss5":
                _dlss5Pending = on;
                if (!on && _plan?.Dlss5 is { } was && was != AutoSetup.Off)
                {
                    // Off: OptiScaler's build picked for the method is let go too.
                    _choice.Dlss5 = AutoSetup.Off;
                    _choice.OptiSource = null;
                    _optiRelease = null;
                }
                break;
            case "opti": _choice.Opti = on; break;
            case "reshade": _choice.ReShade = on; break;
            // One HDR mod at a time: ticking one clears the other.
            case "renodx": _choice.RenoDx = on; if (on) _choice.Luma = false; break;
            case "luma": _choice.Luma = on; if (on) _choice.RenoDx = false; break;
            case "ultra": _choice.Ultra = on; break;
            // A Vulkan layer goes with the others too (2.0.0-rc.16): whether they work through it is the user's call.
            case "dxvk": _choice.Dxvk = on; break;
        }
        Replan(fromTick: true);
    }

    /// <summary>Whether a mod's card can be ticked to go in (or in again): its pickers then show, greyed out until it is ticked.</summary>
    static bool Tickable(AutoItem i) => i.State is AutoState.Install or AutoState.Update || i.State == AutoState.Installed && i.Replaceable;

    /// <summary>The tab button and the bin of a row: one height, the bin square and its icon large enough to read (2.0.0-rc.10).</summary>
    const double ActionHeight = 32;

    /// <summary>A mod taken out as its own tab does (its own question first); the plan is read again after.</summary>
    async Task UninstallAsync(string id, string name)
    {
        if (_running) return;
        try
        {
            var (done, why) = await _host.AutoUninstallAsync(id);
            if (!done && why is not null) App.Window?.ShowInfo(L.F("{0}: {1}", name, why));
        }
        catch (Exception ex) { App.Window?.ShowError(L.F("Could not uninstall {0}", name), ex.Message, ex); }
        _ = LoadAsync();
    }

    /// <summary>Uninstall: the ticked mods that are in the game, one after the other, each with its own tab's question.</summary>
    async Task UninstallTickedAsync()
    {
        if (_running) return;
        var ticked = Ticked();
        if (ticked.Count == 0) return;
        _uninstall.IsEnabled = false;
        foreach (var item in ticked)
        {
            try
            {
                var (done, why) = await _host.AutoUninstallAsync(item.Id);
                if (!done && why is not null) App.Window?.ShowInfo(L.F("{0}: {1}", item.Name, why));
            }
            catch (Exception ex) { App.Window?.ShowError(L.F("Could not uninstall {0}", item.Name), ex.Message, ex); }
        }
        _remove.Clear();
        _ = LoadAsync();
    }

    /// <summary>Where a mod is managed: its own tab or section of Mods, and the button's words.</summary>
    (string Target, string Label) TabOf(string id) => id switch
    {
        "opti" => ("opti/install", L.T("OptiScaler tab")),
        "ultra" => ("ultra", L.T("Open Ultra+")),
        "dxvk" => (Dxvk.Section(_d3d == D3dVersion.D3D12), L.T("Open in Mods")),
        _ => ("mods/" + id, L.T("Open in Mods")),
    };

    static StackPanel IconText(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 13 });
        return panel;
    }

    // ---------- each mod's build ----------

    /// <summary>The pickers of one row: the build (and for OptiScaler its source and file name, for DLSS 5 its way).</summary>
    async Task ReadModelAsync()
    {
        string model;
        try { model = await Task.Run(() => File.Exists(DlssNr.StoredPath) ? DlssNr.Version(DlssNr.StoredPath) : ""); }
        catch (Exception ex) { Log.Warn($"Auto: DLSS NR model file: {ex.Message}"); model = ""; }
        if (model == _model) return;
        _model = model;
        Replan();
    }

    /// <summary>The DLSS 5 card's model file (2.0.0-rc.15): what is kept, and a button to choose or change it right here.</summary>
    FrameworkElement ModelPicker()
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        bool has = !string.IsNullOrEmpty(_model);
        line.Children.Add(new TextBlock
        {
            Text = _model is null ? L.T("Reading…") : has ? $"{DlssNr.FileName} {_model}" : L.T("Not chosen yet"),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = has ? 1 : 0.7,
        });
        var pick = new Button { Content = has ? L.T("Change…") : L.T("Choose file…"), IsEnabled = !_running };
        pick.Click += async (_, _) =>
        {
            pick.IsEnabled = false;
            try { if (await _host.AutoPickDlssNrAsync()) await ReadModelAsync(); }
            finally { pick.IsEnabled = !_running; }
        };
        line.Children.Add(pick);
        return Labeled(L.F("Model file ({0})", DlssNr.FileName), line);
    }

    FrameworkElement? Options(AutoItem item)
    {
        if (_plan is not { } plan || _f is not { } f) return null;
        var row = new WrapPanel(10, []) { Margin = new Thickness(0, 6, 0, 0) };
        // The pickers of a mod that can be ticked show before the tick too (2.0.0), so ticking never changes a card's height
        // and the cards around it stay where they are.
        bool live = !_running;
        switch (item.Id)
        {
            case "opti" when Tickable(item):
            {
                var source = new ComboBox { MinWidth = 220, IsEnabled = live && _sources.Count(s => plan.AllowsSource(s.Key)) > 1 };
                foreach (var (key, name) in _sources)
                {
                    if (!plan.AllowsSource(key)) continue;
                    source.Items.Add(new ComboBoxItem { Content = name, Tag = key });
                    if (key.Equals(plan.OptiSource, StringComparison.OrdinalIgnoreCase)) source.SelectedIndex = source.Items.Count - 1;
                }
                if (source.SelectedIndex < 0)
                {
                    // A source not listed (a fork removed since): shown as it is.
                    source.Items.Add(new ComboBoxItem { Content = AutoSetup.SourceName(plan.OptiSource), Tag = plan.OptiSource });
                    source.SelectedIndex = source.Items.Count - 1;
                }
                source.SelectionChanged += (_, _) =>
                {
                    if ((source.SelectedItem as ComboBoxItem)?.Tag is string key && key != plan.OptiSource)
                    {
                        _choice.OptiSource = key;
                        _optiRelease = null;
                        Replan();
                    }
                };
                row.Children.Add(Labeled(L.T("Build"), source));
                // Builds added here as on the OptiScaler tab (2.0.0-rc.18): a folder, an archive, or a fork's GitHub address.
                var add = AddBuildButton();
                add.IsEnabled = live;
                row.Children.Add(Labeled(" ", add));

                var version = new ComboBox { MinWidth = 260, IsEnabled = live };
                if (_releases.TryGetValue(plan.OptiSource, out var list) && list.Count > 0)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        version.Items.Add(new ComboBoxItem { Content = list[i].Display + (i == 0 ? "  ·  " + L.T("newest") : ""), Tag = list[i] });
                        if (ReferenceEquals(list[i], _optiRelease)) version.SelectedIndex = i;
                    }
                    if (version.SelectedIndex < 0) { version.SelectedIndex = 0; _optiRelease = list[0]; }
                }
                else version.Items.Add(new ComboBoxItem { Content = _sourceErrors.TryGetValue(plan.OptiSource, out var err) ? err : L.T("Reading the releases…"), IsEnabled = false });
                version.SelectionChanged += (_, _) => { if ((version.SelectedItem as ComboBoxItem)?.Tag is OptiRelease r) { _optiRelease = r; UpdateButtons(); } };
                row.Children.Add(Labeled(L.T("Version"), version));

                if (item.State == AutoState.Install)
                {
                    var dll = new ComboBox { MinWidth = 170, IsEnabled = live };
                    dll.Items.Add(new ComboBoxItem { Content = L.F("Auto ({0})", plan.OptiDll), Tag = null });
                    dll.SelectedIndex = 0;
                    foreach (var n in AutoSetup.OptiNameChoices(f, f.UltraInstalled || _choice.Ultra == true))
                    {
                        dll.Items.Add(new ComboBoxItem { Content = n, Tag = n });
                        if (n.Equals(_choice.OptiDll, StringComparison.OrdinalIgnoreCase)) dll.SelectedIndex = dll.Items.Count - 1;
                    }
                    dll.SelectionChanged += (_, _) => { _choice.OptiDll = (dll.SelectedItem as ComboBoxItem)?.Tag as string; Replan(); };
                    row.Children.Add(Labeled(L.T("File name"), dll));
                }
                if (plan.OptiSourceWhy is { } fixedWhy) return Stack(row, Ui.Muted(fixedWhy));
                return row;
            }
            case "dlss5" when item.State != AutoState.Install:
                row.Children.Add(ModelPicker());
                return row;
            case "dlss5":
            {
                // The Method list shows before the tick too (picking a method ticks DLSS 5); red only once ticked with none.
                bool ticked = item.Selected || _dlss5Pending;
                var ways = AutoSetup.Dlss5Ways(f.Dlss5Tier).ToList();
                if (f.OptiFile is not null) ways.Remove(AutoSetup.AmdReShade);
                var method = new ComboBox { MinWidth = 320, IsEnabled = !_running, PlaceholderText = L.T("Select one") };
                foreach (var w in ways)
                {
                    method.Items.Add(new ComboBoxItem { Content = AutoSetup.Dlss5Name(w), Tag = w });
                    if (w == plan.Dlss5 && !_dlss5Pending) method.SelectedIndex = method.Items.Count - 1;
                }
                if (method.SelectedIndex < 0 && ticked)
                {
                    // Ticked with nothing picked: the list says so in red, and Install waits for it.
                    var red = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x5A, 0x5A));
                    method.PlaceholderForeground = red;
                    method.BorderBrush = red;
                }
                method.SelectionChanged += (_, _) =>
                {
                    if ((method.SelectedItem as ComboBoxItem)?.Tag is not string w || (w == plan.Dlss5 && !_dlss5Pending)) return;
                    _dlss5Pending = false;
                    _choice.Dlss5 = w;
                    // The method decides OptiScaler's build: a build picked for another method is let go.
                    _choice.OptiSource = null;
                    _optiRelease = null;
                    Replan();
                };
                row.Children.Add(Labeled(L.T("Method"), method));
                row.Children.Add(ModelPicker());
                return row;
            }
            case "reshade" when Tickable(item):
            {
                // The build (2.0.0-rc.13): standard, or the one with full add-on support; picked for you, and fixed, when a ticked
                // mod needs it (RenoDX).
                var build = new ComboBox { MinWidth = 220, IsEnabled = live && !plan.ReShadeAddonNeeded };
                build.Items.Add(new ComboBoxItem { Content = L.T("Standard"), Tag = false });
                build.Items.Add(new ComboBoxItem { Content = L.T("Add-on (full add-on support)"), Tag = true });
                build.SelectedIndex = plan.ReShadeAddon ? 1 : 0;
                ToolTipService.SetToolTip(build, plan.ReShadeAddonNeeded
                    ? L.T("RenoDX needs the add-on build: it is picked for you.")
                    : L.T("Standard is safer in online games with anti-cheat. Add-on is needed by add-ons such as RenoDX."));
                build.SelectionChanged += (_, _) =>
                {
                    if ((build.SelectedItem as ComboBoxItem)?.Tag is not bool addon || addon == plan.ReShadeAddon) return;
                    _choice.ReShadeAddon = addon;
                    Replan();
                };
                row.Children.Add(Labeled(plan.ReShadeAddonNeeded ? L.T("Build (needed by RenoDX)") : L.T("Build"), build));
                if (_reshadeVersions.Count > 0)
                    row.Children.Add(Labeled(L.T("Version"), Enable(Pick(_reshadeVersions, v => v, _reshadeVersion, v => _reshadeVersion = v), live)));
                return row;
            }
            case "renodx" when (item.Acts || item.State == AutoState.Install) && _renoHits.Count > 1:
                // The mods that fit, the game's own first; a generic one says so in the list (2.0.0-rc.18). No "newest": these are
                // different mods, not versions.
                row.Children.Add(Labeled(L.T("Mod"), Pick(_renoHits, m => m.Name + GenericTag(m.Engine, _renoMatches.FirstOrDefault(h => ReferenceEquals(h.Mod, m))?.Score == 70)
                    + (m.Working ? "" : "  ·  " + L.T("in progress")), _renoDx, m => { _renoPick = m.Name; _renoDx = m; _ = LoadAsync(); }, newest: false)));
                return row;
            case "luma" when item.Acts || item.State == AutoState.Install:
                if (_lumaMatches.Count > 1 && _ctx.Settings.Luma is null)
                    row.Children.Add(Labeled(L.T("Mod"), Pick(_lumaMatches.Select(h => h.Mod).ToList(), m => m.Name + GenericTag(m.Engine, _lumaMatches.FirstOrDefault(h => ReferenceEquals(h.Mod, m))?.Score == 80)
                        + (m.Status == LumaStatus.Working ? "" : "  ·  " + L.T("in progress")), _luma, m => { _lumaPick = m.Name; _luma = m; _lumaVersion = null; _ = LoadAsync(); }, newest: false)));
                if (Tickable(item) && _lumaVersions.Count > 0)
                    row.Children.Add(Labeled(L.T("Version"), Enable(Pick(_lumaVersions, v => v.Display, _lumaVersion, v => _lumaVersion = v), live)));
                return row.Children.Count > 0 ? row : null;
            case "ultra" when Tickable(item) && _ultraVersions.Count > 0:
                row.Children.Add(Labeled(L.T("Version"), Enable(Pick(_ultraVersions, v => $"v{v.Version}  ·  {v.Published:d MMM yyyy}" + (v.Beta ? "  ·  " + L.T("beta") : ""), _ultraFile, v => _ultraFile = v), live)));
                return row;
            case "dxvk" when Tickable(item):
            {
                var combo = new ComboBox { MinWidth = 300, IsEnabled = live };
                if (_dxvkReleases.Count == 0)
                {
                    // Its releases are read once it is ticked (one GitHub request less for a layer most games never get).
                    combo.Items.Add(new ComboBoxItem { Content = item.Acts ? L.T("Reading the releases…") : L.T("Read when ticked"), IsEnabled = false });
                    combo.SelectedIndex = 0;
                    if (item.Acts) _ = LoadDxvkAsync();
                }
                foreach (var r in _dxvkReleases)
                {
                    combo.Items.Add(new ComboBoxItem { Content = $"{Dxvk.Name(r.Flavor)} {r.Display}", Tag = r });
                    if (ReferenceEquals(r, _dxvkRelease)) combo.SelectedIndex = combo.Items.Count - 1;
                }
                if (combo.SelectedIndex < 0 && _dxvkReleases.Count > 0) { combo.SelectedIndex = 0; _dxvkRelease = _dxvkReleases[0]; }
                combo.SelectionChanged += (_, _) => { if ((combo.SelectedItem as ComboBoxItem)?.Tag is DxvkRelease r) _dxvkRelease = r; };
                row.Children.Add(Labeled(L.F("Layer for {0}", Dxvk.Name(_d3d)), combo));
                return row;
            }
        }
        return null;
    }

    /// <summary>
    /// "Add a build" beside OptiScaler's build (2.0.0-rc.18): a build folder or archive imported, or a fork added by its GitHub
    /// address, the same way as the OptiScaler tab (<see cref="OptiSourceImport"/>); the new build is then picked here.
    /// </summary>
    Button AddBuildButton()
    {
        var button = new Button { Content = IconText("", L.T("Add a build")), IsEnabled = !_running };
        ToolTipService.SetToolTip(button, L.T("Import a build (a folder or an archive, e.g. one shared on Discord) or add a fork by its GitHub address, as on the OptiScaler tab"));
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        MenuFlyoutItem Item(string text, string glyph, Func<Task> act)
        {
            var it = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            it.Click += async (_, _) =>
            {
                try { await act(); }
                catch (Exception ex) { App.Window?.ShowError(L.T("Could not import the build"), ex.Message, ex); }
            };
            return it;
        }
        menu.Items.Add(Item(L.T("From a folder…"), "", async () => await UseCustomAsync(await OptiSourceImport.ImportFolderAsync())));
        menu.Items.Add(Item(L.T("From an archive (.zip, .7z, .rar)…"), "", async () => await UseCustomAsync(await OptiSourceImport.ImportArchiveAsync())));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("From a GitHub fork…"), "", async () =>
        {
            if (await OptiSourceImport.AddForkAsync(XamlRoot, ActualTheme, _ctx.Token) is not { } added) return;
            await LoadSourcesAsync(_ctx.Token);
            var key = "fork:" + added.Repo;
            _releases[key] = added.Releases;
            _sourceErrors.Remove(key);
            Use(key);
        }));
        button.Flyout = menu;
        _addBuild = button;
        return button;
    }

    Button? _addBuild;

    /// <summary>Self-test: the "Add a build" menu opened (true) or closed; how many entries it has, or -1 when OptiScaler's row has none.</summary>
    internal int SelfTestAddBuildMenu(bool open)
    {
        if (_addBuild?.Flyout is not MenuFlyout menu || _addBuild.XamlRoot is null) return -1;
        if (open) menu.ShowAt(_addBuild); else menu.Hide();
        return menu.Items.OfType<MenuFlyoutItem>().Count();
    }

    /// <summary>An imported build picked as OptiScaler's source in this tab.</summary>
    async Task UseCustomAsync(CustomBuild? build)
    {
        if (build is null) return;
        await LoadSourcesAsync(_ctx.Token);
        Use("custom:" + build.Name);
    }

    void Use(string key)
    {
        _choice.OptiSource = key;
        _optiRelease = null;
        Replan();
        // A build the DLSS 5 method does not take is said in Conflicts handled; the plan keeps the one it needs.
    }

    /// <summary>"  ·  GENERIC, …" after a generic mod's name in a list (2.0.0-rc.18), in capitals so it is not missed.</summary>
    static string GenericTag(string? engine, bool tested) => engine is null ? ""
        : "  ·  " + (tested ? L.T("GENERIC, tried with this game") : L.T("GENERIC, not made for this game"));

    /// <summary>The engine of the RenoDX or Luma mod picked for a row, when it is a generic one (and not already in).</summary>
    string? GenericOf(string id) => id switch
    {
        "renodx" when _f?.RenoDxInstalled != true => _renoDx?.Engine,
        "luma" when _f?.LumaInstalled != true => _luma?.Engine,
        _ => null,
    };

    ComboBox Pick<T>(IReadOnlyList<T> items, Func<T, string> text, T? selected, Action<T> picked, bool newest = true) where T : class
    {
        var combo = new ComboBox { MinWidth = 260, IsEnabled = !_running };
        for (int i = 0; i < items.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = text(items[i]) + (newest && i == 0 ? "  ·  " + L.T("newest") : ""), Tag = items[i] });
            if (ReferenceEquals(items[i], selected) || Equals(items[i], selected)) combo.SelectedIndex = i;
        }
        if (combo.SelectedIndex < 0 && items.Count > 0) combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) => { if ((combo.SelectedItem as ComboBoxItem)?.Tag is T t) picked(t); };
        return combo;
    }

    static ComboBox Enable(ComboBox combo, bool on)
    {
        combo.IsEnabled = on;
        return combo;
    }

    static StackPanel Labeled(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, Style = Ui.Style("RowDescription") });
        panel.Children.Add(control);
        return panel;
    }

    static StackPanel Stack(params FrameworkElement[] items)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var i in items) panel.Children.Add(i);
        return panel;
    }

    /// <summary>The releases of the plan's OptiScaler source read (once), then the row redrawn with them.</summary>
    async Task PickReleaseAsync(string key)
    {
        if (_releases.ContainsKey(key)) { if (_optiRelease is null) { _optiRelease = _releases[key].FirstOrDefault(); UpdateButtons(); } return; }
        await ReleasesAsync(key);
        if (_plan?.OptiSource != key) return;
        _optiRelease = _releases[key].FirstOrDefault();
        Replan();
    }

    async Task LoadDxvkAsync()
    {
        var flavors = _d3d == D3dVersion.D3D12 ? new[] { DxvkFlavor.Vkd3d } : [DxvkFlavor.Dxvk, DxvkFlavor.GplAsync];
        var all = new List<DxvkRelease>();
        foreach (var fl in flavors) all.AddRange((await SafeList(() => Dxvk.GetReleasesAsync(fl, ct: _ctx.Token))).Take(6));
        _dxvkReleases = all;
        _dxvkRelease = all.FirstOrDefault();
        if (_dxvkReleases.Count > 0) Replan();
    }

    // ---------- self-test ----------

    /// <summary>Self-test: the plan as "opti:Install[x] reshade:Install[ ] …", or why there is none yet.</summary>
    internal string SelfTestDescribe() => _running ? "running" : _plan is null ? "not read"
        : string.Join(" ", _plan.Items.Select(i => $"{i.Id}:{i.State}[{(i.Selected ? "x" : " ")}]"))
          + $" opti-as={_plan.OptiDll} source={_plan.OptiSource} build={_optiRelease?.Tag ?? "-"} reshade-as={_plan.ReShadeDll} reshade-build={(_plan.ReShadeAddon ? "addon" : "standard")} dlss5={(_dlss5Pending ? "pending" : _plan.Dlss5)}"
          + $" renodx-generic={_renoDx?.Engine is not null} luma-generic={_luma?.Engine is not null} renodx-mod=\"{_renoDx?.Name}\" luma-mod=\"{_luma?.Name}\"";

    /// <summary>Self-test: DLSS 5 ticked with no method yet; Install must wait for one.</summary>
    internal string SelfTestDlss5Tick()
    {
        _choice.Dlss5 = AutoSetup.Off;
        _dlss5Pending = true;
        Replan();
        return SelfTestDescribe() + $" install-enabled={_go.IsEnabled}";
    }

    /// <summary>Self-test: ReShade ticked with its build picked (null: back to the recommendation).</summary>
    internal string SelfTestReShadeBuild(bool? addon)
    {
        _choice.ReShade = addon is null ? null : true;
        _choice.ReShadeAddon = addon;
        Replan();
        return SelfTestDescribe();
    }

    /// <summary>Self-test: picks DLSS 5's way (OptiScaler's build follows) and reports the plan.</summary>
    internal string SelfTestDlss5(string way)
    {
        _dlss5Pending = false;
        _choice.Dlss5 = way;
        _choice.OptiSource = null;
        _optiRelease = null;
        Replan();
        return SelfTestDescribe();
    }

    /// <summary>Self-test: picks OptiScaler's source by hand.</summary>
    internal void SelfTestSource(string key)
    {
        _choice.OptiSource = key;
        _optiRelease = null;
        Replan();
    }

    /// <summary>Self-test: back to the recommendation, ReShade ticked on top, and the one click (on a fake game only).</summary>
    internal async Task SelfTestRunAsync()
    {
        _choice.Dlss5 = null;
        _choice.OptiSource = null;
        _choice.ReShade = true;
        _optiRelease = null;
        Replan();
        for (int i = 0; i < 100 && _plan?["opti"] is { Acts: true } && _optiRelease is null; i++) await Task.Delay(200);
        await RunAsync();
    }

    // ---------- the one click ----------

    async Task RunAsync()
    {
        if (_running || _plan is not { } plan || _f is not { } f) return;
        var dir = _ctx.TargetDir();
        if (dir is null) return;
        if (OptiInstaller.RunningGameProcess(dir) is { } running)
        {
            App.Window?.ShowError(L.T("Close the game first"), L.F("{0} is running from this folder.", running));
            return;
        }
        if (GameJobs.InTheWay(_ctx.Game.Id, _ctx.Game.Title)) return;
        if (!OptiInstaller.CanWrite(dir))
        {
            App.Window?.ShowError(L.T("The game folder is protected"), L.T("Hybrid Launcher cannot write there without administrator rights. Install OptiScaler from its tab once to restart as administrator, then come back."));
            return;
        }

        var settings = _ctx.Settings;
        var results = new List<(string Id, string Name, bool Ok, string Text)>();
        // The order matters: the Vulkan layer alone; Ultra+ (OptiScaler renamed out of its way first); DLSS 5's package before
        // OptiScaler, so the install lays it over; OptiScaler; DLSS 5's own ways; then ReShade and the HDR mods on top.
        var steps = new List<(string Id, string Name, Func<Task<(bool Ok, string Text)>> Run)>();
        var item = (string id) => plan[id]!;
        var optiRelease = _optiRelease;
        if (item("dxvk").Acts) steps.Add(("dxvk", "DXVK", () => InstallDxvkAsync(f, dir)));
        if (plan.OptiRename is { } rename && item("ultra").Acts)
            steps.Add(("opti", L.T("OptiScaler's file name"), async () => await _host.AutoRenameOptiAsync(rename) ? (true, L.F("Now {0}", rename)) : (false, L.T("Not renamed"))));
        if (item("ultra").Acts) steps.Add(("ultra", "Ultra+", () => InstallUltraAsync()));
        bool dlss5 = item("dlss5").Acts;
        if (dlss5 && plan.Dlss5 is AutoSetup.Amd or AutoSetup.AmdReShade)
            steps.Add(("dlss5", L.T("DLSS 5 package"), async () => await _host.AutoPrepareAmdAsync() ? (true, L.T("DLSSNR-AMD prepared")) : (false, L.T("Not prepared: DLSS 5 is skipped"))));
        if (item("opti").Acts)
            steps.Add(("opti", "OptiScaler", async () =>
            {
                if (optiRelease is null) return (false, L.T("No build picked"));
                if (dlss5 && plan.Dlss5 == AutoSetup.F5) settings.Dlss5Method = Dlss5.F5;
                if (dlss5 && plan.Dlss5 == AutoSetup.Amd && DlssNrAmd.Stage() is not null) settings.Dlss5Method = Dlss5.Amd;
                bool ok = await InstallOptiAsync(plan, dir, optiRelease);
                return ok ? (true, L.F("{0} in as {1}", settings.Install?.DisplayVersion ?? optiRelease.Tag, settings.Install?.DllName ?? plan.OptiDll)) : (false, L.T("Not installed (see the message above)"));
            }));
        if (dlss5)
            steps.Add(("dlss5", "DLSS 5", async () =>
            {
                switch (plan.Dlss5)
                {
                    case AutoSetup.F5:
                        if (settings.Install is not { } m || !DlssNr.NeededBy(m)) return (false, L.T("The F5 build is not in"));
                        await _host.AutoFinishF5Async();
                        return (true, Dlss5Name(plan.Dlss5));
                    case AutoSetup.Amd:
                        if (DlssNrAmd.Stage() is null) return (false, L.T("DLSSNR-AMD is not prepared"));
                        if (settings.Install?.DlssNrAmd is not null || await _host.AutoPutAmdAsync()) return (true, Dlss5Name(plan.Dlss5));
                        return (false, L.T("Not laid over OptiScaler (see the message above)"));
                    case AutoSetup.AmdReShade:
                        if (DlssNrAmd.Stage() is null) return (false, L.T("DLSSNR-AMD is not prepared"));
                        settings.Dlss5AmdWay = "reshade";
                        return await _host.AutoPutAmdReShadeAsync() ? (true, Dlss5Name(plan.Dlss5)) : (false, L.T("Not put in (see the message above)"));
                }
                return (false, "");
            }));
        if (item("reshade").Acts)
            steps.Add(("reshade", "ReShade", async () =>
            {
                bool optiNow = OptiDetector.Detect(dir) is not null;
                if (plan.ReShadeThroughOpti && !optiNow) return (false, L.T("Skipped: it was to be loaded by OptiScaler, which did not go in"));
                return (true, await InstallReShadeAsync(plan, f, dir));
            }));
        if (item("renodx").Acts) steps.Add(("renodx", "RenoDX", async () => (true, await InstallRenoDxAsync(f, dir))));
        if (item("luma").Acts) steps.Add(("luma", "Luma", async () => (true, await InstallLumaAsync(dir))));

        _running = true;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        Replan();
        GameJobs.Progress += OnJob;
        // What a step's own install said when it failed (its error message), kept as that step's reason (2.0.0-rc.10): the
        // last run said only "see the message above", and the message was gone by the time the list was read.
        var said = new List<string>();
        void OnError(string title, string message) => said.Add(title.Length > 0 && message.Length > 0 ? title + ": " + message : title + message);
        if (App.Window is { } window) window.ErrorShown += OnError;
        try
        {
            int n = 0;
            foreach (var (id, name, run) in steps)
            {
                n++;
                _status.Text = L.F("{0} of {1}: {2}…", n, steps.Count, name);
                _progress.IsIndeterminate = true;
                said.Clear();
                try
                {
                    var (ok, text) = await run();
                    results.Add((id, name, ok, ok ? text : Reason(text, said)));
                }
                catch (OperationCanceledException) { results.Add((id, name, false, L.T("Cancelled"))); break; }
                catch (Exception ex)
                {
                    Log.Error($"Auto tab: {name} failed", ex);
                    results.Add((id, name, false, Reason(ex.Message, said)));
                }
            }
            if (App.Window is { } w2) w2.ErrorShown -= OnError;
            SettingsStore.Save();
            int failed = results.Count(r => !r.Ok);
            if (failed == 0) App.Window?.ShowSuccess(L.F("Done: {0} set up in {1}.", string.Join(", ", results.Select(r => r.Name)), _ctx.Game.Title));
            else App.Window?.ShowError(L.T("Some mods did not go in"), string.Join("\n", results.Where(r => !r.Ok).Select(r => $"{r.Name}: {r.Text}")) + "\n" + L.T("Last run, under the plan, says why and opens each mod's tab."));
        }
        finally
        {
            if (App.Window is { } w3) w3.ErrorShown -= OnError;
            GameJobs.Progress -= OnJob;
            _running = false;
            _progress.Visibility = Visibility.Collapsed;
            _status.Text = "";
            ShowResults(results);
            // What the run asked for is done: the next plan starts from the recommendation again.
            _choice.Opti = _choice.ReShade = _choice.RenoDx = _choice.Luma = _choice.Ultra = _choice.Dxvk = null;
            _choice.Dlss5 = null;
            _choice.OptiDll = null;
            _ctx.OptiChanged();
            await LoadAsync();
        }
    }

    static string Dlss5Name(string way) => AutoSetup.Dlss5Name(way);

    void OnJob(GameJobs.Job job)
    {
        if (!job.GameId.Equals(_ctx.Game.Id, StringComparison.OrdinalIgnoreCase)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_running) return;
            _progress.IsIndeterminate = job.Fraction < 0;
            if (job.Fraction >= 0) _progress.Value = job.Fraction;
        });
    }

    /// <summary>A failed step's reason: what the step knows, with what its install said in its error message (the "see the message above" left out).</summary>
    static string Reason(string text, List<string> said)
    {
        var own = text.Replace(" " + L.T("(see the message above)"), "").Replace(L.T("(see the message above)"), "").Trim();
        var extra = said.Where(m => m.Length > 0 && !own.Contains(m, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        if (extra.Count == 0) return own.Length > 0 ? own : L.T("It stopped without saying why: the log has the details.");
        return (own.Length > 0 ? own + ". " : "") + string.Join(" · ", extra);
    }

    void ShowResults(List<(string Id, string Name, bool Ok, string Text)> results)
    {
        _results.Children.Clear();
        foreach (var r in results)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(ModUi.Check(r.Ok, r.Name, r.Text));
            // A step that did not go in: its tab, where it can be installed by hand with every option.
            if (!r.Ok)
            {
                var (target, label) = TabOf(r.Id);
                var open = Ui.LinkButton(label, "\uE8A7", () => _ctx.ShowTab(target));
                open.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(open, 1);
                line.Children.Add(open);
            }
            _results.Children.Add(line);
        }
        if (results.Any(r => r.Ok))
            _results.Children.Add(Ui.Muted(L.T("Start the game: Insert opens OptiScaler's menu, Home opens ReShade.")));
        _resultsCard.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// OptiScaler through the page's own install (same backups, conflicts, DLSS NR model and job), with the build, name and
    /// spoofing picked. A ReShade in the way is renamed to ReShade64.dll first, and LoadReshade goes into the game's ini
    /// choices so the install sets it.
    /// </summary>
    async Task<bool> InstallOptiAsync(AutoPlan plan, string dir, OptiRelease release)
    {
        var settings = _ctx.Settings;
        if (plan.MoveReShadeFirst is { } from)
        {
            var src = Path.Combine(dir, from);
            var dest = Path.Combine(dir, ReShade.OptiDllName);
            if (File.Exists(src) && ReShade.IsReShade(src))
            {
                if (File.Exists(dest) && ReShade.IsReShade(dest)) File.Delete(dest);
                if (!File.Exists(dest))
                {
                    File.Move(src, dest);
                    settings.ReShade.RenamedFrom = from;
                    settings.ReShade.RestoredFromBackup = false;
                }
            }
        }
        if (plan.ReShadeThroughOpti || plan.MoveReShadeFirst is not null || ReShade.Detect(dir) is { LoadedByOpti: true }) settings.IniOverrides[ReShade.IniKey] = "true";
        SettingsStore.Save();
        return await _host.AutoInstallOptiAsync(release, plan.OptiDll, plan.Spoofing);
    }

    async Task<string> InstallReShadeAsync(AutoPlan plan, AutoFacts f, string dir)
    {
        var job = GameJobs.TryStart(_ctx.Game.Id, _ctx.Game.Title, "reshade", "ReShade") ?? throw new InvalidOperationException(L.T("Another install is running in this game."));
        try
        {
            var version = await ReShade.InstallDirectAsync(dir, plan.ReShadeDll, plan.ReShadeAddon, f.Is64, new Progress<double>(p => GameJobs.Report(job, p)), job.Token, _reshadeVersion);
            var s = _ctx.Settings;
            s.ReShade.Placed = plan.ReShadeDll;
            s.ReShade.Addon = plan.ReShadeAddon;
            s.ReShade.InstallerVersion = version;
            s.ReShade.RenamedFrom = null;
            s.ReShade.RestoredFromBackup = false;
            if (plan.ReShadeThroughOpti)
            {
                s.IniOverrides[ReShade.IniKey] = "true";
                var ini = Path.Combine(dir, "OptiScaler.ini");
                if (File.Exists(ini))
                {
                    var doc = IniDocument.Load(ini);
                    if (doc.Set(ReShade.IniKey, "true")) doc.Save();
                }
            }
            SettingsStore.Save();
            return plan.ReShadeThroughOpti
                ? L.F("ReShade {0} in as {1}, loaded by OptiScaler", version, plan.ReShadeDll)
                : L.F("ReShade {0} in as {1}", version, plan.ReShadeDll);
        }
        finally { GameJobs.End(job); }
    }

    async Task<string> InstallRenoDxAsync(AutoFacts f, string dir)
    {
        var link = _renoDx?.Snapshot(f.Is64) ?? throw new InvalidOperationException(L.T("The RenoDX mod has no direct download."));
        var job = GameJobs.TryStart(_ctx.Game.Id, _ctx.Game.Title, "renodx", "RenoDX") ?? throw new InvalidOperationException(L.T("Another install is running in this game."));
        try
        {
            var path = await RenoDx.InstallSnapshotAsync(link, dir, new Progress<double>(p => GameJobs.Report(job, p)), job.Token);
            _ctx.Settings.RenoDxFiles[Path.GetFileName(path)] = "snapshot " + DateTime.Now.ToString("d MMM yyyy");
            SettingsStore.Save();
            return L.F("{0} in", Path.GetFileName(path));
        }
        finally { GameJobs.End(job); }
    }

    async Task<string> InstallLumaAsync(string dir)
    {
        if (_luma is not { } mod || _lumaVersion is not { } version) throw new InvalidOperationException(L.T("No Luma build to install."));
        if (await ModBackups.DecideAsync("Luma", _ctx.Game.Title) is not { } backup) throw new OperationCanceledException();
        bool opti = File.Exists(Path.Combine(dir, "OptiScaler.ini"));
        var job = GameJobs.TryStart(_ctx.Game.Id, _ctx.Game.Title, "luma", "Luma") ?? throw new InvalidOperationException(L.T("Another install is running in this game."));
        try
        {
            var state = await Luma.InstallAsync(mod, version, dir, opti, _ctx.Settings.Luma, new Progress<double>(p => GameJobs.Report(job, p)), job.Token, backup);
            _ctx.Settings.Luma = state;
            if (opti) _ctx.Settings.IniOverrides[ReShade.IniKey] = "true";
            SettingsStore.Save();
            return L.F("{0} in ({1})", mod.Name, version.Display);
        }
        finally { GameJobs.End(job); }
    }

    async Task<(bool, string)> InstallUltraAsync()
    {
        if (_ultraGame is not { } ultra || _ultraFile is not { } file) return (false, L.T("No Ultra+ build to install."));
        bool ok = await UltraPlusLibrary.InstallAsync(_ctx.Game, ultra, file, ownUe4ss: _ctx.Settings.UltraPlus?.OwnUe4ss ?? false, null, _ctx.Token);
        return ok ? (true, L.F("Ultra+ {0} in", file.Version)) : (false, L.T("Not installed (see the message above)"));
    }

    async Task<(bool, string)> InstallDxvkAsync(AutoFacts f, string dir)
    {
        if (_dxvkRelease is not { } release) return (false, L.T("No release picked"));
        var version = Dxvk.Handles(release.Flavor, _d3d) ? _d3d : release.Flavor == DxvkFlavor.Vkd3d ? D3dVersion.D3D12 : D3dVersion.D3D11;
        var files = Dxvk.Files(release.Flavor, version).Concat(Dxvk.NeedsDxgi(release.Flavor) ? ["dxgi.dll"] : Array.Empty<string>()).ToList();
        if (Dxvk.Blockers(dir, files, _ctx.Settings.Dxvk) is { Count: > 0 } blockers)
            return (false, L.F("{0} belongs to {1}", blockers[0].File, blockers[0].Owner));
        if (await ModBackups.DecideAsync(Dxvk.Name(release.Flavor), _ctx.Game.Title) is not { } backup) throw new OperationCanceledException();
        DxvkRelease? dxgi = Dxvk.NeedsDxgi(release.Flavor) ? (await SafeList(() => Dxvk.GetReleasesAsync(DxvkFlavor.Dxvk, ct: _ctx.Token))).FirstOrDefault() : null;
        var job = GameJobs.TryStart(_ctx.Game.Id, _ctx.Game.Title, "dxvk", Dxvk.Name(release.Flavor)) ?? throw new InvalidOperationException(L.T("Another install is running in this game."));
        try
        {
            var state = await Dxvk.InstallAsync(release, version, dir, f.Is64, _ctx.Settings.Dxvk, dxgi, new Progress<double>(p => GameJobs.Report(job, p)), job.Token, backup);
            _ctx.Settings.Dxvk = state;
            SettingsStore.Save();
            return (true, L.F("{0} {1} in", Dxvk.Name(release.Flavor), release.Version));
        }
        finally { GameJobs.End(job); }
    }
}
