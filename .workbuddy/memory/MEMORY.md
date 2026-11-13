# AiFootBall 长期记忆
> 细则 `判据库.md`；过程见每日 md。

## 一 铁律
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query`（禁 Bash 跑 sqlcmd）；改必 `.\deploy-api.ps1`（不重启=旧 dll）+ `dotnet test`。
3. `jobs.json`：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10` / inplay `*/3`；采赛中须 `inplayOnly`；500.com 只认真浏览器。
4. 成对改 `new RecommendationService(...)` 两处；闸门槛统一 `Payout`；写当日榜走 `_recommendLane`。
5. 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；受注日≠开赛日。
6. 部署留痕 `deploy-history.log` + 核对 `/api/health` 的 `build`(MVID 前 8)。
7. ★★★ 禁止对「部署类」自动化用 `automation_update`（update 后 1–2 分钟内必触发，`90a1a054` 实触发 7 次）。只手工部署。
8. ★★★ **未经明确同意禁止手工删改生产数据/结构**；只读不算；先问、后登记 `OPS-DATA-CHANGES.md`。11-13 删 `PickId=10646` 已越界。
9. **判据不许取自被测对象的输出**（连踩两次）：`input>0 且 snapshot>0`、「预期=引擎自己的存档」都只证明「算出来的都写了」。判据要从**输入侧**独立推出。

## 二 口径
- 返奖率=天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。赔率 1.00 是占位：回测须 `CapturedAt<MatchTime AND Odds>1.01`。`FbJcOddsHistory` ≠ `FbOdds(CompanyId=0)`。
- RecResult 3主/1平/0客；`Handicap` 正=主受让；`PnlIdx` 负=被买重，只有 ≤−60 可否决。
- `HalfHomeScore IS NOT NULL`=真半场。串 BQ/JQ 2串 .636→3串 .507。主胜<1.35+必发≥70% 反向（限主胜+欧战）；负平 .740 永不买。
- `国际赛`＝竞彩对国家队热身赛的叫法，故意不进 `Names`（子串会砍「国际赛-世界杯预选赛」）。`NoPickLeagues`：欧国联/友谊赛/球会/U17+。
- **共用一个口径就只写一份实现**（`Payout`/`HalfFullCombos`/`PaperTradeMath`）：两处各写一份 = 系统撤销自己刚出的票（已踩）。

## 三 概率与权重
- w 冻结：SPF/NSPF .7、BQ .5、JQ .8、BF .3；唯一入口 `MarketWeightFor`。模型精度输市场，改权重未证实，衰减不启用。
- ★★★ **ρ 固定 +0.010**（11-13 拍板 B，`DefaultRho`），理由是可复现；ρ=0 与全量似然（−.040）留 A/C，须换没看过的时段。`WithRho` 只服务回测台。

## 四 告警闭环
- 去重 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行；读取点都要 `ClearedAt IS NULL`。
- 撤注判据禁 EV 绝对值：只管「市场明确反对」，门槛与出票闸同源。

