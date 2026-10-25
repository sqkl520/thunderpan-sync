# ThunderPanSync 架构

本文写给要改这份代码的人。它描述的是**代码现在真正在做的事**，不是计划。
每一条都能在源码里找到对应实现，行号可能漂移，符号名不会。

---

## 1. 三层结构

```
┌─────────────────────────────────────────────────────────┐
│  ThunderPanSync.exe      C# WinForms（原生控件，约 35 KB）│
│  ├─ Program.cs           入口、单实例、--autostart        │
│  ├─ MainForm.cs          界面 / 托盘 / 开机自启 / 进度     │
│  ├─ EngineHost.cs        stdio JSON-RPC 客户端            │
│  └─ BrowserManager.cs    后台 Edge 生命周期 + 端口选择     │
└───────────────┬─────────────────────────────────────────┘
                │  stdio JSON-RPC（每行一个 JSON，UTF-8 无 BOM）
┌───────────────┴─────────────────────────────────────────┐
│  runtime\node.exe  +  engine\host.js                     │
│  ├─ host.js              JSON-RPC 服务端、命令分发        │
│  ├─ engine\engine.js     扫描 / 上传 / 云端核查 / 版本策略 │
│  ├─ engine\cdp.js        自研 CDP 客户端（替代 playwright）│
│  └─ paths.js             唯一的数据路径来源               │
└───────────────┬─────────────────────────────────────────┘
                │  Chrome DevTools Protocol（WebSocket，本机回环）
┌───────────────┴─────────────────────────────────────────┐
│  msedge.exe（独立 profile，带 --remote-debugging-port）   │
│  └─ pan.xunlei.com 网页版 = captcha 与 token 的唯一来源   │
└─────────────────────────────────────────────────────────┘
```

**为什么是三层而不是两层**：captcha 由网页版 JS 现场计算（算法分片动态下发，
实测无法离线复刻），所以必须有一个真实浏览器在跑。引擎负责"驱动它"，
界面只负责"显示与收集用户意图"，浏览器是凭证来源。三者职责不重叠。

**为什么界面不用 Electron**：试过，239 MB，界面是 Chromium 渲染的网页。
换原生 WinForms 后主程序约 35 KB，调度仍然完全交给 Node 引擎。

---

## 2. stdio JSON-RPC 协议

不用 HTTP 的理由：零端口、零防火墙弹窗、零端口冲突，子进程随父进程退出。

**请求**（C# → Node，一行一个 JSON）：

```json
{"id":1,"cmd":"status"}
{"id":2,"cmd":"saveConfig","params":{"watchDirs":["D:\\docs"]}}
```

**响应**（Node → C#，带同一个 id）：

```json
{"id":1,"ok":true,"data":{...}}
{"id":1,"ok":false,"error":"unknown cmd: foo"}
```

**事件**（Node → C#，主动推送，**没有 id**）：

```json
{"event":"ready","data":{"pid":1234,"dataDir":"...","enabled":true}}
{"event":"log","data":{"level":"info","msg":"...","time":"..."}}
{"event":"progress","data":{"phase":"uploading","index":2,"total":9,...}}
{"event":"run-complete","data":{"uploaded":3,"failed":0,...}}
{"event":"protocol-error","data":{"raw":"..."}}
```

命令集：`status` / `scan` / `runOnce` / `start` / `stop` / `saveConfig` /
`connect` / `resume` / `quit`。

### 协议的三条硬约束

1. **stdout 是 JSON-RPC 独占通道。** `host.js` 在启动时把 `console.log/info/
   warn/error/debug` 全部重定向到 **stderr**。不这么做的话，引擎里一句普通日志
   就会混进 stdout，C# 端 `JavaScriptSerializer` 解析失败、协议直接断掉（真实踩过）。
   原始 stderr 另存 `engine-stderr.log`，崩溃时把末尾若干行附进错误详情。
2. **每行一个 JSON，UTF-8 无 BOM。** 两端都按行读写，不做长度前缀。
3. **无法解析的行不是静默丢弃**，而是回一条 `protocol-error` 事件；界面上有
   "协议错误 N" 计数，非 0 才显示。协议坏了必须看得见。

---

## 3. 上传三段式

迅雷的上传**不是**一次 POST 就完事：

```
① POST /drive/v1/files        → 申请上传票据（STS 临时凭据）
② PUT  → 阿里云 OSS           → 真正传字节，可能几十秒到几分钟
③ 服务端回调入库               → 文件这时候才出现在云盘列表里
```

关键结论：**浏览器侧的 POST 200 只代表"票据申请成功"，不代表文件已入库。**
早期实现等这个 POST 响应来判定成功，结果把已经传完的大文件判成"120s 超时"。

现在的判定方式（`engine.js` 的 `uploadOne` / `_verifyRemoteFile`）：

1. 上传前先给目标目录拍一份 **fileId 快照**（`_snapshotIds`，分页取全量，
   最多 20 页 × 200 项）；
