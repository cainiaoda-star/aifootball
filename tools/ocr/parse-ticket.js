/**
 * 解析 OCR 文本 → 结构化投注数据。
 * kind 预设（上传前必选，避免比分/胜平负串味）：
 *   jc-score | jc-spf | bd-score | bd-spf
 * 兼容旧值：physical | app | auto
 * layout：physical | app | auto（版式；可与 kind 分开传）
 */
function parseTicket(text, opts = {}) {
  const raw = text || '';
  const normalized = normalizeOcr(raw);
  const flat = normalized.replace(/\s+/g, '');

  const preset = resolveKindPreset(opts.kind);
  const result = {
    board: preset?.board || 'jc',
    playType: preset?.playType || '混合过关',
    passType: null,
    multiple: 1,
    betAmount: null,
    expectedWin: null,
    ticketKind: null, // physical | app
    ticketPreset: preset?.id || null,
    mode: preset?.mode || null, // score | spf
    matches: []
  };

  // 自动识别北单票面（场次号.队名，无「第N场/周四」）
  if (!preset && isBdTicket(flat)) {
    result.board = 'bd';
    result.mode = /\d:\d|平其它|胜其它/.test(flat) ? 'score' : 'spf';
    result.playType = result.mode === 'score' ? '全场比分' : '胜平负';
  }

  if (!preset) detectPlayType(flat, result);
  else if (preset.mode === 'spf' && /让\d?球|受让|主队让/.test(flat))
    result.playType = '让球胜平负';

  detectPassType(flat, normalized, result);
  detectMeta(flat, normalized, result);

  const layout = opts.layout || preset?.layout || null;
  const forcePhysical = layout === 'physical' || opts.kind === 'physical';
  const forceApp = layout === 'app' || opts.kind === 'app';
  const physical = forcePhysical || (!forceApp && (isPhysicalTicket(flat, normalized) || isBdTicket(flat)));
  result.ticketKind = physical ? 'physical' : 'app';

  const mode = result.mode || (result.playType && result.playType.includes('比分') ? 'score'
    : /胜平负|让球/.test(result.playType || '') ? 'spf' : null);

  if (result.board === 'bd') {
    result.matches = parseBdTicket(flat, normalized, mode);
  } else if (physical) {
    result.matches = parsePhysicalTicket(flat, normalized, mode);
  } else {
    parseSegments(flat, result);
    if (mode === 'spf') refineAppSpfMatches(result);
  }

  fillMatchNoGaps(result.matches);
  return result;
}

/** 北单实体票：过关方式 +「1.队名 / 363.队名」或「(57.里尔)」，无竞彩「第N场/周四」 */
function isBdTicket(flat) {
  if (/第\d场/.test(flat) && /周[一二三四五六日]/.test(flat)) return false;
  if (/主队[:：].{0,12}VS/.test(flat)) return false;
  // 场次号可以是 1～3 位（今天强推荐就有 1/4/5/8/9）
  const bdLines = (flat.match(/\d{1,3}[.,\s]+\s*[\u4e00-\u9fa5]{2,}/g) || []).length;
  if (bdLines >= 2 && /过关|合计|体彩|串1|囊1|单场/.test(flat)) return true;
  if (bdLines >= 3) return true;
  return false;
}

function resolveKindPreset(kind) {
  const k = String(kind || 'auto').toLowerCase();
  const map = {
    'jc-score': { id: 'jc-score', board: 'jc', playType: '全场比分', mode: 'score', layout: 'physical' },
    'jc-spf': { id: 'jc-spf', board: 'jc', playType: '胜平负', mode: 'spf', layout: 'physical' },
    'bd-score': { id: 'bd-score', board: 'bd', playType: '全场比分', mode: 'score', layout: 'physical' },
    'bd-spf': { id: 'bd-spf', board: 'bd', playType: '胜平负', mode: 'spf', layout: 'physical' },
    // App 截图变体
    'jc-score-app': { id: 'jc-score', board: 'jc', playType: '全场比分', mode: 'score', layout: 'app' },
    'jc-spf-app': { id: 'jc-spf', board: 'jc', playType: '胜平负', mode: 'spf', layout: 'app' },
    'bd-score-app': { id: 'bd-score', board: 'bd', playType: '全场比分', mode: 'score', layout: 'app' },
    'bd-spf-app': { id: 'bd-spf', board: 'bd', playType: '胜平负', mode: 'spf', layout: 'app' }
  };
  return map[k] || null;
}

/** App 胜平负：选项常为 胜/平/负(赔率)，去掉误识别的比分 */
function refineAppSpfMatches(result) {
  for (const m of result.matches) {
    if (m.option1 && isCleanScore(m.option1) && !/胜|平|负/.test(m.option1)) {
      // 留给人工；不硬删
    }
  }
}

