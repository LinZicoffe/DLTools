using System.Net;
using System.Net.Sockets;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>下载 URL 安全校验。所有下载仅允许 http/https；镜像清单与镜像节点额外要求 https 公网地址。</summary>
public static class DownloadUrlGuard
{
    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>用户输入的直链下载地址：仅允许 http/https，且拒绝 URL 内嵌账号信息。</summary>
    public static void EnsureDownloadUrl(string? url)
    {
        if (!IsHttpUrl(url))
            throw new ArgumentException("下载地址仅支持 http/https，请检查输入（不支持 file、ftp 或本地路径）。");
        if (!string.IsNullOrEmpty(new Uri(url!.Trim()).UserInfo))
            throw new ArgumentException("下载地址包含内嵌账号信息，请去掉 user:pass@ 部分后重试。");
    }

    /// <summary>镜像清单与镜像节点地址：必须 https，且 host 不能是 localhost、环回、私有或保留地址。</summary>
    public static void EnsureManifestUrl(string? url)
    {
        EnsureDownloadUrl(url);
        var uri = new Uri(url!.Trim());
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"镜像清单地址必须使用 https：{uri.Scheme}://");
        if (uri.HostNameType == UriHostNameType.Dns && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("镜像清单地址不允许指向 localhost。");
        foreach (var address in ResolveHostAddresses(uri.Host))
        {
            if (!IsPublicAddress(address))
                throw new ArgumentException($"镜像清单地址解析到内网/保留地址 {address}，仅允许公网节点。");
        }
    }

    /// <summary>对镜像节点清单逐个校验；返回通过校验的地址（保持原顺序）。</summary>
    public static IReadOnlyList<string> FilterMirrorUrls(IEnumerable<string> urls)
    {
        var allowed = new List<string>();
        foreach (var url in urls)
        {
            try { EnsureManifestUrl(url); allowed.Add(url.Trim()); }
            catch (ArgumentException) { /* 跳过非法节点：清单可能同时服务内外网环境 */ }
        }
        return allowed;
    }

    private static IPAddress[] ResolveHostAddresses(string host)
    {
        // DNS 名称需解析后判定；IP 字面量直接返回。解析失败不拦截（交给传输层报错）。
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        try { return Dns.GetHostAddresses(host); }
        catch (SocketException) { return [];
        }
    }

    /// <summary>公网地址判定（对 IPv4/IPv6/IPv4 映射地址统一判定；测试直接覆盖各段）。</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = address.GetAddressBytes()[0];
            var second = address.GetAddressBytes()[1];
            return !IPAddress.IsLoopback(address)
                && first is not 0 and not 10 and not 127          // 未指定 / 10/8 私网 / 环回
                && !(first == 172 && second >= 16 && second <= 31) // 172.16/12 私网
                && !(first == 192 && second == 168)                // 192.168/16 私网
                && !(first == 169 && second == 254)                // 链路本地
                && first < 224;                                    // 组播/保留/广播
        }
        if (TryGetIPv4Mapped(address, out var mapped)) return IsPublicAddress(mapped);
        return !IPAddress.IsLoopback(address)
            && !address.IsIPv6LinkLocal
            && (address.GetAddressBytes()[0] & 0xFE) != 0xFC;      // fc00::/7 本地单播
    }

    /// <summary>识别 ::ffff:a.b.c.d 映射形式（前 10 字节为零 + 两字节 0xFF），换算回 IPv4 判定。</summary>
    private static bool TryGetIPv4Mapped(IPAddress address, out IPAddress mapped)
    {
        mapped = IPAddress.None;
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 16) return false;
        for (var i = 0; i < 10; i++)
            if (bytes[i] != 0) return false;
        if (bytes[10] != 0xFF || bytes[11] != 0xFF) return false;
        mapped = new IPAddress(bytes[12..16]);
        return true;
    }
}
