namespace 烧录上位机程序.Models.IpPurity;

/// <summary>评分输入。空字段表示上游对象里的缺项（不是“未命中”）。</summary>
public sealed record IpPurityScoreRequest(
    string? ExitIp,
    IpPurityScoreIntel? Intel,
    IReadOnlyList<IpPurityScoreService>? Services,
    IpPurityScoreConsistency? Consistency,
    IpPurityScoreLeak? Leak);

public sealed record IpPurityScoreIntel(
    string? Type,
    string? Confidence,
    string? Source,
    bool? Hosting,
    bool? Vpn,
    bool? Proxy,
    bool? Tor,
    bool? Relay,
    bool? Mobile);

public sealed record IpPurityScoreService(string? Name, string? Status, bool? RegionListed);

public sealed record IpPurityScoreConsistency(bool? Mismatch, int DistinctV4, int DistinctV6);

/// <summary>泄漏输入。State 为空表示上游没有给出结论；Settled / ExitKnown 为 null 表示字段缺失。</summary>
public sealed record IpPurityScoreLeak(
    string? State,
    IReadOnlyList<string>? Leaked,
    bool? Settled,
    bool? ExitKnown);

public sealed record IpPurityScoreItem(
    string Group,
    string Label,
    int Deduction,
    bool Gate,
    bool Missing,
    string Evidence,
    bool Capped,
    bool Absorbed);

public sealed record IpPurityGrade(string Key, string Label, string Text);

public sealed record IpPurityScore(
    int? Total,
    IpPurityGrade? Grade,
    IReadOnlyList<IpPurityScoreItem> Items,
    IReadOnlyList<IpPurityScoreItem> StatusItems,
    IReadOnlyList<string> Advice,
    string Note,
    string Footnote);
