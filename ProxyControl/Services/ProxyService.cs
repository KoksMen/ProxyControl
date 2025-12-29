using ProxyControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace ProxyControl.Services
{
    public class ProxyService
    {
        private readonly ProxyServer _proxyServer;
        private readonly ProcessMonitorService _processMonitor;
        private AppConfig _config;
        private List<ProxyItem> _availableProxies;
        private ExplicitProxyEndPoint _endPoint;

        public event Action<ConnectionLog>? OnConnectionLog;

        public ProxyService()
        {
            _processMonitor = new ProcessMonitorService();
            _proxyServer = new ProxyServer();
            _proxyServer.CertificateManager.EnsureRootCertificate();
            _proxyServer.EnableConnectionPool = true;
            _proxyServer.ForwardToUpstreamGateway = true;
            _proxyServer.BeforeRequest += OnRequest;
        }

        public void UpdateConfig(AppConfig config, List<ProxyItem> proxies)
        {
            _config = config;
            _availableProxies = proxies;
        }

        public void Start()
        {
            _processMonitor.Start();
            if (_proxyServer.ProxyEndPoints.Count == 0)
            {
                _endPoint = new ExplicitProxyEndPoint(System.Net.IPAddress.Any, 8000, true);
                _proxyServer.AddEndPoint(_endPoint);
            }

            if (!_proxyServer.ProxyRunning)
                _proxyServer.Start();

            _proxyServer.SetAsSystemHttpProxy(_endPoint);
            _proxyServer.SetAsSystemHttpsProxy(_endPoint);
        }

        public void Stop()
        {
            _processMonitor.Stop();
            try
            {
                _proxyServer.Stop();
                _proxyServer.RestoreOriginalProxySettings();
            }
            catch { }
            SystemProxyHelper.RestoreSystemProxy();
        }

        public void EnforceSystemProxy()
        {
            if (!_proxyServer.ProxyRunning) return;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
                {
                    int? enabled = key?.GetValue("ProxyEnable") as int?;
                    if (enabled == null || enabled == 0)
                    {
                        _proxyServer.SetAsSystemHttpProxy(_endPoint);
                        _proxyServer.SetAsSystemHttpsProxy(_endPoint);
                    }
                }
            }
            catch { }
        }

        private async Task OnRequest(object sender, SessionEventArgs e)
        {
            if (_config == null || _availableProxies == null) return;

            try
            {
                string processName = "Unknown";
                var sessionCache = e.HttpClient.UserData as Dictionary<string, object>;
                if (sessionCache == null)
                {
                    sessionCache = new Dictionary<string, object>();
                    e.HttpClient.UserData = sessionCache;
                }

                if (sessionCache.ContainsKey("ProcessName"))
                {
                    processName = sessionCache["ProcessName"] as string ?? "Unknown";
                }
                else
                {
                    int pid = 0;
                    try { pid = e.HttpClient.ProcessId.Value; } catch { }
                    processName = _processMonitor.GetProcessName(pid);
                    sessionCache["ProcessName"] = processName;
                }

                string hostname = e.HttpClient.Request.RequestUri.Host;

                var decision = ResolveAction(processName, hostname);

                // Log
                string logResult = decision.Action == RuleAction.Block ? "BLOCKED" : (decision.Proxy != null ? $"Proxy: {decision.Proxy.IpAddress}" : "Direct");
                string logColor = decision.Action == RuleAction.Block ? "#FF5555" : (decision.Proxy != null ? "#55FF55" : "#AAAAAA");
                OnConnectionLog?.Invoke(new ConnectionLog { ProcessName = processName, Host = hostname, Result = logResult, Color = logColor });

                if (decision.Action == RuleAction.Block)
                {
                    e.Ok("Blocked by ProxyControl");
                    return;
                }

                if (decision.Proxy != null)
                {
                    e.CustomUpStreamProxy = new ExternalProxy()
                    {
                        HostName = decision.Proxy.IpAddress,
                        Port = decision.Proxy.Port,
                        UserName = decision.Proxy.Username,
                        Password = decision.Proxy.Password
                    };
                }
            }
            catch { }
        }

        private (RuleAction Action, ProxyItem? Proxy) ResolveAction(string app, string host)
        {
            if (_config.CurrentMode == RuleMode.BlackList)
            {
                var mainProxy = _availableProxies.FirstOrDefault(p => p.Id == _config.BlackListSelectedProxyId.ToString() && p.IsEnabled);

                foreach (var rule in _config.BlackListRules)
                {
                    if (!rule.IsEnabled) continue;
                    if (IsRuleMatch(rule, app, host))
                    {
                        if (rule.Action == RuleAction.Block) return (RuleAction.Block, null);
                        if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null);
                        if (rule.ProxyId != null)
                        {
                            var p = _availableProxies.FirstOrDefault(x => x.Id == rule.ProxyId && x.IsEnabled);
                            if (p != null) return (RuleAction.Proxy, p);
                        }
                        return (RuleAction.Direct, null);
                    }
                }

                if (mainProxy != null) return (RuleAction.Proxy, mainProxy);
                return (RuleAction.Direct, null);
            }
            else // WhiteList
            {
                foreach (var rule in _config.WhiteListRules)
                {
                    if (!rule.IsEnabled) continue;
                    if (IsRuleMatch(rule, app, host))
                    {
                        if (rule.Action == RuleAction.Block) return (RuleAction.Block, null);
                        if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null);
                        if (rule.ProxyId != null)
                        {
                            var p = _availableProxies.FirstOrDefault(x => x.Id == rule.ProxyId && x.IsEnabled);
                            if (p != null) return (RuleAction.Proxy, p);
                        }
                    }
                }
                return (RuleAction.Direct, null);
            }
        }

        private bool IsRuleMatch(TrafficRule rule, string app, string host)
        {
            if (rule.TargetApps.Count > 0)
            {
                bool match = false;
                for (int i = 0; i < rule.TargetApps.Count; i++)
                {
                    string t = rule.TargetApps[i];
                    if (t.Length == 1 && t[0] == '*') { match = true; break; }
                    if (string.Equals(t, app, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                }
                if (!match) return false;
            }

            if (rule.TargetHosts.Count > 0)
            {
                for (int i = 0; i < rule.TargetHosts.Count; i++)
                {
                    string t = rule.TargetHosts[i];
                    if (t.Length == 1 && t[0] == '*') return true;
                    if (host.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            return false;
        }

        public async Task<bool> CheckProxy(ProxyItem proxy)
        {
            if (string.IsNullOrEmpty(proxy.IpAddress) || proxy.Port == 0) return false;
            try
            {
                var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.IpAddress, proxy.Port), UseProxy = true };
                if (!string.IsNullOrEmpty(proxy.Username)) handler.Proxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
                using (var client = new HttpClient(handler)) { client.Timeout = TimeSpan.FromSeconds(5); var response = await client.GetAsync("http://www.google.com/generate_204"); return response.IsSuccessStatusCode; }
            }
            catch { return false; }
        }
    }
}