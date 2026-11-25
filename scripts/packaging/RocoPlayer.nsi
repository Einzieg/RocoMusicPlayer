Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
!define APP_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\RocoPlayerWPF"
Name "RocoMusicPlayer"
OutFile "${OUTFILE}"
Icon "${ICON}"
SetCompressor /SOLID lzma
RequestExecutionLevel admin
InstallDir ""
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "RocoMusicPlayer"
VIAddVersionKey "FileDescription" "RocoMusicPlayer Windows x64 安装程序"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "RocoMusicPlayer contributors"
Var DDStatus
Var PreviousDir
Var PlayerDataDir
Var PlayerLogsDir
Var RemoveData
Var RemoveDataCheckbox
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.DataPage un.DataPageLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

LangString DataTitle ${LANG_SIMPCHINESE} "卸载选项"
LangString DataSubtitle ${LANG_SIMPCHINESE} "选择是否同时删除当前用户的播放器数据。"
LangString DataOption ${LANG_SIMPCHINESE} "同时删除我的曲谱和设置"
LangString DataDetails ${LANG_SIMPCHINESE} "勾选后将删除谱库、播放设置、在线下载的曲谱、键位校准录音和个人运行日志。保留此项为空，以便重装后继续使用。"
LangString DataLocation ${LANG_SIMPCHINESE} "数据目录"
LangString LogsLocation ${LANG_SIMPCHINESE} "个人运行日志目录"
LangString DataFailed ${LANG_SIMPCHINESE} "部分数据未能清理。请检查以下目录：$\r$\n$PlayerDataDir$\r$\n$PlayerLogsDir"
LangString DataTitle ${LANG_ENGLISH} "Uninstall options"
LangString DataSubtitle ${LANG_ENGLISH} "Choose what to do with your player data."
LangString DataOption ${LANG_ENGLISH} "Also delete my scores and settings"
LangString DataDetails ${LANG_ENGLISH} "Select to delete your library, playback settings, downloaded scores, calibration recordings and runtime logs. Leave this unchecked to keep your data for a reinstall."
LangString DataLocation ${LANG_ENGLISH} "Data folder"
LangString LogsLocation ${LANG_ENGLISH} "Personal runtime log folder"
LangString DataFailed ${LANG_ENGLISH} "Some data could not be removed. Check these folders:$\r$\n$PlayerDataDir$\r$\n$PlayerLogsDir"

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
  ReadRegStr $PreviousDir HKLM "${APP_REG_KEY}" "InstallLocation"
  ${If} $INSTDIR == ""
    StrCpy $INSTDIR "$PROGRAMFILES64\RocoPlayer WPF"
    ${If} $PreviousDir != ""
      StrCpy $INSTDIR $PreviousDir
    ${EndIf}
  ${EndIf}
  !insertmacro RequireClosed
FunctionEnd
Function un.onInit
  !insertmacro RequireClosed
  StrCpy $RemoveData ${BST_UNCHECKED}
  SetShellVarContext current
  StrCpy $PlayerLogsDir "$LOCALAPPDATA\RocoPlayer\logs"
  ReadEnvStr $PlayerDataDir "ROCK_MUSIC_DATA_DIR"
  ${If} $PlayerDataDir == ""
    StrCpy $PlayerDataDir "$APPDATA\com.rockmusic.workspace-player"
  ${Else}
    SetOutPath "$INSTDIR"
    GetFullPathName $PlayerDataDir "$PlayerDataDir"
  ${EndIf}
FunctionEnd

Function un.DataPage
  !insertmacro MUI_HEADER_TEXT "$(DataTitle)" "$(DataSubtitle)"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateCheckbox} 0 5u 100% 14u "$(DataOption)"
  Pop $RemoveDataCheckbox
  ${NSD_SetState} $RemoveDataCheckbox $RemoveData
  ${NSD_CreateLabel} 0 26u 100% 34u "$(DataDetails)"
  Pop $0
  ${NSD_CreateLabel} 0 59u 100% 11u "$(DataLocation)"
  Pop $0
  ${NSD_CreateLabel} 0 72u 100% 24u "$PlayerDataDir"
  Pop $0
  ${NSD_CreateLabel} 0 101u 100% 11u "$(LogsLocation)"
  Pop $0
  ${NSD_CreateLabel} 0 114u 100% 24u "$PlayerLogsDir"
  Pop $0
  nsDialogs::Show
FunctionEnd

Function un.DataPageLeave
  ${NSD_GetState} $RemoveDataCheckbox $RemoveData
FunctionEnd

!macro SelectContext
  SetRegView 64
  SetShellVarContext all
!macroend

