using System.Text.Json;
using System.Text.Json.Serialization;

namespace CANVideoEmulator.Scenarios;

/// <summary>
/// scenario.json as produced by the Scenario Builder (requirement 33).
/// </summary>
public sealed class ScenarioManifest
{
    /// <summary>Bumped when the layout changes incompatibly; see <see cref="SupportedFormatVersion"/>.</summary>
    public const int SupportedFormatVersion = 1;

    [JsonPropertyName("format_version")] public int FormatVersion { get; set; }
    [JsonPropertyName("scenario_id")] public string ScenarioId { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("dataset")] public string Dataset { get; set; } = string.Empty;
    [JsonPropertyName("vehicle")] public string Vehicle { get; set; } = string.Empty;
    [JsonPropertyName("vehicle_make")] public string VehicleMake { get; set; } = string.Empty;
    [JsonPropertyName("vehicle_model")] public string VehicleModel { get; set; } = string.Empty;
    [JsonPropertyName("vehicle_year")] public int? VehicleYear { get; set; }
    [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
    [JsonPropertyName("segment")] public int Segment { get; set; }
    [JsonPropertyName("source_dongle_id")] public string? SourceDongleId { get; set; }
    [JsonPropertyName("duration_sec")] public double DurationSeconds { get; set; }
    [JsonPropertyName("video")] public string Video { get; set; } = string.Empty;
    [JsonPropertyName("video_fps")] public double VideoFps { get; set; }
    [JsonPropertyName("video_frame_count")] public int? VideoFrameCount { get; set; }
    [JsonPropertyName("thumbnail")] public string Thumbnail { get; set; } = string.Empty;
    [JsonPropertyName("available_buses")] public List<int> AvailableBuses { get; set; } = [];
    [JsonPropertyName("default_bus")] public int DefaultBus { get; set; }
    [JsonPropertyName("default_bus_reason")] public string? DefaultBusReason { get; set; }

    /// <summary>
    /// The bit rate of the bus in the vehicle the CAN was recorded from, or null
    /// when nothing documents it.
    /// </summary>
    /// <remarks>
    /// comma2k19 publishes no bit rate and nothing in raw_log.bz2 states one, so
    /// for these scenarios this is always null. Writing 500 kbit/s here because
    /// that is the usual Toyota powertrain rate would be inventing a fact about
    /// the car, which requirement 30 forbids. It is informational only: the
    /// player never configures the interface from it.
    /// </remarks>
    [JsonPropertyName("original_bitrate")] public int? OriginalBitrate { get; set; }

    /// <summary>
    /// The bit rate this package is meant to be replayed at on the bench, in
    /// bit/s, or null when the package does not say.
    /// </summary>
    /// <remarks>
    /// This is a real, chosen number -- the rate the PCAN-USB and the connected CAN network are
    /// configured for -- so it is the one the player checks the interface
    /// against. A mismatch is what produces the warning, and what stops playback
    /// starting on hardware.
    /// </remarks>
    [JsonPropertyName("playback_bitrate")] public int? PlaybackBitrate { get; set; }

    /// <summary>Category the Scenario Builder's analysis recommended this for.</summary>
    [JsonPropertyName("recommended_category")] public string? RecommendedCategory { get; set; }

    /// <summary>
    /// Pre-split packages stored a single <c>bitrate</c>. Read it so older
    /// Scenario directories still load, but treat it as the playback rate --
    /// that is what the player used it for.
    /// </summary>
    [JsonPropertyName("bitrate")] public int? LegacyBitrate { get; set; }

    /// <summary>The rate the interface should be set to, honouring the legacy key.</summary>
    [JsonIgnore]
    public int? EffectivePlaybackBitrate => PlaybackBitrate ?? LegacyBitrate;

    /// <summary>
    /// Signed milliseconds from CAN t=0 to the first video frame, so
    /// <c>videoPosition = scenarioTime - VideoCanOffsetMs/1000</c> (requirement 12).
    /// </summary>
    [JsonPropertyName("video_can_offset_ms")] public double VideoCanOffsetMs { get; set; }

    [JsonPropertyName("dbc_profile")] public string? DbcProfile { get; set; }
    [JsonPropertyName("dbc_primary_file")] public string? DbcPrimaryFile { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    /// <summary>Bus index (as a string key) to .canbin file name under <c>can/</c>.</summary>
    [JsonPropertyName("can")] public Dictionary<string, string> Can { get; set; } = [];

    [JsonPropertyName("bus_statistics")] public List<BusStatistics> BusStatistics { get; set; } = [];
    [JsonPropertyName("build_warnings")] public List<string> BuildWarnings { get; set; } = [];

    /// <summary>
    /// Optional GNSS+IMU sidecar file name (under the scenario directory) that
    /// drives the HUD overlay's map, speed readout and IMU graphs, or null when
    /// the source segment carried no usable GNSS/IMU.
    /// </summary>
    /// <remarks>
    /// Additive within <see cref="SupportedFormatVersion"/>: a build that predates
    /// it leaves the key absent and the overlay simply stays hidden, so no version
    /// bump is needed.
    /// </remarks>
    [JsonPropertyName("telemetry")] public string? Telemetry { get; set; }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static ScenarioManifest Parse(string json, string source)
    {
        ScenarioManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ScenarioManifest>(json, JsonOptions);
        }
        catch (JsonException error)
        {
            throw new ScenarioException($"{source}: scenario.json is not valid JSON: {error.Message}",
                error);
        }

        if (manifest is null)
        {
            throw new ScenarioException($"{source}: scenario.json is empty");
        }

        manifest.Validate(source);
        return manifest;
    }

    private void Validate(string source)
    {
        if (FormatVersion != SupportedFormatVersion)
        {
            throw new ScenarioException(
                $"{source}: scenario format_version {FormatVersion} is not supported by this " +
                $"build (which reads version {SupportedFormatVersion}).");
        }

        if (string.IsNullOrWhiteSpace(ScenarioId))
        {
            throw new ScenarioException($"{source}: scenario_id is missing");
        }

        if (Can.Count == 0)
        {
            throw new ScenarioException($"{source}: scenario declares no CAN buses");
        }

        if (!Can.ContainsKey(DefaultBus.ToString()))
        {
            throw new ScenarioException(
                $"{source}: default_bus is {DefaultBus} but there is no CAN file for that bus " +
                $"(buses present: {string.Join(", ", Can.Keys)})");
        }

        if (DurationSeconds <= 0)
        {
            throw new ScenarioException($"{source}: duration_sec must be positive");
        }
    }
}

/// <summary>Per-bus figures shown when a bus is selected (requirement 29).</summary>
public sealed class BusStatistics
{
    [JsonPropertyName("bus")] public int Bus { get; set; }
    [JsonPropertyName("frame_count")] public long FrameCount { get; set; }
    [JsonPropertyName("frames_per_second")] public double FramesPerSecond { get; set; }
    [JsonPropertyName("unique_can_ids")] public int UniqueCanIds { get; set; }
    [JsonPropertyName("average_dlc")] public double AverageDlc { get; set; }
    [JsonPropertyName("estimated_bus_load_bps")] public long EstimatedBusLoadBps { get; set; }
    [JsonPropertyName("duration_sec")] public double DurationSeconds { get; set; }
    [JsonPropertyName("tx_echo_frame_count")] public long TxEchoFrameCount { get; set; }
    [JsonPropertyName("extended_id_frame_count")] public long ExtendedIdFrameCount { get; set; }
}

public sealed class ScenarioException : Exception
{
    public ScenarioException(string message) : base(message)
    {
    }

    public ScenarioException(string message, Exception inner) : base(message, inner)
    {
    }
}
