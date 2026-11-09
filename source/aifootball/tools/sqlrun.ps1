# Minimal SQL runner (no sqlcmd / Invoke-Sqlcmd on this box).
# Uses System.Data.SqlClient from .NET Framework: SQL auth + TrustServerCertificate works.
# ASCII only on purpose: PowerShell 5.1 reads .ps1 as GBK when there is no BOM.
param(
    [string]$Query,
    [string]$File,
    [string]$ConfigPath = 'E:\WB_Aicode\aifootball\src\AiFootBall.Api\appsettings.json',
    [string]$Conn,
    [string]$Db,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if (-not $Conn) {
    $json = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json
    $Conn = $json.ConnectionStrings.AiFootBall
}
if (-not $Conn) { throw 'no connection string' }

if ($Db) {
    $Conn = [regex]::Replace($Conn, '(?i)Database\s*=\s*[^;]*', "Database=$Db")
}

if ($File) {
    if (-not (Test-Path $File)) { throw "sql file not found: $File" }
    $Query = Get-Content -Raw -Path $File
}
if (-not $Query) { throw 'no query' }

$sqlConn = New-Object -TypeName System.Data.SqlClient.SqlConnection -ArgumentList $Conn
$sqlConn.Open()
try {
    $cmd = $sqlConn.CreateCommand()
    $cmd.CommandText = $Query
    $cmd.CommandTimeout = 0
    $ds = New-Object System.Data.DataSet
    $ad = New-Object System.Data.SqlClient.SqlDataAdapter -ArgumentList $cmd
    [void]$ad.Fill($ds)
    if (-not $Quiet) {
        if ($ds.Tables.Count -eq 0) { 'OK' }
        foreach ($t in $ds.Tables) {
            ($t.Columns | ForEach-Object { $_.ColumnName }) -join "`t"
            foreach ($r in $t.Rows) {
                $vals = $t.Columns | ForEach-Object {
                    $v = $r[$_.ColumnName]
                    if ($v -is [DBNull]) { 'NULL' } else { $v.ToString() }
                }
                $vals -join "`t"
            }
        }
    }
}
finally {
    $sqlConn.Close()
    $sqlConn.Dispose()
}
