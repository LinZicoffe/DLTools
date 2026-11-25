using 烧录上位机程序.Models.IpPurity;

namespace 烧录上位机程序.Services.IpPurity;

/// <summary>纯净度评分。ComputeV1 与上游 score.js 逐项对齐，只作为差分基准；
/// 界面使用 ComputeV2：分数只含网络身份和隐私标记，其余是状态。</summary>
public static class IpPurityScoring
{
    public const string Footnote =
        "隐私标记同时命中时会递减，合计最多扣 50 分；AI 服务合计最多扣 32 分。机房扣分随情报准确度下降，只靠组织名匹配不会按已确认机房的额度扣满。";

    private const int BusinessPoints = 10;
    private const int EducationPoints = 16;
    private const int PrivacyCap = 50;
    private const int ServiceCap = 32;
    private const int ExitOne = 16;
    private const int ExitBoth = 20;
    private const int ExitExtra = 2;
    private const int ExitCap = 24;
    private const int LeakOne = 16;
    private const int LeakMany = 20;
    private const int GradeGood = 85;
    private const int GradeWarn = 60;

    private static readonly Dictionary<string, int> PrivacyWeight = new(StringComparer.Ordinal)
    {
        ["tor"] = 45, ["vpn"] = 24, ["proxy"] = 22, ["relay"] = 18
    };

    private static readonly (string Key, string Label, string Clear, string Hit)[] PrivacyMeta =
    [
        ("tor", "Tor 标记", "无 Tor 标记", "属于 Tor 出口节点"),
        ("vpn", "VPN 标记", "无 VPN 标记", "已被标记为 VPN"),
        ("proxy", "代理标记", "无代理标记", "已被标记为代理"),
        ("relay", "中继标记", "无中继标记", "已被标记为中继"),
    ];

    public static IpPurityScore ComputeV1(IpPurityScoreRequest request) =>
        Finalize(Build(request), scoredOnly: false);

    public static IpPurityScore ComputeV2(IpPurityScoreRequest request) =>
        Finalize(Build(request), scoredOnly: true);

    public static IpPurityScore PurityOf(string ip, IpPurityIntel intel) =>
        ComputeV2(new IpPurityScoreRequest(ip, IpPurityParse.ToScoreIntel(intel), [], null, null));

    private static Built Build(IpPurityScoreRequest request)
    {
        if (string.IsNullOrEmpty(request.ExitIp))
        {
            return new Built
            {
                NoExit = true,
                Advice = ["未能获取出口 IP，请检查网络后重新检测。"]
            };
        }

        var intel = request.Intel;
        var hostingFlag = intel?.Hosting;
        var type = intel?.Type;
        var confidence = intel?.Confidence;
        var services = request.Services ?? [];
        var consistency = request.Consistency;
        var leak = request.Leak;
        var items = new List<Row>();
        var hosting = ResolveHosting(type, hostingFlag);
        var typeKnown = !string.IsNullOrEmpty(type) || HasAnyFlag(intel);

        var networkEvidence = "";
        var network = new Row
        {
            Group = "网络身份",
            Label = "机房 / 托管 IP",
            Gate = true
        };
        if (hosting is null)
        {
            network.Missing = true;
            network.Evidence = intel?.Mobile == true ? "没有机房数据；该地址被标为移动网络" : "";
        }
        else if (hosting == true)
        {
            network.Deduction = HostingPoints(confidence);
            networkEvidence = HostingEvidence(confidence);
        }
        else if (type == IpPurityCodes.Isp)
        {
            networkEvidence = "住宅 / 运营商宽带";
        }
        else
        {
            networkEvidence = "不是机房 IP";
        }
        if (hosting is not null && intel?.Mobile == true) networkEvidence += "，同时是移动网络";
        if (hosting is not null) network.Evidence = networkEvidence;
        items.Add(network);

        if (hosting == false && type == IpPurityCodes.Business)
        {
            items.Add(new Row
            {
                Group = "网络身份",
                Label = "商业宽带",
                Deduction = BusinessPoints,
                Evidence = "商业宽带，风控敏感度高于住宅网络"
            });
        }
        else if (hosting == false && type == IpPurityCodes.Education)
        {
            items.Add(new Row
            {
                Group = "网络身份",
                Label = "教育网",
                Deduction = EducationPoints,
                Evidence = "教育网出口通常由多人共享"
            });
        }

        var present = PrivacyMeta
            .Where(meta => Flag(intel, meta.Key) == true)
            .Select(meta => meta.Key)
            .OrderByDescending(key => PrivacyWeight[key])
            .ToList();
        var tierOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < present.Count; index++)
        {
            tierOf[present[index]] = index == 0 ? 0 : index == 1 ? 1 : 2;
        }

