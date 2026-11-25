using System.Text.Json;

namespace 烧录上位机程序.Models.IpPurity;

/// <summary>上游 data.js / score.js 里约定的取值。保留原始字符串而不转成枚举：
/// 第三方返回的未知取值要原样进入评分逻辑，移植结果才能与网页版逐项对得上。</summary>
public static class IpPurityCodes
{
    // IP 类型（intel.type）
    public const string Hosting = "hosting";
    public const string Isp = "isp";
    public const string Business = "business";
    public const string Education = "education";

    // 情报准确度（intel.confidence）
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";

    // AI 服务判定（verdict.status）
    public const string Ok = "ok";
    public const string Risk = "risk";
    public const string Unlisted = "unlisted";
    public const string Region = "region";
    public const string Blocked = "blocked";

    // 泄漏检测结论
    public const string Leak = "leak";
    public const string Clean = "clean";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";
}

/// <summary>一次检测请求。Url 必须是 IpPurityEndpoints 白名单里的固定端点。</summary>
public sealed record IpPurityRequest(
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null,
    TimeSpan? Timeout = null,
    bool ExpectJson = true);

/// <summary>归一化后的失败原因。请求失败也是一种结果，不靠异常传递（取消除外）。</summary>
public sealed record IpPurityError(string Code, string Message);

/// <summary>一次请求的结果，对应上游 api.js 的 request()。Status 为 0 表示没有拿到 HTTP 响应；
/// Data 是响应体按 JSON 解析出的值（只有解析成功才有），文本请求恒为 null。</summary>
public sealed record IpPurityResponse(
    int Status,
    string? Text,
    JsonElement? Data,
    long LatencyMs,
    IpPurityError? Error,
    bool Ok);

/// <summary>隐私与网络属性标记。null 表示数据源没有给出这一项。</summary>
public sealed record IpPurityFlags(bool? Hosting, bool? Vpn, bool? Proxy, bool? Tor, bool? Relay, bool? Mobile);

public sealed record IpPurityGeo(string? Country, string? CountryName, string? Region, string? City);

/// <summary>某个出口 IP 的情报（类型、标记、归属）。Source 为 null 表示没有任何数据源给出资料。</summary>
public sealed record IpPurityIntel(
    string Ip,
    string? Source,
    string? Confidence,
    string? Asn,
    string? AsnName,
    string? Org,
    string? Route,
    string? Hostname,
    string? Timezone,
    string? Type,
    IpPurityFlags? Flags,
    IpPurityGeo? Geo,
    IReadOnlyList<string> Errors);

/// <summary>出口 IP 及其来源。四个来源都失败时 Ip 为 null，Errors 记录每个来源的失败原因。</summary>
public sealed record IpPurityExit(
    string? Ip,
    string? Source,
    long? LatencyMs,
    string? Loc,
    string? Colo,
    string? Hostname,
    string? Org,
    string? Asn,
    string? Timezone,
    IpPurityGeo? Geo,
    IReadOnlyList<string> Errors)
{
    public bool HasIp => Ip is not null;
}

/// <summary>出口 IP 的来源，按回退顺序排列。</summary>
public sealed record IpPurityExitSource(string Name, string Url, bool IsText);

/// <summary>一个 AI 服务的检测端点与资料。</summary>
public sealed record IpPurityServiceInfo(
    string Id,
    string Name,
    string Vendor,
    string Host,
    string Site,
    string TraceUrl,
    string ApiUrl,
    IReadOnlyDictionary<string, string> Headers,
    string RegionSet);

/// <summary>出口比对中的一行。</summary>
public sealed record IpPurityExitRow(string Source, string Ip, bool IsV6, string Align, bool SameAsExit);

/// <summary>各来源出口地址的比对结果，对应上游 checks.js 的 summarizeExits()。
/// Mismatch 为 null 表示同一协议内能比对的地址不够，无法判断。</summary>
public sealed record IpPurityExitSummary(
    IReadOnlyList<IpPurityExitRow> Rows,
    string? RefV4,
    string? RefV6,
    int DistinctV4,
    int DistinctV6,
    bool? Mismatch);
