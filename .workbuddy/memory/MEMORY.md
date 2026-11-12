# AiFootBall 长期记忆（运维手册）

> 细则 `判据库.md`；过程见每日 md。只留「有实测 + 已落地代码」的判据。

## 一 铁律
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（**禁 Bash 跑 sqlcmd**）；改必 `.\deploy-api.ps1`（不重启=旧 dll）+ `dotnet test`。
3. `jobs.json`：jc-odds 2min/water 10min/turn-guard·paper-freeze `*/10`/inplay `*/3`；只采赛中须加 `inplayOnly`。500.com 只认真浏览器。
4. 成对修改：`new RecommendationService(...)` 两处；出票/撤注闸**不许各写常数**（统一 `Payout`）；改 INSERT 必同步 VALUES。写当日榜走 `_recommendLane`。
5. 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；**受注日≠开赛日**。ABORTED≠FAILED。

## 二 口径
- ★★★ 返奖率=天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。
- ★★★ **赔率 1.00 是占位值**：回测须 `CapturedAt<MatchTime AND Odds>1.01`。
- ★★★ **赔率口径不可互换**：`FbJcOddsHistory` 与 `FbOdds`(CompanyId=0) 非同一批场次。
- ★★ 样本量：高赔档 10 点差异每格需 ~5000 注；ROI 单注 sd≈2.3。
- RecResult 3主/1平/0客；`Handicap` **正=主受让、负=主让球**；`PnlIdx` 负=被买重，只有 **≤−60** 有否决力。
- **`HalfHomeScore IS NOT NULL` = 有真半场**。串：BQ/JQ 2串 .636→3串 .507。
- 主胜<1.35+必发≥70% 反向（限主胜+欧战）；**负平 .740 永不买**。
- ★★ 不推名单 `NoPickLeagues`（欧国联/友谊赛/球会/U17-23）：Top-1 14.3% vs 市场 42.9%、13 注 0 中、零基线；★ 上限 15→8 即 8 注 3 中，保守非定论。

## 三 概率与权重（11-11 定稿）
- ★★★ **w 冻结：先验定值，未从 216 注 ROI 选出**。SPF/NSPF **0.7**、BQ **0.5**、JQ **0.8**、BF **0.3**；唯一入口 `MarketWeightFor`。
- ★★★ **模型精度输市场，改权重在回报上没被证实**：Brier SPF .6030/.5821；`weight-replay` 无 w 站得住，分辨 .05 需 ~2000 注/组≈10 月。
- ★★ **JQ 退出模型闸**：纯模型 vs 历史频率 BQ −2.51%、**JQ +1.04%（方向反）**；JQ 准入＝市场隐含 ≥10% ＋ 实证闸。
- ★★ 两道闸读不同概率：安全闸=纯模型（`ModelPctPure`，**禁反解**）；价值闸=混合 EV。过线 ⇔ 纯模型·赔率 ≥ 返奖率·(k−w)/(1−w)，**k=1 与 w 无关**。
- ★★ 权重判据=「市场隐含能不能信」而非「模型准不准」：档少/返奖高→去水稳→w 高。单抬 w≠退出闸（w=.8 仍需模型 EV≥.996）。
- ★★ 混合按玩法**全档**归一化（`BlendNormalizer`），ROI 0.000。
- ★ 衰减**不启用**：365 天最好 −.0007（门槛 .002），w=.7 反 +.0001。
- ★ SPF：纯模型 .6021 胜历史频率 .6438（−.0416 过门槛），输市场 .5810。

## 四 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行；软删除，读取点都要 `ClearedAt IS NULL`。
- ★★ 撤注判据**禁 EV 绝对值**：只管「市场明确反对」，门槛与出票闸同源。回写禁按 `PickDate=@d` 过滤。

## 五 自检与审计（11-12，已部署）
- `tools\milk-check.ps1`：十列/接口键/半场同分率/作业健康度/**4i 出榜却存档空=FAIL**；RCSI=0 致 `odds-sample` 整点 TIMEOUT。
- ★★ 每注落 `ModelVersion`＝`dc1|窗口|n|rho|MVID|w|bf`；`FbMilkProbSnapshot` 存**全档**三列（~1500 行/天）。快照按自然日、注单按受注期号，join 用 MatchId。
- ★★★ **`Fit(asOf)` 真过滤样本**（原只当衰减原点，未来场次按 w=1 混入）；`GoalBands` 是总进球唯一算式。
- ★★ 多列 INSERT 用 `-- audit-check [N]` 验（2090 自清）：try/catch 包着，列不齐/超时**静默**；1500 行 1.3s=预算 2.2%。
- ★★ 比分（停发）：Hit@1 **10.85%**/Hit@3 28.60%；1:1 基线 10.50/26.96；≥13% 档 12.97%。
- ★★ 半全场边际 vs 全场：中位 L1 **0.55 点**（门槛 5）→ 成立，平局低估 .31。

## 六 台账与产品线
- `FbPaperTrade`：**上过榜即记账、首价为准**；分母=已结算未撤注。
- ★★★ **稳胆每日 1 注是唯一正向 edge**：91.4%/**1.108**。去水→最高→≤1.75→剔均衡局→按赔率升取 1。
- 北单方向=**模型概率最大档**（与竞彩同名不同义）。大奶双选每场 2 注。
- ★★★ 注单页 `FbDailyFollow`(`/api/daily-follow`)，**前端不调 `/api/milk/picks`**。

## 七 待办
1. 撤注判据回测 0 注（需≥50）、`PlayOrder` 未回测、`GetEuOddsDelta` 未修。
2. **按开赛日冻结模型**(P1-7)：用户定**不挡本版终验**，下迭代做，与快照 salesDate 口径同改。
3. SyncLog 含 **IDLE**（零场次），判失败须排除。
4. **英锦赛/英联杯无联赛半全场基线**，且「英锦赛 vs 英锦标赛」未收别名。
5. 作业语「漏斗 jc 0」不分「没场次/方向挡/名单挡」；`SaveMilkAsync` 应回 `BuildResult` 带分层数。
