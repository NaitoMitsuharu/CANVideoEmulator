using System.Text.Json;
using CANVideoEmulator.Core.Video;

namespace CANVideoEmulator.Tests;

/// <summary>Builds throwaway Scenario Packages on disk for the tests.</summary>
internal sealed class ScenarioFixture : IDisposable
{
    public ScenarioFixture()
    {
        Root = Path.Combine(Path.GetTempPath(),
            "CANVideoEmulatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Write a scenario whose bus timelines each carry <paramref name="frameCount"/> frames at 1 kHz.</summary>
    public string AddScenario(string scenarioId, int frameCount = 100,
                              int[]? buses = null, int defaultBus = 0,
                              double videoCanOffsetMs = 0,
                              int? originalBitrate = null,
                              int? playbackBitrate = 500_000,
                              int? legacyBitrate = null,
                              string[]? tags = null, bool withVideo = true,
                              int formatVersion = 1)
    {
        buses ??= [0, 1];
        var directory = Path.Combine(Root, scenarioId);
        Directory.CreateDirectory(Path.Combine(directory, "can"));

        var canFiles = new Dictionary<string, string>();
        foreach (var bus in buses)
        {
            var name = $"bus_{bus}.canbin";
            File.WriteAllBytes(Path.Combine(directory, "can", name),
                CanTimelineTests.BuildCanBin(bus,
                    Enumerable.Range(0, frameCount)
                        .Select(i => ((uint)(i * 1000),
                            (uint)(0x100 + bus * 0x10 + (i % 3)),
                            new byte[] { (byte)bus, (byte)i }, false, false))
                        .ToArray()));
            canFiles[bus.ToString()] = name;
        }

        if (withVideo)
        {
            // Not a decodable MP4; the tests use FakeVideoPlayer, and the
            // manifest only needs the file to exist.
            File.WriteAllBytes(Path.Combine(directory, "video.mp4"), new byte[64]);
        }

        var manifest = new Dictionary<string, object?>
        {
            ["format_version"] = formatVersion,
            ["scenario_id"] = scenarioId,
            ["title"] = $"Test {scenarioId}",
            ["dataset"] = "comma2k19",
            ["vehicle"] = "Toyota RAV4",
            ["vehicle_make"] = "Toyota",
            ["vehicle_model"] = "RAV4",
            ["vehicle_year"] = null,
            ["route"] = "b0c9d2329ad1606b|2018-08-02--08-34-47",
            ["segment"] = 40,
            ["duration_sec"] = Math.Max(0.001, (frameCount - 1) / 1000.0),
            ["video"] = withVideo ? "video.mp4" : "",
            ["video_fps"] = 20.0,
            ["video_frame_count"] = 1200,
            ["thumbnail"] = "",
            ["available_buses"] = buses,
            ["default_bus"] = defaultBus,
            ["default_bus_reason"] = "test fixture",
            ["original_bitrate"] = originalBitrate,
            ["playback_bitrate"] = playbackBitrate,
            // Only written when a test is exercising the pre-split key.
            ["bitrate"] = legacyBitrate,
            ["video_can_offset_ms"] = videoCanOffsetMs,
            ["dbc_profile"] = "toyota_rav4_2017",
            ["tags"] = tags ?? [],
            ["description"] = "test scenario",
            ["can"] = canFiles,
            ["bus_statistics"] = buses.Select(b => new Dictionary<string, object>
            {
                ["bus"] = b,
                ["frame_count"] = frameCount,
                ["frames_per_second"] = 1000.0,
                ["unique_can_ids"] = 3,
                ["average_dlc"] = 2.0,
                ["estimated_bus_load_bps"] = 63000,
                ["duration_sec"] = (frameCount - 1) / 1000.0,
                ["tx_echo_frame_count"] = 0,
                ["extended_id_frame_count"] = 0,
            }).ToArray(),
        };

        File.WriteAllText(Path.Combine(directory, "scenario.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return directory;
    }

    public void AddPlaylists(params (string Id, string Title, string[] ScenarioIds)[] playlists)
    {
        var document = new Dictionary<string, object?>
        {
            ["format_version"] = 1,
            ["default_playlist"] = playlists.Length > 0 ? playlists[0].Id : null,
            ["playlists"] = playlists.Select(p => new Dictionary<string, object>
            {
                ["playlist_id"] = p.Id,
                ["title"] = p.Title,
                ["description"] = string.Empty,
                ["scenario_ids"] = p.ScenarioIds,
            }).ToArray(),
        };

        File.WriteAllText(Path.Combine(Root, "playlist.json"),
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file left open by a failing test must not mask the real failure.
        }
    }
}

/// <summary>An <see cref="IVideoPlayer"/> that records what it was told to do.</summary>
internal sealed class FakeVideoPlayer : IVideoPlayer
{
    private readonly object _gate = new();

    public string? Source { get; private set; }

    public bool IsReady { get; set; } = true;

    public TimeSpan NaturalDuration { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan Position { get; set; }

    public bool IsPlaying { get; private set; }

    public List<string> Calls { get; } = [];

    public List<TimeSpan> Seeks { get; } = [];

    public event EventHandler? Opened;

    public event EventHandler<string>? Failed;

    public void Open(string path)
    {
        lock (_gate)
        {
            Source = path;
            Position = TimeSpan.Zero;
            Calls.Add($"Open:{Path.GetFileName(path)}");
        }

        Opened?.Invoke(this, EventArgs.Empty);
    }

    public void CloseMedia()
    {
        lock (_gate) { Calls.Add("Close"); Source = null; }
    }

    public void Play()
    {
        lock (_gate) { Calls.Add("Play"); IsPlaying = true; }
    }

    public void Pause()
    {
        lock (_gate) { Calls.Add("Pause"); IsPlaying = false; }
    }

    public void Stop()
    {
        lock (_gate) { Calls.Add("Stop"); IsPlaying = false; Position = TimeSpan.Zero; }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            Calls.Add($"Seek:{position.TotalMilliseconds:F0}");
            Seeks.Add(position);
            Position = position;
        }
    }

    public void RaiseFailure(string message) => Failed?.Invoke(this, message);

    public void Dispose() => Calls.Add("Dispose");
}
