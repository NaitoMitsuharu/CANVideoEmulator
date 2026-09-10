using CANVideoEmulator.Core.Can;
using Peak.Can.Basic;

namespace CANVideoEmulator.Pcan;

/// <summary>
/// Owns PCAN detection, selection and hot-plug recovery for the application.
/// </summary>
/// <remarks>
/// <para>Requirement 19: detect on start-up and auto-select when exactly one
/// PCAN-USB is attached. Requirement 21: survive the device being unplugged and
/// plugged back in without restarting the app.</para>
///
/// <para>Hot-plug is handled by polling <c>GetAttachedChannels</c> rather than by
/// listening for WM_DEVICECHANGE: the poll is a cheap driver call, needs no
/// window handle (so this project stays UI-free), and also picks up the case
/// where the PEAK driver is installed while the application is already
/// running.</para>
/// </remarks>
public sealed class PcanManager : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _pollInterval;
    private readonly Timer _timer;
    private readonly Func<PcanAvailability> _probe;

    private PcanAvailability _availability;
    private PcanChannelDescriptor? _selected;
    private PcanBasicTransport? _transport;
    private int _bitrate = 500_000;
    private bool _disposed;

    public PcanManager(TimeSpan? pollInterval = null, Func<PcanAvailability>? probe = null)
    {
        _probe = probe ?? PcanEnvironment.Probe;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _availability = _probe();
        AutoSelect();
        _timer = new Timer(_ => Poll(), null, _pollInterval, _pollInterval);
    }

    /// <summary>Raised when the attached-channel list or the API state changes.</summary>
    public event EventHandler<PcanAvailability>? AvailabilityChanged;

    /// <summary>Raised when the selected channel changes, including to none.</summary>
    public event EventHandler<PcanChannelDescriptor?>? SelectionChanged;

    /// <summary>Raised when the open channel goes away (device unplugged).</summary>
    public event EventHandler<string>? ConnectionLost;

    public PcanAvailability Availability
    {
        get { lock (_gate) { return _availability; } }
    }

    public PcanChannelDescriptor? Selected
    {
        get { lock (_gate) { return _selected; } }
    }

    public PcanBasicTransport? Transport
    {
        get { lock (_gate) { return _transport; } }
    }

    /// <summary>Bit rate used when opening a channel. Changing it closes any open one.</summary>
    public int Bitrate
    {
        get { lock (_gate) { return _bitrate; } }
        set
        {
            if (!PcanBasicTransport.SupportedBitrates.Contains(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"{value} bit/s is not offered by PCAN-Basic");
            }

            lock (_gate)
            {
                if (_bitrate == value)
                {
                    return;
                }

                _bitrate = value;
                CloseTransportLocked();
            }
        }
    }

    /// <summary>Re-probe now; also used by automatic hot-plug polling.</summary>
    public PcanAvailability Refresh()
    {
        return ApplyProbe(_probe());
    }

    private PcanAvailability ApplyProbe(PcanAvailability probed)
    {
        bool changed;
        PcanChannelDescriptor? lost = null;
        lock (_gate)
        {
            if (_disposed) return _availability;
            changed = !SameChannels(_availability, probed);
            _availability = probed;
            if (_selected is { } current && !probed.Channels.Any(c => c.Channel == current.Channel))
            {
                lost = current;
                CloseTransportLocked();
                _selected = null;
            }
        }

        if (lost is { } disconnected)
        {
            SelectionChanged?.Invoke(this, null);
            ConnectionLost?.Invoke(this, $"{disconnected.DisplayName} was disconnected.");
        }
        AutoSelect();
        if (changed)
        {
            AvailabilityChanged?.Invoke(this, probed);
        }

        return probed;
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            // GetAttachedChannels is a global PCAN-Basic call. The native
            // driver serializes it with writes, so polling it while replaying
            // produces a periodic CAN transmission gap. An open channel is
            // monitored by CanScheduler.RefreshStatus instead.
            if (_transport is { IsOpen: true })
            {
                return;
            }
        }

        try
        {
            Refresh();
        }
        catch (Exception)
        {
            // A poll must never take the application down (requirement 64).
        }
    }

    private static bool SameChannels(PcanAvailability left, PcanAvailability right)
    {
        if (left.State != right.State || left.Channels.Count != right.Channels.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Channels.Count; i++)
        {
            if (left.Channels[i].Channel != right.Channels[i].Channel ||
                left.Channels[i].Condition != right.Channels[i].Condition)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Requirement 19: pick automatically only when the choice is unambiguous.</summary>
    private void AutoSelect()
    {
        PcanChannelDescriptor? chosen = null;
        bool changed = false;

        lock (_gate)
        {
            var channels = _availability.Channels;
            if (_selected is { } current && channels.Any(c => c.Channel == current.Channel))
            {
                // Keep the user's choice, but refresh its condition.
                var refreshed = channels.First(c => c.Channel == current.Channel);
                if (!refreshed.Equals(current))
                {
                    _selected = refreshed;
                    chosen = refreshed;
                    changed = true;
                }
            }
            else if (channels.Count == 1)
            {
                _selected = channels[0];
                chosen = channels[0];
                changed = true;
            }
            else if (_selected is not null && channels.Count == 0)
            {
                _selected = null;
                changed = true;
            }
        }

        if (changed)
        {
            SelectionChanged?.Invoke(this, chosen);
        }
    }

    /// <summary>Choose a channel explicitly (the dropdown when several are present).</summary>
    public void Select(PcanChannelDescriptor channel)
    {
        lock (_gate)
        {
            if (_selected is { } current && current.Channel == channel.Channel)
            {
                return;
            }

            CloseTransportLocked();
            _selected = channel;
        }

        SelectionChanged?.Invoke(this, channel);
    }

    /// <summary>
    /// Open the selected channel, creating the transport if needed.
    /// Does not transmit.
    /// </summary>
    public PcanBasicTransport OpenSelected()
    {
        lock (_gate)
        {
            if (_selected is not { } channel)
            {
                throw new PcanTransportException(
                    "No PCAN-USB channel is selected. Connect a PCAN-USB, wait for detection, then press PLAY.");
            }

            if (_transport is { IsOpen: true } existing)
            {
                return existing;
            }

            CloseTransportLocked();
            var transport = new PcanBasicTransport(channel.Channel, _bitrate);
            transport.Open();
            _transport = transport;
            return transport;
        }
    }

    /// <summary>Release the channel. Called on shutdown (requirement 24).</summary>
    public void CloseTransport()
    {
        lock (_gate) { CloseTransportLocked(); }
    }

    private void CloseTransportLocked()
    {
        _transport?.Dispose();
        _transport = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        CloseTransport();
    }
}
