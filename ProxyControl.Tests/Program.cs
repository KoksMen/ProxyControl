using ProxyControl.Services;
using ProxyControl.Models;
using System.Text.Json;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var createArgs = SettingsService.BuildAutoStartTaskCreateArguments(@"C:\Apps\Proxy Control\ProxyControl.exe");
Assert(createArgs.Contains("/Create"), "Autostart must create a scheduled task.");
Assert(createArgs.Contains("/RL HIGHEST"), "Autostart scheduled task must request highest privileges.");
Assert(createArgs.Contains("/SC ONLOGON"), "Autostart scheduled task must run at user logon.");
Assert(createArgs.Contains("\\\"C:\\Apps\\Proxy Control\\ProxyControl.exe\\\" --autostart"), "Autostart command must quote the executable path and pass --autostart.");

var deleteArgs = SettingsService.BuildAutoStartTaskDeleteArguments();
Assert(deleteArgs.Contains("/Delete"), "Disabling autostart must delete the scheduled task.");

var legacyRule = JsonSerializer.Deserialize<TrafficRule>(
    """{"IsEnabled":true,"TargetApps":["chrome"],"TargetHosts":["example.com"]}""");
Assert(legacyRule?.TrafficType == RuleTrafficType.Any,
    "Rules saved before protocol support must remain compatible and match any traffic.");

var dnsMatch = new Dictionary<string, object>();
bool dnsHasMatch = false;
TunService.ApplyTrafficTypeMatch(dnsMatch, RuleTrafficType.DNS, ref dnsHasMatch);
Assert(dnsHasMatch && Convert.ToInt32(dnsMatch["port"]) == 53,
    "A DNS rule must produce a real port 53 TUN match.");

var webRtcMatch = new Dictionary<string, object>();
bool webRtcHasMatch = false;
TunService.ApplyTrafficTypeMatch(webRtcMatch, RuleTrafficType.WebRTC, ref webRtcHasMatch);
Assert(webRtcHasMatch && Equals(webRtcMatch["network"], "udp"),
    "A WebRTC rule must produce a UDP TUN match.");
Assert(((string[])webRtcMatch["port_range"]).Contains("3478"),
    "A WebRTC rule must include the standard STUN port.");

var mutableTunConfig = new TunService.TunRulesConfig
{
    Mode = RuleMode.WhiteList,
    Rules = new List<TrafficRule>
    {
        new()
        {
            IsEnabled = true,
            Action = RuleAction.Proxy,
            TrafficType = RuleTrafficType.HTTPS,
            TargetApps = new List<string> { "browser.exe" },
            TargetHosts = new List<string> { "example.com" }
        }
    }
};
var tunSnapshot = TunService.CreateSnapshot(mutableTunConfig);
mutableTunConfig.Rules[0].TargetHosts[0] = "changed.example";
Assert(tunSnapshot.Rules[0].TargetHosts[0] == "example.com",
    "Queued TUN updates must use an immutable snapshot of the rule set.");

var tunRoutes = new List<object>
{
    new Dictionary<string, object> { ["process_name"] = new[] { "browser.exe" }, ["outbound"] = "proxy-out" }
};
TunService.InsertUpstreamProxyBypassRules(tunRoutes, new TunService.TunRulesConfig
{
    UpstreamProxyHosts = new List<string> { "78.111.89.227", "proxy.example" }
});
var tunRoutesJson = JsonSerializer.Serialize(tunRoutes);
Assert(tunRoutesJson.Contains("78.111.89.227/32") &&
       tunRoutesJson.Contains("proxy.example") &&
       tunRoutesJson.IndexOf("78.111.89.227/32", StringComparison.Ordinal) <
       tunRoutesJson.IndexOf("browser.exe", StringComparison.Ordinal),
    "Upstream proxy endpoints must bypass TUN before user rules to prevent a routing loop.");

