using HybridLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Gaming.Input;

namespace HybridLauncher.Views;

/// <summary>
/// Settings → Controller: controller navigation on or off, what each button does (<see cref="PadActions"/>), and the Home
/// button that brings Hybrid Launcher to the front from a game. Built in code and made again when something changes.
/// </summary>
public static class ControllerSettingsView
{
    static readonly (string Tag, string Name)[] Triggers = [("Press", "Press"), ("Hold", "Hold for half a second")];
    static readonly (string Tag, string Name)[] Opens = [("Game", "The page of the game you are playing"), ("Library", "The library"), ("Last", "Where you left it")];

    public static void Fill(Panel host)
    {
        host.Children.Clear();
        var s = AppState.Settings;
        var c = s.Controller;
        void Save(bool again)
        {
            SettingsStore.Save();
            if (again) Fill(host);
        }

        // ---- navigation ----
        host.Children.Add(Ui2.Header("NAVIGATION", top: false));
        string connected;
        try
        {
            var names = Gamepad.Gamepads.Select(p => RawGameController.FromGameController(p)?.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            connected = Gamepad.Gamepads.Count == 0 ? L.T("No controller connected right now.")
                : names.Count > 0 ? L.F("Connected: {0}.", string.Join(", ", names)) : L.T("A controller is connected.");
        }
        catch { connected = ""; }
        host.Children.Add(Ui2.Row(Ui2.Text(L.T("Controller navigation"),
                L.T("A controller moves around Hybrid Launcher while its window is in front: the D-pad or left stick moves, the right stick scrolls, the buttons do what is set below. Turn it off if another tool (JoyToKey, JoyXoff, Steam Input) already moves the mouse with your controller, so each press acts only once."),
                connected),
            Ui2.Toggle(s.GamepadInput, on =>
            {
                s.GamepadInput = on;
                Save(again: true);
                App.Window?.GamepadSettingChanged();
            })));

        // ---- buttons ----
        host.Children.Add(Ui2.Header("BUTTONS", top: true));
        host.Children.Add(Ui2.Note(L.T("What each button does while Hybrid Launcher is in front (PlayStation names in brackets). A button does one thing: giving it to an action swaps it with the action that had it.")));
        double dim = s.GamepadInput ? 1 : 0.5;
        foreach (var (action, name, description) in PadActions.All)
        {
            var combo = new ComboBox { MinWidth = 220 };
            combo.Items.Add(L.T("None"));
            foreach (var b in PadActions.Buttons) combo.Items.Add(L.T(PadActions.Label(b)));
            var current = PadActions.ButtonOf(action);
            combo.SelectedIndex = current is { } k ? Array.IndexOf(PadActions.Buttons, k) + 1 : 0;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                PadKey? picked = combo.SelectedIndex == 0 ? null : PadActions.Buttons[combo.SelectedIndex - 1];
                if (picked == PadActions.ButtonOf(action)) return;
                PadActions.Set(action, picked);
                // Not made again at once: the drop-down that changed would close under the controller's focus.
                host.DispatcherQueue.TryEnqueue(() => Save(again: true));
            };
            var row = Ui2.Row(Ui2.Text(L.T(name), L.T(description)), combo);
            row.Opacity = dim;
            host.Children.Add(row);
        }
        var reset = new Button { Content = L.T("Reset buttons"), IsEnabled = PadActions.Changed };
        reset.Click += (_, _) => { PadActions.Reset(); Save(again: true); };
        host.Children.Add(Ui2.Row(Ui2.Text(L.T("Default buttons"), L.T("A presses, B goes back, X opens the options menu, Y plays, LB / RB change tab, LT / RT scroll a page, Menu goes to the game list, View to Search.")), reset));

        // ---- Home button ----
        host.Children.Add(Ui2.Header("HOME BUTTON", top: true));
        host.Children.Add(Ui2.Row(Ui2.Text(L.T("Home button brings Hybrid Launcher back"),
                L.T("Press the Xbox / Guide or PS button in a game or any app and Hybrid Launcher comes to the front, out of the notification area too, like a console's home screen: your achievements, notes and mods are a press away. The game keeps running.")),
            Ui2.Toggle(c.HomeButton, on =>
            {
                c.HomeButton = on;
                Save(again: true);
                App.Window?.HomeButtonSettingChanged();
            })));
        double homeDim = c.HomeButton ? 1 : 0.5;
        Border Dim(Border b) { b.Opacity = homeDim; return b; }

        host.Children.Add(Dim(Ui2.Row(Ui2.Text(L.T("Press or hold"), L.T("Hold leaves a short press to Windows or your handheld (Game Bar, the maker's menu).")),
            Ui2.Combo(Triggers, c.HomeTrigger, v => { c.HomeTrigger = v; Save(again: false); }))));
        host.Children.Add(Dim(Ui2.Row(Ui2.Text(L.T("Opens on"), L.T("Where Hybrid Launcher opens when the Home button brings it forward.")),
            Ui2.Combo(Opens, c.HomeOpens, v => { c.HomeOpens = v; Save(again: false); }))));
        host.Children.Add(Dim(Ui2.Row(Ui2.Text(L.T("Press again to go back"), L.T("With Hybrid Launcher in front, the Home button takes you back to the game or app you came from.")),
            Ui2.Toggle(c.HomeGoesBack, on => { c.HomeGoesBack = on; Save(again: false); }))));
        host.Children.Add(Dim(Ui2.Row(Ui2.Text(L.T("PlayStation controllers connected directly"),
                L.T("Reads the PS button of a DualShock 4 or DualSense plugged in or paired with Windows itself. Through DS4Windows, DualSenseX or Steam Input they show up as an Xbox controller, which is read already. Read only: the game reads the controller as before.")),
            Ui2.Toggle(c.HomePlayStation, on =>
            {
                c.HomePlayStation = on;
                Save(again: false);
                App.Window?.HomeButtonSettingChanged();
            }))));
        var gameBar = new Button { Content = L.T("Game Bar settings") };
        gameBar.Click += async (_, _) =>
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:gaming-gamebar")); }
            catch (Exception ex) { Log.Warn($"Game Bar settings: {ex.Message}"); }
        };
        host.Children.Add(Dim(Ui2.Row(Ui2.Text(L.T("Xbox button and Game Bar"),
                L.T("Windows opens Game Bar with the Xbox button. To leave the button to Hybrid Launcher, turn off \"Open Game Bar using this button on a controller\" in Game Bar's settings, or pick Hold above. On a handheld (ROG Ally, Legion Go, MSI Claw), its maker's app can map a button to Xbox / Guide.")),
            gameBar)));
    }
}

