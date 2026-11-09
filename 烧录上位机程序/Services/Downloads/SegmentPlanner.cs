namespace 烧录上位机程序.Services.Downloads;

/// <summary>一个分段区间。[Start, End] 闭区间；Completed 为已落盘字节数（续传恢复用）。</summary>
public sealed record DownloadSegment(long Start, long End, long Completed = 0)
{
    public long Length => End - Start + 1;
    public bool IsComplete => Completed >= Length;
    public long Remaining => Length - Completed;
}

/// <summary>分段规划：小文件单流；其余按连接数×4 的目标段数均分，每段不小于 2 MiB，
/// worker 从段队列拉取以自动均衡（消除单段拖尾）。总长未知（分块传输）不进本规划器，由引擎走单流路径。</summary>
public static class SegmentPlanner
{
    public const long SingleStreamThreshold = 1024 * 1024;               // 1 MiB
    public const long MinSegmentSize = 2 * 1024 * 1024;                  // 2 MiB
    public const int MinConnections = 1;
    public const int MaxConnections = 16;

    public static int ClampConnections(int connections) => Math.Clamp(connections, MinConnections, MaxConnections);

    public static IReadOnlyList<DownloadSegment> Plan(long totalLength, int connections)
    {
        if (totalLength <= 0) throw new ArgumentOutOfRangeException(nameof(totalLength), "总长度必须为正数。");
        var workerCount = ClampConnections(connections);
        if (totalLength <= SingleStreamThreshold || workerCount == 1)
            return [new DownloadSegment(0, totalLength - 1)];

        var targetSegments = workerCount * 4;
        var segmentSize = Math.Max(MinSegmentSize, (totalLength + targetSegments - 1) / targetSegments);
        var segments = new List<DownloadSegment>();
        for (var start = 0L; start < totalLength; start += segmentSize)
            segments.Add(new DownloadSegment(start, Math.Min(start + segmentSize - 1, totalLength - 1)));
        return segments;
    }
}
