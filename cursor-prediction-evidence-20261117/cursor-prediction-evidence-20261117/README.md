# Cursor 正式预测改进：只读历史材料包

## 先读边界
本包于 SQL2025Test 从已经存在的文件整理；这次没有建立任何数据库连接，没有新查生产，也未操作 WinSev25Data、改代码或部署。原始证据目录保持不变。本包不是候选程序，不证明预测改进。
SQL2025Test 是数据测试 VM。代码开发在 WinSev25Data 的隔离工程 source\aifootball；不要写 deploy-package\app\api，不要访问生产库/API/作业。
所有显示的无偏移本地时间沿用原始导出，按 Asia/Shanghai (UTC+8) 解释；未把这些时间伪装成带时区的数据库字段。

## 从哪里开始
1. 先读本 README，随后阅读 replay-run-20261117-certified\REPORT.txt、out\summary.txt、export\export-log.txt。
2. 实际已发布官方榜：replay-run-20261117-certified\export\official-picks-2026-11-16.tsv。此文件保留 FbMilkPick 原始导出列，包括 PickId、RunId、ActualScore、ActualHalfScore、Hit、SettledAt、Voided、Handicap、ModelVersion。
3. review\ACTUAL-exported-official-picks-2026-11-16.tsv 只是上述文件的选列，不是新查询，也不是推导结算。
4. review\DERIVED-fulltime-only-2026-11-16.tsv 仅用保存的 outcomes-posthoc.tsv 全场比分和 handicap-2026-11-16.tsv 的认证竞彩让球推算，明确不是数据库结算状态。BQ 在该推导表一律 UNKNOWN（全场比分不足）；原始官方注单实际有 ActualHalfScore 与 Hit，应独立看原始字段，不能将推导表 UNKNOWN 误读为数据库没结算。
5. out\replay-picks-*.tsv、replay-*.json 才是离线回放产出，绝不可混称为正式已发布注单。官方路径与 follow 路径仍须分开。

## 范围：只覆盖盘点的一个 RunId/天，不是昨天所有轮次
2026-11-14: RunId 20261114T225008-4408e9，DecisionAt 22:50:08.9833333。
2026-11-15: RunId 20261115T225017-c6849c，DecisionAt 22:50:17.8133333。周日008 / 1398695 因 JcBookGap=1 跳过。
2026-11-16: RunId 20261116T215008-c60013，DecisionAt 21:50:08.2566667。
不能据此确认“昨天全部推荐只中一场”；未收集所有轮次、所有产品或用户实际下注。

在 11-16 这一个 RunId 的已保存官方导出内：8 注、5 个不同 MatchId；8 行 SettledAt 非空，Hit=True 1 行（周一001 JQ 1，赔率 4.20），7 行 False，Voided 均 False；有命中注的不同场次 1 个。这是这个快照的数据库字段计数，不是对昨天整体的结论或当前重新核验。
按玩法为 JQ 2、BF 3、BQ 2、NSPF 1。仅作描述，不能从一天/这一轮证明模型效果、收益或选定调参。
DERIVED 全场推导为 6 注可评、1 HIT / 5 MISS，2 注 BQ UNKNOWN；这与原始官方结算状态是两套不同证据。没有费用、组合投注、实投金额、奖金到账数据，不提供真实收益结论。

