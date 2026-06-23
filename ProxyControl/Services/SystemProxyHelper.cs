using Microsoft.Win32;
using ProxyControl.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Net.NetworkInformation;

namespace ProxyControl.Services
{
    public static class SystemProxyHelper
    {
        [DllImport("wininet.dll")]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
        public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        public const int INTERNET_OPTION_REFRESH = 37;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);

        private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        private const string AppName = "ProxyManagerSafetyNet";
        private static readonly object ManagedDnsLock = new object();
        private static readonly HashSet<string> ManagedDnsServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        private static IEnumerable<string> GetActiveEthernetInterfaces()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                           (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                            n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                .Select(n => n.Name);
        }

        private static IEnumerable<int> GetActiveEthernetInterfaceIndexes()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                           (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                            n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                .Select(n =>
                {
                    try { return n.GetIPProperties().GetIPv4Properties()?.Index ?? 0; }
                    catch { return 0; }
                })
                .Where(index => index > 0);
        }

        private static readonly object _cacheLock = new object();
        private static Dictionary<int, int> _pidCache = new Dictionary<int, int>();
        private static DateTime _lastCacheUpdate = DateTime.MinValue;
        private const int CacheDurationMs = 500;

        // Task coalescing: If multiple threads request refresh, they await the SAME task.
        private static Task<Dictionary<int, int>>? _refreshTask;

        public static int GetPidByPort(int port, bool forceRefresh = false)
        {
            Dictionary<int, int>? cacheSnapshot = null;

            lock (_cacheLock)
            {
                // Return immediately if cache is fresh enough and contains the key (unless force refresh)
                if (!forceRefresh && (DateTime.UtcNow - _lastCacheUpdate).TotalMilliseconds < CacheDurationMs)
                {
                    if (_pidCache.TryGetValue(port, out int cachedPid) && cachedPid > 0)
                    {
                        return cachedPid;
                    }
                }

                // Get or start the refresh task
                if (_refreshTask == null || _refreshTask.IsCompleted)
                {
                    _refreshTask = Task.Run(() => BuildTcpTableSnapshot());
                }
            }

            try
            {
                // Await the shared refresh task
                cacheSnapshot = _refreshTask.GetAwaiter().GetResult();
            }
            catch
            {
                return 0;
            }

            lock (_cacheLock)
            {
                _pidCache = cacheSnapshot;
                _lastCacheUpdate = DateTime.UtcNow;
                return _pidCache.TryGetValue(port, out int pid) ? pid : 0;
            }
        }

        public static int GetPidByDestAddress(IPAddress destIp, int destPort)
        {
            // Note: We don't cache this as it changes frequently per connection attempt
            return GetPidRelative(destIp, destPort);
        }

