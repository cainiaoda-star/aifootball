/**
 * 表格截图重建：App「自购方案详情」类截图是四列表格
 * （场次号 | 对阵 | 玩法 | 投注内容），PSM6 纯文本流会把行列搅在一起。
 * 这里用词级 bbox 按表头分列、按 y 分行，从单元格里直接配对「选项+赔率」。
 *
 * 入参：合并后的 tesseract.js 词列表（text + bbox + confidence）。
 * 出参：matches 数组（结构与 parse-ticket 的 matches 一致），失败返回 null。
 */
const KEYS = ['场次', '对阵', '玩法', '投注内容'];

function reconstructAppTable(words) {
  if (!Array.isArray(words) || words.length < 8) return null;

  const hs = medianHeight(words);
  const header = findHeader(words, hs);
  if (!header) return null;

  // 「预计中奖金额」以下是票样图/按钮，不是表格
  const cutoff = cutoffY(words);
  const body = words.filter(w =>
    yMid(w) > header.y1 &&
    (cutoff == null || w.bbox.y0 < cutoff));

  if (body.length < 4) return null;

  const colOf = colAssigner(header.bounds);
  const rows = groupRows(body, hs, colOf);

  // 场次号所在行 = 锚点；其余行按「离哪个锚点最近」归属（内容单元格比场次号高，
  // 下一场的首行内容常在本场场次号上方，按行硬切会划错场）。
  const anchors = [];
  for (const row of rows) {
    const c0 = (row[0] || []).map(w => w.text).join('').replace(/\s/g, '');
    const no = matchNoOf(c0);
    if (no) anchors.push({ no, y: rowYMid(row) });
  }
  if (!anchors.length) return null;

  const accs = anchors.map(a => ({ no: a.no, teams: [], play: '', content: [] }));
  for (const row of rows) {
    const c0 = (row[0] || []).map(w => w.text).join('').replace(/\s/g, '');
    if (matchNoOf(c0)) continue; // 锚点行自身
    const y = rowYMid(row);
    let best = 0, bd = Infinity;
    for (let i = 0; i < anchors.length; i++) {
      const d = Math.abs(anchors[i].y - y);
      if (d < bd) { bd = d; best = i; }
    }
    const acc = accs[best];
    for (const w of (row[1] || [])) acc.teams.push(w.text);
    for (const w of (row[2] || [])) acc.play += w.text; // 仅留档
    for (const w of (row[3] || [])) acc.content.push(w.text);
  }

  repairContentOverflow(accs);

  const matches = [];
  for (const a of accs) {
    const { home, away } = splitTeams(a.teams);
    const pairs = pairContent(a.content);
    // 有场次号就保留：队名后端按场次号补，选项空着让用户手填
    if (!a.no) continue;
    const base = { matchNo: a.no, home, away, handicap: null };
    const p0 = pairs[0] || {};
    const p1 = pairs[1] || {};
    matches.push({ ...base, option1: p0.opt || null, odds1: p0.odd ?? null, option2: p1.opt || null, odds2: p1.odd ?? null });
    // 第 3 组起的选项另起一行（同场多玩法，如 比分+总进球+半全场 一行三对）
    for (let i = 2; i < pairs.length; i++) {
      matches.push({ ...base, option1: pairs[i].opt, odds1: pairs[i].odd, option2: null, odds2: null });
    }
  }

  const usable = matches.filter(m => m.matchNo);
  return usable.length ? usable : null;
}

function rowYMid(row) {
  const ws = row.flat();
  return ws.length ? ws.reduce((s, w) => s + yMid(w), 0) / ws.length : 0;
}

/**
 * 内容列按最近锚点归属后，典型的错位是：下一场的首个「选项」被划给上一场，
 * 表现为上一场内容以悬空选项结尾、下一场以赔率开头 —— 把悬空选项移给下一场。
 */
