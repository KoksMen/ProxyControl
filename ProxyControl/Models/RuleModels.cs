using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProxyControl.Models
{
    public enum RuleMode
    {
        BlackList,
        WhiteList
    }

    public enum BlackListType
    {
        ProxyAll,
        ProxyAllExceptApps,
        ProxyAllExceptSites,
        ProxyAllExceptAppsAndSites
    }

    public enum WhiteListType
    {
        ProxyAllAppsAllSites,
        ProxyAllAppsSelectedSites,
        ProxySelectedAppsAllSites,
        ProxySelectedAppsSelectedSites
    }

    public class TrafficRule
    {
        public string ProxyId { get; set; }
        public WhiteListType Type { get; set; }

        // Списки для фильтрации
        public List<string> TargetApps { get; set; } = new List<string>(); // e.g., "chrome.exe"
        public List<string> TargetHosts { get; set; } = new List<string>(); // e.g., "google.com"
    }

    public class AppConfig
    {
        public RuleMode CurrentMode { get; set; } = RuleMode.BlackList;

        // Настройки для Blacklist (работает с одним выбранным прокси)
        public string BlackListSelectedProxyId { get; set; }
        public BlackListType BlackListRuleType { get; set; }
        public List<string> BlackListExcludedApps { get; set; } = new List<string>();
        public List<string> BlackListExcludedSites { get; set; } = new List<string>();

        // Настройки для Whitelist (много прокси, у каждого свои правила)
        public List<TrafficRule> WhiteListRules { get; set; } = new List<TrafficRule>();

        public bool FakeDnsEnabled { get; set; }
    }
}
