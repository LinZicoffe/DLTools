using System.Net;
using Xunit;
using 烧录上位机程序.Services.IpPurity;

namespace DLTools.Tests;

public class IpPurityEndpointTests
{
    [Fact]
    public void FixedEndpoints_AreAllowedUseDnsHostsAndHttpsExceptIpApiFallback()
    {
        var urls = IpPurityEndpoints.ExitSources.Select(source => source.Url)
            .Concat(IpPurityEndpoints.Services.SelectMany(service => new[] { service.TraceUrl, service.ApiUrl }))
            .Concat([
                IpPurityEndpoints.IpApiUrl, IpPurityEndpoints.IpinfoJsonUrl,
                IpPurityEndpoints.IpapiJsonUrl, IpPurityEndpoints.Ipv6Url,
            ])
            .ToList();

        // 出口链里的 IPinfo / ipapi.co 与情报链的基础信息来源是同一地址，所以去重后是 10 条
        Assert.Equal(12, urls.Count);
        Assert.Equal(10, urls.Distinct().Count());
        foreach (var url in urls)
        {
            var uri = IpPurityEndpoints.EnsureAllowed(url);
            Assert.Equal(UriHostNameType.Dns, uri.HostNameType);
            Assert.True(uri.IsDefaultPort);
            Assert.Equal("", uri.UserInfo);
        }

        var plain = urls.Where(url => new Uri(url).Scheme == Uri.UriSchemeHttp).ToList();
        Assert.Equal([IpPurityEndpoints.IpApiUrl], plain);
        Assert.StartsWith("http://ip-api.com/json/", IpPurityEndpoints.IpApiUrl);
        Assert.Contains("hosting", IpPurityEndpoints.IpApiUrl);
        Assert.Contains("proxy", IpPurityEndpoints.IpApiUrl);
    }

    [Theory]
    [InlineData("203.0.113.7", "https://ipinfo.io/widget/demo/203.0.113.7")]
    [InlineData("2001:db8:40:1::a1b2:9c3d", "https://ipinfo.io/widget/demo/2001%3Adb8%3A40%3A1%3A%3Aa1b2%3A9c3d")]
    public void IntelUrl_IsBuiltOnlyForIpLiteralsAndStaysAllowed(string ip, string expected)
    {
        var url = IpPurityEndpoints.BuildIntelUrl(ip);
        Assert.Equal(expected, url);
        Assert.True(IpPurityEndpoints.IsAllowed(IpPurityEndpoints.EnsureAllowed(url)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4/24")]
    [InlineData("1.2.3.4/../x")]
    [InlineData("1.2.3.4?x=1")]
    [InlineData(" 1.2.3.4 ")]
    [InlineData("<script>")]
    public void IntelUrl_RejectsAnythingThatIsNotAnIpLiteral(string? ip)
    {
        Assert.Null(IpPurityEndpoints.BuildIntelUrl(ip));
    }

    [Theory]
    [InlineData("http://localhost/json")]
    [InlineData("https://localhost/json")]
    [InlineData("https://127.0.0.1/json")]
    [InlineData("https://[::1]/json")]
    [InlineData("https://10.0.0.1/cdn-cgi/trace")]
    [InlineData("https://192.168.1.1/json")]
    [InlineData("https://198.18.0.1/json")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://ipinfo.io.evil.example/json")]
    [InlineData("https://ipinfo.io:8443/json")]
    [InlineData("https://user:pass@ipinfo.io/json")]
    [InlineData("ftp://ipinfo.io/json")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://ipinfo.io/json#frag")]
    [InlineData("http://ipinfo.io/json")]
    [InlineData("https://ipinfo.io/widget/demo/not-an-ip")]
    [InlineData("https://ipinfo.io/widget/demo/1.2.3.4/extra")]
    [InlineData("https://ipinfo.io/widget/demo/1.2.3.4?x=1")]
    [InlineData("https://ipinfo.io/widget/demo/1.2.3.4#x")]
    [InlineData("https://ipinfo.io/widget/demo/%2E%2E/json")]
    [InlineData("/cdn-cgi/trace")]
    [InlineData("")]
    [InlineData(null)]
    public void Guard_RejectsLocalPrivateLookalikeAndDecoratedAddresses(string? url)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            Assert.False(IpPurityEndpoints.IsAllowed(uri));
        var error = Assert.Throws<InvalidOperationException>(() => IpPurityEndpoints.EnsureAllowed(url));
        Assert.Contains("白名单", error.Message);
    }

    [Fact]
    public void Services_MatchUpstreamDefinition()
    {
        var services = IpPurityEndpoints.Services;
        Assert.Equal(["chatgpt", "claude"], services.Select(service => service.Id));
        Assert.Equal(["ChatGPT", "Claude"], services.Select(service => service.Name));
        Assert.Equal("https://api.anthropic.com/v1/models", services[1].ApiUrl);
        Assert.Equal("true", services[1].Headers["anthropic-dangerous-direct-browser-access"]);
        Assert.Empty(services[0].Headers);
        Assert.All(services, service => Assert.Equal("https://" + service.Host + "/", service.Site));
    }

    [Fact]
    public void ExitSources_FallBackInTheUpstreamOrder()
    {
        var names = IpPurityEndpoints.ExitSources.Select(source => source.Name).ToList();
        Assert.Equal(["Cloudflare", "IPinfo", "ipify", "ipapi.co"], names);
        Assert.True(IpPurityEndpoints.ExitSources[0].IsText);
        Assert.All(IpPurityEndpoints.ExitSources.Skip(1), source => Assert.False(source.IsText));
    }
}
