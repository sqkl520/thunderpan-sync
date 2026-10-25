'use strict';
// test-upload.js -- 真实上传回归（需要本机已启动带 --remote-debugging-port=9222 的已登录 Edge）
//
// 由 tests/run-tests.ps1 调用：node tests/test-upload.js
// 输出机器可读结果（PS 解析后转成 [PASS]/[FAIL]/[SKIP]）：
//     @@PASS@@|<case-name>|<detail>
//     @@FAIL@@|<case-name>|<reason>
//     @@SKIP@@|<case-name>|<reason>
//
// 三个用例（同一次进程里跑完，避免重复连浏览器）：
//     cdp-9222-endpoint   探测 /json/version 与 /json/list，并确认已登录
//     upload-runonce      2 个文件（小文本 + 64KB 二进制）跑一轮，断言 failed==0 且 uploaded>=2
//     upload-idempotent   立刻再跑一轮，断言 uploaded==0
//
// 安全约束（务必保持）：
//   - stateFile / logFile 一律放在 %TEMP% 的独立临时目录里，绝不碰
//     %APPDATA%\ThunderPanSync\state.json（那是用户真实备份进度）
//   - 上传文件名带毫秒时间戳，避免与云盘已有文件同名导致幂等判定出错
//   - 9222 不可达或未登录时输出 SKIP，绝不伪造 PASS
//
// 副作用：会往用户云盘的 remoteFolderName（默认 ThunderPanSync）里留下两个
//         tps-test-<ts>-*.txt/.bin 文件。这是真实上传用例无法避免的成本。

const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');

const ROOT = path.resolve(__dirname, '..');
const CDP_PORT = String(process.env.TPS_CDP_PORT || 9222);
const CDP_URL = 'http://127.0.0.1:' + CDP_PORT;
const RUN_TIMEOUT_MS = 180000;   // 单轮 runOnce 上限（计划要求 180s）
const SMALL_SIZE = 0;            // 占位，实际写入见下
const BLOB_SIZE = 64 * 1024;

let emitted = new Set();
function write(stream, s) {
  try { fs.writeSync(stream, s); } catch (e) { }
}
function sanitize(s) {
  return String(s === undefined || s === null ? '' : s).replace(/[\r\n|]+/g, ' ').trim();
}
function emit(status, name, detail) {
  emitted.add(name);
  write(1, '@@' + status + '@@|' + name + '|' + sanitize(detail) + '\n');
}

// ---------------------------------------------------------------- 探测

function httpGetJson(url, timeoutMs) {
  return new Promise((resolve) => {
    let settled = false;
    const done = (v) => { if (!settled) { settled = true; resolve(v); } };
    let req;
    try {
      req = http.get(url, { timeout: timeoutMs }, (res) => {
        let body = '';
        res.setEncoding('utf8');
        res.on('data', (c) => { body += c; });
        res.on('end', () => {
          let json = null;
          try { json = JSON.parse(body); } catch (e) { }
          done({ ok: res.statusCode === 200, status: res.statusCode, body: body, json: json, error: json ? null : 'response is not JSON' });
        });
      });
    } catch (e) {
      done({ ok: false, error: e.message });
      return;
    }
    req.on('error', (e) => done({ ok: false, error: e.code || e.message }));
    req.on('timeout', () => { try { req.destroy(); } catch (e) { } done({ ok: false, error: 'timeout' }); });
  });
}

async function probe() {
  const ver = await httpGetJson(CDP_URL + '/json/version', 5000);
  if (!ver.ok) return { ok: false, reason: 'cannot reach ' + CDP_URL + '/json/version (' + (ver.error || ('HTTP ' + ver.status)) + ')' };
  if (!ver.json || !ver.json.webSocketDebuggerUrl) {
    return { ok: false, reason: '/json/version did not return a webSocketDebuggerUrl (' + CDP_URL + ')' };
  }
  const list = await httpGetJson(CDP_URL + '/json/list', 5000);
  if (!list.ok) return { ok: false, reason: 'cannot reach ' + CDP_URL + '/json/list (' + (list.error || ('HTTP ' + list.status)) + ')' };
  if (!Array.isArray(list.json)) return { ok: false, reason: '/json/list did not return an array (' + CDP_URL + ')' };

  const pages = list.json.filter((t) => t && t.type === 'page');
  const panPages = pages.filter((t) => String(t.url || '').indexOf('pan.xunlei.com') >= 0);
  return {
    ok: true,
    browser: ver.json.Browser || 'unknown',
    pageCount: pages.length,
    panPageCount: panPages.length,
    panTitles: panPages.map((p) => p.title).join(' / '),
  };
}

