using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class DownloadSegmentPlannerTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(17, 16)]
    [InlineData(99, 16)]
    public void ClampConnections_StaysWithinBounds(int connections, int expected)
    {
        Assert.Equal(expected, SegmentPlanner.ClampConnections(connections));
    }

    [Fact]
    public void Plan_ArgumentOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlanner.Plan(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlanner.Plan(-1, 8));
    }

    [Fact]
    public void Plan_SmallFileOrSingleConnection_UsesSingleSegment()
    {
        foreach (var (total, connections) in new[] { (512L * 1024, 8), (50L * 1024 * 1024, 1) })
        {
            var segments = SegmentPlanner.Plan(total, connections);
            var segment = Assert.Single(segments);
            Assert.Equal(0, segment.Start);
            Assert.Equal(total - 1, segment.End);
        }
    }

    [Fact]
    public void Plan_SegmentsAreContiguousAndNeverBelowMinimumSize()
    {
        var total = 10L * 1024 * 1024; // 10 MiB, 8 连接 → 目标 32 段 → 钳到 2 MiB/段 → 5 段
        var segments = SegmentPlanner.Plan(total, 8);
        Assert.Equal(5, segments.Count);
        long covered = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            Assert.Equal(covered, segment.Start);
            Assert.Equal(Math.Min(covered + SegmentPlanner.MinSegmentSize - 1, total - 1), segment.End);
            Assert.InRange(segment.Length, 1, SegmentPlanner.MinSegmentSize);
            covered += segment.Length;
        }
        Assert.Equal(total, covered);
        Assert.Equal(0, segments.Sum(segment => segment.Completed));
    }

    [Fact]
    public void Plan_LargeFileSpawnsMoreSegmentsThanConnectionsForLoadBalancing()
    {
        var total = 100L * 1024 * 1024; // 100 MiB, 8 连接 → 3.125 MiB/段 → 32 段（= 8×4，整除）
        var segments = SegmentPlanner.Plan(total, 8);
        Assert.Equal(32, segments.Count);
        Assert.All(segments, segment => Assert.Equal(total / 32, segment.Length));
        Assert.Equal(total, segments.Sum(segment => segment.Length));
    }
}
