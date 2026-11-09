# Unified DB query entry - fixes sqlcmd Chinese garbling.
# Usage (PowerShell tool only, never run sqlcmd from Bash):
#   & tools\db.ps1 -Query "SELECT TOP 5 HomeTeamName FROM FbMatch"
#   & tools\db.ps1 -Query "..." -Database AiFootBall
# Why: sqlcmd output follows console codepage (GBK in Bash pipes).
# Setting [Console]::OutputEncoding = UTF8 makes output UTF-8, Chinese renders fine.
param(
    [Parameter(Mandatory = $true)][string]$Query,
    [string]$Database = "AiFootBall",
    [string]$Server = "",
    [string]$User = "",
    [string]$Password = "",
    [int]$Timeout = 60
)
# Credential is NOT hardcoded: this file is tracked by git (repo root covers the
# whole project since 2026-11-09). Password lives in tools\db-credential.ps1,
# which is git-ignored. Explicit -Server/-User/-Password still win when given.
$credFile = Join-Path $PSScriptRoot 'db-credential.ps1'
if (Test-Path $credFile) {
    $cred = & $credFile
    if (-not $Server) { $Server = $cred.Server }
    if (-not $User) { $User = $cred.User }
    if (-not $Password) { $Password = $cred.Password }
}
if (-not $Password) {
    Write-Error "Missing SQL password: create tools\db-credential.ps1 or pass -Password."
    exit 2
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
sqlcmd -S $Server -d $Database -U $User -P $Password -C -W -s'|' -l $Timeout -Q "SET NOCOUNT ON; $Query"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
