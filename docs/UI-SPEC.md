# ThunderPanSync 界面重构规格（已实测，非推测）

> 这份文档只记录**已经实测验证过的技术事实**和**已确定的设计决策**。
> 每条事实后面注明验证方式。不要基于"应该可以"去实现。
>
> ⚠️ **行号是本轮重构进行中的快照（最后一次核对 2026-10-27 03:50）**，`native\*.cs`
> 与 `engine.js` 正在被多个 agent 同时改，行号会漂移。**引用代码位置时以函数名/符号为准**，
> 行号只作辅助。文档里出现"约 L####"的地方都已按此原则重写。

---

## 一、已实测的技术事实（这些是地基，不要重新验证）

### 1.1 Win10 上 DWM 深色标题栏 API 无效 —— 必须自绘

像素级实测（窗口置前 + PrintWindow，采样标题栏平均亮度）：

| 调用 | 返回 | 标题栏实测亮度 | 结果 |
|---|---|---|---|
| baseline | — | 250 `#BCDEFF` | LIGHT |
| `DwmSetWindowAttribute(h, 20, 1)` | **hr=0（成功）** | 250 `#BCDEFF` | **完全没变** |
| `DwmSetWindowAttribute(h, 19, 1)` | hr=-2147024809 | 250 | 无效（E_INVALIDARG） |
| `DwmSetWindowAttribute(h, 35, ...)` CAPTION_COLOR | hr=-2147024809 | 250 | Win11 才有 |
| `DwmSetWindowAttribute(h, 36, ...)` BORDER_COLOR | hr=-2147024809 | 250 | Win11 才有 |

**结论：Win10 19045 上系统标题栏无法通过 API 变深。必须自绘。**
（本机 `AppsUseLightTheme=0` 已是深色模式，仍然无效。）

### 1.2 自绘标题栏已实现并验证有效

`native/DarkForm.cs` —— `Form` 的派生基类，`FormBorderStyle=None` + 全部自绘。

实测结果：`top-area avg luminance = 35`（目标 `#1C1F24` = L31）。修复前是 250。

已实现并验证的细节（改动时不要破坏这些）：
- `CreateParams` 手工补 `WS_THICKFRAME`(0x40000) / `WS_MINIMIZEBOX`(0x20000) / `WS_MAXIMIZEBOX`(0x10000)
  —— `FormBorderStyle=None` 会去掉它们，导致无阴影、不可边缘缩放
- `WM_NCHITTEST`：边缘 6px 返回 HTLEFT/HTRIGHT/HTTOP/HTBOTTOM + 四个角；标题栏返回 `HTCAPTION`（免费得到拖动、双击最大化、Aero Snap）
- `WM_GETMINMAXINFO`：最大化时用 `rcWork` 而不是 `rcMonitor`，**否则会盖住任务栏**
- 三个按钮（最小化/最大化/关闭）自绘，关闭按钮 hover 是 `#C43E36` 红底白字
- `Application.Run` 路径下验证过（`Show()` + 手动泵不起作用）

### 1.3 `Environment.OSVersion` 会撒谎

```
无 manifest 时：Environment.OSVersion.Version.Major == 6   ← 撒谎（实际是 10.0.19045）
有 manifest 后：仍不可靠，用 RtlGetVersion
```

`Draw.WindowsBuild` 用 `RtlGetVersion`（ntdll），实测返回 **19045**（正确）。
`Draw.SupportsDarkMode` = `WindowsBuild >= 17763`。

**任何系统能力判断都必须用 `Draw.WindowsBuild`，不许用 `Environment.OSVersion`。**

### 1.4 app.manifest 必需

`native/app.manifest` 已创建，含：
- `dpiAwareness = PerMonitorV2,PerMonitor`
- `dpiAware = true/pm`
- `supportedOS` 含 Win10 GUID `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`
- `requestedExecutionLevel level="asInvoker"`（不要求管理员）

`build-native.ps1` 已加 `/win32manifest:` 参数。

### 1.5 深色滚动条：必须显式设置，且时机在 Handle 之后

