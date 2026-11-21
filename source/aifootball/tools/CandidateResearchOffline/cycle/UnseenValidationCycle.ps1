#requires -Version 5.1
<#
.SYNOPSIS
  Real unseen-validation continuous cycle: SELECT export → predict-archive → declared-results export → attach-results → status.
.NOTES
  - Does NOT install scheduler. Does NOT write DB. Does NOT enable candidate.
  - Uses fixed history snapshot; never refreshes TrainCutoff / fit during validation.
  - Forbidden: IN @ids to SqlCommand; current-price backfill; post-kickoff declare.
  Exit: 0 = completed (Activity or Idle); 1 = hard fault; 2 = mutex busy (another instance).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$ConfigPath,
  [switch]$SkipExport,
  [switch]$SkipPredict,
  [switch]$SkipAttach,
  [string]$FixtureMatches,
  [string]$FixtureOdds,
  [string]$FixtureDeclaredMatches,
  [string]$AsOfOverride,
  [string]$RunIdOverride,
  [switch]$AllowOfflineNoDb
)

$ErrorActionPreference = 'Stop'
$script:Outcome = 'Fault'
$script:SummaryLines = New-Object System.Collections.Generic.List[string]
$script:Mutex = $null
$script:LockTaken = $false

function Write-Sum([string]$s) {
  $script:SummaryLines.Add($s)
  Write-Host $s
}

function Fail([string]$code, [string]$detail = '') {
  $script:Outcome = 'Fault'
  Write-Sum "FAULT|$code$(if($detail){"|$detail"})"
  if ($script:RunDir) {
    try {
      $script:SummaryLines | Set-Content (Join-Path $script:RunDir 'summary.txt') -Encoding UTF8
    } catch {}
  }
  exit 1
}

