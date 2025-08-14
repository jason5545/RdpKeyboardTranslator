@echo off
REM Check RDP Keyboard Translator Service Status

echo === RDP Keyboard Translator Service Status ===
echo.

REM Query service status
sc query "RdpKeyboardTranslator"

if %errorLevel% eq 0 (
    echo.
    echo Service configuration:
    sc qc "RdpKeyboardTranslator"
    
    echo.
    echo Recent service events (last 10):
    powershell -Command "Get-WinEvent -FilterHashtable @{LogName='System'; ID=7034,7035,7036,7040} -MaxEvents 10 | Where-Object {$_.Message -like '*RdpKeyboardTranslator*'} | Format-Table TimeCreated, Id, LevelDisplayName, Message -Wrap"
) else (
    echo Service is not installed
    echo.
    echo To install the service, run: install_service.bat
)

echo.
echo Service management commands:
echo   sc start RdpKeyboardTranslator    - Start service
echo   sc stop RdpKeyboardTranslator     - Stop service  
echo   sc query RdpKeyboardTranslator    - Check status
echo   services.msc                      - Open Services Manager

pause