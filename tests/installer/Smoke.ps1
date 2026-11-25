$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# This exercises the shipping, machine-wide installer. Only use a fresh, disposable hosted Windows VM.
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') { throw '此测试需要临时 Windows 托管运行器。' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '安装验证需要管理员权限。' }
Add-Type -TypeDefinition (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'InstallerWizard.cs') -Raw -Encoding UTF8)
$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../artifacts/RocoMusicPlayer-win-x64-Setup.exe')).Path
$registry = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF'
$desktopLink = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'RocoMusicPlayer.lnk'
$menuLink = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'RocoPlayer WPF.lnk'
$logs = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'RocoPlayer/logs'
$runtimeLogs = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RocoPlayer/logs'
foreach ($path in $registry,$desktopLink,$menuLink,$logs) {
    if (Test-Path -LiteralPath $path) { throw "测试需要干净的运行器，已存在 $path" }
}

function Wait-Control($process, [scriptblock]$find, [string]$description) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 90 -and !$process.HasExited) {
        $control = & $find
        if ($control -and $control -ne [IntPtr]::Zero) { return $control }
        Start-Sleep -Milliseconds 125
    }
    throw "向导未显示 $description"
}

function Finish-Wizard($process, [IntPtr]$window) {
    [InstallerWizard]::Next($window)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $lastMessage = ''
    while (!$process.WaitForExit(300) -and $timer.Elapsed.TotalSeconds -lt 150) {
        $message = [InstallerWizard]::ModalMessage($window)
        if ($message) {
            if ($message -ne $lastMessage) { Write-Output "向导提示 $message" }
            $lastMessage = $message
            if ($message -notmatch 'DD') { throw "向导出现意外提示 $message" }
            [InstallerWizard]::ConfirmModal($window)
        } elseif ([InstallerWizard]::NextCaption($window) -match '^(Finish|Close|完成|关闭)') {
            [InstallerWizard]::Next($window)
        }
    }
    if (!$process.HasExited -or $process.ExitCode -ne 0) { throw '安装或卸载向导未成功退出。' }
}

function Open-FirstPage($process) {
    $window = Wait-Control $process { [InstallerWizard]::MainDialog($process.Id) } '主窗口'
    $null = Wait-Control $process {
        if ([InstallerWizard]::HasPage($window) -and [InstallerWizard]::NextCaption($window) -match '^(Next|Install|Uninstall|下一步|安装|卸载)') { $window }
    } '向导首页'
    [InstallerWizard]::Next($window)
    return $window
}

function Install-WithWizard([string]$expected, [string]$destination) {
    $process = Start-Process -FilePath $installer -WindowStyle Hidden -PassThru
    $window = Open-FirstPage $process
    $edit = Wait-Control $process { [InstallerWizard]::DirectoryEdit($window) } '安装目录输入框'
    $shown = [InstallerWizard]::Text($edit)
    if ($shown -ne $expected) { throw "目录预填应为 $expected，实际为 $shown" }
    [InstallerWizard]::SetDirectory($edit, $destination)
    if ([InstallerWizard]::Text($edit) -ne $destination) { throw '目录输入框未接受测试路径。' }
    Finish-Wizard $process $window
}

function Check-Installation([string]$directory) {
    $target = Join-Path $directory 'RocoPlayer.exe'
    if (!(Test-Path -LiteralPath $registry)) {
        $present = Test-Path -LiteralPath $target
        $probes = @(Get-ChildItem -LiteralPath $logs -File -Filter '*.jsonl').Count
        $otherViews = @('HKCU:\Software','HKLM:\Software\WOW6432Node') | Where-Object {
            Test-Path -LiteralPath (Join-Path $_ 'Microsoft/Windows/CurrentVersion/Uninstall/RocoPlayerWPF')
        }
        throw "缺少 64 位机器安装记录。程序存在=$present，预检测日志数=$probes，其他注册位置=$otherViews，目标=$directory"
    }
    $entry = Get-ItemProperty -LiteralPath $registry
    if ($entry.InstallLocation -ne $directory -or !(Test-Path -LiteralPath $target)) { throw "安装目录错误 $directory" }
    $shell = New-Object -ComObject Shell.Application
    foreach ($path in $desktopLink,$menuLink) {
        if (!(Test-Path -LiteralPath $path)) { throw "缺少快捷方式 $path" }
        $link = $shell.NameSpace((Split-Path -Parent $path)).ParseName((Split-Path -Leaf $path)).GetLink
        $actualTarget = $link.Path
        $actualDirectory = $link.WorkingDirectory
        Write-Output "快捷方式 $path → [$actualTarget]，起始位置 [$actualDirectory]"
        if ([InstallerWizard]::ResolvedPath($actualTarget) -ne [InstallerWizard]::ResolvedPath($target) -or
            [InstallerWizard]::ResolvedPath($actualDirectory) -ne [InstallerWizard]::ResolvedPath($directory)) {
            throw "快捷方式未指向此次安装的目录 [$directory]"
        }
    }
}

