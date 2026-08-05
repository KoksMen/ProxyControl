using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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

    public enum TrafficType
    {
        TCP,
        UDP,
        DNS,
        HTTPS,
        WebSocket,
        WebRTC
    }

    public enum RuleTrafficType
    {
        Any,
        TCP,
        UDP,
        DNS,
        HTTPS,
        WebSocket,
        WebRTC
    }



    public class TrafficRule : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private string _groupName = "General";
        private RuleAction _action;
        private string? _proxyId;
        private BlockDirection _blockDirection = BlockDirection.Both;
        private ImageSource? _appIcon;
        private ImageSource? _siteIcon;
        private string? _iconBase64;
        private bool _isTemporary;
        private RuleTrafficType _trafficType = RuleTrafficType.Any;

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
            set
            {
                _action = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionLabel));
            }
        }

        public BlockDirection BlockDirection
        {
            get => _blockDirection;
            set
            {
                _blockDirection = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionLabel));
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string ActionLabel => Action == RuleAction.Block
            ? $"Block · {BlockDirection}"
            : Action.ToString();

        public string? ProxyId
        {
            get => _proxyId;
            set { _proxyId = value; OnPropertyChanged(); }
        }

        public RuleTrafficType TrafficType
        {
            get => _trafficType;
            set
            {
                _trafficType = value;
                OnPropertyChanged();
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public ImageSource? AppIcon
        {
            get => _appIcon;
            set { _appIcon = value; OnPropertyChanged(); }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public ImageSource? SiteIcon
        {
            get => _siteIcon;
            set { _siteIcon = value; OnPropertyChanged(); }
        }

        public string? IconBase64
        {
            get => _iconBase64;
            set { _iconBase64 = value; OnPropertyChanged(); }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsTemporary
        {
            get => _isTemporary;
            set
            {
                _isTemporary = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PersistenceLabel));
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string PersistenceLabel => IsTemporary ? "TEMP" : string.Empty;

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





        private bool _isScheduleEnabled;
        public bool IsScheduleEnabled
        {
            get => _isScheduleEnabled;
            set { _isScheduleEnabled = value; OnPropertyChanged(); }
        }

        private TimeSpan? _scheduleStart;
        public TimeSpan? ScheduleStart
        {
            get => _scheduleStart;
            set { _scheduleStart = value; OnPropertyChanged(); }
        }

        private TimeSpan? _scheduleEnd;
        public TimeSpan? ScheduleEnd
        {
            get => _scheduleEnd;
            set { _scheduleEnd = value; OnPropertyChanged(); }
        }

        private DayOfWeek[] _scheduleDays = Array.Empty<DayOfWeek>();
        public DayOfWeek[] ScheduleDays
        {
            get => _scheduleDays;
            set { _scheduleDays = value; OnPropertyChanged(); }
        }

        private string _timeStart = "";
        public string TimeStart
        {
            get => _timeStart;
            set { _timeStart = value; OnPropertyChanged(); }
        }

        private string _timeEnd = "";
        public string TimeEnd
        {
            get => _timeEnd;
            set { _timeEnd = value; OnPropertyChanged(); }
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

    public class ActiveProcessInfo
    {
        public string ProcessName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string DisplayText => string.IsNullOrWhiteSpace(FilePath)
            ? ProcessName
            : $"{ProcessName} — {FilePath}";
    }

    // Класс для логов подключений (используется во вкладке Connection Logs)
    public class ConnectionLog : INotifyPropertyChanged
    {
        private ImageSource? _siteIcon;

        public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string ProcessName { get; set; } = "";
        public string ProcessPath { get; set; } = "";
        private string _host = "";
        public string Host
        {
            get => _host;
            set { _host = value; OnPropertyChanged(); }
        }
        public string Result { get; set; } = ""; // Blocked, Proxy IP, etc.
        public string Color { get; set; } = "#White";
        public ImageSource? AppIcon { get; set; }
        public string? CountryFlagUrl { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public ImageSource? SiteIcon
        {
            get => _siteIcon;
            set
            {
                if (ReferenceEquals(_siteIcon, value)) return;
                _siteIcon = value;
                OnPropertyChanged();
            }
        }

        // Traffic type (TCP, UDP, DNS, HTTPS)
        public TrafficType Type { get; set; } = TrafficType.TCP;

        [System.Text.Json.Serialization.JsonIgnore]
        public string TypeIcon => Type switch
        {
            TrafficType.TCP => "🔵",
            TrafficType.UDP => "🟢",
            TrafficType.DNS => "🟡",
            TrafficType.HTTPS => "🔒",
            TrafficType.WebSocket => "🔄",
            TrafficType.WebRTC => "📡",
            _ => "⚪"
        };

        [System.Text.Json.Serialization.JsonIgnore]
        public string TypeStr => Type.ToString();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Grid-based Rules UI models
    public class RuleGroupInfo
    {
        public string GroupName { get; set; } = "General";
        public int RuleCount { get; set; }
        public int AppCount { get; set; }
        public List<TrafficRule> Rules { get; set; } = new();
        public bool IsScopeEnabled { get; set; } = true;

        public string GradientStart => GetGradientStart(GroupName);
        public string GradientEnd => GetGradientEnd(GroupName);
        public string Icon { get; set; } = "📁";

        public RuleGroupInfo()
        {
            Icon = GetDefaultIcon(GroupName);
        }

        // Helpers for defaults (can be moved or kept here)
        public static string GetGradientStart(string group) => group switch
        {
            "General" => "#6366F1",
            "Gaming" => "#EC4899",
            "Streaming" => "#F59E0B",
            "Social" => "#10B981",
            "Work" => "#3B82F6",
            _ => "#8B5CF6"
        };

        public static string GetGradientEnd(string group) => group switch
        {
            "General" => "#8B5CF6",
            "Gaming" => "#F43F5E",
            "Streaming" => "#EF4444",
            "Social" => "#14B8A6",
            "Work" => "#6366F1",
            _ => "#A855F7"
        };

        public static string GetDefaultIcon(string group) => group switch
        {
            "General" => "📁",
            "Gaming" => "🎮",
            "Streaming" => "📺",
            "Social" => "💬",
            "Work" => "💼",
            "VPN" => "🔐",
            _ => "📁"
        };
    }

    public class AppRuleInfo
    {
        public string AppName { get; set; } = "*";
        public int RuleCount { get; set; }
        public ImageSource? AppIcon { get; set; }
        public List<TrafficRule> Rules { get; set; } = new();
        public bool IsScopeEnabled { get; set; } = true;
        public string DisplayName => AppName == "*" ? "All Apps" : AppName;
        public string Icon => AppName == "*" ? "🌐" : "📱";
    }
}
