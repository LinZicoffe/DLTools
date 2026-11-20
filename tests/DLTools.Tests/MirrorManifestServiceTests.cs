using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class MirrorManifestServiceTests : IDisposable
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TestWorkspace _workspace = new();

    private static MirrorManifestService CreateService(string json, string contentType = "application/json") =>
        new(new FakeDownloadTransport((_, _) => Task.FromResult(
            FakeDownloadTransport.OkFull(json.Length, Encoding.UTF8.GetBytes(json), contentType))));

    private static string Manifest(string filesJson) =>
        $$"""{"schemaVersion":1,"files":[{{filesJson}}]}""";

    [Fact]
    public async Task Fetch_ValidManifest_ReturnsEntriesWithFilteredNodes()
    {
        var service = CreateService(Manifest(
            $$"""{"name":"fw.bin","size":123456,"sha256":"{{ValidSha}}","urls":["https://8.8.8.8/fw.bin","http://9.9.9.9/fw.bin","https://192.168.1.1/fw.bin"]}"""));
        var manifest = await service.FetchAsync("https://8.8.8.8/manifest.json", CancellationToken.None);

        var entry = Assert.Single(manifest.Files);
        Assert.Equal("fw.bin", entry.Name);
        Assert.Equal(123456, entry.Size);
        Assert.Equal(ValidSha, entry.Sha256);
        Assert.Equal(["https://8.8.8.8/fw.bin"], entry.Urls); // http 与私网节点被过滤
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"files":[]}""")]
    public async Task Fetch_EmptyManifest_IsRejected(string json)
    {
        var service = CreateService(json);
        var error = await Assert.ThrowsAsync<DownloadTransportException>(
            () => service.FetchAsync("https://8.8.8.8/manifest.json", CancellationToken.None));
        Assert.Contains("files", error.Message);
    }

    [Fact]
    public async Task Fetch_FutureSchemaVersion_IsRejected()
    {
        var service = CreateService("""{"schemaVersion":2,"files":[{"name":"a","size":1,"sha256":"x","urls":["https://8.8.8.8/a"]}]}""");
        var error = await Assert.ThrowsAsync<DownloadTransportException>(
            () => service.FetchAsync("https://8.8.8.8/manifest.json", CancellationToken.None));
        Assert.Contains("版本", error.Message);
    }

    [Theory]
    [InlineData("""{"name":"fw.bin","size":0,"sha256":"PLACEHOLDER","urls":["https://8.8.8.8/fw.bin"]}""", "size")]
    [InlineData("""{"name":"fw.bin","size":10,"sha256":"tooshort","urls":["https://8.8.8.8/fw.bin"]}""", "sha256")]
    [InlineData("""{"name":"fw.bin","size":10,"sha256":"PLACEHOLDER","urls":[]}""", "没有可用")]
    [InlineData("""{"name":"","size":10,"sha256":"PLACEHOLDER","urls":["https://8.8.8.8/fw.bin"]}""", "name")]
    public async Task Fetch_EntryMissingMandatoryFields_IsRejectedWithEntryHint(string fileJson, string hint)
    {
        var json = $$"""{"schemaVersion":1,"files":[{{fileJson.Replace("PLACEHOLDER", ValidSha)}}]}""";
        var service = CreateService(json);
        var error = await Assert.ThrowsAsync<DownloadTransportException>(
            () => service.FetchAsync("https://8.8.8.8/manifest.json", CancellationToken.None));
        Assert.Contains(hint, error.Message);
        Assert.True(error.Terminal);
    }

    [Fact]
    public async Task Fetch_HttpOrPrivateManifestUrl_IsRejectedBeforeRequest()
    {
        var requested = false;
        var service = new MirrorManifestService(new FakeDownloadTransport((_, _) =>
        {
            requested = true;
            return Task.FromResult(FakeDownloadTransport.Status(200));
        }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.FetchAsync("http://8.8.8.8/manifest.json", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.FetchAsync("https://192.168.1.9/manifest.json", CancellationToken.None));
        Assert.False(requested); // 校验先于任何网络请求
    }

    [Fact]
    public async Task Fetch_HtmlResponse_IsRejectedAsWrongAddress()
    {
        var service = CreateService("<html/>", contentType: "text/html");
        var error = await Assert.ThrowsAsync<DownloadTransportException>(
            () => service.FetchAsync("https://8.8.8.8/manifest.json", CancellationToken.None));
        Assert.Contains("网页", error.Message);
    }

    [Fact]
    public async Task Fetch_NonSuccessStatusCode_IsRejected()
    {
        var service = new MirrorManifestService(new FakeDownloadTransport((_, _) =>
            Task.FromResult(FakeDownloadTransport.Status(503))));
        var error = await Assert.ThrowsAsync<DownloadTransportException>(
            () => service.FetchAsync("https://8.8.8.8/manifest.json", CancellationToken.None));
        Assert.Contains("503", error.Message);
    }

    [Fact]
    public async Task OrderUrlsByLatency_FastNodeFirstAndFailedNodesLast()
    {
        var service = new MirrorManifestService(new FakeDownloadTransport(async (request, _) =>
        {
            // 1 秒分离度：CI 线程池饥饿时 fast 节点测量仍远小于 slow（250ms 曾有理论抖动）
            if (request.Url.Contains("slow")) await Task.Delay(1000);
            if (request.Url.Contains("dead")) throw new DownloadTransportException("连接失败");
            return FakeDownloadTransport.Partial(10, 0, 0, [1]);
        }));
        var entry = new MirrorFileEntry("fw.bin", 10, ValidSha,
            ["https://8.8.8.8/slow/fw.bin", "https://8.8.8.8/fast/fw.bin", "https://8.8.8.8/dead/fw.bin"]);

        var ordered = await service.OrderUrlsByLatencyAsync(entry, CancellationToken.None);

        Assert.Equal("https://8.8.8.8/fast/fw.bin", ordered[0]);
        Assert.Equal("https://8.8.8.8/slow/fw.bin", ordered[1]);
        Assert.Equal("https://8.8.8.8/dead/fw.bin", ordered[^1]); // 失败节点沉底（保持原相对顺序）
    }

    [Fact]
    public void BuildOptions_ForcesEntrySha256AndWiresMirrorFailover()
    {
        var service = new MirrorManifestService(new FakeDownloadTransport((_, _) =>
            Task.FromResult(FakeDownloadTransport.Status(404))));
        var entry = new MirrorFileEntry("fw.bin", 10, ValidSha,
            ["https://8.8.8.8/a/fw.bin", "https://8.8.8.8/b/fw.bin"]);

        var options = service.BuildOptions(entry, @"C:\out", connections: 4, orderedUrls: entry.Urls);

        Assert.Equal("https://8.8.8.8/a/fw.bin", options.Url);
        Assert.Equal(["https://8.8.8.8/b/fw.bin"], options.MirrorUrls);
        Assert.Equal(ValidSha, options.ExpectedSha256); // 清单分发强制校验
        Assert.True(options.EnforcePublicHttps); // 清单链路的请求（含重定向）保持 https 公网要求
        Assert.Equal(4, options.Connections);
        Assert.Equal(@"C:\out", options.DestinationDirectory);
    }

    [Fact]
    public async Task Fetch_ManifestOver4MiB_IsRejected()
    {
        var oversized = new byte[4 * 1024 * 1024 + 1]; // 恰好超过上限
        var service = new MirrorManifestService(new FakeDownloadTransport((_, _) =>
            Task.FromResult(new TransportResponse(200, "application/json", "", "", null, oversized.Length,
                "", null, new MemoryStream(oversized)))));

        var error = await Assert.ThrowsAsync<DownloadTransportException>(
            () => service.FetchAsync("https://8.8.8.8/manifest.json", CancellationToken.None));

        Assert.Contains("4 MB 上限", error.Message);
    }

    [Fact]
    public async Task OrderUrlsByLatency_AllNodesFail_ReturnsOriginalOrderForEngineErrors()
    {
        var service = new MirrorManifestService(new FakeDownloadTransport((_, _) =>
            Task.FromResult(FakeDownloadTransport.Status(503))));
        var entry = new MirrorFileEntry("fw.bin", 10, ValidSha,
            ["https://8.8.8.8/a/fw.bin", "https://8.8.8.8/b/fw.bin", "https://8.8.8.8/c/fw.bin"]);

        var ordered = await service.OrderUrlsByLatencyAsync(entry, CancellationToken.None);

        // 全部失败时保持原序交回引擎：由引擎重试/failover 报出真实错误，而不是竞速层吞掉
        Assert.Equal(entry.Urls, ordered);
    }

    [Fact]
    public async Task OrderUrlsByLatency_HalfDeadNodeTimingOut_DoesNotBreakBatch()
    {
        // 半死节点：TCP 可连但 5 秒不回头。WaitAsync 超时抛 TimeoutException——
        // 必须按"该节点失败"处理（沉底），不能让异常穿透打断整个"导入所选"批处理
        var service = new MirrorManifestService(new FakeDownloadTransport(async (request, _) =>
        {
            if (request.Url.Contains("halfdead")) await Task.Delay(TimeSpan.FromSeconds(30));
            return FakeDownloadTransport.Partial(10, 0, 0, [1]);
        }), nodeProbeTimeout: TimeSpan.FromMilliseconds(200));
        var entry = new MirrorFileEntry("fw.bin", 10, ValidSha,
            ["https://8.8.8.8/halfdead/fw.bin", "https://8.8.8.8/alive/fw.bin"]);

        var ordered = await service.OrderUrlsByLatencyAsync(entry, CancellationToken.None);

        Assert.Equal("https://8.8.8.8/alive/fw.bin", ordered[0]);
        Assert.Equal("https://8.8.8.8/halfdead/fw.bin", ordered[^1]); // 超时节点沉底而非中断
    }

    /// <summary>清单链路端到端冒烟：清单 JSON → Fetch 校验 → BuildOptions → 真实引擎 failover → SHA 校验落盘。
    /// 假传输承载（EnforcePublicHttps=true 拒绝环回，真实公网冒烟属用户手动验收项），
    /// 逐请求断言 https 公网要求经引擎升级后保持。</summary>
    [Fact]
    public async Task MirrorChain_ManifestToEngineFailover_ContentCorrectAndPublicHttpsKept()
    {
        // > 单流阈值（1MiB）→ 真正走分段路径的 failover（终审 B1：原 1MiB 恰等于阈值会降级单流）
        var data = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i * 31 + 7)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var json = Manifest($$"""{"name":"fw.bin","size":{{data.Length}},"sha256":"{{sha}}","urls":["https://8.8.8.8/dead/fw.bin","https://8.8.8.8/live/fw.bin"]}""");
        var live = new FakeSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) =>
        {
            if (request.Url.EndsWith("manifest.json"))
                return Task.FromResult(FakeDownloadTransport.OkFull(json.Length, Encoding.UTF8.GetBytes(json), "application/json"));
            if (request.Url.Contains("dead"))
            {
                // 探测可用但分段终态 404 → 引擎 failover 到镜像（与 MirrorFailover 用例同语义）
                if (request.RangeStart is 0 && request.RangeEnd is 0)
                    return Task.FromResult(FakeDownloadTransport.Partial(data.Length, 0, 0, [data[0]]));
                return Task.FromResult(FakeDownloadTransport.Status(404));
            }
            return Task.FromResult(live.Serve(request));
        });
        var service = new MirrorManifestService(transport);
        var manifest = await service.FetchAsync("https://8.8.8.8/manifest.json", CancellationToken.None);
        var entry = Assert.Single(manifest.Files);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var options = service.BuildOptions(entry, destination, connections: 2, orderedUrls: entry.Urls);

        var result = await new DownloadEngine(transport).DownloadAsync(options, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.Equal("fw.bin", Path.GetFileName(result.FilePath));
        var requests = transport.SnapshotRequests();
        Assert.Contains(requests, request => request.Url.Contains("dead"));
        Assert.Contains(requests, request => request.Url.Contains("live"));
        Assert.All(requests, request => Assert.True(request.EnforcePublicHttps));
    }

    public void Dispose() => _workspace.Dispose();
}
