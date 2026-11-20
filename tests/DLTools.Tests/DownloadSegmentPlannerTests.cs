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
}
