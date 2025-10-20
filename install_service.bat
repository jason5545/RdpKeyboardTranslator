@echo off
REM Install RDP Keyboard Translator as Windows Service
REM Requires Administrator privileges

echo Installing RDP Keyboard Translator Service...
cd /d "%~dp0"

REM Check if running as administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator
    echo Right-click and select "Run as administrator"
    pause
    exit /b 1
)

REM Build the project first
echo Building project...
dotnet build RdpKeyboardTranslator.csproj --configuration Release
if %errorLevel% neq 0 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

echo Checking if executable exists...
if exist "bin\Release\net6.0-windows\RdpKeyboardTranslator.exe" (
    echo ✓ Executable found
) else (
    echo ✗ Executable NOT found after build
    pause
    exit /b 1
)

echo.
echo Installing Windows service...
REM Get full absolute path to executable
set "FULL_PATH=%~dp0bin\Release\net6.0-windows\RdpKeyboardTranslator.exe"
echo Executable path: %FULL_PATH%
sc create RdpKeyboardTranslator binpath= "\"%FULL_PATH%\" --service" start= auto displayname= "RDP Keyboard Translator Service"

if %errorLevel% eq 0 (
    echo Service installed successfully!
    echo Starting service...
    sc start RdpKeyboardTranslator
    
    if %errorLevel% eq 0 (
        echo Service started successfully!
        echo.
        echo You can manage the service using:
        echo   - Services.msc
        echo   - sc start RdpKeyboardTranslator
        echo   - sc stop RdpKeyboardTranslator
        echo   - sc delete RdpKeyboardTranslator (to uninstall)
    ) else (
        echo Warning: Service installed but failed to start
        echo Check Windows Event Logs for details
    )
) else (
    echo ERROR: Failed to install service
)

pause