        var privacyRows = PrivacyMeta.Select(meta =>
        {
            var row = new Row
            {
                Group = "隐私标记",
                Label = meta.Label,
                Gate = true,
                HitText = meta.Hit
            };
            var value = Flag(intel, meta.Key);
            if (value is null)
            {
                row.Missing = true;
                row.Evidence = "";
                return row;
            }
            if (value != true)
            {
                row.Evidence = meta.Clear;
                return row;
            }
            var tier = tierOf[meta.Key];
            var factor = tier == 0 ? 1d : tier == 1 ? 0.5d : 0.25d;
            row.Deduction = (int)Math.Round(PrivacyWeight[meta.Key] * factor, MidpointRounding.AwayFromZero);
            if (tier == 1) row.TierText = "，与更强的标记同时出现，按一半计入";
            if (tier == 2) row.TierText = "，与更强的标记同时出现，按较低权重计入";
            return row;
        }).ToList();
        ApplyCap(privacyRows.Where(row => row.Deduction > 0).ToList(), PrivacyCap);
        foreach (var row in privacyRows)
        {
            if (row.Missing || row.Evidence.Length > 0) continue;
            row.Evidence = CapSentence(row, "这一类的扣分已达上限，已降低扣分", "这一类的扣分已达上限，此项不再重复扣");
        }
        var privacyOpen = privacyRows.Any(row => row.Missing || row.Deduction > 0 || row.Absorbed);
        if (!privacyOpen)
        {
            items.Add(new Row
            {
                Group = "隐私标记",
                Label = "VPN / 代理 / 中继 / Tor",
                Evidence = "没有 VPN、代理、中继或 Tor 标记"
            });
        }
        else
        {
            items.AddRange(privacyRows);
        }

        var serviceRows = services.Select(service =>
        {
            var row = new Row
            {
                Group = "AI 服务",
                Label = string.IsNullOrEmpty(service.Name) ? "AI 服务" : service.Name,
                Gate = true
            };
            var status = service.Status;
            var listed = service.RegionListed;
            if (string.IsNullOrEmpty(status))
            {
                row.Missing = true;
                row.Evidence = "这项检测没有返回结果";
                return row;
            }
            if (status == IpPurityCodes.Blocked)
            {
                row.Deduction = 16;
                row.HitText = listed == false ? "无法连接，所在地区也不在支持列表内" : "无法连接";
            }
            else if (status == IpPurityCodes.Region)
            {
                row.Deduction = 16;
                row.HitText = "接口返回了地区限制";
            }
            else if (listed == false)
            {
                row.Deduction = 8;
                row.HitText = "接口可以访问，但所在地区不在官方支持列表内";
            }
            else if (status == IpPurityCodes.Risk)
            {
                row.Evidence = "可以连接。IP 风险已在其他项目计入，这里不重复扣分";
            }
            else if (listed is null)
            {
                row.Evidence = "可以连接，所在地区无法判定";
            }
            else
            {
                row.Evidence = "可以连接";
            }
            return row;
        }).ToList();
        ApplyCap(serviceRows.Where(row => row.Deduction > 0).ToList(), ServiceCap);
        foreach (var row in serviceRows)
        {
            if (row.Missing || row.Evidence.Length > 0) continue;
            row.Evidence = CapSentence(row, "AI 服务扣分已达上限，已降低扣分", "AI 服务扣分已达上限，此项不再重复扣");
        }
        items.AddRange(serviceRows);

