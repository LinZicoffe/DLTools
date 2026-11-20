using System.IO;
using System.Net;
using System.Net.Http;
using Xunit;

namespace DLTools.Tests;

/// <summary>FaultInjectionServer 自身故障语义自检（裸 HttpClient，不经下载引擎）。
/// 全部集成门控：DLTOOLS_DOWNLOAD_INTEGRATION=1。</summary>
public class FaultInjectionServerTests
{
    private static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        return client;
    }

    private static async Task<HttpResponseMessage> GetRangeAsync(HttpClient client, string baseUrl,
        long start, long end, string? ifRange = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fw.bin");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
        if (ifRange is not null) request.Headers.TryAddWithoutValidation("If-Range", ifRange);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>读到流尽头或连接中断，返回实际收到的字节数（掐断用例专用）。</summary>
    private static async Task<long> ReadCountingAsync(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                total += read;
            }
        }
        catch (Exception error) when (error is IOException or HttpRequestException) { }
        return total;
    }

    [DownloadIntegrationFact]
    public async Task DataRequest_DropEverySecond_TruncatesBodyAtConfiguredOffset()
    {
        var data = FaultInjectionServer.GenerateData(1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            DropAfterBytes = 64 * 1024,
            DropEvery = 2
        });
        using var client = CreateClient();

        using var first = await GetRangeAsync(client, server.BaseUrl, 0, data.Length - 1);
        Assert.Equal(HttpStatusCode.PartialContent, first.StatusCode);
        Assert.Equal(data.Length, await ReadCountingAsync(await first.Content.ReadAsStreamAsync()));

        using var second = await GetRangeAsync(client, server.BaseUrl, 0, data.Length - 1);
        var truncated = await ReadCountingAsync(await second.Content.ReadAsStreamAsync());
        Assert.True(truncated is > 0 and <= 64 * 1024,
            $"第 2 个数据请求应在 64KiB 处被掐断，实际收到 {truncated} 字节");
    }

    [DownloadIntegrationFact]
    public async Task DataRequest_FailEveryThird429_CarriesRetryAfterHeader()
    {
        var data = FaultInjectionServer.GenerateData(4096);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            FailEvery = 3,
            FailStatus = 429,
            RetryAfterSeconds = 7
        });
        using var client = CreateClient();

        using var first = await GetRangeAsync(client, server.BaseUrl, 0, 1023);
        using var second = await GetRangeAsync(client, server.BaseUrl, 0, 1023);
        using var third = await GetRangeAsync(client, server.BaseUrl, 0, 1023);
        Assert.Equal(HttpStatusCode.PartialContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.TryGetValues("Retry-After", out var values));
        Assert.Equal("7", values!.Single());
    }

    [DownloadIntegrationFact]
    public async Task ConcurrentRequests_ExceedMaxConcurrent_Get503()
    {
        var data = FaultInjectionServer.GenerateData(1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            MaxConcurrent = 1,
            Latency = TimeSpan.FromMilliseconds(300) // 拉住首个请求，确保两个请求同时在途
        });
        using var client = CreateClient();

        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            using var response = await GetRangeAsync(client, server.BaseUrl, 0, data.Length - 1);
            return response.StatusCode;
        }).ToArray();
        var statuses = await Task.WhenAll(tasks);
        Assert.Contains(HttpStatusCode.PartialContent, statuses);
        Assert.Contains(HttpStatusCode.ServiceUnavailable, statuses);
    }

    [DownloadIntegrationFact]
    public async Task DataRequest_StallAfterFirstChunk_HangsUntilClientTimeout()
    {
        var data = FaultInjectionServer.GenerateData(1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile { StallAtBytes = 64 * 1024 });
        using var client = CreateClient(TimeSpan.FromMilliseconds(800));

        using var response = await GetRangeAsync(client, server.BaseUrl, 0, data.Length - 1);
        var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64 * 1024];
        var received = 0;
        // HttpClient.Timeout 不覆盖 ResponseHeadersRead 的内容流读取，须用显式超时 token
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (received < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(received), readCts.Token);
            Assert.True(read > 0, "首块 64KiB 应正常到达");
            received += read;
        }
        // 首块之后永久挂起：显式超时兜底
        using var stallCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.ReadAsync(buffer, stallCts.Token).AsTask());
    }

    [DownloadIntegrationFact]
    public async Task DataRequest_EtagChangeAfterSecondRequest_SwitchesValidator()
    {
        var data = FaultInjectionServer.GenerateData(4096);
        using var server = new FaultInjectionServer(data, new FaultProfile { EtagChangeAfterRequest = 2 });
        using var client = CreateClient();

        using var first = await GetRangeAsync(client, server.BaseUrl, 0, 1023);
        using var second = await GetRangeAsync(client, server.BaseUrl, 0, 1023);
        using var third = await GetRangeAsync(client, server.BaseUrl, 0, 1023);
        Assert.Equal("\"v1\"", first.Headers.ETag!.Tag);
        Assert.Equal("\"v1\"", second.Headers.ETag!.Tag);
        Assert.Equal("\"v2\"", third.Headers.ETag!.Tag);

        // 旧验证器的 If-Range 请求 → 200 全量（资源已变更语义）
        using var stale = await GetRangeAsync(client, server.BaseUrl, 0, 1023, ifRange: "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(data, await stale.Content.ReadAsByteArrayAsync());
    }

    [DownloadIntegrationFact]
    public async Task DataRequest_RateLimited_TricklesAtConfiguredPace()
    {
        var data = FaultInjectionServer.GenerateData(256 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            RateBytesPerSecond = 128 * 1024 // 256KiB 约需 2 秒
        });
        using var client = CreateClient();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await GetRangeAsync(client, server.BaseUrl, 0, data.Length - 1);
        var body = await response.Content.ReadAsByteArrayAsync();
        stopwatch.Stop();
        Assert.Equal(data, body);
        Assert.True(stopwatch.ElapsedMilliseconds >= 1500,
            $"滴流配速未生效：256KiB@128KiB/s 应约 2 秒，实际 {stopwatch.ElapsedMilliseconds}ms");
    }

    [DownloadIntegrationFact]
    public async Task ProbeRequest_WithFaultsConfigured_StaysHealthyByDefault()
    {
        var data = FaultInjectionServer.GenerateData(4096);
        using var server = new FaultInjectionServer(data, new FaultProfile { FailEvery = 1 });
        using var client = CreateClient();

        using var probe = await GetRangeAsync(client, server.BaseUrl, 0, 0);
        Assert.Equal(HttpStatusCode.PartialContent, probe.StatusCode);
        Assert.Equal(new byte[] { data[0] }, await probe.Content.ReadAsByteArrayAsync());

        using var dataRequest = await GetRangeAsync(client, server.BaseUrl, 0, 1023);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, dataRequest.StatusCode);
    }
}