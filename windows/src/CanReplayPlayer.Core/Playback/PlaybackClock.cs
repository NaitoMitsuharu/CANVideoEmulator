using System.Diagnostics;

namespace CanReplayPlayer.Core.Playback;

/// <summary>
/// The single time source both the video player and the CAN scheduler follow.
/// </summary>
/// <remarks>
/// Requirement 11: neither medium is the master. The clock owns the scenario
/// position; <c>VideoPlayer</c> and <c>CanScheduler</c> both read it and correct
/// themselves towards it. Nothing outside this class advances scenario time.
///
/// Time advances from <see cref="Stopwatch"/>, a monotonic high-resolution
/// counter, so it is immune to wall-clock adjustments (NTP steps, DST, the user
/// changing the clock mid-demo).
/// </remarks>
public sealed class PlaybackClock
{
    private readonly object _gate = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <summary>Scenario position accumulated before the current run started.</summary>
    private TimeSpan _baseline = TimeSpan.Zero;

    /// <summary>Stopwatch reading when the current run started; null while not running.</summary>
    private TimeSpan? _runStartedAt;

    private PlaybackState _state = PlaybackState.Stopped;
    private TimeSpan _duration = TimeSpan.Zero;

    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Total length of the loaded scenario; positions are clamped to it.</summary>
    public TimeSpan Duration
    {
        get { lock (_gate) { return _duration; } }
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "duration cannot be negative");
            }

            lock (_gate) { _duration = value; }
        }
    }

    public PlaybackState State
    {
        get { lock (_gate) { return _state; } }
    }

    public bool IsPlaying => State == PlaybackState.Playing;

    /// <summary>Current scenario position.</summary>
    public TimeSpan CurrentTime
    {
        get { lock (_gate) { return CurrentTimeLocked(); } }
    }

    /// <summary>Position as a fraction of <see cref="Duration"/>, 0..1.</summary>
    public double Progress
    {
        get
        {
            lock (_gate)
            {
                return _duration <= TimeSpan.Zero
                    ? 0d
                    : Math.Clamp(CurrentTimeLocked() / _duration, 0d, 1d);
            }
        }
    }

    /// <summary>True once a playing clock has run past the end of the scenario.</summary>
    public bool HasReachedEnd
    {
        get
        {
            lock (_gate)
            {
                return _duration > TimeSpan.Zero && CurrentTimeLocked() >= _duration;
            }
        }
    }

    private TimeSpan CurrentTimeLocked()
    {
        var position = _runStartedAt is { } started
            ? _baseline + (_stopwatch.Elapsed - started)
            : _baseline;

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return _duration > TimeSpan.Zero && position > _duration ? _duration : position;
    }

    /// <summary>Start or resume from the current position.</summary>
    public void Play()
    {
        PlaybackState? changed = null;
        lock (_gate)
        {
            if (_state != PlaybackState.Playing)
            {
                _baseline = CurrentTimeLocked();
                _runStartedAt = _stopwatch.Elapsed;
                _state = PlaybackState.Playing;
                changed = _state;
            }
        }

        Raise(changed);
    }

    /// <summary>Freeze time where it is. Resuming continues from the same position.</summary>
    public void Pause()
    {
        PlaybackState? changed = null;
        lock (_gate)
        {
            if (_state == PlaybackState.Playing || _state == PlaybackState.Seeking)
            {
                _baseline = CurrentTimeLocked();
                _runStartedAt = null;
                _state = PlaybackState.Paused;
                changed = _state;
            }
        }

        Raise(changed);
    }

    /// <summary>Stop and rewind to zero (requirement 9).</summary>
    public void Stop()
    {
        PlaybackState? changed = null;
        lock (_gate)
        {
            _baseline = TimeSpan.Zero;
            _runStartedAt = null;
            if (_state != PlaybackState.Stopped)
            {
                _state = PlaybackState.Stopped;
                changed = _state;
            }
        }

        Raise(changed);
    }

    /// <summary>
    /// Enter the Seeking state: time stops advancing while the video and the CAN
    /// scheduler are repositioned, which is what makes the transition gap silent
    /// on the bus (requirement 10).
    /// </summary>
    public void BeginSeek()
    {
        PlaybackState? changed = null;
        lock (_gate)
        {
            if (_state != PlaybackState.Seeking)
            {
                _baseline = CurrentTimeLocked();
                _runStartedAt = null;
                _state = PlaybackState.Seeking;
                changed = _state;
            }
        }

        Raise(changed);
    }

    /// <summary>Move the clock to <paramref name="position"/> without changing state.</summary>
    public void SetPosition(TimeSpan position)
    {
        lock (_gate)
        {
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            if (_duration > TimeSpan.Zero && position > _duration)
            {
                position = _duration;
            }

            _baseline = position;
            if (_runStartedAt is not null)
            {
                _runStartedAt = _stopwatch.Elapsed;
            }
        }
    }

    /// <summary>Leave the Seeking state, either resuming or holding paused.</summary>
    public void EndSeek(bool resumePlaying)
    {
        PlaybackState? changed = null;
        lock (_gate)
        {
            if (_state != PlaybackState.Seeking)
            {
                return;
            }

            if (resumePlaying)
            {
                _runStartedAt = _stopwatch.Elapsed;
                _state = PlaybackState.Playing;
            }
            else
            {
                _runStartedAt = null;
                _state = PlaybackState.Paused;
            }

            changed = _state;
        }

        Raise(changed);
    }

    /// <summary>Rewind to zero and keep playing -- used by Next/Previous (requirement 6).</summary>
    public void Restart()
    {
        PlaybackState? changed = null;
        lock (_gate)
        {
            _baseline = TimeSpan.Zero;
            _runStartedAt = _state == PlaybackState.Playing ? _stopwatch.Elapsed : null;
            if (_state == PlaybackState.Stopped)
            {
                _state = PlaybackState.Paused;
                changed = _state;
            }
        }

        Raise(changed);
    }

    /// <summary>Move by <paramref name="delta"/>, clamped into the scenario (requirement 5).</summary>
    public TimeSpan Nudge(TimeSpan delta)
    {
        lock (_gate)
        {
            var target = CurrentTimeLocked() + delta;
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }

            if (_duration > TimeSpan.Zero && target > _duration)
            {
                target = _duration;
            }

            _baseline = target;
            if (_runStartedAt is not null)
            {
                _runStartedAt = _stopwatch.Elapsed;
            }

            return target;
        }
    }

    private void Raise(PlaybackState? state)
    {
        if (state is { } value)
        {
            StateChanged?.Invoke(this, value);
        }
    }
}
