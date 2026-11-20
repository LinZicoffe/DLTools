using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>故障注入下的引擎级真实 HTTP 验证：掐断重试、看门狗、限连接退避、ETag 变更降级。
/// 全部集成门控：DLTOOLS_DOWNLOAD_INTEGRATION=1。</summary>
public class DownloadFaultIntegrationTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private string CreateDestination()
    {
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        return destination;
    }

    private static string Sha256Of(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [DownloadIntegrationFact]
    public async Task RealHttp_TruncatedEverySecondRequest_RetriesAndCompletes()
    {
        var data = FaultInjectionServer.GenerateData(5 * 1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            DropAfterBytes = 1024 * 1024, // 2MiB 分段写到一半被掐断
            DropEvery = 2
        });
        using var transport = new DownloadHttpTransport();

        var result = await new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = $"{server.BaseUrl}/fw.bin",
            DestinationDirectory = CreateDestination(),
            Connections = 4,
            ExpectedSha256 = Sha256Of(data)
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        var truncated = server.SnapshotLog().Count(entry =>
            entry.Status == 206 && entry.BytesSent < entry.End - entry.Start + 1);
        Assert.True(truncated > 0, "应至少出现一次截断响应");
        TestEvidence.Save("掐断重试", new
        {
            truncatedResponses = truncated,
            dataRequests = server.DataRequests,
            sha256 = Sha256Of(data)
        });
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_StalledConnection_WatchdogRetriesAndCompletes()
    {
        var data = FaultInjectionServer.GenerateData(5 * 1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            StallAtBytes = 64 * 1024,
            StallEvery = 3
        });
        using var transport = new DownloadHttpTransport();

        var result = await new DownloadEngine(transport, stallTimeout: TimeSpan.FromSeconds(1)).DownloadAsync(new DownloadOptions
        {
            Url = $"{server.BaseUrl}/fw.bin",
            DestinationDirectory = CreateDestination(),
            Connections = 2,
            ExpectedSha256 = Sha256Of(data)
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        server.Dispose(); // 卡死 handler 的日志在收尾时才落盘，先排空再断言（Dispose 幂等）
        var stalledResponses = server.SnapshotLog().Count(entry => entry.BytesSent == 64 * 1024 && entry.Status == 206);
        Assert.True(stalledResponses > 0, "应至少出现一次卡死响应（看门狗路径未被触发则用例失去意义）");
        TestEvidence.Save("看门狗卡死重试", new
        {
            dataRequests = server.DataRequests,
            stalledResponses
        });
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_MaxConns503_CompletesWithBackoff()
    {
        var data = FaultInjectionServer.GenerateData(5 * 1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            MaxConcurrent = 2,
            RetryAfterSeconds = 1
        });
        using var transport = new DownloadHttpTransport();

        var result = await new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = $"{server.BaseUrl}/fw.bin",
            DestinationDirectory = CreateDestination(),
            Connections = 4,
            ExpectedSha256 = Sha256Of(data)
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.Contains(server.SnapshotLog(), entry => entry.Status == 503);
        TestEvidence.Save("限连接退避", new
        {
            refusedResponses = server.SnapshotLog().Count(entry => entry.Status == 503),
            dataRequests = server.DataRequests
        });
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_CombinedFaults_RefusalTruncationTrickle_Completes()
    {
        // F07/F09/F10 协同端到端：限连接 503 退避 + 每第 2 个数据请求掐断 + 每第 3 个滴流。
        // 滴流只命中部分请求（RateSelector）：收数方多数请求全速，整体快速收尾，
        // 被拒 worker 在累计 5 次零进度止损前就能经 AssignOrWait 看到完成而退出。
        var data = FaultInjectionServer.GenerateData(5 * 1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            MaxConcurrent = 2,
            RetryAfterSeconds = 1,
            DropAfterBytes = 1024 * 1024,
            DropEvery = 2,
            RateBytesPerSecond = 256 * 1024,
            RateSelector = seq => seq % 3 == 0
        });
        using var transport = new DownloadHttpTransport();
        var shedCount = 0;

        var result = await new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = $"{server.BaseUrl}/fw.bin",
            DestinationDirectory = CreateDestination(),
            Connections = 4,
            ExpectedSha256 = Sha256Of(data)
        }, update =>
        {
            if (update.Message.Contains("慢连接已丢弃")) Interlocked.Increment(ref shedCount);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        var log = server.SnapshotLog();
        Assert.Contains(log, entry => entry.Status == 503);
        Assert.Contains(log, entry => entry.Status == 206 && entry.BytesSent < entry.End - entry.Start + 1);
        // 滴流腿真实生效（终审 C2）：配速下 ElapsedMs ≥ BytesSent/速率，128KiB@256KiB/s ≈ 500ms，取 400ms 保守下限
        var trickled = log.Where(entry => entry.Seq % 3 == 0 && entry.Status == 206 && entry.BytesSent > 0).ToArray();
        Assert.Contains(trickled, entry => entry.BytesSent >= 128 * 1024 && entry.ElapsedMs >= 400);
        TestEvidence.Save("组合故障协同", new
        {
            refusedResponses = log.Count(entry => entry.Status == 503),
            truncatedResponses = log.Count(entry => entry.Status == 206 && entry.BytesSent < entry.End - entry.Start + 1),
            trickledResponses = trickled.Length,
            shedCount, // F10 参与可观测（终审 A2）：本场景滴流占比低，shed 可能为 0，不硬断言
            dataRequests = server.DataRequests
        });
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_EtagChangedMidDownload_FallsBackToSingleStreamWithCorrectContent()
    {
        var data = FaultInjectionServer.GenerateData(5 * 1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile { EtagChangeAfterRequest = 1 });
        using var transport = new DownloadHttpTransport();

        var result = await new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = $"{server.BaseUrl}/fw.bin",
            DestinationDirectory = CreateDestination(),
            Connections = 4,
            ExpectedSha256 = Sha256Of(data)
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        // 首个数据请求之后 ETag 变更：后续 If-Range 请求收到 200 全量 → RangeIgnored 降级单流
        Assert.Contains(server.SnapshotLog(), entry => entry.Seq > 0 && entry.Status == 200);
        TestEvidence.Save("ETag变更降级", new
        {
            fullBodyResponses = server.SnapshotLog().Count(entry => entry.Seq > 0 && entry.Status == 200),
            dataRequests = server.DataRequests
        });
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_MaxConns1_SchedulerLearnsCapAndSecondRunStaysWithinIt()
    {
        var data = FaultInjectionServer.GenerateData(5 * 1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            MaxConcurrent = 1,
            RetryAfterSeconds = 1,
            RateBytesPerSecond = 1024 * 1024 // 拖慢收数，保证拒绝发生时确有连接正在收数
        });
        var scheduler = new DownloadScheduler(); // 默认工厂：真实传输 + 共享 HostLimits
        var url = $"{server.BaseUrl}/fw.bin";
        var firstDestination = Path.Combine(_workspace.Root, "run1");
        var secondDestination = Path.Combine(_workspace.Root, "run2");
        Directory.CreateDirectory(firstDestination);
        Directory.CreateDirectory(secondDestination);

        var first = scheduler.Enqueue(new DownloadOptions
        {
            Url = url, DestinationDirectory = firstDestination, Connections = 4, ExpectedSha256 = Sha256Of(data)
        });
        await WaitCompletedAsync(scheduler, first.Id);
        Assert.Equal(data, await File.ReadAllBytesAsync(first.FilePath!));
        Assert.Contains(server.SnapshotLog(), entry => entry.Status == 503);
        Assert.Equal(1, scheduler.HostLimits.GetCap(url)); // 第一轮学到上限 1
        var firstRunPeak = server.PeakDataConcurrent;

        server.ResetPeakDataConcurrent();
        var second = scheduler.Enqueue(new DownloadOptions
        {
            Url = url, DestinationDirectory = secondDestination, Connections = 4, ExpectedSha256 = Sha256Of(data)
        });
        await WaitCompletedAsync(scheduler, second.Id);
        Assert.Equal(data, await File.ReadAllBytesAsync(second.FilePath!));
        Assert.Equal(1, server.PeakDataConcurrent); // 第二轮开局即收敛到已学上限，不再撞 503
        TestEvidence.Save("主机上限自学习", new
        {
            learnedCap = scheduler.HostLimits.GetCap(url),
            firstRunPeak,
            secondRunPeak = server.PeakDataConcurrent,
            refusedResponses = server.SnapshotLog().Count(entry => entry.Status == 503)
        });
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_SlowConnectionShed_CompletesFasterThanNoShed()
    {
        const long MiB = 1024 * 1024;
        const long KiB = 1024;
        var data = FaultInjectionServer.GenerateData(16 * MiB);
        // 初始 4 段（对半劈在首块到达前完成，边界确定）：[0,4MiB)/[4MiB,8MiB)/[8MiB,12MiB)/[12MiB,16MiB)。
        // 前 4 个数据请求全部限速：4MiB 段 28KiB/s 滴流（首块 ~2.29s，单样本即过 2s 成熟线且留截断裕量；
        // 放任不管需 ~146s）；[8MiB,12MiB) 段 256KiB/s 跑 16s、其余 128KiB/s 跑 32s——保持全员忙碌，
        // 无空闲 worker 劈救慢段。seq≥5（shed 重连/劈救请求）不限速。
        // 阈值裕量：各区间收数均不足 256KiB → 最佳单连接不成立，基准=链路合计/4≈135KiB/s，
        // 4×滴速=112KiB/s < 基准。2s 节流 journal FlushToDisk 可能连续触发 500ms 磁盘免判，
        // 并行测试负载下 shed 最迟 ~6s 生效，仍远早于 noShed 的 16s+ 劈救点（16MiB 撑开 A/B 边际）。
        var profile = new FaultProfile
        {
            RateBytesPerSecondByRequest = (seq, start, end) => seq > 4 ? 0
                : start == 4 * MiB && end == 8 * MiB - 1 ? 28 * KiB
                : start == 8 * MiB ? 256 * KiB
                : 128 * KiB
        };

        async Task<(long ElapsedMs, int ShedCount, string Log)> RunAsync(bool shedEnabled, string folder)
        {
            using var server = new FaultInjectionServer(data, profile);
            using var transport = new DownloadHttpTransport();
            var destination = Path.Combine(_workspace.Root, folder);
            Directory.CreateDirectory(destination);
            var shedCount = 0;
            var shedTimes = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            var result = await new DownloadEngine(transport, shedEnabled: shedEnabled).DownloadAsync(new DownloadOptions
            {
                Url = $"{server.BaseUrl}/fw.bin",
                DestinationDirectory = destination,
                Connections = 4,
                ExpectedSha256 = Sha256Of(data)
            }, update =>
            {
                if (update.Message.Contains("慢连接已丢弃"))
                {
                    Interlocked.Increment(ref shedCount);
                    lock (shedTimes) shedTimes.Add($"{stopwatch.ElapsedMilliseconds}:{update.Message}");
                }
                return Task.CompletedTask;
            }, CancellationToken.None);
            stopwatch.Stop();
            Assert.True(result.Completed, result.Error);
            Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
            var log = string.Join("; ", server.SnapshotLog().Select(e =>
                $"#{e.Seq}[{e.Start / KiB}K-{e.End / KiB}K]{e.Status} {e.BytesSent / KiB}K/{e.ElapsedMs}ms"));
            string times;
            lock (shedTimes) times = string.Join(",", shedTimes);
            return (stopwatch.ElapsedMilliseconds, Volatile.Read(ref shedCount), $"shed@[{times}]ms {log}");
        }

        var (shedMs, shedCount, shedLog) = await RunAsync(true, "shed");
        var (noShedMs, noShedCount, noShedLog) = await RunAsync(false, "noshed");
        Assert.True(shedCount >= 1, "开启 shed 应至少丢弃一次慢连接");
        Assert.Equal(0, noShedCount);
        Assert.True(shedMs < noShedMs * 0.7,
            $"shed 应显著更快：开启 {shedMs}ms vs 关闭 {noShedMs}ms\nshed日志: {shedLog}\nnoShed日志: {noShedLog}");
        TestEvidence.Save("慢连接shed加速", new
        {
            shedElapsedMs = shedMs,
            noShedElapsedMs = noShedMs,
            shedCount,
            ratio = Math.Round((double)shedMs / noShedMs, 3)
        });
    }

    private static async Task WaitCompletedAsync(DownloadScheduler scheduler, Guid id)
    {
        var deadline = Environment.TickCount64 + 90_000;
        while (true)
        {
            var task = scheduler.Tasks.Single(item => item.Id == id);
            if (task.State == DownloadTaskState.Completed) return;
            if (task.State is DownloadTaskState.Failed or DownloadTaskState.Cancelled)
                throw new InvalidOperationException($"任务未成功：{task.State} {task.Error}");
            if (Environment.TickCount64 > deadline) throw new TimeoutException("等待下载完成超时");
            await Task.Delay(50);
        }
    }

    public void Dispose() => _workspace.Dispose();
}