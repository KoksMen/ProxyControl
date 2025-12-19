using ProxyControl.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ProxyControl.Services
{
    public class TcpProxyService
    {
        private TcpListener _listener;
        private bool _isRunning;

        private List<ProxyItem> _localProxies = new List<ProxyItem>();
        private List<TrafficRule> _localBlackList = new List<TrafficRule>();
        private List<TrafficRule> _localWhiteList = new List<TrafficRule>();
        private string? _blackListProxyId;
        private RuleMode _currentMode;

        private readonly ProcessMonitorService _processMonitor;
        private const int LocalPort = 8000;
        private const int BufferSize = 8192;

        private readonly ConcurrentDictionary<Guid, ClientContext> _activeClients = new ConcurrentDictionary<Guid, ClientContext>();

        private class ClientContext
        {
            public TcpClient Client { get; set; }
            public TcpClient Remote { get; set; }
            public CancellationTokenSource Cts { get; set; }
        }

        public TcpProxyService()
        {
            _processMonitor = new ProcessMonitorService();
        }

        public void UpdateConfig(AppConfig config, List<ProxyItem> proxies)
        {
            _localProxies = proxies.Select(p => new ProxyItem
            {
                Id = p.Id,
                IpAddress = p.IpAddress,
                Port = p.Port,
                Username = p.Username,
                Password = p.Password,
                IsEnabled = p.IsEnabled
            }).ToList();

            if (config.BlackListRules != null)
            {
                _localBlackList = config.BlackListRules.Select(r => new TrafficRule
                {
                    IsEnabled = r.IsEnabled,
                    ProxyId = r.ProxyId,
                    TargetApps = r.TargetApps.ToList(),
                    TargetHosts = r.TargetHosts.ToList()
                }).ToList();
            }
            else
            {
                _localBlackList = new List<TrafficRule>();
            }

            if (config.WhiteListRules != null)
            {
                _localWhiteList = config.WhiteListRules.Select(r => new TrafficRule
                {
                    IsEnabled = r.IsEnabled,
                    ProxyId = r.ProxyId,
                    TargetApps = r.TargetApps.ToList(),
                    TargetHosts = r.TargetHosts.ToList()
                }).ToList();
            }
            else
            {
                _localWhiteList = new List<TrafficRule>();
            }

            _blackListProxyId = config.BlackListSelectedProxyId.ToString();
            _currentMode = config.CurrentMode;

            DisconnectAllClients();
        }

        private void DisconnectAllClients()
        {
            foreach (var kvp in _activeClients)
            {
                try
                {
                    kvp.Value.Cts?.Cancel();

                    ForceClose(kvp.Value.Client);
                    ForceClose(kvp.Value.Remote);
                }
                catch { }
            }
            _activeClients.Clear();
        }

        private void ForceClose(TcpClient client)
        {
            if (client == null) return;
            try
            {
                if (client.Connected)
                {
                    client.LingerState = new LingerOption(true, 0);
                }
                client.Close();
                client.Dispose();
            }
            catch { }
        }

        public void Start()
        {
            if (_isRunning) return;

            _processMonitor.Start();
            _isRunning = true;

            try
            {
                _listener = new TcpListener(IPAddress.Any, LocalPort);
                _listener.Start();
                SystemProxyHelper.SetSystemProxy(true, "127.0.0.1", LocalPort);
                Task.Run(() => AcceptClientsLoop());
            }
            catch
            {
                _isRunning = false;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _processMonitor.Stop();
            DisconnectAllClients();
            try { _listener?.Stop(); } catch { }
            SystemProxyHelper.RestoreSystemProxy();
        }

        public void EnforceSystemProxy()
        {
            if (!_isRunning) return;
            SystemProxyHelper.EnforceSystemProxy("127.0.0.1", LocalPort);
        }

        private async Task AcceptClientsLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
                catch { if (!_isRunning) break; }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            Guid connectionId = Guid.NewGuid();
            var ctx = new ClientContext
            {
                Client = client,
                Cts = new CancellationTokenSource()
            };

            _activeClients.TryAdd(connectionId, ctx);

            try
            {
                client.NoDelay = true;
                NetworkStream clientStream = client.GetStream();

                int clientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;
                int pid = SystemProxyHelper.GetPidByPort(clientPort);
                string processName = _processMonitor.GetProcessName(pid);

                byte[] buffer = new byte[BufferSize];
                int bytesRead = await clientStream.ReadAsync(buffer, 0, buffer.Length, ctx.Cts.Token);
                if (bytesRead == 0) return;

                string headerStr = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                if (!TryGetTargetHost(headerStr, out string targetHost, out int targetPort, out bool isConnectMethod)) return;

                ProxyItem? targetProxy = ResolveProxy(processName, targetHost);

                var remoteServer = new TcpClient();
                remoteServer.NoDelay = true;
                ctx.Remote = remoteServer;

                if (targetProxy != null)
                {
                    await remoteServer.ConnectAsync(targetProxy.IpAddress, targetProxy.Port, ctx.Cts.Token);
                    var remoteStream = remoteServer.GetStream();

                    string auth = "";
                    if (!string.IsNullOrEmpty(targetProxy.Username))
                    {
                        string creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{targetProxy.Username}:{targetProxy.Password}"));
                        auth = $"Proxy-Authorization: Basic {creds}\r\n";
                    }

                    string connectReq = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n{auth}\r\n";
                    byte[] reqBytes = Encoding.ASCII.GetBytes(connectReq);
                    await remoteStream.WriteAsync(reqBytes, 0, reqBytes.Length, ctx.Cts.Token);

                    byte[] proxyRespBuffer = new byte[4096];
                    int respLen = await remoteStream.ReadAsync(proxyRespBuffer, 0, proxyRespBuffer.Length, ctx.Cts.Token);
                    string proxyResp = Encoding.ASCII.GetString(proxyRespBuffer, 0, respLen);

                    if (!proxyResp.Contains("200")) return;

                    if (isConnectMethod)
                    {
                        byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                        await clientStream.WriteAsync(ok, 0, ok.Length, ctx.Cts.Token);
                    }
                    else
                    {
                        await remoteStream.WriteAsync(buffer, 0, bytesRead, ctx.Cts.Token);
                    }

                    await BridgeStreams(clientStream, remoteStream, ctx.Cts.Token);
                }
                else
                {
                    await remoteServer.ConnectAsync(targetHost, targetPort, ctx.Cts.Token);
                    var remoteStream = remoteServer.GetStream();

                    if (isConnectMethod)
                    {
                        byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                        await clientStream.WriteAsync(ok, 0, ok.Length, ctx.Cts.Token);
                    }
                    else
                    {
                        await remoteStream.WriteAsync(buffer, 0, bytesRead, ctx.Cts.Token);
                    }

                    await BridgeStreams(clientStream, remoteStream, ctx.Cts.Token);
                }
            }
            catch
            {
            }
            finally
            {
                _activeClients.TryRemove(connectionId, out _);
                ForceClose(client);
                ForceClose(ctx.Remote);
                ctx.Cts?.Dispose();
            }
        }

        private async Task BridgeStreams(NetworkStream a, NetworkStream b, CancellationToken token)
        {
            var task1 = a.CopyToAsync(b, token);
            var task2 = b.CopyToAsync(a, token);
            await Task.WhenAny(task1, task2);
        }

        private bool TryGetTargetHost(string header, out string host, out int port, out bool isConnect)
        {
            host = ""; port = 80; isConnect = false;
            try
            {
                using (var reader = new StringReader(header))
                {
                    string line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) return false;
                    var parts = line.Split(' ');
                    if (parts.Length < 2) return false;
                    string method = parts[0];
                    string url = parts[1];

                    if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                    {
                        isConnect = true;
                        var hp = url.Split(':');
                        host = hp[0];
                        port = hp.Length > 1 ? int.Parse(hp[1]) : 443;
                    }
                    else
                    {
                        if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) { host = uri.Host; port = uri.Port; }
                        else
                        {
                            string headerLine;
                            while ((headerLine = reader.ReadLine()) != null)
                            {
                                if (headerLine.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                                {
                                    var val = headerLine.Substring(5).Trim();
                                    var hp = val.Split(':');
                                    host = hp[0];
                                    port = hp.Length > 1 ? int.Parse(hp[1]) : 80;
                                    break;
                                }
                                if (string.IsNullOrEmpty(headerLine)) break;
                            }
                        }
                    }
                }
                return !string.IsNullOrEmpty(host);
            }
            catch { return false; }
        }

        private ProxyItem? ResolveProxy(string app, string host)
        {
            if (_currentMode == RuleMode.BlackList)
            {
                var mainProxy = _localProxies.FirstOrDefault(p => p.Id.ToString() == _blackListProxyId && p.IsEnabled);
                if (mainProxy == null) return null;

                foreach (var rule in _localBlackList)
                {
                    if (!rule.IsEnabled) continue;
                    if (rule.ProxyId != null && rule.ProxyId != mainProxy.Id) continue;
                    if (IsRuleMatch(rule, app, host)) return null;
                }
                return mainProxy;
            }
            else
            {
                foreach (var rule in _localWhiteList)
                {
                    if (!rule.IsEnabled || rule.ProxyId == null) continue;
                    var proxy = _localProxies.FirstOrDefault(p => p.Id == rule.ProxyId && p.IsEnabled);
                    if (proxy == null) continue;
                    if (IsRuleMatch(rule, app, host)) return proxy;
                }
                return null;
            }
        }

        private bool IsRuleMatch(TrafficRule rule, string app, string host)
        {
            if (rule.TargetApps.Count > 0)
            {
                bool match = false;
                for (int i = 0; i < rule.TargetApps.Count; i++)
                {
                    if (rule.TargetApps[i] == "*" || string.Equals(rule.TargetApps[i], app, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                }
                if (!match) return false;
            }
            if (rule.TargetHosts.Count > 0)
            {
                for (int i = 0; i < rule.TargetHosts.Count; i++)
                {
                    if (rule.TargetHosts[i] == "*" || host.Contains(rule.TargetHosts[i], StringComparison.OrdinalIgnoreCase)) return true;
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
