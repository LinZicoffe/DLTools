using System.Text.Json;
using Xunit;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Services.IpPurity;

namespace DLTools.Tests;

public class IpPurityScoringTests
{
    [Fact]
    public void V1_MatchesUpstreamScoreJsOnFixedCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "IpPurity", "score-v1.json");
        var fixture = JsonSerializer.Deserialize<FixtureFile>(File.ReadAllText(path), JsonOptions)!;
        var failures = new List<string>();
        foreach (var item in fixture.Cases)
        {
            var actual = IpPurityScoring.ComputeV1(ToRequest(item.Input));
            var expected = item.Expected;
            if (actual.Total != expected.Total || actual.Grade?.Key != expected.GradeKey
                || actual.Grade?.Label != expected.GradeLabel || actual.Grade?.Text != expected.GradeText
                || actual.Note != expected.Note || actual.Footnote != expected.Footnote
                || !actual.Advice.SequenceEqual(expected.Advice)
                || actual.Items.Count != expected.Items.Count)
            {
                failures.Add(item.Name + " total " + actual.Total + " vs " + expected.Total + " / " + actual.Grade?.Text);
                continue;
            }
            for (var i = 0; i < actual.Items.Count; i++)
            {
                var left = actual.Items[i];
                var right = expected.Items[i];
                if (left.Group != right.Group || left.Label != right.Label || left.Deduction != right.Deduction
                    || left.Gate != right.Gate || left.Missing != right.Missing || left.Evidence != right.Evidence
                    || left.Capped != right.Capped || left.Absorbed != right.Absorbed)
                {
                    failures.Add(item.Name + " item " + left.Label + " ≠ " + right.Label + " / " + left.Evidence + " ≠ " + right.Evidence);
                    break;
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(8)));
    }

    [Fact]
    public void V2_ScoresOnlyIdentityAndPrivacy()
    {
        var residential = IpPurityScoring.ComputeV2(Scene(false, "isp", "high", false, false, "ok", "ok", false, "clean"));
        var cleanDc = IpPurityScoring.ComputeV2(Scene(true, "hosting", "high", false, false, "ok", "ok", false, "clean"));
        var proxy = IpPurityScoring.ComputeV2(Scene(true, "hosting", "high", false, true, "risk", "region", true, "unknown"));
        var mainland = IpPurityScoring.ComputeV2(Scene(false, "isp", "high", false, false, "blocked", "blocked", false, "clean"));
        var split = IpPurityScoring.ComputeV2(Scene(true, "hosting", "high", true, false, "risk", "ok", true, "leak"));
        var unknown = IpPurityScoring.ComputeV2(new IpPurityScoreRequest(
            "192.0.2.44",
            new IpPurityScoreIntel(null, "medium", "ip-api.com", false, null, false, null, null, false),
            [new("ChatGPT", "ok", true), new("Claude", "ok", true)],
            new IpPurityScoreConsistency(false, 1, 0),
            new IpPurityScoreLeak("unknown", [], true, true)));

        Assert.Equal(100, residential.Total);
        Assert.Equal("纯净", residential.Grade!.Label);
        Assert.Equal(65, cleanDc.Total);
        Assert.Equal("一般", cleanDc.Grade!.Label);
        Assert.Equal(43, proxy.Total);
        Assert.Equal("高风险", proxy.Grade!.Label);
        Assert.Contains("代理标记", proxy.Grade.Text, StringComparison.Ordinal);
        Assert.Equal(100, mainland.Total);
        Assert.Contains("住宅宽带", mainland.Grade!.Text, StringComparison.Ordinal);
        Assert.Equal(41, split.Total);
        Assert.Contains("UDP 流量绕过了代理", split.Grade!.Text, StringComparison.Ordinal);
        Assert.Null(unknown.Total);
        Assert.Contains("没有数据", unknown.Advice[0], StringComparison.Ordinal);
        Assert.DoesNotContain(proxy.Items, item => item.Group == "AI 服务");
        Assert.Contains(proxy.StatusItems, item => item.Group == "AI 服务");
    }

    private static IpPurityScoreRequest Scene(
        bool hosting, string type, string confidence, bool vpn, bool proxy,
        string chatgpt, string claude, bool mismatch, string udp) =>
        new(
            "203.0.113.7",
            new IpPurityScoreIntel(type, confidence, "IPinfo", hosting, vpn, proxy, false, false, false),
            [new("ChatGPT", chatgpt, true), new("Claude", claude, true)],
            new IpPurityScoreConsistency(mismatch, mismatch ? 2 : 1, 0),
            new IpPurityScoreLeak(udp, udp == "leak" ? ["192.0.2.44"] : [], true, true));

    private static IpPurityScoreRequest ToRequest(InputDto input) => new(
        input.ExitIp,
        input.Intel is null ? null : new IpPurityScoreIntel(
            input.Intel.Type, input.Intel.Confidence, input.Intel.Source,
            input.Intel.Flags?.Hosting, input.Intel.Flags?.Vpn, input.Intel.Flags?.Proxy,
            input.Intel.Flags?.Tor, input.Intel.Flags?.Relay, input.Intel.Flags?.Mobile),
        input.Services.Select(service => new IpPurityScoreService(service.Name, service.Status, service.RegionListed)).ToList(),
        input.Consistency is null ? null : new IpPurityScoreConsistency(input.Consistency.Mismatch, input.Consistency.DistinctV4, input.Consistency.DistinctV6),
        input.Leak is null ? null : new IpPurityScoreLeak(input.Leak.State, input.Leak.Leaked, input.Leak.Settled, input.Leak.ExitKnown));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class FixtureFile
    {
        public List<FixtureCase> Cases { get; set; } = [];
    }

    private sealed class FixtureCase
    {
        public string Name { get; set; } = "";
        public InputDto Input { get; set; } = new();
        public ExpectedDto Expected { get; set; } = new();
    }

    private sealed class InputDto
    {
        public string? ExitIp { get; set; }
        public IntelDto? Intel { get; set; }
        public List<ServiceDto> Services { get; set; } = [];
        public ConsistencyDto? Consistency { get; set; }
        public LeakDto? Leak { get; set; }
    }

    private sealed class IntelDto
    {
        public string? Type { get; set; }
        public string? Confidence { get; set; }
        public string? Source { get; set; }
        public FlagsDto? Flags { get; set; }
    }

    private sealed class FlagsDto
    {
        public bool? Hosting { get; set; }
        public bool? Vpn { get; set; }
        public bool? Proxy { get; set; }
        public bool? Tor { get; set; }
        public bool? Relay { get; set; }
        public bool? Mobile { get; set; }
    }

    private sealed class ServiceDto
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public bool? RegionListed { get; set; }
    }

    private sealed class ConsistencyDto
    {
        public bool? Mismatch { get; set; }
        public int DistinctV4 { get; set; }
        public int DistinctV6 { get; set; }
    }

    private sealed class LeakDto
    {
        public string? State { get; set; }
        public List<string>? Leaked { get; set; }
        public bool? Settled { get; set; }
        public bool? ExitKnown { get; set; }
    }

    private sealed class ExpectedDto
    {
        public int? Total { get; set; }
        public string? GradeKey { get; set; }
        public string? GradeLabel { get; set; }
        public string? GradeText { get; set; }
        public string Note { get; set; } = "";
        public string Footnote { get; set; } = "";
        public List<string> Advice { get; set; } = [];
        public List<ItemDto> Items { get; set; } = [];
    }

    private sealed class ItemDto
    {
        public string Group { get; set; } = "";
        public string Label { get; set; } = "";
        public int Deduction { get; set; }
        public bool Gate { get; set; }
        public bool Missing { get; set; }
        public string Evidence { get; set; } = "";
        public bool Capped { get; set; }
        public bool Absorbed { get; set; }
    }
}