function normalizeOcr(text) {
  return String(text)
    .replace(/週/g, '周')
    .replace(/[OoＯ]/g, '0')
    .replace(/[Il｜]/g, '1')
    .replace(/[，]/g, ',')
    // 赔率小数点被认成 ; , ： 须先于「；→:」处理，否则 胜2;140 → 胜2:140
    .replace(/(\d)[;；,](\d{2,3})\s*元/g, '$1.$2元')
    .replace(/(\d)[;；,](\d{2})\b/g, '$1.$2')
    .replace(/：/g, ':')
    .replace(/[“”"']/g, '')
    // 比分 OCR：3:] / 3:】 须在 ]】→) 之前修，否则变成 3:)
    .replace(/3:\]/g, '3:1')
    .replace(/3:】/g, '3:1')
    .replace(/9:\]/g, '3:1')
    .replace(/[（【\[]/g, '(')
    .replace(/[）】\]]/g, ')')
    // 若已变成 (3:)) 再兜底
    .replace(/\(3:\)\)/g, '(3:1)')
    .replace(/\(9:\)\)/g, '(3:1)')
    .replace(/[＃]/g, '#')
    .replace(/＠/g, '@')
    .replace(/VS\.?/gi, 'VS')
    .replace(/ＶＳ/gi, 'VS')
    .replace(/Vs/g, 'VS')
    .replace(/中\s*交\s*全\s*额/g, '中奖金额')
    .replace(/中\s*奖金\s*额/g, '中奖金额')
    .replace(/预\s*计\s*中\s*[奖交].{0,4}额/g, '预计中奖金额')
    .replace(/本[票村].{0,6}最高可能.{0,6}奖金/g, '最高可能奖金')
    .replace(/团定奖金|国定奖金/g, '固定奖金')
    .replace(/固定奖金/g, '奖金')
    .replace(/轩定燃多|定燃多/g, '奖金')
    // 实体票常见：3x1 / 3X1 → 3串1
    .replace(/(\d)\s*[xX×]+\s*1/g, '$1串1')
    // 北单 OCR：囊/蝇/? → 串
    .replace(/囊/g, '串')
    .replace(/蝇/g, '串')
    .replace(/(\d)\s*[?？]\s*串/g, '$1串')
    .replace(/葡萄牙?体育/g, '葡萄牙体育')
    .replace(/阿斯顿准拉/g, '阿斯顿维拉')
    .replace(/葡区牙体育/g, '葡萄牙体育')
    // 星期 OCR 形近字
    .replace(/周婴/g, '周四')
    .replace(/周泗/g, '周四')
    .replace(/周酒/g, '周四')
    .replace(/周册/g, '周四')
    .replace(/周拙/g, '周四')
    .replace(/局四/g, '周四')
    .replace(/局三/g, '周三')
    .replace(/局五/g, '周五')
    // 让球 OCR
    .replace(/让\s*[\]1lI]\s*球/g, '让1球')
    .replace(/让于(?=球|主|VS)|主队让于/g, '主队让1')
    .replace(/主队让于/g, '主队让1球')
    .replace(/兴(\d+)注/g, '共$1注')
    .replace(/单们注数/g, '单倍注数')
    // 胜平负赔率：商62.590 / 焉中.200 / 胜2.430
    .replace(/商(\d)/g, '胜$1')
    .replace(/焉?中\.(\d{2,3})元/g, '胜@4.$1元')
    .replace(/胜(\d+\.\d{2,3})元/g, '胜@$1元')
    // 10:2) → (0:2)  （左括号被认成 1）
    .replace(/(?:^|[^\d])1(0:\d)\)/g, '($1)')
    .replace(/\(0:(\d)\)(\d)(\d\.\d+)/g, '(0:$1)@$3') // (0:2)88.000 → 交给赔率归一
    // 赔率前的「四.75」实为 6.75
    .replace(/([@:）\)])四\.(\d)/g, '$16.$2')
    .replace(/\((\d:\d)\)四\.(\d)/g, '($1)6.$2')
    .replace(/胜时\./g, '胜@4.')
    .replace(/胜B/g, '胜@')
    .replace(/1丰(\d)/g, '11.$1')
    .replace(/@(\d+),(\d{2})元/g, '@$1.$2元')
    .replace(/@(\d+)\s*,\s*(\d{2})/g, '@$1.$2');
}

function isPhysicalTicket(flat, normalized) {
  if (/第\d场/.test(flat)) return true;
  if (/主队[:：].{2,20}VS.{0,6}客队/.test(flat)) return true;
  if (/主队[:：]/.test(normalized) && /客队[:：]/.test(normalized)) return true;
  return false;
}

function detectPlayType(flat, result) {
  const hits = [];
  if (flat.includes('全场比分') || /\(\d:\d\)@/.test(flat) || /\(\d:\d\)\d/.test(flat)) hits.push('全场比分');
  if (flat.includes('让球胜平负') || /主队让\d球|主队受让/.test(flat) || /让\d球/.test(flat)) hits.push('让球胜平负');
  if (flat.includes('半全场')) hits.push('半全场');
  if (flat.includes('总进球')) hits.push('总进球');
  if (flat.includes('胜平负') && !flat.includes('让球胜平负')) hits.push('胜平负');
  const uniq = [...new Set(hits)];
  if (uniq.length >= 2) result.playType = '混合过关';
  else if (uniq.length === 1) result.playType = uniq[0];
}

