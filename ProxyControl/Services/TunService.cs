using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyControl.Services
{
    /// <summary>
    /// TUN-based VPN service using sing-box for full UDP/WebRTC capture.
    /// Routes ALL system traffic through the selected proxy.
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

        public bool IsRunning => _isRunning;
        public event Action<bool>? StatusChanged;

        public TunService()
        {
            _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyControl", "tun");
            Directory.CreateDirectory(_dataDir);
            _logger = AppLoggerService.Instance;
        }

        /// <summary>
        /// Start TUN mode - routes all traffic through the specified SOCKS5 proxy
        /// </summary>
        public async Task<bool> StartAsync(string proxyHost, int proxyPort, IEnumerable<string> bypassItems, string? username = null, string? password = null)
        {
            if (_isRunning) return true;

            try
            {
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

                // Generate config
                var configPath = Path.Combine(_dataDir, ConfigFile);
                GenerateConfig(configPath, proxyHost, proxyPort, bypassItems, username, password);

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
                        _logger.Warning("TUN", e.Data);
                };

                _singBoxProcess.Start();
                _singBoxProcess.BeginOutputReadLine();
                _singBoxProcess.BeginErrorReadLine();

                // Wait a bit to check if it started successfully
                await Task.Delay(1500);

                if (_singBoxProcess.HasExited)
                {
                    _logger.Error("TUN", $"sing-box exited with code {_singBoxProcess.ExitCode}");
                    return false;
                }

                _isRunning = true;
                _logger.Info("TUN", $"TUN mode started → {proxyHost}:{proxyPort}");
                StatusChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("TUN", $"Start failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stop TUN mode
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

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
                _isRunning = false;
                _logger.Info("TUN", "TUN mode stopped");
                StatusChanged?.Invoke(false);
            }
        }

        private void GenerateConfig(string path, string proxyHost, int proxyPort, IEnumerable<string> bypassItems, string? username, string? password)
        {
            // Prepare bypass lists
            var bypassIps = new List<string>();
            var bypassDomains = new List<string>();

            // Always bypass the local proxy host itself
            if (IPAddress.TryParse(proxyHost, out _)) bypassIps.Add($"{proxyHost}/32");
            else bypassDomains.Add(proxyHost);

            // Add other bypass items (upstream proxies)
            if (bypassItems != null)
            {
                foreach (var item in bypassItems)
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    if (IPAddress.TryParse(item, out _)) bypassIps.Add($"{item}/32");
                    else bypassDomains.Add(item);
                }
            }

            // Exclude current process
            var currentProcess = Process.GetCurrentProcess().ProcessName + ".exe";

            var rules = new List<object>
            {
                new { protocol = "dns", outbound = "dns-out" },
                // Direct for local networks
                new { ip_is_private = true, outbound = "direct" },
                // Bypass ProxyControl local ports
                new { port = new[] { 8000, 8080, 1080 }, outbound = "direct" },
                // Bypass process
                new { process_name = new[] { currentProcess, "ProxyControl.exe" }, outbound = "direct" }
            };

            if (bypassIps.Any()) rules.Add(new { ip_cidr = bypassIps.Distinct(), outbound = "direct" });
            if (bypassDomains.Any()) rules.Add(new { domain = bypassDomains.Distinct(), outbound = "direct" });

            var config = new
            {
                log = new { level = "info", timestamp = true },
                dns = new
                {
                    servers = new object[]
                    {
                        new { tag = "google", address = "8.8.8.8", detour = "direct" },
                        new { tag = "local", address = "223.5.5.5", detour = "direct" }
                    },
                    rules = new object[]
                    {
                        new { outbound = "any", server = "google" } // Use google (direct) for everything to ensure resolution
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
                        strict_route = false,
                        stack = "gvisor",
                        sniff = true,
                        sniff_override_destination = false,
                        endpoint_independent_nat = true
                    }
                },
                outbounds = CreateOutbounds(proxyHost, proxyPort, username, password),
                route = new
                {
                    auto_detect_interface = true,
                    final = "proxy-out",
                    rules = rules.ToArray()
                }
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            // Fix final_ to final (C# keyword escape) should not be needed if using anonymous object with 'final' property name if possible, 
            // but since 'final' is not keyword in C# context of anon types (only reserved in Java/others), we can use it.
            // Wait, 'final' IS NOT a keyword in C#, but previously I used 'final_'. I'll stick to 'final' in anonymous type if compiler allows, 
            // otherwise 'final_' and replace. 
            // C# allows properties named 'final'.

            File.WriteAllText(path, json);
            _logger.Info("TUN", $"Config generated for {proxyHost}:{proxyPort}");
        }

        private object[] CreateOutbounds(string proxyHost, int proxyPort, string? username, string? password)
        {
            object proxyOutbound;
            if (string.IsNullOrEmpty(username))
            {
                proxyOutbound = new
                {
                    type = "socks",
                    tag = "proxy-out",
                    server = proxyHost,
                    server_port = proxyPort,
                    version = "5"
                };
            }
            else
            {
                proxyOutbound = new
                {
                    type = "socks",
                    tag = "proxy-out",
                    server = proxyHost,
                    server_port = proxyPort,
                    version = "5",
                    username = username,
                    password = password
                };
            }

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
        }
    }
}
