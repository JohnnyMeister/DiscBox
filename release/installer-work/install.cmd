@echo off
setlocal
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
  echo.
  echo DiscBox installation failed.
  pause
  exit /b 1
)
exit /b 0
