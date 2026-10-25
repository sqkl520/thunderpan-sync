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
    this.state = { uploaded: {}, failed: {}, versions: {} };
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
          lastRun: s.lastRun,
        };
        this.remoteFolderId = s.remoteFolderId || null;
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
      this.state = { uploaded: {}, failed: {}, versions: {} };
      this.remoteFolderId = null;
    }
  }

  // 原子写：先写临时文件 → fsync → rename 覆盖。
  // 直接 writeFileSync 覆盖目标文件时，断电/强杀会留下 0 字节或半截 JSON，
  // 下次启动解析失败 → 状态清空 → 把用户几个 GB 全部重传（实测过的场景）。
  _saveState() {
    const target = this.cfg.stateFile;
    const tmp = target + '.tmp';
    try {
      fs.mkdirSync(path.dirname(target), { recursive: true });
      const data = JSON.stringify({
        uploaded: this.state.uploaded,
        failed: this.state.failed,
        versions: this.state.versions,   // 云端版本审计记录（keep/replace 都要有）
        lastRun: this.state.lastRun,
        remoteFolderId: this.remoteFolderId,
      }, null, 2);

      const fd = fs.openSync(tmp, 'w');
      try {
        fs.writeSync(fd, data, 0, 'utf8');
        fs.fsyncSync(fd);          // 确保落盘，否则 rename 后内容仍可能在缓存里
      } finally {
        fs.closeSync(fd);
      }
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
      await page.goto(PAN + '/', { waitUntil: 'domcontentloaded' }).catch((e) => {
        this.log('warn', `第 ${i} 次导航到迅雷首页失败: ${e.message}`, 'PAGE');
      });
      // 等待时间递增：页面刚上传完文件时列表刷新较慢，固定 2.5s 会偶发点不到
      await page.waitForTimeout(2500 + (i - 1) * 1500);

      // 找不到链接时先滚动/等待再试，不要立刻判失败
      const clicked = await page.evaluate((n) => {
        const a = [...document.querySelectorAll('a[title]')].find(x => x.getAttribute('title') === n);
        if (a) { a.scrollIntoView(); a.click(); return true; }
        return false;
      }, name).catch((e) => {
        this.log('warn', `第 ${i} 次在页面里查找文件夹链接出错: ${e.message}`, 'PAGE');
        return false;
      });

      if (!clicked) {
        this.log('warn', `第 ${i} 次未找到 "${name}" 链接，重试`);
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
  scan() {
    const out = [];
    const now = Date.now();
    const MAX_DEPTH = 64;               // 防符号链接环/超深目录
    const settleMs = this.cfg.settleMs;

    // 未来时间戳容忍窗口：超过这个量就认为时钟/时间戳异常，按"已稳定"处理
    const FUTURE_TOLERANCE_MS = 60 * 1000;

    const blocked = this._blockedRoots();

    for (const dir of this.cfg.watchDirs) {
      let rootReal;
      try { rootReal = path.resolve(dir); } catch (e) {
        this.log('warn', `监视目录路径无法解析，按原样处理: ${dir}（${e.message}）`, 'DISK');
        rootReal = dir;
      }

      if (!fs.existsSync(dir)) { this.log('warn', `目录不存在: ${dir}`, 'DISK'); continue; }

      // 用户选到系统目录时明确拒绝（否则会尝试上传整个系统盘）
      const hit = blocked.find(b => rootReal.toLowerCase().startsWith(b.toLowerCase()));
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
            if (ent.name.startsWith('.') || ent.name === 'node_modules') continue;
            walk(full, depth + 1);
          } else if (ent.isFile()) {
            let st;
            try { st = fs.statSync(full); }
            catch (e) {
              out.push({ full, st: { size: 0, mtimeMs: now }, skip: `无法读取（${e.code || e.message}）` });
              continue;
            }

            if (st.size === 0) { out.push({ full, st, skip: '空文件（云盘不接受 0 字节）', skipCode: 'QUOTA' }); continue; }
            if (st.size > this.cfg.maxSizeMB * 1024 * 1024) {
              out.push({ full, st, skip: `超过 ${this.cfg.maxSizeMB} MB 上限`, skipCode: 'QUOTA' }); continue;
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
    return out;
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
    const verified = await this._verifyRemoteFile(name, item.st.size, beforeIds);
    if (verified) {
      return await this._onUploadVerified(item, name, beforeIds, ctx);
    }

    if (res.ok) {
      // 浏览器说 200，但云端查不到：可能是刚入库、列表还没刷新。再等一轮。
      await new Promise(r => setTimeout(r, 5000));
      const again = await this._verifyRemoteFile(name, item.st.size, beforeIds);
      if (again) {
        return await this._onUploadVerified(item, name, beforeIds, ctx, true);
      }
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
      const gh = await this._capHeader('GET', 25000);
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
    // 同时留下完整的文件对象数组：版本策略要按名字找旧副本，
    // 复用这份快照就不用为每个文件再拉一遍目录（少一半 API 调用，也少一次被限流的机会）。
    let files = null;
    try {
      if (!this.remoteFolderId) return ids;
      let token = '';
      for (let page = 0; page < 20; page++) {
        let url = API + '/drive/v1/files?parent_id=' + encodeURIComponent(this.remoteFolderId) + '&limit=200';
        if (token) url += '&page_token=' + encodeURIComponent(token);
        const r = await this._apiGet(url);   // 带 header 缓存，不再每页重截 captcha
        if (!r || r.status !== 200) {
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
      this._noteErrCode(this._errCodeOf(e));
      this.log('warn', `云端快照失败: ${e.message}`, this._errCodeOf(e));
    }
    // 快照不完整时置 null：宁可让版本策略退回去重新列举，也不要基于半份快照做删除判断
    this._lastSnapshot = (files && files.length === ids.size) ? files : null;
    return ids;
  }

  // 拿"本次上传之前云端已有的文件列表"：优先用 _snapshotIds 顺手留下的那份
  async _beforeFiles() {
    if (this._lastSnapshot) return this._lastSnapshot;
    const out = [];
    try {
      if (!this.remoteFolderId) return out;
      let token = '';
      for (let page = 0; page < 20; page++) {
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
  async _verifyRemoteFile(name, size, beforeIds) {
    for (let attempt = 0; attempt < 6; attempt++) {
      if (attempt > 0) await new Promise(r => setTimeout(r, 3000));
      try {
        if (!this.remoteFolderId) return false;

        let token = '';
        for (let page = 0; page < 20; page++) {
          let url = API + '/drive/v1/files?parent_id=' + encodeURIComponent(this.remoteFolderId) + '&limit=200';
          if (token) url += '&page_token=' + encodeURIComponent(token);
          const r = await this._apiGet(url);   // 带 header 缓存
          if (!r || r.status !== 200) break;
          const j = await r.json();
          const files = j.files || [];

          for (const f of files) {
            if (Number(f.size) !== Number(size)) continue;
            // 名称可能是原名，也可能被云盘改名成 "x(1).ext"（同名已存在时）
            const isSameName = f.name === name;
            const isRenamed = this._looksLikeRenamedCopy(f.name, name);
            if (!isSameName && !isRenamed) continue;

            // 核心判据：id 必须是本次新出现的
            if (beforeIds && beforeIds.has(f.id)) {
              // 这是上传前就存在的旧文件，不能算本次成功
              continue;
            }
            return true;
          }

          token = j.next_page_token || '';
          if (!token) break;
        }
      } catch (e) {
        if (attempt === 5) {
          const code = this._errCodeOf(e);
          this._noteErrCode(code);
          this.log('warn', `云端核查失败: ${e.message}`, code);
        }
      }
    }
    return false;
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
      this.emit('progress', {
        phase: p.phase || 'uploading',
        index: Number(p.index) || 0,
        total: Number(p.total) || 0,
        name: p.name || '',
        size: Number(p.size) || 0,
        bytesDone: Number(p.bytesDone) || 0,
        bytesTotal: Number(p.bytesTotal) || 0,
        elapsedMs: Number(p.elapsedMs) || 0,
        etaMs: (p.etaMs === null || p.etaMs === undefined) ? null : Number(p.etaMs),
      });
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
    const stats = { runId, runNo, scanned: 0, uploaded: 0, failed: 0, skipped: 0, errors: [], errorCodes: [] };
    let fatalCode = null;
    try {
      this._emitProgress({
        phase: 'scanning', index: 0, total: 0, name: '', size: 0,
        bytesDone: 0, bytesTotal: 0, elapsedMs: 0, etaMs: null,
      });

      await this.connect();
      if (!this.remoteFolderId) await this.ensureRemoteFolder();
      if (!this.remoteFolderId) throw this._err('没有可用的远程文件夹', 'PAGE');

      const items = this.scan();
      stats.scanned = items.length;
      const ready = items.filter(i => !i.skip);
      stats.skipped = items.length - ready.length;

      // 单文件退避：失败 5 次以上的文件不再每轮重试，改为每 10 轮才试一次。
      // 记录的是 nextRetryAt（轮次号），因为轮间隔会随退避变化，用时间戳算不准。
      const due = [];
      let deferred = 0;
      for (const it of ready) {
        const f = this.state.failed[it.full];
        if (f && f.count >= 5 && typeof f.nextRetryAt === 'number' && runNo < f.nextRetryAt) {
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
        this.log('info', '无需同步');
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
          if (!r.ok) {
            this.log('warn', `${nm} 第 1 次失败（${r.error}${r.code ? ' / ' + r.code : ''}），2 秒后重试`, r.code);
            await new Promise(r2 => setTimeout(r2, 2000));
            r = await this.uploadOne(it, ctx);
          }

          if (r.ok) stats.uploaded++;
          else {
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
      this.currentRun = null;
      this.state.lastRun = stats;
      this.lastRunAt = Date.now();
      this._logDay = null;          // 允许下一次 log() 重新判定日期（跨天）
      this._saveState();

      // ---- 退避 / 熔断判定 ----
      // "失败的一轮" = 有致命错误（连不上浏览器/没有远程文件夹），或本轮有文件失败。
      // 只是"没有文件要传"的成功轮不参与退避，否则用户加文件后要等 30 分钟。
      const roundFailed = !!fatalCode || stats.failed > 0;
      this._logDay = null;
      this._applyBackoff(roundFailed, stats, manual);
      stats.backoffMs = this.nextRunAt ? Math.max(0, this.nextRunAt - Date.now()) : null;
      stats.consecutiveFailures = this.consecutiveFailures;
      stats.circuitOpen = this.circuitOpen;

      this._emitProgress({
        phase: 'done', index: stats.uploaded, total: stats.uploaded + stats.failed,
        name: '', size: 0,
        // 收尾事件也要给出本轮的字节基数，否则界面进度条停在最后一条 uploading
        // 的百分比（或显示 0/0），看起来像"卡住没完成"。
        bytesDone: stats.bytesTotal || 0,
        bytesTotal: stats.bytesTotal || 0,
        elapsedMs: stats.durationMs, etaMs: 0,
      });
      this.emit('run-complete', stats);
      this.log('info', `[${runId}] 完成: 上传 ${stats.uploaded}, 失败 ${stats.failed}, 跳过 ${stats.skipped}, ${stats.durationMs}ms`);
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
  }

  // 用户手动"立即同步"：解除熔断并清掉退避计数，然后跑一轮。
  // 熔断的定义是"停止自动重试"，不是"禁止用户操作"。
  async runNow() {
    this.circuitOpen = false;
    this.consecutiveFailures = 0;
    this.nextRunAt = null;
    return this.runOnce({ manual: true });
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
