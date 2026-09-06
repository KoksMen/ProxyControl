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
        private const string StartupApprovedRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string StartupApprovedRun32KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
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
                    ClearStartupApprovalState();
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
                if (HasEnabledRunEntry())
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

        public async Task<bool> SaveDnsAsync(AppConfig dnsConfig, CancellationToken token = default)
        {
            string tempPath = _filePath + ".tmp";
            await _saveLock.WaitAsync(token);
            try
            {
                AppSettings settings;
                if (File.Exists(_filePath))
                {
                    var existingJson = await File.ReadAllTextAsync(_filePath, token);
                    settings = JsonSerializer.Deserialize<AppSettings>(existingJson) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                settings.Config ??= new AppConfig();
                CopyDnsSettings(dnsConfig, settings.Config);

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(tempPath, json, token);
                File.Move(tempPath, _filePath, true);
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

        private static void CopyDnsSettings(AppConfig source, AppConfig destination)
        {
            destination.EnableDnsProtection = source.EnableDnsProtection;
            destination.DnsProvider = source.DnsProvider;
            destination.DnsHost = source.DnsHost;
            destination.DnsFallbackHost = source.DnsFallbackHost;
            destination.PreferPrimaryDns = source.PreferPrimaryDns;
            destination.EnableDoh = source.EnableDoh;
            destination.DohProvider = source.DohProvider;
            destination.AutoDetectDohEndpoint = source.AutoDetectDohEndpoint;
            destination.DohEndpoint = source.DohEndpoint;
            destination.EnableDohFallback = source.EnableDohFallback;
            destination.AutoDetectDohFallbackEndpoint = source.AutoDetectDohFallbackEndpoint;
            destination.DohFallbackEndpoint = source.DohFallbackEndpoint;
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
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                return processPath;
            }

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

        private static bool HasEnabledRunEntry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
                if (key?.GetValue(AppName) == null) return false;

                return !IsStartupEntryDisabled(StartupApprovedRunKeyPath) &&
                       !IsStartupEntryDisabled(StartupApprovedRun32KeyPath);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsStartupEntryDisabled(string keyPath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, false);
            if (key?.GetValue(AppName) is not byte[] state || state.Length == 0)
            {
                return false;
            }

            // Windows uses 0x03 as the disabled StartupApproved state and 0x02
            // as enabled. A missing value also means that the Run entry is active.
            return state[0] == 0x03;
        }

        private static void ClearStartupApprovalState()
        {
            ClearStartupApprovalState(StartupApprovedRunKeyPath);
            ClearStartupApprovalState(StartupApprovedRun32KeyPath);
        }

        private static void ClearStartupApprovalState(string keyPath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
                key?.DeleteValue(AppName, false);
            }
            catch
            {
                // The Run entry itself is still valid on Windows versions that do
                // not expose StartupApproved or restrict writes to this key.
            }
        }
    }
}
