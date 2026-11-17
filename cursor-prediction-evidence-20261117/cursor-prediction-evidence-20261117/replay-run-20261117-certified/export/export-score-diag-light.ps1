# Light as-of score-diag: official first/last BF snapshot comparison,
# but only those two timestamps are normalized. The full official ScoreDiagSql
# (all intermediate rounds) exceeded 30s for some matches and is not retried.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ResultsDir = 'C:\Users\Administrator\FitTraceTest\env-evidence\replay-run-20261117-certified'
$ExportDir  = Join-Path $ResultsDir 'export'
$ChunkDir   = Join-Path $ExportDir 'score-diag-light-chunks'
$LogPath    = Join-Path $ExportDir 'export-log.txt'
New-Item -ItemType Directory -Force -Path $ChunkDir | Out-Null

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
        $header = @()
        for ($i = 0; $i -lt $reader.FieldCount; $i++) { $header += $reader.GetName($i) }
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $writer = New-Object System.IO.StreamWriter($OutPath, $false, $utf8)
        $writer.WriteLine(($header -join "`t"))
        $rows = 0
        while ($reader.Read()) {
            $cells = New-Object string[] $reader.FieldCount
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                if ($reader.IsDBNull($i)) { $cells[$i] = '' }
                else {
                    $val = $reader.GetValue($i)
                    if ($val -is [datetime]) { $cells[$i] = ([datetime]$val).ToString('yyyy-MM-ddTHH:mm:ss.fffffff') }
                    else { $cells[$i] = [string]$val }
                    $cells[$i] = $cells[$i].Replace("`t", ' ').Replace("`r", ' ').Replace("`n", ' ')
                }
            }
            $writer.WriteLine(($cells -join "`t"))
            $rows++
        }
        $writer.Flush(); $writer.Dispose(); $reader.Dispose()
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
        if ($num -eq 1222) { throw ("Lock timeout 1222 on {0}. Stop. Do not retry or raise LOCK_TIMEOUT." -f $Name) }
        throw
    } finally { $cmd.Dispose() }
}

