# 票面 KPI —— 按票型统计回本率

记录日期：2026-11-03。状态：**已上线**（代码 + 表 + 接口 + 页面 + 复盘）。

## 一、为什么做

在它之前，票是**临时算出来的**：`/api/recommend` 请求进来时，`JcStakePlanAsync` 拿当天的推荐
拼一张票、算出中奖率和期望回报、返回给页面。请求结束，这张票就不存在了。

后果是：

- 「8 腿票这个月发了几张、中了几张、回本几张」——系统答不了，只能靠 `0.905ⁿ` 口算。
- 票型对比页只能给**理论概率**，给不了**实际结果**。理论说 8 腿票回本率 10.6%，
  但没人知道真实跑出来是不是这个数。
- 校准（`/api/calibration`）回答的是「单场方向准不准」，回答不了「这样组合到底亏多少」。

所以补的第一件事不是改票型，是**把票记下来**。

## 二、两张表（`db/10_ticket_kpi.sql`）

| 表 | 一行是什么 | 关键字段 |
|---|---|---|
| `TicketPlan` | 一张发出去的票 | `PlanDate` `Board` `PassType` `LegCount` `BetCount` `BetAmount` `HitProbability` `BackProbability` `ExpectedReturn` `Status` `HitLegCount` `PrizeAmount` |
| `TicketPlanLine` | 票里的一腿 | `MatchId` `PickOption` `Odds` `LegProbability` `LineHit` |

`UNIQUE (PlanDate, Board, PassType)`：同一票型一天一张。当天重算会覆盖**未结算**的那张；
已结算的直接跳过，否则晚间作业重跑会插出第二张、撞唯一键，也会把「今天发了几张票」算重。

## 三、数据流

```
save-recommendations 作业（08:40 / 11:40 / 16:40 / 20:40 + 终版：工作日 21:50、周末 22:50，停售前 8 分钟）
  └─ RecommendationService.RecommendAllAndSaveAsync
       ├─ 推荐落库（Recommendation）
       └─ TicketPlanBuilder.Build  →  TicketRepository.UpsertAsync
                                       └─ TicketPlan + TicketPlanLine（PENDING）

settle-recommendations / settle-hourly 作业
  └─ RecommendationService.SettleAsync
       ├─ 回填 Recommendation 的 ActualScore / Hit / HandicapHit
       └─ SettleTicketsAsync
            └─ 腿命中取 Recommendation.HandicapHit
                 → TicketPlanBuilder.Settle（复用 PurchaseSettleMath.CalculatePrize）
                      → Status 转 WON / LOST，写 PrizeAmount
```

**为什么腿命中取推荐的 `HandicapHit`，不自己重算**：票腿本来就是从那条推荐的让球方向来的
（`TryHandicapLeg(r.RecHandicap, ...)`）。另写一套判定，一旦两边算法有差，
就会出现「推荐说中了、票说没中」，复盘和 KPI 互相打脸。

**为什么结算复用手工购票的奖金函数**：`CalculatePrize` 已经处理了任意过关展开
（`6串1#7串1#8串1` → 28+8+1 注）和 1 万元以上扣 20%。系统票和手工票走同一个函数，
才能用同一张 KPI 表比较。

有腿还没判定时**拒绝结算**——宁可晚结，不算半张票。

## 四、两个数必须分开

| 指标 | 含义 | 为什么不能混 |
|---|---|---|
| 有奖（`Won`） | 至少一注中 | 8 腿票常年有奖（31.5%），看着很热闹 |
| 回本（`Backed`） | 奖金 ≥ 本金 | 同一张票只有 10.6% 能回本 |

只看「有奖」会高估自己。KPI 表两个都出，另给 `实际回报 = 奖金 ÷ 本金`。

## 五、接口与展示

| 位置 | 内容 |
|---|---|
| `GET /api/ticket/kpi?days=90` | 按 `Board + PassType` 分组：票数 / 有奖 / 回本 / 本金 / 奖金 / 实际回报 / 模型中奖率 / 实际中奖率 / verdict |
| `GET /api/ticket?take=20` | 待结算的票，含每腿选项、水位、命中 |
| 页面「我的投注」tab | 底部折叠「票面 KPI」。与上面的购物记录是两个口袋：上面是掏钱买的，这里是系统按推荐发的 |
| 每日复盘末尾 | 「票面 KPI（近 90 天已结算）」一节，含 verdict |

verdict 在样本 < 30 张时只说「样本不足」，不解释趋势——十几张票看不出任何结论。

## 六、当前状态（2026-11-03 11:00）

已出第一张真实的票：

```
单关#2串1#3串1   3 腿   7 注   14 元
中奖率 92.7%   回本率 19.7%   期望回报 0.803
周二008 曼联–罗马 让负 1.49 / 周二005 马竞–拜仁 让胜 1.51 / 周二004 让负 1.53
```

注：竞彩胜平负精选仍然是 0 场（`QualifiesForFeatured` 要求 `SignalLevel >= 2`，
盘口刚恢复采集、还没有「升/降」对比），但**票腿来自「有让球方向的全部场次」**，
与 `/api/recommend/jc` 的 `hcpRows` 同源，所以票能出。

这条数据本身就是 B 方案的论据：**中奖率 92.7% 和回本率 19.7% 同时成立**。
三条腿中任意一条，单关那注就有约 3 元奖金，但本金 14 元。

上线过程中修掉的 4 个问题见 `docs/策略审查-2026-11-03.md`，
其中两个会让票永远构建不出来、永远无法结算。

## 七、验证记录

- 单元测试 `tests/AiFootBall.Tests/TicketKpiTests.cs` 7 例：单关全中/未中、
  有腿未判定拒绝结算、2串1 全包中 2 腿（1 注）/ 全中（3 注）、
  8 腿 6+7+8串1 全中（6144 元）、8 腿中 6 腿（22.78 元有奖但不到 74 元本金）。
- 全量 195 个测试通过。
- 端到端：插入一张 2026-11-01 单关测试票（2 元 × 2.00），跑 `settle-recommendations`
  → 奖金 4.00 元、状态 WON、KPI 显示回本率 100% / ROI 2.0 → 验证后删除，表已清空。
