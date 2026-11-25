using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Models.IpPurity;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.IpPurity;

namespace 烧录上位机程序.ViewModels;

public sealed class IpPurityFlagRow
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string Tone { get; init; }
}

public sealed class IpPurityServiceRow
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required string Tone { get; init; }
    public required string Region { get; init; }
    public required string Trace { get; init; }
    public required string Api { get; init; }
    public required string Notes { get; init; }
    public required string Split { get; init; }
    public string? Site { get; init; }
}

public sealed class IpPurityLineRow
{
    public required string Left { get; init; }
    public required string Right { get; init; }
    public required string Note { get; init; }
    public required string Tone { get; init; }
}

public partial class IpPurityViewModel : ObservableObject
{
    private readonly IIpPurityRunner _runner;
    private readonly BackgroundTaskHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _slowTimer;
    private Task? _ownedTask;
    private int _generation;
    private int _slowGeneration;
    private bool _cancelRequested;
    private string? _exitIp;

    public IpPurityViewModel(IIpPurityRunner? runner = null, BackgroundTaskHost? host = null, Dispatcher? dispatcher = null)
    {
        _runner = runner ?? new IpPurityRunner();
        _host = host ?? BackgroundTaskHost.Shared;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _slowTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = SlowAfter };
        _slowTimer.Tick += (_, _) =>
        {
            _slowTimer.Stop();
            if (_slowGeneration != _generation || !IsBusy) return;
            SlowText = "仍在等待第三方响应。可以取消后重试。";
        };
        ShowIdle();
    }

    internal TimeSpan SlowAfter { get; set; } = TimeSpan.FromSeconds(8);

    public ObservableCollection<IpPurityFlagRow> Flags { get; } = new();
    public ObservableCollection<IpPurityServiceRow> Services { get; } = new();
    public ObservableCollection<IpPurityLineRow> ExitRows { get; } = new();
    public ObservableCollection<IpPurityLineRow> UdpRows { get; } = new();
    public ObservableCollection<string> Deductions { get; } = new();
    public ObservableCollection<string> Advice { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();

    [ObservableProperty] private string _headerStatus = "尚未检测";
    [ObservableProperty] private string _summary = "点击「开始检测」，约 10 秒完成 6 项检查。请求由本机直接发往第三方服务，不经过 DL小工具 的服务器。";
    [ObservableProperty] private string _ringPrimary = "—";
    [ObservableProperty] private string _ringSecondary = "尚无评分";
    [ObservableProperty] private string _ringTone = "none";
    [ObservableProperty] private double _ringValue;
    [ObservableProperty] private string _ipText = "";
    [ObservableProperty] private string _locationText = "";
    [ObservableProperty] private string _asnText = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _typeText = "类型未知";
    [ObservableProperty] private string _typeTone = "muted";
    [ObservableProperty] private string _splitText = "";
    [ObservableProperty] private string _consistencyText = "出口一致性";
    [ObservableProperty] private string _consistencyTone = "muted";
    [ObservableProperty] private string _udpText = "UDP 出口";
    [ObservableProperty] private string _udpTone = "muted";
    [ObservableProperty] private string _footnote = "";
    [ObservableProperty] private string _slowText = "";
    [ObservableProperty] private bool _showIdentity;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private bool _showNotes = true;
    [ObservableProperty] private bool _showFailure;
    [ObservableProperty] private bool _showAi;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyIpCommand))]
    private bool _isBusy;

    private bool CanStart() => !IsBusy;
    private bool CanCancel() => IsBusy && !_cancelRequested && _ownedTask is { IsCompleted: false }
        && ReferenceEquals(_host.CurrentTask, _ownedTask);
    private bool CanCopy() => !string.IsNullOrEmpty(_exitIp);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsBusy) return;
        var generation = ++_generation;
        _cancelRequested = false;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_host.TryStart(BackgroundOperationKind.IpPurityCheck, async token =>
            {
                await ready.Task.ConfigureAwait(false);
                await _runner.RunAsync(new DispatcherProgress(_dispatcher, snapshot =>
                {
                    if (generation != _generation) return;
                    Apply(snapshot);
                }), token).ConfigureAwait(false);
            }, out var task))
        {
            HeaderStatus = "另一个后台任务正在进行，请等待结束后重试。";
            return;
        }

        _ownedTask = task;
        IsBusy = true;
        ShowRunning();
        ArmSlow();
        CancelCommand.NotifyCanExecuteChanged();
        ready.TrySetResult();
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) ShowIdle();
        }
        catch (Exception error)
        {
            if (generation == _generation)
            {
                ShowIdle();
                HeaderStatus = "检测失败";
                Summary = error.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_ownedTask, task))
            {
                _slowTimer.Stop();
                _ownedTask = null;
                IsBusy = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!CanCancel()) return;
        _cancelRequested = true;
        _generation++;
        _slowTimer.Stop();
        SlowText = "";
        try { _host.RequestCancellation(); }
        catch (Exception error) { Debug.WriteLine(error); }
        ShowIdle();
        HeaderStatus = "检测已取消";
        Summary = "已取消。可以重新检测。";
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>关窗确认后调用：丢掉尚未上屏的快照，不再把取消收尾写成新的界面状态。</summary>
    internal void AbandonPendingResults()
    {
        _generation++;
        _cancelRequested = true;
        _slowTimer.Stop();
        SlowText = "";
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopyIp()
    {
        if (string.IsNullOrEmpty(_exitIp)) return;
        try
        {
            Clipboard.SetText(_exitIp);
            HeaderStatus = "出口 IP 已复制";
        }
        catch (Exception)
        {
            HeaderStatus = "复制失败，请手动选择地址";
        }
    }

    [RelayCommand]
    private void OpenSite(string? url)
    {
        if (url is null || !IpPurityEndpoints.Services.Any(service => service.Site == url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    internal void ShowSnapshot(IpPuritySnapshot snapshot) => Apply(snapshot);

    private void ArmSlow()
    {
        _slowTimer.Stop();
        _slowTimer.Interval = SlowAfter;
        _slowGeneration = _generation;
        SlowText = "";
        _slowTimer.Start();
    }

    private void ShowIdle()
    {
        HeaderStatus = "尚未检测";
        Summary = "点击「开始检测」，约 10 秒完成 6 项检查。请求由本机直接发往第三方服务，不经过 DL小工具 的服务器。";
        RingPrimary = "—";
        RingSecondary = "尚无评分";
        RingTone = "none";
        RingValue = 0;
        ShowIdentity = false;
        ShowDetails = false;
        ShowNotes = true;
        ShowFailure = false;
        ShowAi = false;
        _exitIp = null;
        CopyIpCommand.NotifyCanExecuteChanged();
        ClearRows();
        ConsistencyText = "出口一致性";
        ConsistencyTone = "muted";
        UdpText = "UDP 出口";
        UdpTone = "muted";
        Footnote = "";
        SplitText = "";
    }

    private void ShowRunning()
    {
        HeaderStatus = "正在获取出口 IP · 0/6";
        Summary = "正在向第三方服务查询出口地址。";
        RingPrimary = "—";
        RingSecondary = "检测中";
        RingTone = "none";
        RingValue = 0;
        ShowIdentity = false;
        ShowDetails = false;
        ShowNotes = false;
        ShowFailure = false;
        ShowAi = true;
        _exitIp = null;
        IpText = "";
        LocationText = "";
        AsnText = "";
        TypeText = "";
        SourceText = "";
        SplitText = "";
        CopyIpCommand.NotifyCanExecuteChanged();
        ClearRows();
        foreach (var service in IpPurityEndpoints.Services)
        {
            Services.Add(Waiting(service.Name));
        }
        ConsistencyText = "全部检查完成后汇总";
        ConsistencyTone = "muted";
        UdpText = "UDP 探测中";
        UdpTone = "muted";
        SlowText = "";
        Footnote = "";
    }

    private void Apply(IpPuritySnapshot snapshot)
    {
        HeaderStatus = snapshot.Phase switch
        {
            IpPurityPhase.Failed => "检测失败 · " + snapshot.Elapsed.TotalSeconds.ToString("0.0") + " 秒",
            IpPurityPhase.Done => "检测完成 · 用时 " + snapshot.Elapsed.TotalSeconds.ToString("0.0") + " 秒",
            _ => snapshot.StageText + " · " + snapshot.Completed + "/6"
        };
        if (snapshot.TimedOut && snapshot.Phase != IpPurityPhase.Running)
        {
            HeaderStatus += " · 已到 30 秒上限";
        }

        var exitReady = snapshot.ExitSettled && snapshot.Exit?.Ip is not null;
        ShowIdentity = exitReady;
        ShowFailure = snapshot.Phase == IpPurityPhase.Failed;
        ShowNotes = false;
        ShowAi = snapshot.Phase != IpPurityPhase.Failed;
        ShowDetails = snapshot.Phase == IpPurityPhase.Done;
        _exitIp = exitReady ? snapshot.Exit!.Ip : null;
        CopyIpCommand.NotifyCanExecuteChanged();

        if (!snapshot.ExitSettled)
        {
            IpText = "";
            RingPrimary = "—";
            RingSecondary = "检测中";
            RingTone = "none";
            RingValue = 0;
            Summary = "正在获取出口 IP。";
        }
        else if (!exitReady)
        {
            RingPrimary = "—";
            RingSecondary = "无法获取";
            RingTone = "none";
            RingValue = 0;
            Summary = "没有拿到出口 IP。请检查网络或代理后重试。";
            Replace(Errors, snapshot.Exit?.Errors ?? []);
        }
        else
        {
            var exit = snapshot.Exit!;
            IpText = exit.Ip!;
            LocationText = FormatPlace(snapshot.Intel, exit);
            AsnText = FormatAsn(snapshot.Intel, exit);
            SourceText = FormatSource(snapshot.Intel);
            (TypeText, TypeTone) = FormatType(snapshot.Intel);
            ReplaceFlags(snapshot.Intel);
            SplitText = FormatSplit(snapshot, exit.Ip!);
            if (!snapshot.IntelSettled)
            {
                Summary = "已拿到出口 IP，正在判断类型与风险标记。";
                TypeText = "类型待定";
                TypeTone = "muted";
            }
        }

        if (snapshot.Phase == IpPurityPhase.Running)
        {
            RingPrimary = snapshot.Completed == 0 ? "—" : snapshot.Completed + "/6";
            RingSecondary = "检测中";
            RingTone = "none";
            RingValue = snapshot.Completed / 6d * 100;
            if (!snapshot.ExitSettled) Summary = "正在获取出口 IP。";
            else if (exitReady && !snapshot.IntelSettled) Summary = "已拿到出口 IP，类型还在确认。";
            else if (exitReady) Summary = "正在检测 AI 服务、UDP 出口和一致性。";
        }

        ReplaceServices(snapshot);
        ReplaceExits(snapshot);
        ReplaceUdp(snapshot);
        ApplyConsistency(snapshot);

        if (snapshot.Phase == IpPurityPhase.Done && snapshot.Score is { } score)
        {
            ApplyScore(snapshot, score);
        }
        else if (snapshot.Phase != IpPurityPhase.Done)
        {
            Deductions.Clear();
            Advice.Clear();
            Footnote = "";
        }
    }

    private void ApplyScore(IpPuritySnapshot snapshot, IpPurityScore score)
    {
        if (score.Total is { } total && score.Grade is { } grade)
        {
            RingPrimary = total.ToString();
            RingSecondary = grade.Label;
            RingTone = grade.Key == "good" ? "ok" : grade.Key == "warn" ? "warn" : "bad";
            RingValue = total;
            Summary = grade.Text;
        }
        else
        {
            RingPrimary = "—";
            RingSecondary = "无法评分";
            RingTone = "none";
            RingValue = 0;
            Summary = score.Advice.FirstOrDefault() ?? "这次没有足够的数据来评分。";
        }

        Deductions.Clear();
        foreach (var item in score.Items)
        {
            if (item.Deduction > 0) Deductions.Add("−" + item.Deduction + " " + item.Label);
            else if (item.Missing && item.Gate) Deductions.Add(item.Label + " 数据不足");
        }
        if (snapshot.Intel?.Flags?.Hosting == true)
        {
            var hosting = score.Items.FirstOrDefault(item => item.Label == "机房 / 托管 IP");
            if (hosting is { Deduction: > 0 }) Deductions.Add("机房 IP 最高 " + (100 - hosting.Deduction) + " 分");
        }
        if (Deductions.Count == 0 && score.Total is not null) Deductions.Add("无扣分");

        Advice.Clear();
        foreach (var line in score.Advice) Advice.Add(line);
        foreach (var service in snapshot.Services)
        {
            var traceIp = service.Verdict?.TraceIp;
            if (traceIp is null || snapshot.Exit?.Ip is null || IpPurityParse.SameIp(traceIp, snapshot.Exit.Ip)) continue;
            Advice.Add(service.Name + " 看到的是另一个出口 " + traceIp + DescribeSplit(service) + "。");
        }
        Footnote = score.Footnote;
    }

    private void ApplyConsistency(IpPuritySnapshot snapshot)
    {
        if (!snapshot.SummarySettled || snapshot.ExitSummary is not { } summary)
        {
            ConsistencyText = snapshot.Phase == IpPurityPhase.Running ? "全部检查完成后汇总" : "出口一致性无法判断";
            ConsistencyTone = "muted";
            return;
        }
        if (summary.Mismatch == true)
        {
            var bits = new List<string>();
            if (summary.DistinctV4 >= 2) bits.Add(summary.DistinctV4 + " 个不同的 IPv4 出口");
            if (summary.DistinctV6 >= 2) bits.Add(summary.DistinctV6 + " 个不同的 IPv6 出口");
            ConsistencyText = bits.Count == 0 ? "出口不一致" : string.Join("、", bits);
            ConsistencyTone = "warn";
        }
        else if (summary.Mismatch == false)
        {
            ConsistencyText = "出口一致";
            ConsistencyTone = "ok";
        }
        else
        {
            ConsistencyText = "出口一致性无法判断";
            ConsistencyTone = "muted";
        }
    }

    private void ReplaceServices(IpPuritySnapshot snapshot)
    {
        Services.Clear();
        if (snapshot.Phase == IpPurityPhase.Failed) return;
        foreach (var info in IpPurityEndpoints.Services)
        {
            var outcome = snapshot.Services.FirstOrDefault(item => item.ServiceId == info.Id);
            if (outcome?.Verdict is not { } verdict)
            {
                Services.Add(Waiting(info.Name));
                continue;
            }
            var tone = verdict.Status switch
            {
                IpPurityCodes.Ok => "ok",
                IpPurityCodes.Risk or IpPurityCodes.Unlisted => "warn",
                IpPurityCodes.Region or IpPurityCodes.Blocked => "bad",
                _ => "muted"
            };
            Services.Add(new IpPurityServiceRow
            {
                Name = info.Name,
                Status = ShortStatus(verdict.Status),
                Tone = tone,
                Region = verdict.RegionText,
                Trace = info.Host + " · " + verdict.TraceText,
                Api = "接口 · " + verdict.ApiText,
                Notes = string.Join("\n", verdict.Notes),
                Split = outcome.SplitIntel is null && outcome.SplitScore is null
                    ? ""
                    : info.Name + " 的出口 " + verdict.TraceIp + DescribeSplit(outcome),
                Site = info.Site
            });
        }
    }

    private void ReplaceExits(IpPuritySnapshot snapshot)
    {
        ExitRows.Clear();
        if (snapshot.ExitSummary is not { } summary) return;
        foreach (var row in summary.Rows)
        {
            var tone = row.Align switch { "match" => "ok", "diff" => "warn", _ => "muted" };
            var note = row.Align switch { "match" => "一致", "diff" => "不一致", _ => "仅一条" };
            ExitRows.Add(new IpPurityLineRow { Left = row.Source, Right = row.Ip, Note = note, Tone = tone });
        }
    }

    private void ReplaceUdp(IpPuritySnapshot snapshot)
    {
        UdpRows.Clear();
        if (!snapshot.UdpSettled)
        {
            UdpText = snapshot.Phase == IpPurityPhase.Running ? "UDP 探测中" : "UDP 无法判断";
            UdpTone = "muted";
            return;
        }
        var report = IpPurityChecks.ClassifyUdp(snapshot.Exit?.Ip, snapshot.Udp);
        foreach (var row in report.Observations)
        {
            var exitIp = snapshot.Exit?.Ip;
            var leaked = row.Foreign && row.Address is not null && report.Leaked.Any(ip => IpPurityParse.SameIp(ip, row.Address));
            var otherFamily = row.Address is not null && exitIp is not null && !IpPurityParse.IsSameFamily(row.Address, exitIp);
            var sameExit = row.Address is not null && exitIp is not null && IpPurityParse.SameIp(row.Address, exitIp);
            var note = row.Address is null
                ? row.Error ?? "无响应"
                : !row.Foreign
                    ? "国内直连，不计入泄漏"
                    : !IpPurityParse.IsPublic(row.Address)
                        ? "内网或保留地址"
                        : otherFamily
                            ? "协议不同，不计入泄漏"
                            : leaked ? "与出口不同" : sameExit ? "与出口一致" : "未计入泄漏";
            var tone = leaked ? "bad" : row.Address is null || !row.Foreign || otherFamily || !IpPurityParse.IsPublic(row.Address) ? "muted" : "ok";
            var right = row.Address ?? "无响应";
            if (row.LatencyMs is { } latency && row.Address is not null) right += " · " + latency + " ms";
            UdpRows.Add(new IpPurityLineRow { Left = row.Host, Right = right, Note = note, Tone = tone });
        }
        (UdpText, UdpTone) = report.State switch
        {
            IpPurityCodes.Leak => ("UDP 检测到泄漏", "bad"),
            IpPurityCodes.Clean => ("UDP 未发现泄漏", "ok"),
            _ => ("UDP 无法判断", "muted")
        };
    }

    private void ReplaceFlags(IpPurityIntel? intel)
    {
        Flags.Clear();
        foreach (var pair in new (string Name, bool? Value)[]
        {
            ("VPN", intel?.Flags?.Vpn), ("代理", intel?.Flags?.Proxy), ("Tor", intel?.Flags?.Tor), ("中继", intel?.Flags?.Relay)
        })
        {
            Flags.Add(new IpPurityFlagRow
            {
                Name = pair.Name,
                Value = pair.Value switch { true => "已标记", false => "无", _ => "未知" },
                Tone = pair.Value switch { true => "bad", false => "ok", _ => "muted" }
            });
        }
    }

    private static string FormatSplit(IpPuritySnapshot snapshot, string exitIp)
    {
        var parts = new List<string>();
        foreach (var service in snapshot.Services)
        {
            var traceIp = service.Verdict?.TraceIp;
            if (traceIp is null || IpPurityParse.SameIp(traceIp, exitIp)) continue;
            parts.Add(service.Name + " → " + traceIp + DescribeSplit(service));
        }
        return parts.Count == 0 ? "" : "AI 服务看到的是另一个出口：" + string.Join("；", parts);
    }

    private static string DescribeSplit(IpPurityServiceOutcome service)
    {
        if (service.SplitIntel is null) return "（这个出口的情报还没有拿到）";
        var type = service.SplitIntel.Type is { } key && IpPurityData.TypeLabels.TryGetValue(key, out var label) ? label : "类型未知";
        var hits = new List<string>();
        if (service.SplitIntel.Flags?.Vpn == true) hits.Add("VPN");
        if (service.SplitIntel.Flags?.Proxy == true) hits.Add("代理");
        if (service.SplitIntel.Flags?.Tor == true) hits.Add("Tor");
        if (service.SplitIntel.Flags?.Relay == true) hits.Add("中继");
        var purity = service.SplitScore?.Total is { } total ? "，纯净度 " + total : "";
        var extra = hits.Count == 0 ? "" : " + " + string.Join(" + ", hits);
        return "（" + type + extra + purity + "）";
    }

    private static string FormatPlace(IpPurityIntel? intel, IpPurityExit exit)
    {
        var geo = intel?.Geo ?? exit.Geo;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(geo?.CountryName)) parts.Add(geo.CountryName!);
        else if (!string.IsNullOrEmpty(geo?.Country)) parts.Add(geo.Country!);
        else if (!string.IsNullOrEmpty(exit.Loc)) parts.Add(exit.Loc);
        if (!string.IsNullOrEmpty(geo?.Region)) parts.Add(geo.Region!);
        if (!string.IsNullOrEmpty(geo?.City)) parts.Add(geo.City!);
        if (intel?.Flags?.Mobile == true) parts.Add("移动网络");
        return parts.Count == 0 ? "地区未知" : string.Join(" · ", parts);
    }

    private static string FormatAsn(IpPurityIntel? intel, IpPurityExit exit)
    {
        var asn = intel?.Asn ?? exit.Asn;
        var name = intel?.AsnName;
        var org = intel?.Org ?? exit.Org;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(asn)) parts.Add(asn);
        if (!string.IsNullOrEmpty(name)) parts.Add(name);
        if (!string.IsNullOrEmpty(org) && org != name) parts.Add(org);
        return parts.Count == 0 ? "运营商未知" : string.Join(" · ", parts);
    }

    private static string FormatSource(IpPurityIntel? intel)
    {
        if (intel?.Source is null) return snapshotErrors(intel);
        var confidence = intel.Confidence is { } key && IpPurityData.ConfidenceLabels.TryGetValue(key, out var label)
            ? label
            : "准确度未知";
        return "情报来源 " + intel.Source + " · " + confidence;
    }

    private static string snapshotErrors(IpPurityIntel? intel)
    {
        if (intel?.Errors is { Count: > 0 }) return "情报没有判定出类型：" + intel.Errors[0];
        return "正在获取 IP 类型与风险";
    }

    private static (string Text, string Tone) FormatType(IpPurityIntel? intel)
    {
        if (intel?.Type is { } type && IpPurityData.TypeLabels.TryGetValue(type, out var label))
        {
            var tone = type switch
            {
                IpPurityCodes.Isp => "ok",
                IpPurityCodes.Business => "muted",
                _ => "warn"
            };
            return (label, tone);
        }
        return ("类型未知", "muted");
    }

    private static string ShortStatus(string status) => status switch
    {
        IpPurityCodes.Ok => "可直连",
        IpPurityCodes.Risk => "有风控风险",
        IpPurityCodes.Unlisted => "地区不在支持列表",
        IpPurityCodes.Region => "地区受限",
        IpPurityCodes.Blocked => "无法连接",
        _ => "无数据"
    };

    private static IpPurityServiceRow Waiting(string name) => new()
    {
        Name = name,
        Status = "检测中",
        Tone = "muted",
        Region = "",
        Trace = "",
        Api = "",
        Notes = "",
        Split = "",
        Site = null
    };

    private void ClearRows()
    {
        Flags.Clear();
        Services.Clear();
        ExitRows.Clear();
        UdpRows.Clear();
        Deductions.Clear();
        Advice.Clear();
        Errors.Clear();
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private sealed class DispatcherProgress(Dispatcher dispatcher, Action<IpPuritySnapshot> apply) : IProgress<IpPuritySnapshot>
    {
        public void Report(IpPuritySnapshot value)
        {
            if (dispatcher.CheckAccess()) apply(value);
            else dispatcher.InvokeAsync(() => apply(value));
        }
    }
}
