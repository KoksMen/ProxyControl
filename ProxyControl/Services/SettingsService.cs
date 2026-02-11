using Microsoft.Win32;
using ProxyControl.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ProxyControl.Services
{
    public class SettingsService
    {
        private readonly string _filePath;
        private const string AppName = "ProxyManagerApp";
        private const string TaskName = "ProxyManagerApp";
        private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public SettingsService()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyManagerApp");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "settings.json");
        }

        public void Save(AppSettings settings, string? path = null)
        {
            try
            {
                string targetPath = path ?? _filePath;
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(targetPath, json);
            }
            catch { }
        }

        public AppSettings Load(string? path = null)
        {
            string targetPath = path ?? _filePath;
            if (!File.Exists(targetPath)) return new AppSettings();
            try
            {
                var json = File.ReadAllText(targetPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch { return new AppSettings(); }
        }

        private bool? _autoStartCached = null;

        public void SetAutoStart(bool enable)
        {
            try
            {
                // Clean up old registry Run key (migration from old approach)
                CleanupOldRegistryAutoStart();

                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                if (enable)
                {
                    // Create a scheduled task that runs at user logon with highest privileges
                    // This works with requireAdministrator manifest, unlike HKCU\Run
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --autostart\" /SC ONLOGON /RL HIGHEST /F",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                    };
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        proc?.WaitForExit(5000);
                    }
                }
                else
                {
                    // Delete the scheduled task
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Delete /TN \"{TaskName}\" /F",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                    };
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        proc?.WaitForExit(5000);
                    }
                }
                // Update cache after successful change
                _autoStartCached = enable;
            }
            catch { }
        }

        public bool IsAutoStartEnabled()
        {
            // Return cached value if available (avoids spawning schtasks.exe on every save)
            if (_autoStartCached.HasValue) return _autoStartCached.Value;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{TaskName}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    proc?.WaitForExit(3000);
                    _autoStartCached = proc?.ExitCode == 0;
                    return _autoStartCached.Value;
                }
            }
            catch { _autoStartCached = false; return false; }
        }

        private void CleanupOldRegistryAutoStart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true))
                {
                    if (key?.GetValue(AppName) != null)
                        key.DeleteValue(AppName, false);
                }
            }
            catch { }
        }
    }
}