; Only descend into real package directories. A junction substituted for a subfolder belongs to its target.
!macro IfPackageDirectory directory
  System::Call 'kernel32::GetFileAttributesW(w "${directory}") i.r6'
  IntOp $6 $6 & 0x410 ; FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT
  ${If} $6 = 0x10
!macroend
!macro RemoveProgramFiles
  !insertmacro IfPackageDirectory "$INSTDIR"
    !include "${REMOVE_LIST}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
  ${EndIf}
!macroend

; Resolve aliases before retiring a previous directory. If its identity is unavailable, keep its files.
!macro PhysicalDirectory directory result
  StrCpy ${result} ""
  System::Call 'kernel32::CreateFileW(w "${directory}", i 0, i 7, p 0, i 3, i 0x02000000, p 0) p.r8'
  ${If} $8 P<> -1
    System::Call 'kernel32::GetFinalPathNameByHandleW(p r8, w .r9, i ${NSIS_MAX_STRLEN}, i 1) i.r7'
    System::Call 'kernel32::CloseHandle(p r8)'
    ${If} $7 > 0
    ${AndIf} $7 < ${NSIS_MAX_STRLEN}
      StrCpy ${result} "$9\"
    ${EndIf}
  ${EndIf}
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
  CreateShortCut "$DESKTOP\RocoMusicPlayer.lnk" "$INSTDIR\RocoPlayer.exe" "" "$INSTDIR\RocoPlayer.exe" 0
  CreateShortCut "$SMPROGRAMS\RocoPlayer WPF.lnk" "$INSTDIR\RocoPlayer.exe"
  WriteRegStr SHCTX "${APP_REG_KEY}" "DisplayName" "RocoMusicPlayer"
  WriteRegStr SHCTX "${APP_REG_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr SHCTX "${APP_REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr SHCTX "${APP_REG_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr SHCTX "${APP_REG_KEY}" "DisplayIcon" "$INSTDIR\RocoPlayer.exe"
  ${If} $DDStatus == 3
    SetOutPath "$PLUGINSDIR\dd-driver"
    File /r "${STAGE}\drivers\dd\drv\*"
    nsExec::ExecToLog /TIMEOUT=180000 '"$PLUGINSDIR\dd-check\RocoPlayer.DDSetup.exe" --driver-dir "$OUTDIR" --log-dir "$APPDATA\RocoPlayer\logs"'
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
  ; When the user switches folders, retire the registered copy using only the package manifest.
  ${If} $PreviousDir != ""
  ${AndIf} ${FileExists} "$PreviousDir\RocoPlayer.exe"
  ${AndIf} ${FileExists} "$PreviousDir\Uninstall.exe"
    !insertmacro PhysicalDirectory "$PreviousDir" $0
    !insertmacro PhysicalDirectory "$INSTDIR" $1
    ${If} $0 != ""
    ${AndIf} $1 != ""
    ${AndIf} $0 != $1
      StrLen $2 $0
      StrCpy $3 $1 $2
      StrLen $2 $1
      StrCpy $4 $0 $2
      ${If} $3 != $0
      ${AndIf} $4 != $1
        Push $INSTDIR
        StrCpy $INSTDIR "$PreviousDir"
        !insertmacro RemoveProgramFiles
        Pop $INSTDIR
      ${Else}
        ; Nested installations can share payload paths. Retire only the old uninstall entry point.
        Delete "$PreviousDir\Uninstall.exe"
      ${EndIf}
    ${EndIf}
  ${EndIf}
SectionEnd

Section "Uninstall"
  !insertmacro SelectContext
  ${If} $RemoveData == ${BST_CHECKED}
    InitPluginsDir
    SetOutPath "$PLUGINSDIR\cleanup"
    File /r /x "*.pdb" "${CHECK}\*"
    nsExec::ExecToLog '"$OUTDIR\RocoPlayer.DDSetup.exe" --remove-player-data "$PlayerDataDir" "$PlayerLogsDir"'
    Pop $0
    ${If} $0 != 0
      MessageBox MB_OK|MB_ICONEXCLAMATION "$(DataFailed)" /SD IDOK
      SetErrorLevel 2
    ${EndIf}
  ${EndIf}
  ReadRegStr $0 SHCTX "${APP_REG_KEY}" "InstallLocation"
  GetFullPathName /SHORT $0 "$0"
  GetFullPathName /SHORT $1 "$INSTDIR"
  ${If} $0 == $1
    Delete "$DESKTOP\RocoMusicPlayer.lnk"
    Delete "$SMPROGRAMS\RocoPlayer WPF.lnk"
    DeleteRegKey SHCTX "${APP_REG_KEY}"
  ${EndIf}
  SetOutPath "$TEMP"
  !insertmacro RemoveProgramFiles
SectionEnd
