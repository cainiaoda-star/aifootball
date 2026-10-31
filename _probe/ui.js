
const $ = s => document.querySelector(s);
const fmt = n => n == null ? "-" : Number(n).toLocaleString("zh-CN");

let current = null;

async function getJSON(url){ const r = await fetch(url); if(!r.ok) throw new Error(url); return r.json(); }

function ball(v, type){
  const cls = type === "B" ? "b" : (type === "D" ? "d" : "r");
  return `<span class="ball ${cls}">${String(v).padStart(2,"0")}</span>`;
}

async function init(){
  const overview = await getJSON("/api/lottery/overview");
  const fb = await getJSON("/api/football/overview");

  const totalDraws = overview.reduce((s,x)=>s+(x.drawCount||0),0);
  $("#kpis").innerHTML = `
    <div class="kpi"><div class="label">彩种数量</div><div class="value">${overview.length}<small>个</small></div></div>
    <div class="kpi"><div class="label">累计开奖期数</div><div class="value">${fmt(totalDraws)}<small>期</small></div></div>
    <div class="kpi"><div class="label">足球赛事</div><div class="value">${fmt(fb.matchCount)}<small>场</small></div></div>
    <div class="kpi"><div class="label">完场 / 联赛 / 球队</div><div class="value">${fmt(fb.finishedCount)}<small>${fb.leagueCount} / ${fb.teamCount}</small></div></div>`;

  $("#tabs").innerHTML = overview.map(o =>
    `<div class="tab" data-code="${o.code}">${o.name}</div>`).join("");
  $("#tabs").querySelectorAll(".tab").forEach(t =>
    t.onclick = () => selectLottery(t.dataset.code));

  if (overview.length) selectLottery(overview[0].code);
  loadFootball();
  loadOdds();
  loadTrajectory();
  $("#oddsBtn").onclick = () => loadOddsDetail($("#oddsFid").value.trim());
  $("#oddsFid").onkeydown = e => { if(e.key === "Enter") loadOddsDetail($("#oddsFid").value.trim()); };
  $("#trajBtn").onclick = () => loadTrajectoryDetail($("#trajFid").value.trim());
  $("#trajFid").onkeydown = e => { if(e.key === "Enter") loadTrajectoryDetail($("#trajFid").value.trim()); };
}

async function selectLottery(code){
  current = code;
  document.querySelectorAll(".tab").forEach(t => t.classList.toggle("active", t.dataset.code === code));

  const meta = (await getJSON("/api/lottery/types")).find(x => x.code === code);
  const draws = await getJSON(`/api/lottery/${code}/draws?take=10`);
  const isDigit = meta && meta.redMax <= 9;
  const typeOf = (i, d) => {
    if (isDigit) return "D";
    return (meta && i >= meta.redCount) ? "B" : "R";
  };

  $("#drawTable").querySelector("tbody").innerHTML = draws.map(d => {
    const balls = d.reds.concat(d.blues).map((v,i)=>ball(v, isDigit ? "D" : (i >= d.reds.length ? "B" : "R"))).join("");
    return `<tr><td>${d.issue}</td><td>${d.drawDate}</td><td>${balls}</td><td>${d.reds.reduce((a,b)=>a+b,0)}</td><td>${fmt(d.salesAmount)}</td></tr>`;
  }).join("");

  const ana = await getJSON(`/api/lottery/${code}/analysis?take=200`);
  $("#sampleN").textContent = ana.sampleSize;
  renderSums(ana.sum);
  renderShape(ana);

  const freq = await getJSON(`/api/lottery/${code}/frequency?ball=R&take=500`);
  renderFreq(freq.numbers);
}

function renderSums(sum){
  const entries = Object.entries(sum.distribution).map(([k,v])=>[+k,v]);
  const max = Math.max(...entries.map(e=>e[1]));
  $("#sumBars").innerHTML = entries.map(([k,v]) =>
    `<div style="height:${Math.round(v/max*100)}%" title="和值 ${k}：${v} 次"></div>`).join("");
  $("#sumMin").textContent = entries.length ? `最小 ${entries[0][0]}` : "";
  $("#sumMax").textContent = entries.length ? `最大 ${entries[entries.length-1][0]}` : "";
}

