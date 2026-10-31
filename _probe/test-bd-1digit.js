const { parseTicket } = require('../tools/ocr/parse-ticket.js');

const clean = `体彩 单场竞猜
过关方式 9串1 10串1 11串1 1倍 合计 134元
1.布里斯班狮吼 3
4.科特布斯 3
5.圣珀尔滕 3
8.尼斯 3
9.博德闪耀 3
17.格勒诺布尔 1
53.普埃布拉 0
66.德岛漩涡 1
214.维拉诺瓦 1
221.拉普拉塔大学生 1
389.图库曼竞技 1
`;

const dirty = `体彩 让了7和
过关方式 是1 10曲1 11康1 1傅 合计 13人元
1.布时斯克基吼 “3 =-
4 .科特布其     3=-
5 圣珀尔且     3- -0
8 尼基          3 --
二9.博德闪内     3 -Ci
17.格勒诺布尔    -1.-
53.昔埃布拉       0
的.德久油     和
214 维拉诺也     人
2.拉拉增大学生1
389 .图库曼苋技    加证
`;

for (const [name, text] of [['clean', clean], ['dirty-ocr', dirty]]) {
  const r = parseTicket(text, { kind: 'bd-spf' });
  console.log('---', name, 'matches=', r.matches.length, 'pass=', r.passType, 'amt=', r.betAmount);
  console.log(r.matches.map(m => `${m.matchNo}:${m.home}:${m.option1}`).join(' | '));
}