function Uninstall-WithWizard([string]$directory, [bool]$deleteData) {
    # Run the normal temporary uninstaller copy with an explicit install directory so we can track its window.
    $copy = Join-Path $scratch 'Uninstall-Test.exe'
    Copy-Item -LiteralPath (Join-Path $directory 'Uninstall.exe') -Destination $copy -Force
    $process = Start-Process -FilePath $copy -ArgumentList "_?=$directory" -WindowStyle Hidden -PassThru
    $window = Open-FirstPage $process
    $box = Wait-Control $process { [InstallerWizard]::Checkbox($window) } '清理数据复选框'
    if ([InstallerWizard]::IsChecked($box) -or ![InstallerWizard]::ShowsPath($window, $env:ROCK_MUSIC_DATA_DIR) -or
        ![InstallerWizard]::ShowsPath($window, $runtimeLogs)) {
        throw '默认选项或卸载向导显示的数据目录错误。'
    }
    if ($deleteData) { [InstallerWizard]::Check($box) }
    Finish-Wizard $process $window
    foreach ($path in $desktopLink,$menuLink,$registry,(Join-Path $directory 'RocoPlayer.exe')) {
        if (Test-Path -LiteralPath $path) { throw "卸载后仍存在 $path" }
    }
}

$scratch = (New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP ("Roco 安装测试 " + [guid]::NewGuid().ToString('N')))).FullName
$first = Join-Path $scratch '首次安装'
$second = Join-Path $scratch '移动后的安装'
$env:ROCK_MUSIC_DATA_DIR = (New-Item -ItemType Directory -Path (Join-Path $scratch '我的谱库')).FullName
$library = Join-Path $env:ROCK_MUSIC_DATA_DIR 'library.json'
$original = Join-Path $env:ROCK_MUSIC_DATA_DIR '手动导入.mid'
$null = New-Item -ItemType Directory -Path $runtimeLogs -Force
$runtimeFile = Join-Path $runtimeLogs 'dd-runtime.jsonl.2'
if (!(Test-Path -LiteralPath $runtimeFile)) { '{}' | Set-Content -LiteralPath $runtimeFile -Encoding UTF8 }
$outside = (New-Item -ItemType Directory -Path (Join-Path $scratch '其他录音')).FullName
$outsideSession = (New-Item -ItemType Directory -Path (Join-Path $outside '20261125-235959-123')).FullName
$recording = Join-Path $outsideSession 'B.wav'
'recording' | Set-Content -LiteralPath $recording -Encoding UTF8
$null = New-Item -ItemType Junction -Path (Join-Path $env:ROCK_MUSIC_DATA_DIR 'calibration') -Target $outside
'{"version":1,"items":[],"settings":{}}' | Set-Content -LiteralPath $library -Encoding UTF8
'original' | Set-Content -LiteralPath $original -Encoding UTF8
$null = New-Item -ItemType Directory -Path $logs -Force
$pending = Join-Path $logs 'dd-install-pending.json'
'{}' | Set-Content -LiteralPath $pending -Encoding UTF8
try {
    Write-Output '验证首次选择目录及两个快捷方式'
    Install-WithWizard (Join-Path $env:ProgramFiles 'RocoPlayer WPF') $first
    Check-Installation $first
    $personal = Join-Path $first '用户文件.txt'
    'user' | Set-Content -LiteralPath $personal -Encoding UTF8

    Write-Output '验证升级预填、改选目录与默认保留数据'
    Install-WithWizard $first $second
    Check-Installation $second
    if ((Test-Path -LiteralPath (Join-Path $first 'Uninstall.exe')) -or !(Test-Path -LiteralPath $personal)) { throw '旧安装目录清理错误。' }
    Uninstall-WithWizard $second $false
    if (!(Test-Path -LiteralPath $library) -or !(Test-Path -LiteralPath $runtimeFile)) { throw '默认卸载删除了用户数据。' }

    Write-Output '验证静默选址及勾选清理数据'
    $silent = Start-Process -FilePath $installer -ArgumentList "/S /D=$second" -WindowStyle Hidden -Wait -PassThru
    if ($silent.ExitCode -ne 0) { throw "静默安装失败 $($silent.ExitCode)" }
    Check-Installation $second
    Uninstall-WithWizard $second $true
    if ((Test-Path -LiteralPath $library) -or (Test-Path -LiteralPath $runtimeFile) -or
        !(Test-Path -LiteralPath $original) -or !(Test-Path -LiteralPath $recording)) {
        throw '勾选清理后的谱库、原件或链接目录状态错误。'
    }
    $events = Get-ChildItem -LiteralPath $logs -Filter '*.jsonl' -File | Get-Content -Encoding UTF8 | ConvertFrom-Json
    if ('vendor_started' -in $events.event) { throw '安装验证意外启动了驱动安装程序。' }
} finally {
    Remove-Item -LiteralPath $pending
}
