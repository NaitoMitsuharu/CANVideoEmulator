using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using CANVideoEmulator.Core.Video;

namespace CANVideoEmulator.Video;

/// <summary>
/// <see cref="IVideoPlayer"/> on WPF's <see cref="MediaElement"/>.
/// </summary>
/// <remarks>
/// <para>Requirement 16 asks for the standard Windows path first, and this is it:
/// <see cref="MediaElement"/> decodes through Media Foundation, so the H.264 MP4
/// the Scenario Builder produces plays on a stock Windows 10 or 11 install with
/// nothing extra to deploy. That keeps the portable build a single EXE plus a
/// Scenarios folder (requirement 56) instead of dragging a media runtime along.</para>
///
/// <para>Two settings matter for this application specifically:
/// <c>LoadedBehavior = Manual</c>, without which the element plays itself and
/// stops honouring our clock; and <c>ScrubbingEnabled = true</c>, without which a
/// seek issued while paused updates <see cref="Position"/> but leaves the last
/// decoded frame on screen -- so a paused seek would look like nothing happened.</para>
///
/// <para><see cref="MediaElement"/> has thread affinity, so every call is
/// marshalled onto its dispatcher. Callers may use this from the CAN scheduler's
/// thread or a timer without extra care.</para>
/// </remarks>
public sealed class MediaElementVideoPlayer : IVideoPlayer
{
    private readonly MediaElement _element;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public MediaElementVideoPlayer(MediaElement element)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _dispatcher = element.Dispatcher;

        _element.LoadedBehavior = MediaState.Manual;
        _element.UnloadedBehavior = MediaState.Manual;
        _element.ScrubbingEnabled = true;
        _element.Volume = 0;                 // exhibition audio is not wanted

        _element.MediaOpened += OnMediaOpened;
        _element.MediaFailed += OnMediaFailed;
    }

    public string? Source { get; private set; }

    public bool IsReady { get; private set; }

    public TimeSpan NaturalDuration { get; private set; }

    public TimeSpan Position => Invoke(() => _element.Position);

    public event EventHandler? Opened;

    public event EventHandler<string>? Failed;

    /// <summary>Raised when the decoder reports end of media.</summary>
    /// <remarks>
    /// The session does not advance scenarios on this: the CAN timeline is the
    /// authority on when a scenario is over, and the video may be a frame period
    /// shorter or longer. It is exposed for diagnostics only.
    /// </remarks>
    public event EventHandler? Ended
    {
        add => _element.MediaEnded += (_, _) => value?.Invoke(this, EventArgs.Empty);
        remove { }
    }

    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            Failed?.Invoke(this, $"video file not found: {path}");
            return;
        }

        Post(() =>
        {
            IsReady = false;
            NaturalDuration = TimeSpan.Zero;
            Source = path;
            _element.Source = new Uri(path, UriKind.Absolute);
            // Manual behaviour means Source alone decodes nothing; a Play/Pause
            // pair is what makes the element open the file and raise MediaOpened
            // while leaving the first frame visible and the clock stopped.
            _element.Play();
            _element.Pause();
        });
    }

    public void CloseMedia() => Post(() =>
    {
        _element.Stop();
        _element.Close();
        _element.Source = null;
        Source = null;
        IsReady = false;
        NaturalDuration = TimeSpan.Zero;
    });

    public void Play() => Post(() => _element.Play());

    public void Pause() => Post(() => _element.Pause());

    public void Stop() => Post(() =>
    {
        _element.Pause();
        _element.Position = TimeSpan.Zero;
    });

    public void Seek(TimeSpan position) => Post(() =>
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (NaturalDuration > TimeSpan.Zero && position > NaturalDuration)
        {
            position = NaturalDuration;
        }

        _element.Position = position;
    });

    private void OnMediaOpened(object? sender, System.Windows.RoutedEventArgs e)
    {
        NaturalDuration = _element.NaturalDuration.HasTimeSpan
            ? _element.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        IsReady = true;
        Opened?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(object? sender, System.Windows.ExceptionRoutedEventArgs e)
    {
        IsReady = false;
        Failed?.Invoke(this,
            $"video could not be decoded ({Path.GetFileName(Source) ?? "unknown file"}): " +
            $"{e.ErrorException?.Message ?? "unknown error"}. " +
            "The scenario's MP4 may be corrupt, or this Windows install may lack an " +
            "H.264 decoder.");
    }

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            RunGuarded(action);
        }
        else
        {
            _dispatcher.BeginInvoke(() => RunGuarded(action));
        }
    }

    private T Invoke<T>(Func<T> function)
    {
        if (_disposed)
        {
            return default!;
        }

        try
        {
            return _dispatcher.CheckAccess() ? function() : _dispatcher.Invoke(function);
        }
        catch (Exception)
        {
            // A media error during shutdown must not propagate into the clock or
            // the scheduler (requirement 64).
            return default!;
        }
    }

    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Failed?.Invoke(this, $"video operation failed: {error.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_dispatcher.CheckAccess())
            {
                _element.MediaOpened -= OnMediaOpened;
                _element.MediaFailed -= OnMediaFailed;
                _element.Stop();
                _element.Close();
            }
            else
            {
                _dispatcher.Invoke(() =>
                {
                    _element.MediaOpened -= OnMediaOpened;
                    _element.MediaFailed -= OnMediaFailed;
                    _element.Stop();
                    _element.Close();
                });
            }
        }
        catch (Exception)
        {
            // The dispatcher may already be shut down; nothing left to release.
        }
    }
}
