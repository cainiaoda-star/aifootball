# AiFootBall 长期记忆
> 细则 `判据库.md`；过程见每日 md。

## 一 铁律
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query`（禁 Bash 跑 sqlcmd）；改必 `.\deploy-api.ps1`（不重启=旧 dll）+ `dotnet test`。
3. `jobs.json`：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10` / inplay `*/3`；采赛中须 `inplayOnly`；500.com 只认真浏览器。
4. 成对改 `new RecommendationService(...)` 两处；闸门槛统一 `Payout`；写当日榜走 `_recommendLane`。
5. 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；受注日≠开赛日。
6. 部署留痕 `deploy-history.log` + 核对 `/api/health` 的 `build`(MVID 前 8)。
7. ★★★ 禁止对「部署类」自动化用 `automation_update`（update 后 1–2 分钟内必触发，`90a1a054` 实触发 7 次）。只手工部署。
8. ★★★ **未经明确同意禁止手工删改生产数据/结构**；只读不算；先问、后登记 `OPS-DATA-CHANGES.md`。11-13 删 `PickId=10646` 已越界。
9. **判据不许取自被测对象的输出**（连踩两次）：`input>0 且 snapshot>0`、「预期=引擎自己的存档」都只证明「算出来的都写了」。判据要从**输入侧**独立推出。
10. ★★★ **不得早于批准窗口执行**：等待目标设为窗口**当刻或之后**，宁晚不早；实际完成时间以 `deploy-history.log` 为准。11-13 批准 23:10，实做 23:09:18（早 42 秒）已记。

## 二 口径
- 返奖率=天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。赔率 1.00 是占位：回测须 `CapturedAt<MatchTime AND Odds>1.01`。`FbJcOddsHistory` ≠ `FbOdds(CompanyId=0)`。
- RecResult 3主/1平/0客；`Handicap` 正=主受让；`PnlIdx` 负=被买重，只有 ≤−60 可否决。
- `HalfHomeScore IS NOT NULL`=真半场。串 BQ/JQ 2串 .636→3串 .507。主胜<1.35+必发≥70% 反向（限主胜+欧战）；负平 .740 永不买。
- `国际赛`＝竞彩对国家队热身赛的叫法，故意不进 `Names`（子串会砍「国际赛-世界杯预选赛」）。`NoPickLeagues`：欧国联/友谊赛/球会/U17+。
- **共用一个口径就只写一份实现**（`Payout`/`HalfFullCombos`/`PaperTradeMath`）：两处各写一份 = 系统撤销自己刚出的票（已踩）。

## 三 概率与权重
- w 冻结：SPF/NSPF .7、BQ .5、JQ .8、BF .3；唯一入口 `MarketWeightFor`。模型精度输市场，改权重未证实，衰减不启用。
- ★★★ **ρ 固定 +0.010**（11-13 拍板 B，`DefaultRho`），理由是可复现；ρ=0 与全量似然（−.040）留 A/C，须换没看过的时段。`WithRho` 只服务回测台。

## 四 告警闭环
- 去重 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行；读取点都要 `ClearedAt IS NULL`。
- 撤注判据禁 EV 绝对值：只管「市场明确反对」，门槛与出票闸同源。