        private static int GetPidRelative(IPAddress destIp, int destPort)
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, 2, 5, 0);

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);

            try
            {
                if (GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, 2, 5, 0) == 0)
                {
                    int rowCount = Marshal.ReadInt32(tcpTablePtr);
                    IntPtr rowPtr = tcpTablePtr + 4;

                    byte[] targetIpBytes = destIp.GetAddressBytes();
                    int targetIpInt = BitConverter.ToInt32(targetIpBytes, 0);

                    for (int i = 0; i < rowCount; i++)
                    {
                        // dwRemoteAddr at offset 12
                        int remoteAddr = Marshal.ReadInt32(rowPtr + 12);

                        // dwRemotePort at offset 16
                        int remotePort = Marshal.ReadInt32(rowPtr + 16);
                        remotePort = ((remotePort & 0xFF00) >> 8) | ((remotePort & 0xFF) << 8);

                        if (remoteAddr == targetIpInt && remotePort == destPort)
                        {
                            // dwState at offset 0
                            int state = Marshal.ReadInt32(rowPtr);
                            // MIB_TCP_STATE_SYN_SENT = 3, ESTABLISHED = 5
                            // We might catch it in SYN_SENT in TUN mode

                            // PID at offset 20
                            return Marshal.ReadInt32(rowPtr + 20);
                        }

                        rowPtr += 24;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }
            return 0;
        }

        private static Dictionary<int, int> BuildTcpTableSnapshot()
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, 2, 5, 0);

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
            var newCache = new Dictionary<int, int>(100);

            try
            {
                if (GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, 2, 5, 0) == 0)
                {
                    int rowCount = Marshal.ReadInt32(tcpTablePtr);
                    IntPtr rowPtr = tcpTablePtr + 4;

                    for (int i = 0; i < rowCount; i++)
                    {
                        // dwLocalPort at offset 8
                        int localPort = Marshal.ReadInt32(rowPtr + 8);
                        localPort = ((localPort & 0xFF00) >> 8) | ((localPort & 0xFF) << 8);

                        // PID at offset 20
                        int pid = Marshal.ReadInt32(rowPtr + 20);

                        newCache[localPort] = pid;

                        rowPtr += 24;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }
            return newCache;
        }

        public static void SetSystemProxy(bool enable, string host, int port)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    if (enable)
                    {
                        key.SetValue("ProxyEnable", 1);
                        key.SetValue("ProxyServer", $"{host}:{port}");
                        key.SetValue("ProxyOverride", "<local>");
                    }
                    else
                    {
                        key.SetValue("ProxyEnable", 0);
                    }
                }
                RefreshSettings();
            }
            catch { }
        }

        public static void EnforceSystemProxy(string host, int port)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    int? enabled = key?.GetValue("ProxyEnable") as int?;
                    if (enabled == null || enabled == 0)
                    {
                        key.SetValue("ProxyEnable", 1);
                        key.SetValue("ProxyServer", $"{host}:{port}");
                        key.SetValue("ProxyOverride", "<local>");
                        RefreshSettings();
                    }
                }
            }
            catch { }
        }

        public static void RestoreSystemProxy()
        {
            SetSystemProxy(false, "", 0);
        }

        private static void RefreshSettings()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }

        public static void EnableSafetyNet()
        {
            try
            {
                StringBuilder cmdBuilder = new StringBuilder();
                cmdBuilder.Append("cmd /C \"reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 0 /f");

                foreach (var iface in GetActiveEthernetInterfaces())
                {
                    cmdBuilder.Append($" & netsh interface ip set dns name=\\\"{iface}\\\" source=dhcp");
                }

                cmdBuilder.Append("\"");

                using (var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, true))
                {
                    key?.SetValue(AppName, cmdBuilder.ToString());
                }
            }
            catch
            {
            }
        }

        public static void DisableSafetyNet()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, true))
                {
                    if (key?.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch { }
        }

        public static void SetSystemDns(bool useLocalProxy)
        {
            if (!IsAdministrator()) return;

            if (useLocalProxy)
            {
                SetSystemDnsServers(new[] { "127.0.0.1" }, markManaged: false);
            }
            else
            {
                RestoreSystemDns();
            }
        }

        public static void SetSystemDns(AppConfig config)
        {
            // DoH support is disabled; keep this overload so existing callers do not break.
            SetSystemDns(true);
        }

        private static void SetSystemDnsServers(IReadOnlyList<string> dnsServers, bool markManaged)
        {
            if (dnsServers.Count == 0) return;

            var interfaceIndexes = GetActiveEthernetInterfaceIndexes().ToArray();
            if (interfaceIndexes.Length == 0) return;

            var indexArray = string.Join(", ", interfaceIndexes);
            var dnsArray = ToPowerShellStringArray(dnsServers);
            RunPowerShell(
                "$indexes = @(" + indexArray + "); " +
                "$servers = @(" + dnsArray + "); " +
                "foreach ($index in $indexes) { " +
                "Set-DnsClientServerAddress -InterfaceIndex $index -ServerAddresses $servers -ErrorAction SilentlyContinue | Out-Null " +
                "}");

            if (markManaged)
            {
                lock (ManagedDnsLock)
                {
                    foreach (var dnsServer in dnsServers)
                    {
                        ManagedDnsServers.Add(dnsServer);
                    }
                }
            }
        }

        private static void ConfigureWindowsDohServer(string serverAddress, string dohTemplate, bool enableDoh, bool isAutoDetected, bool shouldApplyTemplate)
        {
            var address = EscapePowerShellSingleQuoted(serverAddress);
            var template = EscapePowerShellSingleQuoted(dohTemplate ?? string.Empty);

            if (!enableDoh)
            {
                RunPowerShell(
                    "$existing = Get-DnsClientDohServerAddress -ServerAddress '" + address + "' -ErrorAction SilentlyContinue; " +
                    "if ($existing) { Set-DnsClientDohServerAddress -ServerAddress '" + address + "' -AutoUpgrade $false -ErrorAction SilentlyContinue | Out-Null }");
                return;
            }

            if (isAutoDetected && !shouldApplyTemplate)
            {
                RunPowerShell(
                    "$existing = Get-DnsClientDohServerAddress -ServerAddress '" + address + "' -ErrorAction SilentlyContinue; " +
                    "if ($existing) { Set-DnsClientDohServerAddress -ServerAddress '" + address + "' -AllowFallbackToUdp $false -AutoUpgrade $true -ErrorAction SilentlyContinue | Out-Null }");
                return;
            }

            if (shouldApplyTemplate)
            {
                RunPowerShell(
                    "$existing = Get-DnsClientDohServerAddress -ServerAddress '" + address + "' -ErrorAction SilentlyContinue; " +
                    "if ($existing) { Remove-DnsClientDohServerAddress -ServerAddress '" + address + "' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null }; " +
                    "Add-DnsClientDohServerAddress -ServerAddress '" + address + "' -DohTemplate '" + template + "' -AllowFallbackToUdp $false -AutoUpgrade $true -ErrorAction SilentlyContinue | Out-Null");
                return;
            }

            var templateAssignment = string.IsNullOrWhiteSpace(template)
                ? string.Empty
                : " -DohTemplate '" + template + "'";

            RunPowerShell(
                "$existing = Get-DnsClientDohServerAddress -ServerAddress '" + address + "' -ErrorAction SilentlyContinue; " +
                "if ($existing) { Set-DnsClientDohServerAddress -ServerAddress '" + address + "'" + templateAssignment + " -AllowFallbackToUdp $false -AutoUpgrade $true -ErrorAction SilentlyContinue | Out-Null } " +
                "else { Add-DnsClientDohServerAddress -ServerAddress '" + address + "' -DohTemplate '" + template + "' -AllowFallbackToUdp $false -AutoUpgrade $true -ErrorAction SilentlyContinue | Out-Null }");
        }

        public static bool ValidateWindowsDohSettings(AppConfig config, out string message)
        {
            message = string.Empty;
            // DoH support is disabled; validation succeeds because there is nothing to apply.
            return true;
        }

        private static bool AreDnsServersApplied(IReadOnlyList<string> expectedServers)
        {
            var expected = expectedServers
                .Select(x => IPAddress.TryParse(x, out var ip) ? ip.ToString() : x)
                .ToArray();

            foreach (var iface in GetActiveEthernetInterfaces())
            {
                try
                {
                    var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => n.Name == iface);
                    if (networkInterface == null) continue;

                    var current = networkInterface.GetIPProperties().DnsAddresses
                        .Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(x => x.ToString())
                        .ToArray();

                    if (expected.All(x => current.Contains(x, StringComparer.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private static bool TryReadWindowsDohState(string serverAddress, out string dohTemplate, out bool autoUpgrade, out bool allowFallback)
        {
            dohTemplate = string.Empty;
            autoUpgrade = false;
            allowFallback = true;

            try
            {
                var address = EscapePowerShellSingleQuoted(serverAddress);
                var output = RunPowerShellWithOutput(
                    "$item = Get-DnsClientDohServerAddress -ServerAddress '" + address + "' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                    "if ($item) { \"$($item.DohTemplate)`t$($item.AutoUpgrade)`t$($item.AllowFallbackToUdp)\" }");

                if (string.IsNullOrWhiteSpace(output))
                {
                    return false;
                }

                var parts = output.Trim().Split('\t');
                if (parts.Length < 3)
                {
                    return false;
                }

                dohTemplate = parts[0].Trim();
                autoUpgrade = bool.TryParse(parts[1].Trim(), out var parsedAuto) && parsedAuto;
                allowFallback = bool.TryParse(parts[2].Trim(), out var parsedFallback) && parsedFallback;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeDohTemplate(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('/');
        }

        public static void RestoreSystemDnsIfManagedByProxyControl()
        {
            if (!IsAdministrator()) return;

            foreach (var iface in GetActiveEthernetInterfaces())
            {
                try
                {
                    var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => n.Name == iface);
                    if (networkInterface == null) continue;

                    var dnsAddresses = networkInterface.GetIPProperties().DnsAddresses;

                    if (dnsAddresses?.Any(IsProxyControlDnsAddress) == true)
                    {
                        RunNetsh($"interface ip set dns name=\"{iface}\" source=dhcp");
                    }
                }
                catch { }
            }
        }

        public static void RestoreSystemDns()
        {
            if (!IsAdministrator()) return;

            foreach (var iface in GetActiveEthernetInterfaces())
            {
                try
                {
                    RunNetsh($"interface ip set dns name=\"{iface}\" source=dhcp");
                }
                catch { }
            }
        }

        private static bool IsProxyControlDnsAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            lock (ManagedDnsLock)
            {
                return ManagedDnsServers.Contains(address.ToString());
            }
        }

        private static void RunNetsh(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", arguments)
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch { }
        }

        private static void RunPowerShell(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
                using var process = Process.Start(psi);
                process?.WaitForExit(5000);
            }
            catch { }
        }

        private static string RunPowerShellWithOutput(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return string.Empty;
                }

                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    return string.Empty;
                }

                return process.StandardOutput.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string EscapePowerShellSingleQuoted(string value)
        {
            return value.Replace("'", "''");
        }

        private static string ToPowerShellStringArray(IEnumerable<string> values)
        {
            return string.Join(", ", values.Select(x => "'" + EscapePowerShellSingleQuoted(x) + "'"));
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void RestartAsAdmin()
        {
            try
            {
                var processInfo = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(processInfo);
            }
            catch { }
        }
    }
}
