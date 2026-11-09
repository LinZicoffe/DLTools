using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace 烧录上位机程序.ViewModels;

/// <summary>高速下载页视图模型：新建下载表单、下载设置、任务列表。
/// 调度器事件来自线程池线程，全部经 Dispatcher 投递到 UI 线程后再改集合与行状态。</summary>
public partial class DownloadViewModel : ObservableObject
{
    private readonly DownloadScheduler _scheduler;
    private readonly DownloadSettingsStore _store;
    private readonly MirrorManifestService _manifestService;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, DownloadTaskViewModel> _rowsById = [];

    public DownloadViewModel(DownloadScheduler? scheduler = null, string? settingsPath = null,
        Dispatcher? dispatcher = null, MirrorManifestService? manifestService = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _store = new DownloadSettingsStore(settingsPath);
        _scheduler = scheduler ?? DownloadScheduler.Shared;
        _manifestService = manifestService ?? new MirrorManifestService();

        DownloadSettings settings;
        try
        {
            settings = _store.Load();
        }
        catch (IOException error)
        {
            settings = new DownloadSettings();
            StatusText = error.Message; // 损坏不静默：提示用户修复，页面仍按默认可用
        }
        DefaultConnections = settings.DefaultConnections;
        MaxConcurrentTasks = settings.MaxConcurrentTasks;
        MaxRateKB = settings.MaxRateKB;
        ProxyHost = settings.ProxyHost;
        ProxyPort = settings.ProxyPort;
        DestinationDirectory = ResolveDefaultDestination(settings.LastDestinationDirectory);
        _scheduler.UpdateSettings(settings);

        _scheduler.TaskChanged += OnSchedulerTaskChanged;
        _scheduler.TasksChanged += OnSchedulerTasksChanged;
    }

