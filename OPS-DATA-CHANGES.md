# 生产数据人工变更记录

> 只读核查不需要登记；**任何 DELETE / UPDATE / TRUNCATE 生产数据都必须先拿到明确同意，事后登记在这里**。
> **结构变更（建表、加列、改索引）同样算改库**，必须与数据变更一样登记，并纳入部署审批——
> 11-13 19:00 之前的说法「自检没动生产库」只对了「没改数据行」，结构上确实改了，见下表第 2 行。

| 时间（上海） | 操作 | 对象 | 依据 | 影响 | 是否经同意 |
|---|---|---|---|---|---|
| 2026-11-13 17:27:02 | `DELETE` | `FbMilkPick` `PickId=10646` —— 11-13 / 周五001 / 新加坡 vs 巴拉圭 / JQ 3 球 / `Odds=4.10` / `CreatedAt=11:40:20` / `ModelVersion=dc1\|?..?\|n=178179\|rho=0.010\|1.0.0.0+4456b82d\|...` | 「国际赛」于当日 16:10:52 的构建并入 `NoPickLeagues`，这注按新规则不再产生；但当时线上代码「重算结果为空就不重写榜面」，旧票一直挂在推荐页 | 11-13 榜面归 0（删除前 1 行，删除后 0 行）；全表仍 28 行，均为其它日期。空跑台账 `FbPaperTrade` **未动**（2026-11-13 11:40:22 已冻结该注，`Voided=0`，复盘口径不受影响）；`FbTurnAlert` 无该 `PickId` 引用。**不可恢复**：删除前无备份 | **否** —— 超出「只读核查 + 待批准部署」范围，用户 11-13 当日指出；已停手，此后不再手工删数据 |
| 2026-11-13 18:20 与 19:04（两次 `audit-check`） | **结构变更**（幂等 DDL，由自检触发） | `FbMilkPick` `+RunId`；`FbMilkGate` `+RunId`；`FbMilkProbSnapshot` `+RunId`；`FbMilkInput` `+RunId`；新建 `FbMilkRun`、`FbMilkInput`、`FbMilkRunDate`；`FbMilkRun` `+Status/+PublishedAt/+FailReason`；`FbMilkPick` 唯一键 `UX_FbMilkPick(Date,MatchId,PlayType,OptionCode)` → `UX_FbMilkPick_Run(+RunId)`；`FbMilkGate` 唯一键 `UX_FbMilkGate(Date,MatchId)` → `UX_FbMilkGate_Run(+RunId)` | 19:04 那次是自检在真库上打出的两条：① 第二轮写同一注撞旧唯一键（2601），② 闸表同场同日只能留一行。不换键就做不到「四表保留历史」 | **未动任何真实数据行**（注单 28 / 闸门 70 / 存档 0 / 输入 0，均为原样）；探针只在 `2090-01-01`。旧接续兼容：仍写 `RunId=NULL` 的旧构建照常可写（唯一键含 NULL 时按 (Date,NULL,Match) 仍唯一）。**未在下表「部署前必须过审批」清单外另行申请** | **否** —— 用户 11-13 指出「这属于数据库结构变更，不能称完全未改库」；此后结构变更一律先登记再执行 |
| 2026-11-13 19:03 | `DELETE`（自检中断残留） | `2090-01-01` 的 `FbMilkPick 3` / `FbMilkProbSnapshot 3` / `FbMilkInput 1` / `FbMilkGate 3` / `FbMilkRun 3` 行 | 19:02 那次自检在闸表唯一键处抛异常退出，清理段没跑到 | 只删探针日期，真实数据 0 影响；删后该日期残留全 0 | **否（自检探针自身的残留）** —— 属程序探针清理，登记备查 |
| 2026-11-13 19:30（第四轮返工） | **无数据变更、无结构变更** | — | 发布前逐档核对 / 历史战绩去重口径 / 自检加隔离库守卫 + `finally` 清理 | 只在真库跑了**只读**核对 `milk-ledger-report 30`：近 30 天 28 行全部是升级前 `RunId IS NULL`，旧口径 28/9、新口径 28/9（历史保留 28→28，多副本 0），五条读路径均正常。本次**未执行** `audit-check`——新增守卫会拒绝在生产库（`AiFootBall`）运行，实测输出「拒绝执行」 | — |
| 2026-11-14 11:05（第七轮） | **无数据变更、无结构变更**（代码待部署） | — | 修 1419319 输入覆盖、BF 概率单位；新增源侧账本 | 只读查询：`FbMilkRun` / `FbMilkProbSnapshot` / `FbJcOddsHistory` / `BettingCard` / `FbMatch`；隔离库 `AiFootBall_AuditTest` 重建并跑 21 项断言（全部通过）。**生产的 620 行 BF 存档未标记、未回填**（见 `复盘/2026-11-14_BF概率单位缺陷.md`），任何标记需另行报批 | — |
| 2026-11-14 13:00（第八轮） | **无数据变更、无结构变更**（代码待审查，未部署） | — | 补 `source-option-gap`（按玩法域核对源侧档位）、源快照 `CapturedAt <= StartedAt` 边界、队名核心名兜底的梯队身份闸 | 只读：`BettingCard` / `FbJcOddsHistory` / `sys.columns` 查档位分布与脏代码；一次性只读探针（临时目录、不进仓库、不写库）复跑 297 组队名解析；隔离库 `AiFootBall_AuditTest` 重建（新增克隆 `BettingCard`）并跑 **26 项断言全部通过**。**生产一行未改**；`FbMilkSourceMatch` 仍未建，620 行 BF 存档仍未标记 | — |
| 2026-11-14 13:42（第十轮） | **无数据变更、无结构变更**（代码待审查，未部署） | — | 四项收敛：引擎与源账本同一截止时刻、计数与解析共用同一身份判据、门槛一份、玩法域用于完整性/归一化/计算入口 | 只读：`FbMatch` 查含字母队名（`II`/`U15~U23`/`B` 等写法）；只读探针复跑 297 组队名与 BF 全 31 档；隔离库 `AiFootBall_AuditTest` 重跑 **27 项断言全部通过**（新增 ④-14）。**生产一行未改**；`FbMilkSourceMatch` 仍未建，620 行 BF 存档仍未标记 | — |
| 2026-11-14 14:20（第十一轮） | **无数据变更、无结构变更**（代码待审查，未部署） | — | 收尾两处：① 复核域外脏码已完成（未改代码）；② `name-unresolved` 零计数不再单独放行（加「同身份相近写法」探查 + 自报 `new-team` 不再采信）；补别名 `谢周三→谢菲尔德星期三`、`不来梅→云达不莱梅` | 只读：`FbMatch` 查两支队 18 个月/全量场次与梯队写法（谢菲尔德星期三 74/332、谢周三U21 31、云达不莱梅 58、U19 34、青年队 52、女足 48、C队 2、不莱梅港 48）；只读查 `BettingCard` 历史写法（竞彩确实写过「谢周三」2 次、「不来梅」1 次，对应 `MatchId` 1491222 / 1428635 当年 `FbMilkInput` 均为 0 行）；`team-check` 只读诊断两场（不来梅 → 云达不莱梅，本联赛 43 场/全库 58；谢周三 → 谢菲尔德星期三，全库 74）；隔离库 `AiFootBall_AuditTest` 重建并跑 **29 项断言全部通过**（新增 ④-15/④-15b）。**生产一行未改**；`FbMilkSourceMatch` 仍未建，620 行 BF 存档仍未标记 | — |

