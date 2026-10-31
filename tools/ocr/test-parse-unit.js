const { parseTicket } = require('./parse-ticket.js');
const assert = (c, m) => { if (!c) { console.error('FAIL', m); process.exitCode = 1; } else console.log('OK', m); };

const t1 = parseTicket(`自购方案详情 竞彩足球 96.0元 已出票
投注信息 4场 3串1 1倍
周四003 弗洛西诺 VS 莱切 全场比分 2:0(11.00)
周四004 哈马比 VS 天狼星 全场比分 2:0(1:0) (9.50)
周四005 埃弗顿 VS 纽卡斯尔 全场比分 3:1(2:0) (14.00)
预计中奖金额：32598.0元`);
assert(t1.passType === '3串1', 'passType 3串1');
assert(t1.expectedWin === 32598, 'expectedWin');
assert(t1.matches.length === 3, '3 matches');
assert(t1.matches[1].option1 === '2:0' && t1.matches[1].option2 === '1:0', 'score pair');
assert(t1.matches[0].home === '弗洛西诺', 'home from VS');

const t2 = parseTicket(`混合过关 半全场 周四002 热那亚 VS 科莫 平/胜(4.20) 周四003 罗马 VS 卡利亚里 胜/胜(3.10) 2串1#3串1`);
assert(t2.passType === '2串1#3串1', 'combo pass');
assert(t2.matches[0].option1 === '平/胜', 'half-full');
assert(t2.playType === '半全场' || t2.playType === '混合过关', 'play type');

console.log(process.exitCode ? 'DONE WITH FAILURES' : 'ALL PASSED');
