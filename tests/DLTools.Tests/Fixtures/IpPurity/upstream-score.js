(function (IPQ) {
  'use strict';

  // 从 100 分扣起，明细分成四组。隐私标记同时命中时递减，避免同一类匿名特征被多次重罚。
  // 机房扣分随情报准确度下降。拿不到类型，或关键项缺失且乐观结果仍会落到「纯净」时，返回 total: null。
  var HOSTING = { high: 35, medium: 28, low: 18, unknown: 28 };
  var BUSINESS = 10;
  var EDUCATION = 16;
  var PRIVACY_WEIGHT = { tor: 45, vpn: 24, proxy: 22, relay: 18 };
  var PRIVACY_CAP = 50;
  var SERVICE_POINTS = { blocked: 16, region: 16, unlisted: 8 };
  var SERVICE_CAP = 32;
  var EXIT_ONE = 16;
  var EXIT_BOTH = 20;
  var EXIT_EXTRA = 2;
  var EXIT_CAP = 24;
  var LEAK_ONE = 16;
  var LEAK_MANY = 20;
  var GRADE_GOOD = 85;
  var GRADE_WARN = 60;
  var FOOTNOTE = '隐私标记同时命中时会递减，合计最多扣 50 分；AI 服务合计最多扣 32 分。机房扣分随情报准确度下降，只靠组织名匹配不会按已确认机房的额度扣满。';

  var PRIVACY_META = [
    { key: 'tor', label: 'Tor 标记', clear: '无 Tor 标记', hit: '属于 Tor 出口节点' },
    { key: 'vpn', label: 'VPN 标记', clear: '无 VPN 标记', hit: '已被标记为 VPN' },
    { key: 'proxy', label: '代理标记', clear: '无代理标记', hit: '已被标记为代理' },
    { key: 'relay', label: '中继标记', clear: '无中继标记', hit: '已被标记为中继' }
  ];

  function sumOf(list) {
    return list.reduce(function (sum, item) { return sum + (item.deduction || 0); }, 0);
  }

  function groupSum(items, group) {
    return sumOf(items.filter(function (item) { return item.group === group; }));
  }

  // 从当前最小的扣分项减起；并列的项同一轮一起减，避免把相等的服务扣成 0 和满分。
  function applyCap(parts, cap) {
    var sum = sumOf(parts);
    var guard = 0;
    while (sum > cap && guard < 10000) {
      guard++;
      var best = Infinity;
      parts.forEach(function (part) {
        if (part.deduction > 0 && part.deduction < best) best = part.deduction;
      });
      if (best === Infinity) break;
      var moved = false;
      for (var i = parts.length - 1; i >= 0 && sum > cap; i--) {
        if (parts[i].deduction !== best) continue;
        parts[i].deduction -= 1;
        parts[i].capped = true;
        if (parts[i].deduction === 0) parts[i].absorbed = true;
        sum -= 1;
        moved = true;
      }
      if (!moved) break;
    }
  }

  function capSentence(item, cappedText, absorbedText) {
    if (item.absorbed) return item.hitText + '。' + absorbedText;
    if (item.capped) return item.hitText + (item.tierText || '') + '。' + cappedText;
    return item.hitText + (item.tierText || '');
  }

  function resolveHosting(type, flags) {
    if (flags.hosting === true || flags.hosting === false) return flags.hosting;
    if (type === 'hosting') return true;
    if (type === 'isp' || type === 'business' || type === 'education') return false;
    return null;
  }

  function hostingPoints(confidence) {
    if (confidence === 'high') return HOSTING.high;
    if (confidence === 'medium') return HOSTING.medium;
    if (confidence === 'low') return HOSTING.low;
    return HOSTING.unknown;
  }

  function hostingEvidence(confidence) {
    if (confidence === 'high') return '情报源确认该 IP 属于云服务或托管机房';
    if (confidence === 'medium') return 'IP 数据库将其标为机房，准确度中等，扣分低于高准确度来源';
    if (confidence === 'low') return '只根据 ASN 或组织名推断为机房，扣分低于已确认的机房 IP';
    return '被标为机房 IP，但情报来源没有给出准确度';
  }

  function blank(total, advice) {
    return { total: total, grade: null, items: [], advice: advice, note: '', footnote: '', confidence: null };
  }

  IPQ.SCORE = {
    compute: function (r) {
      r = r || {};
      if (!r.exitIp) return blank(null, ['未能获取出口 IP，请检查网络后重新检测。']);

      var intel = r.intel || null;
      var flags = (intel && intel.flags) || {};
      var type = (intel && intel.type) || null;
      var confidence = (intel && intel.confidence) || null;
      var services = Array.isArray(r.services) ? r.services : [];
      var consistency = r.consistency || {};
      var webrtc = r.webrtc || {};
      var items = [];
      var hosting = resolveHosting(type, flags);
      var typeKnown = !!(type || Object.keys(flags).some(function (key) { return flags[key] != null; }));

      var networkEvidence = '';
      var network = {
        group: '网络身份',
        label: '机房 / 托管 IP',
        deduction: 0,
        gate: true,
        evidence: ''
      };
      if (hosting === null) {
        network.missing = true;
        network.evidence = flags.mobile === true ? '没有机房数据；该地址被标为移动网络' : '';
      } else if (hosting) {
        network.deduction = hostingPoints(confidence);
        networkEvidence = hostingEvidence(confidence);
      } else if (type === 'isp') {
        networkEvidence = '住宅 / 运营商宽带';
      } else {
        networkEvidence = '不是机房 IP';
      }
      if (hosting !== null && flags.mobile === true) networkEvidence += '，同时是移动网络';
      if (hosting !== null) network.evidence = networkEvidence;
      items.push(network);

      if (hosting === false && type === 'business') {
        items.push({
          group: '网络身份',
          label: '商业宽带',
          deduction: BUSINESS,
          evidence: '商业宽带，风控敏感度高于住宅网络'
        });
      } else if (hosting === false && type === 'education') {
        items.push({
          group: '网络身份',
          label: '教育网',
          deduction: EDUCATION,
          evidence: '教育网出口通常由多人共享'
        });
      }

      var tierOf = {};
      var present = PRIVACY_META.filter(function (meta) { return flags[meta.key] === true; })
        .map(function (meta) { return meta.key; });
      present.sort(function (a, b) { return PRIVACY_WEIGHT[b] - PRIVACY_WEIGHT[a]; });
      present.forEach(function (key, index) {
        tierOf[key] = index === 0 ? 0 : index === 1 ? 1 : 2;
      });

      var privacyRows = PRIVACY_META.map(function (meta) {
        var row = {
          group: '隐私标记',
          label: meta.label,
          deduction: 0,
          gate: true,
          hitText: meta.hit,
          tierText: ''
        };
        if (flags[meta.key] == null) {
          row.missing = true;
          row.evidence = '';
          return row;
        }
        if (flags[meta.key] !== true) {
          row.evidence = meta.clear;
          return row;
        }
        var tier = tierOf[meta.key];
        var factor = tier === 0 ? 1 : tier === 1 ? 0.5 : 0.25;
        row.deduction = Math.round(PRIVACY_WEIGHT[meta.key] * factor);
        if (tier === 1) row.tierText = '，与更强的标记同时出现，按一半计入';
        if (tier === 2) row.tierText = '，与更强的标记同时出现，按较低权重计入';
        return row;
      });
      applyCap(privacyRows.filter(function (row) { return row.deduction > 0; }), PRIVACY_CAP);
      privacyRows.forEach(function (row) {
        if (row.missing || row.evidence) return;
        row.evidence = capSentence(row, '这一类的扣分已达上限，已降低扣分', '这一类的扣分已达上限，此项不再重复扣');
      });
      var privacyOpen = privacyRows.some(function (row) {
        return row.missing || row.deduction > 0 || row.absorbed;
      });
      if (!privacyOpen) {
        items.push({
          group: '隐私标记',
          label: 'VPN / 代理 / 中继 / Tor',
          deduction: 0,
          evidence: '没有 VPN、代理、中继或 Tor 标记'
        });
      } else {
        privacyRows.forEach(function (row) { items.push(row); });
      }

      var serviceRows = services.map(function (svc) {
        var name = (svc && svc.name) || 'AI 服务';
        var status = svc && svc.status;
        var listed = svc && typeof svc.regionListed === 'boolean' ? svc.regionListed : null;
        var row = { group: 'AI 服务', label: name, deduction: 0, gate: true, hitText: '', tierText: '' };
        if (!status) {
          row.missing = true;
          row.evidence = '这项检测没有返回结果';
          return row;
        }
        if (status === 'blocked') {
          row.deduction = SERVICE_POINTS.blocked;
          row.hitText = listed === false ? '无法连接，所在地区也不在支持列表内' : '无法连接';
        } else if (status === 'region') {
          row.deduction = SERVICE_POINTS.region;
          row.hitText = '接口返回了地区限制';
        } else if (listed === false) {
          row.deduction = SERVICE_POINTS.unlisted;
          row.hitText = '接口可以访问，但所在地区不在官方支持列表内';
        } else if (status === 'risk') {
          row.evidence = '可以连接。IP 风险已在其他项目计入，这里不重复扣分';
        } else if (listed === null) {
          row.evidence = '可以连接，所在地区无法判定';
        } else {
          row.evidence = '可以连接';
        }
        return row;
      });
      applyCap(serviceRows.filter(function (row) { return row.deduction > 0; }), SERVICE_CAP);
      serviceRows.forEach(function (row) {
        if (row.missing || row.evidence) return;
        row.evidence = capSentence(row, 'AI 服务扣分已达上限，已降低扣分', 'AI 服务扣分已达上限，此项不再重复扣');
      });
      serviceRows.forEach(function (row) { items.push(row); });

      var exit = {
        group: '出口与泄漏',
        label: '出口 IP 一致性',
        deduction: 0,
        evidence: ''
      };
      if (consistency.mismatch !== true && consistency.mismatch !== false) {
        exit.missing = true;
        exit.evidence = '能比对的出口地址不够，无法判断是否一致';
      } else if (!consistency.mismatch) {
        exit.evidence = '各来源出口一致';
      } else {
        var families = (consistency.distinctV4 >= 2 ? 1 : 0) + (consistency.distinctV6 >= 2 ? 1 : 0);
        var extra = Math.max(0, (consistency.distinctV4 || 0) - 2) + Math.max(0, (consistency.distinctV6 || 0) - 2);
        var rawExit = (families >= 2 ? EXIT_BOTH : EXIT_ONE) + extra * EXIT_EXTRA;
        exit.deduction = Math.min(EXIT_CAP, rawExit);
        var bits = [];
        if (consistency.distinctV4 >= 2) bits.push(consistency.distinctV4 + ' 个不同的 IPv4');
        if (consistency.distinctV6 >= 2) bits.push(consistency.distinctV6 + ' 个不同的 IPv6');
        exit.evidence = (bits.length ? '检测到' + bits.join('、') : '不同来源的出口不一致');
        if (rawExit > EXIT_CAP) exit.evidence += '。这项最多扣 ' + EXIT_CAP + ' 分';
      }
      items.push(exit);

      var state = webrtc.state || null;
      var leakedCount = webrtc.leaked && webrtc.leaked.length ? webrtc.leaked.length : 0;
      var leak = { group: '出口与泄漏', label: 'WebRTC 泄漏', deduction: 0, evidence: '' };
      if (state === 'leak') {
        leak.deduction = leakedCount >= 2 ? LEAK_MANY : LEAK_ONE;
        leak.evidence = leakedCount > 0
          ? '暴露了 ' + leakedCount + ' 个与出口不同的公网地址'
          : '暴露了与出口不同的公网地址';
      } else if (state === 'clean') {
        leak.evidence = '未发现泄漏';
      } else if (state === 'unsupported') {
        leak.missing = true;
        leak.evidence = '浏览器不支持 WebRTC';
      } else if (webrtc.settled === false) {
        leak.missing = true;
        leak.evidence = '候选地址尚未收集完，不能判断是否泄漏';
      } else if (webrtc.exitKnown === false) {
        leak.missing = true;
        leak.evidence = '没有出口 IP，无法判断是否泄漏';
      } else {
        leak.missing = true;
        leak.evidence = '没有足够的候选地址，不能判断是否泄漏';
      }
      items.push(leak);

      function pack(total, grade, advice, note) {
        return {
          total: total,
          grade: grade,
          items: items,
          advice: advice,
          note: note || '',
          footnote: FOOTNOTE,
          confidence: confidence
        };
      }

      if (!typeKnown) {
        var named = intel && intel.source;
        return pack(null, null, [named
          ? '已从「' + named + '」拿到资料，但没有判定出 IP 类型，无法计算纯净度评分。'
          : '未能获取 IP 类型信息（所有 IP 情报来源均不可用），无法计算纯净度评分。'], '');
      }

      var spent = sumOf(items);
      var total = Math.max(0, 100 - spent);
      var missingGate = items.filter(function (item) { return item.missing && item.gate; });
      if (missingGate.length && total >= GRADE_GOOD) {
        return pack(null, null, [
          missingGate.map(function (item) { return item.label; }).join('、') + '没有数据。这些项按未命中计分会得到纯净结果，因此本次不评分。'
        ], '');
      }

      var networkSpent = groupSum(items, '网络身份');
      var privacySpent = groupSum(items, '隐私标记');
      var serviceSpent = groupSum(items, 'AI 服务');
      var exitSpent = exit.deduction || 0;
      var leakSpent = leak.deduction || 0;
      var hitTor = flags.tor === true;

      var text;
      if (total >= GRADE_GOOD) {
        text = spent > 0
          ? '整体较干净，明细里有轻微扣分，注册前可以再看一眼。'
          : 'IP 类型干净，适合注册和日常使用。';
      } else if (hitTor && hosting === true) {
        text = '该 IP 是 Tor 出口，而且属于机房地址，AI 服务通常会直接拒绝。';
      } else if (hitTor) {
        text = '该 IP 属于 Tor 出口，AI 服务通常会直接拒绝。';
      } else if (hosting === true && confidence !== 'low' && spent > network.deduction) {
        text = '机房 IP 还有其他风险信号，不建议用于注册或登录账号。';
      } else if (hosting === true && confidence !== 'low') {
        text = '机房 IP 可以打开网页，但不适合注册或长期登录账号。';
      } else if (privacySpent > 0) {
        text = '该 IP 带有 VPN、代理或中继标记，注册时更容易遇到风控。';
      } else if (serviceSpent > 0) {
        text = '当前网络访问 AI 服务受限，换到可用地区的节点后分数会回来。';
      } else if (leakSpent > 0) {
        text = 'WebRTC 暴露了与出口不同的公网地址，真实 IP 可能绕过代理。';
      } else if (exitSpent > 0) {
        text = '不同来源看到的出口 IP 不一致，部分流量可能没有走代理。';
      } else if (type === 'education') {
        text = '教育网出口多人共享，注册账号比家庭宽带更容易触发风控。';
      } else if (confidence === 'low' && hosting === true) {
        text = missingGate.length
          ? '机房结果只来自组织名推断，而且还有风险标记没有数据，这个分数不能当成纯净。'
          : '机房结果只来自 ASN 或组织名推断，有可能误判。';
      } else if (total >= GRADE_WARN) {
        text = '可日常访问，但用于注册或订阅账号有一定风控风险。';
      } else {
        text = '风险信号明显，容易触发人机验证或封号，不建议用于账号操作。';
      }

      var grade = total >= GRADE_GOOD
        ? { key: 'good', label: '纯净', text: text }
        : total >= GRADE_WARN
          ? { key: 'warn', label: '一般', text: text }
          : { key: 'bad', label: '高风险', text: text };

      var advice = [];
      if (hitTor) advice.push('该 IP 属于 Tor 网络，绝大多数 AI 服务会直接拒绝，请更换网络。');
      if (hosting === true && confidence === 'low') {
        advice.push('机房一项只来自 ASN 或组织名匹配。如果确认是家用宽带，这项可能是误判。');
      } else if (hosting === true) {
        advice.push('当前是机房 IP：可以正常浏览，但不建议用于注册、订阅或长期登录 ChatGPT / Claude。');
      }
      var anon = [];
      if (flags.vpn === true) anon.push('VPN');
      if (flags.proxy === true) anon.push('代理');
      if (flags.relay === true) anon.push('中继');
      if (anon.length > 1) {
        advice.push('该 IP 被标记为 ' + anon.join('、') + '。多项同时出现时会递减扣分，重要账号建议改用住宅宽带。');
      } else if (anon.length === 1) {
        advice.push('该 IP 被标记为 ' + anon[0] + '，重要账号建议改用住宅宽带。');
      }
      if (hosting === false && type === 'education') advice.push('如果要注册或订阅，尽量换到家庭宽带再操作。');
      if (hosting === false && type === 'business') advice.push('重要账号更适合用家庭宽带，商业宽带偶尔会提高风控敏感度。');

      var blocked = services.filter(function (svc) { return svc && svc.status === 'blocked'; });
      var region = services.filter(function (svc) { return svc && svc.status === 'region'; });
      var unlisted = services.filter(function (svc) {
        return svc && svc.status && svc.status !== 'blocked' && svc.status !== 'region' && svc.regionListed === false;
      });
      function names(list) {
        return list.map(function (svc) { return svc.name; }).filter(Boolean).join('、');
      }
      if (blocked.length) advice.push(names(blocked) + ' 无法连接。请检查代理是否覆盖对应域名，或稍后重新检测。');
      if (region.length) advice.push(names(region) + ' 在当前网络下地区受限，请换到受支持地区的节点后再测。');
      if (unlisted.length) advice.push(names(unlisted) + '的接口有响应，但 Cloudflare 判定的地区不在官方支持列表内，使用时仍可能被限制。');
      var regionUnknown = services.filter(function (svc) {
        return svc && svc.status && svc.status !== 'blocked' && svc.status !== 'region' && svc.regionListed == null;
      });
      if (regionUnknown.length) advice.push(names(regionUnknown) + ' 可以连接，但没有判定出所在地区，不能据此确认该地区受支持。');
      if (exitSpent > 0) {
        if ((consistency.distinctV4 >= 2) && (consistency.distinctV6 >= 2)) {
          advice.push('IPv4 和 IPv6 的出口都不一致，不同协议走了不同的网络。');
        } else if (consistency.distinctV6 >= 2) {
          advice.push('IPv6 出口不一致，支持 IPv6 的网站可能绕过当前代理。');
        } else {
          advice.push('不同来源的 IPv4 出口不一致，部分请求可能没有经过代理。');
        }
      }
      if (leakSpent > 0) {
        advice.push('WebRTC 暴露了真实地址。可以在浏览器中禁用 WebRTC，或在代理软件中开启 UDP 转发。');
      }
      var exposureGap = items.some(function (item) { return item.group === '出口与泄漏' && item.missing; });
      if (!advice.length) {
        advice.push(exposureGap
          ? '已确认的项目里没有明显风险。出口一致性或 WebRTC 有缺项，不能据此断定完全没有泄漏。'
          : '未发现明显的风险信号，当前 IP 适合访问 AI 服务。');
      }
      if (missingGate.length) {
        advice.push(missingGate.map(function (item) { return item.label; }).join('、') + '没有数据，未知项没有按安全计分。');
      }

      var note = '从 100 分扣起，综合网络身份、隐私标记、AI 服务可用性、出口一致性与 WebRTC 泄漏。';
      if (confidence === 'low') note += '机房判断来自 ASN 或组织名，仅供参考。';
      return pack(total, grade, advice, note);
    }
  };
})(window.IPQ = window.IPQ || {});
