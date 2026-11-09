// 半全场「胜平 3-1 / 负平 0-1」提取模型 v2（混合口径）
//
// 拆解：P(胜平) = SUM_n  P(全场 n:n) x P(半场主队领先 | 全场 n:n)
//       P(负平) = SUM_n  P(全场 n:n) x P(半场客队领先 | 全场 n:n)
//
// 其中：
//   · P(全场 n:n) 用竞彩总进球(JQ)报价定总额分布 + 主客占比二项拆分 —— 市场信息，最稳
//   · 方向率 q(n) 用 2023-2025 主流联赛 10340 场平局实测（见 tools/db.ps1 那几条查询）
//     1:1 -> 主领25.63% 客领23.42% / 2:2 -> 33.73% 31.40% / 3:3 -> 35.73% 34.86%
//
// 用法: node tools/halfdraw.js [jsonPath]
const fs = require('fs');

const path = process.argv[2] || 'temp/halfdraw.json';
const rows = JSON.parse(fs.readFileSync(path, 'utf8'));

const PAYBACK = 0.797;   // 竞彩半全场返奖率（实测九档倒数和 1.2545 -> 0.7971）
const MAXG = 10;
const GRID = 0.05, NG = 100, W1 = NG + 1;

// 实测方向率：平局各比分下「半场主队领先 / 客队领先」的占比
const QH = { 1: 0.2563, 2: 0.3373, 3: 0.3573, 4: 0.3600 };
const QA = { 1: 0.2342, 2: 0.3140, 3: 0.3486, 4: 0.3500 };

function pois(lam) {
  const p = new Float64Array(MAXG + 1);
  p[0] = Math.exp(-lam);
  for (let k = 1; k <= MAXG; k++) p[k] = p[k - 1] * lam / k;
  return p;
}
function comb(n, k) {
  let r = 1;
  for (let i = 0; i < k; i++) r = r * (n - i) / (i + 1);
  return r;
}

// 网格：由 SPF 赔率反解 lambda_h / lambda_a（只用来定主客进球占比）
const PW = new Float64Array(W1 * W1), PD = new Float64Array(W1 * W1), PL = new Float64Array(W1 * W1);
const PMF = [];
for (let i = 1; i <= NG; i++) PMF[i] = pois(i * GRID);
for (let i = 1; i <= NG; i++) {
  const ph = PMF[i];
  for (let j = 1; j <= NG; j++) {
    const pa = PMF[j];
    let pw = 0, pd = 0, pl = 0;
    for (let a = 0; a <= MAXG; a++) {
      const x = ph[a];
      for (let b = 0; b <= MAXG; b++) {
        const v = x * pa[b];
        if (a > b) pw += v; else if (a === b) pd += v; else pl += v;
      }
    }
    const k = i * W1 + j;
    PW[k] = pw; PD[k] = pd; PL[k] = pl;
  }
}

