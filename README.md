# AiFootBall · 彩票与足球数据分析系统

基于 **.NET 8 + SQL Server** 的彩票 / 足球赛事数据采集与分析平台。当前已落库 **10,463 期**彩票开奖、**3,906 场**足球赛事，以及来自 55 家博彩公司的 **欧赔 / 亚盘 / 大小球**赔率（初盘 + 终盘），并提供 REST API、内置 Cron 定时调度与可视化分析面板。

推荐怎么发、74 元票、竞彩停售时间，以 `docs/开发文档总结.md` 第 5 节为准。`交接文档.md` 里的旧场次上限和「低水照发」不要再照做。

## 一、当前数据现状

| 域 | 数据 | 量级 | 时间范围 |
|---|---|---|---|
| 彩票 | 大乐透 | 2,940 期 | 2007-05-30 ~ 2026-10-26 |
| 彩票 | 双色球 | 3,520 期 | 2003-02-23 ~ 2026-10-27 |
| 彩票 | 七乐彩 | 3,003 期 | 2007-01-03 ~ 2026-10-26 |
| 彩票 | 福彩3D / 快乐8 / 排列三 / 排列五 / 七星彩 | 各 200 期 | 近 200 期 |
| 彩票合计 | 8 个彩种 | **10,463 期 / 78,044 个号码** | — |
| 足球 | 赛事 | **3,906 场**（完场 3,245） | 2026-10-22 ~ 2026-10-28 |
| 足球 | 联赛 / 球队 | 412 个 / 6,290 支 | — |
| 赔率 | 欧赔 / 亚盘 / 大小球 | **5 万+ 行**（55 家公司，每场含初盘与终盘） | 随赛事回补 |

## 二、数据来源

| 来源 | 用途 | 说明 |
|---|---|---|
| `www.pinble.com` | 彩票开奖（主源） | 用户指定源。历史页 `tbody#tbody` 内嵌最近 **200 期**，含期号、开奖日期、号码；一次请求即得，无需分页 |
| `datachart.500.com` | 彩票全量历史回补 | pinble 单彩种仅 200 期，大乐透/双色球/七乐彩改由 500 历史接口按 `start/end` 参数一次取全量，并附带销售额、奖池、一/二等奖 |
| `live.500.com` | 足球赛事 | GBK 编码；`wanchang.php?e=日期` 取当日全部完场，`?e=日期` 取当日竞彩赛程（带让球） |
| `odds.500.com/fenxi` | 赔率 | 每场 3 个页面：`ouzhi-{fid}` 百家欧指（初盘/即时 + 返还率 + 凯利）、`yazhi-{fid}` 亚指（初始盘口 + 即时盘口，含主客水位）、`daxiao-{fid}` 大小球；页面底部另有平均值行 |

> 已做交叉校验：大乐透 200 期重叠期号在 pinble 与 500 两个源的号码 **完全一致（0 处差异）**，因此采用「号码以 pinble 为准、销售额/奖池由 500 补齐」的合并策略。

## 三、解决方案结构

```
AiFootBall/
├─ AiFootBall.sln
├─ db/
│  ├─ 01_schema.sql          # 建库 + 建表 + 索引（幂等）
│  └─ 02_seed.sql            # 8 个彩种字典（含 pinble 抓取地址）
├─ docs/系统设计说明.md
└─ src/
   ├─ AiFootBall.Core/       # 领域模型、数据源接口、配置项
   ├─ AiFootBall.Data/       # Dapper 仓储（彩票/足球/赔率/日志）、建库器、指标计算
   ├─ AiFootBall.Sources/    # 三个数据源实现 + 采集编排 + Cron 调度 + 反爬处理
   ├─ AiFootBall.Collector/  # 命令行入口
   └─ AiFootBall.Api/        # 分析 API + 内嵌调度宿主 + wwwroot 可视化面板
```

## 四、快速开始

```bash
# 1) 初始化数据库（自动建库、建表、写彩种字典）
dotnet src/AiFootBall.Collector/bin/Debug/net8.0/AiFootBall.Collector.dll initdb

# 2) 采集彩票（全部彩种；--code dlt 指定单个；--no-backfill 只用 pinble）
dotnet src/AiFootBall.Collector/bin/Debug/net8.0/AiFootBall.Collector.dll lotto

# 3) 采集足球（最近 7 天；--date 2026-10-27 指定单日）
dotnet src/AiFootBall.Collector/bin/Debug/net8.0/AiFootBall.Collector.dll football --days 7

# 4) 采集赔率（最近 7 天；--missing 只补未采集过的；--all 全量回补）
dotnet src/AiFootBall.Collector/bin/Debug/net8.0/AiFootBall.Collector.dll odds --missing

# 5) 启动内置 Cron 调度（前台常驻，Ctrl+C 退出）
dotnet src/AiFootBall.Collector/bin/Debug/net8.0/AiFootBall.Collector.dll scheduler

# 6) 查看数据概览（含赔率覆盖率与初终盘变化统计）
dotnet src/AiFootBall.Collector/bin/Debug/net8.0/AiFootBall.Collector.dll report

# 7) 启动分析服务（面板 http://localhost:5088/ ，Swagger /swagger；调度随服务一起运行）
cd src/AiFootBall.Api/bin/Debug/net8.0
ASPNETCORE_URLS=http://localhost:5088 dotnet AiFootBall.Api.dll
```

