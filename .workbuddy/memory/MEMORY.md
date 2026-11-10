# AiFootBall 长期记忆（运维手册）

> 全文在 `判据库.md`（§1-35），过程在 `2026-11-0X.md`。本文件只留每天要用的。

## 0 速查
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40/11:40/16:40/20:40。
2. 查库 `& tools\db.ps1 -Query "..."`；**禁 Bash 跑 sqlcmd、禁 Bash 调 powershell**；ps1 与 **.sql 探针**都要纯 ASCII（ANSI 读，中文注释会吃掉语句）。
3. 改代码必 `.\deploy-api.ps1`（不重启=旧 dll）；`dotnet test tests\AiFootBall.Tests`；手动跑作业 `POST /api/jobs/{name}/run`。
4. **模型没有 alpha**（Brier .5995 vs 市场 .5812）。别再调参假装优化。
5. **EV 落在返奖率 ±8 点内 = 判定在挑口径**；基线 EV 超返奖率 → 几乎一定基线给高了。
6. 返奖率 SPF .878 / BQ·JQ .797 / BF .710 = 任一注的 EV 天花板。
7. **开赛=票已买**，只能 `Voided` 不能 DELETE；「取消」必须开赛 150 分钟后才判。
8. 权威源 `FbPaperTrade`（只增不改）；`FbMilkPick` 重算即删未结算行，不可当账本。换档只算最后发布那档，**跟单评价只认 `RoiFinal`**。
9. `NoPickLeagues`（欧国联/友谊赛/青年）连单关都不发；`NoParlayLeagues`（澳超）能单关不进串。

## 一 运维铁律
- 采样：jc-odds 2min / water 10min / odds-late 20min；**turn-guard `*/10` 只配 Api**；`paper-freeze` `*/10`。
- **Collector 与 Api 各写一份 `new RecommendationService(...)`**：加可选依赖两边同改 + `scheduler --once save-recommendations` 验证。
- **_recommendLane**（清单见 `JobScheduler.RecommendJobTypes`）：写当日榜的都是 DELETE+INSERT 且库上无唯一约束，交错会写两份；留主通道会被 water-sample（110 秒/轮）饿死。**新增纯库作业一律进这条。**
- **完场不信 `Status`**（重采会洗回 0）：`比分齐全 && (Status==3 || MatchTime<=now-150min)`。**受注日 ≠ 开赛日**（22:00 后记次日）。
- 部署包 wwwroot 与 `src/*/wwwroot` 是两份；**验证前端用 `grep -c`**（`-Raw`+`-match` 在 PS5.1 失配）。**部署脚本不拷 appsettings**，计划两处手改。
- ★★ **引用新列的 SQL 必须自己先补列**：`SettleAsync` 用 `p.Handicap` 而补列只在 `SaveDayAsync` → 结算先跑就报「列名无效」。

## 二 数据口径
- RecResult '3'主/'1'平/'0'客；`Handicap` **正=主受让、负=主让球**（净胜+hcp，禁 ABS）。
- **PnlIdx = Pnl/Volume×100；负 = 该方向被买重、庄家在该结果亏钱**。只有 **≤−60** 有否决力；−37~−56 是噪音。
- **`HalfHomeScore IS NOT NULL` 本身就是「有真半场」**；真足球约 20% 场次半场=全场。
- 串：BQ/JQ 2串 .636→3串 .507→4串 .404。**半全场 3 串甜点、比分不串、总进球 3 串封顶。**
- **`BettingCard.Expect` 必须填受注日**（11-10 修）：jc `data-processdate`、bd `scheduleDate`（原只 sfc 填，键退化成 `(Board,MatchNo)`，「周二001」每周重号**原地覆盖**；全 NULL 时唯一约束形同不存在）。

## 三 判据
- **唯一活着的方向**：竞彩单边砸价 ≥10% 而必发不跟 = 该侧被高估（客 −17.0 点，13 场）；主胜同形态**不成立**（+3.4，只打标）。
- **价格漂移（185 场）**：追热整体 **0.754 = 负 alpha**；把冷门买热 **0.519（z−2.6）**。**只当否决规则**。
- 主胜 <1.35 + 必发 ≥70% = 反向，**只限主胜侧+欧战**；强队让胜被买到 1.6x 以下 = 只赢球不赢盘，配让平(3.4~3.6)。胜平/负平**永远同价**；**负平 EV .740 永不购买**。半全场基线**必须按赔率条件化**；体能只标注不扣分。
- 现行闸门：比分**停发**、半全场 ≤8 倍、总进球 ≤10 倍、EV 带 **[返奖率×1.00, ×1.45]**。

## 四 告警闭环
- 已落地：去重键 `(AlertDate,MatchId,PlayType,OptionCode)`、`high`→撤注不删行、`mid` 只红标、new high 立刻重算、榜顶红条、诊断 `/api/milk/turn-guard/scan`。
- ★★ **撤注判据不许用 EV 绝对值**：出票闸管「值不值」（`PassesBaselineEv`+`MaxBaselineEdgeRatio=1.45`），撤注闸只管「市场明确反对」（假热/PnlIdx≤−60/≥2 条同向）。**两闸门槛必须同源**；改判据先确认字段真进了 SELECT。
- ★★ **回写不许按 `p.PickDate=@d` 过滤**（11-10 修，此前从未生效）：`PickDate`=出注那一刻，`GetOpenDayAsync` 不按日期捞 → 0 点后通宵场次（03:00 那批）告警写了、监控页看得见，**推荐页永不撤注**。现只靠 `(MatchId,PlayType,OptionCode)`+`SettledAt IS NULL`，告警/清理/existing 全放宽 ±1 天。
- ★ 11-05~08 的旧告警三列全 NULL，**不可当链路证据**。
- ★★ **旁路 catch 不许静默**：「表没建」与「SQL 写错」长得一样，都是**作业全绿 + 台账停摆**。已改 `SidecarErrors` → SyncLog 带 `!!`。**改完必须故障注入验证**。

