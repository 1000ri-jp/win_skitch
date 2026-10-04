@echo off
setlocal
set "PYTHONW=%~dp0.venv\Scripts\pythonw.exe"
if not exist "%PYTHONW%" set "PYTHONW=%~dp0..\.venv\Scripts\pythonw.exe"
if not exist "%PYTHONW%" (
    echo Python environment not found. See python\README.md for setup.
    pause
    exit /b 1
)
start "" "%PYTHONW%" "%~dp0run.pyw"
