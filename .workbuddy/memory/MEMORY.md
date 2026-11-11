# AiFootBall 长期记忆（运维手册）

> 细则 `判据库.md`；过程见每日 md

## 0 速查
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（**禁 Bash 跑 sqlcmd**，ps1·sql 纯 ASCII）；改必 `.\deploy-api.ps1`+`dotnet test`。
3. **模型无 alpha**（Brier .5995 vs 市场 .5812）；北单 `avg(p×SP)=.9834`；**EV 落返奖率 ±8 点 = 在挑口径**；基线 EV 超返奖率=给高了。
4. **开赛=票已买**只能 `Voided`；`FbMilkPick` 重算即删未结算行，不可当账。

## 一 铁律
- ★★★ **odds.500.com 只认真浏览器**（37）：cookie/头/指纹/token 全无效；唯一通路 `ChromeBridge`(CDP)，有配额、验证页当失败。
- ★★ **作业清单 `jobs.json`**：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10` / **inplay-sample `*/3`（赛中，HoursBehind2.2+inplayOnly）**。
- ★★ **窗口 ≠ 优先级**：`GetMatchesForSamplingAsync` 的 `ORDER BY EXISTS(BettingCard)` 让在售赛前排前，Limit 一截赛中永远进不来——要只采赛中必须在 **WHERE** 加 `inplayOnly`。
- ★★★ **返奖率是天花板**：SPF/NSPF .878 / BQ/JQ .797 / BF .710；大奶榜改前 .785，砍 BF 后 .797。**参数改不动**（39）。
- ★★ **配额≠出票**：`MaxPerPlay[Nspf]` 是死配置，引擎无 NSPF 生成器（提 1→3 仍 0 注）。改前先查生成循环。
- **成对修改**：`new RecommendationService(...)` 两处；出票闸/撤注闸各写常数；**改 INSERT 必同步 VALUES**（北单漏参数→写不进库）。
- **_recommendLane**：写当日榜的都是 DELETE+INSERT 且无唯一约束，新增纯库作业一律进。
- **完场不信 `Status`**：`比分全 && (Status==3 || MatchTime<=now-150min)`。
- ★★ **受注日 ≠ 开赛日**：按 `SteadyPickRules.SalesDate`（期号）分组落库，否则 22:11 重算把次日注记成当日被删。
- ★ **ABORTED ≠ FAILED**，4c 只算 FAILED/TIMEOUT。

## 二 口径与判据
- ★★★ **赔率 1.00 是占位值**：读取点有兜底、分析侧没有（曾造「热门高估 18 点」假结论）。回测须 `CapturedAt<MatchTime AND Odds>1.01`。
- ★★ **样本量硬约束**：高赔档 10 点差异每格需 **~5000 注**（现 421）→ **证明不了错价，别挖**。
- ★★ **联赛别名连「两队样本」一起校验**（11-11）：加「日联赛杯→日联杯」后鹿岛 0 场/神户 2 场 <`MinTeamGames`3→榜面 4→2 场。`HasLeague`≠算得动。
- ★★★ **赔率口径不可互换**（11-11）：竞彩 `FbJcOddsHistory` 与欧赔 `FbOdds`(CompanyId=0) **装的不是同一批场次**；`JcToEuro=1.20` 只是查基线表的粗换算，不是转换器。同一判据两种口径可出相反结论（主胜高估：竞彩 2.42-3.00 档 **+6.69** vs 欧赔 2.90-3.60 档 **−4.64**）。
- ★★ **`HomePriceRich`（欧赔收盘主胜 2.90~3.60=偏贵）只标不撤**：证据仅 1.0~1.6σ；且 28 注历史大奶**只有 3 注落档且全非主胜**（大奶从不押这类势均力敌场）⇒ **当前近似死代码**，别当有效风控写进复盘。判定必须用 `HomeWinEuClose`，不能复用 `spfHome`（有竞彩价时它是竞彩价）。
- RecResult 3主/1平/0客；`Handicap` **正=主受让、负=主让球**。`PnlIdx` 负=被买重，只有 **≤−60** 有否决力。
- **`HalfHomeScore IS NOT NULL` = 有真半场**。串：BQ/JQ 2串 .636→3串 .507；半全场 3 串、比分不串、总进球 3 串顶。
- 主胜<1.35+必发≥70% 反向（限主胜+欧战）；**负平 .740 永不买**。其余口径见判据库 三。

## 三 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行。
- ★★ **FbTurnAlert 软删除**（`ClearedAt`）：每个读取点都要 `ClearedAt IS NULL`，尤其 `ApplyTurnAlertsToPicks` 的 `NOT EXISTS`。
- ★★ **撤注判据禁 EV 绝对值**：出票闸管「值不值」，撤注闸只管「市场明确反对」，两闸门槛同源。
- ★ **回写禁按 `PickDate=@d` 过滤**（通宵场不撤）。

## 四 自检与性能
- `tools\milk-check.ps1`（纯 ASCII）出榜后跑。**自检 SQL 禁 `--` 注释**（sqlcmd -Q 报错→假绿）。
- ★★ **`TrendSql`/`ScoreDiagSql` 全历史重算**：8 场分批 + 300s。
- ★★ **`OddsSampledAt` 单戳喂饱低频作业**→`odds-sample-late` 空转，停用。

## 五 台账 + 产品线
- `FbPaperTrade`：**上过榜即记账、首价为准**；分母=已结算未撤注，回报=命中÷注数，门槛 150。
- **稳胆** `FbSteadyPick`：SPF 去水→最高→≤1.75→剔均衡局(<40%)→按赔率升取 **1**（11-11 改，Rank2 .914 亏）。
- ★★★ **稳胆每日 1 注是唯一正向 edge**：91.4%/**1.108**（前 2 注 1.011、前 3 注 0.983）；`drift-probe`≈`steady-probe`.93 互证。
- **北单**（`BdSpfPickService`）：方向=**模型概率最大档**（竞彩那个是市场档，同名不同义）；11-10 全押英锦赛 .669→加 `MaxPerLeague=3`；双概率列已落库。
- ★★ **大奶双选（11-11）**：每场 2 注，`MaxPerPlay` BQ4/JQ2 同步放大。423 场：JQ .782→.805、BQ .827→.815——**命中翻倍、回报不动**。
- ★★★ 注单页是 `FbDailyFollow`(`/api/daily-follow`)，**前端不调 `/api/milk/picks`**；第二注进 `Alt*` 列（按 MatchId 一对一）。

## 六 待办
1. 撤注判据回测样本 0（需 ≥50）；`PlayOrder` 未回测。
2. ★ **`BqHomeOddsEdges` 2.60 是断崖**（.599→21.32、.601→12.62）：主胜 2.15→2.25 就把胜胜 EV .863 砸到 .511 被拦。未修。
3. Chrome 11-11 12:00 恢复，留意 `LastError` 与配额。
4. RCSI=0 致 TIMEOUT，凌晨 4 点后开 `READ_COMMITTED_SNAPSHOT`。
5. 整点 `chore: 自动同步`→**提交前先看 `git log`**
6. ★ **`GetClosingEuAsync` 不限 `CapturedAt`**（`MAX(CASE WHEN IsOpening=0 THEN HomeWin END)`）：开赛后重算会把**滚球价当收盘价**，`HomeWinEuClose` 同路受影响。修法加 `AND (m.MatchTime IS NULL OR o.CapturedAt <= m.MatchTime)`。未修。
7. `SampleOddsAsync` 「无待采样赛事」记 SUCCESS → 健康度看不出「作业活着但一场没采」。
