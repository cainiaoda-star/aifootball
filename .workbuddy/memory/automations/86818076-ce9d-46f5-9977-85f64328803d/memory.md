# 每日竞彩复盘 自动化执行记录

## 执行口径
- 昨日 = 运行日 - 1 天。三板块：Recommendation(Board='jc')、FbMilkPick(PickDate=昨日)、FbBdSpfPick(PickDate=昨日)。
- sqlcmd：`sqlcmd -S localhost,1433 -U sa -P "Lpw0145$$" -C -d AiFootBall -W -s "|" -Q "..."`，python3 subprocess 以 gbk 解码中文（口令两个 $ 用 python 变量传）。
- 北单让球盘判定：让球后主净胜 = HomeScore - AwayScore + Handicap（Handicap 挂主队：-1 主让1球，+1 主受让1球），>0 让胜/=0 让平走盘/<0 让负。FbBdSpfPick.Hit 结算 job 尚未跑（全 NULL），需手动按让球盘算。

## 关键表结构备忘
- Recommendation：Board/RecDate/MatchNo/RecResult(3主胜/1平/0客胜)/RecHandicap/ActualScore/Hit/HandicapHit/SignalLevel/PickOdds。
- FbMilkPick：PickDate/MatchNo/PlayType(BQ半全场/JQ总进球/BF比分)/OptionName/Odds/Hit/SettledAt/ActualScore。
- FbBdSpfPick：PickDate/MatchNo/Kind(steady/reverse)/Side/SideName(让胜/让负)/Odds/Handicap/HandicapSide/RatioHome/RatioAway/PnlIdxHome/PnlIdxAway/Reason/Hit。比分校准 JOIN FbMatch ON MatchId。
- FbDailySkill：SkillId(IDENTITY自增)/SkillDate/Title/Body/Evidence/CreatedAt。
- 半全场编码 3胜/1平/0负，BQ 9 档：胜胜3-3/胜平3-1/胜负3-0/平胜1-3/平平1-1/平负1-0/负胜0-3/负平0-1/负负0-0。

## 历史执行结果
### 2026-11-06（首次运行，旧名"大奶榜结算验证"）
- 昨日=11-05，赛果未到位（比赛全在11-06下午~11-07凌晨未开赛）。发现结算 bug：未开赛场次被误标 ActualScore='取消' 写死。
- 唯一可验证样本 PickDate=11-04 旧口径 6 注全灭。

### 2026-11-07（升级为"每日竞彩复盘"，09:30）
- 昨日=11-06，赛果已基本到位（大奶榜 6 注全结算、北单 13/14 完赛，仅场55未开赛）。
- 产出 复盘/2026-11-06_竞彩复盘.md。战绩：主推 1/1（哈马比4:1）；大奶榜 2/6 ROI+35%；北单推荐 14注 8中5失 61.5% ROI+43.5%（steady 5/9、reverse 3/4）。
- 核心结论：①中等强度资金反向信号小联赛又是噪音（reverse 4场仅1兑现）；②让平规律限定"主胜>80%极端大热+让-1"，64%普通热门可追让胜（哈马比4:1）；③reverse 标记=押主胜避开深盘，非反向押客胜。
- 未新增 FbDailySkill（核心规律已被 SkillId=8 覆盖）。

### 2026-11-07 09:40（重复触发，数据无变化）
- 本次为同一运行日的第二次调度（09:30 已产出完整复盘）。复查三板块数据与 09:30 完全一致：主推 1/1、大奶榜 2/6、北单 13/14 完赛 8中5失。
- 场55 泰格雷 vs 萨米恩托（阿甲 09:00 开赛）此时 09:40 仍未回填比分（Status=0、比分 NULL）——09:00 开赛 90 分钟要到 ~10:50 才结束，属正常进行中，非数据不同步。复盘文档已正确标注"待结算"。
- 未重写文档（避免覆盖已完成的正确成果）。结论与 09:30 一致，无新落库。

### 2026-11-08 09:46（每日竞彩复盘，昨日=11-07 周六）
- 产出 复盘/2026-11-07_竞彩复盘.md。三板块：主推 5 注未开赛（RecDate=11-07 指向 11-08 周日场，MatchTime 全在 11-08 21:30 后）；大奶榜 9 注全结算 2 中 7 失 ROI -10.6%；北单 14 注结算 8 中 4 走盘 2 真输 ROI +29.6%（steady 7/10、reverse 1/4）。
- **新口径发现**：主推复盘不能按 RecDate=昨日判可结算，主推的 MatchTime 是"推荐日次日"的比赛。RecDate=11-07 的 5 注全是 11-08/11-09 开赛的周日场，比分全 NULL。以后主推结算须按 MatchTime 落在昨日开赛的场次，而非 RecDate=昨日。
- 大奶榜高赔单列（CounterHigh）周六025 奥萨苏纳 负平@17.00 未中（1:1 平平），符合单列"小注买可能"定位。
- reverse 档命中率回归 25%，深负 -180 仍大胜（罗森博格 4:1），小联赛资金信号继续当噪音。
- 未新增 FbDailySkill。

### 2026-11-09 09:46（每日竞彩复盘，昨日=11-08 周日）
- 产出 复盘/2026-11-08_竞彩复盘.md。三板块：
  - 竞彩主推 5 注（RecDate=11-07 指向 11-08 夜场，MatchTime 11-08 21:30~11-09 01:30）方向 4/5、让球 4/5；唯一失手索尔纳客胜 1.55 高水；阿尔克马 4:3 主让-1 赢球不赢盘（让胜走盘）。
  - 大奶榜 7 注 2 中 ROI +47.9%（曼联平平@6.15、弗赖堡总进球2球@4.20）。
  - 北单 12 注（11 已结算）6中1失4走盘；让胜档 2中4走盘、让负档 4中1失（维京 reverse -111 反而 5:1 大胜）。
- 信号：小联赛资金反向仍噪音（-111 不保证爆冷）；让平规律兑现（阿尔克马）；半场平转分胜负 vs 押平平矛盾第三次暴露。
- 未落 FbDailySkill（无≥3场新规律）。北单 Hit 全 NULL 仍手动算。场364 克雷塔罗未结算待补。

## 下次运行注意
- 结算 job 若已修复，FbBdSpfPick.Hit 会有值，优先读 Hit 字段而非手动算；仍未修则继续手动按让球盘算。
- 场55 泰格雷 vs 萨米恩托（11-07 09:00 开赛）需在下个复盘日补结。
- 确认 bd-spf-pick 定时任务是否带结算逻辑，否则北单每天都要人工对账。
- 调度去重：若同一运行日多次触发，先比对三板块数据与已产出文档是否一致，一致则跳过重写。