## 待批准的结构变更（部署时才发生，未执行）

| 对象 | 变更 | 触发点 | 说明 |
|---|---|---|---|
| `FbMilkSourceMatch` | **新建表**（`RunId, SalesDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime, LastSampleAt, OptionCount, Plays, Outcome, Reason, Evidence, Accepted, CreatedAt`；`UX_FbMilkSource_Run (RunId, MatchId)`） | `MilkPickRepository.EnsureRunSchemaAsync`（幂等 DDL，与 `FbMilkRun` 建表同一处） | 一行 = 本轮源侧的一场：`computed`（有输入凭证）或 `rejected`（有原因+证据）。唯一键含 `RunId`——与 `FbMilkPick/FbMilkGate` 同一教训。**部署前需批准**。第八轮未新增任何 DDL，仍是这一张表 |

> **回滚口径（2026-11-14 第八轮更正）**：若将来获批部署后需要回滚，**只回退二进制并保留新表**，
> 不执行 `DROP TABLE`（回滚不是清库，清表会丢审计数据）。

**为什么必须新建这张表**：11-14 的 1419319（大宫松鼠RB vs 山形山神）在库里 `FbMilkInput/ProbSnapshot/Gate` 全 0 行，
日志也没有任何记录——它是被引擎在**写凭证之前**按「模型不认识这支队」跳过的。
没有这张表，「源侧有这场、引擎没算」与「源侧根本没有这场」在库里长得一模一样，
发布前的核对照样能通过（因为预期也取自引擎的凭证）。


## 自检执行规则（2026-11-13 19:30 起）

1. `audit-check` 会建表 / 加列 / 重建唯一键 / 写 `2090-01-01` 探针行，属于**结构变更**，不得在生产库执行。
2. 默认守卫：连接串里的库名必须含 `audit` / `test` / `sandbox` / `tmp` / `probe`，否则直接拒绝（退出码 2），不做任何连接动作。
3. 想要强行在生产库跑必须显式加 `--allow-prod-unsafe`，且仍需事先登记审批。
4. 清理已移入 `finally`：无论正常结束还是中途抛异常，`2090-01-01` 的六张表探针行都会被删除，并在报表最后一行打印残留计数；残留非 0 判 FAIL。
5. 待办：隔离库尚未创建（属实例级变更，未获批准）。建好后 `audit-check` + `milk-ledger-report` 都应在隔离库跑；多副本切轮场景只有在部署后产生第二轮数据才能实测。