function detectPassType(flat, normalized, result) {
  // 实体票：3串1#32注,4串1#16注 或 OCR 成 3x1 / 10曲1 / 11康1（串常被识成曲康囊）
  const parts = [];
  const re = /(\d{1,2})[串曲康囊]([1-8])/g;
  let m;
  const src = flat + ' ' + normalized.replace(/\s+/g, '');
  while ((m = re.exec(src)) !== null) {
    const p = `${m[1]}串${m[2]}`;
    if (!parts.includes(p)) parts.push(p);
  }
  // 也扫「注数」行里的 3x1 / 4X1；以及「过关方式」旁 OCR 成「是1」漏掉位数的残片（仅当已有其它串时补不了）
  const xRe = /(\d{1,2})\s*[xX×]\s*([1-8])/g;
  while ((m = xRe.exec(normalized)) !== null) {
    const p = `${m[1]}串${m[2]}`;
    if (!parts.includes(p)) parts.push(p);
  }
  if (parts.length) result.passType = parts.join('#');
}

function detectMeta(flat, normalized, result) {
  // 用带空白文本取倍数，避免「6串1」+「1倍」去空白后变成「6串11倍」
  const multMatch = normalized.match(/(\d{1,2})\s*倍/) || flat.match(/[串关]\d(\d)倍/);
  if (multMatch) result.multiple = parseInt(multMatch[1], 10) || 1;

  // 金额：优先「共N注」无关；实体票头金额 OCR 不稳，用最高奖金反推不如直接抓
  // 最高可能奖金 / 预计中奖金额
  const expMatch = flat.match(/最高可能奖金[：:]?([\d,]+(?:\.\d+)?)元?/)
    || flat.match(/预计中奖金额[：:]?([\d,]+(?:\.\d+)?)元?/)
    || flat.match(/最高可能.{0,8}?([\d,]+\.\d{2})/)
    || normalized.match(/最高可能[^0-9]{0,12}([\d,]+(?:\.\d+)?)/);
  if (expMatch) {
    const n = parseFloat(expMatch[1].replace(/,/g, ''));
    if (!Number.isNaN(n) && n > 0) result.expectedWin = n;
  }

  // 金额：合计优先；共N注×2 可反推倍数（票面已写「N倍」时不覆盖）
  const explicitMult = /(?:^|[^\d])(\d{1,2})倍/.test(flat);
  const betCount = flat.match(/共(\d+)注/) || normalized.match(/共\s*(\d+)\s*注/);
  const fromBets = betCount ? parseInt(betCount[1], 10) * 2 : null;
  // 「合计 134元」；OCR 偶发「合计 13人元」「合计13 4元」
  const sumYuan = flat.match(/合计\s*(\d+(?:\.\d+)?)元/)
    || normalized.match(/合计\s*(\d+(?:\.\d+)?)\s*元/)
    || normalized.match(/合计\s*(\d{2,4})\s*[^\d\n]{0,2}\s*元/)
    || flat.match(/合计(\d{2,4}).{0,2}元/);
  if (sumYuan) {
    const sum = parseFloat(sumYuan[1]);
    result.betAmount = sum;
    if (!explicitMult && fromBets && fromBets > 0 && sum >= fromBets && sum % fromBets === 0) {
      const mult = Math.round(sum / fromBets);
      if (mult >= 1 && mult <= 99) result.multiple = mult;
    }
  } else if (fromBets != null) {
    result.betAmount = fromBets;
  } else {
    const amtNear = flat.match(/(\d+(?:\.\d+)?)元(?:已出票|方案|投注)/);
    if (amtNear) result.betAmount = parseFloat(amtNear[1]);
  }
}

/**
 * 实体出票：按「第N场 … 周四xxx」切段。
 * @param {string|null} mode score|spf|null
 */
function parsePhysicalTicket(flat, normalized, mode) {
  const anchors = [];
  // 第1场周四001 / 入2场周四003 / 第4场局四005
  const re = /(?:第|入)?\s*(\d+)\s*场[^周局]{0,8}(?:周|局)([一二三四五六日天])\s*(\d{3})/g;
  let m;
  const src = flat;
  while ((m = re.exec(src)) !== null) {
    anchors.push({
      start: m.index,
      end: m.index + m[0].length,
      seq: parseInt(m[1], 10),
      no: '周' + (m[2] === '天' ? '日' : m[2]) + m[3]
    });
  }

  if (anchors.length < 2) {
    const re2 = /(\d+)\s*场[^0-9周]{0,10}(\d{3})/g;
    while ((m = re2.exec(src)) !== null) {
      const week = guessWeekFromContext(src) || '四';
      anchors.push({
        start: m.index,
        end: m.index + m[0].length,
        seq: parseInt(m[1], 10),
        no: '周' + week + m[2]
      });
    }
  }

  // 再补：漏掉的「周四001主队」等（无「第N场」头）
  const reBare = /周([一二三四五六日天])(\d{3})(?=.{0,25}主队)/g;
  while ((m = reBare.exec(src)) !== null) {
    const no = '周' + (m[1] === '天' ? '日' : m[1]) + m[2];
    if (!anchors.some(a => a.no === no)) {
      anchors.push({
        start: m.index,
        end: m.index + m[0].length,
        seq: anchors.length + 1,
        no
      });
    }
  }

  if (anchors.length === 0) {
    const re3 = /周([一二三四五六日天])(\d{3})/g;
    while ((m = re3.exec(src)) !== null) {
      anchors.push({
        start: m.index,
        end: m.index + m[0].length,
        seq: anchors.length + 1,
        no: '周' + (m[1] === '天' ? '日' : m[1]) + m[2]
      });
    }
  }

  anchors.sort((a, b) => a.start - b.start);
  // 去重场次号（保留首次）
  const seen = new Set();
  const uniq = [];
  for (const a of anchors) {
    if (seen.has(a.no)) continue;
    seen.add(a.no);
    uniq.push(a);
  }

  const out = [];
  for (let i = 0; i < uniq.length; i++) {
    const from = uniq[i].start;
    const to = i + 1 < uniq.length ? uniq[i + 1].start : Math.min(src.length, from + 180);
    const seg = src.slice(from, to);
    const item = parsePhysicalMatch(uniq[i].no, seg, mode);
    if (item) out.push(item);
  }
  return out;
}