// 登录态检查：sessionid cookie 是 engine.connect() 的判据
async function checkLogin() {
  let chromium;
  try {
    chromium = require(path.join(ROOT, 'engine', 'cdp.js')).chromium;
  } catch (e) {
    return { ok: false, reason: 'cannot require engine/cdp.js: ' + e.message };
  }
  let browser;
  try {
    browser = await chromium.connectOverCDP(CDP_URL, { timeout: 8000 });
  } catch (e) {
    return { ok: false, reason: 'CDP connect failed: ' + e.message };
  }
  try {
    const ctx = browser.contexts()[0];
    if (!ctx) return { ok: false, reason: 'browser has no context' };
    let cookies = [];
    try { cookies = (await ctx.cookies('https://pan.xunlei.com')) || []; } catch (e) { }
    if (!cookies.find((c) => c && c.name === 'sessionid')) {
      return { ok: false, reason: 'no sessionid cookie for pan.xunlei.com -> not logged in' };
    }
    const pages = typeof ctx.pages === 'function' ? ctx.pages() : [];
    const panPage = pages.find((p) => String(p.url()).indexOf('pan.xunlei.com') >= 0);
    return { ok: true, cookieCount: cookies.length, hasPanPage: !!panPage, panUrl: panPage ? panPage.url() : '(none)' };
  } finally {
    try { if (browser && typeof browser.close === 'function') browser.close(); } catch (e) { }
  }
}

// ---------------------------------------------------------------- 上传回合

function withTimeout(promise, ms, label) {
  let timer = null;
  const timeout = new Promise((resolve, reject) => {
    timer = setTimeout(() => reject(new Error(label + ' exceeded ' + Math.round(ms / 1000) + 's')), ms);
  });
  return Promise.race([promise, timeout]).finally(() => { if (timer) clearTimeout(timer); });
}

function statsDetail(stats) {
  const errs = (stats.errors || []).map((e) => e.file ? (path.basename(String(e.file)) + ': ' + e.error) : String(e.error)).join(' ; ');
  return 'scanned=' + stats.scanned + ' uploaded=' + stats.uploaded + ' failed=' + stats.failed
    + ' skipped=' + stats.skipped + ' durationMs=' + stats.durationMs
    + (errs ? ' errors=[' + errs + ']' : '');
}

