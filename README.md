# ThunderPanSync

把本地文件夹自动备份到**迅雷云盘**的 Windows 桌面应用。

原生 WinForms 界面（不是浏览器套壳），可执行文件约 50 KB。

> **先读这一节，再决定要不要用。** 这个工具的能力边界写得很清楚，请按它的实际能力使用。

## 它做什么

监视你指定的文件夹，文件发生变化后自动上传到迅雷云盘的指定目录。

| 能力 | 状态 |
|---|---|
| 监视多个文件夹，自动上传 | ✅ |
| 定时轮询（默认 60 秒） | ✅ |
| 幂等：未修改的文件不重复上传 | ✅ |
| 开机自启 | ✅ |
| 关闭窗口最小化到托盘，备份继续 | ✅ |
| 空文件跳过（云盘不接受 0 字节） | ✅ |
| 上传结果按云端实际文件核实，不靠浏览器响应 | ✅ |
| 实时进度条（第 x/y 个 · 文件名 · 已传/总量 · 速度 · 剩余时间） | ✅ |
| 失败自动退避 + 连续失败熔断 | ✅ |
| 日志按天分文件 | ✅ |
| 首次使用与安装时都有风险告知（EULA） | ✅ |

## 它不做什么（重要）

**这些限制是设计上的取舍，不是待修的 bug。请在依赖它之前确认能接受。**

