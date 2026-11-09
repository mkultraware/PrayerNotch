param(
    [string]$AppPath = (Join-Path $PSScriptRoot 'PrayerReminder.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $AppPath)) {
    throw 'PrayerReminder.exe is missing. Run build.bat first.'
}

$startupFolder = [Environment]::GetFolderPath('Startup')
$legacyShortcut = Join-Path $startupFolder 'Prayer Reminder.lnk'
if (Test-Path -LiteralPath $legacyShortcut) {
    Remove-Item -LiteralPath $legacyShortcut -Force
}

$legacyScript = Join-Path $startupFolder 'Prayer Reminder.vbs'
if (Test-Path -LiteralPath $legacyScript) {
    Remove-Item -LiteralPath $legacyScript -Force
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $runKey -Name 'PrayerReminder' -PropertyType String -Value ('"' + $AppPath + '"') -Force | Out-Null

Write-Host 'Prayer Reminder will now start when you sign in.'
