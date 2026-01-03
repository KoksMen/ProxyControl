using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
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

        // --- Caching Variables ---
        private static readonly object _cacheLock = new object();
        private static Dictionary<int, int> _pidCache = new Dictionary<int, int>();
        private static DateTime _lastCacheUpdate = DateTime.MinValue;
        private const int CacheDurationMs = 250;
        // -------------------------

        public static int GetPidByPort(int port, bool forceRefresh = false)
        {
            if (!forceRefresh)
            {
                lock (_cacheLock)
                {
                    if ((DateTime.UtcNow - _lastCacheUpdate).TotalMilliseconds < CacheDurationMs)
                    {
                        // Если PID == 0, считаем что "нет данных", и не возвращаем его из кэша,
                        // чтобы дать шанс реальному обновлению (forceRefresh) найти процесс.
                        if (_pidCache.TryGetValue(port, out int cachedPid) && cachedPid > 0)
                        {
                            return cachedPid;
                        }
                    }
                }
            }

            return RefreshAndGetPid(port);
        }

        private static int RefreshAndGetPid(int port)
        {
            lock (_cacheLock)
            {
                if ((DateTime.UtcNow - _lastCacheUpdate).TotalMilliseconds < CacheDurationMs)
                {
                    // Double-check: если другой поток обновил кэш, и нашел валидный PID
                    if (_pidCache.TryGetValue(port, out int existingPid) && existingPid > 0) return existingPid;
                }

                int bufferSize = 0;
                // ipVersion: 2 = AF_INET (IPv4)
                GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, 2, 5, 0);

                IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
                var newCache = new Dictionary<int, int>();

                try
                {
                    if (GetExtendedTcpTable(tcpTablePtr, ref bufferSize, true, 2, 5, 0) == 0)
                    {
                        int rowCount = Marshal.ReadInt32(tcpTablePtr);
                        IntPtr rowPtr = tcpTablePtr + 4;

                        for (int i = 0; i < rowCount; i++)
                        {
                            // dwLocalPort at offset 8
                            int localPort = Marshal.ReadInt32(rowPtr + 8);
                            // Network byte order swap for port (16 bit relevant part)
                            localPort = ((localPort & 0xFF00) >> 8) | ((localPort & 0xFF) << 8);

                            // PID at offset 20
                            int pid = Marshal.ReadInt32(rowPtr + 20);

                            // Кэшируем даже 0, но GetPidByPort будет игнорировать 0 при чтении
                            newCache[localPort] = pid;

                            rowPtr += 24;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(tcpTablePtr);
                }

                _pidCache = newCache;
                _lastCacheUpdate = DateTime.UtcNow;

                return _pidCache.TryGetValue(port, out int resultPid) ? resultPid : 0;
            }
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