using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// A video file played for the TV (FR-116), in the capture helper: Windows' own media player decodes
/// it (on the GPU where it can) and plays its sound on the default speakers; each new frame is
/// copied, GPU to GPU, into the engine's held texture. Nothing touches CPU memory. The player is
/// paused while the TV doesn't show the picture (blank, a cut to black, frozen) and plays on when
/// it does, so a video starts exactly as it fades in.
///
/// Threads: the player's events only set flags and wake the render thread, and every call into the
/// player is made on the render thread (<see cref="Pump"/>, <see cref="TakeLatest"/>). Asking from
/// other threads (<see cref="SetVisible"/>, <see cref="SetHeld"/>, <see cref="Command"/>) only records
/// what's wanted. So a player busy in one of its own events can never wait on a lock the render
/// thread holds, and the engine always stops in time to hand the output window to the next one.
/// </summary>
internal sealed class VideoFeed : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly Action _wake;
    private readonly Action<string> _report;
    private readonly object _sync = new(); // guards the wanted state only: never held while calling the player

    // Set by the player's events (any thread).
    private volatile bool _frameReady;
    private volatile bool _opened;
    private volatile bool _ended;
    private volatile string? _failure;
    private int _width, _height;

    // Wanted, from any thread.
    private bool _wantPlaying = true;  // the presenter's play / pause
    private bool _visible;             // the TV shows the picture
    private bool _held;                // frozen
    private bool _restart;
    private double _seek;

    // Render thread only.
    private bool _playing;
    private bool _disposed;

    private VideoFeed(MediaPlayer player, Action wake, Action<string> report)
    {
        _player = player;
        _wake = wake;
        _report = report;
    }

    /// <summary>Why it can't play (in words); null while it's fine.</summary>
    public string? Failure => _failure;

    /// <summary>The video's own picture size; empty until it has opened.</summary>
    public Size Size => _opened ? new Size(Volatile.Read(ref _width), Volatile.Read(ref _height)) : Size.Empty;

    /// <param name="sound">Play its sound (off: muted).</param>
    /// <param name="report">"ended", or "failed" and a reason, from the player's threads.</param>
    public static VideoFeed Start(string path, bool sound, Action wake, Action<string> report)
    {
        var player = new MediaPlayer { IsVideoFrameServerEnabled = true, AutoPlay = false, IsMuted = !sound, AudioCategory = MediaPlayerAudioCategory.Media };
        player.CommandManager.IsEnabled = false; // not on the keyboard's media keys or Windows' media overlay
        var feed = new VideoFeed(player, wake, report);
        player.VideoFrameAvailable += (_, _) =>
        {
            feed._frameReady = true;
            wake();
        };
        player.MediaOpened += (s, _) => feed.OnOpened(s);
        player.MediaEnded += (_, _) => feed.OnEnded();
        player.MediaFailed += (_, e) => feed.OnFailed(Words(e.Error, e.ErrorMessage));
        try
        {
            player.Source = MediaSource.CreateFromUri(new Uri(path));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            player.Dispose();
            throw new InvalidOperationException($"“{Core.VideoSource.Name(path)}” can't be played.", ex);
        }
        return feed;
    }

    private void OnOpened(MediaPlayer sender)
    {
        uint w, h;
        try
        {
            w = sender.PlaybackSession.NaturalVideoWidth;
            h = sender.PlaybackSession.NaturalVideoHeight;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            OnFailed("The video couldn't be played.");
            System.Diagnostics.Debug.WriteLine(ex);
            return;
        }
        if (w == 0 || h == 0)
        {
            OnFailed("It has no picture: it's sound only.");
            return;
        }
        Volatile.Write(ref _width, (int)Math.Min(w, 7680));
        Volatile.Write(ref _height, (int)Math.Min(h, 7680));
        _opened = true;
        _wake();
    }

    private void OnEnded()
    {
        _ended = true;
        _report("ended");
        _wake();
    }

    private void OnFailed(string why)
    {
        if (_failure is not null) return;
        _failure = why;
        _report("failed " + why);
        _wake();
    }

    /// <summary>Plain words for why a video didn't play.</summary>
    internal static string Words(MediaPlayerError error, string? message) => error switch
    {
        MediaPlayerError.SourceNotSupported => "Windows can't play this kind of video. Try an MP4 file.",
        MediaPlayerError.DecodingError => "The video is damaged or uses a format this PC can't decode.",
        MediaPlayerError.NetworkError => "The video's file couldn't be read.",
        MediaPlayerError.Aborted => "Playing the video was stopped.",
        _ => message is { Length: > 0 } m ? m.Trim() : "The video couldn't be played.",
    };

    /// <summary>Whether the TV shows the picture now (any brightness above black).</summary>
    public void SetVisible(bool visible)
    {
        lock (_sync) _visible = visible;
        _wake();
    }

    public void SetHeld(bool held)
    {
        lock (_sync) _held = held;
        _wake();
    }

    public void Command(Core.VideoCommand command, double seconds)
    {
        lock (_sync)
        {
            switch (command)
            {
                case Core.VideoCommand.Play: _wantPlaying = true; break;
                case Core.VideoCommand.Pause: _wantPlaying = false; break;
                case Core.VideoCommand.Restart:
                    _wantPlaying = true;
                    _restart = true;
                    break;
                case Core.VideoCommand.Seek: _seek += seconds; break;
            }
        }
        _wake();
    }

    /// <summary>On the render thread: what's wanted is done (play, pause, jump, start again).</summary>
    public void Pump()
    {
        if (_disposed || !_opened || _failure is not null) return;
        bool restart, play;
        double seek;
        lock (_sync)
        {
            restart = _restart;
            seek = _seek;
            _restart = false;
            _seek = 0;
            if (restart) _ended = false;
            if (_ended && _wantPlaying && seek < 0) _ended = false; // jumping back from the end plays on
            play = _wantPlaying && _visible && !_held && !_ended;
        }
        try
        {
            MediaPlaybackSession session = _player.PlaybackSession;
            if (restart) session.Position = TimeSpan.Zero;
            else if (seek != 0)
            {
                TimeSpan length = session.NaturalDuration;
                TimeSpan to = session.Position + TimeSpan.FromSeconds(seek);
                if (to < TimeSpan.Zero) to = TimeSpan.Zero;
                if (length > TimeSpan.Zero && to >= length) to = length - TimeSpan.FromMilliseconds(100);
                session.Position = to;
            }
            if (play != _playing)
            {
                if (play) _player.Play();
                else _player.Pause();
                _playing = play;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine(ex); // tried again on the next change
        }
    }

    /// <summary>
    /// On the render thread: the newest frame into <paramref name="surface"/> (a texture the size of
    /// <see cref="Size"/>, made for drawing into). True when there was a new one.
    /// </summary>
    public bool TakeLatest(IDirect3DSurface surface)
    {
        Pump();
        if (!_frameReady || _disposed) return false;
        _frameReady = false;
        try
        {
            _player.CopyFrameToVideoSurface(surface);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex); // a frame lost while resizing: the next one comes
            return false;
        }
    }

    /// <summary>On the render thread: silent at once (the player itself is let go by <see cref="Dispose"/>).</summary>
    public void Silence()
    {
        if (_disposed || !_playing) return;
        try
        {
            _player.Pause();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        _playing = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Silence();
        _disposed = true;
        try
        {
            _player.Source = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        _player.Dispose(); // the sound stops with the picture
    }
}