## 来源、完整性与复现限制
原始来源仅为：
C:\Users\Administrator\FitTraceTest\env-evidence\replay-run-20261117\
C:\Users\Administrator\FitTraceTest\env-evidence\replay-run-20261117-certified\
SOURCE-MANIFEST.tsv 逐文件列出原始绝对路径、字节数与 SHA256，复制后逐一核对一致。原始文件均未修改。
certified 目录完整收录除 bin/obj 外的文件（包括首末轮轻量快照，以及早期不应作为权威输入的 score-diag-chunks）。
first-run 目录收录根报告/日志、out、runner 源码以及 export 的 ps1/txt 日志；不重复收录其大样本、月度块和被隔离的超时查询产物。
认证目录中的 export\samples.tsv 为原来的 180939 行、约 14.5 MB 样本，供离线复现，不是此次重新取数。
runner\Program.cs、MilkReplayOffline.csproj 和导出脚本仅作代码审阅/来源证据。不要运行导出脚本：其中保留历史数据库连接命令，这次不授权联网或访问数据库。
未捆绑 bin/obj、冻结 DLL、整个旧 zip 或开发工程。源码编译仍需原始冻结依赖及相应 .NET/NuGet 环境；本包本身不保证开箱即跑。
旧评估 zip 原位置：C:\Users\Administrator\FitTraceTest\football-replay-diag-20261116c.zip
旧解压目录：C:\Users\Administrator\FitTraceTest\football-replay-diag-20261116c
历史记录的 SHA256 a95c006e8747662b41b2d91e185b4a6af1fb40c165db5672b6f66b81e7c5b0a4，2490743 字节。这是旧评估包哈希，不是本证据 zip 哈希，不是新的开发成果。
FILE-LIST.tsv 是本证据包全部成员清单（除清单自身外带字节数和 SHA256；清单自身列有路径，避免自引用哈希）。

## 审计警告：不要照抄旧说明
- certified 报告/runner 声称纳入场次 full、ComparableToOfficial=true，这是 runner 对其认证输入的覆盖判定，不是正式系统逐位复现证明。
- score-diag 改为按 RunId 限定的首末快照轻量重算，改变了早期全历史查询的重复源行放大行为（曾出现 DeepNegDrop 54 vs 6）。即使认为轻量结果更合理，也不能声称两查询已证明 bitwise 等价，或据此证明正式服务当时使用的值。重开发前需要单独评审正式路径是否存在同类问题。
- JSON/summary 仍有旧文案“uncertified side inputs”“handicap cleared”等，与认证 rerun 实际 ledger/让球输入冲突；原始文件保留原样，未静默修正。不要将旧字句当成新回放真实状态。
- 旧 REPORT 把 earlier published picks 笼统归于 11-16 18:48 包的描述不适用于 11-14/15：这两天在后续切换之前，不应说它们使用了 11-16 的后来版本。精确版本只能依已有 ModelVersion/构建证据判断。
- certified 11-16 键重合 8/8、11-14 0/2、11-15 官方原榜 0 注，这些是选注键对照，不是命中率、收益，也不代表概率/样本数逐位一致。
- 原报告不完整的 posthoc 结果与旧注释不是官方结算，原始 official-picks 文件中的 Hit/SettledAt/ActualHalfScore 才是已经保存的相应字段。不能把赛后字段混进赛前输入。
- 部分导出 SourceTable/TailTag 中文可能存在历史乱码，原样保留；不能据此臆造字段值。

## 缺失材料/不得臆造
没有昨天所有 RunId 的官方榜、其它产品/渠道/用户订单、投注金额、组合票、奖金到账或完整收益账。
没有独立结算流水或对第三方赛果的二次认证；只提供既有 FbMilkPick 结算字段快照和 FbMatch 全场结果快照，不保证当前状态未变化。
没有证明正式运行时内存中每项侧向输入与重建值一致的证据；也没有完整可证明改进的时间切分训练/调参/验证集或新候选包。
若所需字段在包内不存在，列出缺项交 Wren，不自行连接生产取数。

## 给 Cursor 的后续任务
先核对 actual 官方注单与已保存结算字段，区分“注单命中数”“有命中注的场次数”，并明确只覆盖该 RunId。再审阅正式调用链定位可证问题；尤其先审计 score-diag 重复行行为及概率校准，不可直接把重建查询差异当成已证明根因。
只能在隔离开发工程改源码、编译与不连库单测。不得运行本包导出脚本或联网取数据，不放宽能中门槛，不按联赛预过滤，缺档只跳过该场，北单/稳胆不变。正式功能改动需同赛前输入对比与时间隔离验证；数据不足就列出，不宣称提升。新候选包交 Wren 在 SQL2025Test 独立审核，上线另批。
