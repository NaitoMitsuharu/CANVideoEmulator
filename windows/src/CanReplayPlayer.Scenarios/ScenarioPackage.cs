using CanReplayPlayer.Core.Can;

namespace CanReplayPlayer.Scenarios;

/// <summary>
/// One scenario directory on disk, with its CAN timelines loaded on demand.
/// </summary>
/// <remarks>
/// Timelines are loaded lazily and cached per bus: a directory of a hundred
/// scenarios must be browsable without reading a hundred megabytes of CAN, but
/// switching buses inside the scenario being played must be instant.
/// </remarks>
public sealed class ScenarioPackage
{
    private readonly Dictionary<int, CanTimeline> _timelines = [];
    private readonly object _gate = new();

    private ScenarioPackage(string directory, ScenarioManifest manifest)
    {
        Directory = directory;
        Manifest = manifest;
    }

    public string Directory { get; }

    public ScenarioManifest Manifest { get; }

    public string ScenarioId => Manifest.ScenarioId;

    public string Title => Manifest.Title;

    public TimeSpan Duration => TimeSpan.FromSeconds(Manifest.DurationSeconds);

    /// <summary>Offset from CAN t=0 to the first video frame (requirement 12).</summary>
    public TimeSpan VideoCanOffset => TimeSpan.FromMilliseconds(Manifest.VideoCanOffsetMs);

    public IReadOnlyList<int> AvailableBuses => Manifest.AvailableBuses;

    public int DefaultBus => Manifest.DefaultBus;

    public string? VideoPath =>
        string.IsNullOrEmpty(Manifest.Video) ? null : Path.Combine(Directory, Manifest.Video);

    public string? ThumbnailPath =>
        string.IsNullOrEmpty(Manifest.Thumbnail) ? null : Path.Combine(Directory, Manifest.Thumbnail);

    public string? SignalsJsonPath
    {
        get
        {
            var path = Path.Combine(Directory, "dbc", "signals.json");
            return File.Exists(path) ? path : null;
        }
    }

    public static ScenarioPackage Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var manifestPath = Path.Combine(directory, "scenario.json");
        if (!File.Exists(manifestPath))
        {
            throw new ScenarioException($"{directory}: scenario.json not found");
        }

        var manifest = ScenarioManifest.Parse(File.ReadAllText(manifestPath), manifestPath);
        return new ScenarioPackage(directory, manifest);
    }

    public string CanFilePath(int bus)
    {
        if (!Manifest.Can.TryGetValue(bus.ToString(), out var name))
        {
            throw new ScenarioException(
                $"{ScenarioId}: no CAN file recorded for bus {bus} " +
                $"(available: {string.Join(", ", Manifest.Can.Keys)})");
        }

        return Path.Combine(Directory, "can", name);
    }

    /// <summary>Load (and cache) the timeline for one bus.</summary>
    public CanTimeline Timeline(int bus)
    {
        lock (_gate)
        {
            if (_timelines.TryGetValue(bus, out var cached))
            {
                return cached;
            }
        }

        var timeline = CanTimeline.Load(CanFilePath(bus));
        lock (_gate)
        {
            _timelines[bus] = timeline;
            return timeline;
        }
    }

    public BusStatistics? StatisticsFor(int bus) =>
        Manifest.BusStatistics.FirstOrDefault(s => s.Bus == bus);

    /// <summary>Free cached timelines when moving on to another scenario.</summary>
    public void Unload()
    {
        lock (_gate) { _timelines.Clear(); }
    }

    /// <summary>
    /// Everything that would stop this scenario from playing, gathered in one
    /// pass so the UI can report all of it at once rather than one failure at a
    /// time (requirement 64).
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (VideoPath is { } video && !File.Exists(video))
        {
            problems.Add($"video file missing: {video}");
        }

        foreach (var (busText, name) in Manifest.Can)
        {
            var path = Path.Combine(Directory, "can", name);
            if (!File.Exists(path))
            {
                problems.Add($"CAN file missing for bus {busText}: {path}");
            }
        }

        if (!Manifest.AvailableBuses.Contains(Manifest.DefaultBus))
        {
            problems.Add($"default_bus {Manifest.DefaultBus} is not in available_buses");
        }

        return problems;
    }
}
