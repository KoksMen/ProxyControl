using ProxyControl.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
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
        private string _fallbackDnsHost = "1.1.1.1";
        private AppConfig _config = new AppConfig();
        private string _lastAppliedDnsConfigSignature = string.Empty;

        private UdpClient? _udpListener;
        private bool _isRunning;
        private CancellationTokenSource? _cts;

        private List<ProxyItem> _localProxies = new List<ProxyItem>();
        private List<TrafficRule> _localBlackList = new List<TrafficRule>();
        private List<TrafficRule> _localWhiteList = new List<TrafficRule>();

        // High-performance compiled rule indices
        private FastRuleIndex _fastBlackIndex = FastRuleIndex.Empty;
        private FastRuleIndex _fastWhiteIndex = FastRuleIndex.Empty;
        private string? _blackListProxyId;
        private RuleMode _currentMode;
        private bool _isWebRtcBlockingEnabled = true;
        private readonly TrafficMonitorService _trafficMonitor;
        public event Action<ConnectionLog>? OnConnectionLog;

        private readonly ConcurrentDictionary<string, ConcurrentBag<PooledTcpClient>> _connectionPool
            = new ConcurrentDictionary<string, ConcurrentBag<PooledTcpClient>>();

        private sealed class DnsCacheEntry
        {
            public byte[] ResponseData { get; }
            public DateTime ExpiresUtc { get; }

            public DnsCacheEntry(byte[] responseData, DateTime expiresUtc)
            {
                ResponseData = responseData;
                ExpiresUtc = expiresUtc;
            }
        }

        private readonly ConcurrentDictionary<string, DnsCacheEntry> _dnsCache
            = new ConcurrentDictionary<string, DnsCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class Socks5UdpSession : IDisposable
        {
            public TcpClient ControlClient { get; }
            public IPEndPoint RelayEp { get; }
            public UdpClient UdpClient { get; }
            public DateTime LastUsed { get; set; }
            public SemaphoreSlim Lock { get; } = new SemaphoreSlim(1, 1);

            public Socks5UdpSession(TcpClient controlClient, IPEndPoint relayEp, UdpClient udpClient)
            {
                ControlClient = controlClient;
                RelayEp = relayEp;
                UdpClient = udpClient;
                LastUsed = DateTime.UtcNow;
            }

            public bool IsAlive()
            {
                try
                {
                    return ControlClient.Connected &&
                           !(ControlClient.Client.Poll(1, SelectMode.SelectRead) && ControlClient.Client.Available == 0);
                }
                catch { return false; }
            }

            public void Dispose()
            {
                try { UdpClient?.Close(); } catch { }
                try { UdpClient?.Dispose(); } catch { }
                try { ControlClient?.Close(); } catch { }
                try { ControlClient?.Dispose(); } catch { }
                try { Lock?.Dispose(); } catch { }
            }
        }

        private readonly ConcurrentDictionary<string, Socks5UdpSession> _socks5UdpPool
            = new ConcurrentDictionary<string, Socks5UdpSession>();

        private class PooledTcpClient : IDisposable
        {
            public TcpClient Client { get; }
            public Stream Stream { get; }
            public DateTime LastUsed { get; set; }

            public PooledTcpClient(TcpClient client, Stream stream)
            {
                Client = client;
                Stream = stream;
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
            _config = config;
            _localProxies = proxies.Select(p => new ProxyItem
            {
                Id = p.Id,
                Name = p.Name,
                IpAddress = p.IpAddress,
                Port = p.Port,
                Username = p.Username,
                Password = p.Password,
                IsEnabled = p.IsEnabled,
                CountryCode = p.CountryCode,
                Type = p.Type,
                UseTls = p.UseTls,
                UseSsl = p.UseSsl
            }).ToList();

            _localBlackList = config.BlackListRules?.ToList() ?? new List<TrafficRule>();
            _localWhiteList = config.WhiteListRules?.ToList() ?? new List<TrafficRule>();

            _fastBlackIndex = new FastRuleIndex(_localBlackList);
            _fastWhiteIndex = new FastRuleIndex(_localWhiteList);

            _blackListProxyId = config.BlackListSelectedProxyId.ToString();
            _currentMode = config.CurrentMode;
            _isWebRtcBlockingEnabled = config.IsWebRtcBlockingEnabled;

            // DNS hosts may be IP addresses or domain names.
            _remoteDnsHost = NormalizeDnsHost(config.DnsHost, "8.8.8.8");
            _fallbackDnsHost = NormalizeDnsHost(config.DnsFallbackHost, "1.1.1.1");

            ClearConnectionPool();

            if (_isRunning)
            {
                ApplySystemDnsIfNeeded(force: false);
            }
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

            foreach (var session in _socks5UdpPool.Values)
            {
                session.Dispose();
            }
            _socks5UdpPool.Clear();

            _dnsCache.Clear();
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

                ApplySystemDnsIfNeeded(force: true);

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
            SystemProxyHelper.RestoreSystemDnsIfManagedByProxyControl();
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

            foreach (var kvp in _socks5UdpPool)
            {
                if ((DateTime.UtcNow - kvp.Value.LastUsed).TotalSeconds > 60 || !kvp.Value.IsAlive())
                {
                    if (_socks5UdpPool.TryRemove(kvp.Key, out var staleSession))
                    {
                        staleSession.Dispose();
                    }
                }
            }

            var now = DateTime.UtcNow;
            foreach (var kvp in _dnsCache)
            {
                if (now >= kvp.Value.ExpiresUtc)
                {
                    _dnsCache.TryRemove(kvp.Key, out _);
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
                if (!TryParseQuestion(dnsQuery, out string domain, out ushort qtype, out int questionEndOffset))
                {
                    await ForwardDnsDirectly(dnsQuery, clientEndpoint, cacheKey: null);
                    return;
                }

                // 1.3 Fast AAAA Drop (IPv4-Only): eliminate 2-3s timeouts when IPv6 is blocked or unavailable
                if (_config.DisableIpv6Dns && qtype == 28)
                {
                    byte[] aaaaDrop = BuildEmptyDnsResponse(dnsQuery, questionEndOffset, rcode: 0);
                    if (_udpListener != null)
                    {
                        await _udpListener.SendAsync(aaaaDrop, aaaaDrop.Length, clientEndpoint);
                    }
                    _trafficMonitor.CreateConnectionItem("DNS System", null, domain, "IPv6 Drop (AAAA)", "Fast Drop", null, "#888888");
                    OnConnectionLog?.Invoke(new ConnectionLog
                    {
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        ProcessName = "DNS System",
                        Host = domain,
                        Result = "IPv6 Drop (AAAA)",
                        Color = "#888888",
                        Type = TrafficType.DNS
                    });
                    return;
                }

                // WebRTC Protection: Block STUN/TURN server DNS resolution (if enabled)
                if (_isWebRtcBlockingEnabled && IsStunServer(domain))
                {
                    AppLoggerService.Instance.Warning("WebRTC", $"DNS blocked STUN/TURN: {domain}");
                    _trafficMonitor.CreateConnectionItem("DNS System", null, domain, "BLOCKED (STUN)", "WebRTC Protection", null, "#FF5555");
                    OnConnectionLog?.Invoke(new ConnectionLog
                    {
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        ProcessName = "DNS System",
                        Host = domain,
                        Result = "BLOCKED (STUN)",
                        Color = "#FF5555",
                        Type = TrafficType.DNS
                    });
                    // Don't respond - let it timeout (blocks WebRTC ICE candidate gathering)
                    return;
                }

                // 1.1 Local TTL DNS Cache
                string cacheKey = $"{qtype}:{domain.TrimEnd('.').ToLowerInvariant()}";
                if (TryServeFromDnsCache(cacheKey, dnsQuery, clientEndpoint, domain))
                {
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
                    logResult = $"Proxy: {decision.Proxy.Name}";
                    logColor = "#55FF55";
                    success = await TunnelDnsOverProxy(dnsQuery, clientEndpoint, decision.Proxy, cacheKey);
                }

                if (!success && decision.Action != RuleAction.Block && _config.EnableDoh)
                {
                    success = await ForwardDnsOverHttpsAsync(dnsQuery, clientEndpoint, _cts?.Token ?? CancellationToken.None, cacheKey);
                    if (success)
                    {
                        logResult = "DoH";
                        logColor = "#55CCFF";
                    }
                }

                // Log to Traffic Monitor
                _trafficMonitor.CreateConnectionItem("DNS System", null, domain, logResult, "UDP 53", null, logColor);
                OnConnectionLog?.Invoke(new ConnectionLog
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    ProcessName = "DNS System",
                    Host = domain,
                    Result = logResult,
                    Color = logColor,
                    Type = TrafficType.DNS
                });

                // Если правило не прокси или туннелирование не удалось -> отправляем напрямую
                if (!success && decision.Action != RuleAction.Block)
                {
                    await ForwardDnsDirectly(dnsQuery, clientEndpoint, cacheKey);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DNS Handle Error: {ex.Message}");
            }
        }

        private async Task<bool> ForwardDnsOverHttpsAsync(byte[] dnsQuery, IPEndPoint clientEndpoint, CancellationToken token, string? cacheKey)
        {
            var endpoints = new List<string>(2);
            if (DnsOverHttpsClient.TryGetEndpoint(_config, out var primary, out _)) endpoints.Add(primary);
            if (_config.EnableDohFallback &&
                DnsOverHttpsClient.TryGetFallbackEndpoint(_config, out var fallback, out _) &&
                !endpoints.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                endpoints.Add(fallback);
            }

            foreach (var endpoint in endpoints)
            {
                try
                {
                    var response = await DnsOverHttpsClient.QueryAsync(dnsQuery, endpoint, token);
                    if (response is { Length: > 0 } && _udpListener != null)
                    {
                        await _udpListener.SendAsync(response, response.Length, clientEndpoint);
                        if (cacheKey != null) CacheDnsResponse(cacheKey, response);
                        return true;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return false;
                }
                catch { }
            }

            return false;
        }

        // STUN server detection (same logic as TcpProxyService)
        private bool IsStunServer(string host)
        {
            // Quick check: most hosts are NOT stun
            if (host.Length < 4) return false;

            // Check for common STUN/TURN patterns
            if (host.StartsWith("stun.", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("turn.", StringComparison.OrdinalIgnoreCase)) return true;

            if (host.IndexOf(".stun.", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (host.IndexOf(".turn.", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (host.EndsWith(".stun", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith(".turn", StringComparison.OrdinalIgnoreCase)) return true;

            // Block common WebRTC relay patterns
            if (host.IndexOf("webrtc", StringComparison.OrdinalIgnoreCase) >= 0 &&
               (host.IndexOf("relay", StringComparison.OrdinalIgnoreCase) >= 0 || host.IndexOf("ice", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return false;
        }

        private async Task<PooledTcpClient> GetConnectionAsync(ProxyItem proxy, string dnsHost)
        {
            var poolKey = $"{proxy.Id}:{dnsHost}";
            var pool = _connectionPool.GetOrAdd(poolKey, _ => new ConcurrentBag<PooledTcpClient>());

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

            Stream stream = tcpClient.GetStream();
            if (proxy.UseTls || proxy.UseSsl)
            {
                var sslStream = new SslStream(stream, false, ProxyTlsValidator.Validate);
                await sslStream.AuthenticateAsClientAsync(proxy.IpAddress);
                stream = sslStream;
            }

            string auth = "";
            if (!string.IsNullOrEmpty(proxy.Username))
            {
                string creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{proxy.Username}:{proxy.Password}"));
                auth = $"Proxy-Authorization: Basic {creds}\r\n";
            }

            string connectReq = $"CONNECT {dnsHost}:{RemoteDnsPort} HTTP/1.1\r\nHost: {dnsHost}:{RemoteDnsPort}\r\n{auth}\r\n";
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

            return new PooledTcpClient(tcpClient, stream);
        }

        private void ReturnConnection(ProxyItem proxy, string dnsHost, PooledTcpClient conn)
        {
            if (conn.IsConnected())
            {
                conn.LastUsed = DateTime.Now;
                var poolKey = $"{proxy.Id}:{dnsHost}";
                var pool = _connectionPool.GetOrAdd(poolKey, _ => new ConcurrentBag<PooledTcpClient>());
                pool.Add(conn);
            }
            else
            {
                conn.Dispose();
            }
        }

        // Возвращает true, если успешно
        private async Task<bool> TunnelDnsOverProxy(byte[] dnsQuery, IPEndPoint clientEndpoint, ProxyItem proxy, string cacheKey)
        {
            foreach (var dnsHost in GetConfiguredDnsHosts())
            {
                if (proxy.Type == ProxyType.Socks5)
                {
                    // Для SOCKS5 используем UDP Associate (лучше для DNS)
                    if (await TunnelDnsSocks5Udp(dnsQuery, clientEndpoint, proxy, dnsHost, cacheKey))
                    {
                        return true;
                    }
                }
                else if (await TunnelDnsHttpTcp(dnsQuery, clientEndpoint, proxy, dnsHost, cacheKey))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<bool> TunnelDnsSocks5Udp(byte[] dnsQuery, IPEndPoint clientEndpoint, ProxyItem proxy, string dnsHost, string cacheKey)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Socks5UdpSession? session = null;
                if (!_socks5UdpPool.TryGetValue(proxy.Id, out session) || !session.IsAlive())
                {
                    if (session != null)
                    {
                        _socks5UdpPool.TryRemove(proxy.Id, out _);
                        session.Dispose();
                    }

                    try
                    {
                        using var cts = new CancellationTokenSource(3000);
                        var (controlClient, relayEp) = await Socks5Client.UdpAssociateAsync(proxy, cts.Token);
                        var udpClient = new UdpClient();
                        udpClient.Client.ReceiveTimeout = 2000;
                        udpClient.Client.SendTimeout = 2000;
                        session = new Socks5UdpSession(controlClient, relayEp, udpClient);
                        _socks5UdpPool[proxy.Id] = session;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"SOCKS5 UDP Associate creation failed: {ex.Message}");
                        return false;
                    }
                }

                if (session != null)
                {
                    bool lockAcquired = false;
                    try
                    {
                        lockAcquired = await session.Lock.WaitAsync(2000);
                        if (!lockAcquired)
                        {
                            continue;
                        }

                        if (!session.IsAlive())
                        {
                            _socks5UdpPool.TryRemove(proxy.Id, out _);
                            session.Dispose();
                            continue;
                        }

                        byte[] packed = Socks5Client.PackUdp(dnsQuery, dnsHost, RemoteDnsPort);
                        await session.UdpClient.SendAsync(packed, packed.Length, session.RelayEp);

                        var recvTask = session.UdpClient.ReceiveAsync();
                        if (await Task.WhenAny(recvTask, Task.Delay(2000)) == recvTask)
                        {
                            var result = await recvTask;
                            byte[] unpacked = Socks5Client.UnpackUdp(result.Buffer);

                            if (unpacked.Length > 0 && _udpListener != null)
                            {
                                await _udpListener.SendAsync(unpacked, unpacked.Length, clientEndpoint);
                                CacheDnsResponse(cacheKey, unpacked);
                                session.LastUsed = DateTime.UtcNow;
                                return true;
                            }
                        }
                        else
                        {
                            // Relay timeout, drop session to recreate on retry
                            _socks5UdpPool.TryRemove(proxy.Id, out _);
                            session.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"SOCKS5 UDP DNS Error: {ex.Message}");
                        _socks5UdpPool.TryRemove(proxy.Id, out _);
                        session?.Dispose();
                    }
                    finally
                    {
                        if (lockAcquired && session != null)
                        {
                            try { session.Lock.Release(); } catch { }
                        }
                    }
                }
            }

            return false;
        }

        private async Task<bool> TunnelDnsHttpTcp(byte[] dnsQuery, IPEndPoint clientEndpoint, ProxyItem proxy, string dnsHost, string cacheKey)
        {
            PooledTcpClient? conn = null;
            bool retry = false;

            try
            {
                conn = await GetConnectionAsync(proxy, dnsHost);
                var resp = await PerformDnsTransaction(conn, dnsQuery, clientEndpoint);
                ReturnConnection(proxy, dnsHost, conn);
                if (resp != null) CacheDnsResponse(cacheKey, resp);
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
                    conn = await GetConnectionAsync(proxy, dnsHost);
                    var resp = await PerformDnsTransaction(conn, dnsQuery, clientEndpoint);
                    ReturnConnection(proxy, dnsHost, conn);
                    if (resp != null) CacheDnsResponse(cacheKey, resp);
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

        private async Task<byte[]?> PerformDnsTransaction(PooledTcpClient conn, byte[] dnsQuery, IPEndPoint clientEndpoint)
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
                return responseData;
            }
            return null;
        }

        private async Task ForwardDnsDirectly(byte[] dnsQuery, IPEndPoint clientEndpoint, string? cacheKey = null)
        {
            if (_config.PreferPrimaryDns)
            {
                foreach (var server in GetConfiguredDnsHosts().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    using var serverTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
                    serverTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var response = await QueryDnsDirectAsync(dnsQuery, server, serverTimeout.Token);
                    if (IsUsableDnsResponse(response) && _udpListener != null)
                    {
                        await _udpListener.SendAsync(response!, response!.Length, clientEndpoint);
                        if (cacheKey != null) CacheDnsResponse(cacheKey, response!);
                        return;
                    }
                }

                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            var pending = GetConfiguredDnsHosts()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(server => QueryDnsDirectAsync(dnsQuery, server, timeoutCts.Token))
                .ToList();

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var response = await completed;
                if (IsUsableDnsResponse(response) && _udpListener != null)
                {
                    timeoutCts.Cancel();
                    await _udpListener.SendAsync(response!, response!.Length, clientEndpoint);
                    if (cacheKey != null) CacheDnsResponse(cacheKey, response!);
                    return;
                }
            }
        }

        private static bool IsUsableDnsResponse(byte[]? response)
        {
            if (response == null || response.Length < 12) return false;

            bool isResponse = (response[2] & 0x80) != 0;
            int responseCode = response[3] & 0x0F;

            // NOERROR and NXDOMAIN are final DNS answers. Other response codes
            // (for example SERVFAIL or REFUSED) allow trying another resolver.
            return isResponse && (responseCode == 0 || responseCode == 3);
        }

        private async Task<byte[]?> QueryDnsDirectAsync(byte[] dnsQuery, string dnsServer, CancellationToken token)
        {
            try
            {
                using var udpForwarder = new UdpClient();
                var serverAddress = await DnsOverHttpsClient.ResolveHostAsync(dnsServer, token);
                if (serverAddress == null) return null;
                udpForwarder.Connect(serverAddress, 53);
                await udpForwarder.SendAsync(dnsQuery, token);
                var result = await udpForwarder.ReceiveAsync(token);
                return result.Buffer;
            }
            catch { return null; }
        }

        private IEnumerable<string> GetConfiguredDnsHosts()
        {
            yield return _remoteDnsHost;

            if (!string.Equals(_fallbackDnsHost, _remoteDnsHost, StringComparison.OrdinalIgnoreCase))
            {
                yield return _fallbackDnsHost;
            }
        }

        private static string NormalizeDnsHost(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var normalized = value.Trim();
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                return uri.Host;
            }
            return normalized.TrimEnd('.');
        }

        public static string BuildDnsConfigSignature(AppConfig config)
        {
            return string.Join("|",
                NormalizeDnsHost(config.DnsHost, "8.8.8.8"),
                NormalizeDnsHost(config.DnsFallbackHost, "1.1.1.1"),
                config.PreferPrimaryDns,
                config.EnableDoh,
                config.AutoDetectDohEndpoint,
                config.DohEndpoint,
                config.EnableDohFallback,
                config.AutoDetectDohFallbackEndpoint,
                config.DohFallbackEndpoint);
        }

        private void ApplySystemDnsIfNeeded(bool force)
        {
            var signature = BuildDnsConfigSignature(_config);
            if (!force && string.Equals(_lastAppliedDnsConfigSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            SystemProxyHelper.SetSystemDns(true);
            _lastAppliedDnsConfigSignature = signature;
        }

        private (RuleAction Action, ProxyItem? Proxy) ResolveDnsAction(string host)
        {
            if (_currentMode == RuleMode.BlackList)
            {
                var mainProxy = GetMainProxy();

                if (_fastBlackIndex.MatchDns(host, DateTime.Now, out var matchedRule))
                {
                    if (matchedRule!.Action == RuleAction.Block) return (RuleAction.Block, null);
                    if (matchedRule.Action == RuleAction.Direct) return (RuleAction.Direct, null);
                    if (matchedRule.ProxyId != null)
                    {
                        var p = _localProxies.FirstOrDefault(x => x.Id.Equals(matchedRule.ProxyId, StringComparison.OrdinalIgnoreCase));
                        if (p != null && p.IsEnabled) return (RuleAction.Proxy, p);
                        return (RuleAction.Direct, null);
                    }
                    if (mainProxy != null) return (RuleAction.Proxy, mainProxy);
                }

                // System DNS cannot identify the originating application. Keep the
                // default resolver path direct; only explicit global DNS rules may proxy it.
                return (RuleAction.Direct, null);
            }
            else // White List
            {
                if (_fastWhiteIndex.MatchDns(host, DateTime.Now, out var matchedRule))
                {
                    if (matchedRule!.Action == RuleAction.Block) return (RuleAction.Block, null);
                    if (matchedRule.Action == RuleAction.Direct) return (RuleAction.Direct, null);
                    if (matchedRule.ProxyId != null)
                    {
                        var p = _localProxies.FirstOrDefault(x => x.Id.Equals(matchedRule.ProxyId, StringComparison.OrdinalIgnoreCase));
                        if (p != null && p.IsEnabled) return (RuleAction.Proxy, p);

                        var mainProxyFallback = GetMainProxy();
                        if (mainProxyFallback != null) return (RuleAction.Proxy, mainProxyFallback);
                    }

                    var mainProxy = GetMainProxy();
                    if (mainProxy != null) return (RuleAction.Proxy, mainProxy);
                }

                return (RuleAction.Direct, null);
            }
        }

        private ProxyItem? GetMainProxy()
        {
            return _localProxies.FirstOrDefault(p => p.IsEnabled &&
                       string.Equals(p.Id, _blackListProxyId, StringComparison.OrdinalIgnoreCase))
                   ?? _localProxies.FirstOrDefault(p => p.IsEnabled);
        }



        public RuleAction ResolveDnsActionForHost(string host)
        {
            return ResolveDnsAction(host).Action;
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

        private static bool TryParseQuestion(byte[] data, out string domain, out ushort qtype, out int questionEndOffset)
        {
            domain = string.Empty;
            qtype = 0;
            questionEndOffset = 0;

            if (data == null || data.Length < 12) return false;

            try
            {
                int offset = 12;
                domain = ParseName(data, ref offset);
                if (string.IsNullOrEmpty(domain) || offset + 4 > data.Length)
                {
                    return false;
                }

                qtype = (ushort)((data[offset] << 8) | data[offset + 1]);
                questionEndOffset = offset + 4;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] BuildEmptyDnsResponse(byte[] query, int questionEndOffset, int rcode = 0)
        {
            int length = Math.Min(questionEndOffset, query.Length);
            byte[] resp = new byte[length];
            Buffer.BlockCopy(query, 0, resp, 0, length);

            byte rd = (byte)(query[2] & 0x01); // Preserve Recursion Desired bit
            resp[2] = (byte)(0x80 | rd);        // QR=1 (Response), Opcode=0, RD preserved
            resp[3] = (byte)(0x80 | (rcode & 0x0F)); // RA=1 (Recursion Available), Z=0, RCODE

            // QDCOUNT = 1, ANCOUNT = 0, NSCOUNT = 0, ARCOUNT = 0
            resp[4] = 0x00; resp[5] = 0x01;
            resp[6] = 0x00; resp[7] = 0x00;
            resp[8] = 0x00; resp[9] = 0x00;
            resp[10] = 0x00; resp[11] = 0x00;

            return resp;
        }

        private static uint ExtractDnsTtl(byte[] response)
        {
            try
            {
                if (response.Length < 12) return 60;
                int qdCount = (response[4] << 8) | response[5];
                int anCount = (response[6] << 8) | response[7];
                if (anCount <= 0) return 60;

                int offset = 12;
                for (int i = 0; i < qdCount; i++)
                {
                    ParseName(response, ref offset);
                    offset += 4; // QTYPE + QCLASS
                    if (offset >= response.Length) return 60;
                }

                // First Answer RR: NAME
                ParseName(response, ref offset);
                // TYPE (2), CLASS (2), TTL (4)
                if (offset + 8 <= response.Length)
                {
                    uint ttl = ((uint)response[offset + 4] << 24) |
                               ((uint)response[offset + 5] << 16) |
                               ((uint)response[offset + 6] << 8) |
                               ((uint)response[offset + 7]);
                    return Math.Clamp(ttl, 10u, 3600u);
                }
            }
            catch { }
            return 60;
        }

        private void CacheDnsResponse(string cacheKey, byte[]? response)
        {
            if (response == null || !IsUsableDnsResponse(response)) return;

            int anCount = (response[6] << 8) | response[7];
            int rcode = response[3] & 0x0F;
            if (anCount <= 0 || rcode != 0) return;

            uint ttl = ExtractDnsTtl(response);
            _dnsCache[cacheKey] = new DnsCacheEntry(response, DateTime.UtcNow.AddSeconds(ttl));
        }

        private bool TryServeFromDnsCache(string cacheKey, byte[] dnsQuery, IPEndPoint clientEndpoint, string domain)
        {
            if (_dnsCache.TryGetValue(cacheKey, out var entry))
            {
                if (DateTime.UtcNow < entry.ExpiresUtc)
                {
                    byte[] resp = (byte[])entry.ResponseData.Clone();
                    resp[0] = dnsQuery[0];
                    resp[1] = dnsQuery[1];
                    if (_udpListener != null)
                    {
                        try
                        {
                            _ = _udpListener.SendAsync(resp, resp.Length, clientEndpoint);
                        }
                        catch { }
                    }

                    _trafficMonitor.CreateConnectionItem("DNS System", null, domain, "Cache", "Memory (TTL)", null, "#33CC99");
                    OnConnectionLog?.Invoke(new ConnectionLog
                    {
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        ProcessName = "DNS System",
                        Host = domain,
                        Result = "Cache",
                        Color = "#33CC99",
                        Type = TrafficType.DNS
                    });
                    return true;
                }
                else
                {
                    _dnsCache.TryRemove(cacheKey, out _);
                }
            }
            return false;
        }

        private static string ParseName(byte[] data, ref int offset)
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

        private async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int length)
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
