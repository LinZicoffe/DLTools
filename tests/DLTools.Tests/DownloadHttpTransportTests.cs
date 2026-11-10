using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>DownloadHttpTransport 的重定向逐跳校验、内容流异常映射与请求头解析。
/// 注入 HttpMessageHandler，全程无真实网络（清单链路用公网 IP 字面量避免 DNS）。</summary>
public class DownloadHttpTransportTests
{
    /// <summary>按序回放的 HttpMessageHandler：记录每次请求用于断言。</summary>
    private sealed class QueuedHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        private readonly HttpResponseMessage[] _responses;
        private int _index;

        public QueuedHandler(params HttpResponseMessage[] responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = _responses[Math.Min(Interlocked.Increment(ref _index) - 1, _responses.Length - 1)];
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Redirect(string location,
        HttpStatusCode code = HttpStatusCode.Found) =>
        new(code) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static async Task<TransportResponse> SendAsyncAsync(HttpMessageHandler handler, string url,
        long? rangeStart = null, long? rangeEnd = null, string? ifRange = null, bool enforcePublicHttps = false)
    {
        var transport = new DownloadHttpTransport(handler);
        return await transport.SendAsync(
            new TransportRequest(url, rangeStart, rangeEnd, ifRange, enforcePublicHttps),
            CancellationToken.None);
    }

    [Fact]
    public async Task RedirectToAllowedUrl_FollowsAndReturnsFinalBody()
    {
        var body = "firmware-bytes"u8.ToArray();
        var handler = new QueuedHandler(
            Redirect("https://93.184.216.34/real.bin"),
            Ok(body));

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/start");

        Assert.Equal(200, response.StatusCode);
        using var memory = new MemoryStream();
        await response.Content.CopyToAsync(memory);
        Assert.Equal(body, memory.ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://93.184.216.34/real.bin", handler.Requests[1].RequestUri!.AbsoluteUri);
        await response.DisposeAsync();
    }

    [Fact]
    public async Task RedirectRelativeLocation_ResolvesAgainstCurrentUrl()
    {
        var handler = new QueuedHandler(
            Redirect("/fw.bin", HttpStatusCode.TemporaryRedirect),
            Ok([1]));

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/dir/start");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("https://93.184.216.34/fw.bin", handler.Requests[1].RequestUri!.AbsoluteUri);
        await response.DisposeAsync();
    }

    [Fact]
    public async Task RedirectToPrivateHost_InManifestMode_IsRejectedTerminal()
    {
        // Location 保持 https，确保命中的是"私网/保留地址"判定而非 scheme 检查
        var handler = new QueuedHandler(Redirect("https://192.168.1.5/internal"));

        var error = await Assert.ThrowsAsync<DownloadTransportException>(() =>
            SendAsyncAsync(handler, "https://93.184.216.34/manifest.json", enforcePublicHttps: true));

        Assert.True(error.Terminal);
        Assert.Equal(302, error.StatusCode); // 保留发起跳转的状态码
        Assert.Contains("到不允许的地址", error.Message);
        Assert.Contains("内网/保留地址", error.Message); // 私网判定命中，而非只报 scheme
    }

    [Fact]
    public async Task RedirectHttpsToHttp_InManifestMode_IsRejected()
    {
        var handler = new QueuedHandler(Redirect("http://93.184.216.34/downgraded"));

        var error = await Assert.ThrowsAsync<DownloadTransportException>(() =>
            SendAsyncAsync(handler, "https://93.184.216.34/manifest.json", enforcePublicHttps: true));

        Assert.True(error.Terminal);
        Assert.Contains("到不允许的地址", error.Message);
        Assert.Contains("https", error.Message); // 明文降级被拒
    }

    [Fact]
    public async Task RedirectToPrivateHost_InNormalMode_IsAllowedByDesign()
    {
        // 普通直链下载允许任意 http/https 主机（含局域网源）：重定向只需维持 scheme/协议约束
        var handler = new QueuedHandler(
            Redirect("http://192.168.1.5/lan.bin"),
            Ok([7]));

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/start");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("http://192.168.1.5/lan.bin", handler.Requests[1].RequestUri!.AbsoluteUri);
        await response.DisposeAsync();
    }

    [Fact]
    public async Task RedirectChainBeyondLimit_IsRejected()
    {
        var handler = new QueuedHandler(
            Redirect("https://93.184.216.34/h1"),
            Redirect("https://93.184.216.34/h2"),
            Redirect("https://93.184.216.34/h3"),
            Redirect("https://93.184.216.34/h4"),
            Redirect("https://93.184.216.34/h5"),
            Redirect("https://93.184.216.34/h6"), // 第 6 跳
            Ok([1]));

        var error = await Assert.ThrowsAsync<DownloadTransportException>(() =>
            SendAsyncAsync(handler, "https://93.184.216.34/start"));

        Assert.Contains("重定向次数超过", error.Message);
    }

    [Fact]
    public async Task RedirectWithoutLocation_IsRejected()
    {
        var handler = new QueuedHandler(new HttpResponseMessage(HttpStatusCode.Found));

        var error = await Assert.ThrowsAsync<DownloadTransportException>(() =>
            SendAsyncAsync(handler, "https://93.184.216.34/start"));

        Assert.True(error.Terminal);
        Assert.Contains("缺少 Location", error.Message);
    }

    [Fact]
    public async Task RangeHeader_IsPreservedAcrossRedirect()
    {
        var handler = new QueuedHandler(
            Redirect("https://93.184.216.34/real.bin", HttpStatusCode.TemporaryRedirect),
            Ok(new byte[16]));

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/start", rangeStart: 5, rangeEnd: 9);

        Assert.Equal(200, response.StatusCode);
        var range = handler.Requests[1].Headers.Range;
        Assert.NotNull(range);
        Assert.Equal("bytes=5-9", range.ToString());
        await response.DisposeAsync();
    }

    [Fact]
    public async Task IfRangeEtagValue_IsSentAsEntityTagCondition()
    {
        var handler = new QueuedHandler(Ok([1]));

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/fw.bin", ifRange: "\"v1\"");

        var ifRangeEtag = handler.Requests[0].Headers.IfRange;
        Assert.NotNull(ifRangeEtag);
        Assert.NotNull(ifRangeEtag.EntityTag);
        await response.DisposeAsync();
    }

    [Fact]
    public async Task IfRangeDateValue_IsSentAsDateCondition()
    {
        var handler = new QueuedHandler(Ok([1]));

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/fw.bin",
            ifRange: "Wed, 01 Jan 2025 00:00:00 GMT");

        var ifRangeDate = handler.Requests[0].Headers.IfRange;
        Assert.NotNull(ifRangeDate);
        Assert.NotNull(ifRangeDate.Date); // Last-Modified 兜底走日期条件
        await response.DisposeAsync();
    }

