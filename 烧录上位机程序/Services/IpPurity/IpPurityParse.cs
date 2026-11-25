using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using 烧录上位机程序.Models.IpPurity;

namespace 烧录上位机程序.Services.IpPurity;

/// <summary>地址比较、trace 解析与机房推断。行为对齐 checks.js，供检测与评分共用。</summary>
public static class IpPurityParse
{
    public static string DescribeError(string code)
    {
        if (code == "TIMEOUT") return "请求超时";
        if (code == "ABORTED") return "已取消";
        if (code == "NETWORK") return "网络错误或被拦截";
        if (code == "PARSE") return "返回内容无法解析";
        if (code.StartsWith("HTTP_", StringComparison.Ordinal)) return "服务返回 HTTP " + code[5..];
        return "未知错误";
    }

    public static string FailText(IpPurityResponse? response)
    {
        if (response?.Error is { } error && !string.IsNullOrEmpty(error.Message)) return error.Message;
        return "无有效数据";
    }

    public static Dictionary<string, string> ParseTrace(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var index = trimmed.IndexOf('=');
            if (index > 0) result[trimmed[..index]] = trimmed[(index + 1)..];
        }
        return result;
    }

    public static bool IsV4(string? ip)
    {
        if (string.IsNullOrEmpty(ip) || !V4Pattern.IsMatch(ip)) return false;
        var parts = ip.Split('.');
        for (var i = 0; i < 4; i++)
        {
            if (int.Parse(parts[i]) > 255) return false;
        }
        return true;
    }

    public static bool IsIpLike(string? ip)
    {
        if (string.IsNullOrEmpty(ip) || ip.Length > 45) return false;
        return IsV4(ip) || (ip.Contains(':') && IpChars.IsMatch(ip));
    }

    /// <summary>IPv4 按字符串精确相等；IPv6 先展开再比。与 checks.js sameIp 一致。</summary>
    public static bool SameIp(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        left = left.Trim();
        right = right.Trim();
        if (IsV4(left) || IsV4(right)) return IsV4(left) && IsV4(right) && left == right;
        var expandedLeft = ExpandV6(left);
        var expandedRight = ExpandV6(right);
        if (expandedLeft is not null && expandedRight is not null) return expandedLeft == expandedRight;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPrivate(string address)
    {
        var value = address.ToLowerInvariant();
        if (value.StartsWith("::ffff:", StringComparison.Ordinal)) return IsPrivateV4(value[7..]);
        if (value == "::1" || value.StartsWith("fc", StringComparison.Ordinal) || value.StartsWith("fd", StringComparison.Ordinal))
            return true;
        if (LinkLocalV6.IsMatch(value)) return true;
        return IsV4(value) && IsPrivateV4(value);
    }

    public static bool IsPublic(string address) =>
        !address.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !IsPrivate(address) && IsIpLike(address);

    public static string? ExtractAsn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = AsnPattern.Match(text);
        return match.Success ? "AS" + match.Groups[1].Value : null;
    }

    public static IpPurityGeo? PickGeo(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        return new IpPurityGeo(
            Country: FirstString(data, "country_code", "country"),
            CountryName: FirstString(data, "country_name"),
            Region: FirstString(data, "region"),
            City: FirstString(data, "city"));
    }

    public static bool? JsonBool(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static string? JsonString(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static JsonElement? JsonObject(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Object ? value : null;
    }

    /// <summary>ASN 表或组织名关键词命中则视为机房；都未命中返回 null（不是 false）。</summary>
    public static bool? HeuristicHosting(string? asn, string? org, string? asnName, string? hostname)
    {
        var number = asn?.StartsWith("AS", StringComparison.OrdinalIgnoreCase) == true ? asn[2..] : asn ?? "";
        if (number.Length > 0 && IpPurityData.HostingAsns.Contains(number)) return true;
        var text = string.Join(' ', new[] { org, asnName, hostname }.Where(part => !string.IsNullOrEmpty(part))).ToLowerInvariant();
        if (text.Length == 0) return null;
        return IpPurityData.Keywords.Any(keyword => KeywordHit(text, keyword)) ? true : null;
    }

    public static IpPurityScoreIntel? ToScoreIntel(IpPurityIntel? intel)
    {
        if (intel is null) return null;
        var flags = intel.Flags;
        return new IpPurityScoreIntel(
            intel.Type, intel.Confidence, intel.Source,
            flags?.Hosting, flags?.Vpn, flags?.Proxy, flags?.Tor, flags?.Relay, flags?.Mobile);
    }

    private static bool KeywordHit(string text, string keyword)
    {
        keyword = keyword.ToLowerInvariant();
        if (KeywordHasLatin.IsMatch(keyword))
        {
            var escaped = Regex.Escape(keyword);
            return Regex.IsMatch(text, "(^|[^a-z0-9])" + escaped + "([^a-z0-9]|$)", RegexOptions.CultureInvariant);
        }
        return text.Contains(keyword, StringComparison.Ordinal);
    }

    private static bool IsPrivateV4(string address)
    {
        var parts = address.Split('.');
        if (parts.Length != 4) return false;
        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i])) return false;
        }
        var a = numbers[0];
        var b = numbers[1];
        if (a is 10 or 127 or 0) return true;
        if (a == 169 && b == 254) return true;
        if (a == 172 && b is >= 16 and <= 31) return true;
        if (a == 192 && b == 168) return true;
        if (a == 100 && b is >= 64 and <= 127) return true;
        if (a == 198 && b is 18 or 19) return true;
        return false;
    }

    private static string? ExpandV6(string ip)
    {
        var text = ip.ToLowerInvariant().Split('%')[0];
        if (text.Contains('.')) return null;
        var halves = text.Split("::");
        if (halves.Length > 2) return null;
        var head = Groups(halves[0]);
        if (head is null) return null;
        if (halves.Length == 1) return head.Count == 8 ? string.Join(':', head) : null;
        var tail = Groups(halves[1]);
        if (tail is null) return null;
        var missing = 8 - head.Count - tail.Count;
        if (missing < 1) return null;
        return string.Join(':', head.Concat(Enumerable.Repeat("0000", missing)).Concat(tail));
    }

    private static List<string>? Groups(string side)
    {
        if (side.Length == 0) return [];
        var parts = side.Split(':');
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (!V6Group.IsMatch(part)) return null;
            result.Add(part.PadLeft(4, '0'));
        }
        return result;
    }

    private static string? FirstString(JsonElement data, params string[] names)
    {
        foreach (var name in names)
        {
            var value = JsonString(data, name);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    public static bool IsSameFamily(string address, string exitIp) => IsV4(address) == IsV4(exitIp);

    public static IPAddress? ParseLoose(string text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            ? address
            : null;

    private static readonly Regex V4Pattern = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IpChars = new("^[0-9a-fA-F:.]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LinkLocalV6 = new("^fe[89ab][0-9a-f](?::|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AsnPattern = new(@"AS?(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex V6Group = new("^[0-9a-f]{1,4}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KeywordHasLatin = new("[a-z0-9]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
