# Chunked read-only sample export. The single 18-month query exceeded 30s and was rejected.
# Each chunk is a narrower SELECT with LOCK_TIMEOUT 5000, READ COMMITTED, 30s timeout.
# Do not retry a chunk that times out or hits 1222.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ResultsDir = 'C:\Users\Administrator\FitTraceTest\env-evidence\replay-run-20261117'
$ExportDir  = Join-Path $ResultsDir 'export'
$ChunkDir   = Join-Path $ExportDir 'samples-chunks'
$LogPath    = Join-Path $ExportDir 'export-log.txt'
$MergedPath = Join-Path $ExportDir 'samples.tsv'
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
                if ($reader.IsDBNull($i)) {
                    $cells[$i] = ''
                } else {
                    $val = $reader.GetValue($i)
                    if ($val -is [datetime]) {
                        $cells[$i] = ([datetime]$val).ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
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

# Half-open [start, end) chunks covering TrainFrom(2026-11-14) .. TrainUntil(2026-11-16 21:50:08.2566667)
$windows = New-Object System.Collections.Generic.List[object]
$cursor = [datetime]'2025-05-14'
$finalEnd = [datetime]'2026-11-16 19:20:08.2566667'
while ($cursor -lt $finalEnd) {
    $next = $cursor.AddMonths(1)
    if ($next -gt $finalEnd) { $next = $finalEnd }
    $windows.Add([pscustomobject]@{ Start = $cursor; EndExclusive = $next })
    $cursor = $next
}

Write-Log ('hostname=' + (hostname))
Write-Log ('sample-chunks={0} from={1:yyyy-MM-dd HH:mm:ss.fffffff} to={2:yyyy-MM-dd HH:mm:ss.fffffff}' -f $windows.Count, [datetime]'2025-05-14', $finalEnd)

$conn = $null
$chunkFiles = @()
try {
    $conn = New-ProdConnection
    Write-Log 'connection=open samples-monthly'
    $i = 0
    foreach ($w in $windows) {
        $i++
        $start = $w.Start.ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
        $endEx = $w.EndExclusive.ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
        $name = 'samples-{0:000}-{1}' -f $i, $w.Start.ToString('yyyyMM')
        $out = Join-Path $ChunkDir ($name + '.tsv')
        $sql = $prefix + @"

SELECT f.MatchId, f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
       f.MatchTime, f.HomeScore, f.AwayScore
FROM dbo.FbMatch AS f
WHERE f.Status = 3
  AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
  AND f.MatchTime >= CONVERT(datetime2(7), '$start', 126)
  AND f.MatchTime <  CONVERT(datetime2(7), '$endEx', 126)
ORDER BY f.MatchTime, f.MatchId;
"@
        Export-Query -Connection $conn -Name $name -Sql $sql -OutPath $out | Out-Null
        $chunkFiles += $out
    }
} finally {
    if ($conn) {
        $conn.Close()
        $conn.Dispose()
        Write-Log 'connection=closed samples-monthly'
    }
}

# Local merge only; no further database access.
$utf8 = New-Object System.Text.UTF8Encoding $false
$writer = New-Object System.IO.StreamWriter($MergedPath, $false, $utf8)
$headerWritten = $false
$total = 0
foreach ($file in $chunkFiles) {
    $lines = [System.IO.File]::ReadLines($file, $utf8)
    $first = $true
    foreach ($line in $lines) {
        if ($first) {
            if (-not $headerWritten) {
                $writer.WriteLine($line)
                $headerWritten = $true
            }
            $first = $false
            continue
        }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $writer.WriteLine($line)
        $total++
    }
}
$writer.Flush()
$writer.Dispose()
Write-Log ("MERGED samples rows={0} path={1}" -f $total, $MergedPath)
Write-Log 'samples-monthly-done'
