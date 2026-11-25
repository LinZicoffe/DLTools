using System.Diagnostics;
using 烧录上位机程序.Models.IpPurity;

namespace 烧录上位机程序.Services.IpPurity;

public interface IIpPurityRunner
{
    Task RunAsync(IProgress<IpPuritySnapshot> progress, CancellationToken cancellationToken);
}

/// <summary>六项检查并行推进，每完成一项推一次快照。整次最长 30 秒；用户取消会中断在途请求并向上抛出。</summary>
public sealed class IpPurityRunner : IIpPurityRunner
{
    public static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromSeconds(30);

    private readonly IIpPurityTransport _transport;
    private readonly StunProbe _stun;
    private readonly TimeSpan _overallTimeout;

    public IpPurityRunner(IIpPurityTransport? transport = null, StunProbe? stun = null, TimeSpan? overallTimeout = null)
    {
        _transport = transport ?? new IpPurityTransport();
        _stun = stun ?? new StunProbe();
        _overallTimeout = overallTimeout is { } given && given > TimeSpan.Zero ? given : DefaultOverallTimeout;
    }

    public async Task RunAsync(IProgress<IpPuritySnapshot> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        using var overall = new CancellationTokenSource(_overallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overall.Token);
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var checks = new IpPurityChecks(new GuardingTransport(_transport, cancellationToken));
        var started = Stopwatch.GetTimestamp();
        var gate = new object();
        var state = new Mutable(started);

        void Publish()
        {
            IpPuritySnapshot snapshot;
            lock (gate) snapshot = state.Freeze();
            progress.Report(snapshot);
        }

        Publish();
        var chatgpt = RunServiceAsync(checks, IpPurityEndpoints.Services[0], siblings.Token, cancellationToken, state, gate, Publish);
        var claude = RunServiceAsync(checks, IpPurityEndpoints.Services[1], siblings.Token, cancellationToken, state, gate, Publish);
        var ipv6 = RunIpv6Async(checks, siblings.Token, cancellationToken, state, gate, Publish);
        var udp = RunUdpAsync(siblings.Token, cancellationToken, state, gate, Publish);

        try
        {
        try
        {
            IpPurityExit exit;
            try
            {
                exit = await checks.ExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                exit = new IpPurityExit(null, null, null, null, null, null, null, null, null, null, ["出口 IP：请求超时"]);
            }

            lock (gate)
            {
                state.Exit = exit;
                state.ExitSettled = true;
                if (exit.Ip is null) state.IntelSettled = true;
            }
            Publish();

            if (exit.Ip is null)
            {
                siblings.Cancel();
                await DrainAsync(cancellationToken, chatgpt, claude, ipv6, udp).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    state.Phase = IpPurityPhase.Failed;
                    state.SummarySettled = true;
                    state.TimedOut = overall.IsCancellationRequested;
                }
                Publish();
                return;
            }

            IpPurityIntel? intel;
            try
            {
                intel = await checks.IntelAsync(exit.Ip, exit, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                intel = null;
            }
            lock (gate)
            {
                state.Intel = intel;
                state.IntelSettled = true;
                foreach (var raw in state.Raw.Values)
                {
                    var verdict = IpPurityChecks.Judge(raw.Service, raw.Trace, raw.Api, intel, intelPending: false);
                    var previous = state.Services.First(item => item.ServiceId == raw.Service.Id);
                    state.SetService(previous with { Verdict = verdict });
                }
            }
            Publish();

            await DrainAsync(cancellationToken, chatgpt, claude, ipv6, udp).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (overall.IsCancellationRequested) siblings.Cancel();

            await ScoreSplitAsync(checks, exit.Ip, linked.Token, cancellationToken, state, gate).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                state.ExitSummary = BuildSummary(state);
                state.SummarySettled = true;
                state.UdpReport = IpPurityChecks.ClassifyUdp(exit.Ip, state.Udp);
                state.Score = IpPurityScoring.ComputeV2(ToRequest(state));
                state.Phase = IpPurityPhase.Done;
                state.TimedOut = overall.IsCancellationRequested;
            }
            Publish();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        }
        finally
        {
            siblings.Cancel();
            try
            {
                await DrainAsync(cancellationToken, chatgpt, claude, ipv6, udp).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 取消路径已经向上抛出；这里只负责把在途探测排空。
            }
        }
    }

