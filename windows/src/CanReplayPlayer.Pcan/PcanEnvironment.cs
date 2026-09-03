using System.Diagnostics;
using Peak.Can.Basic;

namespace CanReplayPlayer.Pcan;

/// <summary>
/// Whether PCAN can be used at all on this machine, and which channels exist.
/// </summary>
/// <remarks>
/// <para>Requirement 18: the PCAN-Basic.NET NuGet package ships the managed
/// wrapper only. The native <c>PCANBasic.dll</c> and the PCAN-USB device driver
/// both come from PEAK's Windows Driver Setup, so a fresh exhibition PC can have
/// this application installed and still be unable to talk to CAN.</para>
///
/// <para>That failure has to be distinguishable from "the cable is unplugged"
/// (requirement 20), so every call into the API goes through here and the two
/// distinct native failures -- the DLL missing entirely, and the driver
/// reporting no hardware -- are reported as different states. The DLL is loaded
/// lazily by the wrapper's static constructor, so a missing DLL surfaces as a
/// <see cref="TypeInitializationException"/> wrapping
/// <see cref="DllNotFoundException"/>; both shapes are handled.</para>
/// </remarks>
public static class PcanEnvironment
{
    private const string NativeLibrary = "PCANBasic.dll";

    /// <summary>Probe the API without transmitting anything.</summary>
    public static PcanAvailability Probe()
    {
        // Check the file system first. Measured on a machine with no PEAK driver
        // installed, PCAN-Basic.NET 5.1.0 does not raise DllNotFoundException --
        // it raises its own exception saying it "could not be verified whether
        // the current loaded native library PCANBasic.dll is genuine PEAK-System
        // software". That message is indistinguishable from a real tampering
        // check, so the presence of the file is what actually separates
        // "driver not installed" from "driver installed but unhappy", and
        // requirement 20 needs those told apart.
        if (NativeLibraryPath() is null)
        {
            return new PcanAvailability(
                PcanApiState.NativeLibraryMissing,
                $"{NativeLibrary} was not found. Install the PEAK-System Windows driver " +
                "setup (PCAN-USB driver + PCAN-Basic API), then press Refresh.",
                null, null, []);
        }

        try
        {
            var status = Api.GetAttachedChannels(out PcanChannelInformation[] channels);
            if (status != PcanStatus.OK)
            {
                return new PcanAvailability(
                    PcanApiState.ApiError,
                    DescribeStatus(status),
                    NativeLibraryVersion(),
                    ApiVersion(),
                    []);
            }

            var descriptors = channels
                .Where(c => c.DeviceType == PcanDevice.PcanUsb)
                .Select(Describe)
                .OrderBy(c => c.ChannelName, StringComparer.Ordinal)
                .ToArray();

            return new PcanAvailability(
                descriptors.Length > 0 ? PcanApiState.Available : PcanApiState.NoDevices,
                descriptors.Length > 0
                    ? $"{descriptors.Length} PCAN-USB channel(s) attached"
                    : "PCAN-Basic is installed but no PCAN-USB device is attached",
                NativeLibraryVersion(),
                ApiVersion(),
                descriptors);
        }
        catch (Exception error) when (IsNativeLoadFailure(error))
        {
            return new PcanAvailability(
                PcanApiState.NativeLibraryMissing,
                $"{NativeLibrary} could not be loaded. Install the PEAK-System Windows " +
                "driver setup (PCAN-USB driver + PCAN-Basic API), then use Refresh. " +
                $"({Root(error).Message})",
                null, null, []);
        }
        catch (Exception error)
        {
            return new PcanAvailability(
                PcanApiState.ApiError,
                $"PCAN-Basic could not be queried: {Root(error).Message}",
                NativeLibraryVersion(), null, []);
        }
    }

    /// <summary>
    /// True when the failure is the native library not being present/loadable,
    /// rather than a runtime error from a working API.
    /// </summary>
    internal static bool IsNativeLoadFailure(Exception error)
    {
        var root = Root(error);
        if (root is DllNotFoundException or BadImageFormatException
            or EntryPointNotFoundException)
        {
            return true;
        }

        // PCAN-Basic.NET wraps a missing or unverifiable PCANBasic.dll in its own
        // exception type rather than letting the loader's exception through, so
        // fall back to whether the file is actually there.
        return root is PcanBasicException && NativeLibraryPath() is null;
    }

    internal static Exception Root(Exception error)
    {
        while (error is TypeInitializationException { InnerException: { } inner })
        {
            error = inner;
        }

        return error;
    }

    private static PcanChannelDescriptor Describe(PcanChannelInformation info) => new(
        Channel: info.ChannelHandle,
        ChannelName: info.ChannelHandle.ToString(),
        DeviceName: info.DeviceName ?? "PCAN-USB",
        DeviceId: info.DeviceID,
        ControllerNumber: info.ControllerNumber,
        Condition: info.ChannelCondition,
        Features: info.DeviceFeatures);

