// host.js -- 引擎宿主：给原生 C# 前端提供 JSON-RPC over stdio
//
// 为什么用 stdio 而不是 HTTP：
//   1. 原生前端用 Process.RedirectStandardInput/Output 就能驱动，零端口、零防火墙、零端口冲突
//   2. 子进程随父进程生命周期结束（C# 端 kill 就干净退出），不会有孤儿进程
//   3. 不占 18766 端口，用户机器上不会出现"端口被占用"
//
// 协议（每行一个 JSON 对象，UTF-8 无 BOM）：
//   → {"id":1,"cmd":"status"}
//   ← {"id":1,"ok":true,"data":{...}}
//   ← {"event":"log","data":{"level":"info","msg":"...","time":"..."}}   // 主动推送，无 id
//
// 命令：status / scan / runOnce / start / stop / saveConfig / connect / quit
const readline = require('readline');
const fs = require('fs');
const path = require('path');

// ===== 关键：stdout 是 JSON-RPC 独占通道，任何非 JSON 输出都会让 C# 端解析失败 =====
// engine.js 里的 console.log/error 会污染 stdout，必须重定向到 stderr。
// 这不是洁癖：实测过一次，`[21:18:07] 已停止` 混进 stdout 后协议就断了。
(function redirectConsole() {
  const util = require('util');
  const toErr = function () {
    process.stderr.write(util.format.apply(util, arguments) + '\n');
  };
  console.log = toErr;
  console.info = toErr;
  console.warn = toErr;
  console.error = toErr;
  console.debug = toErr;
})();

const P = require('./paths');
const { SyncEngine } = require('./engine/engine');

function loadConfig() {
  try {
    if (fs.existsSync(P.CONFIG_FILE)) return JSON.parse(fs.readFileSync(P.CONFIG_FILE, 'utf8'));
  } catch (e) {
    // 配置读坏了不能静默用默认值：用户会以为监视列表还在，实际一个文件都不备份
    try {
      process.stderr.write('[host] 读取配置失败，使用默认值（旧配置未覆盖）: ' + e.message + '\n');
    } catch (e2) {}
  }
  return {
    watchDirs: [],
    remoteFolderName: 'ThunderPanSync',
    intervalMs: 60000,
    settleMs: 5000,
    maxSizeMB: 2048,
    autoStartBrowser: true,
    // 云端版本策略：keep = 绝不动云端旧文件（默认）；replace = 用户显式开启才删旧副本
    versionsMode: 'keep',
    maxDeletePerRun: 20,
  };
}

function saveConfig(cfg) {
  try {
    fs.mkdirSync(P.DATA_DIR, { recursive: true });
    fs.writeFileSync(P.CONFIG_FILE, JSON.stringify(cfg, null, 2), 'utf8');
  } catch (e) {
    // 保存失败必须让界面看到，否则用户以为设置生效了
    throw new Error('保存配置失败: ' + e.message + '（路径 ' + P.CONFIG_FILE + '）');
  }
}

// ---------- 输出（必须无 BOM、UTF-8、单行）----------
function write(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}
function reply(id, ok, data, error) {
  const o = { id: id, ok: ok };
  if (ok) { o.data = data === undefined ? null : data; }
  else { o.error = String(error || 'unknown'); }
  write(o);
}

// ---------- 引擎 ----------
let engine = null;
function getEngine() {
  if (!engine) {
    const cfg = loadConfig();
    engine = new SyncEngine({
      watchDirs: cfg.watchDirs,
      remoteFolderName: cfg.remoteFolderName,
      intervalMs: cfg.intervalMs,
      settleMs: cfg.settleMs,
      maxSizeMB: cfg.maxSizeMB,
      versionsMode: cfg.versionsMode,
      maxDeletePerRun: cfg.maxDeletePerRun,
      stateFile: P.STATE_FILE,
      logFile: P.LOG_FILE,
    });
    engine.on('log', function (e) { write({ event: 'log', data: e }); });
    engine.on('run-complete', function (s) { write({ event: 'run-complete', data: s }); });
    // 进度事件：界面进度条的唯一数据来源。
    // 字段名（phase/index/total/name/size/bytesDone/bytesTotal/elapsedMs/etaMs）由 engine 定义，
    // host 只做转发，不加工、不改名 —— 改名等于破坏界面契约。
    engine.on('progress', function (p) { write({ event: 'progress', data: p }); });
  }
  return engine;
}

