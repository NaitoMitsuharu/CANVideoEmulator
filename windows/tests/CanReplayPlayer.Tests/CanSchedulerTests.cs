using CanReplayPlayer.Core.Can;
using CanReplayPlayer.Core.Playback;
using Xunit;

namespace CanReplayPlayer.Tests;

public class CanSchedulerTests
{
    private static (PlaybackClock Clock, CanScheduler Scheduler, MemoryCanTransport Transport)
        Build(CanTimeline timeline)
    {
        var clock = new PlaybackClock { Duration = timeline.Duration };
        var transport = new MemoryCanTransport();
        transport.Open();
        var scheduler = new CanScheduler(clock, transport);
        scheduler.LoadTimeline(timeline);
        return (clock, scheduler, transport);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public void SendsNothingUntilThePlaybackClockIsPlaying()
    {
        var (_, scheduler, transport) = Build(CanTimelineTests.EveryMillisecond(200));
        scheduler.Start();

        Thread.Sleep(120);
        scheduler.Stop();

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void SendsFramesInOrderWhilePlaying()
    {
        var (clock, scheduler, transport) = Build(CanTimelineTests.EveryMillisecond(200));
        scheduler.Start(clock.CurrentTime);
        clock.Play();

        Assert.True(WaitUntil(() => transport.Count >= 150),
            $"only {transport.Count} frames were sent");
        scheduler.Stop();

        var sent = transport.Sent.Select(s => s.Frame).ToList();
        for (var i = 1; i < sent.Count; i++)
        {
            Assert.True(sent[i].TimestampMicroseconds >= sent[i - 1].TimestampMicroseconds,
                "frames must be transmitted in timeline order");
        }
    }

    [Fact]
    public void PauseStopsTransmissionImmediately()
    {
        var (clock, scheduler, transport) = Build(CanTimelineTests.EveryMillisecond(5000));
        scheduler.Start(clock.CurrentTime);
        clock.Play();

        Assert.True(WaitUntil(() => transport.Count > 20));
        clock.Pause();
        scheduler.Stop();

        var atPause = transport.Count;
        Thread.Sleep(150);
        Assert.Equal(atPause, transport.Count);
    }

    [Fact]
    public void StartingFromANonZeroPositionSkipsEarlierFramesRatherThanReplayingThem()
    {
        var (clock, scheduler, transport) = Build(CanTimelineTests.EveryMillisecond(5000));
        clock.SetPosition(TimeSpan.FromSeconds(2));
        scheduler.Start(clock.CurrentTime);
        clock.Play();

        Assert.True(WaitUntil(() => transport.Count > 5));
        scheduler.Stop();

        var first = transport.Sent.First().Frame;
        Assert.True(first.TimestampMicroseconds >= 2_000_000,
            $"first frame after seeking to 2 s was at {first.TimestampMicroseconds} us; " +
            "frames before the seek position must not be replayed");
    }

    [Fact]
    public void RaisesTimelineCompletedOnceAtTheEnd()
    {
        var (clock, scheduler, transport) = Build(CanTimelineTests.EveryMillisecond(30));
        var completions = 0;
        scheduler.TimelineCompleted += () => Interlocked.Increment(ref completions);

        scheduler.Start(clock.CurrentTime);
        clock.Play();

        Assert.True(WaitUntil(() => Volatile.Read(ref completions) >= 1));
        Thread.Sleep(80);
        scheduler.Stop();

        Assert.Equal(1, Volatile.Read(ref completions));
        Assert.Equal(30, transport.Count);
    }

    [Fact]
    public void PayloadReachesTheTransportUnmodified()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0x00, 0x00, 0xBE, 0xEF, 0x00, 0x00 };
        var timeline = CanTimelineTests.Timeline(0, (0, 0x1AA, payload, false, false));
        var (clock, scheduler, transport) = Build(timeline);

        scheduler.Start(clock.CurrentTime);
        clock.Play();
        Assert.True(WaitUntil(() => transport.Count == 1));
        scheduler.Stop();

        var frame = transport.Sent.Single().Frame;
        Assert.Equal(0x1AAu, frame.CanId);
        Assert.Equal(8, frame.Dlc);
        Assert.Equal(payload, frame.ToArray());
    }

