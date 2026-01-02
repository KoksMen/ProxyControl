using System;
using System.Collections.Generic;

namespace ProxyControl.Models
{
    // Корневой класс настроек, сохраняемый в JSON
    public class AppSettings
    {
        public bool IsAutoStart { get; set; }
        public bool CheckUpdateOnStartup { get; set; } = true;
        public List<ProxyItem> Proxies { get; set; } = new List<ProxyItem>();
        public AppConfig Config { get; set; } = new AppConfig();
    }

    // Конфигурация правил маршрутизации
    public class AppConfig
    {
        public RuleMode CurrentMode { get; set; } = RuleMode.BlackList;
        public Guid? BlackListSelectedProxyId { get; set; }

        public List<TrafficRule> BlackListRules { get; set; } = new List<TrafficRule>();
        public List<TrafficRule> WhiteListRules { get; set; } = new List<TrafficRule>();
    }
}