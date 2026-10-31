using HybridLauncher.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Playback;
using Windows.UI;

namespace HybridLauncher.Controls;

/// <summary>
/// The overview's media viewer: a screenshot, or a trailer with Hybrid Launcher's own small controls in the style of a game launcher
/// (play / pause, a thin timeline, mute and volume, time, full screen). No zoom or cast buttons. The controls show while the
/// pointer moves over the video and fade out after a moment while it plays; a click on the video plays or pauses it.
/// Full screen moves the player over the whole window and puts the window in full-screen mode; Esc or the button leaves it.
/// </summary>
public sealed partial class TrailerPlayer : UserControl
{
    readonly Grid _host = new();
    readonly Grid _surface = new() { Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x05, 0x05, 0x08)) };
    readonly Image _picture = new() { Stretch = Stretch.Uniform };
    readonly MediaPlayerElement _video = new() { AreTransportControlsEnabled = false, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    readonly Grid _controls = new() { VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0, Visibility = Visibility.Collapsed };
    readonly Slider _seek = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.001, IsThumbToolTipEnabled = false, Margin = new Thickness(12, 0, 12, -6) };
    readonly Button _play, _mute, _full;
    readonly Slider _volume = new() { Width = 90, Minimum = 0, Maximum = 1, StepFrequency = 0.01, VerticalAlignment = VerticalAlignment.Center, IsThumbToolTipEnabled = false };
    readonly TextBlock _time = new() { Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _bigPlay;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(2.4) };
    MediaPlayer? _player;
    bool _seeking, _updatingSeek, _full_;
    Microsoft.UI.Xaml.Controls.Primitives.Popup? _popup;
    int _gen;
    bool _wasMaximized;

    /// <summary>Trailers stay muted until the sound is turned on once; then they keep it for the session.</summary>
    static bool _sound;
    static double _level = 0.6;

    /// <summary>Raised when a trailer cannot be played (the caller decides what to tell the user).</summary>
    public event Action<string>? Failed;

    public bool IsPlaying => _player is not null;

    public TrailerPlayer()
    {
        _play = Glyph("", L.T("Play / pause (Space)"), TogglePlay);
        _mute = Glyph("", L.T("Sound on / off"), ToggleMute);
        _full = Glyph("", L.T("Full screen"), () => SetFull(!_full_));
        _volume.Value = _level;
        _volume.ValueChanged += (_, e) =>
        {
            _level = e.NewValue;
            if (_player is null) return;
            _player.Volume = _level;
            if (_level > 0 && _player.IsMuted) { _player.IsMuted = false; _sound = true; }
            UpdateMute();
        };

        var row = new Grid { ColumnSpacing = 4, Margin = new Thickness(8, 0, 8, 6) };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Put(FrameworkElement e, int col) { Grid.SetColumn(e, col); row.Children.Add(e); }
        Put(_play, 0);
        Put(_mute, 1);
        Put(_volume, 2);
        _time.Margin = new Thickness(10, 0, 0, 0);
        Put(_time, 3);
        Put(_full, 5);

        var stack = new StackPanel();
        stack.Children.Add(_seek);
        stack.Children.Add(row);
        _controls.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0x00, 0, 0, 0), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0xB8, 0, 0, 0), Offset = 1 },
            },
        };
        _controls.Padding = new Thickness(0, 36, 0, 0);
        _controls.Children.Add(stack);
        _controls.RequestedTheme = ElementTheme.Dark;

        _seek.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _seeking = true), true);
        _seek.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _seeking = false; SeekTo(_seek.Value); }), true);
        _seek.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { if (_seeking) { _seeking = false; SeekTo(_seek.Value); } }), true);
        _seek.ValueChanged += (_, _) => { if (!_updatingSeek && !_seeking) SeekTo(_seek.Value); };

        // A big round play mark while paused.
        _bigPlay = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(36), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Background = new HybridLauncher.Helpers.GlassBrush(Color.FromArgb(0x80, 0x10, 0x10, 0x14), 20),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = "", FontSize = 28, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(4, 0, 0, 0) },
        };

        _surface.Children.Add(_picture);
        _surface.Children.Add(_video);
        _surface.Children.Add(_bigPlay);
        _surface.Children.Add(_controls);
        _surface.PointerMoved += (_, _) => ShowControls();
        _surface.PointerExited += (_, _) => { if (_player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) HideControls(); };
        _surface.Tapped += (_, e) =>
        {
            // A tap on the picture of a playing video plays or pauses it; the controls take their own taps.
            if (_player is null || e.OriginalSource is DependencyObject d && IsInside(d, _controls)) return;
            TogglePlay();
        };
        _surface.DoubleTapped += (_, e) => { if (_player is not null && !(e.OriginalSource is DependencyObject d && IsInside(d, _controls))) SetFull(!_full_); };
        _surface.KeyDown += OnKey;
        _surface.IsTabStop = true;
        _host.Children.Add(_surface);
        _host.CornerRadius = new CornerRadius(10);
        Content = _host;

        _tick.Tick += (_, _) => UpdateTime();
        _hide.Tick += (_, _) => { _hide.Stop(); HideControls(); };
        Loaded += (_, _) => Helpers.MediaPolicy.Changed += OnPolicyChanged;
        Unloaded += (_, _) =>
        {
            Helpers.MediaPolicy.Changed -= OnPolicyChanged;
            Stop();
        };
    }

    /// <summary>Paused by Hybrid Launcher (a game started, the window was hidden, effects turned off), not by the user: it resumes by itself.</summary>
    bool _autoPaused;

    void OnPolicyChanged()
    {
        if (_player is null) return;
        if (Helpers.MediaPolicy.Suspended || !Helpers.MediaPolicy.Effects)
        {
            if (_player.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Buffering or MediaPlaybackState.Opening)
            {
                _player.Pause();
                _autoPaused = true;
            }
        }
        else if (_autoPaused && Helpers.MediaPolicy.MayAutoplay)
        {
            _autoPaused = false;
            _player.Play();
        }
    }

    static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var p = d; p is not null; p = VisualTreeHelper.GetParent(p)) if (p == parent) return true;
        return false;
    }

    Button Glyph(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Padding = new Thickness(10, 8, 10, 8),
            MinWidth = 0,
            CornerRadius = new CornerRadius(6),
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => { try { click(); } catch (Exception ex) { Log.Warn($"Trailer control: {ex.Message}"); } };
        return b;
    }

    /// <summary>Shows a picture (a screenshot); a playing trailer stops.</summary>
    public void ShowPicture(Uri uri, int decodeWidth)
    {
        Stop();
        _picture.Source = new BitmapImage { UriSource = uri, DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Physical };
        _picture.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Plays a trailer (HLS or a plain file), muted unless sound was chosen. Started by the page (<paramref name="user"/> false)
    /// it waits paused while the user keeps videos paused, a game runs or hardware acceleration is off; picked by the user it plays.
    /// </summary>
    public async Task PlayAsync(Uri uri, bool hls, bool user = false)
    {
        Stop();
        if (user) Helpers.MediaPolicy.SetUserPaused(false);
        bool start = user ? !Helpers.MediaPolicy.Suspended : Helpers.MediaPolicy.MayAutoplay;
        // Held back only by Hybrid Launcher (not by the user's pause): it starts by itself once that is over.
        _autoPaused = !start && !Helpers.MediaPolicy.UserPaused;
        if (!start) Log.Info($"Trailer held paused: {Helpers.MediaPolicy.Why}");
        int gen = _gen;
        Windows.Media.Core.MediaSource source;
        if (hls)
        {
            var adaptive = await Windows.Media.Streaming.Adaptive.AdaptiveMediaSource.CreateFromUriAsync(uri);
            if (gen != _gen) return;
            source = adaptive.Status == Windows.Media.Streaming.Adaptive.AdaptiveMediaSourceCreationStatus.Success
                ? Windows.Media.Core.MediaSource.CreateFromAdaptiveMediaSource(adaptive.MediaSource)
                : Windows.Media.Core.MediaSource.CreateFromUri(uri);
        }
        else source = Windows.Media.Core.MediaSource.CreateFromUri(uri);

        var player = new MediaPlayer { Source = source, AutoPlay = start, Volume = _level, IsMuted = !_sound };
        player.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _gen) return;
            Stop();
            Failed?.Invoke($"{e.Error} {e.ErrorMessage}");
        });
        player.PlaybackSession.PlaybackStateChanged += (s, _) => DispatcherQueue.TryEnqueue(UpdatePlayState);
        _player = player;
        _video.SetMediaPlayer(player);
        _video.Visibility = Visibility.Visible;
        _controls.Visibility = Visibility.Visible;
        UpdateMute();
        _tick.Start();
        ShowControls();
    }

    /// <summary>Stops a trailer (another picture, another tab or page): its sound must not keep going.</summary>
    public void Stop()
    {
        _gen++;
        _tick.Stop();
        _hide.Stop();
        if (_full_) SetFull(false);
        if (_player is not null)
        {
            try
            {
                var p = _player;
                _video.SetMediaPlayer(null);
                p.Pause();
                p.Dispose();
            }
            catch (Exception ex) { Log.Warn($"Trailer stop: {ex.Message}"); }
            _player = null;
        }
        _video.Visibility = Visibility.Collapsed;
        _controls.Visibility = Visibility.Collapsed;
        _bigPlay.Visibility = Visibility.Collapsed;
    }

    void TogglePlay()
    {
        if (_player is null) return;
        // The user's choice holds for every video of the session, like the trailer's sound.
        _autoPaused = false;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            Helpers.MediaPolicy.SetUserPaused(true);
        }
        else
        {
            Helpers.MediaPolicy.SetUserPaused(false);
            _player.Play();
        }
        ShowControls();
    }

    void ToggleMute()
    {
        if (_player is null) return;
        _player.IsMuted = !_player.IsMuted;
        _sound = !_player.IsMuted;
        if (_sound && _player.Volume <= 0.01) _volume.Value = _player.Volume = 0.6;
        UpdateMute();
    }

    void UpdateMute()
    {
        bool muted = _player?.IsMuted ?? !_sound;
        ((FontIcon)_mute.Content).Glyph = muted ? "" : _level < 0.34 ? "" : _level < 0.67 ? "" : "";
    }

    void UpdatePlayState()
    {
        if (_player is null) return;
        bool playing = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        ((FontIcon)_play.Content).Glyph = playing ? "" : "";
        _bigPlay.Visibility = !playing && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused ? Visibility.Visible : Visibility.Collapsed;
        if (playing) ShowControls();
        else { _hide.Stop(); FadeControls(1); }
    }

    void UpdateTime()
    {
        if (_player is null) return;
        var s = _player.PlaybackSession;
        var total = s.NaturalDuration;
        if (!_seeking && total.TotalSeconds > 0)
        {
            _updatingSeek = true;
            _seek.Value = s.Position.TotalSeconds / total.TotalSeconds;
            _updatingSeek = false;
        }
        _time.Text = $"{Format(s.Position)} / {Format(total)}";
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void SeekTo(double fraction)
    {
        if (_player is null) return;
        var total = _player.PlaybackSession.NaturalDuration;
        if (total.TotalSeconds > 0) _player.PlaybackSession.Position = TimeSpan.FromSeconds(total.TotalSeconds * Math.Clamp(fraction, 0, 1));
    }

    void ShowControls()
    {
        if (_player is null) return;
        FadeControls(1);
        _hide.Stop();
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _hide.Start();
    }

    void HideControls()
    {
        if (_seeking || _player?.PlaybackSession.PlaybackState != MediaPlaybackState.Playing) return;
        FadeControls(0);
    }

    double _controlsTarget = -1;

    void FadeControls(double to)
    {
        if (Math.Abs(_controlsTarget - to) < 0.01) return;
        _controlsTarget = to;
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(to > 0 ? 140 : 380), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(a, _controls);
        Storyboard.SetTargetProperty(a, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space: TogglePlay(); e.Handled = true; break;
            case Windows.System.VirtualKey.Escape when _full_: SetFull(false); e.Handled = true; break;
            case Windows.System.VirtualKey.F: SetFull(!_full_); e.Handled = true; break;
            case Windows.System.VirtualKey.M: ToggleMute(); e.Handled = true; break;
        }
    }

    /// <summary>Full screen: the player moves into a layer over the whole window, and the window goes full screen.</summary>
    void SetFull(bool on)
    {
        if (on == _full_ || XamlRoot is null && on) return;
        _full_ = on;
        var window = App.Window;
        try
        {
            if (on)
            {
                _host.Children.Remove(_surface);
                _popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup { XamlRoot = XamlRoot, Child = _surface };
                void Size() { _surface.Width = XamlRoot!.Size.Width; _surface.Height = XamlRoot.Size.Height; }
                Size();
                XamlRoot!.Changed += OnRootChanged;
                _popup.IsOpen = true;
                _wasMaximized = window?.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
                window?.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _surface.Focus(FocusState.Programmatic);
            }
            else
            {
                if (_popup is not null)
                {
                    if (_popup.XamlRoot is { } root) root.Changed -= OnRootChanged;
                    _popup.IsOpen = false;
                    _popup.Child = null;
                    _popup = null;
                }
                _surface.Width = double.NaN;
                _surface.Height = double.NaN;
                if (!_host.Children.Contains(_surface)) _host.Children.Add(_surface);
                window?.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (_wasMaximized && window?.AppWindow.Presenter is OverlappedPresenter op) op.Maximize();
            }
            ((FontIcon)_full.Content).Glyph = on ? "" : "";
            ToolTipService.SetToolTip(_full, on ? L.T("Leave full screen (Esc)") : L.T("Full screen"));
        }
        catch (Exception ex) { Log.Warn($"Trailer full screen: {ex.Message}"); }
    }

    void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!_full_) return;
        _surface.Width = sender.Size.Width;
        _surface.Height = sender.Size.Height;
    }
}
