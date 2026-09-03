using CanReplayPlayer.Core.Can;
using CanReplayPlayer.Core.Playback;
using CanReplayPlayer.Core.Video;

namespace CanReplayPlayer.Scenarios;

public enum LoopMode
{
    /// <summary>At the end of a scenario, advance to the next in the playlist.</summary>
    PlaylistAdvance,

    /// <summary>Wrap from the end of the playlist back to its first scenario.</summary>
    PlaylistLoop,

    /// <summary>Repeat the current scenario forever.</summary>
    SingleScenarioLoop,
}

public sealed class ReplaySessionOptions
{
    /// <summary>
    /// Silent interval inserted whenever the CAN stream restarts (requirement 7).
    /// </summary>
    /// <remarks>
    /// There is no back-channel from Windows to the Spresense -- the only link is
    /// the CAN bus itself, and requirement 2 forbids inventing control frames. So
    /// a scenario change is signalled by the absence of traffic: the Android side
    /// sees every signal miss its cycle deadline and drops to STALE, which clears
    /// the previous scenario's values before the new ones arrive.
    ///
    /// 350 ms is comfortably longer than the slowest cycle time observed in the
    /// RAV4 recordings (~100 ms) times the 3x staleness factor.
    /// </remarks>
    public TimeSpan TransitionGap { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>How far the video may drift before being nudged (requirement 41).</summary>
    public TimeSpan VideoSyncTolerance { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan VideoSyncCooldown { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan SkipStep { get; set; } = TimeSpan.FromSeconds(10);

    public LoopMode LoopMode { get; set; } = LoopMode.PlaylistAdvance;

    /// <summary>Replay the frames openpilot transmitted as well as those it received.</summary>
    public bool IncludeTxEcho { get; set; } = true;
}

/// <summary>
/// Drives one scenario at a time: the clock, the video and the CAN scheduler.
/// </summary>
/// <remarks>
/// <para>Every state change funnels through here so the ordering that matters is
/// in one place. The order is always: stop transmitting, then move time, then
/// reposition the media, then resume. Requirement 24 -- nothing may be on the bus
/// during a pause, a stop, a seek or a scenario change.</para>
///
/// <para>The class is UI-free (requirement 15). The WPF layer subscribes to its
/// events and calls its commands; it owns no view state.</para>
/// </remarks>
public sealed class ReplaySession : IAsyncDisposable
{
    private readonly PlaybackClock _clock;
    private readonly CanScheduler _scheduler;
    private readonly IVideoPlayer _video;
    private readonly VideoDriftCorrector _drift;
    private readonly object _gate = new();
    private readonly Func<TimeSpan> _monotonic;

    private ScenarioLibrary _library;
    private Playlist? _playlist;
    private ScenarioPackage? _current;
    private int _selectedBus;
    private bool _completionHandled;

    public ReplaySession(PlaybackClock clock, CanScheduler scheduler, IVideoPlayer video,
                         ScenarioLibrary library, ReplaySessionOptions? options = null,
                         Func<TimeSpan>? monotonic = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _video = video ?? throw new ArgumentNullException(nameof(video));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        Options = options ?? new ReplaySessionOptions();
        _monotonic = monotonic ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() is var t
            ? TimeSpan.FromSeconds((double)t / System.Diagnostics.Stopwatch.Frequency)
            : TimeSpan.Zero);

        _drift = new VideoDriftCorrector(Options.VideoSyncTolerance, Options.VideoSyncCooldown);
        _scheduler.TimelineCompleted += OnTimelineCompleted;
        _playlist = _library.Playlists.Default;
    }

    public ReplaySessionOptions Options { get; }

    public ScenarioLibrary Library => _library;

    public ScenarioPackage? Current
    {
        get { lock (_gate) { return _current; } }
    }

    public Playlist? Playlist
    {
        get { lock (_gate) { return _playlist; } }
    }

    public int SelectedBus
    {
        get { lock (_gate) { return _selectedBus; } }
    }

    public PlaybackState State => _clock.State;

    public TimeSpan Position => _clock.CurrentTime;

    public TimeSpan Duration => _clock.Duration;

    public VideoDriftCorrector Drift => _drift;

    /// <summary>Raised after a different scenario has been made current.</summary>
    public event EventHandler<ScenarioPackage>? ScenarioChanged;

    /// <summary>Raised when the selected CAN bus changes.</summary>
    public event EventHandler<int>? BusChanged;

    /// <summary>Raised for conditions the user should see but that are not fatal.</summary>
    public event EventHandler<string>? Warning;

    /// <summary>Raised when the playlist runs out with looping disabled.</summary>
    public event EventHandler? PlaylistFinished;

    // -- library / playlist -------------------------------------------------

    public void SetLibrary(ScenarioLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        StopTransmitting();
        lock (_gate)
        {
            _library = library;
            _playlist = library.Playlists.Default;
            _current = null;
        }

        _clock.Stop();
        _clock.Duration = TimeSpan.Zero;
    }

    public void SelectPlaylist(string playlistId)
    {
        var playlist = _library.Playlists.Find(playlistId);
        if (playlist is null)
        {
            Warning?.Invoke(this, $"playlist '{playlistId}' not found");
            return;
        }

        lock (_gate) { _playlist = playlist; }
    }

    // -- loading ------------------------------------------------------------

    /// <summary>
    /// Make <paramref name="scenarioId"/> current, resetting the timeline to zero.
    /// </summary>
    /// <remarks>
    /// Requirement 6: stop CAN, load, reset the scheduler to 0, reset the video to
    /// 0, then play. The silent gap happens between the stop and the start.
    /// </remarks>
    public bool LoadScenario(string scenarioId, int? bus = null, bool autoPlay = false)
    {
        if (!_library.ById.TryGetValue(scenarioId, out var package))
        {
            Warning?.Invoke(this, $"scenario '{scenarioId}' is not in the library");
            return false;
        }

        return LoadScenario(package, bus, autoPlay);
    }

    public bool LoadScenario(ScenarioPackage package, int? bus = null, bool autoPlay = false)
    {
        ArgumentNullException.ThrowIfNull(package);

        StopTransmitting();

        foreach (var problem in package.Problems())
        {
            Warning?.Invoke(this, $"{package.ScenarioId}: {problem}");
        }

        var targetBus = bus ?? package.DefaultBus;
        if (!package.AvailableBuses.Contains(targetBus))
        {
            Warning?.Invoke(this,
                $"{package.ScenarioId}: bus {targetBus} is not recorded; " +
                $"falling back to default bus {package.DefaultBus}");
            targetBus = package.DefaultBus;
        }

        CanTimeline timeline;
        try
        {
            timeline = package.Timeline(targetBus);
        }
        catch (Exception error) when (error is CanTimelineException or ScenarioException)
        {
            Warning?.Invoke(this, $"{package.ScenarioId}: {error.Message}");
            return false;
        }

        ScenarioPackage? previous;
        lock (_gate)
        {
            previous = _current;
            _current = package;
            _selectedBus = targetBus;
            _completionHandled = false;
        }

        if (previous is not null && !ReferenceEquals(previous, package))
        {
            previous.Unload();
        }

        _scheduler.IncludeTxEcho = Options.IncludeTxEcho;
        _scheduler.LoadTimeline(timeline);
        _scheduler.ResetStatistics();

        _clock.Duration = package.Duration;
        _clock.Stop();
        _drift.Reset();

        OpenVideo(package);

        ScenarioChanged?.Invoke(this, package);
        BusChanged?.Invoke(this, targetBus);

        if (autoPlay)
        {
            // Requirement 7: hold the video too, so both media start together on
            // the far side of the gap.
            Thread.Sleep(Options.TransitionGap);
            Play();
        }

        return true;
    }

    private void OpenVideo(ScenarioPackage package)
    {
        if (package.VideoPath is not { } path)
        {
            Warning?.Invoke(this, $"{package.ScenarioId}: no video in this scenario package");
            return;
        }

        try
        {
            _video.Open(path);
            _video.Seek(VideoPositionFor(TimeSpan.Zero, package));
        }
        catch (Exception error)
        {
            Warning?.Invoke(this, $"{package.ScenarioId}: video could not be opened: {error.Message}");
        }
    }

    /// <summary>
    /// Where the video should be for a given scenario time (requirement 12).
    /// </summary>
    public static TimeSpan VideoPositionFor(TimeSpan scenarioTime, ScenarioPackage package)
    {
        var position = scenarioTime - package.VideoCanOffset;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    public TimeSpan VideoPositionForCurrent(TimeSpan scenarioTime)
    {
        var package = Current;
        return package is null ? scenarioTime : VideoPositionFor(scenarioTime, package);
    }

    // -- transport ----------------------------------------------------------

    public void SelectBus(int bus)
    {
        var package = Current;
        if (package is null)
        {
            return;
        }

        if (bus == SelectedBus)
        {
            return;
        }

        if (!package.AvailableBuses.Contains(bus))
        {
            Warning?.Invoke(this, $"bus {bus} is not recorded in {package.ScenarioId}");
            return;
        }

        // Requirement 27: stop, gap, then switch. The gap is what makes the
        // Android side forget the previous bus's signals.
        var wasPlaying = _clock.IsPlaying;
        var position = _clock.CurrentTime;

        StopTransmitting();
        _clock.BeginSeek();
        Thread.Sleep(Options.TransitionGap);

        CanTimeline timeline;
        try
        {
            timeline = package.Timeline(bus);
        }
        catch (Exception error) when (error is CanTimelineException or ScenarioException)
        {
            Warning?.Invoke(this, error.Message);
            _clock.EndSeek(wasPlaying);
            return;
        }

        lock (_gate) { _selectedBus = bus; }
        _scheduler.LoadTimeline(timeline);
        _scheduler.ResetStatistics();
        _clock.SetPosition(position);
        if (wasPlaying)
        {
            _scheduler.Start(position);
        }

        _clock.EndSeek(wasPlaying);
        BusChanged?.Invoke(this, bus);
    }

    // -- transport controls -------------------------------------------------

    public void Play()
    {
        if (Current is null)
        {
            return;
        }

        lock (_gate) { _completionHandled = false; }

        // Order matters: hand the scheduler the exact position first, then start
        // the clock. Starting the clock first would let time advance past the
        // frames at that position before the scheduler binary-searched for them.
        var anchor = _clock.CurrentTime;
        _scheduler.Start(anchor);
        _clock.Play();
        _video.Play();
    }

    /// <summary>Requirement 8: freeze both media and stop transmitting.</summary>
    public void Pause()
    {
        StopTransmitting();
        _clock.Pause();
        _video.Pause();
    }

    public void TogglePlayPause()
    {
        if (_clock.IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Requirement 9: stop, rewind both to zero, transmit nothing.</summary>
    public void Stop()
    {
        StopTransmitting();
        _clock.Stop();
        _video.Stop();
        var package = Current;
        if (package is not null)
        {
            _video.Seek(VideoPositionFor(TimeSpan.Zero, package));
        }

        _drift.Reset();
    }

    /// <summary>
    /// Requirement 10: stop CAN, wait the transition gap, reposition both, resume.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        var package = Current;
        if (package is null)
        {
            return;
        }

        var wasPlaying = _clock.IsPlaying;

        StopTransmitting();
        _clock.BeginSeek();

        if (wasPlaying)
        {
            // Only a running scenario needs the gap; seeking while paused is
            // already silent.
            Thread.Sleep(Options.TransitionGap);
        }

        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero
            : position > package.Duration ? package.Duration
            : position;

        _clock.SetPosition(clamped);
        _video.Seek(VideoPositionFor(clamped, package));
        _drift.Reset();

        if (wasPlaying)
        {
            _scheduler.Start(clamped);
        }

        _clock.EndSeek(wasPlaying);
        if (wasPlaying)
        {
            _video.Play();
        }
    }

    public void SkipForward() => Seek(_clock.CurrentTime + Options.SkipStep);

    public void SkipBackward() => Seek(_clock.CurrentTime - Options.SkipStep);

    // -- playlist navigation -------------------------------------------------

    public bool Next(bool autoPlay = true) => Step(+1, autoPlay);

    public bool Previous(bool autoPlay = true) => Step(-1, autoPlay);

    private bool Step(int direction, bool autoPlay)
    {
        Playlist? playlist;
        ScenarioPackage? current;
        lock (_gate)
        {
            playlist = _playlist;
            current = _current;
        }

        if (playlist is null || playlist.Count == 0)
        {
            return false;
        }

        var index = current is null ? -1 : playlist.IndexOf(current.ScenarioId);
        var target = index + direction;

        if (index < 0)
        {
            target = direction > 0 ? 0 : playlist.Count - 1;
        }
        else if (target < 0 || target >= playlist.Count)
        {
            if (Options.LoopMode == LoopMode.PlaylistLoop)
            {
                target = (target % playlist.Count + playlist.Count) % playlist.Count;
            }
            else
            {
                return false;
            }
        }

        StopTransmitting();
        _clock.Stop();
        return LoadScenario(playlist.ScenarioIds[target], bus: null, autoPlay: autoPlay);
    }

    // -- end of scenario -----------------------------------------------------

    private void OnTimelineCompleted()
    {
        lock (_gate)
        {
            if (_completionHandled)
            {
                return;
            }

            _completionHandled = true;
        }

        // Fire off the UI thread's back: the scheduler raises this from its own
        // worker, and advancing involves a blocking transition gap.
        _ = Task.Run(HandleScenarioEnd);
    }

    private void HandleScenarioEnd()
    {
        try
        {
            switch (Options.LoopMode)
            {
                case LoopMode.SingleScenarioLoop:
                    RestartCurrent();
                    break;

                case LoopMode.PlaylistLoop:
                case LoopMode.PlaylistAdvance:
                default:
                    if (!Next(autoPlay: true))
                    {
                        Stop();
                        PlaylistFinished?.Invoke(this, EventArgs.Empty);
                    }

                    break;
            }
        }
        catch (Exception error)
        {
            Warning?.Invoke(this, $"advancing to the next scenario failed: {error.Message}");
        }
    }

    private void RestartCurrent()
    {
        var package = Current;
        if (package is null)
        {
            return;
        }

        StopTransmitting();
        _clock.Stop();
        _video.Stop();
        Thread.Sleep(Options.TransitionGap);

        _clock.SetPosition(TimeSpan.Zero);
        _video.Seek(VideoPositionFor(TimeSpan.Zero, package));
        lock (_gate) { _completionHandled = false; }
        _scheduler.ResetStatistics();
        Play();
    }

    /// <summary>Halt transmission and wait for the worker to actually be gone.</summary>
    private void StopTransmitting() => _scheduler.Stop();

    /// <summary>
    /// Called on the UI tick: keep the picture within tolerance of the clock
    /// without seeking constantly (requirement 41).
    /// </summary>
    public void TickVideoSync()
    {
        var package = Current;
        if (package is null || !_clock.IsPlaying || !_video.IsReady)
        {
            return;
        }

        var target = VideoPositionFor(_clock.CurrentTime, package);
        _drift.Tolerance = Options.VideoSyncTolerance;
        _drift.Cooldown = Options.VideoSyncCooldown;

        if (_drift.ShouldCorrect(_video.Position, target, _monotonic()))
        {
            _video.Seek(target);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _scheduler.TimelineCompleted -= OnTimelineCompleted;
        StopTransmitting();
        await _scheduler.DisposeAsync();
        _video.Dispose();
    }
}
