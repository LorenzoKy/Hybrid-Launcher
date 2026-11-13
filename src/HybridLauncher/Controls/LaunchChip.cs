using System.Numerics;
using HybridLauncher.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Controls;

/// <summary>
/// The Library's launch button (1.4.0-rc.20): a small version of the game page's <see cref="PlayButton"/>, in its colours and
/// with its icons. It follows the game: Play, then Stop while the game runs (the Play colour inverted, as on the game page),
/// faded while it starts or closes. Round and icon-only on the covers and rows, a pill with its word on the Last played banner.
/// A press springs it down, the pointer springs it up a little.
/// Since 2.0 it is drawn as a raised button: a face lit from above with a gloss on its upper half, a bright rim at the top
/// and a darker one at the foot, a soft shadow under it, and a glow of its colour that comes up under the pointer. The
/// triangle sits on its centre of mass, not its box, so it looks centred in the circle. The pill with its word (Last played
/// banner) keeps the flat face it had before.
/// </summary>
public sealed partial class LaunchChip : UserControl
{
    readonly Game _game;
    readonly Border _face;
    readonly Border _gloss;
    readonly Microsoft.UI.Xaml.Shapes.Ellipse? _glow;
    readonly bool _round;
    readonly Microsoft.UI.Xaml.Shapes.Path _icon;
    readonly TextBlock? _text;
    readonly Button _button;
    readonly double _iconSize;
    GameRunState _state = (GameRunState)(-1);
    bool _over, _down, _subscribed;

