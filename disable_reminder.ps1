$ErrorActionPreference = 'Stop'

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'PrayerReminder' -ErrorAction SilentlyContinue

$startupFolder = [Environment]::GetFolderPath('Startup')
@('Prayer Reminder.lnk', 'Prayer Reminder.vbs') | ForEach-Object {
    $legacyPath = Join-Path $startupFolder $_
    if (Test-Path -LiteralPath $legacyPath) {
        Remove-Item -LiteralPath $legacyPath -Force
    }
}

$appPath = Join-Path $PSScriptRoot 'PrayerReminder.exe'
if (Test-Path -LiteralPath $appPath) {
    Start-Process -FilePath $appPath -ArgumentList '--stop' -WindowStyle Hidden -Wait
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Process -Name 'PrayerReminder' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
}

$remainingProcess = Get-Process -Name 'PrayerReminder' -ErrorAction SilentlyContinue
if ($remainingProcess) {
    $remainingProcess | Stop-Process -Force
}
Write-Host 'Prayer Reminder has been disabled.'
