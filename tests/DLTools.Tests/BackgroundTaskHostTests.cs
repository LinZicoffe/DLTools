using Xunit;
using 烧录上位机程序.Services;

namespace DLTools.Tests;

public class BackgroundTaskHostTests
{
    [Fact]
    public async Task CompletionNoticeBeforeTaskReturn_DoesNotReleaseOwnership()
    {
        var host = new BackgroundTaskHost();
        var notice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(host.TryStart(BackgroundOperationKind.Flashing, async _ =>
        {
            notice.SetResult();
            await finish.Task;
        }, out var first));
        await notice.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.TryStart(BackgroundOperationKind.Packaging, _ => Task.CompletedTask, out _));
        finish.SetResult();
        await first;
        Assert.True(host.TryStart(BackgroundOperationKind.Packaging, _ => Task.CompletedTask, out var second));
        await second;
    }

    [Fact]
    public async Task ConcurrentCancellationAndReplacement_DoesNotDisposeAnActiveCancellationCall()
    {
        var host = new BackgroundTaskHost();
        for (var i = 0; i < 30; i++)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(host.TryStart(BackgroundOperationKind.Packaging, async token =>
            {
                using var registration = token.Register(() => { _ = host.IsBusy; });
                ready.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { }
            }, out var task));
            await ready.Task;
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(host.RequestCancellation)));
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FaultedOperation_ReleasesOwnershipForNextStart()
    {
        var host = new BackgroundTaskHost();
        Assert.True(host.TryStart(BackgroundOperationKind.Flashing,
            _ => Task.FromException(new InvalidOperationException("boom")), out var first));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        // 异常终态同样归还所有权：宿主可立即承接下一类任务
        Assert.False(host.IsBusy);
        Assert.Null(host.CurrentKind);
        Assert.True(host.TryStart(BackgroundOperationKind.Packaging, _ => Task.CompletedTask, out var second));
        await second;
    }

    [Fact]
    public async Task RequestCancellation_DeliversTokenToRunningOperation()
    {
        var host = new BackgroundTaskHost();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(host.TryStart(BackgroundOperationKind.DiskCleanup, async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
        }, out var task));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        host.RequestCancellation();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CurrentKind_ReportsWhileRunningAndClearsAfterCompletion()
    {
        var host = new BackgroundTaskHost();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(host.TryStart(BackgroundOperationKind.IpScanning, async _ =>
        {
            started.SetResult();
            await release.Task;
        }, out var task));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BackgroundOperationKind.IpScanning, host.CurrentKind);

        release.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(host.CurrentKind);
    }
}
