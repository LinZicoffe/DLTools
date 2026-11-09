using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class DownloadJournalTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    // Create 只负责写日志；.dltmp 数据文件由引擎预分配，测试里手动补齐成对文件。
    private void WriteTemp(string finalPath) => File.WriteAllText(DownloadJournal.TempPathFor(finalPath), "partial");

    [Fact]
    public void Create_ThenTryOpen_RoundTripsSegmentsAndMetadata()
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        var segments = new[]
        {
            new DownloadSegment(0, 99, 100),
            new DownloadSegment(100, 199, 0),
        };
        var journal = DownloadJournal.Create(finalPath, "https://8.8.8.8/fw.bin", "\"etag-1\"",
            "Mon, 01 Jan 2026 00:00:00 GMT", 200, "fw.bin", "abc", segments);
        WriteTemp(finalPath);
        Assert.Equal("https://8.8.8.8/fw.bin", journal.Url);
        Assert.Equal(200, journal.TotalLength);
        Assert.Equal("\"etag-1\"", journal.ETag);
        Assert.True(DownloadJournal.HasJournal(finalPath));

        var reopened = DownloadJournal.TryOpen(finalPath, "https://8.8.8.8/fw.bin");
        Assert.NotNull(reopened);
        Assert.Equal("abc", reopened!.Sha256);
        Assert.Equal(segments, reopened.ReadSegments());
    }

    [Fact]
    public void TryOpen_MissingJournalOrMissingTemp_ReturnsNull()
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        Assert.Null(DownloadJournal.TryOpen(finalPath, "https://8.8.8.8/fw.bin"));

        DownloadJournal.Create(finalPath, "https://8.8.8.8/fw.bin", "", "", 200, "fw.bin", "",
            [new DownloadSegment(0, 199, 0)]);
        WriteTemp(finalPath);
        File.Delete(DownloadJournal.TempPathFor(finalPath)); // 模拟数据文件被清理
        Assert.Null(DownloadJournal.TryOpen(finalPath, "https://8.8.8.8/fw.bin"));
        Assert.True(DownloadJournal.HasJournal(finalPath)); // 陈旧日志保留，由上层决定删除
    }

    [Fact]
    public void TryOpen_DifferentUrl_ReturnsNullWithoutTouchingFiles()
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        DownloadJournal.Create(finalPath, "https://8.8.8.8/fw.bin", "", "", 200, "fw.bin", "",
            [new DownloadSegment(0, 199, 0)]);
        WriteTemp(finalPath);
        Assert.Null(DownloadJournal.TryOpen(finalPath, "https://8.8.8.9/fw.bin"));
        Assert.True(DownloadJournal.HasJournal(finalPath));
    }

    [Fact]
    public void TryOpen_CorruptJournal_ThrowsAndKeepsFileForDiagnosis()
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        File.WriteAllText(DownloadJournal.JournalPathFor(finalPath), "{ not json");
        WriteTemp(finalPath);
        var error = Assert.Throws<IOException>(() => DownloadJournal.TryOpen(finalPath, "https://8.8.8.8/fw.bin"));
        Assert.Contains("损坏", error.Message);
        Assert.True(File.Exists(DownloadJournal.JournalPathFor(finalPath))); // 保留现场
    }

    [Theory]
    [InlineData("null", "空文档")]
    [InlineData("""{"SchemaVersion":2}""", "版本")]
    [InlineData("{}", "分段表")] // 反序列化得到默认实例，但缺少分段表
    public void TryOpen_InvalidDocument_ThrowsWithHint(string content, string hint)
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        File.WriteAllText(DownloadJournal.JournalPathFor(finalPath), content);
        WriteTemp(finalPath);
        var error = Assert.Throws<IOException>(() => DownloadJournal.TryOpen(finalPath, "https://8.8.8.8/fw.bin"));
        Assert.Contains(hint, error.Message);
    }

    [Fact]
    public void TryOpen_SegmentsBeyondTotalLength_Throws()
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        DownloadJournal.Create(finalPath, "https://8.8.8.8/fw.bin", "", "", 100, "fw.bin", "",
            [new DownloadSegment(0, 49, 50), new DownloadSegment(50, 149, 0)]); // 第二段越界
        WriteTemp(finalPath);
        var error = Assert.Throws<IOException>(() => DownloadJournal.TryOpen(finalPath, "https://8.8.8.8/fw.bin"));
        Assert.Contains("总长度不符", error.Message);
    }

    [Fact]
    public void SaveSegments_UpdatesJournalAtomicallyWithoutLeftoverTempFiles()
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        var journal = DownloadJournal.Create(finalPath, "https://8.8.8.8/fw.bin", "", "", 300, "fw.bin", "",
            [new DownloadSegment(0, 99, 0), new DownloadSegment(100, 199, 0), new DownloadSegment(200, 299, 0)]);
        WriteTemp(finalPath);
        journal.SaveSegments(
        [
            new DownloadSegment(0, 99, 100),
            new DownloadSegment(100, 199, 40),
            new DownloadSegment(200, 299, 0),
        ]);

        var reopened = DownloadJournal.TryOpen(finalPath, "https://8.8.8.8/fw.bin");
        Assert.Equal([100, 40, 0], reopened!.ReadSegments().Select(segment => segment.Completed));
        Assert.Empty(Directory.GetFiles(_workspace.Root, "*.tmp"));
    }

    [Fact]
    public async Task SaveSegments_IsSerializedAcrossConcurrentWriters()
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        var journal = DownloadJournal.Create(finalPath, "https://8.8.8.8/fw.bin", "", "", 10_000, "fw.bin", "",
            SegmentPlanner.Plan(10_000, 4));
        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() => journal.SaveSegments(
            SegmentPlanner.Plan(10_000, 4).Select((segment, index) => segment with { Completed = i + index }).ToArray())));
        await Task.WhenAll(writers);
        WriteTemp(finalPath);
        Assert.True(DownloadJournal.HasJournal(finalPath));
        Assert.Null(Record.Exception(() => DownloadJournal.TryOpen(finalPath, "https://8.8.8.8/fw.bin")));
    }

    [Fact]
    public void DeleteJournalAndDeleteAll_RemoveExpectedFiles()
    {
        var finalPath = Path.Combine(_workspace.Root, "fw.bin");
        var journal = DownloadJournal.Create(finalPath, "https://8.8.8.8/fw.bin", "", "", 200, "fw.bin", "",
            [new DownloadSegment(0, 199, 0)]);
        WriteTemp(finalPath);

        journal.DeleteJournal();
        Assert.False(DownloadJournal.HasJournal(finalPath));
        Assert.True(File.Exists(DownloadJournal.TempPathFor(finalPath)));

        DownloadJournal.Create(finalPath, "https://8.8.8.8/fw.bin", "", "", 200, "fw.bin", "", [new DownloadSegment(0, 199, 0)]);
        DownloadJournal.DeleteAll(finalPath);
        Assert.False(DownloadJournal.HasJournal(finalPath));
        Assert.False(File.Exists(DownloadJournal.TempPathFor(finalPath)));
    }

    public void Dispose() => _workspace.Dispose();
}
