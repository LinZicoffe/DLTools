using System.Text.Json;
using 烧录上位机程序.Models.IpPurity;

namespace 烧录上位机程序.Services.IpPurity;

/// <summary>出口、情报、AI 服务判定与出口汇总。控制流对齐 checks.js；桌面版始终尝试 ip-api 回退。</summary>
public sealed class IpPurityChecks
{
    private readonly IIpPurityTransport _transport;

    public IpPurityChecks(IIpPurityTransport transport)
    {
        _transport = transport;
    }

    public async Task<IpPurityExit> ExitAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var source in IpPurityEndpoints.ExitSources)
        {
            var response = await _transport.SendAsync(
                new IpPurityRequest(source.Url, ExpectJson: !source.IsText), cancellationToken).ConfigureAwait(false);
            string? ip = null;
            string? loc = null;
            string? colo = null;
            string? hostname = null;
            string? org = null;
            string? asn = null;
            string? timezone = null;
            IpPurityGeo? geo = null;
            if (response.Ok)
            {
                if (source.IsText)
                {
                    var trace = IpPurityParse.ParseTrace(response.Text);
                    trace.TryGetValue("ip", out ip);
                    trace.TryGetValue("loc", out loc);
                    trace.TryGetValue("colo", out colo);
                }
                else if (response.Data is { } data)
                {
                    ip = IpPurityParse.JsonString(data, "ip");
                    hostname = IpPurityParse.JsonString(data, "hostname");
                    org = IpPurityParse.JsonString(data, "org");
                    asn = IpPurityParse.JsonString(data, "asn");
                    timezone = IpPurityParse.JsonString(data, "timezone");
                    geo = IpPurityParse.PickGeo(data);
                }
            }

            if (!IpPurityParse.IsIpLike(ip)) ip = null;
            if (ip is not null)
            {
                return new IpPurityExit(ip, source.Name, response.LatencyMs, loc, colo, hostname, org, asn, timezone, geo, errors);
            }
            errors.Add(source.Name + "：" + IpPurityParse.FailText(response));
        }
        return new IpPurityExit(null, null, null, null, null, null, null, null, null, null, errors);
    }

    public async Task<IpPurityIntel?> IntelAsync(string ip, IpPurityExit? fallback, CancellationToken cancellationToken)
    {
        fallback ??= new IpPurityExit(ip, null, null, null, null, null, null, null, null, null, []);
        var widget = IpPurityEndpoints.BuildIntelUrl(ip);
        if (widget is null) return null;
        var response = await _transport.SendAsync(new IpPurityRequest(widget), cancellationToken).ConfigureAwait(false);
        if (response.Ok && response.Data is { } root
            && IpPurityParse.JsonObject(root, "data") is { } payload
            && IpPurityParse.SameIp(IpPurityParse.JsonString(payload, "ip"), ip))
        {
            return FromIpinfo(ip, payload);
        }

        var errors = new List<string> { "IPinfo：" + IpPurityParse.FailText(response) };
        response = await _transport.SendAsync(new IpPurityRequest(IpPurityEndpoints.IpApiUrl), cancellationToken).ConfigureAwait(false);
        if (response.Ok && response.Data is { } ipApi
            && IpPurityParse.JsonString(ipApi, "status") == "success"
            && IpPurityParse.SameIp(IpPurityParse.JsonString(ipApi, "query"), ip))
        {
            return FromIpApi(ipApi);
        }
        errors.Add("ip-api.com：" + MismatchOrFail(response));

        var asn = fallback.Asn;
        var org = fallback.Org;
        var hostname = fallback.Hostname;
        var timezone = fallback.Timezone;
        var geo = fallback.Geo;
        response = await _transport.SendAsync(new IpPurityRequest(IpPurityEndpoints.IpinfoJsonUrl), cancellationToken).ConfigureAwait(false);
        if (response.Ok && response.Data is { } basic && IpPurityParse.SameIp(IpPurityParse.JsonString(basic, "ip"), ip))
        {
            asn = IpPurityParse.ExtractAsn(IpPurityParse.JsonString(basic, "org")) ?? asn;
            org = IpPurityParse.JsonString(basic, "org") ?? org;
            hostname = IpPurityParse.JsonString(basic, "hostname") ?? hostname;
            timezone = IpPurityParse.JsonString(basic, "timezone") ?? timezone;
            geo ??= IpPurityParse.PickGeo(basic);
        }
        else
        {
            errors.Add("IPinfo（基础信息）：" + MismatchOrFail(response));
        }

        if (string.IsNullOrEmpty(asn) && string.IsNullOrEmpty(org))
        {
            response = await _transport.SendAsync(new IpPurityRequest(IpPurityEndpoints.IpapiJsonUrl), cancellationToken).ConfigureAwait(false);
            if (response.Ok && response.Data is { } ipapi && IpPurityParse.SameIp(IpPurityParse.JsonString(ipapi, "ip"), ip))
            {
                asn = IpPurityParse.JsonString(ipapi, "asn") ?? asn;
                org = IpPurityParse.JsonString(ipapi, "org") ?? org;
                geo ??= IpPurityParse.PickGeo(ipapi);
            }
            else
            {
                errors.Add("ipapi.co：" + MismatchOrFail(response));
            }
        }

        if (string.IsNullOrEmpty(asn) && string.IsNullOrEmpty(org))
        {
            return new IpPurityIntel(ip, null, null, null, null, null, null, hostname, timezone, null, null, geo, errors);
        }

        var hosting = IpPurityParse.HeuristicHosting(asn, org, null, hostname);
        return new IpPurityIntel(
            ip, "ASN / 组织名推断", IpPurityCodes.Low, asn, null, org, null, hostname, timezone,
            hosting == true ? IpPurityCodes.Hosting : null,
            new IpPurityFlags(hosting, null, null, null, null, null),
            geo, errors);
    }

    public async Task<string?> Ipv6Async(CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(
            new IpPurityRequest(IpPurityEndpoints.Ipv6Url, Timeout: TimeSpan.FromSeconds(4)), cancellationToken).ConfigureAwait(false);
        var ip = response.Ok && response.Data is { } data ? IpPurityParse.JsonString(data, "ip") : null;
        return IpPurityParse.IsIpLike(ip) ? ip : null;
    }

    public async Task<(IpPurityResponse Trace, IpPurityResponse Api)> ProbeServiceAsync(
        IpPurityServiceInfo service, CancellationToken cancellationToken)
    {
        var traceTask = _transport.SendAsync(new IpPurityRequest(service.TraceUrl, ExpectJson: false), cancellationToken);
        var apiTask = _transport.SendAsync(new IpPurityRequest(service.ApiUrl, service.Headers), cancellationToken);
        await Task.WhenAll(traceTask, apiTask).ConfigureAwait(false);
        return (await traceTask.ConfigureAwait(false), await apiTask.ConfigureAwait(false));
    }

    public static IpPurityServiceVerdict Judge(
        IpPurityServiceInfo service,
        IpPurityResponse trace,
        IpPurityResponse api,
        IpPurityIntel? intel,
        bool intelPending)
    {
        var parsed = trace.Ok ? IpPurityParse.ParseTrace(trace.Text) : null;
        parsed ??= new Dictionary<string, string>();
        var traceReachable = parsed.TryGetValue("ip", out var traceIp) && !string.IsNullOrEmpty(traceIp);
        if (!traceReachable) traceIp = null;
        var body = api.Data is { ValueKind: JsonValueKind.Object } element ? element : default;
        var hasBody = api.Data is { ValueKind: JsonValueKind.Object };
        var loc = traceReachable && parsed.TryGetValue("loc", out var rawLoc) && !string.IsNullOrEmpty(rawLoc)
            ? rawLoc.ToUpperInvariant()
            : "";
        var regionHit = api.Status == 403 && RefusesForRegion(service, hasBody ? body : default, loc);
        var apiReachable = api.Ok
            || api.Status is 400 or 401 or 404 or 405 or 429
            || (api.Status == 403 && !regionHit && ApiErrorBody(hasBody ? body : default));

        var notes = new List<string>();
        var risks = new List<string>();
        var serviceIp = traceReachable ? traceIp : null;
        var intelApplies = !(serviceIp is not null && intel?.Ip is not null) || IpPurityParse.SameIp(serviceIp, intel!.Ip);
        var flags = intelApplies ? intel?.Flags : null;
        if (!intelApplies)
        {
            notes.Add("该服务看到的出口与已评估的 IP 不同，未套用机房、VPN 等风险标记");
        }
        else if (flags is not null)
        {
            foreach (var pair in new (bool? Value, string Name)[]
            {
                (flags.Hosting, "机房 IP"), (flags.Vpn, "VPN"), (flags.Proxy, "代理"), (flags.Tor, "Tor"), (flags.Relay, "中继")
            })
            {
                if (pair.Value == true) risks.Add(pair.Name);
            }
            var assessed = flags.Hosting is not null || flags.Vpn is not null || flags.Proxy is not null
                || flags.Tor is not null || flags.Relay is not null;
            if (intel?.Source is null || !assessed) notes.Add("未能获取 IP 类型信息，无法评估风控风险");
        }
        else if (intelPending)
        {
            notes.Add("IP 类型尚未判定，结果稍后更新");
        }
        else
        {
            notes.Add("未能获取 IP 类型信息，无法评估风控风险");
        }

        bool? regionListed = null;
        var regionText = "无法判定";
        if (loc.Length > 0)
        {
            var listed = IpPurityData.IsListed(service.RegionSet, loc);
            regionListed = listed;
            regionText = listed ? "所在地区在支持列表内" : "所在地区不在支持列表内";
            if (!listed) notes.Add("Cloudflare 判定所在地区为 " + loc + "，不在 " + service.Vendor + " 的支持列表内");
            var geoCountry = intelApplies ? intel?.Geo?.Country : null;
            if (!string.IsNullOrEmpty(geoCountry) && !string.Equals(geoCountry, loc, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("IP 数据库记录的地区（" + geoCountry + "）与 " + service.Host + " 判定的地区（" + loc + "）不一致");
            }
        }
        else if (traceReachable)
        {
            regionText = "所在地区无法判定";
        }

        string status;
        if (regionHit) status = IpPurityCodes.Region;
        else if (!apiReachable) status = IpPurityCodes.Blocked;
        else if (risks.Count > 0) status = IpPurityCodes.Risk;
        else if (regionListed == false) status = IpPurityCodes.Unlisted;
        else status = IpPurityCodes.Ok;

        if (!traceReachable)
        {
            notes.Add(service.Host + " 未连通（" + (trace.Error?.Message ?? "无有效数据") + "），请在浏览器中实际打开确认");
        }
        else if (!apiReachable && api.Status == 0)
        {
            notes.Add(service.Host + " 可以访问，但 " + ApiHost(service.ApiUrl) + " 连接失败（"
                + (api.Error?.Message ?? "无响应") + "）。常见原因：代理规则没有覆盖该域名、网络拦截或 DNS 问题");
        }
        if (status == IpPurityCodes.Region)
        {
            notes.Add(service.Vendor + " 的接口返回了地区限制（HTTP 403），当前出口所在地区不受支持");
        }
        if (api.Status == 403 && status == IpPurityCodes.Blocked)
        {
            notes.Add(service.Vendor + " 拒绝了来自该 IP 的请求（HTTP 403）");
        }

        var label = status switch
        {
            IpPurityCodes.Ok => "可直连",
            IpPurityCodes.Risk => "可连接但有风控风险",
            IpPurityCodes.Unlisted => "可连接，地区不在支持列表",
            IpPurityCodes.Region => "地区受限",
            _ => "无法连接"
        };
        var traceText = traceReachable
            ? "可达 · " + trace.LatencyMs + " ms"
            : "不可达 · " + (trace.Error?.Message ?? "无有效数据");
        var apiText = apiReachable
            ? "可达 · " + api.LatencyMs + " ms"
            : api.Error?.Code == "PARSE"
                ? "不可达 · 返回内容无法解析"
                : api.Status > 0
                    ? "已拒绝 · HTTP " + api.Status
                    : "不可达 · " + (api.Error?.Message ?? "无响应");
        return new IpPurityServiceVerdict(
            status, label, regionListed, regionText, risks, notes,
            traceReachable, traceIp, traceReachable ? parsed.GetValueOrDefault("loc") : null, traceText,
            apiReachable, apiText, api.Status);
    }

    public static IpPurityExitSummary SummarizeExits(IReadOnlyList<(string Source, string Ip)> rows, string? exitIp)
    {
        var list = rows.Where(row => IpPurityParse.IsIpLike(row.Ip)).ToList();
        var v4 = list.Where(row => IpPurityParse.IsV4(row.Ip)).Select(row => row.Ip).ToList();
        var v6 = list.Where(row => !IpPurityParse.IsV4(row.Ip)).Select(row => row.Ip).ToList();
        var distinctV4 = Unique(v4);
        var distinctV6 = Unique(v6);
        var comparable = v4.Count >= 2 || v6.Count >= 2;
        var mapped = list.Select(row =>
        {
            var isV6 = !IpPurityParse.IsV4(row.Ip);
            var group = isV6 ? v6 : v4;
            var reference = (isV6 ? distinctV6 : distinctV4).FirstOrDefault();
            var align = group.Count < 2 ? "single" : reference is not null && IpPurityParse.SameIp(row.Ip, reference) ? "match" : "diff";
            return new IpPurityExitRow(row.Source, row.Ip, isV6, align, exitIp is not null && IpPurityParse.SameIp(row.Ip, exitIp));
        }).ToList();
        return new IpPurityExitSummary(
            mapped,
            distinctV4.FirstOrDefault(),
            distinctV6.FirstOrDefault(),
            distinctV4.Count,
            distinctV6.Count,
            comparable ? distinctV4.Count >= 2 || distinctV6.Count >= 2 : null);
    }

    public static IpPurityUdpReport ClassifyUdp(string? exitIp, IReadOnlyList<IpPurityUdpObservation> rows)
    {
        var foreignPublic = rows
            .Where(row => row.Foreign && row.Address is not null && IpPurityParse.IsPublic(row.Address))
            .ToList();
        var leaked = new List<string>();
        if (exitIp is not null)
        {
            foreach (var row in foreignPublic)
            {
                if (!IpPurityParse.IsSameFamily(row.Address!, exitIp)) continue;
                if (IpPurityParse.SameIp(row.Address, exitIp)) continue;
                if (leaked.Any(kept => IpPurityParse.SameIp(kept, row.Address))) continue;
                leaked.Add(row.Address!);
            }
        }

        string state;
        if (exitIp is null || foreignPublic.Count == 0) state = IpPurityCodes.Unknown;
        else if (leaked.Count > 0) state = IpPurityCodes.Leak;
        else if (foreignPublic.All(row => !IpPurityParse.IsSameFamily(row.Address!, exitIp))) state = IpPurityCodes.Unknown;
        else state = IpPurityCodes.Clean;
        return new IpPurityUdpReport(state, leaked, rows);
    }

    private static IpPurityIntel FromIpinfo(string ip, JsonElement data)
    {
        var privacy = IpPurityParse.JsonObject(data, "privacy");
        var company = IpPurityParse.JsonObject(data, "company");
        var asn = IpPurityParse.JsonObject(data, "asn");
        var type = (company is { } companyValue ? IpPurityParse.JsonString(companyValue, "type") : null)
            ?? (asn is { } asnValue ? IpPurityParse.JsonString(asnValue, "type") : null);
        bool? hosting = privacy is { } privacyValue ? IpPurityParse.JsonBool(privacyValue, "hosting") : null;
        hosting ??= IpPurityParse.JsonBool(data, "is_hosting");
        if (hosting is null && type is not null) hosting = type == IpPurityCodes.Hosting;
        return new IpPurityIntel(
            IpPurityParse.JsonString(data, "ip") ?? ip,
            "IPinfo",
            IpPurityCodes.High,
            asn is { } asnNode ? IpPurityParse.JsonString(asnNode, "asn") : null,
            asn is { } asnNameNode ? IpPurityParse.JsonString(asnNameNode, "name") : null,
            company is { } companyNode ? IpPurityParse.JsonString(companyNode, "name") : null,
            asn is { } routeNode ? IpPurityParse.JsonString(routeNode, "route") : null,
            IpPurityParse.JsonString(data, "hostname"),
            IpPurityParse.JsonString(data, "timezone"),
            type,
            new IpPurityFlags(
                hosting,
                privacy is { } vpn ? IpPurityParse.JsonBool(vpn, "vpn") : null,
                privacy is { } proxy ? IpPurityParse.JsonBool(proxy, "proxy") : null,
                privacy is { } tor ? IpPurityParse.JsonBool(tor, "tor") : null,
                privacy is { } relay ? IpPurityParse.JsonBool(relay, "relay") : null,
                IpPurityParse.JsonBool(data, "is_mobile")),
            new IpPurityGeo(
                IpPurityParse.JsonString(data, "country"),
                null,
                IpPurityParse.JsonString(data, "region"),
                IpPurityParse.JsonString(data, "city")),
            []);
    }

    private static IpPurityIntel FromIpApi(JsonElement data)
    {
        var hosting = IpPurityParse.JsonBool(data, "hosting");
        return new IpPurityIntel(
            IpPurityParse.JsonString(data, "query") ?? "",
            "ip-api.com",
            IpPurityCodes.Medium,
            IpPurityParse.ExtractAsn(IpPurityParse.JsonString(data, "as")),
            IpPurityParse.JsonString(data, "asname"),
            IpPurityParse.JsonString(data, "isp") ?? IpPurityParse.JsonString(data, "org"),
            null,
            null,
            null,
            hosting == true ? IpPurityCodes.Hosting : null,
            new IpPurityFlags(hosting, null, IpPurityParse.JsonBool(data, "proxy"), null, null, IpPurityParse.JsonBool(data, "mobile")),
            new IpPurityGeo(
                IpPurityParse.JsonString(data, "countryCode"),
                IpPurityParse.JsonString(data, "country"),
                IpPurityParse.JsonString(data, "regionName"),
                IpPurityParse.JsonString(data, "city")),
            []);
    }

    private static bool RefusesForRegion(IpPurityServiceInfo service, JsonElement body, string loc)
    {
        if (body.ValueKind == JsonValueKind.Object && IpPurityData.IsRegionError(service.Id, body)) return true;
        if (service.Id != "claude" || loc.Length == 0 || body.ValueKind != JsonValueKind.Object) return false;
        if (!IpPurityData.IsPermissionError(body)) return false;
        return !IpPurityData.IsListed(service.RegionSet, loc);
    }

    private static bool ApiErrorBody(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return false;
        if (IpPurityParse.JsonString(body, "type") == "error") return true;
        return body.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
    }

    private static string MismatchOrFail(IpPurityResponse response)
    {
        if (!response.Ok || response.Data is not { } data) return IpPurityParse.FailText(response);
        var status = IpPurityParse.JsonString(data, "status");
        if (!string.IsNullOrEmpty(status) && status != "success")
        {
            return IpPurityParse.JsonString(data, "message") ?? IpPurityParse.FailText(response);
        }
        if (!string.IsNullOrEmpty(IpPurityParse.JsonString(data, "ip")) || !string.IsNullOrEmpty(IpPurityParse.JsonString(data, "query")))
        {
            return "返回的是另一个出口地址，已忽略";
        }
        return IpPurityParse.FailText(response);
    }

    private static string ApiHost(string url)
    {
        var rest = url.StartsWith("https://", StringComparison.Ordinal) ? url[8..] : url.Replace("http://", "", StringComparison.Ordinal);
        var slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest[..slash];
    }

    private static List<string> Unique(IReadOnlyList<string> ips)
    {
        var result = new List<string>();
        foreach (var ip in ips)
        {
            if (!result.Any(kept => IpPurityParse.SameIp(kept, ip))) result.Add(ip);
        }
        return result;
    }
}
