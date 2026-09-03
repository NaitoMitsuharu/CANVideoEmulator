using CANVideoEmulator.Core.Can;
using CANVideoEmulator.Core.Playback;
using CANVideoEmulator.Core.Video;
using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

public class ReplaySessionTests : IDisposable
{
    private readonly ScenarioFixture _fixture = new();
    private readonly PlaybackClock _clock = new();
    private readonly MemoryCanTransport _transport = new();
    private readonly FakeVideoPlayer _video = new();
    private readonly CanScheduler _scheduler;

    public ReplaySessionTests()
    {
        _transport.Open();
        _scheduler = new CanScheduler(_clock, _transport);
    }

    /// <summary>A session with a short transition gap, so the tests stay quick.</summary>
    private ReplaySession Session(ScenarioLibrary library, LoopMode mode = LoopMode.PlaylistAdvance) =>
        new(_clock, _scheduler, _video, library, new ReplaySessionOptions
        {
            TransitionGap = TimeSpan.FromMilliseconds(20),
            LoopMode = mode,
            SkipStep = TimeSpan.FromSeconds(10),
        });

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Thread.Sleep(5);
        }

        return condition();
    }

    public void Dispose()
    {
        _scheduler.Stop();
        _fixture.Dispose();
    }

    // --- loading -----------------------------------------------------------

    [Fact]
    public void LoadingAScenarioSelectsItsDefaultBusAndResetsToZero()
    {
        _fixture.AddScenario("rav4_001", frameCount: 200, buses: [0, 1], defaultBus: 1);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));

        Assert.True(session.LoadScenario("rav4_001"));

        Assert.Equal("rav4_001", session.Current!.ScenarioId);
        Assert.Equal(1, session.SelectedBus);
        Assert.Equal(TimeSpan.Zero, session.Position);
        Assert.Equal(PlaybackState.Stopped, session.State);
        Assert.Equal(1, _scheduler.Timeline.BusIndex);
    }

    [Fact]
    public void LoadingAnUnknownScenarioWarnsRatherThanThrowing()
    {
        _fixture.AddScenario("rav4_001");
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        var warnings = new List<string>();
        session.Warning += (_, w) => warnings.Add(w);

        Assert.False(session.LoadScenario("nope"));
        Assert.Contains(warnings, w => w.Contains("nope"));
    }

    [Fact]
    public void RequestingAnUnavailableBusFallsBackToTheDefault()
    {
        _fixture.AddScenario("rav4_001", buses: [0], defaultBus: 0);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        var warnings = new List<string>();
        session.Warning += (_, w) => warnings.Add(w);

        session.LoadScenario("rav4_001", bus: 5);

        Assert.Equal(0, session.SelectedBus);
        Assert.Contains(warnings, w => w.Contains("bus 5 is not recorded"));
    }

    [Fact]
    public void OpeningAScenarioPositionsTheVideoUsingTheCanOffset()
    {
        // The first video frame is 40 ms *before* CAN t=0, so at scenario time 0
        // the video must sit at +40 ms.
        _fixture.AddScenario("rav4_001", videoCanOffsetMs: -40);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");

        Assert.Contains(_video.Seeks, s => s == TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public void VideoPositionNeverGoesNegative()
    {
        _fixture.AddScenario("rav4_001", videoCanOffsetMs: 500);
        var package = ScenarioPackage.Load(Path.Combine(_fixture.Root, "rav4_001"));

        Assert.Equal(TimeSpan.Zero,
            ReplaySession.VideoPositionFor(TimeSpan.Zero, package));
        Assert.Equal(TimeSpan.FromMilliseconds(500),
            ReplaySession.VideoPositionFor(TimeSpan.FromSeconds(1), package));
    }

    // --- transport controls -------------------------------------------------

    [Fact]
    public void PlayStartsBothMediaAndTheBus()
    {
        _fixture.AddScenario("rav4_001", frameCount: 3000);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");

        session.Play();

        Assert.True(WaitUntil(() => _transport.Count > 10));
        Assert.Equal(PlaybackState.Playing, session.State);
        Assert.True(_video.IsPlaying);
    }

    [Fact]
    public void PauseStopsTheBusAndFreezesBothMedia()
    {
        _fixture.AddScenario("rav4_001", frameCount: 3000);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Play();
        Assert.True(WaitUntil(() => _transport.Count > 10));

        session.Pause();
        var atPause = _transport.Count;
        var positionAtPause = session.Position;

        Thread.Sleep(150);

        Assert.Equal(atPause, _transport.Count);
        Assert.Equal(positionAtPause, session.Position);
        Assert.False(_video.IsPlaying);
        Assert.Equal(PlaybackState.Paused, session.State);
    }

    [Fact]
    public void ResumeContinuesFromThePausedPosition()
    {
        _fixture.AddScenario("rav4_001", frameCount: 3000);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Play();
        Assert.True(WaitUntil(() => _transport.Count > 20));
        session.Pause();

        var paused = session.Position;
        var framesAtPause = _transport.Count;

        session.Play();
        Assert.True(WaitUntil(() => _transport.Count > framesAtPause + 10));

        var resumedFirst = _transport.Sent.Skip(framesAtPause).First().Frame;
        Assert.True(resumedFirst.Timestamp >= paused - TimeSpan.FromMilliseconds(5),
            $"resumed at {resumedFirst.Timestamp.TotalMilliseconds:F1} ms but paused at " +
            $"{paused.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void StopRewindsEverythingAndSilencesTheBus()
    {
        _fixture.AddScenario("rav4_001", frameCount: 3000);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Play();
        Assert.True(WaitUntil(() => _transport.Count > 10));

        session.Stop();
        var atStop = _transport.Count;
        Thread.Sleep(120);

        Assert.Equal(atStop, _transport.Count);
        Assert.Equal(TimeSpan.Zero, session.Position);
        Assert.Equal(PlaybackState.Stopped, session.State);
        Assert.Equal(TimeSpan.Zero, _video.Position);
    }

    // --- seeking ------------------------------------------------------------

    [Fact]
    public void SeekMovesBothMediaAndResumesFromTheTarget()
    {
        _fixture.AddScenario("rav4_001", frameCount: 5000);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Play();
        Assert.True(WaitUntil(() => _transport.Count > 10));

        // Pause before clearing: while the scheduler is still running, anything
        // between reading the count and the seek stopping it would land in the
        // "after" window and make this assertion about the wrong frame.
        session.Pause();
        _transport.Clear();

        session.Seek(TimeSpan.FromSeconds(3));
        session.Play();

        Assert.True(WaitUntil(() => _transport.Count > 5));
        var afterSeek = _transport.Sent.First().Frame;

        Assert.True(afterSeek.Timestamp >= TimeSpan.FromSeconds(3),
            $"first frame after seeking to 3 s was at {afterSeek.Timestamp.TotalSeconds:F3} s");
        Assert.Equal(TimeSpan.FromSeconds(3), _video.Seeks[^1]);
        session.Pause();
    }

    [Fact]
    public void SeekingWhilePausedStaysPausedAndSendsNothing()
    {
        _fixture.AddScenario("rav4_001", frameCount: 5000);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Pause();
        _transport.Clear();

        session.Seek(TimeSpan.FromSeconds(2));
        Thread.Sleep(120);

        Assert.Equal(TimeSpan.FromSeconds(2), session.Position);
        Assert.Empty(_transport.Sent);
        Assert.NotEqual(PlaybackState.Playing, session.State);
    }

    [Fact]
    public void SeekClampsToTheScenarioBounds()
    {
        _fixture.AddScenario("rav4_001", frameCount: 1001);   // 1.0 s
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");

        session.Seek(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, session.Position);

        session.Seek(TimeSpan.FromSeconds(99));
        Assert.Equal(TimeSpan.FromSeconds(1), session.Position);
    }

    [Fact]
    public void SkipForwardAndBackMoveByTheConfiguredStep()
    {
        _fixture.AddScenario("rav4_001", frameCount: 60_001);   // 60 s
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Seek(TimeSpan.FromSeconds(30));

        session.SkipForward();
        Assert.Equal(TimeSpan.FromSeconds(40), session.Position);

        session.SkipBackward();
        Assert.Equal(TimeSpan.FromSeconds(30), session.Position);
    }

    // --- bus selection -------------------------------------------------------

    [Fact]
    public void SelectingAnotherBusSwapsTheTimelineAndKeepsThePosition()
    {
        _fixture.AddScenario("rav4_001", frameCount: 5000, buses: [0, 1], defaultBus: 0);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Seek(TimeSpan.FromSeconds(1));

        session.SelectBus(1);

        Assert.Equal(1, session.SelectedBus);
        Assert.Equal(1, _scheduler.Timeline.BusIndex);
        Assert.Equal(TimeSpan.FromSeconds(1), session.Position);
    }

    [Fact]
    public void OnlyTheSelectedBusReachesTheWire()
    {
        _fixture.AddScenario("rav4_001", frameCount: 3000, buses: [0, 1], defaultBus: 0);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Play();
        Assert.True(WaitUntil(() => _transport.Count > 30));
        session.Pause();

        // The fixture stamps the bus index into the first payload byte.
        Assert.All(_transport.Sent, s => Assert.Equal(0, s.Frame.Data[0]));

        _transport.Clear();
        session.SelectBus(1);
        session.Play();
        Assert.True(WaitUntil(() => _transport.Count > 30));
        session.Pause();

        Assert.All(_transport.Sent, s => Assert.Equal(1, s.Frame.Data[0]));
    }

    [Fact]
    public void SwitchingBusLeavesASilentGap()
    {
        _fixture.AddScenario("rav4_001", frameCount: 5000, buses: [0, 1]);
        var session = new ReplaySession(_clock, _scheduler, _video,
            ScenarioLibrary.Load(_fixture.Root),
            new ReplaySessionOptions { TransitionGap = TimeSpan.FromMilliseconds(200) });

        session.LoadScenario("rav4_001");
        session.Play();
        Assert.True(WaitUntil(() => _transport.Count > 20));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var lastBefore = _transport.Sent.Last().SentAt;
        session.SelectBus(1);
        Assert.True(WaitUntil(() => _transport.Sent.Any(s => s.Frame.Data[0] == 1)));

        var firstAfter = _transport.Sent.First(s => s.Frame.Data[0] == 1).SentAt;
        var gap = firstAfter - lastBefore;

        Assert.True(gap >= TimeSpan.FromMilliseconds(150),
            $"bus switch left only a {gap.TotalMilliseconds:F0} ms gap; the Android side " +
            "needs the silence to time its signals out");
        session.Pause();
        _ = started;
    }

    [Fact]
    public void SelectingAnUnavailableBusWarnsAndChangesNothing()
    {
        _fixture.AddScenario("rav4_001", buses: [0], defaultBus: 0);
        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        var warnings = new List<string>();
        session.Warning += (_, w) => warnings.Add(w);

        session.SelectBus(3);

        Assert.Equal(0, session.SelectedBus);
        Assert.Contains(warnings, w => w.Contains("bus 3 is not recorded"));
    }

    // --- playlist navigation --------------------------------------------------

    [Fact]
    public void NextAndPreviousWalkThePlaylist()
    {
        _fixture.AddScenario("rav4_001");
        _fixture.AddScenario("rav4_002");
        _fixture.AddScenario("rav4_003");
        _fixture.AddPlaylists(("all", "All", ["rav4_001", "rav4_002", "rav4_003"]));

        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");

        Assert.True(session.Next(autoPlay: false));
        Assert.Equal("rav4_002", session.Current!.ScenarioId);

        Assert.True(session.Next(autoPlay: false));
        Assert.Equal("rav4_003", session.Current!.ScenarioId);

        Assert.True(session.Previous(autoPlay: false));
        Assert.Equal("rav4_002", session.Current!.ScenarioId);
    }

    [Fact]
    public void NextStopsAtTheEndWhenLoopingIsOff()
    {
        _fixture.AddScenario("rav4_001");
        _fixture.AddScenario("rav4_002");
        _fixture.AddPlaylists(("all", "All", ["rav4_001", "rav4_002"]));

        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_002");

        Assert.False(session.Next(autoPlay: false));
        Assert.Equal("rav4_002", session.Current!.ScenarioId);
    }

    [Fact]
    public void PlaylistLoopWrapsAtBothEnds()
    {
        _fixture.AddScenario("rav4_001");
        _fixture.AddScenario("rav4_002");
        _fixture.AddPlaylists(("all", "All", ["rav4_001", "rav4_002"]));

        var session = Session(ScenarioLibrary.Load(_fixture.Root), LoopMode.PlaylistLoop);
        session.LoadScenario("rav4_002");

        Assert.True(session.Next(autoPlay: false));
        Assert.Equal("rav4_001", session.Current!.ScenarioId);

        Assert.True(session.Previous(autoPlay: false));
        Assert.Equal("rav4_002", session.Current!.ScenarioId);
    }

    [Fact]
    public void SwitchingScenarioResetsTheClockAndTheVideoToZero()
    {
        _fixture.AddScenario("rav4_001", frameCount: 5000);
        _fixture.AddScenario("rav4_002", frameCount: 5000);
        _fixture.AddPlaylists(("all", "All", ["rav4_001", "rav4_002"]));

        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        session.LoadScenario("rav4_001");
        session.Seek(TimeSpan.FromSeconds(2));

        session.Next(autoPlay: false);

        Assert.Equal("rav4_002", session.Current!.ScenarioId);
        Assert.Equal(TimeSpan.Zero, session.Position);
        Assert.Equal(TimeSpan.Zero, _video.Position);
    }

    [Fact]
    public void ReachingTheEndAdvancesToTheNextScenarioAutomatically()
    {
        _fixture.AddScenario("rav4_001", frameCount: 60);      // ~60 ms
        _fixture.AddScenario("rav4_002", frameCount: 3000);
        _fixture.AddPlaylists(("all", "All", ["rav4_001", "rav4_002"]));

        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        var changes = new List<string>();
        session.ScenarioChanged += (_, p) => changes.Add(p.ScenarioId);

        session.LoadScenario("rav4_001");
        session.Play();

        Assert.True(WaitUntil(() => session.Current?.ScenarioId == "rav4_002"),
            $"still on {session.Current?.ScenarioId}");
        Assert.Equal(["rav4_001", "rav4_002"], changes);
    }

    [Fact]
    public void ReachingTheEndAdvancesInDemoModeWhereTheSchedulerNeverCompletes()
    {
        // NotConnected transport: the scheduler transmits nothing and never
        // reports completion, so only the clock-based end detection in
        // TickPlayback can advance the playlist (the no-PCAN-hardware case).
        _fixture.AddScenario("rav4_001", frameCount: 60);      // ~59 ms
        _fixture.AddScenario("rav4_002", frameCount: 3000);
        _fixture.AddPlaylists(("all", "All", ["rav4_001", "rav4_002"]));

        var demoTransport = new NullCanTransport();
        demoTransport.Open();
        var demoScheduler = new CanScheduler(_clock, demoTransport);
        var session = new ReplaySession(_clock, demoScheduler, _video,
            ScenarioLibrary.Load(_fixture.Root),
            new ReplaySessionOptions
            {
                TransitionGap = TimeSpan.FromMilliseconds(20),
                LoopMode = LoopMode.PlaylistLoop,
            });

        session.LoadScenario("rav4_001");
        session.Play();

        // Jump straight to the end instead of waiting out the clock, then tick
        // once: only the clock-based detection in TickPlayback can advance here.
        _clock.SetPosition(session.Duration);
        session.TickPlayback();

        Assert.True(WaitUntil(() => session.Current?.ScenarioId == "rav4_002"),
            $"still on {session.Current?.ScenarioId}");

        demoScheduler.Stop();
    }

    [Fact]
    public void SingleScenarioLoopRestartsTheSameScenario()
    {
        _fixture.AddScenario("rav4_001", frameCount: 60);
        var session = Session(ScenarioLibrary.Load(_fixture.Root), LoopMode.SingleScenarioLoop);

        session.LoadScenario("rav4_001");
        session.Play();

        // Two full passes means at least 120 frames from a 60 frame timeline.
        Assert.True(WaitUntil(() => _transport.Count >= 120),
            $"only {_transport.Count} frames were sent");
        Assert.Equal("rav4_001", session.Current!.ScenarioId);
        session.Pause();
    }

    [Fact]
    public void FinishingThePlaylistStopsAndAnnouncesIt()
    {
        _fixture.AddScenario("rav4_001", frameCount: 60);
        _fixture.AddPlaylists(("all", "All", ["rav4_001"]));

        var session = Session(ScenarioLibrary.Load(_fixture.Root));
        var finished = false;
        session.PlaylistFinished += (_, _) => finished = true;

        session.LoadScenario("rav4_001");
        session.Play();

        Assert.True(WaitUntil(() => finished));
        Assert.Equal(PlaybackState.Stopped, session.State);
    }

    // --- video drift ----------------------------------------------------------

    [Fact]
    public void DriftWithinToleranceIsNotCorrected()
    {
        var corrector = new VideoDriftCorrector(TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(2));

        Assert.False(corrector.ShouldCorrect(TimeSpan.FromMilliseconds(1100),
            TimeSpan.FromMilliseconds(1000), TimeSpan.Zero));
        Assert.Equal(0, corrector.CorrectionCount);
        Assert.Equal(TimeSpan.FromMilliseconds(100), corrector.LastDrift);
    }

    [Fact]
    public void DriftBeyondToleranceIsCorrectedOnceThenHeldOffByTheCooldown()
    {
        var corrector = new VideoDriftCorrector(TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(2));

        Assert.True(corrector.ShouldCorrect(TimeSpan.FromMilliseconds(1500),
            TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.False(corrector.ShouldCorrect(TimeSpan.FromMilliseconds(1500),
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500)));
        Assert.True(corrector.ShouldCorrect(TimeSpan.FromMilliseconds(1500),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
        Assert.Equal(2, corrector.CorrectionCount);
    }

    [Fact]
    public void DriftIsCorrectedInBothDirections()
    {
        var corrector = new VideoDriftCorrector(TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero);

        Assert.True(corrector.ShouldCorrect(TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(900), TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMilliseconds(-400), corrector.LastDrift);
    }
}