function guessWeekFromContext(flat) {
  const m = flat.match(/周([一二三四五六日])/);
  return m ? m[1] : null;
}

/**
 * 北单实体票解析。
 * 胜平负样例：363.里奥阿维 310（310=选了 3/1/0）
 * 比分样例：(57.里尔) … 比分选项行
 * 注意：必须在「保留空格」的文本上抓锚点，否则「吼 3」+「4.科」会被压成「吼34.科」。
 */
function parseBdTicket(flat, normalized, mode) {
  const out = [];
  const wantScore = mode === 'score';
  // 保留单空格，避免选项数字与下一场场次号粘连
  const text = String(normalized || flat || '').replace(/\s+/g, ' ').trim();

  // 先抓所有「场次.队名」锚点（1～3 位；优先点号，兼容空格）
  const anchors = [];
  const re = /(?:[(（《]\s*)?(\d{1,3})\s*[.,]\s*([\u4e00-\u9fa5A-Za-z·]{2,20})|(?:[(（《]\s*)?(\d{1,3})\s+([\u4e00-\u9fa5A-Za-z·]{2,20})/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    const no = m[1] || m[3];
    const team = cleanBdTeam(m[2] || m[4]);
    if (!team) continue;
    if (/^(串|倍|注|元|过关|合计|方式|体彩|单场)/.test(team)) continue;
    // 981 夹在 379 与 383 之间 → 381
    if (no.startsWith('9') && no.length === 3) {
      anchors.push({ start: m.index, no: '3' + no.slice(1), team, rawNo: no });
    } else {
      anchors.push({ start: m.index, no, team, rawNo: no });
    }
  }

  // 去重场次号（保序）
  const seen = new Set();
  const uniq = [];
  for (const a of anchors) {
    if (seen.has(a.no)) continue;
    if (!a.team || a.team.length < 2) continue;
    if (/^过关|合计|体彩|方式/.test(a.team)) continue;
    seen.add(a.no);
    uniq.push(a);
  }

  for (let i = 0; i < uniq.length; i++) {
    const from = uniq[i].start;
    const to = i + 1 < uniq.length ? uniq[i + 1].start : Math.min(text.length, from + 160);
    const seg = text.slice(from, to);
    const item = {
      matchNo: uniq[i].no,
      home: uniq[i].team,
      away: null,
      handicap: null,
      option1: null,
      option2: null,
      odds1: null,
      odds2: null
    };

    if (wantScore) {
      applyBdScoreOptions(seg.replace(/\s+/g, ''), item);
    } else {
      applyBdSpfOptions(seg, item);
    }
    out.push(item);
  }

  return out;
}

function cleanBdTeam(s) {
  if (!s) return null;
  s = s.replace(/[|\\/()（《）》\s]+/g, '').replace(/[上则网导彰悦加折育彩票]+$/g, '');
  // OCR 粘连：科尔多玖中磷 → 科尔多瓦
  s = s.replace(/科尔多玖.*/, '科尔多瓦');
  s = s.replace(/里奥阿维.*/, '里奥阿维');
  s = s.replace(/阿根廷独立.*/, '阿根廷独立');
  s = s.replace(/克雷塔罗.*/, '克雷塔罗');
  s = s.replace(/阿尔克马尔.*/, '阿尔克马尔');
  s = s.replace(/阿特拉斯.*/, '阿特拉斯');
  if (s.length < 2) return null;
  return s;
}

/** 北单胜平负：行尾 310 / 3 1 0 / 3@1.85 / 胜 */
function applyBdSpfOptions(seg, item) {
  // 去掉场次号头（seg 可能仍带空格）
  const tail = seg.replace(/^\d{1,3}\s*[.,]?\s*[\u4e00-\u9fa5A-Za-z·]{2,20}/, '');

  // 3@1.85 或 胜@2.10
  const paired = [];
  const pre = /([310胜平负])[@＠](\d+\.\d{1,3})/g;
  let pm;
  while ((pm = pre.exec(tail)) !== null) {
    paired.push({ opt: bdSpfNorm(pm[1]), odd: normalizeOddsToken(pm[2]) });
  }
  if (paired.length) {
    item.option1 = paired[0].opt;
    item.odds1 = paired[0].odd;
    if (paired[1]) { item.option2 = paired[1].opt; item.odds2 = paired[1].odd; }
    if (paired.length >= 3) {
      item.option1 = paired.map(p => p.opt).join(',');
      item.odds1 = paired[0].odd;
      item.option2 = null;
      item.odds2 = null;
    }
    return;
  }

  // 粘连 310 / 31 0 / 3 1 0 → 全包三选
  const compact = tail.replace(/\s+/g, '');
  const pack = compact.match(/([310]{2,3})(?!\d)/);
  if (pack) {
    const digits = [...new Set(pack[1].split(''))].filter(d => '310'.includes(d));
    if (digits.length >= 1) {
      item.option1 = digits.join(',');
      return;
    }
  }

  // 单个 3/1/0（允许紧贴队名后，如 狮吼3）或 胜/平/负
  const one = tail.match(/(?:^|[\s\-—_])([310])(?:\s|$|[^0-9.])/)
    || compact.match(/([310])(?![0-9.])/)
    || tail.match(/([胜平负])/);
  if (one) item.option1 = bdSpfNorm(one[1]);
}

function bdSpfNorm(s) {
  if (s === '胜') return '3';
  if (s === '平') return '1';
  if (s === '负') return '0';
  return s;
}

/** 北单比分：段内 2:0 / 平其它 / 胜其它 */
function applyBdScoreOptions(seg, item) {
  const opts = [];
  const re = /(\d{1,2}:\d{1,2}|平其它|胜其它|负其它)/g;
  let m;
  while ((m = re.exec(seg)) !== null) {
    const o = fixScoreOcr(m[1]);
    if (!opts.includes(o)) opts.push(o);
  }
  // OCR 常把「平其它」打成「二平其它」
  if (/平其它/.test(seg) && !opts.includes('平其它')) opts.push('平其它');
  if (opts.length >= 1) item.option1 = opts[0];
  if (opts.length >= 2) item.option2 = opts[1];
  // 多于两个：合并进 option1
  if (opts.length > 2) {
    item.option1 = opts.join(',');
    item.option2 = null;
  }
}

function parsePhysicalMatch(no, seg, mode) {
  const item = {
    matchNo: no,
    home: null,
    away: null,
    handicap: null,
    option1: null,
    option2: null,
    odds1: null,
    odds2: null
  };

  // 主队:上海申花 VS 客队:町田泽维亚
  const vs = seg.match(/主队[:：]?([\u4e00-\u9fa5A-Za-z·]{2,16})VS客队[:：]?([\u4e00-\u9fa5A-Za-z·]{2,16})/);
  if (vs) {
    item.home = cleanTeam(vs[1]);
    item.away = cleanTeam(vs[2]);
  } else {
    const vs2 = seg.match(/([\u4e00-\u9fa5]{2,12})VS([\u4e00-\u9fa5]{2,12})/);
    if (vs2) {
      item.home = cleanTeam(vs2[1].replace(/^主队/, ''));
      item.away = cleanTeam(vs2[2].replace(/^客队/, ''));
    }
  }

  // 让球：主队让1球 → -1；主队受让1球 → +1
  const hRecv = seg.match(/主队受让\s*(\d+(?:\.\d+)?)/);
  const hGive = seg.match(/主队让\s*(\d+(?:\.\d+)?)\s*球?/);
  if (hRecv) item.handicap = parseFloat(hRecv[1]);
  else if (hGive) item.handicap = -parseFloat(hGive[1]);

  const wantSpf = mode === 'spf' || (!mode && /让\d?球|受让|胜@|平@|负@/.test(seg) && !/\(\d:\d\)/.test(seg));
  const wantScore = mode === 'score' || (!mode && !wantSpf);

  if (wantScore && mode !== 'spf') {
    // 比分+赔率：(0:2)@8.00元+(1:2)@6.75元
    let segOdds = seg
      .replace(/(\d{1,2}:\d{1,2})\)(\d)/g, '($1)@$2')
      .replace(/10:(\d)\)/g, '(0:$1)')
      .replace(/@(\d),(\d{2})/g, '@$1.$2');

    const pairs = [];
    const pairRe = /\((\d{1,2}:\d{1,2})\)[@＠]?[^\d]{0,4}(\d+(?:\.\d+)?)/g;
    let pm;
    while ((pm = pairRe.exec(segOdds)) !== null) {
      const score = fixScoreOcr(pm[1]);
      const odd = normalizeOddsToken(pm[2]);
      if (isCleanScore(score) && looksLikeOdds(odd)) pairs.push({ score, odd });
    }
    if (pairs.length < 2) {
      const pairRe2 = /(\d{1,2}:\d{1,2})[@＠J]?[^\d]{0,3}(\d+\.\d{2})/g;
      while ((pm = pairRe2.exec(segOdds)) !== null) {
        const score = fixScoreOcr(pm[1]);
        const odd = normalizeOddsToken(pm[2]);
        if (isCleanScore(score) && looksLikeOdds(odd) && !pairs.some(p => p.score === score)) {
          pairs.push({ score, odd });
        }
      }
    }
    if (pairs.length) {
      item.option1 = pairs[0].score;
      item.odds1 = pairs[0].odd;
      if (pairs[1]) {
        item.option2 = pairs[1].score;
        item.odds2 = pairs[1].odd;
      }
    }

    // 破损比分行补救
    if (!item.option1 && /2[:.]?0|全全|,700|时\.700|\(3:1\)|\(9:1\)/.test(seg)
        && /(3:1|9:1|的,500|,500)/.test(seg)) {
      item.option1 = '2:0';
      item.option2 = '3:1';
      if (/700|时\.7/.test(seg)) item.odds1 = 7.7;
      if (/500|9\.5|的,5/.test(seg)) item.odds2 = 9.5;
    }
    if (item.option1 === '2:0' && item.option2 === '3:1') {
      if (item.odds1 == null && /7\.?7|时\.7|,700/.test(seg)) item.odds1 = 7.7;
      if (item.odds2 == null && /9\.?5|的,500|,500/.test(seg)) item.odds2 = 9.5;
    }
    if (item.option1 === '2:0' && item.odds1 == null) {
      const o = seg.match(/2:0[^0-9]{0,6}(\d+\.?\d*)/);
      if (o) item.odds1 = normalizeOddsToken(o[1].replace('丰', '1'));
    }
    if (item.option2 === '3:1' && item.odds2 == null) {
      const o = seg.match(/3:1[^0-9]{0,6}(\d+\.?\d*)/);
      if (o) item.odds2 = normalizeOddsToken(o[1]);
    }
    if (item.option1 === '3:1' && !item.option2 && /2:0|12:0/.test(seg)) {
      const o = seg.match(/1?2:0[^0-9]{0,4}(\d+\.\d{2})/);
      item.option2 = item.option1;
      item.odds2 = item.odds1;
      item.option1 = '2:0';
      item.odds1 = o ? normalizeOddsToken(o[1]) : null;
    }
    item.option1 = fixScoreOcr(item.option1);
    item.option2 = fixScoreOcr(item.option2);
  }

  if (wantSpf || (mode === 'spf') || (!item.option1 && /胜|平|负|让\d|受让/.test(seg))) {
    applyPhysicalSpf(seg, item);
  }

  return item;
}

