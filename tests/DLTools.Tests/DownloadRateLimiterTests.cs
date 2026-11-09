using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class DownloadRateLimiterTests
{
    private sealed class FakeClock
    {
        public long Milliseconds;
        public readonly List<int> Waits = [];
        public long Now() => Volatile.Read(ref Milliseconds);
        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            lock (Waits) Waits.Add(milliseconds);
            Milliseconds += milliseconds; // 假延时立即“流逝”对应时间
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task UnlimitedLimiter_GrantsRequestedImmediately()
    {
        var limiter = new DownloadRateLimiter(0);
        Assert.True(limiter.IsUnlimited);
        var clock = new FakeClock();
        var granted = await limiter.AcquireAsync(64 * 1024, CancellationToken.None);
        Assert.Equal(64 * 1024, granted);
        _ = clock; // 无限速路径完全不读时钟
    }

    [Fact]
    public async Task FirstRequest_ConsumesBurstThenWaitsForRefill()
    {
        var clock = new FakeClock();
        var limiter = new DownloadRateLimiter(1000, clock.Now, clock.DelayAsync); // 容量钳到 64 KiB
        // 初始令牌 = 64 KiB：一次拿满。
        Assert.Equal(64 * 1024, await limiter.AcquireAsync(64 * 1024, CancellationToken.None));
        // 令牌耗尽：需等待 ceil(1000/1000)=1ms 补 1 字节。
        Assert.Equal(1, await limiter.AcquireAsync(1, CancellationToken.None));
        lock (clock.Waits) Assert.Equal([1], clock.Waits);
    }

    [Fact]
    public async Task RequestedAboveCapacity_IsGrantedPartiallyInBurstSizedChunks()
    {
        var clock = new FakeClock();
        var limiter = new DownloadRateLimiter(1000, clock.Now, clock.DelayAsync);
        var granted = await limiter.AcquireAsync(200_000, CancellationToken.None);
        Assert.Equal(64 * 1024, granted); // 授予不超过容量
    }

    [Fact]
    public async Task AfterIdlePeriod_TokensRefillUpToCapacityOnly()
    {
        var clock = new FakeClock();
        var limiter = new DownloadRateLimiter(2000, clock.Now, clock.DelayAsync);
        Assert.Equal(64 * 1024, await limiter.AcquireAsync(64 * 1024, CancellationToken.None));
        clock.Milliseconds += 60_000; // 空闲 60 秒（补满 64 KiB 需 ≈33 秒）：回满但不超容量
        Assert.Equal(64 * 1024, await limiter.AcquireAsync(300_000, CancellationToken.None));
        // 回满后再次请求必须等待（证明没有超容量囤积）。
        Assert.Equal(1, await limiter.AcquireAsync(1, CancellationToken.None));
        lock (clock.Waits) Assert.Equal([1], clock.Waits); // 2000 B/s → ceil(1000/2000)=1ms
    }

    [Fact]
    public async Task Cancellation_WhileWaitingForTokens_Propagates()
    {
        var clock = new FakeClock();
        var limiter = new DownloadRateLimiter(1000, clock.Now, clock.DelayAsync);
        await limiter.AcquireAsync(64 * 1024, CancellationToken.None); // 先耗尽突发
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.AcquireAsync(1, source.Token));
    }
}
