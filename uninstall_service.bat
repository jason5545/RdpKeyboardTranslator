@echo off
REM Uninstall RDP Keyboard Translator Windows Service
REM Requires Administrator privileges

echo Uninstalling RDP Keyboard Translator Service...

REM Check if running as administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator
    echo Right-click and select "Run as administrator"
    pause
    exit /b 1
)

REM Stop the service if running
echo Stopping service...
sc stop "RdpKeyboardTranslator"
timeout /t 3 /nobreak >nul

REM Delete the service
echo Removing service...
sc delete "RdpKeyboardTranslator"

if %errorLevel% eq 0 (
    echo Service uninstalled successfully!
) else (
    echo ERROR: Failed to uninstall service
    echo The service may not be installed or may still be running
)

pause