using System.Text.Json;
using System.Text.RegularExpressions;
using 烧录上位机程序.Models.IpPurity;

namespace 烧录上位机程序.Services.IpPurity;

/// <summary>上游 data.js 里的静态资料：AI 服务的地区支持列表、已知属于机房的 ASN、机房关键词与显示文案。</summary>
public static class IpPurityData
{
    /// <summary>地区支持列表的核对日期，对应上游 regionsChecked。列表是快照，不会自动更新。</summary>
    public const string RegionsChecked = "2026-11-24";

    private const string OpenAiCodes =
        "AD AE AF AG AL AM AO AR AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BR BS BT BW BZ CA CD CF CG CH CI CL CM CO CR CV CY CZ DE DJ DK DM DO DZ EC EE EG ER ES ET FI FJ FM FO FR GA GB GD GE GF GH GL GM GN GP GQ GR GT GW GY HN HR HT HU ID IE IL IN IQ IS IT JM JO JP KE KG KH KI KM KN KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MQ MR MT MU MV MW MX MY MZ NA NC NE NG NI NL NO NP NR NZ OM PA PE PF PG PH PK PL PM PS PT PW PY QA RE RO RS RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SZ TD TF TG TH TJ TL TM TN TO TR TT TV TW TZ UA UG US UY UZ VA VC VN VU WF WS YE YT ZA ZM ZW";

    private const string AnthropicCodes =
        "AD AE AG AL AM AO AR AT AU AZ BA BB BD BE BF BG BH BI BJ BN BO BR BS BT BW BZ CA CD CF CG CH CI CL CM CO CR CV CY CZ DE DJ DK DM DO DZ EC EE EG ER ES ET FI FJ FM FR GA GB GD GE GH GM GN GQ GR GT GW GY HN HR HT HU ID IE IL IN IQ IS IT JM JO JP KE KG KH KI KM KN KR KW KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MG MH MK ML MN MR MT MU MV MW MX MY MZ NA NE NG NI NL NO NP NR NZ OM PA PE PG PH PK PL PS PT PW PY QA RO RS RW SA SB SC SD SE SG SI SK SL SM SN SO SR SS ST SV SZ TD TG TH TJ TL TM TN TO TR TT TV TW TZ UA UG US UY UZ VA VC VN VU WS ZA ZM ZW";

    public static IReadOnlySet<string> OpenAiRegions { get; } = ToSet(OpenAiCodes);

    public static IReadOnlySet<string> AnthropicRegions { get; } = ToSet(AnthropicCodes);

    /// <summary>已知属于云服务 / 托管机房的 ASN（不带 AS 前缀）。</summary>
    public static IReadOnlySet<string> HostingAsns { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "16509", "14618", "8075", "15169", "14061", "24940", "16276", "20473",
        "45102", "45090", "31898", "45338", "63949",
    };

    /// <summary>命中即推断为机房的关键词。含拉丁字母或数字的按整词匹配，纯中文的按子串匹配。</summary>
    public static IReadOnlyList<string> Keywords { get; } =
    [
        "aws", "amazon", "google cloud", "microsoft", "azure", "ovh", "hetzner", "digitalocean", "vultr",
        "alibaba", "tencent", "huawei", "oracle", "linode", "akamai", "hosting", "datacenter",
        "阿里云", "腾讯云", "华为云",
    ];

    public static IReadOnlyDictionary<string, string> TypeLabels { get; } = new Dictionary<string, string>
    {
        [IpPurityCodes.Hosting] = "机房 / 云服务",
        [IpPurityCodes.Isp] = "住宅 / 运营商宽带",
        [IpPurityCodes.Business] = "商业宽带",
        [IpPurityCodes.Education] = "教育网",
    };

    public static IReadOnlyDictionary<string, string> ConfidenceLabels { get; } = new Dictionary<string, string>
    {
        [IpPurityCodes.High] = "准确度高",
        [IpPurityCodes.Medium] = "准确度中",
        [IpPurityCodes.Low] = "仅供参考",
    };

    public static bool IsListed(string regionSet, string countryCode) =>
        regionSet == "anthropic"
            ? AnthropicRegions.Contains(countryCode)
            : OpenAiRegions.Contains(countryCode);

    /// <summary>与 data.js isRegionError 一致。permission_error 本身不算地区限制，除非文案点名了国家或地区。</summary>
    public static bool IsRegionError(string serviceId, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return false;
        var hasError = body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object;
        if (serviceId == "chatgpt")
        {
            if (!hasError) return false;
            return StringProp(error, "code") == "unsupported_country_region_territory"
                || StringProp(error, "type") == "unsupported_country_region_territory";
        }

        if (StringProp(body, "type") != "error") return false;
        var message = hasError ? StringProp(error, "message") : null;
        return message is not null && RegionWord.IsMatch(message);
    }

    public static bool IsPermissionError(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || StringProp(body, "type") != "error") return false;
        return body.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && StringProp(error, "type") == "permission_error";
    }

    public static string? StringProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static readonly Regex RegionWord = new(
        "country|region|territor",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static IReadOnlySet<string> ToSet(string codes) =>
        new HashSet<string>(codes.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
