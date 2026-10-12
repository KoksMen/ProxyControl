using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using ProxyControl.Models;

namespace ProxyControl.Services
{
    /// <summary>
    /// High-performance compiled rule matching engine.
    /// Uses reverse domain character trie, IP lookup tables, and candidate order pruning.
    /// Preserves strict rule priority order (first match wins) with zero allocations on hot path.
    /// </summary>
    public sealed class FastRuleIndex
    {
        public static readonly FastRuleIndex Empty = new(Enumerable.Empty<TrafficRule>());

        private readonly DomainTrieNode _domainTrieRoot = new();
        private readonly Dictionary<IPAddress, List<CompiledRule>> _ipRules = new();
        private readonly List<CompiledRule> _globalHostRules = new();
        private readonly bool _hasAnyRules;

        public bool IsEmpty => !_hasAnyRules;

        public FastRuleIndex(IEnumerable<TrafficRule>? rules)
        {
            if (rules == null) return;

            int order = 0;
            foreach (var rule in rules)
            {
                if (!rule.IsEnabled)
                {
                    order++;
                    continue;
                }

                var compiled = new CompiledRule(rule, order);
                _hasAnyRules = true;

                bool isHostGlobal = rule.TargetHosts == null ||
                                    rule.TargetHosts.Count == 0 ||
                                    rule.TargetHosts.Any(h => h.Trim() == "*");

                if (isHostGlobal)
                {
                    _globalHostRules.Add(compiled);
                }

                if (rule.TargetHosts != null && rule.TargetHosts.Count > 0)
                {
                    foreach (var rawHost in rule.TargetHosts)
                    {
                        string hostPattern = rawHost.Trim();
                        if (string.IsNullOrEmpty(hostPattern) || hostPattern == "*") continue;

                        if (IPAddress.TryParse(hostPattern, out var ip))
                        {
                            if (!_ipRules.TryGetValue(ip, out var list))
                            {
                                list = new List<CompiledRule>();
                                _ipRules[ip] = list;
                            }
                            if (!list.Contains(compiled))
                            {
                                list.Add(compiled);
                            }
                        }
                        else
                        {
                            // Domain pattern
                            string domain = NormalizeDomainPattern(hostPattern);
                            if (!string.IsNullOrEmpty(domain))
                            {
                                InsertDomainPattern(domain, compiled);
                            }
                        }
                    }
                }

                order++;
            }

            // Ensure rules in lists are sorted by Order
            _globalHostRules.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (var list in _ipRules.Values)
            {
                list.Sort((a, b) => a.Order.CompareTo(b.Order));
            }
        }

