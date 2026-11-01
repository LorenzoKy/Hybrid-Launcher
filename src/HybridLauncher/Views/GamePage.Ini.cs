using System.Globalization;
using System.Text.RegularExpressions;
using HybridLauncher.Core;
using HybridLauncher.Core.Ini;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Views;

/// <summary>
/// The OptiScaler settings tab: four views (the essentials by subject, what must be set before launch, what was changed,
/// every section of the file), each a set of cards in as many columns as fit. Every setting gets the control that suits
/// it: a switch, a slider, segmented choices, a list or a text box, with its plain name, the ini key, the description
/// from the file and a back-to-Auto button. Only the value changes in OptiScaler.ini.
/// </summary>
public sealed partial class GamePage
{
    /// <summary>The essentials, by topic: a topic shows the keys this OptiScaler version has, and is left out when it has none.</summary>
    static readonly (string Title, string Glyph, string Hint, string[] Keys)[] EssentialGroups =
    [
        ("Upscaling", "", "Which upscaler OptiScaler uses for each graphics API, and output scaling (render above the screen's resolution).",
            ["Upscalers/Dx12Upscaler", "Upscalers/Dx11Upscaler", "Upscalers/VulkanUpscaler", "FSR/Fsr4Update", "OutputScaling/Enabled", "OutputScaling/Multiplier", "UpscaleRatio/UpscaleRatioOverrideEnabled", "UpscaleRatio/UpscaleRatioOverrideValue"]),
        ("Frame generation", "", "Turn frame generation on, where its input comes from (the game's own FG, the upscaler...) and what makes the frames.",
            ["FrameGen/Enabled", "FrameGen/FGInput", "FrameGen/FGOutput", "FrameGen/DrawUIOverFG", "Framerate/FramerateLimit"]),
        ("Sharpening", "", "OptiScaler's own sharpening, on top of (or instead of) the game's.",
            ["Sharpness/OverrideSharpness", "Sharpness/Sharpness", "CAS/Enabled", "CAS/MotionSharpnessEnabled"]),
        ("GPU spoofing", "", "Make the game see an Nvidia card, so it offers DLSS / DLSS-FG inputs on AMD and Intel.",
            ["Spoofing/Dxgi", "Spoofing/Vulkan", "Spoofing/StreamlineSpoofing", "Spoofing/SpoofedGPUName"]),
        ("Overlay menu", "", "OptiScaler's in-game menu and FPS counter.",
            ["Menu/OverlayMenu", "Menu/ShortcutKey", "Menu/FpsShortcutKey", "Menu/FpsCycleShortcutKey", "Menu/FGShortcutKey", "Menu/Scale", "Menu/ShowFps"]),
        ("Plugins and logs", "", "ASI plugins, ReShade, Special K, and OptiScaler's log file.",
            ["Plugins/LoadAsiPlugins", "Plugins/LoadReshade", "Plugins/LoadSpecialK", "Log/LogLevel", "Log/LogToFile"]),
    ];

    /// <summary>
    /// Settings OptiScaler only reads at game start: not in its in-game menu, or marked there as needing a restart
    /// (from OptiScaler's menu_common.cpp / Config.cpp). Grouped for the "Before launch" topic.
    /// </summary>
    static readonly (string Title, string[] Keys)[] RestartGroups =
    [
        ("Frame generation", ["FrameGen/FGInput", "FrameGen/FGOutput", "XeFG/ForceBorderless", "XeFG/DepthInverted", "XeFG/HighResMV", "XeFG/JitteredMV", "OptiFG/DisableHUDFix", "V-Sync/OverrideVsync"]),
        ("GPU spoofing and Nvidia", ["Spoofing/Dxgi", "Spoofing/Vulkan", "Spoofing/StreamlineSpoofing", "Spoofing/SpoofedGPUName", "DLSS/UseGenericAppIdWithDlss", "fakenvapi/UseFakenvapi", "fakenvapi/LowLatencyOutput"]),
        ("Upscaler inputs", ["Inputs/EnableDlssInputs", "Inputs/EnableXeSSInputs", "Inputs/EnableFsr2Inputs", "Inputs/UseFsr2Inputs", "Inputs/Fsr2Pattern", "Inputs/EnableFsr3Inputs", "Inputs/UseFsr3Inputs", "Inputs/Fsr3Pattern", "Inputs/EnableFfxInputs", "Inputs/UseFfxInputs"]),
        ("Loading and menu", ["Plugins/LoadAsiPlugins", "Plugins/LoadReshade", "Plugins/LoadSpecialK", "Menu/OverlayMenu", "Menu/ShortcutKey", "Hooks/EarlyHooking", "Hooks/UseNtdllHooks", "Hooks/HookOriginalNvngxOnly", "Log/LogLevel", "Log/LogToFile"]),
    ];

