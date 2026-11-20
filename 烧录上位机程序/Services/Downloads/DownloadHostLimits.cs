namespace 烧录上位机程序.Services.Downloads;

/// <summary>按主机自学习的并发连接上限（进程内共享，不持久化）。
/// 语义：只有"别的连接正在正常收数据、本连接却被拒"才算一次有效拒绝观察；
/// 累计 2 次观察后激活上限 = 历史收数峰值；之后更高的收数峰值可上调上限；
/// 条目 10 分钟无活动即遗忘，下次从零学起。429/503/连接失败是拒绝信号，
/// 403/404 等终态与中途截断/停滞不算（前者是授权问题，后者不是并发限制）。</summary>
public sealed class DownloadHostLimits
{
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(10);
    private const int ObservationsToLearn = 2;

    private readonly object _gate = new();
    private readonly Dictionary<string, HostEntry> _hosts = new(StringComparer.Ordinal);
    private readonly Func<long> _clock;

    /// <param name="clock">毫秒时钟（默认 Environment.TickCount64）；测试注入可控时钟。</param>
    public DownloadHostLimits(Func<long>? clock = null) => _clock = clock ?? (static () => Environment.TickCount64);

    /// <summary>当前已激活的上限；未学到或已遗忘返回 null（调用方不约束）。</summary>
    public int? GetCap(string url)
    {
        var key = HostKeyOf(url);
        lock (_gate)
        {
            if (!_hosts.TryGetValue(key, out var entry)) return null;
            if (_clock() - entry.LastTouchedMs >= (long)ForgetAfter.TotalMilliseconds)
            {
                _hosts.Remove(key);
                return null;
            }
            return entry.RefusalObservations >= ObservationsToLearn ? entry.Cap : null;
        }
    }

    /// <summary>某连接开始正常收数据：刷新收数峰值；已激活的上限可被更高峰值上调（只升不降）。</summary>
    public void ReportWorking(string url, int receivingCount)
    {
        if (receivingCount < 1) return;
        lock (_gate)
        {
            var entry = Touch(HostKeyOf(url));
            entry.PeakWorking = Math.Max(entry.PeakWorking, receivingCount);
            if (entry.RefusalObservations >= ObservationsToLearn)
                entry.Cap = Math.Max(entry.Cap, receivingCount);
        }
    }

    /// <summary>连接被拒（429/503/连接失败）且此刻仍有其他连接在收数据：记一次观察。
    /// 无人收数时的拒绝说明是整站故障而非并发限制，不计数。</summary>
    public void ReportRefused(string url, int receivingCount)
    {
        if (receivingCount < 1) return;
        lock (_gate)
        {
            var entry = Touch(HostKeyOf(url));
            entry.PeakWorking = Math.Max(entry.PeakWorking, receivingCount);
            entry.RefusalObservations++;
            if (entry.RefusalObservations >= ObservationsToLearn)
                entry.Cap = Math.Max(entry.Cap, entry.PeakWorking);
        }
    }

    /// <summary>主机键 = scheme 无关的 authority（host:port）小写；同主机不同路径共享上限。</summary>
    public static string HostKeyOf(string url) => new Uri(url).Authority.ToLowerInvariant();

    private HostEntry Touch(string key)
    {
        var now = _clock();
        if (!_hosts.TryGetValue(key, out var entry)
            || now - entry.LastTouchedMs >= (long)ForgetAfter.TotalMilliseconds)
        {
            entry = new HostEntry(); // 过期条目整体重置：上限与观察次数都从零学起
        }
        entry.LastTouchedMs = now;
        _hosts[key] = entry;
        return entry;
    }

    private sealed class HostEntry
    {
        public int PeakWorking;
        public int RefusalObservations;
        public int Cap;
        public long LastTouchedMs;
    }
}