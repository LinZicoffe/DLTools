using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>本机回环 HttpListener 上的真实 HTTP 传输验证：多段并发、Range 忽略降级、跨引擎实例续传。
/// 证据 JSON 写入 TestResults（可用 DLTOOLS_DOWNLOAD_EVIDENCE 重定向到已存在的绝对目录），不入 Git。</summary>
public class DownloadIntegrationTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    /// <summary>确定性负载填充（非加密用途；仅保证用例可复现、SHA-256 可断言）。</summary>
    private static byte[] TestData(int size, byte seed = 99)
    {
        var data = new byte[size];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 31 + seed);
        return data;
    }

    private static byte[] Sha256Of(byte[] data) => SHA256.HashData(data);

    /// <summary>证据目录：优先环境变量（必须是绝对路径且不含 ..），否则落在输出目录的 TestResults 下。</summary>
    private static string ResolveEvidenceDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("DLTOOLS_DOWNLOAD_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(configured)
            && !configured.Contains("..", StringComparison.Ordinal)
            && Path.IsPathRooted(configured))
            return Path.GetFullPath(configured);
        return Path.Combine(AppContext.BaseDirectory, "TestResults");
    }

    private sealed class RangeServer : IDisposable
    {
        private HttpListener _listener = new(); // 端口被占时重建重试，不能 readonly
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly object _gate = new();

        public byte[] Data;
        public bool IgnoreRange; // true：对任何请求回 200 全量（模拟服务器忽略 Range）
        public Func<HttpListenerRequest, TimeSpan> Delay = _ => TimeSpan.Zero;
        public readonly List<(long Start, long End)> Ranges = [];
        public int Concurrent;
        public int PeakConcurrent;
        public int Requests;

        public RangeServer(byte[] data)
        {
            Data = data;
            // 端口偶发占用时重抽（最多 3 次），避免集成测试因端口冲突闪挂
            int port;
            for (var attempt = 0; ; attempt++)
            {
                port = RandomNumberGenerator.GetInt32(20000, 40000);
                _listener.Prefixes.Add($"http://localhost:{port}/");
                try
                {
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 2)
                {
                    _listener.Close();
                    _listener = new HttpListener();
                }
            }
            BaseUrl = $"http://localhost:{port}";
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync().ConfigureAwait(false);
                        _ = Task.Run(() => Handle(context), _stop.Token);
                    }
                }
                catch (Exception error) when (error is ObjectDisposedException or HttpListenerException) { }
            });
        }

        public string BaseUrl { get; }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                lock (_gate) Requests++;
                var peak = Interlocked.Increment(ref Concurrent);
                lock (_gate) PeakConcurrent = Math.Max(PeakConcurrent, peak);
                try
                {
                    var wait = Delay(context.Request);
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    var range = context.Request.Headers["Range"];
                    long start = 0;
                    long end = Data.Length - 1;
                    var isProbe = range == "bytes=0-0";
                    var partial = !IgnoreRange && range is not null
                        && range.StartsWith("bytes=", StringComparison.Ordinal)
                        && long.TryParse(range["bytes=".Length..].Split('-')[0], out start);
                    if (partial)
                    {
                        var tail = range!.Split('-');
                        if (tail.Length > 1 && long.TryParse(tail[1], out var parsedEnd)) end = parsedEnd;
                        if (!isProbe) lock (_gate) Ranges.Add((start, end));
                        context.Response.StatusCode = 206;
                        context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{Data.Length}";
                    }
                    else
                    {
                        lock (_gate) Ranges.Add((0L, Data.Length - 1));
                        context.Response.StatusCode = 200;
                        start = 0;
                        end = Data.Length - 1;
                    }
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength64 = end - start + 1;
                    context.Response.OutputStream.Write(Data, (int)start, (int)(end - start + 1));
                }
                finally
                {
                    Interlocked.Decrement(ref Concurrent);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
            catch (Exception)
            {
                try { context.Response.Abort(); } catch { /* 客户端取消导致的中断 */ }
            }
        }

        public IReadOnlyList<(long Start, long End)> SnapshotRanges()
        {
            lock (_gate) return Ranges.ToArray();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* 接受循环退出 */ }
        }
    }

    private static void SaveEvidence(string name, object payload)
    {
        var directory = ResolveEvidenceDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"高速下载-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    [DownloadIntegrationFact]
    public async Task RealHttp_MultiSegmentDownloadsMergeAndVerifySha256()
    {
        var data = TestData(5 * 1024 * 1024);
        var expectedSha = Convert.ToHexString(Sha256Of(data)).ToLowerInvariant();
        using var server = new RangeServer(data)
        {
            Delay = _ => TimeSpan.FromMilliseconds(30) // 让多个分段确实同时在途
        };
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
        SaveEvidence("多段并发", new
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
        using var server = new RangeServer(data) { IgnoreRange = true };
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
        var segmentSize = 2 * 1024 * 1024; // Plan(5MiB,2) → 3 段（2/2/1 MiB）
        using var server = new RangeServer(data)
        {
            // 起点非 0 的分段拖 10 秒：第一轮让 worker B 挂住、worker A 完成首段后被取消
            Delay = request => request.Headers["Range"] is { } range
                && range.StartsWith("bytes=", StringComparison.Ordinal)
                && !range.StartsWith("bytes=0-", StringComparison.Ordinal)
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.Zero
        };
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
        Assert.Equal(segmentSize, resumedBytes); // 首段完整、其余未动
        var firstRunRangeCount = server.SnapshotRanges().Count;

        server.Delay = _ => TimeSpan.Zero; // 第二轮不再拖慢
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
        SaveEvidence("跨实例续传", new
        {
            resumedBytesBeforeSecondRun = resumedBytes,
            secondRunRanges = secondRunRanges.Select(range => new { start = range.Start, end = range.End })
        });
    }

    public void Dispose() => _workspace.Dispose();
}
