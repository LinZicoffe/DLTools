using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace DLTools.Tests;

/// <summary>故障配置：确定性故障按"数据请求序号"取模求值（序号只对非探测请求递增，
/// 探测请求默认不注入故障，保证用例可复现；FaultProbeRequests=true 时探测同样注入）。
/// IgnoreRange 与 Latency 属服务器行为而非故障，对探测请求同样生效。</summary>
internal sealed record FaultProfile
{
    public bool IgnoreRange { get; init; }
    public long? DropAfterBytes { get; init; }
    public int? DropEvery { get; init; }
    public int? FailEvery { get; init; }
    public int FailStatus { get; init; } = 503;
    public int? RetryAfterSeconds { get; init; }
    public int? MaxConcurrent { get; init; }
    public TimeSpan Latency { get; init; }
    public Func<HttpListenerRequest, TimeSpan>? LatencySelector { get; init; }
    public long? StallAtBytes { get; init; }
    public int? StallEvery { get; init; }
    public long? RateBytesPerSecond { get; init; }
    public Func<int, bool>? RateSelector { get; init; }
    public int? EtagChangeAfterRequest { get; init; }
    public string ETag { get; init; } = "\"v1\"";
    public string LastModified { get; init; } = "";
    public bool FaultProbeRequests { get; init; }
}

internal sealed record RequestLogEntry(int Seq, long Start, long End, int Status, long BytesSent, long ElapsedMs);

/// <summary>本机回环 HttpListener 故障注入 Range 服务器（集成测试专用，DLTOOLS_DOWNLOAD_INTEGRATION=1 门控）。
/// 逐 64KiB 块写出以支持掐断/卡死/滴流（DropAfterBytes/StallAtBytes 实际触发点向上取整到块边界）；
/// Profile 可运行中整体更换（续传用例第二轮关故障，在途请求持旧快照）。
/// 卡死的请求挂起到服务器 Dispose 才收尾（HttpListener 无法感知客户端断开），期间仍占在途计数：
/// 限连接+卡死组合场景的 503 数会虚高；用例须 using 持有服务器。</summary>
internal sealed class FaultInjectionServer : IDisposable
{
    private const int ChunkSize = 64 * 1024;

    private HttpListener _listener = new(); // 端口被占时重建重试，不能 readonly
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private readonly byte[] _data;
    private readonly List<(long Start, long End)> _ranges = [];
    private readonly List<RequestLogEntry> _log = [];
    private readonly List<Task> _handlers = [];
    private FaultProfile _profile;
    private int _concurrent;
    private int _dataInFlight;
    private int _requests;
    private int _dataRequests;
    private int _peakConcurrent;
    private int _disposed;

