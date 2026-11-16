# 模型留痕候选隔离复测

交给 Wren 在 SQL2025TEST 上重跑全部 29 项。本包不连接生产，不启动 API，不启动调度，不执行生产 DDL，不切换现网，不触发出榜。

此前 source 包的 29/29 只对应该包解压后的文件清单。Wren 没有拿到那个 zip，不能把那次结果写成已经独立核验过 zip 哈希。那次也没有加载本目录里的四个候选 DLL。

## 被测字节

冻结 commit：`97927cbcb5e26e4ad9d8654960f503637009713e`

四个候选 DLL 在 `deploy-package/pending/football-97927cbcb5e2/`，字节与候选部署目录相同：

| 文件 | SHA256 |
| --- | --- |
| AiFootBall.Api.dll | `38E3ADE7A23661B0662F0771930928D5FB4B158CE1A178F0B6128C594AA952D7` |
| AiFootBall.Core.dll | `0928542BAD6EEA36CA28FE8710845CFFD699C36E64F8F78E3058288FB6B1B3D0` |
| AiFootBall.Data.dll | `19BEFE683A7D3F94FB6B1C13F39B705B7F0F68B2AF17BB330532A3C4617EC473` |
| AiFootBall.Sources.dll | `1D3E3D68CC03B0E942424DA113E32F81200A55093CB1BB860C500047E00F5022` |

`deploy-package/pending/football-5c3911c/` 仍是旧版 `82c93bce`，只给原有的旧二进制探针使用。

源码来自该 commit。`OptionBaseline.cs` 的 git 对象是 `de957d16e115d41bf23a77b186794b34f4427bfb`。条件频率实验文件没有打进包。

相对 commit，包内只多了复测夹具，没有改算法：

- `CandidateLoadGuard.cs`：测试进程启动时读取实际加载的四个程序集路径和 SHA256。不一致就中止。加载 `AiFootBall.Api.dll` 只用 `Assembly.LoadFrom`，不调用入口。
- `FitTraceDbTests.cs` 在三条 `SaveMilk` 清理表之前把结果和落库行写出来。断言没有改。差异在 `version/harness.diff`。

构建命令、SDK 和依赖版本在 `version/BUILD.txt`。

## 目标

- 主机：SQL2025TEST
- 数据库：`AiFootBall_FitTrace_Test`
- 认证：`config\sql2025test-integrated.cmd`
- 变量名 `AIFOOTBALL_FITTRACE_CONN`、`AIFOOTBALL_FITTRACE_INSTANCE` 不是实例名
- 不要把变量指到 `WINSEV25DATA`、`192.168.117.128` 或 `192.168.41.128`

## 执行

在解压后的本目录：

```bat
run-candidate.cmd
```

脚本先 `dotnet build -c Release`，再把四个候选 DLL 覆盖到测试输出，然后 `dotnet test -c Release --no-build`。覆盖后的哈希必须与上表一致，否则脚本停止。测试进程里的 `CandidateLoadGuard` 再核对一次实际加载路径。不要手写一条不带 `--no-build` 的 `dotnet test`。

预期仍是 29 项：进程内 16，数据库 13。不要改断言。

## 要带回的输出

- 控制台里的四行加载记录，以及包根目录的 `candidate-load-evidence.txt`。每行要有 `path`、`sha`、`match=true`。
- `fittrace-savemilk-evidence.txt`。三条 `SaveMilk` 各一行，写在 `RestoreStubMilkTables` 之前，含 `kind`、`reason` 和清理前的 `runRows`、`status`、`successRows`、`pickRows`、`pickCount`、`modelFitInstanceId`。
- 非空那条同时仍写 `fittrace-pickrow-evidence.txt`。

这三份都在清理前生成。清理会删掉夹具表里的当轮数据，所以以这两个证据文件和控制台为准。
