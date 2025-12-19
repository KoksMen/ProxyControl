using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace ProxyControl.Services
{
    public class GithubUpdateService
    {
        private const string RepoOwner = "KoksMen";
        private const string RepoName = "ProxyControl";

        public async Task CheckAndInstallUpdate()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Proxy Control", "1.0.0"));

                    string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                    var response = await client.GetStringAsync(url);

                    using (JsonDocument doc = JsonDocument.Parse(response))
                    {
                        var root = doc.RootElement;
                        string tagName = root.GetProperty("tag_name").GetString();

                        Version latestVersion = ParseVersion(tagName);
                        Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;

                        if (latestVersion > currentVersion)
                        {
                            var assets = root.GetProperty("assets");
                            if (assets.GetArrayLength() > 0)
                            {
                                string downloadUrl = assets[0].GetProperty("browser_download_url").GetString();

                                if (MessageBox.Show($"New version {tagName} is available!\nUpdate now?", "Update Found", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                                {
                                    await PerformUpdate(downloadUrl);
                                }
                            }
                        }
                        else
                        {
                            MessageBox.Show("You are using the latest version.", "Check for Updates");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update check failed: {ex.Message}", "Error");
            }
        }

        private Version ParseVersion(string tagName)
        {
            try
            {
                string cleanVer = tagName.TrimStart('v', 'V');
                return new Version(cleanVer);
            }
            catch { return new Version(1, 0, 0); }
        }

        private async Task PerformUpdate(string url)
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string tempExe = Path.Combine(Path.GetDirectoryName(currentExe), "update_temp.exe");

                using (var client = new HttpClient())
                {
                    var bytes = await client.GetByteArrayAsync(url);
                    File.WriteAllBytes(tempExe, bytes);
                }

                string batPath = Path.Combine(Path.GetDirectoryName(currentExe), "updater.bat");
                string batContent = $@"
@echo off
timeout /t 2 /nobreak
del ""{currentExe}""
move ""{tempExe}"" ""{currentExe}""
start """" ""{currentExe}""
del ""%~f0""
";
                File.WriteAllText(batPath, batContent);

                var startInfo = new ProcessStartInfo
                {
                    FileName = batPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                MainWindow.AllowClose = true;
                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update process failed: {ex.Message}");
            }
        }
    }
}
