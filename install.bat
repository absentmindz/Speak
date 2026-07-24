@echo off
title Speak Installer
cd /d "%~dp0"

REM Check if running as admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo Installing Speak...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Silent
if %errorlevel% neq 0 (
    echo.
    echo Installer encountered an error. Run install.bat again or see install.ps1 for details.
    pause
)
