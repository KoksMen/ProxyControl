using ProxyControl.Models;
using ProxyControl.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ProxyControl.Helpers;
using System.Windows.Input;

namespace ProxyControl.ViewModels
{
    public class DashboardViewModel : BaseViewModel
    {
        private readonly TcpProxyService _proxyService;
        private readonly DnsProxyService _dnsProxyService;
        private readonly TunService _tunService;
        private readonly TrafficMonitorService _trafficMonitorService;
        private readonly SettingsService _settingsService;
        private AppConfig _config;

        public ObservableCollection<ProxyItem> Proxies { get; private set; }

        public DashboardViewModel(
            TcpProxyService proxyService,
            DnsProxyService dnsProxyService,
            TunService tunService,
            TrafficMonitorService trafficMonitorService,
            SettingsService settingsService,
            AppConfig config,
            ObservableCollection<ProxyItem> proxies)
        {
            _proxyService = proxyService;
            _dnsProxyService = dnsProxyService;
            _tunService = tunService;
            _trafficMonitorService = trafficMonitorService;
            _settingsService = settingsService;
            _config = config;
            Proxies = proxies;
            Logs = new ObservableCollection<ConnectionLog>();

            ToggleProxyCommand = new RelayCommand(_ => ToggleService());
            SelectMonitorProcessCommand = new RelayCommand(p => SelectedMonitorProcess = p as ProcessTrafficData);
            ApplyFilterCommand = new RelayCommand(_ => ApplyFilter());
            ClearLogsCommand = new RelayCommand(_ =>
            {
                AppLoggerService.Instance.Clear();
                Logs.Clear();
            });

            _proxyService.OnConnectionLog += OnConnectionLogReceived;

            // Initialize TUN Status
            OnPropertyChanged(nameof(TunModeStatus));
            OnPropertyChanged(nameof(IsTunMode));
            OnPropertyChanged(nameof(IsProxyRunning));

            if (Proxies != null)
                Proxies.CollectionChanged += OnProxiesCollectionChanged;
        }

        private void OnProxiesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RefreshTunState();
        }

        // --- Proxy Control ---

        private bool _isProxyRunning = true;
        public bool IsProxyRunning
        {
            get => _isProxyRunning;
            set
            {
                if (SetProperty(ref _isProxyRunning, value))
                {
                    OnPropertyChanged(nameof(ToggleProxyMenuText));
                }
            }
        }

        public string ToggleProxyMenuText => IsProxyRunning ? "Turn Proxy OFF" : "Turn Proxy ON";

