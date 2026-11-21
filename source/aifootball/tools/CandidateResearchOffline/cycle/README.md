# 未见验证持续循环（T=1.25）

日期标记：**2026-11-21 第一版部署后等400场记录**

完整说明见：`docs/2026-11-21-第一版部署后等400场记录.md`

- `UnseenValidationCycle.ps1` — 导出→预测归档→附果→status  
- `schedules/Install-UnseenCycleTask.ps1` — 安装任务（需授权；本机已装）  
- `sql/` — SELECT-only 查询  
- `cycle.local.example.json` — 配置样例（勿提交真实连接串）

旧 `schedules/install-research-bridge.ps1` 已废弃。
