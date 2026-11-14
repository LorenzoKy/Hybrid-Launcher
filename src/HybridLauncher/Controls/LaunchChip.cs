using System.Numerics;
using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Controls;

/// <summary>
/// The Library's launch button: the covers' and the Last played banner's. Since 2.1-rc.3 one raised pill with its word in
/// every place (the round icon-only button on the covers looked out of place), in the game page's Play colours, following
/// the game through all its states:
///  • Play: the accent, a triangle and "Play";
///  • Starting / Closing: a turning ring in place of the icon, faded a little;
///  • Running: "Playing" in the Stop colour (the accent inverted, as on the game page) with a breathing dot; under the
///    pointer it reads "Stop", and a click asks before closing the game;
///  • Updating or Installing (a download of the game going on): blue, the word with its percentage and the progress filling
///    the pill from the left; a click opens Downloads.
/// Since 2.1-rc.4 the face is flat (the user's call: the glossy, shadowed pill looked like a 2015 phone button, and its
/// shadow sat crooked on the Last played banner): one solid colour, soft corners, a faint light line along its top edge and
/// no shadow. The pointer lightens it, a press dips it a little.
/// </summary>
public sealed partial class LaunchChip : UserControl
{
    enum Mode { Play, Starting, Running, Stopping, Updating, Installing, Install }

    // A game owned but not on this PC (Downloads, the library's Not installed): the pill reads Install and starts it (2.1).
    readonly Action? _install;

    readonly Game _game;
    readonly Border _face;
    readonly Border _fill;
    readonly Microsoft.UI.Xaml.Shapes.Path _icon;
    readonly ProgressRing _ring;
    readonly Microsoft.UI.Xaml.Shapes.Ellipse _dot;
    readonly TextBlock _text;
    readonly Button _button;
    readonly double _iconSize;
    Mode _mode = (Mode)(-1);
    string _word = "";
    bool _over, _down, _subscribed;

