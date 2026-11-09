# 空跑台账/告警闭环 巡检（automation）

## 2026-11-08 21:45 首跑
- FbPaperTrade 11-08 = 0 条，属**正常**：JobScheduler paper-freeze 有 `h>=23` 守卫（未到停售不冻今天），17:53 试跑补冻历史 2 天 15 注（11-06 6 / 11-07 9），cron 已改 */20 23,0,1,2,3，nextRun 23:00。**判据：23:00 前为 0 不算冻结失败，不必手动触发（触发也走同一守卫，无副作用但也无效果）。**
- FbMilkPick 11-08 = 6 注（Voided 0 / high 0 / 已结 2 全未中），4 注待赛。
- FbTurnAlert 新格式（PlayType IS NOT NULL）累计 **0 条**；新代码确已上线（`/api/milk/turn-guard/scan` 返回新格式逐注诊断、api dll 17:54），turn-guard 本轮 SUCCESS「无转向告警」→ 零触发而非写入失败。
- 撤注闸生产样本仍为 0（今日/前两日 Voided 均 0，high 告警累计 0），撤注正确性无法评价。
- 经验：`/api/jobs` 的 `lastStatus` 对 paper-freeze 可能为空但实际成功，需结合 api 日志与 `nextRun` 判断。

## 2026-11-08 23:50 二跑（停售后）
- FbPaperTrade 11-08 = **8 条**（已结 2 / 命中 0 / Voided 0 / 待结 6），冻结已跑（21:45:52 六条、22:25:36 两条）。**数字对但成员错**：漏 3 注、多冻 2 注。
- 漏冻 3 注：特温特/阿贾克斯 平负（21:30 已开赛）、特罗姆瑟 0:1（21:30 已开赛）、阿尔克马尔 胜负（23:45 未开赛）。前两场属「开赛后掉榜」，第三场属「引擎整行 DELETE 撤注、无 Voided 痕迹」。→ **paper 台账在 23:00 起跑仍补不回早场，核心缺陷未修**。
- FbMilkPick 11-08 只剩 2 条（周日001 BQ 3-3 / BF 1:0，均未中）；6 注未结算全部消失 = `SaveDayAsync` DELETE 未结算行，**不是 turn-guard 撤注**。Voided=1 生产样本仍为 0。
- FbTurnAlert 今日 2 条全为旧格式（00:20 high / 12:45 mid，PlayType NULL），新格式 0 条、high 0 条。`/api/milk/turn-guard/scan` 返回空（榜面无未结算注可诊断）→ **闭环零触发、无有效样本**。
- 口径教训：**不能只看 FbPaperTrade 条数对账**，必须比对 MatchNo+OptionCode 成员；条数巧合会掩盖漏冻。