const out = [];
for (const r of rows) {
  if (!r.o3 || !r.o1 || !r.o0) continue;

  // 1) SPF -> 归一化概率 -> 反解 lambda
  const i3 = 1 / r.o3, i1 = 1 / r.o1, i0 = 1 / r.o0;
  const t = i3 + i1 + i0;
  const m3 = i3 / t, m1 = i1 / t, m0 = i0 / t;
  let best = Infinity, bk = 0;
  for (let i = 1; i <= NG; i++) {
    const base = i * W1;
    for (let j = 1; j <= NG; j++) {
      const k = base + j;
      const d = (PW[k] - m3) ** 2 + (PD[k] - m1) ** 2 + (PL[k] - m0) ** 2;
      if (d < best) { best = d; bk = k; }
    }
  }
  const lh = Math.floor(bk / W1) * GRID, la = (bk % W1) * GRID;
  const pHomeShare = lh / (lh + la);

  // 2) 全场总进球分布：优先用 JQ 报价，缺失回落到泊松
  let tot = [0, 0, 0, 0, 0, 0, 0, 0];   // 0..6, 7=7+
  let usedJq = false;
  const jkeys = ['j0', 'j1', 'j2', 'j3', 'j4', 'j5', 'j6', 'j7'];
  if (jkeys.every(k => r[k] > 1)) {
    let s = 0;
    for (let k = 0; k < 8; k++) s += 1 / r[jkeys[k]];
    for (let k = 0; k < 8; k++) tot[k] = (1 / r[jkeys[k]]) / s;
    usedJq = true;
  } else {
    const ph = pois(lh), pa = pois(la);
    for (let a = 0; a <= MAXG; a++) {
      for (let b = 0; b <= MAXG; b++) {
        const k = Math.min(a + b, 7);
        tot[k] += ph[a] * pa[b];
      }
    }
  }

  // 3) P(全场 n:n) = P(总进球 2n) x C(2n,n) p^n (1-p)^n
  const ftNN = {};
  for (let n = 0; n <= 4; n++) {
    const k = 2 * n;
    const pt = k <= 6 ? tot[k] : tot[7];
    ftNN[n] = pt * comb(2 * n, n) * Math.pow(pHomeShare, n) * Math.pow(1 - pHomeShare, n);
  }
  let pDraw = 0;
  for (let n = 0; n <= 4; n++) pDraw += ftNN[n];

  // 4) 拆方向
  let p31 = 0, p01 = 0;
  for (let n = 1; n <= 4; n++) {
    p31 += ftNN[n] * (QH[n] || 0.36);
    p01 += ftNN[n] * (QA[n] || 0.35);
  }

  const o31 = r.o31 || null;
  const o01 = r.o01 || o31;
  const done = r.h !== null && r.h !== undefined && r.hh !== null && r.hh !== undefined;
  const hit31 = done && r.hh > r.ha && r.h === r.a;
  const hit01 = done && r.hh < r.ha && r.h === r.a;

  out.push({
    ...r, lh, la, pHomeShare, pDraw, p31, p01, usedJq,
    ev31: o31 ? p31 * o31 : null,
    ev01: o01 ? p01 * o01 : null,
    implied31: o31 ? PAYBACK / o31 : null,
    hit31, hit01, isDraw: done && r.h === r.a, done,
    trusted: r.time >= '2026-10-21'
  });
}

const withOdds = out.filter(x => x.ev31 !== null);
const n = withOdds.length;
const sum = (a, f) => a.reduce((s, x) => s + f(x), 0);
const pct = x => (x * 100).toFixed(2) + '%';
const f3 = x => x.toFixed(3);

console.log(`样本 ${n} 场（竞彩有半全场报价且已完场）  其中 ${withOdds.filter(x => x.usedJq).length} 场用了总进球报价`);
console.log('');
console.log('=== 一、校准 ===');
console.log(`P(全场平) 模型 ${pct(sum(withOdds, x => x.pDraw) / n)}  实际 ${pct(sum(withOdds, x => x.isDraw ? 1 : 0) / n)}`);
console.log(`P(胜平)   模型 ${pct(sum(withOdds, x => x.p31) / n)}  实际 ${pct(sum(withOdds, x => x.hit31 ? 1 : 0) / n)}   市场隐含 ${pct(sum(withOdds, x => x.implied31) / n)}`);
console.log(`P(负平)   模型 ${pct(sum(withOdds, x => x.p01) / n)}  实际 ${pct(sum(withOdds, x => x.hit01 ? 1 : 0) / n)}`);
console.log('');
const tr = withOdds.filter(x => x.trusted);
console.log(`可信窗口（2026-10-21 起，半场比分真实）n=${tr.length}`);
console.log(`  胜平 实际 ${tr.filter(x => x.hit31).length} 次 = ${pct(tr.filter(x => x.hit31).length / tr.length)}   模型 ${pct(sum(tr, x => x.p31) / tr.length)}   市场隐含 ${pct(sum(tr, x => x.implied31) / tr.length)}`);
console.log(`  负平 实际 ${tr.filter(x => x.hit01).length} 次 = ${pct(tr.filter(x => x.hit01).length / tr.length)}   模型 ${pct(sum(tr, x => x.p01) / tr.length)}`);
console.log('');
console.log('=== 二、EV ===');
console.log(`平均 EV(胜平) ${f3(sum(withOdds, x => x.ev31) / n)}   平均 EV(负平) ${f3(sum(withOdds, x => x.ev01) / n)}`);
const dist = [[0.6, 0.7], [0.7, 0.8], [0.8, 0.9], [0.9, 1.0], [1.0, 1.1], [1.1, 9]];
for (const [lo, hi] of dist) {
  const g = withOdds.filter(x => x.ev31 >= lo && x.ev31 < hi);
  console.log(`  EV ${lo.toFixed(1)}~${hi.toFixed(1)}: ${String(g.length).padStart(3)} 场`);
}
console.log(`EV(胜平) >= 1.00 : ${withOdds.filter(x => x.ev31 >= 1).length} 场`);
console.log(`胜平 EV 高于负平 : ${withOdds.filter(x => x.ev31 > x.ev01).length} / ${n}`);
console.log('');
console.log('=== 三、EV 最高的 12 场（EV<1 也列出，用于看差距）===');
const top = [...withOdds].sort((a, b) => b.ev31 - a.ev31).slice(0, 12);
for (const x of top) {
  console.log(`  ${(x.matchNo || '--').padEnd(8)} ${x.league} ${x.home} vs ${x.away} | 赔 ${x.o31} | 模型P31 ${pct(x.p31)} 市场${pct(x.implied31)} | EV ${f3(x.ev31)} | ${x.hh}:${x.ha}->${x.h}:${x.a}${x.hit31 ? ' ★中' : ''}`);
}
console.log('');
console.log('=== 四、EV 最高的 6 场，负平对比 ===');
for (const x of top.slice(0, 6)) {
  console.log(`  ${(x.matchNo || '--').padEnd(8)} 胜平EV ${f3(x.ev31)}  负平EV ${f3(x.ev01)}  P31/P01 = ${(x.p31 / x.p01).toFixed(2)}  主客占比 ${x.pHomeShare.toFixed(2)}`);
}

