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
    Queue<Uri> _sources;
    readonly MediaPlayerElement _element = new() { AreTransportControlsEnabled = false, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, IsHitTestVisible = false };
    MediaPlayer? _player;
    Uri? _current;
    bool _visible, _queued, _playing, _making, _unloaded;

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
        // Pauses while a game runs, the window is hidden or hardware acceleration is off (never because the trailer was paused).
        Loaded += (_, _) => { _unloaded = false; Helpers.MediaPolicy.Changed += Queue; };
        Unloaded += (_, _) =>
        {
            _unloaded = true;
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
            bool play = _visible && Helpers.MediaPolicy.MayLoop;
            if (play == _playing && (_player is not null || !play) && !(Helpers.MediaPolicy.GameRunning && _player is not null)) return;
            if (play)
            {
                if (_player is null)
                {
                    if (_making || !_sources.TryDequeue(out var source)) return;
                    _current = source;
                    _making = true;
                    _ = MakeAsync(source);
                    return;
                }
                _player.Play();
                _playing = true;
            }
            else if (Helpers.MediaPolicy.GameRunning && _player is not null && _current is not null)
            {
                // A game is on: the player goes, so its video decoder and surfaces are given back to the GPU; the same clip is made
                // again when the game closes.
                var current = _current;
                Release();
                _sources = new Queue<Uri>(_sources.Prepend(current));
            }
            else
            {
                _player?.Pause();
                _playing = false;
            }
        }
        catch (Exception ex) { Log.Warn($"Description clip {_current}: {ex.Message}"); }
    }

    /// <summary>
    /// Makes the player on a thread-pool (MTA) thread, never on the UI thread: a MediaPlayer made on the UI thread ties inner
    /// handlers to it, and disposing it off the UI thread (Release) can then deadlock with the UI thread (see TrailerPlayer).
    /// </summary>
    async Task MakeAsync(Uri source)
    {
        MediaPlayer? player = null;
        try
        {
            player = await Task.Run(() =>
            {
                var p = new MediaPlayer();
                p.CommandManager.IsEnabled = false;
                p.IsLoopingEnabled = true;
                p.IsMuted = true;
                p.AutoPlay = false;
                // A short silent loop: decoded as it plays, with the smallest buffer (starts sooner, holds less memory).
                p.RealTimePlayback = true;
                p.Source = MediaSource.CreateFromUri(source);
                return p;
            });
        }
        catch (Exception ex) { Log.Warn($"Description clip {source}: {ex.Message}"); }
        _making = false;
        if (player is null) { Queue(); return; }
        if (_unloaded)
        {
            // The page closed meanwhile; the clip is tried again if it comes back.
            _sources = new Queue<Uri>(_sources.Prepend(source));
            _ = Task.Run(() => { try { player.Dispose(); } catch { } });
            return;
        }
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
        Queue();
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
            // Off the UI thread, like the trailer's: a page full of clips must not hold up the next page.
            _ = Task.Run(() => { try { player.Dispose(); } catch { } });
        }
        catch { }
    }
}
