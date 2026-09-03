using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CanReplayPlayer.Core.Can;
using CanReplayPlayer.Pcan;
using CanReplayPlayer.Wpf.Services;

namespace CanReplayPlayer.Wpf.Views;

/// <summary>
/// The six-step first-run check (requirement 62).
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

    public FirstRunWizard(AppSettings settings)
    {
        _settings = settings;
        _scenarioDirectory = settings.EffectiveScenarioDirectory;

        InitializeComponent();

        BitrateBox.ItemsSource = PcanBasicTransport.SupportedBitrates
            .Select(b => $"{b:N0} bit/s").ToList();
        BitrateBox.SelectedIndex = Math.Max(0,
            PcanBasicTransport.SupportedBitrates.ToList().IndexOf(settings.Bitrate));

        Recheck();
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

        if (!Directory.Exists(_scenarioDirectory))
        {
            ScenarioResult.Text =
                "This folder does not exist. Choose the folder that holds one sub-folder " +
                "per scenario, each containing a scenario.json.";
            ScenarioResult.Foreground = (Brush)FindResource("Warn");
            return;
        }

        var count = Directory.EnumerateDirectories(_scenarioDirectory)
            .Count(d => File.Exists(Path.Combine(d, "scenario.json")));

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

        ChannelBox.ItemsSource = availability.Channels;
        if (availability.Channels.Count > 0)
        {
            var remembered = availability.Channels
                .FirstOrDefault(c => c.Channel.ToString() == _settings.PcanChannel);
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
                DriverDetail.Text = string.Join("\n",
                    availability.Channels.Select(c => $"  · {c}"));
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
        var hasScenarios = Directory.Exists(_scenarioDirectory) &&
                           Directory.EnumerateDirectories(_scenarioDirectory)
                               .Any(d => File.Exists(Path.Combine(d, "scenario.json")));
        var hasChannel = ChannelBox.Items.Count > 0;

        ReadyText.Text = (hasScenarios, hasChannel) switch
        {
            (true, true) =>
                "Ready. Select a scenario, tick “Transmit to PCAN-USB”, and press PLAY.",
            (true, false) =>
                "Ready for Demo Mode. Scenarios will play with video, but nothing will " +
                "reach the CAN bus until a PCAN-USB is connected.",
            (false, true) =>
                "A PCAN-USB is ready, but there are no scenarios to play yet.",
            _ =>
                "Neither scenarios nor a PCAN-USB were found. The application will still " +
                "start; use Change Folder and Refresh once they are available.",
        };

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
        Close();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
