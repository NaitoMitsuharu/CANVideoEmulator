using System.Text.Json;
using CanReplayPlayer.Scenarios;
using Xunit;

namespace CanReplayPlayer.Tests;

public class ScenarioLibraryTests
{
    [Fact]
    public void LoadsEveryScenarioDirectory()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001");
        fixture.AddScenario("rav4_002");
        fixture.AddScenario("rav4_003");

        var library = ScenarioLibrary.Load(fixture.Root);

        Assert.Equal(3, library.Scenarios.Count);
        Assert.Empty(library.LoadErrors);
        Assert.Equal(["rav4_001", "rav4_002", "rav4_003"],
            library.Scenarios.Select(s => s.ScenarioId));
    }

    [Fact]
    public void IgnoresDirectoriesWithoutAManifest()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "not_a_scenario"));

        var library = ScenarioLibrary.Load(fixture.Root);
        Assert.Single(library.Scenarios);
    }

    [Fact]
    public void OneBadScenarioDoesNotStopTheOthersLoading()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001");
        var broken = Path.Combine(fixture.Root, "rav4_bad");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "scenario.json"), "{ not json");
        fixture.AddScenario("rav4_003");

        var library = ScenarioLibrary.Load(fixture.Root);

        Assert.Equal(2, library.Scenarios.Count);
        Assert.Single(library.LoadErrors);
        Assert.Contains("rav4_bad", library.LoadErrors[0]);
    }

    [Fact]
    public void RejectsAnUnsupportedManifestVersionWithAClearMessage()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001", formatVersion: 99);

        var library = ScenarioLibrary.Load(fixture.Root);
        Assert.Empty(library.Scenarios);
        Assert.Contains("format_version 99", library.LoadErrors[0]);
    }

    [Fact]
    public void RejectsADefaultBusThatHasNoCanFile()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001", buses: [0, 1], defaultBus: 2);

        var library = ScenarioLibrary.Load(fixture.Root);
        Assert.Empty(library.Scenarios);
        Assert.Contains("default_bus is 2", library.LoadErrors[0]);
    }

    [Fact]
    public void MissingDirectoryGivesActionableGuidance()
    {
        var error = Assert.Throws<ScenarioException>(
            () => ScenarioLibrary.Load(Path.Combine(Path.GetTempPath(), "definitely_not_here")));
        Assert.Contains("Scenario directory not found", error.Message);
    }

    [Fact]
    public void WithoutAPlaylistFileEverythingIsOneList()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001");
        fixture.AddScenario("rav4_002");

        var library = ScenarioLibrary.Load(fixture.Root);
        var playlist = library.Playlists.Default;

        Assert.NotNull(playlist);
        Assert.Equal("all", playlist.PlaylistId);
        Assert.Equal(2, playlist.Count);
    }

    [Fact]
    public void PlaylistsAreLoadedAndTheDefaultIsHonoured()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001");
        fixture.AddScenario("rav4_002");
        fixture.AddScenario("rav4_003");
        fixture.AddPlaylists(
            ("featured", "Featured", ["rav4_002", "rav4_001"]),
            ("all", "All", ["rav4_001", "rav4_002", "rav4_003"]));

        var library = ScenarioLibrary.Load(fixture.Root);

        Assert.Equal(2, library.Playlists.Playlists.Count);
        Assert.Equal("featured", library.Playlists.Default!.PlaylistId);
        Assert.Equal(["rav4_002", "rav4_001"], library.Playlists.Default.ScenarioIds);
    }

    [Fact]
    public void PlaylistEntriesPointingAtMissingScenariosAreDroppedAndReported()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001");
        fixture.AddPlaylists(("featured", "Featured", ["rav4_001", "rav4_999"]));

        var library = ScenarioLibrary.Load(fixture.Root);

        Assert.Equal(["rav4_001"], library.Playlists.Find("featured")!.ScenarioIds);
        Assert.Contains(library.LoadErrors, e => e.Contains("rav4_999"));
    }

    [Fact]
    public void ACorruptPlaylistFallsBackToASingleListRatherThanFailing()
    {
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001");
        File.WriteAllText(Path.Combine(fixture.Root, "playlist.json"), "{{{");

        var library = ScenarioLibrary.Load(fixture.Root);

        Assert.Single(library.Scenarios);
        Assert.Equal("all", library.Playlists.Default!.PlaylistId);
        Assert.Contains(library.LoadErrors, e => e.Contains("playlist.json"));
    }

    [Fact]
    public void PlaylistIndexOfIsCaseInsensitive()
    {
        var playlist = new Playlist { ScenarioIds = ["rav4_001", "rav4_002"] };
        Assert.Equal(1, playlist.IndexOf("RAV4_002"));
        Assert.Equal(-1, playlist.IndexOf("nope"));
    }
}

