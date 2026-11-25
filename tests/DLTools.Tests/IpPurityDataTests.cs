using Xunit;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Services.IpPurity;

namespace DLTools.Tests;

public class IpPurityDataTests
{
    // 以下数字与取值来自上游 data.js（2026-11-24 快照），已用脚本逐字节核对。
    [Fact]
    public void Regions_MatchUpstreamSnapshot()
    {
        Assert.Equal("2026-11-24", IpPurityData.RegionsChecked);
        Assert.Equal(208, IpPurityData.OpenAiRegions.Count);
        Assert.Equal(185, IpPurityData.AnthropicRegions.Count);

        foreach (var region in IpPurityData.OpenAiRegions.Concat(IpPurityData.AnthropicRegions))
            Assert.Matches("^[A-Z]{2}$", region);

        foreach (var code in new[] { "US", "JP", "SG", "TW", "KR", "IN", "DE", "GB" })
        {
            Assert.Contains(code, IpPurityData.OpenAiRegions);
            Assert.Contains(code, IpPurityData.AnthropicRegions);
        }

        foreach (var code in new[] { "CN", "HK", "MO", "RU", "IR", "KP" })
        {
            Assert.DoesNotContain(code, IpPurityData.OpenAiRegions);
            Assert.DoesNotContain(code, IpPurityData.AnthropicRegions);
        }

        // OpenAI 的列表是 Anthropic 的超集，多出的正是下面这些
        var onlyOpenAi = IpPurityData.OpenAiRegions.Except(IpPurityData.AnthropicRegions).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            "AF AW AX BL BM FO GF GL GP KY MF MM MQ NC PF PM RE SH SJ TF WF YE YT",
            string.Join(" ", onlyOpenAi));
        Assert.Empty(IpPurityData.AnthropicRegions.Except(IpPurityData.OpenAiRegions));
    }

    [Fact]
    public void HostingTables_MatchUpstream()
    {
        Assert.Equal(13, IpPurityData.HostingAsns.Count);
        Assert.Contains("16509", IpPurityData.HostingAsns); // AWS
        Assert.Contains("14618", IpPurityData.HostingAsns); // Amazon
        Assert.Contains("63949", IpPurityData.HostingAsns); // Microsoft
        Assert.DoesNotContain("AS16509", IpPurityData.HostingAsns);

        Assert.Equal([
            "aws", "amazon", "google cloud", "microsoft", "azure", "ovh", "hetzner", "digitalocean", "vultr",
            "alibaba", "tencent", "huawei", "oracle", "linode", "akamai", "hosting", "datacenter",
            "阿里云", "腾讯云", "华为云",
        ], IpPurityData.Keywords);
    }

    [Fact]
    public void Labels_CoverEveryKnownTypeAndConfidence()
    {
        Assert.Equal("机房 / 云服务", IpPurityData.TypeLabels[IpPurityCodes.Hosting]);
        Assert.Equal("住宅 / 运营商宽带", IpPurityData.TypeLabels[IpPurityCodes.Isp]);
        Assert.Equal("商业宽带", IpPurityData.TypeLabels[IpPurityCodes.Business]);
        Assert.Equal("教育网", IpPurityData.TypeLabels[IpPurityCodes.Education]);
        Assert.Equal("准确度高", IpPurityData.ConfidenceLabels[IpPurityCodes.High]);
        Assert.Equal("准确度中", IpPurityData.ConfidenceLabels[IpPurityCodes.Medium]);
        Assert.Equal("仅供参考", IpPurityData.ConfidenceLabels[IpPurityCodes.Low]);
        Assert.Equal(4, IpPurityData.TypeLabels.Count);
        Assert.Equal(3, IpPurityData.ConfidenceLabels.Count);
    }
}
