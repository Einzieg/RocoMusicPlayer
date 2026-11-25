# 构建 Windows 安装包

RocoMusicPlayer 的 Windows x64 安装包内含 WPF 播放器、.NET 桌面运行时、Basic Pitch 模型、DD 63340 用户态 DLL、驱动安装助手和厂商驱动。可从正式版页面一起下载安装包及校验文件。[GitHub Releases](https://github.com/Einzieg/RocoMusicPlayer/releases)

## 本地构建

在 Windows x64 准备 PowerShell 7、.NET 8 SDK、NSIS 3、7-Zip 和 Microsoft Visual C++ x64 运行库。从仓库根目录运行以下命令。

```powershell
$ErrorActionPreference = 'Stop'
./scripts/Test-RocoPlayer.ps1
./scripts/Build-Installer.ps1 -Version '1.0.1'
```

输出位于 `artifacts/RocoMusicPlayer-win-x64-Setup.exe` 和 `artifacts/SHA256SUMS.txt`。默认构建下载厂商提交 `34a1608` 的 63340 压缩包，先核对整个压缩包的 SHA-256，再提取 `2.hid` 中的五个文件并逐一校验。DLL、EXE、CAT 的 Authenticode 签名以及配套的三份 Microsoft VC++ x64 运行库也在构建时检查。生成安装包后再将封装的驱动、运行库、检测助手和主程序与构建输出逐一比对。[厂商版本](https://github.com/ddxoft/master/tree/34a1608ac3754674eb4d2487dc8859b1523bd290) · [组件哈希](../scripts/packaging/dd-package-hashes.json)

已有这套厂商文件时，可用 `-DDPackagePath` 指定包含 `ddhid.63340.dll` 和 `drv/` 的目录。单独运行 `./scripts/Build-RocoPlayer.ps1` 会生成自包含目录 `artifacts/RocoPlayer/`，复制整个目录即可运行。

## 安装前的驱动检查

用户确认安装后，安装程序先从临时目录运行 DD 检测助手，再写入播放器目录。检查读取 Windows 服务 `ddhid63340` 和关联的 PnP 设备。

| 状态 | 结果 |
| --- | --- |
| 服务运行且设备就绪 | 跳过驱动安装。 |
| 没有对应服务或设备 | 从安装程序的临时目录启动随附的 DD 助手。助手加锁并再次确认状态，调用厂商安装程序一次，然后核对服务与设备。 |
| 已有异常服务或设备、检测失败、存在上次安装的待处理标记 | 跳过驱动安装，提示查看诊断。 |
| 厂商安装程序要求重启或运行超时 | 提示重启。超时会留下 `dd-install-pending.json`，用于防止重复启动厂商安装程序。 |

首次安装的默认目录为 `%ProgramFiles%\RocoPlayer WPF`，可在「选择安装位置」页更改。升级时会预填已注册的安装目录。安装后，公共桌面的「RocoMusicPlayer」和开始菜单的「RocoPlayer WPF」均指向所选目录中的播放器。驱动安装日志位于 `%ProgramData%\RocoPlayer\logs`。

卸载向导默认保留用户数据。勾选「同时删除我的曲谱和设置」会清理所示目录中的谱库、播放设置、在线下载缓存、键位校准录音和个人运行日志。默认谱库目录为 `%APPDATA%\com.rockmusic.workspace-player`，设置了 `ROCK_MUSIC_DATA_DIR` 时使用自定义目录。手动导入的原文件、用户备份和系统共享的 DD 驱动均会保留。静默卸载也会保留用户数据。

### DD 诊断

在「应用设置 → DD 只读诊断」查看 DLL、服务、PnP 设备与待处理标记。首次使用 DD 时，播放器会在初始化和键位映射期间临时保护左右 Win 键，并为延迟到达的按键事件留出时间。运行日志位于 `%LOCALAPPDATA%\RocoPlayer\logs\dd-runtime.jsonl`。

也可在安装目录用 PowerShell 查看状态。`--inspect-only` 在服务及设备就绪时返回 0，其他情况返回 2。预安装使用的 `--check-install` 在确认缺少驱动时返回 3。

```powershell
$ErrorActionPreference = 'Stop'
$logs = Join-Path $env:TEMP 'RocoMusicPlayer-DD-inspection'
./RocoPlayer.DDSetup.exe --inspect-only --driver-dir ./drivers/dd/drv --log-dir $logs
```

## GitHub Actions 发版

推送到 `main`、提交拉取请求或手动运行工作流，均会在 Windows 上执行回归测试，构建完整安装包，并把安装包与 SHA-256 文件上传到该次运行的 Artifacts。正式版本使用 `v主版本.次版本.补丁版本` 标签。标签工作流通过相同检查后，将同一份经过验证的产物上传至 GitHub Release。[Windows 构建与发版工作流](../.github/workflows/release.yml)

维护者可在 `docs/releases/<标签>.md` 编写面向用户的更新说明。省略该文件时，GitHub 会生成发布说明。公开发布前，工作流会重新检查下载产物的 SHA-256。
