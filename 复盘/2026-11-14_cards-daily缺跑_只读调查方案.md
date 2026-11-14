# cards-daily 16:30 缺跑：只读调查方案

日期：2026-11-14 17:00
状态：**仅方案，未执行任何查询**。等确认后再查。
约束：只读、不改作业、不补跑、不重启。

---

## 0. 已知事实（本文档的唯一前提，不再重新论证）

| 事实 | 来源 |
| --- | --- |
| `cards-daily` 定义：cron `30 8,11,16,20 * * *`、`Type=cards`、`Enabled=true` | `Api/jobs.json:109-113`；生产 `jobs.json` 哈希 `F02FC00E` 与部署前逐字节相同（15:16 部署记录） |
| `cards` **不在** `DetachedJobTypes`、**不在** `RecommendJobTypes` → 走 `_mainLane`，与 500 系列（odds-sample / exchange / water-sample / inplay-sample / finalize-prices / jc-odds-backfill）**同一条串行通道** | `JobScheduler.cs:545-548`、`:687-690`、`:701-` |
| `cards` 超时 30 分钟，硬上限 35 分钟；超了看门狗取消排队并写 `STUCK` | `JobScheduler.cs:111`、`:143-150`、`:328-352` |
| 错过就不补：`InitializeAsync` 从「现在」算下一次，不按上次运行时间补欠账 | `JobScheduler.cs:172-184` |
| `case "cards"` **无条件返回 Ok**：三板块各 0 场也记 SUCCESS | `JobScheduler.cs:1062-1072` |
| 取数失败被吞：`GetGbkAsync` 拿到 `(html, reason)` 后**丢弃 reason**，只回 null | `BettingCardSource.cs:682-687` |
| 空列表不写库：`UpsertCardsAsync` 在 `list.Count == 0` 时直接 `return 0`，不动 `CapturedAt` | `BettingRepository.cs:208-211` |
| 受注场次读取口径 = `MAX(CapturedAt)`（"最近一整批"） | `BettingRepository.cs:251-262` |
| 存在第二条写入路径：API 手动端点也调 `CollectCardsAsync` | `Program.cs:1197`；采集器另有 `Collector/Program.cs:266` |
| 观测三件套（全部 GET / SELECT）：`/api/jobs`、`/api/jobs/logs`、`/api/odds/pressure` | `Program.cs:777`、`:878`、`:903` |

现象：今天 16:30 无 `job:cards-daily` 执行记录；`BettingCard` 当前批仍停在 **15:25:15**（21 场）。15:25:15 不是任何计划时刻。

---

## 1. 要区分的是三件事，不是一件事

缺跑有三种完全不同的成因，**处置完全不同**，因此调查必须先定性再谈修复：

- **A 调度问题**：作业压根没被排上/被取消。特征：SyncLog 里没有结论行，或只有 `STUCK`。
- **B 日志问题**：作业跑了、也没报错，但"成功"这个结论是假的，没有任何一行能说明它取到 0 场还是没取到。
- **C 采集/入库问题**：取数拿到了但解析成 0 行，或解析到了但没落到 `BettingCard`。

本项目的既有坑正好落在这三类的交界上：`cards` 恒 Ok（B 的来源），`GetGbkAsync` 丢 reason（B 的来源），空列表不写库（B 与 C 同形）。所以**只看 `/api/jobs` 的 SUCCESS 无法区分 B 和 C**，必须靠 SyncLog 的 `RowsAffected` + `BettingCard.CapturedAt` 两侧对照。

---

## 2. 证据清单（按执行顺序，全部只读）

### E1 配置覆盖（最先查，因为它最静默）

页面上改过的时间会写进 `job-schedules.json` 并**盖过** `jobs.json`（`JobScheduleStore.cs:5`、`:24-31`）。若生产那份文件里 `cards-daily` 被关掉或 cron 不含 16，则一切解释得通，且不会留下任何运行日志。

- 读 `deploy-package/app/api/job-schedules.json`：文件是否存在、`cards-daily` 条目的 `cron` / `enabled`。
- GET `/api/jobs`：`cards-daily` 的 `cron`、`nextRun`、`lastRun`、`lastStatus`、`lastMessage`。

判读：
- `enabled=false` 或 `cron` 不含 16 → **A-配置**，定案，后面的不用查。
- `nextRun` 已是 20:30、`lastRun` 停在 11:30 之前、`lastStatus` 为空 → 16:30 那次没入队（转 E5 判断是否重启错过）。
- `lastStatus=STUCK` 且 `lastMessage` 以 `[queued]` 开头 → **A-拥堵**（转 E3 取占据者）。
- `lastStatus=SUCCESS` 且 `lastRun≈16:30` → 转 E2/E4 判 B/C。

### E2 SyncLog 结论行（核心证据）

```sql
SELECT LogId, TaskName, Source, StartedAt, FinishedAt,
       DATEDIFF(SECOND, StartedAt, FinishedAt) AS Secs,
       Status, RowsAffected, Message
FROM dbo.SyncLog
WHERE TaskName = 'job:cards-daily' AND StartedAt >= '2026-11-13'
ORDER BY LogId DESC;
```

