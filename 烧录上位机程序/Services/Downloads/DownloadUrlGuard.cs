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

    /// <summary>公网地址判定（IPv4/IPv6/IPv4 映射/NAT64/IPv4 兼容形式统一判定；测试直接覆盖各段）。
    /// 已知限制：校验时解析与实际建连之间存在 DNS rebinding 窗口（TOCTOU），见功能日志。</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var first = bytes[0];
            var second = bytes[1];
            var third = bytes[2];
            return !IPAddress.IsLoopback(address)
                && first is not 0 and not 10 and not 127            // 未指定 / 10/8 私网 / 环回
                && !(first == 172 && second >= 16 && second <= 31)  // 172.16/12 私网
                && !(first == 192 && second == 168)                 // 192.168/16 私网
                && !(first == 169 && second == 254)                 // 169.254/16 链路本地
                && !(first == 100 && second >= 64 && second <= 127) // 100.64/10 CGNAT 共享地址（RFC 6598）
                && !(first == 192 && second == 0 && third == 0)     // 192.0.0.0/24 IETF 协议分配
                && !(first == 192 && second == 0 && third == 2)     // 192.0.2.0/24 TEST-NET-1
                && !(first == 198 && second == 51 && third == 100)  // 198.51.100.0/24 TEST-NET-2
                && !(first == 203 && second == 0 && third == 113)   // 203.0.113.0/24 TEST-NET-3
                && !(first == 198 && (second == 18 || second == 19))// 198.18/15 基准测试
                && first < 224;                                     // 组播/保留/广播
        }
        var v6 = address.GetAddressBytes();
        if (v6.Length != 16) return false;
        // NAT64 64:ff9b::/96：前 96 位（字节 0..11）匹配，低 32 位是内嵌 IPv4，按其递归判定
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B
            && v6[4] == 0 && v6[5] == 0 && v6[6] == 0 && v6[7] == 0
            && v6[8] == 0 && v6[9] == 0 && v6[10] == 0 && v6[11] == 0)
            return IsPublicAddress(new IPAddress(v6[12..16]));
        // RFC 8215 本地用 NAT64 段 64:ff9b:1::/48
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B
            && v6[4] == 0x00 && v6[5] == 0x01)
            return false;
        if (TryGetIPv4Mapped(address, out var mapped)) return IsPublicAddress(mapped);
        // IPv4 兼容形式 ::/96（非 0xFFFF 映射）：低 32 位同样按 IPv4 递归判定（::a00:1 ≡ 10.0.0.1）
        var first96Zero = true;
        for (var i = 0; i < 12; i++)
            if (v6[i] != 0) { first96Zero = false; break; }
        if (first96Zero) return IsPublicAddress(new IPAddress(v6[12..16]));
        return !IsAllZero(v6)                                       // :: 未指定地址
            && !IPAddress.IsLoopback(address)
            && !address.IsIPv6LinkLocal
            && v6[0] != 0xFF                                        // ff00::/8 组播
            && !(v6[0] == 0xFE && (v6[1] & 0xC0) == 0xC0)           // fec0::/10 站点本地（已废弃仍拒）
            && (v6[0] & 0xFE) != 0xFC;                              // fc00::/7 本地单播
    }

    private static bool IsAllZero(byte[] bytes)
    {
        foreach (var b in bytes)
            if (b != 0) return false;
        return true;
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
