using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using 烧录上位机程序.Models.IpPurity;

namespace 烧录上位机程序.Services.IpPurity;

public sealed record StunServer(string Host, int Port, bool Foreign);

/// <summary>用 STUN Binding 请求看 UDP 从哪个地址出去。桌面程序没有 WebRTC，这是对应的探测。</summary>
public class StunProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    public static IReadOnlyList<StunServer> Servers { get; } =
    [
        new("stun.cloudflare.com", 3478, Foreign: true),
        new("stun.l.google.com", 19302, Foreign: true),
        new("stun.miwifi.com", 3478, Foreign: false),
        new("stun.qq.com", 3478, Foreign: false),
    ];

    public virtual async Task<IReadOnlyList<IpPurityUdpObservation>> ProbeAllAsync(CancellationToken cancellationToken)
    {
        var tasks = Servers.Select(server => ProbeSafeAsync(server, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public virtual async Task<IpPurityUdpObservation> ProbeSafeAsync(StunServer server, CancellationToken cancellationToken)
    {
        try
        {
            return await ProbeAsync(server, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new IpPurityUdpObservation(server.Host, server.Port, server.Foreign, null, null, "网络错误或被拦截");
        }
    }

    public async Task<IpPurityUdpObservation> ProbeAsync(StunServer server, CancellationToken cancellationToken) =>
        await ProbeAsync(server, server.Host, server.Port, cancellationToken).ConfigureAwait(false);

    /// <summary>endpointHost 允许测试把请求打到回环地址，逻辑服务器身份仍用 server 上的名字。</summary>
    public async Task<IpPurityUdpObservation> ProbeAsync(
        StunServer server, string endpointHost, int endpointPort, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpointHost, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new IpPurityUdpObservation(server.Host, server.Port, server.Foreign, null, null, "域名解析失败");
        }

        var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();
        if (address is null)
        {
            return new IpPurityUdpObservation(server.Host, server.Port, server.Foreign, null, null, "域名解析失败");
        }

        var transaction = new byte[12];
        RandomNumberGenerator.Fill(transaction);
        var request = BuildBindingRequest(transaction);
        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, endpointPort), cancellationToken).ConfigureAwait(false);
            await socket.SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultTimeout);
            var buffer = new byte[1500];
            var received = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
            var mapped = ParseMappedAddress(buffer.AsSpan(0, received), transaction);
            var latency = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (mapped is null)
            {
                return new IpPurityUdpObservation(server.Host, server.Port, server.Foreign, null, latency, "返回内容无法解析");
            }
            return new IpPurityUdpObservation(server.Host, server.Port, server.Foreign, mapped.ToString(), latency, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new IpPurityUdpObservation(server.Host, server.Port, server.Foreign, null,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, "请求超时");
        }
        catch (Exception)
        {
            return new IpPurityUdpObservation(server.Host, server.Port, server.Foreign, null, null, "网络错误或被拦截");
        }
    }

    public static byte[] BuildBindingRequest(ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != 12) throw new ArgumentException("STUN 事务号必须是 12 字节。", nameof(transactionId));
        var packet = new byte[20];
        packet[0] = 0x00;
        packet[1] = 0x01;
        packet[4] = 0x21;
        packet[5] = 0x12;
        packet[6] = 0xA4;
        packet[7] = 0x42;
        transactionId.CopyTo(packet.AsSpan(8));
        return packet;
    }

    public static IPAddress? ParseMappedAddress(ReadOnlySpan<byte> message, ReadOnlySpan<byte> transactionId)
    {
        if (message.Length < 20 || transactionId.Length != 12) return null;
        var type = (message[0] << 8) | message[1];
        if (type != 0x0101) return null;
        if (message[4] != 0x21 || message[5] != 0x12 || message[6] != 0xA4 || message[7] != 0x42) return null;
        if (!message.Slice(8, 12).SequenceEqual(transactionId)) return null;
        var length = (message[2] << 8) | message[3];
        var available = Math.Min(message.Length - 20, length);
        IPAddress? mapped = null;
        IPAddress? xorMapped = null;
        var offset = 20;
        var end = 20 + available;
        while (offset + 4 <= end)
        {
            var attrType = (message[offset] << 8) | message[offset + 1];
            var attrLength = (message[offset + 2] << 8) | message[offset + 3];
            offset += 4;
            if (attrLength < 0 || offset + attrLength > end) break;
            var value = message.Slice(offset, attrLength);
            if (attrType == 0x0020) xorMapped = DecodeAddress(value, xor: true, transactionId);
            else if (attrType == 0x0001) mapped = DecodeAddress(value, xor: false, transactionId);
            offset += (attrLength + 3) & ~3;
        }
        return xorMapped ?? mapped;
    }

    private static IPAddress? DecodeAddress(ReadOnlySpan<byte> value, bool xor, ReadOnlySpan<byte> transactionId)
    {
        if (value.Length < 4) return null;
        var family = value[1];
        var port = (ushort)((value[2] << 8) | value[3]);
        if (xor) port ^= 0x2112;
        if (family == 0x01)
        {
            if (value.Length < 8) return null;
            var bytes = value.Slice(4, 4).ToArray();
            if (xor)
            {
                bytes[0] ^= 0x21;
                bytes[1] ^= 0x12;
                bytes[2] ^= 0xA4;
                bytes[3] ^= 0x42;
            }
            return new IPAddress(bytes);
        }
        if (family == 0x02)
        {
            if (value.Length < 20) return null;
            var bytes = value.Slice(4, 16).ToArray();
            if (xor)
            {
                var mask = new byte[16];
                mask[0] = 0x21;
                mask[1] = 0x12;
                mask[2] = 0xA4;
                mask[3] = 0x42;
                transactionId.CopyTo(mask.AsSpan(4));
                for (var i = 0; i < 16; i++) bytes[i] ^= mask[i];
            }
            return new IPAddress(bytes);
        }
        return null;
    }
}
