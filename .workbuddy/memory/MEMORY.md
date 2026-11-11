# AiFootBall 长期记忆（运维手册）

> 细则 `判据库.md`；过程见每日 md。只留「有实测样本 + 已落地代码」的判据。

## 一 铁律
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（**禁 Bash 跑 sqlcmd**，ps1/sql 纯 ASCII）；改必 `.\deploy-api.ps1`（不重启=旧 dll）+ `dotnet test`。
3. `jobs.json`：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10` / inplay-sample `*/3`。只采赛中要在 WHERE 加 `inplayOnly`（`ORDER BY EXISTS(BettingCard)` 会让赛中永远排后）。500.com 只认真浏览器。
4. 成对修改：`new RecommendationService(...)` 两处；出票闸/撤注闸不许各写常数（统一 `Payout`/`MinEvPaybackRatio`）；改 INSERT 必同步 VALUES。写当日榜都走 `_recommendLane`（DELETE+INSERT，无唯一约束）。
5. 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；**受注日≠开赛日**（按期号落库）。ABORTED≠FAILED。

## 二 口径
- ★★★ 返奖率=天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。
- ★★★ **赔率 1.00 是占位值**：分析侧无兜底；回测须 `CapturedAt<MatchTime AND Odds>1.01`。
- ★★★ **赔率口径不可互换**：竞彩 `FbJcOddsHistory` 与欧赔 `FbOdds`(CompanyId=0) 不是同一批场次；`JcToEuro=1.20` 只是粗换算。
- ★★ 样本量硬约束：高赔档 10 点差异每格需 ~5000 注（现 421）；ROI 单注 sd≈2.3，216 注 ±0.16。
- RecResult 3主/1平/0客；`Handicap` **正=主受让、负=主让球**；`PnlIdx` 负=被买重，只有 **≤−60** 有否决力。
- **`HalfHomeScore IS NOT NULL` = 有真半场**。串：BQ/JQ 2串 .636→3串 .507；半全场 3 串、比分不串、总进球 3 串顶。
- 主胜<1.35+必发≥70% 反向（限主胜+欧战）；**负平 .740 永不买**。

## 三 概率与权重（11-11 定稿）
- ★★★ **模型精度输市场，但改权重在回报上没被证实**：Brier SPF 3558 场 .6030 vs .5821、BQ 540 场 .8255 vs .7964；`weight-replay` **没有任何 w 站得住**（调参段与验证段排序相反）。分辨 .05 需 ~2000 注/组 ≈10 个月。
- ★★★ **w 冻结：先验定值，未从 216 注 ROI 选出**。SPF/NSPF **0.7**、BQ **0.5**、JQ **0.8**、BF **0.3**、未知 0.5；唯一入口 `MarketWeightFor`，`FrozenWeights_ArePriorNotRoiFitted` 锁死。
- ★★ **JQ 退出模型闸**：纯模型 vs 历史频率 BQ **−2.51%**、**JQ +1.04%（方向反）**。JQ 准入改 ① 市场去水隐含 ≥10% ② 实证闸 `PassesBaselineEv`；模型只做对照，`UsesModelGates(Jq)=false`。
- ★★ 单抬 w 不能替代「模型退出闸」：w=0.8 时 EV=0.2·模型EV+0.8·返奖率，过 1.05 倍线仍需模型 EV≥0.996。
- ★★ 权重判据是「市场隐含能不能信」，不是「模型准不准」（11-11 写反过被纠正）：档少/返奖高 → 去水稳 → w 高。
- ★★ 两道闸读不同概率：安全闸=纯模型（`ModelPctPure`，**禁反解**）；价值闸=混合 EV；实证闸=基线 EV。代数：过线 ⇔ 纯模型·赔率 ≥ 返奖率·(k−w)/(1−w)，**k=1 与 w 无关**；k=1.05 无回测。
- ★★ 混合后按玩法**全档**归一化（`BlendNormalizer`，Σ市场实测 .9905）；对 ROI 影响 0.000 = 正确性修复。
- ★ 时间衰减无收益（365 天 −0.0010，门槛 −0.002）；注释里的数字不是证据（原「500 场模型全胜」库里无实现）。

## 四 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行。`FbTurnAlert` 软删除，读取点都要 `ClearedAt IS NULL`。
- ★★ 撤注判据**禁 EV 绝对值**：出票闸管「值不值」，撤注闸只管「市场明确反对」，门槛同源。回写禁按 `PickDate=@d` 过滤。

## 五 自检
- `tools\milk-check.ps1`（纯 ASCII）：九列/接口键/半场同分率/作业健康度；接口冷启动给 90s。RCSI=0 致 `odds-sample` 整点 TIMEOUT（静默、非数据坏），根治只能凌晨 4 点后开快照读。

## 六 台账与产品线
- `FbPaperTrade`：**上过榜即记账、首价为准**；分母=已结算未撤注，回报=命中÷注数，门槛 150。
- ★★★ **稳胆每日 1 注是唯一正向 edge**：91.4%/**1.108**（前 2 注 1.011、前 3 注 .983）。SPF 去水→最高→≤1.75→剔均衡局→按赔率升取 1。
- 北单方向=**模型概率最大档**（与竞彩同名不同义）。大奶双选每场 2 注：JQ .782→.805。
- ★★★ 注单页是 `FbDailyFollow`(`/api/daily-follow`)，**前端不调 `/api/milk/picks`**。

## 七 待办
1. 撤注判据回测样本 0（需 ≥50）；`PlayOrder` 未回测；`BqHomeOddsEdges` 2.60 断崖未修。
2. `GetEuOddsDeltaAsync` 同源未修（`GetClosingEuAsync` 已修）。
3. SyncLog 三态含 **IDLE**（采样零场次），判失败须排除。
4. ★★ **冻结权重 + JQ 退出闸已提交 `178b781`**，11-11 22:05 排了自动部署；完成前线上仍是旧口径。
5. **英锦赛/英联杯无联赛半全场基线**，且「英锦赛 vs 英锦标赛」未收别名 → 这两赛事出票闸偏严。
