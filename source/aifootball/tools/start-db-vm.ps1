<#
  AiFootBall boot helper: start the database VM and wait until SQL is reachable.

  Why this exists (2026-11-04):
  The database is NOT a local SQL Server instance. It runs inside a VMware VM
  "WinSrv2025_Aicede" (guest hostname Aicode, host-only VMnet1, 192.168.117.128).
  After a host reboot that VM stays powered off, so the API dies at startup with
  a SQL connection timeout.

  Beware: "D:\Virtual Machines\WinSrv2025_SQL2025" also exists but uses NAT and
  gets 192.168.255.128 - it is NOT this project's database. Identify the VM by
  MAC 00:0c:29:a6:4d:59, not by its display name.

  Triggered at user logon by scheduled task "AiFootBall-DB-VM-Autostart".
  No administrator rights required.

  ASCII ONLY on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI
  (GBK here), and Chinese characters corrupt the parser. Keep this file ASCII.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # otherwise Write-Progress pollutes output

$vmrun  = 'C:\Program Files\VMware\VMware Workstation\vmrun.exe'
$vmx    = 'D:\Virtual Machines\WinSrv2025_Aicede\WinSrv2025_Aicede.vmx'
$log    = 'D:\AiFootBall\logs\vm-autostart.log'
$dbHost = '192.168.117.128'
$dbPort = 1433

function Write-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    $dir = Split-Path $log -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Add-Content -Path $log -Value $line -Encoding utf8
}

function Test-Port {
    param([string]$Computer, [int]$Port, [int]$TimeoutMs = 2000)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($Computer, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) { return $false }
        $client.EndConnect($iar)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Test-VmRunning {
    try {
        $list = & $vmrun list 2>$null
        return (($list -join "`n") -match [regex]::Escape($vmx))
    } catch {
        return $false
    }
}

try {
    Write-Log '===== boot check start ====='

    if (-not (Test-Path $vmrun)) { Write-Log "vmrun not found: $vmrun"; exit 1 }
    if (-not (Test-Path $vmx))   { Write-Log "vmx not found: $vmx"; exit 1 }

    # Already reachable (e.g. the user started it manually) - nothing to do.
    if (Test-Port -Computer $dbHost -Port $dbPort) {
        Write-Log "db already reachable ($dbHost`:$dbPort), skip VM start"
        exit 0
    }

    if (-not (Test-VmRunning)) {
        # At logon the VMware services may still be starting, so retry.
        $started = $false
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Write-Log "starting VM (attempt $attempt): $vmx"
                $out = & $vmrun -T ws start $vmx nogui 2>&1
                if ($out) { foreach ($o in $out) { Write-Log "  vmrun: $o" } }
                Start-Sleep -Seconds 2
                if (Test-VmRunning) { $started = $true; break }
            } catch {
                Write-Log ('  vmrun exception: ' + $_.Exception.Message)
            }
            Start-Sleep -Seconds 15
        }
        if (-not $started) { Write-Log 'VM did not start after 3 attempts, giving up'; exit 1 }
        Write-Log 'VM started, waiting for SQL port'
    } else {
        Write-Log 'VM already running, waiting for SQL port'
    }

    # Windows Server cold boot to a listening 1433 takes about 80-90s; allow 5 min.
    for ($i = 1; $i -le 60; $i++) {
        if (Test-Port -Computer $dbHost -Port $dbPort) {
            Write-Log ("SQL ready ($dbHost`:$dbPort) after about {0}s" -f ($i * 5))
            exit 0
        }
        Start-Sleep -Seconds 5
    }

    Write-Log 'timeout: 1433 still closed after 300s, giving up'
    exit 1
} catch {
    Write-Log ('top-level exception: ' + $_.Exception.Message)
    exit 1
}
