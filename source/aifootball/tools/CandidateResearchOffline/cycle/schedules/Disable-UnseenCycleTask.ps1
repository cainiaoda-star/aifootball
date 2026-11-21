param([string]$TaskName = 'AiFootBall-ResearchUnseen-Cycle')
$ErrorActionPreference = 'Stop'
$t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $t) { Write-Host "ABSENT|$TaskName"; exit 0 }
Disable-ScheduledTask -TaskName $TaskName | Out-Null
Write-Host "DISABLED|$TaskName"
