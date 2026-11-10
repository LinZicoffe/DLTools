using System.Net;
using Xunit;
using 烧录上位机程序.Services.Downloads;

namespace DLTools.Tests;

public class DownloadUrlGuardTests
{
    [Theory]
    [InlineData("http://example.com/firmware.bin", true)]
    [InlineData("https://example.com/firmware.bin?a=1", true)]
    [InlineData("https://8.8.8.8/file.bin", true)]
    [InlineData("file://C:/fw.bin", false)]
    [InlineData("ftp://example.com/fw.bin", false)]
    [InlineData(@"\\server\share\fw.bin", false)]
    [InlineData("C:\\fw.bin", false)]
    [InlineData("example.com/fw.bin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHttpUrl_AcceptsOnlyHttpHttps(string? url, bool expected)
    {
        Assert.Equal(expected, DownloadUrlGuard.IsHttpUrl(url));
    }

    [Theory]
    [InlineData("file://C:/fw.bin")]
    [InlineData("ftp://example.com/fw.bin")]
    [InlineData("not a url")]
    public void EnsureDownloadUrl_RejectsNonHttpSchemeWithReadableHint(string url)
    {
        var error = Assert.Throws<ArgumentException>(() => DownloadUrlGuard.EnsureDownloadUrl(url));
        Assert.Contains("http/https", error.Message);
    }

    [Fact]
    public void EnsureDownloadUrl_RejectsEmbeddedCredentials()
    {
        var error = Assert.Throws<ArgumentException>(
            () => DownloadUrlGuard.EnsureDownloadUrl("http://user:pass@example.com/fw.bin"));
        Assert.Contains("内嵌账号信息", error.Message);
    }

    [Fact]
    public void EnsureDownloadUrl_AcceptsPlainHttpUrl()
    {
        DownloadUrlGuard.EnsureDownloadUrl("http://192.168.1.10:8080/firmware/fw.bin");
    }

    [Theory]
    [InlineData("http://example.com/manifest.json")]          // 清单必须 https
    [InlineData("https://localhost/manifest.json")]           // localhost
    [InlineData("https://127.0.0.1/manifest.json")]           // IPv4 环回
    [InlineData("https://[::1]/manifest.json")]               // IPv6 环回
    [InlineData("https://10.1.2.3/manifest.json")]            // 10/8 私网
    [InlineData("https://172.16.0.9/manifest.json")]          // 172.16/12 私网
    [InlineData("https://172.31.255.1/manifest.json")]
    [InlineData("https://192.168.1.10/manifest.json")]        // 192.168/16 私网
    [InlineData("https://169.254.1.1/manifest.json")]         // 链路本地
    [InlineData("https://0.0.0.0/manifest.json")]             // 未指定
    [InlineData("https://224.0.0.1/manifest.json")]           // 组播
    [InlineData("https://255.255.255.255/manifest.json")]     // 受限广播
    [InlineData("https://[fe80::1]/manifest.json")]           // IPv6 链路本地
    [InlineData("https://[fc00::1]/manifest.json")]           // IPv6 本地单播
    public void EnsureManifestUrl_RejectsPlainHttpAndNonPublicHosts(string url)
    {
        Assert.Throws<ArgumentException>(() => DownloadUrlGuard.EnsureManifestUrl(url));
    }

    [Fact]
    public void EnsureManifestUrl_AcceptsPublicIpLiteralOverHttps()
    {
        DownloadUrlGuard.EnsureManifestUrl("https://8.8.8.8/manifest.json");
    }

    [Theory]
    [InlineData("192.168.1.5", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.0.2", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.5", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    public void IsPublicAddress_ClassifiesIpv4Ranges(string ip, bool expected)
    {
        Assert.Equal(expected, DownloadUrlGuard.IsPublicAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsPublicAddress_ClassifiesIpv6AndMappedAddresses()
    {
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("::1")));
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("fe80::1")));
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("fc00::1")));
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("::ffff:192.168.1.5")));
        Assert.True(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("2606:4700::1111")));
        Assert.True(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public void IsPublicAddress_RejectsNat64AndIpv4CompatibleEmbeddings()
    {
        // NAT64 64:ff9b::/96：低 32 位内嵌 IPv4，按内嵌地址递归判定
        Assert.True(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("64:ff9b::808:808")));   // ≡ 8.8.8.8
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("64:ff9b::a00:1")));    // ≡ 10.0.0.1
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("64:ff9b::7f00:1")));   // ≡ 127.0.0.1
        // /96 掩码边界：组 4 非 0 的地址不属于 NAT64 段（IANA 未分配，按普通公网地址判定，不递归内嵌 IPv4）
        Assert.True(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("64:ff9b:0:0:1::808:808")));
        // RFC 8215 本地用 NAT64 段 64:ff9b:1::/48
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("64:ff9b:1::1")));
        // 已废弃的 IPv4 兼容形式 ::/96（非 0xFFFF 映射）
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("::a00:1")));           // ≡ 10.0.0.1
        Assert.True(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("::808:808")));          // ≡ 8.8.8.8
        // 未指定地址 ::
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("::")));
    }

    [Fact]
    public void IsPublicAddress_RejectsIpv6MulticastAndSiteLocal()
    {
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("ff02::1")));   // 链路本地组播
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("ffb0::1")));  // 组播
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("fec0::1")));  // 站点本地（已废弃仍拒）
    }

    [Fact]
    public void IsPublicAddress_RejectsSharedAndDocumentationRanges()
    {
        // 100.64/10 CGNAT 共享地址（RFC 6598）
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("100.64.0.1")));
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("100.127.255.254")));
        Assert.True(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("100.128.0.1")));
        // 192.0.0.0/24 IETF 协议分配 与 TEST-NET-1/2/3
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("192.0.0.1")));
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("192.0.2.5")));
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("198.51.100.8")));
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("203.0.113.7")));
        // 198.18/15 基准测试
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("198.18.5.1")));
        Assert.False(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("198.19.255.1")));
        Assert.True(DownloadUrlGuard.IsPublicAddress(IPAddress.Parse("198.20.0.1")));
    }

    [Fact]
    public void FilterMirrorUrls_KeepsOnlyValidPublicHttpsNodesInOrder()
    {
        var allowed = DownloadUrlGuard.FilterMirrorUrls(
        [
            "https://8.8.8.8/a.bin",
            "http://9.9.9.9/a.bin",
            "https://192.168.0.9/a.bin",
            "https://1.1.1.1/a.bin",
        ]);
        Assert.Equal(["https://8.8.8.8/a.bin", "https://1.1.1.1/a.bin"], allowed);
    }
}
