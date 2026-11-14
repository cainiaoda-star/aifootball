/**
 * 数字彩对奖展示（前后端共用约定）。
 * 禁止用 note 是否含「中」判断中奖；必须以 status / showAsWin。
 * Node 与浏览器均可加载本文件做测试。
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.LottoHitUi = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  function showAsWin(ticket) {
    if (!ticket) return false;
    return ticket.showAsWin === true || ticket.status === 'won';
  }

  function statusLabel(ticket) {
    if (!ticket) return '';
    var s = ticket.status;
    var note = ticket.note || '';
    if (s === 'pending') return '未开奖';
    if (s === 'won') return note ? ('中奖 · ' + note) : '中奖';
    if (s === 'lost') return note ? ('未中 · ' + note) : '未中';
    if (s === 'illegal') return note ? ('非法投注 · ' + note) : '非法投注';
    if (s === 'undetermined') return note ? ('无法判定 · ' + note) : '无法判定';
    if (s === 'prize_undetermined') return note ? ('奖级未判定 · ' + note) : '奖级未判定';
    return note;
  }

  /** 旧错误逻辑（仅用于反例测试，业务禁止调用） */
  function legacyNoteImpliesWin(note) {
    note = note || '';
    return note.indexOf('中 0') < 0 && /中/.test(note);
  }

  /**
   * 页面保存时按彩种提交的玩法（须与服务端契约一致）。
   * 快乐8 必须是「选十」，禁止写成「单式」。
   * 本轮用户票只支持可单注计价的玩法；复式/胆拖由服务端拒绝。
   */
  function playTypeForSave(game) {
    var g = (game || '').toLowerCase();
    if (g === 'kl8') return '选十';
    if (g === 'fc3d' || g === 'pl3' || g === 'pl5') return '直选';
    return '单式';
  }

  return {
    showAsWin: showAsWin,
    statusLabel: statusLabel,
    legacyNoteImpliesWin: legacyNoteImpliesWin,
    playTypeForSave: playTypeForSave
  };
});