`Draw.TryDarkScrollbars(ctrl)` → `SetWindowTheme(h, "DarkMode_Explorer", null)`
→ 实测滚动条从亮白变深灰。**必须在 `Shown` 或 `HandleCreated` 之后调用**，构造期调用无效。

### 1.6 自绘控件的构造期陷阱（踩过，会静默失效）

`StatChip.Fit()` / `StatusPill.Fit()` 早期用 `CreateGraphics()` 测宽，但构造期 Handle 未建 → 抛异常被吞 → **Width 保持 0 → 控件完全不可见**。

修法：用 `TextRenderer.MeasureText(string, Font, Size, flags)` 静态重载（不需要 Handle），并在 `OnHandleCreated` 里再量一次。

**任何自绘控件的尺寸计算都不许在构造期依赖 `CreateGraphics()`。**

### 1.7 现有视觉层文件（已编译进产品）

> 下表的行数是**重构开始前**的实测值（2026-10-26）。重构期三个文件同时在改，行数会变，以文件实际内容为准。

| 文件 | 行数 | 内容 |
|---|---|---|
| `native/Theme.cs` | 184 | 颜色令牌、字号、间距、`S()` DPI 缩放、`Contrast()` WCAG 计算 |
| `native/Drawing.cs` | 321 | 圆角路径、胶囊、卡片、进度条绘制、文本、深色滚动条、`WindowsBuild` |
| `native/Widgets.cs` | 537 | `CardPanel` / `FlatButton`(4种Kind) / `FlatProgress`(确定+不确定) / `StatusPill`(6态) / `StatChip` / `SegmentedTabs` |
| `native/DarkForm.cs` | 316 | 自绘深色标题栏基类 |
| `native/UiPreview.cs` | 174 | **开发期自检窗口，不进产品构建**（有独立 Main） |

编译产物：**81,920 B**（2026-10-27 03:25 用 `build-native.ps1 -SkipInstaller` 实测；重构期每次改动都会变，别把数字写死到文档/README 的绝对承诺里）。

### 1.8 配色令牌（`Theme.cs` 已定义，对比度已验证）

| 令牌 | 值 | 用途 |
|---|---|---|
| `BgWindow` | `#181A1E` | 窗口底 |
| `BgSurface` | `#202328` | 卡片 |
| `BgSurfaceAlt` | `#262A30` | 次级面板 |
| `BgBar` | `#1C1F24` | 状态条/底栏/标题栏 |
| `BgInset` | `#14161A` | 输入框/日志 |
| `TextPrimary` | `#EBEEF2` | 主要文字（对比度 13.4:1） |
| `TextSecondary` | `#A8B0BC` | 次要文字（7.4:1） |
| `TextTertiary` | `#7C8490` | 弱化（4.6:1，刚好达标） |
| `Ok` | `#4AC27A` | 成功/备份中 |
| `Warn` | `#E6B044` | 警告/未配置 |
| `Danger` | `#E26058` | 危险/未备份 |
| `Accent` | `#488ADC` | 主按钮 |

**不许再用 `Color.Gainsboro` / `Color.Gray` / `Color.Gold` / `Color.Salmon` / `Color.LightGreen`** —— 这些命名色饱和度是 .NET 拍脑袋定的，在深色背景上刺眼且对比度不达标（原 `Color.Gray` 在 `#181A1E` 上只有 4.23:1）。

---

## 二、已确定的信息架构决策

### 2.1 主状态只有 5 个（原设计 6 个，`LoginRequired` 无数据源已删），且必须单一来源

```csharp
internal enum UiState {
    Error,          // 引擎没跑（抢占态，优先级最高）
    NotConfigured,  // 没有监视目录
    LoginRequired,  // 有目录但未登录
    Paused,         // circuitOpen == true（熔断）
    Running,        // running == true 且未熔断
    Ready           // 有目录、running == false
}
```

