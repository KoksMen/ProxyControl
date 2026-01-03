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
        private string? _blackListProxyId;
        private RuleMode _currentMode;
        private readonly ProcessMonitorService _processMonitor;
        private readonly TrafficMonitorService _trafficMonitor;
        private const int LocalPort = 8000;
        private const int BufferSize = 8192;
        // Лимит размера заголовка для защиты и парсинга (16 KB)
        private const int MaxHeaderSize = 16 * 1024;

        private readonly ConcurrentDictionary<Guid, ClientContext> _activeClients = new ConcurrentDictionary<Guid, ClientContext>();
        private readonly HttpClient _geoHttpClient;

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
                CountryCode = p.CountryCode
            }).ToList();

            _localBlackList = config.BlackListRules?.ToList() ?? new List<TrafficRule>();
            _localWhiteList = config.WhiteListRules?.ToList() ?? new List<TrafficRule>();

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
                catch
                {
                    if (!_isRunning) break;
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
                    if (bytesRead == 0) break; // Connection closed

                    totalBytesRead += bytesRead;

                    // Ищем конец заголовков \r\n\r\n (13, 10, 13, 10)
                    // Оптимизация: искать только в новой порции данных + небольшой перехлест
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
                // Возвращаем то, что прочитали. Если заголовка полного нет, вернем сколько есть для попытки парсинга (fail-safe)
                string headerStr = Encoding.ASCII.GetString(buffer, 0, totalBytesRead);

                // Копируем данные в точный массив, чтобы вернуть арендованный,
                // или оставляем арендованный, но тогда caller должен вернуть.
                // Для простоты здесь: копируем в новый массив нужного размера, возвращаем арендованный.
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
                    return i + 4; // Возвращаем позицию сразу после заголовков
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

                if (pid == 0)
                {
                    await Task.Delay(10);
                    pid = SystemProxyHelper.GetPidByPort(clientPort);
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

                // --- Improved Header Reading ---
                var headerData = await ReadHeaderAsync(clientStream, ctx.Cts.Token);
                byte[] buffer = headerData.Buffer;
                int bytesRead = headerData.Length;
                string headerStr = headerData.HeaderStr;

                if (bytesRead == 0) return;

                if (!TryGetTargetHost(headerStr, out string targetHost, out int targetPort, out bool isConnectMethod))
                {
                    return;
                }

                var decision = ResolveAction(processName, targetHost);

                // Determine display status
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
                historyItem = _trafficMonitor.CreateConnectionItem(processName, icon, targetHost, decision.Action == RuleAction.Block ? logResult : decision.Action.ToString(), details, flagUrl, logColor);

                OnConnectionLog?.Invoke(new ConnectionLog
                {
                    ProcessName = processName,
                    Host = targetHost,
                    Result = logResult,
                    Color = logColor,
                    AppIcon = icon,
                    CountryFlagUrl = flagUrl
                });

                // 1. Полная блокировка - разрываем соединение сразу
                if (decision.Action == RuleAction.Block && decision.BlockDir == BlockDirection.Both) return;

                // 2. Иначе - продолжаем (частичная блокировка или пропуск)
                ProxyItem? targetProxy = decision.Proxy;

                var remoteServer = new TcpClient();
                remoteServer.NoDelay = true;
                ctx.Remote = remoteServer;

                // Подключение к удаленному серверу
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

                    // Читаем ответ от прокси (также лучше бы использовать буферизированное чтение, но для простоты оставим массив)
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
                        // Если заблокирован Outbound, не отправляем данные на сервер
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
                else
                {
                    // Direct connection
                    await remoteServer.ConnectAsync(targetHost, targetPort, ctx.Cts.Token);
                    var remoteStream = remoteServer.GetStream();

                    if (isConnectMethod)
                    {
                        byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                        await clientStream.WriteAsync(ok, 0, ok.Length, ctx.Cts.Token);
                    }
                    else
                    {
                        // Если заблокирован Outbound, не отправляем данные на сервер
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

        private async Task BridgeStreams(NetworkStream clientStream, NetworkStream remoteStream, string processName, ConnectionHistoryItem? historyItem, BlockDirection blockDir, CancellationToken token)
        {
            // Upload: Client -> Remote (Блокируем если Outbound)
            var uploadTask = (blockDir == BlockDirection.Outbound)
                ? DiscardAsync(clientStream, token)
                : CopyAndTrackAsync(clientStream, remoteStream, processName, isDownload: false, historyItem, token);

            // Download: Remote -> Client (Блокируем если Inbound)
            var downloadTask = (blockDir == BlockDirection.Inbound)
                ? DiscardAsync(remoteStream, token)
                : CopyAndTrackAsync(remoteStream, clientStream, processName, isDownload: true, historyItem, token);

            // Ждем пока любой из активных потоков завершится (обрыв связи или конец передачи)
            await Task.WhenAny(uploadTask, downloadTask);
        }

        private async Task DiscardAsync(NetworkStream source, CancellationToken token)
        {
            // Читаем из потока "в пустоту", чтобы поддерживать соединение живым, но не передавать данные
            // Используем ArrayPool для минимизации аллокаций даже тут
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                while (await source.ReadAsync(buffer, 0, buffer.Length, token) > 0)
                {
                    // Просто игнорируем данные
                }
            }
            catch { }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task CopyAndTrackAsync(NetworkStream source, NetworkStream destination, string processName, bool isDownload, ConnectionHistoryItem? historyItem, CancellationToken token)
        {
            // Оптимизация: использование ArrayPool
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, bytesRead, token);

                    // Обновление статистики (лучше делать не на каждый пакет, если частота высокая, 
                    // но для монитора трафика оставим как есть для точности)
                    _trafficMonitor.AddLiveTraffic(processName, bytesRead, isDownload);
                    if (historyItem != null)
                    {
                        if (isDownload) historyItem.BytesDown += bytesRead;
                        else historyItem.BytesUp += bytesRead;
                    }
                }
            }
            catch { }
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
            if (_currentMode == RuleMode.BlackList) // Black List
            {
                var mainProxy = _localProxies.FirstOrDefault(p => p.Id.ToString() == _blackListProxyId && p.IsEnabled);

                foreach (var rule in _localBlackList)
                {
                    if (!rule.IsEnabled) continue;

                    if (IsRuleMatch(rule, app, host))
                    {
                        if (rule.Action == RuleAction.Block) return (RuleAction.Block, null, rule.BlockDirection);
                        if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null, BlockDirection.Both);
                        if (rule.ProxyId != null)
                        {
                            var p = _localProxies.FirstOrDefault(x => x.Id == rule.ProxyId);
                            if (p != null && p.IsEnabled) return (RuleAction.Proxy, p, BlockDirection.Both);
                            return (RuleAction.Direct, null, BlockDirection.Both);
                        }
                        // Default Blacklist Rule: Proxy with Main
                        return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
                    }
                }

                if (mainProxy != null) return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
                return (RuleAction.Direct, null, BlockDirection.Both);
            }
            else // White List
            {
                foreach (var rule in _localWhiteList)
                {
                    if (!rule.IsEnabled) continue;

                    if (IsRuleMatch(rule, app, host))
                    {
                        if (rule.Action == RuleAction.Block) return (RuleAction.Block, null, rule.BlockDirection);
                        if (rule.Action == RuleAction.Direct) return (RuleAction.Direct, null, BlockDirection.Both);
                        if (rule.ProxyId != null)
                        {
                            var p = _localProxies.FirstOrDefault(x => x.Id == rule.ProxyId);
                            if (p != null && p.IsEnabled) return (RuleAction.Proxy, p, BlockDirection.Both);

                            var mainProxyFallback = _localProxies.FirstOrDefault(p => p.Id.ToString() == _blackListProxyId && p.IsEnabled);
                            if (mainProxyFallback != null) return (RuleAction.Proxy, mainProxyFallback, BlockDirection.Both);
                        }

                        var mainProxy = _localProxies.FirstOrDefault(p => p.Id.ToString() == _blackListProxyId && p.IsEnabled);
                        if (mainProxy != null) return (RuleAction.Proxy, mainProxy, BlockDirection.Both);
                    }
                }
                return (RuleAction.Direct, null, BlockDirection.Both);
            }
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
                    if (rule.TargetHosts[i] == "*" || host.Contains(rule.TargetHosts[i], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            return true;
        }

        public async Task<(bool IsSuccess, string CountryCode, long Ping, double Speed)> CheckProxy(ProxyItem proxy)
        {
            if (string.IsNullOrEmpty(proxy.IpAddress) || proxy.Port == 0) return (false, "", 0, 0);

            bool connectionSuccess = false;
            long ping = 0;
            double speedMbps = 0;

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxy.IpAddress, proxy.Port),
                UseProxy = true
            };

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
                    var response = await client.GetAsync("http://www.google.com/generate_204");
                    sw.Stop();
                    ping = sw.ElapsedMilliseconds;
                    connectionSuccess = response.IsSuccessStatusCode;
                }
            }
            catch
            {
                connectionSuccess = false;
            }

            if (!connectionSuccess) return (false, "", 0, 0);

            try
            {
                var speedHandler = new HttpClientHandler
                {
                    Proxy = new WebProxy(proxy.IpAddress, proxy.Port),
                    UseProxy = true
                };
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

            return (connectionSuccess, country, ping, speedMbps);
        }
    }
}