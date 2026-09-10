using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CANVideoEmulator.Scenarios;
using CANVideoEmulator.Wpf.Services;

namespace CANVideoEmulator.Wpf.Views;

public partial class GetScenariosWindow : Window
{
    private readonly AppLog _log;
    private readonly string _scenarioDirectory;
    public event EventHandler? ScenariosUpdated;
    private readonly DatasetDownloader _downloader = new();
    private readonly RemoteZipSegmentDownloader _segmentDownloader = new();
    private readonly StringBuilder _logText = new();
    private CancellationTokenSource? _cancellation;
    private string _targetDirectory;
    private bool _closeAfterDownload;
    private readonly AppSettings? _settings;
    private readonly List<string> _knownDirectories;
    private readonly ChunkRow[] _rows = DatasetCatalog.Chunks.Select(file => new ChunkRow(file)).ToArray();

    public GetScenariosWindow(AppLog log, string scenarioDirectory, AppSettings? settings = null)
    {
        _log = log;
        _settings = settings;
        _scenarioDirectory = scenarioDirectory;
        var parent = Directory.GetParent(scenarioDirectory)?.FullName ?? AppContext.BaseDirectory;
        _knownDirectories = [.. settings?.DatasetDownloadDirectories ?? [],
            Path.Combine(parent, "data"), Path.Combine(parent, "comma2k19")];
        _targetDirectory = _knownDirectories[0];
        InitializeComponent();
        ChunkList.ItemsSource = _rows;
        ChunkList.SelectedIndex = 0;
        UpdateTarget();
        Activated += (_, _) => { if (_cancellation is null) UpdateTarget(); };
    }

    private DatasetChunk[] Selected => ChunkList.SelectedItems.Cast<ChunkRow>().Select(row => row.File).ToArray();
    private bool IsPartialDownload => (DownloadMode.SelectedItem as ComboBoxItem)?.Tag as string == "partial";
    private int SegmentLimit => int.TryParse((SegmentCount.SelectedItem as ComboBoxItem)?.Tag as string, out var count) ? count : 3;
    private DatasetDownloadPlan Plan(IReadOnlyList<DatasetChunk> files, bool example = false) =>
        DatasetDownloadPlan.Create(files, _targetDirectory, _knownDirectories, example);

