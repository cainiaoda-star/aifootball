# AiFootBall 长期记忆

> 细则 `判据库.md`，过程见每日 md。

## 一 铁律
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（**禁 Bash 跑 sqlcmd**）；改必 `.\deploy-api.ps1`（不重启=旧 dll）+ `dotnet test`。
3. `jobs.json`：jc-odds 2min/water 10min/turn-guard·paper-freeze `*/10`/inplay `*/3`；采赛中须 `inplayOnly`；500.com 只认真浏览器。
4. 成对改：`new RecommendationService(...)` 两处；闸**不许各写常数**（统一 `Payout`）；改 INSERT 同步 VALUES；写当日榜走 `_recommendLane`。
5. 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；**受注日≠开赛日**。ABORTED≠FAILED。

## 二 口径
- ★★★ 返奖率=天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。
- ★★★ **赔率 1.00 是占位值**：回测须 `CapturedAt<MatchTime AND Odds>1.01`。
- ★★★ **赔率口径不可互换**：`FbJcOddsHistory` 与 `FbOdds`(CompanyId=0) 非同一批场次。
- ★★ 样本量：高赔档 10 点差异每格需 ~5000 注；单注 ROI sd≈2.3。
- RecResult 3主/1平/0客；`Handicap` **正=主受让、负=主让球**；`PnlIdx` 负=被买重，只有 **≤−60** 可否决。
- **`HalfHomeScore IS NOT NULL` = 有真半场**。串：BQ/JQ 2串 .636→3串 .507。
- 主胜<1.35+必发≥70% 反向（限主胜+欧战）；**负平 .740 永不买**。
- ★★ 不推名单 `NoPickLeagues`（欧国联/友谊赛/球会/U17+）：Top-1 14.3% vs 市场 42.9%、13 注 0 中；上限 15→8 即 8 注 3 中，保守非定论。
- ★★★ **`国际赛`＝竞彩对 `友谊赛` 的叫法**（3/3 映射 FbMatch）：名单只认「友谊赛」→**整类漏网**；`LeagueCategory` 也判 LEAGUE 而非 FRIENDLY。11-13 唯一 1 注即来自此。

## 三 概率与权重（11-11 定稿）
- ★★★ **w 冻结：先验定值，未从 216 注 ROI 选出**。SPF/NSPF **0.7**、BQ **0.5**、JQ **0.8**、BF **0.3**；唯一入口 `MarketWeightFor`。
- ★★★ **模型精度输市场，改权重在回报上未被证实**：Brier SPF .6030/.5821；分辨 .05 需 ~2000 注。
- ★★ **JQ 退出模型闸**：纯模型 vs 历史频率 BQ −2.51%、**JQ +1.04%（方向反）**；准入＝市场 ≥10%＋实证闸。
- ★★ 安全闸读纯模型（**禁反解**）、价值闸读混合 EV；过线 ⇔ 纯模型·赔率 ≥ 返奖率·(k−w)/(1−w)，k=1 与 w 无关。
- ★★ 权重判据=「市场隐含能不能信」而非「模型准不准」：档少/返奖高→去水稳→w 高。单抬 w≠退出闸。
- ★ 衰减**不启用**：365 天最好 −.0007（门槛 .002），w=.7 反 +.0001。

## 四 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行；读取点都要 `ClearedAt IS NULL`。
- ★★ 撤注判据**禁 EV 绝对值**：只管「市场明确反对」，门槛与出票闸同源。回写禁按 `PickDate=@d` 过滤。

## 五 自检与审计（11-13 修完，待部署）
- `tools\milk-check.ps1`：十列/接口键/半场同分率/作业健康/**4i 存档空·窗口`?..?`·同档重复=FAIL**；RCSI=0 致 `odds-sample` 整点 TIMEOUT。`/api/health` 报 `build`(MVID 前 8)+`uptimeSeconds`。
- ★★ 每注落 `ModelVersion`＝`dc1|窗口|n|rho|MVID|w|bf`；`FbMilkProbSnapshot` 存全档三列，覆盖**进入计算的每场（含被闸整场撤的）**、五玩法齐；NSPF 调整净胜＝(主−客)+让球数。快照按自然日、注单按期号，join 用 MatchId。
- ★★★ **`Fit(asOf)` 真过滤样本**（原只当衰减原点，未来场次按 w=1 混入）；`GoalBands` 是总进球唯一算式。
- ★★★ 版本戳窗口 `?..?`＝取样 SQL 漏 `f.MatchTime`（11-13 修）：连带衰减 anchor 失效、`Fit(asOf)` 剔光样本；`-- fit-probe` 可发现。
- ★★ `audit-check [N]` 验多列 INSERT；1500 行 1.3s。
- ★★ 比分（停发）Hit@1 **10.85%**/Hit@3 28.60%；半全场边际 L1 中位 **0.55 点**（门槛 5）→ 成立。

## 六 台账与产品线
- `FbPaperTrade`：**上过榜即记账、首价为准**；分母=已结算未撤注。
- ★★★ **稳胆每日 1 注是唯一正向 edge**：91.4%/**1.108**。去水→最高→≤1.75→剔均衡→按赔率升。
- 北单方向=**模型概率最大档**（与竞彩同名不同义）。大奶双选每场 2 注。
- ★★★ 注单页 `FbDailyFollow`，**前端不调 `/api/milk/picks`**。

## 七 待办
1. 撤注判据回测 0 注（需≥50）、`PlayOrder` 未回测、`GetEuOddsDelta` 未修。
2. **按开赛日冻结模型**(P1-7)：不挡本版终验，下迭代做，与快照 salesDate 同改。
3. SyncLog 含 **IDLE**（零场次），判失败须排除。
4. **英锦赛/英联杯无联赛半全场基线**，且「英锦赛 vs 英锦标赛」未收别名。
5. 作业语「漏斗 jc 0」不分「没场次/方向挡/名单挡」；`SaveMilkAsync` 应回 `BuildResult`。
6. `国际赛` 未进 `NoPickLeagues`（用户要求先报告再改）；EV<1 不许称正期望。