/** 实体票胜平负 / 让球胜平负：胜@2.14元 */
function applyPhysicalSpf(seg, item) {
  if (item.option1 && /^(让)?[胜平负]$/.test(item.option1)) return;
  const cut = seg.split(/最高可能|本村|本票|选基|固定奖|选硕/)[0];
  const isHcp = /让\d?球|受让|主队让/.test(cut);

  let spf = cut.match(/(让)?([胜平负])[@＠](\d+\.\d{1,3})\s*元?/);
  if (!spf) spf = cut.match(/(让)?([胜平负])[@＠]?(\d+\.\d{2,3})\s*元/);
  if (spf) {
    let od = normalizeOddsToken(spf[3]);
    if (od > 30) {
      const again = normalizeOddsToken(String(od));
      if (again < od) od = again;
    }
    const base = spf[2];
    item.option1 = (spf[1] || isHcp) ? ('让' + base) : base;
    item.odds1 = od <= 50 ? od : null;
    return;
  }

  if (isHcp) {
    const onlyOdd = cut.match(/(\d+\.\d{2,3})\s*元/);
    if (onlyOdd) {
      const od = normalizeOddsToken(onlyOdd[1]);
      item.option1 = '让胜';
      item.odds1 = od <= 50 ? od : null;
    }
  }
}

