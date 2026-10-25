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
    this.debugUrl = debugUrl || 'http://127.0.0.1:9222';
    this.sessions = new Map();   // wsUrl -> { ws, nextId, pending }
    this._pages = [];
    this.ctx = new Context(this);
    this.browser = new Browser(this);
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

module.exports = { chromium: { connectOverCDP: connectOverCDP }, Cdp: Cdp, Page: Page };
