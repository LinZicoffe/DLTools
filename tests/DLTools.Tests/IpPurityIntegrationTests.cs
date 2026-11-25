using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Services.IpPurity;

namespace DLTools.Tests;

public sealed class IpPurityIntegrationFactAttribute : FactAttribute
{
    public IpPurityIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DLTOOLS_IPPURITY_INTEGRATION"), "1", StringComparison.Ordinal))
            Skip = "设置 DLTOOLS_IPPURITY_INTEGRATION=1 后才访问真实的第三方检测接口。";
    }
}

public class IpPurityIntegrationTests
{
    [IpPurityIntegrationFact]
    public async Task LiveCheck_ReturnsExitIntelligenceServicesAndUdp()
    {
        var snapshots = new ConcurrentQueue<IpPuritySnapshot>();
        var runner = new IpPurityRunner();
        await runner.RunAsync(new Progress<IpPuritySnapshot>(snapshots.Enqueue), CancellationToken.None);
        var done = snapshots.Last();
        var evidence = new
        {
            phase = done.Phase.ToString(),
            exit = done.Exit?.Ip,
            source = done.Exit?.Source,
            errors = done.Exit?.Errors,
            intel = done.Intel?.Source,
            type = done.Intel?.Type,
            services = done.Services.Select(service => new { service.Name, status = service.Verdict?.Status, ip = service.Verdict?.TraceIp }),
            udp = done.Udp.Select(row => new { row.Host, row.Address, row.Error }),
            score = done.Score?.Total,
            grade = done.Score?.Grade?.Label,
            timedOut = done.TimedOut,
            elapsedMs = (int)done.Elapsed.TotalMilliseconds
        };
        var directory = Environment.GetEnvironmentVariable("DLTOOLS_IPPURITY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "ip-purity-live.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(IpPurityPhase.Done, done.Phase);
        Assert.False(string.IsNullOrWhiteSpace(done.Exit?.Ip));
        Assert.NotNull(done.Intel);
        Assert.Equal(2, done.Services.Count);
        Assert.Equal(4, done.Udp.Count);
        Assert.NotNull(done.Score);
    }
}