    public LaunchChip(Game game, bool withLabel = false, double size = 34)
    {
        _game = game;
        _round = !withLabel;
        _iconSize = withLabel ? 14 : Math.Round(size * 0.38);
        _icon = new Microsoft.UI.Xaml.Shapes.Path { Width = _iconSize, Height = _iconSize, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_icon);
        if (withLabel)
        {
            _text = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
            content.Children.Add(_text);
        }
        var radius = new CornerRadius(withLabel ? 10 : size / 2);
        // The gloss: light falling on the upper half of the face.
        var glossBrush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        glossBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Offset = 0 });
        glossBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), Offset = 0.5 });
        glossBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 0.56 });
        // The Last played banner's pill stays flat, as before 2.0 (the gloss sat inside its padding and read as a box on it).
        _gloss = new Border { CornerRadius = radius, Background = glossBrush, IsHitTestVisible = false, Visibility = withLabel ? Visibility.Collapsed : Visibility.Visible };
        var inner = new Grid();
        inner.Children.Add(_gloss);
        inner.Children.Add(content);
        // The rim: bright where the light hits, dark at the foot.
        var rim = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        rim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), Offset = 0 });
        rim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Offset = 0.5 });
        rim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x59, 0x00, 0x00, 0x00), Offset = 1 });
        _face = new Border
        {
            CornerRadius = radius,
            Child = inner,
            Height = withLabel ? 42 : size,
            MinWidth = withLabel ? 116 : size,
            Padding = withLabel ? new Thickness(20, 0, 24, 0) : new Thickness(0),
            BorderBrush = rim,
            BorderThickness = new Thickness(withLabel ? 0 : 1),
        };
        if (!withLabel)
        {
            _face.Width = size;
            // The play triangle's centre of mass is a third of the way in from its flat side: moved right by that much.
            _icon.Margin = new Thickness(Math.Round(_iconSize * 0.16), 0, 0, 0);
        }
        // Under the face: a soft shadow below it, and the glow of its colour that the pointer brings up.
        var root = new Grid();
        if (!withLabel)
        {
            var shadowBrush = new RadialGradientBrush();
            shadowBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x99, 0, 0, 0), Offset = 0.55 });
            shadowBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0, 0, 0), Offset = 1 });
            root.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = size * 1.3, Height = size * 1.3, Fill = shadowBrush, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-size, -size + size * 0.16, -size, -size - size * 0.16),
            });
            _glow = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = size * 1.9, Height = size * 1.9, IsHitTestVisible = false, Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-size), OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) },
            };
            root.Children.Add(_glow);
        }
        root.Children.Add(_face);
        // A Button for focus, keyboard and screen readers; its template draws only the face.
        _button = new Button
        {
            Content = root,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Template = (ControlTemplate)XamlReader.Load(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                "<ContentPresenter Content='{TemplateBinding Content}' Background='Transparent' />" +
                "</ControlTemplate>"),
            UseSystemFocusVisuals = true,
        };
        _button.Click += (_, _) => OnClick();
        _button.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => { _over = true; Spring(); }), true);
        _button.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => { _over = _down = false; Spring(); }), true);
        _button.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => { _down = true; Spring(); }), true);
        _button.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _down = false; Spring(); }), true);
        _button.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { _down = false; Spring(); }), true);
        Content = _button;
        _face.SizeChanged += (_, e) => _face.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        // Painted from the theme's accent: a new theme or palette paints it again.
        Action applied = () => DispatcherQueue.TryEnqueue(Repaint);
        Loaded += (_, _) =>
        {
            if (!_subscribed) { GameSessions.Changed += OnSession; HybridLauncher.Helpers.ThemeRes.Applied += applied; _subscribed = true; }
            Repaint();
        };
        Unloaded += (_, _) => { if (_subscribed) { GameSessions.Changed -= OnSession; HybridLauncher.Helpers.ThemeRes.Applied -= applied; _subscribed = false; } };
        ActualThemeChanged += (_, _) => Repaint();
        Update();
    }

    void Repaint()
    {
        _state = (GameRunState)(-1);
        Update();
    }

    void OnSession(string id)
    {
        if (id.Equals(_game.Id, StringComparison.OrdinalIgnoreCase)) Update();
    }

    void Update()
    {
        var state = GameSessions.State(_game.Id);
        if (state == _state) return;
        _state = state;
        var play = PlayButton.Accent();
        var stop = Color.FromArgb(0xFF, (byte)(255 - play.R), (byte)(255 - play.G), (byte)(255 - play.B));
        bool stopping = state is GameRunState.Running or GameRunState.Stopping;
        var color = state == GameRunState.Stopping ? PlayButton.Mix(stop, Microsoft.UI.Colors.Black, 0.18) : stopping ? stop : play;
        // The game page's face in small: a soft two-tone gradient of the colour, deeper at the foot.
        var face = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        face.GradientStops.Add(new GradientStop { Color = PlayButton.Mix(color, Microsoft.UI.Colors.White, 0.08), Offset = 0 });
        face.GradientStops.Add(new GradientStop { Color = PlayButton.Deepen(color), Offset = 1 });
        _face.Background = face;
        if (_glow is not null)
        {
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xB3, color.R, color.G, color.B), Offset = 0.3 });
            glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, color.R, color.G, color.B), Offset = 1 });
            _glow.Fill = glow;
        }
        var ink = HybridLauncher.Helpers.Contrast.BrushOn(color);
        _icon.Fill = ink;
        string path = stopping ? PlayButton.StopPath : PlayButton.PlayPath;
        _icon.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
        // The square reads bigger than the triangle: a little smaller, as on the game page.
        _icon.Width = _icon.Height = stopping ? Math.Round(_iconSize * 0.86) : _iconSize;
        if (_round) _icon.Margin = new Thickness(stopping ? 0 : Math.Round(_iconSize * 0.16), 0, 0, 0);
        _face.Opacity = state is GameRunState.Starting or GameRunState.Stopping ? 0.75 : 1;
        string word = state switch
        {
            GameRunState.Starting => L.T("Starting…"),
            GameRunState.Running => L.T("Stop"),
            GameRunState.Stopping => L.T("Closing…"),
            _ => L.T("Play"),
        };
        if (_text is not null) { _text.Text = word; _text.Foreground = ink; }
        else ToolTipService.SetToolTip(_button, word);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_button, $"{word}: {_game.Title}");
    }

    void Spring()
    {
        try
        {
            var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
            var spring = compositor.CreateSpringVector3Animation();
            spring.Target = "Scale";
            float s = _down ? 0.92f : _over ? 1.08f : 1f;
            spring.FinalValue = new Vector3(s, s, 1);
            spring.DampingRatio = 0.55f;
            spring.Period = TimeSpan.FromMilliseconds(45);
            _face.StartAnimation(spring);
            // Lit up under the pointer, pressed in on a press: the glow and the gloss follow.
            if (_glow is not null) _glow.Opacity = _down ? 0.35 : _over ? 0.85 : 0;
            _gloss.Opacity = _down ? 0.35 : 1;
        }
        catch { }
    }

    async void OnClick()
    {
        try
        {
            var state = GameSessions.State(_game.Id);
            if (state == GameRunState.Stopping) return;
            if (state is GameRunState.Running or GameRunState.Starting)
            {
                // As on the game page: a game still starting closes at once, a running one asks first (unsaved progress).
                if (state == GameRunState.Running)
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = L.F("Close {0}?", _game.Title),
                        Content = new TextBlock { Text = L.T("Hybrid Launcher asks the game to close, as if you closed its window, and ends it after a few seconds if it does not. Progress that is not saved is lost."), TextWrapping = TextWrapping.Wrap },
                        PrimaryButtonText = L.T("Close the game"),
                        CloseButtonText = L.T("Cancel"),
                        DefaultButton = ContentDialogButton.Close,
                        RequestedTheme = ActualTheme,
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                }
                if (!await GameSessions.StopAsync(_game.Id))
                    App.Window?.ShowError(L.F("{0} is still running", _game.Title), L.T("Windows did not let Hybrid Launcher close it (it may run as administrator). Close it from the game or Task Manager."));
                return;
            }
            Views.LibraryPage.Launch(_game);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
    }
}