> **⚠️ 实施口径（2026-10-27 核对）：`LoginRequired` 没有数据源，不实现。**
> `host.js` 的 `status` 处理器返回 `{config, status, dataDir, configFile, logFile, latestLogPointer, pid}`，
> 而引擎 `getStatus()` 里**没有 `loggedIn` 字段**——界面无法得知"有目录但未登录"。
> 因此实际实现是 **5 态**：`Error / Paused / NotConfigured / Running / Ready`，
> **登录态降级为状态条的副信息**（由 `BrowserManager` 自己的探测结果驱动，不参与 UiState 派生）。
> 不要为了凑 6 态去猜登录状态：猜错就是"绿着但没备份"的同类缺陷。

优先级从高到低。**所有区域（状态胶囊文案/主按钮/进度区可见性/失败区可见性/托盘 tooltip/托盘菜单）只从 UiState 派生，不许各自 if-else。**

两条硬规则：
1. **`circuitOpen == true` 时禁止显示绿色、禁止显示进度条**。当前 `ApplyStatus` 里熔断时 `running` 仍为 true 会走"备份中"分支（`ApplyStatus()` 的 `if (circuitOpen) / else if (running)` 链，2026-10-27 核对时在 `MainForm.cs:1175-1189`），这是必须堵死的复发点。
2. **不显示引擎没给的数据**。`nextRetryAt` 是**轮次号**不是时间戳（`engine.js:752`），绝不能显示成时间。`pausedReason` 只有固定串，没有原因区分。

### 2.2 状态条：从 5 个指标收敛为 1 个状态 + 1 行证据

**删掉**：`文件夹 0`（数字与列表重复）、`已上传 0`（合并进副行）、`失败 0`（只在 >0 时以红色 chip 出现）、`远程 待创建`（引擎实现细节）。

**保留**：
- 主行：`StatusPill`（形状+文字+颜色三重编码，色盲友好）
- 副行：`上次备份 14:02 · 已上传 1,284 个 · 目标 迅雷云盘 / ThunderPanSync`

`●` emoji 必须删掉（字体 fallback 导致宽度漂移、屏幕阅读器读作"黑色圆形"、无法做呼吸动画）。

### 2.3 面积分配：日志不再是 50%

失败区是**条件卡片**，三档高度：
- 失败=0 → **高度 0**（`Visible=false`），日志拿满
- 失败>0 → **44px** 一行摘要（含原因摘要 + 展开按钮）
- 用户展开 → **156px**（3 行 + 表头）

日志区因此从 ~50% 涨到 **57–67%**。删掉 `SplitContainer`（不可拖就不需要 SplitterDistance）。

### 2.4 按钮：全局唯一 Primary，动词随状态变

| UiState | 主按钮 | 次按钮 | 本轮实施 |
|---|---|---|---|
| Error | 重试连接（原「重启引擎」） | 打开日志 | ⚠️ 见下 |
| NotConfigured | 添加文件夹… | — | 做 |
| ~~LoginRequired~~ | （态已取消，见 §2.1） | — | 不做 |
| Paused | 立即同步一次（替代「立即重试一次」） | 查看失败原因 · 关闭自动备份 | 做 |
| Running | — | 查看详情 | ⚠️ 见下 |
| Ready | 开始自动备份 | 立即同步一次 | 做 |

**⚠️ 三个按钮本轮做不了 —— 引擎侧没有对应命令**（`host.js` 只有 `status/scan/runOnce/start/stop/saveConfig/connect/quit`）：

| UI-SPEC 原要求 | 现实 | 处置 |
|---|---|---|
| `重启引擎` | 没有 `restart` 命令 | 本轮不做；Error 态的主按钮改为「重试连接」（复用 `connect`），标 v2 |
| `取消本轮` | 没有 `cancel` 命令；`runOnce` 不可中断 | 本轮不做，标 v2。用户能用的替代是「停止自动备份」，**但文案必须说清它同时关掉自动备份** |
| `重试这个`（§2.6 失败列表动作） | 没有 `retryFile` 命令 | 本轮不做，标 v2；失败列表整行的动作降级为「立即同步一次」（= 重跑一轮，会重试该文件） |

