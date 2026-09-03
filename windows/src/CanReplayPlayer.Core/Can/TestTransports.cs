using System.Collections.Concurrent;

namespace CanReplayPlayer.Core.Can;

/// <summary>
/// Accepts and discards every frame (requirement 66).
/// </summary>
/// <remarks>
/// The player's default transport when no PEAK hardware is present, so the UI,
/// the clock and the scheduler all run and can be demonstrated with nothing
/// plugged in. It reports its own health honestly rather than pretending to be
/// a connected PCAN-USB.
/// </remarks>
public sealed class NullCanTransport : ICanTransport
{
    public string Name => "Demo Mode (no CAN output)";

    public bool IsOpen { get; private set; }

    public int? Bitrate => null;

    public CanTransportStatus Status { get; private set; } =
        new(CanTransportHealth.NotConnected, "Demo Mode: frames are discarded, nothing is transmitted");

    public long SentCount { get; private set; }

    public void Open()
    {
        IsOpen = true;
        Status = new(CanTransportHealth.NotConnected,
            "Demo Mode: frames are discarded, nothing is transmitted");
    }

    public void Close() => IsOpen = false;

    public CanSendResult Send(in CanFrame frame)
    {
        if (!IsOpen)
        {
            return CanSendResult.Failed("transport is not open");
        }

        SentCount++;
        return CanSendResult.Sent;
    }

    public CanTransportStatus RefreshStatus() => Status;

    public void Dispose() => Close();
}

/// <summary>
/// Records every frame it is given, for tests and for offline synchronisation checks.
/// </summary>
/// <remarks>
/// This is what makes requirement 12's "verify sync before touching hardware"
/// step possible: a scheduler run against this transport produces an exact,
/// inspectable list of what would have gone onto the bus and when.
/// </remarks>
public sealed class MemoryCanTransport : ICanTransport
{
    private readonly ConcurrentQueue<SentFrame> _sent = new();
    private int _failEveryNth;
    private long _sendCounter;

    public string Name => "Memory (test transport)";

    public bool IsOpen { get; private set; }

    public int? Bitrate { get; set; }

    public CanTransportStatus Status { get; private set; } =
        CanTransportStatus.NotConnected("Memory transport is closed");

    /// <summary>When &gt; 0, every Nth send fails, to exercise error accounting.</summary>
    public int FailEveryNth
    {
        get => _failEveryNth;
        set => _failEveryNth = Math.Max(0, value);
    }

    public IReadOnlyCollection<SentFrame> Sent => _sent;

    public int Count => _sent.Count;

    public void Open()
    {
        IsOpen = true;
        Status = CanTransportStatus.Ok("Memory transport open");
    }

    public void Close()
    {
        IsOpen = false;
        Status = CanTransportStatus.NotConnected("Memory transport is closed");
    }

    public void Clear()
    {
        _sent.Clear();
        Interlocked.Exchange(ref _sendCounter, 0);
    }

    public CanSendResult Send(in CanFrame frame)
    {
        if (!IsOpen)
        {
            return CanSendResult.Failed("transport is not open");
        }

        var index = Interlocked.Increment(ref _sendCounter);
        if (_failEveryNth > 0 && index % _failEveryNth == 0)
        {
            return CanSendResult.Failed($"injected failure on send #{index}");
        }

        _sent.Enqueue(new SentFrame(frame, DateTimeOffset.UtcNow));
        return CanSendResult.Sent;
    }

    public CanTransportStatus RefreshStatus() => Status;

    public void Dispose() => Close();

    /// <param name="Frame">The frame as handed to the transport, unmodified.</param>
    /// <param name="SentAt">Wall-clock time the transport accepted it.</param>
    public readonly record struct SentFrame(CanFrame Frame, DateTimeOffset SentAt);
}
