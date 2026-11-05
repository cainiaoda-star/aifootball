# 把源码 publish 出来的程序集同步到部署包，并重启 API 计划任务。
#
# 2026-11-05 加。这个迁移包的 app\api 里混着自包含运行时文件（coreclr.dll 等），
# 但 runtimeconfig.json 写的是框架依赖，apphost（AiFootBall.Api.exe）因此找不到运行时，
# 只能由 dotnet 宿主启动（见 boot-api.bat）。所以部署不能整目录覆盖，
# 只替换业务程序集 + deps.json，其余运行时文件原样保留。
#
# 用法：
#   powershell -NoProfile -File .\deploy-api.ps1

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'source\aifootball'
$dep = Join-Path $root 'deploy-package'
$api = Join-Path $dep 'app\api'
$pub = Join-Path $env:TEMP 'aifootball-pub-api'

$files = @(
    'AiFootBall.Api.dll',
    'AiFootBall.Core.dll',
    'AiFootBall.Data.dll',
    'AiFootBall.Sources.dll',
    'AiFootBall.Api.deps.json'
)

# 前端也一起同步：publish 出来的 wwwroot 是全新的，但目标目录的 uploads\ 是用户上传的截图，
# 只能覆盖文件、不能删同目录的多余项，所以用逐文件复制而不是整目录镜像。
$webFiles = @('index.html', 'summary.html')

Write-Output '== 1/4 发布 =='
# 新装的 SDK 不一定在当前进程的 PATH 里（winget 装完只刷新了系统变量），固定路径兜底。
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
& $dotnet publish (Join-Path $src 'src\AiFootBall.Api\AiFootBall.Api.csproj') -c Release -o $pub --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish 失败（exit $LASTEXITCODE）" }

Write-Output '== 2/4 停止服务 =='
Stop-ScheduledTask -TaskName 'AiFootBall-API' -ErrorAction SilentlyContinue
foreach ($c in Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue) {
    Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

# 留一份旧程序集，回滚就是把 logs\api-dll-backup-* 拷回 app\api 再启动。
$bak = Join-Path $dep ('logs\api-dll-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $bak -Force | Out-Null
foreach ($n in $files) {
    $f = Join-Path $api $n
    if (Test-Path $f) { Copy-Item $f -Destination $bak -Force }
}
Write-Output "   旧程序集备份：$bak"

Write-Output '== 3/4 替换程序集 =='
foreach ($n in $files) {
    Copy-Item (Join-Path $pub $n) -Destination (Join-Path $api $n) -Force
}
foreach ($n in $webFiles) {
    $f = Join-Path $pub "wwwroot\$n"
    if (Test-Path $f) { Copy-Item $f -Destination (Join-Path $api "wwwroot\$n") -Force }
}

Write-Output '== 4/4 启动并探活 =='
Start-ScheduledTask -TaskName 'AiFootBall-API'
Start-Sleep -Seconds 14

$conn = Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue
if (-not $conn) {
    Write-Output '!! 端口未监听，日志尾部：'
    Get-Content (Join-Path $dep 'logs\api.log') -Tail 20
    exit 1
}

try {
    $code = (Invoke-WebRequest 'http://127.0.0.1:5088/' -UseBasicParsing -TimeoutSec 10).StatusCode
    Write-Output "   PID=$($conn.OwningProcess)  / => HTTP $code"
} catch {
    Write-Output "   PID=$($conn.OwningProcess)  但探活失败：$($_.Exception.Message)"
    exit 1
}
