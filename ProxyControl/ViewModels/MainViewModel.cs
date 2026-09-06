using Microsoft.Win32;
using ProxyControl.Models;
using ProxyControl.Services;
using ProxyControl.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProxyControl.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly TcpProxyService _proxyService;
        private readonly DnsProxyService _dnsProxyService;
        private readonly TunService _tunService;
        private readonly SettingsService _settingsService;
        private readonly GithubUpdateService _updateService;
        private readonly TrafficMonitorService _trafficMonitorService;
        private readonly SiteIconCacheService _siteIconCacheService;



        private AppConfig _config;
        // private CancellationTokenSource _enforceCts; // Unused in TUN mode
        private CancellationTokenSource? _saveDebounceCts;
        private CancellationTokenSource? _dohStatusRefreshCts;
        private bool _suppressSave = false;
        private string _dohSaveStatus = "Saved";
        private string _dohTransportStatus = string.Empty;
        private string _primaryDnsStatus = "Unknown";
        private string _primaryDnsStatusDetails = "Not checked";
        private string _fallbackDnsStatus = "Unknown";
        private string _fallbackDnsStatusDetails = "Not checked";
        private bool _isDnsCheckInProgress;
        private readonly ConcurrentQueue<ConnectionLog> _pendingConnectionLogs = new();
        private int _pendingConnectionLogCount;
        private readonly DispatcherTimer _connectionLogTimer;
        private const int MaxPendingConnectionLogs = 10000;
        private readonly CancellationTokenSource _connectionIconCts = new();
        private readonly Channel<ConnectionIconRequest> _connectionIconQueue =
            Channel.CreateBounded<ConnectionIconRequest>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        private readonly List<TrafficRule> _temporaryBlackListRules = new();
        private readonly List<TrafficRule> _temporaryWhiteListRules = new();

        private sealed record ConnectionIconRequest(
            string Host,
            Action<ImageSource?> Assign);

        public string DohSaveStatus
        {
            get => _dohSaveStatus;
            private set
            {
                if (_dohSaveStatus != value)
                {
                    _dohSaveStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        // TUN Mode (WebRTC/UDP bypass)
        private bool _isTunMode;
        private CancellationTokenSource? _tunRefreshCts;

        public IEnumerable<ProxyRoutingMode> RoutingModes => Enum.GetValues(typeof(ProxyRoutingMode)).Cast<ProxyRoutingMode>();

        public ProxyRoutingMode RoutingMode
        {
            get => _config.RoutingMode ?? GetLegacyRoutingMode();
            set
            {
                if (RoutingMode == value) return;
                if (UsesTun(value) && !CanEnableTunMode)
                {
                    ShowMessage("TUN requires a proxy", "Add and enable at least one proxy before selecting TUN or Mixed mode.");
                    return;
                }

                _tunRefreshCts?.Cancel();
                _config.RoutingMode = value;
                SynchronizeLegacyRoutingFlags(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTunMode));
                OnPropertyChanged(nameof(IsSystemProxyEnabled));
                OnPropertyChanged(nameof(IsSystemProxyMode));
                OnPropertyChanged(nameof(IsTunOnlyMode));
                OnPropertyChanged(nameof(IsMixedMode));
                OnPropertyChanged(nameof(TunModeStatus));
                ApplyConfig();
                RequestSaveSettings();
                _ = ToggleTunModeAsync();
            }
        }

        public bool IsSystemProxyEnabled => UsesSystemProxy(RoutingMode);
        public bool IsSystemProxyMode
        {
            get => RoutingMode == ProxyRoutingMode.SystemProxy;
            set { if (value) RoutingMode = ProxyRoutingMode.SystemProxy; }
        }

        public bool IsTunOnlyMode
        {
            get => RoutingMode == ProxyRoutingMode.Tun;
            set { if (value) RoutingMode = ProxyRoutingMode.Tun; }
        }

        public bool IsMixedMode
        {
            get => RoutingMode == ProxyRoutingMode.Mixed;
            set { if (value) RoutingMode = ProxyRoutingMode.Mixed; }
        }

        public bool IsTunMode
        {
            get => _isTunMode;
            set
            {
                if (_isTunMode != value)
                {
                    RoutingMode = value
                        ? (IsSystemProxyEnabled ? ProxyRoutingMode.Mixed : ProxyRoutingMode.Tun)
                        : ProxyRoutingMode.SystemProxy;
                }
            }
        }

        private ProxyRoutingMode GetLegacyRoutingMode()
        {
            return _config.IsTunMode
                ? (_config.IsSystemProxyEnabled ? ProxyRoutingMode.Mixed : ProxyRoutingMode.Tun)
                : ProxyRoutingMode.SystemProxy;
        }

        private static bool UsesTun(ProxyRoutingMode mode) => mode is ProxyRoutingMode.Tun or ProxyRoutingMode.Mixed;
        private static bool UsesSystemProxy(ProxyRoutingMode mode) => mode is ProxyRoutingMode.SystemProxy or ProxyRoutingMode.Mixed;

        private void SynchronizeLegacyRoutingFlags(ProxyRoutingMode mode)
        {
            _isTunMode = UsesTun(mode);
            _config.IsTunMode = _isTunMode;
            _config.IsSystemProxyEnabled = UsesSystemProxy(mode);
        }

        private void NormalizeRoutingMode()
        {
            var mode = _config.RoutingMode ?? GetLegacyRoutingMode();
            _config.RoutingMode = mode;
            SynchronizeLegacyRoutingFlags(mode);
        }

        public bool CanEnableTunMode
        {
            get
            {
                return Proxies.Any(p => p.IsEnabled);
            }
        }
        public string TunModeStatus => RoutingMode switch
        {
            ProxyRoutingMode.SystemProxy => "System proxy",
            ProxyRoutingMode.Tun => _isTunMode ? "TUN active" : "TUN off",
            ProxyRoutingMode.Mixed => _isTunMode ? "Mixed active" : "Mixed starting",
            _ => "Off"
        };

        public IEnumerable<RuleAction> ActionTypes => Enum.GetValues(typeof(RuleAction)).Cast<RuleAction>();
        public IEnumerable<BlockDirection> BlockDirectionTypes => Enum.GetValues(typeof(BlockDirection)).Cast<BlockDirection>();

        private string _currentView = "Rules";
        public string CurrentView
        {
            get => _currentView;
            set { _currentView = value; OnPropertyChanged(); }
        }

        private string _currentVersion = "3.2.0";
        public string CurrentVersion
        {
            get => _currentVersion;
            set { _currentVersion = value; OnPropertyChanged(); }
        }





        public ICommand SavePresetCommand { get; }
        public ICommand LoadPresetCommand { get; }
        public ICommand DeletePresetCommand { get; }
        public ICommand SaveProfileCommand { get; }
        public ICommand LoadProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand CreateEmptyProfileCommand { get; }

        public ICommand NavigateCommand { get; }

        private ProxyItem? _tunProxy;
        public ProxyItem? TunProxy
        {
            get => _tunProxy ?? Proxies.FirstOrDefault();
            set
            {
                if (_tunProxy != value)
                {
                    _tunProxy = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanEnableTunMode)); // Notify dependency
                    if (_config != null && _tunProxy != null)
                    {
                        _config.TunProxyId = _tunProxy.Id;
                        RequestSaveSettings();
                    }

                    RefreshTunIfRunning();
                }
            }
        }

        public ObservableCollection<ProxyItem> Proxies { get; set; } = new ObservableCollection<ProxyItem>();
        public ObservableCollection<TrafficRule> RulesList { get; set; } = new ObservableCollection<TrafficRule>();
        public ObservableCollection<ConnectionLog> Logs { get; set; } = new ObservableCollection<ConnectionLog>();
        public ObservableCollection<ActiveProcessInfo> ActiveProcesses { get; } = new();
        public ICollectionView ActiveProcessesView { get; private set; } = null!;
        private readonly ObservableCollection<ConnectionHistoryItem> _emptyMonitorConnections = new ObservableCollection<ConnectionHistoryItem>();

        public IReadOnlyList<string> LogTypeFilters { get; } = new[] { "All", "TCP", "UDP", "DNS", "HTTPS", "WebSocket" };
        public IReadOnlyList<string> LogResultFilters { get; } = new[] { "All", "Proxy", "Direct", "Blocked" };

        private string _logSearchText = "";
        public string LogSearchText
        {
            get => _logSearchText;
            set { _logSearchText = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _logTypeFilter = "All";
        public string LogTypeFilter
        {
            get => _logTypeFilter;
            set { _logTypeFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _logResultFilter = "All";
        public string LogResultFilter
        {
            get => _logResultFilter;
            set { _logResultFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _logProcessFilter = "";
        public string LogProcessFilter
        {
            get => _logProcessFilter;
            set { _logProcessFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _logHostFilter = "";
        public string LogHostFilter
        {
            get => _logHostFilter;
            set { _logHostFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _logResultTextFilter = "";
        public string LogResultTextFilter
        {
            get => _logResultTextFilter;
            set { _logResultTextFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _logCountryFilter = "";
        public string LogCountryFilter
        {
            get => _logCountryFilter;
            set { _logCountryFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _monitorSearchText = "";
        public string MonitorSearchText
        {
            get => _monitorSearchText;
            set { _monitorSearchText = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _monitorTypeFilter = "All";
        public string MonitorTypeFilter
        {
            get => _monitorTypeFilter;
            set { _monitorTypeFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _monitorResultFilter = "All";
        public string MonitorResultFilter
        {
            get => _monitorResultFilter;
            set { _monitorResultFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _monitorHostFilter = "";
        private string _monitorProcessFilter = "";
        public string MonitorProcessFilter
        {
            get => _monitorProcessFilter;
            set { _monitorProcessFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        public string MonitorHostFilter
        {
            get => _monitorHostFilter;
            set { _monitorHostFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _monitorStatusFilter = "";
        public string MonitorStatusFilter
        {
            get => _monitorStatusFilter;
            set { _monitorStatusFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _monitorDetailsFilter = "";
        public string MonitorDetailsFilter
        {
            get => _monitorDetailsFilter;
            set { _monitorDetailsFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        private string _monitorCountryFilter = "";
        public string MonitorCountryFilter
        {
            get => _monitorCountryFilter;
            set { _monitorCountryFilter = value; OnPropertyChanged(); RefreshConnectionLogFilters(); }
        }

        public ObservableCollection<RulePreset> Presets { get; set; } = new ObservableCollection<RulePreset>();
        private RulePreset? _selectedPreset;
        public RulePreset? SelectedPreset
        {
            get => _selectedPreset;
            set { _selectedPreset = value; OnPropertyChanged(); }
        }
        private string _presetName = "My Preset";
        public string PresetName
        {
            get => _presetName;
            set { _presetName = value; OnPropertyChanged(); }
        }

        public string PrimaryDnsStatus
        {
            get => _primaryDnsStatus;
            private set { if (_primaryDnsStatus != value) { _primaryDnsStatus = value; OnPropertyChanged(); } }
        }

        public string PrimaryDnsStatusDetails
        {
            get => _primaryDnsStatusDetails;
            private set { if (_primaryDnsStatusDetails != value) { _primaryDnsStatusDetails = value; OnPropertyChanged(); } }
        }

        public string FallbackDnsStatus
        {
            get => _fallbackDnsStatus;
            private set { if (_fallbackDnsStatus != value) { _fallbackDnsStatus = value; OnPropertyChanged(); } }
        }

        public string FallbackDnsStatusDetails
        {
            get => _fallbackDnsStatusDetails;
            private set { if (_fallbackDnsStatusDetails != value) { _fallbackDnsStatusDetails = value; OnPropertyChanged(); } }
        }

        public bool IsDnsCheckInProgress
        {
            get => _isDnsCheckInProgress;
            private set { if (_isDnsCheckInProgress != value) { _isDnsCheckInProgress = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<AppProfile> Profiles { get; } = new();

        private AppProfile? _selectedProfile;
        public AppProfile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                _selectedProfile = value;
                if (value != null && !string.IsNullOrWhiteSpace(value.Name))
                {
                    _profileName = value.Name;
                    OnPropertyChanged(nameof(ProfileName));
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedProfile));
            }
        }

        public bool HasSelectedProfile => SelectedProfile != null;

        private string _profileName = "My Profile";
        public string ProfileName
        {
            get => _profileName;
            set { _profileName = value; OnPropertyChanged(); }
        }

        private string? _activeProfileId;
        public string ActiveProfileName => Profiles.FirstOrDefault(p => p.Id == _activeProfileId)?.Name ?? "No active profile";
        public bool HasActiveProfile => !string.IsNullOrEmpty(_activeProfileId) && Profiles.Any(p => p.Id == _activeProfileId);

        public string ProxyCheckUrl
        {
            get => string.IsNullOrWhiteSpace(_config.ProxyCheckUrl) ? "https://www.google.com/generate_204" : _config.ProxyCheckUrl;
            set
            {
                _config.ProxyCheckUrl = string.IsNullOrWhiteSpace(value) ? "https://www.google.com/generate_204" : value.Trim();
                if (_proxyService != null) _proxyService.ProxyCheckUrl = _config.ProxyCheckUrl;
                OnPropertyChanged();
                RequestSaveSettings();
            }
        }

        public string ProxySpeedTestUrl
        {
            get => string.IsNullOrWhiteSpace(_config.ProxySpeedTestUrl) ? "https://speed.cloudflare.com/__down?bytes=10000000" : _config.ProxySpeedTestUrl;
            set
            {
                _config.ProxySpeedTestUrl = string.IsNullOrWhiteSpace(value) ? "https://speed.cloudflare.com/__down?bytes=10000000" : value.Trim();
                if (_proxyService != null) _proxyService.ProxySpeedTestUrl = _config.ProxySpeedTestUrl;
                OnPropertyChanged();
                RequestSaveSettings();
            }
        }

        // Grid-based rules UI - Groups as cards
        public IEnumerable<RuleGroupInfo> RuleGroups
        {
            get
            {
                try
                {
                    if (RulesList == null || RulesList.Count == 0)
                        return Enumerable.Empty<RuleGroupInfo>();

                    var source = RulesList.AsEnumerable();
                    if (!string.IsNullOrWhiteSpace(_searchText))
                    {
                        var s = _searchText.ToLower();
                        source = source.Where(r =>
                            (r.GroupName?.ToLower().Contains(s) == true) ||
                            (r.TargetApps?.Any(a => a.ToLower().Contains(s)) == true) ||
                            (r.TargetHosts?.Any(h => h.ToLower().Contains(s)) == true)
                        );
                    }

                    return source.GroupBy(r => r.GroupName ?? "General")
                        .Select(g =>
                        {
                            var rules = g.ToList();
                            return new RuleGroupInfo
                            {
                                GroupName = g.Key,
                                RuleCount = rules.Count,
                                AppCount = rules.SelectMany(r => r.TargetApps ?? new List<string>()).Distinct().Count(),
                                Rules = rules,
                                IsScopeEnabled = rules.Any(r => r.IsEnabled)
                            };
                        })
                        .OrderBy(g => g.GroupName);
                }
                catch (Exception ex)
                {
                    AppLoggerService.Instance.Error("Groups", $"RuleGroups getter error: {ex.Message}");
                    return Enumerable.Empty<RuleGroupInfo>();
                }
            }
        }

        // Confirmation Modal Logic
        private bool _isConfirmModalVisible;
        public bool IsConfirmModalVisible
        {
            get => _isConfirmModalVisible;
            set { _isConfirmModalVisible = value; OnPropertyChanged(); }
        }

        private string _confirmMessage;
        public string ConfirmMessage
        {
            get => _confirmMessage;
            set { _confirmMessage = value; OnPropertyChanged(); }
        }

        private string _confirmTitle = "Confirmation";
        public string ConfirmTitle
        {
            get => _confirmTitle;
            set { _confirmTitle = value; OnPropertyChanged(); }
        }

        private bool _isModalCancelVisible = true;
        public bool IsModalCancelVisible
        {
            get => _isModalCancelVisible;
            set { _isModalCancelVisible = value; OnPropertyChanged(); }
        }

        private Action? _pendingConfirmAction;

        public ICommand CloseConfirmModalCommand { get; }
        public ICommand ConfirmActionCommand { get; }

        public void ShowMessage(string title, string message)
        {
            ConfirmTitle = title;
            ConfirmMessage = message;
            IsModalCancelVisible = false;
            _pendingConfirmAction = null;
            IsConfirmModalVisible = true;
        }

        public void ShowConfirmation(string title, string message, Action onConfirm)
        {
            ConfirmTitle = title;
            ConfirmMessage = message;
            IsModalCancelVisible = true;
            _pendingConfirmAction = onConfirm;
            IsConfirmModalVisible = true;
        }

        // Update Found Modal Logic
        private bool _isUpdateFoundModalVisible;
        public bool IsUpdateFoundModalVisible
        {
            get => _isUpdateFoundModalVisible;
            set { _isUpdateFoundModalVisible = value; OnPropertyChanged(); }
        }

        private UpdateReleaseInfo? _pendingUpdateInfo;
        public UpdateReleaseInfo? PendingUpdateInfo
        {
            get => _pendingUpdateInfo;
            set { _pendingUpdateInfo = value; OnPropertyChanged(); }
        }

        public ObservableCollection<ChangelogEntry> UpdateChangelogEntries { get; } = new();

        public ICommand ConfirmUpdateCommand { get; }
        public ICommand DismissUpdateFoundModalCommand { get; }

        // What's New Modal Logic
        private bool _isWhatsNewModalVisible;
        public bool IsWhatsNewModalVisible
        {
            get => _isWhatsNewModalVisible;
            set { _isWhatsNewModalVisible = value; OnPropertyChanged(); }
        }

        private string _whatsNewVersion = "3.2.0";
        public string WhatsNewVersion
        {
            get => _whatsNewVersion;
            set { _whatsNewVersion = value; OnPropertyChanged(); }
        }

        public ObservableCollection<ChangelogCategory> WhatsNewCategories { get; } = new();

        public ICommand OpenWhatsNewModalCommand { get; }
        public ICommand CloseWhatsNewModalCommand { get; }

        public string HeaderDownloadSpeedText => $"⬇️ {TrafficMonitorService.FormatSpeed(_trafficMonitorService.TotalCurrentDownloadSpeed)}";
        public string HeaderUploadSpeedText => $"⬆️ {TrafficMonitorService.FormatSpeed(_trafficMonitorService.TotalCurrentUploadSpeed)}";
        public string HeaderActiveConnectionsText => $"⚡ {_trafficMonitorService.TotalActiveConnections} conn";

        // Group/App Management Commands
        public ICommand EditGroupCommand { get; }
        public ICommand RemoveGroupCommand { get; }
        public ICommand EditAppCommand { get; }
        public ICommand RemoveAppCommand { get; }
        public ICommand ToggleGroupRulesCommand { get; }
        public ICommand ToggleAppRulesCommand { get; }
        public ICommand AddRuleToGroupCommand { get; }
        public ICommand AddRuleToAppCommand { get; }
        public ICommand CopyTextCommand { get; }

        // Batch Edit State
        private bool _isBatchEditMode;
        private string _batchEditTarget = "";
        private string _batchEditValue = "";

        private bool _isRenameGroupMode;
        public bool IsRenameGroupMode
        {
            get => _isRenameGroupMode;
            set { _isRenameGroupMode = value; OnPropertyChanged(); }
        }  // GroupName or AppName



        private string? _selectedGroupName;
        public string? SelectedGroupName
        {
            get => _selectedGroupName;
            set
            {
                try
                {
                    _selectedGroupName = value;
                    _selectedRule = null;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedRule));
                    OnPropertyChanged(nameof(HasSelectedRule));
                    OnPropertyChanged(nameof(SelectedGroupApps));
                    OnPropertyChanged(nameof(SelectedGroupRules));
                    OnPropertyChanged(nameof(HasSelectedGroupApps));
                    OnPropertyChanged(nameof(HasSelectedGroupRules));
                    OnPropertyChanged(nameof(IsGroupSelected));
                }
                catch (Exception ex)
                {
                    AppLoggerService.Instance.Error("Groups", $"SelectedGroupName setter error: {ex.Message}");
                }
            }
        }
        public bool IsGroupSelected => !string.IsNullOrEmpty(_selectedGroupName);
        public bool HasSelectedGroupApps => SelectedGroupApps?.Any() == true;
        public bool HasSelectedGroupRules => SelectedGroupRules?.Any() == true;

        public IEnumerable<AppRuleInfo> SelectedGroupApps
        {
            get
            {
                try
                {
                    if (string.IsNullOrEmpty(_selectedGroupName) || RulesList == null)
                        return Enumerable.Empty<AppRuleInfo>();

                    var query = RulesList.Where(r => (r.GroupName ?? "General") == _selectedGroupName);

                    // Filter by search text if present
                    if (!string.IsNullOrWhiteSpace(_searchText))
                    {
                        var s = _searchText.ToLower();
                        query = query.Where(r =>
                            (r.TargetApps?.Any(a => a.ToLower().Contains(s)) == true) ||
                            (r.TargetHosts?.Any(h => h.ToLower().Contains(s)) == true)
                        );
                    }

                    return query
                        .SelectMany(r => r.TargetApps ?? new List<string>())
                        .Distinct()
                        .Select(app =>
                        {
                            var appRules = RulesList
                                .Where(r => (r.GroupName ?? "General") == _selectedGroupName &&
                                    (r.TargetApps?.Contains(app) ?? false))
                                .ToList();
                            return new AppRuleInfo
                            {
                                AppName = app,
                                RuleCount = appRules.Count,
                                AppIcon = appRules
                                    .Select(r => r.AppIcon)
                                    .FirstOrDefault(icon => icon != null),
                                Rules = appRules,
                                IsScopeEnabled = appRules.Any(r => r.IsEnabled)
                            };
                        })
                        .ToList();
                }
                catch
                {
                    return Enumerable.Empty<AppRuleInfo>();
                }
            }
        }

        public IEnumerable<TrafficRule> SelectedGroupRules
        {
            get
            {
                try
                {
                    if (string.IsNullOrEmpty(_selectedGroupName) || RulesList == null)
                        return Enumerable.Empty<TrafficRule>();

                    var groupRules = RulesList.Where(r => (r.GroupName ?? "General") == _selectedGroupName);

                    // Filter by search text if present
                    if (!string.IsNullOrWhiteSpace(_searchText))
                    {
                        var s = _searchText.ToLower();
                        groupRules = groupRules.Where(r =>
                            (r.TargetApps?.Any(a => a.ToLower().Contains(s)) == true) ||
                            (r.TargetHosts?.Any(h => h.ToLower().Contains(s)) == true)
                        );
                    }

                    // If an app is selected, filter by that app
                    if (!string.IsNullOrEmpty(_selectedAppName))
                    {
                        groupRules = groupRules.Where(r => r.TargetApps?.Contains(_selectedAppName) ?? false);
                    }

                    return groupRules.ToList();
                }
                catch
                {
                    return Enumerable.Empty<TrafficRule>();
                }
            }
        }

        private string? _selectedAppName;
        public string? SelectedAppName
        {
            get => _selectedAppName;
            set
            {
                try
                {
                    _selectedAppName = value;
                    _selectedRule = null;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedRule));
                    OnPropertyChanged(nameof(HasSelectedRule));
                    OnPropertyChanged(nameof(SelectedGroupRules));
                    OnPropertyChanged(nameof(HasSelectedGroupRules));
                    OnPropertyChanged(nameof(IsAppSelected));
                }
                catch (Exception ex)
                {
                    AppLoggerService.Instance.Error("Groups", $"SelectedAppName setter error: {ex.Message}");
                }
            }
        }
        public bool IsAppSelected => !string.IsNullOrEmpty(_selectedAppName);

        private void RefreshRuleGroups()
        {
            if (string.IsNullOrEmpty(_selectedGroupName))
            {
                var first = RuleGroups?.FirstOrDefault();
                if (first != null)
                {
                    _selectedGroupName = first.GroupName;
                    OnPropertyChanged(nameof(SelectedGroupName));
                    OnPropertyChanged(nameof(IsGroupSelected));
                }
            }
            OnPropertyChanged(nameof(RuleGroups));
            OnPropertyChanged(nameof(SelectedGroupApps));
            OnPropertyChanged(nameof(SelectedGroupRules));
            OnPropertyChanged(nameof(HasSelectedGroupApps));
            OnPropertyChanged(nameof(HasSelectedGroupRules));
        }

        // Application Logs (startup, connections, errors, WebRTC blocks)
        public ObservableCollection<LogEntry> AppLogs => AppLoggerService.Instance.LogEntries;

        public ObservableCollection<ProcessTrafficData> MonitoredProcesses => _trafficMonitorService.DisplayedProcessList;

        private ProcessTrafficData? _selectedMonitorProcess;
        public ProcessTrafficData? SelectedMonitorProcess
        {
            get => _selectedMonitorProcess;
            set
            {
                _selectedMonitorProcess = value;
                UpdateMonitorConnectionsView();
                OnPropertyChanged();
            }
        }

        private TrafficPeriodMode _selectedPeriodMode = TrafficPeriodMode.LiveSession;
        private CancellationTokenSource? _monitorPeriodCts;
        private bool _isMonitorPeriodLoading;
        private string _monitorPeriodStatus = "Live traffic";

        public TrafficPeriodMode SelectedPeriodMode
        {
            get => _selectedPeriodMode;
            set
            {
                if (_selectedPeriodMode == value) return;
                _selectedPeriodMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDateRangeVisible));
                _ = ApplyMonitorPeriodAsync();
            }
        }

        public bool IsDateRangeVisible => SelectedPeriodMode == TrafficPeriodMode.CustomRange;

        public bool IsMonitorPeriodLoading
        {
            get => _isMonitorPeriodLoading;
            private set
            {
                if (_isMonitorPeriodLoading == value) return;
                _isMonitorPeriodLoading = value;
                OnPropertyChanged();
            }
        }

        public string MonitorPeriodStatus
        {
            get => _monitorPeriodStatus;
            private set
            {
                if (_monitorPeriodStatus == value) return;
                _monitorPeriodStatus = value;
                OnPropertyChanged();
            }
        }

        private DateTime _filterDateStart = DateTime.Now;
        public DateTime FilterDateStart
        {
            get => _filterDateStart;
            set { _filterDateStart = value; OnPropertyChanged(); }
        }

        private DateTime _filterDateEnd = DateTime.Now;
        public DateTime FilterDateEnd
        {
            get => _filterDateEnd;
            set { _filterDateEnd = value; OnPropertyChanged(); }
        }

        private string _filterTimeStart = "00:00";
        public string FilterTimeStart
        {
            get => _filterTimeStart;
            set { _filterTimeStart = value; OnPropertyChanged(); }
        }

        private string _filterTimeEnd = "23:59";
        public string FilterTimeEnd
        {
            get => _filterTimeEnd;
            set { _filterTimeEnd = value; OnPropertyChanged(); }
        }

        public bool UseAdvancedLogFilters
        {
            get => _config.UseAdvancedLogFilters;
            set
            {
                if (_config.UseAdvancedLogFilters != value)
                {
                    _config.UseAdvancedLogFilters = value;
                    OnPropertyChanged();
                    RefreshConnectionLogFilters();
                    RequestSaveSettings();
                }
            }
        }

        public ICollectionView RulesView { get; private set; }
        public ICollectionView LogsView { get; private set; }
        public ICollectionView MonitorConnectionsView { get; private set; }

        public string ToggleProxyMenuText => IsProxyRunning ? "Turn Proxy OFF" : "Turn Proxy ON";
        public string AppVersion => "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private bool _isUpdateModalVisible;
        public bool IsUpdateModalVisible
        {
            get => _isUpdateModalVisible;
            set { _isUpdateModalVisible = value; OnPropertyChanged(); }
        }

        private int _updateProgress;
        public int UpdateProgress
        {
            get => _updateProgress;
            set { _updateProgress = value; OnPropertyChanged(); }
        }

        private string _updateStatusText = "Initializing...";
        public string UpdateStatusText
        {
            get => _updateStatusText;
            set { _updateStatusText = value; OnPropertyChanged(); }
        }

        private string _updateDetailText = "";
        public string UpdateDetailText
        {
            get => _updateDetailText;
            set { _updateDetailText = value; OnPropertyChanged(); }
        }

        public string? PendingUpdateUrl { get; set; }
        public long PendingUpdateSize { get; set; }

        public ICommand OpenUpdateModalCommand { get; }

        private string _newRuleApps = "*";
        public string NewRuleApps { get => _newRuleApps; set { _newRuleApps = value; OnPropertyChanged(); } }

        private string _newRuleHosts = "*";
        public string NewRuleHosts { get => _newRuleHosts; set { _newRuleHosts = value; OnPropertyChanged(); } }

        private string _newRuleGroup = "General";
        public string NewRuleGroup { get => _newRuleGroup; set { _newRuleGroup = value; OnPropertyChanged(); } }

        private RuleAction _newRuleAction = RuleAction.Proxy;
        public RuleAction NewRuleAction
        {
            get => _newRuleAction;
            set
            {
                _newRuleAction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNewRuleProxyRequired));
            }
        }

        private BlockDirection _newRuleBlockDirection = BlockDirection.Both;
        public BlockDirection NewRuleBlockDirection
        {
            get => _newRuleBlockDirection;
            set { _newRuleBlockDirection = value; OnPropertyChanged(); }
        }



        private string _newRuleTimeStart = "";
        public string NewRuleTimeStart { get => _newRuleTimeStart; set { _newRuleTimeStart = value; OnPropertyChanged(); } }

        private string _newRuleTimeEnd = "";
        public string NewRuleTimeEnd { get => _newRuleTimeEnd; set { _newRuleTimeEnd = value; OnPropertyChanged(); } }

        private ProxyItem? _newRuleSelectedProxy;
        public ProxyItem? NewRuleSelectedProxy
        {
            get => _newRuleSelectedProxy;
            set { _newRuleSelectedProxy = value; OnPropertyChanged(); }
        }
        public bool IsNewRuleProxyRequired => NewRuleAction == RuleAction.Proxy;

        private bool _isModalVisible;
        public bool IsModalVisible
        {
            get => _isModalVisible;
            set { _isModalVisible = value; OnPropertyChanged(); }
        }

        private string _modalProcessName;
        public string ModalProcessName
        {
            get => _modalProcessName;
            set { _modalProcessName = value; OnPropertyChanged(); }
        }

        private string _modalProcessSearch = "";
        public string ModalProcessSearch
        {
            get => _modalProcessSearch;
            set
            {
                _modalProcessSearch = value ?? "";
                OnPropertyChanged();
                ActiveProcessesView?.Refresh();
            }
        }

        private ActiveProcessInfo? _modalSelectedActiveProcess;
        public ActiveProcessInfo? ModalSelectedActiveProcess
        {
            get => _modalSelectedActiveProcess;
            set
            {
                _modalSelectedActiveProcess = value;
                OnPropertyChanged();
                if (value != null)
                {
                    ModalProcessName = Path.GetFileNameWithoutExtension(value.ProcessName);
                    _modalIcon = IconHelper.GetIconByProcessName(value.ProcessName);
                }
            }
        }

        private bool _modalIsTemporary;
        public bool ModalIsTemporary
        {
            get => _modalIsTemporary;
            set { _modalIsTemporary = value; OnPropertyChanged(); }
        }

        private string _modalHost;
        public string ModalHost
        {
            get => _modalHost;
            set { _modalHost = value; OnPropertyChanged(); }
        }

        private bool _modalIsScheduleEnabled;
        public bool ModalIsScheduleEnabled
        {
            get => _modalIsScheduleEnabled;
            set { _modalIsScheduleEnabled = value; OnPropertyChanged(); }
        }



        private string _modalTimeStart = "";
        public string ModalTimeStart { get => _modalTimeStart; set { _modalTimeStart = value; OnPropertyChanged(); } }

        private string _modalTimeEnd = "";
        public string ModalTimeEnd { get => _modalTimeEnd; set { _modalTimeEnd = value; OnPropertyChanged(); } }

        private RuleAction _modalAction = RuleAction.Proxy;
        public RuleAction ModalAction
        {
            get => _modalAction;
            set { _modalAction = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsModalProxyRequired)); }
        }
        public bool IsModalProxyRequired => ModalAction == RuleAction.Proxy;

        private BlockDirection _modalBlockDirection = BlockDirection.Both;
        public BlockDirection ModalBlockDirection
        {
            get => _modalBlockDirection;
            set { _modalBlockDirection = value; OnPropertyChanged(); }
        }

        private ProxyItem? _modalSelectedProxy;
        public ProxyItem? ModalSelectedProxy
        {
            get => _modalSelectedProxy;
            set { _modalSelectedProxy = value; OnPropertyChanged(); }
        }

        private RuleMode _modalTargetMode;
        public RuleMode ModalTargetMode
        {
            get => _modalTargetMode;
            set { _modalTargetMode = value; OnPropertyChanged(); }
        }

        private RuleTrafficType _modalTrafficType = RuleTrafficType.Any;
        public RuleTrafficType ModalTrafficType
        {
            get => _modalTrafficType;
            set { _modalTrafficType = value; OnPropertyChanged(); }
        }

        public IEnumerable<RuleTrafficType> TrafficTypes => Enum.GetValues(typeof(RuleTrafficType)).Cast<RuleTrafficType>();

        private string _modalGroupName = "QuickRules";
        public string ModalGroupName
        {
            get => _modalGroupName;
            set
            {
                if (_modalGroupName != value)
                {
                    _modalGroupName = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isDeleteModalVisible;
        public bool IsDeleteModalVisible
        {
            get => _isDeleteModalVisible;
            set { _isDeleteModalVisible = value; OnPropertyChanged(); }
        }

        public bool IsDeleteAppEnabled => !string.IsNullOrEmpty(SelectedAppName);
        public bool IsDeleteGroupEnabled => !string.IsNullOrEmpty(SelectedGroupName);

        private TrafficRule? _editingRule;
        public bool IsEditMode => _editingRule != null;
        private string? _modalTitle;
        public string ModalTitle
        {
            get => _modalTitle ?? (IsEditMode ? "✏️ Edit Rule" : "✨ New Rule");
            set { _modalTitle = value; OnPropertyChanged(); }
        }

        private string? _modalSubtitle;
        public string ModalSubtitle
        {
            get => _modalSubtitle ?? (IsEditMode ? "Modify an existing traffic rule" : "Create a routing rule for an application");
            set { _modalSubtitle = value; OnPropertyChanged(); }
        }

        public IEnumerable<string> ExistingGroups => RulesList
            .Select(r => r.GroupName ?? "General")
            .Distinct()
            .OrderBy(g => g);

        private System.Windows.Media.ImageSource? _modalIcon;

        private bool _isProxyModalVisible;
        public bool IsProxyModalVisible
        {
            get => _isProxyModalVisible;
            set { _isProxyModalVisible = value; OnPropertyChanged(); }
        }

        private string _proxyModalTitle = "Add Proxy";
        public string ProxyModalTitle
        {
            get => _proxyModalTitle;
            set { _proxyModalTitle = value; OnPropertyChanged(); }
        }

        private string _proxyModalIp = "";
        public string ProxyModalIp { get => _proxyModalIp; set { _proxyModalIp = value; OnPropertyChanged(); } }

        private string _proxyModalName = "";
        public string ProxyModalName { get => _proxyModalName; set { _proxyModalName = value; OnPropertyChanged(); } }

        private int _proxyModalPort = 8080;
        public int ProxyModalPort { get => _proxyModalPort; set { _proxyModalPort = value; OnPropertyChanged(); } }

        private string? _proxyModalUser;
        public string? ProxyModalUser { get => _proxyModalUser; set { _proxyModalUser = value; OnPropertyChanged(); } }

        private string? _proxyModalPass;
        public string? ProxyModalPass { get => _proxyModalPass; set { _proxyModalPass = value; OnPropertyChanged(); } }

        private bool _proxyModalUseTls = false;
        public bool ProxyModalUseTls
        {
            get => _proxyModalUseTls;
            set
            {
                _proxyModalUseTls = value;
                if (_proxyModalUseTls) ProxyModalUseSsl = false;
                OnPropertyChanged();
            }
        }

        private bool _proxyModalUseSsl = false;
        public bool ProxyModalUseSsl
        {
            get => _proxyModalUseSsl;
            set
            {
                _proxyModalUseSsl = value;
                if (_proxyModalUseSsl) ProxyModalUseTls = false;
                OnPropertyChanged();
            }
        }

        private ProxyType _proxyModalType = ProxyType.Http;
        public ProxyType ProxyModalType
        {
            get => _proxyModalType;
            set { _proxyModalType = value; OnPropertyChanged(); }
        }

        private ProxyItem? _editingProxyItem;

        private string _searchText = "";
        private CancellationTokenSource? _searchDebounceCts;

        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                OnPropertyChanged();

                _searchDebounceCts?.Cancel();
                _searchDebounceCts = new CancellationTokenSource();
                var token = _searchDebounceCts.Token;

                Task.Delay(300, token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        RulesView?.Refresh();
                        RefreshRuleGroups();
                    });
                });
            }
        }

        // --- DNS Logic ---
        public Array DnsProviders => Enum.GetValues(typeof(DnsProviderType));

        public DnsProviderType SelectedDnsProvider
        {
            get => _config.DnsProvider;
            set
            {
                if (_config.DnsProvider != value)
                {
                    _config.DnsProvider = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsDnsCustom));

                    switch (value)
                    {
                        case DnsProviderType.Google:
                            DnsHost = "8.8.8.8";
                            DnsFallbackHost = "8.8.4.4";
                            break;
                        case DnsProviderType.Cloudflare:
                            DnsHost = "1.1.1.1";
                            DnsFallbackHost = "1.0.0.1";
                            break;
                        case DnsProviderType.OpenDNS:
                            DnsHost = "208.67.222.222";
                            DnsFallbackHost = "208.67.220.220";
                            break;
                        case DnsProviderType.Custom: break;
                    }
                    MarkDohSettingsChanged();
                }
            }
        }

        public bool IsDnsCustom => SelectedDnsProvider == DnsProviderType.Custom;

        public string DnsHost
        {
            get => _config.DnsHost;
            set
            {
                if (_config.DnsHost != value)
                {
                    _config.DnsHost = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DohEndpointInput));
                    OnPropertyChanged(nameof(DohEndpointValidationMessage));
                    PrimaryDnsStatus = "Unknown";
                    PrimaryDnsStatusDetails = "Not checked";
                    QueueDohTransportStatusRefresh();
                    MarkDohSettingsChanged();
                }
            }
        }

        public string DnsFallbackHost
        {
            get => _config.DnsFallbackHost;
            set
            {
                if (_config.DnsFallbackHost != value)
                {
                    _config.DnsFallbackHost = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DohFallbackEndpointInput));
                    OnPropertyChanged(nameof(DohFallbackEndpointValidationMessage));
                    FallbackDnsStatus = "Unknown";
                    FallbackDnsStatusDetails = "Not checked";
                    QueueDohTransportStatusRefresh();
                    MarkDohSettingsChanged();
                }
            }
        }

        public bool PreferPrimaryDns
        {
            get => _config.PreferPrimaryDns;
            set
            {
                if (_config.PreferPrimaryDns != value)
                {
                    _config.PreferPrimaryDns = value;
                    OnPropertyChanged();
                    MarkDohSettingsChanged();
                }
            }
        }

        public bool IsDohEnabled
        {
            get => _config.EnableDoh;
            set
            {
                if (_config.EnableDoh != value)
                {
                    _config.EnableDoh = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsDohFallbackEnabled));
                    OnPropertyChanged(nameof(DohEndpointValidationMessage));
                    OnPropertyChanged(nameof(DohFallbackEndpointValidationMessage));
                    QueueDohTransportStatusRefresh();
                    MarkDohSettingsChanged();
                }
            }
        }

        public bool IsDohFallbackEnabled
        {
            get => _config.EnableDohFallback;
            set
            {
                if (_config.EnableDohFallback != value)
                {
                    _config.EnableDohFallback = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DohFallbackEndpointValidationMessage));
                    QueueDohTransportStatusRefresh();
                    MarkDohSettingsChanged();
                }
            }
        }

        public bool IsDohPrimaryAuto
        {
            get => _config.AutoDetectDohEndpoint;
            set
            {
                if (_config.AutoDetectDohEndpoint != value)
                {
                    _config.AutoDetectDohEndpoint = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DohEndpointInput));
                    OnPropertyChanged(nameof(DohEndpointValidationMessage));
                    QueueDohTransportStatusRefresh();
                    MarkDohSettingsChanged();
                }
            }
        }

        public string DohEndpoint
        {
            get => _config.DohEndpoint;
            set
            {
                if (_config.DohEndpoint != value)
                {
                    _config.DohEndpoint = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DohEndpointInput));
                    OnPropertyChanged(nameof(DohEndpointValidationMessage));
                    QueueDohTransportStatusRefresh();
                    MarkDohSettingsChanged();
                }
            }
        }

        public string DohEndpointInput
        {
            get
            {
                if (IsDohPrimaryAuto)
                {
                    return "Windows automatic template";
                }

                return DohEndpoint;
            }
            set
            {
                if (!IsDohPrimaryAuto)
                {
                    DohEndpoint = value;
                }
            }
        }

        public bool IsDohFallbackAuto
        {
            get => _config.AutoDetectDohFallbackEndpoint;
            set
            {
                if (_config.AutoDetectDohFallbackEndpoint != value)
                {
                    _config.AutoDetectDohFallbackEndpoint = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DohFallbackEndpointInput));
                    OnPropertyChanged(nameof(DohFallbackEndpointValidationMessage));
                    QueueDohTransportStatusRefresh();
                    MarkDohSettingsChanged();
                }
            }
        }

        public string DohFallbackEndpoint
        {
            get => _config.DohFallbackEndpoint;
            set
            {
                if (_config.DohFallbackEndpoint != value)
                {
                    _config.DohFallbackEndpoint = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DohFallbackEndpointInput));
                    OnPropertyChanged(nameof(DohFallbackEndpointValidationMessage));
                    QueueDohTransportStatusRefresh();
                    MarkDohSettingsChanged();
                }
            }
        }

        public string DohFallbackEndpointInput
        {
            get
            {
                if (IsDohFallbackAuto)
                {
                    return "Windows automatic template";
                }

                return DohFallbackEndpoint;
            }
            set
            {
                if (!IsDohFallbackAuto)
                {
                    DohFallbackEndpoint = value;
                }
            }
        }

        public string DohEndpointValidationMessage
        {
            get
            {
                if (!IsDohEnabled) return string.Empty;
                if (IsDohPrimaryAuto) return string.Empty;

                var valid = DnsOverHttpsClient.TryNormalizeEndpoint(DohEndpoint, out _, out var error);
                return valid
                    ? string.Empty
                    : $"Primary DoH: {error}";
            }
        }

        public string DohFallbackEndpointValidationMessage
        {
            get
            {
                if (!IsDohEnabled) return string.Empty;
                if (!IsDohFallbackEnabled) return string.Empty;
                if (IsDohFallbackAuto) return string.Empty;

                var valid = DnsOverHttpsClient.TryNormalizeEndpoint(DohFallbackEndpoint, out _, out var error);
                return valid
                    ? string.Empty
                    : $"Alternative DoH: {error}";
            }
        }

        public string DohTransportStatus
        {
            get => _dohTransportStatus;
        }

        private void QueueDohTransportStatusRefresh(int delayMs = 250)
        {
            _dohStatusRefreshCts?.Cancel();

            if (!IsDohEnabled)
            {
                SetDohTransportStatus(string.Empty);
                return;
            }

            var cts = new CancellationTokenSource();
            _dohStatusRefreshCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, cts.Token);
                    var status = BuildDohTransportStatus();
                    if (cts.Token.IsCancellationRequested) return;

                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        dispatcher.Invoke(() => SetDohTransportStatus(status));
                    }
                    else
                    {
                        SetDohTransportStatus(status);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        private void SetDohTransportStatus(string value)
        {
            if (_dohTransportStatus == value) return;
            _dohTransportStatus = value;
            OnPropertyChanged(nameof(DohTransportStatus));
        }

        private string BuildDohTransportStatus()
        {
            if (!IsDohEnabled) return string.Empty;

            if (!DnsOverHttpsClient.TryGetEndpoint(_config, out var primary, out _))
            {
                return "DoH endpoint was not detected. Set the endpoint manually.";
            }

            var status = $"Encrypted via {primary}";
            if (IsDohFallbackEnabled && DnsOverHttpsClient.TryGetFallbackEndpoint(_config, out var fallback, out _))
            {
                status += $"; fallback: {fallback}";
            }

            return status;
        }

        private bool _isProxyRunning = false;
        public bool IsProxyRunning
        {
            get => _isProxyRunning;
            set
            {
                if (_isProxyRunning != value)
                {
                    _isProxyRunning = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsProxyRunningToggle));
                    OnPropertyChanged(nameof(ToggleProxyMenuText));
                    OnPropertyChanged(nameof(ProxyStatusText));
                    OnPropertyChanged(nameof(ProxyStatusColor));
                    RequestSaveSettings();
                }
            }
        }

        public bool IsProxyRunningToggle
        {
            get => IsProxyRunning;
            set
            {
                if (IsProxyRunning != value)
                {
                    ToggleService();
                    OnPropertyChanged();
                }
            }
        }

        public string ProxyStatusText => IsProxyRunning ? "Active" : "Inactive";
        public string ProxyStatusColor => IsProxyRunning ? "#10B981" : "#6B7280";

        private bool _isAutoStart;
        public bool IsAutoStart
        {
            get => _isAutoStart;
            set
            {
                _isAutoStart = value;
                if (!_suppressSave)
                    _settingsService.SetAutoStart(value);
                RequestSaveSettings();
                OnPropertyChanged();
            }
        }

        private bool _checkUpdateOnStartup = true;
        public bool CheckUpdateOnStartup
        {
            get => _checkUpdateOnStartup;
            set
            {
                _checkUpdateOnStartup = value;
                RequestSaveSettings();
                OnPropertyChanged();
            }
        }

        private ProxyItem? _selectedProxy;
        public ProxyItem? SelectedProxy
        {
            get => _selectedProxy;
            set
            {
                _selectedProxy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedProxy));
                OnPropertyChanged(nameof(CanCheckSelectedProxy));
                ProxyCheckStatus = "Ready";
                ProxyCheckSummary = value == null ? "Select a proxy to test" : value.Name;
                ProxyCheckDetails = value == null ? "The test result will appear here." : value.Endpoint;
            }
        }

        public bool HasSelectedProxy => SelectedProxy != null;

        private bool _isProxyPanelExpanded = true;
        public bool IsProxyPanelExpanded
        {
            get => _isProxyPanelExpanded;
            set
            {
                if (_isProxyPanelExpanded == value) return;
                _isProxyPanelExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProxyPanelWidth));
                OnPropertyChanged(nameof(ProxyPanelToggleGlyph));
            }
        }

        public double ProxyPanelWidth => IsProxyPanelExpanded ? 300 : 76;
        public string ProxyPanelToggleGlyph => IsProxyPanelExpanded ? "◀" : "▶";

        private bool _isProxyCheckInProgress;
        public bool IsProxyCheckInProgress
        {
            get => _isProxyCheckInProgress;
            private set
            {
                _isProxyCheckInProgress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanCheckSelectedProxy));
                OnPropertyChanged(nameof(ProxyCheckButtonText));
            }
        }

        public bool CanCheckSelectedProxy => SelectedProxy != null && !IsProxyCheckInProgress;
        public string ProxyCheckButtonText => IsProxyCheckInProgress ? "Checking…" : "Check Proxy";

        private string _proxyCheckStatus = "Ready";
        public string ProxyCheckStatus
        {
            get => _proxyCheckStatus;
            private set { _proxyCheckStatus = value; OnPropertyChanged(); }
        }

        private string _proxyCheckSummary = "Select a proxy to test";
        public string ProxyCheckSummary
        {
            get => _proxyCheckSummary;
            private set { _proxyCheckSummary = value; OnPropertyChanged(); }
        }

        private string _proxyCheckDetails = "The test result will appear here.";
        public string ProxyCheckDetails
        {
            get => _proxyCheckDetails;
            private set { _proxyCheckDetails = value; OnPropertyChanged(); }
        }

        public bool IsBlackListMode
        {
            get => _config.CurrentMode == RuleMode.BlackList;
            set
            {
                _config.CurrentMode = value ? RuleMode.BlackList : RuleMode.WhiteList;
                ReloadRulesForCurrentMode();
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEnableTunMode)); // Notify dependency
                ApplyConfig();
                RequestSaveSettings();

                RefreshTunIfRunning();
            }
        }

        public bool IsDnsProtectionEnabled
        {
            get => _config.EnableDnsProtection;
            set
            {
                if (value && !SystemProxyHelper.IsAdministrator())
                {
                    var result = MessageBox.Show(
                        "DNS Leak Protection requires Administrator privileges to modify system DNS settings.\n\nRestart application as Administrator?",
                        "Admin Rights Required",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        _config.EnableDnsProtection = true;
                        SaveSettingsNow();
                        SystemProxyHelper.RestartAsAdmin();
                        Application.Current.Shutdown();
                        return;
                    }
                    else
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            OnPropertyChanged(nameof(IsDnsProtectionEnabled));
                        }));
                        return;
                    }
                }

                _config.EnableDnsProtection = value;
                OnPropertyChanged();
                UpdateDnsServiceState();
                MarkDohSettingsChanged();
            }
        }

        public bool IsWebRtcBlockingEnabled
        {
            get => _config.IsWebRtcBlockingEnabled;
            set
            {
                _config.IsWebRtcBlockingEnabled = value;
                OnPropertyChanged();
                ApplyConfig();
                RequestSaveSettings();
            }
        }

        public Array ModeTypes => Enum.GetValues(typeof(RuleMode));
        public Array TrafficPeriodModes => Enum.GetValues(typeof(TrafficPeriodMode));
        public IReadOnlyList<ProxyType> ProxyTypes { get; } =
            Enum.GetValues(typeof(ProxyType))
                .Cast<ProxyType>()
                .Where(t => t != ProxyType.Socks4)
                .ToArray();

        public ProxyItem? SelectedBlackListMainProxy
        {
            get => Proxies.FirstOrDefault(p => p.Id == _config.BlackListSelectedProxyId.ToString());
            set
            {
                if (value == null && _config.BlackListSelectedProxyId == null) return;
                _config.BlackListSelectedProxyId = new Guid(value?.Id);
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEnableTunMode)); // Notify dependency
                ReloadRulesForCurrentMode();
                ApplyConfig();
                RequestSaveSettings();
            }
        }

        private TrafficRule? _selectedRule;

        private RulePreset? _selectedRulePreset;
        public RulePreset? SelectedRulePreset
        {
            get => _selectedRulePreset;
            set { _selectedRulePreset = value; OnPropertyChanged(); }
        }

        private string _newPresetName = "";
        public string NewPresetName
        {
            get => _newPresetName;
            set { _newPresetName = value; OnPropertyChanged(); }
        }

        public TrafficRule? SelectedRule
        {
            get => _selectedRule;
            set
            {
                if (ReferenceEquals(_selectedRule, value)) return;
                _selectedRule = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedRule));
            }
        }
        public bool HasSelectedRule => SelectedRule != null;

        private ConnectionLog? _selectedLogItem;
        public ConnectionLog? SelectedLogItem
        {
            get => _selectedLogItem;
            set { _selectedLogItem = value; OnPropertyChanged(); }
        }

        public ICommand OpenAddProxyModalCommand { get; }
        public ICommand OpenEditProxyModalCommand { get; }
        public ICommand SaveProxyModalCommand { get; }
        public ICommand CloseProxyModalCommand { get; }
        public ICommand PasteProxyCommand { get; }
        public ICommand RemoveProxyCommand { get; }
        public ICommand SaveChangesCommand { get; }
        public ICommand CheckDnsCommand { get; }
        public ICommand CheckProxyCommand { get; }
        public ICommand CheckAllProxiesCommand { get; }
        public ICommand SetProxyCheckPresetCommand { get; }
        public ICommand SetProxySpeedPresetCommand { get; }

        // Rules ContextMenu Commands
        public ICommand DuplicateRuleCommand { get; }
        public ICommand ToggleRuleEnabledCommand { get; }
        public ICommand SetRuleActionCommand { get; }
        public ICommand CopyRuleHostsCommand { get; }
        public ICommand CopyRuleAppsCommand { get; }

        // Proxies ContextMenu Commands
        public ICommand SelectProxyCommand { get; }
        public ICommand CheckSpecificProxyCommand { get; }
        public ICommand SetAsMainProxyCommand { get; }
        public ICommand SetAsTunProxyCommand { get; }
        public ICommand CopyProxyEndpointCommand { get; }
        public ICommand CopyProxyIpCommand { get; }
        public ICommand CopyProxyFullCommand { get; }
        public ICommand ToggleProxyEnabledCommand { get; }
        public ICommand RemoveSpecificProxyCommand { get; }
        public ICommand ToggleProxyPanelCommand { get; }
        public ICommand AddRuleCommand { get; }
        public ICommand RemoveRuleCommand { get; }
        public ICommand ShowWindowCommand { get; }
        public ICommand ExitAppCommand { get; }
        public ICommand ToggleProxyCommand { get; }
        public ICommand ImportConfigCommand { get; }
        public ICommand ExportConfigCommand { get; }
        public ICommand CheckUpdateCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand ClearLogFiltersCommand { get; }
        public ICommand ClearMonitorFiltersCommand { get; }
        public ICommand OpenRuleModalCommand { get; }
        public ICommand SaveModalRuleCommand { get; }
        public ICommand CloseModalCommand { get; }
        public ICommand BrowseExeCommand { get; }
        public ICommand BrowseShortcutCommand { get; }
        public ICommand ApplyFilterCommand { get; }
        public ICommand SelectGroupCommand { get; }
        public ICommand SelectAppCommand { get; }
        public ICommand BackToGroupsCommand { get; }
        public ICommand BackToAppsCommand { get; }
        public ICommand TraySelectProxyCommand { get; }
        public ICommand TraySetBlackListModeCommand { get; }
        public ICommand TraySetWhiteListModeCommand { get; }
        public ICommand SelectMonitorProcessCommand { get; }
        public ICommand EditRuleCommand { get; }
        public ICommand BrowseAppCommand { get; }
        public ICommand RefreshActiveProcessesCommand { get; }
        public ICommand OpenBulkDeleteModalCommand { get; }
        public ICommand CloseDeleteModalCommand { get; }
        public ICommand DeleteAppRulesCommand { get; }
        public ICommand DeleteGroupRulesCommand { get; }
        public ICommand SelectRuleCommand { get; }


        public MainViewModel()
        {
            _trafficMonitorService = new TrafficMonitorService();
            _trafficMonitorService.ConnectionCreated += OnMonitorConnectionCreated;
            _trafficMonitorService.OverallStatsUpdated += OnOverallStatsUpdated;
            _siteIconCacheService = new SiteIconCacheService();
            _ = Task.Run(() => ProcessConnectionIconQueueAsync(_connectionIconCts.Token));
            _proxyService = new TcpProxyService(_trafficMonitorService);
            _dnsProxyService = new DnsProxyService(_trafficMonitorService);
            _tunService = new TunService();
            _tunService.TrafficObserved += OnTunTrafficObserved;

            _settingsService = new SettingsService();
            _updateService = new GithubUpdateService();
            _config = new AppConfig();

            _connectionLogTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _connectionLogTimer.Tick += FlushPendingConnectionLogs;
            _connectionLogTimer.Start();

            // Initialize version
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                CurrentVersion = $"{version.Major}.{version.Minor}.{version.Build}";

            _proxyService.OnConnectionLog += OnLogReceived;
            _dnsProxyService.OnConnectionLog += OnLogReceived;

            Proxies.CollectionChanged += OnCollectionChanged;
            RulesList.CollectionChanged += OnCollectionChanged;

            RulesView = CollectionViewSource.GetDefaultView(RulesList);
            // Single-level grouping only (removed nested GroupName to fix lag)
            RulesView.GroupDescriptions.Add(new PropertyGroupDescription("AppKey"));
            RulesView.Filter = FilterRules;

            ActiveProcessesView = CollectionViewSource.GetDefaultView(ActiveProcesses);
            ActiveProcessesView.Filter = FilterActiveProcess;

            LogsView = CollectionViewSource.GetDefaultView(Logs);
            LogsView.Filter = FilterConnectionLog;
            MonitorConnectionsView = CollectionViewSource.GetDefaultView(_emptyMonitorConnections);
            MonitorConnectionsView.Filter = FilterMonitorConnection;

            NavigateCommand = new RelayCommand(view =>
            {
                if (view is string v) CurrentView = v;
            });



            OpenAddProxyModalCommand = new RelayCommand(_ => OpenProxyModal(null));
            OpenEditProxyModalCommand = new RelayCommand(p => OpenProxyModal(p as ProxyItem));
            SaveProxyModalCommand = new RelayCommand(_ => SaveProxyFromModal());
            CloseProxyModalCommand = new RelayCommand(_ => IsProxyModalVisible = false);

            PasteProxyCommand = new RelayCommand(_ => PasteProxy());
            RemoveProxyCommand = new RelayCommand(_ => RemoveProxy());
            SaveChangesCommand = new RelayCommand(async _ => await SaveDohSettingsNowAsync());
            CheckDnsCommand = new RelayCommand(async _ => await CheckDnsServersAsync());
            CheckProxyCommand = new RelayCommand(_ => CheckSelectedProxy());
            CheckAllProxiesCommand = new RelayCommand(async _ => await CheckAllProxies());
            SetProxyCheckPresetCommand = new RelayCommand(url => { if (url != null) ProxyCheckUrl = url.ToString()!; });
            SetProxySpeedPresetCommand = new RelayCommand(url => { if (url != null) ProxySpeedTestUrl = url.ToString()!; });

            // Rules ContextMenu Commands
            DuplicateRuleCommand = new RelayCommand(r => DuplicateRule(r as TrafficRule ?? SelectedRule));
            ToggleRuleEnabledCommand = new RelayCommand(r => ToggleRuleEnabled(r as TrafficRule ?? SelectedRule));
            SetRuleActionCommand = new RelayCommand(p => SetRuleAction(p));
            CopyRuleHostsCommand = new RelayCommand(r => CopyRuleHosts(r as TrafficRule ?? SelectedRule));
            CopyRuleAppsCommand = new RelayCommand(r => CopyRuleApps(r as TrafficRule ?? SelectedRule));

            // Proxies ContextMenu Commands
            SelectProxyCommand = new RelayCommand(p => { if (p is ProxyItem proxy) SelectedProxy = proxy; });
            CheckSpecificProxyCommand = new RelayCommand(p => _ = CheckSpecificProxy(p as ProxyItem ?? SelectedProxy));
            SetAsMainProxyCommand = new RelayCommand(p => SetAsMainProxy(p as ProxyItem ?? SelectedProxy));
            SetAsTunProxyCommand = new RelayCommand(p => SetAsTunProxy(p as ProxyItem ?? SelectedProxy));
            CopyProxyEndpointCommand = new RelayCommand(p => CopyProxyEndpoint(p as ProxyItem ?? SelectedProxy));
            CopyProxyIpCommand = new RelayCommand(p => CopyProxyIp(p as ProxyItem ?? SelectedProxy));
            CopyProxyFullCommand = new RelayCommand(p => CopyProxyFull(p as ProxyItem ?? SelectedProxy));
            ToggleProxyEnabledCommand = new RelayCommand(p => ToggleProxyEnabled(p as ProxyItem ?? SelectedProxy));
            RemoveSpecificProxyCommand = new RelayCommand(p => RemoveSpecificProxy(p as ProxyItem ?? SelectedProxy));
            ToggleProxyPanelCommand = new RelayCommand(_ => IsProxyPanelExpanded = !IsProxyPanelExpanded);
            SaveProfileCommand = new RelayCommand(_ => SaveProfile());
            LoadProfileCommand = new RelayCommand(_ => LoadProfile());
            DeleteProfileCommand = new RelayCommand(_ => DeleteProfile());
            CreateEmptyProfileCommand = new RelayCommand(_ => CreateEmptyProfile());
            AddRuleCommand = new RelayCommand(_ => AddRule());
            RemoveRuleCommand = new RelayCommand(rule => RemoveRule(rule as TrafficRule));

            OpenBulkDeleteModalCommand = new RelayCommand(_ =>
            {
                OnPropertyChanged(nameof(IsDeleteAppEnabled));
                OnPropertyChanged(nameof(IsDeleteGroupEnabled));
                OnPropertyChanged(nameof(SelectedAppName));
                OnPropertyChanged(nameof(SelectedGroupName));
                IsDeleteModalVisible = true;
            });
            CloseDeleteModalCommand = new RelayCommand(_ => IsDeleteModalVisible = false);
            DeleteAppRulesCommand = new RelayCommand(_ => DeleteRules(true));

            SavePresetCommand = new RelayCommand(_ => SavePreset());
            LoadPresetCommand = new RelayCommand(_ => LoadPreset());
            DeletePresetCommand = new RelayCommand(p => DeletePreset(p as RulePreset));

            OpenRuleModalCommand = new RelayCommand(rule => OpenRuleModal(rule as TrafficRule));
            SaveModalRuleCommand = new RelayCommand(_ => SaveRuleFromModal());
            CloseModalCommand = new RelayCommand(_ => IsModalVisible = false);

            EditRuleCommand = new RelayCommand(rule => OpenRuleModal(rule as TrafficRule));
            SelectRuleCommand = new RelayCommand(rule => { SelectedRule = rule as TrafficRule; });
            DeleteGroupRulesCommand = new RelayCommand(_ => DeleteRules(false));

            SelectGroupCommand = new RelayCommand(groupName =>
            {
                try
                {
                    SelectedAppName = null; // Reset app selection when group changes
                    SelectedGroupName = groupName as string;
                }
                catch (Exception ex) { AppLoggerService.Instance.Error("Groups", $"SelectGroupCommand error: {ex.Message}"); }
            });
            SelectAppCommand = new RelayCommand(appName =>
            {
                try { SelectedAppName = appName as string; }
                catch (Exception ex) { AppLoggerService.Instance.Error("Groups", $"SelectAppCommand error: {ex.Message}"); }
            });
            BackToGroupsCommand = new RelayCommand(_ =>
            {
                SelectedAppName = null;
                SelectedGroupName = null;
            });
            BackToAppsCommand = new RelayCommand(_ => SelectedAppName = null);
            ToggleGroupRulesCommand = new RelayCommand(ToggleGroupRules);
            ToggleAppRulesCommand = new RelayCommand(ToggleAppRules);

            ShowWindowCommand = new RelayCommand(_ =>
            {
                var win = Application.Current.MainWindow;
                if (win != null)
                {
                    win.Show();
                    if (win.WindowState == WindowState.Minimized)
                        win.WindowState = WindowState.Normal;
                    win.Activate();
                }
            });

            ExitAppCommand = new RelayCommand(_ =>
            {
                _proxyService.Stop();
                _dnsProxyService.Stop();
                if (IsTunMode) _tunService.Stop();
                SystemProxyHelper.RestoreSystemDnsIfManagedByProxyControl();
                MainWindow.AllowClose = true;
                Application.Current.Shutdown();
            });

            ToggleProxyCommand = new RelayCommand(_ => ToggleService());
            ImportConfigCommand = new RelayCommand(_ => ImportConfig());
            ExportConfigCommand = new RelayCommand(_ => ExportConfig());

            CheckUpdateCommand = new RelayCommand(async _ => await PerformUpdateCheck(silent: false));
            ClearLogsCommand = new RelayCommand(_ => Logs.Clear());
            ClearLogFiltersCommand = new RelayCommand(_ => ClearLogFilters());
            ClearMonitorFiltersCommand = new RelayCommand(_ => ClearMonitorFilters());

            OpenRuleModalCommand = new RelayCommand(obj => OpenRuleModal(obj));
            CloseModalCommand = new RelayCommand(_ => IsModalVisible = false);
            SaveModalRuleCommand = new RelayCommand(_ => SaveRuleFromModal());
            BrowseExeCommand = new RelayCommand(_ => BrowseExeFile());
            BrowseShortcutCommand = new RelayCommand(_ => BrowseShortcutFile());

            ApplyFilterCommand = new RelayCommand(_ => _ = ApplyMonitorPeriodAsync());
            TraySelectProxyCommand = new RelayCommand(p => SelectedBlackListMainProxy = (ProxyItem)p);
            TraySetBlackListModeCommand = new RelayCommand(_ => IsBlackListMode = true);
            TraySetWhiteListModeCommand = new RelayCommand(_ => IsBlackListMode = false);
            SelectMonitorProcessCommand = new RelayCommand(p => SelectedMonitorProcess = p as ProcessTrafficData);
            EditRuleCommand = new RelayCommand(r => OpenRuleModal(r));
            BrowseAppCommand = new RelayCommand(_ => BrowseAppFile());
            RefreshActiveProcessesCommand = new RelayCommand(_ => RefreshActiveProcesses());

            // --- NEW MANAGEMENT COMMANDS ---
            EditGroupCommand = new RelayCommand(grp => OpenBatchEditModal("Group", grp as string ?? (grp as RuleGroupInfo)?.GroupName ?? ""));
            RemoveGroupCommand = new RelayCommand(grp => RequestConfirmDelete("Group", grp as string ?? (grp as RuleGroupInfo)?.GroupName ?? ""));
            EditAppCommand = new RelayCommand(app => OpenBatchEditModal("App", app as string ?? (app as AppRuleInfo)?.AppName ?? ""));
            RemoveAppCommand = new RelayCommand(app => RequestConfirmDelete("App", app as string ?? (app as AppRuleInfo)?.AppName ?? ""));
            AddRuleToGroupCommand = new RelayCommand(grp => OpenRuleModal(grp));
            AddRuleToAppCommand = new RelayCommand(app => OpenRuleModal(app));
            CopyTextCommand = new RelayCommand(obj =>
            {
                string? text = obj as string;
                if (obj is RuleGroupInfo rgi) text = rgi.GroupName;
                else if (obj is AppRuleInfo ari) text = ari.AppName;
                else if (obj is TrafficRule tr) text = tr.TargetApps?.FirstOrDefault() ?? tr.TargetHosts?.FirstOrDefault() ?? tr.GroupName;
                if (!string.IsNullOrEmpty(text))
                {
                    try { Clipboard.SetText(text); } catch { }
                }
            });
            SelectRuleCommand = new RelayCommand(r => SelectedRule = r as TrafficRule);

            SavePresetCommand = new RelayCommand(_ => SavePreset());
            LoadPresetCommand = new RelayCommand(_ => LoadPreset());
            DeletePresetCommand = new RelayCommand(p => DeletePreset(p as RulePreset));

            OpenUpdateModalCommand = new RelayCommand(async _ =>
            {
                // Restore Window
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var win = Application.Current.MainWindow;
                    if (win != null)
                    {
                        win.Show();
                        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                        win.Topmost = true;  // Briefly force top logic if needed
                        win.Activate();
                        win.Topmost = false;
                    }
                });

                if (!string.IsNullOrEmpty(PendingUpdateUrl))
                {
                    await ExecuteUpdate(PendingUpdateUrl, PendingUpdateSize);
                }
            });


            CloseConfirmModalCommand = new RelayCommand(_ => IsConfirmModalVisible = false);
            ConfirmActionCommand = new RelayCommand(_ =>
            {
                IsConfirmModalVisible = false;
                _pendingConfirmAction?.Invoke();
            });

            ConfirmUpdateCommand = new RelayCommand(async _ =>
            {
                IsUpdateFoundModalVisible = false;
                if (PendingUpdateInfo != null && !string.IsNullOrEmpty(PendingUpdateInfo.DownloadUrl))
                {
                    await ExecuteUpdate(PendingUpdateInfo.DownloadUrl, PendingUpdateInfo.FileSize);
                }
                else if (!string.IsNullOrEmpty(PendingUpdateUrl))
                {
                    await ExecuteUpdate(PendingUpdateUrl, PendingUpdateSize);
                }
            });
            DismissUpdateFoundModalCommand = new RelayCommand(_ => IsUpdateFoundModalVisible = false);

            OpenWhatsNewModalCommand = new RelayCommand(v => OpenWhatsNewModal(v as string ?? CurrentVersion));
            CloseWhatsNewModalCommand = new RelayCommand(_ => IsWhatsNewModalVisible = false);

            // --- SAFELY INITIALIZE ---
            try
            {
                LoadSettings();

                if (_config.IsTunMode)
                {
                    if (!CanEnableTunMode)
                    {
                        _config.IsTunMode = false;
                        _isTunMode = false; // Sync backing field
                        _config.RoutingMode = ProxyRoutingMode.SystemProxy;
                        _config.IsSystemProxyEnabled = true;
                        OnPropertyChanged(nameof(IsTunMode));
                        OnPropertyChanged(nameof(TunModeStatus));
                    }
                }
            }
            catch
            {
                _config = new AppConfig();
            }

            if (IsProxyRunning)
            {
                try
                {
                    _proxyService.Start();
                    UpdateDnsServiceState();

                    if (IsTunMode)
                    {
                        _ = ToggleTunModeAsync();
                    }
                }
                catch (Exception ex)
                {
                    IsProxyRunning = false;
                    System.Diagnostics.Debug.WriteLine($"Proxy Start Failed: {ex.Message}");
                }
            }

            StartEnforcementLoop();

            var startupProxies = Proxies.ToList();
            foreach (var proxy in startupProxies)
            {
                proxy.Status = "Checking...";
                proxy.IsSpeedChecking = true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Let the first frame render with loading indicators before I/O starts.
                    await Task.Delay(250);
                    await CheckAllProxies(startupProxies);
                }
                catch (Exception ex)
                {
                    AppLoggerService.Instance.Error(
                        "ProxyCheck",
                        $"Startup proxy check failed: {ex.Message}");
                }
            });

            Task.Run(async () =>
            {
                await Task.Delay(2000);
                if (CheckUpdateOnStartup)
                {
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await PerformUpdateCheck(silent: true);
                    });
                }
            });
        }

        // ... Остальные методы без изменений (или скопируйте из предыдущего ответа, если нужно полное тело) ...
        // Для краткости я привел только измененный конструктор и свойства, 
        // так как проблема была именно в инициализации.
        // Но чтобы следовать вашей инструкции "полный код", я продублирую остальные методы ниже.

        private async Task ApplyMonitorPeriodAsync()
        {
            _monitorPeriodCts?.Cancel();
            _monitorPeriodCts?.Dispose();
            var requestCts = new CancellationTokenSource();
            _monitorPeriodCts = requestCts;
            var token = requestCts.Token;

            SelectedMonitorProcess = null;
            IsMonitorPeriodLoading = true;
            MonitorPeriodStatus = SelectedPeriodMode == TrafficPeriodMode.LiveSession
                ? "Switching to live traffic..."
                : "Loading connection history...";

            try
            {
                if (SelectedPeriodMode == TrafficPeriodMode.LiveSession)
                {
                    _trafficMonitorService.SwitchToLiveMode();
                    MonitorPeriodStatus = "Live traffic";
                    return;
                }

                DateTime start = DateTime.Today;
                DateTime end = DateTime.Today;
                TimeSpan? timeStart = null;
                TimeSpan? timeEnd = null;

                if (SelectedPeriodMode == TrafficPeriodMode.Yesterday)
                {
                    start = DateTime.Today.AddDays(-1);
                    end = start;
                }
                else if (SelectedPeriodMode == TrafficPeriodMode.CustomRange)
                {
                    start = FilterDateStart.Date;
                    end = FilterDateEnd.Date;
                    if (end < start) (start, end) = (end, start);
                    if (TimeSpan.TryParse(FilterTimeStart, out var ts)) timeStart = ts;
                    if (TimeSpan.TryParse(FilterTimeEnd, out var te)) timeEnd = te;
                }

                await _trafficMonitorService.LoadHistoryAsync(start, end, timeStart, timeEnd, token);
                token.ThrowIfCancellationRequested();
                foreach (var connection in _trafficMonitorService.DisplayedProcessList.SelectMany(p => p.Connections))
                    QueueConnectionSiteIcon(connection);
                MonitorPeriodStatus = SelectedPeriodMode switch
                {
                    TrafficPeriodMode.Today => "Today's history",
                    TrafficPeriodMode.Yesterday => "Yesterday's history",
                    TrafficPeriodMode.CustomRange => $"{start:dd.MM.yyyy} – {end:dd.MM.yyyy}",
                    _ => "Connection history"
                };
            }
            catch (OperationCanceledException)
            {
                // A newer period selection superseded this request.
            }
            catch (Exception ex)
            {
                MonitorPeriodStatus = "History loading failed";
                AppLoggerService.Instance.Error("Monitor", $"Period filter failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_monitorPeriodCts, requestCts))
                {
                    IsMonitorPeriodLoading = false;
                    _monitorPeriodCts = null;
                    requestCts.Dispose();
                }
            }
        }

        public event Action<string, string, long> RequestShowNotification; // Tag, Url, Size

        private async Task PerformUpdateCheck(bool silent)
        {
            _updateService.OnMessage -= ShowMessage;
            _updateService.OnUpdateAvailable -= HandleUpdateAvailable;
            _updateService.OnUpdateAvailableWithInfo -= HandleUpdateAvailableWithInfo;

            _updateService.OnMessage += ShowMessage;
            _updateService.OnUpdateAvailable += HandleUpdateAvailable;
            _updateService.OnUpdateAvailableWithInfo += HandleUpdateAvailableWithInfo;

            await _updateService.CheckAndInstallUpdate(null, null, silent);
        }

        private void HandleUpdateAvailable(string tagName, string url, long size)
        {
            HandleUpdateAvailableWithInfo(new UpdateReleaseInfo
            {
                TagName = tagName,
                Title = $"Proxy Control {tagName}",
                Changelog = "Рекомендуемое обновление для улучшения стабильности и работы функций.",
                DownloadUrl = url,
                FileSize = size
            });
        }

        private void HandleUpdateAvailableWithInfo(UpdateReleaseInfo info)
        {
            PendingUpdateInfo = info;
            PendingUpdateUrl = info.DownloadUrl;
            PendingUpdateSize = info.FileSize;

            UpdateChangelogEntries.Clear();
            if (!string.IsNullOrWhiteSpace(info.Changelog))
            {
                var parsed = ChangelogService.ParseMarkdownChangelog(info.Changelog);
                if (parsed.Count > 0)
                {
                    foreach (var entry in parsed) UpdateChangelogEntries.Add(entry);
                }
                else
                {
                    UpdateChangelogEntries.Add(new ChangelogEntry
                    {
                        Icon = "🚀",
                        Title = info.Title,
                        Description = info.Changelog,
                        Tag = "New",
                        TagColor = "#10B981"
                    });
                }
            }
            else
            {
                UpdateChangelogEntries.Add(new ChangelogEntry
                {
                    Icon = "✨",
                    Title = info.Title,
                    Description = "Рекомендуемое обновление для улучшения стабильности и работы функций.",
                    Tag = "Update",
                    TagColor = "#3B82F6"
                });
            }

            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }

            bool isVisible = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var win = Application.Current.MainWindow;
                if (win != null)
                {
                    isVisible = win.Visibility == Visibility.Visible && win.WindowState != WindowState.Minimized;
                }
            });

            bool useToast = IsAutoStart && !isVisible;
            if (useToast)
            {
                RequestShowNotification?.Invoke(info.TagName, info.DownloadUrl, info.FileSize);
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var win = Application.Current.MainWindow;
                    if (win != null)
                    {
                        win.Show();
                        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                        win.Activate();
                    }
                    IsUpdateFoundModalVisible = true;
                });
            }
        }

        public void OpenWhatsNewModal(string version)
        {
            WhatsNewVersion = string.IsNullOrWhiteSpace(version) ? CurrentVersion : version;
            WhatsNewCategories.Clear();
            var list = ChangelogService.GetReleaseNotes(WhatsNewVersion);
            foreach (var cat in list)
            {
                WhatsNewCategories.Add(cat);
            }
            IsWhatsNewModalVisible = true;
        }

        // Public method to be called from Toast or Modal
        public async Task ExecuteUpdate(string url, long size)
        {
            // Ensure window is shown if started from Toast
            Application.Current.Dispatcher.Invoke(() =>
            {
                var win = Application.Current.MainWindow;
                if (win != null)
                {
                    win.Show();
                    if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                    win.Activate();
                }
                IsUpdateModalVisible = true;
            });

            await _updateService.PerformUpdate(
                   url,
                   size,
                   (status, details, percent) =>
                   {
                       IsUpdateModalVisible = true;
                       UpdateStatusText = status;
                       UpdateDetailText = details;
                       UpdateProgress = percent;
                   },
                   () =>
                   {
                       UpdateStatusText = "Update complete!";
                       UpdateDetailText = "Restarting...";
                       UpdateProgress = 100;
                   });
        }

        private void OpenProxyModal(ProxyItem? item)
        {
            _editingProxyItem = item;
            if (item == null)
            {
                ProxyModalTitle = "Add Proxy"; ProxyModalIp = ""; ProxyModalPort = 8080;
                ProxyModalName = "";
                ProxyModalUser = ""; ProxyModalPass = ""; ProxyModalUseTls = false; ProxyModalUseSsl = false;
                ProxyModalType = ProxyType.Http;
            }
            else
            {
                ProxyModalTitle = "Edit Proxy"; ProxyModalIp = item.IpAddress; ProxyModalPort = item.Port;
                ProxyModalName = item.Name;
                ProxyModalUser = item.Username; ProxyModalPass = item.Password; ProxyModalUseTls = item.UseTls; ProxyModalUseSsl = item.UseSsl;
                ProxyModalType = item.Type == ProxyType.Socks4 ? ProxyType.Socks5 : item.Type;
            }
            IsProxyModalVisible = true;
        }

        private async void SaveProxyFromModal()
        {
            if (_editingProxyItem == null)
            {
                var newProxy = new ProxyItem { IpAddress = ProxyModalIp, Port = ProxyModalPort, Username = ProxyModalUser, Password = ProxyModalPass, UseTls = ProxyModalUseTls, UseSsl = ProxyModalUseSsl, IsEnabled = true, Status = "New", Type = ProxyModalType };
                newProxy.Name = string.IsNullOrWhiteSpace(ProxyModalName) ? newProxy.Id : ProxyModalName.Trim();
                if (Proxies.Any(p => p.Name.Equals(newProxy.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show("Proxy names must be unique.", "Duplicate Proxy Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Proxies.Add(newProxy); SelectedProxy = newProxy; _ = CheckSingleProxy(newProxy);
            }
            else
            {
                string newName = string.IsNullOrWhiteSpace(ProxyModalName) ? _editingProxyItem.Id : ProxyModalName.Trim();
                if (Proxies.Any(p => !ReferenceEquals(p, _editingProxyItem) && p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show("Proxy names must be unique.", "Duplicate Proxy Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _editingProxyItem.Name = newName;
                _editingProxyItem.IpAddress = ProxyModalIp; _editingProxyItem.Port = ProxyModalPort; _editingProxyItem.Username = ProxyModalUser;
                _editingProxyItem.Password = ProxyModalPass; _editingProxyItem.UseTls = ProxyModalUseTls; _editingProxyItem.UseSsl = ProxyModalUseSsl;
                _editingProxyItem.Type = ProxyModalType;
                _editingProxyItem.Status = "Updated"; _ = CheckSingleProxy(_editingProxyItem);
                if (ReferenceEquals(SelectedProxy, _editingProxyItem))
                {
                    ProxyCheckStatus = "Ready";
                    ProxyCheckSummary = _editingProxyItem.Name;
                    ProxyCheckDetails = _editingProxyItem.Endpoint;
                }
            }
            IsProxyModalVisible = false;
            _saveDebounceCts?.Cancel();
            if (!await SaveSettingsCoreAsync())
            {
                MessageBox.Show("The proxy name could not be written to the settings file.", "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenRuleModal(object? obj)
        {
            _isBatchEditMode = false;
            _editingRule = null;
            _modalTitle = null;
            _modalSubtitle = null;

            TrafficRule? rule = obj as TrafficRule;
            ConnectionLog? log = obj as ConnectionLog;
            ConnectionHistoryItem? historyItem = obj as ConnectionHistoryItem;

            if (rule != null)
            {
                _editingRule = rule;
                ModalProcessName = rule.TargetApps?.FirstOrDefault() ?? "";
                ModalHost = rule.TargetHosts?.FirstOrDefault() ?? "";
                ModalAction = rule.Action;
                ModalBlockDirection = rule.BlockDirection;
                ModalGroupName = rule.GroupName ?? "General";
                ModalSelectedProxy = Proxies.FirstOrDefault(p => p.Id == rule.ProxyId);
                _modalIcon = rule.AppIcon;
                ModalTargetMode = GetRuleMode(rule);
                ModalIsTemporary = rule.IsTemporary;
                ModalTrafficType = rule.TrafficType;

                ModalIsScheduleEnabled = rule.IsScheduleEnabled;
                ModalTimeStart = rule.TimeStart ?? "";
                ModalTimeEnd = rule.TimeEnd ?? "";
            }
            else if (log != null)
            {
                ModalProcessName = log.ProcessName;
                ModalHost = log.Host;
                ModalAction = RuleAction.Proxy;
                ModalBlockDirection = BlockDirection.Both;
                ModalGroupName = _selectedGroupName ?? "General";
                ModalSelectedProxy = Proxies.FirstOrDefault(p => p.IsEnabled) ?? Proxies.FirstOrDefault();
                ModalTargetMode = IsBlackListMode ? RuleMode.BlackList : RuleMode.WhiteList;
                _modalIcon = null;
                ModalTrafficType = log.Type switch
                {
                    TrafficType.UDP => RuleTrafficType.UDP,
                    TrafficType.DNS => RuleTrafficType.DNS,
                    TrafficType.WebRTC => RuleTrafficType.WebRTC,
                    TrafficType.WebSocket => RuleTrafficType.WebSocket,
                    TrafficType.HTTPS => RuleTrafficType.HTTPS,
                    _ => RuleTrafficType.TCP
                };

                ModalIsScheduleEnabled = false;
                ModalTimeStart = "";
                ModalTimeEnd = "";
                ModalIsTemporary = false;
            }
            else if (historyItem != null)
            {
                ModalProcessName = historyItem.ProcessName;
                ModalHost = historyItem.Host;
                ModalAction = RuleAction.Proxy;
                ModalBlockDirection = BlockDirection.Both;
                ModalGroupName = _selectedGroupName ?? "General";
                ModalSelectedProxy = Proxies.FirstOrDefault(p => p.IsEnabled) ?? Proxies.FirstOrDefault();
                ModalTargetMode = IsBlackListMode ? RuleMode.BlackList : RuleMode.WhiteList;
                _modalIcon = null;
                ModalTrafficType = historyItem.Type switch
                {
                    TrafficType.UDP => RuleTrafficType.UDP,
                    TrafficType.DNS => RuleTrafficType.DNS,
                    TrafficType.WebRTC => RuleTrafficType.WebRTC,
                    TrafficType.WebSocket => RuleTrafficType.WebSocket,
                    TrafficType.HTTPS => RuleTrafficType.HTTPS,
                    _ => RuleTrafficType.TCP
                };

                ModalIsScheduleEnabled = false;
                ModalTimeStart = "";
                ModalTimeEnd = "";
                ModalIsTemporary = false;
            }
            else if (obj is RuleGroupInfo rgi)
            {
                ModalProcessName = "";
                ModalHost = "";
                ModalAction = RuleAction.Proxy;
                ModalBlockDirection = BlockDirection.Both;
                ModalGroupName = rgi.GroupName;
                ModalSelectedProxy = Proxies.FirstOrDefault(p => p.IsEnabled) ?? Proxies.FirstOrDefault();
                ModalTargetMode = _config.CurrentMode;
                _modalIcon = null;
                ModalTrafficType = RuleTrafficType.Any;
            }
            else if (obj is AppRuleInfo ari)
            {
                ModalProcessName = ari.AppName;
                ModalHost = "";
                ModalAction = RuleAction.Proxy;
                ModalBlockDirection = BlockDirection.Both;
                ModalGroupName = !string.IsNullOrEmpty(_selectedGroupName) ? _selectedGroupName : "QuickRules";
                ModalSelectedProxy = Proxies.FirstOrDefault(p => p.IsEnabled) ?? Proxies.FirstOrDefault();
                ModalTargetMode = _config.CurrentMode;
                _modalIcon = ari.AppIcon;
                ModalTrafficType = RuleTrafficType.Any;
            }
            else
            {
                // New Rule
                ModalProcessName = !string.IsNullOrEmpty(_selectedAppName) ? _selectedAppName : "";
                ModalHost = "";
                ModalAction = RuleAction.Proxy;
                ModalBlockDirection = BlockDirection.Both;
                ModalGroupName = !string.IsNullOrEmpty(_selectedGroupName) ? _selectedGroupName : "QuickRules";
                ModalSelectedProxy = Proxies.FirstOrDefault();
                ModalTargetMode = _config.CurrentMode;
                _modalIcon = !string.IsNullOrEmpty(ModalProcessName) ? IconHelper.GetIconByProcessName(ModalProcessName) : null;
                ModalTrafficType = RuleTrafficType.Any;

                ModalIsScheduleEnabled = false;
                ModalTimeStart = "";
                ModalTimeEnd = "";
                ModalIsTemporary = false;
            }

            ModalProcessSearch = "";
            ModalSelectedActiveProcess = null;
            RefreshActiveProcesses();

            OnPropertyChanged(nameof(ModalTitle));
            OnPropertyChanged(nameof(ModalSubtitle));
            OnPropertyChanged(nameof(IsEditMode));
            IsRenameGroupMode = false;
            OnPropertyChanged(nameof(IsRenameGroupMode));
            OnPropertyChanged(nameof(ExistingGroups)); // Refresh groups list

            IsModalVisible = true;
        }

        private void SaveRuleFromModal()
        {
            if (!IsRenameGroupMode && ModalIsScheduleEnabled)
            {
                if (ParseTime(ModalTimeStart) == null || ParseTime(ModalTimeEnd) == null)
                {
                    RequestShowNotification?.Invoke("Error", "Invalid schedule time format. Please use HH:mm.", 3000);
                    return;
                }
            }

            if (IsRenameGroupMode)
            {
                // Rename Group Logic
                string? renamedGroup = null;
                if (!string.IsNullOrEmpty(_batchEditValue) && !string.IsNullOrEmpty(ModalGroupName))
                {
                    renamedGroup = ModalGroupName.Trim();
                    var rulesToUpdate = RulesList.Where(r => r.GroupName == _batchEditValue).ToList();
                    foreach (var rule in rulesToUpdate)
                    {
                        rule.GroupName = renamedGroup;
                    }

                    // Also update configurations lists just in case
                    var blackListUpdates = _config.BlackListRules.Where(r => r.GroupName == _batchEditValue).ToList();
                    blackListUpdates.ForEach(r => r.GroupName = renamedGroup);

                    var whiteListUpdates = _config.WhiteListRules.Where(r => r.GroupName == _batchEditValue).ToList();
                    whiteListUpdates.ForEach(r => r.GroupName = renamedGroup);
                }

                _isBatchEditMode = false;
                IsRenameGroupMode = false;
                ReloadRulesForCurrentMode();
                if (!string.IsNullOrEmpty(renamedGroup)) SelectedGroupName = renamedGroup;
            }
            else if (_isBatchEditMode)
            {
                // Batch Update
                IEnumerable<TrafficRule> targets = Enumerable.Empty<TrafficRule>();

                if (_batchEditTarget == "Group")
                {
                    targets = RulesList.Where(r => r.GroupName == _batchEditValue).ToList();
                }
                else if (_batchEditTarget == "App")
                {
                    targets = RulesList.Where(r => r.TargetApps.Contains(_batchEditValue)).ToList();
                }

                foreach (var rule in targets)
                {
                    // Update common properties
                    rule.Action = ModalAction;
                    rule.BlockDirection = ModalBlockDirection;
                    rule.ProxyId = (ModalAction == RuleAction.Proxy && ModalSelectedProxy != null) ? ModalSelectedProxy.Id : null;
                    rule.TrafficType = ModalTrafficType;

                    rule.IsScheduleEnabled = ModalIsScheduleEnabled;
                    rule.TimeStart = ModalTimeStart;
                    rule.TimeEnd = ModalTimeEnd;
                    rule.ScheduleStart = ParseTime(ModalTimeStart);
                    rule.ScheduleEnd = ParseTime(ModalTimeEnd);

                    StoreRule(rule, ModalTargetMode, ModalIsTemporary);
                }

                _isBatchEditMode = false; // Reset
                ReloadRulesForCurrentMode(preserveSelection: true); // Refresh view
            }
            else if (IsEditMode && _editingRule != null)
            {
                // Edit existing rule
                _editingRule.TargetApps = new List<string> { ModalProcessName?.Trim() ?? "" };
                _editingRule.TargetHosts = new List<string> { ModalHost?.Trim() ?? "" };
                _editingRule.Action = ModalAction;
                _editingRule.BlockDirection = ModalBlockDirection;
                _editingRule.GroupName = ModalGroupName?.Trim() ?? "General";
                _editingRule.ProxyId = (ModalAction == RuleAction.Proxy && ModalSelectedProxy != null) ? ModalSelectedProxy.Id : null;
                _editingRule.AppIcon = _modalIcon;
                _editingRule.IconBase64 = _modalIcon != null ? IconHelper.ImageSourceToBase64(_modalIcon) : null;
                _editingRule.TrafficType = ModalTrafficType;

                _editingRule.IsScheduleEnabled = ModalIsScheduleEnabled;
                _editingRule.TimeStart = ModalTimeStart;
                _editingRule.TimeEnd = ModalTimeEnd;
                _editingRule.ScheduleStart = ParseTime(ModalTimeStart);
                _editingRule.ScheduleEnd = ParseTime(ModalTimeEnd);

                StoreRule(_editingRule, ModalTargetMode, ModalIsTemporary);
            }
            else
            {
                // Create new rule(s) - Split by separator
                var apps = ModalProcessName.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var hosts = ModalHost.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

                if (!apps.Any()) apps.Add("*");
                if (!hosts.Any()) hosts.Add("*");

                // Generate a rule for each combination
                foreach (var app in apps)
                {
                    var icon = IconHelper.GetIconByProcessName(app);
                    var icon64 = icon != null ? IconHelper.ImageSourceToBase64(icon) : null;

                    foreach (var host in hosts)
                    {
                        var rule = new TrafficRule
                        {
                            TargetApps = new List<string> { app },
                            TargetHosts = new List<string> { host },
                            IsEnabled = true,
                            Action = ModalAction,
                            BlockDirection = ModalBlockDirection,
                            GroupName = ModalGroupName,
                            ProxyId = (ModalAction == RuleAction.Proxy && ModalSelectedProxy != null) ? ModalSelectedProxy.Id : null,
                            AppIcon = icon,
                            IconBase64 = icon64,
                            TrafficType = ModalTrafficType,

                            IsScheduleEnabled = ModalIsScheduleEnabled,
                            TimeStart = ModalTimeStart?.Trim() ?? "",
                            TimeEnd = ModalTimeEnd?.Trim() ?? "",
                            ScheduleStart = ParseTime(ModalTimeStart),
                            ScheduleEnd = ParseTime(ModalTimeEnd),
                            ScheduleDays = new DayOfWeek[0], // Default
                            IsTemporary = ModalIsTemporary
                        };

                        StoreRule(rule, ModalTargetMode, ModalIsTemporary);

                        bool isCurrentModeView = (IsBlackListMode && ModalTargetMode == RuleMode.BlackList) || (!IsBlackListMode && ModalTargetMode == RuleMode.WhiteList);
                        if (isCurrentModeView) { SubscribeToItem(rule); RulesList.Add(rule); }
                    }
                }
            }
            RequestSaveSettings();
            ReloadRulesForCurrentMode(preserveSelection: true); // Force reload to ensure active proxy service gets new rules immediately
            RefreshRuleGroups();
            OnPropertyChanged(nameof(ExistingGroups));
            IsModalVisible = false;
        }

        private bool FilterActiveProcess(object obj)
        {
            if (obj is not ActiveProcessInfo process) return false;
            string search = ModalProcessSearch.Trim();
            if (search.Length == 0) return true;
            return process.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   process.FilePath.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        private async void RefreshActiveProcesses()
        {
            var processes = await Task.Run(() =>
            {
                var result = new Dictionary<string, ActiveProcessInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        string name = process.ProcessName + ".exe";
                        string path = "";
                        try { path = process.MainModule?.FileName ?? ""; } catch { }
                        string key = name + "|" + path;
                        if (!result.ContainsKey(key))
                        {
                            result[key] = new ActiveProcessInfo { ProcessName = name, FilePath = path };
                        }
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
                return result.Values
                    .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ActiveProcesses.Clear();
                foreach (var process in processes) ActiveProcesses.Add(process);
                ActiveProcessesView.Refresh();
            });
        }

        private void BrowseExeFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable Files (*.exe)|*.exe",
                Title = "Select Application"
            };
            if (dialog.ShowDialog() == true)
            {
                ModalProcessName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                _modalIcon = IconHelper.GetIconByProcessName(ModalProcessName);
                OnPropertyChanged(nameof(ModalProcessName));
            }
        }

        private void BrowseShortcutFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Shortcut Files (*.lnk)|*.lnk",
                Title = "Select Shortcut"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // Parse .lnk file to get target exe
                    var shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        var shortcut = shell.CreateShortcut(dialog.FileName);
                        string targetPath = shortcut.TargetPath;
                        if (!string.IsNullOrEmpty(targetPath) && targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            ModalProcessName = System.IO.Path.GetFileNameWithoutExtension(targetPath);
                            _modalIcon = IconHelper.GetIconByProcessName(ModalProcessName);
                            OnPropertyChanged(nameof(ModalProcessName));
                        }
                    }
                }
                catch { /* Ignore shortcut parsing errors */ }
            }
        }

        private void BrowseAppFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Applications|*.exe;*.lnk|Executable Files (*.exe)|*.exe|Shortcut Files (*.lnk)|*.lnk",
                Title = "Select Application or Shortcut"
            };
            if (dialog.ShowDialog() == true)
            {
                if (dialog.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var shellType = Type.GetTypeFromProgID("WScript.Shell");
                        if (shellType != null)
                        {
                            dynamic shell = Activator.CreateInstance(shellType)!;
                            var shortcut = shell.CreateShortcut(dialog.FileName);
                            string targetPath = shortcut.TargetPath;
                            if (!string.IsNullOrEmpty(targetPath) && targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                ModalProcessName = System.IO.Path.GetFileNameWithoutExtension(targetPath);
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    ModalProcessName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                }
                _modalIcon = IconHelper.GetIconByProcessName(ModalProcessName);
                OnPropertyChanged(nameof(ModalProcessName));
            }
        }

        private bool _isCheckingAllProxies;
        public bool IsCheckingAllProxies
        {
            get => _isCheckingAllProxies;
            set { _isCheckingAllProxies = value; OnPropertyChanged(); OnPropertyChanged(nameof(PingAllButtonText)); }
        }

        public string PingAllButtonText => _isCheckingAllProxies ? "Checking..." : "Ping All";

        private async Task CheckAllProxies(IReadOnlyList<ProxyItem>? proxies = null)
        {
            if (_isCheckingAllProxies) return;
            var proxyList = proxies?.ToList() ?? Proxies.ToList();
            if (proxyList.Count == 0)
            {
                ShowMessage("Ping All", "No proxies found in the list. Please add proxies first.");
                return;
            }

            IsCheckingAllProxies = true;
            try
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    foreach (var proxy in proxyList)
                    {
                        proxy.Status = "Checking...";
                    }
                });

                var online = new bool[proxyList.Count];

                // Phase 1: availability and TCP ping are lightweight, so check
                // several proxies concurrently and populate their status quickly.
                using (var semaphore = new SemaphoreSlim(6))
                {
                    var quickChecks = proxyList.Select(async (proxy, index) =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var result = await _proxyService.CheckProxy(proxy, measureSpeed: false);
                            online[index] = result.IsSuccess;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                proxy.Status = result.IsSuccess ? "Online" : "Offline";
                                proxy.PingMs = result.Ping;
                                if (!string.IsNullOrEmpty(result.CountryCode))
                                    proxy.CountryCode = result.CountryCode;
                                if (!result.IsSuccess)
                                {
                                    proxy.SpeedMBps = 0;
                                    proxy.IsSpeedChecking = false;
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                proxy.Status = "Offline";
                                proxy.SpeedMBps = 0;
                                proxy.IsSpeedChecking = false;
                            });
                            AppLoggerService.Instance.Error(
                                "ProxyCheck",
                                $"Could not check {proxy.Endpoint}: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    await Task.WhenAll(quickChecks);
                }

                // Phase 2: throughput tests are deliberately sequential. Running
                // them in parallel would make the proxies compete for bandwidth.
                for (int i = 0; i < proxyList.Count; i++)
                {
                    if (!online[i]) continue;

                    var proxy = proxyList[i];
                    double speed = await _proxyService.MeasureProxySpeedAsync(proxy);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        proxy.SpeedMBps = speed;
                        proxy.IsSpeedChecking = false;
                    });
                }
            }
            finally
            {
                IsCheckingAllProxies = false;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    foreach (var proxy in proxyList)
                        proxy.IsSpeedChecking = false;
                });
            }
        }

        private bool FilterRules(object obj)
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            if (obj is TrafficRule rule) { string search = SearchText.ToLower(); return rule.GroupName.ToLower().Contains(search) || rule.TargetApps.Any(a => a.ToLower().Contains(search)) || rule.TargetHosts.Any(h => h.ToLower().Contains(search)); }
            return false;
        }

        private bool FilterConnectionLog(object obj)
        {
            if (obj is not ConnectionLog log) return false;
            if (!MatchesTypeFilter(log.Type.ToString(), LogTypeFilter)) return false;
            if (!MatchesResultCategory(log.Result, LogResultFilter)) return false;

            if (UseAdvancedLogFilters)
            {
                return MatchesText(log.ProcessName, LogProcessFilter)
                    && MatchesText(log.Host, LogHostFilter)
                    && MatchesText(log.Result, LogResultTextFilter)
                    && MatchesText(log.CountryFlagUrl, LogCountryFilter);
            }

            return MatchesAny(LogSearchText, log.ProcessName, log.Host, log.Result, log.TypeStr, log.CountryFlagUrl);
        }

        private bool FilterMonitorConnection(object obj)
        {
            if (obj is not ConnectionHistoryItem item) return false;
            if (!MatchesTypeFilter(item.Type.ToString(), MonitorTypeFilter)) return false;
            if (!MatchesResultCategory(item.Status, MonitorResultFilter)) return false;

            if (UseAdvancedLogFilters)
            {
                return MatchesText(item.ProcessName, MonitorProcessFilter)
                    && MatchesText(item.Host, MonitorHostFilter)
                    && MatchesText(item.Status, MonitorStatusFilter)
                    && MatchesText(item.Details, MonitorDetailsFilter)
                    && MatchesText(item.FlagUrl, MonitorCountryFilter);
            }

            return MatchesAny(MonitorSearchText, item.ProcessName, item.Host, item.Status, item.Details, item.Type.ToString(), item.FlagUrl);
        }

        private static bool MatchesText(string? value, string? filter)
        {
            return string.IsNullOrWhiteSpace(filter)
                || (value?.IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        private static bool MatchesAny(string? filter, params string?[] values)
        {
            return string.IsNullOrWhiteSpace(filter) || values.Any(value => MatchesText(value, filter));
        }

        private static bool MatchesTypeFilter(string type, string selectedType)
        {
            return selectedType == "All" || string.Equals(type, selectedType, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesResultCategory(string? value, string selectedResult)
        {
            if (selectedResult == "All") return true;
            if (string.IsNullOrWhiteSpace(value)) return false;

            return selectedResult switch
            {
                "Proxy" => value.IndexOf("proxy", StringComparison.OrdinalIgnoreCase) >= 0,
                "Direct" => value.IndexOf("direct", StringComparison.OrdinalIgnoreCase) >= 0,
                "Blocked" => value.IndexOf("block", StringComparison.OrdinalIgnoreCase) >= 0,
                _ => true
            };
        }

        private void RefreshConnectionLogFilters()
        {
            LogsView?.Refresh();
            MonitorConnectionsView?.Refresh();
        }

        private void UpdateMonitorConnectionsView()
        {
            MonitorConnectionsView = CollectionViewSource.GetDefaultView(_selectedMonitorProcess?.Connections ?? _emptyMonitorConnections);
            MonitorConnectionsView.Filter = FilterMonitorConnection;
            MonitorConnectionsView.Refresh();
            OnPropertyChanged(nameof(MonitorConnectionsView));
        }

        private void ClearLogFilters()
        {
            LogSearchText = "";
            LogTypeFilter = "All";
            LogResultFilter = "All";
            LogProcessFilter = "";
            LogHostFilter = "";
            LogResultTextFilter = "";
            LogCountryFilter = "";
        }

        private void ClearMonitorFilters()
        {
            MonitorSearchText = "";
            MonitorTypeFilter = "All";
            MonitorResultFilter = "All";
            MonitorProcessFilter = "";
            MonitorHostFilter = "";
            MonitorStatusFilter = "";
            MonitorDetailsFilter = "";
            MonitorCountryFilter = "";
        }

        private void OnLogReceived(ConnectionLog log)
        {
            QueueConnectionSiteIcon(log);
            _pendingConnectionLogs.Enqueue(log);
            int count = Interlocked.Increment(ref _pendingConnectionLogCount);
            while (count > MaxPendingConnectionLogs && _pendingConnectionLogs.TryDequeue(out _))
            {
                count = Interlocked.Decrement(ref _pendingConnectionLogCount);
            }
        }

        private void FlushPendingConnectionLogs(object? sender, EventArgs e)
        {
            int processed = 0;
            while (processed < 500 && _pendingConnectionLogs.TryDequeue(out var log))
            {
                Interlocked.Decrement(ref _pendingConnectionLogCount);
                Logs.Insert(0, log);
                processed++;
            }

            while (Logs.Count > 2500) Logs.RemoveAt(Logs.Count - 1);
        }

        private void OnMonitorConnectionCreated(ConnectionHistoryItem connection) =>
            QueueConnectionSiteIcon(connection);

        private void OnOverallStatsUpdated()
        {
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                OnPropertyChanged(nameof(HeaderDownloadSpeedText));
                OnPropertyChanged(nameof(HeaderUploadSpeedText));
                OnPropertyChanged(nameof(HeaderActiveConnectionsText));
            }));
        }

        private void QueueConnectionSiteIcon(ConnectionLog log)
        {
            string host = log.Host;
            _connectionIconQueue.Writer.TryWrite(new ConnectionIconRequest(host, icon =>
            {
                if (string.Equals(log.Host, host, StringComparison.Ordinal))
                    log.SiteIcon = icon;
            }));
        }

        private void QueueConnectionSiteIcon(ConnectionHistoryItem connection)
        {
            string host = connection.Host;
            _connectionIconQueue.Writer.TryWrite(new ConnectionIconRequest(host, icon =>
            {
                if (string.Equals(connection.Host, host, StringComparison.Ordinal))
                    connection.SiteIcon = icon;
            }));
        }

        private async Task ProcessConnectionIconQueueAsync(CancellationToken token)
        {
            try
            {
                await foreach (ConnectionIconRequest request in
                    _connectionIconQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    ImageSource? icon = await _siteIconCacheService
                        .GetFirstIconAsync(new[] { request.Host })
                        .ConfigureAwait(false);

                    if (Application.Current == null || token.IsCancellationRequested)
                        continue;

                    await Application.Current.Dispatcher.InvokeAsync(
                        () => request.Assign(icon),
                        DispatcherPriority.Background,
                        token);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private void StartEnforcementLoop()
        {
            // Enforcement loop disabled for TUN mode
            // _enforceCts?.Cancel(); _enforceCts = new CancellationTokenSource();
            // Task.Run(async () => { while (!_enforceCts.Token.IsCancellationRequested) { await Task.Delay(5000); if (IsProxyRunning) _proxyService.EnforceSystemProxy(); } }, _enforceCts.Token);
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is TrafficRule rule && e.PropertyName == nameof(TrafficRule.TargetHosts))
                QueueSiteIconLoad(rule);

            var triggers = new HashSet<string> { nameof(TrafficRule.IsEnabled), nameof(TrafficRule.ProxyId), nameof(TrafficRule.TargetApps), nameof(TrafficRule.TargetHosts), nameof(TrafficRule.Action), nameof(TrafficRule.GroupName), nameof(TrafficRule.BlockDirection), nameof(ProxyItem.Name), nameof(ProxyItem.IsEnabled), nameof(ProxyItem.IpAddress), nameof(ProxyItem.Port), nameof(ProxyItem.Username), nameof(ProxyItem.Password), nameof(ProxyItem.CountryCode), nameof(ProxyItem.SpeedMBps), nameof(ProxyItem.UseTls), nameof(ProxyItem.UseSsl), nameof(TrafficRule.IconBase64), nameof(ProxyItem.Type) };

            // Warning removed as support is being implemented

            if (!string.IsNullOrEmpty(e.PropertyName) && triggers.Contains(e.PropertyName))
            {
                RequestSaveSettings();
                RefreshTunIfRunning();

                // If Proxy Type changed, re-evaluate TUN eligibility
                if (e.PropertyName == nameof(ProxyItem.Type))
                {
                    OnPropertyChanged(nameof(CanEnableTunMode));
                    if (IsTunMode && !CanEnableTunMode) IsTunMode = false;
                }
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_suppressSave) return;
            if (e.NewItems != null) foreach (INotifyPropertyChanged item in e.NewItems) SubscribeToItem(item);
            if (e.OldItems != null) foreach (INotifyPropertyChanged item in e.OldItems) item.PropertyChanged -= OnItemPropertyChanged;

            // Collection changed, re-evaluate TUN eligibility (TunProxy might have changed/removed)
            OnPropertyChanged(nameof(CanEnableTunMode));
            if (IsTunMode && !CanEnableTunMode) IsTunMode = false;

            RequestSaveSettings();
            RefreshTunIfRunning();
        }

        private void SubscribeToItem(INotifyPropertyChanged item)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            item.PropertyChanged += OnItemPropertyChanged;
            if (item is TrafficRule rule)
                QueueSiteIconLoad(rule);
        }

        private void QueueSiteIconLoad(TrafficRule rule)
        {
            string signature = GetSiteIconSignature(rule);
            rule.SiteIcon = null;
            _ = LoadSiteIconAsync(rule, signature);
        }

        private async Task LoadSiteIconAsync(TrafficRule rule, string signature)
        {
            ImageSource? icon = await _siteIconCacheService
                .GetFirstIconAsync(rule.TargetHosts.ToArray())
                .ConfigureAwait(false);

            if (Application.Current == null)
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (GetSiteIconSignature(rule) == signature)
                {
                    rule.SiteIcon = icon;
                }
            }, DispatcherPriority.Background);
        }

        private static string GetSiteIconSignature(TrafficRule rule) =>
            string.Join("\n", rule.TargetHosts);

        private void RequestSaveSettings()
        {
            if (_suppressSave) return;
            try { ApplyConfig(); } catch { }
            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            _saveDebounceCts = new CancellationTokenSource();
            _ = SaveSettingsAfterDelayAsync(_saveDebounceCts.Token);
        }

        private async Task SaveSettingsAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(500, token);
                AppSettings data;
                if (Application.Current.Dispatcher.CheckAccess())
                {
                    data = CreateSettingsSnapshot();
                }
                else
                {
                    data = await Application.Current.Dispatcher.InvokeAsync(CreateSettingsSnapshot);
                }

                await _settingsService.SaveAsync(data, token);
            }
            catch (OperationCanceledException) { }
        }

        private void SaveSettingsNow()
        {
            SaveSettingsCore();
        }

        private bool SaveSettingsCore()
        {
            try
            {
                ApplyConfig();
                _settingsService.Save(CreateSettingsSnapshot());
                return true;
            }
            catch { return false; }
        }

        private string? _lastSeenVersion;
        public string? LastSeenVersion
        {
            get => _lastSeenVersion;
            set { _lastSeenVersion = value; OnPropertyChanged(); }
        }

        private AppSettings CreateSettingsSnapshot()
        {
            EnsureProxyNames(Proxies);
            return new AppSettings
            {
                IsAutoStart = IsAutoStart,
                IsProxyRunning = IsProxyRunning,
                CheckUpdateOnStartup = CheckUpdateOnStartup,
                LastSeenVersion = _lastSeenVersion,
                Proxies = Proxies.ToList(),
                Config = _config,
                Profiles = Profiles.ToList(),
                ActiveProfileId = _activeProfileId
            };
        }

        private async Task<bool> SaveSettingsCoreAsync()
        {
            try
            {
                ApplyConfig();
                return await _settingsService.SaveAsync(CreateSettingsSnapshot());
            }
            catch { return false; }
        }

        private void MarkDohSettingsChanged()
        {
            if (_suppressSave) return;
            DohSaveStatus = "Unsaved changes";
        }

        private async Task SaveDohSettingsNowAsync()
        {
            DohSaveStatus = "Saving DNS...";
            if (await _settingsService.SaveDnsAsync(_config))
            {
                // Apply the new upstreams to the already running DNS service before
                // clearing Windows' cached answers.
                ApplyConfig();

                if (IsDnsProtectionEnabled)
                {
                    DohSaveStatus = "Applying...";
                    var validationResult = await Task.Run(() =>
                    {
                        SystemProxyHelper.SetSystemDns(_config);
                        SystemProxyHelper.FlushSystemDnsCache();
                        return SystemProxyHelper.ValidateWindowsDohSettings(_config, out var validationMessage)
                            ? string.Empty
                            : validationMessage;
                    });

                    if (!string.IsNullOrWhiteSpace(validationResult))
                    {
                        DohSaveStatus = validationResult;
                        return;
                    }
                }
                else
                {
                    await Task.Run(SystemProxyHelper.FlushSystemDnsCache);
                }

                DohSaveStatus = "Saved · DNS cache cleared";
            }
            else
            {
                DohSaveStatus = "Save failed";
            }
        }

        private async Task CheckDnsServersAsync()
        {
            if (IsDnsCheckInProgress) return;

            IsDnsCheckInProgress = true;
            PrimaryDnsStatus = "Checking...";
            PrimaryDnsStatusDetails = "Sending DNS request...";
            FallbackDnsStatus = "Checking...";
            FallbackDnsStatusDetails = "Sending DNS request...";

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var primaryTask = DnsHealthCheckService.CheckAsync(_config, false, timeout.Token);
                var fallbackTask = DnsHealthCheckService.CheckAsync(_config, true, timeout.Token);
                await Task.WhenAll(primaryTask, fallbackTask);

                var primary = await primaryTask;
                PrimaryDnsStatus = primary.IsOnline ? "Online" : "Offline";
                PrimaryDnsStatusDetails = primary.Details;

                var fallback = await fallbackTask;
                FallbackDnsStatus = fallback.IsOnline ? "Online" : "Offline";
                FallbackDnsStatusDetails = fallback.Details;
            }
            finally
            {
                IsDnsCheckInProgress = false;
            }
        }

        private List<TrafficRule> GetRulesForMode(RuleMode mode)
        {
            var persistent = mode == RuleMode.BlackList ? _config.BlackListRules : _config.WhiteListRules;
            var temporary = mode == RuleMode.BlackList ? _temporaryBlackListRules : _temporaryWhiteListRules;
            // Temporary rules intentionally take precedence, so they can be used
            // as session-only overrides of an existing persistent rule.
            return temporary.Concat(persistent).ToList();
        }

        private RuleMode GetRuleMode(TrafficRule rule)
        {
            return _config.BlackListRules.Contains(rule) || _temporaryBlackListRules.Contains(rule)
                ? RuleMode.BlackList
                : RuleMode.WhiteList;
        }

        private void RemoveRuleFromStorage(TrafficRule rule)
        {
            _config.BlackListRules.Remove(rule);
            _config.WhiteListRules.Remove(rule);
            _temporaryBlackListRules.Remove(rule);
            _temporaryWhiteListRules.Remove(rule);
        }

        private void StoreRule(TrafficRule rule, RuleMode mode, bool isTemporary)
        {
            RemoveRuleFromStorage(rule);
            rule.IsTemporary = isTemporary;
            if (isTemporary)
            {
                if (mode == RuleMode.BlackList) _temporaryBlackListRules.Add(rule);
                else _temporaryWhiteListRules.Add(rule);
            }
            else
            {
                if (mode == RuleMode.BlackList) _config.BlackListRules.Add(rule);
                else _config.WhiteListRules.Add(rule);
            }
        }

        private AppConfig CreateRuntimeConfig()
        {
            return new AppConfig
            {
                CurrentMode = _config.CurrentMode,
                BlackListSelectedProxyId = _config.BlackListSelectedProxyId,
                TunProxyId = _config.TunProxyId,
                EnableDnsProtection = _config.EnableDnsProtection,
                IsWebRtcBlockingEnabled = _config.IsWebRtcBlockingEnabled,
                IsTunMode = _config.IsTunMode,
                IsSystemProxyEnabled = _config.IsSystemProxyEnabled,
                RoutingMode = _config.RoutingMode,
                UseAdvancedLogFilters = _config.UseAdvancedLogFilters,
                DnsProvider = _config.DnsProvider,
                DnsHost = _config.DnsHost,
                DnsFallbackHost = _config.DnsFallbackHost,
                PreferPrimaryDns = _config.PreferPrimaryDns,
                EnableDoh = _config.EnableDoh,
                DohProvider = _config.DohProvider,
                AutoDetectDohEndpoint = _config.AutoDetectDohEndpoint,
                DohEndpoint = _config.DohEndpoint,
                EnableDohFallback = _config.EnableDohFallback,
                AutoDetectDohFallbackEndpoint = _config.AutoDetectDohFallbackEndpoint,
                DohFallbackEndpoint = _config.DohFallbackEndpoint,
                BlackListRules = GetRulesForMode(RuleMode.BlackList),
                WhiteListRules = GetRulesForMode(RuleMode.WhiteList),
                Presets = _config.Presets
            };
        }

        private void ApplyConfig()
        {
            var runtimeConfig = CreateRuntimeConfig();
            var proxies = Proxies.ToList();
            _proxyService.UpdateConfig(runtimeConfig, proxies);
            _dnsProxyService.UpdateConfig(runtimeConfig, proxies);
        }

        private void RefreshTunIfRunning()
        {
            if (!IsTunMode || !_tunService.IsRunning) return;

            var tunConfig = CreateTunRulesConfig();
            _tunRefreshCts?.Cancel();
            _tunRefreshCts?.Dispose();
            _tunRefreshCts = new CancellationTokenSource();
            var token = _tunRefreshCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    // A queued refresh must never resurrect TUN after it was turned off.
                    if (!IsTunMode || token.IsCancellationRequested) return;
                    await _tunService.StartAsync(tunConfig, token);
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private TunService.TunRulesConfig CreateTunRulesConfig()
        {
            return new TunService.TunRulesConfig
            {
                Mode = _config.CurrentMode,
                Rules = GetRulesForMode(_config.CurrentMode),
                ProxyType = (_config.CurrentMode == RuleMode.BlackList)
                    ? (SelectedBlackListMainProxy?.Type ?? ProxyType.Http)
                    : ProxyType.Http
            };
        }

        private static void EnsureProxyNames(IEnumerable<ProxyItem> proxies)
        {
            foreach (var proxy in proxies)
            {
                if (string.IsNullOrWhiteSpace(proxy.Name)) proxy.Name = proxy.Id;
            }
        }

        private void AddProxy() { var p = new ProxyItem { IpAddress = "", Port = 8080, IsEnabled = true, Status = "New" }; Proxies.Add(p); SelectedProxy = p; }

        private async void PasteProxy()
        {
            if (Clipboard.ContainsText())
            {
                var lines = Clipboard.GetText().Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                bool added = false;
                foreach (var line in lines)
                {
                    var parts = line.Trim().Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int port))
                    {
                        var p = new ProxyItem { IpAddress = parts[0], Port = port, IsEnabled = true, Status = "Pasted" };
                        if (parts.Length >= 4)
                        {
                            p.Username = parts[2];
                            p.Password = parts[3];
                        }
                        Proxies.Add(p);
                        _ = CheckSingleProxy(p);
                        if (!added)
                        {
                            SelectedProxy = p;
                            added = true;
                        }
                    }
                }
            }
        }

        private void RemoveProxy()
        {
            if (SelectedProxy != null)
            {
                ShowConfirmation(
                    "Delete Proxy?",
                    $"Are you sure you want to delete proxy '{SelectedProxy.Name}' ({SelectedProxy.Endpoint})?",
                    () =>
                    {
                        string pid = SelectedProxy.Id;
                        var bl = _config.BlackListRules.Where(r => r.ProxyId == pid).ToList();
                        bl.ForEach(r => _config.BlackListRules.Remove(r));
                        var wl = _config.WhiteListRules.Where(r => r.ProxyId == pid).ToList();
                        wl.ForEach(r => _config.WhiteListRules.Remove(r));
                        _temporaryBlackListRules.RemoveAll(r => r.ProxyId == pid);
                        _temporaryWhiteListRules.RemoveAll(r => r.ProxyId == pid);
                        if (_config.BlackListSelectedProxyId.ToString() == pid)
                        {
                            _config.BlackListSelectedProxyId = null;
                            OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                        }
                        Proxies.Remove(SelectedProxy);
                        SelectedProxy = null;
                        ReloadRulesForCurrentMode();
                        RequestSaveSettings();
                    });
            }
        }

        private void DuplicateRule(TrafficRule? rule)
        {
            if (rule == null) return;
            var clone = new TrafficRule
            {
                GroupName = rule.GroupName,
                Action = rule.Action,
                BlockDirection = rule.BlockDirection,
                ProxyId = rule.ProxyId,
                IsEnabled = rule.IsEnabled,
                TrafficType = rule.TrafficType,
                AppIcon = rule.AppIcon,
                SiteIcon = rule.SiteIcon,
                IconBase64 = rule.IconBase64,
                IsTemporary = rule.IsTemporary,
                TargetHosts = new List<string>(rule.TargetHosts ?? new List<string>()),
                TargetApps = new List<string>(rule.TargetApps ?? new List<string>())
            };

            if (IsBlackListMode)
            {
                _config.BlackListRules.Add(clone);
            }
            else
            {
                _config.WhiteListRules.Add(clone);
            }

            ReloadRulesForCurrentMode();
            SelectedRule = clone;
            RequestSaveSettings();
            string ruleDesc = clone.TargetHosts.Count > 0 ? string.Join(", ", clone.TargetHosts) : (clone.TargetApps.Count > 0 ? string.Join(", ", clone.TargetApps) : "Rule");
            ShowMessage("Rule Duplicated", $"Rule for '{ruleDesc}' has been duplicated.");
        }

        private void ToggleRuleEnabled(TrafficRule? rule)
        {
            if (rule == null) return;
            rule.IsEnabled = !rule.IsEnabled;
            ApplyConfig();
            RequestSaveSettings();
        }

        private void SetRuleAction(object? parameter)
        {
            string? actionStr = parameter as string;
            var targetRule = SelectedRule;
            if (targetRule == null || string.IsNullOrWhiteSpace(actionStr)) return;

            if (Enum.TryParse<RuleAction>(actionStr, true, out var parsedAction))
            {
                targetRule.Action = parsedAction;
                ApplyConfig();
                RequestSaveSettings();
            }
        }

        private void CopyRuleHosts(TrafficRule? rule)
        {
            if (rule == null || rule.TargetHosts == null || rule.TargetHosts.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(", ", rule.TargetHosts));
            }
            catch { }
        }

        private void CopyRuleApps(TrafficRule? rule)
        {
            if (rule == null || rule.TargetApps == null || rule.TargetApps.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(", ", rule.TargetApps));
            }
            catch { }
        }

        private async Task CheckSpecificProxy(ProxyItem? proxy)
        {
            if (proxy == null) return;
            await CheckSingleProxy(proxy);
        }

        private void SetAsMainProxy(ProxyItem? proxy)
        {
            if (proxy == null) return;
            SelectedBlackListMainProxy = proxy;
            RequestSaveSettings();
            ShowMessage("Main Proxy", $"'{proxy.Name}' ({proxy.Endpoint}) is now the default BlackList proxy.");
        }

        private void SetAsTunProxy(ProxyItem? proxy)
        {
            if (proxy == null) return;
            TunProxy = proxy;
            RequestSaveSettings();
            ShowMessage("TUN Proxy", $"'{proxy.Name}' ({proxy.Endpoint}) is now the TUN mode proxy.");
        }

        private void CopyProxyEndpoint(ProxyItem? proxy)
        {
            if (proxy == null) return;
            try
            {
                Clipboard.SetText(proxy.Endpoint);
            }
            catch { }
        }

        private void CopyProxyIp(ProxyItem? proxy)
        {
            if (proxy == null) return;
            try
            {
                Clipboard.SetText(proxy.IpAddress);
            }
            catch { }
        }

        private void CopyProxyFull(ProxyItem? proxy)
        {
            if (proxy == null) return;
            try
            {
                string scheme = proxy.Type == ProxyType.Socks5 ? "socks5" : (proxy.UseTls || proxy.UseSsl ? "https" : "http");
                string formatted = !string.IsNullOrEmpty(proxy.Username)
                    ? $"{scheme}://{proxy.Username}:{proxy.Password}@{proxy.IpAddress}:{proxy.Port}"
                    : $"{scheme}://{proxy.IpAddress}:{proxy.Port}";
                Clipboard.SetText(formatted);
            }
            catch { }
        }

        private void ToggleProxyEnabled(ProxyItem? proxy)
        {
            if (proxy == null) return;
            proxy.IsEnabled = !proxy.IsEnabled;
            ApplyConfig();
            RequestSaveSettings();
        }

        private void RemoveSpecificProxy(ProxyItem? proxy)
        {
            if (proxy == null) return;
            SelectedProxy = proxy;
            RemoveProxy();
        }

        private async Task CheckSingleProxy(ProxyItem p)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                p.Status = "Checking...";
                p.IsSpeedChecking = true;
            });
            try
            {
                var res = await Task.Run(() => _proxyService.CheckProxy(p));
                Application.Current.Dispatcher.Invoke(() => { p.Status = res.IsSuccess ? "Online" : "Offline"; p.PingMs = res.Ping; p.SpeedMBps = res.Speed; if (!string.IsNullOrEmpty(res.CountryCode)) p.CountryCode = res.CountryCode; });
            }
            finally
            {
                Application.Current.Dispatcher.Invoke(() => p.IsSpeedChecking = false);
            }
        }

        private async void CheckSelectedProxy()
        {
            var proxy = SelectedProxy;
            if (proxy == null || IsProxyCheckInProgress) return;

            IsProxyCheckInProgress = true;
            ProxyCheckStatus = "Checking...";
            ProxyCheckSummary = $"Testing {proxy.Name}";
            ProxyCheckDetails = proxy.Endpoint;
            proxy.Status = "Checking...";
            proxy.IsSpeedChecking = true;

            try
            {
                var result = await Task.Run(() => _proxyService.CheckProxy(proxy));
                proxy.Status = result.IsSuccess ? "Online" : "Offline";
                proxy.PingMs = result.Ping;
                proxy.SpeedMBps = result.Speed;
                if (!string.IsNullOrEmpty(result.CountryCode)) proxy.CountryCode = result.CountryCode;

                if (ReferenceEquals(SelectedProxy, proxy))
                {
                    ProxyCheckStatus = proxy.Status;
                    ProxyCheckSummary = result.IsSuccess
                        ? $"{proxy.Name} is reachable"
                        : $"{proxy.Name} is unavailable";
                    ProxyCheckDetails = result.IsSuccess
                        ? $"{proxy.Endpoint}  •  Ping {proxy.PingFormatted}  •  Speed {proxy.SpeedFormatted}"
                        : $"{proxy.Endpoint}  •  {result.SslError}";
                }
            }
            catch (Exception ex)
            {
                proxy.Status = "Offline";
                if (ReferenceEquals(SelectedProxy, proxy))
                {
                    ProxyCheckStatus = "Offline";
                    ProxyCheckSummary = $"{proxy.Name} could not be checked";
                    ProxyCheckDetails = ex.Message;
                }
            }
            finally
            {
                proxy.IsSpeedChecking = false;
                IsProxyCheckInProgress = false;
            }
        }

        private void AddRule()
        {
            var appsList = NewRuleApps.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var hostsList = NewRuleHosts.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (!appsList.Any()) appsList.Add("*"); if (!hostsList.Any()) hostsList.Add("*");
            string group = string.IsNullOrWhiteSpace(NewRuleGroup) ? "General" : NewRuleGroup;
            string? pid = null;
            if (NewRuleAction == RuleAction.Proxy) { if (IsBlackListMode) { if (NewRuleSelectedProxy != null) pid = NewRuleSelectedProxy.Id; } else { if (NewRuleSelectedProxy == null) { MessageBox.Show("Select Proxy!"); return; } pid = NewRuleSelectedProxy.Id; } }
            foreach (var app in appsList) { var icon = IconHelper.GetIconByProcessName(app); string? ib64 = icon != null ? IconHelper.ImageSourceToBase64(icon) : null; foreach (var host in hostsList) { if (RulesList.Any(r => r.GroupName == group && r.TargetApps.Contains(app) && r.TargetHosts.Contains(host))) continue; var rule = new TrafficRule { TargetApps = new List<string> { app }, TargetHosts = new List<string> { host }, IsEnabled = true, Action = NewRuleAction, BlockDirection = NewRuleBlockDirection, GroupName = group, ProxyId = pid, AppIcon = icon, IconBase64 = ib64, TimeStart = NewRuleTimeStart, TimeEnd = NewRuleTimeEnd }; if (IsBlackListMode) _config.BlackListRules.Add(rule); else _config.WhiteListRules.Add(rule); SubscribeToItem(rule); RulesList.Add(rule); } }
        }

        private void RemoveRule(TrafficRule? rule)
        {
            var r = rule ?? SelectedRule;
            if (r != null)
            {
                RemoveRuleFromStorage(r);
                RulesList.Remove(r);
                if (ReferenceEquals(SelectedRule, r)) SelectedRule = null;

                if (!string.IsNullOrEmpty(_selectedAppName) && !RulesList.Any(rl => (rl.GroupName ?? "General") == _selectedGroupName && (rl.TargetApps?.Contains(_selectedAppName) ?? false)))
                {
                    SelectedAppName = null;
                }

                if (!string.IsNullOrEmpty(_selectedGroupName) && !RulesList.Any(rl => (rl.GroupName ?? "General") == _selectedGroupName))
                {
                    SelectedGroupName = null;
                }

                RefreshRuleGroups();
                OnPropertyChanged(nameof(SelectedGroupApps));
                OnPropertyChanged(nameof(SelectedGroupRules));
                ApplyConfig();
                RequestSaveSettings();
            }
        }

        private void DeleteRules(bool byApp)
        {
            var rulesToDelete = new List<TrafficRule>();

            if (byApp && !string.IsNullOrEmpty(_selectedAppName))
            {
                rulesToDelete = RulesList.Where(r => r.TargetApps != null && r.TargetApps.Contains(_selectedAppName)).ToList();
            }
            else if (!byApp && !string.IsNullOrEmpty(_selectedGroupName))
            {
                rulesToDelete = RulesList.Where(r => (r.GroupName ?? "General") == _selectedGroupName).ToList();
            }

            if (rulesToDelete.Any())
            {
                rulesToDelete.ForEach(RemoveRuleFromStorage);

                rulesToDelete.ForEach(r => RulesList.Remove(r));

                // If deleted app rules, reset app selection, keep group if it still has rules
                if (byApp)
                {
                    SelectedAppName = null;
                    if (!RulesList.Any(r => (r.GroupName ?? "General") == _selectedGroupName))
                    {
                        SelectedGroupName = null;
                    }
                }
                else
                {
                    SelectedGroupName = null;
                    SelectedAppName = null;
                }

                RefreshRuleGroups();
                OnPropertyChanged(nameof(SelectedGroupApps));
                OnPropertyChanged(nameof(SelectedGroupRules));
                ApplyConfig();
                RequestSaveSettings();
            }
            IsDeleteModalVisible = false;
        }


        private void ReloadRulesForCurrentMode(bool preserveSelection = false)
        {
            _suppressSave = true;
            string? prevGroup = preserveSelection ? _selectedGroupName : null;
            string? prevApp = preserveSelection ? _selectedAppName : null;

            RulesList.Clear();

            if (!preserveSelection)
            {
                // Reset selected group when switching modes
                _selectedGroupName = null;
                _selectedAppName = null;
                OnPropertyChanged(nameof(SelectedGroupName));
                OnPropertyChanged(nameof(IsGroupSelected));
                OnPropertyChanged(nameof(SelectedAppName));
                OnPropertyChanged(nameof(IsAppSelected));
            }

            var src = GetRulesForMode(IsBlackListMode ? RuleMode.BlackList : RuleMode.WhiteList);
            foreach (var r in src)
            {
                if (!string.IsNullOrEmpty(r.IconBase64))
                    r.AppIcon = IconHelper.Base64ToImageSource(r.IconBase64);
                else if (r.TargetApps.Any())
                {
                    var i = IconHelper.GetIconByProcessName(r.TargetApps.First());
                    if (i != null) { r.AppIcon = i; r.IconBase64 = IconHelper.ImageSourceToBase64(i); }
                }
                SubscribeToItem(r);
                RulesList.Add(r);
            }
            _suppressSave = false;

            if (preserveSelection)
            {
                if (!string.IsNullOrEmpty(prevGroup) && RulesList.Any(r => (r.GroupName ?? "General") == prevGroup))
                {
                    _selectedGroupName = prevGroup;
                    OnPropertyChanged(nameof(SelectedGroupName));
                    OnPropertyChanged(nameof(IsGroupSelected));

                    if (!string.IsNullOrEmpty(prevApp) && RulesList.Any(r => (r.GroupName ?? "General") == prevGroup && (r.TargetApps?.Contains(prevApp) ?? false)))
                    {
                        _selectedAppName = prevApp;
                        OnPropertyChanged(nameof(SelectedAppName));
                        OnPropertyChanged(nameof(IsAppSelected));
                    }
                    else
                    {
                        _selectedAppName = null;
                        OnPropertyChanged(nameof(SelectedAppName));
                        OnPropertyChanged(nameof(IsAppSelected));
                    }

                    OnPropertyChanged(nameof(SelectedGroupApps));
                    OnPropertyChanged(nameof(SelectedGroupRules));
                }
                else
                {
                    _selectedGroupName = null;
                    _selectedAppName = null;
                    OnPropertyChanged(nameof(SelectedGroupName));
                    OnPropertyChanged(nameof(IsGroupSelected));
                    OnPropertyChanged(nameof(SelectedAppName));
                    OnPropertyChanged(nameof(IsAppSelected));
                    OnPropertyChanged(nameof(SelectedGroupApps));
                    OnPropertyChanged(nameof(SelectedGroupRules));
                }
            }

            RulesView.Refresh();
            RefreshRuleGroups();
            OnPropertyChanged(nameof(IsBlackListMode));
        }

        private void ToggleService()
        {
            IsProxyRunning = !IsProxyRunning;
            if (IsProxyRunning)
            {
                try
                {
                    _proxyService.Start();
                    UpdateDnsServiceState();
                    if (IsTunMode)
                    {
                        var tunConfig = CreateTunRulesConfig();
                        _ = _tunService.StartAsync(tunConfig); // Ensure TUN restarts if it was active
                    }
                }
                catch (Exception ex)
                {
                    IsProxyRunning = false;
                    ShowMessage("Proxy Service", $"Failed to start proxy service: {ex.Message}");
                }
            }
            else
            {
                _proxyService.Stop();
                _dnsProxyService.Stop();
                if (IsTunMode) _ = _tunService.StopAsync(); // Stop TUN if main proxy stops
            }
        }

        private void UpdateDnsServiceState()
        {
            _ = Task.Run(() =>
            {
                if (IsProxyRunning && IsDnsProtectionEnabled) _dnsProxyService.Start();
                else _dnsProxyService.Stop();
            });
        }

        private void ImportConfig()
        {
            var dlg = new OpenFileDialog { Filter = "JSON|*.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var d = _settingsService.Load(dlg.FileName);
                _suppressSave = true;
                _temporaryBlackListRules.Clear();
                _temporaryWhiteListRules.Clear();
                _config = d.Config ?? new AppConfig();
                NormalizeRoutingMode();
                IsAutoStart = d.IsAutoStart;
                CheckUpdateOnStartup = d.CheckUpdateOnStartup;
                Proxies.Clear();
                if (d.Proxies != null)
                {
                    EnsureProxyNames(d.Proxies);
                    d.Proxies.ForEach(p => { SubscribeToItem(p); Proxies.Add(p); });
                }
                LoadProfilesFromSettings(d);
                OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                ReloadRulesForCurrentMode();
                _suppressSave = false;
                RequestSaveSettings();
                MessageBox.Show("Imported!");
            }
            catch
            {
                _suppressSave = false;
            }
        }

        private void ExportConfig() { var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = "settings.json" }; if (dlg.ShowDialog() == true) { try { _settingsService.Save(CreateSettingsSnapshot(), dlg.FileName); MessageBox.Show("Exported!"); } catch { } } }

        private void LoadSettings()
        {
            _suppressSave = true;
            bool isFirstRunForNewVersion = false;
            try
            {
                var d = _settingsService.Load();
                _lastSeenVersion = d.LastSeenVersion;
                isFirstRunForNewVersion = string.IsNullOrEmpty(d.LastSeenVersion) || d.LastSeenVersion != CurrentVersion;
                _temporaryBlackListRules.Clear();
                _temporaryWhiteListRules.Clear();
                _config = d.Config ?? new AppConfig();
                NormalizeRoutingMode();
                IsAutoStart = _settingsService.IsAutoStartEnabled();
                CheckUpdateOnStartup = d.CheckUpdateOnStartup;

                Proxies.Clear();
                if (d.Proxies != null)
                {
                    EnsureProxyNames(d.Proxies);
                    d.Proxies.ForEach(p => { SubscribeToItem(p); Proxies.Add(p); });
                }

                LoadProfilesFromSettings(d);

                OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                ReloadRulesForCurrentMode();
                OnPropertyChanged(nameof(IsDnsProtectionEnabled));
                OnPropertyChanged(nameof(SelectedDnsProvider));
                OnPropertyChanged(nameof(DnsHost));
                OnPropertyChanged(nameof(DnsFallbackHost));
                OnPropertyChanged(nameof(PreferPrimaryDns));
                OnPropertyChanged(nameof(IsDohEnabled));
                OnPropertyChanged(nameof(IsDohFallbackEnabled));
                OnPropertyChanged(nameof(IsDohPrimaryAuto));
                OnPropertyChanged(nameof(DohEndpoint));
                OnPropertyChanged(nameof(DohEndpointInput));
                OnPropertyChanged(nameof(IsDohFallbackAuto));
                OnPropertyChanged(nameof(DohFallbackEndpoint));
                OnPropertyChanged(nameof(DohFallbackEndpointInput));
                OnPropertyChanged(nameof(DohEndpointValidationMessage));
                OnPropertyChanged(nameof(DohFallbackEndpointValidationMessage));
                QueueDohTransportStatusRefresh(0);
                OnPropertyChanged(nameof(DohSaveStatus));
                OnPropertyChanged(nameof(UseAdvancedLogFilters));

                // Restore state without starting services from inside loading.
                OnPropertyChanged(nameof(IsTunMode));
                OnPropertyChanged(nameof(RoutingMode));
                OnPropertyChanged(nameof(IsSystemProxyEnabled));
                OnPropertyChanged(nameof(TunModeStatus));

                Presets.Clear();
                if (_config.Presets != null) _config.Presets.ForEach(p => Presets.Add(p));

                if (_proxyService != null)
                {
                    _proxyService.ProxyCheckUrl = ProxyCheckUrl;
                    _proxyService.ProxySpeedTestUrl = ProxySpeedTestUrl;
                }
                OnPropertyChanged(nameof(ProxyCheckUrl));
                OnPropertyChanged(nameof(ProxySpeedTestUrl));

                IsProxyRunning = d.IsProxyRunning;
            }
            finally
            {
                _suppressSave = false;
            }

            if (isFirstRunForNewVersion)
            {
                _lastSeenVersion = CurrentVersion;
                try { _settingsService.Save(CreateSettingsSnapshot()); } catch { }
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    OpenWhatsNewModal(CurrentVersion);
                }), DispatcherPriority.Loaded);
            }
        }



        private async Task ToggleTunModeAsync()
        {
            if (_isTunMode)
            {
                // Ensure the main proxy service is running (as SOCKS5 receiver)
                if (!IsProxyRunning)
                {
                    try
                    {
                        _proxyService.Start();
                        IsProxyRunning = true;
                        UpdateDnsServiceState();
                    }
                    catch (Exception ex)
                    {
                        ShowMessage("Error", $"Failed to start local proxy service: {ex.Message}");
                        IsTunMode = false; // Revert
                        return;
                    }
                }

                // Start TUN (sing-box) pointing to local proxy
                var tunConfig = new TunService.TunRulesConfig
                {
                    Mode = _config.CurrentMode,
                    Rules = GetRulesForMode(_config.CurrentMode),
                    ProxyType = (_config.CurrentMode == RuleMode.BlackList) ? (SelectedBlackListMainProxy?.Type ?? ProxyType.Http) : ProxyType.Http
                };
                _tunRefreshCts?.Cancel();
                _tunRefreshCts?.Dispose();
                _tunRefreshCts = new CancellationTokenSource();
                var token = _tunRefreshCts.Token;
                var success = await _tunService.StartAsync(tunConfig, token);
                if (token.IsCancellationRequested || !IsTunMode) return;
                if (!success)
                {
                    string detail = string.IsNullOrWhiteSpace(_tunService.LastError)
                        ? "Unknown sing-box startup error."
                        : _tunService.LastError;
                    ShowMessage("TUN Mode Start Failed", $"TUN could not start.\n\n{detail}\n\nCheck the TUN log for details.");
                    IsTunMode = false; // Revert
                    TunStatusDescription = "Failed to start";
                }
                else
                {
                    TunStatusDescription = "Active (VPN Mode)";
                }
                OnPropertyChanged(nameof(TunModeStatus));
                OnPropertyChanged(nameof(TunStatusDescription));
            }
            else
            {
                _tunRefreshCts?.Cancel();
                await _tunService.StopAsync();
                TunStatusDescription = "Inactive";
                OnPropertyChanged(nameof(TunModeStatus));
                OnPropertyChanged(nameof(TunStatusDescription));

                // Re-enable System Proxy
                try
                {
                    // We need to ensure logic knows we are back to System Proxy
                    _proxyService.EnforceSystemProxy();
                }
                catch { }
            }
        }


        private string _tunStatusDescription = "Off";
        public string TunStatusDescription
        {
            get => _tunStatusDescription;
            set { _tunStatusDescription = value; OnPropertyChanged(); }
        }

        private void OpenBatchEditModal(string target, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            _isBatchEditMode = true;
            _batchEditTarget = target;
            _batchEditValue = value;

            if (target == "Group")
            {
                IsRenameGroupMode = true;
                ModalTitle = $"Rename Group";
                ModalSubtitle = $"Enter a new name for '{value}'";
                ModalGroupName = value;
                OnPropertyChanged(nameof(ModalGroupName));
                OnPropertyChanged(nameof(IsRenameGroupMode));

                // Clear other fields to avoid confusion (though UI will hide them)
                ModalProcessName = "";
                ModalHost = "";
            }
            else
            {
                IsRenameGroupMode = false;
                ModalTitle = $"Batch Edit {target}: {value}";
                ModalSubtitle = "Apply changes to all rules in this group/app";

                OnPropertyChanged(nameof(IsRenameGroupMode));

                var matchingRules = RulesList.Where(r =>
                    (target == "Group" && r.GroupName == value) ||
                    (target == "App" && r.TargetApps != null && r.TargetApps.Any(a => a.Equals(value, StringComparison.OrdinalIgnoreCase)))
                ).ToList();

                // Aggregate Apps and Hosts for display
                var distinctApps = matchingRules.SelectMany(r => r.TargetApps ?? Enumerable.Empty<string>()).Distinct().ToList();
                var distinctHosts = matchingRules.SelectMany(r => r.TargetHosts ?? Enumerable.Empty<string>()).Distinct().ToList();

                ModalProcessName = string.Join("; ", distinctApps);
                ModalHost = string.Join("; ", distinctHosts);

                var exemplar = matchingRules.FirstOrDefault();

                if (exemplar != null)
                {
                    ModalAction = exemplar.Action;
                    ModalBlockDirection = exemplar.BlockDirection;
                    ModalSelectedProxy = Proxies.FirstOrDefault(p => p.Id == exemplar.ProxyId);
                    ModalTargetMode = GetRuleMode(exemplar);
                    ModalIsTemporary = exemplar.IsTemporary;

                    ModalIsScheduleEnabled = exemplar.IsScheduleEnabled;
                    ModalTimeStart = exemplar.TimeStart ?? "";
                    ModalTimeEnd = exemplar.TimeEnd ?? "";
                }
                else
                {
                    ModalAction = RuleAction.Proxy;
                    ModalSelectedProxy = Proxies.FirstOrDefault();

                    ModalIsScheduleEnabled = false;
                    ModalTimeStart = "";
                    ModalTimeEnd = "";
                    ModalIsTemporary = false;
                }
            }

            OnPropertyChanged(nameof(ModalProcessName));
            OnPropertyChanged(nameof(ModalHost));

            IsModalVisible = true;
            OnPropertyChanged(nameof(IsEditMode));
        }

        private void RequestConfirmDelete(string target, string value)
        {
            _batchEditTarget = target;
            _batchEditValue = value;
            ShowConfirmation("Delete Rules", $"Are you sure you want to delete all rules for {target} '{value}'?", () => ExecuteConfirmAction());
        }

        private void ExecuteConfirmAction()
        {
            if (_batchEditTarget == "Group")
            {
                var targets = RulesList.Where(r => r.GroupName == _batchEditValue).ToList();
                foreach (var r in targets)
                {
                    RemoveRuleFromStorage(r);
                    RulesList.Remove(r);
                }
                SelectedAppName = null;
                SelectedGroupName = null;
            }
            else if (_batchEditTarget == "App")
            {
                var targets = RulesList.Where(r => r.TargetApps.Contains(_batchEditValue)).ToList();
                foreach (var r in targets)
                {
                    RemoveRuleFromStorage(r);
                    RulesList.Remove(r);
                }
                SelectedAppName = null;
            }

            IsConfirmModalVisible = false;
            RequestSaveSettings();
            RefreshRuleGroups();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private string _latestVersion;
        public string LatestVersion
        {
            get => _latestVersion;
            set { _latestVersion = value; OnPropertyChanged(); }
        }

        private static T DeepClone<T>(T value)
        {
            var json = JsonSerializer.Serialize(value);
            return JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidOperationException($"Could not clone {typeof(T).Name}.");
        }

        private AppProfile CreateProfileSnapshot(string name, string? existingId = null)
        {
            EnsureProxyNames(Proxies);
            _config.Presets = Presets.ToList();
            return new AppProfile
            {
                Id = existingId ?? Guid.NewGuid().ToString(),
                Name = name,
                CurrentMode = _config.CurrentMode,
                BlackListSelectedProxyId = _config.BlackListSelectedProxyId,
                TunProxyId = _config.TunProxyId,
                RoutingMode = _config.RoutingMode,
                Proxies = DeepClone(Proxies.ToList()),
                BlackListRules = DeepClone(_config.BlackListRules),
                WhiteListRules = DeepClone(_config.WhiteListRules),
                Presets = DeepClone(Presets.ToList())
            };
        }

        private void LoadProfilesFromSettings(AppSettings settings)
        {
            Profiles.Clear();
            foreach (var profile in settings.Profiles ?? new List<AppProfile>())
            {
                profile.Proxies ??= new List<ProxyItem>();
                profile.BlackListRules ??= new List<TrafficRule>();
                profile.WhiteListRules ??= new List<TrafficRule>();
                profile.Presets ??= new List<RulePreset>();
                EnsureProxyNames(profile.Proxies);
                Profiles.Add(profile);
            }

            _activeProfileId = settings.ActiveProfileId;
            if (!Profiles.Any(p => p.Id == _activeProfileId)) _activeProfileId = null;
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == _activeProfileId) ?? Profiles.FirstOrDefault();
            OnPropertyChanged(nameof(ActiveProfileName));
        }

        private void SaveProfile()
        {
            string name = ProfileName.Trim();
            if (name.Length == 0)
            {
                ShowMessage("Profile Name Required", "Enter a name for the profile.");
                return;
            }

            var existing = Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var snapshot = CreateProfileSnapshot(name, existing?.Id);
            if (existing == null)
            {
                Profiles.Add(snapshot);
            }
            else
            {
                Profiles[Profiles.IndexOf(existing)] = snapshot;
            }

            SelectedProfile = snapshot;
            _activeProfileId = snapshot.Id;
            OnPropertyChanged(nameof(ActiveProfileName));
            RequestSaveSettings();
            ShowMessage("Profile Saved", $"Profile '{name}' now contains {snapshot.Proxies.Count} proxies, {snapshot.BlackListRules.Count + snapshot.WhiteListRules.Count} rules and {snapshot.Presets.Count} presets.");
        }

        private void CreateEmptyProfile()
        {
            string baseName = string.IsNullOrWhiteSpace(ProfileName) ? "New Profile" : ProfileName.Trim();
            string name = baseName;
            int suffix = 2;
            while (Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                name = $"{baseName} ({suffix++})";
            }

            var profile = new AppProfile
            {
                Name = name,
                CurrentMode = RuleMode.BlackList,
                Proxies = new List<ProxyItem>(),
                BlackListRules = new List<TrafficRule>(),
                WhiteListRules = new List<TrafficRule>(),
                Presets = new List<RulePreset>()
            };

            Profiles.Add(profile);
            SelectedProfile = profile;
            ProfileName = name;
            ApplyProfile(profile);
        }

        private void LoadProfile()
        {
            var selected = SelectedProfile;
            if (selected == null) return;
            ShowConfirmation(
                "Activate Profile",
                $"Activate profile '{selected.Name}'? Current proxies, rules and presets will be replaced.",
                () => ApplyProfile(selected));
        }

        private void ApplyProfile(AppProfile source)
        {
            var profile = DeepClone(source);
            bool restoreSystemProxy = false;
            _suppressSave = true;
            try
            {
                _temporaryBlackListRules.Clear();
                _temporaryWhiteListRules.Clear();

                Proxies.Clear();
                EnsureProxyNames(profile.Proxies);
                foreach (var proxy in profile.Proxies)
                {
                    SubscribeToItem(proxy);
                    Proxies.Add(proxy);
                }

                _config.CurrentMode = profile.CurrentMode;
                _config.BlackListRules = profile.BlackListRules;
                _config.WhiteListRules = profile.WhiteListRules;
                _config.Presets = profile.Presets;
                _config.BlackListSelectedProxyId = profile.BlackListSelectedProxyId;
                if (_config.BlackListSelectedProxyId.HasValue &&
                    !Proxies.Any(p => p.Id == _config.BlackListSelectedProxyId.Value.ToString()))
                {
                    _config.BlackListSelectedProxyId = null;
                }

                _config.TunProxyId = Proxies.Any(p => p.Id == profile.TunProxyId) ? profile.TunProxyId : null;
                // Older profiles did not own the routing mode, so preserve the
                // currently selected mode when their field is absent.
                if (profile.RoutingMode.HasValue)
                    _config.RoutingMode = profile.RoutingMode;
                NormalizeRoutingMode();
                _tunProxy = Proxies.FirstOrDefault(p => p.Id == _config.TunProxyId);

                Presets.Clear();
                foreach (var preset in profile.Presets) Presets.Add(preset);
                SelectedPreset = Presets.FirstOrDefault();

                SelectedProxy = Proxies.FirstOrDefault();
                _activeProfileId = source.Id;
                ProfileName = source.Name;
                ReloadRulesForCurrentMode();
            }
            finally
            {
                _suppressSave = false;
            }

            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == source.Id);
            OnPropertyChanged(nameof(ActiveProfileName));
            OnPropertyChanged(nameof(SelectedBlackListMainProxy));
            OnPropertyChanged(nameof(TunProxy));
            OnPropertyChanged(nameof(CanEnableTunMode));
            OnPropertyChanged(nameof(ExistingGroups));

            if (IsTunMode && !CanEnableTunMode)
            {
                _ = _tunService.StopAsync();
                _isTunMode = false;
                _config.IsTunMode = false;
                _config.RoutingMode = ProxyRoutingMode.SystemProxy;
                _config.IsSystemProxyEnabled = true;
                TunStatusDescription = "Inactive";
                OnPropertyChanged(nameof(IsTunMode));
                OnPropertyChanged(nameof(TunModeStatus));
                OnPropertyChanged(nameof(TunStatusDescription));
            }

            ApplyConfig();

            if (restoreSystemProxy && IsProxyRunning) _proxyService.EnforceSystemProxy();

            if (IsTunMode)
            {
                _ = _tunService.StopAsync();
                var tunConfig = CreateTunRulesConfig();
                _ = _tunService.StartAsync(tunConfig);
            }

            RequestSaveSettings();
            ShowMessage("Profile Activated", $"Profile '{source.Name}' is active.");
        }

        private void DeleteProfile()
        {
            var selected = SelectedProfile;
            if (selected == null) return;
            ShowConfirmation("Delete Profile", $"Delete profile '{selected.Name}'?", () =>
            {
                Profiles.Remove(selected);
                if (_activeProfileId == selected.Id) _activeProfileId = null;
                SelectedProfile = Profiles.FirstOrDefault();
                OnPropertyChanged(nameof(ActiveProfileName));
                RequestSaveSettings();
            });
        }



        private void SavePreset()
        {
            if (string.IsNullOrWhiteSpace(PresetName)) return;

            var preset = new RulePreset
            {
                Name = PresetName,
                Mode = _config.CurrentMode,
                Rules = new List<TrafficRule>(_config.CurrentMode == RuleMode.BlackList ? _config.BlackListRules : _config.WhiteListRules)
                    .Select(r => new TrafficRule
                    {
                        // Deep copy key properties
                        Action = r.Action,
                        BlockDirection = r.BlockDirection,
                        GroupName = r.GroupName,
                        IsEnabled = r.IsEnabled,
                        ProxyId = r.ProxyId,
                        TargetApps = new List<string>(r.TargetApps ?? new List<string>()),
                        TargetHosts = new List<string>(r.TargetHosts ?? new List<string>()),

                        ScheduleStart = r.ScheduleStart,
                        ScheduleEnd = r.ScheduleEnd,
                        ScheduleDays = r.ScheduleDays
                    }).ToList()
            };

            // Check if name exists, update if so
            var existing = Presets.FirstOrDefault(p => p.Name.Equals(PresetName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // Update implementation
                int idx = Presets.IndexOf(existing);
                Presets[idx] = preset;
            }
            else
            {
                Presets.Add(preset);
            }

            // Sync with Config
            _config.Presets = Presets.ToList();
            RequestSaveSettings();
            ShowMessage("Preset Saved", $"Rules saved to preset '{PresetName}'");
        }

        private void LoadPreset()
        {
            if (SelectedPreset == null) return;

            ShowConfirmation("Load Preset", $"Load preset '{SelectedPreset.Name}'? This will replace current {(_config.CurrentMode == RuleMode.BlackList ? "Blacklist" : "Whitelist")} rules.", () =>
            {
                // Optionally switch mode to match preset
                if (SelectedPreset.Mode != _config.CurrentMode)
                {
                    // If you want to force switch mode:
                    // IsBlackListMode = SelectedPreset.Mode == RuleMode.BlackList;
                    // For now, let's just load rules into CURRENT mode, or maybe respect preset mode?
                    // Let's safe load into current mode context for now, or warn user.
                    // Actually, let's just REPLACE current rules.
                }

                var newRules = SelectedPreset.Rules.Select(r => new TrafficRule
                {
                    Action = r.Action,
                    BlockDirection = r.BlockDirection,
                    GroupName = r.GroupName,
                    IsEnabled = r.IsEnabled,
                    ProxyId = r.ProxyId,
                    TargetApps = new List<string>(r.TargetApps ?? new List<string>()),
                    TargetHosts = new List<string>(r.TargetHosts ?? new List<string>()),

                    ScheduleStart = r.ScheduleStart,
                    ScheduleEnd = r.ScheduleEnd,
                    ScheduleDays = r.ScheduleDays
                }).ToList();

                // Update Config
                if (_config.CurrentMode == RuleMode.BlackList)
                    _config.BlackListRules = newRules;
                else
                    _config.WhiteListRules = newRules;

                ReloadRulesForCurrentMode();
                ApplyConfig();
                RequestSaveSettings();
                RefreshRuleGroups();
            });
        }

        private void DeletePreset(RulePreset? preset)
        {
            if (preset == null) return;
            ShowConfirmation("Delete Preset", $"Are you sure you want to delete preset '{preset.Name}'?", () =>
            {
                Presets.Remove(preset);
                _config.Presets = Presets.ToList();
                RequestSaveSettings();
            });
        }



        private TimeSpan? ParseTime(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            if (TimeSpan.TryParse(t.Trim(), out var ts)) return ts;
            // Fallback: try DateTime parsing just in case locale is weird
            if (DateTime.TryParse(t.Trim(), out var dt)) return dt.TimeOfDay;
            return null;
        }

        private void ToggleGroupRules(object? param)
        {
            string? groupName = param as string ?? (param as RuleGroupInfo)?.GroupName;
            if (string.IsNullOrEmpty(groupName) || RulesList == null) return;

            var rules = RulesList.Where(r => (r.GroupName ?? "General") == groupName).ToList();
            if (!rules.Any()) return;

            bool targetState = !rules.Any(r => r.IsEnabled);
            foreach (var r in rules)
            {
                r.IsEnabled = targetState;
            }
            RefreshRuleGroups();
            OnPropertyChanged(nameof(SelectedGroupApps));
            OnPropertyChanged(nameof(SelectedGroupRules));
            ApplyConfig();
            RequestSaveSettings();
        }

        private void ToggleAppRules(object? param)
        {
            string? appName = param as string ?? (param as AppRuleInfo)?.AppName;
            if (string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(_selectedGroupName) || RulesList == null) return;

            var rules = RulesList.Where(r => (r.GroupName ?? "General") == _selectedGroupName && (r.TargetApps?.Contains(appName) ?? false)).ToList();
            if (!rules.Any()) return;

            bool targetState = !rules.Any(r => r.IsEnabled);
            foreach (var r in rules)
            {
                r.IsEnabled = targetState;
            }
            RefreshRuleGroups();
            OnPropertyChanged(nameof(SelectedGroupApps));
            OnPropertyChanged(nameof(SelectedGroupRules));
            ApplyConfig();
            RequestSaveSettings();
        }

        private void OnTunTrafficObserved(TunService.TunTrafficEvent e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(e.Host)) return;

                string cleanHost = e.Host.Trim();
                int colonIndex = cleanHost.LastIndexOf(':');
                if (colonIndex > 0)
                {
                    if (cleanHost.StartsWith("[") && cleanHost.Contains("]"))
                    {
                        int closeBracket = cleanHost.IndexOf(']');
                        if (colonIndex > closeBracket)
                            cleanHost = cleanHost.Substring(1, closeBracket - 1);
                    }
                    else if (!cleanHost.Contains("::"))
                    {
                        cleanHost = cleanHost.Substring(0, colonIndex);
                    }
                }

                if (string.IsNullOrWhiteSpace(cleanHost)) return;

                // Filter noise
                if (cleanHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    cleanHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    cleanHost.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
                    cleanHost.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                    cleanHost.StartsWith("172.19.0.", StringComparison.OrdinalIgnoreCase) ||
                    cleanHost.StartsWith("224.0.0.", StringComparison.OrdinalIgnoreCase) ||
                    cleanHost.StartsWith("239.255.", StringComparison.OrdinalIgnoreCase) ||
                    cleanHost.Equals("255.255.255.255", StringComparison.OrdinalIgnoreCase) ||
                    cleanHost.StartsWith("ff02:", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string procName = string.IsNullOrWhiteSpace(e.ProcessName) ? "TUN System" : e.ProcessName;
                ImageSource? icon = !string.IsNullOrWhiteSpace(e.ProcessName) ? IconHelper.GetIconByProcessName(e.ProcessName) : null;

                TrafficType trafficType = e.Network.Equals("udp", StringComparison.OrdinalIgnoreCase)
                    ? (e.Port == 53 ? TrafficType.DNS : (e.Port == 3478 || e.Port == 19302 ? TrafficType.WebRTC : TrafficType.UDP))
                    : (e.Port == 443 ? TrafficType.HTTPS : TrafficType.TCP);

                bool isBlocked = !string.IsNullOrEmpty(e.OutboundTag) && e.OutboundTag.Contains("block", StringComparison.OrdinalIgnoreCase);
                string result = isBlocked ? "Blocked" : (!string.IsNullOrEmpty(e.OutboundTag) && e.OutboundTag.Contains("direct", StringComparison.OrdinalIgnoreCase) ? "Direct" : "Proxied");
                string color = isBlocked ? "#EF4444" : "#10B981";

                var log = new ConnectionLog
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    ProcessName = procName,
                    ProcessPath = e.ProcessPath ?? "",
                    Host = cleanHost,
                    Result = result,
                    Color = color,
                    AppIcon = icon,
                    Type = trafficType
                };
                OnLogReceived(log);

                var historyItem = _trafficMonitorService.CreateConnectionItem(
                    procName,
                    icon,
                    cleanHost,
                    result,
                    "TUN",
                    null,
                    color,
                    trafficType,
                    e.ProcessPath ?? "");
                _trafficMonitorService.CompleteConnection(historyItem);
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("TUN", $"Error handling TUN traffic: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            try
            {
                _monitorPeriodCts?.Cancel();
                _monitorPeriodCts?.Dispose();
                _monitorPeriodCts = null;
                _connectionLogTimer.Stop();
                _proxyService.OnConnectionLog -= OnLogReceived;
                _dnsProxyService.OnConnectionLog -= OnLogReceived;
                _trafficMonitorService.ConnectionCreated -= OnMonitorConnectionCreated;
                _trafficMonitorService.OverallStatsUpdated -= OnOverallStatsUpdated;
                _tunService.TrafficObserved -= OnTunTrafficObserved;
                _connectionIconQueue.Writer.TryComplete();
                _connectionIconCts.Cancel();
                _proxyService?.Stop();
                _dnsProxyService?.Stop();
                _tunService?.Stop();
                _siteIconCacheService.Dispose();
            }
            catch { }
        }
    }

    public class RelayCommand : ICommand { private Action<object> e; public RelayCommand(Action<object> e) => this.e = e; public event EventHandler? CanExecuteChanged; public bool CanExecute(object? p) => true; public void Execute(object? p) => e(p!); }
}