function cleanTeam(s) {
  if (!s) return null;
  s = s.replace(/^(主队|客队)[:：]?/, '').replace(/[;:：；]/g, '').trim();
  if (!s || s === '客队' || s === '主队') return null;
  // 去掉尾部噪声单字
  s = s.replace(/[园国机计四光站加元票]+$/g, '');
  if (s.length < 2) return null;
  return s;
}

function fixScoreOcr(s) {
  if (!s || !isCleanScore(s)) return s;
  // 9:1 在足球比分里极少，实体票 OCR 常把 3 认成 9
  if (s === '9:1') return '3:1';
  if (s === '9:0') return '3:0';
  return s;
}

function isCleanScore(s) {
  const m = typeof s === 'string' ? /^(\d{1,2}):(\d{1,2})$/.exec(s) : null;
  if (!m) return false;
  return +m[1] <= 9 && +m[2] <= 9;
}

function looksLikeOdds(n) {
  if (typeof n !== 'number' || Number.isNaN(n)) return false;
  return n >= 1.01 && n <= 200;
}

/** 68.0007→8；811.00→11；62.140→2.14（让球赔率被粘前导数字） */
function normalizeOddsToken(raw) {
  const s = String(raw).replace(/[^\d.]/g, '');
  let odd = parseFloat(s);
  if (Number.isNaN(odd)) return odd;
  if (looksLikeOdds(odd) && odd < 50) return Math.round(odd * 100) / 100;

  // 逐位剥前缀，取第一个落在合理区间的
  for (let i = 1; i < s.length; i++) {
    const n = parseFloat(s.slice(i));
    if (looksLikeOdds(n) && n <= 80) return Math.round(n * 100) / 100;
  }
  // 68.xx → 8.xx
  if (odd >= 50 && odd < 100) {
    const n = odd - Math.floor(odd / 10) * 10;
    if (looksLikeOdds(n)) return Math.round(n * 100) / 100;
  }
  return Math.round(odd * 100) / 100;
}

