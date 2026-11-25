param(
    [string]$DDPackagePath,
    [ValidatePattern('^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$')]
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($PSVersionTable.PSVersion.Major -lt 7) { throw '请使用 PowerShell 7。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdk = & (Join-Path $PSScriptRoot 'packaging/Find-DotnetSdk.ps1')
$compiler = (Get-Command makensis -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (!$compiler) {
    $compiler = @(
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe')
        if ($env:NSIS_HOME) { Join-Path $env:NSIS_HOME 'makensis.exe' }
        (Join-Path $env:LOCALAPPDATA 'tauri/NSIS/makensis.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (!$compiler) { throw '需要 NSIS 3，请安装后将 makensis.exe 加入 PATH。' }
$sevenZip = (Get-Command 7z -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (!$sevenZip) { $sevenZip = Join-Path $env:ProgramFiles '7-Zip/7z.exe' }
if (!(Test-Path -LiteralPath $sevenZip -PathType Leaf)) { throw '需要 7-Zip 来提取驱动和校验安装包。' }

$output = New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'artifacts')
$buildRoot = [IO.Path]::GetFullPath((Join-Path $output.FullName 'intermediate'))
$stage = New-Item -ItemType Directory -Force -Path (Join-Path $buildRoot ('wpf-' + [guid]::NewGuid().ToString('N')))
$payload = Join-Path $stage.FullName 'app'
$probe = Join-Path $stage.FullName 'check'
$name = 'RocoMusicPlayer-win-x64-Setup.exe'
$installer = Join-Path $stage.FullName $name
$expected = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging/dd-package-hashes.json') -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
try {
    if ($DDPackagePath) {
        $ddRoot = (Resolve-Path -LiteralPath $DDPackagePath).Path
    } else {
        $archive = Join-Path $stage.FullName 'dd.7z'
        $url = 'https://raw.githubusercontent.com/ddxoft/master/34a1608ac3754674eb4d2487dc8859b1523bd290/2026.DD.EV.HVCI.63xxx.7z'
        Invoke-WebRequest -Uri $url -OutFile $archive -MaximumRetryCount 2
        if ((Get-FileHash -LiteralPath $archive).Hash -ne 'C8FD7B9C88111851BF741DF0EA700F24A7227F21DD1C3DAFE4DE80786EEC5C4A') {
            throw 'DD 厂商压缩包的 SHA-256 不匹配。'
        }
        $files = @($expected.Keys | ForEach-Object { "2.hid/$_" })
        & $sevenZip x -y -bso0 -bsp0 "-o$($stage.FullName)" $archive @files
        $ddRoot = Join-Path $stage.FullName '2.hid'
    }

    $driver = New-Item -ItemType Directory -Force -Path (Join-Path $payload 'drivers/dd/drv')
    foreach ($entry in $expected.GetEnumerator()) {
        $from = Join-Path $ddRoot $entry.Key
        if ((Get-FileHash -LiteralPath $from).Hash -ne $entry.Value) { throw "DD 组件校验失败 $from" }
        if ([IO.Path]::GetExtension($from) -in '.dll','.exe','.cat') {
            if ((Get-AuthenticodeSignature -LiteralPath $from).Status -ne 'Valid') { throw "DD 组件签名无效 $from" }
        }
        Copy-Item -LiteralPath $from -Destination (Join-Path $driver.Parent.FullName $entry.Key)
    }
    & (Join-Path $PSScriptRoot 'packaging/Stage-DDRuntime.ps1') -RuntimeDirectory ([Environment]::SystemDirectory) -DriverDirectory $driver.FullName

    & $sdk publish (Join-Path $projectRoot 'src/RocoPlayer/RocoPlayer.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $payload
    & $sdk publish (Join-Path $projectRoot 'tools/RocoPlayer.DDSetup/RocoPlayer.DDSetup.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $probe
    # The installed helper shares the player's .NET runtime. Preflight can run before either is installed.
    foreach ($extension in 'exe','dll','deps.json','runtimeconfig.json') {
        Copy-Item -LiteralPath (Join-Path $probe "RocoPlayer.DDSetup.$extension") -Destination $payload
    }

    $removeList = Join-Path $stage.FullName 'uninstall-files.nsh'
    $lines = @(
        foreach ($file in Get-ChildItem -File -Recurse -LiteralPath $payload) {
            $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('$','$$')
            'Delete "$INSTDIR\' + $relative + '"'
        }
        foreach ($folder in Get-ChildItem -Directory -Recurse -LiteralPath $payload | Sort-Object { $_.FullName.Length } -Descending) {
            $relative = [IO.Path]::GetRelativePath($payload, $folder.FullName).Replace('$','$$')
            'RMDir "$INSTDIR\' + $relative + '"'
        }
    )
    Set-Content -LiteralPath $removeList -Value $lines -Encoding UTF8
    $icon = Join-Path $projectRoot 'src/RocoPlayer/Assets/icon.ico'
    $defines = @("/DSTAGE=$payload","/DCHECK=$probe","/DOUTFILE=$installer","/DICON=$icon","/DREMOVE_LIST=$removeList","/DVERSION=$Version")
    & $compiler /INPUTCHARSET UTF8 /V2 @defines (Join-Path $PSScriptRoot 'packaging/RocoPlayer.nsi')

    & $sevenZip t -bso0 -bsp0 $installer
    $verify = Join-Path $stage.FullName 'verify'
    $files = @('RocoPlayer.exe','RocoPlayer.dll','RocoPlayer.DDSetup.exe','RocoPlayer.DDSetup.dll','RocoPlayer.DDSetup.runtimeconfig.json')
    $files += @($expected.Keys | ForEach-Object { "drivers/dd/$_" })
    $files += @(Get-ChildItem -LiteralPath $driver.FullName -Filter '*.dll' -File | ForEach-Object { "drivers/dd/drv/$($_.Name)" })
    & $sevenZip x -y -bso0 -bsp0 "-o$verify" $installer @files '$PLUGINSDIR/dd-check/*'
    foreach ($file in $files) {
        if ((Get-FileHash -LiteralPath (Join-Path $verify $file)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $payload $file)).Hash) {
            throw "安装包内容与构建结果不同 $file"
        }
    }
    # Exercise the exact preflight payload NSIS will extract, without installing a driver on the builder.
    $probeExe = Join-Path $verify '$PLUGINSDIR/dd-check/RocoPlayer.DDSetup.exe'
    $process = Start-Process -FilePath $probeExe -WorkingDirectory $verify -ArgumentList '--check-install','--driver-dir','.','--log-dir','logs' -WindowStyle Hidden -Wait -PassThru
    $events = Get-ChildItem -LiteralPath (Join-Path $verify 'logs') -Filter '*.jsonl' -File | Get-Content -Encoding UTF8 | ConvertFrom-Json
    $result = @($events | Where-Object event -eq 'result')
    if ($process.ExitCode -notin 0,2,3 -or $result.Count -ne 1 -or $result[0].code -ne $process.ExitCode -or 'vendor_started' -in $events.event) {
        throw '安装包内的 DD 预检查助手未正常返回'
    }
    Write-Output "DD preflight: $($result[0].reason) ($($process.ExitCode))"
    $digest = (Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant()
    Move-Item -LiteralPath $installer -Destination (Join-Path $output.FullName $name) -Force
    Set-Content -LiteralPath (Join-Path $output.FullName 'SHA256SUMS.txt') -Value "$digest  $name" -Encoding UTF8
    Write-Output (Join-Path $output.FullName $name)
} finally {
    $completed = Get-Item -LiteralPath $stage.FullName
    if ($completed.Parent.FullName -eq $buildRoot -and $completed.Name -match '^wpf-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $completed.FullName -Recurse -Force
    }
}
