# 夜间只读巡检 — 执行记录

## 2026-11-08 08:30
- API health = ok（5088）。
- **必发已恢复**：job:water-sample 08:12/08:23 SUCCESS（52/60 行），此前 06:26~08:00 仍 TIMEOUT；FbExchange 最后 CapturedAt=2026-11-08 08:23:29，近 6 小时 126 行。已按报喜处理，必发已知问题关闭。
- jc-odds-sample 每 2 分钟 SUCCESS（1620 行）；job:football-night 三次全 SUCCESS（01:36 3379 / 03:10 3516 / 06:59 3661）。
- 结算全部推进到位：FbMilkPick 11-07 9 注 pending=0；FbDailyFollow 11-07 27 注 unsettled=0，11-08 20 注 15 未结（新榜正常）。
- 近 4 小时 33 场有比分，正常。
- **遗留观察点：TaskName='odds' 08-11-08 06:16 仍 FAILED（连续第 3 天），但发生在必发恢复（08:12）之前，同源风控；下轮 06:16 若仍 FAILED 则需另查 token 续期链路。**
- 结论：必发恢复，无其他新异常。全程只读。

## 2026-11-08 06:16
- API health = ok（5088）。
- jc-odds-sample 06:00~06:15 每 2 分钟 SUCCESS（1674 行）；football-night 最后成功 03:10（3516 行）；settle-hourly / daily-follow-settle 05:13 SUCCESS。
- water-sample 04:21~05:56 全 TIMEOUT；FbExchange 最后 CapturedAt 仍 2026-11-07 22:05 → 必发未恢复（已知）。
- **新增观察点：TaskName='odds'（每日全量赔率）06:16 FAILED，消息「token 失效已提前中止（拦截 120 次）」。查近 6 日：11-06/11-07/11-08 连续 FAILED（11-06 另有一次 SUCCESS 165140 行）。同一 500.com 风控根因，非今晚新增，但每日全量赔率已断 3 天，下次巡检继续跟。**
- odds-sample-late 03:18~05:46 连续 TIMEOUT，同 500 源风控。
- 结算：FbMilkPick 11-07 pending 2（继续降）；FbDailyFollow 11-07 27 注全结清，11-08 20 注 16 未结。
- 近 4 小时 38 场有比分，正常。
- 结论：无已知问题之外的新异常。全程只读。

## 2026-11-08 04:15
- API health = ok（5088）。
- football-night 第二次跑 03:10 SUCCESS 3516 行（01:36 首跑 3379 行），夜间赛果链路稳定。
- jc-odds-sample 每 2 分钟 SUCCESS（1728 行）；refresh-near-kickoff SUCCESS。
- water-sample 01:11~03:48 全部 TIMEOUT，FbExchange 最后 CapturedAt 仍为 2026-11-07 22:05 → 必发未恢复（已知）。
- 结算推进：FbMilkPick 11-07 pending 9→4（02:13）→2；FbDailyFollow 11-07 27 注全结清，11-08 20 注 17 未结（已结 3）。
- 近 4 小时有比分 96 场，正常。
- job:settle-hourly / daily-follow-settle 03:28 均 SUCCESS，上轮观察点彻底关闭。
- 结论：除必发已知问题外无新异常。全程只读。

## 2026-11-08 02:13
- API health = ok（5088）。
- **job:football-night 01:36 SUCCESS，3379 行** —— 01:00 首跑成功（首次报喜项）。
- job:jc-odds-sample 持续 SUCCESS（每 2 分钟，1782~1890 行）；water-sample 仍 TIMEOUT（必发已知风控）。
- 近 4 小时 575 场、306 场已有比分，按小时分布 22h37/23h293/00h86/01h125/02h34，属周六夜正常密度，非脏数据。
- 结算：FbMilkPick 11-07 9 注 pending 9→4（推进中）；FbDailyFollow 11-07 27 注全结清（unsettled 11→0），11-08 20 注全未结（新榜正常）。
- **job:settle-hourly 01:44 SUCCESS（1348 行），上轮记录的 23:05/00:05 空档已补跑，观察点可关闭。** job:daily-follow-settle 01:44 SUCCESS。
- FbExchange 最后 CapturedAt=2026-11-07 22:05，未恢复。
- 结论：除必发已知问题外无新异常。全程只读。

## 2026-11-08 00:16
- API health = ok（5088）。
- jc-odds-sample SUCCESS（00:04/00:08/00:10，每次 2160 行）；water-sample 23:58 TIMEOUT（必发已知风控，未恢复，FbExchange 最后 CapturedAt=2026-11-07 22:05）。
- football-night 表内零行：01:00 首跑尚未到点，属正常。
- 近 4 小时有比分场次 3 场，赛果正常入库。
- FbMilkPick 11-07 共 9 注全 pending：对应 7 场开球 23:00~03:45，00:16 仍在进行中，非卡结算。FbDailyFollow 11-07 27 注 11 未结、11-08 20 注 20 未结（新榜），符合预期。
- 观察点：job:settle-hourly 最后运行 22:05，23:05/00:05 两档缺失；11-06 夜同样有 21:05→00:08 空档，暂判夜间常态，下轮继续观察。job:daily-follow-settle 正常（23:58）。
- 结论：除必发已知问题外无新异常。全程只读，未做任何写操作。