    [Fact]
    public async Task RetryAfterHeader_IsParsedIntoResponse()
    {
        var tooManyRequests = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("slow down", Encoding.UTF8, "text/plain")
        };
        tooManyRequests.Headers.Add("Retry-After", "7");
        var handler = new QueuedHandler(tooManyRequests);

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/fw.bin");

        Assert.Equal(7, response.RetryAfterSeconds);
        await response.DisposeAsync();
    }

    /// <summary>先给一段数据再抛 IOException 的流：模拟真实网络连接中途被重置。</summary>
    private sealed class FaultingStream(byte[] firstChunk) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served) throw new IOException("connection reset by peer (模拟)");
            _served = true;
            var count = Math.Min(buffer.Length, firstChunk.Length);
            firstChunk.AsSpan(0, count).CopyTo(buffer.Span);
            await Task.Yield();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ContentStreamMidReadFault_MapsToTransportException()
    {
        var handler = new QueuedHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FaultingStream([1, 2, 3]))
        });

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/fw.bin");
        var buffer = new byte[8];
        Assert.Equal(3, await response.Content.ReadAsync(buffer));
        // 第二次读抛 IOException → 必须映射为可重试的 DownloadTransportException，而不是裸 IO 异常穿透
        await Assert.ThrowsAsync<DownloadTransportException>(async () => await response.Content.ReadAsync(buffer));
        await response.DisposeAsync();
    }

    [Fact]
    public async Task ContentStreamCancellation_PassesThroughAsOperationCanceled()
    {
        var cancelled = new CancellationToken(canceled: true);
        var stream = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancellationTokenStream())
        };
        var handler = new QueuedHandler(stream);

        var response = await SendAsyncAsync(handler, "https://93.184.216.34/fw.bin");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await response.Content.ReadAsync(new byte[8], cancelled));
        await response.DisposeAsync();
    }

    /// <summary>读到即抛 OCE 的流（取消令牌由调用方传入 ReadAsync）：取消语义必须原样放行（映射层不得吞成传输错误）。</summary>
    private sealed class CancellationTokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
