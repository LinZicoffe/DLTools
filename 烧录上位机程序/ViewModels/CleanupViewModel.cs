using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Models.Cleanup;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.Cleanup;

namespace 烧录上位机程序.ViewModels;

/// <summary>规则行视图模型：勾选状态、扫描聚合数字、分类/删除方式徽标。只读计算属性供 OneWay 绑定。</summary>
public partial class CleanupRuleViewModel(CleanupRule rule) : ObservableObject
{
    [ObservableProperty] private bool _isChecked = rule.DefaultChecked;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long _totalBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileCountText))]
    private int _fileCount;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText))]
    private string? _error;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(FileCountText))]
    private bool _scanned;

    public CleanupRule Rule => rule;
    public string Id => rule.Id;
    public string DisplayName => rule.DisplayName;
    public string Description => rule.Description;
    public string ConsequenceNote => rule.ConsequenceNote;
    public bool IsSelectable => rule.DeleteMode != CleanupDeleteMode.ReportOnly;
    public string RiskClassText => rule.RiskClass switch
    {
        CleanupRiskClass.RegenerableCache => "可再生缓存",
        CleanupRiskClass.AgedLog => "按年龄日志",
        CleanupRiskClass.RollbackCopy => "回滚副本",
        CleanupRiskClass.SessionArtifact => "会话衍生物",
        CleanupRiskClass.ConversationArchive => "对话记录",
        CleanupRiskClass.BackupRotation => "备份轮换",
        CleanupRiskClass.ModelFile => "模型文件",
        CleanupRiskClass.ConversationDatabase => "对话数据库",
        _ => rule.RiskClass.ToString()
    };
    public string DeleteModeText => rule.DeleteMode switch
    {
        CleanupDeleteMode.Permanent => "永久删除",
        CleanupDeleteMode.RecycleBin => "移入回收站",
        _ => "仅展示"
    };
    public string SizeText => !Scanned ? "未扫描" : CleanupViewModel.FormatSize(TotalBytes);
    public string FileCountText => !Scanned ? "—" : $"{FileCount} 项";
    public string ErrorText => Error is null ? "" : "部分目标不可用";

    public void ApplySummary(CleanupRuleSummary summary)
    {
        TotalBytes = summary.TotalBytes;
        FileCount = summary.FileCount;
        SkippedCount = summary.SkippedCount;
        Error = summary.Error;
        Scanned = true;
    }
}

public partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupEngine _engine;
    private readonly BackgroundTaskHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly CleanupSettingsStore _settingsStore;
    private readonly Func<string, bool> _confirm;
    private Task? _ownedTask;
    private bool _cancelRequested;

    public CleanupViewModel(CleanupEngine? engine = null, BackgroundTaskHost? host = null,
        Dispatcher? dispatcher = null, CleanupSettingsStore? settingsStore = null, Func<string, bool>? confirm = null)
    {
        _engine = engine ?? new CleanupEngine();
        _host = host ?? BackgroundTaskHost.Shared;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _settingsStore = settingsStore ?? new CleanupSettingsStore();
        _confirm = confirm ?? DefaultConfirm;

        foreach (var rule in CleanupRuleCatalog.Rules)
        {
            var row = new CleanupRuleViewModel(rule);
            row.PropertyChanged += OnRowPropertyChanged;
            (rule.Group == CleanupGroup.GeneralCache ? GeneralRules : AiRules).Add(row);
        }
    }

    public ObservableCollection<CleanupRuleViewModel> GeneralRules { get; } = new();
    public ObservableCollection<CleanupRuleViewModel> AiRules { get; } = new();

    private IEnumerable<CleanupRuleViewModel> AllRules => GeneralRules.Concat(AiRules);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty]
    private string _status = "点击「扫描预览」查看各类别可释放空间。扫描只做统计，不会删除任何文件。";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyAuditPathCommand))]
    private string _lastAuditPath = "";

    public bool IsIdle => !IsBusy;

    public string SelectedSummaryText
    {
        get
        {
            var selected = AllRules.Where(r => r.IsChecked && r.IsSelectable).ToList();
            if (selected.Count == 0) return "尚未勾选任何类别";
            var permanent = selected.Where(r => r.Rule.DeleteMode == CleanupDeleteMode.Permanent)
                .Sum(r => r.TotalBytes);
            var recycled = selected.Where(r => r.Rule.DeleteMode == CleanupDeleteMode.RecycleBin)
                .Sum(r => r.TotalBytes);
            var parts = new List<string> { $"已勾选 {selected.Count} 类" };
            if (permanent > 0) parts.Add($"永久删除约 {FormatSize(permanent)}");
            if (recycled > 0) parts.Add($"移入回收站约 {FormatSize(recycled)}");
            return string.Join(" · ", parts);
        }
    }

    private bool CanScan() => IsIdle;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!CanScan()) return;
        IsBusy = true;
        _cancelRequested = false;
        CleanupScanResult? result = null;
        try
        {
            if (!_host.TryStart(BackgroundOperationKind.DiskCleanup, token =>
            {
                var progress = new CallbackProgress(update => _dispatcher.InvokeAsync(() =>
                {
                    ProgressText = update.Message;
                    ProgressPercent = update.Percent;
                }));
                result = _engine.Scan(LoadSettings(), progress, token);
                return Task.CompletedTask;
            }, out var task))
            {
                Status = "另一个后台任务正在进行，请等待结束后重试。";
                return;
            }
            // 只有真正拿到全局后台所有权才清空旧数字（与 IP 扫描同一模式）。
            _ownedTask = task;
            foreach (var row in AllRules)
            {
                row.Scanned = false;
                row.TotalBytes = 0;
                row.FileCount = 0;
                row.Error = null;
            }
            ProgressPercent = 0;
            ProgressText = "开始扫描…";
            Status = "正在扫描（只读，不删除任何文件）…";
            OnPropertyChanged(nameof(SelectedSummaryText));
            CancelCommand.NotifyCanExecuteChanged();
            await task;

            if (result is null || result.Cancelled)
            {
                Status = "扫描已取消。";
                return;
            }
            foreach (var summary in result.Summaries)
            {
                var row = AllRules.FirstOrDefault(r => r.Id == summary.RuleId);
                row?.ApplySummary(summary);
            }
            ProgressPercent = 100;
            ProgressText = "扫描完成";
            var total = result.Summaries.Sum(s => s.TotalBytes);
            Status = $"扫描完成：全部类别合计约 {FormatSize(total)}。勾选要清理的类别后点击「清理选中项」。";
            OnPropertyChanged(nameof(SelectedSummaryText));
            CleanCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            Status = "扫描已取消。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = "扫描失败：" + error.Message;
        }
        finally
        {
            _ownedTask = null;
            IsBusy = false;
        }
    }

    private bool CanClean() => IsIdle && AllRules.Any(r => r.IsChecked && r.IsSelectable);

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        if (!CanClean()) return;
        var selected = AllRules.Where(r => r.IsChecked && r.IsSelectable).ToList();
        var summary = SelectedSummaryText + "\n\n永久删除的类别无法从本工具找回；移入回收站的类别可从回收站还原。\n确定开始清理吗？";
        bool confirmed;
        try { confirmed = _confirm(summary); }
        catch (Exception error)
        {
            // 确认对话框本身出异常（等价 async-void 入口）不允许逃逸崩进程：按未确认处理。
            Status = "确认对话框异常，未执行清理：" + error.Message;
            return;
        }
        if (!confirmed)
        {
            Status = "已取消：未执行任何删除。";
            return;
        }

        IsBusy = true;
        _cancelRequested = false;
        CleanupResult? result = null;
        try
        {
            if (!_host.TryStart(BackgroundOperationKind.DiskCleanup, token =>
            {
                var progress = new CallbackProgress(update => _dispatcher.InvokeAsync(() =>
                {
                    ProgressText = update.Message;
                    ProgressPercent = update.Percent;
                }));
                result = _engine.Clean(selected.Select(r => r.Id).ToArray(), LoadSettings(strict: true),
                    simulate: false, progress, token);
                return Task.CompletedTask;
            }, out var task))
            {
                Status = "另一个后台任务正在进行，请等待结束后重试。";
                return;
            }
            _ownedTask = task;
            ProgressPercent = 0;
            ProgressText = "开始清理…";
            Status = "正在清理（每个文件删除前都会重新校验，占用中的文件自动跳过）…";
            CancelCommand.NotifyCanExecuteChanged();
            await task;

            if (result is null) return;
            LastAuditPath = result.AuditLogPath;
            if (result.Cancelled)
            {
                Status = $"清理已取消（已完成部分详见审计日志）。永久 {result.DeletedCount} 项 / 回收站 {result.RecycledCount} 项 / 跳过 {result.SkippedCount} 项 / 失败 {result.FailedCount} 项。";
            }
            else if (result.Error is not null)
            {
                Status = "清理失败：" + result.Error;
            }
            else
            {
                Status = $"清理完成：释放约 {FormatSize(result.ReclaimedBytes)}。" +
                    $"永久删除 {result.DeletedCount} 项 / 移入回收站 {result.RecycledCount} 项 / 跳过 {result.SkippedCount} 项" +
                    (result.FailedCount > 0 ? $" / 失败 {result.FailedCount} 项（多为文件占用）" : "") +
                    "。可重新扫描确认。";
            }
            ResetScanNumbers();
        }
        catch (OperationCanceledException)
        {
            Status = "清理已取消（已完成部分详见审计日志）。";
            ResetScanNumbers();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = "清理失败：" + error.Message;
            ResetScanNumbers();
        }
        finally
        {
            _ownedTask = null;
            IsBusy = false;
        }
    }

    /// <summary>清理结束（成功/取消/失败）后清空扫描数字：这批数字描述的是已被删除的内容，
    /// 保留会让页头摘要与二次确认继续显示陈旧的「可释放」量。重新扫描后恢复。</summary>
    private void ResetScanNumbers()
    {
        foreach (var row in AllRules)
        {
            row.Scanned = false;
            row.TotalBytes = 0;
            row.FileCount = 0;
            row.Error = null;
        }
        OnPropertyChanged(nameof(SelectedSummaryText));
        CleanCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancel() => IsBusy && !_cancelRequested && _ownedTask is { IsCompleted: false }
        && ReferenceEquals(_host.CurrentTask, _ownedTask);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cancelRequested = true;
        Status = "正在取消，等待当前文件处理完成…";
        CancelCommand.NotifyCanExecuteChanged();
        if (ReferenceEquals(_host.CurrentTask, _ownedTask))
            _host.RequestCancellation();
    }

    private bool CanCopyAuditPath() => IsIdle && LastAuditPath.Length > 0;

    /// <summary>复制审计日志路径（新代码不沿用仓库存量的 explorer /select 拼接启动模式，规避命令注入面）。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyAuditPath))]
    private void CopyAuditPath()
    {
        if (!CanCopyAuditPath()) return;
        try
        {
            System.Windows.Clipboard.SetText(LastAuditPath);
            Status = "审计日志路径已复制，可在资源管理器地址栏粘贴打开：" + LastAuditPath;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard.SetText 抛的是 ExternalException（COMException 的基类）——捕基类才能接住剪贴板降级。
            Status = "复制审计路径失败：" + error.Message;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(CleanupRuleViewModel.IsChecked) or nameof(CleanupRuleViewModel.TotalBytes))) return;
        OnPropertyChanged(nameof(SelectedSummaryText));
        CleanCommand.NotifyCanExecuteChanged();
    }

    private CleanupSettings LoadSettings(bool strict = false)
    {
        try { return _settingsStore.Load(); }
        catch (IOException) when (!strict)
        {
            return CleanupSettings.Default; // 扫描路径：损坏时退回默认值，不阻断只读预览
        }
        // 清理路径（strict）：设置损坏时不得静默改用默认阈值——用户可能把年龄阈值调得更保守，
        // 默认值反而更激进，确认框引用的数字与实际执行的阈值将不一致。中止并提示修复。
    }

    private static bool DefaultConfirm(string message) =>
        // 带 owner：确认期间主窗口被模态禁用，杜绝叠出第二个确认框/关窗绕过确认。
        System.Windows.MessageBox.Show(System.Windows.Application.Current?.MainWindow, message, "确认清理",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
        _ => $"{bytes} B"
    };

    private sealed class CallbackProgress(Action<CleanupProgress> report) : IProgress<CleanupProgress>
    {
        public void Report(CleanupProgress value) => report(value);
    }
}
