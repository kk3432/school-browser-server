; ============================================================================
;  CampusBrowser Server（校园浏览器服务端）NSIS 安装脚本
;  框架：NSIS 3.x MUI2（开源，zlib 许可证；https://github.com/kichik/nsis）
;
;  编译（在本目录 server/installer/ 下）：
;    makensis /DAPP_VERSION=0.4.0 campus-browser-server.nsi
;  产物：CampusBrowserServer-setup.exe
;
;  目录约定：
;    publish/win-x64/   dotnet publish 输出（安装前由构建流程生成）
;    campus-browser.ico 应用图标（复用 APP logo）
; ============================================================================

!define APP_NAME        "CampusBrowser Server"
!define APP_PUBLISHER   "校园浏览器项目组"
!define SERVICE_NAME    "CampusBrowserServer"
!define SERVICE_DISPLAY "CampusBrowser Server"
!define FIREWALL_RULE   "CampusBrowser Server"
!define REG_MAIN        "Software\CampusBrowser"
!define REG_UNINST      "Software\Microsoft\Windows\CurrentVersion\Uninstall\CampusBrowser"
!define DEFAULT_PORT    "8080"

; 版本号由编译命令 -DAPP_VERSION=x.y.z 传入，缺省给一个保守值
!ifndef APP_VERSION
  !define APP_VERSION "0.3.0"
!endif

; ----------------------------- 头文件与全局 ---------------------------------
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

Name "${APP_NAME}"
Unicode True
ShowInstDetails show
ShowUnInstDetails show
RequestExecutionLevel admin

InstallDir    "$PROGRAMFILES64\CampusBrowserServer"
InstallDirRegKey HKLM "${REG_MAIN}" "InstallLocation"   ; 升级时默认回到上次安装目录

OutFile "CampusBrowserServer-setup.exe"

!define MUI_ICON   "campus-browser.ico"
!define MUI_UNICON "campus-browser.ico"

; ------------------------------- 页面 ---------------------------------------
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom OptionsPageCreate OptionsPageLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ------------------------------ 变量 ----------------------------------------
Var PortEdit
Var Port
Var AutoStart
Var WantDesktop

; ============================================================================
;  安装前环境检查：仅支持 64 位 Windows
; ============================================================================
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "本安装包仅支持 64 位 Windows 系统。"
    Quit
  ${EndIf}
FunctionEnd

; ============================================================================
;  自定义页：服务端口 / 开机自启动 / 桌面快捷方式
; ============================================================================
Function OptionsPageCreate
  !insertmacro MUI_HEADER_TEXT "安装选项" "设置服务端口、开机自启动与桌面快捷方式"

  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 12u "服务端口（1-65535，默认 ${DEFAULT_PORT}）："
  Pop $1

  ${NSD_CreateText} 0 14u 100% 14u "${DEFAULT_PORT}"
  Pop $PortEdit

  ${NSD_CreateLabel} 0 33u 100% 26u "安装程序会自动添加防火墙入站规则，供教室平板访问管理后台。"
  Pop $2

  ${NSD_CreateCheckbox} 0 66u 100% 12u "开机自动启动服务（推荐）"
  Pop $3
  ${NSD_SetState} $3 ${BST_CHECKED}

  ${NSD_CreateCheckbox} 0 84u 100% 12u "在桌面创建管理后台快捷方式"
  Pop $4
  ${NSD_SetState} $4 ${BST_CHECKED}

  ; 升级时回填上次端口
  ReadRegStr $5 HKLM "${REG_MAIN}" "Port"
  ${If} $5 != ""
    ${NSD_SetText} $PortEdit $5
  ${EndIf}

  nsDialogs::Show
FunctionEnd

