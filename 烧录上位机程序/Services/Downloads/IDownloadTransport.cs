using System.IO;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>传输层异常。Terminal=true 表示重试无意义（如 403/404），引擎应直接失败而不是退避重试；
/// RetryAfterSeconds 携带 429 的 Retry-After 提示，退避时长取其与指数退避的较大值。</summary>
public sealed class DownloadTransportException : Exception
{
    public int? StatusCode { get; }
    public bool Terminal { get; }
    public int? RetryAfterSeconds { get; }

    public DownloadTransportException(string message, int? statusCode = null, bool terminal = false,
        int? retryAfterSeconds = null)
        : base(message)
    {
        StatusCode = statusCode;
        Terminal = terminal;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

/// <param name="Url">目标地址。</param>
/// <param name="RangeStart">Range 起点（含）；null 表示不带 Range 的整段请求。</param>
/// <param name="RangeEnd">Range 终点（含）。</param>
/// <param name="IfRangeETag">续传时的 If-Range 校验值，服务器资源已变更则返回 200 而非 206。</param>
public sealed record TransportRequest(string Url, long? RangeStart, long? RangeEnd, string? IfRangeETag);

/// <param name="StatusCode">HTTP 状态码。非 2xx 时 Content 为空流，由调用方分类处理。</param>
/// <param name="ContentRange">206 响应的 Content-Range 原始值（如 "bytes 0-0/123"）。</param>
/// <param name="ContentLength">Content-Length 头；206 时是剩余长度而非总长，不能当总长使用。</param>
/// <param name="ContentDisposition">Content-Disposition 头原始值，用于推断目标文件名。</param>
/// <param name="RetryAfterSeconds">429 响应的 Retry-After 头（秒）；无则 null。</param>
public sealed record TransportResponse(int StatusCode, string ContentType, string ETag, string LastModified,
    string? ContentRange, long? ContentLength, string ContentDisposition, int? RetryAfterSeconds, Stream Content)
    : IAsyncDisposable
{
    public static TransportResponse Empty(int statusCode) =>
        new(statusCode, "", "", "", null, 0, "", null, Stream.Null);

    public async ValueTask DisposeAsync()
    {
        try { await Content.DisposeAsync().ConfigureAwait(false); }
        catch { /* 连接中断时释放流可能抛出，忽略 */ }
    }
}

/// <summary>可注入 HTTP 传输接缝。生产实现为 DownloadHttpTransport；测试注入假实现驱动引擎全部分支。</summary>
public interface IDownloadTransport
{
    /// <exception cref="DownloadTransportException">连接失败、超时或协议错误。</exception>
    Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken);
}
