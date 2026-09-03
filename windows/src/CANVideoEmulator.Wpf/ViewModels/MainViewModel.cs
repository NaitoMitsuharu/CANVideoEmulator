using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CANVideoEmulator.Core.Can;
using CANVideoEmulator.Core.Playback;
using CANVideoEmulator.Core.Video;
using CANVideoEmulator.Pcan;
using CANVideoEmulator.Scenarios;
using CANVideoEmulator.Wpf.Services;

namespace CANVideoEmulator.Wpf.ViewModels;

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
    private bool _refreshingPcanUi;
    private DateTime _nextPcanStatusUpdate;
    private bool _disposed;

    public MainViewModel(AppSettings settings, AppLog log, IVideoPlayer video)
    {
        _settings = settings;
        _log = log;
        _video = video;

        _pcan = new PcanManager();
        _pcan.Bitrate = settings.Bitrate;
        foreach (var channel in _pcan.Availability.Channels)
            if (channel.Channel.ToString() == settings.PcanChannel) _pcan.Select(channel);
        _transport = new NullCanTransport();
        _transport.Open();

        _scheduler = new CanScheduler(_clock, _transport);
        _scheduler.FrameSent += frame => RecentCan.Add(in frame);
        _scheduler.SendFailed += OnSendFailed;
        _scheduler.BusHealthChanged += status => Dispatch(() => HandleBusStatus(status));

        _library = LoadLibrary(settings.EffectiveScenarioDirectory);
        _session = CreateSession();

        _video.Failed += (_, message) => ReportWarning(message);

        _pcan.AvailabilityChanged += (_, _) => Dispatch(RefreshPcanUi);
        _pcan.SelectionChanged += (_, _) => Dispatch(RefreshPcanUi);
        _pcan.ConnectionLost += (_, message) => Dispatch(() =>
        {
            ReportWarning(message);
            ReleaseHardwareTransport();
            RefreshPcanUi();
        });

        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(() => Run(() => _session.Stop()));
        NextCommand = new RelayCommand(() => Run(() => _session.Next(autoPlay: _clock.IsPlaying)));
        PreviousCommand = new RelayCommand(() => Run(() => _session.Previous(autoPlay: _clock.IsPlaying)));
        SkipForwardCommand = new RelayCommand(() => Run(() => _session.SkipForward()));
        SkipBackwardCommand = new RelayCommand(() => Run(() => _session.SkipBackward()));
        ReloadScenariosCommand = new RelayCommand(ReloadScenarios);
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
    public RelayCommand ReloadScenariosCommand { get; }

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
            if (_refreshingPcanUi || !Set(ref _selectedPcanChannel, value) || value is null)
            {
                return;
            }

            if (UseHardware) ReleaseHardwareTransport();
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
                if (UseHardware) ReleaseHardwareTransport();
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

    private bool _useHardware = true;
    private uint? _lastReportedFault;
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

            // Keep the user's output preference separate from an open channel.
            // Startup, hot-plug and checking this box never begin playback.
            ReleaseHardwareTransport();

            RefreshPcanUi();
        }
    }

    private string _positionText = "00:00";
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }

    private string _durationText = "00:00";
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }

    private double _progress;
    private bool _isScrubbing;
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

    private string _recentCanCaption = "No frames yet";
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

    // -- HUD telemetry overlay (map, speed, IMU graphs) -----------------------

    private GnssTrack? _gnss;
    /// <summary>GNSS track for the trajectory map; null when the package has none.</summary>
    public GnssTrack? Gnss { get => _gnss; private set => Set(ref _gnss, value); }

    private ImuSeries? _imuAccel;
    public ImuSeries? ImuAccel { get => _imuAccel; private set => Set(ref _imuAccel, value); }

    private ImuSeries? _imuGyro;
    public ImuSeries? ImuGyro { get => _imuGyro; private set => Set(ref _imuGyro, value); }

    private ImuSeries? _imuMag;
    public ImuSeries? ImuMag { get => _imuMag; private set => Set(ref _imuMag, value); }

    private bool _hasMapOverlay;
    /// <summary>True when GNSS is available, so the map/speed overlay is shown.</summary>
    public bool HasMapOverlay { get => _hasMapOverlay; private set => Set(ref _hasMapOverlay, value); }

    private bool _hasImuOverlay;
    /// <summary>True when at least one IMU stream is available.</summary>
    public bool HasImuOverlay { get => _hasImuOverlay; private set => Set(ref _hasImuOverlay, value); }

    // Per-sensor presence: comma2k19 recordings vary (RAV4 has no magnetometer,
    // Civic does), so an absent stream's panel is hidden rather than shown empty.
    private bool _hasImuAccel;
    public bool HasImuAccel { get => _hasImuAccel; private set => Set(ref _hasImuAccel, value); }

    private bool _hasImuGyro;
    public bool HasImuGyro { get => _hasImuGyro; private set => Set(ref _hasImuGyro, value); }

    private bool _hasImuMag;
    public bool HasImuMag { get => _hasImuMag; private set => Set(ref _hasImuMag, value); }

    private double _telemetryTime;
    /// <summary>Scenario time (s) the overlay renders at; bound to the graph/map controls.</summary>
    public double TelemetryTime { get => _telemetryTime; private set => Set(ref _telemetryTime, value); }

    private string _speedText = "--";
    /// <summary>Interpolated GNSS speed in km/h, shown inside the map.</summary>
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }

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
        LoadTelemetryOverlay(package);

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

    private void LoadTelemetryOverlay(ScenarioPackage package)
    {
        // Reading the sidecar touches the disk and parses JSON; keep it off the
        // scenario-change path's critical work by tolerating a null result.
        var telemetry = package.Telemetry;
        Gnss = telemetry?.Gnss;
        ImuAccel = FindImu(telemetry, "ACCEL");
        ImuGyro = FindImu(telemetry, "GYRO");
        ImuMag = FindImu(telemetry, "MAG");
        HasImuAccel = ImuAccel is not null;
        HasImuGyro = ImuGyro is not null;
        HasImuMag = ImuMag is not null;
        HasMapOverlay = telemetry?.HasGnss == true;
        HasImuOverlay = telemetry?.HasImu == true;
        TelemetryTime = 0;
        SpeedText = "--";
    }

    private static ImuSeries? FindImu(ScenarioTelemetry? telemetry, string label) =>
        telemetry?.Imu.FirstOrDefault(s => s.Label == label);

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

    private bool EnableHardwareTransport()
    {
        _session.Pause();
        try
        {
            // A normal Reset only clears queues. Reopen after a bus fault to
            // reset the controller (other clients must release this channel).
            if (_pcan.Transport is { } previous && previous.RefreshStatus().Health != CanTransportHealth.Ok)
            {
                _log.Info("PCAN recovery: releasing and reinitialising the channel before PLAY.");
                _pcan.CloseTransport();
            }
            var transport = _pcan.OpenSelected();
            var status = transport.RefreshStatus();
            if (status.Health != CanTransportHealth.Ok)
                throw new PcanTransportException(status.Detail +
                    " Close other CAN applications, check the CAN network, then press PLAY again.");
            SwapTransport(transport);
            _lastReportedFault = null;
            StatusMessage = $"CAN output ready on {transport.Name} at {Bitrate / 1000} kbit/s.";
            _log.Info(StatusMessage);
            return true;
        }
        catch (PcanTransportException error)
        {
            ReportWarning(error.Message + " Turn off Transmit to PCAN-USB for video-only playback.");
            RefreshPcanUi();
            return false;
        }
    }

    private void HandleBusStatus(CanTransportStatus status)
    {
        if (!UseHardware || status.Health == CanTransportHealth.Ok) return;
        if (!status.IsUsable) _session.Pause();
        if (_lastReportedFault == status.RawStatus) return;
        _lastReportedFault = status.RawStatus;
        var hint = status.Health is CanTransportHealth.BusOff or CanTransportHealth.BusPassive
            or CanTransportHealth.BusHeavy or CanTransportHealth.BusLight
            ? " Check receiver power/normal mode, matching bit rate, CAN-H/CAN-L/GND and 120-ohm termination at both ends. After correcting the network, press PLAY to retry."
            : " Check the interface, then press PLAY to retry.";
        ReportWarning($"CAN {status.Health} (0x{status.RawStatus:X8}): {status.Detail}.{hint}");
        _log.Info($"CAN fault context: channel={_pcan.Selected?.DisplayName}; bitrate={Bitrate}; scenario={_session.Current?.ScenarioId}; bus={SelectedBus}; API accepted={_scheduler.Snapshot().FramesSent}");
    }

    private void ReleaseHardwareTransport()
    {
        Run(() =>
        {
            _session.Pause();
            _pcan.CloseTransport();
            var transport = new NullCanTransport();
            transport.Open();
            SwapTransport(transport);

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

    private void RefreshPcanUi()
    {
        var availability = _pcan.Availability;
        DriverMissing = availability.State == PcanApiState.NativeLibraryMissing;

        _refreshingPcanUi = true;
        var channelsChanged = !PcanChannels.SequenceEqual(availability.Channels);
        if (channelsChanged)
        {
            PcanChannels.Clear();
            foreach (var channel in availability.Channels) PcanChannels.Add(channel);
        }

        var selected = _pcan.Selected;
        if (channelsChanged || !Equals(_selectedPcanChannel, selected))
        {
            _selectedPcanChannel = selected;
            Raise(nameof(SelectedPcanChannel));
        }

        ChannelText = selected?.DisplayName ?? "-";
        _refreshingPcanUi = false;

        if (!UseHardware || _pcan.Transport is null)
        {
            PcanHealth = availability.State switch
            {
                PcanApiState.NativeLibraryMissing => CanTransportHealth.DriverMissing,
                PcanApiState.ApiError => CanTransportHealth.Error,
                _ when selected?.IsOccupied == true => CanTransportHealth.ChannelInUse,
                _ => CanTransportHealth.NotConnected,
            };
            PcanStatusText = availability.State switch
            {
                PcanApiState.NativeLibraryMissing => "Driver Missing",
                PcanApiState.NoDevices => "Not Connected",
                PcanApiState.ApiError => "Error",
                _ when selected?.IsOccupied == true => "Channel In Use",
                _ => "USB Ready",
            };
            PcanStatusDetail = availability.State == PcanApiState.Available
                ? $"{availability.Channels.Count} channel(s) available -- " + (UseHardware ? "press PLAY to transmit" : "CAN output is off")
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
            CanTransportHealth.BusPassive => "ERROR PASSIVE",
            CanTransportHealth.ChannelInUse => "Channel In Use",
            CanTransportHealth.DriverMissing => "Driver Missing",
            CanTransportHealth.ApiMissing => "PCAN-Basic Missing",
            CanTransportHealth.NotConnected => "Not Connected",
            _ => "Error",
        };
        PcanStatusDetail = status.Detail;
        HandleBusStatus(status);
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

        Run(() =>
        {
            if (!_clock.IsPlaying && UseHardware && !EnableHardwareTransport()) return;
            _session.TogglePlayPause();
        });
    }

    /// <summary>Called when the user releases the seek bar (requirement 10).</summary>
    /// <summary>
    /// The user grabbed the seek thumb. While true the tick stops pushing the
    /// clock position back onto the slider, so the thumb follows the drag instead
    /// of snapping back 15 times a second.
    /// </summary>
    public void BeginScrub() => _isScrubbing = true;

    public void SeekToProgress(double fraction)
    {
        _isScrubbing = false;
        if (_session.Current is null)
        {
            return;
        }

        // Seek to the released position and play from there. Seek keeps a running
        // scenario running; if it was paused, start it so operating the bar always
        // resumes playback from the new point.
        Run(() =>
        {
            _session.Seek(_session.Duration * Math.Clamp(fraction, 0, 1));
            if (!_clock.IsPlaying)
            {
                _session.Play();
            }
        });
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
        if (!_isScrubbing)
        {
            _progress = _clock.Progress;
            Raise(nameof(Progress));
        }

        // Drive the HUD overlay (map/speed/IMU graphs) from the same clock.
        TelemetryTime = position.TotalSeconds;
        if (_gnss is { } gnss)
        {
            var kmh = gnss.SpeedAt(position.TotalSeconds);
            SpeedText = double.IsNaN(kmh) ? "--" : kmh.ToString("F0");
        }

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

        if (UseHardware && DateTime.UtcNow >= _nextPcanStatusUpdate)
        {
            _nextPcanStatusUpdate = DateTime.UtcNow.AddMilliseconds(250);
            RefreshPcanUi();
        }

        RecentCan.Flush();
        HasRecentCan = RecentCan.Rows.Count > 0;
        RecentCanCaption = RecentCan.Observed == 0
            ? $"newest {RecentCan.DisplayRows} frames"
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

