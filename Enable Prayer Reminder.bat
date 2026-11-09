@echo off
title Prayer Reminder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install_startup.ps1" -AppPath "%~dp0PrayerReminder.exe"
if errorlevel 1 (
  echo.
  echo Could not enable Prayer Reminder.
  pause
  exit /b 1
)

start "" "%~dp0PrayerReminder.exe"
echo.
echo Prayer Reminder is running and will start when you sign in.
pause
