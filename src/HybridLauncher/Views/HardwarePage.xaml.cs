using HybridLauncher.Core;
using HybridLauncher.Core.Hardware;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// My PC: the displays (resolution, refresh rate, HDR, brightness, colour profile) and the Windows switches that matter for
/// games (GPU scheduling, night light, Game Mode, windowed optimisations) on top; then the hardware: graphics cards,
/// processor, memory, system and drives. Read once per visit (Refresh reads again).
/// </summary>
public sealed partial class HardwarePage : Page
{
    readonly List<(Border Card, int Weight)> _cards = [];
    int _columns;
    IReadOnlyList<GpuInfo> _gpus = [];
    HardwareSnapshot? _hw;

    bool _topWide = true;
    CancellationTokenSource _cts = new();

    public HardwarePage()
    {
        InitializeComponent();
        Loc.Apply(this);
        Root.SizeChanged += (_, e) =>
        {
            var cols = e.NewSize.Width >= 1300 ? 3 : e.NewSize.Width >= 820 ? 2 : 1;
            if (cols != _columns) { _columns = cols; Layout(); LayoutTechs(); }
            LayoutTop(e.NewSize.Width >= 1100);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts.Cancel();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    async Task LoadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        LoadingRing.Visibility = Visibility.Visible;
        Body.Visibility = Visibility.Collapsed;
        HardwareSnapshot hw;
        try { hw = await Task.Run(HardwareInfo.Read, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Error("Hardware read failed", ex);
            App.Window?.ShowError(L.T("Could not read the hardware"), ex.Message);
            LoadingRing.Visibility = Visibility.Collapsed;
            return;
        }
        if (ct.IsCancellationRequested) return;
        Show(hw, ct);
        LoadingRing.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
    }

    void Show(HardwareSnapshot hw, CancellationToken ct)
    {
        MachineTitle.Text = string.IsNullOrWhiteSpace(Environment.MachineName) ? L.T("My PC") : Environment.MachineName;
        var gpu = hw.Gpus.FirstOrDefault();
        MachineSubtitle.Text = string.Join("  ·  ", new[] { gpu?.Name, hw.Cpu.Name, hw.Memory.TotalBytes > 0 ? $"{HardwareInfo.FormatBytes(hw.Memory.TotalBytes)} RAM" : null }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        // ---- displays and switches ----
        DisplaysPanel.Children.Clear();
        if (hw.Displays.Count == 0) DisplaysPanel.Children.Add(Ui.Card(Ui.Muted(L.T("No active display found."))));
        foreach (var d in hw.Displays) DisplaysPanel.Children.Add(DisplayCard(d, ct));
        SwitchesHost.Child = SwitchesCard(hw.Displays.Any(d => d.HdrSupported));

        // ---- hardware ----
        _cards.Clear();
        _hw = hw;
        _gpus = hw.Gpus;
        foreach (var g in hw.Gpus)
        {
            var body = Card("", L.T("Graphics card"), Ui.Pink, out var card);
            body.Children.Add(Big(g.Name));
            body.Children.Add(Facts(
                (L.T("Vendor"), g.Vendor switch { GpuVendor.Nvidia => "Nvidia", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => L.T("Other") }),
                (L.T("Video memory"), g.VramBytes > 0 ? HardwareInfo.FormatBytes(g.VramBytes) : L.T("Unknown")),
                (L.T("Driver"), g.DriverVersion),
                (L.T("Driver date"), g.DriverDate?.ToString("yyyy-MM-dd") ?? L.T("Unknown"))));
            _cards.Add((card, 4));
        }
        if (hw.Gpus.Count == 0)
        {
            var body = Card("", L.T("Graphics card"), Ui.Pink, out var card);
            body.Children.Add(Ui.Muted(L.T("No graphics card found in the registry.")));
            _cards.Add((card, 2));
        }

        {
            var body = Card("", L.T("Processor"), Ui.Blue, out var card);
            body.Children.Add(Big(hw.Cpu.Name));
            body.Children.Add(Facts(
                (L.T("Cores"), hw.Cpu.Cores > 0 ? hw.Cpu.Cores.ToString() : L.T("Unknown")),
                (L.T("Threads"), hw.Cpu.Threads.ToString())));
            _cards.Add((card, 3));
        }

        {
            var m = hw.Memory;
            var body = Card("", L.T("Memory"), Ui.Green, out var card);
            body.Children.Add(Big(m.TotalBytes > 0 ? HardwareInfo.FormatBytes(m.TotalBytes) : L.T("Unknown")));
            if (m.TotalBytes > 0) body.Children.Add(Usage(m.TotalBytes - m.AvailableBytes, m.TotalBytes, L.T("in use")));
            if (m.Modules.Count > 0)
            {
                var speed = m.Modules.Max(x => x.SpeedMts);
                var type = m.Modules.Select(x => x.Type).FirstOrDefault(t => t.Length > 0) ?? "";
                body.Children.Add(Facts(
                    (L.T("Modules"), string.Join(" + ", m.Modules.GroupBy(x => x.SizeBytes).Select(g => $"{g.Count()} × {HardwareInfo.FormatBytes(g.Key)}"))),
                    (L.T("Type"), type.Length > 0 ? type : L.T("Unknown")),
                    (L.T("Speed"), speed > 0 ? $"{speed} MT/s" : L.T("Unknown")),
                    (L.T("Part"), string.Join(", ", m.Modules.Select(x => $"{x.Manufacturer} {x.PartNumber}".Trim()).Where(s => s.Length > 0).Distinct()))));
            }
            _cards.Add((card, 5));
        }

        {
            var s = hw.System;
            var body = Card("", L.T("System"), Ui.Slate, out var card);
            body.Children.Add(Facts(
                ("Windows", L.F("{0}  (build {1})", s.Os, s.Build)),
                (L.T("Motherboard"), s.Board.Length > 0 ? s.Board : L.T("Unknown")),
                ("BIOS", s.Bios.Length > 0 ? s.Bios : L.T("Unknown"))));
            _cards.Add((card, 3));
        }

        {
            var body = Card("", L.T("Storage"), Ui.Orange, out var card);
            foreach (var d in hw.Drives)
            {
                body.Children.Add(Line($"{d.Name}  {d.Label}".Trim(), L.F("{0} free of {1}", HardwareInfo.FormatBytes(d.FreeBytes), HardwareInfo.FormatBytes(d.TotalBytes)) + $"  ·  {d.Format}"));
                body.Children.Add(Usage(d.TotalBytes - d.FreeBytes, d.TotalBytes, null));
            }
            if (hw.Drives.Count == 0) body.Children.Add(Ui.Muted(L.T("No fixed drive found.")));
            _cards.Add((card, 1 + hw.Drives.Count * 2));
        }

        Layout();
        LayoutTechs();
        _ = RefreshTechVersionsAsync(ct);
    }

    /// <summary>The versions correct themselves from the vendors' releases (once a day); the cards redraw when one changed.</summary>
    async Task RefreshTechVersionsAsync(CancellationToken ct)
    {
        if (await Task.Run(() => TechVersions.RefreshAsync(ct: ct)) && !ct.IsCancellationRequested) LayoutTechs();
    }

    /// <summary>One card per graphics card with what it runs, laid out again when the page's width changes columns.</summary>
    void LayoutTechs()
    {
        TechPanel.Children.Clear();
        foreach (var g in _gpus) TechPanel.Children.Add(TechCard(g, Math.Max(1, _columns)));
        if (_gpus.Count == 0) TechPanel.Children.Add(Ui.Card(Ui.Muted(L.T("No graphics card found in the registry."))));
    }

    /// <summary>The displays beside the switches on wide pages, the switches below them otherwise.</summary>
    void LayoutTop(bool wide)
    {
        if (wide == _topWide) return;
        _topWide = wide;
        TopGrid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TopGrid.ColumnDefinitions[0].Width = new GridLength(wide ? 1.6 : 1, GridUnitType.Star);
        TopGrid.ColumnSpacing = wide ? 12 : 0;
        Grid.SetColumn(SwitchesHost, wide ? 1 : 0);
        Grid.SetRow(SwitchesHost, wide ? 0 : 1);
    }

    /// <summary>
    /// The hardware cards in rows, in a fixed order (graphics card, processor, memory, system, storage), every card of a row as
    /// tall as the row: a tidy grid instead of ragged columns.
    /// </summary>
    void Layout()
    {
        CardsGrid.Children.Clear();
        CardsGrid.ColumnDefinitions.Clear();
        CardsGrid.RowDefinitions.Clear();
        int cols = Math.Max(1, _columns);
        CardsGrid.RowSpacing = 12;
        for (int i = 0; i < cols; i++) CardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < _cards.Count; i++)
        {
            if (i % cols == 0) CardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = _cards[i].Card;
            card.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(card, i / cols);
            Grid.SetColumn(card, i % cols);
            // The last card fills the rest of its row.
            Grid.SetColumnSpan(card, i == _cards.Count - 1 ? cols - i % cols : 1);
            CardsGrid.Children.Add(card);
        }
    }

    // ---------- one display ----------

    Border DisplayCard(DisplayInfo d, CancellationToken ct)
    {
        var panel = new StackPanel { Spacing = 8 };
        var hdrText = d.HdrOn ? L.T("HDR on") : d.HdrSupported ? L.T("HDR off (supported)") : "SDR";
        panel.Children.Add(Ui.Heading("", d.Name, d.Primary ? Ui.Pill(L.T("Main display"), Ui.Tone.Accent) : null,
            $"{d.Width} × {d.Height}  ·  {d.RefreshHz:0.##} Hz  ·  {hdrText}", Ui.Teal));
        if (d.GdiName.Length == 0)
        {
            panel.Children.Add(Ui.Muted(L.T("Windows did not give this display's name: its settings can only be changed in Settings → Display.")));
            return Ui.Card(panel);
        }

        // Resolution and refresh rate: changing either asks to keep it, and goes back by itself after 15 seconds.
        var modes = DisplaySettings.Modes(d.GdiName);
        var current = DisplaySettings.Current(d.GdiName);
        var sizes = modes.Select(m => (m.Width, m.Height)).Distinct().ToList();
        var resolution = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (w, h) in sizes) resolution.Items.Add($"{w} × {h}");
        var refresh = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        bool filling = false;
        (int W, int H) Picked() => resolution.SelectedIndex >= 0 ? sizes[resolution.SelectedIndex] : (current?.Width ?? 0, current?.Height ?? 0);
        List<int> RatesFor((int W, int H) size) => modes.Where(m => m.Width == size.W && m.Height == size.H).Select(m => m.Hz).Distinct().OrderByDescending(h => h).ToList();
        void FillRates(int? select)
        {
            filling = true;
            refresh.Items.Clear();
            var rates = RatesFor(Picked());
            foreach (var hz in rates) refresh.Items.Add($"{hz} Hz");
            int index = select is { } s ? rates.IndexOf(s) : -1;
            refresh.SelectedIndex = index >= 0 ? index : rates.Count > 0 ? 0 : -1;
            filling = false;
        }
        filling = true;
        resolution.SelectedIndex = current is null ? -1 : sizes.IndexOf((current.Width, current.Height));
        filling = false;
        FillRates(current?.Hz);
        async void Apply()
        {
            if (filling || current is null) return;
            var (w, h) = Picked();
            var rates = RatesFor((w, h));
            if (refresh.SelectedIndex < 0 || refresh.SelectedIndex >= rates.Count) return;
            var wanted = new DisplayMode(w, h, rates[refresh.SelectedIndex]);
            if (wanted == current) return;
            await ChangeModeAsync(d, current, wanted);
        }
        resolution.SelectionChanged += (_, _) =>
        {
            if (filling) return;
            // A new resolution keeps the refresh rate when the display has it at that size.
            FillRates(current?.Hz);
            Apply();
        };
        refresh.SelectionChanged += (_, _) => Apply();
        if (modes.Count > 0)
        {
            panel.Children.Add(Row(L.T("Resolution"), null, resolution));
            panel.Children.Add(Row(L.T("Refresh rate"), L.T("The highest rate the display offers is usually the right one for games."), refresh));
        }

        // HDR.
        if (d.HdrSupported)
        {
            var hdr = new ToggleSwitch { IsOn = d.HdrOn, OnContent = L.T("On"), OffContent = L.T("Off"), MinWidth = 0 };
            hdr.Toggled += async (_, _) =>
            {
                try
                {
                    var on = hdr.IsOn;
                    await Task.Run(() => DisplaySettings.SetHdr(d, on));
                    // The display needs a moment to switch: then the page reads it again.
                    await Task.Delay(1500);
                    if (!ct.IsCancellationRequested) _ = LoadAsync();
                }
                catch (Exception ex) { App.Window?.ShowError(L.T("Could not change HDR"), ex.Message); }
            };
            panel.Children.Add(Row("HDR", L.T("Brighter highlights and wider colours in HDR games. Windows' own switch (Settings → Display → HDR)."), hdr));
        }
        else panel.Children.Add(Row("HDR", L.T("This display does not support HDR."), null));

        // Brightness: read off the UI thread (DDC/CI answers slowly), shown once known.
        var brightRow = new Border();
        panel.Children.Add(brightRow);
        _ = FillBrightnessAsync(d, brightRow, ct);

        // Colour profile.
        var (profiles, def) = DisplaySettings.ColorProfiles(d);
        var openColour = Ui.LinkButton(L.T("Colour management"), "", () => Ui.Open("ms-settings:display-advancedcolor"));
        if (profiles.Count > 1 || (profiles.Count == 1 && def is null))
        {
            var combo = new ComboBox { MinWidth = 200, MaxWidth = 280, VerticalAlignment = VerticalAlignment.Center };
            foreach (var p in profiles) combo.Items.Add(Path.GetFileNameWithoutExtension(p));
            combo.SelectedIndex = def is null ? -1 : profiles.FindIndex(p => p.Equals(def, StringComparison.OrdinalIgnoreCase));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                try
                {
                    DisplaySettings.SetColorProfile(d, profiles[combo.SelectedIndex]);
                    App.Window?.ShowSuccess(L.F("{0} is now the colour profile of {1}.", Path.GetFileNameWithoutExtension(profiles[combo.SelectedIndex]), d.Name));
                }
                catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the colour profile"), ex.Message); }
            };
            panel.Children.Add(Row(L.T("Colour profile"), d.HdrOn ? L.T("Profiles for HDR mode") : L.T("Profiles for SDR mode"), combo));
        }
        else
        {
            var only = def is not null ? Path.GetFileNameWithoutExtension(def) : L.T("None (Windows default)");
            panel.Children.Add(Row(L.T("Colour profile"), L.T("Add more profiles in Windows' colour settings, then pick them here."), new TextBlock { Text = only, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 }));
        }

        var links = new WrapPanel(8, []) { Margin = new Thickness(0, 4, 0, 0) };
        links.Children.Add(Ui.LinkButton(L.T("Display settings"), "", () => Ui.Open("ms-settings:display")));
        links.Children.Add(Ui.LinkButton(L.T("Advanced display"), "", () => Ui.Open("ms-settings:display-advanced")));
        links.Children.Add(openColour);
        panel.Children.Add(links);
        return Ui.Card(panel);
    }

