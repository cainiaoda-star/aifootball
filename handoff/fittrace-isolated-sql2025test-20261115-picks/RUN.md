# 模型留痕隔离测试交接（注单保存夹具）

交给 Wren 在同一台 SQL2025TEST 上重跑本包全部测试。本包没有连接生产，没有连接这台测试库，没有冻结，没有部署。

上一包已经越过 `Skipped`。失败停在绑定检查之前：`落库未全部成功：picks=FAIL,gate=ok,input=ok,snapshot=ok,source=ok`。

## 根因

注单这一步抛的是 `SqlException`，错误号 **4902**。调用栈停在 `MilkPickRepository.EnsureColumnsAsync`，由 `SaveDayAsync` 进入，再被 `SaveMilkAsync` 的 picks 捕获。`MilkRunSteps.Fail` 只保留异常类型和 `Message`，不保留调用栈，所以测试输出里的原因是：

`picks: SqlException: Cannot find the object "dbo.FbTurnAlert" because it does not exist or you do not have permissions.`

澳超这场被名单挡在出票之前，注单列表是空的，插入循环没有执行。失败发生在插入之前的补列批。`COL_LENGTH('dbo.FbTurnAlert', 'SignalCode')` 在表不存在时也返回 NULL，下一条 `ALTER TABLE dbo.FbTurnAlert` 因此报 4902。正式库这张表在，2026-11-14 目录预检里 6 个补列都在。保存代码和发布闸没有改。

测试机上的 `C:\Users\Administrator\FitTraceTest\env-evidence\test-run-20261115-bind\` 不在打包机，上面的错误号和栈是按这条唯一会先失败的语句还原的。

夹具现在按预检里的 6 个补列，以及 `SaveTurnAlerts` 的插入清单建立 `FbTurnAlert`。原来的一项拆成两项，互不影响：

1. `SaveMilk_NotReadyInstance_ReachesBindAndDoesNotPublish`：未 ready 实例到达绑定检查，原因含「模型留痕未提交」，成功轮次为 0。
2. `SaveMilk_ReadyInstance_PublishesWithItsModelFitInstanceId`：ready 实例绑定提交后真实发布，`ModelFitInstanceId` 等于该实例。

参数绑定、内容插入后取消、零值往返、容量和旧版 `82c93bce` 兼容都还在。

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

`version\COMMIT.txt` 是提交。`version\git-status.txt` 是工作区状态。`version\uncommitted.diff` 是打进本包的未提交差异。

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
