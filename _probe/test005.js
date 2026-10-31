const { parseTicket } = require('../tools/ocr/parse-ticket.js');
const raw = `第1场 周四001
主队:上海惠芒 W 客队; 叮国泽维亚
10:2)88.000元+(1:2)66.750元
第2场 周四003
主队;弗洛丁庄内 Ws 客队:沫切
(2:0)810.00元+(3:1)@11 ,00元
第3场，周四004
主队:栓马比 Vs 客队;天狼星
| (人2.0)时.700元+(3:1)的,500元
| 。 第4场 局四005
主了;挨弗顿 WS 客队:纽卡斯尔联
| (2:0)@1丰50元+(3:])@14,00元
| 。 未村最高可能固定奖金;32,598.00元
单倍注数: 3xlk32注,4x1#16注;共48注`;
const j = parseTicket(raw, { kind: 'physical' });
console.log({ pass: j.passType, amt: j.betAmount, exp: j.expectedWin, play: j.playType });
for (const m of j.matches) {
  console.log(m.matchNo, m.option1, m.option2, m.odds1, m.odds2);
}
const m5 = j.matches.find(x => x.matchNo === '周四005');
if (!m5 || m5.option1 !== '2:0' || m5.option2 !== '3:1' || Number(m5.odds1) !== 11.5 || Number(m5.odds2) !== 14) {
  console.error('FAIL 005', m5);
  process.exit(1);
}
console.log('PASS 005');
