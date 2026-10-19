using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Models.Authorization;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.Authorization;

namespace 烧录上位机程序.ViewModels;

public partial class OfflineAuthorizationViewModel : ObservableObject
{
    private readonly AuthorizationProfileStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly BackgroundTaskHost _host;
    private bool _initialized;
    private bool _ownsOperation;

    public OfflineAuthorizationViewModel(AuthorizationProfileStore? store = null, Dispatcher? dispatcher = null, BackgroundTaskHost? host = null)
    {
        _store = store ?? new();
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _host = host ?? BackgroundTaskHost.Shared;
    }

    public ObservableCollection<AuthorizationProfile> Profiles { get; } = [];
    public string StorePath => _store.FilePath;
    [ObservableProperty] private AuthorizationProfile? _selectedProfile;
    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private string _renameName = "";
    [ObservableProperty] private string _challenge = "";
    [ObservableProperty] private string _response = "";
    [ObservableProperty] private string _status = "首次使用请新建设备档案，并及时创建加密备份。";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStoreReady;
    public bool IsIdle => !IsBusy;
    public string SelectedDeviceId => SelectedProfile?.DeviceId ?? "尚未选择设备档案";
    public string ChallengeHint => Challenge.Trim().Length == 0 ? "请输入客户提供的六位申请码，保留前导零。"
        : IsValidChallenge() ? "申请码格式正确。" : "仅接受六位数字 0–9，例如 000012。";

    partial void OnSelectedProfileChanged(AuthorizationProfile? value)
    {
        RenameName = value?.DisplayName ?? "";
        Response = "";
        OnPropertyChanged(nameof(SelectedDeviceId));
        NotifyCommands();
    }
    partial void OnNewProfileNameChanged(string value) => NotifyCommands();
    partial void OnRenameNameChanged(string value) => NotifyCommands();
    partial void OnResponseChanged(string value) => CopyCommand.NotifyCanExecuteChanged();
    partial void OnChallengeChanged(string value)
    {
        Response = "";
        OnPropertyChanged(nameof(ChallengeHint));
        NotifyCommands();
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsIdle)); NotifyCommands(); }
    partial void OnIsStoreReadyChanged(bool value) => NotifyCommands();

    public Task InitializeAsync() => _initialized || IsBusy ? Task.CompletedTask : ReloadAsync();

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync()
    {
        IsStoreReady = false;
        return RunAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            var profiles = _store.Load();
            await OnUiAsync(() => { SetProfiles(profiles); _initialized = true; Status = profiles.Count == 0
                ? "尚无档案。请填写客户／设备名称并新建，然后创建加密备份。" : $"已加载 {profiles.Count} 个设备档案。"; });
        });
    }
    private bool CanReload() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync()
    {
        var name = NewProfileName;
        return RunAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            var created = _store.Create(name);
            var profiles = _store.Load();
            await OnUiAsync(() => { SetProfiles(profiles, created.DeviceId); NewProfileName = "";
                Status = "档案已创建并加密保存。请及时备份；客户验证端仍需由我方预置相同设备密钥。"; });
        });
    }
    private bool CanCreate() => IsIdle && IsStoreReady && !string.IsNullOrWhiteSpace(NewProfileName);

    [RelayCommand(CanExecute = nameof(CanRename))]
    private Task RenameAsync()
    {
        var id = SelectedProfile!.DeviceId;
        var name = RenameName;
        return RunAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            _store.Rename(id, name);
            var profiles = _store.Load();
            await OnUiAsync(() => { SetProfiles(profiles, id); Status = "名称已更新，设备标识和密钥保持不变。"; });
        });
    }
    private bool CanRename() => IsIdle && IsStoreReady && SelectedProfile is not null
        && !string.IsNullOrWhiteSpace(RenameName) && RenameName.Trim() != SelectedProfile.DisplayName;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync()
    {
        var id = SelectedProfile!.DeviceId;
        var input = Challenge;
        return RunAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            var result = _store.Generate(id, input);
            await OnUiAsync(() =>
            {
                if (SelectedProfile?.DeviceId == id && Challenge == input) Response = result;
                Status = "授权码已生成，可告知客户输入。请核对设备档案与申请码。";
            });
        });
    }
    private bool CanGenerate() => IsIdle && IsStoreReady && SelectedProfile is not null && IsValidChallenge();
    private bool IsValidChallenge()
    {
        var value = Challenge.Trim();
        return value.Length == 6 && value.All(c => c is >= '0' and <= '9');
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        try { Clipboard.SetText(Response); Status = "授权码已复制。"; }
        catch (System.Runtime.InteropServices.ExternalException) { Status = "剪贴板正在被占用，请稍后重试。"; }
    }
    private bool CanCopy() => IsIdle && Response.Length == 6;

    public Task BackupAsync(string path, string password) => RunAsync(async token =>
    {
        token.ThrowIfCancellationRequested();
        var bytes = _store.CreateBackup(password);
        try { _store.SaveBackup(path, bytes, token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        await OnUiAsync(() => Status = "加密备份已保存。请分开保管备份与密码，客户无需使用此文件。");
    }, clearResult: false);

    public Task RestoreAsync(string path, string password, Func<AuthorizationRestorePlan, CancellationToken, Task<bool>> confirm) =>
        RunAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            var bytes = AuthorizationProfileStore.ReadBackup(path);
            using var plan = _store.PrepareRestore(bytes, password, recoverUnreadableStore: true);
            token.ThrowIfCancellationRequested();
            if (!await confirm(plan, token)) { await OnUiAsync(() => Status = "已取消恢复，原档案未修改。"); return; }
            var preserved = _store.CommitRestore(plan, token);
            var profiles = _store.Load();
            await OnUiAsync(() => { SetProfiles(profiles); _initialized = true; Status = preserved is null
                ? "档案已合并恢复，现有其他设备档案予以保留。"
                : "档案已恢复，原损坏文件保留于：" + preserved; });
        });

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _host.RequestCancellation();
        Status = "正在取消，请等待安全保存边界。";
    }
    private bool CanCancel() => IsBusy && _ownsOperation;

    private async Task RunAsync(Func<CancellationToken, Task> action, bool clearResult = true)
    {
        if (IsBusy) return;
        IsBusy = true;
        if (clearResult) Response = "";
        Status = "正在处理授权档案…";
        try
        {
            if (!_host.TryStart(BackgroundOperationKind.Authorization, action, out var task))
            {
                Status = "另一个后台任务正在进行，请等待结束后重试。";
                return;
            }
            _ownsOperation = true;
            CancelCommand.NotifyCanExecuteChanged();
            await task;
        }
        catch (OperationCanceledException) { Status = "任务已取消；已完成的原子保存不回滚。"; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or CryptographicException or NotSupportedException)
        {
            Status = error.Message;
        }
        finally { _ownsOperation = false; IsBusy = false; }
    }

    private Task OnUiAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

    private void SetProfiles(IReadOnlyList<AuthorizationProfile> profiles, string? selectedId = null)
    {
        selectedId ??= SelectedProfile?.DeviceId;
        Profiles.Clear();
        foreach (var profile in profiles) Profiles.Add(profile);
        SelectedProfile = profiles.FirstOrDefault(p => p.DeviceId == selectedId) ?? profiles.FirstOrDefault();
        IsStoreReady = true;
    }

    private void NotifyCommands()
    {
        ReloadCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
