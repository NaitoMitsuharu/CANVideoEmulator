using CANVideoEmulator.Core.Can;
using Peak.Can.Basic;

namespace CANVideoEmulator.Pcan;

/// <summary>
/// <see cref="ICanTransport"/> over PEAK's PCAN-Basic API (requirement 17).
/// </summary>
/// <remarks>
/// <para>Classical CAN only (requirement 25): <see cref="Api.Initialize(PcanChannel, Bitrate)"/>
/// with a plain <see cref="Bitrate"/>, and messages built with <c>isFD: false</c>.
/// A frame whose DLC exceeds 8 is refused rather than truncated -- silently
/// shortening a payload would put data on the bus that the vehicle never sent.</para>
///
/// <para>The channel is initialised lazily by <see cref="Open"/> and always
/// released by <see cref="Close"/>, which the application calls on shutdown
/// (requirement 24).</para>
/// </remarks>
public sealed class PcanBasicTransport : ICanTransport
{
    private readonly object _gate = new();
    private readonly PcanChannel _channel;
    private readonly Bitrate _bitrate;
    private readonly PcanMessage _message = new(0, MessageType.Standard, 0, new byte[8], isFD: false);

    private bool _open;
    private CanTransportStatus _status;

    public PcanBasicTransport(PcanChannel channel, int bitrateBitsPerSecond = 500_000)
    {
        _channel = channel;
        _bitrate = ToBitrate(bitrateBitsPerSecond);
        Bitrate = bitrateBitsPerSecond;
        Name = ToDisplayName(channel);
        _status = CanTransportStatus.NotConnected($"{Name} not initialised");
    }

    public string Name { get; }

    public bool IsOpen
    {
        get { lock (_gate) { return _open; } }
    }

    public int? Bitrate { get; }

    public PcanChannel Channel => _channel;

    public CanTransportStatus Status
    {
        get { lock (_gate) { return _status; } }
    }

    /// <summary>Bit rates the PCAN-Basic API accepts for classical CAN.</summary>
    public static IReadOnlyList<int> SupportedBitrates { get; } =
    [
        1_000_000, 800_000, 500_000, 250_000, 125_000, 100_000,
        95_000, 83_000, 50_000, 47_000, 33_000, 20_000, 10_000, 5_000,
    ];

