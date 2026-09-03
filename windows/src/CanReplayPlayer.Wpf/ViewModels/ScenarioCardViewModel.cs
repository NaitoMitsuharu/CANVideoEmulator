using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanReplayPlayer.Scenarios;

namespace CanReplayPlayer.Wpf.ViewModels;

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

    public bool HasTags => Package.Manifest.Tags.Count > 0;

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
            image.DecodePixelWidth = 320;
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
