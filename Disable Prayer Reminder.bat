@echo off
title Prayer Reminder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0disable_reminder.ps1"
if errorlevel 1 (
  echo.
  echo Could not fully disable Prayer Reminder.
  pause
  exit /b 1
)

echo.
echo Prayer Reminder is stopped and disabled at startup.
echo Its files are still here, so you can enable it again later.
pause
