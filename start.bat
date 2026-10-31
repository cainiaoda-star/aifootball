@echo off
chcp 65001 >nul
REM ============================================
REM  AiFootBall 一键启动脚本
REM  1) 启动 OCR 服务 (127.0.0.1:5089)
REM  2) 启动 API 服务 (127.0.0.1:5088)
REM  ============================================

echo [1/2] 启动 OCR 服务...
start "AiFootBall-OCR" /min cmd /c "cd /d %~dp0tools\ocr && node ocr-server.js"

echo [2/2] 启动 API 服务...
start "AiFootBall-API" /min cmd /c "cd /d %~dp0 && dotnet src\AiFootBall.Api\bin\Release\net8.0\AiFootBall.Api.dll --contentRoot %~dp0src\AiFootBall.Api"

echo.
echo 服务启动中，请稍候几秒后访问:
echo   前端页面: http://127.0.0.1:5088/
echo   OCR 服务: http://127.0.0.1:5089/health
echo.
pause
