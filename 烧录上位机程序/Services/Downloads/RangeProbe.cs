using System.Globalization;
using System.IO;
using System.Text;
using 烧录上位机程序.Models.Downloads;

namespace 烧录上位机程序.Services.Downloads;

public sealed record RangeProbeResult(bool SupportsRanges, long? TotalLength, string ETag, string LastModified,
    string FileName, string ContentType, int StatusCode);

/// <summary>Range 探测与响应头解析。探测请求固定 GET + "Range: bytes=0-0"。</summary>
public static class RangeProbe
{
    public static async Task<RangeProbeResult> ProbeAsync(IDownloadTransport transport, string url,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var response = await transport.SendAsync(new TransportRequest(url, 0, 0, null), cancellationToken)
            .ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            var statusCode = response.StatusCode;
            if (statusCode is >= 200 and < 300)
            {
                var fileName = ParseContentDispositionFileName(response.ContentDisposition);
                if (statusCode == 206 && TryParseContentRange(response.ContentRange, out _, out _, out var total))
                    return new RangeProbeResult(true, total, response.ETag, response.LastModified, fileName,
                        response.ContentType, statusCode);
                // 200：服务器忽略 Range 或资源本就只有 1 字节；无法证明支持分段，按单流处理。
                return new RangeProbeResult(false, response.ContentLength, response.ETag, response.LastModified,
                    fileName, response.ContentType, statusCode);
            }
            throw Classify(statusCode);
        }
    }

    /// <summary>把非 2xx 状态码翻译成可行动的中文提示。404/403/410 为终态。</summary>
    public static DownloadTransportException Classify(int statusCode) => statusCode switch
    {
        403 => new("服务器拒绝访问（403）。资源可能需要登录、防盗链或授权已过期。", 403, terminal: true),
        404 => new("文件不存在（404），请核对下载地址。", 404, terminal: true),
        410 => new("资源已被永久移除（410）。", 410, terminal: true),
        429 => new("请求过于频繁（429），请稍后重试或降低连接数。", 429),
        >= 500 => new($"服务器错误（{statusCode}），请稍后重试或更换镜像节点。", statusCode),
        _ => new($"服务器返回意外状态码 {statusCode}。", statusCode)
    };

    public static bool TryParseContentRange(string? value, out long start, out long end, out long? total)
    {
        start = 0;
        end = 0;
        total = null;
        // 形如 "bytes 0-0/12345"；total 为 "*" 表示服务器未给出总长（按未知长度单流处理）。
        if (string.IsNullOrWhiteSpace(value)) return false;
        var slash = value.LastIndexOf('/');
        if (slash <= 0) return false;
        var rangePart = value[..slash].Trim();
        var totalPart = value[(slash + 1)..].Trim();
        var space = rangePart.LastIndexOf(' ');
        if (space >= 0) rangePart = rangePart[(space + 1)..];
        var dash = rangePart.IndexOf('-');
        if (dash <= 0) return false;
        if (!long.TryParse(rangePart[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start)
            || !long.TryParse(rangePart[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out end)
            || start < 0 || end < start) return false;
        if (totalPart == "*") return true;
        if (!long.TryParse(totalPart, NumberStyles.None, CultureInfo.InvariantCulture, out var totalValue)
            || totalValue < 0) return false;
        total = totalValue;
        return true;
    }

    /// <summary>解析 Content-Disposition 文件名；filename*=UTF-8'' 优先于 filename=，自动去引号与百分号解码。</summary>
    public static string ParseContentDispositionFileName(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "";
        var fallback = "";
        foreach (var segment in SplitHeaderSegments(header))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0) continue;
            var name = segment[..equals].Trim();
            var rawValue = segment[(equals + 1)..].Trim();
            if (name.Equals("filename", StringComparison.OrdinalIgnoreCase))
                fallback = Unquote(rawValue);
            else if (name.Equals("filename*", StringComparison.OrdinalIgnoreCase)
                && TryParseExtendedValue(rawValue, out var extended))
                return extended; // 找到 filename* 即立即返回（RFC 6266：优先于 filename）
        }
        return SanitizeFileName(fallback);
    }

    private static IEnumerable<string> SplitHeaderSegments(string header)
    {
        var builder = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < header.Length; i++)
        {
            var c = header[i];
            if (c == '"') { inQuotes = !inQuotes; builder.Append(c); }
            else if (c == ';' && !inQuotes)
            {
                if (builder.Length > 0) yield return builder.ToString();
                builder.Clear();
            }
            else builder.Append(c);
        }
        if (builder.Length > 0) yield return builder.ToString();
    }

    private static bool TryParseExtendedValue(string rawValue, out string value)
    {
        value = "";
        // 形如 UTF-8''%E5%9B%BA%E4%BB%B6.zip：charset''lang''percent-encoded
        var separators = rawValue.Split('\'');
        if (separators.Length < 3) return false;
        var charset = separators[0];
        var encoded = separators[2];
        if (!charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) return false; // 仅支持 UTF-8 扩展值
        value = SanitizeFileName(Uri.UnescapeDataString(encoded));
        return value.Length > 0;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            value = value[1..^1].Replace("\\\"", "\"");
        return value;
    }

    private static string SanitizeFileName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "" : string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
}
