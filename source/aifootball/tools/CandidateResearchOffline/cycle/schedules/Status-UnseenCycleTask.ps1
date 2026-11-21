param([string]$TaskName = 'AiFootBall-ResearchUnseen-Cycle')
$t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $t) { Write-Host "ABSENT|$TaskName"; exit 0 }
$i = Get-ScheduledTaskInfo -TaskName $TaskName
Write-Host "PRESENT|$TaskName|State=$($t.State)|LastTaskResult=$($i.LastTaskResult)|LastRun=$($i.LastRunTime)|NextRun=$($i.NextRunTime)"
$old = Get-ScheduledTask -TaskName 'AiFootBall-ResearchBridge' -ErrorAction SilentlyContinue
if ($old) { Write-Host "DEPRECATED_PRESENT|AiFootBall-ResearchBridge|State=$($old.State)|DO-NOT-USE" }
