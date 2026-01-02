using ProxyControl.Helpers;
using ProxyControl.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ProxyControl.Services
{
    public class TrafficMonitorService
    {
        // Для Live режима
        private readonly ConcurrentDictionary<string, ProcessTrafficData> _liveProcessStats
            = new ConcurrentDictionary<string, ProcessTrafficData>();

        // Основная коллекция для биндинга (может содержать Live или History данные)
        public ObservableCollection<ProcessTrafficData> DisplayedProcessList { get; private set; }
            = new ObservableCollection<ProcessTrafficData>();

        private Timer _speedTimer;
        private readonly string _logsPath;
        private readonly object _logLock = new object();

        // Флаг режима: true = показываем реальное время, false = историю
        public bool IsLiveMode { get; set; } = true;

        public TrafficMonitorService()
        {
            _logsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TrafficLogs");
            if (!Directory.Exists(_logsPath)) Directory.CreateDirectory(_logsPath);

            _speedTimer = new Timer(UpdateSpeeds, null, 1000, 1000);
        }

        // --- Live Logic ---

        public ProcessTrafficData GetOrAddLiveProcess(string processName, ImageSource? icon)
        {
            return _liveProcessStats.GetOrAdd(processName, name =>
            {
                var newData = new ProcessTrafficData { ProcessName = name, Icon = icon };

                // Если мы в Live режиме, добавляем сразу в список отображения
                if (IsLiveMode)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (!DisplayedProcessList.Any(p => p.ProcessName == name))
                            DisplayedProcessList.Add(newData);
                    });
                }
                return newData;
            });
        }

        public void AddLiveTraffic(string processName, long bytes, bool isDownload)
        {
            if (_liveProcessStats.TryGetValue(processName, out var stats))
            {
                if (isDownload)
                {
                    Interlocked.Add(ref stats.BytesDownLastSecond, bytes);
                    stats.TotalDownload += bytes;
                }
                else
                {
                    Interlocked.Add(ref stats.BytesUpLastSecond, bytes);
                    stats.TotalUpload += bytes;
                }
            }
        }

        public ConnectionHistoryItem CreateConnectionItem(string processName, ImageSource? icon, string host, string status, string details, string? flagUrl, string color)
        {
            var item = new ConnectionHistoryItem
            {
                Timestamp = DateTime.Now,
                ProcessName = processName,
                Host = host,
                Status = status,
                Details = details,
                FlagUrl = flagUrl,
                Color = color
            };

            var stats = GetOrAddLiveProcess(processName, icon);

            Application.Current.Dispatcher.Invoke(() =>
            {
                // Добавляем в Live список соединений
                stats.Connections.Insert(0, item);
                if (stats.Connections.Count > 200) stats.Connections.RemoveAt(stats.Connections.Count - 1);
            });

            return item;
        }

        public void CompleteConnection(ConnectionHistoryItem item)
        {
            // Сохраняем завершенное соединение в лог файл
            Task.Run(() => AppendLog(item));
        }

        private void UpdateSpeeds(object? state)
        {
            // Обновляем скорость только для Live статистики
            foreach (var kvp in _liveProcessStats)
            {
                var stats = kvp.Value;
                long down = Interlocked.Exchange(ref stats.BytesDownLastSecond, 0);
                long up = Interlocked.Exchange(ref stats.BytesUpLastSecond, 0);

                if (stats.CurrentDownloadSpeed != down) stats.CurrentDownloadSpeed = down;
                if (stats.CurrentUploadSpeed != up) stats.CurrentUploadSpeed = up;
            }
        }

        // --- Persistence Logic ---

        private void AppendLog(ConnectionHistoryItem item)
        {
            try
            {
                string fileName = $"log_{item.Timestamp:yyyy-MM-dd}.jsonl";
                string fullPath = Path.Combine(_logsPath, fileName);
                string json = JsonSerializer.Serialize(item);

                lock (_logLock)
                {
                    File.AppendAllText(fullPath, json + Environment.NewLine);
                }
            }
            catch { }
        }

        public async Task LoadHistoryAsync(DateTime start, DateTime end, TimeSpan? startTime = null, TimeSpan? endTime = null)
        {
            IsLiveMode = false;

            var resultDict = new Dictionary<string, ProcessTrafficData>();

            await Task.Run(() =>
            {
                var current = start.Date;
                var endDate = end.Date;

                while (current <= endDate)
                {
                    string fileName = $"log_{current:yyyy-MM-dd}.jsonl";
                    string fullPath = Path.Combine(_logsPath, fileName);

                    if (File.Exists(fullPath))
                    {
                        var lines = File.ReadLines(fullPath);
                        foreach (var line in lines)
                        {
                            try
                            {
                                var item = JsonSerializer.Deserialize<ConnectionHistoryItem>(line);
                                if (item != null)
                                {
                                    // Фильтрация по времени внутри дня
                                    if (startTime.HasValue && item.Timestamp.TimeOfDay < startTime.Value) continue;
                                    if (endTime.HasValue && item.Timestamp.TimeOfDay > endTime.Value) continue;

                                    if (!resultDict.ContainsKey(item.ProcessName))
                                    {
                                        resultDict[item.ProcessName] = new ProcessTrafficData
                                        {
                                            ProcessName = item.ProcessName,
                                            // Иконку пробуем найти в кэше Live или загрузить заново
                                            Icon = _liveProcessStats.TryGetValue(item.ProcessName, out var liveP) ? liveP.Icon : IconHelper.GetIconByProcessName(item.ProcessName)
                                        };
                                    }

                                    var pData = resultDict[item.ProcessName];
                                    pData.TotalDownload += item.BytesDown;
                                    pData.TotalUpload += item.BytesUp;

                                    // Добавляем в историю (в начало списка, чтобы новые были сверху)
                                    // Так как мы читаем последовательно, лучше добавлять в конец, а потом развернуть, или Insert(0)
                                    // Для производительности при чтении большого лога лучше Add и потом сортировка
                                    pData.Connections.Add(item);
                                }
                            }
                            catch { }
                        }
                    }
                    current = current.AddDays(1);
                }
            });

            // Сортировка соединений по времени (новые сверху)
            foreach (var p in resultDict.Values)
            {
                var sorted = p.Connections.OrderByDescending(x => x.Timestamp).ToList();
                p.Connections.Clear();
                foreach (var s in sorted) p.Connections.Add(s);
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                DisplayedProcessList.Clear();
                foreach (var p in resultDict.Values)
                {
                    DisplayedProcessList.Add(p);
                }
            });
        }

        public void SwitchToLiveMode()
        {
            IsLiveMode = true;
            Application.Current.Dispatcher.Invoke(() =>
            {
                DisplayedProcessList.Clear();
                foreach (var p in _liveProcessStats.Values)
                {
                    DisplayedProcessList.Add(p);
                }
            });
        }
    }
}