function renderShape(ana){
  const oe = Object.entries(ana.oddEven).slice(0,4)
    .map(([k,v])=>`<span class="chip">奇:偶 ${k} · ${v} 次</span>`).join("");
  $("#shapeInfo").innerHTML = oe +
    `<span class="chip">连号出现率 ${ana.consecutiveRate}%</span>` +
    `<span class="chip">平均重号 ${ana.repeat.avg} 个</span>` +
    `<span class="chip">平均和值 ${ana.sum.avg}</span>`;
}

function renderFreq(nums){
  const maxCount = Math.max(...nums.map(n=>n.countLast100), 1);
  $("#freqTable").querySelector("tbody").innerHTML = nums.map(n => `
    <tr>
      <td>${ball(n.number,"R")}</td>
      <td>${n.totalCount}</td>
      <td>${n.countLast30}</td>
      <td>${n.countLast100}
        <div class="heat"><i style="width:${Math.round(n.countLast100/maxCount*100)}%"></i></div>
      </td>
      <td style="color:${n.currentOmission>15?'var(--hot)':n.currentOmission>8?'var(--warm)':'var(--dim)'}">${n.currentOmission}</td>
      <td>${n.maxOmission}</td>
    </tr>`).join("");

  $("#hotList").innerHTML = nums.slice().sort((a,b)=>b.countLast100-a.countLast100).slice(0,10)
    .map(n=>`<span class="chip hot">${String(n.number).padStart(2,"0")} · ${n.countLast100}次</span>`).join("");
  $("#coldList").innerHTML = nums.slice().sort((a,b)=>b.currentOmission-a.currentOmission).slice(0,10)
    .map(n=>`<span class="chip cold">${String(n.number).padStart(2,"0")} · 遗漏${n.currentOmission}</span>`).join("");
}

async function loadFootball(){
  const today = new Date();
  const to = today.toISOString().slice(0,10);
  const from = new Date(today.getTime()-7*864e5).toISOString().slice(0,10);
  const rows = await getJSON(`/api/football/league-stats?from=${from}&to=${to}`);
  const top = rows.slice(0, 15);
  $("#leagueTable").querySelector("tbody").innerHTML = top.map(r => `
    <tr>
      <td>${r.leagueName}</td><td>${r.matches}</td>
      <td style="color:var(--hot)">${r.homeWins}</td><td>${r.draws}</td>
      <td style="color:var(--cold)">${r.awayWins}</td>
      <td>${r.avgGoals}</td><td>${r.over25Rate}%</td>
    </tr>`).join("") || `<tr><td colspan="7" class="empty">暂无数据</td></tr>`;

  let h=0,d=0,a=0,m=0,g=0;
  rows.forEach(r=>{h+=r.homeWins;d+=r.draws;a+=r.awayWins;m+=r.matches;g+=r.avgGoals*r.matches;});
  const pct = x => m? (x/m*100).toFixed(1) : 0;
  $("#spfBox").innerHTML = `
    <div class="stack">
      <i style="width:${pct(h)}%;background:#ff6b6b"></i>
      <i style="width:${pct(d)}%;background:#8b949e"></i>
      <i style="width:${pct(a)}%;background:#3fb950"></i>
    </div>
    <div class="legend">
      <span><b style="background:#ff6b6b"></b>主胜 ${pct(h)}%</span>
      <span><b style="background:#8b949e"></b>平 ${pct(d)}%</span>
      <span><b style="background:#3fb950"></b>客胜 ${pct(a)}%</span>
      <span>合计 ${m} 场 · 场均 ${m?(g/m).toFixed(2):0} 球</span>
    </div>`;

  const overview = await getJSON("/api/football/overview");
  $("#goalBox").innerHTML = `
    <span class="chip">赛事 ${fmt(overview.matchCount)} 场</span>
    <span class="chip">完场 ${fmt(overview.finishedCount)} 场</span>
    <span class="chip">联赛 ${fmt(overview.leagueCount)} 个</span>
    <span class="chip">球队 ${fmt(overview.teamCount)} 支</span>
    <span class="chip">数据范围 ${overview.firstDate} ~ ${overview.lastDate}</span>`;
}

/* ---------------- 赔率 ---------------- */

const fmtOdds = v => v == null ? "-" : Number(v).toFixed(2);

function hcpText(v){
  if(v == null) return "-";
  const names = {0:"平手",0.25:"平/半",0.5:"半球",0.75:"半/一",1:"一球",1.25:"一/球半",
    1.5:"球半",1.75:"球半/两",2:"两球",2.25:"两/球半",2.5:"两球半",2.75:"两半/三",
    3:"三球",3.25:"三/三半",3.5:"三球半",4:"四球",5:"五球"};
  const a = Math.abs(v);
  const base = names[a] || a.toString();
  return v > 0 ? "受" + base : base;
}

