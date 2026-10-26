// cdp.js -- 原生 CDP 客户端，替代 playwright
//
// 为什么自己做（不是为了炫技）：
//   1. playwright 占 17.8 MB / 2 个包，而我们只用它 5 个操作：
//      connectOverCDP / goto / on(request) / locator.setInputFiles / evaluate
//   2. Node 26 内置 WebSocket + fetch，这 5 个操作都能直接用 CDP 协议完成
//   3. 少一层依赖 = 少一个装不上的可能（用户机器上 npm install 常失败）
//
// 本文件提供与 engine.js 用到的 playwright 完全一致的调用面：
//   connect()                  → Browser
//   browser.contexts()[0]      → Context（只有一个）
//   ctx.pages()                → Page[]
//   page.url() / title() / goto() / waitForTimeout()
//   page.on('request'|'response') / off()
//   page.locator('input[type=file]').setInputFiles(path)
//   page.evaluate(fn, arg)
const http = require('http');
const fs = require('fs');
const path = require('path');
const os = require('os');

// ---------------- 调试端口解析（动态端口） ----------------
//
// 为什么引擎侧要读文件而不是硬编码 9222：
//   CDP 端口没有认证，谁先占住 9222 谁就能冒充浏览器。所以 C# 侧
//   （native/BrowserManager.cs）会先探测 9222，若被本应用以外的程序占用就回退到
//   9223..9233，并把**实际端口**写进 %APPDATA%\ThunderPanSync\cdp-port.txt。
//   引擎必须读这个文件，否则 BrowserManager 换了端口而引擎仍连 9222 → 全部上传失败。
//
// 兼容性铁律：读不到文件 / 读到的内容不是合法端口 → 回退 9222。
//   （老版本没有这个文件，删掉也必须能跑。）
const DEFAULT_CDP_PORT = 9222;
const PORT_FILE_NAME = 'cdp-port.txt';

// 可能是数据目录的所有候选：环境变量 > paths.js 的 DATA_DIR > %APPDATA%\ThunderPanSync
function portFileCandidates() {
  const dirs = [];
  if (process.env.PANSYNC_DATA_DIR) dirs.push(process.env.PANSYNC_DATA_DIR);
  try {
    const P = require('../paths');
    if (P && P.DATA_DIR) dirs.push(P.DATA_DIR);
    // paths.js 在开发态可能指向 <src>/data，而 C# 始终写 %APPDATA%。
    // 所以即便 P.DATA_DIR 命中，也要把 %APPDATA% 作为兜底候选。
  } catch (e) { /* 没有 paths.js 也要能工作 */ }
  const ad = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
  dirs.push(path.join(ad, 'ThunderPanSync'));

  const seen = {};
  const files = [];
  for (const d of dirs) {
    if (!d) continue;
    const key = String(d).toLowerCase();
    if (seen[key]) continue;
    seen[key] = true;
    files.push(path.join(d, PORT_FILE_NAME));
  }
  return files;
}

// 读 cdp-port.txt 得到端口。任何异常/非法内容一律回退 9222。
function readCdpPort() {
  for (const f of portFileCandidates()) {
    try {
      if (!fs.existsSync(f)) continue;
      const raw = fs.readFileSync(f, 'utf8');
      // 允许 BOM / 空白 / 尾部换行；只接受纯数字
      const txt = String(raw).replace(/^\uFEFF/, '').trim();
      const m = /^(\d{1,5})$/.exec(txt);
      if (!m) continue;                    // 内容非法：换下一个候选，最终回退 9222
      const p = parseInt(m[1], 10);
      if (p <= 0 || p > 65535) continue;
      return p;
    } catch (e) { /* 文件被占用/无权限：继续下一个候选 */ }
  }
  return DEFAULT_CDP_PORT;
}

// 引擎侧统一从这里取 CDP 地址（每次调用都重新读，避免"启动早于 C# 写文件"的竞态）
function getCdpUrl() {
  return 'http://127.0.0.1:' + readCdpPort();
}

// engine.js 传入的是历史硬编码 'http://127.0.0.1:9222'；当它仍是默认值时，
// 说明调用方没有显式指定端口，此时必须以 cdp-port.txt 为准。
// 显式传入其它地址（测试用假地址）时尊重调用方。
function resolveDebugUrl(url) {
  if (!url) return getCdpUrl();
  const m = /^(?:https?:\/\/)?(?:127\.0\.0\.1|localhost):(\d+)\/?$/i.exec(String(url).trim());
  if (m && parseInt(m[1], 10) === DEFAULT_CDP_PORT) return getCdpUrl();
  if (m) return String(url).trim();
  return String(url).trim();
}

