using System.IO;
using System.Windows;
using System.Windows.Threading;
using CANVideoEmulator.Wpf.Services;
using CANVideoEmulator.Wpf.Views;

namespace CANVideoEmulator.Wpf;

public partial class App : Application
{
    private AppSettings _settings = new();
    private AppLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The setup dialog becomes WPF's first MainWindow. Closing it must not
        // shut down the process before the actual player is created.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = AppSettings.Load();
        _log = new AppLog(AppSettings.LogDirectory);
        _log.Info($"CANVideoEmulator starting; portable={AppSettings.IsPortable}, " +
                  $"config={AppSettings.ConfigPath}");

        if (_settings.LoadError is { } loadError)
        {
            _log.Warning(loadError);
        }

        // Requirement 64: an unhandled exception anywhere must produce a readable
        // message and a log entry, never a silent disappearance mid-exhibition.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log?.Error("unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        if (e.Args.Contains("--setup", StringComparer.OrdinalIgnoreCase) || ShouldRunFirstRunWizard())
        {
            var wizard = new FirstRunWizard(_settings);
            if (wizard.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            _settings.FirstRunCompleted = true;
            if (_settings.Save() is { } saveError) _log.Warning(saveError);
        }

        var window = new MainWindow(_settings, _log);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    /// <summary>
    /// Requirement 62: guide the first setup, then stay out of the way. It is
    /// also shown when the configured scenario folder has gone missing, which is
    /// the common case after copying the app to a new machine without its data.
    /// </summary>
    private bool ShouldRunFirstRunWizard() =>
        !_settings.FirstRunCompleted ||
        !Directory.Exists(_settings.EffectiveScenarioDirectory);

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("unhandled UI exception", e.Exception);
        e.Handled = true;

        MessageBox.Show(
            $"Something went wrong, but the application is still running.\n\n" +
            $"{e.Exception.Message}\n\n" +
            $"Details were written to:\n{AppSettings.LogDirectory}",
            "CANVideoEmulator", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("exiting");
        _log?.Dispose();
        base.OnExit(e);
    }
}
