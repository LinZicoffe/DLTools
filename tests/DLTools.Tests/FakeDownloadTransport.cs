using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>可编程假传输：记录请求并按委托应答，驱动引擎/探测的全部分支。</summary>
public sealed class FakeDownloadTransport : IDownloadTransport
{
    private readonly Func<TransportRequest, CancellationToken, Task<TransportResponse>> _handler;
    private readonly object _gate = new();

    public FakeDownloadTransport(Func<TransportRequest, CancellationToken, Task<TransportResponse>> handler) =>
        _handler = handler;

    public List<TransportRequest> Requests { get; } = [];

    public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        lock (_gate) Requests.Add(request);
        return _handler(request, cancellationToken);
    }

    public IReadOnlyList<TransportRequest> SnapshotRequests()
    {
        lock (_gate) return Requests.ToArray();
    }

    public static TransportResponse OkFull(long totalLength, byte[] body,
        string contentType = "application/octet-stream", string etag = "\"v1\"", string disposition = "") =>
        new(200, contentType, etag, "", null, totalLength, disposition, null, new MemoryStream(body));

    public static TransportResponse Partial(long totalLength, long start, long end, byte[] body,
        string etag = "\"v1\"", string disposition = "") =>
        new(206, "application/octet-stream", etag, "",
            $"bytes {start}-{end}/{totalLength}", body.Length, disposition, null, new MemoryStream(body));

    public static TransportResponse RateLimited(int retryAfterSeconds) =>
        new(429, "text/plain", "", "", null, 0, "", retryAfterSeconds, Stream.Null);

    public static TransportResponse Status(int statusCode) => TransportResponse.Empty(statusCode);
}

/// <summary>带 Range 语义的假文件服务器；Override 注入失败（返回 null 继续正常逻辑）。
/// 探测请求（RangeStart=0 且 RangeEnd=0）回 1 字节 206；分段请求按 [start,end] 切片。</summary>
internal sealed class FakeSegmentServer
{
    public byte[] Data;
    public Func<TransportRequest, TransportResponse?>? Override;

    public FakeSegmentServer(byte[] data) => Data = data;

    public TransportResponse Serve(TransportRequest request)
    {
        if (Override is { } inject)
        {
            var injected = inject(request);
            if (injected is not null) return injected;
        }
        if (request.RangeStart is { } start && request.RangeEnd is 0)
            return FakeDownloadTransport.Partial(Data.Length, 0, 0, [Data[0]]);
        if (request.RangeStart is { } segmentStart)
        {
            var end = request.RangeEnd ?? Data.Length - 1;
            return FakeDownloadTransport.Partial(Data.Length, segmentStart, end,
                Data[(int)segmentStart..(int)(end + 1)]);
        }
        return FakeDownloadTransport.OkFull(Data.Length, Data);
    }
}