    /// <param name="withLabel">Kept for the callers: the pill always carries its word now.</param>
    /// <param name="size">The pill's height (the covers pass theirs; the banner's is 42).</param>
    /// <param name="install">For a game owned but not installed: the pill reads Install and a click runs this.</param>
    public LaunchChip(Game game, bool withLabel = false, double size = 42, Action? install = null)
    {
        _game = game;
        _install = install;
        double height = Math.Round(size);
        double font = Math.Round(Math.Clamp(height * 0.36, 12, 15));
        _iconSize = Math.Round(font * 0.95);
        // Soft corners, not a capsule: 10 px on the banner's 42, a little less on the covers' smaller chips.
        var radius = new CornerRadius(Math.Round(Math.Min(10, height * 0.26)));

        _icon = new Microsoft.UI.Xaml.Shapes.Path { Width = _iconSize, Height = _iconSize, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        _ring = new ProgressRing { Width = _iconSize + 2, Height = _iconSize + 2, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        _dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
        _text = new TextBlock { FontSize = font, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight, CharacterSpacing = 10 };
        var glyph = new Grid { VerticalAlignment = VerticalAlignment.Center };
        glyph.Children.Add(_icon);
        glyph.Children.Add(_ring);
        glyph.Children.Add(_dot);
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = Math.Round(font * 0.55), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Math.Round(height * 0.42), 0, Math.Round(height * 0.5), 0),
        };
        content.Children.Add(glyph);
        content.Children.Add(_text);

        // Inside the face, back to front: the download's progress, then the icon and the word.
        _fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = radius, Width = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var inner = new Grid();
        inner.Children.Add(_fill);
        inner.Children.Add(content);
        // A faint light line along the top edge only (it fades out by mid-height): depth without a gloss.
        var rim = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        rim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Offset = 0 });
        rim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 0.45 });
        _face = new Border
        {
            CornerRadius = radius, Child = inner, Height = height, MinWidth = Math.Round(height * 2.6), BorderBrush = rim, BorderThickness = new Thickness(1),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) },
        };
        _face.SizeChanged += (_, e) =>
        {
            _face.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            ShowProgress();
        };

        var root = new Grid();
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
        _button.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => { _over = true; Spring(); Words(); }), true);
        _button.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => { _over = _down = false; Spring(); Words(); }), true);
        _button.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => { _down = true; Spring(); }), true);
        _button.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _down = false; Spring(); }), true);
        _button.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { _down = false; Spring(); }), true);
        Content = _button;

        // Painted from the theme's accent: a new theme or palette paints it again. The game's sessions and downloads move it.
        Action applied = () => DispatcherQueue.TryEnqueue(Repaint);
        Loaded += (_, _) =>
        {
            if (!_subscribed)
            {
                GameSessions.Changed += OnSession;
                Downloads.Changed += OnDownloads;
                HybridLauncher.Helpers.ThemeRes.Applied += applied;
                _subscribed = true;
            }
            Repaint();
        };
        Unloaded += (_, _) =>
        {
            if (!_subscribed) return;
            GameSessions.Changed -= OnSession;
            Downloads.Changed -= OnDownloads;
            HybridLauncher.Helpers.ThemeRes.Applied -= applied;
            _subscribed = false;
            _ring.IsActive = false;
        };
        ActualThemeChanged += (_, _) => Repaint();
        Update();
    }

    void Repaint()
    {
        _mode = (Mode)(-1);
        Update();
    }

    void OnSession(string id)
    {
        if (id.Equals(_game.Id, StringComparison.OrdinalIgnoreCase)) Update();
    }

    // Raised on the UI thread; a download of another game changes nothing here.
    void OnDownloads()
    {
        if (_mode is Mode.Updating or Mode.Installing || Downloads.Active(_game.Id) is not null) Update();
    }

    Mode Current()
    {
        var state = GameSessions.State(_game.Id);
        if (state != GameRunState.Stopped)
            return state switch { GameRunState.Starting => Mode.Starting, GameRunState.Running => Mode.Running, _ => Mode.Stopping };
        if (Downloads.Active(_game.Id) is { } job && job.State is DownloadState.Downloading or DownloadState.Waiting or DownloadState.Paused)
            return _game.Installing ? Mode.Installing : Mode.Updating;
        if (_install is not null && _game.NotInstalled) return Mode.Install;
        return _game.Installing ? Mode.Installing : Mode.Play;
    }

    Color _color;

    void Update()
    {
        var mode = Current();
        if (mode != _mode)
        {
            _mode = mode;
            var play = PlayButton.Accent();
            var stop = Color.FromArgb(0xFF, (byte)(255 - play.R), (byte)(255 - play.G), (byte)(255 - play.B));
            var blue = Color.FromArgb(0xFF, 0x2F, 0x8C, 0xF0);
            _color = mode switch
            {
                Mode.Running => stop,
                Mode.Stopping => PlayButton.Mix(stop, Microsoft.UI.Colors.Black, 0.18),
                Mode.Updating or Mode.Installing => PlayButton.Mix(blue, Microsoft.UI.Colors.Black, 0.35),
                _ => play,
            };
            Paint();
            // The download's progress: the same blue, bright, under the gloss.
            _fill.Background = new SolidColorBrush(blue);
            _fill.Visibility = mode is Mode.Updating or Mode.Installing ? Visibility.Visible : Visibility.Collapsed;

            var ink = HybridLauncher.Helpers.Contrast.BrushOn(mode is Mode.Updating or Mode.Installing ? blue : _color);
            _icon.Fill = ink;
            _text.Foreground = ink;
            _dot.Fill = ink;
            _ring.Foreground = ink;
            bool busy = mode is Mode.Starting or Mode.Stopping;
            _ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _ring.IsActive = busy && IsLoaded;
            _face.Opacity = busy ? 0.82 : 1;
            Breathe(mode == Mode.Running);
        }
        Words();
        ShowProgress();
    }

    /// <summary>The icon and the word for the mode (Running reads "Stop" under the pointer).</summary>
    void Words()
    {
        bool stopHint = _mode == Mode.Running && (_over || _button.FocusState == FocusState.Keyboard);
        string path = _mode switch
        {
            Mode.Running or Mode.Stopping => PlayButton.StopPath,
            Mode.Updating or Mode.Installing or Mode.Install => DownloadPath,
            _ => PlayButton.PlayPath,
        };
        var job = _mode is Mode.Updating or Mode.Installing ? Downloads.Active(_game.Id) : null;
        string pct = job is { Total: > 0 } ? $" {job.Fraction:P0}" : "";
        string word = _mode switch
        {
            Mode.Starting => L.T("Starting…"),
            Mode.Running => stopHint ? L.T("Stop") : L.T("Playing"),
            Mode.Stopping => L.T("Closing…"),
            Mode.Updating => (job?.State == DownloadState.Paused ? L.T("Paused") : L.T("Updating")) + pct,
            Mode.Installing => (job?.State == DownloadState.Paused ? L.T("Paused") : L.T("Installing")) + pct,
            Mode.Install => L.T("Install"),
            _ => L.T("Play"),
        };
        bool busy = _mode is Mode.Starting or Mode.Stopping;
        bool dot = _mode == Mode.Running && !stopHint;
        _icon.Visibility = busy || dot ? Visibility.Collapsed : Visibility.Visible;
        _dot.Visibility = dot ? Visibility.Visible : Visibility.Collapsed;
        if (_icon.Data is null || path != _path)
        {
            _path = path;
            _icon.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
        }
        // The square reads bigger than the triangle: a little smaller, as on the game page. The triangle sits on its centre
        // of mass, a sixth of its width right of its box's centre.
        _icon.Width = _icon.Height = path == PlayButton.StopPath ? Math.Round(_iconSize * 0.84) : _iconSize;
        _icon.Margin = new Thickness(path == PlayButton.PlayPath ? Math.Round(_iconSize * 0.12) : 0, 0, 0, 0);
        if (word != _word)
        {
            _word = word;
            _text.Text = word;
        }
        string tip = _mode switch
        {
            Mode.Running => L.F("{0} is running: click to close it", _game.Title),
            Mode.Updating or Mode.Installing => L.T("Open Downloads"),
            Mode.Starting => L.T("Starting… (click to stop it)"),
            _ => $"{word}: {_game.Title}",
        };
        ToolTipService.SetToolTip(_button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_button, $"{word}: {_game.Title}");
    }

    string? _path;
    static readonly string DownloadPath = "M8,1.5 C8.4,1.5 8.75,1.85 8.75,2.25 L8.75,9.2 L11,6.95 C11.3,6.65 11.75,6.65 12.05,6.95 C12.35,7.25 12.35,7.7 12.05,8 L8.53,11.53 C8.24,11.82 7.76,11.82 7.47,11.53 L3.95,8 C3.65,7.7 3.65,7.25 3.95,6.95 C4.25,6.65 4.7,6.65 5,6.95 L7.25,9.2 L7.25,2.25 C7.25,1.85 7.6,1.5 8,1.5 Z M2.75,13 L13.25,13 C13.65,13 14,13.35 14,13.75 C14,14.15 13.65,14.5 13.25,14.5 L2.75,14.5 C2.35,14.5 2,14.15 2,13.75 C2,13.35 2.35,13 2.75,13 Z";

    void ShowProgress()
    {
        if (_mode is not (Mode.Updating or Mode.Installing)) return;
        double share = Downloads.Active(_game.Id) is { } j ? j.Fraction : 0;
        _fill.Width = Math.Max(0, _face.ActualWidth - 2) * share;
    }

    /// <summary>"Playing": the dot breathes, on the compositor.</summary>
    void Breathe(bool on)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_dot);
            if (!on) { visual.StopAnimation("Opacity"); visual.Opacity = 1; return; }
            var c = visual.Compositor;
            var a = c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0, 1);
            a.InsertKeyFrame(0.5f, 0.35f);
            a.InsertKeyFrame(1, 1);
            a.Duration = TimeSpan.FromMilliseconds(1600);
            a.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", a);
        }
        catch { }
    }

    /// <summary>The flat face in its colour: lighter under the pointer, a touch darker while pressed.</summary>
    void Paint()
    {
        var c = _down ? PlayButton.Mix(_color, Microsoft.UI.Colors.Black, 0.1)
            : _over ? PlayButton.Mix(_color, Microsoft.UI.Colors.White, 0.14)
            : _color;
        if (_face.Background is SolidColorBrush b && b.Color == c) return;
        _face.Background = new SolidColorBrush(c);
    }

    void Spring()
    {
        Paint();
        try
        {
            // Only a press moves it: a short, firm dip (no bounce, no growth under the pointer).
            var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
            var spring = compositor.CreateSpringVector3Animation();
            spring.Target = "Scale";
            float s = _down ? 0.97f : 1f;
            spring.FinalValue = new Vector3(s, s, 1);
            spring.DampingRatio = 0.9f;
            spring.Period = TimeSpan.FromMilliseconds(35);
            _face.StartAnimation(spring);
        }
        catch { }
    }

    async void OnClick()
    {
        try
        {
            switch (_mode)
            {
                case Mode.Stopping:
                    return;
                case Mode.Updating or Mode.Installing:
                    App.Window?.OpenDownloads();
                    return;
                case Mode.Install:
                    _install?.Invoke();
                    return;
                case Mode.Running or Mode.Starting:
                    // As on the game page: a game still starting closes at once, a running one asks first (unsaved progress).
                    if (_mode == Mode.Running)
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
                        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
                    }
                    if (!await GameSessions.StopAsync(_game.Id))
                        App.Window?.ShowError(L.F("{0} is still running", _game.Title), L.T("Windows did not let Hybrid Launcher close it (it may run as administrator). Close it from the game or Task Manager."));
                    return;
                default:
                    Views.LibraryPage.Launch(_game);
                    return;
            }
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Action failed"), ex.Message, ex); }
    }
}
