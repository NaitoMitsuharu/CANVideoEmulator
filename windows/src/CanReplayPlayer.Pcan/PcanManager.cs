using CanReplayPlayer.Core.Can;
using Peak.Can.Basic;

namespace CanReplayPlayer.Pcan;

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

    private PcanAvailability _availability;
    private PcanChannelDescriptor? _selected;
    private PcanBasicTransport? _transport;
    private int _bitrate = 500_000;
    private bool _disposed;

    public PcanManager(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _availability = PcanEnvironment.Probe();
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

    /// <summary>Re-probe now (the Refresh button, requirement 21).</summary>
    public PcanAvailability Refresh()
    {
        var probed = PcanEnvironment.Probe();
        bool changed;
        lock (_gate)
        {
            changed = !SameChannels(_availability, probed);
            _availability = probed;
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

        try
        {
            var probed = PcanEnvironment.Probe();
            bool changed;
            PcanChannelDescriptor? selected;
            lock (_gate)
            {
                changed = !SameChannels(_availability, probed);
                _availability = probed;
                selected = _selected;
            }

            if (!changed)
            {
                return;
            }

            // The channel we were using has gone: release it so a later replug
            // starts from a clean handle rather than a stale one.
            if (selected is { } current &&
                !probed.Channels.Any(c => c.Channel == current.Channel))
            {
                lock (_gate)
                {
                    CloseTransportLocked();
                    _selected = null;
                }

                SelectionChanged?.Invoke(this, null);
                ConnectionLost?.Invoke(this,
                    $"{current.DisplayName} was disconnected.");
            }

            AutoSelect();
            AvailabilityChanged?.Invoke(this, probed);
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
                    "No PCAN-USB channel is selected. Connect a PCAN-USB and press Refresh.");
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

    /// <summary>
    /// TEST CONNECTION (requirement 23): initialise, read status, release.
    /// Explicitly transmits nothing.
    /// </summary>
    public PcanTestResult TestConnection()
    {
        PcanChannelDescriptor channel;
        int bitrate;
        lock (_gate)
        {
            if (_selected is not { } selected)
            {
                return new PcanTestResult(false, "No PCAN-USB channel is selected.",
                    null, null, null);
            }

            channel = selected;
            bitrate = _bitrate;
        }

        var alreadyOpen = _transport is { IsOpen: true };
        PcanBasicTransport? probe = null;
        try
        {
            var transport = alreadyOpen ? _transport! : probe = new PcanBasicTransport(
                channel.Channel, bitrate);

            if (!alreadyOpen)
            {
                transport.Open();
            }

            var status = transport.RefreshStatus();
            var driverVersion = PcanEnvironment.ChannelDriverVersion(channel.Channel);

            return new PcanTestResult(
                status.IsUsable,
                status.IsUsable
                    ? $"{channel.DisplayName} initialised at {bitrate / 1000} kbit/s; " +
                      $"status {status.Detail}. No CAN frames were transmitted."
                    : $"{channel.DisplayName} reported {status.Detail}.",
                status,
                driverVersion,
                PcanEnvironment.ApiVersion());
        }
        catch (PcanTransportException error)
        {
            return new PcanTestResult(false, error.Message, null, null,
                PcanEnvironment.ApiVersion());
        }
        catch (Exception error)
        {
            return new PcanTestResult(false,
                $"Unexpected failure testing {channel.DisplayName}: {error.Message}",
                null, null, null);
        }
        finally
        {
            // Leave the channel exactly as we found it.
            probe?.Dispose();
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

/// <param name="Success">True when the channel initialised and reported a usable bus.</param>
/// <param name="Message">Text to show the user verbatim.</param>
public readonly record struct PcanTestResult(
    bool Success,
    string Message,
    CanTransportStatus? Status,
    string? ChannelDriverVersion,
    string? ApiVersion);