## 五 存档 / 发布状态机 / 战绩
- **三种形态**（`MilkBoardPlan`）：①期号确认+采集完整+全被过滤→按**过滤前**受注日重写（空列表=撤失效旧票）；②只采到部分→`Partial` 不写不删；③过滤前 0 场/批次不可用→一行不动。判据 `MilkCaptureState`。
- **发布是状态机不是 `MAX(RunId)`**：`FbMilkRun.Status`；先 running 头 → 写四表 → 全成功才同事务写 `FbMilkRunDate`（0 注也写行）+ success。失败轮再大也不顶榜。读路径全认 `success`，无新轮退回 `RunId IS NULL`。
- **唯一键必须含 RunId**（真库撞 2601 才暴露）：`UX_FbMilkPick_Run`、`UX_FbMilkGate_Run`。
- ★ **逐档核对，预期从输入侧推**：`MilkInputMatch.Options`（`Distributed++` 处记）× 档位域 = 应有存档；漏一场/一档即 failed 不发布。**0 注对 0 行必须通过**。
- ★★★ **豁免必须带证据，`engine-skipped` 不接受**（第六轮）：只认 no-odds / code-outside-domain / unknown-play / nspf-dirty-option / bq-joint-unavailable，每条绑定「允许条件+证据前缀」（`options=0`…）。空证据或证据不符即失败；**被拒的豁免不产生 ignore 键**（否则「拒绝」与「放行」账本表现相同）；`CrossCheck`：no-odds 的场次出了票 → `exempt-violated` 不发布（赔率缺失不许借豁免继续推荐）。
- **历史战绩不照抄「最新轮」**（昨天那注会随今天重算消失），也不能全算多副本。按 (受注日,场次,玩法,档位) 去重：**价格取首份、命中取任一副本、撤注/结算以冻结台账为准**。升级前 `RunId IS NULL` 天然满足，历史不丢。
- ★★ **价格与撤注是两条语义，不能同一次去重**：`MIN(PickId)` 取价会把 `Voided` 冻成首份 → 撤注永远同步不进台账（隔离库实测）。**结算即终态**：`SettledAt` 非空后不许再改撤注。
- 撤注不进命中率分子分母，单列计数（`MilkPlayStat.Voided`）。生产 11-13 因此 9/28=32.1% → 9/27=33.3%（`PickId=10608`）。
- **隔离库 `AiFootBall_AuditTest`**（**保留勿删**）：`milk-isolated-check` 克隆 9 张表结构+补 PK/唯一键/默认值，12 项断言要求**报表与空跑台账逐项相等**；硬编码库名，指向生产库名直接拒绝。

## 六 赔率新鲜度（按链路分开）
- 竞彩 `FbJcOddsHistory`（出票价）：2min 一轮、最大间隔 4min → 数据没坏；`GetLatestAsync` **零 age 守卫**。
- 欧赔 `FbOdds`：各场仅 2–3 采样点、最大间隔 291min；`odds-sample` TIMEOUT（RCSI=0 锁等待，作业静默结束）。
- `MarketTtlHours=8h` 只管亚盘初/终盘；`WatchPoolAsync` 三处不传 `freshHours`。
- 「价涨榜不跟」≠「用了过期价」；数据库超时（生产侧 RCSI）与吃到旧价（消费侧 TTL）**分开处理**。

## 七 训练集与审计（11-13 16:10 部署 `808b13f`，build `14cc0508`）
- 取样双闸：`MatchTime <= DATEADD(min,-150,asOf)` + `<= GETDATE()`；线上取样与 `Fit` 同一 fitMoment；`Fit` 剔队名空/比分>12。
- ★ 150 分钟只是完赛代理：首写延迟仅 1.9% ≤150min、84% >360min → 泄漏**未消除**。
- `ModelVersion`＝`dc1|窗口|n|rho|MVID|w|bf`。

## 八 台账
- `FbPaperTrade` 只增不改、上过榜即记账、首价为准；分母=已结算未撤注。稳胆每日 1 注 91.4%/1.108。注单页 `FbDailyFollow`。

## 九 待办
1. **★ 首次入库时间留痕**（`FbMatch.FirstSeenAt`），否则回测泄漏消不掉。
2. ρ=0 / 全量似然：新时段独立验。
3. 新鲜度（**单独排期**）：JC ~30min、欧赔/必发 3h 都只是待验证阈值。★★ **超龄不得默认退纯模型出票**，应停该玩法+标「数据过期」。
4. RCSI 根治 `odds-sample`（凌晨窗口）。
5. 撤注判据回测 0 注；`PlayOrder`、`GetEuOddsDelta` 未修。
6. P1-7 按 salesDate 冻结模型 + 快照日期键同改。
7. 英锦赛/英联杯无半全场基线；「英锦赛 vs 英锦标赛」未收别名。
8. 部署后：**真实多副本**要在生产再核一次（隔离库只证明逻辑）。
