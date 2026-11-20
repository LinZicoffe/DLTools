using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

/// <summary>主机连接上限学习器的纯逻辑验证：观察计数、峰值激活、只升不降、零收数忽略、过期遗忘、主机键归一。</summary>
public class DownloadHostLimitsTests
{
    private const string Url = "http://fw.example.com:8080/a.bin";

    [Fact]
    public void TwoRefusalsWhileReceiving_ActivateCapAtPeakWorking()
    {
        var limits = new DownloadHostLimits(() => 0);
        limits.ReportRefused(Url, 2);
        Assert.Null(limits.GetCap(Url)); // 单次观察不激活：可能是偶发抖动
        limits.ReportRefused(Url, 2);
        Assert.Equal(2, limits.GetCap(Url));
    }

    [Fact]
    public void SingleRefusal_NeverActivatesCap()
    {
        var limits = new DownloadHostLimits(() => 0);
        limits.ReportRefused(Url, 4);
        Assert.Null(limits.GetCap(Url));
    }

    [Fact]
    public void RefusalWithNobodyReceiving_DoesNotCount()
    {
        var limits = new DownloadHostLimits(() => 0);
        for (var i = 0; i < 5; i++) limits.ReportRefused(Url, 0); // 整站故障而非并发限制
        Assert.Null(limits.GetCap(Url));
    }

    [Fact]
    public void WorkingReports_RaiseLearnedCapButNeverLowerIt()
    {
        var limits = new DownloadHostLimits(() => 0);
        limits.ReportRefused(Url, 2);
        limits.ReportRefused(Url, 2);
        Assert.Equal(2, limits.GetCap(Url));
        limits.ReportWorking(Url, 1);
        Assert.Equal(2, limits.GetCap(Url)); // 只升不降
        limits.ReportWorking(Url, 3);
        Assert.Equal(3, limits.GetCap(Url)); // 服务器放宽了限制：跟随上调
    }

    [Fact]
    public void IdleEntry_ExpiresAfterTenMinutesAndRelearnsFromZero()
    {
        long now = 1_000_000;
        var limits = new DownloadHostLimits(() => now);
        limits.ReportRefused(Url, 2);
        limits.ReportRefused(Url, 2);
        Assert.Equal(2, limits.GetCap(Url));

        now += (long)DownloadHostLimits.ForgetAfter.TotalMilliseconds; // 恰好到期
        Assert.Null(limits.GetCap(Url));
        limits.ReportRefused(Url, 3); // 过期后从零学起：单次观察仍不激活
        Assert.Null(limits.GetCap(Url));
        limits.ReportRefused(Url, 3);
        Assert.Equal(3, limits.GetCap(Url));
    }

    [Fact]
    public void HostKey_NormalizesCaseAndPortButSeparatesHosts()
    {
        var limits = new DownloadHostLimits(() => 0);
        limits.ReportRefused("http://FW.Example.COM:8080/a.bin", 2);
        limits.ReportRefused("http://fw.example.com:8080/b.bin", 2);
        Assert.Equal(2, limits.GetCap("http://fw.example.com:8080/other/path.bin")); // 同主机不同路径共享
        Assert.Null(limits.GetCap("http://fw.example.com:9090/a.bin")); // 端口不同视为不同主机
        Assert.Null(limits.GetCap("http://other.example.com:8080/a.bin"));
        Assert.Equal("fw.example.com:8080", DownloadHostLimits.HostKeyOf("http://FW.Example.COM:8080/x"));
    }
}