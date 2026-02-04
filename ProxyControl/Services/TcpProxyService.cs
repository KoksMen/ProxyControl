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

        private string? _blackListProxyId;
        private RuleMode _currentMode;
        private bool _isWebRtcBlockingEnabled = true;
        private readonly ProcessMonitorService _processMonitor;
        private readonly TrafficMonitorService _trafficMonitor;
        private const int LocalPort = 8000;
        private const int BufferSize = 32768; // 32KB for better high-speed performance
        private const int MaxHeaderSize = 16 * 1024;

        private readonly SemaphoreSlim _connectionLimiter = new SemaphoreSlim(2000);

        private readonly ConcurrentDictionary<Guid, ClientContext> _activeClients = new ConcurrentDictionary<Guid, ClientContext>();
        private readonly HttpClient _geoHttpClient;
        private readonly AppLoggerService _logger;

        // WebRTC STUN/TURN servers to block for preventing IP leaks
        /*
        private static readonly HashSet<string> StunServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "stun.l.google.com", "stun1.l.google.com", "stun2.l.google.com",
            "stun3.l.google.com", "stun4.l.google.com",
            "stun.services.mozilla.com", "stun.stunprotocol.org",
            "stun.cloudflare.com", "stun.nextcloud.com",
            "turn.cloudflare.com", "relay.webrtc.org"
        };
        */

        public event Action<ConnectionLog>? OnConnectionLog;

        private class ClientContext
        {
            public TcpClient Client { get; set; }
            public TcpClient Remote { get; set; }
            public CancellationTokenSource Cts { get; set; }
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
            _localProxies = proxies.Select(p => new ProxyItem
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

            _localBlackList = config.BlackListRules?.ToList() ?? new List<TrafficRule>();
            _localWhiteList = config.WhiteListRules?.ToList() ?? new List<TrafficRule>();

            _blackListHosts.Clear();
            foreach (var rule in _localBlackList)
            {
                if (rule.IsEnabled)
                {
                    foreach (var host in rule.TargetHosts)
                    {
                        if (host != "*") _blackListHosts.Add(host);
                    }
                }
            }

            _whiteListHosts.Clear();
            foreach (var rule in _localWhiteList)
            {
                if (rule.IsEnabled)
                {
                    foreach (var host in rule.TargetHosts)
                    {
                        if (host != "*") _whiteListHosts.Add(host);
                    }
                }
            }

            _blackListProxyId = config.BlackListSelectedProxyId.ToString();
            _currentMode = config.CurrentMode;
            _isWebRtcBlockingEnabled = config.IsWebRtcBlockingEnabled;

            try
            {
                DisconnectAllClients();
            }
            catch { }
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
            try { client.Close(); client.Dispose(); } catch { }
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
                _logger.Info("Proxy", $"Proxy started on port {LocalPort}");
                // _logger.Info("Proxy", $"WebRTC Protection: Blocking {StunServers.Count} STUN/TURN servers");
                Task.Run(() => AcceptClientsLoop());
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger.Error("Proxy", $"Failed to start proxy: {ex.Message}");
                throw;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _processMonitor.Stop();
            DisconnectAllClients();
            try { _listener?.Stop(); } catch { }
            SystemProxyHelper.RestoreSystemProxy();
            _logger.Info("Proxy", "Proxy stopped");
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
            var ctx = new ClientContext { Client = client, Cts = new CancellationTokenSource() };
            _activeClients.TryAdd(connectionId, ctx);

            ConnectionHistoryItem? historyItem = null;

            try
            {
                client.NoDelay = true;
                NetworkStream clientStream = client.GetStream();

                int clientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;

                int pid = SystemProxyHelper.GetPidByPort(clientPort);

                // Retry logic is now mostly handled by the Helper forcing refresh, but keep for safety
                if (pid == 0)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        await Task.Delay(20);
                        pid = SystemProxyHelper.GetPidByPort(clientPort);
                        if (pid > 0) break;
                    }
                }

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
                catch { }

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

                /* SOCKS5 DISABLED
                if (bytesRead > 0 && buffer[0] == 0x05)
                {
                    // SOCKS5 detected
                    await HandleSocks5ServerAsync(clientStream, buffer, bytesRead, client, processName, ctx.Cts.Token);
                    return;
                }
                */

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
                    /* SOCKS5 UPSTREAM DISABLED
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
                    */
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
            catch { }
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
            const long BatchSize = 65536; // 64KB batch for reduced overhead

            try
            {
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
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
            catch { }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
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
            // WebRTC Protection: Block STUN/TURN servers to prevent IP leaks (if enabled)
            if (_isWebRtcBlockingEnabled && IsStunServer(host))
            {
                _logger.Warning("WebRTC", $"Blocked STUN/TURN server: {host} (app: {app})");
                return (RuleAction.Block, null, BlockDirection.Both);
            }

            var mainProxy = _localProxies.FirstOrDefault(p => p.Id.Equals(_blackListProxyId, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);

            if (_currentMode == RuleMode.BlackList)
            {
                if (_blackListHosts.Contains(host))
                {
                    var rule = _localBlackList.FirstOrDefault(r => r.IsEnabled && IsRuleMatch(r, app, host));
                    if (rule != null) return GetRuleDecision(rule, mainProxy);
                }

                foreach (var rule in _localBlackList)
                {
                    if (!rule.IsEnabled) continue;

                    if (IsRuleMatch(rule, app, host))
                    {
                        return GetRuleDecision(rule, mainProxy);
                    }
                }

                if (mainProxy != null) return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
                return (RuleAction.Direct, null, BlockDirection.Both);
            }
            else
            {
                if (_whiteListHosts.Contains(host))
                {
                    var rule = _localWhiteList.FirstOrDefault(r => r.IsEnabled && IsRuleMatch(r, app, host));
                    if (rule != null) return GetRuleDecision(rule, mainProxy);
                }

                foreach (var rule in _localWhiteList)
                {
                    if (!rule.IsEnabled) continue;

                    if (IsRuleMatch(rule, app, host))
                    {
                        return GetRuleDecision(rule, mainProxy);
                    }
                }
                return (RuleAction.Direct, null, BlockDirection.Both);
            }
        }

        private (RuleAction, ProxyItem?, BlockDirection) GetRuleDecision(TrafficRule rule, ProxyItem? mainProxy)
        {
            if (rule.Action == RuleAction.Block) return (RuleAction.Block, null, rule.BlockDirection);
            if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null, BlockDirection.Both);
            if (rule.ProxyId != null)
            {
                var p = _localProxies.FirstOrDefault(x => x.Id.Equals(rule.ProxyId, StringComparison.OrdinalIgnoreCase));
                if (p != null && p.IsEnabled) return (RuleAction.Proxy, p, BlockDirection.Both);

                if (_currentMode == RuleMode.WhiteList && mainProxy != null) return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
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
        private bool IsStunServer(string host)
        {
            // if (StunServers.Contains(host)) return true; // This is the line that was commented out

            // Check for common STUN/TURN patterns
            var lower = host.ToLowerInvariant();
            if (lower.StartsWith("stun.") || lower.StartsWith("turn.") ||
                lower.Contains(".stun.") || lower.Contains(".turn.") ||
                lower.EndsWith(".stun") || lower.EndsWith(".turn"))
            {
                return true;
            }

            // Block common WebRTC relay patterns
            if (lower.Contains("webrtc") && (lower.Contains("relay") || lower.Contains("ice")))
            {
                return true;
            }

            return false;
        }

        private bool IsRuleMatch(TrafficRule rule, string app, string host)
        {
            if (rule.TargetApps.Count > 0)
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
            if (initialBuffer[0] != 0x05) return;

            // We accept NoAuth (0x00)
            byte[] greetingResp = { 0x05, 0x00 };
            await stream.WriteAsync(greetingResp, 0, greetingResp.Length, token);

            // 2. Request
            byte[] reqBuf = new byte[1024];
            int read = await stream.ReadAsync(reqBuf, 0, reqBuf.Length, token);
            if (read < 4) return;

            byte ver = reqBuf[0];
            byte cmd = reqBuf[1]; // 0x01=CONNECT, 0x03=UDP ASSOCIATE
            byte atyp = reqBuf[3];

            if (ver != 0x05) return;

            string targetHost = "";
            int targetPort = 0;

            int offset = 4;
            if (atyp == 0x01) // IPv4
            {
                if (read < offset + 4 + 2) return;
                var ip = new IPAddress(reqBuf[offset..(offset + 4)]);
                targetHost = ip.ToString();
                offset += 4;
            }
            else if (atyp == 0x03) // Domain
            {
                int len = reqBuf[offset];
                offset++;
                if (read < offset + len + 2) return;
                targetHost = Encoding.ASCII.GetString(reqBuf, offset, len);
                offset += len;
            }
            else if (atyp == 0x04) // IPv6
            {
                if (read < offset + 16 + 2) return;
                var ip = new IPAddress(reqBuf[offset..(offset + 16)]);
                targetHost = ip.ToString();
                offset += 16;
            }

            // Port
            targetPort = (reqBuf[offset] << 8) | reqBuf[offset + 1];

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
                // Command not supported
                byte[] rep = { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                await stream.WriteAsync(rep, 0, rep.Length, token);
            }
        }

        private async Task HandleSocks5Connect(NetworkStream stream, string targetHost, int targetPort, string processName, TcpClient client, CancellationToken token)
        {
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

            // Reply Success
            byte[] successRep = { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            try
            {
                await stream.WriteAsync(successRep, 0, successRep.Length, token);
            }
            catch { return; }

            using (var remote = new TcpClient())
            {
                try
                {
                    Stream? remoteStream = null;

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
                            await remote.ConnectAsync(decision.Proxy.IpAddress, decision.Proxy.Port, token);
                            remoteStream = remote.GetStream();

                            if (decision.Proxy.UseTls || decision.Proxy.UseSsl)
                            {
                                var ssl = new SslStream(remoteStream, false, (s, c, ch, e) => true);
                                await ssl.AuthenticateAsClientAsync(decision.Proxy.IpAddress);
                                remoteStream = ssl;
                            }

                            string auth = "";
                            if (!string.IsNullOrEmpty(decision.Proxy.Username))
                            {
                                string creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{decision.Proxy.Username}:{decision.Proxy.Password}"));
                                auth = $"Proxy-Authorization: Basic {creds}\r\n";
                            }

                            string connectReq = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n{auth}\r\n";
                            byte[] rb = Encoding.ASCII.GetBytes(connectReq);
                            await remoteStream.WriteAsync(rb, 0, rb.Length, token);
                            await remoteStream.FlushAsync();

                            byte[] rbo = new byte[1024];
                            await remoteStream.ReadAsync(rbo, 0, rbo.Length, token);
                        }
                    }
                    else
                    {
                        await remote.ConnectAsync(targetHost, targetPort, token);
                        remoteStream = remote.GetStream();
                    }

                    if (remoteStream != null)
                        await BridgeStreams(stream, remoteStream, processName, historyItem, decision.BlockDir, token);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"S5 Connect Error: {ex.Message}");
                }
            }
        }

        private async Task HandleSocks5UdpAssociate(NetworkStream stream, string processName, CancellationToken token)
        {
            TcpClient? proxyControlClient = null;
            UdpClient? proxyUdpClient = null;
            IPEndPoint? proxyUdpEndpoint = null;

            try
            {
                using (var localUdp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                {
                    int assignedPort = ((IPEndPoint)localUdp.Client.LocalEndPoint!).Port;

                    // Get local IP for the reply (use loopback for local clients)
                    var localIp = IPAddress.Loopback;
                    byte[] ipBytes = localIp.GetAddressBytes();

                    byte[] portBytes = BitConverter.GetBytes((ushort)assignedPort);
                    if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);

                    // Send UDP ASSOCIATE reply
                    byte[] rep = new byte[10];
                    rep[0] = 0x05; // VER
                    rep[1] = 0x00; // SUCCESS
                    rep[2] = 0x00; // RSV
                    rep[3] = 0x01; // ATYP = IPv4
                    Buffer.BlockCopy(ipBytes, 0, rep, 4, 4);
                    Buffer.BlockCopy(portBytes, 0, rep, 8, 2);

                    await stream.WriteAsync(rep, 0, rep.Length, token);

                    // Determine if we need to use a proxy for UDP
                    var decision = ResolveAction(processName, "*"); // Check global UDP rule
                    ProxyItem? udpProxy = decision.Proxy;

                    if (udpProxy != null && udpProxy.Type == ProxyType.Socks5)
                    {
                        try
                        {
                            var udpAssocResult = await Socks5Client.UdpAssociateAsync(udpProxy, token);
                            proxyControlClient = udpAssocResult.ControlClient;
                            proxyUdpEndpoint = udpAssocResult.UdpRelay;

                            // If proxy returned 0.0.0.0, use proxy's IP
                            if (proxyUdpEndpoint.Address.Equals(IPAddress.Any) || proxyUdpEndpoint.Address.Equals(IPAddress.IPv6Any))
                            {
                                proxyUdpEndpoint = new IPEndPoint(IPAddress.Parse(udpProxy.IpAddress), proxyUdpEndpoint.Port);
                            }

                            proxyUdpClient = new UdpClient();
                            proxyUdpClient.Connect(proxyUdpEndpoint);

                            _trafficMonitor.CreateConnectionItem(processName, null, "UDP Relay", "Proxied",
                                $"via {udpProxy.IpAddress}:{proxyUdpEndpoint.Port}", null, "#55FF55");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to establish UDP proxy: {ex.Message}");
                            // Fall back to direct
                            udpProxy = null;
                        }
                    }
                    else
                    {
                        _trafficMonitor.CreateConnectionItem(processName, null, "UDP Relay", "Direct",
                            $"Port:{assignedPort}", null, "#AAAAAA");
                    }

                    // Run the relay
                    var relayTask = RunUdpRelayFull(localUdp, proxyUdpClient, proxyUdpEndpoint, processName, token);

                    // Keep TCP connection alive - when it closes, UDP relay ends
                    var tcpHoldTask = HoldTcpConnectionAsync(stream, token);

                    await Task.WhenAny(relayTask, tcpHoldTask);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UDP Associate Error: {ex.Message}");
            }
            finally
            {
                proxyUdpClient?.Dispose();
                proxyControlClient?.Dispose();
            }
        }

        private async Task HoldTcpConnectionAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] buffer = new byte[1];
            try
            {
                // This will complete when TCP connection closes
                await stream.ReadAsync(buffer, 0, 1, token);
            }
            catch { }
        }

        private async Task RunUdpRelayFull(UdpClient localUdp, UdpClient? proxyUdp, IPEndPoint? proxyEndpoint,
            string processName, CancellationToken token)
        {
            // Track client endpoint for responses
            IPEndPoint? clientEndpoint = null;
            var directUdpClients = new System.Collections.Concurrent.ConcurrentDictionary<string, UdpClient>();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var receiveTask = localUdp.ReceiveAsync();
                    var completedTask = await Task.WhenAny(receiveTask, Task.Delay(Timeout.Infinite, token));

                    if (completedTask != receiveTask) break;

                    var result = await receiveTask;
                    clientEndpoint = result.RemoteEndPoint;

                    // Parse SOCKS5 UDP header to get destination
                    var (targetHost, targetPort, payload) = ParseUdpPacket(result.Buffer);
                    if (payload.Length == 0 || string.IsNullOrEmpty(targetHost)) continue;

                    _trafficMonitor.AddLiveTraffic(processName, payload.Length, false); // Upload

                    // Determine action for this specific destination
                    var decision = ResolveAction(processName, targetHost);

                    if (decision.Action == RuleAction.Block)
                    {
                        continue; // Drop packet
                    }

                    if (proxyUdp != null && proxyEndpoint != null && decision.Proxy != null)
                    {
                        // Forward through SOCKS5 proxy
                        // Packet already has SOCKS5 UDP header, forward as-is
                        await proxyUdp.SendAsync(result.Buffer, result.Buffer.Length);

                        // Start receiving from proxy if not already
                        _ = ReceiveFromProxyLoop(proxyUdp, localUdp, clientEndpoint, processName, token);
                    }
                    else
                    {
                        // Direct UDP - forward payload directly to destination
                        string key = $"{targetHost}:{targetPort}";
                        if (!directUdpClients.TryGetValue(key, out var directClient))
                        {
                            directClient = new UdpClient();
                            try
                            {
                                // Resolve target
                                IPAddress targetIp;
                                if (!IPAddress.TryParse(targetHost, out targetIp!))
                                {
                                    var addresses = await Dns.GetHostAddressesAsync(targetHost, token);
                                    targetIp = addresses.FirstOrDefault() ?? IPAddress.Loopback;
                                }
                                directClient.Connect(targetIp, targetPort);
                                directUdpClients[key] = directClient;

                                // Start receiving responses
                                _ = ReceiveDirectUdpLoop(directClient, localUdp, clientEndpoint, targetHost, targetPort, processName, token);
                            }
                            catch
                            {
                                directClient.Dispose();
                                continue;
                            }
                        }

                        await directClient.SendAsync(payload, payload.Length);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UDP Relay Error: {ex.Message}");
            }
            finally
            {
                foreach (var client in directUdpClients.Values)
                {
                    try { client.Dispose(); } catch { }
                }
            }
        }

        private (string Host, int Port, byte[] Payload) ParseUdpPacket(byte[] packet)
        {
            // SOCKS5 UDP header: RSV(2) FRAG(1) ATYP(1) DST.ADDR DST.PORT DATA
            if (packet.Length < 10) return ("", 0, Array.Empty<byte>());

            int offset = 3; // Skip RSV + FRAG
            byte atyp = packet[offset++];

            string host = "";
            if (atyp == 0x01) // IPv4
            {
                if (packet.Length < offset + 4 + 2) return ("", 0, Array.Empty<byte>());
                host = new IPAddress(packet[offset..(offset + 4)]).ToString();
                offset += 4;
            }
            else if (atyp == 0x03) // Domain
            {
                byte len = packet[offset++];
                if (packet.Length < offset + len + 2) return ("", 0, Array.Empty<byte>());
                host = Encoding.ASCII.GetString(packet, offset, len);
                offset += len;
            }
            else if (atyp == 0x04) // IPv6
            {
                if (packet.Length < offset + 16 + 2) return ("", 0, Array.Empty<byte>());
                host = new IPAddress(packet[offset..(offset + 16)]).ToString();
                offset += 16;
            }
            else return ("", 0, Array.Empty<byte>());

            int port = (packet[offset] << 8) | packet[offset + 1];
            offset += 2;

            byte[] payload = new byte[packet.Length - offset];
            if (payload.Length > 0)
                Buffer.BlockCopy(packet, offset, payload, 0, payload.Length);

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
    }
}