    /// <summary>Plain names for the keys people change most; the rest are spelled out from the key ("FpsOverlayShowLatency" → "FPS overlay show latency").</summary>
    static readonly Dictionary<string, string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Upscalers/Dx12Upscaler"] = "Upscaler for DirectX 12 games",
        ["Upscalers/Dx11Upscaler"] = "Upscaler for DirectX 11 games",
        ["Upscalers/VulkanUpscaler"] = "Upscaler for Vulkan games",
        ["FSR/Fsr4Update"] = "Use FSR 4 when the GPU supports it",
        ["OutputScaling/Enabled"] = "Output scaling",
        ["OutputScaling/Multiplier"] = "Output scaling ratio",
        ["UpscaleRatio/UpscaleRatioOverrideEnabled"] = "Override the upscale ratio",
        ["UpscaleRatio/UpscaleRatioOverrideValue"] = "Upscale ratio",
        ["FrameGen/Enabled"] = "Frame generation",
        ["FrameGen/FGInput"] = "Frame generation input",
        ["FrameGen/FGOutput"] = "Frame generation output",
        ["FrameGen/DrawUIOverFG"] = "Draw the UI over generated frames",
        ["Framerate/FramerateLimit"] = "Frame rate limit",
        ["Sharpness/OverrideSharpness"] = "Override the game's sharpness",
        ["Sharpness/Sharpness"] = "Sharpness strength",
        ["CAS/Enabled"] = "RCAS sharpening",
        ["CAS/MotionSharpnessEnabled"] = "Extra sharpening in motion",
        ["Spoofing/Dxgi"] = "Spoof an Nvidia GPU (DXGI)",
        ["Spoofing/Vulkan"] = "Spoof an Nvidia GPU (Vulkan)",
        ["Spoofing/StreamlineSpoofing"] = "Spoof the GPU for Streamline",
        ["Spoofing/SpoofedGPUName"] = "Spoofed GPU name",
        ["Menu/OverlayMenu"] = "In-game overlay menu",
        ["Menu/ShortcutKey"] = "Menu key",
        ["Menu/FpsShortcutKey"] = "FPS overlay key",
        ["Menu/FpsCycleShortcutKey"] = "FPS overlay style key",
        ["Menu/FGShortcutKey"] = "Frame generation on / off key",
        ["Menu/Scale"] = "Menu size",
        ["Menu/ShowFps"] = "Show the FPS counter",
        ["Plugins/LoadAsiPlugins"] = "Load ASI plugins",
        ["Plugins/LoadReshade"] = "Load ReShade",
        ["Plugins/LoadSpecialK"] = "Load Special K",
        ["Log/LogLevel"] = "Log detail",
        ["Log/LogToFile"] = "Write a log file",
        ["XeFG/ForceBorderless"] = "XeFG: force borderless window",
        ["XeFG/DepthInverted"] = "XeFG: inverted depth",
        ["XeFG/HighResMV"] = "XeFG: high-resolution motion vectors",
        ["XeFG/JitteredMV"] = "XeFG: jittered motion vectors",
        ["OptiFG/DisableHUDFix"] = "OptiFG: turn off the HUD fix",
        ["V-Sync/OverrideVsync"] = "Override V-Sync",
        ["DLSS/UseGenericAppIdWithDlss"] = "Use a generic app id with DLSS",
        ["fakenvapi/UseFakenvapi"] = "Use fakenvapi (Reflex on AMD / Intel)",
        ["fakenvapi/LowLatencyOutput"] = "Low latency output",
        ["Inputs/EnableDlssInputs"] = "Accept DLSS inputs",
        ["Inputs/EnableXeSSInputs"] = "Accept XeSS inputs",
        ["Inputs/EnableFsr2Inputs"] = "Accept FSR 2 inputs",
        ["Inputs/UseFsr2Inputs"] = "Use FSR 2 inputs",
        ["Inputs/Fsr2Pattern"] = "FSR 2 pattern matching",
        ["Inputs/EnableFsr3Inputs"] = "Accept FSR 3 inputs",
        ["Inputs/UseFsr3Inputs"] = "Use FSR 3 inputs",
        ["Inputs/Fsr3Pattern"] = "FSR 3 pattern matching",
        ["Inputs/EnableFfxInputs"] = "Accept FidelityFX SDK inputs",
        ["Inputs/UseFfxInputs"] = "Use FidelityFX SDK inputs",
        ["Hooks/EarlyHooking"] = "Hook early",
        ["Hooks/UseNtdllHooks"] = "Use ntdll hooks",
        ["Hooks/HookOriginalNvngxOnly"] = "Hook only the original nvngx",
        ["DLSSG/ForceDMFG"] = "Dynamic frame generation",
        ["FrameGen/DynamicFG"] = "Dynamic frame generation",
        ["OptiFG/DynamicFG"] = "Dynamic frame generation",
        ["DLSSG/FramerateTargetDMFG"] = "Dynamic frame generation target",
        ["FrameGen/FGRatio"] = "Frame generation ratio",
        ["OptiFG/InterpolationCount"] = "Frame generation ratio",
        ["DLSSG/InterpolationCount"] = "Frame generation ratio (DLSSG)",
        ["XeFG/InterpolationCount"] = "Frame generation ratio (XeFG)",
    };

    /// <summary>Plain names for the values of the upscaler and frame generation choices.</summary>
    static readonly Dictionary<string, string> ValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fsr21"] = "FSR 2.1", ["fsr22"] = "FSR 2.2", ["fsr31"] = "FSR 3.1", ["ffx"] = "FidelityFX SDK (FSR 2.3 / 3.1 / 4)",
        ["xess"] = "XeSS", ["xess_12"] = "XeSS (DX11 on DX12)", ["fsr21_12"] = "FSR 2.1 (on DX12)", ["fsr22_12"] = "FSR 2.2 (on DX12)",
        ["ffx_12"] = "FidelityFX SDK (on DX12)", ["dlss"] = "DLSS",
        ["nofg"] = "Off", ["dlssg"] = "DLSS frame generation", ["nvngxfg"] = "NVNGX frame generation", ["fsrfg"] = "FSR frame generation",
        ["upscaler"] = "From the upscaler", ["fsrfg30"] = "FSR 3.0 frame generation", ["xefg"] = "XeSS frame generation (XeFG)",
    };

    // Badge colours of the essential topics, in the order of EssentialGroups.
    static readonly Windows.UI.Color[] TopicTints = [Ui.Green, Ui.Pink, Ui.Blue, Ui.Orange, Ui.Teal, Ui.Slate];

    /// <summary>Slider ranges for the numbers people tune most; other numbers get one when the file's comment gives a range.</summary>
    static readonly Dictionary<string, (double Min, double Max, double Step)> KnownRanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sharpness/Sharpness"] = (0, 1.3, 0.05),
        ["Menu/Scale"] = (0.5, 2.0, 0.05),
        ["OutputScaling/Multiplier"] = (0.5, 3.0, 0.05),
        ["UpscaleRatio/UpscaleRatioOverrideValue"] = (1.0, 3.0, 0.05),
        ["Framerate/FramerateLimit"] = (0, 360, 1),
    };

    IniDocument? _doc;
    HashSet<string> _dupKeys = new(StringComparer.OrdinalIgnoreCase);
    string _iniView = "essentials";
    bool _fillingIniBar;
    double _iniWidth = 1200;

    string? IniPath
    {
        get
        {
            var t = TargetDir;
            if (t is null) return null;
            var p = Path.Combine(t, "OptiScaler.ini");
            return File.Exists(p) ? p : null;
        }
    }

    /// <summary>Reads OptiScaler.ini again and redraws the view shown.</summary>
    void LoadIni()
    {
        _iniStale = false;
        var path = IniPath;
        _doc = null;
        if (path is not null)
        {
            try { _doc = IniDocument.Load(path); }
            catch (Exception ex) { Log.Warn($"Reading {path} failed: {ex.Message}"); }
        }
        var doc = _doc;
        IniPathText.Text = path ?? L.T("Not installed yet");
        IniUnavailable.Visibility = doc is null ? Visibility.Visible : Visibility.Collapsed;
        IniBody.Visibility = doc is null ? Visibility.Collapsed : Visibility.Visible;
        IniSearch.IsEnabled = doc is not null;
        RestoreDefaultsButton.IsEnabled = doc is not null;
        if (doc is null)
        {
            ChangedPill.Visibility = Visibility.Collapsed;
            IniRows.Child = null;
            FillQuickPanel();
            return;
        }
        _dupKeys = doc.Entries.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Older versions stored a topic key ("topic:Upscaling", "sec:Menu"): they open the view that holds it.
        _iniView = AppState.Settings.IniView switch
        {
            "restart" => "restart",
            "changed" => "changed",
            var k when k == "all" || k.StartsWith("sec:") => "all",
            _ => "essentials",
        };
        UpdateChangedPill();
        if (IniSearch.Text.Trim().Length > 0) ShowSearch(IniSearch.Text);
        else ShowView(_iniView);
        FillQuickPanel();
    }

    // ---------- Quick Panel ----------

    /// <summary>
    /// The Quick Panel's slots, each the first key this OptiScaler version has (forks and versions name some differently):
    /// frame rate cap, frame generation input and output, dynamic frame generation, the frame generation ratio, ASI plugins.
    /// </summary>
    static readonly (string Id, string Name, string[] Keys)[] QuickSlots =
    [
        ("fps", "Frame rate cap", ["Framerate/FramerateLimit"]),
        ("fgin", "Frame generation input", ["FrameGen/FGInput"]),
        ("fgout", "Frame generation output", ["FrameGen/FGOutput"]),
        ("dynfg", "Dynamic frame generation", ["DLSSG/ForceDMFG", "FrameGen/DynamicFG", "OptiFG/DynamicFG"]),
        ("ratio", "Frame generation ratio", ["FrameGen/FGRatio", "OptiFG/InterpolationCount", "DLSSG/InterpolationCount", "XeFG/InterpolationCount"]),
        ("asi", "ASI plugins", ["Plugins/LoadAsiPlugins"]),
    ];

    /// <summary>
    /// The Quick Panel's settings now: its own slots the user kept, then the settings the user added (Customize), each found in
    /// this OptiScaler.ini (a setting this version lacks is skipped). Shared by every game.
    /// </summary>
    List<(IniEntry Entry, string? SlotId)> QuickEntries(IniDocument doc)
    {
        var list = new List<(IniEntry Entry, string? SlotId)>();
        foreach (var (id, _, slot) in QuickSlots)
        {
            if (QuickHidden.Contains(id)) continue;
            var keys = slot;
            // The ratio belongs to the frame generation that makes the frames: DLSSG's own count when the output is DLSSG.
            if (slot.Contains("DLSSG/InterpolationCount") && doc.Find("FrameGen/FGOutput")?.Value.Equals("dlssg", StringComparison.OrdinalIgnoreCase) == true)
                keys = ["DLSSG/InterpolationCount", .. slot];
            if (keys.Select(doc.Find).FirstOrDefault(e => e is not null) is { } found) list.Add((found, id));
        }
        foreach (var path in QuickExtras)
            if (doc.Find(path) is { } extra && !list.Any(x => x.Entry == extra)) list.Add((extra, null));
        return list;
    }

    /// <summary>The Quick Panel's settings in use: this game's own (switch on) or the ones every game shares.</summary>
    List<string> QuickExtras => _settings.QuickPanelOwn ? _settings.QuickPanelExtras : AppState.Settings.QuickPanelExtras;
    HashSet<string> QuickHidden => _settings.QuickPanelOwn ? _settings.QuickPanelHidden : AppState.Settings.QuickPanelHidden;

    /// <summary>
    /// The two profiles. "This game only" shows the game's own Quick Panel (the first time, a copy of the Global one to start
    /// from); "Global" loads the panel every game set to Global shares. Each profile keeps its settings when the other is chosen.
    /// </summary>
    void QuickScope_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_game is null || _syncingQuickScope || sender.SelectedItem is not SelectorBarItem { Tag: string tag }) return;
        bool own = tag == "own";
        if (own == _settings.QuickPanelOwn) return;
        if (own && !_settings.QuickPanelOwnMade)
        {
            _settings.QuickPanelExtras = [.. AppState.Settings.QuickPanelExtras];
            _settings.QuickPanelHidden = new HashSet<string>(AppState.Settings.QuickPanelHidden, StringComparer.OrdinalIgnoreCase);
            _settings.QuickPanelOwnMade = true;
        }
        _settings.QuickPanelOwn = own;
        SettingsStore.Save();
        FillQuickPanel();
    }

    bool _syncingQuickScope;

    /// <summary>Redraws the Quick Panel from the OptiScaler.ini read last (nothing to show until OptiScaler is installed).</summary>
    void FillQuickPanel()
    {
        _syncingQuickScope = true;
        QuickScopeBar.SelectedItem = _settings.QuickPanelOwn ? QuickScopeOwn : QuickScopeGlobal;
        _syncingQuickScope = false;
        QuickScopeText.Text = _settings.QuickPanelOwn ? L.F("This game only: what you add or remove stays with {0}.", _game?.Title) : L.T("Global: the panel every game set to Global shares; a change here shows on all of them.");
        if (_doc is not { } doc)
        {
            QuickPanelRows.Child = Ui.Muted(L.T("Install OptiScaler to use the Quick Panel."));
            return;
        }
        var entries = QuickEntries(doc);
        if (entries.Count == 0)
        {
            var empty = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };
            empty.Children.Add(Ui.Muted(L.T("The Quick Panel is empty. Add the settings you change most with +.")));
            empty.Children.Add(AddQuickTile(doc));
            QuickPanelRows.Child = empty;
            return;
        }
        var grid = new TileGrid(_iniWidth < 900 ? 10_000 : 440, 10);
        foreach (var (e, slot) in entries)
        {
            // A change here redraws the settings cards (and this panel: the ratio follows the output).
            var editor = Editor(e, written: () =>
            {
                if (_doc is null) return;
                if (IniSearch.Text.Trim().Length > 0) ShowSearch(IniSearch.Text);
                else ShowView(_iniView);
                if (e.Path.Equals("FrameGen/FGOutput", StringComparison.OrdinalIgnoreCase)) FillQuickPanel();
            });
            // The X takes the setting out of the panel in one click (it stays in Settings; + or Customize brings it back).
            var x = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                Padding = new Thickness(6),
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Opacity = 0.55,
            };
            ToolTipService.SetToolTip(x, L.T("Remove from the Quick Panel"));
            var (removePath, removeSlot) = (e.Path, slot);
            x.Click += (_, _) => RemoveQuick(removePath, removeSlot);
            var line = new Grid { ColumnSpacing = 4 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(editor);
            Grid.SetColumn(x, 1);
            line.Children.Add(x);
            line.PointerEntered += (_, _) => x.Opacity = 1;
            line.PointerExited += (_, _) => x.Opacity = 0.55;
            var row = new Border
            {
                Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowStyle"],
                MinHeight = 0,
                Padding = new Thickness(12, 2, 6, 2),
                Child = line,
            };
            // Right click: take it out of the panel (it stays in Settings, and Customize brings it back).
            var remove = new MenuFlyoutItem { Text = L.T("Remove from the Quick Panel"), Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) => RemoveQuick(e.Path, slot);
            var menu = new MenuFlyout();
            menu.Items.Add(remove);
            row.ContextFlyout = menu;
            grid.Children.Add(row);
        }
        grid.Children.Add(AddQuickTile(doc));
        QuickPanelRows.Child = grid;
    }

    /// <summary>The panel's last slot: a + that opens a search over every OptiScaler setting to add one.</summary>
    FrameworkElement AddQuickTile(IniDocument doc)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 14 });
        content.Children.Add(new TextBlock { Text = L.T("Add a setting"), VerticalAlignment = VerticalAlignment.Center });
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 48,
            Opacity = 0.8,
            BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(8),
            Flyout = flyout,
        };
        ToolTipService.SetToolTip(button, L.T("Add any OptiScaler.ini setting to the Quick Panel"));
        flyout.Opening += (_, _) =>
        {
            var panel = new StackPanel { Spacing = 8, Width = 380 };
            panel.Children.Add(new TextBlock { Text = L.T("ADD A SETTING"), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["GroupHeader"] });
            var box = QuickAddBox(doc, flyout.Hide);
            panel.Children.Add(box);
            flyout.Content = panel;
        };
        flyout.Opened += (_, _) => { if (flyout.Content is StackPanel { Children: [_, AutoSuggestBox b] }) b.Focus(FocusState.Programmatic); };
        return button;
    }

    /// <summary>The search that adds a setting to the Quick Panel (the + slot and Customize).</summary>
    AutoSuggestBox QuickAddBox(IniDocument doc, Action done)
    {
        var box = new AutoSuggestBox { PlaceholderText = L.T("Search every OptiScaler setting"), QueryIcon = new SymbolIcon(Symbol.Find) };
        box.TextChanged += (b, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = b.Text.Trim();
            var taken = QuickEntries(doc).Select(t => t.Entry).ToHashSet();
            b.ItemsSource = q.Length < 2 ? null : doc.Entries
                .Where(en => !taken.Contains(en) && (FriendlyName(en).Contains(q, StringComparison.CurrentCultureIgnoreCase) || en.Path.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .Take(30).Select(en => new QuickPick(en.Path, $"{FriendlyName(en)}  ·  [{en.Section}] {en.Key}")).ToList();
        };
        box.SuggestionChosen += (b, args) =>
        {
            if (args.SelectedItem is not QuickPick pick) return;
            if (!QuickExtras.Contains(pick.Path, StringComparer.OrdinalIgnoreCase)) QuickExtras.Add(pick.Path);
            SettingsStore.Save();
            FillQuickPanel();
            done();
        };
        return box;
    }

    void RemoveQuick(string path, string? slot)
    {
        if (slot is not null) QuickHidden.Add(slot);
        else QuickExtras.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save();
        FillQuickPanel();
    }

    /// <summary>
    /// Customize: what the panel holds (each with a button to take it out), a search over every setting of this OptiScaler.ini
    /// to add more, and the way back to the panel's own set. Applies to every game.
    /// </summary>
    void QuickCustomize_Opening(object sender, object e)
    {
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        var panel = new StackPanel { Spacing = 8, Width = 400 };
        panel.Children.Add(new TextBlock { Text = L.T("IN THE QUICK PANEL"), Style = (Style)res["GroupHeader"] });
        panel.Children.Add(Ui.Muted(_settings.QuickPanelOwn ? L.F("Profile: This game only ({0})", _game?.Title) : L.T("Profile: Global (every game set to Global)")));
        if (_doc is { } doc)
        {
            var now = QuickEntries(doc);
            if (now.Count == 0) panel.Children.Add(Ui.Muted(L.T("Nothing yet.")));
            foreach (var (entry, slot) in now)
            {
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = FriendlyName(entry), TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = $"[{entry.Section}] {entry.Key}", Style = (Style)res["RowDescription"], FontSize = 11 });
                line.Children.Add(text);
                var x = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(8, 6, 8, 6), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(x, L.T("Remove from the Quick Panel"));
                var (p, s) = (entry.Path, slot);
                x.Click += (_, _) => { RemoveQuick(p, s); QuickCustomizeFlyout.Hide(); };
                Grid.SetColumn(x, 1);
                line.Children.Add(x);
                panel.Children.Add(line);
            }

            panel.Children.Add(new TextBlock { Text = L.T("ADD A SETTING"), Style = (Style)res["GroupHeader"], Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(QuickAddBox(doc, QuickCustomizeFlyout.Hide));
        }
        else panel.Children.Add(Ui.Muted(L.T("Install OptiScaler first: the settings come from its OptiScaler.ini.")));

        var reset = new Button { Content = L.T("Back to the Quick Panel's own settings"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) =>
        {
            QuickExtras.Clear();
            QuickHidden.Clear();
            SettingsStore.Save();
            FillQuickPanel();
            QuickCustomizeFlyout.Hide();
        };
        panel.Children.Add(reset);
        QuickCustomizeFlyout.Content = panel;
    }

    /// <summary>A suggestion of the Customize search: shown by its text, added by its "Section/Key".</summary>
    sealed record QuickPick(string Path, string Text)
    {
        public override string ToString() => Text;
    }

    void IniViewBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_fillingIniBar || sender.SelectedItem is not SelectorBarItem { Tag: string view }) return;
        if (AppState.Settings.IniView != view)
        {
            AppState.Settings.IniView = view;
            SettingsStore.Save();
        }
        if (IniSearch.Text.Length > 0) IniSearch.Text = "";
        ShowView(view);
    }

    /// <summary>Essentials (one card per topic), Before launch (one card per group), Changed (one per section), All sections (folded cards).</summary>
    void ShowView(string view)
    {
        if (_doc is not { } doc) return;
        _iniView = view;
        _fillingIniBar = true;
        try
        {
            var item = view switch { "restart" => IniRestartItem, "changed" => IniChangedItem, "all" => IniAllItem, _ => IniEssentialsItem };
            if (IniViewBar.SelectedItem != item) IniViewBar.SelectedItem = item;
        }
        finally { _fillingIniBar = false; }

        RestartNote.Visibility = view == "restart" ? Visibility.Visible : Visibility.Collapsed;
        var cards = new List<FrameworkElement>();
        switch (view)
        {
            case "restart":
                IniPaneTitle.Text = L.T("Before launch");
                IniPaneHint.Text = L.T("Settings OptiScaler reads only when the game starts.");
                foreach (var (title, keys) in RestartGroups)
                {
                    var entries = Find(doc, keys);
                    if (entries.Count > 0) cards.Add(SettingsCard("", title, null, entries, Ui.Orange, showSection: true));
                }
                break;
            case "changed":
                var changed = doc.Entries.Where(e => !IsAuto(e.Value)).GroupBy(e => e.Section).ToList();
                IniPaneTitle.Text = changed.Count == 0 ? L.T("Nothing changed") : L.T("Changed settings");
                IniPaneHint.Text = L.T("Every setting that is not on Auto, in this game's OptiScaler.ini.");
                foreach (var g in changed) cards.Add(SettingsCard("", $"[{g.Key}]", null, g.ToList(), Ui.Blue));
                if (changed.Count == 0)
                    cards.Add(Ui.Card(Ui.Muted(L.T("Every setting is on Auto: OptiScaler decides. Change one in Essentials or All sections and it shows up here."))));
                break;
            case "all":
                IniPaneTitle.Text = L.T("All sections");
                IniPaneHint.Text = L.F("Every section of OptiScaler.ini ({0} settings). Open a section to change its settings.", doc.Entries.Count);
                foreach (var g in doc.Entries.GroupBy(e => e.Section)) cards.Add(SectionCard(g.Key, g.ToList()));
                break;
            default:
                IniPaneTitle.Text = L.T("Essentials");
                IniPaneHint.Text = L.T("The settings people change most, by subject. Auto lets OptiScaler decide.");
                for (int i = 0; i < EssentialGroups.Length; i++)
                {
                    var (title, glyph, hint, keys) = EssentialGroups[i];
                    var entries = Find(doc, keys);
                    if (entries.Count > 0) cards.Add(SettingsCard(glyph, title, hint, entries, TopicTints[i % TopicTints.Length]));
                }
                break;
        }
        IniRows.Child = Masonry(cards, stable: view == "all");
    }

    static List<IniEntry> Find(IniDocument doc, IEnumerable<string> keys) => keys.Select(doc.Find).Where(e => e is not null).Select(e => e!).ToList();

    MasonryPanel Masonry(IEnumerable<FrameworkElement> cards, bool stable = false)
    {
        // Cards in reading order, row by row, every card of a row as tall as the row: an even grid, not a jagged wall.
        var panel = new MasonryPanel(_iniWidth < 900 ? 10_000 : 440, 12) { Stable = stable, Rows = !stable };
        foreach (var c in cards) panel.Children.Add(c);
        return panel;
    }

    /// <summary>
    /// A card of settings: badge, title and count, the hint, then one editor per key. The heading folds the card (remembered per
    /// card for every game); a folded card keeps its heading and how many of its settings are changed.
    /// </summary>
    Border SettingsCard(string glyph, string title, string? hint, List<IniEntry> entries, Windows.UI.Color tint, bool showSection = false)
    {
        // The fold state is saved under the English title, so it survives a change of language.
        var key = "ini:" + title;
        bool open = !AppState.Settings.CollapsedCards.Contains(key);
        int changed = entries.Count(e => !IsAuto(e.Value));
        var count = L.N(entries.Count, "{0} setting", "{0} settings") + (changed > 0 ? "  ·  " + L.F("{0} changed", changed) : "");

        var chevron = Motion.Chevron(open);
        chevron.Margin = new Thickness(8, 0, 6, 0);
        var heading = Ui.Heading(glyph, L.T(title), chevron, subtitle: count, tint: tint);
        heading.Margin = new Thickness(0);
        // One button over the whole heading: a click anywhere on it (the chevron too) folds or opens the card exactly once.
        var header = new Button { Content = heading, Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["FoldHeaderButton"], Margin = new Thickness(-6, -4, -6, 2) };
        ToolTipService.SetToolTip(header, L.T("Fold or open this card"));

        var body = new StackPanel { Spacing = 4, Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        if (hint is not null) body.Children.Add(new TextBlock { Text = L.T(hint), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"], Margin = new Thickness(0, -2, 0, 6) });
        // Built when first opened: folded cards cost nothing.
        void Build()
        {
            if (body.Children.Count > (hint is null ? 0 : 1)) return;
            foreach (var e in entries) body.Children.Add(Editor(e, showSection));
        }
        if (open) Build();

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(header);
        panel.Children.Add(body);
        var card = Ui.Card(panel);
        card.Tag = open ? null : "folded";
        void Relayout() => (card.Parent as MasonryPanel)?.InvalidateMeasure();
        header.Click += (_, _) =>
        {
            open = !open;
            Motion.Turn(chevron, open);
            card.Tag = open ? null : "folded";
            if (open)
            {
                Build();
                Motion.Open(body);
                Relayout();
                AppState.Settings.CollapsedCards.Remove(key);
            }
            else
            {
                Motion.Close(body, Relayout);
                AppState.Settings.CollapsedCards.Add(key);
            }
            SettingsStore.Save();
        };
        return card;
    }

    /// <summary>A section of the file, folded: its editors are built the first time it opens (the file has hundreds of keys).</summary>
    FrameworkElement SectionCard(string section, List<IniEntry> entries)
    {
        int changed = entries.Count(e => !IsAuto(e.Value));
        var header = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(Ui.Badge("", changed > 0 ? Ui.Blue : Ui.Slate, 30));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = $"[{section}]", FontWeight = FontWeights.SemiBold, FontSize = 15 });
        text.Children.Add(new TextBlock { Text = L.N(entries.Count, "{0} setting", "{0} settings") + (changed > 0 ? "  ·  " + L.F("{0} changed", changed) : ""), Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["RowDescription"] });
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        var body = new StackPanel { Spacing = 4 };
        var expander = new Expander { Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        expander.Expanding += (_, _) =>
        {
            if (body.Children.Count > 0) return;
            foreach (var e in entries) body.Children.Add(Editor(e));
        };
        return expander;
    }

    void IniSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput || sender.Text.Length == 0) ShowSearch(sender.Text);
    }

    /// <summary>Matches in key names, plain names, sections and descriptions, one card per section.</summary>
    void ShowSearch(string text)
    {
        text = text.Trim();
        if (_doc is null) return;
        if (text.Length == 0)
        {
            ShowView(_iniView);
            return;
        }
        var hits = _doc.Entries.Where(e => e.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                                        || FriendlyName(e).Contains(text, StringComparison.OrdinalIgnoreCase)
                                        || e.Section.Contains(text, StringComparison.OrdinalIgnoreCase)
                                        || e.Comment.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(80).ToList();
        IniPaneTitle.Text = hits.Count == 0 ? L.T("No setting matches") : L.F("{0} settings match \"{1}\"", hits.Count + (hits.Count == 80 ? "+" : ""), text);
        IniPaneHint.Text = L.T("Searched in every section of OptiScaler.ini. Clear the search to go back.");
        RestartNote.Visibility = Visibility.Collapsed;
        IniRows.Child = Masonry(hits.GroupBy(e => e.Section).Select(g => (FrameworkElement)SettingsCard("", $"[{g.Key}]", null, g.ToList(), Ui.Slate)));
    }

    // ---------- editors ----------

    /// <summary>
    /// One setting: its plain name, the ini key and the file's description, with the control that suits the value:
    /// a switch for true / false, a slider for numbers with a range, segmented buttons for a few choices, a list for many,
    /// a text box otherwise. Auto shows OptiScaler's default, dimmed; a changed setting gets an accent bar and a back-to-Auto button.
    /// </summary>
    FrameworkElement Editor(IniEntry e, bool showSection = false, Action? written = null)
    {
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 8), Background = (Brush)res["AccentFillColorDefaultBrush"] };
        var reset = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(7, 6, 7, 6), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        var defaultNote = e.DefaultText is null ? "" : " " + L.F("(default: {0})", Shorten(e.DefaultText));
        ToolTipService.SetToolTip(reset, L.T("Back to Auto") + defaultNote);

        // Name line: name, then (right) the reset button and the compact control or value.
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        name.Children.Add(new TextBlock { Text = FriendlyName(e), FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        name.Children.Add(new TextBlock
        {
            Text = showSection || _dupKeys.Contains(e.Key) ? $"[{e.Section}] {e.Key}" : e.Key,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = (Brush)res["TextFillColorTertiaryBrush"],
            IsTextSelectionEnabled = true,
        });
        ToolTipService.SetToolTip(name, new ToolTip { Content = new TextBlock { Text = $"[{e.Section}] {e.Key}\n\n{e.Comment}", TextWrapping = TextWrapping.Wrap, MaxWidth = 520 } });

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(reset);
        FrameworkElement? below = null;
        Action sync = () => { };
        void Changed()
        {
            bool isChanged = !IsAuto(e.Value);
            reset.Visibility = isChanged ? Visibility.Visible : Visibility.Collapsed;
            bar.Opacity = isChanged ? 1 : 0;
            sync();
            UpdateChangedPill();
        }
        bool Write(string value)
        {
            if (value.Equals(e.Value, StringComparison.OrdinalIgnoreCase)) return false;
            if (!WriteIni(e, value)) return false;
            Changed();
            // The Quick Panel and the settings cards show the same keys: the other one is redrawn with the new value.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => (written ?? FillQuickPanel)());
            return true;
        }

        // Frame generation ratios ("1 = 2X | 2 = 3X") are choices whatever the file calls them (DLSSG's says "true or false").
        var numbered = e.Kind != IniValueKind.Choice ? RatioChoices(e) : null;
        var range = numbered is null && e.Kind == IniValueKind.Text ? RangeOf(e) : null;
        numbered ??= e.Kind == IniValueKind.Text && range is null ? NumberedChoices(e) : null;

        if (Controls.KeyCapture.IsKeySetting(e.Key, e.Comment))
        {
            // A key: shown by its name, set by pressing it (no virtual-key codes to look up).
            var capture = new Controls.KeyCapture();
            int? def = Controls.KeyCapture.DefaultFromComment(e.Comment);
            sync = () => capture.Set(e.Value, def);
            capture.KeyChosen += v => { if (!Write(v)) sync(); };
            right.Children.Add(capture);
            reset.Click += (_, _) => Write("auto");
        }
        else if (e.Kind == IniValueKind.Bool && numbered is null)
        {
            // A switch: Auto shows the default position, labelled "Auto"; flipping it writes true / false.
            bool? def = e.DefaultText?.Trim().ToLowerInvariant() switch { var d when d is not null && d.StartsWith("true") => true, var d when d is not null && d.StartsWith("false") => false, _ => null };
            var toggle = new ToggleSwitch { MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            bool loading = false;
            sync = () =>
            {
                loading = true;
                bool auto = IsAuto(e.Value);
                toggle.IsOn = auto ? def == true : e.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                toggle.OnContent = auto ? L.T("Auto (on)") : L.T("On");
                toggle.OffContent = auto ? (def is null ? "Auto" : L.T("Auto (off)")) : L.T("Off");
                toggle.Opacity = auto ? 0.7 : 1;
                loading = false;
            };
            toggle.Toggled += (_, _) => { if (!loading) Write(toggle.IsOn ? "true" : "false"); };
            right.Children.Add(toggle);
            reset.Click += (_, _) => Write("auto");
        }
        else if (range is { } r)
        {
            // A slider over the range, the value (or "Auto · default") on the right; written a moment after it stops moving.
            double def = ParseNumber(e.DefaultText) ?? r.Min;
            var value = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Right };
            var slider = new Slider { Minimum = r.Min, Maximum = r.Max, StepFrequency = r.Step, SmallChange = r.Step, LargeChange = r.Step * 10, Margin = new Thickness(0, -4, 0, 0) };
            ToolTipService.SetToolTip(slider, L.F("{0} to {1}", Format(r.Min, r.Step), Format(r.Max, r.Step)) + defaultNote);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            bool loading = false;
            sync = () =>
            {
                loading = true;
                bool auto = IsAuto(e.Value);
                slider.Value = Math.Clamp(auto ? def : ParseNumber(e.Value) ?? def, r.Min, r.Max);
                value.Text = auto ? $"Auto · {Format(def, r.Step)}" : Format(slider.Value, r.Step);
                value.Opacity = auto ? 0.65 : 1;
                slider.Opacity = auto ? 0.75 : 1;
                loading = false;
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!Write(Format(slider.Value, r.Step))) sync();
            };
            slider.ValueChanged += (_, _) =>
            {
                if (loading) return;
                value.Text = Format(slider.Value, r.Step);
                value.Opacity = 1;
                timer.Stop();
                timer.Start();
            };
            // Leaving the page or the tab never loses a value still waiting on the timer.
            slider.Unloaded += (_, _) => { if (timer.IsEnabled) { timer.Stop(); Write(Format(slider.Value, r.Step)); } };
            right.Children.Add(value);
            below = slider;
            reset.Click += (_, _) => { timer.Stop(); Write("auto"); };
        }
        else if (e.Kind == IniValueKind.Choice || numbered is not null)
        {
            var values = new List<(string Value, string Label)> { ("auto", "Auto") };
            if (numbered is not null) values.AddRange(numbered);
            else values.AddRange(e.Options.Select(o => (o, ValueNames.TryGetValue(o, out var n) ? L.T(n) : o)));
            if (!values.Any(v => v.Value.Equals(e.Value, StringComparison.OrdinalIgnoreCase))) values.Add((e.Value, e.Value));

            if (values.Count <= 5 && values.Sum(v => v.Label.Length) <= 46)
            {
                // A few short choices: segmented buttons, all visible at once.
                var segments = new Grid { ColumnSpacing = 4 };
                var buttons = new List<ToggleButton>();
                bool loading = false;
                for (int i = 0; i < values.Count; i++)
                {
                    segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var (v, label) = values[i];
                    var b = new ToggleButton { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 5, 8, 6), MinWidth = 0, FontSize = 13 };
                    if (v == "auto" && e.DefaultText is not null) ToolTipService.SetToolTip(b, L.T("OptiScaler decides") + defaultNote);
                    else if (label != v) ToolTipService.SetToolTip(b, v);
                    b.Click += (_, _) => { if (!loading && !Write(v)) sync(); };
                    Grid.SetColumn(b, i);
                    segments.Children.Add(b);
                    buttons.Add(b);
                }
                sync = () =>
                {
                    loading = true;
                    for (int i = 0; i < values.Count; i++) buttons[i].IsChecked = values[i].Value.Equals(IsAuto(e.Value) ? "auto" : e.Value, StringComparison.OrdinalIgnoreCase);
                    loading = false;
                };
                below = segments;
            }
            else
            {
                var combo = new ComboBox { MinWidth = 200, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center };
                bool loading = false;
                foreach (var (v, label) in values)
                    combo.Items.Add(new ComboBoxItem { Content = v == "auto" ? "Auto" + defaultNote : label == v ? v : $"{label}  ({v})", Tag = v });
                sync = () =>
                {
                    loading = true;
                    combo.SelectedIndex = values.FindIndex(v => v.Value.Equals(IsAuto(e.Value) ? "auto" : e.Value, StringComparison.OrdinalIgnoreCase));
                    loading = false;
                };
                combo.SelectionChanged += (_, _) => { if (!loading && combo.SelectedItem is ComboBoxItem { Tag: string v }) Write(v); };
                right.Children.Add(combo);
            }
            reset.Click += (_, _) => Write("auto");
        }
        else
        {
            var box = new TextBox { MinWidth = 200, MaxWidth = 300, PlaceholderText = "Auto" + defaultNote, VerticalAlignment = VerticalAlignment.Center };
            sync = () => { if (box.FocusState == FocusState.Unfocused) box.Text = IsAuto(e.Value) ? "" : e.Value; };
            void Commit() => Write(string.IsNullOrWhiteSpace(box.Text) ? "auto" : box.Text.Trim());
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, k) => { if (k.Key == Windows.System.VirtualKey.Enter) Commit(); };
            right.Children.Add(box);
            reset.Click += (_, _) => { box.Text = ""; Commit(); };
        }

        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 6, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRowSpan(bar, 3);
        grid.Children.Add(bar);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        int row = 1;
        if (below is not null)
        {
            Grid.SetRow(below, row++);
            Grid.SetColumn(below, 1);
            Grid.SetColumnSpan(below, 2);
            grid.Children.Add(below);
        }
        var description = Description(e);
        if (description.Length > 0)
        {
            var d = new TextBlock { Text = description, Style = (Style)res["RowDescription"], MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis };
            ToolTipService.SetToolTip(d, new ToolTip { Content = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 } });
            Grid.SetRow(d, row);
            Grid.SetColumn(d, 1);
            Grid.SetColumnSpan(d, 2);
            grid.Children.Add(d);
        }
        Changed();
        return grid;
    }

    static double? ParseNumber(string? s)
    {
        if (s is null) return null;
        var m = Regex.Match(s, @"-?\d+(?:\.\d+)?");
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>The value snapped to the step, as OptiScaler.ini writes it ("0.35", "144").</summary>
    static string Format(double v, double step)
    {
        double snapped = Math.Round(v / step) * step;
        return step >= 1 ? Math.Round(snapped).ToString(CultureInfo.InvariantCulture) : Math.Round(snapped, 2).ToString("0.0#", CultureInfo.InvariantCulture);
    }

    /// <summary>The slider range of a number: a known one, or "0.5 to 2.0" / "0.5 - 3.0" / "between 0.0 and 1.0" in the file's comment.</summary>
    static (double Min, double Max, double Step)? RangeOf(IniEntry e)
    {
        if (KnownRanges.TryGetValue(e.Path, out var known)) return known;
        if (!IsAuto(e.Value) && ParseNumber(e.Value) is null) return null;
        foreach (var line in e.Comment.Split('\n'))
        {
            var m = Regex.Match(line, @"(?:between\s+)?(-?\d+(?:\.\d+)?)\s*(?:to|-|–|and)\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
                || !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) || max <= min) continue;
            bool ints = !m.Groups[1].Value.Contains('.') && !m.Groups[2].Value.Contains('.');
            double step = ints ? 1 : max - min > 5 ? 0.1 : 0.05;
            if (ints && max - min > 1000) continue;
            return (min, max, step);
        }
        return null;
    }

    /// <summary>"0 = Trace / 1 = Debug / 2 = Info" in the comment: numbered choices with names.</summary>
    /// <summary>"1 = 2X | 2 = 3X | 3 = 4X" in the comment: frame generation ratios.</summary>
    static List<(string Value, string Label)>? RatioChoices(IniEntry e)
    {
        var ratios = e.Comment.Split('\n').Select(l => Regex.Matches(l, @"\b(\d+)\s*=\s*(\d+X)\b")).FirstOrDefault(m => m.Count >= 2);
        return ratios?.Select(m => (m.Groups[1].Value, m.Groups[2].Value)).DistinctBy(v => v.Item1).ToList();
    }

    static List<(string Value, string Label)>? NumberedChoices(IniEntry e)
    {
        var line = e.Comment.Split('\n').FirstOrDefault(l => Regex.Matches(l, @"\b\d+\s*=\s*[A-Za-z]").Count >= 2);
        if (line is null) return null;
        var list = Regex.Matches(line, @"\b(\d+)\s*=\s*([A-Za-z][A-Za-z0-9 +\-]*?)\s*(?=/|,|;|\b\d+\s*=|$)")
            .Select(m => (m.Groups[1].Value, $"{m.Groups[2].Value.Trim()}")).DistinctBy(v => v.Item1).ToList();
        return list.Count >= 2 ? list : null;
    }

    /// <summary>The key's plain name: from <see cref="KeyNames"/>, or its words spelled out with the usual acronyms.</summary>
    static readonly Dictionary<string, string> KeyHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ShortcutKey"] = "Opens and closes OptiScaler's menu in game.",
        ["FpsShortcutKey"] = "Shows or hides OptiScaler's FPS counter (OSD).",
        ["FpsCycleShortcutKey"] = "Switches the FPS counter between its styles.",
        ["FGShortcutKey"] = "Turns OptiScaler's frame generation on and off while playing.",
    };

    /// <summary>
    /// The OptiScaler tab's step 3: the keys OptiScaler listens to in game (menu, FPS counter...), each set by pressing it.
    /// Read from the installed OptiScaler.ini; hidden when OptiScaler is not installed.
    /// </summary>
    void FillShortcutKeys()
    {
        KeysRows.Children.Clear();
        var path = IniPath;
        List<IniEntry> keys = [];
        if (path is not null)
        {
            try { keys = IniDocument.Load(path).Entries.Where(x => x.Section.Equals("Menu", StringComparison.OrdinalIgnoreCase) && Controls.KeyCapture.IsKeySetting(x.Key, x.Comment)).ToList(); }
            catch (Exception ex) { Log.Warn($"Shortcut keys from {path}: {ex.Message}"); }
        }
        KeysPanel.Visibility = keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in keys)
        {
            var capture = new Controls.KeyCapture();
            int? def = Controls.KeyCapture.DefaultFromComment(e.Comment);
            capture.Set(e.Value, def);
            capture.KeyChosen += v =>
            {
                if (WriteIni(e, v)) _iniStale = true;
                capture.Set(e.Value, def);
            };
            KeysRows.Children.Add(Row(FriendlyName(e), KeyHints.TryGetValue(e.Key, out var hint) ? L.T(hint) : e.Summary, capture));
        }
    }

    static string FriendlyName(IniEntry e)
    {
        if (KeyNames.TryGetValue(e.Path, out var name)) return L.T(name);
        var words = Regex.Replace(e.Key, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var acronyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "fps", "ui", "hud", "fg", "fsr", "dlss", "dlssg", "dlssd", "mv", "gpu", "cpu", "ram", "vram", "dxgi", "asi", "cas", "rcas", "hdr", "drs", "vk", "dx", "rtv", "srv", "uav", "om", "di", "sl", "ngx", "nvngx", "ttf", "hq", "fov", "id", "bg", "rgb", "fp", "rr", "xell", "xefg", "ffx", "dmfg", "mfg", "hags", "sdk" };
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (acronyms.Contains(w)) words[i] = w.ToUpperInvariant();
            else if (i > 0 && !(w.Length > 1 && w.All(char.IsUpper))) words[i] = w.ToLowerInvariant();
        }
        var text = string.Join(' ', words);
        return Regex.Replace(text, @"\bXe SS\b|\bXESS\b", "XeSS").Replace("DX 11", "DX11").Replace("DX 12", "DX12").Replace("XEFG", "XeFG").Replace("XELL", "XeLL");
    }

    /// <summary>The file's description without the "true or false - Default (auto) is..." line (the control shows that).</summary>
    static string Description(IniEntry e)
    {
        var lines = e.Comment.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("true or false", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(l, @"^Default\s*\(auto\)", RegexOptions.IgnoreCase))
            .Where(l => !(e.Kind == IniValueKind.Choice && (l.Contains(" - Default", StringComparison.OrdinalIgnoreCase) || IniDocument.OptionList([l], needsDefaultAfter: false) is not null)));
        return string.Join(" ", lines);
    }

    void UpdateChangedPill()
    {
        int changed = _doc?.Entries.Count(x => !IsAuto(x.Value)) ?? 0;
        ChangedPill.Visibility = changed > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangedPillText.Text = L.F("{0} changed", changed);
        ToolTipService.SetToolTip(ChangedPill, L.T("Settings of this game that are not on Auto (see Changed)"));
    }

    /// <summary>Search beside the title on wide pages, under it otherwise; cards in one column on narrow pages.</summary>
    void LayoutIni(double width)
    {
        bool narrowHeader = width < 760;
        Grid.SetRow(IniSearch, narrowHeader ? 1 : 0);
        Grid.SetColumn(IniSearch, narrowHeader ? 0 : 1);
        Grid.SetColumnSpan(IniSearch, narrowHeader ? 4 : 1);
        IniSearch.Width = narrowHeader ? double.NaN : width < 1100 ? 220 : 280;
        IniSearch.HorizontalAlignment = narrowHeader ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        bool wasNarrow = _iniWidth < 900;
        _iniWidth = width;
        if (wasNarrow != width < 900 && IniRows.Child is MasonryPanel m) m.MinColumnWidth = width < 900 ? 10_000 : 440;
        if (wasNarrow != width < 900 && _doc is not null) FillQuickPanel();
    }

    /// <summary>Self-test hook: opens a view of the settings tab ("essentials", "restart", "changed", "all") and searches once.</summary>
    void SelfTestIni(string view)
    {
        if (_doc is null) return;
        ShowView(view);
        if (view == "all")
        {
            if (IniRows.Child is Panel p && p.Children.OfType<Expander>().FirstOrDefault() is { } first) first.IsExpanded = true;
            IniSearch.Text = "fsr";
            ShowSearch("fsr");
            IniSearch.Text = "";
            ShowSearch("");
        }
    }

    static bool IsAuto(string value) => value.Length == 0 || value.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes one key to OptiScaler.ini and remembers it for reinstalls. Returns false when nothing was written.</summary>
    bool WriteIni(IniEntry e, string value)
    {
        var path = IniPath;
        if (path is null) return false;
        try
        {
            // Re-read first so changes made elsewhere (OptiScaler's own menu, a text editor) are not lost.
            var doc = IniDocument.Load(path);
            if (!doc.Set(e.Section, e.Key, value)) return false;
            doc.Save();
            e.Value = value;
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) _settings.IniOverrides.Remove(e.Path);
            else _settings.IniOverrides[e.Path] = value;
            SettingsStore.Save();
            return true;
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not save OptiScaler.ini"), ex.Message + (OptiInstaller.RunningGameProcess(Path.GetDirectoryName(path)!) is { } p ? " " + L.F("({0} is running)", p) : ""));
            return false;
        }
    }

    static string Shorten(string s) => s.Length > 28 ? s[..26] + "…" : s;

    /// <summary>
    /// Puts OptiScaler.ini back as the install left it (the copy taken at install), or every setting on Auto for installs made
    /// before Hybrid Launcher kept that copy. The choices Hybrid Launcher re-applies after reinstalls are cleared too; profiles stay.
    /// </summary>
    async void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var path = IniPath;
        if (path is null || _game is null) return;
        var settings = _settings;
        var snapshot = settings.Install?.DefaultIni is { } d && File.Exists(d) ? d : null;
        if (!await ConfirmAsync(L.T("Restore the default settings?"),
                snapshot is not null
                    ? L.T("Every setting of this game's OptiScaler.ini goes back to how it was right after the install. The choices Hybrid Launcher applies again after a reinstall are cleared; saved profiles stay.")
                    : L.T("This install was made before Hybrid Launcher kept a copy of the original OptiScaler.ini, so every setting goes back to Auto (OptiScaler's own default). The choices Hybrid Launcher applies again after a reinstall are cleared; saved profiles stay."),
                L.T("Restore defaults")))
            return;
        try
        {
            int changed = await Task.Run(() => OptiInstaller.RestoreDefaultIni(path, snapshot));
            settings.IniOverrides.Clear();
            SettingsStore.Save();
            _iniStale = true;
            if (_tab == "settings") LoadIni();
            App.Window?.ShowSuccess(changed == 0 ? L.T("Every setting was already at its default.")
                : L.N(changed, "{0} setting is back to its default.", "{0} settings are back to their defaults."));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not restore the defaults"), ex.Message);
        }
    }
}

