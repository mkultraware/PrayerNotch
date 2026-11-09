@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Microsoft .NET Framework compiler was not found.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:app.manifest /out:PrayerReminder.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll PrayerReminder.cs
if errorlevel 1 exit /b %errorlevel%

"%CSC%" /nologo /target:exe /optimize+ /platform:anycpu /main:PrayerReminder.Tests /out:PrayerReminder.Tests.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll PrayerReminder.cs PrayerReminder.Tests.cs
if errorlevel 1 exit /b %errorlevel%

PrayerReminder.Tests.exe
exit /b %errorlevel%
