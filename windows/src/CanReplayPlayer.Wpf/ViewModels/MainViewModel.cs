using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CanReplayPlayer.Core.Can;
using CanReplayPlayer.Core.Playback;
using CanReplayPlayer.Core.Video;
using CanReplayPlayer.Pcan;
using CanReplayPlayer.Scenarios;
using CanReplayPlayer.Wpf.Services;

namespace CanReplayPlayer.Wpf.ViewModels;

/// <summary>
/// The main window's state and commands.
/// </summary>
/// <remarks>
/// Owns the wiring between the UI and the UI-free session, and nothing else: no
/// timing, no CAN, no seeking logic lives here. Everything that could affect what
/// goes on the bus is in <see cref="ReplaySession"/> and the Core, which is what
/// makes those parts testable without a window.
/// </remarks>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly PlaybackClock _clock = new();
    private readonly PcanManager _pcan;
    private readonly DispatcherTimer _uiTimer;
    private readonly IVideoPlayer _video;

    private CanScheduler _scheduler;
    private ReplaySession _session;
    private ICanTransport _transport;
    private ScenarioLibrary _library;

    private bool _suppressBusChange;
    private bool _hardwareAutoEnabled;
    private bool _disposed;

    public MainViewModel(AppSettings settings, AppLog log, IVideoPlayer video)
    {
        _settings = settings;
        _log = log;
        _video = video;

        _pcan = new PcanManager();
        _pcan.Bitrate = settings.Bitrate;
        _transport = new NullCanTransport();
        _transport.Open();

        _scheduler = new CanScheduler(_clock, _transport);
        _scheduler.FrameSent += frame => RecentCan.Add(in frame);
        _scheduler.SendFailed += OnSendFailed;
        _scheduler.BusHealthChanged += status => Dispatch(() =>
        {
            // Api.Write only queues, so this is the only signal that frames are
            // not actually reaching the wire.
            ReportWarning($"CAN bus fault: {status.Detail}");
            RefreshPcanUi();
        });

        _library = LoadLibrary(settings.EffectiveScenarioDirectory);
        _session = CreateSession();

        _video.Failed += (_, message) => ReportWarning(message);

        _pcan.AvailabilityChanged += (_, _) => Dispatch(RefreshPcanUi);
        _pcan.SelectionChanged += (_, _) => Dispatch(RefreshPcanUi);
        _pcan.ConnectionLost += (_, message) => Dispatch(() =>
        {
            ReportWarning(message);
            FallBackToDemoTransport();
            RefreshPcanUi();
        });

        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(() => Run(() => _session.Stop()));
        NextCommand = new RelayCommand(() => Run(() => _session.Next()));
        PreviousCommand = new RelayCommand(() => Run(() => _session.Previous()));
        SkipForwardCommand = new RelayCommand(() => Run(() => _session.SkipForward()));
        SkipBackwardCommand = new RelayCommand(() => Run(() => _session.SkipBackward()));
        RefreshPcanCommand = new RelayCommand(RefreshPcan);
        TestConnectionCommand = new RelayCommand(TestConnection);
        ReloadScenariosCommand = new RelayCommand(ReloadScenarios);
        RunBenchCommand = new RelayCommand(
            parameter => _ = RunBenchAsync(parameter),
            _ => BenchIdle && _session.Current is not null);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / RecentCanMonitor.RefreshHz),
        };
        _uiTimer.Tick += (_, _) => Tick();
        _uiTimer.Start();

        ApplySettingsToSession();
        RefreshPcanUi();
        SelectInitialScenario();
    }

    // -- collections ---------------------------------------------------------

    public ObservableCollection<ScenarioCardViewModel> Scenarios { get; } = [];

    public ObservableCollection<Playlist> Playlists { get; } = [];

    public ObservableCollection<int> AvailableBuses { get; } = [];

    public ObservableCollection<PcanChannelDescriptor> PcanChannels { get; } = [];

    public RecentCanMonitor RecentCan { get; } = new();

    public static IReadOnlyList<int> Bitrates => PcanBasicTransport.SupportedBitrates;

    public static IReadOnlyList<LoopMode> LoopModes { get; } =
        [LoopMode.PlaylistAdvance, LoopMode.PlaylistLoop, LoopMode.SingleScenarioLoop];

    // -- commands ------------------------------------------------------------

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand SkipForwardCommand { get; }
    public RelayCommand SkipBackwardCommand { get; }
    public RelayCommand RefreshPcanCommand { get; }
    public RelayCommand TestConnectionCommand { get; }
    public RelayCommand ReloadScenariosCommand { get; }

    /// <summary>Runs a fixed-duration transmit test; the parameter is seconds.</summary>
    public RelayCommand RunBenchCommand { get; }

    /// <summary>The 1 / 5 / 30 / 60 second bring-up ladder.</summary>
    public static IReadOnlyList<int> BenchDurationsSeconds { get; } =
        BenchRun.StandardDurations.Select(d => (int)d.TotalSeconds).ToArray();

    // -- observable state ------------------------------------------------------

    private ScenarioCardViewModel? _selectedScenario;
    public ScenarioCardViewModel? SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (_selectedScenario == value)
            {
                return;
            }

            if (_selectedScenario is not null)
            {
                _selectedScenario.IsSelected = false;
            }

            _selectedScenario = value;
            if (value is not null)
            {
                value.IsSelected = true;
                Run(() => _session.LoadScenario(value.Package, bus: null, autoPlay: false));
            }

            Raise();
        }
    }

    private Playlist? _selectedPlaylist;
    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            if (!Set(ref _selectedPlaylist, value) || value is null)
            {
                return;
            }

            _session.SelectPlaylist(value.PlaylistId);
            _settings.PlaylistId = value.PlaylistId;
            _log.Info($"playlist selected: {value.Title} ({value.Count} scenarios)");
        }
    }

    private int _selectedBus;
    public int SelectedBus
    {
        get => _selectedBus;
        set
        {
            if (!Set(ref _selectedBus, value) || _suppressBusChange)
            {
                return;
            }

            Run(() => _session.SelectBus(value));
        }
    }

    private PcanChannelDescriptor? _selectedPcanChannel;
    public PcanChannelDescriptor? SelectedPcanChannel
    {
        get => _selectedPcanChannel;
        set
        {
            if (!Set(ref _selectedPcanChannel, value) || value is null)
            {
                return;
            }

            _pcan.Select(value.Value);
            _settings.PcanChannel = value.Value.Channel.ToString();
        }
    }

    private int _bitrate = 500_000;
    public int Bitrate
    {
        get => _bitrate;
        set
        {
            if (!Set(ref _bitrate, value))
            {
                return;
            }

            try
            {
                _pcan.Bitrate = value;
                _settings.Bitrate = value;
                _log.Info($"CAN bit rate set to {value / 1000} kbit/s");
            }
            catch (ArgumentOutOfRangeException error)
            {
                ReportWarning(error.Message);
            }

            Raise(nameof(BitrateMismatchWarning));
            Raise(nameof(HasBitrateMismatch));
        }
    }

    private LoopMode _loopMode = LoopMode.PlaylistAdvance;
    public LoopMode LoopMode
    {
        get => _loopMode;
        set
        {
            if (Set(ref _loopMode, value))
            {
                _session.Options.LoopMode = value;
                _settings.LoopMode = value.ToString();
            }
        }
    }

    private bool _useHardware;
    /// <summary>Send to PCAN-USB rather than to the in-memory demo transport.</summary>
    public bool UseHardware
    {
        get => _useHardware;
        set
        {
            if (!Set(ref _useHardware, value))
            {
                return;
            }

            if (value)
            {
                EnableHardwareTransport();
            }
            else
            {
                FallBackToDemoTransport();
            }

            RefreshPcanUi();
        }
    }

    private string _positionText = "00:00";
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }

    private string _durationText = "00:00";
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }

    private double _progress;
    /// <summary>Seek bar value, 0..1. Setting it seeks (requirement 10).</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.00001)
            {
                return;
            }

            _progress = value;
            Raise();
        }
    }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    private string _playbackStateText = "Stopped";
    public string PlaybackStateText
    {
        get => _playbackStateText;
        private set => Set(ref _playbackStateText, value);
    }

    private string _pcanStatusText = "Not Connected";
    public string PcanStatusText { get => _pcanStatusText; private set => Set(ref _pcanStatusText, value); }

    private string _pcanStatusDetail = string.Empty;
    public string PcanStatusDetail
    {
        get => _pcanStatusDetail;
        private set => Set(ref _pcanStatusDetail, value);
    }

    private CanTransportHealth _pcanHealth = CanTransportHealth.NotConnected;
    public CanTransportHealth PcanHealth { get => _pcanHealth; private set => Set(ref _pcanHealth, value); }

    private bool _driverMissing;
    public bool DriverMissing { get => _driverMissing; private set => Set(ref _driverMissing, value); }

    private string _channelText = "-";
    public string ChannelText { get => _channelText; private set => Set(ref _channelText, value); }

    private string _framesScheduledText = "0";
    public string FramesScheduledText
    {
        get => _framesScheduledText;
        private set => Set(ref _framesScheduledText, value);
    }

    private string _framesSentText = "0";
    public string FramesSentText { get => _framesSentText; private set => Set(ref _framesSentText, value); }

    private string _frameRateText = "0";
    public string FrameRateText { get => _frameRateText; private set => Set(ref _frameRateText, value); }

    private string _errorsText = "0";
    public string ErrorsText { get => _errorsText; private set => Set(ref _errorsText, value); }

    private string _jitterText = "-";
    public string JitterText { get => _jitterText; private set => Set(ref _jitterText, value); }

    private bool _hasJitterDetail;
    public bool HasJitterDetail
    {
        get => _hasJitterDetail;
        private set => Set(ref _hasJitterDetail, value);
    }

    private bool _hasRecentCan;
    public bool HasRecentCan
    {
        get => _hasRecentCan;
        private set => Set(ref _hasRecentCan, value);
    }

    private string _jitterDetailText = "-";
    public string JitterDetailText
    {
        get => _jitterDetailText;
        private set => Set(ref _jitterDetailText, value);
    }

    private string _lossText = "0 (0.000 %)";
    public string LossText { get => _lossText; private set => Set(ref _lossText, value); }

    private string _benchStatus = string.Empty;
    /// <summary>Result of the most recent bench run, ready to paste into a report.</summary>
    public string BenchStatus { get => _benchStatus; private set => Set(ref _benchStatus, value); }

    private bool _benchSucceeded = true;
    /// <summary>False when the most recent run's verdict was not OK.</summary>
    public bool BenchSucceeded
    {
        get => _benchSucceeded;
        private set
        {
            if (Set(ref _benchSucceeded, value))
            {
                Raise(nameof(BenchFailed));
            }
        }
    }

    public bool BenchFailed => !_benchSucceeded;

    private bool _benchRunning;
    public bool BenchRunning
    {
        get => _benchRunning;
        private set
        {
            if (Set(ref _benchRunning, value))
            {
                Raise(nameof(BenchIdle));
            }
        }
    }

    public bool BenchIdle => !_benchRunning;

    private string _recentCanCaption = $"newest {RecentCanMonitor.DisplayRows} frames";
    public string RecentCanCaption
    {
        get => _recentCanCaption;
        private set => Set(ref _recentCanCaption, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    private string _vehicleText = "-";
    public string VehicleText { get => _vehicleText; private set => Set(ref _vehicleText, value); }

    private string _scenarioTitleText = "No scenario loaded";
    public string ScenarioTitleText
    {
        get => _scenarioTitleText;
        private set => Set(ref _scenarioTitleText, value);
    }

    private string _busStatisticsText = string.Empty;
    public string BusStatisticsText
    {
        get => _busStatisticsText;
        private set => Set(ref _busStatisticsText, value);
    }

    public string ScenarioDirectory => _settings.EffectiveScenarioDirectory;

    public int ScenarioCount => _library.Scenarios.Count;

    /// <summary>
    /// Requirement 30: warn, do not silently transmit, when the rate the package
    /// expects to be replayed at and the interface's rate disagree.
    /// </summary>
    /// <remarks>
    /// The check is against <c>playback_bitrate</c> -- the bench rate the package
    /// was prepared for -- not <c>original_bitrate</c>. The latter describes the
    /// vehicle the CAN came from and is unknown for comma2k19; it could never
    /// drive a decision here without being invented first.
    /// </remarks>
    public bool HasBitrateMismatch =>
        _session.Current?.Manifest.EffectivePlaybackBitrate is { } expected &&
        expected != Bitrate;

    public string BitrateMismatchWarning => HasBitrateMismatch
        ? $"This scenario expects a {_session.Current!.Manifest.EffectivePlaybackBitrate / 1000} " +
          $"kbit/s bus but the interface is set to {Bitrate / 1000} kbit/s. Playing would put " +
          "frames on the bus at the wrong rate."
        : string.Empty;

    /// <summary>True when the recorded vehicle's own bus rate is not documented.</summary>
    public bool ScenarioBitrateUnknown => _session.Current?.Manifest.OriginalBitrate is null;

    public string PlaybackBitrateText =>
        _session.Current?.Manifest.EffectivePlaybackBitrate is { } rate
            ? $"Scenario expects {rate / 1000} kbit/s"
            : "Scenario states no playback bit rate";

    // -- library ---------------------------------------------------------------

    private ScenarioLibrary LoadLibrary(string directory)
    {
        try
        {
            var library = ScenarioLibrary.Load(directory);
            _log.Info($"loaded {library.Scenarios.Count} scenario(s) from {directory}");
            foreach (var error in library.LoadErrors)
            {
                _log.Warning($"scenario library: {error}");
            }

            return library;
        }
        catch (ScenarioException error)
        {
            _log.Warning(error.Message);
            StatusMessage = error.Message;
            return ScenarioLibrary.Empty(directory);
        }
    }

    private ReplaySession CreateSession()
    {
        var session = new ReplaySession(_clock, _scheduler, _video, _library,
            new ReplaySessionOptions
            {
                TransitionGap = TimeSpan.FromMilliseconds(_settings.TransitionGapMs),
                VideoSyncTolerance = TimeSpan.FromMilliseconds(_settings.VideoSyncToleranceMs),
                VideoSyncCooldown = TimeSpan.FromMilliseconds(_settings.VideoSyncCooldownMs),
                SkipStep = TimeSpan.FromSeconds(_settings.SkipStepSeconds),
                IncludeTxEcho = _settings.IncludeTxEcho,
                LoopMode = Enum.TryParse<LoopMode>(_settings.LoopMode, out var mode)
                    ? mode
                    : LoopMode.PlaylistAdvance,
            });

        session.Warning += (_, message) => Dispatch(() => ReportWarning(message));
        session.ScenarioChanged += (_, package) => Dispatch(() => OnScenarioChanged(package));
        session.BusChanged += (_, bus) => Dispatch(() => OnBusChanged(bus));
        session.PlaylistFinished += (_, _) => Dispatch(() =>
            StatusMessage = "Playlist finished.");
        return session;
    }

    private void ApplySettingsToSession()
    {
        _loopMode = _session.Options.LoopMode;
        _bitrate = _settings.Bitrate;
        RebuildScenarioList();
    }

    private void RebuildScenarioList()
    {
        Scenarios.Clear();
        foreach (var package in _library.Scenarios)
        {
            Scenarios.Add(new ScenarioCardViewModel(package));
        }

        Playlists.Clear();
        foreach (var playlist in _library.Playlists.Playlists)
        {
            Playlists.Add(playlist);
        }

        _selectedPlaylist = (_settings.PlaylistId is not null
                                ? _library.Playlists.Find(_settings.PlaylistId)
                                : null)
                            ?? _library.Playlists.Default;
        if (_selectedPlaylist is not null)
        {
            _session.SelectPlaylist(_selectedPlaylist.PlaylistId);
        }

        Raise(nameof(SelectedPlaylist));
        Raise(nameof(ScenarioCount));
        Raise(nameof(ScenarioDirectory));
    }

    private void SelectInitialScenario()
    {
        if (Scenarios.Count == 0)
        {
            StatusMessage = _library.Scenarios.Count == 0
                ? $"No scenarios found in {ScenarioDirectory}. Build some with the " +
                  "Scenario Builder, or point the app at another folder."
                : string.Empty;
            return;
        }

        var firstId = SelectedPlaylist?.ScenarioIds.FirstOrDefault();
        SelectedScenario = firstId is not null
            ? Scenarios.FirstOrDefault(s => s.ScenarioId == firstId) ?? Scenarios[0]
            : Scenarios[0];
    }

    public void ReloadScenarios()
    {
        Run(() =>
        {
            _session.Stop();
            _library = LoadLibrary(_settings.EffectiveScenarioDirectory);
            _session.SetLibrary(_library);
            _selectedScenario = null;
            RebuildScenarioList();
            SelectInitialScenario();
            StatusMessage = $"Reloaded {_library.Scenarios.Count} scenario(s).";
        });
    }

    public void SetScenarioDirectory(string directory)
    {
        _settings.ScenarioDirectory = directory;
        ReloadScenarios();
    }

    // -- session events --------------------------------------------------------

    private void OnScenarioChanged(ScenarioPackage package)
    {
        ScenarioTitleText = package.Title;
        VehicleText = package.Manifest.Vehicle;
        DurationText = Format(package.Duration);
        RecentCan.Clear();

        _suppressBusChange = true;
        AvailableBuses.Clear();
        foreach (var bus in package.AvailableBuses)
        {
            AvailableBuses.Add(bus);
        }

        _suppressBusChange = false;

        var card = Scenarios.FirstOrDefault(s => s.ScenarioId == package.ScenarioId);
        if (card is not null && !ReferenceEquals(card, _selectedScenario))
        {
            if (_selectedScenario is not null)
            {
                _selectedScenario.IsSelected = false;
            }

            _selectedScenario = card;
            card.IsSelected = true;
            Raise(nameof(SelectedScenario));
        }

        Raise(nameof(HasBitrateMismatch));
        Raise(nameof(BitrateMismatchWarning));
        Raise(nameof(ScenarioBitrateUnknown));
        _log.Info($"scenario loaded: {package.ScenarioId} " +
                  $"({package.Duration.TotalSeconds:F1} s, buses " +
                  $"{string.Join(",", package.AvailableBuses)}, default {package.DefaultBus})");
    }

    private void OnBusChanged(int bus)
    {
        _suppressBusChange = true;
        _selectedBus = bus;
        Raise(nameof(SelectedBus));
        _suppressBusChange = false;

        var statistics = _session.Current?.StatisticsFor(bus);
        BusStatisticsText = statistics is null
            ? string.Empty
            : $"{statistics.FrameCount:N0} frames · {statistics.FramesPerSecond:F0} fps · " +
              $"{statistics.UniqueCanIds} IDs · avg DLC {statistics.AverageDlc:F2} · " +
              $"~{statistics.EstimatedBusLoadBps / 1000.0:F0} kbit/s";
        RecentCan.Clear();
    }

    private void OnSendFailed(string message) => Dispatch(() =>
    {
        // A stream of identical send errors must not flood the log or the status
        // line; the counter in the statistics panel carries the volume.
        if (StatusMessage != message)
        {
            StatusMessage = message;
            _log.Warning($"CAN send failed: {message}");
        }
    });

    // -- transport -------------------------------------------------------------

    private void EnableHardwareTransport()
    {
        Run(() =>
        {
            _session.Pause();
            try
            {
                var transport = _pcan.OpenSelected();
                SwapTransport(transport);
                StatusMessage =
                    $"CAN output armed on {transport.Name} at {Bitrate / 1000} kbit/s. " +
                    "Frames are transmitted only while playing.";
                _log.Info(StatusMessage);
            }
            catch (PcanTransportException error)
            {
                _useHardware = false;
                Raise(nameof(UseHardware));
                ReportWarning(error.Message);
            }
        });
    }

    private void FallBackToDemoTransport()
    {
        Run(() =>
        {
            _session.Pause();
            _pcan.CloseTransport();
            var transport = new NullCanTransport();
            transport.Open();
            SwapTransport(transport);
            _useHardware = false;
            Raise(nameof(UseHardware));
        });
    }

    private void SwapTransport(ICanTransport transport)
    {
        _scheduler.Stop();
        var previous = _transport;
        _transport = transport;
        _scheduler.SetTransport(transport);
        if (!ReferenceEquals(previous, transport) && previous is NullCanTransport)
        {
            previous.Dispose();
        }
    }

    private void RefreshPcan()
    {
        var availability = _pcan.Refresh();
        RefreshPcanUi();
        StatusMessage = availability.Detail;
        _log.Info($"PCAN refresh: {availability.State} -- {availability.Detail}");
    }

    /// <summary>
    /// Turn CAN output on by itself the first time a usable channel is found.
    /// </summary>
    /// <remarks>
    /// Only once, and only on a transition into "a free channel exists". If the
    /// operator deliberately unticks it, re-arming on the next poll would fight
    /// them; and re-enabling after an unplug would start transmitting the moment
    /// a cable was reseated, which is not something to do on the operator's
    /// behalf.
    /// </remarks>
    private void AutoEnableHardwareIfAvailable(PcanAvailability availability)
    {
        if (_hardwareAutoEnabled || UseHardware)
        {
            return;
        }

        if (availability.State != PcanApiState.Available || _pcan.Selected is not { } channel)
        {
            return;
        }

        if (!channel.IsFree)
        {
            return;
        }

        _hardwareAutoEnabled = true;
        _log.Info($"CAN output enabled automatically for {channel.DisplayName}");
        UseHardware = true;
    }

    private void RefreshPcanUi()
    {
        var availability = _pcan.Availability;
        DriverMissing = availability.State == PcanApiState.NativeLibraryMissing;

        PcanChannels.Clear();
        foreach (var channel in availability.Channels)
        {
            PcanChannels.Add(channel);
        }

        var selected = _pcan.Selected;
        if (!Equals(_selectedPcanChannel, selected))
        {
            _selectedPcanChannel = selected;
            Raise(nameof(SelectedPcanChannel));
        }

        ChannelText = selected?.DisplayName ?? "-";
        AutoEnableHardwareIfAvailable(availability);

        if (!UseHardware)
        {
            PcanHealth = CanTransportHealth.NotConnected;
            PcanStatusText = availability.State switch
            {
                PcanApiState.NativeLibraryMissing => "Driver Missing",
                PcanApiState.NoDevices => "Not Connected",
                _ => "Demo Mode",
            };
            PcanStatusDetail = availability.State == PcanApiState.Available
                ? $"{availability.Channels.Count} channel(s) available -- enable CAN output to transmit"
                : availability.Detail;
            return;
        }

        var status = _pcan.Transport?.RefreshStatus()
                     ?? CanTransportStatus.NotConnected("no channel open");
        PcanHealth = status.Health;
        PcanStatusText = status.Health switch
        {
            CanTransportHealth.Ok => "Connected",
            CanTransportHealth.BusLight or CanTransportHealth.BusHeavy => "Bus Errors",
            CanTransportHealth.BusOff => "BUS OFF",
            CanTransportHealth.ChannelInUse => "Channel In Use",
            CanTransportHealth.DriverMissing => "Driver Missing",
            CanTransportHealth.ApiMissing => "PCAN-Basic Missing",
            CanTransportHealth.NotConnected => "Not Connected",
            _ => "Error",
        };
        PcanStatusDetail = status.Detail;
    }

    private void TestConnection()
    {
        var result = _pcan.TestConnection();
        StatusMessage = result.Message;
        _log.Info($"TEST CONNECTION: success={result.Success}; {result.Message}");
        RefreshPcanUi();
    }

    public DiagnosticsReport CollectDiagnostics() => DiagnosticsReport.Collect(new DiagnosticsContext
    {
        Availability = _pcan.Availability,
        Selected = _pcan.Selected,
        TransportName = _transport.Name,
        TransportStatus = _transport.Status.Detail,
        BitrateBitsPerSecond = Bitrate,
        ScenarioDirectory = ScenarioDirectory,
        ScenarioCount = _library.Scenarios.Count,
        PlaylistCount = _library.Playlists.Playlists.Count,
        CurrentScenario = _session.Current?.ScenarioId,
        CurrentBus = _session.Current is null ? null : SelectedBus,
        ScenarioOriginalBitrate = _session.Current?.Manifest.OriginalBitrate,
        ScenarioPlaybackBitrate = _session.Current?.Manifest.EffectivePlaybackBitrate,
        ScenarioLoadErrors = _library.LoadErrors,
        ConfigPath = AppSettings.ConfigPath,
        LogDirectory = AppSettings.LogDirectory,
        TransitionGapMs = _session.Options.TransitionGap.TotalMilliseconds,
        VideoSyncToleranceMs = _session.Options.VideoSyncTolerance.TotalMilliseconds,
    });

    // -- playback --------------------------------------------------------------

    private void TogglePlayPause()
    {
        if (_session.Current is null)
        {
            StatusMessage = "Select a scenario first.";
            return;
        }

        if (HasBitrateMismatch && !_clock.IsPlaying && UseHardware)
        {
            // Requirement 30: refuse to start rather than transmit at a rate the
            // recording was not made at.
            StatusMessage = BitrateMismatchWarning +
                            " Change the interface bit rate, or switch off CAN output.";
            _log.Warning(BitrateMismatchWarning);
            return;
        }

        Run(() => _session.TogglePlayPause());
    }

    /// <summary>
    /// Run a fixed-duration transmit test and leave the report where the
    /// operator can copy it (phase 2 requirement 4).
    /// </summary>
    private async Task RunBenchAsync(object? parameter)
    {
        if (BenchRunning || _session.Current is null)
        {
            return;
        }

        if (!int.TryParse(parameter?.ToString(), out var seconds) || seconds <= 0)
        {
            ReportWarning($"bench duration '{parameter}' is not a positive number of seconds");
            return;
        }

        if (HasBitrateMismatch && UseHardware)
        {
            // Same rule as PLAY: never transmit at a rate the package was not
            // prepared for just because a test was requested.
            ReportWarning(BitrateMismatchWarning);
            return;
        }

        BenchRunning = true;
        RunBenchCommand.RaiseCanExecuteChanged();
        BenchStatus = $"Running {seconds} s test on {_transport.Name} ...";
        _log.Info($"bench run started: {seconds} s, transport {_transport.Name}, " +
                  $"scenario {_session.Current.ScenarioId}, bus {SelectedBus}");

        try
        {
            var bench = new BenchRun(_session, _scheduler, _clock);
            var result = await bench.RunAsync(TimeSpan.FromSeconds(seconds))
                .ConfigureAwait(true);

            BenchStatus = result.ToReport();
            BenchSucceeded = result.Succeeded;

            var counts =
                $"scheduled {result.Timing.FramesScheduled:N0}, " +
                $"sent {result.Timing.FramesSent:N0}, errors {result.Timing.SendErrors:N0}, " +
                $"P95 jitter {result.Timing.JitterP95Ms:F2} ms";

            // Lead with the verdict. Frames sent alone reads as a success even
            // when the controller never got an acknowledgement for any of them.
            StatusMessage = result.Succeeded
                ? $"{seconds} s test OK — {counts}"
                : $"{seconds} s test: {result.Verdict}  ({counts})";

            _log.Info(result.ToReport());
        }
        catch (Exception error)
        {
            BenchStatus = $"Bench run failed: {error.Message}";
            _log.Error("bench run failed", error);
        }
        finally
        {
            BenchRunning = false;
            RunBenchCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Put the latest bench report on the clipboard.</summary>
    public string BenchReportForClipboard() =>
        string.IsNullOrWhiteSpace(BenchStatus)
            ? "No bench run has been performed yet."
            : BenchStatus;

    /// <summary>Called when the user releases the seek bar (requirement 10).</summary>
    public void SeekToProgress(double fraction)
    {
        if (_session.Current is null)
        {
            return;
        }

        Run(() => _session.Seek(_session.Duration * Math.Clamp(fraction, 0, 1)));
    }

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        var position = _clock.CurrentTime;
        PositionText = Format(position);
        DurationText = Format(_clock.Duration);
        _progress = _clock.Progress;
        Raise(nameof(Progress));

        var state = _clock.State;
        IsPlaying = state == PlaybackState.Playing;
        PlaybackStateText = state.ToString();

        var snapshot = _scheduler.Snapshot();
        FramesScheduledText = snapshot.FramesScheduled.ToString("N0");
        FramesSentText = snapshot.FramesSent.ToString("N0");
        LossText = $"{snapshot.FramesLost:N0} ({snapshot.LossFraction * 100:F3} %)";
        FrameRateText = $"{snapshot.FramesPerSecond:N0}";
        ErrorsText = snapshot.SendErrors.ToString("N0");
        JitterText = snapshot.JitterSampleCount == 0
            ? "-"
            : $"{snapshot.AverageJitterMs:F2} ms avg";
        HasJitterDetail = snapshot.JitterSampleCount > 0;
        JitterDetailText = HasJitterDetail
            ? $"P50 {snapshot.JitterP50Ms:F2} · P95 {snapshot.JitterP95Ms:F2} · " +
              $"P99 {snapshot.JitterP99Ms:F2} · max {snapshot.MaxJitterMs:F2} ms"
            : string.Empty;

        if (UseHardware && _pcan.Transport is { IsOpen: true })
        {
            RefreshPcanUi();
        }

        RecentCan.Flush();
        HasRecentCan = RecentCan.Rows.Count > 0;
        RecentCanCaption = RecentCan.Observed == 0
            ? $"newest {RecentCanMonitor.DisplayRows} frames"
            : $"newest {RecentCan.Rows.Count} of {RecentCan.Observed:N0} frames seen";
        _session.TickVideoSync();
    }

    private static string Format(TimeSpan value) =>
        $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";

    // -- plumbing ---------------------------------------------------------------

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            // Requirement 64: an operation failing must never bring the app down
            // mid-exhibition.
            ReportWarning(error.Message);
            _log.Error("command failed", error);
        }
    }

    private void ReportWarning(string message)
    {
        StatusMessage = message;
        _log.Warning(message);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public void PersistSettings()
    {
        _settings.Bitrate = Bitrate;
        _settings.LoopMode = LoopMode.ToString();
        _settings.PlaylistId = SelectedPlaylist?.PlaylistId;
        _settings.PcanChannel = _pcan.Selected?.Channel.ToString();
        if (_settings.Save() is { } error)
        {
            _log.Warning(error);
        }
    }

    /// <summary>Shutdown order matters (requirement 24): stop sending, then release PCAN.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _uiTimer.Stop();

        await _session.DisposeAsync();
        _pcan.Dispose();
        _transport.Dispose();
        _log.Info("shutdown complete: scheduler cancelled, PCAN uninitialised");
    }
}