function delta(open, close){
  if(open == null || close == null) return "";
  const d = close - open;
  if(Math.abs(d) < 0.001) return `<span class="sub">持平</span>`;
  return `<span class="${d>0?'up':'down'}">${d>0?'+':''}${d.toFixed(2)}</span>`;
}

function groupOdds(rows){
  const map = new Map();
  rows.forEach(r => {
    const o = map.get(r.companyId) || { companyId:r.companyId, company:r.company, average:r.companyId===0 };
    if(r.isOpening) o.open = r; else o.close = r;
    map.set(r.companyId, o);
  });
  return [...map.values()].sort((a,b)=> (a.average?-1:b.average?1:a.companyId-b.companyId));
}

function euTable(rows, limit){
  const list = groupOdds(rows).slice(0, limit || 15);
  if(!list.length) return `<div class="empty">无欧赔数据</div>`;
  return `<table><thead><tr><th>公司</th><th>初盘 主 / 平 / 客</th><th>终盘 主 / 平 / 客</th>
      <th>主胜变化</th><th>返还率</th></tr></thead><tbody>` + list.map(o => {
    const p = o.open || {}, c = o.close || {};
    return `<tr>
      <td>${o.average ? '<b>平均值</b>' : o.company}</td>
      <td>${fmtOdds(p.homeWin)} / ${fmtOdds(p.draw)} / ${fmtOdds(p.awayWin)}</td>
      <td>${fmtOdds(c.homeWin)} / ${fmtOdds(c.draw)} / ${fmtOdds(c.awayWin)}</td>
      <td>${delta(p.homeWin, c.homeWin)}</td>
      <td>${c.returnRate ?? p.returnRate ?? "-"}</td></tr>`;
  }).join("") + `</tbody></table>`;
}

function ahTable(rows, limit){
  const list = groupOdds(rows).slice(0, limit || 12);
  if(!list.length) return `<div class="empty">无亚盘数据</div>`;
  return `<table><thead><tr><th>公司</th><th>初盘 主水 / 盘口 / 客水</th><th>终盘 主水 / 盘口 / 客水</th>
      <th>盘口变化</th></tr></thead><tbody>` + list.map(o => {
    const p = o.open || {}, c = o.close || {};
    return `<tr>
      <td>${o.average ? '<b>平均值</b>' : o.company}</td>
      <td>${fmtOdds(p.homeWater)} / ${hcpText(p.handicap)} / ${fmtOdds(p.awayWater)}</td>
      <td>${fmtOdds(c.homeWater)} / ${hcpText(c.handicap)} / ${fmtOdds(c.awayWater)}</td>
      <td>${delta(p.handicap, c.handicap)}</td></tr>`;
  }).join("") + `</tbody></table>`;
}

function ouTable(rows, limit){
  const list = groupOdds(rows).slice(0, limit || 12);
  if(!list.length) return `<div class="empty">无大小球数据</div>`;
  return `<table><thead><tr><th>公司</th><th>初盘 大 / 盘口 / 小</th><th>终盘 大 / 盘口 / 小</th>
      <th>盘口变化</th></tr></thead><tbody>` + list.map(o => {
    const p = o.open || {}, c = o.close || {};
    return `<tr>
      <td>${o.average ? '<b>平均值</b>' : o.company}</td>
      <td>${fmtOdds(p.overWater)} / ${fmtOdds(p.totalLine)} / ${fmtOdds(p.underWater)}</td>
      <td>${fmtOdds(c.overWater)} / ${fmtOdds(c.totalLine)} / ${fmtOdds(c.underWater)}</td>
      <td>${delta(p.totalLine, c.totalLine)}</td></tr>`;
  }).join("") + `</tbody></table>`;
}

