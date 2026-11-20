using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>慢连接 shed 判定纯逻辑（注入时钟，SelectVictims 按 500ms 监控节拍推进）。</summary>
public class DownloadShedPolicyTests
{
    private const long MiB = 1024 * 1024;
    private const long KiB = 1024;

    [Fact]
    public void SelectVictims_SlowBelowQuarterBaselineFor2s_SelectsOnlySlowWorker()
    {
        var now = 0L;
        var policy = new DownloadShedPolicy(() => now);
        for (var tick = 1; tick <= 5; tick++)
        {
            now = tick * 500;
            policy.RecordSample(0, MiB, 500, 0, 100 * MiB);   // 快：2MiB/s
            policy.RecordSample(1, 4 * KiB, 500, 0, 10 * MiB); // 慢：8KiB/s，剩余 10MiB
            var victims = policy.SelectVictims(now);
            // 慢连接累计净读 2s（第 4 拍）才成熟；此前不判
            if (tick < 4) Assert.Empty(victims);
            else Assert.Equal([1], victims);
        }
    }

    [Fact]
    public void CurrentBaseline_ShortNetReadOrFewBytes_ExcludedFromBestSingle()
    {
        var now = 0L;
        var policy = new DownloadShedPolicy(() => now);
        for (var tick = 1; tick <= 4; tick++)
        {
            now = tick * 500;
            // worker0 瞬时 20MiB/s 但区间净读仅 50ms（<100ms）→ 不得计入最佳单连接
            policy.RecordSample(0, MiB, 50, 0, 100 * MiB);
            // worker1 512KiB/500ms=1MiB/s，两门槛均满足 → 最佳单连接=1MiB/s
            policy.RecordSample(1, 512 * KiB, 500, 0, 100 * MiB);
            policy.SelectVictims(now);
        }
        // 链路≈3MiB/s ÷2=1.5MiB/s；基准=min(1MiB, 1.5MiB)=1MiB/s。
        // 若短净读被误计入，基准会变成 1.5MiB/s（min 取链路项），断言可证伪
        Assert.Equal(MiB, policy.CurrentBaseline, 3);

        // 变体：区间字节不足 256KiB 同样不计入最佳单连接。
        // w0=200KiB/100ms=2MiB/s（净读达标、字节不达标）；w2=1MiB/50ms 瞬时（净读不达标）；
        // w1=512KiB/500ms=1MiB/s 唯一合格。链路=(200+1024+512)KiB/0.5s÷3≈1.13MiB/s，
        // 夹在 1MiB 与 2MiB 之间：字节不足的 w0 若被误计入，基准变链路项≈1.13MiB/s，断言可证伪
        now = 0;
        var fewBytes = new DownloadShedPolicy(() => now);
        for (var tick = 1; tick <= 4; tick++)
        {
            now = tick * 500;
            fewBytes.RecordSample(0, 200 * KiB, 100, 0, 100 * MiB);
            fewBytes.RecordSample(1, 512 * KiB, 500, 0, 100 * MiB);
            fewBytes.RecordSample(2, MiB, 50, 0, 100 * MiB);
            fewBytes.SelectVictims(now);
        }
        Assert.Equal(MiB, fewBytes.CurrentBaseline, 3);
    }

    [Fact]
    public void SelectVictims_DiskStallOver50ms_ExemptsAllFor500ms()
    {
        var now = 0L;
        var policy = new DownloadShedPolicy(() => now);
        void Tick(long t)
        {
            now = t;
            policy.RecordSample(0, MiB, 500, 0, 100 * MiB);
            policy.RecordSample(1, 4 * KiB, 500, 0, 10 * MiB);
        }
        Tick(500);
        Tick(1000);
        Tick(1500);
        policy.SelectVictims(1500);
        policy.RecordDiskStall(60); // >50ms → 免判至 t=2000
        now = 1800;
        // 免判期内样本（LastSample=1800 ≤ 免判选择时刻 1900）；快者维持链路基准，慢者就此成熟
        policy.RecordSample(0, MiB, 500, 0, 100 * MiB);
        policy.RecordSample(1, 4 * KiB, 500, 0, 10 * MiB);
        Assert.Empty(policy.SelectVictims(1900)); // 免判窗口内不选，且不得更新 _lastSelectMs
        // 1800>1500 仍活跃；若免判早退误更新 _lastSelectMs=1900，该样本被误判不活跃，断言可证伪
        Assert.Equal([1], policy.SelectVictims(2100));

        policy.RecordDiskStall(30); // ≤50ms 不触发免判
        Tick(2500);
        Assert.Equal([1], policy.SelectVictims(2500));

        policy.RecordSample(1, 4 * KiB, 500, 60, 10 * MiB); // 采样内写盘>50ms 同样触发免判
        Assert.Empty(policy.SelectVictims(2600));
    }

