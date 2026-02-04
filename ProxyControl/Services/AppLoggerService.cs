using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ProxyControl.Services
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public LogLevel Level { get; set; }
        public string Source { get; set; } = "";
        public string Message { get; set; } = "";

        public string TimeStr => Timestamp.ToString("HH:mm:ss.fff");
        public string LevelStr => Level.ToString().ToUpper();

        public string Color => Level switch
        {
            LogLevel.Debug => "#888888",
            LogLevel.Info => "#55AAFF",
            LogLevel.Warning => "#FFAA00",
            LogLevel.Error => "#FF5555",
            _ => "#FFFFFF"
        };
    }

    public class AppLoggerService
    {
        private static AppLoggerService? _instance;
        public static AppLoggerService Instance => _instance ??= new AppLoggerService();

        private readonly Channel<LogEntry> _logChannel;
        private readonly string _logFilePath;
        private readonly object _uiLock = new object();

        public ObservableCollection<LogEntry> LogEntries { get; } = new ObservableCollection<LogEntry>();

        public int MaxLogEntries { get; set; } = 1000;
        public LogLevel MinLevel { get; set; } = LogLevel.Debug;

        private AppLoggerService()
        {
            var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
            _logFilePath = Path.Combine(logsDir, $"app_{DateTime.Now:yyyy-MM-dd}.log");

            _logChannel = Channel.CreateUnbounded<LogEntry>();
            Task.Run(ProcessLogQueue);

            // Log startup
            Info("App", "ProxyControl started");
        }

        public void Debug(string source, string message) => Log(LogLevel.Debug, source, message);
        public void Info(string source, string message) => Log(LogLevel.Info, source, message);
        public void Warning(string source, string message) => Log(LogLevel.Warning, source, message);
        public void Error(string source, string message) => Log(LogLevel.Error, source, message);

        public void Log(LogLevel level, string source, string message)
        {
            if (level < MinLevel) return;

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Source = source,
                Message = message
            };

            _logChannel.Writer.TryWrite(entry);
        }

        private async Task ProcessLogQueue()
        {
            try
            {
                await using var writer = new StreamWriter(_logFilePath, append: true) { AutoFlush = false };
                int batchCount = 0;

                while (await _logChannel.Reader.WaitToReadAsync())
                {
                    while (_logChannel.Reader.TryRead(out var entry))
                    {
                        // Write to file
                        await writer.WriteLineAsync($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.LevelStr}] [{entry.Source}] {entry.Message}");
                        batchCount++;

                        // Update UI (throttled)
                        Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background, () =>
                        {
                            lock (_uiLock)
                            {
                                LogEntries.Add(entry);
                                while (LogEntries.Count > MaxLogEntries)
                                {
                                    LogEntries.RemoveAt(0);
                                }
                            }
                        });
                    }

                    // Flush periodically
                    if (batchCount >= 10)
                    {
                        await writer.FlushAsync();
                        batchCount = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AppLogger error: {ex.Message}");
            }
        }

        public void Clear()
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                lock (_uiLock)
                {
                    LogEntries.Clear();
                }
            });
        }
    }
}
