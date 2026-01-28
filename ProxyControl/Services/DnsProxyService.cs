using ProxyControl.Models;
using System;
using System.Collections.Concurrent;
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
        private int RemoteDnsPort = 53;
        private string _remoteDnsHost = "8.8.8.8";

        private UdpClient? _udpListener;
        private bool _isRunning;
        private CancellationTokenSource? _cts;

        private List<ProxyItem> _localProxies = new List<ProxyItem>();
        private List<TrafficRule> _localBlackList = new List<TrafficRule>();
        private List<TrafficRule> _localWhiteList = new List<TrafficRule>();
        private string? _blackListProxyId;
        private RuleMode _currentMode;
        private readonly TrafficMonitorService _trafficMonitor;

        private readonly ConcurrentDictionary<string, ConcurrentBag<PooledTcpClient>> _connectionPool
            = new ConcurrentDictionary<string, ConcurrentBag<PooledTcpClient>>();

        private class PooledTcpClient : IDisposable
        {
            public TcpClient Client { get; }
            public NetworkStream Stream { get; }
            public DateTime LastUsed { get; set; }

            public PooledTcpClient(TcpClient client)
            {
                Client = client;
                Stream = client.GetStream();
                LastUsed = DateTime.Now;
            }

            public void Dispose()
            {
                try { Stream?.Dispose(); } catch { }
                try { Client?.Close(); } catch { }
            }

            public bool IsConnected()
            {
                try
                {
                    return Client.Connected && !(Client.Client.Poll(1, SelectMode.SelectRead) && Client.Client.Available == 0);
                }
                catch { return false; }
            }
        }

        public DnsProxyService(TrafficMonitorService trafficMonitor)
        {
            _trafficMonitor = trafficMonitor;
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
                CountryCode = p.CountryCode,
                Type = p.Type // Copy type
            }).ToList();

            _localBlackList = config.BlackListRules?.ToList() ?? new List<TrafficRule>();
            _localWhiteList = config.WhiteListRules?.ToList() ?? new List<TrafficRule>();
            _blackListProxyId = config.BlackListSelectedProxyId.ToString();
            _currentMode = config.CurrentMode;

            // Применяем настройки DNS
            if (!string.IsNullOrWhiteSpace(config.DnsHost))
            {
                _remoteDnsHost = config.DnsHost;
            }
            else
            {
                _remoteDnsHost = "8.8.8.8";
            }

            ClearConnectionPool();
        }

        private void ClearConnectionPool()
        {
            foreach (var bag in _connectionPool.Values)
            {
                while (bag.TryTake(out var conn))
                {
                    conn.Dispose();
                }
            }
            _connectionPool.Clear();
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                // ИСПРАВЛЕНО: Вернул Loopback (127.0.0.1). 
                // Any (0.0.0.0) часто блокируется фаерволом Windows для пользовательских приложений.
                _udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, LocalDnsPort));
                _isRunning = true;
                _cts = new CancellationTokenSource();

                SystemProxyHelper.SetSystemDns(true);

                Task.Run(ListenLoop, _cts.Token);

                Task.Run(async () =>
                {
                    while (_isRunning)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        CleanupStaleConnections();
                    }
                }, _cts.Token);
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
            try { _udpListener?.Close(); } catch { }
            _udpListener = null;

            ClearConnectionPool();
            SystemProxyHelper.SetSystemDns(false);
        }

        private void CleanupStaleConnections()
        {
            foreach (var key in _connectionPool.Keys)
            {
                if (_connectionPool.TryGetValue(key, out var bag))
                {
                    var active = new List<PooledTcpClient>();
                    while (bag.TryTake(out var conn))
                    {
                        if ((DateTime.Now - conn.LastUsed).TotalSeconds < 60 && conn.IsConnected())
                        {
                            active.Add(conn);
                        }
                        else
                        {
                            conn.Dispose();
                        }
                    }
                    foreach (var c in active) bag.Add(c);
                }
            }
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

                // Fallback: Если парсер не справился, пробуем отправить "как есть"
                if (string.IsNullOrEmpty(domain))
                {
                    await ForwardDnsDirectly(dnsQuery, clientEndpoint);
                    return;
                }

                var decision = ResolveDnsAction(domain);

                bool success = false;

                string logResult = "Direct";
                string logColor = "#AAAAAA";

                if (decision.Action == RuleAction.Block)
                {
                    // Block
                    logResult = "BLOCKED";
                    logColor = "#FF5555";
                }
                else if (decision.Action == RuleAction.Proxy && decision.Proxy != null && decision.Proxy.IsEnabled)
                {
                    logResult = $"Proxy: {decision.Proxy.IpAddress}";
                    logColor = "#55FF55";
                    success = await TunnelDnsOverProxy(dnsQuery, clientEndpoint, decision.Proxy);
                }

                // Log to Traffic Monitor
                // DNS is usually "svchost" or "System", but we can try to find process or just say "DNS"
                // Ideally we map port to PID but for UDP 53 it's tricky.
                _trafficMonitor.CreateConnectionItem("DNS System", null, domain, logResult, "UDP 53", null, logColor);

                // Если правило не прокси или туннелирование не удалось -> отправляем напрямую
                if (!success && decision.Action != RuleAction.Block)
                {
                    await ForwardDnsDirectly(dnsQuery, clientEndpoint);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DNS Handle Error: {ex.Message}");
            }
        }

        private async Task<PooledTcpClient> GetConnectionAsync(ProxyItem proxy)
        {
            var pool = _connectionPool.GetOrAdd(proxy.Id, _ => new ConcurrentBag<PooledTcpClient>());

            while (pool.TryTake(out var conn))
            {
                if (conn.IsConnected())
                {
                    return conn;
                }
                conn.Dispose();
            }

            var tcpClient = new TcpClient();
            // Таймаут подключения 3 сек, чтобы не висеть долго
            var connectTask = tcpClient.ConnectAsync(proxy.IpAddress, proxy.Port);
            if (await Task.WhenAny(connectTask, Task.Delay(3000)) != connectTask)
            {
                tcpClient.Close();
                throw new TimeoutException("Proxy connect timeout");
            }
            await connectTask;

            var stream = tcpClient.GetStream();

            string auth = "";
            if (!string.IsNullOrEmpty(proxy.Username))
            {
                string creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{proxy.Username}:{proxy.Password}"));
                auth = $"Proxy-Authorization: Basic {creds}\r\n";
            }

            string connectReq = $"CONNECT {_remoteDnsHost}:{RemoteDnsPort} HTTP/1.1\r\nHost: {_remoteDnsHost}:{RemoteDnsPort}\r\n{auth}\r\n";
            byte[] reqBytes = Encoding.ASCII.GetBytes(connectReq);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            await stream.FlushAsync();

            byte[] buffer = new byte[4096];
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
            string response = Encoding.ASCII.GetString(buffer, 0, read);

            if (!response.Contains("200"))
            {
                tcpClient.Close();
                throw new Exception("Proxy CONNECT failed");
            }

            return new PooledTcpClient(tcpClient);
        }

        private void ReturnConnection(ProxyItem proxy, PooledTcpClient conn)
        {
            if (conn.IsConnected())
            {
                conn.LastUsed = DateTime.Now;
                var pool = _connectionPool.GetOrAdd(proxy.Id, _ => new ConcurrentBag<PooledTcpClient>());
                pool.Add(conn);
            }
            else
            {
                conn.Dispose();
            }
        }

        // Возвращает true, если успешно
        private async Task<bool> TunnelDnsOverProxy(byte[] dnsQuery, IPEndPoint clientEndpoint, ProxyItem proxy)
        {
            if (proxy.Type == ProxyType.Socks5)
            {
                // Для SOCKS5 используем UDP Associate (лучше для DNS)
                return await TunnelDnsSocks5Udp(dnsQuery, clientEndpoint, proxy);
            }

            // Для HTTP прокси используем TCP connect (существующая логика)
            return await TunnelDnsHttpTcp(dnsQuery, clientEndpoint, proxy);
        }

        private async Task<bool> TunnelDnsSocks5Udp(byte[] dnsQuery, IPEndPoint clientEndpoint, ProxyItem proxy)
        {
            try
            {
                // UDP Associate требует TCP контрольного соединения
                // В идеале мы должны кэшировать это соединение (Client + RelayEndpoint)
                // Но для простоты пока создаем на каждый запрос (можно оптимизировать позже, если будет медленно)
                // TODO: Optimize by pooling SOCKS5 control connections

                using (var cts = new CancellationTokenSource(3000)) // 3 сек таймаут
                {
                    var (controlClient, relayEp) = await Socks5Client.UdpAssociateAsync(proxy, cts.Token);

                    using (controlClient)
                    using (var udpClient = new UdpClient())
                    {
                        udpClient.Client.ReceiveTimeout = 2000;
                        udpClient.Client.SendTimeout = 2000;

                        // Pack DNS query into SOCKS5 UDP header
                        // Нам нужно знать куда отправлять. Для DNS это _remoteDnsHost:RemoteDnsPort
                        byte[] packed = Socks5Client.PackUdp(dnsQuery, _remoteDnsHost, RemoteDnsPort);

                        await udpClient.SendAsync(packed, packed.Length, relayEp);

                        var recvTask = udpClient.ReceiveAsync();
                        if (await Task.WhenAny(recvTask, Task.Delay(2000)) == recvTask)
                        {
                            var result = await recvTask;
                            byte[] unpacked = Socks5Client.UnpackUdp(result.Buffer);

                            if (unpacked.Length > 0 && _udpListener != null)
                            {
                                await _udpListener.SendAsync(unpacked, unpacked.Length, clientEndpoint);
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SOCKS5 UDP DNS Error: {ex.Message}");
            }
            return false;
        }

        private async Task<bool> TunnelDnsHttpTcp(byte[] dnsQuery, IPEndPoint clientEndpoint, ProxyItem proxy)
        {
            PooledTcpClient? conn = null;
            bool retry = false;

            try
            {
                conn = await GetConnectionAsync(proxy);
                await PerformDnsTransaction(conn, dnsQuery, clientEndpoint);
                ReturnConnection(proxy, conn);
                return true;
            }
            catch
            {
                conn?.Dispose();
                retry = true;
            }

            if (retry)
            {
                try
                {
                    conn = await GetConnectionAsync(proxy);
                    await PerformDnsTransaction(conn, dnsQuery, clientEndpoint);
                    ReturnConnection(proxy, conn);
                    return true;
                }
                catch
                {
                    conn?.Dispose();
                    return false; // Fail -> caller will fallback to direct
                }
            }
            return false;
        }

        private async Task PerformDnsTransaction(PooledTcpClient conn, byte[] dnsQuery, IPEndPoint clientEndpoint)
        {
            var stream = conn.Stream;

            byte[] lengthPrefix = BitConverter.GetBytes((ushort)dnsQuery.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lengthPrefix);

            await stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length);
            await stream.WriteAsync(dnsQuery, 0, dnsQuery.Length);
            await stream.FlushAsync();

            byte[] lenBuf = new byte[2];
            int lenRead = await ReadExactAsync(stream, lenBuf, 2);
            if (lenRead != 2) throw new Exception("Incomplete DNS length");

            if (BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
            ushort responseLength = BitConverter.ToUInt16(lenBuf, 0);

            byte[] responseData = new byte[responseLength];
            int dataRead = await ReadExactAsync(stream, responseData, responseLength);

            if (dataRead == responseLength && _udpListener != null)
            {
                await _udpListener.SendAsync(responseData, responseData.Length, clientEndpoint);
            }
        }

        private async Task ForwardDnsDirectly(byte[] dnsQuery, IPEndPoint clientEndpoint)
        {
            try
            {
                // Используем try-catch внутри для повторной попытки
                await AttemptDirectForward(dnsQuery, clientEndpoint, _remoteDnsHost);
            }
            catch
            {
                // Если основной DNS не ответил, можно попробовать Google как последний шанс, 
                // но лучше просто выйти, чтобы браузер повторил сам.
            }
        }

        private async Task AttemptDirectForward(byte[] dnsQuery, IPEndPoint clientEndpoint, string dnsServer)
        {
            using (var udpForwarder = new UdpClient())
            {
                udpForwarder.Client.ReceiveTimeout = 2000;
                udpForwarder.Client.SendTimeout = 2000;

                // Используем Connect, чтобы отфильтровать мусор
                udpForwarder.Connect(dnsServer, 53);
                await udpForwarder.SendAsync(dnsQuery, dnsQuery.Length);

                var receiveTask = udpForwarder.ReceiveAsync();
                var timeoutTask = Task.Delay(2000); // 2 сек таймаут

                var completed = await Task.WhenAny(receiveTask, timeoutTask);
                if (completed == receiveTask)
                {
                    var result = await receiveTask;
                    if (result.Buffer != null && result.Buffer.Length > 0 && _udpListener != null)
                    {
                        await _udpListener.SendAsync(result.Buffer, result.Buffer.Length, clientEndpoint);
                    }
                }
            }
        }

        private (RuleAction Action, ProxyItem? Proxy) ResolveDnsAction(string host)
        {
            if (_currentMode == RuleMode.BlackList)
            {
                var mainProxy = _localProxies.FirstOrDefault(p => p.Id.Equals(_blackListProxyId, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);

                foreach (var rule in _localBlackList)
                {
                    if (!rule.IsEnabled) continue;
                    if (IsHostMatch(rule, host))
                    {
                        if (rule.Action == RuleAction.Block) return (RuleAction.Direct, null);
                        if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null);
                        if (rule.ProxyId != null)
                        {
                            var p = _localProxies.FirstOrDefault(x => x.Id.Equals(rule.ProxyId, StringComparison.OrdinalIgnoreCase));
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
                            var p = _localProxies.FirstOrDefault(x => x.Id.Equals(rule.ProxyId, StringComparison.OrdinalIgnoreCase));
                            if (p != null && p.IsEnabled) return (RuleAction.Proxy, p);

                            var mainProxyFallback = _localProxies.FirstOrDefault(p => p.Id.Equals(_blackListProxyId, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
                            if (mainProxyFallback != null) return (RuleAction.Proxy, mainProxyFallback);
                        }

                        var mainProxy = _localProxies.FirstOrDefault(p => p.Id.Equals(_blackListProxyId, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
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
                int offset = 12; // Пропускаем заголовок
                if (data.Length <= offset) return "";
                return ParseName(data, ref offset);
            }
            catch
            {
                return "";
            }
        }

        private string ParseName(byte[] data, ref int offset)
        {
            StringBuilder sb = new StringBuilder();
            int jumpOffset = -1;
            int steps = 0;

            while (true)
            {
                if (steps++ > 20) break;
                if (offset >= data.Length) break;

                byte len = data[offset];

                if (len == 0)
                {
                    offset++;
                    break;
                }

                // Обработка сжатия (0xC0)
                if ((len & 0xC0) == 0xC0)
                {
                    if (offset + 1 >= data.Length) break;

                    int pointer = ((len & 0x3F) << 8) | data[offset + 1];
                    if (jumpOffset == -1) jumpOffset = offset + 2;

                    offset = pointer;
                    continue;
                }

                offset++;
                if (sb.Length > 0) sb.Append(".");

                if (offset + len > data.Length) break;

                sb.Append(Encoding.ASCII.GetString(data, offset, len));
                offset += len;
            }

            if (jumpOffset != -1) offset = jumpOffset;
            return sb.ToString();
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