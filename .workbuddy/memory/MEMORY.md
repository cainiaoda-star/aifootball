# AiFootBall 长期记忆
> 细则 `判据库.md`；过程见每日 md。

## 一 铁律
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（禁 Bash 跑 sqlcmd）；改必 `.\deploy-api.ps1`（不重启=旧 dll）+ `dotnet test`。
3. `jobs.json`：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10` / inplay `*/3`；采赛中须 `inplayOnly`；500.com 只认真浏览器。
4. 成对改 `new RecommendationService(...)` 两处；闸门槛统一 `Payout`；写当日榜走 `_recommendLane`。
5. 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；受注日≠开赛日。
6. 部署留痕 `deploy-history.log`（head/hash/PID/备份/build）+ 核对 `/api/health` 的 `build`(MVID 前 8)。
7. ★★★ 禁止对「部署类」自动化 `automation_update`：update 后 1–2 分钟内必触发；`90a1a054` 计划 22:05 实触发 7 次、真部署 4 次——「来源不明的部署」就是它自己。两条已 PAUSED，只手工部署。`head=unknown` 与触发者无关。
8. ★★★ **未经明确同意禁止手工删改生产数据**。只读核查不算；DELETE/UPDATE/TRUNCATE 一律先问、后登记到 `OPS-DATA-CHANGES.md`（时间/对象/依据/影响/是否经同意）。11-13 17:27 手工删 `PickId=10646` 已越界（当时该修的是代码，不是删行）。

## 二 口径
- 返奖率=天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。赔率 1.00 是占位：回测须 `CapturedAt<MatchTime AND Odds>1.01`。`FbJcOddsHistory` ≠ `FbOdds(CompanyId=0)`。
- RecResult 3主/1平/0客；`Handicap` 正=主受让；`PnlIdx` 负=被买重，只有 ≤−60 可否决。
- `HalfHomeScore IS NOT NULL`=真半场。串 BQ/JQ 2串 .636→3串 .507。主胜<1.35+必发≥70% 反向（限主胜+欧战）；负平 .740 永不买。
- `国际赛`＝竞彩对「国家队热身赛」的叫法（3/3 映射友谊赛）；`LeagueCategory` 在 Core 与引擎共用；「国际赛」故意不进 `Names`（子串会砍「国际赛-世界杯预选赛」）。
- `NoPickLeagues`：欧国联/友谊赛/球会/U17+。0 注先分清「禁推」与「没场次」。

## 三 概率与权重
- w 冻结：SPF/NSPF .7、BQ .5、JQ .8、BF .3；唯一入口 `MarketWeightFor`。模型精度输市场，改权重未被证实，衰减不启用。
- ★★★ **ρ 固定 +0.010**（11-13 拍板 B，`DefaultRho`，`FitRho` 已删）。理由是**可复现**：同数据跑出 .010/0/−.010，加 ORDER BY 后落 −.060；前向 53,488 场 −.060 的 1X2 Brier **+0.00072**（显著变差）、0:0 校准 −.11pp→**+.63pp**。ρ=0 与全量似然（−.040）留 A/C，**必须换没看过的时段**；`WithRho` 只服务回测台。换物理顺序版本戳须逐字相同。

## 四 告警闭环
- 去重 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行；读取点都要 `ClearedAt IS NULL`。
- 撤注判据禁 EV 绝对值：只管「市场明确反对」，门槛与出票闸同源。

## 五 训练集与审计（11-13 16:10 部署 `808b13f`，build `14cc0508`）
- 取样双闸：`MatchTime <= DATEADD(min,-150,asOf)` + `<= GETDATE()`；线上取样与 `Fit` 同一 fitMoment；`fit-probe` 零容差；`Fit` 剔队名空/比分>12（113 场）。
- ★ 150 分钟只是**完赛代理**：`UpdatedAt` 是最后写入（回测口径 162737 场 **100%** 被刷）；首写延迟仅 **1.9% ≤150min**、**84% >360min** → 泄漏**未消除**，不许写「已消除」。
- `ModelVersion`＝`dc1|窗口|n|rho|MVID|w|bf`；NSPF 调整净胜=(主−客)+让球数。

### ★★ 存档事故 → 榜面按轮发布（11-13，第三轮改法，未部署）
- **不是「从未写入」**：11:40 实测 48 行/1 场/3 玩法，17:15 变 0；`IDENT_CURRENT=1560` 证明写得成。根因：`SaveProbSnapshotsAsync` 先 `DELETE 当日` 再 `if(rows==0) return`。空表上 `MAX(Id)` 恒 NULL，不能当证据。
- ★★★ **三种形态必须分开**（`MilkBoardPlan`，纯函数可测）：
  ① **期号确认 + 采集完整 + 全部被过滤** → 按**过滤前**受注日重写（空列表=撤失效旧票）；
  ② **只采到部分场次** → `Partial`：不写不删，旧票保留；
  ③ **过滤前 0 场 / 批次不可用** → 一行不动。
  `RawMatches>0` 只证明「拿到一些场次」：`BettingCard` 按 (Board,Expect,MatchNo) MERGE，部分采集**不删旧行**、只把 `CapturedAt` 停在上一批，而 `GetCardsAsync` 只读 `MAX(CapturedAt)` → 工作集凭空缩小。判据 = `MilkCaptureState`（本批在售 vs 该期已知在售，均由 `BettingCard.MatchTime>now` 直接算，无需台账）。
  范围只能来自 `jcRecs`：`build.Inputs`／`FbMilkGate`／`Recommendation` 全是算完之后的产物，全被过滤时同时为 0。
- ★★★ **发布是状态机，不是 `MAX(RunId)`**：`FbMilkRun.Status`（running/success/failed/skipped/partial）+ `PublishedAt`；顺序 **先落 running 头 → 写 picks/gate/input/snapshot → 全部成功且「有输入就有存档」才在同一事务里写 `FbMilkRunDate` 并标 success**。失败轮 RunId 再大也不许被选中。
- `FbMilkRunDate(RunId,SalesDate,PickCount)` 是榜面指针：**0 注也要写一行**，否则「榜面归零」与「这轮没管这天」同形。所有读路径（榜/闸/撤榜/风控/战绩/翻页/空跑冻结）都只认 `Status='success'` 的轮；无新轮退回 `RunId IS NULL` 旧口径。
- ★★ **唯一键必须含 RunId**：`UX_FbMilkPick(Date,MatchId,Play,Opt)` 与 `UX_FbMilkGate(Date,MatchId)` 只能存一轮——自检在真库撞 2601 才暴露。改为 `+RunId` 后同轮内仍唯一（防交错重写），跨轮各留一份。`SaveDayAsync` 的 DELETE 也随之收窄到本轮（撤票=发布不含它的新轮次，不再跨轮 DELETE）。
- 验证：1077 测试通过（新增 9 条）；`audit-check` 七段全过——A 轮发布 2 注→榜 2、B 轮成功 0 注→榜 0 且历史仍 2 行、C 轮 failed 且 **RunId 更大**→榜仍 0、D 轮 partial→榜仍 0、闸门同步跟发布轮。
- ★ **结构变更也算改库**：加列/建表/改索引必须与数据变更一样登记 `OPS-DATA-CHANGES.md` 并纳入部署审批。11-13 18:20 与 19:04 两次 `audit-check` 确实在生产库改了结构（不是「完全未改库」）。

## 六 赔率新鲜度（按链路分开）
- **竞彩 `FbJcOddsHistory`**（出票价）：2min 一轮，11-13 513 次 SUCCESS、最大间隔 4min → 数据没坏；`GetLatestAsync` **零 age 守卫**。
- **欧赔 `FbOdds`**：11-13 各场仅 2–3 采样点，最大间隔 291min；`odds-sample` TIMEOUT 16 次（RCSI=0 锁等待，作业静默结束）。
- **守卫**：唯一阈值 `MarketTtlHours = 8h` 只管亚盘初/终盘；`MilkScanService.WatchPoolAsync` 三处不传 `freshHours`，review 传 `null`。
- 「价涨榜不跟」≠「用了过期价」（新加坡vs巴拉圭 JQ3：11:44 起 4.40，榜仍 4.10），两种形态分开写。
- 数据库超时（生产侧 RCSI）与吃到旧价（消费侧 TTL）**分开处理**。

## 七 台账
- `FbPaperTrade` 上过榜即记账、首价为准；分母=已结算未撤注。稳胆每日 1 注 91.4%/1.108。注单页 `FbDailyFollow`。

## 八 待办
1. **★ 首次入库时间留痕**（`FbMatch.FirstSeenAt`），否则回测泄漏消不掉。
2. ρ=0 / 全量似然：新时段独立验。
3. 新鲜度（**单独排期，不与审计修复捆绑**）：JC age 闸 ~30min、欧赔/必发/大小球 `freshHours` 3h —— 两个数都只是**待验证阈值**。★★ **超龄不得默认退纯模型继续出票**（会绕过冻结权重与 JQ 市场主导），正确行为是**停该玩法推荐并标「数据过期」**，降级方案另行回测。`WatchPoolAsync` 补参。生产侧 RCSI 另排。
4. RCSI 根治 `odds-sample`（凌晨窗口）。
5. 撤注判据回测 0 注；`PlayOrder`、`GetEuOddsDelta` 未修。
6. P1-7 按 salesDate 冻结模型 + 快照日期键同改。
7. 英锦赛/英联杯无半全场基线；「英锦赛 vs 英锦标赛」未收别名。
