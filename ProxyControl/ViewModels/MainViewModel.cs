using Microsoft.Win32;
using ProxyControl.Models;
using ProxyControl.Services;
using ProxyControl.Helpers; // Хелпер иконок
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

        private AppConfig _config;
        private CancellationTokenSource _enforceCts;
        private bool _suppressSave = false;

        public ObservableCollection<ProxyItem> Proxies { get; set; } = new ObservableCollection<ProxyItem>();
        public ObservableCollection<TrafficRule> RulesList { get; set; } = new ObservableCollection<TrafficRule>();
        public ObservableCollection<ConnectionLog> Logs { get; set; } = new ObservableCollection<ConnectionLog>();

        public ICollectionView RulesView { get; private set; }

        public string ToggleProxyMenuText => IsProxyRunning ? "Turn Proxy OFF" : "Turn Proxy ON";
        public string AppVersion => "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

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

        private ProxyItem? _newRuleSelectedProxy;
        public ProxyItem? NewRuleSelectedProxy
        {
            get => _newRuleSelectedProxy;
            set { _newRuleSelectedProxy = value; OnPropertyChanged(); }
        }

        public bool IsNewRuleProxyRequired => NewRuleAction == RuleAction.Proxy;

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

        // Команда показа окна
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

            RulesView = CollectionViewSource.GetDefaultView(RulesList);
            RulesView.GroupDescriptions.Add(new PropertyGroupDescription("GroupName"));
            RulesView.GroupDescriptions.Add(new PropertyGroupDescription("AppKey"));
            RulesView.Filter = FilterRules;

            AddProxyCommand = new RelayCommand(_ => AddProxy());
            PasteProxyCommand = new RelayCommand(_ => PasteProxy());
            RemoveProxyCommand = new RelayCommand(_ => RemoveProxy());
            SaveChangesCommand = new RelayCommand(_ => SaveSettings());
            CheckProxyCommand = new RelayCommand(_ => CheckSelectedProxy());
            AddRuleCommand = new RelayCommand(_ => AddRule());
            RemoveRuleCommand = new RelayCommand(_ => RemoveRule());

            // ИСПРАВЛЕНИЕ ДЛЯ ТРЕЯ: Принудительное разворачивание и активация
            ShowWindowCommand = new RelayCommand(_ =>
            {
                var win = Application.Current.MainWindow;
                if (win != null)
                {
                    win.Show(); // Если был Hidden
                    if (win.WindowState == WindowState.Minimized)
                        win.WindowState = WindowState.Normal;
                    win.Activate(); // На передний план
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
            CheckUpdateCommand = new RelayCommand(async _ => await _updateService.CheckAndInstallUpdate());
            ClearLogsCommand = new RelayCommand(_ => Logs.Clear());

            LoadSettings();
            _proxyService.Start();
            ApplyConfig();
            IsProxyRunning = true;
            StartEnforcementLoop();

            Task.Run(async () =>
            {
                await Task.Delay(2000);
                await CheckAllProxies();
            });
        }

        private async Task CheckAllProxies()
        {
            var proxyList = Proxies.ToList();
            if (proxyList.Count == 0) return;

            using (var semaphore = new SemaphoreSlim(5))
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
                nameof(ProxyItem.IsEnabled), nameof(ProxyItem.IpAddress), nameof(ProxyItem.Port),
                nameof(ProxyItem.Username), nameof(ProxyItem.Password), nameof(ProxyItem.CountryCode)
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
                // Запрашиваем подтверждение у пользователя
                var result = MessageBox.Show(
                    $"Are you sure you want to delete proxy {SelectedProxy.IpAddress}:{SelectedProxy.Port}?\nThis will also remove all associated rules.",
                    "Confirm Deletion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                // Если пользователь нажал "Yes", выполняем удаление
                if (result == MessageBoxResult.Yes)
                {
                    string proxyIdToRemove = SelectedProxy.Id;

                    // 1. Удаляем правила из BlackList, связанные с этим прокси
                    var blackListToRemove = _config.BlackListRules.Where(r => r.ProxyId == proxyIdToRemove).ToList();
                    foreach (var rule in blackListToRemove)
                    {
                        _config.BlackListRules.Remove(rule);
                    }

                    // 2. Удаляем правила из WhiteList, связанные с этим прокси
                    var whiteListToRemove = _config.WhiteListRules.Where(r => r.ProxyId == proxyIdToRemove).ToList();
                    foreach (var rule in whiteListToRemove)
                    {
                        _config.WhiteListRules.Remove(rule);
                    }

                    // 3. Если этот прокси был основным шлюзом (Default Gateway), сбрасываем это
                    if (_config.BlackListSelectedProxyId.ToString() == proxyIdToRemove)
                    {
                        _config.BlackListSelectedProxyId = null;
                        OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                    }

                    // 4. Удаляем сам прокси
                    Proxies.Remove(SelectedProxy);
                    SelectedProxy = null;

                    // 5. Сохраняем и обновляем интерфейс
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
                // Попытка найти иконку при добавлении
                var icon = IconHelper.GetIconByProcessName(app);

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
                        GroupName = group,
                        ProxyId = proxyIdToUse,
                        AppIcon = icon // Сохраняем найденную иконку
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
            if (IsBlackListMode)
            {
                foreach (var r in _config.BlackListRules)
                {
                    // Подгружаем иконки при загрузке правил, если они не были сохранены (ImageSource не сериализуется напрямую, так что лучше искать снова)
                    if (r.TargetApps.Any())
                    {
                        r.AppIcon = IconHelper.GetIconByProcessName(r.TargetApps.First());
                    }

                    SubscribeToItem(r);
                    RulesList.Add(r);
                }
            }
            else
            {
                foreach (var r in _config.WhiteListRules)
                {
                    if (r.TargetApps.Any())
                    {
                        r.AppIcon = IconHelper.GetIconByProcessName(r.TargetApps.First());
                    }
                    SubscribeToItem(r);
                    RulesList.Add(r);
                }
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