@echo off
chcp 65001 >nul
echo 正在停止 AiFootBall 服务...
taskkill /F /FI "WINDOWTITLE eq AiFootBall-*" /T >nul 2>&1
taskkill /F /IM node.exe /FI "WINDOWTITLE eq AiFootBall-OCR*" >nul 2>&1
echo 已停止。按任意键退出。
pause >nul