## 五 存档 / 发布状态机 / 战绩
- **三种形态**（`MilkBoardPlan`）：①期号确认+采集完整+全被过滤→按**过滤前**受注日重写（空列表=撤失效旧票）；②只采到部分→`Partial` 不写不删；③过滤前 0 场/批次不可用→一行不动。判据 `MilkCaptureState`。
- **发布是状态机不是 `MAX(RunId)`**：`FbMilkRun.Status`；先 running 头 → 写四表 → 全成功才同事务写 `FbMilkRunDate`（0 注也写行）+ success。失败轮再大也不顶榜。读路径全认 `success`，无新轮退回 `RunId IS NULL`。
- **唯一键必须含 RunId**（真库撞 2601 才暴露）：`UX_FbMilkPick_Run`、`UX_FbMilkGate_Run`。
- ★ **逐档核对，预期从输入侧推**：`MilkInputMatch.Options`（`Distributed++` 处记）× 档位域 = 应有存档；漏一场/一档即 failed 不发布。**0 注对 0 行必须通过**。
- ★★★ **豁免必须带证据，`engine-skipped` 不接受**（第六轮）：只认 no-odds / code-outside-domain / unknown-play / nspf-dirty-option / bq-joint-unavailable，每条绑定「允许条件+证据前缀」（`options=0`…）。空证据或证据不符即失败；**被拒的豁免不产生 ignore 键**（否则「拒绝」与「放行」账本表现相同）；`CrossCheck`：no-odds 的场次出了票 → `exempt-violated` 不发布（赔率缺失不许借豁免继续推荐）。
- **历史战绩不照抄「最新轮」**（昨天那注会随今天重算消失），也不能全算多副本。按 (受注日,场次,玩法,档位) 去重：**价格取首份、命中取任一副本、撤注/结算以冻结台账为准**。升级前 `RunId IS NULL` 天然满足，历史不丢。
- ★★ **价格与撤注是两条语义，不能同一次去重**：`MIN(PickId)` 取价会把 `Voided` 冻成首份 → 撤注永远同步不进台账（隔离库实测）。**结算即终态**：`SettledAt` 非空后不许再改撤注。
- 撤注不进命中率分子分母，单列计数（`MilkPlayStat.Voided`）。生产 11-13 因此 9/28=32.1% → 9/27=33.3%（`PickId=10608`）。
- **隔离库 `AiFootBall_AuditTest`**（**保留勿删**）：`milk-isolated-check` 克隆 9 张表结构+补 PK/唯一键/默认值，12 项断言要求**报表与空跑台账逐项相等**；硬编码库名，指向生产库名直接拒绝。
- ★ **场次覆盖的基准在源侧**（`MilkSourceSlate`，11-14）：源＝`BettingCard` 当前批 × `JcSaleHours.Sellable`（与 `ScoreAsync` 同一份实现）＋`FbJcOddsHistory` 每档最新采样。每场必须「输入凭证 或 带证据的拒绝记录」；`name-unresolved` **恒不接受**（12轮，见下条）；`no-odds` 与源侧档数矛盾即 `contradicted-rejection`；算过的场次过「采样 ≤30min」+「源侧每一档都进了计算」。落库 `FbMilkSourceMatch`（**新建表，待批准**）。
- ★★★ **`name-unresolved` 恒阻断，`new-team` 已停用**（11-14 十二轮，用户点名）：库检场次只回答「用**这个名**查到几场」，而解析失败的成因就是名字不同（谢周三 0 场 / 谢菲尔德星期三 74 场；不来梅 0 场 / 云达不莱梅 58 场）——0 场与 1~2 场**都不能**作为新队证据。十一轮那版「0 场 + 变体探查为空 → 升 `new-team` 放行」仍是「用计数证明身份」，已删，不允许再加回来。`thin-sample` 才是「身份已确认、样本不足」，两者门槛都是 3；区别在身份，不在数字。变体探查**只作诊断**（查到相近写法 → 说明是解析缺陷；查不到也不放行）。
- ★ 别名（11-14 十一轮，均先库检）：`谢周三→谢菲尔德星期三`（74 场/全量 332）、`不来梅→云达不莱梅`（58）。梯队 `谢周三U21`(31)/`云达不莱梅U19`(34)/青年队(52)/女足(48)/C队(2)/`不莱梅港`(48) 一律不并。事故实证：`MatchId` 1491222、1428635 当年 `FbMilkInput` 都是 0 行（静默丢失）。
- ★ **概率三列一律百分数**：`bfPure` 内部是 0~1 比例，只在落库前 ×100；`MilkProbScale` 按 (场次,玩法) 要求三列合计≈100 且同尺，否则不发布。11-14 那 620 行 BF 存档（ModelPctPure≈1.0）**禁用**，不得算 Hit@1 或调权重。
- ★ 队名解析只允许「查询名比库内名短」→ 竞彩加冠名后缀（大宫松鼠RB）会整场丢失。`Resolve` 已加核心名兜底；**库里出现新队名先查 `team-check`**，别先怀疑样本。
- ★★ **档位域必须是代码清单，不是档数**（`JcPlayDomain`，11-14 第八轮；第十轮收敛到四处）：BQ 有 3 场脏码（`平/胜` 等）让「10 档」看起来齐整。**完整性（逐档存在）、归一化（Σ1/赔率只算域内）、候选与全档存档、预期侧**都只认这一份域；`source-option-gap` 对**所有**源场次逐玩法逐代码比对；域外脏码不阻断、`missing-option` 只看域内。**不给「未启售」豁免**——库里没有逐玩法启售字段，「没采到赔率」一律按缺档阻断（要开口子先加字段报批）。
- ★★ **域判定不能替代玩法相等**：`Contains(playType, code)` 单用会把 SPF 的 `0/1/3` 算进 JQ 分母（JQ 域就是 `0..7`），实测 ΣBlended 100→40.745 且不报错。过滤永远写 `PlayType == playType && Contains(...)`。
- ★★ **源快照必须按 `StartedAt` 截止**（`GetJcSourceSnapshotAsync(asOf)`，两张表都带 `CapturedAt <= @asOf`）：running 头与「最新采样时刻」都不参与过滤，事后补采会改写上一轮的应有场次。
- ★★ **身份判据 = 梯队层次，不是「有没有字母」**（11-14 第十轮）：`TeamNames.SameIdentity` 一份，解析与计数共用；层次由 `HierarchyMark` 判（中文名单 + ASCII `U15~U23` + 罗马数字 `II/III/IV` + 单独 `B` + 二队）。`PSV`/`FC`/`RB` 是俱乐部名的一部分，不算另一支队——按「候选记号 ⊆ 查询记号」实现会误杀 `埃因霍温→PSV埃因霍温`(147 场)、`辛那提→FC辛辛那提`(75 场)。**「样本不足会 thin-sample 拒发」不是防误配保障**（梯队≥5 场就出票）。
- ★ 子序列链路会把核心名那步拒掉的候选**捡回来**（`帕丘卡`→`帕丘卡III`）：同核心名必须再判一次身份，冲突**升级为歧义**（跳过会让 `米兰` 变成「国际米兰」）。
- ★★★ **同一轮只能有一个时间基准**（11-14 第九轮）：引擎取价也必须 `GetLatestAsync(asOf: runAt)`，与 `GetJcSourceSnapshotAsync(runAt)` 同刻；否则账本验 08:40 的清单、票用 08:52 的价。
- ★★★ **身份判定与门槛各只准一份实现**：`TeamNames.SameResolveIdentity`（候选⊆查询）/`SameCountIdentity`（互相包含）——解析与历史计数不能两套口径（解析拒梯队、计数却并梯队 = 库检永远证不出真新队）。门槛 `MilkThresholds.MinTeamGames=3`（引擎与核对同读，不许再写第二个数）。
- ★ **玩法启售依据 = 无法判定**：`BettingCard` 无逐玩法字段，采集器抓空与「不卖」同形。缺档一律阻断并记 `sale-basis=unavailable`；**不得把「一律 54 档」说成验收标准**，也不得从赔率缺失反推未启售。
- ★ 计数口径变严会**暴露**归一化缺口：`谢周三` 由 31（含 U21）→0，会被判「真新队」放行；正解是别名（`谢菲尔德星期三`），不是放宽判据。
- ★ 回滚只回退二进制并**保留新表**，不 `DROP TABLE`（11-14 用户更正）。
- ★★ **「本轮必须采到」与「陈旧批次可放行」是两种策略，不能用一个容差数字统一**（11-14 用户指出）：前者判「本轮采卡成功了没」，后者判「这张榜的场次表还不算太旧」。放宽阈值只让后者通过，前者依然失败，却把「本轮没采到」掩饰成「正常出榜」——正是旧批照发且无法追溯的形态。要选就显式三选一（只 S1 / 只 S2 / 两档并存 + 各自独立落痕），不得混用。
  **正解是分层，且 11-14 17:41 已裁定降级关闭（`DegradeEnabled=false`）**：L1 时间一致性（`|cap−rec.FinishedAt|≤120s`）→ L2 本轮达标（同一条 `rec` 是 `job:cards-daily` 且 `FinishedAt∈[slot,runAt]` 且 jc 成功）。★ **L1 与 L2 必须落在同一条记录**，不得"某条配对、另一条本轮成功"；★ **±120s 只算时间一致，不算来源证明**（真证明要 `CapturedAt = rec.FinishedAt` 一次写入）；★ 关闭必须显式 `if(!DegradeEnabled) return Red`，不得靠 `age≤0` 隐式失效；★ **完整性不足不得因批次新而放行**（`partial` = 不发布、不删旧票、告警、调用者不得报成功）；★ 跨日不再凭 L1 重写（`cross-day-skip`，不告警）；★ **拒绝不等于榜变新**（榜面仍是上一轮），红必须配告警。
