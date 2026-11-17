# Remaining read-only side-input export.
# Does not retry the timed-out 12-match score-diag-2026-11-14 query.
# Completes 11-15/11-16 fast exports, and score-diag one MatchId at a time.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ResultsDir = 'C:\Users\Administrator\FitTraceTest\env-evidence\replay-run-20261117-certified'
$ExportDir  = Join-Path $ResultsDir 'export'
$ChunkDir   = Join-Path $ExportDir 'score-diag-chunks'
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

function Get-Prefix { return @'
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
'@ }

function Get-DiagSql([long]$MatchId, [string]$DecisionAt) {
    $prefix = Get-Prefix
    return $prefix + @"

WITH src AS (
    SELECT s.MatchId, s.MatchNo, s.MatchTime
    FROM dbo.FbMilkSourceMatch AS s
    WHERE s.MatchId = $MatchId
),
base AS (
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
    INNER JOIN src ON src.MatchId = h.MatchId
    WHERE h.PlayType IN (N'BF', N'bf') AND h.Odds > 1
      AND h.CapturedAt <= CONVERT(datetime2(7), '$DecisionAt', 126)
      AND (src.MatchTime IS NULL OR h.CapturedAt <= src.MatchTime)
),
cnt AS (
    SELECT MatchId, CapturedAt, COUNT(*) AS n FROM base GROUP BY MatchId, CapturedAt
),
ok AS (
    SELECT b.* FROM base b JOIN cnt c ON c.MatchId = b.MatchId AND c.CapturedAt = b.CapturedAt
    WHERE c.n >= 20
),
norm AS (
    SELECT MatchId, CapturedAt, OptionCode, Odds, gd, tg,
           1.0 / Odds / SUM(1.0 / Odds) OVER (PARTITION BY MatchId, CapturedAt) AS p
    FROM ok
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
rk AS (
    SELECT *,
           ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt) AS rf,
           ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rl,
           MIN(CapturedAt) OVER (PARTITION BY MatchId) AS fat,
           MAX(CapturedAt) OVER (PARTITION BY MatchId) AS lat,
           COUNT(*) OVER (PARTITION BY MatchId) AS n
    FROM per
),
fr AS (SELECT * FROM rk WHERE rf = 1),
lr AS (SELECT * FROM rk WHERE rl = 1),
fraw AS (
    SELECT n.MatchId, n.OptionCode, n.Odds, n.gd
    FROM norm n JOIN fr ON fr.MatchId = n.MatchId AND fr.CapturedAt = n.CapturedAt
),
lraw AS (
    SELECT n.MatchId, n.OptionCode, n.Odds, n.gd
    FROM norm n JOIN lr ON lr.MatchId = n.MatchId AND lr.CapturedAt = n.CapturedAt
),
deepdrop AS (
    SELECT l.MatchId,
           SUM(CASE WHEN l.gd <= -3 AND l.Odds <= f.Odds * 0.95 THEN 1 ELSE 0 END) AS neg_drop,
           SUM(CASE WHEN l.gd >=  3 AND l.Odds <= f.Odds * 0.95 THEN 1 ELSE 0 END) AS pos_drop
    FROM lraw l JOIN fraw f ON f.MatchId = l.MatchId AND f.OptionCode = l.OptionCode
    GROUP BY l.MatchId
),
dist AS (
    SELECT n.MatchId, n.gd, SUM(n.p) AS p
    FROM norm n JOIN lr ON lr.MatchId = n.MatchId AND lr.CapturedAt = n.CapturedAt
    GROUP BY n.MatchId, n.gd
),
distj AS (
    SELECT MatchId,
           STRING_AGG(CAST(gd AS varchar(4)) + ':' + CAST(CAST(ROUND(p * 1000, 0) AS int) AS varchar(6)), ',')
               WITHIN GROUP (ORDER BY gd) AS DistText
    FROM dist GROUP BY MatchId
)
SELECT
    src.MatchId, src.MatchNo, l.n AS Rounds, l.fat AS FirstAt, l.lat AS LastAt,
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
    N'dbo.FbJcOddsHistory BF official ScoreDiagSql as-of DecisionAt, one MatchId (SELECT only)' AS SourceTable,
    CASE WHEN l.MatchId IS NOT NULL THEN N'certified' ELSE N'uncertified-insufficient-bf' END AS Certification
FROM src
LEFT JOIN lr l ON l.MatchId = src.MatchId
LEFT JOIN fr f ON f.MatchId = src.MatchId
LEFT JOIN deepdrop d ON d.MatchId = src.MatchId
LEFT JOIN distj dj ON dj.MatchId = src.MatchId;
"@
}

$daysRemainingFast = @(
    @{ SalesDate='2026-11-15'; RunId='20261115T225017-c6849c'; DecisionAt='2026-11-15T22:50:17.8133333' },
    @{ SalesDate='2026-11-16'; RunId='20261116T215008-c60013'; DecisionAt='2026-11-16T21:50:08.2566667' }
)

$diagDays = @(
    @{ SalesDate='2026-11-14'; RunId='20261114T225008-4408e9'; DecisionAt='2026-11-14T22:50:08.9833333'; Ids=@(1416115,1398632,1398681,1416124,1416128,1416117,1398644,1398638,1398641,1398683,1362554,1362429) },
    @{ SalesDate='2026-11-15'; RunId='20261115T225017-c6849c'; DecisionAt='2026-11-15T22:50:17.8133333'; Ids=@(1398695,1398689,1398691,1398687) },
    @{ SalesDate='2026-11-16'; RunId='20261116T215008-c60013'; DecisionAt='2026-11-16T21:50:08.2566667'; Ids=@(1398699,1398700,1398702,1398701,1398647,1398692,1398649,1398694) }
)

Write-Log ('hostname=' + (hostname))
Write-Log 'remaining-export begin'

$conn = $null
try {
    $conn = New-ProdConnection
    Write-Log 'connection=open remaining'
    $prefix = Get-Prefix

    foreach ($d in $daysRemainingFast) {
        $run = $d.RunId; $dec = $d.DecisionAt; $sd = $d.SalesDate

        $hcpSql = $prefix + @"

SELECT s.RunId, s.SalesDate, s.MatchId, s.MatchNo, s.MatchTime, c.Handicap, c.CapturedAt AS HandicapKnownAt,
       N'dbo.BettingCard Board=jc' AS SourceTable,
       CASE
           WHEN c.Handicap IS NULL THEN N'uncertified-null'
           WHEN c.CapturedAt IS NULL THEN N'uncertified-no-timestamp'
           WHEN c.CapturedAt > CONVERT(datetime2(7), '$dec', 126) THEN N'uncertified-after-decision'
           WHEN s.MatchTime IS NOT NULL AND c.CapturedAt > s.MatchTime THEN N'uncertified-after-kickoff'
           ELSE N'certified'
       END AS Certification
FROM dbo.FbMilkSourceMatch AS s
LEFT JOIN dbo.BettingCard AS c ON c.Board = N'jc' AND c.MatchId = s.MatchId
WHERE s.RunId = N'$run'
ORDER BY s.MatchNo, s.MatchId;
"@
        Export-Query -Connection $conn -Name "handicap-$sd" -Sql $hcpSql -OutPath (Join-Path $ExportDir "handicap-$sd.tsv") | Out-Null

        $waterSql = $prefix + @"

WITH src AS (SELECT s.MatchId, s.MatchTime FROM dbo.FbMilkSourceMatch AS s WHERE s.RunId = N'$run'),
hist AS (
    SELECT h.MatchId, h.Odds, h.CapturedAt, h.Id,
           ROW_NUMBER() OVER (PARTITION BY h.MatchId ORDER BY h.CapturedAt, h.Id) AS rn_open,
           ROW_NUMBER() OVER (PARTITION BY h.MatchId ORDER BY h.CapturedAt DESC, h.Id DESC) AS rn_curr
    FROM dbo.FbJcOddsHistory AS h
    INNER JOIN src ON src.MatchId = h.MatchId
    WHERE h.PlayType IN (N'SPF', N'spf') AND h.OptionCode = N'3' AND h.Odds > 1
      AND h.CapturedAt <= CONVERT(datetime2(7), '$dec', 126)
      AND (src.MatchTime IS NULL OR h.CapturedAt <= src.MatchTime)
)
SELECT src.MatchId, o.Odds AS OpenWin, o.CapturedAt AS OpenCapturedAt, c.Odds AS CurrWin, c.CapturedAt AS CurrCapturedAt,
       N'dbo.FbJcOddsHistory SPF option 3 first/last as-of DecisionAt' AS SourceTable,
       CASE WHEN o.Odds > 1 AND c.Odds > 1 THEN N'certified' ELSE N'uncertified-incomplete' END AS Certification
FROM src
LEFT JOIN hist o ON o.MatchId = src.MatchId AND o.rn_open = 1
LEFT JOIN hist c ON c.MatchId = src.MatchId AND c.rn_curr = 1
ORDER BY src.MatchId;
"@
        Export-Query -Connection $conn -Name "spf-water-$sd" -Sql $waterSql -OutPath (Join-Path $ExportDir "spf-water-$sd.tsv") | Out-Null

        $spanSql = $prefix + @"

WITH src AS (SELECT s.MatchId, s.MatchTime FROM dbo.FbMilkSourceMatch AS s WHERE s.RunId = N'$run'),
jc AS (
    SELECT t.MatchId,
           MAX(CASE WHEN t.OptionCode='3' AND t.rn_a=1 THEN t.Odds END) AS JcOpenHome,
           MAX(CASE WHEN t.OptionCode='3' AND t.rn_d=1 THEN t.Odds END) AS JcCurrHome,
           MAX(CASE WHEN t.OptionCode='0' AND t.rn_a=1 THEN t.Odds END) AS JcOpenAway,
           MAX(CASE WHEN t.OptionCode='0' AND t.rn_d=1 THEN t.Odds END) AS JcCurrAway
    FROM (
        SELECT h.MatchId, h.OptionCode, h.Odds,
               ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.OptionCode ORDER BY h.CapturedAt, h.Id) rn_a,
               ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.OptionCode ORDER BY h.CapturedAt DESC, h.Id DESC) rn_d
        FROM dbo.FbJcOddsHistory AS h
        INNER JOIN src ON src.MatchId = h.MatchId
        WHERE h.PlayType IN (N'spf', N'SPF') AND h.Odds > 1
          AND h.CapturedAt <= CONVERT(datetime2(7), '$dec', 126)
          AND (src.MatchTime IS NULL OR h.CapturedAt <= src.MatchTime)
    ) t
    WHERE t.rn_a = 1 OR t.rn_d = 1
    GROUP BY t.MatchId
),
ex AS (
    SELECT e.MatchId,
           MAX(CASE WHEN e.rn_a=1 THEN e.PriceHome END) AS ExOpenHome,
           MAX(CASE WHEN e.rn_d=1 THEN e.PriceHome END) AS ExCurrHome,
           MAX(CASE WHEN e.rn_a=1 THEN e.PriceAway END) AS ExOpenAway,
           MAX(CASE WHEN e.rn_d=1 THEN e.PriceAway END) AS ExCurrAway
    FROM (
        SELECT x.MatchId, x.PriceHome, x.PriceAway,
               ROW_NUMBER() OVER (PARTITION BY x.MatchId ORDER BY x.CapturedAt) rn_a,
               ROW_NUMBER() OVER (PARTITION BY x.MatchId ORDER BY x.CapturedAt DESC) rn_d
        FROM dbo.FbExchange AS x
        INNER JOIN src ON src.MatchId = x.MatchId
        WHERE x.TotalVolume > 0 AND x.PriceHome > 1 AND x.PriceAway > 1
          AND x.CapturedAt <= CONVERT(datetime2(7), '$dec', 126)
          AND (src.MatchTime IS NULL OR x.CapturedAt <= src.MatchTime)
    ) e
    GROUP BY e.MatchId
)
SELECT src.MatchId, j.JcOpenHome, j.JcCurrHome, j.JcOpenAway, j.JcCurrAway,
       x.ExOpenHome, x.ExCurrHome, x.ExOpenAway, x.ExCurrAway,
       N'dbo.FbJcOddsHistory spf first/last as-of + dbo.FbExchange first/last as-of (official GetPriceSpanAsync with DecisionAt/MatchTime cutoff)' AS SourceTable,
       CASE WHEN j.MatchId IS NOT NULL THEN N'certified-jc' ELSE N'uncertified-no-jc-span' END AS Certification
FROM src
LEFT JOIN jc j ON j.MatchId = src.MatchId
LEFT JOIN ex x ON x.MatchId = src.MatchId
ORDER BY src.MatchId;
"@
        Export-Query -Connection $conn -Name "price-span-$sd" -Sql $spanSql -OutPath (Join-Path $ExportDir "price-span-$sd.tsv") | Out-Null

        $euSql = $prefix + @"

WITH src AS (SELECT s.MatchId, s.MatchTime FROM dbo.FbMilkSourceMatch AS s WHERE s.RunId = N'$run'),
ranked AS (
    SELECT h.MatchId, h.HomeWin, h.Draw, h.AwayWin, h.CapturedAt,
           ROW_NUMBER() OVER (PARTITION BY h.MatchId ORDER BY h.CapturedAt DESC, h.Id DESC) AS rn
    FROM dbo.FbOddsHistory AS h
    INNER JOIN src ON src.MatchId = h.MatchId
    WHERE h.OddsType = N'EU' AND h.CompanyId = 0
      AND h.CapturedAt <= CONVERT(datetime2(7), '$dec', 126)
      AND (src.MatchTime IS NULL OR h.CapturedAt <= src.MatchTime)
)
SELECT src.MatchId, r.HomeWin AS HomeWinEuClose, r.Draw, r.AwayWin, r.CapturedAt,
       N'dbo.FbOddsHistory EU CompanyId=0 last row as-of DecisionAt (official GetClosingEu history branch; current FbOdds not used)' AS SourceTable,
       CASE WHEN r.HomeWin IS NOT NULL THEN N'certified' ELSE N'uncertified-missing' END AS Certification
FROM src
LEFT JOIN ranked r ON r.MatchId = src.MatchId AND r.rn = 1
ORDER BY src.MatchId;
"@
        Export-Query -Connection $conn -Name "euro-close-$sd" -Sql $euSql -OutPath (Join-Path $ExportDir "euro-close-$sd.tsv") | Out-Null
    }

    foreach ($d in $diagDays) {
        $chunkFiles = @()
        foreach ($mid in $d.Ids) {
            $name = 'score-diag-{0}-{1}' -f $d.SalesDate, $mid
            $out = Join-Path $ChunkDir ($name + '.tsv')
            $sql = Get-DiagSql -MatchId $mid -DecisionAt $d.DecisionAt
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

    Write-Log 'remaining-export-complete'
} finally {
    if ($conn) { $conn.Close(); $conn.Dispose(); Write-Log 'connection=closed remaining' }
}
