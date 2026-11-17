# Read-only as-of snapshot export for MilkReplay.
# Production: SELECT only. No writes. LOCK_TIMEOUT 5000, READ COMMITTED, 30s command timeout.
# Do not retry on lock timeout 1222. Do not fill from current FbOdds / FbTeamProfile / FbMatch.Handicap.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ResultsDir = 'C:\Users\Administrator\FitTraceTest\env-evidence\replay-run-20261117'
$ExportDir  = Join-Path $ResultsDir 'export'
$LogPath    = Join-Path $ExportDir 'export-log.txt'
New-Item -ItemType Directory -Force -Path $ExportDir | Out-Null

function Write-Log([string]$msg) {
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    Write-Output $line
}

function New-ProdConnection {
    $cs = 'Data Source=tcp:192.168.1.222,1433;Initial Catalog=AiFootBall;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;ApplicationIntent=ReadOnly;Pooling=False'
    $conn = New-Object System.Data.SqlClient.SqlConnection $cs
    $conn.Open()
    return $conn
}

function Export-Query {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Name,
        [string]$Sql,
        [string]$OutPath,
        [int]$TimeoutSeconds = 30
    )

    Write-Log ("START {0} -> {1}" -f $Name, $OutPath)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = $TimeoutSeconds
    $cmd.CommandType = [System.Data.CommandType]::Text

    try {
        $reader = $cmd.ExecuteReader()
        $swRead = [System.Diagnostics.Stopwatch]::StartNew()
        $header = @()
        for ($i = 0; $i -lt $reader.FieldCount; $i++) { $header += $reader.GetName($i) }

        $utf8 = New-Object System.Text.UTF8Encoding $false
        $writer = New-Object System.IO.StreamWriter($OutPath, $false, $utf8)
        $writer.WriteLine(($header -join "`t"))
        $rows = 0
        while ($reader.Read()) {
            $cells = New-Object string[] $reader.FieldCount
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                if ($reader.IsDBNull($i)) {
                    $cells[$i] = ''
                } else {
                    $val = $reader.GetValue($i)
                    if ($val -is [datetime]) {
                        $cells[$i] = ([datetime]$val).ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
                    } elseif ($val -is [datetimeoffset]) {
                        $cells[$i] = ([datetimeoffset]$val).ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
                    } else {
                        $cells[$i] = [string]$val
                    }
                    $cells[$i] = $cells[$i].Replace("`t", ' ').Replace("`r", ' ').Replace("`n", ' ')
                }
            }
            $writer.WriteLine(($cells -join "`t"))
            $rows++
        }
        $writer.Flush()
        $writer.Dispose()
        $reader.Dispose()
        $sw.Stop()
        Write-Log ("OK {0} rows={1} seconds={2:N3}" -f $Name, $rows, $sw.Elapsed.TotalSeconds)
        if ($sw.Elapsed.TotalSeconds -gt 30) {
            throw ("Query {0} exceeded 30 seconds ({1:N3}s). Stopped after write; do not retry." -f $Name, $sw.Elapsed.TotalSeconds)
        }
        return $rows
    } catch {
        $sw.Stop()
        $ex = $_
        $num = $null
        if ($ex.Exception -and $ex.Exception.InnerException) { $num = $ex.Exception.InnerException.Number }
        if (-not $num -and $ex.Exception) { $num = $ex.Exception.Number }
        Write-Log ("FAIL {0} seconds={1:N3} number={2} message={3}" -f $Name, $sw.Elapsed.TotalSeconds, $num, $ex.Exception.Message)
        if ($num -eq 1222) {
            throw ("Lock timeout 1222 on {0}. Stop. Do not retry or raise LOCK_TIMEOUT." -f $Name)
        }
        throw
    } finally {
        $cmd.Dispose()
    }
}

$prefix = @'
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
'@

$days = @(
    @{
        SalesDate  = '2026-11-14'
        RunId      = '20261114T225008-4408e9'
        DecisionAt = '2026-11-14T22:50:08.9833333'
    },
    @{
        SalesDate  = '2026-11-15'
        RunId      = '20261115T225017-c6849c'
        DecisionAt = '2026-11-15T22:50:17.8133333'
    },
    @{
        SalesDate  = '2026-11-16'
        RunId      = '20261116T215008-c60013'
        DecisionAt = '2026-11-16T21:50:08.2566667'
    }
)

Write-Log 'hostname begin-export'
Write-Log (hostname)
Write-Log ('user={0}' -f $env:USERNAME)
Write-Log 'target=tcp:192.168.1.222,1433 database=AiFootBall auth=integrated intent=ReadOnly access=SELECT-only'

