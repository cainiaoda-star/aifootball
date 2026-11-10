# AiFootBall 长期记忆（运维手册）

> 细则 `判据库.md`；过程见每日 md

## 0 速查
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40/11:40/16:40/20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（**禁 Bash 跑 sqlcmd/调 powershell**，ps1·sql 纯 ASCII）；改必 `.\deploy-api.ps1`+`dotnet test`；手动 `POST /api/jobs/{name}/run`。
3. **模型无 alpha**（Brier .5995 vs 市场 .5812）；**EV 落返奖率 ±8 点 = 在挑口径**；基线 EV 超返奖率 = 给高了。
4. 返奖率 SPF·NSPF .878 / BQ·JQ .797 / BF .710 = 单注 EV 天花板。
5. **开赛=票已买**只能 `Voided`；`FbMilkPick` 重算即删未结算行，不可当账。
6. 欧国联/友谊/青年不单关；澳超不串。

## 一 运维铁律
- ★★ **作业清单在 `jobs.json`**（appsettings 被 gitignore → 进黑箱）：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10`。已出事：少 `rebuild-standings`，`FbMatchRank` 停更两天；缺失直接抛。
- **Collector 与 Api 各写一份 `new RecommendationService(...)`**，加依赖两边同改。
- **_recommendLane**：写当日榜的都是 DELETE+INSERT 且无唯一约束，新增纯库作业一律进。
- **完场不信 `Status`**：`比分齐全 && (Status==3 || MatchTime<=now-150min)`。
- ★★ **受注日 ≠ 开赛日**：榜必须按 `SteadyPickRules.SalesDate`（期号说了算）分组落库。未改时 22:11 重算把次日注记成当日、`DELETE` 掉 21:50 定版（`SaveMilkAsync`/稳胆已修）。
- ★ **ABORTED ≠ FAILED**（进程退出），4c 只算 FAILED/TIMEOUT。**故障注入造的 FAILED 行必须当场删**，否则 4f 假报 token 失效。

## 二 数据口径与判据
- ★★★ **赔率 1.00 是占位值**：56 场 207 行，多在开赛后 2 分钟写入。读取点有 `Odds>1` 兜底、**分析侧没有** —— 曾凭空造出「热门高估 18 点」，重算后反转（主胜<1.50 81%→89.7%）。**回测必须 `CapturedAt < MatchTime AND Odds > 1.01`**。
- ★★ **样本量硬约束**：高赔单档（7~30 倍）方差极大，检测 10 点差异每格需 **~5000 注**，现 421 注；60 格最强 2.7σ，Bonferroni 需 3.2σ → **36 天数据证明不了任何玩法错价，别再挖**。
- ★★ **必发分歧**：「竞彩比必发乐观 ≥5 点」137 注 **59.2%**（6 周 4 周 <1）。反向 53 注 105.5% 不可当正向。
- RecResult '3'主/'1'平/'0'客；`Handicap` **正=主受让、负=主让球**。`PnlIdx` 负=该方向被买重，只有 **≤−60** 有否决力。
- **`HalfHomeScore IS NOT NULL` 就是「有真半场」**。串：BQ/JQ 2串 .636→3串 .507；半全场 3 串、比分不串、总进球 3 串封顶。
- 主胜<1.35+必发≥70% 反向（限主胜+欧战）；强队让胜破 1.6x = 只赢球不赢盘。
- 胜平/负平**永远同价**，**负平 .740 永不买**；半全场基线**按赔率条件化**。体能只标注不扣分。
- 闸门：比分停发、半全场 ≤8 倍、总进球 ≤10 倍、EV 带 [返奖率×1.00, ×1.45]。

## 三 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`、`high`→撤注不删行。
- ★★ **FbTurnAlert 软删除**（`ClearedAt`）：**每个读取点都要 `ClearedAt IS NULL`**，尤其 `ApplyTurnAlertsToPicks` 的 `NOT EXISTS`。
- ★★ **撤注判据禁 EV 绝对值**：出票闸管「值不值」，撤注闸只管「市场明确反对」，**两闸门槛同源**。
- ★ **回写禁按 `PickDate=@d` 过滤**（通宵场永不被撤）。

## 四 自检与性能
- `tools\milk-check.ps1`（纯 ASCII）出榜后跑。**自检 SQL 禁写 `--` 注释**（sqlcmd -Q 报错→假绿）。
- ★★ **`TrendSql`/`ScoreDiagSql` 全历史重算**（3 天 67 场 = 374 万行 = 全表 62%）：已按 8 场分批 + 300s。
- ★★ **`OddsSampledAt` 单戳喂饱低频作业** → `odds-sample-late` 空转，已停用。
- ★ 薄盘场次被三条筛选联合排除：`StaleResampleHours=6` 兜底**且必须进 ORDER BY**（否则被 Limit 截掉）；`MarketTtlHours=8`。

## 五 台账 + 两条产品线
- `FbPaperTrade`：**上过榜即记账、首次上榜价为准**；分母=已结算未撤注，回报=命中÷注数，撤注单列，门槛 150 注。
- **稳胆** `FbSteadyPick`：SPF 去水→最高→≤1.75→剔均衡局(<40%)→按赔率升 **2**（原 3）。禁为填满而放宽。
- ★★★ **稳胆「每日排名」是唯一正向 edge**：前 1 注 35 注 91.4%/**1.108**、前 2 注 1.011、前 3 注 0.983。赔率 1.209=隐含 82.7% vs 实际 91.4% —— **低赔档抽水最少**。旧「抬不动期望」错在**只记命中率没记回报**；排名含赔率段没有的信息=当天最确定那场。
- ★★ **抽水结构 = 确定性漏损**：25 注里 BQ/JQ/BF 占 96%，加权期望 **0.786**；全玩 SPF/NSPF 是 0.878（差 9.2 点，与模型无关）。BF 实盘 4 注 0 中、返奖率 .710 最低 = 必亏项。

## 六 待办
1. 撤注判据回测样本 0（需 ≥50）；`PlayOrder` 未回测
2. 整点 `chore: 自动同步` 提交工作区 → **提交前先看 `git log`**
