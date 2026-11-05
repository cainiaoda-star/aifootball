# Pull a file that lives on the SQL Server machine down to this host, using
# OPENROWSET(BULK ..., SINGLE_BLOB). No SMB share and no xp_cmdshell needed:
# the SQL login can already read the file (the instance wrote it).
# ASCII only on purpose (PowerShell 5.1 reads .ps1 as GBK without BOM).
param(
    [Parameter(Mandatory = $true)][string]$RemotePath,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$ConfigPath = 'E:\WB_Aicode\aifootball\src\AiFootBall.Api\appsettings.json',
    [string]$Conn
)

$ErrorActionPreference = 'Stop'

if (-not $Conn) {
    $json = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json
    $Conn = $json.ConnectionStrings.AiFootBall
}
if (-not $Conn) { throw 'no connection string' }

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sqlConn = New-Object -TypeName System.Data.SqlClient.SqlConnection -ArgumentList $Conn
$sqlConn.Open()
try {
    $cmd = $sqlConn.CreateCommand()
    $cmd.CommandText = "SELECT BulkColumn FROM OPENROWSET(BULK N'$RemotePath', SINGLE_BLOB) AS x"
    $cmd.CommandTimeout = 0

    $r = $cmd.ExecuteReader([System.Data.CommandBehavior]::SequentialAccess)
    if (-not $r.Read()) { throw 'no row returned' }

    $src = $r.GetStream(0)
    $dst = [System.IO.File]::Create($OutFile)
    $buf = New-Object byte[] (4 * 1024 * 1024)
    $total = 0
    $lastReport = 0
    while (($n = $src.Read($buf, 0, $buf.Length)) -gt 0) {
        $dst.Write($buf, 0, $n)
        $total += $n
        if ($total - $lastReport -gt (100 * 1024 * 1024)) {
            $lastReport = $total
            "  ... " + [math]::Round($total / 1MB, 1) + " MB"
        }
    }
    $dst.Flush()
    $dst.Close()
    $src.Close()
    $r.Close()
    "written: " + $total + " bytes"
}
finally {
    $sqlConn.Close()
    $sqlConn.Dispose()
}
