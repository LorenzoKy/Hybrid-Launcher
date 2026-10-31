using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HybridLauncher.Controls;

/// <summary>
/// The number of an install step: a soft accent disc with an accent ring and the digit in its optical centre.
/// Drawn with ellipses (smooth at any scale) and whole-pixel sizes, so the circle and the digit stay crisp.
/// </summary>
public sealed partial class StepBadge : Grid
{
    readonly TextBlock _text = new()
    {
        FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        TextLineBounds = TextLineBounds.Tight,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

    public StepBadge()
    {
        Width = Height = 28;
        UseLayoutRounding = true;
        VerticalAlignment = VerticalAlignment.Center;
        var res = HybridLauncher.Helpers.ThemeRes.Current;
        Children.Add(new Ellipse { Fill = (Brush)res["AccentFillColorDefaultBrush"], Opacity = 0.22 });
        Children.Add(new Ellipse { Stroke = (Brush)res["AccentFillColorDefaultBrush"], StrokeThickness = 1.5 });
        _text.Foreground = (Brush)res["AccentTextFillColorPrimaryBrush"];
        Children.Add(_text);
    }

    public int Number
    {
        get => int.TryParse(_text.Text, out var n) ? n : 0;
        set => _text.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
