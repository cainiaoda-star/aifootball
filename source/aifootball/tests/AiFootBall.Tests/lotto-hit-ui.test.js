/**
 * 直接加载 wwwroot/lotto-hit-ui.js（与页面同一文件），禁止在 C# 里复制假 UI 逻辑。
 * 运行：node lotto-hit-ui.test.js
 */
'use strict';
var path = require('path');
var ui = require(path.join(__dirname, '../../src/AiFootBall.Api/wwwroot/lotto-hit-ui.js'));
var fails = 0;

function assert(cond, msg) {
  if (!cond) {
    fails++;
    console.error('FAIL:', msg);
  } else {
    console.log('ok:', msg);
  }
}

assert(ui.showAsWin({ status: 'won', showAsWin: true, note: '组选三中' }) === true, 'won shows win');
assert(ui.showAsWin({ status: 'lost', showAsWin: false, note: '组选三未中' }) === false, 'lost note 含中 不中奖');
assert(ui.showAsWin({ status: 'prize_undetermined', showAsWin: false, note: '红球命中 6/6（奖级未判定）' }) === false, 'prize_undetermined 不中奖');
assert(ui.showAsWin({ status: 'illegal', note: '不计中' }) === false, 'illegal 不中奖');
assert(ui.legacyNoteImpliesWin('组选三未中') === true, 'legacy 会误判（对照）');
assert(ui.statusLabel({ status: 'prize_undetermined', note: '红球命中 4/6（奖级未判定）' }).indexOf('奖级未判定') >= 0, 'label prize_undetermined');
assert(ui.statusLabel({ status: 'lost', note: '组选三未中' }).indexOf('未中') === 0, 'label lost');

assert(ui.playTypeForSave('kl8') === '选十', 'KL8 save playType 选十');
assert(ui.playTypeForSave('ssq') === '单式', 'SSQ save playType 单式');
assert(ui.playTypeForSave('dlt') === '单式', 'DLT save playType 单式');
assert(ui.playTypeForSave('fc3d') === '直选', 'FC3D save playType 直选');
assert(ui.playTypeForSave('pl3') === '直选', 'PL3 save playType 直选');
assert(ui.playTypeForSave('pl5') === '直选', 'PL5 save playType 直选');

if (fails) {
  console.error('lotto-hit-ui.test.js failed:', fails);
  process.exit(1);
}
console.log('lotto-hit-ui.test.js passed');
