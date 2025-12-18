using ProxyControl.Models;
using ProxyControl.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ProxyControl.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ProxyService _proxyService;
        private AppConfig _config;

        public ObservableCollection<ProxyItem> Proxies { get; set; } = new ObservableCollection<ProxyItem>();

        // UI Bindings for BlackList
        public string NewExcludedApp { get; set; }
        public string NewExcludedSite { get; set; }
        public ObservableCollection<string> BlackListApps { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> BlackListSites { get; set; } = new ObservableCollection<string>();

        private ProxyItem _selectedProxy;
        public ProxyItem SelectedProxy
        {
            get => _selectedProxy;
            set { _selectedProxy = value; OnPropertyChanged(); }
        }

        private bool _isBlackListMode = true;
        public bool IsBlackListMode
        {
            get => _isBlackListMode;
            set
            {
                _isBlackListMode = value;
                _config.CurrentMode = value ? RuleMode.BlackList : RuleMode.WhiteList;
                ApplyConfig();
                OnPropertyChanged();
            }
        }

        private BlackListType _selectedBlackListType;
        public BlackListType SelectedBlackListType
        {
            get => _selectedBlackListType;
            set { _selectedBlackListType = value; _config.BlackListRuleType = value; ApplyConfig(); OnPropertyChanged(); }
        }

        // Команды
        public ICommand AddProxyCommand { get; private set; }
        public ICommand CheckProxyCommand { get; private set; }
        public ICommand ToggleProxyCommand { get; private set; }
        public ICommand AddBlackListAppCommand { get; private set; }
        public ICommand AddBlackListSiteCommand { get; private set; }

        public MainViewModel()
        {
            //_proxyService = new ProxyService();
            //_config = new AppConfig();

            // Инициализация команд (упрощенная реализация RelayCommand)
            AddProxyCommand = new RelayCommand(AddProxy);
            CheckProxyCommand = new RelayCommand(CheckProxy);
            ToggleProxyCommand = new RelayCommand(ToggleProxy);
            AddBlackListAppCommand = new RelayCommand(obj =>
            {
                if (!string.IsNullOrWhiteSpace(NewExcludedApp))
                {
                    BlackListApps.Add(NewExcludedApp);
                    _config.BlackListExcludedApps.Add(NewExcludedApp);
                    NewExcludedApp = "";
                    OnPropertyChanged(nameof(NewExcludedApp));
                    ApplyConfig();
                }
            });
            AddBlackListSiteCommand = new RelayCommand(obj =>
            {
                if (!string.IsNullOrWhiteSpace(NewExcludedSite))
                {
                    BlackListSites.Add(NewExcludedSite);
                    _config.BlackListExcludedSites.Add(NewExcludedSite);
                    NewExcludedSite = "";
                    OnPropertyChanged(nameof(NewExcludedSite));
                    ApplyConfig();
                }
            });

            _proxyService.Start();
        }

        private void AddProxy(object obj)
        {
            Proxies.Add(new ProxyItem { IpAddress = "127.0.0.1", Port = 8080, IsEnabled = true });
            ApplyConfig();
        }

        private async void CheckProxy(object obj)
        {
            if (SelectedProxy == null) return;
            SelectedProxy.Status = "Checking...";
            bool result = await _proxyService.CheckProxy(SelectedProxy);
            SelectedProxy.Status = result ? "Online" : "Offline";
        }

        private void ToggleProxy(object obj)
        {
            ApplyConfig();
        }

        private void ApplyConfig()
        {
            if (SelectedProxy != null && IsBlackListMode)
            {
                _config.BlackListSelectedProxyId = SelectedProxy.Id;
            }
            _proxyService.UpdateConfig(_config, Proxies.ToList());
        }

        // Boilerplate INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Простая реализация ICommand
    public class RelayCommand : ICommand
    {
        private System.Action<object> execute;
        private System.Predicate<object> canExecute;
        public RelayCommand(System.Action<object> execute, System.Predicate<object> canExecute = null)
        {
            this.execute = execute; this.canExecute = canExecute;
        }
        public event System.EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
        public bool CanExecute(object parameter) => canExecute == null || canExecute(parameter);
        public void Execute(object parameter) => execute(parameter);
    }
}
