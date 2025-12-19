using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace ProxyControl.Models
{
    public enum RuleMode
    {
        BlackList,
        WhiteList
    }

    public class TrafficRule : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        private Guid? _proxyId;
        public Guid? ProxyId
        {
            get => _proxyId;
            set { _proxyId = value; OnPropertyChanged(); }
        }

        public List<string> TargetApps { get; set; } = new List<string>();
        public List<string> TargetHosts { get; set; } = new List<string>();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class AppConfig
    {
        public RuleMode CurrentMode { get; set; } = RuleMode.BlackList;
        public Guid? BlackListSelectedProxyId { get; set; }
        public List<TrafficRule> BlackListRules { get; set; } = new List<TrafficRule>();
        public List<TrafficRule> WhiteListRules { get; set; } = new List<TrafficRule>();
    }
}
