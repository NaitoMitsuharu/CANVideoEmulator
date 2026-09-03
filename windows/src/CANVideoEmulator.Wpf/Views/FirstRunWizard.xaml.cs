using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CANVideoEmulator.Pcan;
using CANVideoEmulator.Wpf.Services;

namespace CANVideoEmulator.Wpf.Views;

/// <summary>
/// Four pages for first-run setup, with a fixed navigation area.
/// </summary>
/// <remarks>
/// Deliberately non-blocking: every step reports what it found, but none of them
/// can stop the user pressing Start. Demo Mode is a supported way to run
/// (requirement 66), so a missing driver or an empty scenario folder is
/// information, not a gate.
/// </remarks>
public partial class FirstRunWizard : Window
{
    private const string DriverDownloadUrl = "https://www.peak-system.com/quick/DrvSetup";

    private readonly AppSettings _settings;
    private string _scenarioDirectory;
    private int _step;
    private readonly FrameworkElement[] _pages;
    private readonly System.Windows.Controls.TextBlock[] _labels;
    private bool _hasScenarios;


    public FirstRunWizard(AppSettings settings)
    {
        _settings = settings;
        _scenarioDirectory = settings.EffectiveScenarioDirectory;

        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        _pages = [FolderPage, DevicePage, SettingsPage, ReadyPage];
        _labels = [StepLabel0, StepLabel1, StepLabel2, StepLabel3];

        BitrateBox.ItemsSource = PcanBasicTransport.SupportedBitrates
            .Select(b => $"{b:N0} bit/s").ToList();
        BitrateBox.SelectedIndex = Math.Max(0,
            PcanBasicTransport.SupportedBitrates.ToList().IndexOf(settings.Bitrate));

        Recheck();
        ShowStep(0, animate: false);
    }

    private void Recheck()
    {
        CheckScenarios();
        CheckPcan();
        UpdateReady();
    }

    private void CheckScenarios()
    {
        ScenarioPath.Text = _scenarioDirectory;
        _hasScenarios = false;

        if (!Directory.Exists(_scenarioDirectory))
        {
            ScenarioResult.Text =
                "This folder does not exist. Choose the folder that holds one sub-folder " +
                "per scenario, each containing a scenario.json.";
            ScenarioResult.Foreground = (Brush)FindResource("Warn");
            return;
        }

        int count;
        try
        {
            count = Directory.EnumerateDirectories(_scenarioDirectory)
                .Count(d => File.Exists(Path.Combine(d, "scenario.json")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ScenarioResult.Text = "This folder cannot be read. Choose another folder or continue and add scenarios later.";
            ScenarioResult.Foreground = (Brush)FindResource("Warn");
            return;
        }
        _hasScenarios = count > 0;

        if (count == 0)
        {
            ScenarioResult.Text =
                "No scenarios found here. Build some with the Scenario Builder " +
                "(see README.md), or choose another folder.";
            ScenarioResult.Foreground = (Brush)FindResource("Warn");
        }
        else
        {
            ScenarioResult.Text = $"Found {count} scenario(s).";
            ScenarioResult.Foreground = (Brush)FindResource("Good");
        }
    }

    private void CheckPcan()
    {
        var availability = PcanEnvironment.Probe();

        var chosenChannel = (ChannelBox.SelectedItem as PcanChannelDescriptor?)?.Channel.ToString()
                            ?? _settings.PcanChannel;
        ChannelBox.ItemsSource = availability.Channels;
        if (availability.Channels.Count > 0)
        {
            var remembered = availability.Channels
                .FirstOrDefault(c => c.Channel.ToString() == chosenChannel);
            ChannelBox.SelectedItem = remembered.ChannelName is null
                ? availability.Channels[0]
                : remembered;
        }

        DriverPageButton.Visibility =
            availability.State == PcanApiState.NativeLibraryMissing
                ? Visibility.Visible
                : Visibility.Collapsed;

        switch (availability.State)
        {
            case PcanApiState.NativeLibraryMissing:
                DriverResult.Text = "PCAN-USB driver: NOT INSTALLED";
                DriverResult.Foreground = (Brush)FindResource("Warn");
                DriverDetail.Text = availability.Detail +
                    "\n\nYou can still run in Demo Mode: the video plays and the CAN " +
                    "scheduler runs, but nothing is transmitted.";
                break;

            case PcanApiState.NoDevices:
                DriverResult.Text = "PCAN-USB driver: installed · device: NOT CONNECTED";
                DriverResult.Foreground = (Brush)FindResource("Warn");
                DriverDetail.Text =
                    $"PCAN-Basic {availability.ApiVersion ?? "?"} is present " +
                    $"(PCANBasic.dll {availability.NativeVersion ?? "?"}), but no PCAN-USB " +
                    "is attached. Plug one in and press Re-check.";
                break;

            case PcanApiState.Available:
                DriverResult.Text =
                    $"PCAN-USB driver: installed · {availability.Channels.Count} channel(s) found";
                DriverResult.Foreground = (Brush)FindResource("Good");
                DriverDetail.Text = "USB interface detected. Choose a channel on the next step. " +
                    "Detection does not confirm delivery to the CAN network.";
                break;

            default:
                DriverResult.Text = "PCAN-Basic: ERROR";
                DriverResult.Foreground = (Brush)FindResource("Bad");
                DriverDetail.Text = availability.Detail;
                break;
        }
    }

    private void UpdateReady()
    {
        var hasScenarios = _hasScenarios;
        var hasChannel = ChannelBox.Items.Count > 0;

        ReadyText.Text = (hasScenarios, hasChannel) switch
        {
            (true, true) =>
                "Ready. Select a scenario, check the CAN settings and press PLAY.",
            (true, false) =>
                "For video-only playback, turn off Transmit to PCAN-USB. Nothing will " +
                "reach the CAN bus until a PCAN-USB is connected.",
            (false, true) =>
                "A PCAN-USB is ready, but there are no scenarios to play yet.",
            _ =>
                "Neither scenarios nor a PCAN-USB were found. The application will still " +
                "start; add scenarios and connect a PCAN-USB when ready.",
        };

        ReadySummary.Text = $"Channel: {(ChannelBox.SelectedItem as PcanChannelDescriptor?)?.DisplayName ?? "none (demo mode)"}\n" +
                            $"Bit rate: {BitrateBox.SelectedItem} · CAN output: on (starts on PLAY)";
        ReadyText.Foreground = (Brush)FindResource(
            hasScenarios ? "TextPrimary" : "Warn");
    }

    private void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Scenarios folder",
            InitialDirectory = Directory.Exists(_scenarioDirectory)
                ? _scenarioDirectory
                : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _scenarioDirectory = dialog.FolderName;
            Recheck();
        }
    }

