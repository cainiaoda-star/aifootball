@echo off
REM 模型留痕隔离测试的本地集成认证模板。
REM 主机 SQL2025TEST，SQL Server 2025 默认实例，空库 AiFootBall_FitTrace_Test。
REM 变量名里的 FITTRACE 是现有测试读取的环境变量名，不是实例名。
REM 不要写成 SQL2025TEST\FITTRACE，也不要写成 SQL2025TEST\MSSQLSERVER。
REM 不填账号，不填密码。
set AIFOOTBALL_FITTRACE_CONN=Server=SQL2025TEST;Database=AiFootBall_FitTrace_Test;Integrated Security=true
set AIFOOTBALL_FITTRACE_INSTANCE=SQL2025TEST
