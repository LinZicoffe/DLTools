@echo off
setlocal
if exist "%~dp0Output\WinForms\OfflineAuthorization.WinForms.exe" (
    start "" "%~dp0Output\WinForms\OfflineAuthorization.WinForms.exe"
    exit /b 0
)
echo Please build and publish the DEMO first. See README.md in this folder.
pause
exit /b 1
