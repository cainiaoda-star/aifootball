@echo off
REM AiFootBall API launcher. ASCII only: the file is read by cmd under a
REM non-UTF8 code page, so any non-ASCII byte here can break command parsing.
cd /d "%~dp0"
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

echo ==== boot %DATE% %TIME% ====>> "%~dp0_api_out.log"
"%DOTNET%" "%~dp0src\AiFootBall.Api\bin\Release\net8.0\AiFootBall.Api.dll" --contentRoot "%~dp0src\AiFootBall.Api" >> "%~dp0_api_out.log" 2>&1
echo ==== exited with %ERRORLEVEL% at %TIME% ====>> "%~dp0_api_out.log"