    [Fact]
    public void TxEchoFramesAreIncludedByDefaultAndCanBeExcluded()
    {
        var timeline = CanTimelineTests.Timeline(0,
            (0, 0x0AA, [1], false, false),
            (1000, 0x2E4, [2], false, true),
            (2000, 0x0AA, [3], false, false));

        var (clock, scheduler, transport) = Build(timeline);
        scheduler.Start(clock.CurrentTime);
        clock.Play();
        Assert.True(WaitUntil(() => transport.Count == 3));
        scheduler.Stop();

        clock.Stop();
        transport.Clear();
        scheduler.IncludeTxEcho = false;
        scheduler.Start(clock.CurrentTime);
        clock.Play();
        Assert.True(WaitUntil(() => transport.Count == 2));
        scheduler.Stop();

        Assert.DoesNotContain(transport.Sent, s => s.Frame.IsTxEcho);
    }

    [Fact]
    public void TimingIsAnchoredSoErrorDoesNotAccumulate()
    {
        // 2000 frames at 1 kHz. A sleep-per-gap scheduler drifts by tens of
        // milliseconds over this; an absolute-deadline one does not.
        var (clock, scheduler, transport) = Build(CanTimelineTests.EveryMillisecond(2000));
        var started = System.Diagnostics.Stopwatch.StartNew();

        scheduler.Start(TimeSpan.Zero);
        clock.Play();
        Assert.True(WaitUntil(() => transport.Count >= 2000, timeoutMs: 10_000),
            $"only {transport.Count} of 2000 frames were sent");
        scheduler.Stop();

        var elapsed = started.Elapsed;
        Assert.InRange(elapsed.TotalMilliseconds, 1900, 2600);

        var snapshot = scheduler.Snapshot();
        Assert.Equal(2000, snapshot.FramesSent);
        Assert.Equal(0, snapshot.SendErrors);
        Assert.True(snapshot.JitterP95Ms < 25,
            $"P95 jitter was {snapshot.JitterP95Ms:F2} ms");
    }

    [Fact]
    public void CountsSendErrorsWithoutStopping()
    {
        var (clock, scheduler, transport) = Build(CanTimelineTests.EveryMillisecond(100));
        transport.FailEveryNth = 10;

        scheduler.Start(clock.CurrentTime);
        clock.Play();
        Assert.True(WaitUntil(() => scheduler.Snapshot().FramesSent
                                    + scheduler.Snapshot().SendErrors >= 100));
        scheduler.Stop();

        var snapshot = scheduler.Snapshot();
        Assert.Equal(10, snapshot.SendErrors);
        Assert.Equal(90, snapshot.FramesSent);
    }

    [Fact]
    public void FrameSentEventFiresForEveryDeliveredFrame()
    {
        var (clock, scheduler, transport) = Build(CanTimelineTests.EveryMillisecond(100));
        var observed = 0;
        scheduler.FrameSent += _ => Interlocked.Increment(ref observed);

        scheduler.Start(clock.CurrentTime);
        clock.Play();
        Assert.True(WaitUntil(() => Volatile.Read(ref observed) >= 100));
        scheduler.Stop();

        Assert.Equal(transport.Count, Volatile.Read(ref observed));
    }

    [Fact]
    public void StopIsIdempotentAndLeavesNothingRunning()
    {
        var (clock, scheduler, _) = Build(CanTimelineTests.EveryMillisecond(5000));
        scheduler.Start(clock.CurrentTime);
        clock.Play();
        Thread.Sleep(30);

        scheduler.Stop();
        scheduler.Stop();
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public void LoadingATimelineWhileRunningIsRejected()
    {
        var (clock, scheduler, _) = Build(CanTimelineTests.EveryMillisecond(5000));
        scheduler.Start(clock.CurrentTime);
        clock.Play();
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => scheduler.LoadTimeline(CanTimelineTests.EveryMillisecond(10)));
        }
        finally
        {
            scheduler.Stop();
        }
    }

    [Fact]
    public void ClosedTransportProducesErrorsNotCrashes()
    {
        var timeline = CanTimelineTests.EveryMillisecond(50);
        var clock = new PlaybackClock { Duration = timeline.Duration };
        var transport = new MemoryCanTransport();      // deliberately not opened
        var scheduler = new CanScheduler(clock, transport);
        scheduler.LoadTimeline(timeline);

        scheduler.Start(clock.CurrentTime);
        clock.Play();
        Assert.True(WaitUntil(() => scheduler.Snapshot().SendErrors >= 50));
        scheduler.Stop();

        Assert.Equal(0, scheduler.Snapshot().FramesSent);
    }
}

