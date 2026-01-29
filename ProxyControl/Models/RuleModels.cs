using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ProxyControl.Models
{
    public enum RuleAction
    {
        Proxy,
        Direct,
        Block
    }

    public enum RuleMode
    {
        BlackList,
        WhiteList
    }

    public enum BlockDirection
    {
        Both,
        Inbound,
        Outbound
    }

    public class TrafficRule : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private string _groupName = "General";
        private RuleAction _action;
        private string? _proxyId;
        private BlockDirection _blockDirection = BlockDirection.Both;
        private ImageSource? _appIcon;
        private string? _iconBase64;
        private bool _isRegex;

        // Backing fields for lists
        private List<string> _targetApps = new List<string>();
        private List<string> _targetHosts = new List<string>();

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public string GroupName
        {
            get => _groupName;
            set { _groupName = value; OnPropertyChanged(); }
        }

        public RuleAction Action
        {
            get => _action;
            set { _action = value; OnPropertyChanged(); }
        }

        public BlockDirection BlockDirection
        {
            get => _blockDirection;
            set { _blockDirection = value; OnPropertyChanged(); }
        }

        public string? ProxyId
        {
            get => _proxyId;
            set { _proxyId = value; OnPropertyChanged(); }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public ImageSource? AppIcon
        {
            get => _appIcon;
            set { _appIcon = value; OnPropertyChanged(); }
        }

        public string? IconBase64
        {
            get => _iconBase64;
            set { _iconBase64 = value; OnPropertyChanged(); }
        }

        public bool IsRegex
        {
            get => _isRegex;
            set { _isRegex = value; OnPropertyChanged(); }
        }

        public List<string> TargetApps
        {
            get => _targetApps;
            set
            {
                _targetApps = value ?? new List<string>();
                OnPropertyChanged();
                // AppKey зависит от TargetApps, уведомляем об изменении для обновления группировки
                OnPropertyChanged(nameof(AppKey));
            }
        }

        public List<string> TargetHosts
        {
            get => _targetHosts;
            set
            {
                _targetHosts = value ?? new List<string>();
                OnPropertyChanged();
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string AppKey
        {
            get
            {
                if (TargetApps != null && TargetApps.Count > 0)
                {
                    return TargetApps[0];
                }
                return "Global";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Класс для логов подключений (используется во вкладке Connection Logs)
    public class ConnectionLog : INotifyPropertyChanged
    {
        public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string ProcessName { get; set; } = "";
        public string Host { get; set; } = "";
        public string Result { get; set; } = ""; // Blocked, Proxy IP, etc.
        public string Color { get; set; } = "#White";
        public ImageSource? AppIcon { get; set; }
        public string? CountryFlagUrl { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}