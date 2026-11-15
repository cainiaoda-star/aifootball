# 模型留痕隔离测试交接

交给 Wren 在虚拟机上执行。本包在生产机上只做了打包，没有连接 SQL2025TEST，没有安装，没有运行测试，没有部署。

## 目标

- 主机：SQL2025TEST
- 系统：Windows Server 2025
- 数据库：SQL Server 2025 默认实例。连接名是 `SQL2025TEST`，不带实例名
- 库：已经准备好的空库 `AiFootBall_FitTrace_Test`
- SDK：.NET SDK 8.0.428
- 认证：Windows 集成认证。模板在 `config\sql2025test-integrated.cmd`，没有账号，没有密码

`AIFOOTBALL_FITTRACE_CONN` 和 `AIFOOTBALL_FITTRACE_INSTANCE` 是测试代码里的环境变量名。它们不表示要安装一个叫 FITTRACE 的命名实例。

默认实例上 `SERVERPROPERTY('ServerName')` 应等于 `SQL2025TEST`。身份校验把这个值和 `AIFOOTBALL_FITTRACE_INSTANCE` 做不区分大小写的比较。若失败信息里的实际 ServerName 不是这个字符串，把变量改成失败信息里的原文，不要自行加上 `\FITTRACE` 或 `\MSSQLSERVER`。

## 被测版本

`version\COMMIT.txt` 是打包时的提交。`version\git-status.txt` 是当时工作区状态。`version\uncommitted.diff` 是打进本包的唯一未提交源码差异：`OptionBaseline.cs` 把已有的半全场赔率边界改成公开字段，数值没变。

未打进本包、因此不参与编译的工作区文件：

- `source/aifootball/src/AiFootBall.Sources/JqFrequencyExperiment.cs`
- `source/aifootball/tests/AiFootBall.Tests/JqFrequencyExperimentTests.cs`
- `复盘/` 下的实验和审查文档
- `.workbuddy/memory/` 下的记忆文件

测试断言没有改。

## 包里有什么

- `source/aifootball/src/AiFootBall.Core`、`AiFootBall.Data`、`AiFootBall.Sources`：测试项目要编译的三个工程。Data 工程把 `source/aifootball/db/*.sql` 嵌进程序集，所以这些建表脚本在包里。它们不是库备份，也没有生产数据。
- `source/aifootball/sql/pending/2026-11-15_milk_model_fit.sql`：待审 DDL。测试夹具自己读这个路径并执行。不要拿到别的库上执行。
- `source/aifootball/tests/AiFootBall.Tests` 里四个文件：`AiFootBall.Tests.csproj`、`FitTraceTests.cs`、`FitTraceDbTests.cs`、`LegacyBinaryProbe.cs`。合成夹具留在测试源码里。
- `deploy-package/pending/football-5c3911c`：冻结构建 `1.0.0.0+82c93bce` 的四个 DLL 和 `FINGERPRINTS.txt`。探针按源码里的 SHA256 核对这四个 DLL。没有放现网目录 `deploy-package/app`，没有放 `deps.json`。

没有 `.git`、生产配置、连接串、凭据、数据库备份、真实业务数据。解决方案文件没有放进来，因为它还引用了未打包的 Api、Collector、Backtest。不要补这些项目。

## 十项数据库测试

都在 `FitTraceDbTests`：

1. `SameContent_IsReused_AndClocksStayOnEachInstance`
2. `ChangedScore_InsertsASecondReadyContent`
3. `ConcurrentSameContent_Dedupes_AndFailedCallerHasNoPartial`
4. `MidWriteFailure_RollsBack_AndWritingRowIsNotReferenced`
5. `CancelledWrite_RollsBackWithAnIndependentToken`
6. `Refresh_PersistFailure_StillUpdatesCache`
7. `BulkWrite_AndRollback_AreMeasuredOnTheIsolatedDatabase`
8. `PendingDdl_HasTheReviewedTablesColumnsAndReadyIndex`
9. `LegacyBinary_ReadsAndWritesWhileNewShapeRemains`
10. `SaveMilk_PublishesWhenBindCommits_AndBlocksWhenInstanceIsNotReady`

第 9 项用冻结的 82c93bce 二进制读写。它只加载 Core 和 Data，不启动 Api。

`FitTraceTests` 是同一批源码里的进程内检查，不连接数据库。其中 `Probe_RefusesUntilAnIsolatedInstanceIsNamed` 用字符串 `lab\FITTRACE` 验证守卫接受一条显式写出的连接。那是断言夹具，不是这台虚拟机的实例名。

## 执行

在解压后的包根目录（能同时看到 `source` 和 `deploy-package` 的那一层）打开命令行：

```bat
dotnet --version
call config\sql2025test-integrated.cmd
dotnet test source\aifootball\tests\AiFootBall.Tests\AiFootBall.Tests.csproj --filter "FullyQualifiedName~FitTraceDbTests" --nologo
```

`dotnet --version` 应为 `8.0.428`。测试未设置那两个变量时会拒绝连接，这是守卫本身的结果，不要改断言，也不要改去连本机其它实例。

夹具在核对 ServerName 和库名之后，若库不存在会创建 `AiFootBall_FitTrace_Test`，并执行待审 DDL。当前库已经是空库，执行仍会在这个库里建表。不要把变量指到 `WINSEV25DATA`、`192.168.117.128` 或 `192.168.41.128`。进程若跑在机器名 `WINSEV25DATA` 上会直接拒绝，并且不连接。

文件清单是 `SHA256SUMS.txt`。每一行是大写十六进制 SHA256、两个空格、相对路径。
