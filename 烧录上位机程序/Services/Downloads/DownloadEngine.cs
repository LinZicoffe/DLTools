using System.Collections.Concurrent;
using System.Diagnostics;
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

/// <summary>单任务下载编排：探测 → 动态分段并发（Assign 三级调度 + claim/commit 水位）→ 终验 → 原子提交。
/// 每个下载任务使用独立引擎实例（非重入）。候选地址（镜像）按序 failover，续传进度跨镜像保留。
/// 取消/失败时排空全部 worker 才返回（IpScan 同款 WhenAll 排空语义），.dltmp + .dltask.json 保留供续传。</summary>
public sealed class DownloadEngine
{
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);
    public const int DefaultMaxAttemptsPerSegment = 5;
    public const int BufferSize = 64 * 1024;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    // Retry-After 单独放宽到 5 分钟：尊重服务器明示的等待要求，但异常头不能把任务挂死
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan AssignPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan JournalCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan JournalForceSaveInterval = TimeSpan.FromSeconds(30);
    private const long JournalSaveByteIntervalMin = 1024 * 1024;
    private const long JournalSaveByteIntervalMax = 64 * 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IDownloadTransport _transport;
    private readonly DownloadRateLimiter? _globalRateLimiter;
    private readonly DownloadHostLimits? _hostLimits;
    private readonly TimeSpan _stallTimeout;
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;
    private readonly Func<long> _clock;
    private readonly bool _shedEnabled;
    private readonly TimeSpan _shedCheckInterval;

    private RunState? _state;

    public DownloadEngine(IDownloadTransport transport,
        DownloadRateLimiter? globalRateLimiter = null,
        TimeSpan? stallTimeout = null,
        int? maxAttempts = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        Func<long>? clock = null,
        DownloadHostLimits? hostLimits = null,
        bool shedEnabled = true,
        TimeSpan? shedCheckInterval = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (shedCheckInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shedCheckInterval), "shed 检查间隔必须为正。");
        _globalRateLimiter = globalRateLimiter;
        _hostLimits = hostLimits;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _maxAttempts = maxAttempts ?? DefaultMaxAttemptsPerSegment;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _clock = clock ?? DefaultClock;
        _shedEnabled = shedEnabled;
        _shedCheckInterval = shedCheckInterval ?? TimeSpan.FromMilliseconds(500);
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
                    ReportRefusal(candidates[index], error); // failover 出口同样上报（此刻收数已排空，学习器自行忽略）
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
        // 探测与分段请求同级重试：单候选任务不应因一次瞬时 429/5xx/连接抖动整体失败（429 尊重 Retry-After）
        RangeProbeResult probe;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var probeStarted = _clock();
                probe = await RangeProbe.ProbeAsync(_transport, candidateUrl, cancellationToken,
                    state.Options.EnforcePublicHttps).ConfigureAwait(false);
                state.ProbeTtfbMs = Math.Max(0, _clock() - probeStarted); // 抢占宽限期基准 max(2×TTFB,20ms)
                break;
            }
            catch (DownloadTransportException error) when (!error.Terminal)
            {
                ReportRefusal(candidateUrl, error); // 末次尝试同样上报（F07 遗留补齐）；此刻无人收数则学习器自行忽略
                if (attempt >= _maxAttempts) throw;
                var backoff = BackoffFor(attempt, error.RetryAfterSeconds);
                await state.ReportAsync(DownloadTaskState.Connecting, state.Total, state.Done, 0,
                    $"探测重试（第 {attempt}/{_maxAttempts} 次，{backoff.TotalMilliseconds:0}ms 后）：{error.Message}",
                    immediate: true, cancellationToken).ConfigureAwait(false);
                await _retryDelay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }
        if (probe.StatusCode == 200 && probe.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            && !HasExplicitFileName(state.Options, probe))
            throw new DownloadTransportException("服务器返回网页而非文件，可能地址已失效、需要登录或被防盗链拦截。");

        var fileName = state.DecidedFileName ?? ResolveFileName(state.Options, probe, candidateUrl);
        state.DecidedFileName = fileName; // 首个候选定名后供 failover 复用，保证续传记录/落盘名跨镜像一致
        var finalPath = Path.Combine(state.Options.DestinationDirectory, fileName);
        state.Total = probe.TotalLength ?? 0;

        var useSegments = probe.SupportsRanges && probe.TotalLength is > SegmentPlanner.SingleStreamThreshold;
        return useSegments
            ? await DownloadSegmentedAsync(candidateUrl, probe, finalPath, cancellationToken).ConfigureAwait(false)
            : await DownloadSingleStreamAsync(candidateUrl, probe, finalPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>text/html 防盗链启发式只对"没有显式文件名"的响应生效：显式指定/Content-Disposition 命名的 html 允许下载。</summary>
    private static bool HasExplicitFileName(DownloadOptions options, RangeProbeResult probe) =>
        !string.IsNullOrWhiteSpace(options.FileNameOverride) || probe.FileNameFromHeader;

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

        DynamicSegmentPlanner planner;
        var resumed = false;
        if (journal is not null)
        {
            var segments = journal.ReadSegments().ToArray();
            // 新鲜度校验：双侧有强 ETag 则必须相等；弱 ETag（W/ 前缀）不是强验证器，连同"任一侧缺失"一律退回
            // Last-Modified 比对且双侧都有才可能兼容——无验证器的服务器变更同长度内容时必须重下，防止拼出混合版本文件
            var validatorsCompatible = IsStrongETag(journal.ETag) && IsStrongETag(probe.ETag)
                ? string.Equals(journal.ETag, probe.ETag, StringComparison.Ordinal)
                : journal.LastModified.Length > 0 && probe.LastModified.Length > 0
                    && string.Equals(journal.LastModified, probe.LastModified, StringComparison.Ordinal);
            if (!validatorsCompatible || journal.TotalLength != total || !CoversRange(segments, total))
            {
                var doneSoFar = segments.Sum(segment => segment.Completed);
                await state.ReportAsync(DownloadTaskState.Downloading, total, doneSoFar, 0,
                    "远端资源已变更或大小不一致，放弃续传记录重新下载。", immediate: true, cancellationToken)
                    .ConfigureAwait(false);
                DownloadJournal.DeleteAll(finalPath);
                journal = null;
                planner = StartFresh(finalPath, total, probe, state.Options, out journal);
            }
            else
            {
                planner = DynamicSegmentPlanner.Resume(total, segments); // 兼容旧静态段表与任意粒度
                resumed = true;
            }
        }
        else
        {
            planner = StartFresh(finalPath, total, probe, state.Options, out journal);
        }
        planner.SetProbeTtfb(state.ProbeTtfbMs);

        var handle = File.OpenHandle(DownloadJournal.TempPathFor(finalPath), FileMode.Open, FileAccess.Write,
            FileShare.None, FileOptions.Asynchronous);
        // If-Range 校验值：优先强 ETag（弱 ETag 禁入 If-Range，RFC 7233 §3.2），其次 Last-Modified
        // （资源已变更时服务器回 200，触发单流重下）
        var ifRangeValue = IsStrongETag(probe.ETag) ? probe.ETag : probe.LastModified;
        try
        {
            Interlocked.Exchange(ref state.DoneRaw, planner.CompletedBytes);
            state.Total = total;
            // 初始 worker 数吸收已学主机上限与 64KiB 粒度下限；运行中由退休机制继续收敛
            var hostCap = _hostLimits?.GetCap(candidateUrl);
            var workerCount = (int)Math.Min(Math.Min(connections, hostCap ?? SegmentPlanner.MaxConnections),
                (total + BufferSize - 1) / BufferSize);
            if (workerCount > 0 && !planner.IsComplete)
            {
                Exception? firstError = null;
                var rangeIgnored = 0;
                var liveWorkers = workerCount;
                // worker 退休（F07 遗留接管）：可重试拒绝且收数段数已达已学上限且仍有同伴 → 让位退出
                bool ShouldRetire() => _hostLimits?.GetCap(candidateUrl) is { } cap
                    && planner.ReceivingCount >= cap && Volatile.Read(ref liveWorkers) > 1;
                // 慢连接 shed：限速器非空时禁用（限速等待污染"连接慢"归因）
                var shed = new DownloadShedPolicy(_clock)
                {
                    Enabled = _shedEnabled && state.TaskRateLimiter is null && _globalRateLimiter is null
                };
                var shedResponses = new ConcurrentDictionary<int, CancellationTokenSource>();
                try
                {
                    // 兄弟止损：单段致命失败立即取消其余 worker，避免失败已定后仍空转派活
                    using var failFast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var workers = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(async () =>
                    {
                        while (!planner.IsComplete)
                        {
                            var assignment = await AssignOrWaitAsync(planner, workerId, failFast.Token)
                                .ConfigureAwait(false);
                            if (assignment is null) break; // 全部完成
                            try
                            {
                                if (!await TransferAssignmentAsync(candidateUrl, journal, planner, workerId,
                                        assignment.Value, handle, ifRangeValue, ShouldRetire, shed, shedResponses,
                                        failFast.Token).ConfigureAwait(false))
                                {
                                    Interlocked.Decrement(ref liveWorkers);
                                    return; // 退休：段已 Release，会被同伴劈走
                                }
                            }
                            catch (Exception error) when (error is not OperationCanceledException)
                            {
                                if (error is RangeIgnoredException) Volatile.Write(ref rangeIgnored, 1);
                                else Interlocked.CompareExchange(ref firstError, error, null);
                                failFast.Cancel();
                                throw;
                            }
                        }
                    }, failFast.Token)).ToArray();
                    var monitor = shed.Enabled
                        ? Task.Run(async () =>
                        {
                            using var timer = new PeriodicTimer(_shedCheckInterval);
                            while (await timer.WaitForNextTickAsync(failFast.Token).ConfigureAwait(false))
                            {
                                foreach (var victim in shed.SelectVictims(_clock()))
                                {
                                    if (!shedResponses.TryGetValue(victim, out var responseCts)) continue;
                                    try { responseCts.Cancel(); }
                                    catch (ObjectDisposedException) { /* 响应已收尾释放：shed 落空无害 */ }
                                }
                            }
                        }, failFast.Token)
                        : Task.CompletedTask;
                    try
                    {
                        await Task.WhenAll(workers).ConfigureAwait(false);
                    }
                    finally
                    {
                        failFast.Cancel(); // 成功出口也停监控；失败出口此前已 Cancel，幂等
                        try { await monitor.ConfigureAwait(false); }
                        catch (OperationCanceledException) { /* 监控随 failFast 正常退出 */ }
                    }
                }
                catch (Exception error)
                {
                    if (Volatile.Read(ref rangeIgnored) == 1)
                    {
                        handle.Dispose();
                        await state.ReportAsync(DownloadTaskState.Downloading, total, 0, 0,
                            "服务器忽略 Range 请求，转为单线程顺序下载。", immediate: true, cancellationToken).ConfigureAwait(false);
                        return await DownloadSingleStreamAsync(candidateUrl, probe, finalPath, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    if (cancellationToken.IsCancellationRequested) throw; // 真取消/暂停：保持 OCE 语义上抛
                    throw Volatile.Read(ref firstError) ?? error; // 兄弟止损引发的 OCE 换回首个真实失败
                }
            }

            if (!planner.IsComplete)
                throw new DownloadTransportException("部分分段未完成。");

            var verified = await VerifyAndCommitAsync(finalPath, total, handle, journal, cancellationToken).ConfigureAwait(false);
            return DownloadEngineResult.FromSuccess(verified, total, resumed);
        }
        finally
        {
            // 异常/取消出口统一落盘一次最新段表（提交成功后日志已删除，跳过）。
            // 顺序硬约束：Snapshot 先于 FlushToDisk——快照后落盘的字节只会多不会少，方向安全（宁可少报重下）。
            if (File.Exists(DownloadJournal.JournalPathFor(finalPath)))
            {
                // 尽力而为落盘：磁盘满/杀毒占用等 IO 故障不得替换原始异常或取消语义（OCE 优先上抛），
                // 保存失败仅损失续传进度（方向安全：journal 永不超报已落盘字节）
                try
                {
                    var snapshot = planner.Snapshot();
                    if (!handle.IsClosed) RandomAccess.FlushToDisk(handle);
                    journal?.SaveSegments(snapshot);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            handle.Dispose();
        }
    }

    /// <summary>派活或短轮询等待：null=全部完成。10ms 轮询成本远低于一次网络往返，Version 供未来条件等待优化。</summary>
    private static async Task<Assignment?> AssignOrWaitAsync(DynamicSegmentPlanner planner, int workerId,
        CancellationToken cancellationToken)
    {
        while (!planner.IsComplete)
        {
            var assignment = planner.Assign(workerId);
            if (assignment is not null) return assignment;
            await Task.Delay(AssignPollInterval, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>单个 worker 的一次分配传输：带重试/退避的完整收尾。RangeIgnored 与终态错误直接上抛。
    /// 重试预算按"零进度连续失败"计算，进度取 worker 本地跨段累计 commit 字节（换段不清零）。
    /// 可重试失败先 Release 再退避，退避后重新 Assign（可能拿别的段）；返回 false=worker 退休。
    /// shed 收场判别：仅响应级 CTS 被取消而 failFast 未取消 → 慢连接丢弃，不耗预算零退避立即重 Assign；
    /// 真取消/暂停/兄弟止损经 failFast 上抛 OCE，绝不吞成 shed。
    /// 接线契约：读→MarkReceiving→Claim→写→Commit（首字节先标记收数，抢占才不会落入 Commit 抛异常窗口）。</summary>
    private async Task<bool> TransferAssignmentAsync(string candidateUrl, DownloadJournal journal,
        DynamicSegmentPlanner planner, int workerId, Assignment assignment, SafeFileHandle handle,
        string ifRangeValue, Func<bool> shouldRetire, DownloadShedPolicy shed,
        ConcurrentDictionary<int, CancellationTokenSource> shedResponses, CancellationToken cancellationToken)
    {
        const int maxTotalFailures = 1000; // 有进度重置的绝对上限，防病态服务器 1 字节/次拖死任务
        var state = _state!;
        var workerCommitted = 0L;
        var progressMark = 0L;
        var totalFailures = 0;
        var current = assignment;
        for (var attempt = 1; ; attempt++)
        {
            CancellationTokenSource? responseCts = null;
            var transferToken = cancellationToken;
            if (shed.Enabled)
            {
                responseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                shedResponses[workerId] = responseCts;
                transferToken = responseCts.Token;
            }
            try
            {
                await CopyAssignmentAsync(candidateUrl, journal, planner, workerId, current, handle, ifRangeValue,
                    bytes => workerCommitted += bytes, shed, transferToken).ConfigureAwait(false);
                return true;
            }
            catch (RangeIgnoredException)
            {
                throw;
            }
            catch (OperationCanceledException) when (responseCts is { IsCancellationRequested: true }
                && !cancellationToken.IsCancellationRequested)
            {
                planner.Release(workerId, current.SegmentIndex); // 段回收靠 Assign 三级规则，无新调度逻辑
                await state.ReportAsync(DownloadTaskState.Downloading, state.Total, state.Done, state.ActiveSegments,
                    "慢连接已丢弃，立即重连…", immediate: true, cancellationToken).ConfigureAwait(false);
                var next = await AssignOrWaitAsync(planner, workerId, cancellationToken).ConfigureAwait(false);
                if (next is null) return true;
                current = next.Value;
                attempt--; // shed 不消耗重试预算（continue 的 attempt++ 抵消）
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DownloadTransportException error)
            {
                if (error.Terminal) throw;
                ReportRefusal(candidateUrl, error);
                planner.Release(workerId, current.SegmentIndex); // 放弃在途数据，段可被同伴领取/劈走
                totalFailures++;
                var madeProgress = workerCommitted > progressMark;
                progressMark = workerCommitted;
                if (madeProgress) attempt = 0; // 有进度：连续零失败计数从头再来
                else if (attempt >= _maxAttempts || totalFailures >= maxTotalFailures) throw;
                if (shouldRetire()) return false;
                var attemptForBackoff = madeProgress ? 1 : attempt;
                var backoff = BackoffFor(attemptForBackoff, error.RetryAfterSeconds);
                await state.ReportAsync(DownloadTaskState.Downloading, state.Total, state.Done, state.ActiveSegments,
                    $"分段重试（第 {attemptForBackoff}/{_maxAttempts} 次，{backoff.TotalMilliseconds:0}ms 后）：{error.Message}",
                    immediate: true, cancellationToken).ConfigureAwait(false);
                await _retryDelay(backoff, cancellationToken).ConfigureAwait(false);
                var next = await AssignOrWaitAsync(planner, workerId, cancellationToken).ConfigureAwait(false);
                if (next is null) return true; // 等待期间同伴已全部完成
                current = next.Value;
            }
            finally
            {
                if (responseCts is not null)
                {
                    shedResponses.TryRemove(workerId, out _);
                    responseCts.Dispose();
                }
            }
        }
    }

    /// <summary>429/503/连接级故障视为并发拒绝信号上报；终态（403/404）与截断/停滞不是并发限制，不报。
    /// 此刻无人收数时 HostLimits 自行忽略（整站故障而非连接数限制）。</summary>
    private void ReportRefusal(string candidateUrl, DownloadTransportException error)
    {
        if (_hostLimits is null) return;
        if (error.StatusCode is 429 or 503 || error.ConnectionFault)
            _hostLimits.ReportRefused(candidateUrl, _state!.ReceivingConnections);
    }

    private static TimeSpan BackoffFor(int attempt, int? retryAfterSeconds)
    {
        var exponential = TimeSpan.FromMilliseconds(
            Math.Min(MaxBackoff.TotalMilliseconds, InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1)));
        if (retryAfterSeconds is { } seconds)
        {
            var honoured = TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfter.TotalSeconds));
            exponential = honoured > exponential ? honoured : exponential;
        }
        return exponential;
    }

    private async Task CopyAssignmentAsync(string candidateUrl, DownloadJournal journal,
        DynamicSegmentPlanner planner, int workerId, Assignment assignment, SafeFileHandle handle,
        string ifRangeValue, Action<long> onCommitted, DownloadShedPolicy shed, CancellationToken cancellationToken)
    {
        var state = _state!;
        var requestStart = assignment.RequestStart;
        var ifRange = string.IsNullOrEmpty(ifRangeValue) ? null : ifRangeValue;
        var response = await _transport.SendAsync(
            new TransportRequest(candidateUrl, requestStart, assignment.RequestEnd, ifRange, state.Options.EnforcePublicHttps),
            cancellationToken).ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            if (response.StatusCode == 200) throw new RangeIgnoredException();
            if (response.StatusCode != 206)
                throw RangeProbe.Classify(response.StatusCode, response.RetryAfterSeconds);
            if (!RangeProbe.TryParseContentRange(response.ContentRange, out var contentStart, out _, out _)
                || contentStart != requestStart)
                throw new DownloadTransportException($"分段错位（期望从 {requestStart} 开始，服务器返回 {contentStart}）。");

            Interlocked.Increment(ref state.ActiveSegmentsRaw);
            var reportedWorking = false; // 首次读到数据才算"正在收数"，供主机上限学习区分拒绝与收数
            try
            {
                var buffer = new byte[BufferSize];
                var position = requestStart;
                while (position <= assignment.RequestEnd)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var want = (int)Math.Min(BufferSize, assignment.RequestEnd - position + 1);
                    // 顺序：限速 Acquire → 看门狗读 → Claim → 写 → Commit。
                    // 限速等待期段可被劈走，由 Claim 裁决；部分授予先写自有前缀再重连（不浪费已读字节）。
                    var granted = await AcquireBandwidthAsync(want, cancellationToken).ConfigureAwait(false);
                    var netStarted = Stopwatch.GetTimestamp();
                    var read = await ReadWithWatchdogAsync(response.Content, buffer, granted, cancellationToken)
                        .ConfigureAwait(false);
                    var netReadMs = (long)Stopwatch.GetElapsedTime(netStarted).TotalMilliseconds;
                    if (read == 0)
                        throw new DownloadTransportException("分段数据提前结束（连接被中断）。");
                    if (!reportedWorking)
                    {
                        reportedWorking = true;
                        Interlocked.Increment(ref state.ReceivingConnectionsRaw);
                        _hostLimits?.ReportWorking(candidateUrl, state.ReceivingConnections);
                        planner.MarkReceiving(workerId, assignment.SegmentIndex); // 先标记再 Claim：收数段免抢占
                    }
                    var claim = planner.Claim(workerId, assignment.SegmentIndex, position, read);
                    if (claim.Lost) break; // 段被抢占：丢弃在途响应，回 Assign 重取
                    var writable = Math.Min(read, claim.GrantedBytes);
                    var diskStarted = Stopwatch.GetTimestamp();
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, writable), position, cancellationToken)
                        .ConfigureAwait(false);
                    var diskWaitMs = (long)Stopwatch.GetElapsedTime(diskStarted).TotalMilliseconds;
                    planner.Commit(workerId, assignment.SegmentIndex, position, writable);
                    onCommitted(writable);
                    Interlocked.Add(ref state.DoneRaw, writable);
                    position += writable;
                    // shed 采样：速度只计净读耗时，写盘耗时单独归因（>50ms 触发全体免判窗口）
                    shed.RecordSample(workerId, writable, netReadMs, diskWaitMs, assignment.RequestEnd - position + 1);
                    var journalStarted = Stopwatch.GetTimestamp();
                    await state.MaybeSaveJournalAsync(journal, planner, handle, cancellationToken).ConfigureAwait(false);
                    shed.RecordDiskStall((long)Stopwatch.GetElapsedTime(journalStarted).TotalMilliseconds);
                    await state.ReportAsync(DownloadTaskState.Downloading, state.Total, state.Done,
                        state.ActiveSegments, "", immediate: false, cancellationToken).ConfigureAwait(false);
                    if (claim.GrantedBytes < read) break; // 段尾被劈走：自有前缀已写，剩余重新 Assign 重发
                }

                // 分段完成属于状态跃迁，绕过节流立即上报（UI 分段计数/暂停判定依赖它）。
                await state.ReportAsync(DownloadTaskState.Downloading, state.Total, state.Done,
                    state.ActiveSegments, "", immediate: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (reportedWorking) Interlocked.Decrement(ref state.ReceivingConnectionsRaw);
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
            // 被放弃的读稍后会随响应流释放以异常收场：挂接观察者，避免 UnobservedTaskException 噪声
            _ = readTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
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

    /// <summary>弱 ETag（W/ 前缀）不能作续传验证器：RFC 7232 §2.3 要求强验证器，RFC 7233 §3.2 禁入 If-Range。</summary>
    private static bool IsStrongETag(string etag) =>
        etag.Length > 0 && !etag.StartsWith("W/", StringComparison.Ordinal);

    /// <summary>全新下载的落盘准备：初始单段规划器 → 预分配 .dltmp → 创建续传日志（段表取 Snapshot）。</summary>
    private static DynamicSegmentPlanner StartFresh(string finalPath, long total,
        RangeProbeResult probe, DownloadOptions options, out DownloadJournal journal)
    {
        var planner = new DynamicSegmentPlanner(total);
        using (var preallocate = new FileStream(DownloadJournal.TempPathFor(finalPath), FileMode.Create,
            FileAccess.Write, FileShare.None))
        {
            // 顺序硬约束：先标稀疏再 SetLength，否则 NTFS 扩展非稀疏文件会同步零填充
            if (total >= SparseFile.MinSparseLength) SparseFile.TryMarkSparse(preallocate.SafeFileHandle);
            preallocate.SetLength(total);
        }
        journal = DownloadJournal.Create(finalPath, options.Url, probe.ETag, probe.LastModified,
            total, Path.GetFileName(finalPath), ExpectedSha256Of(options), planner.Snapshot());
        return planner;
    }

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
        var reportedWorking = false;
        try
        {
            var response = await _transport.SendAsync(
                new TransportRequest(candidateUrl, null, null, null, state.Options.EnforcePublicHttps),
                cancellationToken).ConfigureAwait(false);
            long written = 0;
            await using (response.ConfigureAwait(false))
            {
                if (response.StatusCode is not (200 or 206))
                    throw RangeProbe.Classify(response.StatusCode, response.RetryAfterSeconds);
                if (response.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                    && !HasExplicitFileName(state.Options, probe)
                    && RangeProbe.ParseContentDispositionFileName(response.ContentDisposition).Length == 0)
                    throw new DownloadTransportException("服务器返回网页而非文件，可能地址已失效、需要登录或被防盗链拦截。");
                if (expectedTotal is null && response.ContentLength is { } inline) expectedTotal = inline;
                if (expectedTotal is > 0)
                {
                    if (expectedTotal.Value >= SparseFile.MinSparseLength) SparseFile.TryMarkSparse(handle);
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
                    if (!reportedWorking)
                    {
                        reportedWorking = true;
                        Interlocked.Increment(ref state.ReceivingConnectionsRaw);
                        _hostLimits?.ReportWorking(candidateUrl, state.ReceivingConnections);
                    }
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
            if (written >= SparseFile.MinSparseLength) SparseFile.TryClearSparse(handle);
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
        catch (DownloadTransportException error)
        {
            // 与分段路径语义对齐：先递减再上报，拒绝观察只计"别的连接正在收数"——
            // 单流自身断流不得把 cap 学到 1（F07 遗留补齐，F10 审查修正计数口径）
            if (reportedWorking)
            {
                Interlocked.Decrement(ref state.ReceivingConnectionsRaw);
                reportedWorking = false;
            }
            ReportRefusal(candidateUrl, error);
            throw;
        }
        finally
        {
            if (reportedWorking) Interlocked.Decrement(ref state.ReceivingConnectionsRaw);
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
        // 文件已写满无空洞：清稀疏位，免稀疏碎片拖慢 SHA256 顺序读与后续烧录读取
        if (total >= SparseFile.MinSparseLength) SparseFile.TryClearSparse(handle);
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
                $"文件校验不通过（sha256 不符，期望 {expected.Trim()}，实际 {actual}）。已保留临时文件供排查；"
                + "点击\"重试\"将自动改为全新下载。", terminal: true);
    }

    /// <summary>提交：目标已存在时自动改为 "名称 (n).扩展" 而不是覆盖既有文件。
    /// 存在性检查与移动之间可能被并发任务抢占同名，Move 失败时取下一个 "(n)" 名重试。</summary>
    private static string MoveToFinal(string tempPath, string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);
        for (var n = 0; n < 128; n++)
        {
            var target = n == 0 ? finalPath : NextCandidateName(finalPath, directory, n);
            try
            {
                File.Move(tempPath, target);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // 并发抢占：取下一个 "(n)" 名重试
            }
        }
        throw new IOException($"无法为目标文件找到可用名称：{finalPath}");
    }

    private static string NextCandidateName(string finalPath, string directory, int n)
    {
        var name = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(directory, $"{name} ({n}){extension}");
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
        private long _journalCheckMilliseconds;
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
        public string? DecidedFileName; // 首个候选确定的落盘名，failover 后续候选复用
        public long ProbeTtfbMs; // 探测往返时延，动态规划器抢占宽限期基准
        public long Total;
        public long DoneRaw;
        public int ActiveSegmentsRaw;
        public int ReceivingConnectionsRaw;
        public long Done => Volatile.Read(ref DoneRaw);
        public int ActiveSegments => Volatile.Read(ref ActiveSegmentsRaw);
        public int ReceivingConnections => Volatile.Read(ref ReceivingConnectionsRaw);

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
                    if (done < _lastSpeedSampleBytes)
                    {
                        // 进度回退（failover 重报 / 放弃续传清零）：重置速度基线，避免负瞬时速率污染 EMA
                        _speed = 0;
                    }
                    else
                    {
                        var instant = (done - _lastSpeedSampleBytes) * 1000.0 / sampleMilliseconds;
                        _speed = _speed <= 0 ? instant : _speed * 0.7 + instant * 0.3;
                    }
                    _lastSpeedSampleMilliseconds = now;
                    _lastSpeedSampleBytes = done;
                }
                _lastReportMilliseconds = now;
                await _sink(new DownloadProgress(state, total, done, (long)_speed, activeSegments, message))
                    .ConfigureAwait(false);
            }
            finally { _reportGate.Release(); }
        }

        /// <summary>journal 节流落盘：每 2s 检查一次，新增≥Clamp(total/16,1MiB,64MiB) 或距上次真写≥30s 才落盘
        /// （FlushToDisk 有真实磁盘成本）。顺序硬约束：Snapshot → FlushToDisk → SaveSegments——
        /// 快照先于 flush，落盘字节只会多不会少，崩溃后宁可少报重下也不许超报 corrupt。</summary>
        public async Task MaybeSaveJournalAsync(DownloadJournal journal, DynamicSegmentPlanner planner,
            SafeFileHandle handle, CancellationToken cancellationToken)
        {
            var now = _clock();
            if (now - Volatile.Read(ref _journalCheckMilliseconds) < JournalCheckInterval.TotalMilliseconds) return;
            await _journalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = _clock();
                if (now - _journalCheckMilliseconds < JournalCheckInterval.TotalMilliseconds) return;
                _journalCheckMilliseconds = now;
                var done = Done;
                var byteInterval = Math.Clamp(Total / 16, JournalSaveByteIntervalMin, JournalSaveByteIntervalMax);
                if (done - _journalSaveBytes < byteInterval
                    && now - _journalSaveMilliseconds < JournalForceSaveInterval.TotalMilliseconds) return;
                var snapshot = planner.Snapshot();
                RandomAccess.FlushToDisk(handle);
                // journal 持久化尽力而为：.dltask.json 被杀毒/索引器瞬时占用等瞬态 IO 故障不得报废整个下载
                // （只损失续传粒度，方向安全）；FlushToDisk 失败仍上抛——那是磁盘健康信号
                try { journal.SaveSegments(snapshot); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                _journalSaveBytes = done;
                _journalSaveMilliseconds = now;
            }
            finally { _journalGate.Release(); }
        }
    }
}