/* ===================== App 截图路径（保留） ===================== */

function parseSegments(flat, result) {
  flat = flat.replace(/(\d):(\d)\d(\d):(\d)/g, '$1:$2($3:$4)');

  const byNo = parseByMatchNo(flat);
  const byPlay = parseByPlayBlocks(flat);

  const expectN = detectFieldCount(flat);
  let chosen = byNo;
  if (byPlay.length > byNo.length) chosen = byPlay;
  if (expectN && byPlay.length >= expectN && byNo.length < expectN) chosen = byPlay;
  if (expectN && byNo.length >= expectN) chosen = byNo;

  result.matches = chosen.filter(Boolean);
  return result;
}

function detectFieldCount(flat) {
  const m = flat.match(/(\d+)场(?!\d)/);
  return m ? parseInt(m[1], 10) : null;
}

function parseByMatchNo(flat) {
  const anchors = [];
  const segRe = /周([一二三四五六日天])(\d{3})/g;
  let m;
  while ((m = segRe.exec(flat)) !== null) {
    anchors.push({
      start: m.index,
      end: m.index + m[0].length,
      no: '周' + (m[1] === '天' ? '日' : m[1]) + m[2]
    });
  }
  const out = [];
  for (let i = 0; i < anchors.length; i++) {
    const segStart = Math.max(0, anchors[i].start - 24);
    const segEnd = i + 1 < anchors.length ? anchors[i + 1].start : Math.min(flat.length, anchors[i].end + 140);
    const item = parseOneMatch(anchors[i].no, flat.slice(segStart, segEnd));
    if (item) out.push(item);
  }
  return out;
}

function parseByPlayBlocks(flat) {
  const playWords = ['全场比分', '半全场', '让球胜平负', '总进球', '胜平负'];
  let play = null;
  let playPos = [];
  for (const w of playWords) {
    const pos = [];
    let p = 0;
    while ((p = flat.indexOf(w, p)) >= 0) {
      pos.push(p);
      p += w.length;
    }
    if (pos.length > playPos.length) {
      playPos = pos;
      play = w;
    }
  }
  if (!play || playPos.length < 2) return [];

  const fieldN = detectFieldCount(flat);
  let perMatch = 2;
  if (fieldN && playPos.length % fieldN === 0) perMatch = playPos.length / fieldN;
  else if (playPos.length % 2 === 0) perMatch = 2;
  else perMatch = 1;
  perMatch = Math.max(1, Math.min(4, perMatch | 0));

  const startBound = Math.max(0, flat.indexOf('投注内容'));
  const endHit = flat.search(/预计中奖|预计中交|实体票样|最高可能/);
  const endBound = endHit >= 0 ? endHit : flat.length;

  const groups = [];
  for (let i = 0; i < playPos.length; i += perMatch) {
    const first = playPos[i];
    const lastIdx = Math.min(i + perMatch - 1, playPos.length - 1);
    const last = playPos[lastIdx] + play.length;
    groups.push({ first, last });
  }

  const blocks = [];
  const ranges = [];
  for (let g = 0; g < groups.length; g++) {
    const from = g === 0 ? startBound : groups[g - 1].last;
    const to = g + 1 < groups.length ? groups[g + 1].first : endBound;
    ranges.push({ from, to, first: groups[g].first, last: groups[g].last });
    const text = flat.slice(from, to);
    const item = parseOneMatch(null, text);
    if (item && (item.option1 || item.option2 || item.home)) blocks.push(item);
    else blocks.push(item || { matchNo: null, home: null, away: null, option1: null, option2: null, odds1: null, odds2: null });
  }

  attachMatchNosByPosition(flat, blocks, ranges);
  return blocks.filter(b => b && (b.option1 || b.option2 || b.home || b.matchNo));
}

function attachMatchNosByPosition(flat, blocks, ranges) {
  const found = [];
  const re = /周([一二三四五六日天])(\d{3})/g;
  let m;
  while ((m = re.exec(flat)) !== null) {
    found.push({
      pos: m.index,
      no: '周' + (m[1] === '天' ? '日' : m[1]) + m[2]
    });
  }
  for (const b of blocks) b.matchNo = null;
  const used = new Set();
  for (const f of found) {
    let best = -1;
    for (let i = 0; i < ranges.length; i++) {
      if (used.has(i)) continue;
      const r = ranges[i];
      const prevLast = i === 0 ? -1 : ranges[i - 1].last;
      if (f.pos > prevLast && f.pos <= r.first + 8) {
        best = i;
        break;
      }
    }
    if (best < 0) {
      for (let i = 0; i < ranges.length; i++) {
        if (used.has(i)) continue;
        if (f.pos >= ranges[i].from && f.pos < ranges[i].to) { best = i; break; }
      }
    }
    if (best >= 0) {
      blocks[best].matchNo = f.no;
      used.add(best);
    }
  }
}

