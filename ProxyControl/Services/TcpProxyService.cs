using ProxyControl.Helpers;
using ProxyControl.Models;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ProxyControl.Services
{
    public class GeoIpResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("countryCode")]
        public string CountryCode { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; }
    }

    public class TcpProxyService
    {
        private TcpListener _listener;
        private bool _isRunning;

        private List<ProxyItem> _localProxies = new List<ProxyItem>();
        private List<TrafficRule> _localBlackList = new List<TrafficRule>();
        private List<TrafficRule> _localWhiteList = new List<TrafficRule>();

        private HashSet<string> _blackListHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _whiteListHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Optimization: Fast lookup for exact host matches
        private Dictionary<string, TrafficRule> _fastBlackListRules = new Dictionary<string, TrafficRule>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, TrafficRule> _fastWhiteListRules = new Dictionary<string, TrafficRule>(StringComparer.OrdinalIgnoreCase);

        private string? _blackListProxyId;
        private RuleMode _currentMode;
        private bool _isWebRtcBlockingEnabled = true;
        private bool _isTunMode = false;
        private readonly ProcessMonitorService _processMonitor;
        private readonly TrafficMonitorService _trafficMonitor;
        private const int LocalPort = 8000;
        private const int BufferSize = 81920; // 80KB for better throughput
        private const int MaxHeaderSize = 16 * 1024;

        private readonly SemaphoreSlim _connectionLimiter = new SemaphoreSlim(2000);

        private readonly ConcurrentDictionary<Guid, ClientContext> _activeClients = new ConcurrentDictionary<Guid, ClientContext>();
        private readonly HttpClient _geoHttpClient;
        private readonly AppLoggerService _logger;

        public event Action<ConnectionLog>? OnConnectionLog;

        private class ClientContext
        {
            public TcpClient Client { get; set; }
            public TcpClient Remote { get; set; }
            public CancellationTokenSource Cts { get; set; }
            public string AppName { get; set; }
            public string Host { get; set; }
            public RuleAction InitialAction { get; set; }
            public string? InitialProxyId { get; set; }
        }

        public TcpProxyService(TrafficMonitorService trafficMonitor)
        {
            _trafficMonitor = trafficMonitor;
            _processMonitor = new ProcessMonitorService();
            _logger = AppLoggerService.Instance;
            _geoHttpClient = new HttpClient();
            _geoHttpClient.DefaultRequestHeaders.Add("User-Agent", "ProxyControl/1.0");
            _geoHttpClient.Timeout = TimeSpan.FromSeconds(5);
        }

        public void UpdateConfig(AppConfig config, List<ProxyItem> proxies)
        {
            var newProxies = proxies.Select(p => new ProxyItem
            {
                Id = p.Id,
                IpAddress = p.IpAddress,
                Port = p.Port,
                Username = p.Username,
                Password = p.Password,
                IsEnabled = p.IsEnabled,
                CountryCode = p.CountryCode,
                UseTls = p.UseTls,
                UseSsl = p.UseSsl,
                Type = p.Type // Add Type copy
            }).ToList();

            var newBlackList = config.BlackListRules?.ToList() ?? new List<TrafficRule>();
            var newWhiteList = config.WhiteListRules?.ToList() ?? new List<TrafficRule>();

            var newBlackListHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newFastBlackListRules = new Dictionary<string, TrafficRule>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in newBlackList)
            {
                if (rule.IsEnabled)
                {
                    foreach (var host in rule.TargetHosts)
                    {
                        if (host != "*")
                        {
                            newBlackListHosts.Add(host);
                            if (!newFastBlackListRules.ContainsKey(host))
                            {
                                newFastBlackListRules[host] = rule;
                            }
                        }
                    }
                }
            }

            var newWhiteListHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newFastWhiteListRules = new Dictionary<string, TrafficRule>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in newWhiteList)
            {
                if (rule.IsEnabled)
                {
                    foreach (var host in rule.TargetHosts)
                    {
                        if (host != "*")
                        {
                            newWhiteListHosts.Add(host);
                            if (!newFastWhiteListRules.ContainsKey(host))
                            {
                                newFastWhiteListRules[host] = rule;
                            }
                        }
                    }
                }
            }

            // Atomic Updates
            _localProxies = newProxies;
            _localBlackList = newBlackList;
            _localWhiteList = newWhiteList;
            _blackListHosts = newBlackListHosts;
            _fastBlackListRules = newFastBlackListRules;
            _whiteListHosts = newWhiteListHosts;
            _fastWhiteListRules = newFastWhiteListRules;

            _blackListProxyId = config.BlackListSelectedProxyId;
            _currentMode = config.CurrentMode;
            _isWebRtcBlockingEnabled = config.IsWebRtcBlockingEnabled;
            _isTunMode = config.IsTunMode;

            try
            {
                DisconnectAllClients();
            }
            catch (Exception ex)
            {
                _logger.Warning("Proxy", $"UpdateConfig cleanup error: {ex.Message}");
            }
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
                catch (Exception ex) { _logger.Debug("Proxy", $"Client close error: {ex.Message}"); }
            }
            _activeClients.Clear();
        }

        private void ForceClose(TcpClient client)
        {
            if (client == null) return;
            try { client.Close(); client.Dispose(); } catch (Exception ex) { _logger.Debug("Proxy", $"ForceClose error: {ex.Message}"); }
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
                _logger.Info("Proxy", $"Proxy started on port {LocalPort}");

                // Only set System Proxy if NOT in TUN mode
                if (!_isTunMode)
                {
                    SystemProxyHelper.SetSystemProxy(true, "127.0.0.1", LocalPort);
                }

                Task.Run(() => AcceptClientsLoop());
                StartScheduleEnforcer();
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger.Error("Proxy", $"Failed to start proxy: {ex.Message}");
                throw;
            }
        }

        private void StartScheduleEnforcer()
        {
            Task.Run(async () =>
            {
                while (_isRunning)
                {
                    try
                    {
                        await Task.Delay(5000); // Check every 5 seconds


                        if (!_isTunMode)
                        {
                            SystemProxyHelper.EnforceSystemProxy("127.0.0.1", LocalPort);
                        }

                        foreach (var kvp in _activeClients)
                        {
                            var ctx = kvp.Value;
                            if (string.IsNullOrEmpty(ctx.AppName) || string.IsNullOrEmpty(ctx.Host)) continue;

                            // Re-evaluate rule based on current time
                            var decision = ResolveAction(ctx.AppName, ctx.Host);

                            bool shouldKill = false;

                            // Check for Block
                            if (decision.Action == RuleAction.Block) shouldKill = true;
                            // Check for Switch (Proxy <-> Direct)
                            else if (decision.Action != ctx.InitialAction) shouldKill = true;
                            // Check for Proxy Change (ProxyA <-> ProxyB)
                            else if (decision.Action == RuleAction.Proxy && decision.Proxy?.Id != ctx.InitialProxyId) shouldKill = true;

                            if (shouldKill)
                            {
                                _logger.Debug("Schedule", $"Terminating active connection {ctx.AppName} -> {ctx.Host} due to schedule/rule change.");
                                ctx.Cts?.Cancel();
                                ForceClose(ctx.Client);
                                ForceClose(ctx.Remote);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Schedule", $"Enforcer loop error: {ex.Message}");
                    }
                }
            });
        }

        public void Stop()
        {
            _isRunning = false;
            _processMonitor.Stop();
            DisconnectAllClients();
            try { _listener?.Stop(); } catch { }

            // Only restore System Proxy if NOT in TUN mode
            if (!_isTunMode)
            {
                SystemProxyHelper.RestoreSystemProxy();
            }

            _logger.Info("Proxy", "Proxy stopped");
        }

        public void EnforceSystemProxy()
        {
            if (!_isRunning) return;

            // Limit enforcement to non-TUN mode
            if (!_isTunMode)
            {
                SystemProxyHelper.EnforceSystemProxy("127.0.0.1", LocalPort);
            }
        }

        private async Task AcceptClientsLoop()
        {
            while (_isRunning)
            {
                try
                {
                    await _connectionLimiter.WaitAsync();

                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleClientAsync(client);
                        }
                        finally
                        {
                            _connectionLimiter.Release();
                        }
                    });
                }
                catch
                {
                    if (!_isRunning) break;
                    await Task.Delay(100);
                }
            }
        }

        private async Task<(byte[] Buffer, int Length, string HeaderStr)> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxHeaderSize);
            int totalBytesRead = 0;
            int headerEndIndex = -1;

            try
            {
                while (totalBytesRead < MaxHeaderSize)
                {
                    int bytesRead = await stream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead, token);
                    if (bytesRead == 0) break;

                    totalBytesRead += bytesRead;

                    // SOCKS5 Detection - return immediately if first byte is 0x05
                    if (totalBytesRead > 0 && buffer[0] == 0x05)
                    {
                        break;
                    }

                    int searchStart = Math.Max(0, totalBytesRead - bytesRead - 3);
                    headerEndIndex = FindHeaderEnd(buffer, searchStart, totalBytesRead);

                    if (headerEndIndex != -1)
                    {
                        break;
                    }
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            if (headerEndIndex != -1 || totalBytesRead > 0)
            {
                string headerStr = Encoding.ASCII.GetString(buffer, 0, totalBytesRead);
                byte[] exactBuffer = new byte[totalBytesRead];
                Buffer.BlockCopy(buffer, 0, exactBuffer, 0, totalBytesRead);
                ArrayPool<byte>.Shared.Return(buffer);
                return (exactBuffer, totalBytesRead, headerStr);
            }

            ArrayPool<byte>.Shared.Return(buffer);
            return (Array.Empty<byte>(), 0, "");
        }

        private int FindHeaderEnd(byte[] buffer, int start, int end)
        {
            for (int i = start; i <= end - 4; i++)
            {
                if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                {
                    return i + 4;
                }
            }
            return -1;
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            Guid connectionId = Guid.NewGuid();
            var ctx = new ClientContext { Client = client, Cts = new CancellationTokenSource(), AppName = "", Host = "" };
            _activeClients.TryAdd(connectionId, ctx);

            ConnectionHistoryItem? historyItem = null;

            try
            {
                client.NoDelay = true;
                NetworkStream clientStream = client.GetStream();

                int clientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;

                int pid = SystemProxyHelper.GetPidByPort(clientPort);

                // Removed retry loop for PID to decrease latency. 
                // SystemProxyHelper cache is smart enough, and if we miss it, we miss it.
                // Waiting 60ms+ per connection is too expensive.

                string processName = _processMonitor.GetProcessName(pid);

                string processPath = "";
                try
                {
                    if (pid > 0)
                    {
                        var proc = Process.GetProcessById(pid);
                        processPath = proc.MainModule?.FileName;
                    }
                }
                catch (Exception ex) { _logger.Debug("Proxy", $"Process lookup error for PID {pid}: {ex.Message}"); }

                ImageSource? icon = null;
                if (!string.IsNullOrEmpty(processPath))
                    icon = IconHelper.GetIconByPath(processPath);
                else
                    icon = IconHelper.GetIconByProcessName(processName);

                var headerData = await ReadHeaderAsync(clientStream, ctx.Cts.Token);
                byte[] buffer = headerData.Buffer;
                int bytesRead = headerData.Length;
                string headerStr = headerData.HeaderStr;

                if (bytesRead == 0) return;

                if (bytesRead > 0 && buffer[0] == 0x05)
                {
                    // SOCKS5 detected (from TUN/sing-box)
                    await HandleSocks5ServerAsync(clientStream, buffer, bytesRead, client, processName, ctx.Cts.Token);
                    return;
                }

                if (!TryGetTargetHost(headerStr, out string targetHost, out int targetPort, out bool isConnectMethod))
                {
                    return;
                }

                /* STUN BLOCKED
                // Block WebRTC STUN/TURN servers to prevent IP leaks
                if (_isWebRtcBlockingEnabled && StunServers.Contains(targetHost))
                {
                    _logger.Warning("WebRTC", $"Blocked connection to STUN/TURN server: {targetHost}");
                    return;
                }
                */

                var decision = ResolveAction(processName, targetHost);
                ctx.AppName = processName;
                ctx.Host = targetHost;
                ctx.InitialAction = decision.Action;
                ctx.InitialProxyId = decision.Proxy?.Id;

                string logResult = "Direct";
                string logColor = "#AAAAAA";

                if (decision.Action == RuleAction.Block)
                {
                    if (decision.BlockDir == BlockDirection.Both)
                    {
                        logResult = "BLOCKED";
                        logColor = "#FF5555";
                    }
                    else
                    {
                        logResult = $"Block {decision.BlockDir}";
                        logColor = "#FFAA55";
                    }
                }
                else if (decision.Proxy != null)
                {
                    logResult = $"Proxy: {decision.Proxy.IpAddress}";
                    logColor = "#55FF55";
                }

                string? flagUrl = null;
                if (decision.Proxy != null && !string.IsNullOrEmpty(decision.Proxy.CountryCode))
                {
                    flagUrl = $"https://flagcdn.com/w40/{decision.Proxy.CountryCode.ToLower()}.png";
                }

                string details = decision.Proxy != null ? $"{decision.Proxy.IpAddress}:{decision.Proxy.Port}" : "";
                historyItem = _trafficMonitor.CreateConnectionItem(processName, icon, targetHost, logResult, details, flagUrl, logColor);

                OnConnectionLog?.Invoke(new ConnectionLog
                {
                    ProcessName = processName,
                    Host = targetHost,
                    Result = logResult,
                    Color = logColor,
                    AppIcon = icon,
                    CountryFlagUrl = flagUrl,
                    Type = isConnectMethod ? TrafficType.HTTPS : TrafficType.TCP
                });

                // Enhanced logging
                _logger.Debug("Proxy", $"[{processName}] → {targetHost}:{targetPort} ({(isConnectMethod ? "HTTPS" : "HTTP")}) = {logResult}");
                if (decision.Proxy != null)
                    _logger.Debug("Proxy", $"  ↳ Routing via {decision.Proxy.Type}: {decision.Proxy.IpAddress}:{decision.Proxy.Port} ({decision.Proxy.CountryCode ?? "?"})");

                if (decision.Action == RuleAction.Block && decision.BlockDir == BlockDirection.Both) return;

                ProxyItem? targetProxy = decision.Proxy;
                var remoteServer = new TcpClient();
                remoteServer.NoDelay = true;
                ctx.Remote = remoteServer;

                if (targetProxy != null)
                {
                    if (targetProxy.Type == ProxyType.Socks5)
                    {
                        await remoteServer.ConnectAsync(targetProxy.IpAddress, targetProxy.Port, ctx.Cts.Token);
                        try
                        {
                            await Socks5Client.ConnectAsync(remoteServer, targetProxy, targetHost, targetPort, ctx.Cts.Token);
                        }
                        catch
                        {
                            return;
                        }

                        Stream remoteStream = remoteServer.GetStream();

                        if (isConnectMethod)
                        {
                            byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                            await clientStream.WriteAsync(ok, 0, ok.Length, ctx.Cts.Token);
                        }
                        else
                        {
                            if (decision.BlockDir != BlockDirection.Outbound)
                            {
                                await remoteStream.WriteAsync(buffer, 0, bytesRead, ctx.Cts.Token);
                                if (historyItem != null)
                                {
                                    historyItem.BytesUp += bytesRead;
                                    _trafficMonitor.AddLiveTraffic(processName, bytesRead, false);
                                }
                            }
                        }
                        await BridgeStreams(clientStream, remoteStream, processName, historyItem, decision.BlockDir, ctx.Cts.Token);
                    }
                    else // HTTP Proxy
                    {
                        // Existing HTTP Proxy Logic
                        await remoteServer.ConnectAsync(targetProxy.IpAddress, targetProxy.Port, ctx.Cts.Token);

                        Stream remoteStream = remoteServer.GetStream();

                        if (targetProxy.UseTls || targetProxy.UseSsl)
                        {
                            var protocols = SslProtocols.None;
                            if (targetProxy.UseTls)
                            {
                                protocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                            }
                            else if (targetProxy.UseSsl)
                            {
                                protocols = SslProtocols.Tls | SslProtocols.Tls11;
                            }

                            // Fix 1: Always return true to ignore certificate errors for proxies
                            var sslStream = new SslStream(remoteStream, false, (s, c, ch, e) => true);

                            try
                            {
                                await sslStream.AuthenticateAsClientAsync(targetProxy.IpAddress, null, protocols, false);
                                remoteStream = sslStream;
                            }
                            catch
                            {
                                return;
                            }
                        }

                        string auth = "";
                        if (!string.IsNullOrEmpty(targetProxy.Username))
                        {
                            string creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{targetProxy.Username}:{targetProxy.Password}"));
                            auth = $"Proxy-Authorization: Basic {creds}\r\n";
                        }

                        string connectReq = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n{auth}\r\n";
                        byte[] reqBytes = Encoding.ASCII.GetBytes(connectReq);

                        await remoteStream.WriteAsync(reqBytes, 0, reqBytes.Length, ctx.Cts.Token);
                        await remoteStream.FlushAsync(); // Ensure request is sent

                        byte[] proxyRespBuffer = ArrayPool<byte>.Shared.Rent(4096);
                        try
                        {
                            int respLen = await remoteStream.ReadAsync(proxyRespBuffer, 0, proxyRespBuffer.Length, ctx.Cts.Token);
                            string proxyResp = Encoding.ASCII.GetString(proxyRespBuffer, 0, respLen);

                            if (!proxyResp.Contains("200")) return;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(proxyRespBuffer);
                        }

                        if (isConnectMethod)
                        {
                            byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                            await clientStream.WriteAsync(ok, 0, ok.Length, ctx.Cts.Token);
                        }
                        else
                        {
                            if (decision.BlockDir != BlockDirection.Outbound)
                            {
                                await remoteStream.WriteAsync(buffer, 0, bytesRead, ctx.Cts.Token);
                                if (historyItem != null)
                                {
                                    historyItem.BytesUp += bytesRead;
                                    _trafficMonitor.AddLiveTraffic(processName, bytesRead, false);
                                }
                            }
                        }
                        await BridgeStreams(clientStream, remoteStream, processName, historyItem, decision.BlockDir, ctx.Cts.Token);
                    }
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
                        if (decision.BlockDir != BlockDirection.Outbound)
                        {
                            await remoteStream.WriteAsync(buffer, 0, bytesRead, ctx.Cts.Token);
                            if (historyItem != null)
                            {
                                historyItem.BytesUp += bytesRead;
                                _trafficMonitor.AddLiveTraffic(processName, bytesRead, false);
                            }
                        }
                    }
                    await BridgeStreams(clientStream, remoteStream, processName, historyItem, decision.BlockDir, ctx.Cts.Token);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Proxy", $"HandleClient error: {ex.Message}");
            }
            finally
            {
                if (historyItem != null) _trafficMonitor.CompleteConnection(historyItem);
                _activeClients.TryRemove(connectionId, out _);
                ForceClose(client);
                ForceClose(ctx.Remote);
                ctx.Cts?.Dispose();
            }
        }

        private async Task BridgeStreams(NetworkStream clientStream, Stream remoteStream, string processName, ConnectionHistoryItem? historyItem, BlockDirection blockDir, CancellationToken token)
        {
            var uploadTask = (blockDir == BlockDirection.Outbound)
                ? DiscardAsync(clientStream, token)
                : CopyAndTrackAsync(clientStream, remoteStream, processName, isDownload: false, historyItem, token);

            var downloadTask = (blockDir == BlockDirection.Inbound)
                ? DiscardAsync(remoteStream, token)
                : CopyAndTrackAsync(remoteStream, clientStream, processName, isDownload: true, historyItem, token);

            await Task.WhenAny(uploadTask, downloadTask);
        }

        private async Task DiscardAsync(Stream source, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                while (await source.ReadAsync(buffer, 0, buffer.Length, token) > 0) { }
            }
            catch { }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private async Task CopyAndTrackAsync(Stream source, Stream destination, string processName, bool isDownload, ConnectionHistoryItem? historyItem, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long bytesAccumulated = 0;
            const long BatchSize = 65536; // 64KB batch

            try
            {
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, 0, BufferSize, token)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, bytesRead, token);

                    bytesAccumulated += bytesRead;

                    if (bytesAccumulated >= BatchSize)
                    {
                        _trafficMonitor.AddLiveTraffic(processName, bytesAccumulated, isDownload);
                        if (historyItem != null)
                        {
                            if (isDownload) historyItem.BytesDown += bytesAccumulated;
                            else historyItem.BytesUp += bytesAccumulated;
                        }
                        bytesAccumulated = 0;
                    }
                }

                // Flush remaining
                if (bytesAccumulated > 0)
                {
                    _trafficMonitor.AddLiveTraffic(processName, bytesAccumulated, isDownload);
                    if (historyItem != null)
                    {
                        if (isDownload) historyItem.BytesDown += bytesAccumulated;
                        else historyItem.BytesUp += bytesAccumulated;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                // Normal termination
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        private bool TryGetTargetHost(string header, out string host, out int port, out bool isConnect)
        {
            host = "";
            port = 80;
            isConnect = false;
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
                        if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                        {
                            host = uri.Host;
                            port = uri.Port;
                        }
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
            catch
            {
                return false;
            }
        }

        private (RuleAction Action, ProxyItem? Proxy, BlockDirection BlockDir) ResolveAction(string app, string host)
        {
            // WebRTC Protection: Spoof/Block STUN/TURN servers to prevent IP leaks
            if (_isWebRtcBlockingEnabled && IsStunServer(host))
            {
                var webRtcProxy = _localProxies.FirstOrDefault(p => p.Id != null && p.Id.Equals(_blackListProxyId, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);

                if (webRtcProxy != null)
                {
                    // Spoof: Route STUN/TURN through Proxy
                    // _logger.Debug("WebRTC", $"Spoofing STUN/TURN server: {host} via {webRtcProxy.IpAddress}");
                    return (RuleAction.Proxy, webRtcProxy, BlockDirection.Both);
                }

                _logger.Warning("WebRTC", $"Blocked STUN/TURN server: {host} (No Active Proxy)");
                return (RuleAction.Block, null, BlockDirection.Both);
            }

            var mainProxy = _localProxies.FirstOrDefault(p => p.Id.Equals(_blackListProxyId, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);

            if (_currentMode == RuleMode.BlackList)
            {
                // Fix for Scenario A (sing-box routing):
                // If the incoming connection is from sing-box (TUN), it means sing-box already decided to proxy this traffic.
                // We should trust it and not re-evaluate rules that might block it (e.g. if we can't identify the original process).
                if (app.Contains("sing-box", StringComparison.OrdinalIgnoreCase) || app.Contains("System/TUN", StringComparison.OrdinalIgnoreCase))
                {
                    if (mainProxy != null) return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
                }

                // Trusted 1: Exact Host Match (O(1))
                if (_fastBlackListRules.TryGetValue(host, out var fastRule))
                {
                    if (IsInSchedule(fastRule))
                    {
                        if (IsRuleMatch(fastRule, app, host))
                            return GetRuleDecision(fastRule, mainProxy);
                    }
                }

                // Fallback: Wildcard scanning (O(N))
                foreach (var rule in _localBlackList)
                {
                    if (!rule.IsEnabled) continue;

                    bool inSchedule = IsInSchedule(rule);
                    bool isMatch = IsRuleMatch(rule, app, host);

                    if (isMatch && !inSchedule)
                    {
                        _logger.Debug("Schedule", $"Rule for {app}/{host} ignored due to schedule. Now: {DateTime.Now:HH:mm}, Start: {rule.ScheduleStart}, End: {rule.ScheduleEnd}");
                    }

                    if (!inSchedule) continue; // Check schedule

                    if (isMatch)
                    {
                        return GetRuleDecision(rule, mainProxy);
                    }
                }

                if (mainProxy != null) return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
                return (RuleAction.Direct, null, BlockDirection.Both);
            }
            else
            {
                // Fix for Scenario A (sing-box routing):
                // In Whitelist mode, if sing-box routed traffic here, it matched a whitelist rule in sing-box.
                // We MUST proxy it upstream. If we default to Direct here (because we see "sing-box" instead of "Chrome"),
                // we bypass the upstream proxy, breaking the user's intent.
                if (app.Contains("sing-box", StringComparison.OrdinalIgnoreCase) || app.Contains("System/TUN", StringComparison.OrdinalIgnoreCase))
                {
                    if (mainProxy != null) return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
                }

                if (_fastWhiteListRules.TryGetValue(host, out var fastRule))
                {
                    if (IsInSchedule(fastRule))
                    {
                        if (IsRuleMatch(fastRule, app, host))
                            return GetRuleDecision(fastRule, mainProxy);
                    }
                }

                foreach (var rule in _localWhiteList)
                {
                    if (!rule.IsEnabled) continue;

                    bool inSchedule = IsInSchedule(rule);

                    if (!inSchedule) continue; // Check schedule

                    if (IsRuleMatch(rule, app, host))
                    {
                        return GetRuleDecision(rule, mainProxy);
                    }
                }
                return (RuleAction.Direct, null, BlockDirection.Both);
            }
        }

        private bool IsInSchedule(TrafficRule rule)
        {
            if (!rule.IsScheduleEnabled) return true;

            // Case 1: No Schedule defined -> Always Active
            // We treat null arrays and empty arrays as "Always Active" for days
            // We treat null/empty strings/null TimeSpans as "Always Active" for time
            bool hasDays = rule.ScheduleDays != null && rule.ScheduleDays.Length > 0;
            bool hasTime = rule.ScheduleStart != null && rule.ScheduleEnd != null;

            if (!hasDays && !hasTime) return true;

            var now = DateTime.Now;

            // Check Days
            if (hasDays && rule.ScheduleDays != null)
            {
                if (!rule.ScheduleDays.Contains(now.DayOfWeek))
                    return false;
            }

            // Check Time
            if (hasTime)
            {
                var time = now.TimeOfDay;
                var start = rule.ScheduleStart.Value;
                var end = rule.ScheduleEnd.Value;

                // Handle case where Start == End (e.g. 00:00 to 00:00 usually means all day, but let's assume valid range is required if set)
                // if (start == end) return true; // Removed, explicit check

                if (start <= end)
                {
                    if (time < start || time > end) return false;
                }
                else
                {
                    if (time < start && time > end) return false;
                }
            }

            return true;
        }

        private (RuleAction, ProxyItem?, BlockDirection) GetRuleDecision(TrafficRule rule, ProxyItem? mainProxy)
        {
            if (rule.Action == RuleAction.Block) return (RuleAction.Block, null, rule.BlockDirection);

            if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null, BlockDirection.Both);

            if (rule.ProxyId != null)
            {
                var p = _localProxies.FirstOrDefault(x => x.Id.Equals(rule.ProxyId, StringComparison.OrdinalIgnoreCase));
                if (p != null && p.IsEnabled)
                    return (RuleAction.Proxy, p, BlockDirection.Both);

                // Fallback if specific proxy is missing or disabled
                if (_currentMode == RuleMode.WhiteList && mainProxy != null)
                    return (RuleAction.Proxy, mainProxy, BlockDirection.Both);

                return (RuleAction.Direct, null, BlockDirection.Both);
            }
            return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
        }

        private bool IsWebRtcServer(string host)
        {
            /*
            return StunServers.Contains(host);
            */
            return false;
        }
        // Regex compiled for performance if needed, but simple string checks are fast enough if ordered correctly.
        private bool IsStunServer(string host)
        {
            // Quick check: most hosts are NOT stun
            if (host.Length < 4) return false;

            // Check for common STUN/TURN patterns
            // Use IndexOf with StringComparison.OrdinalIgnoreCase for better performance than ToLowerInvariant() + allocations
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

        private bool IsRuleMatch(TrafficRule rule, string app, string host)
        {
            if (rule.TargetApps.Count > 0)
            {
                if (app.Contains("sing-box", StringComparison.OrdinalIgnoreCase) || app.Contains("System/TUN", StringComparison.OrdinalIgnoreCase))
                {
                    // In TUN mode, we lose process identity. 
                    // Only match if rule explicitly allows Global (*) or System/TUN
                    bool allowsGlobal = rule.TargetApps.Contains("*");
                    bool allowsTun = rule.TargetApps.Any(a => a.Contains("TUN", StringComparison.OrdinalIgnoreCase));

                    // Fix for Scenario A: If we are here, it means we are checking a specific rule.
                    // If the rule is for a specific app (e.g. "Chrome"), sing-box won't match "Chrome".
                    // So we should return false here unless it's a global rule.

                    if (!allowsGlobal && !allowsTun) return false;
                }
                else
                {
                    bool match = false;
                    for (int i = 0; i < rule.TargetApps.Count; i++)
                    {
                        string target = rule.TargetApps[i];
                        if (target == "*")
                        {
                            match = true;
                            break;
                        }
                        if (app.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            match = true;
                            break;
                        }
                    }
                    if (!match) return false;
                }
            }

            if (rule.TargetHosts.Count > 0)
            {
                for (int i = 0; i < rule.TargetHosts.Count; i++)
                {
                    if (rule.TargetHosts[i] == "*") return true;
                    if (host.Contains(rule.TargetHosts[i], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            return true;
        }

        public async Task<(bool IsSuccess, string CountryCode, long Ping, double Speed, string SslError)> CheckProxy(ProxyItem proxy)
        {
            if (string.IsNullOrEmpty(proxy.IpAddress) || proxy.Port == 0) return (false, "", 0, 0, "Invalid IP/Port");

            bool connectionSuccess = false;
            long ping = 0;
            double speedMbps = 0;
            string sslError = "None";

            if (proxy.Type == ProxyType.Socks5)
            {
                // SOCKS5 Check logic
                try
                {
                    using (var client = new TcpClient())
                    {
                        var sw = Stopwatch.StartNew();
                        await Socks5Client.ConnectAsync(client, proxy, "www.google.com", 80, CancellationToken.None);

                        // Simple HTTP check over SOCKS5
                        var stream = client.GetStream();
                        byte[] req = Encoding.ASCII.GetBytes("GET /generate_204 HTTP/1.1\r\nHost: www.google.com\r\n\r\n");
                        await stream.WriteAsync(req, 0, req.Length);

                        byte[] respBuf = new byte[1024];
                        int r = await stream.ReadAsync(respBuf, 0, respBuf.Length);
                        string resp = Encoding.ASCII.GetString(respBuf, 0, r);

                        sw.Stop();
                        ping = sw.ElapsedMilliseconds;
                        connectionSuccess = resp.Contains("204");

                        // Speed Test (approx)
                        // For speed test we would need a larger download, simplified here or ignored
                        // Just setting same ping for now
                    }
                }
                catch (Exception ex)
                {
                    connectionSuccess = false;
                    sslError = ex.Message;
                }
            }
            else
            {
                // HTTP/HTTPS Logic
                // ... (Original Code) ...
                string scheme = "http";
                if (proxy.UseTls || proxy.UseSsl) scheme = "https";

                var proxyUri = new WebProxy($"{scheme}://{proxy.IpAddress}:{proxy.Port}");

                var handler = new HttpClientHandler
                {
                    Proxy = proxyUri,
                    UseProxy = true
                };

                if (proxy.UseTls || proxy.UseSsl)
                {
                    handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                    {
                        sslError = errors.ToString();
                        return true;
                    };
                }

                if (!string.IsNullOrEmpty(proxy.Username))
                {
                    handler.Proxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
                }

                try
                {
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);
                        var sw = Stopwatch.StartNew();
                        var response = await client.GetAsync("https://www.google.com/generate_204");
                        sw.Stop();
                        ping = sw.ElapsedMilliseconds;
                        connectionSuccess = response.IsSuccessStatusCode;
                    }
                }
                catch (Exception ex)
                {
                    connectionSuccess = false;
                    if (sslError == "None") sslError = ex.Message;
                }

                // Speed test for HTTP...
                if (connectionSuccess)
                {
                    try
                    {
                        var speedHandler = new HttpClientHandler
                        {
                            Proxy = proxyUri,
                            UseProxy = true
                        };
                        if (proxy.UseTls || proxy.UseSsl)
                        {
                            speedHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                        }
                        if (!string.IsNullOrEmpty(proxy.Username)) speedHandler.Proxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

                        using (var speedClient = new HttpClient(speedHandler))
                        {
                            speedClient.Timeout = TimeSpan.FromSeconds(15);
                            string testUrl = "https://code.jquery.com/jquery-3.6.0.min.js";

                            var sw = Stopwatch.StartNew();
                            var data = await speedClient.GetByteArrayAsync(testUrl);
                            sw.Stop();

                            double seconds = sw.Elapsed.TotalSeconds;
                            double bits = data.Length * 8;
                            double mbps = (bits / 1000000) / seconds;
                            speedMbps = Math.Round(mbps, 2);
                        }
                    }
                    catch
                    {
                        speedMbps = 0;
                    }
                }
            }

            string country = "";
            try
            {
                var geoResponse = await _geoHttpClient.GetAsync($"http://ip-api.com/json/{proxy.IpAddress}");
                if (geoResponse.IsSuccessStatusCode)
                {
                    var json = await geoResponse.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<GeoIpResult>(json);
                    if (result != null && result.Status == "success")
                    {
                        country = result.CountryCode;
                    }
                }
            }
            catch { }

            return (connectionSuccess, country, ping, speedMbps, sslError);
        }

        // --- SOCKS5 Server Logic ---

        private async Task HandleSocks5ServerAsync(NetworkStream stream, byte[] initialBuffer, int initialLength, TcpClient client, string processName, CancellationToken token)
        {
            // 1. Handshake
            // Ensure we have at least the initial 2 bytes (VER, NMETHODS)
            // initialBuffer contains what we read so far.
            // We need to consume the entire handshake message: VER | NMETHODS | METHODS

            // int offset = 0; // Removed unused
            byte[] buffer = initialBuffer;
            int bytesRead = initialLength;

            // If we have less than 2 bytes, we can't determine length. Even the minimal handshake is 3 bytes (05 01 00).
            if (bytesRead < 2)
            {
                // Try reading more if needed, though ReadHeaderAsync usually grabs standard packet size.
                // For simplicity, if we somehow got 1 byte, we might be in trouble with the logic passing generic buffer.
                // But let's assume valid SOCKS5 starts with at least 05 and NMETHODS.
                // Realistically, ReadHeaderAsync reads >0 bytes.
                if (bytesRead < 2)
                {
                    byte[] extra = new byte[1024];
                    int r = await stream.ReadAsync(extra, 0, extra.Length, token);
                    if (r == 0) return;
                    // Merge? This is getting complex. 
                    // Let's assume the initial read got the start. 
                    // We just need to check if we read ENOUGH.
                }
            }

            if (buffer[0] != 0x05) return;

            int nMethods = buffer[1];
            int handshakeLen = 2 + nMethods;

            // If we haven't read the full handshake yet, we need to read the rest.
            // Note: ReadHeaderAsync buffer might be large enough, so data might be there.
            // But if bytesRead < handshakeLen, we have a partial read.
            // AND we must ensure we don't accidentally treat the *next* packet (Request) as part of handshake if we over-read? 
            // ReadHeaderAsync returns ONE read call's worth. `sing-box` unlikely sends Request before Handshake reply? 
            // Standard SOCKS5 is sync.

            if (bytesRead < handshakeLen)
            {
                // We need to read the rest of the methods
                int missing = handshakeLen - bytesRead;
                byte[] temp = new byte[missing];
                int totalRead = 0;
                while (totalRead < missing)
                {
                    int r = await stream.ReadAsync(temp, totalRead, missing - totalRead, token);
                    if (r == 0) return;
                    totalRead += r;
                }
                // We consumed the methods.
            }

            // Respond: No Auth (0x00)
            byte[] greetingResp = { 0x05, 0x00 };
            await stream.WriteAsync(greetingResp, 0, greetingResp.Length, token);

            // 2. Request
            // Prepare buffer for Request
            byte[] reqBuf = new byte[1024];
            int reqBytesConfigured = 0;

            // Read first chunk (at least 4 bytes for header min)
            while (reqBytesConfigured < 4)
            {
                int r = await stream.ReadAsync(reqBuf, reqBytesConfigured, reqBuf.Length - reqBytesConfigured, token);
                if (r == 0) return;
                reqBytesConfigured += r;
            }

            byte ver = reqBuf[0];
            byte cmd = reqBuf[1];
            byte atyp = reqBuf[3];

            if (ver != 0x05) return;

            string targetHost = "";
            int targetPort = 0;
            int headerOffset = 4;

            // Ensure we have full address
            if (atyp == 0x01) // IPv4 (4 bytes)
            {
                while (reqBytesConfigured < headerOffset + 4 + 2)
                {
                    int r = await stream.ReadAsync(reqBuf, reqBytesConfigured, reqBuf.Length - reqBytesConfigured, token);
                    if (r == 0) return;
                    reqBytesConfigured += r;
                }

                var ip = new IPAddress(reqBuf[headerOffset..(headerOffset + 4)]);
                targetHost = ip.ToString();
                headerOffset += 4;
            }
            else if (atyp == 0x03) // Domain
            {
                // We need the length byte
                while (reqBytesConfigured < headerOffset + 1)
                {
                    int r = await stream.ReadAsync(reqBuf, reqBytesConfigured, reqBuf.Length - reqBytesConfigured, token);
                    if (r == 0) return;
                    reqBytesConfigured += r;
                }

                int len = reqBuf[headerOffset];
                headerOffset++;

                while (reqBytesConfigured < headerOffset + len + 2)
                {
                    int r = await stream.ReadAsync(reqBuf, reqBytesConfigured, reqBuf.Length - reqBytesConfigured, token);
                    if (r == 0) return;
                    reqBytesConfigured += r;
                }

                targetHost = Encoding.ASCII.GetString(reqBuf, headerOffset, len);
                headerOffset += len;
            }
            else if (atyp == 0x04) // IPv6 (16 bytes)
            {
                while (reqBytesConfigured < headerOffset + 16 + 2)
                {
                    int r = await stream.ReadAsync(reqBuf, reqBytesConfigured, reqBuf.Length - reqBytesConfigured, token);
                    if (r == 0) return;
                    reqBytesConfigured += r;
                }

                var ip = new IPAddress(reqBuf[headerOffset..(headerOffset + 16)]);
                targetHost = ip.ToString();
                headerOffset += 16;
            }

            _logger.Debug("Socks5", $"Request: {targetHost}:{targetPort} (CMD: {cmd})");

            targetPort = (reqBuf[headerOffset] << 8) | reqBuf[headerOffset + 1];

            if (cmd == 0x01) // CONNECT
            {
                await HandleSocks5Connect(stream, targetHost, targetPort, processName, client, token);
            }
            else if (cmd == 0x03) // UDP ASSOCIATE
            {
                await HandleSocks5UdpAssociate(stream, processName, token);
            }
            else
            {
                byte[] rep = { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                await stream.WriteAsync(rep, 0, rep.Length, token);
            }
        }


        private async Task HandleSocks5Connect(NetworkStream stream, string targetHost, int targetPort, string processName, TcpClient client, CancellationToken token)
        {
            // For active TUN traffic, processName might be "sing-box" or unknown
            if (string.IsNullOrEmpty(processName) || processName == "Unknown" || processName.Contains("sing-box"))
            {
                // Try to resolve the real process by looking at the destination in the TCP table
                if (!string.IsNullOrEmpty(targetHost) && targetPort > 0)
                {
                    IPAddress? ip = null;
                    if (IPAddress.TryParse(targetHost, out var parsedIp))
                    {
                        ip = parsedIp;
                    }
                    else
                    {
                        // Try to resolve DNS to find the IP in the TCP table
                        // This is best-effort.
                        try
                        {
                            var entry = Dns.GetHostEntry(targetHost);
                            if (entry.AddressList.Length > 0) ip = entry.AddressList[0];
                        }
                        catch { }
                    }

                    if (ip != null)
                    {
                        int realPid = SystemProxyHelper.GetPidByDestAddress(ip, targetPort);
                        if (realPid > 0)
                        {
                            processName = _processMonitor.GetProcessName(realPid);
                            // Log success debug
                            System.Diagnostics.Debug.WriteLine($"TUN Resolved: {targetHost}:{targetPort} -> PID {realPid} ({processName})");
                        }
                        else
                        {
                            processName = "System/TUN";
                        }
                    }
                    else
                    {
                        processName = "System/TUN";
                    }
                }
                else
                {
                    processName = "System/TUN";
                }
            }

            var decision = ResolveAction(processName, targetHost);
            ConnectionHistoryItem? historyItem = null;

            string logResult = "SOCKS5 Direct";
            string logColor = "#AAAAAA";

            if (decision.Action == RuleAction.Block)
            {
                logResult = "BLOCKED"; logColor = "#FF5555";
            }
            else if (decision.Proxy != null)
            {
                logResult = $"Proxy: {decision.Proxy.IpAddress}"; logColor = "#55FF55";
            }

            string? flagUrl = null;
            if (decision.Proxy != null && !string.IsNullOrEmpty(decision.Proxy.CountryCode))
                flagUrl = $"https://flagcdn.com/w40/{decision.Proxy.CountryCode.ToLower()}.png";

            string details = decision.Proxy != null ? $"{decision.Proxy.IpAddress}:{decision.Proxy.Port}" : "";
            historyItem = _trafficMonitor.CreateConnectionItem(processName, null, targetHost, decision.Action == RuleAction.Block ? logResult : decision.Action.ToString(), details, flagUrl, logColor);

            if (decision.Action == RuleAction.Block && decision.BlockDir == BlockDirection.Both)
            {
                byte[] rep = { 0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }; // 0x02 = Not allowed
                await stream.WriteAsync(rep, 0, rep.Length, token);
                return;
            }

            // Establish Upstream Connection FIRST
            TcpClient remote = new TcpClient();
            Stream? remoteStream = null;

            try
            {
                if (decision.Proxy != null)
                {
                    if (decision.Proxy.Type == ProxyType.Socks5)
                    {
                        await remote.ConnectAsync(decision.Proxy.IpAddress, decision.Proxy.Port, token);
                        await Socks5Client.ConnectAsync(remote, decision.Proxy, targetHost, targetPort, token);
                        remoteStream = remote.GetStream();
                    }
                    else
                    {
                        // HTTP Proxy Upstream
                        await remote.ConnectAsync(decision.Proxy.IpAddress, decision.Proxy.Port, token);
                        remoteStream = remote.GetStream();

                        if (decision.Proxy.UseTls || decision.Proxy.UseSsl)
                        {
                            var ssl = new SslStream(remoteStream, false, (s, c, ch, e) => true);
                            await ssl.AuthenticateAsClientAsync(decision.Proxy.IpAddress);
                            remoteStream = ssl;
                        }

                        // Perform HTTP CONNECT Handshake
                        await EstablishHttpConnectTunnelAsync(remoteStream, targetHost, targetPort, decision.Proxy.Username, decision.Proxy.Password, token);



                    }
                }
                else
                {
                    // Direct Connection
                    await remote.ConnectAsync(targetHost, targetPort, token);
                    remoteStream = remote.GetStream();
                }

                // If we reached here, upstream is ready. Send Success to Client.
                byte[] successRep = { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                await stream.WriteAsync(successRep, 0, successRep.Length, token);

                if (remoteStream != null)
                {
                    await BridgeStreams(stream, remoteStream, processName, historyItem, decision.BlockDir, token);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Socks5", $"Upstream failed: {ex.Message}");
                // Try to send failure if stream is still open
                try
                {
                    byte[] failRep = { 0x05, 0x01, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                    await stream.WriteAsync(failRep, 0, failRep.Length, token);
                }
                catch { }

                remote?.Dispose();
            }
            finally
            {
                // remote is disposed in try/catch blocks or implicitly via BridgeStreams closing it
                // But we should ensure it's disposed if we exit early
            }
        }



        private (string Host, int Port, byte[] Payload) ParseUdpPacket(byte[] packet)
        {
            if (packet.Length < 10) return ("", 0, Array.Empty<byte>());
            int offset = 3;
            byte atyp = packet[offset++];
            string host = "";
            if (atyp == 0x01) { if (packet.Length < offset + 4 + 2) return ("", 0, Array.Empty<byte>()); host = new IPAddress(packet[offset..(offset + 4)]).ToString(); offset += 4; }
            else if (atyp == 0x03) { byte len = packet[offset++]; if (packet.Length < offset + len + 2) return ("", 0, Array.Empty<byte>()); host = Encoding.ASCII.GetString(packet, offset, len); offset += len; }
            else if (atyp == 0x04) { if (packet.Length < offset + 16 + 2) return ("", 0, Array.Empty<byte>()); host = new IPAddress(packet[offset..(offset + 16)]).ToString(); offset += 16; }
            else return ("", 0, Array.Empty<byte>());

            int port = (packet[offset] << 8) | packet[offset + 1];
            offset += 2;
            byte[] payload = new byte[packet.Length - offset];
            if (payload.Length > 0) Buffer.BlockCopy(packet, offset, payload, 0, payload.Length);
            return (host, port, payload);
        }

        private async Task ReceiveFromProxyLoop(UdpClient proxyUdp, UdpClient localUdp, IPEndPoint clientEndpoint,
            string processName, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await proxyUdp.ReceiveAsync(token);

                    // Forward response back to client (packet already has SOCKS5 header from proxy)
                    await localUdp.SendAsync(result.Buffer, result.Buffer.Length, clientEndpoint);

                    // Log download traffic (extract payload size)
                    var (_, _, payload) = ParseUdpPacket(result.Buffer);
                    _trafficMonitor.AddLiveTraffic(processName, payload.Length, true);
                }
            }
            catch { }
        }

        private async Task ReceiveDirectUdpLoop(UdpClient directClient, UdpClient localUdp, IPEndPoint clientEndpoint,
            string originalHost, int originalPort, string processName, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await directClient.ReceiveAsync(token);

                    // Wrap response in SOCKS5 UDP header for client
                    byte[] wrappedPacket = Socks5Client.PackUdp(result.Buffer, originalHost, originalPort);
                    await localUdp.SendAsync(wrappedPacket, wrappedPacket.Length, clientEndpoint);

                    _trafficMonitor.AddLiveTraffic(processName, result.Buffer.Length, true);
                }
            }
            catch { }
        }

        private async Task EstablishHttpConnectTunnelAsync(Stream stream, string targetHost, int targetPort, string? user, string? pass, CancellationToken token)
        {
            var sb = new StringBuilder();
            sb.Append($"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n");
            sb.Append($"Host: {targetHost}:{targetPort}\r\n");

            if (!string.IsNullOrEmpty(user))
            {
                string creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
                sb.Append($"Proxy-Authorization: Basic {creds}\r\n");
            }
            sb.Append("\r\n");

            byte[] req = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(req, 0, req.Length, token);
            await stream.FlushAsync(token);

            // Read Response Headers
            var lineBuffer = new StringBuilder();
            bool statusChecked = false;

            // Safety limit
            int totalBytesRead = 0;
            int maxHeaderBytes = 81920;

            byte[] buffer = new byte[1];
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, 1, token);
                if (bytesRead == 0) throw new IOException("Proxy closed connection unexpectedly during handshake.");

                char c = (char)buffer[0];
                totalBytesRead++;
                if (totalBytesRead > maxHeaderBytes) throw new IOException("Proxy response headers too large.");

                if (c == '\n')
                {
                    string line = lineBuffer.ToString().TrimEnd('\r');
                    lineBuffer.Clear();

                    if (!statusChecked)
                    {
                        if (!line.Contains(" 200 ")) // Check for "200" surrounded by spaces or " 200"
                        {
                            // Relaxed check: "HTTP/1.1 200 OK"
                            if (!line.Contains("200")) throw new IOException($"Proxy handshake failed: {line}");
                        }
                        statusChecked = true;
                    }

                    if (string.IsNullOrEmpty(line)) return; // End of headers
                }
                else
                {
                    lineBuffer.Append(c);
                }
            }
        }
        private async Task HandleSocks5UdpAssociate(NetworkStream stream, string processName, CancellationToken token)
        {
            using (var udpListener = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                var localEp = (IPEndPoint)udpListener.Client.LocalEndPoint;
                int relayPort = localEp.Port;
                string relayIp = "127.0.0.1";

                byte[] addressBytes = IPAddress.Parse(relayIp).GetAddressBytes();
                byte[] portBytes = new byte[2];
                portBytes[0] = (byte)((relayPort >> 8) & 0xFF);
                portBytes[1] = (byte)(relayPort & 0xFF);

                byte[] response = new byte[4 + addressBytes.Length + 2];
                response[0] = 0x05;
                response[1] = 0x00;
                response[2] = 0x00;
                response[3] = 0x01;
                Array.Copy(addressBytes, 0, response, 4, addressBytes.Length);
                Array.Copy(portBytes, 0, response, 8, 2);

                await stream.WriteAsync(response, 0, response.Length, token);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        byte[] buffer = new byte[1];
                        while (await stream.ReadAsync(buffer, 0, 1, token) > 0) { }
                    }
                    catch { }
                    finally
                    {
                        udpListener.Close();
                    }
                }, token);

                await RunUdpRelay(udpListener, processName, token);
            }
        }

        private async Task RunUdpRelay(UdpClient udpListener, string processName, CancellationToken token)
        {
            var sessions = new ConcurrentDictionary<IPEndPoint, UdpClient>();

            try
            {
                var tcs = new TaskCompletionSource<bool>();
                using (token.Register(() => tcs.TrySetResult(true)))
                {
                    while (!token.IsCancellationRequested)
                    {
                        var receiveTask = udpListener.ReceiveAsync();
                        var completedTask = await Task.WhenAny(receiveTask, tcs.Task);

                        if (completedTask == tcs.Task) break;

                        var result = receiveTask.Result;
                        byte[] buffer = result.Buffer;
                        IPEndPoint clientEp = result.RemoteEndPoint;

                        if (buffer.Length < 10) continue;
                        if (buffer[0] != 0x00 || buffer[1] != 0x00) continue;
                        if (buffer[2] != 0x00) continue;

                        byte atyp = buffer[3];
                        string targetHost = "";
                        int targetPort = 0;
                        int headerLen = 0;

                        int offset = 4;
                        if (atyp == 0x01)
                        {
                            var ip = new IPAddress(buffer[offset..(offset + 4)]);
                            targetHost = ip.ToString();
                            offset += 4;
                        }
                        else if (atyp == 0x03)
                        {
                            int len = buffer[offset];
                            offset++;
                            targetHost = Encoding.ASCII.GetString(buffer, offset, len);
                            offset += len;
                        }
                        else if (atyp == 0x04)
                        {
                            var ip = new IPAddress(buffer[offset..(offset + 16)]);
                            targetHost = ip.ToString();
                            offset += 16;
                        }

                        targetPort = (buffer[offset] << 8) | buffer[offset + 1];
                        offset += 2;
                        headerLen = offset;

                        byte[] payload = new byte[buffer.Length - headerLen];
                        Array.Copy(buffer, headerLen, payload, 0, payload.Length);

                        var decision = ResolveAction(processName, targetHost);

                        if (!sessions.ContainsKey(clientEp))
                        {
                            string logResult = "UDP Direct";
                            string logColor = "#AAAAAA";

                            if (decision.Action == RuleAction.Block) { logResult = "UDP BLOCKED"; logColor = "#FF5555"; }
                            else if (decision.Proxy != null) { logResult = $"UDP Proxy: {decision.Proxy.IpAddress}"; logColor = "#55FF55"; }

                            string details = decision.Proxy != null ? $"{decision.Proxy.IpAddress}:{decision.Proxy.Port}" : "Direct";

                            ImageSource? icon = null;
                            if (!string.IsNullOrEmpty(processName)) icon = IconHelper.GetIconByProcessName(processName);

                            _trafficMonitor.CreateConnectionItem(processName, icon, targetHost, logResult, details, null, logColor);

                            _logger.Debug("UDP", $"[{processName}] UDP {targetHost}:{targetPort} via {logResult}");
                        }

                        if (decision.Action == RuleAction.Block && decision.BlockDir == BlockDirection.Both) continue;

                        UdpClient? remoteClient;
                        if (!sessions.TryGetValue(clientEp, out remoteClient))
                        {
                            // Pass decision.Proxy to create logic
                            remoteClient = await CreateRemoteUdpClient(decision.Proxy, targetHost, targetPort);
                            if (remoteClient != null)
                            {
                                sessions.TryAdd(clientEp, remoteClient);
                                _ = HandleRemoteUdpReceive(remoteClient, udpListener, clientEp, decision.Proxy != null && decision.Proxy.Type == ProxyType.Socks5, token);
                            }
                        }

                        if (remoteClient != null)
                        {
                            if (decision.Proxy != null && decision.Proxy.Type == ProxyType.Socks5)
                            {
                                await remoteClient.SendAsync(buffer, buffer.Length);
                            }
                            else
                            {
                                await remoteClient.SendAsync(payload, payload.Length);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("UDP", $"Relay Error: {ex.Message}");
            }
            finally
            {
                foreach (var s in sessions.Values) s.Close();
                sessions.Clear();
            }
        }

        private async Task<UdpClient?> CreateRemoteUdpClient(ProxyItem? proxy, string targetHost, int targetPort)
        {
            try
            {
                if (proxy != null && proxy.Type == ProxyType.Socks5)
                {
                    var tcpCtrl = new TcpClient();
                    await tcpCtrl.ConnectAsync(proxy.IpAddress, proxy.Port);
                    var stream = tcpCtrl.GetStream();

                    // 1. Initial Handshake (Method Selection)
                    // We support No Auth (0x00) and User/Pass (0x02)
                    byte[] methods = { 0x00, 0x02 };
                    byte[] hello = new byte[2 + methods.Length];
                    hello[0] = 0x05;
                    hello[1] = (byte)methods.Length;
                    Buffer.BlockCopy(methods, 0, hello, 2, methods.Length);

                    await stream.WriteAsync(hello, 0, hello.Length);

                    byte[] serverChoice = new byte[2];
                    int read = await stream.ReadAsync(serverChoice, 0, 2);
                    if (read < 2 || serverChoice[0] != 0x05) return null; // Invalid SOCKS5

                    byte method = serverChoice[1];

                    if (method == 0x02) // Username/Password Auth Required
                    {
                        if (string.IsNullOrEmpty(proxy.Username)) return null; // Auth required but no creds

                        string user = proxy.Username ?? "";
                        string pass = proxy.Password ?? "";

                        byte[] userBytes = Encoding.UTF8.GetBytes(user);
                        byte[] passBytes = Encoding.UTF8.GetBytes(pass);

                        // Auth Request: VER(1) | ULEN(1) | USER | PLEN(1) | PASS
                        byte[] authReq = new byte[3 + userBytes.Length + passBytes.Length];
                        authReq[0] = 0x01;
                        authReq[1] = (byte)userBytes.Length;
                        Buffer.BlockCopy(userBytes, 0, authReq, 2, userBytes.Length);
                        authReq[2 + userBytes.Length] = (byte)passBytes.Length;
                        Buffer.BlockCopy(passBytes, 0, authReq, 3 + userBytes.Length, passBytes.Length);

                        await stream.WriteAsync(authReq, 0, authReq.Length);

                        // Auth Response: VER(1) | STATUS(1)
                        byte[] authResp = new byte[2];
                        read = await stream.ReadAsync(authResp, 0, 2);
                        if (read < 2 || authResp[1] != 0x00) return null; // Auth Failed
                    }
                    else if (method == 0xFF) // No Acceptable Methods
                    {
                        return null;
                    }

                    // 2. UDP ASSOCIATE Request
                    byte[] req = { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                    await stream.WriteAsync(req, 0, req.Length);

                    byte[] rep = new byte[1024];
                    read = await stream.ReadAsync(rep, 0, 10);
                    if (read < 10 || rep[1] != 0x00) return null; // UDP Associate Failed

                    int offset = 4;
                    string relayIp = "";
                    int relayPort = 0;
                    if (rep[3] == 0x01) { relayIp = new IPAddress(rep[4..8]).ToString(); offset += 4; }
                    else if (rep[3] == 0x03) { int len = rep[4]; relayIp = Encoding.ASCII.GetString(rep, 5, len); offset += 1 + len; }
                    else if (rep[3] == 0x04) { relayIp = new IPAddress(rep[4..20]).ToString(); offset += 16; }

                    relayPort = (rep[offset] << 8) | rep[offset + 1];

                    // If Relay IP is 0.0.0.0, use the Proxy IP
                    if (relayIp == "0.0.0.0") relayIp = proxy.IpAddress;

                    // Keep-Alive
                    _ = Task.Run(async () =>
                    {
                        try { while (true) { await Task.Delay(10000); await stream.WriteAsync(new byte[0], 0, 0); } } catch { }
                    });

                    var udp = new UdpClient();
                    udp.Connect(relayIp, relayPort);
                    return udp;
                }
                else
                {
                    // Direct
                    var udp = new UdpClient();
                    udp.Connect(targetHost, targetPort);
                    return udp;
                }
            }
            catch { return null; }
        }

        private async Task HandleRemoteUdpReceive(UdpClient remote, UdpClient localListener, IPEndPoint clientEp, bool isProxyWrapperUsed, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var res = await remote.ReceiveAsync();
                    byte[] data = res.Buffer;

                    if (isProxyWrapperUsed)
                    {
                        // Data from upstream proxy is ALREADY SOCKS5 wrapped.
                        // Forward as is to client.
                        await localListener.SendAsync(data, data.Length, clientEp);
                    }
                    else
                    {
                        // Data from Direct target is RAW.
                        // We must WRAP it in SOCKS5 header for the client.
                        // Header: RSV(2) FRAG(1) ATYP(1) ADDR(?) PORT(2) DATA
                        // We don't know the source easily here without state, so we cheat and say from 0.0.0.0:0 
                        // mostly irrelevant for client as it just wants payload usually? 
                        // Actually WebRTC might care about Source IP matching Target IP.
                        // We should ideally track original Target set.
                        // For now, construct a dummy header or try to use remote endpoint.

                        // Using Dummy 0.0.0.0:0 header for simplicity of relay
                        byte[] header = { 0x00, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                        byte[] packet = new byte[header.Length + data.Length];
                        Array.Copy(header, 0, packet, 0, header.Length);
                        Array.Copy(data, 0, packet, header.Length, data.Length);

                        await localListener.SendAsync(packet, packet.Length, clientEp);
                    }
                }
            }
            catch { }
        }
    }
}
