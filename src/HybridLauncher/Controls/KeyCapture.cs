using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HybridLauncher.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HybridLauncher.Controls;

/// <summary>
/// A key shortcut of OptiScaler.ini (a Windows virtual-key code: "0x2D", "auto", "-1") shown as the key's name, set by pressing
/// the key: click it, press any key, done. Esc cancels; the ⋯ menu goes back to Auto or turns the shortcut off.
/// Raises <see cref="KeyChosen"/> with the value to write ("0x2D", "auto" or "-1").
/// </summary>
public sealed partial class KeyCapture : UserControl
{
    readonly Button _button;
    readonly TextBlock _label = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly FontIcon _icon = new() { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    string _value = "auto";
    int? _default;
    bool _listening;
    DateTime _acceptedAt;

    public event Action<string>? KeyChosen;

    public KeyCapture()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(_icon);
        content.Children.Add(_label);
        _button = new Button { Content = content, MinWidth = 150, Padding = new Thickness(12, 6, 12, 6), HorizontalContentAlignment = HorizontalAlignment.Left };
        // The key just chosen (Space, Enter) must not "click" the button again and restart listening.
        _button.Click += (_, _) => { if (DateTime.UtcNow - _acceptedAt > TimeSpan.FromMilliseconds(400)) Listen(!_listening); };
        _button.PreviewKeyDown += OnKey;
        _button.KeyUp += OnKeyUp;
        _button.LostFocus += (_, _) => Listen(false);

        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6), MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTipService.SetToolTip(more, L.T("More"));
        var menu = new MenuFlyout();
        var auto = new MenuFlyoutItem { Text = L.T("Back to Auto (OptiScaler's default key)"), Icon = new FontIcon { Glyph = "" } };
        auto.Click += (_, _) => Choose("auto");
        var none = new MenuFlyoutItem { Text = L.T("No key (turn the shortcut off)"), Icon = new FontIcon { Glyph = "" } };
        none.Click += (_, _) => Choose("-1");
        menu.Items.Add(auto);
        menu.Items.Add(none);
        more.Flyout = menu;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(_button);
        row.Children.Add(more);
        Content = row;
        Update();
    }

    /// <summary>The value in the ini and, for Auto, the key OptiScaler uses then (read from the ini's comment).</summary>
    public void Set(string value, int? defaultKey)
    {
        _value = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim();
        _default = defaultKey;
        if (!_listening) Update();
    }

    void Listen(bool on)
    {
        _listening = on;
        if (on) _button.Focus(FocusState.Programmatic);
        Update();
    }

    void Update()
    {
        if (_listening)
        {
            _label.Text = L.T("Press a key… (Esc cancels)");
            _icon.Glyph = "";
            _button.Style = (Style)HybridLauncher.Helpers.ThemeRes.Current["AccentButtonStyle"];
            return;
        }
        _button.ClearValue(StyleProperty);
        _icon.Glyph = "";
        bool auto = _value.Equals("auto", StringComparison.OrdinalIgnoreCase);
        var vk = auto ? _default : Parse(_value);
        _label.Text = _value == "-1" ? L.T("No key")
            : auto ? (vk is { } d ? L.F("Auto · {0}", KeyName(d)) : "Auto")
            : vk is { } k ? KeyName(k) : _value;
        _label.Opacity = auto ? 0.75 : 1;
        ToolTipService.SetToolTip(_button, L.T("Click, then press the key you want") + (vk is { } c ? $"\n{KeyName(c)} = 0x{c:X2}" : ""));
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_listening) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            Listen(false);
            return;
        }
        // Modifiers alone are allowed (OptiScaler takes one key), but Print Screen only arrives on release.
        if (e.Key == VirtualKey.Snapshot) return;
        Accept((int)e.OriginalKey);
    }

    void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_listening && e.Key == VirtualKey.Snapshot)
        {
            e.Handled = true;
            Accept((int)VirtualKey.Snapshot);
        }
    }

    void Accept(int vk)
    {
        // Gamepad "keys" are not keyboard keys OptiScaler can watch.
        if (vk is >= 0xC3 and <= 0xDA || vk <= 0 || vk > 0xFE) return;
        _acceptedAt = DateTime.UtcNow;
        Listen(false);
        Choose($"0x{vk:X2}");
    }

    void Choose(string value)
    {
        _value = value;
        Update();
        KeyChosen?.Invoke(value);
    }

    // ---------- names ----------

    /// <summary>"0x2D" / "45" → 45; anything else (auto, -1) → null.</summary>
    public static int? Parse(string? value)
    {
        if (value is null) return null;
        var v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec > 0 ? dec : null;
    }

    /// <summary>The key Auto stands for, from the ini comment ("Default (auto) is 0x2D").</summary>
    public static int? DefaultFromComment(string? comment) =>
        comment is not null && Regex.Match(comment, @"[Dd]efault[^\n]*?0x([0-9A-Fa-f]{1,2})") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;

    /// <summary>Whether an ini setting holds a key shortcut.</summary>
    public static bool IsKeySetting(string key, string? comment) =>
        key.EndsWith("ShortcutKey", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Hotkey", StringComparison.OrdinalIgnoreCase)
        || (comment?.Contains("virtual-key-codes", StringComparison.OrdinalIgnoreCase) == true);

    static readonly Dictionary<int, string> Names = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt", [0x13] = "Pause",
        [0x14] = "Caps Lock", [0x1B] = "Esc", [0x20] = "Space", [0x21] = "Page Up", [0x22] = "Page Down", [0x23] = "End", [0x24] = "Home",
        [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓", [0x2C] = "Print Screen", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x5B] = "Win", [0x5C] = "Win (right)", [0x5D] = "Menu", [0x6A] = "Num *", [0x6B] = "Num +", [0x6D] = "Num -", [0x6E] = "Num .",
        [0x6F] = "Num /", [0x90] = "Num Lock", [0x91] = "Scroll Lock", [0xA0] = "Shift (left)", [0xA1] = "Shift (right)",
        [0xA2] = "Ctrl (left)", [0xA3] = "Ctrl (right)", [0xA4] = "Alt (left)", [0xA5] = "Alt (right)",
        [0x05] = "Mouse 4", [0x06] = "Mouse 5", [0x04] = "Middle mouse",
    };

    /// <summary>The key's name: "Insert", "F12", "Num 5", "A", or the layout's own name for punctuation keys.</summary>
    public static string KeyName(int vk)
    {
        if (Names.TryGetValue(vk, out var n)) return n;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return $"Num {vk - 0x60}";
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        // Punctuation depends on the keyboard layout: ask Windows for its name.
        try
        {
            uint scan = MapVirtualKey((uint)vk, 0);
            if (scan != 0)
            {
                var sb = new StringBuilder(64);
                if (GetKeyNameText((int)(scan << 16), sb, sb.Capacity) > 0) return sb.ToString();
            }
        }
        catch { }
        return $"0x{vk:X2}";
    }

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetKeyNameText(int lParam, StringBuilder buffer, int size);
}
