using System.Net;
using System.Net.NetworkInformation;
using Xunit;
using 烧录上位机程序.Services;

namespace DLTools.Tests;

public class IpScanServiceTests
{
    [Theory]
    [InlineData("192.168.1.99", "192.168.1.1", "192.168.1.254")]
    [InlineData("10.0.0.0", "10.0.0.1", "10.0.0.254")]
    [InlineData("172.16.255.255", "172.16.255.1", "172.16.255.254")]
    public void EnumerateSubnet_Returns254UniqueHostsWithoutNetworkOrBroadcast(string input, string first, string last)
    {
        var hosts = IpScanService.EnumerateSubnet(input);
        Assert.Equal(254, hosts.Count);
        Assert.Equal(254, hosts.Distinct().Count());
        Assert.Equal(first, hosts[0].ToString());
        Assert.Equal(last, hosts[^1].ToString());
        Assert.All(hosts, host => Assert.InRange(host.GetAddressBytes()[3], (byte)1, (byte)254));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("192.168.1")]
    [InlineData("192.168.1.256")]
    [InlineData("192.168.1.-1")]
    [InlineData("192.168.1.+1")]
    [InlineData("192.168.1. 1")]
    [InlineData("１２７.0.0.1")]
    [InlineData("0xC0.168.1.1")]
    [InlineData("3232235875")]
    [InlineData("192.168.1.1/24")]
    [InlineData("192.168.1.1/16")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("host.local")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    public void InvalidInput_IsRejectedWithReadableHint(string? input)
    {
        Assert.False(IpScanService.TryParseInput(input, out _, out var error));
        Assert.Contains("IPv4", error);
        Assert.Throws<ArgumentException>(() => IpScanService.EnumerateSubnet(input!));
    }

    [Fact]
    public void ParseInput_NormalizesDecimalOctetsAndOuterWhitespace()
    {
        Assert.True(IpScanService.TryParseInput(" 192.168.001.099 ", out var address, out var error));
        Assert.Equal("192.168.1.99", address.ToString());
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(IPStatus.Success, IpProbeStatus.Reachable, 12L)]
    [InlineData(IPStatus.TimedOut, IpProbeStatus.TimedOut, null)]
    [InlineData(IPStatus.DestinationHostUnreachable, IpProbeStatus.Unreachable, null)]
    [InlineData(IPStatus.TtlExpired, IpProbeStatus.Unreachable, null)]
    public void ClassifyReply_OnlySuccessCarriesRoundtripTime(IPStatus status, IpProbeStatus expected, long? latency)
    {
        var result = IpScanService.ClassifyReply(status, 12);
        Assert.Equal(expected, result.Status);
        Assert.Equal(latency, result.RoundtripTimeMs);
    }

    [Fact]
    public async Task Scan_Uses32SlotsAndSerialMonotonicProgressWith500msTimeout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var started = 0;
        var progress = new List<IpScanProgress>();
        var service = new IpScanService(async (address, timeout, token) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(500), timeout);
            var count = Interlocked.Increment(ref active);
            Assert.InRange(count, 1, 32);
            if (Interlocked.Increment(ref started) == 32) full.SetResult();
            try { await release.Task.WaitAsync(token); return new(IpProbeStatus.Reachable, 7); }
            finally { Interlocked.Decrement(ref active); }
        }, _ => { });
        var scan = service.ScanAsync("192.168.1.99", async update =>
        {
            await Task.Yield();
            progress.Add(update);
        }, CancellationToken.None);
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(32, Volatile.Read(ref started));
        release.SetResult();
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(1, 254), progress.Select(update => update.Completed));
        Assert.Equal(254, progress.Select(update => update.Address).Distinct().Count());
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task Cancel_DrainsActivePingsAndDoesNotStartQueuedHostsOrReportAfterReturn()
    {
        using var cancel = new CancellationTokenSource();
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var started = 0;
        var reports = 0;
        var service = new IpScanService(async (_, _, token) =>
        {
            Interlocked.Increment(ref active);
            if (Interlocked.Increment(ref started) == 32) full.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); return new(IpProbeStatus.Reachable, 1); }
            finally { Interlocked.Decrement(ref active); }
        }, _ => { });
        var scan = service.ScanAsync("192.168.1.99", _ => { reports++; return Task.CompletedTask; }, cancel.Token);
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, active);
        Assert.Equal(32, started);
        Assert.Equal(0, reports);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotReadNetworkOrSendPing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var service = new IpScanService((_, _, _) => throw new Xunit.Sdk.XunitException("Unexpected ping"),
            _ => throw new Xunit.Sdk.XunitException("Unexpected network access"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync("192.168.1.1", _ => Task.CompletedTask, cancel.Token));
    }

    [Fact]
    public async Task Timeout_IsReportedAsNotReachableAndScanContinues()
    {
        var updates = new List<IpScanProgress>();
        var service = new IpScanService((_, _, _) => throw new TimeoutException(), _ => { });
        await service.ScanAsync("192.168.1.99", update => { updates.Add(update); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(254, updates.Count);
        Assert.All(updates, update => Assert.Equal(IpProbeStatus.TimedOut, update.Result.Status));
    }

    [Fact]
    public async Task NetworkOffline_FailsBeforeAnyProbe()
    {
        var service = new IpScanService((_, _, _) => throw new Xunit.Sdk.XunitException("Unexpected ping"),
            _ => throw new InvalidOperationException("未找到在线的 IPv4 网卡"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync("192.168.1.1", _ => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("网卡", error.Message);
    }

    [Fact]
    public async Task ProbeFailure_CancelsOtherWorkersAndDrainsBeforeThrowing()
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var started = 0;
        var service = new IpScanService(async (address, _, token) =>
        {
            Interlocked.Increment(ref active);
            if (Interlocked.Increment(ref started) == 32) full.SetResult();
            try
            {
                await full.Task.WaitAsync(token);
                if (address.GetAddressBytes()[3] == 1) throw new InvalidOperationException("ICMP error");
                await Task.Delay(Timeout.Infinite, token);
                return new(IpProbeStatus.Unreachable);
            }
            finally { Interlocked.Decrement(ref active); }
        }, _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync("192.168.1.1", _ => Task.CompletedTask, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, active);
        Assert.Equal(32, started);
    }

    [Fact]
    public async Task ReportFailure_DrainsWorkersBeforeReturningError()
    {
        var active = 0;
        var service = new IpScanService(async (_, _, token) =>
        {
            Interlocked.Increment(ref active);
            try { await Task.Delay(10, token); return new(IpProbeStatus.Reachable, 1); }
            finally { Interlocked.Decrement(ref active); }
        }, _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync("192.168.1.1",
            _ => throw new InvalidOperationException("UI report failed"), CancellationToken.None));
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task CancelDuringReport_WaitsForCallbackAndSuppressesQueuedReports()
    {
        using var cancel = new CancellationTokenSource();
        var reporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var service = new IpScanService((_, _, _) => Task.FromResult(new IpProbeResult(IpProbeStatus.Reachable, 1)), _ => { });
        var scan = service.ScanAsync("192.168.1.1", async _ =>
        {
            Interlocked.Increment(ref count);
            reporting.SetResult();
            await release.Task;
        }, cancel.Token);
        await reporting.Task;
        cancel.Cancel();
        Assert.False(scan.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, count);
    }
}
