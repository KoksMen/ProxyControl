using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ProxyControl.Models
{
    public class ProxyItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _ipAddress;
        private int _port;
        private string _username;
        private string _password;
        private bool _isEnabled;
        private string _status = "Idle";
        private string _countryCode;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

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

        public string Username
        {
            get => _username;
            set { _username = value; OnPropertyChanged(); }
        }

        public string Password
        {
            get => _password;
            set { _password = value; OnPropertyChanged(); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public string CountryCode
        {
            get => _countryCode;
            set
            {
                _countryCode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FlagUrl));
            }
        }

        [JsonIgnore]
        public string? FlagUrl
        {
            get
            {
                if (string.IsNullOrEmpty(CountryCode)) return null;
                return $"https://flagcdn.com/w40/{CountryCode.ToLower()}.png";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}