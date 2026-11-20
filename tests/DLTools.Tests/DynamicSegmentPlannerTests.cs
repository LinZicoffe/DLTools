using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>动态分段规划器定向验证：对半劈对齐/尾段保护/Reserved 保护/抢占宽限与收数保护/
/// Claim 截断与 Lost/Release 回滚/Resume 任意粒度/局部性派活。全部注入可控时钟。</summary>
public class DynamicSegmentPlannerTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * 1024;

    [Fact]
    public void FirstAssign_GivesWholeFileAsOneSegment()
    {
        var planner = new DynamicSegmentPlanner(MiB, clock: () => 0);
        var assignment = planner.Assign(0);
        Assert.NotNull(assignment);
        Assert.Equal(new Assignment(0, 0, MiB - 1), assignment!.Value);
        Assert.Equal([new DownloadSegment(0, MiB - 1)], planner.Snapshot());
        Assert.False(planner.IsComplete);
    }

    [Fact]
    public void SecondAssign_SplitsLargestRemaining_HalfAlignedAfterReserved()
    {
        var total = MiB + 123; // 故意不对齐
        var planner = new DynamicSegmentPlanner(total, clock: () => 0);
        planner.Assign(0);
        Assert.Equal(1000, planner.Claim(0, 0, 0, 1000).GrantedBytes);

        var second = planner.Assign(1);
        Assert.NotNull(second);
        var expectedSplit = 1000 + (total - 1000) / 2 / 4096 * 4096;
        Assert.Equal(expectedSplit, second!.Value.RequestStart);
        Assert.Equal(total - 1, second.Value.RequestEnd);
        Assert.Equal(0, (expectedSplit - 1000) % 4096); // 劈点相对 Reserved 按 4KiB 对齐
        Assert.True(total - expectedSplit >= 12 * KiB);
        // 原 worker 的段尾已收缩：越界 Claim 得 Lost，界内仍授予
        Assert.True(planner.Claim(0, 0, expectedSplit, 10).Lost);
        Assert.Equal(10, planner.Claim(0, 0, expectedSplit - 10, 10).GrantedBytes);
    }

    [Fact]
    public void TailBelowMinSplit_DoesNotSplit_ReturnsNull()
    {
        var planner = new DynamicSegmentPlanner(16 * KiB, clock: () => 0); // 半段 8KiB < 12KiB
        Assert.NotNull(planner.Assign(0));
        Assert.Null(planner.Assign(1));
    }

    [Fact]
    public void Split_NeverCutsBelowReserved()
    {
        var planner = new DynamicSegmentPlanner(MiB, clock: () => 0);
        planner.Assign(0);
        planner.Claim(0, 0, 0, 600 * (int)KiB); // 已 claim 到 600KiB（在途写水位）
        var second = planner.Assign(1);
        Assert.NotNull(second);
        Assert.True(second!.Value.RequestStart >= 600 * KiB);
        Assert.Equal(100, planner.Claim(0, 0, 599 * KiB, 100).GrantedBytes); // Reserved 以下仍归原主
    }

    [Fact]
    public void Preempt_AdjacentIdleBeyondGrace_TakesOverAndOldOwnerGetsLost()
    {
        long now = 1000;
        var planner = DynamicSegmentPlanner.Resume(2 * MiB,
            [new DownloadSegment(0, MiB - 1), new DownloadSegment(MiB, 2 * MiB - 1)],
            minSplitBytes: 2 * MiB, clock: () => now); // minSplit=2MiB 禁用对半劈，隔离抢占路径
        planner.SetProbeTtfb(100); // 宽限期 = max(2×100, 20) = 200ms
        planner.Assign(0);
        Drain(planner, 0, 0, 0, MiB); // worker0 完成首段 → lastCompletedEnd=1MiB
        var second = planner.Assign(1);
        Assert.Equal(1, second!.Value.SegmentIndex);

        now += 199;
        Assert.Null(planner.Assign(0)); // 宽限期内不抢（请求可能已在途）
        for (var i = 0; i < 5; i++) planner.Assign(1); // 原主反复幂等 Assign 不得续期宽限期
        now += 1;
        var preempted = planner.Assign(0);
        Assert.Equal(new Assignment(1, MiB, 2 * MiB - 1), preempted!.Value);
        Assert.True(planner.Claim(1, 1, MiB, 10).Lost); // 原主下次 Claim 得 Lost
    }

    [Fact]
    public void Preempt_ReceivingSegmentIsProtected()
    {
        long now = 1000;
        var planner = DynamicSegmentPlanner.Resume(2 * MiB,
            [new DownloadSegment(0, MiB - 1), new DownloadSegment(MiB, 2 * MiB - 1)],
            minSplitBytes: 2 * MiB, clock: () => now);
        planner.Assign(0);
        Drain(planner, 0, 0, 0, MiB);
        planner.Assign(1);
        planner.MarkReceiving(1, 1);
        Assert.Equal(1, planner.ReceivingCount);
        now += 10_000;
        Assert.Null(planner.Assign(0)); // 正在收数的段不被抢占
    }

    [Fact]
    public void Claim_PartialGrantAtSegmentEnd_AndLostForNonOwnerOrOutOfRange()
    {
        var planner = new DynamicSegmentPlanner(MiB, clock: () => 0);
        planner.Assign(0);
        var second = planner.Assign(1); // 劈点 512KiB
        Assert.Equal(512 * KiB, second!.Value.RequestStart);

        var partial = planner.Claim(0, 0, 512 * KiB - 100, 200);
        Assert.False(partial.Lost);
        Assert.Equal(100, partial.GrantedBytes); // 段尾截断
        Assert.True(planner.Claim(0, 0, 512 * KiB, 10).Lost); // 越界
        Assert.True(planner.Claim(1, 0, 0, 10).Lost); // 非段主
        Assert.True(planner.Claim(0, 0, -1, 10).Lost); // 非法位置
        Assert.True(planner.Claim(0, 0, 0, 0).Lost); // 非法长度
        Assert.True(planner.Claim(0, 99, 0, 10).Lost); // 非法段号
    }

    [Fact]
    public void Release_RollsBackReservedAndUnassigns()
    {
        var planner = new DynamicSegmentPlanner(MiB, clock: () => 0);
        planner.Assign(0);
        planner.Claim(0, 0, 0, 4096);
        planner.Release(0, 0);
        Assert.Equal([new DownloadSegment(0, MiB - 1)], planner.Snapshot()); // Completed 回滚到 0
        var next = planner.Assign(1);
        Assert.Equal(0, next!.Value.RequestStart); // 未 commit 的 claim 由新主重取
        planner.Release(5, 0); // 非段主 Release 无操作
        Assert.False(planner.Claim(1, 0, 0, 10).Lost);
    }

    [Fact]
    public void Resume_ArbitraryGranularityAndOldStaticLayout_InvalidCoverageThrows()
    {
        var oldLayout = new DownloadSegment[]
        {
            new(0, 2 * MiB - 1, 512 * KiB), // 旧静态 2MiB 段表，半段已落盘
            new(2 * MiB, 3 * MiB - 1)
        };
        var planner = DynamicSegmentPlanner.Resume(3 * MiB, oldLayout, clock: () => 0);
        Assert.Equal(oldLayout, planner.Snapshot());
        var assignment = planner.Assign(0);
        Assert.Equal(new Assignment(0, 512 * KiB, 2 * MiB - 1), assignment!.Value); // 从 Cursor 续取

        Assert.Throws<ArgumentException>(() => DynamicSegmentPlanner.Resume(3 * MiB,
            [new DownloadSegment(0, MiB - 1), new DownloadSegment(2 * MiB, 3 * MiB - 1)])); // 有空洞
        Assert.Throws<ArgumentException>(() => DynamicSegmentPlanner.Resume(3 * MiB,
            [new DownloadSegment(0, 3 * MiB - 1, 4 * MiB)])); // Completed 越界
        Assert.Throws<ArgumentException>(() => DynamicSegmentPlanner.Resume(3 * MiB,
            [new DownloadSegment(0, 2 * MiB - 1), new DownloadSegment(MiB, 3 * MiB - 1)])); // 重叠
        Assert.Throws<ArgumentException>(() => DynamicSegmentPlanner.Resume(3 * MiB,
            [new DownloadSegment(0, 4 * MiB - 1)])); // 总长不符
        Assert.Throws<ArgumentException>(() => DynamicSegmentPlanner.Resume(3 * MiB,
            [new DownloadSegment(0, MiB - 1), new DownloadSegment(MiB, MiB - 1),
             new DownloadSegment(MiB, 3 * MiB - 1)])); // 零长段
        Assert.Throws<ArgumentException>(() => DynamicSegmentPlanner.Resume(3 * MiB, []));
    }

    [Fact]
    public void Preempt_RollsBackUncommittedClaim_NewOwnerStartsAtCursor()
    {
        long now = 1000;
        var planner = DynamicSegmentPlanner.Resume(2 * MiB,
            [new DownloadSegment(0, MiB - 1), new DownloadSegment(MiB, 2 * MiB - 1)],
            minSplitBytes: 2 * MiB, clock: () => now);
        planner.Assign(0);
        Drain(planner, 0, 0, 0, MiB);
        planner.Assign(1);
        Assert.Equal(4096, planner.Claim(1, 1, MiB, 4096).GrantedBytes); // 已 claim 未 commit（在途）
        now += 1000;
        var preempted = planner.Assign(0);
        Assert.Equal(new Assignment(1, MiB, 2 * MiB - 1), preempted!.Value); // 新主从 Cursor 而非 Reserved 重取
        Assert.Equal([new DownloadSegment(0, MiB - 1, MiB), new DownloadSegment(MiB, 2 * MiB - 1)],
            planner.Snapshot()); // 在途 4096 字节不落盘
    }

    [Fact]
    public void CrashResume_DropsUncommittedClaim_CompletedExcludesInFlightBytes()
    {
        var planner = new DynamicSegmentPlanner(MiB, clock: () => 0);
        planner.Assign(0);
        planner.Claim(0, 0, 0, 8192); // 在途未 commit
        var snapshot = planner.Snapshot();
        Assert.Equal([new DownloadSegment(0, MiB - 1)], snapshot); // Completed=0，不含在途字节
        var resumed = DynamicSegmentPlanner.Resume(MiB, snapshot, clock: () => 0);
        var assignment = resumed.Assign(0);
        Assert.Equal(0, assignment!.Value.RequestStart);
        Assert.Equal(8192, resumed.Claim(0, 0, 0, 8192).GrantedBytes); // 崩溃后重 claim 成功
    }

    [Fact]
    public void Assign_PrefersSegmentAdjacentToLastCompletedEnd()
    {
        var planner = DynamicSegmentPlanner.Resume(3 * MiB,
            [new DownloadSegment(0, MiB - 1), new DownloadSegment(MiB, 2 * MiB - 1),
             new DownloadSegment(2 * MiB, 3 * MiB - 1)], clock: () => 0);
        planner.Assign(0);
        Drain(planner, 0, 0, 0, MiB); // worker0 完成 [0,1MiB)
        var next = planner.Assign(0);
        Assert.Equal(1, next!.Value.SegmentIndex); // 局部性：衔接上次完成处而非最靠前
    }

    [Fact]
    public void CommitCompletion_TracksProgressReceivingAndVersion()
    {
        var planner = new DynamicSegmentPlanner(24 * KiB, minSplitBytes: MiB, clock: () => 0);
        var version0 = planner.Version;
        planner.Assign(0);
        planner.MarkReceiving(0, 0);
        Assert.Equal(1, planner.ReceivingCount);
        Assert.True(planner.Version > version0);

        var claim = planner.Claim(0, 0, 0, 24 * (int)KiB);
        Assert.Equal(24 * KiB, claim.GrantedBytes);
        planner.Commit(0, 0, 0, 24 * (int)KiB);
        Assert.True(planner.IsComplete);
        Assert.Equal(24 * KiB, planner.CompletedBytes);
        Assert.Equal(0, planner.ReceivingCount); // 完成即清收数标记
        Assert.Null(planner.Assign(0));
        Assert.Throws<InvalidOperationException>(() => planner.Commit(0, 0, 0, 1)); // 完成后无归属
    }

    /// <summary>确定性写完一个段：claim→commit 循环（测试助手）。</summary>
    private static void Drain(DynamicSegmentPlanner planner, int worker, int segment, long start, long end)
    {
        var position = start;
        while (position < end)
        {
            var length = (int)Math.Min(64 * KiB, end - position);
            var claim = planner.Claim(worker, segment, position, length);
            Assert.False(claim.Lost);
            planner.Commit(worker, segment, position, claim.GrantedBytes);
            position += claim.GrantedBytes;
        }
    }
}