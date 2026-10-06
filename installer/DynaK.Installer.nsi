Unicode true
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetDateSave on

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "StrFunc.nsh"
!include "x64.nsh"
${Using:StrFunc} StrStr

!ifndef PRODUCT_VERSION
  !error "PRODUCT_VERSION must be defined."
!endif
!ifndef PRODUCT_VERSION_FILE
  !error "PRODUCT_VERSION_FILE must be defined."
!endif
!ifndef DESKTOP_PUBLISH_DIR
  !error "DESKTOP_PUBLISH_DIR must be defined."
!endif
!ifndef SERVICE_PUBLISH_DIR
  !error "SERVICE_PUBLISH_DIR must be defined."
!endif
!ifndef SETUP_OUTPUT_PATH
  !error "SETUP_OUTPUT_PATH must be defined."
!endif
!ifndef APP_ICON
  !error "APP_ICON must be defined."
!endif

!define PRODUCT_NAME "DynaK Wet Leak Test Station"
!define PRODUCT_PUBLISHER "DynaK"
!define PRODUCT_REG_KEY "Software\DynaK\Wet Leak Test Station"
!define UNINSTALL_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynaK Wet Leak Test Station"
!define INSTALL_DIR "$PROGRAMFILES64\DynaK\Wet Leak Test Station"
!define DESKTOP_EXE "DynaK Wet Leak Test Station.exe"
!define SERVICE_EXE "DynaK.Service.exe"
!define SERVICE_NAME "DynaK.WetLeakTest.Acquisition"
!define SERVICE_DISPLAY_NAME "DynaK Wet Leak Test Acquisition"
!define SERVICE_DESCRIPTION "Local DynaK wet leak test PLC acquisition, API, and SQLite service. Starts in STATION STOPPED state."
!define WEBVIEW2_CLIENT_KEY "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

Name "${PRODUCT_NAME}"
OutFile "${SETUP_OUTPUT_PATH}"
InstallDir "${INSTALL_DIR}"
InstallDirRegKey HKLM "${PRODUCT_REG_KEY}" "InstallLocation"
Icon "${APP_ICON}"
UninstallIcon "${APP_ICON}"

VIProductVersion "${PRODUCT_VERSION_FILE}"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "(c) ${PRODUCT_PUBLISHER}"

!define MUI_ABORTWARNING
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${PRODUCT_NAME}"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApplication
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Var InstallerLogHandle
Var InstallerLogPath
Var MachineDataRoot
Var ReportRoot
Var ServiceBinaryPath
Var ServiceCommand
Var CommandExitCode
Var CommandOutput
Var ServiceFailureMessage

!macro CreateReportYear YEAR
  CreateDirectory "$ReportRoot\${YEAR}\01 - January"
  CreateDirectory "$ReportRoot\${YEAR}\02 - February"
  CreateDirectory "$ReportRoot\${YEAR}\03 - March"
  CreateDirectory "$ReportRoot\${YEAR}\04 - April"
  CreateDirectory "$ReportRoot\${YEAR}\05 - May"
  CreateDirectory "$ReportRoot\${YEAR}\06 - June"
  CreateDirectory "$ReportRoot\${YEAR}\07 - July"
  CreateDirectory "$ReportRoot\${YEAR}\08 - August"
  CreateDirectory "$ReportRoot\${YEAR}\09 - September"
  CreateDirectory "$ReportRoot\${YEAR}\10 - October"
  CreateDirectory "$ReportRoot\${YEAR}\11 - November"
  CreateDirectory "$ReportRoot\${YEAR}\12 - December"
!macroend

Function LogLine
  Exch $R0
  DetailPrint "$R0"
  ${If} $InstallerLogHandle != ""
    FileWrite $InstallerLogHandle "$R0$\r$\n"
  ${EndIf}
  Pop $R0
FunctionEnd

Function RunLoggedCommand
  Push "COMMAND: $ServiceCommand"
  Call LogLine
  ClearErrors
  StrCpy $CommandExitCode -1
  ExecWait '$ServiceCommand' $CommandExitCode
  Push "EXIT CODE: $CommandExitCode"
  Call LogLine
FunctionEnd

