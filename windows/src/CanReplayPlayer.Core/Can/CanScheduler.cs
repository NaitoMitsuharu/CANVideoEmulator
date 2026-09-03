using System.Diagnostics;
using CanReplayPlayer.Core.Playback;

namespace CanReplayPlayer.Core.Can;

/// <summary>
/// Transmits a recorded bus in real time, following the shared playback clock.
/// </summary>
/// <remarks>
/// <para>Requirement 39: deadlines are absolute, never cumulative. Each frame's
/// deadline is <c>runStart + frameTimestamp - clockPositionAtRunStart</c>, all
/// read from one monotonic <see cref="Stopwatch"/>. Sleeping for the gap between
/// consecutive frames would accumulate every sleep's overshoot; anchoring to a
/// run origin means a late frame does not push the ones behind it.</para>
///
/// <para>Windows' default timer granularity is ~15.6 ms, far too coarse for a
/// ~1 kHz frame rate, so the wait is split: sleep only while comfortably early,
/// then spin-wait the last <see cref="SpinThreshold"/>. The spin is bounded and
/// yields, so it costs a fraction of one core rather than pinning it.</para>
///
/// <para>Requirement 24: this class is the only thing in the player that
/// transmits, and it transmits only while the clock is Playing. Pause, Stop,
/// Seeking and shutdown all stop it.</para>
/// </remarks>
public sealed class CanScheduler : IAsyncDisposable
{
    /// <summary>Spin rather than sleep inside this window before a deadline.</summary>
    public static readonly TimeSpan SpinThreshold = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// A frame already this far past its deadline is sent immediately without
    /// trying to catch up further; used only to bound the burst after a stall.
    /// </summary>
    public static readonly TimeSpan LateBurstLimit = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long the worker waits for the clock to start after <see cref="Start"/>.
    /// Exceeding it means the caller never started the clock, so the worker exits
    /// rather than sitting on a thread.
    /// </summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often the interface's own status is read while transmitting.
    /// </summary>
    /// <remarks>
    /// Frequent enough to catch a BUS OFF inside the shortest bench run (1 s),
    /// rare enough that the driver call cannot disturb a ~1 kHz send loop.
    /// </remarks>
    public static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly PlaybackClock _clock;
    private readonly TimingStatistics _statistics = new();
    private readonly object _gate = new();

    private ICanTransport _transport;
    private CanTimeline _timeline = CanTimeline.Empty(0);
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private Stopwatch _monotonic = Stopwatch.StartNew();
    private bool _includeTxEcho = true;
    private CanTransportHealth _worstHealth = CanTransportHealth.Ok;
    private string _worstHealthDetail = string.Empty;

    public CanScheduler(PlaybackClock clock, ICanTransport transport)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>Raised for each frame actually handed to the transport.</summary>
    /// <remarks>
    /// Requirement 38: subscribers must not block. The UI attaches a ring buffer
    /// here and repaints on its own timer rather than per frame.
    /// </remarks>
    public event Action<CanFrame>? FrameSent;

    /// <summary>Raised once when the scheduler passes the end of the timeline.</summary>
    public event Action? TimelineCompleted;

    /// <summary>Raised when a send fails, at most once per failure.</summary>
    public event Action<string>? SendFailed;

    /// <summary>Raised when the bus is found to be off during transmission.</summary>
    public event Action<CanTransportStatus>? BusHealthChanged;

    public bool IsRunning
    {
        get { lock (_gate) { return _cancellation is { IsCancellationRequested: false }; } }
    }

    public CanTimeline Timeline
    {
        get { lock (_gate) { return _timeline; } }
    }

    public TimingStatistics Statistics => _statistics;

    /// <summary>
    /// Whether frames openpilot transmitted are replayed too. Default true: they
    /// were physically on the recorded bus, so excluding them would change what
    /// the bus looked like.
    /// </summary>
    public bool IncludeTxEcho
    {
        get { lock (_gate) { return _includeTxEcho; } }
        set { lock (_gate) { _includeTxEcho = value; } }
    }

