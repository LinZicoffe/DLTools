namespace 烧录上位机程序.Services.Downloads;

/// <summary>worker 的一次任务分配。RequestStart/RequestEnd 为闭区间，直接喂 TransportRequest。</summary>
public readonly record struct Assignment(int SegmentIndex, long RequestStart, long RequestEnd);

/// <summary>Claim 裁决：GrantedBytes 为本次允许写入的字节数（可能被段尾截断）；Lost=true 表示段已被抢占/劈走，须停止写入重新 Assign。</summary>
public readonly record struct ClaimResult(int GrantedBytes, bool Lost);

/// <summary>动态"对半劈"分段规划器（纯逻辑，单锁，无 I/O）。
/// 内部半开区间 [Start,End)，不变量 Start≤Cursor≤Reserved≤End：Cursor=已 commit 落盘水位，
/// Reserved=已 claim 允许写入水位（未 commit 的 claim 在 Release/抢占/崩溃恢复时天然回滚）。
/// Assign 三级：①领未分配未完成段（优先衔接本 worker 上次完成处）；②抢占相邻空闲段
/// （!Receiving 且超过宽限期 max(2×TTFB,20ms)，防重复已在途请求）；③对半劈剩余最大段
/// （劈点在 Reserved 之后按 4KiB 对齐，后半≥minSplit 才劈，绝不切正在写的字节）。
/// 与 journal 映射保持 SchemaVersion=1：End 落盘减一转闭区间，Reserved 不落盘。</summary>
public sealed class DynamicSegmentPlanner
{
    public const long DefaultMinSplitBytes = 12 * 1024;
    public const long DefaultSplitAlignment = 4096;
    private const long MinPreemptAfterMs = 20;

    private readonly object _gate = new();
    private readonly List<PlannerSegment> _segments;
    private readonly Dictionary<int, long> _lastCompletedEnd = [];
    private readonly long _minSplitBytes;
    private readonly long _splitAlignment;
    private readonly Func<long> _clock;
    private long _preemptAfterMs = MinPreemptAfterMs;
    private long _version;

    public DynamicSegmentPlanner(long totalLength, long minSplitBytes = DefaultMinSplitBytes,
        long splitAlignment = DefaultSplitAlignment, Func<long>? clock = null)
    {
        if (totalLength <= 0) throw new ArgumentOutOfRangeException(nameof(totalLength), "总长度必须为正数。");
        if (minSplitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(minSplitBytes));
        if (splitAlignment <= 0) throw new ArgumentOutOfRangeException(nameof(splitAlignment));
        _minSplitBytes = minSplitBytes;
        _splitAlignment = splitAlignment;
        _clock = clock ?? (static () => Environment.TickCount64);
        _segments = [new PlannerSegment { Start = 0, End = totalLength }];
    }

