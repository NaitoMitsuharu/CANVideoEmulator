using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CANVideoEmulator.Video;
using CANVideoEmulator.Wpf.Services;
using CANVideoEmulator.Wpf.ViewModels;
using CANVideoEmulator.Wpf.Views;

namespace CANVideoEmulator.Wpf;

public partial class MainWindow : Window
{
    /// <summary>PEAK's own short link to the Windows driver setup.</summary>
    private const string DriverDownloadUrl = "https://www.peak-system.com/quick/DrvSetup";

    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private MainViewModel? _model;
    private readonly DispatcherTimer _drawerHideTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _drawerOpen;
    private bool _drawerKeyboardMode;

    public MainWindow(AppSettings settings, AppLog log)
    {
        _settings = settings;
        _log = log;

        InitializeComponent();
        _drawerHideTimer.Tick += (_, _) =>
        {
            _drawerHideTimer.Stop();
            if (!ScenarioDrawer.IsMouseOver && !ScenarioRevealArea.IsMouseOver &&
                !(_drawerKeyboardMode && ScenarioDrawer.IsKeyboardFocusWithin))
                SetScenarioDrawer(false);
        };
        PreviewKeyDown += OnBrowserKeyDown;
        Deactivated += (_, _) => SetScenarioDrawer(false);
        RestoreWindowPlacement();

        // The MediaElement only exists once the XAML tree is built, so the video
        // player -- and therefore the view model -- is created here rather than in
        // App.
        var video = new MediaElementVideoPlayer(Video);
        _model = new MainViewModel(settings, log, video);
        DataContext = _model;
        Loaded += (_, _) => OnRecentCanSizeChanged(this, null!);
    }

    private void OnScenarioAreaEnter(object sender, MouseEventArgs e)
    {
        _drawerKeyboardMode = false;
        _drawerHideTimer.Stop();
        SetScenarioDrawer(true);
    }

    private void OnScenarioAreaLeave(object sender, MouseEventArgs e) => ScheduleDrawerHide();
    private void OnScenarioAreaFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _drawerKeyboardMode = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        _drawerHideTimer.Stop();
    }
    private void OnScenarioAreaBlur(object sender, KeyboardFocusChangedEventArgs e) => ScheduleDrawerHide();
    private void ScheduleDrawerHide()
    {
        _drawerHideTimer.Stop();
        _drawerHideTimer.Start();
    }

    private void OnShowScenariosClick(object sender, RoutedEventArgs e)
    {
        SetScenarioDrawer(true);
        ScenarioList.Focus();
        _drawerKeyboardMode = e is KeyEventArgs || !ScenarioRevealButton.IsMouseOver;
    }

    private void OnBrowserKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnShowScenariosClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _drawerOpen)
        {
            SetScenarioDrawer(false);
            e.Handled = true;
        }
    }

    private void SetScenarioDrawer(bool open)
    {
        if (_drawerOpen == open) return;
        _drawerOpen = open;
        _drawerHideTimer.Stop();
        if (!open && ScenarioDrawer.IsKeyboardFocusWithin) ScenarioRevealButton.Focus();
        var from = ScenarioDrawer.Visibility == Visibility.Visible ? DrawerTranslation.Y : ScenarioDrawer.Height + 40;
        ScenarioDrawer.Visibility = Visibility.Visible;
        var destination = open ? 0 : ScenarioDrawer.Height + 40;
        var animation = new DoubleAnimation(from, destination,
            TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 220 : 0))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) =>
        {
            // Keep the tile layout ready while hidden, so opening the drawer
            // does not spend its animation time creating thumbnail controls.
            if (!_drawerOpen) ScenarioDrawer.Visibility = Visibility.Hidden;
        };
        DrawerTranslation.BeginAnimation(TranslateTransform.YProperty, animation);
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

    private void OnRecentCanSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_model is null) return;
        _model.RecentCan.SetDisplayRows((int)Math.Max(0, Math.Floor(
            (RecentCanViewport.ActualHeight - RecentCanGrid.ColumnHeaderHeight - 2) / RecentCanGrid.RowHeight)));
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
        var dialog = new GetScenariosWindow(_log, directory, _settings) { Owner = this };
        dialog.ScenariosUpdated += (_, _) => _model?.ReloadScenarios();
        dialog.ShowDialog();

        // Also pick up packages created outside this dialog.
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
        _drawerHideTimer.Stop();

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