// ---------- 命令分发 ----------
const handlers = {
  status: function () {
    const e = getEngine();
    return {
      config: loadConfig(),
      status: e.getStatus(),
      dataDir: P.DATA_DIR,
      configFile: P.CONFIG_FILE,
      // 按天分文件：给界面"打开日志"用当天的路径，不要用启动时的旧路径（跨天会开错文件）
      logFile: P.getLogFile ? P.getLogFile() : P.LOG_FILE,
      latestLogPointer: P.getLatestLogFile ? P.getLatestLogFile() : null,
      pid: process.pid,
    };
  },
  scan: function () {
    const items = getEngine().scan();
    return {
      count: items.length,
      items: items.map(function (i) {
        return {
          file: i.full,
          name: path.basename(i.full),
          size: i.st.size,
          skip: i.skip,
          skipCode: i.skipCode || null,
        };
      }),
    };
  },
  runOnce: async function () {
    // 手动触发：解除熔断、不受退避限制。这正是"立即同步"按钮的语义 ——
    // 熔断只停止自动重试，不能连用户手动操作也一并禁止。
    const stats = await getEngine().runNow();
    return stats;
  },
  start: function () {
    getEngine().start();
    // 持久化"用户希望自动备份"这个意图。
    // 不存的话：用户勾了开机自启、关了窗口、以为在备份，重启后一个文件都不会传。
    // 这是评审抓出的最严重缺陷（"界面在骗人"）。
    var cfg = loadConfig();
    cfg.enabled = true;
    saveConfig(cfg);
    return { running: true, enabled: true };
  },
  stop: function () {
    getEngine().stop();
    var cfg = loadConfig();
    cfg.enabled = false;
    saveConfig(cfg);
    return { running: false, enabled: false };
  },
  saveConfig: function (p) {
    const cur = loadConfig();
    const next = Object.assign({}, cur, p || {});
    // 版本模式的取值收敛在 host 这一层：拼错的值绝不能变成"可以删云端文件"。
    // 引擎侧还有第二道防线（constructor 只认 'replace'），两道都要有。
    if (next.versionsMode !== 'replace') next.versionsMode = 'keep';
    const mdp = parseInt(next.maxDeletePerRun, 10);
    next.maxDeletePerRun = (!isNaN(mdp) && mdp >= 0 && mdp <= 200) ? mdp : 20;
    saveConfig(next);
    // 引擎已实例化则同步配置
    if (engine) {
      if (next.watchDirs) engine.cfg.watchDirs = next.watchDirs;
      if (next.remoteFolderName) engine.cfg.remoteFolderName = next.remoteFolderName;
      if (next.intervalMs) engine.cfg.intervalMs = next.intervalMs;
      if (next.settleMs !== undefined) engine.cfg.settleMs = next.settleMs;
      if (next.maxSizeMB) engine.cfg.maxSizeMB = next.maxSizeMB;
      engine.cfg.versionsMode = next.versionsMode;
      engine.cfg.maxDeletePerRun = next.maxDeletePerRun;
    }
    return { config: next };
  },
  connect: async function () {
    const page = await getEngine().connect();
    return { url: page.url(), title: await page.title() };
  },
  // 熔断后"立即同步"的显式入口（与 runOnce 同义，语义更明确，供 C# 端识别）
  resume: async function () {
    const e = getEngine();
    const wasOpen = e.circuitOpen;
    const stats = await e.runNow();
    return { resumed: wasOpen, stats: stats, status: e.getStatus() };
  },
  quit: function () {
    if (engine) { try { engine.stop(); } catch (e) {} }
    setTimeout(function () { process.exit(0); }, 100);
    return { bye: true };
  },
};

