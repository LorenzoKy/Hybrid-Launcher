using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Controls;

/// <summary>
/// A thin vertical strip that resizes something by dragging: it shows the left-right cursor and a faint accent line on hover,
/// and reports the pointer's x (in <see cref="Relative"/>'s space) while dragged.
/// </summary>
public sealed partial class ResizeGrip : Grid
{
    readonly Border _line = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0, CornerRadius = new CornerRadius(1) };
    bool _dragging;

    /// <summary>The element whose coordinates the reported x is in.</summary>
    public UIElement? Relative { get; set; }

    /// <summary>The pointer's x while dragged.</summary>
    public event Action<double>? Dragged;

    /// <summary>The drag ended (save the size then).</summary>
    public event Action? Released;

    public ResizeGrip()
    {
        Width = 8;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        _line.Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["AccentFillColorDefaultBrush"];
        Children.Add(_line);
        PointerEntered += (_, _) => _line.Opacity = 0.6;
        PointerExited += (_, _) => { if (!_dragging) _line.Opacity = 0; };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => End(e);
        PointerCaptureLost += (_, e) => End(e);
    }

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        _line.Opacity = 1;
        e.Handled = true;
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        Dragged?.Invoke(e.GetCurrentPoint(Relative).Position.X);
        e.Handled = true;
    }

    void End(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCaptures();
        _line.Opacity = 0;
        Released?.Invoke();
    }
}