2. 通过网页版的 `<input type=file>` 触发上传（`setInputFiles`）；
3. 轮询云端列表，只认**上传前不存在的新 fileId**，并校验大小。
   同名旧文件因为 id 已在快照里，不会被误判成本次成功。

这个改动修掉两个致命 bug：云端已有同名同大小旧文件时会把失败判成成功
（永久不再重试 ⇒ 静默丢数据）；只查第一页时目录超 200 项就会把成功判成失败
（无限重试）。

---

## 4. captcha 约束

- captcha 签名算法**分片动态下发**，实测无法离线复刻，必须从网页版发出的
  真实请求里截取 `x-captcha-token` 与 `authorization`。
- 创建远程文件夹需要 **POST 专属** captcha：先触发一次真实上传（写一个
  `.probe.txt`）来截取，再发创建请求。
- 抓到的 header 会按方法缓存（`_hdrCache`）；遇到 400/401/403 时作废缓存并
  重试一次（`_apiGet` 的两轮循环），避免每页都重新截 captcha。
- 目录切换是 SPA 行为：`page.goto("?path=/X")` 只改 URL，前端不切换；
  必须 `click` 页面上 `<a title="X">` 才真正进入。因此每次上传前都重新
  `enterRemoteFolder(true)` —— 也顺便重建了页面上的 `<input type=file>`，
  避免旧元素句柄失效后 `setInputFiles` 写到已脱离文档的元素上。

---

## 5. 数据目录约定

**安装目录只放程序，一行数据都不写**（安装目录可能只读）。

| 路径 | 内容 |
|---|---|
| `%LOCALAPPDATA%\ThunderPanSync\` | 程序：exe、`engine\`、`runtime\`、`Uninstall.exe`、EULA |
| `%APPDATA%\ThunderPanSync\config.json` | 设置（监视目录、远程文件夹名、间隔、大小上限、版本策略） |
| `%APPDATA%\ThunderPanSync\state.json` | 已上传文件的指纹与失败计数 |
| `%APPDATA%\ThunderPanSync\sync-YYYY-MM-DD.log` | 按天切分的运行日志 |
| `%APPDATA%\ThunderPanSync\latest.log` | 指针文件，内容是当天日志的完整路径 |
| `%APPDATA%\ThunderPanSync\engine-stderr.log` | 引擎原始 stderr（排障用，有上限） |
| `%APPDATA%\ThunderPanSync\browser-profile\` | 浏览器配置与迅雷登录态 |
| `%APPDATA%\ThunderPanSync\cdp-port.txt` | 实际使用的调试端口 |

`paths.js` 是**唯一**的路径来源，优先级：
`PANSYNC_DATA_DIR` 环境变量 > Electron userData > `%APPDATA%\ThunderPanSync`。
三条入口（Electron 主进程 / node 宿主 / 测试脚本）必须看到同一个目录，
否则会出现"界面显示已上传、引擎却写在另一个 state.json"的分裂。

**state.json 用原子写**：先写 `.tmp` → `fsync` → `rename` 覆盖。直接
`writeFileSync` 覆盖时，断电或强杀会留下 0 字节/半截 JSON，下次启动解析失败
⇒ 状态清空 ⇒ 把用户几个 GB 全部重传（实测过的场景）。文件损坏时会先备份成
`state.json.corrupt-<ts>` 再从空开始，而不是静默当空。

日志按天分文件：常驻跑几个月后单文件会到几十 MB，定位"昨天那次失败"时
既打不开也搜不动。Windows 不能建软链，所以用 `latest.log` 当指针。

---

## 6. 动态调试端口

默认 `127.0.0.1:9222`，但该端口很可能被别的东西占着。`BrowserManager` 的规则：

1. 9222 空着 → 用它（向后兼容）；
2. 9222 上跑的就是本应用的浏览器（用 profile 里的 `DevToolsActivePort`
   里的 browser uuid 与 `/json/version` 比对）→ 复用；
3. 9222 被其他程序占用 → 依次试 9223..9233，取第一个空闲端口。

选定后把**实际端口**写进 `cdp-port.txt`，`engine/cdp.js` 读这个文件。
读不到文件、内容非法、或读到明显不对的值，一律**回退 9222** —— 保证老版本
行为不被破坏。这个文件可被本机任意程序改写，所以 CDP 连接前还会做一次
目标校验，避免被指向假 CDP。

> 安全前提：调试端口**没有认证**。本机任何程序都能连上去读这个浏览器的登录态。
> 这是 CDP 的固有特性，无法在保留功能的前提下消除，只能在文档和 EULA 里说清。

---

## 7. 构建流程

```powershell
.\build-native.ps1                  # 编译 C# → 组装 publish → 出 NSIS 安装包
.\build-native.ps1 -SkipInstaller    # 只到 publish
.\build-native.ps1 -RunAfterBuild    # 构建完直接启动
```

步骤（`build-native.ps1`）：

1. **编译 C#**：系统 `csc.exe`（`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`）。
   只支持 **C# 5** 语法 —— 不能用插值字符串、`?.`、元组、`out var`。
   `/out` 和 `/target` 必须写在源文件之前。
2. **组装 `native\publish\`**：exe、`runtime\node.exe`、`engine\host.js`、
   `engine\paths.js`、`engine\engine\{engine,cdp}.js`。**必须逐个指定目标子目录**，
   用 `File /r` 会把 `node.exe` 平铺到根目录，而 C# 找的是 `runtime\node.exe`。
3. **打 NSIS 安装包**：先补 UTF-8 BOM，再调 `makensis`。
   产物：`release\ThunderPanSync-Setup-1.0.0.exe`（约 25.7 MB）。

---

## 8. 编码陷阱（都真实踩过）

| 文件 | 要求 | 不遵守的后果 |
|---|---|---|
| `build-native.ps1` | **纯 ASCII**，一个非 ASCII 字节都不能有 | PowerShell 5.1 按系统 ANSI 码页读无 BOM 的 `.ps1`；注释里的中文会被误解码，破坏**后续 token 解析**，构建直接失败。检查：`($bytes \| ? { $_ -gt 127 }).Count` 必须是 0。 |
| `installer.nsi` | **UTF-8 带 BOM** | `makensis` 报 `Bad text encoding: installer.nsi:1`，整个构建中断。编辑器常静默丢 BOM，所以每次构建都重新补。 |
| `EULA.txt` | **UTF-8 带 BOM** | 被 `!insertmacro MUI_PAGE_LICENSE "EULA.txt"` 引用，同样会让 makensis 报 `Bad text encoding`；中文会变乱码。`build-native.ps1` 现在对 `.nsi` 和 `EULA.txt` 一起补 BOM。 |
| `README.md` | UTF-8 无 BOM | 带 BOM 时部分 Markdown 渲染器会把 BOM 当正文，或被 GitHub 当成字符显示。 |
| Node 源码（`.js`） | UTF-8 无 BOM | 有 BOM 时 V8 会把 BOM 当源码字符；`host.js` 的 JSON-RPC 通道也要求无 BOM。 |

**NSIS 字符串转义还有两个坑**（写卸载脚本时踩的）：

- NSIS 里 `$$_` 才是 PowerShell 的 `$_`。直接写 `$_` 会被 makensis 当作未知
  变量吞掉（只报 `warning 6000`，不失败），`$_.CommandLine` 变成空串
  ⇒ `Where-Object` 恒假 ⇒ 一个进程都杀不掉，而卸载日志看起来完全正常。
- NSIS **不会**把 `''` 折叠成一个单引号（那是 SQL 的习惯）。所以载荷里如果要
  用引号，统一用单引号，外层用 `$\"` 包住，载荷内部不出现任何双引号。

