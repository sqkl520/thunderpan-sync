; ============================================================================
; ThunderPanSync.nsi —— 安装/卸载告知链路
; （风险确认页 + 卸载数据处置页 + 卸载器用词本地化；基线为 work\disclose\installer-proposed.nsi）
;
; 与现状的差异（逐条对应报告 §2）：
;   1. 删除 105 行 EULA 滚动框页（MUI_PAGE_LICENSE），换成「风险确认」交互页：
;      一屏 4 条 + 必须勾选才能点「下一步」，另给「查看完整 EULA」按钮。
;   2. 安装位置页顶部解释 101 MB 里的 100.4 MB 是什么（node.exe）。
;   3. 完成页 3 行风险重申 + 首次启动预告；「立即启动」默认不勾（规避首启共享冲突）。
;   4. 卸载确认页自己写（不再用 MUI_UNPAGE_CONFIRM）：说清数据目录里有什么，
;      三选一（默认=删除全部，含登录态），并检测程序是否还在运行。
;   5. 卸载条目名中文化；卸载条目里补 Comments（在「程序和功能」里能读到风险）。
;   6. 卸载器里「解除安装」全部改成「卸载」（大陆用词）。
;
; 编码铁律：本文件必须 UTF-8 带 BOM，否则 makensis 报 "Bad text encoding"。
; 可覆盖的构建期路径（build-native.ps1 不需要改，除非要改产物名）：
;   /DPUBDIR=...   /DOUTFILE=...   /DEULAFILE=...   /DICONFILE=...
; ============================================================================

Unicode true

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "WinMessages.nsh"


; ---------------- 基本信息 ----------------
!define APPNAME "ThunderPanSync"
!define APPVERSION "1.0.0"
!define PUBLISHER "ThunderPanSync"
!define APPEXE "ThunderPanSync.exe"
!define DATADIR "$APPDATA\${APPNAME}"
!define PROFILE "$APPDATA\${APPNAME}\browser-profile"

; ---------------- 可覆盖路径（默认值 = 仓库内真实相对路径）----------------
!ifndef PUBDIR
  !define PUBDIR "native\publish"
!endif
!ifndef OUTFILE
  !define OUTFILE "release\ThunderPanSync-Setup-${APPVERSION}.exe"
!endif
!ifndef EULAFILE
  !define EULAFILE "EULA.txt"
!endif
!ifndef ICONFILE
  !define ICONFILE "build\icon.ico"
!endif
!ifndef LICFILE
  !define LICFILE "LICENSE"
!endif
!ifndef NOTICESFILE
  !define NOTICESFILE "THIRD_PARTY_NOTICES.md"
!endif
!ifndef READMEFILE
  !define READMEFILE "README.md"
!endif

Name "${APPNAME} ${APPVERSION}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\${APPNAME}"
InstallDirRegKey HKCU "Software\${APPNAME}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName" "${APPNAME}"
; 不要把"迅雷"写进版本资源：那是"暗示关联/背书"，比说明性使用风险高得多。
VIAddVersionKey "FileDescription" "Folder uploader for Xunlei Pan (unofficial)"
VIAddVersionKey "FileVersion" "${APPVERSION}"
VIAddVersionKey "ProductVersion" "${APPVERSION}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 ${PUBLISHER}"

; ---------------- 界面素材 ----------------
!define MUI_ABORTWARNING
!define MUI_ICON "${ICONFILE}"
!define MUI_UNICON "${ICONFILE}"

; 统一简体中文用词（MUI2 的 SimpChinese 语言包通篇用「解除安装」，是台港用词）。
; MUI2 的 LangFileString 是 !ifndef 保护的，所以在 MUI_LANGUAGE 之前 define 即可覆盖；
; 万一某个 MUI2 版本不再保护，这些 define 只是被忽略，不会导致编译失败。
!define MUI_UNTEXT_WELCOME_INFO_TEXT "这个向导将全程指引你卸载 $(^NameDA)。$\r$\n$\r$\n在开始之前，确认 $(^NameDA) 已经退出。$\r$\n$\r$\n$_CLICK"
!define MUI_UNTEXT_UNINSTALLING_TITLE "正在卸载"
!define MUI_UNTEXT_UNINSTALLING_SUBTITLE "$(^NameDA) 正在卸载，请稍候。"
!define MUI_UNTEXT_FINISH_TITLE "卸载已完成"
!define MUI_UNTEXT_FINISH_SUBTITLE "卸载程序已成功运行完成。"
!define MUI_UNTEXT_FINISH_INFO_TITLE "正在完成 $(^NameDA) 卸载"
!define MUI_UNTEXT_FINISH_INFO_TEXT "$(^NameDA) 已从你的计算机移除。$\r$\n$\r$\n云盘上已经上传的文件不会因为卸载而被删除。$\r$\n$\r$\n单击 [完成] 关闭这个向导。"
!define MUI_UNTEXT_ABORTWARNING "你确实要退出 $(^Name) 卸载程序吗？"

