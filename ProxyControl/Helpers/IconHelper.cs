using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProxyControl.Helpers
{
    public static class IconHelper
    {
        // Кэш иконок, чтобы не извлекать их каждый раз (тяжелая операция)
        private static readonly ConcurrentDictionary<string, ImageSource> _iconCache = new ConcurrentDictionary<string, ImageSource>();

        public static ImageSource? GetIconByProcessName(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return null;
            if (_iconCache.TryGetValue(processName, out var cached)) return cached;

            try
            {
                // Пытаемся найти запущенный процесс
                var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
                if (processes.Length > 0)
                {
                    try
                    {
                        var path = processes[0].MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                        {
                            return GetIconByPath(path, processName); // Используем имя как ключ кэша
                        }
                    }
                    catch { } // Ограничение прав доступа к системным процессам
                }
            }
            catch { }

            return null;
        }

        public static ImageSource? GetIconByPath(string path, string cacheKey = null)
        {
            string key = cacheKey ?? path;
            if (_iconCache.TryGetValue(key, out var cached)) return cached;

            try
            {
                if (!File.Exists(path)) return null;

                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) return null;

                    var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());

                    imageSource.Freeze(); // Делаем доступным для других потоков
                    _iconCache.TryAdd(key, imageSource);
                    return imageSource;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}