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
}
