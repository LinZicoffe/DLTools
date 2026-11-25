using System.Text.Json;
using Xunit;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Services.IpPurity;

namespace DLTools.Tests;

public class IpPurityChecksTests
{
    [Fact]
    public async Task Exit_UsesTheFirstSourceThatReturnsAnAddress()
    {
        var transport = new ScriptTransport(url => url.Contains("cloudflare.com", StringComparison.Ordinal)
            ? Text("ip=203.0.113.7\nloc=US\ncolo=SJC\n")
            : Json("{\"ip\":\"198.51.100.9\"}"));
        var exit = await new IpPurityChecks(transport).ExitAsync(CancellationToken.None);
        Assert.Equal("203.0.113.7", exit.Ip);
        Assert.Equal("Cloudflare", exit.Source);
        Assert.Equal("US", exit.Loc);
        Assert.Single(transport.Urls);
    }

    [Fact]
    public async Task Exit_RecordsEveryFailureWhenNothingReturnsAnAddress()
    {
        var transport = new ScriptTransport(_ => Fail("TIMEOUT"));
        var exit = await new IpPurityChecks(transport).ExitAsync(CancellationToken.None);
        Assert.Null(exit.Ip);
        Assert.Equal(4, exit.Errors.Count);
        Assert.Contains("Cloudflare：请求超时", exit.Errors);
    }

    [Fact]
    public async Task Intel_FallsBackToIpApiWhenTheWidgetDisagrees()
    {
        var transport = new ScriptTransport(url =>
        {
            if (url.Contains("/widget/demo/", StringComparison.Ordinal))
                return Json("{\"data\":{\"ip\":\"198.51.100.9\",\"country\":\"US\"}}");
            if (url.StartsWith("http://ip-api.com/", StringComparison.Ordinal))
            {
                return Json("{\"status\":\"success\",\"country\":\"日本\",\"countryCode\":\"JP\",\"regionName\":\"东京\",\"city\":\"东京\",\"isp\":\"SoftBank\",\"org\":\"SoftBank\",\"as\":\"AS17676 SoftBank\",\"asname\":\"SoftBank\",\"mobile\":false,\"proxy\":false,\"hosting\":false,\"query\":\"203.0.113.7\"}");
            }
            return Fail("NETWORK");
        });

        var intel = await new IpPurityChecks(transport).IntelAsync("203.0.113.7", null, CancellationToken.None);
        Assert.Equal("ip-api.com", intel!.Source);
        Assert.Equal("medium", intel.Confidence);
        Assert.Equal("AS17676", intel.Asn);
        Assert.False(intel.Flags!.Vpn.HasValue);
        Assert.Equal(false, intel.Flags.Hosting);
    }

    [Fact]
    public void Verdict_RegionBlockAndSplitRoutingDoNotBorrowTheOtherIpsFlags()
    {
        var service = IpPurityEndpoints.Services.First(item => item.Id == "claude");
        var trace = Text("ip=198.51.100.9\nloc=CN\n");
        var api = new IpPurityResponse(403, "{\"error\":{\"type\":\"permission_error\",\"message\":\"Request not allowed\"}}",
            JsonDocument.Parse("{\"type\":\"error\",\"error\":{\"type\":\"permission_error\",\"message\":\"Request not allowed\"}}").RootElement.Clone(),
            40, new IpPurityError("HTTP_403", "服务返回 HTTP 403"), false);
        var intel = new IpPurityIntel("203.0.113.7", "IPinfo", "high", "AS16509", "Amazon", "Amazon", null, null, null,
            "hosting", new IpPurityFlags(true, true, false, false, false, false), new IpPurityGeo("US", null, null, null), []);

        var verdict = IpPurityChecks.Judge(service, trace, api, intel, intelPending: false);
        Assert.Equal("region", verdict.Status);
        Assert.Empty(verdict.Risks);
        Assert.Contains(verdict.Notes, note => note.Contains("未套用", StringComparison.Ordinal));
    }

    [Fact]
    public void UdpClassification_IgnoresDomesticAndOtherProtocolAddresses()
    {
        var rows = new[]
        {
            new IpPurityUdpObservation("stun.cloudflare.com", 3478, true, "203.0.113.7", 20, null),
            new IpPurityUdpObservation("stun.l.google.com", 19302, true, "2001:db8::1", 20, null),
            new IpPurityUdpObservation("stun.miwifi.com", 3478, false, "192.0.2.44", 10, null),
            new IpPurityUdpObservation("stun.qq.com", 3478, false, null, null, "请求超时"),
        };
        var report = IpPurityChecks.ClassifyUdp("203.0.113.7", rows);
        Assert.Equal("clean", report.State);
        Assert.Empty(report.Leaked);

        var leaked = IpPurityChecks.ClassifyUdp("203.0.113.7",
        [
            new IpPurityUdpObservation("stun.cloudflare.com", 3478, true, "192.0.2.44", 20, null),
            new IpPurityUdpObservation("stun.l.google.com", 19302, true, "198.18.0.8", 20, null),
        ]);
        Assert.Equal("leak", leaked.State);
        Assert.Equal(["192.0.2.44"], leaked.Leaked);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7", true)]
    [InlineData("2001:db8::1", "2001:0db8:0000:0000:0000:0000:0000:0001", true)]
    [InlineData("203.0.113.7", "203.0.113.8", false)]
    [InlineData("198.18.0.1", null, true)]
    public void AddressHelpers_MatchTheWebRules(string left, string? right, bool sameOrPrivate)
    {
        if (right is null) Assert.True(IpPurityParse.IsPrivate(left));
        else Assert.Equal(sameOrPrivate, IpPurityParse.SameIp(left, right));
    }

    private static IpPurityResponse Text(string body) => new(200, body, null, 10, null, true);

    private static IpPurityResponse Json(string body)
    {
        using var document = JsonDocument.Parse(body);
        return new IpPurityResponse(200, body, document.RootElement.Clone(), 10, null, true);
    }

    private static IpPurityResponse Fail(string code) =>
        new(0, null, null, 10, new IpPurityError(code, IpPurityParse.DescribeError(code)), false);

    private sealed class ScriptTransport(Func<string, IpPurityResponse> respond) : IIpPurityTransport
    {
        public List<string> Urls { get; } = [];
        public Task<IpPurityResponse> SendAsync(IpPurityRequest request, CancellationToken cancellationToken)
        {
            Urls.Add(request.Url);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request.Url));
        }
    }
}