        var exit = new Row { Group = "出口与泄漏", Label = "出口 IP 一致性" };
        var mismatch = consistency?.Mismatch;
        if (mismatch is not true and not false)
        {
            exit.Missing = true;
            exit.Evidence = "能比对的出口地址不够，无法判断是否一致";
        }
        else if (mismatch == false)
        {
            exit.Evidence = "各来源出口一致";
        }
        else
        {
            var distinctV4 = consistency?.DistinctV4 ?? 0;
            var distinctV6 = consistency?.DistinctV6 ?? 0;
            var families = (distinctV4 >= 2 ? 1 : 0) + (distinctV6 >= 2 ? 1 : 0);
            var extra = Math.Max(0, distinctV4 - 2) + Math.Max(0, distinctV6 - 2);
            var raw = (families >= 2 ? ExitBoth : ExitOne) + extra * ExitExtra;
            exit.Deduction = Math.Min(ExitCap, raw);
            var bits = new List<string>();
            if (distinctV4 >= 2) bits.Add(distinctV4 + " 个不同的 IPv4");
            if (distinctV6 >= 2) bits.Add(distinctV6 + " 个不同的 IPv6");
            exit.Evidence = bits.Count > 0 ? "检测到" + string.Join('、', bits) : "不同来源的出口不一致";
            if (raw > ExitCap) exit.Evidence += "。这项最多扣 " + ExitCap + " 分";
        }
        items.Add(exit);

        var state = leak?.State;
        var leakedCount = leak?.Leaked?.Count ?? 0;
        var leakRow = new Row { Group = "出口与泄漏", Label = "WebRTC 泄漏" };
        if (state == IpPurityCodes.Leak)
        {
            leakRow.Deduction = leakedCount >= 2 ? LeakMany : LeakOne;
            leakRow.Evidence = leakedCount > 0
                ? "暴露了 " + leakedCount + " 个与出口不同的公网地址"
                : "暴露了与出口不同的公网地址";
        }
        else if (state == IpPurityCodes.Clean)
        {
            leakRow.Evidence = "未发现泄漏";
        }
        else if (state == IpPurityCodes.Unsupported)
        {
            leakRow.Missing = true;
            leakRow.Evidence = "浏览器不支持 WebRTC";
        }
        else if (leak?.Settled == false)
        {
            leakRow.Missing = true;
            leakRow.Evidence = "候选地址尚未收集完，不能判断是否泄漏";
        }
        else if (leak?.ExitKnown == false)
        {
            leakRow.Missing = true;
            leakRow.Evidence = "没有出口 IP，无法判断是否泄漏";
        }
        else
        {
            leakRow.Missing = true;
            leakRow.Evidence = "没有足够的候选地址，不能判断是否泄漏";
        }
        items.Add(leakRow);

