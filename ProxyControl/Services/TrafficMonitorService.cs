using Microsoft.Data.Sqlite;
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
        private readonly string _dbPath;
        private readonly string _connectionString;

        private readonly Channel<ConnectionHistoryItem> _logChannel;
        private readonly CancellationTokenSource _servicesCts;

        private readonly ConcurrentQueue<ConnectionHistoryItem> _pendingConnections = new ConcurrentQueue<ConnectionHistoryItem>();
        private int _pendingConnectionCount;
        private const int MaxPendingConnections = 5000;
        private readonly ConcurrentDictionary<string, TrafficDelta> _pendingTraffic = new ConcurrentDictionary<string, TrafficDelta>();

        private readonly DispatcherTimer _uiBatchTimer;
        private const int UiRefreshRateMs = 250;
        private DateTime _lastSpeedUpdateUtc = DateTime.UtcNow;

        private class TrafficDelta
        {
            public long Down;
            public long Up;
        }

        public bool IsLiveMode { get; set; } = true;

        public TrafficMonitorService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _logsPath = Path.Combine(appData, "ProxyManagerApp", "TrafficLogs");
            if (!Directory.Exists(_logsPath)) Directory.CreateDirectory(_logsPath);

            var oldLogsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TrafficLogs");
            MigrateLegacyLogs(oldLogsPath, _logsPath);

            _dbPath = Path.Combine(_logsPath, "traffic.db");
            _connectionString = $"Data Source={_dbPath}";
            InitializeDatabase();

            Task.Run(() =>
            {
                MigrateJsonlToSqlite();
                CleanupOldLogs(30);
            });

            _logChannel = Channel.CreateBounded<ConnectionHistoryItem>(new BoundedChannelOptions(25000)
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
                    if (stats.Connections.Count > 1000) stats.Connections.RemoveAt(stats.Connections.Count - 1);
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

            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSpeedUpdateUtc).TotalSeconds;
            if (elapsed >= 0.5)
            {
                _lastSpeedUpdateUtc = now;
                UpdateSpeeds(elapsed);
            }
        }

        private void UpdateSpeeds(double elapsedSeconds)
        {
            if (elapsedSeconds <= 0.001) elapsedSeconds = 0.5;

            long totalDown = 0;
            long totalUp = 0;

            foreach (var kvp in _liveProcessStats)
            {
                var stats = kvp.Value;
                long down = Interlocked.Exchange(ref stats.BytesDownLastSecond, 0);
                long up = Interlocked.Exchange(ref stats.BytesUpLastSecond, 0);

                long downSpeed = (long)Math.Round(down / elapsedSeconds);
                long upSpeed = (long)Math.Round(up / elapsedSeconds);

                if (stats.CurrentDownloadSpeed != downSpeed) stats.CurrentDownloadSpeed = downSpeed;
                if (stats.CurrentUploadSpeed != upSpeed) stats.CurrentUploadSpeed = upSpeed;

                totalDown += downSpeed;
                totalUp += upSpeed;
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

        private void InitializeDatabase()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmdPragma = conn.CreateCommand();
                cmdPragma.CommandText = @"
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                ";
                cmdPragma.ExecuteNonQuery();

                using var cmdTable = conn.CreateCommand();
                cmdTable.CommandText = @"
                    CREATE TABLE IF NOT EXISTS connections (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp TEXT NOT NULL,
                        process_name TEXT NOT NULL,
                        process_path TEXT,
                        host TEXT NOT NULL,
                        status TEXT,
                        details TEXT,
                        flag_url TEXT,
                        color TEXT,
                        traffic_type INTEGER NOT NULL DEFAULT 0,
                        bytes_down INTEGER NOT NULL DEFAULT 0,
                        bytes_up INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE INDEX IF NOT EXISTS idx_connections_timestamp ON connections(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_connections_proc_time ON connections(process_name, timestamp);
                ";
                cmdTable.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("TrafficMonitor", $"Failed to initialize SQLite database: {ex.Message}");
            }
        }

        private async Task LogWriterLoop(CancellationToken token)
        {
            try
            {
                while (await _logChannel.Reader.WaitToReadAsync(token))
                {
                    var batch = new List<ConnectionHistoryItem>();
                    while (_logChannel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        if (batch.Count >= 500) break;
                    }

                    if (batch.Count > 0)
                    {
                        await InsertBatchAsync(batch);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("TrafficMonitor", $"LogWriterLoop error: {ex.Message}");
            }
        }

        private async Task InsertBatchAsync(List<ConnectionHistoryItem> batch)
        {
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                await using var tx = conn.BeginTransaction();
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO connections (
                        timestamp, process_name, process_path, host, status, details, flag_url, color, traffic_type, bytes_down, bytes_up
                    ) VALUES (
                        @timestamp, @process_name, @process_path, @host, @status, @details, @flag_url, @color, @traffic_type, @bytes_down, @bytes_up
                    );";

                var pTimestamp = cmd.Parameters.Add("@timestamp", SqliteType.Text);
                var pProcName = cmd.Parameters.Add("@process_name", SqliteType.Text);
                var pProcPath = cmd.Parameters.Add("@process_path", SqliteType.Text);
                var pHost = cmd.Parameters.Add("@host", SqliteType.Text);
                var pStatus = cmd.Parameters.Add("@status", SqliteType.Text);
                var pDetails = cmd.Parameters.Add("@details", SqliteType.Text);
                var pFlagUrl = cmd.Parameters.Add("@flag_url", SqliteType.Text);
                var pColor = cmd.Parameters.Add("@color", SqliteType.Text);
                var pTrafficType = cmd.Parameters.Add("@traffic_type", SqliteType.Integer);
                var pBytesDown = cmd.Parameters.Add("@bytes_down", SqliteType.Integer);
                var pBytesUp = cmd.Parameters.Add("@bytes_up", SqliteType.Integer);

                foreach (var item in batch)
                {
                    pTimestamp.Value = item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    pProcName.Value = item.ProcessName ?? "";
                    pProcPath.Value = item.ProcessPath ?? "";
                    pHost.Value = item.Host ?? "";
                    pStatus.Value = item.Status ?? "";
                    pDetails.Value = item.Details ?? "";
                    pFlagUrl.Value = (object?)item.FlagUrl ?? DBNull.Value;
                    pColor.Value = item.Color ?? "White";
                    pTrafficType.Value = (int)item.Type;
                    pBytesDown.Value = item.BytesDown;
                    pBytesUp.Value = item.BytesUp;

                    await cmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Error("TrafficMonitor", $"InsertBatchAsync error: {ex.Message}");
            }
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

            await Task.Run(async () =>
            {
                var startDateTime = start.Date + (startTime ?? TimeSpan.Zero);
                var endDateTime = end.Date + (endTime ?? new TimeSpan(0, 23, 59, 59, 999));
                var startStr = startDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var endStr = endDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");

                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(token);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT timestamp, process_name, process_path, host, status, details, flag_url, color, traffic_type, bytes_down, bytes_up
                    FROM connections
                    WHERE timestamp >= @startStr AND timestamp <= @endStr
                    ORDER BY timestamp DESC;";
                cmd.Parameters.AddWithValue("@startStr", startStr);
                cmd.Parameters.AddWithValue("@endStr", endStr);

                await using var reader = await cmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    token.ThrowIfCancellationRequested();

                    var tsStr = reader.GetString(0);
                    var procName = reader.GetString(1);
                    var procPath = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var host = reader.GetString(3);
                    var status = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    var details = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    var flagUrl = reader.IsDBNull(6) ? null : reader.GetString(6);
                    var color = reader.IsDBNull(7) ? "White" : reader.GetString(7);
                    var trafficType = (TrafficType)reader.GetInt32(8);
                    var bytesDown = reader.GetInt64(9);
                    var bytesUp = reader.GetInt64(10);

                    DateTime.TryParse(tsStr, out var timestamp);

                    var item = new ConnectionHistoryItem
                    {
                        Timestamp = timestamp,
                        ProcessName = procName,
                        ProcessPath = procPath,
                        Host = host,
                        Status = status,
                        Details = details,
                        FlagUrl = flagUrl,
                        Color = color,
                        Type = trafficType,
                        BytesDown = bytesDown,
                        BytesUp = bytesUp
                    };

                    if (!resultDict.TryGetValue(procName, out var pData))
                    {
                        pData = new ProcessTrafficData
                        {
                            ProcessName = procName,
                            ProcessPath = procPath
                        };
                        resultDict[procName] = pData;
                    }

                    if (string.IsNullOrWhiteSpace(pData.ProcessPath) && !string.IsNullOrWhiteSpace(procPath))
                    {
                        pData.ProcessPath = procPath;
                    }

                    pData.TotalDownload += bytesDown;
                    pData.TotalUpload += bytesUp;

                    if (pData.Connections.Count < 1000)
                    {
                        pData.Connections.Add(item);
                    }
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
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                DisplayedProcessList.Clear();
                foreach (var p in resultDict.Values.OrderByDescending(x => x.TotalTraffic))
                {
                    DisplayedProcessList.Add(p);
                }
            }, DispatcherPriority.Background);
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

        private static void MigrateLegacyLogs(string oldDir, string newDir)
        {
            try
            {
                if (Directory.Exists(oldDir) && !string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var file in Directory.GetFiles(oldDir))
                    {
                        var destFile = Path.Combine(newDir, Path.GetFileName(file));
                        if (!File.Exists(destFile))
                        {
                            try { File.Move(file, destFile); } catch { }
                        }
                    }
                    if (Directory.GetFiles(oldDir).Length == 0 && Directory.GetDirectories(oldDir).Length == 0)
                    {
                        try { Directory.Delete(oldDir); } catch { }
                    }
                }
            }
            catch
            {
                // Ignore migration errors (e.g. read-only legacy directory)
            }
        }

        private void CleanupOldLogs(int retentionDays = 30)
        {
            try
            {
                var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
                var cutoffStr = cutoff.ToString("yyyy-MM-dd HH:mm:ss.fff");

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM connections WHERE timestamp < @cutoffStr;";
                cmd.Parameters.AddWithValue("@cutoffStr", cutoffStr);
                cmd.ExecuteNonQuery();

                if (Directory.Exists(_logsPath))
                {
                    foreach (var file in Directory.GetFiles(_logsPath, "log_*.jsonl"))
                    {
                        try
                        {
                            var name = Path.GetFileNameWithoutExtension(file);
                            if (name.StartsWith("log_") && DateTime.TryParse(name.Substring(4), out var fileDate))
                            {
                                if (fileDate < cutoff) File.Delete(file);
                            }
                            else if (File.GetLastWriteTime(file) < cutoff)
                            {
                                File.Delete(file);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Warning("TrafficMonitor", $"CleanupOldLogs error: {ex.Message}");
            }
        }

        private void MigrateJsonlToSqlite()
        {
            try
            {
                if (!Directory.Exists(_logsPath)) return;

                var files = Directory.GetFiles(_logsPath, "log_*.jsonl");
                if (files.Length == 0) return;

                AppLoggerService.Instance.Info("TrafficMonitor", $"Found {files.Length} legacy JSONL log files. Starting migration to SQLite...");

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                foreach (var file in files)
                {
                    try
                    {
                        var items = new List<ConnectionHistoryItem>();
                        foreach (var line in File.ReadLines(file))
                        {
                            try
                            {
                                var item = JsonSerializer.Deserialize<ConnectionHistoryItem>(line);
                                if (item != null) items.Add(item);
                            }
                            catch { }
                        }

                        if (items.Count > 0)
                        {
                            using var tx = conn.BeginTransaction();
                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = @"
                                INSERT INTO connections (
                                    timestamp, process_name, process_path, host, status, details, flag_url, color, traffic_type, bytes_down, bytes_up
                                ) VALUES (
                                    @timestamp, @process_name, @process_path, @host, @status, @details, @flag_url, @color, @traffic_type, @bytes_down, @bytes_up
                                );";

                            var pTimestamp = cmd.Parameters.Add("@timestamp", SqliteType.Text);
                            var pProcName = cmd.Parameters.Add("@process_name", SqliteType.Text);
                            var pProcPath = cmd.Parameters.Add("@process_path", SqliteType.Text);
                            var pHost = cmd.Parameters.Add("@host", SqliteType.Text);
                            var pStatus = cmd.Parameters.Add("@status", SqliteType.Text);
                            var pDetails = cmd.Parameters.Add("@details", SqliteType.Text);
                            var pFlagUrl = cmd.Parameters.Add("@flag_url", SqliteType.Text);
                            var pColor = cmd.Parameters.Add("@color", SqliteType.Text);
                            var pTrafficType = cmd.Parameters.Add("@traffic_type", SqliteType.Integer);
                            var pBytesDown = cmd.Parameters.Add("@bytes_down", SqliteType.Integer);
                            var pBytesUp = cmd.Parameters.Add("@bytes_up", SqliteType.Integer);

                            foreach (var item in items)
                            {
                                pTimestamp.Value = item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                pProcName.Value = item.ProcessName ?? "";
                                pProcPath.Value = item.ProcessPath ?? "";
                                pHost.Value = item.Host ?? "";
                                pStatus.Value = item.Status ?? "";
                                pDetails.Value = item.Details ?? "";
                                pFlagUrl.Value = (object?)item.FlagUrl ?? DBNull.Value;
                                pColor.Value = item.Color ?? "White";
                                pTrafficType.Value = (int)item.Type;
                                pBytesDown.Value = item.BytesDown;
                                pBytesUp.Value = item.BytesUp;

                                cmd.ExecuteNonQuery();
                            }

                            tx.Commit();
                        }

                        try { File.Delete(file); } catch { }
                    }
                    catch (Exception ex)
                    {
                        AppLoggerService.Instance.Warning("TrafficMonitor", $"Migration of {file} failed: {ex.Message}");
                    }
                }

                AppLoggerService.Instance.Info("TrafficMonitor", "Legacy JSONL migration to SQLite completed successfully.");
            }
            catch (Exception ex)
            {
                AppLoggerService.Instance.Warning("TrafficMonitor", $"MigrateJsonlToSqlite error: {ex.Message}");
            }
        }
    }
}