$conn = $null
try {
    $conn = New-ProdConnection
    Write-Log 'connection=open'

    $pingSql = $prefix + @"

SELECT
    CAST(@@SERVERNAME AS nvarchar(128)) AS ServerName,
    DB_NAME() AS DatabaseName,
    SUSER_SNAME() AS LoginName,
    CAST(SYSDATETIME() AS datetime2(7)) AS ServerTime;
"@
    Export-Query -Connection $conn -Name 'ping' -Sql $pingSql -OutPath (Join-Path $ExportDir 'ping.tsv') | Out-Null

    foreach ($d in $days) {
        $run = $d.RunId
        $dec = $d.DecisionAt
        $sd  = $d.SalesDate

        $matchSql = $prefix + @"

SELECT
    s.RunId,
    s.SalesDate,
    s.MatchId,
    s.MatchNo,
    s.LeagueName,
    s.HomeTeam,
    s.AwayTeam,
    s.MatchTime,
    s.Outcome,
    s.Reason,
    s.Evidence,
    s.Accepted,
    s.OptionCount,
    s.Plays,
    s.LastSampleAt
FROM dbo.FbMilkSourceMatch AS s
WHERE s.RunId = N'$run'
ORDER BY s.MatchNo, s.MatchId;
"@
        Export-Query -Connection $conn -Name "matches-$sd" -Sql $matchSql -OutPath (Join-Path $ExportDir "matches-$sd.tsv") | Out-Null

        $oddsSql = $prefix + @"

WITH src AS (
    SELECT s.MatchId, s.MatchTime
    FROM dbo.FbMilkSourceMatch AS s
    WHERE s.RunId = N'$run'
),
ranked AS (
    SELECT
        h.Id,
        h.MatchId,
        h.PlayType,
        h.OptionCode,
        h.OptionName,
        h.Odds,
        h.CapturedAt,
        ROW_NUMBER() OVER (
            PARTITION BY h.MatchId, h.PlayType, h.OptionCode
            ORDER BY h.CapturedAt DESC, h.Id DESC
        ) AS rn
    FROM dbo.FbJcOddsHistory AS h
    INNER JOIN src ON src.MatchId = h.MatchId
    WHERE h.Odds > 1
      AND h.CapturedAt <= CONVERT(datetime2(7), '$dec', 126)
      AND (src.MatchTime IS NULL OR h.CapturedAt <= src.MatchTime)
)
SELECT Id, MatchId, PlayType, OptionCode, OptionName, Odds, CapturedAt
FROM ranked
WHERE rn = 1
ORDER BY MatchId, PlayType, OptionCode;
"@
        Export-Query -Connection $conn -Name "jc-odds-$sd" -Sql $oddsSql -OutPath (Join-Path $ExportDir "jc-odds-$sd.tsv") | Out-Null

        $ouSql = $prefix + @"

WITH src AS (
    SELECT s.MatchId, s.MatchTime
    FROM dbo.FbMilkSourceMatch AS s
    WHERE s.RunId = N'$run'
)
SELECT h.MatchId, h.TotalLine, h.CapturedAt
FROM dbo.FbOddsHistory AS h
INNER JOIN src ON src.MatchId = h.MatchId
WHERE h.OddsType = N'OU'
  AND h.CompanyId = 0
  AND h.TotalLine IS NOT NULL
  AND h.CapturedAt <= CONVERT(datetime2(7), '$dec', 126)
  AND (src.MatchTime IS NULL OR h.CapturedAt <= src.MatchTime)
ORDER BY h.MatchId, h.CapturedAt;
"@
        Export-Query -Connection $conn -Name "ou-$sd" -Sql $ouSql -OutPath (Join-Path $ExportDir "ou-$sd.tsv") | Out-Null

        $pickSql = $prefix + @"

SELECT *
FROM dbo.FbMilkPick
WHERE RunId = N'$run'
ORDER BY MatchNo, PlayType, OptionCode;
"@
        Export-Query -Connection $conn -Name "official-picks-$sd" -Sql $pickSql -OutPath (Join-Path $ExportDir "official-picks-$sd.tsv") | Out-Null
    }

    $outcomeSql = $prefix + @"

SELECT
    m.MatchId,
    m.MatchTime,
    m.LeagueName,
    m.HomeTeamName AS HomeTeam,
    m.AwayTeamName AS AwayTeam,
    m.HomeScore,
    m.AwayScore,
    m.Status
FROM dbo.FbMatch AS m
WHERE m.MatchId IN (
    1416115,1416124,1416128,1416117,1398632,1398681,1398644,1398638,1398641,1398683,1362554,1362429,
    1398695,1398689,1398691,1398687,
    1398699,1398700,1398702,1398701,1398647,1398692,1398649,1398694
);
"@
    Export-Query -Connection $conn -Name 'outcomes-posthoc' -Sql $outcomeSql -OutPath (Join-Path $ExportDir 'outcomes-posthoc.tsv') | Out-Null

    # One widest sample window; MilkReplay.Run still filters with SampleUsable per DecisionAt.
    # Left: TrainFrom of 2026-11-14 = 2025-05-14 00:00:00
    # Right: TrainUntil of 2026-11-16 = Decision - 150 minutes
    $sampleSql = $prefix + @"

SELECT f.MatchId, f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
       f.MatchTime, f.HomeScore, f.AwayScore
FROM dbo.FbMatch AS f
WHERE f.Status = 3
  AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
  AND f.MatchTime >= CONVERT(datetime2(7), '2025-05-14T00:00:00', 126)
  AND f.MatchTime <= DATEADD(minute, -150, CONVERT(datetime2(7), '2026-11-16T21:50:08.2566667', 126))
ORDER BY f.MatchTime, f.MatchId;
"@
    Export-Query -Connection $conn -Name 'samples' -Sql $sampleSql -OutPath (Join-Path $ExportDir 'samples.tsv') | Out-Null

    Write-Log 'export-complete'
} finally {
    if ($conn) {
        $conn.Close()
        $conn.Dispose()
        Write-Log 'connection=closed'
    }
}

Write-Log 'done'
