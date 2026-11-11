# AiFootBall 长期记忆（运维手册）

> 细则 `判据库.md`；过程见每日 md。只留「有实测样本 + 已落地代码」的判据。

## 0 速查
1. 截止 一~五 21:50 / 六日 22:50（停售 21:58/22:58）；榜面 8:40·11:40·16:40·20:40。
2. 查库 `& tools\db.ps1 -Query "..."`（**禁 Bash 跑 sqlcmd**，ps1/sql 纯 ASCII）；改必 `.\deploy-api.ps1` + `dotnet test`。
3. **模型无 alpha**（Brier .5995 vs 市场 .5812）；**EV 落返奖率 ±8 点 = 在挑口径**；基线 EV 超返奖率 = 给高了。
4. **开赛=票已买**只能 `Voided`；`FbMilkPick` 重算即删未结算行，不可当账。

## 一 铁律（运维）
- ★★★ odds.500.com 只认真浏览器（`ChromeBridge`/CDP），cookie/头/指纹/token 全无效，有配额，验证页当失败。
- ★★ `jobs.json`：jc-odds 2min / water 10min / turn-guard·paper-freeze `*/10` / inplay-sample `*/3`（HoursBehind2.2+inplayOnly）。
- ★★ 窗口≠优先级：`GetMatchesForSamplingAsync` 的 `ORDER BY EXISTS(BettingCard)` 让赛中永远进不来，只采赛中要在 WHERE 加 `inplayOnly`。
- ★★★ 返奖率是天花板：SPF/NSPF .878 / BQ/JQ .797 / BF .710。
- ★★ 配额≠出票：`MaxPerPlay[Nspf]` 是死配置，引擎无 NSPF 生成器。
- 成对修改：`new RecommendationService(...)` 两处；出票闸/撤注闸不许各写常数（统一 `Payout`/`MinEvPaybackRatio`）；改 INSERT 必同步 VALUES。
- `_recommendLane`：写当日榜的都是 DELETE+INSERT 且无唯一约束。
- 完场不信 `Status`：`比分全 && (Status==3 || MatchTime<=now-150min)`；**受注日≠开赛日**（按期号落库）。
- ABORTED≠FAILED，4c 只算 FAILED/TIMEOUT。

## 二 口径
- ★★★ **赔率 1.00 是占位值**：读取点有兜底、分析侧没有；回测须 `CapturedAt<MatchTime AND Odds>1.01`。
- ★★★ **赔率口径不可互换**：竞彩 `FbJcOddsHistory` 与欧赔 `FbOdds`(CompanyId=0) 装的不是同一批场次；`JcToEuro=1.20` 只是查基线表的粗换算。同一判据两种口径可出相反结论。
- ★★ **样本量硬约束**：高赔档 10 点差异每格需 ~5000 注（现 421）→ 证明不了错价，别挖。ROI 单注 sd≈2.3，216 注标准误 ±0.16。
- ★★ 联赛别名要连「两队样本」一起校验（加「日联赛杯→日联杯」后鹿岛 0 场/神户 2 场 → 榜面 4→2 场）。
- RecResult 3主/1平/0客；`Handicap` **正=主受让、负=主让球**；`PnlIdx` 负=被买重，只有 **≤−60** 有否决力。
- **`HalfHomeScore IS NOT NULL` = 有真半场**。串：BQ/JQ 2串 .636→3串 .507；半全场 3 串、比分不串、总进球 3 串顶。
- 主胜<1.35+必发≥70% 反向（限主胜+欧战）；**负平 .740 永不买**；`HomePriceRich` 只标不撤（近似死代码）。

