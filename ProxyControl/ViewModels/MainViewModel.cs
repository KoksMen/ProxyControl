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

namespace ProxyControl.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ProxyService _proxyService;
        private readonly SettingsService _settingsService;
        private readonly GithubUpdateService _updateService;

        private AppConfig _config;
        private CancellationTokenSource _enforceCts;

        private bool _suppressSave = false;

        public ObservableCollection<ProxyItem> Proxies { get; set; } = new ObservableCollection<ProxyItem>();
        public ObservableCollection<TrafficRule> RulesList { get; set; } = new ObservableCollection<TrafficRule>();

        public string ToggleProxyMenuText => IsProxyRunning ? "Turn Proxy OFF" : "Turn Proxy ON";
        public string AppVersion => "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private bool _isProxyRunning = true;
        public bool IsProxyRunning
        {
            get => _isProxyRunning;
            set { _isProxyRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ToggleProxyMenuText)); }
        }

        private bool _isAutoStart;
        public bool IsAutoStart
        {
            get => _isAutoStart;
            set
            {
                _isAutoStart = value;
                if (!_suppressSave) _settingsService.SetAutoStart(value);
                SaveSettings();
                OnPropertyChanged();
            }
        }

        private ProxyItem? _selectedProxy;
        public ProxyItem? SelectedProxy
        {
            get => _selectedProxy;
            set { _selectedProxy = value; OnPropertyChanged(); ApplyConfig(); }
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

        public ProxyItem? SelectedBlackListMainProxy
        {
            get => Proxies.FirstOrDefault(p => p.Id == _config.BlackListSelectedProxyId);
            set
            {
                if (value == null && _config.BlackListSelectedProxyId == null) return;

                _config.BlackListSelectedProxyId = value?.Id;
                OnPropertyChanged();

                ReloadRulesForCurrentMode();
                ApplyConfig();
                SaveSettings();
            }
        }

        private TrafficRule? _selectedRule;
        public TrafficRule? SelectedRule { get => _selectedRule; set { _selectedRule = value; OnPropertyChanged(); } }

        public string NewRuleApps { get; set; } = "*";
        public string NewRuleHosts { get; set; } = "*";
        public ProxyItem? NewRuleSelectedProxy { get; set; }

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

        public MainViewModel()
        {
            _proxyService = new ProxyService();
            _settingsService = new SettingsService();
            _updateService = new GithubUpdateService();
            _config = new AppConfig();

            AddProxyCommand = new RelayCommand(_ => AddProxy());
            PasteProxyCommand = new RelayCommand(_ => PasteProxy());
            RemoveProxyCommand = new RelayCommand(_ => RemoveProxy());
            SaveChangesCommand = new RelayCommand(_ => SaveSettings());
            CheckProxyCommand = new RelayCommand(_ => CheckSelectedProxy());
            AddRuleCommand = new RelayCommand(_ => AddRule());
            RemoveRuleCommand = new RelayCommand(_ => RemoveRule());

            ShowWindowCommand = new RelayCommand(_ => { Application.Current.MainWindow.Show(); Application.Current.MainWindow.WindowState = WindowState.Normal; Application.Current.MainWindow.Activate(); });
            ExitAppCommand = new RelayCommand(_ => { MainWindow.AllowClose = true; Application.Current.Shutdown(); });
            ToggleProxyCommand = new RelayCommand(_ => ToggleService());

            ImportConfigCommand = new RelayCommand(_ => ImportConfig());
            ExportConfigCommand = new RelayCommand(_ => ExportConfig());
            CheckUpdateCommand = new RelayCommand(async _ => await _updateService.CheckAndInstallUpdate());

            LoadSettings();

            _proxyService.Start();
            ApplyConfig();
            IsProxyRunning = true;

            StartEnforcementLoop();
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
                    if (IsProxyRunning) _proxyService.EnforceSystemProxy();
                }
            }, _enforceCts.Token);
        }

        private async void ImportConfig()
        {
            var dialog = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _suppressSave = true;

                    var data = _settingsService.Load(dialog.FileName);
                    _config = data.Config ?? new AppConfig();

                    Proxies.Clear();
                    if (data.Proxies != null) foreach (var p in data.Proxies) Proxies.Add(p);

                    OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                    ReloadRulesForCurrentMode();

                    _suppressSave = false; 

                    ApplyConfig();
                    SaveSettings(); 

                    MessageBox.Show("Configuration imported successfully! Checking proxies...");
                    await CheckAllProxies();
                }
                catch
                {
                    _suppressSave = false;
                    MessageBox.Show("Failed to import configuration.");
                }
            }
        }

        private void ExportConfig()
        {
            var dialog = new SaveFileDialog { Filter = "JSON Files (*.json)|*.json", FileName = "proxy_config.json" };
            if (dialog.ShowDialog() == true)
            {
                var data = new AppSettings { IsAutoStart = IsAutoStart, Proxies = Proxies.ToList(), Config = _config };
                _settingsService.Save(data, dialog.FileName);
                MessageBox.Show("Configuration exported successfully!");
            }
        }

        private void ToggleService()
        {
            if (IsProxyRunning) { _proxyService.Stop(); IsProxyRunning = false; }
            else { _proxyService.Start(); ApplyConfig(); IsProxyRunning = true; }
        }

        private void AddProxy()
        {
            var p = new ProxyItem { IpAddress = "", Port = 8080, IsEnabled = true, Status = "New" };
            Proxies.Add(p); SelectedProxy = p; SaveSettings();
        }

        private async void PasteProxy()
        {
            if (!Clipboard.ContainsText()) return;
            string txt = Clipboard.GetText().Trim();
            var lines = txt.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool added = false;
            foreach (var line in lines)
            {
                var parts = line.Trim().Split(':');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int port))
                {
                    var p = new ProxyItem { IpAddress = parts[0], Port = port, IsEnabled = true, Status = "Pasted" };
                    if (parts.Length >= 4) { p.Username = parts[2]; p.Password = parts[3]; }
                    Proxies.Add(p);
                    _ = CheckSingleProxy(p);
                    if (!added) { SelectedProxy = p; added = true; }
                }
            }
            if (added) SaveSettings();
        }

        private void RemoveProxy()
        {
            if (SelectedProxy == null) return;
            var proxyId = SelectedProxy.Id;

            int blCount = _config.BlackListRules.Count(r => r.ProxyId == proxyId);
            int wlCount = _config.WhiteListRules.Count(r => r.ProxyId == proxyId);
            bool isMainGateway = _config.BlackListSelectedProxyId == proxyId;

            if (blCount + wlCount > 0 || isMainGateway)
            {
                if (MessageBox.Show($"Proxy used in {blCount + wlCount} rules. Delete dependencies?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                _config.WhiteListRules.RemoveAll(r => r.ProxyId == proxyId);
                _config.BlackListRules.RemoveAll(r => r.ProxyId == proxyId);
                if (isMainGateway)
                {
                    _config.BlackListSelectedProxyId = null;
                    OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                }
                ReloadRulesForCurrentMode();
            }

            Proxies.Remove(SelectedProxy); SelectedProxy = null; SaveSettings();
        }

        private async void CheckSelectedProxy()
        {
            if (SelectedProxy != null) await CheckSingleProxy(SelectedProxy);
        }

        private async Task<bool> CheckSingleProxy(ProxyItem p)
        {
            p.Status = "Checking...";
            bool res = await Task.Run(() => _proxyService.CheckProxy(p));
            p.Status = res ? "Online" : "Offline";
            return res;
        }

        private async Task CheckAllProxies()
        {
            var tasks = new List<Task>();
            foreach (var p in Proxies) tasks.Add(CheckSingleProxy(p));
            await Task.WhenAll(tasks);
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
                if (data.Proxies != null) foreach (var p in data.Proxies) Proxies.Add(p);

                OnPropertyChanged(nameof(SelectedBlackListMainProxy));
                ReloadRulesForCurrentMode();
            }
            finally
            {
                _suppressSave = false;
            }
        }

        private void SaveSettings()
        {
            if (_suppressSave) return;

            var data = new AppSettings { IsAutoStart = IsAutoStart, Proxies = Proxies.ToList(), Config = _config };
            _settingsService.Save(data);
            ApplyConfig();
        }

        private void AddRule()
        {
            var apps = NewRuleApps.Split(';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var hosts = NewRuleHosts.Split(';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (!apps.Any()) apps.Add("*"); if (!hosts.Any()) hosts.Add("*");

            var rule = new TrafficRule { TargetApps = apps, TargetHosts = hosts, IsEnabled = true };

            if (IsBlackListMode)
            {
                if (SelectedBlackListMainProxy == null) { MessageBox.Show("Select Default Gateway Proxy first!"); return; }
                rule.ProxyId = SelectedBlackListMainProxy.Id;
                _config.BlackListRules.Add(rule);
            }
            else
            {
                if (NewRuleSelectedProxy == null) { MessageBox.Show("Select Proxy!"); return; }
                rule.ProxyId = NewRuleSelectedProxy.Id;
                _config.WhiteListRules.Add(rule);
            }
            ReloadRulesForCurrentMode(); SaveSettings();
        }

        private void RemoveRule()
        {
            if (SelectedRule == null) return;
            if (IsBlackListMode) _config.BlackListRules.Remove(SelectedRule);
            else _config.WhiteListRules.Remove(SelectedRule);
            ReloadRulesForCurrentMode(); SaveSettings();
        }

        private void ReloadRulesForCurrentMode()
        {
            RulesList.Clear();
            if (IsBlackListMode)
            {
                if (_config.BlackListSelectedProxyId != null)
                {
                    var filtered = _config.BlackListRules.Where(r => r.ProxyId == _config.BlackListSelectedProxyId);
                    foreach (var r in filtered) RulesList.Add(r);
                }
            }
            else
            {
                foreach (var r in _config.WhiteListRules) RulesList.Add(r);
            }
            OnPropertyChanged(nameof(IsBlackListMode));
        }

        private void ApplyConfig() => _proxyService.UpdateConfig(_config, Proxies.ToList());

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
