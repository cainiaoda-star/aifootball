param([string]$TaskName = 'AiFootBall-ResearchUnseen-Cycle')
$ErrorActionPreference = 'Stop'
$t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $t) { Write-Host "ABSENT|$TaskName"; exit 0 }
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host "UNINSTALLED|$TaskName"
Write-Host 'NOTE: does not remove deprecated AiFootBall-ResearchBridge; uninstall that name explicitly if present.'
