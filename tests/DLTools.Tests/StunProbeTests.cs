using System.Net;
using System.Net.Sockets;
using Xunit;
using 烧录上位机程序.Services.IpPurity;

namespace DLTools.Tests;

public class StunProbeTests
{
    [Fact]
    public void Parser_ReadsXorMappedIpv4()
    {
        var transaction = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
        var request = StunProbe.BuildBindingRequest(transaction);
        Assert.Equal(20, request.Length);
        Assert.Equal(0x0001, (request[0] << 8) | request[1]);

        var response = new byte[32];
        response[0] = 0x01;
        response[1] = 0x01;
        response[2] = 0x00;
        response[3] = 12;
        response[4] = 0x21;
        response[5] = 0x12;
        response[6] = 0xA4;
        response[7] = 0x42;
        transaction.CopyTo(response, 8);
        response[20] = 0x00;
        response[21] = 0x20;
        response[22] = 0x00;
        response[23] = 8;
        response[25] = 0x01;
        response[26] = 0xF5;
        response[27] = 0x22;
        response[28] = 0xEA;
        response[29] = 0x12;
        response[30] = 0xD5;
        response[31] = 0x45;

        var mapped = StunProbe.ParseMappedAddress(response, transaction);
        Assert.Equal("203.0.113.7", mapped!.ToString());
    }

    [Fact]
    public async Task Probe_ReadsALoopbackBindingResponseAndHonorsCancellation()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var server = Task.Run(async () =>
        {
            var buffer = new byte[1500];
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
            var response = new byte[32];
            response[0] = 0x01;
            response[1] = 0x01;
            response[3] = 12;
            response[4] = 0x21;
            response[5] = 0x12;
            response[6] = 0xA4;
            response[7] = 0x42;
            buffer.AsSpan(8, 12).CopyTo(response.AsSpan(8));
            response[20] = 0x00;
            response[21] = 0x01;
            response[23] = 8;
            response[25] = 0x01;
            response[28] = 203;
            response[29] = 0;
            response[30] = 113;
            response[31] = 9;
            await socket.SendToAsync(response, SocketFlags.None, received.RemoteEndPoint);
        });

        var probe = new StunProbe();
        var hit = await probe.ProbeAsync(new StunServer("stun.example", 3478, true), "127.0.0.1", port, CancellationToken.None);
        await server;
        Assert.Equal("203.0.113.9", hit.Address);
        Assert.Null(hit.Error);

        using var cancel = new CancellationTokenSource();
        var pending = probe.ProbeAsync(new StunServer("stun.example", 3478, true), "127.0.0.1", port, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
