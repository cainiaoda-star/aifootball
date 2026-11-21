# Deliver-only. DO NOT run without separate user authorization.
# Registers DISABLED task AiFootBall-ResearchUnseen-Cycle every 30 minutes, no overlap.
# Deprecated: schedules-proposal\install-research-bridge.ps1 (10y trigger / fixed TSV / no export / no attach).
param(
  [string]$PackageRoot = (Split-Path $PSScriptRoot -Parent),
  [string]$TaskName = 'AiFootBall-ResearchUnseen-Cycle',
  [switch]$EnableAfterRegister
)
$ErrorActionPreference = 'Stop'
Write-Host 'NOT AUTO-INSTALLED. Review then run only after explicit authorization.'

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
  Write-Host "BLOCKED: task already exists: $TaskName (State=$($existing.State)). Use Status/Disable/Uninstall scripts. Will not duplicate or overwrite unrelated tasks."
  exit 3
}
# also warn on deprecated name
$old = Get-ScheduledTask -TaskName 'AiFootBall-ResearchBridge' -ErrorAction SilentlyContinue
if ($old) {
  Write-Host "NOTE: deprecated task AiFootBall-ResearchBridge still present (State=$($old.State)). Do not use it; uninstall separately if desired."
}

$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$cycle = Join-Path $PackageRoot 'scripts\UnseenValidationCycle.ps1'
$cfg = Join-Path $PackageRoot 'config\cycle.local.json'
if (-not (Test-Path $cycle)) { throw "cycle script missing: $cycle" }
if (-not (Test-Path $cfg)) { throw "config missing: $cfg" }

$arg = "-NoProfile -ExecutionPolicy Bypass -File `"$cycle`" -ConfigPath `"$cfg`""
$action = New-ScheduledTaskAction -Execute $ps -Argument $arg -WorkingDirectory $PackageRoot
# RepetitionDuration must be within Task Scheduler XML range (MaxValue is rejected as out of range).
# Use ~10 years; task remains until explicitly disabled/uninstalled.
$startAt = (Get-Date).AddMinutes(1)
$trigger = New-ScheduledTaskTrigger -Once -At $startAt -RepetitionInterval (New-TimeSpan -Minutes 30) -RepetitionDuration (New-TimeSpan -Days 3650)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
# Run as current user by default when authorized; does not elevate.
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
if (-not $EnableAfterRegister) {
  Disable-ScheduledTask -TaskName $TaskName | Out-Null
  Write-Host "Registered DISABLED task $TaskName. Enable only after separate authorization."
} else {
  Write-Host "Registered ENABLED task $TaskName (EnableAfterRegister set)."
}
Write-Host "Account=$env:USERNAME"
Write-Host "Execute=$ps"
Write-Host "Arguments=$arg"
Write-Host "WorkingDirectory=$PackageRoot"
Write-Host "Trigger=every-30m|MultipleInstances=IgnoreNew"
Write-Host "Logs=$PackageRoot\cycle-runs\<runId>\"
Write-Host "OnFailure=exit1-summary-FAULT|mutex-busy-exit2"