    internal static Bitrate ToBitrate(int bitsPerSecond) => bitsPerSecond switch
    {
        1_000_000 => Peak.Can.Basic.Bitrate.Pcan1000,
        800_000 => Peak.Can.Basic.Bitrate.Pcan800,
        500_000 => Peak.Can.Basic.Bitrate.Pcan500,
        250_000 => Peak.Can.Basic.Bitrate.Pcan250,
        125_000 => Peak.Can.Basic.Bitrate.Pcan125,
        100_000 => Peak.Can.Basic.Bitrate.Pcan100,
        95_000 => Peak.Can.Basic.Bitrate.Pcan95,
        83_000 => Peak.Can.Basic.Bitrate.Pcan83,
        50_000 => Peak.Can.Basic.Bitrate.Pcan50,
        47_000 => Peak.Can.Basic.Bitrate.Pcan47,
        33_000 => Peak.Can.Basic.Bitrate.Pcan33,
        20_000 => Peak.Can.Basic.Bitrate.Pcan20,
        10_000 => Peak.Can.Basic.Bitrate.Pcan10,
        5_000 => Peak.Can.Basic.Bitrate.Pcan5,
        _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSecond),
            $"{bitsPerSecond} bit/s is not one of the bit rates PCAN-Basic offers " +
            $"({string.Join(", ", SupportedBitrates)})"),
    };

    internal static string ToDisplayName(PcanChannel channel)
    {
        var digits = new string(channel.ToString().Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var index) ? $"PCAN_USBBUS{index}" : channel.ToString();
    }

    public void Open()
    {
        lock (_gate)
        {
            if (_open)
            {
                return;
            }

            PcanStatus status;
            try
            {
                status = Api.Initialize(_channel, _bitrate);
            }
            catch (Exception error) when (PcanEnvironment.IsNativeLoadFailure(error))
            {
                _status = new CanTransportStatus(CanTransportHealth.ApiMissing,
                    "PCANBasic.dll is not installed. Run the PEAK-System driver setup.");
                throw new PcanTransportException(_status.Detail, error);
            }

            if (status != PcanStatus.OK)
            {
                _status = MapStatus(status, initialising: true);
                throw new PcanTransportException(
                    $"could not open {Name}: {_status.Detail}");
            }

            _open = true;
            _status = CanTransportStatus.Ok($"{Name} open at {Bitrate / 1000} kbit/s");
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (!_open)
            {
                return;
            }

            _open = false;
            try
            {
                Api.Uninitialize(_channel);
            }
            catch (Exception error) when (PcanEnvironment.IsNativeLoadFailure(error))
            {
                // The driver disappeared underneath us (device unplugged, driver
                // uninstalled). There is nothing left to release.
            }

            _status = CanTransportStatus.NotConnected($"{Name} released");
        }
    }

    public CanSendResult Send(in CanFrame frame)
    {
        if (frame.Dlc > 8)
        {
            // Requirement 25: classical CAN only, and requirement 70 forbids
            // altering the payload, so truncating is not an option.
            return CanSendResult.Failed(
                $"frame {frame.IdText()} has DLC {frame.Dlc}; this transport sends " +
                "classical CAN only and will not truncate");
        }

        lock (_gate)
        {
            if (!_open)
            {
                return CanSendResult.Failed($"{Name} is not open");
            }

            var type = frame.IsExtended ? MessageType.Extended : MessageType.Standard;
            if (frame.IsRemoteRequest)
            {
                type |= MessageType.RemoteRequest;
            }

            // Api.Write completes before returning and every send is serialized
            // by _gate, so this one mutable message is safe to reuse. Avoiding a
            // PcanMessage and byte[] allocation for every frame prevents Gen0 GC
            // pauses from becoming visible as periodic gaps on the CAN bus.
            _message.ID = frame.CanId;
            _message.MsgType = type;
            _message.DLC = frame.Dlc;
            var data = frame.Data;
            for (var index = 0; index < data.Length; index++)
            {
                _message.Data[index] = data[index];
            }

            PcanStatus status;
            try
            {
                status = Api.Write(_channel, _message);
            }
            catch (Exception error) when (PcanEnvironment.IsNativeLoadFailure(error))
            {
                lock (_gate)
                {
                    _open = false;
                    _status = new CanTransportStatus(CanTransportHealth.ApiMissing,
                        "PCANBasic.dll disappeared while sending");
                }

                return CanSendResult.Failed(_status.Detail);
            }

            if (status == PcanStatus.OK)
            {
                return CanSendResult.Sent;
            }

            _status = MapStatus(status, initialising: false);
            var detail = _status.Detail;
            // Bus faults take precedence over a simultaneous full-queue flag.
            if (_status.IsUsable && (status & (PcanStatus.TransmitQueueFull | PcanStatus.TransmitBufferFull)) != 0)
                return CanSendResult.QueueFull(detail);

            return CanSendResult.Failed(detail);
        }
    }

    /// <summary>
    /// Read hardware status without transmitting.
    /// </summary>
    public CanTransportStatus RefreshStatus()
    {
        lock (_gate)
        {
            if (!_open)
            {
                _status = CanTransportStatus.NotConnected($"{Name} is not open");
                return _status;
            }

            try
            {
                _status = MapStatus(Api.GetStatus(_channel), initialising: false);
            }
            catch (Exception error) when (PcanEnvironment.IsNativeLoadFailure(error))
            {
                _open = false;
                _status = new CanTransportStatus(CanTransportHealth.ApiMissing,
                    "PCANBasic.dll is no longer available");
            }

            return _status;
        }
    }

    /// <summary>Map a PCAN status onto the states requirement 20 distinguishes.</summary>
    internal static CanTransportStatus MapStatus(PcanStatus status, bool initialising)
    {
        var detail = PcanEnvironment.DescribeStatus(status);
        // PCAN statuses are flags. Preserve bus errors even when queue flags coexist.
        var health = status switch
        {
            PcanStatus.OK => CanTransportHealth.Ok,
            _ when (status & PcanStatus.BusOff) != 0 => CanTransportHealth.BusOff,
            _ when (status & PcanStatus.BusPassive) != 0 => CanTransportHealth.BusPassive,
            _ when (status & PcanStatus.BusHeavy) != 0 => CanTransportHealth.BusHeavy,
            _ when (status & PcanStatus.BusLight) != 0 => CanTransportHealth.BusLight,
            PcanStatus.IllegalHardwareHandle or PcanStatus.IllegalNetHandle or PcanStatus.IllegalClientHandle => CanTransportHealth.NotConnected,
            PcanStatus.NoDriver => CanTransportHealth.DriverMissing,
            PcanStatus.HardwareInUse or PcanStatus.NetInUse => CanTransportHealth.ChannelInUse,
            PcanStatus.Initialize => CanTransportHealth.NotConnected,
            // Queue capacity does not by itself mean the controller is broken.
            _ when (status & ~(PcanStatus.TransmitQueueFull | PcanStatus.TransmitBufferFull |
                              PcanStatus.ReceiveQueueEmpty | PcanStatus.ReceiveQueueOverrun | PcanStatus.Overrun)) == 0 => CanTransportHealth.Ok,
            _ => CanTransportHealth.Error,
        };

        if (health == CanTransportHealth.ChannelInUse)
        {
            detail = $"{detail} -- another application (often PCAN-View) already " +
                     "has this channel open. Close it and press PLAY again.";
        }
        else if (health == CanTransportHealth.DriverMissing)
        {
            detail = $"{detail} -- install the PEAK-System Windows driver setup.";
        }

        return new CanTransportStatus(health, detail, (uint)status);
    }

    public void Dispose() => Close();
}

public sealed class PcanTransportException : Exception
{
    public PcanTransportException(string message) : base(message)
    {
    }

    public PcanTransportException(string message, Exception inner) : base(message, inner)
    {
    }
}
