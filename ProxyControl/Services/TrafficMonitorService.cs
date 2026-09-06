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
        public event Action<ConnectionHistoryItem>? ConnectionCreated;
        public event Action? OverallStatsUpdated;

        public long TotalCurrentDownloadSpeed { get; private set; }
        public long TotalCurrentUploadSpeed { get; private set; }
        private int _activeConnections;
        public int TotalActiveConnections => Math.Max(0, _activeConnections);

        private readonly ConcurrentDictionary<string, ProcessTrafficData> _liveProcessStats
            = new ConcurrentDictionary<string, ProcessTrafficData>();

        public ObservableCollection<ProcessTrafficData> DisplayedProcessList { get; private set; }
            = new ObservableCollection<ProcessTrafficData>();

        private readonly string _logsPath;

        private readonly Channel<ConnectionHistoryItem> _logChannel;
        private readonly CancellationTokenSource _servicesCts;

        private readonly ConcurrentQueue<ConnectionHistoryItem> _pendingConnections = new ConcurrentQueue<ConnectionHistoryItem>();
        private int _pendingConnectionCount;
        private const int MaxPendingConnections = 5000;
        private readonly ConcurrentDictionary<string, TrafficDelta> _pendingTraffic = new ConcurrentDictionary<string, TrafficDelta>();

        private readonly DispatcherTimer _uiBatchTimer;
        private const int UiRefreshRateMs = 250;

        private class TrafficDelta
        {
            public long Down;
            public long Up;
        }

        public bool IsLiveMode { get; set; } = true;

        public TrafficMonitorService()
        {
            _logsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TrafficLogs");
            if (!Directory.Exists(_logsPath)) Directory.CreateDirectory(_logsPath);

            _logChannel = Channel.CreateBounded<ConnectionHistoryItem>(new BoundedChannelOptions(10000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _servicesCts = new CancellationTokenSource();

            Task.Run(() => LogWriterLoop(_servicesCts.Token));

            _uiBatchTimer = new DispatcherTimer(DispatcherPriority.Background);
            _uiBatchTimer.Interval = TimeSpan.FromMilliseconds(UiRefreshRateMs);
            _uiBatchTimer.Tick += OnUiBatchTick;
            _uiBatchTimer.Start();
        }

        public ProcessTrafficData GetOrAddLiveProcess(string processName, string processPath, ImageSource? icon)
        {
            var process = _liveProcessStats.GetOrAdd(processName, name =>
            {
                var newData = new ProcessTrafficData
                {
                    ProcessName = name,
                    ProcessPath = processPath,
                    Icon = icon
                };

                if (IsLiveMode)
                {
                    Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                    {
                        if (!DisplayedProcessList.Any(p => p.ProcessName == name))
                            DisplayedProcessList.Add(newData);
                    });
                }
                return newData;
            });

            if (string.IsNullOrWhiteSpace(process.ProcessPath) && !string.IsNullOrWhiteSpace(processPath))
                process.ProcessPath = processPath;
            if (process.Icon == null && icon != null)
                process.Icon = icon;
            return process;
        }

        public void AddLiveTraffic(string processName, long bytes, bool isDownload)
        {
            if (_liveProcessStats.TryGetValue(processName, out var stats))
            {
                if (isDownload) Interlocked.Add(ref stats.BytesDownLastSecond, bytes);
                else Interlocked.Add(ref stats.BytesUpLastSecond, bytes);
            }

            _pendingTraffic.AddOrUpdate(processName,
                _ => new TrafficDelta { Down = isDownload ? bytes : 0, Up = isDownload ? 0 : bytes },
                (_, delta) =>
                {
                    if (isDownload) Interlocked.Add(ref delta.Down, bytes);
                    else Interlocked.Add(ref delta.Up, bytes);
                    return delta;
                });
        }

        public ConnectionHistoryItem CreateConnectionItem(
            string processName,
            ImageSource? icon,
            string host,
            string status,
            string details,
            string? flagUrl,
            string color,
            TrafficType trafficType = TrafficType.TCP,
            string processPath = "")
        {
            var item = new ConnectionHistoryItem
            {
                Timestamp = DateTime.Now,
                ProcessName = processName,
                ProcessPath = processPath,
                Host = host,
                Status = status,
                Details = details,
                FlagUrl = flagUrl,
                Color = color,
                Type = trafficType
            };

            GetOrAddLiveProcess(processName, processPath, icon);
            _pendingConnections.Enqueue(item);
            int count = Interlocked.Increment(ref _pendingConnectionCount);
            Interlocked.Increment(ref _activeConnections);
            while (count > MaxPendingConnections && _pendingConnections.TryDequeue(out _))
            {
                count = Interlocked.Decrement(ref _pendingConnectionCount);
            }
            ConnectionCreated?.Invoke(item);
            return item;
        }

        public void CompleteConnection(ConnectionHistoryItem item)
        {
            if (Interlocked.Decrement(ref _activeConnections) < 0)
                Interlocked.Exchange(ref _activeConnections, 0);
            _logChannel.Writer.TryWrite(item);
        }

        private void OnUiBatchTick(object? sender, EventArgs e)
        {
            if (!IsLiveMode) return;

            bool hasNewConnections = !_pendingConnections.IsEmpty;
            while (_pendingConnections.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _pendingConnectionCount);
                if (_liveProcessStats.TryGetValue(item.ProcessName, out var stats))
                {
                    stats.Connections.Insert(0, item);
                    if (stats.Connections.Count > 200) stats.Connections.RemoveAt(stats.Connections.Count - 1);
                }
            }

            foreach (var kvp in _liveProcessStats)
            {
                var processName = kvp.Key;
                var stats = kvp.Value;

                if (_pendingTraffic.TryRemove(processName, out var delta))
                {
                    stats.TotalDownload += delta.Down;
                    stats.TotalUpload += delta.Up;
                }
            }

            if ((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) % 1000 < UiRefreshRateMs * 1.5)
            {
                UpdateSpeeds();
            }
        }

        private void UpdateSpeeds()
        {
            long totalDown = 0;
            long totalUp = 0;

            foreach (var kvp in _liveProcessStats)
            {
                var stats = kvp.Value;
                long down = Interlocked.Exchange(ref stats.BytesDownLastSecond, 0);
                long up = Interlocked.Exchange(ref stats.BytesUpLastSecond, 0);

                if (stats.CurrentDownloadSpeed != down) stats.CurrentDownloadSpeed = down;
                if (stats.CurrentUploadSpeed != up) stats.CurrentUploadSpeed = up;

                totalDown += down;
                totalUp += up;
            }

            TotalCurrentDownloadSpeed = totalDown;
            TotalCurrentUploadSpeed = totalUp;

            OverallStatsUpdated?.Invoke();
        }

        public static string FormatSpeed(long bytesPerSec)
        {
            if (bytesPerSec < 1024)
                return $"{bytesPerSec} B/s";
            if (bytesPerSec < 1024 * 1024)
                return $"{bytesPerSec / 1024.0:F1} KB/s";
            if (bytesPerSec < 1024 * 1024 * 1024)
                return $"{bytesPerSec / (1024.0 * 1024.0):F1} MB/s";
            return $"{bytesPerSec / (1024.0 * 1024.0 * 1024.0):F2} GB/s";
        }

        // Fix 3.1: LogWriter uses a persistent FileStream to reduce IO overhead
        private async Task LogWriterLoop(CancellationToken token)
        {
            try
            {
                string currentFileName = "";
                FileStream? currentStream = null;
                StreamWriter? currentWriter = null;

                while (await _logChannel.Reader.WaitToReadAsync(token))
                {
                    while (_logChannel.Reader.TryRead(out var item))
                    {
                        try
                        {
                            string fileName = $"log_{item.Timestamp:yyyy-MM-dd}.jsonl";
                            string fullPath = Path.Combine(_logsPath, fileName);

                            if (currentFileName != fileName)
                            {
                                if (currentWriter != null)
                                {
                                    await currentWriter.DisposeAsync();
                                    currentStream?.Dispose();
                                }

                                currentFileName = fileName;
                                currentStream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                                currentWriter = new StreamWriter(currentStream) { AutoFlush = false };
                            }

                            string json = JsonSerializer.Serialize(item);
                            if (currentWriter != null)
                            {
                                await currentWriter.WriteLineAsync(json);
                            }
                        }
                        catch { }
                    }

                    // Flush batch
                    if (currentWriter != null) await currentWriter.FlushAsync();
                }

                if (currentWriter != null) await currentWriter.DisposeAsync();
                currentStream?.Dispose();
            }
            catch (OperationCanceledException) { }
        }

        public async Task LoadHistoryAsync(
            DateTime start,
            DateTime end,
            TimeSpan? startTime = null,
            TimeSpan? endTime = null,
            CancellationToken token = default)
        {
            IsLiveMode = false;
            _uiBatchTimer.Stop();

            var resultDict = new Dictionary<string, ProcessTrafficData>();

            await Task.Run(() =>
            {
                var current = start.Date;
                var endDate = end.Date;

                while (current <= endDate)
                {
                    token.ThrowIfCancellationRequested();
                    string fileName = $"log_{current:yyyy-MM-dd}.jsonl";
                    string fullPath = Path.Combine(_logsPath, fileName);

                    if (File.Exists(fullPath))
                    {
                        foreach (var line in File.ReadLines(fullPath))
                        {
                            token.ThrowIfCancellationRequested();
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
                                            ProcessPath = item.ProcessPath
                                        };
                                    }

                                    var pData = resultDict[item.ProcessName];
                                    if (string.IsNullOrWhiteSpace(pData.ProcessPath) &&
                                        !string.IsNullOrWhiteSpace(item.ProcessPath))
                                    {
                                        pData.ProcessPath = item.ProcessPath;
                                    }
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
            }, token);

            token.ThrowIfCancellationRequested();

            foreach (var p in resultDict.Values)
            {
                p.Icon = !string.IsNullOrWhiteSpace(p.ProcessPath)
                    ? IconHelper.GetIconByPath(p.ProcessPath, p.ProcessPath)
                    : _liveProcessStats.TryGetValue(p.ProcessName, out var liveP)
                        ? liveP.Icon
                        : IconHelper.GetIconByProcessName(p.ProcessName);
                var sorted = p.Connections.OrderByDescending(x => x.Timestamp).ToList();
                p.Connections.Clear();
                foreach (var s in sorted) p.Connections.Add(s);
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                DisplayedProcessList.Clear();
                foreach (var p in resultDict.Values)
                {
                    DisplayedProcessList.Add(p);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        public void SwitchToLiveMode()
        {
            IsLiveMode = true;
            _uiBatchTimer.Start();
            Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
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
