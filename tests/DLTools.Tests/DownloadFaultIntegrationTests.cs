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