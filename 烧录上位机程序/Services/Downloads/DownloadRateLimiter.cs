using System.Diagnostics;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>令牌桶限速器。容量 = 1 秒配额（下限 64 KiB，避免小限速下微延迟抖动）；
/// rate ≤ 0 表示不限速。时钟与延时均可注入，供确定性单测。</summary>
public sealed class DownloadRateLimiter
{
    private const long MinBurstBytes = 64 * 1024;

    private readonly long _bytesPerSecond;
    private readonly long _capacity;
    private readonly Func<long> _monotonicMilliseconds;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private readonly object _gate = new();
    private double _tokens;
    private long _lastRefillMilliseconds;

    public DownloadRateLimiter(long bytesPerSecond,
        Func<long>? monotonicMilliseconds = null,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        _bytesPerSecond = bytesPerSecond;
        _capacity = Math.Max(MinBurstBytes, bytesPerSecond);
        _monotonicMilliseconds = monotonicMilliseconds ?? DefaultClock;
        _delayAsync = delayAsync ?? DefaultDelay;
        _tokens = _capacity;
        _lastRefillMilliseconds = _monotonicMilliseconds();
    }

    public long BytesPerSecond => _bytesPerSecond;
    public bool IsUnlimited => _bytesPerSecond <= 0;

    private static long DefaultClock() => Environment.TickCount64;

    private static Task DefaultDelay(int milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(milliseconds, cancellationToken);

    /// <summary>申请至多 <paramref name="requested"/> 字节的发送/接收配额，返回实际授予值（1..requested）。
    /// 不限速时直接返回 requested；令牌不足时等待补足到至少 1 字节再授予。</summary>
    public async Task<int> AcquireAsync(int requested, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requested, 1);
        if (IsUnlimited) return requested;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int granted;
            lock (_gate)
            {
                var now = _monotonicMilliseconds();
                var elapsed = now - _lastRefillMilliseconds;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(_capacity, _tokens + elapsed * _bytesPerSecond / 1000.0);
                    _lastRefillMilliseconds = now;
                }
                var take = Math.Min(requested, (long)Math.Floor(_tokens));
                if (take < 1)
                {
                    // 补满 1 字节所需毫秒；上限 1000ms 防时钟异常死等。
                    var milliseconds = (int)Math.Min(1000, Math.Ceiling(1000.0 / _bytesPerSecond));
                    granted = -milliseconds; // 负值 = 需要等待
                }
                else
                {
                    _tokens -= take;
                    granted = (int)Math.Min(take, int.MaxValue);
                }
            }
            if (granted > 0) return granted;
            await _delayAsync(-granted, cancellationToken).ConfigureAwait(false);
        }
    }
}