    /// <summary>从 journal 段表恢复（兼容旧静态 2MiB 布局与任意粒度）：校验连续覆盖总长，
    /// Cursor=Start+Completed、Reserved=Cursor（未 commit 的 claim 与崩溃语义吻合，天然回滚）。</summary>
    public static DynamicSegmentPlanner Resume(long totalLength, IReadOnlyList<DownloadSegment> segments,
        long minSplitBytes = DefaultMinSplitBytes, long splitAlignment = DefaultSplitAlignment,
        Func<long>? clock = null)
    {
        var planner = new DynamicSegmentPlanner(totalLength, minSplitBytes, splitAlignment, clock);
        long covered = 0;
        var restored = new List<PlannerSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.Start != covered || segment.Length <= 0 || segment.Completed < 0 || segment.Completed > segment.Length)
                throw new ArgumentException("续传段表未连续覆盖总长、含零长段或 Completed 越界。", nameof(segments));
            covered += segment.Length;
            restored.Add(new PlannerSegment
            {
                Start = segment.Start,
                End = segment.Start + segment.Length,
                Cursor = segment.Start + segment.Completed,
                Reserved = segment.Start + segment.Completed
            });
        }
        if (covered != totalLength || restored.Count == 0)
            throw new ArgumentException("续传段表未连续覆盖总长。", nameof(segments));
        planner._segments.Clear();
        planner._segments.AddRange(restored);
        return planner;
    }

    /// <summary>探测往返时延（毫秒）→ 抢占宽限期 max(2×TTFB, 20ms)。</summary>
    public void SetProbeTtfb(long milliseconds)
    {
        lock (_gate) _preemptAfterMs = Math.Max(2 * milliseconds, MinPreemptAfterMs);
    }

    public bool IsComplete { get { lock (_gate) return _segments.TrueForAll(segment => segment.Cursor == segment.End); } }

    public long CompletedBytes { get { lock (_gate) return _segments.Sum(segment => segment.Cursor - segment.Start); } }

    public int ReceivingCount { get { lock (_gate) return _segments.Count(segment => segment.Receiving); } }

    /// <summary>变更计数：任何归属/水位/拆分变化递增，供 worker 短轮询判活。</summary>
    public long Version { get { lock (_gate) return _version; } }

    /// <summary>给 worker 派活；null=暂时无活可干（等待他人推进后再试）。幂等：已持有段时原样返回。</summary>
    public Assignment? Assign(int workerId)
    {
        lock (_gate)
        {
            var now = _clock();
            var owned = _segments.FindIndex(segment => segment.Worker == workerId && segment.Cursor < segment.End);
            if (owned >= 0) return AssignmentOf(owned);

            // ① 未分配未完成段：优先衔接本 worker 上次完成处（磁盘/请求局部性），否则取最靠前
            var locality = _lastCompletedEnd.GetValueOrDefault(workerId, -1);
            var candidate = -1;
            for (var i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                if (segment.Worker != -1 || segment.Cursor == segment.End) continue;
                if (segment.Start == locality) { candidate = i; break; }
                if (candidate < 0 || segment.Start < _segments[candidate].Start) candidate = i;
            }
            if (candidate >= 0) return TakeOver(candidate, workerId, now);

            // ② 抢占相邻空闲段：原 worker 未在收数且派活已超过宽限期（请求可能已在途，宽限防重复发）
            for (var i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                if (segment.Worker == -1 || segment.Worker == workerId || segment.Receiving
                    || segment.Cursor == segment.End || segment.Start != locality) continue;
                if (now - segment.GivenAtMs < _preemptAfterMs) continue;
                return TakeOver(i, workerId, now);
            }

            // ③ 对半劈剩余（End-Reserved）最大段：只从 Reserved 之后切，绝不切正在写的字节
            var victim = -1;
            var largestRemaining = 0L;
            for (var i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                var remaining = segment.End - segment.Reserved;
                if (segment.Cursor == segment.End || remaining <= largestRemaining) continue;
                victim = i;
                largestRemaining = remaining;
            }
            if (victim >= 0)
            {
                var segment = _segments[victim];
                var half = (segment.End - segment.Reserved) / 2;
                var aligned = half / _splitAlignment * _splitAlignment;
                var splitAt = segment.Reserved + aligned;
                if (aligned > 0 && segment.End - splitAt >= _minSplitBytes)
                {
                    var tail = new PlannerSegment
                    {
                        Start = splitAt,
                        End = segment.End,
                        Cursor = splitAt,
                        Reserved = splitAt,
                        Worker = workerId,
                        GivenAtMs = now
                    };
                    segment.End = splitAt;
                    _segments.Add(tail);
                    _version++;
                    return AssignmentOf(_segments.Count - 1);
                }
            }
            return null;
        }
    }

    /// <summary>写入前裁决：段仍归该 worker 且 position 在 [Cursor,End) 内才授予（可能被段尾截断）；
    /// 否则 Lost——段已被抢占或劈走，调用方须丢弃在途数据重新 Assign。</summary>
    public ClaimResult Claim(int workerId, int segmentIndex, long position, int length)
    {
        lock (_gate)
        {
            if (segmentIndex < 0 || segmentIndex >= _segments.Count || length <= 0)
                return new ClaimResult(0, true);
            var segment = _segments[segmentIndex];
            if (segment.Worker != workerId || position < segment.Cursor || position >= segment.End)
                return new ClaimResult(0, true);
            var granted = (int)Math.Min(length, segment.End - position);
            segment.Reserved = Math.Max(segment.Reserved, position + granted);
            _version++;
            return new ClaimResult(granted, false);
        }
    }

    /// <summary>落盘后推进 Cursor。必须紧随成功的 Claim 且 position==Cursor（顺序写）；
    /// 违反即调用方 bug，抛异常而非静默 corrupt。段写满时记录完成处供局部性派活。</summary>
    public void Commit(int workerId, int segmentIndex, long position, int length)
    {
        lock (_gate)
        {
            if (segmentIndex < 0 || segmentIndex >= _segments.Count)
                throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            var segment = _segments[segmentIndex];
            if (segment.Worker != workerId || position != segment.Cursor || length <= 0
                || position + length > segment.Reserved)
                throw new InvalidOperationException("Commit 必须紧随成功 Claim 且顺序推进（worker/位置/Reserved 校验失败）。");
            segment.Cursor = position + length;
            if (segment.Cursor == segment.End)
            {
                _lastCompletedEnd[workerId] = segment.End;
                segment.Worker = -1;
                segment.Receiving = false;
            }
            _version++;
        }
    }

    /// <summary>可重试失败/被劈后放弃在途数据：Reserved 回滚到 Cursor、解除归属（已被抢占则无操作）。</summary>
    public void Release(int workerId, int segmentIndex)
    {
        lock (_gate)
        {
            if (segmentIndex < 0 || segmentIndex >= _segments.Count) return;
            var segment = _segments[segmentIndex];
            if (segment.Worker != workerId) return;
            segment.Reserved = segment.Cursor;
            segment.Worker = -1;
            segment.Receiving = false;
            _version++;
        }
    }

    /// <summary>首字节到达：收数中的段不被抢占（仍可被对半劈，劈点在 Reserved 之后不影响在途写）。</summary>
    public void MarkReceiving(int workerId, int segmentIndex)
    {
        lock (_gate)
        {
            if (segmentIndex < 0 || segmentIndex >= _segments.Count) return;
            var segment = _segments[segmentIndex];
            if (segment.Worker != workerId || segment.Cursor == segment.End) return;
            if (!segment.Receiving)
            {
                segment.Receiving = true;
                _version++;
            }
        }
    }

    /// <summary>journal 段表快照：按 Start 排序（对半劈把尾段追加在列表末尾，索引保持稳定但位置乱序），
    /// 半开转闭区间（End-1），Completed=Cursor-Start；Reserved 不落盘。</summary>
    public IReadOnlyList<DownloadSegment> Snapshot()
    {
        lock (_gate)
        {
            return _segments.OrderBy(segment => segment.Start).Select(segment =>
                new DownloadSegment(segment.Start, segment.End - 1, segment.Cursor - segment.Start)).ToArray();
        }
    }

    private Assignment AssignmentOf(int index)
    {
        var segment = _segments[index];
        return new Assignment(index, segment.Cursor, segment.End - 1);
    }

    private Assignment TakeOver(int index, int workerId, long now)
    {
        var segment = _segments[index];
        segment.Worker = workerId;
        segment.Reserved = segment.Cursor; // 前任未 commit 的 claim 回滚，由新主从 Cursor 重取
        segment.Receiving = false;
        segment.GivenAtMs = now;
        _version++;
        return AssignmentOf(index);
    }

    private sealed class PlannerSegment
    {
        public long Start;
        public long Cursor;
        public long Reserved;
        public long End;
        public int Worker = -1;
        public bool Receiving;
        public long GivenAtMs;
    }
}