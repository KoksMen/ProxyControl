using System;
using System.IO;
using System.Net;

namespace ProxyControl.Services
{
    internal static class RoutingMatchHelper
    {
        public static bool HostMatches(string host, string pattern)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(pattern)) return false;
            if (pattern.Trim() == "*") return true;

            string normalizedHost = NormalizeHost(host);
            string normalizedPattern = NormalizeHost(pattern);
            if (normalizedHost.Length == 0 || normalizedPattern.Length == 0) return false;

            if (IPAddress.TryParse(normalizedPattern, out var patternIp))
            {
                return IPAddress.TryParse(normalizedHost, out var hostIp) && hostIp.Equals(patternIp);
            }

            if (normalizedPattern.StartsWith("*.", StringComparison.Ordinal))
            {
                normalizedPattern = normalizedPattern.Substring(2);
            }

            return string.Equals(normalizedHost, normalizedPattern, StringComparison.OrdinalIgnoreCase) ||
                   normalizedHost.EndsWith("." + normalizedPattern, StringComparison.OrdinalIgnoreCase);
        }

        public static bool AppMatches(string app, string pattern)
        {
            if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(pattern)) return false;
            if (pattern.Trim() == "*") return true;

            string appName = Path.GetFileNameWithoutExtension(app.Trim());
            string patternName = Path.GetFileNameWithoutExtension(pattern.Trim());
            return string.Equals(appName, patternName, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeHost(string value)
        {
            string normalized = value.Trim();
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) normalized = uri.Host;
            return normalized.Trim().TrimEnd('.').ToLowerInvariant();
        }
    }
}