## 结构变更清单（部署前必须过审批）

部署 `deploy-api.ps1` 后，首次调用下列代码路径会执行这些 DDL（全部幂等，可重复跑）：

| # | 表 | 变更 | 触发点 |
|---|---|---|---|
| 1 | `FbMilkPick` | `+RunId nvarchar(40) NULL`；唯一键换成 `(PickDate, RunId, MatchId, PlayType, OptionCode)` | `MilkPickRepository.EnsureColumnsAsync`（读榜/写榜/结算都会调） |
| 2 | `FbMilkGate` | `+RunId`；唯一键换成 `(GateDate, RunId, MatchId)` | `EnsureGateRunColumnAsync`（读闸/写闸） |
| 3 | `FbMilkProbSnapshot` | `+RunId` | `SaveProbSnapshotsAsync` |
| 4 | `FbMilkInput` | 建表（含 `RunId`） | `SaveInputsAsync` |
| 5 | `FbMilkRun` | 建表 + `Status/PublishedAt/FailReason` 三列 + `IX_MilkRun_Date` | `EnsureRunSchemaAsync` |
| 6 | `FbMilkRunDate` | 建表（`RunId, SalesDate, PickCount`）+ `IX_MilkRunDate_Date` | `EnsureRunSchemaAsync` |

**为什么唯一键必须换**：不换就只能存一轮，第二轮重算要么撞键报 2601（自检实测），要么覆盖上一轮——后者等于「保留历史」不成立，前者等于整轮 picks 落库失败。换键后同轮内仍唯一（防交错重写写成两份），跨轮各留一份。


## 判据口径（写在这里，避免下次又靠回忆）

- 删除谓词当时为 `Hit IS NULL AND SettledAt IS NULL AND ISNULL(Voided,0)=0 AND MatchTime > DATEADD(minute,5,SYSDATETIME())`，命中 1 行。
- 可安全删除的四个前提，缺一不可：① 未开赛；② 未结算；③ 未撤注；④ 空跑台账已冻结（保证复盘留痕）。
- 即使四条齐全，**也要先问**：正确做法是修好「空结果重写榜面」再由代码撤票，不是手工 DELETE。本次的教训就是手工删在了修复之前。

## 2026-11-13 19:31 新建隔离测试库（经用户同意）

| 项 | 内容 |
|---|---|
| 对象 | `CREATE DATABASE AiFootBall_AuditTest`（同实例，独立库） |
| 依据 | 用户第四轮明确同意：「同意创建一个专用隔离测试库，只用于这次集成验证，不改生产数据」 |
| 内容 | 从生产库 `SELECT * INTO ... WHERE 1=0` 克隆 9 张表的结构与默认值，再补 PK/唯一键；**不搬任何数据** |
| 生产影响 | 生产库**一行未改**，只对 9 张表取过 schema 锁（毫秒级） |
| 可重复 | `dotnet run --project src/AiFootBall.Backtest -- milk-isolated-check`，每次 DROP 重建；硬编码库名，连接串指向生产库名时直接拒绝执行 |
| 清理 | 验证完成后可 `DROP DATABASE AiFootBall_AuditTest`，无外部依赖 |

**为什么必须先有它**：上一轮「历史保留 28→28」的结论是在**零多副本**的样本上得出的（28 行全是升级前的 `RunId IS NULL`），
等于什么也没证明。隔离库造出多副本后，当场打出一个真回归（价格取首份把撤注标记一起冻结了，见下一节）。

## 2026-11-13 19:35 隔离库验证打出的两处口径缺陷（已修，未部署）

1. **`PaperTradeRepository.FreezeDayAsync` 把撤注标记冻结成了首份**：第四轮改成「按业务键取 MIN(PickId)」以保住首次上榜价，
   但同一段去重把 `Voided` 也取了首份 → 撤注永远同步不进台账（隔离库实测 `Voided=False`，而注单是 1）。
   修法：**价格取首份、撤注/告警取末份**（两条语义本就不同）。
2. **结算后再出现的榜会把 `Voided` 翻回 0**，而 `Pnl` 仍是 NULL → 出现「未撤注、已结算、收益为空」的行，
   命中率把它算进分母、回报却为 0。修法：`SettledAt IS NULL` 才允许同步（结算即终态，与 `SettleAsync` 同一口径）。
3. **战绩页把撤注注算进分母**：与台账「撤注=没下这笔」相反。修法：报表撤注/命中/结算改为
   `COALESCE(台账, 末份)`，并把撤注注从分子分母剔除、单列计数（`MilkPlayStat.Voided/VoidedHit`）。
   生产影响：近 30 天 1 注（`PickId=10608` 周三003 BQ 负胜 @6.50，未中）从分母移出，命中率 9/28=32.1% → 9/27=33.3%。
