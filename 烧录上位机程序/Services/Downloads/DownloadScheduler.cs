using 烧录上位机程序.Models.Downloads;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>调度器持有的单个下载任务：可变状态对象，字段由引擎回调线程更新，
/// UI 通过 TaskChanged 事件在 Dispatcher 上读取快照。</summary>
public sealed class DownloadTask
{
    public Guid Id { get; } = Guid.NewGuid();
    public long Sequence { get; init; }                    // FIFO 序号
    public required DownloadOptions Options { get; init; }
    public bool ForceFreshOnRetry { get; internal set; }   // 续传记录损坏后的重试改为全新下载

    public DownloadTaskState State { get; internal set; }
    public long TotalBytes { get; internal set; }
    public long DownloadedBytes { get; internal set; }
    public long SpeedBytesPerSec { get; internal set; }
    public int ActiveSegments { get; internal set; }
    public string Message { get; internal set; } = "";
    public string? FilePath { get; internal set; }
    public string? Error { get; internal set; }

    internal bool PauseRequested { get; set; }
    internal CancellationTokenSource? Cancellation { get; set; }
    internal Task? RunTask { get; set; }

    public bool IsActive => State is DownloadTaskState.Queued or DownloadTaskState.Connecting
        or DownloadTaskState.Downloading or DownloadTaskState.Verifying or DownloadTaskState.Committing;
}

/// <summary>多任务下载队列：与 BackgroundTaskHost 全局互斥无关（下载可与烧录/打包并行），
/// 内部用 MaxConcurrentTasks 槽位 + FIFO 出队；暂停/取消保留续传日志，重试自动复用。
/// 非 UI 组件：事件可能从线程池线程触发，订阅方自行调度到 UI 线程。</summary>
public sealed class DownloadScheduler
{
    public static DownloadScheduler Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Func<DownloadEngine> _engineFactory;
    private readonly List<DownloadTask> _tasks = [];
    private long _sequence;
    private int _running;
    private int _maxConcurrentTasks = 3;
    private DownloadSettings _currentSettings = new();
    private DownloadRateLimiter? _globalRateLimiter;

    /// <summary>任务状态/进度变化（含新增与移除后的列表变化用 TasksChanged）。</summary>
    public event Action<DownloadTask>? TaskChanged;
    public event Action? TasksChanged;

    public DownloadScheduler(Func<DownloadEngine>? engineFactory = null, int maxConcurrentTasks = 3)
    {
        _engineFactory = engineFactory ?? CreateDefaultEngine;
        _maxConcurrentTasks = Math.Clamp(maxConcurrentTasks, 1, 8);
    }

    /// <summary>默认引擎工厂：新任务按当前设置建传输，并挂接全局限速器（UpdateSettings 后对新任务生效）。</summary>
    private DownloadEngine CreateDefaultEngine()
    {
        DownloadSettings settings;
        DownloadRateLimiter? limiter;
        lock (_gate)
        {
            settings = _currentSettings;
            limiter = _globalRateLimiter;
        }
        return new DownloadEngine(new DownloadHttpTransport(settings), limiter);
    }

    /// <summary>当前全局限速器（MaxRateKB&gt;0 时非空）；测试用于断言设置接线。</summary>
    public DownloadRateLimiter? GlobalRateLimiter
    {
        get { lock (_gate) return _globalRateLimiter; }
    }

    public int MaxConcurrentTasks
    {
        get { lock (_gate) return _maxConcurrentTasks; }
        set
        {
            List<DownloadTask> started;
            lock (_gate)
            {
                var clamped = Math.Clamp(value, 1, 8);
                if (clamped == _maxConcurrentTasks) return;
                _maxConcurrentTasks = clamped;
                started = PumpLocked();
            }
            foreach (var item in started) TaskChanged?.Invoke(item);
        }
    }

    public IReadOnlyList<DownloadTask> Tasks
    {
        get { lock (_gate) return _tasks.ToArray(); }
    }

    /// <summary>是否存在未终态且未暂停的任务（关窗确认判定用；暂停任务保留续传记录，可安全退出）。</summary>
    public bool HasActiveWork
    {
        get { lock (_gate) return _tasks.Any(task => task.IsActive); }
    }

