// 用上游 score.js 的快照生成 score-v1.json。
// 快照对应 https://github.com/LinZicoffe/ip-purity-test 提交 96166cf448de3e185805d2e370951e5660996085（2026-11-24）。
// 随机序列来自 mulberry32(20261125)，共 6 个命名场景加 240 个随机组合。
// 在仓库根目录执行：node "tests/DLTools.Tests/Fixtures/IpPurity/generate-score-v1.js"
const fs = require('fs');
const path = require('path');
global.window = global;
require(path.join(__dirname, 'upstream-score.js'));
const compute = global.IPQ.SCORE.compute;

function mulberry32(seed) {
  return function () {
    seed |= 0; seed = seed + 0x6D2B79F5 | 0;
    let t = Math.imul(seed ^ seed >>> 15, 1 | seed);
    t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
    return ((t ^ t >>> 14) >>> 0) / 4294967296;
  };
}
const rand = mulberry32(20261125);
const pick = (list) => list[Math.floor(rand() * list.length)];
const types = [null, 'hosting', 'isp', 'business', 'education'];
const confidences = [null, 'high', 'medium', 'low'];
const bools = [null, true, false];
const statuses = [null, 'ok', 'risk', 'blocked', 'region', 'unlisted'];
const states = [null, 'leak', 'clean', 'unsupported', 'unknown'];

function flags() {
  return { hosting: pick(bools), vpn: pick(bools), proxy: pick(bools), tor: pick(bools), relay: pick(bools), mobile: pick(bools) };
}
function service(name) {
  return { name, status: pick(statuses), regionListed: pick(bools) };
}
function input() {
  const hasExit = rand() > 0.05;
  const hasIntel = hasExit && rand() > 0.08;
  const serviceCount = Math.floor(rand() * 4);
  const names = ['ChatGPT', 'Claude', '其他服务'];
  return {
    exitIp: hasExit ? (rand() > 0.5 ? '203.0.113.' + (1 + Math.floor(rand() * 200)) : '2001:db8::' + Math.floor(rand() * 9999).toString(16)) : null,
    intel: hasIntel ? { type: pick(types), confidence: pick(confidences), source: pick([null, 'IPinfo', 'ip-api.com', 'ASN / 组织名推断']), flags: flags() } : null,
    services: Array.from({ length: serviceCount }, (_, i) => service(names[i] || '服务' + i)),
    consistency: rand() > 0.1 ? { mismatch: pick([null, true, false]), distinctV4: Math.floor(rand() * 5), distinctV6: Math.floor(rand() * 4) } : null,
    leak: rand() > 0.1 ? { state: pick(states), leaked: rand() > 0.5 ? ['192.0.2.1', '192.0.2.2'].slice(0, 1 + Math.floor(rand() * 2)) : [], settled: pick([null, true, false]), exitKnown: pick([null, true, false]) } : null
  };
}

const named = [
  { name: 'no-exit', input: { exitIp: null, intel: null, services: [], consistency: null, leak: null } },
  { name: 'residential', input: residential() },
  { name: 'clean-dc', input: cleanDc() },
  { name: 'dc-proxy', input: dcProxy() },
  { name: 'privacy-cap', input: privacyCap() },
  { name: 'service-cap', input: serviceCap() }
];

function residential() {
  return {
    exitIp: '192.0.2.44',
    intel: { type: 'isp', confidence: 'high', source: 'IPinfo', flags: { hosting: false, vpn: false, proxy: false, tor: false, relay: false, mobile: false } },
    services: [{ name: 'ChatGPT', status: 'ok', regionListed: true }, { name: 'Claude', status: 'ok', regionListed: true }],
    consistency: { mismatch: false, distinctV4: 1, distinctV6: 1 },
    leak: { state: 'clean', leaked: [], settled: true, exitKnown: true }
  };
}
function cleanDc() {
  return {
    exitIp: '203.0.113.21',
    intel: { type: 'hosting', confidence: 'high', source: 'IPinfo', flags: { hosting: true, vpn: false, proxy: false, tor: false, relay: false, mobile: false } },
    services: [{ name: 'ChatGPT', status: 'ok', regionListed: true }, { name: 'Claude', status: 'ok', regionListed: true }],
    consistency: { mismatch: false, distinctV4: 1, distinctV6: 0 },
    leak: { state: 'clean', leaked: [], settled: true, exitKnown: true }
  };
}
function dcProxy() {
  return {
    exitIp: '203.0.113.7',
    intel: { type: 'hosting', confidence: 'high', source: 'IPinfo', flags: { hosting: true, vpn: false, proxy: true, tor: false, relay: false, mobile: false } },
    services: [{ name: 'ChatGPT', status: 'risk', regionListed: true }, { name: 'Claude', status: 'region', regionListed: true }],
    consistency: { mismatch: true, distinctV4: 2, distinctV6: 0 },
    leak: { state: 'unknown', leaked: [], settled: true, exitKnown: true }
  };
}
function privacyCap() {
  return {
    exitIp: '203.0.113.8',
    intel: { type: 'hosting', confidence: 'low', source: 'ASN / 组织名推断', flags: { hosting: true, vpn: true, proxy: true, tor: true, relay: true, mobile: false } },
    services: [],
    consistency: { mismatch: false, distinctV4: 1, distinctV6: 0 },
    leak: { state: 'clean', leaked: [], settled: true, exitKnown: true }
  };
}
function serviceCap() {
  return {
    exitIp: '203.0.113.9',
    intel: { type: 'isp', confidence: 'high', source: 'IPinfo', flags: { hosting: false, vpn: false, proxy: false, tor: false, relay: false, mobile: false } },
    services: [
      { name: 'ChatGPT', status: 'blocked', regionListed: false },
      { name: 'Claude', status: 'blocked', regionListed: null },
      { name: '其他服务', status: 'region', regionListed: true }
    ],
    consistency: { mismatch: null, distinctV4: 1, distinctV6: 0 },
    leak: { state: 'leak', leaked: ['192.0.2.8', '192.0.2.9'], settled: true, exitKnown: true }
  };
}

function toRequest(value) {
  return {
    exitIp: value.exitIp,
    intel: value.intel,
    services: value.services || [],
    consistency: value.consistency || {},
    webrtc: value.leak ? { state: value.leak.state, leaked: value.leak.leaked || [], settled: value.leak.settled, exitKnown: value.leak.exitKnown } : {}
  };
}
function freeze(score) {
  return {
    total: score.total,
    gradeKey: score.grade ? score.grade.key : null,
    gradeLabel: score.grade ? score.grade.label : null,
    gradeText: score.grade ? score.grade.text : null,
    note: score.note,
    footnote: score.footnote,
    advice: score.advice,
    items: score.items.map((item) => ({
      group: item.group,
      label: item.label,
      deduction: item.deduction || 0,
      gate: item.gate === true,
      missing: item.missing === true,
      evidence: item.evidence || '',
      capped: item.capped === true,
      absorbed: item.absorbed === true
    }))
  };
}

const cases = named.map((item) => ({ name: item.name, input: item.input, expected: freeze(compute(toRequest(item.input))) }));
for (let i = 0; i < 240; i++) {
  const value = input();
  cases.push({ name: 'random-' + i, input: value, expected: freeze(compute(toRequest(value))) });
}
const out = path.join(__dirname, 'score-v1.json');
const before = fs.existsSync(out) ? fs.readFileSync(out) : null;
const next = Buffer.from(JSON.stringify({ cases }));
fs.writeFileSync(out, next);
console.log('cases', cases.length, 'bytes', next.length, 'unchanged', before !== null && before.equals(next));