const rl = readline.createInterface({ input: process.stdin, terminal: false });
rl.on('line', function (line) {
  if (!line || !line.trim()) return;
  let req;
  try {
    req = JSON.parse(line);
  } catch (e) {
    // 协议错误必须带足够信息让 C# 端显示：原始行（截断）+ 解析原因 + 时间。
    // 原来只给 raw，界面无法解释"为什么没反应"。
    write({
      event: 'protocol-error',
      data: {
        raw: line.slice(0, 200),
        reason: e && e.message ? e.message : String(e),
        length: line.length,
        time: new Date().toISOString(),
      },
    });
    return;
  }
  // 解析成功不代表是对象：JSON.parse('null'|'42'|'"s"'|'[]') 都是合法 JSON。
  // 原来直接读 req.id，遇到 null 会抛 TypeError 让进程 exit=1，
  // 而 C# 那边只能看到"引擎进程已退出"，完全不知道原因（实测复现过）。
  if (req === null || typeof req !== 'object' || Array.isArray(req)) {
    write({
      event: 'protocol-error',
      data: {
        raw: line.slice(0, 200),
        reason: '解析结果不是对象（got ' + (req === null ? 'null' : typeof req) + '）',
        length: line.length,
        time: new Date().toISOString(),
      },
    });
    return;
  }

  const id = req.id;
  const cmd = req.cmd;

  // 只认 handlers 自己的属性。用 handlers[cmd] 会让 cmd="constructor" / "toString"
  // 这类原型链上的名字命中，返回 ok:true（实测过），属于不该有的"成功"。
  const fn = Object.prototype.hasOwnProperty.call(handlers, cmd) ? handlers[cmd] : null;
  if (typeof fn !== 'function') {
    // id 可能是 undefined（对方发了 {}）：原样回一个带 id:null 的响应，
    // 否则 C# 按 id 匹配会干等到超时，界面表现为"点了没反应"。
    reply(id === undefined ? null : id, false, null, 'unknown cmd: ' + String(cmd));
    return;
  }

  Promise.resolve()
    .then(function () { return fn(req.params); })
    .then(function (data) { reply(id, true, data); })
    .catch(function (e) {
      // 错误也要带 code，界面才能分类显示（QUOTA 要提示"空间不足"，AUTH 要提示"重新登录"）
      var code = (e && e.code && /^(AUTH|QUOTA|NETWORK|BROWSER|PAGE|DISK|UNKNOWN)$/.test(e.code)) ? e.code : 'UNKNOWN';
      var msg = e && e.message ? e.message : String(e);
      write({ event: 'log', data: { level: 'error', msg: msg, code: code, time: new Date().toISOString() } });
      reply(id, false, null, msg);
    });
});

rl.on('close', function () {
  if (engine) { try { engine.stop(); } catch (e) {} }
  process.exit(0);
});

// 启动即告知宿主已就绪（C# 端可据此判断不需要再等）
var _bootCfg = loadConfig();
write({
  event: 'ready',
  data: {
    pid: process.pid,
    dataDir: P.DATA_DIR,
    node: process.version,
    enabled: _bootCfg.enabled === true,
    hasWatchDirs: !!( _bootCfg.watchDirs && _bootCfg.watchDirs.length ),
  },
});

// ---- 关键：恢复上次的自动备份状态 ----
// 用户上次点过"开始自动备份"（enabled=true），重启后必须自动恢复。
// 否则用户勾了开机自启、关了窗口、以为在备份，实际一个字节都没传
// —— 这是外部评审给的最低分项，也是唯一"骗人"的缺陷。
if (_bootCfg.enabled === true) {
  if (!_bootCfg.watchDirs || _bootCfg.watchDirs.length === 0) {
    write({ event: 'log', data: { level: 'warn', msg: '上次是自动备份状态，但监视列表为空，未启动', time: new Date().toISOString() } });
  } else {
    // 稍等宿主建好界面再启动，避免日志在 UI 就绪前丢失
    setTimeout(function () {
      try {
        getEngine().start();
        write({ event: 'log', data: { level: 'info', msg: '已恢复自动备份（上次退出时处于运行状态）', time: new Date().toISOString() } });
        write({ event: 'autostarted', data: { running: true } });
      } catch (e) {
        write({ event: 'log', data: { level: 'error', msg: '恢复自动备份失败: ' + e.message, time: new Date().toISOString() } });
      }
    }, 1500);
  }
}
