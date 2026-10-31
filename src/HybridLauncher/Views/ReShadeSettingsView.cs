using HybridLauncher.Core;
using HybridLauncher.Core.Ini;
using HybridLauncher.Core.Opti;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Views;

/// <summary>
/// ReShade.ini in the ReShade tab, the way the OptiScaler tab shows OptiScaler.ini: first the settings changed most (performance
/// mode, the FPS and clock overlays, the tutorial, the keys, where screenshots go) with plain names, then every key of the file
/// with a search, switches for the on / off ones. Written to the file at once. ReShade writes the file again when the game
/// closes, so a change made while it runs may be undone: the panel says so.
/// </summary>
public sealed partial class ReShadeSettingsView : UserControl
{
    readonly Func<string?> _dir;
    readonly StackPanel _panel = new() { Spacing = 10 };
    // Made again at every redraw (an element can have one parent only, and the old expander may still hold the old ones).
    StackPanel _all = new() { Spacing = 4 };
    AutoSuggestBox _search = new();
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    IniDocument? _doc;

    // The settings changed most: (section/key, name, what it does, kind). Kinds: bool, tutorial, key, folder.
    static readonly (string Path, string Name, string Info, string Kind)[] Quick =
    [
        ("GENERAL/PerformanceMode", "Performance mode", "Effects are compiled once and load faster. Turn it off to change effects in game.", "bool"),
        ("OVERLAY/ShowFPS", "Show FPS", "ReShade's frame rate counter in a corner of the game.", "bool"),
        ("OVERLAY/ShowFrameTime", "Show frame time", "The time each frame takes, beside the FPS.", "bool"),
        ("OVERLAY/ShowClock", "Show clock", "The time of day in a corner of the game.", "bool"),
        ("OVERLAY/TutorialProgress", "Skip the tutorial", "ReShade's first-start tutorial is not shown.", "tutorial"),
        ("INPUT/KeyOverlay", "Open ReShade's menu", "The key that opens ReShade in game (Home unless changed).", "key"),
        ("INPUT/KeyEffects", "Effects on / off", "Turns every effect off and on again, to compare.", "key"),
        ("INPUT/KeyScreenshot", "Screenshot", "Saves a screenshot with the effects.", "key"),
        ("SCREENSHOT/SavePath", "Screenshots folder", "Where ReShade's screenshots go.", "folder"),
    ];