Function LogServiceState
  Push "SERVICE BINARY PATH: $ServiceBinaryPath"
  Call LogLine
  StrCpy $ServiceCommand '"$SYSDIR\sc.exe" query "${SERVICE_NAME}"'
  Call RunLoggedCommand
  StrCpy $ServiceCommand '"$SYSDIR\sc.exe" qc "${SERVICE_NAME}"'
  Call RunLoggedCommand
FunctionEnd

Function ProbeBackendHealth
  StrCpy $ServiceCommand '"$SYSDIR\curl.exe" --fail --silent --show-error --max-time 2 "http://127.0.0.1:5055/api/health"'
  Push "HEALTH COMMAND: $ServiceCommand"
  Call LogLine
  ClearErrors
  StrCpy $CommandExitCode -1
  StrCpy $CommandOutput ""
  nsExec::ExecToStack /TIMEOUT=5000 '$ServiceCommand'
  Pop $CommandExitCode
  Pop $CommandOutput
  Push "HEALTH EXIT CODE: $CommandExitCode"
  Call LogLine
  ${If} $CommandOutput != ""
    Push "HEALTH OUTPUT: $CommandOutput"
    Call LogLine
  ${EndIf}

  ${If} $CommandExitCode != 0
    Push 0
    Return
  ${EndIf}
  ${StrStr} $0 $CommandOutput '$\"application$\":$\"DynaK.WetLeakTest.Service$\"'
  ${If} $0 == ""
    Push 0
    Return
  ${EndIf}
  ${StrStr} $0 $CommandOutput '$\"ready$\":true'
  ${If} $0 == ""
    Push 0
    Return
  ${EndIf}
  Push 1
FunctionEnd

Function AbortWithServiceLog
  Pop $ServiceFailureMessage
  Push "FAILURE: $ServiceFailureMessage"
  Call LogLine
  Call LogServiceState
  MessageBox MB_ICONSTOP "$ServiceFailureMessage$\r$\nSee:$\r$\n$InstallerLogPath"
  Abort
FunctionEnd

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${PRODUCT_NAME} requires 64-bit Windows."
    Abort
  ${EndIf}

  ReadEnvStr $MachineDataRoot "ProgramData"
  ${If} $MachineDataRoot == ""
    MessageBox MB_ICONSTOP "Windows did not provide the ProgramData directory."
    Abort
  ${EndIf}
  StrCpy $MachineDataRoot "$MachineDataRoot\DynaK\Wet Leak Test Station"
  StrCpy $InstallerLogPath "$TEMP\DynaK-Installer.log"
  FileOpen $InstallerLogHandle "$InstallerLogPath" w
  Push "${PRODUCT_NAME} ${PRODUCT_VERSION} installer started."
  Call LogLine
  Push "INSTALL DIR: $INSTDIR"
  Call LogLine

  SetRegView 64
  Call AssertNoLegacyMsi
FunctionEnd

Function .onGUIEnd
  ${If} $InstallerLogHandle != ""
    FileClose $InstallerLogHandle
  ${EndIf}
FunctionEnd

Function AssertNoLegacyMsi
  StrCpy $0 0
legacy_loop:
  EnumRegKey $1 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall" $0
  StrCmp $1 "" legacy_done
  ReadRegStr $2 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$1" "DisplayName"
  StrCmp $2 "${PRODUCT_NAME}" 0 legacy_next
  ReadRegStr $3 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$1" "WindowsInstaller"
  StrCmp $3 "1" legacy_found legacy_next
legacy_next:
  IntOp $0 $0 + 1
  Goto legacy_loop
legacy_found:
  MessageBox MB_ICONEXCLAMATION "A legacy MSI installation of ${PRODUCT_NAME} is registered on this PC.$\r$\n$\r$\nUninstall that version before installing this direct EXE release. The new installer will not invoke Windows Installer."
  Abort
legacy_done:
FunctionEnd

Function HasWebView2Runtime
  Push $0
  SetRegView 32
  ReadRegStr $0 HKLM "${WEBVIEW2_CLIENT_KEY}" "pv"
  ${If} $0 == ""
    ReadRegStr $0 HKCU "${WEBVIEW2_CLIENT_KEY}" "pv"
  ${EndIf}
  ${If} $0 == ""
    StrCpy $0 "0"
  ${ElseIf} $0 == "0.0.0.0"
    StrCpy $0 "0"
  ${Else}
    StrCpy $0 "1"
  ${EndIf}
  SetRegView 64
  Exch $0
