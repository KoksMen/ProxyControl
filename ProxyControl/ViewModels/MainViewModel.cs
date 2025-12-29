using Microsoft.Win32;
using ProxyControl.Models;
using ProxyControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
using System.Windows.Input;

namespace ProxyControl.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly TcpProxyService _proxyService;
        private readonly SettingsService _settingsService;
        private readonly GithubUpdateService _updateService;

        private AppConfig _config;
        private CancellationTokenSource _enforceCts;
        private bool _suppressSave = false;

        public ObservableCollection<ProxyItem> Proxies { get; set; } = new ObservableCollection<ProxyItem>();
        public ObservableCollection<TrafficRule> RulesList { get; set; } = new ObservableCollection<TrafficRule>();
        public ObservableCollection<ConnectionLog> Logs { get; set; } = new ObservableCollection<ConnectionLog>();

        public string ToggleProxyMenuText => IsProxyRunning ? "Turn Proxy OFF" : "Turn Proxy ON";
        public string AppVersion => "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public string NewRuleApps { get; set; } = "*";
        public string NewRuleHosts { get; set; } = "*";
        public string NewRuleGroup { get; set; } = "General";
        public RuleAction NewRuleAction { get; set; } = RuleAction.Proxy;
        public ProxyItem? NewRuleSelectedProxy { get; set; }

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

        private ProxyItem? _selectedProxy;
        public ProxyItem? SelectedProxy
        {
            get => _selectedProxy;
            set
            {
                _selectedProxy = value;
                OnPropertyChanged();
                ApplyConfig();
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

        public ICommand AddProxyCommand { get; }
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

        public MainViewModel()
        {
            _proxyService = new TcpProxyService();
            _settingsService = new SettingsService();
            _updateService = new GithubUpdateService();
            _config = new AppConfig();

            _proxyService.OnConnectionLog += OnLogReceived;

            Proxies.CollectionChanged += OnCollectionChanged;
            RulesList.CollectionChanged += OnCollectionChanged;

            AddProxyCommand = new RelayCommand(_ => AddProxy());
            PasteProxyCommand = new RelayCommand(_ => PasteProxy());
            RemoveProxyCommand = new RelayCommand(_ => RemoveProxy());
            SaveChangesCommand = new RelayCommand(_ => SaveSettings());
            CheckProxyCommand = new RelayCommand(_ => CheckSelectedProxy());
            AddRuleCommand = new RelayCommand(_ => AddRule());
            RemoveRuleCommand = new RelayCommand(_ => RemoveRule());
            ShowWindowCommand = new RelayCommand(_ =>
            {
                Application.Current.MainWindow.Show();
                Application.Current.MainWindow.WindowState = WindowState.Normal;
                Application.Current.MainWindow.Activate();
            });
            ExitAppCommand = new RelayCommand(_ =>
            {
                MainWindow.AllowClose = true;
                Application.Current.Shutdown();
            });
            ToggleProxyCommand = new RelayCommand(_ => ToggleService());
            ImportConfigCommand = new RelayCommand(_ => ImportConfig());
            ExportConfigCommand = new RelayCommand(_ => ExportConfig());
            CheckUpdateCommand = new RelayCommand(async _ => await _updateService.CheckAndInstallUpdate());
            ClearLogsCommand = new RelayCommand(_ => Logs.Clear());

            LoadSettings();
            _proxyService.Start();
            ApplyConfig();
            IsProxyRunning = true;
            StartEnforcementLoop();
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
                nameof(ProxyItem.IsEnabled), nameof(ProxyItem.IpAddress), nameof(ProxyItem.Port),
                nameof(ProxyItem.Username), nameof(ProxyItem.Password)
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
                Proxies.Remove(SelectedProxy);
                SelectedProxy = null;
            }
        }

        private async Task CheckSingleProxy(ProxyItem p)
        {
            p.Status = "Checking...";
            bool res = await Task.Run(() => _proxyService.CheckProxy(p));
            p.Status = res ? "Online" : "Offline";
        }

        private async void CheckSelectedProxy()
        {
            if (SelectedProxy != null)
                await CheckSingleProxy(SelectedProxy);
        }

        private void AddRule()
        {
            var apps = NewRuleApps.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            var hosts = NewRuleHosts.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

            if (!apps.Any()) apps.Add("*");
            if (!hosts.Any()) hosts.Add("*");

            var rule = new TrafficRule
            {
                TargetApps = apps,
                TargetHosts = hosts,
                IsEnabled = true,
                Action = NewRuleAction,
                GroupName = string.IsNullOrWhiteSpace(NewRuleGroup) ? "General" : NewRuleGroup
            };

            if (NewRuleAction == RuleAction.Proxy)
            {
                if (IsBlackListMode)
                {
                    if (SelectedBlackListMainProxy != null) rule.ProxyId = SelectedBlackListMainProxy.Id;
                }
                else
                {
                    if (NewRuleSelectedProxy != null) rule.ProxyId = NewRuleSelectedProxy.Id;
                }
            }

            if (IsBlackListMode)
            {
                _config.BlackListRules.Add(rule);
            }
            else
            {
                _config.WhiteListRules.Add(rule);
            }

            SubscribeToItem(rule);
            RulesList.Add(rule);
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
            }
        }

        private void ReloadRulesForCurrentMode()
        {
            _suppressSave = true;
            RulesList.Clear();
            if (IsBlackListMode)
            {
                foreach (var r in _config.BlackListRules)
                {
                    SubscribeToItem(r);
                    RulesList.Add(r);
                }
            }
            else
            {
                foreach (var r in _config.WhiteListRules)
                {
                    SubscribeToItem(r);
                    RulesList.Add(r);
                }
            }
            _suppressSave = false;
            OnPropertyChanged(nameof(IsBlackListMode));
        }

        private void ToggleService()
        {
            IsProxyRunning = !IsProxyRunning;
            if (IsProxyRunning) _proxyService.Start(); else _proxyService.Stop();
        }

        private void ImportConfig() { }
        private void ExportConfig() { }

        private void LoadSettings()
        {
            _suppressSave = true;
            try
            {
                var data = _settingsService.Load();
                _config = data.Config ?? new AppConfig();
                IsAutoStart = _settingsService.IsAutoStartEnabled();
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
