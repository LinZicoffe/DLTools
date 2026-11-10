using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class DownloadSchedulerTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private static async Task WaitUntilAsync(Func<bool> condition, string hint, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("等待超时：" + hint);
            await Task.Delay(20);
        }
    }

    private static DownloadOptions Options(string url, string directory, int connections = 1) => new()
    {
        Url = url,
        DestinationDirectory = directory,
        Connections = connections
    };

    /// <summary>可整体闸住的传输：probe（RangeEnd=0）放行，真正分段/全量请求等待闸门。</summary>
    private sealed class GatedTransport
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously); // Reset 需重建
        public readonly List<string> StartedUrls = [];
        public FakeSegmentServer Server;

        public GatedTransport(FakeSegmentServer server) => Server = server;

        public FakeDownloadTransport Build(bool gateProbe = false) => new(async (request, ct) =>
        {
            lock (StartedUrls)
                if (!StartedUrls.Contains(request.Url)) StartedUrls.Add(request.Url);
            if (gateProbe || request.RangeEnd != 0)
                await _gate.Task.WaitAsync(ct);
            return Server.Serve(request);
        });

        public void Release() => _gate.TrySetResult();

        /// <summary>复位闸门（收尾断言需要再次闸住新任务）。</summary>
        public void Reset() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> SnapshotStarted()
        {
            lock (StartedUrls) return StartedUrls.ToArray();
        }
    }

    // FakeSegmentServer 由 FakeDownloadTransport.cs 提供（共享夹具）。

    private static byte[] TestData(int size, int seed = 7)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static DownloadScheduler CreateScheduler(FakeDownloadTransport transport, int maxConcurrent = 2,
        Action? onEngineCreated = null)
    {
        var scheduler = new DownloadScheduler(() =>
        {
            onEngineCreated?.Invoke();
            return new DownloadEngine(transport, retryDelay: (_, _) => Task.CompletedTask);
        }, maxConcurrent);
        return scheduler;
    }

    [Fact]
    public async Task Enqueue_BeyondMaxConcurrent_KeepsExtrasQueuedThenStartsInFifoOrder()
    {
        var data = TestData(3 * 1024 * 1024); // 分段路径（连接数 1 → 1 worker/任务）
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var scheduler = CreateScheduler(gated.Build(), maxConcurrent: 2);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        scheduler.Enqueue(Options("http://8.8.8.8/a.bin", destination));
        scheduler.Enqueue(Options("http://8.8.8.8/b.bin", destination));
        scheduler.Enqueue(Options("http://8.8.8.8/c.bin", destination));

        await WaitUntilAsync(() =>
        {
            var urls = gated.SnapshotStarted();
            return urls.Count == 2;
        }, "前两个任务应占满槽位");
        Assert.Equal(2, scheduler.Tasks.Count(task => task.State
            is DownloadTaskState.Connecting or DownloadTaskState.Downloading
            or DownloadTaskState.Verifying or DownloadTaskState.Committing));
        Assert.Equal(1, scheduler.Tasks.Count(task => task.State == DownloadTaskState.Queued));
        Assert.Contains("http://8.8.8.8/c.bin", scheduler.Tasks.Single(task => task.State == DownloadTaskState.Queued).Options.Url);

        gated.Release();
        await WaitUntilAsync(() => scheduler.Tasks.All(task => task.State == DownloadTaskState.Completed), "全部完成");
        // 第三个任务必须在前两个释放槽位后才启动（FIFO）
        Assert.Equal("http://8.8.8.8/c.bin", gated.SnapshotStarted()[2]);
    }

    [Fact]
    public async Task Pause_WhileConnecting_EntersPausedKeepsJournalThenResumeCompletes()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var scheduler = CreateScheduler(gated.Build(), maxConcurrent: 1); // 只闸分段请求
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var task = scheduler.Enqueue(Options("http://8.8.8.8/fw.bin", destination));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == task.Id).State
            is DownloadTaskState.Connecting or DownloadTaskState.Downloading, "任务应进入运行态");
        Assert.True(scheduler.Pause(task.Id));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == task.Id).State == DownloadTaskState.Paused,
            "暂停应落为 Paused");
        Assert.True(DownloadJournal.HasJournal(Path.Combine(destination, "fw.bin"))); // 续传记录保留

        gated.Release();
        Assert.True(scheduler.Resume(task.Id));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == task.Id).State == DownloadTaskState.Completed,
            "继续后应完成");
        Assert.True(File.Exists(Path.Combine(destination, "fw.bin")));
        Assert.False(scheduler.HasActiveWork);
    }

    [Fact]
    public async Task Cancel_QueuedTask_MarksCancelledAndNeverStartsEngine()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var engines = 0;
        var scheduler = CreateScheduler(gated.Build(gateProbe: true), maxConcurrent: 1, onEngineCreated: () => Interlocked.Increment(ref engines));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var first = scheduler.Enqueue(Options("http://8.8.8.8/first.bin", destination));
        await WaitUntilAsync(() => engines == 1, "第一个任务应已启动引擎");
        var second = scheduler.Enqueue(Options("http://8.8.8.8/second.bin", destination));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == second.Id).State == DownloadTaskState.Queued,
            "第二个任务应排队");

        Assert.True(scheduler.Cancel(second.Id));
        Assert.Equal(DownloadTaskState.Cancelled, scheduler.Tasks.Single(item => item.Id == second.Id).State);
        gated.Release();
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == first.Id).State == DownloadTaskState.Completed,
            "第一个任务应完成");
        Assert.Equal(1, engines); // 被取消的排队任务从未创建引擎
    }

    [Fact]
    public async Task Retry_FailedTask_RerunsAndSucceeds()
    {
        var data = TestData(3 * 1024 * 1024);
        var failing = true;
        var server = new FakeSegmentServer(data)
        {
            Override = _ => failing ? FakeDownloadTransport.Status(404) : null
        };
        var scheduler = CreateScheduler(new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request))));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var task = scheduler.Enqueue(Options("http://8.8.8.8/fw.bin", destination));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == task.Id).State == DownloadTaskState.Failed,
            "首跑应 404 失败");
        Assert.Contains("404", scheduler.Tasks.Single(item => item.Id == task.Id).Error);

        failing = false;
        Assert.True(scheduler.Retry(task.Id));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == task.Id).State == DownloadTaskState.Completed,
            "重试应成功");
    }

    [Fact]
    public async Task Retry_AfterCorruptJournalError_RestartsFreshAndSucceeds()
    {
        var data = TestData(3 * 1024 * 1024);
        var server = new FakeSegmentServer(data);
        var scheduler = CreateScheduler(new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request))));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        // 预置损坏的续传记录：首次运行应失败并提示"损坏"，Retry 应放弃续传改为全新下载
        var finalPath = Path.Combine(destination, "fw.bin");
        File.WriteAllText(DownloadJournal.JournalPathFor(finalPath), "{ broken");
        File.WriteAllText(DownloadJournal.TempPathFor(finalPath), "partial");

        var task = scheduler.Enqueue(Options("http://8.8.8.8/fw.bin", destination));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == task.Id).State == DownloadTaskState.Failed,
            "损坏记录应导致失败");
        Assert.Contains("损坏", scheduler.Tasks.Single(item => item.Id == task.Id).Error);

        Assert.True(scheduler.Retry(task.Id));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == task.Id).State == DownloadTaskState.Completed,
            "全新重试应成功");
        Assert.Equal(data, await File.ReadAllBytesAsync(finalPath));
        Assert.False(DownloadJournal.HasJournal(finalPath));
    }

    [Fact]
    public async Task Remove_RunningTask_ThrowsAndTerminalTaskIsRemoved()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var scheduler = CreateScheduler(gated.Build(gateProbe: true), maxConcurrent: 1);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var running = scheduler.Enqueue(Options("http://8.8.8.8/fw.bin", destination));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == running.Id).State
            is DownloadTaskState.Connecting or DownloadTaskState.Downloading, "任务应处于运行态");
        var error = Assert.Throws<InvalidOperationException>(() => scheduler.Remove(running.Id));
        Assert.Contains("先暂停或取消", error.Message);

        gated.Release();
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == running.Id).State == DownloadTaskState.Completed,
            "任务应完成");
        Assert.True(scheduler.Remove(running.Id));
        Assert.Empty(scheduler.Tasks);
    }

    [Fact]
    public async Task CancelAllAndDrain_PausesRunningAndQueuedTasksAndWaitsForEngines()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var scheduler = CreateScheduler(gated.Build(gateProbe: true), maxConcurrent: 1);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var first = scheduler.Enqueue(Options("http://8.8.8.8/a.bin", destination));
        var second = scheduler.Enqueue(Options("http://8.8.8.8/b.bin", destination));
        await WaitUntilAsync(() => scheduler.Tasks.Single(item => item.Id == first.Id).State == DownloadTaskState.Connecting,
            "第一个任务应已启动");

        await scheduler.CancelAllAndDrainAsync(); // 闸门未放行：取消令牌应解除阻塞并排空
        Assert.Equal(DownloadTaskState.Paused, scheduler.Tasks.Single(item => item.Id == first.Id).State);
        Assert.Equal(DownloadTaskState.Paused, scheduler.Tasks.Single(item => item.Id == second.Id).State);
        Assert.False(scheduler.HasActiveWork);
    }

    [Fact]
    public async Task UpdateSettings_RaisingMaxConcurrent_StartsQueuedTasks()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var scheduler = CreateScheduler(gated.Build(), maxConcurrent: 1);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        scheduler.Enqueue(Options("http://8.8.8.8/a.bin", destination));
        scheduler.Enqueue(Options("http://8.8.8.8/b.bin", destination));
        await WaitUntilAsync(() => gated.SnapshotStarted().Count == 1, "第一个任务占满 1 槽");

        scheduler.UpdateSettings(new DownloadSettings { MaxConcurrentTasks = 2 });
        await WaitUntilAsync(() => gated.SnapshotStarted().Count == 2, "提升并发后第二个任务应启动");
        gated.Release();
        await WaitUntilAsync(() => scheduler.Tasks.All(task => task.State == DownloadTaskState.Completed), "全部完成");
    }

    [Fact]
    public async Task UpdateSettings_LoweringMaxConcurrent_BlocksNewStartsUntilSlotFrees()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var scheduler = CreateScheduler(gated.Build(), maxConcurrent: 2);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        scheduler.Enqueue(Options("http://8.8.8.8/a.bin", destination));
        scheduler.Enqueue(Options("http://8.8.8.8/b.bin", destination));
        await WaitUntilAsync(() => gated.SnapshotStarted().Count == 2, "两个任务占满 2 槽");

        scheduler.UpdateSettings(new DownloadSettings { MaxConcurrentTasks = 1 });
        var third = scheduler.Enqueue(Options("http://8.8.8.8/c.bin", destination));
        await Task.Delay(150);
        Assert.Equal(DownloadTaskState.Queued, third.State); // 调小并发后新任务不再启动

        gated.Release();
        await WaitUntilAsync(() => third.State == DownloadTaskState.Completed, "槽位释放后第三个任务启动并完成");
        await WaitUntilAsync(() => scheduler.Tasks.All(task => task.State == DownloadTaskState.Completed), "全部完成");
    }

    [Fact]
    public void UpdateSettings_WiresGlobalRateLimiterForNewTasks()
    {
        var scheduler = new DownloadScheduler(); // 默认引擎工厂（不真正建任务，不触网）

        scheduler.UpdateSettings(new DownloadSettings { MaxRateKB = 64 });
        Assert.NotNull(scheduler.GlobalRateLimiter); // 全局限速不再是死配置

        scheduler.UpdateSettings(new DownloadSettings { MaxRateKB = 0 });
        Assert.Null(scheduler.GlobalRateLimiter); // 0 = 不限速
    }

    [Fact]
    public async Task EnqueueStartPaused_DoesNotCreateEngineAndStaysPaused()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var engines = 0;
        var scheduler = CreateScheduler(gated.Build(), maxConcurrent: 2, onEngineCreated: () => Interlocked.Increment(ref engines));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var task = scheduler.Enqueue(new DownloadOptions
        {
            Url = "http://8.8.8.8/a.bin", DestinationDirectory = destination, StartPaused = true
        });
        await Task.Delay(150);
        Assert.Equal(DownloadTaskState.Paused, task.State);
        Assert.Equal(0, engines); // 暂停入队不占槽、不建引擎
    }

    [Fact]
    public async Task Remove_QueuedTask_RemovesWithoutStartingEngine()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var engines = 0;
        var scheduler = CreateScheduler(gated.Build(), maxConcurrent: 1,
            onEngineCreated: () => Interlocked.Increment(ref engines));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        scheduler.Enqueue(Options("http://8.8.8.8/a.bin", destination));
        var queued = scheduler.Enqueue(Options("http://8.8.8.8/b.bin", destination));
        await WaitUntilAsync(() => engines == 1, "首个任务启动");
        Assert.Equal(DownloadTaskState.Queued, queued.State);

        Assert.True(scheduler.Remove(queued.Id));
        Assert.DoesNotContain(queued, scheduler.Tasks);
        gated.Release();
        await WaitUntilAsync(() => scheduler.Tasks.All(task => task.State == DownloadTaskState.Completed), "首个任务完成");
        Assert.Equal(1, engines); // 被移除的排队任务从未启动
    }

    [Fact]
    public async Task Pause_RunningTask_ShowsPausingFeedbackThenLandsPaused()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var scheduler = CreateScheduler(gated.Build(), maxConcurrent: 1);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var task = scheduler.Enqueue(Options("http://8.8.8.8/a.bin", destination));
        // 闸住时段无数据回流，状态停在 Connecting（首个数据报文后才转 Downloading）
        await WaitUntilAsync(() => task.State is DownloadTaskState.Connecting or DownloadTaskState.Downloading,
            "任务进入运行态");

        Assert.True(scheduler.Pause(task.Id));
        Assert.Contains("正在暂停", task.Message); // 立即反馈，而非静默等待引擎返回

        gated.Release();
        await WaitUntilAsync(() => task.State == DownloadTaskState.Paused, "引擎返回后落 Paused");
    }

    [Fact]
    public async Task RetryAfterSha256Mismatch_DownloadsFreshInsteadOfReverifyingSameTemp()
    {
        var data = TestData(3 * 1024 * 1024);
        var transport = new FakeDownloadTransport((request, _) =>
            Task.FromResult(new FakeSegmentServer(data).Serve(request)));
        var scheduler = CreateScheduler(transport, maxConcurrent: 1);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        int BodyRangeRequests() => transport.SnapshotRequests()
            .Count(request => request.RangeStart is not null && request.RangeEnd != 0);

        var task = scheduler.Enqueue(new DownloadOptions
        {
            Url = "http://8.8.8.8/a.bin", DestinationDirectory = destination,
            ExpectedSha256 = new string('f', 64) // 永不匹配
        });
        await WaitUntilAsync(() => task.State == DownloadTaskState.Failed, "首次下载校验失败");
        Assert.Contains("校验不通过", task.Error);
        var firstRunRequests = BodyRangeRequests();
        Assert.True(firstRunRequests > 0);

        Assert.True(scheduler.Retry(task.Id));
        await WaitUntilAsync(() => task.State == DownloadTaskState.Failed, "重试仍应校验失败");
        Assert.True(BodyRangeRequests() > firstRunRequests, // 全新重下，而不是复验同一份坏数据
            $"重试后应重新拉取分段：{BodyRangeRequests()} <= {firstRunRequests}");
    }

    /// <summary>并发操作同一任务时的双引擎/状态错乱检测器：同一 URL 的在途请求超过 1 即记违规。</summary>
    private sealed class ConcurrencyGuardTransport : IDownloadTransport
    {
        private readonly FakeDownloadTransport _inner;
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _inflight = [];
        public readonly List<string> Violations = [];

        public ConcurrencyGuardTransport(FakeDownloadTransport inner) => _inner = inner;

        public async Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _inflight[request.Url] = _inflight.GetValueOrDefault(request.Url) + 1;
                if (_inflight[request.Url] > 1)
                    Violations.Add($"并发引擎同时请求 {request.Url}");
            }
            try { return await _inner.SendAsync(request, cancellationToken); }
            finally
            {
                lock (_gate) _inflight[request.Url]--;
            }
        }
    }

    [Fact]
    public async Task ConcurrentPauseResumeCancel_NeverRunsTwoEnginesForSameTask()
    {
        var data = TestData(3 * 1024 * 1024);
        var gated = new GatedTransport(new FakeSegmentServer(data));
        var guard = new ConcurrencyGuardTransport(gated.Build());
        var scheduler = new DownloadScheduler(() =>
            new DownloadEngine(guard, retryDelay: (_, _) => Task.CompletedTask), 2);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var first = scheduler.Enqueue(Options("http://8.8.8.8/a.bin", destination));
        var second = scheduler.Enqueue(Options("http://8.8.8.8/b.bin", destination));
        await WaitUntilAsync(() => gated.SnapshotStarted().Count == 2, "两任务同时下载（闸住）");

        // 确定性操作序列（取模轮转，非随机）：两个线程对两任务交替暂停/继续/取消/重试
        var churn = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                var task = i % 2 == 0 ? first : second;
                switch (i % 4)
                {
                    case 0: scheduler.Pause(task.Id); break;
                    case 1: scheduler.Resume(task.Id); break;
                    case 2: scheduler.Cancel(task.Id); break;
                    default: scheduler.Retry(task.Id); break;
                }
            }
        });
        var churn2 = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                var task = i % 2 == 0 ? second : first;
                scheduler.Pause(task.Id);
                scheduler.Resume(task.Id);
            }
        });
        await Task.WhenAll(churn, churn2);
        gated.Release();
        await scheduler.CancelAllAndDrainAsync();

        Assert.Empty(guard.Violations); // 任意时刻同一任务至多一个引擎在跑
        Assert.All(scheduler.Tasks, task =>
            Assert.True(task.State is DownloadTaskState.Completed or DownloadTaskState.Paused
                or DownloadTaskState.Failed or DownloadTaskState.Cancelled,
                $"终态非法：{task.State}"));
        Assert.All(scheduler.Tasks, task =>
            Assert.True(task.Error is null || (!task.Error.Contains("另一个进程") && !task.Error.Contains("ObjectDisposed")),
                $"并发操作产生坏状态：{task.Error}"));

        // 槽位核算自洽：排空后再入队两个任务必须都能启动（若 _running 被搅动搞坏，新任务会滞留 Queued）
        gated.Reset();
        var lateA = scheduler.Enqueue(Options("http://8.8.8.8/late-a.bin", destination));
        var lateB = scheduler.Enqueue(Options("http://8.8.8.8/late-b.bin", destination));
        await WaitUntilAsync(() => gated.SnapshotStarted().Any(url => url.Contains("late-a"))
            && gated.SnapshotStarted().Any(url => url.Contains("late-b")), "排空后槽位应全部可用");
        gated.Release();
        await WaitUntilAsync(() => lateA.State == DownloadTaskState.Completed
            && lateB.State == DownloadTaskState.Completed, "收尾任务完成");
    }

    public void Dispose() => _workspace.Dispose();
}
