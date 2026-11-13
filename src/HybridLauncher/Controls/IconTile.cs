using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Controls;

/// <summary>
/// The app's heading and row icon (a tile until 1.4.0-rc.7; since rc.8 the glyph alone, in its colour, with no shape behind it). The one look for the icons of card and section headings,
/// dashboard cards, mod rows and the activity feed, so every tab's icons belong to one family. Usable from code
/// (<c>new IconTile(glyph, colour, size)</c>, or <c>Ui.Badge</c>) and from XAML (<c>Glyph</c>, <c>Tint</c> as a name of the
/// app's tints, "Gold", "Blue"..., or a "#RRGGBB" colour, <c>Size</c>). Colours go through the palette (<see cref="Palettes.Adapt"/>),
/// so a theme that harmonises softens them like everything else.
/// </summary>
public sealed partial class IconTile : Grid
{
    string _glyph = "";
    Color _tint = Color.FromArgb(0xFF, 0x8B, 0x94, 0xA8);
    double _size = 34;

    public IconTile()
    {
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        UseLayoutRounding = true;
        ActualThemeChanged += (_, _) => Draw();
        Draw();
    }

    public IconTile(string glyph, Color tint, double size) : this()
    {
        _glyph = glyph;
        _tint = tint;
        _size = size;
        Draw();
    }

    public string Glyph { get => _glyph; set { _glyph = value; Draw(); } }

    /// <summary>A tint by name ("Gold", "Blue", "Green", "Teal", "Orange", "Red", "Pink", "Slate", "Violet", "Amber", "Cyan") or "#RRGGBB".</summary>
    public string Tint
    {
        get => Palettes.ToHex(_tint);
        set { _tint = Named(value); Draw(); }
    }

    public double Size { get => _size; set { _size = value; Draw(); } }

    static Color Named(string name)
    {
        static Color C(uint v) => Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        var raw = name.Trim().ToLowerInvariant() switch
        {
            "gold" => C(0xF2B82E),
            "blue" => C(0x3D9BFF),
            "green" => C(0x3FC26A),
            "teal" => C(0x22B8B0),
            "orange" => C(0xF07F2A),
            "red" => C(0xE5484D),
            "pink" => C(0xE05CB8),
            "violet" => C(0x9A7CFF),
            "amber" => C(0xF5A524),
            "cyan" => C(0x00C2D8),
            "slate" => C(0x8B94A8),
            _ => Palettes.ParseHex(name) ?? C(0x8B94A8),
        };
        return Palettes.Adapt(raw);
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    void Draw()
    {
        Children.Clear();
        Width = Height = _size;
        bool light = ActualTheme == ElementTheme.Light;
        // 1.4.0-rc.8: no tile at all. The wash behind the glyph (rc.7) still read as a bubble set on the card; now the glyph
        // alone carries the colour, drawn larger in the same box (so headings and rows keep their alignment), lifted a little
        // in dark mode and deepened in light mode so it reads on any surface. It is part of the line it sits on, like the
        // glyphs of the tab bar.
        var icon = Icons.Make(_glyph, Math.Round(_size * 0.68));
        icon.Foreground = new SolidColorBrush(light ? Mix(_tint, Microsoft.UI.Colors.Black, 0.28) : Mix(_tint, Microsoft.UI.Colors.White, 0.18));
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(icon);
    }
}
