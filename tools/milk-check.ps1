# Milk board health check - the "baseline EV disappeared?" guard.
#
# Why this exists: 2026-11-07 the board was restored from an old snapshot table
# that predates the BaselineProbability / BaselineEv / ModelLowConfidence columns,
# so all 9 rows had NULL baseline. The front end silently fell back to the model
# EV and nothing looked broken. This script makes that failure loud.
#
# Run it after every 8:40 / 11:40 / 16:40 / 20:40 board build:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\milk-check.ps1
#
# Exit code 0 = all checks passed, 1 = something is wrong (see FAIL lines).
# ASCII only on purpose: PS 5.1 parses BOM-less UTF-8 files as ANSI and would
# garble Chinese output. Translate the result in chat instead.

param(
    [string]$Database = "AiFootBall",
    [string]$Server = "",
    [string]$User = "",
    [string]$Password = "",
    [string]$ApiBase = "http://127.0.0.1:5088",
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
# Invoke-WebRequest writes a progress bar to the information stream; it lands in
# the middle of the report and makes it unreadable. Kill it.
$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = 'Stop'
$fail = 0

function Fail($msg) { Write-Output ("FAIL  " + $msg); $script:fail = 1 }
function Ok($msg)   { Write-Output ("OK    " + $msg) }
function Warn($msg) { Write-Output ("WARN  " + $msg) }

function Sql([string]$q) {
    $out = sqlcmd -S $Server -d $Database -U $User -P $Password -C -W -h -1 -s'|' -l $Timeout -Q "SET NOCOUNT ON; $q"
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $q" }
    return ($out | Where-Object { $_ -ne '' })
}

Write-Output "=== milk board check ==="

# --- 1. schema: the columns must exist ---------------------------------------
# ModelOverconfident added 2026-11-08 (nominal EV above the play-type payback).
# DivergenceTag / DivergenceNote added 2026-11-08 (JC-vs-Betfair one-sided pricing).
# HalfDrawRefined / FatigueTag added 2026-11-08 (3-1/0-1 direction model + fatigue label).
$wantCols = @('CounterHigh', 'BaselineProbability', 'BaselineEv', 'ModelLowConfidence', 'ModelOverconfident', 'DivergenceTag', 'DivergenceNote', 'HalfDrawRefined', 'FatigueTag')
$colList = ($wantCols | ForEach-Object { "'$_'" }) -join ','
$cols = (Sql "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='FbMilkPick' AND COLUMN_NAME IN ($colList)") -join ','
foreach ($c in $wantCols) {
    if ($cols -notmatch $c) { Fail "column FbMilkPick.$c is MISSING (table rebuilt?)" }
}
if ($fail -eq 0) { Ok ("schema has all " + $wantCols.Count + " columns") }

# --- 2. latest board stats ----------------------------------------------------
# d | rows | counter | counterWithBase | lowConf | lowConfWithBase | anyWithBase
$row = (Sql @"
DECLARE @d date = (SELECT MAX(PickDate) FROM FbMilkPick);
SELECT CONVERT(varchar(10),@d,23) + '|' + CAST(COUNT(*) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN CounterHigh=1 THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN CounterHigh=1 AND BaselineEv IS NOT NULL THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN ModelLowConfidence=1 THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN ModelLowConfidence=1 AND BaselineEv IS NOT NULL THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN BaselineEv IS NOT NULL THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN ModelOverconfident=1 THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN DivergenceTag IS NOT NULL THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN DivergenceTag='fake-hot-away' AND (
          (PlayType='BQ' AND RIGHT(OptionCode,1)='0')
       OR (PlayType='BF' AND TRY_CAST(LEFT(OptionCode, CHARINDEX(':',OptionCode)-1) AS int)
                        < TRY_CAST(SUBSTRING(OptionCode, CHARINDEX(':',OptionCode)+1, 2) AS int))
     ) THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN HalfDrawRefined=1 THEN 1 ELSE 0 END) AS varchar(10)) + '|'
 + CAST(SUM(CASE WHEN FatigueTag IS NOT NULL THEN 1 ELSE 0 END) AS varchar(10))
FROM FbMilkPick WHERE PickDate=@d
"@) -join ''

$p = $row -split '\|'
$d           = $p[0]
$nRows       = [int]$p[1]
$nCounter    = [int]$p[2]
$nCounterB   = [int]$p[3]
$nLowConf    = [int]$p[4]
$nLowConfB   = [int]$p[5]
$nAnyBase    = [int]$p[6]
$nOverConf   = [int]$p[7]
$nDivg       = [int]$p[8]
$nDivgAway   = [int]$p[9]
$nHalfDraw   = [int]$p[10]
$nFatigue    = [int]$p[11]

Write-Output ("board date=" + $d + " rows=" + $nRows + " counterHigh=" + $nCounter + " lowConfidence=" + $nLowConf + " overconfident=" + $nOverConf + " withBaseline=" + $nAnyBase + " divergence=" + $nDivg + " halfDrawRefined=" + $nHalfDraw + " fatigueTagged=" + $nFatigue)

# --- 2b. the divergence veto must actually be running ------------------------
# The tag is written per MATCH (after veto) onto every surviving pick, so a
# fake-hot-away tag legitimately appears on HOME/draw picks - it is the note
# "this match's away side was excluded". What must never exist is an AWAY
# direction pick still carrying fake-hot-away; that means the veto did not run.
# (Before 2026-11-08 this check counted every tagged row and produced a false
#  alarm on the Sunday-012 Ipswich row, whose pick was a home direction.)
# NOTE: keep this file pure ASCII. PS 5.1 reads a BOM-less file as ANSI, and
# UTF-8 Chinese in a comment can swallow the following bytes (including the
# newline) and break parsing. This line used to hold Chinese and was the only
# non-ASCII byte run left in the file.
if ($nDivgAway -gt 0) {
    Fail ("$nDivgAway away-direction row(s) tagged fake-hot-away still on the board - divergence veto not applied")
} elseif ($nDivg -gt 0) {
    Ok ("divergence tag present on $nDivg row(s), none of them points at the vetoed away side")
} else {
    Warn "no divergence tag today - the signal did not fire, nothing verified"
}

# --- 3. the actual regression this script guards -------------------------------
if ($nRows -eq 0) {
    Warn "no rows for latest date - board not built yet"
} elseif ($nCounter -eq 0 -and $nLowConf -eq 0) {
    Warn "no CounterHigh and no ModelLowConfidence row - channel not exercised today, cannot verify"
} else {
    if ($nCounter -gt 0 -and $nCounterB -lt $nCounter) {
        Fail ("BaselineEv MISSING on " + ($nCounter - $nCounterB) + "/" + $nCounter + " CounterHigh rows - front end will silently show model EV")
    }
    if ($nLowConf -gt 0 -and $nLowConfB -lt $nLowConf) {
        Fail ("BaselineEv MISSING on " + ($nLowConf - $nLowConfB) + "/" + $nLowConf + " ModelLowConfidence rows - badge has no numbers to show")
    }
    if ($nCounter -gt 0 -and $nCounterB -eq $nCounter) { Ok "all $nCounter CounterHigh rows carry baseline" }
    if ($nLowConf -gt 0 -and $nLowConfB -eq $nLowConf) { Ok "all $nLowConf low-confidence rows carry baseline" }
}

# --- 3a. every BQ/BF/JQ row must carry a baseline -----------------------------
# 2026-11-08: OptionBaseline now covers all 9 half-full codes, all 31 scorelines
# and all 8 total-goal buckets, and MilkPickEngine.MarkBaseline fills every pick.
# Before this, only CounterHigh / ModelLowConfidence rows had a baseline, so the
# check above passed while 9 of 10 main-board rows were NULL - the hole was
# invisible by construction. SPF/NSPF are exempt on purpose (no baseline table;
# direction is handled by the direction gate).
# SettledAt IS NULL: settled rows are kept for review and are not on the board,
# so they must not count against this check.
$noBase = (Sql @"
DECLARE @d date = (SELECT MAX(PickDate) FROM FbMilkPick);
SELECT CAST(COUNT(*) AS varchar(10)) FROM FbMilkPick
WHERE PickDate=@d AND PlayType IN ('BQ','BF','JQ') AND BaselineEv IS NULL AND SettledAt IS NULL
"@) -join ''
if ([int]$noBase -gt 0) {
    Fail ("$noBase BQ/BF/JQ row(s) have no BaselineEv - MarkBaseline dropped or OptionBaseline gap")
} else {
    Ok "every BQ/BF/JQ row carries a baseline"
}

# --- 3b. overconfident flag must match the actual EV vs payback cap ----------
# Cap mirrors MilkPickEngine.EvCapFor: BQ/JQ 0.837, BF 0.745, SPF/NSPF 0.922.
# Two ways this can silently rot: flag set but EV under cap (badge lies), or EV
# over cap without flag (marker never fired because MarkOverconfident was dropped).
$capExpr = "CASE WHEN PlayType IN ('BQ','JQ') THEN 0.837 WHEN PlayType='BF' THEN 0.745 ELSE 0.922 END"
$mis = (Sql @"
DECLARE @d date = (SELECT MAX(PickDate) FROM FbMilkPick);
SELECT CAST(SUM(CASE WHEN ModelOverconfident=1 AND Ev <= $capExpr THEN 1 ELSE 0 END) AS varchar(10))
     + '|' + CAST(SUM(CASE WHEN ModelOverconfident=0 AND Ev > $capExpr THEN 1 ELSE 0 END) AS varchar(10))
FROM FbMilkPick WHERE PickDate=@d
"@) -join ''
$mp = $mis -split '\|'
$falsePos = [int]$mp[0]
$falseNeg = [int]$mp[1]
if ($falsePos -gt 0) { Fail ("$falsePos rows flagged ModelOverconfident but EV is under the play-type cap") }
if ($falseNeg -gt 0) { Fail ("$falseNeg rows have EV above the play-type cap but are NOT flagged (MarkOverconfident dropped?)") }
if ($falsePos -eq 0 -and $falseNeg -eq 0) { Ok "overconfident flag matches EV vs cap on every row" }

# --- 4. API projection must expose the fields ---------------------------------
# A row can have the column filled and still vanish here if the projection in
# Api/Program.cs was reverted - the front end only ever sees the JSON.
try {
    # 90s, not 25s: right after a deploy the endpoint pays JIT + a cold SQL plan
    # (measured 212ms warm, but the first call after restart blew past 25s and
    # produced a false FAIL). This is a health check, not a latency SLO.
    # Ask with the explicit date (2026-11-09 fix). Without ?date= the endpoint applies
    # the "board lists only still-purchasable picks" filter; on a day whose picks are
    # all settled it returns [] and every key check below turns into a false FAIL
    # (measured 7 of them on the morning of 11-09). With ?date= it is a replay view
    # and keeps settled rows - which also proves the replay path itself works.
    $json = (Invoke-WebRequest ($ApiBase + "/api/milk/picks?date=" + $d) -UseBasicParsing -TimeoutSec 90).Content
    $emptyBoard = ($json.Trim() -eq '[]')
    if ($emptyBoard) {
        if ($nRows -gt 0) { Fail ("api returned an empty board for " + $d + " but DB has " + $nRows + " rows") }
        else              { Warn "api board is empty and DB has no rows for that date" }
    } else {
    if ($json -notmatch 'counterHigh')     { Fail "api response has no 'counterHigh' key" }
    if ($json -notmatch 'modelLowConfidence') { Fail "api response has no 'modelLowConfidence' key" }
    if ($json -notmatch 'modelOverconfident') { Fail "api response has no 'modelOverconfident' key" }
    if ($json -notmatch 'evCap')              { Fail "api response has no 'evCap' key" }
    # Same null-suppression rule as divergence: halfDrawRefined is a bool that only
    # shows when true, fatigueTag only when non-null. Demand them only when the DB
    # actually has such rows, otherwise a normal day reads as a regression.
    if ($nHalfDraw -gt 0 -and $json -notmatch 'halfDrawRefined') {
        Fail "api response drops 'halfDrawRefined' but DB has $nHalfDraw refined rows"
    }
    if ($nFatigue -gt 0 -and $json -notmatch 'fatigueTag') {
        Fail "api response drops 'fatigueTag' but DB has $nFatigue tagged rows"
    }
    # Serializer uses WhenWritingNull, so a null divergence field disappears from
    # the JSON entirely. Only demand the key when the DB actually has a tagged row
    # (same rule as baselineEv below) - otherwise a quiet day looks like a bug.
    if ($nDivg -gt 0) {
        if ($json -notmatch 'divergenceTag')  { Fail "api response drops 'divergenceTag' but DB has $nDivg tagged rows" }
        if ($json -notmatch 'divergenceNote') { Fail "api response drops 'divergenceNote' but DB has $nDivg tagged rows" }
        else { Ok "api exposes divergence fields" }
    } else {
        Warn "DB has no divergence tag, api divergence keys expected to be absent"
    }
    # baselineEv/baselineProbability are null on boards where no row qualifies,
    # so their absence is only fatal when a qualifying row exists.
    if ($nAnyBase -gt 0) {
        if ($json -notmatch 'baselineEv')          { Fail "api response drops 'baselineEv' but DB has $nAnyBase rows with baseline" }
        if ($json -notmatch 'baselineProbability') { Fail "api response drops 'baselineProbability' but DB has $nAnyBase rows with baseline" }
        else { Ok "api exposes baseline fields" }
    } else {
        Warn "DB has no baseline values, api baseline keys expected to be absent"
    }
    }
} catch {
    Fail ("api not reachable at " + $ApiBase + " - " + $_.Exception.Message)
}

# --- 4b. half-time score sanity ---------------------------------------------
# Why: 2026-11-08 we found that FbMatch.HalfHomeScore had been a copy of the
# full-time score for 84% of all matches, for months, and nothing noticed. Every
# half-full-time baseline (BQ nine options, league reversal table, HalfDrawModel)
# was built on that garbage. The parser is fixed now; this check is the alarm so
# a silent regression cannot happen again.
# Rule of thumb from a real 500.com page: same-score share ~20%, half goals ~1.34.
try {
    $h = Sql @"
SELECT COUNT(*), SUM(CASE WHEN HalfHomeScore=HomeScore AND HalfAwayScore=AwayScore THEN 1 ELSE 0 END),
       SUM(CASE WHEN HalfHomeScore>HomeScore OR HalfAwayScore>AwayScore THEN 1 ELSE 0 END)
FROM dbo.FbMatch
WHERE MatchTime >= DATEADD(day,-7,CAST(GETDATE() AS date)) AND MatchTime < GETDATE()
  AND HomeScore IS NOT NULL AND HalfHomeScore IS NOT NULL
"@
    if ($h.Count -eq 0) {
        Warn "no finished match with half score in the last 7 days"
    } else {
        # Sql() returns a single string when the query yields one row, and indexing a
        # string in PowerShell gives a CHAR - $h[0] would be "4" out of "4314|868|0"
        # and the check would silently report "only 4 matches". Wrap it into an array.
        $p = (@($h)[0]) -split '\|'
        $n = [int]$p[0]
        $same = 0; if ($p.Count -gt 1 -and $p[1] -ne 'NULL') { $same = [int]$p[1] }
        $bad = 0; if ($p.Count -gt 2 -and $p[2] -ne 'NULL') { $bad = [int]$p[2] }
        if ($n -lt 200) {
            Warn ("only " + $n + " finished matches with half score in last 7 days - too few to judge")
        } else {
            $pct = [math]::Round(100.0 * $same / $n, 1)
            if ($pct -ge 65) {
                Fail ("half score looks copied from full score again: same " + $pct + "% of " + $n + " matches (real ~20%)")
            } elseif ($pct -ge 40) {
                Warn ("half score same-share " + $pct + "% of " + $n + " matches - higher than the ~20% real value")
            } else {
                Ok ("half score sane: same " + $pct + "% of " + $n + " matches")
            }
        }
        if ($bad -gt 0) { Fail ("half score greater than full score on " + $bad + " matches - parser broken") }
    }
} catch {
    Warn ("half-time check skipped - " + $_.Exception.Message)
}

# --- 4c. job health: reads must not be silently killed by write locks ---------
# 2026-11-08 measurement: 51 timeouts in one day, almost all between 00:20 and
# 08:10 -- football-night full collection racing jc-odds-sample (1620 rows every
# 2 minutes), with READ_COMMITTED_SNAPSHOT still OFF. The read blocks on the
# write, hits the 10 minute hard limit and dies. A timeout is not a crash: the
# job simply reports nothing, which is exactly the "monitored but never acted"
# failure shape this project keeps hitting.
# Night failures only leave gaps in the odds trajectory; daytime failures happen
# while the board is being built, so they fail hard.
try {
    $h = @(Sql @"
DECLARE @d date = CAST(GETDATE() AS date);
SELECT ISNULL(CAST(SUM(CASE WHEN DATEPART(hour, ISNULL(StartedAt, FinishedAt)) < 8 THEN 1 ELSE 0 END) AS varchar(10)), '0') + '|'
     + ISNULL(CAST(SUM(CASE WHEN DATEPART(hour, ISNULL(StartedAt, FinishedAt)) >= 8 THEN 1 ELSE 0 END) AS varchar(10)), '0') + '|'
     + ISNULL(CAST(COUNT(*) AS varchar(10)), '0')
FROM dbo.SyncLog
WHERE ISNULL(StartedAt, FinishedAt) >= @d AND Status <> 'SUCCESS'
"@)
    $recent = @(Sql @"
DECLARE @d date = CAST(GETDATE() AS date);
SELECT ISNULL(CAST(COUNT(*) AS varchar(10)), '0')
FROM dbo.SyncLog
WHERE Status <> 'SUCCESS'
  AND ISNULL(StartedAt, FinishedAt) >= DATEADD(minute, -90, GETDATE())
  AND ISNULL(StartedAt, FinishedAt) >= @d;
"@)
    if ($h.Count -eq 0) {
        Warn "job health unknown - SyncLog returned nothing"
    } else {
        $p = ((@($h)[0]) -split '\|')
        $night = [int]$p[0]; $day = [int]$p[1]; $tot = [int]$p[2]
        $now = if (@($recent).Count -gt 0) { [int](@($recent)[0]) } else { 0 }
        # 2026-11-09 changed the verdict rule. It used to be "any daytime failure
        # today = FAIL", which stays red for the rest of the day even after the
        # cause is fixed (RCSI was only enabled at 10:15, so the 140 overnight
        # failures would keep this red until midnight). A check that is always red
        # is a check nobody reads.
        # Three tiers now: failures in the last 90 minutes = FAIL (broken right
        # now); earlier failures = WARN only (the trajectory gaps are real and
        # must be reported, but they do not mean the system is broken now).
        if ($now -gt 0) {
            Fail ("job failures in the last 90 minutes: " + $now + " (day " + $day + " / night " + $night + " / total " + $tot + ") - still failing right now")
        } elseif ($day -gt 0) {
            Warn ("daytime job failures earlier today: " + $day + " (night " + $night + ", total " + $tot + ") - all recovered, odds trajectory keeps the gaps")
        } elseif ($night -ge 10) {
            Warn ("overnight job failures today: " + $night + " - odds trajectory has gaps")
        } else {
            Ok ("job health: " + $tot + " failure(s) today (night " + $night + ", day " + $day + ")")
        }
    }
} catch {
    Warn ("job health check skipped - " + $_.Exception.Message)
}

# --- 4d. alert -> pick write-back must actually reach the pick ----------------
# 2026-11-10: found that the whole turn-alert write-back chain had never once
# reached a pick. ApplyTurnAlertsToPicksAsync filtered p.PickDate = today while
# GetOpenDayAsync returns open picks regardless of date, so after midnight every
# overnight match (03:00 kickoffs, the ones that matter most) got an alert row
# that nobody applied: the monitoring tab showed the alert, the pick stayed clean.
# Nothing anywhere could notice that, because both halves reported success.
# This check is that missing alarm. It only asks about high alerts that are still
# live AND still have a matching unsettled pick row: if such a row exists and was
# not voided, the chain is broken. A pick that was recalculated away has no row
# and is correctly skipped.
try {
    $h = @(Sql @"
DECLARE @live int, @broken int;
SELECT @live = COUNT(*) FROM dbo.FbTurnAlert a
WHERE a.Severity = 'high' AND (a.MatchTime IS NULL OR a.MatchTime > GETDATE());

SELECT @broken = COUNT(*)
FROM dbo.FbTurnAlert a
JOIN dbo.FbMilkPick p
  ON p.MatchId = a.MatchId AND p.PlayType = a.PlayType AND p.OptionCode = a.OptionCode
WHERE a.Severity = 'high'
  AND (a.MatchTime IS NULL OR a.MatchTime > GETDATE())
  AND p.SettledAt IS NULL
  AND (p.AlertSeverity IS NULL OR ISNULL(p.Voided, 0) = 0);

SELECT ISNULL(CAST(@live AS varchar(10)), '0') + '|' + ISNULL(CAST(@broken AS varchar(10)), '0');
"@)
    $p = @($h)[0]
    if ($p -is [string] -and $p.Contains('|')) {
        $q = $p -split '\|'
        $live = [int]$q[0]; $broken = [int]$q[1]
        if ($broken -gt 0) {
            Fail ("turn alert was written but the pick was never voided: " + $broken + " of " + $live + " live high alerts - write-back chain broken (date mismatch class)")
        } elseif ($live -eq 0) {
            Ok "turn alert write-back: no live high alert right now, nothing to verify"
        } else {
            Ok ("turn alert write-back reached every pick: " + $live + " live high alerts, 0 left unvoided")
        }
    } else {
        Warn "turn alert check skipped - unexpected result shape"
    }
} catch {
    Warn ("turn alert check skipped - " + $_.Exception.Message)
}

# --- 4e. BettingCard.Expect must be filled for jc / bd ------------------------
# 2026-11-10: jc and bd never wrote Expect (only sfc did), so the MERGE key
# (Board, Expect, MatchNo) degenerated to (Board, MatchNo). "Tue001" repeats
# every week, so last week's card was overwritten in place -- that is how the
# 11-04 backtest ended up with one Champions League match out of six. The unique
# index UQ_BettingCard was no help either: SQL Server treats multiple NULLs as
# distinct, so it never fired while Expect was NULL for everything.
# Rule: the newest batch per board must carry Expect on every row.
try {
    $h = @(Sql @"
DECLARE @d date = CAST(GETDATE() AS date);
SELECT ISNULL(CAST(SUM(CASE WHEN Expect IS NULL THEN 1 ELSE 0 END) AS varchar(10)), '0') + '|'
     + ISNULL(CAST(COUNT(*) AS varchar(10)), '0')
FROM dbo.BettingCard c
WHERE c.Board IN ('jc', 'bd')
  AND c.CapturedAt = (SELECT MAX(x.CapturedAt) FROM dbo.BettingCard x WHERE x.Board = c.Board)
  AND c.MatchTime >= DATEADD(day, -1, @d);
"@)
    $p = @($h)[0]
    if ($p -is [string] -and $p.Contains('|')) {
        $q = $p -split '\|'
        $miss = [int]$q[0]; $tot = [int]$q[1]
        if ($tot -eq 0) {
            Warn "BettingCard.Expect check skipped - no recent jc/bd card"
        } elseif ($miss -gt 0) {
            Fail ("BettingCard.Expect empty on " + $miss + " of " + $tot + " jc/bd rows - MERGE key degenerated to (Board, MatchNo), weekly overwrite is back")
        } else {
            Ok ("BettingCard.Expect filled on all " + $tot + " recent jc/bd rows")
        }
    } else {
        Warn "BettingCard.Expect check skipped - unexpected result shape"
    }
} catch {
    Warn ("BettingCard.Expect check skipped - " + $_.Exception.Message)
}

# --- 5. decision --------------------------------------------------------------
Write-Output "=== result ==="
if ($fail -eq 0) { Write-Output "PASS"; exit 0 } else { Write-Output "FAIL"; exit 1 }
