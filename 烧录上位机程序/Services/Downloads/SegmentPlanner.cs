namespace 烧录上位机程序.Services.Downloads;

/// <summary>一个分段区间。[Start, End] 闭区间；Completed 为已落盘字节数（续传恢复用）。</summary>
public sealed record DownloadSegment(long Start, long End, long Completed = 0)
{
    public long Length => End - Start + 1;
    public bool IsComplete => Completed >= Length;
    public long Remaining => Length - Completed;
}

/// <summary>分段下载共享常量与连接数钳位。静态均分规划已由 DynamicSegmentPlanner（动态对半劈）取代；
/// 总长未知（分块传输）不进分段路径，由引擎走单流。</summary>
public static class SegmentPlanner
{
    public const long SingleStreamThreshold = 1024 * 1024;               // 1 MiB
    public const int MinConnections = 1;
    public const int MaxConnections = 16;

    public static int ClampConnections(int connections) => Math.Clamp(connections, MinConnections, MaxConnections);
}