> 本机启用了应用程序控制策略（WDAC），直接运行生成的 `.exe` 会被拦截，请统一用 `dotnet xxx.dll` 方式启动。

## 五、API 一览

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/lottery/types` | 彩种字典 |
| GET | `/api/lottery/overview` | 各彩种期数、首末日期、最新期号 |
| GET | `/api/lottery/{code}/draws?take=30` | 最近开奖 |
| GET | `/api/lottery/{code}/frequency?ball=R&take=500` | 频率 / 近30、近100 期 / 当前遗漏 / 最大遗漏 / 平均间隔 |
| GET | `/api/lottery/{code}/analysis?take=200` | 和值分布、奇偶比、连号率、重号、冷热号 |
| GET | `/api/football/overview` | 足球数据总量 |
| GET | `/api/football/matches?from&to&leagueId` | 赛程赛果 |
| GET | `/api/football/league-stats?from&to` | 联赛维度：胜平负、场均进球、大2.5球率、双方进球率 |
| GET | `/api/football/odds-coverage` | 赔率覆盖情况（已采/待采/无数据/失败、各玩法行数、公司数） |
| GET | `/api/football/odds-movement` | 初盘 → 终盘的平均变化（按玩法聚合） |
| GET | `/api/football/odds-signal` | 初终盘变化方向 × 实际赛果（欧赔主胜、亚盘让球赢盘） |
| GET | `/api/football/odds/{matchId}` | 单场赔率：欧赔 / 亚盘 / 大小球，各含初盘与终盘 + 平均值行 |
| GET | `/api/jobs` | 定时作业清单：cron、启用状态、下次运行、上次结果 |
| POST | `/api/jobs/{name}/run` | 手动触发一次作业 |
| GET | `/api/jobs/logs?take=50` | 作业与采集运行日志（dbo.SyncLog） |

## 六、定时任务（内置 Cron）

调度器不依赖外部组件，标准 5 段 cron（分 时 日 月 周），运行记录写入 `dbo.SyncLog`（`TaskName = job:{名称}`），进程重启后按上次运行时间推算下次触发，错过的一次会立即补跑。

| 作业 | 默认 cron | 说明 |
|---|---|---|
| `lotto-daily` | `45 21 * * *` | 每晚开奖后采集 8 个彩种 |
| `football-daily` | `10 8 * * *` | 每天早晨回补最近 3 天赛程赛果 |
| `odds-upcoming` | `*/30 * * * *` | 每 30 分钟刷新未来 30 小时赛事的赔率（跟踪盘口变化） |
| `odds-backfill` | `20 3 * * *` | 凌晨补齐历史缺失赛事的赔率，单次上限 1200 场 |

配置位置：`appsettings.json` 的 `Jobs` 节点；API 里 `Jobs:Enabled=false` 可关闭内嵌调度。

> ⚠️ 赔率采集受站点限流约束，**同一时间只应有一个进程在抓 odds.500.com**。若用 CLI 跑 `odds`，请先把 API 的 `odds-*` 作业置为 `Enabled: false`。

## 七、配置

连接串位于 `Collector/appsettings.json` 与 `Api/appsettings.json`，也可用环境变量覆盖：

```bash
export AIFOOTBALL_CONN="Server=192.168.41.128,1433;Database=AiFootBall;User Id=sa;Password=***;TrustServerCertificate=True;Encrypt=False"
```

采集参数（`Collector` 节点）：

| 参数 | 默认 | 说明 |
|---|---|---|
| `RequestDelayMs` | 800 | 彩票/足球请求间隔 |
| `TimeoutSeconds` | 60 | HTTP 超时 |
| `FootballBackfillDays` | 7 | 足球默认回溯天数 |
| `OddsConcurrency` | 2 | 赔率采集并发赛事数 |
| `OddsBatchSize` | 40 | 每批落库赛事数（越小进度越耐久） |

赔率请求由进程内全局节流器统一排队（基准 900 ms/请求，遇 429 自动降速、连续成功后逐步恢复），并内置 EdgeOne 反爬挑战的纯 HTTP 求解（`__tst_status` / `EO_Bot_Ssid` Cookie 由挑战页数值直接算出，无需浏览器）。

## 八、免责声明

项目仅用于数据统计与技术研究。彩票开奖为独立随机事件，历史频率与遗漏不能预测未来结果，请勿用于非法博彩。