    private static async Task RunServiceAsync(
        IpPurityChecks checks, IpPurityServiceInfo service, CancellationToken token, CancellationToken userToken,
        Mutable state, object gate, Action publish)
    {
        try
        {
            var probed = await checks.ProbeServiceAsync(service, token).ConfigureAwait(false);
            lock (gate)
            {
                state.Raw[service.Id] = new RawProbe(service, probed.Trace, probed.Api);
                var verdict = IpPurityChecks.Judge(
                    service, probed.Trace, probed.Api, state.Intel, intelPending: !state.IntelSettled);
                state.SetService(new IpPurityServiceOutcome(service.Id, service.Name, service.Site, verdict, null, null));
            }
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            lock (gate)
            {
                state.SetService(new IpPurityServiceOutcome(service.Id, service.Name, service.Site, null, null, null));
            }
        }
        publish();
    }

    private static async Task RunIpv6Async(
        IpPurityChecks checks, CancellationToken token, CancellationToken userToken,
        Mutable state, object gate, Action publish)
    {
        string? ip = null;
        try
        {
            ip = await checks.Ipv6Async(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            ip = null;
        }
        lock (gate)
        {
            state.Ipv6 = ip;
            state.Ipv6Settled = true;
        }
        publish();
    }

    private async Task RunUdpAsync(
        CancellationToken token, CancellationToken userToken, Mutable state, object gate, Action publish)
    {
        var tasks = StunProbe.Servers.Select(server => ObserveAsync(server, token, userToken));
        var rows = await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (gate)
        {
            state.Udp = rows;
            state.UdpSettled = true;
        }
        publish();
    }

    private async Task<IpPurityUdpObservation> ObserveAsync(StunServer server, CancellationToken token, CancellationToken userToken)
    {
        try
        {
            return await _stun.ProbeSafeAsync(server, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            return new IpPurityUdpObservation(server.Host, server.Port, server.Foreign, null, null, "请求超时");
        }
    }

    private static async Task ScoreSplitAsync(
        IpPurityChecks checks, string exitIp, CancellationToken token, CancellationToken userToken,
        Mutable state, object gate)
    {
        List<IpPurityServiceOutcome> pending;
        lock (gate) pending = state.Services.ToList();
        foreach (var outcome in pending)
        {
            var traceIp = outcome.Verdict?.TraceIp;
            if (traceIp is null || IpPurityParse.SameIp(traceIp, exitIp)) continue;
            IpPurityIntel? intel = null;
            try
            {
                intel = await checks.IntelAsync(traceIp, null, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
            {
                intel = null;
            }
            IpPurityScore? score = intel?.Source is not null ? IpPurityScoring.PurityOf(traceIp, intel) : null;
            lock (gate)
            {
                state.SetService(outcome with { SplitIntel = intel, SplitScore = score });
            }
        }
    }

    private static async Task DrainAsync(CancellationToken userToken, params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 总时限到了：各任务自己会把未完成项收成超时。
        }
    }

    private static IpPurityExitSummary BuildSummary(Mutable state)
    {
        var rows = new List<(string Source, string Ip)>();
        if (state.Exit?.Ip is { } exitIp && state.Exit.Source is not null) rows.Add((state.Exit.Source, exitIp));
        foreach (var service in IpPurityEndpoints.Services)
        {
            var outcome = state.Services.FirstOrDefault(item => item.ServiceId == service.Id);
            if (outcome?.Verdict is { TraceReachable: true, TraceIp: { } ip }) rows.Add((service.Name, ip));
        }
        if (state.Ipv6 is { } ipv6) rows.Add(("IPv6 探测", ipv6));
        return IpPurityChecks.SummarizeExits(rows, state.Exit?.Ip);
    }

    private static IpPurityScoreRequest ToRequest(Mutable state)
    {
        var report = state.UdpReport ?? IpPurityChecks.ClassifyUdp(state.Exit?.Ip, state.Udp);
        return new IpPurityScoreRequest(
            state.Exit?.Ip,
            IpPurityParse.ToScoreIntel(state.Intel),
            state.Services.Select(service => new IpPurityScoreService(
                service.Name, service.Verdict?.Status, service.Verdict?.RegionListed)).ToList(),
            state.ExitSummary is { } summary
                ? new IpPurityScoreConsistency(summary.Mismatch, summary.DistinctV4, summary.DistinctV6)
                : null,
            new IpPurityScoreLeak(report.State, report.Leaked, state.UdpSettled, state.Exit?.Ip is not null));
    }

    private sealed record RawProbe(IpPurityServiceInfo Service, IpPurityResponse Trace, IpPurityResponse Api);

    private sealed class GuardingTransport(IIpPurityTransport inner, CancellationToken userToken) : IIpPurityTransport
    {
        public async Task<IpPurityResponse> SendAsync(IpPurityRequest request, CancellationToken cancellationToken)
        {
            userToken.ThrowIfCancellationRequested();
            try
            {
                return await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
            {
                return new IpPurityResponse(0, null, null, 0, new IpPurityError("TIMEOUT", "请求超时"), false);
            }
        }
    }

    private sealed class Mutable(long started)
    {
        public IpPurityPhase Phase = IpPurityPhase.Running;
        public IpPurityExit? Exit;
        public bool ExitSettled;
        public IpPurityIntel? Intel;
        public bool IntelSettled;
        public List<IpPurityServiceOutcome> Services = [];
        public Dictionary<string, RawProbe> Raw = new(StringComparer.Ordinal);
        public IpPurityUdpObservation[] Udp = [];
        public bool UdpSettled;
        public IpPurityUdpReport? UdpReport;
        public string? Ipv6;
        public bool Ipv6Settled;
        public IpPurityExitSummary? ExitSummary;
        public bool SummarySettled;
        public IpPurityScore? Score;
        public bool TimedOut;

        public void SetService(IpPurityServiceOutcome outcome)
        {
            Services = Services.Where(item => item.ServiceId != outcome.ServiceId).Append(outcome).ToList();
        }

        public IpPuritySnapshot Freeze()
        {
            var ordered = IpPurityEndpoints.Services
                .Select(service => Services.FirstOrDefault(item => item.ServiceId == service.Id))
                .Where(item => item is not null)
                .Cast<IpPurityServiceOutcome>()
                .ToList();
            var completed = (ExitSettled ? 1 : 0) + (IntelSettled ? 1 : 0)
                + (ordered.Any(item => item.ServiceId == "chatgpt") ? 1 : 0)
                + (ordered.Any(item => item.ServiceId == "claude") ? 1 : 0)
                + (UdpSettled ? 1 : 0) + (SummarySettled ? 1 : 0);
            return new IpPuritySnapshot(
                Phase,
                completed,
                StageText(),
                Exit,
                ExitSettled,
                Intel,
                IntelSettled,
                ordered,
                Udp,
                UdpSettled,
                Ipv6,
                Ipv6Settled,
                SummarySettled ? ExitSummary : null,
                SummarySettled,
                Phase == IpPurityPhase.Done ? Score : null,
                TimedOut,
                Stopwatch.GetElapsedTime(started));
        }

        private string StageText()
        {
            if (Phase == IpPurityPhase.Failed) return "未能获取出口 IP";
            if (Phase == IpPurityPhase.Done) return "检测完成";
            if (!ExitSettled) return "正在获取出口 IP";
            if (!IntelSettled) return "正在获取 IP 类型与风险";
            if (!Services.Any(item => item.ServiceId == "chatgpt")) return "正在检测 ChatGPT";
            if (!Services.Any(item => item.ServiceId == "claude")) return "正在检测 Claude";
            if (!UdpSettled) return "正在探测 UDP 出口";
            return "正在汇总出口一致性";
        }
    }
}