function httpGetJson(url) {
  return new Promise(function (resolve, reject) {
    const req = http.get(url, { timeout: 5000 }, function (res) {
      let d = '';
      res.on('data', function (c) { d += c; });
      res.on('end', function () {
        try { resolve(JSON.parse(d)); } catch (e) { reject(e); }
      });
    });
    req.on('error', reject);
    req.on('timeout', function () { req.destroy(new Error('timeout')); });
  });
}

function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

// ---------------- Page ----------------
class Page {
  constructor(cdp, targetId, wsUrl) {
    this.cdp = cdp;
    this.targetId = targetId;
    this.wsUrl = wsUrl;
    this._url = '';
    this._listeners = { request: [], response: [] };
    this._closed = false;
    this._loadWaiters = [];
  }

  _emit(kind, payload) {
    const ls = this._listeners[kind];
    if (!ls) return;
    for (const fn of ls.slice()) {
      try { fn(payload); } catch (e) { /* 单个监听器异常不能影响其它 */ }
    }
  }

  on(kind, fn) {
    if (!this._listeners[kind]) this._listeners[kind] = [];
    this._listeners[kind].push(fn);
  }

  off(kind, fn) {
    const ls = this._listeners[kind];
    if (!ls) return;
    const i = ls.indexOf(fn);
    if (i >= 0) ls.splice(i, 1);
  }

  isClosed() { return this._closed; }
  url() { return this._url; }

  // 页面是否还能响应 CDP。
  //
  // 为什么必须有这个方法（实测踩到的真问题）：
  //   浏览器连续跑了几小时后，pan.xunlei.com 那个标签的渲染进程会僵死。
  //   此时 Target.getTargets / Target.createTarget 这些 Browser 级命令仍然正常，
  //   WebSocket 也能连上（/json 里照样列出这个页面），但 Page.enable 永远不回。
  //   原来的 connect() 只挑第一个 url 含 pan.xunlei.com 的页面就直接用，
  //   于是表现为：connect() 成功 → 之后每次 _capHeader 都"头截取超时"，
  //   整轮同步永远失败，而界面只会说"登录/凭证问题"，误导用户去重新登录。
  //
  // 判据用 Page.enable：它必须在 5 秒内回。真浏览器一定会回；
  // 僵死的渲染进程不会。不能只靠 isClosed()（那个只反映 CDP 会话状态）。
  async isResponsive(timeoutMs) {
    if (this._closed) return false;
    const t = timeoutMs || 5000;
    try {
      await Promise.race([
        this.cdp.send(this.wsUrl, 'Page.enable', {}),
        sleep(t).then(function () { throw new Error('probe timeout'); }),
      ]);
      return true;
    } catch (e) {
      return false;
    }
  }

  async title() {
    try {
      const r = await this.cdp.send(this.wsUrl, 'Runtime.evaluate', {
        expression: 'document.title', returnByValue: true,
      });
      return r && r.result ? r.result.value : '';
    } catch (e) { return ''; }
  }