/// <summary>Cards in as many columns as fit (each at least <see cref="MinColumnWidth"/>), each card into the shortest column.</summary>
sealed partial class MasonryPanel : Panel
{
    readonly double _spacing;
    double _minColumnWidth;

    public MasonryPanel(double minColumnWidth, double spacing)
    {
        _minColumnWidth = minColumnWidth;
        _spacing = spacing;
    }

    public bool Stable { get; init; }

    /// <summary>Row by row in reading order; open cards stretch to the tallest card of their row (folded ones keep their height).</summary>
    public bool Rows { get; init; }

    public double MinColumnWidth
    {
        get => _minColumnWidth;
        set { _minColumnWidth = value; InvalidateMeasure(); }
    }

    (int Count, double Width) Columns(double available)
    {
        if (double.IsInfinity(available) || available <= 0) return (1, Math.Min(_minColumnWidth, 600));
        int count = Math.Clamp((int)((available + _spacing) / (_minColumnWidth + _spacing)), 1, 4);
        return (count, (available - _spacing * (count - 1)) / count);
    }

    Windows.Foundation.Rect[] Place(double available, bool measure)
    {
        var (count, width) = Columns(available);
        var heights = new double[count];
        var rects = new Windows.Foundation.Rect[Children.Count];
        if (Rows)
        {
            double y = 0;
            for (int start = 0; start < Children.Count; start += count)
            {
                int end = Math.Min(start + count, Children.Count);
                double row = 0;
                for (int i = start; i < end; i++)
                {
                    if (measure) Children[i].Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
                    row = Math.Max(row, Children[i].DesiredSize.Height);
                }
                for (int i = start; i < end; i++)
                {
                    bool folded = Children[i] is FrameworkElement { Tag: "folded" };
                    rects[i] = new Windows.Foundation.Rect((i - start) * (width + _spacing), y, width, folded ? Children[i].DesiredSize.Height : row);
                }
                y += row + _spacing;
            }
            return rects;
        }
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (measure) child.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            // Stable: each card keeps its column (folded sections must not jump around when one opens).
            int col = Stable ? i % count : Array.IndexOf(heights, heights.Min());
            rects[i] = new Windows.Foundation.Rect(col * (width + _spacing), heights[col], width, child.DesiredSize.Height);
            heights[col] += child.DesiredSize.Height + _spacing;
        }
        return rects;
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        var rects = Place(available.Width, measure: true);
        double height = rects.Length == 0 ? 0 : rects.Max(r => r.Bottom);
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? Columns(available.Width).Width : available.Width, height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        var rects = Place(final.Width, measure: false);
        for (int i = 0; i < Children.Count; i++) Children[i].Arrange(rects[i]);
        return final;
    }
}