async function loadOdds(){
  const [cov, mv, signal, jobs] = await Promise.all([
    getJSON("/api/football/odds-coverage"),
    getJSON("/api/football/odds-movement"),
    getJSON("/api/football/odds-signal"),
    getJSON("/api/jobs")
  ]);

  const pct = cov.matchTotal ? (cov.withOdds / cov.matchTotal * 100).toFixed(1) : 0;
  $("#oddsCoverage").innerHTML = `
    <div class="stack"><i style="width:${pct}%;background:linear-gradient(90deg,#4c9aff,#8b5cf6)"></i></div>
    <div class="legend">
      <span>已覆盖 <b style="background:#4c9aff"></b>${fmt(cov.withOdds)} / ${fmt(cov.matchTotal)} 场（${pct}%）</span>
      <span>待采集 ${fmt(cov.unchecked)}</span>
      <span>无赔率 ${fmt(cov.noOdds)}</span>
      <span>失败 ${fmt(cov.failed)}</span>
    </div>
    <div class="legend">
      <span>赔率 <b style="background:#8b5cf6"></b>${fmt(cov.oddsRows)} 行</span>
      <span>欧赔 ${fmt(cov.euRows)}</span>
      <span>亚盘 ${fmt(cov.ahRows)}</span>
      <span>大小球 ${fmt(cov.ouRows)}</span>
      <span>公司 ${fmt(cov.companies)} 家</span>
      <span>完场且有赔率 ${fmt(cov.finishedWithOdds)} 场</span>
    </div>`;

  const total = cov.matchTotal || 1;
  $("#oddsPendingBars").innerHTML = `
    <div style="height:${Math.round(cov.withOdds/total*100)}%;background:linear-gradient(180deg,#4c9aff,#2f5fa8);flex:1;border-radius:3px 3px 0 0" title="已采集 ${cov.withOdds}"></div>
    <div style="height:${Math.round(cov.unchecked/total*100)}%;background:#2c3743;flex:1;border-radius:3px 3px 0 0" title="待采集 ${cov.unchecked}"></div>
    <div style="height:${Math.round(cov.noOdds/total*100)}%;background:#3a3320;flex:1;border-radius:3px 3px 0 0" title="无赔率 ${cov.noOdds}"></div>
    <div style="height:${Math.round(cov.failed/total*100)}%;background:#3a2020;flex:1;border-radius:3px 3px 0 0" title="失败 ${cov.failed}"></div>`;
  $("#oddsPendingLegend").innerHTML = `
    <span><b style="background:#4c9aff"></b>已采集</span>
    <span><b style="background:#2c3743"></b>待采集</span>
    <span><b style="background:#3a3320"></b>无赔率</span>
    <span><b style="background:#3a2020"></b>失败</span>`;

  $("#signalTable").querySelector("tbody").innerHTML = (signal.eu || []).map(r => `
    <tr><td>${r.direction}</td><td>${fmt(r.matches)}</td>
      <td style="color:var(--hot)">${r.homeWinPct}%</td><td>${r.drawPct}%</td>
      <td style="color:var(--cold)">${r.awayWinPct}%</td></tr>`).join("")
    || `<tr><td colspan="5" class="empty">暂无完场且有赔率的赛事</td></tr>`;

  $("#signalAh").innerHTML = (signal.ah || []).map(r =>
    `<span class="chip">${r.direction} · ${r.matches} 场 · 让球赢盘 ${r.homeCoverPct}%</span>`).join("")
    || `<span class="sub">暂无数据</span>`;

  $("#jobTable").querySelector("tbody").innerHTML = jobs.map(j => `
    <tr>
      <td>${j.name}</td>
      <td><code style="color:#a8ccff">${j.cron}</code></td>
      <td>${j.type}</td>
      <td><span class="tag ${j.enabled?'on':'off'}">${j.enabled?'启用':'停用'}</span></td>
      <td>${j.nextRun ? new Date(j.nextRun).toLocaleString("zh-CN") : "-"}</td>
      <td>${j.lastMessage ? `${j.lastStatus||''} · ${j.lastMessage}` : '<span class="sub">尚未运行</span>'}</td>
    </tr>`).join("") || `<tr><td colspan="6" class="empty">未配置作业</td></tr>`;

  // 变化幅度概览
  const eu = mv.find(x => x.oddsType === "EU");
  if(eu) $("#oddsHint").innerHTML =
    `欧赔样本 ${fmt(eu.samples)} · 主胜赔率发生变动 ${eu.homeChangedPct}% · 平均变动 ${eu.avgHomeDelta}`;
}

