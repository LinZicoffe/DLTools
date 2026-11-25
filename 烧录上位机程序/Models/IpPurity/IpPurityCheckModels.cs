namespace 烧录上位机程序.Models.IpPurity;

public enum IpPurityPhase
{
    Running,
    Done,
    Failed
}

/// <summary>一个 AI 服务的判定，字段含义与 checks.js 的 verdict() 对齐。</summary>
public sealed record IpPurityServiceVerdict(
    string Status,
    string Label,
    bool? RegionListed,
    string RegionText,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Notes,
    bool TraceReachable,
    string? TraceIp,
    string? TraceLoc,
    string TraceText,
    bool ApiReachable,
    string ApiText,
    int ApiStatus);

/// <summary>一个 AI 服务的检测结果。分流时 SplitIntel / SplitScore 描述该服务实际看到的出口。</summary>
public sealed record IpPurityServiceOutcome(
    string ServiceId,
    string Name,
    string Site,
    IpPurityServiceVerdict? Verdict,
    IpPurityIntel? SplitIntel,
    IpPurityScore? SplitScore);

public sealed record IpPurityUdpObservation(
    string Host,
    int Port,
    bool Foreign,
    string? Address,
    long? LatencyMs,
    string? Error);

/// <summary>UDP 出口结论。State 取 leak / clean / unknown。Leaked 只含境外、与出口同协议且不同的公网地址。</summary>
public sealed record IpPurityUdpReport(
    string State,
    IReadOnlyList<string> Leaked,
    IReadOnlyList<IpPurityUdpObservation> Observations);

/// <summary>编排器抛给界面的增量快照。列表都是新实例，界面线程可以放心绑定。</summary>
public sealed record IpPuritySnapshot(
    IpPurityPhase Phase,
    int Completed,
    string StageText,
    IpPurityExit? Exit,
    bool ExitSettled,
    IpPurityIntel? Intel,
    bool IntelSettled,
    IReadOnlyList<IpPurityServiceOutcome> Services,
    IReadOnlyList<IpPurityUdpObservation> Udp,
    bool UdpSettled,
    string? Ipv6,
    bool Ipv6Settled,
    IpPurityExitSummary? ExitSummary,
    bool SummarySettled,
    IpPurityScore? Score,
    bool TimedOut,
    TimeSpan Elapsed);