/// <summary>The rows the code-built settings tabs are made of, in the page's own styles.</summary>
static class Ui2
{
    public static TextBlock Header(string text, bool top) =>
        new() { Text = L.T(text), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, top ? 20 : 0, 0, 4) };

    public static TextBlock Note(string text) =>
        new() { Text = text, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

    public static StackPanel Text(string header, string description, string? extra = null)
    {
        var p = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new TextBlock { Text = header, Style = Ui.Style("RowHeader") });
        p.Children.Add(new TextBlock { Text = description, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(extra)) p.Children.Add(new TextBlock { Text = extra, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        return p;
    }

    public static ToggleSwitch Toggle(bool on, Action<bool> set)
    {
        var t = new ToggleSwitch { IsOn = on, OnContent = L.T("On"), OffContent = L.T("Off"), MinWidth = 0 };
        t.Toggled += (_, _) => set(t.IsOn);
        return t;
    }

    public static ComboBox Combo((string Tag, string Name)[] items, string value, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 220 };
        foreach (var (_, name) in items) combo.Items.Add(L.T(name));
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.Tag == value));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) set(items[combo.SelectedIndex].Tag); };
        return combo;
    }

    public static Border Row(FrameworkElement text, FrameworkElement action)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return new Border { Style = Ui.Style("RowStyle"), Child = grid };
    }
}
