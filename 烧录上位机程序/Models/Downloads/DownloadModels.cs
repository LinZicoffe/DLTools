namespace 烧录上位机程序.Models.Downloads;

public enum DownloadTaskState
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Verifying,
    Committing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>单次下载的用户意图。FileNameOverride 为空表示从 Content-Disposition 或 URL 推断。</summary>
public sealed record DownloadOptions
{
    public string Url { get; init; } = "";
    public string DestinationDirectory { get; init; } = "";
    public string FileNameOverride { get; init; } = "";
    public int? Connections { get; init; }
    public long? MaxRateKBps { get; init; }
    public string ExpectedSha256 { get; init; } = "";
    public bool StartPaused { get; init; }
    /// <summary>镜像清单层填充的备用候选地址；引擎按 Url → MirrorUrls 顺序降级。</summary>
    public IReadOnlyList<string> MirrorUrls { get; init; } = [];
    /// <summary>同一 Url+目标路径下的 .dltmp/.dltask.json 续传记录存在时自动续传。</summary>
    public bool AllowResume { get; init; } = true;
}

public sealed record DownloadProgress(DownloadTaskState State, long TotalBytes, long DownloadedBytes,
    long SpeedBytesPerSec, int ActiveSegments, string Message);

public sealed record DownloadEngineResult(bool Completed, bool Cancelled, string? FilePath, string? Error,
    long TotalBytes, long DownloadedBytes, bool Resumed)
{
    public static DownloadEngineResult FromSuccess(string filePath, long totalBytes, bool resumed) =>
        new(true, false, filePath, null, totalBytes, totalBytes, resumed);

    public static DownloadEngineResult FromCancellation(long totalBytes, long downloadedBytes) =>
        new(false, true, null, null, totalBytes, downloadedBytes, false);

    public static DownloadEngineResult FromFailure(string error, long totalBytes, long downloadedBytes) =>
        new(false, false, null, error, totalBytes, downloadedBytes, false);
}

/// <summary>下载设置。MaxRateKB 为 0 表示不限速；代理仅支持 HTTP 且不含认证（凭据不落盘）。</summary>
public sealed record DownloadSettings
{
    public int DefaultConnections { get; init; } = 8;
    public int MaxConcurrentTasks { get; init; } = 3;
    public long MaxRateKB { get; init; }
    public string ProxyHost { get; init; } = "";
    public int ProxyPort { get; init; }
    public string LastDestinationDirectory { get; init; } = "";
    public bool HasProxy => ProxyHost.Length > 0 && ProxyPort > 0;
}

public sealed record MirrorFileEntry(string Name, long Size, string Sha256, IReadOnlyList<string> Urls);

public sealed record MirrorManifest(int SchemaVersion, IReadOnlyList<MirrorFileEntry> Files)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>镜像清单 JSON 的宽松反序列化载体；字段完整性由 MirrorManifestService 校验。</summary>
public sealed class MirrorManifestDocument
{
    public int SchemaVersion { get; set; }
    public List<MirrorFileDocument>? Files { get; set; }
}

public sealed class MirrorFileDocument
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public List<string>? Urls { get; set; }
}
