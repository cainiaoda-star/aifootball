# 定向修复 2 交付 — MilkSaveDecision + 临场重算结构化（2026-11-14）

**范围**：仅 (1) plan→outcome 唯一判定；(2) RefreshNearKickoff 结构化传播。  
**状态**：WorkBuddy 独立复审通过；随足彩冻结提交入库。部署申请另单，**未经明确批准不得部署**。  
**禁止**：生产查询、DDL 执行、外站、部署。DDL 只登记见实施清单 §7。

---

## 1. 代码对应关系（复核锚点）

| 要求 | 源码 |
|---|---|
| 唯一判定函数 | `AiFootBall.Core/Models/MilkSaveDecision.cs` → `Conclude(...)` |
| SaveMilkAsync 必须调用 | `RecommendationService.SaveMilkAsync` 关闭轮次前 `outcome = MilkSaveDecision.Conclude(...)`；再按 `Kind` 走 Publish/Close |
| 测试调同一函数 | `MilkPublishPropagationTests`：`Conclude_Skip` / `_Partial` / `_StepFailure` / `_LedgerFailure` / `_Success`；接线用例 `Wiring_AfterGate_Uses_Conclude_Then_FromMilkOutcome` |
| 临场结构化 | `NearKickoffRefreshResult`；`RefreshNearKickoffAsync` 返回该类型（`FromSave` / `Idle`） |
| 调度分账 | `NearKickoffJobMapping.ToCollectionResult`：`gate-skip`/`cross-day-skip`→IDLE；`gate-reject`/milk-*→FAILED（文案含真实 Outcome）；异常在 `JobScheduler` `refresh-near-kickoff` catch |
| 传播测试 | `NearKickoffRefreshTests`（5 条，见发现清单） |

**未覆盖（本轮明确）**：真实 SQL 落库集成；`RefreshNearKickoffAsync` 端到端连库；生产/外站。

---

## 2. 确切命令（本机已执行）

```powershell
$root = "D:\Setup\AiFootball_迁移包_20261104\WB_AiFootball_代码迁移_261104"
$proj = "$root\source\aifootball\tests\AiFootBall.Tests\AiFootBall.Tests.csproj"
$filter = "FullyQualifiedName~MilkPublishPropagationTests|FullyQualifiedName~RecommendSaveWiringTests|FullyQualifiedName~MilkBatchGateTests|FullyQualifiedName~MilkBoardStatusTests|FullyQualifiedName~GateBlocked|FullyQualifiedName~RecalcFault|FullyQualifiedName~Recalculated_Clears|FullyQualifiedName~CardFetchClassifyTests|FullyQualifiedName~JobLaneTests|FullyQualifiedName~Lotto|FullyQualifiedName~LotteryMath|FullyQualifiedName~NearKickoffRefreshTests"
$outDir = "$root\复盘\test-evidence_2026-11-14_定向修复2"

dotnet test $proj --nologo --list-tests --filter $filter
dotnet test $proj --nologo --filter $filter --logger "trx;LogFileName=results.trx" --results-directory $outDir
```

相对上一轮 filter：**追加** `FullyQualifiedName~NearKickoffRefreshTests`（本轮新增类）。

---

## 3. 证据文件（完整输出，勿用口头条数替代）

| 文件 | 内容 |
|---|---|
| `复盘/test-evidence_2026-11-14_定向修复2/list-tests.txt` | `--list-tests` 完整 stdout |
| `复盘/test-evidence_2026-11-14_定向修复2/discovered-names.txt` | 从 list 抽出的 FQN 清单 |
| `复盘/test-evidence_2026-11-14_定向修复2/test-run.txt` | `dotnet test` 完整 stdout |
| `复盘/test-evidence_2026-11-14_定向修复2/results.trx` | TRX |

**TRX ResultSummary.Counters（解析自 results.trx）**：`total=97` `executed=97` `passed=97` `failed=0` `error=0`；`outcome=Completed`。

**test-run.txt 末行摘要**：`已通过! - 失败: 0，通过: 97，已跳过: 0，总计: 97`。

以 **list-tests 发现清单行数 = TRX total = 执行通过数** 为准；不以全项目静态测试数为据。

---

## 4. 发现清单按测试类（来自本次 list-tests，非静态推算）

| 测试类 | 本 filter 发现条数 |
|---|---:|
| CardFetchClassifyTests | 9 |
| JobLaneTests | 2 |
| LotteryMathFrequencyTests | 5 |
| LottoBetValidatorTests | 6 |
| LottoBoundaryTests | 7 |
| LottoCatalogTests | 11 |
| LottoHitMathTests | 7 |
| LottoIssueTests | 5 |
| LottoRecommendEngineTests | 5 |
| MilkBatchGateTests | 16 |
| MilkBoardStatusTests | 2 |
| MilkPublishPropagationTests | 9 |
| NearKickoffRefreshTests | 5 |
| RecommendSaveWiringTests | 5 |
| TurnGuardTests（GateBlocked/RecalcFault/Recalculated_Clears） | 3 |
| **list-tests 合计** | **97** |

完整 FQN 见 `discovered-names.txt` / `list-tests.txt`。

### 本轮两条相关用例（复核优先）

- `MilkPublishPropagationTests.Conclude_Skip_When_Plan_Skip`
- `MilkPublishPropagationTests.Conclude_Partial_Is_Failed_Not_Published`
- `MilkPublishPropagationTests.Conclude_StepFailure_Does_Not_Publish`
- `MilkPublishPropagationTests.Conclude_LedgerFailure_Does_Not_Publish`
- `MilkPublishPropagationTests.Conclude_Success_Only_When_Rewrite_Steps_And_Ledger_Ok`
- `MilkPublishPropagationTests.Wiring_AfterGate_Uses_Conclude_Then_FromMilkOutcome`
- `NearKickoffRefreshTests.FromSave_Preserves_GateSkip_Outcome`
- `NearKickoffRefreshTests.FromSave_GateReject_Is_Failed_Not_Idle`
- `NearKickoffRefreshTests.FromSave_MilkSkipped_Keeps_Outcome_Not_Fake_Gate`
- `NearKickoffRefreshTests.FromSave_Published_Is_Success`
- `NearKickoffRefreshTests.Idle_When_No_Stale`

---

## 5. DDL（只登记）

见 `复盘/2026-11-14_足彩最小修复_实施清单_待确认.md` §7：DDL-1/2/3 + 同路径 DDL-S（ProbSnapshot）/ DDL-I（Input）/ DDL-C（CounterCandidate）。后续核对按列与索引定义，不数同名对象。

---

## 6. 遗留（不扩大本轮）

- 真实库集成 / Ensure DDL 实跑验证  
- turn-guard / RefreshNearKickoff 连库端到端  
- 冻结与部署另批  
