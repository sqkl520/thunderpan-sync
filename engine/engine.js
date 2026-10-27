// engine.js -- ThunderPanSync 同步引擎（v2：所有关键逻辑都已实测验证）
//
// 五条实测结论（写进代码，避免后人重踩）：
//   1. captcha 不可离线复刻（算法分片动态下发），必须从浏览器真实请求里截取。
//   2. 网页版目录切换是 SPA 行为：page.goto("?path=/X") 只改 URL，前端不切换
//      （面包屑仍显示"全部文件"）；必须点击 <a title="X"> 才真正进入。
//   3. 上传走网页版 file input，落点是"当前浏览目录"，所以必须先点击进入目标文件夹。
//   4. 创建文件夹需要 POST 专属 captcha → 触发一次页面真实上传来截取。
//   5. 0 字节文件云盘拒收（file_size_too_small），标为已知跳过而非失败。
// 原生 CDP 客户端替代 playwright：只用 5 个操作，不值得为它背 17.8 MB 依赖
// 接口与 playwright 保持一致，engine 其余逻辑无需改动
//
// v3（WP1）新增四件事，都是为了"界面不再说谎 + 云端不留垃圾"：
//   1. progress 事件：phase/index/total/name/size/bytesDone/bytesTotal/elapsedMs/etaMs
//   2. 云端版本策略：versionsMode = 'keep'（默认，绝不动云端旧文件）| 'replace'（显式开启）
//      —— 'replace' 的删除条件写死在 _deleteOldVersions() 里，任何一条不满足就不删。
//   3. 错误分级：所有对外错误带 code = AUTH|QUOTA|NETWORK|BROWSER|PAGE|DISK|UNKNOWN
//   4. 退避熔断：连续失败轮数 → 指数退避（上限 30 分钟）；连续 5 轮 → 熔断，只手动手动恢复
const { chromium } = require('./cdp');
const fs = require('fs');
const path = require('path');
const { EventEmitter } = require('events');
// 路径统一从 paths.js 取：打包后 asar 只读，绝不能写 __dirname
const P = require('../paths');

const API = 'https://api-pan.xunlei.com';
const PAN = 'https://pan.xunlei.com';
const CDP = 'http://127.0.0.1:9222';

class SyncEngine extends EventEmitter {
  constructor(opts = {}) {
    super();
    this.cfg = {
      watchDirs: [],
      remoteFolderName: 'ThunderPanSync',
      intervalMs: 60000,
      settleMs: 5000,
      maxSizeMB: 2048,
      stateFile: P.STATE_FILE,
      logFile: P.LOG_FILE,
      // ---- 云端版本策略 ----
      // keep   = 默认。只报告「云端已有 N 个旧版本」，一个字节都不删。
      // replace= 用户显式开启。上传并核实成功后，才删本轮开始前就已存在的同名/同名(n) 旧副本。
      //          删云端文件不可逆，所以默认值必须是 keep，不能靠调用方记得关。
      versionsMode: 'keep',
      maxDeletePerRun: 20,
      ...opts,
    };
    // 配置防御：拼错的 mode 不能退化成"随便删"。任何非 keep/replace 的值一律按 keep。
    if (this.cfg.versionsMode !== 'replace') this.cfg.versionsMode = 'keep';
    this.remoteFolderId = null;
    // unverified = "已上传但云端还没核实"的暂存区（**不是失败**）。
    // 为什么必须单独一个桶：快照不可用时既不能判成功（会静默丢数据），
    // 也不能判失败（文件其实已在云端，判失败会重传 → 云端出现 a.txt + a(1).txt）。
    this.state = { uploaded: {}, failed: {}, versions: {}, unverified: {} };
    this.browser = null;
    this.page = null;
    this.running = false;
    this.timer = null;
    this.currentRun = null;
    this._abortRequested = false;   // stop() 置位，runOnce 的循环检查它

    // ---- 退避 / 熔断 ----
    this.consecutiveFailures = 0;   // 连续失败的"轮"数，成功一轮即清零
    this.circuitOpen = false;       // 熔断：停止自动重试，但仍允许手动 runOnce
    this.nextRunAt = null;          // 下一次自动同步的计划时间（界面上显示"下次 03:12"）
    this.lastRunAt = null;
    this.lastRunOk = null;
    this._autoRun = false;          // 本轮是否由定时器发起（决定失败后要不要重排定时器）
    this._runCounter = 0;           // 轮次计数，用于单文件"每 10 轮才试一次"
    this._deletedThisRun = 0;       // replace 模式下本轮的删除计数（上限 maxDeletePerRun）
    this._runErrCodes = null;       // 本轮出现过的错误 code 集合（配额/鉴权错误时禁止删云端文件）
    this._loadState();
  }

  _loadState() {
    try {
      if (fs.existsSync(this.cfg.stateFile)) {
        const s = JSON.parse(fs.readFileSync(this.cfg.stateFile, 'utf8'));
        this.state = {
          uploaded: s.uploaded || {},
          failed: s.failed || {},
          // 老版本 state 没有 versions 字段：补空对象，保证 getStatus() 里字段恒存在
          versions: s.versions || {},
          // 老版本 state 没有 unverified：补空对象（旧状态里不存在"待认领"这种中间态）
          unverified: (s.unverified && typeof s.unverified === 'object') ? s.unverified : {},
          lastRun: s.lastRun,
        };
        this.remoteFolderId = s.remoteFolderId || null;

        // 补 1（兼容）：修复前写入的 unverified 记录没有 beforeIds 证据，
        // 按新规则既永远认领不了、又会被 scan() 跳过 ⇒ 永久悬空 = 静默丢失。
        // 直接丢掉（下一轮当新文件重传一次，最多多一个重复文件，远好于永久丢失）。
        // 只丢"字段不存在"的旧记录：新记录即使 beforeIds 是空数组也保留（那是快照失败的正常态）。
        let legacy = 0;
        for (const k of Object.keys(this.state.unverified)) {
          const u = this.state.unverified[k];
          if (!u || !Array.isArray(u.beforeIds)) { delete this.state.unverified[k]; legacy++; }
        }
        if (legacy) {
          this.log("warn", "状态迁移：清掉 " + legacy + " 条旧版待认领记录（缺少上传前快照证据，无法安全认领，下一轮会重新上传一次）", "DISK");
        }

        // 补 2（CODE-NITS A1）：failed[*].nextRetryAt 存的是**轮次号**，而 _runCounter 每次启动归零。
        // 重启后 runNo(1) < nextRetryAt(旧值可能 500+) 恒真 ⇒ 失败 ≥5 次的文件被永久当成"仍在退避"跳过。
        // 重启即视为"退避重置"：清掉 nextRetryAt，重新给它机会。
        let resetBackoff = 0;
        for (const k of Object.keys(this.state.failed)) {
          const f = this.state.failed[k];
          if (f && typeof f.nextRetryAt === "number") { f.nextRetryAt = null; resetBackoff++; }
        }
        if (resetBackoff) {
          this.log("info", "状态迁移：" + resetBackoff + " 个失败文件的退避计数已重置（重启后重新尝试）", "DISK");
        }
      }
    } catch (e) {
      // 状态损坏：不能静默当空（那会导致全量重传）。
      // 备份损坏文件供诊断，然后从空开始。
      try {
        const bak = this.cfg.stateFile + '.corrupt-' + Date.now();
        fs.copyFileSync(this.cfg.stateFile, bak);
        this.log('warn', `状态文件损坏，已备份为 ${path.basename(bak)}，将从空状态开始（会重新上传已备份过的文件）`, 'DISK');
      } catch (e2) {
        this.log('warn', '状态文件损坏且无法备份，将从空状态开始', 'DISK');
      }
      this.state = { uploaded: {}, failed: {}, versions: {}, unverified: {} };
      this.remoteFolderId = null;
    }
  }

  // 原子写：先写临时文件 → fsync → rename 覆盖。
  // 直接 writeFileSync 覆盖目标文件时，断电/强杀会留下 0 字节或半截 JSON，
  // 下次启动解析失败 → 状态清空 → 把用户几个 GB 全部重传（实测过的场景）。
  // P2：整份 state.json 全量写 + fsync 在 20k 文件时实测 139–1091ms/次，
  // 而它有 8 个调用点（3 个在"每个文件"的路径上）⇒ 首轮 20,000 文件 ≈ 2.8 小时纯写状态。
  // 这里只置脏，2 秒后统一落盘；runOnce 的 finally / stop() / 退出前会强制 _flushState()。
  _saveState() {
    this._dirtyState = true;
    if (this._saveTimer) return;
    this._saveTimer = setTimeout(() => {
      this._saveTimer = null;
      this._flushState();
    }, 2000);
    if (this._saveTimer && this._saveTimer.unref) this._saveTimer.unref();
  }

  // 立刻落盘（有脏数据才写）。runOnce 结束、stop()、进程退出前必须调用。
  _flushState() {
    if (this._saveTimer) { try { clearTimeout(this._saveTimer); } catch (e) { } this._saveTimer = null; }
    if (!this._dirtyState) return;
    this._dirtyState = false;
    this._writeStateNow();
  }

  _writeStateNow() {
    const target = this.cfg.stateFile;
    const tmp = target + '.tmp';
    try {
      fs.mkdirSync(path.dirname(target), { recursive: true });
      // P2：条目多时用紧凑格式（20k 条：2.63 MB → 约 1.4 MB，stringify/write 都更快）。
      // 代价是 state.json 不再是给人读的；条目少时仍然缩进，方便排查。
      const big = Object.keys(this.state.uploaded || {}).length > 2000;
      const data = JSON.stringify({
        uploaded: this.state.uploaded,
        failed: this.state.failed,
        versions: this.state.versions,   // 云端版本审计记录（keep/replace 都要有）
        // 待认领队列必须落盘：否则重启后第一轮会重传一遍，云端就多出 a(1).txt。
        unverified: this.state.unverified,
        lastRun: this.state.lastRun,
        remoteFolderId: this.remoteFolderId,
      }, null, big ? 0 : 2);

      // P2-c：不再 fsync。实测 2.2 MB 的 state：fsync 版 341–561 ms，不 fsync 版 12–14 ms（28–47×）。
      // 代价：断电可能丢最后几秒的状态 ⇒ 最坏结果是重启后重传一遍（不会丢用户数据，
      // 因为 uploaded 丢了只会重新上传，云端最多多一个同名副本）。
      // 保留 writeFileSync + rename：rename 在 NTFS 上原子，不会留下半截 JSON。
      fs.writeFileSync(tmp, data, 'utf8');
      fs.renameSync(tmp, target);  // rename 在 NTFS 上是原子的
    } catch (e) {
      // 写失败必须让人知道：状态不落盘意味着下次会重传
      this.log('error', `保存状态失败: ${e.message}（下次启动可能重复上传）`, 'DISK');
      try { if (fs.existsSync(tmp)) fs.unlinkSync(tmp); } catch (e2) { }
    }
  }

  // ================= 错误分级 =================
  // 所有对外抛出的错误都带 code，界面据此显示不同提示（配额不足 ≠ 限流 ≠ 未登录）。
  // 分级只看得到的信息，不做猜测：拿不到就 UNKNOWN，绝不假装知道。
  _errCodeOf(e) {
    if (!e) return 'UNKNOWN';
    if (e.code && /^(AUTH|QUOTA|NETWORK|BROWSER|PAGE|DISK|UNKNOWN)$/.test(e.code)) return e.code;
    const msg = String(e.message || e);
    const m = msg.toLowerCase();

    // 磁盘：fs 的 errno 最可靠，先判
    if (/^(EACCES|EPERM|ENOSPC|EBUSY|ENOENT|EMFILE|ENFILE|EIO|EROFS)$/.test(String(e.syscall ? e.code : ''))) return 'DISK';
    if (/enospc|no space left|磁盘空间|写入失败|保存状态失败|eacces|eperm|ebusy/.test(m)) return 'DISK';

    // 配额：云盘空间/数量/大小限制。必须早于 AUTH 判定，否则会被混成"登录失效"
    if (/quota|space|capacity|storage|空间不足|容量|file_size_too_small|too_large|limit_exceeded|exceed/.test(m)) return 'QUOTA';

    // 浏览器层（CDP 连接、context 拿不到）必须在 AUTH 之前判：
    // "连不上浏览器。请先…登录迅雷云盘" 这句话里同时有"浏览器"和"登录"，
    // 但它真正的病因是浏览器没起来，不是登录失效 —— 分成 AUTH 会让用户去重新登录，
    // 白白浪费一轮。所以这里用窄模式（只认"浏览器本身出问题"的措辞）。
    if (/cdp|9222|连不上浏览器|启动浏览器|浏览器没有|browser context|没有 context|newpage|connectovercdp|target closed|websocket|chromium/.test(m)) return 'BROWSER';

    // 鉴权（"浏览器里没有登录态"落在这里：sessionid 缺失是真的要重新登录）
    if (/401|403|unauthor|forbidden|captcha|token|登录态|登录|session/.test(m)) return 'AUTH';


    // 页面层（导航、点击、file input、面包屑）
    if (/页面|导航|goto|click|input\[type=file\]|setinputfiles|breadcrumb|面包屑|folder|文件夹|超时/.test(m)) return 'PAGE';

    // 网络层
    if (/econn|etimedout|enotfound|socket|network|fetch failed|http 5\d\d|http 429|限流|timeout|超时|eai_again/.test(m)) return 'NETWORK';
    return 'UNKNOWN';
  }