Function OptionsPageLeave
  ${NSD_GetText} $PortEdit $Port
  ${NSD_GetState} $3 $AutoStart
  ${NSD_GetState} $4 $WantDesktop

  ; 端口非空 + 数值范围校验（非数字会被 IntCmp 当作 0，落入 <1 分支）
  ${If} $Port == ""
    MessageBox MB_OK|MB_ICONSTOP "请输入服务端口。"
    Abort
  ${EndIf}
  IntCmp $Port 1 bad_port range_ok range_ok
  range_ok:
  IntCmp $Port 65535 port_ok port_ok bad_port
  port_ok:

  ; 端口占用检测：查 LISTENING 状态的 TCP 监听
  nsExec::ExecToStack 'cmd /c netstat -ano -p tcp | findstr "LISTENING" | findstr ":$Port "'
  Pop $0
  Pop $1
  ${If} $0 == 0
    MessageBox MB_OKCANCEL|MB_ICONQUESTION \
      "端口 $Port 似乎已被其他程序占用：$\n$\n$1$\n$\n继续安装可能导致服务无法启动。$\n是否仍要使用该端口？" \
      IDOK port_use_anyway IDCANCEL port_abort
    port_use_anyway:
  ${EndIf}
  Return

  port_abort:
  Abort

  bad_port:
  MessageBox MB_OK|MB_ICONSTOP "端口必须是 1-65535 之间的数字。"
  Abort
FunctionEnd