    private void UpdateTarget()
    {
        if (!IsInitialized) return;
        TargetPath.Text = _targetDirectory;
        SpaceWarning.Visibility = Visibility.Collapsed;
        var selected = Selected;
        var plan = Plan(selected);
        var sample = Plan(DatasetCatalog.ExampleFiles, true);
        foreach (var item in Plan(DatasetCatalog.Chunks).Items)
            _rows.First(row => row.File.Id == item.File.Id).Update(item);
        SelectionSummary.Text = IsPartialDownload
            ? $"{selected.Length} chunk(s) selected · {SegmentLimit} scenario(s) per chunk · ZIP archives are not downloaded"
            : $"{selected.Length} selected · {plan.Items.Count(item => item.IsComplete)} downloaded · {plan.RemainingBytes / 1e9:0.00} GB remaining";
        DownloadButton.IsEnabled = selected.Length > 0 && _cancellation is null;
        DownloadButton.Content = IsPartialDownload ? "Download Selected Scenarios & Convert"
            : plan.IsComplete ? "Convert Selected" : "Download & Convert";
        SegmentCount.IsEnabled = IsPartialDownload && _cancellation is null;
        ExampleButton.IsEnabled = _cancellation is null;
        ExampleButton.Content = sample.IsComplete ? "Add Sample" : "Get 45 MB Sample";
        SampleStatus.Text = $"45 MB sample · {sample.Items.Count(item => item.IsComplete)}/{sample.Items.Count} files downloaded · {sample.Items[0].Directory}";
        SampleStatus.ToolTip = SampleStatus.Text;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_targetDirectory));
            var needed = IsPartialDownload ? 0
                : plan.RemainingBytes + plan.Items.Where(item => !item.IsComplete).Sum(item => item.File.SizeBytes);
            if (root is not null && new DriveInfo(root).AvailableFreeSpace < needed)
            {
                SpaceWarning.Text = $"Allow about {needed / 1e9:0.0} GB more for downloads and extraction. The 45 MB sample needs much less.";
                SpaceWarning.Visibility = Visibility.Visible;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OnChunkSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTarget();
    private void OnDownloadModeChanged(object sender, SelectionChangedEventArgs e) => UpdateTarget();

    private void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Dataset download folder",
            InitialDirectory = Directory.Exists(_targetDirectory) ? _targetDirectory : AppContext.BaseDirectory,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _targetDirectory = dialog.FolderName;
            _knownDirectories.RemoveAll(path => string.Equals(path, _targetDirectory, StringComparison.OrdinalIgnoreCase));
            _knownDirectories.Insert(0, _targetDirectory);
            if (_settings is not null)
            {
                _settings.DatasetDownloadDirectories = [.. _knownDirectories];
                if (_settings.Save() is { } error) _log.Warning(error);
            }
            UpdateTarget();
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e) => await DownloadAsync(Selected, false);
    private async void OnExampleClick(object sender, RoutedEventArgs e) => await DownloadAsync(DatasetCatalog.ExampleFiles, true);

    private async Task DownloadAsync(IReadOnlyList<DatasetChunk> files, bool example)
    {
        if (files.Count == 0 || _cancellation is not null) return;
        var script = ScenarioConverter.FindScript(_scenarioDirectory);
        if (script is null)
        {
            ConvertStageText.Text = "Open this player from a CANVideoEmulator clone to enable automatic conversion.";
            Append("scripts/prepare_scenarios.ps1 was not found. Clone the repository and run scripts/setup.ps1.");
            return;
        }
        _cancellation = new CancellationTokenSource();
        var cancellation = _cancellation.Token;
        // Re-inspect immediately before starting, even if another process has
        // created files since the window was opened.
        var plan = Plan(files, example);
        DownloadButton.IsEnabled = ExampleButton.IsEnabled = ChangeFolderButton.IsEnabled = ChunkList.IsEnabled = false;
        CancelButton.IsEnabled = true;
        DownloadStageText.Text = "Checking existing files, then downloading missing data…";
        ConvertStageText.Text = "Waiting for downloads. Python and FFmpeg are required for conversion.";
        ConversionProgress.Value = 0;
        ConversionProgressText.Text = "0% · Waiting for downloads";
        var total = files.Sum(c => c.SizeBytes);
        long completedBytes = 0;
        var succeeded = 0;
        try
        {
            DownloadStageText.Text = "Checking conversion tools before downloading…";
            await ScenarioConverter.ConvertAsync(script, "", _scenarioDirectory,
                new Progress<string>(Append), cancellation, checkOnly: true);
            if (!example && IsPartialDownload)
            {
                await DownloadSelectedSegmentsAsync(files, plan, script, cancellation);
                return;
            }
            DownloadStageText.Text = "Downloading missing files…";
            for (var i = 0; i < files.Count; i++)
            {
                if (cancellation.IsCancellationRequested) break;
                var item = plan.Items[i];
                var file = item.File;
                var directory = item.Directory;
                var baseBytes = completedBytes;
                var label = $"{i + 1}/{files.Count}";
                Append($"[{label}] {file.FileName} — {directory}");
                var progress = new Progress<DownloadProgress>(p =>
                {
                    Progress.Value = (double)(baseBytes + p.BytesReceived) / total;
                    ProgressText.Text = $"{label} · {(baseBytes + p.BytesReceived) / 1e6:0.0} / {total / 1e6:0.0} MB";
                    SpeedText.Text = p.SpeedText;
                });
                var result = await _downloader.DownloadAsync(file, directory, progress, cancellation);
                Append(result.Message);
                _log.Info($"dataset: {file.Id}; success={result.Success}; {result.Message}");
                if (!result.Success) break;
                succeeded++;
                completedBytes += file.SizeBytes;

            }
            if (succeeded == files.Count)
            {
                DownloadStageText.Text = $"Complete — {files.Count} files available locally.";
                SpeedText.Text = "";
                Progress.Value = 1;
                ProgressText.Text = $"{total / 1e6:0.0} MB available locally";
                var inputs = example
                    ? new[] { Path.Combine(plan.Items[0].Directory, DatasetCatalog.ExampleDirectory) }
                    : plan.Items.Select(item => Path.Combine(item.Directory, item.File.FileName)).ToArray();
                for (var inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
                {
                    var input = inputs[inputIndex];
                    var progressIndex = inputIndex;
                    var acceptingProgress = true;
                    cancellation.ThrowIfCancellationRequested();
                    ConvertStageText.Text = $"Converting {Path.GetFileName(input)} — completed scenarios are reused.";
                    Append($"STEP 3 — Convert: {input}");
                    var conversionLog = new Progress<string>(line =>
                    {
                        Append(line);
                        _log.Info($"convert: {line}");
                    });
                    var measuredProgress = new Progress<ConversionProgressUpdate>(update =>
                    {
                        if (!acceptingProgress) return;
                        ConversionProgress.Value = Math.Max(ConversionProgress.Value,
                            (progressIndex + update.Fraction) / inputs.Length);
                        ConversionProgressText.Text = $"{Math.Min(99, Math.Floor(ConversionProgress.Value * 100)):0}% · Input {progressIndex + 1}/{inputs.Length} · {update.PhaseText}";
                        ConvertStageText.Text = string.IsNullOrWhiteSpace(update.Detail) ? update.PhaseText : update.Detail;
                    });
                    try
                    {
                        await ScenarioConverter.ConvertAsync(script, input, _scenarioDirectory, conversionLog, cancellation, measuredProgress);
                    }
                    finally { acceptingProgress = false; }
                    ConversionProgress.Value = (inputIndex + 1.0) / inputs.Length;
                    ScenariosUpdated?.Invoke(this, EventArgs.Empty);
                }
                ConversionProgress.Value = 1;
                ConversionProgressText.Text = "100% · Conversion and verification complete";
                ConvertStageText.Text = "Complete — scenarios have been added to the player.";
                Append($"Ready: {_scenarioDirectory}");
                // The player already picked up the new scenarios via
                // ScenariosUpdated above; nothing left for the user to do here.
                _closeAfterDownload = true;
            }
            else
            {
                DownloadStageText.Text = cancellation.IsCancellationRequested ? "Cancelled — partial downloads are kept." : "Download failed — see the log below.";
                ConvertStageText.Text = "Conversion has not started. Retry to continue.";
                Append($"Stopped: {succeeded}/{files.Count} complete. Retry skips completed files and resumes partial files.");
            }
        }
        catch (OperationCanceledException)
        {
            ConvertStageText.Text = "Cancelled. Completed downloads and scenarios are kept; retry continues.";
            Append(ConvertStageText.Text);
        }
        catch (Exception error)
        {
            ConvertStageText.Text = "Could not finish. See the log below, then retry.";
            Append(error.Message);
            _log.Error("dataset import failed", error);
        }
        finally
        {
            ConversionProgress.IsIndeterminate = false;
            ScenariosUpdated?.Invoke(this, EventArgs.Empty);
            _cancellation.Dispose();
            _cancellation = null;
            ChangeFolderButton.IsEnabled = ChunkList.IsEnabled = true;
            UpdateTarget();
            CancelButton.IsEnabled = false;
            SpeedText.Text = "";
            if (_closeAfterDownload) Close();
        }
    }

    private async Task DownloadSelectedSegmentsAsync(IReadOnlyList<DatasetChunk> files,
        DatasetDownloadPlan plan, string script, CancellationToken cancellation)
    {
        var inputs = new List<string>();
        for (var chunkIndex = 0; chunkIndex < files.Count; chunkIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            var file = files[chunkIndex];
            var item = plan.Items[chunkIndex];
            DownloadStageText.Text = $"Reading {file.Title} index…";
            Append($"INDEX — {file.FileName} (the full ZIP is not downloaded)");
            var index = await _segmentDownloader.ReadIndexAsync(file, cancellation);
            var selected = index.Segments.Take(SegmentLimit).ToArray();
            if (selected.Length == 0)
                throw new InvalidDataException($"No complete scenarios were found in {file.FileName}.");
            var extractionRoot = Path.Combine(item.Directory,
                Path.GetFileNameWithoutExtension(file.FileName) + "_extracted");
            for (var segmentIndex = 0; segmentIndex < selected.Length; segmentIndex++)
            {
                var segment = selected[segmentIndex];
                var currentChunk = chunkIndex;
                var currentSegment = segmentIndex;
                var label = $"Chunk {chunkIndex + 1}/{files.Count} · scenario {segmentIndex + 1}/{selected.Length}";
                DownloadStageText.Text = $"{label} · {segment.Route} #{segment.SegmentIndex}";
                var segmentProgress = new Progress<DownloadProgress>(update =>
                {
                    Progress.Value = (currentChunk + (currentSegment + update.Fraction) / selected.Length) / files.Count;
                    ProgressText.Text = $"{label} · {update.BytesReceived / 1e6:0.0} / {update.TotalBytes / 1e6:0.0} MB";
                });
                var input = await _segmentDownloader.DownloadAsync(file, segment,
                    extractionRoot, segmentProgress, cancellation);
                inputs.Add(input);
                Append($"Downloaded source: {segment.Route} segment {segment.SegmentIndex}");
            }
        }

        DownloadStageText.Text = $"Complete — {inputs.Count} scenario source(s) cached without full ZIP files.";
        Progress.Value = 1;
        for (var inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            var input = inputs[inputIndex];
            ConvertStageText.Text = $"Converting {Path.GetFileName(input)} — completed scenarios are reused.";
            var conversionLog = new Progress<string>(line => { Append(line); _log.Info($"convert: {line}"); });
            var progressIndex = inputIndex;
            var measured = new Progress<ConversionProgressUpdate>(update =>
            {
                ConversionProgress.Value = (progressIndex + update.Fraction) / inputs.Count;
                ConversionProgressText.Text = $"{Math.Min(99, Math.Floor(ConversionProgress.Value * 100)):0}% · Scenario {progressIndex + 1}/{inputs.Count} · {update.PhaseText}";
            });
            await ScenarioConverter.ConvertAsync(script, input, _scenarioDirectory,
                conversionLog, cancellation, measured);
            ConversionProgress.Value = (inputIndex + 1.0) / inputs.Count;
            ScenariosUpdated?.Invoke(this, EventArgs.Empty);
        }
        ConversionProgress.Value = 1;
        ConversionProgressText.Text = "100% · Conversion and verification complete";
        ConvertStageText.Text = "Complete — scenarios have been added to the player.";
    }

    private void Append(string line)
    {
        _logText.AppendLine(line);
        if (_logText.Length > 32000) _logText.Remove(0, _logText.Length - 24000);
        LogText.Text = _logText.ToString();
        ConversionLog.ScrollToEnd();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DatasetCatalog.DatasetPage) { UseShellExecute = true }); }
        catch (Exception error) { Append(error.Message); }
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_cancellation is not null)
        {
            e.Cancel = true;
            _closeAfterDownload = true;
            _cancellation.Cancel();
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _downloader.Dispose();
        _segmentDownloader.Dispose();
        base.OnClosed(e);
    }
}

internal sealed class ChunkRow(DatasetChunk file) : INotifyPropertyChanged
{
    public DatasetChunk File { get; } = file;
    public string Title => File.Title;
    public string Description => File.Description;
    public string SizeText => File.SizeText;
    public string StatusText { get; private set; } = "";
    public string Location { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(DatasetDownloadItem item)
    {
        StatusText = item.StatusText;
        Location = Path.Combine(item.Directory, item.File.FileName);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Location)));
    }
}
