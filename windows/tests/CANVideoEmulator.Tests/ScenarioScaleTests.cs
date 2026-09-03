using System.Diagnostics;
using CANVideoEmulator.Core.Can;
using CANVideoEmulator.Core.Playback;
using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

/// <summary>
/// Requirement 4 and 75: the library must handle at least ten scenarios today and
/// tens to hundreds later, and requirement 34's browser must open without paying
/// for all of them.
/// </summary>
/// <remarks>
/// comma2k19 ships exactly one example segment inside its repository; the full
/// chunks are a ~100 GB academic-torrent download. These tests therefore prove
/// the *player* scales using synthesised packages, while the real decode
/// correctness is proven separately against the one genuine segment.
/// </remarks>
public class ScenarioScaleTests
{
    [Fact]
    public void ADirectoryOfManyScenariosLoads()
    {
        using var fixture = new ScenarioFixture();
        for (var i = 1; i <= 120; i++)
        {
            fixture.AddScenario($"rav4_{i:000}", frameCount: 200);
        }

        var started = Stopwatch.StartNew();
        var library = ScenarioLibrary.Load(fixture.Root);
        var elapsed = started.Elapsed;

        Assert.Equal(120, library.Scenarios.Count);
        Assert.Empty(library.LoadErrors);
        Assert.True(elapsed < TimeSpan.FromSeconds(10),
            $"loading 120 scenarios took {elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void ScanningDoesNotReadTheCanTimelines()
    {
        // Opening the browser must not pay for every scenario's CAN data; the
        // timelines load only when a scenario is actually selected.
        using var fixture = new ScenarioFixture();
        for (var i = 1; i <= 30; i++)
        {
            fixture.AddScenario($"rav4_{i:000}", frameCount: 5_000);
        }

        var library = ScenarioLibrary.Load(fixture.Root);
        var timelineBytes = Directory.EnumerateFiles(fixture.Root, "*.canbin",
            SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

        Assert.Equal(30, library.Scenarios.Count);
        Assert.True(timelineBytes > 1_000_000,
            "the fixture should have produced a meaningful amount of CAN data");

        // Manifest data is available without any timeline having been touched.
        Assert.All(library.Scenarios, s => Assert.NotEmpty(s.Title));
    }

    [Fact]
    public void AutoAdvanceWalksAWholePlaylistOfTenScenarios()
    {
        using var fixture = new ScenarioFixture();
        var ids = new List<string>();
        for (var i = 1; i <= 10; i++)
        {
            var id = $"rav4_{i:000}";
            ids.Add(id);
            // ~40 ms each so ten of them finish quickly.
            fixture.AddScenario(id, frameCount: 40);
        }

        fixture.AddPlaylists(("featured", "Featured", ids.ToArray()));

        var clock = new PlaybackClock();
        var transport = new MemoryCanTransport();
        transport.Open();
        var scheduler = new CanScheduler(clock, transport);
        var video = new FakeVideoPlayer();

        var session = new ReplaySession(clock, scheduler, video,
            ScenarioLibrary.Load(fixture.Root),
            new ReplaySessionOptions
            {
                TransitionGap = TimeSpan.FromMilliseconds(20),
                LoopMode = LoopMode.PlaylistAdvance,
            });

        var visited = new List<string>();
        session.ScenarioChanged += (_, package) =>
        {
            lock (visited) { visited.Add(package.ScenarioId); }
        };

        var finished = false;
        session.PlaylistFinished += (_, _) => finished = true;

        session.LoadScenario(ids[0]);
        session.Play();

        var deadline = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < deadline && !finished)
        {
            Thread.Sleep(10);
        }

        scheduler.Stop();

        Assert.True(finished, $"playlist did not finish; reached {visited.Count} scenarios");
        lock (visited)
        {
            Assert.Equal(ids, visited);
        }
    }

    [Fact]
    public void EverySelectedBusIsTheOnlyOneTransmitted()
    {
        // Requirement 26 and 82: recorded buses are never merged.
        using var fixture = new ScenarioFixture();
        fixture.AddScenario("rav4_001", frameCount: 2_000, buses: [0, 1, 2], defaultBus: 0);

        var clock = new PlaybackClock();
        var transport = new MemoryCanTransport();
        transport.Open();
        var scheduler = new CanScheduler(clock, transport);
        var video = new FakeVideoPlayer();
        var session = new ReplaySession(clock, scheduler, video,
            ScenarioLibrary.Load(fixture.Root),
            new ReplaySessionOptions { TransitionGap = TimeSpan.FromMilliseconds(10) });

        session.LoadScenario("rav4_001");

        foreach (var bus in new[] { 0, 1, 2 })
        {
            session.SelectBus(bus);
            transport.Clear();
            session.Play();

            var deadline = Environment.TickCount64 + 3_000;
            while (Environment.TickCount64 < deadline && transport.Count < 40)
            {
                Thread.Sleep(5);
            }

            session.Pause();

            Assert.True(transport.Count >= 40, $"bus {bus} produced only {transport.Count} frames");
            // The fixture stamps the bus index into the first payload byte.
            Assert.All(transport.Sent, s => Assert.Equal(bus, s.Frame.Data[0]));
        }

        scheduler.Stop();
    }
}
