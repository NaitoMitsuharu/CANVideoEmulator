namespace CANVideoEmulator.Core.Can;

/// <summary>
/// Send counters, frame rate and jitter percentiles for the scheduler
/// (requirement 40).
/// </summary>
/// <remarks>
/// <para>Jitter is measured as (actual send time - the frame's scheduled
/// deadline), both taken from the same monotonic clock. Samples are kept in a
/// fixed-size ring so a long exhibition run cannot grow memory without bound;
/// percentiles therefore describe the recent window, and <see cref="WindowSize"/>
/// says how wide that window is.</para>
///
/// <para>The frame rate is measured the same way — over a short trailing window
/// rather than as total ÷ elapsed. A cumulative average keeps counting the
/// seconds after transmission stops, so the displayed rate decays slowly towards
/// zero instead of dropping to it, which reads as a fault that is not there. A
/// window also shows a real stall immediately.</para>
/// </remarks>
public sealed class TimingStatistics
{
    /// <summary>Trailing span the frame rate is measured over.</summary>
    public const double RateWindowSeconds = 1.0;

    /// <summary>Minimum interval between expensive percentile refreshes.</summary>
    public const double PercentileRefreshSeconds = 0.5;

    // Small snapshots are cheap and remain exact immediately. Larger windows
    // are sampled and sorted on a worker so the WPF render tick never stalls
    // playback, even when the CAN stream fills the 20k-frame ring.
    private const int SynchronousPercentileSampleLimit = 2_048;
    private const int PercentileSampleLimit = 1_024;

    /// <summary>
    /// No send within this long means the rate is reported as zero rather than
    /// as whatever it was before the stream stopped.
    /// </summary>
    public const double StallSeconds = 0.5;

    private readonly object _gate = new();
    private readonly double[] _window;
    private readonly double[] _sendTimes;
    private int _next;
    private int _filled;

    // Separate ring for send timestamps: the jitter window is large (20k
    // samples) but the rate only needs the last second, and sizing them
    // together would make either the rate coarse or the jitter memory-hungry.
    private int _timeNext;
    private int _timeFilled;
    private double _lastSendSeconds = double.NegativeInfinity;
    private double _lastPercentileRequestSeconds = double.NegativeInfinity;
    private long _statisticsGeneration;
    private bool _percentileCalculationQueued;
    private Percentiles _percentiles;

    private long _framesScheduled;
    private long _framesSent;
    private long _sendErrors;
    private long _queueFullEvents;
    private double _maxJitterMs;
    private double _sumJitterMs;
    private long _jitterSamples;

    public TimingStatistics(int windowSize = 20_000, int rateSamples = 8_192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(rateSamples, 16);
        _window = new double[windowSize];
        _sendTimes = new double[rateSamples];
    }

    public int WindowSize => _window.Length;

    /// <summary>
    /// Count a frame whose deadline came due and was handed to the transport.
    /// </summary>
    /// <remarks>
    /// Scheduled is the total the timeline demanded; sent and errors partition
    /// it. Keeping them separate is what makes an end-to-end loss figure
    /// meaningful: "Windows scheduled N, Windows sent M" localises a shortfall
    /// to the PC before anyone starts looking at the CAN wiring.
    /// </remarks>
    public void RecordScheduled()
    {
        lock (_gate)
        {
            _framesScheduled++;
        }
    }

    /// <param name="jitterMilliseconds">Actual send time minus the frame's deadline.</param>
    /// <param name="atSeconds">
    /// Monotonic time of the send. Required for the windowed frame rate; pass
    /// the same clock <see cref="Snapshot"/> is given.
    /// </param>
    public void RecordSent(double jitterMilliseconds, double atSeconds)
    {
        lock (_gate)
        {
            _framesSent++;
            _jitterSamples++;
            _sumJitterMs += jitterMilliseconds;

            var magnitude = Math.Abs(jitterMilliseconds);
            if (magnitude > _maxJitterMs)
            {
                _maxJitterMs = magnitude;
            }

            _window[_next] = jitterMilliseconds;
            _next = (_next + 1) % _window.Length;
            if (_filled < _window.Length)
            {
                _filled++;
            }

            _sendTimes[_timeNext] = atSeconds;
            _timeNext = (_timeNext + 1) % _sendTimes.Length;
            if (_timeFilled < _sendTimes.Length)
            {
                _timeFilled++;
            }

            _lastSendSeconds = atSeconds;
        }
    }

