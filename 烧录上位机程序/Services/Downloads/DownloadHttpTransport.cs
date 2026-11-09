using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using 烧录上位机程序.Models.Downloads;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>生产 HTTP 传输：SocketsHttpHandler + HTTP/1.1 钉死（避免 H2 把多段 Range 挤进单连接被服务端限速）、
/// 连接超时 10s、整体超时无限（卡死交给引擎看门狗）。重定向不自动跟随——逐跳校验后再继续
/// （普通下载仅要求 http/https；镜像清单链路要求 https 公网，防止 30x 旁路首跳校验）。
/// Content 流由 TransportResponse.DisposeAsync 释放；HttpResponseMessage 本体不释放——
/// 其资源随 Content 流释放归还连接池。读取阶段的中断异常映射为可重试的 DownloadTransportException。</summary>
public sealed class DownloadHttpTransport : IDownloadTransport, IDisposable
{
    public const string UserAgent = "DLTools/1.0"; // UA 头要求 ASCII token，中文产品名会被 HttpHeaderParser 拒绝
    public const int MaxRedirects = 5;

    private readonly HttpClient _client;

    public DownloadHttpTransport(DownloadSettings? settings = null)
        : this(CreateHandler(settings), settings)
    {
    }

    /// <summary>注入自定义 HttpMessageHandler（重定向/异常映射的单元测试接缝；代理设置仅对默认 SocketsHttpHandler 生效）。</summary>
    public DownloadHttpTransport(HttpMessageHandler handler, DownloadSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        _ = settings; // 代理等设置已由 CreateHandler 消化；注入 handler 时由调用方自行决定
    }

    private static SocketsHttpHandler CreateHandler(DownloadSettings? settings)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = DecompressionMethods.None, // 分段下载必须拿到原始字节，解压会破坏 Content-Range 对应关系
            AllowAutoRedirect = false // 重定向由本类逐跳校验后手动跟随，禁止 HttpClient 跳过校验
        };
        if (settings is { HasProxy: true })
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(settings.ProxyHost, settings.ProxyPort);
        }
        return handler;
    }

    public async Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        var currentUrl = request.Url;
        for (var hop = 0; ; hop++)
        {
            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var requestUri))
                throw new DownloadTransportException($"下载地址无法解析：{currentUrl}", terminal: true);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (request.RangeStart is { } start)
                httpRequest.Headers.Range = new RangeHeaderValue(start, request.RangeEnd);
            if (!string.IsNullOrEmpty(request.IfRangeETag))
                httpRequest.Headers.IfRange = ParseIfRange(request.IfRangeETag);
            // 分段请求钉死 HTTP/1.1：HTTP/2 多路复用会让所有 Range 段共享一条 TCP 连接，反而失去多连接加速。
            httpRequest.Version = HttpVersion.Version11;
            httpRequest.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new DownloadTransportException("连接失败：" + error.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DownloadTransportException("连接超时（10 秒内未建立连接），请检查网络或代理设置。");
            }

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
                or (HttpStatusCode)308)
            {
                var redirectStatus = (int)response.StatusCode;
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                    throw new DownloadTransportException(
                        $"服务器重定向（{redirectStatus}）缺少 Location 头。", redirectStatus, terminal: true);
                if (hop >= MaxRedirects)
                    throw new DownloadTransportException(
                        $"重定向次数超过 {MaxRedirects} 次，已停止跟随。", redirectStatus, terminal: true);
                Uri absolute;
                try { absolute = new Uri(requestUri, location); }
                catch (UriFormatException)
                {
                    throw new DownloadTransportException($"重定向地址非法：{location}", redirectStatus, terminal: true);
                }
                ValidateRedirectTarget(absolute, request.EnforcePublicHttps, redirectStatus);
                currentUrl = absolute.AbsoluteUri;
                continue;
            }

            var content = response.Content;
            content.Headers.TryGetValues("Content-Disposition", out var dispositions);
            // Retry-After 属响应头，个别网关放内容头：两处都查
            if (!response.Headers.TryGetValues("Retry-After", out var retryAfter))
                content.Headers.TryGetValues("Retry-After", out retryAfter);
            Stream contentStream;
            try
            {
                contentStream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                response.Dispose(); // 读内容失败也要释放响应，连接才能及时归还池
                throw new DownloadTransportException("读取响应内容失败：" + error.Message);
            }
            return new TransportResponse(
                (int)response.StatusCode,
                content.Headers.ContentType?.MediaType ?? "",
                response.Headers.ETag?.ToString() ?? "",
                content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                content.Headers.ContentRange?.ToString(),
                content.Headers.ContentLength,
                dispositions is null ? "" : string.Join(", ", dispositions),
                ParseRetryAfterSeconds(retryAfter?.FirstOrDefault()),
                new GuardedContentStream(contentStream));
        }
    }

    /// <summary>重定向的每一跳都要重新过 URL 校验：镜像链路维持 https+公网要求，普通链路维持 http/https 要求。</summary>
    private static void ValidateRedirectTarget(Uri absolute, bool enforcePublicHttps, int redirectStatus)
    {
        try
        {
            if (enforcePublicHttps) DownloadUrlGuard.EnsureManifestUrl(absolute.AbsoluteUri);
            else DownloadUrlGuard.EnsureDownloadUrl(absolute.AbsoluteUri);
        }
        catch (ArgumentException error)
        {
            throw new DownloadTransportException(
                $"重定向（{redirectStatus}）到不允许的地址：" + error.Message, redirectStatus, terminal: true);
        }
    }

    /// <summary>If-Range 值既可能是 ETag（带引号）也可能是 HTTP 日期（Last-Modified 兜底）。</summary>
    private static RangeConditionHeaderValue? ParseIfRange(string value)
    {
        if (EntityTagHeaderValue.TryParse(value, out var etag)) return new RangeConditionHeaderValue(etag);
        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var rfc1123))
            return new RangeConditionHeaderValue(rfc1123);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return new RangeConditionHeaderValue(parsed);
        return null; // 无法解析时省略头：退回"长度对账 + 状态码检查"防线
    }

    private static int? ParseRetryAfterSeconds(string? value) =>
        value is not null && int.TryParse(value.Trim(), out var seconds) && seconds >= 0 ? seconds : null;

    public void Dispose() => _client.Dispose();

    /// <summary>内容流包装：把读取阶段的中断（连接被重置等，表现为 IOException/SocketException/HttpRequestException/
    /// ObjectDisposedException）映射为可重试的 DownloadTransportException；OperationCanceledException 原样放行。</summary>
    private sealed class GuardedContentStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return inner.Read(buffer, offset, count); }
            catch (Exception error) when (IsTransportFault(error)) { throw MapFault(error); }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (IsTransportFault(error)) { throw MapFault(error); }
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private static bool IsTransportFault(Exception error) =>
            error is IOException or SocketException or HttpRequestException or ObjectDisposedException;

        private static DownloadTransportException MapFault(Exception error) =>
            new("连接中断：" + error.Message);
    }
}
