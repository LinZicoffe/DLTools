using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class DownloadEngineTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    // 固定种子的确定性负载（非加密用途）：保证用例可复现、SHA-256 可断言。
    private static byte[] TestData(int size, int seed = 42)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>在共享 FakeSegmentServer 语义之上记录分段区间与请求数；LastModified/ETag 可覆盖。</summary>
    private sealed class LoggedSegmentServer
    {
        public byte[] Data;
        public Func<TransportRequest, TransportResponse?>? Override;
        public string ETag = "\"v1\"";
        public string LastModified = "";
        public readonly List<(long Start, long End)> Ranges = [];
        public int Requests;

        public LoggedSegmentServer(byte[] data) => Data = data;

        public TransportResponse Serve(TransportRequest request)
        {
            Interlocked.Increment(ref Requests);
            if (Override is { } inject)
            {
                var injected = inject(request);
                if (injected is not null) return injected;
            }
            if (request.RangeStart is { } start)
            {
                var end = request.RangeEnd ?? Data.Length - 1;
                if (start == 0 && end == 0) // 探测请求
                    return FakeDownloadTransport.Partial(Data.Length, 0, 0, [Data[0]],
                        etag: ETag, lastModified: LastModified);
                lock (Ranges) Ranges.Add((start, end));
                return FakeDownloadTransport.Partial(Data.Length, start, end,
                    Data[(int)start..(int)(end + 1)], etag: ETag, lastModified: LastModified);
            }
            lock (Ranges) Ranges.Add((0, Data.Length - 1));
            return FakeDownloadTransport.OkFull(Data.Length, Data, etag: ETag, lastModified: LastModified);
        }

        public IReadOnlyList<(long Start, long End)> SnapshotRanges()
        {
            lock (Ranges) return Ranges.ToArray();
        }
    }

    private static DownloadEngine CreateEngine(IDownloadTransport transport, List<TimeSpan>? delays = null,
        TimeSpan? stall = null, int maxAttempts = 5) =>
        new(transport, stallTimeout: stall ?? TimeSpan.FromSeconds(30), maxAttempts: maxAttempts,
            retryDelay: (delay, _) => { delays?.Add(delay); return Task.CompletedTask; });

    [Fact]
    public async Task MultiSegment_CompletesWithContiguousBytesAndCoverage()
    {
        var data = TestData(3 * 1024 * 1024); // 3 MiB → 2 段（2 MiB + 1 MiB）
        var server = new LoggedSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin",
            DestinationDirectory = destination,
            Connections = 2,
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(3 * 1024 * 1024, result.TotalBytes);
        var saved = await File.ReadAllBytesAsync(result.FilePath!);
        Assert.Equal(data, saved);
        var ranges = server.SnapshotRanges();
        Assert.Equal(2, ranges.Count); // 2 段各一次完成
        Assert.Equal(0L, ranges.Min(range => range.Start));
        Assert.Equal(data.Length - 1, ranges.Max(range => range.End));
        Assert.False(File.Exists(DownloadJournal.JournalPathFor(result.FilePath!)));
        Assert.False(File.Exists(DownloadJournal.TempPathFor(result.FilePath!)));
    }

    [Fact]
    public async Task SmallFile_DownloadsSingleStreamWithoutJournalFiles()
    {
        var data = TestData(512 * 1024); // < 1 MiB → 单流
        var server = new LoggedSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 8
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        var finalPath = Path.Combine(destination, "fw.bin");
        Assert.False(File.Exists(DownloadJournal.JournalPathFor(finalPath)));
    }

    [Fact]
    public async Task ProbeReturns200_FallsBackToSingleStream()
    {
        var data = TestData(3 * 1024 * 1024);
        // 服务器对任何请求都回 200 全量（不支持 Range）
        var transport = new FakeDownloadTransport((_, _) => Task.FromResult(
            FakeDownloadTransport.OkFull(data.Length, data)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 8
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task RangeIgnoredOnSegmentRequest_FallsBackToSingleStreamWithNotice()
    {
        var data = TestData(3 * 1024 * 1024);
        var requests = 0;
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(
            request.RangeStart is 0 && request.RangeEnd is 0
                ? FakeDownloadTransport.Partial(data.Length, 0, 0, [data[0]])   // 探测：假装支持 Range
                : Interlocked.Increment(ref requests) <= 1
                    ? FakeDownloadTransport.OkFull(data.Length, data)            // 首个分段请求：200 → 忽略 Range
                    : FakeDownloadTransport.OkFull(data.Length, data)));        // 降级后的全量请求
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var progress = new List<DownloadProgress>();

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, update => { lock (progress) progress.Add(update); return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        lock (progress) Assert.Contains(progress, update => update.Message.Contains("转为单线程"));
    }

    [Fact]
    public async Task RetryOn503_EventuallySucceedsPerSegmentWithBackoff()
    {
        var data = TestData(3 * 1024 * 1024);
        var failures = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                ? null // 探测正常
                : Interlocked.Increment(ref failures) <= 2
                    ? FakeDownloadTransport.Status(503)
                    : null
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var delays = new List<TimeSpan>();

        var result = await CreateEngine(transport, delays).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 1
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, delays.Count); // 单连接：同一分段连续两次 503 → 两次退避
        Assert.Equal(TimeSpan.FromMilliseconds(500), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(1), delays[1]);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task FourTwentyNineExhausted_MovesToMirrorAndHonoursRetryAfter()
    {
        var data = TestData(3 * 1024 * 1024);
        var primary = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                ? FakeDownloadTransport.Partial(data.Length, 0, 0, [data[0]])
                : FakeDownloadTransport.RateLimited(1) // 主节点：探测可用，但分段一律 429
        };
        var mirror = new LoggedSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(
            request.Url.Contains("mirror") ? mirror.Serve(request) : primary.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var delays = new List<TimeSpan>();

        // 连接数 1：单 worker 顺序尝试，退避次数确定（多 worker 时兄弟止损会提前截断在途退避）
        var result = await CreateEngine(transport, delays, maxAttempts: 2).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin",
            DestinationDirectory = destination,
            Connections = 1,
            MirrorUrls = ["http://8.8.8.8/mirror/fw.bin"]
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.Equal([TimeSpan.FromSeconds(1)], delays); // 重试一次（尊重 Retry-After=1s）后放弃该节点
        Assert.Single(mirror.SnapshotRanges()); // 全部数据来自镜像（连接数 1 → 单段）
    }

    [Fact]
    public async Task Terminal404OnAllCandidates_FailsWithTriedHint()
    {
        var transport = new FakeDownloadTransport((_, _) =>
            Task.FromResult(FakeDownloadTransport.Status(404)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin",
            DestinationDirectory = destination,
            MirrorUrls = ["http://8.8.8.9/fw.bin"]
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.Error);
        Assert.Contains("404", result.Error);
        Assert.Contains("已尝试 2 个地址", result.Error);
    }

    [Fact]
    public async Task StalledSegment_WatchdogRetriesThenSucceeds()
    {
        var data = TestData(3 * 1024 * 1024);
        var stalled = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                ? null
                : Interlocked.Increment(ref stalled) == 1
                    ? new TransportResponse(206, "application/octet-stream", "", "",
                        $"bytes {request.RangeStart}-{request.RangeEnd}/{data.Length}",
                        null, "", null, new NeverEndingStream()) // 首个分段请求永远不给数据
                    : null
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport, stall: TimeSpan.FromMilliseconds(200)).DownloadAsync(
            new DownloadOptions { Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2 },
            _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task TruncatedStream_RetriesSegmentFromCurrentOffset()
    {
        var data = TestData(3 * 1024 * 1024);
        var truncated = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request =>
            {
                if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
                if (Interlocked.Increment(ref truncated) == 1 && request.RangeStart == 0)
                {
                    // 首段只给一半就断流
                    var half = (int)(request.RangeEnd!.Value + 1) / 2;
                    return FakeDownloadTransport.Partial(data.Length, 0, request.RangeEnd.Value, data[..half]);
                }
                return null;
            }
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 1
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        var ranges = server.SnapshotRanges();
        Assert.Contains(ranges, range => range.Start > 0); // 断流后从半程续取
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task Cancel_KeepsJournalForResumeAndSkipsCompletedBytesOnResume()
    {
        var data = TestData(3 * 1024 * 1024); // 2 段：[0,2MiB) [2MiB,3MiB)
        var firstRun = true;
        LoggedSegmentServer BuildServer() => new(data);
        var blockingServer = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                ? null
                : request.RangeStart >= 2 * 1024 * 1024 && firstRun
                    ? new TransportResponse(206, "application/octet-stream", "", "",
                        $"bytes {request.RangeStart}-{request.RangeEnd}/{data.Length}", null, "", null,
                        new BlockingStream())
                    : null
        };
        var transport = new FakeDownloadTransport((request, _) =>
            Task.FromResult(blockingServer.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var finalPath = Path.Combine(destination, "fw.bin");

        using var cancellation = new CancellationTokenSource();
        var engine = CreateEngine(transport);
        var download = engine.DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, update =>
        {
            if (update.DownloadedBytes >= 2 * 1024 * 1024) cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.True(DownloadJournal.HasJournal(finalPath));
        var journal = DownloadJournal.TryOpen(finalPath, "http://8.8.8.8/fw.bin");
        Assert.NotNull(journal);
        Assert.Equal(2 * 1024 * 1024, journal!.ReadSegments()[0].Completed);

        // 第二次运行：正常服务器，只应请求剩余段
        firstRun = false;
        var resumeServer = BuildServer();
        var resumeTransport = new FakeDownloadTransport((request, _) =>
            Task.FromResult(resumeServer.Serve(request)));
        var result = await CreateEngine(resumeTransport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.True(result.Resumed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.All(resumeServer.SnapshotRanges(), range => Assert.True(range.Start >= 2 * 1024 * 1024));
        // 续传请求必须携带 If-Range（探测 ETag "v1"）：资源变更时服务器回 200 触发重下而非拼接
        Assert.All(resumeTransport.SnapshotRequests()
            .Where(request => request.RangeStart is not null && request.RangeEnd != 0),
            request => Assert.Equal("\"v1\"", request.IfRangeETag));
    }

    [Fact]
    public async Task ProbeTransient503_RetriesThenSucceedsWithBackoff()
    {
        var data = TestData(3 * 1024 * 1024);
        var probeFailures = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                                  && Interlocked.Increment(ref probeFailures) == 1
                ? FakeDownloadTransport.Status(503)
                : null
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var delays = new List<TimeSpan>();

        var result = await CreateEngine(transport, delays).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed); // 探测级瞬时失败不再让任务整体失败/立刻换节点
        Assert.Equal(2, probeFailures); // 探测执行两次：一次 503 + 一次成功
        Assert.Equal([TimeSpan.FromMilliseconds(500)], delays);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task Probe429_HonoursRetryAfterHeader()
    {
        var data = TestData(3 * 1024 * 1024);
        var probeFailures = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                                  && Interlocked.Increment(ref probeFailures) == 1
                ? FakeDownloadTransport.RateLimited(2)
                : null
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var delays = new List<TimeSpan>();

        var result = await CreateEngine(transport, delays).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays); // Retry-After 优先于 500ms 指数退避
    }

    [Fact]
    public async Task RepeatedTruncationWithProgress_ResetsAttemptBudgetAndCompletes()
    {
        // 模拟代理按固定字节掐断：每次只给 200KiB 就 EOF。健康推进的下载不应被 5 次上限误杀
        var data = TestData((int)(2.5 * 1024 * 1024)); // 连接数 1 → 单段
        const int chunk = 200 * 1024; // 2.5MiB / 200KiB ≈ 13 次响应（12 次"提前结束"）
        var server = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is { } start && request.RangeEnd != 0
                ? FakeDownloadTransport.Partial(data.Length, start, request.RangeEnd!.Value,
                    data[(int)start..(int)(start + Math.Min(chunk, request.RangeEnd.Value - start + 1))])
                : null
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var delays = new List<TimeSpan>();

        var result = await CreateEngine(transport, delays).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 1
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.True(delays.Count >= 10, $"应有多次截断重试，实际 {delays.Count} 次");
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task SegmentContentRangeMisaligned_FailsWithOffsetHint()
    {
        var data = TestData(3 * 1024 * 1024);
        var server = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd != 0
                ? FakeDownloadTransport.Partial(data.Length, 5, request.RangeEnd!.Value,
                    data[5..(int)(request.RangeEnd.Value + 1)]) // 从 5 开始，而请求要 0
                : null
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 1
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("分段错位", result.Error);
    }

    [Fact]
    public async Task LastModifiedOnlyResume_UnchangedServer_ResumesSkippedSegments()
    {
        const string lastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
        var data = TestData(3 * 1024 * 1024); // 2 段
        var firstRun = true;
        var blockingServer = new LoggedSegmentServer(data)
        {
            ETag = "", LastModified = lastModified,
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                ? null
                : request.RangeStart >= 2 * 1024 * 1024 && firstRun
                    ? new TransportResponse(206, "application/octet-stream", "", "",
                        $"bytes {request.RangeStart}-{request.RangeEnd}/{data.Length}", null, "", null,
                        new BlockingStream())
                    : null
        };
        var transport = new FakeDownloadTransport((request, _) =>
            Task.FromResult(blockingServer.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        using var cancellation = new CancellationTokenSource();
        var download = CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, update =>
        {
            if (update.DownloadedBytes >= 2 * 1024 * 1024) cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        firstRun = false;
        var resumeServer = new LoggedSegmentServer(data) { ETag = "", LastModified = lastModified };
        var resumeTransport = new FakeDownloadTransport((request, _) =>
            Task.FromResult(resumeServer.Serve(request)));
        var result = await CreateEngine(resumeTransport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.True(result.Resumed); // 无 ETag 时 Last-Modified 一致同样允许续传
        Assert.All(resumeServer.SnapshotRanges(), range => Assert.True(range.Start >= 2 * 1024 * 1024));
        // 无 ETag 时 If-Range 退回 Last-Modified 日期条件
        Assert.All(resumeTransport.SnapshotRequests()
            .Where(request => request.RangeStart is not null && request.RangeEnd != 0),
            request => Assert.Equal(lastModified, request.IfRangeETag));
    }

    [Fact]
    public async Task LastModifiedOnlyResume_ChangedServer_RestartsFromZero()
    {
        var data = TestData(3 * 1024 * 1024);
        var finalPath = Path.Combine(_workspace.Root, "out", "fw.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        // 预置"已下载半段"记录：无 ETag、Last-Modified 旧值
        DownloadJournal.Create(finalPath, "http://8.8.8.8/fw.bin", "", "Wed, 01 Jan 2025 00:00:00 GMT",
            data.Length, "fw.bin", "",
            [new DownloadSegment(0, 2 * 1024 * 1024 - 1, 1024 * 1024),
             new DownloadSegment(2 * 1024 * 1024, data.Length - 1, 0)]);
        File.WriteAllText(DownloadJournal.TempPathFor(finalPath), new string('x', data.Length));

        var server = new LoggedSegmentServer(data)
        {
            ETag = "", LastModified = "Thu, 01 Jan 2026 00:00:00 GMT" // 服务器资源已变更
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = Path.GetDirectoryName(finalPath)!, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.False(result.Resumed); // Last-Modified 不一致 → 放弃续传
        Assert.Contains(server.SnapshotRanges(), range => range.Start == 0);
    }

    [Fact]
    public async Task HtmlResponseWithDispositionFileName_DownloadsFile()
    {
        var html = "<html>release notes</html>"u8.ToArray();
        var transport = new FakeDownloadTransport((_, _) => Task.FromResult(
            FakeDownloadTransport.OkFull(html.Length, html, contentType: "text/html",
                disposition: "attachment; filename=\"notes.html\"")));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/notes", DestinationDirectory = destination
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed); // 显式命名的 html 不是防盗链页，允许下载
        Assert.Equal("notes.html", Path.GetFileName(result.FilePath!));
        Assert.Equal(html, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task MirrorFailover_KeepsFirstCandidateFileName()
    {
        var data = TestData(3 * 1024 * 1024);
        var primary = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                ? FakeDownloadTransport.Partial(data.Length, 0, 0, [data[0]],
                    disposition: "attachment; filename=\"primary.bin\"")
                : FakeDownloadTransport.Status(404) // 探测可用但分段终态 404 → failover
        };
        var mirror = new LoggedSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(
            request.Url.Contains("mirror") ? mirror.Serve(request) : primary.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin",
            DestinationDirectory = destination,
            Connections = 2,
            MirrorUrls = ["http://8.8.8.8/mirror/fw.bin"]
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        // 落盘名沿用首个候选（续传记录/目标路径跨镜像一致），而非镜像返回的 URL 名
        Assert.Equal("primary.bin", Path.GetFileName(result.FilePath!));
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task SingleStreamUnknownLength_CompletesWithActualSize()
    {
        var data = TestData(600 * 1024); // 无 Content-Length 的 200 响应 → 单流未知总长
        var transport = new FakeDownloadTransport((_, _) => Task.FromResult(
            new TransportResponse(200, "application/octet-stream", "\"v1\"", "", null, null, "", null,
                new MemoryStream(data))));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(data.Length, result.TotalBytes);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task EtagChangedOnResume_RestartsFromZero()
    {
        var data = TestData(3 * 1024 * 1024);
        var finalPath = Path.Combine(_workspace.Root, "out", "fw.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        // 预置一个"已下载半段"的续传记录（ETag v1）
        DownloadJournal.Create(finalPath, "http://8.8.8.8/fw.bin", "\"v1\"", "", data.Length, "fw.bin", "",
            [new DownloadSegment(0, 2 * 1024 * 1024 - 1, 1024 * 1024), new DownloadSegment(2 * 1024 * 1024, data.Length - 1, 0)]);
        File.WriteAllText(DownloadJournal.TempPathFor(finalPath), new string('x', data.Length));

        var server = new LoggedSegmentServer(data); // 探测返回默认 ETag "v1"？不——构造 v2
        server.Override = request => request.RangeStart is 0 && request.RangeEnd is 0
            ? new TransportResponse(206, "application/octet-stream", "\"v2\"", "",
                $"bytes 0-0/{data.Length}", 1, "", null, new MemoryStream([data[0]]))
            : null;
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var progress = new List<DownloadProgress>();

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = Path.GetDirectoryName(finalPath)!, Connections = 2
        }, update => { lock (progress) progress.Add(update); return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.False(result.Resumed);
        lock (progress) Assert.Contains(progress, update => update.Message.Contains("重新下载"));
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        var ranges = server.SnapshotRanges();
        Assert.Contains(ranges, range => range.Start == 0); // 从零重下
    }

    [Fact]
    public async Task Sha256Mismatch_FailsAndKeepsTempForDiagnosis()
    {
        var data = TestData(3 * 1024 * 1024);
        var server = new LoggedSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin",
            DestinationDirectory = destination,
            Connections = 2,
            ExpectedSha256 = new string('a', 64)
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("校验不通过", result.Error);
        var finalPath = Path.Combine(destination, "fw.bin");
        Assert.False(File.Exists(finalPath));
        Assert.True(File.Exists(DownloadJournal.TempPathFor(finalPath))); // 保留现场
        Assert.True(File.Exists(DownloadJournal.JournalPathFor(finalPath)));
    }

    [Fact]
    public async Task SizeMismatchOnSingleStream_FailsWithClearError()
    {
        var data = TestData(256 * 1024);
        var transport = new FakeDownloadTransport((_, _) => Task.FromResult(
            FakeDownloadTransport.OkFull(1024 * 1024, data))); // 声称 1MiB，只给 256KiB
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("大小不符", result.Error);
    }

    [Fact]
    public async Task HtmlResponseOnProbe_FailsWithLinkProtectionHint()
    {
        var transport = new FakeDownloadTransport((_, _) => Task.FromResult(
            FakeDownloadTransport.OkFull(1024, "<html/>"u8.ToArray(), contentType: "text/html")));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("网页", result.Error);
    }

    [Fact]
    public async Task ExistingTargetFile_IsRenamedInsteadOfOverwritten()
    {
        var data = TestData(3 * 1024 * 1024);
        var server = new LoggedSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var finalPath = Path.Combine(destination, "fw.bin");
        await File.WriteAllTextAsync(finalPath, "原有文件");

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.EndsWith("fw (1).bin", result.FilePath);
        Assert.Equal("原有文件", await File.ReadAllTextAsync(finalPath));
    }

    [Fact]
    public async Task Segments_DownloadInParallelOnDistinctConnections()
    {
        var data = TestData(3 * 1024 * 1024); // 2 段
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new LoggedSegmentServer(data)
        {
            Override = request =>
            {
                if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
                if (request.RangeStart == 0)
                {
                    firstArrived.TrySetResult();
                    secondArrived.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                else
                {
                    secondArrived.TrySetResult();
                    firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                return null; // 两个分段同时在途后才放行：证明并行
            }
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed); // 死锁（串行）会让 5 秒门闩超时抛异常
    }

    /// <summary>读操作永不返回数据的流：驱动看门狗与取消路径。</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            NeverEndingReadAsync(cancellationToken);

        private static async ValueTask<int> NeverEndingReadAsync(CancellationToken cancellationToken)
        {
            var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await gate.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    /// <summary>一直阻塞直到取消的流：驱动"取消时排空 worker"路径。</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource<int> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    public void Dispose() => _workspace.Dispose();
}
