const { normalizeOcr } = require('../tools/ocr/parse-ticket.js');
let flat = normalizeOcr(require('./ocr_r11.json').text).replace(/\s+/g, '');
flat = flat.replace(/(\d):(\d)\d(\d):(\d)/g, '$1:$2($3:$4)');
const play = '全场比分';
const playPos = [];
let p = 0;
while ((p = flat.indexOf(play, p)) >= 0) { playPos.push(p); p += play.length; }
console.log('playPos', playPos);
console.log('nos', [...flat.matchAll(/周[一二三四五六日天]\d{3}/g)].map(m => ({ no: m[0], pos: m.index })));
const startBound = Math.max(0, flat.indexOf('投注内容'));
const endBound = flat.search(/预计中奖|预计中交|实体票样/);
const per = 2;
const groups = [];
for (let i = 0; i < playPos.length; i += per) {
  groups.push({ first: playPos[i], last: playPos[Math.min(i + per - 1, playPos.length - 1)] + play.length });
}
const ranges = [];
for (let g = 0; g < groups.length; g++) {
  const prevLast = g === 0 ? startBound : groups[g - 1].last;
  const nextFirst = g + 1 < groups.length ? groups[g + 1].first : endBound;
  let from = g === 0 ? startBound : Math.floor((prevLast + groups[g].first) / 2);
  let to = g + 1 < groups.length ? Math.floor((groups[g].last + nextFirst) / 2) : endBound;
  from = Math.min(from, Math.max(startBound, groups[g].first - 28));
  from = Math.max(startBound, from);
  to = Math.max(to, groups[g].last + 24);
  to = Math.min(endBound, to);
  if (g > 0) from = Math.max(from, ranges[g - 1].to);
  ranges.push({ from, to });
  const slice = flat.slice(from, to);
  console.log('block', g, { from, to }, 'len', to - from);
  console.log('  nos', [...slice.matchAll(/周[一二三四五六日天]\d{3}/g)].map(x => x[0]));
  console.log('  text', slice.slice(0, 80));
}
