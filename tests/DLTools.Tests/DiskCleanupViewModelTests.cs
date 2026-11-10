using System.IO;
using System.Windows.Threading;
using Xunit;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.Cleanup;
using 烧录上位机程序.ViewModels;

namespace DLTools.Tests;

/// <summary>清理页 VM：命令启用态、扫描聚合回填、确认拦截、真实清理链路（引擎跑在假环境的 GUID 临时根上）、全局互斥提示。</summary>
public class DiskCleanupViewModelTests
{
    private sealed class CleanupTestHarness
    {
        public TestWorkspace Workspace { get; } = new();
        public DateTimeOffset Now { get; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        public List<string> Confirmations { get; } = [];
        public Func<string, bool> Confirm { get; }
        public CleanupEngine Engine { get; }

        public CleanupTestHarness()
        {
            Confirm = message =>
            {
                Confirmations.Add(message);
                return true;
            };
            Engine = new CleanupEngine(DiskCleanupTestHelpers.CreateEnvironment(Workspace),
                new DiskCleanupTestHelpers.FixedTimeProvider(Now), recycleBin: null,
                auditDirectory: Path.Combine(Workspace.Root, "audit"));
        }

        public string SeedStaleTempFile(string name, long bytes)
        {
            var path = Path.Combine(Workspace.Root, "temp", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime - TimeSpan.FromDays(2));
            File.SetCreationTimeUtc(path, Now.UtcDateTime - TimeSpan.FromDays(2));
            return path;
        }
    }

    [Fact]
    public Task Construction_LoadsCatalogWithGroupsAndReportOnlyNotSelectable() => OnStaAsync(() =>
    {
        var harness = new CleanupTestHarness();
        var vm = NewViewModel(harness);
        Assert.NotEmpty(vm.GeneralRules);
        Assert.NotEmpty(vm.AiRules);
        Assert.Contains(vm.AiRules, r => !r.IsSelectable); // 模型/数据库仅展示
        Assert.All(vm.AiRules.Where(r => !r.IsSelectable), r => Assert.Equal("仅展示", r.DeleteModeText));
        // 默认勾选只出现在可勾选行；清理命令初始可用（目录里有默认勾选类）。
        Assert.All(vm.GeneralRules.Concat(vm.AiRules).Where(r => r.IsChecked), r => Assert.True(r.IsSelectable));
        Assert.True(vm.CleanCommand.CanExecute(null));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Scan_FillsRowSizesFromEngineOnFakeEnvironment() => OnStaAsync(async () =>
    {
        var harness = new CleanupTestHarness();
        var stale = harness.SeedStaleTempFile("stale.tmp", 4096);
        var vm = NewViewModel(harness);

        await vm.ScanCommand.ExecuteAsync(null);

        var userTemp = vm.GeneralRules.Single(r => r.Id == "user-temp");
        Assert.True(userTemp.Scanned);
        Assert.Equal(4096, userTemp.TotalBytes);
        Assert.Equal("4 KB", userTemp.SizeText);
        Assert.Contains("扫描完成", vm.Status);
        Assert.True(File.Exists(stale)); // 扫描只读
        Assert.Equal(100, vm.ProgressPercent);
    });

    [Fact]
    public Task SelectionChanges_UpdateSummaryAndCleanAvailability() => OnStaAsync(() =>
    {
        var harness = new CleanupTestHarness();
        var vm = NewViewModel(harness);
        var userTemp = vm.GeneralRules.Single(r => r.Id == "user-temp");

        userTemp.IsChecked = false;
        foreach (var row in vm.GeneralRules.Concat(vm.AiRules).Where(r => r.IsSelectable))
            if (ReferenceEquals(row, userTemp) is false && row.IsChecked) row.IsChecked = false;
        Assert.Equal("尚未勾选任何类别", vm.SelectedSummaryText);
        Assert.False(vm.CleanCommand.CanExecute(null));

        userTemp.IsChecked = true;
        Assert.Contains("已勾选 1 类", vm.SelectedSummaryText);
        Assert.True(vm.CleanCommand.CanExecute(null));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Clean_ConfirmRejected_DoesNothing() => OnStaAsync(async () =>
    {
        var harness = new CleanupTestHarness();
        var stale = harness.SeedStaleTempFile("keep.tmp", 2048);
        var confirmations = new List<string>();
        var vm = NewViewModel(harness, confirm =>
        {
            confirmations.Add(confirm);
            return false;
        });

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Single(confirmations);
        Assert.Contains("已取消：未执行任何删除", vm.Status);
        Assert.True(File.Exists(stale));
    });

    [Fact]
    public Task Clean_Confirmed_DeletesSelectedAndRecordsAuditPath() => OnStaAsync(async () =>
    {
        var harness = new CleanupTestHarness();
        var stale = harness.SeedStaleTempFile("junk.tmp", 8192);
        DiskCleanupTestHelpers.WriteFile(Path.Combine(harness.Workspace.Root, "temp", "fresh.tmp"), 8);
        // 确认回调必须注入：默认实现是 MessageBox，测试线程会永久阻塞。
        var vm = NewViewModel(harness, _ => true);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Contains("清理完成", vm.Status);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(Path.Combine(harness.Workspace.Root, "temp", "fresh.tmp"))); // 新鲜文件保留
        Assert.True(vm.LastAuditPath.Length > 0);
        Assert.True(File.Exists(vm.LastAuditPath));
        Assert.True(vm.CopyAuditPathCommand.CanExecute(null));
    });

    [Fact]
    public Task Scan_WhenHostBusy_ReportsConflictWithoutClearingRows() => OnStaAsync(async () =>
    {
        var harness = new CleanupTestHarness();
        var occupied = new BackgroundTaskHost();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        occupied.TryStart(BackgroundOperationKind.Flashing,
            async token => await gate.Task.WaitAsync(token), out var blocker);
        var vm = new CleanupViewModel(harness.Engine, occupied, settingsStore: NewSettingsStore(harness));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains("另一个后台任务正在进行", vm.Status);
        Assert.False(vm.IsBusy);
        gate.SetResult();
        await blocker;
    });

    [Fact]
    public Task FormatSize_RendersHumanUnits() => OnStaAsync(() =>
    {
        Assert.Equal("0 B", CleanupViewModel.FormatSize(0));
        Assert.Equal("512 B", CleanupViewModel.FormatSize(512));
        Assert.Equal("1.5 KB", CleanupViewModel.FormatSize(1536));
        Assert.Equal("2 MB", CleanupViewModel.FormatSize(2 * 1024 * 1024));
        Assert.Equal("1.5 GB", CleanupViewModel.FormatSize(3L * 1024 * 1024 * 1024 / 2));
        return Task.CompletedTask;
    });

    private static CleanupViewModel NewViewModel(CleanupTestHarness harness, Func<string, bool>? confirm = null) =>
        new(harness.Engine, new BackgroundTaskHost(), settingsStore: NewSettingsStore(harness), confirm: confirm);

    private static CleanupSettingsStore NewSettingsStore(CleanupTestHarness harness) =>
        new(Path.Combine(harness.Workspace.Root, "settings.json"));

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
        return completion.Task;
    }
}
