'use strict';
// test-unit.js -- 单元级回归用例（不需要浏览器 / 不需要登录迅雷云盘）
//
// 这个文件被 tests/run-tests.ps1 调用，每次进程只跑一个用例：
//     node tests/test-unit.js <case-id>
// 输出一行机器可读结果（PowerShell 解析后转成 [PASS]/[FAIL]/[SKIP]）：
//     @@PASS@@|<case-name>|<detail>
//     @@FAIL@@|<case-name>|<reason>
//     @@SKIP@@|<case-name>|<reason>
//
// 为什么用 fs.writeSync(1, ...) 而不是 console.log：
//   进程末尾会 process.exit()，管道上的异步 stdout 有可能被截断；同步写不会。

const fs = require('fs');
const os = require('os');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'tps-unit-'));

// 必须在 require engine 之前设置：paths.js 在模块加载时解析数据目录。
// 否则 ensureRemoteFolder/日志可能写到用户真实数据目录里。
process.env.PANSYNC_DATA_DIR = path.join(TMP, 'data');

let emitted = false;
function write(stream, s) {
  try { fs.writeSync(stream, s); } catch (e) { }
}
function emit(status, name, detail) {
  emitted = true;
  const safe = (detail === undefined || detail === null) ? '' : String(detail).replace(/[\r\n|]+/g, ' ');
  write(1, '@@' + status + '@@|' + name + '|' + safe + '\n');
}
function fail(msg) { throw new Error(msg); }

let SyncEngine;
let engineLoadError = null;
try {
  SyncEngine = require(path.join(ROOT, 'engine', 'engine.js')).SyncEngine;
} catch (e) {
  // 引擎加载失败（例如别的 Agent 正在改 engine.js）也要给出可读原因，
  // 并且必须冒泡到"当前这个用例"名下 —— 不能额外造一条 case，
  // 否则 PS 侧会看到"每个用例都多一条 FAIL"，汇总数被放大。
  engineLoadError = e;
}

// ---------------------------------------------------------------- helpers

function dir(tag) {
  const d = path.join(TMP, tag);
  fs.mkdirSync(d, { recursive: true });
  return d;
}

// 建一个隔离工作区：watched/ 是被监视目录，state.json / sync.log 放在它的兄弟位置，
// 确保测试文件本身不会混进 scan() 的结果里。
function workspace(tag, cfgOverride) {
  const base = dir(tag);
  const watched = path.join(base, 'watched');
  fs.mkdirSync(watched, { recursive: true });
  const opts = {
    watchDirs: [watched],
    stateFile: path.join(base, 'state.json'),
    logFile: path.join(base, 'sync.log'),
    settleMs: 0,            // 测试里文件刚创建，settleMs>0 会被判"刚修改，等待稳定"
    maxSizeMB: 2048,
  };
  if (cfgOverride) {
    for (const k of Object.keys(cfgOverride)) opts[k] = cfgOverride[k];
  }
  const engine = new SyncEngine(opts);
  return { base, watched, engine, opts };
}

function itemsFor(items, full) {
  const key = path.resolve(full).toLowerCase();
  return (items || []).filter(it => it && it.full && path.resolve(it.full).toLowerCase() === key);
}

// ---------------------------------------------------------------- cases

