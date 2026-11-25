$ErrorActionPreference = 'Stop'
$command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$localSdk = Join-Path $env:LOCALAPPDATA 'RocoPlayerBuild/dotnet8/dotnet.exe'
foreach ($candidate in @($command.Source, $localSdk)) {
    if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        $sdks = & $candidate --list-sdks
        if ($LASTEXITCODE -eq 0 -and ($sdks -match '^(?:[89]|[1-9][0-9]+)\.')) { return $candidate }
    }
}
throw '未找到 .NET 8 或更高版本 SDK。可直接启动独立发布目录中的 RocoPlayer.exe。'