  async goto(url, opts) {
    const timeout = (opts && opts.timeout) || 60000;
    this._url = url;
    await this.cdp.send(this.wsUrl, 'Page.enable', {});
    await this.cdp.send(this.wsUrl, 'Page.navigate', { url: url });
    // 等 load 或超时（不依赖事件，简单可靠：轮询 readyState）
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      await sleep(300);
      try {
        const r = await this.cdp.send(this.wsUrl, 'Runtime.evaluate', {
          expression: 'document.readyState', returnByValue: true,
        });
        const s = r && r.result ? r.result.value : '';
        if (s === 'complete' || s === 'interactive') return { ok: true };
      } catch (e) { /* 导航中执行上下文会销毁，重试 */ }
    }
    return { ok: false, timeout: true };
  }

  async waitForTimeout(ms) { await sleep(ms); }

  async evaluate(fnOrExpr, arg) {
    let expr;
    if (typeof fnOrExpr === 'function') {
      // 把函数序列化后立即调用，带一个参数
      const src = fnOrExpr.toString();
      expr = '(' + src + ')(' + JSON.stringify(arg === undefined ? null : arg) + ')';
    } else {
      expr = String(fnOrExpr);
    }
    const r = await this.cdp.send(this.wsUrl, 'Runtime.evaluate', {
      expression: expr,
      returnByValue: true,
      awaitPromise: true,
      userGesture: true,
    });
    if (r && r.exceptionDetails) {
      throw new Error('evaluate 异常: ' + JSON.stringify(r.exceptionDetails).slice(0, 300));
    }
    return r && r.result ? r.result.value : undefined;
  }

  // input[type=file] 的 setInputFiles —— CDP DOM.setFileInputFiles
  locator(selector) {
    const self = this;
    return {
      first: function () { return this; },
      setInputFiles: async function (filePath) {
        return self._setInputFiles(selector, filePath);
      },
    };
  }

  async _setInputFiles(selector, filePath) {
    // 为什么用 objectId 而不是 nodeId（这是实测踩出来的关键差异）：
    //   playwright 内部用 DOM.setFileInputFiles({ objectId, files })，
    //   我最初用 nodeId 版本：文件确实被"设置"了，但网页的 Vue 上传逻辑
    //   完全不响应 —— 表现为触发不了 POST，上传等 120s 超时。
    //   换成 objectId（先在页面里取到元素引用）后事件正常派发。
    const expr = 'document.querySelector(' + JSON.stringify(selector) + ')';

    // 1) 取元素引用（不 returnByValue，这样拿到的才是可用于 objectId 的句柄）
    const h = await this.cdp.send(this.wsUrl, 'Runtime.evaluate', {
      expression: expr,
      objectGroup: 'fileinput',
    });
    if (!h || !h.result || !h.result.objectId) {
      throw new Error('找不到元素: ' + selector);
    }
    const objectId = h.result.objectId;

    // 2) 用 objectId 设置文件 —— 与 playwright 一致
    await this.cdp.send(this.wsUrl, 'DOM.setFileInputFiles', {
      files: [filePath],
      objectId: objectId,
    });

    // 3) 释放句柄（避免页面里累积引用）
    try {
      await this.cdp.send(this.wsUrl, 'Runtime.releaseObject', { objectId: objectId });
    } catch (e) { }

    return true;
  }

  close() { this._closed = true; }
}

// ---------------- Browser ----------------
class Browser {
  constructor(cdp) { this.cdp = cdp; }
  contexts() { return [this.cdp.ctx]; }
  close() { this.cdp.closeAll(); }
}

// ---------------- Context ----------------
class Context {
  constructor(cdp) { this.cdp = cdp; }
  pages() { return this.cdp.pages(); }

  async newPage() {
    // 打开一个新标签
    const r = await this.cdp.callHttp('/json/new?about:blank', 'PUT');
    const targetId = r.id;
    const page = await this.cdp.attach(targetId, r.webSocketDebuggerUrl);
    return page;
  }

  async cookies(url) {
    // 从 CDP Network.getCookies 取（注意：需要任一 page 的 session）
    const pages = this.pages();
    const p = pages.find(function (x) { return x.url().indexOf('pan.xunlei.com') >= 0; }) || pages[0];
    if (!p) return [];
    try {
      const r = await this.cdp.send(p.wsUrl, 'Network.getCookies', { urls: [url] });
      return (r && r.cookies) ? r.cookies : [];
    } catch (e) { return []; }
  }
}

// ---------------- CDP 连接池 ----------------
class Cdp {
  constructor(debugUrl) {
    // 不再无条件信任调用方给的字面 9222：动态端口由 BrowserManager 写在 cdp-port.txt，
    // 见文件开头的 readCdpPort()。显式传入非 9222 的地址时尊重调用方（测试/调试用）。
    this.debugUrl = resolveDebugUrl(debugUrl);
    this.sessions = new Map();   // wsUrl -> { ws, nextId, pending }
    this._pages = [];
    this.ctx = new Context(this);
    this.browser = new Browser(this);
  }

