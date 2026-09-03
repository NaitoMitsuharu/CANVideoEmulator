using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CanReplayPlayer.Video;
using CanReplayPlayer.Wpf.Services;
using CanReplayPlayer.Wpf.ViewModels;
using CanReplayPlayer.Wpf.Views;

namespace CanReplayPlayer.Wpf;

public partial class MainWindow : Window
{
    /// <summary>PEAK's own short link to the Windows driver setup.</summary>
    private const string DriverDownloadUrl = "https://www.peak-system.com/quick/DrvSetup";

    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private MainViewModel? _model;

    public MainWindow(AppSettings settings, AppLog log)
    {
        _settings = settings;
        _log = log;

        InitializeComponent();
        RestoreWindowPlacement();

        // The MediaElement only exists once the XAML tree is built, so the video
        // player -- and therefore the view model -- is created here rather than in
        // App.
        var video = new MediaElementVideoPlayer(Video);
        _model = new MainViewModel(settings, log, video);
        DataContext = _model;
    }

    private void RestoreWindowPlacement()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top &&
            IsOnAVisibleScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Guard against restoring onto a monitor that is no longer attached -- an
    /// exhibition laptop is routinely moved between a desk and a booth screen,
    /// and a window restored off-screen looks like the app failed to start.
    /// </summary>
    private static bool IsOnAVisibleScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 &&
        top >= SystemParameters.VirtualScreenTop - 50 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    private void OnSeekBarReleased(object sender, MouseButtonEventArgs e) => Seek();

    private void OnSeekDragCompleted(object sender, RoutedEventArgs e) => Seek();

    private void Seek() => _model?.SeekToProgress(SeekSlider.Value);

    private void OnCopyBenchReportClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_model.BenchReportForClipboard());
        }
        catch (Exception error)
        {
            // The clipboard can be held by another process; say so rather than
            // appearing to have copied nothing.
            _log.Warning($"could not copy the bench report: {error.Message}");
            MessageBox.Show(this,
                "The clipboard is currently locked by another application. " +
                "Close it and try again.",
                "Copy Test Report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        new DiagnosticsWindow(_model.CollectDiagnostics()) { Owner = this }.ShowDialog();
    }

    private void OnOpenDriverPageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DriverDownloadUrl) { UseShellExecute = true });
            _log.Info($"opened PEAK driver download page: {DriverDownloadUrl}");
        }
        catch (Exception error)
        {
            _log.Error("could not open the driver download page", error);
            MessageBox.Show(this,
                $"Could not open a browser. Please visit:\n\n{DriverDownloadUrl}",
                "PEAK Driver Download", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnGetScenariosClick(object sender, RoutedEventArgs e)
    {
        var directory = _model?.ScenarioDirectory ?? AppSettings.DefaultScenarioDirectory;
        new GetScenariosWindow(_log, directory) { Owner = this }.ShowDialog();

        // The download stops at the raw archive, so nothing new is playable yet;
        // reload anyway in case the operator built packages while it was open.
        _model?.ReloadScenarios();
    }

    private void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Scenarios folder",
            InitialDirectory = _model?.ScenarioDirectory ?? AppSettings.DefaultScenarioDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _model?.SetScenarioDirectory(dialog.FolderName);
        }
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }

        if (_model is { } model)
        {
            _model = null;
            model.PersistSettings();
            // Requirement 24: the scheduler is cancelled and PCAN uninitialised
            // before the process exits, never left to a finalizer.
            await model.DisposeAsync();
        }
    }
}
