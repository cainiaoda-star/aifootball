/*
 * 数字彩「避热门号」选号核心
 *
 * 前提必须写清楚：开奖是均匀随机的，任何一注的中奖概率完全相同。
 * 本模块不改变中奖概率，只估计「这注号码有多少人也会选」，
 * 从而降低中奖时被平分摊薄的幅度。
 *
 * 热度来源（彩票行为研究里反复出现的偏差）：
 *   1. 生日号偏好：1-31 被大量使用，1-12 更严重（月份）
 *   2. 连号偏好：人觉得 5,6,7 「像样」
 *   3. 同尾偏好：5,15,25
 *   4. 和值居中：避开极端和值
 *   5. 追随上期热号
 *   6. 图形/对称/等差
 *   7. 全挤在一个区间
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.LottoCore = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var GAMES = {
    ssq: {
      key: 'ssq', name: '双色球', org: '福彩',
      main: { max: 33, pick: 6, label: '红球' },
      extra: { max: 16, pick: 1, label: '蓝球' },
      jackpot: '1 / 17,721,088', rtp: 50,
      // 号段均衡：每段至少 1 个、至多 4 个。这是防止算法把 6 个号全推到 32/33 的硬闸。
      zones: [[1, 11], [12, 22], [23, 33]], zoneMax: 4,
      spanMin: 15
    },
    dlt: {
      key: 'dlt', name: '大乐透', org: '体彩',
      main: { max: 35, pick: 5, label: '前区' },
      extra: { max: 12, pick: 2, label: '后区' },
      jackpot: '1 / 21,425,712', rtp: 51,
      zones: [[1, 12], [13, 24], [25, 35]], zoneMax: 3,
      spanMin: 15
    },
    kl8: {
      key: 'kl8', name: '快乐8 · 选十', org: '福彩',
      main: { max: 80, pick: 10, label: '号码' },
      extra: null,
      jackpot: '1 / 8,911,711', rtp: 59,
      zones: [[1, 10], [11, 20], [21, 30], [31, 40], [41, 50], [51, 60], [61, 70], [71, 80]],
      zoneMax: 3,
      spanMin: 1
    }
  };

  // 大盘玩法里连号/同尾本来就很常见，惩罚权重必须压下来，
  // 否则会强烈偏向「等间距散点」这种反而是人爱选的好看号码。
  var WEIGHT = {
    ssq: { birthday: 1.0, run: 1.0, tail: 1.0, sum: 1.0, last: 1.0, pattern: 1.0, zone: 1.0 },
    dlt: { birthday: 1.0, run: 1.0, tail: 1.0, sum: 1.0, last: 1.0, pattern: 1.0, zone: 1.0 },
    kl8: { birthday: 0.35, run: 0.25, tail: 0.45, sum: 0.30, last: 0.90, pattern: 0.40, zone: 0.50 }
  };

  function mulberry32(a) {
    return function () {
      a |= 0; a = (a + 0x6D2B79F5) | 0;
      var t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  function sampleK(max, k, rand) {
    var pool = new Array(max), out = [], i, j, t;
    for (i = 0; i < max; i++) pool[i] = i + 1;
    for (i = 0; i < k; i++) {
      j = i + Math.floor(rand() * (max - i));
      t = pool[i]; pool[i] = pool[j]; pool[j] = t;
      out.push(pool[i]);
    }
    return out.sort(function (a, b) { return a - b; });
  }

  /*
   * 构造式采样：先决定每个号段取几个，再在段内抽号。
   * 拒绝采样（先随机抽再筛）在快乐8 上通过率只有 4%，候选池会小到没得挑。
   */
  function sampleStructured(g, rand) {
    var zones = g.zones, pick = g.main.pick, zmax = g.zoneMax;
    var nz = zones.length;

    for (var attempt = 0; attempt < 40; attempt++) {
      var alloc = new Array(nz), i;
      var total = 0;
      for (i = 0; i < nz; i++) { alloc[i] = 1; total++; }
      var left = pick - total;
      if (left < 0) { // 段比选号还多，退化回均匀采样
        return sampleK(g.main.max, pick, rand);
      }
      var guard = 0;
      while (left > 0 && guard++ < 4000) {
        i = Math.floor(rand() * nz);
        if (alloc[i] < zmax) { alloc[i]++; left--; }
      }
      if (left > 0) continue;

      var out = [], ok = true;
      for (i = 0; i < nz; i++) {
        var lo = zones[i][0], hi = zones[i][1], span = hi - lo + 1;
        if (alloc[i] > span) { ok = false; break; }
        var used = {}, cnt = 0, g2 = 0;
        while (cnt < alloc[i] && g2++ < 500) {
          var k = Math.floor(rand() * span);
          if (used[k]) continue;
          used[k] = 1; out.push(lo + k); cnt++;
        }
        if (cnt < alloc[i]) { ok = false; break; }
      }
      if (!ok) continue;
      out.sort(function (a, b) { return a - b; });
      if (passes(out, g)) return out;
    }
    return null;
  }

  function sumOf(a) { var s = 0; for (var i = 0; i < a.length; i++) s += a[i]; return s; }

  /* 热度惩罚：数值越大 = 越多人会选 = 越该避开 */
  function penalty(nums, gameKey, lastMain) {
    var g = GAMES[gameKey], w = WEIGHT[gameKey];
    var max = g.main.max, pick = g.main.pick;
    var p = 0, i, n;

    // 1. 生日号：1-12 是月份，1-31 是日期，32 以上基本没人用（大盘另算）
    if (max <= 40) {
      for (i = 0; i < nums.length; i++) {
        n = nums[i];
        if (n <= 12) p += 3.2 * w.birthday;
        else if (n <= 31) p += 1.7 * w.birthday;
      }
    } else {
      for (i = 0; i < nums.length; i++) if (nums[i] <= 31) p += 0.55 * w.birthday;
    }

    // 2. 连号
    var run = 1;
    for (i = 1; i < nums.length; i++) {
      if (nums[i] - nums[i - 1] === 1) { run++; p += 4.2 * w.run; }
      else { if (run >= 3) p += 5.5 * (run - 2) * w.run; run = 1; }
    }
    if (run >= 3) p += 5.5 * (run - 2) * w.run;

    // 3. 同尾
    var tails = {};
    for (i = 0; i < nums.length; i++) { var t = nums[i] % 10; tails[t] = (tails[t] || 0) + 1; }
    for (var k in tails) if (tails[k] >= 2) p += 3.0 * (tails[k] - 1) * w.tail;

    // 4. 和值居中（人本能避开极端和值，所以居中 = 热门）
    var mean = pick * (max + 1) / 2;
    var sd = Math.sqrt(pick * (max + 1) * (max - pick) / 12);
    var z = Math.abs((sumOf(nums) - mean) / sd);
    if (z < 0.35) p += 5.0 * w.sum;
    else if (z < 0.85) p += 2.2 * w.sum;
    else if (z > 2.0) p -= 1.6 * w.sum;

    // 5. 上期开过的号（追热号的人）
    if (lastMain && lastMain.length) {
      for (i = 0; i < nums.length; i++) if (lastMain.indexOf(nums[i]) >= 0) p += 6.5 * w.last;
    }

    // 6. 等差 / 镜像对称
    if (nums.length >= 3) {
      var d = nums[1] - nums[0], arith = d > 0;
      for (i = 2; i < nums.length && arith; i++) if (nums[i] - nums[i - 1] !== d) arith = false;
      if (arith) p += 14 * w.pattern;
    }
    var mirrored = nums.map(function (x) { return max + 1 - x; }).sort(function (a, b) { return a - b; });
    if (mirrored.join() === nums.join()) p += 11 * w.pattern;

    // 7. 全挤在一个区
    var zones = [0, 0, 0];
    for (i = 0; i < nums.length; i++) {
      zones[Math.min(2, Math.floor((nums[i] - 1) / (max / 3)))]++;
    }
    var zmax = Math.max(zones[0], zones[1], zones[2]);
    if (zmax >= pick - 1) p += 6.0 * w.zone;

    return p;
  }

  /* 硬约束：不让算法滑向「全是大号」这种畸形组合 */
  function passes(nums, g) {
    var i, j;
    if (g.zones) {
      for (i = 0; i < g.zones.length; i++) {
        var lo = g.zones[i][0], hi = g.zones[i][1], c = 0;
        for (j = 0; j < nums.length; j++) if (nums[j] >= lo && nums[j] <= hi) c++;
        if (c < 1 || c > g.zoneMax) return false;
      }
    }
    if (nums[nums.length - 1] - nums[0] < g.spanMin) return false;
    var run = 1;
    for (i = 1; i < nums.length; i++) {
      if (nums[i] - nums[i - 1] === 1) { if (++run >= 4) return false; }
      else run = 1;
    }
    return true;
  }

  function overlap(a, b) {
    var c = 0;
    for (var i = 0; i < a.length; i++) if (b.indexOf(a[i]) >= 0) c++;
    return c;
  }

  /* 附加区（蓝球 / 后区）：按热度反比加权抽样 */
  function pickExtra(g, rand, lastExtra) {
    if (!g.extra) return [];
    var max = g.extra.max, pick = g.extra.pick, i, n;
    var logw = [];
    for (n = 1; n <= max; n++) {
      var h = 0;
      if (max <= 16) {
        if (n <= 12) h += 2.2;                    // 月份号
        if (n === 6 || n === 8 || n === 9) h += 1.2; // 吉利数
        if (n === 16) h -= 0.8;                   // 最大的几个最冷
      }
      if (lastExtra && lastExtra.indexOf(n) >= 0) h += 2.5;
      logw.push(-0.85 * h);
    }
    var out = [];
    var avail = [];
    for (i = 0; i < max; i++) avail.push(i);
    for (var k = 0; k < pick; k++) {
      var mx = -Infinity;
      for (i = 0; i < avail.length; i++) if (logw[avail[i]] > mx) mx = logw[avail[i]];
      var tot = 0, wts = [];
      for (i = 0; i < avail.length; i++) { wts.push(Math.exp(logw[avail[i]] - mx)); tot += wts[i]; }
      var r = rand() * tot, acc = 0, chosen = avail[avail.length - 1];
      for (i = 0; i < avail.length; i++) { acc += wts[i]; if (r <= acc) { chosen = avail[i]; break; } }
      out.push(chosen + 1);
      avail.splice(avail.indexOf(chosen), 1);
    }
    return out.sort(function (a, b) { return a - b; });
  }

  // level: 0 稳健 / 1 均衡 / 2 极端
  // 取「惩罚分布」的哪一段（penalty 升序 = 冷门在前）。
  // 关键：不取绝对最优。否则所有用这个工具的人都会拿到同一批号码，
  // 工具本身就成了撞车制造机 —— 那就违背它存在的意义。
  var BAND = [[0.22, 0.52], [0.08, 0.32], [0.015, 0.10]];

  function generate(opts) {
    opts = opts || {};
    var gameKey = opts.game || 'ssq';
    var g = GAMES[gameKey];
    if (!g) throw new Error('unknown game: ' + gameKey);

    var count = Math.max(1, Math.min(10, opts.count || 3));
    var level = Math.max(0, Math.min(2, opts.level == null ? 1 : opts.level));
    var samples = opts.samples || 40000;
    var rand = mulberry32(opts.seed == null ? (Date.now() % 2147483647) : opts.seed);
    var lastMain = opts.lastMain || [];
    var lastExtra = opts.lastExtra || [];

    var pool = [], seen = {}, i;
    for (i = 0; i < samples; i++) {
      var m = g.zones ? sampleStructured(g, rand) : sampleK(g.main.max, g.main.pick, rand);
      if (!m) continue;
      if (!passes(m, g)) continue;
      var key = m.join(',');
      if (seen[key]) continue;
      seen[key] = 1;
      pool.push({ main: m, p: penalty(m, gameKey, lastMain) });
    }
    if (!pool.length) throw new Error('采样为空，请调大 samples');

    pool.sort(function (a, b) { return a.p - b.p; });

    // 冷门度 = 在全部分布里的「比多少比例的组合更冷门」
    var total = pool.length;
    for (i = 0; i < total; i++) {
      pool[i].cool = Math.round((1 - i / total) * 1000) / 10;
    }

    var band = BAND[level];
    var bLo = Math.floor(total * band[0]);
    var bHi = Math.min(total, Math.max(bLo + count * 12, Math.floor(total * band[1])));
    var top = pool.slice(bLo, bHi);
    if (!top.length) top = pool;

    // 两注之间最多重叠几个号。买多注的意义是覆盖不同情况，
    // 重叠太多等于花两份钱买同一注。
    var needSame = g.main.pick <= 6 ? 3 : Math.ceil(g.main.pick * 0.5);

    // 同一个号在一批里最多出现几次。不设这个上限时，
    // 冷门档会自然把 33 塞进每一注 —— 那又变成集中了。
    var capPer = Math.max(2, Math.round(count * g.main.pick / g.main.max * 1.8));
    var usedCnt = {};

    var picked = [], guard = 0;
    while (picked.length < count && guard++ < 60000) {
      var cand = top[Math.floor(rand() * top.length)];
      var skip = false, q;
      for (q = 0; q < cand.main.length; q++) {
        if ((usedCnt[cand.main[q]] || 0) >= capPer) { skip = true; break; }
      }
      if (skip) continue;
      for (i = 0; i < picked.length; i++) {
        if (overlap(picked[i].main, cand.main) >= needSame) { skip = true; break; }
      }
      if (skip) continue;
      picked.push(cand);
      for (q = 0; q < cand.main.length; q++) usedCnt[cand.main[q]] = (usedCnt[cand.main[q]] || 0) + 1;
    }
    // 极端档有时凑不满，放宽上限再补
    for (i = 0; picked.length < count && i < pool.length; i++) {
      var c2 = pool[Math.min(pool.length - 1, bLo + i)], d2 = false;
      for (var j = 0; j < picked.length; j++) {
        if (overlap(picked[j].main, c2.main) >= needSame) { d2 = true; break; }
      }
      if (!d2) picked.push(c2);
    }

    picked.sort(function (a, b) { return b.cool - a.cool; });

    return {
      game: gameKey,
      gameName: g.name,
      mainLabel: g.main.label,
      extraLabel: g.extra ? g.extra.label : null,
      tickets: picked.map(function (t) {
        return {
          main: t.main,
          extra: pickExtra(g, rand, lastExtra),
          cool: t.cool,
          penalty: Math.round(t.p * 10) / 10
        };
      }),
      poolSize: total
    };
  }

  function parseNumbers(text, max) {
    if (!text) return [];
    var parts = String(text).split(/[^0-9]+/).filter(Boolean);
    var out = [], seen = {};
    for (var i = 0; i < parts.length; i++) {
      var n = parseInt(parts[i], 10);
      if (!n || n < 1 || n > max || seen[n]) continue;
      seen[n] = 1; out.push(n);
    }
    return out.sort(function (a, b) { return a - b; }).slice(0, max);
  }

  return {
    GAMES: GAMES,
    generate: generate,
    penalty: penalty,
    parseNumbers: parseNumbers,
    sampleK: sampleK
  };
});
