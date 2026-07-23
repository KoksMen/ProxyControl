using ProxyControl.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyControl.Services
{
    public sealed class DnsHealthResult
    {
        public bool IsOnline { get; init; }
        public long LatencyMs { get; init; }
        public string Details { get; init; } = string.Empty;
    }

    public static class DnsHealthCheckService
    {
        public static async Task<DnsHealthResult> CheckAsync(AppConfig config, bool fallback, CancellationToken token)
        {
            try
            {
                var query = BuildQuery(out var queryId);
                var stopwatch = Stopwatch.StartNew();
                byte[]? response;
                string transport;

                if (config.EnableDoh && (!fallback || config.EnableDohFallback))
                {
                    var endpointFound = fallback
                        ? DnsOverHttpsClient.TryGetFallbackEndpoint(config, out var endpoint, out var error)
                        : DnsOverHttpsClient.TryGetEndpoint(config, out endpoint, out error);
                    if (!endpointFound)
                    {
                        return Offline(error);
                    }

                    response = await DnsOverHttpsClient.QueryAsync(query, endpoint, token);
                    transport = "DoH";
                }
                else
                {
                    var host = fallback ? config.DnsFallbackHost : config.DnsHost;
                    if (string.IsNullOrWhiteSpace(host)) return Offline("DNS address is empty");

                    var address = await DnsOverHttpsClient.ResolveHostAsync(NormalizeHost(host), token);
                    if (address == null) return Offline("DNS address could not be resolved");

                    using var udp = new UdpClient(address.AddressFamily);
                    udp.Connect(new IPEndPoint(address, 53));
                    await udp.SendAsync(query, token);
                    response = (await udp.ReceiveAsync(token)).Buffer;
                    transport = "UDP";
                }

                stopwatch.Stop();
                if (!IsValidResponse(response, queryId))
                {
                    return Offline("Invalid DNS response");
                }

                return new DnsHealthResult
                {
                    IsOnline = true,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Details = $"{transport}, {stopwatch.ElapsedMilliseconds} ms"
                };
            }
            catch (OperationCanceledException)
            {
                return Offline("Request timed out");
            }
            catch (Exception ex)
            {
                return Offline(ex.Message);
            }
        }

        private static string NormalizeHost(string value)
        {
            var trimmed = value.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return uri.Host;
            return trimmed.Trim('[', ']');
        }

        private static DnsHealthResult Offline(string details) => new()
        {
            IsOnline = false,
            Details = details
        };

        private static bool IsValidResponse(byte[]? response, ushort queryId)
        {
            if (response == null || response.Length < 12) return false;
            var responseId = (ushort)((response[0] << 8) | response[1]);
            var isResponse = (response[2] & 0x80) != 0;
            var responseCode = response[3] & 0x0F;
            return responseId == queryId && isResponse && responseCode == 0;
        }

        private static byte[] BuildQuery(out ushort queryId)
        {
            queryId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var query = new List<byte>(64);
            WriteUInt16(query, queryId);
            WriteUInt16(query, 0x0100);
            WriteUInt16(query, 1);
            WriteUInt16(query, 0);
            WriteUInt16(query, 0);
            WriteUInt16(query, 0);

            foreach (var label in "example.com".Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                query.Add((byte)bytes.Length);
                query.AddRange(bytes);
            }

            query.Add(0);
            WriteUInt16(query, 1);
            WriteUInt16(query, 1);
            return query.ToArray();
        }

        private static void WriteUInt16(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }
    }
}
