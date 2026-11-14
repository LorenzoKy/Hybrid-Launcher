using HybridLauncher.Core;
using HybridLauncher.Core.Media;
using HybridLauncher.Core.Mods;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The DXVK and VKD3D-Proton sections of the Mods tab (one view each since 1.4.0-rc.18): DXVK or DXVK-gplasync (Direct3D 8 to 11),
/// or VKD3D-Proton (Direct3D 12) for this game. A game holds one layer at a time (GameSettings.Dxvk): installing one replaces the
/// other, and each section says when the other one is in. The Direct3D version is taken from
/// the game's graphics API (PCGamingWiki, else the exe's imports) and can be changed; the right dlls for the game's bitness go
/// next to its exe, with a backup of every file they replace. A few dxvk.conf settings (async, HDR, a frame cap) are switches.
/// </summary>
public sealed partial class DxvkView : UserControl
{
    public const string Glyph = "";
    public static Color Tint => Palettes.Adapt(Color.FromArgb(0xFF, 0xE8, 0x4A, 0x5F));
    public static Color VkTint => Palettes.Adapt(Color.FromArgb(0xFF, 0xB8, 0x4A, 0xE8));

    /// <summary>This view is VKD3D-Proton's section (else DXVK's: DXVK and DXVK-gplasync).</summary>
    readonly bool _vkd3d;
    string Title => _vkd3d ? "VKD3D-Proton" : "DXVK";
    Color MyTint => _vkd3d ? VkTint : Tint;
    /// <summary>The layers this section installs.</summary>
    bool Mine(DxvkFlavor f) => (f == DxvkFlavor.Vkd3d) == _vkd3d;
    /// <summary>What Hybrid Launcher installed in this game when it is one of this section's layers.</summary>
    DxvkState? Own => _ctx.Settings.Dxvk is { } s && Mine(s.Flavor) ? s : null;

    /// <summary>The layers of this section found in the folder (DXVK's dxgi.dll beside VKD3D-Proton counts for VKD3D-Proton).</summary>
    List<DetectedLayer> FoundHere(string dir)
    {
        var all = Core.Mods.Dxvk.Detect(dir);
        bool vk = all.Any(l => l.Layer == "VKD3D-Proton");
        return _vkd3d ? (vk ? all : []) : all.Where(l => l.Layer == "DXVK" && !(vk && l.File == "dxgi.dll")).ToList();
    }

