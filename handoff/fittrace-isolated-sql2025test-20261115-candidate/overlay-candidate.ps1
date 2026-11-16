$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidate = Join-Path $root "deploy-package\pending\football-97927cbcb5e2"
$expected = @{
    'AiFootBall.Api.dll'     = '38E3ADE7A23661B0662F0771930928D5FB4B158CE1A178F0B6128C594AA952D7'
    'AiFootBall.Core.dll'    = '0928542BAD6EEA36CA28FE8710845CFFD699C36E64F8F78E3058288FB6B1B3D0'
    'AiFootBall.Data.dll'    = '19BEFE683A7D3F94FB6B1C13F39B705B7F0F68B2AF17BB330532A3C4617EC473'
    'AiFootBall.Sources.dll' = '1D3E3D68CC03B0E942424DA113E32F81200A55093CB1BB860C500047E00F5022'
}
foreach ($name in $expected.Keys) {
    $src = Join-Path $candidate $name
    $hash = (Get-FileHash $src -Algorithm SHA256).Hash
    if ($hash -ne $expected[$name]) { throw "$name 与候选 SHA 不一致: $hash" }
}

$destinations = @(
    (Join-Path $root "source\aifootball\tests\AiFootBall.Tests\bin\Release\net8.0"),
    (Join-Path $root "source\aifootball\src\AiFootBall.Core\bin\Release\net8.0"),
    (Join-Path $root "source\aifootball\src\AiFootBall.Data\bin\Release\net8.0"),
    (Join-Path $root "source\aifootball\src\AiFootBall.Sources\bin\Release\net8.0")
)
foreach ($dir in $destinations) {
    if (-not (Test-Path $dir)) { continue }
    foreach ($name in $expected.Keys) {
        if ($name -eq 'AiFootBall.Api.dll' -and $dir -notlike '*AiFootBall.Tests*') { continue }
        Copy-Item (Join-Path $candidate $name) (Join-Path $dir $name) -Force
        $copied = (Get-FileHash (Join-Path $dir $name) -Algorithm SHA256).Hash
        if ($copied -ne $expected[$name]) { throw "覆盖后 $dir\$name 哈希不是候选" }
    }
}
Write-Output "overlay-ok"