function fillMatchNoGaps(matches) {
  if (!matches || matches.length === 0) return;

  const parseNo = (s) => {
    const mm = (s || '').match(/^周([一二三四五六日])(\d{3})$/);
    return mm ? { week: mm[1], num: parseInt(mm[2], 10) } : null;
  };

  const seen = new Set();
  for (const m of matches) {
    if (!m.matchNo) continue;
    if (seen.has(m.matchNo)) m.matchNo = null;
    else seen.add(m.matchNo);
  }

  const known = matches.map(m => parseNo(m.matchNo)).filter(Boolean);
  if (known.length === 0) return;
  const week = known[0].week;
  if (!known.every(k => k.week === week)) return;

  let prev = null;
  let prevIdx = -1;
  for (let i = 0; i < matches.length; i++) {
    const cur = parseNo(matches[i].matchNo);
    if (!cur) continue;

    if (prev != null && prevIdx >= 0) {
      const slots = i - prevIdx - 1;
      const span = cur.num - prev - 1;
      if (slots > 0 && slots === span) {
        for (let k = 1; k <= slots; k++) {
          const num = prev + k;
          matches[prevIdx + k].matchNo = '周' + week + String(num).padStart(3, '0');
        }
      }
    }
    prev = cur.num;
    prevIdx = i;
  }
}

function parseOneMatch(no, segText) {
  const item = {
    matchNo: no,
    home: null,
    away: null,
    option1: null,
    option2: null,
    odds1: null,
    odds2: null
  };

  const vs = segText.match(/([\u4e00-\u9fa5A-Za-z·]{2,12})VS([\u4e00-\u9fa5A-Za-z·]{2,12})/);
  if (vs) {
    item.home = cleanTeam(vs[1]);
    item.away = cleanTeam(vs[2]);
  } else {
    const noise = new Set([
      '全场比分', '半全场', '让球胜平负', '总进球', '胜平负', '投注内容', '竞彩足球',
      '已出票', '方案金额', '方案状态', '税后奖金', '订单详情', '实体票样', '自购方案',
      '自购方案详情', '预计中奖', '预计中奖金额', '点击下图', '预览可保存', '可保存图片'
    ]);
    const names = [];
    const nameRe = /[\u4e00-\u9fa5]{2,8}/g;
    let nm;
    while ((nm = nameRe.exec(segText)) !== null) {
      const n = nm[0];
      if (noise.has(n)) continue;
      if (/^[一二三四五六日天零]+$/.test(n)) continue;
      if (n.includes('比分') || n.includes('胜平') || n.includes('过关')) continue;
      if (!names.includes(n)) names.push(n);
    }
    if (names.length >= 1) item.home = names[0];
    if (names.length >= 2) item.away = names[1];
  }

  const options = [];
  const odds = [];
  const isCleanOpt = (s) => isCleanScore(s) || /^(让?[胜平负]|[胜平负]\/[胜平负])$/.test(s);

  const pairedRe = /(\d{1,2}:\d{1,2}|让[胜平负]|[胜平负]\/[胜平负]|[胜平负])\(([^)]+)\)/g;
  let pm;
  while ((pm = pairedRe.exec(segText)) !== null) {
    const opt = pm[1];
    const inside = pm[2];
    if (isCleanScore(inside)) {
      if (isCleanOpt(opt)) pushUnique(options, opt);
      pushUnique(options, inside);
    } else {
      if (isCleanOpt(opt)) pushUnique(options, opt);
      const od = parseFloat(inside);
      if (looksLikeOdds(od)) pushUnique(odds, od);
    }
  }

  const hf = segText.match(/(胜|平|负)\/(胜|平|负)/g);
  if (hf) for (const s of hf) pushUnique(options, s);

  const playHit = segText.search(/全场比分|半全场|让球胜平负|总进球/);
  const scoreRe = /\d{1,2}:\d{1,2}/g;
  let sm;
  while ((sm = scoreRe.exec(segText)) !== null) {
    if (!isCleanScore(sm[0])) continue;
    if (playHit >= 0 && Math.abs(sm.index - playHit) > 48 && options.length >= 2) continue;
    pushUnique(options, sm[0]);
  }

  if (item.home && /^周/.test(item.home)) item.home = null;
  if (item.away && /^周/.test(item.away)) item.away = null;

  const oddsRe = /\((\d+(?:\.\d+)?)\)/g;
  let om;
  while ((om = oddsRe.exec(segText)) !== null) {
    const od = parseFloat(om[1]);
    if (looksLikeOdds(od)) pushUnique(odds, od);
  }

  if (options.length >= 1) item.option1 = options[0];
  if (options.length >= 2) item.option2 = options[1];
  if (odds.length >= 1) item.odds1 = odds[0];
  if (odds.length >= 2) item.odds2 = odds[1];

  if (!item.option1) {
    const goals = segText.match(/(?:总进球|进球)[^\d]{0,6}([0-7])(?!\d)/);
    if (goals) item.option1 = goals[1];
  }

  if (!item.option1 && !item.option2 && !item.home && !item.away && !item.matchNo) return null;
  return item;
}

function pushUnique(arr, v) {
  if (v == null || v === '') return;
  if (!arr.includes(v)) arr.push(v);
}

module.exports = { parseTicket, normalizeOcr };
