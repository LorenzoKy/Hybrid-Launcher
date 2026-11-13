using HybridLauncher.Core;
using HybridLauncher.Core.Hardware;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Views;

/// <summary>
/// DLSS 5 (1.4.0-rc.13 as a window; since rc.18 a section of the Mods tab, its pages behind a row of pills): DLSS 5 neural
/// rendering for this game.
/// Overview (the PC's card and driver, what this game has, the ways that run here with the recommended one first), the model
/// file (NVIDIA's nvngx_dlssnr.dll and which way it fits), DLSSNR-AMD (Radeon RX 9000 / 7000: prepared once from the model
/// file, laid over the official OptiScaler, its settings), F5 Multipass (janblade's fork: NVIDIA NGX on RTX, the vendor-neutral
/// port elsewhere, F5Low, its [DlssNr] settings) and the logs the two write.
/// </summary>
public partial class GamePage
{
    /// <summary>What a long DLSS 5 job is doing (preparing DLSSNR-AMD, putting it in), or null.</summary>
    string? _dlss5Busy;
    TextBlock? _dlss5BusyText;
    DlssNrAmdRelease? _amdLatest;
    bool _amdLatestAsked;
    /// <summary>Why GitLab could not be asked for the newest release (Prepare still asks again), or null.</summary>
    string? _amdLatestError;

    /// <summary>The DLSS 5 section of the Mods tab: the row of pages and the page shown (built the first time the tab opens).</summary>
    Border? _dlss5View;
    WrapPanel? _dlss5Pills;
    StackPanel? _dlss5Body;
    string _dlss5Page = "overview";

    /// <summary>Self-test: opens the DLSS 5 section of the Mods tab on a page; null goes back to its overview page.</summary>
    internal void SelfTestDlss5(string? page)
    {
        ShowTab("mods", remember: false, section: "dlss5");
        Dlss5Go(page ?? "overview");
        ScrollToSection("dlss5", animated: false);
    }