; ---------------- 页 1：欢迎 ----------------
!define MUI_WELCOMEPAGE_TITLE "把文件夹单向推到迅雷云盘"
!define MUI_WELCOMEPAGE_TEXT "这个工具监视你指定的文件夹，把新文件自动上传到迅雷云盘的一个目录里。$\r$\n$\r$\n它只能上传，不能下载或恢复：本地文件删了，它没法帮你取回来。$\r$\n$\r$\n下一页会请你确认 4 条风险。其中最重的一条是：它可能违反《迅雷网盘服务协议》，导致你的账号被终止（俗称封号），而封号会连带永久删除这个云盘里的全部文件。$\r$\n$\r$\n$_CLICK"
!insertmacro MUI_PAGE_WELCOME

; ---------------- 页 2：风险确认（替代 105 行 EULA 滚动页）----------------
Page custom RiskPageShow RiskPageLeave

; ---------------- 页 3：安装位置（顶部解释体积构成）----------------
!define MUI_DIRECTORYPAGE_TEXT_TOP "安装位置。$\r$\n$\r$\n注意：安装后大约占用 101 MB，其中 100.4 MB 是内置的 Node.js 运行时（node.exe）——这个工具用它跑同步引擎，所以体积远大于安装包（26 MB）。卸载时这部分会一起删掉。"
!insertmacro MUI_PAGE_DIRECTORY

; ---------------- 页 4：安装 ----------------
!insertmacro MUI_PAGE_INSTFILES

; ---------------- 页 5：完成（风险重申 + 首启预告）----------------
!define MUI_FINISHPAGE_TITLE "安装完成，但请先记住三件事"
!define MUI_FINISHPAGE_TEXT "$\r$\n  1. 它不是备份：只能上传，不能下载或恢复。请自己保留本地原件。$\r$\n$\r$\n  2. 它可能与迅雷的服务条款冲突，账号可能被限制或封号，封号会连带影响这个云盘里的其它文件。$\r$\n$\r$\n  3. 运行时会在本机开一个无认证的调试端口，本机任何程序都能接管那个已登录的迅雷会话。$\r$\n$\r$\n第一次启动会打开一个浏览器窗口让你登录迅雷云盘。如果第一次打开时左上角显示红色「引擎异常」，关掉重开一次即可（安装器刚写完文件时偶发）。$\r$\n$\r$\n最省事的自保办法：注册一个专门的迅雷小号给这个工具用 —— 万一被封，没的是这个小号，不是你攒了几年的那个号。$\r$\n$\r$\n完整风险说明：安装目录里的 EULA.txt / README.md / RELEASE-NOTES.md。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动 ${APPNAME}"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_FINISH

; ---------------- 卸载页：确认 + 数据处置（自己写，替代 MUI_UNPAGE_CONFIRM）----
UninstPage custom un.DataPageShow un.DataPageLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ============================================================================
; 变量
; ============================================================================
Var RiskCheck          ; 风险页的复选框句柄
Var RiskFontBold       ; 粗体字体句柄
Var RiskFontRed        ; 红色粗体
Var EulaExtracted      ; EULA 是否已解到 $PLUGINSDIR
Var UnDataMode         ; 卸载数据策略：0=全删 1=只删登录态 2=全留
Var UnDataSizeMB
Var UnAppRunning
Var UnRadio0
Var UnRadio1
Var UnRadio2

