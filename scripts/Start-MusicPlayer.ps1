$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($PSVersionTable.PSVersion.Major -lt 7) { throw '请使用 PowerShell 7。' }
$project = Join-Path $projectRoot 'src/RocoPlayer/RocoPlayer.csproj'
$release = Join-Path $projectRoot 'artifacts/RocoPlayer/RocoPlayer.exe'
$oldRoot = $env:ROCK_MUSIC_HOME
try {
    if (!$oldRoot) { $env:ROCK_MUSIC_HOME = $projectRoot }
    if (Test-Path -LiteralPath $release -PathType Leaf) {
        & $release
    } else {
        $sdk = & (Join-Path $PSScriptRoot 'packaging/Find-DotnetSdk.ps1')
        & $sdk run --project $project -c Release
        if ($LASTEXITCODE -ne 0) { throw '播放器启动失败' }
    }
} finally { $env:ROCK_MUSIC_HOME = $oldRoot }
