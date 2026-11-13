# 自动化记忆：部署 7ad73b9 并核对运行构建

## 2026-11-13 14:33（首次执行）
- 结果：部署成功，HEAD=7ad73b9；health `build=1.0.0.0+8f11dafc`、uptime 20s→174s；MVID 对比 PASS；`fit-probe` 全 PASS；`milk-check` FAIL 但归因于既存环境问题。
- 可复用判据：
  - `[Reflection.Assembly]::LoadFile(...).ManifestModule.ModuleVersionId` 在本机被安全策略拦截，**不能**用来读 MVID。
    改用纯 PowerShell 字节解析：PE 头(0x3C)→COFF 节表→可选头(PE32 +96 / PE32+ +112，CLI 目录 idx14=+112)→CLI 头(RVA映射)→元数据根。
    元数据根字段偏移：Signature@0、Major@4、Minor@6、Reserved@8、**Length@12、版本串@16**、Flags、Streams、流头。
    `#GUID` 流首 16 字节即 MVID（用 `[Guid]::new(byte[])` 还原）。
  - 反向校验办法：同法读 `deploy-package\logs\api-dll-backup-*` 的旧 dll，MVID 必须与新包不同（否则解析器是恒值）。
  - `fit-probe` 的版本戳自带 Core MVID，可与 health 的 build 后缀互为第三重证据。
- 环境背景：`milk-check` 长期 FAIL 源为 token-health（500.com cookie 降级、熔断）+ `job:odds-sample` 整点 TIMEOUT；两者与部署无关，
  且 `ABORTED` 不计失败，正常重启不会把这项刷红。
- 已知偏差：任务描述声称「21:58 停售之后」，实际触发时钟为 14:33。本次按「先部署并如实标注时间」处理。
- 待复验：16:40 榜面是否出现真实日期窗口、SPF/NSPF 全档、覆盖 3 场、新注版本戳带新 MVID。

## 2026-11-13 14:42（二次触发，同 HEAD 家族）
- HEAD=`95c2cc1`，仅改 memory md（diff --stat 两文件），代码==`7ad73b9` → 判定「以它为基础」，允许部署。
- 结果：部署 28s 成功（备份 `api-dll-backup-20261113-144209`，PID 25884，启动 14:42:10）；
  health `build=1.0.0.0+8f11dafc` uptime 21→119；MVID8=`8f11dafc` **PASS**；`fit-probe` 全 PASS 且数字与首轮逐项相同；`milk-check` 仍 FAIL（同两条环境问题）。
- ★★ 新增判据：**「备份旧 dll MVID 必须不同」的反向校验只对跨版本部署有效**。同一提交重复部署时（确定性编译）备份 MVID 与新包相同，
  不能据此判失败；此情形用 PID/StartTime + uptimeSeconds + fit-probe 戳内 MVID 三件套代替。
- ★ 判 FAIL 归属口径：milk-check 的 90 分钟窗口以 `StartedAt` 起算，报告 FAIL 时必须回查 `Earliest` 是否早于本次重启时刻，
  再决定写「部署导致」还是「既存环境问题」；且部署后 SyncLog 为空 ≠ 作业健康，只能说无新增失败。

## 2026-11-13 15:34（三次触发）
- HEAD=`95c2cc1`（仍仅 memory md，零代码）→ 允许部署；PID=25832，启动 15:34:47，监听 5088。
- health `build=1.0.0.0+8f11dafc`，uptime 20→147（127s 墙钟，线性）→ MVID8 一致 **PASS**；`fit-probe` 六项全 PASS。
- milk-check 仍 FAIL，唯一窗内失败 `job:odds-sample` 15:05→15:15 TIMEOUT，**早于重启 19 分钟** → 既存环境问题。
- ★ 备份 dll SHA256 与新包**完全相同**（同提交确定性编译），再次印证反向校验只对跨版本有效。
- ★ MVID 解析：**RVA 映射封成 PowerShell function 会返回错值（`streams=0`）**，必须内联循环；`#GUID` 流首 16 字节即 MVID。
- 三次结果完全一致，可作为「同一提交重复部署」的稳定基线：`8f11dafc` / 178292 / 空 0 / n=178179 / asOf 162701 剔 15478。

## 2026-11-13 15:44（四次触发，目标提交 90a8fa0）
- HEAD=`595d1cc`（= 90a8fa0 + memory docs），代码零差异 → 放行；部署 23s，PID 20520 启于 15:44:25。
- health `build=1.0.0.0+b425a886`、uptime 20→93；MVID8=`b425a886` **PASS**；**备份旧 dll=8f11dafc 且 SHA256 不同 → 首次真正跨版本部署，反向校验恢复有效**。
- `fit-probe` 六项全 PASS：178290 / 空 0 / 越界 0 / 戳 `2025-05-13..2026-11-13` + `b425a886` / 二b 逐字相同 / 进训练集 0 / 剔 2 场。
- ★ 新基线数字（此前均为 178292/178179/剔 15478）：现 178290/178177/剔 15476，三项同时 −2 —— `<=@asOf` 双闸把 2 场未来场次从**取样**里也挡掉了，恒等式仍成立。以后拿旧基线判「异常」会误报。
- `milk-check` 仍 FAIL：窗内唯一失败 `job:odds-sample` 15:05→15:15 TIMEOUT，早于重启 29 分钟，既存问题。
- ★ 判读补充：`newest stamp` 的 `?..?`+`4456b82d` 在每次部署后都会出现且属预期残留（旧构建最后一条注单），**不能当回归**；今日 1 注/1 记账即那一行，明日 0 属受注日未到。

