using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProxyControl.Services
{
    /// <summary>
    /// Loads site icons outside the proxy pipeline and keeps them in a persistent local cache.
    /// Network work is deliberately serialized so opening a large rules list cannot create a burst.
    /// </summary>
    public sealed class SiteIconCacheService : IDisposable
    {
        private const int MaxIconBytes = 256 * 1024;
        private static readonly TimeSpan MissingCacheLifetime = TimeSpan.FromDays(7);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

        private readonly string _cacheDirectory;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _networkGate = new(1, 1);
        private readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> _loads =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _shutdown = new();
        private DateTime _lastNetworkRequestUtc = DateTime.MinValue;

        public SiteIconCacheService()
        {
            _cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProxyManagerApp",
                "Favicons");

            try { Directory.CreateDirectory(_cacheDirectory); }
            catch { }

            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.All
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = RequestTimeout
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyControl/2.8");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("image/*");
        }

        public async Task<ImageSource?> GetFirstIconAsync(IEnumerable<string>? targets)
        {
            if (targets == null)
                return null;

            foreach (string host in targets
                .Select(NormalizeHost)
                .Where(host => host != null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3))
            {
                foreach (string candidateHost in GetIconHostCandidates(host))
                {
                    ImageSource? icon = await GetIconAsync(candidateHost).ConfigureAwait(false);
                    if (icon != null)
                        return icon;
                }
            }

            return null;
        }

        private static IEnumerable<string> GetIconHostCandidates(string host)
        {
            yield return host;

            string[] labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length <= 2)
                yield break;

            // Do not turn example.co.uk into the public suffix co.uk.
            bool looksLikeSecondLevelCountryDomain =
                labels.Length == 3 &&
                labels[^1].Length == 2 &&
                labels[^2].Length <= 3;
            if (!looksLikeSecondLevelCountryDomain)
                yield return string.Join('.', labels.Skip(1));
        }

        private Task<ImageSource?> GetIconAsync(string host)
        {
            var load = _loads.GetOrAdd(
                host,
                static (key, service) => new Lazy<Task<ImageSource?>>(
                    () => service.LoadIconAsync(key),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                this);
            return load.Value;
        }

        private async Task<ImageSource?> LoadIconAsync(string host)
        {
            string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host)));
            string iconPath = Path.Combine(_cacheDirectory, cacheKey + ".img");
            string missingPath = Path.Combine(_cacheDirectory, cacheKey + ".missing");

            ImageSource? cached = await ReadCachedImageAsync(iconPath).ConfigureAwait(false);
            if (cached != null)
                return cached;

            try
            {
                if (File.Exists(missingPath) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(missingPath) < MissingCacheLifetime)
                    return null;
            }
            catch { }

            await _networkGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                TimeSpan sinceLastRequest = DateTime.UtcNow - _lastNetworkRequestUtc;
                TimeSpan minimumGap = TimeSpan.FromMilliseconds(250);
                if (sinceLastRequest < minimumGap)
                    await Task.Delay(minimumGap - sinceLastRequest, _shutdown.Token).ConfigureAwait(false);

                foreach (string scheme in new[] { "https", "http" })
                {
                    _lastNetworkRequestUtc = DateTime.UtcNow;
                    byte[]? bytes = await DownloadIconAsync(
                        new Uri($"{scheme}://{host}/favicon.ico"),
                        _shutdown.Token).ConfigureAwait(false);

                    ImageSource? image = CreateImage(bytes);
                    if (image == null)
                        continue;

                    await WriteCacheAsync(iconPath, bytes!).ConfigureAwait(false);
                    TryDelete(missingPath);
                    return image;
                }

                try
                {
                    await File.WriteAllTextAsync(missingPath, string.Empty, _shutdown.Token)
                        .ConfigureAwait(false);
                }
                catch { }
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                _networkGate.Release();
            }
        }

        private async Task<byte[]?> DownloadIconAsync(Uri uri, CancellationToken token)
        {
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentLength is long contentLength &&
                    contentLength > MaxIconBytes)
                    return null;

                await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var destination = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int total = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

                    total += read;
                    if (total > MaxIconBytes)
                        return null;
                    destination.Write(buffer, 0, read);
                }

                return total == 0 ? null : destination.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static async Task<ImageSource?> ReadCachedImageAsync(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                return CreateImage(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            }
            catch
            {
                TryDelete(path);
                return null;
            }
        }

        private static ImageSource? CreateImage(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 32;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private async Task WriteCacheAsync(string path, byte[] bytes)
        {
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                string temporaryPath = path + ".tmp";
                await File.WriteAllBytesAsync(temporaryPath, bytes, _shutdown.Token).ConfigureAwait(false);
                File.Move(temporaryPath, path, overwrite: true);
            }
            catch { }
        }

        private static string? NormalizeHost(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return null;

            string value = target.Trim();
            while (value.StartsWith("*.", StringComparison.Ordinal))
                value = value[2..];

            if (value == "*" ||
                value.Contains('*') ||
                value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                Uri.TryCreate("https://" + value, UriKind.Absolute, out uri);

            string? host = uri?.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(host) ||
                !host.Contains('.') ||
                IPAddress.TryParse(host, out _))
                return null;

            return host;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _httpClient.Dispose();
        }
    }
}
