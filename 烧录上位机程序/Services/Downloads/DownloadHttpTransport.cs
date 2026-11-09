using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using 烧录上位机程序.Models.Downloads;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>生产 HTTP 传输：SocketsHttpHandler + HTTP/1.1 钉死（避免 H2 把多段 Range 挤进单连接被服务端限速）、
/// 连接超时 10s、整体超时无限（卡死交给引擎看门狗）。Content 流由 TransportResponse.DisposeAsync 释放；
/// HttpResponseMessage 本体不释放——其资源随 Content 流释放归还连接池。</summary>
public sealed class DownloadHttpTransport : IDownloadTransport, IDisposable
{
    public const string UserAgent = "DLTools/1.0"; // UA 头要求 ASCII token，中文产品名会被 HttpHeaderParser 拒绝

    private readonly HttpClient _client;

    public DownloadHttpTransport(DownloadSettings? settings = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = DecompressionMethods.None // 分段下载必须拿到原始字节，解压会破坏 Content-Range 对应关系
        };
        if (settings is { HasProxy: true })
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(settings.ProxyHost, settings.ProxyPort);
        }
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
    }

    public async Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (request.RangeStart is { } start)
            httpRequest.Headers.Range = new RangeHeaderValue(start, request.RangeEnd);
        if (!string.IsNullOrEmpty(request.IfRangeETag)
            && EntityTagHeaderValue.TryParse(request.IfRangeETag, out var ifRange))
            httpRequest.Headers.IfRange = new RangeConditionHeaderValue(ifRange);
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

        var content = response.Content;
        content.Headers.TryGetValues("Content-Disposition", out var dispositions);
        content.Headers.TryGetValues("Retry-After", out var retryAfter);
        return new TransportResponse(
            (int)response.StatusCode,
            content.Headers.ContentType?.MediaType ?? "",
            response.Headers.ETag?.ToString() ?? "",
            content.Headers.LastModified?.ToString() ?? "",
            content.Headers.ContentRange?.ToString(),
            content.Headers.ContentLength,
            dispositions is null ? "" : string.Join(", ", dispositions),
            ParseRetryAfterSeconds(retryAfter?.FirstOrDefault()),
            await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
    }

    private static int? ParseRetryAfterSeconds(string? value) =>
        value is not null && int.TryParse(value.Trim(), out var seconds) && seconds >= 0 ? seconds : null;

    public void Dispose() => _client.Dispose();
}
