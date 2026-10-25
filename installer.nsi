; ThunderPanSync.nsi -- 原生版安装程序（NSIS）
;
; 为什么用 NSIS 而不是 MSI/Inno：
;   1. NSIS 编译器已经在机器上（electron-builder 之前下载过），零额外下载
;   2. 单个 exe 安装包，用户双击即可，不需要管理员权限（装到 %LOCALAPPDATA%）
;   3. 支持中文界面、自定义安装目录、卸载程序、开机自启项注册
;
; 安装布局（关键：安装目录只放程序，数据一律在 %APPDATA%）：
;   %LOCALAPPDATA%\ThunderPanSync\
;     ThunderPanSync.exe          ← 原生 C# 界面
;     runtime\node.exe            ← 引擎运行时
;     engine\host.js 等           ← 引擎
;     Uninstall.exe
;
; 数据（配置/日志/浏览器配置）在 %APPDATA%\ThunderPanSync\，卸载时询问是否删除。

Unicode true

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define APPNAME "ThunderPanSync"
!define APPVERSION "1.0.0"
!define PUBLISHER "ThunderPanSync"
!define APPEXE "ThunderPanSync.exe"

Name "${APPNAME} ${APPVERSION}"
OutFile "release\ThunderPanSync-Setup-${APPVERSION}.exe"
InstallDir "$LOCALAPPDATA\${APPNAME}"
InstallDirRegKey HKCU "Software\${APPNAME}" "InstallDir"
RequestExecutionLevel user          ; 不需要管理员：装到用户目录
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "迅雷云盘自动备份"
VIAddVersionKey "FileVersion" "${APPVERSION}"
VIAddVersionKey "ProductVersion" "${APPVERSION}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 ${PUBLISHER}"

; ---------------- 界面 ----------------
!define MUI_ABORTWARNING
!define MUI_ICON "build\icon.ico"
!define MUI_UNICON "build\icon.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动 ThunderPanSync"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ---------------- 安装 ----------------
Section "主程序" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"

  ; 主程序 + 引擎 + 运行时
  ; 注意：File /r 会把源目录内容平铺到当前 $INSTDIR，必须逐个指定目标子目录，
  ; 否则 node.exe 会落到根目录，而 Program.cs 找的是 runtime\node.exe（实测踩到的坑）
  File "native\publish\ThunderPanSync.exe"

  SetOutPath "$INSTDIR\engine"
  File "native\publish\engine\host.js"
  File "native\publish\engine\paths.js"

  SetOutPath "$INSTDIR\engine\engine"
  File "native\publish\engine\engine\cdp.js"
  File "native\publish\engine\engine\engine.js"

  SetOutPath "$INSTDIR\runtime"
  File "native\publish\runtime\node.exe"

  ; 许可与第三方声明必须随二进制一起分发。
  ; Node.js 是 MIT 许可，再分发时"必须随附版权声明与许可文本"，
  ; 只在仓库里有不算数 —— 用户拿到的是安装包，不是仓库。
  SetOutPath "$INSTDIR"
  File "LICENSE"
  File "THIRD_PARTY_NOTICES.md"
  File "README.md"

  SetOutPath "$INSTDIR"

  ; 卸载程序
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 注册表：安装位置、卸载信息（控制面板"应用和功能"能看到）
  WriteRegStr HKCU "Software\${APPNAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\${APPNAME}" "Version" "${APPVERSION}"

  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPNAME} - 迅雷云盘自动备份"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayVersion" "${APPVERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayIcon" "$INSTDIR\${APPEXE}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "NoRepair" 1

  ; 估算安装体积（控制面板显示）
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "EstimatedSize" "$0"

  ; 桌面 + 开始菜单快捷方式
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0
  CreateShortcut "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

; 数据目录提示（单独一节，让用户知道数据在哪）
Section "创建数据目录"
  CreateDirectory "$APPDATA\${APPNAME}"
SectionEnd

; ---------------- 卸载 ----------------
Section "Uninstall"
  ; 先关掉正在运行的实例，否则文件删不掉
  ExecWait 'taskkill /IM ${APPEXE} /F'

  Delete "$INSTDIR\${APPEXE}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR\engine"
  RMDir /r "$INSTDIR\runtime"
  RMDir "$INSTDIR"

  Delete "$DESKTOP\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"

  ; 开机自启项
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APPNAME}"

  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
  DeleteRegKey HKCU "Software\${APPNAME}"

  ; 数据目录：明确询问（备份配置和日志，不该静默删）
  IfFileExists "$APPDATA\${APPNAME}\*.*" 0 done
  MessageBox MB_YESNO|MB_ICONQUESTION \
    "是否同时删除配置文件、日志和浏览器登录状态？$\n$\n位置：$APPDATA\${APPNAME}$\n$\n选择「否」可以保留，下次安装时继续使用。" \
    IDYES delData IDNO done
  delData:
    RMDir /r "$APPDATA\${APPNAME}"
  done:
SectionEnd