    [Fact]
    public void SelectVictims_FinishesWithin2s_NeverSelected()
    {
        var now = 0L;
        var policy = new DownloadShedPolicy(() => now);
        for (var tick = 1; tick <= 5; tick++)
        {
            now = tick * 500;
            policy.RecordSample(0, MiB, 500, 0, 100 * MiB);
            // 慢但只剩 8KiB：8KiB/s 下 1s 即可完成（≤2s）→ 不值得 shed
            policy.RecordSample(1, 4 * KiB, 500, 0, 8 * KiB);
            Assert.Empty(policy.SelectVictims(now));
        }
    }

    [Fact]
    public void SelectVictims_Disabled_NeverSelectsAndSamplesIgnored()
    {
        var now = 0L;
        var policy = new DownloadShedPolicy(() => now) { Enabled = false };
        for (var tick = 1; tick <= 5; tick++)
        {
            now = tick * 500;
            policy.RecordSample(0, MiB, 500, 0, 100 * MiB);
            policy.RecordSample(1, 4 * KiB, 500, 0, 10 * MiB);
            Assert.Empty(policy.SelectVictims(now));
        }
        Assert.Equal(0, policy.CurrentBaseline);
    }

    [Fact]
    public void SelectVictims_SustainedDiskWait_ExcludedFromSpeedNoFalseVictim()
    {
        var now = 0L;
        var policy = new DownloadShedPolicy(() => now);
        for (var tick = 1; tick <= 20; tick++)
        {
            now = tick * 500;
            // A：每区间 10 小块，各净读 10ms+写盘 40ms（单块≤50ms 不触发免判）。
            // 区间净读 100ms 收 260KiB → 净速度≈2.6MiB/s；若按墙钟计只有≈520KiB/s
            for (var chunk = 0; chunk < 10; chunk++)
                policy.RecordSample(0, 26 * KiB, 10, 40, 100 * MiB);
            policy.RecordSample(1, 2 * MiB, 500, 0, 100 * MiB); // B：4MiB/s
            // 基准=min(4MiB, 链路≈4.5MiB÷2≈2.26MiB)=2.26MiB/s。
            // 净速度 2.6MiB×4≥基准 → 不选；墙钟速度 520KiB×4≈2.03MiB<基准 → 会误选，断言可证伪。
            // A 在第 20 拍净读累计 2000ms 成熟，误判会在成熟后暴露
            Assert.Empty(policy.SelectVictims(now));
        }
    }

    [Fact]
    public void SelectVictims_ZeroNetReadWithProgress_InstantSpeedNeverShed()
    {
        var now = 0L;
        var policy = new DownloadShedPolicy(() => now);
        for (var tick = 1; tick <= 4; tick++)
        {
            now = tick * 500;
            policy.RecordSample(0, MiB, 500, 0, 100 * MiB);    // 快：2MiB/s
            policy.RecordSample(1, 4 * KiB, 500, 0, 10 * MiB); // 慢：8KiB/s，第 4 拍成熟
            var victims = policy.SelectVictims(now);
            if (tick < 4) Assert.Empty(victims);
            else Assert.Equal([1], victims);
        }
        // 第 5 拍：慢连接净读计时截断为 0 但有字节进展 → ∞ 速度，不得被 shed
        now = 2500;
        policy.RecordSample(0, MiB, 500, 0, 100 * MiB);
        policy.RecordSample(1, 64 * KiB, 0, 0, 10 * MiB);
        Assert.Empty(policy.SelectVictims(now));
        // 第 6 拍：恢复滴流 → 再次可 shed，证明第 5 拍免选源于 ∞ 而非状态损坏
        now = 3000;
        policy.RecordSample(0, MiB, 500, 0, 100 * MiB);
        policy.RecordSample(1, 4 * KiB, 500, 0, 10 * MiB);
        Assert.Equal([1], policy.SelectVictims(now));
        // 双零：成熟连接整窗零进展记 0 → 可 shed（纯逻辑防御分支，引擎接线不可达）
        now = 3500;
        policy.RecordSample(0, MiB, 500, 0, 100 * MiB);
        policy.RecordSample(1, 0, 0, 0, 10 * MiB);
        Assert.Equal([1], policy.SelectVictims(now));
    }
}