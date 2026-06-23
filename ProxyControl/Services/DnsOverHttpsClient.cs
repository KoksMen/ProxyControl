using ProxyControl.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyControl.Services
{
    public static class DnsOverHttpsClient
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly ConcurrentDictionary<string, DohLookupResult> WindowsDohCache = new();
        private static readonly ConcurrentDictionary<string, DohLookupResult> DdrCache = new();
        private const ushort DnsTypeSvcb = 64;
        private const ushort DnsTypeHttps = 65;
        private const ushort SvcbParamDohPath = 7;

        private static readonly IReadOnlyDictionary<DohProviderType, string> ProviderEndpoints =
            new Dictionary<DohProviderType, string>
            {
                [DohProviderType.Google] = "https://dns.google/dns-query",
                [DohProviderType.Cloudflare] = "https://cloudflare-dns.com/dns-query",
                [DohProviderType.OpenDNS] = "https://doh.opendns.com/dns-query"
            };

        private static readonly IReadOnlyDictionary<string, string> KnownDnsHostEndpoints =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["8.8.8.8"] = "https://dns.google/dns-query",
                ["8.8.4.4"] = "https://dns.google/dns-query",
                ["2001:4860:4860::8888"] = "https://dns.google/dns-query",
                ["2001:4860:4860::8844"] = "https://dns.google/dns-query",
                ["dns.google"] = "https://dns.google/dns-query",

                ["1.1.1.1"] = "https://cloudflare-dns.com/dns-query",
                ["1.0.0.1"] = "https://cloudflare-dns.com/dns-query",
                ["2606:4700:4700::1111"] = "https://cloudflare-dns.com/dns-query",
                ["2606:4700:4700::1001"] = "https://cloudflare-dns.com/dns-query",
                ["cloudflare-dns.com"] = "https://cloudflare-dns.com/dns-query",
                ["dns.cloudflare.com"] = "https://cloudflare-dns.com/dns-query",

                ["9.9.9.9"] = "https://dns.quad9.net/dns-query",
                ["149.112.112.112"] = "https://dns.quad9.net/dns-query",
                ["2620:fe::fe"] = "https://dns.quad9.net/dns-query",
                ["2620:fe::9"] = "https://dns.quad9.net/dns-query",
                ["dns.quad9.net"] = "https://dns.quad9.net/dns-query",

                ["208.67.222.222"] = "https://doh.opendns.com/dns-query",
                ["208.67.220.220"] = "https://doh.opendns.com/dns-query",
                ["208.67.222.220"] = "https://doh.opendns.com/dns-query",
                ["208.67.220.222"] = "https://doh.opendns.com/dns-query",
                ["doh.opendns.com"] = "https://doh.opendns.com/dns-query",
                ["dns.opendns.com"] = "https://doh.opendns.com/dns-query"
            };

        private static readonly IReadOnlyDictionary<string, string> KnownDnsHostAddresses =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dns.google"] = "8.8.8.8",
                ["cloudflare-dns.com"] = "1.1.1.1",
                ["dns.cloudflare.com"] = "1.1.1.1",
                ["dns.quad9.net"] = "9.9.9.9",
                ["doh.opendns.com"] = "208.67.222.222",
                ["dns.opendns.com"] = "208.67.222.222"
            };

        private static readonly IReadOnlyDictionary<string, string[]> KnownTemplateHosts =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["dns.google"] = new[] { "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844" },
                ["cloudflare-dns.com"] = new[] { "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001" },
                ["dns.quad9.net"] = new[] { "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9" },
                ["doh.opendns.com"] = new[] { "208.67.222.222", "208.67.220.220", "208.67.222.220", "208.67.220.222" }
            };

        private readonly record struct DohLookupResult(bool Found, string Endpoint, string Error, DateTime CheckedUtc);

        public sealed class WindowsDohServer
        {
            public string ServerAddress { get; }
            public string DohTemplate { get; }
            public bool EnableDoh { get; }
            public bool IsAutoDetected { get; }
            public bool ShouldApplyTemplate { get; }

            public WindowsDohServer(string serverAddress, string dohTemplate, bool enableDoh, bool isAutoDetected, bool shouldApplyTemplate = false)
            {
                ServerAddress = serverAddress;
                DohTemplate = dohTemplate;
                EnableDoh = enableDoh;
                IsAutoDetected = isAutoDetected;
                ShouldApplyTemplate = shouldApplyTemplate;
            }
        }

        public static string GetDefaultEndpoint(DohProviderType provider)
        {
            return ProviderEndpoints.TryGetValue(provider, out var endpoint)
                ? endpoint
                : ProviderEndpoints[DohProviderType.Cloudflare];
        }

        public static bool TryGetEndpointForDnsHost(string? dnsHost, out string endpoint, out string error)
        {
            return TryGetEndpointForDnsHost(dnsHost, out endpoint, out error, out _);
        }

        public static bool TryGetEndpointForDnsHost(string? dnsHost, out string endpoint, out string error, out bool shouldApplyWindowsTemplate)
        {
            endpoint = string.Empty;
            error = string.Empty;
            shouldApplyWindowsTemplate = false;

            if (string.IsNullOrWhiteSpace(dnsHost))
            {
                error = "DNS host is empty.";
                return false;
            }

            var normalized = dnsHost.Trim().TrimEnd('.');
            if (IPAddress.TryParse(normalized, out var parsedAddress))
            {
                normalized = parsedAddress.ToString();
                if (KnownDnsHostEndpoints.TryGetValue(normalized, out var knownIpEndpoint))
                {
                    endpoint = knownIpEndpoint;
                    shouldApplyWindowsTemplate = true;
                    return true;
                }

                var ddrResult = GetDdrDohEndpoint(parsedAddress);
                if (ddrResult.Found)
                {
                    endpoint = ddrResult.Endpoint;
                    shouldApplyWindowsTemplate = true;
                    return true;
                }

                var result = GetWindowsDohEndpoint(parsedAddress.ToString());
                if (result.Found)
                {
                    endpoint = result.Endpoint;
                    return true;
                }

                error = "Windows did not return a DoH template for this DNS IP. Set DoH manually.";
                return false;
            }

            if (KnownDnsHostEndpoints.TryGetValue(normalized, out var knownEndpoint))
            {
                endpoint = knownEndpoint;
                return true;
            }

            return TryNormalizeEndpoint(normalized, out endpoint, out error);
        }

        private static DohLookupResult GetWindowsDohEndpoint(string serverAddress)
        {
            if (WindowsDohCache.TryGetValue(serverAddress, out var cached))
            {
                var ttl = cached.Found ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(5);
                if (DateTime.UtcNow - cached.CheckedUtc < ttl)
                {
                    return cached;
                }
            }

            var result = LookupWindowsDohEndpoint(serverAddress);
            WindowsDohCache[serverAddress] = result;
            return result;
        }

        private static DohLookupResult LookupWindowsDohEndpoint(string serverAddress)
        {
            try
            {
                using var process = new Process();
                process.StartInfo.FileName = "powershell.exe";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.ArgumentList.Add("-NoProfile");
                process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
                process.StartInfo.ArgumentList.Add("Bypass");
                process.StartInfo.ArgumentList.Add("-Command");
                process.StartInfo.ArgumentList.Add(
                    $"Get-DnsClientDohServerAddress -ServerAddress '{serverAddress}' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty DohTemplate");

                process.Start();

                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    return new DohLookupResult(false, string.Empty, "Windows DoH lookup timed out.", DateTime.UtcNow);
                }

                var output = process.StandardOutput.ReadToEnd().Trim();
                if (TryNormalizeEndpoint(output, out var endpoint, out _))
                {
                    return new DohLookupResult(true, endpoint, string.Empty, DateTime.UtcNow);
                }
            }
            catch
            {
                // Fall through to DNS-host based automatic endpoint construction.
            }

            return new DohLookupResult(false, string.Empty, "Windows DoH lookup did not return an endpoint.", DateTime.UtcNow);
        }

        public static bool TryGetWindowsDohServers(AppConfig config, out IReadOnlyList<WindowsDohServer> servers, out string error)
        {
            var result = new List<WindowsDohServer>();
            error = string.Empty;

            if (TryGetWindowsDohServer(config.DnsHost, true, config.AutoDetectDohEndpoint, config.DohEndpoint, out var primary, out var primaryError))
            {
                result.Add(primary);
            }
            else
            {
                error = primaryError;
            }

            if (TryGetWindowsDohServer(config.DnsFallbackHost, config.EnableDohFallback, config.AutoDetectDohFallbackEndpoint, config.DohFallbackEndpoint, out var fallback, out _)
                && !result.Any(x => string.Equals(x.ServerAddress, fallback.ServerAddress, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(fallback);
            }

            servers = result;
            return result.Count > 0;
        }

        public static bool TryGetWindowsDohServer(
            string? dnsHost,
            bool enableDoh,
            bool autoDetectEndpoint,
            string? manualEndpoint,
            out WindowsDohServer server,
            out string error)
        {
            server = new WindowsDohServer(string.Empty, string.Empty, false, autoDetectEndpoint);
            error = string.Empty;

            if (!TryGetWindowsDnsServerAddress(dnsHost, out var serverAddress, out error))
            {
                return false;
            }

            if (!enableDoh)
            {
                server = new WindowsDohServer(serverAddress, string.Empty, false, autoDetectEndpoint);
                return true;
            }

            if (autoDetectEndpoint)
            {
                TryGetEndpointForDnsHost(serverAddress, out var autoEndpoint, out _, out var shouldApplyTemplate);
                server = new WindowsDohServer(serverAddress, autoEndpoint, true, true, shouldApplyTemplate);
                return true;
            }

            if (!TryNormalizeEndpoint(manualEndpoint, out var endpoint, out error))
            {
                return false;
            }

            server = new WindowsDohServer(serverAddress, endpoint, true, false);
            return true;
        }

        public static bool TryGetWindowsDnsServerAddress(string? dnsHost, out string serverAddress, out string error)
        {
            serverAddress = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(dnsHost))
            {
                error = "DNS host is empty.";
                return false;
            }

            var normalized = dnsHost.Trim().TrimEnd('.');
            if (IPAddress.TryParse(normalized, out var parsedAddress))
            {
                serverAddress = parsedAddress.ToString();
                return true;
            }

            if (KnownDnsHostAddresses.TryGetValue(normalized, out var knownAddress))
            {
                serverAddress = knownAddress;
                return true;
            }

            try
            {
                var addresses = Dns.GetHostAddresses(normalized)
                    .Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .ToArray();
                if (addresses.Length > 0)
                {
                    serverAddress = addresses[0].ToString();
                    return true;
                }
            }
            catch
            {
            }

            error = "DNS host could not be resolved to an IPv4 DNS server address.";
            return false;
        }

        public static bool IsKnownDohTemplateCompatible(string serverAddress, string dohTemplate)
        {
            if (!TryNormalizeEndpoint(dohTemplate, out var normalizedTemplate, out _) ||
                !Uri.TryCreate(normalizedTemplate, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!KnownTemplateHosts.TryGetValue(uri.Host, out var serverAddresses))
            {
                return true;
            }

            if (!IPAddress.TryParse(serverAddress, out var parsedAddress))
            {
                return false;
            }

            var normalizedAddress = parsedAddress.ToString();
            return serverAddresses.Any(x => string.Equals(x, normalizedAddress, StringComparison.OrdinalIgnoreCase));
        }

        public static bool TryNormalizeEndpoint(string? endpoint, out string normalized, out string error)
        {
            normalized = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                error = "DoH endpoint is empty.";
                return false;
            }

            var trimmed = NormalizeTemplate(endpoint.Trim());
            if (!trimmed.Contains("://", StringComparison.Ordinal))
            {
                trimmed = $"https://{trimmed.TrimEnd('/')}/dns-query";
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                error = "DoH endpoint must be a valid URL.";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "DoH endpoint must use HTTPS.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "DoH endpoint must contain a host.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "DoH endpoint must not contain credentials.";
                return false;
            }

            normalized = uri.ToString();
            return true;
        }

        private static string NormalizeTemplate(string endpoint)
        {
            var templateIndex = endpoint.IndexOf('{');
            if (templateIndex >= 0)
            {
                endpoint = endpoint.Substring(0, templateIndex);
            }

            return endpoint.Trim();
        }

        private static DohLookupResult GetDdrDohEndpoint(IPAddress serverAddress)
        {
            var cacheKey = serverAddress.ToString();
            if (DdrCache.TryGetValue(cacheKey, out var cached))
            {
                var ttl = cached.Found ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(10);
                if (DateTime.UtcNow - cached.CheckedUtc < ttl)
                {
                    return cached;
                }
            }

            var result = LookupDdrDohEndpoint(serverAddress);
            DdrCache[cacheKey] = result;
            return result;
        }

        private static DohLookupResult LookupDdrDohEndpoint(IPAddress serverAddress)
        {
            if (serverAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                return new DohLookupResult(false, string.Empty, "DDR lookup supports IPv4 DNS servers only.", DateTime.UtcNow);
            }

            try
            {
                var query = BuildDdrQuery();
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 1000;
                udp.Connect(serverAddress, 53);
                udp.Send(query, query.Length);

                var remote = new IPEndPoint(IPAddress.Any, 0);
                var response = udp.Receive(ref remote);
                if (TryParseDdrResponse(response, out var endpoint) &&
                    TryNormalizeEndpoint(endpoint, out var normalizedEndpoint, out _))
                {
                    return new DohLookupResult(true, normalizedEndpoint, string.Empty, DateTime.UtcNow);
                }
            }
            catch
            {
            }

            return new DohLookupResult(false, string.Empty, "DDR lookup did not return a DoH endpoint.", DateTime.UtcNow);
        }

        private static byte[] BuildDdrQuery()
        {
            var query = new List<byte>(64);
            var id = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
            WriteUInt16(query, id);
            WriteUInt16(query, 0x0100);
            WriteUInt16(query, 1);
            WriteUInt16(query, 0);
            WriteUInt16(query, 0);
            WriteUInt16(query, 0);

            foreach (var label in new[] { "_dns", "resolver", "arpa" })
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                query.Add((byte)bytes.Length);
                query.AddRange(bytes);
            }

            query.Add(0);
            WriteUInt16(query, DnsTypeSvcb);
            WriteUInt16(query, 1);
            return query.ToArray();
        }

        private static bool TryParseDdrResponse(byte[] message, out string endpoint)
        {
            endpoint = string.Empty;
            if (message.Length < 12)
            {
                return false;
            }

            var offset = 4;
            var qdCount = ReadUInt16(message, ref offset);
            var anCount = ReadUInt16(message, ref offset);
            offset += 4;

            for (var i = 0; i < qdCount; i++)
            {
                if (!TryReadDnsName(message, ref offset, out _))
                {
                    return false;
                }

                offset += 4;
            }

            for (var i = 0; i < anCount; i++)
            {
                if (!TryReadDnsName(message, ref offset, out _))
                {
                    return false;
                }

                if (offset + 10 > message.Length)
                {
                    return false;
                }

                var type = ReadUInt16(message, ref offset);
                offset += 6;
                var rdLength = ReadUInt16(message, ref offset);
                var rdataEnd = offset + rdLength;
                if (rdataEnd > message.Length)
                {
                    return false;
                }

                if ((type == DnsTypeSvcb || type == DnsTypeHttps) &&
                    TryParseSvcbRecord(message, offset, rdataEnd, out endpoint))
                {
                    return true;
                }

                offset = rdataEnd;
            }

            return false;
        }

        private static bool TryParseSvcbRecord(byte[] message, int offset, int rdataEnd, out string endpoint)
        {
            endpoint = string.Empty;
            if (offset + 2 > rdataEnd)
            {
                return false;
            }

            offset += 2;
            if (!TryReadDnsName(message, ref offset, out var targetName))
            {
                return false;
            }

            var dohPath = string.Empty;
            while (offset + 4 <= rdataEnd)
            {
                var key = ReadUInt16(message, ref offset);
                var length = ReadUInt16(message, ref offset);
                if (offset + length > rdataEnd)
                {
                    return false;
                }

                if (key == SvcbParamDohPath)
                {
                    dohPath = Encoding.UTF8.GetString(message, offset, length);
                }

                offset += length;
            }

            targetName = targetName.TrimEnd('.');
            if (string.IsNullOrWhiteSpace(targetName))
            {
                return false;
            }

            dohPath = string.IsNullOrWhiteSpace(dohPath) ? "/dns-query" : NormalizeTemplate(dohPath);
            if (!dohPath.StartsWith("/", StringComparison.Ordinal))
            {
                dohPath = "/" + dohPath;
            }

            endpoint = $"https://{targetName}{dohPath}";
            return true;
        }

        private static bool TryReadDnsName(byte[] message, ref int offset, out string name)
        {
            name = string.Empty;
            var labels = new List<string>();
            var current = offset;
            var jumped = false;
            var jumps = 0;

            while (current < message.Length)
            {
                var length = message[current++];
                if (length == 0)
                {
                    if (!jumped)
                    {
                        offset = current;
                    }

                    name = string.Join(".", labels);
                    return true;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (current >= message.Length || ++jumps > 8)
                    {
                        return false;
                    }

                    var pointer = ((length & 0x3F) << 8) | message[current++];
                    if (!jumped)
                    {
                        offset = current;
                    }

                    current = pointer;
                    jumped = true;
                    continue;
                }

                if ((length & 0xC0) != 0 || current + length > message.Length)
                {
                    return false;
                }

                labels.Add(Encoding.ASCII.GetString(message, current, length));
                current += length;
            }

            return false;
        }

        private static ushort ReadUInt16(byte[] message, ref int offset)
        {
            var value = (ushort)((message[offset] << 8) | message[offset + 1]);
            offset += 2;
            return value;
        }

        private static void WriteUInt16(List<byte> message, ushort value)
        {
            message.Add((byte)(value >> 8));
            message.Add((byte)(value & 0xFF));
        }

        public static bool TryGetEndpoint(AppConfig config, out string endpoint, out string error)
        {
            if (config.AutoDetectDohEndpoint)
            {
                return TryGetEndpointForDnsHost(config.DnsHost, out endpoint, out error);
            }

            var candidate = config.DohEndpoint;

            return TryNormalizeEndpoint(candidate, out endpoint, out error);
        }

        public static bool TryGetFallbackEndpoint(AppConfig config, out string endpoint, out string error)
        {
            if (config.AutoDetectDohFallbackEndpoint)
            {
                return TryGetEndpointForDnsHost(config.DnsFallbackHost, out endpoint, out error);
            }

            return TryNormalizeEndpoint(config.DohFallbackEndpoint, out endpoint, out error);
        }

        public static async Task<byte[]?> QueryAsync(byte[] dnsQuery, string endpoint, CancellationToken token)
        {
            using var content = new ByteArrayContent(dnsQuery);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
            request.Content = content;

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(token);
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(4)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyControl/1.0");
            return client;
        }
    }
}
