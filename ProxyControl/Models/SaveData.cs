using System;
using System.Collections.Generic;

namespace ProxyControl.Models
{
    // Добавлен Enum для выбора провайдера
    public enum DnsProviderType
    {
        Google,         // 8.8.8.8
        Cloudflare,     // 1.1.1.1
        OpenDNS,        // 208.67.222.222
        Custom          // Пользовательский ввод
    }

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

        public bool EnableDnsProtection { get; set; } = false;

        // Выбранный тип провайдера
        public DnsProviderType DnsProvider { get; set; } = DnsProviderType.Google;

        // IP адрес DNS сервера (используется сервисом)
        public string DnsHost { get; set; } = "8.8.8.8";

        public List<TrafficRule> BlackListRules { get; set; } = new List<TrafficRule>();
        public List<TrafficRule> WhiteListRules { get; set; } = new List<TrafficRule>();
    }
}