    async void Dlss5Setup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The pages tell the model file's builds apart by its SHA-256: worked out here, not on the UI thread (150 MB).
            if (File.Exists(DlssNr.StoredPath)) await Task.Run(() => { try { Dlss5.HashOf(DlssNr.StoredPath); } catch { } });
            OpenTab("mods/dlss5");
        }
        catch (Exception ex) { Log.Warn($"DLSS 5: {ex.Message}"); }
    }

    /// <summary>The section's frame: a heading, the pages as pills, the page below. Its content is drawn by <see cref="Dlss5Go"/>.</summary>
    Border Dlss5Section()
    {
        // The heading and the pages' pills on a card; the page's rows below it, on the page itself (no card inside a card).
        var head = new StackPanel { Spacing = 14 };
        head.Children.Add(Ui.Heading("\uE945", L.T("DLSS 5 (experimental)"), subtitle: L.T("Neural rendering after the game's upscaler: the card, the model file, DLSSNR-AMD and F5 Multipass"),
            tint: Palettes.Adapt(Windows.UI.Color.FromArgb(0xFF, 0x76, 0xB9, 0x00))));
        _dlss5Pills = new WrapPanel(6, []);
        head.Children.Add(_dlss5Pills);
        _dlss5Body = new StackPanel { Spacing = 8 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Ui.Card(head, 20));
        panel.Children.Add(_dlss5Body);
        _dlss5View = new Border { Child = panel };
        // The model file's hash is read once, off the UI thread, before the pages that tell its builds apart are drawn.
        _ = Task.Run(() => { try { if (File.Exists(DlssNr.StoredPath)) Dlss5.HashOf(DlssNr.StoredPath); } catch { } })
            .ContinueWith(_ => DispatcherQueue.TryEnqueue(() => Dlss5Go(_dlss5Page)), TaskScheduler.Default);
        Dlss5Go(_dlss5Page);
        return _dlss5View;
    }

    /// <summary>Shows a page of the DLSS 5 section ("overview", "model", "amd", "f5", "logs").</summary>
    void Dlss5Go(string id)
    {
        if (_game is not { } game || _dlss5Body is null || _dlss5Pills is null) return;
        var pages = Dlss5Pages().ToList();
        if (!pages.Any(p => p.Id == id)) id = pages[0].Id;
        _dlss5Page = id;
        _dlss5Pills.Children.Clear();
        foreach (var (pid, title) in pages)
        {
            bool on = pid == id;
            var b = new Button { Content = title, Padding = new Thickness(14, 6, 14, 7), CornerRadius = new CornerRadius(15) };
            if (on) b.Style = Ui.Style("AccentButtonStyle");
            var target = pid;
            b.Click += (_, _) => Ui.Safe(() => Dlss5Go(target));
            _dlss5Pills.Children.Add(b);
        }
        _dlss5Body.Children.Clear();
        Dlss5Page(id, _dlss5Body, game);
    }

    IEnumerable<(string Id, string Title)> Dlss5Pages()
    {
        var (tier, _) = Dlss5.Detect();
        yield return ("overview", L.T("Overview"));
        yield return ("model", L.T("Model file"));
        foreach (var method in Dlss5.MethodsFor(tier))
            yield return method == Dlss5.Amd ? ("amd", "DLSSNR-AMD") : ("f5", "F5 Multipass");
        // A game that has the other way in still gets its page (to see it and take it out).
        if (!Dlss5.MethodsFor(tier).Contains(Dlss5.Amd) && (_settings.Install?.DlssNrAmd is not null || _settings.DlssNrAmdReShade is not null)) yield return ("amd", "DLSSNR-AMD");
        yield return ("logs", L.T("Logs"));
    }

    bool _dlss5RefreshQueued;

    /// <summary>
    /// Draws the open DLSS 5 page again (the state changed), keeping where it was scrolled. Queued, not at once: it is called
    /// from the page's own switches and combos, which must not be thrown away inside their own event; several calls in a row
    /// draw it once.
    /// </summary>
    void RefreshDlss5()
    {
        if (_dlss5RefreshQueued || _dlss5Body is null) return;
        _dlss5RefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _dlss5RefreshQueued = false;
            RedrawDlss5();
        });
    }

    /// <summary>The state changed while the Mods tab was not open (OptiScaler installed from its tab): redrawn when it opens.</summary>
    bool _dlss5Stale;

    void RedrawDlss5()
    {
        if (_dlss5Body is null) return;
        if (_tab != "mods") { _dlss5Stale = true; return; }
        _dlss5Stale = false;
        // Redrawn in place: the page keeps its scroll position as long as the section's height does not shrink under it.
        Dlss5Go(_dlss5Page);
    }

    /// <summary>The way this game runs DLSS 5: what is installed, else what was chosen.</summary>
    string? CurrentDlss5Method()
    {
        var m = _settings.Install;
        if (m?.DlssNrAmd is not null || _settings.DlssNrAmdReShade is not null) return Dlss5.Amd;
        if (m is not null && DlssNr.NeededBy(m)) return Dlss5.F5;
        return _settings.Dlss5Method;
    }

    static string MethodName(string method, Dlss5Gpu tier) => method == Dlss5.Amd ? "DLSSNR-AMD"
        : tier == Dlss5Gpu.NvidiaRtx ? L.T("F5 Multipass with NVIDIA NGX") : L.T("F5 Multipass with the vendor-neutral port");

    /// <summary>The DLSS 5 row's line: what this game has now, or what to do next.</summary>
    string Dlss5Summary()
    {
        var (tier, gpu) = Dlss5.Detect();
        var m = _settings.Install;
        if (m?.DlssNrAmd is { } amd)
            return L.F("On: DLSSNR-AMD {0}, network {1}. In game, End opens NR Control.", amd.Tag, DlssNrAmd.GetAt(m.TargetDir, "Network", "Model") ?? amd.Network);
        if (_settings.DlssNrAmdReShade is { } rs)
            return L.F("On: DLSSNR-AMD {0} through ReShade, network {1}. In game, Home opens ReShade; NR is on its Add-ons page.", rs.Tag, DlssNrAmd.GetAt(rs.TargetDir, "Network", "Model") ?? rs.Network);
        if (m is not null && DlssNr.NeededBy(m))
        {
            bool model = m.Files.ContainsKey(DlssNr.FileName);
            if (!model) return L.F("This game has F5 ({0}), but not NVIDIA's {1} yet: Set up DLSS 5 → Model file.", m.DisplayVersion, DlssNr.FileName);
            var through = DlssNr.Backend(m) == "port" ? L.T("the vendor-neutral port") : "NVIDIA NGX";
            return L.F("On: F5 {0} through {1}. In game, Insert opens OptiScaler's menu.", m.DisplayVersion, through);
        }
        if (_settings.Dlss5Method == Dlss5.Amd) return L.T("DLSSNR-AMD chosen, not in this game yet.");
        var name = gpu?.Name ?? L.T("this PC's card");
        return L.F("Off. On {0} the way to go is {1}.", name, MethodName(Dlss5.Recommended(tier), tier));
    }

    void Dlss5Page(string id, StackPanel page, Game game)
    {
        _dlss5BusyText = new TextBlock { Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, Visibility = _dlss5Busy is null ? Visibility.Collapsed : Visibility.Visible, Text = _dlss5Busy ?? "" };
        var (tier, gpu) = Dlss5.Detect();
        // In the Mods tab (rc.18) the rows take the section's width like the other sections; the paragraphs stay no longer than
        // the eye follows (D5Note).
        page.Spacing = 8;
        switch (id)
        {
            case "model": Dlss5ModelPage(page, tier, gpu); break;
            case "amd": Dlss5AmdPage(page, tier, gpu); break;
            case "f5": Dlss5F5Page(page, tier, gpu); break;
            case "logs": Dlss5LogsPage(page); break;
            default: Dlss5Overview(page, tier, gpu); break;
        }
    }

    // ---------- Overview ----------

    void Dlss5Overview(StackPanel page, Dlss5Gpu tier, GpuInfo? gpu)
    {
        // This game first (2.0): what it has, and one card that says what is ready and sets up the rest.
        page.Children.Add(_dlss5BusyText!);
        page.Children.Add(D5Row(L.T("This game"), Dlss5Summary(), Ui.Pill(CurrentDlss5Method() is { } cm ? MethodName(cm, tier) : L.T("Off"), CurrentDlss5Method() is null ? Ui.Tone.Unknown : Ui.Tone.Accent)));
        Dlss5Setup(page, CurrentDlss5Method() ?? Dlss5.Recommended(tier), tier, gpu);

        var tone = tier switch { Dlss5Gpu.NvidiaRtx or Dlss5Gpu.AmdRdna4 => Ui.Tone.Good, Dlss5Gpu.AmdRdna3 => Ui.Tone.Partial, Dlss5Gpu.Unknown => Ui.Tone.Unknown, _ => Ui.Tone.Partial };
        var card = D5Row(L.T("Graphics card"), gpu?.Name ?? L.T("Not found"), Ui.Pill(Dlss5.TierName(tier), tone));
        if (gpu is not null && tier is Dlss5Gpu.NvidiaRtx or Dlss5Gpu.AmdRdna4 or Dlss5Gpu.AmdRdna3)
        {
            var ok = Dlss5.DriverOk(tier, gpu);
            string need = tier == Dlss5Gpu.NvidiaRtx ? L.F("DLSS 5 needs {0} or newer.", $"{Dlss5.NvidiaMinimum:0.00}") : L.F("DLSSNR-AMD needs AMD Software {0}.{1} or newer.", Dlss5.AdrenalinMinimum.Year, Dlss5.AdrenalinMinimum.Month);
            string have = tier == Dlss5Gpu.NvidiaRtx ? (Dlss5.NvidiaDriver(gpu) is { } v ? $"{v:0.00}" : gpu.DriverVersion)
                : Dlss5.AdrenalinVersion(gpu) is { } a ? $"AMD Software {a.Year}.{a.Month}" : gpu.DriverVersion;
            // The card and its driver side by side: two short facts, one line of the page.
            page.Children.Add(D5Pair(card, D5Row(L.T("Driver"), $"{have}. {need}", Ui.Pill(ok switch { true => L.T("New enough"), false => L.T("Too old"), _ => L.T("Unknown") }, ok switch { true => Ui.Tone.Good, false => Ui.Tone.Bad, _ => Ui.Tone.Unknown }))));
        }
        else page.Children.Add(card);

        page.Children.Add(D5Heading(L.T("Ways that run on this PC")));
        var methods = Dlss5.MethodsFor(tier);
        foreach (var method in methods)
        {
            string text = method == Dlss5.Amd
                ? L.T("mochizuki0323's port runs NVIDIA's network natively on Radeon through Vulkan, as a plugin of the official OptiScaler. RX 9000 runs all four networks; RX 7000 and Radeon 700M / 800M are experimental, with two. Hybrid Launcher makes its weights once from your model file.")
                : tier == Dlss5Gpu.NvidiaRtx
                    ? L.T("janblade's OptiScaler fork with NVIDIA's NGX: the reference way on RTX 20 to 50, with F5Low frame generation as an option. It needs the 310.8 model file.")
                    : L.T("janblade's OptiScaler fork with its vendor-neutral port: the model runs with DirectX 12 compute on any card. Its author tested it on NVIDIA only. It needs the 310.9 model file, or it runs the large network slowly.");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (method == methods[0]) buttons.Children.Add(Ui.Pill(L.T("Recommended"), Ui.Tone.Good));
            bool chosen = CurrentDlss5Method() == method;
            var use = PropButton(chosen ? L.T("Open") : L.T("Use this"), () =>
            {
                if (_settings.Install?.DlssNrAmd is null) { _settings.Dlss5Method = method; SettingsStore.Save(); }
                Dlss5Go(method == Dlss5.Amd ? "amd" : "f5");
                UpdateDlssNrRow();
            });
            buttons.Children.Add(use);
            page.Children.Add(D5Row(MethodName(method, tier), text, buttons));
        }
        if (tier is Dlss5Gpu.AmdOlder or Dlss5Gpu.Intel or Dlss5Gpu.Unknown)
            page.Children.Add(D5Note(L.T("DLSSNR-AMD needs a Radeon RX 9000 or RX 7000: on this card only F5's vendor-neutral port can run DLSS 5.")));

        page.Children.Add(D5Note(L.T("Turn on DLSS, FSR or XeSS in the game's settings: DLSS 5 runs on what the game's upscaler hands OptiScaler. Games with an upscaler of their own shaders never hand it over.")));
        page.Children.Add(D5Note(L.T("Do not use it in online games with anti-cheat.")));

        page.Children.Add(D5Heading(L.T("About DLSS 5")));
        page.Children.Add(D5Note(L.T("DLSS 5 neural rendering runs NVIDIA's network on the game's picture, after its upscaler. Two mods make it work: janblade's F5 Multipass, a fork of OptiScaler (NVIDIA NGX on RTX cards, a vendor-neutral port on other cards), and mochizuki0323's DLSSNR-AMD, a plugin of the official OptiScaler that runs it natively on Radeon RX 9000 and RX 7000.")));
    }

    // ---------- Model file ----------

    void Dlss5ModelPage(StackPanel page, Dlss5Gpu tier, GpuInfo? gpu)
    {
        var stored = File.Exists(DlssNr.StoredPath) ? DlssNr.StoredPath : null;
        page.Children.Add(D5Note(L.F("Both mods need NVIDIA's {0}: F5 loads it beside OptiScaler, DLSSNR-AMD only reads its weights (it is never run). It is NVIDIA's file, so Hybrid Launcher cannot download it: pick the one you have. Hybrid Launcher keeps one copy.", DlssNr.FileName)));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(PropButton(stored is null ? L.T("Pick…") : L.T("Change…"), () => _ = ChangeDlssNrAsync()));
        if (stored is not null)
            buttons.Children.Add(PropButton(L.T("Remove"), () =>
            {
                DlssNr.Forget();
                AppState.Settings.UseDlssNr = false;
                SettingsStore.Save();
                _ = SyncDlssNrAsync();
            }));
        string detail = stored is null ? L.T("None picked yet.")
            : L.F("{0}, {1}", Dlss5.Describe(stored), HardwareInfo.FormatBytes((ulong)new FileInfo(stored).Length));
        page.Children.Add(D5Row(DlssNr.FileName, detail, buttons));

        page.Children.Add(D5Heading(L.T("Which ways it fits")));
        foreach (var method in Dlss5.MethodsFor(tier))
        {
            var problem = Dlss5.ModelProblem(stored, method, tier, gpu);
            page.Children.Add(D5Row(MethodName(method, tier), problem ?? L.T("This file works."), Ui.Pill(problem is null ? L.T("Fits") : L.T("Does not fit"), problem is null ? Ui.Tone.Good : Ui.Tone.Bad)));
        }
        page.Children.Add(D5Note(tier == Dlss5Gpu.NvidiaRtx
            ? L.T("RTX 50: NVIDIA's own 310.8. RTX 20, 30 and 40: ShortFuse's modified 310.8 (pinned in the RenoDX Discord); Windows calls its signature invalid, which is expected. Driver 616.56 or newer.")
            : L.T("DLSSNR-AMD reads 310.9.16 (all networks) or 310.8.0 (CC_SILVER_AARDWOLD only). F5's vendor-neutral port wants 310.9 or newer.")));

        page.Children.Add(D5Heading(L.T("F5 installs")));
        bool global = AppState.Settings.UseDlssNr && stored is not null;
        bool game = stored is not null && DlssNr.OnFor(_settings, AppState.Settings);
        page.Children.Add(D5Row(L.T("Put it in every F5 install"), L.T("Every game whose OptiScaler build needs it gets the file, unless the game says otherwise. DLSSNR-AMD does not need this."), Switch(global, on => _ = SetDlssNrGlobalAsync(on))));
        page.Children.Add(D5Row(L.T("Put it in this game's F5 install"), L.T("This game only; it follows the switch above until you change it here."), Switch(game, on => _ = SetDlssNrGameAsync(on))));
    }

    // ---------- DLSSNR-AMD ----------

    void Dlss5AmdPage(StackPanel page, Dlss5Gpu tier, GpuInfo? gpu)
    {
        var m = _settings.Install;
        var stage = DlssNrAmd.Stage();
        if (!_amdLatestAsked)
        {
            _amdLatestAsked = true;
            _ = Task.Run(async () =>
            {
                try { _amdLatest = await DlssNrAmd.LatestAsync(_pageCts.Token); _amdLatestError = _amdLatest is null ? L.T("GitLab lists no Windows package.") : null; }
                catch (Exception ex) { _amdLatestError = ex.Message; Log.Warn($"DLSSNR-AMD releases: {ex.Message}"); }
                DispatcherQueue.TryEnqueue(RefreshDlss5);
            });
        }
        bool outdated = stage is not null && _amdLatest is not null && stage.Tag != _amdLatest.Tag;

        // This game first (2.0): what is ready, one button for the rest, then how it goes in and its settings.
        page.Children.Add(_dlss5BusyText!);
        if (tier is not (Dlss5Gpu.AmdRdna4 or Dlss5Gpu.AmdRdna3))
            page.Children.Add(D5Row(L.T("Graphics card"), L.T("DLSSNR-AMD runs on Radeon RX 9000 (and RX 7000, experimental) only."), Ui.Pill(L.T("Not supported"), Ui.Tone.Bad)));
        else if (tier == Dlss5Gpu.AmdRdna3)
            page.Children.Add(D5Row(L.T("Graphics card"), L.T("RX 7000 and Radeon 700M / 800M: experimental, CC_SMALL_FP8 and CC_SILVER_AARDWOLD only, and none at all when the driver cannot take the prepared machine code."), Ui.Pill("RDNA 3", Ui.Tone.Partial)));
        if (tier is Dlss5Gpu.AmdRdna4 or Dlss5Gpu.AmdRdna3) Dlss5Setup(page, Dlss5.Amd, tier, gpu);

        // Two ways in (1.4.0-rc.20): a plugin of OptiScaler (games with DLSS, FSR or XeSS), or the package's ReShade routes,
        // with no OptiScaler, for any 64-bit DirectX 9 to 12 or Vulkan game (ReShade provides the motion vectors).
        bool reshadeWay = AmdReShadeWay();
        if (m?.DlssNrAmd is null && _settings.DlssNrAmdReShade is null)
        {
            string[] ways = [L.T("Through OptiScaler"), L.T("Through ReShade")];
            page.Children.Add(D5Row(L.T("Way in"), L.T("Through OptiScaler: for games with DLSS, FSR or XeSS, OptiScaler does the upscaling. Through ReShade: no OptiScaler, any 64-bit game; ReShade's VORT effects give NR the motion vectors."),
                Combo(ways, reshadeWay ? 1 : 0, i => { _settings.Dlss5AmdWay = i == 1 ? "reshade" : null; SettingsStore.Save(); RefreshDlss5(); UpdateDlssNrRow(); })));
        }
        if (reshadeWay) AmdReShadeGame(page, tier, stage, outdated);
        else
        {
            if (m?.DlssNrAmd is { } info)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (outdated || stage is not null && stage.Tag != info.Tag)
                    actions.Children.Add(PropButton(L.T("Update"), () => _ = PutAmdAsync()));
                actions.Children.Add(PropButton(L.T("Take out"), () => _ = TakeOutAmdAsync()));
                page.Children.Add(D5Row(L.F("In this game: {0}", info.Tag), L.F("Over OptiScaler {0}. Uninstalling OptiScaler takes it out too.", m.DisplayVersion), actions));
                AmdSettings(page, m.TargetDir, tier, stage, () => _ = PutAmdAsync(reset: true));
            }
            page.Children.Add(D5Heading(L.T("In game")));
            page.Children.Add(D5Note(L.T("Turn on DLSS, FSR or XeSS in the game. End opens NR Control (every NR setting), Insert OptiScaler's menu, Ctrl+F11 cycles the networks, F6 turns NR off and on. The first start compiles for about a minute; until then the picture looks as without NR.")));
            page.Children.Add(D5Note(L.T("If NR Control keeps waiting for the game's upscaler, set [Spoofing] Dxgi=false in OptiScaler.ini. Its own log is in the Logs page.")));
        }

        // The package: prepared once, for every game.
        page.Children.Add(D5Heading(L.T("Package")));
        string latest = _amdLatest is { } l ? L.F("Newest: {0} ({1:d}).", l.Tag, l.Date)
            : _amdLatestError is { } err ? L.F("Could not ask GitLab for the newest release: {0}", err)
            : _amdLatestAsked ? L.T("Checking GitLab…") : "";
        string prepared = stage is null ? L.T("Not prepared yet.")
            : L.F("{0}, prepared {1:d} from {2} {3}: {4}. {5} on this PC.", stage.Tag, stage.PreparedAt, DlssNr.FileName, stage.ModelVersion, string.Join(", ", stage.Networks), HardwareInfo.FormatBytes((ulong)DlssNrAmd.StageBytes()));
        var prepare = PropButton(stage is null ? L.T("Prepare") : outdated ? L.F("Update to {0}", _amdLatest!.Tag) : L.T("Prepare again"), () => _ = PrepareAmdAsync());
        prepare.IsEnabled = _dlss5Busy is null && !_busy && (_amdLatest is not null || _amdLatestError is not null);
        page.Children.Add(D5Row("DLSSNR-AMD", prepared + " " + latest, prepare));
        // Made from 310.8.0: two of the four networks are missing (1.4.0-rc.20). They come from the 310.9.16 model file.
        if (stage is not null && !stage.Networks.Contains("CC_SMALL_FP8"))
        {
            var pick = PropButton(L.T("Pick 310.9.16…"), () => _ = PickNewerAmdModelAsync());
            pick.IsEnabled = _dlss5Busy is null && !_busy;
            page.Children.Add(D5Row("CC_SMALL_FP8, CC_SMALL_FP8_EXPERIMENTAL",
                L.F("These two networks need the model of NVIDIA's {0} 310.9.16; this package was made from {1}. Pick the 310.9.16 file: Hybrid Launcher keeps it as the model file and prepares the package again with all four networks.", DlssNr.FileName, stage.ModelVersion), pick));
        }

        page.Children.Add(D5Heading(L.T("About DLSSNR-AMD")));
        page.Children.Add(D5Note(L.T("mochizuki0323's DLSSNR-AMD runs DLSS 5's network natively on Radeon (Vulkan, AMD's own matrix units), as an .asi plugin of the official OptiScaler or, in any 64-bit game, as a ReShade add-on. Hybrid Launcher runs its installer once to make and check the networks' weights from your model file, then puts it in this game the way you pick below.")));
        var link = new HyperlinkButton { Content = DlssNrAmd.Page, Padding = new Thickness(0, 0, 0, 4) };
        link.Click += (_, _) => Ui.Open(DlssNrAmd.Page);
        page.Children.Add(link);
    }

    void AmdSettings(StackPanel page, string m, Dlss5Gpu tier, DlssNrAmdStage? stage, Action reset)
    {
        page.Children.Add(D5Heading(L.T("Settings")));
        page.Children.Add(D5Note(L.T("Written to dlssnr-amd.ini; NR reads it again while the game runs. NR Control (End) changes the same settings in game.")));
        var networks = (stage?.Networks ?? [.. DlssNrAmd.AllNetworks]).Where(n => tier != Dlss5Gpu.AmdRdna3 || n is "CC_SMALL_FP8" or "CC_SILVER_AARDWOLD").ToList();
        var network = DlssNrAmd.GetAt(m, "Network", "Model") ?? "";
        page.Children.Add(D5Row(L.T("Network"), L.T("CC_SMALL_FP8 is 310.9's default and the fastest; CC_SILVER_AARDWOLD is 310.8's. The two _EXPERIMENTAL / _OPTIMIZED ones are the port's faster versions (RX 9000)."),
            Combo(networks, networks.IndexOf(network), i => AmdSet(m, "Network", "Model", networks[i]))));
        if (network == "CC_SILVER_AARDWOLD_OPTIMIZED")
        {
            string[] levels = [L.T("Quality"), L.T("Balanced"), L.T("Performance"), L.T("Ultra Performance")];
            int.TryParse(DlssNrAmd.GetAt(m, "Network", "OptimizedLayers"), out var lv);
            page.Children.Add(D5Row(L.T("Optimized layers"), L.T("How many of the network's layers run in the faster, rougher form."), Combo(levels, lv, i => AmdSet(m, "Network", "OptimizedLayers", i.ToString()))));
        }
        string[] styles = ["RenoDX", "wilsjo2"];
        page.Children.Add(D5Row(L.T("Processing style"), L.T("What happens before and after the network: RenoDX's DLSS 5 add-on, or OptiScaler-NR's original (wilsjo2)."),
            Combo(styles, DlssNrAmd.GetAt(m, "RenoDX", "Enabled") == "0" ? 1 : 0, i => AmdSet(m, "RenoDX", "Enabled", i == 0 ? "1" : "0"))));
        page.Children.Add(D5Row(L.T("Memory guard"), L.T("Builds a network only when its video memory is free (off by default)."),
            Switch(DlssNrAmd.GetAt(m, "Network", "Memory Guard") == "1", on => AmdSet(m, "Network", "Memory Guard", on ? "1" : "0"))));
        if (DlssNrAmd.GetAt(m, "HudFix", "Mode") is { } hud)
        {
            string[] modes = ["off", "auto", "game", "captured", "estimated"];
            page.Children.Add(D5Row(L.T("HUD fix"), L.T("Keeps the game's HUD out of the network's work."), Combo(modes, Array.IndexOf(modes, hud), i => AmdSet(m, "HudFix", "Mode", modes[i]))));
        }
        bool pre = DlssNrAmd.GetAt(m, "Preprocess", "Enabled") == "1";
        page.Children.Add(D5Row(L.T("Fix green or grainy colours"), L.T("Auto exposure before the network ([Preprocess]); 007 First Light needs it, Ctrl+F10 switches it in game."),
            Switch(pre, on =>
            {
                AmdSet(m, "Preprocess", "Enabled", on ? "1" : "0");
                AmdSet(m, "Preprocess", "Exposure", on ? "auto" : "off");
            })));
        page.Children.Add(D5Row(L.T("Back to the package's settings"), L.T("Writes dlssnr-amd.ini again as the package has it, with the network above."),
            PropButton(L.T("Reset"), reset)));
    }

    void AmdSet(string m, string section, string key, string value)
    {
        try
        {
            if (key == "Model")
            {
                if (_settings.Install?.DlssNrAmd is { } oi) oi.Network = value;
                if (_settings.DlssNrAmdReShade is { } ri) ri.Network = value;
            }
            if (!DlssNrAmd.SetAt(m, section, key, value)) App.Window?.ShowError(L.T("Could not change the setting"), L.F("dlssnr-amd.ini has no {0} in [{1}].", key, section));
            SettingsStore.Save();
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the setting"), ex.Message, ex); }
        UpdateDlssNrRow();
    }

    static string DefaultNetwork(DlssNrAmdStage stage, Dlss5Gpu tier) =>
        stage.Networks.Contains("CC_SMALL_FP8") ? "CC_SMALL_FP8" : "CC_SILVER_AARDWOLD";

    void SetDlss5Busy(string? text)
    {
        _dlss5Busy = text;
        if (_dlss5BusyText is { } t)
        {
            t.Text = text ?? "";
            t.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Downloads the newest package and makes the networks' weights from the stored model file (or one picked now).</summary>
    async Task PrepareAmdAsync()
    {
        if (_dlss5Busy is not null || _busy) return;
        try
        {
            var release = _amdLatest ?? await DlssNrAmd.LatestAsync(_pageCts.Token, fresh: true) ?? throw new InvalidOperationException(L.T("GitLab lists no Windows package of DLSSNR-AMD."));
            var dll = File.Exists(DlssNr.StoredPath) && Dlss5.AmdCanRead(DlssNr.StoredPath) ? DlssNr.StoredPath : null;
            if (dll is null)
            {
                App.Window?.ShowInfo(L.F("Pick NVIDIA's {0} 310.9.16 (all networks) or 310.8.0: only its weights are read.", DlssNr.FileName));
                dll = await Pickers.PickFileAsync(App.Window!, ".dll");
                if (dll is null) return;
                if (!File.Exists(DlssNr.StoredPath)) { try { await DlssNr.StoreAsync(dll, _pageCts.Token); } catch (Exception ex) { Log.Warn($"Storing the model file: {ex.Message}"); } }
            }
            SetDlss5Busy(L.T("Starting…"));
            RefreshDlss5();
            var progress = new Progress<(string Step, double? Share)>(p => SetDlss5Busy(p.Share is { } s ? $"{p.Step} {s:P0}" : p.Step));
            var stage = await Task.Run(() => DlssNrAmd.PrepareAsync(release, dll, progress, _pageCts.Token));
            SetDlss5Busy(null);
            App.Window?.ShowSuccess(L.F("DLSSNR-AMD {0} is ready: {1}.", stage.Tag, string.Join(", ", stage.Networks)));
            // This game had an older one: brought up to date.
            if (_settings.Install?.DlssNrAmd is { } info && info.Tag != stage.Tag) await PutAmdAsync();
            else if (_settings.DlssNrAmdReShade is { } rinfo && rinfo.Tag != stage.Tag) await PutAmdReShadeAsync();
        }
        catch (OperationCanceledException) { SetDlss5Busy(null); }
        catch (Exception ex)
        {
            SetDlss5Busy(null);
            App.Window?.ShowError(L.T("Could not prepare DLSSNR-AMD"), ex.Message, ex);
        }
        RefreshDlss5();
    }

    /// <summary>Lays DLSSNR-AMD over this game's OptiScaler (again, after an update; or with the package's settings).</summary>
    async Task PutAmdAsync(bool reset = false)
    {
        if (_game is not { } game || _settings.Install is not { } m || DlssNrAmd.Stage() is not { } stage || _dlss5Busy is not null || _busy) return;
        var (tier, _) = Dlss5.Detect();
        string network = DlssNrAmd.GetAt(m.TargetDir, "Network", "Model") ?? DefaultNetwork(stage, tier);
        if (!stage.Networks.Contains(network)) network = DefaultNetwork(stage, tier);
        SetDlss5Busy(L.T("Putting DLSSNR-AMD in…"));
        try
        {
            await Task.Run(() =>
            {
                if (reset)
                {
                    DlssNrAmd.Remove(m);
                    DlssNrAmd.ForgetSavedIni(m.TargetDir);
                }
                DlssNrAmd.Apply(m, network, renoDx: DlssNrAmd.GetAt(m.TargetDir, "RenoDX", "Enabled") != "0");
            });
            _settings.Dlss5Method = Dlss5.Amd;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("{0}: DLSSNR-AMD {1} is in. Turn on DLSS, FSR or XeSS in the game; End opens NR Control.", game.Title, stage.Tag));
        }
        catch (Exception ex)
        {
            SettingsStore.Save();
            App.Window?.ShowError(L.T("Could not put DLSSNR-AMD in"), ex.Message, ex);
        }
        finally { SetDlss5Busy(null); }
        _ = UpdateFootprintAsync();
        UpdateOptiStatus();
        UpdateDlssNrRow();
    }

    async Task TakeOutAmdAsync()
    {
        if (_game is not { } game || _settings.Install is not { } m || _dlss5Busy is not null || _busy) return;
        SetDlss5Busy(L.T("Taking DLSSNR-AMD out…"));
        try
        {
            await Task.Run(() =>
            {
                OptiInstaller.EnsureNotRunning(m.TargetDir);
                DlssNrAmd.Remove(m);
            });
            _settings.Dlss5Method = null;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("{0}: DLSSNR-AMD taken out; OptiScaler stays.", game.Title));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not take DLSSNR-AMD out"), ex.Message, ex); }
        finally { SetDlss5Busy(null); }
        _ = UpdateFootprintAsync();
        UpdateOptiStatus();
        UpdateDlssNrRow();
    }

    /// <summary>After an OptiScaler install or update: DLSSNR-AMD back over it, with the game's dlssnr-amd.ini as it was.</summary>
    async Task ReapplyDlssNrAmdAsync(Game game, GameSettings settings, InstallManifest manifest)
    {
        if (DlssNrAmd.Stage() is not { } stage) return;
        if (DlssNrAmd.CannotGoInto(manifest) is { } why)
        {
            App.Window?.ShowInfo(L.F("{0}: DLSSNR-AMD was not put back. {1}", game.Title, why));
            return;
        }
        try
        {
            var (tier, _) = Dlss5.Detect();
            await Task.Run(() => DlssNrAmd.Apply(manifest, DefaultNetwork(stage, tier), renoDx: true));
            SettingsStore.Save();
        }
        catch (Exception ex) { SettingsStore.Save(); App.Window?.ShowError(L.T("Could not put DLSSNR-AMD back"), ex.Message, ex); }
    }

    // ---------- F5 Multipass ----------

    /// <summary>The 310.9.16 model file picked for the two networks 310.8.0 cannot give, then the package prepared again.</summary>
    async Task PickNewerAmdModelAsync()
    {
        try
        {
            if (!await PickDlssNrAsync()) return;
            await SyncDlssNrAsync();
            var version = File.Exists(DlssNr.StoredPath) ? DlssNr.Version(DlssNr.StoredPath) : "";
            if (version.StartsWith("310.9", StringComparison.Ordinal) && Dlss5.AmdCanRead(DlssNr.StoredPath)) await PrepareAmdAsync();
            else App.Window?.ShowInfo(L.F("This {0} is {1}: the two networks need 310.9.16.", DlssNr.FileName, version));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not prepare DLSSNR-AMD"), ex.Message, ex); }
        RefreshDlss5();
    }

    // ---------- DLSSNR-AMD through ReShade (1.4.0-rc.20) ----------

    /// <summary>DLSSNR-AMD goes (or went) in through ReShade in this game.</summary>
    bool AmdReShadeWay() => _settings.DlssNrAmdReShade is not null || _settings.Install?.DlssNrAmd is null && _settings.Dlss5AmdWay == "reshade";

    /// <summary>The ReShade route for this game: picked by hand, else by its graphics API (DirectX 10-12 when unknown).</summary>
    string AmdRoute() => _settings.DlssNrAmdReShade?.Route ?? _settings.Dlss5AmdRoute ?? _amdApiRoute ?? "reshade-feeder";

    string? _amdApiRoute;
    bool _amdApiAsked;

    static string RouteName(string route) => route switch
    {
        "reshade-feeder-dx9" => L.T("DirectX 9 through DXVK"),
        "reshade-feeder-vulkan" => L.T("Vulkan"),
        _ => L.T("DirectX 10, 11 or 12"),
    };

    void AmdReShadeGame(StackPanel page, Dlss5Gpu tier, DlssNrAmdStage? stage, bool outdated)
    {
        if (!_amdApiAsked && _game is { } g)
        {
            _amdApiAsked = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var api = await Core.Media.GraphicsApi.GetAsync(g, Core.Detection.GameScanner.EffectiveExe(g, _settings), g.SteamAppId, _pageCts.Token);
                    if (api.IsKnown) { _amdApiRoute = DlssNrAmd.RouteFor(api.Apis); DispatcherQueue.TryEnqueue(RefreshDlss5); }
                }
                catch (Exception ex) { Log.Warn($"DLSSNR-AMD: graphics API: {ex.Message}"); }
            });
        }
        if (_settings.DlssNrAmdReShade is { } info)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (outdated || stage is not null && stage.Tag != info.Tag)
                actions.Children.Add(PropButton(L.T("Update"), () => _ = PutAmdReShadeAsync()));
            actions.Children.Add(PropButton(L.T("Take out"), () => _ = TakeOutAmdReShadeAsync()));
            page.Children.Add(D5Row(L.F("In this game: {0}", info.Tag), L.F("Through ReShade, {0}. Taking it out puts back the game's own files of the same names.", RouteName(info.Route)), actions));
            AmdSettings(page, info.TargetDir, tier, stage, () => _ = PutAmdReShadeAsync(reset: true));
        }
        else
        {
            var routes = DlssNrAmd.ReShadeRoutes;
            var route = AmdRoute();
            page.Children.Add(D5Row(L.T("Graphics API"), L.T("Picked from what the game uses; change it when the game runs another one. DirectX 9 goes through DXVK (Vulkan), as the package does it."),
                Combo([.. routes.Select(RouteName)], Array.IndexOf(routes, route), i => { _settings.Dlss5AmdRoute = routes[i]; SettingsStore.Save(); RefreshDlss5(); })));
        }
        page.Children.Add(D5Heading(L.T("In game")));
        page.Children.Add(D5Note(L.T("Home opens ReShade; NR's settings are on its Add-ons page. Ctrl+F11 cycles the networks. The first start compiles for about a minute; until then the picture looks as without NR.")));
        page.Children.Add(D5Note(L.T("Motion vectors come from ReShade's VORT effects, put in with it. Do not use it in online games with anti-cheat.")));
    }

    async Task PutAmdReShadeAsync(bool reset = false)
    {
        if (_game is not { } game || TargetDir is not { } target || DlssNrAmd.Stage() is not { } stage || _dlss5Busy is not null || _busy) return;
        var (tier, _) = Dlss5.Detect();
        var old = _settings.DlssNrAmdReShade;
        var at = old?.TargetDir ?? target;
        string network = DlssNrAmd.GetAt(at, "Network", "Model") ?? DefaultNetwork(stage, tier);
        if (!stage.Networks.Contains(network)) network = DefaultNetwork(stage, tier);
        bool renoDx = DlssNrAmd.GetAt(at, "RenoDX", "Enabled") != "0";
        var route = AmdRoute();
        SetDlss5Busy(L.T("Putting DLSSNR-AMD in…"));
        try
        {
            await Task.Run(() =>
            {
                if (reset && old is not null)
                {
                    DlssNrAmd.RemoveReShade(_settings);
                    DlssNrAmd.ForgetSavedReShadeIni(at);
                }
                DlssNrAmd.ApplyReShade(_settings, at, route, network, renoDx);
            });
            _settings.Dlss5Method = Dlss5.Amd;
            _settings.Dlss5AmdWay = "reshade";
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("{0}: DLSSNR-AMD {1} is in through ReShade. In game, Home opens ReShade.", game.Title, stage.Tag));
        }
        catch (Exception ex)
        {
            SettingsStore.Save();
            App.Window?.ShowError(L.T("Could not put DLSSNR-AMD in"), ex.Message, ex);
        }
        finally { SetDlss5Busy(null); }
        _ = UpdateFootprintAsync();
        _ = UpdateModBadgesAsync();
        UpdateDlssNrRow();
    }

    async Task TakeOutAmdReShadeAsync()
    {
        if (_game is not { } game || _settings.DlssNrAmdReShade is not { } info || _dlss5Busy is not null || _busy) return;
        SetDlss5Busy(L.T("Taking DLSSNR-AMD out…"));
        try
        {
            await Task.Run(() =>
            {
                OptiInstaller.EnsureNotRunning(info.TargetDir);
                DlssNrAmd.RemoveReShade(_settings);
            });
            _settings.Dlss5Method = null;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("{0}: DLSSNR-AMD and its ReShade taken out; the game's own files are back.", game.Title));
        }
        catch (Exception ex) { SettingsStore.Save(); App.Window?.ShowError(L.T("Could not take DLSSNR-AMD out"), ex.Message, ex); }
        finally { SetDlss5Busy(null); }
        _ = UpdateFootprintAsync();
        _ = UpdateModBadgesAsync();
        UpdateDlssNrRow();
    }

    void Dlss5F5Page(StackPanel page, Dlss5Gpu tier, GpuInfo? gpu)
    {
        var m = _settings.Install;
        bool rtx = tier == Dlss5Gpu.NvidiaRtx;
        // This game first (2.0).
        page.Children.Add(_dlss5BusyText!);
        Dlss5Setup(page, Dlss5.F5, tier, gpu);
        bool f5 = m is not null && DlssNr.NeededBy(m);
        if (m?.DlssNrAmd is not null)
            page.Children.Add(D5Row(L.T("F5 build"), L.T("This game has DLSSNR-AMD: take it out first (the two do not go together)."), Ui.Pill(L.T("Not possible"), Ui.Tone.Bad)));

        // What runs the model, and F5Low.
        page.Children.Add(D5Heading(L.T("Model backend")));
        string[] backends = [L.T("Automatic"), L.T("NVIDIA NGX (RTX cards)"), L.T("Vendor-neutral port (any DirectX 12 card)")];
        int at = AppState.Settings.DlssNrBackend switch { "ngx" => 1, "port" => 2, _ => 0 };
        string now = m is not null && DlssNr.Backend(m) is { } b ? (b == "port" ? L.T("This game has the vendor-neutral port now.") : L.T("This game uses NGX now.")) : L.T("Applies at the next install.");
        page.Children.Add(D5Row(L.T("Runs the model"), L.T("Automatic: NGX on an RTX card, the vendor-neutral port on any other.") + " " + now,
            Combo(backends, at, i => SetDlssNrBackend(i switch { 1 => "ngx", 2 => "port", _ => "auto" }))));
        if (rtx)
            page.Children.Add(D5Row(L.T("Newer F5Low"), L.T("Puts the F5Low frame generation published beside the build in with it (NVIDIA RTX only)."), Switch(AppState.Settings.F5LowUpdate, SetF5Low)));

        // F5's own settings, read from OptiScaler.ini.
        if (f5 && Path.Combine(m!.TargetDir, "OptiScaler.ini") is var ini && DlssNrAmd.IniGet(ini, "DlssNr", "Enabled") is { } enabled)
        {
            page.Children.Add(D5Heading(L.T("Settings")));
            page.Children.Add(D5Note(L.T("[DlssNr] in OptiScaler.ini; the OptiScaler menu (Insert) changes them in game too. Its status line says which backend runs the model.")));
            void Set(string key, string value)
            {
                try { DlssNrAmd.IniSet(ini, "DlssNr", key, value); }
                catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the setting"), ex.Message, ex); }
            }
            page.Children.Add(D5Row(L.T("Neural rendering"), "[DlssNr] Enabled", Switch(enabled.Equals("true", StringComparison.OrdinalIgnoreCase), on => Set("Enabled", on ? "true" : "false"))));
            if (DlssNrAmd.IniGet(ini, "DlssNr", "Passes") is { } passes)
            {
                string[] p = ["1", "2", "3"];
                page.Children.Add(D5Row(L.T("Passes"), L.T("More passes make a stronger effect and cost more time; 1 is the guide's setting."), Combo(p, Array.IndexOf(p, passes), i => Set("Passes", p[i]))));
            }
            if (DlssNrAmd.IniGet(ini, "DlssNr", "WorkingScale") is { } scale)
            {
                string[] s = ["0.5", "0.75", "1.0"];
                page.Children.Add(D5Row(L.T("Working scale"), L.T("The size the network works at, against the output."), Combo(s, Array.FindIndex(s, x => double.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, out var a) && double.TryParse(scale, System.Globalization.CultureInfo.InvariantCulture, out var c) && Math.Abs(a - c) < 0.01), i => Set("WorkingScale", s[i]))));
            }
            if (DlssNrAmd.IniGet(ini, "DlssNr", "FinishedPicture") is { } finished)
                page.Children.Add(D5Row(L.T("Finished picture"), L.T("Runs on the finished picture: native DirectX 12 games only (not Vulkan, not DirectX 11)."), Switch(finished.Equals("true", StringComparison.OrdinalIgnoreCase), on => Set("FinishedPicture", on ? "true" : "false"))));
        }

        page.Children.Add(D5Heading(L.T("About F5 Multipass")));
        page.Children.Add(D5Note(rtx
            ? L.T("janblade's F5 Multipass is a fork of OptiScaler with DLSS 5 built in. On an RTX card NVIDIA's NGX runs the model: the build, the model file beside it, and the driver 616.56 or newer.")
            : L.T("janblade's F5 Multipass is a fork of OptiScaler with DLSS 5 built in. Without an RTX card its vendor-neutral port (Optional-Vendor-Neutral-DLSS5-Forwarder.zip from the same release) runs the model with DirectX 12 compute; Hybrid Launcher puts it in the game folder with nr_port.ini, beside NVIDIA's model file.")));
        var link = new HyperlinkButton { Content = "github.com/" + Dlss5.F5Repo, Padding = new Thickness(0, 0, 0, 4) };
        link.Click += (_, _) => Ui.Open("https://github.com/" + Dlss5.F5Repo);
        page.Children.Add(link);
    }

    // ---------- Logs ----------

    void Dlss5LogsPage(StackPanel page)
    {
        if (_settings.Install is not { } m)
        {
            page.Children.Add(D5Note(L.T("No OptiScaler in this game yet.")));
            return;
        }
        page.Children.Add(D5Note(L.T("Written in the game folder while the game runs. Open them when DLSS 5 does not start.")));
        foreach (var (file, what) in new[]
        {
            ("dlssnr-amd.log", L.T("DLSSNR-AMD: the network, the driver's machine code, what NR Control waits for.")),
            ("nr_port.log", L.T("F5's vendor-neutral port: \"network: main\" means the model file is older than 310.9 and runs slowly.")),
            ("OptiScaler.log", L.T("OptiScaler (turned on in its settings).")),
        })
        {
            var path = Path.Combine(m.TargetDir, file);
            bool exists = File.Exists(path);
            string detail = what;
            if (exists && file == "nr_port.log")
                try { if (File.ReadAllText(path).Contains("network: main", StringComparison.OrdinalIgnoreCase)) detail += " " + L.T("It says so now: pick the 310.9 model file."); } catch { }
            var open = PropButton(L.T("Open"), () => Shell.Open(path));
            open.IsEnabled = exists;
            page.Children.Add(D5Row(file, exists ? detail : L.T("Not written yet.") + " " + detail, open));
        }
        page.Children.Add(D5Row(L.T("Game folder"), m.TargetDir, PropButton(L.T("Open"), () => Shell.Open(m.TargetDir))));
    }

    // ---------- steps ----------

    /// <summary>One thing a way of running DLSS 5 needs in this game: there or not, and what puts it there.</summary>
    /// <param name="Stops">Its action leaves the page or ends in the background: nothing is chained after it.</param>
    sealed record Dlss5Part(string Title, string Detail, bool Done, string? Action = null, Func<Task>? Run = null, bool Stops = false);

    /// <summary>What <paramref name="method"/> needs in this game, in the order it is set up, and what to do once in the game.</summary>
    List<Dlss5Part> Dlss5Parts(string method, Dlss5Gpu tier, GpuInfo? gpu, out string inGame)
    {
        var m = _settings.Install;
        var stored = File.Exists(DlssNr.StoredPath) ? DlssNr.StoredPath : null;
        var parts = new List<Dlss5Part>();
        var modelProblem = Dlss5.ModelProblem(stored, method, tier, gpu);
        parts.Add(new(L.T("Model file"), modelProblem ?? Dlss5.Describe(stored!), modelProblem is null, stored is null ? L.T("Pick the model file…") : L.T("Change the model file…"), ChangeDlssNrAsync));
        if (method == Dlss5.Amd && AmdReShadeWay())
        {
            var rstage = DlssNrAmd.Stage();
            bool files = DlssNrAmd.StageHasReShade();
            parts.Add(new(L.T("Package"), rstage is null ? L.T("Downloaded and prepared from your model file, once for every game.")
                    : files ? L.F("{0}: {1}", rstage.Tag, string.Join(", ", rstage.Networks)) : L.T("Prepared before the ReShade way existed: prepare it again to get ReShade's files."),
                files, rstage is null ? L.T("Prepare") : L.T("Prepare again"), PrepareAmdAsync));
            var target = TargetDir;
            var why = target is null ? L.T("The game folder was not found.") : DlssNrAmd.CannotGoIntoReShade(_settings, target);
            bool free = why is null || _settings.DlssNrAmdReShade is not null;
            parts.Add(new(L.T("Game folder"), free ? L.T("Free for the ReShade way, which brings its own ReShade.") : why!, free));
            parts.Add(new(L.T("In this game"), _settings.DlssNrAmdReShade is { } rs ? L.F("{0}, {1}, network {2}", rs.Tag, RouteName(rs.Route), DlssNrAmd.GetAt(rs.TargetDir, "Network", "Model") ?? rs.Network)
                    : L.F("Goes in through ReShade ({0}).", RouteName(AmdRoute())),
                _settings.DlssNrAmdReShade is not null, L.T("Put in this game"), () => PutAmdReShadeAsync()));
            inGame = L.T("In game: Home opens ReShade; NR's settings are on its Add-ons page. The first start compiles for about a minute.");
        }
        else if (method == Dlss5.Amd)
        {
            var stage = DlssNrAmd.Stage();
            parts.Add(new(L.T("Package"), stage is null ? L.T("Downloaded and prepared from your model file, once for every game.") : L.F("{0}: {1}", stage.Tag, string.Join(", ", stage.Networks)),
                stage is not null, L.T("Prepare"), PrepareAmdAsync));
            bool optiOk = m is not null && (m.DlssNrAmd is not null || DlssNrAmd.CannotGoInto(m) is null);
            // The build named from the dll in the folder now, as the OptiScaler tab names it: the install record's tag
            // ("nightly-20261111") was wrong once a nightly or a test build had been dropped over it (1.4.0-rc.20).
            string? there = null;
            try { if (m is not null && OptiDetector.Detect(m.TargetDir) is { } found) there = ShortVersion(found.Version); } catch { }
            parts.Add(new("OptiScaler", m is null ? L.T("Not in this game: the latest nightly loads plugins.") : optiOk ? L.F("{0}, loads plugins", there ?? m.DisplayVersion) : DlssNrAmd.CannotGoInto(m)!,
                optiOk, L.T("Show nightly builds"), () => { ShowBuilds(OptiChannel.Nightly, null); return Task.CompletedTask; }, Stops: true));
            parts.Add(new(L.T("In this game"), m?.DlssNrAmd is { } info ? L.F("{0}, network {1}", info.Tag, DlssNrAmd.GetAt(m!.TargetDir, "Network", "Model") ?? info.Network) : L.T("Laid over this game's OptiScaler."),
                m?.DlssNrAmd is not null, L.T("Put in this game"), () => PutAmdAsync()));
            inGame = L.T("In game: turn on DLSS, FSR or XeSS in the game's settings. End opens NR Control; the first start compiles for about a minute.");
        }
        else
        {
            bool f5 = m is not null && DlssNr.NeededBy(m);
            parts.Add(new(L.T("F5 build"), f5 ? m!.DisplayVersion : m is null ? L.T("Not in this game: install janblade's F5 Multipass from the OptiScaler tab.") : L.F("OptiScaler {0} is not F5: the F5 build replaces it.", m.DisplayVersion),
                f5, L.T("Show F5 builds"), () => { _settings.Dlss5Method = Dlss5.F5; SettingsStore.Save(); ShowBuilds(OptiChannel.Fork, Dlss5.F5Repo); return Task.CompletedTask; }, Stops: true));
            bool modelIn = m is not null && m.Files.ContainsKey(DlssNr.FileName);
            parts.Add(new(L.T("Model in this game"), modelIn ? L.T("Beside OptiScaler.") : L.T("Copied beside OptiScaler (uninstalling takes it out)."),
                modelIn, L.T("Put in this game"), () => SetDlssNrGameAsync(true)));
            if (tier != Dlss5Gpu.NvidiaRtx)
            {
                bool port = m is not null && DlssNr.Backend(m) == "port";
                parts.Add(new(L.T("Vendor-neutral port"), port ? L.T("In the game folder, with nr_port.ini.") : L.T("From the same release, extracted into the game folder."),
                    port, L.T("Put in this game"), () => { SetDlssNrBackend("auto"); return Task.CompletedTask; }, Stops: true));
            }
            inGame = L.T("In game: turn on DLSS, FSR or XeSS in the game's settings. Insert opens OptiScaler's menu: turn on Neural Rendering there, its status line names the backend.");
        }
        return parts;
    }

    /// <summary>
    /// DLSS 5 in this game as one card (2.0, in place of the numbered steps): how much of what it needs is there, each part
    /// as a small tile with its state, and one button that does what is left, one part after the other. A part only the
    /// user can do (pick NVIDIA's file, choose an OptiScaler build) is where it stops and asks.
    /// </summary>
    void Dlss5Setup(StackPanel page, string method, Dlss5Gpu tier, GpuInfo? gpu)
    {
        var parts = Dlss5Parts(method, tier, gpu, out var inGame);
        int done = parts.Count(p => p.Done);
        var next = parts.FirstOrDefault(p => !p.Done);
        bool ready = next is null;
        var color = Ui.ToneColor(ready ? Ui.Tone.Good : Ui.Tone.Accent);

        var head = new Grid { ColumnSpacing = 14 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Center,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ready ? color : Windows.UI.Color.FromArgb(0x33, color.R, color.G, color.B)),
            Child = ready
                ? new FontIcon { Glyph = "\uE73E", FontSize = 18, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) }
                : new TextBlock { Text = $"{done}/{parts.Count}", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = ready ? L.F("{0} is set up in this game", MethodName(method, tier)) : L.F("{0}: {1} of {2} ready", MethodName(method, tier), done, parts.Count),
            FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock { Text = ready ? inGame : L.F("Next: {0}. {1}", next!.Title, next.Detail), Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Grid.SetColumn(text, 1);
        head.Children.Add(text);
        if (next?.Run is not null)
        {
            // One part left, or a part that asks the user: its own name. More than one that run by themselves: Set up.
            bool chain = !next.Stops && parts.Count(p => !p.Done && p.Run is not null) > 1;
            var go = PropButton(chain ? L.T("Set up") : next.Action ?? L.T("Set up"), () => _ = Dlss5SetUpAsync(method));
            go.Style = Ui.Style("AccentButtonStyle");
            go.Padding = new Thickness(18, 7, 18, 8);
            go.IsEnabled = _dlss5Busy is null && !_busy;
            go.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(go, 2);
            head.Children.Add(go);
        }

        // The parts side by side, as many columns as fit; two, then one, on a narrow page.
        var strip = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        var tiles = parts.Select(p => PartTile(p, ReferenceEquals(p, next))).ToList();
        foreach (var t in tiles) strip.Children.Add(t);
        int laidOut = 0;
        void Place(double width)
        {
            int columns = width <= 0 ? tiles.Count : width >= tiles.Count * 200 ? tiles.Count : width >= 420 ? 2 : 1;
            if (columns == laidOut) return;
            laidOut = columns;
            strip.ColumnDefinitions.Clear();
            strip.RowDefinitions.Clear();
            for (int c = 0; c < columns; c++) strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < (tiles.Count + columns - 1) / columns; r++) strip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < tiles.Count; i++)
            {
                Grid.SetColumn(tiles[i], i % columns);
                Grid.SetRow(tiles[i], i / columns);
            }
        }
        Place(0);
        strip.SizeChanged += (_, e) => Place(e.NewSize.Width);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(head);
        body.Children.Add(strip);
        if (!ready)
        {
            var note = new Grid { ColumnSpacing = 8 };
            note.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            note.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            note.Children.Add(new FontIcon { Glyph = "\uE7FC", FontSize = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
            var noteText = new TextBlock { Text = inGame, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(noteText, 1);
            note.Children.Add(noteText);
            body.Children.Add(note);
        }
        page.Children.Add(new Border { Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), Child = body });
    }

    /// <summary>A part of the setup card: a tick (there), a dot in the accent colour (next) or a grey one (later), its name and state.</summary>
    static Border PartTile(Dlss5Part p, bool next)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tone = Ui.ToneColor(p.Done ? Ui.Tone.Good : next ? Ui.Tone.Accent : Ui.Tone.Unknown);
        FrameworkElement mark = p.Done
            ? new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(tone),
                Child = new FontIcon { Glyph = "\uE73E", FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) },
            }
            : new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x33, tone.R, tone.G, tone.B)),
                Child = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(tone) },
            };
        mark.VerticalAlignment = VerticalAlignment.Top;
        mark.Margin = new Thickness(0, 1, 0, 0);
        grid.Children.Add(mark);
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = p.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = p.Detail, Style = Ui.Style("RowDescription"), FontSize = 12.5, LineHeight = 17, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border { Background = Ui.Brush("LayerOnAcrylicFillColorDefaultBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 10), Child = grid };
    }

    /// <summary>
    /// Does what DLSS 5 still needs in this game, one part after the other, and stops at the first one that does not get
    /// done (cancelled, failed, or one that asks the user to choose something on another tab).
    /// </summary>
    async Task Dlss5SetUpAsync(string method)
    {
        try
        {
            for (int i = 0; i < 6; i++)
            {
                var (tier, gpu) = Dlss5.Detect();
                var next = Dlss5Parts(method, tier, gpu, out _).FirstOrDefault(p => !p.Done);
                if (next?.Run is null) break;
                await next.Run();
                if (next.Stops || _game is null) break;
                var after = Dlss5Parts(method, tier, gpu, out _).FirstOrDefault(p => !p.Done);
                if (after is null || after.Title == next.Title) break;
            }
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not set up DLSS 5"), ex.Message, ex); }
        RefreshDlss5();
    }

    /// <summary>Two rows side by side (one under the other on a narrow page).</summary>
    static Grid D5Pair(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(left);
        grid.Children.Add(right);
        bool? stacked = null;
        void Place(double width)
        {
            bool stack = width > 0 && width < 720;
            if (stacked == stack) return;
            stacked = stack;
            grid.ColumnDefinitions[1].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            grid.ColumnSpacing = stack ? 0 : 10;
            Grid.SetColumn(right, stack ? 0 : 1);
            Grid.SetRow(right, stack ? 1 : 0);
        }
        Place(0);
        grid.SizeChanged += (_, e) => Place(e.NewSize.Width);
        return grid;
    }

    // ---------- pieces ----------

    /// <summary>The DLSS 5 section's row: a name, what it says, a control. Compact since 2.0 (15 / 13 px, less air).</summary>
    static Border D5Row(string title, string detail, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 9, 12, 10), Child = grid, MinHeight = 52 };
    }

    static TextBlock D5Note(string text) => new() { Text = text, FontSize = 14, LineHeight = 20, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

    static TextBlock D5Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };

    /// <summary>Opens the OptiScaler tab and picks a channel (and fork) there, so its builds are listed.</summary>
    void ShowBuilds(OptiChannel channel, string? fork)
    {
        ShowTab("opti", section: "install");
        if (fork is not null)
        {
            AppState.Settings.SelectedFork = fork;
            SettingsStore.Save();
        }
        var card = ChannelCard(channel);
        if (card.IsChecked != true) card.IsChecked = true;
        if (fork is not null && ForkCombo.Items.OfType<ForkItem>().FirstOrDefault(i => i.Repo.Equals(fork, StringComparison.OrdinalIgnoreCase)) is { } item && !Equals(ForkCombo.SelectedItem, item))
            ForkCombo.SelectedItem = item;
        card.StartBringIntoView();
    }

    static ToggleSwitch Switch(bool on, Action<bool> changed)
    {
        var sw = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        sw.Toggled += (_, _) => changed(sw.IsOn);
        return sw;
    }

    static ComboBox Combo(IReadOnlyList<string> items, int selected, Action<int> changed)
    {
        var c = new ComboBox { MinWidth = 220 };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = selected >= 0 && selected < items.Count ? selected : -1;
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }
}