    /// <summary>应用设置变更：并发槽位立即生效；代理/全局限速对之后启动的任务生效（在途任务沿用创建时的引擎配置）。</summary>
    public void UpdateSettings(DownloadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _currentSettings = settings;
            _globalRateLimiter = settings.MaxRateKB > 0
                ? new DownloadRateLimiter(settings.MaxRateKB * 1024)
                : null; // 0 = 不限速
        }
        MaxConcurrentTasks = settings.MaxConcurrentTasks;
    }

    public DownloadTask Enqueue(DownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var task = new DownloadTask
        {
            Options = options,
            Sequence = System.Threading.Interlocked.Increment(ref _sequence),
            State = options.StartPaused ? DownloadTaskState.Paused : DownloadTaskState.Queued,
            Message = options.StartPaused ? "已暂停入队" : ""
        };
        List<DownloadTask> started;
        lock (_gate)
        {
            _tasks.Add(task);
            started = PumpLocked();
        }
        TasksChanged?.Invoke();
        foreach (var item in started) TaskChanged?.Invoke(item);
        if (task.State is DownloadTaskState.Connecting or DownloadTaskState.Downloading)
            TaskChanged?.Invoke(task);
        return task;
    }

    public bool Pause(Guid id)
    {
        DownloadTask? task;
        lock (_gate)
        {
            task = _tasks.FirstOrDefault(item => item.Id == id);
            if (task is null) return false;
            // 状态写入统一在 _gate + lock(task) 嵌套下完成，避免与泵（PumpLocked）的取任务竞争
            lock (task)
            {
                switch (task.State)
                {
                    case DownloadTaskState.Queued:
                        task.State = DownloadTaskState.Paused;
                        task.Message = "已暂停，可继续（保留续传记录）";
                        break;
                    case DownloadTaskState.Connecting or DownloadTaskState.Downloading
                        or DownloadTaskState.Verifying or DownloadTaskState.Committing:
                        task.PauseRequested = true;
                        task.Message = "正在暂停，等待当前分段收尾…";
                        try { task.Cancellation?.Cancel(); }
                        catch (ObjectDisposedException) { }
                        break; // 终态在引擎返回时落为 Paused
                    default:
                        return false;
                }
            }
        }
        TaskChanged?.Invoke(task);
        return true;
    }

    public bool Resume(Guid id)
    {
        DownloadTask? task;
        List<DownloadTask> started;
        lock (_gate)
        {
            task = _tasks.FirstOrDefault(item => item.Id == id);
            if (task is null) return false;
            lock (task)
            {
                if (task.State != DownloadTaskState.Paused) return false;
                // 此刻旧 RunAsync 至多还剩 finally（引擎已返回才会落 Paused）；CTS 归属检查保证新一轮的
                // 取消能力不会被旧 finally 销毁，_running 增减自洽，无双引擎窗口
                task.State = DownloadTaskState.Queued;
                task.Message = "";
            }
            started = PumpLocked();
        }
        TaskChanged?.Invoke(task);
        foreach (var item in started) TaskChanged?.Invoke(item);
        return true;
    }

    public bool Cancel(Guid id)
    {
        DownloadTask? task;
        bool immediate;
        lock (_gate)
        {
            task = _tasks.FirstOrDefault(item => item.Id == id);
            if (task is null) return false;
            immediate = false;
            lock (task)
            {
                switch (task.State)
                {
                    case DownloadTaskState.Queued or DownloadTaskState.Paused:
                        task.State = DownloadTaskState.Cancelled;
                        task.Message = "已取消（保留续传记录）";
                        immediate = true;
                        break;
                    case DownloadTaskState.Connecting or DownloadTaskState.Downloading
                        or DownloadTaskState.Verifying or DownloadTaskState.Committing:
                        task.PauseRequested = false;
                        try { task.Cancellation?.Cancel(); }
                        catch (ObjectDisposedException) { }
                        return true; // 终态在引擎返回时落为 Cancelled
                    default:
                        return false;
                }
            }
        }
        if (immediate) TaskChanged?.Invoke(task);
        return true;
    }

    /// <summary>重试失败/已取消的任务；上次失败若因续传记录损坏或终验不符（校验和/大小），
    /// 既有临时数据已不可信，重试自动改为全新下载。</summary>
    public bool Retry(Guid id)
    {
        DownloadTask? task;
        List<DownloadTask> started;
        lock (_gate)
        {
            task = _tasks.FirstOrDefault(item => item.Id == id);
            if (task is null) return false;
            lock (task)
            {
                if (task.State is not (DownloadTaskState.Failed or DownloadTaskState.Cancelled)) return false;
                if (task.Error is { } error
                    && (error.Contains("续传记录已损坏", StringComparison.Ordinal)
                        || error.Contains("校验不通过", StringComparison.Ordinal)
                        || error.Contains("大小不符", StringComparison.Ordinal)))
                {
                    task.ForceFreshOnRetry = true;
                }
                task.State = DownloadTaskState.Queued;
                task.Error = null;
                task.Message = "";
            }
            started = PumpLocked();
        }
        TaskChanged?.Invoke(task);
        foreach (var item in started) TaskChanged?.Invoke(item);
        return true;
    }

    /// <summary>移除任务记录。仅允许非运行中的任务（先暂停/取消/等待结束）。</summary>
    public bool Remove(Guid id)
    {
        DownloadTask? task;
        lock (_gate)
        {
            task = _tasks.FirstOrDefault(item => item.Id == id);
            if (task is null) return false;
            lock (task)
            {
                if (task.IsActive && task.State != DownloadTaskState.Queued)
                    throw new InvalidOperationException("任务仍在运行，请先暂停或取消后再移除。");
            }
            _tasks.Remove(task);
        }
        TasksChanged?.Invoke();
        return true;
    }

    /// <summary>取消全部活动任务（暂停语义，保留续传记录）并等待在途引擎排空。</summary>
    public async Task CancelAllAndDrainAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            foreach (var task in _tasks)
            {
                lock (task)
                {
                    if (task.State == DownloadTaskState.Queued)
                    {
                        // 尚未启动的任务直接置为暂停：退出后不再有"活动"任务
                        task.State = DownloadTaskState.Paused;
                        task.Message = "已暂停，可继续（保留续传记录）";
                        continue;
                    }
                    if (task.State == DownloadTaskState.Paused) continue;
                    task.PauseRequested = true;
                    try { task.Cancellation?.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }
            pending = _tasks.Where(task => task.RunTask is { IsCompleted: false })
                .Select(task => task.RunTask!).ToArray();
        }
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* 引擎取消路径的异常已在任务内部消化；这里只负责排空 */ }
        }
    }

    private List<DownloadTask> PumpLocked()
    {
        var started = new List<DownloadTask>();
        while (_running < _maxConcurrentTasks)
        {
            var next = _tasks.Where(task => task.State == DownloadTaskState.Queued)
                .OrderBy(task => task.Sequence).FirstOrDefault();
            if (next is null) break;
            _running++;
            lock (next) // 与 Pause/Cancel 同款嵌套锁序（_gate → task），状态翻转原子于泵
            {
                next.State = DownloadTaskState.Connecting;
                next.PauseRequested = false;
                next.Cancellation = new CancellationTokenSource();
            }
            var cts = next.Cancellation;
            next.RunTask = Task.Run(() => RunAsync(next, cts));
            started.Add(next);
        }
        return started;
    }

    private async Task RunAsync(DownloadTask task, CancellationTokenSource ownedCts)
    {
        var token = ownedCts.Token;
        DownloadOptions options = task.Options;
        if (task.ForceFreshOnRetry)
        {
            options = task.Options with { AllowResume = false };
            task.ForceFreshOnRetry = false;
        }
        try
        {
            var engine = _engineFactory();
            var result = await engine.DownloadAsync(options, progress =>
            {
                ApplyProgress(task, progress);
                return Task.CompletedTask;
            }, token).ConfigureAwait(false);
            lock (task)
            {
                task.TotalBytes = result.TotalBytes;
                task.DownloadedBytes = result.DownloadedBytes;
                if (result.Completed)
                {
                    task.State = DownloadTaskState.Completed;
                    task.FilePath = result.FilePath;
                    task.Message = result.FilePath is null ? "已完成" : $"已保存到：{result.FilePath}";
                }
                else
                {
                    // 引擎对候选耗尽/校验失败等以失败结果返回（不抛异常），这里统一落终态
                    task.State = DownloadTaskState.Failed;
                    task.Error = result.Error ?? "下载失败。";
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (task)
            {
                task.State = task.PauseRequested ? DownloadTaskState.Paused : DownloadTaskState.Cancelled;
                task.Message = task.PauseRequested ? "已暂停，可继续（保留续传记录）" : "已取消（保留续传记录）";
            }
        }
        catch (Exception error)
        {
            lock (task)
            {
                task.State = DownloadTaskState.Failed;
                task.Error = error.Message;
                task.Message = "";
            }
        }
        finally
        {
            List<DownloadTask> started;
            lock (_gate)
            {
                _running--;
                // 归属检查只守"不清空新一轮的取消能力"；旧 CTS 自身无条件释放
                // （被覆盖说明新一轮已接管，旧 CTS 已无用；Pause/Cancel 侧另有 ObjectDisposedException 兜底）
                if (ReferenceEquals(task.Cancellation, ownedCts)) task.Cancellation = null;
                ownedCts.Dispose();
                started = PumpLocked();
            }
            TaskChanged?.Invoke(task);
            foreach (var item in started) TaskChanged?.Invoke(item);
        }
    }

    private void ApplyProgress(DownloadTask task, DownloadProgress progress)
    {
        lock (task)
        {
            if (task.State is DownloadTaskState.Completed or DownloadTaskState.Failed
                or DownloadTaskState.Cancelled or DownloadTaskState.Paused) return; // 抑制迟到进度
            if (progress.State == DownloadTaskState.Completed) return;
            // 引擎的"已完成"进度报告先于结果返回（此刻 FilePath 尚未落）：
            // 终态由 RunAsync 统一写入（状态与文件名同拍），避免行先显示"已完成"却仍挂着 URL 名
            task.State = progress.State;
            task.TotalBytes = progress.TotalBytes;
            task.DownloadedBytes = progress.DownloadedBytes;
            task.SpeedBytesPerSec = progress.SpeedBytesPerSec;
            task.ActiveSegments = progress.ActiveSegments;
            if (progress.Message.Length > 0) task.Message = progress.Message;
        }
        TaskChanged?.Invoke(task);
    }
}