- ★★ **`no-sale`（真 0 场）不能沿用旧批，也不能拿北单/胜负彩替代竞彩**（11-14）：空结果不更新 `CapturedAt`，"本轮 0 场"只能靠 SyncLog 的 `[no-sale] jc=0 containers=jc:1` 证明；出榜必须走空列表形态（不读旧批场次），且要求 `cap` 自该次采集起未被改动。

## 六 赔率新鲜度（按链路分开）
- 竞彩 `FbJcOddsHistory`（出票价）：2min 一轮、最大间隔 4min → 数据没坏；`GetLatestAsync` **零 age 守卫**。
- 欧赔 `FbOdds`：各场仅 2–3 采样点、最大间隔 291min；`odds-sample` TIMEOUT（RCSI=0 锁等待，作业静默结束）。
- `MarketTtlHours=8h` 只管亚盘初/终盘；`WatchPoolAsync` 三处不传 `freshHours`。
- 「价涨榜不跟」≠「用了过期价」；数据库超时（生产侧 RCSI）与吃到旧价（消费侧 TTL）**分开处理**。

## 七 训练集与审计（11-13 16:10 部署 `808b13f`，build `14cc0508`）
- 取样双闸：`MatchTime <= DATEADD(min,-150,asOf)` + `<= GETDATE()`；线上取样与 `Fit` 同一 fitMoment；`Fit` 剔队名空/比分>12。
- ★ 150 分钟只是完赛代理：首写延迟仅 1.9% ≤150min、84% >360min → 泄漏**未消除**。
- `ModelVersion`＝`dc1|窗口|n|rho|MVID|w|bf`。

## 八 台账
- `FbPaperTrade` 只增不改、上过榜即记账、首价为准；分母=已结算未撤注。稳胆每日 1 注 91.4%/1.108。注单页 `FbDailyFollow`。

## 九 待办
1. **★ 首次入库时间留痕**（`FbMatch.FirstSeenAt`），否则回测泄漏消不掉。
2. ρ=0 / 全量似然：新时段独立验。
3. 新鲜度（**单独排期**）：JC ~30min、欧赔/必发 3h 都只是待验证阈值。★★ **超龄不得默认退纯模型出票**，应停该玩法+标「数据过期」。
4. RCSI 根治 `odds-sample`（凌晨窗口）。
5. 撤注判据回测 0 注；`PlayOrder`、`GetEuOddsDelta` 未修。
6. P1-7 按 salesDate 冻结模型 + 快照日期键同改。
7. 英锦赛/英联杯无半全场基线；「英锦赛 vs 英锦标赛」未收别名。
8. 部署后：**真实多副本**要在生产再核一次（隔离库只证明逻辑）。
