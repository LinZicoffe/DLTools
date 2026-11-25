using System.Windows.Threading;
using Xunit;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.IpPurity;
using 烧录上位机程序.ViewModels;

namespace DLTools.Tests;

public class IpPurityViewModelTests
{
    [Fact]
    public Task Start_RendersTheSnapshotAndCancelReturnsToTheIdleState() => OnStaAsync(async () =>
    {
        var host = new BackgroundTaskHost();
        var runner = new ScriptRunner();
        var vm = new IpPurityViewModel(runner, host) { SlowAfter = TimeSpan.FromMilliseconds(40) };
        Assert.True(vm.ShowNotes);
        Assert.Equal("—", vm.RingPrimary);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("203.0.113.7", vm.IpText);
        Assert.Equal("100", vm.RingPrimary);
        Assert.False(vm.IsBusy);
        Assert.False(host.IsBusy);

        runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = vm.StartCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => vm.IsBusy, TimeSpan.FromSeconds(2)));
        vm.CancelCommand.Execute(null);
        runner.Gate.TrySetResult();
        await starting;
        Assert.False(vm.IsBusy);
        Assert.Contains("已取消", vm.HeaderStatus, StringComparison.Ordinal);
        Assert.False(vm.ShowIdentity);
        Assert.Equal("", vm.SlowText);

        runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.FinishedReporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = vm.StartCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => vm.IsBusy, TimeSpan.FromSeconds(2)));
        vm.AbandonPendingResults();
        runner.Gate.TrySetResult();
        await runner.FinishedReporting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        host.RequestCancellation();
        await closing;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(vm.IsBusy);
        Assert.Equal("—", vm.RingPrimary);
        Assert.False(vm.ShowDetails);
        Assert.StartsWith("正在获取出口 IP", vm.HeaderStatus, StringComparison.Ordinal);
        Assert.Equal("", vm.IpText);
        Assert.Equal("", vm.SlowText);
    });

    [Fact]
    public Task Start_RefusesWhenAnotherGlobalTaskIsRunning() => OnStaAsync(async () =>
    {
        var host = new BackgroundTaskHost();
        Assert.True(host.TryStart(BackgroundOperationKind.IpScanning, token => Task.Delay(Timeout.Infinite, token), out _));
        var vm = new IpPurityViewModel(new ScriptRunner(), host);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Contains("另一个后台任务", vm.HeaderStatus, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    });

    private sealed class ScriptRunner : IIpPurityRunner
    {
        public TaskCompletionSource Gate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishedReporting { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScriptRunner() => Gate.SetResult();

        public async Task RunAsync(IProgress<IpPuritySnapshot> progress, CancellationToken cancellationToken)
        {
            var finished = FinishedReporting;
            progress.Report(new IpPuritySnapshot(
                IpPurityPhase.Running, 0, "正在获取出口 IP", null, false, null, false, [], [], false, null, false, null, false, null, false, TimeSpan.Zero));
            await Gate.Task.WaitAsync(cancellationToken);
            var intel = new IpPurityIntel("203.0.113.7", "IPinfo", "high", "AS17676", "SoftBank", "SoftBank", null, null, null,
                "isp", new IpPurityFlags(false, false, false, false, false, false), new IpPurityGeo("JP", "日本", "东京", "东京"), []);
            var score = IpPurityScoring.ComputeV2(new IpPurityScoreRequest(
                "203.0.113.7", IpPurityParse.ToScoreIntel(intel),
                [new("ChatGPT", "ok", true), new("Claude", "ok", true)],
                new IpPurityScoreConsistency(false, 1, 0),
                new IpPurityScoreLeak("clean", [], true, true)));
            progress.Report(new IpPuritySnapshot(
                IpPurityPhase.Done, 6, "检测完成",
                new IpPurityExit("203.0.113.7", "Cloudflare", 20, "JP", null, null, "SoftBank", "AS17676", null, null, []),
                true, intel, true, [], [], true, null, true,
                new IpPurityExitSummary([], "203.0.113.7", null, 1, 0, false),
                true, score, false, TimeSpan.FromSeconds(1)));
            finished.TrySetResult();
        }
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
