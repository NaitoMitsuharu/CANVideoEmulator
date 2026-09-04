using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CANVideoEmulator.Scenarios;

namespace CANVideoEmulator.Wpf.ViewModels;

/// <summary>One thumbnail card in the Scenario Browser (requirement 34).</summary>
public sealed class ScenarioCardViewModel : ObservableObject
{
    private bool _isSelected;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    public ScenarioCardViewModel(ScenarioPackage package)
    {
        Package = package;
    }

    public ScenarioPackage Package { get; }

    public string ScenarioId => Package.ScenarioId;

    public string Title => Package.Title;

    public string Vehicle => Package.Manifest.Vehicle;

    public string DurationText
    {
        get
        {
            var duration = Package.Duration;
            return $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
        }
    }

    public string BusesText => $"Bus {string.Join(", ", Package.AvailableBuses)}";

    public IReadOnlyList<string> Tags => Package.Manifest.Tags;

    /// <summary>Tags with the measurement that earned them, e.g. "High Speed · 100km/h".</summary>
    public IReadOnlyList<string> TagBadges =>
        [.. Tags.Select(tag => FormatTagBadge(tag, Package.Manifest.TagEvidence))];

    public string TagsText => string.Join(" · ", TagBadges);

    public bool HasTags => Package.Manifest.Tags.Count > 0;

    private static string FormatTagBadge(string tag, TagEvidence? evidence) => tag switch
    {
        "High Speed" or "Low Speed" when evidence?.Speed is { } speed =>
            $"{tag} · {speed.Median:F0}km/h",
        "Speed Change" when evidence?.Speed is { } speed =>
            $"{tag} · {speed.Max - speed.Min:F0}km/h span",
        "Vehicle Stop" when evidence?.Speed is { } speed =>
            $"{tag} · min {speed.Min:F0}km/h",
        "Steering Active" when evidence?.Steering is { } steering =>
            $"{tag} · {steering.MaxAbs:F0}°",
        "Winding" when evidence?.Steering is { } steering =>
            $"{tag} · σ{steering.Stdev:F1}°",
        "Cruise" when evidence?.Cruise is { } cruise =>
            $"{tag} · {cruise.Value * 100:F0}%",
        "Braking" when evidence?.Brake is { } brake =>
            $"{tag} · {brake.Value * 100:F0}%",
        "Acceleration" when evidence?.Gas is { } gas =>
            $"{tag} · {gas.Value:F0}%",
        _ => tag,
    };

    public string Tooltip =>
        $"{Package.Manifest.Title}\n" +
        $"{Package.Manifest.Vehicle}  ·  {Package.Manifest.Dataset}\n" +
        $"route {Package.Manifest.Route} segment {Package.Manifest.Segment}\n" +
        $"buses {string.Join(", ", Package.AvailableBuses)} (default {Package.DefaultBus})\n" +
        $"{Package.Manifest.DurationSeconds:F1} s" +
        (Package.Manifest.Tags.Count > 0
            ? $"\ntags: {string.Join(", ", Package.Manifest.Tags)}"
            : string.Empty);

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>
    /// Loaded on first request rather than on library scan: a directory of a
    /// few hundred scenarios must not decode a few hundred JPEGs to open.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _thumbnail = LoadThumbnail();
            }

            return _thumbnail;
        }
    }

    public bool HasThumbnail => Thumbnail is not null;

    private bool _routeOverlayRequested;
    private Geometry? _routeOverlay;

    /// <summary>
    /// The GNSS trajectory shape, normalised to a 100x100 box, for a small route
    /// squiggle drawn over the thumbnail -- reuses <see cref="ScenarioPackage.Telemetry"/>,
    /// so a scenario played later does not parse its telemetry.json twice.
    /// </summary>
    public Geometry? RouteOverlay
    {
        get
        {
            if (!_routeOverlayRequested)
            {
                _routeOverlayRequested = true;
                _routeOverlay = BuildRouteOverlay();
            }

            return _routeOverlay;
        }
    }

    public bool HasRouteOverlay => RouteOverlay is not null;

    private Geometry? BuildRouteOverlay()
    {
        var gnss = Package.Telemetry?.Gnss;
        if (gnss is null || gnss.Count < 2)
        {
            return null;
        }

        var east = gnss.EastM;
        var north = gnss.NorthM;
        var minE = east.Min();
        var maxE = east.Max();
        var minN = north.Min();
        var maxN = north.Max();
        var span = Math.Max(Math.Max(maxE - minE, maxN - minN), 1.0);
        var scale = 100.0 / span;
        var offsetX = (100 - (maxE - minE) * scale) / 2;
        var offsetY = (100 - (maxN - minN) * scale) / 2;

        Point Project(int i) => new(
            offsetX + (east[i] - minE) * scale,
            // Screen Y grows downward; flip so north is up, matching the map overlay.
            100 - (offsetY + (north[i] - minN) * scale));

        var figure = new PathFigure { StartPoint = Project(0), IsClosed = false };
        for (var i = 1; i < gnss.Count; i++)
        {
            figure.Segments.Add(new LineSegment(Project(i), true));
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private ImageSource? LoadThumbnail()
    {
        var path = Package.ThumbnailPath;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = 160;
            // Cache on load so the file is not left locked; scenario folders get
            // rebuilt while the app is open during bring-up.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
