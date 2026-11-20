using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Xunit;
using 烧录上位机程序.Models.Downloads;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>稀疏预分配验证（真实 NTFS，非 NTFS 卷经 NtfsFact 显式跳过）：
/// 助手 IOCTL 语义、引擎"先标稀疏再 SetLength / 提交前清位"接线、真实 HTTP 大文件端到端。</summary>
public class SparseFileTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    [NtfsFact]
    public void MarkBeforeSetLength_LargeFile_AllocatedRangesFarBelowLength()
    {
        var path = Path.Combine(_workspace.Root, "sparse.bin");
        const long total = 64 * 1024 * 1024;
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.True(SparseFile.TryMarkSparse(file.SafeFileHandle));
        file.SetLength(total);
        var payload = new byte[1024 * 1024];
        file.Position = 32 * 1024 * 1024;
        file.Write(payload);
        file.Flush(flushToDisk: true);

        Assert.True(SparseFile.IsSparse(path));
        var allocated = QueryAllocatedBytes(file.SafeFileHandle, total);
        Assert.True(allocated < 4 * 1024 * 1024, $"64MiB 稀疏文件只写 1MiB，实际已分配 {allocated} 字节");
    }

    [NtfsFact]
    public void ClearAfterFullWrite_RemovesSparseAttribute()
    {
        var path = Path.Combine(_workspace.Root, "full.bin");
        const int total = 8 * 1024 * 1024;
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.True(SparseFile.TryMarkSparse(file.SafeFileHandle));
        file.SetLength(total);
        var buffer = new byte[64 * 1024];
        for (var offset = 0; offset < total; offset += buffer.Length) file.Write(buffer);
        file.Flush(flushToDisk: true);
        Assert.True(SparseFile.IsSparse(path));

        Assert.True(SparseFile.TryClearSparse(file.SafeFileHandle));
        Assert.False(SparseFile.IsSparse(path));
    }

    [NtfsFact]
    public void InvalidOrClosedHandle_ReturnsFalseWithoutThrowing()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.False(SparseFile.TryMarkSparse(invalid));
        Assert.False(SparseFile.TryClearSparse(invalid));

        var path = Path.Combine(_workspace.Root, "closed.bin");
        var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        var handle = file.SafeFileHandle;
        file.Dispose();
        Assert.False(SparseFile.TryMarkSparse(handle));
        Assert.False(SparseFile.TryClearSparse(handle));
        Assert.False(SparseFile.IsSparse(Path.Combine(_workspace.Root, "missing.bin")));
    }

    [NtfsFact]
    public void SmallFile_MarkingStillWorks_ThresholdIsEnginePolicy()
    {
        var path = Path.Combine(_workspace.Root, "small.bin");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        Assert.True(SparseFile.TryMarkSparse(file.SafeFileHandle)); // 助手不限长度，5MiB 阈值是引擎策略
        file.SetLength(1024 * 1024);
        Assert.True(SparseFile.IsSparse(path));
    }

    [NtfsFact]
    public async Task Engine_LargeSegmentedDownload_TempIsSparseAndFinalIsNot()
    {
        var data = new byte[6 * 1024 * 1024]; // ≥5MiB 阈值；动态对半劈，段数不固定
        new Random(42).NextBytes(data);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = 0;
        var server = new FakeSegmentServer(data)
        {
            Override = request =>
            {
                if (request.RangeStart is 0 && request.RangeEnd is 0) return null;
                if (Interlocked.Exchange(ref gated, 1) == 0)
                {
                    arrived.TrySetResult();
                    release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                return null;
            }
        };
        var transport = new FakeDownloadTransport((request, _) => Task.FromResult(server.Serve(request)));
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var finalPath = Path.Combine(destination, "fw.bin");

        var download = new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination, Connections = 2
        }, _ => Task.CompletedTask, CancellationToken.None);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(SparseFile.IsSparse(DownloadJournal.TempPathFor(finalPath))); // 预分配即稀疏
        }
        finally
        {
            release.TrySetResult(); // 断言失败也放行被闸 worker，避免空等超时且任务异常无人观察
        }

        var result = await download;
        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.False(SparseFile.IsSparse(result.FilePath!)); // 提交前已清位
    }

    [NtfsFact]
    public async Task Engine_RangeIgnoredLargeFile_SingleStreamTempIsSparseAndFinalIsNot()
    {
        var data = new byte[6 * 1024 * 1024];
        new Random(42).NextBytes(data);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 一律 200 全量 → 单流路径；内容流首读闸住，此刻 SetLength 已完成
        var transport = new FakeDownloadTransport((_, _) =>
        {
            var response = FakeDownloadTransport.OkFull(data.Length, data);
            return Task.FromResult(response with { Content = new GatedReadStream(data, arrived, release) });
        });
        var destination = Path.Combine(_workspace.Root, "out");
        Directory.CreateDirectory(destination);
        var finalPath = Path.Combine(destination, "fw.bin");

        var download = new DownloadEngine(transport).DownloadAsync(new DownloadOptions
        {
            Url = "http://8.8.8.8/fw.bin", DestinationDirectory = destination
        }, _ => Task.CompletedTask, CancellationToken.None);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(SparseFile.IsSparse(DownloadJournal.TempPathFor(finalPath)));
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await download;
        Assert.True(result.Completed, result.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.False(SparseFile.IsSparse(result.FilePath!));
    }

    [NtfsDownloadIntegrationFact]
    public async Task RealHttp_32MiBDownload_ContentCorrectAndFinalNonSparse()
    {
        var data = FaultInjectionServer.GenerateData(32 * 1024 * 1024);
        var expectedSha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        using var server = new FaultInjectionServer(data);
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
        Assert.False(SparseFile.IsSparse(result.FilePath!));
        TestEvidence.Save("稀疏预分配", new
        {
            sizeBytes = data.Length,
            elapsedMilliseconds = stopwatch.ElapsedMilliseconds, // 下载总耗时（含预分配），不硬断言，仅留证据
            sha256 = expectedSha,
            finalIsSparse = SparseFile.IsSparse(result.FilePath!)
        });
    }

    /// <summary>首读闸住的内存流：闸住期间引擎已完成稀疏预分配，供断言 .dltmp 稀疏位。</summary>
    private sealed class GatedReadStream(byte[] data, TaskCompletionSource arrived, TaskCompletionSource release) : Stream
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

    private static long QueryAllocatedBytes(SafeFileHandle handle, long total)
    {
        var input = new FileAllocatedRangeBuffer { StartingVcn = 0, Length = total };
        var output = new FileAllocatedRangeBuffer[64];
        if (!DeviceIoControl(handle, FsctlQueryAllocatedRanges, ref input, Marshal.SizeOf<FileAllocatedRangeBuffer>(),
                output, output.Length * Marshal.SizeOf<FileAllocatedRangeBuffer>(), out var returned, IntPtr.Zero))
            throw new InvalidOperationException($"FSCTL_QUERY_ALLOCATED_RANGES 失败：{Marshal.GetLastWin32Error()}");
        var count = returned / Marshal.SizeOf<FileAllocatedRangeBuffer>();
        var sum = 0L;
        for (var i = 0; i < count; i++) sum += output[i].Length;
        return sum;
    }

    private const uint FsctlQueryAllocatedRanges = 0x000940CF;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAllocatedRangeBuffer
    {
        public long StartingVcn;
        public long Length;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        ref FileAllocatedRangeBuffer lpInBuffer, int nInBufferSize,
        [Out] FileAllocatedRangeBuffer[] lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    public void Dispose() => _workspace.Dispose();
}