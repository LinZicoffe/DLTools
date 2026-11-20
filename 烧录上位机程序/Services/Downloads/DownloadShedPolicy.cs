namespace 烧录上位机程序.Services.Downloads;

/// <summary>慢连接丢弃（shed）判定：纯逻辑 + 时钟可注入，监控循环每 500ms 调 SelectVictims。
/// 速度只计看门狗读耗时（Δbytes/ΔnetReadMs），限速与写盘等待不计——磁盘慢不是连接的错。
/// 基准=min(历史最佳单连接速度[区间须跑满 100ms 且收满 256KiB 才计入], max(链路 1s 窗口最佳合计,1)/活跃连接数)。
/// 受害=累计净读≥2s（新连接不判）&& 速度×4&lt;基准 && 剩余/速度&gt;2s（快收尾的不折腾）。
/// 写盘单次&gt;50ms → 全体免判 500ms（磁盘瓶颈期 shed 无益）；任一限速器非空时 Enabled=false。
/// 被 shed 的连接零退避重连、不耗重试预算，段由 Assign 三级规则回收。</summary>
public sealed class DownloadShedPolicy
{
    private const long MatureNetReadMs = 2000;
    private const long BaselineMinNetReadMs = 100;
    private const long BaselineMinBytes = 256 * 1024;
    private const double SlowFactor = 4.0;
    private const long FinishWithinMs = 2000;
    private const long DiskStallThresholdMs = 50;
    private const long DiskExemptionMs = 500;
    private const long LinkWindowMs = 1000;
    private const long LinkMinSpanMs = 500;

    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly Dictionary<int, WorkerState> _workers = new();
    private readonly List<(long TimeMs, long TotalBytes)> _linkHistory = [];
    private double _bestSingleSpeed;
    private double _bestLinkSpeed;
    private double _currentBaseline;
    private long _diskExemptUntilMs;
    private long _lastSelectMs;

    public DownloadShedPolicy(Func<long>? clock = null) => _clock = clock ?? (static () => Environment.TickCount64);

    /// <summary>任一限速器非空时置 false：限速等待会污染"连接慢"的归因，整体禁用。运行期不可变。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>最近一次 SelectVictims 算出的基准（B/s）；尚未建立为 0。诊断与测试用。</summary>
    public double CurrentBaseline { get { lock (_gate) return _currentBaseline; } }

    /// <summary>逐 chunk 上报：bytes=本块写入字节，netReadMs=看门狗读耗时，diskWaitMs=写盘耗时，
    /// remainingBytes=本段剩余（含本块之后的字节）。写盘超阈值顺手触发全体免判窗口。</summary>
    public void RecordSample(int workerId, long bytes, long netReadMs, long diskWaitMs, long remainingBytes)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (!_workers.TryGetValue(workerId, out var worker))
            {
                worker = new WorkerState();
                _workers[workerId] = worker;
            }
            worker.TotalBytes += bytes;
            worker.TotalNetReadMs += netReadMs;
            worker.RemainingBytes = remainingBytes;
            worker.LastSampleMs = _clock();
            if (diskWaitMs > DiskStallThresholdMs) _diskExemptUntilMs = _clock() + DiskExemptionMs;
        }
    }

    /// <summary>显式磁盘停滞上报（如 journal FlushToDisk 耗时）：&gt;50ms → 500ms 全体免判。</summary>
    public void RecordDiskStall(long durationMs)
    {
        if (!Enabled || durationMs <= DiskStallThresholdMs) return;
        lock (_gate) _diskExemptUntilMs = _clock() + DiskExemptionMs;
    }

    /// <summary>选出应丢弃的 worker。活跃=距上次选择有新样本（退休/完成的 worker 自然出局）。</summary>
    public IReadOnlyList<int> SelectVictims(long nowMs)
    {
        if (!Enabled) return [];
        lock (_gate)
        {
            if (nowMs < _diskExemptUntilMs) return [];
            var active = new List<(int Id, WorkerState Worker, double Speed)>();
            var totalBytes = 0L;
            foreach (var (id, worker) in _workers)
            {
                totalBytes += worker.TotalBytes;
                if (worker.LastSampleMs <= _lastSelectMs) continue;
                var deltaBytes = worker.TotalBytes - worker.PrevBytes;
                var deltaNetMs = worker.TotalNetReadMs - worker.PrevNetReadMs;
                // 净读计时截断为 0 但有字节进展 → 视为极快（∞），不误 shed 瞬时完成的连接。
                // 双零记 0 仅防御零字节采样：引擎采样必带 ≥1 字节，真停滞连接无样本不活跃，由看门狗兜底
                var speed = deltaNetMs > 0 ? deltaBytes * 1000.0 / deltaNetMs
                    : deltaBytes > 0 ? double.PositiveInfinity : 0;
                if (deltaNetMs >= BaselineMinNetReadMs && deltaBytes >= BaselineMinBytes)
                    _bestSingleSpeed = Math.Max(_bestSingleSpeed, speed);
                active.Add((id, worker, speed));
            }
            _lastSelectMs = nowMs;
            if (active.Count == 0) return [];

            _linkHistory.Add((nowMs, totalBytes));
            while (_linkHistory.Count > 1 && _linkHistory[0].TimeMs < nowMs - LinkWindowMs)
                _linkHistory.RemoveAt(0);
            var oldest = _linkHistory[0];
            var span = nowMs - oldest.TimeMs;
            if (span >= LinkMinSpanMs)
            {
                var linkSpeed = (totalBytes - oldest.TotalBytes) * 1000.0 / span;
                _bestLinkSpeed = Math.Max(_bestLinkSpeed, linkSpeed);
            }

            var linkTerm = Math.Max(_bestLinkSpeed, 1) / active.Count;
            var baseline = _bestSingleSpeed > 0 ? Math.Min(_bestSingleSpeed, linkTerm) : linkTerm;
            _currentBaseline = baseline;

            var victims = new List<int>();
            foreach (var (id, worker, speed) in active)
            {
                if (worker.TotalNetReadMs < MatureNetReadMs) continue;
                if (speed * SlowFactor >= baseline) continue;
                if (speed > 0 && worker.RemainingBytes * 1000.0 / speed <= FinishWithinMs) continue;
                victims.Add(id);
            }
            foreach (var (_, worker, _) in active)
            {
                worker.PrevBytes = worker.TotalBytes;
                worker.PrevNetReadMs = worker.TotalNetReadMs;
            }
            return victims;
        }
    }

    private sealed class WorkerState
    {
        public long TotalBytes;
        public long TotalNetReadMs;
        public long RemainingBytes;
        public long LastSampleMs;
        public long PrevBytes;
        public long PrevNetReadMs;
    }
}