        return new Built
        {
            Items = items,
            TypeKnown = typeKnown,
            Hosting = hosting,
            Confidence = confidence,
            Type = type,
            Intel = intel,
            Services = services,
            Consistency = consistency,
            Exit = exit,
            Leak = leakRow
        };
    }

    private static IpPurityScore Finalize(Built built, bool scoredOnly)
    {
        if (built.NoExit)
        {
            return new IpPurityScore(null, null, [], [], built.Advice, "", "");
        }

        var scored = scoredOnly
            ? built.Items.Where(item => item.Group is "网络身份" or "隐私标记").ToList()
            : built.Items;
        var status = scoredOnly
            ? built.Items.Where(item => item.Group is not "网络身份" and not "隐私标记").ToList()
            : [];
        var advice = new List<string>();
        string note;
        if (!built.TypeKnown)
        {
            var named = built.Intel?.Source;
            advice.Add(named is not null
                ? "已从「" + named + "」拿到资料，但没有判定出 IP 类型，无法计算纯净度评分。"
                : "未能获取 IP 类型信息（所有 IP 情报来源均不可用），无法计算纯净度评分。");
            note = "";
            return Pack(null, null, scored, status, advice, note, scoredOnly);
        }

        var spent = scored.Sum(item => item.Deduction);
        var total = Math.Max(0, 100 - spent);
        var missing = scored.Where(item => item.Missing && item.Gate).ToList();
        if (missing.Count > 0 && total >= GradeGood)
        {
            advice.Add(string.Join('、', missing.Select(item => item.Label)) + "没有数据。这些项按未命中计分会得到纯净结果，因此本次不评分。");
            return Pack(null, null, scored, status, advice, "", scoredOnly);
        }

        var privacySpent = scoredOnly
            ? scored.Where(item => item.Group == "隐私标记").Sum(item => item.Deduction)
            : built.Items.Where(item => item.Group == "隐私标记").Sum(item => item.Deduction);
        var serviceSpent = built.Items.Where(item => item.Group == "AI 服务").Sum(item => item.Deduction);
        var exitSpent = built.Exit.Deduction;
        var leakSpent = built.Leak.Deduction;
        var hitTor = built.Intel?.Tor == true;
        var networkDeduction = built.Items.First(item => item.Label == "机房 / 托管 IP").Deduction;
        var fullSpent = built.Items.Sum(item => item.Deduction);
        var leakHit = built.Leak.Deduction > 0;

        string text;
        if (scoredOnly)
        {
            text = V2Text(total, built, privacySpent, leakHit);
        }
        else if (total >= GradeGood)
        {
            text = fullSpent > 0 ? "整体较干净，明细里有轻微扣分，注册前可以再看一眼。" : "IP 类型干净，适合注册和日常使用。";
        }
        else if (hitTor && built.Hosting == true)
        {
            text = "该 IP 是 Tor 出口，而且属于机房地址，AI 服务通常会直接拒绝。";
        }
        else if (hitTor)
        {
            text = "该 IP 属于 Tor 出口，AI 服务通常会直接拒绝。";
        }
        else if (built.Hosting == true && built.Confidence != IpPurityCodes.Low && fullSpent > networkDeduction)
        {
            text = "机房 IP 还有其他风险信号，不建议用于注册或登录账号。";
        }
        else if (built.Hosting == true && built.Confidence != IpPurityCodes.Low)
        {
            text = "机房 IP 可以打开网页，但不适合注册或长期登录账号。";
        }
        else if (privacySpent > 0)
        {
            text = "该 IP 带有 VPN、代理或中继标记，注册时更容易遇到风控。";
        }
        else if (serviceSpent > 0)
        {
            text = "当前网络访问 AI 服务受限，换到可用地区的节点后分数会回来。";
        }
        else if (leakSpent > 0)
        {
            text = "WebRTC 暴露了与出口不同的公网地址，真实 IP 可能绕过代理。";
        }
        else if (exitSpent > 0)
        {
            text = "不同来源看到的出口 IP 不一致，部分流量可能没有走代理。";
        }
        else if (built.Type == IpPurityCodes.Education)
        {
            text = "教育网出口多人共享，注册账号比家庭宽带更容易触发风控。";
        }
        else if (built.Confidence == IpPurityCodes.Low && built.Hosting == true)
        {
            text = missing.Count > 0
                ? "机房结果只来自组织名推断，而且还有风险标记没有数据，这个分数不能当成纯净。"
                : "机房结果只来自 ASN 或组织名推断，有可能误判。";
        }
        else if (total >= GradeWarn)
        {
            text = "可日常访问，但用于注册或订阅账号有一定风控风险。";
        }
        else
        {
            text = "风险信号明显，容易触发人机验证或封号，不建议用于账号操作。";
        }

        var grade = total >= GradeGood
            ? new IpPurityGrade("good", "纯净", text)
            : total >= GradeWarn
                ? new IpPurityGrade("warn", "一般", text)
                : new IpPurityGrade("bad", "高风险", text);

        advice.AddRange(BuildAdvice(built, exitSpent, leakSpent, missing, scoredOnly));
        note = scoredOnly
            ? "分数只评价出口 IP 本身（网络身份与隐私标记）。AI 可用性、出口一致性和 UDP 泄漏单独显示，不计入分数。"
            : "从 100 分扣起，综合网络身份、隐私标记、AI 服务可用性、出口一致性与 WebRTC 泄漏。";
        if (built.Confidence == IpPurityCodes.Low) note += "机房判断来自 ASN 或组织名，仅供参考。";
        return Pack(total, grade, scored, status, advice, note, scoredOnly);
    }

    private static string V2Text(int total, Built built, int privacySpent, bool leakHit)
    {
        var services = built.Services;
        var allBlocked = services.Count > 0 && services.All(service => service.Status == IpPurityCodes.Blocked);
        if (total >= GradeGood)
        {
            string sentence;
            if (allBlocked && built.Hosting == false && privacySpent == 0 && built.Type is null or IpPurityCodes.Isp)
            {
                sentence = "IP 是干净的住宅宽带。连不上 AI 服务是地区限制，不是这个 IP 的问题。";
            }
            else if (built.Items.Where(item => item.Group is "网络身份" or "隐私标记").Sum(item => item.Deduction) == 0)
            {
                sentence = "IP 类型干净，适合注册和日常使用。";
            }
            else
            {
                sentence = "整体较干净，明细里有轻微扣分，注册前可以再看一眼。";
            }
            return WithLeak(sentence, leakHit);
        }
        if (built.Intel?.Tor == true && built.Hosting == true)
        {
            return WithLeak("该 IP 是 Tor 出口，而且属于机房地址，AI 服务通常会直接拒绝。", leakHit);
        }
        if (built.Intel?.Tor == true)
        {
            return WithLeak("该 IP 属于 Tor 出口，AI 服务通常会直接拒绝。", leakHit);
        }
        if (built.Hosting == true && built.Confidence != IpPurityCodes.Low && privacySpent > 0)
        {
            var sentence = built.Intel?.Vpn == true && built.Intel.Proxy != true
                ? "机房 + VPN 出口，不建议用于注册或长期登录账号。"
                : built.Intel?.Proxy == true && built.Intel.Vpn != true
                    ? "机房 IP 且带代理标记，不建议用于注册或长期登录账号。"
                    : "机房 IP 还有其他风险信号，不建议用于注册或长期登录账号。";
            return WithLeak(sentence, leakHit);
        }
        if (built.Hosting == true && built.Confidence != IpPurityCodes.Low)
        {
            return WithLeak("机房 IP 可以打开网页，但不适合注册或长期登录账号。", leakHit);
        }
        if (privacySpent > 0)
        {
            return WithLeak("该 IP 带有 VPN、代理或中继标记，注册时更容易遇到风控。", leakHit);
        }
        if (built.Type == IpPurityCodes.Education)
        {
            return WithLeak("教育网出口多人共享，注册账号比家庭宽带更容易触发风控。", leakHit);
        }
        if (built.Confidence == IpPurityCodes.Low && built.Hosting == true)
        {
            return WithLeak("机房结果只来自 ASN 或组织名推断，有可能误判。", leakHit);
        }
        if (total >= GradeWarn)
        {
            return WithLeak("可日常访问，但用于注册或订阅账号有一定风控风险。", leakHit);
        }
        return WithLeak("风险信号明显，容易触发人机验证或封号，不建议用于账号操作。", leakHit);
    }

    private static string WithLeak(string sentence, bool leak)
    {
        if (!leak) return sentence;
        if (sentence.EndsWith('。')) sentence = sentence[..^1];
        return sentence + "；另外 UDP 流量绕过了代理。";
    }

    private static List<string> BuildAdvice(Built built, int exitSpent, int leakSpent, List<Row> missing, bool scoredOnly)
    {
        var advice = new List<string>();
        var flags = built.Intel;
        if (flags?.Tor == true) advice.Add("该 IP 属于 Tor 网络，绝大多数 AI 服务会直接拒绝，请更换网络。");
        if (built.Hosting == true && built.Confidence == IpPurityCodes.Low)
        {
            advice.Add("机房一项只来自 ASN 或组织名匹配。如果确认是家用宽带，这项可能是误判。");
        }
        else if (built.Hosting == true)
        {
            advice.Add("当前是机房 IP：可以正常浏览，但不建议用于注册、订阅或长期登录 ChatGPT / Claude。");
        }
        var anon = new List<string>();
        if (flags?.Vpn == true) anon.Add("VPN");
        if (flags?.Proxy == true) anon.Add("代理");
        if (flags?.Relay == true) anon.Add("中继");
        if (anon.Count > 1)
        {
            advice.Add("该 IP 被标记为 " + string.Join('、', anon) + "。多项同时出现时会递减扣分，重要账号建议改用住宅宽带。");
        }
        else if (anon.Count == 1)
        {
            advice.Add("该 IP 被标记为 " + anon[0] + "，重要账号建议改用住宅宽带。");
        }
        if (built.Hosting == false && built.Type == IpPurityCodes.Education) advice.Add("如果要注册或订阅，尽量换到家庭宽带再操作。");
        if (built.Hosting == false && built.Type == IpPurityCodes.Business) advice.Add("重要账号更适合用家庭宽带，商业宽带偶尔会提高风控敏感度。");

        var services = built.Services;
        var blocked = services.Where(service => service.Status == IpPurityCodes.Blocked).ToList();
        var region = services.Where(service => service.Status == IpPurityCodes.Region).ToList();
        var unlisted = services.Where(service =>
            !string.IsNullOrEmpty(service.Status)
            && service.Status != IpPurityCodes.Blocked
            && service.Status != IpPurityCodes.Region
            && service.RegionListed == false).ToList();
        if (blocked.Count > 0) advice.Add(Names(blocked) + " 无法连接。请检查代理是否覆盖对应域名，或稍后重新检测。");
        if (region.Count > 0) advice.Add(Names(region) + " 在当前网络下地区受限，请换到受支持地区的节点后再测。");
        if (unlisted.Count > 0)
        {
            advice.Add(Names(unlisted) + "的接口有响应，但 Cloudflare 判定的地区不在官方支持列表内，使用时仍可能被限制。");
        }
        var regionUnknown = services.Where(service =>
            !string.IsNullOrEmpty(service.Status)
            && service.Status != IpPurityCodes.Blocked
            && service.Status != IpPurityCodes.Region
            && service.RegionListed is null).ToList();
        if (regionUnknown.Count > 0)
        {
            advice.Add(Names(regionUnknown) + " 可以连接，但没有判定出所在地区，不能据此确认该地区受支持。");
        }
        if (exitSpent > 0)
        {
            var distinctV4 = built.Consistency?.DistinctV4 ?? 0;
            var distinctV6 = built.Consistency?.DistinctV6 ?? 0;
            if (distinctV4 >= 2 && distinctV6 >= 2) advice.Add("IPv4 和 IPv6 的出口都不一致，不同协议走了不同的网络。");
            else if (distinctV6 >= 2) advice.Add("IPv6 出口不一致，支持 IPv6 的网站可能绕过当前代理。");
            else advice.Add("不同来源的 IPv4 出口不一致，部分请求可能没有经过代理。");
        }
        if (leakSpent > 0)
        {
            advice.Add(scoredOnly
                ? "UDP 出口与代理出口不同：在代理软件中开启 TUN 模式或 UDP 转发，否则 UDP 会暴露真实地址。"
                : "WebRTC 暴露了真实地址。可以在浏览器中禁用 WebRTC，或在代理软件中开启 UDP 转发。");
        }
        var exposureGap = built.Items.Any(item => item.Group == "出口与泄漏" && item.Missing);
        if (advice.Count == 0)
        {
            advice.Add(exposureGap
                ? scoredOnly
                    ? "已确认的项目里没有明显风险。出口一致性或 UDP 有缺项，不能据此断定完全没有泄漏。"
                    : "已确认的项目里没有明显风险。出口一致性或 WebRTC 有缺项，不能据此断定完全没有泄漏。"
                : "未发现明显的风险信号，当前 IP 适合访问 AI 服务。");
        }
        if (missing.Count > 0)
        {
            advice.Add(string.Join('、', missing.Select(item => item.Label)) + "没有数据，未知项没有按安全计分。");
        }
        return advice;
    }

    private static IpPurityScore Pack(
        int? total, IpPurityGrade? grade, List<Row> scored, List<Row> status, List<string> advice, string note, bool scoredOnly)
    {
        var footnote = total is null && advice.Count == 1 && advice[0].StartsWith("未能获取出口 IP", StringComparison.Ordinal)
            ? ""
            : scoredOnly
                ? "隐私标记同时命中时会递减，合计最多扣 50 分。机房扣分随情报准确度下降。AI 可用性、出口一致性与 UDP 泄漏不计入分数。"
                : Footnote;
        if (total is null && note.Length == 0 && grade is null && advice.Count > 0 && advice[0].Contains("无法计算纯净度评分", StringComparison.Ordinal))
        {
            footnote = scoredOnly
                ? "隐私标记同时命中时会递减，合计最多扣 50 分。机房扣分随情报准确度下降。AI 可用性、出口一致性与 UDP 泄漏不计入分数。"
                : Footnote;
        }
        return new IpPurityScore(
            total,
            grade,
            scored.Select(row => row.Freeze()).ToList(),
            status.Select(row => row.Freeze()).ToList(),
            advice,
            note,
            footnote);
    }

    private static string Names(IReadOnlyList<IpPurityScoreService> services) =>
        string.Join('、', services.Select(service => service.Name).Where(name => !string.IsNullOrEmpty(name)));

    private static bool? ResolveHosting(string? type, bool? hostingFlag)
    {
        if (hostingFlag is true or false) return hostingFlag;
        if (type == IpPurityCodes.Hosting) return true;
        if (type is IpPurityCodes.Isp or IpPurityCodes.Business or IpPurityCodes.Education) return false;
        return null;
    }

    private static int HostingPoints(string? confidence) => confidence switch
    {
        IpPurityCodes.High => 35,
        IpPurityCodes.Medium => 28,
        IpPurityCodes.Low => 18,
        _ => 28
    };

    private static string HostingEvidence(string? confidence) => confidence switch
    {
        IpPurityCodes.High => "情报源确认该 IP 属于云服务或托管机房",
        IpPurityCodes.Medium => "IP 数据库将其标为机房，准确度中等，扣分低于高准确度来源",
        IpPurityCodes.Low => "只根据 ASN 或组织名推断为机房，扣分低于已确认的机房 IP",
        _ => "被标为机房 IP，但情报来源没有给出准确度"
    };

    private static bool HasAnyFlag(IpPurityScoreIntel? intel) =>
        intel is not null && (intel.Hosting is not null || intel.Vpn is not null || intel.Proxy is not null
            || intel.Tor is not null || intel.Relay is not null || intel.Mobile is not null);

    private static bool? Flag(IpPurityScoreIntel? intel, string key) => key switch
    {
        "tor" => intel?.Tor,
        "vpn" => intel?.Vpn,
        "proxy" => intel?.Proxy,
        "relay" => intel?.Relay,
        _ => null
    };

    private static void ApplyCap(List<Row> parts, int cap)
    {
        var sum = parts.Sum(part => part.Deduction);
        for (var guard = 0; sum > cap && guard < 10000; guard++)
        {
            var best = int.MaxValue;
            foreach (var part in parts)
            {
                if (part.Deduction > 0 && part.Deduction < best) best = part.Deduction;
            }
            if (best == int.MaxValue) break;
            var moved = false;
            for (var i = parts.Count - 1; i >= 0 && sum > cap; i--)
            {
                if (parts[i].Deduction != best) continue;
                parts[i].Deduction -= 1;
                parts[i].Capped = true;
                if (parts[i].Deduction == 0) parts[i].Absorbed = true;
                sum -= 1;
                moved = true;
            }
            if (!moved) break;
        }
    }

    private static string CapSentence(Row item, string cappedText, string absorbedText)
    {
        if (item.Absorbed) return item.HitText + "。" + absorbedText;
        if (item.Capped) return item.HitText + item.TierText + "。" + cappedText;
        return item.HitText + item.TierText;
    }

    private sealed class Built
    {
        public bool NoExit;
        public List<Row> Items = [];
        public List<string> Advice = [];
        public bool TypeKnown;
        public bool? Hosting;
        public string? Confidence;
        public string? Type;
        public IpPurityScoreIntel? Intel;
        public IReadOnlyList<IpPurityScoreService> Services = [];
        public IpPurityScoreConsistency? Consistency;
        public Row Exit = new();
        public Row Leak = new();
    }

    private sealed class Row
    {
        public string Group = "";
        public string Label = "";
        public int Deduction;
        public bool Gate;
        public bool Missing;
        public string Evidence = "";
        public bool Capped;
        public bool Absorbed;
        public string HitText = "";
        public string TierText = "";

        public IpPurityScoreItem Freeze() =>
            new(Group, Label, Deduction, Gate, Missing, Evidence, Capped, Absorbed);
    }
}
