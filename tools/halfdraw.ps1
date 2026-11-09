# Half-full model for 3-1 (win-draw) and 0-1 (lose-draw). READ ONLY analysis.
# Two-stage Poisson: fit lambda_h/lambda_a from SPF odds, split 90min into half + second half,
# enumerate paths "half score -> full-time n:n".
# NOTE: keep this file pure ASCII (PowerShell 5.1 reads .ps1 as ANSI).
param([string]$From = '2026-10-05', [string]$To = '2026-11-09', [double]$S = 0.45)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$cred = & (Join-Path $PSScriptRoot 'db-credential.ps1')
$connStr = "Server=$($cred.Server);Database=AiFootBall;User Id=$($cred.User);Password=$($cred.Password);TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()

function Q([string]$sql) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = 180
    $dt = New-Object System.Data.DataTable
    $r = $cmd.ExecuteReader()
    $dt.Load($r)
    $r.Close()
    return $dt
}

$sql = @"
WITH spf AS (
  SELECT MatchId,
    MAX(CASE WHEN OptionCode='3' THEN Odds END) AS o3,
    MAX(CASE WHEN OptionCode='1' THEN Odds END) AS o1,
    MAX(CASE WHEN OptionCode='0' THEN Odds END) AS o0
  FROM dbo.FbJcOddsHistory WHERE PlayType='SPF' GROUP BY MatchId
), bq AS (
  SELECT MatchId,
    MAX(CASE WHEN OptionCode='3-1' THEN Odds END) AS o31,
    MAX(CASE WHEN OptionCode='0-1' THEN Odds END) AS o01
  FROM dbo.FbJcOddsHistory WHERE PlayType='BQ' GROUP BY MatchId
)
SELECT m.MatchId, m.MatchNo2 AS MatchNo, m.LeagueName AS LeagueName, m.HomeTeamName, m.AwayTeamName,
       m.MatchTime, m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore,
       s.o3, s.o1, s.o0, b.o31, b.o01
FROM (
  SELECT f.*, (SELECT TOP 1 h.MatchNo FROM dbo.FbJcOddsHistory h WHERE h.MatchId=f.MatchId AND h.PlayType='BQ') AS MatchNo2
  FROM dbo.FbMatch f
  WHERE f.MatchTime >= '$From' AND f.MatchTime < '$To'
    AND f.HomeScore IS NOT NULL AND f.HalfHomeScore IS NOT NULL
    AND EXISTS (SELECT 1 FROM dbo.FbJcOddsHistory h WHERE h.MatchId=f.MatchId AND h.PlayType='BQ')
) m
JOIN spf s ON s.MatchId=m.MatchId
JOIN bq b ON b.MatchId=m.MatchId
WHERE s.o3>1 AND s.o1>1 AND s.o0>1 AND b.o31>1
"@
$rows = Q $sql
Write-Output ("matches = " + $rows.Rows.Count)

$maxG = 9
function Pois([double]$lam) {
    $p = New-Object double[] ($maxG + 1)
    $p[0] = [Math]::Exp(-$lam)
    for ($k = 1; $k -le $maxG; $k++) { $p[$k] = $p[$k-1] * $lam / $k }
    return ,$p
}

$grid = 0.05
$nG = 100
$W1 = $nG + 1
$PW = New-Object double[] ($W1*$W1)
$PD = New-Object double[] ($W1*$W1)
$PL = New-Object double[] ($W1*$W1)
for ($i = 1; $i -le $nG; $i++) {
    $ph = Pois ($i * $grid)
    for ($j = 1; $j -le $nG; $j++) {
        $pa = Pois ($j * $grid)
        $pw = 0.0; $pd = 0.0; $pl = 0.0
        for ($a = 0; $a -le $maxG; $a++) {
            $x = $ph[$a]
            for ($b = 0; $b -le $maxG; $b++) {
                $v = $x * $pa[$b]
                if ($a -gt $b) { $pw += $v } elseif ($a -eq $b) { $pd += $v } else { $pl += $v }
            }
        }
        $PW[$i*$W1+$j] = $pw; $PD[$i*$W1+$j] = $pd; $PL[$i*$W1+$j] = $pl
    }
}
Write-Output "grid ready"