    public ICanTransport Transport
    {
        get { lock (_gate) { return _transport; } }
    }

    /// <summary>Swap the transport. Only legal while stopped.</summary>
    public void SetTransport(ICanTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (IsRunning)
        {
            throw new InvalidOperationException("stop the scheduler before changing transport");
        }

        lock (_gate) { _transport = transport; }
    }

    /// <summary>Load a bus timeline. Only legal while stopped (requirement 27).</summary>
    public void LoadTimeline(CanTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (IsRunning)
        {
            throw new InvalidOperationException("stop the scheduler before changing timeline");
        }

        lock (_gate) { _timeline = timeline; }
    }

    /// <summary>
    /// Begin transmitting from <paramref name="anchor"/> (default: the clock's
    /// current position). Frames before that position are skipped by binary
    /// search, never replayed to catch up.
    /// </summary>
    /// <param name="anchor">
    /// The exact scenario position playback is resuming from. Callers should pass
    /// the position they set deliberately -- 0 for a fresh scenario, the seek
    /// target, or the paused position -- and start the scheduler <em>before</em>
    /// the clock. Reading the position here instead would include the few hundred
    /// microseconds between <c>clock.Play()</c> and this call, which is enough to
    /// binary-search past a scenario's opening frames and silently drop them.
    /// </param>
    public void Start(TimeSpan? anchor = null)
    {
        lock (_gate)
        {
            if (_cancellation is { IsCancellationRequested: false })
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var timeline = _timeline;
            var transport = _transport;
            var includeEcho = _includeTxEcho;
            var startPosition = anchor ?? _clock.CurrentTime;

            _monotonic = Stopwatch.StartNew();
            _worker = Task.Factory.StartNew(
                () => Run(timeline, transport, includeEcho, startPosition, cancellation.Token),
                cancellation.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Stop transmitting and wait for the worker to exit. Safe to call repeatedly;
    /// after it returns, nothing is on the bus.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Task worker;
        lock (_gate)
        {
            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The worker never throws out of Run; a cancellation race is not an error.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    public TimingSnapshot Snapshot() => _statistics.Snapshot(_monotonic.Elapsed.TotalSeconds);

    /// <summary>
    /// Worst transport health seen since statistics were last reset.
    /// </summary>
    /// <remarks>
    /// <see cref="ICanTransport.Send"/> only queues a frame, so a successful
    /// return says nothing about whether it reached the wire. On a bus with no
    /// other node to acknowledge, the controller retransmits, its error counter
    /// climbs and it goes BUS OFF -- while "frames sent" keeps rising. Recording
    /// the worst status seen is what stops a run like that from being reported
    /// as a success.
    /// </remarks>
    public CanTransportHealth WorstHealth
    {
        get { lock (_gate) { return _worstHealth; } }
    }

    /// <summary>Detail line for the worst status seen, or empty.</summary>
    public string WorstHealthDetail
    {
        get { lock (_gate) { return _worstHealthDetail; } }
    }

    public void ResetStatistics()
    {
        _statistics.Reset();
        _monotonic = Stopwatch.StartNew();
        lock (_gate)
        {
            _worstHealth = CanTransportHealth.Ok;
            _worstHealthDetail = string.Empty;
        }
    }

    /// <summary>Rank health so the worst reading in a run is the one kept.</summary>
    private static int Severity(CanTransportHealth health) => health switch
    {
        CanTransportHealth.Ok => 0,
        CanTransportHealth.BusLight => 1,
        CanTransportHealth.BusHeavy => 2,
        CanTransportHealth.NotConnected => 3,
        CanTransportHealth.ChannelInUse => 4,
        CanTransportHealth.DriverMissing => 5,
        CanTransportHealth.ApiMissing => 5,
        CanTransportHealth.Error => 6,
        CanTransportHealth.BusOff => 7,
        _ => 6,
    };

    private void NoteHealth(CanTransportStatus status)
    {
        lock (_gate)
        {
            if (Severity(status.Health) > Severity(_worstHealth))
            {
                _worstHealth = status.Health;
                _worstHealthDetail = status.Detail;
            }
        }

        if (status.Health == CanTransportHealth.BusOff)
        {
            BusHealthChanged?.Invoke(status);
        }
    }

    private void Run(CanTimeline timeline, ICanTransport transport, bool includeTxEcho,
                     TimeSpan startPosition, CancellationToken token)
    {
        // Callers start the scheduler just before the clock, so wait briefly for
        // the clock to actually begin. Anchoring before that point would make the
        // whole bus lag by the start-up gap.
        var spinUp = new SpinWait();
        var giveUpAt = _monotonic.Elapsed + StartupGrace;
        while (!_clock.IsPlaying)
        {
            if (token.IsCancellationRequested || _monotonic.Elapsed > giveUpAt)
            {
                return;
            }

            spinUp.SpinOnce(-1);
        }

        // Anchor: the monotonic instant that corresponds to startPosition on the
        // scenario timeline. Every deadline below is derived from this, so error
        // never accumulates from frame to frame.
        var origin = _monotonic.Elapsed - startPosition;
        var index = timeline.IndexAtOrAfter(startPosition);
        var completed = false;
        var lastHealthCheck = _monotonic.Elapsed;

        // Read the status once up front so a bus that is already off is caught
        // before the first frame rather than a quarter second in.
        NoteHealth(transport.RefreshStatus());

        while (!token.IsCancellationRequested)
        {
            if (!_clock.IsPlaying)
            {
                // Pause / Seek / Stop: transmit nothing and let the session
                // decide what happens next.
                return;
            }

            if (index >= timeline.Count)
            {
                if (!completed)
                {
                    completed = true;
                    TimelineCompleted?.Invoke();
                }

                return;
            }

            var frame = timeline[index];
            if (frame.IsTxEcho && !includeTxEcho)
            {
                index++;
                continue;
            }

            var deadline = origin + frame.Timestamp;
            var now = _monotonic.Elapsed;
            var remaining = deadline - now;

            if (remaining > SpinThreshold)
            {
                // Wake early on cancellation so Pause/Stop are not delayed by a
                // long inter-frame gap.
                var sleep = remaining - SpinThreshold;
                if (token.WaitHandle.WaitOne(sleep))
                {
                    return;
                }

                continue;
            }

            if (remaining > TimeSpan.Zero)
            {
                var spinner = new SpinWait();
                while (_monotonic.Elapsed < deadline)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    spinner.SpinOnce(-1);
                }
            }

            var sentAt = _monotonic.Elapsed;
            var jitterMs = (sentAt - deadline).TotalMilliseconds;

            // Counted before the send, so scheduled always equals sent + errors
            // and an end-to-end loss figure has a denominator that means
            // "frames this timeline demanded".
            _statistics.RecordScheduled();

            var result = transport.Send(in frame);
            if (result.Success)
            {
                _statistics.RecordSent(jitterMs, sentAt.TotalSeconds);
                FrameSent?.Invoke(frame);
            }
            else
            {
                _statistics.RecordError(result.TransmitQueueFull);
                SendFailed?.Invoke(result.Error);
            }

            index++;

            // Poll the interface periodically. Send() only queues, so a run onto
            // a bus with nothing to acknowledge looks entirely successful from
            // the return values while the controller is actually BUS OFF.
            if (sentAt - lastHealthCheck >= HealthCheckInterval)
            {
                lastHealthCheck = sentAt;
                NoteHealth(transport.RefreshStatus());
            }

            // After a stall (a blocked transmit queue, a hypervisor pause) the
            // backlog is released as fast as the transport accepts it rather than
            // replayed at the original spacing. Bounded so it cannot spin for
            // long without checking cancellation.
            if (sentAt - deadline > LateBurstLimit)
            {
                origin = sentAt - frame.Timestamp;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.CompletedTask;
    }
}
