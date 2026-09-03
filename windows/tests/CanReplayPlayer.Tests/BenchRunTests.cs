using CanReplayPlayer.Core.Can;
using CanReplayPlayer.Core.Playback;
using CanReplayPlayer.Scenarios;
using Xunit;

namespace CanReplayPlayer.Tests;

/// <summary>
/// The fixed-duration transmit tests used for hardware bring-up
/// (phase 2 requirements 3 and 4).
/// </summary>
public class BenchRunTests : IDisposable
{
    private readonly ScenarioFixture _fixture = new();
    private readonly PlaybackClock _clock = new();
    private readonly MemoryCanTransport _transport = new();
    private readonly FakeVideoPlayer _video = new();
    private readonly CanScheduler _scheduler;

    public BenchRunTests()
    {
        _transport.Open();
        _scheduler = new CanScheduler(_clock, _transport);
    }

    public void Dispose()
    {
        _scheduler.Stop();
        _fixture.Dispose();
    }

    private ReplaySession Session()
    {
        var library = ScenarioLibrary.Load(_fixture.Root);
        return new ReplaySession(_clock, _scheduler, _video, library,
            new ReplaySessionOptions { TransitionGap = TimeSpan.FromMilliseconds(10) });
    }

    [Fact]
    public void TheStandardLadderIsOneFiveThirtyAndSixtySeconds()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5),
             TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)],
            BenchRun.StandardDurations);
    }

    [Fact]
    public async Task AOneSecondRunTransmitsAboutOneSecondOfFrames()
    {
        // 1 kHz timeline, 10 s long, so a 1 s run is not limited by the scenario.
        _fixture.AddScenario("rav4_001", frameCount: 10_000);
        var session = Session();
        session.LoadScenario("rav4_001");

        var result = await new BenchRun(session, _scheduler, _clock)
            .RunAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), result.RequestedDuration);
        Assert.InRange(result.ActualDuration.TotalMilliseconds, 900, 1600);
        Assert.InRange(result.Timing.FramesSent, 800, 1400);
        Assert.Equal(0, result.Timing.SendErrors);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task ScheduledEqualsSentPlusErrors()
    {
        _fixture.AddScenario("rav4_001", frameCount: 10_000);
        _transport.FailEveryNth = 7;
        var session = Session();
        session.LoadScenario("rav4_001");

        var result = await new BenchRun(session, _scheduler, _clock)
            .RunAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(result.Timing.FramesScheduled,
            result.Timing.FramesSent + result.Timing.SendErrors);
        Assert.True(result.Timing.SendErrors > 0, "the injected failures should have been counted");
        Assert.Equal(result.Timing.SendErrors, result.Timing.FramesLost);
        Assert.InRange(result.Timing.LossFraction, 0.10, 0.20);   // roughly 1 in 7
    }

    [Fact]
    public async Task ARunWithNoFailuresReportsZeroLoss()
    {
        _fixture.AddScenario("rav4_001", frameCount: 10_000);
        var session = Session();
        session.LoadScenario("rav4_001");

        var result = await new BenchRun(session, _scheduler, _clock)
            .RunAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, result.Timing.FramesLost);
        Assert.Equal(0.0, result.Timing.LossFraction);
    }

    [Fact]
    public async Task EachRunStartsFromZeroSoRunsAreComparable()
    {
        _fixture.AddScenario("rav4_001", frameCount: 10_000);
        var session = Session();
        session.LoadScenario("rav4_001");
        var bench = new BenchRun(session, _scheduler, _clock);

        await bench.RunAsync(TimeSpan.FromSeconds(1));
        var firstFrameOfSecondRun = TimeSpan.MaxValue;

        _transport.Clear();
        await bench.RunAsync(TimeSpan.FromSeconds(1));
        if (_transport.Sent.Count > 0)
        {
            firstFrameOfSecondRun = _transport.Sent.First().Frame.Timestamp;
        }

        Assert.True(firstFrameOfSecondRun < TimeSpan.FromMilliseconds(50),
            $"the second run began at {firstFrameOfSecondRun.TotalMilliseconds:F0} ms; " +
            "each run must restart from zero");
    }

    [Fact]
    public async Task StatisticsCoverOnlyTheRunNotEverythingBefore()
    {
        _fixture.AddScenario("rav4_001", frameCount: 10_000);
        var session = Session();
        session.LoadScenario("rav4_001");

        // Transmit for a while first; the run must not inherit those counts.
        session.Play();
        await Task.Delay(400);
        session.Pause();
        var before = _scheduler.Snapshot().FramesSent;
        Assert.True(before > 100);

        var result = await new BenchRun(session, _scheduler, _clock)
            .RunAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.Timing.FramesSent < before + 1400,
            $"run reported {result.Timing.FramesSent} frames, which looks like it " +
            "included the earlier playback");
    }

    [Fact]
    public async Task TheBusIsSilentOnceTheRunEnds()
    {
        _fixture.AddScenario("rav4_001", frameCount: 10_000);
        var session = Session();
        session.LoadScenario("rav4_001");

        await new BenchRun(session, _scheduler, _clock).RunAsync(TimeSpan.FromSeconds(1));

        var atEnd = _transport.Count;
        await Task.Delay(200);
        Assert.Equal(atEnd, _transport.Count);
        Assert.NotEqual(PlaybackState.Playing, session.State);
    }

    [Fact]
    public async Task ARunEndsEarlyWhenTheScenarioIsShorterThanTheRequest()
    {
        // 0.3 s of CAN, but a 5 s run requested.
        _fixture.AddScenario("rav4_001", frameCount: 300);
        var session = Session();
        session.LoadScenario("rav4_001");

        var result = await new BenchRun(session, _scheduler, _clock)
            .RunAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.ActualDuration < TimeSpan.FromSeconds(4),
            $"run took {result.ActualDuration.TotalSeconds:F1} s for a 0.3 s scenario");
        Assert.Equal(TimeSpan.FromSeconds(5), result.RequestedDuration);
    }

    [Fact]
    public async Task CancellationStopsTheRunAndIsReported()
    {
        _fixture.AddScenario("rav4_001", frameCount: 60_000);
        var session = Session();
        session.LoadScenario("rav4_001");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await new BenchRun(session, _scheduler, _clock)
            .RunAsync(TimeSpan.FromSeconds(60), cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.ActualDuration < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunningWithNoScenarioLoadedIsRejected()
    {
        _fixture.AddScenario("rav4_001");
        var session = Session();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BenchRun(session, _scheduler, _clock).RunAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AZeroOrNegativeDurationIsRejected()
    {
        _fixture.AddScenario("rav4_001");
        var session = Session();
        session.LoadScenario("rav4_001");
        var bench = new BenchRun(session, _scheduler, _clock);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => bench.RunAsync(TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => bench.RunAsync(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task TheReportCarriesEveryFigureNeededToCompareThreeDevices()
    {
        _fixture.AddScenario("rav4_001", frameCount: 10_000);
        var session = Session();
        session.LoadScenario("rav4_001");

        var report = (await new BenchRun(session, _scheduler, _clock)
            .RunAsync(TimeSpan.FromSeconds(1))).ToReport();

        foreach (var expected in new[]
                 {
                     "Frames scheduled", "Frames sent", "Send errors",
                     "Windows-side loss", "FPS", "Jitter avg",
                     "Jitter P50/P95/P99", "Jitter max",
                     "Spresense recv_count", "Android  frames received",
                     "Android  decoded updates",
                 })
        {
            Assert.Contains(expected, report);
        }

        Assert.Contains("rav4_001", report);
    }

    [Fact]
    public async Task ProgressRunsFromZeroToOne()
    {
        _fixture.AddScenario("rav4_001", frameCount: 10_000);
        var session = Session();
        session.LoadScenario("rav4_001");

        var bench = new BenchRun(session, _scheduler, _clock);
        var samples = new List<double>();
        bench.Progress += (_, value) => { lock (samples) { samples.Add(value); } };

        await bench.RunAsync(TimeSpan.FromSeconds(1));

        lock (samples)
        {
            Assert.NotEmpty(samples);
            Assert.Equal(1.0, samples[^1]);
            Assert.All(samples, v => Assert.InRange(v, 0.0, 1.0));
        }
    }
}

public class ScheduledFrameCountTests
{
    [Fact]
    public void ScheduledCountsEveryFrameThatCameDue()
    {
        var statistics = new TimingStatistics();
        for (var i = 0; i < 10; i++)
        {
            statistics.RecordScheduled();
        }

        for (var i = 0; i < 7; i++)
        {
            statistics.RecordSent(0.1, i * 0.001);
        }

        for (var i = 0; i < 3; i++)
        {
            statistics.RecordError(queueFull: false);
        }

        var snapshot = statistics.Snapshot(1.0);
        Assert.Equal(10, snapshot.FramesScheduled);
        Assert.Equal(7, snapshot.FramesSent);
        Assert.Equal(3, snapshot.SendErrors);
        Assert.Equal(3, snapshot.FramesLost);
        Assert.Equal(0.3, snapshot.LossFraction, 6);
    }

    [Fact]
    public void LossIsZeroBeforeAnythingIsScheduled()
    {
        var snapshot = new TimingStatistics().Snapshot(1.0);
        Assert.Equal(0, snapshot.FramesScheduled);
        Assert.Equal(0, snapshot.FramesLost);
        Assert.Equal(0.0, snapshot.LossFraction);
    }

    [Fact]
    public void ResetClearsTheScheduledCount()
    {
        var statistics = new TimingStatistics();
        statistics.RecordScheduled();
        statistics.RecordSent(0, 0.001);
        statistics.Reset();

        Assert.Equal(0, statistics.Snapshot(1.0).FramesScheduled);
    }

    [Fact]
    public void LossNeverGoesNegative()
    {
        // Defensive: a sent frame with no matching scheduled count must not
        // produce a negative loss in the UI.
        var statistics = new TimingStatistics();
        statistics.RecordSent(0, 0.001);
        Assert.Equal(0, statistics.Snapshot(1.0).FramesLost);
    }
}
