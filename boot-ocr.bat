@echo off
REM AiFootBall OCR launcher (port 5089). ASCII only, same reason as boot-api.bat.
cd /d "%~dp0tools\ocr"
node ocr-server.js