四种形态对应四个结论，互斥：

| 观测 | 结论 |
| --- | --- |
| 无 16:30 行 | 未入队（A：重启错过 / 被跳过）或未收尾（A：进程中断） |
| `STUCK` + `[queued]` | A-通道拥堵（排队 35 分钟未轮到） |
| `STUCK` + `[hung]` | A-执行卡死（取数挂在网络上） |
| `RUNNING` 无 `FinishedAt` | A-进程在写库前被杀（对照 E6 是否 15:16 起未重启） |
| `SUCCESS` + `RowsAffected=0` | **B/C：跑了但 0 场**，且日志层把"0 场"写成成功 |
| `SUCCESS` + `RowsAffected>0` 而库无新批 | **C-入库** |

### E3 通道占用（回答"是不是被堵死"）

`cards` 与 500 系列同通道，而 16:00–16:40 恰好是 `odds-sample`（55 分钟 recheck、可达十几分钟一轮）、`exchange`、`water-sample`、`inplay-sample` 密集时段。必须看 16:30 那一刻通道里是谁。

```sql
SELECT LogId, TaskName, Status, StartedAt, FinishedAt,
       DATEDIFF(SECOND, StartedAt, FinishedAt) AS Secs, LEFT(Message, 120) AS Msg
FROM dbo.SyncLog
WHERE StartedAt BETWEEN '2026-11-14 16:00' AND '2026-11-14 17:00'
ORDER BY StartedAt;
```

判读：是否存在一条从 16:30 之前一直连续占到 16:35+ 的长链（每条 >10 分钟即为强证据）。若 16:30–16:40 通道被占满且 `cards` 有 `[queued]` 行，A 成立，且**根因是通道容量而非 cards 本身**——这与 `daily-review` 当年被挤掉的形态完全一样（`JobScheduler.cs:697-699`）。

### E4 数据层（入库是否发生）

```sql
SELECT Board, COUNT(*) AS Rows, MIN(CapturedAt) AS MinCap, MAX(CapturedAt) AS MaxCap
FROM dbo.BettingCard
WHERE CapturedAt >= '2026-11-13'
GROUP BY Board;

SELECT CapturedAt, COUNT(*) AS Rows
FROM dbo.BettingCard
WHERE CapturedAt >= '2026-11-13'
GROUP BY CapturedAt
ORDER BY CapturedAt DESC;
```

判读：
- 批次时刻序列 = 真实写入时刻。凡是不落在 8:30/11:30/16:30/20:30 的批次（如 15:25:15），**必来自另一条写入路径**（E7）。
- 16:30 前后完全无新 `CapturedAt` 且 E2 为 `SUCCESS/0` → 与"取到 0 场、没写库"一致。
- `CapturedAt` 有新批但 `Rows=1` 之类 → MERGE 键 `(Board, Expect, MatchNo)` 撞车（`BettingRepository.cs:219`），属 C-入库。

### E5 进程日志

当天文件按日期命名：`deploy-package/logs/api-<yyyy-MM-dd>.log`（`boot-api.bat` 用 `%DATE:/=-%`；中文系统日期格式不同会得到别的文件名，先 `ls logs/` 找当天那份）。

在 16:20–16:45 段找三类行：
- `==== boot ... ====`：说明 16:30 附近进程重启过（重启即"错过不补"）。
- `[看门狗] 作业 ... 长时间未返回`：与 E2 的 STUCK 互为佐证。
- 异常堆栈 / `HttpRequestException` / `timed out`：说明取数挂住而非没跑。

注意：`case "cards"` 不往 `_log` 写逐板块细节，日志里**不会**有"竞彩 0 / 北单 0"这种字句，那只在 SyncLog 的 `message` 里。日志段为空本身就是 B 的证据，不要当成"没跑"。

### E6 进程存活与重启史

- `Get-Process -Id <pid>` 的 `StartTime`：若仍是 15:16 那个进程（部署记录 PID 14696），则 15:16 起未重启，**排除"重启错过"**。
- 只读导出计划任务定义：`schtasks /query /tn AiFootBall-API /v /fo LIST`（看 Last Run Time / Last Result / Restart on failure）。

### E7 15:25:15 那批是谁写的（第二条写入路径定名）

15:25 不属于任何一个 cron 时点，它证明 `BettingCard` 至少有两个生产者。这条不查清，后面所有"批次时刻"的推断都不可靠。

- SyncLog 15:20–15:30 有无 `Source`/`TaskName` 含 `card` 的行；有无**非 `job:` 前缀**的行（手动端点/采集器是否写日志）。
- 若查不到任何痕迹 → **B-日志缺口**成立：有一条能改受注场次的路径不写作业日志。这是本次要单列的缺陷，不是"顺手修"的项。

### E8 反爬与节流（只能作辅证）

