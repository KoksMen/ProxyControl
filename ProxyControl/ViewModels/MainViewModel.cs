using Microsoft.Win32;
using ProxyControl.Models;
using ProxyControl.Services;
using ProxyControl.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

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

        private AppConfig _config;
        private CancellationTokenSource _enforceCts;
        private CancellationTokenSource? _saveDebounceCts;
        private bool _suppressSave = false;

        // TUN Mode (WebRTC/UDP bypass)
        private bool _isTunMode;
        public bool IsTunMode
        {
            get => _isTunMode;
            set
            {
                if (_isTunMode != value)
                {
                    _isTunMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TunModeStatus));
                    _ = ToggleTunModeAsync();
                }
            }
        }
        public string TunModeStatus => _isTunMode ? "🟢 TUN Active (Full UDP)" : "⚪ TUN Off";

        public IEnumerable<RuleAction> ActionTypes => Enum.GetValues(typeof(RuleAction)).Cast<RuleAction>();
        public IEnumerable<BlockDirection> BlockDirectionTypes => Enum.GetValues(typeof(BlockDirection)).Cast<BlockDirection>();

        private string _currentView = "Rules";
        public string CurrentView
        {
            get => _currentView;
            set { _currentView = value; OnPropertyChanged(); }
        }

        private string _currentVersion = "1.0.0";
        public string CurrentVersion
        {
            get => _currentVersion;
            set { _currentVersion = value; OnPropertyChanged(); }
        }

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
                    if (_config != null && _tunProxy != null)
                    {
                        _config.TunProxyId = _tunProxy.Id;
                        RequestSaveSettings();
                    }
                }
            }
        }

        public ObservableCollection<ProxyItem> Proxies { get; set; } = new ObservableCollection<ProxyItem>();
        public ObservableCollection<TrafficRule> RulesList { get; set; } = new ObservableCollection<TrafficRule>();
        public ObservableCollection<ConnectionLog> Logs { get; set; } = new ObservableCollection<ConnectionLog>();

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
                        .Select(g => new RuleGroupInfo
                        {
                            GroupName = g.Key,
                            RuleCount = g.Count(),
                            AppCount = g.SelectMany(r => r.TargetApps ?? new List<string>()).Distinct().Count(),
                            Rules = g.ToList()
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

        // Group/App Management Commands
        public ICommand EditGroupCommand { get; }
        public ICommand RemoveGroupCommand { get; }
        public ICommand EditAppCommand { get; }
        public ICommand RemoveAppCommand { get; }

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
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedGroupApps));
                    OnPropertyChanged(nameof(SelectedGroupRules));
                    OnPropertyChanged(nameof(IsGroupSelected));
                }
                catch (Exception ex)
                {
                    AppLoggerService.Instance.Error("Groups", $"SelectedGroupName setter error: {ex.Message}");
                }
            }
        }
        public bool IsGroupSelected => !string.IsNullOrEmpty(_selectedGroupName);

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
                        .Select(app => new AppRuleInfo
                        {
                            AppName = app,
                            RuleCount = RulesList.Count(r => (r.GroupName ?? "General") == _selectedGroupName &&
                                (r.TargetApps?.Contains(app) ?? false))
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
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedGroupRules));
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
            OnPropertyChanged(nameof(RuleGroups));
            OnPropertyChanged(nameof(SelectedGroupApps));
            OnPropertyChanged(nameof(SelectedGroupRules));
        }

        // Application Logs (startup, connections, errors, WebRTC blocks)
        public ObservableCollection<LogEntry> AppLogs => AppLoggerService.Instance.LogEntries;

        public ObservableCollection<ProcessTrafficData> MonitoredProcesses => _trafficMonitorService.DisplayedProcessList;

        private ProcessTrafficData? _selectedMonitorProcess;
        public ProcessTrafficData? SelectedMonitorProcess
        {
            get => _selectedMonitorProcess;
            set { _selectedMonitorProcess = value; OnPropertyChanged(); }
        }

        private TrafficPeriodMode _selectedPeriodMode = TrafficPeriodMode.LiveSession;
        public TrafficPeriodMode SelectedPeriodMode
        {
            get => _selectedPeriodMode;
            set
            {
                _selectedPeriodMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDateRangeVisible));
                ApplyFilter();
            }
        }

        public bool IsDateRangeVisible => SelectedPeriodMode == TrafficPeriodMode.CustomRange;

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

        public ICollectionView RulesView { get; private set; }

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

        private string _modalHost;
        public string ModalHost
        {
            get => _modalHost;
            set { _modalHost = value; OnPropertyChanged(); }
        }

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
                        case DnsProviderType.Google: DnsHost = "8.8.8.8"; break;
                        case DnsProviderType.Cloudflare: DnsHost = "1.1.1.1"; break;
                        case DnsProviderType.OpenDNS: DnsHost = "208.67.222.222"; break;
                        case DnsProviderType.Custom: break;
                    }
                    RequestSaveSettings();
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
                    RequestSaveSettings();
                }
            }
        }

        private bool _isProxyRunning = true;
        public bool IsProxyRunning
        {
            get => _isProxyRunning;
            set
            {
                _isProxyRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToggleProxyMenuText));
            }
        }

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
            }
        }

        public bool IsBlackListMode
        {
            get => _config.CurrentMode == RuleMode.BlackList;
            set
            {
                _config.CurrentMode = value ? RuleMode.BlackList : RuleMode.WhiteList;
                ReloadRulesForCurrentMode();
                OnPropertyChanged();
                ApplyConfig();
                RequestSaveSettings();
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
                        RequestSaveSettings();
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
                RequestSaveSettings();
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
        public Array ProxyTypes => Enum.GetValues(typeof(ProxyType));

        public ProxyItem? SelectedBlackListMainProxy
        {
            get => Proxies.FirstOrDefault(p => p.Id == _config.BlackListSelectedProxyId.ToString());
            set
            {
                if (value == null && _config.BlackListSelectedProxyId == null) return;
                _config.BlackListSelectedProxyId = new Guid(value?.Id);
                OnPropertyChanged();
                ReloadRulesForCurrentMode();
                ApplyConfig();
                RequestSaveSettings();
            }
        }

        private TrafficRule? _selectedRule;
        public TrafficRule? SelectedRule
        {
            get => _selectedRule;
            set { _selectedRule = value; OnPropertyChanged(); }
        }

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
        public ICommand CheckProxyCommand { get; }
        public ICommand AddRuleCommand { get; }
        public ICommand RemoveRuleCommand { get; }
        public ICommand ShowWindowCommand { get; }
        public ICommand ExitAppCommand { get; }
        public ICommand ToggleProxyCommand { get; }
        public ICommand ImportConfigCommand { get; }
        public ICommand ExportConfigCommand { get; }
        public ICommand CheckUpdateCommand { get; }
        public ICommand ClearLogsCommand { get; }
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
        public ICommand OpenBulkDeleteModalCommand { get; }
        public ICommand CloseDeleteModalCommand { get; }
        public ICommand DeleteAppRulesCommand { get; }
        public ICommand DeleteGroupRulesCommand { get; }
        public ICommand SelectRuleCommand { get; }

        public MainViewModel()
        {
            _trafficMonitorService = new TrafficMonitorService();
            _proxyService = new TcpProxyService(_trafficMonitorService);
            _dnsProxyService = new DnsProxyService(_trafficMonitorService);
            _tunService = new TunService();

            _settingsService = new SettingsService();
            _updateService = new GithubUpdateService();
            _settingsService = new SettingsService();
            _updateService = new GithubUpdateService();
            _config = new AppConfig();

            // Initialize version
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                CurrentVersion = $"{version.Major}.{version.Minor}.{version.Build}";

            _proxyService.OnConnectionLog += OnLogReceived;

            Proxies.CollectionChanged += OnCollectionChanged;
            RulesList.CollectionChanged += OnCollectionChanged;

            RulesView = CollectionViewSource.GetDefaultView(RulesList);
            // Single-level grouping only (removed nested GroupName to fix lag)
            RulesView.GroupDescriptions.Add(new PropertyGroupDescription("AppKey"));
            RulesView.Filter = FilterRules;

            NavigateCommand = new RelayCommand(view =>
            {
                if (view is string v) CurrentView = v;
            });

            OpenAddProxyModalCommand = new RelayCommand(_ => OpenProxyModal(null));
            OpenEditProxyModalCommand = new RelayCommand(p => OpenProxyModal((ProxyItem)p));
            SaveProxyModalCommand = new RelayCommand(_ => SaveProxyFromModal());
            CloseProxyModalCommand = new RelayCommand(_ => IsProxyModalVisible = false);

            PasteProxyCommand = new RelayCommand(_ => PasteProxy());
            RemoveProxyCommand = new RelayCommand(_ => RemoveProxy());
            SaveChangesCommand = new RelayCommand(_ => RequestSaveSettings());
            CheckProxyCommand = new RelayCommand(_ => CheckSelectedProxy());
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
                _proxyService.Stop(); // Changed from _dnsProxyService.Stop() to _proxyService.Stop()
                _dnsProxyService.Stop();
                SystemProxyHelper.RestoreSystemDns();
                MainWindow.AllowClose = true;
                Application.Current.Shutdown();
            });

            ToggleProxyCommand = new RelayCommand(_ => ToggleService());
            ImportConfigCommand = new RelayCommand(_ => ImportConfig());
            ExportConfigCommand = new RelayCommand(_ => ExportConfig());

            CheckUpdateCommand = new RelayCommand(async _ => await PerformUpdateCheck(silent: false));
            ClearLogsCommand = new RelayCommand(_ => Logs.Clear());

            OpenRuleModalCommand = new RelayCommand(obj => OpenRuleModal(obj));
            CloseModalCommand = new RelayCommand(_ => IsModalVisible = false);
            SaveModalRuleCommand = new RelayCommand(_ => SaveRuleFromModal());
            BrowseExeCommand = new RelayCommand(_ => BrowseExeFile());
            BrowseShortcutCommand = new RelayCommand(_ => BrowseShortcutFile());

            ApplyFilterCommand = new RelayCommand(_ => ApplyFilter());
            TraySelectProxyCommand = new RelayCommand(p => SelectedBlackListMainProxy = (ProxyItem)p);
            TraySetBlackListModeCommand = new RelayCommand(_ => IsBlackListMode = true);
            TraySetWhiteListModeCommand = new RelayCommand(_ => IsBlackListMode = false);
            SelectMonitorProcessCommand = new RelayCommand(p => SelectedMonitorProcess = p as ProcessTrafficData);
            EditRuleCommand = new RelayCommand(r => OpenRuleModal(r));
            BrowseAppCommand = new RelayCommand(_ => BrowseAppFile());

            // --- NEW MANAGEMENT COMMANDS ---
            EditGroupCommand = new RelayCommand(grp => OpenBatchEditModal("Group", grp as string));
            RemoveGroupCommand = new RelayCommand(grp => RequestConfirmDelete("Group", grp as string));
            EditAppCommand = new RelayCommand(app => OpenBatchEditModal("App", app as string));
            RemoveAppCommand = new RelayCommand(app => RequestConfirmDelete("App", app as string));
            SelectRuleCommand = new RelayCommand(r => SelectedRule = r as TrafficRule);


            CloseConfirmModalCommand = new RelayCommand(_ => IsConfirmModalVisible = false);
            ConfirmActionCommand = new RelayCommand(_ =>
            {
                IsConfirmModalVisible = false;
                _pendingConfirmAction?.Invoke();
            });

            // --- SAFELY INITIALIZE ---
            try
            {
                LoadSettings();
            }
            catch
            {
                _config = new AppConfig();
            }

            try
            {
                _proxyService.Start();
                IsProxyRunning = true;
                UpdateDnsServiceState();
            }
            catch (Exception ex)
            {
                IsProxyRunning = false;
                // Не показываем MessageBox здесь, так как окно может еще не загрузиться, 
                // но статус IsProxyRunning = false визуально покажет, что прокси выключен.
                System.Diagnostics.Debug.WriteLine($"Proxy Start Failed: {ex.Message}");
            }

            StartEnforcementLoop();

            Task.Run(async () =>
            {
                await Task.Delay(2000);
                await CheckAllProxies();

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

        private async void ApplyFilter()
        {
            SelectedMonitorProcess = null;
            if (SelectedPeriodMode == TrafficPeriodMode.LiveSession)
            {
                _trafficMonitorService.SwitchToLiveMode();
            }
            else
            {
                DateTime start = DateTime.Now;
                DateTime end = DateTime.Now;
                TimeSpan? timeStart = null;
                TimeSpan? timeEnd = null;
                if (SelectedPeriodMode == TrafficPeriodMode.Today) { start = DateTime.Today; end = DateTime.Today; }
                else if (SelectedPeriodMode == TrafficPeriodMode.Yesterday) { start = DateTime.Today.AddDays(-1); end = DateTime.Today.AddDays(-1); }
                else if (SelectedPeriodMode == TrafficPeriodMode.CustomRange)
                {
                    start = FilterDateStart.Date; end = FilterDateEnd.Date;
                    if (TimeSpan.TryParse(FilterTimeStart, out var ts)) timeStart = ts;
                    if (TimeSpan.TryParse(FilterTimeEnd, out var te)) timeEnd = te;
                }
                await _trafficMonitorService.LoadHistoryAsync(start, end, timeStart, timeEnd);
            }
        }

        public event Action<string, string, long> RequestShowNotification; // Tag, Url, Size

        private async Task PerformUpdateCheck(bool silent)
        {
            _updateService.OnMessage -= ShowMessage;
            _updateService.OnUpdateAvailable -= HandleUpdateAvailable;

            _updateService.OnMessage += ShowMessage;
            _updateService.OnUpdateAvailable += HandleUpdateAvailable;

            await _updateService.CheckAndInstallUpdate(null, null, silent);
        }

        private void HandleUpdateAvailable(string tagName, string url, long size)
        {
            // Play notification sound
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }

            // Check window state
            bool isVisible = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var win = Application.Current.MainWindow;
                if (win != null)
                {
                    // Considered "visible" if not minimized and actively visible
                    isVisible = win.Visibility == Visibility.Visible && win.WindowState != WindowState.Minimized;
                }
            });

            // Logic:
            // 1. If AutoStarted AND currently Minimized/Hidden -> Toast (Screen)
            // 2. Else (Manual start OR currently visible) -> Modal (Inside App)

            bool useToast = IsAutoStart && !isVisible;

            if (useToast)
            {
                // Show toast notification
                RequestShowNotification?.Invoke(tagName, url, size);
            }
            else
            {
                // Ensure App is visible for the modal
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var win = Application.Current.MainWindow;
                    if (win != null)
                    {
                        win.Show();
                        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                        win.Activate();
                    }
                });

                // Show modal
                ShowConfirmation("Update Found", $"New version {tagName} is available!\nUpdate now?", async () =>
                {
                    await ExecuteUpdate(url, size);
                });
            }
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
                ProxyModalUser = ""; ProxyModalPass = ""; ProxyModalUseTls = false; ProxyModalUseSsl = false;
                // ProxyModalType = ProxyType.Http;
            }
            else
            {
                ProxyModalTitle = "Edit Proxy"; ProxyModalIp = item.IpAddress; ProxyModalPort = item.Port;
                ProxyModalUser = item.Username; ProxyModalPass = item.Password; ProxyModalUseTls = item.UseTls; ProxyModalUseSsl = item.UseSsl;
                // ProxyModalType = item.Type;
            }
            IsProxyModalVisible = true;
        }

        private void SaveProxyFromModal()
        {
            if (_editingProxyItem == null)
            {
                var newProxy = new ProxyItem { IpAddress = ProxyModalIp, Port = ProxyModalPort, Username = ProxyModalUser, Password = ProxyModalPass, UseTls = ProxyModalUseTls, UseSsl = ProxyModalUseSsl, IsEnabled = true, Status = "New", Type = ProxyModalType };
                Proxies.Add(newProxy); SelectedProxy = newProxy; _ = CheckSingleProxy(newProxy);
            }
            else
            {
                _editingProxyItem.IpAddress = ProxyModalIp; _editingProxyItem.Port = ProxyModalPort; _editingProxyItem.Username = ProxyModalUser;
                _editingProxyItem.Password = ProxyModalPass; _editingProxyItem.UseTls = ProxyModalUseTls; _editingProxyItem.UseSsl = ProxyModalUseSsl;
                _editingProxyItem.Type = ProxyModalType;
                _editingProxyItem.Status = "Updated"; _ = CheckSingleProxy(_editingProxyItem);
            }
            IsProxyModalVisible = false; RequestSaveSettings();
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
                ModalTargetMode = _config.BlackListRules.Contains(rule) ? RuleMode.BlackList : RuleMode.WhiteList;
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
            }

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
            if (IsRenameGroupMode)
            {
                // Rename Group Logic
                if (!string.IsNullOrEmpty(_batchEditValue) && !string.IsNullOrEmpty(ModalGroupName))
                {
                    var rulesToUpdate = RulesList.Where(r => r.GroupName == _batchEditValue).ToList();
                    foreach (var rule in rulesToUpdate)
                    {
                        rule.GroupName = ModalGroupName;
                    }

                    // Also update configurations lists just in case
                    var blackListUpdates = _config.BlackListRules.Where(r => r.GroupName == _batchEditValue).ToList();
                    blackListUpdates.ForEach(r => r.GroupName = ModalGroupName);

                    var whiteListUpdates = _config.WhiteListRules.Where(r => r.GroupName == _batchEditValue).ToList();
                    whiteListUpdates.ForEach(r => r.GroupName = ModalGroupName);
                }

                _isBatchEditMode = false;
                IsRenameGroupMode = false;
                ReloadRulesForCurrentMode();
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

                    // Move to correct list if Mode changed
                    var targetList = ModalTargetMode == RuleMode.BlackList ? _config.BlackListRules : _config.WhiteListRules;
                    var currentList = IsBlackListMode ? _config.BlackListRules : _config.WhiteListRules;
                    // Note: Simplification - we assume we are editing in the current mode's view. 
                    // But if user changed "ModalTargetMode", we might need to move rules between Black/White lists.

                    bool isInBlackList = _config.BlackListRules.Contains(rule);
                    bool isInWhiteList = _config.WhiteListRules.Contains(rule);

                    if (ModalTargetMode == RuleMode.BlackList && !isInBlackList)
                    {
                        _config.WhiteListRules.Remove(rule);
                        _config.BlackListRules.Add(rule);
                    }
                    else if (ModalTargetMode == RuleMode.WhiteList && !isInWhiteList)
                    {
                        _config.BlackListRules.Remove(rule);
                        _config.WhiteListRules.Add(rule);
                    }
                }

                _isBatchEditMode = false; // Reset
                ReloadRulesForCurrentMode(); // Refresh view
            }
            else if (IsEditMode && _editingRule != null)
            {
                // Edit existing rule
                _editingRule.TargetApps = new List<string> { ModalProcessName };
                _editingRule.TargetHosts = new List<string> { ModalHost };
                _editingRule.Action = ModalAction;
                _editingRule.BlockDirection = ModalBlockDirection;
                _editingRule.GroupName = ModalGroupName;
                _editingRule.ProxyId = (ModalAction == RuleAction.Proxy && ModalSelectedProxy != null) ? ModalSelectedProxy.Id : null;
                _editingRule.AppIcon = _modalIcon;
                _editingRule.IconBase64 = _modalIcon != null ? IconHelper.ImageSourceToBase64(_modalIcon) : null;

                // Handle Move between lists if mode changed
                bool isInBlackList = _config.BlackListRules.Contains(_editingRule);
                if (ModalTargetMode == RuleMode.BlackList && !isInBlackList)
                {
                    _config.WhiteListRules.Remove(_editingRule);
                    _config.BlackListRules.Add(_editingRule);
                    RulesList.Remove(_editingRule); // Remove from current view if it was WhiteList and we are viewing WhiteList? 
                                                    // Logic is complex, easiest is to reload.
                    ReloadRulesForCurrentMode();
                }
                else if (ModalTargetMode == RuleMode.WhiteList && isInBlackList)
                {
                    _config.BlackListRules.Remove(_editingRule);
                    _config.WhiteListRules.Add(_editingRule);
                    ReloadRulesForCurrentMode();
                }
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
                            IconBase64 = icon64
                        };

                        if (ModalTargetMode == RuleMode.BlackList) _config.BlackListRules.Add(rule);
                        else _config.WhiteListRules.Add(rule);

                        bool isCurrentModeView = (IsBlackListMode && ModalTargetMode == RuleMode.BlackList) || (!IsBlackListMode && ModalTargetMode == RuleMode.WhiteList);
                        if (isCurrentModeView) { SubscribeToItem(rule); RulesList.Add(rule); }
                    }
                }
            }
            RequestSaveSettings();
            RefreshRuleGroups();
            OnPropertyChanged(nameof(ExistingGroups));
            IsModalVisible = false;
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

        private async Task CheckAllProxies()
        {
            var proxyList = Proxies.ToList(); if (proxyList.Count == 0) return;
            using (var semaphore = new SemaphoreSlim(3))
            {
                var tasks = proxyList.Select(async p => { await semaphore.WaitAsync(); try { Application.Current.Dispatcher.Invoke(() => p.Status = "Checking..."); await CheckSingleProxy(p); } finally { semaphore.Release(); } });
                await Task.WhenAll(tasks);
            }
        }

        private bool FilterRules(object obj)
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            if (obj is TrafficRule rule) { string search = SearchText.ToLower(); return rule.GroupName.ToLower().Contains(search) || rule.TargetApps.Any(a => a.ToLower().Contains(search)) || rule.TargetHosts.Any(h => h.ToLower().Contains(search)); }
            return false;
        }

        private void OnLogReceived(ConnectionLog log)
        {
            Application.Current.Dispatcher.Invoke(() => { Logs.Insert(0, log); if (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1); });
        }

        private void StartEnforcementLoop()
        {
            _enforceCts?.Cancel(); _enforceCts = new CancellationTokenSource();
            Task.Run(async () => { while (!_enforceCts.Token.IsCancellationRequested) { await Task.Delay(5000); if (IsProxyRunning) _proxyService.EnforceSystemProxy(); } }, _enforceCts.Token);
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var triggers = new HashSet<string> { nameof(TrafficRule.IsEnabled), nameof(TrafficRule.ProxyId), nameof(TrafficRule.TargetApps), nameof(TrafficRule.TargetHosts), nameof(TrafficRule.Action), nameof(TrafficRule.GroupName), nameof(TrafficRule.BlockDirection), nameof(ProxyItem.IsEnabled), nameof(ProxyItem.IpAddress), nameof(ProxyItem.Port), nameof(ProxyItem.Username), nameof(ProxyItem.Password), nameof(ProxyItem.CountryCode), nameof(ProxyItem.UseTls), nameof(ProxyItem.UseSsl), nameof(TrafficRule.IconBase64), nameof(ProxyItem.Type) };

            if (e.PropertyName == nameof(ProxyItem.Type) && sender is ProxyItem p && (p.Type == ProxyType.Socks4 || p.Type == ProxyType.Socks5))
            {
                MessageBox.Show("SOCKS4/5 proxy support is currently limited. Some features may not work as expected.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (triggers.Contains(e.PropertyName)) RequestSaveSettings();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_suppressSave) return;
            if (e.NewItems != null) foreach (INotifyPropertyChanged item in e.NewItems) item.PropertyChanged += OnItemPropertyChanged;
            if (e.OldItems != null) foreach (INotifyPropertyChanged item in e.OldItems) item.PropertyChanged -= OnItemPropertyChanged;
            RequestSaveSettings();
        }

        private void SubscribeToItem(INotifyPropertyChanged item) { item.PropertyChanged -= OnItemPropertyChanged; item.PropertyChanged += OnItemPropertyChanged; }

        private void RequestSaveSettings()
        {
            if (_suppressSave) return;
            try { ApplyConfig(); } catch { }
            _saveDebounceCts?.Cancel(); _saveDebounceCts = new CancellationTokenSource(); var token = _saveDebounceCts.Token;
            Task.Delay(500, token).ContinueWith(t => { if (t.IsCanceled) return; try { Application.Current.Dispatcher.Invoke(() => { var data = new AppSettings { IsAutoStart = IsAutoStart, CheckUpdateOnStartup = CheckUpdateOnStartup, Proxies = Proxies.ToList(), Config = _config }; _settingsService.Save(data); }); } catch { } });
        }

        private void ApplyConfig() { _proxyService.UpdateConfig(_config, Proxies.ToList()); _dnsProxyService.UpdateConfig(_config, Proxies.ToList()); }

        private void AddProxy() { var p = new ProxyItem { IpAddress = "", Port = 8080, IsEnabled = true, Status = "New" }; Proxies.Add(p); SelectedProxy = p; }

        private async void PasteProxy()
        {
            if (Clipboard.ContainsText()) { var lines = Clipboard.GetText().Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); bool added = false; foreach (var line in lines) { var parts = line.Trim().Split(':'); if (parts.Length >= 2 && int.TryParse(parts[1], out int port)) { var p = new ProxyItem { IpAddress = parts[0], Port = port, IsEnabled = true, Status = "Pasted" }; if (parts.Length >= 4) { p.Username = parts[2]; p.Password = parts[3]; } Proxies.Add(p); _ = CheckSingleProxy(p); if (!added) { SelectedProxy = p; added = true; } } } }
        }

        private void RemoveProxy()
        {
            if (SelectedProxy != null)
            {
                ShowConfirmation(
                    "Delete Proxy?",
                    $"Are you sure you want to delete proxy {SelectedProxy.IpAddress}:{SelectedProxy.Port}?",
                    () =>
                    {
                        string pid = SelectedProxy.Id;
                        var bl = _config.BlackListRules.Where(r => r.ProxyId == pid).ToList();
                        bl.ForEach(r => _config.BlackListRules.Remove(r));
                        var wl = _config.WhiteListRules.Where(r => r.ProxyId == pid).ToList();
                        wl.ForEach(r => _config.WhiteListRules.Remove(r));
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

        private async Task CheckSingleProxy(ProxyItem p)
        {
            if (p.Type == ProxyType.Socks4 || p.Type == ProxyType.Socks5)
            {
                Application.Current.Dispatcher.Invoke(() => { p.Status = "Not Supported"; p.PingMs = 0; p.SpeedMbps = 0; });
                return;
            }

            Application.Current.Dispatcher.Invoke(() => p.Status = "Checking...");
            var result = await Task.Run(() => _proxyService.CheckProxy(p));
            Application.Current.Dispatcher.Invoke(() => { p.Status = result.IsSuccess ? "Online" : "Offline"; p.PingMs = result.Ping; p.SpeedMbps = result.Speed; if (!string.IsNullOrEmpty(result.CountryCode)) p.CountryCode = result.CountryCode; });
        }

        private async void CheckSelectedProxy()
        {
            if (SelectedProxy != null)
            {
                var result = await Task.Run(() => _proxyService.CheckProxy(SelectedProxy));
                Application.Current.Dispatcher.Invoke(() => { SelectedProxy.Status = result.IsSuccess ? "Online" : "Offline"; SelectedProxy.PingMs = result.Ping; SelectedProxy.SpeedMbps = result.Speed; if (!string.IsNullOrEmpty(result.CountryCode)) SelectedProxy.CountryCode = result.CountryCode; });
                if (SelectedProxy.Status == "Online") MessageBox.Show($"Proxy {SelectedProxy.IpAddress} Online!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                else MessageBox.Show($"Proxy {SelectedProxy.IpAddress} Offline.\nError: {result.SslError}", "Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
            foreach (var app in appsList) { var icon = IconHelper.GetIconByProcessName(app); string? ib64 = icon != null ? IconHelper.ImageSourceToBase64(icon) : null; foreach (var host in hostsList) { if (RulesList.Any(r => r.GroupName == group && r.TargetApps.Contains(app) && r.TargetHosts.Contains(host))) continue; var rule = new TrafficRule { TargetApps = new List<string> { app }, TargetHosts = new List<string> { host }, IsEnabled = true, Action = NewRuleAction, BlockDirection = NewRuleBlockDirection, GroupName = group, ProxyId = pid, AppIcon = icon, IconBase64 = ib64 }; if (IsBlackListMode) _config.BlackListRules.Add(rule); else _config.WhiteListRules.Add(rule); SubscribeToItem(rule); RulesList.Add(rule); } }
        }

        private void RemoveRule(TrafficRule? rule)
        {
            var r = rule ?? SelectedRule;
            if (r != null)
            {
                if (IsBlackListMode) _config.BlackListRules.Remove(r);
                else _config.WhiteListRules.Remove(r);
                RulesList.Remove(r);
                RefreshRuleGroups();
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
                if (IsBlackListMode)
                {
                    rulesToDelete.ForEach(r => _config.BlackListRules.Remove(r));
                }
                else
                {
                    rulesToDelete.ForEach(r => _config.WhiteListRules.Remove(r));
                }

                rulesToDelete.ForEach(r => RulesList.Remove(r));

                RefreshRuleGroups();
                ApplyConfig();
                RequestSaveSettings();

                // If deleted app rules, reset app selection
                if (byApp) SelectedAppName = null;
            }
            IsDeleteModalVisible = false;
        }


        private void ReloadRulesForCurrentMode()
        {
            _suppressSave = true;
            RulesList.Clear();

            // Reset selected group when switching modes
            _selectedGroupName = null;
            OnPropertyChanged(nameof(SelectedGroupName));
            OnPropertyChanged(nameof(IsGroupSelected));

            var src = IsBlackListMode ? _config.BlackListRules : _config.WhiteListRules;
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
            RulesView.Refresh();
            RefreshRuleGroups();
            OnPropertyChanged(nameof(IsBlackListMode));
        }

        private void ToggleService()
        {
            IsProxyRunning = !IsProxyRunning;
            if (IsProxyRunning) { try { _proxyService.Start(); UpdateDnsServiceState(); } catch { IsProxyRunning = false; MessageBox.Show("Failed to start proxy on port 8000."); } }
            else { _proxyService.Stop(); _dnsProxyService.Stop(); }
        }

        private void UpdateDnsServiceState() { if (IsProxyRunning && IsDnsProtectionEnabled) _dnsProxyService.Start(); else _dnsProxyService.Stop(); }

        private void ImportConfig()
        {
            var dlg = new OpenFileDialog { Filter = "JSON|*.json" };
            if (dlg.ShowDialog() == true) { try { var d = _settingsService.Load(dlg.FileName); if (d != null) { _suppressSave = true; _config = d.Config ?? new AppConfig(); IsAutoStart = d.IsAutoStart; CheckUpdateOnStartup = d.CheckUpdateOnStartup; Proxies.Clear(); if (d.Proxies != null) d.Proxies.ForEach(p => { SubscribeToItem(p); Proxies.Add(p); }); OnPropertyChanged(nameof(SelectedBlackListMainProxy)); ReloadRulesForCurrentMode(); _suppressSave = false; RequestSaveSettings(); MessageBox.Show("Imported!"); } } catch { } }
        }

        private void ExportConfig() { var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = "settings.json" }; if (dlg.ShowDialog() == true) { try { var d = new AppSettings { IsAutoStart = IsAutoStart, CheckUpdateOnStartup = CheckUpdateOnStartup, Proxies = Proxies.ToList(), Config = _config }; _settingsService.Save(d, dlg.FileName); MessageBox.Show("Exported!"); } catch { } } }

        private void LoadSettings()
        {
            _suppressSave = true; try { var d = _settingsService.Load(); _config = d.Config ?? new AppConfig(); IsAutoStart = _settingsService.IsAutoStartEnabled(); CheckUpdateOnStartup = d.CheckUpdateOnStartup; Proxies.Clear(); if (d.Proxies != null) d.Proxies.ForEach(p => { SubscribeToItem(p); Proxies.Add(p); }); OnPropertyChanged(nameof(SelectedBlackListMainProxy)); ReloadRulesForCurrentMode(); OnPropertyChanged(nameof(IsDnsProtectionEnabled)); OnPropertyChanged(nameof(SelectedDnsProvider)); OnPropertyChanged(nameof(DnsHost)); } finally { _suppressSave = false; }
        }

        private async Task ToggleTunModeAsync()
        {
            /* TUN MODE DISABLED
            if (_isTunMode)
            {
                // TUN mode requires the main proxy service to be running
                // because we route traffic through local SOCKS5 (127.0.0.1:8000)
                if (!IsProxyRunning)
                {
                    MessageBox.Show("Please enable the main proxy service first.\nTUN mode routes traffic through the local proxy.", "TUN Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _isTunMode = false;
                    OnPropertyChanged(nameof(IsTunMode));
                    OnPropertyChanged(nameof(TunModeStatus));
                    return;
                }

                // Bypass all configured proxy IPs/domains to prevent routing loops!
                var bypassList = Proxies.Select(p => p.IpAddress).Where(ip => !string.IsNullOrEmpty(ip)).ToList();

                // Use local SOCKS5 proxy (port 8000) - this is the local TcpProxyService
                // It will then route to the correct external proxy based on rules
                var success = await _tunService.StartAsync("127.0.0.1", 8000, bypassList, null, null);
                if (!success)
                {
                    MessageBox.Show("Failed to start TUN mode. Ensure 'wintun.dll' is present in %LocalAppData%\\ProxyControl\\tun and you are running as Administrator.", "TUN Mode Start Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    _isTunMode = false;
                    TunStatusDescription = "Failed to start";
                    OnPropertyChanged(nameof(IsTunMode));
                }
                else
                {
                   TunStatusDescription = "Active (Routing via Local Rules)";
                }
                OnPropertyChanged(nameof(TunModeStatus));
                OnPropertyChanged(nameof(TunStatusDescription));
            }
            else
            {
                _tunService.Stop();
                TunStatusDescription = "Off";
                OnPropertyChanged(nameof(TunModeStatus));
                OnPropertyChanged(nameof(TunStatusDescription));
            }
            */
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
                    ModalTargetMode = _config.BlackListRules.Contains(exemplar) ? RuleMode.BlackList : RuleMode.WhiteList;
                }
                else
                {
                    ModalAction = RuleAction.Proxy;
                    ModalSelectedProxy = Proxies.FirstOrDefault();
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
                    _config.BlackListRules.Remove(r);
                    _config.WhiteListRules.Remove(r);
                    RulesList.Remove(r);
                }
            }
            else if (_batchEditTarget == "App")
            {
                var targets = RulesList.Where(r => r.TargetApps.Contains(_batchEditValue)).ToList();
                foreach (var r in targets)
                {
                    _config.BlackListRules.Remove(r);
                    _config.WhiteListRules.Remove(r);
                    RulesList.Remove(r);
                }
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

        // Command to be used by Toast
        public ICommand OpenUpdateModalCommand => new RelayCommand(async _ =>
        {
            // Re-trigger update logic for the saved latest version
            // For simplicity, we assume the toast is only shown when we have specific update info.
            // But the Toast binding needs a command. 
        });

    }

    public class RelayCommand : ICommand { private Action<object> e; public RelayCommand(Action<object> e) => this.e = e; public event EventHandler? CanExecuteChanged; public bool CanExecute(object? p) => true; public void Execute(object? p) => e(p!); }
}