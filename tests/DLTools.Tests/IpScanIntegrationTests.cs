using System.Diagnostics;
using System.Text.Json;
using Xunit;
using 烧录上位机程序.Services;

namespace DLTools.Tests;

public sealed class IpScanIntegrationFactAttribute : FactAttribute
{
    public IpScanIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DLTOOLS_IPSCAN_INTEGRATION") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DLTOOLS_IPSCAN_TARGET")))
            Skip = "Set DLTOOLS_IPSCAN_INTEGRATION=1 and DLTOOLS_IPSCAN_TARGET to a local /24 IPv4 address.";
    }
}

public class IpScanIntegrationTests
{
    [IpScanIntegrationFact]
    public async Task RealLanScan_Reports254HostsAndSavesEvidence()
    {
        var input = Environment.GetEnvironmentVariable("DLTOOLS_IPSCAN_TARGET")!;
        var results = new List<IpScanProgress>();
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await new IpScanService().ScanAsync(input, update =>
        {
            results.Add(update);
            return Task.CompletedTask;
        }, deadline.Token);
        watch.Stop();
        SaveEvidence("I03-real-lan.json", new
        {
            input,
            elapsedMs = watch.ElapsedMilliseconds,
            results = results.Select(update => new
            {
                update.Completed,
                address = update.Address.ToString(),
                status = update.Result.Status.ToString(),
                update.Result.RoundtripTimeMs
            })
        });
        Assert.Equal(Enumerable.Range(1, 254), results.Select(update => update.Completed));
        Assert.Equal(254, results.Select(update => update.Address).Distinct().Count());
        Assert.All(results, update => Assert.InRange(update.Address.GetAddressBytes()[3], (byte)1, (byte)254));
        Assert.Contains(results, update => update.Result.Status == IpProbeStatus.Reachable);
    }

    [IpScanIntegrationFact]
    public async Task RealPingCancellation_DrainsAndStopsAllFurtherReports()
    {
        var input = Environment.GetEnvironmentVariable("DLTOOLS_IPSCAN_TARGET")!;
        using var cancel = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, deadline.Token);
        var reports = 0;
        long? cancelledAtMs = null;
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IpScanService().ScanAsync(input,
            _ =>
            {
                Interlocked.Increment(ref reports);
                cancelledAtMs = watch.ElapsedMilliseconds;
                cancel.Cancel();
                return Task.CompletedTask;
            }, stop.Token));
        watch.Stop();
        var terminalReports = reports;
        await Task.Delay(600);
        Assert.False(deadline.IsCancellationRequested);
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(terminalReports, reports);
        Assert.Equal(1, terminalReports);
        SaveEvidence("I03-real-cancel.json", new { input, cancelTrigger = "first real progress callback", cancelledAtMs,
            elapsedMs = watch.ElapsedMilliseconds, terminalReports, reportsAfter600ms = reports });
    }

    private static void SaveEvidence(string name, object evidence)
    {
        var path = Environment.GetEnvironmentVariable("DLTOOLS_IPSCAN_EVIDENCE")
            ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, name), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }
}
