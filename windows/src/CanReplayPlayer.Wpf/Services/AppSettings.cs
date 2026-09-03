using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanReplayPlayer.Wpf.Services;

/// <summary>
/// User settings, persisted between runs (requirement 63).
/// </summary>
/// <remarks>
/// Two locations are supported. Normally the file lives under the user's profile
/// (<c>%APPDATA%\CanReplayPlayer\config.json</c>), which is writable without
/// elevation and survives an application upgrade. If a file named
/// <c>portable.txt</c> sits next to the executable, settings and logs move beside
/// the EXE instead -- so the portable ZIP can be carried to the exhibition on a
/// USB stick and keep its channel and scenario directory with it.
/// </remarks>
public sealed class AppSettings
{
    public const string PortableMarkerFileName = "portable.txt";

    [JsonPropertyName("scenario_directory")]
    public string? ScenarioDirectory { get; set; }

    /// <summary>Last selected PCAN channel, e.g. "Usb01".</summary>
    [JsonPropertyName("pcan_channel")]
    public string? PcanChannel { get; set; }

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; } = 500_000;

    [JsonPropertyName("playlist_id")]
    public string? PlaylistId { get; set; }

    [JsonPropertyName("transition_gap_ms")]
    public double TransitionGapMs { get; set; } = 350;

    [JsonPropertyName("video_sync_tolerance_ms")]
    public double VideoSyncToleranceMs { get; set; } = 250;

    [JsonPropertyName("video_sync_cooldown_ms")]
    public double VideoSyncCooldownMs { get; set; } = 2000;

    [JsonPropertyName("skip_step_seconds")]
    public double SkipStepSeconds { get; set; } = 10;

    [JsonPropertyName("loop_mode")]
    public string LoopMode { get; set; } = "PlaylistAdvance";

    [JsonPropertyName("include_tx_echo")]
    public bool IncludeTxEcho { get; set; } = true;

    [JsonPropertyName("first_run_completed")]
    public bool FirstRunCompleted { get; set; }

    [JsonPropertyName("window_width")] public double WindowWidth { get; set; } = 1500;
    [JsonPropertyName("window_height")] public double WindowHeight { get; set; } = 950;
    [JsonPropertyName("window_left")] public double? WindowLeft { get; set; }
    [JsonPropertyName("window_top")] public double? WindowTop { get; set; }
    [JsonPropertyName("window_maximized")] public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public string? LoadError { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static bool IsPortable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName));

    public static string RootDirectory => IsPortable
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanReplayPlayer");

    public static string ConfigPath => Path.Combine(RootDirectory, "config.json");

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>Where scenarios live when the user has not chosen a folder.</summary>
    public static string DefaultScenarioDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Scenarios");

    public string EffectiveScenarioDirectory =>
        string.IsNullOrWhiteSpace(ScenarioDirectory)
            ? DefaultScenarioDirectory
            : ScenarioDirectory;

    /// <summary>
    /// Load settings, falling back to defaults. A corrupt file is never fatal
    /// (requirement 64): the reason is recorded and defaults are used.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(ConfigPath), Options);
            return settings ?? new AppSettings();
        }
        catch (Exception error) when (error is JsonException or IOException
                                          or UnauthorizedAccessException)
        {
            return new AppSettings
            {
                LoadError = $"{ConfigPath} could not be read ({error.Message}); " +
                            "default settings are in use.",
            };
        }
    }

    /// <summary>Save settings. Returns the failure reason, or null on success.</summary>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            // Write then move, so a crash mid-write cannot leave a truncated file
            // that would be discarded on the next start.
            var temporary = ConfigPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, ConfigPath, overwrite: true);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"settings could not be saved to {ConfigPath}: {error.Message}";
        }
    }
}
