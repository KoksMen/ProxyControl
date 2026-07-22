using Microsoft.Win32;
using ProxyControl.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;

namespace ProxyControl.Services
{
    public class SettingsService
    {
        private readonly string _filePath;
        private const string AppName = "ProxyManagerApp";
        private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ScheduledTaskName = "ProxyControl Autostart";
        private readonly SemaphoreSlim _saveLock = new(1, 1);

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

        public void SetAutoStart(bool enable)
        {
            try
            {
                if (enable)
                {
                    // HKCU Run works for standard users and does not depend on Task
                    // Scheduler elevation, locale, or task-service policy.
                    using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, true);
                    key?.SetValue(AppName, BuildAutoStartCommand(GetExecutablePath()), RegistryValueKind.String);
                    RunSchtasks(BuildAutoStartTaskDeleteArguments());
                }
                else
                {
                    RemoveLegacyRunEntry();
                    RunSchtasks(BuildAutoStartTaskDeleteArguments());
                }
            }
            catch { }
        }

        public bool IsAutoStartEnabled()
        {
            try
            {
                if (HasLegacyRunEntry())
                {
                    return true;
                }

                return RunSchtasks($"/Query /TN \"{ScheduledTaskName}\"").ExitCode == 0;
            }
            catch { return false; }
        }

        public static string BuildAutoStartTaskCreateArguments(string exePath)
        {
            var taskRun = $"\\\"{exePath}\\\" --autostart";
            return $"/Create /F /SC ONLOGON /RL HIGHEST /TN \"{ScheduledTaskName}\" /TR \"{taskRun}\"";
        }

        public async Task<bool> SaveAsync(AppSettings settings, CancellationToken token = default, string? path = null)
        {
            string targetPath = path ?? _filePath;
            string tempPath = targetPath + ".tmp";
            await _saveLock.WaitAsync(token);
            try
            {
                var json = await Task.Run(
                    () => JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }),
                    token);
                await File.WriteAllTextAsync(tempPath, json, token);
                File.Move(tempPath, targetPath, true);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                _saveLock.Release();
            }
        }

        public static string BuildAutoStartCommand(string exePath)
        {
            return $"\"{exePath}\" --autostart";
        }

        public static string BuildAutoStartTaskDeleteArguments()
        {
            return $"/Delete /F /TN \"{ScheduledTaskName}\"";
        }

        private static string GetExecutablePath()
        {
            var entryLocation = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(entryLocation))
            {
                var exeCandidate = Path.ChangeExtension(entryLocation, ".exe");
                if (File.Exists(exeCandidate))
                {
                    return exeCandidate;
                }
            }

            return Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;
        }

        private static (int ExitCode, string Output) RunSchtasks(string arguments)
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return (-1, string.Empty);
            }

            process.WaitForExit(5000);
            return (process.ExitCode, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
        }

        private static void RemoveLegacyRunEntry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                if (key?.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        private static bool HasLegacyRunEntry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
                return key?.GetValue(AppName) != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
