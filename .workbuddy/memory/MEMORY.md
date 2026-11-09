# AiFootBall 长期记忆（运维手册）

> 2026-11-09。判据全文与实测数字在 `判据库.md`（§1-26），逐日过程在 `2026-11-0X.md`。本文件只留每天要用的。

## 0 速查
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40/11:40/16:40/20:40。
2. 查库 `& tools\db.ps1 -Query "..."`；**禁 Bash 跑 sqlcmd、禁 Bash 调 powershell**；ps1 纯 ASCII。
3. 改代码必 `.\deploy-api.ps1`，不重启=旧 dll；测试 `dotnet test tests\AiFootBall.Tests`。
4. **模型没有 alpha**（§22 Brier .5995 vs 市场 .5812；§25 四方向全输、让球信息量为零）。别再调参假装优化。
5. **EV 落在返奖率 ±8 点内 = 判定等于挑口径**；基线 EV 超过返奖率 → 几乎一定是基线给高了。
6. 返奖率 SPF .878 / BQ·JQ .797 / BF .710 = 任何一注的 EV 天花板。
7. **开赛=票已买**，只能 `Voided` 不能 DELETE；「取消」判据必须开赛 150 分钟后。
8. 权威源 `FbPaperTrade`（只增不改），不是 `FbMilkPick`（重算即删未结算行）。
9. 战绩口径：同场同玩法换档**只算最后发布那档**（`PaperTradeMath.FinalVersion`）。
10. `NoPickLeagues`（欧国联/友谊赛/球会友谊/青年）连单关都不发；`NoParlayLeagues`（澳超）能单关不进串。两者不可合并。

## 一 运维铁律
- 采样 jc-odds 2min / water 10min / odds-late 20min；**turn-guard `*/10` 只配 Api**；`paper-freeze` `*/10`、`paper-settle` `40 * * * *`。
- **Collector 与 Api 各写一份 `new RecommendationService(...)`**：加可选依赖两边同改 + `scheduler --once save-recommendations` 验证。
- **`_recommendLane` 名单**（写当日榜/结算/只读写本库）：save-recommendations、turn-guard、refresh-near-kickoff、paper-settle、settle-hourly、settle-purchases、settle-recommendations、paper-freeze、daily-follow-build、daily-follow-settle。理由：① 都是 DELETE+INSERT 且库上无唯一约束，交错会写成两份；② 留在主通道会被 water-sample（110 秒/轮）、odds-sample-late 饿死。**新增纯库作业一律进这条通道。**
- **完场不信 `Status`**（重采会洗回 0）：`比分齐全 && (Status==3 || MatchTime<=now-150min)`。**受注日 ≠ 开赛日**（22:00 后记次日）。
- 部署包 wwwroot 与 `src/*/wwwroot` 是两份，deploy 会拷；验证前端用 `grep -c`（`Get-Content -Raw`+`-match` 在 PS5.1 失配）。手动跑作业 `POST /api/jobs/{name}/run`。

## 二 数据口径
- RecResult '3'主/'1'平/'0'客；`Handicap` **正=主受让、负=主让球**（净胜+hcp，禁 ABS）。
- **PnlIdx = Pnl/Volume×100；负 = 该方向被买重、庄家在该结果亏钱**。只有 **≤−60** 有否决力；−37~−56 是噪音。
- 串：BQ/JQ 2串 .636→3串 .507→4串 .404；比分 3串 .357。**半全场 3 串甜点、比分不串、总进球 3 串封顶。**
- **`HalfHomeScore IS NOT NULL` 本身就是「有真半场」**；真足球约 20% 场次半场=全场。

## 三 判据（全文见判据库 §1-26）
- **唯一活着的方向**：竞彩单边砸价 ≥10% 而必发不跟 = 该侧被高估（客 −17.0 点，13 场）；主胜同形态**不成立**（+3.4，只打标）。
- 主胜 <1.35 + 必发 ≥70% = 反向，**只限主胜侧+欧战**；强队让胜被买到 1.6x 以下 = 只赢球不赢盘，配让平(3.4~3.6)。
- 胜平/负平**永远同价**（552/552），价格里没方向；**负平 EV .740 永不购买**。
- 体能只标注不扣分（方向是反的）；模型只看进失球、看不见「赢不了球」；BQ 按概率排序=选中价格被压最狠那档。
- 半全场基线**必须按赔率条件化**（§20 三张表，边际平均作废）。
- 现行闸门：比分**停发**、半全场 ≤8 倍、总进球 ≤10 倍、EV 带 **[返奖率×1.00, ×1.45]**（2261 个观测验证全局最优，§26）。
- 命中率三参照组（同一批 420 场）：现引擎 243 注 **14.4%/.893**；跟市场最看好档 420 注 **35.0%/.826**；只买 3 倍以内 265 注 **42.3%/.841**。**三者回报全 <1 = 换筛选口径不能把负期望变正。**