function Read-JsonConfig([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { Fail 'config-missing' $path }
  return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Read-Conn([string]$connFile) {
  if (-not (Test-Path -LiteralPath $connFile)) { Fail 'conn-file-missing' }
  $raw = (Get-Content -LiteralPath $connFile -Raw).Trim()
  if ([string]::IsNullOrWhiteSpace($raw)) { Fail 'conn-empty' }
  if ($raw -notmatch 'ApplicationIntent\s*=\s*ReadOnly') {
    $raw = $raw.TrimEnd(';') + ';ApplicationIntent=ReadOnly'
  }
  return $raw
}

function Assert-SelectOnlySql([string]$sql) {
  # Strip line comments before keyword scans (comments may mention forbidden patterns).
  $noComments = (($sql -split "`r?`n" | Where-Object { $_ -notmatch '^\s*--' }) -join "`n")
  $u = $noComments.ToUpperInvariant()
  foreach ($bad in @(' INSERT ', ' UPDATE ', ' DELETE ', ' MERGE ', ' TRUNCATE ', ' DROP ', ' ALTER ', ' CREATE ', ' EXEC ', ' EXECUTE ')) {
    if ($u.Contains($bad)) { Fail 'sql-not-select-only' $bad.Trim() }
  }
  if ($u -match 'INTO\s+#') { Fail 'sql-not-select-only' 'INTO-temp' }
  if ($u -match 'TEMPDB') { Fail 'sql-not-select-only' 'tempdb' }
  if ($u -notmatch '\bSELECT\b') { Fail 'sql-missing-select' }
  if ($u -match '\bIN\s+@') { Fail 'sql-forbidden-IN-at-ids' }
}

function Get-Sha256([string]$file) {
  if (-not (Test-Path -LiteralPath $file)) { return '' }
  return (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}

function Read-WindowMeta([string]$win) {
  $path = Join-Path $win 'window-meta.txt'
  $integ = Join-Path $win 'window-meta.integrity'
  if (-not (Test-Path $path) -or -not (Test-Path $integ)) { Fail 'window-meta-missing' }
  $map = @{}
  Get-Content -LiteralPath $path | ForEach-Object {
    if ($_ -match '^\s*#' -or [string]::IsNullOrWhiteSpace($_)) { return }
    $eq = $_.IndexOf('=')
    if ($eq -gt 0) { $map[$_.Substring(0, $eq)] = $_.Substring($eq + 1) }
  }
  $stored = ''
  Get-Content -LiteralPath $integ | ForEach-Object {
    if ($_ -match '^metaHash=(.+)$') { $stored = $Matches[1].Trim() }
  }
  return @{ Map = $map; IntegrityHash = $stored; MetaPath = $path; IntegPath = $integ }
}

function Count-Declares([string]$win) {
  $log = Join-Path $win 'archive.log'
  if (-not (Test-Path $log)) { return ,@() }
  $ids = New-Object 'System.Collections.Generic.HashSet[long]'
  Get-Content -LiteralPath $log | ForEach-Object {
    if ($_ -match 'kind=predeclared-unseen' -and $_ -match 'matchId=(\d+)') {
      [void]$ids.Add([long]$Matches[1])
    }
  }
  return ,@($ids)
}

function Count-Results([string]$win) {
  $log = Join-Path $win 'results.log'
  if (-not (Test-Path $log)) { return ,@() }
  $ids = New-Object 'System.Collections.Generic.HashSet[long]'
  Get-Content -LiteralPath $log | ForEach-Object {
    if ($_ -match 'matchId=(\d+)') { [void]$ids.Add([long]$Matches[1]) }
  }
  return ,@($ids)
}

function Quote-Arg([string]$a) {
  if ($null -eq $a) { return '""' }
  if ($a -match '[\s"]') { return '"' + ($a.Replace('"', '\"')) + '"' }
  return $a
}
function Invoke-Research([string]$exe, [string[]]$argList, [string]$logPath) {
  if (-not (Test-Path -LiteralPath $exe)) { Fail 'research-exe-missing' $exe }
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $exe
  # Windows PowerShell 5.1: ArgumentList may be unavailable — use Arguments string.
  $psi.Arguments = (($argList | ForEach-Object { Quote-Arg $_ }) -join ' ')
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.CreateNoWindow = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $out = $p.StandardOutput.ReadToEnd()
  $err = $p.StandardError.ReadToEnd()
  $p.WaitForExit()
  $text = "EXIT=$($p.ExitCode)`n---stdout---`n$out`n---stderr---`n$err`n"
  # scrub possible connection strings
  $text = [regex]::Replace($text, '(?i)(Password|Pwd|User ID|UID)\s*=\s*[^;]+', '$1=***')
  Set-Content -LiteralPath $logPath -Value $text -Encoding UTF8
  return @{ ExitCode = $p.ExitCode; StdOut = $out; StdErr = $err }
}

function Export-Upcoming([string]$conn, [string]$matchesOut, [string]$oddsOut, [string]$sqlDir) {
  $mSql = Get-Content -LiteralPath (Join-Path $sqlDir '01-upcoming-matches.sql') -Raw
  $oSql = Get-Content -LiteralPath (Join-Path $sqlDir '02-upcoming-prekick-jq-odds.sql') -Raw
  Assert-SelectOnlySql $mSql
  Assert-SelectOnlySql $oSql

  $tmpM = $matchesOut + '.tmp'
  $tmpO = $oddsOut + '.tmp'
  $connObj = New-Object System.Data.SqlClient.SqlConnection($conn)
  try {
    $connObj.Open()
  } catch {
    Fail 'db-open-failed' $_.Exception.Message
  }

  try {
    $cmd = $connObj.CreateCommand()
    $cmd.CommandText = $mSql
    $cmd.CommandTimeout = 180
    $r = $cmd.ExecuteReader()
    $sw = New-Object System.IO.StreamWriter($tmpM, $false, [Text.UTF8Encoding]::new($false))
    $sw.WriteLine('# SELECT-only|upcoming|MatchTime>now|scores-null|atomic-tmp')
    $sw.WriteLine('matchId	league	home	away	kickoff	ouLine	homeScore	awayScore	status	settledAt')
    $nM = 0
    while ($r.Read()) {
      $mid = [long]$r.GetValue(0)
      $kick = ([datetime]$r.GetValue(4)).ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
      $st = if ($r.IsDBNull(8)) { '0' } else { [int]$r.GetValue(8) }
      $sw.WriteLine("$mid`t$($r.GetString(1))`t$($r.GetString(2))`t$($r.GetString(3))`t$kick`t`t`t`t$st`t")
      $nM++
    }
    $r.Close(); $sw.Close()

    $cmd2 = $connObj.CreateCommand()
    $cmd2.CommandText = $oSql
    $cmd2.CommandTimeout = 600
    $r2 = $cmd2.ExecuteReader()
    $sw2 = New-Object System.IO.StreamWriter($tmpO, $false, [Text.UTF8Encoding]::new($false))
    $sw2.WriteLine('# SELECT-only|JQ|CapturedAt<MatchTime|no-IN-@ids|atomic-tmp')
    $sw2.WriteLine('matchId	playType	optionCode	odds	capturedAt')
    $nO = 0
    while ($r2.Read()) {
      $cap = ([datetime]$r2.GetValue(4)).ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
      $odds = ([decimal]$r2.GetValue(3)).ToString('0.000', [Globalization.CultureInfo]::InvariantCulture)
      $sw2.WriteLine("$($r2.GetValue(0))`t$($r2.GetString(1))`t$($r2.GetString(2))`t$odds`t$cap")
      $nO++
    }
    $r2.Close(); $sw2.Close()
  } catch {
    try { $connObj.Close() } catch {}
    Fail 'db-export-failed' $_.Exception.Message
  }
  $connObj.Close()

  if (-not (Test-Path $tmpM) -or -not (Test-Path $tmpO)) { Fail 'export-tmp-missing' }
  if ((Get-Item $tmpM).Length -lt 40) { Fail 'export-matches-empty' }
  Move-Item -LiteralPath $tmpM -Destination $matchesOut -Force
  Move-Item -LiteralPath $tmpO -Destination $oddsOut -Force
  return @{ Matches = $nM; OddsRows = $nO }
}

function Export-DeclaredResults([string]$conn, [long[]]$ids, [string]$outPath) {
  $tmp = $outPath + '.tmp'
  $sw = New-Object System.IO.StreamWriter($tmp, $false, [Text.UTF8Encoding]::new($false))
  $sw.WriteLine('# SELECT-only|declared-ids|Status=3+scores|not-from-upcoming-file')
  $sw.WriteLine('matchId	league	home	away	kickoff	ouLine	homeScore	awayScore	status	settledAt')
  if ($ids.Count -eq 0) {
    $sw.Close()
    Move-Item -LiteralPath $tmp -Destination $outPath -Force
    return @{ Finished = 0; Pending = 0 }
  }

  $connObj = New-Object System.Data.SqlClient.SqlConnection($conn)
  try { $connObj.Open() } catch { $sw.Close(); Fail 'db-open-failed-declared' $_.Exception.Message }

  $finished = 0; $pending = 0
  try {
    for ($i = 0; $i -lt $ids.Count; $i += 40) {
      $chunk = $ids[$i..([Math]::Min($i + 39, $ids.Count - 1))]
      $cmd = $connObj.CreateCommand()
      $cmd.CommandTimeout = 120
      $conds = @(); $j = 0
      foreach ($id in $chunk) {
        $pn = "@p$j"
        $conds += "m.MatchId=$pn"
        [void]$cmd.Parameters.AddWithValue($pn, [long]$id)
        $j++
      }
      $cmd.CommandText = @"
SELECT m.MatchId, ISNULL(m.LeagueName,''), ISNULL(m.HomeTeamName,''), ISNULL(m.AwayTeamName,''),
       m.MatchTime, m.HomeScore, m.AwayScore, m.Status, m.UpdatedAt
FROM dbo.FbMatch m WITH (READUNCOMMITTED)
WHERE ($($conds -join ' OR '))
"@
      Assert-SelectOnlySql $cmd.CommandText
      if ($cmd.CommandText -match '(?i)IN\s+@') { Fail 'sql-forbidden-IN-at-ids' 'declared' }
      $r = $cmd.ExecuteReader()
      while ($r.Read()) {
        $mid = [long]$r.GetValue(0)
        $kick = ([datetime]$r.GetValue(4)).ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
        $st = [int]$r.GetValue(7)
        $hs = ''; $as = ''; $settled = ''
        $hasScores = -not $r.IsDBNull(5) -and -not $r.IsDBNull(6)
        if ($hasScores -and $st -eq 3) {
          $hs = [int]$r.GetValue(5)
          $as = [int]$r.GetValue(6)
          $upd = if ($r.IsDBNull(8)) { ([datetime]$r.GetValue(4)).AddMinutes(150) } else { [datetime]$r.GetValue(8) }
          $settled = $upd.ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
          $finished++
        } else {
          # keep scores empty if not finished — do not treat live/partial as final
          $pending++
        }
        $sw.WriteLine("$mid`t$($r.GetString(1))`t$($r.GetString(2))`t$($r.GetString(3))`t$kick`t`t$hs`t$as`t$st`t$settled")
      }
      $r.Close()
    }
  } catch {
    try { $connObj.Close() } catch {}
    $sw.Close()
    Fail 'db-declared-export-failed' $_.Exception.Message
  }
  $connObj.Close()
  $sw.Close()
  Move-Item -LiteralPath $tmp -Destination $outPath -Force
  return @{ Finished = $finished; Pending = $pending }
}

function Acquire-Mutex([string]$name) {
  $created = $false
  $m = New-Object System.Threading.Mutex($false, $name, [ref]$created)
  $taken = $false
  try {
    $taken = $m.WaitOne(0)
  } catch {
    Fail 'mutex-error' $_.Exception.Message
  }
  if (-not $taken) {
    Write-Sum 'FAULT|mutex-busy|another-cycle-running'
    try { $m.Dispose() } catch {}
    exit 2
  }
  $script:Mutex = $m
  $script:LockTaken = $true
}

function Release-Mutex() {
  if ($script:LockTaken -and $script:Mutex) {
    try { $script:Mutex.ReleaseMutex() } catch {}
    try { $script:Mutex.Dispose() } catch {}
    $script:LockTaken = $false
  }
}

# ---------------- main ----------------
try {
  $cfg = Read-JsonConfig $ConfigPath
  # Resolve sql/ by walking up from config path until found (works for package/config and tests/fixtures).
  $sqlDir = $null
  $probe = [IO.Path]::GetFullPath((Split-Path $ConfigPath -Parent))
  for ($i = 0; $i -lt 6; $i++) {
    $cand = Join-Path $probe 'sql'
    if (Test-Path (Join-Path $cand '01-upcoming-matches.sql')) { $sqlDir = $cand; break }
    $parent = Split-Path $probe -Parent
    if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $probe) { break }
    $probe = $parent
  }
  if (-not $sqlDir) { Fail 'sql-dir-missing' }

  Acquire-Mutex ([string]$cfg.mutexName)

  $win = [string]$cfg.windowDirectory
  $hist = [string]$cfg.historyFixedPath
  $exe = [string]$cfg.researchExe
  $cycleRoot = [string]$cfg.cycleRoot
  $expectedModel = [string]$cfg.expectedModelVersion
  $expectedCode = [string]$cfg.expectedCodeHash
  $expectedMeta = [string]$cfg.expectedMetaHash
  $minTest = [int]$cfg.minTestMatches

  if (-not (Test-Path $win)) { Fail 'window-missing' $win }
  if (-not (Test-Path $hist)) { Fail 'history-fixed-missing' $hist }
  if (-not (Test-Path $exe)) { Fail 'exe-missing' $exe }

  $meta = Read-WindowMeta $win
  if ($meta.Map['ModelVersion'] -ne $expectedModel) { Fail 'model-fingerprint-mismatch' $meta.Map['ModelVersion'] }
  if ($meta.Map['CodeHash'] -ne $expectedCode) { Fail 'code-fingerprint-mismatch' $meta.Map['CodeHash'] }
  if ($meta.IntegrityHash -ne $expectedMeta) { Fail 'meta-hash-mismatch' $meta.IntegrityHash }
  if ($meta.Map['PolicyVersion'] -ne 'candidate-rule-v1') { Fail 'policy-mismatch' }

  # live EXE fingerprints must match frozen window (no silent model swap)
  $fp = Invoke-Research $exe @('fingerprints') ([IO.Path]::GetTempFileName())
  if ($fp.ExitCode -ne 0) { Fail 'fingerprints-failed' }
  $liveModel = ''; $liveCode = ''
  foreach ($line in ($fp.StdOut -split "`r?`n")) {
    if ($line -match '^ModelVersion=(.+)$') { $liveModel = $Matches[1].Trim() }
    if ($line -match '^CodeHash=(.+)$') { $liveCode = $Matches[1].Trim() }
  }
  if ($liveModel -ne $expectedModel) { Fail 'live-exe-model-mismatch' $liveModel }
  if ($liveCode -ne $expectedCode) { Fail 'live-exe-code-mismatch' $liveCode }

  $runId = if ($RunIdOverride) { $RunIdOverride } else { Get-Date -Format 'yyyyMMdd-HHmmss' }
  $script:RunDir = Join-Path $cycleRoot $runId
  New-Item -ItemType Directory -Force -Path $script:RunDir | Out-Null
  $inputDir = Join-Path $script:RunDir 'inputs'
  New-Item -ItemType Directory -Force -Path $inputDir | Out-Null

  Write-Sum "runId=$runId"
  Write-Sum "window=$win"
  Write-Sum "trainCutoff=$($meta.Map['TrainCutoff'])"
  Write-Sum "historyFixedSha=$(Get-Sha256 $hist)"
  Write-Sum "preDeclares=$((Count-Declares $win).Count)"
  Write-Sum "preResults=$((Count-Results $win).Count)"

  $matchesPath = Join-Path $inputDir 'upcoming-matches.tsv'
  $oddsPath = Join-Path $inputDir 'upcoming-prekick-jq-odds.tsv'
  $declaredPath = Join-Path $inputDir 'declared-matches.tsv'
  $markerOk = Join-Path $inputDir 'EXPORT_OK.marker'

  $declaresBefore = Count-Declares $win
  $resultsBefore = Count-Results $win

  # ---- export upcoming ----
  if ($SkipExport -and $AllowOfflineNoDb -and $FixtureMatches -and $FixtureOdds) {
    Copy-Item -LiteralPath $FixtureMatches -Destination $matchesPath -Force
    Copy-Item -LiteralPath $FixtureOdds -Destination $oddsPath -Force
    'offline-fixture' | Set-Content $markerOk
    Write-Sum 'export|mode=fixture'
  } elseif ($SkipExport) {
    Fail 'skip-export-without-fixture'
  } else {
    $conn = Read-Conn ([string]$cfg.connFile)
    $ex = Export-Upcoming $conn $matchesPath $oddsPath $sqlDir
    Write-Sum "export|upcomingMatches=$($ex.Matches)|oddsRows=$($ex.OddsRows)"
    "matches=$($ex.Matches)`nodds=$($ex.OddsRows)`nutc=$((Get-Date).ToUniversalTime().ToString('o'))" | Set-Content $markerOk
  }
  if (-not (Test-Path $markerOk)) { Fail 'export-marker-missing' }
  if (-not (Test-Path $matchesPath) -or -not (Test-Path $oddsPath)) { Fail 'export-outputs-missing' }

  # refuse to proceed with stale sibling from previous run: this run's inputs only
  $matchesSha = Get-Sha256 $matchesPath
  $oddsSha = Get-Sha256 $oddsPath
  Write-Sum "inputSha|matches=$matchesSha|odds=$oddsSha"

  # ---- predict ----
  $asOf = if ($AsOfOverride) { $AsOfOverride } else {
    [DateTimeOffset]::Now.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-ddTHH:mm:ss.fffffffzzz')
  }
  Write-Sum "asOf=$asOf"
  $predLog = Join-Path $script:RunDir '01-predict-archive.log'
  $newArchived = 0; $predRejected = 0; $idempotent = 0
  if (-not $SkipPredict) {
    $pr = Invoke-Research $exe @(
      'predict-archive', $win, $hist, $matchesPath, $oddsPath, "--asOf=$asOf"
    ) $predLog
    Write-Sum "predict|exit=$($pr.ExitCode)"
    if ($pr.ExitCode -ne 0 -and $pr.StdOut -notmatch '^predict-archive\|') {
      Fail 'predict-failed' ("exit=$($pr.ExitCode)")
    }
    if ($pr.StdOut -match 'archived=(\d+)') { $newArchived = [int]$Matches[1] }
    if ($pr.StdOut -match 'rejected=(\d+)') { $predRejected = [int]$Matches[1] }
    if ($pr.StdOut -match 'idempotent=(\d+)') { $idempotent = [int]$Matches[1] }
    Write-Sum "predict|archived=$newArchived|rejected=$predRejected|idempotent=$idempotent"
  } else {
    Write-Sum 'predict|skipped'
  }

  $declaresAfterPredict = Count-Declares $win
  $addedDeclares = @($declaresAfterPredict | Where-Object { $declaresBefore -notcontains $_ })
  Write-Sum "declares|before=$($declaresBefore.Count)|after=$($declaresAfterPredict.Count)|newUnique=$($addedDeclares.Count)"

  # ---- declared results export + attach ----
  $attachAdded = 0
  if (-not $SkipAttach) {
    if ($AllowOfflineNoDb -and $FixtureDeclaredMatches) {
      Copy-Item -LiteralPath $FixtureDeclaredMatches -Destination $declaredPath -Force
      Write-Sum 'declaredExport|mode=fixture'
    } else {
      $conn2 = Read-Conn ([string]$cfg.connFile)
      $dx = Export-DeclaredResults $conn2 ([long[]]$declaresAfterPredict) $declaredPath
      Write-Sum "declaredExport|finished=$($dx.Finished)|pendingWait=$($dx.Pending)"
    }
    $attLog = Join-Path $script:RunDir '02-attach-results.log'
    $ar = Invoke-Research $exe @(
      'attach-results', $win, $hist, $declaredPath, $oddsPath
    ) $attLog
    Write-Sum "attach|exit=$($ar.ExitCode)"
    if ($ar.StdOut -match 'attached=(\d+)') { $attachAdded = [int]$Matches[1] }
    if ($ar.ExitCode -ne 0 -and $ar.StdOut -notmatch '^attach-results\|') {
      Fail 'attach-failed' ("exit=$($ar.ExitCode)")
    }
    Write-Sum "attach|attachedReported=$attachAdded"
  } else {
    Write-Sum 'attach|skipped'
  }

  $resultsAfter = Count-Results $win
  $newResults = @($resultsAfter | Where-Object { $resultsBefore -notcontains $_ })
  Write-Sum "results|before=$($resultsBefore.Count)|after=$($resultsAfter.Count)|newUnique=$($newResults.Count)"

  $stLog = Join-Path $script:RunDir '03-status.log'
  $st = Invoke-Research $exe @('status', $win) $stLog
  Write-Sum "status|exit=$($st.ExitCode)"
  foreach ($line in ($st.StdOut -split "`r?`n")) {
    if ($line.StartsWith('status|') -or $line.StartsWith('versionGroup|')) { Write-Sum $line }
  }

  $validFinished = $resultsAfter.Count  # independent matchIds with results attached
  $gapValid = [Math]::Max(0, $minTest - $validFinished)
  Write-Sum "validFinishedValidation=$validFinished"
  Write-Sum "gapValidFinished=$gapValid"
  Write-Sum "note|declares-are-not-finished-validation|cal-507-excluded-from-test"
  Write-Sum "Enabled=false|BoundEvidenceId=empty|no-bind|no-enable"

  $activity = ($addedDeclares.Count -gt 0) -or ($newResults.Count -gt 0) -or ($newArchived -gt 0 -and $idempotent -eq 0)
  if ($activity) { $script:Outcome = 'Activity' } else { $script:Outcome = 'Idle' }
  Write-Sum "OUTCOME=$($script:Outcome)"

  # hashes of this run (exclude secrets)
  $hashLines = @()
  Get-ChildItem $script:RunDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    if ($_.Name -eq 'conn.readonly.env.txt') { return }
    $rel = $_.FullName.Substring($script:RunDir.Length).TrimStart('\')
    $hashLines += "$(Get-Sha256 $_.FullName)  $rel"
  }
  $hashLines += "$(Get-Sha256 $hist)  ../../config/history-samples.FIXED.tsv"
  $hashLines | Set-Content (Join-Path $script:RunDir 'SHA256-run.txt') -Encoding UTF8
  $script:SummaryLines | Set-Content (Join-Path $script:RunDir 'summary.txt') -Encoding UTF8

  Release-Mutex
  exit 0
}
catch {
  Write-Sum "FAULT|exception|$($_.Exception.Message)"
  Release-Mutex
  if ($script:RunDir) {
    try { $script:SummaryLines | Set-Content (Join-Path $script:RunDir 'summary.txt') -Encoding UTF8 } catch {}
  }
  exit 1
}
finally {
  Release-Mutex
}