    public FaultInjectionServer(byte[] data, FaultProfile? profile = null)
    {
        _data = data;
        _profile = profile ?? new FaultProfile();
        // 端口偶发占用时重抽（最多 3 次），避免集成测试因端口冲突闪挂
        int port;
        for (var attempt = 0; ; attempt++)
        {
            port = RandomNumberGenerator.GetInt32(20000, 40000);
            _listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 2)
            {
                _listener.Close();
                _listener = new HttpListener();
            }
        }
        BaseUrl = $"http://localhost:{port}";
        _loop = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    var handler = Task.Run(() => HandleAsync(context), _stop.Token);
                    lock (_gate) _handlers.Add(handler);
                }
            }
            catch (Exception error) when (error is ObjectDisposedException or HttpListenerException) { }
        });
    }

    public string BaseUrl { get; }

    public FaultProfile Profile
    {
        get { lock (_gate) return _profile; }
        set { lock (_gate) _profile = value; }
    }

    public int Requests { get { lock (_gate) return _requests; } }
    public int DataRequests => Volatile.Read(ref _dataRequests);
    public int PeakConcurrent { get { lock (_gate) return _peakConcurrent; } }

    /// <summary>确定性负载填充（非加密用途；仅保证用例可复现、SHA-256 可断言）。</summary>
    public static byte[] GenerateData(long size, byte seed = 99)
    {
        var data = new byte[size];
        for (var i = 0L; i < size; i++)
            data[i] = (byte)(i * 31 + seed);
        return data;
    }

    public IReadOnlyList<(long Start, long End)> SnapshotRanges()
    {
        lock (_gate) return _ranges.ToArray();
    }

    public IReadOnlyList<RequestLogEntry> SnapshotLog()
    {
        lock (_gate) return _log.ToArray();
    }

    private string CurrentETag(FaultProfile profile, int seq) =>
        profile.EtagChangeAfterRequest is { } changeAfter && seq > changeAfter ? "\"v2\"" : profile.ETag;

    private async Task HandleAsync(HttpListenerContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var profile = Profile;
        var request = context.Request;
        var response = context.Response;
        var rangeHeader = request.Headers["Range"];
        var isProbe = rangeHeader == "bytes=0-0";
        var seq = isProbe ? 0 : Interlocked.Increment(ref _dataRequests);
        var applyFaults = !isProbe || profile.FaultProbeRequests;
        long start = 0;
        long end = _data.Length - 1;
        var status = 200;
        long sent = 0;
        var countedAsData = false;
        lock (_gate) _requests++;
        var peak = Interlocked.Increment(ref _concurrent);
        lock (_gate) _peakConcurrent = Math.Max(_peakConcurrent, peak);
        try
        {
            if (!isProbe)
            {
                Interlocked.Increment(ref _dataInFlight);
                countedAsData = true;
            }

            if (applyFaults && profile.MaxConcurrent is { } max && Volatile.Read(ref _dataInFlight) > max)
            {
                status = 503;
                response.StatusCode = 503;
                if (profile.RetryAfterSeconds is { } retryAfter)
                    response.Headers["Retry-After"] = retryAfter.ToString();
                response.ContentLength64 = 0;
                return;
            }
            if (applyFaults && profile.FailEvery is { } failEvery && failEvery > 0 && seq % failEvery == 0)
            {
                status = profile.FailStatus;
                response.StatusCode = status;
                if (profile.RetryAfterSeconds is { } retryAfter)
                    response.Headers["Retry-After"] = retryAfter.ToString();
                response.ContentLength64 = 0;
                return;
            }

            var wait = profile.LatencySelector?.Invoke(request) ?? profile.Latency;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _stop.Token).ConfigureAwait(false);

            var etag = CurrentETag(profile, seq);
            // If-Range 与当前验证器不符 → 资源已变更语义：回 200 全量（引擎据此降级单流）
            var ifRange = request.Headers["If-Range"];
            var validatorMatches = ifRange is null
                || string.Equals(ifRange, etag, StringComparison.Ordinal)
                || (profile.LastModified.Length > 0
                    && string.Equals(ifRange, profile.LastModified, StringComparison.Ordinal));

            var partial = !profile.IgnoreRange && validatorMatches && rangeHeader is not null
                && rangeHeader.StartsWith("bytes=", StringComparison.Ordinal)
                && long.TryParse(rangeHeader["bytes=".Length..].Split('-')[0], out start);
            if (partial)
            {
                var tail = rangeHeader!.Split('-');
                if (tail.Length > 1 && long.TryParse(tail[1], out var parsedEnd)) end = parsedEnd;
                status = 206;
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes {start}-{end}/{_data.Length}";
            }
            else
            {
                start = 0;
                end = _data.Length - 1;
                status = 200;
                response.StatusCode = 200;
            }
            if (!(status == 206 && isProbe)) lock (_gate) _ranges.Add((start, end));
            response.ContentType = "application/octet-stream";
            response.ContentLength64 = end - start + 1;
            if (etag.Length > 0) response.Headers["ETag"] = etag;
            if (profile.LastModified.Length > 0) response.Headers["Last-Modified"] = profile.LastModified;

            var dropThis = applyFaults && profile.DropAfterBytes is not null
                && (profile.DropEvery is null || (profile.DropEvery > 0 && seq % profile.DropEvery == 0));
            var stallThis = applyFaults && profile.StallAtBytes is not null
                && (profile.StallEvery is null || (profile.StallEvery > 0 && seq % profile.StallEvery == 0));
            var rate = applyFaults && profile.RateBytesPerSecond is { } bytesPerSecond && bytesPerSecond > 0
                && (profile.RateSelector is null || profile.RateSelector(seq))
                    ? bytesPerSecond
                    : 0;

            var buffer = new byte[ChunkSize];
            var offset = start;
            var pace = Stopwatch.StartNew();
            while (offset <= end)
            {
                _stop.Token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkSize, end - offset + 1);
                if (rate > 0)
                {
                    var targetMilliseconds = (sent + count) * 1000.0 / rate;
                    var delayMilliseconds = targetMilliseconds - pace.Elapsed.TotalMilliseconds;
                    if (delayMilliseconds > 1)
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), _stop.Token).ConfigureAwait(false);
                }
                Array.Copy(_data, offset, buffer, 0, count);
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, count), _stop.Token).ConfigureAwait(false);
                sent += count;
                offset += count;
                if (dropThis && sent >= profile.DropAfterBytes!.Value)
                {
                    response.Abort();
                    return;
                }
                if (stallThis && sent >= profile.StallAtBytes!.Value)
                    await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            try { response.Abort(); } catch { /* 客户端取消导致的中断 */ }
        }
        finally
        {
            if (countedAsData) Interlocked.Decrement(ref _dataInFlight);
            Interlocked.Decrement(ref _concurrent);
            lock (_gate) _log.Add(new RequestLogEntry(seq, start, end, status, sent, stopwatch.ElapsedMilliseconds));
            try { response.Close(); } catch { /* Abort 后 Close 可能失败 */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* 接受循环退出 */ }
        // 等全部 handler 收尾（含卡死请求被取消后的日志落盘），Dispose 返回后 SnapshotLog 即完整
        Task[] handlers;
        lock (_gate) handlers = _handlers.ToArray();
        try { Task.WaitAll(handlers, TimeSpan.FromSeconds(5)); } catch { /* handler 异常已在内部吞掉 */ }
    }
}