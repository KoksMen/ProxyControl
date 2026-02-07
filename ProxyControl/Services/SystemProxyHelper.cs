using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Net;
using System.Text;
using System.Threading.Tasks;

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

        private static readonly string[] NetworkInterfaces = { "Wi-Fi", "Ethernet", "Ethernet 2", "Беспроводная сеть", "Подключение по локальной сети" };

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

                foreach (var iface in NetworkInterfaces)
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
                string dnsServer = "127.0.0.1";
                string source = "static";
                foreach (var iface in NetworkInterfaces)
                {
                    try
                    {
                        RunNetsh($"interface ip set dns name=\"{iface}\" source={source} addr={dnsServer}");
                    }
                    catch { }
                }
            }
            else
            {
                RestoreSystemDns();
            }
        }

        public static void RestoreSystemDns()
        {
            if (!IsAdministrator()) return;

            foreach (var iface in NetworkInterfaces)
            {
                try
                {
                    RunNetsh($"interface ip set dns name=\"{iface}\" source=dhcp");
                }
                catch { }
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