# AiFootBall 长期记忆

> 细则 `判据库.md`，过程见每日 md。

## 一 铁律
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（禁 Bash 跑 sqlcmd）；改必 `.\deploy-api.ps1`（不重启=旧 dll）+ `dotnet test`。
3. `jobs.json`：jc-odds 2min/water 10min/turn-guard·paper-freeze `*/10`/inplay `*/3`；采赛中须 `inplayOnly`；500.com 只认真浏览器。
4. 成对改：`new RecommendationService(...)` 两处；闸不许各写常数（统一 `Payout`）；写当日榜走 `_recommendLane`。
5. 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；受注日≠开赛日。ABORTED≠FAILED。

## 二 口径
- 返奖率=天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。
- 赔率 1.00 是占位值：回测须 `CapturedAt<MatchTime AND Odds>1.01`。`FbJcOddsHistory` 与 `FbOdds`(CompanyId=0) 不可互换。
- 样本量：高赔档 10 点差异每格需 ~5000 注；单注 ROI sd≈2.3。
- RecResult 3主/1平/0客；`Handicap` 正=主受让、负=主让球；`PnlIdx` 负=被买重，只有 ≤−60 可否决。
- `HalfHomeScore IS NOT NULL` = 有真半场。串：BQ/JQ 2串 .636→3串 .507。
- 主胜<1.35+必发≥70% 反向（限主胜+欧战）；负平 .740 永不买。
- ★★★ `国际赛`＝竞彩对「国家队热身赛」的叫法（3/3 映射 FbMatch 友谊赛）。已修 `90a8fa0`：`LeagueCategory` 迁 Core，`NoPickLeagues` 与引擎共用一份判定，正式赛事优先判定；「国际赛」故意不进 `Names`（子串匹配会砍掉「国际赛-世界杯预选赛」）。两处各写一份判据必然分叉。
- 不推名单 `NoPickLeagues`（欧国联/友谊赛/球会/U17+）；名单外的 0 注要先分清是禁推还是没场次。

## 三 概率与权重
- w 冻结：SPF/NSPF 0.7、BQ 0.5、JQ 0.8、BF 0.3；唯一入口 `MarketWeightFor`。
- 模型精度输市场，改权重在回报上未被证实（Brier SPF .6030/.5821；分辨 .05 需 ~2000 注）。
- JQ 退出模型闸（纯模型 vs 历史频率 JQ +1.04%，方向反）；安全闸读纯模型（禁反解），价值闸读混合 EV。
- 权重判据=「市场隐含能不能信」；衰减不启用（365 天最好 −.0007）。

## 四 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行；读取点都要 `ClearedAt IS NULL`。
- 撤注判据禁 EV 绝对值：只管「市场明确反对」，门槛与出票闸同源。

## 五 自检与审计（`90a8fa0` 于 11-13 15:44 部署，build b425a886）
- `milk-check.ps1`：十列/接口键/半场同分率/作业健康/4i 存档空·窗口`?..?`·同档重复=FAIL；RCSI=0 致 `odds-sample` 整点 TIMEOUT（已知，非回归）。
- 训练集只认开赛时刻：`GetScoreSamplesAsync(asOf)` 双闸 `<=@asOf` + `<=GETDATE()`；线上取样与 `Fit` 同一 fitMoment。有比分、无赔率都不是理由。`fit-probe` 无容差：进训练集即 FAIL。
- `Fit` 剔「队名空/比分>12」（实测 113 场）→ `FitGames = 取样 − 不可用`。
- 取样顺序必须稳定：`FitRho` 按步长 71 抽 2500 点，无 ORDER BY 时同数据能跑出 0.010/0.000/−0.010 → 两张榜。已加 `ORDER BY MatchTime, MatchId`，现值 −0.060，二b 段两次戳钉死。rho 弱识别，不当判据。
- ★★★ **ρ=−0.060 前向验证未通过（11-13，用户暂停上线）**：`rho-study`，训练 12.48 万/测试 53,488 场（切点 2026-07-12）。同一训练集换 5 种物理顺序，ρ 落 −0.010/−0.030/−0.060/−0.050/−0.060（极差 0.05）；训练集子采样重估 **−0.010**、全量似然 **−0.040**。前向 vs +0.010：比分 LogLoss +0.00009（区间跨 0）、**1X2 Brier +0.00072（[+0.00056,+0.00089]，显著变差）**、总进球 +0.00001、半全场不变；低比分 0:0 由 6.25（−0.11pp）推到 **6.99（+0.63pp）**、1:1 高估 +1.14pp。结论：过度修正，不是「更准」。候选方案 ρ 固定 0（0:0 6.35，最贴实际 6.36）/ 固定 +0.010 / 改进估计后独立复核。
- ★★★ **训练集截止=已完赛**：`Fit(asOf)` 与 `GetScoreSamplesAsync` 都要求 `开赛 + 150min ≤ asOf`（`MatchDurationMinutes`）。只判「开赛 ≤ asOf」在历史回测里是泄漏——当时还在踢的比赛因为今天有比分被算成已知。
- 每注 `ModelVersion`＝`dc1|窗口|n|rho|MVID|w|bf`；`FbMilkProbSnapshot` 全档三列、五玩法齐、含被闸整场撤的场次；NSPF 调整净胜＝(主−客)+让球数。
- `Fit(asOf)` 真过滤样本；`GoalBands` 是总进球唯一算式。
- 版本戳 `?..?`＝取样 SQL 漏 `f.MatchTime`（已修）；比分 Hit@1 10.85%，半全场边际 L1 中位 0.55 点。

## 六 台账与产品线
- `FbPaperTrade`：上过榜即记账、首价为准；分母=已结算未撤注。
- 稳胆每日 1 注是唯一正向 edge：91.4%/1.108。
- 北单方向=模型概率最大档；大奶双选每场 2 注。注单页 `FbDailyFollow`，前端不调 `/api/milk/picks`。

## 七 待办
1. 撤注判据回测 0 注、`PlayOrder` 未回测、`GetEuOddsDelta` 未修。
2. 按开赛日冻结模型(P1-7)：下迭代做，与快照 salesDate 同改。
3. SyncLog 含 IDLE（零场次），判失败须排除。
4. 英锦赛/英联杯无联赛半全场基线；「英锦赛 vs 英锦标赛」未收别名。
5. 作业语「漏斗 jc 0」不分原因；`SaveMilkAsync` 应回 `BuildResult`。
6. `FitRho` 改全量似然或固定值；EV<1 不许称正期望。
