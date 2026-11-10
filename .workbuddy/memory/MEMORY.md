# AiFootBall 长期记忆（运维手册）

> 细则 `判据库.md`；过程见每日 md

## 0 速查
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40/11:40/16:40/20:40。
2. 查库 `& tools\db.ps1 -Query "..."`；**禁 Bash 跑 sqlcmd、禁 Bash 调 powershell**；ps1/.sql 探针纯 ASCII。
3. 必 `.\deploy-api.ps1` + `dotnet test`；手动跑 `POST /api/jobs/{name}/run`。
4. **模型没有 alpha**（Brier .5995 vs 市场 .5812）；**EV 落在返奖率 ±8 点 = 在挑口径**；基线 EV 超返奖率 = 基线给高了。
5. 返奖率 SPF .878 / BQ·JQ .797 / BF .710 = 任一注 EV 天花板。
6. **开赛=票已买**，只能 `Voided` 不能 DELETE；`FbMilkPick` 重算即删未结算行，不可当账。
7. `NoPickLeagues`（欧国联/友谊/青年）不发单关；`NoParlayLeagues`（澳超）不串。

## 一 运维铁律
- 采样：jc-odds 2min / water 10min（3 轮全量）/ exchange-late·odds-late 10·20min / turn-guard·paper-freeze `*/10`。
- ★★ **作业清单在 `jobs.json`**（appsettings 含连接串被 gitignore → 作业定义进黑箱）。**已出事**：少 `rebuild-standings`，`FbMatchRank` 停更两天。加改作业 = 改该文件，缺失直接抛。
- **Collector 与 Api 各写一份 `new RecommendationService(...)`**：加可选依赖两边同改。
- **_recommendLane**：写当日榜的都是 DELETE+INSERT 且无唯一约束，**新增纯库作业一律进这条**。
- **完场不信 `Status`**：`比分齐全 && (Status==3 || MatchTime<=now-150min)`。**受注日 ≠ 开赛日**。
- ★ `SyncLog` 显式写 `StartedAt`；STUCK 分 `[hung]`/`[queued]`；**同行从 RUNNING 回填终态**，`SweepAbandoned` 只在 `RunAsync` 调。
- ★ **ABORTED ≠ FAILED**：前者是进程退出（部署/崩溃），4g 单独 WARN；4c 只算 FAILED/TIMEOUT。

## 二 数据口径与判据
- RecResult '3'主/'1'平/'0'客；`Handicap` **正=主受让、负=主让球**。`PnlIdx` 负=该方向被买重，只有 **≤−60** 有否决力。
- **`HalfHomeScore IS NOT NULL` 本身就是「有真半场」**。串：BQ/JQ 2串 .636→3串 .507；**半全场 3 串、比分不串、总进球 3 串封顶**。
- 单边砸价≥10% 而必发不跟 = 该侧高估（客 −17.0，主胜侧不成立）。主胜<1.35+必发≥70% 反向（限主胜+欧战）；强队让胜破 1.6x = 只赢球不赢盘。
- 胜平/负平**永远同价**，**负平 .740 永不买**；半全场基线**按赔率条件化**。体能只标注不扣分。
- 闸门：比分停发、半全场 ≤8 倍、总进球 ≤10 倍、EV 带 [返奖率×1.00, ×1.45]。

## 三 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`、`high`→撤注不删行。
- ★★ **FbTurnAlert 已改软删除**（`ClearedAt`）：**每个读取点都要加 `ClearedAt IS NULL`**，尤其 `ApplyTurnAlertsToPicks` 的 `NOT EXISTS`。
- ★★ **撤注判据禁 EV 绝对值**：出票闸管「值不值」，撤注闸只管「市场明确反对」，**两闸门槛必须同源**。
- ★★ **回写不许按 `PickDate=@d` 过滤**（0 点后通宵场永不被撤）。

## 四 自检与性能
- `tools\milk-check.ps1`（纯 ASCII）出榜后跑。**自检 SQL 禁写 `--` 注释**（sqlcmd -Q 报错 → 降级 WARN 假绿）。
- ★★ **慢必须归因到请求数或数据量**：节流看 `/api/odds/pressure`；回补看日志分段计时（insert 0.7s vs trend+diag 21s）。
- ★★ **`TrendSql`/`ScoreDiagSql` 是全历史重算**：3 天 67 场带出 **374 万行 = 全表 62%**，一条命令必超 30s。**按 8 场分批**，单条上限 300s。
- ★★ **`OddsSampledAt` 单戳会「喂饱」低频作业**：`odds-sample-late` 窗口是 water-sample 子集 → 空转，已 `Enabled=false`。
- ★★ **薄盘场次被三条筛选联合排除**（`CompanyCount>=8`+`在售`+`recheck` 全不满足）：采过一次永久出局。`StaleResampleHours=6` 兜底**且必须进 ORDER BY**（否则被 Limit 截掉）；`MarketTtlHours = Stale×4/3 = 8`。

## 五 空跑台账 + 终版价
- `FbPaperTrade`（只增不改）：**上过榜即记账、以首次上榜价为准**；分母=已结算且未撤注，回报=命中÷注数，撤注单列，门槛 150 注。
- **撤注对照组**：`PaperSlice.VoidedSettled/VoidedHits/VoidedHitPct` + 报表两切片。`EnoughVoided=50` 前不下结论。
- **北单结算**：净胜=主−客+**北单让球数**。**终版价**三表 `FinalOdds/FinalAt/FinalSource`，不改 `Odds`。
- **稳胆** `FbSteadyPick`：SPF 三档去水→最高→≤1.75→剔均衡局(<40%)→按赔率升 3。98 注 77.55%/0.993，**禁止为填满而放宽**。

## 六 待办
1. 撤注判据回测：分母已建，样本 0，需 ≥50
2. 九档 620 → 3000 场约 5 个月；500 留 30 天
3. `PlayOrder` 未回测；「冷门被买热→不买」待回测
4. 整点 `chore: 自动同步` 会提交工作区（18:00 吞半轮）→ **提交前先看 `git log`**
