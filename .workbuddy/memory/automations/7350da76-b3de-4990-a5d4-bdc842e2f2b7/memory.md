# 大奶榜基线 EV 自检（自动化）

## 任务
只读运行 `tools\milk-check.ps1`，确认出榜后「基线 EV」没有消失。禁止任何会重写台账的命令
（dotnet run -- recommend / scheduler --once / POST /api/milk/refresh）。

## 执行记录

### 2026-11-07 23:17
- 结果：exit 1，FAIL。
- 明细：schema 4 列齐全；board date=2026-11-07 rows=9 counterHigh=1 lowConfidence=0 withBaseline=0。
- FAIL 内容：CounterHigh 1/1 注缺 BaselineEv；接口 baselineEv 键缺失（脚本判为 WARN，因库内无基线值）。
- 定性：库内只有 11-06(6 注)、11-07(9 注) 两天，基线全 NULL。11-07 这 9 注 CreatedAt 均为 22:51:30
  （事故快照批量恢复），属已知情况，非新 bug。无 11-08 榜，无法验收本轮改动。
- 旁证：接口 200，含 counterHigh / modelLowConfidence 键；源码 engine/repo/api 三处改动均在位；
  dll 于 23:12 部署、dotnet 进程 23:12:52 启动（下一轮出榜跑的是新代码）。
- 结论口径：对用户只报「今天这 9 注是事故快照，天生没基线；表没缺列、接口没丢字段；真验收在 11-08 首榜」。

### 2026-11-08 08:56
- 结果：exit 1，FAIL，但**基线守卫本身通过**。
- board date=2026-11-08 rows=8 counterHigh=1 lowConfidence=0 overconfident=0 withBaseline=1。
- PASS 项：5 列齐全（脚本已扩到含 ModelOverconfident）；单列注带基线（曼联 vs 维拉 BQ 胜负 @29.00 / BEv 0.777）。
- FAIL 3 条，同一根因 = 「模型高估」标记未上线（不是基线丢失）：
  ① 7 注 Ev 超玩法天花板未打标（BQ cap .837 / JQ .837 / BF .745）；② 接口无 modelOverconfident 键；③ 接口无 evCap 键。
- 根因证据：源码 4 文件 mtime 08:54（MilkPickEngine/Program/MilkPick/MilkPickRepository），Release 编译 08:55；
  deploy-package\app\api 仍是 00:06:28 的 dll，dotnet 进程 00:07:04 启动 → **改了没部署没重启**。
  08:40 榜由旧进程产出，故 7 注无标。
- 已知情况排除：最新日期已推进到 11-08，11-07 那 9 注不在本次检查范围，非事故快照遗留。
- 三条整改验收全部达标：BEv 0.777∈[0.757,0.916]；赔率 29.00≥12（不再是 15~17 固定档）；
  低可信判据 MProb 2.28 ≥ 2.68×0.70=1.876 → 标志位 0 正确。
- 处置：**未部署**（本任务只读；且并发有会话正在改这块，避免抢跑）。建议 11:40 出榜前部署 4 个 dll + 重启 API；
  08:40 那 8 注不补标（重跑会 DELETE 重写台账，铁律禁止）。

## 复跑要点
- 判定分支靠 board date：若最新日期仍为 2026-11-07 → 已知情况；若已推进到 2026-11-08 且仍 FAIL → 改动未生效。
- 若脚本给 "no CounterHigh and no ModelLowConfidence row" → 通道未触发，不可下结论。