    public void RecordError(bool queueFull)
    {
        lock (_gate)
        {
            _sendErrors++;
            if (queueFull)
            {
                _queueFullEvents++;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _statisticsGeneration++;
            _framesScheduled = 0;
            _framesSent = 0;
            _sendErrors = 0;
            _queueFullEvents = 0;
            _maxJitterMs = 0;
            _sumJitterMs = 0;
            _jitterSamples = 0;
            _next = 0;
            _filled = 0;
            _timeNext = 0;
            _timeFilled = 0;
            _lastSendSeconds = double.NegativeInfinity;
            _lastPercentileRequestSeconds = double.NegativeInfinity;
            _percentileCalculationQueued = false;
            _percentiles = default;
        }
    }

    /// <param name="nowSeconds">
    /// Current monotonic time, on the same clock as <see cref="RecordSent"/>.
    /// </param>
    public TimingSnapshot Snapshot(double nowSeconds)
    {
        double[]? sample = null;
        long framesScheduled, framesSent, sendErrors, queueFull, jitterSamples;
        double maxJitter, sumJitter, rate;
        Percentiles percentiles;
        long generation = 0;

        lock (_gate)
        {
            if (_filled > 0 && !_percentileCalculationQueued &&
                nowSeconds - _lastPercentileRequestSeconds >= PercentileRefreshSeconds)
            {
                sample = CopyPercentileSampleLocked();
                _lastPercentileRequestSeconds = nowSeconds;
                generation = _statisticsGeneration;
                if (sample.Length > SynchronousPercentileSampleLimit)
                {
                    _percentileCalculationQueued = true;
                }
            }
            framesScheduled = _framesScheduled;
            framesSent = _framesSent;
            sendErrors = _sendErrors;
            queueFull = _queueFullEvents;
            jitterSamples = _jitterSamples;
            maxJitter = _maxJitterMs;
            sumJitter = _sumJitterMs;
            rate = FramesPerSecondLocked(nowSeconds);
            percentiles = _percentiles;
        }

        if (sample is { } captured)
        {
            if (captured.Length <= SynchronousPercentileSampleLimit)
            {
                percentiles = CalculatePercentiles(captured);
                lock (_gate)
                {
                    if (generation == _statisticsGeneration)
                    {
                        _percentiles = percentiles with { SampleCount = _filled };
                    }
                }
            }
            else
            {
                ThreadPool.QueueUserWorkItem(_ => RefreshPercentiles(captured, generation));
            }
        }

        return new TimingSnapshot(
            FramesScheduled: framesScheduled,
            FramesSent: framesSent,
            SendErrors: sendErrors,
            TransmitQueueFullEvents: queueFull,
            FramesPerSecond: rate,
            AverageJitterMs: jitterSamples > 0 ? sumJitter / jitterSamples : 0,
            JitterP50Ms: percentiles.P50,
            JitterP95Ms: percentiles.P95,
            JitterP99Ms: percentiles.P99,
            MaxJitterMs: maxJitter,
            JitterSampleCount: percentiles.SampleCount);
    }

    private void RefreshPercentiles(double[] sample, long generation)
    {
        var percentiles = CalculatePercentiles(sample);
        lock (_gate)
        {
            if (generation == _statisticsGeneration)
            {
                _percentiles = percentiles with { SampleCount = _filled };
            }

            _percentileCalculationQueued = false;
        }
    }

    private static Percentiles CalculatePercentiles(double[] sample)
    {
        Array.Sort(sample);
        return new Percentiles(
            Percentile(sample, 0.50),
            Percentile(sample, 0.95),
            Percentile(sample, 0.99),
            sample.Length);
    }

    private double[] CopyPercentileSampleLocked()
    {
        var take = Math.Min(_filled, PercentileSampleLimit);
        var sample = new double[take];
        if (take == _filled)
        {
            Array.Copy(_window, sample, take);
            return sample;
        }

        // The ring's physical order is irrelevant for percentiles. Selecting
        // evenly spaced slots preserves a representative distribution while
        // bounding periodic allocation and sorting work.
        for (var i = 0; i < take; i++)
        {
            sample[i] = _window[(int)((long)i * _filled / take)];
        }

        return sample;
    }

    /// <summary>Frames per second over the trailing window; 0 while stalled.</summary>
    public double FramesPerSecond(double nowSeconds)
    {
        lock (_gate) { return FramesPerSecondLocked(nowSeconds); }
    }

    private double FramesPerSecondLocked(double nowSeconds)
    {
        if (_timeFilled < 2 || nowSeconds - _lastSendSeconds > StallSeconds)
        {
            return 0;
        }

        var cutoff = nowSeconds - RateWindowSeconds;
        var counted = 0;
        var oldest = double.MaxValue;

        for (var i = 0; i < _timeFilled; i++)
        {
            var index = (_timeNext - 1 - i + _sendTimes.Length * 2) % _sendTimes.Length;
            var stamp = _sendTimes[index];
            if (stamp < cutoff)
            {
                break;
            }

            counted++;
            oldest = stamp;
        }

        if (counted < 2)
        {
            return 0;
        }

        var span = _lastSendSeconds - oldest;
        // The ring may be shorter than the window at very high rates; dividing by
        // the observed span rather than the window keeps the figure honest.
        return span > 0 ? (counted - 1) / span : 0;
    }

    /// <summary>Nearest-rank percentile over an already sorted sample.</summary>
    internal static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private readonly record struct Percentiles(double P50, double P95, double P99, int SampleCount);
}

public readonly record struct TimingSnapshot(
    long FramesScheduled,
    long FramesSent,
    long SendErrors,
    long TransmitQueueFullEvents,
    double FramesPerSecond,
    double AverageJitterMs,
    double JitterP50Ms,
    double JitterP95Ms,
    double JitterP99Ms,
    double MaxJitterMs,
    int JitterSampleCount)
{
    public static readonly TimingSnapshot Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Frames that came due but never reached the transport.</summary>
    public long FramesLost => Math.Max(0, FramesScheduled - FramesSent);

    /// <summary>Fraction of scheduled frames that did not reach the transport, 0..1.</summary>
    public double LossFraction =>
        FramesScheduled == 0 ? 0 : (double)FramesLost / FramesScheduled;
}
