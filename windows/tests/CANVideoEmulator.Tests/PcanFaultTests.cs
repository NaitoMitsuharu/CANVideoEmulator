using CANVideoEmulator.Core.Can;
using CANVideoEmulator.Core.Playback;
using CANVideoEmulator.Pcan;
using Peak.Can.Basic;
using Xunit;

namespace CANVideoEmulator.Tests;

public class PcanFaultTests
{
    [Theory]
    [InlineData(PcanStatus.BusPassive, CanTransportHealth.BusPassive)]
    [InlineData(PcanStatus.BusOff, CanTransportHealth.BusOff)]
    [InlineData(PcanStatus.BusOff | PcanStatus.TransmitQueueFull, CanTransportHealth.BusOff)]
    [InlineData(PcanStatus.BusPassive | PcanStatus.BusHeavy, CanTransportHealth.BusPassive)]
    [InlineData(PcanStatus.BusHeavy | PcanStatus.TransmitBufferFull, CanTransportHealth.BusHeavy)]
    [InlineData(PcanStatus.IllegalHardwareHandle, CanTransportHealth.NotConnected)]
    [InlineData(PcanStatus.TransmitQueueFull, CanTransportHealth.Ok)]
    public void StatusPreservesBusFaultsAndDistinguishesPassive(PcanStatus raw, CanTransportHealth expected)
    {
        var mapped = PcanBasicTransport.MapStatus(raw, false);
        Assert.Equal(expected, mapped.Health);
        Assert.Equal((uint)raw, mapped.RawStatus);
    }

    [Theory]
    [InlineData(CanTransportHealth.BusOff, true)]
    [InlineData(CanTransportHealth.BusPassive, true)]
    [InlineData(CanTransportHealth.BusOff, false)]
    [InlineData(CanTransportHealth.BusPassive, false)]
    public void SchedulerStopsAtFaultWithoutWaitingForUi(CanTransportHealth health, bool faultAtStart)
    {
        var clock = new PlaybackClock { Duration = TimeSpan.FromSeconds(2) };
        using var transport = new FaultTransport(health, faultAtStart);
        var scheduler = new CanScheduler(clock, transport);
        scheduler.LoadTimeline(CanTimelineTests.EveryMillisecond(2000));
        using var reported = new ManualResetEventSlim();
        scheduler.BusHealthChanged += _ => reported.Set();
        // Retry must report the same fault again even though worst health was retained.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            reported.Reset();
            transport.Attempts = 0;
            scheduler.Start(TimeSpan.Zero);
            clock.Play();
            Assert.True(reported.Wait(2000));
            Thread.Sleep(30);
            Assert.Equal(faultAtStart ? 0 : 1, transport.Attempts);
            scheduler.Stop();
            clock.Pause();
        }
    }

    private sealed class FaultTransport(CanTransportHealth health, bool faultAtStart) : ICanTransport
    {
        public int Attempts;
        public string Name => "fault simulation";
        public bool IsOpen => true;
        public int? Bitrate => 500000;
        public CanTransportStatus Status => faultAtStart || Attempts > 0
            ? new(health, "simulated fault", 16) : CanTransportStatus.Ok();
        public CanTransportStatus RefreshStatus() => Status;
        public CanSendResult Send(in CanFrame frame) { Interlocked.Increment(ref Attempts); return CanSendResult.Failed("fault"); }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }
    }
}
