# AiFootBall 长期记忆（运维手册）

> 细则 `判据库.md`；过程见每日 md

## 0 速查
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（**禁 Bash 跑 sqlcmd/调 powershell**，ps1·sql 纯 ASCII）；改必 `.\deploy-api.ps1`+`dotnet test`。
3. **模型无 alpha**（Brier .5995 vs 市场 .5812）；北单 `avg(p×SP)=0.9834`（38）；**EV 落返奖率 ±8 点 = 在挑口径**；基线 EV 超返奖率 = 给高了。
4. **开赛=票已买**只能 `Voided`；`FbMilkPick` 重算即删未结算行，不可当账。

## 一 铁律
- ★★★ **odds.500.com 只认真浏览器**（37）：换 cookie/头/指纹/token 全无效；唯一通路 `ChromeBridge`(CDP)，有配额、拿到验证页当失败。
- ★★ **作业清单在 `jobs.json`**：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10`。
- ★★★ **返奖率是硬天花板**：SPF/NSPF .878 / BQ/JQ .797 / BF .710；大奶榜改前加权 **.785**，砍 BF 后 .797。**参数改不动它**（39）。
- ★★ **配额≠出票**：`MaxPerPlay[Nspf]` 是死配置，引擎无 NSPF 生成器（提 1→3 仍 0 注）。改配额前先查有没有生成循环。
- **成对修改**：`new RecommendationService(...)` 两处；出票闸/撤注闸各写常数；**改 INSERT 列必同步 VALUES**（北单漏三参数 → 全线写不进库）。
- **_recommendLane**：写当日榜的都是 DELETE+INSERT 且无唯一约束，新增纯库作业一律进。
- **完场不信 `Status`**：`比分齐全 && (Status==3 || MatchTime<=now-150min)`。
- ★★ **受注日 ≠ 开赛日**：按 `SteadyPickRules.SalesDate`（期号）分组落库，否则 22:11 重算会把次日注记成当日并被 `DELETE`。
- ★ **ABORTED ≠ FAILED**，4c 只算 FAILED/TIMEOUT。

## 二 口径与判据
- ★★★ **赔率 1.00 是占位值**：读取点有兜底、分析侧没有（曾造「热门高估 18 点」假结论）。回测必须 `CapturedAt<MatchTime AND Odds>1.01`。
- ★★ **样本量硬约束**：高赔档检测 10 点差异每格需 **~5000 注**（现 421）→ **证明不了任何错价，别挖**。
- RecResult '3'主/'1'平/'0'客；`Handicap` **正=主受让、负=主让球**。`PnlIdx` 负=该方向被买重，只有 **≤−60** 有否决力。
- **`HalfHomeScore IS NOT NULL` 就是「有真半场」**。串：BQ/JQ 2串 .636→3串 .507；半全场 3 串、比分不串、总进球 3 串封顶。
- 主胜<1.35+必发≥70% 反向（限主胜+欧战）；**负平 .740 永不买**。其余口径见判据库 三。

## 三 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行。
- ★★ **FbTurnAlert 软删除**（`ClearedAt`）：**每个读取点都要 `ClearedAt IS NULL`**，尤其 `ApplyTurnAlertsToPicks` 的 `NOT EXISTS`。
- ★★ **撤注判据禁 EV 绝对值**：出票闸管「值不值」，撤注闸只管「市场明确反对」，**两闸门槛同源**。
- ★ **回写禁按 `PickDate=@d` 过滤**（通宵场永不被撤）。

## 四 自检与性能
- `tools\milk-check.ps1`（纯 ASCII）出榜后跑。**自检 SQL 禁写 `--` 注释**（sqlcmd -Q 报错→假绿）。
- ★★ **`TrendSql`/`ScoreDiagSql` 全历史重算**：8 场分批 + 300s。
- ★★ **`OddsSampledAt` 单戳喂饱低频作业** → `odds-sample-late` 空转，已停用。

## 五 台账 + 产品线
- `FbPaperTrade`：**上过榜即记账、首次上榜价为准**；分母=已结算未撤注，回报=命中÷注数，门槛 150 注。
- **稳胆** `FbSteadyPick`：SPF 去水→最高→≤1.75→剔均衡局(<40%)→按赔率升取 **1**（11-11 改，反解 Rank2=0.914 在亏）。
- ★★★ **稳胆每日 1 注是唯一正向 edge**：91.4%/**1.108**（前 2 注 1.011、前 3 注 0.983）；`drift-probe` .937 ≈ `steady-probe` .936 互证。
- **北单**（`BdSpfPickService`）：方向=**模型概率最大档**（竞彩相反，那个是市场档，同名不同义）；11-10 十注全押英锦赛→回报 0.669，已加 `MaxPerLeague=3`；`ModelProb/MarketProb` 双列已落库。
- ★★ **大奶双选（11-11）**：每场 2 注，`MaxPerPlay` BQ4/JQ2 必须同步放大。423 场：JQ 23%/.782→45%/.805、BQ 35%/.827→54%/.815（全押 .785）——**命中翻倍、回报不动**，本金翻倍。
- ★★★ 注单页是 `FbDailyFollow`(`/api/daily-follow`)，**前端不调 `/api/milk/picks`**；第二注进 `Alt*` 列（表按 MatchId 一对一）。

## 六 待办
1. 撤注判据回测样本 0（需 ≥50）；`PlayOrder` 未回测。
2. Chrome 通道 11-11 12:00 已恢复（`FbOdds`/`FbExchange` 同步前进），留意 `LastError` 与配额。
3. RCSI=0 致 TIMEOUT，凌晨 4 点后开 `READ_COMMITTED_SNAPSHOT`。
4. 整点 `chore: 自动同步` → **提交前先看 `git log`**
