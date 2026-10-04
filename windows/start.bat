@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
if errorlevel 1 (
    echo WinSkitch could not start. See the message above.
    pause
    exit /b 1
)
