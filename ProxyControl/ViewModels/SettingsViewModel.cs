using ProxyControl.Helpers;
using ProxyControl.Models;
using ProxyControl.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ProxyControl.ViewModels
{
    public class SettingsViewModel : BaseViewModel
    {
        private readonly SettingsService _settingsService;
        private AppConfig _config;
        private readonly GithubUpdateService _updateService;

        // Backing field
        private bool _checkUpdateOnStartup;

        public event Action<string, string, long>? RequestShowNotification; // Tag, Url, Size
        public event Action? SettingsChanged;
        public event Action<string>? ImportRequested;

        public SettingsViewModel(
            SettingsService settingsService,
            AppConfig config,
            GithubUpdateService updateService,
            bool checkUpdateOnStartup)
        {
            _settingsService = settingsService;
            _config = config;
            _updateService = updateService;
            _checkUpdateOnStartup = checkUpdateOnStartup;

            ImportConfigCommand = new RelayCommand(_ => ImportConfig());
            ExportConfigCommand = new RelayCommand(_ => ExportConfig());
            CheckUpdateCommand = new RelayCommand(async _ => await CheckForUpdatesAsync(silent: false));
            OpenUpdateModalCommand = new RelayCommand(async _ =>
            {
                if (!string.IsNullOrEmpty(PendingUpdateUrl))
                {
                    await ExecuteUpdate(PendingUpdateUrl, PendingUpdateSize);
                }
            });

            // Subscribe to update events
            _updateService.OnUpdateAvailable += OnUpdateAvailable;
        }

        private void OnUpdateAvailable(string version, string url, long size)
        {
            PendingUpdateUrl = url;
            PendingUpdateSize = size;

            RequestShowNotification?.Invoke("Update Available", $"New version {version} is available.", size);

            // Logic to show modal handled by UI/MainVM triggers via property binding if manually checked?
            // If manual check (silent=false), CheckForUpdatesAsync logic will handle it if we modify it.
            // But OnUpdateAvailable is fired from Service.
        }

        // --- Application Settings ---

        public bool IsAutoStart
        {
            get => _settingsService.IsAutoStartEnabled();
            set
            {
                if (IsAutoStart != value)
                {
                    _settingsService.SetAutoStart(value);
                    OnPropertyChanged();
                    RequestSaveSettings();
                }
            }
        }

        public bool CheckUpdateOnStartup
        {
            get => _checkUpdateOnStartup;
            set
            {
                if (SetProperty(ref _checkUpdateOnStartup, value))
                {
                    RequestSaveSettings();
                }
            }
        }

        // --- DNS Settings ---

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

        public Array DnsProviders => Enum.GetValues(typeof(DnsProviderType));

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

        public bool IsDnsProtectionEnabled
        {
            get => _config.EnableDnsProtection;
            set
            {
                if (_config.EnableDnsProtection == value) return;

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
                        OnPropertyChanged(); // Revert UI check
                        return;
                    }
                }

                _config.EnableDnsProtection = value;
                OnPropertyChanged();
                RequestSaveSettings();
            }
        }

        public bool IsWebRtcBlockingEnabled
        {
            get => _config.IsWebRtcBlockingEnabled;
            set
            {
                if (_config.IsWebRtcBlockingEnabled != value)
                {
                    _config.IsWebRtcBlockingEnabled = value;
                    OnPropertyChanged();
                    RequestSaveSettings();
                }
            }
        }

        // --- Updates ---

        private bool _isUpdateModalVisible;
        public bool IsUpdateModalVisible
        {
            get => _isUpdateModalVisible;
            set { SetProperty(ref _isUpdateModalVisible, value); }
        }

        private int _updateProgress;
        public int UpdateProgress
        {
            get => _updateProgress;
            set { SetProperty(ref _updateProgress, value); }
        }

        private string _updateStatusText = "Initializing...";
        public string UpdateStatusText
        {
            get => _updateStatusText;
            set { SetProperty(ref _updateStatusText, value); }
        }

        private string _updateDetailText = "";
        public string UpdateDetailText
        {
            get => _updateDetailText;
            set { SetProperty(ref _updateDetailText, value); }
        }

        public string? PendingUpdateUrl { get; set; }
        public long PendingUpdateSize { get; set; }

        public ICommand ImportConfigCommand { get; }
        public ICommand ExportConfigCommand { get; }
        public ICommand CheckUpdateCommand { get; }
        public ICommand OpenUpdateModalCommand { get; }


        // --- Methods ---

        public void UpdateConfig(AppConfig config)
        {
            _config = config;
            OnPropertyChanged(nameof(IsDnsProtectionEnabled));
            OnPropertyChanged(nameof(IsWebRtcBlockingEnabled));
            OnPropertyChanged(nameof(SelectedDnsProvider));
            OnPropertyChanged(nameof(DnsHost));
            OnPropertyChanged(nameof(IsDnsCustom));
        }

        private void RequestSaveSettings()
        {
            SettingsChanged?.Invoke();
        }

        private void ImportConfig()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                Title = "Import Configuration"
            };

            if (dialog.ShowDialog() == true)
            {
                ImportRequested?.Invoke(dialog.FileName);
            }
        }

        private void ExportConfig()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                Title = "Export Configuration",
                FileName = $"ProxyControl_Config_{DateTime.Now:yyyyMMdd}"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    string json = System.Text.Json.JsonSerializer.Serialize(_config, options);
                    System.IO.File.WriteAllText(dialog.FileName, json);
                    MessageBox.Show("Configuration exported successfully.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error exporting configuration: {ex.Message}");
                }
            }
        }

        public async Task CheckForUpdatesAsync(bool silent)
        {
            try
            {
                await _updateService.CheckAndInstallUpdate(null, null, silent);

                if (!string.IsNullOrEmpty(PendingUpdateUrl) && !silent)
                {
                    // If not silent, show modal
                    IsUpdateModalVisible = true;
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show($"Update check failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecuteUpdate(string url, long size)
        {
            UpdateStatusText = "Downloading...";
            UpdateProgress = 0;
            // PerformUpdate has arguments: url, size, onProgress, onCompleted
            await _updateService.PerformUpdate(url, size, (step, desc, p) =>
            {
                UpdateStatusText = desc;
                UpdateProgress = p;
            },
            () =>
            {
                // Completed
            });
        }
    }
}
