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
        // Кэш иконок
        private static readonly ConcurrentDictionary<string, ImageSource> _iconCache = new ConcurrentDictionary<string, ImageSource>();

        public static ImageSource? GetIconByProcessName(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return null;
            if (processName.ToLower() == "unknown") return null;

            if (_iconCache.TryGetValue(processName, out var cached)) return cached;

            try
            {
                var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
                if (processes.Length > 0)
                {
                    try
                    {
                        var path = processes[0].MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                        {
                            return GetIconByPath(path, processName);
                        }
                    }
                    catch { }
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

                    imageSource.Freeze();
                    _iconCache.TryAdd(key, imageSource);
                    return imageSource;
                }
            }
            catch
            {
                return null;
            }
        }

        // Конвертация в Base64 для сохранения
        public static string? ImageSourceToBase64(ImageSource source)
        {
            if (source is BitmapSource bitmapSource)
            {
                try
                {
                    PngBitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                    using (MemoryStream ms = new MemoryStream())
                    {
                        encoder.Save(ms);
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
                catch { }
            }
            return null;
        }

        // Восстановление из Base64
        public static ImageSource? Base64ToImageSource(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad; // Важно загрузить в память
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze(); // Заморозить для использования в UI
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}