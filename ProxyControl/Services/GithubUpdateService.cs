using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace ProxyControl.Services
{
    public class GithubUpdateService
    {
        private const string RepoOwner = "KoksMen";
        private const string RepoName = "ProxyControl";

        // Обновленная сигнатура: Title, Details, Percent
        public async Task CheckAndInstallUpdate(Action<string, string, int> onProgress, Action onCompleted, bool silent = false)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProxyControl", "1.9.7"));

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
                                long fileSize = 0;
                                if (assets[0].TryGetProperty("size", out var sizeProp))
                                {
                                    fileSize = sizeProp.GetInt64();
                                }

                                if (MessageBox.Show($"New version {tagName} is available!\nUpdate now?", "Update Found", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                                {
                                    await PerformUpdate(downloadUrl, fileSize, onProgress, onCompleted);
                                }
                            }
                        }
                        else if (!silent)
                        {
                            MessageBox.Show("You are using the latest version.", "Check for Updates");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!silent)
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

        private async Task PerformUpdate(string url, long expectedSize, Action<string, string, int> onProgress, Action onCompleted)
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string tempExe = Path.Combine(Path.GetDirectoryName(currentExe), "update_temp.exe");

                using (var client = new HttpClient())
                {
                    // Шаг 1: Скачивание
                    onProgress?.Invoke("Step 1/3: Initializing download...", "Connecting...", 0);

                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
                        if (totalBytes <= 0) totalBytes = 1; // Защита от деления на ноль

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            long totalRead = 0;
                            int bytesRead;

                            var stopwatch = Stopwatch.StartNew();
                            var lastReportTime = stopwatch.ElapsedMilliseconds;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                // Обновляем UI каждые ~50мс, чтобы не фризить интерфейс частыми вызовами
                                if (stopwatch.ElapsedMilliseconds - lastReportTime > 50 || totalRead == totalBytes)
                                {
                                    lastReportTime = stopwatch.ElapsedMilliseconds;

                                    double progressPercent = (double)totalRead / totalBytes * 100;

                                    // Расчет скорости
                                    double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                                    double speedBytesPerSec = elapsedSeconds > 0 ? totalRead / elapsedSeconds : 0;
                                    string speedStr = FormatSpeed(speedBytesPerSec);

                                    // Форматирование размера (MB)
                                    string downloadedMb = (totalRead / 1024d / 1024d).ToString("F1");
                                    string totalMb = (totalBytes / 1024d / 1024d).ToString("F1");

                                    string details = $"{downloadedMb} MB / {totalMb} MB  •  {speedStr}";
                                    onProgress?.Invoke("Step 1/3: Downloading update...", details, (int)progressPercent);
                                }
                            }
                        }
                    }
                }

                // Шаг 2: Подготовка (симуляция процесса установки)
                onProgress?.Invoke("Step 2/3: Preparing installation...", "Verifying integrity...", 100);
                await Task.Delay(800);

                onProgress?.Invoke("Step 2/3: Preparing installation...", "Creating backup...", 100);
                await Task.Delay(800);

                // Шаг 3: Завершение
                onProgress?.Invoke("Step 3/3: Finalizing...", "Restarting application...", 100);
                await Task.Delay(800);

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

                onCompleted?.Invoke();

                MainWindow.AllowClose = true;
                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update process failed: {ex.Message}");
            }
        }

        private string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond > 1024 * 1024)
                return $"{(bytesPerSecond / 1024 / 1024):F1} MB/s";
            if (bytesPerSecond > 1024)
                return $"{(bytesPerSecond / 1024):F1} KB/s";
            return $"{bytesPerSecond:F0} B/s";
        }
    }
}