const CASES = {

  // 1. engine.js 能被 require，SyncEngine 能构造
  'require-construct': {
    name: 'engine-require-construct',
    run() {
      if (typeof SyncEngine !== 'function') fail('module.exports.SyncEngine is not a constructor: ' + typeof SyncEngine);
      const ws = workspace('require-construct');
      const eng = ws.engine;
      if (!(eng instanceof SyncEngine)) fail('new SyncEngine() did not return a SyncEngine instance');

      const { EventEmitter } = require('events');
      if (!(eng instanceof EventEmitter)) fail('SyncEngine must extend EventEmitter');

      // 关键公开接口存在（interface drift 时能立刻发现）
      const required = ['scan', 'runOnce', 'start', 'stop', 'connect', 'uploadOne',
        'ensureRemoteFolder', 'enterRemoteFolder', '_loadState', '_saveState',
        'getStatus', '_remoteNameFor', 'log'];
      const missing = required.filter(m => typeof eng[m] !== 'function');
      if (missing.length > 0) fail('missing methods: ' + missing.join(', '));

      // opts 覆盖必须生效，否则测试无法隔离真实数据目录
      if (path.resolve(eng.cfg.stateFile) !== path.resolve(ws.opts.stateFile)) {
        fail('opts.stateFile override ignored: ' + eng.cfg.stateFile);
      }
      if (path.resolve(eng.cfg.logFile) !== path.resolve(ws.opts.logFile)) {
        fail('opts.logFile override ignored: ' + eng.cfg.logFile);
      }
      if (eng.cfg.settleMs !== 0) fail('opts.settleMs override ignored: ' + eng.cfg.settleMs);

      return 'SyncEngine constructed; 12 methods present; opts override honoured';
    },
  },

  // 2. scan() 对空目录返回 0 项
  'scan-empty': {
    name: 'scan-empty-dir',
    run() {
      const ws = workspace('scan-empty');
      const items = ws.engine.scan();
      if (!Array.isArray(items)) fail('scan() did not return an array: ' + typeof items);
      if (items.length !== 0) fail('expected 0 items for empty dir, got ' + items.length);
      // 空目录也不该产生任何 uploaded/failed 记账
      if (Object.keys(ws.engine.state.uploaded).length !== 0) fail('state.uploaded not empty');
      return 'empty dir -> 0 items';
    },
  },

  // 3. scan() 对 0 字节文件的 skip 必须说明原因（云盘拒收，不是错误）
  'scan-zero-byte': {
    name: 'scan-zero-byte-skip',
    run() {
      const ws = workspace('scan-zero');
      const f = path.join(ws.watched, 'empty.txt');
      fs.writeFileSync(f, Buffer.alloc(0));

      const items = ws.engine.scan();
      const hit = itemsFor(items, f)[0];
      if (!hit) fail('scan() did not report the 0-byte file at all; items=' + items.length);
      const skip = hit.skip;
      if (typeof skip !== 'string' || skip.length === 0) {
        fail('0-byte file has no skip reason (skip=' + JSON.stringify(skip) + ')');
      }
      if (skip.indexOf('0 字节') < 0) {
        fail('skip reason for 0-byte file must mention "0 字节", got: ' + skip);
      }
      if (hit.fp) fail('0-byte file must not get a fingerprint (must never be uploaded)');
      return 'skip="' + skip + '"';
    },
  },

  // 4. 未来时间戳（2030）不能被判成"刚修改，等待稳定" —— 已修的 bug
  //    原逻辑 now - mtime 为负时 `age < settleMs` 恒成立，从 zip 解压/跨机拷贝
  //    /robocopy /COPY:DAT 的文件会永久漏备。
  'scan-future-mtime': {
    name: 'scan-future-mtime-not-pending',
    run() {
      const ws = workspace('scan-future', { settleMs: 5000 });
      const f = path.join(ws.watched, 'from-zip.txt');
      fs.writeFileSync(f, 'unpacked with a future mtime');

      const t2030 = new Date('2030-01-01T00:00:00Z');
      fs.utimesSync(f, t2030, t2030);
      const st = fs.statSync(f);
      if (st.mtimeMs < Date.parse('2030-01-01T00:00:00Z') - 60000) {
        fail('could not set a 2030 mtime (got ' + st.mtime.toISOString() + '); test cannot run');
      }

      const items = ws.engine.scan();
      const hit = itemsFor(items, f)[0];
      if (!hit) fail('scan() silently dropped the future-mtime file (items=' + items.length + ')');

      if (hit.skip === '刚修改，等待稳定') {
        fail('REGRESSION: future mtime is treated as just-modified forever '
          + '(mtime=' + st.mtime.toISOString() + '), file would never be backed up');
      }
      const uploadable = (hit.skip === null || hit.skip === undefined);
      return 'mtime=' + st.mtime.toISOString() + ' skip=' + JSON.stringify(hit.skip === undefined ? null : hit.skip)
        + ' uploadable=' + uploadable;
    },
  },

  // 5. 用户误选 C:\Windows\System32 时必须拒绝（否则会把系统盘往云上传）
  'scan-system-dir': {
    name: 'scan-system-dir-blocked',
    run() {
      const winRoot = process.env.SystemRoot || process.env.WINDIR || 'C:\\Windows';
      const sys32 = path.join(winRoot, 'System32');
      if (!fs.existsSync(sys32)) fail('cannot test guard: ' + sys32 + ' does not exist on this machine');

      const ws = workspace('scan-sysdir', { watchDirs: [sys32] });

      // 通过 'log' 事件收集警告，而不是读日志文件：
      // 新版 engine.log() 按天把 cfg.logFile 改指到 paths.js 的 getLogFile()，
      // 文件位置不再由 opts.logFile 决定。事件是稳定接口。
      const messages = [];
      ws.engine.on('log', ev => { if (ev && ev.msg) messages.push(String(ev.msg)); });

      const items = ws.engine.scan();
      if (!Array.isArray(items)) fail('scan() did not return an array: ' + typeof items);
      if (items.length !== 0) {
        fail('expected 0 items for protected dir ' + sys32 + ', got ' + items.length
          + ' (system-dir guard is NOT working)');
      }

      // 必须留下可读警告，不能静默返回空数组（否则用户以为备份成功了）
      const hit = messages.find(m => m.toLowerCase().indexOf('system32') >= 0
        || m.indexOf(sys32) >= 0
        || m.indexOf('系统目录') >= 0);
      if (!hit) {
        fail('guard returned 0 items but emitted no warning about rejecting ' + sys32
          + '; log events=' + JSON.stringify(messages.slice(0, 5)));
      }
      return 'watchDir=' + sys32 + ' -> 0 items; warned: ' + hit.slice(0, 60);
    },
  },

  // 6. 子目录文件在云端叫 basename（名字被路径前缀污染会让核查永远找不到文件）
  'remote-name': {
    name: 'remote-name-basename',
    run() {
      const ws = workspace('remote-name');
      const deep = path.join(ws.watched, 'sub', 'deep');
      fs.mkdirSync(deep, { recursive: true });
      const f = path.join(deep, 'nested file.txt');
      fs.writeFileSync(f, 'nested content');

      const st = fs.statSync(f);
      const item = { full: f, st: st, fp: st.size + '-' + Math.floor(st.mtimeMs), rel: path.join('sub', 'deep', 'nested file.txt') };

      if (typeof ws.engine._remoteNameFor !== 'function') fail('_remoteNameFor is missing');
      const name = ws.engine._remoteNameFor(item);
      if (name !== 'nested file.txt') {
        fail('expected basename "nested file.txt", got ' + JSON.stringify(name));
      }
      if (name.indexOf(path.sep) >= 0 || name.indexOf('/') >= 0) {
        fail('remote name contains a path separator: ' + JSON.stringify(name));
      }

      // scan() 产出的 item 也必须得到同样的名字（防止 rel/full 混用）
      const scanned = itemsFor(ws.engine.scan(), f)[0];
      if (!scanned) fail('scan() did not report the nested file');
      const name2 = ws.engine._remoteNameFor(scanned);
      if (name2 !== 'nested file.txt') {
        fail('scan() item -> _remoteNameFor gave ' + JSON.stringify(name2));
      }
      return '_remoteNameFor(sub\\deep\\nested file.txt) = "nested file.txt"';
    },
  },

  // 7. state.json 损坏（半截 JSON）时必须能启动，并留下 .corrupt-* 备份供诊断
  //    静默当空会导致全量重传几个 GB，直接崩掉则用户完全无法启动。
  'state-corrupt': {
    name: 'state-corrupt-recovery',
    run() {
      const ws = workspace('state-corrupt');
      const stateFile = ws.opts.stateFile;
      const broken = '{"uploaded": {"D:\\\\docs\\\\a.txt": {"fp": "10-123", "size": 10';
      fs.writeFileSync(stateFile, broken, 'utf8');
      if (fs.statSync(stateFile).size === 0) fail('could not write the corrupt state fixture');

      let eng2;
      try {
        // 构造函数内部走 _loadState()，损坏时不允许抛异常
        eng2 = new SyncEngine({
          watchDirs: [ws.watched],
          stateFile: stateFile,
          logFile: ws.opts.logFile,
          settleMs: 0,
        });
      } catch (e) {
        fail('constructor threw on corrupt state.json: ' + (e && e.message ? e.message : String(e)));
      }

      const names = fs.readdirSync(ws.base);
      const baks = names.filter(n => n.indexOf('state.json.corrupt-') === 0);
      if (baks.length < 1) {
        fail('no "state.json.corrupt-*" backup was created; dir contains: ' + names.join(', '));
      }

      const copy = fs.readFileSync(path.join(ws.base, baks[0]), 'utf8');
      if (copy !== broken) fail('corrupt backup content does not match the original file');

      if (!eng2.state || typeof eng2.state.uploaded !== 'object' || eng2.state.uploaded === null) {
        fail('engine state was not reset to an empty object');
      }
      if (Object.keys(eng2.state.uploaded).length !== 0 || Object.keys(eng2.state.failed).length !== 0) {
        fail('engine state is not empty after corrupt-state recovery');
      }
      return 'recovered, backup=' + baks[0] + ', state reset to empty';
    },
  },

};

