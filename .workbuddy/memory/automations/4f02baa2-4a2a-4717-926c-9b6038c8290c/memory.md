# 自动化记忆：11-13 周五期出票取证（只读快照）

## 2026-11-12 11:53 首次执行
- 触发时点与任务前提不符：任务按「周五期终版刚停售」写，实际执行在 11-12 11:53，周五期榜面尚未发布。
- 取证结果：@d=2026-11-13 全零（注单 0 / 带戳 0 / 存档 0 / 闸门 0）。
- 原因链：jc 最新 Expect=2026-11-12（周四期），6 场全「欧国联」→ 命中 NoPickLeagues 被全拦（旁证 SyncLog save-steady-picks「名单外 6」）。
- 重要的系统级发现（与日期无关）：FbMilkProbSnapshot 全表 0 行；FbMilkPick 全表 28 行 ModelVersion 全 NULL。写入代码 f89549a（11-12 10:47）+ ae13fb2 才进主干，上线 <1.5 小时，尚无任何出票，写入路径**从未被真实流量验证过**。
- 口径提醒：本次不能替代 11-14 08:55 正式核对。判「存档/版本戳是否生效」必须等 11-13 期真实出票那天。
- 下轮注意：先查 BettingCard 是否已有 Expect=2026-11-13 且非 NoPickLeagues 联赛，否则零行无判据意义。

## 2026-11-12 12:05 第二次执行（补充取证）
- 前提仍不成立：11-13 期榜面未出现。BettingCard 最新 jc 批次 CapturedAt=11-12 11:30:36，Expect=2026-11-12，期号分布止于 2026-11-12（无 2026-11-13）。
- 结果：@d=2026-11-13 依旧全零（注单 0 / 带戳 0 / 存档 0 场 0 玩法 / 闸门 0 / CreatedAt NULL）；全表快照 0 行、注单 28 行版本戳 0 非空。
- 旁证：job:daily-follow-build 12:00:06 明写「无候选注：周四001 土耳其vs比利时：欧国联 属不推名单…」→ 零行属**无输入**。
- 部署已就位：deploy-package/app/api/AiFootBall.Data.dll 11-12 11:06，晚于 f89549a 10:47:37。
- 表名/路径勘误（下轮直接用）：表是 `dbo.BettingCard`（**无** Fb 前缀）、`dbo.SyncLog` 用 `TaskName` 非 JobName；`NoPickLeagues.cs` 在 `AiFootBall.Core/Models/` 而非 `.Sources`。PowerShell 里 `LIKE '%X%'` 会被安全策略误判为 %VAR%，改用 `CHARINDEX`。
- 结论未变：审计层写入路径尚未被真实流量验证，正式判定仍归 11-14 08:55。
