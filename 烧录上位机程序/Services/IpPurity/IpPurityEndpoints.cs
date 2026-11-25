using System.Net;
using 烧录上位机程序.Models.IpPurity;

namespace 烧录上位机程序.Services.IpPurity;

/// <summary>检测用到的全部端点。
///
/// 安全边界是"整条 URL 精确匹配白名单"：运行时不接受任何外部提供的地址，只接受白名单里的域名
/// （IP 字面量、localhost、带端口、带账号信息的地址一律不在白名单内），并且不跟随重定向。
/// 这里刻意不在解析后拒绝内网/保留地址：开启 TUN + fake-ip 的代理环境会把域名解析到
/// 198.18.0.0/15，那样的校验会让检测在常见代理环境下全部失败。</summary>
public static class IpPurityEndpoints
{
    private const string IpApiFields =
        "status,message,country,countryCode,regionName,city,isp,org,as,asname,mobile,proxy,hosting,query";

    private const string WidgetPrefix = "https://ipinfo.io/widget/demo/";

    /// <summary>出口 IP 来源，按回退顺序排列。第一个返回合法地址的来源生效。</summary>
    public static IReadOnlyList<IpPurityExitSource> ExitSources { get; } =
    [
        new("Cloudflare", "https://www.cloudflare.com/cdn-cgi/trace", IsText: true),
        new("IPinfo", "https://ipinfo.io/json", IsText: false),
        new("ipify", "https://api64.ipify.org?format=json", IsText: false),
        new("ipapi.co", "https://ipapi.co/json/", IsText: false),
    ];

    /// <summary>要检测的 AI 服务，顺序即界面上的顺序。</summary>
    public static IReadOnlyList<IpPurityServiceInfo> Services { get; } =
    [
        new IpPurityServiceInfo(
            Id: "chatgpt", Name: "ChatGPT", Vendor: "OpenAI", Host: "chatgpt.com", Site: "https://chatgpt.com/",
            TraceUrl: "https://chatgpt.com/cdn-cgi/trace", ApiUrl: "https://api.openai.com/v1/models",
            Headers: new Dictionary<string, string>(), RegionSet: "openai"),
        new IpPurityServiceInfo(
            Id: "claude", Name: "Claude", Vendor: "Anthropic", Host: "claude.ai", Site: "https://claude.ai/",
            TraceUrl: "https://claude.ai/cdn-cgi/trace", ApiUrl: "https://api.anthropic.com/v1/models",
            // 与网页版一致：Anthropic 的接口在浏览器里直接调用时需要这个头；桌面端带上它，判定逻辑才能和网页版对齐
            Headers: new Dictionary<string, string> { ["anthropic-dangerous-direct-browser-access"] = "true" },
            RegionSet: "anthropic"),
    ];

    /// <summary>ip-api.com 只提供 HTTP 接口（HTTPS 是付费功能）。它只在主情报源失败时作为回退。</summary>
    public static string IpApiUrl { get; } = "http://ip-api.com/json/?lang=zh-CN&fields=" + IpApiFields;

    public static string IpinfoJsonUrl { get; } = "https://ipinfo.io/json";

    public static string IpapiJsonUrl { get; } = "https://ipapi.co/json/";

    public static string Ipv6Url { get; } = "https://api6.ipify.org?format=json";

    /// <summary>固定端点的规范形式。Uri 对两边做同样的规范化（例如补上空路径的 "/"），再精确比对。</summary>
    private static readonly HashSet<string> FixedUrls = BuildFixedUrls();

    private static HashSet<string> BuildFixedUrls()
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in ExitSources) urls.Add(Canonical(source.Url));
        foreach (var service in Services)
        {
            urls.Add(Canonical(service.TraceUrl));
            urls.Add(Canonical(service.ApiUrl));
        }
        urls.Add(Canonical(IpApiUrl));
        urls.Add(Canonical(IpinfoJsonUrl));
        urls.Add(Canonical(IpapiJsonUrl));
        urls.Add(Canonical(Ipv6Url));
        return urls;
    }

    /// <summary>IPinfo 情报查询地址。只有合法的 IP 字面量才会被拼进路径，其余一律返回 null。</summary>
    public static string? BuildIntelUrl(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out _)) return null;
        return WidgetPrefix + Uri.EscapeDataString(ip);
    }

    /// <summary>校验并解析一条检测地址；不在白名单内时抛出 InvalidOperationException。</summary>
    public static Uri EnsureAllowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowed(uri))
            throw new InvalidOperationException("检测地址不在固定端点白名单内：" + (url ?? "<null>"));
        return uri;
    }

    public static bool IsAllowed(Uri uri)
    {
        // 只允许 http/https；不带账号信息、不指定端口、不带片段
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Fragment)) return false;
        // 只接受域名形式的 host。IP 字面量（环回、内网、保留地址都属此类）与 localhost 都不在白名单里
        if (uri.HostNameType != UriHostNameType.Dns) return false;
        var absolute = uri.AbsoluteUri;
        if (FixedUrls.Contains(absolute)) return true;
        return IsIntelUrl(absolute);
    }

    private static bool IsIntelUrl(string absolute)
    {
        if (!absolute.StartsWith(WidgetPrefix, StringComparison.Ordinal)) return false;
        var tail = absolute[WidgetPrefix.Length..];
        var query = tail.IndexOf('?');
        if (query >= 0) return false;
        if (tail.Contains('/') || tail.Contains('#')) return false;
        return IPAddress.TryParse(Uri.UnescapeDataString(tail), out _);
    }

    private static string Canonical(string url) => new Uri(url, UriKind.Absolute).AbsoluteUri;
}
