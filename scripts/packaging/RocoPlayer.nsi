Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
Name "RocoMusicPlayer"
OutFile "${OUTFILE}"
Icon "${ICON}"
SetCompressor /SOLID lzma
RequestExecutionLevel admin
InstallDir "$PROGRAMFILES64\RocoPlayer WPF"
InstallDirRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF" "InstallLocation"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "RocoMusicPlayer"
VIAddVersionKey "FileDescription" "RocoMusicPlayer Windows x64 安装程序"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "RocoMusicPlayer contributors"
Var DDStatus
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

!macro RequireClosed
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\RocoPlayer.CSharp") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先关闭 RocoPlayer 后重试。" /SD IDOK
    Abort
  ${EndIf}
!macroend

Function .onInit
  SetRegView 64
  !insertmacro RequireClosed
FunctionEnd
Function un.onInit
  !insertmacro RequireClosed
FunctionEnd

!macro SelectContext
  SetRegView 64
  SetShellVarContext all
!macroend

Section "RocoPlayer"
  !insertmacro SelectContext
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\dd-check"
  File /r /x "*.pdb" "${CHECK}\*"
  DetailPrint "正在检查 DD 63340 驱动、服务和设备…"
  nsExec::ExecToLog /TIMEOUT=60000 '"$PLUGINSDIR\dd-check\RocoPlayer.DDSetup.exe" --check-install --driver-dir "$INSTDIR\drivers\dd\drv" --log-dir "$APPDATA\RocoPlayer\logs"'
  Pop $DDStatus
  ${If} $DDStatus == 0
    DetailPrint "已检测到 DD 驱动，跳过驱动安装。"
  ${ElseIf} $DDStatus == 3
    DetailPrint "未检测到 DD 驱动，将安装随附的 63340 驱动。"
  ${Else}
    DetailPrint "DD 状态需要检查，已跳过驱动安装。"
  ${EndIf}
  SetOutPath "$INSTDIR"
  File /r "${STAGE}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortCut "$SMPROGRAMS\RocoPlayer WPF.lnk" "$INSTDIR\RocoPlayer.exe"
  WriteRegStr SHCTX "Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF" "DisplayName" "RocoMusicPlayer"
  WriteRegStr SHCTX "Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF" "DisplayVersion" "${VERSION}"
  WriteRegStr SHCTX "Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF" "InstallLocation" "$INSTDIR"
  WriteRegStr SHCTX "Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr SHCTX "Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF" "DisplayIcon" "$INSTDIR\RocoPlayer.exe"
  ${If} $DDStatus == 3
    nsExec::ExecToLog /TIMEOUT=180000 '"$INSTDIR\RocoPlayer.DDSetup.exe" --driver-dir "$INSTDIR\drivers\dd\drv" --log-dir "$APPDATA\RocoPlayer\logs"'
    Pop $0
    ${If} $0 == 3010
    ${OrIf} $0 == "timeout"
      SetRebootFlag true
      MessageBox MB_OK "请保存工作并重新启动 Windows，随后在播放器中检查 DD 诊断。" /SD IDOK
    ${ElseIf} $0 != 0
      MessageBox MB_OK|MB_ICONEXCLAMATION "DD 安装未完成，请查看 $APPDATA\RocoPlayer\logs。" /SD IDOK
    ${EndIf}
  ${ElseIf} $DDStatus != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "DD 状态异常或有待处理的安装，已跳过驱动安装。可在应用设置中查看 DD 诊断。日志位于 $APPDATA\RocoPlayer\logs。" /SD IDOK
  ${EndIf}
SectionEnd

Section "Uninstall"
  !insertmacro SelectContext
  Delete "$SMPROGRAMS\RocoPlayer WPF.lnk"
  DeleteRegKey SHCTX "Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF"
  !include "${REMOVE_LIST}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