    public ReShadeSettingsView(Func<string?> dir)
    {
        _dir = dir;
        Content = _panel;
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); FillAll(); };
    }

    /// <summary>Reads ReShade.ini again (after an install, a preset change, the game closing) and redraws.</summary>
    public void Refresh()
    {
        _panel.Children.Clear();
        _doc = null;
        var dir = _dir();
        var path = dir is null ? null : Path.Combine(dir, "ReShade.ini");
        if (path is null || !File.Exists(path))
        {
            _panel.Children.Add(Ui.Muted(L.T("ReShade.ini is made the first time the game runs with ReShade. Its settings show here then.")));
            return;
        }
        try { _doc = IniDocument.Load(path); }
        catch (Exception ex)
        {
            _panel.Children.Add(Ui.Muted(L.F("Could not read ReShade.ini: {0}", ex.Message)));
            return;
        }
        if (OptiInstaller.RunningGameProcess(dir!) is not null)
            _panel.Children.Add(ModUi.Check(null, L.T("The game is running"), L.T("ReShade saves its settings when the game closes, which can undo a change made here now. Change them in ReShade's menu, or after closing the game.")));

        var grid = new TileGrid(420, 10);
        foreach (var (key, name, info, kind) in Quick)
            if (_doc.Find(key) is { } entry) grid.Children.Add(QuickRow(entry, L.T(name), L.T(info), kind));
        if (grid.Children.Count > 0) _panel.Children.Add(grid);

        // Every key of the file, folded by default (the quick ones above are what most people need).
        var expander = new Expander
        {
            Header = new TextBlock { Text = L.F("Every ReShade.ini setting ({0})", _doc.Entries.Count), FontWeight = FontWeights.SemiBold },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = AppState.Settings.CollapsedCards.Contains("reshade:allini"),
        };
        expander.Expanding += (_, _) => { AppState.Settings.CollapsedCards.Add("reshade:allini"); SettingsStore.Save(); };
        expander.Collapsed += (_, _) => { AppState.Settings.CollapsedCards.Remove("reshade:allini"); SettingsStore.Save(); };
        var query = _search.Text;
        _search = new AutoSuggestBox { PlaceholderText = L.T("Search ReShade.ini"), QueryIcon = new SymbolIcon(Symbol.Find), Text = query };
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _all = new StackPanel { Spacing = 4 };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(_search);
        body.Children.Add(_all);
        expander.Content = body;
        _panel.Children.Add(expander);
        var open = ModUi.Action(L.T("Open ReShade.ini"), "\uE8E5", () => ModUi.OpenFile(path, pickApp: true));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        _panel.Children.Add(open);
        FillAll();
    }

    Border QuickRow(IniEntry entry, string name, string info, string kind)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = info, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        FrameworkElement control = kind switch
        {
            "bool" => Switch(entry, entry.Value.Trim() == "1", on => on ? "1" : "0"),
            "tutorial" => Switch(entry, int.TryParse(entry.Value, out var t) && t >= 4, on => on ? "4" : "0"),
            "key" => KeyButton(entry),
            _ => FolderButtons(entry),
        };
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        ToolTipService.SetToolTip(text, $"[{entry.Section}] {entry.Key} = {entry.Value}");
        return new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(14, 8, 10, 8), Child = grid };
    }

    ToggleSwitch Switch(IniEntry entry, bool on, Func<bool, string> value)
    {
        var sw = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        sw.Toggled += (_, _) => Write(entry, value(sw.IsOn));
        return sw;
    }

    /// <summary>A key binding as its name ("Ctrl+F2"); a click waits for the next key pressed and binds it (Esc cancels).</summary>
    Button KeyButton(IniEntry entry)
    {
        var b = new Button { Content = KeyName(entry.Value), MinWidth = 110, Padding = new Thickness(12, 5, 12, 6) };
        bool waiting = false;
        b.Click += (_, _) =>
        {
            waiting = true;
            b.Content = L.T("Press a key…");
            b.Focus(FocusState.Programmatic);
        };
        b.LostFocus += (_, _) => { if (waiting) { waiting = false; b.Content = KeyName(entry.Value); } };
        b.PreviewKeyDown += (_, e) =>
        {
            if (!waiting) return;
            var vk = (int)e.Key;
            // Modifiers alone are not a binding: they wait for the key they go with.
            if (vk is 16 or 17 or 18 or 160 or 161 or 162 or 163 or 164 or 165) return;
            e.Handled = true;
            waiting = false;
            if (e.Key == Windows.System.VirtualKey.Escape) { b.Content = KeyName(entry.Value); return; }
            static bool Down(Windows.System.VirtualKey k) =>
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var value = $"{vk},{(Down(Windows.System.VirtualKey.Control) ? 1 : 0)},{(Down(Windows.System.VirtualKey.Shift) ? 1 : 0)},{(Down(Windows.System.VirtualKey.Menu) ? 1 : 0)}";
            Write(entry, value);
            b.Content = KeyName(value);
        };
        ToolTipService.SetToolTip(b, L.T("Click, then press the new key (Esc to cancel)"));
        return b;
    }

    StackPanel FolderButtons(IniEntry entry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var dir = _dir()!;
        string Full() => entry.Value.Trim().Trim('"') is { Length: > 0 } v ? (Path.IsPathRooted(v) ? v : Path.GetFullPath(Path.Combine(dir, v))) : dir;
        var open = new Button { Content = new FontIcon { Glyph = "\uE838", FontSize = 13 }, Style = Ui.Style("IconButton") };
        ToolTipService.SetToolTip(open, Full());
        open.Click += (_, _) => Ui.Safe(() => { Directory.CreateDirectory(Full()); GameLauncher.OpenFolder(Full()); });
        var change = new Button { Content = L.T("Change…"), Padding = new Thickness(12, 5, 12, 6) };
        change.Click += async (_, _) =>
        {
            if (await Pickers.PickFolderAsync(App.Window!) is { } picked)
            {
                Write(entry, picked);
                ToolTipService.SetToolTip(open, Full());
            }
        };
        row.Children.Add(open);
        row.Children.Add(change);
        return row;
    }

    /// <summary>Every key of the file (or those matching the search), by section: switches for 0 / 1, a box for the rest.</summary>
    void FillAll()
    {
        _all.Children.Clear();
        if (_doc is not { } doc) return;
        var q = _search.Text.Trim();
        foreach (var section in doc.Entries.Where(e => q.Length < 2 || e.Path.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(e => e.Section))
        {
            _all.Children.Add(new TextBlock { Text = section.Key.ToUpper(), Style = Ui.Style("GroupHeader"), FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
            foreach (var e in section)
            {
                var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 3, 6, 3) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.Children.Add(new TextBlock { Text = e.Key, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true });
                FrameworkElement edit;
                if (e.Value.Trim() is "0" or "1") edit = Switch(e, e.Value.Trim() == "1", on => on ? "1" : "0");
                else
                {
                    var box = new TextBox { Text = e.Value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
                    var entry = e;
                    void Commit() { if (box.Text != entry.Value) Write(entry, box.Text); }
                    box.LostFocus += (_, _) => Commit();
                    box.KeyDown += (_, k) => { if (k.Key == Windows.System.VirtualKey.Enter) Commit(); };
                    edit = box;
                }
                Grid.SetColumn(edit, 1);
                grid.Children.Add(edit);
                _all.Children.Add(grid);
            }
        }
        if (_all.Children.Count == 0) _all.Children.Add(Ui.Muted(L.F("Nothing in ReShade.ini matches \"{0}\".", q)));
    }

    void Write(IniEntry entry, string value)
    {
        if (_doc is null) return;
        try
        {
            _doc.Set(entry.Section, entry.Key, value);
            _doc.Save();
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not save ReShade.ini"), ex.Message); }
    }

    /// <summary>"36,0,0,0" (key code, Ctrl, Shift, Alt) as "Home"; "0,0,0,0" as "None".</summary>
    public static string KeyName(string value)
    {
        var parts = value.Split(',').Select(p => int.TryParse(p.Trim(), out var v) ? v : 0).ToArray();
        if (parts.Length == 0 || parts[0] == 0) return L.T("None");
        var name = parts[0] switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)parts[0]).ToString(),
            >= 0x70 and <= 0x87 => $"F{parts[0] - 0x6F}",
            >= 0x60 and <= 0x69 => $"Num {parts[0] - 0x60}",
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "Caps Lock", 0x1B => "Esc", 0x20 => "Space",
            0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home", 0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
            0x2C => "Print Screen", 0x2D => "Insert", 0x2E => "Delete", 0x6A => "Num *", 0x6B => "Num +", 0x6D => "Num -", 0x6E => "Num .",
            0x6F => "Num /", 0x90 => "Num Lock", 0x91 => "Scroll Lock", 0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDC => "\\",
            _ => $"#{parts[0]}",
        };
        return (parts.Length > 1 && parts[1] != 0 ? "Ctrl+" : "") + (parts.Length > 2 && parts[2] != 0 ? "Shift+" : "") + (parts.Length > 3 && parts[3] != 0 ? "Alt+" : "") + name;
    }
}