; ============================================================================
;  安装段
; ============================================================================
Section "CampusBrowser Server" SecCore
  SetDetailsPrint both

  ; ---- 1. 停止并删除旧服务（升级覆盖；全新安装时 sc 返回错误可忽略） ----
  DetailPrint "正在停止旧服务…"
  nsExec::Exec 'sc stop ${SERVICE_NAME}'
  Pop $0
  Sleep 1500
  nsExec::Exec 'sc delete ${SERVICE_NAME}'
  Pop $0
  ; 兜底结束残留进程，避免文件被占用
  nsExec::Exec 'taskkill /F /IM CampusBrowser.Server.exe /T'
  Pop $0
  Sleep 800

  ; ---- 2. 复制程序文件（instfiles 页面原生显示逐文件进度） ----
  DetailPrint "正在复制程序文件…"
  SetOutPath "$INSTDIR"
  File /r "publish\win-x64\*.*"
  File "campus-browser.ico"

  ; ---- 3. 写 campus.port（本次选择的端口） ----
  FileOpen $0 "$INSTDIR\campus.port" w
  FileWrite $0 "$Port"
  FileClose $0

  ; ---- 4. 注册 Windows 服务 ----
  DetailPrint "正在注册 Windows 服务…"
  ${If} $AutoStart == ${BST_CHECKED}
    nsExec::Exec 'sc create ${SERVICE_NAME} binPath= "$INSTDIR\CampusBrowser.Server.exe" start= auto DisplayName= "${SERVICE_DISPLAY}"'
    Pop $0
  ${Else}
    nsExec::Exec 'sc create ${SERVICE_NAME} binPath= "$INSTDIR\CampusBrowser.Server.exe" start= demand DisplayName= "${SERVICE_DISPLAY}"'
    Pop $0
  ${EndIf}
  nsExec::Exec 'sc description ${SERVICE_NAME} "校园浏览器统一上网管控服务端"'
  Pop $0
  ; 服务异常退出时自动重启（教学现场可自愈）
  nsExec::Exec 'sc failure ${SERVICE_NAME} reset= 0 actions= restart/5000/restart/10000/restart/30000'
  Pop $0
  ${If} $AutoStart == ${BST_CHECKED}
    DetailPrint "正在启动服务…"
    nsExec::Exec 'sc start ${SERVICE_NAME}'
    Pop $0
  ${EndIf}

  ; ---- 5. 防火墙入站规则（先删旧的同名规则再建） ----
  DetailPrint "正在配置防火墙规则…"
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${FIREWALL_RULE}"'
  Pop $0
  nsExec::Exec 'netsh advfirewall firewall add rule name="${FIREWALL_RULE}" dir=in action=allow protocol=TCP localport=$Port'
  Pop $0

  ; ---- 6. 开始菜单快捷方式 ----
  CreateDirectory "$SMPROGRAMS\CampusBrowser"
  CreateShortcut "$SMPROGRAMS\CampusBrowser\管理后台.lnk" \
    "http://localhost:$Port/" "" "$INSTDIR\campus-browser.ico" 0
  CreateShortcut "$SMPROGRAMS\CampusBrowser\卸载服务端.lnk" "$INSTDIR\Uninstall.exe"

  ; ---- 7. 可选桌面快捷方式 ----
  ${If} $WantDesktop == ${BST_CHECKED}
    CreateShortcut "$DESKTOP\校园浏览器管理后台.lnk" \
      "http://localhost:$Port/" "" "$INSTDIR\campus-browser.ico" 0
  ${EndIf}

  ; ---- 8. 注册表：安装信息 + 控制面板卸载项 ----
  WriteRegStr HKLM "${REG_MAIN}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${REG_MAIN}" "Port" "$Port"

  WriteRegStr   HKLM "${REG_UNINST}" "DisplayName"     "CampusBrowser Server（校园浏览器服务端）"
  WriteRegStr   HKLM "${REG_UNINST}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${REG_UNINST}" "DisplayIcon"     "$INSTDIR\campus-browser.ico"
  WriteRegStr   HKLM "${REG_UNINST}" "DisplayVersion"  "${APP_VERSION}"
  WriteRegStr   HKLM "${REG_UNINST}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr   HKLM "${REG_UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "${REG_UNINST}" "NoModify" 1
  WriteRegDWORD HKLM "${REG_UNINST}" "NoRepair" 1

  ; ---- 9. 生成卸载器 ----
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  DetailPrint "安装完成。管理后台地址：http://localhost:$Port/"
SectionEnd

; ============================================================================
;  卸载段
; ============================================================================
Section "Uninstall"
  SetDetailsPrint both

  ; ---- 1. 停止并删除服务 ----
  DetailPrint "正在停止服务…"
  nsExec::Exec 'sc stop ${SERVICE_NAME}'
  Pop $0
  Sleep 1500
  nsExec::Exec 'sc delete ${SERVICE_NAME}'
  Pop $0
  nsExec::Exec 'taskkill /F /IM CampusBrowser.Server.exe /T'
  Pop $0

  ; ---- 2. 删除防火墙规则 ----
  DetailPrint "正在删除防火墙规则…"
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${FIREWALL_RULE}"'
  Pop $0

  ; ---- 3. 数据目录处理：询问是否保留（修复旧卸载器 rd /s /q 误删数据库的问题） ----
  IfFileExists "$INSTDIR\data\*.*" 0 wipe_all
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON1 "是否保留服务器数据（data\campus.db，含设备记录与配置）？$\n$\n选“是”：保留数据，便于以后重装恢复（推荐）；$\n选“否”：连同数据彻底删除。" IDYES keep_data IDNO wipe_all

  keep_data:
  DetailPrint "正在删除程序文件（保留 data 数据目录）…"
  ; 先把 data 移到临时位置，清空目录后移回，得到一个只含 data 的干净目录
  Rename "$INSTDIR\data" "$TEMP\campus-data-keep"
  RMDir /r "$INSTDIR"
  CreateDirectory "$INSTDIR"
  Rename "$TEMP\campus-data-keep" "$INSTDIR\data"
  Goto shortcuts

  wipe_all:
  DetailPrint "正在删除全部文件…"
  RMDir /r "$INSTDIR"

  shortcuts:
  ; ---- 4. 快捷方式 ----
  Delete "$SMPROGRAMS\CampusBrowser\管理后台.lnk"
  Delete "$SMPROGRAMS\CampusBrowser\卸载服务端.lnk"
  RMDir  "$SMPROGRAMS\CampusBrowser"
  Delete "$DESKTOP\校园浏览器管理后台.lnk"

  ; ---- 5. 注册表 ----
  DeleteRegKey HKLM "${REG_UNINST}"
  DeleteRegKey HKLM "${REG_MAIN}"

  DetailPrint "卸载完成。"
SectionEnd