    // ---- 新建下载表单 ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDownloadCommand))]
    private string _downloadUrl = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDownloadCommand))]
    private string _destinationDirectory = "";

    [ObservableProperty] private string _connectionsText = "";   // 留空 = 使用默认连接数
    [ObservableProperty] private string _maxRateKBText = "";     // 留空 = 使用全局限速
    [ObservableProperty] private string _sha256Text = "";        // 留空 = 不校验
    [ObservableProperty] private bool _startPaused;

    [ObservableProperty] private string _statusText = "";

    // ---- 下载设置 ----

    [ObservableProperty] private int _defaultConnections;
    [ObservableProperty] private int _maxConcurrentTasks;
    [ObservableProperty] private long _maxRateKB;
    [ObservableProperty] private string _proxyHost = "";
    [ObservableProperty] private int _proxyPort;

    public ObservableCollection<DownloadTaskViewModel> Tasks { get; } = [];

    // ---- 镜像清单导入 ----

    [ObservableProperty] private string _manifestUrl = "";
    [ObservableProperty] private bool _manifestLoaded;
    [ObservableProperty] private int _selectedFileCount;

    public ObservableCollection<MirrorFileItemViewModel> ManifestFiles { get; } = [];

    [RelayCommand]
    private async Task LoadManifestAsync()
    {
        try
        {
            var manifest = await _manifestService.FetchAsync(ManifestUrl, CancellationToken.None);
            ManifestFiles.Clear();
            foreach (var entry in manifest.Files)
            {
                var item = new MirrorFileItemViewModel(entry) { IsSelected = true };
                item.PropertyChanged += (_, _) => SelectedFileCount = ManifestFiles.Count(file => file.IsSelected);
                ManifestFiles.Add(item);
            }
            ManifestLoaded = ManifestFiles.Count > 0;
            SelectedFileCount = ManifestFiles.Count(file => file.IsSelected);
            StatusText = $"清单已加载：{ManifestFiles.Count} 个文件。勾选后点击“导入所选”加入下载队列。";
        }
        catch (ArgumentException error)
        {
            StatusText = error.Message; // 地址校验（https/公网）失败
        }
        catch (DownloadTransportException error)
        {
            StatusText = error.Message;
        }
        catch (IOException error)
        {
            StatusText = "清单读取失败：" + error.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportSelected))]
    private async Task ImportSelectedAsync()
    {
        var destination = DestinationDirectory.Trim();
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception error)
        {
            StatusText = "无法创建目标文件夹：" + error.Message;
            return;
        }
        if (!int.TryParse(ConnectionsText.Trim(), out var connections) || connections is < 1 or > 16)
            connections = DefaultConnections;

        var selected = ManifestFiles.Where(file => file.IsSelected).ToArray();
        foreach (var file in selected)
        {
            // 节点竞速排序：同一清单的条目共享一次测量结果无必要，逐条目测量（节点少、5s 上限）
            var ordered = await _manifestService.OrderUrlsByLatencyAsync(file.Entry, CancellationToken.None);
            _scheduler.Enqueue(_manifestService.BuildOptions(file.Entry, Path.GetFullPath(destination),
                connections, maxRateKBps: null, StartPaused, ordered));
        }
        StatusText = $"已导入 {selected.Length} 个清单文件到下载队列。";
    }
    private bool CanImportSelected() => ManifestLoaded && SelectedFileCount > 0;

    partial void OnSelectedFileCountChanged(int value) => ImportSelectedCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanAddDownload))]
    private void AddDownload()
    {
        var url = DownloadUrl.Trim();
        try
        {
            DownloadUrlGuard.EnsureDownloadUrl(url);
        }
        catch (ArgumentException error)
        {
            StatusText = error.Message;
            return;
        }
        if (!int.TryParse(ConnectionsText.Trim(), out var connections) || connections is < 1 or > 16)
            connections = DefaultConnections;
        long? maxRate = null;
        if (MaxRateKBText.Trim().Length > 0)
        {
            if (!long.TryParse(MaxRateKBText.Trim(), out var parsed) || parsed < 0)
            {
                StatusText = "限速必须是非负整数（KB/s），留空表示跟随全局设置。";
                return;
            }
            maxRate = parsed;
        }
        var sha256 = Sha256Text.Trim();
        if (sha256.Length > 0 && sha256.Length != 64 || sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')))
        {
            StatusText = "SHA-256 应为 64 位十六进制字符，留空表示不校验。";
            return;
        }

        var destination = DestinationDirectory.Trim();
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception error)
        {
            StatusText = "无法创建目标文件夹：" + error.Message;
            return;
        }

        _scheduler.Enqueue(new DownloadOptions
        {
            Url = url,
            DestinationDirectory = Path.GetFullPath(destination),
            Connections = connections,
            MaxRateKBps = maxRate,
            ExpectedSha256 = sha256,
            StartPaused = StartPaused
        });
        StatusText = StartPaused ? "已加入队列（暂停状态）" : "已加入下载队列";
        DownloadUrl = "";
        SaveLastDestination(destination);
    }
    private bool CanAddDownload() => DownloadUrl.Trim().Length > 0 && DestinationDirectory.Trim().Length > 0;

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载保存位置"
        };
        if (Directory.Exists(DestinationDirectory)) dialog.InitialDirectory = DestinationDirectory;
        if (dialog.ShowDialog() == true)
        {
            DestinationDirectory = dialog.FolderName;
            SaveLastDestination(dialog.FolderName);
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        ProxyPort = Math.Clamp(ProxyPort, 0, 65535);
        var settings = new DownloadSettings
        {
            DefaultConnections = Math.Clamp(DefaultConnections, 1, 16),
            MaxConcurrentTasks = Math.Clamp(MaxConcurrentTasks, 1, 8),
            MaxRateKB = Math.Max(0, MaxRateKB),
            ProxyHost = ProxyHost.Trim(),
            ProxyPort = ProxyPort,
            LastDestinationDirectory = DestinationDirectory.Trim()
        };
        try
        {
            _store.Save(settings);
        }
        catch (IOException error)
        {
            StatusText = error.Message;
            return;
        }
        _scheduler.UpdateSettings(settings);
        StatusText = "下载设置已保存。";
    }

    private void SaveLastDestination(string directory)
    {
        try
        {
            var settings = SafeLoadSettings() with { LastDestinationDirectory = directory };
            _store.Save(settings);
        }
        catch (IOException)
        {
            // 保存上次目录失败不影响使用；下次保存设置时会再尝试
        }
    }

    private DownloadSettings SafeLoadSettings()
    {
        try { return _store.Load(); }
        catch (IOException) { return new DownloadSettings(); }
    }

    private static string ResolveDefaultDestination(string? lastUsed)
    {
        if (!string.IsNullOrWhiteSpace(lastUsed) && Directory.Exists(lastUsed)) return lastUsed;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ---- 调度器事件 → UI 线程 ----

    private void OnSchedulerTaskChanged(DownloadTask task)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (_rowsById.TryGetValue(task.Id, out var row)) row.RefreshFromTask();
        });
    }

    private void OnSchedulerTasksChanged()
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            var live = _scheduler.Tasks.OrderBy(task => task.Sequence).ToList();
            for (var i = Tasks.Count - 1; i >= 0; i--)
                if (live.All(task => task.Id != Tasks[i].Id))
                {
                    _rowsById.Remove(Tasks[i].Id);
                    Tasks.RemoveAt(i);
                }
            foreach (var task in live)
                if (!_rowsById.ContainsKey(task.Id))
                {
                    var row = new DownloadTaskViewModel(task, _scheduler);
                    _rowsById[task.Id] = row;
                    Tasks.Add(row);
                    row.RefreshFromTask();
                }
        });
    }
}