FunctionEnd

Function EnsureWebView2Runtime
  Call HasWebView2Runtime
  Pop $0
  ${If} $0 == "1"
    Return
  ${EndIf}

!ifdef WEBVIEW2_INSTALLER
  SetOutPath "$INSTDIR\prerequisites"
  File /oname=MicrosoftEdgeWebView2RuntimeInstallerX64.exe "${WEBVIEW2_INSTALLER}"
  StrCpy $ServiceCommand '"$INSTDIR\prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe" /silent /install'
  Call RunLoggedCommand
  StrCpy $0 $CommandExitCode
  Delete "$INSTDIR\prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
  RMDir "$INSTDIR\prerequisites"
  ${If} $0 != 0
  ${AndIf} $0 != 3010
    MessageBox MB_ICONSTOP "Microsoft Edge WebView2 Runtime installation failed (exit code $0)."
    Abort
  ${EndIf}
  Call HasWebView2Runtime
  Pop $0
  ${If} $0 == "1"
    Return
  ${EndIf}
  MessageBox MB_ICONSTOP "Microsoft Edge WebView2 Runtime is still unavailable after installation."
  Abort
!else
  MessageBox MB_ICONSTOP "Microsoft Edge WebView2 Runtime is required but is not installed.$\r$\n$\r$\nInstall the x64 Evergreen WebView2 Runtime, then run this installer again."
  Abort
!endif
FunctionEnd

Function StopExistingService
  StrCpy $ServiceBinaryPath "$INSTDIR\service\${SERVICE_EXE}"
  StrCpy $ServiceCommand '"$SYSDIR\sc.exe" query "${SERVICE_NAME}"'
  Call RunLoggedCommand
  ${If} $CommandExitCode != 0
    Return
  ${EndIf}

  StrCpy $ServiceCommand '"$SYSDIR\sc.exe" stop "${SERVICE_NAME}"'
  Call RunLoggedCommand
  StrCpy $0 0
wait_service_stop:
  StrCpy $ServiceCommand '"$SYSDIR\cmd.exe" /C ""$SYSDIR\sc.exe" query "${SERVICE_NAME}" | "$SYSDIR\find.exe" "STOPPED""'
  Call RunLoggedCommand
  ${If} $CommandExitCode = 0
    Return
  ${EndIf}
  IntOp $0 $0 + 1
  ${If} $0 >= 30
    Push "The existing DynaK acquisition service did not stop within 30 seconds. Close the service and run setup again."
    Call AbortWithServiceLog
  ${EndIf}
  Sleep 1000
  Goto wait_service_stop
FunctionEnd

Function ConfigureService
  StrCpy $ServiceBinaryPath "$INSTDIR\service\${SERVICE_EXE}"
  Push "EXPECTED SERVICE EXE: $ServiceBinaryPath"
  Call LogLine
  IfFileExists "$ServiceBinaryPath" service_exe_exists
    Push "The acquisition service executable was not found at $ServiceBinaryPath."
    Call AbortWithServiceLog
service_exe_exists:

  StrCpy $ServiceCommand '"$SYSDIR\sc.exe" query "${SERVICE_NAME}"'
  Call RunLoggedCommand
  ${If} $CommandExitCode = 0
    StrCpy $ServiceCommand '"$SYSDIR\sc.exe" config "${SERVICE_NAME}" binPath= "$ServiceBinaryPath" start= auto obj= LocalSystem DisplayName= "${SERVICE_DISPLAY_NAME}"'
  ${Else}
    StrCpy $ServiceCommand '"$SYSDIR\sc.exe" create "${SERVICE_NAME}" binPath= "$ServiceBinaryPath" start= auto obj= LocalSystem DisplayName= "${SERVICE_DISPLAY_NAME}"'
  ${EndIf}
  Call RunLoggedCommand
  ${If} $CommandExitCode != 0
    Push "Acquisition service registration failed."
    Call AbortWithServiceLog
  ${EndIf}

  StrCpy $ServiceCommand '"$SYSDIR\sc.exe" description "${SERVICE_NAME}" "${SERVICE_DESCRIPTION}"'
  Call RunLoggedCommand
  ${If} $CommandExitCode != 0
    Push "DynaK acquisition service description update failed."
    Call AbortWithServiceLog
  ${EndIf}

  StrCpy $ServiceCommand '"$SYSDIR\sc.exe" start "${SERVICE_NAME}"'
  Call RunLoggedCommand
  ${If} $CommandExitCode != 0
  ${AndIf} $CommandExitCode != 1056
    Push "DynaK acquisition service start failed."
    Call AbortWithServiceLog
  ${EndIf}

  StrCpy $0 0