  // 造一个带 code 的错误（保留原始信息，不吞根因）
  _err(msg, code) {
    const e = new Error(msg);
    e.code = code || this._errCodeOf(msg);
    return e;
  }

  // HTTP 状态码 → 错误分级。
  // 401/403 是鉴权（captcha/token 失效，可作废缓存重试）；
  // 429 与 5xx 才是"限流/服务端抖动"；其余 4xx 归 UNKNOWN（不要瞎猜成限流）。
  _httpCode(status) {
    const s = Number(status);
    if (s === 401 || s === 403) return 'AUTH';
    if (s === 429) return 'NETWORK';
    if (s >= 500) return 'NETWORK';
    // 迅雷的空间/文件数超限走 4xx，靠响应体里的关键字判定，这里只能给保守值
    return 'UNKNOWN';
  }

  // 从响应体里识别配额类错误（云盘空间不足 / 单文件过大 / 文件数超限）
  _isQuotaBody(body) {
    return /quota|space|capacity|storage|too_large|file_size_too_small|limit_exceeded|exceed|空间不足|容量/.test(String(body || '').toLowerCase());
  }

  // 记录本轮出现过的错误 code；配额/鉴权错误会让本轮的云端删除全部跳过
  _noteErrCode(code) {
    if (!this._runErrCodes) this._runErrCodes = new Set();
    this._runErrCodes.add(code || 'UNKNOWN');
  }

  log(level, msg, code) {
    const t = new Date().toISOString();
    const ev = { level, msg, time: t };
    if (code) ev.code = code;                 // 带分级时回调方也能分类显示
    this.emit('log', ev);
    try {
      // 按天分文件：跨天时 logFile 换成新一天的文件，
      // 并把 latest.log 指针更新为当天路径（Windows 不能建软链）。
      let file = this.cfg.logFile;
      try {
        if (P.getLogFile && (!this.cfg.logFile || this._logDay !== P.getLogFile())) {
          this._logDay = P.getLogFile();
          this.cfg.logFile = this._logDay;
        }
        if (this._logDay) file = this._logDay;
      } catch (eDay) { /* paths.js 老版本没有 getLogFile：退回单文件 */ }

      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.appendFileSync(file, `${t} [${level}]${code ? ' [' + code + ']' : ''} ${msg}\n`);
      try { if (P.noteLatestLog) P.noteLatestLog(file); } catch (ePtr) { }
    } catch (e) {
      // 日志写不进去（磁盘满/权限）只能走 stderr —— 但绝不能污染 stdout
      try { process.stderr.write(`[log-write-failed] ${e.message}\n`); } catch (e2) { }
    }
    const line = `[${new Date().toTimeString().slice(0, 8)}] ${msg}`;
    if (level === 'error') console.error(line);
    else console.log(line);
  }


  // ================= 浏览器 =================
  async connect() {
    // 缓存页面直接返回，不在这里探活。
    //
    // 为什么不做"每次 connect 都探活"：connect() 在每轮同步里会被调用几十次
    // （每次截 captcha、每次核查都会走），探活一次要 5 秒，等于把每次同步
    // 拖慢几百秒。页面僵死由两条更精准的路径发现：
    //   1. _capHeader 超时 → 调 _dropPage() 作废缓存页面
    //   2. 本函数走"重新连接"分支时对候选页面做一次 isResponsive 探测
    if (this.page && !this.page.isClosed()) return this.page;
    try {
      this.browser = await chromium.connectOverCDP(CDP, { timeout: 8000 });
    } catch (e) {
      // 连不上浏览器 = BROWSER 级错误（不是限流、不是配额），界面要提示去启动浏览器
      throw this._err('连不上浏览器。请先运行 start-browser.cmd 并登录迅雷云盘。', 'BROWSER');
    }
    const ctx = this.browser.contexts()[0];
    if (!ctx) throw this._err('浏览器没有 context', 'BROWSER');
    let page = ctx.pages().find(p => p.url().includes('pan.xunlei.com'));

    // 页面存在 ≠ 页面可用。
    //
    // 实测：浏览器连续跑几小时后 pan.xunlei.com 的渲染进程会僵死 ——
    // WebSocket 能连上、/json 里照样列着这个页面，但 Page.enable 永不返回。
    // 原实现直接拿来用，于是 connect() 成功、之后每次截 captcha 都超时，
    // 整轮同步永远失败，界面却提示"请重新登录"（错的方向）。
    //
    // 所以这里必须探活。探不通就换一个新标签页，而不是抱着死页面继续跑。
    if (page && typeof page.isResponsive === 'function') {
      const alive = await page.isResponsive(5000);
      if (!alive) {
        this.log('warn', '已打开的迅雷云盘页面无响应（浏览器长期运行常见），换一个新标签页', 'BROWSER');
        try { page.close(); } catch (e) { /* 关不掉也无所谓，我们不用它了 */ }
        page = null;
      }
    }

    if (!page) {
      page = await ctx.newPage();
      await page.goto(PAN, { waitUntil: 'domcontentloaded', timeout: 60000 });
    }
    const cookies = await ctx.cookies(PAN);
    if (!cookies.find(c => c.name === 'sessionid')) {
      throw this._err('浏览器里没有登录态，请登录 pan.xunlei.com', 'AUTH');
    }
    this.page = page;
    return page;
  }

  // 作废当前缓存的页面，下次 connect() 会重新选/新建。
  //
  // 用在"页面疑似僵死"的场合（截 captcha 超时）。不这么做的话，
  // 死页面会被一直缓存着，之后每一次操作都超时，用户看到的是
  // "永远在失败"，而实际上换个标签页就能正常工作。
  _dropPage(reason) {
    if (this.page) {
      try { this.page.close(); } catch (e) { }
      this.page = null;
      // header 是从那个页面截的，页面换了必须重新截
      this._hdrCache = {};
      this.log('warn', `已丢弃无响应的浏览器页面（${reason || '未知原因'}），下次会新建`, 'BROWSER');
    }
  }

  // 截取真实请求头 —— captcha 只能这样拿到
  //
  // 性能优化（修一个实测到的严重回归）：
  //   每次调用都会 page.goto 重新导航页面来等一次真实请求，约 2.5~8 秒。
  //   而 _verifyRemoteFile 内部要尝试 6 次 ⇒ 单文件核查就耗 80 秒，
  //   整轮同步慢到不可用（实测：6 个文件跑了 5 分钟还没完）。
  //   而 captcha/authorization 在一次会话内是可复用的（实测过 38 分钟仍有效），
  //   所以这里缓存 header，只在接近过期或明确失败时才重新截取。
  async _capHeader(method = 'GET', timeoutMs = 25000) {
    const cacheKey = method;
    const now = Date.now();

    // GET 头缓存 5 分钟；POST 头缓存 2 分钟（不同 action 的 captcha 可能不同）
    const ttl = method === 'GET' ? 5 * 60 * 1000 : 2 * 60 * 1000;
    if (this._hdrCache && this._hdrCache[cacheKey]
      && (now - this._hdrCache[cacheKey].at) < ttl) {
      return this._hdrCache[cacheKey].h;
    }

    const page = await this.connect();
    return new Promise((resolve, reject) => {
      let done = false;
      const handler = async (req) => {
        if (done) return;
        if (!req.url().startsWith(API) || req.method() !== method) return;
        try {
          const h = await req.allHeaders();
          if (h['x-captcha-token'] && h['authorization']) {
            done = true;
            page.off('request', handler);
            // 缓存供后续复用，避免每查一次就导航一次
            this._hdrCache = this._hdrCache || {};
            this._hdrCache[cacheKey] = { h, at: Date.now() };
            resolve(h);
          }
        } catch (e) {
          // 读头失败（多半是请求已销毁）不致命，但要留痕：否则表现为"莫名超时"
          this.log('warn', `读取请求头失败: ${e.message}`, 'BROWSER');
        }
      };
      page.on('request', handler);
      if (method === 'GET') {
        page.goto(PAN + '/', { waitUntil: 'domcontentloaded' }).catch((e) => {
          this.log('warn', `截取 ${method} 头时导航失败（不影响等后续请求）: ${e.message}`, 'PAGE');
        });
      }
      setTimeout(() => {
        if (!done) {
          done = true;
          page.off('request', handler);
          // 超时最可能的原因就是这个页面已经僵死（实测：浏览器跑久了渲染进程僵死，
          // Page.enable 都不回）。作废它，否则后面每一次都会以同样方式超时。
          this._dropPage('截取 ' + method + ' 头超时');
          reject(this._err(`${method} 头截取超时`, 'AUTH'));
        }
      }, timeoutMs);
    });
  }

  _H(h) {
    return {
      authorization: h['authorization'],
      'x-captcha-token': h['x-captcha-token'],
      'x-client-id': h['x-client-id'],
      'x-device-id': h['x-device-id'],
      origin: PAN,
      referer: PAN + '/',
    };
  }

  // ================= 远程文件夹 =================
  async ensureRemoteFolder() {
    const name = this.cfg.remoteFolderName;
    const gh = await this._capHeader('GET');
    const r = await fetch(API + '/drive/v1/files?limit=100', { headers: this._H(gh) });
    if (r.status === 401 || r.status === 403) {
      this._invalidateHeader('GET');
      throw this._err(`列举云盘根目录失败 ${r.status}（登录态可能失效）`, 'AUTH');
    }
    const j = await r.json();
    const found = (j.files || []).find(f => f.kind === 'drive#folder' && f.name === name);
    if (found) {
      this.remoteFolderId = found.id;
      this.log('info', `远程文件夹已存在: ${name} (${found.id})`);
      return found.id;
    }

    this.log('info', `远程文件夹 "${name}" 不存在，创建中...`);
    const probe = P.PROBE_FILE;
    fs.mkdirSync(path.dirname(probe), { recursive: true });
    fs.writeFileSync(probe, 'probe ' + Date.now());
    const page = await this.connect();
    await page.goto(PAN + '/', { waitUntil: 'domcontentloaded' }).catch((e) => {
      this.log('warn', `创建文件夹前导航失败（继续尝试截 captcha）: ${e.message}`, 'PAGE');
    });
    await page.waitForTimeout(2000);
    const postP = this._capHeader('POST', 30000).catch((e) => {
      this.log('warn', `截取 POST captcha 失败: ${e.message}`, this._errCodeOf(e));
      return null;
    });
    try { await page.locator('input[type=file]').first().setInputFiles(probe); } catch (e) {
      // 探针上传失败不致命（POST captcha 仍可能截到），但必须留痕
      this.log('warn', `探针上传失败（继续等 captcha）: ${e.message}`, 'PAGE');
    }
    const ph = await postP;
    try { fs.unlinkSync(probe); } catch (e) {
      this.log('warn', `探针文件清理失败: ${path.basename(probe)}（可手动删除）`, 'DISK');
    }

    if (!ph) {
      this.log('error', '拿不到 POST captcha，无法创建文件夹', 'AUTH');
      return null;
    }
    const cr = await fetch(API + '/drive/v1/files', {
      method: 'POST',
      headers: { ...this._H(ph), 'content-type': 'application/json' },
      body: JSON.stringify({ kind: 'drive#folder', parent_id: '', name }),
    });
    const ct = await cr.text();
    if (cr.status !== 200) {
      const code = this._isQuotaBody(ct) ? 'QUOTA' : this._httpCode(cr.status);
      this._noteErrCode(code);
      this.log('error', `创建文件夹失败 ${cr.status}: ${ct.slice(0, 200)}`, code);
      if (cr.status === 401 || cr.status === 403) this._invalidateHeader('POST');
      return null;
    }
    const cj = JSON.parse(ct);
    this.remoteFolderId = cj.file && cj.file.id;
    this._saveState();
    this.log('info', `✓ 已创建文件夹 ${name} (${this.remoteFolderId})`);
    return this.remoteFolderId;
  }