    readonly ModContext _ctx;
    readonly StackPanel _state = new() { Spacing = 8 };
    readonly ComboBox _layer = new() { MinWidth = 220 };
    readonly ComboBox _api = new() { MinWidth = 180 };
    readonly ComboBox _version = new() { MinWidth = 260 };
    readonly TextBlock _hint;
    readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed };
    readonly Button _install, _uninstall;
    readonly StackPanel _options = new() { Spacing = 10 };
    readonly Border _optionsCard;
    readonly Dictionary<DxvkFlavor, List<DxvkRelease>> _releases = [];
    List<D3dVersion> _gameApis = [];
    string _apiSource = "";
    bool _loaded, _filling;
    bool Busy => GameJobs.Busy(_ctx.Game.Id);

    public DxvkView(ModContext ctx, bool vkd3d = false)
    {
        _ctx = ctx;
        _vkd3d = vkd3d;
        _hint = Ui.Muted("");
        _hint.TextWrapping = TextWrapping.Wrap;

        var head = new StackPanel { Spacing = 10 };
        head.Children.Add(Ui.Heading(Glyph, Title, Ui.LinkButton("GitHub", "", () => Ui.Open(Core.Mods.Dxvk.Page(Selected))),
            subtitle: _vkd3d ? L.T("Direct3D 12 runs through Vulkan") : L.T("Direct3D 8 to 11 run through Vulkan; DXVK-gplasync compiles shaders in the background"), tint: MyTint));
        head.Children.Add(_state);

        var pick = new StackPanel { Spacing = 10 };
        pick.Children.Add(Ui.Heading("", L.T("Install"), subtitle: _vkd3d ? L.T("The release") : L.T("The layer, the game's Direct3D version and the release"), tint: Ui.Pink));
        var row = new WrapPanel(10, []);
        // VKD3D-Proton has one layer and one Direct3D version: only its release is picked.
        if (!_vkd3d)
        {
            row.Children.Add(Labeled(L.T("Layer"), _layer));
            row.Children.Add(Labeled(L.T("Direct3D version"), _api));
        }
        row.Children.Add(Labeled(L.T("Release"), _version));
        pick.Children.Add(row);
        pick.Children.Add(_hint);
        pick.Children.Add(_progress);
        var buttons = new WrapPanel(8, []);
        _install = ModUi.Action(L.T("Install"), "", () => _ = InstallAsync(), accent: true);
        _uninstall = ModUi.Action(L.T("Uninstall"), "", () => _ = UninstallAsync());
        buttons.Children.Add(_install);
        buttons.Children.Add(_uninstall);
        pick.Children.Add(buttons);
        foreach (var f in Enum.GetValues<DxvkFlavor>().Where(Mine)) _layer.Items.Add(new ComboBoxItem { Content = Core.Mods.Dxvk.Name(f), Tag = f });
        _layer.SelectionChanged += (_, _) => { if (!_filling) FillApis(); };
        _api.SelectionChanged += (_, _) => { if (!_filling) UpdateHint(); };
        _version.SelectionChanged += (_, _) => { if (!_filling) UpdateHint(); };
        _version.Items.Add(new ComboBoxItem { Content = L.T("Loading releases…"), IsEnabled = false });
        _version.SelectedIndex = 0;

        var opts = new StackPanel { Spacing = 10 };
        opts.Children.Add(Ui.Heading("", L.T("Settings"), Ui.LinkButton(L.T("Open dxvk.conf"), "", OpenConf), subtitle: L.T("A few lines of dxvk.conf in the game folder"), tint: Palettes.Adapt(Color.FromArgb(0xFF, 0x5F, 0xA8, 0xFF))));
        opts.Children.Add(_options);
        _optionsCard = Ui.Card(opts);

        var tips = new StackPanel { Spacing = 6 };
        tips.Children.Add(Ui.Heading("", L.T("Good to know"), tint: Ui.Slate));
        var tipList = _vkd3d
            ? new[]
            {
                "• VKD3D-Proton is made for Linux; on Windows it is mostly for testing, or for a Direct3D 12 game whose own renderer misbehaves on this card.",
                "• It needs DXVK's dxgi.dll, which goes in with it. A game holds one layer at a time: VKD3D-Proton replaces DXVK, and the other way round.",
                "• OptiScaler and ReShade (as dxgi.dll, d3d12.dll…) cannot share those file names: Hybrid Launcher does not replace them. ReShade can hook Vulkan instead (pick Vulkan in its installer).",
                "• Uninstall puts back every file the layer replaced, and removes a dxvk.conf Hybrid Launcher made.",
            }
            : new[]
            {
                "• On Windows these layers help older games most: Direct3D 9 games that stutter or run slowly on AMD and Intel cards, and games whose Direct3D renderer has bugs. A game that already runs well rarely gains.",
                "• DXVK-gplasync compiles shaders in the background, so the first time an effect shows the frame does not wait for it. Use it in single-player games: its async mode is something anti-cheat systems can object to.",
                "• OptiScaler and ReShade (as dxgi.dll, d3d11.dll…) cannot share those file names with DXVK: Hybrid Launcher does not replace them. ReShade can hook Vulkan instead (pick Vulkan in its installer).",
                "• Uninstall puts back every file the layer replaced, and removes a dxvk.conf Hybrid Launcher made.",
            };
        foreach (var t in tipList)
            tips.Children.Add(Ui.Muted(L.T(t)));

        var main = new StackPanel { Spacing = 12 };
        main.Children.Add(Ui.Card(head));
        main.Children.Add(Ui.Card(pick));
        main.Children.Add(_optionsCard);
        var side = new StackPanel { Spacing = 12 };
        side.Children.Add(Ui.Card(tips));
        Content = ModUi.TwoColumns(main, side);
        _ = LoadAsync();
    }

    static StackPanel Labeled(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, Style = Ui.Style("RowDescription") });
        panel.Children.Add(control);
        return panel;
    }

    DxvkFlavor Selected => _vkd3d ? DxvkFlavor.Vkd3d : (_layer.SelectedItem as ComboBoxItem)?.Tag is DxvkFlavor f ? f : DxvkFlavor.Dxvk;
    D3dVersion? SelectedApi => (_api.SelectedItem as ComboBoxItem)?.Tag is D3dVersion v ? v : null;
    DxvkRelease? SelectedRelease => (_version.SelectedItem as ComboBoxItem)?.Tag as DxvkRelease;

    string? Exe => Core.Detection.GameScanner.EffectiveExe(_ctx.Game, _ctx.Settings);

    async Task LoadAsync()
    {
        try
        {
            var exe = Exe;
            var api = _ctx.Game.NotInstalled ? GraphicsApiInfo.Unknown : await GraphicsApi.GetAsync(_ctx.Game, exe, _ctx.SteamAppId(), _ctx.Token);
            _gameApis = Core.Mods.Dxvk.FromApis(api.Apis);
            _apiSource = api.IsKnown ? api.Display + "  ·  " + L.T(api.Source) : "";
            // VKD3D-Proton also needs DXVK's releases (its dxgi.dll comes from the newest DXVK).
            var tasks = Enum.GetValues<DxvkFlavor>().Where(f => Mine(f) || f == DxvkFlavor.Dxvk).ToDictionary(f => f, f => Core.Mods.Dxvk.GetReleasesAsync(f, ct: _ctx.Token));
            foreach (var (f, t) in tasks) _releases[f] = await t;
            if (_ctx.Token.IsCancellationRequested) return;
            _loaded = true;
            // A game known to draw with none of this section's Direct3D versions has nothing to gain: the section folds to one line.
            bool fits = _gameApis.Any(v => _vkd3d ? v == D3dVersion.D3D12 : v != D3dVersion.D3D12);
            if (api.IsKnown && !fits && Own is null && FoundHere(_ctx.TargetDir() ?? "").Count == 0 && Content is FrameworkElement full)
            {
                var why = _gameApis.Count == 0 ? L.F("{0} draws with {1}: DXVK and VKD3D-Proton are for Direct3D games.", _ctx.Game.Title, api.Display)
                    : _vkd3d ? L.F("{0} draws with {1}: VKD3D-Proton is for Direct3D 12 games.", _ctx.Game.Title, api.Display)
                    : L.F("{0} draws with {1}: DXVK is for Direct3D 8 to 11 games.", _ctx.Game.Title, api.Display);
                ModUi.Compact(this, full, Glyph, MyTint, Title, why, _vkd3d ? Core.Mods.Dxvk.Page(DxvkFlavor.Vkd3d) : Core.Mods.Dxvk.DxvkPage);
                return;
            }
            var flavor = Own?.Flavor ?? (_vkd3d ? DxvkFlavor.Vkd3d : DxvkFlavor.Dxvk);
            _filling = true;
            _layer.SelectedIndex = Math.Max(0, _layer.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => i.Tag is DxvkFlavor f && f == flavor));
            _filling = false;
            FillApis();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"DXVK section: {ex.Message}"); }
        Refresh();
    }

    /// <summary>The Direct3D versions the chosen layer can run (the game's own first), then its releases.</summary>
    void FillApis()
    {
        _filling = true;
        var flavor = Selected;
        _api.Items.Clear();
        var ownApis = _gameApis.Where(v => Core.Mods.Dxvk.Handles(flavor, v)).ToList();
        var want = _ctx.Settings.Dxvk is { } s && Core.Mods.Dxvk.Handles(flavor, s.Api) ? s.Api
            : ownApis.Count > 0 ? ownApis[0]
            : flavor == DxvkFlavor.Vkd3d ? D3dVersion.D3D12 : D3dVersion.D3D11;
        foreach (var v in Enum.GetValues<D3dVersion>().Where(v => Core.Mods.Dxvk.Handles(flavor, v)).Reverse())
        {
            var label = Core.Mods.Dxvk.Name(v) + (_gameApis.Contains(v) ? "  ·  " + L.T("the game's") : "");
            _api.Items.Add(new ComboBoxItem { Content = label, Tag = v });
            if (v == want) _api.SelectedIndex = _api.Items.Count - 1;
        }
        _version.Items.Clear();
        var list = _releases.TryGetValue(flavor, out var r) ? r : [];
        foreach (var rel in list.Take(12)) _version.Items.Add(new ComboBoxItem { Content = rel.Display, Tag = rel });
        if (list.Count == 0) _version.Items.Add(new ComboBoxItem { Content = _loaded ? L.T("No release found (offline?)") : L.T("Loading releases…"), IsEnabled = false });
        int keep = _ctx.Settings.Dxvk is { } st && st.Flavor == flavor ? list.FindIndex(x => x.Version == st.Version) : -1;
        _version.SelectedIndex = keep >= 0 && keep < 12 ? keep : 0;
        _filling = false;
        UpdateHint();
    }

    void UpdateHint()
    {
        var flavor = Selected;
        var api = SelectedApi;
        var rel = SelectedRelease;
        bool newest = rel is not null && _releases.TryGetValue(flavor, out var list) && list.FirstOrDefault() == rel;
        var parts = new List<string>();
        if (api is { } v)
        {
            var files = Core.Mods.Dxvk.Files(flavor, v).Concat(Core.Mods.Dxvk.NeedsDxgi(flavor) ? ["dxgi.dll"] : Array.Empty<string>());
            parts.Add(L.F("Puts {0} next to the game exe.", string.Join(", ", files)));
            if (_gameApis.Count > 0 && !_gameApis.Contains(v)) parts.Add(L.F("The game is listed as {0}: the dlls of another Direct3D version are not used by it.", string.Join(" / ", _gameApis.Select(Core.Mods.Dxvk.Name))));
        }
        if (rel is not null) parts.Add(newest ? L.T("The newest release.") : L.T("An older release: for when a newer one misbehaves in this game."));
        if (flavor == DxvkFlavor.GplAsync) parts.Add(L.T("Async goes on in dxvk.conf. Avoid it in online games with anti-cheat."));
        if (flavor == DxvkFlavor.Vkd3d && _releases.TryGetValue(DxvkFlavor.Dxvk, out var dx) && dx.FirstOrDefault() is { } d) parts.Add(L.F("dxgi.dll comes from DXVK {0}.", d.Version));
        if (_apiSource.Length > 0) parts.Add(L.F("Graphics API: {0}.", _apiSource));
        _hint.Text = string.Join(" ", parts);
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var mine = _ctx.Settings.Dxvk;
        _install.IsEnabled = !Busy && _loaded && SelectedRelease is not null && SelectedApi is not null && _ctx.TargetDir() is not null;
        ((TextBlock)((StackPanel)_install.Content).Children[1]).Text = mine is null ? L.T("Install")
            : mine.Flavor == Selected ? L.T("Reinstall / change version") : L.F("Install (replaces {0})", Core.Mods.Dxvk.Name(mine.Flavor));
        _uninstall.Visibility = Own is not null ? Visibility.Visible : Visibility.Collapsed;
        _uninstall.IsEnabled = !Busy;
    }

    /// <summary>Self-test: "loaded" with the layer, the Direct3D version, how many releases and the hint, or what is missing.</summary>
    internal string SelfTestDescribe()
    {
        if (!_loaded) return "not loaded yet";
        if (Content is not Grid) return "folded: " + string.Join(" ", ((Content as Border)?.Child is Grid g ? g.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBlock>()).Select(t => t.Text) : []));
        int releases = _version.Items.OfType<ComboBoxItem>().Count(i => i.Tag is DxvkRelease);
        // GitHub's limit for requests without a sign-in (tests run many): the page says no release was found, which is right.
        if (releases == 0 && Core.Net.Http.GitHubLimitedUntil is not null && SelectedApi is not null) return $"loaded (GitHub limit reached, no releases): {Core.Mods.Dxvk.Name(Selected)}, {SelectedApi}";
        if (releases == 0 || SelectedApi is null) return $"missing: {releases} releases, Direct3D {SelectedApi?.ToString() ?? "none"}";
        return $"loaded: {Core.Mods.Dxvk.Name(Selected)}, {SelectedApi}, {releases} releases, size {ActualWidth:0}x{ActualHeight:0}; {_hint.Text}";
    }

    public void ShowJob(GameJobs.Job? job)
    {
        bool mine = job is { Kind: "dxvk" };
        _progress.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        if (mine && job!.Fraction >= 0) _progress.Value = job.Fraction;
        UpdateButtons();
    }

    /// <summary>Redraws what is in the folder now.</summary>
    public void Refresh()
    {
        ShowJob(GameJobs.For(_ctx.Game.Id));
        _state.Children.Clear();
        var dir = _ctx.TargetDir();
        if (dir is null)
        {
            _state.Children.Add(Ui.Muted(_ctx.Game.NotInstalled
                ? L.F("{0} is not installed: the layers can go in once it is.", _ctx.Game.Title)
                : L.T("Pick the game exe first (the gear beside Play → Change game exe).")));
            _optionsCard.Visibility = Visibility.Collapsed;
            return;
        }
        var mine = Own;
        var found = FoundHere(dir);
        // The other section's layer is in: this one would replace it.
        if (mine is null && _ctx.Settings.Dxvk is { } other)
            _state.Children.Add(ModUi.Check(null, L.F("{0} {1} is installed in this game", Core.Mods.Dxvk.Name(other.Flavor), other.Version),
                L.F("A game holds one layer at a time: installing {0} replaces it.", Title)));
        if (mine is not null)
        {
            var changed = Core.Mods.Dxvk.Changed(mine, dir);
            _state.Children.Add(ModUi.Check(changed.Count == 0,
                L.F("{0} {1} is installed for {2}", Core.Mods.Dxvk.Name(mine.Flavor), mine.Version, Core.Mods.Dxvk.Name(mine.Api)),
                changed.Count == 0
                    ? L.F("{0}, {1}-bit, installed {2:d MMM yyyy, HH:mm}", string.Join(", ", mine.Files.Keys), mine.Is64 ? 64 : 32, mine.InstalledAt)
                    : L.F("Changed since: {0}. Install it again to put them back.", string.Join(", ", changed))));
        }
        else if (found.Count > 0)
            _state.Children.Add(ModUi.Check(null, L.F("{0} files are in the folder (not installed by Hybrid Launcher)", string.Join(" + ", found.Select(f => f.Layer).Distinct())),
                string.Join(", ", found.Select(f => f.File))));
        else if (_ctx.Settings.Dxvk is null)
            _state.Children.Add(ModUi.Check(null, _vkd3d ? L.T("No VKD3D-Proton in this game: it runs on Windows' Direct3D 12") : L.T("No DXVK in this game: it runs on Windows' Direct3D")));
        if (OptiDetector.Detect(dir) is { } opti)
            _state.Children.Add(ModUi.Check(!opti.FileName.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) && !opti.FileName.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase),
                L.F("OptiScaler is here as {0}", opti.FileName),
                L.T("OptiScaler's upscalers work with the game's own Direct3D: through DXVK most of them have nothing to hook. Use one or the other in a game.")));
        bool showOptions = mine is not null || found.Count > 0;
        _optionsCard.Visibility = showOptions ? Visibility.Visible : Visibility.Collapsed;
        if (showOptions) FillOptions(dir, mine);
        if (_loaded) UpdateButtons();
    }

    void FillOptions(string dir, DxvkState? mine)
    {
        _options.Children.Clear();
        bool gpl = mine?.Flavor == DxvkFlavor.GplAsync;
        if (gpl)
            _options.Children.Add(Switch(L.T("Async shader compiling"), L.T("dxvk.enableAsync: frames are drawn while shaders compile, so less stutter; an effect can be missing for a moment."),
                "dxvk.enableAsync", true));
        _options.Children.Add(Switch(L.T("HDR"), L.T("dxgi.enableHDR: lets the game offer HDR through DXVK (Windows HDR must be on)."), "dxgi.enableHDR", false));

        // A frame cap for Direct3D 10-12 (dxgi) and 9 (d3d9), 0 = none.
        var cap = new NumberBox { Minimum = 0, Maximum = 1000, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140 };
        cap.Value = int.TryParse(Core.Mods.Dxvk.GetConf(dir, "dxgi.maxFrameRate") ?? Core.Mods.Dxvk.GetConf(dir, "d3d9.maxFrameRate"), out var fps) ? fps : 0;
        cap.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) return;
            Write(() =>
            {
                var value = e.NewValue <= 0 ? null : ((int)e.NewValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Core.Mods.Dxvk.SetConf(dir, "dxgi.maxFrameRate", value);
                Core.Mods.Dxvk.SetConf(dir, "d3d9.maxFrameRate", value);
            });
        };
        _options.Children.Add(Row(L.T("Frame rate cap"), L.T("dxgi.maxFrameRate and d3d9.maxFrameRate: 0 leaves the frame rate free."), cap));
    }

    FrameworkElement Switch(string title, string description, string key, bool fallback)
    {
        var dir = _ctx.TargetDir()!;
        var toggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
        var current = Core.Mods.Dxvk.GetConf(dir, key);
        toggle.IsOn = current is null ? fallback : current.Equals("true", StringComparison.OrdinalIgnoreCase);
        toggle.Toggled += (_, _) => Write(() => Core.Mods.Dxvk.SetConf(dir, key, toggle.IsOn ? "True" : "False"));
        return Row(title, description, toggle);
    }

    static Grid Row(string title, string description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        var d = Ui.Muted(description);
        d.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(d);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    void Write(Action change)
    {
        try
        {
            var dir = _ctx.TargetDir();
            bool existed = dir is not null && File.Exists(Path.Combine(dir, Core.Mods.Dxvk.ConfName));
            change();
            // A dxvk.conf that was not there before is Hybrid Launcher's: it goes with the layer.
            if (!existed && _ctx.Settings.Dxvk is { } s) s.ConfCreated = true;
            SettingsStore.Save();
            App.Window?.ShowInfo(L.T("Saved in dxvk.conf. It applies the next time the game starts."));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not change dxvk.conf"), ex.Message, ex); }
    }

    void OpenConf()
    {
        if (_ctx.TargetDir() is not { } dir) return;
        var conf = Path.Combine(dir, Core.Mods.Dxvk.ConfName);
        if (!File.Exists(conf))
        {
            App.Window?.ShowInfo(L.T("There is no dxvk.conf in the game folder yet: the switches above make one."));
            return;
        }
        ModUi.OpenFile(conf, pickApp: true);
    }

    bool CanInstall(out string dir)
    {
        dir = _ctx.TargetDir() ?? "";
        if (dir.Length == 0)
        {
            App.Window?.ShowError(L.T("No game exe"), L.T("Pick the game exe first (the gear beside Play → Change game exe)."));
            return false;
        }
        if (OptiInstaller.RunningGameProcess(dir) is { } running)
        {
            App.Window?.ShowError(L.T("Close the game first"), L.F("{0} is running from this folder.", running));
            return false;
        }
        return true;
    }

    async Task InstallAsync()
    {
        if (Busy || !CanInstall(out var dir) || SelectedRelease is not { } release || SelectedApi is not { } api) return;
        var flavor = release.Flavor;
        var files = Core.Mods.Dxvk.Files(flavor, api).Concat(Core.Mods.Dxvk.NeedsDxgi(flavor) ? ["dxgi.dll"] : Array.Empty<string>()).ToList();
        var blockers = Core.Mods.Dxvk.Blockers(dir, files, _ctx.Settings.Dxvk);
        if (blockers.Count > 0)
        {
            await ExplainBlockersAsync(blockers);
            return;
        }
        if (await ModBackups.DecideAsync(Core.Mods.Dxvk.Name(flavor), _ctx.Game.Title) is not { } backup) return;
        var exe = Exe;
        bool is64 = exe is null || RenoDx.Is64Bit(exe) != false;
        DxvkRelease? dxgi = Core.Mods.Dxvk.NeedsDxgi(flavor) && _releases.TryGetValue(DxvkFlavor.Dxvk, out var dx) ? dx.FirstOrDefault() : null;
        var job = GameJobs.TryStart(_ctx.Game.Id, _ctx.Game.Title, "dxvk", Core.Mods.Dxvk.Name(flavor));
        if (job is null) return;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        UpdateButtons();
        try
        {
            var state = await Core.Mods.Dxvk.InstallAsync(release, api, dir, is64, _ctx.Settings.Dxvk, dxgi, new Progress<double>(p => GameJobs.Report(job, p)), job.Token, backup);
            _ctx.Settings.Dxvk = state;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("{0} {1} installed in {2}. It runs the next time the game starts.", Core.Mods.Dxvk.Name(flavor), release.Version, _ctx.Game.Title));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Window?.ShowError(L.F("Could not install {0}", Core.Mods.Dxvk.Name(flavor)), ex.Message, ex); }
        finally
        {
            _progress.Visibility = Visibility.Collapsed;
            GameJobs.End(job);
            Refresh();
            _ctx.OptiChanged();
        }
    }

    /// <summary>Why a file is not replaced, with a way to the mod that owns it.</summary>
    async Task ExplainBlockersAsync(List<Core.Mods.Dxvk.Blocker> blockers)
    {
        var body = new StackPanel { Spacing = 10 };
        foreach (var b in blockers)
        {
            var text = b.Owner switch
            {
                "OptiScaler" => L.F("{0} is OptiScaler here. DXVK needs that name for itself, and OptiScaler's upscalers need the game's own Direct3D anyway. Take OptiScaler out (or install it under another dll name) before DXVK.", b.File),
                "ReShade" => L.F("{0} is ReShade here. With DXVK, ReShade has to hook Vulkan instead: run ReShade's installer again, pick Vulkan, then install DXVK.", b.File),
                _ => L.F("{0} belongs to {1}. Take it out first, or the game would lose it.", b.File, b.Owner),
            };
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            body.Children.Add(t);
        }
        var owner = blockers[0].Owner;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.T("These files belong to another mod"),
            Content = body,
            PrimaryButtonText = owner switch { "OptiScaler" => L.T("Open OptiScaler"), "ReShade" => L.T("Open ReShade"), _ => "" },
            CloseButtonText = L.T("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowOnceAsync() == ContentDialogResult.Primary)
            _ctx.ShowTab(owner == "OptiScaler" ? "opti/install" : "mods/reshade");
    }

    /// <summary>Takes this section's layer out (the Mods tab's Installed table calls it too).</summary>
    internal async Task UninstallAsync()
    {
        var state = Own;
        if (state is null || Busy || !CanInstall(out var dir)) return;
        if (!await _ctx.ConfirmRemovalAsync(this, L.F("Uninstall {0}?", Core.Mods.Dxvk.Name(state.Flavor)),
                L.F("Removes {0} and puts back the {1} file(s) it replaced. The game runs on Windows' Direct3D again.", string.Join(", ", state.Files.Keys), state.Backups.Count), L.T("Uninstall")))
            return;
        var job = GameJobs.TryStart(_ctx.Game.Id, _ctx.Game.Title, "dxvk", Core.Mods.Dxvk.Name(state.Flavor));
        if (job is null) return;
        try
        {
            var problems = await Task.Run(() => Core.Mods.Dxvk.Uninstall(state, dir));
            _ctx.Settings.Dxvk = null;
            SettingsStore.Save();
            if (problems.Count > 0) App.Window?.ShowError(L.F("{0} removed, with problems", Core.Mods.Dxvk.Name(state.Flavor)), string.Join("\n", problems));
            else App.Window?.ShowSuccess(L.F("{0} removed and the folder put back as it was.", Core.Mods.Dxvk.Name(state.Flavor)));
        }
        catch (Exception ex) { App.Window?.ShowError(L.F("Could not uninstall {0}", Core.Mods.Dxvk.Name(state.Flavor)), ex.Message, ex); }
        finally
        {
            GameJobs.End(job);
            Refresh();
            _ctx.OptiChanged();
        }
    }
}
