using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.JLink;
using 烧录上位机程序.ViewModels;

namespace DLTools.Tests;

/// <summary>
/// FlashViewModel 测试：输入校验、FlashParams 构造、地址解析与按钮/阶段状态机。
/// 状态机用例跑在 STA 泵线程（与仓库其他 VM 测试一致），烧录经注入假会话完成。
/// </summary>
public class FlashViewModelTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string CreateFile(string name)
    {
        var path = Path.Combine(_workspace.Root, name);
        File.WriteAllText(path, ":00000001FF\n");
        return path;
    }

    #region 地址解析

    [Theory]
    [InlineData("0x08000000", true, 0x08000000)]
    [InlineData("0X10", true, 16)]          // 大写前缀
    [InlineData(" 0x1F ", true, 31)]        // 容忍首尾空白
    [InlineData("123", true, 123)]          // 十进制
    [InlineData("", false, 0)]
    [InlineData("   ", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("0x", false, 0)]
    [InlineData("0xZZ", false, 0)]
    [InlineData("9999999999", false, 0)]    // 超 int 范围
    public void TryParseAddress_AcceptsHexPrefixAndDecimal(string text, bool expected, int value)
    {
        Assert.Equal(expected, FlashViewModel.TryParseAddress(text, out var parsed));
        Assert.Equal(value, parsed);
    }

    #endregion

    #region 输入校验

    private FlashViewModel CreateViewModel() => new(new FlashWorker(new BackgroundTaskHost(), () => new FakeJLinkSession()));

    [Fact]
    public void TryBuildParams_RejectsBlankDeviceName()
    {
        var vm = CreateViewModel();
        vm.DeviceName = "  ";
        vm.FilePath = CreateFile("fw.hex");

        Assert.False(vm.TryBuildParams(out var parameters, out var error, out var field));
        Assert.Null(parameters);
        Assert.Equal(FlashInputField.DeviceName, field);
        Assert.Contains("目标设备名", error);
    }

    [Fact]
    public void TryBuildParams_RejectsMissingFile()
    {
        var vm = CreateViewModel();
        vm.FilePath = Path.Combine(_workspace.Root, "missing.hex");

        Assert.False(vm.TryBuildParams(out _, out var error, out var field));
        Assert.Equal(FlashInputField.FilePath, field);
        Assert.Contains(".axf/.elf/.hex/.bin", error);
    }

    [Fact]
    public void TryBuildParams_RejectsInvalidBinAddress()
    {
        var vm = CreateViewModel();
        vm.FilePath = CreateFile("fw.bin");
        vm.BinAddrText = "not-an-address";

        Assert.False(vm.TryBuildParams(out _, out var error, out var field));
        Assert.Equal(FlashInputField.BinAddr, field);
        Assert.Contains("起始地址", error);
    }

    [Fact]
    public void TryBuildParams_BuildsParamsFromAllInputs()
    {
        var vm = CreateViewModel();
        vm.DeviceName = "  STM32H750VB  ";
        vm.FilePath = CreateFile("FW.BIN");   // 大写后缀走 Invariant 路径
        vm.BinAddrText = "0x08000000";
        vm.SpeedText = "2000";
        vm.Serial = " 123456 ";
        vm.InterfaceIndex = 1;                // JTAG
        vm.IsChipErase = true;

        Assert.True(vm.TryBuildParams(out var parameters, out var error, out var field));
        Assert.Equal("", error);
        Assert.Equal(FlashInputField.None, field);
        var p = Assert.IsType<FlashParams>(parameters);
        Assert.Equal("STM32H750VB", p.DeviceName);
        Assert.Equal("bin", p.FileFormat);
        Assert.Equal(0x08000000, p.BinStartAddr);
        Assert.Equal("JTAG", p.Interface);
        Assert.Equal(2000, p.SpeedKhz);
        Assert.Equal("123456", p.Serial);
        Assert.Equal(JLinkBackend.EraseModeChip, p.EraseMode);
        Assert.Equal(JLinkBackend.PostActionResetRun, p.PostAction); // 默认复位运行
    }

    [Fact]
    public void TryBuildParams_FallsBackToDefaultSpeedAndSkipsBinAddressForHex()
    {
        var vm = CreateViewModel();
        vm.FilePath = CreateFile("fw.hex");
        vm.SpeedText = "不是数字";
        vm.BinAddrText = "垃圾值也不该拦截 hex 文件";

        Assert.True(vm.TryBuildParams(out var parameters, out _, out _));
        Assert.Equal(4000, parameters!.SpeedKhz); // 非法速度回退默认
        Assert.Equal(0, parameters.BinStartAddr); // hex 自带地址，BinAddr 不参与校验
    }

    [Fact]
    public void PostActionRadios_AreMutuallyExclusive()
    {
        var vm = CreateViewModel();
        Assert.True(vm.IsPostResetRun);

        vm.IsPostHalt = true;
        Assert.False(vm.IsPostResetRun);
        Assert.False(vm.IsPostNone);

        vm.IsPostNone = true;
        Assert.False(vm.IsPostHalt);
        Assert.True(vm.IsPostNone);

        vm.FilePath = CreateFile("fw.hex");
        Assert.True(vm.TryBuildParams(out var parameters, out _, out _));
        Assert.Equal(JLinkBackend.PostActionNone, parameters!.PostAction);
    }

    #endregion

    #region 按钮与阶段状态机

    [Fact]
    public async Task StartFlash_WhileHostBusy_ReturnsFalseAndKeepsButtons()
    {
        var host = new BackgroundTaskHost();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(host.TryStart(BackgroundOperationKind.Packaging, async token =>
        {
            gate.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, out var blocker));
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var vm = new FlashViewModel(new FlashWorker(host, () => new FakeJLinkSession()));
        vm.DeviceName = "STM32";
        vm.FilePath = CreateFile("fw.hex");
        Assert.True(vm.TryBuildParams(out var parameters, out _, out _));

        Assert.False(vm.StartFlash(parameters!)); // 被拒
        Assert.True(vm.CanStart);                 // 按钮不得翻转（否则永久卡死）
        Assert.False(vm.CanStop);

        host.RequestCancellation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocker.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public Task SuccessfulFlash_FlipsButtonStateStageVisualAndLog() => OnStaAsync(async () =>
    {
        var worker = new FlashWorker(new BackgroundTaskHost(), () => new FakeJLinkSession());
        var vm = new FlashViewModel(worker);
        vm.DeviceName = "STM32F103";
        vm.FilePath = CreateFile("fw.hex");
        Assert.True(vm.TryBuildParams(out var parameters, out _, out _));

        Assert.True(vm.StartFlash(parameters!));
        Assert.False(vm.CanStart);
        Assert.True(vm.CanStop);

        await worker.CurrentTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await YieldToDispatcherAsync();

        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.Equal(FlashStageVisual.Success, vm.StageVisual);
        Assert.Equal("完成", vm.StageText);
        Assert.Equal(100, vm.ProgressValue);
        Assert.Contains("烧录成功", vm.LogText);
        Assert.Contains("校验通过", vm.LogText); // 必选校验阶段在日志中可见
    });

    [Fact]
    public Task FailedFlash_MarksFailureVisualAndRestoresButtons() => OnStaAsync(async () =>
    {
        var session = new FakeJLinkSession();
        session.ConnectHook = _ => throw new InvalidOperationException("no probe");
        var worker = new FlashWorker(new BackgroundTaskHost(), () => session);
        var vm = new FlashViewModel(worker);
        vm.DeviceName = "STM32F103";
        vm.FilePath = CreateFile("fw.hex");
        Assert.True(vm.TryBuildParams(out var parameters, out _, out _));

        Assert.True(vm.StartFlash(parameters!));
        await worker.CurrentTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await YieldToDispatcherAsync();

        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.Equal(FlashStageVisual.Failure, vm.StageVisual);
        Assert.Equal("失败", vm.StageText);
        Assert.Contains("no probe", vm.LogText);
    });

    [Fact]
    public Task StopFlash_DisablesStopImmediatelyAndRestartsAfterCancel() => OnStaAsync(async () =>
    {
        var session = new FakeJLinkSession();
        var enteredProgram = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProgramHook = ct =>
        {
            enteredProgram.TrySetResult();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            ct.ThrowIfCancellationRequested();
        };
        var worker = new FlashWorker(new BackgroundTaskHost(), () => session);
        var vm = new FlashViewModel(worker);
        vm.DeviceName = "STM32F103";
        vm.FilePath = CreateFile("fw.hex");
        Assert.True(vm.TryBuildParams(out var parameters, out _, out _));
        Assert.True(vm.StartFlash(parameters!));

        await enteredProgram.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.StopFlash();
        Assert.False(vm.CanStop); // 停止立即防重复点击

        await worker.CurrentTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await YieldToDispatcherAsync();
        Assert.True(vm.CanStart); // 取消终态后恢复开始按钮
        Assert.False(vm.CanStop);
        // 取消的权威信号：完成摘要为"已取消"（终态 stage 会被随后的"失败"覆盖，不作为断言点）
        Assert.Contains("已取消", vm.LogText);
    });

    private static Task YieldToDispatcherAsync() =>
        Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
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

    #endregion
}
