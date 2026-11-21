param([string]$TaskName = 'AiFootBall-ResearchBridge')
$ErrorActionPreference = 'Stop'
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Write-Host "Removed task $TaskName (if present)."
