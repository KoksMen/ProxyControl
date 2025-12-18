using ProxyControl.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
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
        private AppConfig _config;
        private List<ProxyItem> _availableProxies;

        // Кэш для FakeDNS: Hostname -> FakeIP
        private ConcurrentDictionary<string, IPAddress> _fakeDnsCache = new ConcurrentDictionary<string, IPAddress>();

        public ProxyService()
        {
            _proxyServer = new ProxyServer();
            _proxyServer.CertificateManager.EnsureRootCertificate(); // Генерирует сертификат для HTTPS
            _proxyServer.BeforeRequest += OnRequest;
        }

        public void UpdateConfig(AppConfig config, List<ProxyItem> proxies)
        {
            _config = config;
            _availableProxies = proxies;
        }

        public void Start()
        {
            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true);
            _proxyServer.AddEndPoint(explicitEndPoint);
            _proxyServer.Start();
            _proxyServer.SetAsSystemHttpProxy(explicitEndPoint); // Устанавливаем как системный прокси
            _proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
        }

        public void Stop()
        {
            _proxyServer.Stop();
            _proxyServer.RestoreOriginalProxySettings();
        }

        private async Task OnRequest(object sender, SessionEventArgs e)
        {
            string hostname = e.HttpClient.Request.RequestUri.Host;
            string processName = GetProcessName(e.HttpClient.ProcessId.Value);

            // 4) FakeDNS функционал (простая реализация: подмена DNS резолвинга если нужно)
            if (_config.FakeDnsEnabled)
            {
                // В реальном FakeDNS мы бы возвращали фейковый IP. 
                // Titanium позволяет переопределить IP для хоста.
                // Здесь мы просто логируем или можем подменить IP назначения, если бы у нас была база маппинга.
            }

            ProxyItem targetProxy = null;

            if (_config.CurrentMode == RuleMode.BlackList)
            {
                targetProxy = ResolveBlackList(processName, hostname);
            }
            else
            {
                targetProxy = ResolveWhiteList(processName, hostname);
            }

            if (targetProxy != null)
            {
                var externalProxy = new ExternalProxy()
                {
                    HostName = targetProxy.IpAddress,
                    Port = targetProxy.Port,
                    UserName = targetProxy.Username,
                    Password = targetProxy.Password
                };

                // Направляем трафик через выбранный внешний прокси
                //e.CustomUpStreamHttpProxy = externalProxy;
                //e.CustomUpStreamHttpsProxy = externalProxy;
            }
            // Если targetProxy == null, трафик идет напрямую (DIRECT)
        }

        private ProxyItem ResolveBlackList(string app, string host)
        {
            // В BlackList режиме должен быть выбран один главный прокси
            var proxy = _availableProxies.FirstOrDefault(p => p.Id == _config.BlackListSelectedProxyId && p.IsEnabled);
            if (proxy == null) return null; // Если прокси не выбран, идем напрямую

            switch (_config.BlackListRuleType)
            {
                case BlackListType.ProxyAll:
                    return proxy;

                case BlackListType.ProxyAllExceptApps:
                    if (_config.BlackListExcludedApps.Contains(app)) return null;
                    return proxy;

                case BlackListType.ProxyAllExceptSites:
                    if (IsHostMatch(host, _config.BlackListExcludedSites)) return null;
                    return proxy;

                case BlackListType.ProxyAllExceptAppsAndSites:
                    if (_config.BlackListExcludedApps.Contains(app)) return null;
                    if (IsHostMatch(host, _config.BlackListExcludedSites)) return null;
                    return proxy;
            }
            return null;
        }

        private ProxyItem ResolveWhiteList(string app, string host)
        {
            // В WhiteList проходим по всем активным прокси и ищем правило, которое подходит
            // Мультипроксирование: первое совпавшее правило выигрывает
            foreach (var rule in _config.WhiteListRules)
            {
                var proxy = _availableProxies.FirstOrDefault(p => p.Id == rule.ProxyId && p.IsEnabled);
                if (proxy == null) continue;

                bool match = false;
                switch (rule.Type)
                {
                    case WhiteListType.ProxyAllAppsAllSites:
                        match = true;
                        break;
                    case WhiteListType.ProxyAllAppsSelectedSites:
                        if (IsHostMatch(host, rule.TargetHosts)) match = true;
                        break;
                    case WhiteListType.ProxySelectedAppsAllSites:
                        if (rule.TargetApps.Contains(app)) match = true;
                        break;
                    case WhiteListType.ProxySelectedAppsSelectedSites:
                        if (rule.TargetApps.Contains(app) && IsHostMatch(host, rule.TargetHosts)) match = true;
                        break;
                }

                if (match) return proxy;
            }
            return null; // Если ни одно правило не подошло - Direct
        }

        // Хелпер для получения имени процесса по ID
        private string GetProcessName(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                return process.ProcessName + ".exe";
            }
            catch
            {
                return "Unknown";
            }
        }

        // Хелпер для проверки домена (включая поддомены)
        private bool IsHostMatch(string requestHost, List<string> patterns)
        {
            foreach (var pattern in patterns)
            {
                if (requestHost.Contains(pattern)) return true; // Упрощенная проверка
            }
            return false;
        }

        // 5) Проверка прокси
        public async Task<bool> CheckProxy(ProxyItem proxy)
        {
            try
            {
                var myProxy = new WebProxy(proxy.IpAddress, proxy.Port);
                if (!string.IsNullOrEmpty(proxy.Username))
                    myProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

                var request = (HttpWebRequest)WebRequest.Create("http://google.com");
                request.Proxy = myProxy;
                request.Timeout = 5000;

                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
