using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CanReplayPlayer.Scenarios;
using CanReplayPlayer.Wpf.Services;

namespace CanReplayPlayer.Wpf.Views;

/// <summary>
/// Picks a comma2k19 chunk, downloads it, and hands over to the Scenario Builder.
/// </summary>
/// <remarks>
/// The download stops at the raw archive on purpose. Converting it into Scenario
/// Packages needs Python and FFmpeg, and the player is deliberately built to
/// require neither on the exhibition PC. Pulling that toolchain into the app to
/// finish the job here would undo the whole reason the player is a single
/// self-contained EXE, so the window prints the exact commands instead.
/// </remarks>
public partial class GetScenariosWindow : Window
{
    private readonly AppLog _log;
    private readonly DatasetDownloader _downloader = new();
    private readonly StringBuilder _logText = new();

    private CancellationTokenSource? _cancellation;
    private string _targetDirectory;

    public GetScenariosWindow(AppLog log, string scenarioDirectory)
    {
        _log = log;
        InitializeComponent();

        // Default beside the Scenarios folder rather than inside it: the raw
        // archives are not scenarios, and the library scan would trip over them.
        var parent = Directory.GetParent(scenarioDirectory)?.FullName
                     ?? AppContext.BaseDirectory;
        _targetDirectory = Path.Combine(parent, "comma2k19");

        ChunkList.ItemsSource = DatasetCatalog.Chunks;
        ChunkList.SelectedIndex = 0;
        UpdateTarget();
    }

    private DatasetChunk? Selected => ChunkList.SelectedItem as DatasetChunk;

    private void UpdateTarget()
    {
        TargetPath.Text = _targetDirectory;
        CheckFreeSpace();
        ShowExistingState();
    }

    /// <summary>
    /// Warn before starting rather than after 8 GB of transfer.
    /// </summary>
    private void CheckFreeSpace()
    {
        SpaceWarning.Visibility = Visibility.Collapsed;
        if (Selected is not { } chunk)
        {
            return;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_targetDirectory));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var free = new DriveInfo(root).AvailableFreeSpace;
            // The archive plus room to extract it; extraction roughly doubles it.
            var needed = chunk.SizeBytes * 2;
            if (free < needed)
            {
                SpaceWarning.Text =
                    $"Only {free / 1_000_000_000.0:0.0} GB free on {root}. " +
                    $"About {needed / 1_000_000_000.0:0.0} GB is needed for the archive " +
                    "plus the extracted data.";
                SpaceWarning.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // A drive that cannot be queried is not a reason to block a download.
        }
    }

    private void ShowExistingState()
    {
        if (Selected is not { } chunk)
        {
            return;
        }

        if (DatasetDownloader.IsComplete(chunk, _targetDirectory))
        {
            Progress.Value = 1;
            ProgressText.Text = $"{chunk.FileName} already downloaded";
            DownloadButton.Content = "Re-download";
            AppendNextSteps(Path.Combine(_targetDirectory, chunk.FileName));
            return;
        }

        var existing = DatasetDownloader.ExistingBytes(chunk, _targetDirectory);
        Progress.Value = chunk.SizeBytes > 0 ? (double)existing / chunk.SizeBytes : 0;
        ProgressText.Text = existing > 0
            ? $"{existing / 1_000_000_000.0:0.00} GB of {chunk.SizeText} already fetched — will resume"
            : $"0.00 GB of {chunk.SizeText}";
        DownloadButton.Content = existing > 0 ? "Resume Download" : "Download";
    }

    private void OnChunkSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateTarget();

    private void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where should the dataset be downloaded?",
            InitialDirectory = Directory.Exists(_targetDirectory)
                ? _targetDirectory
                : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _targetDirectory = dialog.FolderName;
            UpdateTarget();
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } chunk || _cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ChunkList.IsEnabled = false;

        Append($"Downloading {chunk.FileName} ({chunk.SizeText}) to {_targetDirectory}");
        Append($"Source: {chunk.Url}");
        _log.Info($"dataset download started: {chunk.Id} -> {_targetDirectory}");

        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress.Value = p.Fraction;
            ProgressText.Text = $"{p.ReceivedText} of {p.TotalText}  ({p.Fraction * 100:0.0} %)";
            SpeedText.Text = p.Remaining is { } remaining
                ? $"{p.SpeedText}  ·  {remaining:hh\\:mm\\:ss} left"
                : p.SpeedText;
        });

        try
        {
            var result = await _downloader
                .DownloadAsync(chunk, _targetDirectory, progress, _cancellation.Token)
                .ConfigureAwait(true);

            Append(result.Message);
            _log.Info($"dataset download finished: success={result.Success}; {result.Message}");

            if (result.Success)
            {
                AppendNextSteps(result.Path);
            }
        }
        catch (Exception error)
        {
            Append($"Download failed: {error.Message}");
            _log.Error("dataset download failed", error);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            DownloadButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ChunkList.IsEnabled = true;
            ShowExistingState();
        }
    }

    private void AppendNextSteps(string archivePath)
    {
        var extracted = Path.Combine(_targetDirectory,
            Path.GetFileNameWithoutExtension(archivePath));

        Append(string.Empty);
        Append("Next: turn it into Scenario Packages.");
        Append("This needs Python 3.11+ and FFmpeg, which the player itself does not.");
        Append(string.Empty);
        Append("1. Extract the archive:");
        Append($"     tar -xf \"{archivePath}\" -C \"{_targetDirectory}\"");
        Append(string.Empty);
        Append("2. Survey it and pick a varied shortlist:");
        Append("     python -m scenario_builder analyze \\");
        Append($"         --input \"{extracted}\" \\");
        Append("         --output scenario_analysis.json --count 20");
        Append(string.Empty);
        Append("3. Build only what it recommended:");
        Append("     python -m scenario_builder build-selected \\");
        Append("         --analysis scenario_analysis.json \\");
        Append("         --output \"<your Scenarios folder>\"");
        Append(string.Empty);
        Append("Then press Reload in the main window.");
    }

    private void Append(string line)
    {
        _logText.AppendLine(line);
        LogText.Text = _logText.ToString();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        Append("Cancelling… the partial file is kept so the next attempt resumes.");
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DatasetCatalog.DatasetPage)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            MessageBox.Show(this, $"Please visit:\n\n{DatasetCatalog.DatasetPage}",
                "comma2k19", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_cancellation is not null)
        {
            var answer = MessageBox.Show(this,
                "A download is still running. Cancel it and close?\n\n" +
                "What has been fetched is kept, and reopening this window resumes it.",
                "Get Scenarios", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cancellation.Cancel();
        }

        base.OnClosing(e);
        _downloader.Dispose();
    }
}
