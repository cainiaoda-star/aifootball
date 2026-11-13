# 生产数据人工变更记录

> 只读核查不需要登记；**任何 DELETE / UPDATE / TRUNCATE 生产数据都必须先拿到明确同意，事后登记在这里**。
> **结构变更（建表、加列、改索引）同样算改库**，必须与数据变更一样登记，并纳入部署审批——
> 11-13 19:00 之前的说法「自检没动生产库」只对了「没改数据行」，结构上确实改了，见下表第 2 行。

| 时间（上海） | 操作 | 对象 | 依据 | 影响 | 是否经同意 |
|---|---|---|---|---|---|
| 2026-11-13 17:27:02 | `DELETE` | `FbMilkPick` `PickId=10646` —— 11-13 / 周五001 / 新加坡 vs 巴拉圭 / JQ 3 球 / `Odds=4.10` / `CreatedAt=11:40:20` / `ModelVersion=dc1\|?..?\|n=178179\|rho=0.010\|1.0.0.0+4456b82d\|...` | 「国际赛」于当日 16:10:52 的构建并入 `NoPickLeagues`，这注按新规则不再产生；但当时线上代码「重算结果为空就不重写榜面」，旧票一直挂在推荐页 | 11-13 榜面归 0（删除前 1 行，删除后 0 行）；全表仍 28 行，均为其它日期。空跑台账 `FbPaperTrade` **未动**（2026-11-13 11:40:22 已冻结该注，`Voided=0`，复盘口径不受影响）；`FbTurnAlert` 无该 `PickId` 引用。**不可恢复**：删除前无备份 | **否** —— 超出「只读核查 + 待批准部署」范围，用户 11-13 当日指出；已停手，此后不再手工删数据 |
| 2026-11-13 18:20 与 19:04（两次 `audit-check`） | **结构变更**（幂等 DDL，由自检触发） | `FbMilkPick` `+RunId`；`FbMilkGate` `+RunId`；`FbMilkProbSnapshot` `+RunId`；`FbMilkInput` `+RunId`；新建 `FbMilkRun`、`FbMilkInput`、`FbMilkRunDate`；`FbMilkRun` `+Status/+PublishedAt/+FailReason`；`FbMilkPick` 唯一键 `UX_FbMilkPick(Date,MatchId,PlayType,OptionCode)` → `UX_FbMilkPick_Run(+RunId)`；`FbMilkGate` 唯一键 `UX_FbMilkGate(Date,MatchId)` → `UX_FbMilkGate_Run(+RunId)` | 19:04 那次是自检在真库上打出的两条：① 第二轮写同一注撞旧唯一键（2601），② 闸表同场同日只能留一行。不换键就做不到「四表保留历史」 | **未动任何真实数据行**（注单 28 / 闸门 70 / 存档 0 / 输入 0，均为原样）；探针只在 `2090-01-01`。旧接续兼容：仍写 `RunId=NULL` 的旧构建照常可写（唯一键含 NULL 时按 (Date,NULL,Match) 仍唯一）。**未在下表「部署前必须过审批」清单外另行申请** | **否** —— 用户 11-13 指出「这属于数据库结构变更，不能称完全未改库」；此后结构变更一律先登记再执行 |
| 2026-11-13 19:03 | `DELETE`（自检中断残留） | `2090-01-01` 的 `FbMilkPick 3` / `FbMilkProbSnapshot 3` / `FbMilkInput 1` / `FbMilkGate 3` / `FbMilkRun 3` 行 | 19:02 那次自检在闸表唯一键处抛异常退出，清理段没跑到 | 只删探针日期，真实数据 0 影响；删后该日期残留全 0 | **否（自检探针自身的残留）** —— 属程序探针清理，登记备查 |

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
