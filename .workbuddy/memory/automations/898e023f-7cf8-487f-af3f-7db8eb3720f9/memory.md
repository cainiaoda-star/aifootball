# 自动化执行记忆（11-13/11-14 审计层只读核对）

## 2026-11-12 11:50 首次执行
- 只读核对 11-13 / 11-14 两日的 FbMilkPick、FbMilkProbSnapshot、ModelVersion，并跑 milk-check.ps1。
- 结果：两日 0 注、0 存档、版本戳全 NULL；milk-check 报 PASS（audit write-back: no picks today）。
- 判为第⑤类「没有进入计算的场次」（预期）：jc 赛程卡最新批仍是 Expect 2026-11-12 的周四期 6 场全欧国联，
  11-13/11-14 无任何 jc 批次、无竞彩赔率；NoPickLeagues 覆盖欧国联。
- 副产物：发现两个口径问题（存档按写入日记而非受注日；三处写入空 catch 静默），已记入当日工作日志。
- 后续正式取证时点仍为 11-13 22:20 与 11-14 08:55，本次不作结论变更。

## 2026-11-12 12:01 第二次执行（结论 FAIL，第⑤类）
- 11-13 / 11-14 仍 0 注、0 存档；**FbMilkProbSnapshot 全表 0 行（建表至今从未写入）**；FbMilkPick 全表 ModelVersion 全 NULL（4 个受注日 28 注）。
- 归类第⑤类，三项证据齐：FbMilkGate 两日各 0 行；jc 最新批 CapturedAt=11-12 11:30:36 共 6 场 周四001-006、赛时 11-13 01:00~03:45、联赛全「欧国联」、无周五/周六前缀批次；NoPickLeagues.Names 原文含 "欧国联"。
- 排除①/②/④：job:save-recommendations 11-12 08:40 与 11:40 均 SUCCESS、Message「漏斗 jc 0」，未抛异常。
- 口径修正（重要）：**表名是 dbo.BettingCard，不存在 FbBettingCard**；11-13 01:00 那 6 场属周四期（受注日 11-12），不会落到 PickDate='2026-11-13'。
- milk-check 报 PASS，但 audit write-back 是「no picks today」空过，不能算审计层通过。
- 部署面：deploy-package\app\api\AiFootBall.Sources.dll 时间 11-12 11:06:50，早于 11:40 那次作业，说明审计代码已在部署产物里，只是无输入未执行。
- 下次取证：11-13 11:40 后（周五期开售并被采集）或 11-13 22:20 / 11-14 08:55。
