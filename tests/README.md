# ThunderPanSync 回归测试（WP5）

本目录是**纯测试代码**，不参与打包，也不被产品代码引用。所有测试都不修改产品代码，
只通过公开接口（`require` 引擎、`scan()`、`runOnce()`、`_remoteNameFor()`、`log` 事件）验证行为。

## 快速开始

```powershell
# 全部用例（单元 + 真实上传；没有浏览器时上传用例自动 SKIP）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1

# 只跑单元级（不需要浏览器，永远可跑）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1 -UnitOnly

# 只跑真实上传级
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1 -UploadOnly
```

退出码语义：**有 FAIL → 1；否则 → 0**（PASS 与 SKIP 都算 0）。

输出格式（每行一个用例 + 末尾汇总）：

```
[PASS] scan-empty-dir - empty dir -> 0 items
[FAIL] scan-zero-byte-skip - 0-byte file has no skip reason (skip=null)
[SKIP] upload-runonce - prerequisite missing: cannot reach http://127.0.0.1:9222/json/version (ECONNREFUSED)

TOTAL=12 PASS=11 FAIL=1 SKIP=0
```

## 文件

| 文件 | 作用 |
| --- | --- |
| `run-tests.ps1` | 测试运行器。发现 node、启动子进程、解析结果、打印 PASS/FAIL/SKIP 与汇总、决定退出码 |
| `test-unit.js` | 7 个单元级用例，每个子进程只跑一个用例（`node tests/test-unit.js <case-id>`） |
| `test-upload.js` | 3 个真实上传用例，一次进程跑完（探测 → 上传 → 幂等） |

两个 Node 脚本都用 `@@PASS@@|用例名|详情` 这种机器可读行与运行器通信。运行器把
**没有产出结果行**的用例判为 FAIL（子进程崩溃/被杀不会伪装成 PASS）。

## 硬约束：run-tests.ps1 必须纯 ASCII

PowerShell 5.1 读**无 BOM** 的 `.ps1` 用的是系统 ANSI 码页（中文系统是 cp936），
文件里出现非 ASCII 字节会破坏后续 token 解析，脚本直接语法错误 —— `build-native.ps1`
已经踩过这个坑。所以：

- 运行器里的注释、字符串一律英文；
- 运行器启动时**自检**自己的字节，发现 `>127` 的字节直接报
  `[FAIL] runner-ascii`，让问题当场暴露而不是等到某台机器上神秘失败。

改动 `run-tests.ps1` 后请自己确认：

```powershell
$b = [IO.File]::ReadAllBytes('tests\run-tests.ps1')
($b | Where-Object { $_ -gt 127 }).Count   # 必须是 0
```

（`test-unit.js` / `test-upload.js` 是 Node 脚本，按 UTF-8 读，可以写中文。）

## 单元级用例（不需要浏览器）

| 用例名 | 覆盖的行为 / 防的 bug |
| --- | --- |
| `engine-require-construct` | `engine/engine.js` 可被 require；`SyncEngine` 可构造；继承 `EventEmitter`；12 个公开方法存在；`opts` 能覆盖 `stateFile`/`logFile`/`settleMs`（测试隔离的前提） |
| `scan-empty-dir` | 空目录 `scan()` 返回 0 项，不产生任何记账 |
| `scan-zero-byte-skip` | 0 字节文件的 `skip` 必须说明原因且含「0 字节」，且**不给 fingerprint**（云盘拒收 0 字节，这是已知跳过而非失败） |
| `scan-future-mtime-not-pending` | **未来时间戳 bug**：mtime 设为 2030 年时 `skip` 不能是「刚修改，等待稳定」。原逻辑 `now - mtime` 为负 ⇒ `< settleMs` 恒成立 ⇒ 从 zip 解压 / 跨机拷贝 / `robocopy /COPY:DAT` 带来的未来时间戳文件**永久漏备** |
| `scan-system-dir-blocked` | **系统目录防护**：`watchDirs` 指向 `C:\Windows\System32` 时 `scan()` 返回 0 项，且必须通过 `log` 事件留下拒绝原因（不能静默返回空数组让用户以为备份成功） |
| `remote-name-basename` | `_remoteNameFor()` 对子目录文件返回**纯 basename**。若返回带路径前缀的名字，云端核查永远找不到文件 ⇒ 每个文件空等超时 |
| `state-corrupt-recovery` | **state 损坏恢复**：写入半截 JSON 后 `new SyncEngine(...)` 不抛异常、产生 `state.json.corrupt-*` 备份（内容与原文件一致）、状态重置为空。静默当空会导致全量重传几个 GB，直接崩则用户无法启动 |