GET `/api/odds/pressure`：`throttleMs` / `http428` / `http429` / `challenge` / `verify`。

- `verify > 0` 或 429 显著上升 → 采集层被拦的**辅证**。
- 但计数是**进程累计、重启清零**，且 cards 与欧赔共用同一 `OddsHttp`，无法归因到 cards 那一次。**不得单独用它定因**。

### E9 对照基线（判断"是不是部署引起的"）

```sql
SELECT TaskName, CAST(StartedAt AS DATE) AS D, COUNT(*) AS N,
       SUM(CASE WHEN Status = 'SUCCESS' THEN 1 ELSE 0 END) AS Ok,
       SUM(CASE WHEN RowsAffected = 0 THEN 1 ELSE 0 END) AS Zero
FROM dbo.SyncLog
WHERE TaskName = 'job:cards-daily' AND StartedAt >= '2026-11-08'
GROUP BY TaskName, CAST(StartedAt AS DATE)
ORDER BY D DESC;
```

判读：
- 只有 11-14 16:30 缺，8:30/11:30 都有 → 与今天 15:16 部署 + 15:25 有人写库这条时间线强相关。
- 8:30、11:30 也长期缺/长期 0 场 → 是长期形态，与部署无关（周六 8:30 本就常空，工作日 11:00 才开售）。

---

## 3. 判定树（命中即止，不许跳步）

```
E1 配置被覆盖?
 ├─ 是 → A-配置（job-schedules.json 覆盖）
 └─ 否
     E2 16:30 有行吗?
      ├─ 无行
      │   ├─ E5 有 boot 行 → A-重启错过（错过不补，需改策略而非补数据）
      │   ├─ E3 通道被占满 / lastMessage 含 [queued] → A-拥堵
      │   └─ 都无 → A-未入队且无痕（调度器 bug，需另立题）
      ├─ STUCK → A-拥堵 / A-卡死（看 [queued] 还是 [hung]）
      ├─ RUNNING 无收尾 → A-进程中断
      └─ SUCCESS
          ├─ RowsAffected=0 → 取数为空
          │    ├─ E8 verify>0 → C-反爬拦截
          │    ├─ E4 无新批 + E7 有第二路径 → B/C 混合（日志把空采集写成成功）
          │    └─ 站点当时确实无场次 → 非缺陷，但"SUCCESS"仍具误导性
          └─ RowsAffected>0 而 E4 无新批 → C-入库（MERGE 键/事务/连接串）
```

---

## 4. 只读自证（做完要能证明没动过东西）

1. 只执行 `SELECT` 与 `GET`；DB 一律走 `tools\db.ps1 -Query`（禁 Bash 跑 sqlcmd）。
2. **绝不** `POST /api/jobs/{name}/run`（会真的出票/写榜），**绝不** `PUT /api/jobs/{name}`。
3. 不启停 `AiFootBall-API` 计划任务、不重启进程。
4. 前后各取一次指纹，作为"未改动"的证据：`jobs.json`、`job-schedules.json`（若不存在则记录"仍不存在"）、`BettingCard` 的 `COUNT(*)` 与 `MAX(CapturedAt)`。
5. 结束时明确说明：SyncLog 会因其他作业正常运行而自然增长，**不能**用"SyncLog 无新增"来自证只读。

---

## 5. 已知盲区（提前说清，避免查完给不出结论）

- 若 16:30 那次真的没入队，SyncLog 里就是空白，没有任何一行能证明"它本该跑"。这类"缺席"只能靠 `/api/jobs` 的 `nextRun` 跳变 + 日志 boot 行**间接**推断，属于弱证据。
- `GetGbkAsync` 丢弃的 `reason` 无法事后恢复：当时究竟是 428、429、JS 挑战还是连接超时，**查不出来**。这就是要把"日志缺口"单列的原因。
- 只读调查无法回答"当时 500 的 jczq 页面上到底有没有在售场次"。要回答只能现在再去取一次站点页面——那是外部访问，见第 6 节第 3 问，需单独批准。

---

## 6. 待确认（不确认不执行）

1. **调查窗口**：只看今天 15:00–17:00，还是连 11-08 起的基线一起拉（E9）？建议后者，否则分不清"今天缺"和"一直缺"。
2. **是否允许读计划任务定义**（`schtasks /query`，只读）用于判定重启史？不含任何写操作。
3. **是否允许对 `trade.500.com` 做一次不写库的站点只读取回**，仅用于回答"当时/现在该页有无在售场次"？这会打外网、可能被反爬计数，故单列等批准。**默认不做**。
4. **输出形式**：只出一份结论（A/B/C 定性 + 证据行号），还是同时给出"要不要修、修哪一处"的建议清单（不动代码，仅供下次排期）？

---

## 7. 与本次部署的关系（声明边界）

本次部署（`44c17ba`）已按批准范围完成、首轮发布成功，**本方案不改变该结论**。`cards-daily` 缺跑在部署前是否存在（E9 基线可答），是本次调查要回答的第一个问题；在拿到基线前，不得把它归因于部署。
