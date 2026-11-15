# 模型留痕隔离测试交接（发布夹具精度）

交给 Wren 在同一台 SQL2025TEST 上重跑本包全部测试。本包没有连接生产，没有连接这台测试库，没有冻结，没有部署。

上一包 27 项里 26 项已通过。`@smin` 已消失，零值 SQL 往返已通过。剩下的一项是 `SaveMilk_PublishesWhenBindCommits_AndBlocksWhenInstanceIsNotReady`：期望 `Failed`，实际 `Skipped`。

## 这一包改了什么

跳过发生在模型留痕绑定之前。`SaveMilkAsync` 只有在榜面计划已经是重写、落库和核对都通过之后才调用 `BindFitTraceAsync`。实际返回 `Skipped`，说明 `MilkBoardPlan` 走了 Skip：采集批次不可用，绑定检查没有执行。

原因在测试夹具，不在发布跳过规则。正式表 `BettingCard.CapturedAt` 是 `datetime2(0)`。采集查询把 `MAX(CapturedAt)` 读回后，Dapper 用 `SqlDbType.DateTime` 传回 `@cap` 做等值比较。夹具把这一列建成了 `datetime2(7)`，`SYSDATETIME()` 的秒以下精度经 `datetime` 舍入后对不上本批，期号列表为空，计划直接 Skip。

夹具改为 `datetime2(0)`，插入时用 `CONVERT(datetime2(0), SYSDATETIME())` 落到整秒。生产跳过规则、采集完整度、销售时段、算法和 DDL 都没有改。失败断言仍是 `Failed`，并且原因必须含「模型留痕未提交」。

这一项现在分开验证两件事：

1. 未 ready 的实例到达绑定检查，被模型留痕原因阻断，成功轮次为 0。
2. ready 实例在绑定事务提交后，真实 `SaveMilk` 发布，轮次 `ModelFitInstanceId` 等于该实例。

参数绑定、内容插入后取消、零值往返、旧版 `82c93bce` 二进制兼容都还在。

## 目标

- 主机：SQL2025TEST
- 系统：Windows Server 2025
- 数据库：SQL Server 2025 默认实例。连接名是 `SQL2025TEST`，不带实例名
- 库：`AiFootBall_FitTrace_Test`
- SDK：.NET SDK 8.0.428
- 认证：`config\sql2025test-integrated.cmd`，没有密码

`AIFOOTBALL_FITTRACE_CONN` 和 `AIFOOTBALL_FITTRACE_INSTANCE` 只是测试读取的变量名。不要安装名为 FITTRACE 的实例。

默认实例上 `SERVERPROPERTY('ServerName')` 应等于 `SQL2025TEST`。若身份校验失败，把 `AIFOOTBALL_FITTRACE_INSTANCE` 改成失败信息里的 ServerName 原文，不要自行加上 `\FITTRACE` 或 `\MSSQLSERVER`。

## 被测版本

`version\COMMIT.txt` 是提交。`version\git-status.txt` 是工作区状态。`version\uncommitted.diff` 是打进本包的未提交差异，包含参数绑定、取消测试、零值往返和这次夹具精度。

未打进本包：

- `JqFrequencyExperiment.cs` 和它的测试
- `复盘/` 下的实验和审查文档
- `.workbuddy/memory/`
- `.git`、生产配置、连接串、凭据、数据库备份

## 执行

在解压后能同时看到 `source` 和 `deploy-package` 的目录：

```bat
dotnet --version
call config\sql2025test-integrated.cmd
dotnet test source\aifootball\tests\AiFootBall.Tests\AiFootBall.Tests.csproj --nologo
```

`dotnet --version` 应为 `8.0.428`。这个工程只含 `FitTraceTests`、`FitTraceDbTests` 和旧版二进制探针，上面这条命令就是本包的全部测试。不要改断言来迁就失败。不要把变量指到 `WINSEV25DATA`、`192.168.117.128` 或 `192.168.41.128`。

夹具仍会在核对实例和库名之后执行待审 DDL。只作用于 `AiFootBall_FitTrace_Test`。

文件清单是 `SHA256SUMS.txt`。每一行是大写十六进制 SHA256、两个空格、相对路径。
