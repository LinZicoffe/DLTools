using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 烧录上位机程序.Services.Downloads;

public sealed class DownloadJournalDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Url { get; set; } = "";
    public string ETag { get; set; } = "";
    public string LastModified { get; set; } = "";
    public long TotalLength { get; set; }
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public List<JournalSegment> Segments { get; set; } = [];
}

public sealed class JournalSegment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Completed { get; set; }
}

/// <summary>断点续传 sidecar：&lt;目标文件名&gt;.dltmp（预分配数据文件）+ .dltask.json（分段进度表）。
/// 并发写入靠 .dltmp 的独占文件锁（FileShare.None）互斥；日志文件本身由本类内部串行原子替换。
/// 损坏的日志抛异常并保留现场，绝不静默重建——由 UI 决定是否删除续传记录重新下载。</summary>
public sealed class DownloadJournal
{
    public const string TempSuffix = ".dltmp";
    public const string JournalSuffix = ".dltask.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private DownloadJournalDocument _document;

    public string FinalPath { get; }
    public string TempPath { get; }
    public string JournalPath { get; }
    public string Url => _document.Url;
    public string ETag => _document.ETag;
    public string LastModified => _document.LastModified;
    public long TotalLength => _document.TotalLength;
    public string FileName => _document.FileName;
    public string Sha256 => _document.Sha256;

    private DownloadJournal(string finalPath, DownloadJournalDocument document)
    {
        FinalPath = finalPath;
        TempPath = finalPath + TempSuffix;
        JournalPath = finalPath + JournalSuffix;
        _document = document;
    }

    public static string TempPathFor(string finalPath) => finalPath + TempSuffix;
    public static string JournalPathFor(string finalPath) => finalPath + JournalSuffix;

    public static bool HasJournal(string finalPath) => File.Exists(JournalPathFor(finalPath));

    /// <summary>打开既有续传记录。日志存在但 .dltmp 已丢失视为陈旧记录返回 null（调用方可安全删除）。</summary>
    /// <exception cref="IOException">日志损坏或数据非法：保留现场并给出可行动提示。</exception>
    public static DownloadJournal? TryOpen(string finalPath, string url)
    {
        var journalPath = JournalPathFor(finalPath);
        if (!File.Exists(journalPath)) return null;
        if (!File.Exists(TempPathFor(finalPath))) return null;
        CleanupStaleTemps(journalPath);
        DownloadJournalDocument document;
        try
        {
            document = JsonSerializer.Deserialize<DownloadJournalDocument>(File.ReadAllText(journalPath))
                ?? throw new JsonException("空文档");
            if (document.SchemaVersion != 1)
                throw new JsonException($"不支持的续传记录版本 {document.SchemaVersion}");
            if (document.Segments is null || document.Segments.Count == 0) throw new JsonException("缺少分段表");
            Validate(document, finalPath);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new IOException(
                "续传记录已损坏（" + ex.Message + "）。在任务上点击“重试”会自动改为全新下载；原记录已保留以便排查："
                + journalPath, ex);
        }

        var journal = new DownloadJournal(finalPath, document);
        // URL 不同说明是旧地址留下的记录：不能混用，视为无可续传数据。
        if (!string.Equals(journal.Url, url, StringComparison.OrdinalIgnoreCase)) return null;
        return journal;
    }

    /// <summary>创建新续传记录（覆盖任何既有日志；数据文件由引擎负责预分配）。</summary>
    public static DownloadJournal Create(string finalPath, string url, string etag, string lastModified,
        long totalLength, string fileName, string sha256, IReadOnlyList<DownloadSegment> segments)
    {
        CleanupStaleTemps(JournalPathFor(finalPath));
        var journal = new DownloadJournal(finalPath, new DownloadJournalDocument
        {
            Url = url,
            ETag = etag,
            LastModified = lastModified,
            TotalLength = totalLength,
            FileName = fileName,
            Sha256 = sha256,
            Segments = segments.Select(segment => new JournalSegment
            {
                Start = segment.Start, End = segment.End, Completed = segment.Completed
            }).ToList()
        });
        lock (journal._gate) journal.Persist();
        return journal;
    }

    /// <summary>清理崩溃遗留的原子替换临时文件（&lt;日志路径&gt;.&lt;guid&gt;.tmp）。清理失败不影响主流程。</summary>
    private static void CleanupStaleTemps(string journalPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(journalPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            foreach (var leftover in Directory.GetFiles(directory, Path.GetFileName(journalPath) + ".*.tmp"))
            {
                try { File.Delete(leftover); }
                catch (IOException) { /* 被其他进程占用的遗留文件留给下次清理 */ }
            }
        }
        catch (IOException) { /* 目录不可读等：不影响续传主流程 */ }
    }

    public IReadOnlyList<DownloadSegment> ReadSegments() =>
        _document.Segments.Select(segment =>
            new DownloadSegment(segment.Start, segment.End, segment.Completed)).ToArray();

    /// <summary>更新分段进度并原子落盘。内部串行化，可被多个 worker 并发调用。</summary>
    public void SaveSegments(IReadOnlyList<DownloadSegment> segments)
    {
        lock (_gate)
        {
            _document.Segments = segments.Select(segment => new JournalSegment
            {
                Start = segment.Start, End = segment.End, Completed = segment.Completed
            }).ToList();
            Persist();
        }
    }

    /// <summary>下载提交成功后删除日志（.dltmp 已被改名，无需处理）。</summary>
    public void DeleteJournal()
    {
        lock (_gate)
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
        }
    }

    /// <summary>删除续传记录与数据文件（“删除续传记录重新下载”入口）。</summary>
    public static void DeleteAll(string finalPath)
    {
        if (File.Exists(JournalPathFor(finalPath))) File.Delete(JournalPathFor(finalPath));
        if (File.Exists(TempPathFor(finalPath))) File.Delete(TempPathFor(finalPath));
    }

    private static void Validate(DownloadJournalDocument document, string finalPath)
    {
        if (document.TotalLength <= 0) throw new JsonException("总长度非法");
        var covered = 0L;
        foreach (var segment in document.Segments)
        {
            if (segment.Start < 0 || segment.End < segment.Start || segment.End >= document.TotalLength
                || segment.Completed < 0 || segment.Completed > segment.End - segment.Start + 1)
                throw new JsonException($"分段 [{segment.Start}, {segment.End}] 与总长度不符");
            covered += segment.Completed;
        }
        if (covered > document.TotalLength)
            throw new JsonException($"分段已完成字节合计 {covered} 超过总长度 {document.TotalLength}");
        if (string.IsNullOrWhiteSpace(document.FileName)) throw new JsonException("缺少文件名");
        _ = finalPath; // 校验仅针对结构；finalPath 参数保留给错误定位扩展
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(JournalPath)!;
        Directory.CreateDirectory(directory);
        var temporary = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_document, JsonOptions);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(JournalPath)) File.Replace(temporary, JournalPath, null);
            else File.Move(temporary, JournalPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
