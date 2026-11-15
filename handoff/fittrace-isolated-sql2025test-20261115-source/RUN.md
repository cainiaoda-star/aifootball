# 模型留痕隔离测试交接（源侧采集时间）

交给 Wren 在同一台 SQL2025TEST 上重跑本包全部测试。本包没有连接生产，没有连接这台测试库，没有冻结，没有部署。

上一包 `fittrace-isolated-sql2025test-20261115-pickrow` 实跑 29 项：27 通过，2 失败。非空发布通过，`pickRows=pickCount=2`，`modelFitInstanceId=readyInstanceId=42`。失败的是此前已通过的两条空榜 `SaveMilk`。两条都在新增用例开始前结束。

## 根因

两个包的代码差异只有 `FitTraceDbTests.cs`。算法、名单、门槛、发布闸、`NoParlayLeagues` 和拒绝证据格式两边字节相同。空榜用例的联赛仍是澳超，证据仍是 `list=NoParlayLeagues;league=澳超`，推荐场次仍是 88001。注单表和闸表加宽只服务非空插入，源侧查询不读这两张表。

两条失败是同一件缺失：

- `unexpected-rejection`：拒绝记录的 88001 不在源侧场次集合里。
- `rejected-rejection … 联赛不一致`：联赛核对按场次号找源行，找不到时源行为空，精确比对无法成立。

源侧快照只保留 `CapturedAt <= 本轮 runAt` 的受注卡。`runAt` 在 `SaveMilkAsync` 入口取 `DateTime.Now`。空榜夹具写的是 `CONVERT(datetime2(0), SYSDATETIME())`。这个类型把半秒及以上进到下一秒，存储值可以晚于随后取到的 `runAt`。采集完整性用的是读回的 `MAX(CapturedAt)` 等值，没有这道截止，所以本批仍算完整，核对照样执行，然后看不到这场。非空用例的受注卡已经提前两秒，所以它的源侧场次在，发布能通过。

这次只改空榜夹具的采集时间：提前两秒，仍落到整秒。三个用例仍然分开准备、分开清理。断言、发布闸、算法都没有改。半全场提级后概率不归一仍另记，没有放进这次修改。

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

非空用例在清理前把查询结果写到当前目录的 `fittrace-pickrow-evidence.txt`。

文件清单是 `SHA256SUMS.txt`。每一行是大写十六进制 SHA256、两个空格、相对路径。