## 三 概率与权重（11-11 定稿）
- ★★★ **模型精度输市场，但「改权重」在回报上没被证实**：Brier SPF 3558 场 .6030 vs .5821、BQ 540 场 .8255 vs .7964；`weight-replay` 里**没有任何 w 站得住**（两段排序相反）。分辨 .05 需 ~2000 注/组 ≈10 个月。
- ★★★ **w 已冻结：先验定值，未从 216 注 ROI 选出**。SPF/NSPF **0.7**、BQ **0.5**、JQ **0.8**、BF **0.3**、未知 0.5。唯一入口 `MarketWeightFor(playType)`，`FrozenWeights_ArePriorNotRoiFitted` 锁死。要改先答「有效样本够了吗」。
- ★★ **JQ 退出模型闸**（模型无信息）：纯模型 vs 历史频率 BQ **−2.51%**、**JQ +1.04%（方向反，比无脑历史频率还差）**。JQ 准入改为 ① 市场去水隐含 ≥10%（`PassesMarketFloor`）② 实证闸 `PassesBaselineEv`；模型只做对照（`ModelPctPure`）。`UsesModelGates(Jq)=false`。
- ★★ **单抬 w 不能替代「模型退出闸」**：w=0.8 时 EV=0.2·模型EV+0.8·返奖率，过 1.05 倍线仍需模型 EV≥0.996 —— 闸没变，只是变隐蔽。
- ★★ **权重判据是「市场隐含能不能信」，不是「模型准不准」**（我 11-11 写反过被纠正）：档位少/返奖高 → 去水稳 → w 高。
- ★★ 两道闸读不同概率：安全闸=纯模型 vs 门槛（读 `ModelPctPure`，**禁反解**，w→1 分母趋零）；价值闸=混合 EV ≥ 返奖率×k；实证闸=基线 EV（与 w 无关）。代数：过线 ⇔ 纯模型·赔率 ≥ 返奖率·(k−w)/(1−w)，**k=1 与 w 无关，k>1 才随 w 收紧**。k=1.05 仍是占位、无回测。
- ★★ 混合后按玩法**全档**归一化（`BlendNormalizer`）：Σ市场实测 .9905，不归一化则每档概率与 EV 集体偏低。对 ROI 影响 0.000~0.004 = 正确性修复。
- ★★ 时间衰减无收益：365 天 −0.0010（门槛 −0.002），180 天以下单调变差。别再做。
- ★ 注释里的数字不是证据：`MarketWeight` 原「500 场模型全胜」库里无实现；`CounterHighModelVsMarketMin=0.70` 同样无支撑。

## 四 告警闭环
- 去重键 `(AlertDate,MatchId,PlayType,OptionCode)`；`high`→撤注不删行。
- ★★ `FbTurnAlert` 软删除（`ClearedAt`）：每个读取点都要 `ClearedAt IS NULL`。
- ★★ 撤注判据**禁 EV 绝对值**：出票闸管「值不值」，撤注闸只管「市场明确反对」，两闸门槛必须同源。
- ★ 回写禁按 `PickDate=@d` 过滤（通宵场不撤）。

## 五 自检与性能
- `tools\milk-check.ps1`（纯 ASCII、**禁 `--` 注释**）：九列 / 接口键 / 近 7 天半场同分率 / 作业健康度。
- `TrendSql`/`ScoreDiagSql` 全历史重算：8 场分批 + 300s。
- `OddsSampledAt` 单戳喂饱低频作业 → `odds-sample-late` 空转，停用。
- RCSI=0 致 `odds-sample` 整点 TIMEOUT（静默、不报错、非数据坏）；根治只能凌晨 4 点后开 `READ_COMMITTED_SNAPSHOT`。

## 六 台账与产品线
- `FbPaperTrade`：**上过榜即记账、首价为准**；分母=已结算未撤注，回报=命中÷注数，门槛 150。
- ★★★ **稳胆每日 1 注是唯一正向 edge**：91.4%/**1.108**（前 2 注 1.011、前 3 注 .983）。规则：SPF 去水→最高→≤1.75→剔均衡局→按赔率升取 1。
- **北单**：方向=**模型概率最大档**（竞彩那个是市场档，同名不同义）；`MaxPerLeague=3`。
- ★★ 大奶双选：每场 2 注（BQ4/JQ2）。423 场 JQ .782→.805、BQ .827→.815 —— **命中翻倍、回报不动**。
- ★★★ 注单页是 `FbDailyFollow`(`/api/daily-follow`)，**前端不调 `/api/milk/picks`**；第二注进 `Alt*` 列。

## 七 待办
1. 撤注判据回测样本 0（需 ≥50）；`PlayOrder` 未回测。
2. `BqHomeOddsEdges` 2.60 是断崖（.599→21.32、.601→12.62），未修。
3. `GetClosingEuAsync` 已修（`FbOdds` 原地更新，72% 场次 CapturedAt 晚于开赛）；**`GetEuOddsDeltaAsync` 同源未修**（喂 `HomeOddsDelta`）。
4. SyncLog 三态 SUCCESS/FAILED/**IDLE**（采样零场次），判失败须排除 IDLE。
5. ★★ **冻结权重 + JQ 退出模型闸已提交 `178b781`、未部署**（11-11）。部署会改 JQ 出票口径，要先挑窗口。
6. 整点 `chore: 自动同步` → 提交前先看 `git log`。