    /// <summary>Human-readable text for a status code, from the API itself.</summary>
    public static string DescribeStatus(PcanStatus status)
    {
        try
        {
            if (Api.GetErrorText(status, out var text, OutputLanguage.English)
                == PcanStatus.OK && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        catch (Exception error) when (IsNativeLoadFailure(error))
        {
            // Fall through to the enum name.
        }

        return status.ToString();
    }

    /// <summary>File version of the native PCANBasic.dll, or null if not found.</summary>
    public static string? NativeLibraryVersion()
    {
        foreach (var directory in NativeSearchPaths())
        {
            var path = Path.Combine(directory, NativeLibrary);
            try
            {
                if (File.Exists(path))
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    return string.IsNullOrWhiteSpace(info.FileVersion) ? "unknown" : info.FileVersion;
                }
            }
            catch (IOException)
            {
                // Unreadable path; keep looking.
            }
        }

        return null;
    }

    /// <summary>Where the native PCANBasic.dll is found once the driver is installed.</summary>
    public static string? NativeLibraryPath()
    {
        foreach (var directory in NativeSearchPaths())
        {
            var path = Path.Combine(directory, NativeLibrary);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> NativeSearchPaths()
    {
        yield return AppContext.BaseDirectory;
        yield return Environment.SystemDirectory;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Path.Combine(windows, "SysWOW64");

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry.Trim();
        }
    }

    /// <summary>PCAN-Basic API version reported by the driver, or null.</summary>
    public static string? ApiVersion()
    {
        try
        {
            // PcanChannel.None is the API-wide handle for global parameters.
            return Api.GetValue(PcanChannel.None, PcanParameter.ApiVersion, out string value)
                == PcanStatus.OK ? value?.Trim() : null;
        }
        catch (Exception error) when (IsNativeLoadFailure(error))
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Version of the driver serving a specific channel, or null.</summary>
    public static string? ChannelDriverVersion(PcanChannel channel)
    {
        try
        {
            return Api.GetValue(channel, PcanParameter.ChannelVersion, out string value)
                == PcanStatus.OK ? value?.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Managed PCAN-Basic.NET assembly version, for the diagnostics page.</summary>
    public static string ManagedAssemblyVersion() =>
        typeof(Api).Assembly.GetName().Version?.ToString() ?? "unknown";
}

public enum PcanApiState
{
    /// <summary>PCANBasic.dll is not installed: the PEAK driver setup has not been run.</summary>
    NativeLibraryMissing,

    /// <summary>The API works but reports no attached PCAN-USB device.</summary>
    NoDevices,

    /// <summary>The API works and at least one PCAN-USB channel is present.</summary>
    Available,

    /// <summary>The API is installed but returned an error.</summary>
    ApiError,
}

/// <param name="State">What the probe concluded.</param>
/// <param name="Detail">Message safe to show in the UI verbatim.</param>
/// <param name="NativeVersion">PCANBasic.dll file version, when found.</param>
/// <param name="ApiVersion">PCAN-Basic API version reported by the driver.</param>
/// <param name="Channels">Attached PCAN-USB channels.</param>
public readonly record struct PcanAvailability(
    PcanApiState State,
    string Detail,
    string? NativeVersion,
    string? ApiVersion,
    IReadOnlyList<PcanChannelDescriptor> Channels)
{
    public bool DriverInstalled => State != PcanApiState.NativeLibraryMissing;

    /// <summary>Requirement 19: auto-select when exactly one device is attached.</summary>
    public PcanChannelDescriptor? SingleChannel =>
        Channels.Count == 1 ? Channels[0] : null;
}

/// <param name="Channel">Handle to pass to the API.</param>
/// <param name="ChannelName">e.g. "Usb01"; shown as PCAN_USBBUS1 in the UI.</param>
/// <param name="DeviceName">Model reported by the driver, e.g. "PCAN-USB".</param>
/// <param name="DeviceId">User-assignable device id, for telling two units apart.</param>
/// <param name="Condition">Free / occupied / unavailable.</param>
public readonly record struct PcanChannelDescriptor(
    PcanChannel Channel,
    string ChannelName,
    string DeviceName,
    uint DeviceId,
    int ControllerNumber,
    ChannelCondition Condition,
    PcanDeviceFeatures Features)
{
    /// <summary>The PCAN_USBBUSn spelling PEAK's own tools use.</summary>
    public string DisplayName
    {
        get
        {
            var digits = new string(ChannelName.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var index)
                ? $"PCAN_USBBUS{index}"
                : ChannelName;
        }
    }

    public bool IsFree => Condition is ChannelCondition.ChannelAvailable
        or ChannelCondition.ChannelPCanView;

    public bool IsOccupied => Condition == ChannelCondition.ChannelOccupied;

    public override string ToString() =>
        $"{DisplayName} ({DeviceName}, device id {DeviceId}, {Condition})";
}
