# 自动部署：冻结市场权重（178b781）上线

## 2026-11-11 首跑 — 成功

- 触发 21:37，早于脚本预设的部署窗口（dll 须晚于 22:00、今晚停售 21:58），故等到 22:01:30 才执行 `deploy-api.ps1`。
- HEAD `46ac183`（178b781 后的 docs 提交）；deploy 退出码 0，dll 时间戳 22:01:55、762368 字节，与本次 publish 的 SHA256 一致。
- 三项校验全过：dll 晚于 22:00；`/api/health` 200 / ok / stalled=false / tick 3.7s（复检 9.1s）；任务 Running、PID 19988 → 9812。
- 未触发任何重算：`FbMilkPick` 当日仍 6 行（BQ4 / JQ2），`MAX(CreatedAt)=17:22:48`，与部署前逐行一致。
- 遗留观察：`LastTaskResult=2147946720`（Stop→Start 残留码，非启动失败）；备份在 `deploy-package\logs\api-dll-backup-20261111-220203`。

## 2026-11-11 22:05 二次触发 — 只复检，未重启

- 同任务重复触发（首跑已在 22:01:55 完成部署）。在线状态已满足三项验收，故**跳过 `deploy-api.ps1`**，只做只读复检。
- HEAD `8f3adfe`（178b781 为祖先）；dll 22:01:55；health 200/ok/stalled=false，tick 3.8~19.3s；任务 Running、PID 9812。
- 榜面未变：`FbMilkPick` 6 行（BQ4/JQ2），`MAX(CreatedAt)=17:22:48`；22:00:16~22:00:32 的 `*/10` 作业均为旧进程、早于部署。

## 复用要点
- 该类任务排程应直接定在 22:05 之后，避免「触发早于部署窗口」需要空等。
- 校验顺序：dll mtime → health JSON → 任务态与 PID → 榜面行数与 CreatedAt 对比。
- `schedulerTickSecondsAgo` 实测在 4~19s 波动，**不要按「个位数」判**；判据是多次取样全部 <60s 且 stalled=false。
- 重复触发时先复检在线状态，已达标就不要再跑部署脚本，避免无谓重启。
