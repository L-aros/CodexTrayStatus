Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
SetDatablockOptimize on

!ifndef APP_SOURCE
  !define APP_SOURCE "..\artifacts\native\CodexTrayStatus.exe"
!endif
!ifndef APP_VERSION
  !define APP_VERSION "0.2.0"
!endif
!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "..\artifacts\installer\CodexTrayStatus-Setup.exe"
!endif

!define PRODUCT_NAME "Codex Tray Status"
!define PRODUCT_EXE "CodexTrayStatus.exe"
!define PRODUCT_KEY "Software\CodexTrayStatus"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexTrayStatus"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define RUN_VALUE "CodexTrayStatus"
!define LEGACY_RUN_VALUE "Codex Tray Status"
!define DOTNET_FULL_KEY "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"
!define DOTNET48_RELEASE 528040

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "Sections.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

Name "${PRODUCT_NAME}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\CodexTrayStatus"
InstallDirRegKey HKCU "${PRODUCT_KEY}" "InstallDir"
ShowInstDetails show
ShowUninstDetails show

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Run ${PRODUCT_NAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Section "${PRODUCT_NAME} (required)" SEC_MAIN
  SectionIn RO
  SetShellVarContext current
  SetRegView 32
  ; A running previous version locks the executable. Stop it before replacing it.
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /IM "${PRODUCT_EXE}" /F'
  Pop $0
  Pop $1
  SetOutPath "$INSTDIR"
  SetOverwrite on

  File "/oname=${PRODUCT_EXE}" "${APP_SOURCE}"
  File /nonfatal "/oname=${PRODUCT_EXE}.config" "${APP_SOURCE}.config"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall ${PRODUCT_NAME}.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "${PRODUCT_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Codex Tray Status contributors"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0

  ; Reinstalling with the startup component cleared disables the old entry.
  DeleteRegValue HKCU "${RUN_KEY}" "${RUN_VALUE}"
  ; Clean up the value name emitted by pre-0.2 installers during upgrades.
  DeleteRegValue HKCU "${RUN_KEY}" "${LEGACY_RUN_VALUE}"
SectionEnd

Section /o "Start automatically when I sign in" SEC_AUTOSTART
  WriteRegStr HKCU "${RUN_KEY}" "${RUN_VALUE}" '$\"$INSTDIR\${PRODUCT_EXE}$\" --startup'
SectionEnd

Function .onInit
  SetShellVarContext current

  ; On 64-bit Windows check the native view first, then always check the
  ; 32-bit view as well. Some managed-runtime installers populate only one.
  ${If} ${RunningX64}
    SetRegView 64
    ClearErrors
    ReadRegDWORD $0 HKLM "${DOTNET_FULL_KEY}" "Release"
    ${IfNot} ${Errors}
      IntCmpU $0 ${DOTNET48_RELEASE} dotnet_ok dotnet_check_32 dotnet_ok
    ${EndIf}
  ${EndIf}

dotnet_check_32:
  SetRegView 32
  ClearErrors
  ReadRegDWORD $0 HKLM "${DOTNET_FULL_KEY}" "Release"
  ${If} ${Errors}
    Goto dotnet_missing
  ${EndIf}
  IntCmpU $0 ${DOTNET48_RELEASE} dotnet_ok dotnet_missing dotnet_ok

dotnet_missing:
  IfSilent dotnet_missing_silent dotnet_missing_interactive
dotnet_missing_interactive:
  MessageBox MB_OK|MB_ICONSTOP "Codex Tray Status 需要 Microsoft .NET Framework 4.8。请先安装或启用 .NET Framework 4.8，然后重新运行本安装程序。"
  Goto dotnet_missing_exit
dotnet_missing_silent:
  DetailPrint "缺少 Microsoft .NET Framework 4.8，安装已取消。"
dotnet_missing_exit:
  SetErrorLevel 5100
  Abort

dotnet_ok:
  ; Application and uninstall registration intentionally use the 32-bit view
  ; on every platform, matching NSIS' default and preserving upgrade behavior.
  SetRegView 32
  ReadRegStr $0 HKCU "${RUN_KEY}" "${RUN_VALUE}"
  StrCmp $0 "" check_legacy_startup startup_selected
check_legacy_startup:
  ReadRegStr $0 HKCU "${RUN_KEY}" "${LEGACY_RUN_VALUE}"
  StrCmp $0 "" startup_done startup_selected
startup_selected:
  SectionGetFlags ${SEC_AUTOSTART} $1
  IntOp $1 $1 | ${SF_SELECTED}
  SectionSetFlags ${SEC_AUTOSTART} $1
startup_done:
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 32
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /IM "${PRODUCT_EXE}" /F'
  Pop $0
  Pop $1
  DeleteRegValue HKCU "${RUN_KEY}" "${RUN_VALUE}"
  DeleteRegValue HKCU "${RUN_KEY}" "${LEGACY_RUN_VALUE}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "${PRODUCT_KEY}"

  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall ${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"

  Delete "$INSTDIR\${PRODUCT_EXE}"
  Delete "$INSTDIR\${PRODUCT_EXE}.config"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