public class ScenarioPackageTests
{
    [Fact]
    public void ExposesManifestValues()
    {
        using var fixture = new ScenarioFixture();
        var directory = fixture.AddScenario("rav4_001", frameCount: 1001,
            buses: [0, 1, 2], defaultBus: 1, videoCanOffsetMs: -37.472,
            tags: ["High Speed"], originalBitrate: null, playbackBitrate: 500_000);

        var package = ScenarioPackage.Load(directory);

        Assert.Equal("rav4_001", package.ScenarioId);
        Assert.Equal([0, 1, 2], package.AvailableBuses);
        Assert.Equal(1, package.DefaultBus);
        Assert.Equal(500_000, package.Manifest.PlaybackBitrate);
        Assert.Equal(500_000, package.Manifest.EffectivePlaybackBitrate);
        Assert.Equal(TimeSpan.FromSeconds(1), package.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(-37.472), package.VideoCanOffset);
        Assert.Equal(["High Speed"], package.Manifest.Tags);
    }

    [Fact]
    public void AnUnknownOriginalBitrateStaysNullRatherThanBeingDefaulted()
    {
        // comma2k19 documents no bus bit rate, so this must never acquire one.
        using var fixture = new ScenarioFixture();
        var directory = fixture.AddScenario("rav4_001", originalBitrate: null);
        var manifest = ScenarioPackage.Load(directory).Manifest;

        Assert.Null(manifest.OriginalBitrate);
        Assert.Equal(500_000, manifest.PlaybackBitrate);
    }

    [Fact]
    public void OriginalAndPlaybackBitratesAreIndependent()
    {
        using var fixture = new ScenarioFixture();
        var directory = fixture.AddScenario("rav4_001",
            originalBitrate: 250_000, playbackBitrate: 500_000);
        var manifest = ScenarioPackage.Load(directory).Manifest;

        Assert.Equal(250_000, manifest.OriginalBitrate);
        Assert.Equal(500_000, manifest.PlaybackBitrate);
        Assert.Equal(500_000, manifest.EffectivePlaybackBitrate);
    }

    [Fact]
    public void APreSplitPackageStillLoadsAndItsBitrateIsTreatedAsPlayback()
    {
        // Packages built before the split carried a single "bitrate", and the
        // player used it as the rate to configure the interface to.
        using var fixture = new ScenarioFixture();
        var directory = fixture.AddScenario("rav4_001",
            originalBitrate: null, playbackBitrate: null, legacyBitrate: 250_000);
        var manifest = ScenarioPackage.Load(directory).Manifest;

        Assert.Null(manifest.PlaybackBitrate);
        Assert.Equal(250_000, manifest.EffectivePlaybackBitrate);
        Assert.Null(manifest.OriginalBitrate);
    }

    [Fact]
    public void TimelinesAreLoadedPerBusAndCached()
    {
        using var fixture = new ScenarioFixture();
        var package = ScenarioPackage.Load(
            fixture.AddScenario("rav4_001", frameCount: 50, buses: [0, 1]));

        var bus0 = package.Timeline(0);
        var bus1 = package.Timeline(1);

        Assert.Equal(0, bus0.BusIndex);
        Assert.Equal(1, bus1.BusIndex);
        Assert.Equal(50, bus0.Count);
        Assert.Same(bus0, package.Timeline(0));

        package.Unload();
        Assert.NotSame(bus0, package.Timeline(0));
    }

    [Fact]
    public void AskingForAnUnrecordedBusIsAClearError()
    {
        using var fixture = new ScenarioFixture();
        var package = ScenarioPackage.Load(fixture.AddScenario("rav4_001", buses: [0]));

        var error = Assert.Throws<ScenarioException>(() => package.Timeline(7));
        Assert.Contains("no CAN file recorded for bus 7", error.Message);
    }

    [Fact]
    public void ProblemsListsEveryMissingFileAtOnce()
    {
        using var fixture = new ScenarioFixture();
        var directory = fixture.AddScenario("rav4_001", buses: [0, 1]);
        File.Delete(Path.Combine(directory, "video.mp4"));
        File.Delete(Path.Combine(directory, "can", "bus_1.canbin"));

        var problems = ScenarioPackage.Load(directory).Problems();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("video file missing"));
        Assert.Contains(problems, p => p.Contains("bus_1.canbin"));
    }

    [Fact]
    public void AHealthyPackageHasNoProblems()
    {
        using var fixture = new ScenarioFixture();
        Assert.Empty(ScenarioPackage.Load(fixture.AddScenario("rav4_001")).Problems());
    }

    [Fact]
    public void ManifestJsonRejectsAZeroDuration()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["format_version"] = 1,
            ["scenario_id"] = "x",
            ["duration_sec"] = 0,
            ["default_bus"] = 0,
            ["can"] = new Dictionary<string, string> { ["0"] = "bus_0.canbin" },
        });

        var error = Assert.Throws<ScenarioException>(() => ScenarioManifest.Parse(json, "test"));
        Assert.Contains("duration_sec must be positive", error.Message);
    }
}
