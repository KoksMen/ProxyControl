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
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProxyControl.Services
{
    public class TrafficMonitorService
    {
        // Для Live режима (хранилище данных)
        private readonly ConcurrentDictionary<string, ProcessTrafficData> _liveProcessStats
            = new ConcurrentDictionary<string, ProcessTrafficData>();

        // Основная коллекция для биндинга (UI)
        public ObservableCollection<ProcessTrafficData> DisplayedProcessList { get; private set; }
            = new ObservableCollection<ProcessTrafficData>();

        private readonly string _logsPath;

        // --- Async Logging (Disk I/O) ---
        private readonly Channel<ConnectionHistoryItem> _logChannel;
        private readonly CancellationTokenSource _servicesCts;
        // --------------------------------

        // --- UI Throttling (Batching) ---
        private readonly ConcurrentQueue<ConnectionHistoryItem> _pendingConnections = new ConcurrentQueue<ConnectionHistoryItem>();
        // Накапливаем дельту трафика: Key -> (DownloadedBytes, UploadedBytes)
        private readonly ConcurrentDictionary<string, TrafficDelta> _pendingTraffic = new ConcurrentDictionary<string, TrafficDelta>();

        private readonly DispatcherTimer _uiBatchTimer;
        private const int UiRefreshRateMs = 250; // Частота обновления UI (4 раза в секунду)

        private class TrafficDelta
        {
            public long Down;
            public long Up;
        }
        // --------------------------------

        // Флаг режима: true = показываем реальное время, false = историю
        public bool IsLiveMode { get; set; } = true;

        public TrafficMonitorService()
        {
            _logsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TrafficLogs");
            if (!Directory.Exists(_logsPath)) Directory.CreateDirectory(_logsPath);

            _logChannel = Channel.CreateUnbounded<ConnectionHistoryItem>();
            _servicesCts = new CancellationTokenSource();

            // Запуск фоновой задачи записи логов на диск
            Task.Run(() => LogWriterLoop(_servicesCts.Token));

            // Таймер для пакетного обновления UI (Throttling)
            // Используем DispatcherTimer, чтобы тик срабатывал сразу в UI потоке
            _uiBatchTimer = new DispatcherTimer(DispatcherPriority.Background);
            _uiBatchTimer.Interval = TimeSpan.FromMilliseconds(UiRefreshRateMs);
            _uiBatchTimer.Tick += OnUiBatchTick;
            _uiBatchTimer.Start();
        }

        // --- Live Logic ---

        public ProcessTrafficData GetOrAddLiveProcess(string processName, ImageSource? icon)
        {
            return _liveProcessStats.GetOrAdd(processName, name =>
            {
                var newData = new ProcessTrafficData { ProcessName = name, Icon = icon };

                // Добавляем в UI коллекцию через Dispatcher, если еще нет
                // (Это происходит редко - только при появлении нового процесса, поэтому можно не батчить жестко)
                if (IsLiveMode)
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
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
            // 1. Обновляем атомарные счетчики для расчета скорости (они не привязаны к UI напрямую)
            if (_liveProcessStats.TryGetValue(processName, out var stats))
            {
                if (isDownload) Interlocked.Add(ref stats.BytesDownLastSecond, bytes);
                else Interlocked.Add(ref stats.BytesUpLastSecond, bytes);
            }

            // 2. Накапливаем дельту для UI (Total Download/Upload) в буфер
            _pendingTraffic.AddOrUpdate(processName,
                _ => new TrafficDelta { Down = isDownload ? bytes : 0, Up = isDownload ? 0 : bytes },
                (_, delta) =>
                {
                    if (isDownload) Interlocked.Add(ref delta.Down, bytes);
                    else Interlocked.Add(ref delta.Up, bytes);
                    return delta;
                });
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

            // Убеждаемся, что процесс есть в статистике
            GetOrAddLiveProcess(processName, icon);

            // Вместо тяжелого Invoke - кладем в очередь
            _pendingConnections.Enqueue(item);

            return item;
        }

        public void CompleteConnection(ConnectionHistoryItem item)
        {
            _logChannel.Writer.TryWrite(item);
        }

        // --- Batch UI Update Logic ---

        private void OnUiBatchTick(object? sender, EventArgs e)
        {
            if (!IsLiveMode) return;

            // 1. Обработка новых соединений (Log list)
            bool hasNewConnections = !_pendingConnections.IsEmpty;
            while (_pendingConnections.TryDequeue(out var item))
            {
                if (_liveProcessStats.TryGetValue(item.ProcessName, out var stats))
                {
                    stats.Connections.Insert(0, item);
                    // Ограничиваем размер истории в памяти
                    if (stats.Connections.Count > 200) stats.Connections.RemoveAt(stats.Connections.Count - 1);
                }
            }

            // 2. Обработка накопленного трафика и расчет скорости
            // Проходим по всем процессам, даже если трафика не было (чтобы сбросить скорость в 0)
            foreach (var kvp in _liveProcessStats)
            {
                var processName = kvp.Key;
                var stats = kvp.Value;

                // Забираем накопленные байты для TotalDownload/Upload
                if (_pendingTraffic.TryRemove(processName, out var delta))
                {
                    stats.TotalDownload += delta.Down;
                    stats.TotalUpload += delta.Up;
                }

                // Расчет скорости (Atomic exchange сбрасывает счетчик за интервал)
                // Интервал таймера ~250мс, но счетчики BytesDownLastSecond накапливаются
                // Для корректного отображения "в секунду" нам нужен отдельный механизм или пересчет.
                // В оригинале был таймер раз в 1 сек. Здесь мы сделаем проще:
                // Мы будем читать атомарно счетчики, но сбрасывать их раз в секунду.
                // Для этого добавим счетчик тиков или используем DateTime. 
                // НО, чтобы не ломать логику, просто перенесем логику скорости сюда.
                // UpdateSpeeds был раз в 1с. Сделаем проверку времени.
            }

            // Вызов обновления скоростей раз в секунду (приблизительно каждые 4 тика)
            if ((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) % 1000 < UiRefreshRateMs * 1.5)
            {
                UpdateSpeeds();
            }
        }

        private void UpdateSpeeds()
        {
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

        private async Task LogWriterLoop(CancellationToken token)
        {
            try
            {
                while (await _logChannel.Reader.WaitToReadAsync(token))
                {
                    while (_logChannel.Reader.TryRead(out var item))
                    {
                        try
                        {
                            string fileName = $"log_{item.Timestamp:yyyy-MM-dd}.jsonl";
                            string fullPath = Path.Combine(_logsPath, fileName);
                            string json = JsonSerializer.Serialize(item);

                            await File.AppendAllTextAsync(fullPath, json + Environment.NewLine, token);
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task LoadHistoryAsync(DateTime start, DateTime end, TimeSpan? startTime = null, TimeSpan? endTime = null)
        {
            IsLiveMode = false;
            // Останавливаем таймер обновлений, чтобы не мешал
            _uiBatchTimer.Stop();

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
                        foreach (var line in File.ReadLines(fullPath))
                        {
                            try
                            {
                                var item = JsonSerializer.Deserialize<ConnectionHistoryItem>(line);
                                if (item != null)
                                {
                                    if (startTime.HasValue && item.Timestamp.TimeOfDay < startTime.Value) continue;
                                    if (endTime.HasValue && item.Timestamp.TimeOfDay > endTime.Value) continue;

                                    if (!resultDict.ContainsKey(item.ProcessName))
                                    {
                                        resultDict[item.ProcessName] = new ProcessTrafficData
                                        {
                                            ProcessName = item.ProcessName,
                                            Icon = _liveProcessStats.TryGetValue(item.ProcessName, out var liveP) ? liveP.Icon : IconHelper.GetIconByProcessName(item.ProcessName)
                                        };
                                    }

                                    var pData = resultDict[item.ProcessName];
                                    pData.TotalDownload += item.BytesDown;
                                    pData.TotalUpload += item.BytesUp;
                                    pData.Connections.Add(item);
                                }
                            }
                            catch { }
                        }
                    }
                    current = current.AddDays(1);
                }
            });

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
            _uiBatchTimer.Start();
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