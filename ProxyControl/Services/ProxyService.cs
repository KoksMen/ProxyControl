using Microsoft.Win32;
using ProxyControl.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
                _endPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true);
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
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
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
                    try
                    {
                        pid = e.HttpClient.ProcessId.Value;
                    }
                    catch { }

                    processName = _processMonitor.GetProcessName(pid);

                    sessionCache["ProcessName"] = processName;
                }

                string hostname = e.HttpClient.Request.RequestUri.Host;

                ProxyItem? targetProxy = _config.CurrentMode == RuleMode.BlackList
                    ? ResolveBlackList(processName, hostname)
                    : ResolveWhiteList(processName, hostname);

                if (targetProxy != null && !string.IsNullOrEmpty(targetProxy.IpAddress) && targetProxy.Port > 0)
                {
                    e.CustomUpStreamProxy = new ExternalProxy()
                    {
                        HostName = targetProxy.IpAddress,
                        Port = targetProxy.Port,
                        UserName = targetProxy.Username,
                        Password = targetProxy.Password
                    };
                }
            }
            catch { }
        }

        private ProxyItem? ResolveBlackList(string app, string host)
        {
            var mainProxy = _availableProxies.FirstOrDefault(p => p.Id == _config.BlackListSelectedProxyId.ToString() && p.IsEnabled);
            if (mainProxy == null) return null;

            foreach (var rule in _config.BlackListRules)
            {
                if (!rule.IsEnabled) continue;
                if (rule.ProxyId != null && rule.ProxyId != mainProxy.Id) continue;
                if (IsRuleMatch(rule, app, host)) return null;
            }
            return mainProxy;
        }

        private ProxyItem? ResolveWhiteList(string app, string host)
        {
            foreach (var rule in _config.WhiteListRules)
            {
                if (!rule.IsEnabled) continue;
                if (rule.ProxyId == null) continue;

                var associatedProxy = _availableProxies.FirstOrDefault(p => p.Id == rule.ProxyId && p.IsEnabled);
                if (associatedProxy == null) continue;

                if (IsRuleMatch(rule, app, host)) return associatedProxy;
            }
            return null;
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
                if (!string.IsNullOrEmpty(proxy.Username))
                    handler.Proxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetAsync("http://www.google.com/generate_204");
                    return response.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }
    }
}
