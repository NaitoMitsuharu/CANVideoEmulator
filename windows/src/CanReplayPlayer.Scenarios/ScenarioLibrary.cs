using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanReplayPlayer.Scenarios;

/// <summary>
/// The Scenarios directory: every scenario package plus playlist.json.
/// </summary>
/// <remarks>
/// Requirement 4 asks for tens to hundreds of scenarios to work, so scanning is
/// shallow (one directory level) and reads only scenario.json per scenario.
/// A scenario that fails to load does not abort the scan: it is collected in
/// <see cref="LoadErrors"/> and the rest of the library still opens
/// (requirement 64).
/// </remarks>
public sealed class ScenarioLibrary
{
    private ScenarioLibrary(string root, IReadOnlyList<ScenarioPackage> scenarios,
                            PlaylistCollection playlists, IReadOnlyList<string> loadErrors)
    {
        Root = root;
        Scenarios = scenarios;
        Playlists = playlists;
        LoadErrors = loadErrors;
        ById = scenarios.ToDictionary(s => s.ScenarioId, StringComparer.OrdinalIgnoreCase);
    }

    public string Root { get; }

    public IReadOnlyList<ScenarioPackage> Scenarios { get; }

    public IReadOnlyDictionary<string, ScenarioPackage> ById { get; }

    public PlaylistCollection Playlists { get; }

    /// <summary>Scenarios that could not be loaded, with the reason.</summary>
    public IReadOnlyList<string> LoadErrors { get; }

    public static ScenarioLibrary Empty(string root) =>
        new(root, [], PlaylistCollection.Empty, []);

    public static ScenarioLibrary Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            throw new ScenarioException(
                $"Scenario directory not found: {root}\n" +
                "Point the player at a folder containing one sub-folder per scenario, " +
                "each with a scenario.json.");
        }

        var scenarios = new List<ScenarioPackage>();
        var errors = new List<string>();

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(directory, "scenario.json")))
            {
                continue;
            }

            try
            {
                scenarios.Add(ScenarioPackage.Load(directory));
            }
            catch (Exception error) when (error is ScenarioException or IOException)
            {
                errors.Add($"{Path.GetFileName(directory)}: {error.Message}");
            }
        }

        return new ScenarioLibrary(root, scenarios, LoadPlaylists(root, scenarios, errors), errors);
    }

    private static PlaylistCollection LoadPlaylists(string root,
                                                    List<ScenarioPackage> scenarios,
                                                    List<string> errors)
    {
        var path = Path.Combine(root, "playlist.json");
        if (!File.Exists(path))
        {
            return PlaylistCollection.SingleAll(scenarios);
        }

        try
        {
            var document = JsonSerializer.Deserialize<PlaylistCollection>(
                File.ReadAllText(path), ScenarioManifest.JsonOptions);
            if (document is null || document.Playlists.Count == 0)
            {
                return PlaylistCollection.SingleAll(scenarios);
            }

            // Drop ids that no longer exist so Next/Previous can never land on a
            // scenario that is not in the library.
            var known = scenarios.Select(s => s.ScenarioId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var playlist in document.Playlists)
            {
                var missing = playlist.ScenarioIds.Where(id => !known.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    errors.Add($"playlist '{playlist.PlaylistId}' references " +
                               $"{missing.Count} unknown scenario(s): {string.Join(", ", missing)}");
                    playlist.ScenarioIds = playlist.ScenarioIds.Where(known.Contains).ToList();
                }
            }

            document.Playlists = document.Playlists.Where(p => p.ScenarioIds.Count > 0).ToList();
            return document.Playlists.Count > 0
                ? document
                : PlaylistCollection.SingleAll(scenarios);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            errors.Add($"playlist.json could not be read ({error.Message}); " +
                       "falling back to a single list of every scenario.");
            return PlaylistCollection.SingleAll(scenarios);
        }
    }
}

/// <summary>playlist.json (requirement 36).</summary>
public sealed class PlaylistCollection
{
    [JsonPropertyName("format_version")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("default_playlist")] public string? DefaultPlaylist { get; set; }
    [JsonPropertyName("playlists")] public List<Playlist> Playlists { get; set; } = [];

    public static readonly PlaylistCollection Empty = new();

    public static PlaylistCollection SingleAll(IEnumerable<ScenarioPackage> scenarios) => new()
    {
        DefaultPlaylist = "all",
        Playlists =
        [
            new Playlist
            {
                PlaylistId = "all",
                Title = "All Scenarios",
                Description = "Every scenario found in the scenario directory.",
                ScenarioIds = scenarios.Select(s => s.ScenarioId).ToList(),
            },
        ],
    };

    public Playlist? Find(string playlistId) =>
        Playlists.FirstOrDefault(p =>
            string.Equals(p.PlaylistId, playlistId, StringComparison.OrdinalIgnoreCase));

    public Playlist? Default =>
        (DefaultPlaylist is not null ? Find(DefaultPlaylist) : null) ?? Playlists.FirstOrDefault();
}

public sealed class Playlist
{
    [JsonPropertyName("playlist_id")] public string PlaylistId { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("scenario_ids")] public List<string> ScenarioIds { get; set; } = [];

    public int Count => ScenarioIds.Count;

    public int IndexOf(string scenarioId) =>
        ScenarioIds.FindIndex(id => string.Equals(id, scenarioId, StringComparison.OrdinalIgnoreCase));
}