## 五 自检与性能
- `tools\milk-check.ps1`（只读、**纯 ASCII**）每次出榜后跑，超时 90s。**11-10 加 4d 告警没撤注 / 4e `Expect` 为空 = FAIL，4f token 续期无效 = WARN**。
- **已开 RCSI + SIMPLE**：`odds-sample` 10 分钟被杀 → 113 秒。SIMPLE **无 PITR**，01:00 全备留 5 份。索引坑见判据库 §28-29。
- **`/api/health` 带调度器心跳**：>300 秒无 tick 返 **503**。写日志处禁 `CancellationToken.None`，一律 30 秒上限。
- ★ **判中函数漏玩法是静默的**（11-10 修）：`IsHit` 原只有 BF/BQ/JQ，SPF/NSPF 落 `default:false` → 什么比分都算「没中」，命中率被压低且台账无异常。现加 `decimal? handicap` + `Handicap` 快照列；**缺让球数必须 continue，判 false = 记成未中**。
- ★★ **采集层「沉默失败」**（11-10 修）：①`SampleOddsAsync` 状态硬编码、`RowsAffected` 填场数，`CollectOddsAsync` 内层 FAILED 却 `return Ok` → `job:` 永远 SUCCESS；现 `AllFailed(done,failed)`（部分失败仍成功、`done=0` 是闲时）+ `Fail(rows,msg)`，`StatVerify` 报增量。②「续期成功」只证明 cookie 被写，11-09 报 **41 次成功**而数据等到 **08:05**；现 `RefreshProvenIneffective`（续期后又被拦 ≥30）+ `token-health/FAILED`，冷却期不空试。
- ★ **夜间欧赔 10 小时黑洞**：`FbOddsHistory` 每天首条 08:02~08:05、末条 22:05；凌晨 2~7 点开赛 **738 场**赛前 3 小时采样 **0**（欧战通宵场，30 天 24 注里 9 注在 2~3 点）。**`MarketTtlHours=12` 把 22:05 的盘当现盘算 `EuDelta`**（未修）。

## 六 空跑台账 + 终版价
- `FbPaperTrade` 只增不改：`FreezeMissingAsync(7)` 兜底。
- ★★ **冻结口径=一旦上过榜就记账，以首次上榜价为准**（只冻终版会漏：放弃某注整行 DELETE 不留 Voided；早场注开赛后移出榜面）。**漏的注不许近似价补。**
- 三条硬口径（`PaperTradeMath`，12 条测试钉住）：①分母=已结算**且未撤注** ②回报=命中赔率合计 **÷ 注数** ③撤注单列 `Pnl=NULL`。门槛 150 注。
- ★★ **`/api/milk/picks` 过滤要区分当日/历史**：`if (!explicitDate) Where(SettledAt IS NULL || Voided)`；原一律过滤已结算 → 历史恒 0 行 + 6 条假 FAIL。
- **北单台账可结算**（`bd-spf-settle`）：净胜=主−客+**北单让球数**（禁用 FbMatch.Handicap），走盘=让平杀两侧。**45 注复算 0 差异。**
- ★★ **终版价**：三表加 `FinalOdds/FinalAt/FinalSource`，**不改 `Odds`**。竞彩取 `FbJcOddsHistory` 开赛前最后采样（`jc-close`，**按 MatchId**）；北单取 `FbBdHistory.ResultSp`（未中赔付为 0，故精确）。作业 `finalize-prices`（`*/20`）+ `bd-history`（`25 5 * * *`）。★ 停售那刻 `Odds=1.00` 占位：取价 `>1.0`，先作废 `FinalOdds<=1.0` 自愈。

## 七 稳胆
- `FbSteadyPick`，`save-steady-picks`（`50 8,11,16,20`）/ `steady-settle`（`15 * * * *`）。
- 口径：SPF 三档去水 → 取最高 → ≤1.75 → 剔均衡局(<40%) → 全天按赔率升序取前 3。**不掺模型、不看必发。**
- 实测（382 注）：前 3 → **98 注 77.55% / 0.993**；前 1 → 90.9%。放宽 3.0 掉 57.1%，**禁止为填满而放宽**。
- **改参数必重跑 `steady-check`/`steady-probe`，逐字一致**。定位**命中导向**，0.878 附近，不许说赚钱。

## 八 待办
1. 撤注判据回测（需 150 条 `BaselineEv` 已结算样本，现 0/150）
2. 九档 **620 场** → 3000 场需约 **5 个月**；500 只留 30 天。
3. `PlayOrder` 从未回测；`SyncLog` 无 `StartedAt`，单次作业耗时无法回溯
4. **「冷门被买热 → 该侧不买」待复现**：52 注 z−2.6，需第二批独立窗口。
5. 北单开奖 SP 靠 `bd-history` 补采；**盯 `FinalCoverage` 别掉下 95%**。
6. 夜间欧赔缺口 → `MarketTtlHours=12` 把旧盘当现盘；降 TTL 需回测（连带必发判据）。
7. `FbMatch_HalfBackup_20261108` + 三张 `FbMilkPick_bak*` 待清。