public class TimingStatisticsTests
{
    [Fact]
    public void PercentilesUseNearestRank()
    {
        var statistics = new TimingStatistics();
        for (var i = 1; i <= 100; i++)
        {
            statistics.RecordSent(i, i * 0.001);
        }

        var snapshot = statistics.Snapshot(1.0);
        Assert.Equal(100, snapshot.FramesSent);
        Assert.Equal(50, snapshot.JitterP50Ms);
        Assert.Equal(95, snapshot.JitterP95Ms);
        Assert.Equal(99, snapshot.JitterP99Ms);
        Assert.Equal(100, snapshot.MaxJitterMs);
        Assert.Equal(50.5, snapshot.AverageJitterMs, 6);
    }

    [Fact]
    public void MaxJitterUsesMagnitudeSoEarlySendsCount()
    {
        var statistics = new TimingStatistics();
        statistics.RecordSent(-8, 0.001);
        statistics.RecordSent(3, 0.002);
        Assert.Equal(8, statistics.Snapshot(1).MaxJitterMs);
    }

    [Fact]
    public void WindowIsBoundedButTotalsAreNot()
    {
        var statistics = new TimingStatistics(windowSize: 16);
        for (var i = 0; i < 1000; i++)
        {
            statistics.RecordSent(i, i * 0.001);
        }

        var snapshot = statistics.Snapshot(1);
        Assert.Equal(1000, snapshot.FramesSent);
        Assert.Equal(16, snapshot.JitterSampleCount);
        Assert.Equal(999, snapshot.MaxJitterMs);
    }

    [Fact]
    public void FramesPerSecondIsMeasuredOverATrailingWindow()
    {
        // 500 sends at 500 Hz, spanning one second.
        var statistics = new TimingStatistics();
        for (var i = 0; i < 500; i++)
        {
            statistics.RecordSent(0, i * 0.002);
        }

        // Read just after the last send: the rate is the recent rate.
        Assert.Equal(500, statistics.Snapshot(1.0).FramesPerSecond, 0);
    }

    [Fact]
    public void FramesPerSecondFallsToZeroWhenTransmissionStops()
    {
        // The bug this replaces: total-divided-by-elapsed keeps counting the
        // seconds after the last send, so the displayed rate decayed slowly
        // towards zero instead of dropping to it.
        var statistics = new TimingStatistics();
        for (var i = 0; i < 500; i++)
        {
            statistics.RecordSent(0, i * 0.002);
        }

        Assert.True(statistics.Snapshot(1.0).FramesPerSecond > 100);
        Assert.Equal(0, statistics.Snapshot(1.0 + TimingStatistics.StallSeconds + 0.01)
            .FramesPerSecond);
        // ...and it stays at zero rather than trending down.
        Assert.Equal(0, statistics.Snapshot(30.0).FramesPerSecond);
    }

    [Fact]
    public void FramesPerSecondNeedsTwoSamples()
    {
        var statistics = new TimingStatistics();
        Assert.Equal(0, statistics.Snapshot(1.0).FramesPerSecond);

        statistics.RecordSent(0, 1.0);
        Assert.Equal(0, statistics.Snapshot(1.0).FramesPerSecond);
    }

    [Fact]
    public void FramesPerSecondTracksARateChange()
    {
        var statistics = new TimingStatistics();
        // 100 Hz for two seconds...
        for (var i = 0; i < 200; i++)
        {
            statistics.RecordSent(0, i * 0.01);
        }

        Assert.Equal(100, statistics.Snapshot(2.0).FramesPerSecond, 0);

        // ...then 1 kHz for one second. The window must follow the new rate,
        // not average it with the old one.
        for (var i = 0; i < 1000; i++)
        {
            statistics.RecordSent(0, 2.0 + i * 0.001);
        }

        Assert.InRange(statistics.Snapshot(3.0).FramesPerSecond, 900, 1100);
    }

    [Fact]
    public void QueueFullIsCountedSeparately()
    {
        var statistics = new TimingStatistics();
        statistics.RecordError(queueFull: true);
        statistics.RecordError(queueFull: false);

        var snapshot = statistics.Snapshot(1);
        Assert.Equal(2, snapshot.SendErrors);
        Assert.Equal(1, snapshot.TransmitQueueFullEvents);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var statistics = new TimingStatistics();
        statistics.RecordSent(5, 0.001);
        statistics.RecordError(false);
        statistics.Reset();

        Assert.Equal(TimingSnapshot.Empty with { FramesPerSecond = 0 },
            statistics.Snapshot(1));
    }

    [Fact]
    public void EmptySampleGivesZeroPercentiles()
    {
        var snapshot = new TimingStatistics().Snapshot(1);
        Assert.Equal(0, snapshot.JitterP50Ms);
        Assert.Equal(0, snapshot.JitterSampleCount);
    }
}