用例都用 `fs.mkdtempSync(os.tmpdir())` 建独立工作区，`PANSYNC_DATA_DIR` 也指向临时目录，
**不会碰 `%APPDATA%\ThunderPanSync`**。

## 真实上传用例（需要 9222 已登录 Edge）

**前置条件**：本机有一个带 `--remote-debugging-port=9222` 启动、且已登录
`pan.xunlei.com` 的 Edge（`start-browser.cmd`）。判据是 `sessionid` cookie 存在。

| 用例名 | 覆盖的行为 |
| --- | --- |
| `cdp-9222-endpoint` | `http://127.0.0.1:9222/json/version` 与 `/json/list` 可达、返回合法 JSON、有 `webSocketDebuggerUrl`、`pan.xunlei.com` 页面存在、`sessionid` cookie 存在 |
| `upload-runonce` | 在 `%TEMP%` 建 2 个文件（小文本 + 64KB 二进制，mtime 回拨 5 分钟避免判「刚修改」），`runOnce()` 后断言 `stats.failed == 0` 且 `stats.uploaded >= 2`，并确认 stateFile 真的落盘且 `uploaded` 有 ≥2 条 |
| `upload-idempotent` | 紧接着再跑一次 `runOnce()`，断言 `stats.uploaded == 0`（指纹匹配 ⇒ 不重复上传） |

**会 SKIP 的情况**（明确输出 `SKIP`，绝不伪造 PASS）：

- 9222 端口不可达（`ECONNREFUSED` / 超时）；
- 端口通了但 `/json/version` 不返回 `webSocketDebuggerUrl`；
- 浏览器里没有 `sessionid` cookie（未登录）；
- `runOnce` 在「还没开始上传任何文件」的阶段就失败（连不上浏览器、建不了远程文件夹）——
  这是环境问题，不是产品回归；
- `runOnce` 超过 180 秒（判为 SKIP 而不是 FAIL，避免慢网络被误报成产品缺陷）。

只有「真的开始传了但失败 / 传了数量不对 / 第二轮又重复上传」才算 FAIL。

### 副作用与安全边界

> **真实上传用例会往你的迅雷云盘写文件。**

- 落点是 `remoteFolderName` 指定的文件夹（默认 `ThunderPanSync`），文件名带毫秒时间戳：
  `tps-test-<timestamp>-note.txt` 和 `tps-test-<timestamp>-blob.bin`，避免与已有文件撞名
  （撞名会让「幂等」判定失真）。
- 测完**不会**自动删除云盘上的这两个文件（没有安全的删除接口，误删风险大于收益）。
  需要清理请手动在云盘里删 `tps-test-*`。
- `stateFile` / `logFile` / `PANSYNC_DATA_DIR` **全部指向 `%TEMP%` 下的临时目录**，
  跑完即删。绝不读写 `%APPDATA%\ThunderPanSync\state.json`（那是用户真实备份进度，
  污染它会让用户全量重传或误判已备份）。
- 自定义端口：设 `TPS_CDP_PORT` 环境变量（默认 9222）。
  自定义远程文件夹：设 `TPS_REMOTE_FOLDER` 环境变量（默认 `ThunderPanSync`）。

## 关于「基线未稳定」

多个 Agent 并行修改 `engine/engine.js` 时，测试可能因为别人**尚未完成**的改动而失败。
这种情况下请看失败原因里是不是 `require(engine/engine.js) failed` 或方法缺失 ——
如果是，说明是基线问题而非测试逻辑问题，等引擎改完后重跑即可。

测试刻意只依赖**稳定接口**，避免与并行改动打架：

- 日志断言走 `log` 事件（`engine.on('log', ev => ev.msg)`），
  **不**读日志文件 —— 新版 `log()` 会按天把 `cfg.logFile` 改指到 `paths.js`
  的 `getLogFile()`，文件位置不再由 `opts.logFile` 决定；
- 不假设 `state.json` 的具体字段（老版本没有 `versions`），只检查 `uploaded` 这种
  `_loadState()` 明确兜底的字段；
- 不依赖 `skip` 的确切文案完整性，只要求含关键子串（如「0 字节」「刚修改」），
  文案微调不会误报。

## 已知限制

- 未做 UI（C# WinForms）端到端测试：需要交互式桌面，无法在 CI/无人值守下稳定跑。
- 未覆盖 `stop()` 中断语义、退避/熔断、云端删除（replace 模式）等需要真实云盘状态的路径。
- `upload-runonce` 依赖真实网络，耗时约 10~30 秒（实测 15.3 秒）；单文件上传 1~3 秒。
- 超时值可用 `-UnitTimeoutSec` / `-UploadTimeoutSec` 调整（默认 120 / 420 秒）。
