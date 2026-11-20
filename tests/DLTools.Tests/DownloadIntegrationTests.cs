using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>本机回环 HttpListener 上的真实 HTTP 传输验证：多段并发、Range 忽略降级、跨引擎实例续传。
/// 服务器夹具为 FaultInjectionServer；证据 JSON 写入 TestResults（可用 DLTOOLS_DOWNLOAD_EVIDENCE 重定向），不入 Git。</summary>
public class DownloadIntegrationTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private static byte[] TestData(int size, byte seed = 99) => FaultInjectionServer.GenerateData(size, seed);

    private static byte[] Sha256Of(byte[] data) => SHA256.HashData(data);

    [DownloadIntegrationFact]
    public async Task RealHttp_MultiSegmentDownloadsMergeAndVerifySha256()
    {
        var data = TestData(5 * 1024 * 1024);
        var expectedSha = Convert.ToHexString(Sha256Of(data)).ToLowerInvariant();
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            Latency = TimeSpan.FromMilliseconds(30) // 让多个分段确实同时在途
        });
        using var transport = new DownloadHttpTransport();
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var stopwatch = Stopwatch.StartNew();

        var result = await new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = $"{server.BaseUrl}/fw.bin",
            DestinationDirectory = destination,
            Connections = 4,
            ExpectedSha256 = expectedSha
        }, _ => Task.CompletedTask, CancellationToken.None);

        stopwatch.Stop();
        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        var ranges = server.SnapshotRanges();
        Assert.True(ranges.Count >= 2, $"分段请求数异常：{ranges.Count}");
        Assert.True(server.PeakConcurrent >= 2, $"并发峰值 {server.PeakConcurrent}，多连接未生效");
        Assert.Equal(data.Length - 1, ranges.Max(range => range.End));
        TestEvidence.Save("多段并发", new
        {
            requests = server.Requests,
            peakConcurrent = server.PeakConcurrent,
            ranges = ranges.Select(range => new { start = range.Start, end = range.End }),
            elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            sha256 = expectedSha
        });
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_ServerIgnoringRange_FallsBackToSingleStream()
    {
        var data = TestData(2 * 1024 * 1024);
        using var server = new FaultInjectionServer(data, new FaultProfile { IgnoreRange = true });
        using var transport = new DownloadHttpTransport();
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = $"{server.BaseUrl}/fw.bin",
            DestinationDirectory = destination,
            Connections = 4
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        // 服务器忽略 Range：所有数据请求都是 200 全量
        Assert.All(server.SnapshotRanges(), range => Assert.Equal((0L, data.Length - 1), range));
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_CancelThenNewEngineInstance_ResumesRemainingRangesOnly()
    {
        var data = TestData(5 * 1024 * 1024);
        var segmentSize = 2 * 1024 * 1024; // 取消阈值：首段至少完整交付 2MiB
        using var server = new FaultInjectionServer(data, new FaultProfile
        {
            // 起点非 0 的分段拖 10 秒：第一轮让 worker B 挂住、worker A 完成首段后被取消
            LatencySelector = request => request.Headers["Range"] is { } range
                && range.StartsWith("bytes=", StringComparison.Ordinal)
                && !range.StartsWith("bytes=0-", StringComparison.Ordinal)
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.Zero
        });
        using var transport = new DownloadHttpTransport();
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var finalPath = Path.Combine(destination, "fw.bin");
        var url = $"{server.BaseUrl}/fw.bin";

        using var cancellation = new CancellationTokenSource();
        var firstRun = new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = url,
            DestinationDirectory = destination,
            Connections = 2
        }, progress =>
        {
            if (progress.DownloadedBytes >= segmentSize) cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun);
        Assert.True(DownloadJournal.HasJournal(finalPath), "取消后应保留续传记录");
        var journal = DownloadJournal.TryOpen(finalPath, url);
        Assert.NotNull(journal);
        var resumedBytes = journal!.ReadSegments().Sum(segment => segment.Completed);
        // 动态对半劈下段边界不固定：取消时至少完成 2MiB 阈值、且不可能完成全部
        Assert.InRange(resumedBytes, segmentSize, data.Length - 1);
        var firstRunRangeCount = server.SnapshotRanges().Count;

        server.Profile = new FaultProfile(); // 第二轮不再拖慢
        var result = await new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = url,
            DestinationDirectory = destination,
            Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.True(result.Resumed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        var secondRunRanges = server.SnapshotRanges().Skip(firstRunRangeCount).ToArray();
        Assert.NotEmpty(secondRunRanges);
        Assert.All(secondRunRanges, range => Assert.True(range.Start >= segmentSize,
            $"续传不应重拉已完成分段，实际出现起点 {range.Start}"));
        TestEvidence.Save("跨实例续传", new
        {
            resumedBytesBeforeSecondRun = resumedBytes,
            secondRunRanges = secondRunRanges.Select(range => new { start = range.Start, end = range.End })
        });
    }

    public void Dispose() => _workspace.Dispose();
}