using (var tunService = new TunService())
{
    var whiteListConfig = tunService.GenerateConfigJson(new TunService.TunRulesConfig
    {
        Mode = RuleMode.WhiteList,
        ProxyType = ProxyType.Socks5,
        DnsServer = "9.9.9.9",
        UseDnsProtection = true
    });
    using var configDocument = JsonDocument.Parse(whiteListConfig);
    var routeRules = configDocument.RootElement.GetProperty("route").GetProperty("rules");
    Assert(routeRules.EnumerateArray().Any(rule =>
            rule.TryGetProperty("port", out var port) &&
            port.ValueKind == JsonValueKind.Number &&
            port.GetInt32() == 53 &&
            rule.GetProperty("outbound").GetString() == "dns-out"),
        "WhiteList TUN must intercept port 53 instead of routing DNS back to its virtual peer.");
    Assert(configDocument.RootElement.GetProperty("route").GetProperty("final").GetString() == "direct",
        "Unmatched WhiteList TUN traffic must remain direct.");

    bool missingTunDnsRejected = false;
    try
    {
        tunService.GenerateConfigJson(new TunService.TunRulesConfig
        {
            Mode = RuleMode.WhiteList,
            ProxyType = ProxyType.Socks5,
            UseDnsProtection = true
        });
    }
    catch (InvalidOperationException)
    {
        missingTunDnsRejected = true;
    }
    Assert(missingTunDnsRejected,
        "TUN must not silently fall back to a hard-coded DNS server.");

    var systemDnsConfig = tunService.GenerateConfigJson(new TunService.TunRulesConfig
    {
        Mode = RuleMode.WhiteList,
        ProxyType = ProxyType.Socks5,
        DnsServer = "9.9.9.9",
        UseDnsProtection = false,
        SystemDnsServers = new List<string> { "192.0.2.53" }
    });
    using var systemDnsDocument = JsonDocument.Parse(systemDnsConfig);
    var systemDnsRule = systemDnsDocument.RootElement.GetProperty("dns").GetProperty("rules")[0];
    Assert(systemDnsRule.GetProperty("server").GetString() == "system-0",
        "TUN must use Windows system DNS when DNS Protection is disabled.");
    Assert(systemDnsDocument.RootElement.GetProperty("dns").GetProperty("servers")[0]
            .GetProperty("address").GetString() == "192.0.2.53",
        "TUN must query the system DNS address directly instead of creating a resolver loop.");

    var protectedDnsConfig = tunService.GenerateConfigJson(new TunService.TunRulesConfig
    {
        Mode = RuleMode.WhiteList,
        ProxyType = ProxyType.Socks5,
        DnsServer = "9.9.9.9",
        UseDnsProtection = true
    });
    using var protectedDnsDocument = JsonDocument.Parse(protectedDnsConfig);
    var protectedDnsRoutes = protectedDnsDocument.RootElement.GetProperty("route").GetProperty("rules")
        .EnumerateArray().ToArray();
    Assert(protectedDnsRoutes.Any(rule =>
            rule.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number && port.GetInt32() == 53 &&
            rule.GetProperty("outbound").GetString() == "dns-out"),
        "TUN must send DNS through sing-box instead of routing loopback DNS directly.");
    var dnsServers = protectedDnsDocument.RootElement.GetProperty("dns").GetProperty("servers").EnumerateArray().ToArray();
    Assert(dnsServers.Any(server =>
            server.GetProperty("tag").GetString() == "configured" &&
            server.GetProperty("address").GetString() == "9.9.9.9"),
        "TUN DNS must use the DNS server selected in ProxyControl settings.");

    var hostnameDnsConfig = tunService.GenerateConfigJson(new TunService.TunRulesConfig
    {
        Mode = RuleMode.WhiteList,
        ProxyType = ProxyType.Socks5,
        DnsServer = "resolver.example",
        UseDnsProtection = true
    });
    using var hostnameDnsDocument = JsonDocument.Parse(hostnameDnsConfig);
    var hostnameDnsServer = hostnameDnsDocument.RootElement.GetProperty("dns").GetProperty("servers")
        .EnumerateArray().First(server => server.GetProperty("tag").GetString() == "configured");
    Assert(hostnameDnsServer.GetProperty("address_resolver").GetString() == "local",
        "A hostname DNS server must declare an address resolver so sing-box can start.");

    const string httpProxyId = "http-rule-proxy";
    const string socksProxyId = "socks-rule-proxy";
    var perProxyConfig = tunService.GenerateConfigJson(new TunService.TunRulesConfig
    {
        Mode = RuleMode.WhiteList,
        ProxyType = ProxyType.Socks5,
        DnsServer = "9.9.9.9",
        SystemDnsServers = new List<string> { "192.0.2.53" },
        Proxies = new List<ProxyItem>
        {
            new()
            {
                Id = httpProxyId, IsEnabled = true, Type = ProxyType.Http,
                IpAddress = "http.proxy.test", Port = 8443,
                Username = "http-user", Password = "http-password", UseTls = true
            },
            new()
            {
                Id = socksProxyId, IsEnabled = true, Type = ProxyType.Socks5,
                IpAddress = "socks.proxy.test", Port = 1080,
                Username = "socks-user", Password = "socks-password"
            }
        },
        Rules = new List<TrafficRule>
        {
            new()
            {
                IsEnabled = true, Action = RuleAction.Proxy, ProxyId = httpProxyId,
                TrafficType = RuleTrafficType.Any,
                TargetApps = new List<string> { "browser.exe" },
                TargetHosts = new List<string> { "api.example" }
            },
            new()
            {
                IsEnabled = true, Action = RuleAction.Proxy, ProxyId = socksProxyId,
                TrafficType = RuleTrafficType.TCP,
                TargetApps = new List<string> { "language_server.exe" },
                TargetHosts = new List<string> { "generative.example" }
            }
        }
    });
    using var perProxyDocument = JsonDocument.Parse(perProxyConfig);
    var outboundList = perProxyDocument.RootElement.GetProperty("outbounds").EnumerateArray().ToArray();
    Assert(outboundList.Any(outbound =>
            outbound.GetProperty("tag").GetString() == TunService.GetProxyOutboundTag(httpProxyId) &&
            outbound.GetProperty("type").GetString() == "http" &&
            outbound.GetProperty("tls").GetProperty("enabled").GetBoolean()),
        "A TLS HTTP rule must get its own authenticated sing-box HTTP outbound.");
    Assert(outboundList.Any(outbound =>
            outbound.GetProperty("tag").GetString() == TunService.GetProxyOutboundTag(socksProxyId) &&
            outbound.GetProperty("type").GetString() == "socks"),
        "A SOCKS5 rule must get its own sing-box SOCKS outbound.");

    var perProxyRoutes = perProxyDocument.RootElement.GetProperty("route").GetProperty("rules")
        .EnumerateArray().ToArray();
    Assert(perProxyRoutes.Any(route =>
            route.TryGetProperty("process_name", out var processNames) &&
            processNames.EnumerateArray().Any(name => name.GetString() == "browser.exe") &&
            route.GetProperty("outbound").GetString() == TunService.GetProxyOutboundTag(httpProxyId) &&
            route.GetProperty("network").GetString() == "tcp"),
        "The HTTP WhiteList rule must route to its selected HTTP outbound and limit Any to TCP.");
    Assert(perProxyRoutes.Any(route =>
            route.TryGetProperty("process_name", out var processNames) &&
            processNames.EnumerateArray().Any(name => name.GetString() == "language_server.exe") &&
            route.GetProperty("outbound").GetString() == TunService.GetProxyOutboundTag(socksProxyId)),
        "The SOCKS WhiteList rule must route to its selected SOCKS outbound.");
}

var historyJson = JsonSerializer.Serialize(new ConnectionHistoryItem
{
    ProcessName = "browser.exe",
    ProcessPath = @"C:\Apps\Browser\browser.exe",
    Host = "example.com",
    Type = TrafficType.UDP
});
var restoredHistory = JsonSerializer.Deserialize<ConnectionHistoryItem>(historyJson);
Assert(restoredHistory?.ProcessPath == @"C:\Apps\Browser\browser.exe",
    "Connection history must persist the executable path used for icon recovery.");
Assert(restoredHistory?.Type == TrafficType.UDP,
    "Connection history must persist the real traffic type.");

Console.WriteLine("ProxyControl.Tests passed");
