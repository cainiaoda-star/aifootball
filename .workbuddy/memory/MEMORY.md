# AiFootBall 长期记忆（运维手册）

> 全文 `判据库.md`（§1-40），过程见每日 md

## 0 速查
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40/11:40/16:40/20:40。
2. 查库 `& tools\db.ps1 -Query "..."`；**禁 Bash 跑 sqlcmd、禁 Bash 调 powershell**；ps1 与 .sql 探针纯 ASCII。
3. 改代码必 `.\deploy-api.ps1`；`dotnet test`；手动跑 `POST /api/jobs/{name}/run`。
4. **模型没有 alpha**（Brier .5995 vs 市场 .5812），别调参假装优化。
5. **EV 落在返奖率 ±8 点内 = 在挑口径**；基线 EV 超返奖率 → 基线给高了。
6. 返奖率 SPF .878 / BQ·JQ .797 / BF .710 = 任一注 EV 天花板。
7. **开赛=票已买**，只能 `Voided` 不能 DELETE。
8. 权威账本 `FbPaperTrade`；`FbMilkPick` 重算即删未结算行，不可当账。
9. `NoPickLeagues`（欧国联/友谊/青年）不发单关；`NoParlayLeagues`（澳超）不串。

## 一 运维铁律
- 采样：jc-odds 2min / water 10min（每 3 轮全量，余只欧赔）/ exchange-late 10min / odds-late 20min / turn-guard·paper-freeze `*/10`。
- **Collector 与 Api 各写一份 `new RecommendationService(...)`**：加可选依赖两边同改。
- **_recommendLane**：写当日榜的都是 DELETE+INSERT 且库上无唯一约束，交错写两份。**新增纯库作业一律进这条**；手动触发也走通道。
- **完场不信 `Status`**：`比分齐全 && (Status==3 || MatchTime<=now-150min)`。**受注日 ≠ 开赛日**。
- **部署脚本不拷 appsettings**（且被 gitignore）→ **新增作业只存在于本机**，换机静默消失。
- ★ `SyncLog` 显式写 `StartedAt`；STUCK 分 `[hung]`/`[queued]`。
- ★★ **只在结束时写 SyncLog = 半路死掉零痕迹**（12:21 的回补 12:40 被重启掐断 → 库里零行、看板仍绿、档期已消费不补跑；抛异常同样零行）。已改「开始插 RUNNING → 退出 UPDATE → 启动扫 ABORTED」。`SweepAbandonedAsync` 只在 `RunAsync` 调。

## 二 数据口径
- RecResult '3'主/'1'平/'0'客；`Handicap` **正=主受让、负=主让球**。
- **PnlIdx = Pnl/Volume×100；负 = 该方向被买重**。只有 **≤−60** 有否决力。
- **`HalfHomeScore IS NOT NULL` 本身就是「有真半场」**。
- 串：BQ/JQ 2串 .636→3串 .507。**半全场 3 串、比分不串、总进球 3 串封顶**。

## 三 判据
- 单边砸价 ≥10% 而必发不跟 = 该侧高估（客 −17.0；主胜侧不成立）。主胜<1.35+必发≥70% 反向，只限主胜侧+欧战；强队让胜破 1.6x = 只赢球不赢盘。
- 胜平/负平**永远同价**，**负平 .740 永不买**；半全场基线**按赔率条件化**。体能只标注不扣分。
- 闸门：比分停发、半全场 ≤8 倍、总进球 ≤10 倍、EV 带 [返奖率×1.00, ×1.45]。

## 四 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`、`high`→撤注不删行。
- ★★ **撤注判据禁 EV 绝对值**：出票闸管「值不值」，撤注闸只管「市场明确反对」。**两闸门槛必须同源**。
- ★★ **回写不许按 `PickDate=@d` 过滤**（0 点后通宵场永不被撤）。
- ★★ **旁路 catch 不许静默**：`SidecarErrors` → SyncLog 带 `!!`；改完必须故障注入验证。

## 五 自检与性能
- `tools\milk-check.ps1`（纯 ASCII）出榜后跑。4d/4e FAIL；4f/4g/4h WARN。
- ★ 自检 SQL **禁写 `--` 注释**（sqlcmd -Q 报错 → 降级 WARN 假绿）；4c 排除 `RUNNING`。
- **已开 RCSI**：`odds-sample` 被杀 → 113 秒。
- ★★ **每层都要问「会不会把结论吃掉」**：状态硬编码 / 内层 Ok / 组合层 Ok / 续期成功≠能用。
- ★★ **慢要归因到请求数**：每场串行 3 页 × 900ms；`FbOddsHistory` 的 AH/OU（63% 行）无消费者 → 多数轮只采 EU。看 `/api/odds/pressure`。
- ★★ **`OddsSampledAt` 单戳会给低频作业「喂饱」**：AH/OU 曾静默停更。water-sample 每 3 轮补全量；`odds-sample-late` 仍空转。
- ★ 夜间欧赔 10 小时黑洞；`MarketTtlHours=12` 把 22:05 的盘当现盘（未修）。

## 六 空跑台账 + 终版价
- `FbPaperTrade`（只增不改，`FreezeMissingAsync(7)` 兜底）：**上过榜就记账、以首次上榜价为准**，漏的不许近似补；分母=已结算且未撤注、回报=命中合计÷注数、撤注单列 `Pnl=NULL`。门槛 150 注。
- **北单结算**：净胜=主−客+**北单让球数**。**终版价**三表 `FinalOdds/FinalAt/FinalSource`，不改 `Odds`。

## 七 稳胆
- `FbSteadyPick`：SPF 三档去水→最高→≤1.75→剔均衡局(<40%)→按赔率升 3。实测前 3 = 98 注 77.55% / 0.993。**禁止为填满而放宽**。

## 八 待办
1. 撤注判据回测（0/150 样本）
2. 九档 620 → 3000 场需约 5 个月；500 只留 30 天
3. `PlayOrder` 未回测；「冷门被买热→不买」待第二批窗口
4. 降 `MarketTtlHours` 需回测（连带必发判据）
5. `jc-odds-backfill` 近 3 天 2 次 SQL 超时（非 RCSI）
6. 档期被重启消费后不补跑（设计如此，需人工补）
