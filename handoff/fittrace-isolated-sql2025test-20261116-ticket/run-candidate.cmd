@echo off
setlocal
cd /d "%~dp0"
echo SDK:
dotnet --version
if errorlevel 1 exit /b 1
call config\sql2025test-integrated.cmd

dotnet build source\aifootball\tests\AiFootBall.Tests\AiFootBall.Tests.csproj -c Release --nologo
if errorlevel 1 exit /b 1

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0overlay-candidate.ps1"
if errorlevel 1 exit /b 1

dotnet test source\aifootball\tests\AiFootBall.Tests\AiFootBall.Tests.csproj -c Release --no-build --nologo
exit /b %ERRORLEVEL%
