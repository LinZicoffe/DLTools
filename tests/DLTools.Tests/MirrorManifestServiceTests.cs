using System.Text;
using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class MirrorManifestServiceTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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
            if (request.Url.Contains("slow")) await Task.Delay(250);
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
        Assert.Equal(4, options.Connections);
        Assert.Equal(@"C:\out", options.DestinationDirectory);
    }
}
