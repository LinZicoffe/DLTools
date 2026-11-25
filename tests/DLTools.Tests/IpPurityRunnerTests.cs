using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Services.IpPurity;

namespace DLTools.Tests;

public class IpPurityRunnerTests
{
    [Fact]
    public async Task Run_PublishesIncrementalSnapshotsAndScoresASplitExit()
    {
        var transport = new ScriptTransport();
        var snapshots = new ConcurrentQueue<IpPuritySnapshot>();
        var runner = new IpPurityRunner(transport, new FixedStun("192.0.2.44"), TimeSpan.FromSeconds(5));
        await runner.RunAsync(new Progress<IpPuritySnapshot>(snapshots.Enqueue), CancellationToken.None);

        var done = snapshots.Last();
        Assert.Equal(IpPurityPhase.Done, done.Phase);
        Assert.Equal(6, done.Completed);
        Assert.Equal("203.0.113.7", done.Exit!.Ip);
        Assert.Equal("IPinfo", done.Intel!.Source);
        var claude = done.Services.Single(service => service.ServiceId == "claude");
        Assert.Equal("198.51.100.9", claude.Verdict!.TraceIp);
        Assert.NotNull(claude.SplitIntel);
        Assert.NotNull(claude.SplitScore);
        Assert.Contains(transport.Urls, url => url.Contains("198.51.100.9", StringComparison.Ordinal));
        Assert.Equal("leak", done.Score is null ? "" : IpPurityChecks.ClassifyUdp(done.Exit.Ip, done.Udp).State);
        Assert.Contains(snapshots, snapshot => snapshot.Phase == IpPurityPhase.Running && snapshot.Completed < 6);
    }

    [Fact]
    public async Task Run_CancelInterruptsTheRequestAndTimeoutFinishesWithWhatItHas()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptTransport(_ => gate.Task);
        var runner = new IpPurityRunner(transport, new FixedStun(null), TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var running = runner.RunAsync(new Progress<IpPuritySnapshot>(_ => { }), cancel.Token);
        Assert.True(SpinWait.SpinUntil(() => transport.Started > 0, TimeSpan.FromSeconds(2)));
        cancel.Cancel();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var slow = new ScriptTransport(token => Task.Delay(Timeout.Infinite, token));
        var timed = new ConcurrentQueue<IpPuritySnapshot>();
        var timeoutRunner = new IpPurityRunner(slow, new FixedStun(null), TimeSpan.FromMilliseconds(200));
        await timeoutRunner.RunAsync(new Progress<IpPuritySnapshot>(timed.Enqueue), CancellationToken.None);
        var last = timed.Last();
        Assert.Equal(IpPurityPhase.Failed, last.Phase);
        Assert.True(last.TimedOut);
        Assert.Null(last.Exit!.Ip);
    }

    [Fact]
    public async Task Run_DoesNotPublishAFinishedScoreAfterTheCallerCancels()
    {
        using var cancel = new CancellationTokenSource();
        var snapshots = new ConcurrentQueue<IpPuritySnapshot>();
        var runner = new IpPurityRunner(new ScriptTransport(), new FixedStun("203.0.113.7"), TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Progress<IpPuritySnapshot>(snapshot =>
            {
                snapshots.Enqueue(snapshot);
                if (snapshot.IntelSettled && snapshot.Phase == IpPurityPhase.Running) cancel.Cancel();
            }), cancel.Token));
        Assert.DoesNotContain(snapshots, snapshot => snapshot.Phase == IpPurityPhase.Done);
    }

    private sealed class FixedStun(string? address) : StunProbe
    {
        public override Task<IpPurityUdpObservation> ProbeSafeAsync(StunServer server, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IpPurityUdpObservation(
                server.Host, server.Port, server.Foreign, server.Foreign ? address : "203.0.113.61", 5,
                address is null && server.Foreign ? "请求超时" : null));
        }
    }

    private sealed class ScriptTransport : IIpPurityTransport
    {
        private readonly Func<CancellationToken, Task>? _delay;
        public List<string> Urls { get; } = [];
        public int Started;

        public ScriptTransport(Func<CancellationToken, Task>? delay = null) => _delay = delay;

        public async Task<IpPurityResponse> SendAsync(IpPurityRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Started);
            Urls.Add(request.Url);
            if (_delay is not null) await _delay(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Respond(request.Url);
        }

        private static IpPurityResponse Respond(string url)
        {
            if (url.Contains("cloudflare.com/cdn-cgi/trace", StringComparison.Ordinal) && url.Contains("www.cloudflare.com", StringComparison.Ordinal))
                return Text("ip=203.0.113.7\nloc=US\ncolo=SJC\n");
            if (url.Contains("chatgpt.com/cdn-cgi/trace", StringComparison.Ordinal))
                return Text("ip=203.0.113.7\nloc=US\n");
            if (url.Contains("claude.ai/cdn-cgi/trace", StringComparison.Ordinal))
                return Text("ip=198.51.100.9\nloc=US\n");
            if (url.Contains("/widget/demo/198.51.100.9", StringComparison.Ordinal))
                return Json("{\"data\":{\"ip\":\"198.51.100.9\",\"country\":\"US\",\"company\":{\"name\":\"Alibaba\",\"type\":\"hosting\"},\"asn\":{\"asn\":\"AS45102\",\"name\":\"Alibaba\"},\"privacy\":{\"hosting\":true,\"vpn\":false,\"proxy\":false,\"tor\":false,\"relay\":true}}}");
            if (url.Contains("/widget/demo/", StringComparison.Ordinal))
                return Json("{\"data\":{\"ip\":\"203.0.113.7\",\"country\":\"US\",\"company\":{\"name\":\"Amazon\",\"type\":\"hosting\"},\"asn\":{\"asn\":\"AS16509\",\"name\":\"Amazon\"},\"privacy\":{\"hosting\":true,\"vpn\":true,\"proxy\":false,\"tor\":false,\"relay\":false}}}");
            if (url.Contains("api.openai.com", StringComparison.Ordinal) || url.Contains("api.anthropic.com", StringComparison.Ordinal))
                return Json("{\"object\":\"list\",\"data\":[]}");
            if (url.Contains("api6.ipify.org", StringComparison.Ordinal))
                return Fail("TIMEOUT");
            return Fail("NETWORK");
        }

        private static IpPurityResponse Text(string body) => new(200, body, null, 5, null, true);

        private static IpPurityResponse Json(string body)
        {
            using var document = JsonDocument.Parse(body);
            return new IpPurityResponse(200, body, document.RootElement.Clone(), 5, null, true);
        }

        private static IpPurityResponse Fail(string code) =>
            new(0, null, null, 5, new IpPurityError(code, IpPurityParse.DescribeError(code)), false);
    }
}
