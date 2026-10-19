using System.Net;
using System.Windows.Threading;
using Xunit;
using 烧录上位机程序.Services;
using 烧录上位机程序.ViewModels;

namespace DLTools.Tests;

public class IpScanViewModelTests
{
    [Fact]
    public Task InputValidation_UpdatesHintAndScanAvailabilityImmediately() => OnStaAsync(() =>
    {
        var vm = new IpScanViewModel(host: new());
        Assert.False(vm.ScanCommand.CanExecute(null));
        vm.InputIp = "192.168.1.99";
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.Contains("192.168.1.1", vm.InputHint);
        vm.InputIp = "192.168.1.999";
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.Contains("IPv4", vm.InputHint);
        vm.InputIp = "::1";
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.Contains("/24", vm.InputHint);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Scan_IncrementallyPublishesReachableDevicesOnUiThreadAndOwnsHostUntilDrained() => OnStaAsync(async () =>
    {
        var host = new BackgroundTaskHost();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubService(async (_, report, token) =>
        {
            await report(new(1, IPAddress.Parse("192.168.1.1"), new(IpProbeStatus.TimedOut)));
            await report(new(2, IPAddress.Parse("192.168.1.2"), new(IpProbeStatus.Reachable, 3)));
            reported.SetResult();
            await release.Task.WaitAsync(token);
            await report(new(254, IPAddress.Parse("192.168.1.254"), new(IpProbeStatus.Unreachable)));
        });
        var vm = new IpScanViewModel(service, host) { InputIp = "192.168.1.99" };
        var uiThread = Environment.CurrentManagedThreadId;
        vm.Devices.CollectionChanged += (_, _) => Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
        var scan = vm.ScanCommand.ExecuteAsync(null);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("192.168.1.2", Assert.Single(vm.Devices).Ip);
        Assert.Equal(3, vm.Devices[0].RoundtripTimeMs);
        Assert.Equal("可达", vm.Devices[0].Status);
        Assert.Equal("2/254", vm.ProgressText);
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsIdle);
        Assert.Equal(BackgroundOperationKind.IpScanning, host.CurrentKind);
        Assert.False(host.TryStart(BackgroundOperationKind.Flashing, _ => Task.CompletedTask, out _));
        release.SetResult();
        await scan;
        Assert.False(host.IsBusy);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.Equal("254/254", vm.ProgressText);
        Assert.Contains("扫描完成", vm.Status);
    });

    [Fact]
    public Task Cancel_KeepsPartialResultsWaitsForDrainAndAllowsCleanRestart() => OnStaAsync(async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = 0;
        var service = new StubService(async (_, report, token) =>
        {
            if (Interlocked.Increment(ref run) == 2)
            {
                await report(new(254, IPAddress.Parse("192.168.1.9"), new(IpProbeStatus.Reachable, 9)));
                return;
            }
            await report(new(1, IPAddress.Parse("192.168.1.1"), new(IpProbeStatus.Reachable, 1)));
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                await drain.Task;
                throw;
            }
        });
        var host = new BackgroundTaskHost();
        var vm = new IpScanViewModel(service, host) { InputIp = "192.168.1.99" };
        var scan = vm.ScanCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.IsBusy);
        Assert.True(vm.IsBusy);
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.Single(vm.Devices);
        drain.SetResult();
        await scan;
        Assert.False(host.IsBusy);
        Assert.Contains("已取消", vm.Status);
        Assert.Single(vm.Devices);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal("192.168.1.9", Assert.Single(vm.Devices).Ip);
        Assert.Equal("254/254", vm.ProgressText);
    });

    [Fact]
    public Task GlobalCancellation_UsedByWindowCloseReachesScanAndReleasesHost() => OnStaAsync(async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new BackgroundTaskHost();
        var vm = new IpScanViewModel(new StubService(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }), host) { InputIp = "192.168.1.99" };
        var scan = vm.ScanCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.RequestCancellation();
        await scan;
        Assert.False(host.IsBusy);
        Assert.False(vm.IsBusy);
        Assert.Contains("已取消", vm.Status);
    });

    [Fact]
    public Task AnotherTaskBusy_PreservesResultsAndCannotBeCancelledFromScanPage() => OnStaAsync(async () =>
    {
        var host = new BackgroundTaskHost();
        var called = false;
        var vm = new IpScanViewModel(new StubService((_, _, _) => { called = true; return Task.CompletedTask; }), host)
        { InputIp = "192.168.1.99" };
        vm.Devices.Add(new("192.168.1.3", 1));
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wasCancelled = false;
        Assert.True(host.TryStart(BackgroundOperationKind.Flashing, async token =>
        {
            using var registration = token.Register(() => wasCancelled = true);
            entered.SetResult();
            await finish.Task;
        }, out var flash));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.ScanCommand.ExecuteAsync(null);
            Assert.False(called);
            Assert.Single(vm.Devices);
            Assert.Contains("另一个后台任务", vm.Status);
            Assert.False(vm.CancelCommand.CanExecute(null));
            vm.CancelCommand.Execute(null);
            Assert.False(wasCancelled);
            Assert.True(host.IsBusy);
        }
        finally { finish.SetResult(); await flash; }
    });

    [Fact]
    public Task NoDevices_ExplainsIcmpLimitation() => OnStaAsync(async () =>
    {
        var vm = new IpScanViewModel(new StubService(async (_, report, _) =>
        {
            await report(new(254, IPAddress.Parse("192.168.1.254"), new(IpProbeStatus.TimedOut)));
        }), new()) { InputIp = "192.168.1.99" };
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Empty(vm.Devices);
        Assert.Equal("未发现设备（部分设备会禁用 ICMP 回复）", vm.Status);
    });

    [Fact]
    public Task NetworkError_ShowsReadableMessageAndRestoresCommands() => OnStaAsync(async () =>
    {
        var vm = new IpScanViewModel(new StubService((_, _, _) => throw new InvalidOperationException("未找到在线的 IPv4 网卡")), new())
        { InputIp = "192.168.1.99" };
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Contains("扫描失败", vm.Status);
        Assert.Contains("网卡", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.True(vm.ScanCommand.CanExecute(null));
    });

    [Fact]
    public Task CancelBeforeQueuedUiCallback_DoesNotAppendLateDevice() => OnStaAsync(async () =>
    {
        using var queued = new ManualResetEventSlim();
        var vm = new IpScanViewModel(new StubService(async (_, report, token) =>
        {
            var callback = report(new(1, IPAddress.Parse("192.168.1.1"), new(IpProbeStatus.Reachable, 1)));
            queued.Set();
            await callback;
            token.ThrowIfCancellationRequested();
        }), new()) { InputIp = "192.168.1.99" };
        var scan = vm.ScanCommand.ExecuteAsync(null);
        // Intentionally hold the UI dispatcher until the worker has queued its callback.
        Assert.True(queued.Wait(TimeSpan.FromSeconds(5)));
        vm.CancelCommand.Execute(null);
        await scan;
        Assert.Empty(vm.Devices);
        Assert.Equal(0, vm.Completed);
        Assert.Contains("已取消", vm.Status);
    });

    private sealed class StubService(Func<string, Func<IpScanProgress, Task>, CancellationToken, Task> scan) : IIpScanService
    {
        public Task ScanAsync(string input, Func<IpScanProgress, Task> reportAsync, CancellationToken cancellationToken) =>
            scan(input, reportAsync, cancellationToken);
    }

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