  // 防冒充：确认端点确实是 Edge/Chrome 的真 CDP 服务。
  //
  // 为什么需要这一层（红队实测出的攻击）：cdp-port.txt 可被本机任意程序改写指向假 CDP。
  // 假服务只要返回 {"Browser":"Edg/120..."} 就能通过原来那个前缀检查，
  // 然后返回伪造的 /json/list，让 _verifyRemoteFile 认定"云端已有该文件" ——
  // 界面显示上传成功、云端什么都没有（静默丢数据），
  // 而且 setInputFiles 会把待备份文件的本地路径交给对方。
  //
  // 所以不能只信字符串，还要验证 CDP 的协议级特征：
  //   1. /json/version 必须同时给出 webSocketDebuggerUrl（真 CDP 一定有）
  //   2. /json/list 每一项必须是 {id,type,url,webSocketDebuggerUrl} 结构
  //   3. 真浏览器的 /json/version 一定带 "Protocol-Version"（Chromium 的固定字段）
  async assertRealBrowser() {
    const ver = await this.callHttp('/json/version', 'GET');
    if (!ver || typeof ver !== 'object') {
      throw new Error('CDP 端点返回了非 JSON 的 /json/version，已拒绝连接 ' + this.debugUrl);
    }
    const b = String(ver.Browser || '');
    const proto = String(ver['Protocol-Version'] || '');
    const ws = String(ver.webSocketDebuggerUrl || '');

    if (!/^(Edg|Chrome)\//i.test(b)) {
      throw new Error('CDP 端点不是浏览器（Browser="' + b + '"），可能被其他程序冒充；已拒绝连接 '
        + this.debugUrl);
    }
    // Chromium 的 /json/version 必然带 Protocol-Version（形如 "1.3"）与浏览器级 ws 地址。
    // 假服务常常只伪造 Browser 字段。
    if (!/^\d+\.\d+$/.test(proto)) {
      throw new Error('CDP 端点缺少合法的 Protocol-Version（got "' + proto
        + '"），疑似冒充；已拒绝连接 ' + this.debugUrl);
    }
    if (!/^wss?:\/\//i.test(ws)) {
      throw new Error('CDP 端点缺少 webSocketDebuggerUrl，疑似冒充；已拒绝连接 ' + this.debugUrl);
    }

    // /json/list 的结构校验：真 CDP 的每项都有 id/type/webSocketDebuggerUrl
    const list = await this.callHttp('/json/list', 'GET');
    if (!Array.isArray(list)) {
      throw new Error('CDP /json/list 不是数组，疑似冒充；已拒绝连接 ' + this.debugUrl);
    }
    for (const t of list) {
      if (!t || typeof t !== 'object') {
        throw new Error('CDP /json/list 含非法项，疑似冒充；已拒绝连接 ' + this.debugUrl);
      }
      if (!t.id || !t.type || !t.webSocketDebuggerUrl) {
        throw new Error('CDP /json/list 项缺少 id/type/webSocketDebuggerUrl，疑似冒充；已拒绝连接 '
          + this.debugUrl);
      }
    }
    return b;
  }

  callHttp(pathAndQuery, method) {
    return new Promise(function (resolve, reject) {
      const u = new URL(this.debugUrl + pathAndQuery);
      const req = http.request({
        hostname: u.hostname, port: u.port, path: u.pathname + u.search,
        method: method || 'GET', timeout: 8000,
      }, function (res) {
        let d = '';
        res.on('data', function (c) { d += c; });
        res.on('end', function () {
          try { resolve(JSON.parse(d)); } catch (e) { resolve(d); }
        });
      });
      req.on('error', reject);
      req.on('timeout', function () { req.destroy(new Error('timeout')); });
      req.end();
    }.bind(this));
  }

  async _open(wsUrl) {
    let s = this.sessions.get(wsUrl);
    if (s) return s;

    const ws = new WebSocket(wsUrl);
    s = { ws: ws, nextId: 0, pending: new Map(), page: null };
    this.sessions.set(wsUrl, s);

    await new Promise(function (resolve, reject) {
      ws.addEventListener('open', function () { resolve(); }, { once: true });
      ws.addEventListener('error', function (e) { reject(new Error('WebSocket 连接失败')); }, { once: true });
    });

    const self = this;
    ws.addEventListener('message', function (ev) {
      let msg;
      try { msg = JSON.parse(ev.data); } catch (e) { return; }

      // 命令响应
      if (msg.id !== undefined && s.pending.has(msg.id)) {
        const { resolve, reject } = s.pending.get(msg.id);
        s.pending.delete(msg.id);
        if (msg.error) reject(new Error(msg.error.message || JSON.stringify(msg.error)));
        else resolve(msg.result);
        return;
      }

      // 事件 → 转成 playwright 风格的 request/response 对象
      const page = s.page;
      if (!page) return;

      if (msg.method === 'Network.requestWillBeSent') {
        const p = msg.params;
        const hdrs = p.request.headers || {};
        const reqObj = {
          url: function () { return p.request.url; },
          method: function () { return p.request.method; },
          headers: function () { return hdrs; },
          allHeaders: async function () { return hdrs; },
          _requestId: p.requestId,
        };
        // 缓存 requestId -> request，供 response 事件关联
        page._reqById = page._reqById || {};
        page._reqById[p.requestId] = reqObj;
        page._emit('request', reqObj);
      } else if (msg.method === 'Network.requestWillBeSentExtraInfo') {
        // 这里才有浏览器加上的真实头（x-captcha-token 等）
        const hdrs = msg.params.headers || {};
        page._extraById = page._extraById || {};
        page._extraById[msg.params.requestId] = hdrs;

        // 把 extra headers 合并到已发出的 request 对象上
        const reqObj = page._reqById && page._reqById[msg.params.requestId];
        if (reqObj) {
          reqObj.allHeaders = async function () { return hdrs; };
          reqObj.headers = function () { return hdrs; };
        }
        // 也通知 request 监听器一次（带完整头）
        if (reqObj) page._emit('request', reqObj);
      } else if (msg.method === 'Network.responseReceived') {
        const p = msg.params;
        const reqObj = (page._reqById && page._reqById[p.requestId]) || {
          url: function () { return p.response.url; },
          method: function () { return ''; },
        };
        const respObj = {
          url: function () { return p.response.url; },
          status: function () { return p.response.status; },
          request: function () { return reqObj; },
          text: async function () {
            const r = await self.send(wsUrl, 'Network.getResponseBody', { requestId: p.requestId });
            let body = r && r.body ? r.body : '';
            if (r && r.base64Encoded) {
              body = Buffer.from(body, 'base64').toString('utf8');
            }
            return body;
          },
          _requestId: p.requestId,
        };
        page._emit('response', respObj);
      }
    });

    return s;
  }