wait_service_running:
  StrCpy $ServiceCommand '"$SYSDIR\cmd.exe" /C ""$SYSDIR\sc.exe" query "${SERVICE_NAME}" | "$SYSDIR\find.exe" "RUNNING""'
  Call RunLoggedCommand
  ${If} $CommandExitCode = 0
    Goto wait_backend_health
  ${EndIf}
  IntOp $0 $0 + 1
  ${If} $0 >= 30
    Push "The DynaK acquisition service did not reach RUNNING state within 30 seconds."
    Call AbortWithServiceLog
  ${EndIf}
  Sleep 1000
  Goto wait_service_running

wait_backend_health:
  StrCpy $0 0
wait_backend_health_retry:
  Call ProbeBackendHealth
  Pop $1
  ${If} $1 == 1
    Push "BACKEND HEALTH CONFIRMED: http://127.0.0.1:5055/api/health"
    Call LogLine
    Return
  ${EndIf}

  StrCpy $ServiceCommand '"$SYSDIR\cmd.exe" /C ""$SYSDIR\sc.exe" query "${SERVICE_NAME}" | "$SYSDIR\find.exe" "RUNNING""'
  Call RunLoggedCommand
  ${If} $CommandExitCode != 0
    Push "The DynaK service process stopped before SQLite/backend initialization completed. Review $MachineDataRoot\logs\dynak-service.log and the Windows Application and CodeIntegrity logs."
    Call AbortWithServiceLog
  ${EndIf}

  IntOp $0 $0 + 1
  ${If} $0 >= 60
    Push "The DynaK service reached RUNNING state, but the backend health endpoint did not confirm SQLite initialization within 60 seconds. Review $MachineDataRoot\logs\dynak-service.log and the Windows Application and CodeIntegrity logs."
    Call AbortWithServiceLog
  ${EndIf}
  Sleep 1000
  Goto wait_backend_health_retry
FunctionEnd