        private static string NormalizeDomainPattern(string pattern)
        {
            string normalized = pattern.Trim();
            if (normalized.StartsWith("*.", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                normalized = uri.Host;
            }
            return normalized.Trim().TrimEnd('.').ToLowerInvariant();
        }

        private void InsertDomainPattern(string domain, CompiledRule rule)
        {
            DomainTrieNode current = _domainTrieRoot;
            // Insert in reverse character order so we can match domain suffixes
            for (int i = domain.Length - 1; i >= 0; i--)
            {
                char c = domain[i];
                if (current.Children == null)
                {
                    current.Children = new Dictionary<char, DomainTrieNode>();
                }
                if (!current.Children.TryGetValue(c, out var next))
                {
                    next = new DomainTrieNode();
                    current.Children[c] = next;
                }
                current = next;
            }

            if (current.Rules == null)
            {
                current.Rules = new List<CompiledRule>();
            }
            if (!current.Rules.Contains(rule))
            {
                current.Rules.Add(rule);
                current.Rules.Sort((a, b) => a.Order.CompareTo(b.Order));
            }
        }

        /// <summary>
        /// Matches a client connection against the rules.
        /// Finds the matching rule with the lowest Order index (first matching rule in user list).
        /// </summary>
        public bool Match(string app, string host, DateTime now, out TrafficRule? matchedRule)
        {
            matchedRule = null;
            if (!_hasAnyRules) return false;

            string cleanHost = CleanHost(host);
            if (string.IsNullOrEmpty(cleanHost)) return false;

            bool isTunApp = app.Contains("sing-box", StringComparison.OrdinalIgnoreCase) ||
                            app.Contains("System/TUN", StringComparison.OrdinalIgnoreCase);

            string normalizedAppName = isTunApp ? string.Empty : ExtractNormalizedAppName(app);

            int bestOrder = int.MaxValue;
            CompiledRule? bestMatch = null;

            // Check IP or Domain matches
            if (IPAddress.TryParse(cleanHost, out var hostIp))
            {
                if (_ipRules.TryGetValue(hostIp, out var ipCandidates))
                {
                    for (int i = 0; i < ipCandidates.Count; i++)
                    {
                        var candidate = ipCandidates[i];
                        if (candidate.Order >= bestOrder) break;

                        if (candidate.MatchesApp(isTunApp, normalizedAppName) && candidate.IsInSchedule(now))
                        {
                            bestOrder = candidate.Order;
                            bestMatch = candidate;
                            break; // Sorted by Order, no later rule can beat this one
                        }
                    }
                }
            }
            else
            {
                // Match domain trie
                MatchDomainTrie(cleanHost, isTunApp, normalizedAppName, now, ref bestOrder, ref bestMatch);
            }

            // Check global host rules (rules that match any host)
            for (int i = 0; i < _globalHostRules.Count; i++)
            {
                var candidate = _globalHostRules[i];
                if (candidate.Order >= bestOrder) break; // Sorted by Order, cannot beat bestOrder

                if (candidate.MatchesApp(isTunApp, normalizedAppName) && candidate.IsInSchedule(now))
                {
                    bestOrder = candidate.Order;
                    bestMatch = candidate;
                    break; // Sorted by Order, no subsequent global rule can beat this
                }
            }

            if (bestMatch != null)
            {
                matchedRule = bestMatch.Rule;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Matches a DNS request against rules applicable to DNS (global app scope).
        /// </summary>
        public bool MatchDns(string host, DateTime now, out TrafficRule? matchedRule)
        {
            matchedRule = null;
            if (!_hasAnyRules) return false;

            string cleanHost = CleanHost(host);
            if (string.IsNullOrEmpty(cleanHost)) return false;

            int bestOrder = int.MaxValue;
            CompiledRule? bestMatch = null;

            if (IPAddress.TryParse(cleanHost, out var hostIp))
            {
                if (_ipRules.TryGetValue(hostIp, out var ipCandidates))
                {
                    for (int i = 0; i < ipCandidates.Count; i++)
                    {
                        var candidate = ipCandidates[i];
                        if (candidate.Order >= bestOrder) break;

                        if (candidate.IsAppGlobal && candidate.IsInSchedule(now))
                        {
                            bestOrder = candidate.Order;
                            bestMatch = candidate;
                            break;
                        }
                    }
                }
            }
            else
            {
                MatchDomainTrieDns(cleanHost, now, ref bestOrder, ref bestMatch);
            }

            // Check global host rules
            for (int i = 0; i < _globalHostRules.Count; i++)
            {
                var candidate = _globalHostRules[i];
                if (candidate.Order >= bestOrder) break;

                if (candidate.IsAppGlobal && candidate.IsInSchedule(now))
                {
                    bestOrder = candidate.Order;
                    bestMatch = candidate;
                    break;
                }
            }

            if (bestMatch != null)
            {
                matchedRule = bestMatch.Rule;
                return true;
            }

            return false;
        }

        private void MatchDomainTrie(string host, bool isTunApp, string normalizedAppName, DateTime now, ref int bestOrder, ref CompiledRule? bestMatch)
        {
            DomainTrieNode current = _domainTrieRoot;

            for (int i = host.Length - 1; i >= 0; i--)
            {
                char c = char.ToLowerInvariant(host[i]);
                if (current.Children == null || !current.Children.TryGetValue(c, out current))
                {
                    break;
                }

                // Domain boundary: either start of string (exact match) or preceded by '.' (subdomain match)
                if (i == 0 || host[i - 1] == '.')
                {
                    if (current.Rules != null)
                    {
                        for (int r = 0; r < current.Rules.Count; r++)
                        {
                            var candidate = current.Rules[r];
                            if (candidate.Order >= bestOrder) break;

                            if (candidate.MatchesApp(isTunApp, normalizedAppName) && candidate.IsInSchedule(now))
                            {
                                bestOrder = candidate.Order;
                                bestMatch = candidate;
                                if (bestOrder == 0) return; // Cannot beat Order 0
                                break;
                            }
                        }
                    }
                }
            }
        }

        private void MatchDomainTrieDns(string host, DateTime now, ref int bestOrder, ref CompiledRule? bestMatch)
        {
            DomainTrieNode current = _domainTrieRoot;

            for (int i = host.Length - 1; i >= 0; i--)
            {
                char c = char.ToLowerInvariant(host[i]);
                if (current.Children == null || !current.Children.TryGetValue(c, out current))
                {
                    break;
                }

                if (i == 0 || host[i - 1] == '.')
                {
                    if (current.Rules != null)
                    {
                        for (int r = 0; r < current.Rules.Count; r++)
                        {
                            var candidate = current.Rules[r];
                            if (candidate.Order >= bestOrder) break;

                            if (candidate.IsAppGlobal && candidate.IsInSchedule(now))
                            {
                                bestOrder = candidate.Order;
                                bestMatch = candidate;
                                if (bestOrder == 0) return;
                                break;
                            }
                        }
                    }
                }
            }
        }

        private static string ExtractNormalizedAppName(string app)
        {
            if (string.IsNullOrWhiteSpace(app)) return string.Empty;
            string trimmed = app.Trim();
            return Path.GetFileNameWithoutExtension(trimmed).ToLowerInvariant();
        }

        private static string CleanHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return string.Empty;
            string trimmed = host.Trim();
            if (trimmed.StartsWith("[") && trimmed.Contains("]"))
            {
                int close = trimmed.IndexOf(']');
                return trimmed.Substring(1, close - 1);
            }
            int colon = trimmed.IndexOf(':');
            if (colon > 0 && trimmed.IndexOf(':', colon + 1) < 0)
            {
                trimmed = trimmed.Substring(0, colon);
            }
            return trimmed.TrimEnd('.');
        }

        private sealed class DomainTrieNode
        {
            public Dictionary<char, DomainTrieNode>? Children;
            public List<CompiledRule>? Rules;
        }

        private sealed class CompiledRule
        {
            public TrafficRule Rule { get; }
            public int Order { get; }
            public bool IsAppGlobal { get; }
            public bool AllowsTun { get; }
            private readonly HashSet<string>? _targetApps;
            private readonly bool _isScheduleEnabled;
            private readonly DayOfWeek[]? _scheduleDays;
            private readonly TimeSpan? _scheduleStart;
            private readonly TimeSpan? _scheduleEnd;

            public CompiledRule(TrafficRule rule, int order)
            {
                Rule = rule;
                Order = order;

                if (rule.TargetApps == null || rule.TargetApps.Count == 0)
                {
                    IsAppGlobal = true;
                    AllowsTun = false;
                }
                else
                {
                    bool hasGlobalWildcard = false;
                    bool hasTun = false;
                    var appSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var rawApp in rule.TargetApps)
                    {
                        string appName = rawApp.Trim();
                        if (appName == "*")
                        {
                            hasGlobalWildcard = true;
                        }
                        if (appName.Contains("TUN", StringComparison.OrdinalIgnoreCase))
                        {
                            hasTun = true;
                        }
                        string norm = Path.GetFileNameWithoutExtension(appName).ToLowerInvariant();
                        if (!string.IsNullOrEmpty(norm))
                        {
                            appSet.Add(norm);
                        }
                    }

                    IsAppGlobal = hasGlobalWildcard;
                    AllowsTun = hasTun || hasGlobalWildcard;
                    _targetApps = appSet;
                }

                _isScheduleEnabled = rule.IsScheduleEnabled;
                _scheduleDays = rule.ScheduleDays;
                _scheduleStart = rule.ScheduleStart;
                _scheduleEnd = rule.ScheduleEnd;
            }

            public bool MatchesApp(bool isTunApp, string normalizedAppName)
            {
                if (isTunApp) return AllowsTun;
                if (IsAppGlobal) return true;
                return _targetApps != null && _targetApps.Contains(normalizedAppName);
            }

            public bool IsInSchedule(DateTime now)
            {
                if (!_isScheduleEnabled) return true;

                if (_scheduleDays != null && _scheduleDays.Length > 0)
                {
                    bool dayMatches = false;
                    for (int i = 0; i < _scheduleDays.Length; i++)
                    {
                        if (_scheduleDays[i] == now.DayOfWeek)
                        {
                            dayMatches = true;
                            break;
                        }
                    }
                    if (!dayMatches) return false;
                }

                if (_scheduleStart.HasValue && _scheduleEnd.HasValue)
                {
                    var time = now.TimeOfDay;
                    var start = _scheduleStart.Value;
                    var end = _scheduleEnd.Value;

                    if (start <= end)
                    {
                        if (time < start || time > end) return false;
                    }
                    else
                    {
                        if (time < start && time > end) return false;
                    }
                }

                return true;
            }
        }
    }
}