; ============================================================================
; 页 2 实现：风险确认
; ============================================================================
Function RiskPageShow
  !insertmacro MUI_HEADER_TEXT "安装之前：请确认 4 件事" "这些不是免责声明，是这个工具的真实边界"

  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  CreateFont $RiskFontBold "Microsoft YaHei UI" 9 700
  CreateFont $RiskFontRed  "Microsoft YaHei UI" 10 700

  ; 最刺眼的一条放最上面，红字加粗
  ${NSD_CreateLabel} 0 0 100% 26u "① 它可能违反《迅雷网盘服务协议》第 3.12、3.4.3 条：你的迅雷账号可能被终止（俗称封号）。封号后迅雷有权永久删除云盘里的全部文件 —— 不只是本工具传的，也包括你以前手动传的（协议 2.8 条）。"
  Pop $1
  SendMessage $1 ${WM_SETFONT} $RiskFontRed 1
  SetCtlColors $1 "C00000" "FFFFFF"

  ${NSD_CreateLabel} 0 28u 100% 50u "② 它不是备份：只能上传，不能下载、不能恢复，也不与云端对账。本地文件删了，云盘上那一份就是唯一的，而且你只能去网页版手动下载。$\r$\n$\r$\n③ 它会在本机开一个没有认证的调试端口（端口每次随机、只绑 127.0.0.1，应用运行期间一直开放），本机任何程序都能连上去，接管那个已登录迅雷账号的浏览器。$\r$\n$\r$\n④ 云端文件也可能被平台按协议处理或删除（超容、超期未登录、账号终止）。作者不对你的数据做任何担保。"
  Pop $1
  SendMessage $1 ${WM_SETFONT} $RiskFontBold 1

  ${NSD_CreateCheckbox} 0 82u 100% 10u "我已阅读并理解以上 4 条风险，自愿承担后果（包括账号被封、文件被永久删除）"
  Pop $RiskCheck
  ${NSD_OnClick} $RiskCheck RiskToggle

  ; 小号句：四个告知面逐字一致（README / RELEASE-NOTES / EULA.txt / 这里）
  ${NSD_CreateLabel} 0 94u 100% 14u "最省事的自保办法：注册一个专门的迅雷小号给这个工具用 —— 万一被封，没的是这个小号，不是你攒了几年的那个号。"
  Pop $1
  SendMessage $1 ${WM_SETFONT} $RiskFontBold 1
  SetCtlColors $1 "1F5FAF" "FFFFFF"

  ${NSD_CreateButton} 0 112u 92u 14u "查看完整许可文本（EULA.txt）"
  Pop $1
  ${NSD_OnClick} $1 RiskOpenEula

  ; 未勾选前禁用「下一步」
  GetDlgItem $2 $HWNDPARENT 1
  EnableWindow $2 0

  nsDialogs::Show
FunctionEnd

Function RiskToggle
  Pop $0
  ${NSD_GetState} $RiskCheck $1
  GetDlgItem $2 $HWNDPARENT 1
  ${If} $1 == ${BST_CHECKED}
    EnableWindow $2 1
  ${Else}
    EnableWindow $2 0
  ${EndIf}
FunctionEnd

Function RiskOpenEula
  Pop $0
  ${If} $EulaExtracted != "1"
    File "/oname=$PLUGINSDIR\EULA.txt" "${EULAFILE}"
    StrCpy $EulaExtracted "1"
  ${EndIf}
  ExecShell "open" "$PLUGINSDIR\EULA.txt"
FunctionEnd

Function RiskPageLeave
  ${NSD_GetState} $RiskCheck $0
  ${If} $0 != ${BST_CHECKED}
    MessageBox MB_ICONEXCLAMATION|MB_OK "请先勾选「我已阅读并理解以上 4 条风险」，再点下一步。"
    Abort
  ${EndIf}
FunctionEnd

; ============================================================================
; 安装段
; ============================================================================
Section "主程序" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"

  ; 主程序 + 引擎 + 运行时
  ; 注意：File /r 会把源目录内容平铺到当前 $INSTDIR，必须逐个指定目标子目录，
  ; 否则 node.exe 会落到根目录，而 Program.cs 找的是 runtime\node.exe（实测踩到的坑）
  File "${PUBDIR}\ThunderPanSync.exe"

  SetOutPath "$INSTDIR\engine"
  File "${PUBDIR}\engine\host.js"
  File "${PUBDIR}\engine\paths.js"

  SetOutPath "$INSTDIR\engine\engine"
  File "${PUBDIR}\engine\engine\cdp.js"
  File "${PUBDIR}\engine\engine\engine.js"

  SetOutPath "$INSTDIR\runtime"
  File "${PUBDIR}\runtime\node.exe"

  ; 许可与第三方声明必须随二进制一起分发。
  ; Node.js 是 MIT 许可，再分发时"必须随附版权声明与许可文本"，
  ; 只在仓库里有不算数 —— 用户拿到的是安装包，不是仓库。
  SetOutPath "$INSTDIR"
  File "${LICFILE}"
  File "${NOTICESFILE}"
  File "${READMEFILE}"
  File "${EULAFILE}"

  SetOutPath "$INSTDIR"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\${APPNAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\${APPNAME}" "Version" "${APPVERSION}"

  ; DisplayName 不带"迅雷"（他人商标），但用中文：控制面板里和一堆中文软件放在一起才不突兀。
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPNAME} 文件夹上传工具"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayVersion" "${APPVERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayIcon" "$INSTDIR\${APPEXE}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "InstallLocation" "$INSTDIR"
  ; 「程序和功能」的"详细信息"列会显示 Comments —— 让卸载列表这一屏也带一句风险。
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "Comments" "单向上传工具：只能上传，不能下载或恢复；可能与迅雷服务条款冲突，账号存在被封风险。"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "EstimatedSize" "$0"

  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0
  CreateShortcut "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "创建数据目录"
  CreateDirectory "${DATADIR}"
SectionEnd

