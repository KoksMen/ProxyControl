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
        private readonly SettingsService _settingsService;
        private readonly GithubUpdateService _updateService;
        private readonly TrafficMonitorService _trafficMonitorService;

        private AppConfig _config;
        private CancellationTokenSource _enforceCts;
        private bool _suppressSave = false;

        public ObservableCollection<ProxyItem> Proxies { get; set; } = new ObservableCollection<ProxyItem>();
        public ObservableCollection<TrafficRule> RulesList { get; set; } = new ObservableCollection<TrafficRule>();
        public ObservableCollection<ConnectionLog> Logs { get; set; } = new ObservableCollection<ConnectionLog>();

        // --- Monitor & Filtering Properties ---
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
        // --------------------------------------

        public ICollectionView RulesView { get; private set; }

        public string ToggleProxyMenuText => IsProxyRunning ? "Turn Proxy OFF" : "Turn Proxy ON";
        public string AppVersion => "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        // --- UPDATE Modal Fields ---
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
        // ---------------------------

        // --- Create Rule (Settings Tab) ---
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

        // --- RULE Modal Fields ---
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

        private System.Windows.Media.ImageSource? _modalIcon;

        // --- PROXY Modal Fields ---
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

        private ProxyItem? _editingProxyItem;

        // --- End Modal Fields ---

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                OnPropertyChanged();
                RulesView.Refresh();
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
                SaveSettings();
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
                SaveSettings();
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
                SaveSettings();
            }
        }

        public Array ActionTypes => Enum.GetValues(typeof(RuleAction));
        public Array ModeTypes => Enum.GetValues(typeof(RuleMode));
        public Array TrafficPeriodModes => Enum.GetValues(typeof(TrafficPeriodMode));
        public Array BlockDirectionTypes => Enum.GetValues(typeof(BlockDirection));

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
                SaveSettings();
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

        public ICommand ApplyFilterCommand { get; }
        public ICommand TraySelectProxyCommand { get; }
        public ICommand TraySetBlackListModeCommand { get; }
        public ICommand TraySetWhiteListModeCommand { get; }

        public MainViewModel()
        {
            _trafficMonitorService = new TrafficMonitorService();
            _proxyService = new TcpProxyService(_trafficMonitorService);
            _settingsService = new SettingsService();
            _updateService = new GithubUpdateService();
            _config = new AppConfig();

            _proxyService.OnConnectionLog += OnLogReceived;

            Proxies.CollectionChanged += OnCollectionChanged;
            RulesList.CollectionChanged += OnCollectionChanged;

            RulesView = CollectionViewSource.GetDefaultView(RulesList);
            RulesView.GroupDescriptions.Add(new PropertyGroupDescription("GroupName"));
            RulesView.GroupDescriptions.Add(new PropertyGroupDescription("AppKey"));
            RulesView.Filter = FilterRules;

            OpenAddProxyModalCommand = new RelayCommand(_ => OpenProxyModal(null));
            OpenEditProxyModalCommand = new RelayCommand(p => OpenProxyModal((ProxyItem)p));
            SaveProxyModalCommand = new RelayCommand(_ => SaveProxyFromModal());
            CloseProxyModalCommand = new RelayCommand(_ => IsProxyModalVisible = false);

            PasteProxyCommand = new RelayCommand(_ => PasteProxy());
            RemoveProxyCommand = new RelayCommand(_ => RemoveProxy());
            SaveChangesCommand = new RelayCommand(_ => SaveSettings());
            CheckProxyCommand = new RelayCommand(_ => CheckSelectedProxy());
            AddRuleCommand = new RelayCommand(_ => AddRule());
            RemoveRuleCommand = new RelayCommand(_ => RemoveRule());

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

            ApplyFilterCommand = new RelayCommand(_ => ApplyFilter());

            TraySelectProxyCommand = new RelayCommand(p => SelectedBlackListMainProxy = (ProxyItem)p);
            TraySetBlackListModeCommand = new RelayCommand(_ => IsBlackListMode = true);
            TraySetWhiteListModeCommand = new RelayCommand(_ => IsBlackListMode = false);

            LoadSettings();
            _proxyService.Start();
            ApplyConfig();
            IsProxyRunning = true;
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

                if (SelectedPeriodMode == TrafficPeriodMode.Today)
                {
                    start = DateTime.Today;
                    end = DateTime.Today;
                }
                else if (SelectedPeriodMode == TrafficPeriodMode.Yesterday)
                {
                    start = DateTime.Today.AddDays(-1);
                    end = DateTime.Today.AddDays(-1);
                }
                else if (SelectedPeriodMode == TrafficPeriodMode.CustomRange)
                {
                    start = FilterDateStart.Date;
                    end = FilterDateEnd.Date;

                    if (TimeSpan.TryParse(FilterTimeStart, out var ts)) timeStart = ts;
                    if (TimeSpan.TryParse(FilterTimeEnd, out var te)) timeEnd = te;
                }

                await _trafficMonitorService.LoadHistoryAsync(start, end, timeStart, timeEnd);
            }
        }

        private async Task PerformUpdateCheck(bool silent)
        {
            await _updateService.CheckAndInstallUpdate(
                onProgress: (status, details, percent) =>
                {
                    IsUpdateModalVisible = true;
                    UpdateStatusText = status;
                    UpdateDetailText = details;
                    UpdateProgress = percent;
                },
                onCompleted: () =>
                {
                    UpdateStatusText = "Update complete!";
                    UpdateDetailText = "Restarting...";
                    UpdateProgress = 100;
                },
                silent: silent
            );
        }

        private void OpenProxyModal(ProxyItem? item)
        {
            _editingProxyItem = item;
            if (item == null)
            {
                ProxyModalTitle = "Add Proxy";
                ProxyModalIp = "";
                ProxyModalPort = 8080;
                ProxyModalUser = "";
                ProxyModalPass = "";
            }
            else
            {
                ProxyModalTitle = "Edit Proxy";
                ProxyModalIp = item.IpAddress;
                ProxyModalPort = item.Port;
                ProxyModalUser = item.Username;
                ProxyModalPass = item.Password;
            }
            IsProxyModalVisible = true;
        }

        private void SaveProxyFromModal()
        {
            if (_editingProxyItem == null)
            {
                var newProxy = new ProxyItem
                {
                    IpAddress = ProxyModalIp,
                    Port = ProxyModalPort,
                    Username = ProxyModalUser,
                    Password = ProxyModalPass,
                    IsEnabled = true,
                    Status = "New"
                };
                Proxies.Add(newProxy);
                SelectedProxy = newProxy;
                _ = CheckSingleProxy(newProxy);
            }
            else
            {
                _editingProxyItem.IpAddress = ProxyModalIp;
                _editingProxyItem.Port = ProxyModalPort;
                _editingProxyItem.Username = ProxyModalUser;
                _editingProxyItem.Password = ProxyModalPass;
                _editingProxyItem.Status = "Updated";
                _ = CheckSingleProxy(_editingProxyItem);
            }

            IsProxyModalVisible = false;
            SaveSettings();
        }

        private void OpenRuleModal(object obj)
        {
            if (obj is ConnectionLog log)
            {
                ModalProcessName = log.ProcessName;
                ModalHost = log.Host;
                _modalIcon = log.AppIcon;
            }
            else if (obj is ConnectionHistoryItem historyItem)
            {
                ModalProcessName = historyItem.ProcessName;
                ModalHost = historyItem.Host;
                _modalIcon = IconHelper.GetIconByProcessName(historyItem.ProcessName);
            }
            else return;

            ModalAction = RuleAction.Proxy;
            ModalBlockDirection = BlockDirection.Both;
            ModalSelectedProxy = Proxies.FirstOrDefault(p => p.IsEnabled) ?? Proxies.FirstOrDefault();
            ModalTargetMode = IsBlackListMode ? RuleMode.BlackList : RuleMode.WhiteList;

            IsModalVisible = true;
        }

        private void SaveRuleFromModal()
        {
            string group = "QuickRules";
            var app = ModalProcessName;
            var host = ModalHost;
            string? iconBase64 = _modalIcon != null ? IconHelper.ImageSourceToBase64(_modalIcon) : null;

            var rule = new TrafficRule
            {
                TargetApps = new List<string> { app },
                TargetHosts = new List<string> { host },
                IsEnabled = true,
                Action = ModalAction,
                BlockDirection = ModalBlockDirection,
                GroupName = group,
                ProxyId = (ModalAction == RuleAction.Proxy && ModalSelectedProxy != null) ? ModalSelectedProxy.Id : null,
                AppIcon = _modalIcon,
                IconBase64 = iconBase64
            };

            if (ModalTargetMode == RuleMode.BlackList)
                _config.BlackListRules.Add(rule);
            else
                _config.WhiteListRules.Add(rule);

            bool isCurrentModeView = (IsBlackListMode && ModalTargetMode == RuleMode.BlackList) ||
                                     (!IsBlackListMode && ModalTargetMode == RuleMode.WhiteList);

            if (isCurrentModeView)
            {
                SubscribeToItem(rule);
                RulesList.Add(rule);
            }

            SaveSettings();
            IsModalVisible = false;
        }

        private async Task CheckAllProxies()
        {
            var proxyList = Proxies.ToList();
            if (proxyList.Count == 0) return;

            using (var semaphore = new SemaphoreSlim(3))
            {
                var tasks = proxyList.Select(async p =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        Application.Current.Dispatcher.Invoke(() => p.Status = "Checking...");
                        await CheckSingleProxy(p);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }
        }

        private bool FilterRules(object obj)
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            if (obj is TrafficRule rule)
            {
                string search = SearchText.ToLower();
                bool inGroup = rule.GroupName.ToLower().Contains(search);
                bool inApps = rule.TargetApps.Any(a => a.ToLower().Contains(search));
                bool inHosts = rule.TargetHosts.Any(h => h.ToLower().Contains(search));
                return inGroup || inApps || inHosts;
            }
            return false;
        }

        private void OnLogReceived(ConnectionLog log)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Logs.Insert(0, log);
                if (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
            });
        }

        private void StartEnforcementLoop()
        {
            _enforceCts?.Cancel();
            _enforceCts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                while (!_enforceCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(5000);
                    if (IsProxyRunning)
                    {
                        _proxyService.EnforceSystemProxy();
                    }
                }
            }, _enforceCts.Token);
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var triggers = new HashSet<string>
            {
                nameof(TrafficRule.IsEnabled), nameof(TrafficRule.ProxyId), nameof(TrafficRule.TargetApps),
                nameof(TrafficRule.TargetHosts), nameof(TrafficRule.Action), nameof(TrafficRule.GroupName),
                nameof(TrafficRule.BlockDirection),
                nameof(ProxyItem.IsEnabled), nameof(ProxyItem.IpAddress), nameof(ProxyItem.Port),
                nameof(ProxyItem.Username), nameof(ProxyItem.Password), nameof(ProxyItem.CountryCode),
                nameof(TrafficRule.IconBase64)
            };
            if (triggers.Contains(e.PropertyName))
                SaveSettings();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_suppressSave) return;
            if (e.NewItems != null)
                foreach (INotifyPropertyChanged item in e.NewItems) item.PropertyChanged += OnItemPropertyChanged;
            if (e.OldItems != null)
                foreach (INotifyPropertyChanged item in e.OldItems) item.PropertyChanged -= OnItemPropertyChanged;
            SaveSettings();
        }

        private void SubscribeToItem(INotifyPropertyChanged item)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            item.PropertyChanged += OnItemPropertyChanged;
        }

        private void SaveSettings()
        {
            if (_suppressSave) return;
            var data = new AppSettings
            {
                IsAutoStart = IsAutoStart,
                CheckUpdateOnStartup = CheckUpdateOnStartup,
                Proxies = Proxies.ToList(),
                Config = _config
            };
            _settingsService.Save(data);
            ApplyConfig();
        }

        private void ApplyConfig()
        {
            _proxyService.UpdateConfig(_config, Proxies.ToList());
        }

        private void AddProxy()
        {
            var p = new ProxyItem { IpAddress = "", Port = 8080, IsEnabled = true, Status = "New" };
            Proxies.Add(p);
            SelectedProxy = p;
        }

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
                        var p = new ProxyItem
                        {
                            IpAddress = parts[0],
                            Port = port,
                            IsEnabled = true,
                            Status = "Pasted"
                        };
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
                var result = MessageBox.Show(
                    $"Are you sure you want to delete proxy {SelectedProxy.IpAddress}:{SelectedProxy.Port}?\nThis will also remove all associated rules.",
                    "Confirm Deletion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    string proxyIdToRemove = SelectedProxy.Id;

                    var blackListToRemove = _config.BlackListRules.Where(r => r.ProxyId == proxyIdToRemove).ToList();
                    foreach (var rule in blackListToRemove)
                    {
                        _config.BlackListRules.Remove(rule);
                    }

                    var whiteListToRemove = _config.WhiteListRules.Where(r => r.ProxyId == proxyIdToRemove).ToList();
                    foreach (var rule in whiteListToRemove)
                    {
                        _config.WhiteListRules.Remove(rule);
                    }

                    if (_config.BlackListSelectedProxyId.ToString() == proxyIdToRemove)
                    {
                        _config.BlackListSelectedProxyId = null;
                        OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                    }

                    Proxies.Remove(SelectedProxy);
                    SelectedProxy = null;

                    ReloadRulesForCurrentMode();
                    SaveSettings();
                }
            }
        }

        private async Task CheckSingleProxy(ProxyItem p)
        {
            Application.Current.Dispatcher.Invoke(() => p.Status = "Checking...");

            var result = await Task.Run(() => _proxyService.CheckProxy(p));

            Application.Current.Dispatcher.Invoke(() =>
            {
                p.Status = result.IsSuccess ? "Online" : "Offline";
                p.PingMs = result.Ping;
                p.SpeedMbps = result.Speed;
                if (!string.IsNullOrEmpty(result.CountryCode))
                {
                    p.CountryCode = result.CountryCode;
                }
            });
        }

        private async void CheckSelectedProxy()
        {
            if (SelectedProxy != null)
                await CheckSingleProxy(SelectedProxy);
        }

        private void AddRule()
        {
            var appsList = NewRuleApps.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim())
                                      .Where(s => !string.IsNullOrEmpty(s))
                                      .OrderBy(x => x)
                                      .ToList();

            var hostsList = NewRuleHosts.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(s => s.Trim())
                                        .Where(s => !string.IsNullOrEmpty(s))
                                        .OrderBy(x => x)
                                        .ToList();

            if (!appsList.Any()) appsList.Add("*");
            if (!hostsList.Any()) hostsList.Add("*");

            string group = string.IsNullOrWhiteSpace(NewRuleGroup) ? "General" : NewRuleGroup;

            string? proxyIdToUse = null;
            if (NewRuleAction == RuleAction.Proxy)
            {
                if (IsBlackListMode)
                {
                    if (NewRuleSelectedProxy != null) proxyIdToUse = NewRuleSelectedProxy.Id;
                    else if (SelectedBlackListMainProxy != null)
                    {
                        MessageBox.Show("Please select a Proxy server for this rule.", "Proxy Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    else
                    {
                        MessageBox.Show("Please select a Proxy server.", "Proxy Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else
                {
                    if (NewRuleSelectedProxy == null)
                    {
                        MessageBox.Show("Please select a Proxy server for this rule.", "Proxy Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    proxyIdToUse = NewRuleSelectedProxy.Id;
                }
            }

            foreach (var app in appsList)
            {
                var icon = IconHelper.GetIconByProcessName(app);
                string? iconBase64 = icon != null ? IconHelper.ImageSourceToBase64(icon) : null;

                foreach (var host in hostsList)
                {
                    bool isDuplicate = RulesList.Any(r =>
                        r.GroupName == group &&
                        r.TargetApps.Count == 1 && r.TargetApps[0] == app &&
                        r.TargetHosts.Count == 1 && r.TargetHosts[0] == host
                    );

                    if (isDuplicate) continue;

                    var rule = new TrafficRule
                    {
                        TargetApps = new List<string> { app },
                        TargetHosts = new List<string> { host },
                        IsEnabled = true,
                        Action = NewRuleAction,
                        BlockDirection = NewRuleBlockDirection,
                        GroupName = group,
                        ProxyId = proxyIdToUse,
                        AppIcon = icon,
                        IconBase64 = iconBase64
                    };

                    if (IsBlackListMode)
                        _config.BlackListRules.Add(rule);
                    else
                        _config.WhiteListRules.Add(rule);

                    SubscribeToItem(rule);
                    RulesList.Add(rule);
                }
            }
        }

        private void RemoveRule()
        {
            if (SelectedRule != null)
            {
                if (IsBlackListMode)
                    _config.BlackListRules.Remove(SelectedRule);
                else
                    _config.WhiteListRules.Remove(SelectedRule);
                RulesList.Remove(SelectedRule);

                ApplyConfig();
            }
        }

        private void ReloadRulesForCurrentMode()
        {
            _suppressSave = true;
            RulesList.Clear();

            var sourceRules = IsBlackListMode ? _config.BlackListRules : _config.WhiteListRules;

            foreach (var r in sourceRules)
            {
                if (!string.IsNullOrEmpty(r.IconBase64))
                {
                    r.AppIcon = IconHelper.Base64ToImageSource(r.IconBase64);
                }
                else if (r.TargetApps.Any())
                {
                    var icon = IconHelper.GetIconByProcessName(r.TargetApps.First());
                    if (icon != null)
                    {
                        r.AppIcon = icon;
                        r.IconBase64 = IconHelper.ImageSourceToBase64(icon);
                    }
                }

                SubscribeToItem(r);
                RulesList.Add(r);
            }

            _suppressSave = false;
            RulesView.Refresh();
            OnPropertyChanged(nameof(IsBlackListMode));
        }

        private void ToggleService()
        {
            IsProxyRunning = !IsProxyRunning;
            if (IsProxyRunning) _proxyService.Start(); else _proxyService.Stop();
        }

        private void ImportConfig()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "JSON Config|*.json|All Files|*.*",
                Title = "Import Configuration"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var data = _settingsService.Load(openFileDialog.FileName);
                    if (data != null)
                    {
                        _suppressSave = true;

                        _config = data.Config ?? new AppConfig();
                        IsAutoStart = data.IsAutoStart;
                        CheckUpdateOnStartup = data.CheckUpdateOnStartup;

                        Proxies.Clear();
                        if (data.Proxies != null)
                        {
                            foreach (var p in data.Proxies)
                            {
                                SubscribeToItem(p);
                                Proxies.Add(p);
                            }
                        }

                        OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                        ReloadRulesForCurrentMode();

                        _suppressSave = false;
                        SaveSettings();
                        MessageBox.Show("Configuration imported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                        Task.Run(async () =>
                        {
                            await Task.Delay(1000);
                            await CheckAllProxies();
                        });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error importing config: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _suppressSave = false;
                }
            }
        }

        private void ExportConfig()
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "JSON Config|*.json|All Files|*.*",
                Title = "Export Configuration",
                FileName = "settings.json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    var data = new AppSettings
                    {
                        IsAutoStart = IsAutoStart,
                        CheckUpdateOnStartup = CheckUpdateOnStartup,
                        Proxies = Proxies.ToList(),
                        Config = _config
                    };
                    _settingsService.Save(data, saveFileDialog.FileName);
                    MessageBox.Show("Configuration exported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error exporting config: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LoadSettings()
        {
            _suppressSave = true;
            try
            {
                var data = _settingsService.Load();
                _config = data.Config ?? new AppConfig();
                IsAutoStart = _settingsService.IsAutoStartEnabled();
                CheckUpdateOnStartup = data.CheckUpdateOnStartup;
                Proxies.Clear();
                if (data.Proxies != null)
                {
                    foreach (var p in data.Proxies)
                    {
                        SubscribeToItem(p);
                        Proxies.Add(p);
                    }
                }
                OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                ReloadRulesForCurrentMode();
            }
            finally
            {
                _suppressSave = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RelayCommand : ICommand
    {
        private Action<object> execute;
        public RelayCommand(Action<object> execute) => this.execute = execute;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter!);
    }
}