using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProxyControl.Models
{
    public class ProxyItem : INotifyPropertyChanged
    {
        private string _ipAddress = "";
        private int _port;
        private string? _username;
        private string? _password;
        private bool _isEnabled;
        private string _status = "Unknown"; // Online, Offline, Checking...
        private string _countryCode = "";

        // Новые поля статистики
        private long _pingMs = 0;
        private double _speedMbps = 0;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); }
        }

        public int Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); }
        }

        public string? Username
        {
            get => _username;
            set { _username = value; OnPropertyChanged(); }
        }

        public string? Password
        {
            get => _password;
            set { _password = value; OnPropertyChanged(); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public string CountryCode
        {
            get => _countryCode;
            set { _countryCode = value; OnPropertyChanged(); OnPropertyChanged(nameof(FlagUrl)); }
        }

        public long PingMs
        {
            get => _pingMs;
            set { _pingMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(PingFormatted)); }
        }

        public double SpeedMbps
        {
            get => _speedMbps;
            set { _speedMbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedFormatted)); }
        }

        public string FlagUrl => !string.IsNullOrEmpty(CountryCode)
            ? $"https://flagcdn.com/w40/{CountryCode.ToLower()}.png"
            : "";

        public string PingFormatted => PingMs > 0 ? $"{PingMs} ms" : "-";
        public string SpeedFormatted => SpeedMbps > 0 ? $"{SpeedMbps:0.##} Mb/s" : "-";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}