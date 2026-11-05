@echo off
REM AiFootBall one-click launcher. ASCII only on purpose: cmd reads this file
REM under the OEM code page, so Chinese text here breaks command parsing
REM (that is why the API window silently failed to start on 2026-11-03).
REM OCR service removed 2026-11-04: ticket entry is manual now.
setlocal
set "ROOT=%~dp0"

echo [1/1] starting API service (127.0.0.1:5088) ...
start "AiFootBall-API" /min "%ROOT%boot-api.bat"

echo.
echo One minimized window was opened.
echo   Web UI  : http://127.0.0.1:5088/
echo   API log : %ROOT%_api_out.log
echo.
echo Wait 8-20 seconds before opening the page.
echo If the page does not load, read the API log above first.
echo.
if /I "%~1"=="silent" exit /b 0
pause
