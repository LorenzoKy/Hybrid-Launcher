using HybridLauncher.Core;
using HybridLauncher.Core.Mods;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>What the mod tabs (ReShade, RenoDX, Quick access, Notes) need from the game page.</summary>
public sealed class ModContext
{
    public required Game Game { get; init; }
    public required GameSettings Settings { get; init; }
    public required GameData Data { get; init; }
    /// <summary>The folder mods go in (next to the game exe), or null when no exe is known.</summary>
    public required Func<string?> TargetDir { get; init; }
    public required CancellationToken Token { get; init; }
    /// <summary>OptiScaler.ini or the folder changed: the page redraws its OptiScaler parts.</summary>
    public required Action OptiChanged { get; init; }
    public required Action<string> ShowTab { get; init; }
    public required Action ReinstallOpti { get; init; }

    public XamlRoot? Root { get; set; }

    public async Task<bool> ConfirmAsync(FrameworkElement owner, string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = owner.ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>Small pieces shared by the mod tabs.</summary>
static class ModUi
{
    /// <summary>
    /// Puts an X in the top right corner of a mod card: a click takes the card out of the list (<paramref name="removed"/> says
    /// what happens next: remembered for the game, or just gone).
    /// </summary>
    public static Border WithRemove(Border card, Action removed)
    {
        if (card.Child is not FrameworkElement body) return card;
        card.Child = null;
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(body);
        var x = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
            Padding = new Thickness(7),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -6, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Opacity = 0.6,
        };
        ToolTipService.SetToolTip(x, L.T("Remove from this list"));
        x.Click += (_, _) => removed();
        Grid.SetColumn(x, 1);
        grid.Children.Add(x);
        card.Child = grid;
        return card;
    }

    /// <summary>"Show the N mods you removed": puts every card taken out of this game's list back.</summary>
    public static HyperlinkButton ShowRemoved(int count, Action restore)
    {
        var link = new HyperlinkButton { Content = L.F(count == 1 ? "Show the {0} mod you removed" : "Show the {0} mods you removed", count), Padding = new Thickness(0, 2, 0, 2), FontSize = 13 };
        link.Click += (_, _) => restore();
        return link;
    }

    /// <summary>A part of a card: a small tinted icon and a title, then its body indented under the title.</summary>
    public static StackPanel Part(string glyph, string title, UIElement body, Color? tint = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(Ui.Badge(glyph, tint, 28));
        head.Children.Add(new TextBlock { Text = title, Style = Ui.Style("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(head);
        if (body is FrameworkElement fe) fe.Margin = new Thickness(38, 0, 0, 0);
        panel.Children.Add(body);
        return panel;
    }

    /// <summary>
    /// <see cref="Part"/> that folds: a chevron before the title, a click on the heading folds or opens the body, and the state
    /// is remembered for every game (Settings: CollapsedCards), as the OptiScaler tab's parts are.
    /// </summary>
    public static StackPanel FoldPart(string key, string glyph, string title, UIElement body, Color? tint = null)
    {
        var panel = Part(glyph, title, body, tint);
        var head = (StackPanel)panel.Children[0];
        var content = (FrameworkElement)body;
        bool open = !AppState.Settings.CollapsedCards.Contains(key);
        var chevron = Motion.Chevron(open, 11);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Insert(0, chevron);
        content.Margin = new Thickness(59, 0, 0, 0);
        content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.RemoveAt(0);
        var hit = new Button
        {
            Content = head, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 8, 2), Margin = new Thickness(-4, -2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTipService.SetToolTip(hit, L.T("Click to fold or open"));
        panel.Children.Insert(0, hit);
        hit.Click += (_, _) =>
        {
            open = !open;
            Motion.Turn(chevron, open);
            if (open) Motion.Open(content); else Motion.Close(content);
            if (open) AppState.Settings.CollapsedCards.Remove(key); else AppState.Settings.CollapsedCards.Add(key);
            SettingsStore.Save();
        };
        return panel;
    }

    public static Button Action(string text, string glyph, Action click, bool accent = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = text, FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal });
        var b = new Button { Content = panel, Padding = new Thickness(accent ? 18 : 14, 8, accent ? 18 : 14, 8) };
        if (accent) b.Style = Ui.Style("AccentButtonStyle");
        b.Click += (_, _) => Ui.Safe(click);
        return b;
    }

    /// <summary>A line with a check / cross / dot and a text: the state of one requirement.</summary>
    public static Grid Check(bool? ok, string text, string? detail = null)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var color = ok switch { true => Ui.ToneColor(Ui.Tone.Good), false => Ui.ToneColor(Ui.Tone.Bad), _ => Ui.ToneColor(Ui.Tone.Partial) };
        grid.Children.Add(new FontIcon { Glyph = ok switch { true => "", false => "", _ => "" }, FontSize = 14, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        var text2 = new StackPanel();
        text2.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (detail is not null) text2.Children.Add(Ui.Muted(detail));
        Grid.SetColumn(text2, 1);
        grid.Children.Add(text2);
        return grid;
    }

    /// <summary>Two columns on wide pages (the second about a quarter of the width), stacked when narrow.</summary>
    public static Grid TwoColumns(FrameworkElement main, FrameworkElement side)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.VerticalAlignment = side.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(main);
        grid.Children.Add(side);
        Grid.SetColumn(side, 1);
        grid.SizeChanged += (_, e) =>
        {
            bool wide = e.NewSize.Width >= 1000;
            grid.ColumnDefinitions[1].Width = wide ? new GridLength(Math.Clamp(Math.Round(e.NewSize.Width * 0.27), 340, 560)) : new GridLength(0);
            grid.ColumnSpacing = wide ? 16 : 0;
            Grid.SetColumn(side, wide ? 1 : 0);
            Grid.SetRow(side, wide ? 0 : 1);
        };
        return grid;
    }

    public static void OpenFile(string path, bool pickApp = false) => Ui.Safe(() =>
    {
        if (pickApp) GameLauncher.OpenWith(path);
        else GameLauncher.OpenUri(path);
    });

    /// <summary>ReShade's own logo (from reshade.me), on a soft tile in the colour of the state.</summary>
    public static Border ReShadeLogo(Color tint, double size)
    {
        var image = new Image
        {
            Source = new BitmapImage { UriSource = new Uri("ms-appx:///Assets/Brands/ReShade.png"), DecodePixelWidth = (int)(size * 2) },
            Width = size * 0.78,
            Height = size * 0.78,
            Stretch = Stretch.Uniform,
        };
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 4),
            Background = new SolidColorBrush(Color.FromArgb(0x33, tint.R, tint.G, tint.B)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = image,
        };
    }
}

// ======================================================================================================================

/// <summary>
/// The ReShade tab: pick a version and edition, Hybrid Launcher downloads the official installer from reshade.me (kept as a saved
/// version) and starts it with the game's exe; the installer does the rest. With OptiScaler in the same folder, one button makes
/// OptiScaler load ReShade (ReShade64.dll + LoadReshade = true), and puts it back as it was.
/// </summary>
public sealed partial class ReShadeView : UserControl
{
    readonly ModContext _ctx;
    readonly ComboBox _version = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = L.T("Loading versions…") };
    readonly ToggleButton _standard = new() { Content = L.T("Standard"), Padding = new Thickness(12, 6, 12, 7), MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ToggleButton _addon = new() { Content = L.T("Full add-on support"), Padding = new Thickness(12, 6, 12, 7), MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _editionHint = Ui.Muted("");
    readonly Button _run;
    readonly Button _remove;
    readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed };
    readonly StackPanel _optiBody = new() { Spacing = 10 };
    readonly WrapPanel _tools = new(8, []);
    readonly StackPanel _presets = new() { Spacing = 6 };
    readonly StackPanel _saved = new() { Spacing = 6 };
    readonly Grid _header = new() { Padding = new Thickness(20, 16, 20, 16), ColumnSpacing = 14, CornerRadius = new CornerRadius(8, 8, 0, 0) };
    List<string> _versions = [];
    bool _busy;
    readonly ReShadeSettingsView _settingsView;

    public ReShadeView(ModContext ctx)
    {
        _ctx = ctx;
        _run = ModUi.Action(L.T("Download and run the installer"), "", () => _ = RunAsync(), accent: true);
        _remove = ModUi.Action(L.T("Remove ReShade"), "", () => _ = RemoveAsync());

        var edition = new Grid { ColumnSpacing = 4 };
        edition.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        edition.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        edition.Children.Add(_standard);
        Grid.SetColumn(_addon, 1);
        edition.Children.Add(_addon);
        _standard.Click += (_, _) => SetEdition(false);
        _addon.Click += (_, _) => SetEdition(true);
        SetEdition(ctx.Settings.ReShade.Addon ?? false, save: false);

        var install = new StackPanel { Spacing = 10 };
        install.Children.Add(_version);
        install.Children.Add(edition);
        install.Children.Add(_editionHint);
        var runRow = new WrapPanel(8, []);
        runRow.Children.Add(_run);
        runRow.Children.Add(_remove);
        install.Children.Add(runRow);
        install.Children.Add(Ui.Muted(L.T("Hybrid Launcher downloads the official installer from reshade.me and starts it with this game's exe chosen. Pick the graphics API there and, if you like, some shaders. Run it again to update; Remove ReShade takes it out, ReShade64.dll included.")));
        install.Children.Add(_progress);

        // Not steps to follow in order: independent parts, each with its own icon, each folding (remembered) as in the OptiScaler tab.
        _settingsView = new ReShadeSettingsView(ctx.TargetDir);
        var body = new StackPanel { Padding = new Thickness(20, 16, 20, 20), Spacing = 22 };
        body.Children.Add(ModUi.FoldPart("reshade:install", "", L.T("Install ReShade in the game"), install, Ui.Orange));
        body.Children.Add(ModUi.FoldPart("reshade:settings", "", L.T("ReShade settings"), _settingsView, Ui.Yellow));
        body.Children.Add(ModUi.FoldPart("reshade:opti", "", L.T("Use it together with OptiScaler"), _optiBody, Color.FromArgb(0xFF, 0x9A, 0x7C, 0xFF)));
        body.Children.Add(ModUi.FoldPart("reshade:presets", "", L.T("Presets"), _presets, Ui.Pink));
        body.Children.Add(ModUi.FoldPart("reshade:files", "", L.T("Files"), _tools, Ui.Teal));

        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var main = new StackPanel();
        main.Children.Add(_header);
        main.Children.Add(new Rectangle { Height = 1, Fill = Ui.Brush("CardStrokeColorDefaultBrush") });
        main.Children.Add(body);
        var mainCard = new Border { Style = Ui.Style("CardStyle"), Padding = new Thickness(0), Child = main };

        var savedCard = new StackPanel { Spacing = 10 };
        savedCard.Children.Add(Ui.Heading("", L.T("Saved installers"), subtitle: L.T("Kept on this PC, ready to run again"), tint: Ui.Orange));
        savedCard.Children.Add(_saved);
        var links = new StackPanel { Spacing = 6 };
        links.Children.Add(Ui.Heading("", L.T("ReShade links"), tint: Ui.Blue));
        links.Children.Add(Link(L.T("reshade.me (downloads, forum)"), "https://reshade.me"));
        links.Children.Add(Link(L.T("ReShade on GitHub"), "https://github.com/crosire/reshade"));
        links.Children.Add(Link(L.T("Shader list"), "https://github.com/crosire/reshade-shaders"));
        var side = new StackPanel { Spacing = 12 };
        side.Children.Add(Ui.Card(savedCard));
        side.Children.Add(Ui.Card(links));

        Content = ModUi.TwoColumns(mainCard, side);
        _ = LoadVersionsAsync();
    }

    static HyperlinkButton Link(string text, string url)
    {
        var b = new HyperlinkButton { Content = text, Padding = new Thickness(0, 3, 0, 3) };
        b.Click += (_, _) => Ui.Open(url);
        return b;
    }

    void SetEdition(bool addon, bool save = true)
    {
        _standard.IsChecked = !addon;
        _addon.IsChecked = addon;
        _editionHint.Text = addon
            ? L.T("Needed by RenoDX and other add-ons. Use it only in single-player games: online games with anti-cheat may block or ban add-on builds.")
            : L.T("The usual build, fine for shaders and presets. RenoDX needs the full add-on support edition.");
        if (save && _ctx.Settings.ReShade.Addon != addon)
        {
            _ctx.Settings.ReShade.Addon = addon;
            SettingsStore.Save();
        }
    }

    /// <summary>Opens the tab with an edition already picked (RenoDX asks for the add-on one).</summary>
    public void PreferAddon() => SetEdition(true);

    async Task LoadVersionsAsync()
    {
        try
        {
            _versions = await ReShade.GetVersionsAsync(ct: _ctx.Token);
        }
        catch (Exception ex) { Log.Warn($"ReShade versions: {ex.Message}"); }
        if (_ctx.Token.IsCancellationRequested) return;
        _version.Items.Clear();
        // Offline: the saved installers still work.
        var versions = _versions.Count > 0 ? _versions : ReShade.Saved().Select(s => s.Release.Version).Distinct().ToList();
        if (versions.Count == 0)
        {
            _version.PlaceholderText = L.T("Offline: can't list versions");
            _run.IsEnabled = false;
            return;
        }
        _version.Items.Add(L.F("Latest  ({0})", versions[0]));
        foreach (var v in versions) _version.Items.Add(v);
        _version.SelectedIndex = 0;
        _run.IsEnabled = true;
    }

    string? SelectedVersion
    {
        get
        {
            var versions = _versions.Count > 0 ? _versions : ReShade.Saved().Select(s => s.Release.Version).Distinct().ToList();
            if (versions.Count == 0) return null;
            int i = _version.SelectedIndex;
            return i <= 0 ? versions[0] : versions[Math.Min(i - 1, versions.Count - 1)];
        }
    }

    /// <summary>Redraws the folder state: ReShade found, how it is loaded, the OptiScaler part and the file buttons.</summary>
    public void Refresh()
    {
        var dir = _ctx.TargetDir();
        var found = dir is null ? null : ReShade.Detect(dir);
        bool optiIni = dir is not null && File.Exists(Path.Combine(dir, "OptiScaler.ini"));
        bool? optiLoads = dir is null ? null : ReShade.OptiLoadsReShade(dir);
        var opti = dir is null ? null : OptiDetector.Detect(dir);

        // Header: one look says what is in the folder, under ReShade's own logo.
        _header.Children.Clear();
        // ReShade64.dll does nothing on its own: only OptiScaler with LoadReshade = true loads it.
        bool idle = found is { LoadedByOpti: true } && optiLoads != true;
        _remove.Visibility = found is null ? Visibility.Collapsed : Visibility.Visible;
        Color tint = found is null ? Ui.ToneColor(Ui.Tone.Unknown) : Ui.ToneColor(idle ? Ui.Tone.Partial : Ui.Tone.Good);
        _header.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0x30, tint.R, tint.G, tint.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x00, tint.R, tint.G, tint.B), Offset = 0.7 },
            },
        };
        _header.Children.Add(ModUi.ReShadeLogo(tint, 48));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = found is null ? L.T("ReShade is not installed in this game")
                : idle ? L.F("ReShade {0} is in the folder but nothing loads it", found.Version)
                : L.F("ReShade {0} is installed", found.Version),
            Style = Ui.Style("SectionTitle"),
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(Ui.Muted(dir is null ? L.T("Pick the game exe first (the gear beside Play → Change game exe).")
            : found is null ? L.F("Run the installer below. It goes next to the game exe: {0}", dir)
            : idle ? L.F("{0} is only loaded by OptiScaler with LoadReshade on. Turn it on below, or remove ReShade.", found.FileName)
            : found.LoadedByOpti ? L.F("As {0}, loaded by OptiScaler", found.FileName) + "."
            : L.F("As {0}", found.FileName) + (_ctx.Settings.ReShade.Addon is { } a ? "  ·  " + L.F("last installer: {0}", a ? L.T("full add-on support") : L.T("standard")) : "")));
        Grid.SetColumn(text, 1);
        _header.Children.Add(text);
        // What ReShade takes in the folder (its dll, settings, shaders, presets), as the OptiScaler tab shows its own files.
        if (found is not null && dir is not null)
        {
            // A new line each time: the header is made again, and an element can have one parent only.
            var footprint = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Text = L.T("Measuring ReShade's files…") };
            text.Children.Add(footprint);
            var probe = found.Path;
            _ = Task.Run(() =>
            {
                long size = 0;
                int files = 0;
                foreach (var f in new[] { probe, Path.Combine(dir, "ReShade.ini"), Path.Combine(dir, "ReShade.log"), Path.Combine(dir, "ReShadePreset.ini") })
                    if (File.Exists(f)) { size += new FileInfo(f).Length; files++; }
                foreach (var sub in new[] { "reshade-shaders", Core.Mods.ReShadePresets.Folder })
                {
                    var d = Path.Combine(dir, sub);
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) { size += new FileInfo(f).Length; files++; }
                }
                return (size, files);
            }).ContinueWith(t => DispatcherQueue.TryEnqueue(() =>
            {
                if (t.IsCompletedSuccessfully) footprint.Text = L.F("{0} files, {1} in the game folder (shaders and presets included)", t.Result.files, DiskUsage.Format(t.Result.size));
            }));
        }

        // Together with OptiScaler: only meaningful with OptiScaler in the same folder.
        _optiBody.Children.Clear();
        if (!optiIni)
        {
            _optiBody.Children.Add(Ui.Muted(L.T("OptiScaler is not installed in this game, so ReShade loads on its own: nothing to do here. With OptiScaler, both want to be the game's dxgi.dll; this makes OptiScaler load ReShade instead.")));
        }
        else
        {
            bool through = found is { LoadedByOpti: true } && optiLoads == true;
            _optiBody.Children.Add(ModUi.Check(through, through ? L.T("OptiScaler loads ReShade (ReShade64.dll, LoadReshade = true)") : L.T("OptiScaler does not load ReShade yet"),
                through ? L.T("Both work together. Press Home in game for ReShade, Insert for OptiScaler's menu.") :
                L.T("OptiScaler and ReShade can't both be dxgi.dll. Hybrid Launcher renames ReShade's dll to ReShade64.dll and turns on LoadReshade in OptiScaler.ini (kept after OptiScaler updates).")));
            if (opti is null && found is not null && !found.LoadedByOpti)
                _optiBody.Children.Add(ModUi.Check(false, L.T("ReShade replaced OptiScaler's dll"), L.T("The ReShade installer wrote over OptiScaler's file. Load ReShade through OptiScaler, then reinstall OptiScaler (Hybrid Launcher offers it).")));
            else if (opti is null && found is { LoadedByOpti: true })
                _optiBody.Children.Add(ModUi.Check(false, L.T("OptiScaler's own dll is missing"), L.T("Only OptiScaler.ini is left (ReShade's installer may have written over OptiScaler's dll), so nothing loads ReShade64.dll. Install OptiScaler again from the OptiScaler tab.")));
            var row = new WrapPanel(8, []);
            if (!through) row.Children.Add(ModUi.Action(L.T("Load ReShade through OptiScaler"), "", () => _ = LoadThroughOptiAsync(), accent: true));
            else row.Children.Add(ModUi.Action(L.T("Stop loading it through OptiScaler"), "", () => _ = StopThroughOptiAsync()));
            _optiBody.Children.Add(row);
        }

        FillPresets(dir, found is not null);
        _settingsView.Refresh();
        _tools.Children.Clear();
        if (dir is not null)
        {
            var ini = Path.Combine(dir, "ReShade.ini");
            var shaders = Path.Combine(dir, "reshade-shaders");
            var log = Path.Combine(dir, "ReShade.log");
            if (File.Exists(ini)) _tools.Children.Add(ModUi.Action(L.T("Open ReShade.ini"), "", () => ModUi.OpenFile(ini, pickApp: true)));
            if (Directory.Exists(shaders)) _tools.Children.Add(ModUi.Action(L.T("Shaders folder"), "", () => GameLauncher.OpenFolder(shaders)));
            if (File.Exists(log)) _tools.Children.Add(ModUi.Action("ReShade.log", "", () => ModUi.OpenFile(log, pickApp: true)));
            _tools.Children.Add(ModUi.Action(L.T("Game folder"), "", () => GameLauncher.OpenFolder(dir)));
        }
        FillSaved();
    }

    /// <summary>
    /// The game's ReShade presets: the switch makes one the preset ReShade loads (only one at a time), the bin deletes it.
    /// Presets imported here are copied into the game's reshade-presets folder.
    /// </summary>
    void FillPresets(string? dir, bool installed)
    {
        _presets.Children.Clear();
        if (dir is null)
        {
            _presets.Children.Add(Ui.Muted(L.T("Pick the game exe first (the gear beside Play → Change game exe).")));
            return;
        }
        var actions = new WrapPanel(8, []);
        actions.Children.Add(ModUi.Action(L.T("Import presets…"), "", () => _ = ImportPresetsAsync(dir)));
        var folder = Path.Combine(dir, ReShadePresets.Folder);
        if (Directory.Exists(folder)) actions.Children.Add(ModUi.Action(L.T("Presets folder"), "", () => GameLauncher.OpenFolder(folder)));
        List<ReShadePreset> presets = [];
        try { presets = ReShadePresets.List(dir); }
        catch (Exception ex) { Log.Warn($"ReShade presets: {ex.Message}"); }
        if (presets.Count == 0)
            _presets.Children.Add(Ui.Muted(L.T("No presets in this game yet. Import .ini presets (from Nexus Mods, a friend, another game): they are copied into the game folder, then turn one on.")));
        else if (!installed)
            _presets.Children.Add(Ui.Muted(L.T("ReShade is not installed here: the presets are ready for when it is.")));
        foreach (var p in presets)
        {
            var toggle = new ToggleSwitch { IsOn = p.Active, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(toggle, p.Active ? L.T("ReShade loads this preset. Turn it off to go back to its default preset.") : L.T("Make ReShade load this preset (the one on now goes off)."));
            toggle.Toggled += (_, _) =>
            {
                if (toggle.IsOn == p.Active) return;
                try
                {
                    ReShadePresets.SetActive(dir, toggle.IsOn ? p.Path : null);
                    App.Window?.ShowSuccess(toggle.IsOn ? L.F("ReShade will load \"{0}\". In game it can still be changed from its menu.", p.Name) : L.T("ReShade will load its default preset."));
                }
                catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the preset"), ex.Message); }
                FillPresets(dir, installed);
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (p.Active) title.Children.Add(Ui.Pill(L.T("Active"), Ui.Tone.Good));
            text.Children.Add(title);
            var info = L.N(p.Techniques.Count, "{0} effect", "{0} effects") + $"  ·  {p.Modified:d MMM yyyy}" + (p.Imported ? "  ·  " + L.T("imported") : "");
            var detail = Ui.Muted(info + (p.MissingShaders.Count > 0 ? "  ·  " + L.N(p.MissingShaders.Count, "{0} shader missing", "{0} shaders missing") : ""));
            if (p.MissingShaders.Count > 0) detail.Foreground = new SolidColorBrush(Ui.ToneColor(Ui.Tone.Partial));
            text.Children.Add(detail);
            ToolTipService.SetToolTip(text, new ToolTip
            {
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                    Text = p.Path + "\n\n" + L.T("Effects") + ": " + string.Join(", ", p.Techniques.Take(30)) + (p.Techniques.Count > 30 ? "…" : "")
                        + (p.MissingShaders.Count > 0 ? "\n\n" + L.T("Not in reshade-shaders (install them with ReShade's setup or copy them there):") + " " + string.Join(", ", p.MissingShaders) : ""),
                },
            });
            var open = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = Ui.Style("IconButton") };
            ToolTipService.SetToolTip(open, L.T("Open the preset with…"));
            open.Click += (_, _) => ModUi.OpenFile(p.Path, pickApp: true);
            var bin = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = Ui.Style("IconButton") };
            ToolTipService.SetToolTip(bin, L.T("Delete this preset"));
            bin.Click += async (_, _) =>
            {
                if (!await _ctx.ConfirmAsync(this, L.F("Delete the preset \"{0}\"?", p.Name), L.F("Deletes {0}", p.Path) + (p.Active ? "\n\n" + L.T("It is the active one: ReShade goes back to its default preset.") : ""), L.T("Delete"))) return;
                try { ReShadePresets.Delete(dir, p); }
                catch (Exception ex) { App.Window?.ShowError(L.T("Could not delete the preset"), ex.Message); }
                FillPresets(dir, installed);
            };
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(toggle);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(open, 2);
            grid.Children.Add(open);
            Grid.SetColumn(bin, 3);
            grid.Children.Add(bin);
            _presets.Children.Add(new Border { Style = Ui.Style("RowStyle"), Child = grid });
        }
        _presets.Children.Add(actions);
    }

    async Task ImportPresetsAsync(string dir)
    {
        try
        {
            var files = await Pickers.PickFilesAsync(App.Window!, ".ini", ".txt");
            if (files.Count == 0) return;
            var added = await Task.Run(() => ReShadePresets.Import(dir, files));
            App.Window?.ShowSuccess(L.N(added.Count, "{0} preset imported. Turn it on to use it.", "{0} presets imported. Turn one on to use it."));
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not import the preset"), ex.Message); }
        Refresh();
    }

    void FillSaved()
    {
        _saved.Children.Clear();
        var saved = ReShade.Saved();
        if (saved.Count == 0) _saved.Children.Add(Ui.Muted(L.T("None yet: the first installer you run is kept here.")));
        foreach (var (release, path, size, date) in saved)
        {
            var grid = new Grid { ColumnSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var logo = new Image { Source = new BitmapImage { UriSource = new Uri("ms-appx:///Assets/Brands/ReShade.png"), DecodePixelWidth = 48 }, Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(logo);
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = $"ReShade {release.Version}", FontWeight = FontWeights.SemiBold });
            t.Children.Add(Ui.Muted($"{(release.Addon ? L.T("Full add-on support") : L.T("Standard"))}  ·  {DiskUsage.Format(size)}  ·  {date:d MMM yyyy}"));
            Grid.SetColumn(t, 1);
            grid.Children.Add(t);
            var run = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = Ui.Style("IconButton") };
            ToolTipService.SetToolTip(run, L.T("Run this installer for this game"));
            run.Click += (_, _) => Ui.Safe(() => Start(path, release));
            Grid.SetColumn(run, 2);
            grid.Children.Add(run);
            var bin = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = Ui.Style("IconButton") };
            ToolTipService.SetToolTip(bin, L.T("Delete this installer from this PC"));
            bin.Click += async (_, _) =>
            {
                if (!await _ctx.ConfirmAsync(this, L.F("Delete ReShade {0}?", release.Version), L.F("Removes the saved installer:\n{0}\n\nReShade stays installed in your games.", path), L.T("Delete"))) return;
                Ui.Safe(() => ReShade.Delete(path));
                FillSaved();
            };
            Grid.SetColumn(bin, 3);
            grid.Children.Add(bin);
            _saved.Children.Add(new Border { Style = Ui.Style("RowStyle"), Child = grid });
        }
    }

    void Start(string installer, ReShadeRelease release)
    {
        var exe = Core.Detection.GameScanner.EffectiveExe(_ctx.Game, _ctx.Settings);
        ReShade.RunInstaller(installer, exe);
        _ctx.Settings.ReShade.Addon = release.Addon;
        _ctx.Settings.ReShade.InstallerVersion = release.Version;
        SettingsStore.Save();
        App.Window?.ShowInfo(_ctx.TargetDir() is { } d && File.Exists(Path.Combine(d, "OptiScaler.ini"))
            ? L.T("ReShade's setup is open. When it finishes, come back here and press Load ReShade through OptiScaler.")
            : L.T("ReShade's setup is open. When it finishes, come back here."));
        WatchForInstall();
    }

    /// <summary>Notices when the installer has put ReShade in the folder and redraws, without polling forever.</summary>
    void WatchForInstall()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        int ticks = 0;
        var before = _ctx.TargetDir() is { } d ? ReShade.Detect(d)?.Version : null;
        timer.Tick += (_, _) =>
        {
            if (++ticks > 200 || _ctx.Token.IsCancellationRequested) { timer.Stop(); return; }
            var now = _ctx.TargetDir() is { } dir ? ReShade.Detect(dir)?.Version : null;
            if (now == before) return;
            timer.Stop();
            Refresh();
            _ctx.OptiChanged();
        };
        timer.Start();
    }

    async Task RemoveAsync()
    {
        var dir = _ctx.TargetDir();
        if (dir is null || ReShade.Detect(dir) is not { } found) { Refresh(); return; }
        if (OptiInstaller.RunningGameProcess(dir) is { } running)
        {
            App.Window?.ShowError(L.T("Close the game first"), L.F("{0} is running from this folder.", running));
            return;
        }
        if (!await _ctx.ConfirmAsync(this, L.T("Remove ReShade from this game?"),
                L.F("Deletes {0} from the game folder. ReShade.ini, presets and shaders are kept.", found.FileName), L.T("Remove"))) return;
        try
        {
            var message = await Task.Run(() => ReShade.Uninstall(dir, _ctx.Settings));
            SettingsStore.Save();
            App.Window?.ShowSuccess(message);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not remove ReShade"), ex.Message); }
        Refresh();
        _ctx.OptiChanged();
    }

    async Task RunAsync(bool uninstallHint = false)
    {
        if (_busy) return;
        var version = SelectedVersion;
        if (version is null) return;
        bool addon = _addon.IsChecked == true;
        var release = new ReShadeRelease(version, addon);
        _busy = true;
        _run.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        try
        {
            var path = await ReShade.DownloadAsync(release, new Progress<double>(p => _progress.Value = p), _ctx.Token);
            if (uninstallHint) App.Window?.ShowInfo(L.T("In ReShade's setup, pick this game, then Uninstall."));
            Start(path, release);
            FillSaved();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not get ReShade"), ex.Message);
        }
        finally
        {
            _busy = false;
            _run.IsEnabled = true;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    async Task LoadThroughOptiAsync()
    {
        var dir = _ctx.TargetDir();
        if (dir is null) return;
        if (OptiInstaller.RunningGameProcess(dir) is { } running)
        {
            App.Window?.ShowError(L.T("Close the game first"), L.F("{0} is running from this folder.", running));
            return;
        }
        // OptiScaler installed outside Hybrid Launcher: when its dll is not found, ReShade most likely went in over its dxgi.dll.
        var optiDll = _ctx.Settings.Install?.DllName ?? (OptiDetector.Detect(dir) is null ? "dxgi.dll" : null);
        ReShade.SetupResult result;
        try
        {
            result = await Task.Run(() => ReShade.LoadThroughOpti(dir, _ctx.Settings, optiDll));
            SettingsStore.Save();
            App.Window?.ShowSuccess(result.Message);
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not set it up"), ex.Message);
            return;
        }
        // The redraw is not part of the setup: a failure there must not read as "could not set it up".
        Ui.Safe(Refresh);
        _ctx.OptiChanged();
        if (result.OptiDllReplaced && _ctx.Settings.Install is null)
            App.Window?.ShowInfo(L.T("ReShade had written over OptiScaler's dll: install OptiScaler again from the OptiScaler tab. ReShade stays as ReShade64.dll."));
        else if (result.OptiDllReplaced && _ctx.Settings.Install is not null &&
                await _ctx.ConfirmAsync(this, L.T("Reinstall OptiScaler?"), L.T("ReShade's installer had written over OptiScaler's dll. Reinstall OptiScaler now, with your settings? ReShade stays as ReShade64.dll."), L.T("Reinstall")))
                _ctx.ReinstallOpti();
    }

    async Task StopThroughOptiAsync()
    {
        var dir = _ctx.TargetDir();
        if (dir is null) return;
        try
        {
            var message = await Task.Run(() => ReShade.StopLoadingThroughOpti(dir, _ctx.Settings));
            SettingsStore.Save();
            App.Window?.ShowSuccess(message);
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not undo it"), ex.Message);
        }
        Ui.Safe(Refresh);
        _ctx.OptiChanged();
    }
}

// ======================================================================================================================

/// <summary>
/// The RenoDX tab: the mods that fit this game (its own entry in the RenoDX list, or the generic mod of its engine), with what
/// they need checked off: ReShade with full add-on support, loaded through OptiScaler when OptiScaler is there. Snapshot builds
/// install with one click; mods only on Nexus Mods open there, and the downloaded file is picked up from Downloads.
/// </summary>
public sealed partial class RenoDxView : UserControl
{
    readonly ModContext _ctx;
    readonly StackPanel _checks = new() { Spacing = 8 };
    readonly StackPanel _matches = new() { Spacing = 10 };
    readonly StackPanel _installed = new() { Spacing = 6 };
    readonly AutoSuggestBox _search = new() { PlaceholderText = L.T("Search every RenoDX mod"), QueryIcon = new SymbolIcon(Symbol.Find) };
    readonly InfoBar _found = new() { IsOpen = false, Severity = InfoBarSeverity.Success, IsClosable = true };
    readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed };
    List<RenoDxMod> _mods = [];
    FileSystemWatcher? _watcher;
    bool _busy;

    public RenoDxView(ModContext ctx)
    {
        _ctx = ctx;
        var head = new StackPanel { Spacing = 10 };
        head.Children.Add(Ui.Heading("", L.T("RenoDX: HDR for this game"), Ui.LinkButton(L.T("RenoDX mod list"), "", () => Ui.Open(RenoDx.WikiPage)),
            subtitle: L.T("Rewritten shaders for real HDR, as a ReShade add-on"), tint: Ui.Gold));
        head.Children.Add(_checks);

        var mods = new StackPanel { Spacing = 10 };
        mods.Children.Add(Ui.Heading("", L.T("Mods for this game"), subtitle: L.T("From the RenoDX list, matched by name and engine"), tint: Ui.Pink));
        // The search sits on top: a mod the matching missed is one search away, before the suggestions.
        mods.Children.Add(_search);
        mods.Children.Add(_found);
        mods.Children.Add(_progress);
        _matches.Children.Add(Ui.Loading());
        mods.Children.Add(_matches);
        _search.TextChanged += (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = RenoDx.Key(s.Text);
            s.ItemsSource = q.Length < 2 ? null : _mods.Where(m => RenoDx.Key(m.Name).Contains(q)).Take(12).Select(m => m.Name).ToList();
        };
        _search.SuggestionChosen += (s, e) =>
        {
            if (_mods.FirstOrDefault(m => m.Name == e.SelectedItem as string) is { } mod)
            {
                // A mod picked by hand shows what it does at once, like the best match.
                _firstFeatures = true;
                _ctx.Settings.HiddenModCards.Remove("renodx:" + mod.Name);
                _matches.Children.Insert(0, Removable(ModCard(new RenoDxMatch(mod, L.T("Picked from the list"), 0)), mod.Name));
                s.Text = "";
            }
        };

        var installedCard = new StackPanel { Spacing = 10 };
        installedCard.Children.Add(Ui.Heading("", L.T("In the game folder"), subtitle: L.T(".addon64 / .addon32 files next to the exe"), tint: Ui.Teal));
        installedCard.Children.Add(_installed);
        var fromFile = ModUi.Action(L.T("Install a downloaded file…"), "", () => _ = InstallFromPickerAsync());
        installedCard.Children.Add(fromFile);

        var tips = new StackPanel { Spacing = 6 };
        tips.Children.Add(Ui.Heading("", L.T("Tips"), tint: Ui.Slate));
        tips.Children.Add(Ui.Muted(L.T("• Turn on HDR in Windows. Turn off Windows Auto HDR and RTX HDR for this game, or the picture is tone mapped twice and looks washed out.")));
        tips.Children.Add(Ui.Muted(L.T("• In game, Home opens ReShade: RenoDX's settings are in its Add-ons tab.")));
        tips.Children.Add(Ui.Muted(L.T("• Snapshot builds are newer than the Nexus Mods ones but may be less stable. Some mods need extra steps: read their page.")));

        var main = new StackPanel { Spacing = 12 };
        main.Children.Add(Ui.Card(head));
        main.Children.Add(Ui.Card(mods));
        var side = new StackPanel { Spacing = 12 };
        side.Children.Add(Ui.Card(installedCard));
        side.Children.Add(Ui.Card(tips));
        Content = ModUi.TwoColumns(main, side);
        Unloaded += (_, _) => StopWatching();
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        try { _mods = await RenoDx.GetModsAsync(ct: _ctx.Token); }
        catch (Exception ex) { Log.Warn($"RenoDX list: {ex.Message}"); }
        if (_ctx.Token.IsCancellationRequested) return;
        _matches.Children.Clear();
        if (_mods.Count == 0)
        {
            _matches.Children.Add(Ui.Muted(L.T("Could not load the RenoDX list (offline?). The mod list link above opens it in the browser.")));
            return;
        }
        var exe = Core.Detection.GameScanner.EffectiveExe(_ctx.Game, _ctx.Settings);
        var engine = await Task.Run(() => RenoDx.DetectEngine(_ctx.Game.InstallDir, exe));
        var hits = RenoDx.Match(_mods, Core.Media.GameNames.PlainOrClean(Core.Media.GameNames.LookupName(_ctx.Game)), engine);
        if (hits.Count == 0)
            _matches.Children.Add(Ui.Muted((engine is null ? L.F("No RenoDX mod is listed for {0} yet.", _ctx.Game.Name) : L.F("No RenoDX mod is listed for {0} yet, and no generic one for its engine ({1}).", _ctx.Game.Name, engine))
                + " " + L.T("Search the list above if it goes by another name.")));
        _hits = hits.Take(5).ToList();
        FillMatches();
    }

    List<RenoDxMatch> _hits = [];

    /// <summary>The matched mods, less the ones removed with their X (remembered for this game; a link brings them back).</summary>
    void FillMatches()
    {
        foreach (var old in _matches.Children.Where(c => c is Border or HyperlinkButton).ToList()) _matches.Children.Remove(old);
        int hidden = 0;
        foreach (var h in _hits)
        {
            if (_ctx.Settings.HiddenModCards.Contains("renodx:" + h.Mod.Name)) { hidden++; continue; }
            _matches.Children.Add(Removable(ModCard(h), h.Mod.Name));
        }
        if (hidden > 0)
            _matches.Children.Add(ModUi.ShowRemoved(hidden, () =>
            {
                _ctx.Settings.HiddenModCards.RemoveWhere(k => k.StartsWith("renodx:", StringComparison.OrdinalIgnoreCase));
                SettingsStore.Save();
                FillMatches();
            }));
    }

    Border Removable(Border card, string name) => ModUi.WithRemove(card, () =>
    {
        _ctx.Settings.HiddenModCards.Add("renodx:" + name);
        SettingsStore.Save();
        _matches.Children.Remove(card);
        if (_hits.Any(h => h.Mod.Name == name)) FillMatches();
    });

    Border ModCard(RenoDxMatch hit)
    {
        var mod = hit.Mod;
        var exe = Core.Detection.GameScanner.EffectiveExe(_ctx.Game, _ctx.Settings);
        bool want64 = exe is null || RenoDx.Is64Bit(exe) != false;
        var panel = new StackPanel { Spacing = 6 };
        var title = new WrapPanel(8, []);
        title.Children.Add(new TextBlock { Text = mod.Name, FontWeight = FontWeights.SemiBold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        title.Children.Add(Ui.Pill(mod.Working ? L.T("Working") : L.T("In progress"), mod.Working ? Ui.Tone.Good : Ui.Tone.Partial));
        panel.Children.Add(title);
        panel.Children.Add(Ui.Muted(hit.Reason + (mod.Maintainer.Length > 0 ? "  ·  " + L.F("by {0}", mod.Maintainer) : "")));
        if (mod.Note is { Length: > 0 } note) panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 13 });

        var buttons = new WrapPanel(8, []) { Margin = new Thickness(0, 4, 0, 0) };
        if (mod.Snapshot(want64) is { } snap)
            buttons.Children.Add(ModUi.Action(L.F("Install snapshot ({0}-bit)", snap.Is32Bit ? "32" : "64"), "", () => _ = InstallSnapshotAsync(snap), accent: true));
        if (mod.Nexus is { } nexus)
            buttons.Children.Add(ModUi.Action(L.T("Nexus Mods page"), "", () =>
            {
                Ui.Open(nexus.Url);
                WatchDownloads();
                App.Window?.ShowInfo(L.T("Download it on Nexus Mods (Manual download). Hybrid Launcher watches your Downloads folder and offers to install the file."));
            }));
        if (mod.Discord is { } discord) buttons.Children.Add(ModUi.Action("Discord", "", () => Ui.Open(discord.Url)));
        panel.Children.Add(buttons);
        if (mod.SourceDir is not null) panel.Children.Add(FeaturesExpander(mod));
        return new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(14, 12, 14, 12), Child = panel };
    }

    /// <summary>
    /// "What this mod lets you change": the settings the mod offers in ReShade's add-on page, by section, read from its source
    /// (the version the snapshot build is made from). Loaded when first opened; the best match opens by itself.
    /// </summary>
    FrameworkElement FeaturesExpander(RenoDxMod mod)
    {
        var body = new StackPanel { Spacing = 10 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Ui.Gold) });
        header.Children.Add(new TextBlock { Text = L.T("What this mod lets you change"), FontWeight = FontWeights.SemiBold });
        var expander = new Expander { Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
        bool loaded = false;
        async void Load()
        {
            if (loaded) return;
            loaded = true;
            body.Children.Add(Ui.Loading());
            RenoDxFeatures? f = null;
            try { f = await RenoDx.GetFeaturesAsync(mod, _ctx.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn($"RenoDX features of {mod.Name}: {ex.Message}"); }
            body.Children.Clear();
            if (f is null || f.Count == 0)
            {
                body.Children.Add(Ui.Muted(L.T("Its settings could not be read now. The mod's page lists them.")));
                return;
            }
            body.Children.Add(Ui.Muted(L.N(f.Count, "{0} setting in ReShade's Add-ons tab (Home in game)", "{0} settings in ReShade's Add-ons tab (Home in game)")
                + (f.Updated is { } u ? "  ·  " + L.F("source updated {0:d MMM yyyy}", u) : "") + "  ·  " + L.T("snapshot version")));
            foreach (var (section, settings) in f.Sections)
            {
                body.Children.Add(new TextBlock { Text = L.T(section).ToUpperInvariant(), Style = Ui.Style("GroupHeader") });
                var chips = new List<FrameworkElement>();
                foreach (var s in settings)
                {
                    var chip = Ui.Chip(s.Choices.Count > 1 ? $"{s.Label}  ·  {s.Choices.Count}" : s.Label);
                    var tip = (s.Tooltip ?? "") + (s.Choices.Count > 0 ? (s.Tooltip is null ? "" : "\n") + L.T("Choices") + ": " + string.Join(", ", s.Choices) : "");
                    if (tip.Length > 0) ToolTipService.SetToolTip(chip, new ToolTip { Content = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 } });
                    chips.Add(chip);
                }
                body.Children.Add(Ui.Wrap(chips));
            }
            body.Children.Add(Ui.LinkButton(L.T("Source on GitHub"), "", () => Ui.Open(f.SourceUrl)));
        }
        expander.Expanding += (_, _) => Load();
        if (_firstFeatures)
        {
            _firstFeatures = false;
            expander.IsExpanded = true;
            Load();
        }
        return expander;
    }

    bool _firstFeatures = true;

    /// <summary>Redraws the requirements and the installed files.</summary>
    public void Refresh()
    {
        var dir = _ctx.TargetDir();
        _checks.Children.Clear();
        _installed.Children.Clear();
        if (dir is null)
        {
            _checks.Children.Add(Ui.Muted(_ctx.Game.NotInstalled
                ? L.F("{0} is not installed: the mods below can go in once it is.", _ctx.Game.Name)
                : L.T("Pick the game exe first (the gear beside Play → Change game exe).")));
            return;
        }
        var reshade = ReShade.Detect(dir);
        bool optiIni = File.Exists(Path.Combine(dir, "OptiScaler.ini"));
        bool? addon = _ctx.Settings.ReShade.Addon;

        var check1 = ModUi.Check(reshade is null ? false : addon == false ? false : addon == true ? true : null,
            reshade is null ? L.T("ReShade with full add-on support is not installed")
                : L.F("ReShade {0} is installed", reshade.Version) + (addon == true ? " " + L.T("(full add-on support)") : addon == false ? " " + L.T("(standard: RenoDX needs full add-on support)") : ""),
            reshade is null || addon != true ? L.T("Get it in the ReShade tab: pick \"Full add-on support\" and run the installer.") : null);
        _checks.Children.Add(check1);
        if (reshade is null || addon != true)
            _checks.Children.Add(new Border { Margin = new Thickness(24, 0, 0, 0), Child = ModUi.Action(L.T("Open the ReShade section"), "", () => _ctx.ShowTab("reshade")) });
        if (optiIni)
        {
            bool through = reshade is { LoadedByOpti: true } && ReShade.OptiLoadsReShade(dir) == true;
            _checks.Children.Add(ModUi.Check(through, through ? L.T("OptiScaler loads ReShade") : L.T("OptiScaler is installed: ReShade must load through it"),
                through ? null : L.T("One click in the ReShade tab: ReShade becomes ReShade64.dll and OptiScaler loads it. RenoDX then works with OptiScaler's upscaling and frame generation.")));
        }
        else _checks.Children.Add(ModUi.Check(true, L.T("No OptiScaler here: ReShade loads on its own")));

        var files = RenoDx.Installed(dir);
        _checks.Children.Add(ModUi.Check(files.Count > 0, files.Count > 0 ? L.F("RenoDX installed: {0}", string.Join(", ", files.Select(Path.GetFileName))) : L.T("No RenoDX mod in the game folder yet")));

        if (files.Count == 0) _installed.Children.Add(Ui.Muted(L.T("Nothing yet.")));
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var grid = new Grid { ColumnSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            long size = 0;
            try { size = new FileInfo(f).Length; } catch { }
            t.Children.Add(Ui.Muted($"{DiskUsage.Format(size)}" + (_ctx.Settings.RenoDxFiles.TryGetValue(name, out var from) ? $"  ·  {from}" : "  ·  " + L.T("not added by Hybrid Launcher"))));
            grid.Children.Add(t);
            var bin = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = Ui.Style("IconButton") };
            ToolTipService.SetToolTip(bin, L.T("Remove this mod from the game folder"));
            bin.Click += async (_, _) =>
            {
                if (!await _ctx.ConfirmAsync(this, L.F("Remove {0}?", name), L.F("Deletes it from\n{0}", dir), L.T("Remove"))) return;
                Ui.Safe(() =>
                {
                    File.Delete(f);
                    _ctx.Settings.RenoDxFiles.Remove(name);
                    SettingsStore.Save();
                });
                Refresh();
            };
            Grid.SetColumn(bin, 1);
            grid.Children.Add(bin);
            _installed.Children.Add(new Border { Style = Ui.Style("RowStyle"), Child = grid });
        }
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

    async Task InstallSnapshotAsync(RenoDxLink link)
    {
        if (_busy || !CanInstall(out var dir)) return;
        _busy = true;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        try
        {
            var path = await RenoDx.InstallSnapshotAsync(link, dir, new Progress<double>(p => _progress.Value = p), _ctx.Token);
            _ctx.Settings.RenoDxFiles[Path.GetFileName(path)] = "snapshot " + DateTime.Now.ToString("d MMM yyyy");
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("{0} installed. Start the game and press Home for its settings.", Path.GetFileName(path)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not install the mod"), ex.Message); }
        finally
        {
            _busy = false;
            _progress.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    async Task InstallFromPickerAsync()
    {
        var file = await Pickers.PickFileAsync(App.Window!, ".addon64", ".addon32", ".zip", ".7z", ".rar");
        if (file is not null) await InstallFileAsync(file);
    }

    async Task InstallFileAsync(string file)
    {
        if (_busy || !CanInstall(out var dir)) return;
        _busy = true;
        try
        {
            var added = await RenoDx.InstallFromFileAsync(file, dir, _ctx.Token);
            foreach (var a in added) _ctx.Settings.RenoDxFiles[a] = Path.GetFileName(file);
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("Installed {0}.", string.Join(", ", added)));
            _found.IsOpen = false;
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not install the file"), ex.Message); }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    /// <summary>After opening Nexus Mods: a new .addon or archive in Downloads is offered for install (for 15 minutes).</summary>
    void WatchDownloads()
    {
        StopWatching();
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return;
        try
        {
            var started = DateTime.Now;
            _watcher = new FileSystemWatcher(downloads) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            void OnFile(string path)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".addon64" or ".addon32" or ".zip" or ".7z" or ".rar")) return;
                if (DateTime.Now - started > TimeSpan.FromMinutes(15)) { DispatcherQueue.TryEnqueue(StopWatching); return; }
                DispatcherQueue.TryEnqueue(() => Offer(path));
            }
            _watcher.Created += (_, e) => OnFile(e.FullPath);
            _watcher.Renamed += (_, e) => OnFile(e.FullPath);
        }
        catch (Exception ex) { Log.Warn($"Watching Downloads: {ex.Message}"); }
    }

    void Offer(string path)
    {
        if (_ctx.Token.IsCancellationRequested) return;
        _found.Title = L.T("Downloaded");
        _found.Message = L.F("{0} is in your Downloads folder.", Path.GetFileName(path));
        var install = new Button { Content = L.T("Install it in this game") };
        install.Click += async (_, _) =>
        {
            // Browsers write .part / .crdownload first: wait for the file to be complete.
            for (int i = 0; i < 20 && !CanOpen(path); i++) await Task.Delay(500);
            await InstallFileAsync(path);
        };
        _found.ActionButton = install;
        _found.IsOpen = true;
    }

    static bool CanOpen(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch { return false; }
    }

    void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}

// ======================================================================================================================

/// <summary>
/// The Quick access tab: the folders, programs and files the user links for this game (mods folder, a trainer, a config file,
/// a notes file...), as tiles that open with one click. Suggestions from what Hybrid Launcher knows (saves, configuration, ini files)
/// are one click away.
/// </summary>
public sealed partial class QuickAccessView : UserControl
{
    readonly ModContext _ctx;
    readonly WrapPanel _tiles = new(10, []);
    readonly WrapPanel _suggested = new(8, []);
    readonly StackPanel _suggestedPanel = new() { Spacing = 8 };

    public QuickAccessView(ModContext ctx)
    {
        _ctx = ctx;
        var add = new DropDownButton
        {
            Content = Label("", L.T("Add")),
            Style = Ui.Style("AccentButtonStyle"),
            Padding = new Thickness(14, 7, 14, 7),
        };
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(Item(L.T("A folder…"), "", async () => Add("folder", await Pickers.PickFolderAsync(App.Window!))));
        menu.Items.Add(Item(L.T("A program (.exe)…"), "", async () => Add("exe", await Pickers.PickExeAsync(App.Window!))));
        menu.Items.Add(Item(L.T("A file (notes, .ini, anything)…"), "", async () => Add("file", await Pickers.PickFileAsync(App.Window!, "*"))));
        add.Flyout = menu;

        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Ui.Heading("", L.T("Quick access"), add, subtitle: L.F("Your shortcuts for {0}: right-click a tile to rename, move or remove it", ctx.Game.Name), tint: Color.FromArgb(0xFF, 0x00, 0xC2, 0xD8)));
        panel.Children.Add(_tiles);
        _suggestedPanel.Children.Add(new TextBlock { Text = L.T("SUGGESTED"), Style = Ui.Style("GroupHeader") });
        _suggestedPanel.Children.Add(_suggested);
        panel.Children.Add(_suggestedPanel);
        Content = Ui.Card(panel, 20);
        Fill();
        _ = SuggestAsync();
    }

    static StackPanel Label(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        p.Children.Add(new TextBlock { Text = text });
        return p;
    }

    static MenuFlyoutItem Item(string text, string glyph, Func<Task> click)
    {
        var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        mi.Click += async (_, _) =>
        {
            try { await click(); }
            catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message); }
        };
        return mi;
    }

    void Add(string kind, string? path)
    {
        if (path is null) return;
        if (_ctx.Settings.QuickLinks.Any(l => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            App.Window?.ShowInfo(L.T("That one is already in Quick access."));
            return;
        }
        _ctx.Settings.QuickLinks.Add(new QuickLink { Kind = kind, Path = path });
        SettingsStore.Save();
        Fill();
        _ = SuggestAsync();
    }

    static string LinkName(QuickLink l) => l.Label is { Length: > 0 } label ? L.T(label) : Path.GetFileName(Path.TrimEndingDirectorySeparator(l.Path)) is { Length: > 0 } n ? n : l.Path;

    static (string Glyph, Color Tint) Look(string kind, string path) => kind switch
    {
        "folder" => ("", Color.FromArgb(0xFF, 0xFF, 0xC8, 0x57)),
        "exe" => ("", Color.FromArgb(0xFF, 0x9A, 0x7C, 0xFF)),
        _ => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ini" or ".cfg" or ".toml" or ".json" or ".xml" or ".yaml" => ("", Color.FromArgb(0xFF, 0x5F, 0xA8, 0xFF)),
            ".txt" or ".md" or ".log" => ("", Color.FromArgb(0xFF, 0xB9, 0xA3, 0xFF)),
            _ => ("", Color.FromArgb(0xFF, 0x8B, 0x94, 0xA8)),
        },
    };

    void Fill()
    {
        _tiles.Children.Clear();
        var links = _ctx.Settings.QuickLinks;
        if (links.Count == 0)
            _tiles.Children.Add(Ui.Muted(L.T("Nothing linked yet. Add a folder, a program or a file: it opens from here with one click.")));
        for (int i = 0; i < links.Count; i++) _tiles.Children.Add(Tile(links[i], i));
    }

    FrameworkElement Tile(QuickLink link, int index)
    {
        bool exists = link.Kind == "folder" ? Directory.Exists(link.Path) : File.Exists(link.Path);
        var (glyph, tint) = Look(link.Kind, link.Path);
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x33, tint.R, tint.G, tint.B)),
            Child = new FontIcon { Glyph = glyph, FontSize = 18, Foreground = new SolidColorBrush(tint) },
        };
        if (link.Kind == "exe") _ = SetExeIconAsync(icon, link.Path);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = LinkName(link), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 });
        text.Children.Add(new TextBlock
        {
            Text = exists ? (link.Kind switch { "folder" => L.T("Folder"), "exe" => L.T("Program"), _ => L.F("{0} file", Path.GetExtension(link.Path).TrimStart('.').ToUpperInvariant()) }) : L.T("Missing"),
            FontSize = 12,
            Foreground = exists ? Ui.Brush("TextFillColorSecondaryBrush") : new SolidColorBrush(Ui.ToneColor(Ui.Tone.Bad)),
        });
        var grid = new Grid { ColumnSpacing = 12, Width = 260 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button
        {
            Content = grid,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Opacity = exists ? 1 : 0.6,
        };
        ToolTipService.SetToolTip(button, link.Path);
        button.Click += (_, _) => Ui.Safe(() => Open(link));

        var menu = new MenuFlyout();
        menu.Items.Add(Item(L.T("Open"), "", () => { Open(link); return Task.CompletedTask; }));
        if (link.Kind != "folder") menu.Items.Add(Item(L.T("Open with…"), "", () => { GameLauncher.OpenWith(link.Path); return Task.CompletedTask; }));
        menu.Items.Add(Item(L.T("Show in its folder"), "", () =>
        {
            var dir = link.Kind == "folder" ? link.Path : Path.GetDirectoryName(link.Path)!;
            GameLauncher.OpenFolder(dir);
            return Task.CompletedTask;
        }));
        menu.Items.Add(Item(L.T("Rename…"), "", () => RenameAsync(link)));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (index > 0) menu.Items.Add(Item(L.T("Move left"), "", () => { Move(index, -1); return Task.CompletedTask; }));
        if (index < _ctx.Settings.QuickLinks.Count - 1) menu.Items.Add(Item(L.T("Move right"), "", () => { Move(index, 1); return Task.CompletedTask; }));
        menu.Items.Add(Item(L.T("Remove from Quick access"), "", () =>
        {
            _ctx.Settings.QuickLinks.Remove(link);
            SettingsStore.Save();
            Fill();
            _ = SuggestAsync();
            return Task.CompletedTask;
        }));
        button.ContextFlyout = menu;
        return button;
    }

    static async Task SetExeIconAsync(Border target, string exe)
    {
        if (await IconCache.GetAsync(exe) is { } img) target.Child = new Image { Source = img, Width = 26, Height = 26 };
    }

    void Open(QuickLink link)
    {
        switch (link.Kind)
        {
            case "folder": GameLauncher.OpenFolder(link.Path); break;
            case "exe":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(link.Path)! })?.Dispose();
                break;
            default:
                if (!File.Exists(link.Path)) throw new FileNotFoundException(L.T("File not found."), link.Path);
                GameLauncher.OpenUri(link.Path);
                break;
        }
    }

    void Move(int index, int by)
    {
        var list = _ctx.Settings.QuickLinks;
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(Math.Clamp(index + by, 0, list.Count), item);
        SettingsStore.Save();
        Fill();
    }

    async Task RenameAsync(QuickLink link)
    {
        var box = new TextBox { Text = LinkName(link), MaxLength = 60 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.T("Rename"),
            Content = box,
            PrimaryButtonText = L.T("Save"),
            SecondaryButtonText = L.T("Use the file name"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        link.Label = result == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
        SettingsStore.Save();
        Fill();
    }

    /// <summary>Folders and files Hybrid Launcher already knows for this game, not linked yet: one click adds them.</summary>
    async Task SuggestAsync()
    {
        var dir = _ctx.TargetDir();
        // Labels are saved in English (the key of the translation), so a link keeps its name if the language changes.
        var list = new List<(string Kind, string Path, string Label)>();
        if (_ctx.Game.InstallDir.Length > 0) list.Add(("folder", _ctx.Game.InstallDir, "Install folder"));
        if (dir is not null)
        {
            foreach (var (file, label) in new[] { ("OptiScaler.ini", "OptiScaler.ini"), ("ReShade.ini", "ReShade.ini"), ("OptiScaler.log", "OptiScaler log") })
            {
                var p = Path.Combine(dir, file);
                if (File.Exists(p)) list.Add(("file", p, label));
            }
            var shaders = Path.Combine(dir, "reshade-shaders");
            if (Directory.Exists(shaders)) list.Add(("folder", shaders, "ReShade shaders"));
        }
        try
        {
            if (await _ctx.Data.Pcgw is { } wiki)
            {
                var store = _ctx.Game.Store;
                var (config, saves) = await Task.Run(() => (Core.Media.Pcgw.ExistingFolder(wiki.ConfigPaths, store), Core.Media.Pcgw.ExistingFolder(wiki.SavePaths, store)));
                if (saves is not null) list.Add(("folder", saves, "Saves"));
                if (config is not null) list.Add(("folder", config, "Configuration"));
            }
        }
        catch (Exception ex) { Log.Warn($"Quick access suggestions: {ex.Message}"); }
        if (_ctx.Token.IsCancellationRequested) return;

        _suggested.Children.Clear();
        foreach (var (kind, path, label) in list.DistinctBy(l => l.Path.ToLowerInvariant()))
        {
            if (_ctx.Settings.QuickLinks.Any(l => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            var (glyph, tint) = Look(kind, path);
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            p.Children.Add(new FontIcon { Glyph = "", FontSize = 11 });
            p.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = new SolidColorBrush(tint) });
            p.Children.Add(new TextBlock { Text = L.T(label) });
            var b = new Button { Content = p, Padding = new Thickness(10, 5, 12, 6), CornerRadius = new CornerRadius(14) };
            ToolTipService.SetToolTip(b, L.T("Add to Quick access") + "\n" + path);
            b.Click += (_, _) =>
            {
                _ctx.Settings.QuickLinks.Add(new QuickLink { Kind = kind, Path = path, Label = label });
                SettingsStore.Save();
                Fill();
                _ = SuggestAsync();
            };
            _suggested.Children.Add(b);
        }
        _suggestedPanel.Visibility = _suggested.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

// ======================================================================================================================

/// <summary>
/// The Notes tab, like a small notes app: a list of named notes on the left (search, new, rename, reorder, delete) and the
/// open note on the right, saved as you type. Older single-text notes become the first note.
/// </summary>
public sealed partial class NotesView : UserControl
{
    readonly ModContext _ctx;
    readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    readonly TextBox _title = new() { PlaceholderText = L.T("Title"), FontSize = 20, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(0, 4, 0, 4) };
    readonly TextBox _body = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 380,
        FontSize = 15,
        PlaceholderText = L.T("Settings that worked, mods to try, where you left off…"),
    };
    readonly TextBlock _saved = Ui.Muted("");
    readonly AutoSuggestBox _search = new() { PlaceholderText = L.T("Search notes"), QueryIcon = new SymbolIcon(Symbol.Find) };
    readonly StackPanel _editor = new() { Spacing = 8 };
    readonly TextBlock _empty = new() { Text = L.T("No notes yet. Make the first one with New note."), Style = Ui.Style("RowDescription"), Margin = new Thickness(4, 8, 4, 8) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    NoteSection? _open;
    bool _loading;

    List<NoteSection> Notes => _ctx.Settings.NoteSections;

    public NotesView(ModContext ctx)
    {
        _ctx = ctx;
        // Before notes had sections: the old text becomes the first note.
        if (Notes.Count == 0 && ctx.Settings.Notes.Trim().Length > 0)
        {
            Notes.Add(new NoteSection { Title = L.T("Notes"), Text = ctx.Settings.Notes });
            ctx.Settings.Notes = "";
            SettingsStore.Save();
        }
        ScrollViewer.SetVerticalScrollBarVisibility(_body, ScrollBarVisibility.Auto);

        var add = ModUi.Action(L.T("New note"), "", () => NewNote(), accent: true);
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        var left = new StackPanel { Spacing = 10 };
        left.Children.Add(add);
        left.Children.Add(_search);
        left.Children.Add(_empty);
        left.Children.Add(_list);
        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (_list.SelectedItem is ListViewItem { Tag: NoteSection n }) Open(n);
        };
        _search.TextChanged += (_, _) => FillList();

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_title);
        _saved.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_saved, 1);
        head.Children.Add(_saved);
        var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = Ui.Style("IconButton") };
        ToolTipService.SetToolTip(delete, L.T("Delete this note"));
        delete.Click += async (_, _) => await DeleteAsync();
        Grid.SetColumn(delete, 2);
        head.Children.Add(delete);
        _editor.Children.Add(head);
        _editor.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerStrokeColorDefaultBrush") });
        _editor.Children.Add(_body);

        _title.TextChanged += (_, _) => Edited();
        _body.TextChanged += (_, _) => Edited();
        _title.LostFocus += (_, _) => Save();
        _body.LostFocus += (_, _) => Save();
        _timer.Tick += (_, _) => Save();
        Unloaded += (_, _) => Save();

        var grid = new Grid { ColumnSpacing = 28 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(left);
        Grid.SetColumn(_editor, 1);
        grid.Children.Add(_editor);
        grid.SizeChanged += (_, e) =>
        {
            // Narrow pages: the list over the editor.
            bool narrow = e.NewSize.Width < 760;
            grid.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(300);
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            if (grid.RowDefinitions.Count == 0) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            Grid.SetColumn(_editor, narrow ? 0 : 1);
            Grid.SetRow(_editor, narrow ? 1 : 0);
            grid.RowSpacing = narrow ? 18 : 0;
        };

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Ui.Heading("", L.T("Notes"), subtitle: L.F("Only for {0}, kept on this PC", ctx.Game.Name), tint: Color.FromArgb(0xFF, 0xB9, 0xA3, 0xFF)));
        panel.Children.Add(grid);
        // A card like the other tabs' (Quick access, RenoDX, ReShade).
        Content = Ui.Card(panel, 20);
        FillList();
        if (Notes.Count > 0) Open(Notes[0]);
        else _editor.Visibility = Visibility.Collapsed;
    }

    void FillList()
    {
        _loading = true;
        try
        {
            _list.Items.Clear();
            var q = _search.Text.Trim();
            var shown = Notes.Where(n => q.Length == 0 || n.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || n.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
            foreach (var n in shown)
            {
                var text = new StackPanel { Spacing = 1, Padding = new Thickness(2, 6, 2, 6) };
                text.Children.Add(new TextBlock { Text = n.Title.Length > 0 ? n.Title : L.T("Untitled"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var first = n.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? L.T("Empty");
                text.Children.Add(new TextBlock { Text = first, Style = Ui.Style("RowDescription"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                text.Children.Add(new TextBlock { Text = Playtime.FormatLast(n.Updated) + n.Updated.ToString(" · HH:mm"), FontSize = 11, Opacity = 0.55 });
                var item = new ListViewItem { Content = text, Tag = n, ContextFlyout = Menu(n) };
                _list.Items.Add(item);
                if (n == _open) _list.SelectedItem = item;
            }
            _empty.Visibility = Notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (Notes.Count > 0 && shown.Count == 0) { _empty.Text = L.T("No note matches the search."); _empty.Visibility = Visibility.Visible; }
            else _empty.Text = L.T("No notes yet. Make the first one with New note.");
        }
        finally { _loading = false; }
    }

    MenuFlyout Menu(NoteSection n)
    {
        var menu = new MenuFlyout();
        void Item(string text, string glyph, Action a) { var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } }; i.Click += (_, _) => a(); menu.Items.Add(i); }
        int index = Notes.IndexOf(n);
        if (index > 0) Item(L.T("Move up"), "", () => Move(n, -1));
        if (index < Notes.Count - 1) Item(L.T("Move down"), "", () => Move(n, 1));
        Item(L.T("Duplicate"), "", () => NewNote(n.Title + " " + L.T("(copy)"), n.Text));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(L.T("Delete"), "", () => { Open(n); _ = DeleteAsync(); });
        return menu;
    }

    void Move(NoteSection n, int by)
    {
        int i = Notes.IndexOf(n);
        Notes.RemoveAt(i);
        Notes.Insert(Math.Clamp(i + by, 0, Notes.Count), n);
        SettingsStore.Save();
        FillList();
    }

    void NewNote(string? title = null, string text = "")
    {
        Save();
        var n = new NoteSection { Title = title ?? L.F("Note {0}", Notes.Count + 1), Text = text };
        Notes.Insert(0, n);
        SettingsStore.Save();
        _search.Text = "";
        Open(n);
        FillList();
        _title.Focus(FocusState.Programmatic);
        _title.SelectAll();
    }

    void Open(NoteSection n)
    {
        if (n == _open) return;
        Save();
        _open = n;
        _loading = true;
        _title.Text = n.Title;
        _body.Text = n.Text;
        _loading = false;
        _saved.Text = L.T("Saved automatically");
        _editor.Visibility = Visibility.Visible;
        foreach (var item in _list.Items.OfType<ListViewItem>()) if (item.Tag == n) { _loading = true; _list.SelectedItem = item; _loading = false; }
    }

    void Edited()
    {
        if (_loading || _open is null) return;
        _timer.Stop();
        _timer.Start();
        _saved.Text = L.T("Editing…");
    }

    void Save()
    {
        _timer.Stop();
        if (_open is null || (_open.Title == _title.Text && _open.Text == _body.Text)) return;
        _open.Title = _title.Text.Trim();
        _open.Text = _body.Text;
        _open.Updated = DateTime.Now;
        SettingsStore.Save();
        _saved.Text = L.F("Saved {0:HH:mm}", DateTime.Now);
        FillList();
    }

    async Task DeleteAsync()
    {
        if (_open is null) return;
        var n = _open;
        if ((n.Text.Trim().Length > 0) && !await _ctx.ConfirmAsync(this, L.F("Delete \"{0}\"?", n.Title.Length > 0 ? n.Title : L.T("Untitled")), L.T("The note is deleted from this PC."), L.T("Delete")))
            return;
        _timer.Stop();
        Notes.Remove(n);
        SettingsStore.Save();
        _open = null;
        FillList();
        if (Notes.Count > 0) Open(Notes[0]);
        else _editor.Visibility = Visibility.Collapsed;
    }
}

// ======================================================================================================================

/// <summary>
/// The Luma tab: the Luma Framework mods that fit this game (its own mod, or the generic Unreal one when the game was tested
/// with it), what each one adds, and the version to install, picked for the game (newest stable build, the right 32 / 64-bit
/// file; the test build and older builds one click away). Install copies the package next to the exe with backups; with
/// OptiScaler there, ReShade goes in as ReShade64.dll and OptiScaler loads it. Uninstall puts the folder back.
/// </summary>
public sealed partial class LumaView : UserControl
{
    readonly ModContext _ctx;
    readonly StackPanel _state = new() { Spacing = 8 };
    readonly StackPanel _matches = new() { Spacing = 12 };
    readonly AutoSuggestBox _search = new() { PlaceholderText = L.T("Search every Luma mod"), QueryIcon = new SymbolIcon(Symbol.Find) };
    readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed };
    List<LumaMod> _mods = [];
    List<LumaBuild> _builds = [];
    bool _busy;

    public LumaView(ModContext ctx)
    {
        _ctx = ctx;
        var head = new StackPanel { Spacing = 10 };
        head.Children.Add(Ui.Heading("", L.T("Luma: HDR and DLSS for this game"), Ui.LinkButton(L.T("Luma mod list"), "", () => Ui.Open(Luma.WikiPage)),
            subtitle: L.T("Per-game ReShade add-on mods for DirectX 11 games, by Pumbo and friends"), tint: Color.FromArgb(0xFF, 0xFF, 0x8A, 0x3D)));
        head.Children.Add(_state);

        var mods = new StackPanel { Spacing = 10 };
        mods.Children.Add(Ui.Heading("", L.T("Mods for this game"), subtitle: L.T("From the Luma list, matched by name and engine"), tint: Ui.Pink));
        // The search sits on top: a mod the matching missed is one search away, before the suggestions.
        mods.Children.Add(_search);
        mods.Children.Add(_progress);
        _matches.Children.Add(Ui.Loading());
        mods.Children.Add(_matches);
        _search.TextChanged += (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = RenoDx.Key(s.Text);
            s.ItemsSource = q.Length < 2 ? null : _mods.Where(m => RenoDx.Key(m.Name).Contains(q)).Take(12).Select(m => m.Name).ToList();
        };
        _search.SuggestionChosen += (s, e) =>
        {
            if (_mods.FirstOrDefault(m => m.Name == e.SelectedItem as string) is { } mod)
            {
                _ctx.Settings.HiddenModCards.Remove("luma:" + mod.Name);
                _matches.Children.Insert(0, Removable(ModCard(new LumaMatch(mod, L.T("Picked from the list"), 0)), mod.Name));
                s.Text = "";
            }
        };

        var tips = new StackPanel { Spacing = 6 };
        tips.Children.Add(Ui.Heading("", L.T("Good to know"), tint: Ui.Slate));
        foreach (var t in new[]
        {
            "• Each Luma mod is made for one game (or one engine): it rewrites the game's post-processing for real HDR, and many add DLSS / FSR.",
            "• The package brings its own ReShade with add-on support: no separate ReShade install is needed.",
            "• With OptiScaler in the folder, Hybrid Launcher installs Luma's ReShade as ReShade64.dll and OptiScaler loads it (LoadReshade = true): both work together. Use one upscaler at a time: Luma's DLSS or OptiScaler's.",
            "• Turn on HDR in Windows (or Smart HDR on the launch bar). Turn off Auto HDR / RTX HDR for the game: they would tone map twice.",
            "• In game, Home opens ReShade: Luma's settings are in its Add-ons tab.",
        })
            tips.Children.Add(Ui.Muted(L.T(t)));
        var links = new WrapPanel(8, []);
        links.Children.Add(Ui.LinkButton(L.T("Releases"), "", () => Ui.Open(Luma.ReleasesPage)));
        links.Children.Add(Ui.LinkButton("GitHub", "", () => Ui.Open("https://github.com/Filoppi/Luma-Framework")));
        tips.Children.Add(links);

        // The same layout as the RenoDX and ReShade tabs: cards in the main column, the notes on the side.
        var main = new StackPanel { Spacing = 12 };
        main.Children.Add(Ui.Card(head));
        main.Children.Add(Ui.Card(mods));
        var side = new StackPanel { Spacing = 12 };
        side.Children.Add(Ui.Card(tips));
        Content = ModUi.TwoColumns(main, side);
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        try
        {
            var mods = Luma.GetModsAsync(ct: _ctx.Token);
            var builds = Luma.GetBuildsAsync(ct: _ctx.Token);
            _mods = await mods;
            _builds = await builds;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"Luma list: {ex.Message}"); }
        if (_ctx.Token.IsCancellationRequested) return;
        _matches.Children.Clear();
        if (_mods.Count == 0)
        {
            _matches.Children.Add(Ui.Muted(L.T("Could not load the Luma list (offline?). The mod list link above opens it in the browser.")));
            return;
        }
        var exe = Core.Detection.GameScanner.EffectiveExe(_ctx.Game, _ctx.Settings);
        var engine = await Task.Run(() => RenoDx.DetectEngine(_ctx.Game.InstallDir, exe));
        var hits = Luma.Match(_mods, Core.Media.GameNames.PlainOrClean(Core.Media.GameNames.LookupName(_ctx.Game)), engine);
        // The one installed comes first even if the list calls it differently now.
        if (_ctx.Settings.Luma is { } installed && !hits.Any(h => h.Mod.Name == installed.Mod) && _mods.FirstOrDefault(m => m.Name == installed.Mod) is { } im)
            hits.Insert(0, new LumaMatch(im, L.T("Installed in this game"), 200));
        if (hits.Count == 0)
            _matches.Children.Add(Ui.Muted(L.F("No Luma mod is listed for {0} yet.", _ctx.Game.Name) + " " + L.T("Search the list above if it goes by another name.")));
        _hits = hits.Take(4).ToList();
        FillMatches();
        Refresh();
    }

    List<LumaMatch> _hits = [];

    /// <summary>The matched mods, less the ones removed with their X (remembered for this game; a link brings them back).</summary>
    void FillMatches()
    {
        foreach (var old in _matches.Children.Where(c => c is Border or HyperlinkButton).ToList()) _matches.Children.Remove(old);
        int hidden = 0;
        foreach (var h in _hits)
        {
            if (_ctx.Settings.HiddenModCards.Contains("luma:" + h.Mod.Name)) { hidden++; continue; }
            _matches.Children.Add(Removable(ModCard(h), h.Mod.Name));
        }
        if (hidden > 0)
            _matches.Children.Add(ModUi.ShowRemoved(hidden, () =>
            {
                _ctx.Settings.HiddenModCards.RemoveWhere(k => k.StartsWith("luma:", StringComparison.OrdinalIgnoreCase));
                SettingsStore.Save();
                FillMatches();
                Refresh();
            }));
    }

    Border Removable(Border card, string name) => ModUi.WithRemove(card, () =>
    {
        _ctx.Settings.HiddenModCards.Add("luma:" + name);
        SettingsStore.Save();
        _matches.Children.Remove(card);
        if (_hits.Any(h => h.Mod.Name == name)) FillMatches();
    });

    /// <summary>Redraws what is in the folder now.</summary>
    public void Refresh()
    {
        _state.Children.Clear();
        var dir = _ctx.TargetDir();
        if (dir is null)
        {
            _state.Children.Add(Ui.Muted(_ctx.Game.NotInstalled
                ? L.F("{0} is not installed: the mods below can go in once it is.", _ctx.Game.Name)
                : L.T("Pick the game exe first (the gear beside Play → Change game exe).")));
            return;
        }
        var luma = _ctx.Settings.Luma;
        var files = Luma.Installed(dir);
        bool opti = File.Exists(Path.Combine(dir, "OptiScaler.ini"));
        _state.Children.Add(ModUi.Check(luma is not null || files.Count > 0,
            luma is not null ? L.F("{0} is installed ({1}, {2})", luma.Mod, luma.Asset.Replace(".zip", ""), luma.Release.Replace("latest-", "build "))
            : files.Count > 0 ? L.T("Luma files are in the folder (not installed by Hybrid Launcher)") : L.T("Luma is not installed in this game"),
            luma is not null ? L.F("{0} files, installed {1:d MMM yyyy, HH:mm}", luma.Files.Count, luma.InstalledAt) : null));
        if (opti)
            _state.Children.Add(ModUi.Check(true, L.T("OptiScaler is here: Luma's ReShade goes in as ReShade64.dll and OptiScaler loads it"),
                L.T("Pick one upscaler: Luma's own DLSS, or OptiScaler's (then leave Luma's off in its settings).")));
        else
            _state.Children.Add(ModUi.Check(true, L.T("No OptiScaler here: Luma's ReShade loads as dxgi.dll")));
        foreach (var card in _matches.Children.OfType<Border>()) if (card.Tag is Action update) update();
    }

    Border ModCard(LumaMatch hit)
    {
        var mod = hit.Mod;
        var exe = Core.Detection.GameScanner.EffectiveExe(_ctx.Game, _ctx.Settings);
        bool want32 = exe is not null && RenoDx.Is64Bit(exe) == false;
        var versions = Luma.Versions(mod, _builds, want32);

        var panel = new StackPanel { Spacing = 8 };
        var title = new WrapPanel(8, []);
        title.Children.Add(new TextBlock { Text = mod.Name, FontWeight = FontWeights.SemiBold, FontSize = 17, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        title.Children.Add(Ui.Pill(mod.Status switch { LumaStatus.Working => L.T("Working"), LumaStatus.InProgress => L.T("In progress"), _ => L.T("Planned") },
            mod.Status switch { LumaStatus.Working => Ui.Tone.Good, LumaStatus.InProgress => Ui.Tone.Partial, _ => Ui.Tone.Unknown }));
        if (mod.MoreComing) title.Children.Add(Ui.Chip(L.T("more coming")));
        panel.Children.Add(title);
        panel.Children.Add(Ui.Muted(hit.Reason + (mod.Author.Length > 0 ? "  ·  " + L.F("by {0}", mod.Author) : "")));
        if (mod.Notes is { Length: > 0 } notes) panel.Children.Add(Ui.Callout("", notes, Ui.Tone.Partial));

        // What it adds, from the wiki's feature list of the mod.
        if (mod.Features.Count > 0)
        {
            var list = new StackPanel { Spacing = 3 };
            foreach (var f in mod.Features)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = new SolidColorBrush(Ui.ToneColor(Ui.Tone.Good)), Margin = new Thickness(0, 3, 0, 0), VerticalAlignment = VerticalAlignment.Top });
                var t = new TextBlock { Text = f, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                Grid.SetColumn(t, 1);
                row.Children.Add(t);
                list.Children.Add(row);
            }
            var features = new StackPanel { Spacing = 6 };
            features.Children.Add(new TextBlock { Text = L.T("WHAT IT ADDS"), Style = Ui.Style("GroupHeader") });
            features.Children.Add(mod.Features.Count > 6 ? Ui.Collapsible(list, 6 * 22) : list);
            panel.Children.Add(features);
        }
        if (mod.Engine is not null && mod.Tested.Count > 0)
            panel.Children.Add(Ui.Muted(L.F("Tested with {0} Unreal Engine games; games that need it start with -dx11.", mod.Tested.Count)));

        // The version to install: chosen for this game (stable, right bitness), test and older builds in the list.
        var combo = new ComboBox { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var v in versions) combo.Items.Add(new ComboBoxItem { Content = v.Display + $"  ·  {DiskUsage.Format(v.Size)}", Tag = v });
        if (versions.Count > 0) combo.SelectedIndex = 0;
        var versionHint = Ui.Muted("");
        void Hint()
        {
            var v = (combo.SelectedItem as ComboBoxItem)?.Tag as LumaVersion;
            versionHint.Text = v is null ? "" : v.Test ? L.T("Test build: newer fixes, less tested. Go back to the stable one if something breaks.")
                : v == versions.FirstOrDefault() ? L.F("Recommended: the newest stable build{0}.", want32 ? " " + L.T("(32-bit, like the game)") : "")
                : L.T("An older build: for when a newer one misbehaves in this game.");
        }
        combo.SelectionChanged += (_, _) => Hint();
        Hint();

        var buttons = new WrapPanel(8, []) { Margin = new Thickness(0, 4, 0, 0) };
        var install = ModUi.Action(L.T("Install"), "", () => { if ((combo.SelectedItem as ComboBoxItem)?.Tag is LumaVersion v) _ = InstallAsync(mod, v); }, accent: true);
        var remove = ModUi.Action(L.T("Uninstall"), "", () => _ = UninstallAsync());
        if (versions.Count > 0)
        {
            var pick = new StackPanel { Spacing = 6 };
            pick.Children.Add(combo);
            pick.Children.Add(versionHint);
            panel.Children.Add(pick);
            buttons.Children.Add(install);
        }
        else if (mod.Status != LumaStatus.Planned && mod.Nexus is null && mod.Other is null)
            panel.Children.Add(Ui.Muted(L.T("No build of this mod is published on GitHub yet.")));
        buttons.Children.Add(remove);
        if (mod.Nexus is { } nexus) buttons.Children.Add(ModUi.Action(L.T("Nexus Mods page"), "", () => Ui.Open(nexus.Url)));
        if (mod.Other is { } other) buttons.Children.Add(ModUi.Action(other.Kind, "", () => Ui.Open(other.Url)));
        panel.Children.Add(buttons);

        var card = new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(16, 14, 16, 14), Child = panel };
        void Update()
        {
            bool mine = _ctx.Settings.Luma?.Mod == mod.Name;
            remove.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
            install.IsEnabled = !_busy;
            ((TextBlock)((StackPanel)install.Content).Children[1]).Text = mine ? L.T("Reinstall / change version") : _ctx.Settings.Luma is not null ? L.T("Install (replaces the other Luma mod)") : L.T("Install");
        }
        card.Tag = (Action)Update;
        Update();
        return card;
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

    async Task InstallAsync(LumaMod mod, LumaVersion version)
    {
        if (_busy || !CanInstall(out var dir)) return;
        bool opti = File.Exists(Path.Combine(dir, "OptiScaler.ini"));
        if (await ModBackups.DecideAsync("Luma", _ctx.Game.Name) is not { } backup) return;
        var reshade = ReShade.Detect(dir);
        if (reshade is not null && _ctx.Settings.Luma is null && !await _ctx.ConfirmAsync(this, L.T("ReShade is already in this game"),
                backup
                    ? L.F("Luma brings its own ReShade with add-on support and replaces {0} (Hybrid Launcher keeps a backup and puts it back when you uninstall Luma). Your ReShade presets and shaders stay.", reshade.FileName)
                    : L.F("Luma brings its own ReShade with add-on support and replaces {0}, without a backup (as chosen in Settings → Features). Your ReShade presets and shaders stay.", reshade.FileName),
                L.T("Install Luma")))
            return;
        _busy = true;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        Refresh();
        try
        {
            var state = await Luma.InstallAsync(mod, version, dir, opti, _ctx.Settings.Luma, new Progress<double>(p => _progress.Value = p), _ctx.Token, backup);
            _ctx.Settings.Luma = state;
            // OptiScaler keeps loading ReShade after its own reinstalls and updates.
            if (opti) _ctx.Settings.IniOverrides["Plugins/LoadReshade"] = "true";
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("{0} installed. Start the game and press Home for Luma's settings.", mod.Name));
            _ctx.OptiChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not install Luma"), ex.Message); }
        finally
        {
            _busy = false;
            _progress.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    async Task UninstallAsync()
    {
        var state = _ctx.Settings.Luma;
        if (state is null || _busy || !CanInstall(out var dir)) return;
        if (!await _ctx.ConfirmAsync(this, L.F("Uninstall {0}?", state.Mod),
                L.F("Removes the {0} files Luma added and puts back the {1} file(s) it replaced.", state.Files.Count, state.Backups.Count), L.T("Uninstall")))
            return;
        _busy = true;
        try
        {
            var problems = await Task.Run(() => Luma.Uninstall(state, dir));
            _ctx.Settings.Luma = null;
            if (state.ThroughOpti && !File.Exists(Path.Combine(dir, ReShade.OptiDllName))) _ctx.Settings.IniOverrides.Remove("Plugins/LoadReshade");
            SettingsStore.Save();
            if (problems.Count > 0) App.Window?.ShowError(L.T("Luma removed, with problems"), string.Join("\n", problems));
            else App.Window?.ShowSuccess(L.F("{0} removed and the folder put back as it was.", state.Mod));
            _ctx.OptiChanged();
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not uninstall Luma"), ex.Message); }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
