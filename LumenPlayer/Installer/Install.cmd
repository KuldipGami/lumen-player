@echo off
rem Double-click to install Lumen Player for the current user (no admin rights needed).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
if errorlevel 1 (
    echo.
    echo Installation failed.
)
echo.
pause
