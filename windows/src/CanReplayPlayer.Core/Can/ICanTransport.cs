namespace CanReplayPlayer.Core.Can;

/// <summary>
/// Everything the player needs from a CAN interface (requirement 68).
/// </summary>
/// <remarks>
/// The UI and the scheduler talk only to this interface, so nothing above the
/// transport layer references the PCAN-Basic API. That is what lets the app be
/// developed and tested on a machine with no PEAK hardware or driver
/// (requirement 66), and what keeps <c>MemoryCanTransport</c> usable as a
/// deterministic test double.
/// </remarks>
public interface ICanTransport : IDisposable
{
    /// <summary>Short name shown in the UI, e.g. "PCAN_USBBUS1" or "Demo (in-memory)".</summary>
    string Name { get; }

    /// <summary>True once <see cref="Open"/> has succeeded and frames may be sent.</summary>
    bool IsOpen { get; }

    /// <summary>Nominal bit rate in bit/s, or null when the transport has none.</summary>
    int? Bitrate { get; }

    /// <summary>Current health, refreshed on demand.</summary>
    CanTransportStatus Status { get; }

    /// <summary>
    /// Acquire the interface. Must be safe to call when already open (no-op) and
    /// must not transmit anything.
    /// </summary>
    void Open();

    /// <summary>Release the interface. Must be safe to call when already closed.</summary>
    void Close();

    /// <summary>
    /// Transmit one frame exactly as recorded. Implementations must not rewrite
    /// the identifier, DLC or payload, and must not pad or truncate.
    /// </summary>
    /// <returns>The outcome; callers count failures rather than throwing per frame.</returns>
    CanSendResult Send(in CanFrame frame);

    /// <summary>Re-read hardware status without transmitting (requirement 23).</summary>
    CanTransportStatus RefreshStatus();
}

/// <summary>Health of a transport, mapped onto the states requirement 20 asks for.</summary>
public enum CanTransportHealth
{
    NotConnected,
    Ok,
    DriverMissing,
    ApiMissing,
    ChannelInUse,
    BusOff,
    BusHeavy,
    BusLight,
    Error,
}

/// <param name="Health">Coarse state driving the status pill in the UI.</param>
/// <param name="Detail">Human-readable explanation, safe to show verbatim.</param>
/// <param name="RawStatus">Underlying API status code, for the diagnostics page.</param>
public readonly record struct CanTransportStatus(
    CanTransportHealth Health,
    string Detail,
    uint RawStatus = 0)
{
    public bool IsUsable => Health is CanTransportHealth.Ok
        or CanTransportHealth.BusLight or CanTransportHealth.BusHeavy;

    public static CanTransportStatus NotConnected(string detail = "No CAN interface selected") =>
        new(CanTransportHealth.NotConnected, detail);

    public static CanTransportStatus Ok(string detail = "OK") =>
        new(CanTransportHealth.Ok, detail);
}

/// <param name="Success">False when the frame did not reach the bus.</param>
/// <param name="Error">Reason for a failure; empty on success.</param>
/// <param name="TransmitQueueFull">
/// True when the interface accepted nothing because its transmit queue is full.
/// The scheduler treats this as backpressure rather than a hardware fault.
/// </param>
public readonly record struct CanSendResult(bool Success, string Error, bool TransmitQueueFull = false)
{
    public static readonly CanSendResult Sent = new(true, string.Empty);

    public static CanSendResult Failed(string error) => new(false, error);

    public static CanSendResult QueueFull(string error) => new(false, error, true);
}
