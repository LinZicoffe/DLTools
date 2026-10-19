using System.Windows.Threading;
using Xunit;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.Authorization;
using 烧录上位机程序.ViewModels;

namespace DLTools.Tests;

public class OfflineAuthorizationViewModelTests
{
    private const string Password = "Offline-UI-Test-Password!2026";

    [Fact]
    public Task CreateGenerateAndEdit_ClearStaleResponsesAndPreserveLeadingZeros() => OnStaAsync(async () =>
    {
        using var workspace = new TestWorkspace();
        var store = new AuthorizationProfileStore(Path.Combine(workspace.Root, "profiles.dat"));
        var vm = new OfflineAuthorizationViewModel(store, host: new());
        await vm.InitializeAsync();
        Assert.True(vm.IsStoreReady);
        Assert.False(vm.GenerateCommand.CanExecute(null));
        vm.NewProfileName = "现场 A";
        await vm.CreateCommand.ExecuteAsync(null);
        var selected = Assert.Single(vm.Profiles);
        Assert.Equal(selected, vm.SelectedProfile);
        vm.Challenge = "000012";
        Assert.True(vm.GenerateCommand.CanExecute(null));
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(store.Generate(selected.DeviceId, "000012"), vm.Response);
        Assert.True(vm.CopyCommand.CanExecute(null));
        vm.Challenge = "000013";
        Assert.Empty(vm.Response);
        Assert.False(vm.CopyCommand.CanExecute(null));
        await vm.GenerateCommand.ExecuteAsync(null);
        vm.NewProfileName = "现场 B";
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Empty(vm.Response);
        Assert.NotEqual(selected, vm.SelectedProfile);
        vm.Challenge = "１２３４５６";
        Assert.False(vm.GenerateCommand.CanExecute(null));
        Assert.Contains("六位数字", vm.ChallengeHint);
    });

    [Fact]
    public Task Restore_RequiresConfirmationAndKeepsHostOwnershipUntilDecision() => OnStaAsync(async () =>
    {
        using var workspace = new TestWorkspace();
        var store = new AuthorizationProfileStore(Path.Combine(workspace.Root, "profiles.dat"));
        var profile = store.Create("备份名称");
        var backup = Path.Combine(workspace.Root, "backup.dlauth");
        var host = new BackgroundTaskHost();
        var vm = new OfflineAuthorizationViewModel(store, host: host);
        await vm.InitializeAsync();
        await vm.BackupAsync(backup, Password);
        Assert.True(File.Exists(backup));
        store.Rename(profile.DeviceId, "当前名称");
        await vm.RestoreAsync(backup, Password, (_, _) => Task.FromResult(false));
        Assert.Equal("当前名称", Assert.Single(store.Load()).DisplayName);

        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = vm.RestoreAsync(backup, Password, async (plan, token) =>
        {
            Assert.Equal(1, plan.RenamedProfiles);
            asked.SetResult();
            return await decision.Task.WaitAsync(token);
        });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(host.IsBusy);
        Assert.Equal(BackgroundOperationKind.Authorization, host.CurrentKind);
        Assert.False(host.TryStart(BackgroundOperationKind.Flashing, _ => Task.CompletedTask, out _));
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await restore;
        Assert.False(host.IsBusy);
        Assert.Equal("当前名称", Assert.Single(store.Load()).DisplayName);

        await vm.RestoreAsync(backup, Password, (_, _) => Task.FromResult(true));
        Assert.Equal("备份名称", Assert.Single(vm.Profiles).DisplayName);
        Assert.False(vm.IsBusy);
    });

    [Fact]
    public Task CorruptStore_DisablesGenerationButAllowsExplicitBackupRecovery() => OnStaAsync(async () =>
    {
        using var workspace = new TestWorkspace();
        var source = new AuthorizationProfileStore(Path.Combine(workspace.Root, "source.dat"));
        var profile = source.Create("可恢复档案");
        var backup = Path.Combine(workspace.Root, "backup.dlauth");
        source.SaveBackup(backup, source.CreateBackup(Password));
        var corrupt = workspace.Write("corrupt.dat", "KEEP_DAMAGED_STORE");
        var vm = new OfflineAuthorizationViewModel(new(corrupt), host: new());
        await vm.InitializeAsync();
        Assert.False(vm.IsStoreReady);
        vm.NewProfileName = "不能覆盖损坏档案";
        Assert.False(vm.CreateCommand.CanExecute(null));
        await vm.RestoreAsync(backup, Password, (plan, _) => Task.FromResult(plan.PreservesDamagedFile));
        Assert.True(vm.IsStoreReady);
        Assert.Equal(profile.DeviceId, Assert.Single(vm.Profiles).DeviceId);
        var preserved = Assert.Single(Directory.GetFiles(workspace.Root, "*.damaged-*"));
        Assert.Equal("KEEP_DAMAGED_STORE", File.ReadAllText(preserved));
    });

    [Fact]
    public Task BusyFlashing_DoesNotAllowAuthorizationWorkOrCancelTheFlashTask() => OnStaAsync(async () =>
    {
        using var workspace = new TestWorkspace();
        var store = new AuthorizationProfileStore(Path.Combine(workspace.Root, "profiles.dat"));
        var host = new BackgroundTaskHost();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        Assert.True(host.TryStart(BackgroundOperationKind.Flashing, async token =>
        {
            using var registration = token.Register(() => cancelled = true);
            await finish.Task;
        }, out var flash));
        try
        {
            var vm = new OfflineAuthorizationViewModel(store, host: host);
            await vm.InitializeAsync();
            Assert.False(vm.IsStoreReady);
            Assert.Contains("正在进行", vm.Status);
            Assert.False(vm.CancelCommand.CanExecute(null));
            Assert.False(File.Exists(store.FilePath));
            Assert.False(cancelled);
        }
        finally { finish.SetResult(); await flash; }
    });

    [Fact]
    public Task WrongBackupPassword_DoesNotDisableTheExistingUsableProfiles() => OnStaAsync(async () =>
    {
        using var workspace = new TestWorkspace();
        var store = new AuthorizationProfileStore(Path.Combine(workspace.Root, "profiles.dat"));
        var profile = store.Create("原有档案");
        var backup = Path.Combine(workspace.Root, "backup.dlauth");
        store.SaveBackup(backup, store.CreateBackup(Password));
        var vm = new OfflineAuthorizationViewModel(store, host: new());
        await vm.InitializeAsync();
        var asked = false;
        await vm.RestoreAsync(backup, "Wrong-Backup-Password!2026", (_, _) => { asked = true; return Task.FromResult(true); });
        Assert.False(asked);
        Assert.True(vm.IsStoreReady);
        Assert.Contains("密码错误", vm.Status);
        vm.Challenge = "000012";
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(store.Generate(profile.DeviceId, "000012"), vm.Response);
    });

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