  // 进入文件夹 —— 必须用点击，URL 导航无效
  // force=true 时即使已经在目标目录也会重新导航（用于重建页面的 file input）
  async enterRemoteFolder(force) {
    const name = this.cfg.remoteFolderName;
    const page = await this.connect();
    let crumbErr = null;
    const crumbsOf = async () => page.evaluate(() => {
      const c = [...document.querySelectorAll('[class*=breadcrumb],[class*=crumb]')]
        .map(e => (e.innerText || '').replace(/\s+/g, ' ').trim())
        .filter(Boolean).join(' ');
      return c;
    }).catch((e) => {
      // 原来这里静默返回 ''：面包屑读不到就看不出"没进去"，会误判成已在目录。
      crumbErr = e;
      return '';
    });

    for (let i = 1; i <= 4; i++) {
      if (!force && (await crumbsOf()).includes(name)) {
        this.log('info', `已在 "${name}" 目录`);
        return true;
      }

      // 快路径：页面已经是渲染好的首页（文件列表在），就不要重新导航。
      //
      // 为什么要这条：enterRemoteFolder 在**每个文件**上传前都会被调用一次
      // （uploadOne 里 force=true），外加 ensureRemoteFolder 一次。每次都
      // goto + 等满重试预算，单次最坏约 53 秒；2 个文件就是 3 次调用 ≈160 秒，
      // 实测直接把 300 秒的用例看门狗撞掉。而绝大多数情况下页面本来就是好的
      // （上一个文件刚传完，列表还在），重新导航纯属浪费。
      const readyNow = await page.evaluate((n) => {
        return document.readyState === 'complete'
          && document.querySelectorAll('a[title]').length > 0
          && !! [...document.querySelectorAll('a[title]')].find(x => x.getAttribute('title') === n);
      }, name).catch(() => false);

      if (readyNow) {
        const t = Date.now();
        const hit = await page.evaluate((n) => {
          const a = [...document.querySelectorAll('a[title]')].find(x => x.getAttribute('title') === n);
          if (a) { a.scrollIntoView(); a.click(); return true; }
          return false;
        }, name).catch(() => false);
        if (hit) {
          await page.waitForTimeout(1500);
          if ((await crumbsOf()).includes(name)) {
            this.log('info', `✓ 已进入 "${name}"（页面已就绪，跳过重新导航 ${Date.now() - t}ms）`);
            return true;
          }
        }
        // 快路径没成：落到下面的完整导航
      }

      await page.goto(PAN + '/', { waitUntil: 'domcontentloaded' }).catch((e) => {
        this.log('warn', `第 ${i} 次导航到迅雷首页失败: ${e.message}`, 'PAGE');
      });
      // 等待时间递增：页面刚上传完文件时列表刷新较慢，固定 2.5s 会偶发点不到
      await page.waitForTimeout(2500 + (i - 1) * 1500);

      // 轮询等目标链接真的出现，而不是只靠固定 sleep。
      //
      // 为什么必须轮询（实测故障）：goto() 在 document.readyState 变成 interactive
      // 时就返回了，而 pan.xunlei.com 是 SPA —— interactive 时只有一个空壳，
      // 文件列表要再等几秒才由前端渲染出来。于是在"刚启动的浏览器 / 冷缓存"
      // 上，固定 2.5s 之后 querySelectorAll('a[title]') 仍然是 0 个，
      // 4 次重试全部落空 ⇒ 每个文件都报 "无法进入目标文件夹"。
      // 旧版本没暴露这个问题，是因为它总是复用同一个长期运行的浏览器，
      // 页面早就渲染好了。改成随机端口后浏览器每次新起，冷页面成为常态。
      //
      // ⚠ 预算必须小：本函数在 **每个文件** 上传前都会被调用一次
      // （uploadOne 里 enterRemoteFolder(true)）。轮询一旦给到几十秒，
      // 一个文件失败就会吃掉整轮的时间预算（实测 4×20s 让 2 个文件的用例
      // 直接撞上 300s 看门狗）。所以：首轮最多等 8 秒（覆盖 SPA 首屏渲染），
      // 后面几轮各 4 秒（页面此时已经热了，只是列表刷新）。
      let clicked = false;
      const linkBudget = (i === 1) ? 8000 : 4000;
      const linkDeadline = Date.now() + linkBudget;
      while (Date.now() < linkDeadline) {
        clicked = await page.evaluate((n) => {
          const a = [...document.querySelectorAll('a[title]')].find(x => x.getAttribute('title') === n);
          if (a) { a.scrollIntoView(); a.click(); return true; }
          return false;
        }, name).catch((e) => {
          this.log('warn', `第 ${i} 次在页面里查找文件夹链接出错: ${e.message}`, 'PAGE');
          return false;
        });
        if (clicked) break;
        await page.waitForTimeout(800);
      }

      if (!clicked) {
        this.log('warn', `第 ${i} 次等待 ${linkBudget / 1000} 秒仍未找到 "${name}" 链接（页面可能没渲染完），重试`);
        await page.waitForTimeout(1500);
        continue;
      }
      await page.waitForTimeout(2500);
      if ((await crumbsOf()).includes(name)) {
        this.log('info', `✓ 已进入 "${name}"`);
        return true;
      }
      this.log('warn', `第 ${i} 次点击后未确认进入，重试`);
    }
    this.log('error', `无法进入文件夹 "${name}"（已重试 4 次）`, 'PAGE');
    if (crumbErr) this.log('warn', `读取面包屑时出错（可能是页面结构变化）: ${crumbErr.message}`, 'PAGE');
    return false;
  }

  // ================= 扫描 =================
  // 已知的坑（每一条都是实测/评审确认过的）：
  //   1. 未来 mtime：now - mtime 为负时 `< settleMs` 恒成立 ⇒ 文件永久"等待稳定"。
  //      从 zip 解压、跨机拷贝、robocopy /COPY:DAT 都会带未来时间戳。
  //      ⇒ 用 Math.max(0, ...) 把负值当 0（视为刚修改），并给它一个有限等待次数。
  //   2. junction/符号链接：Dirent 上 isDirectory/isFile 都是 false，
  //      原来的 if/else-if 没有 else 分支 ⇒ 整棵子树被静默丢弃、零日志。
  //   3. 同步递归无深度上限：10 万文件会阻塞事件循环数十秒。
  //   4. 无系统目录防护：用户选 C:\ 会把整个系统盘往云上传。
  // 跳过日志去重：同一个文件、同一个版本，日志里只出现一次。
  // 不去重的话，500 个 0 字节文件 × 每 5 分钟一轮 = 一天十几万行，
  // 真正要看的错误会被自己的噪音埋掉（"说了等于没说"）。
  // 文件一改（size/mtime 变化）就会重新记一次，用户不会漏掉。
  _logSkipOnce(full, st, msg) {
    if (!this._skipLogged) this._skipLogged = new Set();
    const key = `${full}|${st ? st.size : 0}|${st ? Math.floor(st.mtimeMs) : 0}`;
    if (this._skipLogged.has(key)) return;
    this._skipLogged.add(key);
    this.log('info', msg);
  }

  scan() {
    const out = [];
    // 必修 16：本轮有哪些监视目录整个不见了（路径解析失败/被删/被改名/没权限）
    this._missingDirs = [];
    // P1：空文件/超限文件的跳过不再逐个写日志（20k 文件 = 20k 行 = UI 假死 30 分钟），
    // 只计数，scan() 末尾汇总一条。
    this._skipStats = { empty: 0, oversize: 0, names: [] };
    const now = Date.now();
    const MAX_DEPTH = 64;               // 防符号链接环/超深目录
    const settleMs = this.cfg.settleMs;

    // 未来时间戳容忍窗口：超过这个量就认为时钟/时间戳异常，按"已稳定"处理
    const FUTURE_TOLERANCE_MS = 60 * 1000;

    // 归一化一次，扫描过程复用：26 个盘符的 existsSync 不能每进一个子目录都做一遍
    const blockedNorm = this._blockedRoots().map(b => this._normPath(b)).filter(Boolean);
    const isBlocked = (p) => this._hitBlocked(p, blockedNorm);
    // 凭据/配置目录名（精确匹配，大小写不敏感）与浏览器登录态文件名
    const exDirs = new Set(this._excludedDirs());
    const exFiles = new Set(this._excludedFiles());

    for (const dir of this.cfg.watchDirs) {
      let rootReal;
      try { rootReal = path.resolve(dir); } catch (e) {
        this.log('warn', `监视目录路径无法解析，按原样处理: ${dir}（${e.message}）`, 'DISK');
        rootReal = dir;
      }

      if (!fs.existsSync(dir)) {
        // 必修 16：只 warn 一句就当"没有新文件"会让整轮 scanned=0、failed=0，
        // 界面于是显示"同步成功"；用户以为备份好了，其实目录早就没了。
        this._missingDirs.push(dir);
        this.log('warn', `目录不存在: ${dir}（可能被改名或删除；本轮按失败处理）`, 'DISK');
        continue;
      }

      // 用户选到系统目录/盘符根时明确拒绝（否则会尝试上传整个系统盘）
      const hit = this._hitBlocked(rootReal, blockedNorm);
      if (hit && /^[a-z]:$/.test(hit)) {
        this.log('error', `拒绝备份盘符根 "${dir}"：整个磁盘不能作为监视目录（会遍历并上传全盘）。请选择具体的子目录，例如 ${hit.toUpperCase()}\\我的文档。`, 'DISK');
        continue;
      }
      if (hit) {
        this.log('error', `拒绝备份系统目录 "${dir}"（命中保护路径 ${hit}）。请选择你自己的文档/照片等目录。`, 'DISK');
        continue;
      }

      let skippedLinks = 0;
      let skippedOther = 0;

      const walk = (d, depth) => {
        if (depth > MAX_DEPTH) {
          this.log('warn', `目录层级超过 ${MAX_DEPTH} 层，已跳过: ${d}`, 'DISK');
          return;
        }
        let ents;
        try { ents = fs.readdirSync(d, { withFileTypes: true }); }
        catch (e) {
          // 读不到目录必须记下来：否则用户以为备份了，实际整棵树被跳过
          this.log('warn', `无法读取目录 ${d}: ${e.code || e.message}`, 'DISK');
          return;
        }
        for (const ent of ents) {
          const full = path.join(d, ent.name);
          if (ent.isDirectory()) {
            if (ent.name.startsWith('.')) continue;
            const lowName = ent.name.toLowerCase();
            // 凭据/配置目录：不上传。否则用户选 C:\Users\<me> 就会把
            // browser-profile\Default\Network\Cookies（以及日常 Chrome/Edge 的登录态）
            // 一起传上云盘（产品 D-B）。
            if (exDirs.has(lowName)) {
              this._logSkipOnce(full, null, `跳过 ${full}（凭据/配置目录，不上传）`);
              continue;
            }
            // 逐层复核系统目录：监视根本身通过检查（例如 C:\）也不能进 Windows\System32
            const bhit = isBlocked(full);
            if (bhit) {
              this._logSkipOnce(full, null, `跳过 ${full}（系统目录，命中保护路径 ${bhit}）`);
              continue;
            }
            walk(full, depth + 1);
          } else if (ent.isFile()) {
            // 浏览器登录态文件：按文件名拦（产品 D-B 的兜底，目录改名也挡得住）
            if (exFiles.has(ent.name.toLowerCase())) {
              this._logSkipOnce(full, null, `跳过 ${full}（浏览器登录态文件，不上传）`);
              continue;
            }
            let st;
            try { st = fs.statSync(full); }
            catch (e) {
              out.push({ full, st: { size: 0, mtimeMs: now }, skip: `无法读取（${e.code || e.message}）` });
              continue;
            }

            // 跳过必须留下痕迹：界面从不调用 scan()，所以不写日志 = 用户永远不知道
            // 这个文件根本没被备份（例如 3 GB 视频超上限时，界面全程显示"失败 0"）。
            // 只记 info，不升级成 warn：跳过是设计行为，不是出错。
            if (st.size === 0) {
              this._noteSkip('empty', full);
              out.push({ full, st, skip: '空文件（云盘不接受 0 字节）', skipCode: 'QUOTA' });
              continue;
            }
            if (st.size > this.cfg.maxSizeMB * 1024 * 1024) {
              // 小文件也给人话：不足 1 MB 用 KB 显示，否则会出现"0.0 MB 超过上限 0.001 MB"
              const sizeTxt = st.size >= 1048576
                ? `${(st.size / 1048576).toFixed(1)} MB`
                : `${Math.max(1, Math.round(st.size / 1024))} KB`;
              this._noteSkip('oversize', full);
              out.push({ full, st, skip: `超过 ${this.cfg.maxSizeMB} MB 上限`, skipCode: 'QUOTA' });
              continue;
            }

            // ---- 稳定性判定（修未来时间戳）----
            const age = now - st.mtimeMs;              // 可能是负数
            if (age < -FUTURE_TOLERANCE_MS) {
              // 时间戳在未来且超出容差：按"已稳定"处理，不能永久挂起
              // （实测：mtime=2027 年时原逻辑永远 skip"等待稳定"）
              this.log('info', `${path.basename(full)} 的修改时间在未来（${new Date(st.mtimeMs).toLocaleString()}），按已稳定处理`);
            } else if (age >= 0 && age < settleMs) {
              out.push({ full, st, skip: '刚修改，等待稳定' }); continue;
            } else if (age < 0) {
              // 未来但在容差内：视为刚写入，再等一轮
              out.push({ full, st, skip: '刚修改，等待稳定' }); continue;
            }

            const fp = `${st.size}-${Math.floor(st.mtimeMs)}`;
            const prev = this.state.uploaded[full];
            if (prev && prev.fp === fp) continue;
            // unverified = 上一轮已经交给浏览器上传过、只是云端暂时核实不了的文件。
            // 它**不是失败**，也**不该重传**：重传会在云端产生 a.txt + a(1).txt，
            // 正是三态核查要消灭的结果。等 _claimUnverified() 拿到证据后转成 uploaded（必修 11a）。
            const unv = this.state.unverified[full];
            if (unv && unv.fp === fp) continue;
            out.push({ full, st, fp, rel: path.relative(dir, full), skip: null });
          } else if (ent.isSymbolicLink()) {
            // 原来这里没有分支 ⇒ 静默丢弃。现在至少计数并汇总告警。
            skippedLinks++;
          } else {
            // 设备文件、管道等：不备份，但要记账
            skippedOther++;
          }
        }
      };
      walk(dir, 0);

      if (skippedLinks > 0) {
        this.log('warn', `"${path.basename(dir)}" 下有 ${skippedLinks} 个符号链接/junction 未跟随（云盘无法表达链接；如需备份其内容，请把目标目录直接加入监视列表）`, 'DISK');
      }
      if (skippedOther > 0) {
        this.log('warn', `"${path.basename(dir)}" 下有 ${skippedOther} 个非普通文件被跳过`, 'DISK');
      }
    }
    // P1：跳过日志按轮汇总（否则 20,000 个文件 = 20,000 行日志 = UI 线程占满）
    const sk = this._skipStats;
    if (sk && ((sk.empty || 0) + (sk.oversize || 0)) > 0) {
      const total = (sk.empty || 0) + (sk.oversize || 0);
      const parts = [];
      if (sk.empty) parts.push(`0 字节 ${sk.empty} 个`);
      if (sk.oversize) parts.push(`超过上限 ${sk.oversize} 个`);
      this.log('info', `本轮跳过 ${total} 个文件：${parts.join('、')}（示例：${sk.names.join('、')}）`, 'UNKNOWN');
    }
    return out;
  }