    async Task FillBrightnessAsync(DisplayInfo d, Border host, CancellationToken ct)
    {
        int? value = null;
        try { value = await Task.Run(() => DisplaySettings.Brightness(d), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"Brightness of {d.Name}: {ex.Message}"); }
        if (ct.IsCancellationRequested) return;
        if (value is not { } v)
        {
            host.Child = Row(L.T("Brightness"), d.Internal ? L.T("Windows does not let apps change this screen's brightness.")
                : L.T("This monitor does not take brightness from Windows. Turn on DDC/CI in the monitor's own menu, then Refresh."), null);
            return;
        }
        var label = new TextBlock { Text = $"{v}%", MinWidth = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = v, Width = 180, VerticalAlignment = VerticalAlignment.Center };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        bool setting = false;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (setting) { timer.Start(); return; }
            setting = true;
            int percent = (int)slider.Value;
            try
            {
                if (!await Task.Run(() => DisplaySettings.SetBrightness(d, percent)))
                    App.Window?.ShowError(L.T("Could not change the brightness"), L.T("The display did not accept the new value."));
            }
            catch (Exception ex) { App.Window?.ShowError(L.T("Could not change the brightness"), ex.Message); }
            finally { setting = false; }
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = $"{(int)e.NewValue}%";
            timer.Stop();
            timer.Start();
        };
        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        control.Children.Add(slider);
        control.Children.Add(label);
        host.Child = Row(L.T("Brightness"), d.Internal ? L.T("The built-in screen.") : L.T("Set over DDC/CI, like the monitor's own buttons."), control);
    }

    /// <summary>Applies a new mode, then asks to keep it; no answer within 15 seconds puts the old mode back.</summary>
    async Task ChangeModeAsync(DisplayInfo d, DisplayMode before, DisplayMode wanted)
    {
        try
        {
            await Task.Run(() => DisplaySettings.SetMode(d.GdiName, wanted));
        }
        catch (Exception ex)
        {
            App.Window?.ShowError(L.T("Could not change the display mode"), ex.Message);
            _ = LoadAsync();
            return;
        }
        int seconds = 15;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Update() => text.Text = L.F("{0} now runs at {1} × {2}, {3} Hz. Keep it? The previous mode comes back in {4} s.", d.Name, wanted.Width, wanted.Height, wanted.Hz, seconds);
        Update();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.T("Keep these display settings?"),
            Content = text,
            PrimaryButtonText = L.T("Keep"),
            CloseButtonText = L.T("Go back"),
            DefaultButton = ContentDialogButton.Close,
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            seconds--;
            Update();
            if (seconds <= 0)
            {
                timer.Stop();
                dialog.Hide();
            }
        };
        timer.Start();
        var result = await dialog.ShowAsync();
        timer.Stop();
        if (result != ContentDialogResult.Primary)
        {
            try { await Task.Run(() => DisplaySettings.SetMode(d.GdiName, before)); }
            catch (Exception ex) { App.Window?.ShowError(L.T("Could not put the previous mode back"), ex.Message); }
        }
        _ = LoadAsync();
    }

    // ---------- the switches for games ----------

    Border SwitchesCard(bool anyHdr)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Ui.Heading("", L.T("Graphics settings for games"), subtitle: L.T("Windows' own switches, the same as in its Settings app"), tint: Color.FromArgb(0xFF, 0x9A, 0x7C, 0xFF)));

        // GPU scheduling: machine-wide, needs administrator rights and a restart.
        var hags = DisplaySettings.HardwareScheduling;
        panel.Children.Add(Switch(L.T("Hardware-accelerated GPU scheduling"),
            hags is null ? L.T("Not offered by this graphics card or driver.") : L.T("Needed by DLSS Frame Generation, recommended for any frame generation. Windows asks for administrator rights; it applies after a restart."),
            hags, async on =>
            {
                bool admin = Elevation.IsAdmin;
                bool done = await Task.Run(() => DisplaySettings.SetHardwareScheduling(on, admin));
                if (done) App.Window?.ShowInfo(L.T("Saved. Restart Windows to apply the GPU scheduling change."));
                return done;
            }));

        var night = DisplaySettings.NightLight;
        panel.Children.Add(Switch(L.T("Night light"),
            night is null ? L.T("Turn it on once in Windows' Settings; then it can be switched from here.") : L.T("Warmer colours to ease the eyes at night. Turn it off for colour-accurate games and HDR."),
            night, on =>
            {
                if (DisplaySettings.SetNightLight(on)) return Task.FromResult(true);
                Ui.Open("ms-settings:nightlight");
                return Task.FromResult(false);
            }));


        var links = new WrapPanel(8, []) { Margin = new Thickness(0, 6, 0, 0) };
        links.Children.Add(Ui.LinkButton(L.T("Graphics settings"), "", () => Ui.Open("ms-settings:display-advancedgraphics")));
        links.Children.Add(Ui.LinkButton(L.T("HDR settings"), "", () => Ui.Open("ms-settings:display-hdr")));
        links.Children.Add(Ui.LinkButton(L.T("Night light"), "", () => Ui.Open("ms-settings:nightlight")));
        panel.Children.Add(links);
        return Ui.Card(panel);
    }

    /// <summary>A row with a switch: the change is applied at once; refused or failed, the switch goes back.</summary>
    static Border Switch(string title, string description, bool? on, Func<bool, Task<bool>> apply, bool enabled = true, string? disabledNote = null)
    {
        var toggle = new ToggleSwitch { IsOn = on == true, OnContent = L.T("On"), OffContent = L.T("Off"), MinWidth = 0, IsEnabled = on is not null && enabled };
        bool reverting = false;
        toggle.Toggled += async (_, _) =>
        {
            if (reverting) return;
            bool wanted = toggle.IsOn;
            bool ok;
            try { ok = await apply(wanted); }
            catch (Exception ex)
            {
                App.Window?.ShowError(L.F("Could not change {0}", title), ex.Message);
                ok = false;
            }
            if (ok) return;
            reverting = true;
            toggle.IsOn = !wanted;
            reverting = false;
        };
        return Row(title, enabled || disabledNote is null ? description : disabledNote, toggle);
    }

    // ---------- building blocks ----------

    /// <summary>
    /// The technologies a graphics card runs, one column per kind (upscaling, frame generation, ray reconstruction, latency;
    /// two columns or one on narrower pages). Each one is a line with its exact version as a chip and whether it runs on the
    /// card itself ("Native") or only through OptiScaler; the ones the card cannot run are dimmed.
    /// </summary>
    static Border TechCard(GpuInfo gpu, int pageColumns)
    {
        var body = Card("\uE7F4", gpu.Name, Ui.Pink, out var card);
        body.Children.Add(new TextBlock
        {
            Text = L.T("The newest version of each technology (its latest release, whatever driver is installed). Native runs on the card itself; the others work through OptiScaler. Update the driver to get the newest ones."),
            Style = S("RowDescription"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -4, 0, 2),
        });
        var groups = gpu.Techs.GroupBy(t => t.Kind).ToList();
        int cols = Math.Max(1, Math.Min(groups.Count, pageColumns >= 3 ? 4 : pageColumns == 2 ? 2 : 1));
        var grid = new Grid { ColumnSpacing = 28, RowSpacing = 18 };
        for (int i = 0; i < cols; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < groups.Count; i++)
        {
            if (i % cols == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var column = new StackPanel { Spacing = 10 };
            column.Children.Add(new TextBlock
            {
                Text = L.T(groups[i].Key switch
                {
                    GpuTechKind.Upscaling => "Upscaling",
                    GpuTechKind.FrameGeneration => "Frame generation",
                    GpuTechKind.RayReconstruction => "Ray reconstruction",
                    _ => "Latency",
                }),
                Style = S("GroupHeader"),
            });
            foreach (var t in groups[i]) column.Children.Add(TechLine(t));
            Grid.SetRow(column, i / cols);
            Grid.SetColumn(column, i % cols);
            grid.Children.Add(column);
        }
        body.Children.Add(grid);
        return card;
    }

    /// <summary>One technology: name and version chip on the first line, how it runs and the note under them.</summary>
    static FrameworkElement TechLine(GpuTech t)
    {
        var panel = new StackPanel { Spacing = 2, Opacity = t.Native ? 1 : 0.6 };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = t.Name,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = t.Native ? FontWeights.SemiBold : FontWeights.Normal,
        });
        bool lit = t.Native && t.Version.Length > 0;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 2),
            MinWidth = 36,
            VerticalAlignment = VerticalAlignment.Center,
            Background = lit ? B("AccentFillColorDefaultBrush") : B("ControlFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = t.Version.Length > 0 ? t.Version : "\u2014",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = lit ? B("TextOnAccentFillColorPrimaryBrush") : B("TextFillColorSecondaryBrush"),
            },
        };
        Grid.SetColumn(chip, 1);
        top.Children.Add(chip);
        panel.Children.Add(top);
        var how = t.Native ? L.T("Native") : L.T("Through OptiScaler");
        panel.Children.Add(new TextBlock
        {
            Text = t.Note.Length > 0 ? how + "  \u00B7  " + L.T(t.Note) : how,
            Style = S("RowDescription"),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    static Style S(string key) => (Style)HybridLauncher.Helpers.ThemeRes.Current[key];
    static Brush B(string key) => (Brush)HybridLauncher.Helpers.ThemeRes.Current[key];

    /// <summary>A setting row: name and description on the left, its control on the right (wrapping under on narrow cards).</summary>
    static Border Row(string title, string? description, FrameworkElement? control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = S("RowHeader") });
        if (description is { Length: > 0 }) text.Children.Add(new TextBlock { Text = description, Style = S("RowDescription") });
        grid.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return new Border { Style = S("RowStyle"), Child = grid };
    }

    static StackPanel Card(string glyph, string title, Color tint, out Border card)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Ui.Heading(glyph, title, tint: tint));
        card = Ui.Card(body);
        return body;
    }

    static TextBlock Big(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    /// <summary>Label / value pairs in two aligned columns.</summary>
    static Grid Facts(params (string Label, string Value)[] facts)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (var (label, value) in facts.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Style = S("RowDescription"), VerticalAlignment = VerticalAlignment.Top };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            row++;
        }
        return grid;
    }

    static StackPanel Line(string title, string detail)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock { Text = title, Style = S("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true });
        panel.Children.Add(new TextBlock { Text = detail, Style = S("RowDescription") });
        return panel;
    }

    static FrameworkElement Usage(ulong used, ulong total, string? label)
    {
        var panel = new StackPanel { Spacing = 3 };
        double ratio = total == 0 ? 0 : (double)used / total;
        panel.Children.Add(new ProgressBar { Maximum = 1, Value = ratio, MinHeight = 4 });
        if (label is not null)
            panel.Children.Add(new TextBlock { Text = $"{HardwareInfo.FormatBytes(used)} {label} ({ratio:P0})", Style = S("RowDescription") });
        return panel;
    }

    /// <summary>A highlighted hint inside a card.</summary>
    static Border Note(string glyph, string title, string text)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = B("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, Style = S("BodyStrongTextBlockStyle") });
        texts.Children.Add(new TextBlock { Text = text, Style = S("RowDescription") });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return new Border { Style = S("GroupStyle"), Child = grid };
    }
}