**`已登录 ✓` 不再是按钮** —— 它是状态。放副行末尾（弱化灰）或未登录时升级为黄色横幅。

### 2.5 进度：绝不给假速度

**当前速度算法是假的**（`ApplyProgress()` 里 `if (elapsedMs > 500 && bytesDone > 0)` 分支，2026-10-27 核对时在 `MainForm.cs:968-988`）：
```csharp
double bps = (double)bytesDone * 1000.0 / (double)elapsedMs;
// bytesDone = 本轮其他已完成文件的字节；elapsedMs = 当前文件的耗时
// 分子分母不同源 → 换文件瞬间剧烈跳变
```
**必须删除速度显示。**

进度条语义 = **文件个数**，不是字节。引擎单文件生命周期 `2.5s 进目录 → 7~11s 快照 → 上传 → ≤18s 核对`，`progress` 只在文件开始/核对/结束发 —— 大文件上传期间**零字节数据**，任何字节百分比都是编的。

ETA 按文件数外推，且必须同时满足 `total>=3 && 成功>=2 && 无失败` 才显示。

「没卡住」的 4 个信号：
1. UI 自己的 1 秒 Timer 驱动"这一项已用 0:38"秒表（**不依赖引擎事件**）
2. 阶段在变（准备→上传→核对云端）
3. 「最后一条引擎消息：8 秒前」（>30s 转灰，>3min 转橙）
4. tooltip 解释"网页版上传拿不到实时进度"

### 2.6 失败列表改"待办清单"

列：`文件 | 原因(人话) | 何时 | 状态 | 怎么办(按钮)`

原因→动作映射（按引擎错误码）：

| 码 | 人话原因 | 按钮 |
|---|---|---|
| AUTH | 迅雷登录失效 | 重新登录 |
| BROWSER | 后台浏览器没起来 | 重新启动浏览器 |
| QUOTA | 云盘空间不足 / 文件超上限 | 打开云盘清理 |
| NETWORK | 网络断了 | 现在重试 |
| PAGE | 云盘网页可能改版了 | 打开日志 |
| DISK | 读不到文件（被占用/没权限） | 打开所在文件夹 |
| UNKNOWN | 说不清原因（已记录） | 重试这个 |

**`failedFiles` 只返回前 50 条**（`getStatus()` 里的 `Object.entries(this.state.failed).slice(0, 50)`，2026-10-27 核对时在 `engine.js:1619`）—— `failedTotal > 50` 时标题必须写「显示前 50 个（共 N 个）」。

### 2.7 保留 Tab，设置留在主窗口内（原方案已否决）

**裁决：保留 `SegmentedTabs`（备份 | 设置），不做独立 `SettingsForm` 模态对话框。**

否决理由是成本收益：模态窗要新增 ≥150 行代码、多一个 DPI 面（§3.3 的 `Theme.S()` 要在新窗体里重新初始化）、
多一套"设置改了要不要重启引擎"的状态同步；而收益只是"少一个 Tab 的套壳观感"——
Tab 已经换成自绘 `SegmentedTabs`（不是原生 `TabControl`，见 `Widgets.cs`），深色下没有白边问题。

- 主窗口两页：`备份` / `设置`，用自绘 `SegmentedTabs` 切换
- 「保存设置」在主窗口的页内，**只对设置页生效**（不会把当前同步状态写回配置）
- 底栏不再放设置齿轮（没有独立设置窗）

> 原文"删掉 TabControl，设置进独立模态 `SettingsForm`(640×560)，主窗口不存在任何保存语义"**作废**。

### 2.8 空状态 = 三步清单 + 唯一 CTA

```
① ○ 选择文件夹      ← 当前步
② ○ 登录迅雷云盘
③ ○ 开始自动备份

[ 选择文件夹… ]     ← 全局唯一 Primary

仅上传新增文件 · 不会删除本地文件 · 无恢复功能
```

不用模态向导（备份工具会最小化到托盘，模态向导被中断就无法恢复）。

---

## 三、WinForms 布局技术规范

