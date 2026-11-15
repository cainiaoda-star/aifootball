# 模型留痕隔离测试交接（非空注单）

交给 Wren 在同一台 SQL2025TEST 上重跑本包全部测试。本包没有连接生产，没有连接这台测试库，没有冻结，没有部署。

上一包 `fittrace-isolated-sql2025test-20261115-picks` 已在隔离机 28/28 通过。那 28 项原样保留。本包只多一项：`SaveMilk_ReadyInstance_PublishesAtLeastOnePick`。

## 这一项在测什么

已通过的两项仍用澳超和空赔率。澳超在不串名单里，引擎在出票前记下排除并继续，赔率读取返回空列表，所以发布的是空榜。那两项的断言没有改。

新项单独准备一场英超。样本经 `ScorelineModel.Fit` 得到 ready 模型，追踪经 `SaveReadyInstanceAsync` 提交。赔率按各玩法的官方水位配平，五个玩法的档位域都齐。总进球 2 球钉在 4.50。半全场胜平、负平钉在 3.50：这两档若进入候选，半全场提级会改概率且不重新归一，概率合计会离开 100，现有发布核对会拒绝。这是夹具避开那条路径，不是放宽门槛。

注单行只由真实的 `SaveMilkAsync` 写入。测试不插入 `FbMilkPick`，也不替换绑定检查的返回值。

清理夹具之前，测试把查询结果写进当前目录的 `fittrace-pickrow-evidence.txt`，并写入测试输出。一行里有轮次、返回种类、成功轮次数、`FbMilkPick` 行数、`FbMilkRunDate.PickCount` 合计、成功轮次的 `ModelFitInstanceId`、本次提交并用于计算的 ready 实例号。

断言：

- 返回 `Published`，且存在一行 `Status=success` 的轮次。
- 该轮次的 `FbMilkPick` 行数大于 0，并等于该轮次 `PickCount` 合计。
- 成功轮次的 `ModelFitInstanceId` 等于这次计算使用、已经提交的 ready 实例。

若合成数据被某一道闸挡住，失败信息里有返回种类和原因。不要改算法、名单、门槛或发布闸来让它通过。

打包机上用同一套合成盘跑过进程内的 `BuildDetailed` 和 `MilkProbScale.Verify`：有总进球注留下，概率合计在容差内。数据库这一项没有在打包机上执行。这台机器是 `WINSEV25DATA`，测试会拒绝连接。

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

`dotnet --version` 应为 `8.0.428`。这个工程只含 `FitTraceTests`、`FitTraceDbTests` 和旧版二进制探针，上面这条命令就是本包的全部测试。预期 29 项。不要改断言来迁就失败。不要把变量指到 `WINSEV25DATA`、`192.168.117.128` 或 `192.168.41.128`。

夹具仍会在核对实例和库名之后执行待审 DDL。只作用于 `AiFootBall_FitTrace_Test`。

文件清单是 `SHA256SUMS.txt`。每一行是大写十六进制 SHA256、两个空格、相对路径。