$res = New-Object System.Collections.ArrayList
$cnt = 0
foreach ($r in $rows.Rows) {
    $cnt++
    $i3 = 1.0 / [double]$r.o3; $i1 = 1.0 / [double]$r.o1; $i0 = 1.0 / [double]$r.o0
    $t = $i3 + $i1 + $i0
    $m3 = $i3 / $t; $m1 = $i1 / $t; $m0 = $i0 / $t

    $best = [double]::MaxValue; $bi = 1; $bj = 1
    for ($i = 1; $i -le $nG; $i++) {
        for ($j = 1; $j -le $nG; $j++) {
            $k = $i*$W1+$j
            $d = ($PW[$k]-$m3)*($PW[$k]-$m3) + ($PD[$k]-$m1)*($PD[$k]-$m1) + ($PL[$k]-$m0)*($PL[$k]-$m0)
            if ($d -lt $best) { $best = $d; $bi = $i; $bj = $j }
        }
    }
    $lh = $bi * $grid; $la = $bj * $grid

    $ph1 = Pois ($lh * $S); $pa1 = Pois ($la * $S)
    $ph2 = Pois ($lh * (1-$S)); $pa2 = Pois ($la * (1-$S))
    $p31 = 0.0; $p01 = 0.0
    for ($nn = 0; $nn -le 6; $nn++) {
        for ($a = 0; $a -le $nn; $a++) {
            for ($b = 0; $b -le $nn; $b++) {
                if ($a -eq $b) { continue }
                $v = $ph1[$a] * $pa1[$b] * $ph2[$nn-$a] * $pa2[$nn-$b]
                if ($a -gt $b) { $p31 += $v } else { $p01 += $v }
            }
        }
    }

    $hh = [int]$r.HalfHomeScore; $ha = [int]$r.HalfAwayScore; $h = [int]$r.HomeScore; $aa = [int]$r.AwayScore
    [void]$res.Add([pscustomobject]@{
        MatchNo = [string]$r.MatchNo; League = [string]$r.LeagueName
        Home = [string]$r.HomeTeamName; Away = [string]$r.AwayTeamName
        Odds = [double]$r.o31; Pd = $m1; P31 = $p31; P01 = $p01
        Ev31 = $p31 * [double]$r.o31; Ev01 = $p01 * [double]$r.o01
        Hit31 = ($hh -gt $ha -and $h -eq $aa); Hit01 = ($hh -lt $ha -and $h -eq $aa)
        Path = "$hh`:$ha->$h`:$aa"; Lh = $lh; La = $la
    })
}
$conn.Close()
Write-Output ("modelled = " + $res.Count)

$n = $res.Count
$real31 = ($res | Where-Object { $_.Hit31 }).Count / $n
$real01 = ($res | Where-Object { $_.Hit01 }).Count / $n
Write-Output ""
Write-Output "=== calibration (all) ==="
$nWd = @($res | Where-Object { $_.Hit31 }).Count
$nLd = @($res | Where-Object { $_.Hit01 }).Count
Write-Output ("realised 3-1 = " + $nWd + " / " + $n + "   0-1 = " + $nLd)
Write-Output ("model P(3-1) = " + [Math]::Round((($res | Measure-Object P31 -Average).Average)*100,2) + "%   actual = " + [Math]::Round($real31*100,2) + "%")
Write-Output ("model P(0-1) = " + [Math]::Round((($res | Measure-Object P01 -Average).Average)*100,2) + "%   actual = " + [Math]::Round($real01*100,2) + "%")
Write-Output ("model avg EV(3-1) = " + [Math]::Round(($res | Measure-Object Ev31 -Average).Average,3))
Write-Output ("model avg EV(0-1) = " + [Math]::Round(($res | Measure-Object Ev01 -Average).Average,3))
Write-Output ("avg lambda_h = " + [Math]::Round(($res | Measure-Object Lh -Average).Average,2) + "  lambda_a = " + [Math]::Round(($res | Measure-Object La -Average).Average,2))

Write-Output ""
Write-Output "=== by model P(3-1) bucket ==="
$buckets = @(
    @{n='A <4.0%';  lo=0.0;   hi=0.04},
    @{n='B 4.0-4.5';lo=0.04;  hi=0.045},
    @{n='C 4.5-5.0';lo=0.045; hi=0.05},
    @{n='D 5.0-5.5';lo=0.05;  hi=0.055},
    @{n='E 5.5-6.5';lo=0.055; hi=0.065},
    @{n='F >=6.5%'; lo=0.065; hi=9.9}
)
foreach ($b in $buckets) {
    $g = @($res | Where-Object { $_.P31 -ge $b.lo -and $_.P31 -lt $b.hi })
    if ($g.Count -eq 0) { continue }
    $hits = @($g | Where-Object { $_.Hit31 }).Count
    Write-Output ($b.n + "  n=" + $g.Count + "  model=" + [Math]::Round(100*(($g | Measure-Object P31 -Average).Average),2) + "%  actual=" + [Math]::Round(100.0*$hits/$g.Count,2) + "%  avgEV=" + [Math]::Round(($g | Measure-Object Ev31 -Average).Average,3))
}

Write-Output ""
Write-Output "=== candidates EV31 >= 1.0 ==="
$cand = @($res | Where-Object { $_.Ev31 -ge 1.0 } | Sort-Object Ev31 -Descending)
Write-Output ("picks = " + $cand.Count + "  hits = " + @($cand | Where-Object { $_.Hit31 }).Count)
$cand | Select-Object -First 12 MatchNo, League, Home, Away, Odds, @{n='Pd%';e={[Math]::Round($_.Pd*100,1)}}, @{n='P31%';e={[Math]::Round($_.P31*100,2)}}, @{n='EV31';e={[Math]::Round($_.Ev31,3)}}, Hit31, Path | Format-Table -AutoSize | Out-String -Width 200
