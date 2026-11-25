$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdk = & (Join-Path $PSScriptRoot 'packaging/Find-DotnetSdk.ps1')
if ($PSVersionTable.PSVersion.Major -lt 7) { throw '请使用 PowerShell 7。' }
$project = Join-Path $projectRoot 'src/RocoPlayer/RocoPlayer.csproj'
$destination = Join-Path $projectRoot 'artifacts/RocoPlayer'
& $sdk publish $project -c Release -r win-x64 --self-contained true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Write-Output (Join-Path $destination 'RocoPlayer.exe')
