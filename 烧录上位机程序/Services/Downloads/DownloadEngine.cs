using System.Collections.Concurrent;
using System.IO;
using Microsoft.Win32.SafeHandles;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Packaging;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>服务器对 Range 请求返回 200（忽略 Range）时抛出；引擎原地降级为单线程顺序下载。</summary>
internal sealed class RangeIgnoredException : Exception
{
    public RangeIgnoredException() : base("服务器忽略 Range 请求。") { }
}

/// <summary>单任务下载编排：探测 → 分段并发（worker 拉段队列自动均衡）→ 终验 → 原子提交。
/// 每个下载任务使用独立引擎实例（非重入）。候选地址（镜像）按序 failover，续传进度跨镜像保留。
/// 取消/失败时排空全部 worker 才返回（IpScan 同款 WhenAll 排空语义），.dltmp + .dltask.json 保留供续传。</summary>
public sealed class DownloadEngine
{
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);
    public const int DefaultMaxAttemptsPerSegment = 5;
    public const int BufferSize = 64 * 1024;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan JournalSaveInterval = TimeSpan.FromSeconds(2);
    private const long JournalSaveByteInterval = 512 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IDownloadTransport _transport;
    private readonly DownloadRateLimiter? _globalRateLimiter;
    private readonly TimeSpan _stallTimeout;
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;
    private readonly Func<long> _clock;

    private RunState? _state;

    public DownloadEngine(IDownloadTransport transport,
        DownloadRateLimiter? globalRateLimiter = null,
        TimeSpan? stallTimeout = null,
        int? maxAttempts = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        Func<long>? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _globalRateLimiter = globalRateLimiter;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _maxAttempts = maxAttempts ?? DefaultMaxAttemptsPerSegment;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _clock = clock ?? DefaultClock;
    }

    private static long DefaultClock() => Environment.TickCount64;

    private static Task DefaultRetryDelay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    public async Task<DownloadEngineResult> DownloadAsync(DownloadOptions options,
        Func<DownloadProgress, Task> reportAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reportAsync);
        if (_state is not null) throw new InvalidOperationException("DownloadEngine 实例不支持并发/复用，请为每个任务新建实例。");
        DownloadUrlGuard.EnsureDownloadUrl(options.Url);
        var candidates = BuildCandidates(options);
        _state = new RunState(options, reportAsync, _clock);
        try
        {
            Exception lastError = new DownloadTransportException("没有可用下载地址。");
            for (var index = 0; index < candidates.Count; index++)
            {
                var isLast = index == candidates.Count - 1;
                try
                {
                    return await DownloadFromCandidateAsync(candidates[index], cancellationToken).ConfigureAwait(false);
                }
                catch (DownloadTransportException error)
                {
                    lastError = error;
                    if (isLast) break;
                    await _state.ReportAsync(DownloadTaskState.Connecting, 0, 0, 0,
                        $"节点不可用，切换下一个（{index + 1}/{candidates.Count}）：{error.Message}", immediate: true,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            var suffix = candidates.Count > 1 ? $"（已尝试 {candidates.Count} 个地址）" : "";
            return DownloadEngineResult.FromFailure(lastError.Message + suffix, _state.Total, _state.Done);
        }
        finally
        {
            _state = null;
        }
    }

    private static IReadOnlyList<string> BuildCandidates(DownloadOptions options)
    {
        var candidates = new List<string> { options.Url.Trim() };
        foreach (var mirror in options.MirrorUrls)
        {
            var trimmed = mirror.Trim();
            if (trimmed.Length == 0) continue;
            DownloadUrlGuard.EnsureDownloadUrl(trimmed); // 镜像节点同样只允许 http/https（公网校验在清单层完成）
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) candidates.Add(trimmed);
        }
        return candidates;
    }

    // ---------------------------------------------------------------- candidate 层

    private async Task<DownloadEngineResult> DownloadFromCandidateAsync(string candidateUrl,
        CancellationToken cancellationToken)
    {
        var state = _state!;
        await state.ReportAsync(DownloadTaskState.Connecting, state.Total, state.Done, 0, "正在连接并探测资源…",
            immediate: true, cancellationToken).ConfigureAwait(false);
        var probe = await RangeProbe.ProbeAsync(_transport, candidateUrl, cancellationToken).ConfigureAwait(false);
        if (probe.StatusCode == 200 && probe.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new DownloadTransportException("服务器返回网页而非文件，可能地址已失效、需要登录或被防盗链拦截。");

        var fileName = ResolveFileName(state.Options, probe, candidateUrl);
        var finalPath = Path.Combine(state.Options.DestinationDirectory, fileName);
        state.Total = probe.TotalLength ?? 0;

        var useSegments = probe.SupportsRanges && probe.TotalLength is > SegmentPlanner.SingleStreamThreshold;
        return useSegments
            ? await DownloadSegmentedAsync(candidateUrl, probe, finalPath, cancellationToken).ConfigureAwait(false)
            : await DownloadSingleStreamAsync(candidateUrl, probe, finalPath, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveFileName(DownloadOptions options, RangeProbeResult probe, string url)
    {
        var name = !string.IsNullOrWhiteSpace(options.FileNameOverride) ? options.FileNameOverride
            : !string.IsNullOrEmpty(probe.FileName) ? probe.FileName
            : UrlFileName(url);
        name = PackagingFileSystem.SafeFileSegment(name.Trim());
        return name.Length == 0 ? "download.bin" : name;
    }

    private static string UrlFileName(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var last = path.Split('/').LastOrDefault(segment => segment.Length > 0) ?? "";
        return Uri.UnescapeDataString(last);
    }

    // ---------------------------------------------------------------- 分段路径

    private async Task<DownloadEngineResult> DownloadSegmentedAsync(string candidateUrl, RangeProbeResult probe,
        string finalPath, CancellationToken cancellationToken)
    {
        var state = _state!;
        var total = probe.TotalLength!.Value;
        var journal = state.Options.AllowResume
            ? DownloadJournal.TryOpen(finalPath, state.Options.Url) // 损坏时抛 IOException，由上层作为任务失败处理
            : null;
        var connections = SegmentPlanner.ClampConnections(state.Options.Connections ?? state.DefaultConnections);

        DownloadSegment[] segments;
        var resumed = false;
        if (journal is not null)
        {
            segments = journal.ReadSegments().ToArray();
            var etagCompatible = journal.ETag.Length == 0 || probe.ETag.Length == 0
                || string.Equals(journal.ETag, probe.ETag, StringComparison.Ordinal);
            if (!etagCompatible || journal.TotalLength != total || !CoversRange(segments, total))
            {
                var doneSoFar = segments.Sum(segment => segment.Completed);
                await state.ReportAsync(DownloadTaskState.Downloading, total, doneSoFar, 0,
                    "远端资源已变更或大小不一致，放弃续传记录重新下载。", immediate: true, cancellationToken)
                    .ConfigureAwait(false);
                DownloadJournal.DeleteAll(finalPath);
                journal = null;
                segments = StartFresh(finalPath, total, connections, probe, state.Options, out journal);
            }
            else resumed = true;
        }
        else
        {
            segments = StartFresh(finalPath, total, connections, probe, state.Options, out journal);
        }

        var handle = File.OpenHandle(DownloadJournal.TempPathFor(finalPath), FileMode.Open, FileAccess.Write,
            FileShare.None, FileOptions.Asynchronous);
        try
        {
            Interlocked.Exchange(ref state.DoneRaw, segments.Sum(segment => segment.Completed));
            state.Total = total;
            var pending = Enumerable.Range(0, segments.Length).Where(i => !segments[i].IsComplete).ToArray();
            var workerCount = Math.Min(SegmentPlanner.ClampConnections(state.Options.Connections
                ?? state.DefaultConnections), pending.Length);
            if (workerCount > 0)
            {
                var pendingQueue = new ConcurrentQueue<int>(pending); // worker 拉段队列：先完成者取下一段，自动负载均衡
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
                    {
                        while (pendingQueue.TryDequeue(out var segmentIndex))
                        {
                            await TransferSegmentAsync(candidateUrl, journal, segments, segmentIndex, handle,
                                probe.ETag, cancellationToken).ConfigureAwait(false);
                        }
                    }, cancellationToken))).ConfigureAwait(false);
                }
                catch (RangeIgnoredException)
                {
                    handle.Dispose();
                    await state.ReportAsync(DownloadTaskState.Downloading, total, 0, 0,
                        "服务器忽略 Range 请求，转为单线程顺序下载。", immediate: true, cancellationToken).ConfigureAwait(false);
                    return await DownloadSingleStreamAsync(candidateUrl, probe, finalPath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (segments.Any(segment => !segment.IsComplete))
                throw new DownloadTransportException("部分分段未完成。");

            var verified = await VerifyAndCommitAsync(finalPath, total, handle, journal, cancellationToken).ConfigureAwait(false);
            return DownloadEngineResult.FromSuccess(verified, total, resumed);
        }
        finally
        {
            handle.Dispose();
            // 异常/取消出口统一落盘一次最新分段表（提交成功后日志已删除，跳过）。
            if (File.Exists(DownloadJournal.JournalPathFor(finalPath)) && segments is not null)
                journal?.SaveSegments(segments);
        }
    }

    /// <summary>单个 worker 的一个分段传输：带重试/退避的完整收尾。RangeIgnored 与终态错误直接上抛。</summary>
    private async Task TransferSegmentAsync(string candidateUrl, DownloadJournal journal,
        DownloadSegment[] segments, int segmentIndex, SafeFileHandle handle, string probeETag,
        CancellationToken cancellationToken)
    {
        var state = _state!;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CopySegmentAsync(candidateUrl, journal, segments, segmentIndex, handle, probeETag,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RangeIgnoredException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DownloadTransportException error) when (attempt < _maxAttempts && !error.Terminal)
            {
                var backoff = BackoffFor(attempt, error.RetryAfterSeconds);
                await state.ReportAsync(DownloadTaskState.Downloading, state.Total, state.Done, state.ActiveSegments,
                    $"分段重试（第 {attempt}/{_maxAttempts} 次，{backoff.TotalMilliseconds:0}ms 后）：{error.Message}",
                    immediate: true, cancellationToken).ConfigureAwait(false);
                await _retryDelay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan BackoffFor(int attempt, int? retryAfterSeconds)
    {
        var exponential = TimeSpan.FromMilliseconds(
            Math.Min(MaxBackoff.TotalMilliseconds, InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1)));
        if (retryAfterSeconds is { } seconds)
        {
            var honoured = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
            exponential = honoured > exponential ? honoured : exponential;
        }
        return exponential;
    }

    private async Task CopySegmentAsync(string candidateUrl, DownloadJournal journal, DownloadSegment[] segments,
        int segmentIndex, SafeFileHandle handle, string probeETag, CancellationToken cancellationToken)
    {
        var state = _state!;
        var segment = segments[segmentIndex];
        var requestStart = segment.Start + segment.Completed;
        var ifRange = string.IsNullOrEmpty(probeETag) ? null : probeETag;
        var response = await _transport.SendAsync(
            new TransportRequest(candidateUrl, requestStart, segment.End, ifRange), cancellationToken)
            .ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            if (response.StatusCode == 200) throw new RangeIgnoredException();
            if (response.StatusCode != 206)
                throw RangeProbe.Classify(response.StatusCode) is { } classified
                    ? ApplyRetryAfter(classified, response.RetryAfterSeconds)
                    : new DownloadTransportException($"意外的状态码 {response.StatusCode}。");
            if (!RangeProbe.TryParseContentRange(response.ContentRange, out var contentStart, out _, out _)
                || contentStart != requestStart)
                throw new DownloadTransportException($"分段错位（期望从 {requestStart} 开始，服务器返回 {contentStart}）。");

            Interlocked.Increment(ref state.ActiveSegmentsRaw);
            try
            {
                var buffer = new byte[BufferSize];
                while (segments[segmentIndex].Remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    segment = segments[segmentIndex];
                    var granted = await AcquireBandwidthAsync(Math.Min(BufferSize, (int)segment.Remaining),
                        cancellationToken).ConfigureAwait(false);
                    var read = await ReadWithWatchdogAsync(response.Content, buffer, granted, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        throw new DownloadTransportException("分段数据提前结束（连接被中断）。");
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read),
                        segment.Start + segment.Completed, cancellationToken).ConfigureAwait(false);
                    segments[segmentIndex] = segment with { Completed = segment.Completed + read };
                    Interlocked.Add(ref state.DoneRaw, read);
                    await state.MaybeSaveJournalAsync(journal, segments, cancellationToken).ConfigureAwait(false);
                    await state.ReportAsync(DownloadTaskState.Downloading, state.Total, state.Done,
                        state.ActiveSegments, "", immediate: false, cancellationToken).ConfigureAwait(false);
                }

                // 分段完成属于状态跃迁，绕过节流立即上报（UI 分段计数/暂停判定依赖它）。
                await state.ReportAsync(DownloadTaskState.Downloading, state.Total, state.Done,
                    state.ActiveSegments, "", immediate: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref state.ActiveSegmentsRaw);
            }
        }
    }

    private async Task<int> AcquireBandwidthAsync(int requested, CancellationToken cancellationToken)
    {
        var taskLimiter = _state!.TaskRateLimiter;
        var granted = taskLimiter is null ? requested : await taskLimiter.AcquireAsync(requested, cancellationToken)
            .ConfigureAwait(false);
        if (_globalRateLimiter is not null)
            granted = Math.Min(granted, await _globalRateLimiter.AcquireAsync(granted, cancellationToken)
                .ConfigureAwait(false));
        return granted;
    }

    /// <summary>带看门狗的读：超过 stallTimeout 无数据视为卡死，转为可重试错误。</summary>
    private async Task<int> ReadWithWatchdogAsync(Stream stream, byte[] buffer, int count,
        CancellationToken cancellationToken)
    {
        var readTask = stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).AsTask();
        var winner = await Task.WhenAny(readTask, Task.Delay(_stallTimeout, cancellationToken)).ConfigureAwait(false);
        if (winner != readTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DownloadTransportException($"分段超过 {_stallTimeout.TotalSeconds:0} 秒无数据，重试该分段。");
        }
        return await readTask.ConfigureAwait(false);
    }

    private static bool CoversRange(IReadOnlyList<DownloadSegment> segments, long total)
    {
        long covered = 0;
        foreach (var segment in segments)
        {
            if (segment.Start != covered) return false;
            covered += segment.Length;
        }
        return covered == total;
    }

    /// <summary>全新下载的落盘准备：分段规划 → 预分配 .dltmp → 创建续传日志。</summary>
    private static DownloadSegment[] StartFresh(string finalPath, long total, int connections,
        RangeProbeResult probe, DownloadOptions options, out DownloadJournal journal)
    {
        var segments = SegmentPlanner.Plan(total, connections).ToArray();
        using (var preallocate = new FileStream(DownloadJournal.TempPathFor(finalPath), FileMode.Create,
            FileAccess.Write, FileShare.None))
        {
            preallocate.SetLength(total);
        }
        journal = DownloadJournal.Create(finalPath, options.Url, probe.ETag, probe.LastModified,
            total, Path.GetFileName(finalPath), ExpectedSha256Of(options), segments);
        return segments;
    }

    private static DownloadTransportException ApplyRetryAfter(DownloadTransportException error, int? retryAfterSeconds) =>
        retryAfterSeconds is null ? error
            : new DownloadTransportException(error.Message, error.StatusCode, error.Terminal, retryAfterSeconds);

    // ---------------------------------------------------------------- 单流路径

    /// <summary>单线程顺序下载：小文件、不支持 Range 或分段降级。单流不做续传（重跑即重来）。</summary>
    private async Task<DownloadEngineResult> DownloadSingleStreamAsync(string candidateUrl, RangeProbeResult probe,
        string finalPath, CancellationToken cancellationToken)
    {
        var state = _state!;
        var expectedTotal = probe.TotalLength;
        DownloadJournal.DeleteAll(finalPath); // 清掉可能存在的分段续传记录（布局不兼容）
        var tempPath = DownloadJournal.TempPathFor(finalPath);
        var handle = File.OpenHandle(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        try
        {
            var response = await _transport.SendAsync(new TransportRequest(candidateUrl, null, null, null),
                cancellationToken).ConfigureAwait(false);
            long written = 0;
            await using (response.ConfigureAwait(false))
            {
                if (response.StatusCode is not (200 or 206))
                    throw RangeProbe.Classify(response.StatusCode);
                if (response.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new DownloadTransportException("服务器返回网页而非文件，可能地址已失效、需要登录或被防盗链拦截。");
                if (expectedTotal is null && response.ContentLength is { } inline) expectedTotal = inline;
                if (expectedTotal is > 0)
                {
                    RandomAccess.SetLength(handle, expectedTotal.Value);
                    state.Total = expectedTotal.Value;
                }
                else state.Total = 0; // 未知总长：进度条以 0 表示

                var buffer = new byte[BufferSize];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var granted = await AcquireBandwidthAsync(BufferSize, cancellationToken).ConfigureAwait(false);
                    var read = await ReadWithWatchdogAsync(response.Content, buffer, granted, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), written, cancellationToken)
                        .ConfigureAwait(false);
                    written += read;
                    Interlocked.Exchange(ref state.DoneRaw, written);
                    await state.ReportAsync(DownloadTaskState.Downloading, state.Total, state.Done,
                        1, "", immediate: false, cancellationToken).ConfigureAwait(false);
                }
            }

            if (expectedTotal is { } expected && written != expected)
                throw new DownloadTransportException($"文件大小不符：实际 {written} 字节 / 期望 {expected} 字节。");
            RandomAccess.FlushToDisk(handle);
            handle.Dispose(); // 单流同样先释放句柄再校验
            var sha = ExpectedSha256Of(state.Options);
            if (sha.Length > 0)
                await VerifySha256Async(tempPath, sha, cancellationToken).ConfigureAwait(false);

            await state.ReportAsync(DownloadTaskState.Committing, state.Total, state.Done, 0, "正在保存文件…",
                immediate: true, cancellationToken).ConfigureAwait(false);
            var target = MoveToFinal(tempPath, finalPath);
            await state.ReportAsync(DownloadTaskState.Completed, state.Total, state.Done, 0,
                $"已保存到：{target}", immediate: true, CancellationToken.None).ConfigureAwait(false);
            return DownloadEngineResult.FromSuccess(target, written, resumed: false);
        }
        finally
        {
            handle.Dispose();
        }
    }

    // ---------------------------------------------------------------- 校验与提交

    private async Task<string> VerifyAndCommitAsync(string finalPath, long total, SafeFileHandle handle,
        DownloadJournal journal, CancellationToken cancellationToken)
    {
        var state = _state!;
        var tempPath = DownloadJournal.TempPathFor(finalPath);
        await state.ReportAsync(DownloadTaskState.Verifying, total, total, 0, "正在校验文件…",
            immediate: true, cancellationToken).ConfigureAwait(false);
        // 先落盘并释放写句柄，校验读才能打开文件（写句柄为独占）。
        RandomAccess.FlushToDisk(handle);
        handle.Dispose();
        var actualLength = new FileInfo(tempPath).Length;
        if (actualLength != total)
            throw new DownloadTransportException($"文件大小不符：实际 {actualLength} 字节 / 期望 {total} 字节。");
        var sha = ExpectedSha256Of(state.Options);
        if (sha.Length > 0)
            await VerifySha256Async(tempPath, sha, cancellationToken).ConfigureAwait(false);

        await state.ReportAsync(DownloadTaskState.Committing, total, total, 0, "正在保存文件…",
            immediate: true, cancellationToken).ConfigureAwait(false);
        var target = MoveToFinal(tempPath, finalPath);
        journal.DeleteJournal();
        await state.ReportAsync(DownloadTaskState.Completed, total, total, 0,
            $"已保存到：{target}", immediate: true, CancellationToken.None).ConfigureAwait(false);
        return target;
    }

    private static async Task VerifySha256Async(string tempPath, string expected, CancellationToken cancellationToken)
    {
        var actual = await Sha256FileHasher.HashFileAsync(tempPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new DownloadTransportException(
                $"文件校验不通过（sha256 不符，期望 {expected.Trim()}，实际 {actual}），已保留临时文件供排查。", terminal: true);
    }

    /// <summary>提交：目标已存在时自动改为 "名称 (n).扩展" 而不是覆盖既有文件。</summary>
    private static string MoveToFinal(string tempPath, string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);
        var target = finalPath;
        for (var n = 1; File.Exists(target); n++)
        {
            var name = Path.GetFileNameWithoutExtension(finalPath);
            var extension = Path.GetExtension(finalPath);
            target = Path.Combine(directory, $"{name} ({n}){extension}");
        }
        File.Move(tempPath, target);
        return target;
    }

    private static string ExpectedSha256Of(DownloadOptions options) => options.ExpectedSha256?.Trim() ?? "";

    // ---------------------------------------------------------------- 运行期状态

    /// <summary>一次下载运行的共享可变状态：进度（节流 + EMA 速度）、journal 节流落盘、速率限制器。</summary>
    private sealed class RunState
    {
        private readonly Func<long> _clock;
        private readonly Func<DownloadProgress, Task> _sink;
        private readonly SemaphoreSlim _reportGate = new(1);
        private readonly SemaphoreSlim _journalGate = new(1);
        private long _lastReportMilliseconds;
        private long _lastSpeedSampleMilliseconds;
        private long _lastSpeedSampleBytes;
        private long _journalSaveBytes;
        private long _journalSaveMilliseconds;
        private double _speed;

        public RunState(DownloadOptions options, Func<DownloadProgress, Task> sink, Func<long> clock)
        {
            Options = options;
            DefaultConnections = 8;
            TaskRateLimiter = options.MaxRateKBps is { } kbps and > 0
                ? new DownloadRateLimiter(kbps * 1024)
                : null;
            _sink = sink;
            _clock = clock;
        }

        public DownloadOptions Options { get; }
        public int DefaultConnections { get; }
        public DownloadRateLimiter? TaskRateLimiter { get; }
        public long Total;
        public long DoneRaw;
        public int ActiveSegmentsRaw;
        public long Done => Volatile.Read(ref DoneRaw);
        public int ActiveSegments => Volatile.Read(ref ActiveSegmentsRaw);

        public async Task ReportAsync(DownloadTaskState state, long total, long done, int activeSegments,
            string message, bool immediate, CancellationToken cancellationToken)
        {
            if (!immediate)
            {
                var now = _clock();
                if (now - Volatile.Read(ref _lastReportMilliseconds) < ProgressInterval.TotalMilliseconds) return;
            }
            await _reportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _clock();
                if (!immediate && now - _lastReportMilliseconds < ProgressInterval.TotalMilliseconds) return;
                var sampleMilliseconds = now - _lastSpeedSampleMilliseconds;
                if (sampleMilliseconds > 0)
                {
                    var instant = (done - _lastSpeedSampleBytes) * 1000.0 / sampleMilliseconds;
                    _speed = _speed <= 0 ? instant : _speed * 0.7 + instant * 0.3;
                    _lastSpeedSampleMilliseconds = now;
                    _lastSpeedSampleBytes = done;
                }
                _lastReportMilliseconds = now;
                await _sink(new DownloadProgress(state, total, done, (long)_speed, activeSegments, message))
                    .ConfigureAwait(false);
            }
            finally { _reportGate.Release(); }
        }

        public async Task MaybeSaveJournalAsync(DownloadJournal journal, DownloadSegment[] segments,
            CancellationToken cancellationToken)
        {
            var now = _clock();
            var done = Done;
            if (done - Volatile.Read(ref _journalSaveBytes) < JournalSaveByteInterval
                && now - Volatile.Read(ref _journalSaveMilliseconds) < JournalSaveInterval.TotalMilliseconds) return;
            await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = _clock();
                if (done - _journalSaveBytes < JournalSaveByteInterval
                    && now - _journalSaveMilliseconds < JournalSaveInterval.TotalMilliseconds) return;
                journal.SaveSegments(segments);
                _journalSaveBytes = done;
                _journalSaveMilliseconds = now;
            }
            finally { _journalGate.Release(); }
        }
    }
}