## 2026-11-13 15:49（五次触发，幂等复验 → **未重复部署**）
- 判据落地：HEAD=`595d1cc`（含 90a8fa0，零代码 diff）+ build 后缀与包内 dll MVID 一致 + uptime 319>300 → 三项全中即跳过。结果全中，进程仍 PID 20520（启于 15:44:25），未重启。
- ★ 修正记忆中的 MVID 解析口径（此前记录有误，照抄会静默返回全 0 GUID）：
  - `cliDir = opt + (PE32?96:112) + 14*8` → PE32 **opt+208**、PE32+ **opt+224**；把基址当目录项会读到 Export（RVA=0）。
  - health/版本戳后缀 = MVID **前** 8 位（`b425a886-6c35-…`），不是后 8 位。
- `fit-probe` 全 PASS，数字与 15:44 基线逐项相同（178290/0/0、n=178177、剔113、rho−0.060、二b 逐字同、进训练集0、剔2场）→ 该基线可长期作「未回归」参照。
- `milk-check` FAIL 唯一源仍是 `job:odds-sample` 15:05 TIMEOUT（早于重启 39 分钟）；`job:exchange`/`jc-odds-sample` 的 ABORTED 由脚本排除，不计数。每次部署都会新增 1~2 条 ABORTED，属预期。
- 周五期 11 场实测全禁推（8 欧国联 + 3 国际赛），16:40 后 0 注预期成立；真实出票看周六期。

## 2026-11-13 15:57（六次触发，幂等复验 → **未重复部署**）
- 三项判据全中：HEAD=`595d1cc`（含 90a8fa0）+ 包内 dll MVID=`b425a886`（health 后缀同）+ uptime 641>300。进程仍 PID 20520（15:44:25），包内 Core.dll 写入时间 15:44:11、最新备份 15:44:24 → 证实无部署。
- ★★ 新发现（重要判读前提）：**工作区此刻有未提交代码改动**（另一会话在做 `asOf` 收紧为「开赛+150 分钟」：`BettingRepository.cs` / `ScorelineModel.cs` / `Program.cs` / 新增 `RhoStudy.cs`+测试，mtime 15:52–15:53），15:55 有一次并发的 `dotnet test -c Release` 重建了本地 Release。
- ★ 因此 `fit-probe` 的版本戳 MVID 变成 **`cec1b74d`（≠ 生产 b425a886）**，探针跑的是本地脏构建，不是生产构建。**不能凭戳后缀不等就判 FAIL**；要同时看 `git status` 是否有未提交改动。
- 该轮六项仍全 PASS：178290 / 未来 0 / 越界 0 / 戳 `2025-05-13..2026-11-13` n=178177 rho−0.060 / 二b 逐字同 / 进训练集 0 / 剔 2 场 ↔ 威冠联、萨尔超。
- ★ 但第三节数字变了：`传 asOf 162634、被剔 15543`（旧基线 162701/15478，差 65）——正是未提交的「+150 分钟」闸把截止日前后刚开赛的场次也剔了，**是改动的预期效果，不是回归**。以后看第三节必须连同工作区是否脏一起读。
- `milk-check` FAIL 唯一源 `job:odds-sample` 15:05:13→15:15:13 TIMEOUT（早于重启 29 分钟，RCSI=0 已知项）；`audit write-back` 两行：WARN 1 注 `?..?` 旧残留（预期）、OK picks=1 snapshots=48 stamped=1；`newest stamp` 仍 `?..?|n=178179|rho=0.010|4456b82d` 旧构建残留。
- 台账实测：`FbPaperTrade` 2026-11-13 = 1 注（禁推联赛 0 注）；今晚竞彩 11 场 = 欧国联 8 + 友谊赛（竞彩叫「国际赛」）3，全在 `NoPickLeagues`。

## 2026-11-13 16:00（七次触发，**本次真的部署了**）
- HEAD 前进到 `03193f8`，其中 `3613725` 改生产代码（BettingRepository SQL 收紧为「开赛+150min ≤ asOf」、ScorelineModel 加 MatchDurationMinutes/WithRho/Score）→ 跳过条件不成立，部署。
- 16:02 部署：备份 `api-dll-backup-20261113-160208`（旧 `b425a886`，SHA 不同）；PID 13976 启于 16:02:09；health `1.0.0.0+4bf3194b`，uptime 28→206；包内 MVID `4bf3194b-…` PASS。
- `fit-probe` 六项全 PASS，新基线 178289/113/178176、三节 162633 剔 15543、五节进训练集 0、七节剔 2 场（威冠联/萨尔超）；rho 由 −0.060 跳 **0.000**（样本仅少 1 场）→ 再次证明 rho 不可当判据。
- `milk-check` FAIL，唯一 FAIL 仍是 `job:odds-sample` 15:05:13 TIMEOUT（早于重启 57 分钟，既存）。
- ★ 方法判据（下次直接照做）：
  1. **`--no-build` 不能信**：deploy 不重建 Backtest 项目，首跑戳是旧 Release（本次 `cec1b74d`）。先 `dotnet build src/AiFootBall.Backtest -c Release` 再跑，否则版本戳不等于生产。
  2. MVID 解析用「BSJB + 搜 `#GUID` 名字、namePos-8 取 offset」，手工流表对齐会错位读到 DOS stub（假值 `905a4d00-…`）。
  3. 跳过条件三件套仍有效，但必须先看 `git log` 新增提交是否碰生产代码，别只看 HEAD 哈希家族。
- 附注：`deploy-history.log` 新功能写出 `head=unknown pid=System.Object[]`（日志 bug，未改代码）。
