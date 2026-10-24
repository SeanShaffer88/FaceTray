; FaceTray installer (NSIS) — per-user install, no admin required.
;
; Build (from the repo root, with the verified release publish staged):
;   makensis /DVERSION=0.2.0 /DSTAGE=<dir containing the publish output> installer\FaceTray.nsi
; Produces FaceTray-Setup-v<VERSION>-x64.exe next to this script.
;
; Design notes (see DECISIONS.md, 2026-10-23):
; - Per-user install to %LOCALAPPDATA%\Programs\FaceTray, HKCU uninstall
;   registration: matches the app's per-user data model and needs no UAC.
; - Uninstall removes the program and shortcuts but KEEPS the user's data
;   (%APPDATA%\FaceTray settings/cache/log and the sign-in profile under
;   %LOCALAPPDATA%\FaceTray) unless the user answers Yes to the
;   "also delete" prompt.

!ifndef VERSION
  !define VERSION "0.2.0"
!endif
!ifndef STAGE
  !error "Pass /DSTAGE=<publish output dir>"
!endif

Unicode true
Name "FaceTray"
OutFile "FaceTray-Setup-v${VERSION}-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\FaceTray"
InstallDirRegKey HKCU "Software\FaceTray" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "FaceTray"
VIAddVersionKey "FileDescription" "FaceTray Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "MIT License"

!include "MUI2.nsh"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\FaceTray.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Run FaceTray"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "FaceTray" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "${STAGE}\*.*"

  WriteRegStr HKCU "Software\FaceTray" "InstallDir" "$INSTDIR"

  ; Start Menu shortcut
  CreateDirectory "$SMPROGRAMS\FaceTray"
  CreateShortcut "$SMPROGRAMS\FaceTray\FaceTray.lnk" "$INSTDIR\FaceTray.exe"

  ; Uninstaller + Apps & Features registration (per-user)
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray" "DisplayName" "FaceTray"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray" "Publisher" "Sean Shaffer"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray" "DisplayIcon" "$INSTDIR\FaceTray.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray" "NoRepair" 1
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\FaceTray.lnk" "$INSTDIR\FaceTray.exe"
SectionEnd

LangString DESC_SecMain ${LANG_ENGLISH} "FaceTray program files (required)."
LangString DESC_SecDesktop ${LANG_ENGLISH} "Adds a FaceTray shortcut to your desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} $(DESC_SecMain)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(DESC_SecDesktop)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  ; Program files and shortcuts
  Delete "$SMPROGRAMS\FaceTray\FaceTray.lnk"
  RMDir "$SMPROGRAMS\FaceTray"
  Delete "$DESKTOP\FaceTray.lnk"
  ; Remove the autostart entry if FaceTray created one
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "FaceTray"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FaceTray"
  DeleteRegKey HKCU "Software\FaceTray"

  ; User data is kept by default (settings, cache, log, and the sign-in profile).
  MessageBox MB_YESNO|MB_ICONQUESTION \
    "Also delete FaceTray's settings and sign-in data?$\n$\nThis removes %APPDATA%\FaceTray and the profile folder under %LOCALAPPDATA%\FaceTray, signing you out of Facebook in FaceTray on this PC. Your Facebook account itself is not affected." \
    IDNO keepData
    RMDir /r "$APPDATA\FaceTray"
    RMDir /r "$LOCALAPPDATA\FaceTray"
  keepData:
SectionEnd
