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
        TimeSpan? stall = null, int maxAttempts = 5, DownloadHostLimits? hostLimits = null) =>
        new(transport, stallTimeout: stall ?? TimeSpan.FromSeconds(30), maxAttempts: maxAttempts,
            retryDelay: (delay, _) => { delays?.Add(delay); return Task.CompletedTask; }, hostLimits: hostLimits);

    [Fact]
    public async Task MultiSegment_CompletesWithContiguousBytesAndCoverage()
    {
        var data = TestData(3 * 1024 * 1024); // 动态对半劈：段数不固定，断言内容+覆盖
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
        Assert.True(ranges.Count >= 2, $"动态分段应至少两次请求，实际 {ranges.Count}");
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
        var data = TestData(3 * 1024 * 1024);
        // 单 worker 单段 + 精确 2MiB 前缀后阻塞：取消时 Completed 是确定值（动态多 worker 下水位不确定）
        var blockingServer = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                ? null
                : new TransportResponse(206, "application/octet-stream", "", "",
                    $"bytes {request.RangeStart}-{request.RangeEnd}/{data.Length}", null, "", null,
                    new PrefixThenBlockingStream(data[..(2 * 1024 * 1024)], TimeSpan.FromMilliseconds(100)))
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
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 1
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

        // 第二次运行：正常服务器，只应请求剩余字节（动态劈分后各请求起点均 ≥ 已完成水位）
        var resumeServer = new LoggedSegmentServer(data);
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
        var data = TestData(3 * 1024 * 1024);
        // 单 worker + 精确 2MiB 前缀后阻塞：无 ETag 时 Last-Modified 一致同样允许续传
        var blockingServer = new LoggedSegmentServer(data)
        {
            ETag = "", LastModified = lastModified,
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                ? null
                : new TransportResponse(206, "application/octet-stream", "", "",
                    $"bytes {request.RangeStart}-{request.RangeEnd}/{data.Length}", null, "", null,
                    new PrefixThenBlockingStream(data[..(2 * 1024 * 1024)], TimeSpan.FromMilliseconds(100)))
        };
        var transport = new FakeDownloadTransport((request, _) =>
            Task.FromResult(blockingServer.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        using var cancellation = new CancellationTokenSource();
        var download = CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 1
        }, update =>
        {
            if (update.DownloadedBytes >= 2 * 1024 * 1024) cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

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
        var data = TestData(3 * 1024 * 1024);
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataSeq = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request =>
            {
                if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
                // 按请求序号分流（动态调度下抢占/重发请求起点不固定，按 RangeStart==0 分流会死锁）
                var seq = Interlocked.Increment(ref dataSeq);
                if (seq == 1)
                {
                    firstArrived.TrySetResult();
                    secondArrived.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                else if (seq == 2)
                {
                    secondArrived.TrySetResult();
                    firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                return null; // 前两个数据请求同时在途后才放行：证明并行
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

    [Fact]
    public async Task Segment429WhileOtherWorkerReceiving_LearnsCapAtPeakWorking()
    {
        var data = TestData(3 * 1024 * 1024);
        var limits = new DownloadHostLimits();
        var receiving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seq = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request =>
            {
                if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
                // 动态调度下抢占/重发请求起点不固定，按请求序号分流而非 RangeStart==0
                var n = Interlocked.Increment(ref seq);
                if (n == 1)
                {
                    // 首个数据请求：慢速流占住接收计数（约 500ms 窗口），保证拒绝发生时有人正在收数
                    var end = (int)request.RangeEnd!.Value;
                    return new TransportResponse(206, "application/octet-stream", "\"v1\"", "",
                        $"bytes {request.RangeStart}-{end}/{data.Length}", end + 1 - (int)request.RangeStart!.Value, "", null,
                        new PacedStream(data[(int)request.RangeStart!.Value..(end + 1)],
                            receiving, receiving, TimeSpan.FromMilliseconds(20)));
                }
                if (n <= 4)
                {
                    receiving.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (n == 2) Thread.Sleep(200); // 覆盖"流信号 → 引擎计数"之间的微秒级间隙
                    return FakeDownloadTransport.RateLimited(0);
                }
                return null; // 多余 worker 退休后只剩单 worker 顺序拉尾段
            }
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport, hostLimits: limits).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        // 接收中连续两次 429 → cap=当时接收峰值 1；第三次拒绝触发多余 worker 退休，不再烧重试预算
        Assert.Equal(1, limits.GetCap("http://8.8.8.8/fw.bin"));
    }

    [Fact]
    public async Task LearnedCap_NextRunOpensAtMostCapWorkers()
    {
        var data = TestData(9 * 1024 * 1024); // 已学上限 2 → 动态调度下最多 2 个 worker 在途
        var limits = new DownloadHostLimits();
        limits.ReportRefused("http://8.8.8.8/fw.bin", 2);
        limits.ReportRefused("http://8.8.8.8/fw.bin", 2);
        var inFlight = 0;
        var peak = 0;
        var arrived = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new LoggedSegmentServer(data)
        {
            Override = request =>
            {
                if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
                var current = Interlocked.Increment(ref inFlight);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < current)
                    Interlocked.CompareExchange(ref peak, current, seen);
                var order = Interlocked.Increment(ref arrived);
                if (order == 1)
                {
                    first.TrySetResult();
                    second.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                else if (order == 2)
                {
                    second.TrySetResult();
                    first.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                var start = (int)request.RangeStart!.Value;
                var end = (int)request.RangeEnd!.Value;
                var body = data[start..(end + 1)];
                return new TransportResponse(206, "application/octet-stream", "\"v1\"", "",
                    $"bytes {start}-{end}/{data.Length}", body.Length, "", null,
                    new DisposalCallbackStream(new MemoryStream(body), () => Interlocked.Decrement(ref inFlight)));
            }
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport, hostLimits: limits).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 8
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.Equal(2, Volatile.Read(ref peak)); // 前两段 rendezvous 保证 ≥2；上限约束保证 ≤2
    }

    [Fact]
    public async Task Probe429_RetryAfterBeyondFiveMinutes_IsCappedAt300Seconds()
    {
        var data = TestData(3 * 1024 * 1024);
        var probeFailures = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request => request.RangeStart is 0 && request.RangeEnd is 0
                                  && Interlocked.Increment(ref probeFailures) == 1
                ? FakeDownloadTransport.RateLimited(600) // 异常长的 Retry-After：不能把任务挂死
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
        Assert.Equal([TimeSpan.FromSeconds(300)], delays);
    }

    [Fact]
    public async Task SlowFirstConnection_IdleWorkerTakesTailHalf()
    {
        var data = TestData(3 * 1024 * 1024);
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new LoggedSegmentServer(data)
        {
            Override = request =>
            {
                if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
                // 挂住起点为 0 的请求（整文件或头段）；尾半请求正常放行并落 Ranges 记录
                if (request.RangeStart != 0) return null;
                var end = (int)request.RangeEnd!.Value;
                return new TransportResponse(206, "application/octet-stream", "\"v1\"", "",
                    $"bytes 0-{end}/{data.Length}", end + 1, "", null,
                    new GatedStream(data[..(end + 1)], firstArrived, release));
            }
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var download = CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var deadline = Environment.TickCount64 + 5000;
            // 首连接挂住时，空闲 worker 应劈出并请求 [splitAt, total-1] 的尾半
            while (!server.SnapshotRanges().Any(range => range.Start > 0 && range.End == data.Length - 1))
            {
                Assert.True(Environment.TickCount64 < deadline, "空闲 worker 未在 5 秒内劈出尾半请求");
                await Task.Delay(10);
            }
        }
        finally { release.TrySetResult(); }

        var result = await download;
        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task SplitDuringStream_ClaimTruncatesAndReassigns_ContentExact()
    {
        var data = TestData(3 * 1024 * 1024);
        var dataRequests = 0;
        var wholeRequested = 0;
        var server = new LoggedSegmentServer(data);
        server.Override = request =>
        {
            if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
            Interlocked.Increment(ref dataRequests);
            if (request.RangeStart != 0) return null;
            if (request.RangeEnd == data.Length - 1) Interlocked.Exchange(ref wholeRequested, 1);
            var end = (int)request.RangeEnd!.Value;
            // 先交付 128KiB 让对端有得劈，再等第二个数据请求（劈出的尾半）出现后才继续：
            // 注入响应不走 Ranges 记录，等待条件必须用自己的请求计数
            return new TransportResponse(206, "application/octet-stream", "\"v1\"", "",
                $"bytes 0-{end}/{data.Length}", end + 1, "", null,
                new SplitWaitStream(data[..(end + 1)], () => Volatile.Read(ref dataRequests) >= 2));
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!)); // 截断+重发不得产生重叠写
        Assert.Equal(1, Volatile.Read(ref wholeRequested)); // 首连接确实请求过整个文件
        // 尾半请求与整文件区间重叠：旧流被 Claim 截断后重发，内容仍逐字节一致
        Assert.Contains(server.SnapshotRanges(), range => range.Start > 0 && range.End == data.Length - 1);
    }

    [Fact]
    public async Task Cancel_JournalFlushOrder_TempPrefixMatchesJournalExactly()
    {
        var data = TestData(3 * 1024 * 1024);
        var seq = 0;
        var server = new LoggedSegmentServer(data)
        {
            Override = request =>
            {
                if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
                var start = (int)request.RangeStart!.Value;
                var end = (int)request.RangeEnd!.Value;
                if (Interlocked.Increment(ref seq) == 1)
                    return new TransportResponse(206, "application/octet-stream", "\"v1\"", "",
                        $"bytes {start}-{end}/{data.Length}", end + 1 - start, "", null,
                        new PrefixThenBlockingStream(data[start..(start + 1024 * 1024)], TimeSpan.FromMilliseconds(100)));
                return new TransportResponse(206, "application/octet-stream", "\"v1\"", "",
                    $"bytes {start}-{end}/{data.Length}", end + 1 - start, "", null,
                    new BlockingStream());
            }
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var finalPath = Path.Combine(destination, "fw.bin");
        var url = "http://8.8.8.8/fw.bin";

        using var cancellation = new CancellationTokenSource();
        var download = CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = url, DestinationDirectory = destination, Connections = 2
        }, update =>
        {
            if (update.DownloadedBytes >= 1024 * 1024) cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        // 先 flush 数据再写 journal：取消后 journal 声称的每个已完成前缀必须已在 .dltmp 落盘。
        // 局限：读断言走同一 OS 缓存，无崩溃注入时无法证伪 flush/save 顺序颠倒；顺序保证另靠代码审查守护。
        var journal = DownloadJournal.TryOpen(finalPath, url);
        Assert.NotNull(journal);
        var segments = journal!.ReadSegments();
        Assert.Equal(1024 * 1024, segments.Sum(segment => segment.Completed));
        var temp = await File.ReadAllBytesAsync(DownloadJournal.TempPathFor(finalPath));
        foreach (var segment in segments.Where(segment => segment.Completed > 0))
        {
            var start = (int)segment.Start;
            var length = (int)segment.Completed;
            Assert.Equal(data[start..(start + length)], temp[start..(start + length)]);
        }
    }

    [Fact]
    public async Task OldStaticLayoutResume_DoesNotRewriteCompletedPrefix()
    {
        var data = TestData(3 * 1024 * 1024);
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var finalPath = Path.Combine(destination, "fw.bin");
        var url = "http://8.8.8.8/fw.bin";
        // 旧版静态段表：[0,2MiB) 已完成 1MiB + [2MiB,3MiB) 未动
        DownloadJournal.Create(finalPath, url, "\"v1\"", "", data.Length, "fw.bin", "",
        [
            new DownloadSegment(0, 2 * 1024 * 1024 - 1, 1024 * 1024),
            new DownloadSegment(2 * 1024 * 1024, data.Length - 1, 0)
        ]);
        var tempPath = DownloadJournal.TempPathFor(finalPath);
        await File.WriteAllBytesAsync(tempPath, data[..(1024 * 1024)]);
        using (var extend = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.None))
            extend.SetLength(data.Length);

        var server = new LoggedSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = url, DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.True(result.Resumed);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.All(server.SnapshotRanges(), range => Assert.True(range.Start >= 1024 * 1024,
            $"续传不应重拉已完成前缀，实际起点 {range.Start}"));
    }

    [Fact]
    public async Task RateLimitWait_SplitDuringAcquire_ClaimGrantsOnlyOwnedBytes()
    {
        var data = TestData(3 * 1024 * 1024 / 2); // 1.5MiB，限速 1MiB/s → 传输期足够长
        var server = new LoggedSegmentServer(data);
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);

        var result = await CreateEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination,
            Connections = 2, MaxRateKBps = 1024
        }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        var ranges = server.SnapshotRanges();
        Assert.Contains(ranges, range => range.Start == 0 && range.End == data.Length - 1);
        Assert.Contains(ranges, range => range.Start > 0); // 限速等待期被劈：重叠请求下 Claim 只授予自有字节
    }

    /// <summary>慢速收数流：首块立即返回并发信号，其后每块间隔 pace；最后一块交付时发完成信号。</summary>
    private sealed class PacedStream(byte[] data, TaskCompletionSource receivingSignal,
        TaskCompletionSource completionSignal, TimeSpan pace) : Stream
    {
        private int _position;
        private bool _firstRead = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_position >= data.Length) return 0;
            if (!_firstRead) await Task.Delay(pace, cancellationToken);
            _firstRead = false;
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            receivingSignal.TrySetResult();
            if (_position == data.Length) completionSignal.TrySetResult();
            return count;
        }
    }

    /// <summary>释放时回调的流包装：用于统计在途响应数（响应流释放 = 该连接收尾）。</summary>
    private sealed class DisposalCallbackStream(Stream inner, Action onDisposed) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                onDisposed();
            }
            base.Dispose(disposing);
        }
        // 不重写 DisposeAsync：Stream 基类会路由到 Dispose(true)，重写会导致回调触发两次
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

    /// <summary>先按 pace 逐块交付给定前缀，随后阻塞直到取消：驱动"流式传输中取消"路径。
    /// pace 取进度节流间隔（100ms）保证每块（含末块）都触发上报，取消经流式回调而非看门狗兜底。</summary>
    private sealed class PrefixThenBlockingStream(byte[] prefix, TimeSpan pace) : Stream
    {
        private int _position;
        private readonly TaskCompletionSource<int> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_position < prefix.Length)
            {
                if (_position > 0) await Task.Delay(pace, cancellationToken);
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            await _gate.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    /// <summary>首次读取先发到达信号并等待放行，之后正常交付：驱动"慢首连接 → 空闲 worker 劈尾半"路径。</summary>
    private sealed class GatedStream(byte[] data, TaskCompletionSource arrived, TaskCompletionSource release) : Stream
    {
        private int _position;
        private bool _firstRead = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_firstRead)
            {
                _firstRead = false;
                arrived.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            if (_position >= data.Length) return 0;
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    /// <summary>先交付 128KiB，再轮询等待劈裂发生（5 秒超时）后继续：驱动"流中途被劈 → Claim 截断"路径。</summary>
    private sealed class SplitWaitStream(byte[] data, Func<bool> splitHappened) : Stream
    {
        private int _position;
        private bool _waited;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!_waited && _position >= 128 * 1024)
            {
                _waited = true;
                var deadline = Environment.TickCount64 + 5000;
                while (!splitHappened())
                {
                    if (Environment.TickCount64 >= deadline)
                        throw new TimeoutException("对端 worker 未在 5 秒内触发劈裂");
                    await Task.Delay(10, cancellationToken);
                }
            }
            if (_position >= data.Length) return 0;
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    public void Dispose() => _workspace.Dispose();
}
