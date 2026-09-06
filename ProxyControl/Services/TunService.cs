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
using System.Linq;
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
        private readonly string _dataDir;
        private readonly AppLoggerService _logger;
        private readonly SemaphoreSlim _lifecycleLock = new SemaphoreSlim(1, 1);
        private const string TunInterfaceName = "ProxyControlTUN";
        private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(8);
        private string? _lastError;

        public bool IsRunning => _isRunning;
        public string? LastError => _lastError;
        public event Action<bool>? StatusChanged;

        public TunService()
        {
            _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyControl", "tun");
            Directory.CreateDirectory(_dataDir);
            _logger = AppLoggerService.Instance;
        }

        /// <summary>
        /// Start TUN mode - routes all traffic through local proxy
        /// </summary>
        public async Task<bool> StartAsync(TunRulesConfig rulesConfig, CancellationToken cancellationToken = default)
        {
            bool lockAcquired = false;
            try
            {
                await _lifecycleLock.WaitAsync(cancellationToken);
                lockAcquired = true;
                _lastError = null;
                cancellationToken.ThrowIfCancellationRequested();
                // Ensure sing-box exists
                var singBoxPath = Path.Combine(_dataDir, SingBoxExe);
                if (!File.Exists(singBoxPath))
                {
                    _logger.Info("TUN", "sing-box not found, downloading...");
                    if (!await DownloadSingBoxAsync())
                    {
                        _logger.Error("TUN", "Failed to download sing-box");
                        return false;
                    }
                }

                // Generate config content first
                var configPath = Path.Combine(_dataDir, ConfigFile);
                var newJson = GenerateConfigJson(rulesConfig);
                var currentJson = File.Exists(configPath) ? File.ReadAllText(configPath) : null;

                var isProcessAlive = _isRunning && _singBoxProcess != null && !_singBoxProcess.HasExited;

                // Optimization: If config is same and process running, do nothing
                if (isProcessAlive && string.Equals(newJson, currentJson, StringComparison.Ordinal))
                {
                    _logger.Info("TUN", "Config unchanged, skipping restart.");
                    return true;
                }

                // The Wintun adapter is released asynchronously by Windows.  Do not
                // start a replacement until both the process and old adapter are gone.
                if (_isRunning || _singBoxProcess != null)
                    await StopProcessCoreAsync(raiseStatusChanged: false);

                await RemoveStaleTunAdapterAsync();

                cancellationToken.ThrowIfCancellationRequested();

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
                _singBoxProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.Debug("TUN", e.Data);
                };
                _singBoxProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        if (e.Data.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                            e.Data.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                            _lastError = e.Data;
                        _logger.Warning("TUN", e.Data);
                    }
                };

                _singBoxProcess.Start();
                _singBoxProcess.BeginOutputReadLine();
                _singBoxProcess.BeginErrorReadLine();

                // Wait a bit to check if it started successfully
                await Task.Delay(1500, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    await StopProcessCoreAsync(raiseStatusChanged: false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (_singBoxProcess.HasExited)
                {
                    _lastError ??= $"sing-box exited with code {_singBoxProcess.ExitCode}";
                    _logger.Error("TUN", _lastError);
                    return false;
                }

                _isRunning = true;
                _logger.Info("TUN", $"TUN mode started.");
                StatusChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) return false;
                _lastError = ex.Message;
                _logger.Error("TUN", $"Start failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (lockAcquired) _lifecycleLock.Release();
            }
        }

        /// <summary>
        /// Stop TUN mode
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning && (_singBoxProcess == null || _singBoxProcess.HasExited)) return;

            await _lifecycleLock.WaitAsync();
            try
            {
                await StopProcessCoreAsync(raiseStatusChanged: true);
                await RemoveStaleTunAdapterAsync();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public void Stop() => StopAsync().GetAwaiter().GetResult();

        private async Task StopProcessCoreAsync(bool raiseStatusChanged)
        {
            try
            {
                if (_singBoxProcess != null && !_singBoxProcess.HasExited)
                {
                    _singBoxProcess.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(ProcessStopTimeout);
                    try { await _singBoxProcess.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        _logger.Warning("TUN", "sing-box did not exit before timeout; continuing cleanup.");
                    }
                }
            }
            catch { }
            finally
            {
                _singBoxProcess?.Dispose();
                _singBoxProcess = null;
                _isRunning = false;
                _logger.Info("TUN", "TUN mode stopped");
                if (raiseStatusChanged) StatusChanged?.Invoke(false);
            }
        }

        private async Task RemoveStaleTunAdapterAsync()
        {
            try
            {
                // sing-box 1.8 creates a new Wintun adapter instead of reopening a
                // stopped one. Deleting only our named adapter prevents the next
                // start from failing with ERROR_FILE_EXISTS.
                var cleanup = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"interface delete interface name=\"{TunInterfaceName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var process = Process.Start(cleanup);
                if (process == null) return;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { _logger.Warning("TUN", "Timed out removing the old TUN adapter."); }
                // Give Windows a brief chance to release the Wintun device name.
                await Task.Delay(250);
            }
            catch (Exception ex)
            {
                _logger.Debug("TUN", $"TUN adapter cleanup skipped: {ex.Message}");
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
                foreach (var rule in rulesConfig.Rules)
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
                        else if (rule.Action == RuleAction.Proxy) outboundTag = "proxy-out";
                        else continue; // Direct action is default, so skip rule
                    }

                    var matchObject = new Dictionary<string, object> { { "outbound", outboundTag } };
                    bool hasMatch = false;

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
                        var domains = rule.TargetHosts.Where(h => h != "*").ToArray();
                        if (domains.Length > 0)
                        {
                            matchObject["domain_suffix"] = domains;
                            hasMatch = true;
                        }
                    }

                    if (hasMatch)
                    {
                        singboxRules.Add(matchObject);
                    }
                }
            }

            return singboxRules;
        }

        public class TunRulesConfig
        {
            public RuleMode Mode { get; set; }
            public List<TrafficRule> Rules { get; set; } = new();
            public ProxyType ProxyType { get; set; }
        }

        private string GenerateConfigJson(TunRulesConfig? rulesConfig = null)
        {
            if (rulesConfig == null) rulesConfig = new TunRulesConfig { Mode = RuleMode.BlackList, ProxyType = ProxyType.Socks5 };

            // Logic Split:
            // Scenario A (HTTP Proxy OR Whitelist Mode): Use sing-box routing.
            // Scenario B (SOCKS5 Proxy AND Blacklist Mode): Use TcpProxyService routing (All traffic -> proxy-out).

            bool useSingBoxRouting = (rulesConfig.ProxyType == ProxyType.Http || rulesConfig.Mode == RuleMode.WhiteList);

            string finalOutbound;
            // Windows sends DNS to the TUN peer (172.19.0.2). That traffic must
            // be consumed by sing-box before user rules, never sent "direct".
            var dnsRoutes = new List<object>
            {
                new { protocol = "dns", outbound = "dns-out" },
                new { port = 53, outbound = "dns-out" }
            };
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
                    // System rules for local/loopback must be added to prevent loops
                    routes.InsertRange(0, dnsRoutes);
                    routes.Insert(2, new { ip_cidr = new[] { "127.0.0.1/32", "0.0.0.0/32", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }, outbound = "direct" });
                    routes.Insert(3, new { port = new[] { 8000, 8080 }, outbound = "direct" });
                    routes.Insert(4, new { process_name = new[] { Process.GetCurrentProcess().ProcessName + ".exe", "ProxyControl.exe", "sing-box.exe" }, outbound = "direct" });
                }
                else
                {
                    // Whitelist Mode
                    finalOutbound = "direct";
                    routes = GenerateRouteRules(rulesConfig);
                    routes.InsertRange(0, dnsRoutes);
                }
            }
            else
            {
                // Scenario B: Default PROXY, TcpProxyService handles blocking/WebRTC
                finalOutbound = "proxy-out";
                routes = new List<object>();
                // We don't need app-specific routes here because everything goes to proxy-out
                // Exception: DNS still needs to be handled
                routes.AddRange(dnsRoutes);
                // We MUST exclude localhost/private from proxy-out to avoid loops, 
                // OR rely on TcpProxyService to handle it? 
                // Better to exclude essential system traffic here to be safe.
                routes.Add(new { ip_cidr = new[] { "127.0.0.1/32", "0.0.0.0/32", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }, outbound = "direct" });
                routes.Add(new { port = new[] { 8000, 8080 }, outbound = "direct" });
                routes.Add(new { process_name = new[] { Process.GetCurrentProcess().ProcessName + ".exe", "ProxyControl.exe", "sing-box.exe" }, outbound = "direct" });
            }

            var config = new
            {
                log = new { level = "info", timestamp = true },
                dns = new
                {
                    servers = new object[]
                    {
                        new { tag = "google", address = "8.8.8.8", detour = "direct" },
                        new { tag = "local", address = "local", detour = "direct" }
                    },
                    rules = new object[]
                    {
                        new { outbound = "any", server = "google" }
                    },
                    strategy = "ipv4_only"
                },
                inbounds = new object[]
                {
                    new
                    {
                        type = "tun",
                        tag = "tun-in",
                        interface_name = TunInterfaceName,
                        inet4_address = "172.19.0.1/30",
                        mtu = 1400,
                        auto_route = true,
                        strict_route = true,
                        stack = "mixed",
                        sniff = true,
                        sniff_override_destination = true
                    }
                },
                outbounds = CreateOutbounds(),
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

        private object[] CreateOutbounds()
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

            return new object[]
            {
                proxyOutbound,
                new { type = "direct", tag = "direct" },
                new { type = "block", tag = "block" },
                new { type = "dns", tag = "dns-out" }
            };
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
            _lifecycleLock.Dispose();
        }
    }
}
