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
; 不要把"迅雷"写进版本资源：这是"暗示关联/背书"，比 README 里的说明性使用风险高得多。
VIAddVersionKey "FileDescription" "Folder backup tool for cloud drive"
VIAddVersionKey "FileVersion" "${APPVERSION}"
VIAddVersionKey "ProductVersion" "${APPVERSION}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 ${PUBLISHER}"

; ---------------- 界面 ----------------
!define MUI_ABORTWARNING
!define MUI_ICON "build\icon.ico"
!define MUI_UNICON "build\icon.ico"

!insertmacro MUI_PAGE_WELCOME
; EULA 必须出现在安装流程里：风险披露只有在用户真正看到时才有意义。
; （此前 EULA.txt 写好了却没接进安装包，用户拿到 exe 什么也看不到）
!insertmacro MUI_PAGE_LICENSE "EULA.txt"
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
  ; EULA.txt 必须一起装：安装页显示过，但用户之后还要能翻出来看。
  SetOutPath "$INSTDIR"
  File "LICENSE"
  File "THIRD_PARTY_NOTICES.md"
  File "README.md"
  File "EULA.txt"

  SetOutPath "$INSTDIR"

  ; 卸载程序
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 注册表：安装位置、卸载信息（控制面板"应用和功能"能看到）
  WriteRegStr HKCU "Software\${APPNAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\${APPNAME}" "Version" "${APPVERSION}"

  ; DisplayName 不带"迅雷"：那是他人商标，写进控制面板等于暗示关联/背书。
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPNAME} - Folder backup tool"
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

  ; 清理本应用启动的那个浏览器。
  ; 严格只杀"命令行里含本应用 profile 路径"的 msedge/chrome 进程：
  ; 绝不能 taskkill /IM msedge.exe —— 那会把用户自己的浏览器一起杀掉。
  ; 不清理的后果：卸载后残留一个带调试端口的浏览器常驻（该端口无认证）。
  ;
  ; 转义坑（两条都实测踩过）：
  ;   1. NSIS 里 $$_ 才是 PowerShell 的 $_。直接写 $_ 会被 makensis 当成未知变量
  ;      吞掉（warning 6000），$_.CommandLine 变成空串 ⇒ Where-Object 恒假
  ;      ⇒ 一个进程都杀不掉，而安装日志看起来"执行成功"。
  ;   2. NSIS 不把 '' 折叠成一个单引号（那是 SQL 的习惯，不是 NSIS 的）。
  ;      所以这里的 PowerShell 载荷刻意全部用单引号，外层用 $\" 包住，
  ;      载荷里不出现任何双引号 —— 既躲开 NSIS 替换，也躲开 CreateProcess 的引号规则。
  ; 也不要给 powershell 加 -WindowStyle Hidden：实测会让退出码变成 -1，
  ; 而 nsExec 本身已经把窗口隐藏了。
  !define BROWSERPROFILE "$APPDATA\${APPNAME}\browser-profile"
  !define PSCLEAN "try { $$m = '${BROWSERPROFILE}'; $$v = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { ($$_.Name -eq 'msedge.exe' -or $$_.Name -eq 'chrome.exe') -and $$_.CommandLine -and $$_.CommandLine.IndexOf($$m, [StringComparison]::OrdinalIgnoreCase) -ge 0 }); foreach ($$p in $$v) { try { Stop-Process -Id $$p.ProcessId -Force -ErrorAction Stop } catch { } }; exit 0 } catch { exit 1 }"

  nsExec::ExecToLog "$\"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe$\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $\"${PSCLEAN}$\""
  Pop $0

  ; 降级：PowerShell 被策略禁用/被精简掉时用 wmic 再扫一遍。
  ; WQL 的 LIKE 里反斜杠必须写成 \\，否则 "Invalid query"（实测）。
  ; 两条都跑一遍是刻意的：先 PS 后 wmic，谁可用谁生效，都不可用也只是漏清理。
  nsExec::ExecToLog "$\"$SYSDIR\wbem\WMIC.exe$\" process where $\"(name='msedge.exe' or name='chrome.exe') and CommandLine like '%${APPNAME}\\browser-profile%'$\" call terminate"
  Pop $0

  Delete "$INSTDIR\${APPEXE}"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\THIRD_PARTY_NOTICES.md"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\EULA.txt"
  RMDir /r "$INSTDIR\engine"
  RMDir /r "$INSTDIR\runtime"
  RMDir "$INSTDIR"

  Delete "$DESKTOP\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"

  ; 开机自启项。
  ; 注意路径是 ...\CurrentVersion\Run（MainForm.ApplyAutoStart 写的就是这里）。
  ; 写成 Uninstall\${APPNAME} 下的值会静默删掉一个不存在的键，
  ; 结果是卸载后自启项残留，下次开机还会启动一个已被删除的程序。
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APPNAME}"

  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
  DeleteRegKey HKCU "Software\${APPNAME}"

  ; 数据目录：明确询问。
  ; 默认按钮必须是「否」（MB_DEFBUTTON2）—— 该目录含配置、日志和浏览器登录态，
  ; 默认「是」意味着用户一路回车就把登录态删了。
  IfFileExists "$APPDATA\${APPNAME}\*.*" 0 done
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 \
    "是否同时删除配置文件、日志和浏览器登录状态？$\n$\n位置：$APPDATA\${APPNAME}$\n$\n默认保留（选「否」）。删除后下次安装需要重新登录迅雷。$\n注意：云盘上已上传的文件不受影响。" \
    IDYES delData IDNO done
  delData:
    RMDir /r "$APPDATA\${APPNAME}"
  done:
SectionEnd
