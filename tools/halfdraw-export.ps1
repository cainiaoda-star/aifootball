# Export JC matches + odds + results to JSON for tools/halfdraw.js
# Pure ASCII only (PowerShell 5.1 reads .ps1 as ANSI).
param([string]$From = '2026-10-05', [string]$To = '2026-11-09', [string]$Out = 'temp\halfdraw.json', [switch]$Live)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$cred = & (Join-Path $PSScriptRoot 'db-credential.ps1')
$connStr = "Server=$($cred.Server);Database=AiFootBall;User Id=$($cred.User);Password=$($cred.Password);TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandTimeout = 180
$scoreFilter = if ($Live) { '' } else { 'AND f.HomeScore IS NOT NULL AND f.HalfHomeScore IS NOT NULL' }
$cmd.CommandText = @"
WITH spf AS (
  SELECT MatchId, MAX(CASE WHEN OptionCode='3' THEN Odds END) AS o3,
    MAX(CASE WHEN OptionCode='1' THEN Odds END) AS o1, MAX(CASE WHEN OptionCode='0' THEN Odds END) AS o0
  FROM dbo.FbJcOddsHistory WHERE PlayType='SPF' GROUP BY MatchId
), bq AS (
  SELECT MatchId, MAX(CASE WHEN OptionCode='3-1' THEN Odds END) AS o31,
    MAX(CASE WHEN OptionCode='0-1' THEN Odds END) AS o01,
    MAX(CASE WHEN OptionCode='3-3' THEN Odds END) AS o33,
    MAX(CASE WHEN OptionCode='0-0' THEN Odds END) AS o00,
    MAX(CASE WHEN OptionCode='1-1' THEN Odds END) AS o11
  FROM dbo.FbJcOddsHistory WHERE PlayType='BQ' GROUP BY MatchId
), jq AS (
  SELECT MatchId, MAX(CASE WHEN OptionCode='0' THEN Odds END) AS j0,
    MAX(CASE WHEN OptionCode='1' THEN Odds END) AS j1, MAX(CASE WHEN OptionCode='2' THEN Odds END) AS j2,
    MAX(CASE WHEN OptionCode='3' THEN Odds END) AS j3, MAX(CASE WHEN OptionCode='4' THEN Odds END) AS j4,
    MAX(CASE WHEN OptionCode='5' THEN Odds END) AS j5, MAX(CASE WHEN OptionCode='6' THEN Odds END) AS j6,
    MAX(CASE WHEN OptionCode='7' THEN Odds END) AS j7
  FROM dbo.FbJcOddsHistory WHERE PlayType='JQ' GROUP BY MatchId
)
SELECT m.MatchId, m.MatchNo2 AS MatchNo, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
       m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore,
       s.o3, s.o1, s.o0, b.o31, b.o01, b.o33, b.o00, b.o11,
       q.j0, q.j1, q.j2, q.j3, q.j4, q.j5, q.j6, q.j7,
       CASE WHEN m.HalfHomeScore=m.HomeScore AND m.HalfAwayScore=m.AwayScore THEN 1 ELSE 0 END AS ht_copy
FROM (SELECT f.*, (SELECT TOP 1 h.MatchNo FROM dbo.FbJcOddsHistory h WHERE h.MatchId=f.MatchId AND h.PlayType='BQ') AS MatchNo2
      FROM dbo.FbMatch f
      WHERE f.MatchTime >= '$From' AND f.MatchTime < '$To' $scoreFilter
        AND EXISTS (SELECT 1 FROM dbo.FbJcOddsHistory h WHERE h.MatchId=f.MatchId AND h.PlayType='BQ')) m
LEFT JOIN spf s ON s.MatchId=m.MatchId
LEFT JOIN bq b ON b.MatchId=m.MatchId
LEFT JOIN jq q ON q.MatchId=m.MatchId
"@
$dt = New-Object System.Data.DataTable
$rd = $cmd.ExecuteReader(); $dt.Load($rd); $rd.Close(); $conn.Close()

$list = New-Object System.Collections.ArrayList
foreach ($r in $dt.Rows) {
    $o = [ordered]@{
        matchId = $r['MatchId']; matchNo = [string]$r['MatchNo']; league = [string]$r['LeagueName']
        home = [string]$r['HomeTeamName']; away = [string]$r['AwayTeamName']
        time = ([datetime]$r['MatchTime']).ToString('yyyy-MM-dd HH:mm')
        h = $(if ($r['HomeScore'] -is [DBNull]) { $null } else { [int]$r['HomeScore'] })
        a = $(if ($r['AwayScore'] -is [DBNull]) { $null } else { [int]$r['AwayScore'] })
        hh = $(if ($r['HalfHomeScore'] -is [DBNull]) { $null } else { [int]$r['HalfHomeScore'] })
        ha = $(if ($r['HalfAwayScore'] -is [DBNull]) { $null } else { [int]$r['HalfAwayScore'] })
        htCopy = [int]$r['ht_copy']
    }
    foreach ($c in @('o3','o1','o0','o31','o01','o33','o00','o11','j0','j1','j2','j3','j4','j5','j6','j7')) {
        $v = $r[$c]
        if ($v -is [DBNull] -or $null -eq $v) { $o[$c] = $null }
        else { $o[$c] = [double]$v }
    }
    [void]$list.Add([pscustomobject]$o)
}
$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$json = ConvertTo-Json -InputObject $list -Depth 3 -Compress
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $Out), $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("exported " + $list.Count + " rows to " + $Out)