function Get-LightDiagSql([long]$MatchId, [string]$RunId, [string]$DecisionAt) {
    return @"
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

WITH src AS (
    SELECT TOP (1) s.MatchId, s.MatchNo, s.MatchTime
    FROM dbo.FbMilkSourceMatch AS s
    WHERE s.MatchId = $MatchId AND s.RunId = N'$RunId'
),
snaps AS (
    SELECT h.MatchId, h.CapturedAt, COUNT(*) AS n
    FROM dbo.FbJcOddsHistory AS h
    INNER JOIN src ON src.MatchId = h.MatchId
    WHERE h.PlayType IN (N'BF', N'bf') AND h.Odds > 1
      AND h.CapturedAt <= CONVERT(datetime2(7), '$DecisionAt', 126)
      AND (src.MatchTime IS NULL OR h.CapturedAt <= src.MatchTime)
    GROUP BY h.MatchId, h.CapturedAt
    HAVING COUNT(*) >= 20
),
bounds AS (
    SELECT MatchId, MIN(CapturedAt) AS fat, MAX(CapturedAt) AS lat, COUNT(*) AS n
    FROM snaps
    GROUP BY MatchId
),
pts AS (
    SELECT h.MatchId, h.CapturedAt, h.OptionCode, h.Odds,
           CASE WHEN CHARINDEX(':', h.OptionCode) > 0
                THEN CAST(LEFT(h.OptionCode, CHARINDEX(':', h.OptionCode) - 1) AS int)
                   - CAST(SUBSTRING(h.OptionCode, CHARINDEX(':', h.OptionCode) + 1, 3) AS int)
                WHEN UNICODE(LEFT(h.OptionCode, 1)) = 32988 THEN 5
                WHEN UNICODE(LEFT(h.OptionCode, 1)) = 36127 THEN -5
                ELSE 0 END AS gd,
           CASE WHEN CHARINDEX(':', h.OptionCode) > 0
                THEN CAST(LEFT(h.OptionCode, CHARINDEX(':', h.OptionCode) - 1) AS int)
                   + CAST(SUBSTRING(h.OptionCode, CHARINDEX(':', h.OptionCode) + 1, 3) AS int)
                ELSE 3 END AS tg
    FROM dbo.FbJcOddsHistory AS h
    INNER JOIN bounds b ON b.MatchId = h.MatchId
    WHERE h.PlayType IN (N'BF', N'bf') AND h.Odds > 1
      AND (h.CapturedAt = b.fat OR h.CapturedAt = b.lat)
),
norm AS (
    SELECT p.*, 1.0 / p.Odds / SUM(1.0 / p.Odds) OVER (PARTITION BY p.MatchId, p.CapturedAt) AS p
    FROM pts p
),
per AS (
    SELECT MatchId, CapturedAt,
           SUM(gd * p) AS exp_gd, SUM(tg * p) AS exp_tg,
           SUM(CASE WHEN gd >  0 THEN p ELSE 0 END) AS p_home,
           SUM(CASE WHEN gd =  0 THEN p ELSE 0 END) AS p_draw,
           SUM(CASE WHEN gd >= 3 THEN p ELSE 0 END) AS p3,
           SUM(CASE WHEN gd <= -3 THEN p ELSE 0 END) AS pm3
    FROM norm
    GROUP BY MatchId, CapturedAt
),
fr AS (SELECT p.* FROM per p INNER JOIN bounds b ON b.MatchId = p.MatchId AND b.fat = p.CapturedAt),
lr AS (SELECT p.* FROM per p INNER JOIN bounds b ON b.MatchId = p.MatchId AND b.lat = p.CapturedAt),
fraw AS (SELECT n.MatchId, n.OptionCode, n.Odds, n.gd FROM norm n INNER JOIN bounds b ON b.MatchId = n.MatchId AND n.CapturedAt = b.fat),
lraw AS (SELECT n.MatchId, n.OptionCode, n.Odds, n.gd FROM norm n INNER JOIN bounds b ON b.MatchId = n.MatchId AND n.CapturedAt = b.lat),
deepdrop AS (
    SELECT l.MatchId,
           SUM(CASE WHEN l.gd <= -3 AND l.Odds <= f.Odds * 0.95 THEN 1 ELSE 0 END) AS neg_drop,
           SUM(CASE WHEN l.gd >=  3 AND l.Odds <= f.Odds * 0.95 THEN 1 ELSE 0 END) AS pos_drop
    FROM lraw l JOIN fraw f ON f.MatchId = l.MatchId AND f.OptionCode = l.OptionCode
    GROUP BY l.MatchId
),
dist AS (
    SELECT n.MatchId, n.gd, SUM(n.p) AS p
    FROM norm n INNER JOIN bounds b ON b.MatchId = n.MatchId AND n.CapturedAt = b.lat
    GROUP BY n.MatchId, n.gd
),
distj AS (
    SELECT MatchId,
           STRING_AGG(CAST(gd AS varchar(4)) + ':' + CAST(CAST(ROUND(p * 1000, 0) AS int) AS varchar(6)), ',')
               WITHIN GROUP (ORDER BY gd) AS DistText
    FROM dist GROUP BY MatchId
)
SELECT
    src.MatchId, src.MatchNo, b.n AS Rounds, b.fat AS FirstAt, b.lat AS LastAt,
    l.exp_gd AS ExpGd, f.exp_gd AS ExpGdOpen, l.exp_gd - f.exp_gd AS ExpGdMove, l.exp_tg AS ExpTotal,
    l.p_home AS PHome, l.p_draw AS PDraw, 1.0 - l.p_home - l.p_draw AS PAway, f.p_home AS PHomeOpen,
    l.p3 AS P3Plus, l.pm3 AS PMinus3, l.p3 - f.p3 AS P3PlusMove, l.pm3 - f.pm3 AS PMinus3Move,
    ISNULL(d.neg_drop, 0) AS DeepNegDrop, ISNULL(d.pos_drop, 0) AS DeepPosDrop, dj.DistText,
    CASE
        WHEN ISNULL(d.neg_drop,0) >= 3 AND ISNULL(d.pos_drop,0) >= 3 THEN N'两端整批降价'
        WHEN ISNULL(d.neg_drop,0) >= 3 THEN N'客队深档整批降价'
        WHEN ISNULL(d.pos_drop,0) >= 3 THEN N'主队深档整批降价'
        WHEN l.p3 - f.p3 >= 0.01 AND l.pm3 - f.pm3 >= 0.01 THEN N'两端加厚'
        WHEN l.p3 - f.p3 >= 0.01 THEN N'主队深档加价'
        WHEN l.pm3 - f.pm3 >= 0.01 THEN N'客队深档加价'
        WHEN l.p3 - f.p3 <= -0.01 AND l.pm3 - f.pm3 <= -0.01 THEN N'两端削薄'
        WHEN l.p3 - f.p3 <= -0.01 THEN N'主队深档减价'
        WHEN l.pm3 - f.pm3 <= -0.01 THEN N'客队深档减价'
        ELSE N'尾部未动'
    END AS TailTag,
    N'dbo.FbJcOddsHistory BF first/last qualifying snapshots as-of DecisionAt (official ScoreDiag first-vs-last口径; intermediate rounds not rescanned)' AS SourceTable,
    CASE WHEN l.MatchId IS NOT NULL THEN N'certified' ELSE N'uncertified-insufficient-bf' END AS Certification
FROM src
LEFT JOIN bounds b ON b.MatchId = src.MatchId
LEFT JOIN lr l ON l.MatchId = src.MatchId
LEFT JOIN fr f ON f.MatchId = src.MatchId
LEFT JOIN deepdrop d ON d.MatchId = src.MatchId
LEFT JOIN distj dj ON dj.MatchId = src.MatchId;
"@
}

