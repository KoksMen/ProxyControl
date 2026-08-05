using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ProxyControl.Models;
using ProxyControl.ViewModels;

namespace ProxyControl.Services
{
    /// <summary>
    /// TUN-based VPN service using sing-box.
    /// Routes ALL system traffic through the local proxy service (127.0.0.1:8000), 
    /// which then applies rules and routes to upstream proxies.
    /// </summary>
    public class TunService : IDisposable
    {
        private const string SingBoxExe = "sing-box.exe";
        private const string ConfigFile = "sing-box-config.json";
        private const string DownloadUrl = "https://github.com/SagerNet/sing-box/releases/download/v1.8.0/sing-box-1.8.0-windows-amd64.zip";

        private Process? _singBoxProcess;
        private bool _isRunning;
        private readonly SemaphoreSlim _stateGate = new(1, 1);
        private readonly string _dataDir;
        private readonly AppLoggerService _logger;
        private readonly ConcurrentDictionary<string, TunTraceContext> _trafficTraces = new();
        private long _requestedConfigVersion;
        private long _processedLogLineCount;
        private string _lastError = string.Empty;

        private static readonly Regex AnsiEscapeRegex = new(
            @"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex TraceIdRegex = new(
            @"\[(?<id>\d+)\s+[^\]]+\]", RegexOptions.Compiled);
        private static readonly Regex InboundConnectionRegex = new(
            @"inbound/tun\[[^\]]+\]: inbound (?<packet>packet )?connection to (?<host>.+):(?<port>\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ProcessPathRegex = new(
            @"router: found process path:\s*(?<path>.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex OutboundConnectionRegex = new(
            @"outbound/(?<type>[^\[]+)\[(?<tag>[^\]]+)\]: outbound (?<packet>packet )?connection to (?<host>.+):(?<port>\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public bool IsRunning => _isRunning;
        public string LastError => _lastError;
        public event Action<bool>? StatusChanged;
        public event Action<bool, string>? ApplyStatusChanged;
        public event Action<TunTrafficEvent>? TrafficObserved;

        public TunService()
        {
            _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyControl", "tun");
            Directory.CreateDirectory(_dataDir);
            _logger = AppLoggerService.Instance;
        }

        /// <summary>
        /// Removes sing-box left behind by a previous ProxyControl process.
        /// Only the binary stored in ProxyControl's own TUN directory is touched.
        /// </summary>
        public static int StopOrphanedManagedProcesses()
        {
            string expectedPath = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProxyControl", "tun", SingBoxExe));
            int stopped = 0;

            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SingBoxExe)))
            {
                try
                {
                    string? processPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(processPath) ||
                        !string.Equals(Path.GetFullPath(processPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    stopped++;
                }
                catch
                {
                    // A process can exit between enumeration and inspection.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return stopped;
        }

        /// <summary>
        /// Start TUN mode - routes all traffic through local proxy
        /// </summary>
        public async Task<bool> StartAsync(TunRulesConfig rulesConfig)
        {
            // Callers update rules from several UI paths. Capture the complete
            // request now, rather than retaining references to mutable UI lists.
            var configSnapshot = CreateSnapshot(rulesConfig);
            long requestVersion = Interlocked.Increment(ref _requestedConfigVersion);
            _lastError = string.Empty;
            ReportApplyStatus(requestVersion, true, "Applying TUN rules…");

            await _stateGate.WaitAsync();
            try
            {
                if (!IsLatestRequest(requestVersion)) return true;

                // A previous application instance may have been terminated
                // before it could stop its child process.
                if (_singBoxProcess == null || _singBoxProcess.HasExited)
                {
                    int orphanCount = StopOrphanedManagedProcesses();
                    if (orphanCount > 0)
                        _logger.Warning("TUN", $"Stopped {orphanCount} orphaned sing-box process(es).");
                }

                // Ensure sing-box exists
                var singBoxPath = Path.Combine(_dataDir, SingBoxExe);
                if (!File.Exists(singBoxPath))
                {
                    _logger.Info("TUN", "sing-box not found, downloading...");
                    if (!await DownloadSingBoxAsync())
                    {
                        _lastError = "sing-box could not be downloaded.";
                        _logger.Error("TUN", "Failed to download sing-box");
                        ReportApplyStatus(requestVersion, false, "Failed to apply TUN rules");
                        return false;
                    }
                }

                // Generate config content first
                var configPath = Path.Combine(_dataDir, ConfigFile);
                var newJson = GenerateConfigJson(configSnapshot);
                var currentJson = File.Exists(configPath) ? File.ReadAllText(configPath) : null;

                // A newer UI action superseded this request while it was queued.
                if (!IsLatestRequest(requestVersion)) return true;

                // Optimization: If config is same and process running, do nothing
                if (_isRunning && _singBoxProcess != null && !_singBoxProcess.HasExited &&
                    string.Equals(newJson, currentJson, StringComparison.Ordinal))
                {
                    _logger.Info("TUN", "Config unchanged, skipping restart.");
                    ReportApplyStatus(requestVersion, false, "TUN rules are up to date");
                    return true;
                }

                // Restart needed: Stop first if running
                if (_isRunning) StopCore();

                if (!IsLatestRequest(requestVersion)) return true;

                File.WriteAllText(configPath, newJson);
                _logger.Info("TUN", $"Config generated/updated.");

                // Start sing-box
                var psi = new ProcessStartInfo
                {
                    FileName = singBoxPath,
                    Arguments = $"run -c \"{configPath}\"",
                    WorkingDirectory = _dataDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Verb = "runas" // Request admin
                };

                _singBoxProcess = new Process { StartInfo = psi };
                string? startupError = null;
                _singBoxProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.Debug("TUN", e.Data);
                        ProcessSingBoxLogLine(e.Data);
                    }
                };
                _singBoxProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        startupError = e.Data;
                        _logger.Warning("TUN", e.Data);
                        ProcessSingBoxLogLine(e.Data);
                    }
                };

                _singBoxProcess.Start();
                _singBoxProcess.BeginOutputReadLine();
                _singBoxProcess.BeginErrorReadLine();

                // Wait a bit to check if it started successfully
                await Task.Delay(1500);

                if (_singBoxProcess.HasExited)
                {
                    _lastError = string.IsNullOrWhiteSpace(startupError)
                        ? $"sing-box exited with code {_singBoxProcess.ExitCode}."
                        : startupError;
                    _logger.Error("TUN", _lastError);
                    ReportApplyStatus(requestVersion, false, "Failed to apply TUN rules");
                    return false;
                }

                // Do not leave an obsolete process running just because a new
                // rule update arrived during sing-box startup.
                if (!IsLatestRequest(requestVersion))
                {
                    StopCore();
                    return true;
                }

                _isRunning = true;
                _logger.Info("TUN", $"TUN mode started.");
                StatusChanged?.Invoke(true);
                ReportApplyStatus(requestVersion, false, "TUN rules applied");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.Error("TUN", $"Start failed: {ex.Message}");
                ReportApplyStatus(requestVersion, false, "Failed to apply TUN rules");
                return false;
            }
            finally
            {
                _stateGate.Release();
            }
        }

        /// <summary>
        /// Stop TUN mode
        /// </summary>
        public void Stop()
        {
            // Invalidate queued StartAsync calls before waiting for the lock.
            // Otherwise an old queued request can start TUN after the user turns it off.
            Interlocked.Increment(ref _requestedConfigVersion);
            ApplyStatusChanged?.Invoke(false, "TUN is off");
            _stateGate.Wait();
            try
            {
                StopCore();
            }
            finally
            {
                _stateGate.Release();
            }
        }

        private bool IsLatestRequest(long requestVersion) =>
            requestVersion == Volatile.Read(ref _requestedConfigVersion);

        private void ReportApplyStatus(long requestVersion, bool isApplying, string message)
        {
            if (IsLatestRequest(requestVersion))
                ApplyStatusChanged?.Invoke(isApplying, message);
        }

        internal static TunRulesConfig CreateSnapshot(TunRulesConfig? source)
        {
            source ??= new TunRulesConfig();
            return new TunRulesConfig
            {
                Mode = source.Mode,
                ProxyType = source.ProxyType,
                DnsServer = source.DnsServer,
                UseDnsProtection = source.UseDnsProtection,
                SystemDnsServers = source.SystemDnsServers?.ToList() ?? new List<string>(),
                UpstreamProxyHosts = source.UpstreamProxyHosts?.ToList() ?? new List<string>(),
                Rules = source.Rules?.Select(rule => new TrafficRule
                {
                    IsEnabled = rule.IsEnabled,
                    Action = rule.Action,
                    BlockDirection = rule.BlockDirection,
                    ProxyId = rule.ProxyId,
                    TrafficType = rule.TrafficType,
                    TargetApps = new List<string>(rule.TargetApps ?? new List<string>()),
                    TargetHosts = new List<string>(rule.TargetHosts ?? new List<string>())
                }).ToList() ?? new List<TrafficRule>(),
                Proxies = source.Proxies?.Select(proxy => new ProxyItem
                {
                    Id = proxy.Id,
                    Name = proxy.Name,
                    IpAddress = proxy.IpAddress,
                    Port = proxy.Port,
                    Username = proxy.Username,
                    Password = proxy.Password,
                    IsEnabled = proxy.IsEnabled,
                    Type = proxy.Type,
                    UseTls = proxy.UseTls,
                    UseSsl = proxy.UseSsl
                }).ToList() ?? new List<ProxyItem>()
            };
        }

        private void StopCore()
        {
            if (!_isRunning && _singBoxProcess == null) return;
            try
            {
                if (_singBoxProcess != null && !_singBoxProcess.HasExited)
                {
                    _singBoxProcess.Kill();
                    _singBoxProcess.WaitForExit(3000);
                }
            }
            catch { }
            finally
            {
                _singBoxProcess?.Dispose();
                _singBoxProcess = null;
                _trafficTraces.Clear();
                int orphanCount = StopOrphanedManagedProcesses();
                if (orphanCount > 0)
                    _logger.Warning("TUN", $"Stopped {orphanCount} orphaned sing-box process(es).");
                _isRunning = false;
                _logger.Info("TUN", "TUN mode stopped");
                StatusChanged?.Invoke(false);
            }
        }

        private List<object> GenerateRouteRules(TunRulesConfig rulesConfig)
        {
            var singboxRules = new List<object>();

            // 1. Essential System Rules (DNS, Loopback, etc.)
            // These are now handled in GenerateConfigJson for Blacklist mode to ensure they are at the top.
            // But for Whitelist mode (Default Direct), we might not need them as rules, 
            // EXCEPT if we want to force them Direct even if a wildcard rule exists?
            // Actually, in Whitelist mode, default is Direct, so these are redundant unless overridden.
            // Let's keep them common if possible, or leave them to GenerateConfigJson.
            // For now, let GenerateConfigJson handle the critical system excludes for Blacklist mode.
            // For Whitelist mode, we just need to add the user's proxy rules.

            if (rulesConfig != null && rulesConfig.Rules != null)
            {
                // sing-box uses the first matching route. A protocol-specific
                // rule must therefore win over an older/broader Any rule for
                // the same process or host.
                var orderedRules = rulesConfig.Rules
                    .Select((rule, index) => new { Rule = rule, Index = index })
                    .OrderByDescending(item => GetRuleSpecificity(item.Rule))
                    .ThenBy(item => item.Rule.TrafficType == RuleTrafficType.Any ? 1 : 0)
                    .ThenBy(item => item.Index)
                    .Select(item => item.Rule);

                foreach (var rule in orderedRules)
                {
                    if (!rule.IsEnabled) continue;

                    string outboundTag;

                    if (rulesConfig.Mode == RuleMode.BlackList)
                    {
                        // Blacklist Mode: Default is PROXY.
                        // Rules define what to BLOCK or what to go DIRECT.
                        // If rule says PROXY -> It's redundant (matches default), unless it's a specific proxy?
                        // sing-box "route" doesn't easily support per-rule proxies without defining multiple outbounds.
                        // For now, we assume "Proxy" action means "Use Default Proxy".

                        if (rule.Action == RuleAction.Block) outboundTag = "block";
                        else if (rule.Action == RuleAction.Direct) outboundTag = "direct";
                        else continue; // Proxy action is default, so skip rule
                    }
                    else
                    {
                        // Whitelist Mode: Default is DIRECT.
                        // Rules define what to PROXY or BLOCK.

                        if (rule.Action == RuleAction.Block) outboundTag = "block";
                        else if (rule.Action == RuleAction.Proxy)
                        {
                            var selectedProxy = rulesConfig.Proxies.FirstOrDefault(proxy =>
                                proxy.IsEnabled &&
                                string.Equals(proxy.Id, rule.ProxyId, StringComparison.OrdinalIgnoreCase));
                            if (selectedProxy == null)
                                continue;

                            // HTTP CONNECT cannot carry raw UDP. For legacy Any
                            // rules, proxy TCP and leave UDP on WhiteList's direct
                            // final route.
                            if (selectedProxy.Type == ProxyType.Http &&
                                rule.TrafficType is RuleTrafficType.UDP or RuleTrafficType.DNS or RuleTrafficType.WebRTC)
                                continue;

                            outboundTag = GetProxyOutboundTag(selectedProxy.Id);
                        }
                        else continue; // Direct action is default, so skip rule
                    }

                    var matchObject = new Dictionary<string, object> { { "outbound", outboundTag } };
                    bool hasMatch = false;

                    ApplyTrafficTypeMatch(matchObject, rule.TrafficType, ref hasMatch);

                    if (rulesConfig.Mode == RuleMode.WhiteList &&
                        rule.Action == RuleAction.Proxy &&
                        rule.TrafficType == RuleTrafficType.Any)
                    {
                        var selectedProxy = rulesConfig.Proxies.FirstOrDefault(proxy =>
                            proxy.IsEnabled &&
                            string.Equals(proxy.Id, rule.ProxyId, StringComparison.OrdinalIgnoreCase));
                        if (selectedProxy?.Type == ProxyType.Http)
                        {
                            matchObject["network"] = "tcp";
                            hasMatch = true;
                        }
                    }

                    // Match Apps
                    if (rule.TargetApps != null && rule.TargetApps.Count > 0)
                    {
                        // sing-box process_name matching
                        var apps = rule.TargetApps.Where(a => a != "*").Select(a => a.EndsWith(".exe") ? a : a + ".exe").ToArray();
                        if (apps.Length > 0)
                        {
                            matchObject["process_name"] = apps;
                            hasMatch = true;
                        }
                    }

                    // Match Hosts (Domains)
                    if (rule.TargetHosts != null && rule.TargetHosts.Count > 0)
                    {
                        var domains = rule.TargetHosts
                            .Where(h => h != "*" && !IPAddress.TryParse(h, out _))
                            .Select(h => h.StartsWith("*.") ? h.Substring(2) : h)
                            .ToArray();
                        if (domains.Length > 0)
                        {
                            matchObject["domain_suffix"] = domains;
                            hasMatch = true;
                        }

                        var addresses = rule.TargetHosts
                            .Where(h => h != "*" && IPAddress.TryParse(h, out _))
                            .ToArray();
                        if (addresses.Length > 0)
                        {
                            matchObject["ip_cidr"] = addresses
                                .Select(address => address.Contains(':') ? address + "/128" : address + "/32")
                                .ToArray();
                            hasMatch = true;
                        }
                    }

                    bool isGlobalAppScope = rule.TargetApps == null ||
                                            rule.TargetApps.Count == 0 ||
                                            rule.TargetApps.Contains("*");
                    bool isGlobalHostScope = rule.TargetHosts == null ||
                                             rule.TargetHosts.Count == 0 ||
                                             rule.TargetHosts.Contains("*");

                    if (hasMatch || (isGlobalAppScope && isGlobalHostScope))
                    {
                        singboxRules.Add(matchObject);
                    }
                }
            }

            return singboxRules;
        }

        internal static void ApplyTrafficTypeMatch(
            Dictionary<string, object> matchObject,
            RuleTrafficType trafficType,
            ref bool hasMatch)
        {
            switch (trafficType)
            {
                case RuleTrafficType.TCP:
                    matchObject["network"] = "tcp";
                    hasMatch = true;
                    break;
                case RuleTrafficType.UDP:
                    matchObject["network"] = "udp";
                    hasMatch = true;
                    break;
                case RuleTrafficType.DNS:
                    matchObject["protocol"] = "dns";
                    matchObject["port"] = 53;
                    hasMatch = true;
                    break;
                case RuleTrafficType.HTTPS:
                    matchObject["network"] = "tcp";
                    matchObject["protocol"] = "tls";
                    matchObject["port"] = 443;
                    hasMatch = true;
                    break;
                case RuleTrafficType.WebSocket:
                    // sing-box cannot inspect the Upgrade header in a route rule,
                    // but requiring sniffed HTTP avoids treating every TCP flow as
                    // WebSocket traffic.
                    matchObject["network"] = "tcp";
                    matchObject["protocol"] = "http";
                    hasMatch = true;
                    break;
                case RuleTrafficType.WebRTC:
                    matchObject["network"] = "udp";
                    matchObject["port_range"] = new[] { "3478", "5349", "49152:65535" };
                    hasMatch = true;
                    break;
            }
        }

        /// <summary>
        /// Converts sing-box's correlated inbound/outbound trace lines into one
        /// connection event. WhiteList routes bypass TcpProxyService, so this is
        /// the authoritative source for those connections.
        /// </summary>
        private static int GetRuleSpecificity(TrafficRule rule)
        {
            int score = 0;
            if (rule.TargetApps?.Any(app => app != "*") == true) score++;
            if (rule.TargetHosts?.Any(host => host != "*") == true) score++;
            return score;
        }

        /// <summary>
        /// Converts sing-box's correlated inbound/outbound trace lines into one
        /// connection event. WhiteList routes bypass TcpProxyService, so this is
        /// the authoritative source for those connections.
        /// </summary>
        internal void ProcessSingBoxLogLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return;

            string line = AnsiEscapeRegex.Replace(rawLine, string.Empty).Trim();
            var traceIdMatch = TraceIdRegex.Match(line);
            if (!traceIdMatch.Success) return;

            string traceId = traceIdMatch.Groups["id"].Value;
            var inboundMatch = InboundConnectionRegex.Match(line);
            if (inboundMatch.Success)
            {
                _trafficTraces[traceId] = new TunTraceContext
                {
                    Host = inboundMatch.Groups["host"].Value,
                    Port = ParsePort(inboundMatch.Groups["port"].Value),
                    Network = inboundMatch.Groups["packet"].Success ? "udp" : "tcp",
                    LastSeenUtc = DateTime.UtcNow
                };
                CleanupOldTrafficTraces();
                return;
            }

            if (!_trafficTraces.TryGetValue(traceId, out var trace)) return;
            trace.LastSeenUtc = DateTime.UtcNow;

            var processMatch = ProcessPathRegex.Match(line);
            if (processMatch.Success)
            {
                trace.ProcessPath = NormalizeProcessPath(processMatch.Groups["path"].Value.Trim());
                trace.ProcessName = Path.GetFileName(trace.ProcessPath);
                return;
            }

            var outboundMatch = OutboundConnectionRegex.Match(line);
            if (!outboundMatch.Success) return;

            trace.OutboundType = outboundMatch.Groups["type"].Value;
            trace.OutboundTag = outboundMatch.Groups["tag"].Value;
            if (string.IsNullOrWhiteSpace(trace.Host))
                trace.Host = outboundMatch.Groups["host"].Value;
            if (trace.Port <= 0)
                trace.Port = ParsePort(outboundMatch.Groups["port"].Value);
            if (string.IsNullOrWhiteSpace(trace.Network))
                trace.Network = outboundMatch.Groups["packet"].Success ? "udp" : "tcp";

            _trafficTraces.TryRemove(traceId, out _);
            TrafficObserved?.Invoke(new TunTrafficEvent(
                string.IsNullOrWhiteSpace(trace.ProcessName) ? "System/TUN" : trace.ProcessName,
                trace.ProcessPath ?? string.Empty,
                trace.Host ?? string.Empty,
                trace.Port,
                trace.Network ?? "tcp",
                trace.OutboundTag ?? string.Empty,
                trace.OutboundType ?? string.Empty));
        }

        private void CleanupOldTrafficTraces()
        {
            if (Interlocked.Increment(ref _processedLogLineCount) % 256 != 0) return;

            DateTime cutoff = DateTime.UtcNow.AddMinutes(-1);
            foreach (var pair in _trafficTraces)
            {
                if (pair.Value.LastSeenUtc < cutoff)
                    _trafficTraces.TryRemove(pair.Key, out _);
            }
        }

        private static int ParsePort(string value) =>
            int.TryParse(value, out int port) ? port : 0;

        private static string NormalizeProcessPath(string processPath)
        {
            const string devicePrefix = @"\Device\";
            if (!processPath.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
                return processPath;

            foreach (var drive in DriveInfo.GetDrives())
            {
                string driveName = drive.Name.TrimEnd('\\');
                var target = new StringBuilder(512);
                if (QueryDosDevice(driveName, target, target.Capacity) == 0) continue;

                string devicePath = target.ToString();
                if (processPath.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
                    return driveName + processPath.Substring(devicePath.Length);
            }

            return processPath;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint QueryDosDevice(
            string lpDeviceName,
            StringBuilder lpTargetPath,
            int ucchMax);

        private sealed class TunTraceContext
        {
            public string? ProcessName { get; set; }
            public string? ProcessPath { get; set; }
            public string? Host { get; set; }
            public int Port { get; set; }
            public string? Network { get; set; }
            public string? OutboundTag { get; set; }
            public string? OutboundType { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }

        public sealed record TunTrafficEvent(
            string ProcessName,
            string ProcessPath,
            string Host,
            int Port,
            string Network,
            string OutboundTag,
            string OutboundType);

        public class TunRulesConfig
        {
            public RuleMode Mode { get; set; }
            public List<TrafficRule> Rules { get; set; } = new();
            public ProxyType ProxyType { get; set; }
            /// <summary>
            /// DNS resolver used by sing-box while TUN is active. This avoids
            /// routing system DNS back to the local loopback listener.
            /// </summary>
            public string DnsServer { get; set; } = string.Empty;
            /// <summary>
            /// When disabled, TUN resolves through Windows' system DNS instead
            /// of silently using ProxyControl's configured DNS server.
            /// </summary>
            public bool UseDnsProtection { get; set; }
            public List<string> SystemDnsServers { get; set; } = new();
            public List<string> UpstreamProxyHosts { get; set; } = new();
            public List<ProxyItem> Proxies { get; set; } = new();
        }

        internal static string GetProxyOutboundTag(string proxyId)
        {
            var safeId = new string((proxyId ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .ToArray());
            return "proxy-" + (safeId.Length > 0 ? safeId : "unknown");
        }

        internal static void InsertUpstreamProxyBypassRules(List<object> routes, TunRulesConfig rulesConfig)
        {
            var hosts = rulesConfig.UpstreamProxyHosts
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Select(host => host.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var resolvedAddresses = new HashSet<IPAddress>();
            foreach (var host in hosts)
            {
                if (IPAddress.TryParse(host, out var address))
                {
                    resolvedAddresses.Add(address);
                    continue;
                }

                try
                {
                    // ProxyControl resolves an upstream hostname before opening
                    // its socket. TUN therefore sees the numeric destination,
                    // not the original hostname.
                    foreach (var resolved in Dns.GetHostAddresses(host))
                        resolvedAddresses.Add(resolved);
                }
                catch
                {
                    // Keep the domain rule below; a later config refresh can
                    // add its resolved addresses when DNS is available.
                }
            }

            var addresses = resolvedAddresses
                .Select(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? address + "/128"
                    : address + "/32")
                .ToArray();
            if (addresses.Length > 0)
            {
                // ProxyControl opens the real upstream connection itself. If that
                // socket is captured again by TUN, proxy-out points back to
                // ProxyControl and creates an infinite connection loop.
                routes.Insert(0, new { ip_cidr = addresses, outbound = "direct" });
            }

            var domains = hosts
                .Where(host => !IPAddress.TryParse(host, out _))
                .ToArray();
            if (domains.Length > 0)
            {
                routes.Insert(0, new { domain = domains, outbound = "direct" });
            }
        }

        internal string GenerateConfigJson(TunRulesConfig? rulesConfig = null)
        {
            if (rulesConfig == null) rulesConfig = new TunRulesConfig { Mode = RuleMode.BlackList, ProxyType = ProxyType.Socks5 };

            // Logic Split:
            // Scenario A (HTTP Proxy OR Whitelist Mode): Use sing-box routing.
            // Scenario B (SOCKS5 Proxy AND Blacklist Mode): Use TcpProxyService routing (All traffic -> proxy-out).

            bool useSingBoxRouting = (rulesConfig.ProxyType == ProxyType.Http || rulesConfig.Mode == RuleMode.WhiteList);

            string finalOutbound;
            List<object> routes;

            if (useSingBoxRouting)
            {
                // Scenario A: sing-box Routing
                // Whitelist: Final = Direct, Rules -> Proxy
                // Blacklist: Final = Proxy, Rules -> Block/Direct

                if (rulesConfig.Mode == RuleMode.BlackList)
                {
                    finalOutbound = "proxy-out";
                    routes = GenerateRouteRules(rulesConfig);
                    InsertSystemRoutes(routes);
                }
                else
                {
                    // Whitelist Mode
                    finalOutbound = "direct";
                    routes = GenerateRouteRules(rulesConfig);
                    // Windows points DNS at the TUN peer (172.19.0.2). Without
                    // interception, a direct port-53 route loops back into the
                    // virtual adapter and every hostname lookup times out.
                    InsertSystemRoutes(routes);
                }
            }
            else
            {
                // Scenario B: Default PROXY, TcpProxyService handles blocking/WebRTC
                finalOutbound = "proxy-out";
                routes = new List<object>();
                // We don't need app-specific routes here because everything goes to proxy-out
                InsertSystemRoutes(routes, append: true);
            }

            // Must precede every user rule and the final outbound in both modes.
            InsertUpstreamProxyBypassRules(routes, rulesConfig);

            var configuredDnsAddress = NormalizeDnsServer(rulesConfig.DnsServer);
            if (rulesConfig.UseDnsProtection && string.IsNullOrWhiteSpace(configuredDnsAddress))
                throw new InvalidOperationException("DNS Protection in TUN mode requires an explicitly configured DNS server.");

            var dnsServers = new List<object>();
            string dnsServerTag;
            if (rulesConfig.UseDnsProtection)
            {
                bool isDnsUrl = Uri.TryCreate(configuredDnsAddress, UriKind.Absolute, out var dnsUri) &&
                    (dnsUri.Scheme == Uri.UriSchemeHttps || dnsUri.Scheme == Uri.UriSchemeHttp);
                object configuredDnsServer = isDnsUrl || IPAddress.TryParse(configuredDnsAddress, out _)
                    ? new { tag = "configured", address = configuredDnsAddress, detour = "direct" }
                    : new { tag = "configured", address = configuredDnsAddress, address_resolver = "local", detour = "direct" };
                dnsServers.Insert(0, configuredDnsServer);
                dnsServerTag = "configured";
            }
            else
            {
                var systemDns = rulesConfig.SystemDnsServers
                    .Where(address => IPAddress.TryParse(address, out var parsed) &&
                                      !IPAddress.IsLoopback(parsed) &&
                                      !parsed.Equals(IPAddress.Any))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (systemDns.Length == 0)
                    throw new InvalidOperationException("TUN requires a reachable system DNS server when DNS Protection is disabled.");

                for (var index = 0; index < systemDns.Length; index++)
                {
                    var tag = $"system-{index}";
                    dnsServers.Add(new { tag, address = systemDns[index], detour = "direct" });
                }
                dnsServerTag = "system-0";
            }

            var config = new
            {
                // Keep sing-box's normal connection traces enabled. The output
                // is observed for Monitor/Connection Logs but does not change
                // TUN routing behaviour.
                log = new { level = "info", timestamp = true },
                dns = new
                {
                    servers = dnsServers.ToArray(),
                    rules = new object[]
                    {
                        new { outbound = "any", server = dnsServerTag }
                    },
                    strategy = "ipv4_only"
                },
                inbounds = new object[]
                {
                    new
                    {
                        type = "tun",
                        tag = "tun-in",
                        interface_name = "ProxyControlTUN",
                        inet4_address = "172.19.0.1/30",
                        mtu = 1400,
                        auto_route = true,
                        strict_route = true,
                        stack = "system",
                        sniff = true,
                        sniff_override_destination = true
                    }
                },
                outbounds = CreateOutbounds(rulesConfig),
                route = new
                {
                    auto_detect_interface = true,
                    final = finalOutbound,
                    rules = routes.ToArray()
                }
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        private static string NormalizeDnsServer(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var normalized = value.Trim();
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                return uri.AbsoluteUri;
            return normalized.TrimEnd('.');
        }

        private static void InsertSystemRoutes(List<object> routes, bool append = false)
        {
            var systemRoutes = new List<object>();
            // TUN must answer DNS itself; forwarding 127.0.0.1:53 through a
            // direct outbound can leave Windows' DNS Client waiting forever.
            // dns-out uses the DNS server selected in ProxyControl's settings.
            systemRoutes.Add(new { protocol = "dns", outbound = "dns-out" });
            systemRoutes.Add(new { port = 53, outbound = "dns-out" });

            systemRoutes.Add(new { ip_cidr = new[] { "127.0.0.1/32", "0.0.0.0/32", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }, outbound = "direct" });
            systemRoutes.Add(new { port = new[] { 8000, 8080 }, outbound = "direct" });
            systemRoutes.Add(new { process_name = new[] { Process.GetCurrentProcess().ProcessName + ".exe", "ProxyControl.exe", "sing-box.exe" }, outbound = "direct" });

            if (append)
                routes.AddRange(systemRoutes);
            else
                routes.InsertRange(0, systemRoutes);
        }

        private object[] CreateOutbounds(TunRulesConfig rulesConfig)
        {
            // Route to LOCAL ProxyControl SOCKS5 server (127.0.0.1:8000)
            var proxyOutbound = new
            {
                type = "socks",
                tag = "proxy-out",
                server = "127.0.0.1",
                server_port = 8000,
                version = "5"
            };

            var outbounds = new List<object>
            {
                proxyOutbound,
                new { type = "direct", tag = "direct" },
                new { type = "block", tag = "block" },
                new { type = "dns", tag = "dns-out" }
            };

            foreach (var proxy in rulesConfig.Proxies
                         .Where(proxy => proxy.IsEnabled)
                         .GroupBy(proxy => proxy.Id, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                var outbound = new Dictionary<string, object>
                {
                    ["tag"] = GetProxyOutboundTag(proxy.Id),
                    ["server"] = proxy.IpAddress,
                    ["server_port"] = proxy.Port
                };

                if (!string.IsNullOrEmpty(proxy.Username))
                    outbound["username"] = proxy.Username;
                if (!string.IsNullOrEmpty(proxy.Password))
                    outbound["password"] = proxy.Password;

                if (proxy.Type == ProxyType.Http)
                {
                    outbound["type"] = "http";
                    if (proxy.UseTls || proxy.UseSsl)
                    {
                        outbound["tls"] = new
                        {
                            enabled = true,
                            server_name = proxy.IpAddress,
                            insecure = true
                        };
                    }
                }
                else
                {
                    outbound["type"] = "socks";
                    outbound["version"] = proxy.Type == ProxyType.Socks4 ? "4" : "5";
                }

                outbounds.Add(outbound);
            }

            return outbounds.ToArray();
        }

        private async Task<bool> DownloadSingBoxAsync()
        {
            try
            {
                var zipPath = Path.Combine(_dataDir, "sing-box.zip");

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromMinutes(5);

                _logger.Info("TUN", $"Downloading from {DownloadUrl}");
                var bytes = await http.GetByteArrayAsync(DownloadUrl);
                await File.WriteAllBytesAsync(zipPath, bytes);

                // Extract
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, _dataDir, true);

                // Find and move exe
                foreach (var dir in Directory.GetDirectories(_dataDir))
                {
                    var exe = Path.Combine(dir, SingBoxExe);
                    if (File.Exists(exe))
                    {
                        File.Move(exe, Path.Combine(_dataDir, SingBoxExe), true);
                        break;
                    }
                }

                File.Delete(zipPath);
                _logger.Info("TUN", "sing-box downloaded successfully");
                return File.Exists(Path.Combine(_dataDir, SingBoxExe));
            }
            catch (Exception ex)
            {
                _logger.Error("TUN", $"Download failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            Stop();
            _stateGate.Dispose();
        }
    }
}