| 限制 | 说明 |
|---|---|
| **没有恢复功能** | 只能上传，不能下载。本地硬盘坏了，你要自己去 [pan.xunlei.com](https://pan.xunlei.com) 手动找文件。这是单向备份器，不是双向同步盘。 |
| **不保留版本历史** | 文件更新后会再传一份，云盘里出现 `a.txt` 和 `a(1).txt`。默认行为是**只报告、不删除**云端旧版本（`versionsMode=keep`）；代码里存在一个 `replace` 模式可删旧版本，但**当前没有任何 UI 能开启它**，且删除需同时满足 7 个安全条件（模式显式开启、本轮已核实上传成功、id 属于上传前快照、不是本次新文件、同源文件名、本轮无配额/鉴权错误、未超单轮删除上限）。简言之：你用这个工具时，它不会删你云盘上的任何文件。 |
| **云端是单层目录** | 子目录结构不会保留。`照片\2023\a.jpg` 和 `照片\2024\a.jpg` 在云端会变成 `a.jpg` 和 `a(1).jpg`，**无法从名字判断来源**。检测到同名冲突时会在日志里明确告警。 |
| **单文件上限 2 GB** | 超过的直接跳过（可在设置里调大，但越大越慢）。 |
| **跳过点开头的目录** | `.git`、`.ssh`、`.config` 等不会被备份。 |
| **不跟随符号链接/junction** | 会统计并在日志里告警，但不会备份其内容。如需备份，请把目标目录直接加入监视列表。 |
| **拒绝系统目录** | `C:\Windows`、`Program Files`、`ProgramData` 等会被拒收，防止误选导致上传整个系统盘。 |
| **无删除同步** | 本地删了文件，云端保留（对备份来说这是正确行为）。 |

## 它是怎么工作的（以及为什么可能违反迅雷协议）

**这一点必须在用之前知道。**

迅雷云盘没有公开的上传 API。本工具的做法是：

1. 后台启动一个 Microsoft Edge（独立 profile，带 `--remote-debugging-port=9222`；端口被占用时会自动回退到 9223–9233，实际端口记录在数据目录）
2. 用 Chrome DevTools Protocol 连上去
3. 从网页版 `pan.xunlei.com` 发出的真实请求里截取 `x-captcha-token` 和 `authorization`
4. 上传时驱动网页版自己的文件选择框，让浏览器发出上传请求

**为什么不自己算 captcha：** 迅雷的 captcha 签名算法分片动态下发，实测无法离线复刻。

**风险披露：**

- 这可能违反《迅雷网盘用户协议》中关于「破解、去除技术保护措施」和「非授权衍生软件」的条款
- 可能触发风控，**账号存在被限制或封禁的可能**
- 迅雷接口变更会导致本工具失效
- 本工具不发送任何遥测数据，所有网络请求只发往 `api-pan.xunlei.com` 和 `pan.xunlei.com`

**请自行评估是否承担这个风险。** 如果只是想备份少量重要文件，手动上传更安全。

## 安全提示

应用会在本机启动一个带**调试端口**的 Edge（默认 9222，被占用时回退 9223–9233），该端口**没有认证**——本机上任何程序都能连上去，读取这个浏览器里的迅雷登录态。

实际使用的端口写在 `%APPDATA%\ThunderPanSync\cdp-port.txt`，引擎读它来连浏览器；
读不到时回退 9222。

这是 CDP 协议的固有特性。为了降低风险：

- 应用使用**独立 profile**（`%APPDATA%\ThunderPanSync\browser-profile`），不碰你日常浏览器的登录态
- 退出应用时会终止自己启动的那个 Edge（只杀带本应用 profile 的进程，不动你自己的浏览器）
- 卸载时同样只按 profile 路径过滤清理，**绝不** `taskkill /IM msedge.exe`

如果本机存在不可信的软件，不建议使用本工具。

## 安装

下载 `ThunderPanSync-Setup-1.0.0.exe`，双击安装。

- 装到 `%LOCALAPPDATA%\ThunderPanSync`，**不需要管理员权限**
- 会创建桌面和开始菜单快捷方式
- 卸载走「设置 → 应用」，会问你是否删除配置和登录态

**安装包没有代码签名。** 首次运行 Windows 会弹「Windows 已保护你的电脑」，需要点「更多信息 → 仍要运行」。这是未签名程序的正常提示。

## 使用

1. 启动后点「需要登录迅雷云盘」，在弹出的浏览器窗口里登录（只需一次）
2. 点「添加文件夹…」选择要备份的目录
3. 点「开始自动备份」
4. 关窗口 = 最小化到托盘，备份继续

**状态条三种颜色：**

- 🟢 `● 备份中` — 正常工作
- 🔴 `● 未在备份（点「开始自动备份」）` — 有目录但没在跑，**这时候你的文件没有在被备份**
- 🟡 `● 尚未配置（请添加文件夹）`

**进度条：** 同步时显示 `第 3/12 个 · a.jpg · 1.2 MB / 8.4 MB · 300 KB/s · 剩余 12 秒`，
空闲时显示「空闲」。

**失败与退避：** 「失败的文件」列表给出每个文件的原因和失败次数。连续失败会
自动退避（间隔翻倍，最长 30 分钟）；连续 5 轮失败会**熔断**，停止自动重试并在
日志里说明。此时点「立即同步一次」仍然可用——熔断只是停掉自动重试，不是锁死。

## 安装时的风险告知

安装程序在欢迎页之后会显示一份 EULA（也装到安装目录 `EULA.txt`），必须点
「我接受」才能继续。首次运行应用时还会再弹一次同样的告知。

它明确写了四件事，请在安装前确认能接受：

1. 本工具通过 CDP 截取网页版的 captcha 与 token 来上传，**可能违反迅雷用户协议**
2. 账号**存在被限制或封禁的可能**
3. 本工具**没有恢复功能**，不能把云盘上的文件取回本地
4. 会在本机启动一个带调试端口（默认 9222，**无认证**）的浏览器

## 日志

运行日志按天分文件：`%APPDATA%\ThunderPanSync\sync-YYYY-MM-DD.log`。
界面上「打开日志文件」打开的是当天那个文件。`latest.log` 是一个指针文件，
内容为当前活动日志的完整路径（Windows 不能建软链）。
另有 `engine-stderr.log` 保存引擎原始输出，排障时用；它有大小上限，不会无限增长。

## 从源码构建

需要：

- **.NET Framework 4.8**（Win10/11 自带，提供 `csc.exe`）
- **Node.js 18+**（构建时用来确定运行时来源，会被打进安装包）
- **NSIS 3.x（makensis）** —— 只有生成安装包时才需要。脚本按下面顺序找：
  1. `%LOCALAPPDATA%\electron-builder\Cache\nsis-3.0.4.1\nsis-3.0.4.1-1mpw4\makensis.exe`
  2. `PATH` 里的 `makensis`

  如果两者都没有，`-SkipInstaller` 仍然可用（只产出 exe 与发布目录）；不加该开关会以
  `makensis not found` 失败。

```powershell
# 一键构建（编译 C# → 组装发布目录 → 生成 NSIS 安装包）
.\build-native.ps1

# 只构建目录，不生成安装包（不需要 NSIS）
.\build-native.ps1 -SkipInstaller

# 构建完直接启动
.\build-native.ps1 -RunAfterBuild
```

产物在 `release\ThunderPanSync-Setup-1.0.0.exe`。

`installer.nsi` 与 `EULA.txt` 必须是 **UTF-8 带 BOM**，否则 `makensis` 报 `Bad text encoding`。
多数编辑器保存时会丢掉 BOM，所以 `build-native.ps1` 每次构建前自动补 ——
但如果你手工直接跑 `makensis`，要自己确保 BOM 在。

构建脚本是纯 ASCII 的——Windows PowerShell 5.1 会按系统 ANSI 代码页读取无 BOM 的 `.ps1`，注释里的中文会破坏后续 token 解析（这个坑踩过）。

## 架构

```
ThunderPanSync.exe          50 KB   C# WinForms 界面（原生控件）
  ├─ EngineHost.cs                  stdio JSON-RPC 客户端，管理 Node 子进程
  ├─ BrowserManager.cs              启动/隐藏/清理后台 Edge，选调试端口
  └─ MainForm.cs                    界面 + 托盘 + 开机自启 + 进度 + EULA
runtime\node.exe            100 MB  内嵌 Node 运行时（MIT，见 THIRD_PARTY_NOTICES）
engine\host.js                      JSON-RPC 服务端
engine\paths.js                     数据路径（一律 %APPDATA%，安装目录只读）
engine\engine\engine.js             同步引擎（扫描/上传/核查/版本策略）
engine\engine\cdp.js                自研 CDP 客户端（替代 playwright，用 Node 内置 WebSocket）
```

界面与引擎通过 **stdio JSON-RPC** 通信——不占端口、不触发防火墙、随主进程退出。

**为什么不用 Electron：** 试过。239 MB，界面是 Chromium 渲染的网页。换成原生 WinForms 后主程序约 50 KB，界面是真 Windows 控件。

## 数据位置

- 配置/日志/状态：`%APPDATA%\ThunderPanSync\`
  - `config.json` 设置　`state.json` 已上传记录（原子写，断电不会写坏）
  - `sync-YYYY-MM-DD.log` 按天分文件　`latest.log` 指向当天日志
  - `engine-stderr.log` 引擎原始输出　`cdp-port.txt` 实际调试端口
- 浏览器登录态：`%APPDATA%\ThunderPanSync\browser-profile\`
- 安装目录只放程序，不写任何数据

`state.json` 用「写临时文件 → fsync → rename」的原子替换。直接覆盖写时，断电或
强杀会留下 0 字节/半截 JSON，下次启动解析失败就会把已备份的内容全部重传。
文件真的损坏时会先备份成 `state.json.corrupt-<时间戳>` 再从空状态开始。

## 更多文档

架构细节见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)：三层结构、stdio JSON-RPC
协议格式、上传三段式、captcha 约束、数据目录约定、构建流程、编码陷阱。

## 许可

MIT，见 [LICENSE](LICENSE)。内嵌的 Node.js 运行时遵循其 MIT 许可，完整声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

**本项目与迅雷（Xunlei）无关，未获其授权。** 「迅雷」「迅雷云盘」是其权利人商标，此处仅用于说明本工具的用途。