    private void OnRecheckClick(object sender, RoutedEventArgs e) => Recheck();

    private void OnOpenDriverPageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DriverDownloadUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(this, $"Please visit:\n\n{DriverDownloadUrl}",
                "PEAK Driver Download", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowStep(int target, bool animate = true)
    {
        var direction = target >= _step ? 1 : -1;
        _step = Math.Clamp(target, 0, _pages.Length - 1);
        for (var i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
            _labels[i].Foreground = (Brush)FindResource(i == _step ? "Accent" : "TextSecondary");
            _labels[i].FontWeight = i == _step ? FontWeights.SemiBold : FontWeights.Normal;
        }
        StepCounter.Text = $"STEP {_step + 1} / {_pages.Length}";
        StepProgress.Value = _step + 1;
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == _pages.Length - 1 ? "Finish & Open Player" : "Next";
        if (_step == _pages.Length - 1) UpdateReady();

        // Replace any in-flight animation, so rapid Back/Next clicks cannot
        // leave the navigation disabled or display the wrong page.
        StepContent.BeginAnimation(OpacityProperty, null);
        StepTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            var duration = TimeSpan.FromMilliseconds(200);
            StepContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            StepTranslation.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(24 * direction, 0, duration)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        NextButton.Focus();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowStep(_step - 1);
    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step < _pages.Length - 1) ShowStep(_step + 1);
        else OnStartClick(sender, e);
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _settings.ScenarioDirectory = _scenarioDirectory;

        if (ChannelBox.SelectedItem is PcanChannelDescriptor channel)
        {
            _settings.PcanChannel = channel.Channel.ToString();
        }

        if (BitrateBox.SelectedIndex >= 0 &&
            BitrateBox.SelectedIndex < PcanBasicTransport.SupportedBitrates.Count)
        {
            _settings.Bitrate = PcanBasicTransport.SupportedBitrates[BitrateBox.SelectedIndex];
        }

        DialogResult = true;
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
