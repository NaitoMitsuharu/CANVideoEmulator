using System.Diagnostics;
using System.IO;
using System.Windows;
using CanReplayPlayer.Pcan;
using CanReplayPlayer.Wpf.Services;

namespace CanReplayPlayer.Wpf.Views;

public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsReport _report;

    public DiagnosticsWindow(DiagnosticsReport report)
    {
        _report = report;
        InitializeComponent();
        Grid.ItemsSource = report.Lines
            .Select(l => new { l.Section, l.Name, l.Value })
            .ToList();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_report.ToText());
            CopiedNotice.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // The clipboard can be locked by another process; say so rather than
            // silently doing nothing.
            MessageBox.Show(this,
                "The clipboard is currently locked by another application. " +
                "Close it and try again.",
                "Copy Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.LogDirectory);
            Process.Start(new ProcessStartInfo(AppSettings.LogDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            MessageBox.Show(this,
                $"Could not open {AppSettings.LogDirectory}:\n{error.Message}",
                "Open Log Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
