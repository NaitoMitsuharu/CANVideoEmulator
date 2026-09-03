using System.Text;
using CanReplayPlayer.Core.Can;
using CanReplayPlayer.Core.Playback;

namespace CanReplayPlayer.Scenarios;

/// <summary>
/// A fixed-duration transmit run for end-to-end bring-up.
/// </summary>
/// <remarks>
/// <para>The bring-up procedure is 1 s, then 5 s, then 30 s, then 60 s. Short
/// runs first because a wiring or bit-rate fault shows up in one second, and
/// finding it there costs a second rather than a minute. The longer runs then
/// answer a different question -- whether anything drifts, overflows or drops
/// once buffers have had time to fill.</para>
///
/// <para>Every run reports what Windows scheduled and what it actually sent, so
/// a shortfall can be localised before anyone opens the Android app: if Windows
/// already lost frames, the CAN wiring is not the problem yet.</para>
///
/// <para>This transmits the scenario's own recorded frames and nothing else. It
/// is not a traffic generator, and it adds no CAN IDs of its own.</para>
/// </remarks>
public sealed class BenchRun
{
    /// <summary>The bring-up ladder, shortest first.</summary>
    public static readonly IReadOnlyList<TimeSpan> StandardDurations =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly ReplaySession _session;
    private readonly CanScheduler _scheduler;
    private readonly PlaybackClock _clock;