async function loadOddsDetail(fid){
  const el = $("#oddsResult");
  if(!fid) return;
  el.innerHTML = `<div class="empty">加载中…</div>`;
  try {
    const d = await getJSON(`/api/football/odds/${fid}`);
    const m = d.match || {};
    el.innerHTML = `
      <div class="legend" style="margin:12px 0">
        <span><b style="background:#4c9aff"></b>${m.leagueName || ""} ${m.matchTime || ""}</span>
        <span>${m.homeTeamName || ""} ${m.homeScore ?? ""} : ${m.awayScore ?? ""} ${m.awayTeamName || ""}</span>
        <span>公司 ${d.companyCount} 家</span>
      </div>
      <div class="od-title">欧赔</div>${euTable(d.eu, 15)}
      <div class="od-title">亚盘（负值=主队让球）</div>${ahTable(d.ah, 12)}
      <div class="od-title">大小球</div>${ouTable(d.ou, 12)}`;
  } catch(e) {
    el.innerHTML = `<div class="empty">赛事 ${fid} 暂无赔率数据（可能未被 500.com 收录）</div>`;
  }
}

async function loadTrajectory(){
  let cov = null, ts = null, es = null;
  try { cov = await getJSON("/api/football/exchange-coverage"); } catch(e) {}
  try { ts = await getJSON("/api/football/trajectory-stats"); } catch(e) {}
  try { es = await getJSON("/api/football/exchange-stats"); } catch(e) {}

  if (cov) {
    const pct = cov.matchTotal ? ((cov.withExchange||0) / cov.matchTotal * 100).toFixed(1) : 0;
    $("#txCoverage").innerHTML = `
      <div class="stack"><i style="width:${pct}%;background:linear-gradient(90deg,#f5a623,#e2564a)"></i></div>
      <div class="legend">
        <span>必发有数据 <b style="background:#e2564a"></b>${fmt(cov.withExchange)} / ${fmt(cov.matchTotal)} 场（${pct}%）</span>
        <span>无盘口 ${fmt(cov.noExchange)}</span>
        <span>失败 ${fmt(cov.failed)}</span>
        <span>待检查 ${fmt(cov.unchecked)}</span>
      </div>
      <div class="legend">
        <span>必发快照 ${fmt(cov.snapshots)} 条</span>
        <span>大额明细 ${fmt(cov.trades)} 条</span>
        <span>赔率轨迹点 ${fmt(cov.oddsHistoryPoints)} 条 · 覆盖 ${fmt(cov.oddsHistoryMatches)} 场</span>
      </div>`;
  }

  const rows = (ts && ts.drift) || [];
  $("#trajTable").querySelector("tbody").innerHTML = rows.map(r => {
    const label = String(r.bucket).replace(/^\d_/, "").replace(/_/g, " ");
    return `<tr><td>${label}</td><td>${fmt(r.matches)}</td>
      <td style="color:var(--hot)">${r.homeWinPct}%</td><td>${r.drawPct}%</td>
      <td style="color:var(--cold)">${r.awayWinPct}%</td>
      <td>${r.avgHomeDelta > 0 ? "+" : ""}${r.avgHomeDelta}</td></tr>`;
  }).join("") || `<tr><td colspan="6" class="empty">尚无足量轨迹样本（需赛前多轮采样后积累）</td></tr>`;

  const sm = ts && ts.summary;
  $("#trajHint").innerHTML = sm
    ? `累计 ${fmt(sm.matches)} 场有轨迹，其中 ${fmt(sm.lateSampled)} 场在赛前 90 分钟内仍有采样`
    : "";

  const tb = (el, rows, cols) => {
    $(el).querySelector("tbody").innerHTML = (rows||[]).map(r =>
      `<tr>${cols.map(c => `<td${c.style?` style="${c.style(r)}"`:""}>${c.fmt?c.fmt(r):fmt(r[c.key])}</td>`).join("")}</tr>`
    ).join("") || `<tr><td colspan="${cols.length}" class="empty">暂无完场样本</td></tr>`;
  };

  tb("#exRatioTable", es && es.ratio, [
    {key:"bucket"},
    {key:"matches"},
    {key:"homeWinPct", style:()=>"color:var(--hot)", fmt:r=>r.homeWinPct+"%"},
    {key:"drawPct", fmt:r=>r.drawPct+"%"},
    {key:"awayWinPct", style:()=>"color:var(--cold)", fmt:r=>r.awayWinPct+"%"},
    {key:"avgVolume", fmt:r=>fmt(r.avgVolume)}
  ]);

  tb("#exPnlTable", es && es.pnl, [
    {key:"bucket"},
    {key:"matches"},
    {key:"homeWinPct", style:()=>"color:var(--hot)", fmt:r=>r.homeWinPct+"%"},
    {key:"awayWinPct", style:()=>"color:var(--cold)", fmt:r=>r.awayWinPct+"%"}
  ]);

  tb("#exVolTable", es && es.volume, [
    {key:"bucket"},
    {key:"matches"},
    {key:"homeWinPct", style:()=>"color:var(--hot)", fmt:r=>r.homeWinPct+"%"},
    {key:"drawPct", fmt:r=>r.drawPct+"%"},
    {key:"awayWinPct", style:()=>"color:var(--cold)", fmt:r=>r.awayWinPct+"%"}
  ]);
}

