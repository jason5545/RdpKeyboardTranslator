@echo off
REM Restart RDP Keyboard Translator Service
REM Requires Administrator privileges

echo Restarting RDP Keyboard Translator Service...

REM Check if running as administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator
    echo Right-click and select "Run as administrator"
    pause
    exit /b 1
)

REM Stop the service
echo Stopping service...
sc stop "RdpKeyboardTranslator"

REM Wait for service to stop
echo Waiting for service to stop...
timeout /t 5 /nobreak >nul

REM Start the service
echo Starting service...
sc start "RdpKeyboardTranslator"

if %errorLevel% eq 0 (
    echo Service restarted successfully!
) else (
    echo ERROR: Failed to restart service
    echo Check service status and Windows Event Logs for details
)

pause