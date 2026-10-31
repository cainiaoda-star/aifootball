const { parseTicket } = require('../tools/ocr/parse-ticket.js');

const spfRaw = `体彩 0977
过关方式 6囊1    1倍 合计 1458元
363.里奥阿维       310
370, 阿尔克马尔    310
374.阿根廷独立      310
379.阿特拉斯       和
981.科尔多玖中磷 31 0
383,克雷塔罗        310`;

const j = parseTicket(spfRaw, { kind: 'bd-spf' });
console.log({ board: j.board, play: j.playType, pass: j.passType, amt: j.betAmount, mult: j.multiple });
for (const m of j.matches) console.log(m.matchNo, m.home, m.option1, m.odds1);

const scoreRaw = `体彩C9TTE
过关方式 2?串1 3蝇1 1倍。。 合计 70元
( 57.里尔)
--- -二平其它
(61.理人大尼村)
( 63.葡区牙体育)
(64.阿斯顿准拉)`;
const s = parseTicket(scoreRaw, { kind: 'bd-score' });
console.log({ board: s.board, play: s.playType, pass: s.passType, amt: s.betAmount });
for (const m of s.matches) console.log(m.matchNo, m.home, m.option1, m.option2);

if (j.matches.length !== 6) { console.error('FAIL spf count'); process.exit(1); }
if (j.matches[0].option1 !== '3,1,0') { console.error('FAIL opts', j.matches[0]); process.exit(1); }
if (j.matches[4].matchNo !== '381') { console.error('FAIL 381', j.matches[4]); process.exit(1); }
if (j.passType !== '6串1') { console.error('FAIL pass', j.passType); process.exit(1); }
if (s.matches.length < 4) { console.error('FAIL score count'); process.exit(1); }
console.log('ALL OK');