Section "${PRODUCT_NAME}" SecMain
  Call EnsureWebView2Runtime
  Call StopExistingService

  ; The backend directory owns the service and all HMI assets. Recreate it so
  ; an upgrade cannot combine new binaries with stale renderer files.
  RMDir /r "$INSTDIR\service"
  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "${DESKTOP_PUBLISH_DIR}\*.*"
  SetOutPath "$INSTDIR\service"
  File /r /x "*.pdb" "${SERVICE_PUBLISH_DIR}\*.*"

  Push "Creating machine-data directory: $MachineDataRoot"
  Call LogLine
  ClearErrors
  CreateDirectory "$MachineDataRoot"
  ${If} ${Errors}
    Push "FAILURE: Unable to create the DynaK machine-data directory."
    Call LogLine
    MessageBox MB_ICONSTOP "Unable to create the DynaK machine-data directory:$\r$\n$MachineDataRoot$\r$\nSee:$\r$\n$InstallerLogPath"
    Abort
  ${EndIf}

  Push "Applying machine-data permissions."
  Call LogLine
  StrCpy $ServiceCommand '"$SYSDIR\icacls.exe" "$MachineDataRoot" /grant "*S-1-5-32-545:(OI)(CI)M" /T /C'
  Call RunLoggedCommand
  ${If} $CommandExitCode != 0
    Push "FAILURE: Unable to configure DynaK machine-data permissions."
    Call LogLine
    MessageBox MB_ICONSTOP "Unable to configure DynaK machine-data permissions.$\r$\nCommand: $ServiceCommand$\r$\nExit code: $CommandExitCode$\r$\nSee:$\r$\n$InstallerLogPath"
    Abort
  ${EndIf}
  Push "Machine-data permissions configured successfully."
  Call LogLine

  SetShellVarContext all
  StrCpy $ReportRoot "$DOCUMENTS\DynaK Leak Test Report"
  Push "Creating report directory tree without replacing existing reports: $ReportRoot"
  Call LogLine
  ClearErrors
  !insertmacro CreateReportYear 2026
  !insertmacro CreateReportYear 2027
  !insertmacro CreateReportYear 2028
  !insertmacro CreateReportYear 2029
  ${If} ${Errors}
    Push "FAILURE: Unable to create the DynaK report directory tree."
    Call LogLine
    MessageBox MB_ICONSTOP "Unable to create the DynaK report directory tree:$\r$\n$ReportRoot$\r$\nSee:$\r$\n$InstallerLogPath"
    Abort
  ${EndIf}
  StrCpy $ServiceCommand '"$SYSDIR\icacls.exe" "$ReportRoot" /grant "*S-1-5-32-545:(OI)(CI)M" /T /C'
  Call RunLoggedCommand
  ${If} $CommandExitCode != 0
    Push "FAILURE: Unable to configure DynaK report-directory permissions."
    Call LogLine
    MessageBox MB_ICONSTOP "Unable to configure DynaK report-directory permissions.$\r$\nCommand: $ServiceCommand$\r$\nExit code: $CommandExitCode$\r$\nSee:$\r$\n$InstallerLogPath"
    Abort
  ${EndIf}

  CreateDirectory "$SMPROGRAMS\DynaK"
  CreateShortcut "$SMPROGRAMS\DynaK\${PRODUCT_NAME}.lnk" "$INSTDIR\${DESKTOP_EXE}" "" "$INSTDIR\${DESKTOP_EXE}" 0
  CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${DESKTOP_EXE}" "" "$INSTDIR\${DESKTOP_EXE}" 0

  SetRegView 64
  WriteRegStr HKLM "${PRODUCT_REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${PRODUCT_REG_KEY}" "MachineDataRoot" "$MachineDataRoot"
  WriteRegStr HKLM "${PRODUCT_REG_KEY}" "ReportRoot" "$ReportRoot"
  WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "DisplayIcon" "$INSTDIR\${DESKTOP_EXE},0"
  WriteRegStr HKLM "${UNINSTALL_REG_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegDWORD HKLM "${UNINSTALL_REG_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_REG_KEY}" "NoRepair" 1
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  Call ConfigureService
SectionEnd

Function LaunchApplication
  IfFileExists "$INSTDIR\${DESKTOP_EXE}" 0 done
  Exec '"$INSTDIR\${DESKTOP_EXE}"'
done:
FunctionEnd

Function un.StopAndRemoveService
  ClearErrors
  StrCpy $0 -1
  ExecWait '"$SYSDIR\sc.exe" query "${SERVICE_NAME}"' $0
  ${If} $0 != 0
    Return
  ${EndIf}

  ClearErrors
  StrCpy $0 -1
  ExecWait '"$SYSDIR\sc.exe" stop "${SERVICE_NAME}"' $0
  StrCpy $0 0
un_wait_service_stop:
  ClearErrors
  StrCpy $1 -1
  ExecWait '"$SYSDIR\cmd.exe" /C ""$SYSDIR\sc.exe" query "${SERVICE_NAME}" | "$SYSDIR\find.exe" "STOPPED""' $1
  ${If} $1 = 0
    Goto un_delete_service
  ${EndIf}
  IntOp $0 $0 + 1
  ${If} $0 >= 30
    MessageBox MB_ICONSTOP "The DynaK acquisition service did not stop within 30 seconds. Close the service and run uninstall again."
    Abort
  ${EndIf}
  Sleep 1000
  Goto un_wait_service_stop
un_delete_service:
  ClearErrors
  StrCpy $0 -1
  ExecWait '"$SYSDIR\sc.exe" delete "${SERVICE_NAME}"' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "DynaK acquisition service removal failed (exit code $0)."
    Abort
  ${EndIf}
FunctionEnd

Section "Uninstall"
  Call un.StopAndRemoveService

  SetShellVarContext all
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\DynaK\${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\DynaK"

  SetRegView 64
  DeleteRegKey HKLM "${UNINSTALL_REG_KEY}"
  DeleteRegValue HKLM "${PRODUCT_REG_KEY}" "InstallLocation"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR"

  ; Machine data under ProgramData and reports under Public Documents are intentionally preserved.
SectionEnd