; ============================================================================
; 页：卸载确认 + 数据处置
; ============================================================================
Function un.DataPageShow
  !insertmacro MUI_HEADER_TEXT "卸载 ${APPNAME}" "先选择数据怎么处理，再开始卸载"

  ; 数据目录大小（动态算，不让用户猜）
  StrCpy $UnDataSizeMB "0"
  ${If} ${FileExists} "${DATADIR}\*.*"
    ${GetSize} "${DATADIR}" "/S=0K" $0 $1 $2
    IntOp $UnDataSizeMB $0 / 1024
  ${EndIf}

  ; 程序是否还在跑（卸载会强制关掉它）
  StrCpy $UnAppRunning "0"
  ; 用 CSV 输出的首字符判断：有进程时首字符是 ASCII 引号；没有进程时是本地化的
  ; "信息: 没有运行的任务..."（首字符不是引号）。不依赖任何字符串函数。
  nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${APPEXE}" /NH /FO CSV'
  Pop $0
  Pop $1
  StrCpy $2 $1 1
  StrCmp $2 '$\"' 0 +2
    StrCpy $UnAppRunning "1"

  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  CreateFont $RiskFontBold "Microsoft YaHei UI" 9 700

  ${NSD_CreateLabel} 0 0 100% 30u "卸载会删掉程序目录（约 101 MB）。$\r$\n云盘上已经上传的文件不会因为卸载而被删除。$\r$\n下面这块是你选的安装位置：$INSTDIR"
  Pop $1
  SendMessage $1 ${WM_SETFONT} $RiskFontBold 1

  ${NSD_CreateLabel} 0 34u 100% 26u "另有数据目录（当前 $UnDataSizeMB MB；长期使用会持续增长，可达约 800 MB —— 绝大部分是浏览器 profile）：${DATADIR}$\r$\n里面有：设置、运行日志、已上传记录，以及浏览器登录状态（含你的迅雷登录）。"
  Pop $1
  SetCtlColors $1 "C00000" "FFFFFF"

  ${NSD_CreateRadioButton} 0 62u 100% 12u "删除全部数据（推荐；卖电脑、转让他人、或不想留任何痕迹时选这个）"
  Pop $UnRadio0
  ${NSD_CreateRadioButton} 0 76u 100% 12u "只删除浏览器登录状态，保留设置和已上传记录（下次重装不用重新配置）"
  Pop $UnRadio1
  ${NSD_CreateRadioButton} 0 90u 100% 12u "全部保留（下次重装不用重新登录，但登录态会留在硬盘上）"
  Pop $UnRadio2
  ${NSD_SetState} $UnRadio0 ${BST_CHECKED}
  StrCpy $UnDataMode "0"

  ${If} $UnAppRunning == "1"
    ${NSD_CreateLabel} 0 106u 100% 22u "${APPNAME} 正在运行。继续卸载会先强制关闭它（正在上传的文件会中断，下一轮启动后会重试）。"
    Pop $1
    SetCtlColors $1 "B36B00" "FFFFFF"
  ${EndIf}

  nsDialogs::Show
FunctionEnd

Function un.DataPageLeave
  ${NSD_GetState} $UnRadio0 $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $UnDataMode "0"
  ${EndIf}
  ${NSD_GetState} $UnRadio1 $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $UnDataMode "1"
  ${EndIf}
  ${NSD_GetState} $UnRadio2 $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $UnDataMode "2"
  ${EndIf}
FunctionEnd

; ============================================================================
; 卸载段
; ============================================================================
Section "Uninstall"
  ; 先关掉正在运行的实例，否则文件删不掉。
  ; 卸载页已经告诉过用户"会强制关闭"，这里不再二次弹窗。
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
  !define BROWSERPROFILE "${PROFILE}"
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

  ; ---- 数据目录：按用户在卸载页里的选择处理 ----
  ; 0 = 全删；1 = 只删登录态；2 = 全留
  ${If} $UnDataMode == "0"
    RMDir /r "${DATADIR}"
  ${ElseIf} $UnDataMode == "1"
    RMDir /r "${PROFILE}"
    ; 登录态没了，下次启动要重新登录；端口记录也一并清掉（重新探测）
    Delete "${DATADIR}\cdp-port.txt"
  ${EndIf}

  ; 只有在"数据还留着"的时候，才在最后明确告诉用户留了什么、在哪、怎么彻底清。
  ${If} $UnDataMode != "0"
    ${If} ${FileExists} "${DATADIR}\*.*"
      MessageBox MB_ICONINFORMATION|MB_OK "卸载完成。$\r$\n$\r$\n数据目录被保留：${DATADIR}$\r$\n里面仍有设置、日志，以及（如果选了「全部保留」）浏览器登录状态。$\r$\n$\r$\n要彻底清除：删掉上面这个文件夹即可。$\r$\n云盘上已上传的文件不受影响。"
    ${EndIf}
  ${EndIf}
SectionEnd