  // P1：跳过计数（不写日志，避免刷屏）
  _noteSkip(kind, full) {
    const s = this._skipStats;
    if (!s) return;
    s[kind] = (s[kind] || 0) + 1;
    if (s.names.length < 3) s.names.push(path.basename(full));
  }

  // 禁止备份的系统路径（大小写不敏感）。用户误选会毁掉配额并上传系统文件。
  _blockedRoots() {
    const sys = process.env.SystemRoot || 'C:\\Windows';
    const pf = process.env.ProgramFiles || 'C:\\Program Files';
    const pf86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)';
    const pd = process.env.ProgramData || 'C:\\ProgramData';
    const win = process.env.WINDIR || 'C:\\Windows';
    return [
      sys,
      win,
      sys + '\\System32',
      pf,
      pf86,
      pd,
      'C:\\$Recycle.Bin',
      'C:\\Recovery',
      'C:\\System Volume Information',
      // 引导/恢复分区等
      'C:\\Boot',
    ].concat(this._driveRoots());
  }

  // 所有存在的盘符根（C:\、D:\ …）。盘符根必须拦：选了 C:\ 就会遍历整个系统盘。
  // README 承诺"防止误选导致上传整个系统盘"，但旧黑名单里没有盘符根，
  // 实测 watchDirs=['C:\\'] 直接进了 readdirSync('C:\\')。
  _driveRoots() {
    const out = [];
    for (let i = 0; i < 26; i++) {
      const letter = String.fromCharCode(65 + i) + ':\\';
      try { if (fs.existsSync(letter)) out.push(letter); } catch (e) { /* 无权限的盘符忽略 */ }
    }
    return out;
  }

  // 路径归一化：去掉尾部分隔符 + 转小写（Windows 文件系统大小写不敏感）
  _normPath(s) {
    return String(s || '').replace(/[\\/]+$/, '').toLowerCase();
  }

  // p 是否命中 blocked 列表中的某一项（**带分隔符边界**）。
  // 旧实现是裸 startsWith：C:\Program Files 会把 C:\ProgramFiles-backup 也判成系统目录
  // （实测误拒），而对 C:\ 这种盘符根又完全不命中（实测放行 ⇒ 遍历全盘）。
  _hitBlocked(p, blockedNorm) {
    const np = this._normPath(p);
    if (!np) return null;
    const list = blockedNorm || [];
    for (const nb of list) {
      if (!nb) continue;
      // 盘符根只做"完全相等"判定：盘符根带尾分隔符做前缀会把该盘所有路径都判成根
      // （实测：加了盘符根后 D:\ 上任何目录都被拒）。
      if (/^[a-z]:$/.test(nb)) { if (np === nb) return nb; continue; }
      if (np === nb || np.indexOf(nb + '\\') === 0 || np.indexOf(nb + '/') === 0) return nb;
    }
    return null;
  }

  // 绝不备份的目录（按**目录名**精确匹配，不做子串匹配，免得误伤普通目录）
  _excludedDirs() {
    return [
      'appdata', 'application data', 'local settings',
      'browser-profile', 'user data',
      '.ssh', '.gnupg', '.aws', '.azure', '.config',
      '$recycle.bin', 'system volume information',
      'node_modules',
    ];
  }

  // 绝不备份的文件（浏览器登录态/凭据文件）
  _excludedFiles() {
    return [
      'cookies', 'cookies-journal', 'login data', 'login data-journal',
      'web data', 'local state', 'key4.db', 'logins.json',
    ];
  }

  // ================= 上传 =================
  // opts 由 runOnce 传入（{ index, total, bytesDone, bytesTotal, etaMs }），仅用于进度事件；
  // 单独调用 uploadOne() 时可以不传。
  async uploadOne(item, opts) {
    const ctx = opts || {};
    const tStart = Date.now();
    const name = this._remoteNameFor(item);
    // bytesDone 是"本轮已完成的其他文件字节"，不含本文件；
    // 本文件只在真正传成功后才计入（否则失败重试会让进度条虚高，进而 ETA 失真）。
    const baseDone = ctx.bytesDone || 0;
    const bytesTotal = ctx.bytesTotal || item.st.size;
    const common = {
      index: ctx.index || 0,
      total: ctx.total || 0,
      name,
      size: item.st.size,
      bytesTotal,
      etaMs: ctx.etaMs === undefined ? null : ctx.etaMs,
    };
    const emitProgress = (phase, bytesDone, elapsedMs) => {
      this._emitProgress(Object.assign({}, common, {
        phase,
        bytesDone: bytesDone === undefined ? baseDone : bytesDone,
        elapsedMs: elapsedMs === undefined ? (Date.now() - tStart) : elapsedMs,
      }));
    };

    // 进入文件夹要 2.5s 起，先把"正在准备第 n 个"推给界面，别让进度条干等
    emitProgress('uploading', baseDone, 0);

    // 关键：每次上传前必须重新进入目标文件夹（不能复用上一轮的状态）。
    // 实测：第一个文件传成功后，网页版会刷新文件列表并重建 <input type=file>，
    // 旧元素句柄失效 ⇒ 后续 setInputFiles 写到了已脱离文档的元素上，
    // 事件不触发、POST 不发、文件传不上去（表现为"云端未找到该文件"）。
    // 重新进入文件夹会让页面重建到干净的初始状态，这个模式稳定可复现。
    //
    // ⚠ 不要把它"优化"成复用句柄：省下的 2.5 秒换来的是静默失败（云端没有文件，界面说成功）。
    const entered = await this.enterRemoteFolder(true);
    if (!entered) {
      this._noteErrCode('PAGE');
      this.log('error', `${name} 未上传: 无法进入目标文件夹`, 'PAGE');
      return { ok: false, error: '无法进入目标文件夹', code: 'PAGE' };
    }

    const page = await this.connect();
    this.log('info', `上传 ${name} (${item.st.size} B)...`);

    // 上传前拍快照：判定成功时只认"新出现的 fileId"，
    // 否则云端已有同名同大小旧文件会被误判成本次成功（静默丢数据）
    const beforeIds = await this._snapshotIds();
    // 快照列举成功 + 空集 ⇒ 上传前目录确实是空的（不用再探测，也不会白判"未核实"）
    const snapshotOk = this._lastSnapshotOk === true;

    const done = new Promise((resolve) => {
      let settled = false;
      let timer = null;
      const finish = (v) => {
        if (settled) return;
        settled = true;
        if (timer) { clearTimeout(timer); timer = null; }   // 必须清掉，否则定时器一直挂着
        page.off('response', onResp);
        resolve(v);
      };
      const onResp = async (resp) => {
        if (settled) return;
        if (!resp.url().startsWith(API)) return;
        if (resp.request().method() !== 'POST') return;
        if (!resp.url().includes('/drive/v1/files')) return;
        const status = resp.status();
        if (status === 200 || status >= 400) {
          let body = '';
          try { body = (await resp.text()).slice(0, 300); } catch (e) {
            // 拿不到响应体不影响判定（判定靠云端核查），但要知道是"没拿到"而不是"空"
            this.log('warn', `${name} 响应体读取失败: ${e.message}`, 'NETWORK');
          }
          // 注意：200 返回的是"上传票据"（STS 临时凭据），不是上传完成。
          // 这条响应只用来判断"服务端接受了这次上传请求"，成功与否必须靠云端核查。
          finish({ ok: status === 200, status, body });
        }
      };
      page.on('response', onResp);
      // 45s 只是"拿到票据"的合理上限；真正判定成功靠云端核查，不靠这个响应
      timer = setTimeout(() => {
        finish({ ok: false, timeout: true, body: '45s 未收到上传响应' });
      }, 45000);
    });

    try {
      await page.locator('input[type=file]').first().setInputFiles(item.full);
    } catch (e) {
      // setInputFiles 失败是页面层问题（句柄失效/元素不在），不是网络问题
      this._noteErrCode('PAGE');
      this.log('error', `${name} 触发上传失败: ${e.message}`, 'PAGE');
      return { ok: false, error: '触发上传失败: ' + e.message, code: 'PAGE' };
    }

    // 判定成功的方式改了（原先是等 page 上的 POST 响应，不可靠）：
    //   迅雷上传是三段式：POST 申请票据 → PUT 到阿里云 OSS → 服务端回调入库。
    //   PUT 阶段可能几十秒到几分钟，且真正入库发生在服务端回调之后，
    //   浏览器侧的 POST 响应并不代表"文件已经在云盘里"。
    //   实测：靠响应判定会把成功的大文件判成"120s 无响应"（文件其实已入库）。
    // 改为：等一个较短时间让 POST 拿到票据，然后用 API 去查文件是否真的在目标文件夹。
    const res = await done;

    if (!res.ok && res.timeout) {
      this.log('info', `${name} 上传较慢，改为核对云端是否已入库...`);
    }

    emitProgress('verifying', ctx.bytesDone || 0);

    // 无论响应是否拿到，都做一次事实核查（这才是"文件真的上去了"的证据）
    const verified = await this._verifyRemoteFile(name, item.st.size, beforeIds, snapshotOk);
    if (verified.ok === true) {
      return await this._onUploadVerified(item, name, beforeIds, ctx);
    }

    // 只有"确认失败"才值得再等一轮重查；"未核实"再查也是白等（快照本身不可用）
    let verdict = verified;
    if (verified.ok === false && res.ok) {
      // 浏览器说 200，但云端查不到：可能是刚入库、列表还没刷新。再等一轮。
      await new Promise(r => setTimeout(r, 5000));
      const again = await this._verifyRemoteFile(name, item.st.size, beforeIds, snapshotOk);
      if (again.ok === true) {
        return await this._onUploadVerified(item, name, beforeIds, ctx, true);
      }
      verdict = again;
    }

    // ---- 三态的第三种：未核实（P0 的灵魂）----
    // 文件很可能已经在云端了。此时既不能记成功（快照不可用时可能把"云盘里本来就有的
    // 同名同大小旧文件"误认成本次上传 —— 那会静默丢数据），也不能记失败（文件真在云端，
    // 记失败下一轮会重传，云端就多出 a(1).txt）。所以进 unverified 队列，
    // 下一轮开始前按"名字 + 大小"认领，认领成功直接转成 uploaded，不重传。
    if (verdict.ok === null) {
      this.state.unverified[item.full] = {
        fp: item.fp,
        name: name,
        size: item.st.size,
        at: new Date().toISOString(),
        reason: verdict.reason,
        // 上传前快照里的 id 列表 —— 这是"云端那份文件到底是不是本次传上去的"唯一证据。
        // 没有它，认领就只剩"名字 + 大小"，而云端本来就可能有一个同名同大小的旧文件
        // （用户改了内容但字节数没变，或另一个子目录里有同名同大小文件）。一旦认错，
        // scan() 会因为 fp 相同而永久跳过该文件 = 静默丢数据（红队 R1）。
        beforeIds: (beforeIds ? Array.from(beforeIds) : []),
      };
      delete this.state.failed[item.full];   // 绝不能同时挂在"失败"里，否则界面说失败、下轮又重传
      this._saveState();
      this.log('warn', `${name} 已上传但云端未核实（${verdict.reason}），下一轮按名字+大小认领，不会重传`, 'NETWORK');
      return { ok: null, unverified: true, error: '云端未核实（' + verdict.reason + '）', code: 'UNVERIFIED' };
    }

    // 失败分类：拿到 HTTP 状态就按状态分（401/403=AUTH, 429/5xx=限流/网络），
    // 拿到响应体且像配额就归 QUOTA，否则 UNKNOWN —— 不要在日志里断言"被限流"。
    let code = 'UNKNOWN';
    if (res.status) {
      code = this._isQuotaBody(res.body) ? 'QUOTA' : this._httpCode(res.status);
    } else if (res.timeout) {
      code = 'NETWORK';
    }
    this._noteErrCode(code);
    const err = res.status ? ('HTTP ' + res.status) : (res.timeout ? '未收到上传响应' : '云端未核实');
    this.log('error', `✗ ${name} 失败: 云端未找到该文件（${err}）`, code);

    const prev = this.state.failed[item.full] || { count: 0 };
    const count = prev.count + 1;
    this.state.failed[item.full] = {
      fp: item.fp,
      count,
      lastError: err,
      code,
      at: new Date().toISOString(),
      // 单文件退避：失败 5 次后不再每轮重试，改为每 10 轮才试一次。
      // nextRetryAt 用轮次（不是时间）更可靠：间隔会随退避变化，时间戳算不准。
      nextRetryAt: count >= 5 ? this._runCounter + 10 : null,
    };
    this._saveState();
    return { ok: false, error: err, code };
  }

  // 上传核实成功后的收尾：记状态 → 版本策略（keep 只报告 / replace 才删旧副本）
  async _onUploadVerified(item, name, beforeIds, ctx, delayed) {
    this.log('info', `✓ ${name} 上传成功（${delayed ? '延迟入库已确认' : '云端已核实，新增 fileId'}）`);
    this.state.uploaded[item.full] = {
      fp: item.fp,
      size: item.st.size,
      at: new Date().toISOString(),
    };
    delete this.state.failed[item.full];
    this._saveState();

    // 版本策略：这一步只影响"云端旧副本要不要清"，失败绝不影响本次上传成功
    try {
      await this._applyVersionPolicy(item, name, beforeIds);
    } catch (e) {
      const code = this._errCodeOf(e);
      this._noteErrCode(code);
      this.log('error', `处理云端旧版本时出错（本次上传本身已成功）: ${e.message}`, code);
    }

    return { ok: true };
  }

  // 决定文件在云端叫什么名字
  //
  // 重要前提（踩过一次坑）：上传走的是网页版的 <input type=file>，
  // 云端文件名由【浏览器读到的真实文件路径】决定，我们无法在上传时改名。
  // 也就是说：这个函数只影响"日志里显示什么"和"核查时按什么名字找"，
  // 不会改变云端实际名字。若拿改名后的名字去核查，就永远找不到 → 每个文件空等超时。
  //
  // 因此这里返回的是"用于识别"的名字，上传仍必须用真实路径。
  // 真正的目录结构问题（同名不同目录）改用 _resolveCollision() 处理：
  // 上传前若发现云端已有同名同大小但 id 不同的文件，先把它改名为 xxx(目录).ext，
  // 从而让本次上传得到原名 —— 但这需要额外的 rename API，暂不做，
  // 改为在日志与界面里明确提示"两个同名文件会变成 x.txt 与 x(1).txt"。
  _remoteNameFor(item) {
    return path.basename(item.full);
  }

  // 作废缓存的 header（服务端返回 401/403 或 captcha_invalid 时调用）
  _invalidateHeader(method) {
    try {
      if (this._hdrCache) {
        if (method) delete this._hdrCache[method];
        else this._hdrCache = {};
      }
      this.log('info', `已作废缓存的 ${method || '全部'} 请求头，下次会重新截取 captcha`);
    } catch (e) {
      this.log('warn', `作废 header 缓存失败: ${e.message}`, 'UNKNOWN');
    }
  }

  // 带缓存的 GET：遇到鉴权/captcha 失效时自动作废缓存并重试一次
  async _apiGet(url) {
    for (let attempt = 0; attempt < 2; attempt++) {
      let gh;
      try {
        gh = await this._capHeader('GET', 25000);
      } catch (e) {
        // 截头失败（最可能是页面僵死，_capHeader 已顺手 _dropPage 了）。
        // 第一次失败就重试：此时 connect() 会新建一个页面，通常立刻就正常。
        // 原实现让异常直接冒泡，一次页面僵死就让整轮同步全军覆没。
        if (attempt === 0) {
          this.log('info', '截取请求头失败，换页面重试一次', 'AUTH');
          continue;
        }
        throw e;
      }
      const r = await fetch(url, { headers: this._H(gh) });
      if (r.status === 200) return r;
      // captcha 或 token 失效：清缓存后重试一次
      if (r.status === 400 || r.status === 401 || r.status === 403) {
        this._invalidateHeader('GET');
        if (attempt === 0) continue;
      }
      return r;
    }
    return null;
  }

  // 云端事实核查：判定"这次上传是否真的入库了"
  //
  // 原实现只比 name+size（engine.js 旧版 L374），有两个致命漏洞：
  //   1. 云端已有同名同大小的旧文件 ⇒ 本次上传失败也判成功 ⇒ 永久不再重试（静默丢数据）
  //   2. 只查 limit=200 第一页 ⇒ 目录超 200 项后，新文件查不到 ⇒ 已传成功也判失败 ⇒ 无限重试
  //
  // 新方案：上传前先给目标目录拍一份 fileId 快照，上传后只认"新出现的 id"。
  //   - 同名旧文件 id 早已在快照里 ⇒ 不会误判
  //   - 分页取全量（最多 20 页 = 4000 项）⇒ 不会漏
  async _snapshotIds() {
    const ids = new Set();
    // listingOk：这次快照的"列举本身"是否成功（true 且 size===0 ⇒ 上传前目录确实是空的）。
    // 有了这个标志，_verifyRemoteFile 就不必再靠 _remoteFolderIsEmpty() 去猜
    // "空集是目录空还是快照失败"，于是"往空文件夹里传第一个文件"不会白判成未核实。
    let listingOk = true;
    // R4：每次拍快照先清掉上一次的结论。旧实现在 !remoteFolderId 时提前 return，
    // 把上一次的 _lastSnapshotOk 留了下来，于是"云端旧文件"会被判成本次上传成功。
    this._lastSnapshot = null;
    this._lastSnapshotOk = false;
    // 同时留下完整的文件对象数组：版本策略要按名字找旧副本，
    // 复用这份快照就不用为每个文件再拉一遍目录（少一半 API 调用，也少一次被限流的机会）。
    let files = null;
    let token = '';        // 提到 try 外面：循环之后要靠"token 是否耗尽"判断分页完整性
    try {
      if (!this.remoteFolderId) return ids;
      for (let page = 0; page < 100; page++) {
        let url = API + '/drive/v1/files?parent_id=' + encodeURIComponent(this.remoteFolderId) + '&limit=200';
        if (token) url += '&page_token=' + encodeURIComponent(token);
        const r = await this._apiGet(url);   // 带 header 缓存，不再每页重截 captcha
        if (!r || r.status !== 200) {
          listingOk = false;
          this.log('warn', `云端快照第 ${page + 1} 页未取到（HTTP ${r ? r.status : 'n/a'}），旧版本判定将偏保守`,
            r ? this._httpCode(r.status) : 'NETWORK');
          break;
        }
        const j = await r.json();
        const batch = j.files || [];
        files = (files || []).concat(batch);
        for (const f of batch) ids.add(f.id);
        token = j.next_page_token || '';
        if (!token) break;
      }
    } catch (e) {
      listingOk = false;
      this._noteErrCode(this._errCodeOf(e));
      this.log('warn', `云端快照失败: ${e.message}`, this._errCodeOf(e));
    }
    // 完整 = 每页都 200 且 page_token 耗尽（翻满 20 页还没完 = 不完整）。
    // 旧判据 files.length === ids.size 恒成立（两个集合由同一批数据生成），等于没判——
    // 于是"前两页成功、第三页 500"留下的半份快照会被当成完整快照复用（红队 R3）。
    const complete = listingOk && !token;
    this._lastSnapshot = complete ? files : null;
    this._lastSnapshotOk = complete;
    return ids;
  }

  // 拿"本次上传之前云端已有的文件列表"：优先用 _snapshotIds 顺手留下的那份
  async _beforeFiles() {
    if (this._lastSnapshot) return this._lastSnapshot;
    const out = [];
    try {
      if (!this.remoteFolderId) return out;
      let token = '';
      for (let page = 0; page < 100; page++) {
        let url = API + '/drive/v1/files?parent_id=' + encodeURIComponent(this.remoteFolderId) + '&limit=200';
        if (token) url += '&page_token=' + encodeURIComponent(token);
        const r = await this._apiGet(url);
        if (!r || r.status !== 200) break;
        const j = await r.json();
        for (const f of (j.files || [])) out.push(f);
        token = j.next_page_token || '';
        if (!token) break;
      }
    } catch (e) {
      this.log('warn', `列举云端文件失败: ${e.message}`, this._errCodeOf(e));
    }
    return out;
  }

  // 等待 targetName 对应的、且 id 不在 before 集合里的文件出现
  // 同时校验大小（防止把同名的其他新文件误认成本次的）
  // 认领上一轮"已上传但云端未核实"的文件（P0 的另一半）：
  //   判据只有一条 —— 云端现在有一个**同名且同大小**的文件。
  //   不看 id：上传前的 id 快照本来就不可用，这正是当初判"未核实"的原因。
  //   安全性：只有"上一轮确实把文件交给浏览器上传过"才会进 unverified 队列，
  //   所以按名字+大小认领不会把"用户自己早就传过的同名同大小旧文件"算成本次成功
  //   （那种情况本来就已经在 uploaded 里，不会出现在 unverified）。
  //   认领成功 ⇒ 写进 uploaded ⇒ 下一轮 scan() 按 fp 命中后直接跳过，不会重传。
  async _claimUnverified() {
    const pend = this.state.unverified || {};
    const keys = Object.keys(pend);
    if (!keys.length) return 0;
    if (!this.remoteFolderId) return 0;
    let files = [];
    try { files = await this._beforeFiles(); } catch (e) { files = []; }
    if (!files.length) {
      this.log('info', `${keys.length} 个文件待认领，但本轮列举云端失败，下一轮再试`, 'NETWORK');
      return 0;
    }
    let claimed = 0;
    for (const full of keys) {
      const u = pend[full];
      if (!u) continue;
      // 认领的唯一合法证据：云端那个文件的 id **不在上传前的快照里**（即本次新出现的）。
      // 拿不到证据就不认领：宁可让它多悬一轮，也不能把云端旧文件记成本次上传
      // —— 那会让 scan() 永久跳过它，用户的修改永远到不了云端（红队 R1）。
      const before = Array.isArray(u.beforeIds) ? u.beforeIds : null;
      if (!before || before.length === 0) {
        this.log('info', `${u.name} 仍待认领（缺少上传前快照证据，无法证明云端那份是本次上传），下一轮再试`);
        continue;
      }
      const hit = files.find(f => f && f.name === u.name
        && Number(f.size) === Number(u.size)
        && before.indexOf(f.id) < 0);
      if (!hit) {
        this.log('info', `${u.name} 仍待认领（云端只有上传前就存在的旧副本，或暂未见到同名同大小文件），下一轮再试`);
        continue;
      }
      this.state.uploaded[full] = { fp: u.fp, size: u.size, at: new Date().toISOString() };
      delete this.state.unverified[full];
      claimed++;
      this.log('info', `${u.name} 已按名字+大小认领（云端已有 ${Number(u.size)} 字节），不会重传`);
    }
    this._saveState();
    return claimed;
  }

  // 三态核查（P0 修复的核心）：
  //   { ok: true,  reason } = 确认本次新出现（新增 id，或目录本来空）
  //   { ok: false, reason } = 确认失败（云端明确没有，且列举本身成功）
  //   { ok: null,  reason } = **无法核实**（快照不可用/列举全失败）—— 既不是成功也不是失败
  //
  // 原来只有布尔：快照不可用时返回 false，于是"文件已经传上去了"被判失败，
  // 下一轮重传 → 云端出现 a.txt + a(1).txt；连续 5 轮还会熔断。
  // 三态的意义：把"不知道"从"失败"里分出来，交给 unverified 队列下一轮按名字+大小认领，
  // 既不丢数据（绝不把"不知道"当成功），也不重复上传（也不把它当失败）。
  async _verifyRemoteFile(name, size, beforeIds, snapshotOk) {
    // 快照无效时**不能**判成功。
    //
    // 这是"静默丢数据"的最后一个入口（红队实测出的）：
    // _snapshotIds 在分页失败/网络抖动时返回空 Set（不是抛异常），
    // 而 `beforeIds && beforeIds.has(...)` 对空 Set 恒为 false ⇒
    // 云端任何一个同名同大小的旧文件都会被当成本次上传成功 ⇒
    // 写进 state.uploaded ⇒ 该文件此后再也不重传。
    // 用户改了内容但字节数没变（等长文本、定长记录、同大小替换）就会永久漏备。
    //
    // 删除侧早就有 `beforeIds.size === 0` 防护（_deleteOldVersions），
    // 核查侧却没有 —— 同一个空集，一个方向安全、另一个方向丢数据。现在对齐。
    let claimedEmpty = false;
    if ((!beforeIds || beforeIds.size === 0) && snapshotOk === true) {
      // 列举成功且返回 0 个文件 ⇒ 上传前目录确实是空的，
      // 任何同名同大小文件都只可能是刚传上去的（无需再探测，也避免把"第一个文件"白判成未核实）
      claimedEmpty = true;
    } else if (!beforeIds || beforeIds.size === 0) {
      // 目录确实是空的时候，快照也会是空集。这时无法区分"目录空"和"快照失败"，
      // 所以额外查一次，并且区分三种答案：
      const emptiness = await this._remoteFolderIsEmpty();
      if (emptiness === null) {
        // 连"目录为空吗"都问不出来：快照不可用 + 目录状态未知 ⇒ 未核实
        return { ok: null, reason: 'snapshot-unavailable' };
      }
      if (emptiness === false) {
        // 目录非空，但没有上传前的 id 集合 ⇒ 无法区分"新传的"和"本来就有的同名文件"。
        // 这正是原来防静默丢数据的理由，必须保留：绝不能在这里判成功。
        return { ok: null, reason: 'snapshot-unavailable' };
      }
      claimedEmpty = true;   // 目录确实为空：同名同大小文件都只可能是刚传上去的
      // 目录确实为空：此时任何同名同大小文件都只可能是刚传上去的
    }

    let listedOk = false;      // 是否至少成功列举过一页（决定"没找到"是"确认没有"还是"查不到"）
    let lastStatus = null;
    for (let attempt = 0; attempt < 6; attempt++) {
      if (attempt > 0) await new Promise(r => setTimeout(r, 3000));
      try {
        if (!this.remoteFolderId) return { ok: false, reason: 'no-folder' };

        let token = '';
        let sawPages = 0;
        // 补 3（CODE-NITS A2）：只有**翻到最后一页**才算"确认没有"。
        // 旧实现"第一页成功就 listedOk=true"，中途某页 500 时仍会返回 ok:false，
        // 于是目录里 >200 个文件时，已经传上去的文件会被判失败 ⇒ 重传 ⇒ a(1).txt（与 R1 同源的 P0）。
        let exhaustive = true;
        for (let page = 0; page < 100; page++) {
          let url = API + '/drive/v1/files?parent_id=' + encodeURIComponent(this.remoteFolderId) + '&limit=200';
          if (token) url += '&page_token=' + encodeURIComponent(token);
          const r = await this._apiGet(url);   // 带 header 缓存
          if (!r || r.status !== 200) { lastStatus = r ? r.status : null; exhaustive = false; break; }
          const j = await r.json();
          const files = j.files || [];
          sawPages++;

          for (const f of files) {
            if (Number(f.size) !== Number(size)) continue;
            // 名称可能是原名，也可能被云盘改名成 "x(1).ext"（同名已存在时）
            const isSameName = f.name === name;
            const isRenamed = this._looksLikeRenamedCopy(f.name, name);
            if (!isSameName && !isRenamed) continue;

            // 核心判据：id 必须是本次新出现的
            if (beforeIds && beforeIds.size > 0 && beforeIds.has(f.id)) {
              // 这是上传前就存在的旧文件，不能算本次成功
              continue;
            }
            return { ok: true, reason: claimedEmpty ? 'folder-was-empty' : 'new-id' };
          }

          token = j.next_page_token || '';
          if (!token) break;
          if (page === 99) exhaustive = false;   // 翻满 20 页还有下一页 = 没取全
        }
        if (!exhaustive) {
          // P3：目录超过 100 页 × 200 = 20,000 项时列举不完整 ⇒ 不能判成功也不能判失败
          this.log('warn', '云端目录项数超过 20,000，无法完整核查（该目录太大了）', 'PAGE');
        }
        if (exhaustive) listedOk = true;         // 只有完整翻完，后面的"没找到"才算"确认没有"
      } catch (e) {
        if (attempt === 5) {
          const code = this._errCodeOf(e);
          this._noteErrCode(code);
          this.log('warn', `云端核查失败: ${e.message}`, code);
        }
      }
    }
    // 列举成功过但 6 轮都没看到它 ⇒ 确认失败；一次都没列举成功 ⇒ 只是"没查到"，不能判失败
    if (listedOk) return { ok: false, reason: 'not-found' };
    return { ok: null, reason: 'list-failed' + (lastStatus ? ('（HTTP ' + lastStatus + '）') : '') };
  }

  // 云端目标目录是否确实为空。
  // 用途：区分"目录真的是空的（快照空集是正常的）"与"快照查询失败（空集是假的）"。
  // 只有前者才允许在没有 beforeIds 的情况下判定上传成功。
  // 返回 true / false / null：
  //   true  = 确认目录为空
  //   false = 确认目录非空
  //   null  = **查不到**（没文件夹 / 请求失败 / 解析失败）—— 不能当成"非空"，也不能当成"空"
  // 原来把"查不到"也返回 false，于是调用方无法区分"目录非空"和"查询失败"，
  // 只能一律判失败 —— 那正是 P0（上传成功却判失败、随后重复上传）的成因。
  async _remoteFolderIsEmpty() {
    try {
      if (!this.remoteFolderId) return false;
      const url = API + '/drive/v1/files?parent_id='
        + encodeURIComponent(this.remoteFolderId) + '&limit=1';
      const r = await this._apiGet(url);
      if (!r || r.status !== 200) return null;
      const j = await r.json();
      const files = j.files || [];
      return files.length === 0;
    } catch (e) {
      return null;
    }
  }

  // "report.txt" 被云盘重命名后是 "report(1).txt" / "report(2).txt"
  _looksLikeRenamedCopy(candidate, original) {
    if (!candidate || !original) return false;
    const ext = path.extname(original);
    const stem = ext ? original.slice(0, -ext.length) : original;
    const suffix = ext || '';
    if (stem.length === 0) return false;
    const re = new RegExp('^' + stem.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\(\\d+\\)' + suffix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$');
    return re.test(candidate);
  }

  // "report(2).txt" 的 stem 仍然是 "report"，与 "report.txt" 同源。
  // 取"去掉尾部 (n)"后的 stem，用于把同名旧副本分组。
  _stemOf(name) {
    if (!name) return '';
    const ext = path.extname(name);
    let stem = ext ? name.slice(0, -ext.length) : name;
    stem = stem.replace(/\(\d+\)$/, '');
    return stem;
  }

  // 本地监视列表里有哪些文件与 name 共享同一个 stem（用于阻止"删掉别人唯一副本"）。
  // 只看本地：云端名字我们无法反推来源，本地文件却是确定的。
  //
  // 性能：整棵树递归一次可能上万文件，而 replace 模式下每个成功文件都要问一次。
  // 因此按轮缓存（_stemIndex / _stemIndexRun），一轮内只走一次磁盘。
  _stemOwners(name) {
    const stem = this._stemOf(name);
    if (!stem) return [];
    if (this._stemIndexRun !== this._runCounter || !this._stemIndex) {
      const idx = {};
      let walkFailed = false;
      for (const dir of (this.cfg.watchDirs || [])) {
        const stack = [[dir, 0]];
        while (stack.length) {
          const [d, depth] = stack.pop();
          if (depth > 64) continue;
          let ents;
          try { ents = fs.readdirSync(d, { withFileTypes: true }); }
          catch (e) { walkFailed = true; continue; }
          for (const ent of ents) {
            const full = path.join(d, ent.name);
            if (ent.isDirectory()) {
              if (ent.name.startsWith('.') || ent.name === 'node_modules') continue;
              stack.push([full, depth + 1]);
            } else if (ent.isFile()) {
              const s = this._stemOf(ent.name);
              (idx[s] = idx[s] || []).push(full);
            }
          }
        }
      }
      this._stemIndex = idx;
      this._stemIndexRun = this._runCounter;
      this._stemIndexFailed = walkFailed;
      if (walkFailed) {
        this.log('warn', '统计本地同名文件时有目录读不到（本轮为保证安全，不删云端旧版本）', 'DISK');
      }
    }
    if (this._stemIndexFailed) return [name, name];   // 哨兵：让调用方跳过删除
    return this._stemIndex[stem] || [];
  }

  // ================= 进度事件 =================
  // 字段名是界面契约（WP2 的进度条直接读这些键），改名即破坏界面：
  //   phase(scanning|uploading|verifying|done) index total name size
  //   bytesDone bytesTotal elapsedMs etaMs
  // etaMs 在不该有值时一律给 null，不要给 0（0 会显示成"马上完成"，是撒谎）。
  _emitProgress(p) {
    try {
      // 字段白名单：只透传约定好的字段，避免把内部状态泄漏到 UI 层。
      // 注意 uploaded/failed 必须放行 —— 它们只在 done 事件里有意义，
      // 界面靠它们决定说"完成"还是"有失败"。曾漏过这两个字段，
      // 导致界面上刚写好的「完成 / 部分成功 / 本轮失败」三分支全成了死代码。
      var out = {
        phase: p.phase || 'uploading',
        index: Number(p.index) || 0,
        total: Number(p.total) || 0,
        name: p.name || '',
        size: Number(p.size) || 0,
        bytesDone: Number(p.bytesDone) || 0,
        bytesTotal: Number(p.bytesTotal) || 0,
        elapsedMs: Number(p.elapsedMs) || 0,
        etaMs: (p.etaMs === null || p.etaMs === undefined) ? null : Number(p.etaMs),
      };
      // 只有确实带了才透传，免得旧界面读到 0 会误判"全部失败"
      if (p.uploaded !== undefined && p.uploaded !== null) out.uploaded = Number(p.uploaded);
      if (p.failed !== undefined && p.failed !== null) out.failed = Number(p.failed);
      // 未核实（必修 11b）：既不是成功也不是失败，但界面必须看得到。
      // 不白名单放行的话，"整轮全是 unverified"会被界面说成"没有需要上传的新文件"（红队 R2）。
      if (p.unverified !== undefined && p.unverified !== null) out.unverified = Number(p.unverified) || 0;
      // 必修 16：缺目录数也要透传，否则界面无法区分"目录没了"和"真的没新文件"
      if (p.missingDirs !== undefined && p.missingDirs !== null) out.missingDirs = Number(p.missingDirs) || 0;
      // 致命错误码同样必须放行：没有它界面无法区分
      // "本轮整轮失败（连不上浏览器）" 和 "本轮真的没有文件要传"——
      // 这两种情况 uploaded/failed 都是 0，界面会把前者说成"完成"（实测确认过）。
      if (p.fatalCode) out.fatalCode = String(p.fatalCode);
      if (p.errorCodes && p.errorCodes.length) out.errorCodes = p.errorCodes.slice(0, 8);
      this.emit('progress', out);
    } catch (e) {
      // 事件监听器抛错不能拖垮同步主流程（但也不能静默）
      this.log('warn', `进度事件派发失败: ${e.message}`, 'UNKNOWN');
    }
  }

  // 根据已传字节与耗时估算剩余时间；样本太小就返回 null（不猜）
  _etaFor(bytesDone, bytesTotal, elapsedMs) {
    if (!(bytesTotal > 0) || !(bytesDone > 0) || !(elapsedMs > 0)) return null;
    if (bytesDone >= bytesTotal) return 0;
    const speed = bytesDone / elapsedMs;            // bytes/ms
    if (!(speed > 0)) return null;
    return Math.round((bytesTotal - bytesDone) / speed);
  }

  // ================= 云端版本策略 =================
  // 危险操作集中在这里。默认 keep 下本函数只读不写，replace 才有删除路径。
  //
  // 删除的必要条件（全部满足才删，任何一条不满足就跳过并记日志）：
  //   ① versionsMode === 'replace'（用户显式开启，默认不删）
  //   ② 新文件已通过 _verifyRemoteFile 核实成功（调用点保证：只有核实成功才进来）
  //   ③ 该 id 在 before 快照里（确属本次之前就存在的旧副本，不是刚传的新文件）
  //   ④ id 不是本次上传产生的新文件 id（beforeIds 已排除，再做一次显式防御）
  //   ⑤ 文件名 stem 与本次文件相同（同源版本，不碰无关文件）
  //   ⑥ 本轮错误里没有 QUOTA / AUTH（配额或鉴权出问题时不删任何东西）
  //   ⑦ 本轮删除数 < maxDeletePerRun（默认 20）
  async _applyVersionPolicy(item, name, beforeIds) {
    if (this.cfg.versionsMode !== 'replace') {
      // keep 模式：只报告，不动云端任何文件
      const olds = await this._listOldVersions(name, beforeIds);
      if (olds.length > 0) {
        this.log('info', `云端已有 ${olds.length} 个同名旧版本（模式 keep，保留不删）: `
          + olds.map(o => `${o.name}${o.id ? '#' + o.id : ''}`).join(', '));
        this._recordVersions(item.full, name, olds, 'kept');
      } else {
        this._recordVersions(item.full, name, [], 'kept');
      }
      return;
    }
    await this._deleteOldVersions(item, name, beforeIds);
  }

  // 记录版本审计信息到 state.versions（供界面展示与事后追溯）
  _recordVersions(localPath, name, olds, action) {
    try {
      const key = localPath;
      const prev = Array.isArray(this.state.versions[key]) ? this.state.versions[key] : [];
      const now = new Date().toISOString();
      const add = olds.map(o => ({
        id: o.id || null,
        name: o.name || '',
        size: Number(o.size) || 0,
        uploadedAt: o.modified_time || o.created_time || null,
        action,
        recordedAt: now,
      }));
      // 只保留最近 50 条，避免 state.json 无限膨胀
      this.state.versions[key] = prev.concat(add).slice(-50);
      this._saveState();
    } catch (e) {
      this.log('warn', `写入版本记录失败: ${e.message}`, 'DISK');
    }
  }

  // 列出云端与 name 同源的旧副本（id 在 beforeIds 里的才算"旧"）
  // 优先用上传前快照 _snapshotIds 留下的文件列表：那份数据"确实早于本次上传"，
  // 语义上比重新列举更强（重新列举可能把刚上传成功的文件也包含进来）。
  async _listOldVersions(name, beforeIds) {
    const out = [];
    const stem = this._stemOf(name);
    if (!stem) return out;
    const files = await this._beforeFiles();
    for (const f of files) {
      if (f.kind && f.kind !== 'drive#file') continue;
      if (this._stemOf(f.name) !== stem) continue;
      // 只认"上传前就已存在"的 id。快照为空时这里一个都不会命中 —— 保守方向正确。
      if (!beforeIds || !beforeIds.has(f.id)) continue;
      out.push(f);
    }
    return out;
  }

  // 删除同一个 DELETE /drive/v1/files/<id>
  async _deleteRemoteFile(id) {
    const gh = await this._capHeader('GET');
    const r = await fetch(API + '/drive/v1/files/' + encodeURIComponent(id), {
      method: 'DELETE',
      headers: this._H(gh),
    });
    let body = '';
    try { body = (await r.text()).slice(0, 200); } catch (e) {
      this.log('warn', `删除 ${id} 的响应体读取失败: ${e.message}`, 'NETWORK');
    }
    return { status: r.status, ok: r.status < 300, body };
  }

  async _deleteOldVersions(item, name, beforeIds) {
    // ⑤ 无关：stem 已经限制了范围。这里再做一次 beforeIds 存在性防御 —— 快照失败时是空集合，
    // 空集合意味着"什么都不该删"，而不是"什么都可以删"。
    if (!beforeIds || beforeIds.size === 0) {
      this.log('warn', `跳过删除云端旧版本: 上传前的 id 快照为空（无法判断哪些是旧副本）`, 'UNKNOWN');
      return;
    }
    // ⑥ 本轮出现配额/鉴权错误时，绝不删任何云端文件
    if (this._runErrCodes && (this._runErrCodes.has('QUOTA') || this._runErrCodes.has('AUTH'))) {
      this.log('warn', `跳过删除云端旧版本: 本轮出现 ${[...this._runErrCodes].join('/')} 错误（配额或登录异常时不动云端文件）`, 'QUOTA');
      return;
    }
    // ⑧ 最要紧的一条：如果本地有多个文件共享同一个 stem，云端那个 "x(1).txt"
    //    可能根本不是"本文件的旧版本"，而是另一个本地文件的唯一备份。
    //    云盘是单层目录，这恰恰是本项目最典型的用法（多个子目录里的同名文件）。
    //    这种情况下删任何一个都可能删掉别人唯一的副本 ⇒ 一律不删，只报告。
    const owners = this._stemOwners(name);
    if (owners.length > 1) {
      this.log('warn', `跳过删除云端旧版本: 本地有 ${owners.length} 个文件同名（stem 相同），`
        + `云端带 (n) 的副本可能属于另一个文件，删除会丢数据。涉及: `
        + owners.slice(0, 5).map(o => path.basename(o)).join(' / ')
        + `${owners.length > 5 ? ' …' : ''}。建议给不同目录使用不同的远程文件夹名。`, 'UNKNOWN');
      return;
    }

    const olds = await this._listOldVersions(name, beforeIds);
    if (olds.length === 0) return;

    this.log('info', `模式 replace: 云端有 ${olds.length} 个旧版本待清理（${name}）`);
    const deleted = [];
    const recorded = [];
    for (const f of olds) {
      // ⑦ 单轮删除上限：防止一次配置错误把整个目录清空
      if (this._deletedThisRun >= this.cfg.maxDeletePerRun) {
        this.log('warn', `已达到单轮删除上限 ${this.cfg.maxDeletePerRun} 个，剩余旧版本留到下一轮处理`, 'QUOTA');
        break;
      }
      // ④ 二次核对：必须确属 before 集合，且不能是本次上传的新 id
      if (!beforeIds.has(f.id)) {
        this.log('warn', `跳过 ${f.name}(${f.id}): 不在上传前的快照里`, 'UNKNOWN');
        continue;
      }
      if (this.state.uploaded[item.full] && this.state.uploaded[item.full].remoteId === f.id) {
        this.log('warn', `跳过 ${f.name}(${f.id}): 这是本次上传的记录 id`, 'UNKNOWN');
        continue;
      }

      const r = await this._deleteRemoteFile(f.id);
      if (r.ok) {
        this._deletedThisRun++;
        deleted.push(f);
        recorded.push({ ...f, action: 'deleted' });
        this.log('info', `已删除云端旧版本 ${f.name} (${f.id}, ${f.size || 0} B)`);
      } else {
        const code = this._isQuotaBody(r.body) ? 'QUOTA' : this._httpCode(r.status);
        this._noteErrCode(code);
        recorded.push({ ...f, action: 'delete-failed' });
        this.log('warn', `删除云端旧版本 ${f.name} (${f.id}) 失败 ${r.status}: ${r.body}`, code);
        // 第一个删除就撞上配额/鉴权 → 立即停止，别继续撞（可能触发风控）
        if (code === 'QUOTA' || code === 'AUTH') {
          this.log('warn', '删除旧版本遇到配额/鉴权错误，本轮停止删除', code);
          break;
        }
      }
    }
    if (recorded.length > 0) this._recordVersions(item.full, name, recorded, 'replace');
  }

  // ================= 一轮 =================
  // opts.silentBackoff：手动触发（界面上"立即同步"）时置位 —— 手动那一轮失败不改变
  // 自动重试的退避计数，也不因为熔断而拒绝执行。
  async runOnce(opts) {
    if (this.currentRun) throw this._err('已有同步进行中', 'UNKNOWN');
    const manual = !!(opts && opts.manual);
    const runId = new Date().toISOString().replace(/[:.]/g, '-');
    this.currentRun = runId;
    this._runCounter++;
    this._deletedThisRun = 0;
    this._runErrCodes = new Set();
    const t0 = Date.now();
    const runNo = this._runCounter;
    // unverified：本轮"上传过但云端暂时核实不了"的文件数（不是失败，也不是成功）
    const stats = { runId, runNo, scanned: 0, uploaded: 0, failed: 0, skipped: 0, unverified: 0, errors: [], errorCodes: [] };
    let fatalCode = null;
    try {
      this._emitProgress({
        phase: 'scanning', index: 0, total: 0, name: '', size: 0,
        bytesDone: 0, bytesTotal: 0, elapsedMs: 0, etaMs: null,
      });

      await this.connect();
      if (!this.remoteFolderId) await this.ensureRemoteFolder();
      if (!this.remoteFolderId) throw this._err('没有可用的远程文件夹', 'PAGE');

      // 上轮"未核实"的文件先尝试认领：认领成功就不重传、也不计失败。
      // 必须放在 scan() 之前 —— scan() 会按 state.uploaded 的 fp 过滤，认领晚一步就会重传。
      stats.unverifiedClaimed = await this._claimUnverified();
      if (stats.unverifiedClaimed) {
        this.log('info', `已认领 ${stats.unverifiedClaimed} 个上轮未核实的文件（未重传）`);
      }

      const items = this.scan();
      stats.scanned = items.length;
      const ready = items.filter(i => !i.skip);
      stats.skipped = items.length - ready.length;

      // 单文件退避：失败 5 次以上的文件不再每轮重试，改为每 10 轮才试一次。
      // 记录的是 nextRetryAt（轮次号），因为轮间隔会随退避变化，用时间戳算不准。
      //
      // manual（用户点"立即同步"）必须绕过这条过滤：退避是给自动轮省资源用的，
      // 而手动按钮的语义是"用我现在的凭据立刻再试一次"。原实现没看 manual，
      // 于是失败 5 次的文件手动也 attempted=0，界面显示"完成·成功 0 个文件"，
      // 用户以为修好了，实际一个字节都没传（实测确认过的说谎）。
      const due = [];
      let deferred = 0;
      for (const it of ready) {
        const f = this.state.failed[it.full];
        if (!manual && f && f.count >= 5 && typeof f.nextRetryAt === 'number' && runNo < f.nextRetryAt) {
          deferred++;
          continue;
        }
        due.push(it);
      }
      stats.deferred = deferred;
      if (deferred > 0) {
        this.log('info', `${deferred} 个文件已连续失败 5 次以上，本轮跳过（每 10 轮才重试一次，可在"失败文件"里看到）`, 'UNKNOWN');
      }
      this.log('info', `[${runId}] 扫描 ${items.length} 项，待上传 ${due.length} 项`);

      // 同名冲突检测：云盘是单层目录，子目录里的同名文件会互相顶掉
      // （第二个会变成 xxx(1).ext）。这不是能静默忽略的事：
      // 用户以为备份了 a\report.txt 和 b\report.txt，云端只有 report.txt + report(1).txt，
      // 且无法从名字判断谁是谁。这里明确告警，并给出可操作建议。
      const byName = {};
      for (const it of due) {
        const n = path.basename(it.full);
        (byName[n] = byName[n] || []).push(it);
      }
      for (const n of Object.keys(byName)) {
        const group = byName[n];
        if (group.length > 1) {
          const where = group.map(g => g.rel || path.basename(g.full)).join(' | ');
          this.log('warn', `发现 ${group.length} 个同名文件 "${n}"（${where}）—— 云盘是单层目录，它们会变成 "${n}" 和带 (1) 后缀的副本，无法区分来源。建议把不同目录分别加入监视列表并使用不同的远程文件夹名。`, 'UNKNOWN');
          stats.nameCollisions = (stats.nameCollisions || 0) + 1;
        }
      }

      // 轮级进度基数：已完成的文件字节 + 已尝试但失败的文件字节也要计入，
      // 否则"剩余时间"会随着失败文件反复重试而变得不可信。
      let bytesDone = 0;
      const bytesTotal = due.reduce((s, i) => s + (i.st.size || 0), 0);
      const total = due.length;
      stats.bytesTotal = bytesTotal;
      stats.bytesUploaded = 0;

      if (due.length === 0) {
        // 必修 16：目录整个不见了不能说「无需同步」（用户会以为一切正常）
        const missN = (this._missingDirs || []).length;
        if (missN > 0) {
          this.log('error', `没有可备份的文件：${missN} 个监视文件夹找不到了（可能被改名或删除）。请在设置里重新添加。`, 'MISSING_DIR');
        } else {
          this.log('info', '无需同步');
        }
      } else {
        // 不再做这里的一次预检：uploadOne 内部每个文件都会 enterRemoteFolder(true)，
        // 预检等于把导航（最易失败的一步）多跑一遍，实测多耗 18 秒且失败面翻倍。
        let idx = 0;
        for (const it of due) {
          // "停止"必须真的停下来：原来 stop() 只 clearTimeout，
          // 当前文件仍会传完、后续循环继续跑，界面却已显示"已停止"（说谎）。
          if (this._abortRequested) {
            this.log('info', `已按用户请求停止，本轮剩余 ${due.length - idx} 个文件不再上传`);
            stats.aborted = true;
            break;
          }

          idx++;
          const nm = path.basename(it.full);
          const etaMs = this._etaFor(bytesDone, bytesTotal, Date.now() - t0);
          const ctx = {
            index: idx, total, bytesDone, bytesTotal, etaMs,
          };

          // 单文件最多试 2 次（网络抖动/页面慢时一次失败不代表真失败）。
          // 原来函数内零重试，失败只能等下一轮（60s 后），用户体验是"卡住不动"。
          let r = await this.uploadOne(it, ctx);
          // 只对"确认失败"重试。unverified（ok===null）绝不能重试 —— 那正是重复上传的来源。
          if (r.ok === false) {
            this.log('warn', `${nm} 第 1 次失败（${r.error}${r.code ? ' / ' + r.code : ''}），2 秒后重试`, r.code);
            await new Promise(r2 => setTimeout(r2, 2000));
            r = await this.uploadOne(it, ctx);
          }

          if (r.ok === true) stats.uploaded++;
          else if (r.unverified) {
            // 未核实 != 失败：单独计数，不计入 failed（否则会触发退避/熔断的误判）
            stats.unverified = (stats.unverified || 0) + 1;
            this.log('info', `${nm} 本轮未核实（云端暂时查不到快照），已记入待认领队列`);
          } else {
            stats.failed++;
            stats.errors.push({ file: it.full, error: r.error, code: r.code || 'UNKNOWN' });
            if (r.code) this._noteErrCode(r.code);
          }
          bytesDone += (it.st.size || 0);
          if (r.ok) stats.bytesUploaded += (it.st.size || 0);

          // 每传完一个就推一次，界面进度条不会停在"第 n/total"不动
          this._emitProgress({
            phase: 'uploading',
            index: idx,
            total,
            name: nm,
            size: it.st.size,
            bytesDone,
            bytesTotal,
            elapsedMs: Date.now() - t0,
            etaMs: this._etaFor(bytesDone, bytesTotal, Date.now() - t0),
          });

          // 文件之间留间隔：网页版一次只能处理一个上传，连续触发会被服务端限流
          if (idx < due.length) {
            await new Promise(r2 => setTimeout(r2, 1500));
          }
        }
      }
    } catch (e) {
      fatalCode = this._errCodeOf(e);
      this._noteErrCode(fatalCode);
      this.log('error', `[${runId}] 失败: ${e.message}`, fatalCode);
      stats.errors.push({ error: e.message, code: fatalCode });
    } finally {
      stats.durationMs = Date.now() - t0;
      stats.errorCodes = this._runErrCodes ? [...this._runErrCodes] : [];
      // 致命错误码必须进 stats：runNow() 只能看到 stats，看不到本函数作用域里的
      // fatalCode。少了这个字段，致命轮（连不上浏览器：failed=0/uploaded=0）
      // 会被 runNow 的旧判据当成"成功"，于是解熔断 + 记一条"手动同步成功"。
      stats.fatalCode = fatalCode || null;
      this.currentRun = null;
      this.state.lastRun = stats;
      this.lastRunAt = Date.now();
      this._logDay = null;          // 允许下一次 log() 重新判定日期（跨天）
      this._saveState();
      this._flushState();           // P2：一轮结束必须真落盘（debounce 不能拖过这一轮）

      // ---- 退避 / 熔断判定 ----
      // "失败的一轮" = 有致命错误（连不上浏览器/没有远程文件夹），或本轮有文件失败。
      // 只是"没有文件要传"的成功轮不参与退避，否则用户加文件后要等 30 分钟。
      // 整轮全是 unverified（failed=0, uploaded=0, unverified=N）时，本轮的真相是
      // "传了但一个都没核实到"：既不能算成功（否则绿条 + 熔断被静默清掉），也不能算失败。
      // 这里按"未完成"处理：参与退避，不清熔断。
      // 必修 16：整个被监视目录不见了属于整轮级故障（否则会走"无需同步"绿色分支）
      stats.missingDirs = (this._missingDirs || []).length;
      if (stats.missingDirs > 0 && stats.scanned === 0 && stats.uploaded === 0 && !fatalCode) {
        // stats.fatalCode 在上面已经赋过一次值，这里必须同时写回两份，
        // 否则 done 事件与 runNow 看到的还是 null ⇒ 界面照样绿（实测踩到）。
        fatalCode = 'MISSING_DIR';
        stats.fatalCode = 'MISSING_DIR';
      }
      const roundFailed = !!fatalCode || stats.failed > 0
        || ((stats.unverified || 0) > 0 && stats.uploaded === 0)
        || stats.missingDirs > 0;
      this._logDay = null;
      this._applyBackoff(roundFailed, stats, manual);
      stats.backoffMs = this.nextRunAt ? Math.max(0, this.nextRunAt - Date.now()) : null;
      stats.consecutiveFailures = this.consecutiveFailures;
      stats.circuitOpen = this.circuitOpen;

      this._emitProgress({
        phase: 'done', index: stats.uploaded,
        total: stats.uploaded + stats.failed + (stats.unverified || 0),
        name: '', size: 0,
        // 收尾事件也要给出本轮的字节基数，否则界面进度条停在最后一条 uploading
        // 的百分比（或显示 0/0），看起来像"卡住没完成"。
        //
        // 但 bytesDone 只能用**成功**的字节数（stats.bytesUploaded），
        // 不能用 bytesTotal —— 后者含失败文件的字节，会让进度条走到 100%
        // 而实际有文件没传上去，配合界面上的"完成"二字就是说谎。
        bytesDone: stats.bytesUploaded || 0,
        bytesTotal: stats.bytesTotal || 0,
        elapsedMs: stats.durationMs, etaMs: 0,
        // 明确告诉界面本轮有几成成功，界面据此决定说"完成"还是"有失败"
        uploaded: stats.uploaded,
        failed: stats.failed,
        // 未核实（既不算成功也不算失败）。界面可以不说，但绝不能把它当失败。
        unverified: stats.unverified || 0,
        // 必修 16：有几个监视目录这轮根本没看到（用户要知道"某个文件夹没在备份"）
        missingDirs: stats.missingDirs || 0,
        // 致命错误码（null = 本轮没有整轮级故障）。
        //
        // 为什么必须带出去（实测确证的严重缺陷）：
        //   致命失败轮与"真的没有文件要传"的成功轮，字段完全一样：
        //     浏览器连不上: uploaded=0 failed=0 total=0 errors=[{code:'BROWSER'}]
        //     真的没文件:   uploaded=0 failed=0 total=0 errors=[]
        //   界面按 failed===0 判定，就把前者显示成
        //     "完成 · 成功 0 个文件" + 绿色满格进度条
        //   而实际上一个字节都没传出去 —— 这是本项目历史上最严重的"界面说谎"
        //   类别里最隐蔽的一种：看起来完全正常，用户看到"完成"就不会再检查。
        //   带上 fatalCode 后界面才能说"本轮失败：连不上浏览器"。
        fatalCode: fatalCode || null,
        errorCodes: stats.errorCodes || [],
      });
      this.emit('run-complete', stats);
      this.log('info', `[${runId}] 完成: 上传 ${stats.uploaded}, 失败 ${stats.failed}, 跳过 ${stats.skipped}`
        + (stats.unverified ? `, 未核实 ${stats.unverified}` : '') + `, ${stats.durationMs}ms`);
    }
    return stats;
  }

  // 退避与熔断：
  //   连续失败 n 轮 → 下次间隔 min(intervalMs * 2^n, 30min)
  //   连续失败 >= 5 轮 → 熔断，停止自动重试；手动 runOnce 仍然可用
  //   成功一轮 → 计数与熔断一起清零
  // 手动触发（manual=true）不写入退避状态：用户已经亲自在等了，不该再被"退避"卡住。
  _applyBackoff(roundFailed, stats, manual) {
    if (manual) {
      this.log('info', '本轮为手动触发，不改变自动重试的退避计划');
      if (!this.running) this.nextRunAt = null;
      return;
    }
    if (roundFailed) {
      this.consecutiveFailures++;
      if (this.consecutiveFailures >= 5) {
        if (!this.circuitOpen) {
          this.circuitOpen = true;
          this.log('error', `连续失败 ${this.consecutiveFailures} 轮，已熔断：停止自动重试。`
            + '请检查浏览器/登录态后点"立即同步"手动恢复。', this._dominantCode(stats));
        }
        this.nextRunAt = null;         // 熔断期间不排期
        return;
      }
      const delay = Math.min(this.cfg.intervalMs * Math.pow(2, this.consecutiveFailures), 30 * 60 * 1000);
      this.nextRunAt = Date.now() + delay;
      this.log('warn', `连续失败 ${this.consecutiveFailures} 轮，下次自动同步延后 ${Math.round(delay / 1000)} 秒`
        + `（退避上限 30 分钟；连续 5 轮将熔断）`, this._dominantCode(stats));
    } else {
      if (this.consecutiveFailures > 0 || this.circuitOpen) {
        this.log('info', '已恢复：退避计数与熔断状态清零');
      }
      this.consecutiveFailures = 0;
      this.circuitOpen = false;
      this.nextRunAt = Date.now() + this.cfg.intervalMs;
    }
  }

  // 一轮里最该展示给用户的错误码：配额和鉴权优先于"网络/未知"
  _dominantCode(stats) {
    const codes = (stats && stats.errorCodes) || [];
    if (codes.indexOf('QUOTA') >= 0) return 'QUOTA';
    if (codes.indexOf('AUTH') >= 0) return 'AUTH';
    if (codes.indexOf('BROWSER') >= 0) return 'BROWSER';
    if (codes.indexOf('PAGE') >= 0) return 'PAGE';
    if (codes.indexOf('NETWORK') >= 0) return 'NETWORK';
    if (codes.indexOf('DISK') >= 0) return 'DISK';
    return 'UNKNOWN';
  }

  start() {
    if (this.running) return;
    this.running = true;
    this._abortRequested = false;
    this.consecutiveFailures = 0;
    this.circuitOpen = false;
    this.log('info', `启动常驻同步，间隔 ${this.cfg.intervalMs}ms`);
    const tick = async () => {
      if (!this.running) return;

      // 熔断期间不自动重试，但**必须继续排期**。
      // 原来这里直接 return —— 定时器链就此断裂，running 仍为 true，
      // 界面显示"备份中"，实际再也不会自动跑。这是最难发现的一类静默停摆。
      // 现在改为：熔断时也挂一个较长的轮询（5 分钟），
      // 一旦用户手动跑成功（runNow 清掉 circuitOpen），下一次轮询自动接管。
      if (this.circuitOpen) {
        this.log('warn', '已熔断：暂停自动重试，等待手动"立即同步"（每 5 分钟检查一次）', 'UNKNOWN');
        this.timer = setTimeout(tick, 5 * 60 * 1000);
        this.nextRunAt = Date.now() + 5 * 60 * 1000;
        return;
      }

      try { await this.runOnce(); } catch (e) { this.log('error', e.message, this._errCodeOf(e)); }
      if (!this.running) return;
      // 退避：用 _applyBackoff 算出的间隔，而不是固定 intervalMs
      const delay = this.nextRunAt
        ? Math.max(1000, this.nextRunAt - Date.now())
        : this.cfg.intervalMs;
      this.nextRunAt = Date.now() + delay;
      this.timer = setTimeout(tick, delay);
    };
    tick();
  }

  // 停止：既要停掉定时器，也要让正在跑的这一轮尽快收尾退出。
  // 只 clearTimeout 的话，当前文件会传完、后续循环继续，而界面已经显示"已停止" —— 那是撒谎。
  stop() {
    this.running = false;
    this._abortRequested = true;
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    this.nextRunAt = null;
    this.log('info', this.currentRun
      ? '已停止（本轮正在收尾，剩余文件不再上传）'
      : '已停止');
    this._flushState();           // P2：停止时把脏状态落盘
  }

  // 用户手动"立即同步"：允许在熔断态下跑一轮。
  // 熔断的定义是"停止自动重试"，不是"禁止用户操作"。
  //
  // 关键：**不能**在这里无条件清零熔断标志。
  // 原实现先 clear 再跑，而 _applyBackoff(manual=true) 又直接 return 不回升计数 ⇒
  // 只要用户点一次"立即同步"（哪怕这一轮照样失败），熔断就永久解除、
  // 之后自动同步按正常间隔一直重试。熔断语义被"点一下"废掉。
  // 现在改为：先跑，跑成功才解除；失败则恢复原来的熔断状态。
  async runNow() {
    const wasOpen = this.circuitOpen;
    const savedFailures = this.consecutiveFailures;
    this.circuitOpen = false;          // 临时放行，让 runOnce 不被自己挡住
    let stats = null;
    try {
      stats = await this.runOnce({ manual: true });
    } catch (e) {
      stats = null;
    }
    // 判据必须覆盖"整轮致命"：致命轮（连不上浏览器/没有远程文件夹）
    // uploaded=0 且 failed=0，旧判据 (failed>0 && uploaded===0) 对它恒为假
    // ⇒ 判成功 ⇒ 解熔断 ⇒ 记"手动同步成功" ⇒ 状态条转绿。这是阻断级的界面说谎（C-1）。
    // 致命码来源有二：runOnce 新导出的 stats.fatalCode（首选），
    // 以及 errors[] 里"没有 file 字段"的整轮级错误条目（兜底，防旧字段缺失）。
    const fatal = !stats ? null
      : (stats.fatalCode
        || ((stats.errors || []).filter(function (e) { return e && !e.file && e.code; })[0] || {}).code
        || null);
    const roundFailed = !stats || fatal !== null || (stats.failed > 0 && stats.uploaded === 0)
      || ((stats.unverified || 0) > 0 && stats.uploaded === 0)
      || (stats.missingDirs || 0) > 0;
    if (roundFailed) {
      // 手动这轮也失败：恢复熔断，不要把用户的"点一下"当成问题已解决
      // circuitOpen 只保持原状（wasOpen）：手动轮**绝不**自己开启熔断
      this.circuitOpen = wasOpen;
      this.consecutiveFailures = Math.max(savedFailures, this.consecutiveFailures);
      // 失败必须留痕，无论当前是否处于熔断态。
      // 原来只有 circuitOpen 为真才写日志 ⇒ "未熔断时手动失败"完全静默，
      // 用户只看到状态条没变绿，日志里一个字都没有。
      if (this.circuitOpen) {
        this.log('warn', '手动同步仍然失败，保持暂停状态（自动重试仍关闭）', fatal || 'UNKNOWN');
      } else {
        this.log('warn', '手动同步失败' + (fatal ? `（${fatal}）` : '') + '，自动重试计划未改变', fatal || 'UNKNOWN');
      }
    } else {
      this.consecutiveFailures = 0;
      this.circuitOpen = false;
      this.nextRunAt = null;
      this.log('info', '手动同步成功，已恢复自动重试', 'UNKNOWN');
    }
    return stats;
  }

  getStatus() {
    const versions = this.state.versions || {};
    let versionsTotal = 0;
    for (const k of Object.keys(versions)) {
      if (Array.isArray(versions[k])) versionsTotal += versions[k].length;
    }
    return {
      running: this.running,
      watchDirs: this.cfg.watchDirs,
      remoteFolderName: this.cfg.remoteFolderName,
      remoteFolderId: this.remoteFolderId,
      uploadedTotal: Object.keys(this.state.uploaded).length,
      failedTotal: Object.keys(this.state.failed).length,
      // 已上传但云端暂时核实不了的文件数（既不是成功也不是失败，界面必须单独说）
      unverifiedTotal: Object.keys(this.state.unverified || {}).length,
      // 必修 16：本轮/最近一轮有多少监视目录已经找不到（0 = 都还在）
      missingDirs: (this._missingDirs || []).length,
      lastRun: this.state.lastRun || null,
      failedFiles: Object.entries(this.state.failed).slice(0, 50).map(([f, v]) => ({
        file: f, count: v.count, error: v.lastError, code: v.code || 'UNKNOWN',
        at: v.at, nextRetryAt: v.nextRetryAt || null,
      })),
      // ---- 新增：界面需要的运行状态 ----
      versionsMode: this.cfg.versionsMode,       // keep（默认）| replace
      maxDeletePerRun: this.cfg.maxDeletePerRun,
      versions: versions,                        // { 本地路径: [{id,name,size,uploadedAt,action}] }
      versionsTotal: versionsTotal,
      consecutiveFailures: this.consecutiveFailures,
      circuitOpen: this.circuitOpen,             // true = 已暂停，等待手动恢复
      pausedReason: this.circuitOpen ? '已暂停，等待手动恢复' : null,
      nextRunAt: this.nextRunAt ? new Date(this.nextRunAt).toISOString() : null,
      lastRunAt: this.lastRunAt ? new Date(this.lastRunAt).toISOString() : null,
      logFile: this.cfg.logFile,
      runNo: this._runCounter,
    };
  }
}

module.exports = { SyncEngine };
