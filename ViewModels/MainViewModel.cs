using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Media;
using wetwiLAG.Infrastructure;
using wetwiLAG.Models;
using wetwiLAG.Services;

namespace wetwiLAG.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly string[] WidgetIds = ["connection", "latency", "traffic", "fortnite", "adapter"];
    private static readonly string[] DefaultWidgetOrder = ["connection", "latency", "traffic", "fortnite", "adapter"];
    private static readonly (string Id, string Title, string Icon)[] WidgetDefinitions =
    [
        ("connection", "Connection", "◉"),
        ("latency", "Latency", "⌁"),
        ("traffic", "Network traffic", "↕"),
        ("fortnite", "Fortnite", "◈"),
        ("adapter", "Active adapter", "▤")
    ];

    private readonly AppDataStore _store;
    private readonly SafeOptimizerService _optimizer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<double> _recentLatencies = new();
    private readonly Queue<bool> _recentReplies = new();
    private AppSettings _settings = new();
    private UserProfile? _selectedProfile;
    private LayoutPreset? _selectedPreset;
    private NetworkAdapterInfo? _selectedAdapter;
    private string _currentSection = "Dashboard";
    private string _probeHost = "1.1.1.1";
    private string _probeHostInput = "1.1.1.1";
    private string _dnsLookupHost = "epicgames.com";
    private string _dnsPrimaryInput = "";
    private string _dnsSecondaryInput = "";
    private string _theme = "Dark";
    private string _accentColor = "#FF63D6C5";
    private string _optimizerMode = "SAFE";
    private double _scale = 1.0;
    private bool _sidebarCollapsed;
    private bool _sidebarVisible = true;
    private bool _sidebarOnRight;
    private bool _isInitializing = true;
    private bool _isFortniteRunning;
    private bool _isAdministrator;
    private string _statusMessage = "Ready";
    private string _errorMessage = "";
    private string _connectionStatus = "Collecting live measurements…";
    private string _fortniteConnectionStatus = "No game server endpoint is configured.";
    private string _dnsStatus = "Select a network adapter to inspect its DNS settings.";
    private string _powerPlanName = "Reading current plan…";
    private string _selectiveSuspendStatus = "Not checked";
    private string _dnsResolutionSummary = "";
    private string _pingSummary = "";
    private string _routeSummary = "";
    private string _optimizerResult = "";
    private string _historySummary = "No saved measurements yet.";
    private double _downloadBytesPerSecond;
    private double _uploadBytesPerSecond;
    private bool _hasTrafficMeasurement;
    private bool _hasCounterBaseline;
    private string? _trafficAdapterId;
    private double? _latencyMs;
    private double? _jitterMs;
    private double? _packetLossPercent;
    private PointCollection _historyChartPoints = [];
    private DnsResolutionResult? _dnsResolution;
    private OptimizerApplyResult? _lastOptimizerResult;
    private PowerPlanInfo? _powerPlan;
    private DnsConfiguration? _dnsConfiguration;
    private OptimizerBackup? _latestDnsBackup;
    private OptimizerBackup? _latestPowerPlanBackup;
    private OptimizerBackup? _latestSelectiveSuspendBackup;
    private string? _selectedAdapterId;
    private Task? _monitorTask;

    private MainViewModel(AppDataStore store)
    {
        _store = store;
        _optimizer = new SafeOptimizerService(store);
        VisibleWidgets = [];
        HiddenWidgets = [];
        Adapters = [];
        Profiles = [];
        History = [];
        RouteHops = [];
        Backups = [];
        Operations = [];
        LayoutPresets = [];
        AccentOptions = ["#FF63D6C5", "#FF7AA2F7", "#FFF4B860", "#FFF07886", "#FFB58AF2"];

        NavigateCommand = new RelayCommand<string>(Navigate);
        ToggleSidebarCommand = new RelayCommand(() => SidebarVisible = !SidebarVisible);
        ToggleSidebarCollapsedCommand = new RelayCommand(() => SidebarCollapsed = !SidebarCollapsed);
        RefreshAdaptersCommand = CreateAsyncCommand(RefreshAdaptersAsync);
        RunPingTestCommand = CreateAsyncCommand(RunPingTestAsync, () => IsProbeHostInputValid);
        ResolveDnsCommand = CreateAsyncCommand(ResolveDnsAsync);
        RunTraceCommand = CreateAsyncCommand(RunTraceAsync, () => IsProbeHostInputValid);
        CancelTraceCommand = new RelayCommand(() => RunTraceCommand.Cancel(), () => RunTraceCommand.IsExecuting);
        RunTraceCommand.CanExecuteChanged += (_, _) => CancelTraceCommand.NotifyCanExecuteChanged();
        RefreshFortniteCommand = new RelayCommand(RefreshFortnite);
        ApplyDnsCommand = CreateAsyncCommand(
            ApplyDnsAsync,
            () => IsManualMode && SelectedAdapter is not null &&
                  _dnsConfiguration is { Error: null, Mode: "DHCP" or "Static" } &&
                  (_dnsConfiguration.Mode != "Static" || _dnsConfiguration.Servers.Count > 0));
        ApplyPowerPlanCommand = CreateAsyncCommand(ApplyPowerPlanAsync, () => IsManualMode && CanApplyPowerPlan);
        ApplySelectiveSuspendCommand = CreateAsyncCommand(
            ApplySelectiveSuspendAsync,
            () => IsManualMode && SelectedAdapter is not null &&
                  SelectiveSuspendStatus.Equals("Enabled", StringComparison.OrdinalIgnoreCase));
        RefreshOptimizerCommand = CreateAsyncCommand(RefreshOptimizerAsync);
        RestoreBackupCommand = CreateAsyncCommand<OptimizerBackup>(
            RestoreBackupAsync,
            backup => IsRestoreMode && backup is not null);
        SaveProfileCommand = CreateAsyncCommand(SaveProfileAsync);
        EditProfileCommand = CreateAsyncCommand(EditProfileAsync, () => SelectedProfile is not null);
        DeleteProfileCommand = CreateAsyncCommand(DeleteProfileAsync, () => SelectedProfile is not null);
        CreatePresetCommand = new RelayCommand(CreatePreset);
        UpdatePresetCommand = new RelayCommand(UpdatePreset, () => SelectedPreset is not null);
        RenamePresetCommand = new RelayCommand(RenamePreset, () => SelectedPreset is not null);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => SelectedPreset is not null);
        ApplyPresetCommand = new RelayCommand(ApplyPreset, () => SelectedPreset is not null);
        ResetLayoutCommand = new RelayCommand(ResetLayout);
        HideWidgetCommand = new RelayCommand<string>(HideWidget);
        RestoreWidgetCommand = new RelayCommand<string>(RestoreWidget);
        ResizeWidgetCommand = new RelayCommand<string>(ResizeWidget);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
    }

    public ObservableCollection<DashboardWidget> VisibleWidgets { get; }
    public ObservableCollection<string> HiddenWidgets { get; }
    public ObservableCollection<NetworkAdapterInfo> Adapters { get; }
    public ObservableCollection<UserProfile> Profiles { get; }
    public ObservableCollection<MeasurementRecord> History { get; }
    public ObservableCollection<RouteHop> RouteHops { get; }
    public ObservableCollection<OptimizerBackup> Backups { get; }
    public ObservableCollection<OptimizerOperation> Operations { get; }
    public ObservableCollection<LayoutPreset> LayoutPresets { get; }
    public OptimizerBackup? LatestDnsBackup
    {
        get => _latestDnsBackup;
        private set => SetProperty(ref _latestDnsBackup, value);
    }

    public OptimizerBackup? LatestPowerPlanBackup
    {
        get => _latestPowerPlanBackup;
        private set => SetProperty(ref _latestPowerPlanBackup, value);
    }

    public OptimizerBackup? LatestSelectiveSuspendBackup
    {
        get => _latestSelectiveSuspendBackup;
        private set => SetProperty(ref _latestSelectiveSuspendBackup, value);
    }

    public string DnsOperationStatus => LatestOperationStatus("Dns");
    public string PowerPlanOperationStatus => LatestOperationStatus("PowerPlan");
    public string SelectiveSuspendOperationStatus => LatestOperationStatus("SelectiveSuspend");
    public IReadOnlyList<string> AccentOptions { get; }
    public IReadOnlyList<string> OptimizerModes { get; } = ["SAFE", "MANUAL", "RESTORE"];
    public IReadOnlyList<string> Themes { get; } = ["Dark", "Light"];

    public RelayCommand<string> NavigateCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand ToggleSidebarCollapsedCommand { get; }
    public AsyncRelayCommand RefreshAdaptersCommand { get; }
    public AsyncRelayCommand RunPingTestCommand { get; }
    public AsyncRelayCommand ResolveDnsCommand { get; }
    public AsyncRelayCommand RunTraceCommand { get; }
    public RelayCommand CancelTraceCommand { get; }
    public RelayCommand RefreshFortniteCommand { get; }
    public AsyncRelayCommand ApplyDnsCommand { get; }
    public AsyncRelayCommand ApplyPowerPlanCommand { get; }
    public AsyncRelayCommand ApplySelectiveSuspendCommand { get; }
    public AsyncRelayCommand RefreshOptimizerCommand { get; }
    public AsyncRelayCommand<OptimizerBackup> RestoreBackupCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand EditProfileCommand { get; }
    public AsyncRelayCommand DeleteProfileCommand { get; }
    public RelayCommand CreatePresetCommand { get; }
    public RelayCommand UpdatePresetCommand { get; }
    public RelayCommand RenamePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public RelayCommand ResetLayoutCommand { get; }
    public RelayCommand<string> HideWidgetCommand { get; }
    public RelayCommand<string> RestoreWidgetCommand { get; }
    public RelayCommand<string> ResizeWidgetCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    public string CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (SetProperty(ref _currentSection, value))
            {
                OnPropertyChanged(nameof(SectionSubtitle));
            }
        }
    }

    public string SectionSubtitle => CurrentSection switch
    {
        "Dashboard" => "Live measurements from your network interfaces",
        "Fortnite" => "Process detection and transparent connection checks",
        "Network" => "Adapter details, DNS resolution and measured latency",
        "Routes" => "ICMP traceroute with real hop responses",
        "Optimizer" => "Conservative changes, verified backups and rollback",
        "Profiles" => "Saved application test targets and adapter preferences",
        "History" => "Persistent measurements collected by real diagnostics",
        "Settings" => "Appearance, dashboard widgets and layout presets",
        _ => ""
    };

    public string ProbeHost
    {
        get => _probeHost;
        set
        {
            var candidate = value?.Trim() ?? "";
            if (!NetworkDiagnostics.IsValidHost(candidate))
            {
                ErrorMessage = "Enter a valid IP address or DNS host name for the probe target.";
                OnPropertyChanged(nameof(IsProbeHostInputValid));
                RunPingTestCommand.NotifyCanExecuteChanged();
                RunTraceCommand.NotifyCanExecuteChanged();
                return;
            }

            if (SetProperty(ref _probeHost, candidate))
            {
                SetProperty(ref _probeHostInput, candidate, nameof(ProbeHostInput));
                OnPropertyChanged(nameof(ProbeTargetLabel));
                OnPropertyChanged(nameof(ProbeStatus));
                if (!_isInitializing)
                {
                    _settings.ProbeHost = candidate;
                    _ = SaveSettingsSafelyAsync();
                }
            }

            if (IsProbeHostInputValid &&
                ErrorMessage == "Enter a valid IP address or DNS host name for the probe target.")
            {
                ErrorMessage = "";
            }

            OnPropertyChanged(nameof(IsProbeHostInputValid));
            RunPingTestCommand.NotifyCanExecuteChanged();
            RunTraceCommand.NotifyCanExecuteChanged();
        }
    }

    public string ProbeHostInput
    {
        get => _probeHostInput;
        set
        {
            var candidate = value?.Trim() ?? "";
            if (!SetProperty(ref _probeHostInput, candidate))
            {
                return;
            }

            if (!NetworkDiagnostics.IsValidHost(candidate))
            {
                ErrorMessage = "Enter a valid IP address or DNS host name for the probe target.";
            }
            else
            {
                ProbeHost = candidate;
            }

            OnPropertyChanged(nameof(IsProbeHostInputValid));
            RunPingTestCommand.NotifyCanExecuteChanged();
            RunTraceCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsProbeHostInputValid => NetworkDiagnostics.IsValidHost(ProbeHostInput);

    public string ProbeTargetLabel => $"Probe target: {ProbeHost}";
    public string ProbeStatus => _latencyMs is null ? "Awaiting a successful ICMP response" : $"Measured against {ProbeHost}";

    public string DnsLookupHost
    {
        get => _dnsLookupHost;
        set => SetProperty(ref _dnsLookupHost, value);
    }

    public string DnsPrimaryInput
    {
        get => _dnsPrimaryInput;
        set => SetProperty(ref _dnsPrimaryInput, value);
    }

    public string DnsSecondaryInput
    {
        get => _dnsSecondaryInput;
        set => SetProperty(ref _dnsSecondaryInput, value);
    }

    public string Theme
    {
        get => _theme;
        set
        {
            var selected = value is "Light" ? "Light" : "Dark";
            if (SetProperty(ref _theme, selected))
            {
                ApplyAppearance();
                if (!_isInitializing)
                {
                    _settings.Theme = selected;
                    _ = SaveSettingsSafelyAsync();
                }
            }
        }
    }

    public string AccentColor
    {
        get => _accentColor;
        set
        {
            if (!AccentOptions.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            if (SetProperty(ref _accentColor, value))
            {
                ApplyAppearance();
                if (!_isInitializing)
                {
                    _settings.AccentColor = value;
                    _ = SaveSettingsSafelyAsync();
                }
            }
        }
    }

    public double Scale
    {
        get => _scale;
        set
        {
            var scale = Math.Round(Math.Clamp(value, 0.8, 1.2), 2);
            if (SetProperty(ref _scale, scale))
            {
                OnPropertyChanged(nameof(ScaleLabel));
                if (!_isInitializing)
                {
                    _settings.Scale = scale;
                    _ = SaveSettingsSafelyAsync();
                }
            }
        }
    }

    public string ScaleLabel => $"{Scale:P0}";

    public bool SidebarCollapsed
    {
        get => _sidebarCollapsed;
        set
        {
            if (SetProperty(ref _sidebarCollapsed, value) && !_isInitializing)
            {
                _settings.SidebarCollapsed = value;
                OnPropertyChanged(nameof(SidebarColumnWidth));
                OnPropertyChanged(nameof(MainColumnZeroWidth));
                OnPropertyChanged(nameof(MainColumnOneWidth));
                _ = SaveSettingsSafelyAsync();
            }
        }
    }

    public bool SidebarVisible
    {
        get => _sidebarVisible;
        set
        {
            if (SetProperty(ref _sidebarVisible, value) && !_isInitializing)
            {
                _settings.SidebarVisible = value;
                OnPropertyChanged(nameof(SidebarColumnWidth));
                OnPropertyChanged(nameof(MainColumnZeroWidth));
                OnPropertyChanged(nameof(MainColumnOneWidth));
                _ = SaveSettingsSafelyAsync();
            }
        }
    }

    public bool SidebarOnRight
    {
        get => _sidebarOnRight;
        set
        {
            if (SetProperty(ref _sidebarOnRight, value) && !_isInitializing)
            {
                _settings.SidebarOnRight = value;
                OnPropertyChanged(nameof(SidebarColumnIndex));
                OnPropertyChanged(nameof(ContentColumnIndex));
                OnPropertyChanged(nameof(MainColumnZeroWidth));
                OnPropertyChanged(nameof(MainColumnOneWidth));
                _ = SaveSettingsSafelyAsync();
            }
        }
    }

    public double SidebarColumnWidth => !SidebarVisible ? 0 : SidebarCollapsed ? 72 : 238;
    public GridLength MainColumnZeroWidth => SidebarOnRight
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(SidebarColumnWidth);
    public GridLength MainColumnOneWidth => SidebarOnRight
        ? new GridLength(SidebarColumnWidth)
        : new GridLength(1, GridUnitType.Star);
    public int SidebarColumnIndex => SidebarOnRight ? 1 : 0;
    public int ContentColumnIndex => SidebarOnRight ? 0 : 1;

    public string OptimizerMode
    {
        get => _optimizerMode;
        set
        {
            if (!OptimizerModes.Contains(value, StringComparer.OrdinalIgnoreCase) || !SetProperty(ref _optimizerMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsManualMode));
            OnPropertyChanged(nameof(IsRestoreMode));
            ApplyDnsCommand.NotifyCanExecuteChanged();
            ApplyPowerPlanCommand.NotifyCanExecuteChanged();
            ApplySelectiveSuspendCommand.NotifyCanExecuteChanged();
            RestoreBackupCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsManualMode => OptimizerMode == "MANUAL";
    public bool IsRestoreMode => OptimizerMode == "RESTORE";

    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (SetProperty(ref _selectedAdapter, value))
            {
                _selectedAdapterId = value?.Id;
                OnPropertyChanged(nameof(SelectedAdapterSummary));
                ApplyDnsCommand.NotifyCanExecuteChanged();
                ApplySelectiveSuspendCommand.NotifyCanExecuteChanged();
                if (!_isInitializing && value is not null)
                {
                    _settings.SelectedAdapterId = value.Id;
                    _ = SaveSettingsSafelyAsync();
                    _ = RefreshOptimizerSafelyAsync();
                }
            }
        }
    }

    public UserProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value) || _isInitializing || value is null)
            {
                EditProfileCommand.NotifyCanExecuteChanged();
                DeleteProfileCommand.NotifyCanExecuteChanged();
                return;
            }

            _settings.SelectedProfileId = value.Id.ToString();
            ProbeHost = value.ProbeHost;
            SelectedAdapter = value.PreferredAdapterId is null
                ? NetworkDiagnostics.FindDefaultAdapter() ?? Adapters.FirstOrDefault(adapter => adapter.Status == "Up")
                : Adapters.FirstOrDefault(adapter => adapter.Id == value.PreferredAdapterId);

            EditProfileCommand.NotifyCanExecuteChanged();
            DeleteProfileCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Profile “{value.Name}” selected. System network settings were not changed.";
            _ = SaveSettingsSafelyAsync();
        }
    }

    public LayoutPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                UpdatePresetCommand.NotifyCanExecuteChanged();
                RenamePresetCommand.NotifyCanExecuteChanged();
                DeletePresetCommand.NotifyCanExecuteChanged();
                ApplyPresetCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedAdapterSummary =>
        SelectedAdapter is null ? "No active adapter selected" : $"{SelectedAdapter.Name} · {SelectedAdapter.Status}";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                UpdateWidgets();
            }
        }
    }

    public string FortniteConnectionStatus
    {
        get => _fortniteConnectionStatus;
        private set
        {
            if (SetProperty(ref _fortniteConnectionStatus, value))
            {
                UpdateWidgets();
            }
        }
    }

    public string DnsStatus
    {
        get => _dnsStatus;
        private set => SetProperty(ref _dnsStatus, value);
    }

    public string PowerPlanName
    {
        get => _powerPlanName;
        private set
        {
            if (SetProperty(ref _powerPlanName, value))
            {
                OnPropertyChanged(nameof(CanApplyPowerPlan));
                ApplyPowerPlanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectiveSuspendStatus
    {
        get => _selectiveSuspendStatus;
        private set
        {
            if (SetProperty(ref _selectiveSuspendStatus, value))
            {
                ApplySelectiveSuspendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DnsResolutionSummary
    {
        get => _dnsResolutionSummary;
        private set => SetProperty(ref _dnsResolutionSummary, value);
    }

    public string PingSummary
    {
        get => _pingSummary;
        private set => SetProperty(ref _pingSummary, value);
    }

    public string RouteSummary
    {
        get => _routeSummary;
        private set => SetProperty(ref _routeSummary, value);
    }

    public string OptimizerResult
    {
        get => _optimizerResult;
        private set => SetProperty(ref _optimizerResult, value);
    }

    public string HistorySummary
    {
        get => _historySummary;
        private set => SetProperty(ref _historySummary, value);
    }

    public double DownloadBytesPerSecond
    {
        get => _downloadBytesPerSecond;
        private set
        {
            if (SetProperty(ref _downloadBytesPerSecond, value))
            {
                OnPropertyChanged(nameof(DownloadRate));
                UpdateWidgets();
            }
        }
    }

    public double UploadBytesPerSecond
    {
        get => _uploadBytesPerSecond;
        private set
        {
            if (SetProperty(ref _uploadBytesPerSecond, value))
            {
                OnPropertyChanged(nameof(UploadRate));
                UpdateWidgets();
            }
        }
    }

    public bool HasTrafficMeasurement
    {
        get => _hasTrafficMeasurement;
        private set
        {
            if (SetProperty(ref _hasTrafficMeasurement, value))
            {
                OnPropertyChanged(nameof(DownloadRate));
                OnPropertyChanged(nameof(UploadRate));
                UpdateWidgets();
            }
        }
    }

    public string DownloadRate => HasTrafficMeasurement
        ? NetworkDiagnostics.FormatRate(DownloadBytesPerSecond)
        : "Unavailable";
    public string UploadRate => HasTrafficMeasurement
        ? NetworkDiagnostics.FormatRate(UploadBytesPerSecond)
        : "Unavailable";

    public double? LatencyMs
    {
        get => _latencyMs;
        private set
        {
            if (SetProperty(ref _latencyMs, value))
            {
                OnPropertyChanged(nameof(LatencyLabel));
                OnPropertyChanged(nameof(ProbeStatus));
                UpdateWidgets();
            }
        }
    }

    public double? JitterMs
    {
        get => _jitterMs;
        private set
        {
            if (SetProperty(ref _jitterMs, value))
            {
                OnPropertyChanged(nameof(JitterLabel));
            }
        }
    }

    public double? PacketLossPercent
    {
        get => _packetLossPercent;
        private set
        {
            if (SetProperty(ref _packetLossPercent, value))
            {
                OnPropertyChanged(nameof(PacketLossLabel));
            }
        }
    }

    public string LatencyLabel => LatencyMs is null ? "Unavailable" : $"{LatencyMs:0.##} ms";
    public string JitterLabel => JitterMs is null ? "Unavailable" : $"{JitterMs:0.##} ms";
    public string PacketLossLabel => PacketLossPercent is null ? "Unavailable" : $"{PacketLossPercent:0.#}%";
    public string FortniteStatus => IsFortniteRunning ? "Game process detected" : "Game process not detected";
    public bool IsFortniteRunning
    {
        get => _isFortniteRunning;
        private set
        {
            if (SetProperty(ref _isFortniteRunning, value))
            {
                OnPropertyChanged(nameof(FortniteStatus));
                UpdateWidgets();
            }
        }
    }

    public bool IsAdministrator
    {
        get => _isAdministrator;
        private set
        {
            if (SetProperty(ref _isAdministrator, value))
            {
                OnPropertyChanged(nameof(AdministratorStatus));
            }
        }
    }

    public string AdministratorStatus => IsAdministrator
        ? "Administrator session"
        : "Standard session · Windows may require reopening as administrator for system changes. wetwiLAG never elevates automatically.";

    public bool CanApplyPowerPlan => _powerPlan is { Error: null, HighPerformanceAvailable: true } &&
                                     !string.Equals(
                                         _powerPlan.ActiveId,
                                         "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                                         StringComparison.OrdinalIgnoreCase);

    public PointCollection HistoryChartPoints
    {
        get => _historyChartPoints;
        private set => SetProperty(ref _historyChartPoints, value);
    }

    public bool HasDnsResolution => _dnsResolution is not null;
    public bool HasOptimizerComparison => _lastOptimizerResult?.Before is not null;

    public static async Task<MainViewModel> CreateAsync()
    {
        var store = new AppDataStore();
        var viewModel = new MainViewModel(store);
        await viewModel.InitializeAsync();
        return viewModel;
    }

    public async Task InitializeAsync()
    {
        _settings = await _store.LoadSettingsAsync();
        NormalizeSettings();
        _theme = _settings.Theme;
        _accentColor = _settings.AccentColor;
        _scale = _settings.Scale;
        _sidebarCollapsed = _settings.SidebarCollapsed;
        _sidebarVisible = _settings.SidebarVisible;
        _sidebarOnRight = _settings.SidebarOnRight;
        _probeHost = NetworkDiagnostics.IsValidHost(_settings.ProbeHost)
            ? _settings.ProbeHost
            : "1.1.1.1";
        _probeHostInput = _probeHost;
        ApplyAppearance();

        foreach (var profile in await _store.LoadProfilesAsync())
        {
            Profiles.Add(profile);
        }

        foreach (var measurement in (await _store.LoadHistoryAsync()).OrderByDescending(item => item.Timestamp))
        {
            History.Add(measurement);
        }

        foreach (var operation in (await _store.LoadOperationsAsync()).OrderByDescending(item => item.Timestamp))
        {
            Operations.Add(operation);
        }

        foreach (var backup in await _store.LoadBackupsAsync())
        {
            Backups.Add(backup);
        }
        LatestDnsBackup = Backups.FirstOrDefault(backup => backup.SettingKind == "Dns");
        LatestPowerPlanBackup = Backups.FirstOrDefault(backup => backup.SettingKind == "PowerPlan");
        LatestSelectiveSuspendBackup = Backups.FirstOrDefault(backup => backup.SettingKind == "SelectiveSuspend");

        foreach (var preset in _settings.LayoutPresets)
        {
            LayoutPresets.Add(preset);
        }

        RebuildWidgets();
        await RefreshAdaptersAsync(_lifetime.Token);
        var savedProfile = Profiles.FirstOrDefault(profile => profile.Id.ToString() == _settings.SelectedProfileId);
        SelectedAdapter = Adapters.FirstOrDefault(adapter =>
                              adapter.Id == (savedProfile?.PreferredAdapterId ?? _settings.SelectedAdapterId))
                          ?? NetworkDiagnostics.FindDefaultAdapter()
                          ?? Adapters.FirstOrDefault(adapter => adapter.Status == "Up");
        _selectedProfile = savedProfile;
        if (savedProfile is not null && NetworkDiagnostics.IsValidHost(savedProfile.ProbeHost))
        {
            _probeHost = savedProfile.ProbeHost;
        }
        else
        {
            _settings.SelectedAdapterId = SelectedAdapter?.Id;
        }
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(ProbeHost));
        OnPropertyChanged(nameof(ProbeHostInput));

        foreach (var warning in new[] { _store.LastLoadWarning }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            ErrorMessage = warning!;
        }

        IsAdministrator = _optimizer.IsAdministrator;
        _isInitializing = false;
        UpdateWidgets();
        UpdateHistoryChart();
        RefreshFortnite();
        await RefreshOptimizerAsync(_lifetime.Token);
        _monitorTask = Task.Run(() => MonitorAsync(_lifetime.Token));
        await SaveSettingsSafelyAsync();
    }

    public void ReorderWidgets(string sourceId, string targetId)
    {
        var sourceIndex = VisibleWidgets.ToList().FindIndex(widget => widget.Id == sourceId);
        var targetIndex = VisibleWidgets.ToList().FindIndex(widget => widget.Id == targetId);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        VisibleWidgets.Move(sourceIndex, targetIndex);
        SaveWidgetLayout();
    }

    public void HideWidget(string? id)
    {
        var widget = VisibleWidgets.FirstOrDefault(candidate => candidate.Id == id);
        if (widget is null)
        {
            return;
        }

        VisibleWidgets.Remove(widget);
        if (!HiddenWidgets.Contains(widget.Id))
        {
            HiddenWidgets.Add(widget.Id);
        }

        SaveWidgetLayout();
    }

    public void RestoreWidget(string? id)
    {
        if (id is null || !WidgetIds.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        HiddenWidgets.Remove(id);
        if (VisibleWidgets.All(widget => widget.Id != id))
        {
            var definition = WidgetDefinitions.First(widget => widget.Id == id);
            var widget = CreateWidget(definition.Id, definition.Title, definition.Icon);
            VisibleWidgets.Add(widget);
            var savedOrder = _settings.DashboardOrder.IndexOf(id);
            if (savedOrder >= 0)
            {
                var targetIndex = Math.Min(savedOrder, VisibleWidgets.Count - 1);
                VisibleWidgets.Move(VisibleWidgets.Count - 1, targetIndex);
            }

            UpdateWidgets();
        }

        SaveWidgetLayout();
    }

    public void ResizeWidget(string? id)
    {
        var widget = VisibleWidgets.FirstOrDefault(candidate => candidate.Id == id);
        if (widget is null)
        {
            return;
        }

        var nextWidth = widget.Width < 400 ? 520 : 265;
        widget.Width = nextWidth;
        _settings.WidgetWidths[widget.Id] = nextWidth;
        _ = SaveSettingsSafelyAsync();
    }

    private void NormalizeSettings()
    {
        _settings.Theme = _settings.Theme == "Light" ? "Light" : "Dark";
        if (!AccentOptions.Contains(_settings.AccentColor, StringComparer.OrdinalIgnoreCase))
        {
            _settings.AccentColor = "#FF63D6C5";
        }

        _settings.Scale = Math.Clamp(_settings.Scale, 0.8, 1.2);
        _settings.DashboardOrder ??= new ObservableCollection<string>();
        _settings.HiddenWidgets ??= new ObservableCollection<string>();
        _settings.WidgetWidths ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        _settings.LayoutPresets ??= new ObservableCollection<LayoutPreset>();
        var validOrder = _settings.DashboardOrder
            .Where(id => WidgetIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var id in DefaultWidgetOrder)
        {
            if (!validOrder.Contains(id, StringComparer.OrdinalIgnoreCase) &&
                !_settings.HiddenWidgets.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                validOrder.Add(id);
            }
        }

        _settings.DashboardOrder = new ObservableCollection<string>(validOrder);
        _settings.HiddenWidgets = new ObservableCollection<string>(
            _settings.HiddenWidgets
                .Where(id => WidgetIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var id in _settings.HiddenWidgets)
        {
            _settings.DashboardOrder.Remove(id);
        }
    }

    private void ApplyAppearance()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var light = Theme == "Light";
        resources["WindowBackground"] = Brush(light ? "#FFF4F6F8" : "#FF101317");
        resources["SidebarBackground"] = Brush(light ? "#FFEAEDF0" : "#FF15191E");
        resources["PanelBackground"] = Brush(light ? "#FFFFFFFF" : "#FF1A1F25");
        resources["CardBackground"] = Brush(light ? "#FFF9FAFB" : "#FF20262D");
        resources["BorderBrush"] = Brush(light ? "#FFE1E5E9" : "#FF2D353E");
        resources["PrimaryText"] = Brush(light ? "#FF20262D" : "#FFF1F4F7");
        resources["MutedText"] = Brush(light ? "#FF707983" : "#FF9AA5B1");
        var accent = (Color)ColorConverter.ConvertFromString(AccentColor);
        resources["AccentBrush"] = Brush(accent);
        accent.A = light ? (byte)0x25 : (byte)0x2F;
        resources["AccentSoftBrush"] = Brush(accent);
        resources["DangerBrush"] = Brush("#FFF07886");
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private static SolidColorBrush Brush(Color color) => new(color);

    private void Navigate(string? section)
    {
        if (section is null || !new[]
            {
                "Dashboard", "Fortnite", "Network", "Routes", "Optimizer", "Profiles", "History", "Settings"
            }.Contains(section, StringComparer.Ordinal))
        {
            return;
        }

        CurrentSection = section;
        StatusMessage = $"Opened {section}.";
    }

    private async Task RefreshAdaptersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = await Task.Run(NetworkDiagnostics.GetAdapters, cancellationToken);
        Application.Current.Dispatcher.Invoke(() =>
        {
            var previousAdapterId = SelectedAdapter?.Id;
            Adapters.Clear();
            foreach (var adapter in adapters)
            {
                Adapters.Add(adapter);
            }

            var defaultAdapterId = NetworkDiagnostics.FindDefaultAdapter()?.Id;
            SelectedAdapter = Adapters.FirstOrDefault(adapter => adapter.Id == previousAdapterId)
                              ?? Adapters.FirstOrDefault(adapter => adapter.Id == defaultAdapterId)
                              ?? Adapters.FirstOrDefault(adapter => adapter.Status == "Up")
                              ?? Adapters.FirstOrDefault();
        });
        StatusMessage = $"Found {adapters.Count} network adapter(s).";
        await RefreshOptimizerAsync(cancellationToken);
    }

    private async Task RunPingTestAsync(CancellationToken cancellationToken)
    {
        if (!NetworkDiagnostics.IsValidHost(ProbeHost))
        {
            throw new ArgumentException("Enter a valid probe host.");
        }

        StatusMessage = $"Measuring {ProbeHost} with 10 ICMP samples…";
        var result = await NetworkDiagnostics.MeasureAsync(ProbeHost, 10, cancellationToken: cancellationToken);
        await SaveMeasurementAsync(result, "Manual ping");
        PingSummary = DescribeDiagnostic(result);
        LatencyMs = result.AverageLatencyMs;
        JitterMs = result.JitterMs;
        PacketLossPercent = result.PacketLossPercent;
        StatusMessage = result.Received > 0
            ? $"Received {result.Received} of {result.Sent} replies from {result.Target}."
            : result.Error ?? "The diagnostic received no replies.";
    }

    private async Task ResolveDnsAsync(CancellationToken cancellationToken)
    {
        if (!NetworkDiagnostics.IsValidHost(DnsLookupHost))
        {
            throw new ArgumentException("Enter a valid host name for DNS resolution.");
        }

        StatusMessage = $"Resolving {DnsLookupHost}…";
        _dnsResolution = await NetworkDiagnostics.ResolveDnsAsync(DnsLookupHost, cancellationToken);
        OnPropertyChanged(nameof(HasDnsResolution));
        DnsResolutionSummary = _dnsResolution.Error is null
            ? $"{string.Join(Environment.NewLine, _dnsResolution.Addresses)}{Environment.NewLine}Resolved in {_dnsResolution.ElapsedMilliseconds:0.##} ms"
            : $"Resolution failed: {_dnsResolution.Error}";
        StatusMessage = _dnsResolution.Error is null ? "DNS resolution completed." : "DNS resolution failed; see the result.";
    }

    private async Task RunTraceAsync(CancellationToken cancellationToken)
    {
        RouteHops.Clear();
        RouteSummary = $"Tracing {ProbeHost}; each hop is an actual ICMP response or timeout.";
        StatusMessage = $"Tracing route to {ProbeHost}…";
        try
        {
            var hops = await NetworkDiagnostics.TraceRouteAsync(ProbeHost, cancellationToken: cancellationToken);
            foreach (var hop in hops)
            {
                RouteHops.Add(hop);
            }

            RouteSummary = hops.Count == 0
                ? "No route hops were returned."
                : $"Trace finished with {hops.Count} hop(s). A * means that hop did not reply before its timeout.";
            StatusMessage = "Route trace completed.";
        }
        catch (OperationCanceledException)
        {
            RouteSummary = $"Trace cancelled after {RouteHops.Count} hop(s). Returned hops were retained.";
            StatusMessage = "Route trace cancelled.";
        }
    }

    private void RefreshFortnite()
    {
        try
        {
            IsFortniteRunning = NetworkDiagnostics.IsFortniteRunning();
            FortniteConnectionStatus = IsFortniteRunning
                ? $"The game process is running. Windows does not expose a Fortnite server endpoint here; the separate probe targets {ProbeHost}."
                : "No Fortnite game process was detected. No game-server connection is claimed.";
            StatusMessage = FortniteStatus;
        }
        catch (Exception exception)
        {
            _store.LogError("fortnite-detection", exception);
            FortniteConnectionStatus = $"Process detection failed: {exception.Message}";
        }
    }

    private async Task RefreshOptimizerAsync(CancellationToken cancellationToken)
    {
        if (SelectedAdapter is not null)
        {
            try
            {
                _dnsConfiguration = await _optimizer.GetDnsConfigurationAsync(SelectedAdapter.Id, cancellationToken);
                DnsStatus = _dnsConfiguration.Error ??
                            $"{_dnsConfiguration.Mode}; {_dnsConfiguration.Servers.Count switch
                            {
                                0 => "no IPv4 DNS servers reported",
                                1 => _dnsConfiguration.Servers[0],
                                _ => string.Join(", ", _dnsConfiguration.Servers)
                            }}";
                if (_dnsConfiguration.Servers.Count > 0)
                {
                    DnsPrimaryInput = _dnsConfiguration.Servers[0];
                    DnsSecondaryInput = _dnsConfiguration.Servers.Count > 1
                        ? _dnsConfiguration.Servers[1]
                        : "";
                }

                try
                {
                    SelectiveSuspendStatus = await _optimizer.GetSelectiveSuspendAsync(
                        SelectedAdapter.Name,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    SelectiveSuspendStatus = $"Unavailable: {exception.Message}";
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                DnsStatus = $"Unavailable: {exception.Message}";
                _store.LogError("read-optimizer-dns", exception);
            }
        }
        else
        {
            _dnsConfiguration = null;
            DnsStatus = "No network adapter is available.";
            SelectiveSuspendStatus = "Unavailable";
            _dnsConfiguration = null;
            ApplyDnsCommand.NotifyCanExecuteChanged();
        }

        _powerPlan = await _optimizer.GetPowerPlanAsync(cancellationToken);
        PowerPlanName = _powerPlan.Error is null
            ? $"{_powerPlan.ActiveName} · {_powerPlan.ActiveId}"
            : $"Unavailable: {_powerPlan.Error}";
        OnPropertyChanged(nameof(CanApplyPowerPlan));

        Backups.Clear();
        foreach (var backup in await _store.LoadBackupsAsync())
        {
            Backups.Add(backup);
        }
        LatestDnsBackup = Backups.FirstOrDefault(backup => backup.SettingKind == "Dns");
        LatestPowerPlanBackup = Backups.FirstOrDefault(backup => backup.SettingKind == "PowerPlan");
        LatestSelectiveSuspendBackup = Backups.FirstOrDefault(backup => backup.SettingKind == "SelectiveSuspend");

        Operations.Clear();
        foreach (var operation in (await _store.LoadOperationsAsync()).OrderByDescending(item => item.Timestamp))
        {
            Operations.Add(operation);
        }
        OnPropertyChanged(nameof(DnsOperationStatus));
        OnPropertyChanged(nameof(PowerPlanOperationStatus));
        OnPropertyChanged(nameof(SelectiveSuspendOperationStatus));
    }

    private async Task RefreshOptimizerSafelyAsync()
    {
        try
        {
            await RefreshOptimizerAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _store.LogError("refresh-optimizer", exception);
            DnsStatus = $"Unable to read settings: {exception.Message}";
        }
    }

    private async Task ApplyDnsAsync(CancellationToken cancellationToken)
    {
        var servers = SafeOptimizerService.ValidateDnsServers([DnsPrimaryInput, DnsSecondaryInput]);
        var result = await _optimizer.ApplyDnsAsync(
            SelectedAdapter?.Id ?? throw new InvalidOperationException("Select a network adapter first."),
            servers,
            ProbeHost,
            (current, proposed) => ConfirmAsync(
                "Apply IPv4 DNS servers",
                $"Setting: IPv4 DNS on {current.AdapterName}\n" +
                $"Current: {current.Mode} — {string.Join(", ", current.Servers)}\n" +
                $"Proposed: Static — {string.Join(", ", proposed)}\n\n" +
                "Why: use only the DNS servers you entered for name resolution.\n" +
                "Possible side effects: name-resolution behavior, filtering, availability, or privacy may change. This does not guarantee lower latency.\n" +
                "Permissions: Administrator may be required. No elevation will be requested automatically.\n" +
                $"Performance check: five ICMP samples to {ProbeHost} before and after.\n\n" +
                "A timestamped backup will be saved before the system setting changes.",
                cancellationToken),
            cancellationToken);
        await ShowOptimizerResultAsync(result);
    }

    private async Task ApplyPowerPlanAsync(CancellationToken cancellationToken)
    {
        var result = await _optimizer.ApplyHighPerformancePlanAsync(
            ProbeHost,
            current => ConfirmAsync(
                "Switch Windows power plan",
                $"Setting: active Windows power plan\n" +
                $"Current: {current.ActiveName} ({current.ActiveId})\n" +
                "Proposed: High performance\n\n" +
                "Why: offer a Windows power plan that favors performance over power savings.\n" +
                "Possible side effects: increased power use, heat, fan noise, or reduced battery life. It does not guarantee lower ping or higher FPS.\n" +
                "Permissions: Administrator may be required. No elevation will be requested automatically.\n" +
                $"Performance check: five ICMP samples to {ProbeHost} before and after.\n\n" +
                "The original plan will be backed up before switching.",
                cancellationToken),
            cancellationToken);
        await ShowOptimizerResultAsync(result);
    }

    private async Task ApplySelectiveSuspendAsync(CancellationToken cancellationToken)
    {
        var result = await _optimizer.ApplySelectiveSuspendAsync(
            SelectedAdapter?.Id ?? throw new InvalidOperationException("Select a network adapter first."),
            ProbeHost,
            current => ConfirmAsync(
                "Disable adapter selective suspend",
                $"Setting: selective suspend on {SelectedAdapter?.Name}\n" +
                $"Current: {current}\nProposed: Disabled\n\n" +
                "Why: prevent Windows from selectively suspending this adapter to save power.\n" +
                "Possible side effects: increased power use, heat, or battery drain; adapter or driver behavior can vary. This does not guarantee lower ping.\n" +
                "Permissions: Administrator is normally required. No elevation will be requested automatically.\n" +
                $"Performance check: five ICMP samples to {ProbeHost} before and after.\n\n" +
                "The original value will be backed up before changing it.",
                cancellationToken),
            cancellationToken);
        await ShowOptimizerResultAsync(result);
    }

    private async Task RestoreBackupAsync(OptimizerBackup? backup, CancellationToken cancellationToken)
    {
        if (backup is null)
        {
            return;
        }

        var result = await _optimizer.RestoreBackupAsync(
            backup,
            ProbeHost,
            (item, current) => ConfirmAsync(
                "Restore backed-up setting",
                $"Setting: {item.SettingName}\n" +
                $"Current value: {current}\n" +
                $"Restore original value: {item.OriginalMode switch
                {
                    "Static" => $"Static DNS — {string.Join(", ", item.OriginalServers)}",
                    "DHCP" => "DNS assigned automatically by DHCP",
                    _ => item.OriginalValue
                }}\n" +
                $"Backup: {item.BackupFile}\n\n" +
                "The selected setting will be restored and verified. Other settings will not be changed.",
                cancellationToken),
            cancellationToken);
        await ShowOptimizerResultAsync(result);
    }

    private async Task ShowOptimizerResultAsync(OptimizerApplyResult result)
    {
        _lastOptimizerResult = result;
        OnPropertyChanged(nameof(HasOptimizerComparison));
        OptimizerResult = result.Details;
        StatusMessage = result.Status;
        if (result.BackupFile is not null)
        {
            await RefreshOptimizerAsync(CancellationToken.None);
        }
    }

    private static Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    private async Task SaveMeasurementAsync(DiagnosticResult result, string testType)
    {
        var measurement = new MeasurementRecord
        {
            Timestamp = result.Timestamp,
            ProbeHost = result.Target,
            TestType = testType,
            AverageLatencyMs = result.AverageLatencyMs,
            JitterMs = result.JitterMs,
            PacketLossPercent = result.PacketLossPercent,
            Sent = result.Sent,
            Received = result.Received,
            Error = result.Error
        };
        await _store.AppendMeasurementAsync(measurement);
        Application.Current.Dispatcher.Invoke(() =>
        {
            History.Insert(0, measurement);
            while (History.Count > 5000)
            {
                History.RemoveAt(History.Count - 1);
            }

            UpdateHistoryChart();
        });
    }

    private static string DescribeDiagnostic(DiagnosticResult result)
    {
        var latency = result.AverageLatencyMs is null ? "Unavailable" : $"{result.AverageLatencyMs:0.##} ms";
        var jitter = result.JitterMs is null ? "Unavailable" : $"{result.JitterMs:0.##} ms";
        return $"{result.Target}: latency {latency}; jitter {jitter}; packet loss {result.PacketLossPercent:0.#}% " +
               $"({result.Received}/{result.Sent} replies).";
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var previousReceived = 0L;
        var previousSent = 0L;
        var previousAt = Stopwatch.GetTimestamp();
        var lastSavedAt = DateTimeOffset.Now;
        var lastFortniteCheck = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var adapterId = _selectedAdapterId;
                var adapter = await Task.Run(
                    () => NetworkDiagnostics.FindAdapter(adapterId) ?? NetworkDiagnostics.FindDefaultAdapter(),
                    cancellationToken);
                var now = Stopwatch.GetTimestamp();
                var elapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(previousAt, now).TotalSeconds);
                previousAt = now;
                var adapterIdForTraffic = adapter?.Id;
                if (!string.Equals(_trafficAdapterId, adapterIdForTraffic, StringComparison.Ordinal))
                {
                    _trafficAdapterId = adapterIdForTraffic;
                    _hasCounterBaseline = false;
                }

                var received = adapter?.BytesReceived ?? 0;
                var sent = adapter?.BytesSent ?? 0;
                var hasTrafficInterval = adapter is not null && _hasCounterBaseline;
                var download = hasTrafficInterval
                    ? Math.Max(0, received - previousReceived) / elapsed
                    : 0;
                var upload = hasTrafficInterval
                    ? Math.Max(0, sent - previousSent) / elapsed
                    : 0;
                previousReceived = received;
                previousSent = sent;
                _hasCounterBaseline = adapter is not null;

                var target = _probeHost;
                var probe = await NetworkDiagnostics.MeasureAsync(
                    target,
                    1,
                    TimeSpan.FromSeconds(1.5),
                    cancellationToken);
                if (probe.Received > 0 && probe.AverageLatencyMs is { } latency)
                {
                    _recentLatencies.Enqueue(latency);
                    while (_recentLatencies.Count > 30)
                    {
                        _recentLatencies.Dequeue();
                    }
                }

                _recentReplies.Enqueue(probe.Received > 0);
                while (_recentReplies.Count > 30)
                {
                    _recentReplies.Dequeue();
                }

                var rollingLatency = _recentLatencies.Count > 0 ? _recentLatencies.Average() : (double?)null;
                double? rollingJitter = _recentLatencies.Count > 1
                    ? _recentLatencies.Zip(_recentLatencies.Skip(1), (previous, next) => Math.Abs(next - previous)).Average()
                    : null;
                var rollingLoss = _recentReplies.Count > 0
                    ? 100d * _recentReplies.Count(reply => !reply) / _recentReplies.Count
                    : (double?)null;
                var snapshot = new NetworkSnapshot
                {
                    AdapterName = adapter?.Name ?? "Unavailable",
                    InterfaceStatus = adapter?.Status ?? "Unavailable",
                    DownloadBytesPerSecond = download,
                    UploadBytesPerSecond = upload,
                    InternetReachable = probe.Received > 0,
                    ProbeHost = target,
                    LatencyMs = rollingLatency,
                    JitterMs = rollingJitter,
                    PacketLossPercent = rollingLoss,
                    Sent = _recentReplies.Count,
                    Received = _recentReplies.Count(reply => reply),
                    DiagnosticError = probe.Error
                };
                Application.Current.Dispatcher.Invoke(() =>
                {
                    DownloadBytesPerSecond = snapshot.DownloadBytesPerSecond;
                    UploadBytesPerSecond = snapshot.UploadBytesPerSecond;
                    HasTrafficMeasurement = hasTrafficInterval;
                    LatencyMs = snapshot.LatencyMs;
                    JitterMs = snapshot.JitterMs;
                    PacketLossPercent = snapshot.PacketLossPercent;
                    ConnectionStatus = snapshot.InternetReachable == true
                        ? $"Probe {snapshot.ProbeHost} reachable · {snapshot.Received}/{snapshot.Sent} recent replies"
                        : $"No ICMP reply from {snapshot.ProbeHost} · {snapshot.Received}/{snapshot.Sent} recent replies";
                    if (snapshot.DiagnosticError is not null)
                    {
                        StatusMessage = snapshot.DiagnosticError;
                    }
                });

                if (DateTimeOffset.Now - lastFortniteCheck >= TimeSpan.FromSeconds(5))
                {
                    lastFortniteCheck = DateTimeOffset.Now;
                    var gameRunning = await Task.Run(NetworkDiagnostics.IsFortniteRunning, cancellationToken);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        IsFortniteRunning = gameRunning;
                        FortniteConnectionStatus = gameRunning
                            ? $"The game process is running. Windows does not expose a Fortnite server endpoint here; the separate probe targets {target}."
                            : "No Fortnite game process was detected. No game-server connection is claimed.";
                    });
                }

                if (DateTimeOffset.Now - lastSavedAt >= TimeSpan.FromSeconds(30) && _recentReplies.Count >= 2)
                {
                    lastSavedAt = DateTimeOffset.Now;
                    var historyRecord = new DiagnosticResult
                    {
                        Target = target,
                        Timestamp = DateTimeOffset.Now,
                        AverageLatencyMs = rollingLatency,
                        JitterMs = rollingJitter,
                        PacketLossPercent = rollingLoss ?? 0,
                        Sent = _recentReplies.Count,
                        Received = _recentReplies.Count(reply => reply),
                        Error = rollingLatency is null ? "No successful ICMP samples in the rolling window." : null
                    };
                    await SaveMeasurementAsync(historyRecord, "Live monitor (rolling 30-sample window)");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _store.LogError("network-monitor", exception);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ConnectionStatus = $"Measurement error: {exception.Message}";
                    SetErrorMessage(exception.Message);
                });
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void UpdateWidgets()
    {
        foreach (var widget in VisibleWidgets)
        {
            (widget.Value, widget.Detail) = widget.Id switch
            {
                "connection" => (
                    SelectedAdapter?.Status ?? "Unavailable",
                    ConnectionStatus),
                "latency" => (
                    LatencyLabel,
                    $"Jitter {JitterLabel} · Loss {PacketLossLabel}"),
                "traffic" => (
                    $"↓ {DownloadRate}",
                    $"↑ {UploadRate}"),
                "fortnite" => (
                    FortniteStatus,
                    FortniteConnectionStatus),
                "adapter" => (
                    SelectedAdapter?.Name ?? "Unavailable",
                    SelectedAdapter?.Description ?? "No adapter selected"),
                _ => ("Unavailable", "")
            };
        }
    }

    private DashboardWidget CreateWidget(string id, string title, string icon)
    {
        var widget = new DashboardWidget(id, title, icon);
        if (_settings.WidgetWidths.TryGetValue(id, out var savedWidth))
        {
            widget.Width = savedWidth is >= 250 and <= 550 ? savedWidth : 265;
        }

        return widget;
    }

    private void SaveWidgetLayout()
    {
        _settings.DashboardOrder = new ObservableCollection<string>(BuildFullWidgetOrder());
        _settings.HiddenWidgets = new ObservableCollection<string>(HiddenWidgets);
        _ = SaveSettingsSafelyAsync();
    }

    private List<string> BuildFullWidgetOrder()
    {
        var order = VisibleWidgets.Select(widget => widget.Id).ToList();
        foreach (var hiddenId in HiddenWidgets)
        {
            var oldIndex = _settings.DashboardOrder.IndexOf(hiddenId);
            order.Insert(oldIndex < 0 ? order.Count : Math.Min(oldIndex, order.Count), hiddenId);
        }

        return order;
    }

    private void ResetLayout()
    {
        VisibleWidgets.Clear();
        HiddenWidgets.Clear();
        _settings.WidgetWidths.Clear();
        foreach (var item in WidgetDefinitions)
        {
            VisibleWidgets.Add(CreateWidget(item.Id, item.Title, item.Icon));
        }

        SidebarCollapsed = false;
        SidebarVisible = true;
        SidebarOnRight = false;
        SaveWidgetLayout();
        UpdateWidgets();
        StatusMessage = "Default dashboard layout restored.";
    }

    private void CreatePreset()
    {
        var name = InputDialog.Show("Create layout preset", "Preset name", "", Application.Current.MainWindow);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var preset = CapturePreset(name.Trim());
        LayoutPresets.Add(preset);
        SelectedPreset = preset;
        PersistPresets();
        StatusMessage = $"Layout preset “{preset.Name}” created.";
    }

    private void UpdatePreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var replacement = CapturePreset(SelectedPreset.Name);
        replacement.Id = SelectedPreset.Id;
        var index = LayoutPresets.IndexOf(SelectedPreset);
        LayoutPresets[index] = replacement;
        SelectedPreset = replacement;
        PersistPresets();
        StatusMessage = $"Layout preset “{replacement.Name}” updated.";
    }

    private void RenamePreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var name = InputDialog.Show("Rename layout preset", "Preset name", SelectedPreset.Name, Application.Current.MainWindow);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SelectedPreset.Name = name.Trim();
        PersistPresets();
        OnPropertyChanged(nameof(SelectedPreset));
        StatusMessage = $"Layout preset renamed to “{SelectedPreset.Name}”.";
    }

    private void DeletePreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        if (MessageBox.Show(
                Application.Current.MainWindow,
                $"Delete the “{SelectedPreset.Name}” layout preset?",
                "Delete preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        LayoutPresets.Remove(SelectedPreset);
        SelectedPreset = LayoutPresets.FirstOrDefault();
        PersistPresets();
        StatusMessage = "Layout preset deleted.";
    }

    private void ApplyPreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var preset = SelectedPreset;
        Theme = preset.Theme;
        AccentColor = preset.AccentColor;
        Scale = preset.Scale;
        SidebarCollapsed = preset.SidebarCollapsed;
        SidebarVisible = preset.SidebarVisible;
        SidebarOnRight = preset.SidebarOnRight;
        _settings.WidgetWidths = new Dictionary<string, double>(preset.WidgetWidths, StringComparer.OrdinalIgnoreCase);
        _settings.DashboardOrder = new ObservableCollection<string>(preset.DashboardOrder);
        _settings.HiddenWidgets = new ObservableCollection<string>(preset.HiddenWidgets);
        RebuildWidgets();
        SaveWidgetLayout();
        StatusMessage = $"Layout preset “{preset.Name}” applied.";
    }

    private LayoutPreset CapturePreset(string name) => new()
    {
        Name = name,
        Theme = Theme,
        AccentColor = AccentColor,
        Scale = Scale,
        SidebarCollapsed = SidebarCollapsed,
        SidebarVisible = SidebarVisible,
        SidebarOnRight = SidebarOnRight,
        DashboardOrder = new ObservableCollection<string>(BuildFullWidgetOrder()),
        HiddenWidgets = new ObservableCollection<string>(HiddenWidgets),
        WidgetWidths = new Dictionary<string, double>(_settings.WidgetWidths, StringComparer.OrdinalIgnoreCase)
    };

    private void RebuildWidgets()
    {
        VisibleWidgets.Clear();
        HiddenWidgets.Clear();
        foreach (var id in _settings.HiddenWidgets)
        {
            if (WidgetIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                HiddenWidgets.Add(id);
            }
        }

        foreach (var id in _settings.DashboardOrder)
        {
            var definition = WidgetDefinitions.FirstOrDefault(item => item.Id == id);
            if (definition.Id is not null && !HiddenWidgets.Contains(id))
            {
                VisibleWidgets.Add(CreateWidget(definition.Id, definition.Title, definition.Icon));
            }
        }

        UpdateWidgets();
    }

    private void PersistPresets()
    {
        _settings.LayoutPresets = new ObservableCollection<LayoutPreset>(LayoutPresets);
        _ = SaveSettingsSafelyAsync();
    }

    private async Task SaveProfileAsync(CancellationToken cancellationToken)
    {
        var result = ProfileDialog.ShowEditor(
            Application.Current.MainWindow,
            null,
            Adapters.ToList(),
            SelectedAdapter?.Id);
        if (result is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Profiles.Add(result);
        SelectedProfile = result;
        await _store.SaveProfilesAsync(Profiles);
        StatusMessage = $"Profile “{result.Name}” saved. Selecting a profile changes only app preferences.";
    }

    private async Task EditProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var selected = SelectedProfile;
        var result = ProfileDialog.ShowEditor(
            Application.Current.MainWindow,
            selected,
            Adapters.ToList(),
            SelectedAdapter?.Id);
        if (result is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var index = Profiles.IndexOf(selected);
        Profiles[index] = result;
        SelectedProfile = result;
        await _store.SaveProfilesAsync(Profiles);
        StatusMessage = $"Profile “{result.Name}” updated.";
    }

    private async Task DeleteProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var selected = SelectedProfile;
        if (MessageBox.Show(
                Application.Current.MainWindow,
                $"Delete the “{selected.Name}” application profile?",
                "Delete profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Profiles.Remove(selected);
        SelectedProfile = Profiles.FirstOrDefault();
        if (SelectedProfile is null)
        {
            _settings.SelectedProfileId = null;
        }

        await _store.SaveProfilesAsync(Profiles);
        await SaveSettingsSafelyAsync();
        StatusMessage = "Profile deleted.";
    }

    private void ClearHistory()
    {
        if (MessageBox.Show(
                Application.Current.MainWindow,
                "Permanently clear all saved network measurements?",
                "Clear history",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        History.Clear();
        HistoryChartPoints = [];
        HistorySummary = "No saved measurements yet.";
        _ = ClearHistorySafelyAsync();
    }

    private async Task ClearHistorySafelyAsync()
    {
        try
        {
            await _store.SaveHistoryAsync([]);
            StatusMessage = "Measurement history cleared.";
        }
        catch (Exception exception)
        {
            HandleExecutionFailure(exception);
        }
    }

    private void UpdateHistoryChart()
    {
        var measured = History.Where(record => record.AverageLatencyMs.HasValue)
            .OrderBy(record => record.Timestamp)
            .TakeLast(60)
            .ToList();
        if (measured.Count == 0)
        {
            HistoryChartPoints = [];
            HistorySummary = "No successful latency measurements are available to chart.";
            return;
        }

        var maximum = Math.Max(1, measured.Max(record => record.AverageLatencyMs!.Value));
        var points = new PointCollection();
        for (var index = 0; index < measured.Count; index++)
        {
            var x = measured.Count == 1 ? 300 : index * 600d / (measured.Count - 1);
            var y = 110 - measured[index].AverageLatencyMs!.Value / maximum * 100;
            points.Add(new Point(x, y));
        }

        HistoryChartPoints = points;
        HistorySummary = $"Showing {measured.Count} successful measurement(s). Latest: " +
                         $"{measured[^1].AverageLatencyMs:0.##} ms at {measured[^1].Timestamp.LocalDateTime:t}. " +
                         $"Probe: {measured[^1].ProbeHost}.";
    }

    private async Task SaveSettingsSafelyAsync()
    {
        try
        {
            await _store.SaveSettingsAsync(_settings);
        }
        catch (Exception exception)
        {
            HandleExecutionFailure(exception);
        }
    }

    private AsyncRelayCommand CreateAsyncCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null)
    {
        var command = new AsyncRelayCommand(execute, canExecute);
        command.ExecutionFailed += HandleExecutionFailure;
        return command;
    }

    private AsyncRelayCommand<T> CreateAsyncCommand<T>(
        Func<T?, CancellationToken, Task> execute,
        Predicate<T?>? canExecute = null)
    {
        var command = new AsyncRelayCommand<T>(execute, canExecute);
        command.ExecutionFailed += HandleExecutionFailure;
        return command;
    }

    private void HandleExecutionFailure(Exception exception)
    {
        _store.LogError("application-operation", exception);
        SetErrorMessage(exception.Message);
        if (CurrentSection == "Optimizer")
        {
            _ = RefreshOptimizerSafelyAsync();
        }
    }

    private string LatestOperationStatus(string settingKind) =>
        Operations.FirstOrDefault(operation => operation.SettingKind == settingKind)?.Status ?? "Not applied";

    private void SetErrorMessage(string message)
    {
        ErrorMessage = message;
        StatusMessage = "Action failed. See the error message for details.";
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await SaveSettingsSafelyAsync();
        _lifetime.Dispose();
    }
}
