using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CANVideoEmulator.Pcan;

/// <summary>
/// The System Diagnostics page's content (requirement 22).
/// </summary>
/// <remarks>
/// Built as an ordered list of name/value lines so the on-screen table and the
/// Copy Diagnostics clipboard text are generated from one source and cannot drift
/// apart. Anything that cannot be determined is reported as "not found" rather
/// than omitted -- on a support call, the absence of PCANBasic.dll is the answer,
/// so it has to be visible.
/// </remarks>
public sealed class DiagnosticsReport
{
    private readonly List<(string Section, string Name, string Value)> _lines = [];

    public IReadOnlyList<(string Section, string Name, string Value)> Lines => _lines;

    public DiagnosticsReport Add(string section, string name, string? value)
    {
        _lines.Add((section, name, string.IsNullOrWhiteSpace(value) ? "(not available)" : value));
        return this;
    }

    public static DiagnosticsReport Collect(DiagnosticsContext context)
    {
        var report = new DiagnosticsReport();
        var entry = Assembly.GetEntryAssembly();

        report
            .Add("Application", "Product", context.ProductName)
            .Add("Application", "Version",
                entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                ?? entry?.GetName().Version?.ToString())
            .Add("Application", "Executable", Environment.ProcessPath)
            .Add("Application", "Base directory", AppContext.BaseDirectory)
            .Add("Application", "Single-file / self-contained",
                DescribeDeployment())

            .Add("System", "Windows version", Environment.OSVersion.VersionString)
            .Add("System", "OS description", RuntimeInformation.OSDescription)
            .Add("System", "OS architecture", RuntimeInformation.OSArchitecture.ToString())
            .Add("System", "Process architecture", RuntimeInformation.ProcessArchitecture.ToString())
            .Add("System", "Machine name", Environment.MachineName)
            .Add("System", "Logical processors", Environment.ProcessorCount.ToString())

            .Add(".NET", "Runtime", RuntimeInformation.FrameworkDescription)
            .Add(".NET", "Runtime identifier", RuntimeInformation.RuntimeIdentifier)
            .Add(".NET", "Server GC", System.Runtime.GCSettings.IsServerGC.ToString())

            .Add("PCAN", "PCAN-Basic.NET assembly", PcanEnvironment.ManagedAssemblyVersion())
            .Add("PCAN", "PCANBasic.dll path",
                PcanEnvironment.NativeLibraryPath() ?? "not found -- PEAK driver setup not installed")
            .Add("PCAN", "PCANBasic.dll version", PcanEnvironment.NativeLibraryVersion())
            .Add("PCAN", "PCAN-Basic API version", context.Availability.ApiVersion)
            .Add("PCAN", "API state", context.Availability.State.ToString())
            .Add("PCAN", "API detail", context.Availability.Detail)
            .Add("PCAN", "Attached PCAN-USB channels",
                context.Availability.Channels.Count.ToString());

        // One row per attribute rather than one concatenated line: on a support
        // call these get read out individually, and Api.GetAttachedChannels is
        // the only place several of them are available at all.
        if (context.Availability.Channels.Count == 0)
        {
            report.Add("PCAN devices", "attached channels",
                "none -- Api.GetAttachedChannels() returned no PCAN-USB device");
        }

        foreach (var channel in context.Availability.Channels)
        {
            var prefix = channel.DisplayName;
            report
                .Add("PCAN devices", $"{prefix} · DeviceName", channel.DeviceName)
                .Add("PCAN devices", $"{prefix} · ChannelHandle",
                    $"{channel.Channel} (0x{(int)channel.Channel:X2})")
                .Add("PCAN devices", $"{prefix} · DeviceID",
                    $"{channel.DeviceId} (0x{channel.DeviceId:X})")
                .Add("PCAN devices", $"{prefix} · ControllerNumber",
                    channel.ControllerNumber.ToString())
                .Add("PCAN devices", $"{prefix} · ChannelCondition",
                    DescribeCondition(channel.Condition))
                .Add("PCAN devices", $"{prefix} · DeviceFeatures",
                    channel.Features.ToString())
                .Add("PCAN devices", $"{prefix} · Channel driver version",
                    PcanEnvironment.ChannelDriverVersion(channel.Channel))
                .Add("PCAN devices", $"{prefix} · Selected",
                    context.Selected is { } selected && selected.Channel == channel.Channel
                        ? "yes"
                        : "no");
        }

        report
            .Add("PCAN", "Selected channel",
                context.Selected?.DisplayName ?? "none selected")
            .Add("PCAN", "Channel status", context.TransportStatus)
            .Add("PCAN", "Configured bit rate", $"{context.BitrateBitsPerSecond / 1000} kbit/s")
            .Add("PCAN", "Active transport", context.TransportName)

            .Add("Scenarios", "Scenario directory", context.ScenarioDirectory)
            .Add("Scenarios", "Scenario count", context.ScenarioCount.ToString())
            .Add("Scenarios", "Playlist count", context.PlaylistCount.ToString())
            .Add("Scenarios", "Current scenario", context.CurrentScenario ?? "none loaded")
            .Add("Scenarios", "Current CAN bus",
                context.CurrentBus?.ToString() ?? "none")
            // Two separate facts (phase 2 requirement 10): what the car's bus ran
            // at, and what this bench is set up to replay at.
            .Add("Scenarios", "Scenario original bit rate",
                context.ScenarioOriginalBitrate is { } original
                    ? $"{original / 1000} kbit/s"
                    : "unknown -- not documented by the dataset")
            .Add("Scenarios", "Scenario playback bit rate",
                context.ScenarioPlaybackBitrate is { } playback
                    ? $"{playback / 1000} kbit/s"
                    : "not stated by the package")
            .Add("Scenarios", "Load errors",
                context.ScenarioLoadErrors.Count == 0
                    ? "none"
                    : string.Join(" | ", context.ScenarioLoadErrors))

            .Add("Configuration", "Config file", context.ConfigPath)
            .Add("Configuration", "Log directory", context.LogDirectory)
            .Add("Configuration", "Transition gap",
                $"{context.TransitionGapMs} ms")
            .Add("Configuration", "Video sync tolerance",
                $"{context.VideoSyncToleranceMs} ms");

        return report;
    }