// ---------------------------------------------------------------- runner

function cleanup() {
  // Windows 上刚写完的文件可能仍被索引/杀软短暂持有句柄，rmSync 会 EPERM/EBUSY。
  // maxRetries 让 Node 自己退避重试，避免 %TEMP% 里堆一堆 tps-unit-* 空目录。
  try { fs.rmSync(TMP, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 }); } catch (e) { }
}

const arg = process.argv[2];
const testCase = arg ? CASES[arg] : null;

if (!testCase) {
  process.stderr.write('unknown case: ' + String(arg) + '\nknown cases: ' + Object.keys(CASES).join(', ') + '\n');
  cleanup();
  process.exit(2);
}

const watchdog = setTimeout(() => {
  if (!emitted) emit('FAIL', testCase.name, 'unit case watchdog timeout (60s)');
  cleanup();
  process.exit(3);
}, 60000);

let status = 'FAIL';
let detail = '';
if (engineLoadError) {
  // 引擎加载失败时，当前用例必然无法执行：报在"这个用例"名下，说清原因
  status = 'FAIL';
  detail = 'require(engine/engine.js) failed: '
    + ((engineLoadError && engineLoadError.message) ? engineLoadError.message : String(engineLoadError));
} else {
  try {
    detail = testCase.run();
    status = 'PASS';
  } catch (e) {
    status = 'FAIL';
    detail = (e && e.message) ? e.message : String(e);
    if (e && e.stack) process.stderr.write(e.stack + '\n');
  }
}

clearTimeout(watchdog);
emit(status, testCase.name, detail);
cleanup();
process.exit(status === 'PASS' ? 0 : 1);
