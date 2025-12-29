using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProxyControl.Models
{
    public enum RuleMode
    {
        BlackList, // Весь трафик через прокси, правила — исключения (Direct) или Блокировка
        WhiteList  // Весь трафик напрямую, правила — через прокси или Блокировка
    }

    public enum RuleAction
    {
        // Default удалено
        Proxy,   // Принудительно через прокси
        Direct,  // Принудительно напрямую
        Block    // Блокировать соединение
    }

    public class TrafficRule : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private string? _proxyId;
        private string _groupName = "General";
        private RuleAction _action = RuleAction.Proxy; // Изменено с Default на Proxy
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

        public string? ProxyId
        {
            get => _proxyId;
            set { _proxyId = value; OnPropertyChanged(); }
        }

        public List<string> TargetApps
        {
            get => _targetApps;
            set { _targetApps = value; OnPropertyChanged(); }
        }

        public List<string> TargetHosts
        {
            get => _targetHosts;
            set { _targetHosts = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ConnectionLog
    {
        public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string ProcessName { get; set; } = "";
        public string Host { get; set; } = "";
        public string Result { get; set; } = ""; // Blocked, Direct, ProxyIP
        public string Color { get; set; } = "White"; // Цвет для UI
    }

    public class AppConfig
    {
        public RuleMode CurrentMode { get; set; } = RuleMode.BlackList;
        public Guid? BlackListSelectedProxyId { get; set; }
        public List<TrafficRule> BlackListRules { get; set; } = new List<TrafficRule>();
        public List<TrafficRule> WhiteListRules { get; set; } = new List<TrafficRule>();
    }
}