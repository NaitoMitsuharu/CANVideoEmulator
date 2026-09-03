using CanReplayPlayer.Core.Playback;
using Xunit;

namespace CanReplayPlayer.Tests;

public class PlaybackClockTests
{
    private static PlaybackClock NewClock(double durationSeconds = 60) =>
        new() { Duration = TimeSpan.FromSeconds(durationSeconds) };

    [Fact]
    public void StartsStoppedAtZero()
    {
        var clock = NewClock();
        Assert.Equal(PlaybackState.Stopped, clock.State);
        Assert.Equal(TimeSpan.Zero, clock.CurrentTime);
        Assert.False(clock.IsPlaying);
    }

    [Fact]
    public void PlayAdvancesTime()
    {
        var clock = NewClock();
        clock.Play();
        Assert.Equal(PlaybackState.Playing, clock.State);

        Thread.Sleep(60);
        var position = clock.CurrentTime;
        Assert.InRange(position.TotalMilliseconds, 30, 400);
    }

    [Fact]
    public void PauseFreezesTimeAndResumeContinuesFromThere()
    {
        var clock = NewClock();
        clock.Play();
        Thread.Sleep(50);
        clock.Pause();

        var paused = clock.CurrentTime;
        Assert.Equal(PlaybackState.Paused, clock.State);

        Thread.Sleep(60);
        Assert.Equal(paused, clock.CurrentTime);

        clock.Play();
        Thread.Sleep(50);
        Assert.True(clock.CurrentTime > paused,
            "resuming must continue from the paused position, not restart");
    }

    [Fact]
    public void StopRewindsToZero()
    {
        var clock = NewClock();
        clock.Play();
        Thread.Sleep(40);
        clock.Stop();

        Assert.Equal(PlaybackState.Stopped, clock.State);
        Assert.Equal(TimeSpan.Zero, clock.CurrentTime);

        Thread.Sleep(30);
        Assert.Equal(TimeSpan.Zero, clock.CurrentTime);
    }

    [Fact]
    public void SetPositionMovesTheClock()
    {
        var clock = NewClock();
        clock.SetPosition(TimeSpan.FromSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(12), clock.CurrentTime);
    }

    [Fact]
    public void SetPositionClampsIntoTheScenario()
    {
        var clock = NewClock(durationSeconds: 30);
        clock.SetPosition(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, clock.CurrentTime);

        clock.SetPosition(TimeSpan.FromSeconds(90));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.CurrentTime);
    }

    [Fact]
    public void SeekingHoldsTimeStillThenResumes()
    {
        var clock = NewClock();
        clock.Play();
        Thread.Sleep(30);

        clock.BeginSeek();
        Assert.Equal(PlaybackState.Seeking, clock.State);

        clock.SetPosition(TimeSpan.FromSeconds(10));
        Thread.Sleep(50);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.CurrentTime);

        clock.EndSeek(resumePlaying: true);
        Assert.Equal(PlaybackState.Playing, clock.State);
        Thread.Sleep(40);
        Assert.True(clock.CurrentTime > TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void EndSeekCanLandPaused()
    {
        var clock = NewClock();
        clock.BeginSeek();
        clock.SetPosition(TimeSpan.FromSeconds(5));
        clock.EndSeek(resumePlaying: false);

        Assert.Equal(PlaybackState.Paused, clock.State);
        Thread.Sleep(40);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.CurrentTime);
    }

    [Fact]
    public void NudgeMovesRelativelyAndClamps()
    {
        var clock = NewClock(durationSeconds: 30);
        clock.SetPosition(TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(10), clock.Nudge(TimeSpan.FromSeconds(-10)));
        Assert.Equal(TimeSpan.Zero, clock.Nudge(TimeSpan.FromSeconds(-30)));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Nudge(TimeSpan.FromSeconds(999)));
    }

    [Fact]
    public void PositionNeverExceedsDuration()
    {
        var clock = new PlaybackClock { Duration = TimeSpan.FromMilliseconds(40) };
        clock.Play();
        Thread.Sleep(200);
        Assert.Equal(TimeSpan.FromMilliseconds(40), clock.CurrentTime);
        Assert.True(clock.HasReachedEnd);
    }

    [Fact]
    public void RestartRewindsButKeepsPlaying()
    {
        var clock = NewClock();
        clock.Play();
        Thread.Sleep(50);
        clock.Restart();

        Assert.Equal(PlaybackState.Playing, clock.State);
        Assert.True(clock.CurrentTime < TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public void ProgressIsAFractionOfDuration()
    {
        var clock = NewClock(durationSeconds: 100);
        clock.SetPosition(TimeSpan.FromSeconds(25));
        Assert.Equal(0.25, clock.Progress, 3);
    }

    [Fact]
    public void ProgressIsZeroWhenDurationIsUnknown()
    {
        var clock = new PlaybackClock();
        clock.SetPosition(TimeSpan.FromSeconds(5));
        Assert.Equal(0, clock.Progress);
    }

    [Fact]
    public void StateChangedFiresOnceForEachRealTransition()
    {
        var clock = NewClock();
        var observed = new List<PlaybackState>();
        clock.StateChanged += (_, state) => observed.Add(state);

        clock.Play();
        clock.Play();                 // already playing: no second event
        clock.Pause();
        clock.Pause();                // already paused
        clock.Stop();

        Assert.Equal([PlaybackState.Playing, PlaybackState.Paused, PlaybackState.Stopped],
            observed);
    }

    [Fact]
    public void NegativeDurationIsRejected()
    {
        var clock = new PlaybackClock();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => clock.Duration = TimeSpan.FromSeconds(-1));
    }
}
