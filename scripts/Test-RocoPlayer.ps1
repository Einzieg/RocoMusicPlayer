$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($PSVersionTable.PSVersion.Major -lt 7) { throw '请使用 PowerShell 7。' }

$projectRoot = Split-Path -Parent $PSScriptRoot
$sdk = & (Join-Path $PSScriptRoot 'packaging/Find-DotnetSdk.ps1')
Push-Location -LiteralPath $projectRoot
try {
    & $sdk build RocoPlayer.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw '解决方案构建失败' }

    foreach ($suite in 'music','audio','playback','desktop','ddsetup','clicker') {
        & $sdk run --project "tests/csharp-$suite" -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "$suite 回归检查失败" }
    }
    foreach ($tool in 'Arrange','Calibrate') {
        & $sdk run --project "tools/RocoPlayer.$tool" -c Release --no-build -- --help
        if ($LASTEXITCODE -ne 0) { throw "$tool 命令行入口检查失败" }
    }
} finally {
    Pop-Location
}
