using ProxyControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyControl.Services
{
    public class DnsProxyService
    {
        private const int LocalDnsPort = 53;
        private const string RemoteDnsHost = "8.8.8.8"; // Google DNS
        private const int RemoteDnsPort = 53;
        private const string FallbackDns = "8.8.8.8";

        private UdpClient? _udpListener;
        private bool _isRunning;
        private CancellationTokenSource? _cts;

        private List<ProxyItem> _localProxies = new List<ProxyItem>();
        private List<TrafficRule> _localBlackList = new List<TrafficRule>();
        private List<TrafficRule> _localWhiteList = new List<TrafficRule>();
        private string? _blackListProxyId;
        private RuleMode _currentMode;

        public DnsProxyService()
        {
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
                IsEnabled = p.IsEnabled,
                CountryCode = p.CountryCode
            }).ToList();

            _localBlackList = config.BlackListRules?.ToList() ?? new List<TrafficRule>();
            _localWhiteList = config.WhiteListRules?.ToList() ?? new List<TrafficRule>();
            _blackListProxyId = config.BlackListSelectedProxyId.ToString();
            _currentMode = config.CurrentMode;
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, LocalDnsPort));
                _isRunning = true;
                _cts = new CancellationTokenSource();

                // Устанавливаем системный DNS на 127.0.0.1
                SystemProxyHelper.SetSystemDns(true);

                Task.Run(ListenLoop, _cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DNS Start Error: {ex.Message}");
                Stop();
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            _udpListener?.Close();
            _udpListener = null;

            // ОБЯЗАТЕЛЬНО возвращаем автоматический DNS (DHCP) при остановке
            SystemProxyHelper.SetSystemDns(false);
        }

        private async Task ListenLoop()
        {
            while (_isRunning && _udpListener != null)
            {
                try
                {
                    var result = await _udpListener.ReceiveAsync();
                    _ = HandleDnsQueryAsync(result.Buffer, result.RemoteEndPoint);
                }
                catch
                {
                    if (!_isRunning) break;
                }
            }
        }

        private async Task HandleDnsQueryAsync(byte[] dnsQuery, IPEndPoint clientEndpoint)
        {
            try
            {
                string domain = ParseDomainFromQuery(dnsQuery);
                var decision = ResolveDnsAction(domain);

                if (decision.Action == RuleAction.Proxy && decision.Proxy != null && decision.Proxy.IsEnabled)
                {
                    await TunnelDnsOverProxy(dnsQuery, clientEndpoint, decision.Proxy);
                }
                else
                {
                    await ForwardDnsDirectly(dnsQuery, clientEndpoint);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DNS Handle Error: {ex.Message}");
            }
        }

        private async Task TunnelDnsOverProxy(byte[] dnsQuery, IPEndPoint clientEndpoint, ProxyItem proxy)
        {
            try
            {
                using (var tcpClient = new TcpClient())
                {
                    await tcpClient.ConnectAsync(proxy.IpAddress, proxy.Port);
                    var stream = tcpClient.GetStream();

                    string auth = "";
                    if (!string.IsNullOrEmpty(proxy.Username))
                    {
                        string creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{proxy.Username}:{proxy.Password}"));
                        auth = $"Proxy-Authorization: Basic {creds}\r\n";
                    }

                    string connectReq = $"CONNECT {RemoteDnsHost}:{RemoteDnsPort} HTTP/1.1\r\nHost: {RemoteDnsHost}:{RemoteDnsPort}\r\n{auth}\r\n";
                    byte[] reqBytes = Encoding.ASCII.GetBytes(connectReq);
                    await stream.WriteAsync(reqBytes, 0, reqBytes.Length);

                    byte[] buffer = new byte[4096];
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    string response = Encoding.ASCII.GetString(buffer, 0, read);

                    if (!response.Contains("200")) return;

                    byte[] lengthPrefix = BitConverter.GetBytes((ushort)dnsQuery.Length);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lengthPrefix);

                    await stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length);
                    await stream.WriteAsync(dnsQuery, 0, dnsQuery.Length);

                    byte[] lenBuf = new byte[2];
                    int lenRead = await ReadExactAsync(stream, lenBuf, 2);
                    if (lenRead != 2) return;

                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
                    ushort responseLength = BitConverter.ToUInt16(lenBuf, 0);

                    byte[] responseData = new byte[responseLength];
                    int dataRead = await ReadExactAsync(stream, responseData, responseLength);

                    if (dataRead == responseLength && _udpListener != null)
                    {
                        await _udpListener.SendAsync(responseData, responseData.Length, clientEndpoint);
                    }
                }
            }
            catch { }
        }

        private async Task ForwardDnsDirectly(byte[] dnsQuery, IPEndPoint clientEndpoint)
        {
            try
            {
                using (var udpForwarder = new UdpClient())
                {
                    udpForwarder.Client.ReceiveTimeout = 2000;
                    udpForwarder.Client.SendTimeout = 2000;

                    await udpForwarder.SendAsync(dnsQuery, dnsQuery.Length, FallbackDns, 53);

                    var result = await udpForwarder.ReceiveAsync();
                    if (result.Buffer != null && result.Buffer.Length > 0 && _udpListener != null)
                    {
                        await _udpListener.SendAsync(result.Buffer, result.Buffer.Length, clientEndpoint);
                    }
                }
            }
            catch { }
        }

        private (RuleAction Action, ProxyItem? Proxy) ResolveDnsAction(string host)
        {
            if (_currentMode == RuleMode.BlackList)
            {
                var mainProxy = _localProxies.FirstOrDefault(p => p.Id.ToString() == _blackListProxyId && p.IsEnabled);

                foreach (var rule in _localBlackList)
                {
                    if (!rule.IsEnabled) continue;
                    if (IsHostMatch(rule, host))
                    {
                        if (rule.Action == RuleAction.Block) return (RuleAction.Direct, null);
                        if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null);
                        if (rule.ProxyId != null)
                        {
                            var p = _localProxies.FirstOrDefault(x => x.Id == rule.ProxyId);
                            if (p != null && p.IsEnabled) return (RuleAction.Proxy, p);
                            return (RuleAction.Direct, null);
                        }
                        if (mainProxy != null) return (RuleAction.Proxy, mainProxy);
                    }
                }

                if (mainProxy != null) return (RuleAction.Proxy, mainProxy);
                return (RuleAction.Direct, null);
            }
            else // White List
            {
                foreach (var rule in _localWhiteList)
                {
                    if (!rule.IsEnabled) continue;
                    if (IsHostMatch(rule, host))
                    {
                        if (rule.Action == RuleAction.Block) return (RuleAction.Direct, null);
                        if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null);
                        if (rule.ProxyId != null)
                        {
                            var p = _localProxies.FirstOrDefault(x => x.Id == rule.ProxyId);
                            if (p != null && p.IsEnabled) return (RuleAction.Proxy, p);

                            var mainProxyFallback = _localProxies.FirstOrDefault(p => p.Id.ToString() == _blackListProxyId && p.IsEnabled);
                            if (mainProxyFallback != null) return (RuleAction.Proxy, mainProxyFallback);
                        }

                        var mainProxy = _localProxies.FirstOrDefault(p => p.Id.ToString() == _blackListProxyId && p.IsEnabled);
                        if (mainProxy != null) return (RuleAction.Proxy, mainProxy);
                    }
                }
                return (RuleAction.Direct, null);
            }
        }

        private bool IsHostMatch(TrafficRule rule, string host)
        {
            if (rule.TargetHosts.Count > 0)
            {
                for (int i = 0; i < rule.TargetHosts.Count; i++)
                {
                    if (rule.TargetHosts[i] == "*" || host.Contains(rule.TargetHosts[i], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            return true;
        }

        private string ParseDomainFromQuery(byte[] data)
        {
            try
            {
                int offset = 12;
                StringBuilder sb = new StringBuilder();
                while (offset < data.Length)
                {
                    byte len = data[offset];
                    if (len == 0) break;
                    if ((len & 0xC0) == 0xC0) break;

                    offset++;
                    if (sb.Length > 0) sb.Append(".");

                    if (offset + len > data.Length) break;

                    sb.Append(Encoding.ASCII.GetString(data, offset, len));
                    offset += len;
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int length)
        {
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = await stream.ReadAsync(buffer, totalRead, length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            return totalRead;
        }
    }
}