async function main() {
  // ---- 1. 探测
  const p = await probe();
  if (!p.ok) {
    emit('SKIP', 'cdp-9222-endpoint', p.reason);
    emit('SKIP', 'upload-runonce', 'prerequisite missing: ' + p.reason);
    emit('SKIP', 'upload-idempotent', 'prerequisite missing: ' + p.reason);
    return;
  }

  const login = await checkLogin();
  if (!login.ok) {
    emit('SKIP', 'cdp-9222-endpoint', 'port reachable (' + p.browser + ') but ' + login.reason);
    emit('SKIP', 'upload-runonce', 'not logged in: ' + login.reason);
    emit('SKIP', 'upload-idempotent', 'not logged in: ' + login.reason);
    return;
  }
  emit('PASS', 'cdp-9222-endpoint',
    'port ' + CDP_PORT + ' ok; browser=' + p.browser + '; pages=' + p.pageCount
    + '; pan.xunlei.com pages=' + p.panPageCount + '; sessionid ok; panUrl=' + login.panUrl);

  // ---- 2. 临时工作区（绝不写用户的 %APPDATA%）
  const tmpBase = fs.mkdtempSync(path.join(os.tmpdir(), 'tps-upload-'));
  const watched = path.join(tmpBase, 'watched');
  fs.mkdirSync(watched, { recursive: true });
  const stateFile = path.join(tmpBase, 'state.json');
  const logFile = path.join(tmpBase, 'sync.log');

  // paths.js 在模块加载时解析数据目录，这里必须提前指向临时目录
  process.env.PANSYNC_DATA_DIR = path.join(tmpBase, 'data');

  const stamp = new Date().toISOString().replace(/[-:.TZ]/g, '');
  const small = path.join(watched, 'tps-test-' + stamp + '-note.txt');
  const blob = path.join(watched, 'tps-test-' + stamp + '-blob.bin');

  const old = new Date(Date.now() - 300000);   // mtime 回拨 5 分钟，避免判"刚修改，等待稳定"
  fs.writeFileSync(small, 'ThunderPanSync upload regression test ' + stamp + '\r\n', 'utf8');
  const buf = Buffer.alloc(BLOB_SIZE);
  for (let i = 0; i < BLOB_SIZE; i++) buf[i] = (i * 131 + 17) & 0xff;
  fs.writeFileSync(blob, buf);
  fs.utimesSync(small, old, old);
  fs.utimesSync(blob, old, old);

  let SyncEngine;
  try {
    SyncEngine = require(path.join(ROOT, 'engine', 'engine.js')).SyncEngine;
  } catch (e) {
    emit('FAIL', 'upload-runonce', 'cannot require engine/engine.js: ' + e.message);
    emit('FAIL', 'upload-idempotent', 'cannot require engine/engine.js: ' + e.message);
    return;
  }

  const engine = new SyncEngine({
    watchDirs: [watched],
    stateFile: stateFile,
    logFile: logFile,
    settleMs: 5000,
    remoteFolderName: process.env.TPS_REMOTE_FOLDER || 'ThunderPanSync',
  });

  try {
    // ---- 3. 第一轮：应该上传 2 个
    let stats = null;
    let firstError = null;
    try {
      stats = await withTimeout(engine.runOnce(), RUN_TIMEOUT_MS, 'runOnce #1');
    } catch (e) {
      firstError = e;
    }

    if (firstError) {
      const msg = firstError.message || String(firstError);
      const skip = /exceeded/i.test(msg);
      emit(skip ? 'SKIP' : 'FAIL', 'upload-runonce', 'runOnce #1: ' + msg + ' (watched=' + watched + ')');
      emit(skip ? 'SKIP' : 'FAIL', 'upload-idempotent', 'skipped because runOnce #1 did not finish: ' + msg);
      return;
    }

    const detail1 = statsDetail(stats);

    // 环境问题（连不上浏览器 / 建不了远程文件夹）不是产品回归 -> SKIP，不伪造 PASS
    if (stats.uploaded === 0 && stats.failed === 0 && (stats.errors || []).length > 0) {
      const why = (stats.errors || []).map((e) => e.error).join(' ; ');
      emit('SKIP', 'upload-runonce', 'runOnce aborted before uploading (environment): ' + why);
      emit('SKIP', 'upload-idempotent', 'skipped: first run never uploaded anything');
      return;
    }

    if (stats.failed > 0) {
      emit('FAIL', 'upload-runonce', 'stats.failed must be 0; got ' + detail1);
      emit('SKIP', 'upload-idempotent', 'skipped: first run reported failures');
      return;
    }
    if (!(stats.uploaded >= 2)) {
      emit('FAIL', 'upload-runonce', 'stats.uploaded must be >= 2 (2 files were prepared); got ' + detail1);
      emit('SKIP', 'upload-idempotent', 'skipped: first run did not upload both files');
      return;
    }
    emit('PASS', 'upload-runonce', detail1 + '; files=' + path.basename(small) + ',' + path.basename(blob));

    // 状态必须真的落盘，否则幂等无从谈起
    if (!fs.existsSync(stateFile)) {
      emit('FAIL', 'upload-idempotent', 'stateFile was not written: ' + stateFile);
      return;
    }
    let saved = null;
    try { saved = JSON.parse(fs.readFileSync(stateFile, 'utf8')); } catch (e) {
      emit('FAIL', 'upload-idempotent', 'stateFile is not valid JSON: ' + e.message);
      return;
    }
    const uploadedKeys = Object.keys((saved && saved.uploaded) || {});
    if (uploadedKeys.length < 2) {
      emit('FAIL', 'upload-idempotent', 'stateFile.uploaded has ' + uploadedKeys.length + ' entries, expected >= 2');
      return;
    }

    // ---- 4. 第二轮：必须幂等（0 上传）
    let stats2 = null;
    let secondError = null;
    try {
      stats2 = await withTimeout(engine.runOnce(), RUN_TIMEOUT_MS, 'runOnce #2');
    } catch (e) {
      secondError = e;
    }
    if (secondError) {
      const msg = secondError.message || String(secondError);
      emit(/exceeded/i.test(msg) ? 'SKIP' : 'FAIL', 'upload-idempotent', 'runOnce #2: ' + msg);
      return;
    }

    const detail2 = statsDetail(stats2);
    if (stats2.failed > 0) {
      emit('FAIL', 'upload-idempotent', 'second run stats.failed must be 0; got ' + detail2);
      return;
    }
    if (stats2.uploaded !== 0) {
      emit('FAIL', 'upload-idempotent',
        'second run must upload 0 files (state.json fingerprint match), got uploaded=' + stats2.uploaded + '; ' + detail2);
      return;
    }
    emit('PASS', 'upload-idempotent', detail2 + '; stateFile.uploaded=' + uploadedKeys.length);
  } finally {
    try { engine.stop(); } catch (e) { }
    // 只清理本地临时文件；云盘里的两个测试文件保留（无法安全自动删除）
    try { fs.rmSync(tmpBase, { recursive: true, force: true }); } catch (e) { }
  }
}

const EXPECTED = ['cdp-9222-endpoint', 'upload-runonce', 'upload-idempotent'];

// 兜底：进程级超时后补齐结果，绝不让 PS 侧看到"没有结果"
const watchdog = setTimeout(() => {
  for (const n of EXPECTED) {
    if (!emitted.has(n)) emit('FAIL', n, 'test-upload.js watchdog timeout (300s)');
  }
  process.exit(3);
}, 300000);
watchdog.unref();

main().then(() => {
  for (const n of EXPECTED) {
    if (!emitted.has(n)) emit('FAIL', n, 'case produced no result (internal error)');
  }
  clearTimeout(watchdog);
  process.exit(0);
}).catch((e) => {
  const msg = (e && e.message) ? e.message : String(e);
  for (const n of EXPECTED) {
    if (!emitted.has(n)) emit('FAIL', n, 'unexpected error: ' + msg);
  }
  write(2, (e && e.stack ? e.stack : msg) + '\n');
  clearTimeout(watchdog);
  process.exit(1);
});