// 五、EV 是否随「预期总进球」变化（市场若不区分大小球，这里会有系统性偏差）
console.log('');
console.log('=== 五、按预期总进球分档 ===');
const withTot = withOdds.slice();
for (const x of withTot) {
  // 用 JQ 报价算期望总进球（7+ 记 7.5）
  let s = 0, w = 0;
  const jk = ['j0', 'j1', 'j2', 'j3', 'j4', 'j5', 'j6', 'j7'];
  if (jk.every(k => x[k] > 1)) {
    let z = 0;
    for (let k = 0; k < 8; k++) z += 1 / x[jk[k]];
    for (let k = 0; k < 8; k++) { const p = (1 / x[jk[k]]) / z; s += p * (k === 7 ? 7.5 : k); w += p; }
    x.expTot = s / w;
  } else x.expTot = x.lh + x.la;
}
const tbands = [[0, 2.2], [2.2, 2.5], [2.5, 2.8], [2.8, 3.1], [3.1, 9]];
for (const [lo, hi] of tbands) {
  const g = withTot.filter(x => x.expTot >= lo && x.expTot < hi);
  if (!g.length) continue;
  console.log(`  期望总进球 ${lo}~${hi}  n=${String(g.length).padStart(3)}  平均EV ${f3(sum(g, x => x.ev31) / g.length)}  模型P31 ${pct(sum(g, x => x.p31) / g.length)}  实际胜平 ${pct(g.filter(x => x.hit31).length / g.length)}`);
}
console.log('');
console.log('=== 六、按赔率分档 ===');
const obands = [[12, 15], [15, 18], [18, 22], [22, 30], [30, 200]];
for (const [lo, hi] of obands) {
  const g = withTot.filter(x => x.o31 >= lo && x.o31 < hi);
  if (!g.length) continue;
  console.log(`  赔率 ${lo}~${hi}  n=${String(g.length).padStart(3)}  平均EV ${f3(sum(g, x => x.ev31) / g.length)}  模型P31 ${pct(sum(g, x => x.p31) / g.length)}  实际 ${pct(g.filter(x => x.hit31).length / g.length)}`);
}

// 七、指定日期之后的场次（在售候选）
if (process.argv[3]) {
  const from = process.argv[3];
  const live = withOdds.filter(x => x.time >= from && !x.done).sort((a, b) => b.ev31 - a.ev31);
  console.log('');
  console.log(`=== 七、${from} 起未开赛场次（n=${live.length}），按胜平 EV 降序 ===`);
  for (const x of live) {
    console.log(`  ${(x.matchNo || '--').padEnd(8)} ${(x.league || '').padEnd(6)} ${x.home} vs ${x.away} | 赔 ${x.o31} | P31 ${pct(x.p31)} 市场 ${pct(x.implied31)} | 胜平EV ${f3(x.ev31)} | 负平EV ${f3(x.ev01)}`);
  }
}
