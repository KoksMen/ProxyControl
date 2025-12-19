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
        private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public SettingsService()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyManagerApp");

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

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

        public void SetAutoStart(bool enable)
        {
            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true))
                {
                    if (enable)
                    {
                        key.SetValue(AppName, $"\"{exePath}\" --autostart");
                    }
                    else
                    {
                        if (key.GetValue(AppName) != null)
                            key.DeleteValue(AppName, false);
                    }
                }
            }
            catch { }
        }

        public bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false))
                {
                    return key?.GetValue(AppName) != null;
                }
            }
            catch { return false; }
        }
    }
}