function repairContentOverflow(accs) {
  for (let k = 0; k < accs.length - 1; k++) {
    const toks = accs[k].content;
    let pending = null, pendingIdx = -1;
    for (let i = 0; i < toks.length; i++) {
      const t = toks[i];
      if (isOddsToken(t)) { pending = null; pendingIdx = -1; }
      else if (isOptionToken(t)) { pending = t; pendingIdx = i; }
    }
    if (pending == null) continue;
    const next = accs[k + 1].content;
    if (next.length && isOddsToken(next[0])) {
      const moved = toks.splice(pendingIdx);
      accs[k + 1].content = moved.concat(next);
    }
  }
}

function isOddsToken(t) {
  const raw = String(t);
  const s = raw.replace(/[^\d.]/g, '');
  if (!/^\d{1,4}(\.\d{1,3})?$/.test(s)) return false;
  const wrapped = /[（(]/.test(raw) || /\./.test(s);
  if (!wrapped) return false;
  const n = parseFloat(s);
  return Number.isFinite(n) && n >= 1.01 && n <= 1000;
}

function isOptionToken(t) {
  // OCR 常在孤字前后粘标点（如「。4」），先洗掉非 数字/汉字/冒号/斜杠 的字符
  const s = String(t).replace(/[^\d\u4e00-\u9fa5:：\/]/g, '');
  if (!s) return false;
  return /^\d{1,2}:\d{1,2}$/.test(s) || /^[0-9]$/.test(s) || /^[让]?[胜平负]{1,2}$/.test(s)
    || /^[胜平负]\/[胜平负]$/.test(s) || /^(平其它|胜其它|负其它)$/.test(s);
}

/**
 * 双遍识别（PSM6 + PSM11）词框合并：
 * 同位置重复词保留分高的（置信度 + 长度奖励），孤字/长词互补。
 */
function mergeWordLists(lists) {
  const all = [];
  for (const list of lists) {
    for (const w of (list || [])) {
      if (w && w.text && w.text.trim() && w.bbox) all.push(w);
    }
  }
  const score = w => (w.confidence || 0) + (w.text.replace(/\s/g, '').length - 1) * 20;
  all.sort((a, b) => score(b) - score(a));
  const kept = [];
  for (const w of all) {
    const dup = kept.find(k => overlapRatio(k.bbox, w.bbox) > 0.55);
    if (!dup) kept.push(w);
  }
  return kept;
}

function overlapRatio(a, b) {
  const ix = Math.min(a.x1, b.x1) - Math.max(a.x0, b.x0);
  const iy = Math.min(a.y1, b.y1) - Math.max(a.y0, b.y0);
  if (ix <= 0 || iy <= 0) return 0;
  const inter = ix * iy;
  const areaA = (a.x1 - a.x0) * (a.y1 - a.y0);
  const areaB = (b.x1 - b.x0) * (b.y1 - b.y0);
  const min = Math.min(areaA, areaB);
  return min > 0 ? inter / min : 0;
}

/** 从词列表提取 tesseract 词（兼容 rec / rec.data 两种层级） */
function collectWords(rec) {
  const out = [];
  const blocks = (rec && (rec.blocks || (rec.data && rec.data.blocks))) || [];
  for (const b of blocks) {
    for (const p of (b.paragraphs || [])) {
      for (const l of (p.lines || [])) {
        for (const w of (l.words || [])) {
          if (w && w.text && w.text.trim() && w.bbox) out.push(w);
        }
      }
    }
  }
  const top = rec && (rec.words || (rec.data && rec.data.words));
  if (!out.length && Array.isArray(top)) {
    for (const w of top) if (w && w.text && w.text.trim() && w.bbox) out.push(w);
  }
  return out;
}

function medianHeight(words) {
  const hs = words.map(w => w.bbox.y1 - w.bbox.y0).filter(h => h > 0).sort((a, b) => a - b);
  return hs.length ? hs[Math.floor(hs.length / 2)] : 20;
}

/**
 * 表头：场次(号) / 对阵 / 玩法 / 投注内容。
 * OCR 常把表头拆碎（「对阵」→「对」+「阵」），所以按「词文本是 key 子串」归键，
 * 再在四键各选一个候选、要求 x 顺序递增且 y 在同一带上。
 */
function findHeader(words, hs) {
  const cand = KEYS.map(key => words
    .filter(w => {
      const t = w.text.replace(/\s/g, '');
      return t.length >= 1 && t.length <= key.length && key.includes(t);
    })
    .sort((a, b) => (b.confidence || 0) - (a.confidence || 0))
    .slice(0, 6));
  if (cand.some(c => c.length === 0)) return null;

  let best = null;
  const choose = (i, chosen) => {
    if (i === KEYS.length) {
      const ycs = chosen.map(yMid);
      const spread = Math.max(...ycs) - Math.min(...ycs);
      const xok = chosen.every((w, k) => k === 0 || w.bbox.x0 > chosen[k - 1].bbox.x0);
      if (xok && spread <= hs * 3 && (!best || spread < best.spread)) {
        best = { chosen: [...chosen], spread };
      }
      return;
    }
    for (const w of cand[i]) { chosen.push(w); choose(i + 1, chosen); chosen.pop(); }
  };
  choose(0, []);
  if (!best) return null;

  const picked = [...best.chosen].sort((a, b) => a.bbox.x0 - b.bbox.x0);
  const bounds = [];
  for (let i = 0; i < 3; i++) bounds.push((picked[i].bbox.x1 + picked[i + 1].bbox.x0) / 2);
  const y1 = Math.max(...picked.map(w => w.bbox.y1));
  return { bounds, y1 };
}

function colAssigner(bounds) {
  return w => {
    const cx = (w.bbox.x0 + w.bbox.x1) / 2;
    if (cx < bounds[0]) return 0;
    if (cx < bounds[1]) return 1;
    if (cx < bounds[2]) return 2;
    return 3;
  };
}

/** 跨列按 y 中心聚成行；行内按列分组，列内先按 x 排序再合并粘连碎词 */
function groupRows(words, hs, colOf) {
  const th = hs * 0.6;
  const sorted = [...words].sort((a, b) => yMid(a) - yMid(b));
  const lines = [];
  let cur = [sorted[0]];
  for (let i = 1; i < sorted.length; i++) {
    if (Math.abs(yMid(sorted[i]) - yMid(cur[cur.length - 1])) <= th) cur.push(sorted[i]);
    else { lines.push(cur); cur = [sorted[i]]; }
  }
  lines.push(cur);

  return lines.map(lineWords => {
    const cols = [[], [], [], []];
    for (const w of lineWords) cols[colOf(w)].push(w);
    return cols.map(c => mergeAdjacent(c.sort((a, b) => a.bbox.x0 - b.bbox.x0), hs));
  });
}

/** 同列相邻（重叠或小缝隙）碎词合成一个 token：「对」+「阵」→「对阵」，「负」+「负」→「负负」 */
function mergeAdjacent(words, hs) {
  if (words.length <= 1) return words;
  const out = [words[0]];
  for (let i = 1; i < words.length; i++) {
    const prev = out[out.length - 1];
    const w = words[i];
    const gap = w.bbox.x0 - prev.bbox.x1;
    const vOverlap = Math.min(prev.bbox.y1, w.bbox.y1) - Math.max(prev.bbox.y0, w.bbox.y0);
    if (gap < hs * 0.9 && vOverlap > hs * 0.4) {
      out[out.length - 1] = {
        text: prev.text + w.text,
        confidence: Math.min(prev.confidence || 0, w.confidence || 0),
        bbox: {
          x0: Math.min(prev.bbox.x0, w.bbox.x0), y0: Math.min(prev.bbox.y0, w.bbox.y0),
          x1: Math.max(prev.bbox.x1, w.bbox.x1), y1: Math.max(prev.bbox.y1, w.bbox.y1)
        }
      };
    } else {
      out.push(w);
    }
  }
  return out;
}

function yMid(w) { return (w.bbox.y0 + w.bbox.y1) / 2; }

function matchNoOf(s) {
  const m = /周[一二三四五六日天]\d{3}/.exec(s || '');
  return m ? m[0].replace('天', '日') : null;
}

function splitTeams(tokens) {
  const parts = [];
  for (const t of tokens) {
    const s = String(t).replace(/\s/g, '');
    if (!s) continue;
    if (/^VS\.?$/i.test(s)) { parts.push('VS'); continue; }
    if (/^VS/i.test(s)) { parts.push('VS'); parts.push(s.replace(/^VS\.?/i, '')); continue; }
    if (/VS$/i.test(s)) { parts.push(s.replace(/VS\.?$/i, '')); parts.push('VS'); continue; }
    parts.push(s);
  }
  const vsIdx = parts.findIndex(p => /^VS$/i.test(p));
  let home = null, away = null;
  if (vsIdx > 0) {
    home = parts.slice(0, vsIdx).join('') || null;
    away = parts.slice(vsIdx + 1).join('') || null;
  } else {
    const names = parts.filter(p => p && !/^VS$/i.test(p));
    home = names[0] || null;
    away = names[1] || null;
  }
  const clean = s => s ? s.replace(/[|｜]/g, '').trim() : s;
  return { home: clean(home), away: clean(away) };
}

/** 投注内容列 token 流 → [选项, 赔率] 对 */
function pairContent(tokens) {
  const cleanOpt = s => String(s).replace(/[^\d\u4e00-\u9fa5:：\/]/g, '');
  const pairs = [];
  let pending = null;
  for (const raw of tokens) {
    const t = String(raw).replace(/[（）【】\[\]]/g, m => (m === '(' || m === '（') ? '(' : ')').replace(/\s/g, '');
    if (!t || t === '(' || t === ')') continue;
    if (isOddsToken(t)) {
      const n = parseFloat(t.replace(/[^\d.]/g, ''));
      if (pending != null) { pairs.push({ opt: cleanOpt(pending), odd: Math.round(n * 100) / 100 }); pending = null; }
      continue;
    }
    if (isOptionToken(t)) {
      if (pending != null) pairs.push({ opt: cleanOpt(pending), odd: null }); // 上一选项没等到赔率，先落库
      pending = t;
    }
    // 其余 token（噪音）忽略
  }
  if (pending != null) pairs.push({ opt: cleanOpt(pending), odd: null });
  return pairs;
}

/**
 * 兜底：不依赖表头，把场次号+对阵抠出来。
 * 表格重建失败（表头没认出/票面截图裁切）时，保底把队伍带出来，选项赔率留给用户手填。
 *
 * App 截图的实际排版：对阵列竖排——主队 / VS / 客队 上下三行，VS 居中；
 * 「周三」和「004」常是两个分离的词块，要合并。只有场次号也行：后端按号反查队名。
 */
function extractTeamRows(words) {
  if (!Array.isArray(words) || words.length < 4) return null;
  const hs = medianHeight(words);
  const cut = cutoffY(words);
  const pool = words.filter(w =>
    w && w.text && w.text.trim() && w.bbox && (cut == null || w.bbox.y0 < cut));
  if (pool.length < 4) return null;

  // 场次号候选：周X 词块 + 右侧贴近的 2~3 位数字词块合并成「周XNNN」
  const nos = [];
  for (const w of pool) {
    const t = (w.text || '').replace(/\s/g, '');
    const m = /^(周[一二三四五六日天])(\d{0,3})$/.exec(t);
    if (!m) continue;
    const week = m[1];
    let digits = m[2];
    if (!/^\d{2,3}$/.test(digits)) {
      digits = '';
      for (const x of pool) {
        if (x === w) continue;
        const tx = (x.text || '').replace(/\s/g, '');
        if (!/^\d{2,3}$/.test(tx)) continue;
        const vov = Math.min(w.bbox.y1, x.bbox.y1) - Math.max(w.bbox.y0, x.bbox.y0);
        if (x.bbox.x0 >= w.bbox.x0 - hs && x.bbox.x0 <= w.bbox.x1 + hs * 2.5 && vov > hs * 0.3) {
          digits = tx;
          break;
        }
      }
    }
    if (/^\d{2,3}$/.test(digits)) nos.push({ no: week + digits, y: yMid(w) });
  }

  // VS 锚点：主队在上方最近的中文行，客队在下方最近的中文行（x 落在 VS 邻带内）
  const vsList = pool.filter(w => /^VS\.?$/i.test((w.text || '').replace(/\s/g, '')));
  const rows = [];
  const seenNo = new Set();

  const isTeam = w => {
    const t = (w.text || '').replace(/\s/g, '');
    if (!t || !/[\u4e00-\u9fa5]/.test(t)) return false;
    if (t.length > 10) return false;
    if (/周[一二三四五六日天]/.test(t)) return false;
    if (/^(场次|对阵|玩法|投注|内容|进球|比分|半全|全场|胜|平|负|胜平负|让球|方案|详情|订单|金额|状态|奖金|实体|票样|联系|店主|竞彩|足球|自购|保存|预览|预计|税后|已出|出票|过关|串|单关)$/.test(t)) return false;
    return true;
  };

  for (const vs of vsList) {
    const vy = yMid(vs);
    const band = w => w.bbox.x0 < vs.bbox.x1 + hs * 3 && w.bbox.x1 > vs.bbox.x0 - hs * 3;
    const pickSide = below => {
      const cand = pool.filter(w => {
        if (w === vs || !band(w) || !isTeam(w)) return false;
        const dy = yMid(w) - vy;
        return below ? (dy > hs * 0.25 && dy < hs * 2.5) : (dy < -hs * 0.25 && dy > -hs * 2.5);
      });
      if (!cand.length) return null;
      const best = cand.reduce((a, b) => Math.abs(yMid(a) - vy) < Math.abs(yMid(b) - vy) ? a : b);
      const line = cand.filter(w => Math.abs(yMid(w) - yMid(best)) < hs * 0.6)
        .sort((a, b) => a.bbox.x0 - b.bbox.x0);
      return line.map(w => w.text).join('') || null;
    };
    const home = pickSide(false);
    const away = pickSide(true);
    let no = null, bd = Infinity;
    for (const c of nos) {
      const d = Math.abs(c.y - vy);
      if (d < bd) { bd = d; no = c.no; }
    }
    if (!no && !home && !away) continue;
    if (no && seenNo.has(no)) continue;
    if (no) seenNo.add(no);
    rows.push({ matchNo: no, home, away, handicap: null, option1: null, odds1: null, option2: null, odds2: null });
  }

  // 只有场次号（VS 没认出/被裁）：后端按场次号反查队名
  for (const c of nos) {
    if (!seenNo.has(c.no)) {
      seenNo.add(c.no);
      rows.push({ matchNo: c.no, home: null, away: null, handicap: null, option1: null, odds1: null, option2: null, odds2: null });
    }
  }
  return rows.length ? rows : null;
}

function cutoffY(words) {
  let cut = null;
  for (const w of words) {
    const t = (w.text || '').replace(/\s/g, '');
    if (/预计|中奖金额|实体票样|票样|联系店主/.test(t)) {
      cut = cut == null ? w.bbox.y0 : Math.min(cut, w.bbox.y0);
    }
  }
  return cut;
}

module.exports = { reconstructAppTable, extractTeamRows, mergeWordLists, collectWords };