  async send(wsUrl, method, params) {
    const s = await this._open(wsUrl);
    const id = ++s.nextId;
    const payload = JSON.stringify({ id: id, method: method, params: params || {} });
    const p = new Promise(function (resolve, reject) {
      s.pending.set(id, { resolve: resolve, reject: reject });
    });
    s.ws.send(payload);
    // 30 秒超时
    const timeout = new Promise(function (_, reject) {
      setTimeout(function () { reject(new Error('CDP 超时: ' + method)); }, 30000);
    });
    return Promise.race([p, timeout]);
  }

  async attach(targetId, wsUrl) {
    const s = await this._open(wsUrl);
    const page = new Page(this, targetId, wsUrl);
    s.page = page;

    // 开网络域（后面要截 captcha）
    try { await this.send(wsUrl, 'Network.enable', { maxTotalBufferSize: 104857600 }); } catch (e) { }
    try { await this.send(wsUrl, 'Page.enable', {}); } catch (e) { }
    try {
      const info = await this.send(wsUrl, 'Target.getTargetInfo', {});
      page._url = (info && info.targetInfo && info.targetInfo.url) || wsUrl;
    } catch (e) { }

    this._pages.push(page);
    return page;
  }

  pages() { return this._pages.slice(); }

  async connect() {
    // 再解析一次端口：引擎可能在 C# 写完 cdp-port.txt 之前就把 Cdp 构造出来了
    // （EngineHost 先起 node，BrowserManager 的端口探测稍后才落盘）。
    // resolveDebugUrl 是幂等的：已经是显式非 9222 地址就原样返回。
    this.debugUrl = resolveDebugUrl(this.debugUrl);

    // 先验明端点身份：不是真浏览器就拒绝（防冒充）
    await this.assertRealBrowser();

    const list = await this.callHttp('/json/list', 'GET');
    if (!Array.isArray(list)) throw new Error('CDP 返回异常');
    const tabs = list.filter(function (t) { return t.type === 'page'; });

    // 复用已有 page
    for (const t of tabs) {
      const exist = this._pages.find(function (p) { return p.targetId === t.id; });
      if (exist) { exist._url = t.url; continue; }
      const pg = await this.attach(t.id, t.webSocketDebuggerUrl);
      pg._url = t.url;
    }
    return this.browser;
  }

  closeAll() {
    for (const s of this.sessions.values()) {
      try { s.ws.close(); } catch (e) { }
    }
    this.sessions.clear();
    this._pages = [];
  }
}

function connectOverCDP(url, opts) {
  const cdp = new Cdp(url);
  return cdp.connect().then(function () { return cdp.browser; });
}

module.exports = {
  chromium: { connectOverCDP: connectOverCDP },
  Cdp: Cdp,
  Page: Page,
  // 端口相关：engine.js 若要显式取地址可调用 getCdpUrl()（可选，不强制）
  getCdpUrl: getCdpUrl,
  readCdpPort: readCdpPort,
  DEFAULT_CDP_PORT: DEFAULT_CDP_PORT,
};
