# AiFootBall 长期记忆

> 细则 `判据库.md`；过程见每日 md。

## 一 铁律
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（禁 Bash 跑 sqlcmd）；改必 `.\deploy-api.ps1`（不重启=旧 dll）+ `dotnet test`。
3. `jobs.json`：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10` / inplay `*/3`；采赛中须 `inplayOnly`；500.com 只认真浏览器。
4. 成对改 `new RecommendationService(...)` 两处；闸门槛统一 `Payout`；写当日榜走 `_recommendLane`。
5. 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；受注日≠开赛日；ABORTED≠FAILED。
6. 部署必须留痕：`deploy-package\logs\deploy-history.log`（head/旧新 hash/PID/备份目录/触发者/build），核对 `/api/health` 的 `build`(MVID 前 8)+`uptimeSeconds`。来源不明的部署要记录在案。

## 二 口径
- 返奖率=天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。赔率 1.00 是占位：回测须 `CapturedAt<MatchTime AND Odds>1.01`。`FbJcOddsHistory` ≠ `FbOdds(CompanyId=0)`。
- RecResult 3主/1平/0客；`Handicap` 正=主受让；`PnlIdx` 负=被买重，只有 ≤−60 可否决。
- `HalfHomeScore IS NOT NULL`=真半场。串 BQ/JQ 2串 .636→3串 .507。主胜<1.35+必发≥70% 反向（限主胜+欧战）；负平 .740 永不买。
- ★★★ `国际赛`＝竞彩对「国家队热身赛」叫法（3/3 映射 FbMatch 友谊赛）。`LeagueCategory` 已迁 Core、与引擎共用；正式赛事优先判；「国际赛」故意不进 `Names`（子串匹配会砍「国际赛-世界杯预选赛」）。
- `NoPickLeagues`：欧国联/友谊赛/球会/U17+。0 注先分清「禁推」与「没场次」。

## 三 概率与权重
- w 冻结：SPF/NSPF .7、BQ .5、JQ .8、BF .3；唯一入口 `MarketWeightFor`。
- 模型精度输市场；改权重在回报上未被证实。JQ 退出模型闸（纯模型方向反）；安全闸读纯模型，价值闸读混合 EV。衰减不启用。
- ★★★ **ρ 固定 +0.010**（11-13 用户拍板 B；`ScorelineModel.DefaultRho`，`FitRho` 已删）。理由是可复现，不是更准：旧路径同数据跑出 .010/0/−.010，加 ORDER BY 后落 −.060；前向 53,488 场（切点 07-12）−.060 的 1X2 Brier **+0.00072**（显著变差）、0:0 校准 −.11pp→**+.63pp**。ρ=0 与全量似然（−.040）留 A/C 下一轮，**必须换没看过的时段**；`WithRho` 只服务回测台。
- 回归：换物理顺序版本戳必须逐字相同（`RhoStudyTests`）。

## 四 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行；读取点都要 `ClearedAt IS NULL`。
- 撤注判据禁 EV 绝对值：只管「市场明确反对」，门槛与出票闸同源。

## 五 训练集与审计（11-13 16:10 部署 `808b13f`，build `14cc0508`）
- 取样 `GetScoreSamplesAsync(asOf)` 双闸：`MatchTime <= DATEADD(min,-150,asOf)` + `<= GETDATE()`；线上取样与 `Fit` 同一 fitMoment。有比分、无赔率都不是进训练集的理由；`fit-probe` 零容差。
- ★ 150 分钟只是**完赛代理**，不是证据：`UpdatedAt` 是最后写入（重采刷晚，回测口径 162737 场 **100%** 被刷），只能证「此刻已知」。可观测首写延迟仅 **1.9% ≤150min**、**84% >360min**；`football` 抓取每小时一次。→ 残余泄漏**未消除**，任何结论不许写「已消除」；严格口径要「首次入库时间」留痕。
- `Fit` 剔队名空/比分>12（实测 113 场）→ `FitGames = 取样 − 不可用`。
- `ModelVersion`＝`dc1|窗口|n|rho|MVID|w|bf`；`FbMilkProbSnapshot` 全档三列、五玩法齐、含被闸整场撤的场次；NSPF 调整净胜=(主−客)+让球数。
- `milk-check.ps1`：十列/接口键/半场同分率/作业健康/4i（存档空·窗口`?..?`·同档重复=FAIL）；RCSI=0 致 `odds-sample` 整点 TIMEOUT（已知，非回归）。

## 六 台账与产品线
- `FbPaperTrade` 上过榜即记账、首价为准；分母=已结算未撤注。稳胆每日 1 注 91.4%/1.108。注单页 `FbDailyFollow`，前端不调 `/api/milk/picks`。

## 七 待办
1. ρ=0 / 全量似然：用**新时段**独立验（旧测试集已看过）。
2. 回测截止口径：补「首次入库时间」或用抓取网格重建。
3. 撤注判据回测 0 注；`PlayOrder`、`GetEuOddsDelta` 未修。
4. P1-7 按 salesDate 冻结模型 + 快照日期键同改。
5. 漏斗日志不分原因（`SaveMilkAsync` 应回 `BuildResult`）；EV<1 不许称正期望。
6. 英锦赛/英联杯无半全场基线；「英锦赛 vs 英锦标赛」未收别名。
