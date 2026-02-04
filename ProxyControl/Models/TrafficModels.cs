using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace ProxyControl.Models
{
    public class ProcessTrafficData : INotifyPropertyChanged
    {
        private long _currentDownloadSpeed;
        private long _currentUploadSpeed;
        private long _totalDownload;
        private long _totalUpload;

        public string ProcessName { get; set; } = "Unknown";

        [JsonIgnore]
        public ImageSource? Icon { get; set; }

        [JsonIgnore]
        public long CurrentDownloadSpeed
        {
            get => _currentDownloadSpeed;
            set
            {
                if (_currentDownloadSpeed != value)
                {
                    _currentDownloadSpeed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DownloadSpeedFormatted));
                }
            }
        }

        [JsonIgnore]
        public long CurrentUploadSpeed
        {
            get => _currentUploadSpeed;
            set
            {
                if (_currentUploadSpeed != value)
                {
                    _currentUploadSpeed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UploadSpeedFormatted));
                }
            }
        }

        public long TotalDownload
        {
            get => _totalDownload;
            set
            {
                if (_totalDownload != value)
                {
                    _totalDownload = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TotalDownloadFormatted));
                }
            }
        }

        public long TotalUpload
        {
            get => _totalUpload;
            set
            {
                if (_totalUpload != value)
                {
                    _totalUpload = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TotalUploadFormatted));
                }
            }
        }

        [JsonIgnore]
        public ObservableCollection<ConnectionHistoryItem> Connections { get; set; } = new ObservableCollection<ConnectionHistoryItem>();

        [JsonIgnore]
        public long BytesDownLastSecond;
        [JsonIgnore]
        public long BytesUpLastSecond;

        [JsonIgnore]
        public string DownloadSpeedFormatted => FormatSpeed(CurrentDownloadSpeed);
        [JsonIgnore]
        public string UploadSpeedFormatted => FormatSpeed(CurrentUploadSpeed);
        [JsonIgnore]
        public string TotalDownloadFormatted => FormatSize(TotalDownload);
        [JsonIgnore]
        public string TotalUploadFormatted => FormatSize(TotalUpload);

        public static string FormatSpeed(long bytesPerSec) => $"{FormatSize(bytesPerSec)}/s";

        public static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ConnectionHistoryItem : INotifyPropertyChanged
    {
        private long _bytesDown;
        private long _bytesUp;

        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string TimeStr => Timestamp.ToString("HH:mm:ss"); // Для отображения в гриде
        public string Host { get; set; } = "";
        public string ProcessName { get; set; } = ""; // Для сохранения в лог
        public string Status { get; set; } = "";
        public string Details { get; set; } = "";
        public string? FlagUrl { get; set; }
        public string Color { get; set; } = "White";

        // Traffic type for Monitor display
        public TrafficType Type { get; set; } = TrafficType.TCP;

        [JsonIgnore]
        public string TypeIcon => Type switch
        {
            TrafficType.TCP => "🔵",
            TrafficType.UDP => "🟢",
            TrafficType.DNS => "🟡",
            TrafficType.HTTPS => "🔒",
            TrafficType.WebSocket => "🔄",
            _ => "⚪"
        };

        public long BytesDown
        {
            get => _bytesDown;
            set { _bytesDown = value; OnPropertyChanged(); OnPropertyChanged(nameof(TrafficFormatted)); }
        }

        public long BytesUp
        {
            get => _bytesUp;
            set { _bytesUp = value; OnPropertyChanged(); OnPropertyChanged(nameof(TrafficFormatted)); }
        }

        [JsonIgnore]
        public string TrafficFormatted => $"↓{ProcessTrafficData.FormatSize(BytesDown)} ↑{ProcessTrafficData.FormatSize(BytesUp)}";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum TrafficPeriodMode
    {
        LiveSession,
        Today,
        Yesterday,
        CustomRange
    }
}