/* 时间序列折线（纯 SVG，无外部依赖） */
function lineChart(series, w, h){
  const pts = series.flatMap(s => (s.data||[]).filter(d => d.v != null));
  if (!pts.length) return `<div class="empty">无轨迹数据</div>`;

  const t0 = Math.min(...pts.map(d => +new Date(d.t)));
  const t1 = Math.max(...pts.map(d => +new Date(d.t)));
  const v0 = Math.min(...pts.map(d => d.v));
  const v1 = Math.max(...pts.map(d => d.v));
  const padL = 34, padR = 10, padT = 10, padB = 20;
  const span = (v1 - v0) || 1;

  const X = t => t1 === t0 ? padL
    : padL + (+new Date(t) - t0) / (t1 - t0) * (w - padL - padR);
  const Y = v => padT + (1 - (v - v0) / span) * (h - padT - padB);

  const grid = [0, 0.5, 1].map(f => {
    const y = padT + f * (h - padT - padB);
    const val = (v1 - f * span).toFixed(2);
    return `<line x1="${padL}" y1="${y.toFixed(1)}" x2="${w-padR}" y2="${y.toFixed(1)}" stroke="#2c3743" stroke-width="1"/>
            <text x="2" y="${(y+3).toFixed(1)}" fill="#7d8794" font-size="10">${val}</text>`;
  }).join("");

  const paths = series.map(s => {
    const d = (s.data||[]).filter(x => x.v != null);
    if (d.length < 1) return "";
    const line = d.map((p,i) => `${i?"L":"M"}${X(p.t).toFixed(1)},${Y(p.v).toFixed(1)}`).join(" ");
    const dots = d.map(p =>
      `<circle cx="${X(p.t).toFixed(1)}" cy="${Y(p.v).toFixed(1)}" r="2.6" fill="${s.color}"/>`).join("");
    return `<path d="${line}" fill="none" stroke="${s.color}" stroke-width="1.8"/>${dots}`;
  }).join("");

  const legend = series.map(s =>
    `<span style="margin-right:12px;font-size:11px;color:${s.color}">● ${s.name}</span>`).join("");

  const tLabel = `<text x="${padL}" y="${h-5}" fill="#7d8794" font-size="10">${new Date(t0).toLocaleString("zh-CN",{month:"2-digit",day:"2-digit",hour:"2-digit",minute:"2-digit"})}</text>
    <text x="${w-padR}" y="${h-5}" fill="#7d8794" font-size="10" text-anchor="end">${new Date(t1).toLocaleString("zh-CN",{month:"2-digit",day:"2-digit",hour:"2-digit",minute:"2-digit"})}</text>`;

  return `<div style="margin:6px 0 2px">${legend}</div>
    <svg viewBox="0 0 ${w} ${h}" width="100%" height="${h}" style="background:#131920;border-radius:8px">
      ${grid}${paths}${tLabel}
    </svg>`;
}

