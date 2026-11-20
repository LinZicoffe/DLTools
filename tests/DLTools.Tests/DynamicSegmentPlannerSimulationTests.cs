using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>随机操作序列模拟（20 个固定种子，可复现）：8 个虚拟 worker 随机 Assign/Claim+Commit/
/// 在途 Claim（延迟 Commit）/Release/MarkReceiving/时钟推进/崩溃恢复（Snapshot→Resume），
/// 每步校验段表不变量；结束后确定性排空，断言每个字节恰好 Commit 一次（覆盖数组只在 Commit 时递增）。
/// 在途 claim 使抢占/崩溃回滚在 Reserved&gt;Cursor 状态下被真实行使。
/// 失败时把种子与操作序列 dump 进异常消息便于复现。</summary>
public class DynamicSegmentPlannerSimulationTests
{
    private const int WorkerCount = 8;
    private const int RandomSteps = 300;
    private const int ChunkLimit = 64 * 1024;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void RandomOpsWithCrashResume_EveryByteCommittedExactlyOnce(int seed)
    {
        var random = new Random(seed);
        var total = random.Next(1024, 8 * 1024 * 1024 + 1);
        long now = 0;
        var planner = new DynamicSegmentPlanner(total, clock: () => now);
        var committed = new byte[total];
        var owned = new int[WorkerCount];
        var nextPos = new long[WorkerCount];
        var segEnd = new long[WorkerCount]; // 排他段尾（可能因对半劈过期，Claim Lost 自愈）
        var pending = new int[WorkerCount]; // 已 claim 未 commit 的在途字节数
        Array.Fill(owned, -1);
        var operations = new List<string>();

        try
        {
            for (var step = 0; step < RandomSteps; step++)
            {
                now += random.Next(0, 50);
                var worker = random.Next(WorkerCount);
                var action = random.Next(14);
                operations.Add($"{step}:{action}:w{worker}");
                switch (action)
                {
                    case 0:
                    case 1:
                    case 2:
                        var assignment = planner.Assign(worker);
                        if (assignment is { } value)
                        {
                            if (value.SegmentIndex != owned[worker]) pending[worker] = 0; // 换段即弃在途
                            owned[worker] = value.SegmentIndex;
                            nextPos[worker] = value.RequestStart;
                            segEnd[worker] = value.RequestEnd + 1;
                        }
                        else
                        {
                            owned[worker] = -1;
                            pending[worker] = 0;
                        }
                        break;
                    case 3:
                    case 4:
                    case 5:
                    case 6:
                        if (owned[worker] < 0) break;
                        // 兑现写入：始终先重 Claim 再 Commit（引擎接线契约）；在途 claim 若已被抢占/劈走则 Lost
                        var length = pending[worker] > 0 ? pending[worker] : random.Next(1, ChunkLimit + 1);
                        pending[worker] = 0;
                        var claim = planner.Claim(worker, owned[worker], nextPos[worker], length);
                        if (claim.Lost)
                        {
                            owned[worker] = -1;
                            break;
                        }
                        planner.Commit(worker, owned[worker], nextPos[worker], claim.GrantedBytes);
                        for (var i = 0; i < claim.GrantedBytes; i++) committed[nextPos[worker] + i]++;
                        nextPos[worker] += claim.GrantedBytes;
                        if (nextPos[worker] == segEnd[worker]) owned[worker] = -1;
                        break;
                    case 7:
                        if (owned[worker] >= 0)
                        {
                            planner.Release(worker, owned[worker]);
                            owned[worker] = -1;
                            pending[worker] = 0;
                        }
                        break;
                    case 8:
                        if (owned[worker] >= 0) planner.MarkReceiving(worker, owned[worker]);
                        break;
                    case 9: // 崩溃恢复：journal 快照重建，归属与未 commit 的 claim 全部回滚
                        planner = DynamicSegmentPlanner.Resume(total, planner.Snapshot(), clock: () => now);
                        Array.Fill(owned, -1);
                        Array.Fill(pending, 0);
                        break;
                    case 10:
                        now += 500; // 大跨步推进时钟，触发抢占宽限期过期
                        break;
                    case 11:
                        planner.SetProbeTtfb(random.Next(0, 200));
                        break;
                    case 12:
                    case 13: // 在途 claim：只 Claim 不 Commit，Reserved 领先 Cursor，跨步骤保留
                        if (owned[worker] < 0) break;
                        var inflight = planner.Claim(worker, owned[worker], nextPos[worker],
                            random.Next(1, ChunkLimit + 1));
                        if (inflight.Lost)
                        {
                            owned[worker] = -1;
                            pending[worker] = 0;
                        }
                        else pending[worker] = inflight.GrantedBytes;
                        break;
                }
                AssertInvariants(planner, total);
            }

            DrainDeterministically(planner, committed);

            long misplaced = 0;
            for (var i = 0; i < total; i++)
                if (committed[i] != 1) misplaced++;
            Assert.Equal(0, misplaced);
            Assert.True(planner.IsComplete);
            Assert.Equal(total, planner.CompletedBytes);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"seed={seed} total={total} ops=[{string.Join(" ", operations)}]", error);
        }
    }

    private static void AssertInvariants(DynamicSegmentPlanner planner, long total)
    {
        long covered = 0;
        long completedSum = 0;
        foreach (var segment in planner.Snapshot())
        {
            Assert.Equal(covered, segment.Start);
            Assert.InRange(segment.Completed, 0, segment.Length);
            covered += segment.Length;
            completedSum += segment.Completed;
        }
        Assert.Equal(total, covered);
        Assert.Equal(completedSum, planner.CompletedBytes);
        Assert.InRange(planner.ReceivingCount, 0, WorkerCount);
    }

    /// <summary>确定性排空：轮流给每个 worker 派活并整段写完；Lost 即规划器 bug（排空期无并发拆分）。</summary>
    private static void DrainDeterministically(DynamicSegmentPlanner planner, byte[] committed)
    {
        var passes = 0;
        while (!planner.IsComplete)
        {
            if (++passes > 10_000) throw new InvalidOperationException("排空不收敛");
            for (var worker = 0; worker < WorkerCount; worker++)
            {
                if (planner.Assign(worker) is not { } assignment) continue;
                var position = assignment.RequestStart;
                while (position <= assignment.RequestEnd)
                {
                    var length = (int)Math.Min(ChunkLimit, assignment.RequestEnd - position + 1);
                    var claim = planner.Claim(worker, assignment.SegmentIndex, position, length);
                    Assert.False(claim.Lost);
                    planner.Commit(worker, assignment.SegmentIndex, position, claim.GrantedBytes);
                    for (var i = 0; i < claim.GrantedBytes; i++) committed[position + i]++;
                    position += claim.GrantedBytes;
                }
            }
        }
    }
}