        public ICommand ToggleProxyCommand { get; }

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
                        var tunConfig = new TunService.TunRulesConfig
                        {
                            Mode = _config.CurrentMode,
                            Rules = _config.CurrentMode == RuleMode.WhiteList ? _config.WhiteListRules : _config.BlackListRules,
                            ProxyType = IsBlackListMode ? (SelectedBlackListMainProxy?.Type ?? ProxyType.Http) : ProxyType.Http,
                        };
                        _ = _tunService.StartAsync(tunConfig);
                    }
                }
                catch
                {
                    IsProxyRunning = false;
                    MessageBox.Show("Failed to start proxy on port 8000.");
                }
            }
            else
            {
                _proxyService.Stop();
                _dnsProxyService.Stop();
                if (IsTunMode) _tunService.Stop();
            }
        }

        public void UpdateDnsServiceState()
        {
            if (IsProxyRunning && _config.EnableDnsProtection)
                _dnsProxyService.Start();
            else
                _dnsProxyService.Stop();
        }

        // --- TUN Mode ---

        public bool IsTunMode
        {
            get => _config.IsTunMode;
            set
            {
                if (_config.IsTunMode != value)
                {
                    if (value && !CanEnableTunMode)
                    {
                        // Validation logic
                        bool isSocks5 = false;
                        var proxy = SelectedBlackListMainProxy;
                        if (proxy != null && proxy.Type == ProxyType.Socks5) isSocks5 = true;

                        if (!IsBlackListMode || !isSocks5)
                        {
                            MessageBox.Show("TUN Mode is only available in Blacklist Mode with a SOCKS5 proxy selected.", "TUN Mode Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                            OnPropertyChanged(); // Reset UI
                            return;
                        }
                    }

                    _config.IsTunMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TunModeStatus));
                    RequestSaveSettings();
                    _ = ToggleTunModeAsync();
                }
            }
        }

        private string _tunStatusDescription = "Off";
        public string TunStatusDescription
        {
            get => _tunStatusDescription;
            set => SetProperty(ref _tunStatusDescription, value);
        }

        public string TunModeStatus => IsTunMode ? "🟢 TUN Active (Full UDP)" : "⚪ TUN Off";


        public bool IsBlackListMode => _config.CurrentMode == RuleMode.BlackList;

        public ProxyItem? SelectedBlackListMainProxy
        {
            get => Proxies.FirstOrDefault(p => p.Id == _config.BlackListSelectedProxyId);
        }

        public bool CanEnableTunMode
        {
            get
            {
                if (!IsBlackListMode)
                {
                    System.Diagnostics.Debug.WriteLine("CanEnableTunMode: False (Not BlackListMode)");
                    return false;
                }
                var proxy = SelectedBlackListMainProxy;
                if (proxy == null)
                {
                    System.Diagnostics.Debug.WriteLine($"CanEnableTunMode: False (Proxy is null). ConfigId: '{_config.BlackListSelectedProxyId}'");
                    return false;
                }
                if (proxy.Type != ProxyType.Socks5)
                {
                    System.Diagnostics.Debug.WriteLine($"CanEnableTunMode: False (Proxy Type is {proxy.Type})");
                    return false;
                }
                System.Diagnostics.Debug.WriteLine("CanEnableTunMode: True");
                return true;
            }
        }

        private async Task ToggleTunModeAsync()
        {
            if (IsTunMode)
            {
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
                        MessageBox.Show($"Failed to start local proxy service: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        IsTunMode = false;
                        return;
                    }
                }

                var tunConfig = new TunService.TunRulesConfig
                {
                    Mode = _config.CurrentMode,
                    Rules = _config.CurrentMode == RuleMode.WhiteList ? _config.WhiteListRules : _config.BlackListRules,
                    ProxyType = IsBlackListMode ? (SelectedBlackListMainProxy?.Type ?? ProxyType.Http) : ProxyType.Http
                };

                var success = await _tunService.StartAsync(tunConfig, status =>
                {
                    // Update status on UI thread if needed, though PropertyChanged is usually safe for simple bindings
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        TunStatusDescription = status;
                    });
                });

                if (!success)
                {
                    // MessageBox is already shown in the catch block if we moved it, 
                    // BUT TunService now catches exceptions and returns false.
                    // So we must show the error here.
                    // AND TunService reports the error via callback before returning false.

                    // We might want to keep the final status as the error message, 
                    // instead of overwriting it with "Failed to start".
                    // But let's append " - FAILED" to make it clear.

                    TunStatusDescription += " - FAILED";

                    MessageBox.Show($"TUN Mode failed to start.\nLast Status: {TunStatusDescription}\n\nPlease check the 'Logs' tab for full details.\nCommon issues:\n- Missing Admin rights\n- Antivirus blocking 'sing-box.exe'\n- Port 8000 in use",
                                    "TUN Mode Verification Failed",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);

                    IsTunMode = false;
                }
                else
                {
                    TunStatusDescription = "Active (VPN Mode)";
                }
            }
            else
            {
                _tunService.Stop();
                TunStatusDescription = "Inactive";

                try { _proxyService.EnforceSystemProxy(); } catch { }
            }
            OnPropertyChanged(nameof(TunModeStatus));
            OnPropertyChanged(nameof(TunStatusDescription));
        }

        private void RequestSaveSettings()
        {
            _settingsService.Save(new AppSettings { Config = _config, IsAutoStart = _settingsService.IsAutoStartEnabled(), Proxies = Proxies.ToList() });
        }


        public void UpdateConfig(AppConfig config)
        {
            _config = config;
            RefreshTunState();
        }

        public void RefreshTunState()
        {
            OnPropertyChanged(nameof(CanEnableTunMode));
            OnPropertyChanged(nameof(TunModeStatus));
            OnPropertyChanged(nameof(IsTunMode));
            OnPropertyChanged(nameof(SelectedBlackListMainProxy));
            UpdateTunAvailabilityMessage();
        }

        private string _tunAvailabilityMessage = "";
        public string TunAvailabilityMessage
        {
            get => _tunAvailabilityMessage;
            set => SetProperty(ref _tunAvailabilityMessage, value);
        }

        private void UpdateTunAvailabilityMessage()
        {
            if (!IsBlackListMode)
            {
                TunAvailabilityMessage = "🚫 TUN Mode is only available in Blacklist Mode.";
                return;
            }

            var proxy = SelectedBlackListMainProxy;
            if (proxy == null)
            {
                TunAvailabilityMessage = "⚠️ Please select a proxy to enable TUN Mode.";
                return;
            }

            if (proxy.Type != ProxyType.Socks5)
            {
                TunAvailabilityMessage = "⚠️ TUN Mode requires a SOCKS5 proxy.";
                return;
            }

            TunAvailabilityMessage = "✅ Ready to enable TUN (VPN) Mode.";
        }

        // --- Monitoring ---

        public ObservableCollection<ProcessTrafficData> MonitoredProcesses => _trafficMonitorService.DisplayedProcessList;

        private ProcessTrafficData? _selectedMonitorProcess;
        public ProcessTrafficData? SelectedMonitorProcess
        {
            get => _selectedMonitorProcess;
            set => SetProperty(ref _selectedMonitorProcess, value);
        }

        public ICommand SelectMonitorProcessCommand { get; }

        public ICommand ApplyFilterCommand { get; }

        public Array TrafficPeriodModes => Enum.GetValues(typeof(TrafficPeriodMode));

        private TrafficPeriodMode _selectedPeriodMode = TrafficPeriodMode.LiveSession;
        public TrafficPeriodMode SelectedPeriodMode
        {
            get => _selectedPeriodMode;
            set
            {
                if (SetProperty(ref _selectedPeriodMode, value))
                {
                    OnPropertyChanged(nameof(IsDateRangeVisible));
                    ApplyFilter();
                }
            }
        }

        public bool IsDateRangeVisible => SelectedPeriodMode == TrafficPeriodMode.CustomRange;

        private DateTime _filterDateStart = DateTime.Now;
        public DateTime FilterDateStart
        {
            get => _filterDateStart;
            set => SetProperty(ref _filterDateStart, value);
        }

        private DateTime _filterDateEnd = DateTime.Now;
        public DateTime FilterDateEnd
        {
            get => _filterDateEnd;
            set => SetProperty(ref _filterDateEnd, value);
        }

        private string _filterTimeStart = "00:00";
        public string FilterTimeStart
        {
            get => _filterTimeStart;
            set => SetProperty(ref _filterTimeStart, value);
        }

        private string _filterTimeEnd = "23:59";
        public string FilterTimeEnd
        {
            get => _filterTimeEnd;
            set => SetProperty(ref _filterTimeEnd, value);
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

        // --- Logs ---

        public ObservableCollection<LogEntry> AppLogs => AppLoggerService.Instance.LogEntries;
        public ObservableCollection<ConnectionLog> Logs { get; private set; }
        public ICommand ClearLogsCommand { get; }

        private void OnConnectionLogReceived(ConnectionLog log)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Logs.Insert(0, log);
                if (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
            });
        }
    }
}