async function loadTrajectoryDetail(fid){
  const el = $("#trajResult");
  if (!fid) return;
  el.innerHTML = `<div class="empty">加载中…</div>`;

  let traj = null, ex = null;
  try { traj = await getJSON(`/api/football/odds-trajectory/${fid}`); } catch(e) {}
  try { ex = await getJSON(`/api/football/exchange/${fid}`); } catch(e) {}

  if (!traj && !ex) {
    el.innerHTML = `<div class="empty">赛事 ${fid} 暂无轨迹与必发数据</div>`;
    return;
  }

  let html = "";

  if (traj && traj.groups && traj.groups.length) {
    const eu = traj.groups.filter(g => g.oddsType === "EU");
    if (eu.length) {
      const series = [
        { name: "主胜", color: "#f2564a", data: eu[0].points.map(p => ({t:p.capturedAt, v:p.homeWin})) },
        { name: "平局", color: "#8b93a1", data: eu[0].points.map(p => ({t:p.capturedAt, v:p.draw})) },
        { name: "客胜", color: "#35c07d", data: eu[0].points.map(p => ({t:p.capturedAt, v:p.awayWin})) }
      ];
      const g0 = eu[0];
      html += `<div class="muted" style="margin:14px 0 6px">
        欧赔轨迹（${g0.company}，${g0.pointCount} 个采样点，首点赛前 ${g0.firstMinutesToKick} 分钟，末点赛前 ${g0.lastMinutesToKick} 分钟）
        ${g0.homeDeltaPct != null ? ` · 主胜赔率累计 ${g0.homeDeltaPct > 0 ? "+" : ""}${g0.homeDeltaPct}%` : ""}
      </div>`;
      html += lineChart(series, 620, 150);

      html += `<table style="margin-top:10px"><thead><tr><th>采样时间</th><th>距开赛</th><th>主</th><th>平</th><th>客</th></tr></thead><tbody>` +
        g0.points.map(p => `<tr><td>${new Date(p.capturedAt).toLocaleString("zh-CN")}</td>
          <td>${p.minutesToKick == null ? "-" : p.minutesToKick + " 分"}</td>
          <td>${p.homeWin ?? "-"}</td><td>${p.draw ?? "-"}</td><td>${p.awayWin ?? "-"}</td></tr>`).join("") +
        `</tbody></table>`;
    }

    const ah = traj.groups.filter(g => g.oddsType === "AH" && g.lastHandicap != null);
    if (ah.length) {
      html += `<div class="muted" style="margin:14px 0 6px">亚盘轨迹</div>` +
        lineChart([{ name: "让球盘口", color: "#4c9aff",
          data: ah[0].points.map(p => ({t:p.capturedAt, v:p.handicap})) }], 620, 120);
    }
  }

  if (ex) {
    const snaps = ex.snapshots || [];
    if (snaps.length) {
      const s = snaps[snaps.length - 1];
      html += `<div class="muted" style="margin:16px 0 8px">必发成交（${snaps.length} 次快照，最新一次 ${new Date(s.capturedAt).toLocaleString("zh-CN")}）</div>`;
      html += `<div class="legend">
        <span>成交价 <b style="background:#f5a623"></b>${s.priceHome ?? "-"} / ${s.priceDraw ?? "-"} / ${s.priceAway ?? "-"}</span>
        <span>成交量 ${fmt(s.volumeHome)} / ${fmt(s.volumeDraw)} / ${fmt(s.volumeAway)}</span>
        <span>总成交 ${fmt(s.totalVolume)}</span>
        <span>交易占比 ${s.ratioHome ?? "-"}% / ${s.ratioDraw ?? "-"}% / ${s.ratioAway ?? "-"}%</span>
        <span>庄家盈亏 ${fmt(s.pnlHome)} / ${fmt(s.pnlDraw)} / ${fmt(s.pnlAway)}</span>
      </div>`;

      if (snaps.length > 1) {
        html += lineChart([{ name: "总成交额", color: "#e2564a",
          data: snaps.map(x => ({t: x.capturedAt, v: Number(x.totalVolume)})) }], 620, 120);
      }
    }

    const trades = ex.trades || [];
    if (trades.length) {
      html += `<div class="muted" style="margin:14px 0 6px">大额交易明细（最近 ${Math.min(trades.length, 15)} 笔）</div>
        <table><thead><tr><th>成交时间</th><th>方向</th><th>买/卖</th><th>金额</th><th>占比</th></tr></thead><tbody>` +
        trades.slice(0, 15).map(t => {
          const side = t.side === "H" ? "主" : t.side === "A" ? "客" : "平";
          return `<tr><td>${new Date(t.tradeTime).toLocaleString("zh-CN")}</td>
            <td>${side}</td><td>${t.direction}</td>
            <td>${fmt(t.volume)}</td><td>${t.ratio}%</td></tr>`;
        }).join("") + `</tbody></table>`;
    }
  }

  el.innerHTML = html || `<div class="empty">此场暂无可用数据</div>`;
}

setInterval(()=>{ $("#clock").textContent = new Date().toLocaleString("zh-CN"); }, 1000);
init().catch(e => document.querySelector("main").insertAdjacentHTML("beforeend",
  `<div class="empty">数据加载失败：${e.message}</div>`));
