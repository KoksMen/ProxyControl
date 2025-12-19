using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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

        private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        private const string AppName = "ProxyManagerSafetyNet";

        public static void EnableSafetyNet()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, true))
                {
                    string command = "cmd /C reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 0 /f";
                    key?.SetValue(AppName, command);
                }
            }
            catch { }
        }

        public static void DisableSafetyNet()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, true))
                {
                    key?.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        public static void RestoreSystemProxy()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    if (key != null) key.SetValue("ProxyEnable", 0);
                }
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch { }
        }
    }
}
