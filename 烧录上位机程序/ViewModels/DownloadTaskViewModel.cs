using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace 烧录上位机程序.ViewModels;

/// <summary>任务列表中的一行。字段由 DownloadViewModel 在 UI 线程上从 DownloadTask 快照刷新。</summary>
public partial class DownloadTaskViewModel : ObservableObject
{
    private readonly DownloadScheduler _scheduler;

    public DownloadTaskViewModel(DownloadTask task, DownloadScheduler scheduler)
    {
        Task = task;
        _scheduler = scheduler;
        RefreshFromTask();
    }

    public DownloadTask Task { get; }
    public Guid Id => Task.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsPendingOrRunning))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsTerminal))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private DownloadTaskState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    private long _downloadedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    private long _speedBytesPerSec;

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string? _error;

    public string StateText => State switch
    {
        DownloadTaskState.Queued => "排队中",
        DownloadTaskState.Connecting => "连接中",
        DownloadTaskState.Downloading => "下载中",
        DownloadTaskState.Paused => "已暂停",
        DownloadTaskState.Verifying => "校验中",
        DownloadTaskState.Committing => "保存中",
        DownloadTaskState.Completed => "已完成",
        DownloadTaskState.Failed => "失败",
        DownloadTaskState.Cancelled => "已取消",
        _ => ""
    };

    public bool IsRunning => State is DownloadTaskState.Connecting or DownloadTaskState.Downloading
        or DownloadTaskState.Verifying or DownloadTaskState.Committing;
    public bool IsPendingOrRunning => IsRunning || State == DownloadTaskState.Queued;
    public bool IsPaused => State == DownloadTaskState.Paused;
    public bool IsTerminal => State is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Cancelled;
    public bool IsFailed => State == DownloadTaskState.Failed;

    public double ProgressPercent => TotalBytes > 0 ? DownloadedBytes * 100.0 / TotalBytes : State == DownloadTaskState.Completed ? 100 : 0;
    public string SizeText => $"{FormatBytes(DownloadedBytes)} / {(TotalBytes > 0 ? FormatBytes(TotalBytes) : "未知大小")}";
    public string SpeedText => SpeedBytesPerSec > 0 ? $"{FormatBytes(SpeedBytesPerSec)}/s" : "";
    public string EtaText => SpeedBytesPerSec > 0 && TotalBytes > DownloadedBytes
        ? TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / (double)SpeedBytesPerSec).ToString(@"hh\:mm\:ss")
        : "";

    public void RefreshFromTask()
    {
        var task = Task;
        State = task.State;
        TotalBytes = task.TotalBytes;
        DownloadedBytes = task.DownloadedBytes;
        SpeedBytesPerSec = task.SpeedBytesPerSec;
        Message = task.Message;
        Error = task.Error;
        DisplayName = task.FilePath is { Length: > 0 } ? Path.GetFileName(task.FilePath)
            : task.Options.FileNameOverride.Length > 0 ? task.Options.FileNameOverride
            : task.Options.Url;
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _scheduler.Pause(Id);
    private bool CanPause() => IsPendingOrRunning;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => _scheduler.Resume(Id);
    private bool CanResume() => IsPaused;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _scheduler.Cancel(Id);
    private bool CanCancel() => !IsTerminal;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => _scheduler.Retry(Id);
    private bool CanRetry() => State is DownloadTaskState.Failed or DownloadTaskState.Cancelled;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        // 行状态经 Dispatcher 投递可能滞后（暂停→继续的瞬间按钮仍可点）：调度器拒删时
        // 只刷新本行状态让按钮重新评估，不让异常穿透到全局错误弹窗
        try { _scheduler.Remove(Id); } // 列表行由 TasksChanged 事件移除
        catch (InvalidOperationException) { RefreshFromTask(); }
    }
    private bool CanRemove() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenFolder()
    {
        if (Task.FilePath is not { } path) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Message = "打开文件夹失败：" + error.Message;
        }
    }
    private bool CanOpen() => Task.FilePath is not null && State == DownloadTaskState.Completed;

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024.0:0.##} KB",
        _ => $"{bytes} B"
    };
}
