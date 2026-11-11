using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using 烧录上位机程序.Models;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.JLink;

namespace DLTools.Tests;

/// <summary>
/// FlashWorker 编排逻辑离线测试：注入独立 BackgroundTaskHost 与内存假会话，
/// 验证调用序列、事件流、失败/取消路径与按钮语义；不加载 JLink_x64.dll。
/// </summary>
public class FlashWorkerTests
{
    private static FlashParams ValidParams() => new()
    {
        DeviceName = "STM32H750VB",
        Interface = "SWD",
        SpeedKhz = 4000,
        FilePath = "firmware.hex",
        FileFormat = "hex",
        EraseMode = JLinkBackend.EraseModeSector,
        PostAction = JLinkBackend.PostActionResetRun,
    };

    private static async Task<(bool Success, string Summary)> RunToCompletionAsync(FlashWorker worker, FlashParams parameters)
    {
        var finished = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.FlashFinished += (success, summary) => finished.TrySetResult((success, summary));
        Assert.True(worker.StartFlash(parameters));
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.CurrentTask!.WaitAsync(TimeSpan.FromSeconds(10));
        return result;
    }

    [Fact]
    public async Task SuccessfulFlash_RunsFullSequenceWithStagesAndProgress()
    {
        var host = new BackgroundTaskHost();
        var session = new FakeJLinkSession();
        var stages = new ConcurrentQueue<string>();
        var worker = new FlashWorker(host, () => session);
        worker.FlashStageChanged += stages.Enqueue;
        var progressTotal = 0;
        worker.FlashProgress += (_, total) => progressTotal = total;

        var (success, summary) = await RunToCompletionAsync(worker, ValidParams());

        Assert.True(success);
        Assert.Equal("烧录成功", summary);
        Assert.Equal(new[] { "connect:STM32H750VB:SWD", "program:hex", "verify", "reset:False:True", "close" }, session.Calls);
        // 阶段语义代码完整有序（扇区擦除不产生 erase 阶段）
        Assert.Equal(new[] { "connect", "program", "verify", "reset", "disconnect" }, stages);
        Assert.Equal(100, progressTotal);
        Assert.False(worker.IsBusy);
        Assert.False(host.IsBusy); // 注入的 host（而非 Shared）：任务终态后所有权归还
    }

    [Fact]
    public async Task ChipErase_RunsEraseBetweenConnectAndProgram()
    {
        var session = new FakeJLinkSession();
        var worker = new FlashWorker(new BackgroundTaskHost(), () => session);
        var parameters = ValidParams();
        parameters.EraseMode = JLinkBackend.EraseModeChip;

        var (success, _) = await RunToCompletionAsync(worker, parameters);

        Assert.True(success);
        Assert.Equal(new[] { "connect:STM32H750VB:SWD", "erase:chip", "program:hex", "verify", "reset:False:True", "close" }, session.Calls);
    }

    [Fact]
    public async Task SessionFailure_ClosesSessionAndReportsFailure()
    {
        var session = new FakeJLinkSession();
        session.ConnectHook = _ => throw new InvalidOperationException("probe not found");
        var worker = new FlashWorker(new BackgroundTaskHost(), () => session);

        var (success, summary) = await RunToCompletionAsync(worker, ValidParams());

        Assert.False(success);
        Assert.Equal("probe not found", summary);
        Assert.Contains("close", session.Calls); // 失败路径也必须关闭会话
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task StopFlash_CancelsAtProgramBoundaryAndReportsCancelled()
    {
        var session = new FakeJLinkSession();
        var enteredProgram = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProgramHook = ct =>
        {
            enteredProgram.TrySetResult();
            // 模拟真实 Program 的段边界取消：阻塞等待取消生效后抛 OCE
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            ct.ThrowIfCancellationRequested();
        };
        var worker = new FlashWorker(new BackgroundTaskHost(), () => session);
        var stages = new ConcurrentQueue<string>();
        worker.FlashStageChanged += stages.Enqueue;
        var finished = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.FlashFinished += (success, summary) => finished.TrySetResult((success, summary));

        Assert.True(worker.StartFlash(ValidParams()));
        await enteredProgram.Task.WaitAsync(TimeSpan.FromSeconds(10));
        worker.StopFlash();
        var (success, summary) = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.CurrentTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(success);
        Assert.Equal("已取消", summary);
        Assert.Contains("close", session.Calls);
        Assert.Contains("cancel", stages); // 停止请求必须立即反馈"正在取消"
    }

    [Fact]
    public async Task FinishedHandlerThrows_DoesNotFaultHostTask()
    {
        var worker = new FlashWorker(new BackgroundTaskHost(), () => new FakeJLinkSession());
        worker.FlashFinished += (_, _) => throw new InvalidOperationException("UI handler bug");

        Assert.True(worker.StartFlash(ValidParams()));
        // 事件订阅方异常不得把宿主任务变成 faulted（否则所有权挂到进程结束）
        await worker.CurrentTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task StartFlash_RejectsWhileHostBusyAndResetsIsBusy()
    {
        var host = new BackgroundTaskHost();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(host.TryStart(BackgroundOperationKind.Packaging, async token =>
        {
            gate.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, out var blocker));
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var worker = new FlashWorker(host, () => new FakeJLinkSession());
        Assert.False(worker.StartFlash(ValidParams())); // 宿主被其他任务占用
        Assert.False(worker.IsBusy);                    // 拒绝路径必须复位 IsBusy（否则按钮永久卡死）

        host.RequestCancellation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocker.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
