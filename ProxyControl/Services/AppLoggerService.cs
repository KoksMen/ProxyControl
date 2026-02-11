using Serilog;
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
        private readonly object _uiLock = new object();
        private readonly Serilog.ILogger _fileLogger;

        public ObservableCollection<LogEntry> LogEntries { get; } = new ObservableCollection<LogEntry>();

        public int MaxLogEntries { get; set; } = 1000;
        public LogLevel MinLevel { get; set; } = LogLevel.Debug;

        private AppLoggerService()
        {
            var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);

            // Configure Serilog
            _fileLogger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(Path.Combine(logsDir, "app-.log"),
                              rollingInterval: RollingInterval.Day,
                              outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

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

            // Log to Serilog
            switch (level)
            {
                case LogLevel.Debug:
                    _fileLogger.ForContext("SourceContext", source).Debug(message);
                    break;
                case LogLevel.Info:
                    _fileLogger.ForContext("SourceContext", source).Information(message);
                    break;
                case LogLevel.Warning:
                    _fileLogger.ForContext("SourceContext", source).Warning(message);
                    break;
                case LogLevel.Error:
                    _fileLogger.ForContext("SourceContext", source).Error(message);
                    break;
            }

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
                while (await _logChannel.Reader.WaitToReadAsync())
                {
                    while (_logChannel.Reader.TryRead(out var entry))
                    {
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