$diagDays = @(
    @{ SalesDate='2026-11-14'; RunId='20261114T225008-4408e9'; DecisionAt='2026-11-14T22:50:08.9833333'; Ids=@(1416115,1398632,1398681,1416124,1416128,1416117,1398644,1398638,1398641,1398683,1362554,1362429) },
    @{ SalesDate='2026-11-15'; RunId='20261115T225017-c6849c'; DecisionAt='2026-11-15T22:50:17.8133333'; Ids=@(1398695,1398689,1398691,1398687) },
    @{ SalesDate='2026-11-16'; RunId='20261116T215008-c60013'; DecisionAt='2026-11-16T21:50:08.2566667'; Ids=@(1398699,1398700,1398702,1398701,1398647,1398692,1398649,1398694) }
)

Write-Log ('hostname=' + (hostname))
Write-Log 'score-diag-light begin'

$conn = $null
try {
    $conn = New-ProdConnection
    Write-Log 'connection=open score-diag-light'
    foreach ($d in $diagDays) {
        $chunkFiles = @()
        foreach ($mid in $d.Ids) {
            $name = 'score-diag-light-{0}-{1}' -f $d.SalesDate, $mid
            $out = Join-Path $ChunkDir ($name + '.tsv')
            $sql = Get-LightDiagSql -MatchId $mid -RunId $d.RunId -DecisionAt $d.DecisionAt
            Export-Query -Connection $conn -Name $name -Sql $sql -OutPath $out | Out-Null
            $chunkFiles += $out
        }
        $merged = Join-Path $ExportDir ('score-diag-' + $d.SalesDate + '.tsv')
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $writer = New-Object System.IO.StreamWriter($merged, $false, $utf8)
        $headerWritten = $false
        $total = 0
        foreach ($file in $chunkFiles) {
            $first = $true
            foreach ($line in [System.IO.File]::ReadLines($file, $utf8)) {
                if ($first) {
                    if (-not $headerWritten) { $writer.WriteLine($line); $headerWritten = $true }
                    $first = $false
                    continue
                }
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                $writer.WriteLine($line)
                $total++
            }
        }
        $writer.Flush(); $writer.Dispose()
        Write-Log ("MERGED score-diag-{0} rows={1} path={2}" -f $d.SalesDate, $total, $merged)
    }
    Write-Log 'score-diag-light-complete'
} finally {
    if ($conn) { $conn.Close(); $conn.Dispose(); Write-Log 'connection=closed score-diag-light' }
}
