using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Services.IpPurity;

namespace DLTools.Tests;

public class IpPurityTransportTests
{
    [Fact]
    public async Task Send_RejectsAddressesOutsideTheFixedWhitelist()
    {
        var called = false;
        using var transport = new IpPurityTransport(new StubHandler((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(new IpPurityRequest("https://127.0.0.1/json"), CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task Send_UsesHonestUserAgentAndDoesNotFollowRedirects()
    {
        var calls = 0;
        string? userAgent = null;
        using var transport = new IpPurityTransport(new StubHandler((request, _) =>
        {
            calls++;
            userAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://evil.example/steal") },
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }));

        var response = await transport.SendAsync(new IpPurityRequest(IpPurityEndpoints.IpinfoJsonUrl), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("DLTools/1.0", userAgent);
        Assert.False(response.Ok);
        Assert.Equal("HTTP_302", response.Error!.Code);
        Assert.Equal(302, response.Status);
    }

    [Fact]
    public async Task Send_TurnsTransportFailuresIntoResultsAndKeepsJsonOnHttpErrors()
    {
        using var transport = new IpPurityTransport(new StubHandler((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("trace", StringComparison.Ordinal))
            {
                throw new IOException("连接被重置");
            }
            if (request.RequestUri.Host == "api.openai.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"unsupported_country_region_territory\"}}", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-json", Encoding.UTF8, "text/plain")
            });
        }));

        var network = await transport.SendAsync(new IpPurityRequest("https://www.cloudflare.com/cdn-cgi/trace", ExpectJson: false), CancellationToken.None);
        var denied = await transport.SendAsync(new IpPurityRequest("https://api.openai.com/v1/models"), CancellationToken.None);
        var parse = await transport.SendAsync(new IpPurityRequest(IpPurityEndpoints.IpinfoJsonUrl), CancellationToken.None);

        Assert.Equal("NETWORK", network.Error!.Code);
        Assert.Equal("网络错误或被拦截", network.Error.Message);
        Assert.False(denied.Ok);
        Assert.Equal("HTTP_403", denied.Error!.Code);
        Assert.Equal(JsonValueKind.Object, denied.Data!.Value.ValueKind);
        Assert.Equal("PARSE", parse.Error!.Code);
    }

    [Fact]
    public async Task Send_TimesOutWithoutThrowing_AndCallerCancelStillThrows()
    {
        using var transport = new IpPurityTransport(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var timed = await transport.SendAsync(
            new IpPurityRequest(IpPurityEndpoints.IpinfoJsonUrl, Timeout: TimeSpan.FromMilliseconds(80)),
            CancellationToken.None);
        Assert.Equal("TIMEOUT", timed.Error!.Code);

        using var cancel = new CancellationTokenSource();
        var pending = transport.SendAsync(new IpPurityRequest(IpPurityEndpoints.IpinfoJsonUrl), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