### 3.1 Dock 顺序（写反了不报错，只是控件被遮住）

```
Controls.Add(content)    // Fill  ← 必须第一个
Controls.Add(footer)     // Bottom
Controls.Add(progress)   // Top
Controls.Add(header)     // Top
```

**禁止用 `BringToFront()`** —— 现状 `BuildMainTab()` 末尾有 3 行 `BringToFront()` 是在打补丁。一律用 `Controls.SetChildIndex(ctrl, n)` 显式声明。

### 3.2 卡片圆角与 Dock=Fill 子控件冲突

`CardPanel` 自绘圆角，而 `Dock=Fill` 的子控件是矩形，会盖住 4 个圆角。
→ 卡片 `Padding` 必须 ≥ 圆角半径（6px），**实际用 12px**。

### 3.3 单一 DPI 缩放源

**禁止 `AutoScaleMode.Font` + `Theme.S()` 混用**（双重缩放，125% 下变 156%）。

本项目选：`AutoScaleMode = AutoScaleMode.None` + 全部尺寸走 `Theme.S()`。
`Theme.InitScale(g.DpiX / 96f)` 在窗体创建后调一次。

### 3.4 表单用 TableLayoutPanel，别用绝对坐标

现状 `BuildSettingsTab()` 用 `Location(20, y)` + `y += 40`，150% 缩放下必然错位。

TableLayoutPanel 坑：
- 单元格内控件默认 `Margin=3`，必须显式 `Margin=0`
- `AutoSize=true` 的 Label 会撑开列宽 → 必须 `AutoSize=false` + `AutoEllipsis=true`

### 3.5 ListView 列宽不许写死

现状写死 `240/50/420`。必须在 `OnResize` 里按比例重算，否则高 DPI 下内容截断。

---

## 四、明确的负向清单（不要做）

- ❌ Mica / Acrylic 背景（需 Win11 22621+ 合成器）
- ❌ 旋转动画 / Lottie（WinForms Timer 精度 15ms 且跑 UI 线程，必掉帧，掉帧比不转更廉价）
- ❌ 卡片投影（GDI+ 多次半透明叠加出现色带）
- ❌ 列表项 hover 过渡动画（每帧全列表重绘会闪）—— 静态 hover 就够
- ❌ 圆角窗口手绘（用 `Region` 边缘锯齿且失阴影；Win11 用 `DWMWA_WINDOW_CORNER_PREFERENCE`，Win10 就不做）
- ❌ 拖拽排序文件夹（第一版用上移/下移按钮）
- ❌ Toast 浮动错误提示（**会消失的告警 = 没有告警**）
- ❌ 在状态条放百分比/环形图（备份是持续任务，没有"总进度"）
- ❌ `Color.Gainsboro` / `Color.Gray` / `Color.Gold` / `Color.Salmon` / `Color.LightGreen`
- ❌ `Environment.OSVersion` 判断系统能力

---

## 五、构建与验证

```powershell
# 编译（不生成安装包）
powershell -NoProfile -ExecutionPolicy Bypass -File build-native.ps1 -SkipInstaller

# 完整构建
powershell -NoProfile -ExecutionPolicy Bypass -File build-native.ps1

# 回归测试
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

**注意**：`build-native.ps1` 必须保持**纯 ASCII**（PowerShell 5.1 按 GBK 读无 BOM 的 .ps1，中文会破坏 token 解析）。

---

## 六、如何肉眼验证界面（不用截图工具）

`native/UiPreview.cs` 是独立自检窗口，可编译运行看视觉效果：

```powershell
$csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:"$env:TEMP\preview.exe" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Net.Http.dll `
  native\Theme.cs native\Drawing.cs native\Widgets.cs native\UiPreview.cs
```

抓图用 `PrintWindow(hwnd, hdc, 2)`（`PW_RENDERFULLCONTENT`），**不要用 `CopyFromScreen`** —— 用户屏幕常年被全屏游戏覆盖，会截到游戏画面。

采样验证颜色用像素平均亮度：深色应 < 80，浅色 > 150。
