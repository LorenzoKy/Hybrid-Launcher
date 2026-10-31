using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HybridLauncher.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace HybridLauncher.Controls;

/// <summary>
/// A GIF of a store description as Steam serves it now: a short muted video that loops. The poster frame shows first; the
/// video plays only while it is on screen and stops when it scrolls away or the page closes, so a long description full of
/// clips costs nothing off screen. The video surface is in place from the start (like the trailer player's); the player is
/// made after layout, when the clip first comes into view. A format this PC cannot decode falls back to the next one, then
/// to the poster alone.
/// </summary>
public sealed partial class LoopVideo : Grid
{
    readonly Queue<Uri> _sources;
    readonly MediaPlayerElement _element = new() { AreTransportControlsEnabled = false, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, IsHitTestVisible = false };
    MediaPlayer? _player;
    Uri? _current;
    bool _visible, _queued, _playing;

    public LoopVideo(IEnumerable<Uri> sources, UIElement? poster)
    {
        _sources = new Queue<Uri>(sources);
        if (poster is not null) Children.Add(poster);
        Children.Add(_element);
        EffectiveViewportChanged += (_, e) =>
        {
            var vp = e.EffectiveViewport;
            bool visible = ActualHeight > 0 && vp.Width > 0 && vp.Height > 0 && vp.Bottom > 0 && vp.Top < ActualHeight;
            if (visible == _visible) return;
            _visible = visible;
            // Never change the player during layout: once it is over.
            Queue();
        };
        // Pauses while a game runs, the window is hidden, the user paused a video or hardware acceleration is off.
        Loaded += (_, _) => Helpers.MediaPolicy.Changed += Queue;
        Unloaded += (_, _) =>
        {
            Helpers.MediaPolicy.Changed -= Queue;
            Release();
        };
    }

    void Queue()
    {
        if (_queued) return;
        _queued = true;
        DispatcherQueue.TryEnqueue(Apply);
    }

    void Apply()
    {
        _queued = false;
        try
        {
            // Scrolling raises the viewport event every frame: the player is only told when the answer changes.
            bool play = _visible && Helpers.MediaPolicy.MayAutoplay;
            if (play == _playing && (_player is not null || !play)) return;
            if (play)
            {
                if (_player is null)
                {
                    if (!_sources.TryDequeue(out var source)) return;
                    _current = source;
                    var player = new MediaPlayer { IsLoopingEnabled = true, IsMuted = true, AutoPlay = false, Source = MediaSource.CreateFromUri(source) };
                    // Raised off the UI thread: the player is dropped there, and the next format tried.
                    player.MediaFailed += (_, e) =>
                    {
                        Log.Warn($"Description clip {source}: {e.Error}");
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_player != player) return;
                            Release();
                            Queue();
                        });
                    };
                    _player = player;
                    _element.SetMediaPlayer(player);
                }
                _player.Play();
                _playing = true;
            }
            else
            {
                _player?.Pause();
                _playing = false;
            }
        }
        catch (Exception ex) { Log.Warn($"Description clip {_current}: {ex.Message}"); }
    }

    void Release()
    {
        var player = _player;
        if (player is null) return;
        _player = null;
        _playing = false;
        try
        {
            player.Pause();
            _element.SetMediaPlayer(null);
            player.Dispose();
        }
        catch { }
    }
}