    /// <summary>Spell out what a ChannelCondition means for a technician.</summary>
    private static string DescribeCondition(Peak.Can.Basic.ChannelCondition condition) =>
        condition switch
        {
            Peak.Can.Basic.ChannelCondition.ChannelAvailable =>
                "ChannelAvailable -- free, can be initialised",
            Peak.Can.Basic.ChannelCondition.ChannelOccupied =>
                "ChannelOccupied -- another application holds this channel",
            Peak.Can.Basic.ChannelCondition.ChannelPCanView =>
                "ChannelPCanView -- open in PCAN-View but still shareable",
            Peak.Can.Basic.ChannelCondition.ChannelUnavailable =>
                "ChannelUnavailable -- hardware not present",
            _ => condition.ToString(),
        };

    private static string DescribeDeployment()
    {
        // An assembly bundled into a single-file app reports an empty Location.
        // That is precisely the signal wanted here, so the single-file analyzer's
        // warning about it is suppressed rather than worked around.
#pragma warning disable IL3000
        var bundled = string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000

        // A self-contained app carries its own runtime beside the executable; a
        // framework-dependent one resolves it from the shared install.
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var selfContained = runtimeDirectory.StartsWith(AppContext.BaseDirectory,
            StringComparison.OrdinalIgnoreCase);

        return $"single-file: {bundled}, self-contained: {selfContained} " +
               $"(runtime from {runtimeDirectory})";
    }

    /// <summary>Plain text for the Copy Diagnostics button (requirement 22).</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        var width = _lines.Count == 0 ? 0 : _lines.Max(l => l.Name.Length);
        string? section = null;

        builder.AppendLine("CANVideoEmulator -- System Diagnostics");
        builder.AppendLine($"Collected {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        foreach (var line in _lines)
        {
            if (line.Section != section)
            {
                section = line.Section;
                builder.AppendLine();
                builder.AppendLine($"[{section}]");
            }

            builder.AppendLine($"  {line.Name.PadRight(width)} : {line.Value}");
        }

        return builder.ToString();
    }
}

/// <summary>Everything the report needs that only the running app knows.</summary>
public sealed class DiagnosticsContext
{
    public string ProductName { get; init; } = "CANVideoEmulator";
    public PcanAvailability Availability { get; init; }
    public PcanChannelDescriptor? Selected { get; init; }
    public string TransportName { get; init; } = "none";
    public string TransportStatus { get; init; } = "not open";
    public int BitrateBitsPerSecond { get; init; } = 500_000;
    public string ScenarioDirectory { get; init; } = string.Empty;
    public int ScenarioCount { get; init; }
    public int PlaylistCount { get; init; }
    public string? CurrentScenario { get; init; }
    public int? CurrentBus { get; init; }
    /// <summary>The recorded vehicle's own bus rate; null when undocumented.</summary>
    public int? ScenarioOriginalBitrate { get; init; }

    /// <summary>The bench rate the package expects to be replayed at.</summary>
    public int? ScenarioPlaybackBitrate { get; init; }
    public IReadOnlyList<string> ScenarioLoadErrors { get; init; } = [];
    public string ConfigPath { get; init; } = string.Empty;
    public string LogDirectory { get; init; } = string.Empty;
    public double TransitionGapMs { get; init; }
    public double VideoSyncToleranceMs { get; init; }
}
