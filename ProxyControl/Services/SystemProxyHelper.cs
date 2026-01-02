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

        // Список интерфейсов для сброса DNS
        private static readonly string[] NetworkInterfaces = { "Wi-Fi", "Ethernet", "Ethernet 2", "Беспроводная сеть", "Подключение по локальной сети" };

        public static int GetPidByPort(int port)
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, 2, 5, 0);

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetExtendedTcpTable(tcpTablePtr, ref bufferSize, true, 2, 5, 0) == 0)
                {
                    int rowCount = Marshal.ReadInt32(tcpTablePtr);
                    IntPtr rowPtr = tcpTablePtr + 4;

                    for (int i = 0; i < rowCount; i++)
                    {
                        int localPort = Marshal.ReadInt32(rowPtr + 8);
                        localPort = ((localPort & 0xFF00) >> 8) | ((localPort & 0xFF) << 8);

                        if (localPort == port)
                        {
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
                // Формируем команду, которая сбросит прокси И сбросит DNS для основных интерфейсов
                // Это сработает при следующей загрузке Windows, если приложение упало и не удалило этот ключ.
                StringBuilder cmdBuilder = new StringBuilder();
                cmdBuilder.Append("cmd /C \"reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 0 /f");

                // Добавляем команды сброса DNS для каждого интерфейса
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
                // Установка статического DNS (127.0.0.1)
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
                // Сброс на DHCP
                RestoreSystemDns();
            }
        }

        // Метод для явного сброса DNS
        public static void RestoreSystemDns()
        {
            if (!IsAdministrator()) return;

            foreach (var iface in NetworkInterfaces)
            {
                try
                {
                    // Команда source=dhcp НЕ должна содержать addr=...
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