## 四 告警闭环（曾被骂三次，细节见判据库 §27）
- 病根：`turn-guard` 写 `FbTurnAlert` 却无人写回 `FbMilkPick`；告警只在监控 tab；不分有利/不利。**已落地**：`Voided/AlertSeverity` 回写、去重键 (AlertDate,MatchId,PlayType,OptionCode)、`high`→撤注不删行、`mid` 只红标、new high 立刻重算、榜顶红条+撤榜展示区、只读诊断 `/api/milk/turn-guard/scan`。
- ★★ **撤注判据不许用 EV 绝对值**：出票闸管「值不值」（`PassesBaselineEv` + `MaxBaselineEdgeRatio=1.45`），撤注闸只管「市场明确反对」（假热 / PnlIdx≤−60 / ≥2 条同向）。**两道闸门槛必须同源**（委托 `MilkPickEngine.Payout`），改判据先确认字段真 SELECT 进来了。

## 五 自检与性能（坑详见判据库 §28-29）
- `tools\milk-check.ps1`（只读、必须纯 ASCII）每次出榜后跑；**必须带 `?date=`**，超时给 90s。
- **token 反复被拦是常态**（自动续期），别当故障。
- `FbJcOddsHistory` 531 万行：走 `IX_JcOddsHistory_NoPlayOpt`，按场/档查询才快（903ms→4ms）。
- **11-09 已开 RCSI + 恢复模式 SIMPLE**：`odds-sample` 10 分钟被杀 → 113 秒；日志 8264MB→1032MB。SIMPLE **无 PITR**（原本也无），01:00 全备留 5 份在 `D:\AiFootBall_DA_BAK`。
- **`/api/health` 带调度器心跳**：>300 秒无 tick 返 **503**。写日志处禁 `CancellationToken.None`，一律 30 秒上限。

## 六 空跑台账 + 撤榜
- `FbPaperTrade` 只增不改：`paper-freeze`（`*/10`）、`paper-settle`（`40 * * * *`）、`FreezeMissingAsync(7)` 兜底。
- ★★ **冻结口径=一旦上过榜就记账，以首次上榜价为准**（只冻终版会漏：放弃某注是整行 DELETE 不留 Voided；早场注开赛后即移出榜面）。`FreezeDayAsync` 幂等（先 UPDATE 同步撤注/告警，再 `INSERT … WHERE NOT EXISTS`）。**漏掉的注不许用近似价补，宁缺勿假。**
- 三条硬口径（`PaperTradeMath`，12 条测试钉住）：①分母=已结算**且未撤注**注数 ②回报=命中赔率合计 **÷ 注数** ③撤注单独计数 `Pnl` 记 NULL。**分母永远是注数不是赔率合计**。门槛 `EnoughSettled=150`。
- **撤榜展示区**：`voided`=告警撤注（带原因/时间）；`replaced`=曾上榜后被重算换掉（只有台账留痕，取 `MIN(TradeId)` 首次冻结行）。`GET /api/milk/voided?date=` 用台账对榜面做差集，**不另建表**。
- ★★ **`/api/milk/picks` 过滤必须区分当日/历史**：`if (!explicitDate) list = list.Where(SettledAt IS NULL || Voided)`；此前一律过滤已结算 → 历史恒 0 行并连带 6 条自检假 FAIL。

- **北单台账 `FbBdSpfPick` 已能结算**（11-09 补，`bd-spf-settle` `10,40 * * * *` **只配 Api**；部署脚本不拷 appsettings，要手改 deploy-package 那份）。口径 `BdSpfSettlement`：净胜=主−客+**北单让球数**（禁用 FbMatch.Handicap 竞彩盘），走盘=让平把两侧都杀掉。★ 台账 `Odds` 是**推荐时点 SP**，实购终版差 10~46%，ROI 只看方向不是账本。首日 41 注 24 中 58.5%/1.145。

## 七 稳胆（2026-11-09 上线，实测与口径见判据库 §30）
- 独立表 `FbSteadyPick`、独立作业 `save-steady-picks`（`50 8,11,16,20`）/ `steady-settle`（`15 * * * *`），都进 `_recommendLane`。
- 口径：竞彩 SPF 三档去水 → 取最高 → 赔率 ≤1.75 → 剔均衡局（<40%）→ 全天后按赔率升序取前 3。**不掺模型、不看必发。**
- 实测（382 注可出，33 个比赛日）：每日前 3 → **98 注 77.55% / 0.993**；前 1 → 90.9%（33 注）。
- **改参数必须重跑 `steady-check`（线上引擎）与 `steady-probe`（统计），两者必须逐字一致**，否则口径已分叉。
- 定位：**命中导向，不是期望导向**。回报仍在返奖率 0.878 附近，不许对外说它赚钱；与大奶主榜分开记账。

## 八 待办
1. 撤注判据回测（需 150 条 `BaselineEv` 已结算样本，现 0/150）
2. 九档 **620 场**（10-05 起，≈17.7 场/天）→ 3000 场需约 **5 个月**；500 只留 30 天，回补不到更早
3. `PlayOrder` 从未回测
4. 备份表 `FbMatch_HalfBackup_20261108`(405832 行) + 三张 `FbMilkPick_bak*` 待清
5. `SyncLog` 只写 `FinishedAt`、从不写 `StartedAt`，单次作业耗时无法回溯
