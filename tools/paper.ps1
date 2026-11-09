# Paper-trade report reader (read-only).
#
# Why this exists: FbMilkPick gets rewritten on every recompute, so it can never
# answer "if I had followed the board, would I be up or down". FbPaperTrade is the
# frozen ledger that can. This script just prints it - it never writes.
#
# Usage (PowerShell tool only):
#   & .\tools\paper.ps1              # last 14 days
#   & .\tools\paper.ps1 -Days 30
#   & .\tools\paper.ps1 -Days 3 -Rows
#
# NOTE: keep this file pure ASCII. PS 5.1 reads no-BOM UTF-8 as ANSI and garbles it.

param(
    [int]$Days = 14,
    [switch]$Rows
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$db = Join-Path $PSScriptRoot 'db.ps1'

$api = 'http://127.0.0.1:5088'

function Try-Api {
    param([string]$Path)
    try {
        $r = Invoke-WebRequest ($api + $Path) -UseBasicParsing -TimeoutSec 120
        return ($r.Content | ConvertFrom-Json)
    } catch {
        return $null
    }
}

$report = Try-Api "/api/milk/paper?days=$Days"

if ($null -ne $report) {
    Write-Output ("=== paper trade, last " + $report.days + " days (" + $report.fromDate.Substring(0,10) + " .. " + $report.toDate.Substring(0,10) + ") ===")
    Write-Output ("frozen days: " + $report.frozenDays)

    function Show-Slice {
        param($S, [string]$Label)
        if ($null -eq $S) { return }
        $roi = if ($S.stake -gt 0) { "{0:N3}" -f $S.roi } else { "-" }
        $hit = if ($S.settled -gt 0) { "{0,5:N1}%" -f $S.hitPct } else { "    -" }
        Write-Output ("{0,-12} total={1,4} voided={2,3} settled={3,4} hits={4,4} hit={5} roi={6}" -f `
            $Label, $S.total, $S.voided, $S.settled, $S.hits, $hit, $roi)
    }

    Show-Slice $report.overall 'ALL'
    Write-Output '--- by play type ---'
    foreach ($s in $report.byPlayType) { Show-Slice $s $s.key }
    Write-Output '--- by flag ---'
    foreach ($s in $report.byFlag) { Show-Slice $s $s.key }
    Write-Output '--- by date ---'
    foreach ($s in $report.byDate) { Show-Slice $s $s.key }

    $need = 150
    $have = $report.overall.settled
    Write-Output ("superseded (switched-out, NOT counted): " + $report.superseded)
    if ($have -lt $need) {
        Write-Output ("sample: {0}/{1} settled - NOT ENOUGH to conclude anything" -f $have, $need)
    } else {
        Write-Output ("sample: {0}/{1} settled - enough to read" -f $have, $need)
    }
} else {
    Write-Output 'API not reachable, falling back to direct SQL'
    $from = (Get-Date).Date.AddDays(-($Days - 1)).ToString('yyyy-MM-dd')
    # Same rule as PaperTradeMath.FinalVersion: one bet per (date, match, play type),
    # the LAST published option wins; switched-out rows are not counted at all.
    $q = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.FbPaperTrade', N'U') IS NULL
    SELECT 'table not created yet' AS Note;
ELSE
    WITH v AS (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY TradeDate, MatchId, PlayType
                                     ORDER BY FrozenAt DESC, TradeId DESC) AS rn
        FROM dbo.FbPaperTrade
        WHERE TradeDate >= '$from'
    )
    SELECT COUNT(*) AS Total,
           SUM(CASE WHEN Voided = 1 THEN 1 ELSE 0 END) AS Voided,
           SUM(CASE WHEN Voided = 0 AND Hit IS NOT NULL THEN 1 ELSE 0 END) AS Settled,
           SUM(CASE WHEN Voided = 0 AND Hit = 1 THEN 1 ELSE 0 END) AS Hits,
           CAST(ISNULL(SUM(CASE WHEN Voided = 0 AND Hit = 1 THEN Odds ELSE 0 END)
                / NULLIF(SUM(CASE WHEN Voided = 0 AND Hit IS NOT NULL THEN 1 ELSE 0 END), 0), 0) AS DECIMAL(8,4)) AS Roi,
           (SELECT COUNT(*) FROM v) - COUNT(*) AS Superseded
    FROM v WHERE rn = 1;
"@
    & $db -Query $q
}

if ($Rows) {
    Write-Output '--- rows ---'
    $rowsData = Try-Api "/api/milk/paper/rows?days=$Days"
    if ($null -ne $rowsData) {
        foreach ($r in $rowsData) {
            $mark = if ($r.superseded) { 'DROP' } elseif ($r.voided) { 'VOID' } elseif ($null -eq $r.hit) { 'open' } elseif ($r.hit) { 'HIT ' } else { 'miss' }
            Write-Output ("{0} {1,-7} {2,-6} {3}-{4,-10} {5,-3} {6,-5} odds={7,-6} {8} void={9}" -f `
                $r.tradeDate.Substring(5,5), $r.matchNo, $r.playType, $r.homeTeam, $r.awayTeam, `
                $r.playType, $r.optionCode, $r.odds, $mark, $r.voided)
        }
    }
}
