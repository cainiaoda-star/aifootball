# 模型留痕隔离测试交接（参数绑定补丁）

交给 Wren 在同一台 SQL2025TEST 上重跑。本包没有连接生产，没有连接这台测试库，没有冻结，没有部署。

上一包的 0.684 秒是容量测试在提交失败时记下的耗时，不是成功写入的耗时。不要拿它当基准。

## 这一包改了什么

`MilkModelFitStore.InsertContentAsync` 里，`new SqlParameter("@smin", FitContentCodec.ScoreMin)` 的 `ScoreMin` 是常量 0。这个调用选中 `SqlParameter(string, SqlDbType)`，`Value` 保持空，SQL 就报 `@smin` 未提供。同一条插入上的整数、浮点和 bit 都改成先指定 `SqlDbType`，再写 `Value`。DDL 没改，算法没改。

进程内测试 `ConstantZero_BindsAsSqlDbType_UnlessTheValueIsSetExplicitly` 锁住这个重载，不连数据库。打包机上这项已通过。

数据库往返 `ZeroScoreMin_AndZeroGlobalAway_RoundTripThroughSql` 在隔离库里写入并读回 `ScoreMin = 0`、`GlobalAway = 0` 以及同一行上的其它数值常数。

取消测试改为：内容行插入成功之后才取消令牌，再进入子表批量写入。断言要求已经进入写入，并且抛出的是 `OperationCanceledException`，不是参数错误。就绪行仍须是 0。

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

`version\COMMIT.txt` 是提交。`version\git-status.txt` 是工作区状态。`version\uncommitted.diff` 是打进本包的未提交差异，包含参数绑定和测试。

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
dotnet test source\aifootball\tests\AiFootBall.Tests\AiFootBall.Tests.csproj --filter "FullyQualifiedName~FitTraceDbTests" --nologo
```

`dotnet --version` 应为 `8.0.428`。这个过滤器包含原先 10 项数据库测试，加上新的数值往返。不要改断言来迁就失败。不要把变量指到 `WINSEV25DATA`、`192.168.117.128` 或 `192.168.41.128`。

夹具仍会在核对实例和库名之后执行待审 DDL。只作用于 `AiFootBall_FitTrace_Test`。

文件清单是 `SHA256SUMS.txt`。每一行是大写十六进制 SHA256、两个空格、相对路径。