---

## 9. 卸载时的浏览器清理（安全红线）

卸载只做两件事关进程：

```nsis
ExecWait 'taskkill /IM ${APPEXE} /F'          ; 只杀本程序
```

浏览器**绝不能**用 `taskkill /IM msedge.exe` —— 那会把用户正在用的浏览器
一起杀掉，是严重事故。正确做法是按命令行过滤，只杀 profile 路径属于本应用的：

- 主路径：`nsExec` 调 PowerShell，`Get-CimInstance Win32_Process` 取
  `msedge.exe` / `chrome.exe`，命令里含 `%APPDATA%\ThunderPanSync\browser-profile`
  才 `Stop-Process`。
- 降级路径：PowerShell 被禁用/被精简时用 `wmic`。注意 **WQL 的 `LIKE` 里
  反斜杠必须写成 `\\`**，否则返回 `Invalid query`（实测）。

同样的规则也用在应用正常退出（`BrowserManager.ShutdownBrowser`）：先 Kill
自己记录的进程树，再用命令行过滤兜底处理 Edge 派生的子进程。

---

## 10. 云端版本策略

`versionsMode` 只有两个值，默认 `keep`：

- **`keep`（默认）**：只报告"云端已有 N 个旧版本"，一个字节都不删。
- **`replace`**：上传并核查成功之后，才删除**本轮开始前就已存在**的同名/同名(n)
  旧副本，且每轮最多删 `maxDeletePerRun`（默认 20）个。

删云端文件不可逆，所以默认必须是 `keep`，不能靠调用方记得关。任何非
`keep`/`replace` 的值一律按 `keep` 处理；`host.js` 在 `saveConfig` 里也再夹一次。

修订/熔断：连续失败按 `intervalMs * 2^n` 退避（上限 30 分钟），连续 5 轮失败
熔断、停止自动重试；手动"立即同步"始终可用（手动触发不写退避状态）。

---

## 11. 术语

- **captcha / token**：迅雷网页版请求头里的 `x-captcha-token` 与 `authorization`，
  本工具靠截取它们来调 API。
- **CDP**：Chrome DevTools Protocol，浏览器调试协议，本机 WebSocket。
- **fileId 快照**：上传前记录目标目录所有文件 id，上传后只认新增 id，
  用于"事实核查"而不是"相信响应"。
- **指纹（fp）**：`size-mtimeMs`，用于判定本地文件是否变过，避免重复上传。
