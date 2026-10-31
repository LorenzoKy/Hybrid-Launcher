using System.Numerics;
using HybridLauncher.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Controls;

/// <summary>
/// The big launch button of the game page. Drawn by itself (no system button chrome): a squared face with a soft two-tone
/// gradient of its colour, a crisp edge (lighter at the top, darker at the bottom), a faint gloss on the top half, a glow of
/// its own colour under it, a light veil and a small lift on hover, a press on click. Its look follows the game: Play,
/// Starting…, Stop (while the game runs), Closing…, Download, Downloading. Every mode is drawn the same way (one solid icon,
/// one word in capitals, Segoe UI Variable Display) and the button is only as wide as its word.
/// </summary>
public sealed partial class PlayButton : UserControl
{
    public enum Mode { Play, Starting, Stop, Stopping, Install, Downloading }

    const double Corner = 4;
    readonly Button _button;
    readonly Border _surface = new() { CornerRadius = new CornerRadius(Corner) };
    // The gloss on the top half (always there) and the hover veil over the whole face (faded on the compositor).
    readonly Border _gloss = new() { CornerRadius = new CornerRadius(Corner, Corner, 0, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    readonly Border _veil = new() { CornerRadius = new CornerRadius(Corner), IsHitTestVisible = false };
    readonly Grid _glowHost = new() { IsHitTestVisible = false };
    readonly Grid _back = new() { IsHitTestVisible = false };
    // While the game starts: a small stop square after the word says a click cancels the start (closes the game for good).
    readonly Microsoft.UI.Xaml.Shapes.Path _cancel = new() { Width = 15, Height = 15, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Opacity = 0.9 };
    // The icons are paths of one weight in a 16 px box (the icon font mixes outline Play with solid Stop).
    readonly Microsoft.UI.Xaml.Shapes.Path _icon = new() { Width = 22, Height = 22, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    readonly ProgressRing _ring = new() { Width = 22, Height = 22, MinWidth = 22, MinHeight = 22, IsActive = false, Visibility = Visibility.Collapsed };
    readonly TextBlock _text = new()
    {
        // The 0.14 face (Windows 11's display font), larger: the button is the page's main action.
        FontSize = 21, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold,
        CharacterSpacing = 60, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight,
    };
    SpriteVisual? _glow;
    DropShadow? _shadow;
    Visual? _visual;
    Mode _mode = (Mode)(-1);
    Color _color;
    bool _over, _down, _busy;

    public event Action? Clicked;

    public Mode Current => _mode;

    static readonly string PlayPath = "M4,2.3 C4,1.5 4.9,1 5.6,1.5 L14.1,7.2 C14.7,7.6 14.7,8.4 14.1,8.8 L5.6,14.5 C4.9,15 4,14.5 4,13.7 Z";
    static readonly string StopPath = "M2.5,3.5 C2.5,2.9 2.9,2.5 3.5,2.5 L12.5,2.5 C13.1,2.5 13.5,2.9 13.5,3.5 L13.5,12.5 C13.5,13.1 13.1,13.5 12.5,13.5 L3.5,13.5 C2.9,13.5 2.5,13.1 2.5,12.5 Z";
    static readonly string DownloadPath = "M6.9,1 H9.1 V8.3 L11.7,5.7 L13.2,7.2 L8,12.4 L2.8,7.2 L4.3,5.7 L6.9,8.3 Z M2,13.6 H14 V15.6 H2 Z";

    public PlayButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_ring);
        content.Children.Add(_icon);
        content.Children.Add(_text);
        content.Children.Add(_cancel);

        // The spring scales the face only: scaling the word with it redrew the text as a stretched bitmap (soft, ringing edges).
        _back.Children.Add(_glowHost);
        _back.Children.Add(_surface);
        _back.Children.Add(_gloss);
        _back.Children.Add(_veil);
        var face = new Grid();
        face.Children.Add(_back);
        // As wide as the word needs: Play is short, Downloading longer; never narrower than a comfortable target.
        face.Children.Add(new Border { Child = content, Padding = new Thickness(32, 0, 34, 0) });

        // A Button for focus, keyboard and screen readers; its template draws nothing but the face above.
        _button = new Button
        {
            Content = face,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Template = (ControlTemplate)XamlReader.Load(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                "<ContentPresenter Content='{TemplateBinding Content}' HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch' Background='Transparent' />" +
                "</ControlTemplate>"),
            UseSystemFocusVisuals = true,
        };
        // Busy states ignore clicks instead of disabling the button: a disabled button drops its keyboard focus, and Windows
        // would hand it to the next control in the bar (the launch arguments box), which then opened by itself.
        _button.Click += (_, _) => { if (!_busy) Clicked?.Invoke(); };
        _button.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => { _over = true; Animate(); }), true);
        _button.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => { _over = false; _down = false; Animate(); }), true);
        _button.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => { _down = true; Animate(); }), true);
        _button.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _down = false; Animate(); }), true);
        _button.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { _down = false; Animate(); }), true);
        Content = _button;
        MinWidth = 168;
        MinHeight = 58;

        SizeChanged += (_, e) =>
        {
            _gloss.Height = e.NewSize.Height / 2;
            if (_visual is not null) _visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        };
        Loaded += (_, _) => SetUpComposition();
        ElementCompositionPreview.GetElementVisual(_veil).Opacity = 0;
        SetMode(Mode.Play);
    }

    /// <summary>The glow: a blurred shadow of a sprite tucked under the face (its corners never show), in the button's colour.</summary>
    void SetUpComposition()
    {
        if (_glow is not null) return;
        try
        {
            _visual = ElementCompositionPreview.GetElementVisual(_back);
            _visual.CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
            var compositor = _visual.Compositor;
            _shadow = compositor.CreateDropShadow();
            _shadow.BlurRadius = 14;
            _shadow.Offset = new Vector3(0, 4, 0);
            _glow = compositor.CreateSpriteVisual();
            _glow.Brush = compositor.CreateColorBrush(_color);
            _glow.RelativeSizeAdjustment = Vector2.One;
            _glow.Size = new Vector2(-16, -12);
            _glow.Offset = new Vector3(8, 6, 0);
            _glow.Shadow = _shadow;
            ElementCompositionPreview.SetElementChildVisual(_glowHost, _glow);
            ApplyGlow();
        }
        catch (Exception ex) { Log.Warn($"Play button glow: {ex.Message}"); }
    }

    public void SetMode(Mode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        // Stop mirrors Play: the Play colour inverted, so the two read as one pair in every theme (a violet Play stops in lime).
        var play = Accent();
        var stop = Color.FromArgb(0xFF, (byte)(255 - play.R), (byte)(255 - play.G), (byte)(255 - play.B));
        var blue = Color.FromArgb(0xFF, 0x2F, 0x8C, 0xF0);
        (string path, string text, Color color, bool busy) = mode switch
        {
            // Starting keeps its ring but takes clicks: one forces the game closed (the stop square after the word says so).
            Mode.Starting => (PlayPath, Labels[0], play, true),
            Mode.Stop => (StopPath, Labels[1], stop, false),
            // A static arrow, not a spinner: the download may be paused or queued, the bar beside it says which.
            Mode.Downloading => (DownloadPath, Labels[2], blue, false),
            Mode.Stopping => (StopPath, Labels[3], Mix(stop, Microsoft.UI.Colors.Black, 0.18), true),
            Mode.Install => (DownloadPath, Labels[4], blue, false),
            _ => (PlayPath, Labels[5], play, false),
        };
        // White on deep colours, dark on bright ones (a yellow or cyan accent): the word always reads.
        var ink = HybridLauncher.Helpers.Contrast.BrushOn(color);
        _icon.Fill = _ring.Foreground = _text.Foreground = ink;
        _color = color;
        _icon.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
        // Each icon as tall as the capitals beside it, so icon and word read as one line (the square looks bigger: a little smaller).
        _icon.Height = _icon.Width = path == StopPath ? 19 : 22;
        _icon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _ring.IsActive = busy;
        _ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _text.Text = text.ToUpper();
        _busy = busy && mode != Mode.Starting;
        _cancel.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), StopPath);
        _cancel.Fill = ink;
        _cancel.Visibility = mode == Mode.Starting ? Visibility.Visible : Visibility.Collapsed;
        var name = mode == Mode.Starting ? L.T("Starting… Click to force the game closed") : text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_button, name);
        ToolTipService.SetToolTip(_button, mode == Mode.Starting ? name : null);
        // The 0.14 two-tone gradient, softer: a little lighter at the top, a little deeper at the bottom.
        _surface.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Mix(color, Microsoft.UI.Colors.White, 0.10), Offset = 0 },
                new GradientStop { Color = color, Offset = 0.5 },
                new GradientStop { Color = Mix(color, Microsoft.UI.Colors.Black, 0.16), Offset = 1 },
            },
        };
        // No rim: a hairline darker at the bottom broke up on the rounded corners (Steam's Play is a plain fill too). The face's
        // own gradient and the gloss give it its edge.
        _surface.BorderThickness = new Thickness(0);
        // Less shine and veil on a light face, where they would wash the dark word out.
        bool light = HybridLauncher.Helpers.Contrast.IsLight(color);
        _gloss.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(light ? (byte)0x0E : (byte)0x1C, 0xFF, 0xFF, 0xFF), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1 },
            },
        };
        _veil.Background = new SolidColorBrush(Color.FromArgb(light ? (byte)0x14 : (byte)0x22, 0xFF, 0xFF, 0xFF));
        if (_glow?.Brush is CompositionColorBrush b) b.Color = color;
        ApplyGlow();
        Pulse(mode == Mode.Stop);
    }

    /// <summary>The words of the modes, in the order of <see cref="SetMode"/>: Starting, Stop, Downloading, Closing, Download, Play.</summary>
    static readonly string[] Labels = [L.T("Starting…"), L.T("Stop"), L.T("Downloading"), L.T("Closing…"), L.T("Download"), L.T("Play")];

    /// <summary>The theme's own accent (not the lightened fill WinUI uses for controls): a deep, saturated face for the main button.</summary>
    static Color Accent() =>
        HybridLauncher.Helpers.ThemeRes.Current.TryGetValue("SystemAccentColor", out var v) && v is Color c ? c
        : (HybridLauncher.Helpers.ThemeRes.Current["AccentFillColorDefaultBrush"] as SolidColorBrush)?.Color ?? Color.FromArgb(0xFF, 0x9A, 0x5C, 0xFF);

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    void ApplyGlow()
    {
        if (_shadow is null) return;
        _shadow.Color = Color.FromArgb(0xFF, _color.R, _color.G, _color.B);
        _shadow.Opacity = _over ? 0.6f : 0.32f;
        _shadow.BlurRadius = _over ? 22 : 14;
    }

    /// <summary>Hover fades the veil in and lifts the face a little, a press sinks it: on the compositor, smooth at any refresh rate.</summary>
    void Animate()
    {
        ApplyGlow();
        var veil = ElementCompositionPreview.GetElementVisual(_veil);
        var fade = veil.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, _down ? 0.5f : _over ? 1f : 0f);
        fade.Duration = TimeSpan.FromMilliseconds(120);
        veil.StartAnimation("Opacity", fade);
        if (_visual is null) return;
        float scale = _down ? 0.97f : _over ? 1.015f : 1f;
        var spring = _visual.Compositor.CreateSpringVector3Animation();
        spring.Target = "Scale";
        spring.FinalValue = new Vector3(scale, scale, 1);
        spring.DampingRatio = 0.7f;
        spring.Period = TimeSpan.FromMilliseconds(45);
        _visual.StartAnimation("Scale", spring);
    }

    /// <summary>While the game runs, the Stop square breathes slowly (on the compositor: no layout, smooth at any refresh rate).</summary>
    void Pulse(bool on)
    {
        var icon = ElementCompositionPreview.GetElementVisual(_icon);
        if (!on)
        {
            icon.StopAnimation("Opacity");
            icon.Opacity = 1;
            return;
        }
        var anim = icon.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 1f);
        anim.InsertKeyFrame(0.5f, 0.35f);
        anim.InsertKeyFrame(1f, 1f);
        anim.Duration = TimeSpan.FromMilliseconds(1600);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        icon.StartAnimation("Opacity", anim);
    }
}