    public BenchRun(ReplaySession session, CanScheduler scheduler, PlaybackClock clock)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Raised as the run progresses, 0..1, for a progress bar.</summary>
    public event EventHandler<double>? Progress;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Play from the current position for <paramref name="duration"/>, then stop
    /// and report.
    /// </summary>
    /// <remarks>
    /// Deliberately leaves playback paused at the end rather than stopped: the
    /// operator usually wants to see where the video got to, and pausing keeps
    /// the bus silent just as effectively.
    /// </remarks>
    public async Task<BenchResult> RunAsync(TimeSpan duration,
                                            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        var scenario = _session.Current;
        if (scenario is null)
        {
            throw new InvalidOperationException("no scenario is loaded");
        }

        if (IsRunning)
        {
            throw new InvalidOperationException("a bench run is already in progress");
        }

        IsRunning = true;
        try
        {
            // Start from zero so the same frames are transmitted every time and
            // two runs of the same length are comparable.
            _session.Pause();
            _session.Seek(TimeSpan.Zero);
            _scheduler.ResetStatistics();

            var transport = _scheduler.Transport;
            var startedAt = DateTimeOffset.Now;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _session.Play();
            try
            {
                while (stopwatch.Elapsed < duration)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // The clock reaching the end of a short scenario ends the run
                    // early; reporting the truth is better than padding it.
                    if (!_clock.IsPlaying)
                    {
                        break;
                    }

                    Progress?.Invoke(this, Math.Clamp(
                        stopwatch.Elapsed / duration, 0, 1));

                    var remaining = duration - stopwatch.Elapsed;
                    var step = remaining < TimeSpan.FromMilliseconds(50)
                        ? remaining
                        : TimeSpan.FromMilliseconds(50);
                    if (step > TimeSpan.Zero)
                    {
                        await Task.Delay(step, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _session.Pause();
                stopwatch.Stop();
            }

            Progress?.Invoke(this, 1.0);

            return new BenchResult(
                ScenarioId: scenario.ScenarioId,
                Bus: _session.SelectedBus,
                TransportName: transport.Name,
                BitrateBitsPerSecond: transport.Bitrate,
                RequestedDuration: duration,
                ActualDuration: stopwatch.Elapsed,
                StartedAt: startedAt,
                Timing: _scheduler.Snapshot(),
                WorstHealth: _scheduler.WorstHealth,
                WorstHealthDetail: _scheduler.WorstHealthDetail,
                Cancelled: cancellationToken.IsCancellationRequested);
        }
        finally
        {
            IsRunning = false;
        }
    }
}

/// <param name="Timing">Scheduler counters for this run only; statistics are reset at the start.</param>
public readonly record struct BenchResult(
    string ScenarioId,
    int Bus,
    string TransportName,
    int? BitrateBitsPerSecond,
    TimeSpan RequestedDuration,
    TimeSpan ActualDuration,
    DateTimeOffset StartedAt,
    TimingSnapshot Timing,
    CanTransportHealth WorstHealth,
    string WorstHealthDetail,
    bool Cancelled)
{
    /// <summary>
    /// True only when frames were transmitted <em>and</em> the interface stayed
    /// healthy throughout.
    /// </summary>
    /// <remarks>
    /// Frames sent alone is not success. <c>Api.Write</c> queues a frame and
    /// returns OK regardless of what happens on the wire, so a run onto a bus
    /// with no second node to acknowledge reports thousands of frames sent while
    /// the controller sits BUS OFF. The health reading is what separates the two.
    /// </remarks>
    public bool Succeeded =>
        Timing.FramesSent > 0 &&
        Timing.SendErrors == 0 &&
        WorstHealth is CanTransportHealth.Ok or CanTransportHealth.BusLight;

    /// <summary>One line saying whether the run is trustworthy, and why not.</summary>
    public string Verdict => WorstHealth switch
    {
        CanTransportHealth.BusOff =>
            "FAILED -- the interface went BUS OFF. Frames were queued but not "
            + "acknowledged on the wire. A CAN bus needs a second node to "
            + "acknowledge; check that the MCP2515/Spresense is powered and "
            + "wired, and that both ends are terminated with 120 ohm.",
        CanTransportHealth.BusHeavy or CanTransportHealth.BusLight =>
            "FAILED -- the interface's error counter climbed while transmitting, so "
            + "the frames were not being acknowledged. CAN needs a second node to "
            + "acknowledge every frame: check that the MCP2515/Spresense is powered "
            + "and wired, that CAN-H and CAN-L are not swapped, that both ends are "
            + "terminated with 120 ohm, and that both sides use the same bit rate.",
        CanTransportHealth.NotConnected or CanTransportHealth.DriverMissing
            or CanTransportHealth.ApiMissing =>
            "FAILED -- the interface was not available.",
        CanTransportHealth.ChannelInUse =>
            "FAILED -- another application holds the channel.",
        CanTransportHealth.Error =>
            "FAILED -- the interface reported an error.",
        _ when Timing.FramesSent == 0 => "FAILED -- no frames were transmitted.",
        _ when Timing.SendErrors > 0 =>
            $"SUSPECT -- {Timing.SendErrors:N0} send error(s).",
        _ => "OK",
    };
    /// <summary>
    /// A block the operator can paste into HARDWARE_TEST.md beside the counts
    /// read off the Spresense console and the Android Debug tab.
    /// </summary>
    public string ToReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"=== Bench run: {RequestedDuration.TotalSeconds:F0} s ===");
        builder.AppendLine($"  started            : {StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"  scenario           : {ScenarioId}  (bus {Bus})");
        builder.AppendLine($"  transport          : {TransportName}");
        builder.AppendLine($"  bit rate           : " +
            (BitrateBitsPerSecond is { } rate ? $"{rate / 1000} kbit/s" : "n/a"));
        builder.AppendLine($"  requested duration : {RequestedDuration.TotalSeconds:F1} s");
        builder.AppendLine($"  actual duration    : {ActualDuration.TotalSeconds:F3} s"
            + (Cancelled ? "  (cancelled)" : string.Empty));
        builder.AppendLine();
        builder.AppendLine($"  VERDICT            : {Verdict}");
        builder.AppendLine($"  worst bus status   : {WorstHealth}"
            + (string.IsNullOrWhiteSpace(WorstHealthDetail)
                ? string.Empty
                : $" ({WorstHealthDetail})"));
        builder.AppendLine();
        builder.AppendLine($"  Frames scheduled   : {Timing.FramesScheduled:N0}");
        builder.AppendLine($"  Frames sent        : {Timing.FramesSent:N0}");
        builder.AppendLine($"  Send errors        : {Timing.SendErrors:N0}");
        builder.AppendLine($"  TX queue full      : {Timing.TransmitQueueFullEvents:N0}");
        builder.AppendLine($"  Windows-side loss  : {Timing.FramesLost:N0} " +
            $"({Timing.LossFraction * 100:F3} %)");
        builder.AppendLine($"  FPS                : {Timing.FramesPerSecond:N0} frames/s");
        builder.AppendLine($"  Jitter avg         : {Timing.AverageJitterMs:F3} ms");
        builder.AppendLine($"  Jitter P50/P95/P99 : {Timing.JitterP50Ms:F3} / " +
            $"{Timing.JitterP95Ms:F3} / {Timing.JitterP99Ms:F3} ms");
        builder.AppendLine($"  Jitter max         : {Timing.MaxJitterMs:F3} ms");
        builder.AppendLine();
        builder.AppendLine("  Fill in from the other two devices:");
        builder.AppendLine("    Spresense recv_count      : ______   drops: ______");
        builder.AppendLine("    Android  frames received  : ______");
        builder.AppendLine("    Android  decoded updates  : ______");
        return builder.ToString();
    }
}
