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
        // private CancellationTokenSource _enforceCts; // Unused in TUN mode
        private CancellationTokenSource? _saveDebounceCts;
        private bool _suppressSave = false;
        private bool _checkUpdateOnStartup = true; // Temporary storage for initialization

        // TUN Mode (WebRTC/UDP bypass)
        // TUN Mode (WebRTC/UDP bypass) - Only available for SOCKS5 + Blacklist
        // TUN Mode logic moved to DashboardViewModel


        public RulesViewModel RulesVM { get; private set; }
        public DashboardViewModel DashboardVM { get; private set; }
        public SettingsViewModel SettingsVM { get; private set; }



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





        public ICommand SavePresetCommand { get; }
        public ICommand LoadPresetCommand { get; }
        public ICommand DeletePresetCommand { get; }

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
                    DashboardVM?.RefreshTunState();
                    if (_config != null && _tunProxy != null)
                    {
                        _config.TunProxyId = _tunProxy.Id;
                        RequestSaveSettings();
                    }

                    if (DashboardVM != null && DashboardVM.IsTunMode && !DashboardVM.CanEnableTunMode) DashboardVM.IsTunMode = false;

                }
            }
        }

        public ObservableCollection<ProxyItem> Proxies { get; set; } = new ObservableCollection<ProxyItem>();
        public ObservableCollection<TrafficRule> RulesList { get; set; } = new ObservableCollection<TrafficRule>();

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

        // Group/App Management Commands moved to RulesViewModel

        // Batch Edit State


        private bool _isRenameGroupMode;
        public bool IsRenameGroupMode
        {
            get => _isRenameGroupMode;
            set { _isRenameGroupMode = value; OnPropertyChanged(); }
        }  // GroupName or AppName





        // Application Logs moved to DashboardViewModel

        public ObservableCollection<ProcessTrafficData> MonitoredProcesses => _trafficMonitorService.DisplayedProcessList;


        private ProcessTrafficData? _selectedMonitorProcess;
        public ProcessTrafficData? SelectedMonitorProcess
        {
            get => _selectedMonitorProcess;
            set { _selectedMonitorProcess = value; OnPropertyChanged(); }
        }




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
                    OnPropertyChanged(nameof(ToggleProxyMenuText));
                    RequestSaveSettings();
                }
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
                DashboardVM?.RefreshTunState();
                ApplyConfig();
                RequestSaveSettings();

                // If switched to WhiteList, disable TUN mode
                if (!value && DashboardVM != null && DashboardVM.IsTunMode) DashboardVM.IsTunMode = false;
            }
        }



        public Array ModeTypes => Enum.GetValues(typeof(RuleMode));
        public Array TrafficPeriodModes => Enum.GetValues(typeof(TrafficPeriodMode));
        public Array ProxyTypes => Enum.GetValues(typeof(ProxyType));

        public ProxyItem? SelectedBlackListMainProxy
        {
            get => Proxies.FirstOrDefault(p => p.Id == _config.BlackListSelectedProxyId);
            set
            {
                if (value == null && _config.BlackListSelectedProxyId == null) return;

                if (value != null)
                    _config.BlackListSelectedProxyId = value.Id;
                else
                    _config.BlackListSelectedProxyId = null;

                OnPropertyChanged();
                DashboardVM?.RefreshTunState();
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
        // AddRuleCommand, RemoveRuleCommand REMOVED
        public ICommand ShowWindowCommand { get; }
        public ICommand ExitAppCommand { get; }
        public ICommand ToggleProxyCommand { get; }
        public ICommand ClearLogsCommand { get; }
        // Rule Modal Commands REMOVED
        // Rule Modal Commands REMOVED


        // DeleteAppRulesCommand REMOVED
        // DeleteGroupRulesCommand REMOVED



        public event Action<string, string, long> RequestShowNotification; // Tag, Url, Size

        public MainViewModel(
            TrafficMonitorService trafficMonitorService,
            TcpProxyService proxyService,
            DnsProxyService dnsProxyService,
            TunService tunService,
            SettingsService settingsService,
            GithubUpdateService updateService)
        {
            _trafficMonitorService = trafficMonitorService;
            _proxyService = proxyService;
            _dnsProxyService = dnsProxyService;
            _tunService = tunService;
            _settingsService = settingsService;
            _updateService = updateService;

            // Initialize commands that are not nullable but flagged
            ToggleProxyCommand = new RelayCommand(_ => { }); // Dummy init, overwritten later or used via DashboardVM
            ClearLogsCommand = new RelayCommand(_ => { }); // Dummy


            // Initialize non-nullable fields to avoid warnings
            _confirmMessage = "";

            _latestVersion = "1.0.0";
            // RequestShowNotification likely needs to be nullable or assigned a dummy delegate
            RequestShowNotification += (t, m, d) => { };

            _config = new AppConfig();

            // Initialize RulesViewModel
            RulesVM = new RulesViewModel(_settingsService, RulesList, Proxies, _config);
            RulesVM.RulesChanged += () =>
            {
                ApplyConfig();
                RequestSaveSettings();
            };
            RulesVM.RequestShowNotification += (t, m, d) => RequestShowNotification?.Invoke(t, m, d);

            // Initialize DashboardViewModel
            DashboardVM = new DashboardViewModel(_proxyService, _dnsProxyService, _tunService, _trafficMonitorService, _settingsService, _config, Proxies);

            // Initialize SettingsViewModel
            // CheckUpdateOnStartup is read from logic in LoadSettings (which populates local field/config) or we need to read it again.
            // MainViewModel field _checkUpdateOnStartup was populated.
            SettingsVM = new SettingsViewModel(_settingsService, _config, _updateService, _checkUpdateOnStartup);

            SettingsVM.SettingsChanged += RequestSaveSettings;
            SettingsVM.ImportRequested += (path) => ImportConfig(path);
            SettingsVM.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.IsDnsProtectionEnabled))
                {
                    DashboardVM?.UpdateDnsServiceState();
                }
            };


            // Initialize version
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                if (version != null)
                    CurrentVersion = $"{version.Major}.{version.Minor}.{version.Build}";

            // _proxyService.OnConnectionLog handled in DashboardViewModel



            // RulesView initialization removed as part of refactoring

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



            // DeleteAppRulesCommand removed (moved to RulesVM logic or handled differently)

            SavePresetCommand = new RelayCommand(_ => SavePreset());
            LoadPresetCommand = new RelayCommand(_ => LoadPreset());
            DeletePresetCommand = new RelayCommand(p => DeletePreset(p as RulePreset));

            // Rule logic moved to RulesVM


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
                if (DashboardVM?.IsTunMode == true) _tunService.Stop();
                SystemProxyHelper.RestoreSystemDns();
                MainWindow.AllowClose = true;
                Application.Current.Shutdown();
            });


            SettingsVM.RequestShowNotification += (tag, url, size) => RequestShowNotification?.Invoke(tag, url, size);


            // --- NEW MANAGEMENT COMMANDS ---

            // EditGroupCommand Removed (Moved to RulesVM)
            // RemoveGroupCommand Removed (Moved to RulesVM)
            // EditAppCommand Removed (Moved to RulesVM)
            // RemoveAppCommand Removed (Moved to RulesVM)


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

            // --- SAFELY INITIALIZE ---
            try
            {
                LoadSettings();

                // Validate TUN Mode on startup (must be after LoadSettings)
                if (_config.IsTunMode)
                {
                    // Check if conditions are met: Blacklist Mode + SOCKS5 Proxy
                    bool isSocks5 = false;
                    var tunProxyId = _config.TunProxyId;
                    // If TunProxyId is null/empty, it might default to first proxy, but let's check explicit or main proxy
                    // Actually, TunProxy property uses _tunProxy ?? Proxies.FirstOrDefault()
                    // Let's resolve the actual proxy being used for TUN

                    var proxy = Proxies.FirstOrDefault(p => p.Id == tunProxyId) ?? Proxies.FirstOrDefault();

                    if (proxy != null && proxy.Type == ProxyType.Socks5) isSocks5 = true;

                    if (!IsBlackListMode || !isSocks5)
                    {
                        _config.IsTunMode = false;
                        _config.IsTunMode = false;
                        DashboardVM?.RefreshTunState();

                    }
                }
            }
            catch
            {
                _config = new AppConfig();
            }

            try
            {
                _proxyService.Start();
                if (DashboardVM != null) DashboardVM.IsProxyRunning = true; // Sync
                if (DashboardVM != null) DashboardVM.IsProxyRunning = true; // Sync

                if (DashboardVM != null && DashboardVM.IsTunMode)
                {
                    // Logic handled in DashboardVM properties or we trigger it
                    // DashboardVM init should handle reading Config.IsTunMode and starting if needed?
                    // DashboardVM ctor sets IsTunMode from config, but setters trigger StartAsync.
                    // We need to ensure startup logic works.
                    // Actually, DashboardVM doesn't auto-start in ctor.
                    // We should trigger it.
                    // But wait, IsProxyRunning set to true in MainVM lines 961.
                    // DashboardVM listens to IsProxyRunning?
                    // We should inject state or sync.
                    DashboardVM.IsProxyRunning = true;
                }
            }
            catch (Exception ex)
            {
                if (DashboardVM != null) DashboardVM.IsProxyRunning = false;
                // Не показываем MessageBox здесь, так как окно может еще не загрузиться, 
                // но статус IsProxyRunning = false визуально покажет, что прокси выключен.
                System.Diagnostics.Debug.WriteLine($"Proxy Start Failed: {ex.Message}");
            }

            StartEnforcementLoop();

            Task.Run(async () =>
            {
                await Task.Delay(2000);
                await CheckAllProxies();

                if (SettingsVM?.CheckUpdateOnStartup == true)
                {
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await SettingsVM.CheckForUpdatesAsync(silent: true);
                    });
                }
            });
        }

        // ... Остальные методы без изменений (или скопируйте из предыдущего ответа, если нужно полное тело) ...
        // Для краткости я привел только измененный конструктор и свойства, 
        // так как проблема была именно в инициализации.
        // Но чтобы следовать вашей инструкции "полный код", я продублирую остальные методы ниже.



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
                ProxyModalType = item.Type;
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




        private async Task CheckAllProxies()

        {
            var proxyList = Proxies.ToList(); if (proxyList.Count == 0) return;
            using (var semaphore = new SemaphoreSlim(3))
            {
                var tasks = proxyList.Select(async p => { await semaphore.WaitAsync(); try { Application.Current.Dispatcher.Invoke(() => p.Status = "Checking..."); await CheckSingleProxy(p); } finally { semaphore.Release(); } });
                await Task.WhenAll(tasks);
            }
        }



        private void StartEnforcementLoop()
        {
            // Enforcement loop disabled for TUN mode
            // _enforceCts?.Cancel(); _enforceCts = new CancellationTokenSource();
            // Task.Run(async () => { while (!_enforceCts.Token.IsCancellationRequested) { await Task.Delay(5000); if (IsProxyRunning) _proxyService.EnforceSystemProxy(); } }, _enforceCts.Token);
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var triggers = new HashSet<string> { nameof(TrafficRule.IsEnabled), nameof(TrafficRule.ProxyId), nameof(TrafficRule.TargetApps), nameof(TrafficRule.TargetHosts), nameof(TrafficRule.Action), nameof(TrafficRule.GroupName), nameof(TrafficRule.BlockDirection), nameof(ProxyItem.IsEnabled), nameof(ProxyItem.IpAddress), nameof(ProxyItem.Port), nameof(ProxyItem.Username), nameof(ProxyItem.Password), nameof(ProxyItem.CountryCode), nameof(ProxyItem.UseTls), nameof(ProxyItem.UseSsl), nameof(TrafficRule.IconBase64), nameof(ProxyItem.Type) };

            // Warning removed as support is being implemented

            if (!string.IsNullOrEmpty(e.PropertyName) && triggers.Contains(e.PropertyName))
            {
                RequestSaveSettings();

                // If Proxy Type changed, re-evaluate TUN eligibility
                if (e.PropertyName == nameof(ProxyItem.Type))
                {
                    DashboardVM?.RefreshTunState();
                    if (DashboardVM != null && DashboardVM.IsTunMode && !DashboardVM.CanEnableTunMode) DashboardVM.IsTunMode = false;
                }
            }
        }


        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_suppressSave) return;
            if (e.NewItems != null) foreach (INotifyPropertyChanged item in e.NewItems) item.PropertyChanged += OnItemPropertyChanged;
            if (e.OldItems != null) foreach (INotifyPropertyChanged item in e.OldItems) item.PropertyChanged -= OnItemPropertyChanged;

            // Collection changed, re-evaluate TUN eligibility (TunProxy might have changed/removed)
            // Collection changed, re-evaluate TUN eligibility (TunProxy might have changed/removed)
            DashboardVM?.RefreshTunState();
            if (DashboardVM != null && DashboardVM.IsTunMode && !DashboardVM.CanEnableTunMode) DashboardVM.IsTunMode = false;

            RequestSaveSettings();
        }

        private void SubscribeToItem(INotifyPropertyChanged item) { item.PropertyChanged -= OnItemPropertyChanged; item.PropertyChanged += OnItemPropertyChanged; }

        private void RequestSaveSettings()
        {
            if (_suppressSave) return;
            try { ApplyConfig(); } catch { }
            _saveDebounceCts?.Cancel(); _saveDebounceCts = new CancellationTokenSource(); var token = _saveDebounceCts.Token;
            Task.Delay(500, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                try
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var data = new AppSettings
                        {
                            IsAutoStart = SettingsVM?.IsAutoStart ?? _settingsService.IsAutoStartEnabled(),
                            CheckUpdateOnStartup = SettingsVM?.CheckUpdateOnStartup ?? _checkUpdateOnStartup,
                            Proxies = Proxies.ToList(),
                            Config = _config
                        };
                        _settingsService.Save(data);
                    });
                }
                catch { }
            });
        }

        private void ApplyConfig() { _proxyService.UpdateConfig(_config, Proxies.ToList()); _dnsProxyService.UpdateConfig(_config, Proxies.ToList()); }

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
            Application.Current.Dispatcher.Invoke(() => p.Status = "Checking...");

            // Fix: Check SOCKS5 connectivity using Socks5Client
            if (p.Type == ProxyType.Socks5)
            {
                try
                {
                    var result = await Task.Run(async () =>
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        using (var client = new System.Net.Sockets.TcpClient())
                        {
                            var cts = new CancellationTokenSource(5000);
                            // Test connection to Google DNS or similar reliable target
                            await Services.Socks5Client.ConnectAsync(client, p, "8.8.8.8", 53, cts.Token);
                            sw.Stop();
                            return (true, (int)sw.ElapsedMilliseconds);
                        }
                    });

                    Application.Current.Dispatcher.Invoke(() => { p.Status = "Online"; p.PingMs = result.Item2; p.SpeedMbps = 0; }); // Speed not measured here 
                }
                catch
                {
                    Application.Current.Dispatcher.Invoke(() => { p.Status = "Offline"; p.PingMs = 0; p.SpeedMbps = 0; });
                }
                return;
            }

            // Fallback for HTTP/HTTPS
            var res = await Task.Run(() => _proxyService.CheckProxy(p));
            Application.Current.Dispatcher.Invoke(() => { p.Status = res.IsSuccess ? "Online" : "Offline"; p.PingMs = res.Ping; p.SpeedMbps = res.Speed; if (!string.IsNullOrEmpty(res.CountryCode)) p.CountryCode = res.CountryCode; });
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



        private void ReloadRulesForCurrentMode()
        {
            _suppressSave = true;
            RulesVM?.ReloadRulesForCurrentMode();
            _suppressSave = false;
            OnPropertyChanged(nameof(IsBlackListMode));
        }




        private void ImportConfig(string? path = null)
        {
            _suppressSave = true;
            try
            {
                var d = _settingsService.Load();
                _config = d.Config ?? new AppConfig();
                IsAutoStart = _settingsService.IsAutoStartEnabled();
                CheckUpdateOnStartup = d.CheckUpdateOnStartup;

                Proxies.Clear();
                if (d.Proxies != null) d.Proxies.ForEach(p => { SubscribeToItem(p); Proxies.Add(p); });

                OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                ReloadRulesForCurrentMode();
                OnPropertyChanged(nameof(IsDnsProtectionEnabled));
                OnPropertyChanged(nameof(SelectedDnsProvider));
                OnPropertyChanged(nameof(DnsHost));

                // Restore TUN Mode
                if (_config.IsTunMode) IsTunMode = true;

                Presets.Clear();
                if (_config.Presets != null) _config.Presets.ForEach(p => Presets.Add(p));

                // Restore Proxy State (added persistence logic)
                if (d.IsProxyRunning)
                {
                    IsProxyRunning = true;
                    try
                    {
                        _proxyService.Start();
                        UpdateDnsServiceState();

                        // Ensure system proxy is applied after a short delay to override any system resets
                        Task.Run(async () =>
                        {
                            await Task.Delay(2000);
                            if (IsProxyRunning)
                            {
                                if (!_config.IsTunMode)
                                {
                                    SystemProxyHelper.SetSystemProxy(true, "127.0.0.1", 8000);
                                }

                                if (IsDnsProtectionEnabled)
                                {
                                    SystemProxyHelper.SetSystemDns(true);
                                }
                            }
                        });
                    }
                    catch { IsProxyRunning = false; }
                }
            }

            try
            {
                var d = _settingsService.Load(filePath);
                if (d != null && d.Config != null)
                {
                    _suppressSave = true;

                    // Deep Copy Config Properties to preserve references used by VMs
                    var newConfig = d.Config;
                    // Proxies logic:
                    Proxies.Clear();
                    if (d.Proxies != null) d.Proxies.ForEach(p => { SubscribeToItem(p); Proxies.Add(p); });

                    _config.CurrentMode = newConfig.CurrentMode;
                    _config.BlackListRules = newConfig.BlackListRules;
                    _config.WhiteListRules = newConfig.WhiteListRules;
                    _config.BlackListSelectedProxyId = newConfig.BlackListSelectedProxyId;
                    _config.EnableDnsProtection = newConfig.EnableDnsProtection;
                    _config.IsWebRtcBlockingEnabled = newConfig.IsWebRtcBlockingEnabled;
                    _config.DnsProvider = newConfig.DnsProvider;
                    _config.DnsHost = newConfig.DnsHost;
                    _config.Presets = newConfig.Presets;
                    _config.IsTunMode = newConfig.IsTunMode; // DashboardVM tracks this via Config, so update Config.

                    // Update SettingsVM properties
                    if (SettingsVM != null)
                    {
                        SettingsVM.CheckUpdateOnStartup = d.CheckUpdateOnStartup;
                        SettingsVM.IsAutoStart = d.IsAutoStart; // This sets registry
                        // SettingsVM properties reading from _config will notify automatically if we raise PropertyChanged?
                        // SettingsVM doesn't subscribe to _config changes.
                        // We must call OnPropertyChanged on SettingsVM? 
                        // SettingsVM reads _config.DnsProvider etc. 
                        // SettingsVM.RaisePropertyChanged for those? 
                        // Or just set SettingsVM properties to trigger updates?
                        SettingsVM.SelectedDnsProvider = newConfig.DnsProvider;
                        SettingsVM.DnsHost = newConfig.DnsHost;
                        SettingsVM.IsDnsProtectionEnabled = newConfig.EnableDnsProtection;
                        SettingsVM.IsWebRtcBlockingEnabled = newConfig.IsWebRtcBlockingEnabled;
                    }
                    else
                    {
                        _checkUpdateOnStartup = d.CheckUpdateOnStartup;
                        _settingsService.SetAutoStart(d.IsAutoStart);
                    }

                    OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                    ReloadRulesForCurrentMode();

                    _suppressSave = false;
                    RequestSaveSettings();
                    MessageBox.Show("Imported!");
                }
            }
            catch { }
        }

        private void LoadSettings()
        {
            _suppressSave = true; try
            {
                var d = _settingsService.Load();
                // Initialize _config from loaded data, or new
                if (d.Config != null)
                {
                    // Copy manual properties to EXISTING _config if initialized? 
                    // In LoadSettings (called from ctor), _config is null or empty.
                    // MainViewModel field private AppConfig _config;
                    // We can assign it here.
                    _config = d.Config;
                }
                else _config = new AppConfig();

                RulesVM?.UpdateConfig(_config);
                DashboardVM?.UpdateConfig(_config);
                SettingsVM?.UpdateConfig(_config);

                _checkUpdateOnStartup = d.CheckUpdateOnStartup;
                // IsAutoStart handled by SettingsService directly or SettingsVM

                Proxies.Clear();
                if (d.Proxies != null) d.Proxies.ForEach(p => { SubscribeToItem(p); Proxies.Add(p); });

                OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                ReloadRulesForCurrentMode();
                // Removed removed property notifications

                // Restore TUN Mode
                if (_config.IsTunMode && DashboardVM != null) DashboardVM.IsTunMode = true;
                Presets.Clear(); if (_config.Presets != null) _config.Presets.ForEach(p => Presets.Add(p));
            }
            finally { _suppressSave = false; }
        }






        private string _tunStatusDescription = "Off";
        public string TunStatusDescription
        {
            get => _tunStatusDescription;
            set { _tunStatusDescription = value; OnPropertyChanged(); }
        }



        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private string _latestVersion;
        public string LatestVersion
        {
            get => _latestVersion;
            set { _latestVersion = value; OnPropertyChanged(); }
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

                RulesList.Clear();
                foreach (var r in newRules) RulesList.Add(r);

                // Update Config
                if (_config.CurrentMode == RuleMode.BlackList)
                    _config.BlackListRules = newRules;
                else
                    _config.WhiteListRules = newRules;

                ApplyConfig();
                RequestSaveSettings();

            });
        }

        private void DeletePreset(RulePreset? preset)
        {
            if (preset == null) return;
            ShowConfirmation("Delete Preset", $"Are you sure you want to delete preset '{preset.Name}'?", () =>
            {
                Presets.Remove(preset);
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

        public void Cleanup()
        {
            try
            {
                _proxyService?.Stop();
                _dnsProxyService?.Stop();
                _tunService?.Stop();
            }
            catch { }
        }
    }


}